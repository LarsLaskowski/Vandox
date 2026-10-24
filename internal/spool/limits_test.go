package spool

import (
	"cmp"
	"context"
	"fmt"
	"io"
	"log/slog"
	"os"
	"slices"
	"strings"
	"sync"
	"syscall"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	msgDropped   = "spool data dropped"
	msgNoDropped = "spool data no longer dropped"
	msgWriteFail = "spool write failed"
	msgWriteOK   = "spool writes again"
	capSmall     = 4 * mebi
)

// feeder hands numbered batches of log records to a spool and remembers what it handed over.
type feeder struct {
	t        *testing.T
	sp       *Spool
	perBatch int
	size     int
	batches  int
	fed      []model.Record
	captured map[uint64]time.Time
}

func newFeeder(t *testing.T, sp *Spool, perBatch, size int) *feeder {
	t.Helper()

	return &feeder{t: t, sp: sp, perBatch: perBatch, size: size, captured: map[uint64]time.Time{}}
}

// put hands over one batch captured one second after the previous one.
func (f *feeder) put() {
	f.t.Helper()

	recs := putTagged(f.t, f.sp, epoch.Add(time.Duration(f.batches)*time.Second), fmt.Sprintf("f%d", f.batches), f.perBatch, f.size)
	f.batches++
	f.fed = append(f.fed, recs...)
	for _, r := range recs {
		f.captured[r.Seq] = r.CapturedAt
	}
}

// putN hands over n batches.
func (f *feeder) putN(n int) {
	f.t.Helper()

	for range n {
		f.put()
	}
}

// putUntil hands over batches until stop reports true, at most limit batches.
func (f *feeder) putUntil(limit int, stop func() bool) {
	f.t.Helper()

	for range limit {
		if stop() {
			return
		}
		f.put()
	}
	f.t.Fatalf("the condition did not come true within %d batches", limit)
}

// suffixFrom returns the fed records whose number is at least first.
func (f *feeder) suffixFrom(first uint64) []model.Record {
	var out []model.Record
	for _, r := range f.fed {
		if r.Seq >= first {
			out = append(out, r)
		}
	}

	return out
}

// requireOnlyOldestDropped checks that the data records read are a suffix of the records fed (only the oldest were
// dropped) and that something was dropped.
func (f *feeder) requireOnlyOldestDropped(recs []model.Record) {
	f.t.Helper()

	kept := dataOf(recs)
	need(f.t, len(kept) > 0, "no data record is kept")
	requireSame(f.t, "data records kept", kept, f.suffixFrom(kept[0].Seq))
	need(f.t, len(kept) < len(f.fed), "%d records fed and %d kept, want some of the oldest dropped", len(f.fed), len(kept))
}

// lowestSeq returns the lowest number among recs.
func lowestSeq(recs []model.Record) uint64 {
	lowest := recs[0].Seq
	for _, r := range recs {
		lowest = min(lowest, r.Seq)
	}

	return lowest
}

// requireAgentGap checks the origin and source of a gap the spool wrote.
func requireAgentGap(t *testing.T, g model.Record) {
	t.Helper()

	expect(t, g.Origin == model.OriginAgent && g.Source == "agent", "gap %d has origin %q and source %q, want agent and agent", g.Seq, g.Origin, g.Source)
}

// inAnySpan reports whether at lies inside the time span of one of the gaps.
func inAnySpan(at time.Time, gaps []model.Record) bool {
	for _, g := range gaps {
		if gp := gapOf(g); !at.Before(gp.From) && !at.After(gp.To) {
			return true
		}
	}

	return false
}

