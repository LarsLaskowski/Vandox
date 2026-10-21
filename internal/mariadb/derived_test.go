package mariadb

import (
	"fmt"
	"math/big"
	"net/netip"
	"slices"
	"strconv"
	"syscall"
	"testing"
	"testing/fstest"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/connstats"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// capturedVariables returns the variables of the selection the captured server reports.
func capturedVariables(t *testing.T) map[string]string {
	t.Helper()
	c := loadSelectCapture(t)
	return expectedVariables(rowMap(decodeResultSet(t, c.command(t, variablesText).reply).rows))
}

// variablesWith returns the captured variables changed as set says; a value of absent removes the variable.
func variablesWith(t *testing.T, set map[string]string) map[string]string {
	t.Helper()
	out := capturedVariables(t)
	for k, v := range set {
		if v == absent {
			delete(out, k)
			continue
		}
		out[k] = v
	}
	return out
}

// numbers reads the variables of the formula as big integers.
type numbers map[string]string

// get returns a variable: 0 when it is missing, ok false when it is no plain number.
func (n numbers) get(name string) (*big.Int, bool) {
	v, present := n[name]
	if !present {
		return new(big.Int), true
	}
	x, ok := new(big.Int).SetString(v, 10)
	if !ok || x.Sign() < 0 || v != x.String() {
		return nil, false
	}
	return x, true
}

// sum adds the variables; ok is false when one is no plain number.
func (n numbers) sum(names ...string) (*big.Int, bool) {
	total := new(big.Int)
	for _, name := range names {
		x, ok := n.get(name)
		if !ok {
			return nil, false
		}
		total.Add(total, x)
	}
	return total, true
}

// allTrue reports whether every flag is set.
func allTrue(flags ...bool) bool {
	for _, f := range flags {
		if !f {
			return false
		}
	}
	return true
}

// referenceMemoryMax computes the formula of S7 with big integers; ok is false when a required value is missing or a value that
// counts is not a plain number.
func referenceMemoryMax(vars map[string]string) (*big.Int, bool) {
	n := numbers(vars)
	_, hasPool := vars["innodb_buffer_pool_size"]
	_, hasConnections := vars["max_connections"]
	global, okGlobal := n.sum("innodb_buffer_pool_size", "innodb_log_buffer_size", "key_buffer_size", "aria_pagecache_buffer_size", "query_cache_size")
	tmp, okTmp := n.get("tmp_table_size")
	heap, okHeap := n.get("max_heap_table_size")
	perConn, okPer := n.sum("sort_buffer_size", "read_buffer_size", "read_rnd_buffer_size", "join_buffer_size", "thread_stack")
	connections, okConn := n.get("max_connections")
	binlog, okBinlog := n.get("binlog_cache_size")
	if !allTrue(hasPool, hasConnections, okGlobal, okTmp, okHeap, okPer, okConn) {
		return nil, false
	}
	if vars["log_bin"] == "ON" {
		if !okBinlog {
			return nil, false
		}
		perConn.Add(perConn, binlog)
	}
	global.Add(global, minBig(tmp, heap))
	return global.Add(global, perConn.Mul(perConn, connections)), true
}

// minBig returns the smaller of two numbers.
func minBig(a, b *big.Int) *big.Int {
	if a.Cmp(b) < 0 {
		return a
	}
	return b
}

func TestMemoryMax_Formula(t *testing.T) {
	for _, tc := range []struct {
		name string
		set  map[string]string
		ok   bool
	}{
		{"the captured values", nil, true},
		{"log_bin ON adds the binlog cache of every connection", map[string]string{"log_bin": "ON"}, true},
		{"log_bin OFF", map[string]string{"log_bin": "OFF"}, true},
		{"log_bin missing", map[string]string{"log_bin": absent}, true},
		{"optional global buffers missing", map[string]string{"aria_pagecache_buffer_size": absent, "query_cache_size": absent, "key_buffer_size": absent}, true},
		{"optional per-connection buffers missing", map[string]string{"read_rnd_buffer_size": absent, "join_buffer_size": absent, "binlog_cache_size": absent, "log_bin": "ON"}, true},
		{"both temporary table sizes missing", map[string]string{"tmp_table_size": absent, "max_heap_table_size": absent}, true},
		{"the smaller table size is tmp_table_size", map[string]string{"tmp_table_size": "1000", "max_heap_table_size": "5000"}, true},
		{"the smaller table size is max_heap_table_size", map[string]string{"tmp_table_size": "5000", "max_heap_table_size": "1000"}, true},
		{"buffer pool near 2^64", map[string]string{"innodb_buffer_pool_size": "18446744073709551615"}, true},
		{"several summands near 2^64", map[string]string{
			"innodb_buffer_pool_size": "18446744073709551615", "key_buffer_size": "18446744073709551615", "query_cache_size": "18446744073709551615",
		}, true},
		{"a per-connection buffer near 2^64", map[string]string{"sort_buffer_size": "18446744073709551615"}, true},
		{"every per-connection buffer near 2^64", map[string]string{
			"sort_buffer_size": "18446744073709551615", "read_buffer_size": "18446744073709551615", "join_buffer_size": "18446744073709551615",
			"thread_stack": "18446744073709551615", "max_connections": "1000",
		}, true},
		{"many connections", map[string]string{"max_connections": "4294967295"}, true},
		{"no connections", map[string]string{"max_connections": "0"}, true},
		{"buffer pool missing", map[string]string{"innodb_buffer_pool_size": absent}, false},
		{"max_connections missing", map[string]string{"max_connections": absent}, false},
		{"buffer pool not a number", map[string]string{"innodb_buffer_pool_size": "128M"}, false},
		{"max_connections negative", map[string]string{"max_connections": "-1"}, false},
		{"a summand with a fraction", map[string]string{"sort_buffer_size": "1.5"}, false},
		{"a summand empty", map[string]string{"key_buffer_size": ""}, false},
		{"a summand text", map[string]string{"thread_stack": "large"}, false},
		{"a table size text", map[string]string{"tmp_table_size": "large"}, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			vars := variablesWith(t, tc.set)
			want, wantOK := referenceMemoryMax(vars)
			if wantOK != tc.ok {
				t.Fatalf("the reference formula says ok = %v, the case says %v", wantOK, tc.ok)
			}

			hi, lo, ok := memoryMax(vars)

			if ok != tc.ok {
				t.Fatalf("memoryMax() ok = %v, want %v", ok, tc.ok)
			}
			if !ok {
				return
			}
			got := new(big.Int).Lsh(new(big.Int).SetUint64(hi), 64)
			got.Or(got, new(big.Int).SetUint64(lo))
			if got.Cmp(want) != 0 {
				t.Errorf("memoryMax() = %s (hi %d, lo %d), want %s", got, hi, lo, want)
			}
		})
	}
}

