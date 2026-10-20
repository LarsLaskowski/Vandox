// Package scheduler runs the agent's collectors, each in its own goroutine under its own deadline (record 0029).
package scheduler

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"sync"
	"sync/atomic"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// Source is the source of the records the scheduler writes itself (gaps).
const Source = "agent"

// LivenessSlack is how long after its run's deadline a runner may still be busy before Healthy reports false.
const LivenessSlack = 30 * time.Second

// Run describes one run of a collector.
type Run struct {
	Start        time.Time // when the run started, in UTC
	Pressure     bool      // whether memory pressure mode was on when the run started
	PreviousLost bool      // the previous due sample's result was not kept: timeout, skip, error, panic, refused record or sink error; a late start does not set it
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

// pressureMode is the memory pressure mode and the moment it last really changed.
type pressureMode struct {
	high  bool
	since time.Time
}

// Scheduler runs jobs.
type Scheduler struct {
	runners []*runner
	sink    Putter
	log     *slog.Logger
	mode    atomic.Pointer[pressureMode]
	modeMu  sync.Mutex // serializes the writers of mode
}

// New checks jobs and returns a scheduler that puts records into sink and logs to log.
func New(jobs []Job, sink Putter, log *slog.Logger) (*Scheduler, error) {
	if sink == nil || log == nil {
		return nil, errors.New("scheduler: a sink and a logger are required")
	}
	s := &Scheduler{sink: sink, log: log}
	s.mode.Store(&pressureMode{})
	seen := make(map[string]struct{}, len(jobs))
	for i, j := range jobs {
		if err := checkJob(i, j, seen); err != nil {
			return nil, err
		}
		s.runners = append(s.runners, newRunner(s, j))
	}
	return s, nil
}

// checkJob checks job number i; seen holds the names of the jobs before it and receives its own.
func checkJob(i int, j Job, seen map[string]struct{}) error {
	if j.Collector == nil {
		return fmt.Errorf("scheduler: job %d: collector is nil", i)
	}
	name := j.Collector.Name()
	if !model.ValidName(name) {
		return fmt.Errorf("scheduler: job %d: collector name must match the wire format's name pattern", i)
	}
	if _, dup := seen[name]; dup {
		return fmt.Errorf("scheduler: job %d (%s): duplicate collector name", i, name)
	}
	seen[name] = struct{}{}
	return checkSchedule(i, name, j.Schedule)
}

// checkSchedule checks the schedule of job number i, named name.
func checkSchedule(i int, name string, sc Schedule) error {
	switch {
	case sc.Interval <= 0:
		return fmt.Errorf("scheduler: job %d (%s): interval must be positive", i, name)
	case sc.Timeout <= 0 || sc.Timeout > sc.Interval:
		return fmt.Errorf("scheduler: job %d (%s): timeout must be positive and at most the interval", i, name)
	case sc.PressureInterval != 0 && (sc.PressureInterval < sc.Timeout || sc.PressureInterval > sc.Interval):
		return fmt.Errorf("scheduler: job %d (%s): pressure interval must be at least the timeout and at most the interval", i, name)
	}
	return nil
}

// Run runs every job until ctx is done and returns when every runner has stopped. Abandoned runs are not waited for.
func (s *Scheduler) Run(ctx context.Context) {
	var wg sync.WaitGroup
	for _, r := range s.runners {
		wg.Go(func() { r.loop(ctx) })
	}
	wg.Wait()
}

// SetPressure switches memory pressure mode on or off. A runner with a pressure interval then counts its next due time
// from its last due time with the new interval, but not earlier than the moment of the switch. Setting the mode it
// already has changes nothing.
func (s *Scheduler) SetPressure(high bool) {
	s.modeMu.Lock()
	changed := s.mode.Load().high != high
	if changed {
		s.mode.Store(&pressureMode{high: high, since: time.Now()})
	}
	s.modeMu.Unlock()

	if changed {
		for _, r := range s.runners {
			r.wakeUp()
		}
	}
}

// Healthy reports whether every started runner is within its liveness deadline at now.
func (s *Scheduler) Healthy(now time.Time) bool {
	for _, r := range s.runners {
		if d := r.deadline.Load(); d != 0 && d < now.UnixNano() {
			return false
		}
	}
	return true
}
