package main

import (
	"context"
	"io"
	"log/slog"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/server"
	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store"
	"github.com/LarsLaskowski/Vandox/internal/config"
	"github.com/LarsLaskowski/Vandox/internal/version"
)

const (
	// pingTimeout limits one /healthz database check.
	pingTimeout = 2 * time.Second
	// shutdownTimeout is the deadline of the graceful shutdown; the compose file's stop_grace_period is longer.
	shutdownTimeout = 10 * time.Second
)

// serve runs the backend service until ctx is done or SIGTERM/SIGINT arrives and returns the exit code.
func serve(ctx context.Context, configPath string, environ []string, stderr io.Writer, listen listenFunc) int {
	ctx, stop := signal.NotifyContext(ctx, syscall.SIGTERM, os.Interrupt)
	defer stop()
	// After the first signal the default handling returns, so a second signal ends the process at once.
	context.AfterFunc(ctx, stop)

	bootstrap, _ := newLogger(stderr, "info")
	cfg, err := config.LoadBackend(configPath, environ)
	if err != nil {
		bootstrap.ErrorContext(ctx, "configuration invalid", slog.Any("error", err))
		return 1
	}
	logger, err := newLogger(stderr, cfg.Log.Level)
	if err != nil {
		bootstrap.ErrorContext(ctx, "configuration invalid", slog.Any("error", err))
		return 1
	}
	logger.InfoContext(ctx, "vandoxd starting",
		slog.String("version", version.Version),
		slog.String("commit", version.Commit),
		slog.String("config", configPath),
		slog.String("web", cfg.Web.Listen),
		slog.String("ingest", cfg.Ingest.Listen),
		slog.String("storage", cfg.Storage.Directory))

	code := runService(ctx, cfg, logger, listen)
	logger.InfoContext(ctx, "vandoxd stopped")
	return code
}

// runService opens the database and the listeners, serves until ctx is done and returns the exit code.
func runService(ctx context.Context, cfg *config.Backend, logger *slog.Logger, listen listenFunc) int {
	db, err := store.Open(ctx, cfg.Storage.Directory)
	if err != nil {
		logger.ErrorContext(ctx, "opening database failed", slog.Any("error", err))
		return 1
	}
	logger.InfoContext(ctx, "database opened",
		slog.String("path", filepath.Join(cfg.Storage.Directory, store.FileName)),
		slog.Bool("created", db.Created()))

	code := 0
	if err := serveListeners(ctx, cfg, logger, listen, db); err != nil {
		logger.ErrorContext(ctx, "serving failed", slog.Any("error", err))
		code = 1
	}
	if err := db.Close(); err != nil {
		logger.ErrorContext(ctx, "closing database failed", slog.Any("error", err))
		code = 1
	}
	return code
}

// serveListeners opens the web and ingest listeners and serves them until ctx is done.
func serveListeners(ctx context.Context, cfg *config.Backend, logger *slog.Logger, listen listenFunc, db *store.Store) error {
	web, err := listen(ctx, "tcp", cfg.Web.Listen)
	if err != nil {
		return err
	}
	ingest, err := listen(ctx, "tcp", cfg.Ingest.Listen)
	if err != nil {
		_ = web.Close()
		return err
	}
	return server.Run(ctx, server.Options{
		Web:             web,
		Ingest:          ingest,
		DB:              db,
		Logger:          logger,
		PingTimeout:     pingTimeout,
		ShutdownTimeout: shutdownTimeout,
	})
}

