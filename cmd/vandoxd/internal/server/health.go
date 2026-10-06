package server

import (
	"context"
	"log/slog"
	"net/http"
	"sync"
	"time"
)

// NewWebHandler returns the handler of the web listener: GET and HEAD /healthz, 200 when db answers within
// pingTimeout, 503 otherwise.
func NewWebHandler(db Pinger, pingTimeout time.Duration, logger *slog.Logger) http.Handler {
	checker := &pingChecker{db: db, timeout: pingTimeout}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) {
		err := checker.check(r.Context())
		h := w.Header()
		h.Set("Content-Type", "text/plain; charset=utf-8")
		h.Set("Cache-Control", "no-store")
		h.Set("X-Content-Type-Options", "nosniff")
		if err != nil {
			logger.WarnContext(r.Context(), "health check failed", slog.Any("error", err))
			w.WriteHeader(http.StatusServiceUnavailable)
			_, _ = w.Write([]byte("unavailable\n"))
			return
		}
		_, _ = w.Write([]byte("ok\n"))
	})
	return mux
}

// flight is one database ping that may be shared by several requests.
type flight struct {
	done chan struct{} // closed when the ping returned
	err  error         // the ping's result, valid after done is closed
}

// pingChecker runs at most one database ping at a time. A request arriving while a ping runs waits for
// that ping's result; a ping that never returns is abandoned by the requests, never stacked.
type pingChecker struct {
	db      Pinger
	timeout time.Duration

	mu       sync.Mutex
	inflight *flight
}

// check returns the result of the shared ping, or the context error when it does not answer within the
// timeout.
func (c *pingChecker) check(ctx context.Context) error {
	f := c.join()
	ctx, cancel := context.WithTimeout(ctx, c.timeout)
	defer cancel()
	select {
	case <-f.done:
		return f.err
	case <-ctx.Done():
		return ctx.Err()
	}
}

// join returns the running ping, starting one when none runs.
func (c *pingChecker) join() *flight {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.inflight != nil {
		return c.inflight
	}
	f := &flight{done: make(chan struct{})}
	c.inflight = f
	go c.ping(f)
	return f
}

// ping runs the database check with its own deadline, independent of any single request.
func (c *pingChecker) ping(f *flight) {
	ctx, cancel := context.WithTimeout(context.Background(), c.timeout)
	defer cancel()
	f.err = c.db.Ping(ctx)
	c.mu.Lock()
	c.inflight = nil
	c.mu.Unlock()
	close(f.done)
}
