package spool

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"math"
	"math/rand/v2"
	"net/netip"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// The reference stream is the estimate of one server's records that the spool's retention is sized on (spec,
// *Non-functional requirements*): per day 2,880 system samples of 110 metrics, 1,440 filesystem samples of 7 metrics,
// 1,440 clock samples of 3 and 1,440 self samples of 5, 1,440 process snapshots of 50 processes and 100 programs,
// 1,440 connection snapshots of 30 listeners, 40 owning processes and 25 remotes, 2,880 MariaDB status records, 400
// service states and 200,000 log lines. A scale of 80 divides every daily count of runs by 80. Every record is drawn
// from a generator with a fixed seed, so a run is reproducible and no record repeats verbatim.
const (
	refSeedHigh       = 38
	refSeedLow        = 2026
	refLinesPerRun    = 50
	refSystemMetrics  = 110
	refFSMetrics      = 7
	refClockMetrics   = 3
	refSelfMetrics    = 5
	refProcesses      = 50
	refPrograms       = 100
	refListeners      = 30
	refConnProcesses  = 40
	refRemotes        = 25
	refFullScale      = 1
	refScaledDivisor  = 80
	refDays           = 7
	refLongTestsEnv   = "VANDOX_LONG_TESTS"
	secondsPerDay     = 86400
	refDefaultMaxSize = int64(2) << 30
)

type refKind int

const (
	refSystem refKind = iota
	refFilesystem
	refClock
	refSelf
	refProcess
	refConnection
	refMariaDB
	refService
	refLogs
	refKinds
)

// refRuns returns the runs per day of each kind at the given divisor.
func refRuns(divisor int) [refKinds]int {
	return [refKinds]int{
		refSystem: 2880 / divisor, refFilesystem: 1440 / divisor, refClock: 1440 / divisor, refSelf: 1440 / divisor,
		refProcess: 1440 / divisor, refConnection: 1440 / divisor, refMariaDB: 2880 / divisor, refService: 400 / divisor,
		refLogs: 200000 / refLinesPerRun / divisor,
	}
}

// refEvent is one collector run of the stream.
type refEvent struct {
	at   time.Time
	kind refKind
}

// refStream generates the reference stream.
type refStream struct {
	rng     *rand.Rand
	divisor int
	lines   []string
	hosts   []string
	progs   []string
}

func newRefStream(t *testing.T, divisor int) *refStream {
	t.Helper()

	g := &refStream{rng: rand.New(rand.NewPCG(refSeedHigh, refSeedLow)), divisor: divisor}
	g.loadShapes(t)

	return g
}

// loadShapes reads the line shapes of testdata/logs and testdata/journal.
func (g *refStream) loadShapes(t *testing.T) {
	t.Helper()

	files, err := filepath.Glob(filepath.Join("..", "..", "testdata", "logs", "*.log"))
	if err != nil || len(files) == 0 {
		t.Fatalf("Glob(testdata/logs/*.log) = %v, %v, want the fixture files", files, err)
	}
	for _, f := range files {
		g.addLines(t, f, func(line string) string { return line })
	}
	g.addLines(t, filepath.Join("..", "..", "testdata", "journal", "live-records.jsonl"), func(line string) string {
		var rec struct {
			Host    string `json:"host"`
			Program string `json:"program"`
		}
		if json.Unmarshal([]byte(line), &rec) == nil {
			g.hosts = append(g.hosts, rec.Host)
			g.progs = append(g.progs, rec.Program)
		}

		return ""
	})
	if len(g.lines) < 20 || len(g.hosts) == 0 {
		t.Fatalf("line shapes: %d lines, %d hosts, want the fixtures' content", len(g.lines), len(g.hosts))
	}
}

