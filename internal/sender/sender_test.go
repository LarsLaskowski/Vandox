package sender

import (
	"context"
	"errors"
	"log/slog"
	"math/rand/v2"
	"os"
	"runtime"
	"runtime/debug"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/spool"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// Every test here runs in a testing/synctest bubble with a real spool in a temporary directory and the fake
// transport (record 0062): there is no network and the waits of 30 and 60 seconds pass in fake time.

// slowOK answers every request after d, as a backend that is a little slow does.
func slowOK(d time.Duration) script {
	return func(call) reply { return replyOK().after(d) }
}

// requireLiveBatches checks what every batch of the calls carries: the format, the identity and the order.
func requireLiveBatches(t *testing.T, calls []call) {
	t.Helper()

	if len(calls) == 0 {
		t.Errorf("the backend received no request, want the batches")
	}
	for _, c := range calls {
		h := c.header
		if h.FormatMajor != 1 || h.FormatMinor != 1 || h.AgentID != testAgent || h.BootID != testBoot || h.Mode != wire.ModeLive || h.ClockOffset != nil {
			t.Errorf("batch %s has header %+v, want format 1.1, agent %s, boot %s, mode live and no clock offset", c.describe(), h, testAgent, testBoot)
		}
	}
	requireInOrder(t, "live batches", calls)
}

func TestSender_LiveRecordsSentWithinTenSeconds(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.start()
		handed := map[string]time.Time{}
		for i := range 25 {
			advance(time.Duration(300+r.rng.IntN(2700)) * time.Millisecond)
			tag := "rec" + strconv.Itoa(i)
			r.live(tag, 200)
			handed[tag] = time.Now()
		}
		advance(11 * time.Second)

		calls := r.tr.snapshot()

		requireLatency(t, handed, calls, 10*time.Second)
		requireLiveBatches(t, calls)
		requireNoDuplicates(t, r)
		if r.hasUnacked() {
			t.Errorf("the spool holds unacknowledged records after every batch was acknowledged, want none")
		}
	})
}

// putTexts hands n text records to a spool directly and returns their numbers.
func putTexts(t *testing.T, sp *spool.Spool, tag string, n int) []uint64 {
	t.Helper()

	rng := rand.New(rand.NewPCG(21, 22))
	recs := make([]model.Record, 0, n)
	for i := range n {
		recs = append(recs, textRec(time.Now().UTC(), tag+strconv.Itoa(i), 200, rng))
	}
	before := sp.LastSeq()
	if err := sp.Put(context.Background(), recs); err != nil {
		t.Fatalf("Put(%d records) error = %v, want nil", n, err)
	}

	return seqRange(before+1, sp.LastSeq())
}

func TestSender_BatchCarriesCaptureContext(t *testing.T) {
	bubble(t, func(t *testing.T) {
		dir := t.TempDir()
		first := openSpool(t, dir, testBoot, 128*mebi)
		bootA := putTexts(t, first, "a", 5)
		if err := first.Close(context.Background()); err != nil {
			t.Fatalf("Close() error = %v, want nil", err)
		}
		r := newRigOn(t, openSpool(t, dir, otherBoot, 128*mebi))
		bootB := r.backlog("b", 5, 200)
		const offset = 1500 * time.Millisecond
		r.sp.readHook = func(b *spool.Batch) {
			if b.BootID == testBoot {
				v := int64(offset)
				b.ClockOffsetNS = &v
			}
		}
		r.start()
		advance(time.Second)
		live := r.live("l", 200)
		advance(30 * time.Second)

		calls := r.tr.snapshot()

		requireContexts(t, calls, bootA, append(bootB, live), offset)
		if len(r.tr.callsOf(wire.ModeLive)) == 0 || !r.tr.callsOf(wire.ModeLive)[0].has(live) {
			t.Errorf("the live record %d was not sent in a live batch", live)
		}
		requireComplete(t, r, bootA[0])
	})
}

// contextOf returns which boot the records of a batch were captured in: "a", "b", or "both".
func contextOf(c call, bootA, bootB []uint64) string {
	inA := slices.ContainsFunc(c.seqs(), func(s uint64) bool { return slices.Contains(bootA, s) })
	inB := slices.ContainsFunc(c.seqs(), func(s uint64) bool { return slices.Contains(bootB, s) })
	switch {
	case inA && inB:
		return "both"
	case inA:
		return "a"
	default:
		return "b"
	}
}

// requireContexts checks that each batch carries the identity of the boot its records were captured in, and that none
// mixes two boots.
func requireContexts(t *testing.T, calls []call, bootA, bootB []uint64, offset time.Duration) {
	t.Helper()

	for _, c := range calls {
		ctx := contextOf(c, bootA, bootB)
		h := c.header
		switch {
		case ctx == "both":
			t.Errorf("batch %s holds records of two boots, want one capture context", c.describe())
		case ctx == "a" && (h.BootID != testBoot || h.ClockOffset == nil || *h.ClockOffset != offset):
			t.Errorf("batch %s of the first boot has boot %q offset %v, want boot %q offset %v", c.describe(), h.BootID, h.ClockOffset, testBoot, offset)
		case ctx == "b" && (h.BootID != otherBoot || h.ClockOffset != nil):
			t.Errorf("batch %s of the second boot has boot %q offset %v, want boot %q and no offset", c.describe(), h.BootID, h.ClockOffset, otherBoot)
		}
		if h.AgentID != testAgent {
			t.Errorf("batch %s has agent %q, want %q", c.describe(), h.AgentID, testAgent)
		}
	}
}

// waitGaps returns the gaps between the starts of consecutive calls.
func waitGaps(calls []call) []time.Duration {
	var out []time.Duration
	for i := 1; i < len(calls); i++ {
		out = append(out, calls[i].at.Sub(calls[i-1].at))
	}

	return out
}

// scheduleValue returns the i-th value (from 1) of the schedule 1 s doubling to 60 s.
func scheduleValue(i int) time.Duration {
	d := FirstWait
	for range i - 1 {
		d = min(d*2, MaxWait)
	}

	return d
}

// requireBackoffGaps checks that the i-th gap lies between half and the whole of the i-th schedule value.
func requireBackoffGaps(t *testing.T, what string, gaps []time.Duration) {
	t.Helper()

	for i, g := range gaps {
		d := scheduleValue(i + 1)
		if g < d/2 || g > d {
			t.Errorf("%s: wait %d lasted %v, want between %v and %v (schedule value %v)", what, i+1, g, d/2, d, d)
		}
	}
}

func TestSender_WaitsDoubleWithJitterUpToSixtySeconds(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.rand = cyclingRand()
		r.tr.setScript(failAll(connFail))
		r.start()
		r.feed(123*time.Millisecond, 400*time.Millisecond, r.tagged("f", 100))

		advance(15 * time.Minute)

		calls := r.tr.snapshot()
		if len(calls) < 12 {
			t.Fatalf("%d attempts in 15 minutes, want at least 12 to see the waits reach their cap", len(calls))
		}
		requireBackoffGaps(t, "waits between attempts", waitGaps(calls))
		if got := r.tr.maxInFlight(); got != 1 {
			t.Errorf("up to %d requests were in flight during the episode, want only one attempt at a time", got)
		}
		for _, c := range calls {
			if c.mode() != wire.ModeLive {
				t.Errorf("attempt %s is not a live batch, want only live attempts during an episode", c.describe())
			}
		}
	})
}

// warningKeys returns the attribute keys of a warning that the plan fixes.
func warningKeys(e logEntry) []string {
	var out []string
	for _, k := range e.keys {
		if k == "reason" || k == "status" {
			out = append(out, k)
		}
	}

	return out
}

// requireWarning checks the n-th warning of the episodes.
func requireWarning(t *testing.T, r *rig, n int, reason string, status int) {
	t.Helper()

	all := r.logs.notAccepting()
	if len(all) <= n {
		t.Errorf("%d warnings %q, want more than %d", len(all), msgNotAccepting, n)

		return
	}
	e := all[n]
	st, hasStatus := e.num("status")
	wantKeys := []string{"reason"}
	if status > 0 {
		wantKeys = append(wantKeys, "status")
	}
	if e.level != slog.LevelWarn || e.str("reason") != reason || hasStatus != (status > 0) || st != int64(status) || !slices.Equal(warningKeys(e), wantKeys) {
		t.Errorf("warning %d is %s %q (keys %v), want level WARN, reason=%s status=%d (keys %v)", n, e.level, e.text(), warningKeys(e), reason, status, wantKeys)
	}
}

func TestSender_OneWarningPerEpisodeAndReason(t *testing.T) {
	t.Run("one warning for a long outage and one per change", func(t *testing.T) {
		bubble(t, testWarningsPerChange)
	})
	t.Run("both streams failing log one warning", func(t *testing.T) {
		bubble(t, testWarningForBothStreams)
	})
}

