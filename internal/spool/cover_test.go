package spool

import (
	"context"
	"errors"
	"io"
	"os"
	"path/filepath"
	"syscall"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func TestStatAvailable(t *testing.T) {
	t.Run("a real directory has space", func(t *testing.T) {
		got, err := statAvailable(t.TempDir())
		needNoErr(t, "statAvailable", err)
		expect(t, got > 0, "statAvailable(temp dir) = %d, want a positive number of bytes", got)
	})
	t.Run("a missing directory is an error", func(t *testing.T) {
		got, err := statAvailable(filepath.Join(t.TempDir(), "missing"))
		expect(t, err != nil, "statAvailable(missing) error = nil, want an error")
		expect(t, got == 0, "statAvailable(missing) = %d, want 0", got)
	})
	t.Run("the default check is used without an injected one", func(t *testing.T) {
		r := newRig(t)
		r.opts.Available = nil
		sp := r.open()
		got, err := sp.available()
		needNoErr(t, "available", err)
		expect(t, got > 0, "available() = %d, want a positive number of bytes", got)
	})
}

func TestNewSpool_RefusesBadOptions(t *testing.T) {
	long := make([]byte, maxAgentLen+1)
	for i := range long {
		long[i] = 'a'
	}
	tests := []struct {
		name string
		mod  func(o *Options)
		want string
	}{
		{"empty agent id", func(o *Options) { o.AgentID = "" }, "spool: agent id: not between 1 and 64 bytes"},
		{"agent id too long", func(o *Options) { o.AgentID = string(long) }, "spool: agent id: not between 1 and 64 bytes"},
		{"boot id not a uuid", func(o *Options) { o.BootID = "nope" }, "spool: boot id: not a lower-case UUID"},
		{"upper-case boot id", func(o *Options) { o.BootID = "0F0F0F0F-1111-4222-8333-444455556666" }, "spool: boot id: not a lower-case UUID"},
		{"zero size", func(o *Options) { o.MaxBytes = 0 }, "spool: max size: not positive"},
		{"negative size", func(o *Options) { o.MaxBytes = -1 }, "spool: max size: not positive"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			r := newRig(t)
			tc.mod(&r.opts)
			sp, err := Open(r.opts, nil)
			if sp != nil {
				_ = sp.Close(context.Background())
			}
			need(t, err != nil && err.Error() == tc.want, "Open() error = %v, want %q", err, tc.want)
		})
	}
	t.Run("a nil logger is accepted", func(t *testing.T) {
		r := newRig(t)
		sp, err := Open(r.opts, nil)
		needNoErr(t, "Open", err)
		needNoErr(t, "Close", sp.Close(context.Background()))
	})
}

func TestFinishSpan_MissingSides(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	from := epoch.Add(time.Hour)
	to := epoch.Add(2 * time.Hour)
	tests := []struct {
		name         string
		haveFrom     bool
		haveTo       bool
		wantA, wantB time.Time
	}{
		{"both sides", true, true, from, to},
		{"only the start", true, false, from, from.Add(time.Second)},
		{"only the end", false, true, to.Add(-time.Second), to},
		{"neither side uses the clock", false, false, epoch.Add(-time.Second), epoch},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			a, b := sp.finishSpan(from.UnixNano(), tc.haveFrom, to.UnixNano(), tc.haveTo)
			expect(t, a.Equal(tc.wantA) && b.Equal(tc.wantB), "finishSpan() = %v to %v, want %v to %v", a, b, tc.wantA, tc.wantB)
		})
	}
}

func TestSpanOf_Bounds(t *testing.T) {
	t.Run("an end before the start is one second after it", func(t *testing.T) {
		a, b := spanOf(epoch, epoch.Add(-time.Hour))
		expect(t, a.Equal(epoch) && b.Equal(epoch.Add(time.Second)), "spanOf() = %v to %v, want one second from the start", a, b)
	})
	t.Run("an end beyond the stored range is clamped", func(t *testing.T) {
		a, b := spanOf(epoch, maxTime.Add(time.Hour))
		expect(t, b.Equal(maxTime) && a.Equal(maxTime.Add(-time.Second)), "spanOf() = %v to %v, want the end at the maximum", a, b)
	})
	t.Run("a start before the stored range is clamped", func(t *testing.T) {
		a, b := spanOf(minTime.Add(-time.Hour), epoch)
		expect(t, a.Equal(minTime) && b.Equal(minTime.Add(time.Second)), "spanOf() = %v to %v, want the start at the minimum", a, b)
	})
}