func TestMemoryMax_CapturedValuesGive898035712(t *testing.T) {
	vars := capturedVariables(t)

	hi, lo, ok := memoryMax(vars)

	if !ok || hi != 0 || lo != 898035712 {
		t.Errorf("memoryMax(captured) = %d, %d, %v, want 0, 898035712, true", hi, lo, ok)
	}
	vars["log_bin"] = "ON"
	binlog, err := strconv.ParseUint(vars["binlog_cache_size"], 10, 64)
	if err != nil || binlog != 32768 {
		t.Fatalf("binlog_cache_size = %q (%v), want 32768 as the plan states", vars["binlog_cache_size"], err)
	}
	if hi, lo, ok = memoryMax(vars); !ok || hi != 0 || lo != 898035712+151*32768 {
		t.Errorf("memoryMax(captured, log_bin ON) = %d, %d, %v, want 0, %d, true", hi, lo, ok, 898035712+151*32768)
	}
}

func TestBufferPoolUsed_Percentage(t *testing.T) {
	for _, tc := range []struct {
		name   string
		status map[string]uint64
		want   float64
		ok     bool
	}{
		{"a quarter free", map[string]uint64{"Innodb_buffer_pool_pages_total": 1000, "Innodb_buffer_pool_pages_free": 250}, 75, true},
		{"all free", map[string]uint64{"Innodb_buffer_pool_pages_total": 1000, "Innodb_buffer_pool_pages_free": 1000}, 0, true},
		{"none free", map[string]uint64{"Innodb_buffer_pool_pages_total": 8, "Innodb_buffer_pool_pages_free": 0}, 100, true},
		{"total zero", map[string]uint64{"Innodb_buffer_pool_pages_total": 0, "Innodb_buffer_pool_pages_free": 0}, 0, false},
		{"total missing", map[string]uint64{"Innodb_buffer_pool_pages_free": 5}, 0, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, ok := bufferPoolUsed(tc.status)

			if ok != tc.ok || (ok && !near(got, tc.want)) {
				t.Errorf("bufferPoolUsed(%v) = %v, %v, want %v, %v", tc.status, got, ok, tc.want, tc.ok)
			}
		})
	}
}

