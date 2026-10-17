package agent

import (
	"bytes"
	"context"
	"io"
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
	"time"

	"github.com/LarsLaskowski/Vandox/internal/config"
	"github.com/LarsLaskowski/Vandox/internal/model"
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
		Stderr: logFile,
		Sink:   sink,
		Self:   fixtureSources(),
		System: fixtureSystem(),
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

	tests := []struct {
		name        string
		mountPoints []string
		want        []jobSchedule
	}{
		{"default mount point", []string{"/"}, []jobSchedule{self, system, filesystem, clock}},
		{"two mount points", []string{"/", "/var"}, []jobSchedule{self, system, filesystem, clock}},
		{"no mount point leaves out the filesystem job", []string{}, []jobSchedule{self, system, clock}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg := config.DefaultAgent()
			cfg.Filesystem.MountPoints = tt.mountPoints
			log := slog.New(slog.NewTextHandler(io.Discard, nil))

			list := jobs(Options{Self: fixtureSources(), System: fixtureSystem()}, &cfg, log)

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
	})

	r.waitMetrics(sink, "load1", "mem_available_bytes", "pressure_avg10", "filesystem_size_bytes", "clock_synchronized")

	if code := r.stop(); code != 0 {
		t.Errorf("Run() = %d after the context was cancelled, want 0", code)
	}
}

var _ scheduler.Putter = (*metricSink)(nil)