func (g *refStream) addLines(t *testing.T, path string, take func(string) string) {
	t.Helper()

	f, err := os.Open(path)
	if err != nil {
		t.Fatalf("Open(%s) = %v", path, err)
	}
	defer func() { _ = f.Close() }()
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64<<10), 1<<20)
	for sc.Scan() {
		if s := take(sc.Text()); strings.TrimSpace(s) != "" && utf8.ValidString(s) {
			g.lines = append(g.lines, s)
		}
	}
	if err := sc.Err(); err != nil {
		t.Fatalf("Scan(%s) = %v", path, err)
	}
}

// day calls emit with the records of each collector run of the day, in time order.
func (g *refStream) day(day int, emit func(run []model.Record)) {
	start := epoch.Add(time.Duration(day) * 24 * time.Hour)
	runs := refRuns(g.divisor)
	var events []refEvent
	for kind := range refKinds {
		n := runs[kind]
		step := time.Duration(secondsPerDay) * time.Second / time.Duration(n)
		for i := range n {
			jitter := time.Duration(g.rng.Int64N(int64(step / 2)))
			events = append(events, refEvent{at: start.Add(time.Duration(i)*step + jitter), kind: kind})
		}
	}
	slices.SortStableFunc(events, func(a, b refEvent) int {
		if c := a.at.Compare(b.at); c != 0 {
			return c
		}

		return int(a.kind) - int(b.kind)
	})
	for _, e := range events {
		emit(g.run(e))
	}
}

// run builds the records of one collector run.
func (g *refStream) run(e refEvent) []model.Record {
	switch e.kind {
	case refSystem:
		return g.metrics("system", "node_system", refSystemMetrics, e.at)
	case refFilesystem:
		return g.metrics("filesystem", "node_filesystem", refFSMetrics, e.at)
	case refClock:
		return g.metrics("clock", "node_clock", refClockMetrics, e.at)
	case refSelf:
		return g.metrics("self", "agent_self", refSelfMetrics, e.at)
	case refProcess:
		return []model.Record{g.processSnapshot(e.at)}
	case refConnection:
		return []model.Record{g.connectionSnapshot(e.at)}
	case refMariaDB:
		return []model.Record{g.mariadbStatus(e.at)}
	case refService:
		return []model.Record{g.serviceState(e.at)}
	default:
		return g.logLines(e.at)
	}
}

func agentMeta(source string, at time.Time) model.Meta {
	return model.Meta{Origin: model.OriginAgent, Source: source, CapturedAt: at}
}

func (g *refStream) metrics(source, prefix string, n int, at time.Time) []model.Record {
	out := make([]model.Record, 0, n)
	for i := range n {
		labels := map[string]string{"cpu": strconv.Itoa(i % 8), "mode": []string{"user", "system", "idle", "iowait"}[i%4]}
		out = append(out, model.Record{
			Meta: agentMeta(source, at.Add(time.Duration(g.rng.IntN(1000))*time.Millisecond)),
			Data: &model.MetricPoint{
				Name:   fmt.Sprintf("%s_%03d_total", prefix, i),
				Value:  g.rng.Float64() * 1e9,
				Unit:   []string{"seconds", "bytes", "ratio"}[i%3],
				Labels: labels,
			},
		})
	}

	return out
}

const processStates = "SRDZTIDSRS"

// toByte, toU16 and toI32 convert a drawn number that is in range by construction.
func toByte(n int) byte {
	if n < 0 || n > math.MaxUint8 {
		return 0
	}

	return byte(n)
}

func toU16(n int) uint16 {
	if n < 0 || n > math.MaxUint16 {
		return 0
	}

	return uint16(n)
}

func toI32(n int) int32 {
	if n < math.MinInt32 || n > math.MaxInt32 {
		return 0
	}

	return int32(n)
}

var refWords = []string{"php-fpm:", "pool", "www", "--config", "/etc/plesk/php.ini", "-c", "/var/www/vhosts", "httpdocs", "index.php",
	"--max-children=32", "nginx:", "worker", "process", "mysqld", "--defaults-file=/etc/my.cnf", "--socket=/run/mysqld.sock", "postfix/smtpd", "-n", "smtp"}