func TestTimeHelpers_PickEarlierAndLater(t *testing.T) {
	early, late := epoch, epoch.Add(time.Hour)
	expect(t, minTimeOf(early, late).Equal(early) && minTimeOf(late, early).Equal(early), "minTimeOf() did not return the earlier time")
	expect(t, maxTimeOf(early, late).Equal(late) && maxTimeOf(late, early).Equal(late), "maxTimeOf() did not return the later time")
}

func TestReadError_WordsTheSystemError(t *testing.T) {
	got := readError(&os.PathError{Op: "open", Path: "/secret/path", Err: syscall.EIO})
	want := "spool: read: " + syscall.EIO.Error()
	expect(t, got.Error() == want, "readError() = %q, want %q", got.Error(), want)
}

func TestPut_IgnoresCancelledContext(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	recs := batchOf(epoch, "cancelled", 3, 100)

	needNoErr(t, "Put with a cancelled context", sp.Put(ctx, recs))
	requireSame(t, "records kept", readAll(t, sp), numbered(recs, 1+sp.LastSeq()-3))
}

func TestRead_CancelledContextIsAnError(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	mustPut(t, sp, batchOf(epoch, "x", 2, 100))
	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	b, err := sp.Read(ctx, 0, 10, mebi)
	expect(t, errors.Is(err, context.Canceled) && len(b.Records) == 0, "Read(cancelled) = %d records, %v, want context.Canceled", len(b.Records), err)
	_, _, err = sp.OldestCapture(ctx)
	expect(t, errors.Is(err, context.Canceled), "OldestCapture(cancelled) error = %v, want context.Canceled", err)
}

func TestRead_FirstRecordLargerThanMaxBytes(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	mustPut(t, sp, batchOf(epoch, "big", 3, 2000))

	b, err := sp.Read(context.Background(), 0, 10, 100)
	needNoErr(t, "Read(0, 10, 100)", err)
	expect(t, len(b.Records) == 0, "Read with a first record larger than maxBytes returned %d records, want an empty batch", len(b.Records))
	b, err = sp.Read(context.Background(), 0, 0, mebi)
	needNoErr(t, "Read(0, 0, 1 MiB)", err)
	expect(t, len(b.Records) == 0, "Read with maxRecords 0 returned %d records, want none", len(b.Records))
	requireSame(t, "records with a roomy limit", readAll(t, sp), numbered(batchOf(epoch, "big", 3, 2000), 1+sp.LastSeq()-3))
}

func TestOldestCapture_ClosedAndEmpty(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	_, ok, err := sp.OldestCapture(context.Background())
	expect(t, err == nil && !ok, "OldestCapture() of an empty spool = %v, %v, want none", ok, err)
	r.closeSpool(sp)
	_, _, err = sp.OldestCapture(context.Background())
	expect(t, errors.Is(err, errClosed), "OldestCapture() after Close error = %v, want %v", err, errClosed)
	_, err = sp.Read(context.Background(), 0, 1, mebi)
	expect(t, errors.Is(err, errClosed), "Read() after Close error = %v, want %v", err, errClosed)
	err = sp.Put(context.Background(), batchOf(epoch, "late", 1, 50))
	expect(t, errors.Is(err, errClosed), "Put() after Close error = %v, want %v", err, errClosed)
}

