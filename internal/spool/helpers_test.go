package spool

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"math"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	testAgent   = "agent-one"
	testBoot    = "0f0f0f0f-1111-4222-8333-444455556666"
	otherBoot   = "9a9a9a9a-aaaa-4bbb-8ccc-ddddeeeeffff"
	thirdBoot   = "12345678-1234-4234-8234-123456789abc"
	stateName   = "agent-state.json"
	metaName    = "spool-meta.json"
	metaTmpName = "spool-meta.json.tmp"
	mebi        = int64(1) << 20
	// hugeAvailable is the free space of a disk that never constrains the spool.
	hugeAvailable = uint64(1) << 50
)

// epoch is the injected clock's start and the base of the capture times of the tests.
var epoch = time.Date(2026, time.March, 1, 12, 0, 0, 0, time.UTC)

// fakeClock is the injected Options.Now.
type fakeClock struct {
	mu  sync.Mutex
	now time.Time
}

// Now returns the injected time.
func (c *fakeClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()

	return c.now
}

// Set moves the clock to t.
func (c *fakeClock) Set(t time.Time) {
	c.mu.Lock()
	defer c.mu.Unlock()

	c.now = t
}

// Advance moves the clock on by d.
func (c *fakeClock) Advance(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()

	c.now = c.now.Add(d)
}

// fakeDisk is the injected Options.Available: a scripted number of available bytes.
type fakeDisk struct {
	mu    sync.Mutex
	avail uint64
	dirs  []string
}

// Available returns the scripted value and records the directory asked about.
func (d *fakeDisk) Available(dir string) (uint64, error) {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.dirs = append(d.dirs, dir)

	return d.avail, nil
}

// Set scripts the number of available bytes.
func (d *fakeDisk) Set(v uint64) {
	d.mu.Lock()
	defer d.mu.Unlock()

	d.avail = v
}

// virtualDisk models a file system of a fixed size on which only the spool's files take space: the available bytes
// are the capacity minus the sizes of the spool's files in the directory. It is a model, not a measurement: the real
// statfs counts allocated blocks (rounded up to the block size) of every file of the file system.
type virtualDisk struct {
	capacity int64
	t        *testing.T
}

// Available returns the capacity minus the bytes the spool's files take in dir, never below zero.
func (d virtualDisk) Available(dir string) (uint64, error) {
	free := d.capacity - diskBytes(d.t, dir)
	if free < 0 {
		return 0, nil
	}

	return u64(d.t, free), nil
}

// logEntry is one log line the spool wrote.
type logEntry struct {
	level slog.Level
	msg   string
	attrs map[string]slog.Value
}

// logRecorder is a slog.Handler that keeps every record.
type logRecorder struct {
	mu      *sync.Mutex
	entries *[]logEntry
	pre     []slog.Attr
}

func newLogRecorder() *logRecorder {
	return &logRecorder{mu: &sync.Mutex{}, entries: &[]logEntry{}}
}

// Enabled accepts every level.
func (l *logRecorder) Enabled(context.Context, slog.Level) bool { return true }

// Handle keeps the record with its attributes.
func (l *logRecorder) Handle(_ context.Context, r slog.Record) error {
	e := logEntry{level: r.Level, msg: r.Message, attrs: map[string]slog.Value{}}
	for _, a := range l.pre {
		e.attrs[a.Key] = a.Value.Resolve()
	}
	r.Attrs(func(a slog.Attr) bool {
		e.attrs[a.Key] = a.Value.Resolve()

		return true
	})
	l.mu.Lock()
	defer l.mu.Unlock()
	*l.entries = append(*l.entries, e)

	return nil
}

// WithAttrs returns a handler that adds attrs to every record.
func (l *logRecorder) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &logRecorder{mu: l.mu, entries: l.entries, pre: append(slices.Clone(l.pre), attrs...)}
}

// WithGroup ignores the group.
func (l *logRecorder) WithGroup(string) slog.Handler { return l }

// all returns a copy of the recorded entries.
func (l *logRecorder) all() []logEntry {
	l.mu.Lock()
	defer l.mu.Unlock()

	return slices.Clone(*l.entries)
}

// find returns the entries whose message is msg.
func (l *logRecorder) find(msg string) []logEntry {
	var out []logEntry
	for _, e := range l.all() {
		if e.msg == msg {
			out = append(out, e)
		}
	}

	return out
}

