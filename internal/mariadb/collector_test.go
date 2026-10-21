package mariadb

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"log/slog"
	"maps"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"testing"
	"testing/fstest"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// The selection of the spec (S1), written out here so that the tests do not read it from the code under test.
var (
	selectedStatus = []string{
		"Uptime", "Threads_connected", "Threads_running", "Max_used_connections", "Aborted_connects", "Aborted_clients", "Questions",
		"Slow_queries", "Created_tmp_disk_tables", "Innodb_buffer_pool_pages_total", "Innodb_buffer_pool_pages_free",
		"Innodb_buffer_pool_read_requests", "Innodb_buffer_pool_reads", "Memory_used",
	}
	selectedVariables = []string{
		"version", "max_connections", "innodb_buffer_pool_size", "innodb_log_buffer_size", "key_buffer_size", "aria_pagecache_buffer_size",
		"query_cache_size", "tmp_table_size", "max_heap_table_size", "sort_buffer_size", "read_buffer_size", "read_rnd_buffer_size",
		"join_buffer_size", "thread_stack", "binlog_cache_size", "log_bin", "bind_address", "skip_networking", "port", "extra_port",
	}
)

// Texts and names the spec fixes.
const (
	fixText          = "run deploy/agent/mariadb-user.sql as the database administrator"
	msgDown          = "mariadb down"
	msgAgain         = "mariadb answering again"
	msgNotAnswering  = "mariadb not answering"
	msgUserMissing   = "mariadb user missing or not allowed"
	msgLoginRefused  = "mariadb login refused"
	msgAboveRAM      = "mariadb memory maximum above RAM"
	msgExposed       = "mariadb listens on a non-loopback address"
	msgLoopbackOnly  = "mariadb listens on loopback only"
	msgNotAsExpected = "mariadb user not as installed"
	msgRestarted     = "mariadb restarted"

	metricMemoryMax     = "mariadb_memory_max_bytes"
	metricMemoryPercent = "mariadb_memory_max_ram_percent"
	metricQuestions     = "mariadb_questions_per_second"
	metricPoolUsed      = "mariadb_innodb_buffer_pool_used_percent"
	metricPoolHit       = "mariadb_innodb_buffer_pool_read_hit_percent"
	metricExposed       = "mariadb_exposed_listeners"
)

// logLine is a line the collector logged.
type logLine struct {
	level slog.Level
	msg   string
	attrs map[string]string
}

// logStore holds the lines a recordingHandler received.
type logStore struct {
	mu    sync.Mutex
	lines []logLine
}

// take returns the lines logged since the last call.
func (s *logStore) take() []logLine {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := s.lines
	s.lines = nil
	return out
}

// recordingHandler is a slog.Handler that stores entries (groups are flattened).
type recordingHandler struct {
	store *logStore
	attrs []slog.Attr
}

func (h *recordingHandler) Enabled(context.Context, slog.Level) bool { return true }

func (h *recordingHandler) Handle(_ context.Context, r slog.Record) error {
	line := logLine{level: r.Level, msg: r.Message, attrs: map[string]string{}}
	for _, a := range h.attrs {
		line.attrs[a.Key] = a.Value.Resolve().String()
	}
	r.Attrs(func(a slog.Attr) bool {
		line.attrs[a.Key] = a.Value.Resolve().String()
		return true
	})
	h.store.mu.Lock()
	h.store.lines = append(h.store.lines, line)
	h.store.mu.Unlock()
	return nil
}

func (h *recordingHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &recordingHandler{store: h.store, attrs: append(slices.Clone(h.attrs), attrs...)}
}

func (h *recordingHandler) WithGroup(string) slog.Handler { return h }

// wantLog is a line the collector must log; every line carries collector=mariadb besides its attrs.
type wantLog struct {
	level slog.Level
	msg   string
	attrs map[string]string
}

// describeLog renders a line for the comparison of two sets of lines.
func describeLog(level slog.Level, msg string, attrs map[string]string) string {
	return fmt.Sprintf("%s %q %v", level, msg, attrs)
}

// checkLogs fails unless got holds exactly the wanted lines, in any order and with exactly the wanted attributes.
func checkLogs(t *testing.T, what string, got []logLine, want ...wantLog) {
	t.Helper()
	var g, w []string
	for _, l := range got {
		g = append(g, describeLog(l.level, l.msg, l.attrs))
	}
	for _, l := range want {
		attrs := map[string]string{"collector": Name}
		for k, v := range l.attrs {
			attrs[k] = v
		}
		w = append(w, describeLog(l.level, l.msg, attrs))
	}
	slices.Sort(g)
	slices.Sort(w)
	if !slices.Equal(g, w) {
		t.Errorf("%s logged\n  %s\nwant\n  %s", what, strings.Join(g, "\n  "), strings.Join(w, "\n  "))
	}
}

// warn and info build wanted lines.
func warn(msg string, attrs map[string]string) wantLog { return wantLog{slog.LevelWarn, msg, attrs} }
func info(msg string) wantLog                          { return wantLog{slog.LevelInfo, msg, nil} }

// meminfoFile returns a /proc/meminfo with every required field and the given MemTotal.
func meminfoFile(totalKB int) *fstest.MapFile {
	var sb strings.Builder
	fmt.Fprintf(&sb, "MemTotal:       %d kB\n", totalKB)
	for _, name := range []string{"MemFree", "MemAvailable", "Buffers", "Cached", "SReclaimable", "SwapTotal", "SwapFree"} {
		fmt.Fprintf(&sb, "%-15s%d kB\n", name+":", 1000)
	}
	return &fstest.MapFile{Data: []byte(sb.String())}
}

// hexAddr writes an address as the kernel does in /proc/net/tcp*: every 32-bit word in host byte order, little endian.
func hexAddr(a netip.Addr) string {
	var sb strings.Builder
	if a.Is4() {
		b := a.As4()
		for i := 3; i >= 0; i-- {
			fmt.Fprintf(&sb, "%02X", b[i])
		}
		return sb.String()
	}
	b := a.As16()
	for w := 0; w < 4; w++ {
		for i := 3; i >= 0; i-- {
			fmt.Fprintf(&sb, "%02X", b[4*w+i])
		}
	}
	return sb.String()
}

const tcpHeader = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode"

// socketLine writes a line of /proc/net/tcp or tcp6 for a socket on ap in the given state ("0A" listens).
func socketLine(slot int, ap netip.AddrPort, state string) string {
	remote := strings.Repeat("0", 8)
	if !ap.Addr().Is4() {
		remote = strings.Repeat("0", 32)
	}
	return fmt.Sprintf("%4d: %s:%04X %s:0000 %s 00000000:00000000 00:00000000 00000000     0        0 %d 1 0000000000000000 100 0 0 10 0",
		slot, hexAddr(ap.Addr()), ap.Port(), remote, state, 5000+slot)
}

// tableFile writes lines as a table with the kernel's header.
func tableFile(lines ...string) *fstest.MapFile {
	text := tcpHeader + "\n"
	for _, l := range lines {
		text += l + "\n"
	}
	return &fstest.MapFile{Data: []byte(text)}
}

// listenerTables returns the net/tcp and net/tcp6 files with one listening socket per address: IPv4 addresses (also
// IPv4-mapped ones are IPv6 addresses) go to tcp6 when they are not plain IPv4.
func listenerTables(listeners ...string) fstest.MapFS {
	var v4, v6 []string
	for i, s := range listeners {
		ap := netip.MustParseAddrPort(s)
		if ap.Addr().Is4() {
			v4 = append(v4, socketLine(i, ap, "0A"))
		} else {
			v6 = append(v6, socketLine(i, ap, "0A"))
		}
	}
	return fstest.MapFS{"net/tcp": tableFile(v4...), "net/tcp6": tableFile(v6...)}
}

// newProc returns a /proc with the given MemTotal and the listeners (host:port) on net/tcp and net/tcp6.
func newProc(totalKB int, listeners ...string) fstest.MapFS {
	proc := listenerTables(listeners...)
	proc["meminfo"] = meminfoFile(totalKB)
	return proc
}

// defsOf returns the column definition packets of a captured result set.
func defsOf(reply []capturePacket) [][]byte {
	count := int(reply[0].payload[0])
	defs := make([][]byte, count)
	for i := range defs {
		defs[i] = reply[1+i].payload
	}
	return defs
}

// rowMap maps the first cell of every row to the second.
func rowMap(rows [][]textCell) map[string]string {
	out := make(map[string]string, len(rows))
	for _, r := range rows {
		out[r[0].value] = r[1].value
	}
	return out
}

// absent as a value removes a row.
var absent = string([]byte{0})

