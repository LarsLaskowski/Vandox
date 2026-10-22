package kernevents

import (
	"bytes"
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"runtime"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"testing/fstest"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/journal/journaltest"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

var (
	bulkMu    sync.Mutex
	bulkCache = map[[3]int][]byte{}
)

// bulkJournal returns the bytes of one archived journal file of n kernel entries one millisecond apart; every killEvery-th entry is a
// kill of PID i+1 with a process name of nameLen bytes (killEvery 0: no kill). The file is built once per size and shared.
func bulkJournal(t testing.TB, n, killEvery, nameLen int) []byte {
	t.Helper()
	bulkMu.Lock()
	defer bulkMu.Unlock()
	key := [3]int{n, killEvery, nameLen}
	if b, ok := bulkCache[key]; ok {
		return b
	}
	entries := make([]entry, 0, n)
	name := strings.Repeat("n", nameLen)
	for i := range n {
		us := uint64(i) * 1000 // #nosec G115 -- small test values
		rt, mono, seq := r0()+us, m0+us, uint64(2*(i+1))
		// the lines share their text and a few source stamps, so that a large file is cheap to build
		msg, src := "handmade kernel line", m0+uint64(i%64)*1000 // #nosec G115 -- small test values
		if killEvery > 0 && i%killEvery == killEvery-1 {
			msg, src = killMsg(i+1, name, 100, 0), mono-lagUS
		}
		entries = append(entries, kernelLine(seq, bootB, rt, mono, src, msg))
	}
	var buf bytes.Buffer
	opts := journaltest.Options{FileID: mustID(t, "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"), MachineID: mustID(t, machineID), SeqnumID: mustID(t, seqIDA)}
	if err := journaltest.Write(&buf, entries, opts); err != nil {
		t.Fatalf("journaltest.Write() error = %v, want nil", err)
	}
	bulkCache[key] = buf.Bytes()
	return buf.Bytes()
}

// bulkEnv returns an environment whose journal is the bulk file, with a saved position before its first entry.
func bulkEnv(t *testing.T, n, killEvery, nameLen int) *env {
	t.Helper()
	e := newEnv(t)
	e.j.put(fileName(kindArchived, seqIDA, 2, r0()), bulkJournal(t, n, killEvery, nameLen))
	e.seed(dashed(bootB), before())
	return e
}

func TestKernel_RunStopsAfter256Kills(t *testing.T) {
	e := bulkEnv(t, 600, 2, 5) // 300 kills
	e.setUptime(5_000_000)

	first := e.run(false)
	second := e.run(false)
	third := e.run(false)

	if len(killsOf(first)) != 256 {
		t.Fatalf("first run returned %d kills, want exactly 256 (the limit of one run)", len(killsOf(first)))
	}
	all := append(pids(killsOf(first)), pids(killsOf(second))...)
	want := make([]int32, 300)
	for i := range want {
		want[i] = int32(2*i + 2) // #nosec G115 -- small test values
	}
	if !slices.Equal(all, want) {
		t.Errorf("kills of the first two runs = %d PIDs starting %v, want the 300 kills in journal order without a duplicate or a loss", len(all), all[:min(len(all), 5)])
	}
	if len(killsOf(second)) != 44 || len(killsOf(third)) != 0 {
		t.Errorf("second run kills = %d, third = %d, want 44 and 0", len(killsOf(second)), len(killsOf(third)))
	}
	checkValid(t, first, t0)
}

func TestKernel_TimeBudgetEachRunGetsFurther(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		e := bulkEnv(t, 1500, 250, 5) // 6 kills
		e.j.setHook(func(op, _ string, _ int64) error {
			if op == "read" {
				time.Sleep(time.Millisecond) // a slow disk
			}
			return nil
		})
		var all []int32
		var runs int
		for runs < 400 {
			begin := time.Now()

			recs := e.run(false)

			if took := time.Since(begin); took > 1500*time.Millisecond {
				t.Fatalf("run %d took %s, want at most about 1 s of reading (half the timeout)", runs+1, took)
			}
			all = append(all, pids(killsOf(recs))...)
			runs++
			if len(all) == 6 {
				break
			}
		}

		want := []int32{250, 500, 750, 1000, 1250, 1500}
		if !slices.Equal(all, want) {
			t.Errorf("kills over %d runs = %v, want %v (every run goes further than the one before)", runs, all, want)
		}
		if runs < 2 {
			t.Errorf("the backlog was read in %d run, want it spread over several runs of at most 1 s", runs)
		}
	})
}

