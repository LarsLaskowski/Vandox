package wire_test

import (
	"bytes"
	"compress/gzip"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	tsJSON     = `"2026-03-01T12:00:00Z"`
	hdrTail    = `"agent_id":"agent-1","boot_id":"` + testBootID + `","mode":"live"`
	headerJSON = `{"format_major":1,"format_minor":0,` + hdrTail + `}`
	okData     = `{"name":"cpu","value":1}`
	bom        = "\xef\xbb\xbf"
)

func jsonString(s string) string {
	b, _ := json.Marshal(s)
	return string(b)
}

// envelope builds a record line from raw parts.
func envelope(kind string, seq int, capturedAt, data string) string {
	return fmt.Sprintf(`{"kind":%s,"source":"proc.stat","seq":%d,"captured_at":%s,"data":%s}`, jsonString(kind), seq, capturedAt, data)
}

func metricLine(seq int) string { return envelope("metric", seq, tsJSON, okData) }

func kindLine(kind, data string) string { return envelope(kind, 1, tsJSON, data) }

func gzipString(t *testing.T, s string) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	if _, err := zw.Write([]byte(s)); err != nil {
		t.Fatalf("gzip write = %v, want nil", err)
	}
	if err := zw.Close(); err != nil {
		t.Fatalf("gzip close = %v, want nil", err)
	}
	return buf.Bytes()
}

func joinLines(lines ...string) string { return strings.Join(lines, "\n") + "\n" }

func stream(t *testing.T, lines ...string) []byte {
	t.Helper()
	return gzipString(t, joinLines(lines...))
}

// withHeader returns a stream of the standard header followed by the given lines.
func withHeader(t *testing.T, lines ...string) []byte {
	t.Helper()
	return stream(t, append([]string{headerJSON}, lines...)...)
}

// padded appends spaces (JSON whitespace) to line until it is exactly n bytes long.
func padded(t *testing.T, line string, n int) string {
	t.Helper()
	if len(line) > n {
		t.Fatalf("test setup: line has %d bytes, more than %d", len(line), n)
	}
	return line + strings.Repeat(" ", n-len(line))
}

// decodeAll reads a whole stream; the error is nil when the stream ended with io.EOF.
func decodeAll(t *testing.T, data []byte, lim wire.Limits) ([]model.Record, error) {
	t.Helper()
	d, err := wire.NewDecoder(bytes.NewReader(data), lim)
	if err != nil {
		return nil, err
	}
	t.Cleanup(func() { _ = d.Close() })
	var recs []model.Record
	for range 100000 {
		rec, err := d.Next()
		if errors.Is(err, io.EOF) {
			return recs, nil
		}
		if err != nil {
			return recs, err
		}
		recs = append(recs, rec)
	}
	t.Fatalf("decoder did not end the stream within 100000 records")
	return recs, nil
}

// want describes an expected decode error.
type want struct {
	is          error  // sentinel the error must wrap
	field       string // exact *model.FieldError field path
	fieldSuffix string // suffix of the *model.FieldError field path
	reason      string // exact *model.FieldError reason
	line        int    // *wire.DecodeError line, 0 = not checked
}

func (w want) check(t *testing.T, err error) {
	t.Helper()
	if err == nil {
		t.Fatalf("got nil error, want %+v", w)
	}
	if w.is != nil && !errors.Is(err, w.is) {
		t.Errorf("error = %v, want errors.Is(err, %v)", err, w.is)
	}
	if w.field != "" || w.fieldSuffix != "" || w.reason != "" {
		w.checkFieldError(t, err)
	}
	if w.line > 0 {
		w.checkLine(t, err)
	}
}

func (w want) checkFieldError(t *testing.T, err error) {
	t.Helper()
	var fe *model.FieldError
	if !errors.As(err, &fe) {
		t.Fatalf("error = %T (%v), want a *model.FieldError", err, err)
	}
	if !errors.Is(err, model.ErrInvalid) {
		t.Errorf("error = %v, want errors.Is(err, model.ErrInvalid)", err)
	}
	if w.field != "" && fe.Field != w.field {
		t.Errorf("field = %q, want %q (reason %q)", fe.Field, w.field, fe.Reason)
	}
	if w.fieldSuffix != "" && !strings.HasSuffix(fe.Field, w.fieldSuffix) {
		t.Errorf("field = %q, want suffix %q", fe.Field, w.fieldSuffix)
	}
	if w.reason != "" && fe.Reason != w.reason {
		t.Errorf("reason = %q, want %q", fe.Reason, w.reason)
	}
}

func (w want) checkLine(t *testing.T, err error) {
	t.Helper()
	var de *wire.DecodeError
	if !errors.As(err, &de) {
		t.Fatalf("error = %T (%v), want a *wire.DecodeError", err, err)
	}
	if de.Line != w.line {
		t.Errorf("DecodeError.Line = %d, want %d (%v)", de.Line, w.line, err)
	}
}

func malformed(line int) want { return want{is: wire.ErrMalformed, line: line} }

func TestNewDecoder_RejectsUnsupportedVersion(t *testing.T) {
	cases := []struct {
		name   string
		header string
	}{
		{"major 2", `{"format_major":2,"format_minor":0,` + hdrTail + `}`},
		{"major 3", `{"format_major":3,` + hdrTail + `}`},
		{"major 0", `{"format_major":0,` + hdrTail + `}`},
		{"major negative", `{"format_major":-1,` + hdrTail + `}`},
		{"major missing", `{"format_minor":0,` + hdrTail + `}`},
		{"major null", `{"format_major":null,` + hdrTail + `}`},
		{"line is null", `null`},
		{"major 2 with incompatible header shape", `{"format_major":2,"agent_id":{"x":1},"boot_id":[1],"mode":5}`},
		{"upper-case duplicate wins", `{"format_major":1,"FORMAT_MAJOR":2,` + hdrTail + `}`},
		{"later duplicate wins", `{"format_major":1,"format_major":2,` + hdrTail + `}`},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := wire.NewDecoder(bytes.NewReader(stream(t, tc.header, metricLine(1))), wire.DefaultLimits())
			want{is: wire.ErrUnsupportedVersion, line: 1}.check(t, err)
		})
	}

	t.Run("folded duplicate with major 1 wins", func(t *testing.T) {
		header := `{"format_major":2,"Format_Major":1,"format_minor":0,` + hdrTail + `}`
		d, err := wire.NewDecoder(bytes.NewReader(stream(t, header, metricLine(1))), wire.DefaultLimits())
		if err != nil {
			t.Fatalf("NewDecoder() = %v, want nil", err)
		}
		if got := d.Header().FormatMajor; got != 1 {
			t.Errorf("Header().FormatMajor = %d, want 1", got)
		}
	})
}

func TestDecoder_AcceptsNewerMinorWithUnknownKeys(t *testing.T) {
	header := `{"format_major":1,"format_minor":7,"future_header":{"a":[1,2,{"b":null}]},` + hdrTail + `}`
	record := `{"kind":"metric","source":"proc.stat","seq":1,"captured_at":` + tsJSON + `,"future_envelope":[1],"data":{"name":"cpu","value":2.5,"future_payload":{"x":"y"}}}`
	d, err := wire.NewDecoder(bytes.NewReader(stream(t, header, record)), wire.DefaultLimits())
	if err != nil {
		t.Fatalf("NewDecoder() = %v, want nil", err)
	}
	if got := d.Header().FormatMinor; got != 7 {
		t.Errorf("Header().FormatMinor = %d, want 7", got)
	}
	rec, err := d.Next()
	if err != nil {
		t.Fatalf("Next() = %v, want nil", err)
	}
	mp, ok := rec.Data.(*model.MetricPoint)
	if !ok || mp.Name != "cpu" || mp.Value != 2.5 {
		t.Errorf("Next() data = %#v, want metric cpu = 2.5", rec.Data)
	}
	if _, err := d.Next(); !errors.Is(err, io.EOF) {
		t.Errorf("second Next() = %v, want io.EOF", err)
	}
}

