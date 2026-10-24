package selfstats_test

import (
	"context"
	"errors"
	"io/fs"
	"os"
	"strings"
	"testing"
	"testing/fstest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/selfstats"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// sampleStatusFile is the real /proc/self/status file captured on a running system (testdata/proc/self/status).
const sampleStatusFile = "../../testdata/proc/self/status"

// sampleRSSKB is the VmRSS value of the sample in kB.
const sampleRSSKB = 1632

// sampleVMRSSLine is the VmRSS line of the sample, without its line feed.
const sampleVMRSSLine = "VmRSS:\t    1632 kB"

// busySink keeps the busy loop of TestProcessCPUTime from being optimized away.
var busySink int

var runStart = time.Date(2026, time.March, 1, 12, 0, 0, 0, time.UTC)

type fakeSpool struct {
	bytes int64
	err   error
}

func (f fakeSpool) SpoolBytes(context.Context) (int64, error) { return f.bytes, f.err }

type fakeSender struct {
	lag time.Duration
	err error
}

func (f fakeSender) SendLag(context.Context) (time.Duration, error) { return f.lag, f.err }

type fakeSpoolAge struct {
	oldest time.Time
	found  bool
	err    error
}

func (f fakeSpoolAge) OldestCapture(context.Context) (time.Time, bool, error) {
	return f.oldest, f.found, f.err
}

func readSample(t *testing.T) string {
	t.Helper()

	data, err := os.ReadFile(sampleStatusFile)
	if err != nil {
		t.Fatalf("reading the sample %s: %v", sampleStatusFile, err)
	}
	if !strings.Contains(string(data), sampleVMRSSLine+"\n") {
		t.Fatalf("sample %s does not hold the line %q", sampleStatusFile, sampleVMRSSLine)
	}
	return string(data)
}

// sources returns sources that read status as self/status and take the fixed values of the plan's example.
func sources(status string) selfstats.Sources {
	return selfstats.Sources{
		Proc:       fstest.MapFS{"self/status": {Data: []byte(status)}},
		CPUTime:    func() (time.Duration, error) { return 1500 * time.Millisecond, nil },
		Goroutines: func() int { return 12 },
	}
}

// withVMRSS returns the sample with its VmRSS line replaced by line.
func withVMRSS(t *testing.T, line string) string {
	t.Helper()

	return strings.Replace(readSample(t), sampleVMRSSLine, line, 1)
}

func collect(t *testing.T, src selfstats.Sources) ([]model.Record, error) {
	t.Helper()

	return selfstats.New(src).Collect(context.Background(), scheduler.Run{Start: runStart})
}

type wantMetric struct {
	name  string
	value float64
	unit  string
}

func requireMetrics(t *testing.T, got []model.Record, want []wantMetric) {
	t.Helper()

	if len(got) != len(want) {
		t.Fatalf("Collect() returned %d records, want %d", len(got), len(want))
	}
	for i, w := range want {
		r := got[i]
		p, ok := r.Data.(*model.MetricPoint)
		if !ok {
			t.Errorf("record %d is a %q, want a metric", i, r.Kind())
			continue
		}
		if p.Name != w.name || p.Value != w.value || p.Unit != w.unit || len(p.Labels) != 0 {
			t.Errorf("record %d = name %q value %v unit %q labels %v, want name %q value %v unit %q and no labels",
				i, p.Name, p.Value, p.Unit, p.Labels, w.name, w.value, w.unit)
		}
		if r.Source != "agent" || !r.CapturedAt.Equal(runStart) {
			t.Errorf("record %d (%s) source %q captured_at %v, want source agent captured_at %v",
				i, w.name, r.Source, r.CapturedAt, runStart)
		}
		checked := r
		checked.Origin, checked.Seq = model.OriginAgent, 1
		if _, err := wire.CheckRecord(&checked); err != nil {
			t.Errorf("record %d (%s): wire.CheckRecord with origin agent and seq 1 = %v, want nil", i, w.name, err)
		}
	}
}

func TestCollector_RecordsSelfMetrics(t *testing.T) {
	src := selfstats.Sources{
		Proc:       os.DirFS("../../testdata/proc"),
		CPUTime:    func() (time.Duration, error) { return 1500 * time.Millisecond, nil },
		Goroutines: func() int { return 12 },
		Spool:      fakeSpool{bytes: 4096},
		Sender:     fakeSender{lag: 2500 * time.Millisecond},
	}
	_ = readSample(t)

	got, err := collect(t, src)

	if err != nil {
		t.Fatalf("Collect() error = %v, want nil", err)
	}
	requireMetrics(t, got, []wantMetric{
		{selfstats.MetricRSS, sampleRSSKB * 1024, "bytes"},
		{selfstats.MetricCPU, 1.5, "seconds"},
		{selfstats.MetricGoroutines, 12, ""},
		{selfstats.MetricSpool, 4096, "bytes"},
		{selfstats.MetricSendLag, 2.5, "seconds"},
	})
}

func TestCollector_WithoutSpoolAndSender(t *testing.T) {
	src := sources(readSample(t))

	got, err := collect(t, src)

	if err != nil {
		t.Fatalf("Collect() error = %v, want nil", err)
	}
	requireMetrics(t, got, []wantMetric{
		{selfstats.MetricRSS, sampleRSSKB * 1024, "bytes"},
		{selfstats.MetricCPU, 1.5, "seconds"},
		{selfstats.MetricGoroutines, 12, ""},
	})
}

const statusPrefix = "selfstats: self/status: "

// statusCase is a self/status variant and the error Collect must return for it.
type statusCase struct {
	name   string
	status func(t *testing.T) string
	want   string
}

// valueCase is a replacement of the VmRSS line.
type valueCase struct{ name, line string }

func rejectedStatusCases() []statusCase {
	errNoLine := statusPrefix + "no VmRSS line"
	errValue := statusPrefix + "VmRSS: not a kB value of 1 to 12 digits"
	cases := []statusCase{
		{"line removed", func(t *testing.T) string { return withVMRSS(t, "") }, errNoLine},
		{"other key VmRSSx", func(t *testing.T) string { return withVMRSS(t, "VmRSSx:\t    1632 kB") }, errNoLine},
		{"larger than the cap", func(t *testing.T) string {
			s := readSample(t)
			return s + strings.Repeat("x", 65537-len(s))
		}, statusPrefix + "larger than 65536 bytes"},
	}
	values := []valueCase{
		{"negative", "VmRSS:\t    -1 kB"},
		{"plus sign", "VmRSS:\t    +1 kB"},
		{"decimal", "VmRSS:\t    1.5 kB"},
		{"letters", "VmRSS:\t    abc kB"},
		{"13 digits", "VmRSS:\t    1234567890123 kB"},
		{"unit MB", "VmRSS:\t    1632 MB"},
		{"trailing space after unit", "VmRSS:\t    1632 kB "},
		{"no unit", "VmRSS:\t    1632"},
		{"no digits", "VmRSS:\t     kB"},
		{"full-width digits", "VmRSS:\t    \uff11\uff12 kB"},
	}
	for _, v := range values {
		cases = append(cases, statusCase{
			"value " + v.name, func(t *testing.T) string { return withVMRSS(t, v.line) }, errValue,
		})
	}
	return cases
}

func TestCollector_StatusErrors(t *testing.T) {
	t.Run("rejected", testStatusRejected)
	t.Run("accepted", testStatusAccepted)
	t.Run("exactly 65536 bytes", testStatusAtTheCap)
	t.Run("missing file", testStatusMissing)
}

func testStatusRejected(t *testing.T) {
	for _, tc := range rejectedStatusCases() {
		t.Run(tc.name, func(t *testing.T) {
			got, err := collect(t, sources(tc.status(t)))

			if err == nil || err.Error() != tc.want {
				t.Errorf("Collect() error = %v, want %q", err, tc.want)
			}
			if len(got) != 0 {
				t.Errorf("Collect() returned %d records with an error, want none", len(got))
			}
		})
	}
}

func testStatusAccepted(t *testing.T) {
	tests := []struct {
		name string
		line string
		kb   int64
	}{
		{"real format", sampleVMRSSLine, sampleRSSKB},
		{"one tab", "VmRSS:\t7 kB", 7},
		{"spaces only", "VmRSS:   7 kB", 7},
		{"tabs and spaces", "VmRSS:\t \t 7 kB", 7},
		{"12 digits", "VmRSS:\t999999999999 kB", 999999999999},
		{"zero", "VmRSS:\t       0 kB", 0},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got, err := collect(t, sources(withVMRSS(t, tc.line)))

			if err != nil {
				t.Fatalf("Collect() error = %v, want nil", err)
			}
			requireFirstMetric(t, got, selfstats.MetricRSS, float64(tc.kb*1024))
		})
	}
}

