package scheduler_test

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// anyTime marks an expected captured_at that a test does not check.
const anyTime = time.Duration(-1)

// scriptedCollector is a scheduler.Collector whose runs are scripted by fn; it records every run it was started with.
// Without fn it returns one metric record per run.
type scriptedCollector struct {
	name string
	fn   func(ctx context.Context, n int, run scheduler.Run) ([]model.Record, error)
	mu   sync.Mutex
	runs []scheduler.Run
}

func newScripted(name string, fn func(ctx context.Context, n int, run scheduler.Run) ([]model.Record, error)) *scriptedCollector {
	return &scriptedCollector{name: name, fn: fn}
}

func (c *scriptedCollector) Name() string { return c.name }

func (c *scriptedCollector) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	c.mu.Lock()
	n := len(c.runs)
	c.runs = append(c.runs, run)
	c.mu.Unlock()

	if c.fn == nil {
		return []model.Record{metricRecord(c.name, float64(n))}, nil
	}
	return c.fn(ctx, n, run)
}

// started returns the runs the collector was started with.
func (c *scriptedCollector) started() []scheduler.Run {
	c.mu.Lock()
	defer c.mu.Unlock()
	return slices.Clone(c.runs)
}

// hangingCollector blocks every Collect call until it is released, ignoring its context. It is released in t.Cleanup.
type hangingCollector struct {
	name    string
	release chan struct{}
	once    sync.Once
	calls   atomic.Int32
}

func newHanging(t *testing.T, name string) *hangingCollector {
	t.Helper()

	h := &hangingCollector{name: name, release: make(chan struct{})}
	t.Cleanup(h.Release)
	return h
}

func (h *hangingCollector) Name() string { return h.name }

func (h *hangingCollector) Collect(context.Context, scheduler.Run) ([]model.Record, error) {
	h.calls.Add(1)
	<-h.release
	return nil, nil
}

// Release lets every blocked and later Collect call return.
func (h *hangingCollector) Release() { h.once.Do(func() { close(h.release) }) }

// put is one call of Put.
type put struct {
	at      time.Time
	records []model.Record
}

// gapSeen is a gap record the sink received.
type gapSeen struct {
	at   time.Time
	meta model.Meta
	gap  model.Gap
}

// recordingSink is a scheduler.Putter that records every Put; hook, when set, runs after the call was recorded and its
// result is the result of Put.
type recordingSink struct {
	mu    sync.Mutex
	puts  []put
	hook  func(ctx context.Context, n int) error
	calls atomic.Int32
}

func (s *recordingSink) Put(ctx context.Context, records []model.Record) error {
	s.mu.Lock()
	n := len(s.puts)
	s.puts = append(s.puts, put{at: time.Now(), records: slices.Clone(records)})
	hook := s.hook
	s.mu.Unlock()
	s.calls.Add(1)

	if hook != nil {
		return hook(ctx, n)
	}
	return nil
}

func (s *recordingSink) all() []put {
	s.mu.Lock()
	defer s.mu.Unlock()
	return slices.Clone(s.puts)
}

// records returns every record received so far.
func (s *recordingSink) records() []model.Record {
	var out []model.Record
	for _, p := range s.all() {
		out = append(out, p.records...)
	}
	return out
}

// fromSource returns the puts that hold a record of source, with only those records.
func (s *recordingSink) fromSource(source string) []put {
	var out []put
	for _, p := range s.all() {
		var rs []model.Record
		for _, r := range p.records {
			if r.Source == source {
				rs = append(rs, r)
			}
		}
		if len(rs) > 0 {
			out = append(out, put{at: p.at, records: rs})
		}
	}
	return out
}

// gaps returns the gap records received, of one collector or, for "", of all.
func (s *recordingSink) gaps(collector string) []gapSeen {
	var out []gapSeen
	for _, p := range s.all() {
		for _, r := range p.records {
			g, ok := r.Data.(*model.Gap)
			if ok && (collector == "" || g.Collector == collector) {
				out = append(out, gapSeen{at: p.at, meta: r.Meta, gap: *g})
			}
		}
	}
	return out
}

// logEntry is one log line the scheduler wrote.
type logEntry struct {
	at    time.Time
	level slog.Level
	msg   string
	attrs map[string]string
}

// logStore collects log entries.
type logStore struct {
	mu      sync.Mutex
	entries []logEntry
}

// find returns the entries of level with message msg.
func (l *logStore) find(level slog.Level, msg string) []logEntry {
	l.mu.Lock()
	defer l.mu.Unlock()

	var out []logEntry
	for _, e := range l.entries {
		if e.level == level && e.msg == msg {
			out = append(out, e)
		}
	}
	return out
}

// recordingHandler is a slog.Handler that stores entries (groups are flattened).
type recordingHandler struct {
	store *logStore
	attrs []slog.Attr
}

func (h *recordingHandler) Enabled(context.Context, slog.Level) bool { return true }