func TestRead_DamageInActiveFileDetachesIt(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	first := putTagged(t, sp, epoch, "act", 6, 100)
	names := segmentNumbers(t, r.dir())
	need(t, len(names) == 1, "%d data files, want 1", len(names))
	path := filepath.Join(r.dir(), segName(names[0]))
	st, err := os.Stat(path)
	needNoErr(t, "Stat", err)
	flipAt(t, path, st.Size()-20)

	recs := readAll(t, sp)
	got := seqs(dataOf(recs))
	need(t, len(got) < len(first), "%d records handed out of a damaged file of %d, want fewer", len(got), len(first))
	expect(t, len(gapsOf(recs, model.GapSpoolDropped)) <= 1, "%d spool_dropped gaps before the next Put, want at most 1", len(gapsOf(recs, model.GapSpoolDropped)))

	after := putTagged(t, sp, epoch.Add(time.Hour), "after", 2, 100)
	all := dataOf(readAll(t, sp))
	expect(t, len(all) == len(got)+len(after), "%d records after the damage and a new Put, want %d", len(all), len(got)+len(after))
}

func TestRead_DamageInFirstFrameUsesTheFileBefore(t *testing.T) {
	r := newRig(t)
	w := newWorld(t, r.dir())
	sp := r.open()
	sb := encodeSegment(t, w.b.spec())
	flipAt(t, filepath.Join(w.dir, w.b.name()), int64(sb.frameStart[0]+8+8+5))

	recs := readAll(t, sp)
	want := concat(rangeNumbers(w.a.first, w.a.last()), rangeNumbers(w.c.first, w.c.last()))
	expectNumbers(t, "numbers handed out", seqs(dataOf(recs)), want)
	gaps := gapsOf(recs, model.GapSpoolDropped)
	need(t, len(gaps) == 1, "%d spool_dropped gaps, want 1", len(gaps))
	gp := gapOf(gaps[0])
	expect(t, gp.FirstSeq == w.b.first && gp.LastSeq == w.b.last(), "gap covers %d to %d, want %d to %d", gp.FirstSeq, gp.LastSeq, w.b.first, w.b.last())
	expect(t, gp.From.Equal(w.a.at(w.a.n-1)), "gap starts at %v, want the latest record of the file before, %v", gp.From, w.a.at(w.a.n-1))
}

func TestSpool_FileNeighbours(t *testing.T) {
	r := newRig(t)
	w := newWorld(t, r.dir())
	sp := r.open()
	need(t, len(sp.files) == 3, "%d files, want 3", len(sp.files))
	a, b, c := sp.files[0], sp.files[1], sp.files[2]
	stranger := &segFile{num: 1}

	expect(t, sp.fileBefore(a) == nil, "fileBefore(first) != nil")
	expect(t, sp.fileBefore(b) == a && sp.fileBefore(c) == b, "fileBefore did not return the previous file")
	expect(t, sp.fileBefore(stranger) == nil, "fileBefore(unknown) != nil")
	expect(t, sp.fileAfter(c) == nil, "fileAfter(last) != nil")
	expect(t, sp.fileAfter(a) == b && sp.fileAfter(b) == c, "fileAfter did not return the next file")
	expect(t, sp.fileAfter(stranger) == nil, "fileAfter(unknown) != nil")
	expect(t, a.num == w.a.first, "first file number = %d, want %d", a.num, w.a.first)
}

func TestSpool_DetachActive(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	sp.detachActive()
	expect(t, sp.act == nil && sp.actF == nil, "detachActive() on a spool without an active file left one")
	mustPut(t, sp, batchOf(epoch, "d", 2, 100))
	need(t, sp.act != nil && sp.actF != nil, "Put left no active file")
	f := sp.actF
	sp.detachActive()
	expect(t, sp.act == nil && sp.actF == nil, "detachActive() left an active file")
	_, err := f.Write([]byte{0})
	expect(t, errors.Is(err, os.ErrClosed), "write to the detached file error = %v, want it closed", err)
	mustPut(t, sp, batchOf(epoch.Add(time.Hour), "e", 2, 100))
	expect(t, len(dataOf(readAll(t, sp))) == 4, "%d records after a Put into a new active file, want 4", len(dataOf(readAll(t, sp))))
}

