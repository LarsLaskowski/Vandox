package main

import (
	"bytes"
	"context"
	"errors"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/config"
)

// guard bounds every wait on the service, so a hanging test fails instead of blocking; it is never asserted.
const guard = 30 * time.Second

// syncBuffer is a bytes.Buffer that run may write while the test reads it.
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

// recordingListener wraps a listener and reports its Close.
type recordingListener struct {
	net.Listener
	once   sync.Once
	closed chan struct{}
}

func (l *recordingListener) Close() error {
	l.once.Do(func() { close(l.closed) })
	return l.Listener.Close()
}

func (l *recordingListener) isClosed() bool {
	select {
	case <-l.closed:
		return true
	default:
		return false
	}
}

// fakeListen hands out pre-opened loopback listeners and records the addresses it was asked for. A call
// whose index is in fail returns an error instead of a listener.
type fakeListen struct {
	t         *testing.T
	mu        sync.Mutex
	asked     [][2]string
	listeners []*recordingListener
	fail      map[int]bool
	opened    chan struct{} // receives one value per listener handed out
}

func newFakeListen(t *testing.T, failCalls ...int) *fakeListen {
	t.Helper()
	f := &fakeListen{t: t, fail: map[int]bool{}, opened: make(chan struct{}, 8)}
	for _, n := range failCalls {
		f.fail[n] = true
	}
	return f
}

func (f *fakeListen) listen(ctx context.Context, network, address string) (net.Listener, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	index := len(f.asked)
	f.asked = append(f.asked, [2]string{network, address})
	if f.fail[index] {
		return nil, errors.New("listen refused")
	}
	l, err := (&net.ListenConfig{}).Listen(ctx, "tcp", "127.0.0.1:0")
	if err != nil {
		f.t.Fatalf("opening a loopback listener: %v", err)
	}
	rl := &recordingListener{Listener: l, closed: make(chan struct{})}
	f.listeners = append(f.listeners, rl)
	f.t.Cleanup(func() { _ = rl.Close() })
	f.opened <- struct{}{}
	return rl, nil
}

func (f *fakeListen) calls() [][2]string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([][2]string(nil), f.asked...)
}

func (f *fakeListen) listener(i int) *recordingListener {
	f.mu.Lock()
	defer f.mu.Unlock()
	if i >= len(f.listeners) {
		f.t.Fatalf("listener %d was never opened (%d opened)", i, len(f.listeners))
	}
	return f.listeners[i]
}

// serviceConfig describes a vandoxd.yaml for the serve tests.
type serviceConfig struct {
	web, ingest, dir, level string
}

func newServiceConfig(t *testing.T) serviceConfig {
	t.Helper()
	return serviceConfig{web: "127.0.0.1:18080", ingest: "127.0.0.1:18081", dir: t.TempDir(), level: "info"}
}

func (c serviceConfig) write(t *testing.T) string {
	t.Helper()
	content := "web:\n  listen: \"" + c.web + "\"\ningest:\n  listen: \"" + c.ingest + "\"\nstorage:\n  directory: " + c.dir + "\nlog:\n  level: " + c.level + "\n"
	path := filepath.Join(t.TempDir(), "vandoxd.yaml")
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}

// service is a run call executing in a goroutine.
type service struct {
	t      *testing.T
	cancel context.CancelFunc
	stderr *syncBuffer
	listen *fakeListen
	exited chan struct{}
	code   int
}

// startService runs `vandoxd -config path` in a goroutine under ctx. The service is cancelled and awaited when
// the test ends.
func startService(ctx context.Context, t *testing.T, configPath string, environ []string, listen *fakeListen) *service {
	t.Helper()
	ctx, cancel := context.WithCancel(ctx)
	s := &service{t: t, cancel: cancel, stderr: &syncBuffer{}, listen: listen, exited: make(chan struct{})}
	go func() {
		defer close(s.exited)
		s.code = run(ctx, []string{"-config", configPath}, environ, io.Discard, s.stderr, listen.listen)
	}()
	t.Cleanup(func() {
		cancel()
		s.wait()
	})
	return s
}

// wait waits for run to return and returns its exit code.
func (s *service) wait() int {
	s.t.Helper()
	select {
	case <-s.exited:
	case <-time.After(guard):
		s.t.Fatal("run did not return")
	}
	return s.code
}

// get requests path from the i-th listener the service opened. It gives up when run has returned.
func (s *service) get(i int, path string) (status int, body string, err error) {
	s.t.Helper()
	ctx, cancel := context.WithTimeout(s.t.Context(), guard)
	defer cancel()
	go func() {
		select {
		case <-s.exited:
			cancel()
		case <-ctx.Done():
		}
	}()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, "http://"+s.awaitListener(i).Addr().String()+path, nil)
	if err != nil {
		s.t.Fatal(err)
	}
	client := &http.Client{Transport: &http.Transport{DisableKeepAlives: true}}
	resp, err := client.Do(req)
	if err != nil {
		return 0, "", err
	}
	defer func() { _ = resp.Body.Close() }()
	data, err := io.ReadAll(resp.Body)
	return resp.StatusCode, string(data), err
}