func (h *recordingHandler) Handle(_ context.Context, r slog.Record) error {
	e := logEntry{at: time.Now(), level: r.Level, msg: r.Message, attrs: map[string]string{}}
	for _, a := range h.attrs {
		e.attrs[a.Key] = a.Value.Resolve().String()
	}
	r.Attrs(func(a slog.Attr) bool {
		e.attrs[a.Key] = a.Value.Resolve().String()
		return true
	})
	h.store.mu.Lock()
	h.store.entries = append(h.store.entries, e)
	h.store.mu.Unlock()
	return nil
}

func (h *recordingHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &recordingHandler{store: h.store, attrs: append(slices.Clone(h.attrs), attrs...)}
}

func (h *recordingHandler) WithGroup(string) slog.Handler { return h }

// metricRecord returns a valid metric record of source without origin and time (the scheduler stamps both).
func metricRecord(source string, value float64) model.Record {
	return model.Record{
		Meta: model.Meta{Source: source},
		Data: &model.MetricPoint{Name: "sample", Value: value},
	}
}

func job(c scheduler.Collector, interval, timeout time.Duration) scheduler.Job {
	return scheduler.Job{Collector: c, Schedule: scheduler.Schedule{Interval: interval, Timeout: timeout}}
}

// harness runs a scheduler in a synctest bubble.
type harness struct {
	t      *testing.T
	sched  *scheduler.Scheduler
	sink   *recordingSink
	logs   *logStore
	cancel context.CancelFunc
	done   chan struct{}
	base   time.Time
}

// start creates a scheduler for jobs and calls Run in a goroutine; the scheduler is stopped in t.Cleanup.
func start(t *testing.T, jobs []scheduler.Job, sink *recordingSink) *harness {
	t.Helper()

	return startIn(t, jobs, sink, false)
}

// startIn is start with the pressure mode set before Run begins, so the first run never counts its due time with the other interval.
func startIn(t *testing.T, jobs []scheduler.Job, sink *recordingSink, pressure bool) *harness {
	t.Helper()

	logs := &logStore{}
	s, err := scheduler.New(jobs, sink, slog.New(&recordingHandler{store: logs}))
	if err != nil {
		t.Fatalf("New() error = %v, want nil", err)
	}
	if pressure {
		s.SetPressure(true)
	}
	ctx, cancel := context.WithCancel(context.Background())
	h := &harness{t: t, sched: s, sink: sink, logs: logs, cancel: cancel, done: make(chan struct{}), base: time.Now()}
	go func() {
		defer close(h.done)
		s.Run(ctx)
	}()
	t.Cleanup(h.stop)
	return h
}

// stop cancels the context given to Run and waits for Run to return.
func (h *harness) stop() {
	h.cancel()
	<-h.done
}

// sleepTo sleeps until offset after the start of Run and waits until every other goroutine of the bubble is blocked.
func (h *harness) sleepTo(offset time.Duration) {
	time.Sleep(offset - time.Since(h.base))
	synctest.Wait()
}

// offsets returns the start times of the runs as offsets from the start of Run.
func (h *harness) offsets(runs []scheduler.Run) []time.Duration {
	out := make([]time.Duration, len(runs))
	for i, r := range runs {
		out[i] = r.Start.Sub(h.base)
	}
	return out
}

func seconds(n ...int) []time.Duration {
	out := make([]time.Duration, len(n))
	for i, v := range n {
		out[i] = time.Duration(v) * time.Second
	}
	return out
}

func pressures(runs []scheduler.Run) []bool {
	out := make([]bool, len(runs))
	for i, r := range runs {
		out[i] = r.Pressure
	}
	return out
}

// wantGap is the expected content of one gap record received by the sink; times are offsets from the start of Run.
type wantGap struct {
	at       time.Duration // when the sink received it
	captured time.Duration // captured_at, or anyTime
	from, to time.Duration
	cause    model.GapCause
}

func (h *harness) describe(gaps []gapSeen) string {
	parts := make([]string, len(gaps))
	for i, g := range gaps {
		parts[i] = fmt.Sprintf("{at %v captured %v from %v to %v cause %s collector %q origin %s source %q seq %d}",
			g.at.Sub(h.base), g.meta.CapturedAt.Sub(h.base), g.gap.From.Sub(h.base), g.gap.To.Sub(h.base),
			g.gap.Cause, g.gap.Collector, g.meta.Origin, g.meta.Source, g.meta.Seq)
	}
	return "[" + strings.Join(parts, " ") + "]"
}

