package sysstats_test

import (
	"context"
	"errors"
	"go/ast"
	"go/parser"
	"go/token"
	"io/fs"
	"log/slog"
	"math"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"testing"
	"testing/fstest"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/sysstats"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// The fixture trees are real captures of a Linux 6.18 kernel (testdata/proc, testdata/sys).
const (
	procDir = "../../testdata/proc"
	sysDir  = "../../testdata/sys"
)

// runStart is the start of the first run of a test.
var runStart = time.Date(2026, time.March, 1, 12, 0, 0, 0, time.UTC)

// Values of the fixture tree in kB.
const (
	kib          = 1024
	memTotalKB   = 16479424
	memFreeKB    = 15539700
	memAvailKB   = 15901132
	buffersKB    = 33240
	cachedKB     = 511576
	sreclaimKB   = 103776
	fixtureLines = 54 // lines of testdata/proc/meminfo
)

// Counters of the disk vda of the fixture tree (fields 1, 3, 5, 7 and 10 of block/vda/stat).
const (
	vdaReads         = 14573
	vdaReadSectors   = 1068218
	vdaWrites        = 8833
	vdaWriteSectors  = 6437752
	vdaIOMilliSecond = 6764
)

// Texts of the fixture that variants replace.
const (
	fixtureCPU = "cpu  28110 0 10232 889261 519 0 1222 1564 0 0"
	goodSome   = "some avg10=0.00 avg60=0.00 avg300=0.00 total=0"
	goodFull   = "full avg10=0.00 avg60=0.00 avg300=0.00 total=0"
)

// cpuModes is the order of the cpu_percent records.
var cpuModes = []string{"user", "system", "nice", "idle", "iowait", "irq", "softirq", "steal"}

// wantRec is the expected content of one metric record.
type wantRec struct {
	name   string
	value  float64
	unit   string
	labels map[string]string
}

// rec returns a wantRec with the labels kv (key, value, key, value, ...).
func rec(name string, value float64, unit string, kv ...string) wantRec {
	labels := map[string]string{}
	for i := 0; i+1 < len(kv); i += 2 {
		labels[kv[i]] = kv[i+1]
	}
	return wantRec{name: name, value: value, unit: unit, labels: labels}
}

func (w wantRec) String() string {
	return w.name + "=" + strconv.FormatFloat(w.value, 'g', -1, 64) + "/" + w.unit + mapString(w.labels)
}

func mapString(m map[string]string) string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	slices.Sort(keys)
	var b strings.Builder
	b.WriteString("{")
	for _, k := range keys {
		b.WriteString(k + "=" + m[k] + ",")
	}
	b.WriteString("}")
	return b.String()
}

// near reports whether got and want agree to 1e-9 relative.
func near(got, want float64) bool {
	return math.Abs(got-want) <= 1e-9*math.Max(1, math.Abs(want))
}

// gotRec is the content of one collected record.
func gotRec(r model.Record) (wantRec, bool) {
	p, ok := r.Data.(*model.MetricPoint)
	if !ok {
		return wantRec{}, false
	}
	return wantRec{name: p.Name, value: p.Value, unit: p.Unit, labels: p.Labels}, true
}

func sameLabels(a, b map[string]string) bool {
	if len(a) != len(b) {
		return false
	}
	for k, v := range a {
		if w, ok := b[k]; !ok || w != v {
			return false
		}
	}
	return true
}

func describe(records []model.Record) string {
	parts := make([]string, len(records))
	for i, r := range records {
		if g, ok := gotRec(r); ok {
			parts[i] = g.String()
		} else {
			parts[i] = "<" + string(r.Kind()) + ">"
		}
	}
	return strings.Join(parts, " ")
}

