package connstats

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log/slog"
	"net/netip"
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

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Table state codes as the kernel writes them.
const (
	stateEstablished = "01"
	stateTimeWait    = "06"
	stateUDPOpen     = "07"
	stateListen      = "0A"
)

// Command names the owned world's processes derive from their command lines.
const (
	cmdSSHD   = "sshd"
	cmdNginx  = "nginx"
	cmdNamed  = "named"
	cmdMaster = "master"
)

// Endpoints of the captured server tables (the anonymized capture uses the documentation ranges).
const (
	sshdListener  = "0.0.0.0:22"
	nginxListener = "192.0.2.10:443"
)

var (
	tableNames = []string{"tcp", "tcp6", "udp", "udp6"}
	runBase    = time.Date(2026, time.October, 18, 12, 0, 0, 0, time.UTC)
)

// runAt returns the i-th run, one minute after the previous one.
func runAt(i int, pressure bool) scheduler.Run {
	return scheduler.Run{Start: runBase.Add(time.Duration(i) * time.Minute), Pressure: pressure}
}

// quietLogger discards everything.
func quietLogger() *slog.Logger {
	return slog.New(slog.DiscardHandler)
}

// etcDir holds the passwd file the tests resolve user names from: it names UID 0 (root), 33 (www-data) and 65534 (nobody), none of
// the other UIDs of the captured tables.
const etcDir = "../../testdata/etc"

// etcOf returns an /etc that holds the given passwd file.
func etcOf(passwd string) fs.FS {
	return fstest.MapFS{"passwd": {Data: []byte(passwd)}}
}

// fixtureEtc returns the /etc of the captured server (testdata/etc).
func fixtureEtc() fs.FS {
	return os.DirFS(etcDir)
}

// newCollector returns a collector over proc that knows the socket mount of the captured samples and reads the captured passwd.
func newCollector(proc fs.FS) *Connections {
	return newCollectorWith(proc, fixtureEtc())
}

// newCollectorWith returns the collector of newCollector over the given /etc.
func newCollectorWith(proc, etc fs.FS) *Connections {
	return New(Sources{Proc: proc, Etc: etc, SocketMount: SocketMount{ID: socketMount}}, quietLogger())
}

// snapshotOf runs the collector once and returns its single record and snapshot.
func snapshotOf(t *testing.T, c *Connections, run scheduler.Run) (model.Record, *model.ConnectionSnapshot) {
	t.Helper()

	records, err := c.Collect(context.Background(), run)
	if err != nil {
		t.Fatalf("Collect(run %v) error = %v, want nil", run.Start, err)
	}
	if len(records) != 1 {
		t.Fatalf("Collect(run %v) = %d records, want 1", run.Start, len(records))
	}
	snap, ok := records[0].Data.(*model.ConnectionSnapshot)
	if !ok {
		t.Fatalf("Collect(run %v) record data = %T, want *model.ConnectionSnapshot", run.Start, records[0].Data)
	}
	if records[0].Source != Source {
		t.Errorf("record source = %q, want %q", records[0].Source, Source)
	}
	if !records[0].CapturedAt.Equal(run.Start) {
		t.Errorf("record captured at %v, want the run's start %v", records[0].CapturedAt, run.Start)
	}
	return records[0], snap
}

// wantList compares two lists of comparable values.
func wantList[T comparable](t *testing.T, what string, got, want []T) {
	t.Helper()

	if !slices.Equal(got, want) {
		t.Errorf("%s = %v, want %v", what, got, want)
	}
}

// world is an in-memory /proc: the socket tables as lines, the processes and their descriptors.
type world struct {
	*procTree
	headers map[string]string
	lines   map[string][]string
	tmpl    map[string]string // a captured line of each table
	dirty   bool
}

// tableHeader returns the first line of a captured table.
func tableHeader(t *testing.T, dir, name string) string {
	t.Helper()

	data, err := os.ReadFile(filepath.Join(dir, name))
	if err != nil {
		t.Fatalf("reading the fixture %s/%s: %v", dir, name, err)
	}
	header, _, _ := strings.Cut(string(data), "\n")
	return header
}

// newWorld returns a /proc with the four captured server tables and no process.
func newWorld(t *testing.T) *world {
	t.Helper()

	w := &world{procTree: newProcTree(t), headers: map[string]string{}, lines: map[string][]string{}, tmpl: map[string]string{}}
	for _, name := range tableNames {
		w.headers[name] = tableHeader(t, serverNetDir, name)
		w.lines[name] = tableLines(t, serverNetDir, name)
		w.tmpl[name] = w.lines[name][0]
	}
	w.flush()
	return w
}