// testWarningsPerChange runs an outage with changing answers: a warning when the episode starts and when the reason
// or the status changes, nothing for the attempts in between, a new warning for a second episode.
func testWarningsPerChange(t *testing.T) {
	r := newRig(t)
	r.tr.setScript(failAll(connFail))
	r.start()
	r.feed(123*time.Millisecond, 400*time.Millisecond, r.tagged("f", 100))
	advance(5 * time.Minute)
	requireWarning(t, r, 0, "connection failed", 0)

	r.tr.setScript(failAll(Result{Status: 503, Reason: "status"}))
	advance(3 * time.Minute)
	requireWarning(t, r, 1, "status", 503)

	r.tr.setScript(failAll(Result{Status: 502, Reason: "status"}))
	advance(3 * time.Minute)
	requireWarning(t, r, 2, "status", 502)

	r.tr.setScript(failAll(connFail))
	advance(3 * time.Minute)
	requireWarning(t, r, 3, "connection failed", 0)
	if got := len(r.logs.notAccepting()); got != 4 {
		t.Errorf("%d warnings after four different answers over 14 minutes of attempts, want 4 (one per start or change)", got)
	}

	r.tr.setScript(allOK)
	advance(2 * time.Minute)
	if got := r.logs.count(msgAccepting); got != 1 {
		t.Errorf("%d lines %q, want one for the episode", got, msgAccepting)
	}
	if e := r.logs.find(msgAccepting); len(e) == 1 {
		if secs, ok := e[0].num("seconds"); !ok || secs < 600 || e[0].level != slog.LevelInfo {
			t.Errorf("%q is %s with seconds=%d (present %v), want INFO and the episode's length of at least 600", msgAccepting, e[0].level, secs, ok)
		}
	}

	r.tr.setScript(failAll(connFail))
	advance(2 * time.Minute)
	requireWarning(t, r, 4, "connection failed", 0)
	if got := len(r.logs.notAccepting()); got != 5 {
		t.Errorf("%d warnings after a second episode, want 5", got)
	}
}

// testWarningForBothStreams fails the backend while the backfill runs: the live and the backfill stream both fail, one
// warning is logged for the episode of both.
func testWarningForBothStreams(t *testing.T) {
	r := newRig(t)
	r.bandwidth = 64 << 10
	r.backlog("b", 200, 15000)
	r.start()
	advance(time.Second)
	r.live("probe", 200)
	advanceUntil(t, time.Minute, time.Second, "the backfill to start", func() bool { return r.logs.count(msgBfStarted) > 0 })

	r.tr.setScript(failAll(connFail))
	r.feed(123*time.Millisecond, 2*time.Second, r.tagged("f", 100))
	advance(3 * time.Minute)

	if got := len(r.logs.notAccepting()); got != 1 {
		t.Errorf("%d warnings %q while both streams failed, want one for the episode of both", got, msgNotAccepting)
	}
	requireWarning(t, r, 0, "connection failed", 0)
}

func TestSender_ReasonPerAnswer(t *testing.T) {
	cases := []struct {
		name   string
		res    Result
		reason string
		status int
	}{
		{"401", Result{Status: 401, Reason: "token refused"}, "token refused", 401},
		{"403", Result{Status: 403, Reason: "token refused"}, "token refused", 403},
		{"400 without the marker", Result{Status: 400, Reason: "status"}, "status", 400},
		{"404", Result{Status: 404, Reason: "status"}, "status", 404},
		{"408", Result{Status: 408, Reason: "status"}, "status", 408},
		{"415", Result{Status: 415, Reason: "status"}, "status", 415},
		{"429", Result{Status: 429, Reason: "status"}, "status", 429},
		{"redirect", Result{Status: 302, Reason: "status"}, "status", 302},
		{"500", Result{Status: 500, Reason: "status"}, "status", 500},
		{"timeout", Result{Reason: "timeout"}, "timeout", 0},
		{"connection failed", connFail, "connection failed", 0},
		{"tls failed", Result{Reason: "tls failed"}, "tls failed", 0},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			bubble(t, func(t *testing.T) {
				r := newRig(t)
				r.tr.setScript(failAll(tc.res))
				r.start()
				r.live("x", 100)

				advance(6 * time.Second)

				requireWarning(t, r, 0, tc.reason, tc.status)
				if got := len(r.logs.notAccepting()); got != 1 {
					t.Errorf("%d warnings, want one", got)
				}
			})
		})
	}
}

// requireOutageOrder checks the order of an outage and its recovery in the calls: the first batch the backend
// acknowledged afterwards is a live batch of records handed over after the start of the last failed attempt, and no
// backfill batch comes before it.
func requireOutageOrder(t *testing.T, r *rig, recovered int) call {
	t.Helper()

	calls := r.tr.snapshot()
	var lastFailed call
	for _, c := range calls[:recovered] {
		if c.mode() == wire.ModeBackfill {
			t.Errorf("batch %s was sent before the backend accepted a batch again, want no backfill during the outage", c.describe())
		}
		if !c.committed {
			lastFailed = c
		}
	}
	idx := slices.IndexFunc(calls[recovered:], func(c call) bool { return c.committed })
	if idx < 0 {
		t.Fatalf("the backend acknowledged no batch after the outage")
	}
	firstOK := calls[recovered+idx]
	if firstOK.mode() != wire.ModeLive || firstOK.first() <= lastFailed.lastSeq {
		t.Errorf("first acknowledged batch %s, want a live batch with no record handed over before the start of the last failed attempt (the spool's newest number was %d then)", firstOK.describe(), lastFailed.lastSeq)
	}
	for _, c := range calls[recovered:] {
		if c.mode() == wire.ModeBackfill && c.n < firstOK.n {
			t.Errorf("backfill batch %s was sent before the first acknowledgement %s", c.describe(), firstOK.describe())
		}
	}

	return firstOK
}

// requireBackfillLines checks the info lines of one outage: each once, with the number of backfilled records.
func requireBackfillLines(t *testing.T, r *rig) {
	t.Helper()

	sent := 0
	for _, c := range r.tr.callsOf(wire.ModeBackfill) {
		if c.committed {
			sent += len(c.recs)
		}
	}
	for _, msg := range []string{msgAccepting, msgBfStarted, msgBfFinished} {
		if got := r.logs.count(msg); got != 1 {
			t.Errorf("%d lines %q, want exactly one", got, msg)
		}
	}
	if e := r.logs.find(msgBfFinished); len(e) == 1 {
		if n, ok := e[0].num("records"); !ok || n != int64(sent) {
			t.Errorf("%q has records=%d (present %v), want the %d records the backfill batches carried", msgBfFinished, n, ok, sent)
		}
	}
}

func TestSender_ThirteenHourOutageDeliversEverySequenceNumber(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		first := r.sp.LastSeq() + 1
		r.start()
		f := r.feed(123*time.Millisecond, 20250*time.Millisecond, r.varied())
		advance(2 * time.Minute)
		r.tr.setScript(failAll(connFail))
		advance(13 * time.Hour)
		recovered := len(r.tr.snapshot())
		r.tr.setScript(allOK)
		advanceUntil(t, 30*time.Minute, time.Second, "the backfill to finish", func() bool { return r.logs.count(msgBfFinished) > 0 })
		f.halt()
		advance(time.Minute)

		requireComplete(t, r, first)
		firstOK := requireOutageOrder(t, r, recovered)
		requireInOrder(t, "backfill batches of the first pass", r.tr.callsOf(wire.ModeBackfill))
		requireBackfillLines(t, r)
		if backfill := r.tr.callsOf(wire.ModeBackfill); len(backfill) < 3 {
			t.Errorf("%d backfill batches for a backlog of 13 hours, want several (first acknowledged batch %s)", len(backfill), firstOK.describe())
		}
	})
}

func TestSender_OutageDuringBackfillStillDeliversEverything(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.bandwidth = 64 << 10
		first := r.sp.LastSeq() + 1
		r.start()
		f := r.feed(123*time.Millisecond, 20250*time.Millisecond, r.varied())
		advance(2 * time.Minute)
		r.tr.setScript(failAll(connFail))
		advance(3 * time.Hour)
		r.tr.setScript(allOK)
		advanceUntil(t, 10*time.Minute, time.Second, "the first backfill batch", func() bool { return len(r.tr.callsOf(wire.ModeBackfill)) > 0 })

		r.tr.setScript(failAll(connFail))
		advance(40 * time.Minute)
		r.tr.setScript(allOK)
		advanceUntil(t, 6*time.Hour, time.Minute, "the backlog to be delivered", func() bool { return !r.hasUnacked() })
		f.halt()
		advance(time.Minute)

		requireComplete(t, r, first)
		if got := r.logs.count(msgAccepting); got < 2 {
			t.Errorf("%d lines %q, want at least two: one per outage", got, msgAccepting)
		}
	})
}

// burst hands n text records of size bytes to the spool at once and notes their hand-over time under their tags.
func burst(r *rig, handed map[string]time.Time, prefix string, n, size int) {
	recs := make([]model.Record, 0, n)
	now := time.Now().UTC()
	for i := range n {
		recs = append(recs, textRec(now, prefix+strconv.Itoa(i), size, r.rng))
	}
	r.put(recs...)
	at := time.Now()
	for _, rec := range recs {
		handed[tagOf(rec)] = at
	}
}

// recordsOfLines returns how many records of size bytes hold the given bytes of record lines.
func recordsOfLines(t *testing.T, lines, size int) int {
	t.Helper()

	sample := textRec(time.Now().UTC(), "x", size, rand.New(rand.NewPCG(1, 1)))

	return lines / lineLen(t, sample)
}

func TestSender_LiveRateDuringBackfill(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.bandwidth = 64 << 10
		r.tr.setScript(slowOK(300 * time.Millisecond))
		r.backlog("b", 400, 15000)
		r.start()
		advance(time.Second)
		r.live("probe", 200)
		advanceUntil(t, time.Minute, time.Second, "the backfill to start", func() bool { return len(r.tr.callsOf(wire.ModeBackfill)) > 0 })
		windowStart := time.Now()

		handed := map[string]time.Time{}
		for i := range 5 {
			burst(r, handed, "burst"+strconv.Itoa(i)+"-", 1000, 2000)
			advance(2 * time.Second)
		}
		advance(30 * time.Second)

		requireLatency(t, handed, r.tr.callsOf(wire.ModeLive), 10*time.Second)
		during := 0
		for _, c := range r.tr.callsOf(wire.ModeBackfill) {
			if !c.at.Before(windowStart) {
				during++
			}
		}
		if during == 0 || r.logs.count(msgBfFinished) != 0 {
			t.Errorf("%d backfill batches during the window and %d backfill finished lines, want the backfill running throughout", during, r.logs.count(msgBfFinished))
		}
	})
}

