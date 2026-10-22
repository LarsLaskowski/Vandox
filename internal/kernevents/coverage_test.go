package kernevents

import (
	"errors"
	"io"
	"io/fs"
	"log/slog"
	"strings"
	"sync/atomic"
	"testing"
	"testing/fstest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
)

var errBoom = errors.New("boom")

// wrapFS passes the regular files of an fs.FS through wrap.
type wrapFS struct {
	fs.FS
	wrap func(name string, f fs.File) fs.File
}

func (w wrapFS) Open(name string) (fs.File, error) {
	f, err := w.FS.Open(name)
	if err != nil {
		return nil, err
	}
	if st, serr := f.Stat(); serr != nil || st.IsDir() {
		return f, nil
	}
	return w.wrap(name, f), nil
}

// plainFile hides the ReadAt of a file.
type plainFile struct{ fs.File }

// statFails is a file whose Stat fails from the n-th call on (n 1 is the first).
type statFails struct {
	fs.File
	calls *atomic.Int32
	from  int32
}

func (s statFails) Stat() (fs.FileInfo, error) {
	if s.calls.Add(1) >= s.from {
		return nil, errBoom
	}
	return s.File.Stat()
}

func (s statFails) ReadAt(p []byte, off int64) (int, error) {
	return s.File.(io.ReaderAt).ReadAt(p, off)
}

// sizeInfo is a FileInfo of another size.
type sizeInfo struct {
	fs.FileInfo
	size int64
}

func (s sizeInfo) Size() int64 { return s.size }

// growing is the online file as it grows: its first stats report the cut size, later ones the full size of the bytes it serves. It
// models a file that journald appends to between two reads of the collector.
type growing struct {
	fs.File
	data  []byte
	cut   int64
	calls *atomic.Int32
	whole int32 // the stat call from which the file is whole
}

func (g growing) Stat() (fs.FileInfo, error) {
	st, err := g.File.Stat()
	if err != nil {
		return nil, err
	}
	size := int64(len(g.data))
	if g.calls.Add(1) < g.whole {
		size = g.cut
	}
	return sizeInfo{FileInfo: st, size: size}, nil
}

func (g growing) ReadAt(p []byte, off int64) (int, error) {
	if off >= int64(len(g.data)) {
		return 0, io.EOF
	}
	n := copy(p, g.data[off:])
	if n < len(p) {
		return n, io.EOF
	}
	return n, nil
}

// useJournal reads the journal through root from now on.
func (e *env) useJournal(root fs.FS) {
	e.k = New(Sources{Journal: []fs.FS{root}, Etc: e.etc, Proc: e.proc}, e.store, slog.New(e.logs))
	e.k.SetTrigger(e.trig.fn)
}

func TestNew_DefaultsFillNilSources(t *testing.T) {
	log := slog.New(&logSink{})

	k := New(Sources{}, nil, log)

	if len(k.src.Journal) != 2 || k.src.Journal[0] == nil || k.src.Journal[1] == nil || k.src.Etc == nil || k.src.Proc == nil {
		t.Errorf("New(Sources{}) sources = %+v, want the two journal roots, /etc and /proc", k.src)
	}
	own := newJournalFS()
	own2 := fstest.MapFS{}
	k = New(Sources{Journal: []fs.FS{own}, Etc: own2}, nil, log)
	if len(k.src.Journal) != 1 || k.src.Journal[0] != fs.FS(own) || k.src.Proc == nil {
		t.Errorf("New() with a journal root and /etc given: sources = %+v, want them kept and only /proc defaulted", k.src)
	}
}

func TestKernel_WorksWithoutAStore(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	e.add(c.line(bootB, 1))
	k := New(Sources{Journal: []fs.FS{e.j}, Etc: e.etc, Proc: e.proc}, nil, slog.New(e.logs))
	e.k = k

	first := e.run(false)
	e.add(c.kill(bootB, 4, 4711, "p", lagUS))
	second := e.run(false)

	if len(bootsOf(first)) != 1 || len(killsOf(first)) != 0 {
		t.Errorf("first run: %d boot events and %d kills, want 1 and 0 (no saved state: a boot event, history unread)", len(bootsOf(first)), len(killsOf(first)))
	}
	if got := pids(killsOf(second)); len(got) != 1 || got[0] != 4711 {
		t.Errorf("second run kills = %v, want [4711]", got)
	}
}