// Open writes the tables first when a test changed them since the last Open.
func (w *world) Open(name string) (fs.File, error) {
	if w.dirty {
		w.flush()
	}
	return w.files.Open(name)
}

// dropTable removes a table, as a kernel without IPv6 does.
func (w *world) dropTable(name string) {
	delete(w.lines, name)
	w.dirty = true
}

// clear leaves every table with its header only.
func (w *world) clear() {
	for _, name := range tableNames {
		w.lines[name] = []string{}
	}
	w.dirty = true
}

// flush writes the tables into the file tree; a table without an entry in lines does not exist.
func (w *world) flush() {
	w.dirty = false
	for _, name := range tableNames {
		lines, ok := w.lines[name]
		if !ok {
			delete(w.files, "net/"+name)
			continue
		}
		var b strings.Builder
		b.WriteString(w.headers[name])
		b.WriteByte('\n')
		for _, line := range lines {
			b.WriteString(line)
			b.WriteByte('\n')
		}
		w.files["net/"+name] = &fstest.MapFile{Data: []byte(b.String())}
	}
}

// process adds the stat and cmdline files of a process; cmdline holds the raw bytes (arguments separated by NUL).
func (w *world) process(pid int32, comm, cmdline string) {
	n := strconv.Itoa(int(pid))
	fields := make([]string, 50)
	for i := range fields {
		fields[i] = "0"
	}
	fields[0], fields[1], fields[19] = "S", "1", "1000"
	w.files[n+"/stat"] = &fstest.MapFile{Data: []byte(n + " (" + comm + ") " + strings.Join(fields, " ") + "\n")}
	w.files[n+"/cmdline"] = &fstest.MapFile{Data: []byte(cmdline)}
}

// holdAll gives pid one descriptor per inode, numbered from 3.
func (w *world) holdAll(pid int32, inodes ...uint64) {
	for i, ino := range inodes {
		w.add(pid, 3+i, socketMount, ino)
	}
}

// endpointHex writes an endpoint as the kernel's tables do: address words in host byte order, then the port.
func endpointHex(ap netip.AddrPort) string {
	a := ap.Addr()
	var b strings.Builder
	if a.Is4() {
		v := a.As4()
		fmt.Fprintf(&b, "%08X", binary.NativeEndian.Uint32(v[:]))
	} else {
		v := a.As16()
		for i := 0; i < len(v); i += 4 {
			fmt.Fprintf(&b, "%08X", binary.NativeEndian.Uint32(v[i:i+4]))
		}
	}
	fmt.Fprintf(&b, ":%04X", ap.Port())
	return b.String()
}

// socketLine builds a table line from a captured line of the same table, replacing local, remote, state, uid and inode.
func socketLine(t *testing.T, tmpl string, local, remote netip.AddrPort, state string, uid uint32, inode uint64) string {
	t.Helper()

	line := setField(t, tmpl, 1, endpointHex(local))
	line = setField(t, line, 2, endpointHex(remote))
	line = setField(t, line, 3, state)
	line = setField(t, line, 7, strconv.FormatUint(uint64(uid), 10))
	return setField(t, line, 9, strconv.FormatUint(inode, 10))
}

// zeroRemote is the remote endpoint of a socket that has none.
func zeroRemote(table string) netip.AddrPort {
	if strings.HasSuffix(table, "6") {
		return netip.AddrPortFrom(netip.IPv6Unspecified(), 0)
	}
	return netip.AddrPortFrom(netip.IPv4Unspecified(), 0)
}

// addSocket appends a socket of the UID 0 to a table; the line is a captured one of that table with the values replaced.
func (w *world) addSocket(table, local, remote, state string, inode uint64) {
	w.t.Helper()

	w.addSocketUID(table, local, remote, state, 0, inode)
}

// addSocketUID appends a socket of the given UID to a table; the line is a captured one of that table with the values replaced.
func (w *world) addSocketUID(table, local, remote, state string, uid uint32, inode uint64) {
	w.t.Helper()

	r := zeroRemote(table)
	if remote != "" {
		r = addrPort(remote)
	}
	w.lines[table] = append(w.lines[table], socketLine(w.t, w.tmpl[table], addrPort(local), r, state, uid, inode))
	w.dirty = true
}

