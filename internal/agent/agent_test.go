package agent

import (
	"bytes"
	"context"
	"errors"
	"fmt"
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

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/config"
	"github.com/LarsLaskowski/Vandox/internal/connstats"
	"github.com/LarsLaskowski/Vandox/internal/journal/journaltest"
	"github.com/LarsLaskowski/Vandox/internal/kernevents"
	"github.com/LarsLaskowski/Vandox/internal/logship"
	"github.com/LarsLaskowski/Vandox/internal/mariadb"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/selfstats"
	"github.com/LarsLaskowski/Vandox/internal/svcstats"
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

// fixtureSocketMount is the mnt_id of the captured sockets (testdata/connections/sandbox/fdinfo), so no agent test opens a socket to probe it.
const fixtureSocketMount = 10

// fixtureConnections returns connection sources that read the fixture tree, so no agent test reads the host's /proc.
func fixtureConnections() connstats.Sources {
	return connstats.Sources{Proc: os.DirFS("../../testdata/proc"), SocketMount: connstats.SocketMount{ID: fixtureSocketMount}}
}

// The identities of the kernel fixture: the machine, the boot, the sequence number ID of the journal files and the boot time.
const (
	kernelMachineID = "0badc0de0badc0de0badc0de0badc0de"
	kernelSeqID     = "00000000000000000000000000000001"
	kernelBootRaw   = "c0ffee00-c0ff-ee00-c0ff-ee00c0ffee02"
	kernelBootStamp = "c0ffee00c0ffee00c0ffee00c0ffee02"

	// kernelMono is the monotonic time in microseconds at second 0 of the timeline of TestNewScheduler_LiveKillTriggersExtraSnapshots.
	kernelMono = 2_000_000_000
)

// memTree is a file tree in memory that a test changes while the agent reads it. The kernel sources use it as /proc, /etc and
// the journal root: the journal is modelled as journald lays it out (the machine directory below the root, files named
// system@<seqnum ID>-<head seqnum>-<head real time>.journal, written by journaltest in the layout of systemd 249). That layout
// is pinned against journalctl by journaltest.TestWrite_JournalctlAcceptsFixture and is the one the journal reader and the
// harness of internal/kernevents use; the growth of a journal is modelled by adding a file, as journald's rotation does.
type memTree struct {
	mu    sync.Mutex
	files map[string][]byte
}

// newMemTree returns a tree with the files.
func newMemTree(files map[string]string) *memTree {
	m := &memTree{files: map[string][]byte{}}
	for name, data := range files {
		m.files[name] = []byte(data)
	}
	return m
}

// set stores the bytes of a file.
func (m *memTree) set(name string, data []byte) {
	m.mu.Lock()
	defer m.mu.Unlock()

	m.files[name] = data
}

// Open implements fs.FS.
func (m *memTree) Open(name string) (fs.File, error) {
	m.mu.Lock()
	tree := fstest.MapFS{}
	for n, data := range m.files {
		tree[n] = &fstest.MapFile{Data: data, Mode: 0o640}
	}
	m.mu.Unlock()

	return tree.Open(name)
}

// kernelWorld is the host the kernel collector reads: a /proc with the boot, an /etc with the machine ID and a journal root
// without files.
type kernelWorld struct {
	proc    *memTree
	etc     *memTree
	journal *memTree
}

// newKernelWorld returns the host of boot kernelBootRaw that booted an hour before the start of the fake clock.
func newKernelWorld() *kernelWorld {
	return &kernelWorld{
		proc: newMemTree(map[string]string{
			"sys/kernel/random/boot_id": kernelBootRaw + "\n",
			"stat":                      "cpu  1 2 3 4\nbtime 946681200\nprocesses 5\n",
			"uptime":                    "5000000.00 100.00\n",
		}),
		etc:     newMemTree(map[string]string{"machine-id": kernelMachineID + "\n"}),
		journal: newMemTree(nil),
	}
}

// sources returns the kernel sources of the world.
func (w *kernelWorld) sources() kernevents.Sources {
	return kernevents.Sources{Journal: []fs.FS{w.journal}, Etc: w.etc, Proc: w.proc}
}

// setUptime sets the first field of /proc/uptime.
func (w *kernelWorld) setUptime(seconds float64) {
	w.proc.set("uptime", []byte(strconv.FormatFloat(seconds, 'f', 2, 64)+" 100.00\n"))
}

// addJournalFile writes entries as an archived journal file of the machine.
func (w *kernelWorld) addJournalFile(t *testing.T, entries []map[string][]byte) {
	t.Helper()

	var id, machine, seqnum [16]byte
	for i := range id {
		id[i] = byte(len(w.journal.files) + 1) // #nosec G115 -- a handful of files
	}
	for i := range machine {
		v, err := strconv.ParseUint(kernelMachineID[2*i:2*i+2], 16, 8)
		if err != nil {
			t.Fatalf("machine ID %q is not hex: %v", kernelMachineID, err)
		}
		machine[i] = byte(v)
	}
	seqnum[15] = 1
	var buf bytes.Buffer
	if err := journaltest.Write(&buf, entries, journaltest.Options{FileID: id, MachineID: machine, SeqnumID: seqnum}); err != nil {
		t.Fatalf("journaltest.Write() error = %v, want nil", err)
	}
	name := "system@" + kernelSeqID + "-" + hexStamp(t, entries[0]["__SEQNUM"]) + "-" + hexStamp(t, entries[0]["__REALTIME_TIMESTAMP"]) + ".journal"
	w.journal.set(kernelMachineID+"/"+name, buf.Bytes())
}

// hexStamp returns the 16 hex digits of a decimal stamp, as journald writes them into a file name.
func hexStamp(t *testing.T, decimal []byte) string {
	t.Helper()

	v, err := strconv.ParseUint(string(decimal), 10, 64)
	if err != nil {
		t.Fatalf("stamp %q is not a number: %v", decimal, err)
	}
	return fmt.Sprintf("%016x", v)
}

// kernelLine returns a journal entry of the kernel transport and facility 0 that the journal received at second sec of the
// timeline that starts at origin, and that the kernel wrote lagUS microseconds earlier.
func kernelLine(seq uint64, origin time.Time, sec int, lagUS uint64, message string) map[string][]byte {
	us := uint64(sec) * 1_000_000 // #nosec G115 -- small test values
	mono := kernelMono + us
	text := func(v uint64) []byte { return []byte(strconv.FormatUint(v, 10)) }
	return map[string][]byte{
		"__SEQNUM":                    text(seq),
		"__REALTIME_TIMESTAMP":        text(uint64(origin.UnixMicro()) + us), // #nosec G115 -- the fake clock is after 1970
		"__MONOTONIC_TIMESTAMP":       text(mono),
		"_BOOT_ID":                    []byte(kernelBootStamp),
		"_TRANSPORT":                  []byte("kernel"),
		"SYSLOG_FACILITY":             []byte("0"),
		"SYSLOG_IDENTIFIER":           []byte("kernel"),
		"PRIORITY":                    []byte("6"),
		"_SOURCE_MONOTONIC_TIMESTAMP": text(mono - lagUS),
		"MESSAGE":                     []byte(message),
	}
}

// fixtureKernel returns kernel sources in memory (a host without journal files), so no agent test reads the host's journal, /etc
// or /proc.
func fixtureKernel() kernevents.Sources {
	return newKernelWorld().sources()
}

// fixtureLogs returns log shipping sources of a host without a journal and without files, so no agent test reads the host's
// journal, /etc or log files.
func fixtureLogs() logship.Sources {
	return logship.Sources{
		Etc:     fstest.MapFS{},
		Journal: []fs.FS{fstest.MapFS{}},
		Open:    func(string) (*os.File, error) { return nil, os.ErrNotExist },
	}
}

// errFixtureMariaDB is what the dialer of fixtureMariaDB answers.
var errFixtureMariaDB = errors.New("fixture: no database")

// fixtureMariaDB returns MariaDB sources whose dialer always fails and that read the fixture /proc, so no agent test
// connects to the host's database socket or reads the host's /proc; every Options a test runs or schedules sets it.
func fixtureMariaDB() mariadb.Sources {
	return mariadb.Sources{
		Dial: func(context.Context) (net.Conn, error) { return nil, errFixtureMariaDB },
		Proc: os.DirFS("../../testdata/proc"),
	}
}

// fakeSystemd is a hand-written systemd: every unit asked for is active and running. It models no real timing or error
// behavior of systemd; the real reader is tested against the measured fake bus of the sdbus package.
type fakeSystemd struct {
	mu    sync.Mutex
	opens int
	reads [][]string
}

// Open implements svcstats.Opener.
func (f *fakeSystemd) Open(context.Context) (svcstats.Reader, error) {
	f.mu.Lock()
	defer f.mu.Unlock()

	f.opens++
	return &fakeReader{f: f}, nil
}

// readNames returns the names of every Read call.
func (f *fakeSystemd) readNames() [][]string {
	f.mu.Lock()
	defer f.mu.Unlock()

	return slices.Clone(f.reads)
}

// fakeReader is one open connection of fakeSystemd.
type fakeReader struct {
	f *fakeSystemd
}

// List implements svcstats.Reader: no unit matches.
func (r *fakeReader) List(context.Context, []string) ([]svcstats.Unit, error) {
	return nil, nil
}

// Read implements svcstats.Reader.
func (r *fakeReader) Read(_ context.Context, names []string) ([]svcstats.Unit, error) {
	r.f.mu.Lock()
	r.f.reads = append(r.f.reads, slices.Clone(names))
	r.f.mu.Unlock()

	units := make([]svcstats.Unit, 0, len(names))
	for _, name := range names {
		units = append(units, svcstats.Unit{ID: name, LoadState: "loaded", ActiveState: "active", SubState: "running", ActiveEnter: 1792240371069328})
	}
	return units, nil
}

// Close implements svcstats.Reader.
func (r *fakeReader) Close() error { return nil }

// fixtureServices returns systemd sources that answer from a fake, so no agent test dials the system bus.
func fixtureServices() svcstats.Sources {
	return svcstats.Sources{Systemd: &fakeSystemd{}}
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
		Stderr:      logFile,
		Sink:        sink,
		Self:        fixtureSources(),
		System:      fixtureSystem(),
		Processes:   fixtureProcesses(),
		Connections: fixtureConnections(),
		Services:    fixtureServices(),
		MariaDB:     fixtureMariaDB(),
		Kernel:      fixtureKernel(),
		Logs:        fixtureLogs(),
		StateDir:    t.TempDir(),
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
			stderr := &syncBuffer{}

			code := Run(context.Background(), Options{
				ConfigPath:  tc.path,
				Environ:     []string{"VANDOX_AGENT_TOKEN=" + testToken, "NOTIFY_SOCKET=" + socket.path},
				Stderr:      stderr,
				Self:        fixtureSources(),
				System:      fixtureSystem(),
				Processes:   fixtureProcesses(),
				Connections: fixtureConnections(),
				Services:    fixtureServices(),
				MariaDB:     fixtureMariaDB(),
				Kernel:      fixtureKernel(),
				Logs:        fixtureLogs(),
				StateDir:    t.TempDir(),
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
	stderr := &syncBuffer{}
	sink := newMetricSink()
	r := startAgent(t, Options{
		ConfigPath:  writeConfig(t, validConfig+"log:\n  level: debug\n"),
		Environ:     []string{"VANDOX_AGENT_TOKEN=" + token},
		Stderr:      stderr,
		Sink:        sink,
		Self:        fixtureSources(),
		System:      fixtureSystem(),
		Processes:   fixtureProcesses(),
		Connections: fixtureConnections(),
		Services:    fixtureServices(),
		MariaDB:     fixtureMariaDB(),
		Kernel:      fixtureKernel(),
		Logs:        fixtureLogs(),
		StateDir:    t.TempDir(),
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
			stderr := &syncBuffer{}
			sink := newMetricSink()
			r := startAgent(t, Options{
				ConfigPath:  writeConfig(t, validConfig),
				Environ:     []string{"VANDOX_AGENT_TOKEN=" + testToken, tc.env},
				Stderr:      stderr,
				Sink:        sink,
				Self:        fixtureSources(),
				System:      fixtureSystem(),
				Processes:   fixtureProcesses(),
				Connections: fixtureConnections(),
				Services:    fixtureServices(),
				MariaDB:     fixtureMariaDB(),
				Kernel:      fixtureKernel(),
				Logs:        fixtureLogs(),
				StateDir:    t.TempDir(),
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
	connections := jobSchedule{"connections", scheduler.Schedule{Interval: 60 * time.Second, Timeout: 10 * time.Second, PressureInterval: 15 * time.Second}}

	systemd := jobSchedule{"systemd", scheduler.Schedule{Interval: 30 * time.Second, Timeout: 5 * time.Second, PressureInterval: 10 * time.Second}}
	kernel := jobSchedule{"kernel", scheduler.Schedule{Interval: 2 * time.Second, Timeout: 2 * time.Second}}
	logs := jobSchedule{"logs", scheduler.Schedule{Interval: 2 * time.Second, Timeout: 2 * time.Second}}
	backups := jobSchedule{"backups", scheduler.Schedule{Interval: 10 * time.Second, Timeout: 5 * time.Second}}
	state := jobSchedule{"state", scheduler.Schedule{Interval: 10 * time.Second, Timeout: 5 * time.Second}}

	tests := []struct {
		name        string
		mountPoints []string
		units       []string
		want        []jobSchedule
	}{
		{"default mount point", []string{"/"}, []string{"nginx.service"}, []jobSchedule{self, system, processes, connections, systemd, filesystem, clock, kernel, logs, backups, state}},
		{"two mount points", []string{"/", "/var"}, []string{"nginx.service"}, []jobSchedule{self, system, processes, connections, systemd, filesystem, clock, kernel, logs, backups, state}},
		{"no mount point leaves out the filesystem job", []string{}, []string{"nginx.service"}, []jobSchedule{self, system, processes, connections, systemd, clock, kernel, logs, backups, state}},
		{"no service unit leaves out the systemd job", []string{"/"}, []string{}, []jobSchedule{self, system, processes, connections, filesystem, clock, kernel, logs, backups, state}},
		{"no mount point and no service unit", []string{}, []string{}, []jobSchedule{self, system, processes, connections, clock, kernel, logs, backups, state}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg := config.DefaultAgent()
			cfg.Filesystem.MountPoints = tt.mountPoints
			cfg.Services.Units = tt.units
			cfg.MariaDB.Enabled = false
			log := slog.New(slog.NewTextHandler(io.Discard, nil))

			list, _, err := jobs(Options{Self: fixtureSources(), System: fixtureSystem(), Processes: fixtureProcesses(), Connections: fixtureConnections(), Services: fixtureServices(), MariaDB: fixtureMariaDB(), Kernel: fixtureKernel(), Logs: fixtureLogs(), StateDir: t.TempDir()}, &cfg, storeFor(Options{Self: fixtureSources(), System: fixtureSystem(), Processes: fixtureProcesses(), Connections: fixtureConnections(), Services: fixtureServices(), MariaDB: fixtureMariaDB(), Kernel: fixtureKernel(), Logs: fixtureLogs(), StateDir: t.TempDir()}, &cfg), log)

			if err != nil {

				t.Fatalf("jobs() error = %v, want nil", err)

			}

			if got := scheduleOf(list); !slices.Equal(got, tt.want) {
				t.Errorf("jobs() = %+v, want %+v", got, tt.want)
			}
			if _, err := scheduler.New(list, scheduler.DiscardSink{}, log); err != nil {
				t.Errorf("scheduler.New(jobs()) error = %v, want nil", err)
			}
		})
	}
}

// jobNames returns the collector names of list in order.
func jobNames(list []scheduler.Job) []string {
	var got []string
	for _, j := range list {
		got = append(got, j.Collector.Name())
	}
	return got
}

// requireMariaDBSchedule fails when the mariadb job of list does not run on the MariaDB intervals.
func requireMariaDBSchedule(t *testing.T, list []scheduler.Job) {
	t.Helper()
	want := scheduler.Schedule{Interval: MariaDBInterval, Timeout: MariaDBTimeout, PressureInterval: MariaDBPressureInterval}
	for _, j := range list {
		if j.Collector.Name() == "mariadb" && j.Schedule != want {
			t.Errorf("mariadb schedule = %+v, want %+v", j.Schedule, want)
		}
	}
}

func TestJobs_MariaDBAfterSystemdOnlyWhenEnabled(t *testing.T) {
	tests := []struct {
		name    string
		enabled bool
		units   []string
		want    []string
	}{
		{"enabled follows systemd", true, []string{"nginx.service"}, []string{"self", "system", "processes", "connections", "systemd", "mariadb", "filesystem", "clock", "kernel", "logs", "backups", "state"}},
		{"enabled without systemd follows connections", true, []string{}, []string{"self", "system", "processes", "connections", "mariadb", "filesystem", "clock", "kernel", "logs", "backups", "state"}},
		{"disabled leaves the job out", false, []string{"nginx.service"}, []string{"self", "system", "processes", "connections", "systemd", "filesystem", "clock", "kernel", "logs", "backups", "state"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg := config.DefaultAgent()
			cfg.MariaDB.Enabled = tt.enabled
			cfg.Services.Units = tt.units
			log := slog.New(slog.NewTextHandler(io.Discard, nil))

			list, _, err := jobs(Options{Self: fixtureSources(), System: fixtureSystem(), Processes: fixtureProcesses(), Connections: fixtureConnections(), Services: fixtureServices(), MariaDB: fixtureMariaDB(), Kernel: fixtureKernel(), Logs: fixtureLogs(), StateDir: t.TempDir()}, &cfg, storeFor(Options{Self: fixtureSources(), System: fixtureSystem(), Processes: fixtureProcesses(), Connections: fixtureConnections(), Services: fixtureServices(), MariaDB: fixtureMariaDB(), Kernel: fixtureKernel(), Logs: fixtureLogs(), StateDir: t.TempDir()}, &cfg), log)

			if err != nil {

				t.Fatalf("jobs() error = %v, want nil", err)

			}

			if got := jobNames(list); !slices.Equal(got, tt.want) {
				t.Errorf("job names = %v, want %v", got, tt.want)
			}
			if tt.enabled {
				requireMariaDBSchedule(t, list)
			}
		})
	}
}

func TestRun_RecordsSystemMetrics(t *testing.T) {
	sink := newMetricSink()
	r := startAgent(t, Options{
		ConfigPath:  writeConfig(t, validConfig),
		Environ:     []string{"VANDOX_AGENT_TOKEN=" + testToken},
		Stderr:      io.Discard,
		Sink:        sink,
		Self:        fixtureSources(),
		System:      fixtureSystem(),
		Processes:   fixtureProcesses(),
		Connections: fixtureConnections(),
		Services:    fixtureServices(),
		MariaDB:     fixtureMariaDB(),
		Kernel:      fixtureKernel(),
		Logs:        fixtureLogs(),
		StateDir:    t.TempDir(),
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
		// The in-memory /proc holds the captured socket tables (net/ is in the fixture tree); the socket mount is the captured sockets'.
		Connections: connstats.Sources{Proc: proc, SocketMount: connstats.SocketMount{ID: fixtureSocketMount}},
		Services:    fixtureServices(),
		MariaDB:     fixtureMariaDB(),
		Kernel:      fixtureKernel(),
		Logs:        fixtureLogs(),
		StateDir:    t.TempDir(),
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
		sched, err := newScheduler(opts, &cfg, storeFor(opts, &cfg), sinkFor(opts), slog.New(slog.DiscardHandler))
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

func TestConnectionSources_ProbesOnlyWhenUnset(t *testing.T) {
	probeErr := errors.New("sockmount: socket: permission denied")
	proc := fstest.MapFS{"net/tcp": &fstest.MapFile{Data: []byte("header\n")}}
	tests := []struct {
		name      string
		src       connstats.SocketMount
		id        int32
		err       error
		wantMount connstats.SocketMount
		wantCalls int
	}{
		{"unset takes the probed mount", connstats.SocketMount{}, 17, nil, connstats.SocketMount{ID: 17}, 1},
		{"unset takes the probe's error", connstats.SocketMount{}, 0, probeErr, connstats.SocketMount{Err: probeErr}, 1},
		{"a given mount is kept", connstats.SocketMount{ID: fixtureSocketMount}, 17, nil, connstats.SocketMount{ID: fixtureSocketMount}, 0},
		{"a given error is kept", connstats.SocketMount{Err: probeErr}, 17, nil, connstats.SocketMount{Err: probeErr}, 0},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			calls := 0
			probe := func() (int32, error) {
				calls++
				return tt.id, tt.err
			}

			got := connectionSources(connstats.Sources{Proc: proc, SocketMount: tt.src}, probe)

			if calls != tt.wantCalls {
				t.Errorf("connectionSources() ran the probe %d times, want %d", calls, tt.wantCalls)
			}
			if got.SocketMount.ID != tt.wantMount.ID || !errors.Is(got.SocketMount.Err, tt.wantMount.Err) || (tt.wantMount.Err == nil) != (got.SocketMount.Err == nil) {
				t.Errorf("connectionSources() socket mount = %+v, want %+v", got.SocketMount, tt.wantMount)
			}
			if got.Proc == nil {
				t.Error("connectionSources() Proc = nil, want the given file system kept")
			}
		})
	}
}

// recordSink is a scheduler.Putter that keeps the records it received.
type recordSink struct {
	mu      sync.Mutex
	records []model.Record
}

func (s *recordSink) Put(_ context.Context, records []model.Record) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.records = append(s.records, records...)
	return nil
}

func (s *recordSink) all() []model.Record {
	s.mu.Lock()
	defer s.mu.Unlock()

	return slices.Clone(s.records)
}

// serviceUnits returns the units of the service_state records, and fails for a record whose source is not systemd.
func serviceUnits(t *testing.T, records []model.Record) []string {
	t.Helper()

	var units []string
	for _, r := range records {
		state, ok := r.Data.(*model.ServiceState)
		if !ok {
			continue
		}
		if r.Source != "systemd" {
			t.Errorf("Source of a service_state record = %q, want %q", r.Source, "systemd")
		}
		units = append(units, state.Unit)
	}
	return units
}

func TestNewScheduler_RecordsServiceStatesOfConfiguredUnits(t *testing.T) {
	cfg := config.DefaultAgent()
	cfg.Services.Units = []string{"nginx.service", "postfix.service"}
	fake := &fakeSystemd{}
	opts := lowMemoryOptions(t, 0)
	opts.Services = svcstats.Sources{Systemd: fake}

	synctest.Test(t, func(t *testing.T) {
		sink := &recordSink{}
		opts.Sink = sink
		sched, err := newScheduler(opts, &cfg, storeFor(opts, &cfg), sinkFor(opts), slog.New(slog.DiscardHandler))
		if err != nil {
			t.Fatalf("newScheduler() error = %v, want nil", err)
		}

		runFor(t, sched, 5*time.Second)

		units := serviceUnits(t, sink.all())
		if want := []string{"nginx.service", "postfix.service"}; !slices.Equal(units, want) {
			t.Errorf("units of the service_state records = %v, want %v", units, want)
		}
		if reads := fake.readNames(); len(reads) == 0 || !slices.Equal(reads[0], []string{"nginx.service", "postfix.service"}) {
			t.Errorf("units read from systemd = %v, want the configured units first", reads)
		}
	})
}

func TestRun_ServiceUnitsFromConfigurationFile(t *testing.T) {
	fake := &fakeSystemd{}
	sink := &recordSink{}
	r := startAgent(t, Options{
		ConfigPath:  writeConfig(t, validConfig+"services:\n  units:\n    - nginx\n"),
		Environ:     []string{"VANDOX_AGENT_TOKEN=" + testToken},
		Stderr:      io.Discard,
		Sink:        sink,
		Self:        fixtureSources(),
		System:      fixtureSystem(),
		Processes:   fixtureProcesses(),
		Connections: fixtureConnections(),
		Services:    svcstats.Sources{Systemd: fake},
		MariaDB:     fixtureMariaDB(),
		Kernel:      fixtureKernel(),
		Logs:        fixtureLogs(),
		StateDir:    t.TempDir(),
	})

	limit := time.After(waitLimit)
	for len(fake.readNames()) == 0 {
		select {
		case <-r.done:
			t.Fatalf("Run returned %d before systemd was read", r.code)
		case <-limit:
			t.Fatalf("systemd was not read within %v", waitLimit)
		case <-time.After(5 * time.Millisecond):
		}
	}

	if code := r.stop(); code != 0 {
		t.Errorf("Run() = %d after the context was cancelled, want 0", code)
	}
	if got := fake.readNames()[0]; !slices.Equal(got, []string{"nginx.service"}) {
		t.Errorf("units read from systemd = %v, want [nginx.service] (the entry nginx with the suffix added)", got)
	}
}

func TestNewScheduler_EmptyServiceUnitsNeverOpenSystemd(t *testing.T) {
	cfg := config.DefaultAgent()
	cfg.Services.Units = []string{}
	fake := &fakeSystemd{}
	opts := lowMemoryOptions(t, 0)
	opts.Services = svcstats.Sources{Systemd: fake}

	synctest.Test(t, func(t *testing.T) {
		sink := &recordSink{}
		opts.Sink = sink
		sched, err := newScheduler(opts, &cfg, storeFor(opts, &cfg), sinkFor(opts), slog.New(slog.DiscardHandler))
		if err != nil {
			t.Fatalf("newScheduler() error = %v, want nil", err)
		}

		runFor(t, sched, 2*SystemdInterval)

		fake.mu.Lock()
		opens := fake.opens
		fake.mu.Unlock()
		if opens != 0 {
			t.Errorf("systemd was opened %d times with services.units empty, want 0", opens)
		}
		if units := serviceUnits(t, sink.all()); len(units) != 0 {
			t.Errorf("service_state records for %v with services.units empty, want none", units)
		}
		if len(sink.all()) == 0 {
			t.Errorf("no record at all, want the other collectors to run")
		}
	})
}

// syncBuffer is a log destination several goroutines write to.
type syncBuffer struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (b *syncBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()

	return b.buf.Write(p)
}

func (b *syncBuffer) String() string {
	b.mu.Lock()
	defer b.mu.Unlock()

	return b.buf.String()
}

func TestJobs_BackupsOnlyWithRules(t *testing.T) {
	tests := []struct {
		name  string
		rules []config.BackupRule
		want  []string
	}{
		{"the default rule", config.DefaultAgent().Backups.Processes, []string{"clock", "kernel", "logs", "backups", "state"}},
		{"a command rule", []config.BackupRule{{Command: "borg"}}, []string{"clock", "kernel", "logs", "backups", "state"}},
		{"a cmdline prefix rule", []config.BackupRule{{CmdlinePrefix: "/usr/bin/restic "}}, []string{"clock", "kernel", "logs", "backups", "state"}},
		{"no rule leaves out the backups job", []config.BackupRule{}, []string{"clock", "kernel", "logs", "state"}},
		{"nil rules leave out the backups job", nil, []string{"clock", "kernel", "logs", "state"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg := config.DefaultAgent()
			cfg.Backups.Processes = tt.rules
			log := slog.New(slog.NewTextHandler(io.Discard, nil))

			list, _, err := jobs(Options{Self: fixtureSources(), System: fixtureSystem(), Processes: fixtureProcesses(), Connections: fixtureConnections(), Services: fixtureServices(), MariaDB: fixtureMariaDB(), Kernel: fixtureKernel(), Logs: fixtureLogs(), StateDir: t.TempDir()}, &cfg, storeFor(Options{Self: fixtureSources(), System: fixtureSystem(), Processes: fixtureProcesses(), Connections: fixtureConnections(), Services: fixtureServices(), MariaDB: fixtureMariaDB(), Kernel: fixtureKernel(), Logs: fixtureLogs(), StateDir: t.TempDir()}, &cfg), log)

			if err != nil {

				t.Fatalf("jobs() error = %v, want nil", err)

			}

			names := jobNames(list)
			if len(names) < len(tt.want) || !slices.Equal(names[len(names)-len(tt.want):], tt.want) {
				t.Errorf("job names = %v, want them to end with %v", names, tt.want)
			}
			if _, err := scheduler.New(list, scheduler.DiscardSink{}, log); err != nil {
				t.Errorf("scheduler.New(jobs()) error = %v, want nil", err)
			}
		})
	}
}

func TestNewScheduler_StateFileIsKeptInTheSpoolDirectoryOrStateDir(t *testing.T) {
	tests := []struct {
		name     string
		useState bool
	}{
		{"spool.directory when StateDir is empty", false},
		{"StateDir when it is set", true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			spool, other := t.TempDir(), t.TempDir()
			cfg := config.DefaultAgent()
			cfg.Spool.Directory = spool
			want, unwanted := spool, other
			opts := lowMemoryOptions(t, 0)
			opts.StateDir = ""
			if tt.useState {
				opts.StateDir = other
				want, unwanted = other, spool
			}
			opts.Kernel = newKernelWorld().sources()
			opts.Logs = fixtureLogs()

			runSchedulerFor(t, opts, &cfg, 25*time.Second)

			requireStateFile(t, want, unwanted)
		})
	}
}

// runSchedulerFor builds the scheduler of opts and cfg and runs it for d of fake time.
func runSchedulerFor(t *testing.T, opts Options, cfg *config.Agent, d time.Duration) {
	t.Helper()

	synctest.Test(t, func(t *testing.T) {
		sched, err := newScheduler(opts, cfg, storeFor(opts, cfg), sinkFor(opts), slog.New(slog.DiscardHandler))
		if err != nil {
			t.Fatalf("newScheduler() error = %v, want nil", err)
		}

		runFor(t, sched, d)
	})
}

// requireStateFile fails unless dir holds a state file with the boot of the kernel world and other holds none.
func requireStateFile(t *testing.T, dir, other string) {
	t.Helper()

	data, err := os.ReadFile(filepath.Join(dir, "agent-state.json"))
	if err != nil {
		t.Fatalf("reading the state file: %v, want the state job to have saved the boot there", err)
	}
	if !strings.Contains(string(data), kernelBootRaw) {
		t.Errorf("state file = %s, want it to hold the boot ID %s", data, kernelBootRaw)
	}
	if _, err := os.Stat(filepath.Join(other, "agent-state.json")); !errors.Is(err, fs.ErrNotExist) {
		t.Errorf("state file in the other directory: Stat error = %v, want %v", err, fs.ErrNotExist)
	}
}

func TestNewScheduler_MissingSpoolDirectoryIsNeverCreated(t *testing.T) {
	missing := filepath.Join(t.TempDir(), "missing-spool")
	cfg := config.DefaultAgent()
	cfg.Spool.Directory = missing
	opts := lowMemoryOptions(t, 0)
	opts.StateDir = ""
	out := &syncBuffer{}
	sink := &recordSink{}
	opts.Sink = sink

	synctest.Test(t, func(t *testing.T) {
		logger := slog.New(slog.NewTextHandler(out, nil))
		store, _, _ := openStorage(opts, &cfg, logger)
		sched, err := newScheduler(opts, &cfg, store, sinkFor(opts), logger)
		if err != nil {
			t.Fatalf("newScheduler() error = %v, want nil (a missing spool directory does not stop the agent)", err)
		}

		runFor(t, sched, 65*time.Second)
	})

	if _, err := os.Stat(missing); !errors.Is(err, fs.ErrNotExist) {
		t.Errorf("Stat(%s) error = %v, want %v (the agent never creates the directory)", "spool.directory", err, fs.ErrNotExist)
	}
	if n := countLines(out.String(), `msg="agent state not saved"`); n != 1 {
		t.Errorf("lines %q = %d, want 1 (logged once until a save succeeds); log:\n%s", "agent state not saved", n, out.String())
	}
	if strings.Contains(out.String(), missing) {
		t.Errorf("the log holds the spool directory path, want no path; log:\n%s", out.String())
	}
	if len(sink.all()) == 0 {
		t.Errorf("no record at all, want the collectors to run beside the failing state")
	}
}

func TestNewScheduler_LiveKillTriggersExtraSnapshots(t *testing.T) {
	cfg := config.DefaultAgent()
	world := newKernelWorld()
	sink := &recordSink{}
	opts := Options{
		Sink:        sink,
		Self:        fixtureSources(),
		System:      fixtureSystem(),
		Processes:   fixtureProcesses(),
		Connections: fixtureConnections(),
		Services:    fixtureServices(),
		MariaDB:     fixtureMariaDB(),
		Kernel:      world.sources(),
		Logs:        fixtureLogs(),
		StateDir:    t.TempDir(),
	}
	const victim = 4711

	synctest.Test(t, func(t *testing.T) {
		sched, err := newScheduler(opts, &cfg, storeFor(opts, &cfg), sinkFor(opts), slog.New(slog.DiscardHandler))
		if err != nil {
			t.Fatalf("newScheduler() error = %v, want nil", err)
		}
		runWithKill(t, sched, world, victim)
	})

	records := sink.all()
	if n := countKills(records, victim); n != 1 {
		t.Fatalf("oom_kill events of PID %d = %d, want 1 (the kernel job read the journal)", victim, n)
	}
	processes, connections := countTriggered(t, records, victim)
	if processes != 1 || connections != 1 {
		t.Errorf("triggered snapshots = %d process and %d connection, want 1 and 1 within seconds of the kill (the regular ones are 60 s apart)", processes, connections)
	}
}

// runWithKill runs sched inside a synctest bubble: the journal of world holds three lines at the first start, and a kill of the
// victim is added three seconds later. Second s of the timeline is origin + s; the first run is at second 5.
func runWithKill(t *testing.T, sched *scheduler.Scheduler, world *kernelWorld, victim int) {
	t.Helper()

	killLine := fmt.Sprintf("Out of memory: Killed process %d (pzstd) total-vm:1000kB, anon-rss:100kB, file-rss:0kB, shmem-rss:0kB, UID:0 pgtables:4kB oom_score_adj:0", victim)
	origin := time.Now().Add(-5 * time.Second)
	world.setUptime(kernelMono/1e6 + 5)
	world.addJournalFile(t, []map[string][]byte{
		kernelLine(2, origin, 1, 300_000, "handmade kernel line 2"),
		kernelLine(4, origin, 2, 300_000, "handmade kernel line 4"),
		kernelLine(6, origin, 3, 300_000, "handmade kernel line 6"),
	})
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		sched.Run(ctx)
	}()

	time.Sleep(3 * time.Second)
	synctest.Wait()
	world.setUptime(kernelMono/1e6 + 8)
	world.addJournalFile(t, []map[string][]byte{kernelLine(8, origin, 7, 300_000, killLine)})
	time.Sleep(4 * time.Second)
	synctest.Wait()
	cancel()
	<-done
}

// countTriggered returns the number of triggered process and connection snapshots in records; every triggered snapshot must name
// the victim's kill.
func countTriggered(t *testing.T, records []model.Record, victim int32) (processes, connections int) {
	t.Helper()

	for _, r := range records {
		switch d := r.Data.(type) {
		case *model.ProcessSnapshot:
			if requireTriggerNames(t, "process", d.Trigger, victim) {
				processes++
				if !d.Complete {
					t.Errorf("triggered process snapshot has Complete = false, want a full snapshot")
				}
			}
		case *model.ConnectionSnapshot:
			if requireTriggerNames(t, "connection", d.Trigger, victim) {
				connections++
			}
		}
	}
	return processes, connections
}

// countKills returns the number of oom_kill events of the victim in records.
func countKills(records []model.Record, victim int32) int {
	n := 0
	for _, r := range records {
		if ev, ok := r.Data.(*model.KernelEvent); ok && ev.Type == model.KernelEventOOMKill && ev.OOMKill != nil && ev.OOMKill.VictimPID == victim {
			n++
		}
	}
	return n
}

// requireTriggerNames reports whether trigger is set; a set trigger must name exactly the kill of the victim, in the boot of the world.
func requireTriggerNames(t *testing.T, what string, trigger *model.SnapshotTrigger, victim int32) bool {
	t.Helper()

	if trigger == nil {
		return false
	}
	if len(trigger.Kills) != 1 || trigger.Kills[0].VictimPID != victim || trigger.Kills[0].BootID != kernelBootRaw || trigger.KillsOmitted != 0 {
		t.Errorf("trigger of the %s snapshot = %+v, want the one kill of PID %d in boot %s", what, trigger, victim, kernelBootRaw)
	}
	return true
}

func TestJobs_ZeroKernelSourcesStillBuildAllJobs(t *testing.T) {
	cfg := config.DefaultAgent()
	log := slog.New(slog.NewTextHandler(io.Discard, nil))

	list, _, err := jobs(Options{Self: fixtureSources(), System: fixtureSystem(), Processes: fixtureProcesses(), Connections: fixtureConnections(), Services: fixtureServices(), MariaDB: fixtureMariaDB(), StateDir: t.TempDir()}, &cfg, storeFor(Options{Self: fixtureSources(), System: fixtureSystem(), Processes: fixtureProcesses(), Connections: fixtureConnections(), Services: fixtureServices(), MariaDB: fixtureMariaDB(), StateDir: t.TempDir()}, &cfg), log)

	if err != nil {

		t.Fatalf("jobs() error = %v, want nil", err)

	}

	names := jobNames(list)
	if want := []string{"kernel", "logs", "backups", "state"}; len(names) < 4 || !slices.Equal(names[len(names)-4:], want) {
		t.Errorf("job names = %v, want them to end with %v with zero kernel sources", names, want)
	}
}

func TestNewScheduler_BackupsAndStateReadTheKernelProc(t *testing.T) {
	cfg := config.DefaultAgent()
	world := newKernelWorld()
	world.proc.set("1234/stat", []byte("1234 (pzstd) S "+strings.Repeat("0 ", 49)+"\n"))
	world.proc.set("1234/cmdline", []byte("/usr/bin/pzstd\x00-p\x004\x00"))
	sink := &recordSink{}
	opts := lowMemoryOptions(t, 0)
	opts.Sink = sink
	opts.Kernel = world.sources()
	opts.Logs = fixtureLogs()

	runSchedulerFor(t, opts, &cfg, 25*time.Second)

	starts := 0
	for _, r := range sink.all() {
		if run, ok := r.Data.(*model.BackupRun); ok && r.Source == "backups" && run.Phase == "start" {
			starts++
		}
	}
	if starts != 1 {
		t.Errorf("backup_run start records = %d, want 1 (the backups job reads the process of Kernel.Proc, not Processes.Proc)", starts)
	}
}

// logSources returns the log shipping sources that read the journal and /etc of the world.
func (w *kernelWorld) logSources() logship.Sources {
	return logship.Sources{Etc: w.etc, Journal: []fs.FS{w.journal}, Open: func(string) (*os.File, error) { return nil, os.ErrNotExist }}
}

func TestRun_SavesTheStateWhenItStops(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		world := newKernelWorld()
		origin := time.Now().Add(-time.Hour)
		world.addJournalFile(t, []map[string][]byte{kernelLine(1, origin, 10, 0, "first"), kernelLine(2, origin, 20, 0, "second")})
		opts := lowMemoryOptions(t, 0)
		opts.ConfigPath = writeConfig(t, validConfig)
		opts.Environ = []string{"VANDOX_AGENT_TOKEN=" + testToken}
		opts.Kernel = world.sources()
		opts.Logs = world.logSources()
		sink := &recordSink{}
		opts.Sink = sink
		ctx, cancel := context.WithCancel(context.Background())
		done := make(chan int, 1)
		go func() { done <- Run(ctx, opts) }()

		// the first run of the log shipping collector starts at the end of the journal; two lines arrive and are shipped by the run at 2 s
		time.Sleep(time.Second)
		synctest.Wait()
		world.addJournalFile(t, []map[string][]byte{kernelLine(3, origin, 30, 0, "third"), kernelLine(4, origin, 40, 0, "fourth")})
		time.Sleep(4 * time.Second)
		synctest.Wait()
		cancel()
		code := <-done

		if code != 0 {
			t.Fatalf("Run() = %d after the cancel, want 0", code)
		}
		var shipped []string
		for _, r := range sink.all() {
			if l, ok := r.Data.(*model.LogLine); ok && l.Log == "journal" {
				shipped = append(shipped, l.Message)
			}
		}
		if !slices.Equal(shipped, []string{"third", "fourth"}) {
			t.Fatalf("log lines shipped = %v, want [third fourth] (nothing older than the start)", shipped)
		}
		// the state job saved the state at the start and would save again after 10 s: only the save at the stop holds the last position
		store, status := agentstate.Open(opts.StateDir, slog.New(slog.DiscardHandler))
		if status != agentstate.StatusLoaded {
			t.Fatalf("state file status = %v, want StatusLoaded after the stop", status)
		}
		logs := store.State().Logs
		if logs == nil || len(logs.Journal) != 1 || logs.Journal[0].Seqnum != 4 {
			t.Errorf("saved log positions = %+v, want the one journal position at the sequence number 4: the agent saves the state when it stops", logs)
		}
	})
}

func TestLogshipConfig_MapsEveryOption(t *testing.T) {
	t.Run("the default options", func(t *testing.T) {
		opts := config.DefaultAgent().LogShipping

		got, err := logshipConfig(opts)

		if err != nil {
			t.Fatalf("logshipConfig(defaults) error = %v, want nil", err)
		}
		want, _ := config.PriorityLevel(opts.MinPriority)
		if got.Journal != opts.Journal || got.MinPriority != want || len(got.Files) != len(opts.Files) || len(got.Drop) != len(opts.Drop) {
			t.Errorf("logshipConfig(defaults) = %+v, want journal %v, min priority %d, %d files and %d rules", got, opts.Journal, want, len(opts.Files), len(opts.Drop))
		}
	})
	t.Run("journal off, a priority and files", func(t *testing.T) {
		opts := config.LogShipping{
			Journal:     false,
			MinPriority: "warning",
			Files:       []config.LogFile{{Path: "/var/log/mail.log", Format: "syslog"}, {Path: "/var/log/auth.log", Format: "syslog"}},
		}

		got, err := logshipConfig(opts)

		if err != nil {
			t.Fatalf("logshipConfig() error = %v, want nil", err)
		}
		if got.Journal || got.MinPriority != 4 {
			t.Errorf("logshipConfig() journal %v, min priority %d, want false and 4 (warning)", got.Journal, got.MinPriority)
		}
		wantFiles := []logship.FileSpec{{Path: "/var/log/mail.log", Format: "syslog"}, {Path: "/var/log/auth.log", Format: "syslog"}}
		if !slices.Equal(got.Files, wantFiles) {
			t.Errorf("logshipConfig() files = %+v, want %+v", got.Files, wantFiles)
		}
	})
}

// dropRule is what a rule of the collector is expected to hold; a nil priority or pattern is expected to be unset.
type dropRule struct {
	log, program string
	priority     *uint8
	pattern      string
	matches      []string
	misses       []string
}

func levelOf(n uint8) *uint8 { return &n }

func TestLogshipConfig_MapsEveryDropCondition(t *testing.T) {
	opts := config.LogShipping{
		Journal:     true,
		MinPriority: "info",
		Drop: []config.DropRule{
			{Log: "journal", Program: "cron", Priority: "debug", Message: `^session (opened|closed) for user \w+`},
			{Log: "/var/log/mail.log"},
			{Program: "noisy"},
			{Priority: "info"},
			{Message: "health check"},
		},
	}
	want := []dropRule{
		{log: "journal", program: "cron", priority: levelOf(7), pattern: `^session (opened|closed) for user \w+`,
			matches: []string{"session opened for user root"}, misses: []string{"a session opened for user root"}},
		{log: "/var/log/mail.log"},
		{program: "noisy"},
		{priority: levelOf(6)},
		{pattern: "health check", matches: []string{"a health check ran"}, misses: []string{"a health ran"}},
	}

	got, err := logshipConfig(opts)

	if err != nil {
		t.Fatalf("logshipConfig() error = %v, want nil", err)
	}
	if len(got.Drop) != len(want) {
		t.Fatalf("logshipConfig() holds %d rules, want %d", len(got.Drop), len(want))
	}
	for i, w := range want {
		requireDropRule(t, i, got.Drop[i], w)
	}
}

// requireDropRule fails the test when the rule i of the collector is not the expected one.
func requireDropRule(t *testing.T, i int, got logship.Rule, want dropRule) {
	t.Helper()
	if got.Log != want.log || got.Program != want.program {
		t.Errorf("rule %d = log %q program %q, want log %q program %q", i, got.Log, got.Program, want.log, want.program)
	}
	if (got.Priority == nil) != (want.priority == nil) || (want.priority != nil && *got.Priority != *want.priority) {
		t.Errorf("rule %d priority = %v, want %v", i, got.Priority, want.priority)
	}
	if (got.Message == nil) != (want.pattern == "") {
		t.Fatalf("rule %d pattern = %v, want %q", i, got.Message, want.pattern)
	}
	if got.Message == nil {
		return
	}
	if got.Message.String() != want.pattern {
		t.Errorf("rule %d pattern = %q, want %q", i, got.Message, want.pattern)
	}
	for _, line := range want.matches {
		if !got.Message.MatchString(line) {
			t.Errorf("rule %d pattern %q does not match %q, want a match", i, got.Message, line)
		}
	}
	for _, line := range want.misses {
		if got.Message.MatchString(line) {
			t.Errorf("rule %d pattern %q matches %q, want no match", i, got.Message, line)
		}
	}
}

func TestLogshipConfig_PatternThatDoesNotCompileIsRefusedWithoutQuotingIt(t *testing.T) {
	const pattern = "(unclosed-pattern-text"
	opts := config.LogShipping{Journal: true, MinPriority: "info", Drop: []config.DropRule{{Program: "cron"}, {Message: pattern}}}

	t.Run("the conversion", func(t *testing.T) {
		_, err := logshipConfig(opts)

		if !errors.Is(err, errDropPattern) || strings.Contains(err.Error(), pattern) {
			t.Errorf("logshipConfig() error = %v, want %v without the pattern", err, errDropPattern)
		}
	})
	t.Run("the collector", func(t *testing.T) {
		logs, err := newLogs(fixtureLogs(), opts, nil, slog.New(slog.DiscardHandler))

		if !errors.Is(err, errDropPattern) || logs != nil {
			t.Errorf("newLogs() = %v, %v, want no collector and %v", logs, err, errDropPattern)
		}
	})
	t.Run("the agent is not built and says why", func(t *testing.T) {
		cfg := config.DefaultAgent()
		cfg.MariaDB.Enabled = false
		cfg.LogShipping = opts
		options := Options{Self: fixtureSources(), System: fixtureSystem(), Processes: fixtureProcesses(), Connections: fixtureConnections(), Services: fixtureServices(), MariaDB: fixtureMariaDB(), Kernel: fixtureKernel(), Logs: fixtureLogs(), StateDir: t.TempDir()}
		log := slog.New(slog.DiscardHandler)

		list, _, err := jobs(options, &cfg, storeFor(options, &cfg), log)

		if !errors.Is(err, errDropPattern) || list != nil || strings.Contains(err.Error(), pattern) {
			t.Errorf("jobs() = %d jobs, error %v, want no jobs and %v without the pattern", len(list), err, errDropPattern)
		}
		sched, err := newScheduler(options, &cfg, storeFor(options, &cfg), sinkFor(options), log)
		if !errors.Is(err, errDropPattern) || sched != nil || strings.Contains(err.Error(), pattern) {
			t.Errorf("newScheduler() = %v, error %v, want no scheduler and %v without the pattern", sched, err, errDropPattern)
		}
	})
}

// storeFor opens the state store the way Run does, for the tests that call jobs and newScheduler directly.
func storeFor(opts Options, cfg *config.Agent) *agentstate.Store {
	store, _, _ := openStorage(opts, cfg, slog.New(slog.DiscardHandler))
	return store
}

// sinkFor returns the sink Run hands the scheduler: opts.Sink or else the discarding one.
func sinkFor(opts Options) scheduler.Putter {
	if opts.Sink != nil {
		return opts.Sink
	}
	return scheduler.DiscardSink{}
}
