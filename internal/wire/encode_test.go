package wire_test

import (
	"bytes"
	"compress/gzip"
	"encoding/json"
	"errors"
	"io"
	"net/netip"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// atLoc builds a time in loc; with time.UTC the value carries no monotonic reading and no zone pointer.
func atLoc(loc *time.Location, hour int, nanos int) time.Time {
	return time.Date(2026, time.March, 1, hour, 30, 15, nanos, loc)
}

func agentMeta(seq uint64, source string, loc *time.Location) model.Meta {
	return model.Meta{Origin: model.OriginAgent, Source: source, Seq: seq, CapturedAt: atLoc(loc, 12, 123456789)}
}

func metricAll(seq uint64, loc *time.Location) model.Record {
	return model.Record{Meta: agentMeta(seq, "proc.meminfo", loc), Data: &model.MetricPoint{
		Name: "node_filesystem_free_bytes", Value: 1536.5, Unit: "bytes",
		Labels: map[string]string{"mount": "/var", "device": "sda1"},
	}}
}

func processAll(seq uint64, loc *time.Location) model.Record {
	return model.Record{Meta: agentMeta(seq, "ps", loc), Data: &model.ProcessSnapshot{
		Complete: true,
		Total:    ptr(uint32(120)),
		Processes: []model.ProcessSample{{
			PID: 1, PPID: 0, User: "root", Command: "systemd", Cmdline: "/usr/lib/systemd/systemd --system",
			Truncated: true, State: "S", StartedAt: atLoc(loc, 8, 5), CPUPercent: 1.5, RSSBytes: 4096,
			PSSBytes: ptr(uint64(2048)), SwapBytes: ptr(uint64(512)), OOMScoreAdj: ptr(int16(-1000)),
		}},
		Programs: []model.ProgramAggregate{{
			Program: "php-fpm", Count: 3, CPUPercent: 2.5, RSSBytes: 8192, PSSBytes: ptr(uint64(4096)), RSSOvercounted: true,
		}},
	}}
}

func gapAll(seq uint64, loc *time.Location) model.Record {
	return model.Record{Meta: agentMeta(seq, "agent", loc), Data: &model.Gap{
		From: atLoc(loc, 9, 0), To: atLoc(loc, 10, 999), Cause: model.GapCollectorTimeout,
		Collector: "proc.meminfo", FirstSeq: 3, LastSeq: 9,
	}}
}

// allKindRecords returns one fully populated record of every kind (the kernel kind twice), seq 1..9.
func allKindRecords(loc *time.Location) []model.Record {
	return []model.Record{
		metricAll(1, loc),
		processAll(2, loc),
		{Meta: agentMeta(3, "lsof", loc), Data: &model.ConnectionSnapshot{
			Complete: true,
			States:   []model.StateCount{{Proto: model.ProtoTCP, State: "ESTABLISHED", Count: 3}},
			Processes: []model.ProcessConnections{{PID: 10, Command: "nginx", Count: 2}},
			Remotes:   []model.RemoteCount{{Addr: netip.MustParseAddr("2001:db8::1"), Count: 4}},
			Listeners: []model.Listener{{Proto: model.ProtoTCP6, Local: netip.MustParseAddrPort("[::]:443"), PID: 10, Command: "nginx"}},
			Connections: []model.Connection{{
				Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("10.0.0.1:80"), Remote: netip.MustParseAddrPort("192.0.2.1:5000"),
				State: "ESTABLISHED", PID: 10, Command: "nginx",
			}},
		}},
		{Meta: agentMeta(4, "systemd", loc), Data: &model.ServiceState{
			Unit: "nginx.service", LoadState: "loaded", ActiveState: "active", SubState: "running",
			ActiveEnterAt: atLoc(loc, 7, 0), Restarts: 2,
		}},
		{Meta: agentMeta(5, "mariadb", loc), Data: &model.MariaDBStatus{
			Availability: model.MariaDBUp,
			PingLatency:  ptr(5 * time.Millisecond),
			Status:       map[string]uint64{"Uptime": 100, "Threads_connected": 3},
			Variables:    map[string]string{"max_connections": "151"},
			Threads: []model.MariaDBThread{{
				ID: 7, User: "root", Host: "localhost", DB: "shop", Command: "Query", TimeSeconds: 3,
				State: "executing", Info: "SELECT 1", Truncated: true,
			}},
			Complete: true,
		}},
		{Meta: agentMeta(6, "kernel", loc), Data: &model.KernelEvent{
			Type: model.KernelEventOOMKill, Message: "Out of memory",
			OOMKill: &model.OOMKill{VictimPID: 4242, VictimCommand: "mysqld", AnonRSSBytes: ptr(uint64(1 << 30)), OOMScoreAdj: ptr(int16(-900))},
		}},
		{Meta: agentMeta(7, "kernel", loc), Data: &model.KernelEvent{
			Type: model.KernelEventBoot, Message: "booted",
			Boot: &model.Boot{BootID: testBootID, PreviousBootID: "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d", BootedAt: atLoc(loc, 6, 0), PreviousUptime: ptr(36 * time.Hour)},
		}},
		{Meta: agentMeta(8, "journal", loc), Data: &model.LogLine{
			Log: "journal", Program: "sshd", PID: 5120, Priority: ptr(uint8(3)), Message: "Failed password", Truncated: true,
		}},
		gapAll(9, loc),
	}
}

func gunzip(t *testing.T, data []byte) string {
	t.Helper()
	zr, err := gzip.NewReader(bytes.NewReader(data))
	if err != nil {
		t.Fatalf("gzip.NewReader() = %v, want nil", err)
	}
	out, err := io.ReadAll(zr)
	if err != nil {
		t.Fatalf("reading gzip stream = %v, want nil", err)
	}
	return string(out)
}

// encodedLines encodes b and returns the decompressed lines without their trailing newline.
func encodedLines(t *testing.T, b *wire.Batch) []string {
	t.Helper()
	var buf bytes.Buffer
	if err := wire.EncodeBatch(&buf, b); err != nil {
		t.Fatalf("EncodeBatch() = %v, want nil", err)
	}
	text := gunzip(t, buf.Bytes())
	if !strings.HasSuffix(text, "\n") {
		t.Fatalf("decompressed batch does not end with a newline")
	}
	return strings.Split(strings.TrimSuffix(text, "\n"), "\n")
}

// envelopeLine marshals r the way the wire format specifies, as an independent reference.
func envelopeLine(t *testing.T, r *model.Record) []byte {
	t.Helper()
	line, err := json.Marshal(struct {
		Kind       model.Kind    `json:"kind"`
		Source     string        `json:"source"`
		Seq        uint64        `json:"seq"`
		CapturedAt time.Time     `json:"captured_at"`
		Data       model.Payload `json:"data"`
	}{r.Kind(), r.Source, r.Seq, r.CapturedAt.UTC(), r.Data})
	if err != nil {
		t.Fatalf("json.Marshal(envelope) = %v, want nil", err)
	}
	return line
}

type countingWriter struct{ n int }

func (w *countingWriter) Write(p []byte) (int, error) {
	w.n += len(p)
	return len(p), nil
}

var errWrite = errors.New("disk full")

type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) { return 0, errWrite }

