package scheduler_test

import (
	"context"
	"errors"
	"log/slog"
	"maps"
	"math"
	"slices"
	"strings"
	"sync"
	"testing"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

func TestPanicError_Error(t *testing.T) {
	tests := []struct {
		name  string
		value any
		want  string
	}{
		{"string", "boom", "scheduler: collector panicked: boom"},
		{"error value", errors.New("bad state"), "scheduler: collector panicked: bad state"},
		{"integer", 42, "scheduler: collector panicked: 42"},
		{"cut at 1024 bytes", strings.Repeat("a", 2000), "scheduler: collector panicked: " + strings.Repeat("a", 1024)},
		{"exactly 1024 bytes", strings.Repeat("b", 1024), "scheduler: collector panicked: " + strings.Repeat("b", 1024)},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			e := &scheduler.PanicError{Value: tc.value}

			if got := e.Error(); got != tc.want {
				t.Errorf("PanicError (%s).Error() = %.100q (%d bytes), want %.100q (%d bytes)", tc.name, got, len(got), tc.want, len(tc.want))
			}
		})
	}
}

func TestScheduler_PanicRecoveredAsGap(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		boom := newScripted("boom", func(_ context.Context, n int, _ scheduler.Run) ([]model.Record, error) {
			if n == 0 {
				panic("boom")
			}
			return []model.Record{metricRecord("boom", float64(n))}, nil
		})
		healthy := newScripted("healthy", nil)
		h := start(t, []scheduler.Job{
			job(boom, 10*time.Second, 2*time.Second),
			job(healthy, 10*time.Second, 2*time.Second),
		}, &recordingSink{})

		h.sleepTo(15 * time.Second)

		h.requireGaps("boom", []wantGap{
			{at: 0, captured: anyTime, from: 0, to: 10 * time.Second, cause: model.GapUnknown},
		})
		if got := h.offsets(boom.started()); !slices.Equal(got, seconds(0, 10)) {
			t.Errorf("run starts of the panicking collector = %v, want [0s 10s] (it runs again at its next due time)", got)
		}
		after := h.sink.fromSource("boom")
		if len(after) != 1 || after[0].at.Sub(h.base) != 10*time.Second {
			t.Errorf("records of boom = %v, want one put at 10s (the run after the panic)", after)
		}
		if got := h.offsets(healthy.started()); !slices.Equal(got, seconds(0, 10)) {
			t.Errorf("run starts of the healthy collector = %v, want [0s 10s]", got)
		}
		h.requireLog(slog.LevelError, "collector panicked", anyTime, map[string]string{"collector": "boom", "panic": "boom"})
		stack := h.logs.find(slog.LevelError, "collector panicked")[0].attrs["stack"]
		if stack == "" || len(stack) > 4096 {
			t.Errorf(`"collector panicked" stack attribute has %d bytes, want 1 to 4096 bytes`, len(stack))
		}
	})
}

func TestScheduler_CollectorErrorRecordsGap(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		flaky := newScripted("flaky", func(_ context.Context, n int, _ scheduler.Run) ([]model.Record, error) {
			if n < 3 {
				return []model.Record{metricRecord("dropped", 1)}, errors.New("disk on fire")
			}
			return []model.Record{metricRecord("flaky", float64(n))}, nil
		})
		h := start(t, []scheduler.Job{job(flaky, 10*time.Second, 2*time.Second)}, &recordingSink{})

		h.sleepTo(35 * time.Second)

		s := time.Second
		h.requireGaps("flaky", []wantGap{
			{at: 0, captured: anyTime, from: 0, to: 10 * s, cause: model.GapUnknown},
			{at: 10 * s, captured: anyTime, from: 10 * s, to: 20 * s, cause: model.GapUnknown},
			{at: 20 * s, captured: anyTime, from: 20 * s, to: 30 * s, cause: model.GapUnknown},
		})
		if dropped := h.sink.fromSource("dropped"); len(dropped) != 0 {
			t.Errorf("records of a failed run reached the sink: %d puts, want none", len(dropped))
		}
		if ok := h.sink.fromSource("flaky"); len(ok) != 1 || ok[0].at.Sub(h.base) != 30*s {
			t.Errorf("records of the successful run = %v, want one put at 30s", ok)
		}
		h.requireLog(slog.LevelWarn, "collector failed", anyTime, map[string]string{"collector": "flaky"})
		h.requireLog(slog.LevelInfo, "collector recovered", 30*s, map[string]string{"collector": "flaky", "failed_runs": "3"})
	})
}

