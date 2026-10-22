package kernevents

import (
	"context"
	"fmt"
	"io"
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
	"testing"
	"testing/fstest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/journal/journaltest"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// The IDs of the committed fixture (testdata/journal) and of the synthetic journals.
const (
	machineID = "0badc0de0badc0de0badc0de0badc0de"
	seqIDA    = "00000000000000000000000000000001"
	seqIDB    = "00000000000000000000000000000002"
	seqIDC    = "00000000000000000000000000000003"
	bootA     = "c0ffee00c0ffee00c0ffee00c0ffee01"
	bootB     = "c0ffee00c0ffee00c0ffee00c0ffee02"
	bootC     = "c0ffee00c0ffee00c0ffee00c0ffee03"
	missedMsg = "e9bf28e6e834481bb6f48f548ad13606"

	// leak is a text no log line may contain; it is never part of a test name or path.
	leak = "zq7leak-victim"
)

// t0 is the start of the first run of every test.
var t0 = time.Date(2026, time.October, 22, 12, 0, 0, 0, time.UTC)

// entry is one journal entry in the form of journaltest.Write.
type entry = map[string][]byte

// dashed returns the UUID form of 32 hex digits.
func dashed(id string) string {
	return id[0:8] + "-" + id[8:12] + "-" + id[12:16] + "-" + id[16:20] + "-" + id[20:32]
}

// usec returns the journal stamp of t in µs.
func usec(t time.Time) uint64 {
	return uint64(t.UnixMicro())
}

// stamp returns the decimal text of a journal stamp.
func stamp(us uint64) []byte {
	return []byte(strconv.FormatUint(us, 10))
}

// kopt changes an entry.
type kopt func(entry)

// withFacilities replaces the SYSLOG_FACILITY items of an entry; none removes them.
func withFacilities(values ...string) kopt {
	return func(e entry) {
		delete(e, "SYSLOG_FACILITY")
		for i, v := range values {
			key := "SYSLOG_FACILITY"
			if i > 0 {
				key += "#" + strconv.Itoa(i+1)
			}
			e[key] = []byte(v)
		}
	}
}

// withTransport sets _TRANSPORT.
func withTransport(v string) kopt {
	return func(e entry) { e["_TRANSPORT"] = []byte(v) }
}

// withSource sets _SOURCE_MONOTONIC_TIMESTAMP to the raw text v.
func withSource(v string) kopt {
	return func(e entry) { e["_SOURCE_MONOTONIC_TIMESTAMP"] = []byte(v) }
}

// withoutSource removes _SOURCE_MONOTONIC_TIMESTAMP.
func withoutSource() kopt {
	return func(e entry) { delete(e, "_SOURCE_MONOTONIC_TIMESTAMP") }
}

// withMonotonic sets the monotonic stamp of the entry.
func withMonotonic(us uint64) kopt {
	return func(e entry) { e["__MONOTONIC_TIMESTAMP"] = stamp(us) }
}

// kernelLine returns an entry of the kernel transport and facility 0 that the journal received at rt (real time, µs) and mono
// (monotonic, µs) and that the kernel wrote at src (µs).
func kernelLine(seq uint64, boot string, rt, mono, src uint64, msg string, opts ...kopt) entry {
	e := entry{
		"__SEQNUM":                    stamp(seq),
		"__REALTIME_TIMESTAMP":        stamp(rt),
		"__MONOTONIC_TIMESTAMP":       stamp(mono),
		"_BOOT_ID":                    []byte(boot),
		"_TRANSPORT":                  []byte("kernel"),
		"SYSLOG_FACILITY":             []byte("0"),
		"SYSLOG_IDENTIFIER":           []byte("kernel"),
		"PRIORITY":                    []byte("6"),
		"_SOURCE_MONOTONIC_TIMESTAMP": stamp(src),
		"MESSAGE":                     []byte(msg),
	}
	for _, o := range opts {
		o(e)
	}
	return e
}

// missedLine returns journald's entry about missed kernel messages.
func missedLine(seq uint64, boot string, rt, mono uint64, opts ...kopt) entry {
	e := entry{
		"__SEQNUM":              stamp(seq),
		"__REALTIME_TIMESTAMP":  stamp(rt),
		"__MONOTONIC_TIMESTAMP": stamp(mono),
		"_BOOT_ID":              []byte(boot),
		"_TRANSPORT":            []byte("driver"),
		"SYSLOG_IDENTIFIER":     []byte("systemd-journald"),
		"PRIORITY":              []byte("4"),
		"MESSAGE_ID":            []byte(missedMsg),
		"MESSAGE":               []byte("Missed 5 kernel messages"),
	}
	for _, o := range opts {
		o(e)
	}
	return e
}

// killMsg returns the kill line of the kernel.
func killMsg(pid int, comm string, rssKB, adj int) string {
	return fmt.Sprintf("Out of memory: Killed process %d (%s) total-vm:1000kB, anon-rss:%dkB, file-rss:0kB, shmem-rss:0kB, UID:0 pgtables:4kB oom_score_adj:%d",
		pid, comm, rssKB, adj)
}

// seqCounter hands out the sequence numbers of one journal.
type seqCounter struct{ n uint64 }

func (c *seqCounter) next() uint64 {
	c.n += 2 // seqnums are not dense
	return c.n
}

// fileKind says how a journal file is named and flagged.
type fileKind int

const (
	kindArchived fileKind = iota // system@...journal, state archived
	kindOnline                   // system.journal, state online
	kindSetAside                 // system@...journal~, state online as after an unclean shutdown
)

// journalFS is the journal root the collector reads: the machine directory with the journal files, hooks on every open and read, and
// a count of the reads.
//
// It models a journal directory as journald writes it: files named as journald names them (system.journal, system@<seqnum
// ID>-<head seqnum>-<head real time>.journal and the .journal~ suffix of a set-aside file), written by journaltest in the layout
// of systemd 249 (pinned against journalctl by journaltest.TestWrite_JournalctlAcceptsFixture). Growth of a journal is modelled by
// adding a file, as journald's rotation does, because journaltest cannot append to a file.
type journalFS struct {
	mu     sync.Mutex
	files  map[string][]byte
	noDir  bool
	denied error
	hook   func(op, name string, off int64) error
	reads  atomic.Int64
	nextID byte
}

func newJournalFS() *journalFS {
	return &journalFS{files: map[string][]byte{}}
}

// fileName returns the name journald gives a file.
func fileName(kind fileKind, seqID string, headSeq, headRT uint64) string {
	if kind == kindOnline {
		return "system.journal"
	}
	name := fmt.Sprintf("system@%s-%016x-%016x.journal", seqID, headSeq, headRT)
	if kind == kindSetAside {
		name += "~"
	}
	return name
}

// mustID decodes 32 hex digits.
func mustID(t testing.TB, s string) [16]byte {
	t.Helper()
	var id [16]byte
	if len(s) != 32 {
		t.Fatalf("ID %q has %d characters, want 32", s, len(s))
	}
	for i := range id {
		v, err := strconv.ParseUint(s[2*i:2*i+2], 16, 8)
		if err != nil {
			t.Fatalf("ID %q is not hex: %v", s, err)
		}
		id[i] = byte(v)
	}
	return id
}

// add writes entries as a journal file of the given seqnum ID and returns its name.
func (j *journalFS) add(t testing.TB, kind fileKind, seqID string, entries []entry) string {
	t.Helper()
	j.mu.Lock()
	j.nextID++
	fid := j.nextID
	j.mu.Unlock()
	var fileID [16]byte
	for i := range fileID {
		fileID[i] = fid
	}
	var buf strings.Builder
	opts := journaltest.Options{FileID: fileID, MachineID: mustID(t, machineID), SeqnumID: mustID(t, seqID), Online: kind != kindArchived}
	if err := journaltest.Write(&buf, entries, opts); err != nil {
		t.Fatalf("journaltest.Write() error = %v, want nil", err)
	}
	headSeq, _ := strconv.ParseUint(string(entries[0]["__SEQNUM"]), 10, 64)
	headRT, _ := strconv.ParseUint(string(entries[0]["__REALTIME_TIMESTAMP"]), 10, 64)
	name := fileName(kind, seqID, headSeq, headRT)
	j.put(name, []byte(buf.String()))
	return name
}

// put stores the bytes of a file.
func (j *journalFS) put(name string, data []byte) {
	j.mu.Lock()
	defer j.mu.Unlock()
	j.files[name] = data
}

// data returns a copy of the bytes of a file.
func (j *journalFS) data(name string) []byte {
	j.mu.Lock()
	defer j.mu.Unlock()
	return slices.Clone(j.files[name])
}

// setHook installs the function called before every open and read of a journal file; nil removes it.
func (j *journalFS) setHook(h func(op, name string, off int64) error) {
	j.mu.Lock()
	defer j.mu.Unlock()
	j.hook = h
}

// setDenied makes opening the machine directory fail with err; nil lifts it.
func (j *journalFS) setDenied(err error) {
	j.mu.Lock()
	defer j.mu.Unlock()
	j.denied = err
}

// setNoDir hides the machine directory.
func (j *journalFS) setNoDir(v bool) {
	j.mu.Lock()
	defer j.mu.Unlock()
	j.noDir = v
}

// loadFixture copies the committed journal fixture files into j.
func (j *journalFS) loadFixture(t testing.TB) {
	t.Helper()
	dir := filepath.Join("..", "..", "testdata", "journal")
	names, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("reading the fixture directory: %v", err)
	}
	n := 0
	for _, de := range names {
		if !strings.HasPrefix(de.Name(), "system") {
			continue
		}
		b, err := os.ReadFile(filepath.Join(dir, de.Name()))
		if err != nil {
			t.Fatalf("reading the fixture file: %v", err)
		}
		j.put(de.Name(), b)
		n++
	}
	if n != 3 {
		t.Fatalf("the fixture directory holds %d journal files, want 3", n)
	}
}

