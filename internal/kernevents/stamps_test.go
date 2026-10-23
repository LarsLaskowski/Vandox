package kernevents

import (
	"math"
	"slices"
	"strconv"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// requireMovedOn checks that the next run returns nothing and that the committed position is past the entry with seq.
func requireMovedOn(t *testing.T, e *env, seq uint64) {
	t.Helper()
	if recs := e.run(false); len(recs) != 0 {
		t.Errorf("the run after a kept result returned %d records, want none (the span is not returned again)", len(recs))
	}
	if got := e.store.State().Journal; got == nil || got.Seqnum != seq {
		t.Errorf("committed position = %+v, want sequence number %d (the position moved on)", got, seq)
	}
}

// stampJournal puts a line, the entry bad, a second line and a control kill into the journal and returns the lines' times.
func stampJournal(e *env, c *seqCounter, bad func(rt, mono uint64) entry) (l1, l3, control entry) {
	l1 = c.line(bootB, 1)
	rt2, mono2 := tl(2)
	badEntry := bad(rt2, mono2)
	l3 = c.line(bootB, 3)
	control = c.kill(bootB, 4, 900, "control", lagUS)
	e.add(l1, badEntry, l3, control)
	e.seed(dashed(bootB), before())
	return l1, l3, control
}

// monotonicVariants are the monotonic stamps that make an entry unreadable.
func monotonicVariants() map[string][]kopt {
	const maxMicros = math.MaxInt64 / 1000
	out := map[string][]kopt{
		"entry stamp above the limit":       {withMonotonic(maxMicros + 1)},
		"entry stamp of the largest uint64": {withMonotonic(math.MaxUint64)},
	}
	for _, txt := range []string{"", "100000000000000000000", "18446744073709551616", "18446744073709551615", "9223372036854776", "+5", "-5", " 5", "5 ", "5\n", "1e3", "0x5", "５"} {
		out["source stamp "+strconv.Quote(txt)] = []kopt{withSource(txt)}
	}
	return out
}

func TestKernel_MonotonicStampOutOfRangeIsUnreadable(t *testing.T) {
	for name, opts := range monotonicVariants() {
		t.Run(name, func(t *testing.T) {
			e := newEnv(t)
			var c seqCounter
			l1, l3, control := stampJournal(e, &c, func(rt, mono uint64) entry {
				return kernelLine(c.next(), bootB, rt, mono, mono-lagUS, killMsg(800, "bad", 1, 0), opts...)
			})

			recs := e.run(false)

			requireOnlyControlKill(t, recs)
			gaps := gapsOf(recs)
			if len(gaps) != 1 {
				t.Fatalf("gaps = %d, want 1", len(gaps))
			}
			requireGap(t, gaps[0], micro(rtOf(l1)), micro(rtOf(l3)))
			checkValid(t, recs, t0)
			requireMovedOn(t, e, seqOf(control))
		})
	}
	t.Run("boundary in both fields is accepted", monotonicBoundaryAccepted)
	t.Run("source stamp after the entry stamp gives no delay", func(t *testing.T) {
		e := newEnv(t)
		var c seqCounter
		rt, mono := tl(2)
		e.add(kernelLine(c.next(), bootB, rt, mono, mono+5_000_000, killMsg(802, "ahead", 1, 0)))
		e.seed(dashed(bootB), before())

		kills := killsOf(e.run(false))

		if len(kills) != 1 || !kills[0].rec.CapturedAt.Equal(micro(rt)) {
			t.Fatalf("kills = %d, want 1 captured at the receipt time %s (a negative lag is no delay)", len(kills), micro(rt))
		}
	})
}

// requireOnlyControlKill checks that the control kill 900 is the one event and that no kernel_time_ns is negative.
func requireOnlyControlKill(t *testing.T, recs []model.Record) {
	t.Helper()
	kills := killsOf(recs)
	if got := pids(kills); !slices.Equal(got, []int32{900}) {
		t.Errorf("kills = %v, want [900] (an entry with an unreadable stamp is no event)", got)
	}
	for _, k := range kills {
		if k.ev.OOMKill.KernelTime != nil && *k.ev.OOMKill.KernelTime < 0 {
			t.Errorf("kernel_time_ns = %d, want it never negative", *k.ev.OOMKill.KernelTime)
		}
	}
}

func monotonicBoundaryAccepted(t *testing.T) {
	const maxMicros = math.MaxInt64 / 1000
	e := newEnv(t)
	var c seqCounter
	rt, _ := tl(2)
	e.add(c.line(bootB, 1), kernelLine(c.next(), bootB, rt, maxMicros, maxMicros, killMsg(801, "edge", 1, 0)))
	e.seed(dashed(bootB), before())

	recs := e.run(false)

	kills := killsOf(recs)
	if len(kills) != 1 || len(gapsOf(recs)) != 0 {
		t.Fatalf("boundary stamps gave %d kills and %d gaps, want 1 kill and no gap", len(kills), len(gapsOf(recs)))
	}
	if got, want := identityOf(t, kills[0]).KernelTime, time.Duration(maxMicros)*time.Microsecond; got != want {
		t.Errorf("kernel_time_ns = %d, want %d", got, want)
	}
	if !kills[0].rec.CapturedAt.Equal(micro(rt)) {
		t.Errorf("captured_at = %s, want the receipt time %s (no delay)", kills[0].rec.CapturedAt, micro(rt))
	}
	checkValid(t, recs, t0)
}

var badRealtimes = []uint64{0, 253402300800000000, 1 << 62, math.MaxUint64}

func TestKernel_RealtimeOutOfRangeIsUnreadable(t *testing.T) {
	for _, bad := range badRealtimes {
		name := strconv.FormatUint(bad, 10)
		t.Run("kill entry "+name, func(t *testing.T) { badRealtimeKill(t, bad) })
		t.Run("neighbour of a missed-messages entry "+name, func(t *testing.T) { badRealtimeNeighbour(t, bad) })
		t.Run("end of a given-up span "+name, func(t *testing.T) { badRealtimeEndsSpan(t, bad) })
	}
	t.Run("largest accepted real time", func(t *testing.T) {
		const last = 253402300799999999
		e := newEnv(t)
		var c seqCounter
		_, mono := tl(2)
		e.add(kernelLine(c.next(), bootB, last, mono, mono-lagUS, killMsg(803, "far", 1, 0)))
		e.seed(dashed(bootB), before())

		recs := e.run(false)

		kills := killsOf(recs)
		if len(kills) != 1 || len(gapsOf(recs)) != 0 {
			t.Fatalf("kills = %d, gaps = %d, want 1 kill and no gap", len(kills), len(gapsOf(recs)))
		}
		if kills[0].rec.CapturedAt.After(micro(last)) {
			t.Errorf("captured_at = %s, after the receipt time", kills[0].rec.CapturedAt)
		}
		checkValid(t, recs, t0)
	})
}

func badRealtimeKill(t *testing.T, bad uint64) {
	e := newEnv(t)
	var c seqCounter
	l1, l3, control := stampJournal(e, &c, func(_, mono uint64) entry {
		return kernelLine(c.next(), bootB, bad, mono, mono-lagUS, killMsg(800, "bad", 1, 0))
	})

	recs := e.run(false)

	requireOnlyControlKill(t, recs)
	gaps := gapsOf(recs)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(l1)), micro(rtOf(l3)))
	checkValid(t, recs, t0)
	requireMovedOn(t, e, seqOf(control))
}

