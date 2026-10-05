// Package server serves the web and ingest listeners of the backend.
package server

import (
	"context"
	"errors"
	"log/slog"
	"net"
	"net/http"
	"time"
)

var errNotImplemented = errors.New("not implemented")

// Pinger reports whether the database is reachable.
type Pinger interface {
	Ping(ctx context.Context) error
}

// Options configures Run.
type Options struct {
	Web             net.Listener  // listener of the web UI; serves /healthz
	Ingest          net.Listener  // listener of the ingest API; no routes yet
	DB              Pinger        // checked by /healthz
	Logger          *slog.Logger  // destination of the server's log records
	PingTimeout     time.Duration // limit of one /healthz database check
	ShutdownTimeout time.Duration // deadline of the graceful shutdown of both listeners
}

// Run serves Options.Web and Options.Ingest until ctx is done or a listener fails, then shuts both down
// gracefully within ShutdownTimeout. It returns nil after a clean shutdown, an error wrapping
// context.DeadlineExceeded when requests outlast the deadline (their connections are then closed), or the
// serve error of a failed listener.
func Run(ctx context.Context, opts Options) error {
	return errNotImplemented
}

// NewIngestHandler returns the handler of the ingest listener; it answers 404 until the ingest API exists.
func NewIngestHandler() http.Handler {
	return http.NotFoundHandler()
}

func newHTTPServer(h http.Handler, logger *slog.Logger) *http.Server {
	return &http.Server{Handler: h}
}