func TestNewDecoder_MalformedHeader(t *testing.T) {
	notGzip := []byte(headerJSON + "\n" + metricLine(1) + "\n")
	cases := []struct {
		name string
		data []byte
		w    want
	}{
		{"plain JSON lines", notGzip, malformed(1)},
		{"zstd magic", []byte{0x28, 0xb5, 0x2f, 0xfd, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07}, malformed(1)},
		{"empty input", nil, malformed(1)},
		{"one byte", []byte{0x1f}, malformed(1)},
		{"empty gzip stream", gzipString(t, ""), malformed(1)},
		{"empty first line", stream(t, "", metricLine(1)), malformed(1)},
		{"whitespace-only first line", stream(t, " \t ", metricLine(1)), malformed(1)},
		{"BOM before header", stream(t, bom+headerJSON, metricLine(1)), malformed(1)},
		{"header is an array", stream(t, `[]`, metricLine(1)), malformed(1)},
		{"header is a string", stream(t, `"x"`, metricLine(1)), malformed(1)},
		{"header is a number", stream(t, `5`, metricLine(1)), malformed(1)},
		{"two header objects on one line", stream(t, headerJSON+headerJSON, metricLine(1)), malformed(1)},
		{"header with trailing comma", stream(t, `{"format_major":1,}`, metricLine(1)), malformed(1)},
		{"major as 1.0", stream(t, `{"format_major":1.0,`+hdrTail+`}`, metricLine(1)), malformed(1)},
		{"major as 1e0", stream(t, `{"format_major":1e0,`+hdrTail+`}`, metricLine(1)), malformed(1)},
		{"major as string", stream(t, `{"format_major":"1",`+hdrTail+`}`, metricLine(1)), malformed(1)},
		{"major 1 with incompatible shape", stream(t, `{"format_major":1,"format_minor":0,"agent_id":{"x":1},"boot_id":"`+testBootID+`","mode":"live"}`, metricLine(1)), malformed(1)},
		{"minor as string", stream(t, `{"format_major":1,"format_minor":"0",`+hdrTail+`}`, metricLine(1)), malformed(1)},

		{"agent id empty", stream(t, `{"format_major":1,"format_minor":0,"agent_id":"","boot_id":"`+testBootID+`","mode":"live"}`, metricLine(1)), want{is: model.ErrInvalid, fieldSuffix: "agent_id", line: 1}},
		{"agent id with space", stream(t, `{"format_major":1,"agent_id":"a b","boot_id":"`+testBootID+`","mode":"live"}`, metricLine(1)), want{is: model.ErrInvalid, fieldSuffix: "agent_id", line: 1}},
		{"agent id with trailing newline", stream(t, `{"format_major":1,"agent_id":"a\n","boot_id":"`+testBootID+`","mode":"live"}`, metricLine(1)), want{is: model.ErrInvalid, fieldSuffix: "agent_id", line: 1}},
		{"boot id upper case", stream(t, `{"format_major":1,"agent_id":"a","boot_id":"`+strings.ToUpper(testBootID)+`","mode":"live"}`, metricLine(1)), want{is: model.ErrInvalid, fieldSuffix: "boot_id", line: 1}},
		{"boot id with braces", stream(t, `{"format_major":1,"agent_id":"a","boot_id":"{`+testBootID+`}","mode":"live"}`, metricLine(1)), want{is: model.ErrInvalid, fieldSuffix: "boot_id", line: 1}},
		{"boot id as urn", stream(t, `{"format_major":1,"agent_id":"a","boot_id":"urn:uuid:`+testBootID+`","mode":"live"}`, metricLine(1)), want{is: model.ErrInvalid, fieldSuffix: "boot_id", line: 1}},
		{"boot id with trailing newline", stream(t, `{"format_major":1,"agent_id":"a","boot_id":"`+testBootID+`\n","mode":"live"}`, metricLine(1)), want{is: model.ErrInvalid, fieldSuffix: "boot_id", line: 1}},
		{"mode capitalized", stream(t, `{"format_major":1,"agent_id":"a","boot_id":"`+testBootID+`","mode":"Live"}`, metricLine(1)), want{is: model.ErrInvalid, fieldSuffix: "mode", line: 1}},
		{"mode missing", stream(t, `{"format_major":1,"agent_id":"a","boot_id":"`+testBootID+`"}`, metricLine(1)), want{is: model.ErrInvalid, fieldSuffix: "mode", line: 1}},
		{"minor negative", stream(t, `{"format_major":1,"format_minor":-1,`+hdrTail+`}`, metricLine(1)), want{is: model.ErrInvalid, fieldSuffix: "format_minor", line: 1}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := wire.NewDecoder(bytes.NewReader(tc.data), wire.DefaultLimits())
			tc.w.check(t, err)
		})
	}
}