// text returns printable text of exactly n bytes drawn from the word list.
func (g *refStream) text(n int) string {
	var b strings.Builder
	for b.Len() < n {
		b.WriteString(refWords[g.rng.IntN(len(refWords))])
		b.WriteByte(' ')
		b.WriteString(strconv.Itoa(g.rng.IntN(100000)))
		b.WriteByte(' ')
	}

	return strings.TrimSpace(b.String()[:n]) + strings.Repeat("x", n-len(strings.TrimSpace(b.String()[:n])))
}

func (g *refStream) processSnapshot(at time.Time) model.Record {
	procs := make([]model.ProcessSample, refProcesses)
	for i := range procs {
		rss := g.rng.Uint64N(1 << 32)
		procs[i] = model.ProcessSample{
			PID: toI32(100 + i*13 + g.rng.IntN(13)), PPID: 1, User: []string{"root", "psaadm", "mysql", "www-data"}[g.rng.IntN(4)],
			Command: []string{"php-fpm", "nginx", "mysqld", "smtpd", "dovecot"}[g.rng.IntN(5)], Cmdline: g.text(60 + g.rng.IntN(141)),
			State: processStates[g.rng.IntN(len(processStates)):][:1], StartedAt: at.Add(-time.Duration(g.rng.Int64N(int64(30 * 24 * time.Hour)))),
			CPUPercent: g.rng.Float64() * 100, RSSBytes: rss,
		}
	}
	progs := make([]model.ProgramAggregate, refPrograms)
	for i := range progs {
		progs[i] = model.ProgramAggregate{
			Program: fmt.Sprintf("program-%03d", i), Count: 1 + g.rng.Uint32N(20), CPUPercent: g.rng.Float64() * 100, RSSBytes: g.rng.Uint64N(1 << 33),
		}
	}
	total := refProcesses + g.rng.Uint32N(500)

	return model.Record{Meta: agentMeta("processes", at), Data: &model.ProcessSnapshot{Complete: true, Total: &total, Processes: procs, Programs: progs}}
}

func (g *refStream) addr() netip.Addr {
	return netip.AddrFrom4([4]byte{toByte(1 + g.rng.IntN(222)), toByte(g.rng.IntN(256)), toByte(g.rng.IntN(256)), toByte(1 + g.rng.IntN(254))})
}

func (g *refStream) connectionSnapshot(at time.Time) model.Record {
	listeners := make([]model.Listener, refListeners)
	for i := range listeners {
		listeners[i] = model.Listener{
			Proto: model.ProtoTCP, Local: netip.AddrPortFrom(netip.AddrFrom4([4]byte{0, 0, 0, 0}), toU16(1024+g.rng.IntN(60000))),
			PID: toI32(100 + g.rng.IntN(30000)), Command: []string{"nginx", "mysqld", "sshd", "dovecot"}[g.rng.IntN(4)],
		}
	}
	owners := make([]model.ProcessConnections, refConnProcesses)
	for i := range owners {
		owners[i] = model.ProcessConnections{PID: toI32(100 + i*7 + g.rng.IntN(7)), Command: fmt.Sprintf("owner-%02d", i), Count: 1 + g.rng.Uint32N(500)}
	}
	remotes := make([]model.RemoteCount, refRemotes)
	for i := range remotes {
		remotes[i] = model.RemoteCount{Addr: g.addr(), Count: 1 + g.rng.Uint32N(300)}
	}
	states := []model.StateCount{
		{Proto: model.ProtoTCP, State: "ESTABLISHED", Count: 1 + g.rng.Uint32N(900)},
		{Proto: model.ProtoTCP, State: "TIME_WAIT", Count: 1 + g.rng.Uint32N(900)},
		{Proto: model.ProtoTCP, State: "LISTEN", Count: refListeners},
	}

	return model.Record{Meta: agentMeta("connections", at), Data: &model.ConnectionSnapshot{
		Complete: true, States: states, Processes: owners, Remotes: remotes, Listeners: listeners,
	}}
}