// listenerForm is a listener of AC11 and whether it counts as exposed.
type listenerForm struct {
	addr    string
	exposed bool
}

// listenerForms are the forms of the spec (E12), with a Tailscale address, private, link-local and public ones.
var listenerForms = []listenerForm{
	{"127.0.0.1", false}, {"127.1.2.3", false}, {"127.255.255.255", false}, {"::1", false}, {"::ffff:127.0.0.1", false}, {"::ffff:127.9.9.9", false},
	{"0.0.0.0", true}, {"::", true}, {"::127.0.0.1", true}, {"::ffff:0.0.0.0", true}, {"::ffff:10.1.2.3", true},
	{"203.0.113.7", true}, {"8.8.8.8", true}, {"2001:db8::1", true}, {"2606:4700::1111", true},
	{"100.64.5.6", true}, {"100.101.102.103", true}, {"fd7a:115c:a1e0::1", true},
	{"10.0.0.1", true}, {"192.168.1.5", true}, {"172.16.0.9", true}, {"169.254.1.1", true}, {"fe80::1", true}, {"126.255.255.255", true}, {"128.0.0.1", true},
}

func TestFixture_TableLinesReadBackAsWritten(t *testing.T) {
	for _, f := range listenerForms {
		t.Run(f.addr, func(t *testing.T) {
			want := netip.AddrPortFrom(netip.MustParseAddr(f.addr), 3306)
			var got []netip.AddrPort

			err := connstats.TCPListeners(listenerTables(want.String()), func(local netip.AddrPort) { got = append(got, local) })

			if err != nil || len(got) != 1 || got[0].Port() != 3306 || got[0].Addr().Unmap() != want.Addr().Unmap() {
				t.Errorf("the connection reader read %v, %v from the fixture of %v, want that listener back", got, err, want)
			}
		})
	}
}

func TestExposedListeners_Forms(t *testing.T) {
	for _, f := range listenerForms {
		t.Run(f.addr, func(t *testing.T) {
			addr := netip.MustParseAddr(f.addr)
			if got := exposed(addr); got != f.exposed {
				t.Errorf("exposed(%s) = %v, want %v", f.addr, got, f.exposed)
			}
			table := listenerTables(netip.AddrPortFrom(addr, 3306).String())
			want := 0
			if f.exposed {
				want = 1
			}

			got, err := countExposed(table, []uint16{3306})

			if err != nil || got != want {
				t.Errorf("countExposed() for a listener on %s = %d, %v, want %d", f.addr, got, err, want)
			}
		})
	}
}