func TestDecoder_RejectsMalformedRecords(t *testing.T) {
	const zoneMsg = "zone not allowed"
	cases := []struct {
		name  string
		lines []string
		w     want
	}{
		// Syntax.
		{"two values on one line", []string{metricLine(1) + metricLine(2)}, malformed(2)},
		{"lone carriage return as separator", []string{metricLine(1) + "\r" + metricLine(2)}, malformed(2)},
		{"comment after the object", []string{metricLine(1) + " // comment"}, malformed(2)},
		{"trailing comma", []string{metricLine(1) + ","}, malformed(2)},
		{"NaN value", []string{envelope("metric", 1, tsJSON, `{"name":"cpu","value":NaN}`)}, malformed(2)},
		{"Infinity value", []string{envelope("metric", 1, tsJSON, `{"name":"cpu","value":Infinity}`)}, malformed(2)},
		{"single quotes", []string{`{'kind':'metric'}`}, malformed(2)},
		{"line is an array", []string{`[]`}, malformed(2)},
		{"line is a string", []string{`"x"`}, malformed(2)},
		{"line is a number", []string{`5`}, malformed(2)},
		{"BOM at line start", []string{bom + metricLine(1)}, malformed(2)},
		{"empty line", []string{"", metricLine(1)}, malformed(2)},
		{"whitespace-only line", []string{" \t ", metricLine(1)}, malformed(2)},
		{"empty line after a record", []string{metricLine(1), ""}, malformed(3)},
		{"data is a number", []string{envelope("metric", 1, tsJSON, `5`)}, malformed(2)},
		{"data is an array", []string{envelope("metric", 1, tsJSON, `[]`)}, malformed(2)},
		{"data is a string", []string{envelope("metric", 1, tsJSON, `"x"`)}, malformed(2)},

		// Numbers.
		{"negative seq", []string{envelope("metric", -1, tsJSON, okData)}, malformed(2)},
		{"fractional seq", []string{strings.Replace(metricLine(1), `"seq":1`, `"seq":1.5`, 1)}, malformed(2)},
		{"exponent seq", []string{strings.Replace(metricLine(1), `"seq":1`, `"seq":1e0`, 1)}, malformed(2)},
		{"seq of 2^64", []string{strings.Replace(metricLine(1), `"seq":1`, `"seq":18446744073709551616`, 1)}, malformed(2)},
		{"pid above int32", []string{kindLine("process_snapshot", `{"processes":[{"pid":2147483648,"command":"x"}]}`)}, malformed(2)},
		{"fractional pid", []string{kindLine("process_snapshot", `{"processes":[{"pid":1.5,"command":"x"}]}`)}, malformed(2)},
		{"exponent pid", []string{kindLine("process_snapshot", `{"processes":[{"pid":1e0,"command":"x"}]}`)}, malformed(2)},
		{"negative rss_bytes", []string{kindLine("process_snapshot", `{"processes":[{"pid":1,"command":"x","rss_bytes":-1}]}`)}, malformed(2)},
		{"float out of range", []string{envelope("metric", 1, tsJSON, `{"name":"cpu","value":1e400}`)}, malformed(2)},
		{"value as string", []string{envelope("metric", 1, tsJSON, `{"name":"cpu","value":"1"}`)}, malformed(2)},
		{"status value as string", []string{kindLine("mariadb_status", `{"availability":"up","status":{"evil\nkey<script>":"x"}}`)}, malformed(2)},

		// Times.
		{"time with space instead of T", []string{envelope("metric", 1, `"2026-03-01 12:00:00Z"`, okData)}, malformed(2)},
		{"time with lower-case t and z", []string{envelope("metric", 1, `"2026-03-01t12:00:00z"`, okData)}, malformed(2)},
		{"time not RFC 3339", []string{envelope("metric", 1, `"yesterday"`, okData)}, malformed(2)},
		{"time as number", []string{envelope("metric", 1, `1772366400`, okData)}, malformed(2)},
		{"time null", []string{envelope("metric", 1, `null`, okData)}, want{is: model.ErrInvalid, field: "captured_at", line: 2}},
		{"time zero", []string{envelope("metric", 1, `"0001-01-01T00:00:00Z"`, okData)}, want{is: model.ErrInvalid, field: "captured_at", line: 2}},
		{"time with offset", []string{envelope("metric", 1, `"2026-03-01T14:00:00+02:00"`, okData)}, want{is: model.ErrInvalid, field: "captured_at", line: 2}},
		{"time missing", []string{`{"kind":"metric","source":"proc.stat","seq":1,"data":` + okData + `}`}, want{is: model.ErrInvalid, field: "captured_at", line: 2}},

		// Kinds and data.
		{"line null is an empty kind", []string{`null`}, want{is: wire.ErrUnknownKind, line: 2}},
		{"kind wrong case", []string{envelope("Metric", 1, tsJSON, okData)}, want{is: wire.ErrUnknownKind, line: 2}},
		{"kind unknown", []string{envelope("backup_run", 1, tsJSON, `{}`)}, want{is: wire.ErrUnknownKind, line: 2}},
		{"kind empty", []string{envelope("", 1, tsJSON, okData)}, want{is: wire.ErrUnknownKind, line: 2}},
		{"kind missing", []string{`{"source":"proc.stat","seq":1,"captured_at":` + tsJSON + `,"data":` + okData + `}`}, want{is: wire.ErrUnknownKind, line: 2}},
		{"kind null", []string{`{"kind":null,"source":"proc.stat","seq":1,"captured_at":` + tsJSON + `,"data":` + okData + `}`}, want{is: wire.ErrUnknownKind, line: 2}},
		{"data missing", []string{`{"kind":"metric","source":"proc.stat","seq":1,"captured_at":` + tsJSON + `}`}, want{is: model.ErrInvalid, field: "data", line: 2}},
		{"data null", []string{envelope("metric", 1, tsJSON, `null`)}, want{is: model.ErrInvalid, field: "data", line: 2}},

		// Field rules reached through the decoder.
		{"seq zero", []string{envelope("metric", 0, tsJSON, okData)}, want{is: model.ErrInvalid, field: "seq", line: 2}},
		{"source with escaped NUL", []string{`{"kind":"metric","source":"a\u0000","seq":1,"captured_at":` + tsJSON + `,"data":` + okData + `}`}, want{is: model.ErrInvalid, field: "source", line: 2}},
		{"source with trailing newline", []string{`{"kind":"metric","source":"a\n","seq":1,"captured_at":` + tsJSON + `,"data":` + okData + `}`}, want{is: model.ErrInvalid, field: "source", line: 2}},
		{"metric name with newline", []string{envelope("metric", 1, tsJSON, `{"name":"a\nb","value":1}`)}, want{is: model.ErrInvalid, field: "data.name", line: 2}},
		{"label key with newline", []string{envelope("metric", 1, tsJSON, `{"name":"cpu","value":1,"labels":{"a\nb":"v"}}`)}, want{is: model.ErrInvalid, field: `data.labels["a\nb"]`, line: 2}},
		{"status key with markup", []string{kindLine("mariadb_status", `{"availability":"up","status":{"evil\nkey<script>":1}}`)}, want{is: model.ErrInvalid, field: `data.status["evil\nkey<script>"]`, line: 2}},

		// Addresses.
		{"remote addr empty", []string{kindLine("connection_snapshot", `{"remotes":[{"addr":"","count":1}]}`)}, want{is: model.ErrInvalid, field: "data.remotes[0].addr", line: 2}},
		{"remote addr with zone", []string{kindLine("connection_snapshot", `{"remotes":[{"addr":"fe80::1%eth0","count":1}]}`)}, want{is: model.ErrInvalid, field: "data.remotes[0].addr", reason: zoneMsg, line: 2}},
		{"remote addr with newline zone", []string{kindLine("connection_snapshot", `{"remotes":[{"addr":"fe80::1%\n<b>x]:","count":1}]}`)}, want{is: model.ErrInvalid, field: "data.remotes[0].addr", reason: zoneMsg, line: 2}},
		{"remote addr IPv4-mapped with zone", []string{kindLine("connection_snapshot", `{"remotes":[{"addr":"::ffff:1.2.3.4%eth0","count":1}]}`)}, want{is: model.ErrInvalid, field: "data.remotes[0].addr", reason: zoneMsg, line: 2}},
		{"listener local with 5000-byte zone", []string{kindLine("connection_snapshot", `{"listeners":[{"proto":"tcp6","local":"[fe80::1%`+strings.Repeat("z", 5000)+`]:80"}]}`)}, want{is: model.ErrInvalid, field: "data.listeners[0].local", reason: zoneMsg, line: 2}},
		{"connection remote with bracket zone", []string{kindLine("connection_snapshot", `{"connections":[{"proto":"tcp6","local":"[::1]:80","remote":"[fe80::1%x]:]:80","state":"ESTABLISHED"}]}`)}, want{is: model.ErrInvalid, field: "data.connections[0].remote", reason: zoneMsg, line: 2}},
		{"connection local with zone", []string{kindLine("connection_snapshot", `{"connections":[{"proto":"tcp6","local":"[fe80::1%eth0]:80","remote":"[::1]:81","state":"ESTABLISHED"}]}`)}, want{is: model.ErrInvalid, field: "data.connections[0].local", reason: zoneMsg, line: 2}},
		{"listener local hostname", []string{kindLine("connection_snapshot", `{"listeners":[{"proto":"tcp","local":"host:80"}]}`)}, malformed(2)},
		{"listener local without port", []string{kindLine("connection_snapshot", `{"listeners":[{"proto":"tcp","local":"1.2.3.4"}]}`)}, malformed(2)},
		{"remote addr with empty zone", []string{kindLine("connection_snapshot", `{"remotes":[{"addr":"fe80::1%","count":1}]}`)}, malformed(2)},
		{"remote addr IPv4 with zone", []string{kindLine("connection_snapshot", `{"remotes":[{"addr":"1.2.3.4%eth0","count":1}]}`)}, malformed(2)},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			recs, err := decodeAll(t, withHeader(t, tc.lines...), wire.DefaultLimits())
			tc.w.check(t, err)
			if len(recs) != 0 && tc.name != "empty line after a record" {
				t.Errorf("decoded %d records before the error, want 0", len(recs))
			}
		})
	}

	t.Run("empty line after a record keeps the earlier record", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLine(1), ""), wire.DefaultLimits())
		malformed(3).check(t, err)
		if len(recs) != 1 {
			t.Errorf("decoded %d records before the error, want 1", len(recs))
		}
	})
}

func TestDecoder_UnknownKindText(t *testing.T) {
	long := strings.Repeat("k", 2500) + "\n" + strings.Repeat("k", 2499)
	cases := []struct {
		name string
		kind string
	}{
		{"wrong case", "Metric"},
		{"unknown", "backup_run"},
		{"empty", ""},
		{"5000 bytes with newline", long},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := decodeAll(t, withHeader(t, envelope(tc.kind, 1, tsJSON, okData)), wire.DefaultLimits())
			want{is: wire.ErrUnknownKind, line: 2}.check(t, err)
			wantText := "wire: line 2: unknown record kind: " + model.QuoteName(tc.kind)
			if got := err.Error(); got != wantText {
				t.Errorf("Error() = %q, want %q", got, wantText)
			}
			if strings.ContainsAny(err.Error(), "\n\r") {
				t.Errorf("Error() holds a line break: %q", err.Error())
			}
		})
	}
}

type recordCheck func(t *testing.T, recs []model.Record)

// metricSeq checks that the first record is a metric with the given seq.
func metricSeq(seq uint64) recordCheck {
	return func(t *testing.T, recs []model.Record) {
		t.Helper()
		if recs[0].Kind() != model.KindMetric || recs[0].Seq != seq {
			t.Errorf("record = kind %q seq %d, want metric seq %d", recs[0].Kind(), recs[0].Seq, seq)
		}
	}
}

func checkSeq3(t *testing.T, recs []model.Record) {
	t.Helper()
	if recs[0].Seq != 3 {
		t.Errorf("Seq = %d, want 3", recs[0].Seq)
	}
}

