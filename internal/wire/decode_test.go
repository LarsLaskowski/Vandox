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
	"runtime"
	"slices"
	"strconv"
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
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := wire.NewDecoder(bytes.NewReader(stream(t, tc.header, metricLine(1))), wire.DefaultLimits())
			want{is: wire.ErrUnsupportedVersion, line: 1}.check(t, err)
		})
	}

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
		{"upper-case duplicate of format_major", stream(t, `{"format_major":1,"FORMAT_MAJOR":2,`+hdrTail+`}`, metricLine(1)), malformed(1)},
		{"repeated format_major", stream(t, `{"format_major":1,"format_major":2,`+hdrTail+`}`, metricLine(1)), malformed(1)},
		{"folded duplicate of format_major with major 1 last", stream(t, `{"format_major":2,"Format_Major":1,"format_minor":0,`+hdrTail+`}`, metricLine(1)), malformed(1)},
		{"header is true", stream(t, `true`, metricLine(1)), malformed(1)},
		{"major as 1E0", stream(t, `{"format_major":1E0,`+hdrTail+`}`, metricLine(1)), malformed(1)},
		{"major above int64", stream(t, `{"format_major":9223372036854775808,`+hdrTail+`}`, metricLine(1)), malformed(1)},
		{"major as true", stream(t, `{"format_major":true,`+hdrTail+`}`, metricLine(1)), malformed(1)},
		{"minor above int64", stream(t, `{"format_major":1,"format_minor":9223372036854775808,`+hdrTail+`}`, metricLine(1)), malformed(1)},
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
		{"unknown keys at any depth", stream(t, headerJSON, `{"kind":"metric","source":"proc.stat","seq":1,"captured_at":`+tsJSON+`,"extra":{"a":[1,{"b":null}]},"data":{"name":"cpu","value":1,"extra":[[[]]]}}`), 1, nil},
		{"origin and received_at keys are ignored", stream(t, headerJSON, `{"kind":"metric","origin":"backend","received_at":"2026-03-02T00:00:00Z","source":"proc.stat","seq":1,"captured_at":`+tsJSON+`,"data":`+okData+`}`), 1, checkOriginAgent},
		{"time forms with zero offset", withHeader(t,
			envelope("metric", 1, `"2026-03-01T12:00:00+00:00"`, okData),
			envelope("metric", 2, `"2026-03-01T12:00:00-00:00"`, okData),
			envelope("metric", 3, `"2026-03-01T12:00:00.123456789Z"`, okData)), 3, checkZeroOffsetTimes},
		{"escaped control characters in a text", withHeader(t, kindLine("log_line", `{"log":"journal","message":"a\u0000b\n"}`)), 1, logMessage("a\x00b\n")},
		{"IPv4-mapped address without zone", withHeader(t, kindLine("connection_snapshot", `{"remotes":[{"addr":"::ffff:1.2.3.4","count":1}]}`)), 1, nil},
		{"empty connection snapshot", withHeader(t, kindLine("connection_snapshot", `{"complete":true}`)), 1, nil},
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
	if len(elements) != 14 {
		t.Fatalf("golden batch has %d lists of objects, want 14", len(elements))
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

// sharedRecord is a record a shared decoder case expects.
type sharedRecord struct {
	Kind string `json:"kind"`
	Seq  uint64 `json:"seq"`
}

// sharedWant is the outcome of a shared decoder case: the records before the end or the error, and the error.
type sharedWant struct {
	Records     []sharedRecord `json:"records"`
	Error       string         `json:"error"`
	Line        int            `json:"line"`
	Field       string         `json:"field"`
	Reason      string         `json:"reason"`
	FormatMinor *int64         `json:"format_minor"`
}

// sharedCase is one case of testdata/wire/decoder-cases.json.
type sharedCase struct {
	Name   string     `json:"name"`
	Lines  []string   `json:"lines"`
	Latin1 bool       `json:"latin1"`
	Want   sharedWant `json:"want"`
}

// sharedErrors maps the error names of the fixture (the names of WireErrorKind in the backend) to the Go errors.
var sharedErrors = map[string]error{
	"UnsupportedVersion": wire.ErrUnsupportedVersion,
	"Malformed":          wire.ErrMalformed,
	"UnknownKind":        wire.ErrUnknownKind,
	"Sequence":           wire.ErrSequence,
	"EmptyBatch":         wire.ErrEmptyBatch,
	"LimitExceeded":      wire.ErrLimitExceeded,
	"Invalid":            model.ErrInvalid,
}

// escapedCaseIDs are the cases whose line under test holds a JSON escape that the fixture spells with a doubled
// backslash; read wrongly, they would pass without testing the escape.
var escapedCaseIDs = []string{"K4", "K5", "N4", "N7", "N8", "N9", "N10", "N11", "D9", "S13", "S14", "S15", "S16", "S17", "S18", "S19", "S20", "S21", "S22", "S23", "S24", "S25", "S26", "S27", "S28", "S29", "S30", "S31"}

// requiredCaseIDs lists the cases the fixture must hold, from the plans of issues 175 and 182.
func requiredCaseIDs() []string {
	var ids []string
	for prefix, count := range map[string]int{"K": 6, "N": 11, "D": 28, "H": 31, "O": 4, "X": 4, "M": 5, "U": 2, "S": 38} {
		for i := 1; i <= count; i++ {
			ids = append(ids, prefix+strconv.Itoa(i))
		}
	}
	return ids
}

func sharedCaseID(name string) string {
	id, _, _ := strings.Cut(name, " ")
	return id
}

func loadSharedCases(t *testing.T) []sharedCase {
	t.Helper()
	path := filepath.Join("..", "..", "testdata", "wire", "decoder-cases.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("reading %s = %v, want nil", path, err)
	}
	var file struct {
		Cases []sharedCase `json:"cases"`
	}
	dec := json.NewDecoder(bytes.NewReader(raw))
	dec.DisallowUnknownFields()
	if err := dec.Decode(&file); err != nil {
		t.Fatalf("parsing %s = %v, want nil", path, err)
	}
	return file.Cases
}

// requireSharedCasesWellFormed fails the test for a case with an unknown error name, a duplicate case name, a missing
// required case and for a case with an escaped key whose line holds no backslash after the fixture was read.
func requireSharedCasesWellFormed(t *testing.T, cases []sharedCase) {
	t.Helper()
	names := make(map[string]bool)
	ids := make(map[string]bool)
	for _, c := range cases {
		if names[c.Name] {
			t.Fatalf("case name %q appears twice, want unique names", c.Name)
		}
		names[c.Name] = true
		ids[sharedCaseID(c.Name)] = true
		requireSharedCaseWellFormed(t, c)
	}
	for _, id := range requiredCaseIDs() {
		if !ids[id] {
			t.Errorf("fixture holds no case %s, want every case of the plan", id)
		}
	}
}

// requireSharedCaseWellFormed fails the test for a case with an unknown error name, an escaped key whose lines hold no
// backslash, or a malformed latin1 case.
func requireSharedCaseWellFormed(t *testing.T, c sharedCase) {
	t.Helper()
	if c.Want.Error != "" {
		if _, ok := sharedErrors[c.Want.Error]; !ok {
			t.Fatalf("case %q: error %q is not one of the seven error names", c.Name, c.Want.Error)
		}
	}
	if slices.Contains(escapedCaseIDs, sharedCaseID(c.Name)) && !slices.ContainsFunc(c.Lines, func(line string) bool { return strings.Contains(line, `\`) }) {
		t.Fatalf("case %q: no line holds a backslash, want the escape of the key kept (doubled backslash in the fixture)", c.Name)
	}
	if c.Latin1 {
		requireLatin1Case(t, c)
	}
}

// requireLatin1Case fails the test for a latin1 case with a character above U+00FF, or with no character from U+0080
// to U+00FF (a byte whose escape was lost or doubled in the file).
func requireLatin1Case(t *testing.T, c sharedCase) {
	t.Helper()
	high := false
	for _, line := range c.Lines {
		for _, r := range line {
			if r > 0xFF {
				t.Fatalf("case %q: latin1 case holds U+%04X, want only U+0000 to U+00FF", c.Name, r)
			}
			high = high || r >= 0x80
		}
	}
	if !high {
		t.Fatalf("case %q: latin1 case holds no character from U+0080 to U+00FF, want the byte kept", c.Name)
	}
}

// latin1Stream gzips the lines one character per byte, each line followed by a line feed.
func latin1Stream(t *testing.T, lines []string) []byte {
	t.Helper()
	var plain []byte
	for _, line := range lines {
		for _, r := range line {
			if r < 0 || r > 0xFF {
				t.Fatalf("latin1Stream: U+%04X is above U+00FF, want one byte per character", r)
			} else {
				plain = append(plain, byte(r))
			}
		}
		plain = append(plain, '\n')
	}
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	if _, err := zw.Write(plain); err != nil {
		t.Fatalf("gzip write = %v, want nil", err)
	}
	if err := zw.Close(); err != nil {
		t.Fatalf("gzip close = %v, want nil", err)
	}
	return buf.Bytes()
}

// decodeSharedCase gzips the lines of a case and decodes the batch to its end.
func decodeSharedCase(t *testing.T, c sharedCase) ([]sharedRecord, int64, error) {
	t.Helper()
	data := stream(t, c.Lines...)
	if c.Latin1 {
		data = latin1Stream(t, c.Lines)
	}
	d, err := wire.NewDecoder(bytes.NewReader(data), wire.DefaultLimits())
	if err != nil {
		return nil, 0, err
	}
	defer func() { _ = d.Close() }()
	var recs []sharedRecord
	for range 1000 {
		rec, err := d.Next()
		if errors.Is(err, io.EOF) {
			return recs, d.Header().FormatMinor, nil
		}
		if err != nil {
			return recs, d.Header().FormatMinor, err
		}
		recs = append(recs, sharedRecord{Kind: string(rec.Kind()), Seq: rec.Seq})
	}
	t.Fatalf("decoder did not end the stream within 1000 records")
	return nil, 0, nil
}

// requireSharedOutcome compares the records, the error and the minor version a shared case gave with the ones it wants.
func requireSharedOutcome(t *testing.T, c sharedCase, recs []sharedRecord, minor int64, err error) {
	t.Helper()
	if !slices.Equal(recs, c.Want.Records) {
		t.Errorf("records = %v, want %v (error %v)", recs, c.Want.Records, err)
	}
	if c.Want.Error == "" {
		if err != nil {
			t.Fatalf("decoding = %v, want nil", err)
		}
	} else {
		want{is: sharedErrors[c.Want.Error], field: c.Want.Field, reason: c.Want.Reason, line: c.Want.Line}.check(t, err)
	}
	if c.Want.FormatMinor != nil && minor != *c.Want.FormatMinor {
		t.Errorf("Header().FormatMinor = %d, want %d", minor, *c.Want.FormatMinor)
	}
}

func TestDecoder_SharedCases(t *testing.T) {
	cases := loadSharedCases(t)
	requireSharedCasesWellFormed(t, cases)
	for _, c := range cases {
		t.Run(c.Name, func(t *testing.T) {
			recs, minor, err := decodeSharedCase(t, c)
			requireSharedOutcome(t, c, recs, minor, err)
		})
	}
}

func TestDecoder_MaxDepthIs64(t *testing.T) {
	if wire.MaxDepth != 64 {
		t.Errorf("wire.MaxDepth = %d, want 64", wire.MaxDepth)
	}
}

func TestDecoder_HeaderFormatMinorIsInt64(t *testing.T) {
	for _, minor := range []int64{3000000000, math.MaxInt64} {
		header := fmt.Sprintf(`{"format_major":1,"format_minor":%d,%s}`, minor, hdrTail)
		d, err := wire.NewDecoder(bytes.NewReader(stream(t, header, metricLine(1))), wire.DefaultLimits())
		if err != nil {
			t.Fatalf("NewDecoder(format_minor %d) = %v, want nil", minor, err)
		}
		if got := d.Header().FormatMinor; got != minor {
			t.Errorf("Header().FormatMinor = %d, want %d", got, minor)
		}
	}
}

func TestDecoder_RejectsRawInvalidUTF8Keys(t *testing.T) {
	keys := []struct {
		name string
		key  string
	}{
		{"byte 0xFF", "\xff"},
		{"byte 0xFF after letters", "kin\xffd"},
		{"overlong encoding 0xC0 0xAF", "\xc0\xaf"},
		{"encoded surrogate 0xED 0xA0 0x80", "\xed\xa0\x80"},
	}
	for _, k := range keys {
		t.Run("record key "+k.name, func(t *testing.T) {
			record := strings.Replace(metricLine(1), `"captured_at"`, `"`+k.key+`":1,"captured_at"`, 1)
			_, err := decodeAll(t, withHeader(t, record), wire.DefaultLimits())
			malformed(2).check(t, err)
		})
		t.Run("header key "+k.name, func(t *testing.T) {
			header := `{"format_major":1,"format_minor":0,"` + k.key + `":1,` + hdrTail + `}`
			_, err := wire.NewDecoder(bytes.NewReader(stream(t, header, metricLine(1))), wire.DefaultLimits())
			malformed(1).check(t, err)
		})
	}
}

func TestDecoder_ErrorTextsNameNoKey(t *testing.T) {
	deep := strings.Repeat("[", wire.MaxDepth) + "0" + strings.Repeat("]", wire.MaxDepth)
	withExtra := func(extra string) string {
		return strings.Replace(metricLine(1), `"captured_at"`, extra+`,"captured_at"`, 1)
	}
	cases := []struct {
		name   string
		header string
		record string
		want   string
	}{
		{"duplicate key", headerJSON, withExtra(`"SENTINELKEY":1,"SENTINELKEY":2`), "wire: line 2: malformed batch: duplicate key"},
		{"duplicate key in the header", `{"format_major":1,"format_minor":0,` + hdrTail + `,"SENTINELKEY":1,"SENTINELKEY":2}`, metricLine(1), "wire: line 1: malformed batch: duplicate key"},
		{"key not ASCII", headerJSON, withExtra("\"SENTINELK\xc3\x89Y\":1"), "wire: line 2: malformed batch: key not ASCII"},
		{"key not ASCII in the header", `{"format_major":1,"format_minor":0,` + hdrTail + `,"SENTINELK` + "\xc3\x89" + `Y":1}`, metricLine(1), "wire: line 1: malformed batch: key not ASCII"},
		{"nesting too deep", headerJSON, withExtra(`"x":` + deep), "wire: line 2: malformed batch: nesting deeper than 64"},
		{"nesting too deep in the header", `{"format_major":1,"format_minor":0,` + hdrTail + `,"x":` + deep + `}`, metricLine(1), "wire: line 1: malformed batch: nesting deeper than 64"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := decodeAll(t, stream(t, tc.header, tc.record), wire.DefaultLimits())
			if err == nil {
				t.Fatalf("decoding = nil, want %q", tc.want)
			}
			if !errors.Is(err, wire.ErrMalformed) {
				t.Errorf("error = %v, want errors.Is(err, ErrMalformed)", err)
			}
			if got := err.Error(); got != tc.want {
				t.Errorf("error text = %q, want %q", got, tc.want)
			}
		})
	}
}

func TestDecoder_InterleavedDecodersKeepTheirOwnState(t *testing.T) {
	record := func(seq int) string {
		return strings.Replace(metricLine(seq), `"captured_at"`, `"x":{"a":{"b":1},"c":[{"b":2},{"b":3,"d":4}]},"captured_at"`, 1)
	}
	open := func() *wire.Decoder {
		d, err := wire.NewDecoder(bytes.NewReader(stream(t, headerJSON, record(1), record(2), record(3))), wire.DefaultLimits())
		if err != nil {
			t.Fatalf("NewDecoder() = %v, want nil", err)
		}
		t.Cleanup(func() { _ = d.Close() })
		return d
	}
	first, second := open(), open()
	for seq := uint64(1); seq <= 3; seq++ {
		for name, d := range map[string]*wire.Decoder{"first": first, "second": second} {
			rec, err := d.Next()
			if err != nil || rec.Seq != seq {
				t.Fatalf("%s decoder Next() = seq %d, %v, want seq %d, nil", name, rec.Seq, err, seq)
			}
		}
	}
	for name, d := range map[string]*wire.Decoder{"first": first, "second": second} {
		if _, err := d.Next(); !errors.Is(err, io.EOF) {
			t.Errorf("%s decoder Next() after the last record = %v, want io.EOF", name, err)
		}
	}
}

// Shapes of the memory and time tests: every one sits in the unknown envelope key "x" of a valid metric record.

// shapeLineBytes is the size of the record line of the shapes; the default line limit is 1 MiB.
const shapeLineBytes = 1_000_000

// shortKeys returns the first n distinct keys, ignoring ASCII case, that are as short as possible: the printable ASCII
// characters other than the quote and the backslash, then pairs, then triples.
func shortKeys(n int) []string {
	keys := make([]string, 0, n)
	var alphabet []string
	seen := make(map[string]bool)
	for c := byte(0x20); c <= 0x7e; c++ {
		if c == '"' || c == '\\' || seen[strings.ToLower(string(c))] {
			continue
		}
		seen[strings.ToLower(string(c))] = true
		alphabet = append(alphabet, string(c))
	}
	keys = append(keys, alphabet...)
	for _, a := range alphabet {
		for _, b := range alphabet {
			keys = append(keys, a+b)
		}
	}
	for _, a := range alphabet {
		for _, b := range alphabet {
			for _, c := range alphabet {
				keys = append(keys, a+b+c)
			}
		}
	}
	return keys[:n]
}

// objectOfKeys returns {"k1":0,"k2":0,...} for the keys.
func objectOfKeys(keys []string) string {
	var b strings.Builder
	b.WriteByte('{')
	for i, k := range keys {
		if i > 0 {
			b.WriteByte(',')
		}
		b.WriteString(`"` + k + `":0`)
	}
	b.WriteByte('}')
	return b.String()
}

// objectFillingBytes returns an object of distinct short keys of about size bytes.
func objectFillingBytes(size int) string {
	keys := shortKeys(250_000)
	n := 0
	for length := 2; length < size; n++ {
		length += len(keys[n]) + 5
	}
	return objectOfKeys(keys[:n])
}

// listOf returns [first,unit,unit,...] with first and as many units as fit into about size bytes.
func listOf(first, unit string, size int) string {
	var b strings.Builder
	b.WriteByte('[')
	if first != "" {
		b.WriteString(first)
	}
	for b.Len() < size {
		if first != "" || b.Len() > 1 {
			b.WriteByte(',')
		}
		b.WriteString(unit)
	}
	b.WriteByte(']')
	return b.String()
}

// shapeRecord returns a valid metric record line whose unknown envelope key "x" holds x.
func shapeRecord(seq int, x string) string {
	return fmt.Sprintf(`{"kind":"metric","source":"host","seq":%d,"captured_at":"2026-10-01T10:00:00Z","x":%s,"data":{"name":"cpu","value":1}}`, seq, x)
}

// shapeCase is one shape: its name and the JSON value of the key "x".
type shapeCase struct{ name, x string }

// shapeValues returns the value of "x" of the shapes 1 to 6 of the plan, named.
func shapeValues() []shapeCase {
	half := objectFillingBytes(shapeLineBytes / 2)
	shapes := []shapeCase{
		{"1 list of small objects", listOf("", `{"a":0}`, shapeLineBytes)},
		{"2 one object of distinct keys", objectFillingBytes(shapeLineBytes)},
		{"3 list of empty arrays", listOf("", `[]`, shapeLineBytes)},
		{"4 list of numbers", listOf("", `0`, shapeLineBytes)},
		{"5 big object then small objects", listOf(half, `{"a":0}`, shapeLineBytes)},
	}
	for _, k := range []int{9, 12, 24, 48, 65, 1025} {
		shapes = append(shapes, shapeCase{fmt.Sprintf("6 objects of %d keys", k), listOf("", objectOfKeys(shortKeys(k)), shapeLineBytes)})
	}
	return shapes
}

// readBatch opens the batch and reads it to the end; any error fails the test.
func readBatch(t *testing.T, data []byte, lim wire.Limits) {
	t.Helper()
	d, err := wire.NewDecoder(bytes.NewReader(data), lim)
	if err != nil {
		t.Fatalf("NewDecoder() = %v, want nil", err)
	}
	defer func() { _ = d.Close() }()
	for {
		_, err := d.Next()
		if errors.Is(err, io.EOF) {
			return
		}
		if err != nil {
			t.Fatalf("Next() = %v, want nil", err)
		}
	}
}

// allocatedBy returns the bytes the process allocated while f ran.
func allocatedBy(f func()) uint64 {
	var before, after runtime.MemStats
	runtime.ReadMemStats(&before)
	f()
	runtime.ReadMemStats(&after)
	return after.TotalAlloc - before.TotalAlloc
}

// maxAllocFactor bounds the bytes the decoder allocates per byte of the record line (shape 7: of the batch).
const maxAllocFactor = 64

func TestDecoder_AllocationIsBoundedPerShape(t *testing.T) {
	for _, shape := range shapeValues() {
		t.Run("shape "+shape.name, func(t *testing.T) {
			line := shapeRecord(1, shape.x)
			if len(line) > wire.DefaultLimits().MaxLineBytes || len(line) < shapeLineBytes*9/10 {
				t.Fatalf("test setup: line has %d bytes, want about %d and within the line limit", len(line), shapeLineBytes)
			}
			data := withHeader(t, line)
			allocated := allocatedBy(func() { readBatch(t, data, wire.DefaultLimits()) })
			t.Logf("allocated %.1f times the line of %d bytes", float64(allocated)/float64(len(line)), len(line))
			if limit := uint64(maxAllocFactor) * uint64(len(line)); allocated > limit {
				t.Errorf("decoding allocated %d bytes (%.1f times the line of %d bytes), want at most %d times", allocated, float64(allocated)/float64(len(line)), len(line), maxAllocFactor)
			}
		})
	}
}

// deepBatch returns a batch of about size decompressed bytes of short records, each with the unknown key "x" holding x.
func deepBatch(t *testing.T, x string, size int) ([]byte, int) {
	t.Helper()
	lines := []string{headerJSON}
	total := len(headerJSON) + 1
	for seq := 1; total < size; seq++ {
		line := shapeRecord(seq, x)
		lines = append(lines, line)
		total += len(line) + 1
	}
	return stream(t, lines...), total
}

func TestDecoder_AllocationIsBoundedForDeeplyNestedRecords(t *testing.T) {
	variants := []shapeCase{
		{"63 arrays deep", strings.Repeat("[", 63) + "0" + strings.Repeat("]", 63)},
		{"61 objects deep", strings.Repeat(`{"":`, 61) + "0" + strings.Repeat("}", 61)},
		{"61 objects deep with 8 more keys each", strings.Repeat(`{"a":0,"b":0,"c":0,"d":0,"e":0,"f":0,"g":0,"h":0,"":`, 61) + "0" + strings.Repeat("}", 61)},
	}
	for _, v := range variants {
		t.Run(v.name, func(t *testing.T) {
			data, size := deepBatch(t, v.x, 1<<20)
			allocated := allocatedBy(func() { readBatch(t, data, wire.DefaultLimits()) })
			t.Logf("allocated %.1f times the batch of %d bytes", float64(allocated)/float64(size), size)
			if ratio := float64(allocated) / float64(size); ratio > maxAllocFactor {
				t.Errorf("decoding allocated %d bytes (%.1f times the batch of %d bytes), want at most %d times", allocated, ratio, size, maxAllocFactor)
			}
		})
	}
}

// bestOfThree runs f once to warm up and three times more and returns the shortest of the three durations.
func bestOfThree(f func()) time.Duration {
	f()
	best := time.Duration(math.MaxInt64)
	for range 3 {
		start := time.Now()
		f()
		best = min(best, time.Since(start))
	}
	return best
}

// maxTimeFactor bounds the time of the decoder against a plain json.Unmarshal of the same line.
const maxTimeFactor = 10

// TestDecoder_TimeIsInProportionToTheLine guards against a quadratic key check: one big object followed by many small
// ones. It compares two durations measured here and asserts no absolute duration (docs/UNIT_TESTS.md).
func TestDecoder_TimeIsInProportionToTheLine(t *testing.T) {
	line := shapeRecord(1, listOf(objectFillingBytes(shapeLineBytes/2), `{"a":0}`, shapeLineBytes))
	if len(line) > wire.DefaultLimits().MaxLineBytes {
		t.Fatalf("test setup: line has %d bytes, want at most %d", len(line), wire.DefaultLimits().MaxLineBytes)
	}
	data := withHeader(t, line)
	decode := bestOfThree(func() { readBatch(t, data, wire.DefaultLimits()) })
	raw := []byte(line)
	plain := bestOfThree(func() {
		var v any
		if err := json.Unmarshal(raw, &v); err != nil {
			t.Fatalf("json.Unmarshal() = %v, want nil", err)
		}
	})
	ratio := float64(decode) / float64(plain)
	t.Logf("decoding took %v, json.Unmarshal %v, ratio %.2f", decode, plain, ratio)
	if ratio > maxTimeFactor {
		t.Errorf("decoding took %.1f times a json.Unmarshal of the line, want at most %d times", ratio, maxTimeFactor)
	}
}

func TestDecoder_RejectsDuplicateAtTheEndOfALongLine(t *testing.T) {
	// The distinct keys include "A"; the key "a" at the end repeats it ignoring case.
	object := strings.TrimSuffix(objectFillingBytes(shapeLineBytes-100), "}") + `,"a":0}`
	line := shapeRecord(1, object)
	if len(line) > wire.DefaultLimits().MaxLineBytes || len(line) < shapeLineBytes/2 {
		t.Fatalf("test setup: line has %d bytes, want a long line within the limit", len(line))
	}
	_, err := decodeAll(t, withHeader(t, line), wire.DefaultLimits())
	malformed(2).check(t, err)
}

func TestDecoder_RejectsDuplicateKeysOfTheTable(t *testing.T) {
	cases := []struct {
		name string
		data []byte
	}{
		{"duplicate keys, last used to win", stream(t, headerJSON, `{"kind":"metric","source":"proc.stat","seq":9,"seq":3,"captured_at":`+tsJSON+`,"data":{"name":"x y","name":"cpu","value":1}}`)},
		{"Kelvin sign and long s in keys", withHeader(t, "{\"Kind\":\"metric\",\"source\":\"proc.stat\",\"\xc5\xbfeq\":5,\"captured_at\":"+tsJSON+",\"data\":"+okData+"}")},
		{"Kelvin sign alone in a key", withHeader(t, "{\"\xe2\x84\xaaind\":\"metric\",\"source\":\"proc.stat\",\"seq\":5,\"captured_at\":"+tsJSON+",\"data\":"+okData+"}")},
		{"folded duplicate key", withHeader(t, "{\"kind\":\"gap\",\"Kind\":\"metric\",\"source\":\"proc.stat\",\"seq\":1,\"captured_at\":"+tsJSON+",\"data\":"+okData+"}")},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			recs, err := decodeAll(t, tc.data, wire.DefaultLimits())
			malformed(2).check(t, err)
			if len(recs) != 0 {
				t.Errorf("decoded %d records before the error, want 0", len(recs))
			}
		})
	}
}

func TestDecoder_ReturnsRecordsBeforeDuplicateKeyThenFailsForGood(t *testing.T) {
	repeated := strings.Replace(metricLine(2), `"seq":2`, `"seq":2,"seq":2`, 1)
	d, err := wire.NewDecoder(bytes.NewReader(withHeader(t, metricLine(1), repeated, metricLine(3))), wire.DefaultLimits())
	if err != nil {
		t.Fatalf("NewDecoder() = %v, want nil", err)
	}
	t.Cleanup(func() { _ = d.Close() })
	rec, err := d.Next()
	if err != nil || rec.Seq != 1 {
		t.Fatalf("first Next() = seq %d, %v, want seq 1, nil", rec.Seq, err)
	}
	_, first := d.Next()
	malformed(3).check(t, first)
	_, again := d.Next()
	if !errors.Is(again, first) {
		t.Errorf("Next() after the error = %v, want the same error %v", again, first)
	}
}

// Text rule (record 0090, option 16): a complete line must be valid UTF-8 and hold no surrogate escape that is not a high
// half directly followed by an escaped low half.

const (
	textNotUTF8   = "line not valid UTF-8"
	textUnpaired  = "unpaired surrogate escape"
	textMalformed = "malformed batch: "
)

// invalidByteForms are the byte forms of the plan (AC6) that are no valid UTF-8.
var invalidByteForms = []struct {
	name string
	form string
}{
	{"byte FF", "\xff"},
	{"byte F5 80 80 80", "\xf5\x80\x80\x80"},
	{"stray continuation byte", "\x80"},
	{"overlong C0 AF", "\xc0\xaf"},
	{"overlong C0 80", "\xc0\x80"},
	{"overlong E0 80 80", "\xe0\x80\x80"},
	{"encoded surrogate", "\xed\xa0\x80"},
	{"above U+10FFFF", "\xf4\x90\x80\x80"},
	{"cut short E2 82", "\xe2\x82"},
	{"cut short F0 9F 98", "\xf0\x9f\x98"},
}

// unpairedEscapeForms are the escapes of the cases S13-S20 that hold an unpaired surrogate.
var unpairedEscapeForms = []struct {
	name string
	form string
}{
	{"S13 high alone", `\ud800`},
	{"S14 low alone", `\udc00`},
	{"S15 high then non-surrogate escape", `\ud800\u0041`},
	{"S16 high then letter", `\ud800x`},
	{"S17 low then high", `\udc00\ud800`},
	{"S18 high before a valid pair", `\ud800\ud800\udc00`},
	{"S19 upper-case hex", `\uD800`},
	{"S20 high before escaped newline", `\ud800\n`},
	{"escaped backslash then escape", `\\\ud800`},
	{"high then short escape", `\ud800\u12`},
	{"high then backslash alone", `\ud800\`},
	{"high then non-hex escape", `\ud800\uZZZZ`},
}

// acceptedEscapeForms are escapes the text rule keeps.
var acceptedEscapeForms = []struct {
	name string
	form string
}{
	{"pair", `\ud83d\ude00`},
	{"pair upper-case", `\uD83D\uDE00`},
	{"pair mixed case", `\uD83d\uDe00`},
	{"replacement character", `\ufffd`},
	{"noncharacter", `\uffff`},
	{"escaped backslash before ud800", `\\ud800`},
	{"simple escapes", `\"\/\b\f\n\r\t`},
	{"escaped backslash before pair", `\\\ud83d\ude00`},
}

func logMessageLine(message string) string {
	return kindLine("log_line", `{"log":"journal","message":"`+message+`"}`)
}

// textVariants places a form at the very start, in the middle and at the very end of a message.
func textVariants(form string) map[string]string {
	return map[string]string{"start": form + "b", "middle": "a" + form + "b", "end": "a" + form, "whole": form}
}

func requireTextError(t *testing.T, err error, line int, reason string) {
	t.Helper()
	if err == nil {
		t.Fatalf("decoding = nil, want malformed (%s)", reason)
	}
	if !errors.Is(err, wire.ErrMalformed) {
		t.Errorf("error = %v, want errors.Is(err, ErrMalformed)", err)
	}
	want := fmt.Sprintf("wire: line %d: %s%s", line, textMalformed, reason)
	if got := err.Error(); got != want {
		t.Errorf("error text = %q, want %q", got, want)
	}
}

func TestDecoder_TextRuleRejectsInvalidBytes(t *testing.T) {
	for _, f := range invalidByteForms {
		for place, message := range textVariants(f.form) {
			t.Run(f.name+" "+place, func(t *testing.T) {
				_, err := decodeAll(t, withHeader(t, logMessageLine(message)), wire.DefaultLimits())
				requireTextError(t, err, 2, textNotUTF8)
			})
		}
		t.Run(f.name+" followed by a letter", func(t *testing.T) {
			_, err := decodeAll(t, withHeader(t, logMessageLine(f.form+"x")), wire.DefaultLimits())
			requireTextError(t, err, 2, textNotUTF8)
		})
	}
}

func TestDecoder_TextRuleRejectsUnpairedSurrogateEscapes(t *testing.T) {
	for _, f := range unpairedEscapeForms {
		if !strings.Contains(f.form, `\`) {
			t.Fatalf("test setup: %s holds no backslash", f.name)
		}
		// Forms that end in a lone backslash or a short escape are no JSON; the text rule only sees them through checkText.
		if strings.HasSuffix(f.form, `\`) || strings.HasSuffix(f.form, `\u12`) || strings.HasSuffix(f.form, `\uZZZZ`) {
			continue
		}
		for place, message := range textVariants(f.form) {
			t.Run(f.name+" "+place, func(t *testing.T) {
				_, err := decodeAll(t, withHeader(t, logMessageLine(message)), wire.DefaultLimits())
				requireTextError(t, err, 2, textUnpaired)
			})
		}
	}
}

func TestDecoder_TextRuleAcceptsValidEscapes(t *testing.T) {
	for _, f := range acceptedEscapeForms {
		t.Run(f.name, func(t *testing.T) {
			if !strings.Contains(f.form, `\`) {
				t.Fatalf("test setup: %s holds no backslash", f.name)
			}
			recs, err := decodeAll(t, withHeader(t, logMessageLine("a"+f.form+"b")), wire.DefaultLimits())
			if err != nil || len(recs) != 1 {
				t.Fatalf("decoding = %d records, %v, want 1 record, nil", len(recs), err)
			}
		})
	}
}

func TestDecoder_TextRuleAcceptsRawMultiByteText(t *testing.T) {
	recs, err := decodeAll(t, withHeader(t, logMessageLine("\u00e9\u20ac\U0001F600")), wire.DefaultLimits())
	if err != nil || len(recs) != 1 {
		t.Fatalf("decoding = %d records, %v, want 1 record, nil", len(recs), err)
	}
	logMessage("\u00e9\u20ac\U0001F600")(t, recs)
}

func TestDecoder_TextRuleCoversEveryPlaceOfALine(t *testing.T) {
	record := func(extra string) string {
		return strings.Replace(metricLine(1), `"captured_at"`, extra+`,"captured_at"`, 1)
	}
	header := func(extra string) string {
		return `{"format_major":1,"format_minor":0,` + hdrTail + `,` + extra + `}`
	}
	for _, value := range []string{"\xff", `\ud800`} {
		reason := textNotUTF8
		if value != "\xff" {
			reason = textUnpaired
		}
		t.Run("unknown record value "+reason, func(t *testing.T) {
			_, err := decodeAll(t, stream(t, headerJSON, record(`"x":"`+value+`"`)), wire.DefaultLimits())
			requireTextError(t, err, 2, reason)
		})
		t.Run("unknown record value in a list "+reason, func(t *testing.T) {
			_, err := decodeAll(t, stream(t, headerJSON, record(`"x":["`+value+`"]`)), wire.DefaultLimits())
			requireTextError(t, err, 2, reason)
		})
		t.Run("unknown header value "+reason, func(t *testing.T) {
			_, err := decodeAll(t, stream(t, header(`"x":"`+value+`"`), metricLine(1)), wire.DefaultLimits())
			requireTextError(t, err, 1, reason)
		})
		t.Run("header of major 2 "+reason, func(t *testing.T) {
			_, err := decodeAll(t, stream(t, `{"format_major":2,"x":"`+value+`"}`, metricLine(1)), wire.DefaultLimits())
			requireTextError(t, err, 1, reason)
		})
		t.Run("agent_id "+reason, func(t *testing.T) {
			line := `{"format_major":1,"format_minor":0,"agent_id":"` + value + `","boot_id":"` + testBootID + `","mode":"live"}`
			_, err := decodeAll(t, stream(t, line, metricLine(1)), wire.DefaultLimits())
			requireTextError(t, err, 1, reason)
		})
		t.Run("kind "+reason, func(t *testing.T) {
			line := strings.Replace(metricLine(1), `"kind":"metric"`, `"kind":"metric`+value+`"`, 1)
			_, err := decodeAll(t, stream(t, headerJSON, line), wire.DefaultLimits())
			requireTextError(t, err, 2, reason)
		})
		t.Run("label value "+reason, func(t *testing.T) {
			line := kindLine("metric", `{"name":"cpu","value":1,"labels":{"a":"`+value+`"}}`)
			_, err := decodeAll(t, stream(t, headerJSON, line), wire.DefaultLimits())
			requireTextError(t, err, 2, reason)
		})
		t.Run("unit "+reason, func(t *testing.T) {
			line := kindLine("metric", `{"name":"cpu","value":1,"unit":"`+value+`"}`)
			_, err := decodeAll(t, stream(t, headerJSON, line), wire.DefaultLimits())
			requireTextError(t, err, 2, reason)
		})
	}
}

func TestDecoder_TextRuleReturnsRecordsBeforeTheLine(t *testing.T) {
	data := withHeader(t, logMessageLine("a"), strings.Replace(logMessageLine("a\xffb"), `"seq":1`, `"seq":2`, 1))
	recs, err := decodeAll(t, data, wire.DefaultLimits())
	requireTextError(t, err, 3, textNotUTF8)
	if len(recs) != 1 {
		t.Errorf("records before the error = %d, want 1", len(recs))
	}
}

func TestDecoder_TextRuleErrorNamesNoContent(t *testing.T) {
	cases := []struct {
		name   string
		header string
		record string
		reason string
		line   int
	}{
		{"record byte", headerJSON, logMessageLine("SENTINELTEXT\xff"), textNotUTF8, 2},
		{"record escape", headerJSON, logMessageLine(`SENTINELTEXT\ud800`), textUnpaired, 2},
		{"header byte", `{"format_major":1,"format_minor":0,` + hdrTail + `,"x":"SENTINELTEXT` + "\xff" + `"}`, metricLine(1), textNotUTF8, 1},
		{"header escape", `{"format_major":1,"format_minor":0,` + hdrTail + `,"x":"SENTINELTEXT\ud800"}`, metricLine(1), textUnpaired, 1},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := decodeAll(t, stream(t, tc.header, tc.record), wire.DefaultLimits())
			requireTextError(t, err, tc.line, tc.reason)
			if strings.Contains(err.Error(), "SENTINEL") {
				t.Errorf("error text = %q, want no part of the line", err.Error())
			}
		})
	}
}

// syntaxFirstLines are lines that are not one complete JSON value; whatever bytes they hold, the syntax error stays.
func syntaxFirstLines(complete string) map[string]string {
	return map[string]string{
		"cut short inside a string after FF":          `{"kind":"log_line","x":"a` + "\xff",
		"cut short after a string holding FF":         `{"kind":"log_line","x":"a` + "\xff" + `"`,
		"cut short inside a string after escape":      `{"kind":"log_line","x":"a\ud800`,
		"FF after the object":                         complete + "\xff",
		"escaped surrogate string after the object":   complete + ` "\ud800"`,
		"cut short inside a string after bad pair":    `{"kind":"log_line","x":"\ud800\u0041`,
		"invalid byte after the object and a comma":   complete + ",\xff",
		"cut short after an object holding a bad key": `{"kind":"log_line","x\ud800":`,
	}
}

func TestDecoder_SyntaxErrorComesBeforeTextRule(t *testing.T) {
	for name, line := range syntaxFirstLines(metricLine(1)) {
		t.Run("record line "+name, func(t *testing.T) {
			_, err := decodeAll(t, stream(t, headerJSON, line), wire.DefaultLimits())
			requireSyntaxError(t, err)
		})
	}
	for name, line := range syntaxFirstLines(headerJSON) {
		t.Run("header line "+name, func(t *testing.T) {
			_, err := decodeAll(t, stream(t, line, metricLine(1)), wire.DefaultLimits())
			requireSyntaxError(t, err)
		})
	}
}

func requireSyntaxError(t *testing.T, err error) {
	t.Helper()
	if err == nil {
		t.Fatalf("decoding = nil, want a syntax error")
	}
	if !errors.Is(err, wire.ErrMalformed) {
		t.Errorf("error = %v, want errors.Is(err, ErrMalformed)", err)
	}
	for _, text := range []string{textNotUTF8, textUnpaired} {
		if strings.Contains(err.Error(), text) {
			t.Errorf("error text = %q, want the syntax error, not %q", err.Error(), text)
		}
	}
}

func TestDecoder_KeyRulesComeBeforeTextRule(t *testing.T) {
	withExtra := func(extra string) string {
		return strings.Replace(metricLine(1), `"captured_at"`, extra+`,"captured_at"`, 1)
	}
	cases := []struct {
		name   string
		extra  string
		reason string
	}{
		{"key not ASCII, byte FF in a value", "\"k\u00e9y\":1,\"x\":\"\xff\"", "key not ASCII"},
		{"key not ASCII, surrogate escape in a value", "\"k\u00e9y\":1,\"x\":\"\\ud800\"", "key not ASCII"},
		{"duplicate key, surrogate escape in a value", `"a":1,"a":2,"x":"\ud800"`, "duplicate key"},
		{"duplicate key, byte FF in a value", "\"a\":1,\"a\":2,\"x\":\"\xff\"", "duplicate key"},
		{"key of invalid UTF-8", "\"k\xffy\":1", "key not ASCII"},
		{"key with a surrogate escape", `"k\ud800y":1`, "key not ASCII"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := decodeAll(t, stream(t, headerJSON, withExtra(tc.extra)), wire.DefaultLimits())
			requireTextError(t, err, 2, tc.reason)
		})
	}
}

func TestDecoder_InvalidByteIsReportedBeforeUnpairedEscape(t *testing.T) {
	for _, message := range []string{"\xff" + `\ud800`, `\ud800` + "\xff"} {
		_, err := decodeAll(t, withHeader(t, logMessageLine(message)), wire.DefaultLimits())
		requireTextError(t, err, 2, textNotUTF8)
	}
}

// checkTextReason fails the test when CheckText does not report the want reason (empty for an accepted input) or the
// error is not ErrMalformed.
func checkTextReason(t *testing.T, input, want string) {
	t.Helper()
	err := wire.CheckText([]byte(input))
	got := ""
	if err != nil {
		if !errors.Is(err, wire.ErrMalformed) {
			t.Errorf("error = %v, want errors.Is(err, ErrMalformed)", err)
		}
		got = strings.TrimPrefix(err.Error(), textMalformed)
	}
	if got != want {
		t.Errorf("CheckText(%q) = %q, want %q", input, got, want)
	}
}

func TestCheckText(t *testing.T) {
	t.Run("accepted", func(t *testing.T) {
		accepted := []string{"", "plain ASCII", "\u00e9\u20ac\U0001F600", `\ud83d\ude00`, `\uD83D\uDE00`, `\uD83d\uDe00`, `\ufffd`, `\uffff`,
			`\\ud800`, `\"`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`, `\`, `\u`, `\u12`, `\u123`, `\uZZZZ`, `\ud83d\ude00\ud83d\ude00`,
			`\\\ud83d\ude00`, `\u0041\u0042`}
		for _, input := range accepted {
			checkTextReason(t, input, "")
		}
	})
	t.Run("whitespace inside an escape is no hex", func(t *testing.T) {
		for _, input := range []string{`\u d800`, `\u d80`, `\u+d800`} {
			checkTextReason(t, input, "")
		}
		checkTextReason(t, `\ud800 \udc00`, textUnpaired)
		checkTextReason(t, `\ud800\u dc00`, textUnpaired)
	})
	t.Run("invalid UTF-8", func(t *testing.T) {
		for _, f := range invalidByteForms {
			requireTextVariantsReason(t, f.form, textNotUTF8)
			checkTextReason(t, f.form+"x", textNotUTF8)
		}
	})
	t.Run("unpaired surrogate escapes", func(t *testing.T) {
		for _, f := range unpairedEscapeForms {
			requireTextVariantsReason(t, f.form, textUnpaired)
		}
		checkTextReason(t, `\ud800`+`\ud800`, textUnpaired)
	})
	t.Run("invalid byte before escape and after", func(t *testing.T) {
		checkTextReason(t, "\xff"+`\ud800`, textNotUTF8)
		checkTextReason(t, `\ud800`+"\xff", textNotUTF8)
	})
}

// requireTextVariantsReason checks every textVariants input of the form against the want reason.
func requireTextVariantsReason(t *testing.T, form, want string) {
	t.Helper()
	for _, input := range textVariants(form) {
		checkTextReason(t, input, want)
	}
}

func TestCheckText_AllocatesNothing(t *testing.T) {
	var sb strings.Builder
	for sb.Len() < 1<<20 {
		sb.WriteString(`valid text \ud83d\ude00 and \u00e9 `)
	}
	line := []byte(sb.String())
	if err := wire.CheckText(line); err != nil {
		t.Fatalf("test setup: CheckText() = %v, want nil", err)
	}
	if allocs := testing.AllocsPerRun(100, func() { _ = wire.CheckText(line) }); allocs != 0 {
		t.Errorf("CheckText() allocates %v times per call, want 0", allocs)
	}
}
