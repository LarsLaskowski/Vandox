package spool

import (
	"context"
	"errors"
	"fmt"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

func TestPut_AssignsIncreasingSequenceNumbers(t *testing.T) {
	r := newRig(t)
	sp := r.open()

	var want []model.Record
	var prev uint64
	for i, n := range []int{1, 3, 2, 5} {
		batch := batchOf(epoch.Add(time.Duration(i)*time.Minute), fmt.Sprintf("b%d", i), n, 80+i*40)
		mustPut(t, sp, batch)
		last := sp.LastSeq()
		need(t, last > prev, "LastSeq() = %d after %d, want a strictly increasing number", last, prev)
		need(t, prev == 0 || last == prev+ofLen(t, n), "after Put of %d records LastSeq() = %d, want %d (the numbers follow each other)", n, last, prev+ofLen(t, n))
		want = append(want, numbered(batch, last-ofLen(t, n)+1)...)
		prev = last
	}

	pieces := readPieces(t, sp, 0, 1000, 8*mebi)
	need(t, len(pieces) == 1, "Read(0, 1000, 8 MiB) returned %d pieces, want 1 (one context)", len(pieces))
	b := pieces[0]
	expect(t, b.AgentID == testAgent && b.BootID == testBoot, "batch identity = agent %q boot %q, want %q and %q", b.AgentID, b.BootID, testAgent, testBoot)
	expect(t, b.ClockOffsetNS == nil, "batch ClockOffsetNS = %v, want none (unknown)", b.ClockOffsetNS)
	requireSame(t, "records read back", b.Records, want)
	for _, rec := range b.Records {
		expect(t, rec.Origin == model.OriginAgent, "record %d has origin %q, want %q", rec.Seq, rec.Origin, model.OriginAgent)
	}
}

// collect hands batches of one collector to the spool.
func collect(t *testing.T, sp *Spool, c, batches, perBatch int) {
	t.Helper()

	for b := range batches {
		recs := batchOf(epoch.Add(time.Duration(b)*time.Second), fmt.Sprintf("c%d-b%d", c, b), perBatch, 100)
		if err := sp.Put(context.Background(), recs); err != nil {
			t.Errorf("collector %d: Put(batch %d) = %v, want nil", c, b, err)

			return
		}
	}
}

// messageTag returns the first word of the message of a log line record.
func messageTag(t *testing.T, rec model.Record) string {
	t.Helper()

	line, ok := rec.Data.(*model.LogLine)
	need(t, ok, "record %d is %T, want a log line", rec.Seq, rec.Data)

	return strings.Fields(line.Message)[0]
}

func TestPut_ConcurrentCollectors(t *testing.T) {
	const collectors, batches, perBatch = 8, 40, 3

	r := newRig(t)
	sp := r.open()
	var wg sync.WaitGroup
	for c := range collectors {
		wg.Add(1)
		go func() {
			defer wg.Done()
			collect(t, sp, c, batches, perBatch)
		}()
	}
	wg.Wait()

	got := readAll(t, sp)
	need(t, len(got) == collectors*batches*perBatch, "Read returned %d records, want %d", len(got), collectors*batches*perBatch)
	next := map[int]int{}
	seen := map[string]bool{}
	for i, rec := range got {
		need(t, i == 0 || rec.Seq == got[i-1].Seq+1, "record %d has number %d after %d, want strictly increasing and contiguous", i, rec.Seq, seqAt(got, i-1))
		tag := messageTag(t, rec)
		need(t, !seen[tag], "record %q was read twice", tag)
		seen[tag] = true
		var c, b, k int
		_, err := fmt.Sscanf(tag, "c%d-b%d-%d", &c, &b, &k)
		needNoErr(t, "Sscanf("+tag+")", err)
		need(t, next[c] == b*perBatch+k, "collector %d: record %d of its batches came after %d, want the hand-over order kept", c, b*perBatch+k, next[c])
		next[c]++
	}
}

// seqAt returns the number of recs[i], or 0 when i is out of range.
func seqAt(recs []model.Record, i int) uint64 {
	if i < 0 || i >= len(recs) {
		return 0
	}

	return recs[i].Seq
}

// largestRecord returns a process snapshot whose wire line is exactly as long as the wire format allows (1 MiB), with
// the longest number a sequence number can have.
func largestRecord(t *testing.T) model.Record {
	t.Helper()

	at := epoch.Add(123456789 * time.Nanosecond)
	build := func(full int, lastLen int) model.Record {
		procs := make([]model.ProcessSample, 0, full+1)
		var pid int32
		for range full {
			pid++
			procs = append(procs, model.ProcessSample{PID: pid, Command: "php-fpm", Cmdline: filler(16000), State: "S", CPUPercent: 1.5, RSSBytes: 4096})
		}
		procs = append(procs, model.ProcessSample{PID: pid + 1, Command: "php-fpm", Cmdline: filler(lastLen), State: "S", CPUPercent: 1.5, RSSBytes: 4096})

		return model.Record{
			Meta: model.Meta{Origin: model.OriginAgent, Source: "processes", Seq: 1<<63 - 2, CapturedAt: at},
			Data: &model.ProcessSnapshot{Complete: true, Processes: procs},
		}
	}
	fits := func(rec model.Record) bool {
		_, err := wire.MarshalRecord(&rec)

		return err == nil
	}
	full := 0
	for fits(build(full+1, 0)) {
		full++
	}
	lo, hi := 0, 16000
	for lo < hi {
		mid := (lo + hi + 1) / 2
		if fits(build(full, mid)) {
			lo = mid
		} else {
			hi = mid - 1
		}
	}
	rec := build(full, lo)
	line, err := wire.MarshalRecord(&rec)
	need(t, err == nil && len(line) >= wire.DefaultLimits().MaxLineBytes-4, "largest process snapshot: line of %d bytes (err %v), want one just under %d", len(line), err, wire.DefaultLimits().MaxLineBytes)
	rec.Seq = 0

	return rec
}

func TestPut_LargestRecordRoundTrips(t *testing.T) {
	r := newRig(t)
	sp := r.open()

	big := largestRecord(t)
	batch := []model.Record{logRec(epoch, "before", 100), big, logRec(epoch.Add(time.Second), "after", 100)}
	mustPut(t, sp, batch)
	want := numbered(batch, sp.LastSeq()-2)

	requireSame(t, "records read back with the largest one in the middle", readAll(t, sp), want)

	sp = r.reopen(sp)
	requireSame(t, "records read back after a restart", readAll(t, sp), want)
}

func TestRead_FromSequenceNumber(t *testing.T) {
	r := newRig(t)
	sp := r.open()

	var all []model.Record
	for i, n := range []int{4, 3, 3} {
		all = append(all, putTagged(t, sp, epoch.Add(time.Duration(i)*time.Second), fmt.Sprintf("r%d", i), n, 100)...)
	}
	first, last := all[0].Seq, all[len(all)-1].Seq

	tests := []struct {
		name  string
		after uint64
		want  []model.Record
	}{
		{"zero reads from the oldest", 0, all},
		{"below the oldest reads everything", first - 1000, all},
		{"the number of the oldest reads the rest", first, all[1:]},
		{"the middle", all[3].Seq, all[4:]},
		{"the newest reads nothing", last, nil},
		{"above the newest reads nothing", last + 1000, nil},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			b, err := sp.Read(context.Background(), tc.after, 100, 8*mebi)
			needNoErr(t, fmt.Sprintf("Read(%d)", tc.after), err)
			requireSame(t, fmt.Sprintf("Read(after %d)", tc.after), b.Records, tc.want)
		})
	}
}