// requireDroppedRecorded checks the drop gaps in recs: well formed, apart from every kept number, one of them ends
// right below the lowest kept number, and the capture time of every dropped record lies inside some gap's span.
func (f *feeder) requireDroppedRecorded(recs []model.Record) {
	f.t.Helper()

	gaps := gapsOf(recs, model.GapSpoolDropped)
	need(f.t, len(gaps) > 0, "no spool_dropped gap is kept")
	requireGapsDisjoint(f.t, recs)
	lowest := lowestSeq(recs)
	endsBelow := false
	for _, g := range gaps {
		requireAgentGap(f.t, g)
		endsBelow = endsBelow || gapOf(g).LastSeq == lowest-1
	}
	expect(f.t, endsBelow, "no gap ends at %d, the number below the lowest kept record %d", lowest-1, lowest)
	keptSeq := map[uint64]bool{}
	for _, r := range dataOf(recs) {
		keptSeq[r.Seq] = true
	}
	for _, r := range f.fed {
		need(f.t, keptSeq[r.Seq] || inAnySpan(r.CapturedAt, gaps), "the dropped record %d captured at %v lies in no gap's time span", r.Seq, r.CapturedAt)
	}
}

// expectedSpan returns the time span the gap of the numbers first to last must have: from the earliest to the latest
// capture time of the records, at least one second long.
func (f *feeder) expectedSpan(first, last uint64) (time.Time, time.Time) {
	var from, to time.Time
	for n := first; n <= last; n++ {
		c, ok := f.captured[n]
		if !ok {
			continue
		}
		if from.IsZero() || c.Before(from) {
			from = c
		}
		to = laterOf(to, c)
	}
	if to.Sub(from) < time.Second {
		to = from.Add(time.Second)
	}

	return from, to
}

func laterOf(a, b time.Time) time.Time {
	if b.After(a) {
		return b
	}

	return a
}

// requireRunGaps checks that exactly the runs want are recorded as spool_dropped gaps, with their exact spans.
func (f *feeder) requireRunGaps(recs []model.Record, want [][2]uint64) {
	f.t.Helper()

	gaps := gapsOf(recs, model.GapSpoolDropped)
	need(f.t, len(gaps) == len(want), "%d spool_dropped gaps are kept (%v), want one per run of dropped numbers: %v", len(gaps), seqsHead(gaps), want)
	slices.SortFunc(gaps, func(a, b model.Record) int { return cmp.Compare(gapOf(a).FirstSeq, gapOf(b).FirstSeq) })
	for i, g := range gaps {
		gp := gapOf(g)
		expect(f.t, gp.FirstSeq == want[i][0] && gp.LastSeq == want[i][1], "gap %d covers %d to %d, want %d to %d", i, gp.FirstSeq, gp.LastSeq, want[i][0], want[i][1])
		from, to := f.expectedSpan(want[i][0], want[i][1])
		expect(f.t, gp.From.Equal(from) && gp.To.Equal(to), "gap %d spans %v to %v, want %v to %v", i, gp.From, gp.To, from, to)
		requireAgentGap(f.t, g)
	}
}

// dropLines returns the log lines about drops, in order.
func dropLines(l *logRecorder) []logEntry {
	var out []logEntry
	for _, e := range l.all() {
		if e.msg == msgDropped || e.msg == msgNoDropped {
			out = append(out, e)
		}
	}

	return out
}

// requireEpisodeLine checks line i of the drop lines: a warning at the start of an episode (even i) or an info at its end.
func requireEpisodeLine(t *testing.T, i int, e logEntry, reason string) {
	t.Helper()

	wantMsg, wantLevel := msgDropped, slog.LevelWarn
	if i%2 == 1 {
		wantMsg, wantLevel = msgNoDropped, slog.LevelInfo
	}
	n, ok := e.num("records")
	expect(t, e.msg == wantMsg, "drop line %d is %q, want %q (one warning per episode, then one info at its end)", i, e.msg, wantMsg)
	expect(t, e.level == wantLevel, "drop line %d (%q) has level %v, want %v", i, e.msg, e.level, wantLevel)
	expect(t, ok && n > 0, "drop line %d (%q) has records = %v, %v, want a positive count", i, e.msg, n, ok)
	expect(t, e.msg != msgDropped || e.str("reason") == reason, "drop line %d has reason %q, want %q", i, e.str("reason"), reason)
}

