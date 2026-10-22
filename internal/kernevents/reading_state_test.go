package kernevents

import (
	"slices"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func TestKernel_KillsOfOneRunShareOneTrigger(t *testing.T) {
	t.Run("three kills of one run", func(t *testing.T) {
		e, c := liveEnv(t)
		e.add(c.kill(bootB, 3, 11, "p", lagUS), c.kill(bootB, 4, 12, "p", lagUS), c.kill(bootB, 5, 13, "p", lagUS))
		e.uptimeNow(0)
		beforeRun := e.trig.count()

		e.run(false)

		refs := []model.KillRef{killIdentity(bootB, killSrc(3, lagUS), 11), killIdentity(bootB, killSrc(4, lagUS), 12), killIdentity(bootB, killSrc(5, lagUS), 13)}
		requireTriggerPair(t, e.trig.since(beforeRun), refs, 0)
	})

	t.Run("more than 64 kills of one run", func(t *testing.T) {
		e, c := liveEnv(t)
		rt, mono := tl(5)
		var ents []entry
		var refs []model.KillRef
		for i := range 70 {
			us := uint64(i) // #nosec G115 -- small test values
			ents = append(ents, kernelLine(c.next(), bootB, rt+us, mono+us, mono+us-lagUS, killMsg(100+i, "p", 1, 0)))
			refs = append(refs, killIdentity(bootB, mono+us-lagUS, int32(100+i))) // #nosec G115 -- small test values
		}
		e.add(ents...)
		e.uptimeNow(0)
		beforeRun := e.trig.count()

		e.run(false)

		requireTriggerPair(t, e.trig.since(beforeRun), refs[:64], 6)
	})

	t.Run("kept kills and the kills of a later run", func(t *testing.T) {
		e, c := liveEnv(t)
		e.add(c.kill(bootB, 3, 11, "p", lagUS))
		e.uptimeNow(0)
		e.run(false) // run 1: triggered at once
		e.add(c.kill(bootB, 4, 12, "p", lagUS), c.kill(bootB, 5, 13, "p", lagUS))
		e.uptimeNow(0)
		e.run(false) // run 2: kept
		for range 6 {
			e.uptimeNow(0)
			e.run(false) // runs 3 to 8, less than 15 s after run 1
		}
		e.add(c.kill(bootB, 6, 14, "p", lagUS), c.kill(bootB, 6, 15, "p", lagUS+7))
		e.uptimeNow(0)
		beforeRun := e.trig.count()

		e.run(false) // run 9: 16 s after run 1

		refs := []model.KillRef{killIdentity(bootB, killSrc(4, lagUS), 12), killIdentity(bootB, killSrc(5, lagUS), 13),
			killIdentity(bootB, killSrc(6, lagUS), 14), killIdentity(bootB, killSrc(6, lagUS+7), 15)}
		requireTriggerPair(t, e.trig.since(beforeRun), refs, 0)
	})
}

// requireNothingMore checks that n further runs return no record at all.
func requireNothingMore(t *testing.T, e *env, n int) {
	t.Helper()
	for i := range n {
		if recs := e.run(false); len(recs) != 0 {
			t.Fatalf("run %d after the files were read returned %d records %v, want none (nothing is read twice)", i+1, len(recs), pids(killsOf(recs)))
		}
	}
}

func TestKernel_PositionNeverMovesBackInTime(t *testing.T) {
	t.Run("fixture with an older set-aside file read last", positionFixture)
	t.Run("dropped run reads the same files again", positionFixtureAfterDrop)
	t.Run("files without a head time read last", positionNoHeadFiles)
	t.Run("online file of another ID with a head time before the position", positionClockSetBack)
	t.Run("file without a head time cut by the 256-kill limit", positionNoHeadCut)
}

func fixtureEnv(t *testing.T) *env {
	t.Helper()
	e := newEnv(t)
	e.j.loadFixture(t)
	e.seed(dashed(bootB), position(seqIDA, 0, 1789000000000000))
	return e
}

func positionFixture(t *testing.T) {
	e := fixtureEnv(t)

	first := e.run(false)

	if got := pids(killsOf(first)); !slices.Equal(got, []int32{3021, 352448, 351416, 5150, 6161}) {
		t.Fatalf("kills = %v, want the 5 kills of the fixture", got)
	}
	requireNothingMore(t, e, 2)
	requirePosition(t, e, seqIDA, 157, 1792657800000000)
}

func positionFixtureAfterDrop(t *testing.T) {
	e := fixtureEnv(t)
	first := e.run(false)

	again := e.run(true)

	sameKills(t, "read again after a dropped result", killsOf(first), killsOf(again))
	if len(gapsOf(first)) != len(gapsOf(again)) || len(killsOf(again)) != 5 {
		t.Errorf("re-read: %d kills and %d gaps, first reading: %d kills and %d gaps, want the same files read exactly as before", len(killsOf(again)), len(gapsOf(again)), len(killsOf(first)), len(gapsOf(first)))
	}
	requireNothingMore(t, e, 3)
	requirePosition(t, e, seqIDA, 157, 1792657800000000)
}

func positionNoHeadFiles(t *testing.T) {
	const (
		posID  = "fffffffffffffffffffffffffffffff1"
		headID = "fffffffffffffffffffffffffffffff2"
		noID1  = "00000000000000000000000000000008"
		noID2  = "00000000000000000000000000000009"
	)
	e := newEnv(t)
	var a, b, n1, n2 seqCounter
	fa := []entry{a.line(bootB, 1), a.kill(bootB, 2, 1, "a", lagUS)}
	fb := []entry{b.line(bootB, 10), b.kill(bootB, 11, 2, "b", lagUS)}
	e.j.add(t, kindArchived, posID, fa)
	e.j.add(t, kindSetAside, headID, fb)
	e.j.add(t, kindSetAside, noID2, []entry{noHeadEntry(n2.next(), bootB), n2.kill(bootB, 4, 4, "n2", lagUS)})
	e.j.add(t, kindSetAside, noID1, []entry{noHeadEntry(n1.next(), bootB), n1.kill(bootB, 3, 3, "n1", lagUS)})
	e.seed(dashed(bootB), position(posID, seqOf(fa[0]), rtOf(fa[0])))

	first := e.run(false)

	if got := pids(killsOf(first)); !slices.Equal(got, []int32{1, 2, 3, 4}) {
		t.Fatalf("kills = %v, want [1 2 3 4]", got)
	}
	requireNothingMore(t, e, 2)
	requirePosition(t, e, headID, seqOf(fb[1]), rtOf(fb[1]))
}

func positionClockSetBack(t *testing.T) {
	e := newEnv(t)
	var a, b seqCounter
	f0 := []entry{a.line(bootB, 10), a.line(bootB, 12)}
	e.add(f0...)
	e.j.add(t, kindOnline, seqIDB, []entry{b.line(bootB, 1), b.kill(bootB, 3, 222, "set back", lagUS)})
	e.seed(dashed(bootB), tailOf(seqIDA, f0))

	first := e.run(false)

	if got := pids(killsOf(first)); !slices.Equal(got, []int32{222}) {
		t.Fatalf("kills = %v, want [222] (an online file is read whatever its head time)", got)
	}
	requireNothingMore(t, e, 2)
}

func positionNoHeadCut(t *testing.T) {
	e := newEnv(t)
	var a seqCounter
	f0 := []entry{a.line(bootB, 1)}
	e.add(f0...)
	var n seqCounter
	ents := []entry{noHeadEntry(n.next(), bootB)}
	want := make([]int32, 300)
	for i := range 300 {
		us := uint64(i) * 1000 // #nosec G115 -- small test values
		ents = append(ents, kernelLine(n.next(), bootB, r0()+us+1_000_000, m0+us+1_000_000, m0+us+1_000_000-lagUS, killMsg(i+1, "k", 1, 0)))
		want[i] = int32(i + 1) // #nosec G115 -- small test values
	}
	e.j.add(t, kindSetAside, seqIDC, ents)
	e.seed(dashed(bootB), tailOf(seqIDA, f0))

	first := e.run(false)
	second := e.run(false)
	third := e.run(false)

	if got := pids(killsOf(first)); !slices.Equal(got, want[:256]) {
		t.Fatalf("first run kills = %d PIDs, want the first 256 in order", len(got))
	}
	if got := pids(killsOf(second)); !slices.Equal(got, want[256:]) {
		t.Errorf("second run kills = %v, want [257 ... 300] (the file is continued, not read again from its head)", got)
	}
	if len(third) != 0 {
		t.Errorf("third run returned %d records, want none", len(third))
	}
}
