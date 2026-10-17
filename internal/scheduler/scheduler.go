// Package scheduler runs the agent's collectors, each in its own goroutine under its own deadline (record 0029).
package scheduler

import (
	"context"
	"errors"
	"log/slog"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// Source is the source of the records the scheduler writes itself (gaps).
const Source = "agent"

// LivenessSlack is how long after its run's deadline a runner may still be busy before Healthy reports false.
const LivenessSlack = 30 * time.Second

// Run describes one run of a collector.
type Run struct {
	Start    time.Time // when the run started, in UTC
	Pressure bool      // whether memory pressure mode was on when the run started
}

// Collector takes one sample per run.
type Collector interface {
	// Name returns the collector's name, a name of the wire format; it names the collector in gaps and logs.
	Name() string
	// Collect takes one sample. It honors ctx where its source allows; the scheduler stops waiting at the deadline
	// either way and drops a late result.
	Collect(ctx context.Context, run Run) ([]model.Record, error)
}

// Schedule is how often and how long a collector runs.
type Schedule struct {
	Interval         time.Duration // between the starts of two runs
	Timeout          time.Duration // deadline of one run; at most Interval, and at most PressureInterval when set
	PressureInterval time.Duration // interval under memory pressure; 0 keeps Interval
}

// Job is a collector with its schedule.
type Job struct {
	Collector Collector
	Schedule  Schedule
}

// Scheduler runs jobs.
type Scheduler struct {
	jobs []Job
	sink Sink
	log  *slog.Logger
}

// New checks jobs and returns a scheduler that puts records into sink and logs to log.
func New(jobs []Job, sink Sink, log *slog.Logger) (*Scheduler, error) {
	return &Scheduler{jobs: jobs, sink: sink, log: log}, nil
}

// Run runs every job until ctx is done and returns when every runner has stopped. Abandoned runs are not waited for.
func (s *Scheduler) Run(ctx context.Context) {
}

// SetPressure switches memory pressure mode on or off. A runner with a pressure interval then counts its next due time
// from its last due time with the new interval, but not earlier than the moment of the switch. Setting the mode it
// already has changes nothing.
func (s *Scheduler) SetPressure(high bool) {
}

// Healthy reports whether every started runner is within its liveness deadline at now.
func (s *Scheduler) Healthy(now time.Time) bool {
	return false
}

var errNotImplemented = errors.New("not implemented")