func TestExposedListeners_CountsPortsAndExtraPort(t *testing.T) {
	tcp := tableFile(
		socketLine(0, netip.MustParseAddrPort("127.0.0.1:3306"), "0A"),
		socketLine(1, netip.MustParseAddrPort("127.1.2.3:3306"), "0A"),
		socketLine(2, netip.MustParseAddrPort("0.0.0.0:3306"), "0A"),
		socketLine(3, netip.MustParseAddrPort("203.0.113.7:3306"), "0A"),
		socketLine(4, netip.MustParseAddrPort("100.64.5.6:3306"), "0A"),
		socketLine(5, netip.MustParseAddrPort("10.0.0.1:3307"), "0A"),
		socketLine(6, netip.MustParseAddrPort("0.0.0.0:8080"), "0A"),
		socketLine(7, netip.MustParseAddrPort("127.0.0.1:3307"), "0A"),
		socketLine(8, netip.MustParseAddrPort("0.0.0.0:3306"), "01"),
		socketLine(9, netip.MustParseAddrPort("0.0.0.0:3307"), "06"),
	)
	tcp6 := tableFile(
		socketLine(0, netip.MustParseAddrPort("[::1]:3306"), "0A"),
		socketLine(1, netip.MustParseAddrPort("[::ffff:127.0.0.1]:3306"), "0A"),
		socketLine(2, netip.MustParseAddrPort("[::]:3306"), "0A"),
		socketLine(3, netip.MustParseAddrPort("[::127.0.0.1]:3306"), "0A"),
		socketLine(4, netip.MustParseAddrPort("[2001:db8::1]:3306"), "0A"),
		socketLine(5, netip.MustParseAddrPort("[fe80::1]:3306"), "0A"),
		socketLine(6, netip.MustParseAddrPort("[::]:3307"), "0A"),
		socketLine(7, netip.MustParseAddrPort("[::]:9999"), "0A"),
		socketLine(8, netip.MustParseAddrPort("[::]:3306"), "01"),
	)
	proc := fstest.MapFS{"net/tcp": tcp, "net/tcp6": tcp6}

	for _, tc := range []struct {
		name  string
		ports []uint16
		want  int
	}{
		{"port only", []uint16{3306}, 3 + 4},
		{"port and extra port", []uint16{3306, 3307}, 3 + 4 + 2},
		{"extra port only", []uint16{3307}, 2},
		{"a port nobody listens on", []uint16{4000}, 0},
		{"no port", nil, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, err := countExposed(proc, tc.ports)

			if err != nil || got != tc.want {
				t.Errorf("countExposed(%v) = %d, %v, want %d: listening sockets of tcp and tcp6 that are not loopback", tc.ports, got, err, tc.want)
			}
		})
	}
}

func TestExposedListeners_Tables(t *testing.T) {
	listening := socketLine(0, netip.MustParseAddrPort("0.0.0.0:3306"), "0A")
	t.Run("tcp alone without a tcp6 table", func(t *testing.T) {
		got, err := countExposed(fstest.MapFS{"net/tcp": tableFile(listening)}, []uint16{3306})

		if err != nil || got != 1 {
			t.Errorf("countExposed() = %d, %v, want 1 and no error", got, err)
		}
	})
	t.Run("missing tcp is an error", func(t *testing.T) {
		if _, err := countExposed(fstest.MapFS{"net/tcp6": tableFile()}, []uint16{3306}); err == nil {
			t.Errorf("countExposed() without net/tcp = nil, want an error")
		}
	})
	t.Run("an unreadable line is an error", func(t *testing.T) {
		if _, err := countExposed(fstest.MapFS{"net/tcp": tableFile(listening, "this is not a socket")}, []uint16{3306}); err == nil {
			t.Errorf("countExposed() with an unreadable line = nil, want an error")
		}
	})
	t.Run("an unreadable line of tcp6 is an error", func(t *testing.T) {
		proc := fstest.MapFS{"net/tcp": tableFile(listening), "net/tcp6": tableFile("this is not a socket")}
		if _, err := countExposed(proc, []uint16{3306}); err == nil {
			t.Errorf("countExposed() with an unreadable line in tcp6 = nil, want an error")
		}
	})
}