func (g *refStream) mariadbStatus(at time.Time) model.Record {
	status := make(map[string]uint64, 60)
	for i := range 60 {
		status[fmt.Sprintf("Com_stat_%03d", i)] = g.rng.Uint64N(1 << 40)
	}
	vars := make(map[string]string, 15)
	for i := range 15 {
		vars[fmt.Sprintf("variable_%02d", i)] = strconv.Itoa(g.rng.IntN(1 << 30))
	}
	threads := make([]model.MariaDBThread, 8)
	for i := range threads {
		threads[i] = model.MariaDBThread{
			ID: g.rng.Uint64N(1 << 30), User: "psa", Host: "localhost", DB: "psa", Command: "Query", TimeSeconds: g.rng.Uint64N(100),
			State: "executing", Info: g.text(40 + g.rng.IntN(120)),
		}
	}
	ping := time.Duration(g.rng.Int64N(int64(50 * time.Millisecond)))

	return model.Record{Meta: agentMeta("mariadb", at), Data: &model.MariaDBStatus{
		Availability: model.MariaDBUp, PingLatency: &ping, Status: status, Variables: vars, Threads: threads, Complete: true,
	}}
}

func (g *refStream) serviceState(at time.Time) model.Record {
	return model.Record{Meta: agentMeta("services", at), Data: &model.ServiceState{
		Unit: fmt.Sprintf("svc-%03d.service", g.rng.IntN(400)), LoadState: "loaded", ActiveState: "active", SubState: "running",
		ActiveEnterAt: at.Add(-time.Duration(g.rng.Int64N(int64(48 * time.Hour)))), Restarts: g.rng.Uint32N(50),
	}}
}

// logMessage returns a line shaped like a line of the fixtures, with every digit drawn anew and 40 to 400 bytes long.
func (g *refStream) logMessage() string {
	shape := []byte(g.lines[g.rng.IntN(len(g.lines))])
	for i, c := range shape {
		if c >= '0' && c <= '9' {
			shape[i] = "0123456789"[g.rng.IntN(10)]
		}
	}
	n := 40 + g.rng.IntN(361)
	s := string(shape)
	for len(s) > n || !utf8.ValidString(s[:min(len(s), n)]) {
		if len(s) <= n {
			break
		}
		s = s[:len(s)-1]
	}
	if len(s) < n {
		s += " " + g.text(n-len(s)-1)
	}

	return s
}

func (g *refStream) logLines(at time.Time) []model.Record {
	out := make([]model.Record, 0, refLinesPerRun)
	logs := []string{"mail.log", "nginx-error.log", "apache-error.log", "mariadb-error.log", "dovecot.log", "plesk-panel.log", "journal"}
	for range refLinesPerRun {
		prio := toByte(g.rng.IntN(8))
		out = append(out, model.Record{
			Meta: agentMeta("logs", at.Add(time.Duration(g.rng.IntN(1000))*time.Millisecond)),
			Data: &model.LogLine{
				Log: logs[g.rng.IntN(len(logs))], Host: g.hosts[g.rng.IntN(len(g.hosts))], Program: g.progs[g.rng.IntN(len(g.progs))],
				PID: g.rng.Int32N(32768), Priority: &prio, Message: g.logMessage(),
			},
		})
	}

	return out
}

// refSig is a cheap fingerprint of a record's content, without its sequence number, to compare what was handed over
// with what is read back.
func refSig(r *model.Record) uint64 {
	h := uint64(14695981039346656037)
	mix := func(s string) {
		for i := range len(s) {
			h = (h ^ uint64(s[i])) * 1099511628211
		}
		h = (h ^ 0xff) * 1099511628211
	}
	mix(string(r.Kind()))
	mix(r.Source)
	mix(strconv.FormatInt(r.CapturedAt.UnixNano(), 10))
	switch d := r.Data.(type) {
	case *model.MetricPoint:
		mix(d.Name)
		mix(strconv.FormatFloat(d.Value, 'g', -1, 64))
	case *model.LogLine:
		mix(d.Message)
		mix(d.Program)
	case *model.ProcessSnapshot:
		mix(strconv.Itoa(len(d.Processes)) + "/" + strconv.Itoa(len(d.Programs)))
		mix(d.Processes[len(d.Processes)-1].Cmdline)
	case *model.ConnectionSnapshot:
		mix(d.Listeners[0].Local.String() + d.Remotes[0].Addr.String())
	case *model.MariaDBStatus:
		mix(strconv.Itoa(len(d.Status)) + d.Threads[0].Info)
	case *model.ServiceState:
		mix(d.Unit + strconv.Itoa(int(d.Restarts)))
	}

	return h
}