func TestRead_Limits(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	all := putTagged(t, sp, epoch, "lim", 10, 1000)

	tests := []struct {
		name       string
		maxRecords int
		maxBytes   int64
		want       int
	}{
		{"record limit three", 3, 8 * mebi, 3},
		{"record limit one", 1, 8 * mebi, 1},
		{"byte limit of about two records", 100, 2500, 2},
		{"both limits roomy", 100, 8 * mebi, 10},
		{"record limit above the count", 11, 8 * mebi, 10},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			b, err := sp.Read(context.Background(), 0, tc.maxRecords, tc.maxBytes)
			needNoErr(t, fmt.Sprintf("Read(0, %d, %d)", tc.maxRecords, tc.maxBytes), err)
			requireSame(t, fmt.Sprintf("Read(0, %d, %d)", tc.maxRecords, tc.maxBytes), b.Records, all[:tc.want])
		})
	}

	t.Run("reading on in pieces returns every record once", func(t *testing.T) {
		var got []model.Record
		for _, b := range readPieces(t, sp, 0, 4, 8*mebi) {
			expect(t, len(b.Records) <= 4, "a piece holds %d records, want at most 4", len(b.Records))
			got = append(got, b.Records...)
		}
		requireSame(t, "pieces of at most 4 records", got, all)
	})
}

