package spool

import (
	"context"
	"encoding/binary"
	"fmt"
	"log/slog"
	"math"
	"os"
	"path/filepath"
	"runtime"
	"slices"
	"strings"
	"syscall"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	msgDamaged      = "spool data damaged"
	errChecksum     = "checksum mismatch"
	errCutShort     = "file cut short"
	errNotFormat    = "not the spool format"
	errVersion      = "unknown format version"
	errInconsistent = "inconsistent value"
	errRefused      = "refused entry"
	errTooMany      = "too many files"
)

// damage describes one way the middle file b of a world is damaged and what the spool must make of it.
type damage struct {
	name string
	// apply returns the name and content of the damaged file that replaces b.
	apply func(t *testing.T, w world) (string, []byte)
	// extra forges more files before the spool opens.
	extra func(t *testing.T, w world, dir string)
	// handed are the numbers of b that may be handed out: exactly these.
	handed func(w world) []uint64
	// anyPrefix accepts any prefix of b's numbers instead of handed.
	anyPrefix bool
	// gap is the run the spool_dropped gap must name, when it is known.
	gap func(w world) *[2]uint64
	// span is the time span the gap must have.
	span func(w world) *[2]time.Time
	// errs are the accepted reasons.
	errs []string
	// file is the name the warning must name; empty means the replaced file's name.
	file func(w world) string
	// after runs on the open spool.
	after func(t *testing.T, w world, sp *Spool)
}

func firstN(w world, n int) []uint64 { return rangeNumbers(w.b.first, w.b.first+ofLenInt(n)-1) }

func noneHanded(world) []uint64 { return nil }

func wholeB(w world) *[2]uint64 { return &[2]uint64{w.b.first, w.b.last()} }

func fromRecord(k int) func(w world) *[2]uint64 {
	return func(w world) *[2]uint64 { return &[2]uint64{w.b.first + ofLenInt(k), w.b.last()} }
}

func goodSpan(k int) func(w world) *[2]time.Time {
	return func(w world) *[2]time.Time { return &[2]time.Time{w.b.at(0), w.b.at(k - 1)} }
}

// installDamage replaces b of the world with the damaged file of the case and returns the damaged file's name.
func installDamage(t *testing.T, dir string, w world, dc damage) string {
	t.Helper()

	name, data := dc.apply(t, w)
	needNoErr(t, "Remove(b)", os.Remove(filepath.Join(dir, w.b.name())))
	writeSegment(t, dir, name, data)
	if dc.extra != nil {
		dc.extra(t, w, dir)
	}

	return name
}

// requireHanded checks the numbers handed out against the case: all of a and c, and of b exactly the numbers the case
// names, or any prefix of b's numbers.
func requireHanded(t *testing.T, dc damage, w world, got []uint64) {
	t.Helper()

	wantA, wantC := rangeNumbers(w.a.first, w.a.last()), rangeNumbers(w.c.first, w.c.last())
	if !dc.anyPrefix {
		handed := noneHanded
		if dc.handed != nil {
			handed = dc.handed
		}
		expectNumbers(t, "numbers handed out", got, concat(wantA, handed(w), wantC))

		return
	}
	need(t, len(got) >= len(wantA)+len(wantC), "numbers handed out = %v, want all of the other files", headOf(got, 12))
	middle := got[len(wantA) : len(got)-len(wantC)]
	expectNumbers(t, "numbers of the other files", concat(got[:len(wantA)], got[len(got)-len(wantC):]), concat(wantA, wantC))
	expectNumbers(t, "numbers handed out from the damaged file (a prefix of its numbers)", middle, rangeNumbers(w.b.first, w.b.last())[:len(middle)])
}

// requireDamagedLine checks the one warning that names the damaged file.
func requireDamagedLine(t *testing.T, r *rig, dc damage, file string) {
	t.Helper()

	lines := r.logs.find(msgDamaged)
	need(t, len(lines) == 1, "%d lines %q = %v, want one for the file", len(lines), msgDamaged, lines)
	expect(t, lines[0].level == slog.LevelWarn && lines[0].str("file") == file && slices.Contains(dc.errs, lines[0].str("err")),
		"%q has level %v, file %q, err %q, want a warning for %q with err in %v", msgDamaged, lines[0].level, lines[0].str("file"), lines[0].str("err"), file, dc.errs)
}

