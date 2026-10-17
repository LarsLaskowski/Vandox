package agent

import (
	"bytes"
	"context"
	"io"
	"io/fs"
	"log/slog"
	"net"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"testing"
	"testing/fstest"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/config"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/selfstats"
	"github.com/LarsLaskowski/Vandox/internal/sysstats"
)

const (
	// waitLimit is the safety net of every wait on a real timer; what is waited for happens at once.
	waitLimit = 10 * time.Second

	validConfig = "agent_id: web-1\nbackend:\n  url: http://100.64.0.1:8081\n"
	testToken   = "0123456789abcdef0123456789abcdef0123456789"

	msgNotificationDisabled = "service manager notification disabled"
	msgWatchdogDisabled     = "watchdog disabled"
)

// journalPrefix matches the start of a line in journal mode.
var journalPrefix = regexp.MustCompile(`^<[0-7]>`)

// metricSink is a scheduler.Putter that remembers the metric names it received.
type metricSink struct {
	mu     sync.Mutex
	names  map[string]bool
	signal chan struct{}
}

func newMetricSink() *metricSink {
	return &metricSink{names: map[string]bool{}, signal: make(chan struct{}, 1)}
}

func (s *metricSink) Put(_ context.Context, records []model.Record) error {
	s.mu.Lock()
	for _, r := range records {
		if p, ok := r.Data.(*model.MetricPoint); ok {
			s.names[p.Name] = true
		}
	}
	s.mu.Unlock()
	select {
	case s.signal <- struct{}{}:
	default:
	}
	return nil
}

func (s *metricSink) has(names ...string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, n := range names {
		if !s.names[n] {
			return false
		}
	}
	return true
}

// running is an agent started by startAgent.
type running struct {
	t      *testing.T
	cancel context.CancelFunc
	done   chan struct{}
	code   int
}

// startAgent calls Run in a goroutine; the agent is stopped in t.Cleanup.
func startAgent(t *testing.T, opts Options) *running {
	t.Helper()

	ctx, cancel := context.WithCancel(context.Background())
	r := &running{t: t, cancel: cancel, done: make(chan struct{})}
	go func() {
		defer close(r.done)
		r.code = Run(ctx, opts)
	}()
	t.Cleanup(func() {
		cancel()
		<-r.done
	})
	return r
}

// stop cancels the agent's context and returns its exit code.
func (r *running) stop() int {
	r.t.Helper()

	r.cancel()
	select {
	case <-r.done:
	case <-time.After(waitLimit):
		r.t.Fatalf("Run did not return within %v after its context was cancelled", waitLimit)
	}
	return r.code
}

// waitMetrics waits until the sink received every named metric.
func (r *running) waitMetrics(s *metricSink, names ...string) {
	r.t.Helper()

	limit := time.After(waitLimit)
	for !s.has(names...) {
		select {
		case <-s.signal:
		case <-r.done:
			r.t.Fatalf("Run returned %d before the sink received %v", r.code, names)
		case <-limit:
			r.t.Fatalf("the sink did not receive %v within %v", names, waitLimit)
		}
	}
}

// notificationSocket is a unixgram socket standing in for the systemd notification socket; its datagrams arrive on msgs.
type notificationSocket struct {
	path string
	conn net.PacketConn
	msgs chan string
}

func newNotificationSocket(t *testing.T) *notificationSocket {
	t.Helper()

	path := filepath.Join(t.TempDir(), "n.sock")
	conn, err := net.ListenPacket("unixgram", path)
	if err != nil {
		t.Fatalf("binding the notification socket: %v", err)
	}
	s := &notificationSocket{path: path, conn: conn, msgs: make(chan string, 1024)}
	t.Cleanup(func() { _ = conn.Close() })
	go func() {
		buf := make([]byte, 256)
		for {
			n, _, err := conn.ReadFrom(buf)
			if err != nil {
				return
			}
			s.msgs <- string(buf[:n])
		}
	}()
	return s
}

// next returns the next datagram, and fails when the agent returns (a running agent is watched through r; r is nil
// once it has stopped) or nothing arrives.
func (s *notificationSocket) next(t *testing.T, r *running) string {
	t.Helper()

	var done <-chan struct{}
	if r != nil {
		done = r.done
	}
	select {
	case m := <-s.msgs:
		return m
	case <-done:
		t.Fatalf("Run returned %d before the next notification", r.code)
	case <-time.After(waitLimit):
		t.Fatalf("no notification within %v", waitLimit)
	}
	return ""
}