func TestSender_LiveBurstRightAfterTick(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.tr.setScript(slowOK(500 * time.Millisecond))
		r.start()
		r.live("probe", 200)
		advanceUntil(t, time.Minute, 100*time.Millisecond, "the tick that sends the probe", func() bool {
			c := r.tr.snapshot()

			return len(c) > 0 && c[0].res.Status == 200
		})

		handed := map[string]time.Time{}
		burst(r, handed, "burst-", recordsOfLines(t, 10*mebi, 2000), 2000)
		advance(time.Minute)

		requireLatency(t, handed, r.tr.callsOf(wire.ModeLive), 10*time.Second)
		requireComplete(t, r, r.sp.LastSeq()-u64(t, len(handed)))
	})
}

func TestSender_AboveLiveRateStaysInOrder(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.tr.setScript(slowOK(2 * time.Second))
		first := r.sp.LastSeq() + 1
		r.start()
		advance(time.Second)
		handed := map[string]time.Time{}
		burst(r, handed, "burst-", recordsOfLines(t, 10*mebi, 2000), 2000)
		for i := range 10 {
			advance(2 * time.Second)
			r.live("trickle"+strconv.Itoa(i), 200)
		}
		advance(2 * time.Minute)

		calls := r.tr.snapshot()

		for _, c := range calls {
			if c.mode() != wire.ModeLive {
				t.Errorf("batch %s is not live, want every record to stay in the live stream", c.describe())
			}
		}
		requireInOrder(t, "live batches", calls)
		for i := 1; i < len(calls); i++ {
			if idle := calls[i].at.Sub(calls[i-1].end); idle > time.Second {
				t.Errorf("batch %d waited %v after the previous answer, want full batches back to back", i, idle)
			}
		}
		requireComplete(t, r, first)
		requireNoDuplicates(t, r)
	})
}

func TestSender_BackfillThrottledLiveNot(t *testing.T) {
	bubble(t, func(t *testing.T) {
		const bandwidth = 64 << 10
		r := newRig(t)
		r.bandwidth = bandwidth
		r.tr.setScript(slowOK(100 * time.Millisecond))
		r.backlog("b", 300, 15000)
		r.start()
		f := r.feed(123*time.Millisecond, time.Second, r.tagged("f", 300))
		advance(time.Second)
		r.live("probe", 200)

		advance(120 * time.Second)

		backfill := r.tr.callsOf(wire.ModeBackfill)
		if len(backfill) < 3 {
			t.Fatalf("%d backfill batches in two minutes, want at least 3", len(backfill))
		}
		total, largest := 0, 0
		for i, c := range backfill {
			total, largest = total+c.size, max(largest, c.size)
			if i+1 < len(backfill) {
				if gap, want := backfill[i+1].at.Sub(c.at), sendTime(c.size, bandwidth); gap < want-time.Millisecond {
					t.Errorf("backfill batch %d was followed after %v, want at least %v for its %d bytes at %d bytes per second", i, gap, want, c.size, bandwidth)
				}
			}
		}
		span := backfill[len(backfill)-1].at.Sub(backfill[0].at)
		if limit := int64(span/time.Second)*bandwidth + int64(largest); int64(total-backfill[len(backfill)-1].size) > limit {
			t.Errorf("backfill bodies sent over %v hold %d bytes, want at most %d (the rate times the span plus one batch)", span, total, limit)
		}
		f.halt()
		advance(10 * time.Second)
		requireLatency(t, f.handedAt(), r.tr.callsOf(wire.ModeLive), 10*time.Second)
	})
}

// ackSingles acknowledges the records at the given indexes of seqs, one range each, in the spool directly.
func ackSingles(t *testing.T, r *rig, seqs []uint64, indexes ...int) {
	t.Helper()

	for _, i := range indexes {
		if err := r.sp.Spool.Ack(context.Background(), seqs[i], seqs[i]); err != nil {
			t.Fatalf("Ack(%d) error = %v, want nil", seqs[i], err)
		}
	}
}

// oddIndexes returns the odd numbers below n, n/2 of them.
func oddIndexes(n int) []int {
	var out []int
	for i := 1; i < n; i += 2 {
		out = append(out, i)
	}

	return out
}

func TestSender_StartWithBacklogSendsLiveFirst(t *testing.T) {
	t.Run("backlog behind the first live batch", func(t *testing.T) { bubble(t, testStartWithBacklog) })
	t.Run("32 open ranges: no jump", func(t *testing.T) { bubble(t, testStartWithFullRanges) })
	t.Run("31 open ranges: jump", func(t *testing.T) { bubble(t, testStartWithFewerRanges) })
}

// testStartWithBacklog starts on a spool with records, some acknowledged: the first batch is the new live record,
// then the backlog goes in ascending order without the acknowledged records.
func testStartWithBacklog(t *testing.T) {
	r := newRig(t)
	seqs := r.backlog("b", 30, 300)
	acked := seqs[10:15]
	if err := r.sp.Spool.Ack(context.Background(), acked[0], acked[len(acked)-1]); err != nil {
		t.Fatalf("Ack() error = %v, want nil", err)
	}
	r.start()
	advance(time.Second)
	live := r.live("l", 200)
	advance(30 * time.Second)

	calls := r.tr.snapshot()

	if len(calls) < 2 || calls[0].mode() != wire.ModeLive || !slices.Equal(calls[0].seqs(), []uint64{live}) {
		t.Fatalf("batches %v, want the live record %d first and alone, then the backlog", describeCalls(calls), live)
	}
	for _, c := range calls[1:] {
		if c.mode() != wire.ModeBackfill || c.at.Before(calls[0].end) {
			t.Errorf("batch %s, want a backfill batch only after the first live batch was acknowledged", c.describe())
		}
	}
	requireInOrder(t, "backfill batches", calls[1:])
	requireSeqsAbsent(t, "acknowledged before the start", calls, acked)
	skip := map[uint64]bool{}
	for _, s := range acked {
		skip[s] = true
	}
	requireCompleteExcept(t, r, seqs[0], skip)
	requireNoDuplicates(t, r)
}

// testStartWithFullRanges starts on a spool with 32 open acknowledged ranges: the live stream starts at the oldest
// unacknowledged record and opens no hole.
func testStartWithFullRanges(t *testing.T) {
	r := newRig(t)
	seqs := r.backlog("b", 70, 300)
	odd := oddIndexes(64)
	ackSingles(t, r, seqs, odd...)
	if got := r.sp.AckRanges(); got != MaxHoles {
		t.Fatalf("AckRanges() = %d before the start, want %d", got, MaxHoles)
	}
	r.start()
	advance(30 * time.Second)

	calls := r.tr.snapshot()

	if len(calls) == 0 || calls[0].mode() != wire.ModeLive || calls[0].first() != seqs[0] {
		t.Fatalf("batches %v, want a live batch that starts at the oldest unacknowledged record %d", describeCalls(calls), seqs[0])
	}
	if got := len(r.tr.callsOf(wire.ModeBackfill)); got != 0 {
		t.Errorf("%d backfill batches, want none: the live stream sent every unacknowledged record", got)
	}
	var acked []uint64
	for _, i := range odd {
		acked = append(acked, seqs[i])
	}
	requireSeqsAbsent(t, "acknowledged before the start", calls, acked)
	skip := map[uint64]bool{}
	for _, s := range acked {
		skip[s] = true
	}
	requireCompleteExcept(t, r, seqs[0], skip)
}

// testStartWithFewerRanges starts on a spool with 31 open ranges: the live stream waits for new data.
func testStartWithFewerRanges(t *testing.T) {
	r := newRig(t)
	seqs := r.backlog("b", 70, 300)
	ackSingles(t, r, seqs, oddIndexes(62)...)
	if got := r.sp.AckRanges(); got != MaxHoles-1 {
		t.Fatalf("AckRanges() = %d before the start, want %d", got, MaxHoles-1)
	}
	r.start()
	advance(30 * time.Second)
	if got := len(r.tr.snapshot()); got != 0 {
		t.Errorf("%d batches before any new record, want none: the backlog is not live data", got)
	}
	live := r.live("l", 200)
	advance(10 * time.Second)

	calls := r.tr.snapshot()

	if len(calls) == 0 || calls[0].mode() != wire.ModeLive || !slices.Equal(calls[0].seqs(), []uint64{live}) {
		t.Errorf("batches %v, want the live record %d first and alone", describeCalls(calls), live)
	}
}

// describeCalls returns the calls as one text for messages.
func describeCalls(calls []call) string {
	var parts []string
	for _, c := range calls {
		parts = append(parts, c.describe())
	}

	return "[" + strings.Join(parts, "; ") + "]"
}

func TestSender_LostAnswerSentAgain(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		var n atomic.Int32
		r.tr.setScript(func(call) reply {
			if n.Add(1) == 1 {
				return replyLost()
			}

			return replyOK()
		})
		r.start()
		a := r.live("a", 200)
		advance(5 * time.Second)
		b := r.live("b", 200)
		advance(2 * time.Minute)

		stored := r.tr.committedSet()
		if stored[a] != 2 || stored[b] == 0 {
			t.Errorf("the backend committed record a %d times and record b %d times, want a sent again after its lost answer (2) and b delivered", stored[a], stored[b])
		}
		requireComplete(t, r, a)
	})
}