// requireEpisodes checks that the drop lines come in pairs of one warning at the start of an episode and one info at
// its end, never two warnings in a row, each with the fixed reason and a positive count, and returns the lines.
func requireEpisodes(t *testing.T, l *logRecorder, reason string) []logEntry {
	t.Helper()

	lines := dropLines(l)
	need(t, len(lines) > 0, "no line %q was logged", msgDropped)
	for i, e := range lines {
		requireEpisodeLine(t, i, e, reason)
	}

	return lines
}

func warnings(l *logRecorder) []logEntry {
	var out []logEntry
	for _, e := range l.all() {
		if e.level >= slog.LevelWarn {
			out = append(out, e)
		}
	}

	return out
}

// newCapped returns a rig capped at 4 MiB with its open spool and a feeder of batches of 20 records of 1000 bytes.
func newCapped(t *testing.T) (*rig, *Spool, *feeder) {
	t.Helper()

	r := newRig(t)
	r.opts.MaxBytes = capSmall
	sp := r.open()

	return r, sp, newFeeder(t, sp, 20, 1000)
}

// lastDropLineIs returns a condition for putUntil: the last drop line has the message msg.
func lastDropLineIs(r *rig, msg string) func() bool {
	return func() bool {
		lines := dropLines(r.logs)

		return len(lines) > 0 && lines[len(lines)-1].msg == msg
	}
}

// acknowledgeAll acknowledges everything the spool holds.
func acknowledgeAll(t *testing.T, sp *Spool) {
	t.Helper()

	mustAck(t, sp, lowestSeq(readAll(t, sp)), sp.LastSeq())
}

func TestPut_SizeBoundDropsOldest(t *testing.T) {
	r, sp, f := newCapped(t)

	for i := range 600 {
		f.put()
		disk := diskBytes(t, r.dir())
		need(t, disk <= r.opts.MaxBytes, "after hand-over %d the spool's files take %d bytes, want at most the cap of %d", i, disk, r.opts.MaxBytes)
		got, err := sp.SpoolBytes(context.Background())
		need(t, err == nil && got == disk, "after hand-over %d SpoolBytes() = %d, %v, want the size of the files on disk, %d", i, got, err, disk)
	}

	recs := readAll(t, sp)
	f.requireOnlyOldestDropped(recs)
	f.requireDroppedRecorded(recs)
	requireEpisodes(t, r.logs, "size_bound")
	requireNoLeak(t, r)

	// A dropped number acknowledged afterwards changes nothing.
	kept := dataOf(recs)
	_ = sp.Ack(context.Background(), f.fed[0].Seq, kept[0].Seq-1)
	requireSame(t, "records after acknowledging dropped numbers", dataOf(readAll(t, sp)), kept)

	// The episode ends when a hand-over fits again without a drop.
	f.putUntil(1000, lastDropLineIs(r, msgDropped))
	acknowledgeAll(t, sp)
	f.put()
	lines := requireEpisodes(t, r.logs, "size_bound")
	need(t, lines[len(lines)-1].msg == msgNoDropped, "the last drop line is %q after a hand-over fitted again, want %q", lines[len(lines)-1].msg, msgNoDropped)
}

// TestPut_DroppedGapsPerRun pins that the span of each run is exact: the runs are parts of one file, so the spool
// must scan the frames (a trailer would give the whole file's span).
func TestPut_DroppedGapsPerRun(t *testing.T) {
	r, sp, f := newCapped(t)

	f.putUntil(2000, func() bool { return len(segmentNumbers(t, r.dir())) >= 2 })
	names := segmentNumbers(t, r.dir())
	first, next := names[0], names[1]
	need(t, next-first >= 100, "the oldest file holds the numbers %d to %d, want at least 100 records", first, next-1)
	mustAck(t, sp, first+10, first+19)
	mustAck(t, sp, first+40, first+49)
	f.putUntil(2000, func() bool { return segmentNumbers(t, r.dir())[0] != first })
	oldest := segmentNumbers(t, r.dir())[0]
	need(t, oldest == next, "the oldest file after the drop starts at %d, want only the first file dropped, so %d", oldest, next)

	recs := readAll(t, sp)
	f.requireRunGaps(recs, [][2]uint64{{first, first + 9}, {first + 20, first + 39}, {first + 50, next - 1}})
	requireGapsDisjoint(t, recs)
	for _, rec := range recs {
		acked := rec.Seq >= first+10 && rec.Seq <= first+19 || rec.Seq >= first+40 && rec.Seq <= first+49
		expect(t, !acked, "acknowledged number %d is read again", rec.Seq)
	}
}