func TestSpool_Discard(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	mustPut(t, sp, batchOf(epoch, "dis", 2, 100))
	f := sp.files[0]

	t.Run("a file of another identity is refused and kept", func(t *testing.T) {
		stranger := *f
		stranger.id = fileID{dev: f.id.dev, ino: f.id.ino + 1}
		err := sp.discard(&stranger)
		need(t, err != nil, "discard(foreign identity) = nil, want an error")
		expect(t, err.Error() == "spool: write: "+errDamageRefused.Error(), "discard() error = %q, want the refused-entry write error", err.Error())
		expect(t, len(sp.files) == 1, "%d files after a refused discard, want 1", len(sp.files))
	})
	t.Run("a file is removed from disk and list", func(t *testing.T) {
		needNoErr(t, "discard", sp.discard(f))
		expect(t, len(sp.files) == 0, "%d files after discard, want 0", len(sp.files))
		expect(t, len(segmentNumbers(t, r.dir())) == 0, "the file is still on disk after discard")
	})
	t.Run("a file the list does not hold is no error", func(t *testing.T) {
		needNoErr(t, "discard", sp.discard(f))
	})
}

func TestSpool_HealCutsTheActiveFile(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	mustPut(t, sp, batchOf(epoch, "heal", 2, 100))

	t.Run("nothing to heal", func(t *testing.T) {
		needNoErr(t, "heal", sp.heal())
	})
	t.Run("the file is cut back", func(t *testing.T) {
		good := sp.act.size
		_, err := sp.actF.WriteAt([]byte("junk"), good)
		needNoErr(t, "WriteAt", err)
		sp.healTail = true
		needNoErr(t, "heal", sp.heal())
		st, err := sp.actF.Stat()
		needNoErr(t, "Stat", err)
		expect(t, st.Size() == good && !sp.healTail, "after heal size = %d (want %d), healTail = %v (want false)", st.Size(), good, sp.healTail)
	})
	t.Run("a failed cut keeps the flag", func(t *testing.T) {
		ro, err := os.Open(filepath.Join(r.dir(), segName(sp.files[0].num)))
		needNoErr(t, "Open", err)
		defer func() { _ = ro.Close() }()
		saved := sp.actF
		sp.actF, sp.healTail = ro, true
		err = sp.heal()
		sp.actF = saved
		need(t, err != nil, "heal() on a read-only file = nil, want an error")
		expect(t, sp.healTail, "healTail cleared after a failed cut")
	})
}

func TestSpool_NewActiveHeaderWriteFails(t *testing.T) {
	fault := installFault(t)
	r := newRig(t)
	sp := r.open()
	fault.set(faultFail, syscall.ENOSPC)

	err := sp.newActive(77)
	need(t, err != nil, "newActive() with failing writes = nil, want an error")
	expect(t, err.Error() == "spool: write: "+syscall.ENOSPC.Error(), "newActive() error = %q, want the write error", err.Error())
	expect(t, len(segmentNumbers(t, r.dir())) == 0, "the half-made file is still on disk")
	expect(t, sp.act == nil && len(sp.files) == 0, "the failed file joined the spool")

	fault.set(faultPass, nil)
	needNoErr(t, "newActive", sp.newActive(77))
	expect(t, len(sp.files) == 1 && sp.act == sp.files[0], "a successful newActive did not make the file active")
	t.Run("an existing name cannot be created again", func(t *testing.T) {
		err := sp.newActive(77)
		expect(t, err != nil, "newActive() of an existing name = nil, want an error")
	})
}

func TestSpool_SealActive(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	mustPut(t, sp, batchOf(epoch, "seal", 2, 100))
	needNoErr(t, "sealActive", sp.sealActive())
	need(t, len(sp.files) == 1 && sp.files[0].sealed, "the sealed file is not marked sealed")
	expect(t, sp.act == nil, "sealActive left an active file")

	needNoErr(t, "newActive", sp.newActive(sp.next))
	needNoErr(t, "sealActive of an empty file", sp.sealActive())
	expect(t, len(sp.files) == 1, "%d files after sealing an empty one, want it removed (1 left)", len(sp.files))
}

