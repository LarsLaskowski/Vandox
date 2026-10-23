package logship_test

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/binary"
	"fmt"
	"io/fs"
	"log/slog"
	"math"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"testing/fstest"
	"time"

	"github.com/klauspost/compress/zstd"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/journal/journaltest"
	"github.com/LarsLaskowski/Vandox/internal/logship"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	machineID = "0badc0de0badc0de0badc0de0badc0de"
	bootID    = "c0ffee00c0ffee00c0ffee00c0ffee0f"
	// runInterval is the time between the starts of two runs of the collector.
	runInterval = 2 * time.Second
	// utcPlus2 is the offset of the zone of the fixture host.
	utcPlus2 = 2 * 3600
)

// startTime is the time of reading when a world starts; journal entries and file lines lie before it.
var startTime = time.Date(2026, time.October, 23, 7, 20, 0, 0, time.UTC)

// journalBase is the time of the entry with sequence number 0: entry n lies n seconds after it.
var journalBase = time.Date(2026, time.October, 23, 6, 0, 0, 0, time.UTC)

// entryTime returns the real time of the journal entry with sequence number seq.
func entryTime(seq uint64) time.Time {
	return journalBase.Add(time.Duration(seq) * time.Second) // #nosec G115 -- the fixture sequence numbers are small
}

func decimal(v uint64) []byte { return []byte(fmt.Sprintf("%d", v)) }

// jentry returns a journal entry of the info priority, host web-1 and program app with the sequence number seq, whose real time is
// entryTime(seq); kv are pairs of a field name and a value that replace a field, an empty value removes it.
func jentry(seq uint64, message string, kv ...string) map[string][]byte {
	e := map[string][]byte{
		"__SEQNUM":              decimal(seq),
		"__REALTIME_TIMESTAMP":  decimal(uint64(entryTime(seq).UnixMicro())), // #nosec G115 -- the fixture times lie after 1970
		"__MONOTONIC_TIMESTAMP": decimal(seq * 1000),
		"_BOOT_ID":              []byte(bootID),
		"MESSAGE":               []byte(message),
		"PRIORITY":              []byte("6"),
		"_HOSTNAME":             []byte("web-1"),
		"SYSLOG_IDENTIFIER":     []byte("app"),
		"_PID":                  []byte("100"),
	}
	for i := 0; i+1 < len(kv); i += 2 {
		if kv[i+1] == "" {
			delete(e, kv[i])
		} else {
			e[kv[i]] = []byte(kv[i+1])
		}
	}
	return e
}

// slimEntry returns an entry with the fewest fields, so that a journal of 100,000 of them is written quickly.
func slimEntry(seq uint64) map[string][]byte {
	return map[string][]byte{
		"__SEQNUM":              decimal(seq),
		"__REALTIME_TIMESTAMP":  decimal(uint64(entryTime(seq).UnixMicro())), // #nosec G115 -- the fixture times lie after 1970
		"__MONOTONIC_TIMESTAMP": decimal(seq * 1000),
		"MESSAGE":               []byte("line " + fmt.Sprint(seq)),
		"PRIORITY":              []byte("6"),
	}
}

// jentries returns the entries first, first+1, ..., last with the messages "line <seq>".
func jentries(first, last uint64) []map[string][]byte {
	var out []map[string][]byte
	for seq := first; seq <= last; seq++ {
		out = append(out, jentry(seq, fmt.Sprintf("line %d", seq)))
	}
	return out
}

// seqID returns the sequence number ID whose last byte is n.
func seqID(n byte) [16]byte {
	var id [16]byte
	id[15] = n
	return id
}

// uuid returns the text form of an ID as the state file keeps it.
func uuid(id [16]byte) string {
	h := fmt.Sprintf("%x", id[:])
	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:32]
}

func machineBytes() [16]byte {
	var id [16]byte
	for i := range id {
		_, _ = fmt.Sscanf(machineID[2*i:2*i+2], "%02x", &id[i])
	}
	return id
}

