// Package logship ships the lines of the systemd journal and of configured syslog files as log records (collector "logs").
//
// A run works on a fork of the committed reading state and returns the records of what it read; the fork is committed when the scheduler
// reports the records kept (Kept), so a dropped run is read again. The committed positions are handed to the agent's state file.
package logship

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"os"
	"regexp"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/journal"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/syslogline"
	"github.com/LarsLaskowski/Vandox/internal/zstdfield"
)

// Name is the collector name.
const Name = "logs"

const (
	// maxRecords is the most records one run returns.
	maxRecords = 1000
	// maxBytes is the text (log, host, program and message of the log lines) after which a run ends; the record that crosses it is the
	// last.
	maxBytes = 4 << 20
	// readBudget is how long one run reads, half the timeout of the job.
	readBudget = time.Second
	// timeCheckEvery is the number of entries or lines examined between two looks at the clock when none of them was shipped.
	timeCheckEvery = 1000
	// giveUpAfter is the number of runs in a row that were dropped before the span they read is given up.
	giveUpAfter = 4
	// maxCompressed is the most compressed items decoded per journal entry.
	maxCompressed = 4
	// readChunk is the size of the buffer files are read through.
	readChunk = 64 << 10
	// lineKeep is the most bytes of a line that are kept: the message limit and room for the header of the line.
	lineKeep = maxMessageBytes + 512
	// refusedGapEvery is the spacing of the further gaps while the online journal file is refused.
	refusedGapEvery = 600 * time.Second
	// failuresToGiveUp is the number of runs in a row that stop at the same damage of an online journal file before it is given up.
	failuresToGiveUp = 3
	// maxTracked bounds the journal files whose refusal is remembered.
	maxTracked = 1024

	keyErr = "err"

	msgJournalNotReadable = "journal not readable"
	msgJournalReadable    = "journal readable again"
	msgFileNotReadable    = "log file not readable"
	msgFileReadable       = "log file readable again"
	msgZoneNotReadable    = "time zone not readable"
	msgZoneReadable       = "time zone readable again"
	reasonZone            = "localtime not readable"
)

// Sources are the file trees and functions the collector reads through.
type Sources struct {
	Etc     fs.FS                               // machine-id and localtime; nil: os.DirFS("/etc")
	Journal []fs.FS                             // persistent, then volatile journal roots; nil: /var/log/journal, /run/log/journal
	Open    func(path string) (*os.File, error) // nil: OpenNoLinks
	Now     func() time.Time                    // nil: time.Now
}

// FileSpec is a configured log file.
type FileSpec struct{ Path, Format string }

// Rule drops the lines that match all of its set conditions.
type Rule struct {
	Log      string         // "" any, "journal" or a configured path
	Program  string         // "" any
	Priority *uint8         // nil any
	Message  *regexp.Regexp // nil any
}

// Config configures the collector.
type Config struct {
	Journal     bool
	MinPriority uint8
	Files       []FileSpec
	Drop        []Rule
}

// failure counts the runs in a row that stopped at the same damage of an online journal file.
type failure struct {
	name string
	id   [16]byte
	seq  uint64
	n    int
}

// pendingRun is the fork of a run whose result the scheduler has not reported yet.
type pendingRun struct {
	start time.Time
	fork  *state
}

// Logs is the log shipping collector.
type Logs struct {
	src    Sources
	cfg    Config
	store  *agentstate.Store
	log    *slog.Logger
	filter *filter
	dec    *zstdfield.Decoder
	entry  journal.Entry
	buf    []byte
	clocks []*syslogline.Clock

	inited    bool
	committed *state
	pending   *pendingRun
	lastBack  *state // the fork of the last dropped run whose result came back
	drops     int
	gaveUp    bool
	saved     *agentstate.LogPositions
	turn      int
	fail      failure
	refused   map[string]time.Time

	journalBad bool
	fileBad    []string
	zoneBad    bool
}

// New returns the collector.
func New(src Sources, cfg Config, store *agentstate.Store, log *slog.Logger) (*Logs, error) {
	if len(cfg.Files) > agentstate.MaxLogFiles {
		return nil, errors.New("logship: too many log files")
	}
	if src.Etc == nil {
		src.Etc = os.DirFS("/etc")
	}
	if src.Journal == nil {
		src.Journal = []fs.FS{os.DirFS("/var/log/journal"), os.DirFS("/run/log/journal")}
	}
	if src.Open == nil {
		src.Open = OpenNoLinks
	}
	if src.Now == nil {
		src.Now = time.Now
	}
	dec, err := zstdfield.New()
	if err != nil {
		return nil, err
	}
	return &Logs{
		src:     src,
		cfg:     cfg,
		store:   store,
		log:     log,
		filter:  newFilter(cfg),
		dec:     dec,
		clocks:  make([]*syslogline.Clock, len(cfg.Files)),
		fileBad: make([]string, len(cfg.Files)),
		refused: map[string]time.Time{},
	}, nil
}

// Name returns the collector's name.
func (l *Logs) Name() string {
	return Name
}

