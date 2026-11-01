// Package logstages classifies and groups live log lines the way the backend's import stages do: mail lines get a
// priority and an event, MariaDB lines are joined into messages, and kernel reports are grouped.
package logstages

import (
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// MaxSpan is the longest time a group may stay open, measured from its anchor line.
const MaxSpan = 60 * time.Second

// Mark is the saved position that reads a line again: a journal sequence number before it, or a file offset in the
// current or the renamed part, together with the time of the line before it.
type Mark struct {
	// Renamed reports that At is an offset in the renamed (rotated) part of the file.
	Renamed bool
	// At is the journal sequence number or the file offset.
	At uint64
	// Before is the time of the line before the marked one.
	Before time.Time
}

// Line is a mapped, filtered line handed to the stages.
type Line struct {
	// Rec is the log record of the line.
	Rec model.Record
	// Mark is the position that reads the line again.
	Mark Mark
	// RunStart is the start of the run that produced the line.
	RunStart time.Time
	// MailOnly marks a line that gets only the mail classification.
	MailOnly bool
}

// Output is a record to emit and the start of the gap it replaces.
type Output struct {
	// Rec is the record to emit.
	Rec model.Record
	// From is the start of the gap the record replaces.
	From time.Time
}

// Stages combines the mail, MariaDB and kernel stages in the order the import runs them. A stage allocates its buffers
// only while a group is open.
type Stages struct {
	maria        *mariaGroup
	kernel       *kernelGroup
	recoveryOpen bool
	arrivals     uint64
}

// New returns empty stages.
func New() *Stages {
	return &Stages{}
}

// Add hands a line to the stages and appends the records that became ready to out.
func (s *Stages) Add(l Line, out []Output) []Output {
	payload, ok := l.Rec.Data.(*model.LogLine)
	if ok {
		ClassifyMail(payload)
	}

	if !ok || l.MailOnly {
		return append(out, Output{Rec: l.Rec, From: l.Mark.Before})
	}

	s.arrivals++

	var buf [2]item

	ready := s.addMaria(item{rec: l.Rec, mark: l.Mark, runStart: l.RunStart, arrival: s.arrivals}, buf[:0])

	return s.forward(ready, out)
}

// Expire closes the groups whose anchor lies more than MaxSpan before t and appends their records to out. The anchor is
// the earlier of the capture time of the first line of the group and the start of the run that read it.
func (s *Stages) Expire(t time.Time, out []Output) []Output {
	if g := s.maria; g != nil && t.Sub(g.first.anchor()) > MaxSpan {
		var buf [1]item

		out = s.forward(s.flushMaria(buf[:0]), out)
	}

	if g := s.kernel; g != nil && t.Sub(g.first.anchor()) > MaxSpan {
		out = s.flushKernel(out)
	}

	return out
}

// Close closes every open group, the MariaDB entry first, and appends the records to out.
func (s *Stages) Close(out []Output) []Output {
	var buf [1]item

	out = s.forward(s.flushMaria(buf[:0]), out)

	return s.flushKernel(out)
}

// Open reports whether a group is open.
func (s *Stages) Open() bool {
	return s.maria != nil || s.kernel != nil
}

// HeldFrom returns the mark of the oldest held line, and false when no line is held. The oldest line is the first line of
// the group that was opened first.
func (s *Stages) HeldFrom() (Mark, bool) {
	switch {
	case s.maria != nil && s.kernel != nil:
		if s.kernel.first.arrival < s.maria.first.arrival {
			return s.kernel.first.mark, true
		}

		return s.maria.first.mark, true
	case s.maria != nil:
		return s.maria.first.mark, true
	case s.kernel != nil:
		return s.kernel.first.mark, true
	}

	return Mark{}, false
}

// Clone returns a deep copy of the stages.
func (s *Stages) Clone() *Stages {
	c := *s

	if s.maria != nil {
		c.maria = s.maria.clone()
	}

	if s.kernel != nil {
		c.kernel = s.kernel.clone()
	}

	return &c
}

// forward passes the items of the MariaDB stage through the kernel stage and appends the results to out.
func (s *Stages) forward(ready []item, out []Output) []Output {
	for i := range ready {
		out = s.addKernel(ready[i], out)
	}

	return out
}

// Recovery reports whether a MariaDB crash recovery is open: the next InnoDB "started" line ends it.
func (s *Stages) Recovery() bool {
	return s.recoveryOpen
}

// SetRecovery sets the open crash recovery of new stages to what an earlier stages of the same source ended with.
func (s *Stages) SetRecovery(open bool) {
	s.recoveryOpen = open
}