func TestKernel_PollWorkIndependentOfEarlierEntries(t *testing.T) {
	const budget = 400
	measure := func(n int) (first, idle, withNew int64) {
		t.Helper()
		e := newEnv(t)
		e.j.put(fileName(kindArchived, seqIDA, 2, r0()), bulkJournal(t, n, 0, 0))
		e.seed(dashed(bootB), position(seqIDA, uint64(2*n), r0()+uint64(n-1)*1000)) // #nosec G115 -- small test values
		before := e.j.reads.Load()
		e.run(false)
		first = e.j.reads.Load() - before
		before = e.j.reads.Load()
		e.run(false)
		idle = e.j.reads.Load() - before
		var c seqCounter
		c.n = uint64(n) * 2 // #nosec G115 -- a test size
		var kills []entry
		for i := range 10 {
			kills = append(kills, c.kill(bootB, 200+i, 9000+i, "p", lagUS))
		}
		e.add(kills...)
		before = e.j.reads.Load()
		recs := e.run(false)
		withNew = e.j.reads.Load() - before
		if len(killsOf(recs)) != 10 {
			t.Fatalf("%d entries: the poll after the new file returned %d kills, want 10", n, len(killsOf(recs)))
		}
		return first, idle, withNew
	}

	smallFirst, smallIdle, smallNew := measure(1000)
	bigFirst, bigIdle, bigNew := measure(100000)

	for name, v := range map[string]int64{"first poll (1,000)": smallFirst, "idle poll (1,000)": smallIdle, "first poll (100,000)": bigFirst, "idle poll (100,000)": bigIdle} {
		if v > budget {
			t.Errorf("%s made %d reads, want at most %d", name, v, budget)
		}
	}
	if bigFirst > smallFirst+100 || bigIdle > smallIdle+100 {
		t.Errorf("reads grew with the journal: first poll %d to %d, idle poll %d to %d, want a difference of at most 100", smallFirst, bigFirst, smallIdle, bigIdle)
	}
	if bigNew > smallNew+100 {
		t.Errorf("the poll for 10 new kills made %d reads at 100,000 entries and %d at 1,000, want the work to follow the new entries only", bigNew, smallNew)
	}
	if smallIdle == 0 || smallFirst == 0 {
		t.Errorf("polls made %d and %d reads, want the test to count the collector's reads", smallFirst, smallIdle)
	}
}

func TestKernel_BacklogMemoryIsBounded(t *testing.T) {
	const bound = 4 << 20
	for _, n := range []int{1000, 100000} {
		t.Run(strconv.Itoa(n)+" kernel entries", func(t *testing.T) {
			e := bulkEnv(t, n, 300, 280)
			heap := func() int64 {
				runtime.GC()
				var m runtime.MemStats
				runtime.ReadMemStats(&m)
				return int64(m.HeapAlloc) // #nosec G115 -- a heap size fits
			}
			base := heap()
			var peak atomic.Int64
			var reads atomic.Int64
			e.j.setHook(func(op, _ string, _ int64) error {
				if op == "read" && reads.Add(1)%20000 == 0 {
					peak.Store(max(peak.Load(), heap()-base))
				}
				return nil
			})

			recs := e.run(false)
			grown := max(peak.Load(), heap()-base)
			runtime.KeepAlive(recs)

			if len(killsOf(recs)) == 0 {
				t.Fatalf("the run returned no kills, want some of the backlog")
			}
			if grown > bound {
				t.Errorf("live heap grew by %d bytes during a run over %d kernel entries, want at most %d (independent of the backlog)", grown, n, bound)
			}
		})
	}
}

