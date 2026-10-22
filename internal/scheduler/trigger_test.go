package scheduler_test

import (
	"context"
	"errors"
	"math"
	"slices"
	"testing"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// killOf returns the i-th kill of a test, distinct from every other.
func killOf(i int) model.KillRef {
	pid := int32(1000)
	for range i {
		pid++
	}
	return model.KillRef{
		BootID:     "0f1e2d3c-4b5a-4968-8776-655443322110",
		KernelTime: time.Duration(i+1) * time.Second,
		VictimPID:  pid,
	}
}

// killsOf returns the kills first to first+n-1.
func killsOf(first, n int) []model.KillRef {
	out := make([]model.KillRef, n)
	for i := range out {
		out[i] = killOf(first + i)
	}
	return out
}

// triggersOf returns the trigger of the extra runs in runs, in order.
func triggersOf(runs []scheduler.Run) []*scheduler.Trigger {
	out := make([]*scheduler.Trigger, 0, len(runs))
	for _, r := range runs {
		if r.Trigger != nil {
			out = append(out, r.Trigger)
		}
	}
	return out
}

// requireAccepted calls Trigger and checks that the request was accepted.
func requireAccepted(t *testing.T, s *scheduler.Scheduler, collector string, tr scheduler.Trigger) {
	t.Helper()

	if !s.Trigger(collector, tr) {
		t.Errorf("Trigger(%q, %d kills) = false, want true (the collector is known)", collector, len(tr.Kills))
	}
}

func TestScheduler_TriggerRunsExtraWithinOneSecond(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		c := newScripted("kern", nil)
		other := newScripted("other", nil)
		h := start(t, []scheduler.Job{job(c, 10*time.Second, 2*time.Second), job(other, 10*time.Second, 2*time.Second)}, &recordingSink{})
		h.sleepTo(3 * time.Second)

		requireAccepted(t, h.sched, "kern", scheduler.Trigger{Kills: killsOf(0, 2), KillsOmitted: 0})
		h.sleepTo(4 * time.Second)

		runs := c.started()
		if got := h.offsets(runs); len(got) != 2 || got[1] < 3*time.Second || got[1] > 4*time.Second {
			t.Fatalf("run starts of the triggered collector = %v, want [0s, a start within 1 s of the trigger at 3s]", got)
		}
		if runs[0].Trigger != nil {
			t.Errorf("Run.Trigger of the regular run = %+v, want nil", runs[0].Trigger)
		}
		got := runs[1].Trigger
		if got == nil || !slices.Equal(got.Kills, killsOf(0, 2)) || got.KillsOmitted != 0 {
			t.Errorf("Run.Trigger of the extra run = %+v, want the 2 kills of the request and none omitted", got)
		}
		if runs[1].Pressure || runs[1].PreviousLost {
			t.Errorf("extra run Pressure, PreviousLost = %v, %v, want false, false", runs[1].Pressure, runs[1].PreviousLost)
		}
		if n := len(other.started()); n != 1 {
			t.Errorf("runs of the other collector = %d, want 1 (a trigger names one collector)", n)
		}
		if n := len(h.sink.fromSource("kern")); n != 2 {
			t.Errorf("puts of samples = %d, want 2 (the extra run delivers its records)", n)
		}
		h.requireGaps("kern", nil)
	})
}

func TestScheduler_TriggerWaitsForCurrentRun(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		c := newScripted("kern", func(_ context.Context, n int, _ scheduler.Run) ([]model.Record, error) {
			if n == 0 {
				time.Sleep(4 * time.Second)
			}
			return []model.Record{metricRecord("kern", float64(n))}, nil
		})
		h := start(t, []scheduler.Job{job(c, 10*time.Second, 5*time.Second)}, &recordingSink{})
		h.sleepTo(time.Second)

		requireAccepted(t, h.sched, "kern", scheduler.Trigger{Kills: killsOf(0, 1)})
		h.sleepTo(3 * time.Second)

		if got := h.offsets(c.started()); !slices.Equal(got, seconds(0)) {
			t.Fatalf("run starts at 3s = %v, want [0s] (the extra run waits for the current run)", got)
		}

		h.sleepTo(12 * time.Second)

		runs := c.started()
		if got := h.offsets(runs); !slices.Equal(got, seconds(0, 4, 10)) {
			t.Fatalf("run starts = %v, want [0s 4s 10s] (the extra run starts when the current run ends)", got)
		}
		if triggers := triggersOf(runs); len(triggers) != 1 || !slices.Equal(triggers[0].Kills, killsOf(0, 1)) || runs[1].Trigger == nil {
			t.Errorf("triggers of the runs = %+v, want only the run at 4s to carry the 1 kill", triggers)
		}
		h.requireGaps("kern", nil)
	})
}

