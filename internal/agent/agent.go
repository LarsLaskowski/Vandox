// Package agent runs vandox-agent: configuration, logging, the scheduler with its collectors and the systemd notification.
package agent

import (
	"context"
	"io"
	"log/slog"
	"os"
	"sync"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/config"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/sdnotify"
	"github.com/LarsLaskowski/Vandox/internal/selfstats"
	"github.com/LarsLaskowski/Vandox/internal/version"
)

// Schedule of the self-metrics collector.
const (
	SelfInterval = 60 * time.Second
	SelfTimeout  = 10 * time.Second
)

// Options are the process boundaries Run works with.
type Options struct {
	ConfigPath string            // the configuration file
	Environ    []string          // the environment, in os.Environ form
	Stderr     io.Writer         // the log output
	Sink       scheduler.Sink    // nil: scheduler.DiscardSink
	Self       selfstats.Sources // the self-metrics sources; zero fields take their defaults
}

// Run runs the agent until ctx is done and returns the exit code: 0 after a clean stop, 1 when it cannot start.
func Run(ctx context.Context, opts Options) int {
	stderr := opts.Stderr
	if stderr == nil {
		stderr = io.Discard
	}
	journal := journalStream(opts.Environ, stderr)

	cfg, err := config.LoadAgent(opts.ConfigPath, opts.Environ)
	if err != nil {
		NewLogger(stderr, "info", journal).Error("agent cannot start", "err", err)
		return 1
	}
	log := NewLogger(stderr, cfg.Log.Level, journal)

	sched, err := newScheduler(opts, log)
	if err != nil {
		log.Error("agent cannot start", "err", err)
		return 1
	}
	notify, interval := notification(opts.Environ, log)

	var wg sync.WaitGroup
	wg.Go(func() { sched.Run(ctx) })
	if err := notify.Notify(sdnotify.Ready); err != nil {
		log.Warn("service manager notification failed", "state", sdnotify.Ready, "err", err)
	}
	log.Info("agent started", "agent_id", cfg.AgentID, "version", version.Version)

	if notify.Enabled() && interval > 0 {
		runWatchdog(ctx, notify, interval, sched.Healthy, log)
	}
	<-ctx.Done()

	if err := notify.Notify(sdnotify.Stopping); err != nil {
		log.Warn("service manager notification failed", "state", sdnotify.Stopping, "err", err)
	}
	wg.Wait()
	log.Info("agent stopped")
	return 0
}

// newScheduler returns the scheduler with the self-metrics job.
func newScheduler(opts Options, log *slog.Logger) (*scheduler.Scheduler, error) {
	var sink scheduler.Sink = scheduler.DiscardSink{}
	if opts.Sink != nil {
		sink = opts.Sink
	}
	self := scheduler.Job{
		Collector: selfstats.New(opts.Self),
		Schedule:  scheduler.Schedule{Interval: SelfInterval, Timeout: SelfTimeout},
	}
	return scheduler.New([]scheduler.Job{self}, sink, log)
}

// notification returns the notifier of the service manager and the watchdog interval; a problem with either is
// logged as a warning and leaves it off, the agent runs on.
func notification(environ []string, log *slog.Logger) (*sdnotify.Notifier, time.Duration) {
	notify, err := sdnotify.FromEnviron(environ)
	if err != nil {
		log.Warn("service manager notification disabled", "err", err)
	}
	interval, err := sdnotify.WatchdogInterval(environ, os.Getpid())
	if err != nil {
		log.Warn("watchdog disabled", "err", err)
	}
	return notify, interval
}