// waitFor reads datagrams until one equals want; the ones before it must be in allowed.
func (s *notificationSocket) waitFor(t *testing.T, r *running, want string, allowed ...string) {
	t.Helper()

	for {
		m := s.next(t, r)
		if m == want {
			return
		}
		if !slices.Contains(allowed, m) {
			t.Fatalf("notification %q while waiting for %q, want only %v before it", m, want, allowed)
		}
	}
}

// requireSilent checks that no datagram is queued.
func (s *notificationSocket) requireSilent(t *testing.T) {
	t.Helper()

	select {
	case m := <-s.msgs:
		t.Errorf("notification %q sent, want none", m)
	case <-time.After(50 * time.Millisecond):
	}
}

func writeConfig(t *testing.T, content string) string {
	t.Helper()

	path := filepath.Join(t.TempDir(), "agent.yaml")
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatalf("writing the configuration: %v", err)
	}
	return path
}

// fixtureSources reads self/status from the real sample.
func fixtureSources() selfstats.Sources {
	return selfstats.Sources{Proc: os.DirFS("../../testdata/proc")}
}

// fixtureSystem returns system metrics sources that read the fixture trees and answer statfs and adjtimex with fixed
// values, so no agent test reads the host.
func fixtureSystem() sysstats.Sources {
	return sysstats.Sources{
		Proc: os.DirFS("../../testdata/proc"),
		Sys:  os.DirFS("../../testdata/sys"),
		StatFS: func(string) (sysstats.FSStats, error) {
			return sysstats.FSStats{BlockSize: 4096, Blocks: 1000, BlocksFree: 400, BlocksAvailable: 300, Inodes: 100, InodesFree: 50}, nil
		},
		Clock: func() (sysstats.ClockState, error) {
			return sysstats.ClockState{MaxError: time.Millisecond, EstimatedError: time.Microsecond}, nil
		},
	}
}

// fixtureProcesses returns process sources that read the fixture trees, so no agent test reads the host's /proc or /etc.
func fixtureProcesses() procstats.Sources {
	return procstats.Sources{Proc: os.DirFS("../../testdata/proc"), Etc: os.DirFS("../../testdata/etc")}
}

func logLines(out string) []string {
	return strings.Split(strings.TrimSuffix(out, "\n"), "\n")
}

// countLines returns how many lines of out contain every one of parts.
func countLines(out string, parts ...string) int {
	n := 0
	for _, line := range logLines(out) {
		if slices.ContainsFunc(parts, func(p string) bool { return !strings.Contains(line, p) }) {
			continue
		}
		n++
	}
	return n
}

func TestRun_ReadyWatchdogAndSelfMetrics(t *testing.T) {
	logFile, err := os.Create(filepath.Join(t.TempDir(), "agent.log"))
	if err != nil {
		t.Fatalf("creating the log file: %v", err)
	}
	t.Cleanup(func() { _ = logFile.Close() })
	info, err := logFile.Stat()
	if err != nil {
		t.Fatalf("Stat: %v", err)
	}
	st, ok := info.Sys().(*syscall.Stat_t)
	if !ok {
		t.Fatalf("Stat().Sys() = %T, want *syscall.Stat_t", info.Sys())
	}
	stream := strconv.FormatUint(st.Dev, 10) + ":" + strconv.FormatUint(st.Ino, 10)

	socket := newNotificationSocket(t)
	sink := newMetricSink()
	r := startAgent(t, Options{
		ConfigPath: writeConfig(t, validConfig),
		Environ: []string{
			"VANDOX_AGENT_TOKEN=" + testToken,
			"NOTIFY_SOCKET=" + socket.path,
			"WATCHDOG_USEC=100000",
			"JOURNAL_STREAM=" + stream,
		},
		Stderr:    logFile,
		Sink:      sink,
		Self:      fixtureSources(),
		System:    fixtureSystem(),
		Processes: fixtureProcesses(),
	})

	if first := socket.next(t, r); first != "READY=1" {
		t.Fatalf("first notification = %q, want %q", first, "READY=1")
	}
	socket.waitFor(t, r, "WATCHDOG=1")
	r.waitMetrics(sink, selfstats.MetricRSS, selfstats.MetricCPU, selfstats.MetricGoroutines)
	code := r.stop()

	socket.waitFor(t, nil, "STOPPING=1", "WATCHDOG=1")
	if code != 0 {
		t.Errorf("Run() = %d after the context was cancelled, want 0", code)
	}
	data, err := os.ReadFile(logFile.Name())
	if err != nil {
		t.Fatalf("reading the log file: %v", err)
	}
	out := string(data)
	for i, line := range logLines(out) {
		if !journalPrefix.MatchString(line) {
			t.Errorf("log line %d = %q, want it to start with <N>", i, line)
		}
	}
	if countLines(out, "<6>", `msg="agent started"`, "agent_id=web-1") != 1 {
		t.Errorf(`log holds no info line "agent started" with agent_id=web-1; log:\n%s`, out)
	}
	if countLines(out, `msg="agent stopped"`) != 1 {
		t.Errorf(`log holds no line "agent stopped"; log:\n%s`, out)
	}
}