// runDamage forges the world with the damaged file in place of b, opens the spool and checks the outcome.
func runDamage(t *testing.T, dc damage) {
	t.Helper()

	r := newRig(t)
	w := newWorld(t, r.dir())
	name := installDamage(t, r.dir(), w, dc)
	wantFile := name
	if dc.file != nil {
		wantFile = dc.file(w)
	}

	var before, after runtime.MemStats
	runtime.ReadMemStats(&before)
	sp := r.open()
	runtime.ReadMemStats(&after)
	expect(t, after.TotalAlloc-before.TotalAlloc <= 64<<20, "Open allocated %d bytes for a directory of 3 small files, want a damaged length never allocated", after.TotalAlloc-before.TotalAlloc)

	readAll(t, sp) // damage in a sealed file is found when it is read, and its gap is written then
	recs := readAll(t, sp)
	requireHanded(t, dc, w, seqs(dataOf(recs)))
	requireDamagedLine(t, r, dc, wantFile)
	requireGapsDisjoint(t, recs)
	checkDamageGap(t, dc, w, recs)
	requireNoLeak(t, r)
	if dc.after != nil {
		dc.after(t, w, sp)
	}
}

// checkDamageGap compares the spool_dropped gaps with the run and span the case names.
func checkDamageGap(t *testing.T, dc damage, w world, recs []model.Record) {
	t.Helper()

	gaps := gapsOf(recs, model.GapSpoolDropped)
	if dc.gap == nil {
		return
	}
	want := dc.gap(w)
	if len(gaps) != 1 {
		t.Fatalf("%d spool_dropped gaps are kept, want one for %d to %d", len(gaps), want[0], want[1])
	}
	gp := gapOf(gaps[0])
	if gp.FirstSeq != want[0] || gp.LastSeq != want[1] {
		t.Errorf("gap covers %d to %d, want %d to %d", gp.FirstSeq, gp.LastSeq, want[0], want[1])
	}
	if gaps[0].Source != "agent" {
		t.Errorf("gap source = %q, want agent", gaps[0].Source)
	}
	if dc.span != nil {
		span := dc.span(w)
		if !gp.From.Equal(span[0]) || !gp.To.Equal(span[1]) {
			t.Errorf("gap spans %v to %v, want %v to %v (the good records only)", gp.From, gp.To, span[0], span[1])
		}
	}
}

func damagedDataCases() []damage {
	return []damage{
		{
			name: "a flipped byte",
			apply: func(t *testing.T, w world) (string, []byte) {
				sb := encodeSegment(t, w.b.spec())
				sb.data[sb.frameStart[4]+8+8+5] ^= 0xFF

				return w.b.name(), sb.data
			},
			handed: func(w world) []uint64 { return firstN(w, 4) },
			gap:    fromRecord(4), span: goodSpan(4), errs: []string{errChecksum},
		},
		{
			name: "a file cut short",
			apply: func(t *testing.T, w world) (string, []byte) {
				sb := encodeSegment(t, w.b.spec())

				return w.b.name(), sb.data[:sb.frameStart[5]+3]
			},
			handed: func(w world) []uint64 { return firstN(w, 5) },
			gap:    fromRecord(5), errs: []string{errCutShort},
		},
		{
			name: "foreign bytes",
			apply: func(_ *testing.T, w world) (string, []byte) {
				return w.b.name(), []byte(strings.Repeat("this is not a spool file. ", 12))
			},
			gap: wholeB, errs: []string{errNotFormat},
		},
		{
			name: "an unknown format version",
			apply: func(t *testing.T, w world) (string, []byte) {
				s := w.b.spec()
				s.version = 2

				return w.b.name(), encodeSegment(t, s).data
			},
			gap: wholeB, errs: []string{errVersion},
		},
		{
			name: "a frame length beyond the limit",
			apply: func(t *testing.T, w world) (string, []byte) {
				sb := encodeSegment(t, w.b.spec())
				data := sb.data[:sb.frameStart[3]]
				data = binary.LittleEndian.AppendUint32(data, 0x7FFFFFF0)
				data = binary.LittleEndian.AppendUint32(data, 0)

				return w.b.name(), append(data, strings.Repeat("x", 64)...)
			},
			handed: func(w world) []uint64 { return firstN(w, 3) },
			gap:    fromRecord(3), errs: []string{errChecksum, errCutShort, errInconsistent},
		},
	}
}

func TestOpen_DamagedData(t *testing.T) {
	for _, tc := range damagedDataCases() {
		t.Run(tc.name, func(t *testing.T) { runDamage(t, tc) })
	}
}

