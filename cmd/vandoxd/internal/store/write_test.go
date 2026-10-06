package store

import (
	"context"
	"errors"
	"fmt"
	"math"
	"net/netip"
	"reflect"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	testAgent = "agent-1"
	testBoot  = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b"
)

// baseTime is the fixed instant the fixtures are built on; tests use no real clock.
var baseTime = time.Date(2026, time.October, 6, 12, 0, 0, 0, time.UTC)

// metricRecord returns a valid agent metric record.
func metricRecord(source, name string, seq uint64, at time.Time) model.Record {
	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: source, Seq: seq, CapturedAt: at},
		Data: &model.MetricPoint{Name: name, Value: float64(seq)},
	}
}

// logRecord returns a valid agent log line record.
func logRecord(source, message string, seq uint64, at time.Time) model.Record {
	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: source, Seq: seq, CapturedAt: at},
		Data: &model.LogLine{Log: "syslog", Program: "kernel", Message: message},
	}
}

// agentBatch returns a batch of the test agent received at baseTime.
func agentBatch(recs ...model.Record) Batch {
	return Batch{AgentID: testAgent, ReceivedAt: baseTime.Add(time.Minute), Records: recs}
}

// writeOK writes b and fails the test on an error.
func writeOK(t *testing.T, s *Store, b Batch) WriteResult {
	t.Helper()
	res, err := s.WriteBatch(context.Background(), b)
	if err != nil {
		t.Fatalf("WriteBatch() error = %v, want nil", err)
	}
	return res
}

// countRows returns the number of rows of table, read through the writer pool.
func countRows(t *testing.T, s *Store, table string) int {
	t.Helper()
	var n int
	if err := s.db.QueryRowContext(context.Background(), "SELECT count(*) FROM "+table).Scan(&n); err != nil {
		t.Fatalf("count of %s error = %v, want nil", table, err)
	}
	return n
}

// readAll returns the records of kind in the window around baseTime.
func readAll(t *testing.T, s *Store, kind model.Kind) []StoredRecord {
	t.Helper()
	got, err := s.Records(context.Background(), RecordQuery{
		Kind: kind, From: baseTime.Add(-time.Hour), To: baseTime.Add(48 * time.Hour), Limit: MaxQueryLimit,
	})
	if err != nil {
		t.Fatalf("Records(%s) error = %v, want nil", kind, err)
	}
	return got
}

// injectFailure creates a trigger that aborts the insert of the record with the given seq.
func injectFailure(t *testing.T, s *Store, seq int) {
	t.Helper()
	stmt := fmt.Sprintf("CREATE TRIGGER inject_failure BEFORE INSERT ON records WHEN new.seq = %d BEGIN SELECT RAISE(ABORT, 'injected'); END", seq)
	if _, err := s.db.ExecContext(context.Background(), stmt); err != nil {
		t.Fatalf("creating the failure trigger error = %v, want nil", err)
	}
}

// integrityCheck runs the FTS5 integrity check of log_fts.
func integrityCheck(s *Store) error {
	_, err := s.db.ExecContext(context.Background(), "INSERT INTO log_fts(log_fts, rank) VALUES('integrity-check', 1)")
	return err
}

func u8(v uint8) *uint8 { return &v }

func u32(v uint32) *uint32 { return &v }

func u64(v uint64) *uint64 { return &v }

func i16(v int16) *int16 { return &v }

func dur(v time.Duration) *time.Duration { return &v }