func TestSpool_WriteTrailerErrors(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	mustPut(t, sp, batchOf(epoch, "tr", 2, 100))
	seg, file := sp.act, sp.actF
	size := seg.size

	t.Run("a failed write cuts the file back", func(t *testing.T) {
		ro, err := os.Open(filepath.Join(r.dir(), seg.name()))
		needNoErr(t, "Open", err)
		defer func() { _ = ro.Close() }()
		err = sp.writeTrailer(ro, seg)
		expect(t, err != nil, "writeTrailer() on a read-only file = nil, want an error")
		expect(t, !seg.sealed && seg.size == size, "a failed trailer sealed the file (sealed=%v size=%d, want false and %d)", seg.sealed, seg.size, size)
	})
	t.Run("a failed sync is reported after the figures are set", func(t *testing.T) {
		restore := SetWriteFile(func(f *os.File, b []byte, off int64) (int, error) {
			n, err := f.WriteAt(b, off)
			_ = f.Close()

			return n, err
		})
		defer restore()
		err := sp.writeTrailer(file, seg)
		expect(t, err != nil, "writeTrailer() with a failing sync = nil, want an error")
	})
}

func TestSpool_ActivateAndSealFoundErrors(t *testing.T) {
	r := newRig(t)
	w := newWorld(t, r.dir())
	sp := r.open()
	seg := sp.files[2]
	cases := map[string]*found{
		"foreign identity": {seg: &segFile{num: seg.num, id: fileID{dev: seg.id.dev, ino: seg.id.ino + 1}, size: seg.size}},
		"absent file":      {seg: &segFile{num: 5, size: 10}},
	}
	for name, e := range cases {
		t.Run("activate "+name, func(t *testing.T) {
			expect(t, sp.activate(e) != nil, "activate() = nil, want an error")
		})
		t.Run("seal "+name, func(t *testing.T) {
			expect(t, sp.sealFound(e) != nil, "sealFound() = nil, want an error")
		})
	}
	t.Run("a failing cut", func(t *testing.T) {
		e := &found{seg: &segFile{num: w.c.first, id: seg.id, size: seg.size}, res: scanResult{goodEnd: -1}}
		expect(t, sp.activate(e) != nil, "activate() with an impossible cut = nil, want an error")
		expect(t, sp.sealFound(e) != nil, "sealFound() with an impossible cut = nil, want an error")
	})
	t.Run("a file read for reading must be the known one", func(t *testing.T) {
		_, err := sp.openForRead(&segFile{num: seg.num, id: fileID{ino: seg.id.ino + 1}})
		expect(t, errors.Is(err, errDamageRefused), "openForRead(foreign identity) error = %v, want %v", err, errDamageRefused)
		_, err = sp.openForRead(&segFile{num: 5})
		expect(t, errors.Is(err, errAbsent), "openForRead(absent) error = %v, want %v", err, errAbsent)
	})
}

