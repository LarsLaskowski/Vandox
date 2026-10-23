package kernevents

import (
	"context"
	"io/fs"
	"log/slog"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// liveEnv returns an environment in boot bootB whose first run (the first start, which reads from the end of the journal) has
// read the existing lines; the next run starts two seconds later and reads what the test adds.
func liveEnv(t *testing.T) (*env, *seqCounter) {
	t.Helper()
	e := newEnv(t)
	c := &seqCounter{}
	e.seed(dashed(bootB), nil)
	e.add(c.line(bootB, 1), c.line(bootB, 2), c.line(bootB, 3))
	e.uptimeNow(0)
	if recs := e.run(false); len(killsOf(recs)) != 0 || len(gapsOf(recs)) != 0 {
		t.Fatalf("the first start made %d kills and %d gaps from the journal's history, want none", len(killsOf(recs)), len(gapsOf(recs)))
	}
	return e, c
}

// requireTriggerPair checks that calls are one request for each of the process and the connection collector naming kills.
func requireTriggerPair(t *testing.T, calls []trigCall, kills []model.KillRef, omitted uint32) {
	t.Helper()
	if len(calls) != 2 {
		t.Fatalf("trigger calls = %d %+v, want 2 (processes and connections)", len(calls), calls)
	}
	got := []string{calls[0].collector, calls[1].collector}
	slices.Sort(got)
	if !slices.Equal(got, []string{"connections", "processes"}) {
		t.Errorf("trigger calls for %v, want connections and processes", got)
	}
	for _, c := range calls {
		if !slices.Equal(c.trigger.Kills, kills) || c.trigger.KillsOmitted != omitted {
			t.Errorf("trigger for %s = kills %+v omitted %d, want kills %+v omitted %d", c.collector, c.trigger.Kills, c.trigger.KillsOmitted, kills, omitted)
		}
	}
}

func TestKernel_LiveKillBecomesEventWithinFiveSeconds(t *testing.T) {
	e, c := liveEnv(t)
	killRT, _ := tl(6)
	lag := uint64(400_000)
	e.add(c.line(bootB, 5), c.kill(bootB, 6, 4711, "pzstd", lag))
	e.uptimeNow(0)
	runStart := e.start

	recs := e.run(false)

	if gap := runStart.Sub(micro(killRT)); gap > 5*time.Second || gap < 0 {
		t.Fatalf("test set-up: the run starts %s after the journal received the kill, want 0 to 5 s", gap)
	}
	kills := killsOf(recs)
	if len(kills) != 1 {
		t.Fatalf("the first run after the journal received the kill returned %d oom_kill events, want 1", len(kills))
	}
	o := kills[0].ev.OOMKill
	if o.VictimPID != 4711 || o.VictimCommand != "pzstd" {
		t.Errorf("event victim = %d %q, want 4711 %q", o.VictimPID, o.VictimCommand, "pzstd")
	}
	if o.AnonRSSBytes == nil || *o.AnonRSSBytes != 100*1024 || o.OOMScoreAdj == nil || *o.OOMScoreAdj != 0 {
		t.Errorf("event anon_rss_bytes = %v, oom_score_adj = %v, want %d and 0", o.AnonRSSBytes, o.OOMScoreAdj, 100*1024)
	}
	if want := killMsg(4711, "pzstd", 100, 0); kills[0].ev.Message != want {
		t.Errorf("event message = %q, want the kill line %q", kills[0].ev.Message, want)
	}
	if want := micro(killRT - 100_000); !kills[0].rec.CapturedAt.Equal(want) {
		t.Errorf("event captured_at = %s, want %s (receipt time minus the delay of 0.4 s less the 0.3 s clock drift)", kills[0].rec.CapturedAt, want)
	}
	if want := killIdentity(bootB, killSrc(6, lag), 4711); identityOf(t, kills[0]) != want {
		t.Errorf("event identity = %+v, want %+v", identityOf(t, kills[0]), want)
	}
	checkValid(t, recs, runStart)
}

func TestKernel_NoTriggerFuncIsNotAnError(t *testing.T) {
	e, c := liveEnv(t)
	e.k.SetTrigger(nil)
	e.add(c.line(bootB, 5), c.kill(bootB, 6, 4711, "pzstd", lagUS))
	e.uptimeNow(0)

	recs := e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{4711}) {
		t.Errorf("kills without a trigger function = %v, want [4711]", got)
	}
}

