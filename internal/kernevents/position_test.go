package kernevents

import (
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// requirePosition checks the committed journal position.
func requirePosition(t *testing.T, e *env, seqID string, seq uint64, rt uint64) {
	t.Helper()
	got := e.store.State().Journal
	if got == nil {
		t.Fatalf("committed position = nil, want %s %d at %s", dashed(seqID), seq, micro(rt))
	}
	if got.SeqnumID != dashed(seqID) || got.Seqnum != seq || !got.Realtime.Equal(micro(rt)) {
		t.Errorf("committed position = %s %d at %s, want %s %d at %s", got.SeqnumID, got.Seqnum, got.Realtime, dashed(seqID), seq, micro(rt))
	}
}

// seqOf returns the sequence number of an entry.
func seqOf(e entry) uint64 {
	v, _ := strconv.ParseUint(string(e["__SEQNUM"]), 10, 64)
	return v
}

func TestKernel_NoSavedPositionStartsAtTail(t *testing.T) {
	e := newEnv(t)
	e.seed(dashed(bootB), nil)
	var c seqCounter
	e.add(c.line(bootB, 1), c.kill(bootB, 2, 111, "history", lagUS), c.line(bootB, 3))
	e.uptimeNow(0)

	first := e.run(false)

	if len(killsOf(first)) != 0 || len(gapsOf(first)) != 0 {
		t.Fatalf("without a saved position the first run returned %d kills and %d gaps from the history, want none", len(killsOf(first)), len(gapsOf(first)))
	}
	e.add(c.kill(bootB, 6, 222, "new", lagUS))
	e.uptimeNow(0)

	second := e.run(false)

	if got := pids(killsOf(second)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills after the start = %v, want [222] (reading starts at the end of the journal)", got)
	}
}

func TestKernel_ResumesAtSavedPositionAcrossBoots(t *testing.T) {
	e := newEnv(t)
	e.j.loadFixture(t)
	e.seed(dashed(bootB), position(seqIDA, 0, 1789000000000000))

	recs := e.run(false)

	kills := killsOf(recs)
	wantPIDs := []int32{3021, 352448, 351416, 5150, 6161}
	if got := pids(kills); !slices.Equal(got, wantPIDs) {
		t.Fatalf("kills = %v, want %v (the previous boots and the set-aside file after the files of the saved ID)", got, wantPIDs)
	}
	wantBoot := []string{bootA, bootA, bootA, bootB, bootC}
	receipt := []uint64{1790790383152982, 1790916696593383, 1790917172776852, 1792657800000000, 1789891200000000}
	exact := map[int32]uint64{3021: 1790790382989841, 5150: 1792657799750000, 6161: 1789891200000000}
	for i, k := range kills {
		if got := k.ev.OOMKill.BootID; got != dashed(wantBoot[i]) {
			t.Errorf("kill %d boot_id = %q, want %q", wantPIDs[i], got, dashed(wantBoot[i]))
		}
		if k.rec.CapturedAt.After(micro(receipt[i])) {
			t.Errorf("kill %d captured_at = %s, after the receipt time %s", wantPIDs[i], k.rec.CapturedAt, micro(receipt[i]))
		}
		if want, ok := exact[wantPIDs[i]]; ok && !k.rec.CapturedAt.Equal(micro(want)) {
			t.Errorf("kill %d captured_at = %s, want %s", wantPIDs[i], k.rec.CapturedAt.Format(time.RFC3339Nano), micro(want).Format(time.RFC3339Nano))
		}
	}
	if want := "Out of memory: Killed process 352448 (pzstd) total-vm:377064kB, anon-rss:192940kB, file-rss:1716kB, shmem-rss:0kB, UID:30 pgtables:452kB oom_score_adj:0"; kills[1].ev.Message != want {
		t.Errorf("message of the kill of 2 Oct = %q, want the real kill line %q", kills[1].ev.Message, want)
	}
	if calls := e.trig.since(0); len(calls) != 0 {
		t.Errorf("kills of earlier boots and old kills made trigger calls %+v, want none", calls)
	}
	gaps := gapsOf(recs)
	if len(gaps) != 2 {
		t.Fatalf("gaps = %d, want 2 (the entry with a compressed message and journald's missed-messages entry)", len(gaps))
	}
	requireGap(t, gaps[0], micro(1790790389787497), micro(1790790392787497))
	requireGap(t, gaps[1], micro(1790790392787497), micro(1790915966787497))
	checkValid(t, recs, t0)
}

func TestKernel_CommitsPositionAtTheStartOfTheNextRun(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	ents := []entry{c.line(bootB, 1), c.kill(bootB, 2, 111, "p", lagUS), c.line(bootB, 3)}
	e.add(ents...)
	e.seed(dashed(bootB), before())

	first := e.run(false)

	if got := pids(killsOf(first)); !slices.Equal(got, []int32{111}) {
		t.Fatalf("first run kills = %v, want [111]", got)
	}
	if st := e.store.State().Journal; st == nil || st.Seqnum != 0 {
		t.Errorf("state after the first run = %+v, want the old position (only the start of the next run commits)", st)
	}

	dropped := e.run(true)

	if got := pids(killsOf(dropped)); !slices.Equal(got, []int32{111}) {
		t.Errorf("run after a dropped result: kills = %v, want [111] (read again from the committed position)", got)
	}
	if st := e.store.State().Journal; st == nil || st.Seqnum != 0 {
		t.Errorf("state after a run that dropped the previous result = %+v, want the old position", st)
	}

	kept := e.run(false)

	if len(killsOf(kept)) != 0 {
		t.Errorf("run after a kept result returned kills %v, want none", pids(killsOf(kept)))
	}
	requirePosition(t, e, seqIDA, seqOf(ents[2]), rtOf(ents[2]))
}

func TestKernel_ReadsSetAsideFilesAfterUncleanShutdown(t *testing.T) {
	t.Run("set-aside file of the saved seqnum ID, then the new file", func(t *testing.T) {
		e := newEnv(t)
		var a, b seqCounter
		old := []entry{a.line(bootB, 1), a.line(bootB, 2), a.kill(bootB, 3, 111, "old", lagUS), a.line(bootB, 4)}
		e.j.add(t, kindSetAside, seqIDA, old)
		e.j.add(t, kindOnline, seqIDB, []entry{b.line(bootB, 6), b.kill(bootB, 7, 222, "new", lagUS)})
		e.seed(dashed(bootB), position(seqIDA, seqOf(old[1]), rtOf(old[1])))

		recs := e.run(false)

		if got := pids(killsOf(recs)); !slices.Equal(got, []int32{111, 222}) {
			t.Errorf("kills = %v, want [111 222] (the set-aside file, then the file with the new seqnum ID)", got)
		}
		if g := gapsOf(recs); len(g) != 0 {
			t.Errorf("gaps = %d, want none", len(g))
		}
		checkValid(t, recs, t0)
	})

	t.Run("fixture file of another seqnum ID after the position is read", func(t *testing.T) {
		e := newEnv(t)
		e.j.loadFixture(t)
		e.seed(dashed(bootB), position(seqIDA, 34, 1790917172776852))

		got := pids(killsOf(e.run(false)))

		if !slices.Equal(got, []int32{5150}) {
			t.Errorf("kills = %v, want [5150] (the set-aside file is older than the position)", got)
		}
	})

	t.Run("files without a head time are read after the others", func(t *testing.T) {
		const (
			posID  = "fffffffffffffffffffffffffffffff1"
			headID = "fffffffffffffffffffffffffffffff2"
			noID1  = "00000000000000000000000000000008"
			noID2  = "00000000000000000000000000000009"
		)
		e := newEnv(t)
		var a, b, n1, n2 seqCounter
		fa := []entry{a.line(bootB, 1), a.kill(bootB, 2, 1, "a", lagUS)}
		e.j.add(t, kindArchived, posID, fa)
		e.j.add(t, kindSetAside, headID, []entry{b.line(bootB, 10), b.kill(bootB, 11, 2, "b", lagUS)})
		e.j.add(t, kindSetAside, noID2, []entry{noHeadEntry(n2.next(), bootB), n2.kill(bootB, 4, 4, "n2", lagUS)})
		e.j.add(t, kindSetAside, noID1, []entry{noHeadEntry(n1.next(), bootB), n1.kill(bootB, 3, 3, "n1", lagUS)})
		e.seed(dashed(bootB), position(posID, seqOf(fa[0]), rtOf(fa[0])))

		recs := e.run(false)

		if got := pids(killsOf(recs)); !slices.Equal(got, []int32{1, 2, 3, 4}) {
			t.Errorf("kills = %v, want [1 2 3 4] (a file whose header has no head time is read after the others, in file-name order, not skipped)", got)
		}
		if g := gapsOf(recs); len(g) != 0 {
			t.Errorf("gaps = %d, want none", len(g))
		}
		checkValid(t, recs, t0)
	})
}

// noHeadEntry returns an entry that is in no list the collector reads and whose real-time stamp is 0, so that a file starting with it
// has no head time.
func noHeadEntry(seq uint64, boot string) entry {
	return entry{
		"__SEQNUM":              stamp(seq),
		"__REALTIME_TIMESTAMP":  stamp(0),
		"__MONOTONIC_TIMESTAMP": stamp(1),
		"_BOOT_ID":              []byte(boot),
		"_TRANSPORT":            []byte("driver"),
		"MESSAGE":               []byte("handmade driver message"),
	}
}

func TestKernel_JournaldRestartStartsNewSeqnumID(t *testing.T) {
	e := newEnv(t)
	var a, b seqCounter
	archived := []entry{a.line(bootB, 1), a.line(bootB, 2), a.kill(bootB, 3, 111, "before", lagUS)}
	e.j.add(t, kindArchived, seqIDA, archived)
	// journald restarted: the new file has a new seqnum ID and sequence numbers that start again.
	fresh := []entry{b.line(bootB, 5), b.kill(bootB, 6, 222, "after", lagUS), b.line(bootB, 7)}
	e.j.add(t, kindOnline, seqIDB, fresh)
	e.seed(dashed(bootB), tailOf(seqIDA, archived))

	recs := e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222] (the kill of the old file was read before, the new file is read from its head)", got)
	}
	if g := gapsOf(recs); len(g) != 0 {
		t.Errorf("gaps = %d, want none (a new seqnum ID loses nothing)", len(g))
	}
	e.run(false)
	requirePosition(t, e, seqIDB, seqOf(fresh[2]), rtOf(fresh[2]))
}