// giveUps returns the "record refused" warnings.
func giveUps(r *rig) []logEntry { return r.logs.find(msgRefused) }

// gapsOfCalls returns the gap records the backend committed, by the first sequence number they name.
func gapsOfCalls(calls []call) map[uint64]model.Record {
	out := map[uint64]model.Record{}
	for _, c := range calls {
		if !c.committed {
			continue
		}
		for _, rec := range c.recs {
			if g := gapOf(rec); g != nil {
				out[g.FirstSeq] = rec
			}
		}
	}

	return out
}

// requireGiveUp checks the gap that stands for the refused record seq and the warning that names it.
func requireGiveUp(t *testing.T, r *rig, poison model.Record, seq uint64, status int) {
	t.Helper()

	rec, ok := gapsOfCalls(r.tr.snapshot())[seq]
	g := gapOf(rec)
	if !ok || g == nil {
		t.Errorf("the backend received no gap for the refused record %d", seq)

		return
	}
	if g.Cause != model.GapUnknown || g.FirstSeq != seq || g.LastSeq != seq || rec.Source != "agent" || !g.From.Equal(poison.CapturedAt) || !g.To.Equal(poison.CapturedAt.Add(time.Second)) {
		t.Errorf("gap for record %d = %+v source %q, want cause unknown, first_seq = last_seq = %d, from the capture time %v to one second later, source agent", seq, g, rec.Source, seq, poison.CapturedAt)
	}
	for _, e := range giveUps(r) {
		if n, _ := e.seq("seq"); n == seq {
			st, _ := e.num("status")
			if e.level != slog.LevelWarn || st != int64(status) || e.str("kind") != "log_line" || !slices.Equal(e.keys, []string{"status", "seq", "kind"}) {
				t.Errorf("warning for record %d is %s %q, want level WARN with status=%d seq=%d kind=log_line in this order", seq, e.level, e.text(), status, seq)
			}

			return
		}
	}
	t.Errorf("no warning %q names record %d", msgRefused, seq)
}

func TestSender_RefusedRecordIsolatedAndGivenUp(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.tr.setScript(refusePoison)
		r.start()
		now := time.Now().UTC()
		recs := make([]model.Record, 0, 8)
		for i := range 8 {
			tag := "n" + strconv.Itoa(i)
			if i == 5 {
				tag = poisonTag
			}
			recs = append(recs, textRec(now, tag, 200, r.rng))
		}
		seqs := r.put(recs...)
		advance(30 * time.Second)

		requireOnlyRefusedGivenUp(t, r, seqs, 5)
		requireGiveUp(t, r, recs[5], seqs[5], 422)

		later := r.live("later", 200)
		advance(10 * time.Second)
		if r.tr.committedSet()[later] != 1 {
			t.Errorf("a record handed over after the give-up was not delivered, want the live stream to go on")
		}
	})
}

// requireOnlyRefusedGivenUp checks that every record but the refused one at index was committed once, the refused one
// is acknowledged in the spool without being committed, and nothing else was logged or left behind.
func requireOnlyRefusedGivenUp(t *testing.T, r *rig, seqs []uint64, index int) {
	t.Helper()

	stored := r.tr.committedSet()
	for i, s := range seqs {
		if i != index && stored[s] != 1 {
			t.Errorf("record %d (seq %d) was committed %d times, want once: only the refused record is given up", i, s, stored[s])
		}
	}
	if stored[seqs[index]] != 0 {
		t.Errorf("the refused record was committed %d times, want never", stored[seqs[index]])
	}
	if got := len(giveUps(r)); got != 1 {
		t.Errorf("%d warnings %q, want one", got, msgRefused)
	}
	if got := r.logs.count(msgNotAccepting); got != 0 {
		t.Errorf("%d warnings %q for a refusal, want none: a refusal is no episode", got, msgNotAccepting)
	}
	if left := r.sp.unacked(t); len(left) != 0 {
		t.Errorf("%d records unacknowledged after the give-up, want none (first seq %d)", len(left), left[0].Seq)
	}
}

// requireHourlyBound fails the test when any hour holds more than MaxGivenUpPerHour of the warnings.
func requireHourlyBound(t *testing.T, entries []logEntry) {
	t.Helper()

	for i := 0; i+MaxGivenUpPerHour < len(entries); i++ {
		if span := entries[i+MaxGivenUpPerHour].at.Sub(entries[i].at); span < time.Hour {
			t.Errorf("%d records were given up within %v (from %v), want at most %d per hour", MaxGivenUpPerHour+1, span, entries[i].at.Format("15:04:05"), MaxGivenUpPerHour)

			return
		}
	}
}

// mixedBacklog returns records with a poison record after each of the first count normal ones, then tail normal ones.
func mixedBacklog(r *rig, count, tail int) []model.Record {
	now := time.Now().UTC()
	var recs []model.Record
	for i := range count {
		recs = append(recs, textRec(now, "n"+strconv.Itoa(i), 300, r.rng), textRec(now, poisonTag+strconv.Itoa(i), 300, r.rng))
	}
	for i := range tail {
		recs = append(recs, textRec(now, "t"+strconv.Itoa(i), 300, r.rng))
	}

	return recs
}

func TestSender_EleventhRefusalParksOnlyItsStream(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.tr.setScript(refusePoison)
		recs := mixedBacklog(r, 11, 3)
		seqs := r.put(recs...)
		eleventh := seqs[2*10+1]
		r.start()
		advance(time.Second)
		r.live("l0", 200)
		advance(10 * time.Second)

		if got := len(giveUps(r)); got != MaxGivenUpPerHour {
			t.Fatalf("%d records given up in the first seconds, want %d (the bound of an hour)", got, MaxGivenUpPerHour)
		}
		parkedAt := len(r.tr.callsOf(wire.ModeBackfill))
		f := r.feed(123*time.Millisecond, time.Minute, r.tagged("lv", 200))
		advance(50 * time.Minute)

		requireParked(t, r, eleventh, parkedAt)
		requireLatency(t, f.handedAt(), r.tr.callsOf(wire.ModeLive), 10*time.Second)

		f.halt()
		advance(30 * time.Minute)

		if got := len(giveUps(r)); got != MaxGivenUpPerHour+1 {
			t.Errorf("%d records given up after the hour, want the eleventh as well (%d)", got, MaxGivenUpPerHour+1)
		}
		requireHourlyBound(t, giveUps(r))
		if got := len(r.logs.find(msgNotAccepting)); got != 1 {
			t.Errorf("%d warnings %q, want one for the wait", got, msgNotAccepting)
		}
		skip := map[uint64]bool{}
		for i := range 11 {
			skip[seqs[2*i+1]] = true
		}
		requireCompleteExcept(t, r, seqs[0], skip)
	})
}

// requireParked checks the state of a backfill stream that waits for a give-up slot: the refused record and its
// successors are unacknowledged, no backfill batch is sent, and one warning with reason refused was logged.
func requireParked(t *testing.T, r *rig, eleventh uint64, backfillCalls int) {
	t.Helper()

	if got := len(r.tr.callsOf(wire.ModeBackfill)); got != backfillCalls {
		t.Errorf("%d backfill batches while the stream waits, want no more than the %d sent before", got, backfillCalls)
	}
	if left := r.sp.unacked(t); len(left) == 0 || left[0].Seq != eleventh {
		t.Errorf("lowest unacknowledged record is %v, want the refused record %d to stay in the spool", describeSeqs(left), eleventh)
	}
	warnings := r.logs.notAccepting()
	if len(warnings) != 1 {
		t.Fatalf("%d warnings %q, want one with reason refused for the wait", len(warnings), msgNotAccepting)
	}
	if st, _ := warnings[0].num("status"); warnings[0].str("reason") != "refused" || st != 422 || warnings[0].level != slog.LevelWarn {
		t.Errorf("warning %q, want level WARN with reason=refused status=422", warnings[0].text())
	}
}

func TestSender_PermanentRefusalBoundsRequestsAndLogs(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.tr.setScript(refusePoison)
		now := time.Now().UTC()
		recs := make([]model.Record, 0, 30)
		for i := range 30 {
			recs = append(recs, textRec(now, poisonTag+strconv.Itoa(i), 300, r.rng))
		}
		seqs := r.put(recs...)
		r.start()
		r.feed(123*time.Millisecond, 10*time.Minute, r.tagged("lv", 200))
		advance(30 * time.Minute)
		if got := len(giveUps(r)); got != MaxGivenUpPerHour {
			t.Errorf("%d records given up in the first half hour, want %d", got, MaxGivenUpPerHour)
		}

		advance(95 * time.Minute)

		if got := len(giveUps(r)); got != 30 {
			t.Errorf("%d records given up in two hours, want all 30 (ten per hour)", got)
		}
		requireHourlyBound(t, giveUps(r))
		got := r.logs.notAccepting()
		if len(got) != 2 {
			t.Fatalf("%d warnings %q, want exactly two with reason refused, one for each of the two waits", len(got), msgNotAccepting)
		}
		for _, e := range got {
			if e.str("reason") != "refused" {
				t.Errorf("warning %q, want reason=refused", e.text())
			}
		}
		if got := len(r.tr.snapshot()); got > 500 {
			t.Errorf("%d requests in two hours for %d refused records, want at most 500", got, len(seqs))
		}
	})
}

