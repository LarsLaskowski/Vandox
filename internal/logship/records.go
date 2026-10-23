package logship

import (
	"math"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/journal"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	// maxMessageBytes, maxShortBytes and maxPidDigits are the bounds of the import's mapping.
	maxMessageBytes = model.MaxTextBytes
	maxShortBytes   = model.MaxShortTextBytes
	maxPidDigits    = 10
	// maxRealtimeUS is the largest __REALTIME_TIMESTAMP in µs the import accepts.
	maxRealtimeUS = math.MaxInt64 / 1000
	// maxPriorityValue is the least severe syslog priority.
	maxPriorityValue = 7

	// Names of the journal fields the mapping reads.
	fieldMessage    = "MESSAGE"
	fieldPriority   = "PRIORITY"
	fieldHostname   = "_HOSTNAME"
	fieldIdentifier = "SYSLOG_IDENTIFIER"
	fieldComm       = "_COMM"
	fieldPid        = "_PID"
	fieldSyslogPid  = "SYSLOG_PID"

	// sourceJournal and sourceSyslog are the source of the records, and logJournal is the log of the journal's.
	sourceJournal = "journal"
	sourceSyslog  = "syslog"
	logJournal    = "journal"
)

// wantFields are the journal items that are read in full.
var wantFields = []string{fieldMessage, fieldPriority, fieldHostname, fieldIdentifier, fieldComm, fieldPid, fieldSyslogPid}

// checkRecord is the record check checkRecords calls; the tests of the seam swap it and never run in parallel.
var checkRecord = wire.CheckRecord

// newGap returns a gap record of the collector for [from, to) detected at at; To is From + 1 µs when to is not after from.
func newGap(from, to, at time.Time) model.Record {
	from, to = from.UTC(), to.UTC()
	if !to.After(from) {
		to = from.Add(time.Microsecond)
	}
	return model.Record{
		Meta: model.Meta{Source: Name, CapturedAt: at.UTC()},
		Data: &model.Gap{From: from, To: to, Cause: model.GapUnknown, Collector: Name},
	}
}

// checkRecords replaces every record that the record check refuses with a gap from gapFrom(i) to the capture time of the record, so that
// every record returned passes the check as the scheduler stamps it. A gap that is refused becomes [at - 1 µs, at).
func checkRecords(records []model.Record, at time.Time, gapFrom func(i int) time.Time) []model.Record {
	for i := range records {
		if accepted(&records[i], at) {
			continue
		}
		if _, isGap := records[i].Data.(*model.Gap); !isGap {
			replacement := newGap(gapFrom(i), records[i].CapturedAt, at)
			if accepted(&replacement, at) {
				records[i] = replacement
				continue
			}
		}
		records[i] = newGap(at.Add(-time.Microsecond), at, at)
	}
	return records
}

// accepted reports whether the record check takes r as the scheduler would stamp it.
func accepted(r *model.Record, at time.Time) bool {
	c := *r
	c.Origin, c.Seq = model.OriginAgent, math.MaxInt64
	if c.CapturedAt.IsZero() {
		c.CapturedAt = at
	}
	_, err := checkRecord(&c)
	return err == nil
}

// textBytes returns the UTF-8 bytes of the text a run budget counts: log, host, program and message of a log line.
func textBytes(l *model.LogLine) int {
	return len(l.Log) + len(l.Host) + len(l.Program) + len(l.Message)
}

// cleaned makes b valid UTF-8 and cuts it to limit bytes at a character boundary; cut tells that text is shorter than b.
func cleaned(b []byte, limit int) (text string, cut bool) {
	return procstats.CleanText(string(b), limit)
}

// parsePID reads a process ID: one to ten digits of at most 2,147,483,647, else 0.
func parsePID(b []byte) int32 {
	if len(b) == 0 || len(b) > maxPidDigits {
		return 0
	}
	var v int64
	for _, c := range b {
		if c < '0' || c > '9' {
			return 0
		}
		v = v*10 + int64(c-'0')
	}
	if v > math.MaxInt32 {
		return 0
	}
	return int32(v) // #nosec G115 -- bounded by the check above
}

// parsePriority reads a priority: one digit from 0 to 7.
func parsePriority(b []byte) *uint8 {
	if len(b) != 1 || b[0] < '0' || b[0] > '0'+maxPriorityValue {
		return nil
	}
	p := b[0] - '0'
	return &p
}

// firstField returns the value of the first field of e with the name, and whether it was cut.
func firstField(e *journal.Entry, name string) (value []byte, cut, found bool) {
	for _, f := range e.Fields {
		if f.Name == name && !f.Compressed {
			return f.Value, f.Cut, true
		}
	}
	return nil, false, false
}

// journalRecord maps a journal entry to a log line record by the import's journal mapping; false means the entry has no MESSAGE or no
// usable time.
func journalRecord(e *journal.Entry) (model.Record, bool) {
	if e.RealtimeUS == 0 || e.RealtimeUS > maxRealtimeUS {
		return model.Record{}, false
	}
	message, messageCut, found := firstField(e, fieldMessage)
	if !found {
		return model.Record{}, false
	}
	line := &model.LogLine{Log: logJournal}
	var cut, hostCut, programCut, textCut bool
	line.Message, textCut = cleaned(message, maxMessageBytes)
	host, hostRaw, _ := firstField(e, fieldHostname)
	line.Host, hostCut = cleaned(host, maxShortBytes)
	program, programRaw, ok := firstField(e, fieldIdentifier)
	if !ok || len(program) == 0 {
		program, programRaw, _ = firstField(e, fieldComm)
	}
	line.Program, programCut = cleaned(program, maxShortBytes)
	cut = messageCut || textCut || hostCut || hostRaw || programCut || programRaw
	line.Truncated = cut
	pid, _, _ := firstField(e, fieldPid)
	line.PID = parsePID(pid)
	if line.PID == 0 {
		syslogPid, _, _ := firstField(e, fieldSyslogPid)
		line.PID = parsePID(syslogPid)
	}
	priority, _, _ := firstField(e, fieldPriority)
	line.Priority = parsePriority(priority)
	return model.Record{
		Meta: model.Meta{Source: sourceJournal, CapturedAt: e.Realtime.UTC()},
		Data: line,
	}, true
}

// lineOf returns the log line payload of a record made by this package.
func lineOf(rec model.Record) *model.LogLine {
	l, _ := rec.Data.(*model.LogLine)
	return l
}
