package mariadb

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Name is the name of the collector.
const Name = "mariadb"

// Budgets and bounds of the collector.
const (
	GreetingBudget   = 2 * time.Second
	RunBudget        = 4 * time.Second
	ThreadsThreshold = 10
	MaxThreads       = 4096
	MaxInfoThreads   = 64
	MaxInfoBytes     = 16384
	RestartSlack     = 5 * time.Second
)

// Sources are the sources of the collector; zero fields take their defaults.
type Sources struct {
	Dial Dialer // default UnixDialer(socket)
	Proc fs.FS  // default os.DirFS("/proc")
}

// Collector collects the state of the database.
type Collector struct {
	src    Sources
	socket string
	log    *slog.Logger
}

// New returns a Collector for the database at socket.
func New(src Sources, socket string, log *slog.Logger) *Collector {
	return &Collector{src: src, socket: socket, log: log}
}

// Name returns Name.
func (c *Collector) Name() string { return Name }

// Collect takes one sample.
func (c *Collector) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	_, _ = ctx, run
	return nil, errors.New("not implemented")
}

// classify maps err to an availability and an error code; ok is false for an error that is no answer of the database.
func classify(err error) (availability model.MariaDBAvailability, code uint16, ok bool) {
	_ = err
	return "", 0, false
}