func TestScheduler_TriggerKeepsGridWithoutPressure(t *testing.T) {
	tests := []struct {
		name string
		job  func(c scheduler.Collector) scheduler.Job
	}{
		{"plain job", func(c scheduler.Collector) scheduler.Job { return job(c, 10*time.Second, 2*time.Second) }},
		{"job with a pressure interval, mode off", func(c scheduler.Collector) scheduler.Job {
			return scheduler.Job{Collector: c, Schedule: scheduler.Schedule{
				Interval: 10 * time.Second, Timeout: 2 * time.Second, PressureInterval: 5 * time.Second,
			}}
		}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				c := newScripted("kern", nil)
				h := start(t, []scheduler.Job{tc.job(c)}, &recordingSink{})
				h.sleepTo(3 * time.Second)
				requireAccepted(t, h.sched, "kern", scheduler.Trigger{Kills: killsOf(0, 1)})

				h.sleepTo(25 * time.Second)

				runs := c.started()
				if got := h.offsets(runs); !slices.Equal(got, seconds(0, 3, 10, 20)) {
					t.Errorf("run starts = %v, want [0s 3s 10s 20s] (the grid stays)", got)
				}
				if got := pressures(runs); !slices.Equal(got, []bool{false, false, false, false}) {
					t.Errorf("Run.Pressure = %v, want all false", got)
				}
				h.requireGaps("kern", nil)
			})
		})
	}
}

func TestScheduler_TriggerReplacesNextRunUnderPressure(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		c := newScripted("kern", nil)
		h := start(t, []scheduler.Job{pressureJob(c)}, &recordingSink{})
		h.sched.SetPressure(true)
		h.sleepTo(13 * time.Second)
		requireAccepted(t, h.sched, "kern", scheduler.Trigger{Kills: killsOf(0, 1)})

		h.sleepTo(35 * time.Second)

		runs := c.started()
		if got := h.offsets(runs); !slices.Equal(got, seconds(0, 10, 13, 23, 33)) {
			t.Errorf("run starts = %v, want [0s 10s 13s 23s 33s] (the extra run replaces the run due at 20s)", got)
		}
		if got := pressures(runs); !slices.Equal(got, []bool{true, true, true, true, true}) {
			t.Errorf("Run.Pressure = %v, want all true", got)
		}
		if runs[2].Trigger == nil {
			t.Errorf("Run.Trigger of the run at 13s = nil, want the extra run's trigger")
		}
		h.requireGaps("kern", nil)
	})
}

// mergeCase is one case of TestScheduler_TriggerMergesKillsUpToSixtyFour: requests made while the first run is busy.
type mergeCase struct {
	name        string
	requests    []scheduler.Trigger
	wantKills   []model.KillRef
	wantOmitted uint32
}

func mergeCases() []mergeCase {
	return []mergeCase{
		{
			name:      "one request",
			requests:  []scheduler.Trigger{{Kills: killsOf(0, 1)}},
			wantKills: killsOf(0, 1),
		},
		{
			name:      "two requests in order",
			requests:  []scheduler.Trigger{{Kills: killsOf(0, 2)}, {Kills: killsOf(2, 3)}},
			wantKills: killsOf(0, 5),
		},
		{
			name:      "exactly 64 kills",
			requests:  []scheduler.Trigger{{Kills: killsOf(0, 40)}, {Kills: killsOf(40, 24)}},
			wantKills: killsOf(0, 64),
		},
		{
			name:        "65 kills: the first 64 are listed, one is counted",
			requests:    []scheduler.Trigger{{Kills: killsOf(0, 40)}, {Kills: killsOf(40, 25)}},
			wantKills:   killsOf(0, 64),
			wantOmitted: 1,
		},
		{
			name:        "70 kills and 3 already omitted by the caller",
			requests:    []scheduler.Trigger{{Kills: killsOf(0, 40)}, {Kills: killsOf(40, 30), KillsOmitted: 3}},
			wantKills:   killsOf(0, 64),
			wantOmitted: 9,
		},
		{
			name:        "omitted of a request is added",
			requests:    []scheduler.Trigger{{Kills: killsOf(0, 64), KillsOmitted: 5}, {Kills: killsOf(64, 1), KillsOmitted: 2}},
			wantKills:   killsOf(0, 64),
			wantOmitted: 8,
		},
	}
}

// mergedRuns makes the requests of tc while the first run of a collector is busy and returns the runs after 10 s.
func mergedRuns(t *testing.T, h *harness, c *scriptedCollector, requests []scheduler.Trigger) []scheduler.Run {
	t.Helper()

	for i, req := range requests {
		h.sleepTo(time.Duration(i+1) * 500 * time.Millisecond)
		requireAccepted(t, h.sched, "kern", req)
	}
	h.sleepTo(10 * time.Second)
	return c.started()
}