type closeSpyWriter struct {
	bytes.Buffer
	closed bool
}

func (w *closeSpyWriter) Close() error { w.closed = true; return nil }

func decodeBatch(t *testing.T, data []byte) (wire.Header, []model.Record) {
	t.Helper()
	d, err := wire.NewDecoder(bytes.NewReader(data), wire.DefaultLimits())
	if err != nil {
		t.Fatalf("NewDecoder() = %v, want nil", err)
	}
	var recs []model.Record
	for range 1000 {
		rec, err := d.Next()
		if errors.Is(err, io.EOF) {
			return d.Header(), recs
		}
		if err != nil {
			t.Fatalf("Next() = %v after %d records, want io.EOF", err, len(recs))
		}
		recs = append(recs, rec)
	}
	t.Fatalf("decoder did not reach io.EOF")
	return wire.Header{}, nil
}

func TestEncodeBatch_RoundTrip(t *testing.T) {
	t.Run("every kind with all optional fields", func(t *testing.T) {
		b := &wire.Batch{
			Header:  wire.NewHeader("agent-1", testBootID, wire.ModeBackfill),
			Records: allKindRecords(time.UTC),
		}
		b.Header.ClockOffset = ptr(-1500 * time.Millisecond)
		var buf bytes.Buffer
		if err := wire.EncodeBatch(&buf, b); err != nil {
			t.Fatalf("EncodeBatch() = %v, want nil", err)
		}
		h, recs := decodeBatch(t, buf.Bytes())
		if !reflect.DeepEqual(h, b.Header) {
			t.Errorf("decoded header = %+v, want %+v", h, b.Header)
		}
		if len(recs) != len(b.Records) {
			t.Fatalf("decoded %d records, want %d", len(recs), len(b.Records))
		}
		for i := range recs {
			if !reflect.DeepEqual(recs[i], b.Records[i]) {
				t.Errorf("record %d (%s) = %+v (data %+v), want %+v (data %+v)", i, b.Records[i].Kind(), recs[i], recs[i].Data, b.Records[i], b.Records[i].Data)
			}
		}
	})

	t.Run("zero-offset zones are written as Z and decode as UTC", func(t *testing.T) {
		zones := map[string]*time.Location{"empty name": time.FixedZone("", 0), "named": time.FixedZone("X", 0)}
		for name, loc := range zones {
			t.Run(name, func(t *testing.T) {
				in := []model.Record{metricAll(1, loc), processAll(2, loc), gapAll(3, loc)}
				want := []model.Record{metricAll(1, time.UTC), processAll(2, time.UTC), gapAll(3, time.UTC)}
				b := &wire.Batch{Header: wire.NewHeader("agent-1", testBootID, wire.ModeLive), Records: in}
				var buf bytes.Buffer
				if err := wire.EncodeBatch(&buf, b); err != nil {
					t.Fatalf("EncodeBatch() = %v, want nil", err)
				}
				_, got := decodeBatch(t, buf.Bytes())
				if len(got) != len(want) {
					t.Fatalf("decoded %d records, want %d", len(got), len(want))
				}
				for i := range got {
					if !reflect.DeepEqual(got[i], want[i]) {
						t.Errorf("record %d = %+v, want %+v", i, got[i], want[i])
					}
				}
				times := []time.Time{got[0].CapturedAt}
				times = append(times, got[1].Data.(*model.ProcessSnapshot).Processes[0].StartedAt)
				gap := got[2].Data.(*model.Gap)
				times = append(times, gap.From, gap.To)
				for i, ts := range times {
					if ts.Location() != time.UTC {
						t.Errorf("decoded time %d (%v) has location %v, want UTC", i, ts, ts.Location())
					}
				}
			})
		}
	})
}

