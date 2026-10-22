// Package backups collects the backup runs of the monitored server: it watches the processes the configured rules
// name and writes the start and the end of each run.
package backups

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Name is the collector name and the source of every record.
const Name = "backups"

// Rule names the processes of a backup: a process matches when its command equals Command or its cleaned command
// line starts with CmdlinePrefix.
type Rule struct {
	Command       string
	CmdlinePrefix string
}

// Backups is the backup run collector.
type Backups struct {
	proc  fs.FS
	rules []Rule
	store *agentstate.Store
	log   *slog.Logger
}

// New returns the collector that reads the processes below proc, matches them against rules and keeps the open
// run in store.
func New(proc fs.FS, rules []Rule, store *agentstate.Store, log *slog.Logger) *Backups {
	return &Backups{proc: proc, rules: rules, store: store, log: log}
}

// Name returns the collector name.
func (b *Backups) Name() string {
	return Name
}

// Collect checks the processes and returns the backup_run records of the run.
func (b *Backups) Collect(_ context.Context, _ scheduler.Run) ([]model.Record, error) {
	return nil, errors.New("not implemented")
}