// requirePiecesPerContext checks that no piece holds records of two boots, and returns all records read.
func requirePiecesPerContext(t *testing.T, pieces []Batch, secondFrom uint64) []model.Record {
	t.Helper()

	var got []model.Record
	for _, p := range pieces {
		for _, rec := range p.Records {
			want := testBoot
			if rec.Seq >= secondFrom {
				want = otherBoot
			}
			need(t, p.BootID == want, "a piece with boot %s holds record %d captured in boot %s", p.BootID, rec.Seq, want)
		}
		got = append(got, p.Records...)
	}

	return got
}

func TestRead_NeverMixesCaptureContext(t *testing.T) {
	t.Run("boot IDs", func(t *testing.T) {
		r := newRig(t)
		sp := r.open()
		first := putTagged(t, sp, epoch, "x", 5, 80)
		r.opts.BootID = otherBoot
		sp = r.reopen(sp)
		second := putTagged(t, sp, epoch.Add(time.Minute), "y", 5, 80)

		for _, maxRecords := range []int{100, 2} {
			got := requirePiecesPerContext(t, readPieces(t, sp, 0, maxRecords, 8*mebi), second[0].Seq)
			requireSame(t, fmt.Sprintf("all records, pieces of %d", maxRecords), got, concat2(first, second))
		}
		pieces := readPieces(t, sp, 0, 100, 8*mebi)
		expect(t, len(pieces) == 2, "Read with roomy limits returned %d pieces, want 2 (one per boot)", len(pieces))
	})

	t.Run("clock offset estimates", func(t *testing.T) {
		r := newRig(t)
		offset := int64(123456)
		f1 := fileSpec{first: forgedFirst, n: 4, sealed: true, boot: testBoot, capture: epoch, tag: "o1"}
		f2 := fileSpec{first: forgedFirst + 4, n: 4, boot: testBoot, offset: &offset, capture: epoch.Add(time.Minute), tag: "o2"}
		writeFiles(t, r.dir(), f1, f2)
		writeMeta(t, r.dir(), metaSpec{agent: testAgent, boot: testBoot, next: f2.last() + 1})
		sp := r.open()

		pieces := readPieces(t, sp, 0, 100, 8*mebi)
		need(t, len(pieces) == 2, "Read returned %d pieces, want 2 (offset unknown, then offset %d)", len(pieces), offset)
		expect(t, pieces[0].ClockOffsetNS == nil && len(pieces[0].Records) == 4, "first piece: offset %v with %d records, want no offset and 4 records", pieces[0].ClockOffsetNS, len(pieces[0].Records))
		got := pieces[1].ClockOffsetNS
		expect(t, got != nil && *got == offset && len(pieces[1].Records) == 4, "second piece: offset %v with %d records, want offset %d and 4 records", got, len(pieces[1].Records), offset)
	})
}