func TestRun_ConfigErrorExitsOne(t *testing.T) {
	missing := filepath.Join(t.TempDir(), "none.yaml")
	noID := writeConfig(t, "backend:\n  url: http://100.64.0.1:8081\n")
	tests := []struct {
		name    string
		path    string
		wantErr string
	}{
		{"agent_id missing", noID, "config: " + noID + ": agent_id: required key is missing"},
		{"file missing", missing, ""},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			socket := newNotificationSocket(t)
			var stderr bytes.Buffer

			code := Run(context.Background(), Options{
				ConfigPath: tc.path,
				Environ:    []string{"VANDOX_AGENT_TOKEN=" + testToken, "NOTIFY_SOCKET=" + socket.path},
				Stderr:     &stderr,
				Self:       fixtureSources(),
				System:     fixtureSystem(),
				Processes:  fixtureProcesses(),
			})

			if code != 1 {
				t.Errorf("Run() = %d, want 1", code)
			}
			out := stderr.String()
			if countLines(out, "level=ERROR", `msg="agent cannot start"`) != 1 {
				t.Errorf(`log holds %d error lines "agent cannot start", want 1; log:\n%s`,
					countLines(out, "level=ERROR", `msg="agent cannot start"`), out)
			}
			if tc.wantErr != "" && !strings.Contains(out, `err="`+tc.wantErr+`"`) {
				t.Errorf("log does not hold err=%q; log:\n%s", tc.wantErr, out)
			}
			socket.requireSilent(t)
		})
	}
}

func TestRun_DoesNotLogSecret(t *testing.T) {
	const token = "leak-sentinel-0123456789abcdef0123456789"
	var stderr bytes.Buffer
	sink := newMetricSink()
	r := startAgent(t, Options{
		ConfigPath: writeConfig(t, validConfig+"log:\n  level: debug\n"),
		Environ:    []string{"VANDOX_AGENT_TOKEN=" + token},
		Stderr:     &stderr,
		Sink:       sink,
		Self:       fixtureSources(),
		System:     fixtureSystem(),
		Processes:  fixtureProcesses(),
	})

	r.waitMetrics(sink, selfstats.MetricRSS)
	code := r.stop()

	out := stderr.String()
	if code != 0 {
		t.Errorf("Run() = %d, want 0", code)
	}
	if countLines(out, `msg="agent started"`) != 1 {
		t.Fatalf(`the log holds no line "agent started", so the run did not happen; log:\n%s`, out)
	}
	if strings.Contains(out, token) {
		t.Errorf("the log holds the whole agent token")
	}
	if strings.Contains(out, token[:16]) {
		t.Errorf("the log holds the first 16 bytes of the agent token")
	}
}

func TestRun_NotificationProblemsDoNotStopTheAgent(t *testing.T) {
	const vsockErr = "sdnotify: NOTIFY_SOCKET: unsupported address " +
		"(an absolute path of at most 107 bytes or @ and an abstract name of 1 to 107 bytes)"
	const usecErr = "sdnotify: WATCHDOG_USEC: must be a number of microseconds from 100000 to 9223372036854775 in ASCII digits"
	tests := []struct {
		name    string
		env     string
		message string
		wantErr string
	}{
		{"unsupported notification socket", "NOTIFY_SOCKET=vsock:2:1", msgNotificationDisabled, vsockErr},
		{"watchdog interval not a number", "WATCHDOG_USEC=abc", msgWatchdogDisabled, usecErr},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			var stderr bytes.Buffer
			sink := newMetricSink()
			r := startAgent(t, Options{
				ConfigPath: writeConfig(t, validConfig),
				Environ:    []string{"VANDOX_AGENT_TOKEN=" + testToken, tc.env},
				Stderr:     &stderr,
				Sink:       sink,
				Self:       fixtureSources(),
				System:     fixtureSystem(),
				Processes:  fixtureProcesses(),
			})

			r.waitMetrics(sink, selfstats.MetricRSS)
			code := r.stop()

			out := stderr.String()
			if code != 0 {
				t.Errorf("Run() = %d after the cancel, want 0 (the agent runs on)", code)
			}
			if got := countLines(out, "level=WARN", `msg="`+tc.message+`"`, tc.wantErr); got != 1 {
				t.Errorf("log holds %d warning lines %q with the error %q, want 1; log:\n%s", got, tc.message, tc.wantErr, out)
			}
		})
	}
}

