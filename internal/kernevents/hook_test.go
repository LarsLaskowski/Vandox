// These tests swap the package variable checkRecord (var checkRecord = wire.CheckRecord, added with the collector). The build tag keeps
// them out of the build until the variable exists; the Dev removes the tag line when adding it. They must not run in parallel.

package kernevents

import (
	"errors"
	"slices"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// swapCheckRecord installs a record check for the test and restores the real one afterwards.
func swapCheckRecord(t *testing.T, fn func(*model.Record) (int, error)) {
	t.Helper()
	old := checkRecord
	checkRecord = fn
	t.Cleanup(func() { checkRecord = old })
}

func TestKernel_RefusedRecordBecomesGap(t *testing.T) {
	var calls int
	swapCheckRecord(t, func(r *model.Record) (int, error) {
		calls++
		if ev, ok := r.Data.(*model.KernelEvent); ok && ev.OOMKill != nil && ev.OOMKill.VictimPID == 666 {
			return 0, errors.New("refused for the test")
		}
		return wire.CheckRecord(r)
	})
	e := newEnv(t)
	var c seqCounter
	l1 := c.line(bootB, 1)
	refused := c.kill(bootB, 2, 666, "refused", lagUS)
	l3 := c.line(bootB, 3)
	control := c.kill(bootB, 4, 900, "control", lagUS)
	e.add(l1, refused, l3, control)
	e.seed(dashed(bootB), before())

	recs := e.run(false)

	if calls == 0 {
		t.Fatalf("the swapped record check was never called")
	}
	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{900}) {
		t.Errorf("kills = %v, want [900] (the refused record is left out)", got)
	}
	gaps := gapsOf(recs)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1 in place of the refused record", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(l1)), micro(rtOf(refused)))
	requireMovedOn(t, e, seqOf(control))
}

func TestKernel_RealtimeOutOfRangeIsUnreadable_RefusedGapFallsBack(t *testing.T) {
	var start time.Time
	swapCheckRecord(t, func(r *model.Record) (int, error) {
		if g, ok := r.Data.(*model.Gap); ok && !g.To.Equal(start) {
			return 0, errors.New("refused for the test")
		}
		return wire.CheckRecord(r)
	})
	e := newEnv(t)
	var c seqCounter
	_, mono := tl(2)
	l1 := c.line(bootB, 1)
	bad := kernelLine(c.next(), bootB, 0, mono, mono-lagUS, "handmade kernel line with a bad time")
	control := c.kill(bootB, 4, 900, "control", lagUS)
	e.add(l1, bad, control)
	e.seed(dashed(bootB), before())
	start = e.start

	recs := e.run(false)

	gaps := gapsOf(recs)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1 (the refused gap replaced by the fallback)", len(gaps))
	}
	requireGap(t, gaps[0], start.Add(-time.Microsecond), start)
	checkValid(t, recs, start)
	requireMovedOn(t, e, seqOf(control))
}