// roundTripCases returns one valid record per kind and variant.
func roundTripCases() []struct {
	name string
	rec  model.Record
} {
	agent := func(source string, seq uint64, data model.Payload) model.Record {
		return model.Record{
			Meta: model.Meta{Origin: model.OriginAgent, Source: source, Seq: seq, CapturedAt: baseTime.Add(time.Duration(seq) * time.Second)},
			Data: data,
		}
	}
	return []struct {
		name string
		rec  model.Record
	}{
		{"metric with labels and unit", agent("node", 1, &model.MetricPoint{
			Name: "disk.used", Value: 12.5, Unit: "percent", Labels: map[string]string{"mount": "/var", "device": "sda1"},
		})},
		{"metric without labels and unit", agent("node", 2, &model.MetricPoint{Name: "load.1m", Value: -0.25})},
		{"process snapshot", agent("node", 3, &model.ProcessSnapshot{
			Complete: true,
			Total:    u32(2),
			Processes: []model.ProcessSample{
				{PID: 1, PPID: 0, User: "root", Command: "systemd", Cmdline: "/sbin/init", State: "S",
					StartedAt: baseTime.Add(-time.Hour), CPUPercent: 0.5, RSSBytes: 4096, PSSBytes: u64(2048), OOMScoreAdj: i16(-1000)},
				{PID: 42, Command: "mariadbd", RSSBytes: 1 << 30},
			},
			Programs: []model.ProgramAggregate{{Program: "mariadbd", Count: 1, CPUPercent: 3.5, RSSBytes: 1 << 30, PSSBytes: u64(1 << 29)}},
		})},
		{"connection snapshot", agent("node", 4, &model.ConnectionSnapshot{
			Complete:  true,
			States:    []model.StateCount{{Proto: model.ProtoTCP, State: "ESTABLISHED", Count: 3}},
			Processes: []model.ProcessConnections{{PID: 42, Command: "mariadbd", Count: 3}},
			Remotes:   []model.RemoteCount{{Addr: netip.MustParseAddr("192.0.2.7"), Count: 3}},
			Listeners: []model.Listener{{Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:3306"), PID: 42, Command: "mariadbd"}},
			Connections: []model.Connection{{
				Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("10.0.0.1:3306"),
				Remote: netip.MustParseAddrPort("192.0.2.7:51000"), State: "ESTABLISHED", PID: 42, Command: "mariadbd",
			}},
		})},
		{"service state", agent("node", 5, &model.ServiceState{
			Unit: "mariadb.service", LoadState: "loaded", ActiveState: "active", SubState: "running",
			ActiveEnterAt: baseTime.Add(-2 * time.Hour), Restarts: 2,
		})},
		{"mariadb status", agent("db", 6, &model.MariaDBStatus{
			Availability: model.MariaDBUp,
			PingLatency:  dur(1500 * time.Microsecond),
			Status:       map[string]uint64{"Threads_connected": 7, "Uptime": 86400},
			Variables:    map[string]string{"max_connections": "151"},
			Threads:      []model.MariaDBThread{{ID: 9, User: "app", Host: "localhost", DB: "shop", Command: "Query", TimeSeconds: 3, State: "executing", Info: "SELECT 1"}},
			Complete:     true,
		})},
		{"kernel event oom_kill", agent("node", 7, &model.KernelEvent{
			Type:    model.KernelEventOOMKill,
			OOMKill: &model.OOMKill{VictimPID: 42, VictimCommand: "mariadbd", AnonRSSBytes: u64(1 << 31), OOMScoreAdj: i16(0)},
			Message: "Out of memory: Killed process 42 (mariadbd)",
		})},
		{"kernel event boot", agent("node", 8, &model.KernelEvent{
			Type: model.KernelEventBoot,
			Boot: &model.Boot{BootID: testBoot, PreviousBootID: "ffffffff-4b5a-6978-8a9b-0c1d2e3f4a5b",
				BootedAt: baseTime.Add(-24 * time.Hour), PreviousUptime: dur(36 * time.Hour)},
		})},
		{"log line with priority", agent("node", 9, &model.LogLine{
			Log: "syslog", Program: "sshd", PID: 311, Priority: u8(3), Message: "Failed password for root", Truncated: true,
		})},
		{"log line without priority", agent("node", 10, &model.LogLine{Log: "mysql-error", Message: "InnoDB: started"})},
		{"gap", agent("node", 11, &model.Gap{
			From: baseTime.Add(-time.Hour), To: baseTime.Add(-time.Minute), Cause: model.GapSpoolDropped, FirstSeq: 3, LastSeq: 8,
		})},
		{"backend gap", model.Record{
			Meta: model.Meta{Origin: model.OriginBackend, Source: "node", CapturedAt: baseTime.Add(12 * time.Second)},
			Data: &model.Gap{From: baseTime.Add(-time.Hour), To: baseTime, Cause: model.GapNoData},
		}},
	}
}

// checkStored compares the stored record with the record and batch it was written from.
func checkStored(t *testing.T, got StoredRecord, want model.Record, b Batch) {
	t.Helper()
	if !reflect.DeepEqual(got.Record, want) {
		t.Errorf("stored record = %#v, want %#v", got.Record, want)
	}
	if got.ID <= 0 {
		t.Errorf("stored ID = %d, want > 0", got.ID)
	}
	if got.AgentID != b.AgentID || got.BootID != b.BootID {
		t.Errorf("stored AgentID, BootID = %q, %q, want %q, %q", got.AgentID, got.BootID, b.AgentID, b.BootID)
	}
	if got.ClockOffset == nil || *got.ClockOffset != *b.ClockOffset {
		t.Errorf("stored ClockOffset = %v, want %v", got.ClockOffset, *b.ClockOffset)
	}
	if !got.ReceivedAt.Equal(b.ReceivedAt) {
		t.Errorf("stored ReceivedAt = %v, want %v", got.ReceivedAt, b.ReceivedAt)
	}
}

func TestStore_WriteBatch_RoundTrip(t *testing.T) {
	offset := 250 * time.Millisecond
	for _, tc := range roundTripCases() {
		t.Run(tc.name, func(t *testing.T) {
			s := openStore(t, t.TempDir())
			b := agentBatch(tc.rec)
			b.BootID = testBoot
			b.ClockOffset = &offset

			res := writeOK(t, s, b)

			if res != (WriteResult{Stored: 1}) {
				t.Errorf("WriteBatch() = %+v, want {Stored: 1}", res)
			}
			got := readAll(t, s, tc.rec.Kind())
			if len(got) != 1 {
				t.Fatalf("Records(%s) returned %d records, want 1", tc.rec.Kind(), len(got))
			}
			checkStored(t, got[0], tc.rec, b)
		})
	}
}

func TestStore_WriteBatch_RoundTripBatchContext(t *testing.T) {
	t.Run("no clock offset and no boot ID", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		b := agentBatch(metricRecord("node", "cpu.load", 1, baseTime))

		writeOK(t, s, b)

		got := readAll(t, s, model.KindMetric)
		if len(got) != 1 {
			t.Fatalf("Records() returned %d records, want 1", len(got))
		}
		if got[0].ClockOffset != nil {
			t.Errorf("stored ClockOffset = %v, want nil", *got[0].ClockOffset)
		}
		if got[0].BootID != "" {
			t.Errorf("stored BootID = %q, want empty", got[0].BootID)
		}
		if got[0].AgentID != testAgent {
			t.Errorf("stored AgentID = %q, want %q", got[0].AgentID, testAgent)
		}
	})

	t.Run("zero clock offset is kept", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		b := agentBatch(metricRecord("node", "cpu.load", 1, baseTime))
		b.ClockOffset = dur(0)

		writeOK(t, s, b)

		got := readAll(t, s, model.KindMetric)
		if len(got) != 1 || got[0].ClockOffset == nil || *got[0].ClockOffset != 0 {
			t.Errorf("stored ClockOffset = %v, want a non-nil zero offset", got)
		}
	})

	t.Run("empty labels map reads back as nil", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		rec := metricRecord("node", "cpu.load", 1, baseTime)
		rec.Data.(*model.MetricPoint).Labels = map[string]string{}

		writeOK(t, s, agentBatch(rec))

		got := readAll(t, s, model.KindMetric)
		if len(got) != 1 {
			t.Fatalf("Records() returned %d records, want 1", len(got))
		}
		if labels := got[0].Record.Data.(*model.MetricPoint).Labels; labels != nil {
			t.Errorf("stored Labels = %#v, want nil", labels)
		}
	})
}