// jobSchedule is a job's collector name and schedule.
type jobSchedule struct {
	name     string
	schedule scheduler.Schedule
}

// scheduleOf returns the names and schedules of jobs in order.
func scheduleOf(list []scheduler.Job) []jobSchedule {
	got := make([]jobSchedule, 0, len(list))
	for _, j := range list {
		got = append(got, jobSchedule{name: j.Collector.Name(), schedule: j.Schedule})
	}
	return got
}

func TestJobs_Schedules(t *testing.T) {
	self := jobSchedule{"self", scheduler.Schedule{Interval: 60 * time.Second, Timeout: 10 * time.Second}}
	system := jobSchedule{"system", scheduler.Schedule{Interval: 30 * time.Second, Timeout: 5 * time.Second, PressureInterval: 10 * time.Second}}
	filesystem := jobSchedule{"filesystem", scheduler.Schedule{Interval: 60 * time.Second, Timeout: 10 * time.Second}}
	clock := jobSchedule{"clock", scheduler.Schedule{Interval: 60 * time.Second, Timeout: 5 * time.Second}}
	processes := jobSchedule{"processes", scheduler.Schedule{Interval: 60 * time.Second, Timeout: 10 * time.Second, PressureInterval: 15 * time.Second}}

	tests := []struct {
		name        string
		mountPoints []string
		want        []jobSchedule
	}{
		{"default mount point", []string{"/"}, []jobSchedule{self, system, processes, filesystem, clock}},
		{"two mount points", []string{"/", "/var"}, []jobSchedule{self, system, processes, filesystem, clock}},
		{"no mount point leaves out the filesystem job", []string{}, []jobSchedule{self, system, processes, clock}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg := config.DefaultAgent()
			cfg.Filesystem.MountPoints = tt.mountPoints
			log := slog.New(slog.NewTextHandler(io.Discard, nil))

			list, _ := jobs(Options{Self: fixtureSources(), System: fixtureSystem(), Processes: fixtureProcesses()}, &cfg, log)

			if got := scheduleOf(list); !slices.Equal(got, tt.want) {
				t.Errorf("jobs() = %+v, want %+v", got, tt.want)
			}
			if _, err := scheduler.New(list, scheduler.DiscardSink{}, log); err != nil {
				t.Errorf("scheduler.New(jobs()) error = %v, want nil", err)
			}
		})
	}
}

func TestRun_RecordsSystemMetrics(t *testing.T) {
	sink := newMetricSink()
	r := startAgent(t, Options{
		ConfigPath: writeConfig(t, validConfig),
		Environ:    []string{"VANDOX_AGENT_TOKEN=" + testToken},
		Stderr:     io.Discard,
		Sink:       sink,
		Self:       fixtureSources(),
		System:     fixtureSystem(),
		Processes:  fixtureProcesses(),
	})

	r.waitMetrics(sink, "load1", "mem_available_bytes", "pressure_avg10", "filesystem_size_bytes", "clock_synchronized")

	if code := r.stop(); code != 0 {
		t.Errorf("Run() = %d after the context was cancelled, want 0", code)
	}
}

// loadDir reads all files below dir into a MapFS, so a test in a synctest bubble reads no real file.
func loadDir(t *testing.T, dir string) fstest.MapFS {
	t.Helper()

	files := fstest.MapFS{}
	err := filepath.WalkDir(dir, func(path string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return err
		}
		rel, err := filepath.Rel(dir, path)
		if err != nil {
			return err
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		files[filepath.ToSlash(rel)] = &fstest.MapFile{Data: data}
		return nil
	})
	if err != nil {
		t.Fatalf("reading the fixture tree %s: %v", dir, err)
	}
	return files
}

// addWorker adds a process named worker with the PID pid, a resident set of pid kB and the command line "worker job<pid>".
func addWorker(files fstest.MapFS, pid int) {
	n := strconv.Itoa(pid)
	fields := make([]string, 50)
	for i := range fields {
		fields[i] = "0"
	}
	fields[0], fields[1], fields[19] = "S", "1", "1000"
	files[n+"/stat"] = &fstest.MapFile{Data: []byte(n + " (worker) " + strings.Join(fields, " ") + "\n")}
	files[n+"/status"] = &fstest.MapFile{Data: []byte("Name:\tworker\nUid:\t0\t0\t0\t0\nVmRSS:\t" + n + " kB\nVmSwap:\t0 kB\n")}
	files[n+"/cmdline"] = &fstest.MapFile{Data: []byte("worker job" + n + "\x00")}
	files[n+"/smaps_rollup"] = &fstest.MapFile{Data: []byte("00400000-7ffd1a94b000 ---p 00000000 00:00 0   [rollup]\nRss: " + n + " kB\nPss: " + n + " kB\n")}
}

