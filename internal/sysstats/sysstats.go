// Package sysstats collects the system metrics of the monitored server from /proc, /sys and the read-only system calls statfs
// and adjtimex (record 0111).
package sysstats

import (
	"fmt"
	"io"
	"io/fs"
	"log/slog"
	"os"
	"strconv"
	"strings"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Source is the source of every record of the system collectors.
const Source = "system"

// Collector names.
const (
	SystemName     = "system"
	FilesystemName = "filesystem"
	ClockName      = "clock"
)

// MaxDisks is the most disks a system sample records.
const MaxDisks = 16

// maxDigits is the most digits of a counter: 2^64 - 1 has 20.
const maxDigits = 20

// Sources are what the collectors read; a nil field takes its default.
type Sources struct {
	Proc   fs.FS                              // default os.DirFS("/proc")
	Sys    fs.FS                              // default os.DirFS("/sys")
	StatFS func(path string) (FSStats, error) // default StatFS
	Clock  func() (ClockState, error)         // default ReadClock
}

// withDefaults returns s with every nil field set to its default.
func (s Sources) withDefaults() Sources {
	if s.Proc == nil {
		s.Proc = os.DirFS("/proc")
	}
	if s.Sys == nil {
		s.Sys = os.DirFS("/sys")
	}
	if s.StatFS == nil {
		s.StatFS = StatFS
	}
	if s.Clock == nil {
		s.Clock = ReadClock
	}
	return s
}

// logger returns log, or a logger that discards when log is nil.
func logger(log *slog.Logger) *slog.Logger {
	if log == nil {
		return slog.New(slog.DiscardHandler)
	}
	return log
}

// readFile reads at most limit bytes of the file name of fsys; a longer file is an error. The error names the file by its
// relative name and wraps the cause of an I/O failure.
func readFile(fsys fs.FS, name string, limit int) (string, error) {
	data, err := readLimited(fsys, name, limit+1)
	if err != nil {
		return "", err
	}
	if len(data) > limit {
		return "", fmt.Errorf("sysstats: %s: larger than %d bytes", name, limit)
	}
	return data, nil
}

// readLimited reads at most n bytes of the file name of fsys.
func readLimited(fsys fs.FS, name string, n int) (string, error) {
	f, err := fsys.Open(name)
	if err != nil {
		return "", fmt.Errorf("sysstats: %s: %w", name, err)
	}
	defer func() { _ = f.Close() }()

	data, err := io.ReadAll(io.LimitReader(f, int64(n)))
	if err != nil {
		return "", fmt.Errorf("sysstats: %s: %w", name, err)
	}
	return string(data), nil
}

// splitLines returns the lines of data; a final line feed does not start another line, and empty data has no line.
func splitLines(data string) []string {
	if data == "" {
		return nil
	}
	return strings.Split(strings.TrimSuffix(data, "\n"), "\n")
}

// isDigits reports whether s is 1 to maxDigits ASCII digits.
func isDigits(s string) bool {
	return len(s) >= 1 && len(s) <= maxDigits && allDigits(s)
}

// parseCounter parses s, 1 to 20 ASCII digits, as an unsigned 64-bit number.
func parseCounter(s string) (uint64, bool) {
	if !isDigits(s) {
		return 0, false
	}
	v, err := strconv.ParseUint(s, 10, 64)
	return v, err == nil
}

// recorder collects the metric records of one run.
type recorder struct {
	run     scheduler.Run
	records []model.Record
}

// add appends a metric record captured at the start of the run; kv are label keys and values.
func (r *recorder) add(name string, value float64, unit string, kv ...string) {
	var labels map[string]string
	if len(kv) > 0 {
		labels = make(map[string]string, len(kv)/2)
		for i := 0; i+1 < len(kv); i += 2 {
			labels[kv[i]] = kv[i+1]
		}
	}
	r.records = append(r.records, model.Record{
		Meta: model.Meta{Source: Source, CapturedAt: r.run.Start},
		Data: &model.MetricPoint{Name: name, Value: value, Unit: unit, Labels: labels},
	})
}