// Open implements fs.FS.
func (j *journalFS) Open(name string) (fs.File, error) {
	j.mu.Lock()
	m := fstest.MapFS{}
	if !j.noDir {
		m[machineID] = &fstest.MapFile{Mode: fs.ModeDir | 0o755}
		for n, d := range j.files {
			m[machineID+"/"+n] = &fstest.MapFile{Data: d, Mode: 0o640}
		}
	}
	hook, denied := j.hook, j.denied
	j.mu.Unlock()

	if denied != nil && (name == machineID || strings.HasPrefix(name, machineID+"/")) {
		return nil, denied
	}
	f, err := m.Open(name)
	if err != nil {
		return nil, err
	}
	st, err := f.Stat()
	if err != nil || st.IsDir() {
		return f, err
	}
	if hook != nil {
		if herr := hook("open", name, 0); herr != nil {
			_ = f.Close()
			return nil, herr
		}
	}
	return &hookFile{File: f, name: name, j: j}, nil
}

// hookFile is an open journal file that counts and hooks its reads.
type hookFile struct {
	fs.File
	name string
	j    *journalFS
}

func (h *hookFile) before(off int64) error {
	h.j.reads.Add(1)
	h.j.mu.Lock()
	hook := h.j.hook
	h.j.mu.Unlock()
	if hook == nil {
		return nil
	}
	return hook("read", h.name, off)
}