// addListener appends a listener of the UID 0: TCP in state LISTEN, UDP without remote endpoint.
func (w *world) addListener(table, local string, inode uint64) {
	w.t.Helper()

	w.addListenerUID(table, local, 0, inode)
}

// addListenerUID appends a listener of the given UID: TCP in state LISTEN, UDP without remote endpoint.
func (w *world) addListenerUID(table, local string, uid uint32, inode uint64) {
	w.t.Helper()

	state := stateListen
	if strings.HasPrefix(table, "udp") {
		state = stateUDPOpen
	}
	w.addSocketUID(table, local, "", state, uid, inode)
}

// addListeners appends one TCP listener of the UID 0 per port on addr, from first on, with the inodes from inode on.
func (w *world) addListeners(addr string, first, count uint16, inode uint64) {
	w.t.Helper()

	for i := range count {
		local := netip.AddrPortFrom(netip.MustParseAddr(addr), first+i)
		w.lines["tcp"] = append(w.lines["tcp"], socketLine(w.t, w.tmpl["tcp"], local, zeroRemote("tcp"), stateListen, 0, inode+uint64(i)))
	}
	w.dirty = true
}

// replaceLine swaps the i-th data line of a table (physical line i+2) and writes the tables.
func (w *world) replaceLine(table string, i int, line string) {
	w.lines[table][i] = line
	w.dirty = true
}

// holdRange gives pid count descriptors from firstFd on, holding the inodes from first on.
func (w *world) holdRange(pid int32, firstFd int, first uint64, count int) {
	fd, ino := firstFd, first
	for range count {
		w.add(pid, fd, socketMount, ino)
		fd++
		ino++
	}
}

// ownedWorld returns the captured server tables with processes holding some of their sockets.
//
//	100 sshd:    tcp 0.0.0.0:22 and its established connection
//	200, 201:    nginx, both hold tcp 192.0.2.10:443 and :80
//	300:         named, holds udp 192.0.2.10:53 (one of two sockets) and the connected udp socket 192.0.2.10:11651
//	301:         named, holds the other udp socket on 192.0.2.10:53
//	400:         master, holds tcp 0.0.0.0:25, :587 and :465
//	600, 601:    named, hold the two tcp sockets on 127.0.0.1:53; 601 holds the first one of the table, 600 the second
func ownedWorld(t *testing.T) *world {
	t.Helper()

	w := newWorld(t)
	w.process(100, cmdSSHD, "/usr/sbin/sshd\x00-D\x00")
	w.holdAll(100, 16363918, 17280839)
	for _, pid := range []int32{200, 201} {
		w.process(pid, cmdNginx, "nginx: worker process\x00")
		w.holdAll(pid, 16364675, 16364676)
	}
	w.process(300, cmdNamed, "/usr/sbin/named\x00-f\x00")
	w.holdAll(300, 16364553, 16955396)
	w.process(301, cmdNamed, "/usr/sbin/named\x00-f\x00")
	w.holdAll(301, 16364552)
	w.process(400, cmdMaster, "/usr/lib/postfix/sbin/master\x00-w\x00")
	w.holdAll(400, 16366396, 16366506, 16366497)
	w.process(600, cmdNamed, "/usr/sbin/named\x00-f\x00")
	w.holdAll(600, 16364547)
	w.process(601, cmdNamed, "/usr/sbin/named\x00-f\x00")
	w.holdAll(601, 16364551)
	return w
}

// logEntry is one line the collector logged.
type logEntry struct {
	level slog.Level
	msg   string
	attrs map[string]string
}

// logStore keeps the lines of a recording logger.
type logStore struct {
	mu      sync.Mutex
	entries []logEntry
}

func (s *logStore) add(e logEntry) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.entries = append(s.entries, e)
}

// all returns a copy of the lines logged so far.
func (s *logStore) all() []logEntry {
	s.mu.Lock()
	defer s.mu.Unlock()

	return slices.Clone(s.entries)
}

// find returns the lines with the message msg.
func (s *logStore) find(msg string) []logEntry {
	var out []logEntry
	for _, e := range s.all() {
		if e.msg == msg {
			out = append(out, e)
		}
	}
	return out
}

// text returns every message, attribute key and attribute value of every line, for leak checks.
func (s *logStore) text() string {
	var b strings.Builder
	for _, e := range s.all() {
		b.WriteString(e.msg)
		b.WriteByte('\n')
		for k, v := range e.attrs {
			b.WriteString(k + "=" + v + "\n")
		}
	}
	return b.String()
}