// requireRecords checks that got holds exactly the metric records want, in order, with source system, captured at at, and that
// each passes the wire check once stamped with origin agent and sequence number 1.
func requireRecords(t *testing.T, at time.Time, got []model.Record, want []wantRec) {
	t.Helper()

	if len(got) != len(want) {
		t.Errorf("Collect() returned %d records, want %d\ngot:  %s\nwant: %v", len(got), len(want), describe(got), want)
		return
	}
	for i, w := range want {
		g, ok := gotRec(got[i])
		if !ok {
			t.Errorf("record %d is a %q, want the metric %s", i, got[i].Kind(), w)
			continue
		}
		if g.name != w.name || g.unit != w.unit || !near(g.value, w.value) || !sameLabels(g.labels, w.labels) {
			t.Errorf("record %d = %s, want %s", i, g, w)
		}
		requireStamp(t, i, got[i], at)
	}
}

// requireStamp checks the source, the capture time and the wire check of record i.
func requireStamp(t *testing.T, i int, r model.Record, at time.Time) {
	t.Helper()

	if r.Source != sysstats.Source || !r.CapturedAt.Equal(at) {
		t.Errorf("record %d source %q captured_at %v, want source %q captured_at %v", i, r.Source, r.CapturedAt, sysstats.Source, at)
	}
	checked := r
	checked.Origin, checked.Seq = model.OriginAgent, 1
	if _, err := wire.CheckRecord(&checked); err != nil {
		t.Errorf("record %d: wire.CheckRecord with origin agent and seq 1 = %v, want nil", i, err)
	}
}

// find returns the metric records named name.
func find(records []model.Record, name string) []wantRec {
	var out []wantRec
	for _, r := range records {
		if g, ok := gotRec(r); ok && g.name == name {
			out = append(out, g)
		}
	}
	return out
}

// hasPrefix reports whether any metric record has a name starting with prefix.
func hasPrefix(records []model.Record, prefix string) bool {
	for _, r := range records {
		if g, ok := gotRec(r); ok && strings.HasPrefix(g.name, prefix) {
			return true
		}
	}
	return false
}

// drop returns list without the records for which gone reports true.
func drop(list []wantRec, gone func(wantRec) bool) []wantRec {
	var out []wantRec
	for _, w := range list {
		if !gone(w) {
			out = append(out, w)
		}
	}
	return out
}

// collect runs one sample of c that starts at start.
func collect(c scheduler.Collector, start time.Time) ([]model.Record, error) {
	return c.Collect(context.Background(), scheduler.Run{Start: start})
}

// mustCollect runs one sample of c and fails the test if it fails.
func mustCollect(t *testing.T, c scheduler.Collector, start time.Time) []model.Record {
	t.Helper()

	got, err := collect(c, start)
	if err != nil {
		t.Fatalf("Collect() error = %v, want nil", err)
	}
	return got
}

// requireFails checks that a sample fails with exactly the error text want and records nothing.
func requireFails(t *testing.T, c scheduler.Collector, start time.Time, want string) {
	t.Helper()

	got, err := collect(c, start)
	if err == nil {
		t.Fatalf("Collect() error = nil with %d records, want the error %q", len(got), want)
	}
	if err.Error() != want {
		t.Errorf("Collect() error = %q, want %q", err.Error(), want)
	}
	if len(got) != 0 {
		t.Errorf("Collect() returned %d records with its error, want none", len(got))
	}
}

// tree is the fixture trees in memory, to be varied by a test.
type tree struct {
	proc fstest.MapFS
	sys  fstest.MapFS
}

// loadDir reads all files below dir into a MapFS.
func loadDir(t *testing.T, dir string) fstest.MapFS {
	t.Helper()

	files := fstest.MapFS{}
	err := filepath.WalkDir(dir, func(path string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return err
		}
		rel, err := filepath.Rel(dir, path)
		if err != nil {
			return err
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		files[filepath.ToSlash(rel)] = &fstest.MapFile{Data: data}
		return nil
	})
	if err != nil {
		t.Fatalf("reading the fixture tree %s: %v", dir, err)
	}
	return files
}

func loadTree(t *testing.T) *tree {
	t.Helper()

	return &tree{proc: loadDir(t, procDir), sys: loadDir(t, sysDir)}
}