func checkOriginAgent(t *testing.T, recs []model.Record) {
	t.Helper()
	if recs[0].Origin != model.OriginAgent {
		t.Errorf("Origin = %q, want %q", recs[0].Origin, model.OriginAgent)
	}
}

func checkZeroOffsetTimes(t *testing.T, recs []model.Record) {
	t.Helper()
	for i, r := range recs {
		if _, off := r.CapturedAt.Zone(); off != 0 {
			t.Errorf("record %d CapturedAt offset = %d, want 0", i, off)
		}
	}
	if want := time.Date(2026, 3, 1, 12, 0, 0, 123456789, time.UTC); !recs[2].CapturedAt.Equal(want) {
		t.Errorf("record 2 CapturedAt = %v, want %v", recs[2].CapturedAt, want)
	}
}

// logMessage checks the message of the first record, a log line.
func logMessage(want string) recordCheck {
	return func(t *testing.T, recs []model.Record) {
		t.Helper()
		if got := recs[0].Data.(*model.LogLine).Message; got != want {
			t.Errorf("Message = %q, want %q", got, want)
		}
	}
}

func checkKindMetric(t *testing.T, recs []model.Record) {
	t.Helper()
	if recs[0].Kind() != model.KindMetric {
		t.Errorf("Kind() = %q, want %q", recs[0].Kind(), model.KindMetric)
	}
}

type acceptedCase struct {
	name  string
	data  []byte
	count int
	check recordCheck
}

func acceptedFormCases(t *testing.T) []acceptedCase {
	t.Helper()
	const keyHeader = `{"FORMAT_MAJOR":1,"Format_Minor":0,"AGENT_ID":"agent-1","Boot_ID":"` + testBootID + `","MODE":"live"}`
	return []acceptedCase{
		{"CRLF line ends", gzipString(t, headerJSON+"\r\n"+metricLine(1)+"\r\n"+metricLine(2)+"\r\n"), 2, nil},
		{"last line without newline", gzipString(t, headerJSON+"\n"+metricLine(1)), 1, nil},
		{"spaces and tabs around objects", gzipString(t, " \t"+headerJSON+"\t \n \t"+metricLine(1)+" \t\n"), 1, nil},
		{"keys in other case", stream(t, keyHeader, `{"KIND":"metric","Source":"proc.stat","SEQ":4,"Captured_At":`+tsJSON+`,"DATA":{"NAME":"cpu","Value":1}}`), 1, metricSeq(4)},
		{"duplicate keys, last wins", stream(t, headerJSON, `{"kind":"metric","source":"proc.stat","seq":9,"seq":3,"captured_at":`+tsJSON+`,"data":{"name":"x y","name":"cpu","value":1}}`), 1, checkSeq3},
		{"unknown keys at any depth", stream(t, headerJSON, `{"kind":"metric","source":"proc.stat","seq":1,"captured_at":`+tsJSON+`,"extra":{"a":[1,{"b":null}]},"data":{"name":"cpu","value":1,"extra":[[[]]]}}`), 1, nil},
		{"origin and received_at keys are ignored", stream(t, headerJSON, `{"kind":"metric","origin":"backend","received_at":"2026-03-02T00:00:00Z","source":"proc.stat","seq":1,"captured_at":`+tsJSON+`,"data":`+okData+`}`), 1, checkOriginAgent},
		{"time forms with zero offset", withHeader(t,
			envelope("metric", 1, `"2026-03-01T12:00:00+00:00"`, okData),
			envelope("metric", 2, `"2026-03-01T12:00:00-00:00"`, okData),
			envelope("metric", 3, `"2026-03-01T12:00:00.123456789Z"`, okData)), 3, checkZeroOffsetTimes},
		{"invalid UTF-8 in a string", withHeader(t, kindLine("log_line", "{\"log\":\"journal\",\"message\":\"a\xffb\"}")), 1, logMessage("a\ufffdb")},
		{"escaped control characters in a text", withHeader(t, kindLine("log_line", `{"log":"journal","message":"a\u0000b\n"}`)), 1, logMessage("a\x00b\n")},
		{"IPv4-mapped address without zone", withHeader(t, kindLine("connection_snapshot", `{"remotes":[{"addr":"::ffff:1.2.3.4","count":1}]}`)), 1, nil},
		{"empty connection snapshot", withHeader(t, kindLine("connection_snapshot", `{"complete":true}`)), 1, nil},
		{"Kelvin sign and long s in keys", withHeader(t, "{\"Kind\":\"metric\",\"source\":\"proc.stat\",\"\u017feq\":5,\"captured_at\":"+tsJSON+",\"data\":"+okData+"}"), 1, metricSeq(5)},
		{"folded duplicate key wins", withHeader(t, "{\"kind\":\"gap\",\"Kind\":\"metric\",\"source\":\"proc.stat\",\"seq\":1,\"captured_at\":"+tsJSON+",\"data\":"+okData+"}"), 1, checkKindMetric},
	}
}

func TestDecoder_AcceptedForms(t *testing.T) {
	for _, tc := range acceptedFormCases(t) {
		t.Run(tc.name, func(t *testing.T) {
			recs, err := decodeAll(t, tc.data, wire.DefaultLimits())
			if err != nil {
				t.Fatalf("decoding = %v, want nil", err)
			}
			if len(recs) != tc.count {
				t.Fatalf("decoded %d records, want %d", len(recs), tc.count)
			}
			if tc.check != nil {
				tc.check(t, recs)
			}
		})
	}
}

// requireDecoded fails the test unless decoding produced exactly n records and no error.
func requireDecoded(t *testing.T, label string, recs []model.Record, err error, n int) {
	t.Helper()
	if err != nil || len(recs) != n {
		t.Errorf("%sdecoding = %d records, %v, want %d records and nil", label, len(recs), err, n)
	}
}

// requireRecordCount fails the test unless exactly n records were decoded before the error.
func requireRecordCount(t *testing.T, recs []model.Record, n int) {
	t.Helper()
	if len(recs) != n {
		t.Errorf("decoded %d records before the error, want %d", len(recs), n)
	}
}

// requireLimitExceeded fails the test unless err is ErrLimitExceeded (on line, when line > 0).
func requireLimitExceeded(t *testing.T, err error, line int) {
	t.Helper()
	want{is: wire.ErrLimitExceeded, line: line}.check(t, err)
}

func smallLimits(line, rec int, batch int64) wire.Limits {
	return wire.Limits{MaxLineBytes: line, MaxBatchBytes: batch, MaxRecords: rec}
}

// metricLines returns n metric lines with seq 1..n.
func metricLines(n int) []string {
	lines := make([]string, n)
	for i := range lines {
		lines[i] = metricLine(i + 1)
	}
	return lines
}

// paddedMetricLines returns n metric lines with seq 1..n, each padded to size bytes.
func paddedMetricLines(t *testing.T, n, size int) []string {
	t.Helper()
	lines := make([]string, n)
	for i := range lines {
		lines[i] = padded(t, metricLine(i+1), size)
	}
	return lines
}

func TestDecoder_LimitsLine(t *testing.T) {
	small := smallLimits

	t.Run("line of exactly MaxLineBytes is read", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, padded(t, metricLine(1), 400)), small(400, 10, 1<<20))
		requireDecoded(t, "", recs, err, 1)
	})

	t.Run("line one byte over MaxLineBytes", func(t *testing.T) {
		_, err := decodeAll(t, withHeader(t, padded(t, metricLine(1), 401)), small(400, 10, 1<<20))
		requireLimitExceeded(t, err, 2)
	})

	t.Run("carriage return counts towards the line", func(t *testing.T) {
		recs, err := decodeAll(t, gzipString(t, headerJSON+"\n"+padded(t, metricLine(1), 399)+"\r\n"), small(400, 10, 1<<20))
		requireDecoded(t, "399 bytes plus CR: ", recs, err, 1)
		_, err = decodeAll(t, gzipString(t, headerJSON+"\n"+padded(t, metricLine(1), 400)+"\r\n"), small(400, 10, 1<<20))
		requireLimitExceeded(t, err, 0)
	})

	t.Run("header line over MaxLineBytes", func(t *testing.T) {
		_, err := decodeAll(t, stream(t, padded(t, headerJSON, 500), metricLine(1)), small(400, 10, 1<<20))
		requireLimitExceeded(t, err, 1)
	})

	t.Run("unterminated last line of exactly MaxLineBytes is read", func(t *testing.T) {
		data := gzipString(t, headerJSON+"\n"+padded(t, metricLine(1), 400))
		recs, err := decodeAll(t, data, small(400, 10, 1<<20))
		requireDecoded(t, "", recs, err, 1)
	})

	t.Run("unterminated last line one byte over MaxLineBytes", func(t *testing.T) {
		data := gzipString(t, headerJSON+"\n"+padded(t, metricLine(1), 401))
		_, err := decodeAll(t, data, small(400, 10, 1<<20))
		requireLimitExceeded(t, err, 0)
	})

	t.Run("unterminated last line with carriage return counts towards the line", func(t *testing.T) {
		data := gzipString(t, headerJSON+"\n"+padded(t, metricLine(1), 400)+"\r")
		_, err := decodeAll(t, data, small(400, 10, 1<<20))
		requireLimitExceeded(t, err, 0)
	})

	t.Run("MaxLineBytes of math.MaxInt decodes a valid batch", func(t *testing.T) {
		lim := wire.Limits{MaxLineBytes: math.MaxInt, MaxBatchBytes: 1 << 20, MaxRecords: 10}
		recs, err := decodeAll(t, withHeader(t, metricLine(1), metricLine(2)), lim)
		requireDecoded(t, "", recs, err, 2)
	})
}