func TestKernel_CaptureTimeCorrectsKernelClockDrift(t *testing.T) {
	t.Run("fixture with 20 days of uptime and 40 s of drift", captureFixtureDrift)
	t.Run("lines of an earlier run give the drift", captureEarlierRunDrift)
	t.Run("fixture read from the start of the boot", captureFixtureFromBootStart)
	t.Run("a line more than 600 s old is ignored", func(t *testing.T) { captureOldLine(t, 700, 0) })
	t.Run("a line 599 s old counts", func(t *testing.T) { captureOldLine(t, 600, 1_000_000) })
	t.Run("a re-read gives the same capture time", captureReread)
	t.Run("a source stamp above the receipt stamp counts as no lag", captureSourceAhead)
	for _, tc := range noDelayCases {
		t.Run(tc.name, func(t *testing.T) { captureWithoutDelay(t, tc) })
	}
}

func captureFixtureDrift(t *testing.T) {
	e := newEnv(t)
	e.j.loadFixture(t)
	e.seed(dashed(bootB), position(seqIDA, 21, 1790790396787497))

	recs := e.run(false)

	kills := killsOf(recs)
	wantPID := []int32{352448, 351416, 5150}
	if got := pids(kills); !slices.Equal(got, wantPID) {
		t.Fatalf("kills = %v, want %v", got, wantPID)
	}
	// capture time and receipt time of each kill: the receipt minus the delay (the lag less the smallest lag of the lines read before)
	wantRT := []uint64{1790916696102919, 1790917172365977, 1792657799750000}
	receipt := []uint64{1790916696593383, 1790917172776852, 1792657800000000}
	for i, k := range kills {
		if want := micro(wantRT[i]); !k.rec.CapturedAt.Equal(want) {
			t.Errorf("kill %d captured_at = %s, want %s", wantPID[i], k.rec.CapturedAt.Format(time.RFC3339Nano), want.Format(time.RFC3339Nano))
		}
		if k.rec.CapturedAt.After(micro(receipt[i])) {
			t.Errorf("kill %d captured_at = %s, after the receipt time %s", wantPID[i], k.rec.CapturedAt, micro(receipt[i]))
		}
	}
	checkValid(t, recs, t0)
}

func captureFixtureFromBootStart(t *testing.T) {
	e := newEnv(t)
	e.j.loadFixture(t)
	e.seed(dashed(bootB), position(seqIDA, 0, 1790000000000000))

	kills := killsOf(e.run(false))

	want := map[int32]uint64{
		3021:   1790790382989841,
		352448: 1790916696593383 - 490464, // the delay of the pzstd kill is 0.490464 s, not that of the lines of the boot's start
		351416: 1790917172776852 - 410875, // 0.410875 s
		5150:   1792657799750000,
	}
	if len(kills) != 4 {
		t.Fatalf("kills = %v, want the 4 kills of the fixture", pids(kills))
	}
	for _, k := range kills {
		pid := k.ev.OOMKill.VictimPID
		if !k.rec.CapturedAt.Equal(micro(want[pid])) {
			t.Errorf("kill %d captured_at = %s, want %s", pid, k.rec.CapturedAt.Format(time.RFC3339Nano), micro(want[pid]).Format(time.RFC3339Nano))
		}
	}
}

