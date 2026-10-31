package logship

import (
	"cmp"
	"context"
	"slices"
	"strings"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/logstages"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

const (
	// userFilePrefix starts the name of the journal files of regular users, whose lines get the mail classification only.
	userFilePrefix = "user-"
	// systemJournal is the name of the online journal file of the system.
	systemJournal = "system.journal"
)

// isUserFile tells whether a journal file belongs to a regular user: user-<UID>.journal and its archives.
func isUserFile(name string) bool {
	return strings.HasPrefix(name, userFilePrefix)
}

// journalKey is the stages key of a journal sequence number ID.
func journalKey(id [16]byte) stageKey {
	return stageKey{id: id}
}

// fileKey is the stages key of the configured file i.
func fileKey(i int) stageKey {
	return stageKey{file: true, index: i}
}

// stage hands a mapped, filtered line of the source key to its stages and emits the records that became ready. captured is the time
// of the line; a line dated after the start of the run counts as captured at the start, so that a forged date never closes a group
// early.
func (r *run) stage(key stageKey, line logstages.Line, captured time.Time) {
	if line.MailOnly {
		if l := lineOf(line.Rec); l != nil {
			logstages.ClassifyMail(l)
		}
		r.emit(line.Rec, line.Mark.Before)
		return
	}
	st := r.st.stages[key]
	if st == nil {
		if r.spare == nil {
			r.spare = logstages.New()
		}
		st = r.spare
		st.SetRecovery(r.st.recovery[key])
	}
	var buf [2]logstages.Output
	out := st.Expire(minTime(captured, r.start), buf[:0])
	out = st.Add(line, out)
	r.emitOutputs(out)
	r.keepStages(key, st)
}

// keepStages stores the stages of key while a group is open and drops them when the last group closed.
func (r *run) keepStages(key stageKey, st *logstages.Stages) {
	r.keepRecovery(key, st)
	switch {
	case st.Open():
		if r.st.stages[key] == nil {
			r.st.stages[key] = st
			r.spare = nil
		}
	default:
		delete(r.st.stages, key)
	}
}

// keepRecovery keeps an open crash recovery of a source beyond its stages, and forgets it when it ended.
func (r *run) keepRecovery(key stageKey, st *logstages.Stages) {
	if st.Recovery() {
		r.st.recovery[key] = true
	} else {
		delete(r.st.recovery, key)
	}
}

// emitOutputs emits the records of the stages, each with the start of the gap that replaces it.
func (r *run) emitOutputs(out []logstages.Output) {
	for i := range out {
		r.emit(out[i].Rec, out[i].From)
	}
}

// closeStages closes the groups of the source key and emits their records.
func (r *run) closeStages(key stageKey) {
	st := r.st.stages[key]
	if st == nil {
		return
	}
	delete(r.st.stages, key)
	r.emitOutputs(st.Close(nil))
	r.keepRecovery(key, st)
}

// expireStages closes the groups of the source key that the clock has outlasted: the source was read to its end in this run.
func (r *run) expireStages(key stageKey) {
	st := r.st.stages[key]
	if st == nil {
		return
	}
	r.emitOutputs(st.Expire(r.start, nil))
	r.keepStages(key, st)
}

// closeJournalStages closes the groups of every journal ID.
func (r *run) closeJournalStages() {
	for _, key := range r.stageKeys() {
		if !key.file {
			r.closeStages(key)
		}
	}
}

// closeAllStages closes the groups of every source.
func (r *run) closeAllStages() {
	for _, key := range r.stageKeys() {
		r.closeStages(key)
	}
}

// stageKeys returns the keys of the open stages in a fixed order: journal IDs, then files.
func (r *run) stageKeys() []stageKey {
	keys := make([]stageKey, 0, len(r.st.stages))
	for key := range r.st.stages {
		keys = append(keys, key)
	}
	slices.SortFunc(keys, func(a, b stageKey) int {
		if a.file != b.file {
			if a.file {
				return 1
			}
			return -1
		}
		if c := slices.Compare(a.id[:], b.id[:]); c != 0 {
			return c
		}
		return cmp.Compare(a.index, b.index)
	})
	return keys
}

// Flush closes every open group of a fork of the committed state and returns the records. It commits nothing itself: only
// Kept(true) for the returned run adopts the fork and saves the shipped positions.
func (l *Logs) Flush(_ context.Context, run scheduler.Run) []model.Record {
	givenUp, base := l.begin(run)
	st := base.clone()
	if base != l.committed {
		st.last = base.last
	}
	r := newRun(l, run, st)
	r.closeAllStages()
	for _, g := range givenUp {
		r.emit(g, run.Start)
	}
	records := checkRecords(r.records, run.Start, func(i int) time.Time { return r.froms[i] })
	l.pending = &pendingRun{start: run.Start, fork: r.st, fromSpan: base != l.committed}
	return records
}