func TestDecoder_LimitsBatchAndRecords(t *testing.T) {
	small := smallLimits

	t.Run("decompressed size over MaxBatchBytes", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLines(100)...), small(1<<20, 1000, 2000))
		requireLimitExceeded(t, err, 0)
		if len(recs) >= 100 {
			t.Errorf("decoded %d records, want fewer than 100 before the limit", len(recs))
		}
	})

	t.Run("small gzip of a padded line over MaxBatchBytes", func(t *testing.T) {
		data := withHeader(t, padded(t, metricLine(1), 100000))
		if len(data) > 1000 {
			t.Fatalf("test setup: gzip stream has %d bytes, want a small one", len(data))
		}
		_, err := decodeAll(t, data, small(1<<20, 10, 10000))
		requireLimitExceeded(t, err, 0)
	})

	t.Run("exactly MaxRecords records", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLines(3)...), small(1<<20, 3, 1<<20))
		requireDecoded(t, "", recs, err, 3)
	})

	t.Run("MaxRecords plus one records", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLines(4)...), small(1<<20, 3, 1<<20))
		requireLimitExceeded(t, err, 0)
		requireRecordCount(t, recs, 3)
	})
}

func TestDecoder_LimitsDefaults(t *testing.T) {
	t.Run("zero and negative fields mean the defaults", func(t *testing.T) {
		for name, lim := range map[string]wire.Limits{
			"zero":     {},
			"negative": {MaxLineBytes: -1, MaxBatchBytes: -1, MaxRecords: -1},
		} {
			recs, err := decodeAll(t, withHeader(t, metricLine(1), metricLine(2)), lim)
			requireDecoded(t, name+" limits: ", recs, err, 2)
		}
	})

	t.Run("default line limit applies when the field is zero", func(t *testing.T) {
		def := wire.DefaultLimits()
		lim := wire.Limits{MaxRecords: 5}
		recs, err := decodeAll(t, withHeader(t, padded(t, metricLine(1), def.MaxLineBytes)), lim)
		requireDecoded(t, fmt.Sprintf("line of %d bytes: ", def.MaxLineBytes), recs, err, 1)
		_, err = decodeAll(t, withHeader(t, padded(t, metricLine(1), def.MaxLineBytes+1)), lim)
		requireLimitExceeded(t, err, 0)
	})

	t.Run("default record limit applies when the field is zero", func(t *testing.T) {
		limit := wire.DefaultLimits().MaxRecords
		recs, err := decodeAll(t, withHeader(t, metricLines(limit+1)...), wire.Limits{})
		requireLimitExceeded(t, err, 0)
		requireRecordCount(t, recs, limit)
	})

	t.Run("default batch limit applies when the field is zero", func(t *testing.T) {
		lines := paddedMetricLines(t, 17, wire.DefaultLimits().MaxLineBytes)
		_, err := decodeAll(t, withHeader(t, lines...), wire.Limits{})
		requireLimitExceeded(t, err, 0)
	})
}

// requireMalformedAfter decodes data and expects ErrMalformed after exactly n records.
func requireMalformedAfter(t *testing.T, data []byte, n int) {
	t.Helper()
	recs, err := decodeAll(t, data, wire.DefaultLimits())
	want{is: wire.ErrMalformed}.check(t, err)
	requireRecordCount(t, recs, n)
}

// newTestDecoder opens a decoder over data with the default limits.
func newTestDecoder(t *testing.T, data []byte) *wire.Decoder {
	t.Helper()
	d, err := wire.NewDecoder(bytes.NewReader(data), wire.DefaultLimits())
	if err != nil {
		t.Fatalf("NewDecoder() = %v, want nil", err)
	}
	return d
}

func TestDecoder_Integrity(t *testing.T) {
	lines := []string{metricLine(1), metricLine(2), metricLine(3), metricLine(4), metricLine(5)}
	intact := withHeader(t, lines...)
	damaged := func(f func(b []byte) []byte) []byte {
		return f(append([]byte(nil), intact...))
	}

	t.Run("corrupted CRC", func(t *testing.T) {
		requireMalformedAfter(t, damaged(func(b []byte) []byte { b[len(b)-8] ^= 0xff; return b }), 5)
	})

	t.Run("corrupted ISIZE", func(t *testing.T) {
		requireMalformedAfter(t, damaged(func(b []byte) []byte { b[len(b)-1] ^= 0xff; return b }), 5)
	})

	t.Run("missing trailer", func(t *testing.T) {
		requireMalformedAfter(t, damaged(func(b []byte) []byte { return b[:len(b)-8] }), 5)
	})

	t.Run("truncated inside the compressed data", func(t *testing.T) {
		data := damaged(func(b []byte) []byte { return b[:len(b)-20] })
		recs, err := decodeAll(t, data, wire.DefaultLimits())
		want{is: wire.ErrMalformed}.check(t, err)
		if len(recs) > 5 {
			t.Errorf("decoded %d records, want at most 5", len(recs))
		}
	})

	t.Run("trailing non-gzip bytes", func(t *testing.T) {
		requireMalformedAfter(t, damaged(func(b []byte) []byte { return append(b, []byte("this is not a gzip member")...) }), 5)
	})

	t.Run("concatenated gzip members", func(t *testing.T) {
		data := append(gzipString(t, joinLines(headerJSON, metricLine(1))), gzipString(t, joinLines(metricLine(2), metricLine(3)))...)
		recs, err := decodeAll(t, data, wire.DefaultLimits())
		requireDecoded(t, "", recs, err, 3)
	})

	t.Run("header only", func(t *testing.T) {
		d := newTestDecoder(t, stream(t, headerJSON))
		if _, err := d.Next(); !errors.Is(err, wire.ErrEmptyBatch) {
			t.Errorf("Next() = %v, want ErrEmptyBatch", err)
		}
	})
}

func TestDecoder_BatchRules(t *testing.T) {
	t.Run("equal sequence numbers", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLine(1), metricLine(2), metricLine(2)), wire.DefaultLimits())
		want{is: wire.ErrSequence, line: 4}.check(t, err)
		requireRecordCount(t, recs, 2)
	})

	t.Run("decreasing sequence numbers", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLine(5), metricLine(4)), wire.DefaultLimits())
		want{is: wire.ErrSequence, line: 3}.check(t, err)
		requireRecordCount(t, recs, 1)
	})

	t.Run("gaps in sequence numbers are allowed", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLine(5), metricLine(9), metricLine(1000)), wire.DefaultLimits())
		requireDecoded(t, "", recs, err, 3)
	})

	t.Run("seq zero", func(t *testing.T) {
		_, err := decodeAll(t, withHeader(t, envelope("metric", 0, tsJSON, okData)), wire.DefaultLimits())
		want{is: model.ErrInvalid, field: "seq", line: 2}.check(t, err)
	})
}