// captureOldLine puts a line at second 1 with a lag of 0.3 s and a kill at second sec with a lag of 1.3 s; extra is the delay wanted.
func captureOldLine(t *testing.T, sec int, extra uint64) {
	e, c := liveEnv(t)
	killRT, _ := tl(sec)
	e.add(c.line(bootB, 1), c.kill(bootB, sec, 4711, "p", lagUS+1_000_000))
	e.uptimeNow(0)

	kills := killsOf(e.run(false))

	if len(kills) != 1 {
		t.Fatalf("kills = %d, want 1", len(kills))
	}
	if want := micro(killRT - extra); !kills[0].rec.CapturedAt.Equal(want) {
		t.Errorf("captured_at = %s, want %s (delay %d µs)", kills[0].rec.CapturedAt, want, extra)
	}
}

func captureReread(t *testing.T) {
	e, c := liveEnv(t)
	killRT, _ := tl(6)
	e.add(c.line(bootB, 5), c.kill(bootB, 6, 4711, "p", lagUS+1_000_000))
	e.uptimeNow(0)

	first := killsOf(e.run(false))
	again := killsOf(e.run(true))

	want := micro(killRT - 1_000_000)
	if len(first) != 1 || len(again) != 1 || !first[0].rec.CapturedAt.Equal(want) || !again[0].rec.CapturedAt.Equal(want) {
		t.Errorf("captured_at on the first reading and the re-read = %v and %v, want both %s", first, again, want)
	}
}

func captureSourceAhead(t *testing.T) {
	e, c := liveEnv(t)
	rt5, mono5 := tl(5)
	killRT, _ := tl(6)
	ahead := kernelLine(c.next(), bootB, rt5, mono5, mono5+3_000_000, "handmade kernel line with a source stamp ahead")
	e.add(ahead, c.kill(bootB, 6, 4711, "p", 1_000_000))
	e.uptimeNow(0)

	kills := killsOf(e.run(false))

	if len(kills) != 1 {
		t.Fatalf("kills = %d, want 1", len(kills))
	}
	if want := micro(killRT - 1_000_000); !kills[0].rec.CapturedAt.Equal(want) {
		t.Errorf("captured_at = %s, want %s (the line counts as lag 0, so the whole lag of 1 s is delay)", kills[0].rec.CapturedAt, want)
	}
}

func captureEarlierRunDrift(t *testing.T) {
	e, c := liveEnv(t)
	e.add(c.line(bootB, 4), c.line(bootB, 5))
	e.uptimeNow(0)
	e.run(false)
	killRT, _ := tl(7)
	e.add(c.kill(bootB, 7, 4711, "pzstd", lagUS+1_000_000))
	e.uptimeNow(0)

	kills := killsOf(e.run(false))

	if len(kills) != 1 {
		t.Fatalf("kills = %d, want 1", len(kills))
	}
	if want := micro(killRT - 1_000_000); !kills[0].rec.CapturedAt.Equal(want) {
		t.Errorf("captured_at = %s, want %s (1.3 s of lag less the 0.3 s of drift the earlier run showed)", kills[0].rec.CapturedAt, want)
	}
}

// noDelayCase is a kill that is captured at its receipt time.
type noDelayCase struct {
	name       string
	lag        uint64
	lineBefore bool
	noSource   bool
}

var noDelayCases = []noDelayCase{
	{name: "no earlier line gives no delay", lag: 2_000_000},
	{name: "a lag below the drift gives no delay", lag: 100_000, lineBefore: true},
	{name: "no source stamp gives no delay", noSource: true},
}