func TestKernel_LostPositionRecordsGapAndContinues(t *testing.T) {
	e := newEnv(t)
	var older, fresh seqCounter
	e.j.add(t, kindArchived, seqIDC, []entry{older.kill(bootB, 1, 999, "older", lagUS)})
	first := fresh.line(bootB, 10)
	e.j.add(t, kindOnline, seqIDB, []entry{first, fresh.kill(bootB, 11, 222, "found", lagUS), fresh.line(bootB, 12)})
	posRT, _ := tl(3)
	e.seed(dashed(bootB), position(seqIDA, 100, posRT)) // the files of seqnum ID A are gone

	recs := e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222] (a file older than the position is not read)", got)
	}
	gaps := gapsOf(recs)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1 from the saved position to the first entry the journal still has", len(gaps))
	}
	requireGap(t, gaps[0], micro(posRT), micro(rtOf(first)))
	checkValid(t, recs, t0)
	e.run(false)
	if again := gapsOf(e.run(false)); len(again) != 0 {
		t.Errorf("the gap of the lost position was recorded again: %d gaps, want none", len(again))
	}
}

// sameKills requires that two runs returned the same kills with the same identities.
func sameKills(t *testing.T, name string, a, b []killOf) {
	t.Helper()
	if len(a) != len(b) {
		t.Errorf("%s: %d kills against %d", name, len(a), len(b))
		return
	}
	for i := range a {
		if identityOf(t, a[i]) != identityOf(t, b[i]) || a[i].ev.Message != b[i].ev.Message {
			t.Errorf("%s: kill %d = %+v against %+v, want the same kill with the same identity", name, i, identityOf(t, a[i]), identityOf(t, b[i]))
		}
	}
}