func TestKernel_NoFilesAtAllIsQuiet(t *testing.T) {
	e := newEnv(t)
	e.seed(dashed(bootB), nil)

	recs := e.run(false)

	if len(recs) != 0 {
		t.Errorf("an empty journal directory without a saved position gave %d records, want none", len(recs))
	}
}

func TestBoot_EventIsCarriedAgainUntilARunIsKept(t *testing.T) {
	e := newEnv(t)
	healthyJournal(e)
	e.seed("", before())

	first := e.run(false)
	second := e.run(true) // the result with the boot event was dropped
	third := e.run(true)
	fourth := e.run(false) // the third run's result was kept
	fifth := e.run(false)

	for i, recs := range [][]model.Record{first, second, third} {
		boots := bootsOf(recs)
		if len(boots) != 1 || boots[0].ev.Boot.BootID != dashed(bootB) {
			t.Errorf("run %d: boot events = %d, want the one boot event again", i+1, len(boots))
		}
	}
	if len(bootsOf(fourth)) != 0 || len(bootsOf(fifth)) != 0 {
		t.Errorf("runs 4 and 5 made %d and %d boot events, want none once a run was kept", len(bootsOf(fourth)), len(bootsOf(fifth)))
	}
}

func TestKernel_OpenFailuresAtScanTime(t *testing.T) {
	tests := []struct {
		name string
		make func(e *env, failFile string)
	}{
		{"open fails", func(e *env, failFile string) {
			e.j.setHook(func(op, file string, _ int64) error {
				if op == "open" && strings.HasSuffix(file, failFile) {
					return errBoom
				}
				return nil
			})
		}},
		{"stat fails", func(e *env, failFile string) {
			e.useJournal(wrapFS{FS: e.j, wrap: func(name string, f fs.File) fs.File {
				if strings.HasSuffix(name, failFile) {
					return statFails{File: f, calls: new(atomic.Int32), from: 1}
				}
				return f
			}})
		}},
		{"file is no ReaderAt", func(e *env, failFile string) {
			e.useJournal(wrapFS{FS: e.j, wrap: func(name string, f fs.File) fs.File {
				if strings.HasSuffix(name, failFile) {
					return plainFile{File: f}
				}
				return f
			}})
		}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { openFailure(t, tc.make) })
	}
}

func openFailure(t *testing.T, install func(e *env, failFile string)) {
	e := newEnv(t)
	var c seqCounter
	f1 := e.add(c.line(bootB, 1), c.kill(bootB, 2, 111, "k", lagUS), c.line(bootB, 3))
	f2 := []entry{c.kill(bootB, 4, 222, "k2", lagUS)}
	e.add(f2...)
	e.seed(dashed(bootB), before())
	install(e, f1)
	for i := range 2 {
		if recs := e.run(false); len(recs) != 0 {
			t.Fatalf("run %d with a file that cannot be opened returned %d records, want none (the run stops, nothing is lost while the journal holds it)", i+1, len(recs))
		}
	}
	start := e.start

	third := e.run(false)

	gaps := gapsOf(third)
	if len(gaps) != 1 {
		t.Fatalf("gaps at the third failing run = %d, want 1 for the file that is given up", len(gaps))
	}
	requireGap(t, gaps[0], before().Realtime, start)
	if got := pids(killsOf(third)); len(got) != 1 || got[0] != 222 {
		t.Errorf("kills at the third failing run = %v, want [222] (the other files are read on)", got)
	}
	for i := range 2 {
		if recs := e.run(false); len(recs) != 0 {
			t.Errorf("run %d after the give-up returned %d records, want none (the file is not given up twice)", i+1, len(recs))
		}
	}
	if warns := e.logs.find(slog.LevelWarn, "kernel messages not readable"); len(warns) != 1 || warns[0].attrs["err"] != "journal: archived: read failed" {
		t.Errorf("warnings = %v, want one with the fixed reason read failed", warns)
	}
}

