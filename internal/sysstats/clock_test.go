package sysstats_test

import (
	"errors"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/sysstats"
)

// clockSource returns a clock source that answers state and err.
func clockSource(state sysstats.ClockState, err error) sysstats.Sources {
	return sysstats.Sources{Clock: func() (sysstats.ClockState, error) { return state, err }}
}

func TestClock_Records(t *testing.T) {
	synced := func(v, maxError, estimated float64) []wantRec {
		return []wantRec{
			rec("clock_synchronized", v, ""),
			rec("clock_max_error_seconds", maxError, "seconds"),
			rec("clock_estimated_error_seconds", estimated, "seconds"),
		}
	}
	cases := []struct {
		name  string
		state sysstats.ClockState
		want  []wantRec
	}{
		{
			"maximum error just below 16 seconds",
			sysstats.ClockState{MaxError: 15999999 * time.Microsecond, EstimatedError: 2 * time.Millisecond},
			synced(1, 15.999999, 0.002),
		},
		{
			"maximum error of 16 seconds", sysstats.ClockState{MaxError: 16 * time.Second, EstimatedError: 2 * time.Millisecond},
			synced(0, 16, 0.002),
		},
		{
			"phase-locked loop on with a negative offset",
			sysstats.ClockState{MaxError: 500 * time.Microsecond, EstimatedError: 20 * time.Microsecond, PLL: true, PLLOffset: -250 * time.Millisecond},
			append(synced(1, 0.0005, 0.00002), rec("clock_pll_offset_seconds", -0.25, "seconds")),
		},
		{
			"phase-locked loop on with a zero offset",
			sysstats.ClockState{MaxError: time.Second, PLL: true},
			append(synced(1, 1, 0), rec("clock_pll_offset_seconds", 0, "seconds")),
		},
		{
			"phase-locked loop off leaves the offset out",
			sysstats.ClockState{MaxError: time.Second, EstimatedError: time.Millisecond, PLLOffset: 5 * time.Millisecond},
			synced(1, 1, 0.001),
		},
	}
	for _, tt := range cases {
		t.Run(tt.name, func(t *testing.T) {
			c := sysstats.NewClock(clockSource(tt.state, nil))

			got := mustCollect(t, c, runStart)

			requireRecords(t, runStart, got, tt.want)
		})
	}
}

func TestClock_Errors(t *testing.T) {
	t.Run("an error of the source is returned unchanged", func(t *testing.T) {
		c := sysstats.NewClock(clockSource(sysstats.ClockState{MaxError: time.Second}, errBoom))

		got, err := collect(c, runStart)

		if !errors.Is(err, errBoom) || err.Error() != errBoom.Error() {
			t.Errorf("Collect() error = %v, want the error of the source unchanged (%v)", err, errBoom)
		}
		if len(got) != 0 {
			t.Errorf("Collect() returned %d records with its error, want none", len(got))
		}
	})

	negative := []struct {
		name  string
		state sysstats.ClockState
	}{
		{"negative maximum error", sysstats.ClockState{MaxError: -time.Nanosecond, EstimatedError: time.Second}},
		{"negative estimated error", sysstats.ClockState{MaxError: time.Second, EstimatedError: -time.Nanosecond}},
	}
	for _, tt := range negative {
		t.Run(tt.name, func(t *testing.T) {
			requireFails(t, sysstats.NewClock(clockSource(tt.state, nil)), runStart, "sysstats: adjtimex: negative error estimate")
		})
	}
}

func TestReadClock_ReadsTheKernel(t *testing.T) {
	got, err := sysstats.ReadClock()

	if err != nil {
		t.Fatalf("ReadClock() error = %v, want nil", err)
	}
	if got.MaxError < 0 || got.EstimatedError < 0 {
		t.Errorf("ReadClock() = %+v, want non-negative error estimates", got)
	}
}

func TestClock_HangingSourceIsAbandoned(t *testing.T) {
	hangingSample(t, func(_ *testing.T, g *gate) scheduler.Collector {
		return sysstats.NewClock(sysstats.Sources{Clock: func() (sysstats.ClockState, error) {
			g.wait()
			return sysstats.ClockState{}, nil
		}})
	})
}