func TestStore_WriteBatch_DuplicateCounts(t *testing.T) {
	s := openStore(t, t.TempDir())
	first := agentBatch(
		metricRecord("node", "cpu.load", 1, baseTime),
		metricRecord("node", "cpu.load", 2, baseTime.Add(time.Second)),
		metricRecord("node", "cpu.load", 3, baseTime.Add(2*time.Second)),
	)

	if got := writeOK(t, s, first); got != (WriteResult{Stored: 3}) {
		t.Errorf("first WriteBatch() = %+v, want {Stored: 3}", got)
	}
	if got := writeOK(t, s, first); got != (WriteResult{Duplicates: 3}) {
		t.Errorf("repeated WriteBatch() = %+v, want {Duplicates: 3}", got)
	}
	if got := countRows(t, s, "records"); got != 3 {
		t.Errorf("stored records = %d, want 3", got)
	}
}

func TestStore_WriteBatch_ResentRecordNeverOverwrites(t *testing.T) {
	s := openStore(t, t.TempDir())
	writeOK(t, s, agentBatch(metricRecord("node", "cpu.load", 3, baseTime)))
	resent := metricRecord("node", "other.metric", 3, baseTime.Add(time.Hour))
	resent.Data.(*model.MetricPoint).Value = 999
	later := agentBatch(resent, metricRecord("node", "cpu.load", 4, baseTime.Add(time.Second)))
	later.ReceivedAt = baseTime.Add(2 * time.Hour)
	later.BootID = testBoot

	got := writeOK(t, s, later)

	if got != (WriteResult{Stored: 1, Duplicates: 1}) {
		t.Errorf("WriteBatch() = %+v, want {Stored: 1, Duplicates: 1}", got)
	}
	stored := readAll(t, s, model.KindMetric)
	if len(stored) != 2 {
		t.Fatalf("stored records = %d, want 2", len(stored))
	}
	original := stored[0]
	if original.Record.Seq != 3 {
		t.Fatalf("first stored record has seq %d, want 3", original.Record.Seq)
	}
	if want := metricRecord("node", "cpu.load", 3, baseTime); !reflect.DeepEqual(original.Record, want) {
		t.Errorf("record seq 3 = %#v, want the original %#v", original.Record, want)
	}
	if want := baseTime.Add(time.Minute); !original.ReceivedAt.Equal(want) {
		t.Errorf("record seq 3 ReceivedAt = %v, want the original %v", original.ReceivedAt, want)
	}
	if original.BootID != "" {
		t.Errorf("record seq 3 BootID = %q, want the original (empty)", original.BootID)
	}
	if got := countRows(t, s, "metrics"); got != 2 {
		t.Errorf("rows in metrics = %d, want 2 (no payload row for a duplicate)", got)
	}
}