func TestKernel_LostRunRereadsEntries(t *testing.T) {
	e, c := liveEnv(t)
	e.add(c.line(bootB, 5), c.kill(bootB, 6, 4711, "pzstd", lagUS))
	e.uptimeNow(0)
	beforeRun := e.trig.count()

	first := killsOf(e.run(false))
	triggered := e.trig.count() - beforeRun
	e.uptimeNow(0)
	again := killsOf(e.run(true)) // the result of the first run was dropped
	e.uptimeNow(0)
	third := killsOf(e.run(false)) // the result of the second run was kept

	if len(first) != 1 {
		t.Fatalf("first run kills = %d, want 1", len(first))
	}
	sameKills(t, "read again after a dropped result", first, again)
	if len(third) != 0 {
		t.Errorf("run after a kept result returned %d kills, want none (the position moved past them)", len(third))
	}
	if triggered != 2 || e.trig.count()-beforeRun != 2 {
		t.Errorf("trigger calls: %d in the first run, %d in all, want 2 and 2 (a kill read again is not triggered again)", triggered, e.trig.count()-beforeRun)
	}
}

// rereadEnv returns an environment with a committed position C0 and a journal holding one kill and one line after it.
func rereadEnv(t *testing.T) (*env, *seqCounter, []entry) {
	t.Helper()
	e := newEnv(t)
	c := &seqCounter{}
	pre := []entry{c.line(bootB, 1), c.line(bootB, 2)}
	post := []entry{c.kill(bootB, 4, 111, "k1", lagUS), c.line(bootB, 5)}
	e.add(append(slices.Clone(pre), post...)...)
	e.seed(dashed(bootB), tailOf(seqIDA, pre))
	return e, c, append(slices.Clone(pre), post...)
}

