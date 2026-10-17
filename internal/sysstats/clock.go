package sysstats

import (
	"context"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Clock collects the clock synchronization; it implements scheduler.Collector.
type Clock struct {
	src Sources
}

// NewClock returns the clock collector reading src.
func NewClock(src Sources) *Clock {
	return &Clock{src: src}
}

// Name returns ClockName.
func (c *Clock) Name() string {
	return ClockName
}

// Collect takes one sample of the clock synchronization.
func (c *Clock) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	return nil, errNotImplemented
}
