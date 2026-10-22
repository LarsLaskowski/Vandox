package procstats

import (
	"bytes"
	"context"
	"go/ast"
	"go/parser"
	"go/token"
	"io/fs"
	"log/slog"
	"math"
	"os"
	"path/filepath"
	"runtime"
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
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// The fixture trees are real captures of a Linux 6.18 kernel (testdata/proc/<pid>, testdata/proc/stat).
const (
	procDir      = "../../testdata/proc"
	etcDir       = "../../testdata/etc"
	fixtureBtime = 1792260183
	kib          = 1024
)

// PIDs of the captured processes.
const (
	pidKthread   = 12   // kworker/u16:0-kvfree_rcu_reclaim, state I, no address space
	pidFpmMaster = 6508 // setproctitle "php-fpm: pool example.com", 30 MB shared with its child
	pidMulti     = 6509 // /usr/bin/perl -e "sleep 600" -- -k start
	pidPrctl     = 6510 // comm "a\nUid:\t9\t9" set with prctl
	pidUnnamed   = 6511 // empty command line, comm ()
	pidZombie    = 6513 // state Z
	pidFpmChild  = 6514 // effective UID 33
)

// bootTime is the btime of testdata/proc/stat; runStart is 2000 seconds later.
var (
	bootTime = time.Unix(fixtureBtime, 0).UTC()
	runStart = bootTime.Add(2000 * time.Second)
)

// pythonCmdline is the cleaned command line of the process with the prctl name; its backslashes are in the capture.
var pythonCmdline = "/usr/bin/python3 -c \nimport ctypes, time\nlibc = ctypes.CDLL(None)\n" +
	`libc.prctl(15, b"a\nUid:\t9\t9", 0, 0, 0)` + "\ntime.sleep(600)\n"

// wantProc is the expected sample of a captured process.
type wantProc struct {
	pid, ppid                     int32
	user, command, cmdline, state string
	start, ticks                  int64 // starttime, utime + stime
	rssKB, pssKB                  uint64
	swap                          bool // whether the status has a VmSwap line (0 kB)
}

// fixtureProcs are the captured processes in the order of a snapshot: memory key (PSS) descending, then PID.
var fixtureProcs = []wantProc{
	{pidFpmMaster, 6507, "root", "php-fpm", "php-fpm: pool example.com", "S", 192559, 227, 93300, 45355, true},
	{pidFpmChild, pidFpmMaster, "www-data", "php-fpm", "php-fpm: pool example.com", "S", 192790, 0, 89732, 44525, true},
	{pidPrctl, 6507, "root", "python3", pythonCmdline, "S", 192559, 2, 9952, 6135, true},
	{pidUnnamed, 6507, "root", UnnamedCommand, "", "S", 192559, 0, 5112, 1376, true},
	{pidMulti, 6507, "root", "perl", "/usr/bin/perl -e sleep 600 -- -k start", "S", 192559, 0, 4976, 1295, true},
	{pidKthread, 2, "root", "kworker/u16:0-kvfree_rcu_reclaim", "", "I", 35, 22, 0, 0, false},
	{pidZombie, 6512, "root", "perl", "", "Z", 192559, 0, 0, 0, false},
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

// readFixture returns the content of a fixture file below testdata/proc.
func readFixture(t *testing.T, name string) string {
	t.Helper()

	data, err := os.ReadFile(filepath.Join(procDir, filepath.FromSlash(name)))
	if err != nil {
		t.Fatalf("reading the fixture %s: %v", name, err)
	}
	return string(data)
}

// readEtcFixture returns the content of a fixture file below testdata/etc.
func readEtcFixture(t *testing.T, name string) string {
	t.Helper()

	data, err := os.ReadFile(filepath.Join(etcDir, filepath.FromSlash(name)))
	if err != nil {
		t.Fatalf("reading the fixture %s: %v", name, err)
	}
	return string(data)
}

// faultFS answers the named files with an error at Open or at the first Read; the maps may change between runs. The name
// "." fails the listing.
type faultFS struct {
	fs.FS
	openErr map[string]error
	readErr map[string]error
}

func newFaultFS(f fs.FS) *faultFS {
	return &faultFS{FS: f, openErr: map[string]error{}, readErr: map[string]error{}}
}

func (f *faultFS) Open(name string) (fs.File, error) {
	if err, ok := f.openErr[name]; ok {
		return nil, &fs.PathError{Op: "open", Path: name, Err: err}
	}
	rerr, failRead := f.readErr[name]
	file, err := f.FS.Open(name)
	if err != nil && failRead {
		// The file is absent from the fixtures but answers an error when read, as smaps_rollup of a kernel thread does.
		file, err = fstest.MapFS{name: &fstest.MapFile{}}.Open(name)
	}
	if err != nil {
		return nil, err
	}
	if failRead {
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

// tree is a /proc and an /etc in memory, with the faults a test scripts.
type tree struct {
	procFiles, etcFiles fstest.MapFS
	proc, etc           *faultFS
}

func newTreeOf(proc, etc fstest.MapFS) *tree {
	return &tree{procFiles: proc, etcFiles: etc, proc: newFaultFS(proc), etc: newFaultFS(etc)}
}

// newTree returns the captured processes and the fixture passwd. The captured kernel thread and zombie have no
// smaps_rollup: the kernel answers ESRCH, which the tree scripts.
func newTree(t *testing.T) *tree {
	t.Helper()

	tr := newTreeOf(loadDir(t, procDir), loadDir(t, etcDir))
	tr.proc.readErr[smapsOf(pidKthread)] = syscall.ESRCH
	tr.proc.readErr[smapsOf(pidZombie)] = syscall.ESRCH
	return tr
}

// synthTree returns an empty /proc with the btime of the fixture and a small passwd.
func synthTree() *tree {
	proc := fstest.MapFS{"stat": &fstest.MapFile{Data: []byte("cpu  1 2 3 4\nbtime " + strconv.Itoa(fixtureBtime) + "\nprocesses 5\n")}}
	etc := fstest.MapFS{"passwd": &fstest.MapFile{Data: []byte("root:x:0:0:root:/root:/bin/bash\nwww-data:x:33:33::/var/www:/usr/sbin/nologin\n")}}
	return newTreeOf(proc, etc)
}

func (tr *tree) sources() Sources { return Sources{Proc: tr.proc, Etc: tr.etc} }

func smapsOf(pid int32) string { return fileOf(pid, "smaps_rollup") }

func fileOf(pid int32, name string) string { return strconv.Itoa(int(pid)) + "/" + name }

func setFile(files fstest.MapFS, name, content string) {
	files[name] = &fstest.MapFile{Data: []byte(content)}
}

// procSpec is a synthetic process.
type procSpec struct {
	pid, ppid           int32
	comm                string
	state               byte // 0: S
	flags               uint32
	utime, stime, start uint64
	uid                 uint32
	rssKB, pssKB        uint64
	cmdline             string // the raw file
}

// statLine returns the stat file of the process: 52 fields, those the collector reads set.
func (p procSpec) statLine() string {
	state := p.state
	if state == 0 {
		state = 'S'
	}
	fields := make([]string, 50) // fields 3 to 52
	for i := range fields {
		fields[i] = "0"
	}
	fields[0] = string(state)
	fields[1] = strconv.Itoa(int(max(p.ppid, 1)))
	fields[2], fields[3], fields[5] = "1", "1", "-1"
	fields[6] = strconv.FormatUint(uint64(p.flags), 10)
	fields[11] = strconv.FormatUint(p.utime, 10)
	fields[12] = strconv.FormatUint(p.stime, 10)
	fields[19] = strconv.FormatUint(p.start, 10)
	return strconv.Itoa(int(p.pid)) + " (" + p.comm + ") " + strings.Join(fields, " ") + "\n"
}

// statusText returns the status file of the process.
func (p procSpec) statusText() string {
	uid := strconv.FormatUint(uint64(p.uid), 10)
	return "Name:\tx\nUmask:\t0022\nState:\tS (sleeping)\nTgid:\t" + strconv.Itoa(int(p.pid)) + "\nUid:\t" + uid + "\t" + uid + "\t" + uid + "\t" + uid +
		"\nGid:\t0\t0\t0\t0\nVmRSS:\t" + strconv.FormatUint(p.rssKB, 10) + " kB\nVmSwap:\t       0 kB\nThreads:\t1\n"
}

// smapsText returns the smaps_rollup file of the process.
func (p procSpec) smapsText() string {
	return "00400000-7ffd1a94b000 ---p 00000000 00:00 0                              [rollup]\n" +
		"Rss:             " + strconv.FormatUint(p.rssKB, 10) + " kB\nPss:             " + strconv.FormatUint(p.pssKB, 10) + " kB\n" +
		"Pss_Dirty:            0 kB\nShared_Clean:         0 kB\nSwap:                 0 kB\n"
}

// add writes the four files of the process.
func (tr *tree) add(p procSpec) {
	setFile(tr.procFiles, fileOf(p.pid, "stat"), p.statLine())
	setFile(tr.procFiles, fileOf(p.pid, "status"), p.statusText())
	setFile(tr.procFiles, fileOf(p.pid, "cmdline"), p.cmdline)
	setFile(tr.procFiles, smapsOf(p.pid), p.smapsText())
}

// bulkTree returns a synthetic tree of the processes spec(1) to spec(n).
func bulkTree(n int, spec func(i int) procSpec) *tree {
	tr := synthTree()
	for i := 1; i <= n; i++ {
		tr.add(spec(i))
	}
	return tr
}

func u64p(v uint64) *uint64 { return &v }

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

// all returns every entry so far, of any level.
func (s *logStore) all() []logEntry {
	s.root.mu.Lock()
	defer s.root.mu.Unlock()

	return slices.Clone(s.root.entries)
}

// logged returns the entries of level info and above so far.
func (s *logStore) logged() []logEntry {
	var out []logEntry
	for _, e := range s.all() {
		if e.level >= slog.LevelInfo {
			out = append(out, e)
		}
	}
	return out
}

// text returns every entry (message and attributes, any level) as one string.
func (e logEntry) text() string {
	var b strings.Builder
	b.WriteString(e.msg)
	for k, v := range e.attrs {
		b.WriteString(" " + k + "=" + v)
	}
	return b.String()
}

// wantLog is an expected log line; the attributes in attrs must match, others may be present.
type wantLog struct {
	level slog.Level
	msg   string
	attrs map[string]string
}

// requireLogged checks that the entries of level info and above since the start are exactly want, in order.
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

// newCollector returns the collector over tr and the store of its log lines.
func newCollector(tr *tree, rules []Rule) (*Processes, *logStore) {
	store, log := newLogStore()
	return New(tr.sources(), rules, log), store
}

// collectAt runs one sample of c that starts at start.
func collectAt(c *Processes, start time.Time, pressure bool) ([]model.Record, error) {
	return c.Collect(context.Background(), scheduler.Run{Start: start, Pressure: pressure})
}

// snapshotOf runs one sample of c and returns its one process snapshot, after checking the record around it.
func snapshotOf(t *testing.T, c *Processes, start time.Time, pressure bool) *model.ProcessSnapshot {
	t.Helper()

	records, err := collectAt(c, start, pressure)
	if err != nil {
		t.Fatalf("Collect(start %v, pressure %v) error = %v, want nil", start, pressure, err)
	}
	return snapshotIn(t, records, start)
}

// snapshotIn returns the one process snapshot of records, after checking the record around it.
func snapshotIn(t *testing.T, records []model.Record, start time.Time) *model.ProcessSnapshot {
	t.Helper()

	if len(records) != 1 {
		t.Fatalf("Collect() returned %d records, want 1", len(records))
	}
	r := records[0]
	snap, ok := r.Data.(*model.ProcessSnapshot)
	if !ok {
		t.Fatalf("Collect() returned a %q record, want a %q record", r.Kind(), model.KindProcessSnapshot)
	}
	if r.Source != Source || !r.CapturedAt.Equal(start) {
		t.Errorf("record source %q captured_at %v, want source %q captured_at %v", r.Source, r.CapturedAt, Source, start)
	}
	checked := r
	checked.Origin, checked.Seq = model.OriginAgent, 1
	if _, err := wire.CheckRecord(&checked); err != nil {
		t.Errorf("wire.CheckRecord(record with origin agent and seq 1) = %v, want nil", err)
	}
	return snap
}

// pidsOf returns the PIDs of the listed processes in order.
func pidsOf(snap *model.ProcessSnapshot) []int32 {
	pids := make([]int32, 0, len(snap.Processes))
	for i := range snap.Processes {
		pids = append(pids, snap.Processes[i].PID)
	}
	return pids
}

// processOf returns the listed process pid, or nil.
func processOf(snap *model.ProcessSnapshot, pid int32) *model.ProcessSample {
	for i := range snap.Processes {
		if snap.Processes[i].PID == pid {
			return &snap.Processes[i]
		}
	}
	return nil
}

// programNamed returns the listed program name, or nil.
func programNamed(snap *model.ProcessSnapshot, name string) *model.ProgramAggregate {
	for i := range snap.Programs {
		if snap.Programs[i].Program == name {
			return &snap.Programs[i]
		}
	}
	return nil
}

// programNames returns the names of the programs in order.
func programNames(snap *model.ProcessSnapshot) []string {
	names := make([]string, 0, len(snap.Programs))
	for i := range snap.Programs {
		names = append(names, snap.Programs[i].Program)
	}
	return names
}

// total returns the snapshot's total, or -1 when it has none.
func total(snap *model.ProcessSnapshot) int64 {
	if snap.Total == nil {
		return -1
	}
	return int64(*snap.Total)
}

// near reports whether got and want agree to 1e-9 relative.
func near(got, want float64) bool {
	return math.Abs(got-want) <= 1e-9*math.Max(1, math.Abs(want))
}

// deref returns *p as text, or "absent" for nil, for failure messages.
func deref(p *uint64) string {
	if p == nil {
		return "absent"
	}
	return strconv.FormatUint(*p, 10)
}

// pidOf returns i as a PID, or 0 when it is not one.
func pidOf(i int) int32 {
	if i >= 1 && i <= math.MaxInt32 {
		return int32(i)
	}
	return 0
}

// units returns i as an unsigned number, or 0 when it is negative.
func units(i int) uint64 {
	if i >= 0 {
		return uint64(i)
	}
	return 0
}

func TestProcesses_SampleFieldsFromFixtures(t *testing.T) {
	c, _ := newCollector(newTree(t), nil)

	snap := snapshotOf(t, c, runStart, true)

	if got := pidsOf(snap); len(got) != len(fixtureProcs) {
		t.Fatalf("listed PIDs = %v, want the %d captured processes", got, len(fixtureProcs))
	}
	if !snap.Complete {
		t.Errorf("Complete = false, want true (every process was read and listed)")
	}
	if total(snap) != int64(len(fixtureProcs)) {
		t.Errorf("Total = %d, want %d", total(snap), len(fixtureProcs))
	}
	for i, want := range fixtureProcs {
		t.Run(strconv.Itoa(int(want.pid)), func(t *testing.T) {
			requireProcess(t, &snap.Processes[i], want)
		})
	}
}

// requireProcess checks every field of got against the capture.
func requireProcess(t *testing.T, got *model.ProcessSample, want wantProc) {
	t.Helper()

	if got.PID != want.pid || got.PPID != want.ppid {
		t.Errorf("pid, ppid = %d, %d, want %d, %d", got.PID, got.PPID, want.pid, want.ppid)
	}
	if got.User != want.user || got.Command != want.command || got.Cmdline != want.cmdline || got.State != want.state {
		t.Errorf("user, command, cmdline, state = %q, %q, %q, %q\nwant %q, %q, %q, %q",
			got.User, got.Command, got.Cmdline, got.State, want.user, want.command, want.cmdline, want.state)
	}
	if got.Truncated {
		t.Errorf("truncated = true, want false")
	}
	requireStartAndCPU(t, got, want)
	if got.RSSBytes != want.rssKB*kib {
		t.Errorf("rss_bytes = %d, want %d", got.RSSBytes, want.rssKB*kib)
	}
	if got.PSSBytes == nil || *got.PSSBytes != want.pssKB*kib {
		t.Errorf("pss_bytes = %s, want %d", deref(got.PSSBytes), want.pssKB*kib)
	}
	switch {
	case want.swap && (got.SwapBytes == nil || *got.SwapBytes != 0):
		t.Errorf("swap_bytes = %s, want 0", deref(got.SwapBytes))
	case !want.swap && got.SwapBytes != nil:
		t.Errorf("swap_bytes = %d, want absent (the status has no VmSwap line)", *got.SwapBytes)
	}
	if got.OOMScoreAdj != nil {
		t.Errorf("oom_score_adj = %d, want absent (never read)", *got.OOMScoreAdj)
	}
}

// requireStartAndCPU checks started_at (boot time plus starttime / 100 seconds, to the second or to the clock tick) and the
// CPU average since the start from the start the sample reports.
func requireStartAndCPU(t *testing.T, got *model.ProcessSample, want wantProc) {
	t.Helper()

	whole := bootTime.Add(time.Duration(want.start/ClockTicks) * time.Second)
	exact := bootTime.Add(time.Duration(want.start) * 10 * time.Millisecond)
	if got.StartedAt.Location() != time.UTC || (!got.StartedAt.Equal(whole) && !got.StartedAt.Equal(exact)) {
		t.Errorf("started_at = %v, want %v or %v in UTC", got.StartedAt, whole, exact)
	}
	wantCPU := float64(want.ticks) / ClockTicks / runStart.Sub(got.StartedAt).Seconds() * 100
	if !near(got.CPUPercent, wantCPU) {
		t.Errorf("cpu_percent = %v, want %v (average since the start)", got.CPUPercent, wantCPU)
	}
}

func TestProcesses_ProgramSumsPSSAndRSS(t *testing.T) {
	c, _ := newCollector(newTree(t), nil)

	snap := snapshotOf(t, c, runStart, false)

	fpm := programNamed(snap, "php-fpm")
	if fpm == nil {
		t.Fatalf("programs = %v, want php-fpm among them", programNames(snap))
	}
	wantRSS, wantPSS := uint64(93300+89732)*kib, uint64(45355+44525)*kib
	if fpm.Count != 2 || fpm.RSSBytes != wantRSS || fpm.PSSBytes == nil || *fpm.PSSBytes != wantPSS || !fpm.RSSOvercounted {
		t.Errorf("php-fpm = count %d rss %d pss %s overcounted %v, want 2, %d, %d, true",
			fpm.Count, fpm.RSSBytes, deref(fpm.PSSBytes), fpm.RSSOvercounted, wantRSS, wantPSS)
	}
	if fpm.PSSBytes != nil && fpm.RSSBytes <= *fpm.PSSBytes {
		t.Errorf("php-fpm rss sum %d, pss sum %d, want the rss sum above the pss sum (shared pages)", fpm.RSSBytes, *fpm.PSSBytes)
	}
	master, child := processOf(snap, pidFpmMaster), processOf(snap, pidFpmChild)
	if master == nil || child == nil || !near(fpm.CPUPercent, master.CPUPercent+child.CPUPercent) {
		t.Errorf("php-fpm cpu_percent = %v, want the sum of its processes", fpm.CPUPercent)
	}
	perl := programNamed(snap, "perl")
	if perl == nil || perl.Count != 2 || perl.RSSBytes != 4976*kib || perl.PSSBytes == nil || *perl.PSSBytes != 1295*kib || !perl.RSSOvercounted {
		t.Errorf("perl = %+v, want count 2 (a process and a zombie), rss %d, pss %d, overcounted", perl, 4976*kib, 1295*kib)
	}
	python := programNamed(snap, "python3")
	if python == nil || python.Count != 1 || python.RSSOvercounted || python.PSSBytes == nil || *python.PSSBytes != 6135*kib {
		t.Errorf("python3 = %+v, want count 1, pss %d, not overcounted", python, 6135*kib)
	}
	wantOrder := []string{"php-fpm", "python3", UnnamedCommand, "perl", "kworker/u16:0-kvfree_rcu_reclaim"}
	if got := programNames(snap); !slices.Equal(got, wantOrder) {
		t.Errorf("programs in order = %q, want %q (PSS sum descending)", got, wantOrder)
	}
}

func TestProcesses_ProgramPSSOmittedWhenAMemberHasNone(t *testing.T) {
	tr := newTree(t)
	tr.proc.readErr[smapsOf(pidFpmChild)] = syscall.EACCES
	c, _ := newCollector(tr, nil)

	snap := snapshotOf(t, c, runStart, false)

	child := processOf(snap, pidFpmChild)
	if child == nil || child.PSSBytes != nil || child.RSSBytes != 89732*kib {
		t.Errorf("php-fpm child = %+v, want pss absent and rss %d from VmRSS", child, 89732*kib)
	}
	fpm := programNamed(snap, "php-fpm")
	if fpm == nil || fpm.PSSBytes != nil || fpm.RSSBytes != (93300+89732)*kib || fpm.Count != 2 {
		t.Errorf("php-fpm = %+v, want count 2, rss %d and pss absent (one member has none)", fpm, (93300+89732)*kib)
	}
	python := programNamed(snap, "python3")
	if python == nil || python.PSSBytes == nil || *python.PSSBytes != 6135*kib {
		t.Errorf("python3 = %+v, want its own pss %d (every member has one)", python, 6135*kib)
	}
}

func TestProcesses_ProgramsCoverUnlistedProcesses(t *testing.T) {
	const n = 80
	tr := bulkTree(n, func(i int) procSpec {
		return procSpec{pid: pidOf(i), comm: "p", start: 1000, rssKB: units(i) * 10, pssKB: units(i) * 5, cmdline: "prog" + strconv.Itoa(i%10) + "\x00-x\x00"}
	})
	c, _ := newCollector(tr, nil)

	snap := snapshotOf(t, c, runStart, false)

	if len(snap.Processes) >= n || len(snap.Processes) < TopProcesses {
		t.Errorf("listed %d processes of %d without pressure, want at least %d and fewer than %d", len(snap.Processes), n, TopProcesses, n)
	}
	if snap.Complete {
		t.Errorf("Complete = true, want false (processes were left out)")
	}
	if total(snap) != n {
		t.Errorf("Total = %d, want %d", total(snap), n)
	}
	var sum uint32
	for i := range snap.Programs {
		sum += snap.Programs[i].Count
	}
	if sum != n || len(snap.Programs) != 10 {
		t.Errorf("programs hold %d processes in %d programs, want %d in 10 (every process read, listed or not)", sum, len(snap.Programs), n)
	}
}

func TestProcesses_RanksByRSSWhenPSSPartlyMissing(t *testing.T) {
	build := func() *tree {
		tr := synthTree()
		tr.add(procSpec{pid: 1, comm: "a", start: 1000, rssKB: 1000, pssKB: 100, cmdline: "a\x00"})
		tr.add(procSpec{pid: 2, comm: "b", start: 1000, rssKB: 500, pssKB: 400, cmdline: "b\x00"})
		tr.add(procSpec{pid: 3, comm: "c", start: 1000, rssKB: 800, pssKB: 200, cmdline: "c\x00"})
		return tr
	}

	t.Run("every PSS readable ranks by PSS", func(t *testing.T) {
		c, _ := newCollector(build(), nil)

		snap := snapshotOf(t, c, runStart, true)

		if got, want := pidsOf(snap), []int32{2, 3, 1}; !slices.Equal(got, want) {
			t.Errorf("processes in order = %v, want %v (PSS 400, 200, 100)", got, want)
		}
		if got, want := programNames(snap), []string{"b", "c", "a"}; !slices.Equal(got, want) {
			t.Errorf("programs in order = %v, want %v", got, want)
		}
	})

	t.Run("one PSS missing ranks everything by RSS", func(t *testing.T) {
		tr := build()
		tr.proc.readErr[smapsOf(3)] = syscall.EACCES
		c, _ := newCollector(tr, nil)

		snap := snapshotOf(t, c, runStart, true)

		if got, want := pidsOf(snap), []int32{1, 3, 2}; !slices.Equal(got, want) {
			t.Errorf("processes in order = %v, want %v (RSS 1000, 800, 500; one key for the whole run)", got, want)
		}
		if got, want := programNames(snap), []string{"a", "c", "b"}; !slices.Equal(got, want) {
			t.Errorf("programs in order = %v, want %v", got, want)
		}
	})
}

// bulkRank is a synthetic tree whose memory rank rises with the PID and whose CPU rank falls with it.
func bulkRank(n int) *tree {
	return bulkTree(n, func(i int) procSpec {
		return procSpec{
			pid: pidOf(i), comm: "p", start: 1000, utime: units(n+1-i) * 10,
			rssKB: units(i) * 100, pssKB: units(i) * 50, cmdline: "p" + strconv.Itoa(i) + "\x00",
		}
	})
}

func TestProcesses_TopProcessesWithoutPressure(t *testing.T) {
	const n = 120
	tr := bulkRank(n)
	// PIDs 95 and 96 tie for the 25th place by memory: the lower PID is taken.
	tr.add(procSpec{pid: 96, comm: "p", start: 1000, utime: units(n+1-96) * 10, rssKB: 95 * 100, pssKB: 95 * 50, cmdline: "p96\x00"})
	c, _ := newCollector(tr, nil)

	snap := snapshotOf(t, c, runStart, false)

	var want []int32
	for pid := int32(n); pid >= 97; pid-- {
		want = append(want, pid)
	}
	want = append(want, 95)
	for pid := int32(25); pid >= 1; pid-- {
		want = append(want, pid)
	}
	if got := pidsOf(snap); !slices.Equal(got, want) {
		t.Errorf("listed PIDs = %v, want the top 25 by memory and the top 25 by CPU, memory key descending then PID:\n%v", got, want)
	}
	if snap.Complete {
		t.Errorf("Complete = true, want false (70 processes were left out)")
	}
	if total(snap) != n {
		t.Errorf("Total = %d, want %d", total(snap), n)
	}
	if len(snap.Programs) != n {
		t.Errorf("listed %d programs, want %d (programs cover every process)", len(snap.Programs), n)
	}
}

func TestProcesses_FullSnapshotUnderPressure(t *testing.T) {
	const n = 120
	c, _ := newCollector(bulkRank(n), nil)

	snap := snapshotOf(t, c, runStart, true)

	var want []int32
	for pid := int32(n); pid >= 1; pid-- {
		want = append(want, pid)
	}
	if got := pidsOf(snap); !slices.Equal(got, want) {
		t.Errorf("listed PIDs = %v, want every process, memory key descending:\n%v", got, want)
	}
	if !snap.Complete || total(snap) != n {
		t.Errorf("Complete, Total = %v, %d, want true, %d", snap.Complete, total(snap), n)
	}
}

func TestProcesses_UnnamedProcessIsRecorded(t *testing.T) {
	c, _ := newCollector(newTree(t), nil)

	snap := snapshotOf(t, c, runStart, true)

	p := processOf(snap, pidUnnamed)
	if p == nil {
		t.Fatalf("the process with an empty command line and an empty comm is not listed; listed %v", pidsOf(snap))
	}
	if p.Command != UnnamedCommand || p.Cmdline != "" {
		t.Errorf("command, cmdline = %q, %q, want %q and empty", p.Command, p.Cmdline, UnnamedCommand)
	}
	if program := programNamed(snap, UnnamedCommand); program == nil || program.Count != 1 {
		t.Errorf("program %q = %+v, want a program of one process", UnnamedCommand, program)
	}
	if !snap.Complete {
		t.Errorf("Complete = false, want true (the unnamed process is no failure)")
	}
}

func TestProcesses_ProgramSumsSaturate(t *testing.T) {
	const largest = 1<<54 - 1 // kB: times 1024 the largest value that fits 64 bits
	tr := synthTree()
	tr.add(procSpec{pid: 1, comm: "x", start: 1000, rssKB: largest, pssKB: largest, cmdline: "same\x00"})
	tr.add(procSpec{pid: 2, comm: "x", start: 1000, rssKB: largest, pssKB: largest, cmdline: "same\x00"})
	c, _ := newCollector(tr, nil)

	snap := snapshotOf(t, c, runStart, true)

	program := programNamed(snap, "same")
	if program == nil || program.Count != 2 || program.RSSBytes != math.MaxUint64 || program.PSSBytes == nil || *program.PSSBytes != math.MaxUint64 {
		t.Errorf("program same = %+v, want count 2 and both sums saturated at %d", program, uint64(math.MaxUint64))
	}
}

func TestProcesses_StateLeftOutWhenNotALetter(t *testing.T) {
	tr := synthTree()
	tr.add(procSpec{pid: 1, comm: "x", state: '5', start: 1000, rssKB: 10, pssKB: 5, cmdline: "x\x00"})
	tr.add(procSpec{pid: 2, comm: "x", state: 'R', start: 1000, rssKB: 20, pssKB: 8, cmdline: "x\x00"})
	c, _ := newCollector(tr, nil)

	snap := snapshotOf(t, c, runStart, true)

	if p := processOf(snap, 1); p == nil || p.State != "" {
		t.Errorf("process 1 = %+v, want it listed with the state left out (the stat state is not a letter)", p)
	}
	if p := processOf(snap, 2); p == nil || p.State != "R" {
		t.Errorf("process 2 = %+v, want the state R", p)
	}
}

// outcome is the expected result of a sample over a varied tree.
type outcome struct {
	name     string
	mutate   func(tr *tree)
	fails    bool
	pids     []int32 // the listed PIDs, in any order
	total    int64
	complete bool
	check    func(t *testing.T, snap *model.ProcessSnapshot)
}

var allFixturePIDs = []int32{pidKthread, pidFpmMaster, pidMulti, pidPrctl, pidUnnamed, pidZombie, pidFpmChild}

// without returns allFixturePIDs less pid.
func without(pid int32) []int32 {
	return slices.DeleteFunc(slices.Clone(allFixturePIDs), func(p int32) bool { return p == pid })
}

// padded returns data followed by filler lines up to size bytes.
func padded(data, filler string, size int) string {
	var b strings.Builder
	b.WriteString(data)
	for b.Len() < size {
		b.WriteString(filler)
	}
	return b.String()[:size]
}

// mustReplace returns s with the one occurrence of old replaced by repl; it panics when old is not there exactly once, which
// would make the test vacuous.
func mustReplace(s, old, repl string) string {
	if strings.Count(s, old) != 1 {
		panic("the fixture does not hold " + strconv.Quote(old) + " exactly once")
	}
	return strings.Replace(s, old, repl, 1)
}

// editFile replaces the one occurrence of old in the file name of files by repl.
func editFile(files fstest.MapFS, name, old, repl string) {
	setFile(files, name, mustReplace(string(files[name].Data), old, repl))
}

// ended is a case in which the process pidMulti has ended: it is left out and not counted.
func ended(name string, mutate func(tr *tree)) outcome {
	return outcome{name: name, mutate: mutate, pids: without(pidMulti), total: 6, complete: true}
}

// unread is a case in which the process pidMulti cannot be read: it is left out and counted.
func unread(name string, mutate func(tr *tree)) outcome {
	return outcome{name: name, mutate: mutate, pids: without(pidMulti), total: 7, complete: false}
}

// noPSS is a case in which smaps_rollup of the process pidMulti is refused with err: it is listed with its RSS and without PSS.
func noPSS(name string, err error, onOpen bool) outcome {
	return outcome{
		name: name, pids: allFixturePIDs, total: 7, complete: true,
		mutate: func(tr *tree) {
			if onOpen {
				tr.proc.openErr[smapsOf(pidMulti)] = err
			} else {
				tr.proc.readErr[smapsOf(pidMulti)] = err
			}
		},
		check: func(t *testing.T, snap *model.ProcessSnapshot) {
			t.Helper()
			if p := processOf(snap, pidMulti); p == nil || p.PSSBytes != nil || p.RSSBytes != 4976*kib {
				t.Errorf("process %d = %+v, want pss absent and rss %d from VmRSS", pidMulti, p, 4976*kib)
			}
		},
	}
}

// endedAndRefusedCases are the cases of a process that has ended and of a smaps_rollup that is refused.
func endedAndRefusedCases() []outcome {
	return []outcome{
		{name: "every process read", pids: allFixturePIDs, total: 7, complete: true},
		ended("stat file gone", func(tr *tree) { delete(tr.procFiles, fileOf(pidMulti, "stat")) }),
		ended("status read answers ESRCH", func(tr *tree) { tr.proc.readErr[fileOf(pidMulti, "status")] = syscall.ESRCH }),
		ended("cmdline open answers ENOENT", func(tr *tree) { tr.proc.openErr[fileOf(pidMulti, "cmdline")] = syscall.ENOENT }),
		ended("smaps_rollup of an ordinary process answers ESRCH", func(tr *tree) { tr.proc.readErr[smapsOf(pidMulti)] = syscall.ESRCH }),
		noPSS("smaps_rollup read answers EACCES", syscall.EACCES, false),
		noPSS("smaps_rollup read answers EPERM", syscall.EPERM, false),
		noPSS("smaps_rollup open answers EACCES", syscall.EACCES, true),
	}
}

// unreadCases are the cases of a process that cannot be read or parsed: it is left out and the sample is not complete.
func unreadCases() []outcome {
	return []outcome{
		unread("smaps_rollup read answers EIO", func(tr *tree) { tr.proc.readErr[smapsOf(pidMulti)] = syscall.EIO }),
		unread("cmdline read answers EIO", func(tr *tree) { tr.proc.readErr[fileOf(pidMulti, "cmdline")] = syscall.EIO }),
		unread("stat does not parse", func(tr *tree) { setFile(tr.procFiles, fileOf(pidMulti, "stat"), "garbage\n") }),
		unread("stat of 4 KiB + 1", func(tr *tree) {
			p := procSpec{pid: pidMulti, start: 100}
			p.comm = strings.Repeat("x", 4097-len(p.statLine()))
			setFile(tr.procFiles, fileOf(pidMulti, "stat"), p.statLine())
		}),
		unread("status of 16 KiB + 1", func(tr *tree) {
			name := fileOf(pidMulti, "status")
			setFile(tr.procFiles, name, padded(string(tr.procFiles[name].Data), "Filler:\t1 kB\n", 16385))
		}),
		unread("smaps_rollup of 4 KiB + 1", func(tr *tree) {
			name := smapsOf(pidMulti)
			setFile(tr.procFiles, name, padded(string(tr.procFiles[name].Data), "Filler:             1 kB\n", 4097))
		}),
		unread("VmRSS times 1024 overflows", func(tr *tree) {
			editFile(tr.procFiles, fileOf(pidMulti, "status"), "VmRSS:\t    4976 kB", "VmRSS:\t18014398509481984 kB")
		}),
		unread("started_at in the year 10000", func(tr *tree) { setStart(tr, "25161004061700") }),
		unread("starttime of 2^64 - 1", func(tr *tree) { setStart(tr, "18446744073709551615") }),
		{
			name: "started_at on the last second of the year 9999", pids: allFixturePIDs, total: 7, complete: true,
			mutate: func(tr *tree) { setStart(tr, "25161004061699") },
			check: func(t *testing.T, snap *model.ProcessSnapshot) {
				t.Helper()
				if p := processOf(snap, pidMulti); p == nil || p.StartedAt.Year() != 9999 || p.CPUPercent != 0 {
					t.Errorf("process %d = %+v, want started_at in 9999 and cpu_percent 0 (not yet started)", pidMulti, p)
				}
			},
		},
	}
}

// failingSampleCases are the cases in which the whole sample fails.
func failingSampleCases() []outcome {
	removeProcesses := func(tr *tree) {
		for _, pid := range allFixturePIDs {
			for _, f := range []string{"stat", "status", "cmdline", "smaps_rollup"} {
				delete(tr.procFiles, fileOf(pid, f))
			}
		}
	}
	garbleStats := func(tr *tree) {
		for _, pid := range allFixturePIDs {
			setFile(tr.procFiles, fileOf(pid, "stat"), "garbage\n")
		}
	}
	return []outcome{
		{name: "listing fails", mutate: func(tr *tree) { tr.proc.openErr["."] = syscall.EIO }, fails: true},
		{name: "btime missing", mutate: func(tr *tree) { delete(tr.procFiles, "stat") }, fails: true},
		{name: "btime twice", mutate: func(tr *tree) { setFile(tr.procFiles, "stat", "btime 1792260183\nbtime 1792260183\n") }, fails: true},
		{name: "no process", mutate: removeProcesses, fails: true},
		{name: "no process readable", mutate: garbleStats, fails: true},
	}
}

// userCases are the cases of a passwd that is missing or lacks a user.
func userCases() []outcome {
	return []outcome{
		{
			name: "passwd missing", mutate: func(tr *tree) { delete(tr.etcFiles, "passwd") },
			pids: allFixturePIDs, total: 7, complete: true, check: requireDecimalUsers,
		},
		{
			name: "passwd unreadable", mutate: func(tr *tree) { tr.etc.readErr["passwd"] = syscall.EIO },
			pids: allFixturePIDs, total: 7, complete: true, check: requireDecimalUsers,
		},
		{
			name: "user without a passwd line", pids: allFixturePIDs, total: 7, complete: true,
			mutate: func(tr *tree) {
				editFile(tr.procFiles, fileOf(pidMulti, "status"), "Uid:\t0\t0\t0\t0", "Uid:\t0\t4242\t0\t0")
			},
			check: func(t *testing.T, snap *model.ProcessSnapshot) {
				t.Helper()
				if p := processOf(snap, pidMulti); p == nil || p.User != "4242" {
					t.Errorf("process %d user = %+v, want the effective UID 4242 in decimal", pidMulti, p)
				}
			},
		},
	}
}

func readOutcomeCases() []outcome {
	var all []outcome
	for _, cases := range [][]outcome{endedAndRefusedCases(), unreadCases(), failingSampleCases(), userCases()} {
		all = append(all, cases...)
	}
	return all
}

// setStart sets the starttime (field 22) of the process pidMulti to start.
func setStart(tr *tree, start string) {
	name := fileOf(pidMulti, "stat")
	text := string(tr.procFiles[name].Data)
	rest := text[strings.LastIndex(text, ")")+2:]
	fields := strings.Split(rest, " ")
	fields[19] = start
	setFile(tr.procFiles, name, text[:strings.LastIndex(text, ")")+2]+strings.Join(fields, " "))
}

// requireDecimalUsers checks that the users of the root and www-data processes are their UIDs in decimal.
func requireDecimalUsers(t *testing.T, snap *model.ProcessSnapshot) {
	t.Helper()

	for pid, want := range map[int32]string{pidFpmMaster: "0", pidFpmChild: "33"} {
		if p := processOf(snap, pid); p == nil || p.User != want {
			t.Errorf("process %d user = %+v, want %q (no passwd)", pid, p, want)
		}
	}
}

func TestProcesses_ReadOutcomes(t *testing.T) {
	for _, tt := range readOutcomeCases() {
		t.Run(tt.name, func(t *testing.T) { runOutcome(t, tt) })
	}
}

// runOutcome runs one sample over the fixture tree varied by the case and checks the result.
func runOutcome(t *testing.T, tt outcome) {
	t.Helper()

	tr := newTree(t)
	if tt.mutate != nil {
		tt.mutate(tr)
	}
	c, _ := newCollector(tr, nil)
	if tt.fails {
		records, err := collectAt(c, runStart, true)
		if err == nil || len(records) != 0 {
			t.Errorf("Collect() = %d records, %v, want no record and an error", len(records), err)
		}
		return
	}
	snap := snapshotOf(t, c, runStart, true)

	got := pidsOf(snap)
	slices.Sort(got)
	if want := slices.Sorted(slices.Values(tt.pids)); !slices.Equal(got, want) {
		t.Errorf("listed PIDs = %v, want %v", got, want)
	}
	if total(snap) != tt.total || snap.Complete != tt.complete {
		t.Errorf("Total, Complete = %d, %v, want %d, %v", total(snap), snap.Complete, tt.total, tt.complete)
	}
	if tt.check != nil {
		tt.check(t, snap)
	}
}

func TestProcesses_LogsTransitionsOnce(t *testing.T) {
	tr := newTree(t)
	c, store := newCollector(tr, nil)
	at := runStart
	next := func() time.Time { at = at.Add(60 * time.Second); return at }
	warn := func(msg string, attrs map[string]string) wantLog {
		attrs["collector"] = Name
		return wantLog{slog.LevelWarn, msg, attrs}
	}
	info := func(msg string) wantLog { return wantLog{slog.LevelInfo, msg, map[string]string{"collector": Name}} }
	var want []wantLog
	step := func(when string, add ...wantLog) {
		t.Helper()
		snapshotOf(t, c, next(), true)
		want = append(want, add...)
		requireLogged(t, store, when, want)
	}

	step("first sample, everything readable")
	tr.proc.readErr[smapsOf(pidFpmChild)] = syscall.EACCES
	step("PSS of one process missing", warn("process memory partly unknown", map[string]string{"processes": "1"}))
	step("PSS of one process still missing")
	tr.proc.readErr[smapsOf(pidMulti)] = syscall.EPERM
	step("PSS of two processes missing")
	clear(tr.proc.readErr)
	tr.proc.readErr[smapsOf(pidKthread)] = syscall.ESRCH
	tr.proc.readErr[smapsOf(pidZombie)] = syscall.ESRCH
	step("PSS readable again", info("process memory known again"))

	tr.proc.readErr[fileOf(pidMulti, "cmdline")] = syscall.EIO
	step("a process cannot be read", warn("processes not read", map[string]string{"processes": "1"}))
	requireErrNames(t, store, "processes not read", fileOf(pidMulti, "cmdline"))
	step("the process still cannot be read")
	delete(tr.proc.readErr, fileOf(pidMulti, "cmdline"))
	step("every process readable again", info("processes read again"))

	tr.etc.readErr["passwd"] = syscall.EIO
	step("passwd unreadable", warn("user names unavailable", map[string]string{}))
	requireErrNames(t, store, "user names unavailable", "passwd")
	step("passwd still unreadable")
	delete(tr.etc.readErr, "passwd")
	step("passwd readable again", info("user names available"))
}

// requireErrNames checks that the log line msg carries an attribute err that names the file.
func requireErrNames(t *testing.T, s *logStore, msg, file string) {
	t.Helper()

	for _, e := range s.logged() {
		if e.msg == msg {
			if !strings.Contains(e.attrs["err"], file) {
				t.Errorf("log line %q attribute err = %q, want it to name %q", msg, e.attrs["err"], file)
			}
			return
		}
	}
	t.Errorf("no log line %q in %v", msg, s.logged())
}

// sentinel is a text that must reach neither an error nor a log line.
const sentinel = "S3NT1NEL"

// leakCase is a variant of the fixture tree whose texts carry the sentinel.
type leakCase struct {
	name   string
	mutate func(tr *tree)
	fails  bool
}

func leakCases() []leakCase {
	allUnparsable := func(tr *tree) {
		for _, pid := range allFixturePIDs {
			setFile(tr.procFiles, fileOf(pid, "stat"), strconv.Itoa(int(pid))+" ("+sentinel+") S 1\n")
		}
		setFile(tr.etcFiles, "passwd", sentinel+"user:x:0:0::/:/bin/sh\n")
	}
	return []leakCase{
		{"comm of a stat that does not parse", func(tr *tree) {
			setFile(tr.procFiles, fileOf(pidMulti, "stat"), strconv.Itoa(pidMulti)+" ("+sentinel+") S 1 2 3\n")
		}, false},
		{"name of a status that does not parse", func(tr *tree) {
			setFile(tr.procFiles, fileOf(pidMulti, "status"), "Name:\t"+sentinel+"\nUid:\t0\t0\t0\t0\nUid:\t0\t0\t0\t0\nVmRSS:\t1 kB\n")
		}, false},
		{"command line of a process whose smaps_rollup fails", func(tr *tree) {
			setFile(tr.procFiles, fileOf(pidMulti, "cmdline"), sentinel+"\x00"+sentinel+"\x00")
			tr.proc.readErr[smapsOf(pidMulti)] = syscall.EIO
		}, false},
		{"user name of a process that does not parse", func(tr *tree) {
			setFile(tr.etcFiles, "passwd", sentinel+"user:x:0:0:root:/root:/bin/bash\n")
			setFile(tr.procFiles, fileOf(pidMulti, "stat"), "garbage "+sentinel+"\n")
		}, false},
		{"passwd that is too large", func(tr *tree) {
			setFile(tr.etcFiles, "passwd", sentinel+"user:x:0:0::/:/bin/sh\n"+strings.Repeat("a:x:1:1::/:/bin/sh\n", 1<<16))
		}, false},
		{"stat of every process does not parse", allUnparsable, true},
	}
}

func TestProcesses_ErrorsAndLogsHoldNoProcessText(t *testing.T) {
	for _, tt := range leakCases() {
		t.Run(tt.name, func(t *testing.T) {
			tr := newTree(t)
			tt.mutate(tr)
			c, store := newCollector(tr, nil)

			_, err := collectAt(c, runStart, true)

			if tt.fails != (err != nil) {
				t.Fatalf("%s: Collect() error = %v, want an error: %v", tt.name, err, tt.fails)
			}
			if err != nil && strings.Contains(err.Error(), sentinel) {
				t.Errorf("%s: the error %q holds process text", tt.name, err)
			}
			for _, e := range store.all() {
				if strings.Contains(e.text(), sentinel) {
					t.Errorf("%s: the log line %q holds process text", tt.name, e.text())
				}
			}
		})
	}
}

// encodeAndDecode writes record through the wire encoder and returns the record the wire decoder reads back.
func encodeAndDecode(t *testing.T, r model.Record) model.Record {
	t.Helper()

	r.Origin, r.Seq = model.OriginAgent, 1
	var buf bytes.Buffer
	header := wire.NewHeader("agent-1", "0123abcd-0123-0123-0123-0123456789ab", wire.ModeLive)
	if err := wire.EncodeBatch(&buf, &wire.Batch{Header: header, Records: []model.Record{r}}); err != nil {
		t.Fatalf("wire.EncodeBatch() error = %v, want nil", err)
	}
	dec, err := wire.NewDecoder(&buf, wire.Limits{})
	if err != nil {
		t.Fatalf("wire.NewDecoder() error = %v, want nil", err)
	}
	got, err := dec.Next()
	if err != nil {
		t.Fatalf("Decoder.Next() error = %v, want nil", err)
	}
	return got
}

func TestProcesses_InvalidUTF8StaysWithinLimits(t *testing.T) {
	invalid := strings.Repeat("\xff", 1024)
	tr := synthTree()
	tr.add(procSpec{pid: 1, comm: "x", start: 1000, rssKB: 10, pssKB: 5, cmdline: invalid})
	tr.add(procSpec{pid: 2, comm: invalid, start: 1000, rssKB: 20, pssKB: 8, cmdline: ""})
	tr.add(procSpec{pid: 3, comm: "y", uid: 5, start: 1000, rssKB: 30, pssKB: 9, cmdline: "p " + strings.Repeat("\xc3", 2000)})
	setFile(tr.etcFiles, "passwd", "root:x:0:0::/:/bin/sh\n"+strings.Repeat("\xff", 300)+":x:5:5::/:/bin/sh\n")
	c, _ := newCollector(tr, nil)
	records, err := collectAt(c, runStart, true)
	if err != nil {
		t.Fatalf("Collect() error = %v, want nil", err)
	}

	got := encodeAndDecode(t, records[0])

	if err := got.Validate(); err != nil {
		t.Errorf("the decoded record Validate() = %v, want nil (the backend sees what the agent encoded)", err)
	}
	snap, ok := got.Data.(*model.ProcessSnapshot)
	if !ok || len(snap.Processes) != 3 {
		t.Fatalf("decoded payload = %T with %d processes, want a snapshot of 3", got.Data, len(snap.Processes))
	}
	if p := processOf(snap, 3); p == nil || p.User == "" || len(p.User) > 256 || !utf8.ValidString(p.User) {
		t.Errorf("process 3 user = %+v, want 1 to 256 bytes of valid UTF-8 (a passwd name of invalid bytes, cut)", p)
	}
	for _, p := range snap.Processes {
		if len(p.Command) > 1024 || len(p.Cmdline) > 1024 || len(p.User) > 1024 || p.Command == "" ||
			!utf8.ValidString(p.Command) || !utf8.ValidString(p.Cmdline) {
			t.Errorf("process %d command %d bytes, cmdline %d bytes, want 1 to 1024 bytes of valid UTF-8 after decoding", p.PID, len(p.Command), len(p.Cmdline))
		}
	}
	for _, a := range snap.Programs {
		if len(a.Program) > 1024 || a.Program == "" || !utf8.ValidString(a.Program) {
			t.Errorf("program of %d bytes, want 1 to 1024 bytes of valid UTF-8", len(a.Program))
		}
	}
}

func TestProcesses_ReadsAtMostMaxProcesses(t *testing.T) {
	const processes = 4100
	tr := synthTree()
	for pid := int32(1); pid <= processes; pid++ {
		setFile(tr.procFiles, fileOf(pid, "stat"), procSpec{pid: pid, comm: "p", start: 1000}.statLine())
		setFile(tr.procFiles, fileOf(pid, "status"), procSpec{pid: pid, rssKB: 100}.statusText())
		setFile(tr.procFiles, fileOf(pid, "cmdline"), "")
		setFile(tr.procFiles, smapsOf(pid), procSpec{rssKB: 100, pssKB: 50}.smapsText())
	}
	// Directories that are no PID: leading zero, name, zero, sign, text, beyond 32 bits.
	for _, name := range []string{"0123", "self", "thread-self", "0", "+5", "5x", "2147483648", "99999999999"} {
		setFile(tr.procFiles, name+"/stat", "x")
	}
	c, _ := newCollector(tr, nil)

	snap := snapshotOf(t, c, runStart, true)

	got := pidsOf(snap)
	slices.Sort(got)
	if len(got) != MaxProcesses || got[0] != 1 || got[len(got)-1] != MaxProcesses {
		t.Errorf("listed %d PIDs, want exactly the PIDs 1 to %d", len(got), MaxProcesses)
	}
	if total(snap) != processes || snap.Complete {
		t.Errorf("Total, Complete = %d, %v, want %d, false (PIDs beyond %d are counted and not read)", total(snap), snap.Complete, processes, MaxProcesses)
	}
	pids, seen, err := listPIDs(tr.proc)
	if err != nil || seen != processes || len(pids) != MaxProcesses || !slices.IsSorted(pids) || pids[0] != 1 || pids[MaxProcesses-1] != MaxProcesses {
		t.Errorf("listPIDs() = %d PIDs, seen %d, %v, want the PIDs 1 to %d in ascending order and seen %d", len(pids), seen, err, MaxProcesses, processes)
	}
}

// longControlLine returns a command line of the word w followed by control characters, more than 4 KiB long.
func longControlLine(w string) string {
	return w + " " + strings.Repeat("\x01", 4200)
}

// allocatedBy returns the bytes the process allocated while f ran.
func allocatedBy(f func()) uint64 {
	var before, after runtime.MemStats
	runtime.ReadMemStats(&before)
	f()
	runtime.ReadMemStats(&after)
	return after.TotalAlloc - before.TotalAlloc
}

// maxCollectAlloc is the most one Collect over MaxProcesses processes with maximal texts allocates in total (AC10).
const maxCollectAlloc = 48 << 20

func TestProcesses_LargeSnapshotFitsTheLineLimit(t *testing.T) {
	tr := bulkTree(MaxProcesses, func(i int) procSpec {
		return procSpec{pid: pidOf(i), comm: "p", start: 1000, rssKB: units(i) + 1000, pssKB: units(i) + 500, cmdline: longControlLine("p" + strconv.Itoa(i))}
	})
	c, _ := newCollector(tr, nil)
	var records []model.Record
	var err error

	allocated := allocatedBy(func() { records, err = collectAt(c, runStart, true) })

	if err != nil || len(records) != 1 {
		t.Fatalf("Collect() = %d records, %v, want one record and no error", len(records), err)
	}
	t.Logf("Collect over %d processes allocated %.1f MiB", MaxProcesses, float64(allocated)/(1<<20))
	if allocated > maxCollectAlloc {
		t.Errorf("Collect allocated %d bytes, want at most %d (48 MiB in total)", allocated, maxCollectAlloc)
	}
	snap := snapshotIn(t, records, runStart)
	if snap.Complete {
		t.Errorf("Complete = true, want false (the processes do not fit the line limit)")
	}
	if len(snap.Programs) != MaxProcesses {
		t.Errorf("listed %d programs, want all %d (programs are filled before any process)", len(snap.Programs), MaxProcesses)
	}
	if len(snap.Processes) == 0 || len(snap.Processes) >= MaxProcesses {
		t.Errorf("listed %d processes, want some but not all %d", len(snap.Processes), MaxProcesses)
	}
	if total(snap) != MaxProcesses {
		t.Errorf("Total = %d, want %d", total(snap), MaxProcesses)
	}
}

func TestProcesses_ReadsBoundedCmdline(t *testing.T) {
	const processes = 64
	big := bytes.Repeat([]byte("arg\x00"), 1<<18) // 1 MiB, shared by every file
	tr := synthTree()
	for i := range processes {
		pid := int32(i + 1)
		tr.add(procSpec{pid: pid, comm: "p", start: 1000, rssKB: 10, pssKB: 5})
		tr.procFiles[fileOf(pid, "cmdline")] = &fstest.MapFile{Data: big}
	}
	c, _ := newCollector(tr, nil)
	var records []model.Record
	var err error

	allocated := allocatedBy(func() { records, err = collectAt(c, runStart, true) })

	if err != nil || len(records) != 1 {
		t.Fatalf("Collect() = %d records, %v, want one record and no error", len(records), err)
	}
	if limit := uint64(processes * 64 << 10); allocated >= limit {
		t.Errorf("Collect over %d command lines of 1 MiB allocated %d bytes, want less than %d (at most 4 KiB read per process)", processes, allocated, limit)
	}
	snap, _ := records[0].Data.(*model.ProcessSnapshot)
	if snap == nil || len(snap.Processes) != processes {
		t.Fatalf("snapshot = %+v, want %d processes", snap, processes)
	}
	for _, p := range snap.Processes {
		if !p.Truncated || len(p.Cmdline) == 0 || len(p.Cmdline) > 1024 {
			t.Errorf("process %d truncated %v cmdline %d bytes, want truncated and 1 to 1024 bytes", p.PID, p.Truncated, len(p.Cmdline))
		}
	}
}

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

func TestProcesses_BlockingReadIsAbandonedAsGap(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		g := newGate(t)
		tr := newTree(t)
		c := New(Sources{Proc: blockingFS{FS: tr.proc, name: smapsOf(pidMulti), gate: g}, Etc: tr.etc}, nil, nil)
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

		gaps := sink.gaps(Name)
		if len(gaps) != 4 {
			t.Errorf("gaps of %q = %d, want 4 (the first run and the 3 skipped runs)", Name, len(gaps))
		}
		for i, gap := range gaps {
			if gap.Cause != model.GapCollectorTimeout {
				t.Errorf("gap %d has cause %q, want %q", i, gap.Cause, model.GapCollectorTimeout)
			}
		}
		if got := g.entered.Load(); got != 1 {
			t.Errorf("the blocked read was entered %d times, want 1 (a stuck collector is not started again)", got)
		}
		if got := sink.count("ok"); got != 4 {
			t.Errorf("samples of the other collector = %d, want 4 (one per interval)", got)
		}
	})
}

// BenchmarkProcesses_Collect measures one full snapshot of the host's /proc (AC13: 250 processes in under 100 ms). It asserts
// no duration; docs/BENCHMARKS.md holds the result.
func BenchmarkProcesses_Collect(b *testing.B) {
	c := New(Sources{}, nil, nil)
	ctx := context.Background()

	for b.Loop() {
		if _, err := c.Collect(ctx, scheduler.Run{Start: time.Now().UTC(), Pressure: true}); err != nil {
			b.Fatalf("Collect() error = %v, want nil", err)
		}
	}
}

// allowedImports are the packages the non-test files of internal/procstats import (AC10): no program, socket, cgo or unsafe.
var allowedImports = []string{
	"bufio", "bytes", "cmp", "container/heap", "context", "encoding/json", "errors", "fmt", "io", "io/fs", "log/slog", "math", "os",
	"slices", "strconv", "strings", "sync", "syscall", "time", "unicode/utf8",
	"github.com/LarsLaskowski/Vandox/internal/model",
	"github.com/LarsLaskowski/Vandox/internal/scheduler",
	"github.com/LarsLaskowski/Vandox/internal/wire",
}

// allowedSelectors maps a package to the only selectors of it the files may use.
var allowedSelectors = map[string][]string{
	"syscall": {"ESRCH", "EACCES", "EPERM"},
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
		violations = append(violations, checkGoFile(filepath.Join(dir, name), selectors)...)
	}
	return violations, selectors, parsed
}

// checkGoFile checks one Go file and records the selectors of the restricted packages it uses in selectors.
func checkGoFile(path string, selectors map[string]bool) []string {
	file, err := parser.ParseFile(token.NewFileSet(), path, nil, parser.ParseComments)
	if err != nil {
		return []string{path + ": " + err.Error()}
	}
	violations := checkFileImports(path, file)
	violations = append(violations, checkFileSelectors(path, file, selectors)...)
	return append(violations, checkFileDirectives(path, file)...)
}

// checkFileImports returns the imports of file that are not allowed or carry a name.
func checkFileImports(path string, file *ast.File) []string {
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

// checkFileSelectors returns the uses of restricted selectors that are not allowed and records every use in selectors.
func checkFileSelectors(path string, file *ast.File, selectors map[string]bool) []string {
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

// checkFileDirectives returns the comment lines that start with //go: other than //go:build.
func checkFileDirectives(path string, file *ast.File) []string {
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

func TestProcstats_ImportsOnlyAllowedPackages(t *testing.T) {
	t.Run("the package", func(t *testing.T) {
		violations, selectors, parsed := checkPackageDir(".")

		for _, v := range violations {
			t.Errorf("violation: %s", v)
		}
		if parsed == 0 {
			t.Errorf("no non-test Go file was parsed, want at least one")
		}
		for _, want := range []string{"syscall.ESRCH", "syscall.EACCES", "os.DirFS"} {
			if !selectors[want] {
				t.Errorf("the package never uses %s, want it found (the check would be vacuous)", want)
			}
		}
	})

	probes := []struct{ name, file, want string }{
		{"user lookup", "package p\n\nimport \"os/user\"\n\nvar _ = user.Current\n", "import of os/user is not allowed"},
		{"program package", "package p\n\nimport \"os/exec\"\n\nvar _ = exec.ErrDot\n", "import of os/exec is not allowed"},
		{"network package", "package p\n\nimport \"net\"\n\nvar _ = net.IPv4len\n", "import of net is not allowed"},
		{"unsafe", "package p\n\nimport \"unsafe\"\n\nvar _ unsafe.Pointer\n", "import of unsafe is not allowed"},
		{"cgo", "package p\n\nimport \"C\"\n", "import of C is not allowed"},
		{"raw system call", "package p\n\nimport \"syscall\"\n\nvar _ = syscall.Syscall\n", "syscall.Syscall is not allowed"},
		{"other errno", "package p\n\nimport \"syscall\"\n\nvar _ = syscall.EIO\n", "syscall.EIO is not allowed"},
		{"process start", "package p\n\nimport \"os\"\n\nvar _ = os.StartProcess\n", "os.StartProcess is not allowed"},
		{"linkname", "package p\n\n//go:linkname f runtime.f\nfunc f()\n", "directive //go:linkname"},
		{"renamed import", "package p\n\nimport sc \"syscall\"\n\nvar _ = sc.ESRCH\n", "carries the name sc"},
	}
	for _, tt := range probes {
		t.Run("detects "+tt.name, func(t *testing.T) { requireProbeRefused(t, tt.file, "x.go", tt.want) })
	}
	t.Run("detects an assembly file", func(t *testing.T) { requireProbeRefused(t, "TEXT f(SB),0,$0\n", "x.s", "a source file other than Go") })
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

// triggerKills are the kills a triggered run names in the tests of the snapshots.
func triggerKills(n int) []model.KillRef {
	kills := make([]model.KillRef, n)
	for i := range kills {
		kills[i] = model.KillRef{BootID: "0f1e2d3c-4b5a-4968-8776-655443322110", KernelTime: time.Duration(i+1) * time.Second, VictimPID: int32(900 + i)}
	}
	return kills
}

// requireTrigger checks the trigger of a snapshot against the kills and the omitted count of the run.
func requireTrigger(t *testing.T, got *model.SnapshotTrigger, kills []model.KillRef, omitted uint32) {
	t.Helper()

	if got == nil {
		t.Fatalf("snapshot trigger = nil, want %d kills and %d omitted", len(kills), omitted)
	}
	if !slices.Equal(got.Kills, kills) || got.KillsOmitted != omitted {
		t.Errorf("snapshot trigger = %+v, want kills %+v and %d omitted", got, kills, omitted)
	}
}

func TestProcesses_TriggeredRunIsFullAndNamesKills(t *testing.T) {
	const n = 120
	tests := []struct {
		name     string
		pressure bool
		kills    []model.KillRef
		omitted  uint32
	}{
		{"one kill without pressure", false, triggerKills(1), 0},
		{"kills with omitted ones without pressure", false, triggerKills(model.MaxTriggerKills), 7},
		{"one kill under pressure", true, triggerKills(1), 0},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			c, _ := newCollector(bulkRank(n), nil)
			run := scheduler.Run{Start: runStart, Pressure: tc.pressure, Trigger: &scheduler.Trigger{Kills: tc.kills, KillsOmitted: tc.omitted}}

			records, err := c.Collect(context.Background(), run)

			if err != nil {
				t.Fatalf("Collect(triggered run) error = %v, want nil", err)
			}
			snap := snapshotIn(t, records, runStart)
			if len(snap.Processes) != n || !snap.Complete || total(snap) != n {
				t.Errorf("listed processes, Complete, Total = %d, %v, %d, want every process: %d, true, %d", len(snap.Processes), snap.Complete, total(snap), n, n)
			}
			requireTrigger(t, snap.Trigger, tc.kills, tc.omitted)
			if err := snap.Validate(); err != nil {
				t.Errorf("Validate() of the triggered snapshot = %v, want nil", err)
			}
		})
	}
}

func TestCommandReader_Process(t *testing.T) {
	t.Run("captured processes", processOfCapturedProcesses)

	t.Run("hostile names match the snapshot", processMatchesSnapshot)

	t.Run("errors", processErrors)
}

func processOfCapturedProcesses(t *testing.T) {
	r := NewCommandReader(newTree(t).proc)

	for _, want := range fixtureProcs {
		got, err := r.Process(want.pid, bootTime)

		if err != nil {
			t.Errorf("Process(%d) error = %v, want nil", want.pid, err)
			continue
		}
		wantStart := bootTime.Add(time.Duration(want.start) * time.Second / ClockTicks)
		if got.PID != want.pid || got.Command != want.command || got.Cmdline != want.cmdline || !got.StartedAt.Equal(wantStart) {
			t.Errorf("Process(%d) = {%d %.60q %.60q %v}, want {%d %.60q %.60q %v}",
				want.pid, got.PID, got.Command, got.Cmdline, got.StartedAt, want.pid, want.command, want.cmdline, wantStart)
		}
		if got.StartedAt.Location() != time.UTC {
			t.Errorf("Process(%d) StartedAt location = %v, want UTC", want.pid, got.StartedAt.Location())
		}
	}
}

func processMatchesSnapshot(t *testing.T) {
	tr := synthTree()
	for _, s := range hostileSpecs() {
		tr.add(s)
	}
	c, _ := newCollector(tr, nil)
	snap := snapshotOf(t, c, runStart, true)
	r := NewCommandReader(tr.proc)

	if len(snap.Processes) != len(hostileSpecs()) {
		t.Fatalf("snapshot holds %d processes, want %d", len(snap.Processes), len(hostileSpecs()))
	}
	for _, p := range snap.Processes {
		got, err := r.Process(p.PID, bootTime)

		if err != nil || got.Command != p.Command || got.Cmdline != p.Cmdline || !got.StartedAt.Equal(p.StartedAt) {
			t.Errorf("Process(%d) = {%.40q %.40q %v}, %v, want the snapshot's {%.40q %.40q %v}, nil",
				p.PID, got.Command, got.Cmdline, got.StartedAt, err, p.Command, p.Cmdline, p.StartedAt)
		}
	}
}

func processErrors(t *testing.T) {
	t.Run("a missing process has ended", func(t *testing.T) {
		r := NewCommandReader(synthTree().proc)

		got, err := r.Process(4242, bootTime)

		if err == nil || !Ended(err) || got != (ProcessInfo{}) {
			t.Errorf("Process(4242) = %+v, %v (Ended %v), want the zero value and an error that says the process ended", got, err, Ended(err))
		}
	})

	t.Run("a refused file is an error and not an ended process", func(t *testing.T) {
		for _, name := range []string{"stat", "cmdline"} {
			tr := synthTree()
			tr.add(procSpec{pid: 7, comm: "x", cmdline: "x", start: 1})
			tr.proc.openErr[fileOf(7, name)] = syscall.EACCES
			r := NewCommandReader(tr.proc)

			got, err := r.Process(7, bootTime)

			if err == nil || Ended(err) || got != (ProcessInfo{}) {
				t.Errorf("Process(7) with %s refused = %+v, %v (Ended %v), want the zero value and an error that is not an ended process", name, got, err, Ended(err))
			}
		}
	})

	t.Run("a malformed stat is an error", func(t *testing.T) {
		tr := synthTree()
		tr.add(procSpec{pid: 7, comm: "x", cmdline: "x", start: 1})
		setFile(tr.procFiles, fileOf(7, "stat"), "garbage without parentheses\n")
		r := NewCommandReader(tr.proc)

		_, err := r.Process(7, bootTime)

		if err == nil || Ended(err) {
			t.Errorf("Process(7) error = %v (Ended %v), want an error that is not an ended process", err, Ended(err))
		}
	})

	t.Run("a start beyond the year 9999 is an error", func(t *testing.T) {
		tr := synthTree()
		tr.add(procSpec{pid: 7, comm: "x", cmdline: "x", start: 1 << 62})
		r := NewCommandReader(tr.proc)

		_, err := r.Process(7, bootTime)

		if err == nil || Ended(err) {
			t.Errorf("Process(7) with a start beyond year 9999 error = %v (Ended %v), want an error that is not an ended process", err, Ended(err))
		}
	})
}

func TestReadBootTime_Exported(t *testing.T) {
	tests := []struct {
		name string
		stat string
		want time.Time
		fail bool
	}{
		{"captured proc/stat", readFixture(t, "stat"), time.Unix(fixtureBtime, 0).UTC(), false},
		{"btime alone", "btime 1000\n", time.Unix(1000, 0).UTC(), false},
		{"missing", "cpu 1 2 3\nctxt 5\n", time.Time{}, true},
		{"twice", "btime 1000\nbtime 1000\n", time.Time{}, true},
		{"not a number", "btime 10x\n", time.Time{}, true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			proc := fstest.MapFS{"stat": &fstest.MapFile{Data: []byte(tt.stat)}}

			got, err := ReadBootTime(proc)

			if (err != nil) != tt.fail {
				t.Fatalf("ReadBootTime() error = %v, want an error: %v", err, tt.fail)
			}
			if !tt.fail && (!got.Equal(tt.want) || got.Location() != time.UTC) {
				t.Errorf("ReadBootTime() = %v, want %v in UTC", got, tt.want)
			}
		})
	}

	t.Run("stat file missing", func(t *testing.T) {
		if _, err := ReadBootTime(fstest.MapFS{}); err == nil {
			t.Errorf("ReadBootTime(empty /proc) error = nil, want an error")
		}
	})
}