// requireGaps checks the gaps of collector the sink received, in order.
func (h *harness) requireGaps(collector string, want []wantGap) {
	h.t.Helper()

	got := h.sink.gaps(collector)
	if len(got) != len(want) {
		h.t.Fatalf("gaps of %q = %s, want %d gaps %+v", collector, h.describe(got), len(want), want)
	}
	for i, w := range want {
		g := got[i]
		ok := g.at.Sub(h.base) == w.at &&
			g.gap.From.Sub(h.base) == w.from && g.gap.To.Sub(h.base) == w.to &&
			g.gap.Cause == w.cause &&
			g.meta.Origin == model.OriginAgent && g.meta.Source == scheduler.Source && g.meta.Seq == 0 &&
			(w.captured == anyTime || g.meta.CapturedAt.Sub(h.base) == w.captured)
		if !ok || g.gap.Collector == "" {
			h.t.Errorf("gap %d of %q = %s, want %+v (origin agent, source agent, seq 0)",
				i, collector, h.describe([]gapSeen{g}), w)
		}
	}
}

// requireLog checks that the scheduler wrote exactly one entry of level with message msg, at offset at (anyTime: any
// time) and with the attributes in want.
func (h *harness) requireLog(level slog.Level, msg string, at time.Duration, want map[string]string) {
	h.t.Helper()

	entries := h.logs.find(level, msg)
	if len(entries) != 1 {
		h.t.Fatalf("%s lines %q = %d, want exactly 1", level, msg, len(entries))
	}
	e := entries[0]
	for k, v := range want {
		if e.attrs[k] != v {
			h.t.Errorf("%s line %q attribute %s = %q, want %q (attributes %v)", level, msg, k, e.attrs[k], v, e.attrs)
		}
	}
	if at != anyTime && e.at.Sub(h.base) != at {
		h.t.Errorf("%s line %q written at %v, want %v", level, msg, e.at.Sub(h.base), at)
	}
}

func TestNew_InvalidJobs(t *testing.T) {
	good := func(name string) scheduler.Job { return job(newScripted(name, nil), 10*time.Second, 2*time.Second) }
	withSchedule := func(name string, s scheduler.Schedule) scheduler.Job {
		return scheduler.Job{Collector: newScripted(name, nil), Schedule: s}
	}
	sched := func(interval, timeout, pressure time.Duration) scheduler.Schedule {
		return scheduler.Schedule{Interval: interval, Timeout: timeout, PressureInterval: pressure}
	}
	logger := slog.New(slog.DiscardHandler)
	var sink scheduler.Putter = scheduler.DiscardSink{}
	longName := strings.Repeat("a", 129)

	const (
		errSinkLogger = "scheduler: a sink and a logger are required"
		errNilColl    = "scheduler: job 0: collector is nil"
		errName       = "scheduler: job 0: collector name must match the wire format's name pattern"
		errInterval   = "scheduler: job 0 (x): interval must be positive"
		errTimeout    = "scheduler: job 0 (x): timeout must be positive and at most the interval"
		errPressure   = "scheduler: job 0 (x): pressure interval must be at least the timeout and at most the interval"
	)

	tests := []struct {
		name string
		jobs []scheduler.Job
		sink scheduler.Putter
		log  *slog.Logger
		want string
	}{
		{"nil sink", []scheduler.Job{good("x")}, nil, logger, errSinkLogger},
		{"nil logger", []scheduler.Job{good("x")}, sink, nil, errSinkLogger},
		{"nil sink and logger", []scheduler.Job{good("x")}, nil, nil, errSinkLogger},
		{"nil sink before a bad job", []scheduler.Job{{}}, nil, logger, errSinkLogger},
		{"nil collector", []scheduler.Job{{Schedule: sched(10*time.Second, 2*time.Second, 0)}}, sink, logger, errNilColl},
		{"empty name", []scheduler.Job{good("")}, sink, logger, errName},
		{"name starts with dash", []scheduler.Job{good("-x")}, sink, logger, errName},
		{"name of 129 bytes", []scheduler.Job{good(longName)}, sink, logger, errName},
		{"name with space", []scheduler.Job{good("a b")}, sink, logger, errName},
		{"name before schedule", []scheduler.Job{withSchedule("", sched(0, 0, 0))}, sink, logger, errName},
		{"duplicate name", []scheduler.Job{good("y"), good("y")}, sink, logger, "scheduler: job 1 (y): duplicate collector name"},
		{
			"duplicate before schedule",
			[]scheduler.Job{good("x"), withSchedule("x", sched(0, 0, 0))},
			sink, logger, "scheduler: job 1 (x): duplicate collector name",
		},
		{"interval zero", []scheduler.Job{withSchedule("x", sched(0, 0, 0))}, sink, logger, errInterval},
		{"interval negative", []scheduler.Job{withSchedule("x", sched(-time.Second, 0, 0))}, sink, logger, errInterval},
		{"timeout zero", []scheduler.Job{withSchedule("x", sched(10*time.Second, 0, 0))}, sink, logger, errTimeout},
		{
			"timeout above interval",
			[]scheduler.Job{withSchedule("x", sched(10*time.Second, 10*time.Second+1, 0))}, sink, logger, errTimeout,
		},
		{
			"timeout before pressure",
			[]scheduler.Job{withSchedule("x", sched(10*time.Second, 0, time.Second))}, sink, logger, errTimeout,
		},
		{
			"pressure interval below timeout",
			[]scheduler.Job{withSchedule("x", sched(10*time.Second, 2*time.Second, 2*time.Second-1))}, sink, logger, errPressure,
		},
		{
			"pressure interval above interval",
			[]scheduler.Job{withSchedule("x", sched(10*time.Second, 2*time.Second, 10*time.Second+1))}, sink, logger, errPressure,
		},
		{
			"pressure interval negative",
			[]scheduler.Job{withSchedule("x", sched(10*time.Second, 2*time.Second, -time.Second))}, sink, logger, errPressure,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got, err := scheduler.New(tc.jobs, tc.sink, tc.log)

			if err == nil || err.Error() != tc.want {
				t.Errorf("New() error = %v, want %q", err, tc.want)
			}
			if got != nil {
				t.Errorf("New() scheduler = %v, want nil with an error", got)
			}
		})
	}
}