func TestKernel_RereadCappedAtThree(t *testing.T) {
	t.Run("four dropped runs, then one gap and the entries after the span", rereadGivesUp)
	t.Run("three dropped re-reads give no gap", rereadThreeAreAllowed)
	t.Run("a kept run resets the count", rereadCountResets)
	t.Run("dropped runs that read nothing do not count", rereadIdleRunsDoNotCount)
}

func rereadGivesUp(t *testing.T) {
	e, c, all := rereadEnv(t)
	pre, last := all[1], all[3]
	for i := range maxRereads + 1 { // run 1 and the re-reads, all dropped
		if got := pids(killsOf(e.run(i > 0))); !slices.Equal(got, []int32{111}) {
			t.Fatalf("run %d kills = %v, want [111] (a re-read gives the kills of the span again)", i+1, got)
		}
	}
	later := []entry{c.kill(bootB, 8, 222, "k2", lagUS), c.line(bootB, 9)}
	e.add(later...)

	fifth := e.run(true)

	if got := pids(killsOf(fifth)); !slices.Equal(got, []int32{222}) {
		t.Errorf("run 5 kills = %v, want [222] (the span is given up, reading goes on after it)", got)
	}
	requireOneGap(t, "run 5", fifth, micro(rtOf(pre)), micro(rtOf(last)))
	checkValid(t, fifth, t0)

	sixth := e.run(true) // the give-up run was dropped too

	if got := pids(killsOf(sixth)); !slices.Equal(got, []int32{222}) {
		t.Errorf("run 6 kills = %v, want [222] (the span's kills are never returned again)", got)
	}
	requireOneGap(t, "run 6", sixth, micro(rtOf(pre)), micro(rtOf(last)))

	seventh := e.run(false)

	if len(killsOf(seventh)) != 0 || len(gapsOf(seventh)) != 0 {
		t.Errorf("run 7 returned %d kills and %d gaps, want none (the position moved past the span)", len(killsOf(seventh)), len(gapsOf(seventh)))
	}
	requirePosition(t, e, seqIDA, seqOf(later[1]), rtOf(later[1]))
}

// requireOneGap checks that a run returned exactly one gap with the bounds.
func requireOneGap(t *testing.T, name string, recs []model.Record, from, to time.Time) {
	t.Helper()
	gaps := gapsOf(recs)
	if len(gaps) != 1 {
		t.Errorf("%s: gaps = %d, want 1 over the given-up span", name, len(gaps))
		return
	}
	requireGap(t, gaps[0], from, to)
}

func rereadThreeAreAllowed(t *testing.T) {
	e, _, _ := rereadEnv(t)
	for i := range maxRereads {
		e.run(i > 0)
	}

	fourth := e.run(true) // the third re-read

	if got := pids(killsOf(fourth)); !slices.Equal(got, []int32{111}) || len(gapsOf(fourth)) != 0 {
		t.Errorf("run 4 kills = %v, gaps = %d, want [111] and no gap (three re-reads are allowed)", got, len(gapsOf(fourth)))
	}
}

func rereadCountResets(t *testing.T) {
	e, c, _ := rereadEnv(t)
	e.run(false) // run 1 reads the first kill and is dropped
	e.run(true)  // two re-reads, both dropped
	e.run(true)
	if recs := e.run(false); len(killsOf(recs)) != 0 { // the result of the last re-read was kept
		t.Fatalf("run after a kept result returned %d kills, want none", len(killsOf(recs)))
	}
	e.add(c.kill(bootB, 10, 300, "k2", lagUS))
	e.run(false) // the next span: read and dropped, then three re-reads, all dropped
	for i := range maxRereads {
		recs := e.run(true)

		if len(gapsOf(recs)) != 0 || !slices.Equal(pids(killsOf(recs)), []int32{300}) {
			t.Fatalf("re-read %d of the next span: kills = %v, gaps = %d, want [300] and no gap (the count restarted)", i+1, pids(killsOf(recs)), len(gapsOf(recs)))
		}
	}
}

