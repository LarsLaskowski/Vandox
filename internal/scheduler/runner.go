package scheduler

import (
	"context"
	"errors"
	"fmt"
	"math"
	"runtime/debug"
	"sync/atomic"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	// maxPanicValueBytes is the most of a panic value that PanicError.Error and the log show.
	maxPanicValueBytes = 1024
	// maxStackBytes is the most of a stack that the log shows.
	maxStackBytes = 4096
)

// PanicError is the error of a run whose collector panicked.
type PanicError struct {
	Value any    // the value passed to panic
	Stack []byte // the stack of the panicking goroutine
}

// Error returns "scheduler: collector panicked: " and fmt.Sprint(Value), cut to its first 1,024 bytes.
func (e *PanicError) Error() string {
	return "scheduler: collector panicked: " + cutBytes(fmt.Sprint(e.Value), maxPanicValueBytes)
}

// cutBytes returns the first limit bytes of s.
func cutBytes(s string, limit int) string {
	if len(s) > limit {
		return s[:limit]
	}
	return s
}

// outcome is how one run ended.
type outcome int

const (
	outcomeDone     outcome = iota // the collector returned in time
	outcomeTimedOut                // the deadline passed
	outcomeStopped                 // the scheduler's context ended
)

// result is what one worker returns.
type result struct {
	records []model.Record
	err     error
}

// runner runs one job; its state is used by its own goroutine only, except wake and deadline.
type runner struct {
	s        *Scheduler
	job      Job
	name     string
	adaptive bool
	wake     chan struct{}
	deadline atomic.Int64 // liveness deadline in unix nanoseconds, 0 before the first cycle

	pending     chan result // the result channel of an abandoned run, nil when none is out
	skipped     int         // cycles skipped while pending was out
	failedRuns  int         // failed runs in a row
	sinkFailing bool
}

func newRunner(s *Scheduler, j Job) *runner {
	return &runner{
		s:        s,
		job:      j,
		name:     j.Collector.Name(),
		adaptive: j.Schedule.PressureInterval > 0,
		wake:     make(chan struct{}, 1),
	}
}

// wakeUp tells a runner with a pressure interval that the mode changed.
func (r *runner) wakeUp() {
	if !r.adaptive {
		return
	}
	select {
	case r.wake <- struct{}{}:
	default:
	}
}

// interval returns the interval for the mode high.
func (r *runner) interval(high bool) time.Duration {
	if high && r.adaptive {
		return r.job.Schedule.PressureInterval
	}
	return r.job.Schedule.Interval
}

// setDeadline sets the liveness deadline for a cycle due at due.
func (r *runner) setDeadline(due time.Time) {
	r.deadline.Store(due.Add(r.job.Schedule.Timeout + LivenessSlack).UnixNano())
}

// loop runs the cycles of the job until ctx is done.
func (r *runner) loop(ctx context.Context) {
	d := time.Now()
	for ctx.Err() == nil {
		seen := r.s.mode.Load()
		d = r.startCycle(ctx, d, seen)
		r.cycle(ctx, d, seen)
		var ok bool
		if d, ok = r.waitNext(ctx, d, seen); !ok {
			return
		}
	}
}

// startCycle checks whether a whole slot has passed since the due time d and records a gap for it; it returns the due
// time the cycle counts from.
func (r *runner) startCycle(ctx context.Context, d time.Time, seen *pressureMode) time.Time {
	now := time.Now()
	if !now.Before(d.Add(r.interval(seen.high))) {
		r.s.log.Warn("collector late", "collector", r.name, "missed", now.Sub(d))
		r.put(ctx, r.gap(model.GapUnknown, d, now, now))
		d = now
	}
	r.setDeadline(d)
	return d
}

// waitNext waits for the next due time and returns it; false when ctx ended.
func (r *runner) waitNext(ctx context.Context, d time.Time, seen *pressureMode) (time.Time, bool) {
	for {
		n := r.nextDue(d, seen)
		r.setDeadline(n)
		wait := time.Until(n)
		if wait <= 0 {
			return n, ctx.Err() == nil
		}
		timer := time.NewTimer(wait)
		select {
		case <-timer.C:
			return n, true
		case <-r.wake:
			timer.Stop()
		case <-ctx.Done():
			timer.Stop()
			return n, false
		}
	}
}

// nextDue returns the next due time of a cycle that was due at d under the mode seen.
func (r *runner) nextDue(d time.Time, seen *pressureMode) time.Time {
	cur := r.s.mode.Load()
	if r.adaptive && cur.high != seen.high {
		n := d.Add(r.interval(cur.high))
		if cur.since.After(n) {
			return cur.since
		}
		return n
	}
	return d.Add(r.interval(seen.high))
}