// count returns how many entries carry the message msg.
func (l *logRecorder) count(msg string) int { return len(l.find(msg)) }

// reset forgets the recorded entries.
func (l *logRecorder) reset() {
	l.mu.Lock()
	defer l.mu.Unlock()
	*l.entries = nil
}

// text returns the entry as one string (message and attributes) for leak checks.
func (e logEntry) text() string {
	var b strings.Builder
	b.WriteString(e.msg)
	for k, v := range e.attrs {
		b.WriteString(" " + k + "=" + v.String())
	}

	return b.String()
}

// str returns a string attribute, or "" when it is absent.
func (e logEntry) str(key string) string {
	v, ok := e.attrs[key]
	if !ok {
		return ""
	}

	return v.String()
}

// num returns a numeric attribute.
func (e logEntry) num(key string) (int64, bool) {
	v, ok := e.attrs[key]
	if !ok {
		return 0, false
	}
	switch v.Kind() {
	case slog.KindInt64:
		return v.Int64(), true
	case slog.KindUint64:
		u := v.Uint64()
		if u > math.MaxInt64 {
			return 0, false
		}

		return int64(u), true
	default:
		return 0, false
	}
}

// rig is one spool directory with its injected clock, disk and log recorder.
type rig struct {
	t      *testing.T
	opts   Options
	clk    *fakeClock
	logs   *logRecorder
	disk   *fakeDisk
	closed map[*Spool]bool
}

// newRig returns a rig on a fresh temporary directory with a roomy cap, the reserve switched off and a sync interval
// that never fires within a test.
func newRig(t *testing.T) *rig {
	t.Helper()

	r := &rig{t: t, clk: &fakeClock{now: epoch}, logs: newLogRecorder(), disk: &fakeDisk{avail: hugeAvailable}, closed: map[*Spool]bool{}}
	r.opts = Options{
		Dir:          t.TempDir(),
		AgentID:      testAgent,
		BootID:       testBoot,
		MaxBytes:     64 * mebi,
		SyncInterval: time.Hour,
		Now:          r.clk.Now,
		Available:    r.disk.Available,
	}

	return r
}

// dir returns the spool directory.
func (r *rig) dir() string { return r.opts.Dir }

// tryOpen opens a spool with the rig's options and returns the error without failing the test.
func (r *rig) tryOpen() (*Spool, error) {
	r.t.Helper()

	sp, err := Open(r.opts, slog.New(r.logs))
	if err != nil {
		return nil, err
	}
	r.t.Cleanup(func() {
		if !r.closed[sp] {
			_ = sp.Close(context.Background())
		}
	})

	return sp, nil
}

// open opens a spool with the rig's options and fails the test when that is refused.
func (r *rig) open() *Spool {
	r.t.Helper()

	sp, err := r.tryOpen()
	if err != nil {
		r.t.Fatalf("Open(%+v) = %v, want a spool", r.describe(), err)
	}

	return sp
}

// describe returns the options without the directory, for messages.
func (r *rig) describe() string {
	return fmt.Sprintf("agent=%s boot=%s max=%d minfree=%d", r.opts.AgentID, r.opts.BootID, r.opts.MaxBytes, r.opts.MinFreeBytes)
}

// closeSpool closes sp and remembers that the cleanup must not close it again.
func (r *rig) closeSpool(sp *Spool) {
	r.t.Helper()

	r.closed[sp] = true
	if err := sp.Close(context.Background()); err != nil {
		r.t.Fatalf("Close() = %v, want nil", err)
	}
}

// reopen closes sp and opens the directory again with the rig's current options.
func (r *rig) reopen(sp *Spool) *Spool {
	r.t.Helper()

	r.closeSpool(sp)

	return r.open()
}

// snapshot copies the directory as it is now, as a kill -9 or a power loss would leave it, and returns a rig whose
// options point at the copy. The original spool stays open.
func (r *rig) snapshot() *rig {
	r.t.Helper()

	c := *r
	c.opts.Dir = copyDir(r.t, r.dir())
	c.closed = map[*Spool]bool{}

	return &c
}

// u64 converts a non-negative int64.
func u64(t testing.TB, n int64) uint64 {
	t.Helper()

	if n < 0 {
		t.Fatalf("u64(%d): negative value in a test", n)

		return 0
	}

	return uint64(n)
}