// recordingHandler is a slog.Handler that keeps what it is given, with the attributes of WithAttrs.
type recordingHandler struct {
	store  *logStore
	preset []slog.Attr
}

func (h *recordingHandler) Enabled(context.Context, slog.Level) bool { return true }

func (h *recordingHandler) Handle(_ context.Context, r slog.Record) error {
	e := logEntry{level: r.Level, msg: r.Message, attrs: map[string]string{}}
	for _, a := range h.preset {
		e.attrs[a.Key] = a.Value.String()
	}
	r.Attrs(func(a slog.Attr) bool {
		e.attrs[a.Key] = a.Value.String()
		return true
	})
	h.store.add(e)
	return nil
}

func (h *recordingHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &recordingHandler{store: h.store, preset: append(slices.Clone(h.preset), attrs...)}
}

func (h *recordingHandler) WithGroup(string) slog.Handler { return h }

// newRecordingCollector returns a collector over proc and the captured passwd, and the store of its log lines.
func newRecordingCollector(proc fs.FS, mount SocketMount) (*Connections, *logStore) {
	return newRecordingCollectorWith(proc, fixtureEtc(), mount)
}

// newRecordingCollectorWith returns the collector of newRecordingCollector over the given /etc.
func newRecordingCollectorWith(proc, etc fs.FS, mount SocketMount) (*Connections, *logStore) {
	store := &logStore{}
	return New(Sources{Proc: proc, Etc: etc, SocketMount: mount}, slog.New(&recordingHandler{store: store})), store
}

// wantLogged checks that exactly n lines with message msg and level were logged, each from the collector, and returns them.
func wantLogged(t *testing.T, store *logStore, level slog.Level, msg string, n int) []logEntry {
	t.Helper()

	found := store.find(msg)
	if len(found) != n {
		t.Errorf("log holds %d lines %q, want %d; log:\n%s", len(found), msg, n, store.text())
	}
	for _, e := range found {
		if e.level != level {
			t.Errorf("line %q has level %v, want %v", msg, e.level, level)
		}
		if e.attrs["collector"] != Name {
			t.Errorf("line %q has collector = %q, want %q", msg, e.attrs["collector"], Name)
		}
	}
	return found
}

// blockingFS blocks the Open of one name until it is released.
type blockingFS struct {
	fs.FS
	name    string
	reached chan struct{}
	release chan struct{}
	once    sync.Once
	armed   atomic.Bool
	entered atomic.Int32
}

// newBlockingFS returns a file system whose Open of name blocks (while armed); the test's end releases it.
func newBlockingFS(t *testing.T, base fs.FS, name string) *blockingFS {
	t.Helper()

	b := &blockingFS{FS: base, name: name, reached: make(chan struct{}, 1), release: make(chan struct{})}
	b.armed.Store(true)
	t.Cleanup(b.open)
	return b
}

func (b *blockingFS) Open(name string) (fs.File, error) {
	if name == b.name && b.armed.Load() {
		b.entered.Add(1)
		select {
		case b.reached <- struct{}{}:
		default:
		}
		<-b.release
	}
	return b.FS.Open(name)
}

// open releases every blocked and later Open.
func (b *blockingFS) open() { b.once.Do(func() { close(b.release) }) }

// recordingFS remembers the names it was asked to open.
type recordingFS struct {
	fs.FS
	mu    sync.Mutex
	names []string
}

func (r *recordingFS) Open(name string) (fs.File, error) {
	r.mu.Lock()
	r.names = append(r.names, name)
	r.mu.Unlock()
	return r.FS.Open(name)
}

// opened returns the names opened so far.
func (r *recordingFS) opened() []string {
	r.mu.Lock()
	defer r.mu.Unlock()

	return slices.Clone(r.names)
}

// okCollector always succeeds with one record.
type okCollector struct{}

func (*okCollector) Name() string { return "ok" }

func (*okCollector) Collect(context.Context, scheduler.Run) ([]model.Record, error) {
	return []model.Record{{Meta: model.Meta{Source: "ok"}, Data: &model.MetricPoint{Name: "sample", Value: 1}}}, nil
}

// gapSink is a scheduler.Putter that keeps the gaps it received per collector.
type gapSink struct {
	mu   sync.Mutex
	gaps []model.Gap
	ok   int
}