func rereadIdleRunsDoNotCount(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	pre := []entry{c.line(bootB, 1)}
	e.add(pre...)
	e.seed(dashed(bootB), tailOf(seqIDA, pre))
	for i := range 6 {
		if recs := e.run(i > 0); len(recs) != 0 {
			t.Fatalf("idle run %d returned %d records, want none", i+1, len(recs))
		}
	}
	e.add(c.kill(bootB, 5, 555, "late", lagUS))

	recs := e.run(true)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{555}) || len(gapsOf(recs)) != 0 {
		t.Errorf("kills = %v, gaps = %d, want [555] and no gap (idle dropped runs are not re-reads)", got, len(gapsOf(recs)))
	}
}

func TestKernel_ReversedTimeGapStillMovesPosition(t *testing.T) {
	tests := []struct {
		name string
		make func(c *seqCounter) (ents []entry, from time.Time)
	}{
		{"missed-messages entry between two kernel entries of the same microsecond", func(c *seqCounter) ([]entry, time.Time) {
			rt, mono := tl(5)
			a := kernelLine(c.next(), bootB, rt, mono, mono-lagUS, "handmade before")
			m := missedLine(c.next(), bootB, rt, mono+1)
			b := kernelLine(c.next(), bootB, rt, mono+2, mono+2-lagUS, "handmade after")
			return []entry{a, m, b}, micro(rt)
		}},
		{"clock set back between the two kernel entries", func(c *seqCounter) ([]entry, time.Time) {
			rt5, mono5 := tl(5)
			rt2, mono2 := tl(2)
			a := kernelLine(c.next(), bootB, rt5, mono5, mono5-lagUS, "handmade before")
			m := missedLine(c.next(), bootB, rt5+1, mono5+1)
			b := kernelLine(c.next(), bootB, rt2, mono2+10_000_000, mono2+10_000_000-lagUS, "handmade after")
			return []entry{a, m, b}, micro(rt5)
		}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			e := newEnv(t)
			var c seqCounter
			ents, from := tc.make(&c)
			ents = append(ents, c.kill(bootB, 12, 333, "k", lagUS))
			e.add(ents...)
			e.seed(dashed(bootB), before())

			recs := e.run(false)

			gaps := gapsOf(recs)
			if len(gaps) != 1 {
				t.Fatalf("gaps = %d, want 1", len(gaps))
			}
			requireGap(t, gaps[0], from, from.Add(time.Microsecond))
			if got := pids(killsOf(recs)); !slices.Equal(got, []int32{333}) {
				t.Errorf("kills = %v, want [333]", got)
			}
			checkValid(t, recs, t0)

			again := e.run(false) // the scheduler kept every record: PreviousLost is false

			if len(again) != 0 {
				t.Errorf("the run after it returned %d records, want none (the span is not returned again)", len(again))
			}
			requirePosition(t, e, seqIDA, seqOf(ents[len(ents)-1]), rtOf(ents[len(ents)-1]))
		})
	}
}

func TestKernel_MissedKernelMessagesRecordGap(t *testing.T) {
	t.Run("entry of journald between two kernel entries", func(t *testing.T) {
		e := newEnv(t)
		var c seqCounter
		a := c.line(bootB, 1)
		rtM, monoM := tl(3)
		m := missedLine(c.next(), bootB, rtM, monoM)
		b := c.line(bootB, 8)
		e.add(a, m, b, c.kill(bootB, 9, 77, "k", lagUS))
		e.seed(dashed(bootB), before())

		recs := e.run(false)

		gaps := gapsOf(recs)
		if len(gaps) != 1 {
			t.Fatalf("gaps = %d, want 1", len(gaps))
		}
		requireGap(t, gaps[0], micro(rtOf(a)), micro(rtOf(b)))
		if got := pids(killsOf(recs)); !slices.Equal(got, []int32{77}) {
			t.Errorf("kills = %v, want [77]", got)
		}
		checkValid(t, recs, t0)
	})

	t.Run("entries that only look like it give no gap", func(t *testing.T) {
		e := newEnv(t)
		var c seqCounter
		first := c.line(bootB, 1)
		forged := []entry{
			missedLine(c.next(), bootB, rtAt(2), monoAt(2), withTransport("syslog")),
			missedLine(c.next(), bootB, rtAt(3), monoAt(3), withTransport("journal")),
			missedLine(c.next(), bootB, rtAt(4), monoAt(4), withTransport("kernel"), withFacilities("0"), withSource("1")),
			missedLine(c.next(), bootB, rtAt(5), monoAt(5), func(en entry) { en["MESSAGE_ID"] = []byte("e9bf28e6e834481bb6f48f548ad13607") }),
			missedLine(c.next(), bootB, rtAt(6), monoAt(6), func(en entry) { delete(en, "MESSAGE_ID") }),
		}
		ents := []entry{first}
		ents = append(ents, forged...)
		ents = append(ents, c.line(bootB, 8), c.kill(bootB, 9, 77, "k", lagUS))
		e.add(ents...)
		e.seed(dashed(bootB), before())

		recs := e.run(false)

		if g := gapsOf(recs); len(g) != 0 {
			t.Errorf("gaps = %d, want none (only journald's own entry on the driver transport counts)", len(g))
		}
		if got := pids(killsOf(recs)); !slices.Equal(got, []int32{77}) {
			t.Errorf("kills = %v, want [77]", got)
		}
	})
}