func inconsistentValueCases() []damage {
	at := func(year int) time.Time { return time.Date(year, time.January, 1, 0, 0, 0, 0, time.UTC) }
	withRecord := func(k int, mutate func(w world, r *model.Record)) func(t *testing.T, w world) (string, []byte) {
		return func(t *testing.T, w world) (string, []byte) {
			s := w.b.spec()
			mutate(w, &s.frames[k].rec)

			return w.b.name(), encodeSegment(t, s).data
		}
	}
	return []damage{
		{
			name: "a file name that is not the number of its first record",
			apply: func(t *testing.T, w world) (string, []byte) {
				return segName(w.b.first + 3), w.b.bytes(t)
			},
			errs: []string{errInconsistent},
		},
		{
			name: "numbers that do not increase within a file",
			apply: func(t *testing.T, w world) (string, []byte) {
				s := w.b.spec()
				s.frames[3].rec.Seq = w.b.first + 2

				return w.b.name(), encodeSegment(t, s).data
			},
			handed: func(w world) []uint64 { return firstN(w, 3) },
			gap:    fromRecord(3), errs: []string{errInconsistent},
		},
		{
			name: "numbers that do not increase across files",
			apply: func(t *testing.T, w world) (string, []byte) {
				f := fileSpec{first: w.a.first + 4, n: forgedFileSize, sealed: true, boot: otherBoot, capture: w.b.capture, tag: "x"}

				return f.name(), f.bytes(t)
			},
			errs: []string{errInconsistent},
		},
		{
			name: "a record number that differs from its line's",
			apply: func(t *testing.T, w world) (string, []byte) {
				s := w.b.spec()
				s.frames[4].frameSeq = w.b.first + 4
				s.frames[4].rec.Seq = w.b.first + 11

				return w.b.name(), encodeSegment(t, s).data
			},
			handed: func(w world) []uint64 { return firstN(w, 4) },
			gap:    fromRecord(4), errs: []string{errInconsistent},
		},
		{
			name: "a summary whose last number disagrees with the last record",
			apply: func(t *testing.T, w world) (string, []byte) {
				s := w.b.spec()
				s.trailer.last += 3

				return w.b.name(), encodeSegment(t, s).data
			},
			anyPrefix: true, errs: []string{errInconsistent, errChecksum},
		},
		{
			name: "a summary whose count disagrees with the records",
			apply: func(t *testing.T, w world) (string, []byte) {
				s := w.b.spec()
				s.trailer.count--

				return w.b.name(), encodeSegment(t, s).data
			},
			anyPrefix: true, errs: []string{errInconsistent, errChecksum},
		},
		{
			name: "a capture time in the year 3000",
			apply: withRecord(4, func(_ world, r *model.Record) {
				r.CapturedAt = at(3000)
			}),
			handed: func(w world) []uint64 { return firstN(w, 4) },
			gap:    fromRecord(4), span: goodSpan(4), errs: []string{errInconsistent},
		},
		{
			name: "a capture time in the year 1600",
			apply: withRecord(4, func(_ world, r *model.Record) {
				r.CapturedAt = at(1600)
			}),
			handed: func(w world) []uint64 { return firstN(w, 4) },
			gap:    fromRecord(4), span: goodSpan(4), errs: []string{errInconsistent},
		},
		{
			name: "a gap record that ends in the year 3000",
			apply: withRecord(4, func(w world, r *model.Record) {
				r.Source = "agent"
				r.Data = &model.Gap{From: w.b.at(4), To: at(3000), Cause: model.GapSpoolDropped, FirstSeq: 1, LastSeq: 2}
			}),
			handed: func(w world) []uint64 { return firstN(w, 4) },
			gap:    fromRecord(4), span: goodSpan(4), errs: []string{errInconsistent},
		},
		{
			name: "a file named with the largest number",
			apply: func(t *testing.T, w world) (string, []byte) {
				return w.b.name(), w.b.bytes(t)
			},
			extra: func(t *testing.T, _ world, dir string) {
				f := fileSpec{first: math.MaxInt64, n: 2, boot: testBoot, capture: epoch.Add(time.Hour), tag: "max"}
				writeSegment(t, dir, f.name(), f.bytes(t))
			},
			handed: func(w world) []uint64 { return firstN(w, forgedFileSize) },
			errs:   []string{errInconsistent},
			file:   func(world) string { return segName(math.MaxInt64) },
			after: func(t *testing.T, w world, sp *Spool) {
				mustPut(t, sp, []model.Record{logRec(epoch.Add(2*time.Hour), "next", 100)})
				if got := sp.LastSeq(); got <= w.c.last() || got >= math.MaxInt64 {
					t.Errorf("the number issued after a damaged file named %d is %d, want one just above %d and below 2^63-1", uint64(math.MaxInt64), got, w.c.last())
				}
			},
		},
	}
}