// drain reads and acknowledges a spool while another goroutine writes into it.
type drain struct {
	sp    *Spool
	after uint64
	tags  []string
}

// take checks one record read and remembers its tag.
func (d *drain) take(rec model.Record) error {
	line, ok := rec.Data.(*model.LogLine)
	if !ok || rec.Seq <= d.after {
		return fmt.Errorf("record %d after %d is %T, want a log line with a number above the last read", rec.Seq, d.after, rec.Data)
	}
	d.after = rec.Seq
	d.tags = append(d.tags, strings.Fields(line.Message)[0])

	return nil
}

// step reads and acknowledges one piece and returns the number of records in it.
func (d *drain) step() (int, error) {
	b, err := d.sp.Read(context.Background(), d.after, 64, mebi)
	if err != nil {
		return 0, fmt.Errorf("Read(after %d) = %w", d.after, err)
	}
	for _, rec := range b.Records {
		if err := d.take(rec); err != nil {
			return 0, err
		}
	}
	if len(b.Records) == 0 {
		return 0, nil
	}
	if err := d.sp.Ack(context.Background(), b.Records[0].Seq, d.after); err != nil {
		return 0, fmt.Errorf("Ack(%d, %d) = %w", b.Records[0].Seq, d.after, err)
	}

	return len(b.Records), nil
}

// run drains until the writer is done and nothing is left.
func (d *drain) run(done *atomic.Bool) error {
	for {
		finished := done.Load()
		n, err := d.step()
		if err != nil {
			return err
		}
		if n == 0 && finished {
			return nil
		}
		if n == 0 {
			runtime.Gosched()
		}
	}
}

func TestRead_ConcurrentWithPut(t *testing.T) {
	const batches, perBatch = 300, 2

	r := newRig(t)
	sp := r.open()

	var done atomic.Bool
	d := &drain{sp: sp}
	readerErr := make(chan error, 1)
	go func() { readerErr <- d.run(&done) }()

	for i := range batches {
		mustPut(t, sp, batchOf(epoch.Add(time.Duration(i)*time.Second), "w"+strconv.Itoa(i), perBatch, 200))
	}
	done.Store(true)
	needNoErr(t, "the reader", <-readerErr)
	need(t, len(d.tags) == batches*perBatch, "the reader read %d records, want %d (each exactly once)", len(d.tags), batches*perBatch)
	for i, tag := range d.tags {
		want := fmt.Sprintf("w%d-%d", i/perBatch, i%perBatch)
		need(t, tag == want, "record %d read is %q, want %q (in order, none twice, none torn)", i, tag, want)
	}
}

func TestAck_NeverHandedOutAgain(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	all := putTagged(t, sp, epoch, "a", 20, 100)

	mustAck(t, sp, all[0].Seq, all[4].Seq)
	requireSame(t, "after acknowledging the oldest five", readAll(t, sp), all[5:])

	mustAck(t, sp, all[15].Seq, all[19].Seq)
	requireSame(t, "after acknowledging the newest five before the older ones", readAll(t, sp), all[5:15])

	mustAck(t, sp, all[8].Seq, all[9].Seq)
	requireSame(t, "after acknowledging two in the middle", readAll(t, sp), concat2(all[5:8], all[10:15]))

	mustAck(t, sp, all[5].Seq, all[14].Seq)
	got := readAll(t, sp)
	need(t, len(got) == 0, "after acknowledging everything Read returned %d records, want none", len(got))
}

func TestAck_SurvivesRestart(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	all := putTagged(t, sp, epoch, "s", 30, 100)
	mustAck(t, sp, all[0].Seq, all[4].Seq)
	mustAck(t, sp, all[10].Seq, all[14].Seq)
	mustAck(t, sp, all[25].Seq, all[29].Seq)
	want := concat2(all[5:10], all[15:25])

	killed := r.snapshot()
	t.Run("after a stop", func(t *testing.T) {
		sp = r.reopen(sp)
		requireSame(t, "records after Close and Open", readAll(t, sp), want)
	})
	t.Run("after a kill in the same boot", func(t *testing.T) {
		requireSame(t, "records after a kill and Open", readAll(t, killed.open()), want)
	})
}