// onlineTear returns an environment whose online file (seqnum ID B) holds eight kernel entries, the kills 111 and 112 among them, and
// the whole and the cut bytes of that file; the cut leaves some, not all, entries readable.
func onlineTear(t *testing.T) (e *env, ents []entry, full, cut []byte, readable []uint64) {
	t.Helper()
	e = newEnv(t)
	var a, b seqCounter
	pre := []entry{a.line(bootB, 1)}
	e.add(pre...)
	ents = []entry{b.line(bootB, 10), b.line(bootB, 11), b.kill(bootB, 12, 111, "k", lagUS), b.line(bootB, 13), b.line(bootB, 14),
		b.line(bootB, 15), b.line(bootB, 16), b.kill(bootB, 17, 112, "k", lagUS)}
	e.j.add(t, kindOnline, seqIDB, ents)
	full = e.j.data(onlineName)
	cut, readable = cutInsideTheList(t, full, len(ents), false)
	e.seed(dashed(bootB), tailOf(seqIDA, pre))
	return e, ents, full, cut, readable
}

func TestKernel_OnlineFileTearIsRetriedAfterRefresh(t *testing.T) {
	t.Run("the file grew meanwhile", func(t *testing.T) {
		e, _, full, cut, _ := onlineTear(t)
		calls := new(atomic.Int32)
		e.useJournal(wrapFS{FS: e.j, wrap: func(name string, f fs.File) fs.File {
			if strings.HasSuffix(name, onlineName) {
				return growing{File: f, data: full, cut: int64(len(cut)), calls: calls, whole: 3}
			}
			return f
		}})

		recs := e.run(false)

		if got := pids(killsOf(recs)); len(got) != 2 || got[0] != 111 || got[1] != 112 {
			t.Errorf("kills = %v, want [111 112] (the read is repeated with the new size)", got)
		}
		if len(gapsOf(recs)) != 0 || len(e.logs.all()) != 0 {
			t.Errorf("gaps = %d, log lines = %d, want none of either", len(gapsOf(recs)), len(e.logs.all()))
		}
	})

	t.Run("the size cannot be read again", func(t *testing.T) {
		e, _, full, cut, _ := onlineTear(t)
		calls := new(atomic.Int32)
		e.useJournal(wrapFS{FS: e.j, wrap: func(name string, f fs.File) fs.File {
			if strings.HasSuffix(name, onlineName) {
				return statThenFail{growing{File: f, data: full, cut: int64(len(cut)), calls: calls, whole: 1 << 20}, calls}
			}
			return f
		}})

		recs := e.run(false)

		if got := pids(killsOf(recs)); len(got) != 1 || got[0] != 111 || len(gapsOf(recs)) != 0 {
			t.Errorf("kills = %v, gaps = %d, want [111] and no gap (the tear stays, the run stops at it)", got, len(gapsOf(recs)))
		}
	})

	t.Run("the tear stays and the file is given up at the third run", tornOnlineGivenUp)
}

// statThenFail fails the third Stat of a growing file.
type statThenFail struct {
	growing
	calls *atomic.Int32
}

func (s statThenFail) Stat() (fs.FileInfo, error) {
	if s.calls.Load() >= 2 {
		return nil, errBoom
	}
	return s.growing.Stat()
}

func tornOnlineGivenUp(t *testing.T) {
	e, ents, _, cut, readable := onlineTear(t)
	e.j.put(onlineName, cut)
	var kills []int32
	var gapRuns []int
	var warnsAt []int
	for run := 1; run <= 6; run++ {
		recs := e.run(false)
		kills = append(kills, pids(killsOf(recs))...)
		for range gapsOf(recs) {
			gapRuns = append(gapRuns, run)
		}
		if g := gapsOf(recs); len(g) == 1 {
			requireGap(t, g[0], micro(readable[len(readable)-1]), micro(rtOf(ents[7])))
		}
		if len(e.logs.find(slog.LevelWarn, "kernel messages not readable")) > len(warnsAt) {
			warnsAt = append(warnsAt, run)
		}
	}

	if len(kills) != 1 || kills[0] != 111 {
		t.Errorf("kills over six runs = %v, want [111] once (the entries before the tear are read, the kill 112 is lost in the tear)", kills)
	}
	if len(gapRuns) != 1 || gapRuns[0] != 3 {
		t.Errorf("gaps were made at runs %v, want one at run 3: the third run in a row that fails at the same position", gapRuns)
	}
	if len(warnsAt) != 1 || warnsAt[0] != 3 {
		t.Errorf("warnings were logged at runs %v, want one at run 3 (quiet until then)", warnsAt)
	}
	if warns := e.logs.find(slog.LevelWarn, "kernel messages not readable"); len(warns) == 1 && warns[0].attrs["err"] != "journal: online: malformed file" {
		t.Errorf("warning err = %q, want %q", warns[0].attrs["err"], "journal: online: malformed file")
	}
	requirePosition(t, e, seqIDB, seqOf(ents[7]), rtOf(ents[7]))
}