// i64 converts a uint64 below 2^63.
func i64(t testing.TB, n uint64) int64 {
	t.Helper()

	if n > math.MaxInt64 {
		t.Fatalf("i64(%d): value too large for a test", n)

		return 0
	}

	return int64(n)
}

// ofLen converts a slice length to uint64.
func ofLen(t testing.TB, n int) uint64 {
	t.Helper()

	return u64(t, int64(n))
}

// segName returns the file name of the data file whose first record has the number first.
func segName(first uint64) string {
	return fmt.Sprintf("segment-%020d%s", first, segmentExt)
}

// segNumber returns the number of a data file name, and false for any other name.
func segNumber(name string) (uint64, bool) {
	rest, ok := strings.CutPrefix(name, "segment-")
	if !ok {
		return 0, false
	}
	digits, ok := strings.CutSuffix(rest, segmentExt)
	if !ok || len(digits) != 20 {
		return 0, false
	}
	var n uint64
	for _, c := range digits {
		if c < '0' || c > '9' {
			return 0, false
		}
		d := uint64(c - '0')
		if n > (math.MaxUint64-d)/10 {
			return 0, false
		}
		n = n*10 + d
	}

	return n, true
}

// isSpoolName reports whether name is one of the spool's own file names.
func isSpoolName(name string) bool {
	_, isSeg := segNumber(name)

	return isSeg || name == metaName || name == metaTmpName
}

// diskBytes returns the sum of the sizes of the spool's own files in dir.
func diskBytes(t *testing.T, dir string) int64 {
	t.Helper()

	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("ReadDir(spool dir) = %v", err)
	}
	var sum int64
	for _, e := range entries {
		if !isSpoolName(e.Name()) {
			continue
		}
		info, err := e.Info()
		if err != nil {
			t.Fatalf("Info(%s) = %v", e.Name(), err)
		}
		sum += info.Size()
	}

	return sum
}

// segmentNumbers returns the first numbers of the data files in dir, ascending.
func segmentNumbers(t *testing.T, dir string) []uint64 {
	t.Helper()

	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("ReadDir(spool dir) = %v", err)
	}
	var out []uint64
	for _, e := range entries {
		if n, ok := segNumber(e.Name()); ok {
			out = append(out, n)
		}
	}
	slices.Sort(out)

	return out
}

// copyDir copies the regular files of src into a new temporary directory and returns it.
func copyDir(t *testing.T, src string) string {
	t.Helper()

	dst := t.TempDir()
	entries, err := os.ReadDir(src)
	if err != nil {
		t.Fatalf("ReadDir(copy source) = %v", err)
	}
	for _, e := range entries {
		if !e.Type().IsRegular() {
			continue
		}
		data, err := os.ReadFile(filepath.Join(src, e.Name()))
		if err != nil {
			t.Fatalf("ReadFile(%s) = %v", e.Name(), err)
		}
		if err := os.WriteFile(filepath.Join(dst, e.Name()), data, 0o600); err != nil {
			t.Fatalf("WriteFile(%s) = %v", e.Name(), err)
		}
	}

	return dst
}

// filler returns n bytes of printable, JSON-safe text.
func filler(n int) string {
	const unit = "abcdefghijklmnopqrstuvwxyz0123456789"
	var b strings.Builder
	for b.Len() < n {
		b.WriteString(unit)
	}

	return b.String()[:n]
}

// logRec returns a log line record of the agent with the message tag followed by filler up to size bytes.
func logRec(at time.Time, tag string, size int) model.Record {
	msg := tag + " " + filler(max(size-len(tag)-1, 0))

	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: "logs", CapturedAt: at},
		Data: &model.LogLine{Log: "app.log", Message: msg},
	}
}

// metricRec returns a metric record of the agent.
func metricRec(at time.Time, name string, value float64) model.Record {
	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: "system", CapturedAt: at},
		Data: &model.MetricPoint{Name: name, Value: value, Unit: "bytes"},
	}
}

// batchOf returns n log records tagged tag-0 to tag-(n-1), captured at at, each of about size bytes.
func batchOf(at time.Time, tag string, n, size int) []model.Record {
	out := make([]model.Record, 0, n)
	for i := range n {
		out = append(out, logRec(at, fmt.Sprintf("%s-%d", tag, i), size))
	}

	return out
}