func TestPut_DroppedGapRecordsFolded(t *testing.T) {
	r, sp, f := newCapped(t)

	oldest := func() uint64 { return segmentNumbers(t, r.dir())[0] }
	f.putUntil(2000, func() bool { return len(segmentNumbers(t, r.dir())) >= 2 })
	first := oldest()
	f.putUntil(2000, func() bool { return oldest() != first })
	g1 := gapsOf(readAll(t, sp), model.GapSpoolDropped)
	need(t, len(g1) == 1, "after the first drop %d gaps are kept, want 1", len(g1))
	earlier := g1[0]

	f.putUntil(4000, func() bool { return oldest() > earlier.Seq })
	recs := readAll(t, sp)
	var folded *model.Gap
	for _, g := range gapsOf(recs, model.GapSpoolDropped) {
		if gp := gapOf(g); gp.FirstSeq <= earlier.Seq && earlier.Seq <= gp.LastSeq {
			folded = gp
		}
	}
	need(t, folded != nil, "the gap record %d was dropped and no later gap covers its number", earlier.Seq)
	before := gapOf(earlier)
	expect(t, folded.From.Equal(before.From) && !folded.To.Before(before.To), "the gap that replaces the dropped gap spans %v to %v, want it to cover the dropped gap's span %v to %v", folded.From, folded.To, before.From, before.To)
	f.requireOnlyOldestDropped(recs)
	f.requireDroppedRecorded(recs)
	expect(t, diskBytes(t, r.dir()) <= r.opts.MaxBytes, "the spool's files take %d bytes, want at most %d", diskBytes(t, r.dir()), r.opts.MaxBytes)
}

// dropSeveralFilesInOneHandOver fills the spool nearly to its cap, then hands over 2 MB at once and checks that one
// warning and one gap cover the run across the files dropped.
func dropSeveralFilesInOneHandOver(t *testing.T, r *rig, sp *Spool, f *feeder) {
	t.Helper()

	f.putUntil(1000, func() bool { return len(segmentNumbers(t, r.dir())) >= 4 })
	f.putUntil(1000, func() bool { return diskBytes(t, r.dir()) >= r.opts.MaxBytes-300<<10 })
	names := segmentNumbers(t, r.dir())
	r.logs.reset()
	mustPut(t, sp, batchOf(epoch.Add(24*time.Hour), "big", 200, 10000))

	lines := requireEpisodes(t, r.logs, "size_bound")
	need(t, len(lines) == 1 && lines[0].msg == msgDropped, "a hand-over that drops several files logged %d drop lines, want one warning for the episode", len(lines))
	remaining := segmentNumbers(t, r.dir())
	need(t, remaining[0] > names[1], "the oldest file starts at %d after the big hand-over, want more than one file dropped (the second file started at %d)", remaining[0], names[1])
	gaps := gapsOf(readAll(t, sp), model.GapSpoolDropped)
	need(t, len(gaps) == 1, "%d gaps are kept after dropping %d files, want 1 for the one run of dropped numbers", len(gaps), len(names)-len(remaining))
	gp := gapOf(gaps[0])
	expect(t, gp.FirstSeq == names[0] && gp.LastSeq == remaining[0]-1, "gap covers %d to %d, want %d to %d", gp.FirstSeq, gp.LastSeq, names[0], remaining[0]-1)
	// The run is whole files, so its span comes from their trailers: exactly the earliest and latest capture time.
	from, to := f.expectedSpan(names[0], remaining[0]-1)
	expect(t, gp.From.Equal(from) && gp.To.Equal(to), "gap spans %v to %v, want %v to %v", gp.From, gp.To, from, to)
}