// requireStickyError fails the test unless again repeats the first error and no record was returned.
func requireStickyError(t *testing.T, n int, first, again error, rec model.Record) {
	t.Helper()
	if again == nil || again.Error() != first.Error() || !errors.Is(again, wire.ErrSequence) {
		t.Errorf("Next() #%d after the error = %v, want the same error %v", n, again, first)
	}
	if rec.Data != nil {
		t.Errorf("Next() #%d after the error returned a record %+v, want none", n, rec)
	}
}

func TestDecoder_StickyErrors(t *testing.T) {
	d := newTestDecoder(t, withHeader(t, metricLine(1), metricLine(1), metricLine(2)))
	if _, err := d.Next(); err != nil {
		t.Fatalf("first Next() = %v, want nil", err)
	}
	_, first := d.Next()
	want{is: wire.ErrSequence, line: 3}.check(t, first)
	for i := range 3 {
		rec, again := d.Next()
		requireStickyError(t, i+1, first, again, rec)
	}
}

// requireRepeated fails the test unless each of n further Next calls fails with target.
func requireRepeated(t *testing.T, d *wire.Decoder, n int, target error) {
	t.Helper()
	for i := range n {
		if _, err := d.Next(); !errors.Is(err, target) {
			t.Errorf("Next() #%d = %v, want %v", i+1, err, target)
		}
	}
}

func TestDecoder_EOFIsRepeated(t *testing.T) {
	d := newTestDecoder(t, withHeader(t, metricLine(1)))
	if _, err := d.Next(); err != nil {
		t.Fatalf("first Next() = %v, want nil", err)
	}
	requireRepeated(t, d, 3, io.EOF)
}

func TestDecoder_EmptyBatchErrorIsSticky(t *testing.T) {
	requireRepeated(t, newTestDecoder(t, stream(t, headerJSON)), 2, wire.ErrEmptyBatch)
}

func TestDecoder_OriginAndReceiveTimeCannotBeInjected(t *testing.T) {
	const injected = `"origin":"backend","received_at":"2026-03-02T00:00:00Z",`
	gapData := func(cause string) string {
		return `{"from":"2026-03-01T10:00:00Z","to":"2026-03-01T11:00:00Z","cause":"` + cause + `","first_seq":1,"last_seq":2}`
	}
	line := func(kind string, seq int, data string) string {
		return `{"kind":"` + kind + `",` + injected + `"source":"proc.stat","seq":` + fmt.Sprint(seq) + `,"captured_at":` + tsJSON + `,"data":` + data + `}`
	}

	t.Run("origin is always agent", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, line("metric", 1, okData)), wire.DefaultLimits())
		if err != nil || len(recs) != 1 {
			t.Fatalf("decoding = %d records, %v, want 1 record and nil", len(recs), err)
		}
		if recs[0].Origin != model.OriginAgent {
			t.Errorf("Origin = %q, want %q", recs[0].Origin, model.OriginAgent)
		}
	})

	for _, cause := range []string{"sequence_missing", "no_data"} {
		t.Run("gap cause "+cause+" is rejected", func(t *testing.T) {
			_, err := decodeAll(t, withHeader(t, line("gap", 1, gapData(cause))), wire.DefaultLimits())
			want{is: model.ErrInvalid, field: "data.cause", line: 2}.check(t, err)
		})
		t.Run("gap cause "+cause+" without injected keys is rejected", func(t *testing.T) {
			_, err := decodeAll(t, withHeader(t, kindLine("gap", gapData(cause))), wire.DefaultLimits())
			want{is: model.ErrInvalid, field: "data.cause", line: 2}.check(t, err)
		})
	}

	t.Run("agent gap cause is accepted", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, line("gap", 1, gapData("spool_dropped"))), wire.DefaultLimits())
		if err != nil || len(recs) != 1 {
			t.Errorf("decoding = %d records, %v, want 1 record and nil", len(recs), err)
		}
	})
}

type closeSpyReader struct {
	*bytes.Reader
	closed bool
}

func (r *closeSpyReader) Close() error { r.closed = true; return nil }

func TestDecoder_Header(t *testing.T) {
	t.Run("without clock offset", func(t *testing.T) {
		d, err := wire.NewDecoder(bytes.NewReader(withHeader(t, metricLine(1))), wire.DefaultLimits())
		if err != nil {
			t.Fatalf("NewDecoder() = %v, want nil", err)
		}
		got := d.Header()
		want := wire.Header{FormatMajor: 1, FormatMinor: 0, AgentID: "agent-1", BootID: testBootID, Mode: wire.ModeLive}
		if got.ClockOffset != nil || got != want {
			t.Errorf("Header() = %+v, want %+v", got, want)
		}
	})

	t.Run("with clock offset", func(t *testing.T) {
		header := `{"format_major":1,"format_minor":0,"agent_id":"agent-1","boot_id":"` + testBootID + `","clock_offset_ns":-5000000000,"mode":"backfill"}`
		d, err := wire.NewDecoder(bytes.NewReader(stream(t, header, metricLine(1))), wire.DefaultLimits())
		if err != nil {
			t.Fatalf("NewDecoder() = %v, want nil", err)
		}
		got := d.Header()
		if got.ClockOffset == nil || *got.ClockOffset != -5*time.Second {
			t.Fatalf("Header().ClockOffset = %v, want -5s", got.ClockOffset)
		}
		if got.Mode != wire.ModeBackfill {
			t.Errorf("Header().Mode = %q, want %q", got.Mode, wire.ModeBackfill)
		}
	})
}

func TestDecoder_Close(t *testing.T) {
	src := &closeSpyReader{Reader: bytes.NewReader(withHeader(t, metricLine(1)))}
	d, err := wire.NewDecoder(src, wire.DefaultLimits())
	if err != nil {
		t.Fatalf("NewDecoder() = %v, want nil", err)
	}
	if err := d.Close(); err != nil {
		t.Errorf("Close() = %v, want nil", err)
	}
	if src.closed {
		t.Errorf("Close() closed the underlying reader, want it left open")
	}
}

// gzipWithHeader gzips s with the given gzip header fields.
func gzipWithHeader(t *testing.T, h gzip.Header, s string) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	zw.Header = h
	if _, err := zw.Write([]byte(s)); err != nil {
		t.Fatalf("gzip write = %v, want nil", err)
	}
	if err := zw.Close(); err != nil {
		t.Fatalf("gzip close = %v, want nil", err)
	}
	return buf.Bytes()
}

func TestDecoder_GzipHeaderFields(t *testing.T) {
	body := joinLines(headerJSON, metricLine(1))

	t.Run("short name, comment and extra are accepted", func(t *testing.T) {
		h := gzip.Header{Name: "batch.ndjson", Comment: "a comment", Extra: []byte{'a', 'b', 2, 0, 1, 2}}
		recs, err := decodeAll(t, gzipWithHeader(t, h, body), wire.Limits{})
		if err != nil || len(recs) != 1 {
			t.Errorf("decoding = %d records, %v, want 1 record and nil", len(recs), err)
		}
	})

	t.Run("overlong fields are malformed on line 1", func(t *testing.T) {
		long := strings.Repeat("a", 512)
		for name, h := range map[string]gzip.Header{
			"name 512 bytes":    {Name: long},
			"comment 512 bytes": {Comment: long},
		} {
			_, err := decodeAll(t, gzipWithHeader(t, h, body), wire.Limits{})
			if err == nil {
				t.Errorf("%s: got nil error, want ErrMalformed", name)
				continue
			}
			want{is: wire.ErrMalformed, line: 1}.check(t, err)
		}
	})
}

// pathStep is one step of a path into a JSON document: an object key (string) or an array index (int).
type pathStep = any

// parseJSON parses text into a generic tree; numbers stay json.Number, so they are written back unchanged.
func parseJSON(t *testing.T, text string) any {
	t.Helper()
	dec := json.NewDecoder(strings.NewReader(text))
	dec.UseNumber()
	var v any
	if err := dec.Decode(&v); err != nil {
		t.Fatalf("test setup: parsing %q = %v, want nil", text, err)
	}
	return v
}

// goldenLines returns the header and the nine records of the golden batch.
func goldenLines(t *testing.T) []string {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join("..", "..", "testdata", "wire", "all-kinds.jsonl"))
	if err != nil {
		t.Fatalf("reading golden file = %v, want nil", err)
	}
	return strings.Split(strings.TrimRight(string(raw), "\n"), "\n")
}