// withRows returns the rows with the given names set to the values, a value of absent removing the row; names the rows do not
// hold are appended in name order.
func withRows(base [][]textCell, set map[string]string) [][]textCell {
	var out [][]textCell
	seen := map[string]bool{}
	for _, r := range base {
		v, ok := set[r[0].value]
		seen[r[0].value] = ok
		switch {
		case !ok:
			out = append(out, r)
		case v != absent:
			out = append(out, []textCell{r[0], text(v)})
		}
	}
	for _, name := range sortedKeys(set) {
		if !seen[name] && set[name] != absent {
			out = append(out, []textCell{text(name), text(set[name])})
		}
	}
	return out
}

// sortedKeys returns the keys of m in order.
func sortedKeys(m map[string]string) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	slices.Sort(keys)
	return keys
}

// expectedStatus is what the selection keeps of status rows: the names of the selection with 1 to 20 digits within uint64.
func expectedStatus(rows map[string]string) map[string]uint64 {
	out := map[string]uint64{}
	for _, name := range selectedStatus {
		v, ok := rows[name]
		if !ok || len(v) == 0 || len(v) > 20 || strings.Trim(v, "0123456789") != "" {
			continue
		}
		if n, err := strconv.ParseUint(v, 10, 64); err == nil {
			out[name] = n
		}
	}
	return out
}

// expectedVariables is what the selection keeps of variable rows: the names of the selection with values of at most 1,024 bytes.
func expectedVariables(rows map[string]string) map[string]string {
	out := map[string]string{}
	for _, name := range selectedVariables {
		if v, ok := rows[name]; ok && len(v) <= 1024 {
			out[name] = v
		}
	}
	return out
}

// rig is a collector over a fake server whose answers a test changes between runs.
type rig struct {
	t       *testing.T
	srv     *fakeServer
	logs    *logStore
	proc    fstest.MapFS
	col     *Collector
	dialErr error
	defs    map[string][][]byte
	status  [][]textCell
	vars    [][]textCell
	ownID   string
	created time.Time
	set     map[string]string // the overrides of the last setStatus
	mine    []capturePacket   // the status reply the rig made last; a test that replaces it keeps its own
}

// newRig returns a rig whose database answers as the captures hold it, over a /proc with 4 GB of memory and a loopback listener
// on port 3306.
func newRig(t *testing.T, cfg fakeConfig) *rig {
	t.Helper()
	srv := newFakeServer(t, cfg)
	r := &rig{t: t, srv: srv, logs: &logStore{}, proc: newProc(4*1024*1024, "127.0.0.1:3306"), defs: map[string][][]byte{}, created: time.Now()}
	for _, stmt := range []string{statusText, variablesText, processlistText, currentUserText, privilegesText} {
		r.defs[stmt] = defsOf(srv.replies[stmt])
	}
	r.status = decodeResultSet(t, srv.replies[statusText]).rows
	r.mine = srv.replies[statusText]
	r.vars = decodeResultSet(t, srv.replies[variablesText]).rows
	r.ownID = strconv.FormatUint(uint64(binaryID(srv.capture.greeting.payload)), 10)
	log := slog.New(&recordingHandler{store: r.logs})
	r.col = New(Sources{Dial: r.dial, Proc: r.proc}, "/run/mysqld/mysqld.sock", log)
	return r
}

// binaryID returns the connection id of a greeting payload.
func binaryID(greetingPayload []byte) uint32 {
	g, _ := parseGreeting(greetingPayload)
	return g.id
}

// dial is the Dialer of the collector under test.
func (r *rig) dial(ctx context.Context) (net.Conn, error) {
	if r.dialErr != nil {
		return nil, r.dialErr
	}
	return r.srv.dial(ctx)
}

// setStatus makes the next runs answer SHOW GLOBAL STATUS with the captured rows changed as set says.
func (r *rig) setStatus(set map[string]string) {
	r.set = set
	r.mine = resultSetPackets(r.defs[statusText], withRows(r.status, set))
	r.srv.replies[statusText] = r.mine
}

// setVariables makes the next runs answer SHOW GLOBAL VARIABLES with the captured rows changed as set says.
func (r *rig) setVariables(set map[string]string) {
	r.srv.replies[variablesText] = resultSetPackets(r.defs[variablesText], withRows(r.vars, set))
}

// setProcesslist makes the next runs answer SHOW FULL PROCESSLIST with rows.
func (r *rig) setProcesslist(rows ...[]textCell) {
	r.srv.replies[processlistText] = resultSetPackets(r.defs[processlistText], rows)
}

// setAccount makes the next runs answer the account check with the account and its privileges.
func (r *rig) setAccount(account string, privileges ...string) {
	r.srv.replies[currentUserText] = resultSetPackets(r.defs[currentUserText], [][]textCell{{text(account)}})
	rows := make([][]textCell, len(privileges))
	for i, p := range privileges {
		rows[i] = []textCell{text(p)}
	}
	r.srv.replies[privilegesText] = resultSetPackets(r.defs[privilegesText], rows)
}

// setListeners makes the tables of the next runs hold the listeners; broken removes net/tcp, which makes them unreadable.
func (r *rig) setListeners(broken bool, listeners ...string) {
	delete(r.proc, "net/tcp")
	delete(r.proc, "net/tcp6")
	for name, f := range listenerTables(listeners...) {
		if !broken || name != "net/tcp" {
			r.proc[name] = f
		}
	}
}

// settle waits until the fake server has no connection left, which a finished run guarantees.
func (r *rig) settle() {
	r.t.Helper()
	done := make(chan struct{})
	go func() {
		r.srv.wait()
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(testGuard):
		r.t.Error("the fake server still serves a connection after the run ended")
	}
}

// ownsStatus reports whether the status reply is still the one the rig made or the captured one.
func (r *rig) ownsStatus() bool {
	cur := r.srv.replies[statusText]
	return r.mine == nil || (len(cur) > 0 && len(r.mine) > 0 && &cur[0] == &r.mine[0])
}

// collect runs the collector once at the current time.
func (r *rig) collect() ([]model.Record, error) {
	r.t.Helper()
	return r.collectRun(scheduler.Run{Start: time.Now().UTC()})
}

// collectRun runs the collector for run.
func (r *rig) collectRun(run scheduler.Run) ([]model.Record, error) {
	r.t.Helper()
	if _, own := r.set["Uptime"]; !own && r.ownsStatus() {
		// The server keeps running while the clock moves: its Uptime grows with the time since the rig was made.
		up, _ := strconv.ParseUint(rowMap(r.status)["Uptime"], 10, 64)
		set := maps.Clone(r.set)
		if set == nil {
			set = map[string]string{}
		}
		set["Uptime"] = strconv.FormatUint(up+uint64(max(run.Start.Sub(r.created), 0)/time.Second), 10)
		r.mine = resultSetPackets(r.defs[statusText], withRows(r.status, set))
		r.srv.replies[statusText] = r.mine
	}
	recs, err := r.col.Collect(testContext(r.t), run)
	r.settle()
	return recs, err
}

// mustCollect runs the collector once and fails the test when the run fails.
func (r *rig) mustCollect() []model.Record {
	r.t.Helper()
	recs, err := r.collect()
	if err != nil {
		r.t.Fatalf("Collect() = %v, want a record", err)
	}
	return recs
}

// statusOf returns the one mariadb_status record of a run.
func statusOf(t *testing.T, recs []model.Record) *model.MariaDBStatus {
	t.Helper()
	var found []*model.MariaDBStatus
	for _, r := range recs {
		if s, ok := r.Data.(*model.MariaDBStatus); ok {
			found = append(found, s)
		}
	}
	if len(found) != 1 {
		t.Fatalf("Collect() returned %d mariadb_status records among %d, want 1", len(found), len(recs))
	}
	return found[0]
}

// metricsOf returns the metric records of a run by name.
func metricsOf(t *testing.T, recs []model.Record) map[string]model.MetricPoint {
	t.Helper()
	out := map[string]model.MetricPoint{}
	for _, r := range recs {
		if m, ok := r.Data.(*model.MetricPoint); ok {
			if _, dup := out[m.Name]; dup {
				t.Errorf("metric %s written twice in one run", m.Name)
			}
			out[m.Name] = *m
		}
	}
	return out
}

// checkMeta fails unless every record has source mariadb and the run's start as its capture time.
func checkMeta(t *testing.T, recs []model.Record, start time.Time) {
	t.Helper()
	for i, r := range recs {
		if r.Source != Name || !r.CapturedAt.Equal(start) {
			t.Errorf("record %d (%s) has source %q captured at %v, want source %q captured at %v", i, r.Kind(), r.Source, r.CapturedAt, Name, start)
		}
	}
}

// wantOnlyStatus fails unless the run wrote exactly one record, of the given availability and nothing else.
func wantOnlyStatus(t *testing.T, recs []model.Record, availability model.MariaDBAvailability, code uint16) {
	t.Helper()
	if len(recs) != 1 {
		t.Fatalf("Collect() returned %d records, want only the mariadb_status", len(recs))
	}
	s := statusOf(t, recs)
	if s.Availability != availability || s.PingLatency != nil || len(s.Status) != 0 || len(s.Variables) != 0 || len(s.Threads) != 0 || s.Complete {
		t.Errorf("record = %+v, want only availability %q", *s, availability)
	}
	switch {
	case code == 0 && s.ErrorCode != nil:
		t.Errorf("error_code = %d, want none", *s.ErrorCode)
	case code != 0 && (s.ErrorCode == nil || *s.ErrorCode != code):
		t.Errorf("error_code = %v, want %d", s.ErrorCode, code)
	}
}

