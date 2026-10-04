package model

import (
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
func (g *Gap) Kind() Kind { return KindGap }

// Validate checks the payload.
func (g *Gap) Validate() error {
	if g == nil {
		return nilReceiver()
	}
	if err := checkTime("from", g.From); err != nil {
		return err
	}
	if err := checkTime("to", g.To); err != nil {
		return err
	}
	if !g.To.After(g.From) {
		return invalid("to", "must be after from")
	}
	if err := checkOneOf("cause", g.Cause, GapAgentNotRunning, GapSpoolDropped, GapCollectorTimeout,
		GapSequenceMissing, GapNoData, GapUnknown); err != nil {
		return err
	}
	if err := checkOptionalName("collector", g.Collector); err != nil {
		return err
	}
	if g.Cause == GapCollectorTimeout && g.Collector == "" {
		return invalid("collector", "required for cause collector_timeout")
	}
	if (g.FirstSeq == 0) != (g.LastSeq == 0) || g.FirstSeq > g.LastSeq {
		return invalid("last_seq", "first_seq and last_seq must both be set, first_seq not after last_seq")
	}
	if g.FirstSeq == 0 && (g.Cause == GapSpoolDropped || g.Cause == GapSequenceMissing) {
		return invalid("first_seq", "required for this cause")
	}
	return nil
}
