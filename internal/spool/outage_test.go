package spool

import (
	"bufio"
	"bytes"
	"context"
	"fmt"
	"math/rand/v2"
	"os"
	"os/exec"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// feedOutage hands seven days of the reference stream to the spool and returns the fingerprints of the records in the
// order handed over and the encoded bytes of day 0.
func feedOutage(t *testing.T, r *rig, sp *Spool, g *refStream) ([]uint64, int64) {
	t.Helper()

	var sigs []uint64
	var dayBytes int64
	for day := range refDays {
		g.day(day, func(run []model.Record) {
			if day == 0 {
				dayBytes += encodedBytes(t, run)
			}
			for i := range run {
				sigs = append(sigs, refSig(&run[i]))
			}
			r.clk.Set(run[0].CapturedAt)
			mustPut(t, sp, run)
		})
	}

	return sigs, dayBytes
}

// outageReader checks the records read back against the fingerprints of the records handed over.
type outageReader struct {
	sigs  []uint64
	next  int
	after uint64
}

func (v *outageReader) check(t *testing.T, rec *model.Record) {
	t.Helper()

	need(t, rec.Kind() != model.KindGap, "a gap record %d is kept, want none: nothing was dropped", rec.Seq)
	need(t, v.next == 0 || rec.Seq == v.after+1, "record %d has number %d after %d, want the numbers in order without holes", v.next, rec.Seq, v.after)
	need(t, v.next < len(v.sigs) && refSig(rec) == v.sigs[v.next], "record %d (number %d) is not the record handed over at that place (or there are more records than handed over)", v.next, rec.Seq)
	v.after = rec.Seq
	v.next++
}

// readBack reads the spool in pieces from the oldest, checks every record and acknowledges every piece.
func (v *outageReader) readBack(t *testing.T, sp *Spool) {
	t.Helper()

	for {
		b, err := sp.Read(context.Background(), v.after, 2000, 4*mebi)
		needNoErr(t, fmt.Sprintf("Read(after %d)", v.after), err)
		if len(b.Records) == 0 {
			return
		}
		need(t, b.BootID == testBoot && b.AgentID == testAgent, "piece identity = %s, %s, want one boot %s and agent %s", b.AgentID, b.BootID, testBoot, testAgent)
		for i := range b.Records {
			v.check(t, &b.Records[i])
		}
		mustAck(t, sp, b.Records[0].Seq, v.after)
	}
}

// runOutage hands seven days of the reference stream, divided by divisor, to a spool of the default cap divided by
// divisor, then reads it back in pieces and acknowledges each piece.
func runOutage(t *testing.T, divisor int) {
	t.Helper()

	r := newRig(t)
	r.opts.MaxBytes = refDefaultMaxSize / int64(divisor)
	sp := r.open()
	sigs, dayBytes := feedOutage(t, r, sp, newRefStream(t, divisor))
	t.Logf("reference stream at 1/%d: %d records over 7 days, %d encoded bytes on day 0, about %d bytes per day at full scale; the spool holds %d bytes of %d",
		divisor, len(sigs), dayBytes, dayBytes*int64(divisor), diskBytes(t, r.dir()), r.opts.MaxBytes)

	size, err := sp.SpoolBytes(context.Background())
	need(t, err == nil && size <= r.opts.MaxBytes, "SpoolBytes() = %d, %v after 7 days, want at most the cap of %d", size, err, r.opts.MaxBytes)
	need(t, r.logs.count(msgDropped) == 0, "%q was logged %d times, want 0: 7 days of the reference stream fit the default cap", msgDropped, r.logs.count(msgDropped))

	v := &outageReader{sigs: sigs}
	v.readBack(t, sp)
	need(t, v.next == len(sigs), "%d records were read, want all %d", v.next, len(sigs))
	size, err = sp.SpoolBytes(context.Background())
	expect(t, err == nil && size <= r.opts.MaxBytes/16, "SpoolBytes() = %d, %v after everything is acknowledged, want at most %d", size, err, r.opts.MaxBytes/16)
	_, ok, err := sp.OldestCapture(context.Background())
	expect(t, err == nil && !ok, "OldestCapture() reports a record (%v, %v) after everything is acknowledged, want none", ok, err)
}

func TestSpool_SevenDayOutage(t *testing.T) {
	runOutage(t, refScaledDivisor)
}

func TestSpool_SevenDayOutageFullScale(t *testing.T) {
	if os.Getenv(refLongTestsEnv) != "1" {
		t.Skip("set VANDOX_LONG_TESTS=1 to run seven days of the reference stream at full scale into the default 2 GiB cap (run it without -race)")
	}
	runOutage(t, refFullScale)
}

const (
	envHelperDir       = "VANDOX_SPOOL_HELPER_DIR"
	envHelperTag       = "VANDOX_SPOOL_HELPER_TAG"
	hardKillRounds     = 22
	hardKillMaxBatches = 400
	hardKillMaxTarget  = 60
)

// hardKillBatch returns the records of batch tag: one to seven records of varying size, now and then a large one.
func hardKillBatch(tag int) []model.Record {
	size := 20 + (tag*37)%600
	if tag%29 == 0 {
		size = 12000
	}

	return batchOf(epoch.Add(time.Duration(tag)*time.Second), fmt.Sprintf("hk%d", tag), 1+tag%7, size)
}

// helperOptions are the options of the spool in the helper process and in the checks after its death.
func helperOptions(dir string) Options {
	return Options{
		Dir: dir, AgentID: testAgent, BootID: testBoot, MaxBytes: 256 * mebi, SyncInterval: 5 * time.Millisecond,
		Now:       func() time.Time { return epoch },
		Available: func(string) (uint64, error) { return hugeAvailable, nil },
	}
}

// TestSpool_HardKillHelper is the writer that TestSpool_HardKill kills: it is the test binary run again. It prints
// "ok <batch> <last number> ." for every hand-over that returned.
func TestSpool_HardKillHelper(t *testing.T) {
	dir := os.Getenv(envHelperDir)
	if dir == "" {
		t.Skip("the writer process of TestSpool_HardKill")
	}
	start, err := strconv.Atoi(os.Getenv(envHelperTag))
	if err != nil {
		t.Fatalf("%s = %q: %v", envHelperTag, os.Getenv(envHelperTag), err)
	}
	sp, err := Open(helperOptions(dir), silentLogger())
	if err != nil {
		t.Fatalf("FAIL helper: Open() = %v", err)
	}
	for tag := start; tag < start+hardKillMaxBatches; tag++ {
		if err := sp.Put(context.Background(), hardKillBatch(tag)); err != nil {
			t.Fatalf("FAIL helper: Put(batch %d) = %v", tag, err)
		}
		fmt.Printf("ok %d %d .\n", tag, sp.LastSeq())
	}
}

// acceptedBatch is a hand-over that returned in the helper.
type acceptedBatch struct {
	tag   int
	first uint64
}

// parseAccepted reads the complete "ok" lines of the helper's output; a line cut by the kill has no end marker.
func parseAccepted(t *testing.T, lines []string) []acceptedBatch {
	t.Helper()

	var out []acceptedBatch
	for _, l := range lines {
		f := strings.Fields(l)
		if len(f) == 4 && f[0] == "ok" && f[3] == "." {
			tag, err1 := strconv.Atoi(f[1])
			last, err2 := strconv.ParseUint(f[2], 10, 64)
			if err1 != nil || err2 != nil {
				t.Fatalf("helper output %q is not a number line", l)
			}
			out = append(out, acceptedBatch{tag: tag, first: last - ofLen(t, len(hardKillBatch(tag))) + 1})

			continue
		}
		if strings.Contains(l, "FAIL") || strings.Contains(l, "panic") {
			t.Fatalf("the helper process failed: %q", l)
		}
	}

	return out
}

// runHelper starts the writer on dir, kills it with SIGKILL after it reported target hand-overs and returns what it
// reported. The bool is false when the process had already ended.
func runHelper(t *testing.T, exe, dir string, start, target int) ([]acceptedBatch, bool) {
	t.Helper()

	cmd := exec.Command(exe, "-test.run=^TestSpool_HardKillHelper$", "-test.count=1")
	cmd.Env = append(os.Environ(), envHelperDir+"="+dir, envHelperTag+"="+strconv.Itoa(start))
	var stderr bytes.Buffer
	cmd.Stderr = &stderr
	out, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatal(err)
	}
	if err := cmd.Start(); err != nil {
		t.Fatalf("starting the writer: %v", err)
	}
	lines := make(chan string, 4*hardKillMaxBatches)
	go func() {
		sc := bufio.NewScanner(out)
		for sc.Scan() {
			lines <- sc.Text()
		}
		close(lines)
	}()

	var text []string
	for seen := 0; seen < target; {
		l, ok := <-lines
		if !ok {
			break
		}
		text = append(text, l)
		if strings.HasPrefix(l, "ok ") {
			seen++
		}
	}
	killErr := cmd.Process.Kill()
	for l := range lines {
		text = append(text, l)
	}
	_ = cmd.Wait()
	if stderr.Len() > 0 && strings.Contains(stderr.String(), "panic") {
		t.Fatalf("the writer panicked: %s", stderr.String())
	}

	finished := slices.Contains(text, "PASS")

	return parseAccepted(t, text), killErr == nil && !finished
}