func TestKernel_OnlineReadErrorGivenUpUsesTheHeadersTail(t *testing.T) {
	e := newEnv(t)
	var a, b seqCounter
	pre := []entry{a.line(bootB, 1)}
	e.add(pre...)
	ents := []entry{b.line(bootB, 10), b.kill(bootB, 11, 111, "k", lagUS), b.line(bootB, 12)}
	name := e.j.add(t, kindOnline, seqIDB, ents)
	e.seed(dashed(bootB), tailOf(seqIDA, pre))
	failFileReads(e, name)
	for i := range 2 {
		if recs := e.run(false); len(recs) != 0 {
			t.Fatalf("run %d with a read error returned %d records, want none", i+1, len(recs))
		}
	}

	third := e.run(false)

	gaps := gapsOf(third)
	if len(gaps) != 1 {
		t.Fatalf("gaps at the third failing run = %d, want 1", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(pre[0])), micro(rtOf(ents[2])))
	e.j.setHook(nil)
	if again := e.run(false); len(again) != 0 {
		t.Errorf("the run after the give-up returned %d records, want none", len(again))
	}
	requirePosition(t, e, seqIDB, seqOf(ents[2]), rtOf(ents[2]))
}

func TestKernel_GiveUpWithoutAnyTimeUsesTheRunStart(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	_, mono := tl(2)
	e.add(kernelLine(c.next(), bootB, 0, mono, mono-lagUS, "handmade kernel line with a bad time"))
	e.seed(dashed(bootB), &agentstate.JournalPosition{SeqnumID: dashed(seqIDA), Seqnum: 0})
	for i := range maxRereads + 1 {
		e.run(i > 0)
	}
	start := e.start

	fifth := e.run(true)

	var found bool
	for _, g := range gapsOf(fifth) {
		found = found || (g.gap.From.Equal(start) && g.gap.To.Equal(start.Add(time.Microsecond)))
	}
	if !found {
		t.Errorf("gaps = %+v, want the given-up span as [run.Start, run.Start + 1 µs) since neither the position nor an entry has a readable time", gapsOf(fifth))
	}
	checkValid(t, fifth, start)
}

// manyKills returns n kills with PIDs from first, one second apart from sec on, in the entries of c.
func manyKills(c *seqCounter, boot string, sec, first, n int) []entry {
	var out []entry
	for i := range n {
		us := uint64(i) * 1000 // #nosec G115 -- small test values
		rt, mono := tl(sec)
		out = append(out, kernelLine(c.next(), boot, rt+us, mono+us, mono+us-lagUS, killMsg(first+i, "k", 1, 0)))
	}
	return out
}

func TestKernel_OlderFilesAreReadInOrderAndContinuedAfterTheKillLimit(t *testing.T) {
	const (
		idD = "00000000000000000000000000000004"
	)
	e := newEnv(t)
	var a, c, d, f seqCounter
	pre := []entry{a.line(bootB, 20)}
	e.add(pre...)
	// three older files of other IDs: C and D share a head time, so the name decides; F starts earlier
	e.j.add(t, kindArchived, idD, append([]entry{d.line(bootB, 40)}, manyKills(&d, bootB, 41, 2000, 1)...))
	e.j.add(t, kindArchived, seqIDC, append([]entry{c.line(bootB, 40)}, manyKills(&c, bootB, 41, 3000, 1)...))
	e.j.add(t, kindArchived, seqIDB, append([]entry{f.line(bootB, 30)}, manyKills(&f, bootB, 31, 1000, 300)...))
	e.seed(dashed(bootB), tailOf(seqIDA, pre))

	var all []int32
	for range 3 {
		all = append(all, pids(killsOf(e.run(false)))...)
	}

	if len(all) != 302 {
		t.Fatalf("kills = %d, want 302", len(all))
	}
	if all[0] != 1000 || all[299] != 1299 {
		t.Errorf("the first file's kills = %d ... %d, want 1000 ... 1299 in order, continued after the limit of 256 without a repeat", all[0], all[299])
	}
	// the files C (name ...0003) and D (...0004) have the same head time: the name decides
	if all[300] != 3000 || all[301] != 2000 {
		t.Errorf("kills after the first file = %v, want [3000 2000]: the file with the lower name first", all[300:])
	}
}