// slowPing returns an OnCommand hook that answers the ping after d.
func slowPing(d time.Duration) func(*fakeSession, string) bool {
	return func(c *fakeSession, command string) bool {
		if command != "ping" {
			return false
		}
		time.Sleep(d)
		c.send(capturePacket{seq: 1, payload: pingOKFromDocs()})
		return true
	}
}

// opError wraps an errno as the dial of a unix socket reports it.
func opError(errno syscall.Errno) error {
	return &net.OpError{Op: "dial", Net: "unix", Err: os.NewSyscallError("connect", errno)}
}

// fixedStatements returns the statements of one up run in their order.
func fixedStatements(processlist bool) []string {
	out := []string{statusText, variablesText, currentUserText, privilegesText}
	if processlist {
		out = append(out, processlistText)
	}
	return out
}

func TestCollect_UpRecordHoldsTheSelection(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		r.srv.cfg.OnCommand = slowPing(50 * time.Millisecond)
		run := scheduler.Run{Start: time.Now().UTC()}

		recs, err := r.collectRun(run)

		if err != nil {
			t.Fatalf("Collect() = %v, want the up record", err)
		}
		checkMeta(t, recs, run.Start)
		s := statusOf(t, recs)
		if s.Availability != model.MariaDBUp || s.Complete || len(s.Threads) != 0 || s.ErrorCode != nil {
			t.Errorf("record = %+v, want up, complete false, no threads, no error_code", *s)
		}
		if s.PingLatency == nil || *s.PingLatency != 50*time.Millisecond {
			t.Errorf("ping_latency_ns = %v, want the 50ms round trip of the ping", s.PingLatency)
		}
		wantStatus := expectedStatus(rowMap(r.status))
		if len(wantStatus) != 14 || !mapsEqual(s.Status, wantStatus) {
			t.Errorf("status = %v, want the %d values of the selection %v", s.Status, len(wantStatus), wantStatus)
		}
		wantVars := expectedVariables(rowMap(r.vars))
		if len(wantVars) != 20 || !mapsEqual(s.Variables, wantVars) {
			t.Errorf("variables = %v, want the %d values of the selection %v", s.Variables, len(wantVars), wantVars)
		}
		checkLogs(t, "an up run", r.logs.take())
	})
}

// mapsEqual compares two maps.
func mapsEqual[V comparable](a, b map[string]V) bool {
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

func TestCollect_FirstUpRunWritesTheMetricsOfTheFirstSample(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		const ram = 4.0 * 1024 * 1024 * 1024
		want := map[string]model.MetricPoint{
			metricMemoryMax:     {Name: metricMemoryMax, Value: 898035712, Unit: "bytes"},
			metricMemoryPercent: {Name: metricMemoryPercent, Value: 100 * 898035712 / ram, Unit: "percent"},
			metricPoolUsed:      {Name: metricPoolUsed, Value: 100 * float64(8112-6534) / 8112, Unit: "percent"},
		}

		got := metricsOf(t, r.mustCollect())

		for name, w := range want {
			if g, ok := got[name]; !ok || !near(g.Value, w.Value) || g.Unit != w.Unit || len(g.Labels) != 0 {
				t.Errorf("%s = %+v (%v), want %+v", name, g, ok, w)
			}
		}
		if g, ok := got[metricExposed]; !ok || g.Value != 0 {
			t.Errorf("%s = %+v (%v), want 0 for a loopback listener", metricExposed, g, ok)
		}
		if len(got) != 4 {
			t.Errorf("metrics = %v, want the four of a first sample: the maximum, its share of RAM, the pool and the listeners", got)
		}
	})
}

func TestCollect_DownForMissingRefusedOrNonSocketPath(t *testing.T) {
	staleSocket := func(path string) {
		l, err := net.Listen("unix", path)
		if err != nil {
			t.Fatalf("listening on %s: %v", path, err)
		}
		ul, ok := l.(*net.UnixListener)
		if !ok {
			t.Fatalf("listener is a %T, want a unix listener", l)
		}
		ul.SetUnlinkOnClose(false)
		if err = ul.Close(); err != nil {
			t.Fatalf("closing the listener: %v", err)
		}
	}
	regularFile := func(path string) {
		if err := os.WriteFile(path, []byte("not a socket"), 0o600); err != nil {
			t.Fatalf("writing %s: %v", path, err)
		}
	}
	for _, tc := range []struct {
		name  string
		setup func(path string)
	}{
		{"none", func(string) {}},
		{"stale", staleSocket},
		{"file", regularFile},
	} {
		t.Run(tc.name, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "s")
			if len(path) > 100 {
				t.Fatalf("socket path %q has %d bytes, too long for a unix socket in this environment", path, len(path))
			}
			tc.setup(path)
			logs := &logStore{}
			c := New(Sources{Proc: newProc(4 * 1024 * 1024)}, path, slog.New(&recordingHandler{store: logs}))
			run := scheduler.Run{Start: time.Now().UTC()}

			recs, err := c.Collect(testContext(t), run)

			if err != nil {
				t.Fatalf("Collect() = %v, want a down record", err)
			}
			wantOnlyStatus(t, recs, model.MariaDBDown, 0)
			checkMeta(t, recs, run.Start)
			checkLogs(t, "the first down run", logs.take(), warn(msgDown, nil))
		})
	}
}

func TestCollect_DownForInjectedRefusals(t *testing.T) {
	for _, errno := range []syscall.Errno{syscall.ENOENT, syscall.ECONNREFUSED} {
		t.Run(errno.Error(), func(t *testing.T) {
			r := newRig(t, fakeConfig{})
			r.dialErr = opError(errno)

			recs, err := r.collect()

			if err != nil {
				t.Fatalf("Collect() = %v, want a down record", err)
			}
			wantOnlyStatus(t, recs, model.MariaDBDown, 0)
			checkLogs(t, "the first down run", r.logs.take(), warn(msgDown, nil))
		})
	}
}

func TestCollect_LogsAvailabilityChangesOnce(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		steps := []struct {
			name string
			down bool
			want []wantLog
		}{
			{"first down run", true, []wantLog{warn(msgDown, nil)}},
			{"second down run", true, nil},
			{"first up run after it", false, []wantLog{info(msgAgain)}},
			{"second up run", false, nil},
			{"down again", true, []wantLog{warn(msgDown, nil)}},
		}
		for _, s := range steps {
			r.dialErr = nil
			if s.down {
				r.dialErr = opError(syscall.ENOENT)
			}

			r.mustCollect()

			checkLogs(t, s.name, r.logs.take(), s.want...)
			time.Sleep(30 * time.Second)
		}
	})
}

func TestCollect_NotAnsweringWithinTheBudget(t *testing.T) {
	statusReply := func(r *rig) []capturePacket { return r.srv.replies[statusText] }
	for _, tc := range []struct {
		name    string
		prepare func(r *rig)
		elapsed time.Duration
	}{
		{"no greeting", func(r *rig) { r.srv.cfg.NoGreeting = true }, 2 * time.Second},
		{"connection pending in a full queue", func(r *rig) { r.dialErr = opError(syscall.EAGAIN) }, 0},
		{"silent after the handshake", func(r *rig) { r.srv.cfg.SilentLogin = true }, 4 * time.Second},
		{"silent at the ping", func(r *rig) { r.srv.cfg.OnCommand = hangOn("ping") }, 4 * time.Second},
		{"silent at the variables statement", func(r *rig) { r.srv.cfg.OnCommand = hangOn(variablesText) }, 4 * time.Second},
		{"closed in the middle of the status result", func(r *rig) {
			r.srv.cfg.OnCommand = func(c *fakeSession, command string) bool {
				return command == statusText && closeAfter(statusReply(r), 5)(c, command)
			}
		}, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				r := newRig(t, fakeConfig{})
				tc.prepare(r)
				start := time.Now()

				recs, err := r.collect()

				if err != nil {
					t.Fatalf("Collect() = %v, want a not_answering record and no error", err)
				}
				wantOnlyStatus(t, recs, model.MariaDBNotAnswering, 0)
				if got := time.Since(start); got != tc.elapsed {
					t.Errorf("Collect() returned after %v, want %v after the run's start", got, tc.elapsed)
				}
				checkLogs(t, "the first run", r.logs.take(), warn(msgNotAnswering, nil))

				time.Sleep(30 * time.Second)
				r.mustCollect()
				checkLogs(t, "the second run of the episode", r.logs.take())
			})
		})
	}
}