func badRealtimeNeighbour(t *testing.T, bad uint64) {
	e := newEnv(t)
	var c seqCounter
	l1 := c.line(bootB, 1)
	_, mono2 := tl(2)
	badLine := kernelLine(c.next(), bootB, bad, mono2, mono2-lagUS, "handmade kernel line with a bad time")
	m := missedLine(c.next(), bootB, rtAt(3), monoAt(3))
	l4 := c.line(bootB, 4)
	control := c.kill(bootB, 5, 900, "control", lagUS)
	e.add(l1, badLine, m, l4, control)
	e.seed(dashed(bootB), before())

	recs := e.run(false)

	gaps := gapsOf(recs)
	if len(gaps) == 0 {
		t.Fatalf("gaps = 0, want a gap for the unreadable entry and the missed messages")
	}
	for _, g := range gaps {
		requireGap(t, g, micro(rtOf(l1)), micro(rtOf(l4)))
	}
	requireOnlyControlKill(t, recs)
	checkValid(t, recs, t0)
	requireMovedOn(t, e, seqOf(control))
}

func badRealtimeEndsSpan(t *testing.T, bad uint64) {
	e := newEnv(t)
	var c seqCounter
	pos := c.line(bootB, 1)
	k1 := c.kill(bootB, 3, 111, "k1", lagUS)
	_, mono4 := tl(4)
	badLine := kernelLine(c.next(), bootB, bad, mono4, mono4-lagUS, "handmade kernel line with a bad time")
	e.add(pos, k1, badLine)
	e.seed(dashed(bootB), tailOf(seqIDA, []entry{pos}))
	for i := range 4 {
		e.run(i > 0)
	}

	recs := e.run(true)

	var found bool
	for _, g := range gapsOf(recs) {
		found = found || (g.gap.From.Equal(micro(rtOf(pos))) && g.gap.To.Equal(micro(rtOf(k1))))
		if g.gap.To.Year() >= 9999 || g.gap.From.Year() >= 9999 || g.gap.From.Year() < 2000 {
			t.Errorf("gap [%s, %s) takes a bound from an unreadable time", g.gap.From, g.gap.To)
		}
	}
	if !found {
		t.Errorf("gaps = %+v, want one from the committed position %s to the last readable entry %s", gapsOf(recs), micro(rtOf(pos)), micro(rtOf(k1)))
	}
	if got := pids(killsOf(recs)); slices.Contains(got, 111) {
		t.Errorf("kills = %v, want the kills of the given-up span left out", got)
	}
	checkValid(t, recs, t0)
}