func TestScanResult_FinishTrailer(t *testing.T) {
	const prefix = 16
	good := trailerSpec{last: 9, count: 3, earliest: 1, latest: 2}
	tests := []struct {
		name       string
		tail       func(t *testing.T) []byte
		count      uint64
		last       uint64
		wantSealed bool
		wantDamage error
		wantTorn   bool
	}{
		{"an intact trailer", func(t *testing.T) []byte { return encodeTrailer(t, &good) }, 3, 9, true, nil, false},
		{"a trailer cut short", func(t *testing.T) []byte { return encodeTrailer(t, &good)[:10] }, 3, 9, false, errDamageCutShort, true},
		{"bytes after the trailer", func(t *testing.T) []byte { return append(encodeTrailer(t, &good), 0) }, 3, 9, false, errDamageInconsistent, false},
		{"a wrong checksum", func(t *testing.T) []byte {
			bad := good
			bad.badCRC = true

			return encodeTrailer(t, &bad)
		}, 3, 9, false, errDamageChecksum, false},
		{"a trailer after no record", func(t *testing.T) []byte { return encodeTrailer(t, &good) }, 0, 0, false, errDamageInconsistent, false},
		{"a different last number", func(t *testing.T) []byte { return encodeTrailer(t, &good) }, 3, 8, false, errDamageInconsistent, false},
		{"a different count", func(t *testing.T) []byte { return encodeTrailer(t, &good) }, 4, 9, false, errDamageInconsistent, false},
		{"earliest after latest", func(t *testing.T) []byte {
			bad := good
			bad.earliest, bad.latest = 5, 1

			return encodeTrailer(t, &bad)
		}, 3, 9, false, errDamageInconsistent, false},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "f")
			data := append(make([]byte, prefix), tc.tail(t)...)
			needNoErr(t, "WriteFile", os.WriteFile(path, data, 0o600))
			f, err := os.Open(path)
			needNoErr(t, "Open", err)
			defer func() { _ = f.Close() }()
			res := scanResult{count: tc.count, last: tc.last}

			res.finishTrailer(f, prefix, int64(len(data)))
			expect(t, res.goodEnd == prefix, "goodEnd = %d, want %d", res.goodEnd, prefix)
			expect(t, (res.tr != nil) == tc.wantSealed, "trailer accepted = %v, want %v", res.tr != nil, tc.wantSealed)
			expect(t, errors.Is(res.damage, tc.wantDamage) && (tc.wantDamage != nil || res.damage == nil), "damage = %v, want %v", res.damage, tc.wantDamage)
			expect(t, res.torn == tc.wantTorn, "torn = %v, want %v", res.torn, tc.wantTorn)
		})
	}
	t.Run("a trailer that cannot be read", func(t *testing.T) {
		path := filepath.Join(t.TempDir(), "f")
		needNoErr(t, "WriteFile", os.WriteFile(path, make([]byte, prefix+trailerLen), 0o600))
		f, err := os.Open(path)
		needNoErr(t, "Open", err)
		needNoErr(t, "Close", f.Close())
		res := scanResult{count: 3, last: 9}
		res.finishTrailer(f, prefix, prefix+trailerLen)
		expect(t, errors.Is(res.damage, errDamageCutShort), "damage = %v, want %v", res.damage, errDamageCutShort)
	})
}

func TestIsDamageAndErrText(t *testing.T) {
	expect(t, isDamage(errDamageTooMany) && isDamage(errDamageVersion) && isDamage(errDamageNotFormat), "isDamage() missed a damage reason")
	expect(t, !isDamage(io.EOF), "isDamage(io.EOF) = true, want false")
	expect(t, errText(&os.LinkError{Op: "rename", Old: "/a", New: "/b", Err: syscall.EIO}) == syscall.EIO.Error(), "errText(LinkError) kept a path")
	expect(t, errText(&os.PathError{Path: "/a", Err: syscall.EIO}) == syscall.EIO.Error(), "errText(PathError) kept a path")
	expect(t, errText(syscall.ENOSPC) == syscall.ENOSPC.Error(), "errText(errno) changed the text")
	expect(t, errText(io.EOF) == "EOF", "errText(plain) changed the text")
}

func TestDirFS_Operations(t *testing.T) {
	r := newRig(t)
	d, err := openDir(r.dir())
	needNoErr(t, "openDir", err)
	defer d.close()

	f, id, err := d.create("x")
	needNoErr(t, "create", err)
	_ = f.Close()
	_, _, err = d.create("x")
	expect(t, errors.Is(err, syscall.EEXIST), "create(existing) error = %v, want EEXIST", err)
	_, _, err = d.openEntry("absent", false)
	expect(t, errors.Is(err, errAbsent), "openEntry(absent) error = %v, want %v", err, errAbsent)
	needNoErr(t, "removeTrusted", d.removeTrusted("absent", nil))
	other := fileID{dev: id.dev, ino: id.ino + 1}
	expect(t, errors.Is(d.removeTrusted("x", &other), errDamageRefused), "removeTrusted(foreign identity) did not refuse")
	needNoErr(t, "rename", d.rename("x", "y"))
	needNoErr(t, "sync", d.sync())
	var names []string
	needNoErr(t, "list", d.list(func(n string) { names = append(names, n) }))
	expect(t, len(names) == 1 && names[0] == "y", "list() = %v, want [y]", names)
	needNoErr(t, "removeTrusted", d.removeTrusted("y", &id))
	expect(t, newFile(-1) == nil, "newFile(-1) != nil")
	n, err := syscall.Getpagesize(), error(nil)
	expect(t, n > 0 && err == nil, "page size")
}
