// Package svcstats collects the state of the tracked systemd service units of the monitored server through the system
// D-Bus: the active and sub state, the time of the last activation and the restart counter, recorded on change.
package svcstats

import (
	"context"
	"errors"
	"log/slog"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Name, bounds and cadence of the systemd collector.
const (
	Name       = "systemd" // collector name and source of every record
	MaxTracked = 256       // tracked units, exact entries and pattern matches together
	Repeat     = time.Hour // age of the last record of a unit after which it is recorded again
)

// Unit is the state of one unit as the reader returns it.
type Unit struct {
	ID          string
	LoadState   string
	ActiveState string
	SubState    string
	ActiveEnter uint64 // microseconds since the epoch, 0 = never
	Restarts    uint32
}

// Reader reads unit states from one open connection to systemd.
type Reader interface {
	// List returns the units matching patterns with their states; ActiveEnter and Restarts are not read.
	List(ctx context.Context, patterns []string) ([]Unit, error)
	// Read returns one Unit per name, in order.
	Read(ctx context.Context, names []string) ([]Unit, error)
	// Close closes the connection.
	Close() error
}

// Systemd opens readers.
type Systemd interface {
	// Open opens a connection to systemd for one run.
	Open(ctx context.Context) (Reader, error)
}

// Sources are the inputs of the collector; a nil field takes its default.
type Sources struct {
	Systemd Systemd // default BusSystemd(sdbus.SystemBusPath)
}

// Collector collects the unit states; it implements scheduler.Collector.
type Collector struct {
	src      Sources
	units    []string
	schedule scheduler.Schedule
	log      *slog.Logger
}

// BusSystemd returns the Systemd that talks to the system bus at path.
func BusSystemd(path string) Systemd {
	return nil
}

// New returns the collector tracking units (full names or patterns), run on schedule; a nil log logs nothing.
func New(src Sources, units []string, schedule scheduler.Schedule, log *slog.Logger) *Collector {
	return &Collector{src: src, units: units, schedule: schedule, log: log}
}

// Name returns Name.
func (c *Collector) Name() string {
	return Name
}

// Collect reads the tracked units and returns the records of the units that changed.
func (c *Collector) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	return nil, errors.New("not implemented")
}