func TestOpen_InconsistentValues(t *testing.T) {
	for _, tc := range inconsistentValueCases() {
		t.Run(tc.name, func(t *testing.T) { runDamage(t, tc) })
	}
}

// readDamageCase damages file b of an open spool's world and names what the read must make of it.
type readDamageCase struct {
	name   string
	damage func(t *testing.T, w world)
	kept   int
	err    string
}

func readDamageCases() []readDamageCase {
	return []readDamageCase{
		{"a flipped byte", func(t *testing.T, w world) {
			sb := encodeSegment(t, w.b.spec())
			flipAt(t, filepath.Join(w.dir, w.b.name()), int64(sb.frameStart[4]+8+8+5))
		}, 4, errChecksum},
		{"a file cut short", func(t *testing.T, w world) {
			sb := encodeSegment(t, w.b.spec())
			needNoErr(t, "Truncate", os.Truncate(filepath.Join(w.dir, w.b.name()), int64(sb.frameStart[6]+3)))
		}, 6, errCutShort},
	}
}

func runReadDamaged(t *testing.T, tc readDamageCase) {
	t.Helper()

	r := newRig(t)
	w := newWorld(t, r.dir())
	sp := r.open()
	need(t, r.logs.count(msgDamaged) == 0, "Open logged %q for undamaged files", msgDamaged)
	tc.damage(t, w)

	want := concat(rangeNumbers(w.a.first, w.a.last()), firstN(w, tc.kept), rangeNumbers(w.c.first, w.c.last()))
	expectNumbers(t, "numbers handed out (the records before the damage, and the other files)", seqs(dataOf(readAll(t, sp))), want)
	recs := readAll(t, sp)
	expectNumbers(t, "numbers handed out on the second read", seqs(dataOf(recs)), want)
	gaps := gapsOf(recs, model.GapSpoolDropped)
	need(t, len(gaps) == 1, "%d spool_dropped gaps are kept after the damage was found, want 1", len(gaps))
	gp := gapOf(gaps[0])
	wantFirst := w.b.first + ofLenInt(tc.kept)
	expect(t, gp.FirstSeq == wantFirst && gp.LastSeq == w.b.last(), "gap covers %d to %d, want %d to %d", gp.FirstSeq, gp.LastSeq, wantFirst, w.b.last())
	lines := r.logs.find(msgDamaged)
	need(t, len(lines) == 1, "%d lines %q, want one warning per file, not per read", len(lines), msgDamaged)
	expect(t, lines[0].str("file") == w.b.name() && lines[0].str("err") == tc.err && lines[0].level == slog.LevelWarn, "%q = %v, want a warning for %s with err %q", msgDamaged, lines[0], w.b.name(), tc.err)
	requireGapsDisjoint(t, recs)

	mustAck(t, sp, w.a.first, sp.LastSeq())
	expect(t, !slices.Contains(segmentNumbers(t, r.dir()), w.b.first), "the damaged file %s is still on disk after everything readable was acknowledged, want it deleted", w.b.name())
}

func TestRead_DamagedData(t *testing.T) {
	for _, tc := range readDamageCases() {
		t.Run(tc.name, func(t *testing.T) { runReadDamaged(t, tc) })
	}
}

// flipAt inverts the byte at offset in the file.
func flipAt(t *testing.T, path string, offset int64) {
	t.Helper()

	f, err := os.OpenFile(path, os.O_RDWR, 0)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = f.Close() }()
	b := make([]byte, 1)
	if _, err := f.ReadAt(b, offset); err != nil {
		t.Fatal(err)
	}
	b[0] ^= 0xFF
	if _, err := f.WriteAt(b, offset); err != nil {
		t.Fatal(err)
	}
}

// rigCopy returns a rig over a copy of src's directory, with its own clock one minute later, its own log and the
// boot ID otherBoot, as the start after a crash and a reboot.
func rigCopy(r *rig, dir string) *rig {
	c := *r
	c.opts.Dir = dir
	c.opts.BootID = otherBoot
	c.clk = &fakeClock{now: epoch.Add(time.Minute)}
	c.opts.Now = c.clk.Now
	c.logs = newLogRecorder()
	c.closed = map[*Spool]bool{}

	return &c
}