// readOptions returns the limits of reading journal entries.
func (l *Logs) readOptions() *journal.ReadOptions {
	return &journal.ReadOptions{
		MaxPayload:         maxMessageBytes,
		Decompress:         l.dec,
		MaxCompressed:      maxCompressed,
		MaxCompressedBytes: zstdfield.MaxFrameBytes,
	}
}

// Collect reads the new lines of the journal and the configured files. It never returns an error: what cannot be read is logged once
// and, where something is lost, a gap.
func (l *Logs) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	givenUp := l.begin(run)
	r := newRun(ctx, l, run, l.committed.clone())
	for _, g := range givenUp {
		r.emit(g, run.Start)
	}
	r.readSources()
	records := checkRecords(r.records, run.Start, func(i int) time.Time { return r.froms[i] })
	if !r.failed {
		l.fail = failure{}
	}
	l.logOutcome(r)
	l.pending = &pendingRun{start: run.Start, fork: r.st}
	return records, nil
}

// begin resolves the previous run, which no call of Kept reported on when it was lost, and gives up a span that was dropped too
// often; it returns the gap of that.
func (l *Logs) begin(run scheduler.Run) []model.Record {
	if !l.inited {
		l.inited = true
		var logs *agentstate.LogPositions
		if l.store != nil {
			logs = l.store.State().Logs
		}
		l.saved = logs
		l.committed = restore(logs, l.cfg.Files)
	}
	if p := l.pending; p != nil {
		l.pending = nil
		if run.PreviousLost {
			l.drops++
		}
		p.fork.release(l.committed, l.lastBack)
	}
	if l.drops < giveUpAfter {
		return nil
	}
	return l.giveUp(run)
}

// giveUp gives up the span the dropped runs read: when the result of one of them came back, its positions are committed and one gap
// covers the span; when none did, one gap from the committed time to the start of the run is made once per committed state.
func (l *Logs) giveUp(run scheduler.Run) []model.Record {
	from := l.committed.committedTime()
	if from.IsZero() {
		from = run.Start
	}
	if back := l.lastBack; back != nil {
		to := back.last
		if to.IsZero() {
			to = run.Start
		}
		l.adopt(back)
		return []model.Record{newGap(from, to, run.Start)}
	}
	if l.gaveUp {
		return nil
	}
	l.gaveUp = true
	return []model.Record{newGap(from, run.Start, run.Start)}
}

// Kept commits the reading state of run when its records were kept, and counts the run as dropped otherwise.
func (l *Logs) Kept(run scheduler.Run, kept bool) {
	p := l.pending
	if p == nil || !p.start.Equal(run.Start) {
		return
	}
	l.pending = nil
	if kept {
		l.adopt(p.fork)
		return
	}
	l.drops++
	l.lastBack.release(l.committed, p.fork)
	l.lastBack = p.fork
}

// adopt makes the fork the committed state and hands its positions to the state file.
func (l *Logs) adopt(fork *state) {
	old, back := l.committed, l.lastBack
	l.committed, l.lastBack = fork, nil
	l.drops, l.gaveUp = 0, false
	old.release(fork)
	back.release(fork)
	l.save()
}

// save hands the committed positions to the state file when they changed.
func (l *Logs) save() {
	if l.store == nil {
		return
	}
	logs := positionsOf(l.committed, l.cfg.Files)
	if sameLogs(logs, l.saved) {
		return
	}
	l.saved = logs
	l.store.Update(true, func(s *agentstate.State) { s.Logs = logs })
}

// countFailure counts the runs in a row that stop at the same position of a file and reports whether the file is given up now.
func (l *Logs) countFailure(name string, id [16]byte, seq uint64) bool {
	if l.fail.name == name && l.fail.id == id && l.fail.seq == seq {
		l.fail.n++
	} else {
		l.fail = failure{name: name, id: id, seq: seq, n: 1}
	}
	if l.fail.n < failuresToGiveUp {
		return false
	}
	l.fail = failure{}
	return true
}

// logOutcome logs the changes between readable and unreadable sources, once each. It uses fixed texts only: never a path, a field
// value or a message.
func (l *Logs) logOutcome(r *run) {
	switch {
	case r.jproblem != "" && !l.journalBad:
		l.log.Warn(msgJournalNotReadable, keyErr, r.jproblem)
		l.journalBad = true
	case r.jproblem == "" && l.journalBad && r.journalRead:
		l.log.Info(msgJournalReadable)
		l.journalBad = false
	}
	for i, reason := range r.fproblems {
		switch {
		case reason == noProblem:
		case reason != "" && reason != l.fileBad[i]:
			l.log.Warn(msgFileNotReadable, "file", i, keyErr, reason)
			l.fileBad[i] = reason
		case reason == "" && l.fileBad[i] != "":
			l.log.Info(msgFileReadable, "file", i)
			l.fileBad[i] = ""
		}
	}
	switch {
	case r.zoneChecked && r.zoneBad && !l.zoneBad:
		l.log.Warn(msgZoneNotReadable, keyErr, reasonZone)
		l.zoneBad = true
	case r.zoneChecked && !r.zoneBad && l.zoneBad:
		l.log.Info(msgZoneReadable)
		l.zoneBad = false
	}
}