func TestPut_DropLogLines(t *testing.T) {
	r, sp, f := newCapped(t)

	dropSeveralFilesInOneHandOver(t, r, sp, f)

	// A long feed logs pairs of lines and not one per file.
	f.putN(400)
	requireEpisodes(t, r.logs, "size_bound")

	// After everything is acknowledged the next hand-over fits and ends the episode; a refill starts a new one.
	f.putUntil(1000, lastDropLineIs(r, msgDropped))
	acknowledgeAll(t, sp)
	f.put()
	ended := len(requireEpisodes(t, r.logs, "size_bound"))
	need(t, lastDropLineIs(r, msgNoDropped)(), "the last drop line is not %q after the spool was emptied", msgNoDropped)
	f.putN(400)
	lines := requireEpisodes(t, r.logs, "size_bound")
	expect(t, len(lines) > ended, "%d drop lines after refilling the spool, want a new episode after the %d earlier lines", len(lines), ended)
	for _, e := range r.logs.all() {
		expect(t, !strings.Contains(e.text(), filler(20)) && !strings.Contains(e.text(), "f0-"), "log line %q holds a record's content", e.text())
	}
	requireNoLeak(t, r)
}

// forgeMaxFiles forges maxFiles sealed files of one record each, in two alternating boots, with a fitting meta file.
func forgeMaxFiles(t *testing.T, dir string) []fileSpec {
	t.Helper()

	boots := []string{otherBoot, thirdBoot}
	var files []fileSpec
	for i := range maxFiles {
		files = append(files, fileSpec{first: forgedFirst + ofLenInt(i), n: 1, sealed: true, boot: boots[i%2], capture: epoch.Add(time.Duration(i) * time.Minute), tag: fmt.Sprintf("l%d", i)})
	}
	writeFiles(t, dir, files...)
	writeMeta(t, dir, metaSpec{agent: testAgent, boot: otherBoot, clean: true, next: forgedFirst + maxFiles})

	return files
}

func TestPut_FileLimit(t *testing.T) {
	r := newRig(t)
	files := forgeMaxFiles(t, r.dir())

	sp := r.open()
	need(t, len(segmentNumbers(t, r.dir())) == maxFiles, "%d data files after Open, want %d", len(segmentNumbers(t, r.dir())), maxFiles)
	need(t, r.logs.count(msgDamaged) == 0, "Open logged %q for %d valid files", msgDamaged, maxFiles)

	at := epoch.Add(48 * time.Hour)
	mustPut(t, sp, []model.Record{logRec(at, "new-0", 80)})
	names := segmentNumbers(t, r.dir())
	need(t, len(names) == maxFiles, "%d data files after the hand-over that needs a new file, want the limit of %d", len(names), maxFiles)
	expect(t, names[0] == forgedFirst+1, "the oldest file starts at %d, want %d (the oldest file dropped)", names[0], forgedFirst+1)
	requireFileLimitGap(t, readAll(t, sp), files[0])
	drops := r.logs.find(msgDropped)
	need(t, len(drops) == 1 && drops[0].level == slog.LevelWarn && drops[0].str("reason") == "file_limit", "log lines %q = %v, want one warning with reason file_limit", msgDropped, drops)
	n, ok := drops[0].num("records")
	expect(t, ok && n == 1, "spool data dropped has records = %v, %v, want 1", n, ok)

	// Restarts in the same boot go on writing into the newest file.
	for i := range 3 {
		sp = r.reopen(sp)
		mustPut(t, sp, []model.Record{logRec(at.Add(time.Duration(i+1)*time.Minute), fmt.Sprintf("again-%d", i), 80)})
		count := len(segmentNumbers(t, r.dir()))
		need(t, count == maxFiles, "after restart %d there are %d data files, want %d (a restart in the same boot adds no file)", i+1, count, maxFiles)
	}
	requireGapsDisjoint(t, readAll(t, sp))
}

