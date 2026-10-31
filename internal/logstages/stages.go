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

// Stages combines the mail, MariaDB and kernel stages.
type Stages struct{}

// New returns empty stages.
func New() *Stages {
	return &Stages{}
}

// Add hands a line to the stages and appends the records that became ready to out.
func (s *Stages) Add(l Line, out []Output) []Output {
	return out
}

// Expire closes the groups whose anchor lies more than MaxSpan before t and appends their records to out.
func (s *Stages) Expire(t time.Time, out []Output) []Output {
	return out
}

// Close closes every open group and appends the records to out.
func (s *Stages) Close(out []Output) []Output {
	return out
}

// Open reports whether a group is open.
func (s *Stages) Open() bool {
	return false
}

// HeldFrom returns the mark of the oldest held line, and false when no line is held.
func (s *Stages) HeldFrom() (Mark, bool) {
	return Mark{}, false
}

// Clone returns a deep copy of the stages.
func (s *Stages) Clone() *Stages {
	return &Stages{}
}