func TestNew_ValidJobs(t *testing.T) {
	tests := []struct {
		name     string
		pressure time.Duration
	}{
		{"pressure interval equal to the timeout", 2 * time.Second},
		{"pressure interval equal to the interval", 10 * time.Second},
		{"no pressure interval", 0},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				c := newScripted("x", nil)
				j := scheduler.Job{Collector: c, Schedule: scheduler.Schedule{
					Interval: 10 * time.Second, Timeout: 2 * time.Second, PressureInterval: tc.pressure,
				}}

				h := start(t, []scheduler.Job{j}, &recordingSink{})
				h.sleepTo(time.Second)

				if got := h.offsets(c.started()); !slices.Equal(got, seconds(0)) {
					t.Errorf("run starts of a valid job = %v, want [0s]", got)
				}
			})
		})
	}

	t.Run("no jobs", func(t *testing.T) {
		synctest.Test(t, func(t *testing.T) {
			h := start(t, nil, &recordingSink{})
			h.sleepTo(time.Second)

			if !h.sched.Healthy(time.Now()) {
				t.Errorf("Healthy() of a scheduler without jobs = false, want true")
			}
			h.stop()
		})
	})
}

// hangSetup starts the setup of AC1: a fast job and a job that hangs on its first call (both 10 s interval, 2 s timeout).
func hangSetup(t *testing.T) (h *harness, fast *scriptedCollector, hang *hangingCollector) {
	t.Helper()

	fast = newScripted("fast", nil)
	hang = newHanging(t, "hang")
	h = start(t, []scheduler.Job{job(fast, 10*time.Second, 2*time.Second), job(hang, 10*time.Second, 2*time.Second)}, &recordingSink{})
	return h, fast, hang
}

func TestScheduler_HangingCollectorDoesNotDelayOthers(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		h, fast, hang := hangSetup(t)

		h.sleepTo(60 * time.Second)

		want := seconds(0, 10, 20, 30, 40, 50, 60)
		if got := h.offsets(fast.started()); !slices.Equal(got, want) {
			t.Errorf("fast run starts = %v, want %v", got, want)
		}
		puts := h.sink.fromSource("fast")
		if len(puts) != len(want) {
			t.Fatalf("puts with samples of fast = %d, want %d", len(puts), len(want))
		}
		for i, p := range puts {
			if got := p.at.Sub(h.base); got != want[i] {
				t.Errorf("sample %d of fast reached the sink at %v, want its start %v", i, got, want[i])
			}
		}
		if got := hang.calls.Load(); got != 1 {
			t.Errorf("hang.Collect calls = %d, want 1 (a stuck collector is not started again)", got)
		}
	})
}

func TestScheduler_TimeoutRecordsGap(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		h, _, _ := hangSetup(t)

		h.sleepTo(2 * time.Second)

		h.requireGaps("hang", []wantGap{
			{at: 2 * time.Second, captured: 2 * time.Second, from: 0, to: 10 * time.Second, cause: model.GapCollectorTimeout},
		})

		h.sleepTo(35 * time.Second)

		h.requireLog(slog.LevelWarn, "collector timed out", anyTime, map[string]string{"collector": "hang"})
	})
}

func TestScheduler_StuckCollectorSkipsCyclesWithGaps(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		h, _, hang := hangSetup(t)

		h.sleepTo(35 * time.Second)

		s := time.Second
		h.requireGaps("hang", []wantGap{
			{at: 2 * s, captured: 2 * s, from: 0, to: 10 * s, cause: model.GapCollectorTimeout},
			{at: 10 * s, captured: 10 * s, from: 10 * s, to: 20 * s, cause: model.GapCollectorTimeout},
			{at: 20 * s, captured: 20 * s, from: 20 * s, to: 30 * s, cause: model.GapCollectorTimeout},
			{at: 30 * s, captured: 30 * s, from: 30 * s, to: 40 * s, cause: model.GapCollectorTimeout},
		})
		if got := hang.calls.Load(); got != 1 {
			t.Errorf("hang.Collect calls = %d, want 1 while it is stuck", got)
		}
	})
}