// requireFileLimitGap checks the gap for the one record of the dropped oldest file.
func requireFileLimitGap(t *testing.T, recs []model.Record, dropped fileSpec) {
	t.Helper()

	gaps := gapsOf(recs, model.GapSpoolDropped)
	need(t, len(gaps) == 1, "%d spool_dropped gaps are kept, want 1 for the dropped file", len(gaps))
	gp := gapOf(gaps[0])
	expect(t, gp.FirstSeq == dropped.first && gp.LastSeq == dropped.first, "gap covers %d to %d, want %d to %d", gp.FirstSeq, gp.LastSeq, dropped.first, dropped.first)
	expect(t, gp.From.Equal(dropped.at(0)) && gp.To.Equal(dropped.at(0).Add(time.Second)), "gap spans %v to %v, want %v to one second later", gp.From, gp.To, dropped.at(0))
}

// reserveKeptByDropping checks that the free space never falls below the reserve on a disk of limited size.
func reserveKeptByDropping(t *testing.T, reserve int64) {
	t.Helper()

	r := newRig(t)
	capacity := reserve + 7*mebi/2
	r.opts.MinFreeBytes = reserve
	r.opts.Available = virtualDisk{capacity: capacity, t: t}.Available
	sp := r.open()
	f := newFeeder(t, sp, 20, 1000)
	for i := range 300 {
		f.put()
		left := capacity - diskBytes(t, r.dir())
		need(t, left >= reserve, "after hand-over %d %d bytes remain available, want at least the reserve of %d", i, left, reserve)
	}
	recs := readAll(t, sp)
	f.requireOnlyOldestDropped(recs)
	f.requireDroppedRecorded(recs)
	requireEpisodes(t, r.logs, "disk_space")
}

// plentyOfSpace checks that a roomy disk drops nothing.
func plentyOfSpace(t *testing.T, reserve int64) {
	t.Helper()

	r := newRig(t)
	r.opts.MinFreeBytes = reserve
	r.disk.Set(u64(t, reserve+64*mebi))
	sp := r.open()
	f := newFeeder(t, sp, 20, 1000)
	f.putN(30)
	requireSame(t, "records with plenty of space", readAll(t, sp), f.fed)
	expect(t, r.logs.count(msgDropped) == 0, "%q was logged %d times, want 0", msgDropped, r.logs.count(msgDropped))
}

// refusedThenWritten checks that a hand-over the disk cannot take is refused, and the gap is written with the first
// hand-over that fits.
func refusedThenWritten(t *testing.T, minFree int64, refusing, ample uint64) {
	t.Helper()

	r := newRig(t)
	r.opts.MinFreeBytes = minFree
	sp := r.open()
	r.disk.Set(refusing)

	refusedA := []model.Record{logRec(epoch.Add(time.Hour), "ra-0", 200), logRec(epoch.Add(time.Hour+5*time.Second), "ra-1", 200)}
	refusedB := []model.Record{logRec(epoch.Add(2*time.Hour), "rb-0", 200)}
	for _, b := range [][]model.Record{refusedA, refusedB} {
		err := sp.Put(context.Background(), slices.Clone(b))
		need(t, err != nil, "Put with %d bytes available and a reserve of %d = nil, want it refused", refusing, minFree)
	}
	need(t, len(readAll(t, sp)) == 0, "records are kept after the refused hand-overs, want none")
	expect(t, r.logs.count(msgWriteFail) == 1, "%q was logged %d times for the episode, want 1", msgWriteFail, r.logs.count(msgWriteFail))

	r.disk.Set(ample)
	fits := batchOf(epoch.Add(3*time.Hour), "fits", 2, 200)
	mustPut(t, sp, fits)
	recs := readAll(t, sp)
	requireUnknownGap(t, recs, epoch.Add(time.Hour), epoch.Add(2*time.Hour))
	requireSame(t, "records after the gap", dataOf(recs), numbered(fits, sp.LastSeq()-1))
	expect(t, r.logs.count(msgWriteOK) == 1, "%q was logged %d times, want 1", msgWriteOK, r.logs.count(msgWriteOK))
	r.disk.mu.Lock()
	defer r.disk.mu.Unlock()
	for _, d := range r.disk.dirs {
		expect(t, d == r.dir(), "Available was asked about %q, want the spool directory", d)
	}
}