// requireStamped checks that records are of origin agent with sequence number 0 and have the given sources.
func requireStamped(t *testing.T, records []model.Record, sources ...string) {
	t.Helper()

	for i, want := range sources {
		r := records[i]
		if r.Origin != model.OriginAgent || r.Seq != 0 || r.Source != want {
			t.Errorf("record %d meta = origin %q seq %d source %q, want origin agent seq 0 source %q",
				i, r.Origin, r.Seq, r.Source, want)
		}
	}
}

func TestScheduler_RecordsGetAgentMetadata(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		explicit := time.Date(1999, time.December, 31, 23, 59, 30, 0, time.UTC)
		meta := newScripted("meta", func(context.Context, int, scheduler.Run) ([]model.Record, error) {
			return []model.Record{
				{
					Meta: model.Meta{Origin: model.OriginImport, Source: "meta.zero", Seq: 7},
					Data: &model.MetricPoint{Name: "sample", Value: 1},
				},
				{
					Meta: model.Meta{Origin: model.OriginImport, Source: "meta.set", Seq: 7, CapturedAt: explicit},
					Data: &model.MetricPoint{Name: "sample", Value: 2},
				},
			}, nil
		})
		h := start(t, []scheduler.Job{job(meta, 10*time.Second, 2*time.Second)}, &recordingSink{})

		h.sleepTo(time.Second)

		runs := meta.started()
		if len(runs) != 1 {
			t.Fatalf("collector runs = %d, want 1", len(runs))
		}
		if runs[0].Start.Location() != time.UTC {
			t.Errorf("Run.Start location = %v, want UTC", runs[0].Start.Location())
		}
		got := h.sink.records()
		if len(got) != 2 {
			t.Fatalf("records received = %d, want 2", len(got))
		}
		requireStamped(t, got, "meta.zero", "meta.set")
		if !got[0].CapturedAt.Equal(runs[0].Start) {
			t.Errorf("zero captured_at became %v, want Run.Start %v", got[0].CapturedAt, runs[0].Start)
		}
		if !got[1].CapturedAt.Equal(explicit) {
			t.Errorf("set captured_at became %v, want it unchanged %v", got[1].CapturedAt, explicit)
		}
	})
}

// oversizedSnapshot returns a valid process snapshot whose encoded line exceeds the wire format's line limit.
func oversizedSnapshot() *model.ProcessSnapshot {
	snap := &model.ProcessSnapshot{Complete: true}
	for i := range 100 {
		snap.Processes = append(snap.Processes, model.ProcessSample{
			PID:     int32(i + 1),
			Command: "c",
			Cmdline: strings.Repeat("a", model.MaxTextBytes),
		})
	}
	return snap
}

// requireRefusal checks the premise of a table case: wire.CheckRecord refuses bad, with a size error only when size is set.
func requireRefusal(t *testing.T, bad model.Record, size bool) {
	t.Helper()

	check := bad
	check.Origin, check.Seq = model.OriginAgent, 1
	if check.CapturedAt.IsZero() {
		check.CapturedAt = time.Date(2026, time.March, 1, 12, 0, 0, 0, time.UTC)
	}
	_, err := wire.CheckRecord(&check)
	var sizeErr *wire.RecordSizeError
	if err == nil || errors.As(err, &sizeErr) != size {
		t.Fatalf("test data: wire.CheckRecord(bad) = %v, want a refusal (a *wire.RecordSizeError: %v)", err, size)
	}
}

func runInvalidRecord(t *testing.T, bad model.Record) {
	t.Helper()

	mix := newScripted("mix", func(context.Context, int, scheduler.Run) ([]model.Record, error) {
		return []model.Record{metricRecord("first", 1), bad, metricRecord("third", 3)}, nil
	})
	h := start(t, []scheduler.Job{job(mix, 10*time.Second, 2*time.Second)}, &recordingSink{})

	h.sleepTo(time.Second)

	var sources []string
	for _, r := range h.sink.records() {
		if _, isGap := r.Data.(*model.Gap); !isGap {
			sources = append(sources, r.Source)
		}
	}
	if !slices.Equal(sources, []string{"first", "third"}) {
		t.Errorf("records received (without gaps) from sources %v, want [first third]", sources)
	}
	h.requireGaps("mix", []wantGap{{at: 0, captured: anyTime, from: 0, to: 10 * time.Second, cause: model.GapUnknown}})
	h.requireLog(slog.LevelWarn, "record refused", anyTime, map[string]string{"collector": "mix"})
}

