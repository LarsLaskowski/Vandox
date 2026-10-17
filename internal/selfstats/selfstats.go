// Package selfstats collects the agent's own resource use as metrics.
package selfstats

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"runtime"
	"syscall"
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

// statusFile is the file of the /proc tree that holds the resident set size.
const statusFile = "self/status"

// maxRSSDigits is the most digits of a VmRSS value: 999,999,999,999 kB times 1024 is below 2^53.
const maxRSSDigits = 12

// SpoolBytesReporter reports the size of the spool (implemented by the spool, #38).
type SpoolBytesReporter interface {
	SpoolBytes(ctx context.Context) (int64, error)
}

// SendLagReporter reports how far sending lags behind capture (implemented by the sender, #39).
type SendLagReporter interface {
	SendLag(ctx context.Context) (time.Duration, error)
}

// Sources are what the collector reads; a nil field takes its default.
type Sources struct {
	Proc       fs.FS                         // default os.DirFS("/proc"); self/status is read
	CPUTime    func() (time.Duration, error) // default ProcessCPUTime
	Goroutines func() int                    // default runtime.NumGoroutine
	Spool      SpoolBytesReporter            // nil: no spool metric
	Sender     SendLagReporter               // nil: no send lag metric
}

// Collector collects the self-metrics; it implements scheduler.Collector.
type Collector struct {
	src Sources
}

// New returns a collector reading src.
func New(src Sources) *Collector {
	if src.Proc == nil {
		src.Proc = os.DirFS("/proc")
	}
	if src.CPUTime == nil {
		src.CPUTime = ProcessCPUTime
	}
	if src.Goroutines == nil {
		src.Goroutines = runtime.NumGoroutine
	}
	return &Collector{src: src}
}

// Name returns Name.
func (c *Collector) Name() string {
	return Name
}

// Collect returns the self-metrics of one sample, or an error and no records.
func (c *Collector) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	rss, err := readRSS(c.src.Proc)
	if err != nil {
		return nil, err
	}
	cpu, err := c.src.CPUTime()
	if err != nil {
		return nil, fmt.Errorf("selfstats: cpu time: %w", err)
	}
	records := []model.Record{
		metric(run, MetricRSS, float64(rss), "bytes"),
		metric(run, MetricCPU, cpu.Seconds(), "seconds"),
		metric(run, MetricGoroutines, float64(c.src.Goroutines()), ""),
	}
	return c.appendProviders(ctx, run, records)
}

// appendProviders adds the metrics of the spool and sender providers that are set.
func (c *Collector) appendProviders(ctx context.Context, run scheduler.Run, records []model.Record) ([]model.Record, error) {
	if c.src.Spool != nil {
		size, err := c.src.Spool.SpoolBytes(ctx)
		if err != nil {
			return nil, fmt.Errorf("selfstats: spool size: %w", err)
		}
		if size < 0 {
			return nil, errors.New("selfstats: spool size: negative")
		}
		records = append(records, metric(run, MetricSpool, float64(size), "bytes"))
	}
	if c.src.Sender != nil {
		lag, err := c.src.Sender.SendLag(ctx)
		if err != nil {
			return nil, fmt.Errorf("selfstats: send lag: %w", err)
		}
		if lag < 0 {
			return nil, errors.New("selfstats: send lag: negative")
		}
		records = append(records, metric(run, MetricSendLag, lag.Seconds(), "seconds"))
	}
	return records, nil
}

// metric returns a metric record of the agent's source captured at the start of run.
func metric(run scheduler.Run, name string, value float64, unit string) model.Record {
	return model.Record{
		Meta: model.Meta{Source: scheduler.Source, CapturedAt: run.Start},
		Data: &model.MetricPoint{Name: name, Value: value, Unit: unit},
	}
}

// readRSS returns the resident set size in bytes from self/status of proc.
func readRSS(proc fs.FS) (int64, error) {
	f, err := proc.Open(statusFile)
	if err != nil {
		return 0, fmt.Errorf("selfstats: %s: %w", statusFile, err)
	}
	defer func() { _ = f.Close() }()

	data, err := io.ReadAll(io.LimitReader(f, MaxStatusBytes+1))
	if err != nil {
		return 0, fmt.Errorf("selfstats: %s: %w", statusFile, err)
	}
	if len(data) > MaxStatusBytes {
		return 0, fmt.Errorf("selfstats: %s: larger than %d bytes", statusFile, MaxStatusBytes)
	}
	for line := range bytes.SplitSeq(data, []byte{'\n'}) {
		if rest, ok := bytes.CutPrefix(line, []byte("VmRSS:")); ok {
			return parseKB(rest)
		}
	}
	return 0, fmt.Errorf("selfstats: %s: no VmRSS line", statusFile)
}

// parseKB parses the rest of the VmRSS line (blanks, 1 to 12 ASCII digits, " kB") into bytes.
func parseKB(rest []byte) (int64, error) {
	digits := bytes.TrimLeft(rest, " \t")
	n := 0
	for n < len(digits) && digits[n] >= '0' && digits[n] <= '9' {
		n++
	}
	var kb int64
	if n == 0 || n > maxRSSDigits || string(digits[n:]) != " kB" {
		return 0, fmt.Errorf("selfstats: %s: VmRSS: not a kB value of 1 to %d digits", statusFile, maxRSSDigits)
	}
	for _, d := range digits[:n] {
		kb = kb*10 + int64(d-'0')
	}
	return kb * 1024, nil
}

// ProcessCPUTime returns the user plus system CPU time this process has used (getrusage RUSAGE_SELF).
func ProcessCPUTime() (time.Duration, error) {
	var ru syscall.Rusage
	if err := syscall.Getrusage(syscall.RUSAGE_SELF, &ru); err != nil {
		return 0, fmt.Errorf("selfstats: getrusage: %w", err)
	}
	return time.Duration(ru.Utime.Nano() + ru.Stime.Nano()), nil
}