func TestKernel_OnlineFileOfAnotherIDIsContinuedAfterTheKillLimit(t *testing.T) {
	e := newEnv(t)
	var a, b seqCounter
	pre := []entry{a.line(bootB, 1)}
	e.add(pre...)
	e.j.add(t, kindOnline, seqIDB, append([]entry{b.line(bootB, 9)}, manyKills(&b, bootB, 10, 1, 300)...))
	e.seed(dashed(bootB), tailOf(seqIDA, pre))

	first := pids(killsOf(e.run(false)))
	second := pids(killsOf(e.run(false)))
	third := e.run(false)

	if len(first) != 256 || len(second) != 44 || second[0] != 257 || len(third) != 0 {
		t.Errorf("kills per run = %d, %d (from %v), then %d records, want 256, 44 from 257 and nothing", len(first), len(second), second[:min(1, len(second))], len(third))
	}
}

func TestKernel_SavedPositionThatDoesNotDecodeIsNone(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	e.add(c.line(bootB, 1), c.kill(bootB, 2, 111, "history", lagUS))
	e.seed(dashed(bootB), &agentstate.JournalPosition{SeqnumID: strings.Repeat("z", 36), Seqnum: 5, Realtime: micro(rtAt(1))})

	first := e.run(false)

	if len(killsOf(first)) != 0 || len(gapsOf(first)) != 0 {
		t.Errorf("an undecodable saved position gave %d kills and %d gaps, want none (it starts at the end of the journal like no position)", len(killsOf(first)), len(gapsOf(first)))
	}
}

func TestKernel_FilesOfOneIDAreReadByHeadSeqnumNotByName(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	pre := []entry{c.line(bootB, 1)}
	first := []entry{c.kill(bootB, 2, 111, "first", lagUS)}
	second := []entry{c.kill(bootB, 3, 222, "second", lagUS)}
	e.add(pre...)
	// the file with the higher head seqnum has the lower name
	e.j.put("system@"+seqIDA+"-0000000000000fff-0000000000000001.journal", e.j.dataOf(t, kindArchived, seqIDA, second))
	e.j.put("system@"+seqIDA+"-0000000000000fff-0000000000000002.journal", e.j.dataOf(t, kindArchived, seqIDA, first))
	e.seed(dashed(bootB), tailOf(seqIDA, pre))

	recs := e.run(false)

	if got := pids(killsOf(recs)); len(got) != 2 || got[0] != 111 || got[1] != 222 {
		t.Errorf("kills = %v, want [111 222]: the file with the lower head seqnum first, whatever its name", got)
	}
}

func TestKernel_MissedMessagesEntryFirstAndLast(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	rt1, mono1 := tl(1)
	m1 := missedLine(c.next(), bootB, rt1, mono1)
	l2 := c.line(bootB, 2)
	rt3, mono3 := tl(3)
	m3 := missedLine(c.next(), bootB, rt3, mono3)
	e.add(m1, l2, m3)
	e.seed(dashed(bootB), before())

	recs := e.run(false)

	gaps := gapsOf(recs)
	if len(gaps) != 2 {
		t.Fatalf("gaps = %d, want 2 (one before the first kernel entry, one after the last)", len(gaps))
	}
	requireGap(t, gaps[0], before().Realtime, micro(rtOf(l2)))
	requireGap(t, gaps[1], micro(rtOf(l2)), e.start.Add(-2*time.Second))
}
