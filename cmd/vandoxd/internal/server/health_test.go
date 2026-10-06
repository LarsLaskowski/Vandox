package server

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"testing/synctest"
	"time"
)

// fakePinger is a Pinger with a scripted result. When block is non-nil, Ping waits until block is closed
// (it ignores its context, like a database call that hangs) and then returns err.
type fakePinger struct {
	err         error
	block       chan struct{}
	entered     chan struct{} // receives a value each time Ping starts, when non-nil
	calls       atomic.Int32
	hadDeadline atomic.Bool
}

func (p *fakePinger) Ping(ctx context.Context) error {
	p.calls.Add(1)
	if _, ok := ctx.Deadline(); ok {
		p.hadDeadline.Store(true)
	}
	if p.entered != nil {
		p.entered <- struct{}{}
	}
	if p.block != nil {
		<-p.block
	}
	return p.err
}

// discardLogger returns a logger that drops every record.
func discardLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(io.Discard, nil))
}

func serve(h http.Handler, method, target string) *httptest.ResponseRecorder {
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequest(method, target, nil))
	return rec
}

func TestNewWebHandler_HealthzHealthy(t *testing.T) {
	h := NewWebHandler(&fakePinger{}, 2*time.Second, discardLogger())

	rec := serve(h, http.MethodGet, "/healthz")

	if rec.Code != http.StatusOK {
		t.Errorf("GET /healthz status = %d, want 200", rec.Code)
	}
	if got := rec.Body.String(); got != "ok\n" {
		t.Errorf("GET /healthz body = %q, want %q", got, "ok\n")
	}
	wantHeaders := map[string]string{
		"Content-Type":           "text/plain; charset=utf-8",
		"Cache-Control":          "no-store",
		"X-Content-Type-Options": "nosniff",
	}
	for name, want := range wantHeaders {
		if got := rec.Header().Get(name); got != want {
			t.Errorf("GET /healthz header %s = %q, want %q", name, got, want)
		}
	}
}

func TestNewWebHandler_HealthzHead(t *testing.T) {
	// Through a real server: net/http drops the body of a HEAD response, a recorder does not.
	srv := httptest.NewServer(NewWebHandler(&fakePinger{}, 2*time.Second, discardLogger()))
	t.Cleanup(srv.Close)

	resp, err := srv.Client().Head(srv.URL + "/healthz")
	if err != nil {
		t.Fatalf("HEAD /healthz error = %v, want nil", err)
	}
	defer func() { _ = resp.Body.Close() }()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("reading the HEAD response body: %v", err)
	}

	if resp.StatusCode != http.StatusOK {
		t.Errorf("HEAD /healthz status = %d, want 200", resp.StatusCode)
	}
	if len(body) != 0 {
		t.Errorf("HEAD /healthz body = %q, want empty", body)
	}
}

func TestNewWebHandler_HealthzUnavailable(t *testing.T) {
	const pingErr = "open /secret/data/vandox.db: disk on fire"
	var logs bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&logs, nil))
	h := NewWebHandler(&fakePinger{err: errors.New(pingErr)}, 2*time.Second, logger)

	rec := serve(h, http.MethodGet, "/healthz")

	if rec.Code != http.StatusServiceUnavailable {
		t.Errorf("GET /healthz status = %d, want 503", rec.Code)
	}
	if got := rec.Body.String(); got != "unavailable\n" {
		t.Errorf("GET /healthz body = %q, want %q (the error text must not leak)", got, "unavailable\n")
	}
	var records []map[string]any
	for _, line := range bytes.Split(bytes.TrimSpace(logs.Bytes()), []byte("\n")) {
		var m map[string]any
		if err := json.Unmarshal(line, &m); err != nil {
			t.Fatalf("log line %q is not JSON: %v", line, err)
		}
		records = append(records, m)
	}
	if len(records) != 1 {
		t.Fatalf("logged %d records %q, want exactly 1", len(records), logs.String())
	}
	if got := records[0]["level"]; got != "WARN" {
		t.Errorf("log level = %v, want WARN", got)
	}
	if got := records[0]["error"]; got != pingErr {
		t.Errorf("log attribute error = %v, want %q", got, pingErr)
	}
}

func TestNewWebHandler_Routing(t *testing.T) {
	tests := []struct {
		method string
		target string
		want   int
	}{
		{http.MethodPost, "/healthz", http.StatusMethodNotAllowed},
		{http.MethodGet, "/", http.StatusNotFound},
		{http.MethodGet, "/healthz/", http.StatusNotFound},
		{http.MethodGet, "/other", http.StatusNotFound},
	}
	for _, tc := range tests {
		t.Run(tc.method+" "+tc.target, func(t *testing.T) {
			h := NewWebHandler(&fakePinger{}, 2*time.Second, discardLogger())

			rec := serve(h, tc.method, tc.target)

			if rec.Code != tc.want {
				t.Errorf("%s %s status = %d, want %d", tc.method, tc.target, rec.Code, tc.want)
			}
		})
	}
}

// request runs one GET /healthz against h in a goroutine and returns a channel with the recorder.
func request(h http.Handler) <-chan *httptest.ResponseRecorder {
	done := make(chan *httptest.ResponseRecorder, 1)
	go func() { done <- serve(h, http.MethodGet, "/healthz") }()
	return done
}

func TestNewWebHandler_SingleFlightPing(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		pinger := &fakePinger{block: make(chan struct{})}
		defer close(pinger.block)
		h := NewWebHandler(pinger, 2*time.Second, discardLogger())

		first := <-request(h)

		if first.Code != http.StatusServiceUnavailable {
			t.Errorf("first request status = %d, want 503 after the ping timeout", first.Code)
		}
		if !pinger.hadDeadline.Load() {
			t.Error("Ping context has no deadline, want one")
		}
		if got := pinger.calls.Load(); got != 1 {
			t.Errorf("Ping calls after the first request = %d, want 1", got)
		}

		// The first ping is still blocked: concurrent requests start no second ping and time out as well.
		second, third := request(h), request(h)
		for i, ch := range []<-chan *httptest.ResponseRecorder{second, third} {
			if rec := <-ch; rec.Code != http.StatusServiceUnavailable {
				t.Errorf("concurrent request %d status = %d, want 503", i+1, rec.Code)
			}
		}
		if got := pinger.calls.Load(); got != 1 {
			t.Errorf("Ping calls while the first ping is blocked = %d, want 1 (never stacked)", got)
		}
	})
}

func TestNewWebHandler_PingRestartsAfterRelease(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		pinger := &fakePinger{block: make(chan struct{})}
		h := NewWebHandler(pinger, 2*time.Second, discardLogger())
		if rec := <-request(h); rec.Code != http.StatusServiceUnavailable {
			t.Fatalf("request with a blocked ping status = %d, want 503", rec.Code)
		}

		close(pinger.block) // the hung ping returns nil
		synctest.Wait()
		rec := <-request(h)

		if rec.Code != http.StatusOK {
			t.Errorf("request after the release status = %d, want 200", rec.Code)
		}
		if got := pinger.calls.Load(); got != 2 {
			t.Errorf("Ping calls after the release = %d, want 2 (a new ping, not a stale result)", got)
		}
	})
}