func (tr *tree) sources() sysstats.Sources {
	return sysstats.Sources{Proc: tr.proc, Sys: tr.sys}
}

// text returns the content of the file name of files.
func text(t *testing.T, files fstest.MapFS, name string) string {
	t.Helper()

	f, ok := files[name]
	if !ok {
		t.Fatalf("the fixture tree has no file %s", name)
	}
	return string(f.Data)
}

func set(files fstest.MapFS, name, content string) {
	files[name] = &fstest.MapFile{Data: []byte(content)}
}

// edit replaces the one occurrence of old in the file name of files by repl.
func edit(t *testing.T, files fstest.MapFS, name, old, repl string) {
	t.Helper()

	content := text(t, files, name)
	if n := strings.Count(content, old); n != 1 {
		t.Fatalf("the fixture file %s holds %q %d times, want once", name, old, n)
	}
	set(files, name, strings.Replace(content, old, repl, 1))
}

// replaceLine replaces the line that starts with prefix in the file name of files by line (a deletion for ""), and fails if there
// is none.
func replaceLine(t *testing.T, files fstest.MapFS, name, prefix, line string) {
	t.Helper()

	lines := strings.SplitAfter(text(t, files, name), "\n")
	for i, l := range lines {
		if !strings.HasPrefix(l, prefix) {
			continue
		}
		if line != "" {
			line += "\n"
		}
		lines[i] = line
		set(files, name, strings.Join(lines, ""))
		return
	}
	t.Fatalf("the fixture file %s has no line starting with %q", name, prefix)
}

// lineNumber returns the 1-based number of the line of the file name of files that starts with prefix.
func lineNumber(t *testing.T, files fstest.MapFS, name, prefix string) int {
	t.Helper()

	for i, l := range strings.Split(text(t, files, name), "\n") {
		if strings.HasPrefix(l, prefix) {
			return i + 1
		}
	}
	t.Fatalf("the fixture file %s has no line starting with %q", name, prefix)
	return 0
}

// statText returns the content of a block device stat file of 17 fields with the given counters and the other fields of the
// fixture.
func statText(reads, readSectors, writes, writeSectors, ioMs uint64) string {
	fields := []uint64{reads, 8362, readSectors, 3840, writes, 74381, writeSectors, 39502, 0, ioMs, 45032, 5911, 0, 1422192, 1429, 114, 259}
	parts := make([]string, len(fields))
	for i, f := range fields {
		parts[i] = strconv.FormatUint(f, 10)
	}
	return strings.Join(parts, " ") + "\n"
}

// addDisk adds a disk named name with a device entry and the stat content.
func (tr *tree) addDisk(name, stat string) {
	set(tr.sys, "block/"+name+"/stat", stat)
	set(tr.sys, "block/"+name+"/device/uevent", "DRIVER=virtio_blk\n")
}

// logEntry is one line a collector logged.
type logEntry struct {
	level slog.Level
	msg   string
	attrs map[string]string
}

// logStore is a slog.Handler that keeps the entries (groups are flattened).
type logStore struct {
	mu      sync.Mutex
	entries []logEntry
	attrs   []slog.Attr
	root    *logStore
}

func newLogStore() (*logStore, *slog.Logger) {
	s := &logStore{}
	s.root = s
	return s, slog.New(s)
}

func (s *logStore) Enabled(context.Context, slog.Level) bool { return true }

func (s *logStore) Handle(_ context.Context, r slog.Record) error {
	e := logEntry{level: r.Level, msg: r.Message, attrs: map[string]string{}}
	for _, a := range s.attrs {
		e.attrs[a.Key] = a.Value.Resolve().String()
	}
	r.Attrs(func(a slog.Attr) bool {
		e.attrs[a.Key] = a.Value.Resolve().String()
		return true
	})
	s.root.mu.Lock()
	s.root.entries = append(s.root.entries, e)
	s.root.mu.Unlock()
	return nil
}