func TestStore_WriteBatch_SameSequenceTwiceInOneBatch(t *testing.T) {
	s := openStore(t, t.TempDir())

	got := writeOK(t, s, agentBatch(
		metricRecord("node", "cpu.load", 7, baseTime),
		metricRecord("node", "cpu.load", 7, baseTime),
	))

	if got != (WriteResult{Stored: 1, Duplicates: 1}) {
		t.Errorf("WriteBatch() = %+v, want {Stored: 1, Duplicates: 1}", got)
	}
	if n := countRows(t, s, "records"); n != 1 {
		t.Errorf("stored records = %d, want 1", n)
	}
}

func TestStore_WriteBatch_SameSequenceUnderAnotherAgent(t *testing.T) {
	s := openStore(t, t.TempDir())
	writeOK(t, s, agentBatch(metricRecord("node", "cpu.load", 1, baseTime)))
	other := agentBatch(metricRecord("node", "cpu.load", 1, baseTime))
	other.AgentID = "agent-2"

	got := writeOK(t, s, other)

	if got != (WriteResult{Stored: 1}) {
		t.Errorf("WriteBatch() under another agent = %+v, want {Stored: 1}", got)
	}
	if n := countRows(t, s, "records"); n != 2 {
		t.Errorf("stored records = %d, want 2", n)
	}
}

func TestStore_WriteBatch_ImportAndBackendAreNotDeduplicated(t *testing.T) {
	for _, origin := range []model.Origin{model.OriginImport, model.OriginBackend} {
		t.Run(string(origin), func(t *testing.T) {
			s := openStore(t, t.TempDir())
			rec := model.Record{
				Meta: model.Meta{Origin: origin, Source: "node", CapturedAt: baseTime},
				Data: &model.MetricPoint{Name: "cpu.load", Value: 1},
			}
			b := Batch{ReceivedAt: baseTime, Records: []model.Record{rec}}

			first := writeOK(t, s, b)
			second := writeOK(t, s, b)

			if first != (WriteResult{Stored: 1}) || second != (WriteResult{Stored: 1}) {
				t.Errorf("WriteBatch() twice = %+v, %+v, want {Stored: 1} both times", first, second)
			}
			if n := countRows(t, s, "records"); n != 2 {
				t.Errorf("stored records = %d, want 2", n)
			}
		})
	}
}