func TestScheduler_LateResultDiscardedAndCollectorResumes(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		hang := newScripted("hang", func(_ context.Context, n int, _ scheduler.Run) ([]model.Record, error) {
			if n == 0 {
				time.Sleep(25 * time.Second)
				return []model.Record{metricRecord("late", 1)}, nil
			}
			return []model.Record{metricRecord("second", float64(n))}, nil
		})
		h := start(t, []scheduler.Job{job(hang, 10*time.Second, 2*time.Second)}, &recordingSink{})

		h.sleepTo(35 * time.Second)

		if got := h.offsets(hang.started()); !slices.Equal(got, seconds(0, 30)) {
			t.Errorf("run starts = %v, want [0s 30s]", got)
		}
		if late := h.sink.fromSource("late"); len(late) != 0 {
			t.Errorf("late result reached the sink: %d puts, want none", len(late))
		}
		second := h.sink.fromSource("second")
		if len(second) != 1 || second[0].at.Sub(h.base) != 30*time.Second {
			t.Errorf("records of the run at 30s = %v, want one put at 30s", second)
		}
		s := time.Second
		h.requireGaps("hang", []wantGap{
			{at: 2 * s, captured: anyTime, from: 0, to: 10 * s, cause: model.GapCollectorTimeout},
			{at: 10 * s, captured: anyTime, from: 10 * s, to: 20 * s, cause: model.GapCollectorTimeout},
			{at: 20 * s, captured: anyTime, from: 20 * s, to: 30 * s, cause: model.GapCollectorTimeout},
		})
		h.requireLog(slog.LevelInfo, "collector returned after its deadline", 30*time.Second,
			map[string]string{"collector": "hang", "skipped": "2"})
	})
}

func TestScheduler_RunStopsWithoutWaitingForStuckWorker(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		hang := newHanging(t, "hang")
		h := start(t, []scheduler.Job{job(hang, 10*time.Second, 2*time.Second)}, &recordingSink{})
		h.sleepTo(5 * time.Second)
		if got := hang.calls.Load(); got != 1 {
			t.Fatalf("hang.Collect calls before the stop = %d, want 1", got)
		}

		h.cancel()
		select {
		case <-h.done:
		case <-time.After(time.Second):
			t.Fatalf("Run did not return within 1s after its context was cancelled while hang is stuck")
		}

		before := h.sink.calls.Load()
		hang.Release()
		time.Sleep(time.Minute)
		synctest.Wait()
		if after := h.sink.calls.Load(); after != before {
			t.Errorf("Put calls after Run returned = %d, want %d (none)", after, before)
		}
	})
}

func TestScheduler_RunStopsSinkBlockedOnContext(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		var returned atomic.Bool
		var putErr atomic.Pointer[error]
		sink := &recordingSink{hook: func(ctx context.Context, _ int) error {
			<-ctx.Done()
			err := ctx.Err()
			putErr.Store(&err)
			returned.Store(true)
			return err
		}}
		c := newScripted("x", nil)
		h := start(t, []scheduler.Job{job(c, 10*time.Second, 2*time.Second)}, sink)

		h.sleepTo(60 * time.Second)

		if got := sink.calls.Load(); got != 1 {
			t.Fatalf("Put calls at 60s = %d, want 1 (the first Put is still blocked)", got)
		}
		if returned.Load() {
			t.Fatalf("Put returned before the context was cancelled, want it blocked at 60s (its context is not bounded by the timeout)")
		}

		h.cancel()
		select {
		case <-h.done:
		case <-time.After(time.Second):
			t.Fatalf("Run did not return within 1s after its context was cancelled while Put was blocked")
		}
		p := putErr.Load()
		if p == nil || !errors.Is(*p, context.Canceled) {
			t.Errorf("Put returned error %v after the cancel, want an error that is context.Canceled", p)
		}
	})
}

// blockFirstPut returns a sink hook whose first Put blocks until release is closed or the context is done.
func blockFirstPut(release <-chan struct{}) func(context.Context, int) error {
	return func(ctx context.Context, n int) error {
		if n == 0 {
			select {
			case <-release:
			case <-ctx.Done():
			}
		}
		return nil
	}
}

func requireHealthy(t *testing.T, s *scheduler.Scheduler, now time.Time, want bool, why string) {
	t.Helper()

	if got := s.Healthy(now); got != want {
		t.Errorf("Healthy() %s = %v, want %v", why, got, want)
	}
}