func captureWithoutDelay(t *testing.T, tc noDelayCase) {
	e, c := liveEnv(t)
	rt, mono := tl(6)
	var opts []kopt
	if tc.noSource {
		opts = append(opts, withoutSource())
	}
	var ents []entry
	if tc.lineBefore {
		ents = append(ents, c.line(bootB, 5))
	}
	ents = append(ents, kernelLine(c.next(), bootB, rt, mono, mono-tc.lag, killMsg(7, "p", 1, 0), opts...))
	e.add(ents...)
	e.uptimeNow(0)

	kills := killsOf(e.run(false))

	if len(kills) != 1 {
		t.Fatalf("kills = %d, want 1", len(kills))
	}
	if !kills[0].rec.CapturedAt.Equal(micro(rt)) {
		t.Errorf("captured_at = %s, want the receipt time %s", kills[0].rec.CapturedAt, micro(rt))
	}
	if tc.noSource {
		want := time.Duration(mono) * time.Microsecond // #nosec G115 -- a test value
		if got := identityOf(t, kills[0]); got.KernelTime != want || got.BootID != dashed(bootB) || got.VictimPID != 7 {
			t.Errorf("identity without a source stamp = %+v, want the monotonic time %s of the entry", got, want)
		}
	}
}

// journalWithTwoBoots returns the entries of a journal with kills of the same PID in two boots and two kills in one boot.
func journalWithTwoBoots() []entry {
	var c seqCounter
	return []entry{
		c.line(bootA, 1),
		c.kill(bootA, 2, 100, "a", lagUS),
		c.kill(bootA, 3, 100, "a", lagUS),
		c.kill(bootA, 3, 101, "a", lagUS+7),
		c.line(bootB, 4),
		c.kill(bootB, 5, 100, "a", lagUS),
	}
}

func TestKernel_KillIdentityStableAcrossReadings(t *testing.T) {
	var readings [][]killOf
	for range 2 {
		e := newEnv(t)
		e.seed(dashed(bootB), before())
		e.add(journalWithTwoBoots()...)
		readings = append(readings, killsOf(e.run(false)))
	}

	if len(readings[0]) != 4 || len(readings[1]) != 4 {
		t.Fatalf("readings hold %d and %d kills, want 4 each", len(readings[0]), len(readings[1]))
	}
	seen := map[model.KillRef]bool{}
	for i := range readings[0] {
		a, b := identityOf(t, readings[0][i]), identityOf(t, readings[1][i])
		if a != b {
			t.Errorf("kill %d: identity %+v on the first reading, %+v on the second, want the same", i, a, b)
		}
		if seen[a] {
			t.Errorf("kill %d: identity %+v is shared with an earlier kill, want identities that differ between any two kills", i, a)
		}
		seen[a] = true
		if a.BootID != readings[0][i].ev.OOMKill.BootID {
			t.Errorf("kill %d: identity boot %q differs from the event's boot_id", i, a.BootID)
		}
	}
	if got := identityOf(t, readings[0][0]).BootID; got != dashed(bootA) {
		t.Errorf("identity boot of a kill of boot A = %q, want %q", got, dashed(bootA))
	}
	if got := identityOf(t, readings[0][3]).BootID; got != dashed(bootB) {
		t.Errorf("identity boot of a kill of boot B = %q, want %q", got, dashed(bootB))
	}
}

// forgedKills are kill lines that do not come from the kernel; each is told by the options that change a genuine entry.
var forgedKills = []struct {
	pid  int
	opts []kopt
}{
	{2001, []kopt{withFacilities("3")}},
	{2002, []kopt{withFacilities("1")}},
	{2003, []kopt{withFacilities("5")}},
	{2004, []kopt{withFacilities("00")}},
	{2005, []kopt{withFacilities("")}},
	{2006, []kopt{withFacilities(" 0")}},
	{2007, []kopt{withFacilities("0 ")}},
	{2008, []kopt{withFacilities("0", "3")}},
	{2009, []kopt{withFacilities("3", "0")}},
	{2010, []kopt{withFacilities("0", "0")}},
	{2011, []kopt{withFacilities()}},
	{2012, []kopt{withTransport("syslog")}},
	{2013, []kopt{withTransport("journal")}},
	{2014, []kopt{withTransport("stdout")}},
	{2015, []kopt{withTransport("driver")}},
	{2016, []kopt{withTransport("audit")}},
	{2017, []kopt{withTransport("Kernel")}},
	{2018, []kopt{withTransport("kernel ")}},
}