func testStatusAtTheCap(t *testing.T) {
	s := readSample(t)

	got, err := collect(t, sources(s+strings.Repeat("x", 65536-len(s))))

	if err != nil || len(got) != 3 {
		t.Errorf("Collect() of a file of exactly 65536 bytes = %d records, error %v, want 3 records and nil", len(got), err)
	}
}

func testStatusMissing(t *testing.T) {
	src := sources("")
	src.Proc = fstest.MapFS{}

	got, err := collect(t, src)

	if err == nil || !errors.Is(err, fs.ErrNotExist) || !strings.HasPrefix(err.Error(), statusPrefix) {
		t.Errorf("Collect() error = %v, want an fs.ErrNotExist error starting with %q", err, statusPrefix)
	}
	if len(got) != 0 {
		t.Errorf("Collect() returned %d records with an error, want none", len(got))
	}
}

// requireFirstMetric checks the name and value of the first record.
func requireFirstMetric(t *testing.T, got []model.Record, name string, value float64) {
	t.Helper()

	if len(got) == 0 {
		t.Fatalf("Collect() returned no records, want the self-metrics")
	}
	p, ok := got[0].Data.(*model.MetricPoint)
	if !ok || p.Name != name || p.Value != value {
		t.Errorf("first record = %+v, want %s = %v", got[0].Data, name, value)
	}
}