func TestEncodeBatch_StreamLayout(t *testing.T) {
	b := &wire.Batch{Header: wire.NewHeader("agent-1", testBootID, wire.ModeLive), Records: allKindRecords(time.UTC)}
	var buf bytes.Buffer
	if err := wire.EncodeBatch(&buf, b); err != nil {
		t.Fatalf("EncodeBatch() = %v, want nil", err)
	}
	data := buf.Bytes()
	if len(data) < 2 || data[0] != 0x1f || data[1] != 0x8b {
		t.Fatalf("encoded bytes start with % x, want gzip magic 1f 8b", data[:min(2, len(data))])
	}
	text := gunzip(t, data)
	if !strings.HasSuffix(text, "\n") {
		t.Fatalf("decompressed batch does not end with a newline")
	}
	if got, want := strings.Count(text, "\n"), len(b.Records)+1; got != want {
		t.Fatalf("decompressed batch has %d newlines, want %d (header plus %d records)", got, want, len(b.Records))
	}
	lines := strings.Split(strings.TrimSuffix(text, "\n"), "\n")

	var header map[string]any
	if err := json.Unmarshal([]byte(lines[0]), &header); err != nil {
		t.Fatalf("line 1 is not a JSON object: %v", err)
	}
	for key, want := range map[string]any{"format_major": float64(1), "format_minor": float64(0), "agent_id": "agent-1", "boot_id": testBootID, "mode": "live"} {
		if header[key] != want {
			t.Errorf("header[%q] = %v, want %v", key, header[key], want)
		}
	}
	if _, ok := header["clock_offset_ns"]; ok {
		t.Errorf("header has clock_offset_ns although it is unknown, want the key omitted")
	}

	for i, line := range lines[1:] {
		var obj map[string]json.RawMessage
		if err := json.Unmarshal([]byte(line), &obj); err != nil {
			t.Fatalf("line %d is not a JSON object: %v", i+2, err)
		}
		for _, key := range []string{"origin", "received_at"} {
			if _, ok := obj[key]; ok {
				t.Errorf("line %d has key %q, want it absent", i+2, key)
			}
		}
		for _, key := range []string{"kind", "source", "seq", "captured_at", "data"} {
			if _, ok := obj[key]; !ok {
				t.Errorf("line %d lacks key %q", i+2, key)
			}
		}
		var capturedAt string
		if err := json.Unmarshal(obj["captured_at"], &capturedAt); err != nil || !strings.HasSuffix(capturedAt, "Z") {
			t.Errorf("line %d captured_at = %s, want a string ending in Z", i+2, obj["captured_at"])
		}
	}
}

