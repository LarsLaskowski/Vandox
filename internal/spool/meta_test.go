package spool

import (
	"context"
	"log/slog"
	"math"
	"os"
	"path/filepath"
	"slices"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// wantBound is the clock bound as the spec states it: the clock's microseconds since the Unix epoch plus 1,000,000.
func wantBound(t testing.TB, now time.Time) uint64 {
	t.Helper()

	micros := now.UnixMicro()
	if micros < 0 {
		t.Fatalf("UnixMicro(%v) = %d, want a time after 1970", now, micros)

		return 0
	}

	return uint64(micros) + 1_000_000
}

func TestClockBound(t *testing.T) {
	tests := []struct {
		name string
		now  time.Time
		want uint64
	}{
		{"the Unix epoch", time.Unix(0, 0).UTC(), 1_000_000},
		{"one microsecond after the epoch", time.Unix(0, 1000).UTC(), 1_000_001},
		{"nanoseconds are cut off", time.Unix(5, 1999).UTC(), 5_000_001 + 1_000_000},
		{"a day in 2026", time.Date(2026, time.October, 23, 18, 41, 0, 123456789, time.UTC), 1_792_780_860_123_456 + 1_000_000},
		{"another zone, the same instant", time.Date(2026, time.October, 23, 20, 41, 0, 123456789, time.FixedZone("CEST", 2*3600)), 1_792_780_860_123_456 + 1_000_000},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := ClockBound(tc.now); got != tc.want {
				t.Errorf("ClockBound(%v) = %d, want %d", tc.now, got, tc.want)
			}
			if got := wantBound(t, tc.now); got != tc.want {
				t.Fatalf("the test's own expectation for %v is %d, want %d", tc.now, got, tc.want)
			}
		})
	}
}

// removeSpoolFiles removes every file of the spool from dir and nothing else.
func removeSpoolFiles(t *testing.T, dir string) {
	t.Helper()

	entries, err := os.ReadDir(dir)
	needNoErr(t, "ReadDir", err)
	for _, e := range entries {
		if isSpoolName(e.Name()) {
			needNoErr(t, "Remove("+e.Name()+")", os.Remove(filepath.Join(dir, e.Name())))
		}
	}
}

// putBurst hands 100,000 metric records to the spool in ten hand-overs.
func putBurst(t *testing.T, sp *Spool) {
	t.Helper()

	for range 10 {
		burst := make([]model.Record, 0, 10_000)
		for i := range 10_000 {
			burst = append(burst, metricRec(epoch, "burst_metric", float64(i)))
		}
		mustPut(t, sp, burst)
	}
}

func freshSpoolStartsAtClockBound(t *testing.T) {
	t.Helper()

	r := newRig(t)
	sp := r.open()
	mustPut(t, sp, batchOf(epoch, "fresh", 1, 100))
	expect(t, sp.LastSeq() == wantBound(t, epoch), "the first number of a fresh spool is %d, want the clock bound %d", sp.LastSeq(), wantBound(t, epoch))
}

func removedFilesAfterBurst(t *testing.T) {
	t.Helper()

	r := newRig(t)
	sp := r.open()
	putBurst(t, sp)
	last := sp.LastSeq()
	need(t, last == wantBound(t, epoch)+99_999, "after 100,000 records within one microsecond the last number is %d, want %d", last, wantBound(t, epoch)+99_999)
	r.closeSpool(sp)
	removeSpoolFiles(t, r.dir())
	r.clk.Advance(time.Second)

	sp = r.open()
	mustPut(t, sp, batchOf(epoch.Add(time.Second), "again", 1, 100))
	expect(t, sp.LastSeq() > last, "the first number of the new spool is %d, want a number above the %d of the earlier one", sp.LastSeq(), last)
	expect(t, sp.LastSeq() == wantBound(t, r.clk.Now()), "the first number of the new spool is %d, want the clock bound %d", sp.LastSeq(), wantBound(t, r.clk.Now()))
}

func rolledBackCounter(t *testing.T) {
	t.Helper()

	r := newRig(t)
	sp := r.open()
	metaAtOpen, err := os.ReadFile(filepath.Join(r.dir(), metaName))
	needNoErr(t, "ReadFile(meta)", err)
	issued := putTagged(t, sp, epoch, "lost", 5, 100)

	c := rigCopy(r, copyDir(t, r.dir()))
	removeSpoolFiles(t, c.dir())
	writeSegment(t, c.dir(), metaName, metaAtOpen)

	sp2 := c.open()
	mustPut(t, sp2, batchOf(epoch.Add(time.Minute), "after", 1, 100))
	expect(t, sp2.LastSeq() > issued[4].Seq, "the first number after the crash is %d, want a number above the %d issued before", sp2.LastSeq(), issued[4].Seq)
	expect(t, sp2.LastSeq() == wantBound(t, c.clk.Now()), "the first number after the crash is %d, want the clock bound %d", sp2.LastSeq(), wantBound(t, c.clk.Now()))
}