func (s *gapSink) Put(_ context.Context, records []model.Record) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	for _, r := range records {
		switch d := r.Data.(type) {
		case *model.Gap:
			s.gaps = append(s.gaps, *d)
		case *model.MetricPoint:
			s.ok++
		}
	}
	return nil
}

func (s *gapSink) gapsOf(collector string) []model.Gap {
	s.mu.Lock()
	defer s.mu.Unlock()

	var out []model.Gap
	for _, g := range s.gaps {
		if g.Collector == collector {
			out = append(out, g)
		}
	}
	return out
}

func (s *gapSink) samples() int {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.ok
}

// Generated tables and descriptors for the tests of the caps. They produce their bytes on demand, so a table of 400,000 lines
// or a process of 200,000 descriptors is never in memory, and a Read or an Open allocates nothing beyond what a real file does.

// sockSpec is one generated socket (IPv4 tables only); state is the kernel's code, uid at most 99,999 (the width of the captured
// field) and inode at most 99,999,999.
type sockSpec struct {
	local, remote netip.AddrPort
	state         string
	uid           uint32
	inode         uint64
}

// lineTemplate is a captured table line whose fixed-width fields are overwritten in place.
type lineTemplate struct {
	line                          []byte
	local, remote, state, inodeAt int
	uidEnd                        int // the offset after the uid field, whose width is uidDigits
}

// newLineTemplate cuts the offsets of the fields out of a captured line of an IPv4 table.
func newLineTemplate(t *testing.T, name string) *lineTemplate {
	t.Helper()

	raw := tableLines(t, serverNetDir, name)[0]
	tp := &lineTemplate{line: []byte(strings.TrimRight(raw, " ") + "\n")}
	var offsets [10]int
	i := 0
	for n := range offsets {
		for tp.line[i] == ' ' {
			i++
		}
		offsets[n] = i
		for tp.line[i] != ' ' && tp.line[i] != '\n' {
			i++
		}
	}
	tp.local, tp.remote, tp.state, tp.inodeAt = offsets[1], offsets[2], offsets[3], offsets[9]
	tp.uidEnd = offsets[7] + 1
	if tp.uidEnd < uidDigits || string(tp.line[tp.uidEnd-uidDigits:tp.uidEnd]) != "    0" {
		t.Fatalf("template line %q: the uid field is not %d characters wide and 0", raw, uidDigits)
	}
	if got := string(tp.line[tp.inodeAt : tp.inodeAt+inodeDigits]); len(strings.Fields(got)) != 1 || tp.line[tp.inodeAt+inodeDigits] != ' ' {
		t.Fatalf("template line %q: the inode field is not %d digits wide", raw, inodeDigits)
	}
	return tp
}

const (
	inodeDigits = 8
	uidDigits   = 5
)

// hexInto writes v as digits upper-case hex digits at dst.
func hexInto(dst []byte, v uint64, digits int) {
	const hexDigits = "0123456789ABCDEF"
	for i := digits - 1; i >= 0; i-- {
		dst[i] = hexDigits[v&0xF]
		v >>= 4
	}
}

// endpointInto writes an IPv4 endpoint at dst.
func endpointInto(dst []byte, ap netip.AddrPort) {
	a := ap.Addr().As4()
	hexInto(dst, uint64(binary.NativeEndian.Uint32(a[:])), 8)
	hexInto(dst[9:], uint64(ap.Port()), 4)
}

// fill writes the line of s into buf and returns it.
func (tp *lineTemplate) fill(buf []byte, s *sockSpec) []byte {
	buf = append(buf[:0], tp.line...)
	endpointInto(buf[tp.local:], s.local)
	endpointInto(buf[tp.remote:], s.remote)
	copy(buf[tp.state:], s.state)
	uid := strconv.AppendUint(make([]byte, 0, uidDigits), uint64(s.uid), 10)
	for i := tp.uidEnd - uidDigits; i < tp.uidEnd; i++ {
		buf[i] = ' '
	}
	copy(buf[tp.uidEnd-len(uid):], uid)
	var tmp [20]byte
	digits := strconv.AppendUint(tmp[:0], s.inode, 10)
	for i := range inodeDigits {
		buf[tp.inodeAt+i] = '0'
	}
	copy(buf[tp.inodeAt+inodeDigits-len(digits):], digits)
	return buf
}

// tableGen generates a table of n lines.
type tableGen struct {
	header []byte
	tp     *lineTemplate
	n      uint32
	spec   func(i uint32) sockSpec
}