// sinkRec is a scheduler sink that keeps the records it gets.
type sinkRec struct {
	mu   sync.Mutex
	recs []model.Record
}

func (s *sinkRec) Put(_ context.Context, r []model.Record) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.recs = append(s.recs, r...)
	return nil
}

func (s *sinkRec) all() []model.Record {
	s.mu.Lock()
	defer s.mu.Unlock()
	return slices.Clone(s.recs)
}

// metronome is a collector that records that it ran.
type metronome struct{ n atomic.Int32 }

func (m *metronome) Name() string { return "metronome" }

func (m *metronome) Collect(_ context.Context, _ scheduler.Run) ([]model.Record, error) {
	return []model.Record{{Meta: model.Meta{Source: "metronome"}, Data: &model.MetricPoint{Name: "ticks", Value: float64(m.n.Add(1))}}}, nil
}

// stuckJournal makes the reads of the journal block until it is released and counts the readers that entered.
type stuckJournal struct {
	release chan struct{}
	once    sync.Once
	entered atomic.Int32
	inside  atomic.Int32
	most    atomic.Int32
}

func newStuckJournal(j *journalFS) *stuckJournal {
	s := &stuckJournal{release: make(chan struct{})}
	j.setHook(func(op, _ string, _ int64) error {
		if op == "read" || op == "open" {
			s.entered.Add(1)
			n := s.inside.Add(1)
			s.most.Store(max(s.most.Load(), n))
			<-s.release
			s.inside.Add(-1)
		}
		return nil
	})
	return s
}

func (s *stuckJournal) unblock() { s.once.Do(func() { close(s.release) }) }

// countKernelGaps returns the collector_timeout gaps of the kernel collector and the kills of PID pid in the records.
func countKernelGaps(recs []model.Record, pid int32) (timeouts, kills int) {
	for _, r := range recs {
		switch d := r.Data.(type) {
		case *model.Gap:
			if d.Collector == Name && d.Cause == model.GapCollectorTimeout {
				timeouts++
			}
		case *model.KernelEvent:
			if d.Type == model.KernelEventOOMKill && d.OOMKill.VictimPID == pid {
				kills++
			}
		}
	}
	return timeouts, kills
}

func TestKernel_HangingJournalLosesOnlyItsSample(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		e := newEnv(t)
		var c seqCounter
		pre := []entry{c.line(bootB, 1)}
		e.add(append(slices.Clone(pre), c.kill(bootB, 2, 4711, "pzstd", lagUS))...)
		e.seed(dashed(bootB), position(seqIDA, seqOf(pre[0]), rtOf(pre[0])))
		stuck := newStuckJournal(e.j)
		other := &metronome{}
		sink := &sinkRec{}
		sched, err := scheduler.New([]scheduler.Job{
			{Collector: e.k, Schedule: scheduler.Schedule{Interval: 2 * time.Second, Timeout: 2 * time.Second}},
			{Collector: other, Schedule: scheduler.Schedule{Interval: 2 * time.Second, Timeout: time.Second}},
		}, sink, slog.New(e.logs))
		if err != nil {
			t.Fatalf("scheduler.New() error = %v, want nil", err)
		}
		ctx, cancel := context.WithCancel(context.Background())
		done := make(chan struct{})
		go func() {
			defer close(done)
			sched.Run(ctx)
		}()
		t.Cleanup(func() {
			cancel()
			stuck.unblock()
			<-done
			synctest.Wait()
		})

		time.Sleep(11 * time.Second)
		synctest.Wait()

		if got := other.n.Load(); got != 6 {
			t.Errorf("the other collector ran %d times in 11 s, want 6 (it keeps its schedule)", got)
		}
		if stuck.most.Load() != 1 || stuck.entered.Load() != 1 {
			t.Errorf("the journal read was entered %d times with %d at once, want once: the reader is not started a second time while it is stuck", stuck.entered.Load(), stuck.most.Load())
		}
		if timeouts, _ := countKernelGaps(sink.all(), 4711); timeouts == 0 {
			t.Errorf("no collector_timeout gap of the kernel collector in the sink, want the lost sample recorded")
		}
		if !sched.Healthy(time.Now()) {
			t.Errorf("Healthy() = false after 11 s of a stuck read, want true (the watchdog keeps running)")
		}

		stuck.unblock()
		time.Sleep(6 * time.Second)
		synctest.Wait()

		if _, kills := countKernelGaps(sink.all(), 4711); kills != 1 {
			t.Errorf("the kill reached the sink %d times after the read returned, want exactly once", kills)
		}
	})
}

