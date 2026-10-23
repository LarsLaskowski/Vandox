// Package logship ships the lines of the systemd journal and of configured syslog files as log records (collector "logs").
package logship

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"os"
	"regexp"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Name is the collector name.
const Name = "logs"

var errNotImplemented = errors.New("logship: not implemented")

// Sources are the file trees and functions the collector reads through.
type Sources struct {
	Etc     fs.FS                               // machine-id and localtime; nil: os.DirFS("/etc")
	Journal []fs.FS                             // persistent, then volatile journal roots; nil: /var/log/journal, /run/log/journal
	Open    func(path string) (*os.File, error) // nil: OpenNoLinks
	Now     func() time.Time                    // nil: time.Now
}

// FileSpec is a configured log file.
type FileSpec struct{ Path, Format string }

// Rule drops the lines that match all of its set conditions.
type Rule struct {
	Log      string         // "" any, "journal" or a configured path
	Program  string         // "" any
	Priority *uint8         // nil any
	Message  *regexp.Regexp // nil any
}

// Config configures the collector.
type Config struct {
	Journal     bool
	MinPriority uint8
	Files       []FileSpec
	Drop        []Rule
}

// Logs is the log shipping collector.
type Logs struct {
	src   Sources
	cfg   Config
	store *agentstate.Store
	log   *slog.Logger
}

// New returns the collector.
func New(src Sources, cfg Config, store *agentstate.Store, log *slog.Logger) (*Logs, error) {
	return &Logs{src: src, cfg: cfg, store: store, log: log}, nil
}

// Name returns the collector's name.
func (l *Logs) Name() string {
	return Name
}

// Collect reads the new lines of the journal and the configured files.
func (l *Logs) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	return nil, errNotImplemented
}

// Kept commits the reading state of run when its records were kept.
func (l *Logs) Kept(run scheduler.Run, kept bool) {
	// Skeleton: the implementation commits or drops the fork of run.
}