// samplesAt returns two samples with the given counters, the second one after the interval.
func samplesAt(interval time.Duration, prev, cur map[string]uint64) (*sample, *sample) {
	start := time.Date(2026, 10, 21, 12, 0, 0, 0, time.UTC)
	return &sample{status: prev, run: start}, &sample{status: cur, run: start.Add(interval)}
}

// counters builds the status values the rates read.
func counters(uptime, questions, reads, requests uint64) map[string]uint64 {
	return map[string]uint64{
		"Uptime": uptime, "Questions": questions, "Innodb_buffer_pool_reads": reads, "Innodb_buffer_pool_read_requests": requests,
		"Innodb_buffer_pool_pages_total": 1000, "Innodb_buffer_pool_pages_free": 400,
	}
}

// ratesByName returns the points of rates by name.
func ratesByName(t *testing.T, points []model.MetricPoint) map[string]model.MetricPoint {
	t.Helper()
	out := map[string]model.MetricPoint{}
	for _, p := range points {
		if _, dup := out[p.Name]; dup {
			t.Errorf("rates() returned %s twice", p.Name)
		}
		out[p.Name] = p
	}
	return out
}

func TestRates_ValuesOfTheFormulas(t *testing.T) {
	prev, cur := samplesAt(30*time.Second, counters(1000, 1000, 100, 10000), counters(1030, 1600, 130, 10400))

	got := ratesByName(t, rates(prev, cur))

	if q, ok := got[metricQuestions]; !ok || !near(q.Value, 20) || q.Unit != "per_second" {
		t.Errorf("%s = %+v (%v), want 20 per_second: 600 questions in 30 seconds", metricQuestions, q, ok)
	}
	if h, ok := got[metricPoolHit]; !ok || !near(h.Value, 92.5) || h.Unit != "percent" {
		t.Errorf("%s = %+v (%v), want 92.5 percent: 100 x (1 - 30 / 400)", metricPoolHit, h, ok)
	}
}

func TestRates_LeftOut(t *testing.T) {
	good := counters(1030, 1600, 130, 10400)
	for _, tc := range []struct {
		name      string
		interval  time.Duration
		prev, cur map[string]uint64
		qps, hit  want
	}{
		{"questions fell", 30 * time.Second, counters(1000, 1700, 100, 10000), good, none, anything},
		{"reads fell", 30 * time.Second, counters(1000, 1000, 131, 10000), good, anything, none},
		{"read requests fell", 30 * time.Second, counters(1000, 1000, 100, 10500), good, anything, none},
		{"no read requests in between", 30 * time.Second, counters(1000, 1000, 100, 10400), good, anything, none},
		{"more reads than read requests", 30 * time.Second, counters(1000, 1000, 100, 10380), good, anything, none},
		{"no time between the samples", 0, counters(1000, 1000, 100, 10000), good, none, anything},
		{"the second sample is earlier", -30 * time.Second, counters(1000, 1000, 100, 10000), good, none, anything},
	} {
		t.Run(tc.name, func(t *testing.T) {
			prev, cur := samplesAt(tc.interval, tc.prev, tc.cur)

			got := ratesByName(t, rates(prev, cur))

			tc.qps.check(t, tc.name, metricQuestions, got)
			tc.hit.check(t, tc.name, metricPoolHit, got)
		})
	}
	t.Run("no previous sample", func(t *testing.T) {
		_, cur := samplesAt(30*time.Second, nil, good)

		if got := rates(nil, cur); len(got) != 0 {
			t.Errorf("rates(nil, cur) = %v, want none", got)
		}
	})
}

// want says what a test expects of a metric of a run: left out, a value, or no opinion.
type want struct {
	kind int
	v    float64
}

var (
	none     = want{kind: 0}
	anything = want{kind: 2}
)

// val expects a value.
func val(v float64) want { return want{kind: 1, v: v} }