// encodedBytes returns the size of the uncompressed JSON lines of run, newline included.
func encodedBytes(t *testing.T, run []model.Record) int64 {
	t.Helper()

	var sum int64
	for _, r := range run {
		r.Seq = 1
		line, err := wire.MarshalRecord(&r)
		if err != nil {
			t.Fatalf("MarshalRecord(%s from %s) = %v, want a valid reference record", r.Kind(), r.Source, err)
		}
		sum += int64(len(line)) + 1
	}

	return sum
}

// TestReferenceStream_Shape checks the generator the outage tests rely on: the counts per kind, the fixed seed, that no
// record repeats and that every record is valid. It cannot fail on the stubs of the spool; it guards the test data.
func TestReferenceStream_Shape(t *testing.T) {
	counts := map[model.Kind]int{}
	var first []uint64
	sigs := map[uint64]struct{}{}
	var bytes int64
	g := newRefStream(t, refScaledDivisor)
	g.day(0, func(run []model.Record) {
		bytes += encodedBytes(t, run)
		for i := range run {
			counts[run[i].Kind()]++
			sig := refSig(&run[i])
			if _, dup := sigs[sig]; dup {
				t.Fatalf("record %d of kind %s repeats an earlier record's content", len(first), run[i].Kind())
			}
			sigs[sig] = struct{}{}
			first = append(first, sig)
		}
	})
	runs := refRuns(refScaledDivisor)
	want := map[model.Kind]int{
		model.KindMetric:             runs[refSystem]*refSystemMetrics + runs[refFilesystem]*refFSMetrics + runs[refClock]*refClockMetrics + runs[refSelf]*refSelfMetrics,
		model.KindProcessSnapshot:    runs[refProcess],
		model.KindConnectionSnapshot: runs[refConnection],
		model.KindMariaDBStatus:      runs[refMariaDB],
		model.KindServiceState:       runs[refService],
		model.KindLogLine:            runs[refLogs] * refLinesPerRun,
	}
	for kind, n := range want {
		if counts[kind] != n {
			t.Errorf("reference stream day at 1/%d has %d records of kind %s, want %d", refScaledDivisor, counts[kind], kind, n)
		}
	}
	if want[model.KindLogLine] != 200000/refScaledDivisor {
		t.Errorf("log lines per scaled day = %d, want %d", want[model.KindLogLine], 200000/refScaledDivisor)
	}

	var again []uint64
	newRefStream(t, refScaledDivisor).day(0, func(run []model.Record) {
		for i := range run {
			again = append(again, refSig(&run[i]))
		}
	})
	if !slices.Equal(first, again) {
		t.Errorf("a second generator with the same seed makes a different stream")
	}
	t.Logf("reference stream at 1/%d: %d records, %d encoded bytes per day, about %d bytes per day at full scale",
		refScaledDivisor, len(first), bytes, bytes*refScaledDivisor)
}

// TestReferenceStream_FullScaleDay measures the encoded size of one full-scale day of the reference stream (fact 16 of
// the plan); it needs no spool and runs with VANDOX_LONG_TESTS=1.
func TestReferenceStream_FullScaleDay(t *testing.T) {
	if os.Getenv(refLongTestsEnv) != "1" {
		t.Skip("set VANDOX_LONG_TESTS=1 to measure a full-scale day of the reference stream")
	}
	var bytes int64
	var records int
	newRefStream(t, refFullScale).day(0, func(run []model.Record) {
		bytes += encodedBytes(t, run)
		records += len(run)
	})
	t.Logf("reference stream at full scale: %d records, %d encoded bytes per day (%.1f MB), 7 days %.2f GB",
		records, bytes, float64(bytes)/1e6, float64(bytes)*refDays/1e9)
	if limit := refDefaultMaxSize / refDays; bytes >= limit {
		t.Errorf("one day of the reference stream is %d bytes, 7 days of it do not fit the default cap of %d bytes", bytes, refDefaultMaxSize)
	}
}