// cycle runs one cycle that is due at d.
func (r *runner) cycle(ctx context.Context, d time.Time, seen *pressureMode) {
	iv := r.interval(seen.high)
	if r.pending != nil {
		select {
		case <-r.pending:
			r.s.log.Info("collector returned after its deadline", "collector", r.name, "skipped", r.skipped)
			r.pending, r.skipped = nil, 0
		default:
			r.skipped++
			r.put(ctx, r.gap(model.GapCollectorTimeout, d, d.Add(iv), time.Now()))
			return
		}
	}
	run := Run{Start: time.Now().UTC(), Pressure: seen.high}
	res, out := r.collect(ctx, run)
	switch out {
	case outcomeTimedOut:
		r.s.log.Warn("collector timed out", "collector", r.name, "timeout", r.job.Schedule.Timeout)
		r.put(ctx, r.gap(model.GapCollectorTimeout, d, d.Add(iv), time.Now()))
	case outcomeDone:
		r.finish(ctx, d, iv, run, res)
	}
}

// collect runs the collector in a worker goroutine and waits for its result, the deadline or the end of ctx.
func (r *runner) collect(ctx context.Context, run Run) (result, outcome) {
	ch := make(chan result, 1)
	wctx, cancel := context.WithTimeout(ctx, r.job.Schedule.Timeout)
	go func() {
		defer cancel()
		ch <- r.invoke(wctx, run)
	}()
	timer := time.NewTimer(r.job.Schedule.Timeout)
	defer timer.Stop()

	select {
	case res := <-ch:
		if wctx.Err() == context.DeadlineExceeded {
			return result{}, outcomeTimedOut
		}
		return res, outcomeDone
	case <-timer.C:
		select {
		case <-ch:
		default:
			r.pending = ch
		}
		return result{}, outcomeTimedOut
	case <-ctx.Done():
		return result{}, outcomeStopped
	}
}

// invoke calls the collector and turns a panic into a *PanicError.
func (r *runner) invoke(ctx context.Context, run Run) (res result) {
	defer func() {
		if v := recover(); v != nil {
			res = result{err: &PanicError{Value: v, Stack: debug.Stack()}}
		}
	}()
	records, err := r.job.Collector.Collect(ctx, run)
	return result{records: records, err: err}
}

// finish handles the result of a run in time.
func (r *runner) finish(ctx context.Context, d time.Time, iv time.Duration, run Run, res result) {
	if res.err != nil {
		r.logFailure(res.err)
		r.failedRuns++
		r.put(ctx, r.gap(model.GapUnknown, d, d.Add(iv), time.Now()))
		return
	}
	if r.failedRuns > 0 {
		r.s.log.Info("collector recovered", "collector", r.name, "failed_runs", r.failedRuns)
		r.failedRuns = 0
	}
	records, refused := r.stamp(res.records, run)
	if refused != nil {
		r.s.log.Warn("record refused", "collector", r.name, "err", refused)
		records = append(records, r.gap(model.GapUnknown, d, d.Add(iv), time.Now()))
	}
	r.put(ctx, records...)
}

// logFailure logs the error of a failed run: every panic, and an error only at the first failure after a success.
func (r *runner) logFailure(err error) {
	var pe *PanicError
	if errors.As(err, &pe) {
		r.s.log.Error("collector panicked", "collector", r.name,
			"panic", cutBytes(fmt.Sprint(pe.Value), maxPanicValueBytes), "stack", cutBytes(string(pe.Stack), maxStackBytes))
		return
	}
	if r.failedRuns == 0 {
		r.s.log.Warn("collector failed", "collector", r.name, "err", err)
	}
}

// stamp gives records the metadata of the agent and drops the ones the wire format would refuse; refused is the first
// error.
func (r *runner) stamp(records []model.Record, run Run) (kept []model.Record, refused error) {
	kept = make([]model.Record, 0, len(records)+1)
	for _, rec := range records {
		rec.Origin, rec.Seq = model.OriginAgent, 0
		if rec.CapturedAt.IsZero() {
			rec.CapturedAt = run.Start
		}
		check := rec
		check.Seq = math.MaxInt64
		if _, err := wire.CheckRecord(&check); err != nil {
			if refused == nil {
				refused = err
			}
			continue
		}
		kept = append(kept, rec)
	}
	return kept, refused
}

// gap returns a gap record of the scheduler for this job, captured at at.
func (r *runner) gap(cause model.GapCause, from, to, at time.Time) model.Record {
	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: Source, CapturedAt: at.UTC()},
		Data: &model.Gap{From: from.UTC(), To: to.UTC(), Cause: cause, Collector: r.name},
	}
}

// put hands records to the sink and logs its failures at the first and its recovery.
func (r *runner) put(ctx context.Context, records ...model.Record) {
	if len(records) == 0 {
		return
	}
	err := r.s.sink.Put(ctx, records)
	switch {
	case err != nil && ctx.Err() == nil && !r.sinkFailing:
		r.sinkFailing = true
		r.s.log.Error("sink failed", "collector", r.name, "err", err)
	case err == nil && r.sinkFailing:
		r.sinkFailing = false
		r.s.log.Info("sink recovered", "collector", r.name)
	}
}
