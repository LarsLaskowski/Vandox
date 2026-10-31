package logship

import (
	"context"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/logstages"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/syslogline"
)

// noProblem marks a file that was not looked at in this run: its readability did not change.
const noProblem = "\x00"

// openGap is a gap that the next readable entry, or the end of the reading of a journal ID, closes.
type openGap struct {
	pending bool
	from    time.Time
}

// run is one pass over the sources: the fork it works on, the records it returns and its budgets.
type run struct {
	l     *Logs
	run   scheduler.Run
	start time.Time
	begin time.Time
	st    *state

	// havePos and newest are the newest journal position of the committed state when the run started.
	havePos bool
	newest  time.Time

	records []model.Record
	froms   []time.Time
	// spare is stages ready for a source that has none; it moves into the state when a group opens.
	spare    *logstages.Stages
	bytes    int
	examined int
	stopped  bool
	failed   bool
	gap      openGap
	lastBad  time.Time

	jproblem    string
	journalRead bool
	fproblems   []string
	zone        *time.Location
	zoneChecked bool
	zoneBad     bool
}

// newRun returns a run on the fork st.
func newRun(l *Logs, sr scheduler.Run, st *state) *run {
	newest, have := l.committed.newestJournal()
	r := &run{
		l:         l,
		run:       sr,
		start:     sr.Start,
		begin:     l.src.Now(),
		st:        st,
		havePos:   have,
		newest:    newest,
		fproblems: make([]string, len(l.cfg.Files)),
	}
	for i := range r.fproblems {
		r.fproblems[i] = noProblem
	}
	return r
}

// emit adds a record; from is the start of the gap that replaces the record if the record check refuses it.
func (r *run) emit(rec model.Record, from time.Time) {
	r.records = append(r.records, rec)
	r.froms = append(r.froms, from)
	if l, ok := rec.Data.(*model.LogLine); ok {
		r.bytes += textBytes(l)
	}
}

// emitGap adds a gap over [from, to).
func (r *run) emitGap(from, to time.Time) {
	r.emit(newGap(from, to, r.start), time.Time{})
}

// tick counts an examined entry or line and reports whether the run must stop reading: it has produced 1,000 records or 4 MiB of text,
// its context ended, or it has read for a second. The clock is looked at after every shipped record and after every 1,000 entries or
// lines examined, so that a flood of filtered lines delays wanted ones without ever stopping them.
func (r *run) tick(ctx context.Context, shipped bool) bool {
	r.examined++
	switch {
	case len(r.records) >= maxRecords || r.bytes >= maxBytes || ctx.Err() != nil:
		r.stopped = true
	case shipped || r.examined%timeCheckEvery == 0:
		r.stopped = r.timeUp()
	}
	return r.stopped
}

// timeUp reports whether the run has read for its budget of time.
func (r *run) timeUp() bool {
	return r.l.src.Now().Sub(r.begin) >= readBudget
}

// openGapFrom starts a gap at from unless one is pending.
func (r *run) openGapFrom(from time.Time) {
	if from.IsZero() {
		from = r.start
	}
	if !r.gap.pending {
		r.gap = openGap{pending: true, from: from}
	}
}

// closeGapAt ends the pending gap at to.
func (r *run) closeGapAt(to time.Time) {
	if r.gap.pending {
		r.emitGap(r.gap.from, to)
		r.gap.pending = false
	}
}

// readSources reads the journal and the files; the source that comes first changes from run to run, so that a backlog in one never
// keeps the others waiting for good.
func (r *run) readSources(ctx context.Context) {
	n := 1 + len(r.l.cfg.Files)
	first := r.l.turn % n
	r.l.turn++
	for k := 0; k < n && !r.stopped; k++ {
		if i := (first + k) % n; i == 0 {
			r.readJournal(ctx)
		} else {
			r.readFile(ctx, i-1)
		}
	}
}

// zoneOf loads the zone of the server once per run; nil means it is unknown.
func (r *run) zoneOf() *time.Location {
	if !r.zoneChecked {
		r.zoneChecked = true
		loc, err := syslogline.LoadZone(r.l.src.Etc)
		if err != nil {
			r.zoneBad = true
		} else {
			r.zone = loc
		}
	}
	return r.zone
}