// requireMerged checks the one extra run that the requests of tc merged into.
func requireMerged(t *testing.T, h *harness, runs []scheduler.Run, tc mergeCase) {
	t.Helper()

	if got := h.offsets(runs); !slices.Equal(got, seconds(0, 3)) {
		t.Fatalf("run starts = %v, want [0s 3s] (the requests merge into one extra run)", got)
	}
	got := runs[1].Trigger
	if got == nil {
		t.Fatalf("Run.Trigger of the extra run = nil, want the merged request")
	}
	if !slices.Equal(got.Kills, tc.wantKills) {
		t.Errorf("merged kills = %d entries %+v, want %d entries %+v", len(got.Kills), got.Kills, len(tc.wantKills), tc.wantKills)
	}
	if got.KillsOmitted != tc.wantOmitted {
		t.Errorf("merged KillsOmitted = %d, want %d", got.KillsOmitted, tc.wantOmitted)
	}
}

func TestScheduler_TriggerMergesKillsUpToSixtyFour(t *testing.T) {
	cases := append(mergeCases(), mergeCase{
		name:        "omitted counts saturate at the largest uint32",
		requests:    []scheduler.Trigger{{Kills: killsOf(0, 1), KillsOmitted: math.MaxUint32}, {Kills: killsOf(1, 1), KillsOmitted: 5}},
		wantKills:   killsOf(0, 2),
		wantOmitted: math.MaxUint32,
	})
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				c := newScripted("kern", func(_ context.Context, n int, _ scheduler.Run) ([]model.Record, error) {
					if n == 0 {
						time.Sleep(3 * time.Second)
					}
					return nil, nil
				})
				h := start(t, []scheduler.Job{job(c, 20*time.Second, 5*time.Second)}, &recordingSink{})

				runs := mergedRuns(t, h, c, tc.requests)

				requireMerged(t, h, runs, tc)
			})
		})
	}
}

func TestScheduler_TriggerUnknownCollectorIsRefused(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		c := newScripted("kern", nil)
		h := start(t, []scheduler.Job{job(c, 10*time.Second, 2*time.Second)}, &recordingSink{})
		h.sleepTo(3 * time.Second)

		if h.sched.Trigger("nobody", scheduler.Trigger{Kills: killsOf(0, 1)}) {
			t.Errorf(`Trigger("nobody") = true, want false (unknown collector)`)
		}
		h.sleepTo(5 * time.Second)

		if got := h.offsets(c.started()); !slices.Equal(got, seconds(0)) {
			t.Errorf("run starts = %v, want [0s] (a refused request starts no run)", got)
		}
	})
}

func TestScheduler_TriggerAfterExtraRunStartsNewExtraRun(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		c := newScripted("kern", nil)
		h := start(t, []scheduler.Job{job(c, 20*time.Second, 5*time.Second)}, &recordingSink{})
		h.sleepTo(2 * time.Second)
		requireAccepted(t, h.sched, "kern", scheduler.Trigger{Kills: killsOf(0, 1)})
		h.sleepTo(4 * time.Second)
		requireAccepted(t, h.sched, "kern", scheduler.Trigger{Kills: killsOf(1, 1)})

		h.sleepTo(6 * time.Second)

		triggers := triggersOf(c.started())
		if len(triggers) != 2 || !slices.Equal(triggers[0].Kills, killsOf(0, 1)) || !slices.Equal(triggers[1].Kills, killsOf(1, 1)) {
			t.Errorf("triggers of the runs = %+v, want one extra run per request, the kills not repeated", triggers)
		}
	})
}

func TestScheduler_FailedExtraRunRecordsGap(t *testing.T) {
	tests := []struct {
		name   string
		result func() ([]model.Record, error)
	}{
		{"error", func() ([]model.Record, error) { return nil, errors.New("disk on fire") }},
		{"panic", func() ([]model.Record, error) { panic("boom") }},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				c := newScripted("kern", func(_ context.Context, _ int, run scheduler.Run) ([]model.Record, error) {
					if run.Trigger != nil {
						return tc.result()
					}
					return []model.Record{metricRecord("kern", 1)}, nil
				})
				h := start(t, []scheduler.Job{job(c, 10*time.Second, 2*time.Second)}, &recordingSink{})
				h.sleepTo(3 * time.Second)
				requireAccepted(t, h.sched, "kern", scheduler.Trigger{Kills: killsOf(0, 1)})

				h.sleepTo(12 * time.Second)

				s := time.Second
				h.requireGaps("kern", []wantGap{
					{at: 3 * s, captured: 3 * s, from: 3 * s, to: 13 * s, cause: model.GapUnknown},
				})
				runs := c.started()
				if got := h.offsets(runs); !slices.Equal(got, seconds(0, 3, 10)) {
					t.Fatalf("run starts = %v, want [0s 3s 10s] (the grid stays after a failed extra run)", got)
				}
				if !runs[2].PreviousLost {
					t.Errorf("PreviousLost of the run after the failed extra run = false, want true (the extra run is its predecessor)")
				}
			})
		})
	}
}

