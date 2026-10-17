// Package procstats collects the process list of the monitored server from /proc: the top processes by memory and CPU,
// the sums per program and, under memory pressure, a full snapshot.
package procstats

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"os"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Names and bounds of the process collector.
const (
	Name           = "processes" // collector name
	Source         = "processes" // source of every record
	TopProcesses   = 25          // processes listed per ranking without memory pressure
	MaxProcesses   = 4096        // processes read per run
	ClockTicks     = 100         // USER_HZ
	UnnamedCommand = "(unnamed)" // command and program of a process with an empty command line and an empty comm
)

// errNotImplemented is returned by the parts of the collector that are not written yet.
var errNotImplemented = errors.New("not implemented")

// Sources are the file trees the collector reads; a nil field takes its default.
type Sources struct {
	Proc fs.FS // default os.DirFS("/proc")
	Etc  fs.FS // default os.DirFS("/etc")
}

// Rule is a configured rule that names the program of a process.
type Rule struct {
	Program        string
	Command        string
	CmdlinePrefix  string
	AppendNextWord bool
}

// Processes collects the process snapshot; it implements scheduler.Collector.
type Processes struct {
	src   Sources
	rules []Rule
	log   *slog.Logger
}

// New returns the process collector reading src and naming programs by rules; a nil log logs nothing.
func New(src Sources, rules []Rule, log *slog.Logger) *Processes {
	if src.Proc == nil {
		src.Proc = os.DirFS("/proc")
	}
	if src.Etc == nil {
		src.Etc = os.DirFS("/etc")
	}
	return &Processes{src: src, rules: rules, log: log}
}

// Name returns Name.
func (c *Processes) Name() string {
	return Name
}

// Collect takes one process snapshot.
func (c *Processes) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	return nil, errNotImplemented
}

// listPIDs returns the lowest MaxProcesses canonical PIDs of proc in ascending order; seen counts all PID directories.
func listPIDs(proc fs.FS) (pids []int32, seen int, err error) {
	return nil, 0, errNotImplemented
}