// rtAt and monoAt return the stamps of second sec of the timeline.
func rtAt(sec int) uint64 {
	rt, _ := tl(sec)
	return rt
}

func monoAt(sec int) uint64 {
	_, mono := tl(sec)
	return mono
}

func TestKernel_UndecodableEntryRecordsGap(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	padded := killMsg(666, "compressed", 1, 0) + strings.Repeat(" padding", 80) // 600 bytes and more are stored compressed
	if len(padded) < 600 {
		t.Fatalf("test set-up: the padded kill line has %d bytes, want 600 or more", len(padded))
	}
	facilities := make([]string, 17)
	for i := range facilities {
		facilities[i] = strconv.Itoa(i)
	}
	l1 := c.line(bootB, 1)
	rt2, mono2 := tl(2)
	compressed := kernelLine(c.next(), bootB, rt2, mono2, mono2-lagUS, padded)
	l3 := c.line(bootB, 3)
	rt4, mono4 := tl(4)
	omitted := kernelLine(c.next(), bootB, rt4, mono4, mono4-lagUS, killMsg(667, "omitted", 1, 0), withFacilities(facilities...))
	l5 := c.line(bootB, 5)
	e.add(l1, compressed, l3, omitted, l5, c.kill(bootB, 6, 777, "control", lagUS))
	e.seed(dashed(bootB), before())

	recs := e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{777}) {
		t.Errorf("kills = %v, want [777] (an entry with a compressed item or with items beyond the kept fields is never a kill)", got)
	}
	gaps := gapsOf(recs)
	if len(gaps) != 2 {
		t.Fatalf("gaps = %d, want 2", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(l1)), micro(rtOf(l3)))
	requireGap(t, gaps[1], micro(rtOf(l3)), micro(rtOf(l5)))
	checkValid(t, recs, t0)
}

// remove deletes a journal file, as a vacuum of journald does.
func (j *journalFS) remove(name string) {
	j.mu.Lock()
	defer j.mu.Unlock()
	delete(j.files, name)
}

// vacuumWorld is a journal whose archived file of the seqnum ID A holds the saved position; the file may be removed.
type vacuumWorld struct {
	e  *env
	c  *seqCounter
	f1 []entry
}

// newVacuumWorld returns an environment with the archived file [line 0, line 1, kill 111, line 3] (seconds of the timeline) and
// the saved position at its entry posIdx; removed deletes the file again.
func newVacuumWorld(t *testing.T, posIdx int, removed bool) *vacuumWorld {
	t.Helper()
	w := &vacuumWorld{e: newEnv(t), c: &seqCounter{}}
	w.f1 = []entry{w.c.line(bootB, 0), w.c.line(bootB, 1), w.c.kill(bootB, 2, 111, "vacuumed", lagUS), w.c.line(bootB, 3)}
	name := w.e.add(w.f1...)
	if removed {
		w.e.j.remove(name)
	}
	w.e.seed(dashed(bootB), position(seqIDA, seqOf(w.f1[posIdx]), rtOf(w.f1[posIdx])))
	return w
}

// online adds the online file of the seqnum ID A.
func (w *vacuumWorld) online(entries ...entry) {
	w.e.j.add(w.e.t, kindOnline, seqIDA, entries)
}

func TestKernel_VacuumedPositionRecordsGap(t *testing.T) {
	t.Run("position in a removed file, a later file of the same ID remains", vacuumedLaterFileRemains)
	t.Run("the file with the position is present and fully read", vacuumedPositionFileFullyRead)
	t.Run("later file without a head time ends the gap at the next readable kernel entry", vacuumedNoHeadTimeNextEntry)
	t.Run("later file without a head time and without a kernel entry ends the gap at the run start", vacuumedNoHeadTimeRunStart)
	t.Run("later file without a kernel entry gives the gap once", vacuumedLaterFileWithoutKernelEntry)
	t.Run("a dropped run after the gap reads the same single gap again", vacuumedGapReadAgainAfterDroppedRun)
	t.Run("position 0 and the remaining file starts at sequence number 50", vacuumedPositionZeroFileStartsAtFifty)
	t.Run("position 0 and the first file starts at sequence number 1 gives no gap", vacuumedPositionZeroFileStartsAtOne)
	t.Run("position n and the next file starts at n+1 gives no gap", vacuumedNextFileContinuesThePosition)
	t.Run("a refused file met first in the run gives its gap and no vacuumed-position gap", vacuumedWithRefusedFileFirstMet)
	t.Run("a refused file met in an earlier run does not suppress the vacuumed-position gap", vacuumedAfterRefusedFileMetEarlier)
}