func TestScheduler_Healthy(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		release := make(chan struct{})
		sink := &recordingSink{hook: blockFirstPut(release)}
		jobs := []scheduler.Job{job(newScripted("x", nil), 10*time.Second, 2*time.Second)}

		idle, err := scheduler.New(jobs, sink, slog.New(slog.DiscardHandler))
		if err != nil {
			t.Fatalf("New() error = %v, want nil", err)
		}
		requireHealthy(t, idle, time.Now(), true, "of a scheduler whose Run has not started")

		h := start(t, jobs, sink)
		h.sleepTo(31 * time.Second)
		requireHealthy(t, h.sched, time.Now(), true, "at 31s with Put blocked since 0s (deadline 32s)")
		h.sleepTo(32 * time.Second)
		requireHealthy(t, h.sched, time.Now(), true, "at 32s (the deadline itself is still healthy)")
		requireHealthy(t, h.sched, h.base.Add(32*time.Second+time.Nanosecond), false,
			"at 32s+1ns (due 0 + timeout 2s + LivenessSlack "+scheduler.LivenessSlack.String()+")")

		close(release)
		synctest.Wait()
		requireHealthy(t, h.sched, time.Now(), true, "after Put returned")
	})
}

func TestScheduler_HealthyAcrossPressureSwitch(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		adaptive := newScripted("adaptive", nil)
		j := scheduler.Job{Collector: adaptive, Schedule: scheduler.Schedule{
			Interval: 60 * time.Second, Timeout: 5 * time.Second, PressureInterval: 10 * time.Second,
		}}
		h := start(t, []scheduler.Job{j}, &recordingSink{})

		for i := 0; i <= 120; i++ {
			synctest.Wait()
			if !h.sched.Healthy(time.Now()) {
				t.Fatalf("Healthy() at %ds = false, want true at every second (pressure switch at 45s)", i)
			}
			if i == 45 {
				h.sched.SetPressure(true)
			}
			time.Sleep(time.Second)
		}
	})
}

func TestScheduler_SinkFailureLoggedOnce(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		sink := &recordingSink{hook: func(_ context.Context, n int) error {
			if n < 3 {
				return errors.New("spool full")
			}
			return nil
		}}
		h := start(t, []scheduler.Job{job(newScripted("x", nil), 10*time.Second, 2*time.Second)}, sink)

		h.sleepTo(35 * time.Second)

		h.requireLog(slog.LevelError, "sink failed", anyTime, map[string]string{"collector": "x"})
		h.requireLog(slog.LevelInfo, "sink recovered", 30*time.Second, map[string]string{"collector": "x"})
		if got := h.sink.gaps(""); len(got) != 0 {
			t.Errorf("gaps for refused records = %s, want none (the sink owns that loss)", h.describe(got))
		}
		if got := sink.calls.Load(); got != 4 {
			t.Errorf("Put calls over four runs = %d, want 4", got)
		}
	})
}

func TestScheduler_CancelWhileCollectorBlocked(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		hang := newHanging(t, "hang")
		h := start(t, []scheduler.Job{job(hang, 10*time.Second, 2*time.Second)}, &recordingSink{})
		h.sleepTo(time.Second)

		h.cancel()
		select {
		case <-h.done:
		case <-time.After(500 * time.Millisecond):
			t.Fatalf("Run did not return before the 2s timeout after the cancel at 1s")
		}

		synctest.Wait()
		if got := h.sink.gaps(""); len(got) != 0 {
			t.Errorf("gaps after a stop = %s, want none", h.describe(got))
		}
	})
}

// flushValue is the value of the metric record a flushingCollector returns from Flush; its Collect returns smaller values.
const flushValue = 99.0

// flushingCollector is a scheduler.Collector that also implements scheduler.Flusher and scheduler.Observer.
type flushingCollector struct {
	name         string
	sink         *recordingSink
	collectGate  chan struct{} // when set, the first Collect blocks until it is closed, ignoring its context
	flushGate    chan struct{} // when set, Flush blocks until it is closed, ignoring its context
	flushRecords []model.Record
	collects     atomic.Int32
	collecting   atomic.Int32
	overlapped   atomic.Bool // Flush was called while a Collect was still running
	mu           sync.Mutex
	flushRuns    []scheduler.Run
	kept         []keptCall
}

func newFlushing(name string, sink *recordingSink) *flushingCollector {
	return &flushingCollector{name: name, sink: sink, flushRecords: []model.Record{metricRecord(name, flushValue)}}
}

func (c *flushingCollector) Name() string { return c.name }

func (c *flushingCollector) Collect(context.Context, scheduler.Run) ([]model.Record, error) {
	n := c.collects.Add(1)
	if c.collectGate != nil && n == 1 {
		c.collecting.Add(1)
		defer c.collecting.Add(-1)
		<-c.collectGate
		return nil, nil
	}
	return []model.Record{metricRecord(c.name, float64(n))}, nil
}

func (c *flushingCollector) Flush(_ context.Context, run scheduler.Run) []model.Record {
	if c.collecting.Load() > 0 {
		c.overlapped.Store(true)
	}
	c.mu.Lock()
	c.flushRuns = append(c.flushRuns, run)
	c.mu.Unlock()
	if c.flushGate != nil {
		<-c.flushGate
	}
	return slices.Clone(c.flushRecords)
}