func TestSender_RefusalWithoutHeaderGivesNothingUp(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.tr.setScript(failAll(Result{Status: 400, Reason: "status"}))
		seqs := r.backlog("b", 3, 200)
		last := r.sp.LastSeq()
		r.start()
		f := r.feed(123*time.Millisecond, 20*time.Second, r.tagged("lv", 200))

		advance(2 * time.Hour)

		if got := len(giveUps(r)); got != 0 {
			t.Errorf("%d records given up after a 400 without the marker, want none", got)
		}
		if got, want := r.sp.LastSeq(), last+u64(t, f.count()); got != want {
			t.Errorf("the spool's newest number is %d, want %d: no gap may be handed over", got, want)
		}
		if got, want := len(r.sp.unacked(t)), 3+f.count(); got != want {
			t.Errorf("%d records unacknowledged, want all %d (first seq %d)", got, want, seqs[0])
		}
		requireWarning(t, r, 0, "status", 400)
	})
}

func TestSender_HangingRequestEndsAfterThirtySeconds(t *testing.T) {
	t.Run("live request", func(t *testing.T) { bubble(t, testLiveHang) })
	t.Run("backfill request", func(t *testing.T) { bubble(t, testBackfillHang) })
}

// requireTimedOut checks that the call hung for exactly the request timeout and came back as a timeout.
func requireTimedOut(t *testing.T, c call) {
	t.Helper()

	if d := c.end.Sub(c.at); d != RequestTimeout || c.res != (Result{Reason: "timeout"}) {
		t.Errorf("request %s ended after %v with %+v, want %v and a timeout", c.describe(), d, c.res, RequestTimeout)
	}
}

// testLiveHang lets the first live request hang: it ends after 30 seconds, and a record handed over meanwhile is sent.
func testLiveHang(t *testing.T) {
	r := newRig(t)
	var n atomic.Int32
	r.tr.setScript(func(call) reply {
		if n.Add(1) == 1 {
			return replyHang()
		}

		return replyOK()
	})
	r.start()
	a := r.live("a", 200)
	advance(5 * time.Second)
	r.live("b", 200)
	advance(40 * time.Second)

	calls := r.tr.snapshot()
	if len(calls) == 0 {
		t.Fatalf("the backend received no request")
	}
	requireTimedOut(t, calls[0])
	requireWarning(t, r, 0, "timeout", 0)

	advance(5 * time.Minute)
	requireComplete(t, r, a)
}

// testBackfillHang lets the backfill request hang: the live stream is not held up while it does.
func testBackfillHang(t *testing.T) {
	r := newRig(t)
	r.tr.setScript(func(c call) reply {
		if c.mode() == wire.ModeBackfill {
			return replyHang()
		}

		return replyOK()
	})
	r.backlog("b", 5, 200)
	r.start()
	advance(time.Second)
	r.live("probe", 200)
	advanceUntil(t, time.Minute, 100*time.Millisecond, "the backfill request", func() bool { return len(r.tr.callsOf(wire.ModeBackfill)) > 0 })

	handed := map[string]time.Time{}
	for i := range 3 {
		advance(8 * time.Second)
		tag := "during" + strconv.Itoa(i)
		r.live(tag, 200)
		handed[tag] = time.Now()
	}
	advance(2 * time.Minute)

	backfill := r.tr.callsOf(wire.ModeBackfill)
	if len(backfill) == 0 {
		t.Fatalf("the backend received no backfill request")
	}
	requireTimedOut(t, backfill[0])
	requireLatency(t, handed, r.tr.callsOf(wire.ModeLive), 10*time.Second)
	requireWarning(t, r, 0, "timeout", 0)
}

// leakSentinel is in the body of a record; no log line may show it.
const leakSentinel = "SECRET-BODY-CANARY"

// fixedKeys are the attribute keys of the sender's log lines.
var fixedKeys = []string{"reason", "status", "seconds", "records", "seq", "kind", "err", "component"}

// fixedMessages are the messages of the sender's log lines.
var fixedMessages = []string{msgNotAccepting, msgAccepting, msgBfStarted, msgBfFinished, msgBfPaused, msgBfResumed, msgRefused, msgAckNotKept, msgNotReadable}

var errReadFailed = errors.New("spool: read: input/output error")

func TestSender_LogsHoldNoURLTokenOrBody(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.tr.setScript(refusePoison)
		r.start()
		now := time.Now().UTC()
		poison := textRec(now, poisonTag, 200, r.rng)
		poison.Data = &model.LogLine{Log: "app.log", Message: poisonTag + " " + leakSentinel}
		r.put(textRec(now, "n0", 200, r.rng), poison)
		advance(10 * time.Second)

		f := r.feed(123*time.Millisecond, 400*time.Millisecond, r.tagged("f", 100))
		r.tr.setScript(failAll(Result{Status: 503, Reason: "status"}))
		advance(2 * time.Minute)
		r.tr.setScript(refusePoison)
		advance(time.Minute)

		r.sp.setAckErr(func(uint64, uint64) error { return spool.ErrTooManyRanges })
		advance(30 * time.Second)
		r.sp.setAckErr(nil)
		r.sp.setReadErr(func(uint64) error { return errReadFailed })
		advance(30 * time.Second)
		r.sp.setReadErr(nil)
		advance(5 * time.Minute)
		f.halt()

		requireFixedLogs(t, r.logs.all())
		for _, msg := range []string{msgNotAccepting, msgAccepting, msgRefused} {
			if r.logs.count(msg) == 0 {
				t.Errorf("no line %q was logged, want the scenario to produce it", msg)
			}
		}
	})
}

// requireFixedLogs checks that every line has a fixed message, fixed keys and none of the forbidden texts.
func requireFixedLogs(t *testing.T, entries []logEntry) {
	t.Helper()

	forbidden := []string{leakSentinel, testToken, "Bearer", "http://", "https://", "backend.example", "100.64.0.1"}
	for _, e := range entries {
		requireFixedShape(t, e)
		for _, f := range forbidden {
			if strings.Contains(e.text(), f) {
				t.Errorf("log line %q contains %q, want no URL, token or record body", e.text(), f)
			}
		}
	}
}

// requireFixedShape checks the message, the keys and the err text of one line.
func requireFixedShape(t *testing.T, e logEntry) {
	t.Helper()

	allowedErr := []string{spool.ErrTooManyRanges.Error(), errReadFailed.Error()}
	if !slices.Contains(fixedMessages, e.msg) {
		t.Errorf("log line %q has no fixed message", e.text())
	}
	for _, k := range e.keys {
		if !slices.Contains(fixedKeys, k) {
			t.Errorf("log line %q has the key %q, want only the fixed keys %v", e.text(), k, fixedKeys)
		}
	}
	if e.str("err") != "" && !slices.Contains(allowedErr, e.str("err")) {
		t.Errorf("log line %q has an err that is not one of the spool's error texts %v", e.text(), allowedErr)
	}
}

func TestSender_AcknowledgementRefusedLoggedOncePerEpisode(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.sp.setAckErr(func(uint64, uint64) error { return spool.ErrTooManyRanges })
		r.start()
		a := r.live("a", 200)
		advance(5 * time.Minute)

		calls := r.tr.snapshot()
		if len(calls) < 5 || len(calls) > 16 {
			t.Errorf("%d requests in five minutes for an acknowledgement the spool refuses, want 5 to 16 (waits of the backoff schedule, never at once)", len(calls))
		}
		for _, c := range calls {
			if !slices.Equal(c.seqs(), []uint64{a}) {
				t.Errorf("request %s, want the same record %d every time: the position does not move", c.describe(), a)
			}
		}
		requireBackoffGaps(t, "resends", waitGaps(calls))
		requireAckWarnings(t, r, 1)

		r.sp.setAckErr(nil)
		advance(70 * time.Second)
		if r.hasUnacked() {
			t.Errorf("the record is still unacknowledged after the spool accepts the acknowledgement again")
		}
		requireAckWarnings(t, r, 1)

		r.live("b", 200)
		r.sp.setAckErr(func(uint64, uint64) error { return spool.ErrTooManyRanges })
		advance(2 * time.Minute)
		requireAckWarnings(t, r, 2)
		if got := r.logs.count(msgNotAccepting); got != 0 {
			t.Errorf("%d warnings %q for a refused acknowledgement, want none: the backend accepted the batch", got, msgNotAccepting)
		}
	})
}

// requireAckWarnings checks the number of warnings about a refused acknowledgement and that they carry the spool's text.
func requireAckWarnings(t *testing.T, r *rig, want int) {
	t.Helper()

	got := r.logs.find(msgAckNotKept)
	if len(got) != want {
		t.Errorf("%d warnings %q, want %d (one per episode)", len(got), msgAckNotKept, want)
	}
	for _, e := range got {
		if e.level != slog.LevelWarn || e.str("err") != spool.ErrTooManyRanges.Error() {
			t.Errorf("warning %q, want level WARN with err=%q", e.text(), spool.ErrTooManyRanges.Error())
		}
	}
}

func TestSender_LeftoverBelowLiveStartSentAgain(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.tr.setScript(refusePoison)
		now := time.Now().UTC()
		var puts atomic.Int32
		r.sp.setPutErr(func([]model.Record) error {
			if puts.Add(1) == 1 {
				return errors.New("spool: write: no space left on device")
			}

			return nil
		})
		poison := textRec(now, poisonTag, 200, r.rng)
		seqs := r.put(textRec(now, "n0", 200, r.rng), poison, textRec(now, "n1", 200, r.rng), textRec(now, "n2", 200, r.rng))
		r.start()
		advance(time.Second)
		r.live("l", 200)

		advanceUntil(t, 30*time.Minute, 5*time.Second, "the backlog to be delivered", func() bool { return !r.hasUnacked() })
		advance(time.Minute)

		sent := 0
		for _, c := range r.tr.snapshot() {
			if c.has(seqs[1]) {
				sent++
			}
		}
		if sent < 2 {
			t.Errorf("the refused record was sent %d times, want again after the failed hand-over of its gap", sent)
		}
		requireGiveUp(t, r, poison, seqs[1], 422)
		skip := map[uint64]bool{seqs[1]: true}
		requireCompleteExcept(t, r, seqs[0], skip)
		if got := len(r.tr.snapshot()); got > 100 {
			t.Errorf("%d requests for one failed hand-over, want a bounded number", got)
		}
	})
}