// awaitListener returns the i-th listener, failing when run exits before opening it.
func (s *service) awaitListener(i int) *recordingListener {
	s.t.Helper()
	for {
		s.listen.mu.Lock()
		n := len(s.listen.listeners)
		s.listen.mu.Unlock()
		if n > i {
			return s.listen.listener(i)
		}
		select {
		case <-s.listen.opened:
		case <-s.exited:
			s.t.Fatalf("run returned %d before opening listener %d; stderr: %s", s.code, i, s.stderr.String())
		}
	}
}

// waitHealthy waits until the web listener answers /healthz with 200.
func (s *service) waitHealthy() {
	s.t.Helper()
	status, body, err := s.get(0, "/healthz")
	if err != nil {
		s.t.Fatalf("GET web /healthz error = %v, want nil (stderr: %s)", err, s.stderr.String())
	}
	if status != http.StatusOK || body != "ok\n" {
		s.t.Fatalf("GET web /healthz = %d %q, want 200 \"ok\\n\"", status, body)
	}
}

// stop cancels the service and returns its exit code.
func (s *service) stop() int {
	s.t.Helper()
	s.cancel()
	return s.wait()
}

// runOnce runs `vandoxd -config path` to completion and returns the exit code and stderr.
func runOnce(t *testing.T, configPath string, environ []string, listen *fakeListen) (int, string) {
	t.Helper()
	var stderr syncBuffer
	code := run(t.Context(), []string{"-config", configPath}, environ, io.Discard, &stderr, listen.listen)
	return code, stderr.String()
}

// findLine returns the first log line whose msg is msg.
func findLine(t *testing.T, stderr, msg string) map[string]any {
	t.Helper()
	for _, line := range logLines(t, stderr) {
		if line["msg"] == msg {
			return line
		}
	}
	t.Fatalf("no log line with msg %q in %q", msg, stderr)
	return nil
}

func TestServe_ServesHealthzOnWebAndNothingOnIngest(t *testing.T) {
	cfg := newServiceConfig(t)
	listen := newFakeListen(t)
	s := startService(t.Context(), t, cfg.write(t), nil, listen)

	s.waitHealthy()
	ingestStatus, _, err := s.get(1, "/healthz")

	if err != nil {
		t.Fatalf("GET ingest /healthz error = %v, want nil", err)
	}
	if ingestStatus != http.StatusNotFound {
		t.Errorf("GET ingest /healthz status = %d, want 404", ingestStatus)
	}
	want := [][2]string{{"tcp", cfg.web}, {"tcp", cfg.ingest}}
	got := listen.calls()
	if len(got) != 2 || got[0] != want[0] || got[1] != want[1] {
		t.Errorf("listen calls = %v, want %v", got, want)
	}
}

func TestServe_StopsOnContextCancel(t *testing.T) {
	cfg := newServiceConfig(t)
	s := startService(t.Context(), t, cfg.write(t), nil, newFakeListen(t))
	s.waitHealthy()

	code := s.stop()

	if code != 0 {
		t.Errorf("run exit code after cancel = %d, want 0 (stderr: %s)", code, s.stderr.String())
	}
	findLine(t, s.stderr.String(), "vandoxd stopped")
	if _, err := os.Stat(filepath.Join(cfg.dir, "vandox.db")); err != nil {
		t.Errorf("Stat(vandox.db) error = %v, want the database file to exist", err)
	}
}

func TestServe_StopsOnSIGTERM(t *testing.T) {
	// Not parallel: the signal goes to the whole test process.
	cfg := newServiceConfig(t)
	s := startService(context.Background(), t, cfg.write(t), nil, newFakeListen(t))
	s.waitHealthy() // the signal is sent only once run has registered its handler and serves

	if err := syscall.Kill(os.Getpid(), syscall.SIGTERM); err != nil {
		t.Fatalf("Kill(SIGTERM) error = %v, want nil", err)
	}

	if code := s.wait(); code != 0 {
		t.Errorf("run exit code after SIGTERM = %d, want 0 (stderr: %s)", code, s.stderr.String())
	}
	findLine(t, s.stderr.String(), "vandoxd stopped")
}

func TestServe_ReopensDatabase(t *testing.T) {
	cfg := newServiceConfig(t)
	path := cfg.write(t)
	var created []any

	for range 2 {
		s := startService(t.Context(), t, path, nil, newFakeListen(t))
		s.waitHealthy()
		if code := s.stop(); code != 0 {
			t.Fatalf("run exit code = %d, want 0 (stderr: %s)", code, s.stderr.String())
		}
		created = append(created, findLine(t, s.stderr.String(), "database opened")["created"])
	}

	if created[0] != true || created[1] != false {
		t.Errorf("created attribute of the two runs = %v, want [true false]", created)
	}
}

// startupFailure prepares a configuration and environment for which run must fail.
type startupFailure struct {
	name     string
	listen   func(t *testing.T) *fakeListen
	prepare  func(cfg *serviceConfig) (environ []string)
	noFile   bool // the configuration file does not exist
	wantCall int  // number of listen calls expected
}