func (c *flushingCollector) Kept(run scheduler.Run, kept bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.kept = append(c.kept, keptCall{start: run.Start, kept: kept, puts: c.sink.calls.Load()})
}

func (c *flushingCollector) flushed() []scheduler.Run {
	c.mu.Lock()
	defer c.mu.Unlock()
	return slices.Clone(c.flushRuns)
}

func (c *flushingCollector) keptCalls() []keptCall {
	c.mu.Lock()
	defer c.mu.Unlock()
	return slices.Clone(c.kept)
}

// flushRecordsIn returns the records of the sink that came from a Flush.
func flushRecordsIn(sink *recordingSink) []model.Record {
	var out []model.Record
	for _, r := range sink.records() {
		if p, ok := r.Data.(*model.MetricPoint); ok && p.Value == flushValue {
			out = append(out, r)
		}
	}
	return out
}

// flushCase is one case of TestScheduler_FlushPutsTheRecordsAndReportsKept.
type flushCase struct {
	name     string
	sinkFail bool
	records  []model.Record
	wantKept bool
}

func TestScheduler_FlushPutsTheRecordsAndReportsKept(t *testing.T) {
	nan := model.Record{Meta: model.Meta{Source: "flushing"}, Data: &model.MetricPoint{Name: "sample", Value: math.NaN()}}
	tests := []flushCase{
		{name: "the sink keeps the records", wantKept: true},
		{name: "the sink fails", sinkFail: true},
		{name: "a record is refused", records: []model.Record{metricRecord("flushing", flushValue), nan}},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) { runFlushCase(t, tc) })
		})
	}
}

func runFlushCase(t *testing.T, tc flushCase) {
	t.Helper()

	var failPuts atomic.Bool
	sink := &recordingSink{hook: func(context.Context, int) error {
		if failPuts.Load() {
			return errors.New("disk full")
		}
		return nil
	}}
	fc := newFlushing("flushing", sink)
	if tc.records != nil {
		fc.flushRecords = tc.records
	}
	plain := newScripted("plain", nil)
	h := start(t, []scheduler.Job{job(fc, 10*time.Second, 2*time.Second), job(plain, 10*time.Second, 2*time.Second)}, sink)
	h.sleepTo(5 * time.Second)
	h.stop()
	putsAtStop := sink.calls.Load()
	keptAtStop := len(fc.keptCalls())
	plainPutsAtStop := len(sink.fromSource("plain"))
	failPuts.Store(tc.sinkFail)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	h.sched.Flush(ctx)

	runs := fc.flushed()
	if len(runs) != 1 {
		t.Fatalf("Flush calls of the collector = %d, want 1 (case %q)", len(runs), tc.name)
	}
	if runs[0].Start.IsZero() || runs[0].Start.Location() != time.UTC {
		t.Errorf("Run.Start of the flush = %v, want the time of the flush in UTC", runs[0].Start)
	}
	got := flushRecordsIn(sink)
	if len(got) != 1 {
		t.Fatalf("flush records put = %d, want 1 (the valid record, put once; case %q); all records: %v", len(got), tc.name, sink.records())
	}
	requireStamped(t, got, "flushing")
	if !got[0].CapturedAt.Equal(runs[0].Start) {
		t.Errorf("captured_at of the flush record = %v, want the start of the flush run %v", got[0].CapturedAt, runs[0].Start)
	}
	calls := fc.keptCalls()[keptAtStop:]
	if len(calls) != 1 || calls[0].kept != tc.wantKept || !calls[0].start.Equal(runs[0].Start) {
		t.Fatalf("Kept calls = %+v, want exactly one for the flush run %v with kept = %v (case %q)", calls, runs[0].Start, tc.wantKept, tc.name)
	}
	if calls[0].puts <= putsAtStop {
		t.Errorf("Put calls when Kept was called = %d, want more than the %d before the flush (the records are put before Kept)", calls[0].puts, putsAtStop)
	}
	if n := len(sink.fromSource("plain")); n != plainPutsAtStop {
		t.Errorf("puts of the collector without Flush = %d after the flush, want %d (it has nothing to flush)", n, plainPutsAtStop)
	}
	if plain.started()[0].Start.IsZero() {
		t.Errorf("run of the collector without Flush has no start")
	}
}

func TestScheduler_FlushWaitsForAStoppedWorkerOrSkips(t *testing.T) {
	t.Run("the worker returns before the context ends", func(t *testing.T) {
		synctest.Test(t, runFlushWaits)
	})
	t.Run("the worker outlasts the context", func(t *testing.T) {
		synctest.Test(t, runFlushSkips)
	})
}

// stopWithBlockedCollect runs a scheduler whose first Collect blocks, stops it at 1 s and returns the harness with the collector.
func stopWithBlockedCollect(t *testing.T) (*harness, *flushingCollector, *recordingSink) {
	t.Helper()

	sink := &recordingSink{}
	fc := newFlushing("flushing", sink)
	fc.collectGate = make(chan struct{})
	t.Cleanup(func() { closeOnce(fc.collectGate) })
	h := start(t, []scheduler.Job{job(fc, 10*time.Second, 2*time.Second)}, sink)
	h.sleepTo(time.Second)
	h.stop() // the stop leaves the blocked Collect running
	return h, fc, sink
}