func TestEncodeBatch_ClockOffsetWritten(t *testing.T) {
	b := validBatch(1)
	b.Header.ClockOffset = ptr(2 * time.Second)
	lines := encodedLines(t, b)
	var header map[string]any
	if err := json.Unmarshal([]byte(lines[0]), &header); err != nil {
		t.Fatalf("line 1 is not a JSON object: %v", err)
	}
	if got := header["clock_offset_ns"]; got != float64(2e9) {
		t.Errorf("header clock_offset_ns = %v, want 2e+09", got)
	}
}

func TestEncodeBatch_DoesNotCloseWriter(t *testing.T) {
	w := &closeSpyWriter{}
	if err := wire.EncodeBatch(w, validBatch(1)); err != nil {
		t.Fatalf("EncodeBatch() = %v, want nil", err)
	}
	if w.closed {
		t.Errorf("EncodeBatch closed the writer, want it left open")
	}
	if w.Len() == 0 {
		t.Errorf("EncodeBatch wrote no bytes, want a complete gzip stream")
	}
}

func bigStatus(threads, infoLen int, truncated bool) *model.MariaDBStatus {
	th := make([]model.MariaDBThread, threads)
	for i := range th {
		th[i] = model.MariaDBThread{ID: uint64(i + 1), Info: strings.Repeat("x", infoLen), Truncated: truncated}
	}
	return &model.MariaDBStatus{Availability: model.MariaDBUp, Threads: th, Complete: !truncated}
}

func TestEncodeBatch_InvalidBatchWritesNothing(t *testing.T) {
	tests := []struct {
		name  string
		build func() *wire.Batch
		check func(t *testing.T, err error)
	}{
		{"invalid header", func() *wire.Batch { b := validBatch(1); b.Header.AgentID = ""; return b },
			func(t *testing.T, err error) { requireFieldError(t, err, "header.agent_id") }},
		{"unsupported version", func() *wire.Batch { b := validBatch(1); b.Header.FormatMajor = 2; return b },
			func(t *testing.T, err error) {
				if !errors.Is(err, wire.ErrUnsupportedVersion) {
					t.Errorf("EncodeBatch() = %v, want ErrUnsupportedVersion", err)
				}
			}},
		{"empty batch", func() *wire.Batch { return validBatch(0) },
			func(t *testing.T, err error) {
				if !errors.Is(err, wire.ErrEmptyBatch) {
					t.Errorf("EncodeBatch() = %v, want ErrEmptyBatch", err)
				}
			}},
		{"bad record", func() *wire.Batch { b := validBatch(3); b.Records[1].Data = &model.MetricPoint{}; return b },
			func(t *testing.T, err error) { requireFieldError(t, err, "records[1].data.name") }},
		{"wrong origin", func() *wire.Batch {
			b := validBatch(2)
			b.Records[0].Origin, b.Records[0].Seq = model.OriginImport, 0
			return b
		}, func(t *testing.T, err error) { requireFieldError(t, err, "records[0].origin") }},
		{"sequence order", func() *wire.Batch { b := validBatch(3); b.Records[2].Seq = 2; return b },
			func(t *testing.T, err error) {
				if !errors.Is(err, wire.ErrSequence) {
					t.Errorf("EncodeBatch() = %v, want ErrSequence", err)
				}
			}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			w := &countingWriter{}
			err := wire.EncodeBatch(w, tc.build())
			if err == nil {
				t.Fatalf("EncodeBatch() = nil, want an error")
			}
			tc.check(t, err)
			if w.n != 0 {
				t.Errorf("EncodeBatch wrote %d bytes, want 0", w.n)
			}
		})
	}
}