func TestScheduler_ExtraRunAfterLostRunHasPreviousLost(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		c := newScripted("kern", func(_ context.Context, n int, _ scheduler.Run) ([]model.Record, error) {
			if n == 0 {
				return nil, errors.New("first run fails")
			}
			return nil, nil
		})
		h := start(t, []scheduler.Job{job(c, 10*time.Second, 2*time.Second)}, &recordingSink{})
		h.sleepTo(3 * time.Second)
		requireAccepted(t, h.sched, "kern", scheduler.Trigger{Kills: killsOf(0, 1)})

		h.sleepTo(12 * time.Second)

		runs := c.started()
		if len(runs) != 3 || !runs[1].PreviousLost || runs[2].PreviousLost {
			t.Errorf("PreviousLost of the runs = %v, want [false true false] (the extra run is the next run's predecessor)",
				previousLost(runs))
		}
	})
}

// previousLost returns Run.PreviousLost of every run.
func previousLost(runs []scheduler.Run) []bool {
	out := make([]bool, len(runs))
	for i, r := range runs {
		out[i] = r.PreviousLost
	}
	return out
}

func TestScheduler_AbandonedExtraRunRecordsGapAndSkips(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		release := make(chan struct{})
		t.Cleanup(func() { close(release) })
		c := newScripted("kern", func(_ context.Context, _ int, run scheduler.Run) ([]model.Record, error) {
			if run.Trigger != nil {
				<-release
			}
			return nil, nil
		})
		h := start(t, []scheduler.Job{job(c, 10*time.Second, 2*time.Second)}, &recordingSink{})
		h.sleepTo(3 * time.Second)
		requireAccepted(t, h.sched, "kern", scheduler.Trigger{Kills: killsOf(0, 1)})

		h.sleepTo(25 * time.Second)

		s := time.Second
		h.requireGaps("kern", []wantGap{
			{at: 5 * s, captured: 5 * s, from: 3 * s, to: 13 * s, cause: model.GapCollectorTimeout},
			{at: 10 * s, captured: 10 * s, from: 10 * s, to: 20 * s, cause: model.GapCollectorTimeout},
			{at: 20 * s, captured: 20 * s, from: 20 * s, to: 30 * s, cause: model.GapCollectorTimeout},
		})
		if got := h.offsets(c.started()); !slices.Equal(got, seconds(0, 3)) {
			t.Errorf("run starts = %v, want [0s 3s] (regular runs are skipped while the abandoned extra run hangs)", got)
		}
	})
}

func TestScheduler_TriggerWhileStuckRecordsGap(t *testing.T) {
	tests := []struct {
		name     string
		job      func(c scheduler.Collector) scheduler.Job
		pressure bool
		wantTo   time.Duration
	}{
		{"one interval without pressure", func(c scheduler.Collector) scheduler.Job { return job(c, 10*time.Second, 2*time.Second) }, false, 15 * time.Second},
		{"one pressure interval under pressure", func(c scheduler.Collector) scheduler.Job {
			return scheduler.Job{Collector: c, Schedule: scheduler.Schedule{
				Interval: 20 * time.Second, Timeout: 2 * time.Second, PressureInterval: 10 * time.Second,
			}}
		}, true, 15 * time.Second},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				hang := newHanging(t, "kern")
				h := start(t, []scheduler.Job{tc.job(hang)}, &recordingSink{})
				if tc.pressure {
					h.sched.SetPressure(true)
				}
				h.sleepTo(5 * time.Second)

				requireAccepted(t, h.sched, "kern", scheduler.Trigger{Kills: killsOf(0, 1)})
				h.sleepTo(7 * time.Second)

				s := time.Second
				h.requireGaps("kern", []wantGap{
					{at: 2 * s, captured: 2 * s, from: 0, to: 10 * s, cause: model.GapCollectorTimeout},
					{at: 5 * s, captured: 5 * s, from: 5 * s, to: tc.wantTo, cause: model.GapCollectorTimeout},
				})
				if got := hang.calls.Load(); got != 1 {
					t.Errorf("Collect calls = %d, want 1 (a kill while the collector is stuck starts no extra run)", got)
				}
			})
		})
	}
}
