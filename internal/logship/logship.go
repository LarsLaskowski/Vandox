// Package logship ships the lines of the systemd journal and of configured syslog files as log records (collector "logs").
//
// A run works on a fork of the committed reading state and returns the records of what it read; the fork is committed when the scheduler
// reports the records kept (Kept), so a dropped run is read again. The committed positions are handed to the agent's state file.
package logship

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"log/slog"
	"os"
	"regexp"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/journal"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
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
	// lineKeep is the most bytes of a line that are kept: the import's line limit and one byte for a \r before the line feed.
	lineKeep = maxMessageBytes + 1
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
	// fromSpan tells that the run started from the span that is given up; when it is dropped too, the span stays what it was.
	fromSpan bool
}

// Logs is the log shipping collector.
type Logs struct {
	src    Sources
	cfg    Config
	store  *agentstate.Store
	log    *slog.Logger
	filter *filter
	dec    *countingDecoder
	entry  journal.Entry
	buf    []byte
	// formats are the formats of the configured files, by index.
	formats []fileFormat

	inited    bool
	committed *state
	pending   *pendingRun
	lastBack  *state // the fork of the last dropped run whose result came back
	drops     int
	gaveUp    bool
	saved     *agentstate.LogPositions
	turn      int
	fail      failure

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
	formats := make([]fileFormat, len(cfg.Files))
	for i, f := range cfg.Files {
		format, known := formatOf(f.Format)
		if known {
			formats[i] = format
			continue
		}
		return nil, fmt.Errorf("logship: log file %d has an unknown format", i)
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
		dec:     &countingDecoder{d: dec},
		fileBad: make([]string, len(cfg.Files)),
		formats: formats,
	}, nil
}

// Name returns the collector's name.
func (l *Logs) Name() string {
	return Name
}

// countingDecoder counts the calls of the decoder, so that the run can look at the clock after an entry whose reading decoded.
type countingDecoder struct {
	d     *zstdfield.Decoder
	calls int
}

// Decompress decodes a frame through the decoder.
func (c *countingDecoder) Decompress(dst, src []byte, limit int) ([]byte, error) {
	c.calls++
	return c.d.Decompress(dst, src, limit)
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
	givenUp, base := l.begin(run)
	st := base.clone()
	if base != l.committed {
		st.last = base.last // the span that is given up keeps where the dropped run got
	}
	r := newRun(l, run, st)
	for _, g := range givenUp {
		r.emit(g, run.Start)
	}
	r.readSources(ctx)
	records := checkRecords(r.records, run.Start, func(i int) time.Time { return r.froms[i] })
	if !r.failed {
		l.fail = failure{}
	}
	l.logOutcome(r)
	l.pending = &pendingRun{start: run.Start, fork: r.st, fromSpan: base != l.committed}
	return records, nil
}

// begin resolves the previous run, which no call of Kept reported on when it was lost, and gives up a span that was dropped too
// often. It returns the gap of that and the state the run starts from: the committed state, or after a give-up the state the last
// dropped run reached, which stays the start of every run, with its gap, until a run is kept and commits its fork.
func (l *Logs) begin(run scheduler.Run) ([]model.Record, *state) {
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
		return nil, l.committed
	}
	return l.giveUp(run)
}

// giveUp gives up the span the dropped runs read: when the result of one of them came back, the run starts from the state it reached
// and one gap covers the span, in every run until one is kept; when none did, one gap from the committed time to the start of the run is
// made once per committed state.
func (l *Logs) giveUp(run scheduler.Run) ([]model.Record, *state) {
	from := l.committed.committedTime()
	if from.IsZero() {
		from = run.Start
	}
	if back := l.lastBack; back != nil {
		to := back.last
		if to.IsZero() {
			to = run.Start
		}
		return []model.Record{newGap(from, to, run.Start)}, back
	}
	if l.gaveUp {
		return nil, l.committed
	}
	l.gaveUp = true
	return []model.Record{newGap(from, run.Start, run.Start)}, l.committed
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
	if p.fromSpan {
		p.fork.release(l.committed, l.lastBack)
		return
	}
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