// concat2 joins record lists.
func concat2(lists ...[]model.Record) []model.Record {
	var out []model.Record
	for _, l := range lists {
		out = append(out, l...)
	}

	return out
}

func TestAck_NoEffectCases(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	old := putTagged(t, sp, epoch, "old", 5, 100)
	// An unclean stop in an earlier boot leaves a jump in the numbers: the numbers between the two runs were never issued.
	killed := rigCopy(r, copyDir(t, r.dir()))
	sp2 := killed.open()
	fresh := putTagged(t, sp2, epoch.Add(time.Minute), "new", 5, 100)
	need(t, fresh[0].Seq > old[4].Seq+10, "the restart after the unclean stop continued at %d after %d, want a jump", fresh[0].Seq, old[4].Seq)
	all := concat2(old, fresh)

	mustAck(t, sp2, all[0].Seq, all[1].Seq)
	remaining := all[2:]

	tests := []struct {
		name        string
		first, last uint64
	}{
		{"above the newest number", fresh[4].Seq + 1, fresh[4].Seq + 1000},
		{"below the oldest number", 1, all[0].Seq - 1},
		{"already acknowledged", all[0].Seq, all[1].Seq},
		{"inside the jump, never issued", old[4].Seq + 1, fresh[0].Seq - 1},
		{"a single number never issued", fresh[0].Seq - 1, fresh[0].Seq - 1},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			_ = sp2.Ack(context.Background(), tc.first, tc.last)
			requireSame(t, fmt.Sprintf("records after Ack(%d, %d)", tc.first, tc.last), readAll(t, sp2), remaining)
		})
	}
}

// ackSingles acknowledges the numbers of recs at the given indexes, one range each.
func ackSingles(t *testing.T, sp *Spool, recs []model.Record, indexes ...int) {
	t.Helper()

	for _, i := range indexes {
		mustAck(t, sp, recs[i].Seq, recs[i].Seq)
	}
}

func TestAck_TooManyRanges(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	all := putTagged(t, sp, epoch, "t", 200, 60)

	// 64 disjoint ranges above the oldest unacknowledged record (all[0]): the numbers at the odd indexes 1, 3, ..., 127.
	var odd []int
	want := []model.Record{all[0]}
	for i := range MaxAckRanges {
		odd = append(odd, 2*i+1)
		want = append(want, all[2*i+2])
	}
	want = append(want, all[2*MaxAckRanges+1:]...)
	ackSingles(t, sp, all, odd...)
	requireSame(t, "after 64 disjoint ranges", readAll(t, sp), want)

	extra := all[2*MaxAckRanges+2]
	err := sp.Ack(context.Background(), extra.Seq, extra.Seq)
	need(t, errors.Is(err, ErrTooManyRanges), "Ack of a 65th disjoint range = %v, want ErrTooManyRanges", err)
	expect(t, err.Error() == "spool: more than 64 acknowledged ranges", "error text = %q, want %q", err.Error(), "spool: more than 64 acknowledged ranges")
	requireSame(t, "after the refused 65th range", readAll(t, sp), want)

	// Acknowledging the oldest record joins the first range with the oldest end: 63 ranges remain above the oldest unacknowledged record.
	ackSingles(t, sp, all, 0)
	ackSingles(t, sp, all, 2*MaxAckRanges+2)
	for _, rec := range readAll(t, sp) {
		expect(t, rec.Seq != all[0].Seq && rec.Seq != extra.Seq, "record %d is handed out after it was acknowledged", rec.Seq)
	}
}