// requireUnknownGap checks that recs hold exactly one gap of cause unknown from the agent, spanning from to to.
func requireUnknownGap(t *testing.T, recs []model.Record, from, to time.Time) {
	t.Helper()

	gaps := gapsOf(recs, model.GapUnknown)
	need(t, len(gaps) == 1, "%d unknown gaps are kept, want 1 for the episode", len(gaps))
	gp := gapOf(gaps[0])
	expect(t, gp.From.Equal(from) && gp.To.Equal(to), "gap spans %v to %v, want %v to %v (the earliest to the latest capture time of the refused records)", gp.From, gp.To, from, to)
	requireAgentGap(t, gaps[0])
	expect(t, len(gapsOf(recs, model.GapSpoolDropped)) == 0, "spool_dropped gaps are kept after a refusal, want none")
}

func TestPut_FreeSpaceReserve(t *testing.T) {
	const reserve = 8 * mebi

	t.Run("the reserve is kept by dropping the oldest records", func(t *testing.T) { reserveKeptByDropping(t, reserve) })
	t.Run("plenty of space drops nothing", func(t *testing.T) { plentyOfSpace(t, reserve) })
	t.Run("an empty spool below the reserve refuses and writes the gap later", func(t *testing.T) {
		refusedThenWritten(t, 1*mebi, uint64(1*mebi-1), uint64(16*mebi))
	})
	t.Run("available space below the write with a reserve of one byte", func(t *testing.T) {
		refusedThenWritten(t, 1, 100, uint64(16*mebi))
	})
	t.Run("no space at all with a reserve of one byte", func(t *testing.T) {
		refusedThenWritten(t, 1, 0, uint64(16*mebi))
	})
	t.Run("a reserve of zero switches the check off", func(t *testing.T) {
		r := newRig(t)
		r.opts.MinFreeBytes = 0
		r.disk.Set(0)
		sp := r.open()
		all := putTagged(t, sp, epoch, "off", 3, 200)
		requireSame(t, "records with no space reported and the check off", readAll(t, sp), all)
		expect(t, len(warnings(r.logs)) == 0, "warnings logged with the check off: %v", warnings(r.logs))
	})
}

// writeFault is the replacement of the write seam: it passes, fails or stops short.
type writeFault struct {
	mu   sync.Mutex
	mode int
	err  error
}

const (
	faultPass = iota
	faultFail
	faultShort
)

func (w *writeFault) set(mode int, err error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.mode, w.err = mode, err
}

func (w *writeFault) write(f *os.File, b []byte, off int64) (int, error) {
	w.mu.Lock()
	mode, err := w.mode, w.err
	w.mu.Unlock()
	switch mode {
	case faultFail:
		return 0, err
	case faultShort:
		n, werr := f.WriteAt(b[:len(b)/2], off)
		if werr != nil {
			return n, werr
		}

		return n, io.ErrShortWrite
	default:
		return f.WriteAt(b, off)
	}
}

func installFault(t *testing.T) *writeFault {
	t.Helper()

	w := &writeFault{}
	t.Cleanup(SetWriteFile(w.write))

	return w
}

// refuseWhileFailing hands over batches while writes fail and checks that each is refused with the system error.
func refuseWhileFailing(t *testing.T, r *rig, sp *Spool, errno error, batches ...[]model.Record) {
	t.Helper()

	for _, b := range batches {
		err := sp.Put(context.Background(), slices.Clone(b))
		need(t, err != nil, "Put while the writes fail = nil, want an error")
		expect(t, errno == nil || strings.Contains(err.Error(), errno.Error()), "Put error = %q, want the system error text %v", err.Error(), errno)
		requireNoPath(t, "Put error", err.Error(), r.dir())
	}
}

