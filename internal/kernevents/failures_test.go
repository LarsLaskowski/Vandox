package kernevents

import (
	"bytes"
	"encoding/binary"
	"errors"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/journal"
)

// readableRTs reads the kernel list of a journal file with the reader and returns the real-time stamps of the entries read before the
// list ends or fails: the oracle for what is readable in a damaged file.
func readableRTs(t *testing.T, b []byte, setAside bool) []uint64 {
	t.Helper()
	f, err := journal.Open(bytes.NewReader(b), int64(len(b)), setAside)
	if err != nil {
		t.Fatalf("journal.Open() error = %v, want nil", err)
	}
	d, found, err := f.Lookup([]byte("_TRANSPORT=kernel"))
	if err != nil || !found {
		t.Fatalf("Lookup() = %v, %v, want found", found, err)
	}
	cur, err := f.Seek(d, 0)
	if err != nil {
		t.Fatalf("Seek() error = %v, want nil", err)
	}
	var out []uint64
	var en journal.Entry
	for {
		ok, err := f.Next(&cur, []string{"MESSAGE"}, &en)
		if err != nil || !ok {
			return out
		}
		out = append(out, en.RealtimeUS)
	}
}

// cutInsideTheList returns full cut so that the reader still reads some, but not all, of its total kernel entries.
func cutInsideTheList(t *testing.T, full []byte, total int, setAside bool) (cut []byte, readable []uint64) {
	t.Helper()
	for n := len(full) - 8; n > 512; n -= 8 {
		rts := readableRTs(t, full[:n], setAside)
		if len(rts) >= 4 && len(rts) < total {
			return slices.Clone(full[:n]), rts
		}
	}
	t.Fatalf("no cut of the %d bytes leaves between 4 and %d entries readable", len(full), total-1)
	return nil, nil
}

// corruptEntry returns b with the first item of the entry object with sequence number seq pointing far outside the file.
func corruptEntry(t *testing.T, b []byte, seq uint64) []byte {
	t.Helper()
	out := slices.Clone(b)
	for off := 256; off+72 <= len(out); off += 8 {
		if out[off] == 3 && binary.LittleEndian.Uint64(out[off+16:]) == seq {
			binary.LittleEndian.PutUint64(out[off+64:], 1<<40)
			return out
		}
	}
	t.Fatalf("no entry object with sequence number %d in the file", seq)
	return nil
}

func torn(t *testing.T, full []byte, ents []entry) ([]byte, uint64) {
	cut, rts := cutInsideTheList(t, full, len(ents), true)
	return cut, rts[len(rts)-1]
}

func malformedMiddle(t *testing.T, full []byte, ents []entry) ([]byte, uint64) {
	return corruptEntry(t, full, seqOf(ents[4])), rtOf(ents[3])
}

func TestKernel_TornOrMalformedArchivedFileRecordsGap(t *testing.T) {
	tests := []struct {
		name   string
		kind   fileKind
		damage func(t *testing.T, full []byte, ents []entry) ([]byte, uint64)
	}{
		{"set-aside file cut inside an entry", kindSetAside, torn},
		{"archived file with a malformed entry in the middle", kindArchived, malformedMiddle},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			e := newEnv(t)
			var a, b seqCounter
			ents := []entry{a.line(bootB, 1), a.line(bootB, 2), a.kill(bootB, 3, 111, "k", lagUS), a.line(bootB, 4), a.line(bootB, 5),
				a.line(bootB, 6), a.line(bootB, 7), a.kill(bootB, 8, 112, "k", lagUS)}
			name := e.j.add(t, tc.kind, seqIDA, ents)
			bad, lastReadable := tc.damage(t, e.j.data(name), ents)
			e.j.put(name, bad)
			next := []entry{b.line(bootB, 20), b.kill(bootB, 21, 222, "next", lagUS)}
			e.j.add(t, kindOnline, seqIDB, next)
			e.seed(dashed(bootB), before())

			recs := e.run(false)

			if got := pids(killsOf(recs)); !slices.Equal(got, []int32{111, 222}) {
				t.Errorf("kills = %v, want [111 222] (the entries before the break, then the next file; not the kill 112 after it)", got)
			}
			gaps := gapsOf(recs)
			if len(gaps) != 1 {
				t.Fatalf("gaps = %d, want 1", len(gaps))
			}
			requireGap(t, gaps[0], micro(lastReadable), micro(rtOf(ents[7])))
			checkValid(t, recs, t0)
			if again := e.run(false); len(again) != 0 {
				t.Errorf("the next run returned %d records, want none and no further gap", len(again))
			}
			requirePosition(t, e, seqIDB, seqOf(next[1]), rtOf(next[1]))
		})
	}
}

// garble replaces the signature of a stored file.
func garble(e *env, name string) {
	b := e.j.data(name)
	copy(b, "NOTAJRNL")
	e.j.put(name, b)
}

func TestKernel_UnsupportedFileRecordsGap(t *testing.T) {
	t.Run("archived file", unsupportedArchived)
	t.Run("online file", unsupportedOnline)
}

