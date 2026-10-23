// Package agent runs vandox-agent: configuration, logging, the scheduler with its collectors and the systemd notification.
package agent

import (
	"context"
	"io"
	"log/slog"
	"os"
	"sync"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/backups"
	"github.com/LarsLaskowski/Vandox/internal/config"
	"github.com/LarsLaskowski/Vandox/internal/connstats"
	"github.com/LarsLaskowski/Vandox/internal/kernevents"
	"github.com/LarsLaskowski/Vandox/internal/logship"
	"github.com/LarsLaskowski/Vandox/internal/mariadb"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/sdnotify"
	"github.com/LarsLaskowski/Vandox/internal/selfstats"
	"github.com/LarsLaskowski/Vandox/internal/sockmount"
	"github.com/LarsLaskowski/Vandox/internal/svcstats"
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

// Schedules of the systemd collector.
const (
	SystemdInterval         = 30 * time.Second
	SystemdPressureInterval = 10 * time.Second
	SystemdTimeout          = 5 * time.Second
)

// Schedules of the MariaDB collector.
const (
	MariaDBInterval         = 30 * time.Second
	MariaDBPressureInterval = 10 * time.Second
	MariaDBTimeout          = 5 * time.Second
)

// Schedule of the log shipping collector.
const (
	LogsInterval = 2 * time.Second
	LogsTimeout  = 2 * time.Second
)

// Schedules of the kernel event, backup and state collectors.
const (
	KernelInterval  = 2 * time.Second
	KernelTimeout   = 2 * time.Second
	BackupsInterval = 10 * time.Second
	BackupsTimeout  = 5 * time.Second
	StateInterval   = 10 * time.Second
	StateTimeout    = 5 * time.Second
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
	// Services are the systemd sources; the zero value uses the system bus.
	Services svcstats.Sources
	// MariaDB are the MariaDB sources; zero fields take their defaults.
	MariaDB mariadb.Sources
	// Kernel are the kernel event sources; zero fields take their defaults.
	Kernel kernevents.Sources
	// Logs are the log shipping sources; zero fields take their defaults.
	Logs logship.Sources
	// StateDir is the directory of the agent's state file; "" takes spool.directory.
	StateDir string
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

	sched, _, err := newScheduler(opts, cfg, log)
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

// jobs returns the collectors with their schedules: self, system, processes, connections, systemd (only when a service unit is
// configured), mariadb (only when enabled), filesystem (only when a mount point is configured), clock, kernel, backups (only
// when backups.processes has rules) and state, and the system collector, which newScheduler connects to the scheduler. The
// agent's state file is opened in opts.StateDir or else spool.directory. The collectors log through log.
func jobs(opts Options, cfg *config.Agent, log *slog.Logger) ([]scheduler.Job, *sysstats.System, *agentstate.Store) {
	system := sysstats.NewSystem(opts.System, log)
	systemdSchedule := scheduler.Schedule{Interval: SystemdInterval, Timeout: SystemdTimeout, PressureInterval: SystemdPressureInterval}
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
		{
			Collector: connstats.New(connectionSources(opts.Connections, probeSocketMount), log),
			Schedule:  scheduler.Schedule{Interval: ConnectionsInterval, Timeout: ConnectionsTimeout, PressureInterval: ConnectionsPressureInterval},
		},
	}
	if len(cfg.Services.Units) > 0 {
		list = append(list, scheduler.Job{
			Collector: svcstats.New(opts.Services, cfg.Services.Units, systemdSchedule, log),
			Schedule:  systemdSchedule,
		})
	}
	if cfg.MariaDB.Enabled {
		list = append(list, scheduler.Job{
			Collector: mariadb.New(opts.MariaDB, cfg.MariaDB.Socket, log),
			Schedule:  scheduler.Schedule{Interval: MariaDBInterval, Timeout: MariaDBTimeout, PressureInterval: MariaDBPressureInterval},
		})
	}
	if len(cfg.Filesystem.MountPoints) > 0 {
		list = append(list, scheduler.Job{
			Collector: sysstats.NewFilesystem(opts.System, cfg.Filesystem.MountPoints, log),
			Schedule:  scheduler.Schedule{Interval: FilesystemInterval, Timeout: FilesystemTimeout},
		})
	}
	list = append(list, scheduler.Job{
		Collector: sysstats.NewClock(opts.System),
		Schedule:  scheduler.Schedule{Interval: ClockInterval, Timeout: ClockTimeout},
	})

	stateDir := opts.StateDir
	if stateDir == "" {
		stateDir = cfg.Spool.Directory
	}
	store, _ := agentstate.Open(stateDir, log)
	proc := opts.Kernel.Proc
	if proc == nil {
		proc = os.DirFS("/proc")
	}
	list = append(list, scheduler.Job{
		Collector: kernevents.New(opts.Kernel, store, log),
		Schedule:  scheduler.Schedule{Interval: KernelInterval, Timeout: KernelTimeout},
	})
	if logs, err := logship.New(opts.Logs, logship.Config{}, store, log); err == nil {
		list = append(list, scheduler.Job{
			Collector: logs,
			Schedule:  scheduler.Schedule{Interval: LogsInterval, Timeout: LogsTimeout},
		})
	}
	if len(cfg.Backups.Processes) > 0 {
		list = append(list, scheduler.Job{
			Collector: backups.New(proc, backupRules(cfg.Backups.Processes), store, log),
			Schedule:  scheduler.Schedule{Interval: BackupsInterval, Timeout: BackupsTimeout},
		})
	}
	return append(list, scheduler.Job{
		Collector: agentstate.NewSaver(store, proc),
		Schedule:  scheduler.Schedule{Interval: StateInterval, Timeout: StateTimeout},
	}), system, store
}

// backupRules converts the configured backup rules for the backup collector.
func backupRules(rules []config.BackupRule) []backups.Rule {
	out := make([]backups.Rule, 0, len(rules))
	for _, r := range rules {
		out = append(out, backups.Rule{Command: r.Command, CmdlinePrefix: r.CmdlinePrefix})
	}
	return out
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
// pressure mode and the kernel collector requests the extra runs of the processes and connections collectors.
func newScheduler(opts Options, cfg *config.Agent, log *slog.Logger) (*scheduler.Scheduler, *agentstate.Store, error) {
	var sink scheduler.Putter = scheduler.DiscardSink{}
	if opts.Sink != nil {
		sink = opts.Sink
	}
	list, system, store := jobs(opts, cfg, log)
	sched, err := scheduler.New(list, sink, log)
	if err != nil {
		return nil, nil, err
	}
	system.SetPressureSwitch(sched.SetPressure)
	for _, j := range list {
		if k, ok := j.Collector.(*kernevents.Kernel); ok {
			k.SetTrigger(sched.Trigger)
		}
	}
	return sched, store, nil
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
	if src.SocketMount.ID != 0 || src.SocketMount.Err != nil {
		return src
	}
	src.SocketMount.ID, src.SocketMount.Err = probe()
	return src
}

// probeSocketMount learns the socket mount from a socket of the agent's own, through the host's /proc.
func probeSocketMount() (int32, error) {
	return sockmount.Probe(os.DirFS("/proc"))
}