func runWriteFailure(t *testing.T, mode int, errno error) {
	t.Helper()

	fault := installFault(t)
	r := newRig(t)
	sp := r.open()
	before := putTagged(t, sp, epoch, "ok", 3, 200)
	size := diskBytes(t, r.dir())

	fault.set(mode, errno)
	refuseWhileFailing(t, r, sp, errno,
		[]model.Record{logRec(epoch.Add(time.Hour), "rb-0", 200), logRec(epoch.Add(time.Hour+30*time.Second), "rb-1", 200)},
		[]model.Record{logRec(epoch.Add(2*time.Hour), "rc-0", 200)})
	expect(t, diskBytes(t, r.dir()) == size, "the spool's files take %d bytes after the refused hand-overs, want %d (the active file cut back to its last good record)", diskBytes(t, r.dir()), size)
	fails := r.logs.find(msgWriteFail)
	need(t, len(fails) == 1 && fails[0].level == slog.LevelWarn, "log lines %q = %v, want one warning for the episode", msgWriteFail, fails)
	expect(t, errno == nil || strings.Contains(fails[0].str("err"), errno.Error()), "%q has err %q, want it to hold %v", msgWriteFail, fails[0].str("err"), errno)
	expect(t, r.logs.count(msgWriteOK) == 0, "%q was logged while writes fail", msgWriteOK)

	fault.set(faultPass, nil)
	after := batchOf(epoch.Add(3*time.Hour), "after", 2, 200)
	mustPut(t, sp, after)
	expect(t, r.logs.count(msgWriteOK) == 1, "%q was logged %d times, want 1", msgWriteOK, r.logs.count(msgWriteOK))
	recs := readAll(t, sp)
	requireUnknownGap(t, recs, epoch.Add(time.Hour), epoch.Add(2*time.Hour))
	want := concat2(before, numbered(after, sp.LastSeq()-1))
	requireSame(t, "records around the gap", dataOf(recs), want)
	requireNoLeak(t, r)

	r.logs.reset()
	sp = r.reopen(sp)
	requireSame(t, "records after a restart", dataOf(readAll(t, sp)), want)
	expect(t, r.logs.count(msgDamaged) == 0, "%q was logged %d times after the failed writes, want 0", msgDamaged, r.logs.count(msgDamaged))
}

func TestPut_WriteFailureEpisode(t *testing.T) {
	for _, tc := range []struct {
		name string
		mode int
		err  error
	}{
		{"no space left", faultFail, syscall.ENOSPC},
		{"i/o error", faultFail, syscall.EIO},
		{"read-only file system", faultFail, syscall.EROFS},
		{"short write", faultShort, nil},
	} {
		t.Run(tc.name, func(t *testing.T) { runWriteFailure(t, tc.mode, tc.err) })
	}
}

func TestPut_LargerThanCap(t *testing.T) {
	r := newRig(t)
	r.opts.MaxBytes = 256 << 10
	sp := r.open()

	refused := batchOf(epoch.Add(time.Hour), "huge", 100, 4000)
	refused[len(refused)-1].CapturedAt = epoch.Add(2 * time.Hour)
	err := sp.Put(context.Background(), slices.Clone(refused))
	need(t, err != nil, "Put of 400 KB into a spool capped at 256 KiB = nil, want it refused")
	expect(t, diskBytes(t, r.dir()) <= r.opts.MaxBytes, "the spool's files take %d bytes after the refusal, want at most %d", diskBytes(t, r.dir()), r.opts.MaxBytes)
	expect(t, r.logs.count(msgWriteFail) == 1, "%q was logged %d times, want 1", msgWriteFail, r.logs.count(msgWriteFail))

	fits := batchOf(epoch.Add(3*time.Hour), "fits", 3, 500)
	mustPut(t, sp, fits)
	recs := readAll(t, sp)
	requireUnknownGap(t, recs, epoch.Add(time.Hour), epoch.Add(2*time.Hour))
	requireSame(t, "records after the gap", dataOf(recs), numbered(fits, sp.LastSeq()-2))
	expect(t, r.logs.count(msgWriteOK) == 1, "%q was logged %d times, want 1", msgWriteOK, r.logs.count(msgWriteOK))
	expect(t, diskBytes(t, r.dir()) <= r.opts.MaxBytes, "the spool's files take %d bytes, want at most %d", diskBytes(t, r.dir()), r.opts.MaxBytes)
}
