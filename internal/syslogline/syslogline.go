// Package syslogline parses the lines of syslog files the way the import does: a hand-written, linear port of its syslog line rules.
package syslogline

import "time"

// Line is one parsed syslog line.
type Line struct {
	Time     time.Time // UTC; zero when the line has no header time
	Host     string
	Program  string
	PID      int32
	Priority *uint8
	Message  string
	Header   bool // the line has a syslog header
}

// Parse parses line; clock supplies the year and the daylight-saving rules of a time without them, now is the time of reading.
func Parse(line []byte, clock *Clock, now time.Time) Line {
	return Line{}
}