// requirePrefixAndNewNumbers opens the rig and checks that what is handed out is a prefix of want, unchanged, that
// the prefix has at least minKept records, and that a new record gets a number above every number of want and above
// highest. It returns the number of records kept.
func requirePrefixAndNewNumbers(t *testing.T, c *rig, want []model.Record, highest uint64) int {
	t.Helper()

	sp := c.open()
	got := dataOf(readAll(t, sp))
	if len(got) > len(want) {
		t.Fatalf("%d records are handed out, only %d were written", len(got), len(want))
	}
	requireSame(t, "records handed out after the damage", got, want[:len(got)])
	mustPut(t, sp, []model.Record{logRec(epoch.Add(2*time.Hour), "after", 100)})
	if sp.LastSeq() <= highest {
		t.Fatalf("the first number after the damage is %d, want a number above %d, the highest issued before", sp.LastSeq(), highest)
	}
	c.closeSpool(sp)

	again := c.open()
	rest := dataOf(readAll(t, again))
	if len(rest) != len(got)+1 {
		t.Fatalf("after a restart %d records are kept, want the %d from before and the new one", len(rest), len(got))
	}

	return len(got)
}

// cutOffsets returns the offsets to cut a file of the given length at: every fifth byte of the header region, then
// every 61st, and the last two.
func cutOffsets(length int) []int {
	var offsets []int
	for off := 0; off < min(160, length); off += 5 {
		offsets = append(offsets, off)
	}
	for off := 160; off < length; off += 61 {
		offsets = append(offsets, off)
	}

	return append(offsets, length-1, length)
}

// cutAndRecover cuts the file name of a copy of the killed directory to off bytes and recovers the copy.
func cutAndRecover(t *testing.T, killed *rig, name string, off int, all []model.Record) int {
	t.Helper()

	c := rigCopy(killed, copyDir(t, killed.dir()))
	needNoErr(t, "Truncate", os.Truncate(filepath.Join(c.dir(), name), int64(off)))
	kept := requirePrefixAndNewNumbers(t, c, all, all[len(all)-1].Seq)
	for _, e := range c.logs.find(msgDamaged) {
		expect(t, slices.Contains([]string{errChecksum, errCutShort, errNotFormat, errInconsistent}, e.str("err")), "cut at %d: %q has err %q, want a fixed reason", off, msgDamaged, e.str("err"))
	}

	return kept
}

func TestOpen_CutFiles(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	var all []model.Record
	for i, size := range []int{50, 120, 300, 40, 220, 90} {
		all = append(all, putTagged(t, sp, epoch.Add(time.Duration(i)*time.Second), fmt.Sprintf("cut%d", i), 5, size)...)
	}
	killed := r.snapshot()
	names := segmentNumbers(t, killed.dir())
	need(t, len(names) == 1, "%d data files, want 1", len(names))
	segFile := segName(names[0])
	data, err := os.ReadFile(filepath.Join(killed.dir(), segFile))
	needNoErr(t, "ReadFile", err)

	prev := 0
	for _, off := range cutOffsets(len(data)) {
		kept := cutAndRecover(t, killed, segFile, off, all)
		need(t, kept >= prev, "cut at %d keeps %d records, fewer than the %d kept by a shorter cut", off, kept, prev)
		need(t, off != 0 || kept == 0, "a file cut to %d bytes keeps %d records, want none", off, kept)
		prev = kept
	}
	need(t, prev == len(all), "an uncut file keeps %d records, want %d", prev, len(all))
}

func TestOpen_CutMetaFile(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	var all []model.Record
	for i := range 4 {
		all = append(all, putTagged(t, sp, epoch.Add(time.Duration(i)*time.Second), fmt.Sprintf("mc%d", i), 5, 100)...)
	}
	killed := r.snapshot()
	meta, err := os.ReadFile(filepath.Join(killed.dir(), metaName))
	needNoErr(t, "ReadFile(meta)", err)
	for off := 0; off < len(meta); off += 7 {
		kept := cutAndRecover(t, killed, metaName, off, all)
		need(t, kept == len(all), "meta file cut to %d bytes: %d records kept, want all %d (the data files are intact)", off, kept, len(all))
	}
}