func TestSender_SpoolReadFailureRetried(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		backlog := r.backlog("b", 5, 200)
		backlogLast := backlog[len(backlog)-1]
		var failed atomic.Int32
		r.sp.setReadErr(func(after uint64) error {
			if after < backlogLast {
				failed.Add(1)

				return errReadFailed
			}

			return nil
		})
		r.start()
		advance(time.Second)
		r.live("probe", 200)
		f := r.feed(123*time.Millisecond, 10*time.Second, r.tagged("lv", 200))

		advance(5 * time.Minute)

		requireReadWarnings(t, r, 1, 1)
		if n := failed.Load(); n < 3 || n > 17 {
			t.Errorf("%d failed backfill reads in five minutes, want 3 to 17: retried after the waits of the backoff schedule", n)
		}
		requireLatency(t, f.handedAt(), r.tr.callsOf(wire.ModeLive), 10*time.Second)
		if got := len(r.tr.callsOf(wire.ModeBackfill)); got != 0 {
			t.Errorf("%d backfill batches while every backfill read failed, want none", got)
		}

		r.sp.setReadErr(nil)
		advanceUntil(t, 5*time.Minute, 5*time.Second, "the backlog to be delivered", func() bool { return !r.hasUnacked() || len(r.tr.callsOf(wire.ModeBackfill)) > 0 })
		advance(time.Minute)
		f.halt()
		requireComplete(t, r, backlog[0])

		r.sp.setReadErr(func(uint64) error { return errReadFailed })
		r.live("q", 200)
		advance(2 * time.Minute)
		requireReadWarnings(t, r, 2, 3)
		r.sp.setReadErr(nil)
		advance(2 * time.Minute)
		requireComplete(t, r, backlog[0])
	})
}

// requireReadWarnings checks that the number of warnings about an unreadable spool is between min and max and that
// each carries the spool's error text.
func requireReadWarnings(t *testing.T, r *rig, lo, hi int) {
	t.Helper()

	got := r.logs.find(msgNotReadable)
	if len(got) < lo || len(got) > hi {
		t.Errorf("%d warnings %q, want %d to %d (one per failure episode of a stream)", len(got), msgNotReadable, lo, hi)
	}
	for _, e := range got {
		if e.level != slog.LevelWarn || e.str("err") != errReadFailed.Error() {
			t.Errorf("warning %q, want level WARN with err=%q", e.text(), errReadFailed.Error())
		}
	}
}

func TestSender_BackfillPausedUnderMemoryPressure(t *testing.T) {
	t.Run("pressure before the pass", func(t *testing.T) { bubble(t, testPressureBeforePass) })
	t.Run("pressure during the pass", func(t *testing.T) { bubble(t, testPressureDuringPass) })
	t.Run("outage hole under pressure", func(t *testing.T) { bubble(t, testOutageHoleUnderPressure) })
}

// testOutageHoleUnderPressure ends an outage under memory pressure: the probe the outage left behind is a hole below the
// new live start, so it is backfilled once the pressure is off and not before.
func testOutageHoleUnderPressure(t *testing.T) {
	r := newRig(t)
	r.tr.setScript(failAll(connFail))
	r.start()
	r.snd.SetPressure(true)
	advance(time.Second)
	probe := r.live("probe", 200)
	advance(30 * time.Second)

	r.tr.setScript(allOK)
	r.live("up", 200)
	advanceUntil(t, 5*time.Minute, time.Second, "the backend to be accepted again", func() bool { return r.logs.count(msgAccepting) > 0 })
	advance(30 * time.Second)

	if got := len(r.tr.callsOf(wire.ModeBackfill)); got != 0 {
		t.Errorf("%d backfill batches under memory pressure, want none", got)
	}
	requireBackfillState(t, r, 1, 0)

	r.snd.SetPressure(false)
	advanceUntil(t, 5*time.Minute, time.Second, "the backfill to finish", func() bool { return r.logs.count(msgBfFinished) > 0 })
	advance(30 * time.Second)

	requireBackfillState(t, r, 1, 1)
	if r.tr.committedSet()[probe] != 1 {
		t.Errorf("the probe %d was committed %d times, want once (backfilled after the pressure)", probe, r.tr.committedSet()[probe])
	}
	requireComplete(t, r, probe)
}

// requireBackfillState checks how often the pause and resume lines were logged and their content.
func requireBackfillState(t *testing.T, r *rig, paused, resumed int) {
	t.Helper()

	if got := r.logs.count(msgBfPaused); got != paused {
		t.Errorf("%d lines %q, want %d", got, msgBfPaused, paused)
	}
	if got := r.logs.count(msgBfResumed); got != resumed {
		t.Errorf("%d lines %q, want %d", got, msgBfResumed, resumed)
	}
	for _, e := range r.logs.find(msgBfPaused) {
		if e.level != slog.LevelInfo || e.str("reason") != "memory_pressure" {
			t.Errorf("line %q, want level INFO with reason=memory_pressure", e.text())
		}
	}
	for _, e := range r.logs.find(msgBfResumed) {
		if e.level != slog.LevelInfo {
			t.Errorf("line %q, want level INFO", e.text())
		}
	}
}

// testPressureBeforePass turns the pressure on before the backfill is released: nothing is backfilled and live data
// goes on, until the pressure turns off.
func testPressureBeforePass(t *testing.T) {
	r := newRig(t)
	backlog := r.backlog("b", 150, 15000)
	r.start()
	r.snd.SetPressure(true)
	advance(time.Second)
	r.live("probe", 200)
	f := r.feed(123*time.Millisecond, 30*time.Second, r.tagged("lv", 200))

	advance(10 * time.Minute)

	if got := len(r.tr.callsOf(wire.ModeBackfill)); got != 0 {
		t.Errorf("%d backfill batches under memory pressure, want none", got)
	}
	requireLatency(t, f.handedAt(), r.tr.callsOf(wire.ModeLive), 10*time.Second)
	paused := r.logs.count(msgBfPaused)
	if paused > 1 {
		t.Errorf("%d lines %q, want at most one for the one change", paused, msgBfPaused)
	}

	r.snd.SetPressure(false)
	advanceUntil(t, 5*time.Minute, time.Second, "the backfill to finish", func() bool { return r.logs.count(msgBfFinished) > 0 })
	f.halt()
	advance(time.Minute)
	requireBackfillState(t, r, paused, paused)
	requireComplete(t, r, backlog[0])
	requireInOrder(t, "backfill batches", r.tr.callsOf(wire.ModeBackfill))
}

// testPressureDuringPass turns the pressure on while a backfill batch is in flight: that batch completes, no further
// batch is sent until the pressure is off.
func testPressureDuringPass(t *testing.T) {
	r := newRig(t)
	r.tr.setScript(slowOK(500 * time.Millisecond))
	backlog := r.backlog("b", 150, 15000)
	r.start()
	advance(time.Second)
	r.live("probe", 200)
	advanceUntil(t, time.Minute, 100*time.Millisecond, "the first backfill batch", func() bool { return len(r.tr.callsOf(wire.ModeBackfill)) > 0 })
	r.snd.SetPressure(true)
	sent := len(r.tr.callsOf(wire.ModeBackfill))

	advance(5 * time.Minute)

	if got := len(r.tr.callsOf(wire.ModeBackfill)); got != sent {
		t.Errorf("%d backfill batches after the pressure turned on with %d sent, want no more", got, sent)
	}
	requireBackfillState(t, r, 1, 0)

	r.snd.SetPressure(false)
	advanceUntil(t, 5*time.Minute, time.Second, "the backfill to finish", func() bool { return r.logs.count(msgBfFinished) > 0 })
	requireBackfillState(t, r, 1, 1)
	requireComplete(t, r, backlog[0])
}

func TestSender_GateAfterThrottleWaitHoldsBackfill(t *testing.T) {
	t.Run("pressure turns on during the wait", func(t *testing.T) {
		bubble(t, func(t *testing.T) {
			r, backlog := throttledBackfill(t)

			r.snd.SetPressure(true)
			requireBackfillHeld(t, r, 1, 40*time.Second)

			r.snd.SetPressure(false)
			advanceUntil(t, 10*time.Minute, time.Second, "the backfill to finish", func() bool { return r.logs.count(msgBfFinished) > 0 })
			requireComplete(t, r, backlog[0])
		})
	})
	t.Run("an episode starts during the wait", func(t *testing.T) {
		bubble(t, func(t *testing.T) {
			r, backlog := throttledBackfill(t)

			r.tr.setScript(func(c call) reply {
				if c.mode() == wire.ModeLive {
					return replyFail(connFail)
				}

				return replyOK()
			})
			r.live("down", 200)
			advanceUntil(t, 10*time.Second, 100*time.Millisecond, "the episode to start", r.snd.episodeActive)
			requireBackfillHeld(t, r, 1, 40*time.Second)

			r.tr.setScript(allOK)
			r.live("up", 200)
			advanceUntil(t, 10*time.Minute, time.Second, "the backfill to finish", func() bool { return r.logs.count(msgBfFinished) > 0 })
			requireComplete(t, r, backlog[0])
		})
	})
}