// check fails unless the metric of the run is as w says.
func (w want) check(t *testing.T, step, name string, m map[string]model.MetricPoint) {
	t.Helper()
	p, ok := m[name]
	switch {
	case w.kind == 0 && ok:
		t.Errorf("%s: %s = %v, want it left out", step, name, p.Value)
	case w.kind == 1 && (!ok || !near(p.Value, w.v)):
		t.Errorf("%s: %s = %v (%v), want %v", step, name, p.Value, ok, w.v)
	}
}

// rateStep is one run of a sequence: the counters the server reports and what the run must write and log.
type rateStep struct {
	name                  string
	after                 time.Duration // since the start of the previous run
	uptime, q, reads, req uint64
	total, free           uint64
	down, lost            bool
	qps, hit, used        want
	logs                  []wantLog
	restartedAt           uint64 // when set, the line "mariadb restarted" with this uptime is expected
	answeringAgain        bool
}

// statusRows returns the status rows of a step.
func (s rateStep) statusRows() map[string]string {
	return map[string]string{
		"Uptime": fmt.Sprint(s.uptime), "Questions": fmt.Sprint(s.q), "Innodb_buffer_pool_reads": fmt.Sprint(s.reads),
		"Innodb_buffer_pool_read_requests": fmt.Sprint(s.req), "Innodb_buffer_pool_pages_total": fmt.Sprint(s.total),
		"Innodb_buffer_pool_pages_free": fmt.Sprint(s.free),
	}
}

// wantLogs returns the lines the step must log.
func (s rateStep) wantLogs() []wantLog {
	out := slices.Clone(s.logs)
	if s.restartedAt != 0 {
		out = append(out, warn(msgRestarted, map[string]string{"uptime_seconds": strconv.FormatUint(s.restartedAt, 10)}))
	}
	if s.answeringAgain {
		out = append(out, info(msgAgain))
	}
	return out
}

// runSteps runs the steps on a rig and checks each one.
func runSteps(t *testing.T, steps []rateStep) {
	t.Helper()
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		for i, s := range steps {
			if i > 0 {
				time.Sleep(s.after)
			}
			r.dialErr = nil
			if s.down {
				r.dialErr = opError(syscall.ENOENT)
			}
			r.setStatus(s.statusRows())

			recs, err := r.collectRun(scheduler.Run{Start: time.Now().UTC(), PreviousLost: s.lost})

			if err != nil {
				t.Fatalf("%s: Collect() = %v, want a record", s.name, err)
			}
			if s.down {
				wantOnlyStatus(t, recs, model.MariaDBDown, 0)
			} else {
				m := metricsOf(t, recs)
				s.qps.check(t, s.name, metricQuestions, m)
				s.hit.check(t, s.name, metricPoolHit, m)
				s.used.check(t, s.name, metricPoolUsed, m)
			}
			checkLogs(t, s.name, r.logs.take(), s.wantLogs()...)
		}
	})
}

