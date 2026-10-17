// Package selfstats collects the agent's own resource use as metrics.
package selfstats

import (
	"context"
	"errors"
	"io/fs"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Name is the collector's name.
const Name = "self"

// Metric names.
const (
	MetricRSS        = "agent_rss_bytes"
	MetricCPU        = "agent_cpu_seconds_total"
	MetricGoroutines = "agent_goroutines"
	MetricSpool      = "agent_spool_bytes"
	MetricSendLag    = "agent_send_lag_seconds"
)

// MaxStatusBytes is the most of self/status that is read.
const MaxStatusBytes = 64 << 10

var errNotImplemented = errors.New("not implemented")

// SpoolStats reports the size of the spool (implemented by the spool, #38).
type SpoolStats interface {
	SpoolBytes(ctx context.Context) (int64, error)
}

// SendStats reports how far sending lags behind capture (implemented by the sender, #39).
type SendStats interface {
	SendLag(ctx context.Context) (time.Duration, error)
}

// Sources are what the collector reads; a nil field takes its default.
type Sources struct {
	Proc       fs.FS                         // default os.DirFS("/proc"); self/status is read
	CPUTime    func() (time.Duration, error) // default ProcessCPUTime
	Goroutines func() int                    // default runtime.NumGoroutine
	Spool      SpoolStats                    // nil: no spool metric
	Sender     SendStats                     // nil: no send lag metric
}

// Collector collects the self-metrics; it implements scheduler.Collector.
type Collector struct {
	src Sources
}

// New returns a collector reading src.
func New(src Sources) *Collector {
	return &Collector{src: src}
}

// Name returns Name.
func (c *Collector) Name() string {
	return ""
}

// Collect returns the self-metrics of one sample, or an error and no records.
func (c *Collector) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	return nil, errNotImplemented
}

// ProcessCPUTime returns the user plus system CPU time this process has used (getrusage RUSAGE_SELF).
func ProcessCPUTime() (time.Duration, error) {
	return 0, errNotImplemented
}