func TestCollector_ProviderErrors(t *testing.T) {
	boom := errors.New("boom")
	with := func(mod func(*selfstats.Sources)) selfstats.Sources {
		src := sources(readSample(t))
		src.Spool = fakeSpool{bytes: 1}
		src.Sender = fakeSender{lag: time.Second}
		mod(&src)
		return src
	}
	tests := []struct {
		name    string
		src     selfstats.Sources
		want    string
		wrapped bool
	}{
		{
			"cpu time", with(func(s *selfstats.Sources) { s.CPUTime = func() (time.Duration, error) { return 0, boom } }),
			"selfstats: cpu time: boom", true,
		},
		{
			"spool error", with(func(s *selfstats.Sources) { s.Spool = fakeSpool{err: boom} }),
			"selfstats: spool size: boom", true,
		},
		{
			"negative spool size", with(func(s *selfstats.Sources) { s.Spool = fakeSpool{bytes: -1} }),
			"selfstats: spool size: negative", false,
		},
		{
			"send error", with(func(s *selfstats.Sources) { s.Sender = fakeSender{err: boom} }),
			"selfstats: send lag: boom", true,
		},
		{
			"negative send lag", with(func(s *selfstats.Sources) { s.Sender = fakeSender{lag: -time.Nanosecond} }),
			"selfstats: send lag: negative", false,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got, err := collect(t, tc.src)

			if err == nil || err.Error() != tc.want {
				t.Errorf("Collect() error = %v, want %q", err, tc.want)
			}
			if tc.wrapped && !errors.Is(err, boom) {
				t.Errorf("Collect() error = %v, want it to wrap the provider's error", err)
			}
			if len(got) != 0 {
				t.Errorf("Collect() returned %d records with an error, want none", len(got))
			}
		})
	}
}

func TestCollect_SpoolAge(t *testing.T) {
	tests := []struct {
		name string
		age  fakeSpoolAge
		want float64
	}{
		{"oldest 90.5 seconds before the start", fakeSpoolAge{oldest: runStart.Add(-90500 * time.Millisecond), found: true}, 90.5},
		{"oldest at the start", fakeSpoolAge{oldest: runStart, found: true}, 0},
		{"oldest after the start", fakeSpoolAge{oldest: runStart.Add(time.Hour), found: true}, 0},
		{"empty spool", fakeSpoolAge{found: false}, 0},
		{"empty spool ignores the time", fakeSpoolAge{oldest: runStart.Add(-time.Hour), found: false}, 0},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			src := sources(readSample(t))
			src.Spool = fakeSpool{bytes: 4096}
			src.SpoolAge = tc.age

			got, err := collect(t, src)

			if err != nil {
				t.Fatalf("Collect() error = %v, want nil", err)
			}
			requireMetrics(t, got, []wantMetric{
				{selfstats.MetricRSS, sampleRSSKB * 1024, "bytes"},
				{selfstats.MetricCPU, 1.5, "seconds"},
				{selfstats.MetricGoroutines, 12, ""},
				{selfstats.MetricSpool, 4096, "bytes"},
				{selfstats.MetricSpoolAge, tc.want, "seconds"},
			})
		})
	}
}