// recoveredRecords checks the records of the recovered spool: numbers strictly increasing and every record a
// well-formed record of the writer (or a gap). It returns the records by number.
func recoveredRecords(t *testing.T, round int, recs []model.Record) map[uint64]model.Record {
	t.Helper()

	byNumber := make(map[uint64]model.Record, len(recs))
	for i, rec := range recs {
		need(t, i == 0 || rec.Seq > recs[i-1].Seq, "round %d: record %d has number %d after %d, want strictly increasing numbers (none issued twice)", round, i, rec.Seq, seqAt(recs, i-1))
		byNumber[rec.Seq] = rec
		line, isLine := rec.Data.(*model.LogLine)
		need(t, gapOf(rec) != nil || (isLine && strings.HasPrefix(line.Message, "hk")), "round %d: record %d is %T, want a record of the writer", round, rec.Seq, rec.Data)
	}

	return byNumber
}

// requireAcceptedPresent checks that every accepted hand-over is there, intact.
func requireAcceptedPresent(t *testing.T, round int, accepted []acceptedBatch, byNumber map[uint64]model.Record) {
	t.Helper()

	for _, b := range accepted {
		for i, want := range numbered(hardKillBatch(b.tag), b.first) {
			got, ok := byNumber[want.Seq]
			need(t, ok, "round %d: record %d of batch %d (number %d) was accepted and is missing", round, i, b.tag, want.Seq)
			need(t, lineOf(t, got) == lineOf(t, want), "round %d: batch %d record %d (number %d) reads back as %.120q, want %.120q", round, b.tag, i, want.Seq, lineOf(t, got), lineOf(t, want))
		}
	}
}