func emptyNewestFileAfterKill(t *testing.T) {
	t.Helper()

	r := newRig(t)
	start := wantBound(t, epoch) + 5_000_000_000
	writeSegment(t, r.dir(), segName(start), encodeSegment(t, segSpec{agent: testAgent, boot: testBoot}).data)
	writeMeta(t, r.dir(), metaSpec{agent: testAgent, boot: testBoot, next: start - 100})

	sp := r.open()
	mustPut(t, sp, batchOf(epoch, "named", 2, 100))
	need(t, sp.LastSeq() > start, "the numbers after the kill end at %d, want numbers from %d on (the empty newest file's name)", sp.LastSeq(), start)
	all := dataOf(readAll(t, sp))
	sp = r.reopen(sp)
	again := dataOf(readAll(t, sp))
	requireSame(t, "records after a restart", again, all)
	expect(t, r.logs.count(msgDamaged) == 0, "%q was logged %d times, want 0 (a file named %d holds the number %d first)", msgDamaged, r.logs.count(msgDamaged), start, seqAt(again, 0))
}

func TestOpen_SequenceAfterLoss(t *testing.T) {
	t.Run("a fresh spool starts at the clock bound", freshSpoolStartsAtClockBound)
	t.Run("a new spool after the files were removed passes a burst of 100,000 numbers", removedFilesAfterBurst)
	t.Run("an unclean stop in an earlier boot with a rolled-back counter continues above the clock bound", rolledBackCounter)
	t.Run("a kill in the same boot leaves an empty newest file that the next number does not go below", emptyNewestFileAfterKill)
}

const metaRecords = 20

// brokenMeta is a meta file that breaks one consistency rule of the spec (E20); first is the first number of the records.
type brokenMeta struct {
	name string
	meta func(first uint64) metaSpec
}

func brokenMetas() []brokenMeta {
	const n = metaRecords

	return []brokenMeta{
		{"acknowledged ranges not sorted", func(f uint64) metaSpec {
			return metaSpec{next: f + n, acked: [][2]uint64{{f + 10, f + 12}, {f + 2, f + 4}}}
		}},
		{"acknowledged ranges overlapping", func(f uint64) metaSpec {
			return metaSpec{next: f + n, acked: [][2]uint64{{f + 2, f + 6}, {f + 5, f + 8}}}
		}},
		{"a range with a first number above the last", func(f uint64) metaSpec {
			return metaSpec{next: f + n, acked: [][2]uint64{{f + 9, f + 5}}}
		}},
		{"a range ending at or above the next number", func(f uint64) metaSpec {
			return metaSpec{next: f + n, acked: [][2]uint64{{f + 18, f + n + 5}}}
		}},
		{"a range starting at the next number", func(f uint64) metaSpec {
			return metaSpec{next: f + n, acked: [][2]uint64{{f + n, f + n + 2}}}
		}},
		{"a next number of 2^63 - 1", func(uint64) metaSpec { return metaSpec{next: math.MaxInt64} }},
		{"a next number of 2^64 - 1", func(uint64) metaSpec { return metaSpec{next: math.MaxUint64} }},
	}
}

// metaSpool writes metaRecords records, closes the spool and returns the rig and the first number.
func metaSpool(t *testing.T) (*rig, uint64) {
	t.Helper()

	r := newRig(t)
	sp := r.open()
	recs := putTagged(t, sp, epoch, "meta", metaRecords, 100)
	r.closeSpool(sp)

	return r, recs[0].Seq
}

