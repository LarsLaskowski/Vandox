// Package server serves the web and ingest listeners of the backend.
package server

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"sync"
	"time"
)

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
	if err := opts.validate(); err != nil {
		return err
	}
	web := newHTTPServer(NewWebHandler(opts.DB, opts.PingTimeout, opts.Logger), opts.Logger)
	ingest := newHTTPServer(NewIngestHandler(), opts.Logger)
	servers := []namedServer{{"web", web, opts.Web}, {"ingest", ingest, opts.Ingest}}

	serveErrs := make(chan error, len(servers))
	var serving sync.WaitGroup
	for _, s := range servers {
		opts.Logger.InfoContext(ctx, "listening", slog.String("listener", s.name), slog.String("address", s.listener.Addr().String()))
		serving.Add(1)
		go func() {
			defer serving.Done()
			if err := s.server.Serve(s.listener); err != nil && !errors.Is(err, http.ErrServerClosed) {
				serveErrs <- fmt.Errorf("serving %s listener: %w", s.name, err)
			}
		}()
	}

	var serveErr error
	select {
	case <-ctx.Done():
		opts.Logger.InfoContext(ctx, "shutting down")
	case serveErr = <-serveErrs:
	}

	shutdownErr := shutdown(servers, opts.ShutdownTimeout)
	serving.Wait()
	return errors.Join(serveErr, shutdownErr)
}

// namedServer is an HTTP server with its listener and a name for log and error messages.
type namedServer struct {
	name     string
	server   *http.Server
	listener net.Listener
}

// validate reports the first unusable option.
func (o Options) validate() error {
	switch {
	case o.Web == nil:
		return errors.New("server: web listener is nil")
	case o.Ingest == nil:
		return errors.New("server: ingest listener is nil")
	case o.DB == nil:
		return errors.New("server: database pinger is nil")
	case o.Logger == nil:
		return errors.New("server: logger is nil")
	case o.PingTimeout <= 0:
		return errors.New("server: ping timeout must be positive")
	case o.ShutdownTimeout <= 0:
		return errors.New("server: shutdown timeout must be positive")
	}
	return nil
}

// shutdown gracefully stops all servers within timeout; servers still busy at the deadline are closed and
// the returned error wraps context.DeadlineExceeded.
func shutdown(servers []namedServer, timeout time.Duration) error {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	errs := make([]error, len(servers))
	var wg sync.WaitGroup
	for i, s := range servers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			if err := s.server.Shutdown(ctx); err != nil {
				_ = s.server.Close()
				errs[i] = fmt.Errorf("shutting down %s listener: %w", s.name, err)
			}
		}()
	}
	wg.Wait()
	return errors.Join(errs...)
}

// NewIngestHandler returns the handler of the ingest listener; it answers 404 until the ingest API exists.
func NewIngestHandler() http.Handler {
	return http.NotFoundHandler()
}

// newHTTPServer returns an http.Server for h with timeouts and a header limit that keep a slow client from
// holding a connection forever.
func newHTTPServer(h http.Handler, logger *slog.Logger) *http.Server {
	return &http.Server{
		Handler:           h,
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       30 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       120 * time.Second,
		MaxHeaderBytes:    16 << 10,
		ErrorLog:          slog.NewLogLogger(logger.Handler(), slog.LevelWarn),
	}
}
