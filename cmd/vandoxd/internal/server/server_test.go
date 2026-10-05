package server

import (
	"bytes"
	"context"
	"errors"
	"net"
	"net/http"
	"sync"
	"testing"
	"time"
)

// guard bounds every wait on Run, so a hanging test fails instead of blocking; it is never asserted.
const guard = 30 * time.Second

// recordingListener wraps a listener and reports its Close on a channel.
type recordingListener struct {
	net.Listener
	once   sync.Once
	closed chan struct{}
}

func (l *recordingListener) Close() error {
	l.once.Do(func() { close(l.closed) })
	return l.Listener.Close()
}

func newListener(t *testing.T) *recordingListener {
	t.Helper()
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("Listen: %v", err)
	}
	rl := &recordingListener{Listener: l, closed: make(chan struct{})}
	t.Cleanup(func() { _ = rl.Close() })
	return rl
}

// failingListener is a listener whose Accept fails with a non-temporary error.
type failingListener struct {
	err error
}

func (l failingListener) Accept() (net.Conn, error) { return nil, l.err }
func (l failingListener) Close() error              { return nil }
func (l failingListener) Addr() net.Addr            { return &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 1} }

// harness is a Run call executing in a goroutine.
type harness struct {
	t      *testing.T
	cancel context.CancelFunc
	exited chan struct{} // closed when Run has returned
	err    error         // Run's result, valid after exited is closed
}

func validOptions(t *testing.T, db Pinger) (Options, *recordingListener, *recordingListener) {
	t.Helper()
	web, ingest := newListener(t), newListener(t)
	return Options{
		Web:             web,
		Ingest:          ingest,
		DB:              db,
		Logger:          discardLogger(),
		PingTimeout:     2 * time.Second,
		ShutdownTimeout: 10 * time.Second,
	}, web, ingest
}

func startRun(t *testing.T, opts Options) *harness {
	t.Helper()
	ctx, cancel := context.WithCancel(t.Context())
	h := &harness{t: t, cancel: cancel, exited: make(chan struct{})}
	go func() {
		defer close(h.exited)
		h.err = Run(ctx, opts)
	}()
	t.Cleanup(func() {
		cancel()
		select {
		case <-h.exited:
		case <-time.After(guard):
			t.Error("Run did not return after cancel")
		}
	})
	return h
}

// result waits for Run to return.
func (h *harness) result() error {
	h.t.Helper()
	select {
	case <-h.exited:
		return h.err
	case <-time.After(guard):
		h.t.Fatal("Run did not return")
		return nil
	}
}

// returned reports whether Run has returned, without waiting.
func (h *harness) returned() bool {
	select {
	case <-h.exited:
		return true
	default:
		return false
	}
}

// await waits for ch to receive; it fails the test when Run returns first.
func (h *harness) await(ch <-chan struct{}, what string) {
	h.t.Helper()
	select {
	case <-ch:
	case <-h.exited:
		h.t.Fatalf("Run returned (%v) before %s", h.err, what)
	case <-time.After(guard):
		h.t.Fatalf("timed out waiting for %s", what)
	}
}

// get requests path from l; the request is abandoned once Run has returned.
func (h *harness) get(l net.Listener, path string) (int, string, error) {
	h.t.Helper()
	ctx, cancel := context.WithTimeout(h.t.Context(), guard)
	defer cancel()
	go func() {
		select {
		case <-h.exited:
			cancel()
		case <-ctx.Done():
		}
	}()
	return doGet(ctx, l, path)
}

func doGet(ctx context.Context, l net.Listener, path string) (int, string, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, "http://"+l.Addr().String()+path, nil)
	if err != nil {
		return 0, "", err
	}
	client := &http.Client{Transport: &http.Transport{DisableKeepAlives: true}}
	resp, err := client.Do(req)
	if err != nil {
		return 0, "", err
	}
	defer func() { _ = resp.Body.Close() }()
	buf := new(bytes.Buffer)
	_, err = buf.ReadFrom(resp.Body)
	return resp.StatusCode, buf.String(), err
}

func dialFails(t *testing.T, l net.Listener) bool {
	t.Helper()
	conn, err := net.DialTimeout("tcp", l.Addr().String(), guard)
	if err == nil {
		_ = conn.Close()
		return false
	}
	return true
}

func TestNewIngestHandler_AnswersNotFound(t *testing.T) {
	tests := []struct {
		method string
		target string
	}{
		{http.MethodGet, "/"},
		{http.MethodGet, "/healthz"},
		{http.MethodPost, "/v1/batches"},
	}
	for _, tc := range tests {
		t.Run(tc.method+" "+tc.target, func(t *testing.T) {
			rec := serve(NewIngestHandler(), tc.method, tc.target)

			if rec.Code != http.StatusNotFound {
				t.Errorf("%s %s status = %d, want 404", tc.method, tc.target, rec.Code)
			}
		})
	}
}

func TestNewHTTPServer_Limits(t *testing.T) {
	srv := newHTTPServer(http.NotFoundHandler(), discardLogger())

	checks := []struct {
		name      string
		got, want time.Duration
	}{
		{"ReadHeaderTimeout", srv.ReadHeaderTimeout, 5 * time.Second},
		{"ReadTimeout", srv.ReadTimeout, 30 * time.Second},
		{"WriteTimeout", srv.WriteTimeout, 30 * time.Second},
		{"IdleTimeout", srv.IdleTimeout, 120 * time.Second},
	}
	for _, c := range checks {
		if c.got != c.want {
			t.Errorf("newHTTPServer %s = %v, want %v", c.name, c.got, c.want)
		}
	}
	if srv.MaxHeaderBytes != 16<<10 {
		t.Errorf("newHTTPServer MaxHeaderBytes = %d, want %d", srv.MaxHeaderBytes, 16<<10)
	}
	if srv.ErrorLog == nil {
		t.Error("newHTTPServer ErrorLog = nil, want a logger")
	}
	if srv.Handler == nil {
		t.Error("newHTTPServer Handler = nil, want the given handler")
	}
}