func TestScheduler_InvalidRecordReplacedByGap(t *testing.T) {
	zone := time.FixedZone("", 2*3600)
	tests := []struct {
		name string
		bad  model.Record
		size bool
	}{
		{
			"NaN value",
			model.Record{Meta: model.Meta{Source: "mix"}, Data: &model.MetricPoint{Name: "sample", Value: math.NaN()}},
			false,
		},
		{
			"log line with a non-UTC captured_at",
			model.Record{
				Meta: model.Meta{Source: "mix", CapturedAt: time.Date(2026, time.March, 1, 12, 0, 0, 0, zone)},
				Data: &model.LogLine{Log: "x", Message: "m"},
			},
			false,
		},
		{
			"process snapshot over the line limit",
			model.Record{Meta: model.Meta{Source: "mix"}, Data: oversizedSnapshot()},
			true,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			requireRefusal(t, tc.bad, tc.size)
			synctest.Test(t, func(t *testing.T) { runInvalidRecord(t, tc.bad) })
		})
	}
}

// pressureJob is the job adaptive of AC9: 60 s interval, 5 s timeout, 10 s under memory pressure.
func pressureJob(c scheduler.Collector) scheduler.Job {
	return scheduler.Job{Collector: c, Schedule: scheduler.Schedule{
		Interval: 60 * time.Second, Timeout: 5 * time.Second, PressureInterval: 10 * time.Second,
	}}
}

// pressureEvent is a call of SetPressure at an offset from the start of Run.
type pressureEvent struct {
	at   time.Duration
	high bool
}

// pressureCase is one case of TestScheduler_PressureShortensInterval.
type pressureCase struct {
	name         string
	withPlain    bool
	events       []pressureEvent
	blockFirst   time.Duration // the first Put sleeps this long
	until        time.Duration
	wantStarts   []time.Duration
	wantPressure []bool
	wantPlain    []time.Duration
	wantGaps     []wantGap
}

func runPressureCase(t *testing.T, tc pressureCase) {
	t.Helper()

	adaptive := newScripted("adaptive", nil)
	plain := newScripted("plain", nil)
	jobs := []scheduler.Job{pressureJob(adaptive)}
	if tc.withPlain {
		jobs = append(jobs, job(plain, 60*time.Second, 5*time.Second))
	}
	sink := &recordingSink{}
	if tc.blockFirst > 0 {
		sink.hook = func(_ context.Context, n int) error {
			if n == 0 {
				time.Sleep(tc.blockFirst)
			}
			return nil
		}
	}
	h := start(t, jobs, sink)

	for _, e := range tc.events {
		h.sleepTo(e.at)
		h.sched.SetPressure(e.high)
	}
	h.sleepTo(tc.until)

	runs := adaptive.started()
	if got := h.offsets(runs); !slices.Equal(got, tc.wantStarts) {
		t.Errorf("adaptive run starts = %v, want %v", got, tc.wantStarts)
	}
	if got := pressures(runs); !slices.Equal(got, tc.wantPressure) {
		t.Errorf("adaptive Run.Pressure = %v, want %v", got, tc.wantPressure)
	}
	if tc.withPlain {
		plainRuns := plain.started()
		if got := h.offsets(plainRuns); !slices.Equal(got, tc.wantPlain) {
			t.Errorf("plain run starts = %v, want %v", got, tc.wantPlain)
		}
		if got := pressures(plainRuns); !slices.Equal(got, []bool{false, false}) {
			t.Errorf("plain Run.Pressure = %v, want [false false]", got)
		}
	}
	h.requireGaps("adaptive", tc.wantGaps)
}