func TestRates_Sequence(t *testing.T) {
	const sec = time.Second
	steps := []rateStep{
		{name: "first sample", uptime: 1000, q: 1000, reads: 100, req: 10000, total: 1000, free: 400, qps: none, hit: none, used: val(60)},
		{name: "second sample", after: 30 * sec, uptime: 1030, q: 1600, reads: 130, req: 10400, total: 1000, free: 300, qps: val(20), hit: val(92.5), used: val(70)},
		{name: "database down", after: 30 * sec, down: true, logs: []wantLog{warn(msgDown, nil)}},
		{name: "up after a down sample", after: 30 * sec, uptime: 1090, q: 2200, reads: 140, req: 10800, total: 1000, free: 250, qps: none, hit: none, used: val(75), answeringAgain: true},
		{name: "next sample", after: 30 * sec, uptime: 1120, q: 2800, reads: 150, req: 11200, total: 1000, free: 250, qps: val(20), hit: val(97.5), used: val(75)},
		{name: "restart", after: 30 * sec, uptime: 24, q: 3400, reads: 160, req: 11600, total: 1000, free: 200, qps: none, hit: none, used: val(80), restartedAt: 24},
		{name: "after the restart", after: 30 * sec, uptime: 54, q: 4000, reads: 170, req: 12000, total: 1000, free: 200, qps: val(20), hit: val(97.5), used: val(80)},
		{name: "start moved by 5 seconds", after: 30 * sec, uptime: 79, q: 4600, reads: 180, req: 12400, total: 1000, free: 200, qps: val(20), hit: val(97.5), used: val(80)},
		{name: "start moved by 6 seconds", after: 30 * sec, uptime: 103, q: 5200, reads: 190, req: 12800, total: 1000, free: 200, qps: none, hit: none, used: val(80), restartedAt: 103},
		{name: "steady again", after: 30 * sec, uptime: 133, q: 5800, reads: 200, req: 13200, total: 1000, free: 200, qps: val(20), hit: val(97.5), used: val(80)},
		{name: "questions fell", after: 30 * sec, uptime: 163, q: 100, reads: 210, req: 13600, total: 1000, free: 200, qps: none, hit: anything, used: val(80)},
		{name: "after a fall", after: 30 * sec, uptime: 193, q: 700, reads: 220, req: 14000, total: 1000, free: 200, qps: val(20), hit: val(97.5), used: val(80)},
		{name: "reads fell", after: 30 * sec, uptime: 223, q: 1300, reads: 5, req: 14400, total: 1000, free: 200, qps: anything, hit: none, used: val(80)},
		{name: "no read requests in between", after: 30 * sec, uptime: 253, q: 1900, reads: 5, req: 14400, total: 1000, free: 200, qps: anything, hit: none, used: val(80)},
		{name: "more reads than requests", after: 30 * sec, uptime: 283, q: 2500, reads: 55, req: 14420, total: 1000, free: 200, qps: anything, hit: none, used: val(80)},
		{name: "no pages", after: 30 * sec, uptime: 313, q: 3100, reads: 56, req: 14820, total: 0, free: 0, qps: val(20), hit: val(99.75), used: none},
		{name: "previous sample lost", after: 30 * sec, lost: true, uptime: 343, q: 3700, reads: 57, req: 15220, total: 1000, free: 500, qps: none, hit: none, used: val(50)},
		{name: "after the lost sample", after: 30 * sec, uptime: 373, q: 4300, reads: 58, req: 15620, total: 1000, free: 500, qps: val(20), hit: val(99.75), used: val(50)},
		{name: "no time between the runs", after: 0, uptime: 373, q: 4300, reads: 58, req: 15620, total: 1000, free: 500, qps: none, hit: anything, used: val(50)},
	}

	runSteps(t, steps)
}

func TestRates_RestartAcrossADownSample(t *testing.T) {
	const sec = time.Second
	runSteps(t, []rateStep{
		{name: "first sample", uptime: 1000, q: 1000, reads: 100, req: 10000, total: 1000, free: 400, qps: none, hit: none, used: val(60)},
		{name: "down", after: 60 * sec, down: true, logs: []wantLog{warn(msgDown, nil)}},
		{name: "up with a young server", after: 60 * sec, uptime: 10, q: 50, reads: 5, req: 500, total: 1000, free: 900, qps: none, hit: none, used: val(10), answeringAgain: true, restartedAt: 10},
	})
}

func TestRates_StartOfTheServerIsRunStartMinusUptime(t *testing.T) {
	const sec = time.Second
	runSteps(t, []rateStep{
		{name: "first sample", uptime: 5000, q: 1000, reads: 100, req: 10000, total: 1000, free: 400, qps: none, hit: none, used: val(60)},
		{name: "the server started 4 seconds later by its own clock", after: 60 * sec, uptime: 5056, q: 2200, reads: 130, req: 10400, total: 1000, free: 400, qps: val(20), hit: val(92.5), used: val(60)},
		{name: "and 6 seconds later again", after: 60 * sec, uptime: 5110, q: 3400, reads: 140, req: 10800, total: 1000, free: 400, qps: none, hit: none, used: val(60), restartedAt: 5110},
	})
}