func TestAck_AllAcknowledgedShrinks(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	var firstNumber, last uint64
	for i := range 140 {
		recs := putTagged(t, sp, epoch.Add(time.Duration(i)*time.Second), fmt.Sprintf("big%d", i), 20, 2000)
		if i == 0 {
			firstNumber = recs[0].Seq
		}
		last = recs[len(recs)-1].Seq
	}
	files := len(segmentNumbers(t, r.dir()))
	need(t, files >= 4, "5.6 MB of records are in %d data files, want at least 4 (files of 1 MiB)", files)
	before, err := sp.SpoolBytes(context.Background())
	need(t, err == nil && before >= 5*mebi, "SpoolBytes() = %d, %v before the acknowledgement, want the size of the records", before, err)

	mustAck(t, sp, firstNumber, last)

	limit := r.opts.MaxBytes / 16
	after, err := sp.SpoolBytes(context.Background())
	expect(t, err == nil && after <= limit, "SpoolBytes() = %d, %v after everything is acknowledged, want at most %d (one sixteenth of the cap)", after, err, limit)
	disk := diskBytes(t, r.dir())
	expect(t, disk <= limit, "the spool's files take %d bytes after everything is acknowledged, want at most %d", disk, limit)
	n := len(segmentNumbers(t, r.dir()))
	expect(t, n == 1, "%d data files remain after everything is acknowledged, want only the active one", n)
	expect(t, len(readAll(t, sp)) == 0, "Read returned records after everything is acknowledged, want none")
	_, ok, err := sp.OldestCapture(context.Background())
	expect(t, err == nil && !ok, "OldestCapture() reports a record (%v, %v) after everything is acknowledged, want none", ok, err)
}

func TestOldestCapture_FollowsTheOldestUnacknowledgedRecord(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	_, ok, err := sp.OldestCapture(context.Background())
	need(t, err == nil && !ok, "OldestCapture() on an empty spool = _, %v, %v, want false", ok, err)
	var all []model.Record
	for i := range 4 {
		all = append(all, putTagged(t, sp, epoch.Add(time.Duration(i)*time.Hour), fmt.Sprintf("oc%d", i), 2, 100)...)
	}
	check := func(want time.Time) {
		t.Helper()

		got, ok, err := sp.OldestCapture(context.Background())
		need(t, err == nil && ok && got.Equal(want), "OldestCapture() = %v, %v, %v, want %v", got, ok, err, want)
	}
	check(epoch)
	mustAck(t, sp, all[0].Seq, all[1].Seq)
	check(epoch.Add(time.Hour))
	mustAck(t, sp, all[6].Seq, all[7].Seq)
	check(epoch.Add(time.Hour))
	mustAck(t, sp, all[2].Seq, all[5].Seq)
	_, ok, err = sp.OldestCapture(context.Background())
	need(t, err == nil && !ok, "OldestCapture() with everything acknowledged = _, %v, %v, want false", ok, err)
}

func TestClose_KeepsRecordsAndContinuesNumbers(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	before := putTagged(t, sp, epoch, "c", 5, 100)
	last := sp.LastSeq()
	r.closeSpool(sp)

	sp = r.open()
	requireSame(t, "records after Close and Open", readAll(t, sp), before)
	expect(t, sp.LastSeq() == last, "LastSeq() after the restart = %d, want %d", sp.LastSeq(), last)
	after := putTagged(t, sp, epoch.Add(time.Minute), "d", 3, 100)
	expect(t, after[0].Seq == last+1, "the first number after the restart is %d, want %d (directly after the last one issued)", after[0].Seq, last+1)
	requireSame(t, "records after the second Put", readAll(t, sp), concat2(before, after))

	killed := r.snapshot()
	k := killed.open()
	more := putTagged(t, k, epoch.Add(2*time.Minute), "e", 2, 100)
	expect(t, more[0].Seq == after[2].Seq+1, "the first number after a kill in the same boot is %d, want %d", more[0].Seq, after[2].Seq+1)
	requireSame(t, "records after a kill and Open", readAll(t, k), concat2(before, after, more))
}