func TestKernel_OnlyKernelTransportWithFacilityZero(t *testing.T) {
	t.Run("forged kill lines", onlyGenuineKills)
	t.Run("committed fixture", func(t *testing.T) {
		e := newEnv(t)
		e.j.loadFixture(t)
		e.seed(dashed(bootB), position(seqIDA, 0, 1790000000000000))

		recs := e.run(false)

		want := []int32{3021, 352448, 351416, 5150}
		if got := pids(killsOf(recs)); !slices.Equal(got, want) {
			t.Errorf("kills of the fixture = %v, want %v (not the facility 3, facility 1, syslog and two-facility lines 4243 to 4246, not the compressed 4247)", got, want)
		}
		checkValid(t, recs, t0)
	})
}

func onlyGenuineKills(t *testing.T) {
	e := newEnv(t)
	e.seed(dashed(bootB), before())
	var c seqCounter
	ents := []entry{c.line(bootB, 1), c.kill(bootB, 2, 1001, "real", lagUS)}
	for i, f := range forgedKills {
		ents = append(ents, c.kill(bootB, 3+i, f.pid, "forged", lagUS, f.opts...))
	}
	ents = append(ents, c.kill(bootB, 30, 1002, "real", lagUS))
	e.add(ents...)
	e.uptimeNow(30)

	recs := e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{1001, 1002}) {
		t.Errorf("kills = %v, want [1001 1002] (only the kernel transport with exactly one facility item 0)", got)
	}
	if g := gapsOf(recs); len(g) != 0 {
		t.Errorf("forged entries made %d gaps, want none", len(g))
	}
	for _, call := range e.trig.since(0) {
		for _, k := range call.trigger.Kills {
			if k.VictimPID != 1001 && k.VictimPID != 1002 {
				t.Errorf("trigger for %s names PID %d, want only the genuine kills", call.collector, k.VictimPID)
			}
		}
	}
	checkValid(t, recs, t0)
}

func TestKernel_HostileKillLinesGiveValidEvents(t *testing.T) {
	e := newEnv(t)
	e.seed(dashed(bootB), before())
	var c seqCounter
	repl := string(rune(0xFFFD))
	names := []string{"my proc", "a)b", "x) anon-rss:1kB", "a) total-vm:5kB, anon-rss:1kB, oom_score_adj:99", "line\nfeed", "bad\xff\xfeutf8", "", strings.Repeat("n", 300)}
	var ents []entry
	for i, n := range names {
		rt, mono := tl(1 + i)
		ents = append(ents, kernelLine(c.next(), bootB, rt, mono, mono-lagUS, killMsg(500+i, n, 2048, -7)))
	}
	e.add(ents...)
	e.uptimeNow(0)

	recs := e.run(false)

	kills := killsOf(recs)
	if len(kills) != len(names) {
		t.Fatalf("hostile kill lines gave %d events, want %d", len(kills), len(names))
	}
	for i, k := range kills {
		o := k.ev.OOMKill
		if o.VictimPID != int32(500+i) || o.AnonRSSBytes == nil || *o.AnonRSSBytes != 2048*1024 || o.OOMScoreAdj == nil || *o.OOMScoreAdj != -7 {
			t.Errorf("event %d: pid = %d, anon_rss_bytes = %v, oom_score_adj = %v, want %d, %d and -7 (the kernel's real values)", i, o.VictimPID, o.AnonRSSBytes, o.OOMScoreAdj, 500+i, 2048*1024)
		}
	}
	if got := kills[6].ev.OOMKill.VictimCommand; got != "(unnamed)" {
		t.Errorf("command of the empty name = %q, want %q", got, "(unnamed)")
	}
	if got, want := kills[5].ev.OOMKill.VictimCommand, "bad"+repl+repl+"utf8"; got != want {
		t.Errorf("command of the invalid name = %q, want the invalid bytes replaced by U+FFFD", got)
	}
	checkValid(t, recs, t0)
}