// tzFixed returns the time zone file of a zone with one offset (seconds east of UTC) and no transition.
func tzFixed(offset int32) []byte {
	b := append([]byte("TZif"), 0)
	b = append(b, make([]byte, 15)...)
	for _, n := range []uint32{0, 0, 0, 0, 1, 4} {
		b = binary.BigEndian.AppendUint32(b, n)
	}
	b = binary.BigEndian.AppendUint32(b, uint32(offset)) // #nosec G115 -- the bit pattern of a small signed number
	b = append(b, 0, 0)
	return append(b, 'T', 'S', 'T', 0)
}

// journalSpec is one journal file of the fixture host.
type journalSpec struct {
	name     string
	seqnumID [16]byte
	online   bool
	entries  []map[string][]byte
	compress bool // payloads of 512 bytes and more are real zstd frames of the library, else frames of one raw block
}

// fakeClock is the clock of the collector: every call of Now returns the current time and then adds step.
type fakeClock struct {
	mu   sync.Mutex
	now  time.Time
	step time.Duration
}

func (c *fakeClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	t := c.now
	c.now = c.now.Add(c.step)
	return t
}

func (c *fakeClock) peek() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *fakeClock) advance(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = c.now.Add(d)
}

// etcFS is the /etc of the fixture host, which a test changes while the collector reads it.
type etcFS struct {
	mu    sync.Mutex
	files map[string][]byte
}

func (e *etcFS) Open(name string) (fs.File, error) {
	e.mu.Lock()
	tree := fstest.MapFS{}
	for n, data := range e.files {
		tree[n] = &fstest.MapFile{Data: data, Mode: 0o644}
	}
	e.mu.Unlock()
	return tree.Open(name)
}

func (e *etcFS) set(name string, data []byte) {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.files[name] = data
}

func (e *etcFS) remove(name string) {
	e.mu.Lock()
	defer e.mu.Unlock()
	delete(e.files, name)
}

// logEntry is one log line of the collector.
type logEntry struct {
	level slog.Level
	msg   string
	attrs map[string]string
}

// logRecorder is a slog.Handler that keeps the entries.
type logRecorder struct {
	mu      sync.Mutex
	entries *[]logEntry
	attrs   []slog.Attr
}

func (h *logRecorder) Enabled(context.Context, slog.Level) bool { return true }

func (h *logRecorder) Handle(_ context.Context, r slog.Record) error {
	e := logEntry{level: r.Level, msg: r.Message, attrs: map[string]string{}}
	for _, a := range h.attrs {
		e.attrs[a.Key] = a.Value.Resolve().String()
	}
	r.Attrs(func(a slog.Attr) bool {
		e.attrs[a.Key] = a.Value.Resolve().String()
		return true
	})
	h.mu.Lock()
	defer h.mu.Unlock()
	*h.entries = append(*h.entries, e)
	return nil
}

func (h *logRecorder) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &logRecorder{entries: h.entries, attrs: append(append([]slog.Attr{}, h.attrs...), attrs...)}
}

func (h *logRecorder) WithGroup(string) slog.Handler { return h }

// world is the fixture host of the collector tests: a journal directory, a directory of configured log files, /etc, the agent's state
// file in a directory of its own and a collector on all of them. It plays the part of the scheduler: it passes Run to Collect and
// reports whether the records were kept.
type world struct {
	t         *testing.T
	root      string
	jroot     string
	logDir    string
	stateDir  string
	etc       *etcFS
	clock     *fakeClock
	store     *agentstate.Store
	cfg       logship.Config
	logs      *logRecorder
	entries   []logEntry
	l         *logship.Logs
	run       scheduler.Run
	lost      bool
	journals  []fs.FS // nil: the journal root of the world
	openFiles func(path string) (*os.File, error)
}

// newWorld returns a host with an empty journal directory, a zone of +02:00, no configured file and the journal shipped from
// the info priority on. The collector is made by start.
func newWorld(t *testing.T) *world {
	t.Helper()
	root, err := filepath.EvalSymlinks(t.TempDir())
	if err != nil {
		t.Fatalf("EvalSymlinks() error = %v, want nil", err)
	}
	w := &world{
		t:        t,
		root:     root,
		jroot:    filepath.Join(root, "journal"),
		logDir:   filepath.Join(root, "log"),
		stateDir: filepath.Join(root, "state"),
		etc:      &etcFS{files: map[string][]byte{"machine-id": []byte(machineID + "\n"), "localtime": tzFixed(utcPlus2)}},
		clock:    &fakeClock{now: startTime},
		cfg:      logship.Config{Journal: true, MinPriority: 6},
	}
	w.logs = &logRecorder{entries: &w.entries}
	for _, dir := range []string{filepath.Join(w.jroot, machineID), w.logDir, w.stateDir} {
		if err := os.MkdirAll(dir, 0o700); err != nil {
			t.Fatalf("MkdirAll(%s) error = %v, want nil", dir, err)
		}
	}
	w.reopen()
	return w
}