func TestOpen_NewBootKeepsOldBootID(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	old := putTagged(t, sp, epoch, "old", 4, 100)
	r.opts.BootID = otherBoot
	r.clk.Advance(time.Hour)
	sp = r.reopen(sp)
	fresh := putTagged(t, sp, epoch.Add(time.Hour), "new", 4, 100)

	expect(t, fresh[0].Seq == old[3].Seq+1, "the first number in the new boot is %d, want %d (a clean stop continues the numbers)", fresh[0].Seq, old[3].Seq+1)
	pieces := readPieces(t, sp, 0, 100, 8*mebi)
	need(t, len(pieces) == 2, "Read returned %d pieces, want 2 (the old boot, then the new one)", len(pieces))
	expect(t, pieces[0].BootID == testBoot && pieces[1].BootID == otherBoot, "boot IDs of the pieces = %s, %s, want %s, %s", pieces[0].BootID, pieces[1].BootID, testBoot, otherBoot)
	requireSame(t, "records of the old boot", pieces[0].Records, old)
	requireSame(t, "records of the new boot", pieces[1].Records, fresh)
}

func TestOpen_RestartAppendsToNewest(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	all := putTagged(t, sp, epoch, "r0", 3, 100)
	for i := 1; i <= 5; i++ {
		sp = r.reopen(sp)
		all = append(all, putTagged(t, sp, epoch.Add(time.Duration(i)*time.Minute), fmt.Sprintf("r%d", i), 3, 100)...)
		n := len(segmentNumbers(t, r.dir()))
		need(t, n == 1, "after %d restarts in the same boot there are %d data files, want 1", i, n)
	}
	killed := r.snapshot()
	k := killed.open()
	all = append(all, putTagged(t, k, epoch.Add(time.Hour), "kill", 3, 100)...)
	n := len(segmentNumbers(t, killed.dir()))
	need(t, n == 1, "after a kill and a start in the same boot there are %d data files, want 1", n)
	requireSame(t, "all records over the restarts", readAll(t, k), all)

	r.opts.BootID = otherBoot
	sp = r.reopen(sp)
	putTagged(t, sp, epoch.Add(2*time.Hour), "boot", 3, 100)
	n = len(segmentNumbers(t, r.dir()))
	need(t, n == 2, "after a restart in a new boot there are %d data files, want 2", n)
}

func TestSync_AfterPutSucceeds(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	all := putTagged(t, sp, epoch, "sync", 5, 100)

	needNoErr(t, "Sync()", sp.Sync(context.Background()))
	killed := r.snapshot()
	requireSame(t, "records after Sync and a kill", readAll(t, killed.open()), all)
}

func TestAck_MaxRangesSurviveRestart(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	all := putTagged(t, sp, epoch, "t", 200, 60)

	// A range that starts at the oldest kept number is not counted against the limit, so 65 ranges can be held.
	mustAck(t, sp, all[0].Seq, all[1].Seq)
	var odd []int
	var want []model.Record
	for i := range MaxAckRanges {
		odd = append(odd, 2*i+3)
		want = append(want, all[2*i+2])
	}
	want = append(want, all[2*MaxAckRanges+2:]...)
	ackSingles(t, sp, all, odd...)
	requireSame(t, "before the restart", readAll(t, sp), want)

	r.logs.reset()
	sp = r.reopen(sp)
	expect(t, r.logs.count(msgDamaged) == 0, "%q was logged %d times after the restart, want 0 (the meta file is consistent)", msgDamaged, r.logs.count(msgDamaged))
	requireSame(t, "after the restart (acknowledged records are not handed out again)", readAll(t, sp), want)
	mustPut(t, sp, batchOf(epoch.Add(time.Hour), "next", 1, 60))
	expect(t, sp.LastSeq() == all[len(all)-1].Seq+1, "the number after the restart is %d, want %d (no jump)", sp.LastSeq(), all[len(all)-1].Seq+1)
}