func TestEncodeBatch_RecordTooLarge(t *testing.T) {
	b := validBatch(4)
	big := model.Record{Meta: agentMetaOf(b.Records[2]), Data: bigStatus(500, model.MaxTextBytes, false)}
	b.Records[2] = big
	if err := big.Validate(); err != nil {
		t.Fatalf("test setup: oversized record fails the model validation: %v", err)
	}

	w := &countingWriter{}
	err := wire.EncodeBatch(w, b)
	var rse *wire.RecordSizeError
	if !errors.As(err, &rse) {
		t.Fatalf("EncodeBatch() = %v, want a *wire.RecordSizeError", err)
	}
	if rse.Index != 2 {
		t.Errorf("RecordSizeError.Index = %d, want 2", rse.Index)
	}
	if want := len(envelopeLine(t, &big)); rse.Size != want {
		t.Errorf("RecordSizeError.Size = %d, want the encoded line length %d", rse.Size, want)
	}
	if want := wire.DefaultLimits().MaxLineBytes; rse.Limit != want {
		t.Errorf("RecordSizeError.Limit = %d, want %d", rse.Limit, want)
	}
	if !errors.Is(err, wire.ErrRecordTooLarge) || !errors.Is(err, wire.ErrLimitExceeded) {
		t.Errorf("EncodeBatch() = %v, want ErrRecordTooLarge and ErrLimitExceeded", err)
	}
	if w.n != 0 {
		t.Errorf("EncodeBatch wrote %d bytes, want 0", w.n)
	}
}

func agentMetaOf(r model.Record) model.Meta { return r.Meta }

func TestEncodeBatch_BatchTooLarge(t *testing.T) {
	// 17 records of about 1.03 MB each: every line fits the line limit, together they exceed 16 MiB.
	b := &wire.Batch{Header: wire.NewHeader("agent-1", testBootID, wire.ModeLive)}
	for i := range 17 {
		b.Records = append(b.Records, model.Record{
			Meta: model.Meta{Origin: model.OriginAgent, Source: "mariadb", Seq: uint64(i + 1), CapturedAt: testTime},
			Data: bigStatus(63, model.MaxTextBytes, false),
		})
	}
	w := &countingWriter{}
	err := wire.EncodeBatch(w, b)
	if !errors.Is(err, wire.ErrLimitExceeded) {
		t.Fatalf("EncodeBatch() = %v, want ErrLimitExceeded", err)
	}
	var rse *wire.RecordSizeError
	if errors.As(err, &rse) {
		t.Errorf("EncodeBatch() = %v, want the batch limit and not a single record's size error", err)
	}
	if w.n != 0 {
		t.Errorf("EncodeBatch wrote %d bytes, want 0", w.n)
	}
}

func TestEncodeBatch_WriterError(t *testing.T) {
	err := wire.EncodeBatch(failingWriter{}, validBatch(2))
	if !errors.Is(err, errWrite) {
		t.Errorf("EncodeBatch() = %v, want an error wrapping %v", err, errWrite)
	}
}

// worstText is a text of n bytes that JSON escapes as six bytes per byte.
func worstText(n int) string { return strings.Repeat("\x01", n) }

