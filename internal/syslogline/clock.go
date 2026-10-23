package syslogline

import "time"

// Clock turns the time of a traditional syslog header (no year, no zone) into an instant. One Clock serves one file and keeps the
// previous line's instant.
type Clock struct {
	loc  *time.Location
	prev time.Time
}

// NewClock returns a clock for lines written in loc.
func NewClock(loc *time.Location) *Clock {
	return &Clock{loc: loc}
}