func (s *logStore) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &logStore{attrs: append(slices.Clone(s.attrs), attrs...), root: s.root}
}

func (s *logStore) WithGroup(string) slog.Handler { return s }

// logged returns the entries of level info and above so far.
func (s *logStore) logged() []logEntry {
	s.root.mu.Lock()
	defer s.root.mu.Unlock()

	var out []logEntry
	for _, e := range s.root.entries {
		if e.level >= slog.LevelInfo {
			out = append(out, e)
		}
	}
	return out
}

// wantLog is an expected log line; the attributes in attrs must match, others may be present.
type wantLog struct {
	level slog.Level
	msg   string
	attrs map[string]string
}

// requireLogged checks that the entries of level info and above are exactly want, in order.
func requireLogged(t *testing.T, s *logStore, when string, want []wantLog) {
	t.Helper()

	got := s.logged()
	if len(got) != len(want) {
		t.Errorf("%s: %d log lines %v, want %d %v", when, len(got), got, len(want), want)
		return
	}
	for i, w := range want {
		g := got[i]
		if g.level != w.level || g.msg != w.msg {
			t.Errorf("%s: log line %d = %s %q, want %s %q", when, i, g.level, g.msg, w.level, w.msg)
		}
		for k, v := range w.attrs {
			if g.attrs[k] != v {
				t.Errorf("%s: log line %d (%q) attribute %s = %q, want %q (attributes %v)", when, i, g.msg, k, g.attrs[k], v, g.attrs)
			}
		}
	}
}

// failFS answers the named files with an error at Open or at the first Read.
type failFS struct {
	fs.FS
	openErr map[string]error
	readErr map[string]error
}

func (f failFS) Open(name string) (fs.File, error) {
	if err, ok := f.openErr[name]; ok {
		return nil, &fs.PathError{Op: "open", Path: name, Err: err}
	}
	file, err := f.FS.Open(name)
	if err != nil {
		return nil, err
	}
	if rerr, ok := f.readErr[name]; ok {
		return failingFile{File: file, err: &fs.PathError{Op: "read", Path: name, Err: rerr}}, nil
	}
	return file, nil
}

// failingFile is a file whose Read fails.
type failingFile struct {
	fs.File
	err error
}

func (f failingFile) Read([]byte) (int, error) { return 0, f.err }

// gate blocks callers until it is opened and counts how often it was entered; it is opened when the test ends.
type gate struct {
	release chan struct{}
	once    sync.Once
	entered atomic.Int32
}

func newGate(t *testing.T) *gate {
	t.Helper()

	g := &gate{release: make(chan struct{})}
	t.Cleanup(g.open)
	return g
}

func (g *gate) wait() {
	g.entered.Add(1)
	<-g.release
}

func (g *gate) open() { g.once.Do(func() { close(g.release) }) }

// blockingFS blocks the Open of one file on a gate.
type blockingFS struct {
	fs.FS
	name string
	gate *gate
}

func (b blockingFS) Open(name string) (fs.File, error) {
	if name == b.name {
		b.gate.wait()
	}
	return b.FS.Open(name)
}

// statFake answers statfs from a script and remembers the paths asked.
type statFake struct {
	mu    sync.Mutex
	stats map[string]sysstats.FSStats
	errs  map[string]error
	calls []string
}

func (f *statFake) statFS(path string) (sysstats.FSStats, error) {
	f.mu.Lock()
	defer f.mu.Unlock()

	f.calls = append(f.calls, path)
	if err, ok := f.errs[path]; ok {
		return sysstats.FSStats{}, err
	}
	return f.stats[path], nil
}

func (f *statFake) called() []string {
	f.mu.Lock()
	defer f.mu.Unlock()

	return slices.Clone(f.calls)
}