func TestCollect_SpoolAgeWithoutSpoolBytes(t *testing.T) {
	src := sources(readSample(t))
	src.SpoolAge = fakeSpoolAge{oldest: runStart.Add(-2 * time.Second), found: true}

	got, err := collect(t, src)

	if err != nil {
		t.Fatalf("Collect() error = %v, want nil", err)
	}
	requireMetrics(t, got, []wantMetric{
		{selfstats.MetricRSS, sampleRSSKB * 1024, "bytes"},
		{selfstats.MetricCPU, 1.5, "seconds"},
		{selfstats.MetricGoroutines, 12, ""},
		{selfstats.MetricSpoolAge, 2, "seconds"},
	})
}

func TestCollect_SpoolAgeNilEmitsNoMetric(t *testing.T) {
	src := sources(readSample(t))
	src.Spool = fakeSpool{bytes: 1}

	got, err := collect(t, src)

	if err != nil {
		t.Fatalf("Collect() error = %v, want nil", err)
	}
	for _, r := range got {
		if p, ok := r.Data.(*model.MetricPoint); ok && p.Name == selfstats.MetricSpoolAge {
			t.Errorf("Collect() holds %s without a SpoolAge provider, want none", selfstats.MetricSpoolAge)
		}
	}
}

func TestCollect_SpoolAgeProviderError(t *testing.T) {
	boom := errors.New("boom")
	src := sources(readSample(t))
	src.Spool = fakeSpool{bytes: 1}
	src.SpoolAge = fakeSpoolAge{err: boom}

	got, err := collect(t, src)

	if !errors.Is(err, boom) {
		t.Errorf("Collect() error = %v, want it to wrap the provider's error", err)
	}
	if err != nil && !strings.HasPrefix(err.Error(), "selfstats: spool age: ") {
		t.Errorf("Collect() error = %q, want the prefix %q", err, "selfstats: spool age: ")
	}
	if len(got) != 0 {
		t.Errorf("Collect() returned %d records with an error, want none", len(got))
	}
}

func TestNew_Defaults(t *testing.T) {
	c := selfstats.New(selfstats.Sources{})

	if got := c.Name(); got != "self" {
		t.Errorf("Name() = %q, want %q", got, "self")
	}
	got, err := c.Collect(context.Background(), scheduler.Run{Start: runStart})

	if err != nil {
		t.Fatalf("Collect() with the default sources error = %v, want nil", err)
	}
	if len(got) != 3 {
		t.Fatalf("Collect() with the default sources returned %d records, want 3", len(got))
	}
	rss, okRSS := got[0].Data.(*model.MetricPoint)
	goroutines, okG := got[2].Data.(*model.MetricPoint)
	if !okRSS || !okG || rss.Name != selfstats.MetricRSS || rss.Value <= 0 ||
		goroutines.Name != selfstats.MetricGoroutines || goroutines.Value < 1 {
		t.Errorf("default metrics = %+v and %+v, want %s > 0 and %s >= 1",
			got[0].Data, got[2].Data, selfstats.MetricRSS, selfstats.MetricGoroutines)
	}
}

func TestProcessCPUTime(t *testing.T) {
	first, err := selfstats.ProcessCPUTime()
	if err != nil {
		t.Fatalf("ProcessCPUTime() error = %v, want nil", err)
	}
	if first < 0 {
		t.Errorf("ProcessCPUTime() = %v, want >= 0", first)
	}

	for i := range 20_000_000 {
		busySink += i % 7
	}
	second, err := selfstats.ProcessCPUTime()

	if err != nil {
		t.Fatalf("ProcessCPUTime() after a busy loop error = %v, want nil", err)
	}
	if second < first {
		t.Errorf("ProcessCPUTime() after a busy loop = %v, want it not smaller than the first value %v",
			second, first)
	}
}