func unsupportedArchived(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	pre := []entry{c.line(bootB, 1), c.line(bootB, 2)}
	e.add(pre...)
	garble(e, e.add(c.line(bootB, 3), c.line(bootB, 4)))
	e.add(c.kill(bootB, 5, 222, "after", lagUS))
	e.seed(dashed(bootB), tailOf(seqIDA, pre))
	start := e.start

	first := e.run(false)

	gaps := gapsOf(first)
	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1 for the file the reader refuses", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(pre[1])), start)
	if got := pids(killsOf(first)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills = %v, want [222] (the other files are read on)", got)
	}
	checkValid(t, first, start)
	for i := range 3 {
		if recs := e.run(i == 1); len(gapsOf(recs)) != 0 {
			t.Errorf("run %d after the first made %d gaps, want none (one gap per process)", i+2, len(gapsOf(recs)))
		}
	}
}

func unsupportedOnline(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	pre := []entry{c.line(bootB, 1)}
	e.add(pre...)
	e.j.add(t, kindOnline, seqIDB, []entry{c.line(bootB, 2)})
	garble(e, "system.journal")
	e.seed(dashed(bootB), tailOf(seqIDA, pre))
	first := e.start

	gaps := gapsOf(e.run(false))

	if len(gaps) != 1 {
		t.Fatalf("gaps = %d, want 1", len(gaps))
	}
	requireGap(t, gaps[0], micro(rtOf(pre[0])), first)
	last := first
	for _, after := range []time.Duration{2 * time.Second, 598 * time.Second, 600 * time.Second, 602 * time.Second, 1200 * time.Second} {
		e.start = first.Add(after)
		now := e.start
		got := gapsOf(e.run(false))
		due := now.Sub(last) >= 600*time.Second
		if len(got) != btoi(due) {
			t.Errorf("run %s after the first: %d gaps (a gap was due: %v), want a gap only at least 600 s after the last one", after, len(got), due)
			continue
		}
		if due {
			requireGap(t, got[0], last, now)
			last = now
		}
	}
}

func btoi(b bool) int {
	if b {
		return 1
	}
	return 0
}

// readErrorEnv returns an environment whose first file of the position's seqnum ID fails to read beyond its header once failing is on.
func readErrorEnv(t *testing.T) (e *env, failFile string, f1, f2 []entry) {
	t.Helper()
	e = newEnv(t)
	var c seqCounter
	f1 = []entry{c.line(bootB, 1), c.kill(bootB, 2, 111, "k", lagUS), c.line(bootB, 3)}
	f2 = []entry{c.kill(bootB, 4, 222, "k2", lagUS)}
	failFile = e.add(f1...)
	e.add(f2...)
	e.seed(dashed(bootB), before())
	return e, failFile, f1, f2
}

// failFileReads makes the reads of the named file beyond its header fail.
func failFileReads(e *env, name string) {
	e.j.setHook(func(op, file string, off int64) error {
		if op == "read" && off >= 256 && strings.HasSuffix(file, name) {
			return errors.New("read /secret-path: input/output error")
		}
		return nil
	})
}

func TestKernel_ReadErrorRetriedThenGivenUp(t *testing.T) {
	t.Run("given up at the third run in a row", readErrorGivenUp)

	for _, bad := range []int{1, 2} {
		t.Run("a file that opens again after "+strconv.Itoa(bad)+" failing runs loses nothing", func(t *testing.T) {
			e, name, _, _ := readErrorEnv(t)
			failFileReads(e, name)
			for range bad {
				e.run(false)
			}
			e.j.setHook(nil)

			recs := e.run(false)

			if got := pids(killsOf(recs)); !slices.Equal(got, []int32{111, 222}) || len(gapsOf(recs)) != 0 {
				t.Errorf("kills = %v, gaps = %d, want [111 222] and no gap", got, len(gapsOf(recs)))
			}
		})
	}
}

func readErrorGivenUp(t *testing.T) {
	e, name, f1, f2 := readErrorEnv(t)
	failFileReads(e, name)
	for i := range 2 {
		if recs := e.run(false); len(recs) != 0 {
			t.Fatalf("run %d with a read error returned %d records, want none (the files after it are not read, no gap)", i+1, len(recs))
		}
	}

	third := e.run(false)

	gaps := gapsOf(third)
	if len(gaps) != 1 {
		t.Fatalf("gaps at the third failing run = %d, want 1", len(gaps))
	}
	requireGap(t, gaps[0], before().Realtime, micro(rtOf(f1[2])))
	if got := pids(killsOf(third)); !slices.Equal(got, []int32{222}) {
		t.Errorf("kills at the third failing run = %v, want [222] (reading goes on)", got)
	}
	if again := e.run(false); len(again) != 0 {
		t.Errorf("the run after it returned %d records, want none", len(again))
	}
	requirePosition(t, e, seqIDA, seqOf(f2[0]), rtOf(f2[0]))
}