// hangingSample runs the collector built by build, whose source blocks on the gate it is given, next to a collector that always
// succeeds in a scheduler for 35 seconds (interval 10 s, timeout 2 s) and checks that only its own samples are lost. The
// collector and the gate are created in the bubble because a channel from outside it never counts as blocked.
func hangingSample(t *testing.T, build func(t *testing.T, g *gate) scheduler.Collector) {
	t.Helper()

	synctest.Test(t, func(t *testing.T) {
		g := newGate(t)
		c := build(t, g)
		sink := &collectSink{}
		schedule := scheduler.Schedule{Interval: 10 * time.Second, Timeout: 2 * time.Second}
		jobs := []scheduler.Job{{Collector: c, Schedule: schedule}, {Collector: &okCollector{}, Schedule: schedule}}
		sched, err := scheduler.New(jobs, sink, slog.New(slog.DiscardHandler))
		if err != nil {
			t.Fatalf("scheduler.New() error = %v, want nil", err)
		}
		ctx, cancel := context.WithCancel(context.Background())
		done := make(chan struct{})
		go func() {
			defer close(done)
			sched.Run(ctx)
		}()
		t.Cleanup(func() {
			g.open()
			cancel()
			<-done
		})

		time.Sleep(35 * time.Second)
		synctest.Wait()

		gaps := sink.gaps(c.Name())
		if len(gaps) != 4 {
			t.Errorf("gaps of %q = %d, want 4 (the first run and the 3 skipped runs)", c.Name(), len(gaps))
		}
		for i, gap := range gaps {
			if gap.Cause != model.GapCollectorTimeout {
				t.Errorf("gap %d of %q has cause %q, want %q", i, c.Name(), gap.Cause, model.GapCollectorTimeout)
			}
		}
		if got := g.entered.Load(); got != 1 {
			t.Errorf("the blocked source was entered %d times, want 1 (a stuck collector is not started again)", got)
		}
		if got := sink.count("ok"); got != 4 {
			t.Errorf("samples of the other collector = %d, want 4 (one per interval)", got)
		}
	})
}

// okCollector always succeeds with one record.
type okCollector struct{}

func (*okCollector) Name() string { return "ok" }

func (*okCollector) Collect(context.Context, scheduler.Run) ([]model.Record, error) {
	return []model.Record{{Meta: model.Meta{Source: "ok"}, Data: &model.MetricPoint{Name: "sample", Value: 1}}}, nil
}

// collectSink is a scheduler.Putter that keeps what it received.
type collectSink struct {
	mu      sync.Mutex
	records []model.Record
}

func (s *collectSink) Put(_ context.Context, records []model.Record) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.records = append(s.records, records...)
	return nil
}

func (s *collectSink) gaps(collector string) []model.Gap {
	s.mu.Lock()
	defer s.mu.Unlock()

	var out []model.Gap
	for _, r := range s.records {
		if g, ok := r.Data.(*model.Gap); ok && g.Collector == collector {
			out = append(out, *g)
		}
	}
	return out
}

func (s *collectSink) count(source string) int {
	s.mu.Lock()
	defer s.mu.Unlock()

	n := 0
	for _, r := range s.records {
		if r.Source == source {
			n++
		}
	}
	return n
}

// allowedImports are the packages the files of internal/sysstats may import (AC10).
var allowedImports = []string{
	"bufio", "bytes", "context", "errors", "fmt", "io", "io/fs", "log/slog", "math/bits", "os", "slices", "strconv", "strings",
	"sync", "syscall", "time",
	"github.com/LarsLaskowski/Vandox/internal/model",
	"github.com/LarsLaskowski/Vandox/internal/scheduler",
}

// allowedSelectors maps a package to the only selectors of it the files may use.
var allowedSelectors = map[string][]string{
	"syscall": {"Statfs", "Statfs_t", "Adjtimex", "Timex", "Errno", "EOPNOTSUPP"},
	"os":      {"DirFS"},
}

// foreignSuffixes are the extensions of source files other than Go (assembly, C, objects).
var foreignSuffixes = []string{".s", ".S", ".c", ".h", ".cc", ".cpp", ".cxx", ".hh", ".hpp", ".m", ".syso", ".swig", ".swigcxx", ".f", ".F"}