func TestCollect_NotAnsweringIsLoggedAgainAfterAnotherState(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		r.dialErr = opError(syscall.EAGAIN)
		r.mustCollect()
		checkLogs(t, "not answering", r.logs.take(), warn(msgNotAnswering, nil))
		r.dialErr = nil
		r.mustCollect()
		checkLogs(t, "up again", r.logs.take(), info(msgAgain))
		r.dialErr = opError(syscall.EAGAIN)

		r.mustCollect()

		checkLogs(t, "not answering a second time", r.logs.take(), warn(msgNotAnswering, nil))
	})
}

// refusal is a way the server refuses the agent.
type refusal struct {
	name  string
	apply func(cfg *fakeConfig)
	code  uint16
}

// refusals are the errors of AC5: before the greeting, at the login, and an authentication switch followed by an error.
func refusals() []refusal {
	login := func(code uint16, state string) func(*fakeConfig) {
		return func(cfg *fakeConfig) {
			cfg.LoginAnswers = [][]byte{loginErrorFromDocs(code, state, "refused for SENTINEL")}
		}
	}
	greeting := func(code uint16) func(*fakeConfig) {
		return func(cfg *fakeConfig) { cfg.Greeting = greetingErrorFromDocs(code, "refused for SENTINEL") }
	}
	return []refusal{
		{"1040 instead of the greeting", greeting(1040), 1040},
		{"1053 instead of the greeting", greeting(1053), 1053},
		{"1698 at the login", login(1698, "28000"), 1698},
		{"1045 at the login", login(1045, "28000"), 1045},
		{"1226 at the login", login(1226, "42000"), 1226},
		{"1129 at the login", login(1129, "HY000"), 1129},
		{"an authentication switch and 1045", func(cfg *fakeConfig) {
			cfg.LoginAnswers = [][]byte{authSwitchFromDocs(), loginErrorFromDocs(1045, "28000", "refused for SENTINEL")}
		}, 1045},
		{"an authentication switch and 1698", func(cfg *fakeConfig) {
			cfg.LoginAnswers = [][]byte{authSwitchFromDocs(), loginErrorFromDocs(1698, "28000", "refused for SENTINEL")}
		}, 1698},
	}
}

// refusalLog returns the line a refusal with code logs.
func refusalLog(code uint16) wantLog {
	attrs := map[string]string{"error_code": strconv.Itoa(int(code))}
	if code == 1045 || code == 1698 {
		attrs["fix"] = fixText
		return warn(msgUserMissing, attrs)
	}
	return warn(msgLoginRefused, attrs)
}

func TestCollect_ServerErrorsBecomeNotAnsweringWithCode(t *testing.T) {
	for _, tc := range refusals() {
		t.Run(tc.name, func(t *testing.T) {
			var cfg fakeConfig
			tc.apply(&cfg)
			r := newRig(t, cfg)

			recs, err := r.collect()

			if err != nil {
				t.Fatalf("Collect() = %v, want a not_answering record and no gap", err)
			}
			wantOnlyStatus(t, recs, model.MariaDBNotAnswering, tc.code)
			checkLogs(t, "the first refused run", r.logs.take(), refusalLog(tc.code))
			for i := 2; i <= 3; i++ {
				r.mustCollect()
				checkLogs(t, fmt.Sprintf("refused run %d of the episode", i), r.logs.take())
			}
		})
	}
}

func TestCollect_RefusalAfterUpRunsIsLoggedOncePerEpisode(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		r.mustCollect()
		r.mustCollect()
		checkLogs(t, "two up runs", r.logs.take())
		refused := fakeConfig{}
		refusals()[2].apply(&refused)
		r.srv.cfg.LoginAnswers = refused.LoginAnswers

		recs := r.mustCollect()
		wantOnlyStatus(t, recs, model.MariaDBNotAnswering, 1698)
		checkLogs(t, "the first refused run", r.logs.take(), refusalLog(1698))
		r.mustCollect()
		checkLogs(t, "the second refused run", r.logs.take())

		r.srv.cfg.LoginAnswers = nil
		r.mustCollect()
		checkLogs(t, "the up run after the refusal", r.logs.take(), info(msgAgain))
		r.srv.cfg.LoginAnswers = refused.LoginAnswers
		r.mustCollect()
		checkLogs(t, "the next refusal", r.logs.take(), refusalLog(1698))
	})
}

// failedRun is a run the collector must fail, with the exact error text.
type failedRun struct {
	name    string
	prepare func(r *rig)
	want    string
}

// failedRuns are the replies and errors of AC12 that fail a run.
func failedRuns() []failedRun {
	replaceLast := func(reply []capturePacket, payload []byte) []capturePacket {
		return withPayload(reply, len(reply)-1, payload)
	}
	return []failedRun{
		{"permission denied", func(r *rig) { r.dialErr = opError(syscall.EACCES) }, "mariadb: connect: permission denied"},
		{"another connect error", func(r *rig) { r.dialErr = errors.New("dial unix /secret/path: strange") }, "mariadb: connect: failed"},
		{"status name twice", func(r *rig) {
			rows := append(slices.Clone(r.status), []textCell{text("Uptime"), text("5")})
			r.srv.replies[statusText] = resultSetPackets(r.defs[statusText], rows)
		}, "mariadb: status: name sent twice"},
		{"variables name twice", func(r *rig) {
			rows := append(slices.Clone(r.vars), []textCell{text("version"), text("other")})
			r.srv.replies[variablesText] = resultSetPackets(r.defs[variablesText], rows)
		}, "mariadb: variables: name sent twice"},
		{"error to the status statement", func(r *rig) {
			r.srv.replies[statusText] = sequenced(statementErrorFromDocs(1227, "42000", "denied for SENTINEL"))
		}, "mariadb: status: server error 1227"},
		{"error to the ping", func(r *rig) { r.srv.cfg.Ping = statementErrorFromDocs(1064, "42000", "SENTINEL") }, "mariadb: ping: server error 1064"},
		{"local file request", func(r *rig) {
			r.srv.replies[statusText] = sequenced(append([]byte{0xfb}, "/etc/SENTINEL"...))
		}, "mariadb: status: local file request refused"},
		{"more results", func(r *rig) {
			reply := r.srv.replies[variablesText]
			r.srv.replies[variablesText] = replaceLast(reply, eofPayload(statusCaptured|statusMoreResults))
		}, "mariadb: variables: more results refused"},
		{"a second authentication switch", func(r *rig) {
			r.srv.cfg.LoginAnswers = [][]byte{authSwitchFromDocs(), authSwitchFromDocs()}
		}, "mariadb: login: authentication switch refused"},
		{"packet out of sequence", func(r *rig) {
			r.srv.replies[statusText] = withSeq(r.srv.replies[statusText], 3, 77)
		}, "mariadb: status: packet out of sequence"},
	}
}

// namedFailures are the failed runs that have a test of their own.
var namedFailures = []string{"permission denied", "status name twice", "variables name twice"}

func TestCollect_FailedRunsHaveFixedTexts(t *testing.T) {
	for _, tc := range failedRuns() {
		if slices.Contains(namedFailures, tc.name) {
			continue
		}
		t.Run(tc.name, func(t *testing.T) { checkFailedRun(t, tc) })
	}
}

func TestCollect_StatusNameTwiceFailsTheRun(t *testing.T) {
	for _, tc := range failedRuns() {
		if tc.name == "status name twice" || tc.name == "variables name twice" {
			t.Run(tc.name, func(t *testing.T) { checkFailedRun(t, tc) })
		}
	}
}

func TestCollect_PermissionDeniedFailsTheRun(t *testing.T) {
	for _, tc := range failedRuns() {
		if tc.name == "permission denied" {
			checkFailedRun(t, tc)
		}
	}
}

// checkFailedRun runs the collector against a failure and fails unless the run fails with the exact text and logs no text the server sent.
func checkFailedRun(t *testing.T, tc failedRun) {
	t.Helper()
	r := newRig(t, fakeConfig{})
	tc.prepare(r)

	_, err := r.collect()

	if err == nil || err.Error() != tc.want {
		t.Errorf("Collect() error = %v, want %q", err, tc.want)
	}
	for _, l := range r.logs.take() {
		if strings.Contains(describeLog(l.level, l.msg, l.attrs), "SENTINEL") {
			t.Errorf("the collector logged %v, a text the server sent", l)
		}
	}
}

func TestCollect_ProcesslistRefusalsFailTheRun(t *testing.T) {
	for _, tc := range []struct {
		name string
		row  []textCell
		want string
	}{
		{"id not a number", processlistRow("abc", "u", "h", "", "Query", "1", "", "", "0.000"), "mariadb: processlist: unexpected reply"},
		{"id NULL", append([]textCell{nullCell}, processlistRow("1", "u", "h", "", "Query", "1", "", "", "0.000")[1:]...), "mariadb: processlist: unexpected reply"},
		{"time of 21 characters", processlistRow("5", "u", "h", "", "Query", strings.Repeat("1", 21), "", "", "0.000"), "mariadb: processlist: cell too long"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := newRig(t, fakeConfig{})
			r.setStatus(map[string]string{"Threads_running": "11"})
			r.setProcesslist(tc.row)

			_, err := r.collect()

			if err == nil || err.Error() != tc.want {
				t.Errorf("Collect() error = %v, want %q", err, tc.want)
			}
		})
	}
}

