package model

import (
	"errors"
	"time"
)

// GapCause names why data is missing.
type GapCause string

// Gap causes.
const (
	GapAgentNotRunning  GapCause = "agent_not_running"
	GapSpoolDropped     GapCause = "spool_dropped"
	GapCollectorTimeout GapCause = "collector_timeout"
	GapSequenceMissing  GapCause = "sequence_missing"
	GapNoData           GapCause = "no_data"
	GapUnknown          GapCause = "unknown"
)

// Gap records a period without data.
type Gap struct {
	From      time.Time `json:"from"`
	To        time.Time `json:"to"`
	Cause     GapCause  `json:"cause"`
	Collector string    `json:"collector,omitempty"`
	FirstSeq  uint64    `json:"first_seq,omitempty"`
	LastSeq   uint64    `json:"last_seq,omitempty"`
}

// Kind returns KindGap.
func (g *Gap) Kind() Kind { return "" }

// Validate checks the payload.
func (g *Gap) Validate() error { return errors.New("not implemented") }
