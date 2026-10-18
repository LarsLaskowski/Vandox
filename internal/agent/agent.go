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
	"github.com/LarsLaskowski/Vandox/internal/connstats"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/sdnotify"
	"github.com/LarsLaskowski/Vandox/internal/selfstats"
	"github.com/LarsLaskowski/Vandox/internal/sysstats"
	"github.com/LarsLaskowski/Vandox/internal/version"
)

// Schedule of the self-metrics collector.
const (
	SelfInterval = 60 * time.Second
	SelfTimeout  = 10 * time.Second
)

// Schedules of the system, file system and clock collectors.
const (
	SystemInterval         = 30 * time.Second
	SystemPressureInterval = 10 * time.Second
	SystemTimeout          = 5 * time.Second
	FilesystemInterval     = 60 * time.Second
	FilesystemTimeout      = 10 * time.Second
	ClockInterval          = 60 * time.Second
	ClockTimeout           = 5 * time.Second
)

// Schedules of the process collector.
const (
	ProcessesInterval         = 60 * time.Second
	ProcessesPressureInterval = 15 * time.Second
	ProcessesTimeout          = 10 * time.Second
)

// Schedules of the connection collector.
const (
	ConnectionsInterval         = 60 * time.Second
	ConnectionsPressureInterval = 15 * time.Second
	ConnectionsTimeout          = 10 * time.Second
)

// Options are the process boundaries Run works with.
type Options struct {
	ConfigPath string            // the configuration file
	Environ    []string          // the environment, in os.Environ form
	Stderr     io.Writer         // the log output
	Sink       scheduler.Putter  // nil: scheduler.DiscardSink
	Self       selfstats.Sources // the self-metrics sources; zero fields take their defaults
	System     sysstats.Sources  // the system metrics sources; zero fields take their defaults
	Processes  procstats.Sources // the process sources; zero fields take their defaults
	// Connections are the connection sources; zero fields take their defaults.
	Connections connstats.Sources
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

	sched, err := newScheduler(opts, cfg, log)
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

// jobs returns the collectors with their schedules: self, system, processes, filesystem (only when a mount point is
// configured) and clock, and the system collector, which newScheduler connects to the scheduler. The collectors log
// through log.
func jobs(opts Options, cfg *config.Agent, log *slog.Logger) ([]scheduler.Job, *sysstats.System) {
	system := sysstats.NewSystem(opts.System, log)
	list := []scheduler.Job{
		{
			Collector: selfstats.New(opts.Self),
			Schedule:  scheduler.Schedule{Interval: SelfInterval, Timeout: SelfTimeout},
		},
		{
			Collector: system,
			Schedule:  scheduler.Schedule{Interval: SystemInterval, Timeout: SystemTimeout, PressureInterval: SystemPressureInterval},
		},
		{
			Collector: procstats.New(opts.Processes, programRules(cfg.Processes.Programs), log),
			Schedule:  scheduler.Schedule{Interval: ProcessesInterval, Timeout: ProcessesTimeout, PressureInterval: ProcessesPressureInterval},
		},
	}
	if len(cfg.Filesystem.MountPoints) > 0 {
		list = append(list, scheduler.Job{
			Collector: sysstats.NewFilesystem(opts.System, cfg.Filesystem.MountPoints, log),
			Schedule:  scheduler.Schedule{Interval: FilesystemInterval, Timeout: FilesystemTimeout},
		})
	}
	return append(list, scheduler.Job{
		Collector: sysstats.NewClock(opts.System),
		Schedule:  scheduler.Schedule{Interval: ClockInterval, Timeout: ClockTimeout},
	}), system
}

// programRules converts the configured program rules for the process collector.
func programRules(rules []config.ProgramRule) []procstats.Rule {
	out := make([]procstats.Rule, 0, len(rules))
	for _, r := range rules {
		out = append(out, procstats.Rule{
			Program:        r.Program,
			Command:        r.Command,
			CmdlinePrefix:  r.CmdlinePrefix,
			AppendNextWord: r.AppendNextWord,
		})
	}
	return out
}

// newScheduler returns the scheduler with the jobs of the configuration; the system collector switches its memory
// pressure mode.
func newScheduler(opts Options, cfg *config.Agent, log *slog.Logger) (*scheduler.Scheduler, error) {
	var sink scheduler.Putter = scheduler.DiscardSink{}
	if opts.Sink != nil {
		sink = opts.Sink
	}
	list, system := jobs(opts, cfg, log)
	sched, err := scheduler.New(list, sink, log)
	if err != nil {
		return nil, err
	}
	system.SetPressureSwitch(sched.SetPressure)
	return sched, nil
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

// connectionSources returns src with the socket mount filled by probe, which runs only when src.SocketMount is zero.
func connectionSources(src connstats.Sources, probe func() (int32, error)) connstats.Sources {
	return src
}