func TestCollect_SendsOnlyFixedStatementsOverOneConnection(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		r.setStatus(map[string]string{"Threads_running": "11"})
		for i := 0; i < 2; i++ {
			r.mustCollect()
			time.Sleep(30 * time.Second)
		}
		r.setStatus(map[string]string{"Threads_running": "2"})
		r.mustCollect()

		commands, received, handshakes := r.srv.snapshot()
		dials, maxOpen := r.srv.counts()
		want := flattenRuns(true, true, false)
		if !slices.Equal(commands, want) {
			t.Errorf("commands = %q, want %q: a ping, the fixed statements and a quit per run", commands, want)
		}
		if dials != 3 || maxOpen != 1 || len(handshakes) != 3 {
			t.Errorf("dials = %d, most connections open at once = %d, handshakes = %d, want 3, 1 and 3", dials, maxOpen, len(handshakes))
		}
		checkHandshakes(t, handshakes)
		checkCommandBytes(t, received)
	})
}

// checkHandshakes fails unless every handshake names the user, carries no authentication data and names the plugin.
func checkHandshakes(t *testing.T, handshakes []handshake) {
	t.Helper()
	for _, h := range handshakes {
		if h.user != User || h.authLen != 0 || len(h.auth) != 0 || h.plugin != authPlugin {
			t.Errorf("handshake = user %q, %d bytes of authentication data, plugin %q, want %q, none and %q", h.user, h.authLen, h.plugin, User, authPlugin)
		}
	}
}

// checkCommandBytes fails unless every command the client sent is a query, a ping or a quit.
func checkCommandBytes(t *testing.T, received []capturePacket) {
	t.Helper()
	for _, p := range received {
		if p.seq == 0 && (len(p.payload) == 0 || !slices.Contains([]byte{commandQuery, commandPing, commandQuit}, p.payload[0])) {
			t.Errorf("the client sent a command packet %v, want only query, ping and quit", p.payload)
		}
	}
}

// flattenRuns returns the commands of runs in order: ping, the fixed statements, and quit, per run; the flag tells whether the run
// reads the process list.
func flattenRuns(processlist ...bool) []string {
	var out []string
	for _, p := range processlist {
		out = append(out, "ping")
		out = append(out, fixedStatements(p)...)
		out = append(out, "quit")
	}
	return out
}

// account is what the account check of a run finds.
type account struct {
	user       string
	privileges []string
	fail       string // the statement that answers with an error
}

var (
	accountOK        = account{user: "vandox-agent@localhost", privileges: []string{"PROCESS"}}
	accountAnonymous = account{user: "@localhost", privileges: []string{"PROCESS"}}
	accountOtherHost = account{user: "vandox-agent@%", privileges: []string{"PROCESS"}}
	accountNoProcess = account{user: "vandox-agent@localhost", privileges: []string{"SELECT"}}
	accountNoRights  = account{user: "vandox-agent@localhost"}
	accountMore      = account{user: "vandox-agent@localhost", privileges: []string{"SELECT", "PROCESS"}}
	accountUserError = account{fail: currentUserText}
	accountRightsErr = account{user: "vandox-agent@localhost", fail: privilegesText}
)

// apply makes the rig answer the account check as a does.
func (a account) apply(r *rig) {
	r.setAccount(a.user, a.privileges...)
	if a.fail != "" {
		r.srv.replies[a.fail] = sequenced(statementErrorFromDocs(1142, "42000", "denied for SENTINEL"))
	}
}

func TestCollect_AccountCheck(t *testing.T) {
	for _, tc := range []struct {
		name     string
		accounts []account
		warnings []int // lines logged per run
	}{
		{"the installed account", []account{accountOK, accountOK}, []int{0, 0}},
		{"PROCESS among other privileges", []account{accountMore}, []int{0}},
		{"an anonymous account", []account{accountAnonymous, accountAnonymous, accountAnonymous}, []int{1, 0, 0}},
		{"the right user for another host", []account{accountOtherHost}, []int{1}},
		{"no PROCESS", []account{accountNoProcess, accountNoProcess}, []int{1, 0}},
		{"no privilege at all", []account{accountNoRights}, []int{1}},
		{"a change after passing runs", []account{accountOK, accountOK, accountAnonymous, accountAnonymous}, []int{0, 0, 1, 0}},
		{"again only after a passing check", []account{accountAnonymous, accountOK, accountNoProcess}, []int{1, 0, 1}},
		{"an error leaves the state passing", []account{accountOK, accountUserError, accountAnonymous}, []int{0, 0, 1}},
		{"an error leaves the state failing", []account{accountAnonymous, accountUserError, accountAnonymous}, []int{1, 0, 0}},
		{"an error to the privileges", []account{accountOK, accountRightsErr, accountNoProcess}, []int{0, 0, 1}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) { runAccountCase(t, tc.accounts, tc.warnings) })
		})
	}
}

// runAccountCase runs the collector once per account and checks the record, the lines and the statements of every run.
func runAccountCase(t *testing.T, accounts []account, warnings []int) {
	t.Helper()
	r := newRig(t, fakeConfig{})
	wantStatus := expectedStatus(rowMap(r.status))
	for i, a := range accounts {
		a.apply(r)
		before, _, _ := r.srv.snapshot()

		recs := r.mustCollect()

		s := statusOf(t, recs)
		wantRun := maps.Clone(wantStatus)
		wantRun["Uptime"] = s.Status["Uptime"] // the server's uptime grows with the clock
		if s.Availability != model.MariaDBUp || !mapsEqual(s.Status, wantRun) {
			t.Errorf("run %d record = %+v, want the up record of the selection whatever the check finds", i+1, *s)
		}
		var want []wantLog
		for range warnings[i] {
			want = append(want, warn(msgNotAsExpected, map[string]string{"fix": fixText}))
		}
		checkLogs(t, fmt.Sprintf("run %d", i+1), r.logs.take(), want...)
		checkAccountStatements(t, i+1, r, len(before), a)
		time.Sleep(30 * time.Second)
	}
}

// checkAccountStatements fails unless the run sent the statements of the account check: the first always, the second unless
// the first one failed.
func checkAccountStatements(t *testing.T, run int, r *rig, before int, a account) {
	t.Helper()
	commands, _, _ := r.srv.snapshot()
	sent := statementsOf(commands[before:])
	want := []string{statusText, variablesText, currentUserText, privilegesText}
	if a.fail == currentUserText {
		want = want[:3]
	}
	if !slices.Equal(sent, want) {
		t.Errorf("run %d sent the statements %q, want %q: the account check on every up run", run, sent, want)
	}
}

// listenerRun is one run of an exposure test: the listeners in the tables, or tables that cannot be read.
type listenerRun struct {
	listeners []string
	broken    bool
	want      []wantLog
	count     float64 // the metric; negative when it must be left out
}

func TestCollect_ExposureLogsOnChanges(t *testing.T) {
	loopback, wild := "127.0.0.1:3306", "0.0.0.0:3306"
	exposed1 := []wantLog{warn(msgExposed, map[string]string{"count": "1"})}
	runs := []listenerRun{
		{[]string{loopback}, false, nil, 0},
		{[]string{wild}, false, exposed1, 1},
		{[]string{wild}, false, nil, 1},
		{[]string{wild}, true, nil, -1},
		{[]string{wild}, false, nil, 1},
		{[]string{loopback}, false, []wantLog{info(msgLoopbackOnly)}, 0},
		{[]string{loopback}, false, nil, 0},
		{[]string{loopback}, true, nil, -1},
		{[]string{wild, "[::]:3306"}, false, []wantLog{warn(msgExposed, map[string]string{"count": "2"})}, 2},
	}
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		for i, run := range runs {
			r.setListeners(run.broken, run.listeners...)

			recs := r.mustCollect()

			m, ok := metricsOf(t, recs)[metricExposed]
			switch {
			case run.count < 0 && ok:
				t.Errorf("run %d wrote %s = %v for unreadable tables, want the metric left out", i+1, metricExposed, m.Value)
			case run.count >= 0 && (!ok || m.Value != run.count):
				t.Errorf("run %d wrote %s = %v (%v), want %v", i+1, metricExposed, m.Value, ok, run.count)
			}
			checkLogs(t, fmt.Sprintf("run %d", i+1), r.logs.take(), run.want...)
			time.Sleep(30 * time.Second)
		}
	})
}