// reopen reads the state file again, as a new process does.
func (w *world) reopen() {
	w.t.Helper()
	store, status := agentstate.Open(w.stateDir, slog.New(slog.DiscardHandler))
	if status == agentstate.StatusUnavailable {
		w.t.Fatalf("agentstate.Open() status = unavailable, want the state file of the world to be usable")
	}
	w.store = store
}

func (w *world) sources() logship.Sources {
	journals := w.journals
	if journals == nil {
		journals = []fs.FS{os.DirFS(w.jroot)}
	}
	return logship.Sources{Etc: w.etc, Journal: journals, Now: w.clock.Now, Open: w.openFiles}
}

// start makes the collector from the configuration, the state of the store and the files of the world as they are now.
func (w *world) start() {
	w.t.Helper()
	l, err := logship.New(w.sources(), w.cfg, w.store, slog.New(w.logs))
	if err != nil {
		w.t.Fatalf("logship.New() error = %v, want nil", err)
	}
	w.l, w.lost = l, false
}

// stop saves the state as the agent does when it stops, and restart then reads it back and starts a new collector.
func (w *world) stop() {
	w.t.Helper()
	if err := w.store.Flush(w.clock.peek()); err != nil {
		w.t.Fatalf("Flush() error = %v, want nil", err)
	}
}

func (w *world) restart() {
	w.t.Helper()
	w.stop()
	w.reopen()
	w.start()
}

// crash starts a new collector on the state file as it was last saved, without saving the changes since.
func (w *world) crash() {
	w.t.Helper()
	w.reopen()
	w.start()
}

// writeJournal writes a journal file below the machine directory, replacing a file of that name atomically.
func (w *world) writeJournal(spec journalSpec) {
	w.t.Helper()
	w.writeJournalIn(w.jroot, spec)
}

// writeJournalIn writes a journal file below the machine directory of the journal root.
func (w *world) writeJournalIn(root string, spec journalSpec) {
	w.t.Helper()
	sum := sha256.Sum256([]byte(spec.name))
	var fileID [16]byte
	copy(fileID[:], sum[:])
	var buf bytes.Buffer
	opts := journaltest.Options{FileID: fileID, MachineID: machineBytes(), SeqnumID: spec.seqnumID, Online: spec.online}
	if spec.compress {
		enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderCRC(false), zstd.WithSingleSegment(true))
		if err != nil {
			w.t.Fatalf("zstd.NewWriter() error = %v, want nil", err)
		}
		defer func() { _ = enc.Close() }()
		opts.Compress = func(payload []byte) ([]byte, error) { return enc.EncodeAll(payload, nil), nil }
	}
	if err := journaltest.Write(&buf, spec.entries, opts); err != nil {
		w.t.Fatalf("journaltest.Write(%s) error = %v, want nil", spec.name, err)
	}
	dir := filepath.Join(root, machineID)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		w.t.Fatalf("MkdirAll(%s) error = %v, want nil", dir, err)
	}
	tmp := filepath.Join(dir, spec.name+".tmp")
	if err := os.WriteFile(tmp, buf.Bytes(), 0o600); err != nil {
		w.t.Fatalf("writing %s: %v", spec.name, err)
	}
	if err := os.Rename(tmp, filepath.Join(dir, spec.name)); err != nil {
		w.t.Fatalf("renaming %s: %v", spec.name, err)
	}
}

// online writes the online system journal of the ID with the given entries.
func (w *world) online(id [16]byte, entries []map[string][]byte) {
	w.t.Helper()
	w.writeJournal(journalSpec{name: "system.journal", seqnumID: id, online: true, entries: entries})
}

