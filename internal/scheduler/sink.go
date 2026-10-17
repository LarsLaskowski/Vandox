package scheduler

import (
	"context"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// Sink takes the records the scheduler produced: origin agent, sequence number 0 (the sink assigns sequence numbers).
// Put is called with the context given to Scheduler.Run, not one bounded by a run's timeout, and must return when it is
// done. The scheduler logs an error of Put and records no gap for the refused records: a sink that refuses records owns
// recording their loss as a gap.
type Sink interface {
	Put(ctx context.Context, records []model.Record) error
}

// DiscardSink drops every record. The agent uses it until the spool exists (#38).
type DiscardSink struct{}

// Put drops records and returns nil.
func (DiscardSink) Put(ctx context.Context, records []model.Record) error {
	return errNotImplemented
}