func TestCollect_ExposureNeedsAValidPort(t *testing.T) {
	for _, tc := range []struct {
		name string
		set  map[string]string
		want float64 // negative: left out
	}{
		{"captured port", nil, 1},
		{"port missing", map[string]string{"port": absent}, -1},
		{"port not a number", map[string]string{"port": "mysql"}, -1},
		{"port zero", map[string]string{"port": "0"}, -1},
		{"port above 65535", map[string]string{"port": "65536"}, -1},
		{"extra port not a number", map[string]string{"extra_port": "none"}, -1},
		{"extra port above 65535", map[string]string{"extra_port": "65536"}, -1},
		{"extra port missing", map[string]string{"extra_port": absent}, 1},
		{"extra port exposed", map[string]string{"extra_port": "3307"}, 2},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := newRig(t, fakeConfig{})
			r.setListeners(false, "0.0.0.0:3306", "10.1.2.3:3307", "0.0.0.0:8080")
			r.setVariables(tc.set)

			m, ok := metricsOf(t, r.mustCollect())[metricExposed]

			switch {
			case tc.want < 0 && ok:
				t.Errorf("%s = %v, want the metric left out", metricExposed, m.Value)
			case tc.want >= 0 && (!ok || m.Value != tc.want):
				t.Errorf("%s = %v (%v), want %v", metricExposed, m.Value, ok, tc.want)
			}
		})
	}
}

func TestCollect_MemoryAboveRAMLogsOnTransitions(t *testing.T) {
	const maxBytes = 898035712
	above := warn(msgAboveRAM, map[string]string{"max_bytes": "898035712", "ram_bytes": "524288000"})
	steps := []struct {
		name    string
		totalKB int // 0 removes meminfo
		want    []wantLog
	}{
		{"RAM above the maximum", 4 * 1024 * 1024, nil},
		{"RAM below the maximum", 512000, []wantLog{above}},
		{"still below", 512000, nil},
		{"RAM above again", 4 * 1024 * 1024, nil},
		{"below again", 512000, []wantLog{above}},
	}
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		for _, s := range steps {
			r.proc["meminfo"] = meminfoFile(s.totalKB)

			m := metricsOf(t, r.mustCollect())

			ram := float64(s.totalKB) * 1024
			if got, ok := m[metricMemoryMax]; !ok || got.Value != maxBytes || got.Unit != "bytes" {
				t.Errorf("%s: %s = %+v (%v), want %d bytes", s.name, metricMemoryMax, got, ok, maxBytes)
			}
			if got, ok := m[metricMemoryPercent]; !ok || !near(got.Value, 100*maxBytes/ram) || got.Unit != "percent" {
				t.Errorf("%s: %s = %+v (%v), want %v percent", s.name, metricMemoryPercent, got, ok, 100*maxBytes/ram)
			}
			checkLogs(t, s.name, r.logs.take(), s.want...)
			time.Sleep(30 * time.Second)
		}
	})
}

func TestCollect_MemoryMaximumWithoutMeminfoLeavesOutOnlyThePercentage(t *testing.T) {
	r := newRig(t, fakeConfig{})
	delete(r.proc, "meminfo")

	recs := r.mustCollect()

	m := metricsOf(t, recs)
	if got, ok := m[metricMemoryMax]; !ok || got.Value != 898035712 {
		t.Errorf("%s = %+v (%v), want 898035712", metricMemoryMax, got, ok)
	}
	if got, ok := m[metricMemoryPercent]; ok {
		t.Errorf("%s = %v, want it left out without meminfo", metricMemoryPercent, got.Value)
	}
	if s := statusOf(t, recs); s.Availability != model.MariaDBUp {
		t.Errorf("availability = %q, want up", s.Availability)
	}
	checkLogs(t, "a run without meminfo", r.logs.take())
}

// near compares floats of a formula.
func near(got, want float64) bool {
	d := got - want
	return d < 1e-9*max(1, want) && -d < 1e-9*max(1, want)
}

// threadRows is a process list in the captured column layout: the agent's own row and six others.
func threadRows(own string) [][]textCell {
	return [][]textCell{
		processlistRow(own, User, "localhost", "", "Query", "0", "starting", "SHOW FULL PROCESSLIST", "0.000"),
		processlistRow("100", "u1", "h1", "db1", "Sleep", "300", "", "", "0.000"),
		processlistRow("101", "u2", "h2", "db2", "Query", "5", "executing", "SELECT 1", "0.000"),
		processlistRow("102", "u3", "h3", "", "Query", "50", "executing", strings.Repeat("x", 16385), "0.000"),
		processlistRow("103", "u4", "h4", "db4", "Sleep", "300", "", "", "0.000"),
		processlistRow("104", "u5", "h5", "", "Sleep", "10", "", "", "0.000"),
		processlistRow("105", "u6", "h6", "db6", "Connect", "50", "waiting", "", "0.000"),
	}
}

// expectedThreads is the order of S6 for threadRows.
func expectedThreads() []model.MariaDBThread {
	return []model.MariaDBThread{
		{ID: 102, User: "u3", Host: "h3", Command: "Query", TimeSeconds: 50, State: "executing", Info: strings.Repeat("x", 16384), Truncated: true},
		{ID: 105, User: "u6", Host: "h6", DB: "db6", Command: "Connect", TimeSeconds: 50, State: "waiting"},
		{ID: 101, User: "u2", Host: "h2", DB: "db2", Command: "Query", TimeSeconds: 5, State: "executing", Info: "SELECT 1"},
		{ID: 100, User: "u1", Host: "h1", DB: "db1", Command: "Sleep", TimeSeconds: 300},
		{ID: 103, User: "u4", Host: "h4", DB: "db4", Command: "Sleep", TimeSeconds: 300},
		{ID: 104, User: "u5", Host: "h5", Command: "Sleep", TimeSeconds: 10},
	}
}

func TestCollect_ThreadSnapshotWhenRunningOrUnderPressure(t *testing.T) {
	for _, tc := range []struct {
		name     string
		running  string // absent removes the status row
		pressure bool
		want     bool
	}{
		{"11 running", "11", false, true},
		{"10 running", "10", false, false},
		{"Threads_running missing", absent, false, false},
		{"1 running under pressure", "1", true, true},
		{"1 running without pressure", "1", false, false},
		{"10 running under pressure", "10", true, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := newRig(t, fakeConfig{})
			r.setStatus(map[string]string{"Threads_running": tc.running})
			r.setProcesslist(threadRows(r.ownID)...)
			run := scheduler.Run{Start: time.Now().UTC(), Pressure: tc.pressure}

			recs, err := r.collectRun(run)

			if err != nil {
				t.Fatalf("Collect() = %v, want the up record", err)
			}
			commands, _, _ := r.srv.snapshot()
			checkSnapshot(t, statusOf(t, recs), slices.Contains(commands, processlistText), tc.want)
		})
	}
}

// checkSnapshot fails unless the process list was asked exactly when due and the record holds the threads exactly then.
func checkSnapshot(t *testing.T, s *model.MariaDBStatus, asked, due bool) {
	t.Helper()
	if asked != due {
		t.Errorf("the process list was asked = %v, want %v", asked, due)
	}
	if !due {
		if len(s.Threads) != 0 {
			t.Errorf("threads = %v, want none", s.Threads)
		}
		return
	}
	if want := expectedThreads(); !slices.Equal(s.Threads, want) {
		t.Errorf("threads = %+v\nwant %+v", s.Threads, want)
	}
	if s.Availability != model.MariaDBUp || s.Complete {
		t.Errorf("record = %+v, want up and complete false", *s)
	}
}

func TestCollect_StatusAndVariablesKeepOnlyUsableValues(t *testing.T) {
	r := newRig(t, fakeConfig{})
	r.setStatus(map[string]string{"Questions": "twelve", "Slow_queries": "18446744073709551616", "Aborted_clients": "18446744073709551615"})
	r.setVariables(map[string]string{"version": strings.Repeat("v", 1025), "log_bin": strings.Repeat("o", 1024)})

	s := statusOf(t, r.mustCollect())

	for _, name := range []string{"Questions", "Slow_queries"} {
		if v, ok := s.Status[name]; ok {
			t.Errorf("status %s = %d, want it left out", name, v)
		}
	}
	if s.Status["Aborted_clients"] != 18446744073709551615 || len(s.Status) != 12 {
		t.Errorf("status = %v, want 12 values with Aborted_clients at the largest uint64", s.Status)
	}
	if _, kept := s.Variables["version"]; kept || len(s.Variables["log_bin"]) != 1024 || len(s.Variables) != 19 {
		t.Errorf("variables: %d entries, version kept = %v, log_bin has %d bytes, want 19, false and 1024", len(s.Variables), kept, len(s.Variables["log_bin"]))
	}
}

// blockingFS is a /proc whose file name blocks until release is closed.
type blockingFS struct {
	fs.FS
	name    string
	release <-chan struct{}
}

// Open blocks on the file name.
func (b blockingFS) Open(name string) (fs.File, error) {
	if name == b.name {
		<-b.release
	}
	return b.FS.Open(name)
}

// sinkPut is one call of the recording sink.
type sinkPut struct {
	at      time.Time
	records []model.Record
}