// checkPackageDir parses the non-test Go files of dir and returns the violations of the allow-list and the selectors it found.
func checkPackageDir(dir string) (violations []string, selectors map[string]bool, parsed int) {
	selectors = map[string]bool{}
	entries, err := os.ReadDir(dir)
	if err != nil {
		return []string{"reading " + dir + ": " + err.Error()}, selectors, 0
	}
	for _, e := range entries {
		name := e.Name()
		if slices.ContainsFunc(foreignSuffixes, func(s string) bool { return strings.HasSuffix(name, s) }) {
			violations = append(violations, name+": a source file other than Go")
			continue
		}
		if !strings.HasSuffix(name, ".go") || strings.HasSuffix(name, "_test.go") {
			continue
		}
		parsed++
		violations = append(violations, checkFile(filepath.Join(dir, name), selectors)...)
	}
	return violations, selectors, parsed
}

// checkFile checks one Go file and records the selectors of syscall and os it uses in selectors.
func checkFile(path string, selectors map[string]bool) []string {
	file, err := parser.ParseFile(token.NewFileSet(), path, nil, parser.ParseComments)
	if err != nil {
		return []string{path + ": " + err.Error()}
	}
	violations := checkImports(path, file)
	violations = append(violations, checkSelectors(path, file, selectors)...)
	return append(violations, checkDirectives(path, file)...)
}

// checkImports returns the imports of file that are not on the allow-list or carry a name.
func checkImports(path string, file *ast.File) []string {
	var violations []string
	for _, imp := range file.Imports {
		p, _ := strconv.Unquote(imp.Path.Value)
		if imp.Name != nil {
			violations = append(violations, path+": import of "+p+" carries the name "+imp.Name.Name)
		}
		if !slices.Contains(allowedImports, p) {
			violations = append(violations, path+": import of "+p+" is not allowed")
		}
	}
	return violations
}

// checkSelectors returns the uses of syscall and os selectors outside the allowed ones and records every use in selectors.
func checkSelectors(path string, file *ast.File, selectors map[string]bool) []string {
	var violations []string
	ast.Inspect(file, func(n ast.Node) bool {
		sel, ok := n.(*ast.SelectorExpr)
		if !ok {
			return true
		}
		id, ok := sel.X.(*ast.Ident)
		if !ok {
			return true
		}
		allowed, restricted := allowedSelectors[id.Name]
		if !restricted {
			return true
		}
		selectors[id.Name+"."+sel.Sel.Name] = true
		if !slices.Contains(allowed, sel.Sel.Name) {
			violations = append(violations, path+": "+id.Name+"."+sel.Sel.Name+" is not allowed")
		}
		return true
	})
	return violations
}

// checkDirectives returns the comment lines that start with //go: other than //go:build.
func checkDirectives(path string, file *ast.File) []string {
	var violations []string
	for _, group := range file.Comments {
		for _, c := range group.List {
			for _, line := range strings.Split(c.Text, "\n") {
				line = strings.TrimSpace(line)
				if strings.HasPrefix(line, "//go:") && !strings.HasPrefix(line, "//go:build") {
					violations = append(violations, path+": directive "+line)
				}
			}
		}
	}
	return violations
}

// probeCase is a Go file that the check must refuse.
type probeCase struct {
	name string
	file string
	want string
}