// ReadAt implements io.ReaderAt.
func (h *hookFile) ReadAt(p []byte, off int64) (int, error) {
	if err := h.before(off); err != nil {
		return 0, err
	}
	return h.File.(io.ReaderAt).ReadAt(p, off)
}

// Read implements io.Reader.
func (h *hookFile) Read(p []byte) (int, error) {
	if err := h.before(0); err != nil {
		return 0, err
	}
	return h.File.Read(p)
}

// Seek implements io.Seeker.
func (h *hookFile) Seek(off int64, whence int) (int64, error) {
	return h.File.(io.Seeker).Seek(off, whence)
}

// logLine is one captured log record.
type logLine struct {
	level slog.Level
	msg   string
	attrs map[string]string
}

// logSink is a slog.Handler that keeps what is logged.
type logSink struct {
	mu    sync.Mutex
	lines []logLine
}

func (s *logSink) Enabled(context.Context, slog.Level) bool { return true }

func (s *logSink) Handle(_ context.Context, r slog.Record) error {
	l := logLine{level: r.Level, msg: r.Message, attrs: map[string]string{}}
	r.Attrs(func(a slog.Attr) bool {
		l.attrs[a.Key] = fmt.Sprint(a.Value.Any())
		return true
	})
	s.mu.Lock()
	s.lines = append(s.lines, l)
	s.mu.Unlock()
	return nil
}