func runFlushWaits(t *testing.T) {
	t.Helper()

	h, fc, sink := stopWithBlockedCollect(t)
	keptAtStop := len(fc.keptCalls())
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	flushed := make(chan struct{})
	go func() {
		defer close(flushed)
		h.sched.Flush(ctx)
	}()

	time.Sleep(2 * time.Second)
	synctest.Wait()

	if n := len(fc.flushed()); n != 0 {
		t.Fatalf("Flush calls while Collect still runs = %d, want 0 (Flush waits for the worker)", n)
	}
	closeOnce(fc.collectGate)
	<-flushed

	if n := len(fc.flushed()); n != 1 {
		t.Fatalf("Flush calls after Collect returned = %d, want 1", n)
	}
	if fc.overlapped.Load() {
		t.Errorf("Flush ran while Collect was still running, want never concurrent")
	}
	if n := len(flushRecordsIn(sink)); n != 1 {
		t.Errorf("flush records put = %d, want 1", n)
	}
	if calls := fc.keptCalls()[keptAtStop:]; len(calls) != 1 || calls[0].kept == false {
		t.Errorf("Kept calls of the flush = %+v, want one with kept = true", calls)
	}
}

func runFlushSkips(t *testing.T) {
	t.Helper()

	h, fc, sink := stopWithBlockedCollect(t)
	begin := time.Now()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()

	h.sched.Flush(ctx)

	if took := time.Since(begin); took != 3*time.Second {
		t.Errorf("Flush returned after %v, want 3s (it gives up when its context ends)", took)
	}
	if n := len(fc.flushed()); n != 0 {
		t.Errorf("Flush calls = %d, want 0 (skipped while the worker is out)", n)
	}
	if n := len(flushRecordsIn(sink)); n != 0 {
		t.Errorf("flush records put = %d, want 0", n)
	}
	closeOnce(fc.collectGate)
	synctest.Wait()
	if n := len(fc.flushed()); n != 0 {
		t.Errorf("Flush calls after the late return of Collect = %d, want 0 (a skipped flush is not made up later)", n)
	}
	if fc.overlapped.Load() {
		t.Errorf("Flush ran while Collect was still running, want never concurrent")
	}
}

func TestScheduler_FlushHonoursItsContextWhenFlushBlocks(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		sink := &recordingSink{}
		healthy := newFlushing("healthy", sink)
		blocked := newFlushing("blocked", sink)
		blocked.flushGate = make(chan struct{})
		t.Cleanup(func() { closeOnce(blocked.flushGate) })
		h := start(t, []scheduler.Job{job(healthy, 10*time.Second, 2*time.Second), job(blocked, 10*time.Second, 2*time.Second)}, sink)
		h.sleepTo(time.Second)
		h.stop()
		healthyKeptAtStop, blockedKeptAtStop := len(healthy.keptCalls()), len(blocked.keptCalls())
		begin := time.Now()
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()

		h.sched.Flush(ctx)

		if took := time.Since(begin); took != 3*time.Second {
			t.Errorf("Flush returned after %v, want 3s (the blocked Flush is abandoned when the context ends)", took)
		}
		if n := len(blocked.flushed()); n != 1 {
			t.Errorf("Flush calls of the blocked collector = %d, want 1", n)
		}
		if len(flushRecordsIn(sink)) != 1 {
			t.Errorf("flush records put = %d, want 1 (the healthy collector's, flushed before the blocked one is abandoned)", len(flushRecordsIn(sink)))
		}
		requireKeptOnce(t, healthy.keptCalls()[healthyKeptAtStop:], "healthy")
		closeOnce(blocked.flushGate)
		synctest.Wait()
		if calls := blocked.keptCalls()[blockedKeptAtStop:]; len(calls) != 0 {
			t.Errorf("Kept calls of the abandoned collector = %+v, want none (no observer is told)", calls)
		}
		for _, r := range flushRecordsIn(sink) {
			if r.Source == "blocked" {
				t.Errorf("record of the abandoned Flush reached the sink after its late return: %+v, want none", r)
			}
		}
	})
}

// closeOnce closes ch unless it is closed already; it is called from one goroutine at a time.
func closeOnce(ch chan struct{}) {
	select {
	case <-ch:
	default:
		close(ch)
	}
}

// requireKeptOnce fails unless calls is exactly one call of Kept with kept = true.
func requireKeptOnce(t *testing.T, calls []keptCall, who string) {
	t.Helper()

	if len(calls) != 1 || calls[0].kept == false {
		t.Errorf("Kept calls of the %s collector = %+v, want one with kept = true", who, calls)
	}
}