func TestOpen_RolledBackDirectory(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	early := putTagged(t, sp, epoch, "early", 10, 100)
	older := r.snapshot().dir()
	late := putTagged(t, sp, epoch.Add(time.Minute), "late", 10, 100)
	newer := r.snapshot().dir()
	all := concat2(early, late)
	highest := late[len(late)-1].Seq
	name := segName(early[0].Seq)

	read := func(dir, file string) []byte {
		b, err := os.ReadFile(filepath.Join(dir, file))
		if err != nil {
			t.Fatal(err)
		}

		return b
	}
	tests := []struct {
		name  string
		build func(dst string)
		kept  int
	}{
		{"the whole directory at an older state", func(dst string) {
			writeSegment(t, dst, name, read(older, name))
			writeSegment(t, dst, metaName, read(older, metaName))
		}, 10},
		{"the files of the newer state with an older meta file", func(dst string) {
			writeSegment(t, dst, name, read(newer, name))
			writeSegment(t, dst, metaName, read(older, metaName))
		}, 20},
		{"the files of the older state with a newer meta file", func(dst string) {
			writeSegment(t, dst, name, read(older, name))
			writeSegment(t, dst, metaName, read(newer, metaName))
		}, 10},
		{"the data file lost and the newer meta file kept", func(dst string) {
			writeSegment(t, dst, metaName, read(newer, metaName))
		}, 0},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			dst := t.TempDir()
			tc.build(dst)
			if kept := requirePrefixAndNewNumbers(t, rigCopy(r, dst), all, highest); kept != tc.kept {
				t.Errorf("%d records are kept, want %d", kept, tc.kept)
			}
		})
	}
}

// entryKind makes a refused entry at path; content is what a valid file of that name would hold.
type entryKind struct {
	name      string
	needsRoot bool
	make      func(t *testing.T, rw *refusedWorld, path string, content []byte)
}

// refusedWorld is a world whose agent-state.json and a file outside the directory must stay untouched.
type refusedWorld struct {
	dir         string
	statePath   string
	outsidePath string
}