// collectPaths adds the path of every object member below node to members and the path of the first element of
// every array of objects to elements.
func collectPaths(node any, path []pathStep, members, elements *[][]pathStep) {
	switch n := node.(type) {
	case map[string]any:
		for key, child := range n {
			childPath := append(append([]pathStep{}, path...), key)
			*members = append(*members, childPath)
			collectPaths(child, childPath, members, elements)
		}
	case []any:
		for i, child := range n {
			childPath := append(append([]pathStep{}, path...), i)
			if _, isObject := child.(map[string]any); isObject && i == 0 {
				*elements = append(*elements, childPath)
			}
			collectPaths(child, childPath, members, elements)
		}
	}
}

// applyAt replaces the value at path of the JSON line with the JSON text repl, or removes the key when repl is nil.
func applyAt(t *testing.T, line string, path []pathStep, repl *string) string {
	t.Helper()
	root := parseJSON(t, line)
	container := root
	for _, step := range path[:len(path)-1] {
		switch s := step.(type) {
		case string:
			container = container.(map[string]any)[s]
		case int:
			container = container.([]any)[s]
		}
	}
	var value any
	if repl != nil {
		value = parseJSON(t, *repl)
	}
	switch last := path[len(path)-1].(type) {
	case string:
		if repl == nil {
			delete(container.(map[string]any), last)
		} else {
			container.(map[string]any)[last] = value
		}
	case int:
		container.([]any)[last] = value
	}
	out, err := json.Marshal(root)
	if err != nil {
		t.Fatalf("test setup: writing the changed line = %v, want nil", err)
	}
	return string(out)
}

// goldenVariant names a position in the golden batch: the line (0 is the header) and the path of a key or element.
type goldenVariant struct {
	line int
	path []pathStep
}

func (v goldenVariant) String() string {
	parts := make([]string, len(v.path))
	for i, step := range v.path {
		parts[i] = fmt.Sprint(step)
	}
	return fmt.Sprintf("line %d %s", v.line+1, strings.Join(parts, "."))
}

// goldenVariants lists every object member at any depth of the golden batch, plus the model keys the batch lacks
// (the header's clock_offset_ns, the first process's ppid, the OOM kill's boot and the boot's oom_kill), and
// the first element of every array of objects.
func goldenVariants(t *testing.T, lines []string) (members, elements []goldenVariant) {
	t.Helper()
	for i, line := range lines {
		var m, e [][]pathStep
		collectPaths(parseJSON(t, line), nil, &m, &e)
		for _, p := range m {
			members = append(members, goldenVariant{i, p})
		}
		for _, p := range e {
			elements = append(elements, goldenVariant{i, p})
		}
	}
	members = append(members,
		goldenVariant{0, []pathStep{"clock_offset_ns"}},
		goldenVariant{2, []pathStep{"data", "processes", 0, "ppid"}},
		goldenVariant{6, []pathStep{"data", "boot"}},
		goldenVariant{7, []pathStep{"data", "oom_kill"}},
	)
	return members, elements
}

// isMapEntry reports whether the path names an entry of labels, status or variables.
func isMapEntry(path []pathStep) bool {
	if len(path) != 3 || path[0] != "data" {
		return false
	}
	return path[1] == "labels" || path[1] == "status" || path[1] == "variables"
}

// errorClass names the sentinel an error wraps.
func errorClass(err error) string {
	classes := []struct {
		name string
		is   error
	}{
		{"invalid", model.ErrInvalid},
		{"malformed", wire.ErrMalformed},
		{"unknown kind", wire.ErrUnknownKind},
		{"unsupported version", wire.ErrUnsupportedVersion},
		{"sequence", wire.ErrSequence},
		{"empty batch", wire.ErrEmptyBatch},
		{"limit", wire.ErrLimitExceeded},
	}
	for _, c := range classes {
		if errors.Is(err, c.is) {
			return c.name
		}
	}
	return "other"
}

// describeError describes the class, the line and the field error of a decode error.
func describeError(err error) string {
	desc := "fail " + errorClass(err)
	var de *wire.DecodeError
	if errors.As(err, &de) {
		desc += fmt.Sprintf(" line %d", de.Line)
	}
	var fe *model.FieldError
	if errors.As(err, &fe) {
		desc += fmt.Sprintf(" field %q reason %q", fe.Field, fe.Reason)
	}
	return desc
}

// decodeOutcome decodes a header line (line 0) or a record line behind a valid header and describes the result.
func decodeOutcome(t *testing.T, line int, text string) string {
	t.Helper()
	data := withHeader(t, text)
	if line == 0 {
		data = stream(t, text, metricLine(1))
	}
	d, err := wire.NewDecoder(bytes.NewReader(data), wire.DefaultLimits())
	if err != nil {
		return describeError(err)
	}
	t.Cleanup(func() { _ = d.Close() })
	if line == 0 {
		header, _ := json.Marshal(d.Header())
		return "header " + string(header)
	}
	rec, err := d.Next()
	if err != nil {
		return describeError(err)
	}
	payload, _ := json.Marshal(rec.Data)
	return fmt.Sprintf("record %s %d %s %s %s", rec.Source, rec.Seq, rec.CapturedAt.Format(time.RFC3339Nano), rec.Kind(), payload)
}

// requireSameOutcome fails the test unless both texts decode to the same outcome.
func requireSameOutcome(t *testing.T, v goldenVariant, lines []string, other string, repl, otherRepl *string) {
	t.Helper()
	got := decodeOutcome(t, v.line, applyAt(t, lines[v.line], v.path, repl))
	want := decodeOutcome(t, v.line, applyAt(t, lines[v.line], v.path, otherRepl))
	if got != want {
		t.Errorf("%v: null decodes to [%s], %s decodes to [%s], want equal", v, got, other, want)
	}
}

func TestDecoder_NullReadsAsAbsentKey(t *testing.T) {
	lines := goldenLines(t)
	members, _ := goldenVariants(t, lines)
	checked := 0
	for _, v := range members {
		if isMapEntry(v.path) {
			continue
		}
		checked++
		requireSameOutcome(t, v, lines, "an absent key", ptr("null"), nil)
	}
	if checked < 100 {
		t.Errorf("checked %d keys, want at least 100", checked)
	}
}

func TestDecoder_NullListElementReadsAsEmptyObject(t *testing.T) {
	lines := goldenLines(t)
	_, elements := goldenVariants(t, lines)
	if len(elements) != 8 {
		t.Fatalf("golden batch has %d lists of objects, want 8", len(elements))
	}
	for _, v := range elements {
		requireSameOutcome(t, v, lines, "{}", ptr("null"), ptr("{}"))
	}
}

func TestDecoder_NullMapValueReadsAsZeroValue(t *testing.T) {
	lines := goldenLines(t)
	members, _ := goldenVariants(t, lines)
	checked := 0
	for _, v := range members {
		if !isMapEntry(v.path) {
			continue
		}
		checked++
		zero := ptr(`""`)
		if v.path[1] == "status" {
			zero = ptr("0")
		}
		requireSameOutcome(t, v, lines, *zero, ptr("null"), zero)
	}
	if checked != 5 {
		t.Errorf("golden batch has %d map entries, want 5", checked)
	}
}

func TestDecoder_NullHeaderFields(t *testing.T) {
	cases := []struct {
		key string
		w   want
	}{
		{"agent_id", want{is: model.ErrInvalid, field: "agent_id", reason: "must be 1 to 64 characters of [A-Za-z0-9._-], starting with a letter or digit", line: 1}},
		{"boot_id", want{is: model.ErrInvalid, field: "boot_id", reason: "must be a lower-case UUID", line: 1}},
		{"mode", want{is: model.ErrInvalid, field: "mode", reason: "must be live or backfill", line: 1}},
	}
	for _, tc := range cases {
		t.Run(tc.key, func(t *testing.T) {
			header := applyAt(t, headerJSON, []pathStep{tc.key}, ptr("null"))
			_, err := decodeAll(t, stream(t, header, metricLine(1)), wire.DefaultLimits())
			tc.w.check(t, err)
		})
	}
	t.Run("optional fields read as absent", func(t *testing.T) {
		header := applyAt(t, applyAt(t, headerJSON, []pathStep{"format_minor"}, ptr("3")), []pathStep{"clock_offset_ns"}, ptr("5"))
		nulled := applyAt(t, applyAt(t, header, []pathStep{"format_minor"}, ptr("null")), []pathStep{"clock_offset_ns"}, ptr("null"))
		d := newTestDecoder(t, stream(t, nulled, metricLine(1)))
		if got := d.Header(); got.FormatMinor != 0 || got.ClockOffset != nil {
			t.Errorf("Header() = %+v, want format_minor 0 and no clock offset", got)
		}
	})
	t.Run("line is null", func(t *testing.T) {
		_, err := decodeAll(t, stream(t, "null", metricLine(1)), wire.DefaultLimits())
		want{is: wire.ErrUnsupportedVersion, line: 1}.check(t, err)
	})
}