func TestKernel_LiveKillTriggersBothCollectors(t *testing.T) {
	e, c := liveEnv(t)
	lag := uint64(450_000)
	e.add(c.line(bootB, 5), c.kill(bootB, 6, 4711, "pzstd", lag))
	e.uptimeNow(0)
	before := e.trig.count()

	recs := e.run(false)

	if len(killsOf(recs)) != 1 {
		t.Fatalf("kills = %d, want 1", len(killsOf(recs)))
	}
	requireTriggerPair(t, e.trig.since(before), []model.KillRef{killIdentity(bootB, killSrc(6, lag), 4711)}, 0)
	if n := e.trig.count(); n != 2 {
		t.Errorf("trigger calls over all runs = %d, want 2 (none for the first start)", n)
	}
}

func TestKernel_StormCoalescesWithinFifteenSeconds(t *testing.T) {
	// Run n starts at t0 + 2n seconds; the first start was run 0. Kills arrive before runs 1, 2, 3 and 11. The first kill is
	// triggered at once (run 1); the kills that follow within 15 s wait for the first run 15 s later (run 9, at 18 s >= 2 s + 15 s);
	// the kill of run 11 waits for the first run 15 s after run 9 (run 17, at 34 s >= 18 s + 15 s).
	e, c := liveEnv(t)
	killAt := map[int]int32{1: 11, 2: 12, 3: 13, 11: 14}
	ref := func(n int) model.KillRef { return killIdentity(bootB, killSrc(4+2*n, lagUS), killAt[n]) }
	want := map[int][]model.KillRef{1: {ref(1)}, 9: {ref(2), ref(3)}, 17: {ref(11)}}

	for n := 1; n <= 17; n++ {
		if pid, ok := killAt[n]; ok {
			e.add(c.kill(bootB, 4+2*n, int(pid), "p", lagUS))
		}
		e.uptimeNow(0)
		beforeRun := e.trig.count()

		recs := e.run(false)

		if _, ok := killAt[n]; ok && len(killsOf(recs)) != 1 {
			t.Errorf("run %d returned %d kills, want 1 (every kill is its own event)", n, len(killsOf(recs)))
		}
		calls := e.trig.since(beforeRun)
		if kills, ok := want[n]; ok {
			requireTriggerPair(t, calls, kills, 0)
		} else if len(calls) != 0 {
			t.Errorf("run %d made trigger calls %+v, want none (at most one pair per 15 seconds)", n, calls)
		}
	}
}

func TestKernel_StormNamesAtMostSixtyFourKills(t *testing.T) {
	e, c := liveEnv(t)
	e.add(c.kill(bootB, 6, 10, "p", lagUS))
	e.uptimeNow(0)
	e.run(false) // run 1: the first kill is triggered at once
	var ents []entry
	var refs []model.KillRef
	rt, mono := tl(8)
	for i := range 70 {
		us := uint64(i) // #nosec G115 -- small test values
		ents = append(ents, kernelLine(c.next(), bootB, rt+us, mono+us, mono+us-lagUS, killMsg(100+i, "p", 1, 0)))
		refs = append(refs, killIdentity(bootB, mono+us-lagUS, int32(100+i))) // #nosec G115 -- small test values
	}
	e.add(ents...)
	e.uptimeNow(0)
	beforeRun := e.trig.count()

	recs := e.run(false) // run 2

	if len(killsOf(recs)) != 70 {
		t.Fatalf("the storm gave %d events, want 70", len(killsOf(recs)))
	}
	for n := 3; n <= 8; n++ { // runs 3 to 8 start 6 to 16 s after run 1... the pending kills wait for 15 s
		e.uptimeNow(0)
		e.run(false)
	}
	if calls := e.trig.since(beforeRun); len(calls) != 0 {
		t.Fatalf("kills within 15 s of the last trigger made %d trigger calls before the 15 s passed, want none", len(calls))
	}
	e.uptimeNow(0)

	e.run(false) // run 9, 16 s after run 1

	requireTriggerPair(t, e.trig.since(beforeRun), refs[:64], 6)
}