func TestScheduler_PressureShortensInterval(t *testing.T) {
	s := time.Second
	tests := []pressureCase{
		{
			name:         "switch on at 15s and off at 40s",
			withPlain:    true,
			events:       []pressureEvent{{15 * s, true}, {20 * s, true}, {40 * s, false}},
			until:        100 * s,
			wantStarts:   seconds(0, 15, 25, 35, 95),
			wantPressure: []bool{false, true, true, true, false},
			wantPlain:    seconds(0, 60),
		},
		{
			name:         "switch long after the old grid point",
			events:       []pressureEvent{{45 * s, true}},
			until:        70 * s,
			wantStarts:   seconds(0, 45, 55, 65),
			wantPressure: []bool{false, true, true, true},
		},
		{
			name:         "switch shortly after a run keeps the grid",
			events:       []pressureEvent{{3 * s, true}},
			until:        15 * s,
			wantStarts:   seconds(0, 10),
			wantPressure: []bool{false, true},
		},
		{
			name:         "switch while the runner is held in Put for less than a slot",
			events:       []pressureEvent{{24 * s, true}},
			blockFirst:   26 * s,
			until:        36 * s,
			wantStarts:   seconds(0, 26, 34),
			wantPressure: []bool{false, true, true},
		},
		{
			name:         "switch while the runner is held in Put for a whole slot",
			events:       []pressureEvent{{24 * s, true}},
			blockFirst:   50 * s,
			until:        52 * s,
			wantStarts:   seconds(0, 50),
			wantPressure: []bool{false, true},
			wantGaps:     []wantGap{{at: 50 * s, captured: anyTime, from: 24 * s, to: 50 * s, cause: model.GapUnknown}},
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) { runPressureCase(t, tc) })
		})
	}
}

func TestScheduler_LateWakeUpRecordsGap(t *testing.T) {
	s := time.Second
	tests := []struct {
		name       string
		block      time.Duration
		until      time.Duration
		wantStarts []time.Duration
		wantGaps   []wantGap
	}{
		{
			name:       "Put returns a whole slot late",
			block:      30 * s,
			until:      42 * s,
			wantStarts: seconds(0, 30, 40),
			wantGaps:   []wantGap{{at: 30 * s, captured: anyTime, from: 10 * s, to: 30 * s, cause: model.GapUnknown}},
		},
		{
			name:       "Put returns inside the next slot",
			block:      15 * s,
			until:      22 * s,
			wantStarts: seconds(0, 15, 20),
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				c := newScripted("late", nil)
				sink := &recordingSink{hook: func(_ context.Context, n int) error {
					if n == 0 {
						time.Sleep(tc.block)
					}
					return nil
				}}
				h := start(t, []scheduler.Job{job(c, 10*time.Second, 2*time.Second)}, sink)

				h.sleepTo(tc.until)

				if got := h.offsets(c.started()); !slices.Equal(got, tc.wantStarts) {
					t.Errorf("run starts = %v, want %v", got, tc.wantStarts)
				}
				h.requireGaps("late", tc.wantGaps)
			})
		})
	}
}

// lostCase is one case of TestRunner_PreviousLostAfterEveryKindOfLoss.
type lostCase struct {
	name       string
	first      func() ([]model.Record, error) // what the first run does; every later run returns one clean record
	sinkFails  bool                           // the first Put returns an error
	until      time.Duration
	wantStarts []time.Duration
	wantLost   []bool
}

func runLostCase(t *testing.T, tc lostCase) {
	t.Helper()

	c := newScripted("lost", func(_ context.Context, n int, _ scheduler.Run) ([]model.Record, error) {
		if n == 0 {
			return tc.first()
		}
		return []model.Record{metricRecord("lost", 1)}, nil
	})
	sink := &recordingSink{}
	if tc.sinkFails {
		sink.hook = func(_ context.Context, n int) error {
			if n == 0 {
				return errors.New("spool full")
			}
			return nil
		}
	}
	h := start(t, []scheduler.Job{job(c, 10*time.Second, 2*time.Second)}, sink)

	h.sleepTo(tc.until)

	runs := c.started()
	if got := h.offsets(runs); !slices.Equal(got, tc.wantStarts) {
		t.Fatalf("run starts after %s = %v, want %v", tc.name, got, tc.wantStarts)
	}
	got := make([]bool, len(runs))
	for i, r := range runs {
		got[i] = r.PreviousLost
	}
	if !slices.Equal(got, tc.wantLost) {
		t.Errorf("Run.PreviousLost per run after %s = %v, want %v (true after the loss, false after a kept result)",
			tc.name, got, tc.wantLost)
	}
}