func (s *logSink) WithAttrs([]slog.Attr) slog.Handler { return s }

func (s *logSink) WithGroup(string) slog.Handler { return s }

// all returns the lines logged so far.
func (s *logSink) all() []logLine {
	s.mu.Lock()
	defer s.mu.Unlock()
	return slices.Clone(s.lines)
}

// find returns the lines with the level and message.
func (s *logSink) find(level slog.Level, msg string) []logLine {
	var out []logLine
	for _, l := range s.all() {
		if l.level == level && l.msg == msg {
			out = append(out, l)
		}
	}
	return out
}

// text returns every message and attribute as one string.
func (s *logSink) text() string {
	var b strings.Builder
	for _, l := range s.all() {
		b.WriteString(l.msg)
		for k, v := range l.attrs {
			b.WriteString(" " + k + "=" + v)
		}
		b.WriteString("\n")
	}
	return b.String()
}

// trigCall is one request for an extra run.
type trigCall struct {
	collector string
	trigger   scheduler.Trigger
}

// trigRec records the calls of the TriggerFunc.
type trigRec struct {
	mu    sync.Mutex
	calls []trigCall
}

func (r *trigRec) fn(collector string, t scheduler.Trigger) bool {
	t.Kills = slices.Clone(t.Kills)
	r.mu.Lock()
	r.calls = append(r.calls, trigCall{collector: collector, trigger: t})
	r.mu.Unlock()
	return true
}

func (r *trigRec) count() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.calls)
}

func (r *trigRec) since(n int) []trigCall {
	r.mu.Lock()
	defer r.mu.Unlock()
	return slices.Clone(r.calls[n:])
}

// env is a collector with its sources.
type env struct {
	t     *testing.T
	j     *journalFS
	etc   fstest.MapFS
	proc  fstest.MapFS
	logs  *logSink
	trig  *trigRec
	dir   string
	store *agentstate.Store
	k     *Kernel
	start time.Time
	runs  int
}

// newEnv returns an environment in boot bootB with a far uptime (no kill is live), a first-start state and no journal files.
func newEnv(t *testing.T) *env {
	t.Helper()
	e := &env{
		t:     t,
		j:     newJournalFS(),
		etc:   fstest.MapFS{"machine-id": {Data: []byte(machineID + "\n")}},
		proc:  fstest.MapFS{},
		logs:  &logSink{},
		trig:  &trigRec{},
		dir:   t.TempDir(),
		start: t0,
	}
	e.setBoot(dashed(bootB))
	e.setUptime(5_000_000)
	e.proc["stat"] = &fstest.MapFile{Data: []byte("cpu  1 2 3 4\nbtime 1790000000\nprocesses 5\n")}
	e.openStore()
	e.newKernel()
	return e
}

// bootTime is the btime of the proc files of an environment.
var bootTime = time.Unix(1790000000, 0).UTC()

func (e *env) setBoot(raw string) {
	e.proc["sys/kernel/random/boot_id"] = &fstest.MapFile{Data: []byte(raw + "\n")}
}

func (e *env) setUptime(sec float64) {
	e.proc["uptime"] = &fstest.MapFile{Data: []byte(fmt.Sprintf("%.2f 100.00\n", sec))}
}

func (e *env) openStore() {
	e.store, _ = agentstate.Open(e.dir, slog.New(e.logs))
}

func (e *env) newKernel() {
	e.k = New(Sources{Journal: []fs.FS{e.j}, Etc: e.etc, Proc: e.proc}, e.store, slog.New(e.logs))
	e.k.SetTrigger(e.trig.fn)
}