// verifyKilled opens a copy of dir as the next start would and checks everything the writer reported.
func verifyKilled(t *testing.T, round int, dir string, accepted []acceptedBatch, highest *uint64) {
	t.Helper()

	sp, err := Open(helperOptions(copyDir(t, dir)), silentLogger())
	need(t, err == nil, "round %d: Open after the kill = %v, want the spool recovered", round, err)
	defer func() { _ = sp.Close(context.Background()) }()

	recs := readAll(t, sp)
	requireAcceptedPresent(t, round, accepted, recoveredRecords(t, round, recs))
	newest := seqAt(recs, len(recs)-1)
	need(t, newest >= *highest, "round %d: the newest number is %d, want it not below the %d of earlier rounds", round, newest, *highest)
	*highest = newest
	mustPut(t, sp, []model.Record{logRec(epoch, "hk-probe", 50)})
	need(t, sp.LastSeq() > *highest, "round %d: the first number issued after the kill is %d, want one above %d, the newest before", round, sp.LastSeq(), *highest)
}

func TestSpool_HardKill(t *testing.T) {
	exe, err := os.Executable()
	if err != nil {
		t.Fatalf("Executable() = %v", err)
	}
	dir := t.TempDir()
	rng := rand.New(rand.NewPCG(5, 2026))

	var accepted []acceptedBatch
	var highest uint64
	killed := 0
	for round := range hardKillRounds {
		target := rng.IntN(hardKillMaxTarget)
		got, running := runHelper(t, exe, dir, round*hardKillMaxBatches, target)
		if running {
			killed++
		}
		accepted = append(accepted, got...)
		verifyKilled(t, round, dir, accepted, &highest)
	}
	if killed < 20 {
		t.Fatalf("the writer was killed while running %d times, want at least 20", killed)
	}
	t.Logf("%d rounds, %d kills while writing, %d hand-overs accepted in total", hardKillRounds, killed, len(accepted))
}