// outages are the ways the journal can become unreadable, each with its repair.
var outages = []struct {
	name    string
	breakIt func(e *env)
	fix     func(e *env)
}{
	{"no machine ID", func(e *env) { delete(e.etc, "machine-id") }, func(e *env) {
		e.etc["machine-id"] = &fstest.MapFile{Data: []byte(machineID + "\n")}
	}},
	{"no journal directory", func(e *env) { e.j.setNoDir(true) }, func(e *env) { e.j.setNoDir(false) }},
	{"directory not readable", func(e *env) { e.j.setDenied(fs.ErrPermission) }, func(e *env) { e.j.setDenied(nil) }},
}

func TestKernel_UnreadableJournalLogsOnceAndResumes(t *testing.T) {
	for _, tc := range outages {
		t.Run(tc.name, func(t *testing.T) {
			e := newEnv(t)
			var c seqCounter
			pre := []entry{c.line(bootB, 1), c.line(bootB, 2)}
			e.add(pre...)
			e.seed(dashed(bootB), tailOf(seqIDA, pre))
			e.run(false)
			if n := len(e.logs.all()); n != 0 {
				t.Fatalf("a readable journal logged %d lines %v, want none", n, e.logs.all())
			}

			tc.breakIt(e)
			lost := runBroken(e, 3)
			e.add(c.kill(bootB, 4, 4711, "pzstd", lagUS)) // written while the journal cannot be read
			requireOneWarning(t, e, 1)
			tc.fix(e)

			recs, err := e.collect(lost)

			if err != nil {
				t.Fatalf("Collect() after the journal is readable again: error = %v, want nil", err)
			}
			if got := pids(killsOf(recs)); !slices.Equal(got, []int32{4711}) {
				t.Errorf("kills after the journal is readable again = %v, want [4711] (reading resumes at the saved position, no kill is lost)", got)
			}
			e.run(false)
			if n := len(e.logs.find(slog.LevelInfo, "kernel messages readable again")); n != 1 {
				t.Errorf("\"readable again\" lines = %d, want 1", n)
			}
			requireOneWarning(t, e, 1)
			tc.breakIt(e)
			runBroken(e, 2)
			requireOneWarning(t, e, 2)
		})
	}
}

// runBroken makes n runs on an unreadable journal and returns whether the last result was dropped.
func runBroken(e *env, n int) (lost bool) {
	for range n {
		_, err := e.collect(lost)
		lost = err != nil
	}
	return lost
}

// requireOneWarning checks the number of "not readable" warnings and that the err attribute of the last one is set and names no path.
func requireOneWarning(t *testing.T, e *env, want int) {
	t.Helper()
	warns := e.logs.find(slog.LevelWarn, "kernel messages not readable")
	if len(warns) != want || warns[len(warns)-1].attrs["err"] == "" {
		t.Fatalf("warnings = %v, want %d, each with an err attribute", warns, want)
	}
	if strings.Contains(warns[len(warns)-1].attrs["err"], machineID) {
		t.Errorf("err attribute %q holds the machine ID, want no path", warns[len(warns)-1].attrs["err"])
	}
}

// failReads makes every read of a journal file beyond its header fail with an error that carries a path and a secret text.
func failReads(e *env) {
	e.j.setHook(func(op, _ string, off int64) error {
		if op == "read" && off >= 256 {
			return errors.New("read /secret-path-" + leak + ": input/output error")
		}
		return nil
	})
}