// seed puts the boot and the journal position into the saved state.
func (e *env) seed(boot string, pos *agentstate.JournalPosition) {
	e.store.Update(false, func(s *agentstate.State) {
		if boot != "" {
			s.Boot = &agentstate.Boot{ID: boot, Uptime: time.Hour, BootedAt: bootTime}
		}
		s.Journal = pos
	})
}

// position returns a saved journal position.
func position(seqID string, seq, rt uint64) *agentstate.JournalPosition {
	return &agentstate.JournalPosition{SeqnumID: dashed(seqID), Seqnum: seq, Realtime: time.UnixMicro(int64(rt)).UTC()} // #nosec G115 -- test values
}

// run makes one run starting two seconds after the previous one and returns its records; lost says that the previous result was dropped.
func (e *env) run(lost bool) []model.Record {
	e.t.Helper()
	recs, err := e.collect(lost)
	if err != nil {
		e.t.Fatalf("run %d: Collect() error = %v, want nil", e.runs, err)
	}
	return recs
}

// collect makes one run and returns the records and the error.
func (e *env) collect(lost bool) ([]model.Record, error) {
	e.t.Helper()
	run := scheduler.Run{Start: e.start, PreviousLost: lost}
	e.start = e.start.Add(2 * time.Second)
	e.runs++
	return e.k.Collect(context.Background(), run)
}

// checkValid requires that every record is of source kernel and passes the check the scheduler makes.
func checkValid(t testing.TB, recs []model.Record, start time.Time) {
	t.Helper()
	for i := range recs {
		if recs[i].Source != Name {
			t.Errorf("record %d: source = %q, want %q", i, recs[i].Source, Name)
		}
		c := recs[i]
		c.Origin, c.Seq = model.OriginAgent, math.MaxInt64
		if c.CapturedAt.IsZero() {
			c.CapturedAt = start
		}
		if _, err := wire.CheckRecord(&c); err != nil {
			t.Errorf("record %d (%s): wire.CheckRecord() error = %v, want nil", i, recs[i].Kind(), err)
		}
	}
}

// killOf is a recorded oom_kill event with the time it was captured.
type killOf struct {
	rec model.Record
	ev  *model.KernelEvent
}

// killsOf returns the oom_kill events in the order of recs.
func killsOf(recs []model.Record) []killOf {
	var out []killOf
	for _, r := range recs {
		if ev, ok := r.Data.(*model.KernelEvent); ok && ev.Type == model.KernelEventOOMKill {
			out = append(out, killOf{rec: r, ev: ev})
		}
	}
	return out
}

// bootsOf returns the boot events.
func bootsOf(recs []model.Record) []killOf {
	var out []killOf
	for _, r := range recs {
		if ev, ok := r.Data.(*model.KernelEvent); ok && ev.Type == model.KernelEventBoot {
			out = append(out, killOf{rec: r, ev: ev})
		}
	}
	return out
}

// gapOf is a gap record with its payload.
type gapOf struct {
	rec model.Record
	gap *model.Gap
}

// gapsOf returns the gap records.
func gapsOf(recs []model.Record) []gapOf {
	var out []gapOf
	for _, r := range recs {
		if g, ok := r.Data.(*model.Gap); ok {
			out = append(out, gapOf{rec: r, gap: g})
		}
	}
	return out
}

// pids returns the victim PIDs of the kills.
func pids(ks []killOf) []int32 {
	out := make([]int32, 0, len(ks))
	for _, k := range ks {
		out = append(out, k.ev.OOMKill.VictimPID)
	}
	return out
}

// requireGap checks one gap of the kernel collector.
func requireGap(t testing.TB, g gapOf, from, to time.Time) {
	t.Helper()
	if !g.gap.From.Equal(from) || !g.gap.To.Equal(to) {
		t.Errorf("gap = [%s, %s), want [%s, %s)", g.gap.From.Format(time.RFC3339Nano), g.gap.To.Format(time.RFC3339Nano),
			from.Format(time.RFC3339Nano), to.Format(time.RFC3339Nano))
	}
	if g.gap.Cause != model.GapUnknown || g.gap.Collector != Name || g.rec.Source != Name {
		t.Errorf("gap cause = %q, collector = %q, source = %q, want %q, %q, %q", g.gap.Cause, g.gap.Collector, g.rec.Source, model.GapUnknown, Name, Name)
	}
	if g.gap.From.Location() != time.UTC || g.gap.To.Location() != time.UTC {
		t.Errorf("gap times are in %v and %v, want UTC", g.gap.From.Location(), g.gap.To.Location())
	}
}