func vacuumedWithRefusedFileFirstMet(t *testing.T) {
	w := newVacuumWorld(t, 1, true)
	garble(w.e, w.e.add(w.c.line(bootB, 4), w.c.line(bootB, 5)))
	f2 := []entry{w.c.line(bootB, 6), w.c.kill(bootB, 7, 222, "online", lagUS)}
	w.online(f2...)
	start := w.e.start

	first := w.e.run(false)

	if got := pids(killsOf(first)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222]", got)
	}
	gaps := gapsOf(first)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want exactly the gap of the refused file (the vacuumed span is part of it)", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(w.f1[1])), start)
	checkValid(t, first, start)
	for i := 2; i <= 3; i++ {
		if again := gapsOf(w.e.run(false)); len(again) != 0 {
			t.Errorf("run %d: %d gaps, want none", i, len(again))
		}
	}
}

func vacuumedAfterRefusedFileMetEarlier(t *testing.T) {
	e := newEnv(t)
	c := &seqCounter{}
	f1 := []entry{c.line(bootB, 0), c.line(bootB, 1), c.kill(bootB, 2, 111, "first", lagUS), c.line(bootB, 3)}
	name := e.add(f1...)
	garble(e, e.add(c.line(bootB, 4), c.line(bootB, 5)))
	e.seed(dashed(bootB), position(seqIDA, seqOf(f1[0]), rtOf(f1[0])))
	start := e.start

	first := e.run(false)

	if gaps := gapsOf(first); len(gaps) != 1 {
		t.Fatalf("first run: gaps = %d, want 1 for the refused file", len(gaps))
	}
	e.j.remove(name)
	f2 := []entry{c.line(bootB, 6), c.kill(bootB, 7, 222, "online", lagUS)}
	e.j.add(t, kindOnline, seqIDA, f2)

	second := e.run(false)

	gaps := gapsOf(second)
	if len(gaps) != 1 {
		t.Fatalf("second run: gaps = %d, want 1 for the vacuumed span (the refused file was met earlier)", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(f1[3])), micro(rtOf(f2[0])))
	checkValid(t, second, start)
	if got := pids(killsOf(second)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222]", got)
	}
}

func vacuumedPositionZeroFileStartsAtFifty(t *testing.T) {
	e := newEnv(t)
	c := &seqCounter{n: 48} // the first entry is 50
	f := []entry{c.line(bootB, 4), c.kill(bootB, 5, 222, "online", lagUS)}
	e.j.add(t, kindOnline, seqIDA, f)
	e.seed(dashed(bootB), before())

	recs := e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222]", got)
	}
	gaps := gapsOf(recs)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1 from the position's time to the head time of the file (entries 1 to 49 are gone)", len(gaps))
	}
	requireGap(t, gaps[0], before().Realtime, micro(rtOf(f[0])))
	checkValid(t, recs, t0)
	if again := gapsOf(e.run(false)); len(again) != 0 {
		t.Errorf("second run: %d gaps, want none (the vacuumed span is recorded once)", len(again))
	}
}

func vacuumedPositionZeroFileStartsAtOne(t *testing.T) {
	e := newEnv(t)
	c := &seqCounter{}
	f := []entry{c.line(bootB, 4), c.kill(bootB, 5, 222, "online", lagUS)}
	if got := seqOf(f[0]); got != 1 {
		t.Fatalf("first sequence number = %d, want 1", got)
	}
	e.j.add(t, kindOnline, seqIDA, f)
	e.seed(dashed(bootB), before())

	recs := e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222]", got)
	}
	if g := gapsOf(recs); len(g) != 0 {
		t.Errorf("gaps = %d, want none (the file starts directly after position 0)", len(g))
	}
}

func vacuumedNextFileContinuesThePosition(t *testing.T) {
	w := newVacuumWorld(t, 3, true)
	pos := seqOf(w.f1[3])
	c := &seqCounter{n: pos - 1} // the first entry is pos+1
	f2 := []entry{c.line(bootB, 4), c.kill(bootB, 5, 222, "online", lagUS)}
	if got := seqOf(f2[0]); got != pos+1 {
		t.Fatalf("first sequence number = %d, want %d", got, pos+1)
	}
	w.online(f2...)

	recs := w.e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222]", got)
	}
	if g := gapsOf(recs); len(g) != 0 {
		t.Errorf("gaps = %d, want none (the next file continues at position+1, nothing is gone)", len(g))
	}
}