func TestRun_ServesBothListeners(t *testing.T) {
	opts, web, ingest := validOptions(t, &fakePinger{})
	h := startRun(t, opts)

	status, body, err := h.get(web, "/healthz")
	if err != nil || status != http.StatusOK || body != "ok\n" {
		t.Errorf("GET web /healthz = %d %q, %v, want 200 \"ok\\n\", nil", status, body, err)
	}
	status, _, err = h.get(ingest, "/healthz")
	if err != nil || status != http.StatusNotFound {
		t.Errorf("GET ingest /healthz = %d, %v, want 404, nil", status, err)
	}
	h.cancel()

	if err := h.result(); err != nil {
		t.Errorf("Run() after cancel = %v, want nil", err)
	}
	if !dialFails(t, web) || !dialFails(t, ingest) {
		t.Error("a listener still accepts connections after Run returned, want both closed")
	}
}

func TestRun_GracefulShutdownWaitsForRequest(t *testing.T) {
	pinger := &fakePinger{block: make(chan struct{}), entered: make(chan struct{}, 4)}
	opts, web, _ := validOptions(t, pinger)
	opts.PingTimeout = time.Minute
	opts.ShutdownTimeout = time.Minute
	h := startRun(t, opts)
	type reply struct {
		status int
		err    error
	}
	replies := make(chan reply, 1)
	go func() {
		status, _, err := h.get(web, "/healthz")
		replies <- reply{status, err}
	}()
	h.await(pinger.entered, "the request reached the database")

	h.cancel()
	h.await(web.closed, "Shutdown closed the web listener") // Shutdown closes the listeners first

	if h.returned() {
		t.Fatal("Run returned while a request was still in flight, want it to wait")
	}
	close(pinger.block)
	r := <-replies
	if r.err != nil || r.status != http.StatusOK {
		t.Errorf("in-flight request = %d, %v, want 200, nil", r.status, r.err)
	}
	if err := h.result(); err != nil {
		t.Errorf("Run() = %v, want nil", err)
	}
}

func TestRun_ShutdownDeadline(t *testing.T) {
	pinger := &fakePinger{block: make(chan struct{}), entered: make(chan struct{}, 4)}
	t.Cleanup(func() { close(pinger.block) })
	opts, web, _ := validOptions(t, pinger)
	opts.PingTimeout = time.Minute
	opts.ShutdownTimeout = 50 * time.Millisecond
	h := startRun(t, opts)
	clientErr := make(chan error, 1)
	go func() {
		_, _, err := h.get(web, "/healthz")
		clientErr <- err
	}()
	h.await(pinger.entered, "the request reached the database")

	h.cancel()

	err := h.result()
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Errorf("Run() = %v, want an error wrapping context.DeadlineExceeded", err)
	}
	if err := <-clientErr; err == nil {
		t.Error("in-flight request error = nil, want the connection closed by the shutdown deadline")
	}
}

func TestRun_ListenerFailure(t *testing.T) {
	acceptErr := errors.New("accept failed for good")
	tests := []struct {
		name    string
		failing string
	}{
		{"web listener fails", "web"},
		{"ingest listener fails", "ingest"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			opts, web, ingest := validOptions(t, &fakePinger{})
			var other *recordingListener
			if tc.failing == "web" {
				opts.Web = failingListener{err: acceptErr}
				other = ingest
			} else {
				opts.Ingest = failingListener{err: acceptErr}
				other = web
			}
			h := startRun(t, opts)

			err := h.result()

			if !errors.Is(err, acceptErr) {
				t.Errorf("Run() = %v, want an error wrapping the Accept error", err)
			}
			if !dialFails(t, other) {
				t.Error("the healthy listener still accepts connections after Run returned, want it shut down")
			}
		})
	}
}

func TestRun_InvalidOptions(t *testing.T) {
	tests := []struct {
		name   string
		mutate func(o *Options)
	}{
		{"nil Web", func(o *Options) { o.Web = nil }},
		{"nil Ingest", func(o *Options) { o.Ingest = nil }},
		{"nil DB", func(o *Options) { o.DB = nil }},
		{"nil Logger", func(o *Options) { o.Logger = nil }},
		{"zero PingTimeout", func(o *Options) { o.PingTimeout = 0 }},
		{"negative PingTimeout", func(o *Options) { o.PingTimeout = -time.Second }},
		{"zero ShutdownTimeout", func(o *Options) { o.ShutdownTimeout = 0 }},
		{"negative ShutdownTimeout", func(o *Options) { o.ShutdownTimeout = -time.Second }},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			opts, _, _ := validOptions(t, &fakePinger{})
			tc.mutate(&opts)
			ctx, cancel := context.WithCancel(t.Context())
			defer cancel()

			err := Run(ctx, opts)

			if err == nil {
				t.Error("Run() error = nil, want an error for the invalid options")
			}
		})
	}
}

func TestRun_CancelledContext(t *testing.T) {
	opts, _, _ := validOptions(t, &fakePinger{})
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	done := make(chan error, 1)

	go func() { done <- Run(ctx, opts) }()

	select {
	case err := <-done:
		if err != nil {
			t.Errorf("Run(cancelled ctx) = %v, want nil", err)
		}
	case <-time.After(guard):
		t.Fatal("Run(cancelled ctx) did not return")
	}
}
