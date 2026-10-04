package model

import (
	"errors"
)

// LogLine is one line of a log.
type LogLine struct {
	Log       string `json:"log"`
	Program   string `json:"program,omitempty"`
	PID       int32  `json:"pid,omitempty"`
	Priority  *uint8 `json:"priority,omitempty"`
	Message   string `json:"message"`
	Truncated bool   `json:"truncated,omitempty"`
}

// Kind returns KindLogLine.
func (l *LogLine) Kind() Kind { return "" }

// Validate checks the payload.
func (l *LogLine) Validate() error { return errors.New("not implemented") }