// throttledBackfill returns a rig whose backfill has sent its first batch and waits in the throttle for the second: the
// bandwidth lets a megabyte batch pass in about sixteen seconds.
func throttledBackfill(t *testing.T) (*rig, []uint64) {
	t.Helper()

	r := newRig(t)
	r.bandwidth = 64 << 10
	backlog := r.backlog("b", 150, 15000)
	r.start()
	advance(time.Second)
	r.live("probe", 200)
	advanceUntil(t, time.Minute, 100*time.Millisecond, "the first backfill batch", func() bool { return len(r.tr.callsOf(wire.ModeBackfill)) > 0 })

	return r, backlog
}

// requireBackfillHeld fails the test when a backfill request is made beyond sent in the span.
func requireBackfillHeld(t *testing.T, r *rig, sent int, span time.Duration) {
	t.Helper()

	advance(span)
	if got := len(r.tr.callsOf(wire.ModeBackfill)); got != sent {
		t.Errorf("%d backfill requests, want %d: the backfill must not send while the backend is down or the memory is short", got, sent)
	}
}

func TestSender_NoPauseLineWithoutBacklog(t *testing.T) {
	t.Run("start under pressure", func(t *testing.T) {
		bubble(t, func(t *testing.T) {
			r := newRig(t)
			r.start()
			r.snd.SetPressure(true)
			advance(time.Second)
			r.live("probe", 200)
			advance(30 * time.Second)

			r.snd.SetPressure(false)
			advance(30 * time.Second)

			requireBackfillState(t, r, 0, 0)
		})
	})
}

func TestSender_GapPutThenFailedAckWritesOneGap(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.tr.setScript(refusePoison)
		r.start()
		recs := []model.Record{
			textRec(time.Now().UTC(), "n0", 200, r.rng),
			textRec(time.Now().UTC(), poisonTag, 200, r.rng),
			textRec(time.Now().UTC(), "n2", 200, r.rng),
		}
		seqs := r.put(recs...)
		failed := false
		r.sp.setAckErr(func(first, last uint64) error {
			if first == seqs[1] && last == seqs[1] && failed == false {
				failed = true

				return errors.New("injected acknowledgement failure")
			}

			return nil
		})

		advance(2 * time.Minute)

		if failed == false {
			t.Fatalf("the acknowledgement of the refused record %d was never attempted, want the give-up to reach it", seqs[1])
		}
		gaps := 0
		for _, c := range r.tr.snapshot() {
			for _, rec := range c.recs {
				if g := gapOf(rec); g != nil && g.FirstSeq == seqs[1] && c.committed {
					gaps++
				}
			}
		}
		if gaps != 1 {
			t.Errorf("%d gap records committed for the refused record %d, want exactly one (the retry after the failed acknowledgement must not write a second)", gaps, seqs[1])
		}
		if left := r.sp.unacked(t); len(left) != 0 {
			t.Errorf("%d records unacknowledged afterwards, want none (first seq %d)", len(left), left[0].Seq)
		}
	})
}

// requireBatchBounds checks that no batch is above the bounds of a batch.
func requireBatchBounds(t *testing.T, calls []call) {
	t.Helper()

	for _, c := range calls {
		if n := c.lineBytes(t); len(c.recs) > MaxBatchRecords || n > MaxBatchBytes {
			t.Errorf("batch %s holds %d bytes of record lines, want at most %d bytes and %d records", c.describe(), n, MaxBatchBytes, MaxBatchRecords)
		}
	}
}

func TestSender_LargestRecordSentAlone(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRigOn(t, openSpool(t, t.TempDir(), testBoot, 512*mebi))
		r.start()
		now := time.Now().UTC()
		giant := giantRec(t, now, MaxBatchBytes)
		seqs := r.put(textRec(now, "s0", 300, r.rng), giant, textRec(now, "s1", 300, r.rng), textRec(now, "s2", 300, r.rng))
		advance(30 * time.Second)

		calls := r.tr.snapshot()

		alone := 0
		for _, c := range calls {
			if c.has(seqs[1]) && len(c.recs) == 1 && c.lineBytes(t) == MaxBatchBytes {
				alone++
			}
		}
		if alone != 1 {
			t.Errorf("the record of %d bytes was sent alone %d times in %s, want once", MaxBatchBytes, alone, describeCalls(calls))
		}
		requireBatchBounds(t, calls)
		requireComplete(t, r, seqs[0])
	})
}

func TestSender_BatchBounds(t *testing.T) {
	bubble(t, func(t *testing.T) {
		r := newRig(t)
		r.start()
		now := time.Now().UTC()
		small := make([]model.Record, 0, 12000)
		for range 12000 {
			small = append(small, metricRec(now, r.rng.Float64()))
		}
		first := r.put(small...)[0]
		large := make([]model.Record, 0, 600)
		for i := range 600 {
			large = append(large, textRec(now, "big"+strconv.Itoa(i), 4000, r.rng))
		}
		r.put(large...)
		advance(time.Minute)

		calls := r.tr.snapshot()

		requireBatchBounds(t, calls)
		requireLiveBatches(t, calls)
		fullest := 0
		for _, c := range calls {
			fullest = max(fullest, c.lineBytes(t))
		}
		if fullest < MaxBatchBytes*9/10 {
			t.Errorf("the fullest batch holds %d bytes of record lines, want batches filled up to the bound of %d", fullest, MaxBatchBytes)
		}
		requireComplete(t, r, first)
		requireNoDuplicates(t, r)
	})
}

func TestSender_StopEndsWithinTwoSeconds(t *testing.T) {
	t.Run("request in flight", func(t *testing.T) { bubble(t, testStopInFlight) })
	t.Run("acknowledgement refused by the closing spool", func(t *testing.T) { bubble(t, testStopAckRefused) })
}

// testStopInFlight stops the sender while a request hangs: Run returns within two seconds, nothing is logged for the
// stop, the record stays unacknowledged, and the next start sends it.
func testStopInFlight(t *testing.T) {
	r := newRig(t)
	r.tr.setScript(func(call) reply { return replyHang() })
	r.start()
	a := r.live("a", 200)
	advance(5 * time.Second)
	before := time.Now()

	r.stop()

	if d := time.Since(before); d > 2*time.Second {
		t.Errorf("Run returned %v after the stop, want within 2s", d)
	}
	if got := len(r.logs.all()); got != 0 {
		t.Errorf("%d log lines for a stop, want none (first %q)", got, r.logs.all()[0].text())
	}
	if left := r.sp.unacked(t); len(left) != 1 || left[0].Seq != a {
		t.Errorf("unacknowledged records %v after the stop, want only %d", describeSeqs(left), a)
	}

	r.tr.setScript(allOK)
	r.start()
	advance(time.Second)
	r.live("b", 200)
	advance(30 * time.Second)
	requireComplete(t, r, a)
}

// testStopAckRefused lets the spool refuse every acknowledgement as a closing spool does: no log line at all.
func testStopAckRefused(t *testing.T) {
	r := newRig(t)
	r.sp.setAckErr(func(uint64, uint64) error { return spool.ErrClosed })
	r.start()
	r.live("a", 200)

	advance(2 * time.Minute)

	if len(r.tr.snapshot()) == 0 {
		t.Errorf("the backend received no request, want the batch to be sent before its acknowledgement is refused")
	}
	for _, e := range r.logs.all() {
		t.Errorf("log line %q for an acknowledgement refused with %v, want none: it is part of the stop", e.text(), spool.ErrClosed)
	}
}

// lagOf returns the sender's lag.
func lagOf(t *testing.T, r *rig) time.Duration {
	t.Helper()

	lag, err := r.snd.SendLag(context.Background())
	if err != nil {
		t.Fatalf("SendLag() error = %v, want nil", err)
	}

	return lag
}

// requireLag fails the test unless the lag is the time since from.
func requireLag(t *testing.T, r *rig, what string, from time.Time) {
	t.Helper()

	if got, want := lagOf(t, r), time.Since(from); got != want {
		t.Errorf("SendLag() %s = %v, want %v", what, got, want)
	}
}

func TestSender_SendLag(t *testing.T) {
	t.Run("newest acknowledged record", func(t *testing.T) { bubble(t, testSendLag) })
	t.Run("backfill acknowledgements do not move it", func(t *testing.T) { bubble(t, testSendLagAfterBackfill) })
}

// testSendLag follows the lag through the start, a delivery, an outage, a recovery and a record from the future.
func testSendLag(t *testing.T) {
	r := newRig(t)
	started := time.Now()
	r.start()
	advance(10 * time.Second)
	requireLag(t, r, "before the first acknowledgement (since the start)", started)

	c1 := time.Now()
	r.live("a", 200)
	advance(8 * time.Second)
	requireLag(t, r, "after the first acknowledgement (since the record's capture)", c1)

	r.tr.setScript(failAll(connFail))
	r.live("b", 200)
	advance(5 * time.Minute)
	requireLag(t, r, "while the backend is unreachable", c1)

	r.tr.setScript(allOK)
	c2 := time.Now()
	seq := r.live("c", 200)
	advanceUntil(t, 3*time.Minute, time.Second, "the record to be acknowledged", func() bool { return r.tr.committedSet()[seq] > 0 })
	requireLag(t, r, "after the backend accepts batches again", c2)

	r.put(textRec(time.Now().UTC().Add(time.Hour), "future", 200, r.rng))
	advance(10 * time.Second)
	if got := lagOf(t, r); got != 0 {
		t.Errorf("SendLag() with a capture time an hour ahead = %v, want 0, never negative", got)
	}
}