// snapshotSink is a scheduler.Putter that keeps the process snapshots it received.
type snapshotSink struct {
	mu        sync.Mutex
	snapshots []*model.ProcessSnapshot
}

func (s *snapshotSink) Put(_ context.Context, records []model.Record) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	for _, r := range records {
		if snap, ok := r.Data.(*model.ProcessSnapshot); ok {
			s.snapshots = append(s.snapshots, snap)
		}
	}
	return nil
}

func (s *snapshotSink) all() []*model.ProcessSnapshot {
	s.mu.Lock()
	defer s.mu.Unlock()

	return slices.Clone(s.snapshots)
}

// lowMemoryOptions returns options whose sources read in-memory trees: the system sample reports about 6 percent of the memory
// available, and the process sample sees workers beside the captured processes.
func lowMemoryOptions(t *testing.T, workers int) Options {
	t.Helper()

	proc := loadDir(t, "../../testdata/proc")
	meminfo := string(proc["meminfo"].Data)
	if !strings.Contains(meminfo, "MemAvailable:   15901132 kB") {
		t.Fatalf("test setup: the fixture meminfo lacks the MemAvailable line this test replaces")
	}
	// 1000000 of 16479424 kB are about 6 percent: below the 10 percent that switch memory pressure mode on.
	proc["meminfo"] = &fstest.MapFile{Data: []byte(strings.Replace(meminfo, "MemAvailable:   15901132 kB", "MemAvailable:    1000000 kB", 1))}
	for pid := 1001; pid < 1001+workers; pid++ {
		addWorker(proc, pid)
	}
	return Options{
		Self:      selfstats.Sources{Proc: proc},
		System:    sysstats.Sources{Proc: proc, Sys: loadDir(t, "../../testdata/sys"), StatFS: fixtureSystem().StatFS, Clock: fixtureSystem().Clock},
		Processes: procstats.Sources{Proc: proc, Etc: loadDir(t, "../../testdata/etc")},
	}
}

// runFor runs the scheduler for d of fake time inside a synctest bubble and stops it.
func runFor(t *testing.T, sched *scheduler.Scheduler, d time.Duration) {
	t.Helper()

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		sched.Run(ctx)
	}()
	time.Sleep(d)
	synctest.Wait()
	cancel()
	<-done
}

// fullSnapshot returns the last of the snapshots that is complete and lists n processes, or nil.
func fullSnapshot(snapshots []*model.ProcessSnapshot, n int) *model.ProcessSnapshot {
	var full *model.ProcessSnapshot
	for _, snap := range snapshots {
		if snap.Complete && len(snap.Processes) == n {
			full = snap
		}
	}
	return full
}

func TestNewScheduler_LowMemoryMakesFullProcessSnapshots(t *testing.T) {
	const workers = 60
	opts := lowMemoryOptions(t, workers)
	cfg := config.DefaultAgent()
	cfg.Processes.Programs = []config.ProgramRule{{Program: "grouped", CmdlinePrefix: "worker ", AppendNextWord: true}}
	// The workers and five captured processes: the kernel thread and the zombie of the fixture tree have no smaps_rollup file, which
	// a MapFS answers with "does not exist" (the kernel answers ESRCH), so they count as ended.
	wantProcesses := workers + 5

	synctest.Test(t, func(t *testing.T) {
		sink := &snapshotSink{}
		opts.Sink = sink
		sched, err := newScheduler(opts, &cfg, slog.New(slog.DiscardHandler))
		if err != nil {
			t.Fatalf("newScheduler() error = %v, want nil", err)
		}

		runFor(t, sched, 70*time.Second)

		full := fullSnapshot(sink.all(), wantProcesses)
		if full == nil {
			t.Fatalf("no process snapshot lists all %d processes with complete true among %d snapshots, want one after the low memory switched the mode on",
				wantProcesses, len(sink.all()))
		}
		if full.Total == nil || int(*full.Total) != wantProcesses {
			t.Errorf("Total of the full snapshot = %v, want %d", full.Total, wantProcesses)
		}
		if !slices.ContainsFunc(full.Programs, func(a model.ProgramAggregate) bool { return a.Program == "grouped/job1001" && a.Count == 1 }) {
			t.Errorf("programs of the full snapshot = %+v, want grouped/job1001 named by the configured rule", full.Programs)
		}
	})
}