func runBrokenMeta(t *testing.T, tc brokenMeta) {
	t.Helper()

	r, first := metaSpool(t)
	m := tc.meta(first)
	m.agent, m.boot, m.clean = testAgent, testBoot, true
	writeMeta(t, r.dir(), m)
	r.clk.Advance(10 * time.Second)

	sp := r.open()
	lines := r.logs.find(msgDamaged)
	need(t, len(lines) == 1 && lines[0].level == slog.LevelWarn && lines[0].str("file") == metaName && lines[0].str("err") == errInconsistent,
		"log lines %q = %v, want one warning for %s with err %q", msgDamaged, lines, metaName, errInconsistent)
	expectNumbers(t, "numbers handed out (the acknowledgements are discarded)", readNumbers(t, sp), rangeNumbers(first, first+metaRecords-1))
	mustPut(t, sp, batchOf(epoch.Add(time.Hour), "next", 1, 100))
	expect(t, sp.LastSeq() == wantBound(t, r.clk.Now()), "the next number is %d, want the clock bound %d as on a fresh spool", sp.LastSeq(), wantBound(t, r.clk.Now()))
	requireNoLeak(t, r)
}

func consistentMetaHonored(t *testing.T) {
	t.Helper()

	r, first := metaSpool(t)
	writeMeta(t, r.dir(), metaSpec{agent: testAgent, boot: testBoot, clean: true, next: first + metaRecords, acked: [][2]uint64{{first, first + 4}, {first + 10, first + 12}}})

	sp := r.open()
	expect(t, r.logs.count(msgDamaged) == 0, "%q was logged %d times for a consistent meta file, want 0", msgDamaged, r.logs.count(msgDamaged))
	expectNumbers(t, "numbers handed out", readNumbers(t, sp), concat(rangeNumbers(first+5, first+9), rangeNumbers(first+13, first+metaRecords-1)))
	mustPut(t, sp, batchOf(epoch.Add(time.Hour), "next", 1, 100))
	expect(t, sp.LastSeq() == first+metaRecords, "the next number is %d, want %d (a clean stop continues the numbers)", sp.LastSeq(), first+metaRecords)
}

func TestOpen_MetaInconsistent(t *testing.T) {
	for _, tc := range brokenMetas() {
		t.Run(tc.name, func(t *testing.T) { runBrokenMeta(t, tc) })
	}
	t.Run("a consistent meta file is honored", consistentMetaHonored)
}

const exhaustedText = "spool: sequence numbers exhausted"

// exhaustingRig returns a rig whose clock bound leaves exactly three numbers below 2^63 - 1, with its open spool.
func exhaustingRig(t *testing.T) (*rig, *Spool, uint64) {
	t.Helper()

	start := uint64(math.MaxInt64) - 3
	now := time.UnixMicro(i64(t, start-1_000_000)).UTC()
	need(t, wantBound(t, now) == start, "the clock bound of the test clock is %d, want %d", wantBound(t, now), start)
	r := newRig(t)
	r.clk.Set(now)

	return r, r.open(), start
}

func lastNumberStillIssued(t *testing.T) {
	t.Helper()

	r, sp, start := exhaustingRig(t)
	limit := uint64(math.MaxInt64)
	mustPut(t, sp, batchOf(epoch, "last", 3, 100))
	need(t, sp.LastSeq() == limit-1, "after 3 records LastSeq() = %d, want %d (2^63 - 2)", sp.LastSeq(), limit-1)
	err := sp.Put(context.Background(), batchOf(epoch.Add(time.Second), "over", 1, 100))
	need(t, err != nil && err.Error() == exhaustedText, "Put when the next number would be 2^63 - 1 = %v, want the error %q", err, exhaustedText)
	expect(t, sp.LastSeq() == limit-1, "LastSeq() after the refusal = %d, want %d", sp.LastSeq(), limit-1)
	expectNumbers(t, "numbers handed out", readNumbers(t, sp), []uint64{start, start + 1, start + 2})
	fails := r.logs.find(msgWriteFail)
	expect(t, len(fails) == 1 && fails[0].str("err") == exhaustedText, "log lines %q = %v, want one with err %q", msgWriteFail, fails, exhaustedText)
}

func refusedWholeWhenTooFewRemain(t *testing.T) {
	t.Helper()

	_, sp, start := exhaustingRig(t)
	limit := uint64(math.MaxInt64)
	mustPut(t, sp, batchOf(epoch, "two", 2, 100))
	err := sp.Put(context.Background(), batchOf(epoch.Add(time.Second), "three", 2, 100))
	need(t, err != nil && err.Error() == exhaustedText, "Put of 2 records with 1 number left = %v, want the error %q", err, exhaustedText)
	expect(t, sp.LastSeq() == limit-2, "LastSeq() after the refusal = %d, want %d", sp.LastSeq(), limit-2)
	expectNumbers(t, "numbers handed out (only the first hand-over, all below 2^63 - 1)", readNumbers(t, sp), []uint64{start, start + 1})
}

