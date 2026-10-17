package sysstats

import (
	"context"
	"log/slog"
	"sync"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// System collects CPU, load, memory, pressure, vmstat and disk metrics; it implements scheduler.Collector.
type System struct {
	src Sources
	log *slog.Logger
	mu  sync.Mutex
}

// NewSystem returns the system collector reading src; a nil log logs nothing.
func NewSystem(src Sources, log *slog.Logger) *System {
	return &System{src: src, log: log}
}

// Name returns SystemName.
func (c *System) Name() string {
	return SystemName
}

// Collect takes one system sample.
func (c *System) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	return nil, errNotImplemented
}