func TestDecoder_NullEnvelopeFields(t *testing.T) {
	cases := []struct {
		key string
		w   want
	}{
		{"kind", want{is: wire.ErrUnknownKind, line: 2}},
		{"source", want{is: model.ErrInvalid, field: "source", reason: "required", line: 2}},
		{"seq", want{is: model.ErrInvalid, field: "seq", reason: "must be greater than 0 for origin agent", line: 2}},
		{"captured_at", want{is: model.ErrInvalid, field: "captured_at", reason: "required", line: 2}},
		{"data", want{is: model.ErrInvalid, field: "data", reason: "required", line: 2}},
	}
	for _, tc := range cases {
		t.Run(tc.key, func(t *testing.T) {
			line := applyAt(t, metricLine(1), []pathStep{tc.key}, ptr("null"))
			_, err := decodeAll(t, withHeader(t, line), wire.DefaultLimits())
			tc.w.check(t, err)
		})
	}
	t.Run("line is null", func(t *testing.T) {
		_, err := decodeAll(t, withHeader(t, "null"), wire.DefaultLimits())
		want{is: wire.ErrUnknownKind, line: 2}.check(t, err)
	})
}

func TestDecoder_NullPayloadFieldIsAFieldError(t *testing.T) {
	cases := []struct {
		name  string
		kind  string
		data  string
		field string
		why   string
	}{
		{"metric name", "metric", `{"name":null,"value":1}`, "data.name", "required"},
		{"log line log", "log_line", `{"log":null,"message":"m"}`, "data.log", "required"},
		{"null process", "process_snapshot", `{"complete":true,"processes":[null]}`, "data.processes[0].pid", "must be greater than 0"},
		{"null program", "process_snapshot", `{"complete":true,"programs":[null]}`, "data.programs[0].program", "required"},
		{"process command", "process_snapshot", `{"complete":true,"processes":[{"pid":1,"command":null}]}`, "data.processes[0].command", "required"},
		{"state proto", "connection_snapshot", `{"complete":true,"states":[{"proto":null,"count":1}]}`, "data.states[0].proto", "unknown value"},
		{"connection process command", "connection_snapshot", `{"complete":true,"processes":[{"pid":1,"command":null,"count":1}]}`, "data.processes[0].command", "required"},
		{"remote addr", "connection_snapshot", `{"complete":true,"remotes":[{"addr":null,"count":1}]}`, "data.remotes[0].addr", "invalid address"},
		{"service unit", "service_state", `{"unit":null,"load_state":"loaded","active_state":"active"}`, "data.unit", "required"},
		{"null status entry of a down database", "mariadb_status", `{"availability":"down","status":{"A":null},"complete":true}`, "data.availability", "status, variables and threads must be empty unless up"},
		{"oom victim command", "kernel_event", `{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":null}}`, "data.oom_kill.victim_command", "required"},
		{"boot id", "kernel_event", `{"type":"boot","boot":{"boot_id":null}}`, "data.boot.boot_id", "required"},
		{"gap from", "gap", `{"from":null,"to":"2026-03-01T10:00:00Z","cause":"unknown"}`, "data.from", "required"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := decodeAll(t, withHeader(t, kindLine(tc.kind, tc.data)), wire.DefaultLimits())
			want{is: model.ErrInvalid, field: tc.field, reason: tc.why, line: 2}.check(t, err)
		})
	}
}

func TestDecoder_NullOfOptionalPayloadFieldIsAccepted(t *testing.T) {
	cases := []struct {
		name string
		kind string
		data string
		want string // the decoded payload, written as JSON
	}{
		{"metric value", "metric", `{"name":"cpu","value":null}`, `{"name":"cpu","value":0}`},
		{"metric unit", "metric", `{"name":"cpu","value":1,"unit":null}`, `{"name":"cpu","value":1}`},
		{"metric label", "metric", `{"name":"cpu","value":1,"labels":{"device":null,"mount":"/var"}}`, `{"name":"cpu","value":1,"labels":{"device":"","mount":"/var"}}`},
		{"log line texts", "log_line", `{"log":"l","host":null,"program":null,"event":null,"message":null}`, `{"log":"l","message":""}`},
		{"log line numbers", "log_line", `{"log":"l","message":"m","pid":null,"truncated":null,"priority":null}`, `{"log":"l","message":"m"}`},
		{"process fields", "process_snapshot", `{"complete":true,"processes":[{"pid":1,"command":"c","user":null,"cmdline":null,"state":null,"started_at":null}]}`, `{"complete":true,"processes":[{"pid":1,"command":"c","cpu_percent":0,"rss_bytes":0}]}`},
		{"process ppid", "process_snapshot", `{"complete":true,"processes":[{"pid":1,"command":"c","ppid":null}]}`, `{"complete":true,"processes":[{"pid":1,"command":"c","cpu_percent":0,"rss_bytes":0}]}`},
		{"state", "connection_snapshot", `{"complete":true,"states":[{"proto":"udp","state":null,"count":1}]}`, `{"complete":true,"states":[{"proto":"udp","count":1}]}`},
		{"listener command", "connection_snapshot", `{"complete":true,"listeners":[{"proto":"tcp","local":"1.2.3.4:1","command":null}]}`, `{"complete":true,"listeners":[{"proto":"tcp","local":"1.2.3.4:1"}]}`},
		{"service fields", "service_state", `{"unit":"nginx.service","load_state":"loaded","active_state":"active","sub_state":null,"active_enter_at":null,"restarts":null}`, `{"unit":"nginx.service","load_state":"loaded","active_state":"active","restarts":0}`},
		{"null thread", "mariadb_status", `{"availability":"up","threads":[null],"complete":true}`, `{"availability":"up","threads":[{"id":0,"time_seconds":0}],"complete":true}`},
		{"null variable", "mariadb_status", `{"availability":"up","variables":{"A":null},"complete":true}`, `{"availability":"up","variables":{"A":""},"complete":true}`},
		{"null status entry", "mariadb_status", `{"availability":"up","status":{"A":null},"complete":true}`, `{"availability":"up","status":{"A":0},"complete":true}`},
		{"kernel message", "kernel_event", `{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":"c"},"message":null}`, `{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":"c"}}`},
		{"gap collector", "gap", `{"from":"2026-03-01T09:00:00Z","to":"2026-03-01T10:00:00Z","cause":"unknown","collector":null}`, `{"from":"2026-03-01T09:00:00Z","to":"2026-03-01T10:00:00Z","cause":"unknown"}`},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			recs, err := decodeAll(t, withHeader(t, kindLine(tc.kind, tc.data)), wire.DefaultLimits())
			requireDecoded(t, "", recs, err, 1)
			got, _ := json.Marshal(recs[0].Data)
			if string(got) != tc.want {
				t.Errorf("decoded payload = %s, want %s", got, tc.want)
			}
		})
	}
}

func TestDecoder_WrongTypeStaysMalformed(t *testing.T) {
	cases := []struct {
		name string
		kind string
		data string
	}{
		{"number as name", "metric", `{"name":5}`},
		{"string as pid", "log_line", `{"log":"l","message":"m","pid":"1"}`},
		{"object as list", "process_snapshot", `{"complete":true,"processes":{}}`},
		{"number as element", "process_snapshot", `{"complete":true,"processes":[5]}`},
		{"number as label", "metric", `{"name":"cpu","value":1,"labels":{"a":5}}`},
		{"string as status", "mariadb_status", `{"availability":"up","status":{"A":"x"},"complete":true}`},
		{"quoted null as address", "connection_snapshot", `{"complete":true,"remotes":[{"addr":"null","count":1}]}`},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := decodeAll(t, withHeader(t, kindLine(tc.kind, tc.data)), wire.DefaultLimits())
			malformed(2).check(t, err)
		})
	}
}