// recordingSink records what the scheduler puts.
type recordingSink struct {
	mu   sync.Mutex
	puts []sinkPut
}

// Put records the records with the time of the call.
func (s *recordingSink) Put(_ context.Context, records []model.Record) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.puts = append(s.puts, sinkPut{at: time.Now(), records: slices.Clone(records)})
	return nil
}

// gapsOf returns the gap puts of a collector with the time they were put.
func (s *recordingSink) gapsOf(collector string) []sinkPut {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []sinkPut
	for _, p := range s.puts {
		for _, r := range p.records {
			if g, ok := r.Data.(*model.Gap); ok && g.Collector == collector {
				out = append(out, sinkPut{at: p.at, records: []model.Record{r}})
			}
		}
	}
	return out
}

// kinds returns how many records of kind source put, by source.
func (s *recordingSink) count(source string, kind model.Kind) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	n := 0
	for _, p := range s.puts {
		for _, r := range p.records {
			if r.Source == source && r.Kind() == kind {
				n++
			}
		}
	}
	return n
}

// ticker is a collector that writes a metric per run and notes when its runs started.
type ticker struct {
	mu     sync.Mutex
	starts []time.Time
}

// Name returns the name of the collector.
func (*ticker) Name() string { return "ticker" }

// Collect notes the start and returns one metric.
func (c *ticker) Collect(_ context.Context, run scheduler.Run) ([]model.Record, error) {
	c.mu.Lock()
	c.starts = append(c.starts, run.Start)
	c.mu.Unlock()
	return []model.Record{{Meta: model.Meta{Source: "ticker"}, Data: &model.MetricPoint{Name: "tick", Value: 1}}}, nil
}

func TestCollect_BlockedProcReadBecomesATimeoutGap(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		release := make(chan struct{})
		r := newRig(t, fakeConfig{})
		r.col = New(Sources{Dial: r.dial, Proc: blockingFS{FS: r.proc, name: "meminfo", release: release}}, "/run/mysqld/mysqld.sock", slog.New(&recordingHandler{store: r.logs}))
		sink := &recordingSink{}
		tick := &ticker{}
		// The schedule of the agent: every 30 seconds, every 10 under memory pressure, a timeout of 5 seconds.
		sch, err := scheduler.New([]scheduler.Job{
			{Collector: r.col, Schedule: scheduler.Schedule{Interval: 30 * time.Second, PressureInterval: 10 * time.Second, Timeout: 5 * time.Second}},
			{Collector: tick, Schedule: scheduler.Schedule{Interval: 10 * time.Second, Timeout: 2 * time.Second}},
		}, sink, slog.New(slog.DiscardHandler))
		if err != nil {
			t.Fatalf("scheduler.New() = %v", err)
		}
		ctx, cancel := context.WithCancel(t.Context())
		base := time.Now()
		done := make(chan struct{})
		go func() {
			sch.Run(ctx)
			close(done)
		}()

		time.Sleep(41 * time.Second)

		close(release)
		cancel()
		<-done
		checkTimeoutGaps(t, sink, base)
		if dials, _ := r.srv.counts(); dials != 1 {
			t.Errorf("connections opened = %d, want 1: no second connection while the run is stuck", dials)
		}
		if n := sink.count(Name, model.KindMariaDBStatus); n != 0 {
			t.Errorf("mariadb_status records put = %d, want none from the abandoned run", n)
		}
		checkTickerStarts(t, tick, base)
	})
}

// checkTimeoutGaps fails unless the sink holds the two gaps of the stuck collector: the abandoned run at 5 seconds and the skipped
// run at 30 seconds, both collector_timeout, the first from the start of the run to the next due time.
func checkTimeoutGaps(t *testing.T, sink *recordingSink, base time.Time) {
	t.Helper()
	gaps := sink.gapsOf(Name)
	if len(gaps) != 2 {
		t.Fatalf("gaps for %s = %d, want 2: the abandoned run at 5s and the skipped run at 30s", Name, len(gaps))
	}
	for i, wantAt := range []time.Duration{5 * time.Second, 30 * time.Second} {
		g, ok := gaps[i].records[0].Data.(*model.Gap)
		if !ok || g.Cause != model.GapCollectorTimeout || g.Collector != Name || gaps[i].at.Sub(base) != wantAt {
			t.Errorf("gap %d = %+v put at %v, want cause %s of %s put at %v", i+1, g, gaps[i].at.Sub(base), model.GapCollectorTimeout, Name, wantAt)
		}
	}
	if g, ok := gaps[0].records[0].Data.(*model.Gap); ok && (!g.From.Equal(base) || !g.To.Equal(base.Add(30*time.Second))) {
		t.Errorf("first gap covers %v to %v, want the start of the run to the next due time 30s later", g.From.Sub(base), g.To.Sub(base))
	}
}

// checkTickerStarts fails unless the other collector ran every 10 seconds from the start.
func checkTickerStarts(t *testing.T, tick *ticker, base time.Time) {
	t.Helper()
	tick.mu.Lock()
	defer tick.mu.Unlock()
	for i, start := range tick.starts {
		if want := time.Duration(i) * 10 * time.Second; start.Sub(base) != want {
			t.Errorf("run %d of the other collector started at %v, want %v: it keeps its schedule", i+1, start.Sub(base), want)
		}
	}
	if len(tick.starts) != 5 {
		t.Errorf("runs of the other collector = %d, want 5 by 41s", len(tick.starts))
	}
}

func TestClassify_AnswersAndFailures(t *testing.T) {
	for _, tc := range []struct {
		name     string
		err      error
		avail    model.MariaDBAvailability
		code     uint16
		answered bool
	}{
		{"missing socket file", &dialError{errno: syscall.ENOENT}, model.MariaDBDown, 0, true},
		{"connection refused", &dialError{errno: syscall.ECONNREFUSED}, model.MariaDBDown, 0, true},
		{"full backlog", &dialError{errno: syscall.EAGAIN}, model.MariaDBNotAnswering, 0, true},
		{"timeout while connecting", &stepError{step: "connect", err: errTimeout}, model.MariaDBNotAnswering, 0, true},
		{"timeout at the greeting", &stepError{step: "greeting", err: errTimeout}, model.MariaDBNotAnswering, 0, true},
		{"closed at the ping", &stepError{step: "ping", err: errClosed}, model.MariaDBNotAnswering, 0, true},
		{"closed at a statement", fmt.Errorf("run: %w", &stepError{step: "status", err: errClosed}), model.MariaDBNotAnswering, 0, true},
		{"error instead of the greeting", &serverError{step: "greeting", code: 1040}, model.MariaDBNotAnswering, 1040, true},
		{"error at the login", &serverError{step: "login", code: 1698}, model.MariaDBNotAnswering, 1698, true},
		{"error at the login, wrapped", fmt.Errorf("run: %w", &serverError{step: "login", code: 65535}), model.MariaDBNotAnswering, 65535, true},
		{"error to the ping", &serverError{step: "ping", code: 1064}, "", 0, false},
		{"error to a statement", &serverError{step: "status", code: 1227}, "", 0, false},
		{"permission denied", &dialError{errno: syscall.EACCES}, "", 0, false},
		{"unknown connect error", &dialError{}, "", 0, false},
		{"protocol violation", &protocolError{step: "status", reason: reasonMalformed}, "", 0, false},
		{"a plain error", errors.New("boom"), "", 0, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			avail, code, ok := classify(tc.err)

			if avail != tc.avail || code != tc.code || ok != tc.answered {
				t.Errorf("classify(%v) = %q, %d, %v, want %q, %d, %v", tc.err, avail, code, ok, tc.avail, tc.code, tc.answered)
			}
		})
	}
}

// packetStream writes the packets of a reply without holding it in memory: it fills one buffer and flushes it in large writes.
type packetStream struct {
	conn   net.Conn
	buf    []byte
	seq    byte
	failed bool
	num    [20]byte
}

// newPacketStream returns a stream whose first packet has sequence number 1, as a reply to a command.
func newPacketStream(conn net.Conn) *packetStream {
	return &packetStream{conn: conn, buf: make([]byte, 0, 128<<10), seq: 1}
}

// begin starts a packet and returns its offset.
func (w *packetStream) begin() int {
	start := len(w.buf)
	w.buf = append(w.buf, 0, 0, 0, w.seq)
	w.seq++
	return start
}

// end finishes the packet that began at start.
func (w *packetStream) end(start int) {
	n := len(w.buf) - start - 4
	w.buf[start], w.buf[start+1], w.buf[start+2] = byte(n&0xff), byte(n>>8&0xff), byte(n>>16&0xff)
	if len(w.buf) >= 32<<10 {
		w.flush()
	}
}

// flush writes the buffered packets.
func (w *packetStream) flush() {
	if len(w.buf) > 0 && !w.failed {
		if _, err := w.conn.Write(w.buf); err != nil {
			w.failed = true
		}
	}
	w.buf = w.buf[:0]
}