// The backfill's batch and bandwidth bounds (plan, fact 16): a batch holds at most 1 MiB of record lines and 20,000
// records, and the backfill sends 256 KiB of compressed bodies per second.
const (
	backfillBatchBytes   = 1 << 20
	backfillBatchRecords = 20000
	backfillBandwidth    = 256 << 10
	backfillMinRate      = 1000
	backfillMaxRate      = 8500
	// backfillHeaderRoom is what the header line takes of a batch's byte bound.
	backfillHeaderRoom = 512
	refBackfillBoot    = "0f0f0f0f-1111-4222-8333-444455556666"
)

// refBatch is a batch of the backfill: its records and the bytes of their lines.
type refBatch struct {
	records []model.Record
	lines   int64
}

// refBatches cuts one scaled day of the reference stream into batches of the backfill's bounds.
func refBatches(t *testing.T) []refBatch {
	t.Helper()

	var batches []refBatch
	var current refBatch
	var seq uint64
	newRefStream(t, refScaledDivisor).day(0, func(run []model.Record) {
		for _, r := range run {
			seq++
			r.Seq = seq
			line, err := wire.MarshalRecord(&r)
			if err != nil {
				t.Fatalf("MarshalRecord(%s from %s) = %v, want a valid reference record", r.Kind(), r.Source, err)
			}
			size := int64(len(line)) + 1
			if current.lines+size+backfillHeaderRoom > backfillBatchBytes || len(current.records) >= backfillBatchRecords {
				batches = append(batches, current)
				current = refBatch{}
			}
			current.records = append(current.records, r)
			current.lines += size
		}
	})

	return append(batches, current)
}

// TestReferenceStream_BackfillRate encodes one scaled day of the reference stream in batches of the backfill's bounds
// and checks that at the default bandwidth the backfill delivers 1,000 to 8,500 records a second: the compressed bytes
// of the day take bytes/256 KiB seconds to send, and every record of the day is delivered in that time.
func TestReferenceStream_BackfillRate(t *testing.T) {
	batches := refBatches(t)
	var records int
	var plain, compressed int64
	for i, batch := range batches {
		var buf bytes.Buffer
		b := wire.Batch{Header: wire.NewHeader("agent-one", refBackfillBoot, wire.ModeBackfill), Records: batch.records}
		if err := wire.EncodeBatch(&buf, &b); err != nil {
			t.Fatalf("EncodeBatch(batch %d of %d records) = %v, want nil", i, len(batch.records), err)
		}
		compressed += int64(buf.Len())
		plain += batch.lines
		records += len(batch.records)
	}
	if records == 0 || compressed == 0 {
		t.Fatalf("the scaled day made %d records in %d compressed bytes, want a stream", records, compressed)
	}
	ratio := float64(plain) / float64(compressed)
	rate := float64(records) / (float64(compressed) / backfillBandwidth)
	t.Logf("reference stream day at 1/%d: %d records in %d batches, %d bytes of lines, %d compressed bytes, gzip ratio %.2f, %.0f records a second at %d bytes a second of compressed bodies",
		refScaledDivisor, records, len(batches), plain, compressed, ratio, rate, backfillBandwidth)
	if rate < backfillMinRate || rate > backfillMaxRate {
		t.Errorf("the backfill delivers %.0f records a second of the reference stream at %d bytes a second (gzip ratio %.2f), want %d to %d",
			rate, backfillBandwidth, ratio, backfillMinRate, backfillMaxRate)
	}
}