// archived writes an archived system journal file named by its ID and first sequence number.
func (w *world) archived(id [16]byte, entries []map[string][]byte) string {
	w.t.Helper()
	name := fmt.Sprintf("system@%x-%016x-0005f8d1b1a2c3d4.journal", id[:], mustSeq(entries[0]))
	w.writeJournal(journalSpec{name: name, seqnumID: id, entries: entries})
	return name
}

func mustSeq(e map[string][]byte) uint64 {
	var v uint64
	_, _ = fmt.Sscanf(string(e["__SEQNUM"]), "%d", &v)
	return v
}

// position sets the journal position of the ID in the state, as an earlier run of the collector left it, at the time of the entry.
func (w *world) position(id [16]byte, seq uint64) {
	w.t.Helper()
	w.positionAt(id, seq, entryTime(seq))
}

// positionAt sets the journal position of the ID in the state to the sequence number seq read at the time rt.
func (w *world) positionAt(id [16]byte, seq uint64, rt time.Time) {
	w.t.Helper()
	w.store.Update(false, func(s *agentstate.State) {
		if s.Logs == nil {
			s.Logs = &agentstate.LogPositions{}
		}
		p := agentstate.JournalPosition{SeqnumID: uuid(id), Seqnum: seq, Realtime: rt}
		for i := range s.Logs.Journal {
			if s.Logs.Journal[i].SeqnumID == p.SeqnumID {
				s.Logs.Journal[i] = p
				return
			}
		}
		s.Logs.Journal = append(s.Logs.Journal, p)
	})
}

// savedJournal returns the journal position of the ID in the state, or nil.
func (w *world) savedJournal(id [16]byte) *agentstate.JournalPosition {
	logs := w.store.State().Logs
	if logs == nil {
		return nil
	}
	for i := range logs.Journal {
		if logs.Journal[i].SeqnumID == uuid(id) {
			return &logs.Journal[i]
		}
	}
	return nil
}

// collect runs the collector once and checks that every record passes the check the scheduler applies.
func (w *world) collect() []model.Record {
	w.t.Helper()
	run := scheduler.Run{Start: w.clock.peek().UTC(), PreviousLost: w.lost}
	recs, err := w.l.Collect(context.Background(), run)
	if err != nil {
		w.t.Fatalf("Collect() error = %v, want nil", err)
	}
	w.run = run
	for i := range recs {
		c := recs[i]
		c.Origin, c.Seq = model.OriginAgent, math.MaxInt64
		if c.CapturedAt.IsZero() {
			c.CapturedAt = run.Start
		}
		if _, err := wire.CheckRecord(&c); err != nil {
			w.t.Errorf("record %d of the run fails the check of the scheduler: %v", i, err)
		}
	}
	return recs
}

// keep tells the collector that the records of the last run were kept.
func (w *world) keep() {
	w.t.Helper()
	w.l.Kept(w.run, true)
	w.lost = false
	w.clock.advance(runInterval)
}

// refuse tells the collector that the sink refused the records of the last run.
func (w *world) refuse() {
	w.t.Helper()
	w.l.Kept(w.run, false)
	w.lost = true
	w.clock.advance(runInterval)
}

// timeout lets the last run time out: the scheduler calls nothing and tells the next run that the previous result was lost.
func (w *world) timeout() {
	w.t.Helper()
	w.lost = true
	w.clock.advance(runInterval)
}

// step runs the collector once and keeps the records.
func (w *world) step() []model.Record {
	w.t.Helper()
	recs := w.collect()
	w.keep()
	return recs
}

// drain runs the collector until a run returns nothing (at most limit runs) and returns all records.
func (w *world) drain(limit int) []model.Record {
	w.t.Helper()
	var all []model.Record
	for range limit {
		recs := w.step()
		if len(recs) == 0 {
			return all
		}
		all = append(all, recs...)
	}
	w.t.Fatalf("the collector still returned records after %d runs", limit)
	return all
}

// logPath returns the path of a configured file.
func (w *world) logPath(name string) string {
	return filepath.Join(w.logDir, name)
}

// useFiles configures the syslog files of the given names.
func (w *world) useFiles(names ...string) {
	w.cfg.Files = nil
	for _, n := range names {
		w.cfg.Files = append(w.cfg.Files, logship.FileSpec{Path: w.logPath(n), Format: "syslog"})
	}
}