// lateCase is a kill the collector reads; live says whether it gets the additional snapshots.
type lateCase struct {
	name string
	boot string
	lag  uint64
	plus float64 // seconds the uptime is ahead of the monotonic time of the run's start
	live bool
}

var lateCases = []lateCase{
	{"fresh", bootB, lagUS, 0, true},
	{"59 s late", bootB, lagUS, 58, true},
	{"61 s late", bootB, lagUS, 60, false},
	{"delay counts: 58.5 s late plus 2 s delay", bootB, lagUS + 2_000_000, 57.5, false},
	{"delay counts: 56 s late plus 2 s delay", bootB, lagUS + 2_000_000, 55, true},
	{"another boot, fresh", bootA, lagUS, 0, false},
	{"another boot, 5 s late", bootC, lagUS, 4, false},
}

func TestKernel_LateOrOtherBootKillsGetNoSnapshot(t *testing.T) {
	for _, tc := range lateCases {
		t.Run(tc.name, func(t *testing.T) { lateKill(t, tc) })
	}
	t.Run("fixture with 20 days of uptime, fresh", func(t *testing.T) { fixtureKillAt(t, 1728010, true) })
	t.Run("fixture with 20 days of uptime, late", func(t *testing.T) { fixtureKillAt(t, 1728100, false) })
}

func lateKill(t *testing.T, tc lateCase) {
	e, c := liveEnv(t)
	e.add(c.line(tc.boot, 5), c.kill(tc.boot, 6, 4711, "pzstd", tc.lag))
	e.uptimeNow(tc.plus)
	beforeRun := e.trig.count()

	recs := e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{4711}) {
		t.Fatalf("kills = %v, want [4711] (a late kill is an event all the same)", got)
	}
	calls := e.trig.since(beforeRun)
	if tc.live {
		requireTriggerPair(t, calls, []model.KillRef{killIdentity(tc.boot, killSrc(6, tc.lag), 4711)}, 0)
	} else if len(calls) != 0 {
		t.Errorf("trigger calls = %+v, want none", calls)
	}
}

func fixtureKillAt(t *testing.T, uptime float64, live bool) {
	e := newEnv(t)
	e.j.loadFixture(t)
	e.seed(dashed(bootB), position(seqIDA, 34, 1790917172776852))
	e.setUptime(uptime)

	kills := killsOf(e.run(false))

	if got := pids(kills); !slices.Equal(got, []int32{5150}) {
		t.Fatalf("kills = %v, want [5150]", got)
	}
	calls := e.trig.since(0)
	if live {
		requireTriggerPair(t, calls, []model.KillRef{killIdentity(bootB, 1727959750000, 5150)}, 0)
	} else if len(calls) != 0 {
		t.Errorf("trigger calls = %+v, want none", calls)
	}
}

// uptimeHook is a /proc whose uptime file, when opened, runs a function: it models the moment during a run at which the run's
// context ends (the deadline of the job passes while the run reads).
type uptimeHook struct {
	fs.FS
	onUptime func()
}

// Open implements fs.FS.
func (h *uptimeHook) Open(name string) (fs.File, error) {
	if name == "uptime" && h.onUptime != nil {
		h.onUptime()
	}
	return h.FS.Open(name)
}