func vacuumedLaterFileRemains(t *testing.T) {
	w := newVacuumWorld(t, 1, true)
	f2 := []entry{w.c.line(bootB, 4), w.c.kill(bootB, 5, 222, "online", lagUS)}
	w.online(f2...)

	first := w.e.run(false)

	if got := pids(killsOf(first)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222] (the kill of the removed file is lost, the online file is read from its head)", got)
	}
	gaps := gapsOf(first)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1 from the position's time to the head time of the online file", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(w.f1[1])), micro(rtOf(f2[0])))
	checkValid(t, first, t0)
	for i := 2; i <= 3; i++ {
		if again := gapsOf(w.e.run(false)); len(again) != 0 {
			t.Errorf("run %d: %d gaps, want none (the vacuumed span is recorded once)", i, len(again))
		}
	}
	requirePosition(t, w.e, seqIDA, seqOf(f2[1]), rtOf(f2[1]))
}

func vacuumedPositionFileFullyRead(t *testing.T) {
	w := newVacuumWorld(t, 3, false)
	w.online(w.c.line(bootB, 4), w.c.kill(bootB, 5, 222, "online", lagUS))

	recs := w.e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222]", got)
	}
	if g := gapsOf(recs); len(g) != 0 {
		t.Errorf("gaps = %d, want none (the file with the position is still there, nothing between is gone)", len(g))
	}
}

func vacuumedNoHeadTimeNextEntry(t *testing.T) {
	w := newVacuumWorld(t, 1, true)
	next := w.c.line(bootB, 4)
	w.online(noHeadEntry(w.c.next(), bootB), next, w.c.kill(bootB, 5, 222, "online", lagUS))

	recs := w.e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222]", got)
	}
	gaps := gapsOf(recs)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1 ending at the next readable kernel entry", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(w.f1[1])), micro(rtOf(next)))
	checkValid(t, recs, t0)
}

func vacuumedNoHeadTimeRunStart(t *testing.T) {
	w := newVacuumWorld(t, 1, true)
	w.online(noHeadEntry(w.c.next(), bootB))

	recs := w.e.run(false)

	gaps := gapsOf(recs)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1 ending at the start of the run", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(w.f1[1])), t0)
	checkValid(t, recs, t0)
	if again := gapsOf(w.e.run(false)); len(again) != 0 {
		t.Errorf("second run: %d gaps, want none", len(again))
	}
}

func vacuumedLaterFileWithoutKernelEntry(t *testing.T) {
	w := newVacuumWorld(t, 1, true)
	head := missedLine(w.c.next(), bootB, rtAt(4), monoAt(4))
	head["MESSAGE_ID"] = []byte("00000000000000000000000000000000") // a driver entry that is no missed-messages entry
	w.online(head)

	first := w.e.run(false)

	gaps := gapsOf(first)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1 to the head time of the file", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(w.f1[1])), micro(rtOf(head)))
	for i := 2; i <= 4; i++ {
		if again := gapsOf(w.e.run(false)); len(again) != 0 {
			t.Errorf("run %d: %d gaps, want none (the gap is recorded once although the position did not move)", i, len(again))
		}
	}
}

func vacuumedGapReadAgainAfterDroppedRun(t *testing.T) {
	w := newVacuumWorld(t, 1, true)
	f2 := []entry{w.c.line(bootB, 4), w.c.kill(bootB, 5, 222, "online", lagUS)}
	w.online(f2...)

	first := w.e.run(false)
	again := w.e.run(true) // the result of the first run was dropped
	next := w.e.run(false)

	for name, recs := range map[string][]model.Record{"first run": first, "run after the dropped one": again} {
		gaps := gapsOf(recs)
		if len(gaps) != 1 {
			t.Errorf("%s: gaps = %d, want 1", name, len(gaps))
			continue
		}
		requireGap(t, gaps[0], micro(rtOf(w.f1[1])), micro(rtOf(f2[0])))
		if got := pids(killsOf(recs)); !slices.Equal(got, []int32{222}) {
			t.Errorf("%s: kills = %v, want [222]", name, got)
		}
	}
	if g := gapsOf(next); len(g) != 0 || len(killsOf(next)) != 0 {
		t.Errorf("run after a kept result: %d gaps and %d kills, want none", len(g), len(killsOf(next)))
	}
}