func probeCases() []probeCase {
	return []probeCase{
		{"network package", "package p\n\nimport \"net\"\n\nvar _ = net.IPv4len\n", "import of net is not allowed"},
		{"program package", "package p\n\nimport \"os/exec\"\n\nvar _ = exec.ErrDot\n", "import of os/exec is not allowed"},
		{"cgo", "package p\n\nimport \"C\"\n", "import of C is not allowed"},
		{"unsafe", "package p\n\nimport \"unsafe\"\n\nvar _ unsafe.Pointer\n", "import of unsafe is not allowed"},
		{"x/sys", "package p\n\nimport \"golang.org/x/sys/unix\"\n\nvar _ = unix.AF_INET\n", "import of golang.org/x/sys/unix is not allowed"},
		{"raw system call", "package p\n\nimport \"syscall\"\n\nvar _ = syscall.Syscall\n", "syscall.Syscall is not allowed"},
		{"socket", "package p\n\nimport \"syscall\"\n\nvar _ = syscall.Socket\n", "syscall.Socket is not allowed"},
		{"process start", "package p\n\nimport \"os\"\n\nvar _ = os.StartProcess\n", "os.StartProcess is not allowed"},
		{"linkname", "package p\n\n//go:linkname f runtime.f\nfunc f()\n", "directive //go:linkname"},
		{"renamed import", "package p\n\nimport sc \"syscall\"\n\nvar _ = sc.Statfs\n", "carries the name sc"},
		{"dot import", "package p\n\nimport . \"syscall\"\n", "carries the name ."},
		{"blank import", "package p\n\nimport _ \"syscall\"\n", "carries the name _"},
	}
}

func TestSysstats_ImportsOnlyAllowedPackages(t *testing.T) {
	t.Run("the package", requirePackageClean)
	for _, tt := range probeCases() {
		t.Run("detects "+tt.name, func(t *testing.T) { requireProbeRefused(t, tt.file, "x.go", tt.want) })
	}
	t.Run("detects an assembly file", func(t *testing.T) {
		requireProbeRefused(t, "TEXT f(SB),0,$0\n", "x.s", "a source file other than Go")
	})
}

// requirePackageClean checks the package in the current directory against the allow-list.
func requirePackageClean(t *testing.T) {
	violations, selectors, parsed := checkPackageDir(".")

	for _, v := range violations {
		t.Errorf("violation: %s", v)
	}
	if parsed == 0 {
		t.Errorf("no non-test Go file was parsed, want at least one")
	}
	for _, want := range []string{"syscall.Statfs", "syscall.Adjtimex"} {
		if !selectors[want] {
			t.Errorf("the package never uses %s, want it found (the check would be vacuous)", want)
		}
	}
}

// requireProbeRefused checks that a directory holding only the file name with content gets a violation containing want.
func requireProbeRefused(t *testing.T, content, name, want string) {
	t.Helper()

	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, name), []byte(content), 0o600); err != nil {
		t.Fatalf("writing the probe file: %v", err)
	}

	violations, _, _ := checkPackageDir(dir)

	if !slices.ContainsFunc(violations, func(v string) bool { return strings.Contains(v, want) }) {
		t.Errorf("violations = %q, want one containing %q", violations, want)
	}
}

func TestNew_DefaultsReadTheHost(t *testing.T) {
	start := time.Now().UTC()

	t.Run("system", func(t *testing.T) {
		got := mustCollect(t, sysstats.NewSystem(sysstats.Sources{}, nil), start)

		if len(find(got, "load1")) != 1 {
			t.Errorf("the system sample of the host holds %d load1 records, want 1", len(find(got, "load1")))
		}
	})

	t.Run("filesystem", func(t *testing.T) {
		got := mustCollect(t, sysstats.NewFilesystem(sysstats.Sources{}, []string{"/"}, nil), start)

		sizes := find(got, "filesystem_size_bytes")
		if len(sizes) != 1 || sizes[0].labels["mount"] != "/" {
			t.Errorf("filesystem_size_bytes records of the host = %v, want one with mount=/", sizes)
		}
	})

	t.Run("clock", func(t *testing.T) {
		got := mustCollect(t, sysstats.NewClock(sysstats.Sources{}), start)

		if len(find(got, "clock_synchronized")) != 1 {
			t.Errorf("the clock sample of the host holds %d clock_synchronized records, want 1", len(find(got, "clock_synchronized")))
		}
	})
}

// errBoom is an error of a fake source.
var errBoom = errors.New("boom")

// eopnotsupp is the error a read of an unsupported pressure file answers.
var eopnotsupp = syscall.EOPNOTSUPP