// bulkRecords returns metrics seq 1..metrics followed by log lines up to metrics+logs.
func bulkRecords(metrics, logs int) []model.Record {
	recs := make([]model.Record, 0, metrics+logs)
	for i := range metrics {
		recs = append(recs, metricRecord("node", "cpu.load", uint64(i+1), baseTime.Add(time.Duration(i)*time.Millisecond)))
	}
	for i := range logs {
		seq := uint64(metrics + i + 1)
		recs = append(recs, logRecord("node", fmt.Sprintf("Out of memory: Killed process %d (mariadbd)", seq), seq,
			baseTime.Add(time.Duration(seq)*time.Millisecond)))
	}
	return recs
}

func TestStore_WriteBatch_Atomic(t *testing.T) {
	s := openStore(t, t.TempDir())
	injectFailure(t, s, 5000)

	res, err := s.WriteBatch(context.Background(), agentBatch(bulkRecords(5000, 5000)...))

	if err == nil {
		t.Fatalf("WriteBatch() = %+v, error = nil, want an error", res)
	}
	for _, table := range []string{"records", "metrics", "log_lines", "log_fts_docsize"} {
		if n := countRows(t, s, table); n != 0 {
			t.Errorf("rows in %s after the failed batch = %d, want 0", table, n)
		}
	}
}

func TestStore_WriteBatch_TenThousandRecords(t *testing.T) {
	s := openStore(t, t.TempDir())

	res := writeOK(t, s, agentBatch(bulkRecords(5000, 5000)...))

	if res != (WriteResult{Stored: 10000}) {
		t.Fatalf("WriteBatch() = %+v, want {Stored: 10000}", res)
	}
	if got := len(readAll(t, s, model.KindMetric)); got != 5000 {
		t.Errorf("Records(metric) returned %d records, want 5000", got)
	}
	if got := len(readAll(t, s, model.KindLogLine)); got != 5000 {
		t.Errorf("Records(log_line) returned %d records, want 5000", got)
	}
	hits, err := s.SearchLogs(context.Background(), LogSearch{
		Text: "mariadbd", From: baseTime.Add(-time.Hour), To: baseTime.Add(time.Hour), Limit: MaxQueryLimit,
	})
	if err != nil {
		t.Fatalf("SearchLogs() error = %v, want nil", err)
	}
	if len(hits) != 5000 {
		t.Errorf("SearchLogs() returned %d lines, want 5000", len(hits))
	}
}

type rejection struct {
	name      string
	mutate    func(b *Batch)
	wantModel bool
	wantPath  string
}

func rejections() []rejection {
	minTime := time.Unix(0, math.MinInt64).UTC()
	maxTime := time.Unix(0, math.MaxInt64).UTC()
	withRecord := func(mutate func(r *model.Record)) func(b *Batch) {
		return func(b *Batch) { mutate(&b.Records[0]) }
	}
	tooMany := make([]model.Record, MaxBatchRecords+1)
	for i := range tooMany {
		tooMany[i] = metricRecord("node", "cpu.load", uint64(i+1), baseTime)
	}
	return []rejection{
		{name: "no records", mutate: func(b *Batch) { b.Records = nil }},
		{name: "more than MaxBatchRecords", mutate: func(b *Batch) { b.Records = tooMany }},
		{name: "zero ReceivedAt", mutate: func(b *Batch) { b.ReceivedAt = time.Time{} }},
		{name: "non-UTC ReceivedAt", mutate: func(b *Batch) { b.ReceivedAt = baseTime.In(time.FixedZone("plus1", 3600)) }},
		{name: "ReceivedAt before the storable range", mutate: func(b *Batch) { b.ReceivedAt = minTime.Add(-time.Nanosecond) }},
		{name: "ReceivedAt after the storable range", mutate: func(b *Batch) { b.ReceivedAt = maxTime.Add(time.Nanosecond) }},
		{name: "CapturedAt before the storable range", mutate: withRecord(func(r *model.Record) { r.CapturedAt = minTime.Add(-time.Nanosecond) })},
		{name: "CapturedAt after the storable range", mutate: withRecord(func(r *model.Record) { r.CapturedAt = maxTime.Add(time.Nanosecond) })},
		{name: "invalid record", wantModel: true, wantPath: "records[1]", mutate: func(b *Batch) {
			b.Records = append(b.Records, metricRecord("node", "", 2, baseTime))
		}},
		{name: "agent record without agent ID", mutate: func(b *Batch) { b.AgentID = "" }},
		{name: "invalid agent ID", mutate: func(b *Batch) { b.AgentID = "bad id!" }},
		{name: "sequence number above the storable range", mutate: withRecord(func(r *model.Record) { r.Seq = math.MaxInt64 + 1 })},
	}
}