// droppedRunEnv returns an environment with the saved position before a live kill of PID 4711 (read at the second 4 of the timeline),
// a collector over a /proc that ends the context of the run in progress when the uptime is read, and the identity of the kill.
func droppedRunEnv(t *testing.T) (*env, *uptimeHook, model.KillRef) {
	t.Helper()
	e := newEnv(t)
	var c seqCounter
	e.add(c.line(bootB, 3), c.kill(bootB, 4, 4711, "pzstd", lagUS))
	e.seed(dashed(bootB), position(seqIDA, 0, r0()-1_000_000))
	hook := &uptimeHook{FS: e.proc}
	e.k = New(Sources{Journal: []fs.FS{e.j}, Etc: e.etc, Proc: hook}, e.store, slog.New(e.logs))
	e.k.SetTrigger(e.trig.fn)
	e.uptimeNow(0)
	return e, hook, killIdentity(bootB, killSrc(4, lagUS), 4711)
}

// runEnding makes a run whose context ends while it reads the live kill, as the deadline of a job does; the scheduler drops its result.
func (e *env) runEnding(hook *uptimeHook) int {
	e.t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	hook.onUptime = cancel
	_, _ = e.k.Collect(ctx, scheduler.Run{Start: e.start})
	hook.onUptime = nil
	e.start = e.start.Add(2 * time.Second)
	e.runs++
	if n := e.trig.count(); n != 0 {
		e.t.Errorf("the run whose context ended made %d trigger calls, want none", n)
	}
	return e.trig.count()
}

func TestKernel_DroppedRunSendsNoLateTrigger(t *testing.T) {
	t.Run("a run whose context ended sends nothing", func(t *testing.T) {
		e, hook, _ := droppedRunEnv(t)

		ctx, cancel := context.WithCancel(context.Background())
		defer cancel()
		hook.onUptime = cancel
		_, _ = e.k.Collect(ctx, scheduler.Run{Start: e.start})

		if ctx.Err() == nil {
			t.Fatalf("test set-up: the context did not end during the run, the live kill was not read")
		}
		if calls := e.trig.since(0); len(calls) != 0 {
			t.Errorf("trigger calls = %+v, want none (a run whose context ended queues and sends nothing)", calls)
		}
	})

	t.Run("the next run reads the kill again and sends one pair", func(t *testing.T) {
		e, hook, ref := droppedRunEnv(t)
		dropped := e.runEnding(hook)
		e.uptimeNow(0)

		recs := e.run(true)

		if got := pids(killsOf(recs)); !slices.Equal(got, []int32{4711}) {
			t.Fatalf("kills of the run after the dropped one = %v, want [4711]", got)
		}
		requireTriggerPair(t, e.trig.since(dropped), []model.KillRef{ref}, 0)
	})

	t.Run("a kill that is older than 60 s by then sends nothing", func(t *testing.T) {
		e, hook, _ := droppedRunEnv(t)
		dropped := e.runEnding(hook)
		e.uptimeNow(120)

		recs := e.run(true)

		if got := pids(killsOf(recs)); !slices.Equal(got, []int32{4711}) {
			t.Fatalf("kills of the run after the dropped one = %v, want [4711]", got)
		}
		if calls := e.trig.since(dropped); len(calls) != 0 {
			t.Errorf("trigger calls = %+v, want none (the kill is no longer live)", calls)
		}
	})

	t.Run("a trigger of a kept run is not sent twice after a sink drop", func(t *testing.T) {
		e, _, ref := droppedRunEnv(t)
		e.run(false) // a kept run that queues and sends the pair
		requireTriggerPair(t, e.trig.since(0), []model.KillRef{ref}, 0)
		e.uptimeNow(0)

		again := e.run(true) // the sink dropped the records of that run

		if got := pids(killsOf(again)); !slices.Equal(got, []int32{4711}) {
			t.Errorf("kills of the run after the dropped one = %v, want [4711] (the records are read again)", got)
		}
		if n := e.trig.count(); n != 2 {
			t.Errorf("trigger calls = %d, want 2 (the pair of the first run only: a kill that was sent is not sent again)", n)
		}
	})
}