// writeLog creates or replaces a configured file.
func (w *world) writeLog(name, text string) {
	w.t.Helper()
	if err := os.WriteFile(w.logPath(name), []byte(text), 0o640); err != nil {
		w.t.Fatalf("writing %s: %v", name, err)
	}
}

// appendLog appends text to a configured file.
func (w *world) appendLog(name, text string) {
	w.t.Helper()
	f, err := os.OpenFile(w.logPath(name), os.O_WRONLY|os.O_APPEND|os.O_CREATE, 0o640)
	if err != nil {
		w.t.Fatalf("opening %s: %v", name, err)
	}
	if _, err := f.WriteString(text); err != nil {
		w.t.Fatalf("appending to %s: %v", name, err)
	}
	if err := f.Close(); err != nil {
		w.t.Fatalf("closing %s: %v", name, err)
	}
}

// rename renames a file of the log directory.
func (w *world) rename(from, to string) {
	w.t.Helper()
	if err := os.Rename(w.logPath(from), w.logPath(to)); err != nil {
		w.t.Fatalf("renaming %s to %s: %v", from, to, err)
	}
}

// fileLineTime is the real time of the file line with number n: the lines of the files lie in the zone of +02:00 on 23 October.
func fileLineTime(n int) time.Time {
	return time.Date(2026, time.October, 23, 6, 0, 0, 0, time.UTC).Add(time.Duration(n) * time.Second)
}

// fileLine returns the syslog line number n (a header with the local time, host web-1, program app and process ID 7).
func fileLine(n int, message string) string {
	local := fileLineTime(n).In(time.FixedZone("", utcPlus2))
	return local.Format("Jan _2 15:04:05") + " web-1 app[7]: " + message + "\n"
}

// fileLines returns the lines first..last with the messages "file <n>".
func fileLines(first, last int) string {
	var b strings.Builder
	for n := first; n <= last; n++ {
		b.WriteString(fileLine(n, fmt.Sprintf("file %d", n)))
	}
	return b.String()
}

// logLines returns the log line payloads of the records.
func logLines(recs []model.Record) []*model.LogLine {
	var out []*model.LogLine
	for _, r := range recs {
		if l, ok := r.Data.(*model.LogLine); ok {
			out = append(out, l)
		}
	}
	return out
}

// messages returns the messages of the log lines among the records.
func messages(recs []model.Record) []string {
	var out []string
	for _, l := range logLines(recs) {
		out = append(out, l.Message)
	}
	return out
}

// gapsOf returns the gaps among the records.
func gapsOf(recs []model.Record) []*model.Gap {
	var out []*model.Gap
	for _, r := range recs {
		if g, ok := r.Data.(*model.Gap); ok {
			out = append(out, g)
		}
	}
	return out
}

// numbered returns the messages "<prefix> <n>" for n = first..last.
func numbered(prefix string, first, last int) []string {
	var out []string
	for n := first; n <= last; n++ {
		out = append(out, fmt.Sprintf("%s %d", prefix, n))
	}
	return out
}

// logsOf returns the entries of the collector's log with the message msg.
func (w *world) logsOf(msg string) []logEntry {
	w.logs.mu.Lock()
	defer w.logs.mu.Unlock()
	var out []logEntry
	for _, e := range w.entries {
		if e.msg == msg {
			out = append(out, e)
		}
	}
	return out
}

// requireGap checks that g is a gap of the collector logs over [from, to].
func requireGap(t *testing.T, g *model.Gap, from, to time.Time) {
	t.Helper()
	if g.Collector != logship.Name || g.Cause != model.GapUnknown {
		t.Errorf("gap = collector %q cause %q, want collector %q cause %q", g.Collector, g.Cause, logship.Name, model.GapUnknown)
	}
	if !g.From.Equal(from) || !g.To.Equal(to) {
		t.Errorf("gap = [%v, %v], want [%v, %v]", g.From, g.To, from, to)
	}
}

// head returns the first few elements of s for a failure message.
func head(s []string) []string {
	if len(s) > 6 {
		return append(append([]string{}, s[:6]...), fmt.Sprintf("... (%d in all)", len(s)))
	}
	return s
}