// numbered returns a copy of recs with the sequence numbers first, first+1, ...
func numbered(recs []model.Record, first uint64) []model.Record {
	out := slices.Clone(recs)
	next := first
	for i := range out {
		out[i].Seq = next
		next++
	}

	return out
}

// lineOf returns the wire line of r.
func lineOf(t *testing.T, r model.Record) string {
	t.Helper()

	line, err := wire.MarshalRecord(&r)
	if err != nil {
		t.Fatalf("MarshalRecord(seq %d) = %v", r.Seq, err)
	}

	return string(line)
}

// requireSame fails unless got holds the records of want, with the same numbers and wire lines, in order.
func requireSame(t *testing.T, what string, got, want []model.Record) {
	t.Helper()

	if len(got) != len(want) {
		t.Fatalf("%s: got %d records (numbers %v), want %d (numbers %v)", what, len(got), seqsHead(got), len(want), seqsHead(want))
	}
	for i := range want {
		if got[i].Seq != want[i].Seq {
			t.Fatalf("%s: record %d has number %d, want %d", what, i, got[i].Seq, want[i].Seq)
		}
		if g, w := lineOf(t, got[i]), lineOf(t, want[i]); g != w {
			t.Fatalf("%s: record %d (number %d) reads back as %.200q, want %.200q", what, i, want[i].Seq, g, w)
		}
	}
}

// seqs returns the sequence numbers of recs.
func seqs(recs []model.Record) []uint64 {
	out := make([]uint64, len(recs))
	for i := range recs {
		out[i] = recs[i].Seq
	}

	return out
}

// seqsHead returns at most the first 12 numbers of recs, for messages.
func seqsHead(recs []model.Record) []uint64 {
	return seqs(recs[:min(len(recs), 12)])
}

// gapOf returns the gap payload of r, or nil.
func gapOf(r model.Record) *model.Gap {
	g, _ := r.Data.(*model.Gap)

	return g
}

// gapsOf returns the gap records of recs.
func gapsOf(recs []model.Record, cause model.GapCause) []model.Record {
	var out []model.Record
	for _, r := range recs {
		if g := gapOf(r); g != nil && g.Cause == cause {
			out = append(out, r)
		}
	}

	return out
}

// dataOf returns the records of recs that are not gaps.
func dataOf(recs []model.Record) []model.Record {
	var out []model.Record
	for _, r := range recs {
		if gapOf(r) == nil {
			out = append(out, r)
		}
	}

	return out
}

// readPieces reads everything unacknowledged from after in pieces and returns the pieces. It acknowledges nothing.
func readPieces(t *testing.T, sp *Spool, after uint64, maxRecords int, maxBytes int64) []Batch {
	t.Helper()

	var out []Batch
	for range 1_000_000 {
		b, err := sp.Read(context.Background(), after, maxRecords, maxBytes)
		if err != nil {
			t.Fatalf("Read(after %d) = %v", after, err)
		}
		if len(b.Records) == 0 {
			return out
		}
		out = append(out, b)
		after = b.Records[len(b.Records)-1].Seq
	}
	t.Fatalf("Read never returned an empty batch")

	return nil
}

// readAll reads every unacknowledged record from the oldest and returns them in the order handed out.
func readAll(t *testing.T, sp *Spool) []model.Record {
	t.Helper()

	var out []model.Record
	for _, b := range readPieces(t, sp, 0, 4096, 64*mebi) {
		out = append(out, b.Records...)
	}

	return out
}

// mustPut hands recs to the spool and fails the test when it is refused.
func mustPut(t *testing.T, sp *Spool, recs []model.Record) {
	t.Helper()

	if err := sp.Put(context.Background(), slices.Clone(recs)); err != nil {
		t.Fatalf("Put(%d records) = %v, want nil", len(recs), err)
	}
}

// mustAck acknowledges first to last and fails the test on an error.
func mustAck(t *testing.T, sp *Spool, first, last uint64) {
	t.Helper()

	if err := sp.Ack(context.Background(), first, last); err != nil {
		t.Fatalf("Ack(%d, %d) = %v, want nil", first, last, err)
	}
}