func TestPut_SequenceExhausted(t *testing.T) {
	t.Run("the last number below the limit can still be issued", lastNumberStillIssued)
	t.Run("a hand-over that needs more numbers than remain is refused whole", refusedWholeWhenTooFewRemain)
}

func TestOpen_OtherAgentID(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	old := putTagged(t, sp, epoch, "old", 5, 100)
	r.closeSpool(sp)
	oldName := segName(old[0].Seq)
	_, err := os.Lstat(filepath.Join(r.dir(), oldName))
	needNoErr(t, "Lstat of the earlier spool's file "+oldName, err)

	r.opts.AgentID = "agent-two"
	r.clk.Advance(time.Second)
	r.logs.reset()
	sp = r.open()

	got := readAll(t, sp)
	expect(t, len(got) == 0, "%d records of another agent are handed out (numbers %v), want none and no gap", len(got), seqsHead(got))
	lines := r.logs.find("spool records of another agent dropped")
	need(t, len(lines) == 1 && lines[0].level == slog.LevelWarn, "log lines %q = %v, want one warning", "spool records of another agent dropped", lines)
	n, ok := lines[0].num("records")
	expect(t, ok && n == 5, "the warning has records = %v, %v, want 5", n, ok)
	expect(t, r.logs.count("spool created") <= 1, "spool created was logged %d times, want at most once", r.logs.count("spool created"))
	_, err = os.Lstat(filepath.Join(r.dir(), oldName))
	expect(t, os.IsNotExist(err), "Lstat(%s) = %v, want the file of the other agent deleted", oldName, err)

	mustPut(t, sp, batchOf(epoch.Add(time.Hour), "new", 2, 100))
	expect(t, sp.LastSeq() == wantBound(t, r.clk.Now())+1, "the numbers of the new agent end at %d, want %d (from the clock bound %d)", sp.LastSeq(), wantBound(t, r.clk.Now())+1, wantBound(t, r.clk.Now()))
	pieces := readPieces(t, sp, 0, 100, 8*mebi)
	need(t, len(pieces) == 1, "Read returned %d pieces, want 1", len(pieces))
	expect(t, pieces[0].AgentID == "agent-two" && len(pieces[0].Records) == 2, "the piece is under agent %q with %d records, want agent-two with the 2 new records", pieces[0].AgentID, len(pieces[0].Records))
	requireNoLeak(t, r)
}

// manyRangesMeta writes a meta file of n single-number acknowledged ranges (every second number) over a closed spool
// of 2n+4 records and opens it. It returns the rig, the spool and the first number.
func manyRangesMeta(t *testing.T, n int) (*rig, *Spool, uint64) {
	t.Helper()

	r := newRig(t)
	sp := r.open()
	recs := putTagged(t, sp, epoch, "many", 2*n+4, 60)
	r.closeSpool(sp)
	first := recs[0].Seq
	var acked [][2]uint64
	for i := range n {
		num := first + ofLenInt(2*i+1)
		acked = append(acked, [2]uint64{num, num})
	}
	writeMeta(t, r.dir(), metaSpec{agent: testAgent, boot: testBoot, clean: true, next: first + ofLenInt(len(recs)), acked: acked})
	r.logs.reset()

	return r, r.open(), first
}

func TestOpen_MetaAckRangeLimit(t *testing.T) {
	t.Run("MaxAckRanges plus one ranges are honored", func(t *testing.T) {
		r, sp, first := manyRangesMeta(t, MaxAckRanges+1)
		expect(t, r.logs.count(msgDamaged) == 0, "%q was logged %d times for %d ranges, want 0", msgDamaged, r.logs.count(msgDamaged), MaxAckRanges+1)
		expect(t, len(readNumbers(t, sp)) == 2*(MaxAckRanges+1)+4-(MaxAckRanges+1), "%d records handed out, want the unacknowledged ones", len(readNumbers(t, sp)))
		expect(t, !slices.Contains(readNumbers(t, sp), first+1), "acknowledged number %d is handed out", first+1)
	})
	t.Run("MaxAckRanges plus two ranges are refused", func(t *testing.T) {
		r, sp, first := manyRangesMeta(t, MaxAckRanges+2)
		lines := r.logs.find(msgDamaged)
		need(t, len(lines) == 1 && lines[0].str("file") == metaName && lines[0].str("err") == errInconsistent,
			"log lines %q = %v, want one warning for %s with err %q", msgDamaged, lines, metaName, errInconsistent)
		expect(t, slices.Contains(readNumbers(t, sp), first+1), "the acknowledgements were honored, want them discarded")
	})
}