func (rw *refusedWorld) writeContent(t *testing.T, path string, content []byte) {
	t.Helper()

	if err := os.WriteFile(path, content, 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(path, 0o600); err != nil {
		t.Fatal(err)
	}
}

var refusedKinds = []entryKind{
	{name: "symbolic link to agent-state.json", make: func(t *testing.T, _ *refusedWorld, path string, _ []byte) {
		if err := os.Symlink(stateName, path); err != nil {
			t.Fatal(err)
		}
	}},
	{name: "symbolic link outside the directory", make: func(t *testing.T, rw *refusedWorld, path string, _ []byte) {
		if err := os.Symlink(rw.outsidePath, path); err != nil {
			t.Fatal(err)
		}
	}},
	{name: "hard link to agent-state.json", make: func(t *testing.T, rw *refusedWorld, path string, _ []byte) {
		if err := os.Link(rw.statePath, path); err != nil {
			t.Fatal(err)
		}
	}},
	{name: "fifo", make: func(t *testing.T, _ *refusedWorld, path string, _ []byte) {
		if err := syscall.Mkfifo(path, 0o600); err != nil {
			t.Fatal(err)
		}
	}},
	{name: "directory", make: func(t *testing.T, _ *refusedWorld, path string, _ []byte) {
		if err := os.Mkdir(path, 0o700); err != nil {
			t.Fatal(err)
		}
	}},
	{name: "mode 0640", make: func(t *testing.T, rw *refusedWorld, path string, content []byte) {
		rw.writeContent(t, path, content)
		if err := os.Chmod(path, 0o640); err != nil {
			t.Fatal(err)
		}
	}},
	{name: "owned by another user", needsRoot: true, make: func(t *testing.T, rw *refusedWorld, path string, content []byte) {
		rw.writeContent(t, path, content)
		if err := os.Chown(path, 65534, 65534); err != nil {
			t.Fatal(err)
		}
	}},
}

// entryState is what a guard test remembers of a refused entry.
type entryState struct {
	info   os.FileInfo
	target string
	stat   stat
	uid    uint32
	count  int
}

func captureEntry(t *testing.T, path string) entryState {
	t.Helper()

	info, err := os.Lstat(path)
	if err != nil {
		t.Fatalf("Lstat(%s) = %v", filepath.Base(path), err)
	}
	e := entryState{info: info, stat: statOf(t, path)}
	if sys, ok := info.Sys().(*syscall.Stat_t); ok {
		e.uid = sys.Uid
	}
	if info.Mode()&os.ModeSymlink != 0 {
		if e.target, err = os.Readlink(path); err != nil {
			t.Fatal(err)
		}
	}
	if info.IsDir() {
		entries, err := os.ReadDir(path)
		if err != nil {
			t.Fatal(err)
		}
		e.count = len(entries)
	}

	return e
}

func requireEntryUnchanged(t *testing.T, what, path string, before entryState) {
	t.Helper()

	after := captureEntry(t, path)
	if !os.SameFile(before.info, after.info) || before.info.Mode() != after.info.Mode() || before.target != after.target || before.uid != after.uid || before.count != after.count {
		t.Fatalf("%s changed: mode %v -> %v, target %q -> %q, owner %d -> %d, entries %d -> %d, same inode %v",
			what, before.info.Mode(), after.info.Mode(), before.target, after.target, before.uid, after.uid, before.count, after.count, os.SameFile(before.info, after.info))
	}
	if before.info.Mode().IsRegular() {
		requireUnchanged(t, what, path, before.stat)
	}
}

// openWithin opens the rig's spool and fails the test when Open does not return, as it would on a FIFO.
func openWithin(t *testing.T, r *rig, what string) (*Spool, error) {
	t.Helper()

	type result struct {
		sp  *Spool
		err error
	}
	done := make(chan result, 1)
	go func() {
		sp, err := Open(r.opts, slog.New(r.logs))
		done <- result{sp, err}
	}()
	select {
	case res := <-done:
		if res.sp != nil {
			t.Cleanup(func() {
				if !r.closed[res.sp] {
					_ = res.sp.Close(context.Background())
				}
			})
		}

		return res.sp, res.err
	case <-time.After(30 * time.Second):
		t.Fatalf("Open did not return with %s in the directory", what)

		return nil, nil
	}
}

func TestOpen_RefusedEntries(t *testing.T) {
	names := []struct {
		label string
		meta  bool
		name  func(w world) string
	}{
		{"the newest data file", false, func(w world) string { return w.c.name() }},
		{"an older data file", false, func(w world) string { return w.b.name() }},
		{"the meta file", true, func(world) string { return metaName }},
		{"the meta temporary file", true, func(world) string { return metaTmpName }},
	}
	for _, n := range names {
		for _, k := range refusedKinds {
			t.Run(n.label+"/"+k.name, func(t *testing.T) {
				if k.needsRoot && os.Geteuid() != 0 {
					t.Skip("changing the owner of a file needs root")
				}
				runRefused(t, n.label, n.meta, n.name, k)
			})
		}
	}
}

func runRefused(t *testing.T, label string, meta bool, name func(w world) string, kind entryKind) {
	t.Helper()

	r := newRig(t)
	w := newWorld(t, r.dir())
	rw := &refusedWorld{dir: r.dir(), statePath: filepath.Join(r.dir(), stateName), outsidePath: filepath.Join(t.TempDir(), "outside.txt")}
	writeSegment(t, rw.dir, stateName, []byte(`{"agent":"state","positions":[1,2,3]}`))
	writeSegment(t, filepath.Dir(rw.outsidePath), "outside.txt", []byte("a file of the agent's user outside the spool"))

	entry := filepath.Join(rw.dir, name(w))
	content, err := os.ReadFile(entry)
	if os.IsNotExist(err) {
		content = metaJSON(metaSpec{agent: testAgent, boot: testBoot, next: w.c.last() + 1})
	} else if err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(entry); err != nil && !os.IsNotExist(err) {
		t.Fatal(err)
	}
	kind.make(t, rw, entry, content)
	stateBefore, outsideBefore, entryBefore := statOf(t, rw.statePath), statOf(t, rw.outsidePath), captureEntry(t, entry)

	sp, err := openWithin(t, r, kind.name)
	if meta {
		if err == nil {
			t.Fatalf("Open with a %s as %s = nil, want an error", kind.name, label)
		}
		if !strings.HasPrefix(err.Error(), "spool: ") || !strings.Contains(err.Error(), errRefused) {
			t.Errorf("Open error = %q, want it to start with %q and name %q", err.Error(), "spool: ", errRefused)
		}
		requireNoPath(t, "error", err.Error(), rw.dir)
	} else {
		if err != nil {
			t.Fatalf("Open with a %s as %s = %v, want a spool that treats the data as damaged", kind.name, label, err)
		}
		checkRefusedData(t, r, sp, w, name(w))
	}

	requireUnchanged(t, stateName, rw.statePath, stateBefore)
	requireUnchanged(t, "the file outside the directory", rw.outsidePath, outsideBefore)
	requireEntryUnchanged(t, "the refused "+kind.name, entry, entryBefore)
	requireNoLeak(t, r)
}

// checkRefusedData checks a spool that opened over a refused entry in place of the data file refused.
func checkRefusedData(t *testing.T, r *rig, sp *Spool, w world, refused string) {
	t.Helper()

	var want []uint64
	older := refused == w.b.name()
	if older {
		want = concat(rangeNumbers(w.a.first, w.a.last()), rangeNumbers(w.c.first, w.c.last()))
	} else {
		want = concat(rangeNumbers(w.a.first, w.a.last()), rangeNumbers(w.b.first, w.b.last()))
	}
	recs := readAll(t, sp)
	if got := seqs(dataOf(recs)); !slices.Equal(got, want) {
		t.Errorf("numbers handed out = %v, want %v (nothing from the refused entry)", got, want)
	}
	lines := r.logs.find(msgDamaged)
	if len(lines) != 1 || lines[0].level != slog.LevelWarn || lines[0].str("file") != refused || lines[0].str("err") != errRefused {
		t.Errorf("log lines %q = %v, want one warning for %s with err %q", msgDamaged, lines, refused, errRefused)
	}
	requireGapsDisjoint(t, recs)
	if older {
		gaps := gapsOf(recs, model.GapSpoolDropped)
		if len(gaps) != 1 || gapOf(gaps[0]).FirstSeq != w.b.first || gapOf(gaps[0]).LastSeq != w.b.last() {
			t.Errorf("gaps = %v, want one for the numbers %d to %d of the refused file", gaps, w.b.first, w.b.last())
		}
	}

	mustPut(t, sp, []model.Record{logRec(epoch.Add(time.Hour), "after-0", 100), logRec(epoch.Add(time.Hour), "after-1", 100)})
	mustAck(t, sp, w.a.first, sp.LastSeq())
	if left := dataOf(readAll(t, sp)); len(left) != 0 {
		t.Errorf("%d records are handed out after everything was acknowledged, want none", len(left))
	}
	r.closeSpool(sp)
}

func TestOpen_TooManyFiles(t *testing.T) {
	const total, keep = 1100, maxFiles + 2

	r := newRig(t)
	boots := []string{otherBoot, thirdBoot}
	var files []fileSpec
	for i := range total {
		f := fileSpec{first: forgedFirst + ofLenInt(i), n: 1, sealed: i < total-1, boot: boots[i%2], capture: epoch.Add(time.Duration(i) * time.Minute), tag: "tm"}
		if !f.sealed {
			f.boot = testBoot
		}
		files = append(files, f)
	}
	writeFiles(t, r.dir(), files...)
	writeMeta(t, r.dir(), metaSpec{agent: testAgent, boot: testBoot, next: files[total-1].last() + 1})
	unread := map[string]stat{}
	for _, f := range files[:total-keep] {
		unread[f.name()] = statOf(t, filepath.Join(r.dir(), f.name()))
	}

	var sp *Spool
	growth := peakHeapGrowth(func() { sp = r.open() })
	if growth >= uint64(4*mebi) {
		t.Errorf("opening %d files grew the heap by %d bytes, want less than 4 MiB", total, growth)
	}

	recs := readAll(t, sp)
	var want []uint64
	for _, f := range files[total-keep:] {
		want = append(want, f.first)
	}
	if got := seqs(dataOf(recs)); !slices.Equal(got, want) {
		t.Fatalf("%d numbers handed out starting at %v, want the %d newest files' numbers starting at %v", len(got), head(got), keep, head(want))
	}
	if gaps := gapsOf(recs, model.GapSpoolDropped); len(gaps) != 0 {
		t.Errorf("%d gaps are written for files that were never read, want none", len(gaps))
	}
	lines := r.logs.find(msgDamaged)
	if len(lines) != 1 || lines[0].level != slog.LevelWarn || lines[0].str("err") != errTooMany || lines[0].str("file") != files[0].name() {
		t.Errorf("log lines %q = %v, want one warning with err %q naming the lowest file %s", msgDamaged, lines, errTooMany, files[0].name())
	}
	mustPut(t, sp, []model.Record{logRec(epoch.Add(1000*time.Hour), "late", 100)})
	if got, err := sp.SpoolBytes(context.Background()); err != nil || got > 2*mebi {
		t.Errorf("SpoolBytes() = %d, %v, want only the kept files counted", got, err)
	}
	for _, f := range files[:total-keep] {
		requireUnchanged(t, "the unread file "+f.name(), filepath.Join(r.dir(), f.name()), unread[f.name()])
	}
}

// head returns at most the first three numbers.
func head(n []uint64) []uint64 { return n[:min(len(n), 3)] }