// checkRejection checks the result and error of a batch that must have been rejected.
func checkRejection(t *testing.T, tc rejection, res WriteResult, err error) {
	t.Helper()
	if !errors.Is(err, ErrInvalidBatch) {
		t.Fatalf("WriteBatch() = %+v, error = %v, want an error wrapping ErrInvalidBatch", res, err)
	}
	if res != (WriteResult{}) {
		t.Errorf("WriteBatch() result = %+v, want the zero result", res)
	}
	if tc.wantModel && !errors.Is(err, model.ErrInvalid) {
		t.Errorf("WriteBatch() error = %v, want it to wrap model.ErrInvalid", err)
	}
	if tc.wantPath != "" && !strings.Contains(err.Error(), tc.wantPath) {
		t.Errorf("WriteBatch() error = %q, want it to name %q", err, tc.wantPath)
	}
}

func TestStore_WriteBatch_Rejections(t *testing.T) {
	for _, tc := range rejections() {
		t.Run(tc.name, func(t *testing.T) {
			s := openStore(t, t.TempDir())
			b := agentBatch(metricRecord("node", "cpu.load", 1, baseTime))
			tc.mutate(&b)

			res, err := s.WriteBatch(context.Background(), b)

			checkRejection(t, tc, res, err)
			if n := countRows(t, s, "records"); n != 0 {
				t.Errorf("stored records after the rejected batch = %d, want 0", n)
			}
		})
	}
}

func TestStore_WriteBatch_AcceptsBoundaryInstants(t *testing.T) {
	minTime := time.Unix(0, math.MinInt64).UTC()
	maxTime := time.Unix(0, math.MaxInt64).UTC()
	s := openStore(t, t.TempDir())
	b := Batch{AgentID: testAgent, ReceivedAt: maxTime, Records: []model.Record{
		metricRecord("node", "cpu.load", 1, minTime),
		metricRecord("node", "cpu.load", 2, maxTime),
	}}

	res, err := s.WriteBatch(context.Background(), b)

	if err != nil || res != (WriteResult{Stored: 2}) {
		t.Fatalf("WriteBatch() = %+v, %v, want {Stored: 2}, nil", res, err)
	}
	got, err := s.Records(context.Background(), RecordQuery{
		Kind: model.KindMetric, From: minTime, To: minTime.Add(time.Second), Limit: 10,
	})
	if err != nil || len(got) != 1 || !got[0].Record.CapturedAt.Equal(minTime) {
		t.Errorf("Records() at the lower boundary = %v, %v, want the record captured at %v", got, err, minTime)
	}
}

func TestStore_WriteBatch_AcceptsLargestSequenceNumber(t *testing.T) {
	s := openStore(t, t.TempDir())

	res, err := s.WriteBatch(context.Background(), agentBatch(metricRecord("node", "cpu.load", math.MaxInt64, baseTime)))

	if err != nil || res != (WriteResult{Stored: 1}) {
		t.Errorf("WriteBatch(seq MaxInt64) = %+v, %v, want {Stored: 1}, nil", res, err)
	}
}

