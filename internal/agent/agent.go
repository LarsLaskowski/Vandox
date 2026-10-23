// Package agent runs vandox-agent: configuration, logging, the scheduler with its collectors and the systemd notification.
package agent

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"regexp"
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
	"github.com/LarsLaskowski/Vandox/internal/spool"
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
	Sink       scheduler.Putter  // nil: the spool in StateDir, else in spool.directory
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

	store, sp, err := openStorage(opts, cfg, log)
	if err != nil {
		log.Error("agent cannot start", "err", err)
		return 1
	}
	sink := opts.Sink
	if sp != nil {
		sink = sp
		opts.Self.Spool = sp
		opts.Self.SpoolAge = sp
	}
	sched, err := newScheduler(opts, cfg, store, sink, log)
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
	// the last positions of the collectors are saved once the scheduler has stopped; Flush logs a failed save itself
	_ = store.Flush(time.Now())
	closeSpool(sp, log)
	log.Info("agent stopped")
	return 0
}

// jobs returns the collectors with their schedules: self, system, processes, connections, systemd (only when a service unit is
// configured), mariadb (only when enabled), filesystem (only when a mount point is configured), clock, kernel, logs, backups (only
// when backups.processes has rules) and state, and the system collector, which newScheduler connects to the scheduler. The
// state store is the one openStorage opened. The collectors log through log. It fails, and the agent does not start, when the log shipping collector cannot be built: the error wraps
// errDropPattern for a pattern that does not compile and never quotes a pattern.
func jobs(opts Options, cfg *config.Agent, store *agentstate.Store, log *slog.Logger) ([]scheduler.Job, *sysstats.System, error) {
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

	proc := opts.Kernel.Proc
	if proc == nil {
		proc = os.DirFS("/proc")
	}
	list = append(list, scheduler.Job{
		Collector: kernevents.New(opts.Kernel, store, log),
		Schedule:  scheduler.Schedule{Interval: KernelInterval, Timeout: KernelTimeout},
	})
	logs, err := newLogs(opts.Logs, cfg.LogShipping, store, log)
	if err != nil {
		return nil, nil, fmt.Errorf("log shipping: %w", err)
	}
	list = append(list, scheduler.Job{
		Collector: logs,
		Schedule:  scheduler.Schedule{Interval: LogsInterval, Timeout: LogsTimeout},
	})
	if len(cfg.Backups.Processes) > 0 {
		list = append(list, scheduler.Job{
			Collector: backups.New(proc, backupRules(cfg.Backups.Processes), store, log),
			Schedule:  scheduler.Schedule{Interval: BackupsInterval, Timeout: BackupsTimeout},
		})
	}
	return append(list, scheduler.Job{
		Collector: agentstate.NewSaver(store, proc),
		Schedule:  scheduler.Schedule{Interval: StateInterval, Timeout: StateTimeout},
	}), system, nil
}

// errDropPattern is the error of a drop pattern that does not compile; it does not quote the pattern.
var errDropPattern = errors.New("a drop pattern does not compile")

// newLogs returns the log shipping collector for the options of log_shipping.
func newLogs(src logship.Sources, opts config.LogShipping, store *agentstate.Store, log *slog.Logger) (*logship.Logs, error) {
	cfg, err := logshipConfig(opts)
	if err != nil {
		return nil, err
	}
	return logship.New(src, cfg, store, log)
}

// logshipConfig converts the checked options of log_shipping for the collector.
func logshipConfig(opts config.LogShipping) (logship.Config, error) {
	minLevel, _ := config.PriorityLevel(opts.MinPriority)
	cfg := logship.Config{Journal: opts.Journal, MinPriority: minLevel}
	for _, f := range opts.Files {
		cfg.Files = append(cfg.Files, logship.FileSpec{Path: f.Path, Format: f.Format})
	}
	for _, r := range opts.Drop {
		rule := logship.Rule{Log: r.Log, Program: r.Program}
		if level, ok := config.PriorityLevel(r.Priority); ok {
			rule.Priority = &level
		}
		if r.Message != "" {
			re, err := regexp.Compile(r.Message)
			if err != nil {
				return logship.Config{}, errDropPattern
			}
			rule.Message = re
		}
		cfg.Drop = append(cfg.Drop, rule)
	}
	return cfg, nil
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

// closeTimeout is how long closeSpool waits for the spool; tests replace it.
var closeTimeout = 5 * time.Second

// spoolSyncInterval is the interval of the spool's background sync.
const spoolSyncInterval = time.Second

// Fixed start errors of the boot ID; neither names a path or the content read.
var (
	errBootIDRead = errors.New("spool: boot id: not readable")
	errBootIDForm = errors.New("spool: boot id: not a lower-case UUID")
)

// openStorage opens the agent's state store and then the spool, both in opts.StateDir or else spool.directory. The spool is nil
// when opts.Sink is set. The store's barrier is the spool's Sync. It fails, and the agent does not start, when the boot ID cannot
// be read or the spool cannot be opened.
func openStorage(opts Options, cfg *config.Agent, log *slog.Logger) (*agentstate.Store, *spool.Spool, error) {
	stateDir := opts.StateDir
	if stateDir == "" {
		stateDir = cfg.Spool.Directory
	}
	store, _ := agentstate.Open(stateDir, log)
	if opts.Sink != nil {
		return store, nil, nil
	}
	proc := opts.Kernel.Proc
	if proc == nil {
		proc = os.DirFS("/proc")
	}
	bootID, err := procstats.ReadBootID(proc)
	if err != nil {
		if errors.Unwrap(err) == nil {
			return store, nil, errBootIDForm
		}
		return store, nil, errBootIDRead
	}
	sp, err := spool.Open(spool.Options{
		Dir:          stateDir,
		AgentID:      cfg.AgentID,
		BootID:       bootID,
		MaxBytes:     cfg.Spool.MaxBytes,
		MinFreeBytes: cfg.Spool.MinFreeBytes,
		SyncInterval: spoolSyncInterval,
	}, log)
	if err != nil {
		return store, nil, err
	}
	store.SetBarrier(sp.Sync)
	return store, sp, nil
}

// closeSpool closes the spool within closeTimeout and logs "spool not closed" otherwise; a nil spool is ignored.
func closeSpool(sp *spool.Spool, log *slog.Logger) {
	if sp == nil {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), closeTimeout)
	defer cancel()
	if err := sp.Close(ctx); err != nil {
		log.Warn("spool not closed", "component", "spool")
	}
}

// newScheduler returns the scheduler with the jobs of the configuration, taking the records into sink; the system collector
// switches its memory pressure mode and the kernel collector requests the extra runs of the processes and connections collectors.
func newScheduler(opts Options, cfg *config.Agent, store *agentstate.Store, sink scheduler.Putter, log *slog.Logger) (*scheduler.Scheduler, error) {
	list, system, err := jobs(opts, cfg, store, log)
	if err != nil {
		return nil, err
	}
	sched, err := scheduler.New(list, sink, log)
	if err != nil {
		return nil, err
	}
	system.SetPressureSwitch(sched.SetPressure)
	for _, j := range list {
		if k, ok := j.Collector.(*kernevents.Kernel); ok {
			k.SetTrigger(sched.Trigger)
		}
	}
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