func TestRunner_PreviousLostAfterEveryKindOfLoss(t *testing.T) {
	s := time.Second
	clean := func() ([]model.Record, error) { return []model.Record{metricRecord("lost", 1)}, nil }
	nanRecord := model.Record{Meta: model.Meta{Source: "lost"}, Data: &model.MetricPoint{Name: "sample", Value: math.NaN()}}
	tests := []lostCase{
		{
			name: "a timeout",
			first: func() ([]model.Record, error) {
				time.Sleep(5 * s) // ignores its context and returns after the deadline of 2s
				return clean()
			},
			until: 25 * s, wantStarts: seconds(0, 10, 20), wantLost: []bool{false, true, false},
		},
		{
			name: "skipped runs",
			first: func() ([]model.Record, error) {
				time.Sleep(25 * s) // the cycles at 10s and 20s are skipped
				return clean()
			},
			until: 45 * s, wantStarts: seconds(0, 30, 40), wantLost: []bool{false, true, false},
		},
		{
			name:  "an error",
			first: func() ([]model.Record, error) { return nil, errors.New("disk on fire") },
			until: 25 * s, wantStarts: seconds(0, 10, 20), wantLost: []bool{false, true, false},
		},
		{
			name:  "a panic",
			first: func() ([]model.Record, error) { panic("boom") },
			until: 25 * s, wantStarts: seconds(0, 10, 20), wantLost: []bool{false, true, false},
		},
		{
			name:  "a refused record",
			first: func() ([]model.Record, error) { return []model.Record{metricRecord("lost", 1), nanRecord}, nil },
			until: 25 * s, wantStarts: seconds(0, 10, 20), wantLost: []bool{false, true, false},
		},
		{
			name: "a sink error", first: clean, sinkFails: true,
			until: 25 * s, wantStarts: seconds(0, 10, 20), wantLost: []bool{false, true, false},
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) { runLostCase(t, tc) })
		})
	}
}

// runLateStart blocks the first Put for block and checks that no run sees PreviousLost.
func runLateStart(t *testing.T, name string, block, until time.Duration, wantStarts []time.Duration) {
	t.Helper()

	c := newScripted("late", nil)
	sink := &recordingSink{hook: func(_ context.Context, n int) error {
		if n == 0 {
			time.Sleep(block)
		}
		return nil
	}}
	h := start(t, []scheduler.Job{job(c, 10*time.Second, 2*time.Second)}, sink)

	h.sleepTo(until)

	runs := c.started()
	if got := h.offsets(runs); !slices.Equal(got, wantStarts) {
		t.Fatalf("run starts after %s = %v, want %v", name, got, wantStarts)
	}
	for i, r := range runs {
		if r.PreviousLost {
			t.Errorf("Run.PreviousLost of run %d after %s = true, want false: a late start loses no Collect result", i, name)
		}
	}
}

func TestRunner_LateStartDoesNotSetPreviousLost(t *testing.T) {
	s := time.Second

	t.Run("a start inside the next slot", func(t *testing.T) {
		synctest.Test(t, func(t *testing.T) { runLateStart(t, "a start inside the next slot", 15*s, 22*s, seconds(0, 15, 20)) })
	})
	t.Run("a whole-slot gap recorded in startCycle", func(t *testing.T) {
		synctest.Test(t, func(t *testing.T) {
			runLateStart(t, "a whole-slot gap recorded in startCycle", 30*s, 42*s, seconds(0, 30, 40))
		})
	})
}

// keptCall is one call of Kept.
type keptCall struct {
	start time.Time
	kept  bool
	puts  int32 // the calls of Put the sink had received when Kept was called
}

// observedCollector is a scripted collector that also implements scheduler.Observer and records every call of Kept.
type observedCollector struct {
	*scriptedCollector
	sink  *recordingSink
	mu    sync.Mutex
	calls []keptCall
}

func (c *observedCollector) Kept(run scheduler.Run, kept bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.calls = append(c.calls, keptCall{start: run.Start, kept: kept, puts: c.sink.calls.Load()})
}

func (c *observedCollector) keptCalls() []keptCall {
	c.mu.Lock()
	defer c.mu.Unlock()
	return slices.Clone(c.calls)
}