// testSendLagAfterBackfill delivers a backlog captured hours ago after a live record: the lag stays that of the live
// record.
func testSendLagAfterBackfill(t *testing.T) {
	r := newRig(t)
	old := time.Now().UTC().Add(-5 * time.Hour)
	recs := make([]model.Record, 0, 20)
	for i := range 20 {
		recs = append(recs, textRec(old, "old"+strconv.Itoa(i), 200, r.rng))
	}
	r.put(recs...)
	r.start()
	advance(time.Second)
	c := time.Now()
	r.live("live", 200)
	advance(30 * time.Second)

	if r.hasUnacked() {
		t.Fatalf("the backlog was not delivered, want the backfill to have finished")
	}
	requireLag(t, r, "after the backfill acknowledged records captured five hours ago", c)
}

// countSegments returns the number of data files in the spool directory.
func countSegments(t *testing.T, dir string) int {
	t.Helper()

	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("ReadDir(%s) error = %v, want nil", dir, err)
	}
	n := 0
	for _, e := range entries {
		if strings.HasPrefix(e.Name(), "segment-") {
			n++
		}
	}

	return n
}

// flapBackend flaps: up for the first up of every cycle, down for the rest.
func flapBackend(start time.Time, up, cycle time.Duration) script {
	return func(c call) reply {
		if c.at.Sub(start)%cycle < up {
			return refusePoison(c)
		}

		return replyFail(connFail)
	}
}

// poisonSeqs returns the sequence numbers of the poison records the backend saw.
func poisonSeqs(calls []call) map[uint64]bool {
	out := map[uint64]bool{}
	for _, c := range calls {
		for _, rec := range c.recs {
			if isPoison(rec) {
				out[rec.Seq] = true
			}
		}
	}

	return out
}

func TestSender_FlapsAndRefusalsKeepRangesBounded(t *testing.T) {
	bubble(t, func(t *testing.T) {
		const (
			cycles = 100
			up     = 40 * time.Second
			cycle  = 380 * time.Second
		)
		r := newRig(t)
		r.bandwidth = 20
		first := r.sp.LastSeq() + 1
		r.tr.setScript(flapBackend(time.Now(), up, cycle))
		r.start()
		rng := r.ownRng()
		f := r.feed(123*time.Millisecond, 45*time.Second, func(i int, at time.Time) model.Record {
			tag := "n" + strconv.Itoa(i)
			if i%8 == 5 && i < 800 {
				tag = poisonTag + strconv.Itoa(i)
			}

			return textRec(at, tag, 200, rng)
		})
		advance(cycles * cycle)
		f.halt()
		r.tr.setScript(refusePoison)
		advanceUntil(t, 72*time.Hour, 10*time.Minute, "every record to be acknowledged", func() bool { return !r.hasUnacked() })
		advance(time.Minute)

		requireFlapInvariants(t, r, first)
	})
}

// requireFlapInvariants checks the end of a long run with flaps and refusals: nothing lost, the spool never refused an
// acknowledgement, the holes stayed below their bound and the files are gone.
func requireFlapInvariants(t *testing.T, r *rig, first uint64) {
	t.Helper()

	poison := poisonSeqs(r.tr.snapshot())
	if len(poison) != 100 {
		t.Errorf("the backend saw %d refused records, want the 100 the stream holds", len(poison))
	}
	requireCompleteExcept(t, r, first, poison)
	gaps := gapsOfCalls(r.tr.snapshot())
	for s := range poison {
		if _, ok := gaps[s]; !ok {
			t.Errorf("the backend received no gap for the refused record %d", s)

			break
		}
	}
	_, tooMany, maxRanges, _ := r.sp.stats()
	if tooMany != 0 || r.logs.count(msgAckNotKept) != 0 {
		t.Errorf("the spool refused %d acknowledgements (%d warnings), want none", tooMany, r.logs.count(msgAckNotKept))
	}
	if maxRanges > MaxHoles {
		t.Errorf("the spool held up to %d acknowledged ranges above the oldest record, want at most %d holes", maxRanges, MaxHoles)
	}
	if got := len(giveUps(r)); got != 100 {
		t.Errorf("%d records given up, want the 100 refused ones", got)
	}
	requireHourlyBound(t, giveUps(r))
	if got := countSegments(t, r.dir); got > 1 {
		t.Errorf("%d data files left, want only the active one: acknowledged files must be deleted", got)
	}
	if got := r.logs.count(msgAccepting); got < 95 {
		t.Errorf("%d episodes ended in the 100 flaps, want about one per flap", got)
	}
}

// holdTransport is a backend that answers the first request at once and holds every later one until its context ends,
// without decoding any, so that a heap measurement holds the sender's batches and no decoded copy of them.
type holdTransport struct {
	calls atomic.Int32
	held  atomic.Int32
}

// Send answers the first request and holds the others until ctx ends.
func (h *holdTransport) Send(ctx context.Context, _ []byte) Result {
	if h.calls.Add(1) == 1 {
		return Result{Status: 200}
	}
	h.held.Add(1)
	<-ctx.Done()

	return ctxResult(ctx)
}

// heapSampler reads the heap in real time from outside the bubble, so it sees the peak of the transient buffers too.
type heapSampler struct {
	armed atomic.Bool
	peak  atomic.Uint64
	stop  chan struct{}
	done  sync.WaitGroup
}

func newHeapSampler() *heapSampler {
	h := &heapSampler{stop: make(chan struct{})}
	h.done.Go(func() {
		var ms runtime.MemStats
		for {
			select {
			case <-h.stop:
				return
			case <-time.After(time.Millisecond):
			}
			if h.armed.Load() {
				runtime.ReadMemStats(&ms)
				if ms.HeapAlloc > h.peak.Load() {
					h.peak.Store(ms.HeapAlloc)
				}
			}
		}
	})

	return h
}

func (h *heapSampler) close() {
	close(h.stop)
	h.done.Wait()
}

// heapNow returns the live heap after a collection.
func heapNow() uint64 {
	var ms runtime.MemStats
	runtime.GC()
	runtime.GC()
	runtime.ReadMemStats(&ms)

	return ms.HeapAlloc
}

// memoryGrowth runs a sender over a backlog of the given records until a full batch is in flight on both streams and
// returns the growth of the live heap at that moment and the growth of the sampled peak, both over the heap before the
// sender started.
func memoryGrowth(t *testing.T, backlog int) (retained, peak uint64) {
	t.Helper()

	// A low collection target keeps the garbage of finished buffers out of the sampled peak.
	defer debug.SetGCPercent(debug.SetGCPercent(10))
	sampler := newHeapSampler()
	defer sampler.close()
	bubble(t, func(t *testing.T) {
		sp := openSpool(t, t.TempDir(), testBoot, 512*mebi)
		tr := &holdTransport{}
		rng := rand.New(rand.NewPCG(21, 22))
		now := time.Now().UTC()
		recs := make([]model.Record, 0, backlog)
		for i := range backlog {
			recs = append(recs, textRec(now, "b"+strconv.Itoa(i), 4000, rng))
		}
		if err := sp.Put(context.Background(), recs); err != nil {
			t.Fatalf("Put(%d records) error = %v, want nil", backlog, err)
		}
		ctx, cancel := context.WithCancel(context.Background())
		done := make(chan struct{})
		snd := New(Options{Spool: sp, Transport: tr, Bandwidth: 1 << 30, Rand: zeroRand}, slog.New(slog.DiscardHandler))
		go func() {
			defer close(done)
			snd.Run(ctx)
		}()
		defer func() {
			cancel()
			<-done
		}()
		synctest.Wait()
		// 1000 live records of 4,000 bytes fill two live batches: the first is answered and releases the backfill, which
		// reads and holds a full batch; the second live batch is held too.
		fresh := make([]model.Record, 0, 1000)
		for i := range 1000 {
			fresh = append(fresh, textRec(now, "l"+strconv.Itoa(i), 4000, rng))
		}
		if err := sp.Put(context.Background(), fresh); err != nil {
			t.Fatalf("Put(%d live records) error = %v, want nil", len(fresh), err)
		}
		base := heapNow()
		sampler.armed.Store(true)
		advanceUntil(t, time.Minute, time.Second, "a full batch in flight on each stream", func() bool { return tr.held.Load() >= 2 })
		advance(SendInterval)
		if got := tr.held.Load(); got != 2 {
			t.Fatalf("%d requests in flight, want 2 (a full batch on each stream)", got)
		}
		retained = max(heapNow(), base) - base
		peak = max(sampler.peak.Load(), base) - base
	})

	return retained, peak
}

func TestSender_MemoryBound(t *testing.T) {
	const limit = 16 << 20
	const slack = 2 << 20
	small, smallPeak := memoryGrowth(t, 600)
	large, largePeak := memoryGrowth(t, 2400)
	t.Logf("heap growth with a full batch in flight on both streams: backlog of 600 records: %d bytes held, %d bytes peak; backlog of 2400 records: %d bytes held, %d bytes peak",
		small, smallPeak, large, largePeak)
	for _, g := range []struct {
		what string
		got  uint64
	}{{"held, backlog 600", small}, {"peak, backlog 600", smallPeak}, {"held, backlog 2400", large}, {"peak, backlog 2400", largePeak}} {
		if g.got >= limit {
			t.Errorf("heap growth (%s) = %d bytes, want below %d", g.what, g.got, limit)
		}
	}
	if large > small+slack || largePeak > smallPeak+slack {
		t.Errorf("heap growth rises with the backlog: held %d to %d bytes, peak %d to %d bytes for 4 times the records, want at most %d more",
			small, large, smallPeak, largePeak, slack)
	}
}