func TestStore_WriteBatch_ContextAndClosedStore(t *testing.T) {
	t.Run("cancelled context", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		ctx, cancel := context.WithCancel(context.Background())
		cancel()

		_, err := s.WriteBatch(ctx, agentBatch(metricRecord("node", "cpu.load", 1, baseTime)))

		if !errors.Is(err, context.Canceled) {
			t.Errorf("WriteBatch(cancelled ctx) error = %v, want it to wrap context.Canceled", err)
		}
		if n := countRows(t, s, "records"); n != 0 {
			t.Errorf("stored records after the cancelled call = %d, want 0", n)
		}
	})

	t.Run("closed store", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		if err := s.Close(); err != nil {
			t.Fatalf("Close() error = %v, want nil", err)
		}

		if _, err := s.WriteBatch(context.Background(), agentBatch(metricRecord("node", "cpu.load", 1, baseTime))); err == nil {
			t.Error("WriteBatch() on a closed store error = nil, want an error")
		}
	})
}

func TestMaxBatchRecords_EqualsWireLimit(t *testing.T) {
	if want := wire.DefaultLimits().MaxRecords; MaxBatchRecords != want {
		t.Errorf("MaxBatchRecords = %d, want wire.DefaultLimits().MaxRecords = %d", MaxBatchRecords, want)
	}
}

func TestStore_WriteBatch_ErrorsDoNotShowPayload(t *testing.T) {
	const secret = "PAYLOAD-SECRET-4711"
	cases := []struct {
		name  string
		build func() []model.Record
		setup func(t *testing.T, s *Store)
	}{
		{"log line without a log name", func() []model.Record {
			r := logRecord("node", secret, 1, baseTime)
			r.Data.(*model.LogLine).Log = ""
			return []model.Record{r}
		}, nil},
		{"log line with an invalid priority", func() []model.Record {
			r := logRecord("node", secret, 1, baseTime)
			r.Data.(*model.LogLine).Priority = u8(9)
			return []model.Record{r}
		}, nil},
		{"metric with an invalid name and a label value", func() []model.Record {
			r := metricRecord("node", "bad name", 1, baseTime)
			r.Data.(*model.MetricPoint).Labels = map[string]string{"host": secret}
			return []model.Record{r}
		}, nil},
		{"metric with an invalid label key", func() []model.Record {
			r := metricRecord("node", "cpu.load", 1, baseTime)
			r.Data.(*model.MetricPoint).Labels = map[string]string{"bad key": secret}
			return []model.Record{r}
		}, nil},
		{"database failure", func() []model.Record {
			r := metricRecord("node", "cpu.load", 1, baseTime)
			r.Data.(*model.MetricPoint).Labels = map[string]string{"host": secret}
			return []model.Record{r, logRecord("node", secret, 2, baseTime)}
		}, func(t *testing.T, s *Store) { injectFailure(t, s, 2) }},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			dir := t.TempDir()
			s := openStore(t, dir)
			if tc.setup != nil {
				tc.setup(t, s)
			}

			_, err := s.WriteBatch(context.Background(), agentBatch(tc.build()...))

			if err == nil {
				t.Fatal("WriteBatch() error = nil, want an error")
			}
			if text := strings.ReplaceAll(err.Error(), dir, ""); strings.Contains(text, secret) {
				t.Errorf("WriteBatch() error = %q, want no payload text", text)
			}
		})
	}
}

// writeHostileHistory writes the batches AC-W10 names: a batch with hostile log messages, the same batch
// again, a batch with one sequence number twice, and a failing batch.
func writeHostileHistory(t *testing.T, s *Store, hostile []string) {
	t.Helper()
	first := []model.Record{
		metricRecord("node", "cpu.load", 1, baseTime),
		logRecord("node", "Out of memory: Killed process 1 (mariadbd)", 2, baseTime.Add(time.Second)),
	}
	for i, msg := range hostile {
		first = append(first, logRecord("node", msg, uint64(3+i), baseTime.Add(time.Duration(2+i)*time.Second)))
	}
	firstBatch := agentBatch(first...)
	writeOK(t, s, firstBatch)
	writeOK(t, s, firstBatch)
	writeOK(t, s, agentBatch(
		logRecord("node", "twice in one batch", 100, baseTime.Add(time.Minute)),
		logRecord("node", "twice in one batch", 100, baseTime.Add(time.Minute)),
	))
	injectFailure(t, s, 5000)
	if _, err := s.WriteBatch(context.Background(), agentBatch(bulkRecords(5000, 5000)...)); err == nil {
		t.Fatal("WriteBatch() of the failing batch error = nil, want an error")
	}
}