// observedCase is one case of TestRunner_TellsTheObserverWhetherARunWasKept.
type observedCase struct {
	name      string
	first     func() ([]model.Record, error) // what the first run does; every later run returns one clean record
	sinkFails bool                           // the first Put returns an error
	until     time.Duration
	// want maps the number of a run (in the order of the starts) to the flag of its call of Kept; a run that is not in it gets no call.
	want map[int]bool
	// putsBefore is true when the run 0 must have been delivered to the sink before Kept was called.
	putsBefore bool
}

func observedCases() []observedCase {
	s := time.Second
	clean := func() ([]model.Record, error) { return []model.Record{metricRecord("observed", 1)}, nil }
	refused := func() ([]model.Record, error) {
		nan := model.Record{Meta: model.Meta{Source: "observed"}, Data: &model.MetricPoint{Name: "sample", Value: math.NaN()}}
		return []model.Record{metricRecord("observed", 1), nan}, nil
	}
	slow := func(d time.Duration) func() ([]model.Record, error) {
		return func() ([]model.Record, error) {
			time.Sleep(d) // ignores its context
			return clean()
		}
	}
	kept := map[int]bool{0: true, 1: true, 2: true}
	dropped := map[int]bool{0: false, 1: true, 2: true}
	return []observedCase{
		{name: "a clean run", first: clean, until: 25 * s, want: kept, putsBefore: true},
		{name: "a refused record", first: refused, until: 25 * s, want: dropped},
		{name: "a sink error", first: clean, sinkFails: true, until: 25 * s, want: dropped, putsBefore: true},
		{name: "an error", first: func() ([]model.Record, error) { return nil, errors.New("disk on fire") }, until: 25 * s, want: dropped},
		{name: "a panic", first: func() ([]model.Record, error) { panic("boom") }, until: 25 * s, want: dropped},
		{name: "a timeout", first: slow(5 * s), until: 25 * s, want: map[int]bool{1: true, 2: true}},
		{name: "skipped runs", first: slow(25 * s), until: 45 * s, want: map[int]bool{1: true, 2: true}},
	}
}

func TestRunner_TellsTheObserverWhetherARunWasKept(t *testing.T) {
	for _, tc := range observedCases() {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) { runObserved(t, tc) })
		})
	}
}

// runObserved runs an observed collector as the case says and checks the calls of Kept.
func runObserved(t *testing.T, tc observedCase) {
	t.Helper()
	sink := &recordingSink{}
	if tc.sinkFails {
		sink.hook = func(_ context.Context, n int) error {
			if n == 0 {
				return errors.New("spool full")
			}
			return nil
		}
	}
	c := &observedCollector{sink: sink}
	c.scriptedCollector = newScripted("observed", func(_ context.Context, n int, _ scheduler.Run) ([]model.Record, error) {
		if n == 0 {
			return tc.first()
		}
		return []model.Record{metricRecord("observed", 1)}, nil
	})
	h := start(t, []scheduler.Job{job(c, 10*time.Second, 2*time.Second)}, sink)

	h.sleepTo(tc.until)

	got := keptPerRun(t, h, c, tc.putsBefore)
	if !maps.Equal(got, tc.want) {
		t.Errorf("calls of Kept per run = %v, want %v after %s (true: kept, false: dropped; no entry: a run that was abandoned or skipped)", got, tc.want, tc.name)
	}
}

// keptPerRun maps the number of each run to the flag of its call of Kept and reports calls that belong to no run, twice-called runs and,
// when putsBefore is set, a call of the run 0 that came before the sink received its records.
func keptPerRun(t *testing.T, h *harness, c *observedCollector, putsBefore bool) map[int]bool {
	t.Helper()
	runs := c.started()
	got := map[int]bool{}
	for _, call := range c.keptCalls() {
		i := slices.IndexFunc(runs, func(r scheduler.Run) bool { return r.Start.Equal(call.start) })
		if i < 0 {
			t.Errorf("Kept was called with the start %v, which no run had (run starts %v)", call.start.Sub(h.base), h.offsets(runs))
			continue
		}
		if _, twice := got[i]; twice {
			t.Errorf("Kept was called twice for run %d", i)
		}
		got[i] = call.kept
		if putsBefore && i == 0 && call.puts == 0 {
			t.Error("Kept of run 0 was called before the sink received its records, want the call after the delivery")
		}
	}
	return got
}