func startupFailures() []startupFailure {
	noFail := func(t *testing.T) *fakeListen { return newFakeListen(t) }
	return []startupFailure{
		{"configuration file missing", noFail, func(_ *serviceConfig) []string {
			return nil
		}, true, 0},
		{"configuration invalid", noFail, func(cfg *serviceConfig) []string {
			cfg.level = "verbose"
			return nil
		}, false, 0},
		{"unknown VANDOX variable", noFail, func(_ *serviceConfig) []string {
			return []string{"VANDOX_BOGUS=1"}
		}, false, 0},
		{"storage directory missing", noFail, func(cfg *serviceConfig) []string {
			cfg.dir = filepath.Join(cfg.dir, "missing")
			return nil
		}, false, 0},
		{"web listen fails", func(t *testing.T) *fakeListen { return newFakeListen(t, 0) }, func(_ *serviceConfig) []string {
			return nil
		}, false, 1},
		{"ingest listen fails", func(t *testing.T) *fakeListen { return newFakeListen(t, 1) }, func(_ *serviceConfig) []string {
			return nil
		}, false, 2},
	}
}

// countLevel returns the number of JSON log lines in stderr with the given level.
func countLevel(t *testing.T, stderr, level string) int {
	t.Helper()
	n := 0
	for _, line := range logLines(t, stderr) {
		if line["level"] == level {
			n++
		}
	}
	return n
}

func TestServe_StartupFailures(t *testing.T) {
	for _, tc := range startupFailures() {
		t.Run(tc.name, func(t *testing.T) {
			cfg := newServiceConfig(t)
			environ := tc.prepare(&cfg)
			path := filepath.Join(t.TempDir(), "absent.yaml")
			if !tc.noFile {
				path = cfg.write(t)
			}
			listen := tc.listen(t)

			code, stderr := runOnce(t, path, environ, listen)

			if code != 1 {
				t.Errorf("run exit code = %d, want 1 (stderr: %s)", code, stderr)
			}
			if n := countLevel(t, stderr, "ERROR"); n != 1 {
				t.Errorf("stderr has %d ERROR lines, want exactly 1: %s", n, stderr)
			}
			if got := len(listen.calls()); got != tc.wantCall {
				t.Errorf("listen was called %d times, want %d", got, tc.wantCall)
			}
		})
	}
}

func TestServe_IngestListenFailureClosesWebListener(t *testing.T) {
	cfg := newServiceConfig(t)
	listen := newFakeListen(t, 1)

	code, stderr := runOnce(t, cfg.write(t), nil, listen)

	if code != 1 {
		t.Fatalf("run exit code = %d, want 1 (stderr: %s)", code, stderr)
	}
	if !listen.listener(0).isClosed() {
		t.Error("web listener was not closed after the ingest listen failed")
	}
}

func TestServe_NeverLogsTheSecret(t *testing.T) {
	sentinel := strings.Repeat("Zq7k", 16)
	cfg := newServiceConfig(t)
	environ := []string{config.EnvAgentToken + "=" + sentinel}
	s := startService(t.Context(), t, cfg.write(t), environ, newFakeListen(t))
	s.waitHealthy()

	code := s.stop()

	if code != 0 {
		t.Fatalf("run exit code = %d, want 0 (stderr: %s)", code, s.stderr.String())
	}
	if strings.Contains(s.stderr.String(), sentinel) {
		t.Error("stderr contains the agent token, want it never logged")
	}
}

func TestServe_StartLine(t *testing.T) {
	cfg := newServiceConfig(t)
	path := cfg.write(t)
	s := startService(t.Context(), t, path, nil, newFakeListen(t))
	s.waitHealthy()
	if code := s.stop(); code != 0 {
		t.Fatalf("run exit code = %d, want 0 (stderr: %s)", code, s.stderr.String())
	}

	line := findLine(t, s.stderr.String(), "vandoxd starting")

	want := map[string]string{"config": path, "web": cfg.web, "ingest": cfg.ingest, "storage": cfg.dir}
	for key, value := range want {
		if line[key] != value {
			t.Errorf("start line %q = %v, want %q", key, line[key], value)
		}
	}
	for _, key := range []string{"version", "commit"} {
		if _, ok := line[key]; !ok {
			t.Errorf("start line has no %q attribute: %v", key, line)
		}
	}
}

func TestServe_HonorsConfiguredLogLevel(t *testing.T) {
	cfg := newServiceConfig(t)
	cfg.level = "warn"
	s := startService(t.Context(), t, cfg.write(t), nil, newFakeListen(t))
	s.waitHealthy()

	code := s.stop()

	if code != 0 {
		t.Fatalf("run exit code = %d, want 0 (stderr: %s)", code, s.stderr.String())
	}
	if n := countLevel(t, s.stderr.String(), "INFO"); n != 0 {
		t.Errorf("stderr has %d INFO lines with log.level warn, want 0: %s", n, s.stderr.String())
	}
}