func TestStore_WriteBatch_FTSIndexStaysInStep(t *testing.T) {
	hostile := []string{"a\x00b oom", "\xff\xfe", "ends with a lone \xc3", "\ufeffstarts with a BOM"}
	s := openStore(t, t.TempDir())

	writeHostileHistory(t, s, hostile)

	if err := integrityCheck(s); err != nil {
		t.Errorf("FTS5 integrity-check error = %v, want nil", err)
	}
	docs, lines := countRows(t, s, "log_fts_docsize"), countRows(t, s, "log_lines")
	if docs != lines {
		t.Errorf("rows in log_fts_docsize = %d, log_lines = %d, want equal", docs, lines)
	}
	if want := 1 + len(hostile) + 1; lines != want {
		t.Errorf("rows in log_lines = %d, want %d", lines, want)
	}
	var stored []string
	for _, sr := range readAll(t, s, model.KindLogLine) {
		stored = append(stored, sr.Record.Data.(*model.LogLine).Message)
	}
	for _, msg := range hostile {
		if !slices.Contains(stored, msg) {
			t.Errorf("message %q not read back byte-identical", msg)
		}
	}
}

func TestStore_FTSIntegrityCheck_FailsForAnUnindexedLine(t *testing.T) {
	s := openStore(t, t.TempDir())
	ctx := context.Background()
	res, err := s.db.ExecContext(ctx, "INSERT INTO records(kind, origin, source, captured_at, received_at) VALUES ('log_line', 'import', 'node', 1, 2)")
	if err != nil {
		t.Fatalf("direct insert into records error = %v, want nil", err)
	}
	id, err := res.LastInsertId()
	if err != nil {
		t.Fatalf("LastInsertId() error = %v, want nil", err)
	}
	if _, err := s.db.ExecContext(ctx, "INSERT INTO log_lines(record_id, log, program, pid, message, truncated) VALUES (?, 'syslog', '', 0, 'unindexed', 0)", id); err != nil {
		t.Fatalf("direct insert into log_lines error = %v, want nil", err)
	}

	if err := integrityCheck(s); err == nil {
		t.Error("FTS5 integrity-check on an unindexed log line error = nil, want an error")
	}
}

// benchBatch returns a batch of size new records with fresh sequence numbers after *seq: the first metrics
// are metric points, the rest kernel OOM-kill log lines with varying numbers.
func benchBatch(seq *uint64, size, metrics int) Batch {
	recs := make([]model.Record, 0, size)
	for i := range size {
		*seq++
		at := baseTime.Add(time.Duration(*seq) * time.Millisecond)
		if i < metrics {
			recs = append(recs, metricRecord("node", "cpu.load", *seq, at))
			continue
		}
		n := int(*seq)
		msg := fmt.Sprintf("Out of memory: Killed process %d (mariadbd) total-vm:%dkB, anon-rss:%dkB, file-rss:0kB, shmem-rss:0kB UID:27 pgtables:%dkB oom_score_adj:0",
			1000+n%30000, 2000000+n, 1000000+n%5000, 4000+n%900)
		recs = append(recs, logRecord("node", msg, *seq, at))
	}
	return agentBatch(recs...)
}

func BenchmarkStore_WriteBatch(b *testing.B) {
	const size = 10000
	for _, bc := range []struct {
		name    string
		metrics int
	}{
		{"metric", size},
		{"log_line", 0},
		{"mixed", 9000},
	} {
		b.Run(bc.name, func(b *testing.B) {
			s, err := Open(context.Background(), b.TempDir())
			if err != nil {
				b.Fatalf("Open() error = %v, want nil", err)
			}
			b.Cleanup(func() { _ = s.Close() })
			var seq uint64
			total := 0
			for b.Loop() {
				b.StopTimer()
				batch := benchBatch(&seq, size, bc.metrics)
				b.StartTimer()

				res, err := s.WriteBatch(context.Background(), batch)
				if err != nil || res.Stored != size {
					b.Fatalf("WriteBatch() = %+v, %v, want {Stored: %d}, nil", res, err, size)
				}
				total += size
			}
			b.ReportMetric(float64(total)/b.Elapsed().Seconds(), "records/s")
		})
	}
}