// genTableFile is the open file of a generated table.
type genTableFile struct {
	g       *tableGen
	next    uint32
	started bool
	pending []byte
	buf     [256]byte
}

func (f *genTableFile) Read(p []byte) (int, error) {
	n := 0
	for n < len(p) {
		if len(f.pending) == 0 && !f.refill() {
			break
		}
		c := copy(p[n:], f.pending)
		f.pending = f.pending[c:]
		n += c
	}
	if n == 0 && len(p) > 0 {
		return 0, io.EOF
	}
	return n, nil
}

// refill prepares the next piece (the header, then one line per socket) and reports whether there was one.
func (f *genTableFile) refill() bool {
	switch {
	case !f.started:
		f.started = true
		f.pending = f.g.header
	case f.next < f.g.n:
		s := f.g.spec(f.next)
		f.pending = f.g.tp.fill(f.buf[:0], &s)
		f.next++
	default:
		return false
	}
	return true
}

func (f *genTableFile) Stat() (fs.FileInfo, error) { return nil, errors.ErrUnsupported }
func (f *genTableFile) Close() error               { return nil }

// fdGen generates the descriptors 3 to count+2 of one process; inode gives the inode of the i-th descriptor.
type fdGen struct {
	dir   string
	count uint32
	inode func(i uint32) uint64
}

// genEntry is a generated directory entry.
type genEntry struct{ name string }

func (e *genEntry) Name() string               { return e.name }
func (e *genEntry) IsDir() bool                { return false }
func (e *genEntry) Type() fs.FileMode          { return 0 }
func (e *genEntry) Info() (fs.FileInfo, error) { return nil, errors.ErrUnsupported }

// genDir is the open directory of generated descriptors.
type genDir struct {
	g    *fdGen
	next int
}

func (d *genDir) ReadDir(n int) ([]fs.DirEntry, error) {
	left := int(d.g.count) - d.next
	if n > 0 && left == 0 {
		return nil, io.EOF
	}
	if n <= 0 || n > left {
		n = left
	}
	entries := make([]fs.DirEntry, n)
	for i := range entries {
		entries[i] = &genEntry{name: strconv.Itoa(3 + d.next + i)}
	}
	d.next += n
	return entries, nil
}

func (d *genDir) Read([]byte) (int, error)   { return 0, errors.ErrUnsupported }
func (d *genDir) Stat() (fs.FileInfo, error) { return nil, errors.ErrUnsupported }
func (d *genDir) Close() error               { return nil }

// genFile is the open fdinfo file of a generated descriptor.
type genFile struct{ r *strings.Reader }

func (f *genFile) Read(p []byte) (int, error) { return f.r.Read(p) }
func (f *genFile) Stat() (fs.FileInfo, error) { return nil, errors.ErrUnsupported }
func (f *genFile) Close() error               { return nil }

// genFS answers the generated tables and descriptors itself and everything else from base. hook runs once, at the first Open
// of hookName.
type genFS struct {
	base     fs.FS
	tables   map[string]*tableGen
	fds      *fdGen
	hookName string
	hook     func()
	hooked   bool
}

func (g *genFS) Open(name string) (fs.File, error) {
	if g.hook != nil && name == g.hookName && !g.hooked {
		g.hooked = true
		g.hook()
	}
	if t, ok := g.tables[name]; ok {
		return &genTableFile{g: t}, nil
	}
	if g.fds != nil {
		if name == g.fds.dir {
			return &genDir{g: g.fds}, nil
		}
		if fd, ok := strings.CutPrefix(name, g.fds.dir+"/"); ok {
			return g.fdFile(fd, name)
		}
	}
	return g.base.Open(name)
}

// fdFile returns the fdinfo file of descriptor fd, a socket on the socket mount.
func (g *genFS) fdFile(fd, name string) (fs.File, error) {
	n, err := strconv.ParseUint(fd, 10, 32)
	if err != nil || n < 3 || n >= 3+uint64(g.fds.count) {
		return nil, &fs.PathError{Op: "open", Path: name, Err: fs.ErrNotExist}
	}
	content := "pos:\t0\nflags:\t02000002\nmnt_id:\t" + strconv.Itoa(int(socketMount)) + "\nino:\t" + strconv.FormatUint(g.fds.inode(uint32(n-3)), 10) + "\nscm_fds:\t0\n"
	return &genFile{r: strings.NewReader(content)}, nil
}