// putTagged hands n tagged log records captured at at to the spool and returns them numbered from the first number
// the spool issued, which is read from LastSeq.
func putTagged(t *testing.T, sp *Spool, at time.Time, tag string, n, size int) []model.Record {
	t.Helper()

	recs := batchOf(at, tag, n, size)
	mustPut(t, sp, recs)
	last := sp.LastSeq()

	return numbered(recs, last-ofLen(t, n)+1)
}

// stat is the state of a file that a guard test compares before and after.
type stat struct {
	data  []byte
	size  int64
	mtime time.Time
	mode  os.FileMode
}

// statOf reads the content, size, modification time and mode of the file at path (following no link).
func statOf(t *testing.T, path string) stat {
	t.Helper()

	info, err := os.Lstat(path)
	if err != nil {
		t.Fatalf("Lstat(%s) = %v", filepath.Base(path), err)
	}
	s := stat{size: info.Size(), mtime: info.ModTime(), mode: info.Mode()}
	if info.Mode().IsRegular() {
		s.data, err = os.ReadFile(path)
		if err != nil {
			t.Fatalf("ReadFile(%s) = %v", filepath.Base(path), err)
		}
	}

	return s
}

// requireUnchanged fails when the file at path differs from before.
func requireUnchanged(t *testing.T, what, path string, before stat) {
	t.Helper()

	after := statOf(t, path)
	if string(after.data) != string(before.data) || after.size != before.size || !after.mtime.Equal(before.mtime) || after.mode != before.mode {
		t.Fatalf("%s changed: size %d -> %d, mtime %v -> %v, mode %v -> %v, bytes equal %v",
			what, before.size, after.size, before.mtime, after.mtime, before.mode, after.mode, string(after.data) == string(before.data))
	}
}

// requireNoPath fails when text contains the directory, which no error or log line may name.
func requireNoPath(t *testing.T, what, text, dir string) {
	t.Helper()

	if strings.Contains(text, dir) {
		t.Fatalf("%s names the directory: %q", what, text)
	}
}

// requireNoLeak fails when any log line names the directory.
func requireNoLeak(t *testing.T, r *rig) {
	t.Helper()

	for _, e := range r.logs.all() {
		requireNoPath(t, "log line "+e.msg, e.text(), r.dir())
	}
}

// requireGapsDisjoint fails when a spool_dropped gap names a number that a record of recs carries, or a time outside
// the range the wire format stores.
func requireGapsDisjoint(t *testing.T, recs []model.Record) {
	t.Helper()

	lo, hi := time.Unix(0, math.MinInt64).UTC(), time.Unix(0, math.MaxInt64).UTC()
	for _, g := range gapsOf(recs, model.GapSpoolDropped) {
		gp := gapOf(g)
		for _, r := range recs {
			if r.Seq >= gp.FirstSeq && r.Seq <= gp.LastSeq {
				t.Fatalf("gap %d..%d covers the kept record %d", gp.FirstSeq, gp.LastSeq, r.Seq)
			}
		}
		if gp.From.Before(lo) || gp.To.After(hi) || !gp.To.After(gp.From) {
			t.Fatalf("gap %d..%d has the times %v to %v, want an increasing span inside the stored range", gp.FirstSeq, gp.LastSeq, gp.From, gp.To)
		}
	}
}

// silentLogger is a logger that drops everything.
func silentLogger() *slog.Logger { return slog.New(slog.NewTextHandler(io.Discard, nil)) }

// expect reports an error unless ok. The arguments are evaluated whether or not ok holds.
func expect(t testing.TB, ok bool, format string, args ...any) {
	t.Helper()

	if !ok {
		t.Errorf(format, args...)
	}
}

// need fails the test at once unless ok. The arguments are evaluated whether or not ok holds.
func need(t testing.TB, ok bool, format string, args ...any) {
	t.Helper()

	if !ok {
		t.Fatalf(format, args...)
	}
}

// needNoErr fails the test at once when err is not nil.
func needNoErr(t testing.TB, what string, err error) {
	t.Helper()

	if err != nil {
		t.Fatalf("%s = %v, want nil", what, err)
	}
}

// expectNumbers reports an error unless got and want are the same numbers.
func expectNumbers(t testing.TB, what string, got, want []uint64) {
	t.Helper()

	expect(t, slices.Equal(got, want), "%s = %v, want %v", what, headOf(got, 12), headOf(want, 12))
}

// headOf returns at most the first n numbers.
func headOf(nums []uint64, n int) []uint64 { return nums[:min(len(nums), n)] }