func TestCheckRecord_Valid(t *testing.T) {
	t.Run("size equals the encoded line plus newline", func(t *testing.T) {
		recs := allKindRecords(time.UTC)
		b := &wire.Batch{Header: wire.NewHeader("agent-1", testBootID, wire.ModeLive), Records: recs}
		lines := encodedLines(t, b)
		for i := range recs {
			n, err := wire.CheckRecord(&recs[i])
			if err != nil {
				t.Errorf("CheckRecord(%s) = %v, want nil", recs[i].Kind(), err)
				continue
			}
			if want := len(lines[i+1]) + 1; n != want {
				t.Errorf("CheckRecord(%s) = %d, want %d", recs[i].Kind(), n, want)
			}
		}
	})

	t.Run("status with every info cut and truncated", func(t *testing.T) {
		r := model.Record{Meta: agentMetaOf(metricRecord(1)), Data: bigStatus(500, 64, true)}
		if n, err := wire.CheckRecord(&r); err != nil || n <= 0 {
			t.Errorf("CheckRecord() = %d, %v, want a positive size and nil", n, err)
		}
	})

	longName := strings.Repeat("n", model.MaxNameBytes)
	worst := map[string]model.Record{}
	labels := make(map[string]string, model.MaxLabels)
	for i := range model.MaxLabels {
		key := string(rune('a'+i%26)) + string(rune('a'+i/26)) + strings.Repeat("k", model.MaxNameBytes-2)
		labels[key] = worstText(model.MaxShortTextBytes)
	}
	worst["metric with max labels"] = model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: longName, Seq: 1, CapturedAt: testTime},
		Data: &model.MetricPoint{Name: longName, Value: 1, Unit: longName, Labels: labels},
	}
	worst["log line"] = model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: longName, Seq: 1, CapturedAt: testTime},
		Data: &model.LogLine{Log: worstText(model.MaxShortTextBytes), Program: worstText(model.MaxShortTextBytes), PID: 1, Priority: ptr(uint8(7)), Message: worstText(model.MaxTextBytes), Truncated: true},
	}
	worst["kernel event"] = model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: longName, Seq: 1, CapturedAt: testTime},
		Data: &model.KernelEvent{
			Type: model.KernelEventOOMKill, Message: worstText(model.MaxTextBytes),
			OOMKill: &model.OOMKill{VictimPID: 1, VictimCommand: worstText(model.MaxShortTextBytes), AnonRSSBytes: ptr(uint64(1)), OOMScoreAdj: ptr(int16(1000))},
		},
	}
	for name, r := range worst {
		t.Run("worst case "+name, func(t *testing.T) {
			if err := r.Validate(); err != nil {
				t.Fatalf("test setup: worst-case record is invalid: %v", err)
			}
			n, err := wire.CheckRecord(&r)
			if err != nil {
				t.Fatalf("CheckRecord() = %v, want nil", err)
			}
			if limit := wire.DefaultLimits().MaxLineBytes; n > limit+1 {
				t.Errorf("CheckRecord() = %d, want at most %d", n, limit+1)
			}
		})
	}
}

func TestCheckRecord_Invalid(t *testing.T) {
	t.Run("invalid record returns the unprefixed field error", func(t *testing.T) {
		r := metricRecord(1)
		r.Data = &model.MetricPoint{}
		_, err := wire.CheckRecord(&r)
		requireFieldError(t, err, "data.name")
	})

	t.Run("origin import", func(t *testing.T) {
		r := metricRecord(1)
		r.Origin, r.Seq = model.OriginImport, 0
		_, err := wire.CheckRecord(&r)
		requireFieldError(t, err, "origin")
	})

	t.Run("origin backend", func(t *testing.T) {
		r := metricRecord(1)
		r.Origin, r.Seq = model.OriginBackend, 0
		_, err := wire.CheckRecord(&r)
		requireFieldError(t, err, "origin")
	})

	t.Run("record too large", func(t *testing.T) {
		r := model.Record{Meta: agentMetaOf(metricRecord(1)), Data: bigStatus(500, model.MaxTextBytes, false)}
		if err := r.Validate(); err != nil {
			t.Fatalf("test setup: oversized record fails the model validation: %v", err)
		}
		n, err := wire.CheckRecord(&r)
		var rse *wire.RecordSizeError
		if !errors.As(err, &rse) {
			t.Fatalf("CheckRecord() = %d, %v, want a *wire.RecordSizeError", n, err)
		}
		if rse.Index != -1 {
			t.Errorf("RecordSizeError.Index = %d, want -1", rse.Index)
		}
		if want := len(envelopeLine(t, &r)); rse.Size != want {
			t.Errorf("RecordSizeError.Size = %d, want %d", rse.Size, want)
		}
		if want := wire.DefaultLimits().MaxLineBytes; rse.Limit != want {
			t.Errorf("RecordSizeError.Limit = %d, want %d", rse.Limit, want)
		}
		if !errors.Is(err, wire.ErrRecordTooLarge) {
			t.Errorf("CheckRecord() = %v, want ErrRecordTooLarge", err)
		}
	})
}