// raw appends a whole packet payload.
func (w *packetStream) raw(payload []byte) {
	start := w.begin()
	w.buf = append(w.buf, payload...)
	w.end(start)
}

// cell appends a length-encoded string.
func (w *packetStream) cell(b []byte) {
	w.buf = appendLenenc(w.buf, uint64(len(b)))
	w.buf = append(w.buf, b...)
}

// number appends a number as a cell of digits.
func (w *packetStream) number(n uint64) {
	digits := strconv.AppendUint(w.num[:0], n, 10)
	w.cell(digits)
}

// appendLenenc appends a length-encoded integer without allocating.
func appendLenenc(dst []byte, n uint64) []byte {
	switch {
	case n < 0xfb:
		return append(dst, byte(n))
	case n < 1<<16:
		return append(dst, 0xfc, byte(n&0xff), byte(n>>8&0xff))
	case n < 1<<24:
		return append(dst, 0xfd, byte(n&0xff), byte(n>>8&0xff), byte(n>>16&0xff))
	}
	panic("cell too large for the test stream")
}

// header sends the column count, the definitions and their EOF.
func (w *packetStream) header(defs [][]byte) {
	w.raw([]byte{byte(len(defs) & 0xff), 1})
	for _, d := range defs {
		w.raw(d)
	}
	w.raw(eofPayload(statusCaptured))
}

// streamed returns an OnCommand hook that answers the statement by calling send on a stream; other commands get their usual answers.
func streamed(statement string, send func(w *packetStream)) func(*fakeSession, string) bool {
	return func(c *fakeSession, command string) bool {
		if command != statement {
			return false
		}
		w := newPacketStream(c.conn)
		send(w)
		w.raw(eofPayload(statusCaptured))
		w.flush()
		return true
	}
}

// Cells of the streamed rows, allocated once.
var (
	progressZero = []byte("0.000")
	valueOne     = []byte("1")
)

// pattern is a text of n bytes of the characters a statement can use against a careless reader: markup, control characters and
// invalid UTF-8.
func pattern(n int) []byte {
	unit := []byte{'<', 0x07, 0xff, '&', '>', 0x1b, 0xc3}
	out := make([]byte, n)
	for i := range out {
		out[i] = unit[i%len(unit)]
	}
	return out
}

// worstCaseProcesslist streams rows threads whose every kept field is at its cap and whose statement has 20 KiB.
func worstCaseProcesslist(defs [][]byte, rows int) func(w *packetStream) {
	user, host, db, command, state, statement := pattern(512), pattern(512), pattern(256), pattern(32), pattern(256), pattern(20*1024)
	return func(w *packetStream) {
		w.header(defs)
		for i := 0; i < rows; i++ {
			start := w.begin()
			w.number(uint64(100000 + i))
			w.cell(user)
			w.cell(host)
			w.cell(db)
			w.cell(command)
			w.number(uint64(100000 + (i*7919)%90000))
			w.cell(state)
			w.cell(statement)
			w.cell(progressZero)
			w.end(start)
		}
	}
}

func TestCollect_WorstCaseProcessListIsBounded(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		r.setStatus(map[string]string{"Threads_running": "11"})
		r.mustCollect() // warms up what the first run initializes
		r.srv.cfg.OnCommand = streamed(processlistText, worstCaseProcesslist(r.defs[processlistText], 4200))
		var recs []model.Record
		var err error

		allocated := allocatedBy(func() { recs, err = r.collect() })

		if err != nil {
			t.Fatalf("Collect() = %v, want the up record", err)
		}
		if allocated > 32<<20 {
			t.Errorf("the run allocated %d bytes, want at most 32 MiB", allocated)
		}
		s := statusOf(t, recs)
		if len(s.Threads) == 0 || len(s.Threads) > MaxThreads {
			t.Fatalf("threads = %d, want 1 to %d", len(s.Threads), MaxThreads)
		}
		rec := model.Record{Meta: model.Meta{Origin: model.OriginAgent, Source: Name, Seq: 1, CapturedAt: recs[0].CapturedAt}, Data: s}
		size, err := wire.CheckRecord(&rec)
		if err != nil || size > 1<<20 {
			t.Errorf("CheckRecord() = %d, %v, want a line of at most 1 MiB that passes", size, err)
		}
		if got := estimate(s); got < size {
			t.Errorf("estimate() = %d, want at least the encoded line of %d bytes", got, size)
		}
		checkThreadTexts(t, s.Threads)
		checkLogs(t, "the worst-case run", r.logs.take())
	})
}

// checkThreadTexts fails unless every text of the threads is valid UTF-8 within the limits of the model.
func checkThreadTexts(t *testing.T, threads []model.MariaDBThread) {
	t.Helper()
	bad := 0
	for _, th := range threads {
		for _, s := range []string{th.User, th.Host, th.DB, th.Command, th.State, th.Info} {
			if strings.ToValidUTF8(s, "") != s {
				bad++
			}
		}
	}
	if bad != 0 {
		t.Errorf("%d thread texts hold invalid UTF-8, want every invalid byte replaced", bad)
	}
}

// longReply streams the rows of the selection and then n further distinct names.
func longReply(defs [][]byte, head [][]textCell, n int) func(w *packetStream) {
	return func(w *packetStream) {
		w.header(defs)
		for _, row := range head {
			w.raw(rowPayload(row...))
		}
		name := []byte("filler_0000000")
		const digits = "0123456789"
		for i := 0; i < n; i++ {
			for j, v := len(name)-1, i; j >= len(name)-7; j, v = j-1, v/10 {
				name[j] = digits[v%10]
			}
			start := w.begin()
			w.cell(name)
			w.cell(valueOne)
			w.end(start)
		}
	}
}

func TestCollect_LongStatusAndVariablesRepliesAreBounded(t *testing.T) {
	for _, statement := range []string{statusText, variablesText} {
		t.Run(statement, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) { runLongReply(t, statement) })
		})
	}
}

// runLongReply runs the collector against a reply of 1,000,000 distinct names to statement and checks what it allocated and kept.
func runLongReply(t *testing.T, statement string) {
	t.Helper()
	r := newRig(t, fakeConfig{})
	r.mustCollect() // warms up what the first run initializes
	head := r.status
	if statement == variablesText {
		head = r.vars
	}
	r.srv.cfg.OnCommand = streamed(statement, longReply(r.defs[statement], head, 1_000_000))
	var recs []model.Record
	var err error

	allocated := allocatedBy(func() { recs, err = r.collect() })

	if err != nil {
		t.Fatalf("Collect() = %v, want the up record", err)
	}
	if allocated > 4<<20 {
		t.Errorf("the run with 1,000,000 names in the reply to %q allocated %d bytes, want at most 4 MiB", statement, allocated)
	}
	s := statusOf(t, recs)
	if want := expectedStatus(rowMap(r.status)); !mapsEqual(s.Status, want) {
		t.Errorf("status = %v, want only the selection %v", s.Status, want)
	}
	if want := expectedVariables(rowMap(r.vars)); !mapsEqual(s.Variables, want) {
		t.Errorf("variables = %v, want only the selection %v", s.Variables, want)
	}
}

func TestServerStart_IsTheRunLessTheUptime(t *testing.T) {
	run := time.Date(2026, 3, 1, 12, 0, 0, 0, time.UTC)
	for _, tc := range []struct {
		name   string
		status map[string]uint64
		want   time.Time
		wantOK bool
	}{
		{"an uptime", map[string]uint64{"Uptime": 3600}, run.Add(-time.Hour), true},
		{"no uptime", map[string]uint64{"Questions": 1}, time.Time{}, false},
		{"an uptime that does not fit a duration", map[string]uint64{"Uptime": maxUptime + 1}, time.Time{}, false},
		{"the largest uptime that fits", map[string]uint64{"Uptime": maxUptime}, run.Add(-time.Duration(maxUptime) * time.Second), true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, ok := serverStart(&sample{status: tc.status, run: run})

			if ok != tc.wantOK || !got.Equal(tc.want) {
				t.Errorf("serverStart() = %v, %v, want %v, %v", got, ok, tc.want, tc.wantOK)
			}
		})
	}
}

func TestNew_FillsTheDefaults(t *testing.T) {
	c := New(Sources{}, "/nonexistent/vandox-test.sock", nil)

	if c.src.Dial == nil || c.src.Proc == nil || c.log == nil {
		t.Fatalf("New() left dial = %v, proc = %v, log = %v unset, want the defaults", c.src.Dial != nil, c.src.Proc != nil, c.log != nil)
	}
	if _, err := fs.Stat(c.src.Proc, "self"); err != nil {
		t.Errorf("default Proc cannot see /proc/self: %v", err)
	}
	if _, err := c.src.Dial(t.Context()); err == nil || !errors.Is(err, syscall.ENOENT) {
		t.Errorf("default dialer on a missing socket = %v, want ENOENT", err)
	}
}