// micro returns the time of a journal stamp.
func micro(us uint64) time.Time {
	return time.UnixMicro(int64(us)).UTC() // #nosec G115 -- test values
}

// killIdentity is the identity of a kill as the snapshot trigger names it.
func killIdentity(boot string, srcUS uint64, pid int32) model.KillRef {
	return model.KillRef{BootID: dashed(boot), KernelTime: time.Duration(srcUS) * time.Microsecond, VictimPID: pid} // #nosec G115 -- test values
}

// identityOf returns the identity the event carries.
func identityOf(t testing.TB, k killOf) model.KillRef {
	t.Helper()
	o := k.ev.OOMKill
	if o.KernelTime == nil {
		t.Fatalf("oom_kill event of PID %d has no kernel_time_ns", o.VictimPID)
	}
	return model.KillRef{BootID: o.BootID, KernelTime: *o.KernelTime, VictimPID: o.VictimPID}
}

// The synthetic timeline: second s of it is the real time r0()+s and the monotonic time m0+s (both in µs); the kernel wrote a line
// lagUS before the journal received it unless a test says otherwise.
const (
	m0    = 2_000_000_000
	lagUS = 300_000
)

// r0 is the real time of second 0 of the timeline, five seconds before the first run.
func r0() uint64 {
	return usec(t0) - 5*1_000_000
}

// tl returns the real and monotonic stamps of second sec of the timeline.
func tl(sec int) (rt, mono uint64) {
	us := uint64(sec) * 1_000_000 // #nosec G115 -- test values
	return r0() + us, m0 + us
}

// line returns a plain kernel line received at second sec of the timeline.
func (c *seqCounter) line(boot string, sec int, opts ...kopt) entry {
	rt, mono := tl(sec)
	seq := c.next()
	return kernelLine(seq, boot, rt, mono, mono-lagUS, fmt.Sprintf("handmade kernel line %d", seq), opts...)
}

// kill returns a kill line received at second sec of the timeline that the kernel wrote lag µs before.
func (c *seqCounter) kill(boot string, sec int, pid int, comm string, lag uint64, opts ...kopt) entry {
	rt, mono := tl(sec)
	return kernelLine(c.next(), boot, rt, mono, mono-lag, killMsg(pid, comm, 100, 0), opts...)
}

// killSrc returns the _SOURCE_MONOTONIC_TIMESTAMP of kill(boot, sec, ..., lag).
func killSrc(sec int, lag uint64) uint64 {
	_, mono := tl(sec)
	return mono - lag
}

// before is a saved position before the whole timeline.
func before() *agentstate.JournalPosition {
	return position(seqIDA, 0, r0()-1_000_000)
}

// tailOf returns the saved position after the last entry of entries.
func tailOf(seqID string, entries []entry) *agentstate.JournalPosition {
	last := entries[len(entries)-1]
	seq, _ := strconv.ParseUint(string(last["__SEQNUM"]), 10, 64)
	rt, _ := strconv.ParseUint(string(last["__REALTIME_TIMESTAMP"]), 10, 64)
	return position(seqID, seq, rt)
}

// rtOf returns the real-time stamp of an entry.
func rtOf(e entry) uint64 {
	v, _ := strconv.ParseUint(string(e["__REALTIME_TIMESTAMP"]), 10, 64)
	return v
}

// uptimeNow makes /proc/uptime read plus seconds more than the monotonic time of the start of the next run on the timeline.
func (e *env) uptimeNow(plus float64) {
	sec := float64(usec(e.start)-r0()) / 1e6
	e.setUptime(float64(m0)/1e6 + sec + plus)
}

// add writes a journal file of the seqnum ID A with entries.
func (e *env) add(entries ...entry) string {
	e.t.Helper()
	return e.j.add(e.t, kindArchived, seqIDA, entries)
}