func TestKernel_JournalErrorsLogFixedReasons(t *testing.T) {
	tests := []struct {
		name string
		kind fileKind
		mess func(e *env, name string)
		want string
	}{
		{"unsupported layout", kindArchived, func(e *env, name string) {
			b := e.j.data(name)
			copy(b, "NOTAJRNL")
			e.j.put(name, b)
		}, "journal: archived: unsupported layout"},
		{"malformed file", kindArchived, func(e *env, name string) {
			b := e.j.data(name)
			copy(b[104:112], []byte{0, 0, 0, 0, 0, 1, 0, 0}) // the data hash table lies far outside the file
			e.j.put(name, b)
		}, "journal: archived: malformed file"},
		{"read failure of an archived file", kindArchived, func(e *env, _ string) { failReads(e) }, "journal: archived: read failed"},
		{"read failure of the online file", kindOnline, func(e *env, _ string) { failReads(e) }, "journal: online: read failed"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			e := newEnv(t)
			var c seqCounter
			ents := []entry{c.line(bootB, 1), c.line(bootB, 2), c.kill(bootB, 3, 4711, "p", lagUS)}
			name := e.j.add(t, tc.kind, seqIDA, ents)
			e.seed(dashed(bootB), before())
			tc.mess(e, name)

			_, _ = e.collect(false)
			_, _ = e.collect(false)

			warns := e.logs.find(slog.LevelWarn, "kernel messages not readable")
			if len(warns) != 1 || warns[0].attrs["err"] != tc.want {
				t.Errorf("warnings = %v, want exactly one with err %q", warns, tc.want)
			}
			if strings.Contains(e.logs.text(), leak) || strings.Contains(e.logs.text(), "secret-path") {
				t.Errorf("the log %q holds text of the underlying error, want only the fixed reason", e.logs.text())
			}
		})
	}
}

func TestKernel_LogsHoldNoMessageText(t *testing.T) {
	e := newEnv(t)
	var c seqCounter
	e.add(c.line(bootB, 1), c.kill(bootB, 2, 4711, leak, lagUS), missedLine(c.next(), bootB, rtAt(3), monoAt(3)), c.line(bootB, 4),
		kernelLine(c.next(), bootB, rtAt(5), monoAt(5), monoAt(5)-lagUS, killMsg(4712, leak, 1, 0)+strings.Repeat(" "+leak, 60)),
		c.line(bootB, 7), c.kill(bootB, 8, 4713, leak+"\n"+leak, lagUS))
	e.seed("", before()) // a first boot of the state: a boot event is made as well
	e.uptimeNow(0)

	recs := e.run(false)
	e.run(false)

	if len(killsOf(recs)) != 2 || len(bootsOf(recs)) != 1 || len(gapsOf(recs)) != 2 {
		t.Fatalf("test set-up: %d kills, %d boot events and %d gaps, want 2, 1 and 2", len(killsOf(recs)), len(bootsOf(recs)), len(gapsOf(recs)))
	}
	if lines := e.logs.all(); len(lines) != 0 {
		t.Errorf("a normal run logged %d lines %q, want none (no line per event, per snapshot or per gap)", len(lines), e.logs.text())
	}
}

func TestKernel_FallsBackToTheVolatileJournal(t *testing.T) {
	e := newEnv(t)
	persistent := newJournalFS()
	persistent.setNoDir(true) // /var/log/journal has no directory of this machine
	var c seqCounter
	pre := []entry{c.line(bootB, 1)}
	e.add(append(slices.Clone(pre), c.kill(bootB, 2, 4711, "pzstd", lagUS))...)
	e.seed(dashed(bootB), tailOf(seqIDA, pre))
	e.k = New(Sources{Journal: []fs.FS{persistent, e.j}, Etc: e.etc, Proc: e.proc}, e.store, slog.New(e.logs))
	e.k.SetTrigger(e.trig.fn)

	recs := e.run(false)

	if got := pids(killsOf(recs)); !slices.Equal(got, []int32{4711}) {
		t.Errorf("kills = %v, want [4711] from the second journal root", got)
	}
	if lines := e.logs.all(); len(lines) != 0 {
		t.Errorf("logged %v, want nothing (the volatile journal is a normal place to read)", lines)
	}
}
