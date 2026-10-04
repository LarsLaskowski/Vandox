package wire_test

import (
	"bytes"
	"compress/gzip"
	"encoding/json"
	"errors"
	"fmt"
	"io"
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
	if w.line > 0 {
		var de *wire.DecodeError
		if !errors.As(err, &de) {
			t.Fatalf("error = %T (%v), want a *wire.DecodeError", err, err)
		}
		if de.Line != w.line {
			t.Errorf("DecodeError.Line = %d, want %d (%v)", de.Line, w.line, err)
		}
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

func TestDecoder_AcceptedForms(t *testing.T) {
	const keyHeader = `{"FORMAT_MAJOR":1,"Format_Minor":0,"AGENT_ID":"agent-1","Boot_ID":"` + testBootID + `","MODE":"live"}`
	cases := []struct {
		name  string
		data  []byte
		count int
		check func(t *testing.T, recs []model.Record)
	}{
		{"CRLF line ends", gzipString(t, headerJSON+"\r\n"+metricLine(1)+"\r\n"+metricLine(2)+"\r\n"), 2, nil},
		{"last line without newline", gzipString(t, headerJSON+"\n"+metricLine(1)), 1, nil},
		{"spaces and tabs around objects", gzipString(t, " \t"+headerJSON+"\t \n \t"+metricLine(1)+" \t\n"), 1, nil},
		{"keys in other case", stream(t, keyHeader, `{"KIND":"metric","Source":"proc.stat","SEQ":4,"Captured_At":`+tsJSON+`,"DATA":{"NAME":"cpu","Value":1}}`), 1,
			func(t *testing.T, recs []model.Record) {
				if recs[0].Kind() != model.KindMetric || recs[0].Seq != 4 {
					t.Errorf("record = kind %q seq %d, want metric seq 4", recs[0].Kind(), recs[0].Seq)
				}
			}},
		{"duplicate keys, last wins", stream(t, headerJSON, `{"kind":"metric","source":"proc.stat","seq":9,"seq":3,"captured_at":`+tsJSON+`,"data":{"name":"x y","name":"cpu","value":1}}`), 1,
			func(t *testing.T, recs []model.Record) {
				if recs[0].Seq != 3 {
					t.Errorf("Seq = %d, want 3", recs[0].Seq)
				}
			}},
		{"unknown keys at any depth", stream(t, headerJSON, `{"kind":"metric","source":"proc.stat","seq":1,"captured_at":`+tsJSON+`,"extra":{"a":[1,{"b":null}]},"data":{"name":"cpu","value":1,"extra":[[[]]]}}`), 1, nil},
		{"origin and received_at keys are ignored", stream(t, headerJSON, `{"kind":"metric","origin":"backend","received_at":"2026-03-02T00:00:00Z","source":"proc.stat","seq":1,"captured_at":`+tsJSON+`,"data":`+okData+`}`), 1,
			func(t *testing.T, recs []model.Record) {
				if recs[0].Origin != model.OriginAgent {
					t.Errorf("Origin = %q, want %q", recs[0].Origin, model.OriginAgent)
				}
			}},
		{"time forms with zero offset", withHeader(t,
			envelope("metric", 1, `"2026-03-01T12:00:00+00:00"`, okData),
			envelope("metric", 2, `"2026-03-01T12:00:00-00:00"`, okData),
			envelope("metric", 3, `"2026-03-01T12:00:00.123456789Z"`, okData)), 3,
			func(t *testing.T, recs []model.Record) {
				for i, r := range recs {
					if _, off := r.CapturedAt.Zone(); off != 0 {
						t.Errorf("record %d CapturedAt offset = %d, want 0", i, off)
					}
				}
				if want := time.Date(2026, 3, 1, 12, 0, 0, 123456789, time.UTC); !recs[2].CapturedAt.Equal(want) {
					t.Errorf("record 2 CapturedAt = %v, want %v", recs[2].CapturedAt, want)
				}
			}},
		{"invalid UTF-8 in a string", withHeader(t, kindLine("log_line", "{\"log\":\"journal\",\"message\":\"a\xffb\"}")), 1,
			func(t *testing.T, recs []model.Record) {
				if got := recs[0].Data.(*model.LogLine).Message; got != "a�b" {
					t.Errorf("Message = %q, want %q", got, "a�b")
				}
			}},
		{"escaped control characters in a text", withHeader(t, kindLine("log_line", `{"log":"journal","message":"a\u0000b\n"}`)), 1,
			func(t *testing.T, recs []model.Record) {
				if got := recs[0].Data.(*model.LogLine).Message; got != "a\x00b\n" {
					t.Errorf("Message = %q, want %q", got, "a\x00b\n")
				}
			}},
		{"IPv4-mapped address without zone", withHeader(t, kindLine("connection_snapshot", `{"remotes":[{"addr":"::ffff:1.2.3.4","count":1}]}`)), 1, nil},
		{"empty connection snapshot", withHeader(t, kindLine("connection_snapshot", `{"complete":true}`)), 1, nil},
		{"Kelvin sign and long s in keys", withHeader(t, "{\"Kind\":\"metric\",\"source\":\"proc.stat\",\"ſeq\":5,\"captured_at\":"+tsJSON+",\"data\":"+okData+"}"), 1,
			func(t *testing.T, recs []model.Record) {
				if recs[0].Kind() != model.KindMetric || recs[0].Seq != 5 {
					t.Errorf("record = kind %q seq %d, want metric seq 5", recs[0].Kind(), recs[0].Seq)
				}
			}},
		{"folded duplicate key wins", withHeader(t, "{\"kind\":\"gap\",\"Kind\":\"metric\",\"source\":\"proc.stat\",\"seq\":1,\"captured_at\":"+tsJSON+",\"data\":"+okData+"}"), 1,
			func(t *testing.T, recs []model.Record) {
				if recs[0].Kind() != model.KindMetric {
					t.Errorf("Kind() = %q, want %q", recs[0].Kind(), model.KindMetric)
				}
			}},
	}
	for _, tc := range cases {
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

func TestDecoder_Limits(t *testing.T) {
	small := func(line, rec int, batch int64) wire.Limits {
		return wire.Limits{MaxLineBytes: line, MaxBatchBytes: batch, MaxRecords: rec}
	}

	t.Run("line of exactly MaxLineBytes is read", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, padded(t, metricLine(1), 400)), small(400, 10, 1<<20))
		if err != nil || len(recs) != 1 {
			t.Errorf("decoding = %d records, %v, want 1 record and nil", len(recs), err)
		}
	})

	t.Run("line one byte over MaxLineBytes", func(t *testing.T) {
		_, err := decodeAll(t, withHeader(t, padded(t, metricLine(1), 401)), small(400, 10, 1<<20))
		want{is: wire.ErrLimitExceeded, line: 2}.check(t, err)
	})

	t.Run("carriage return counts towards the line", func(t *testing.T) {
		recs, err := decodeAll(t, gzipString(t, headerJSON+"\n"+padded(t, metricLine(1), 399)+"\r\n"), small(400, 10, 1<<20))
		if err != nil || len(recs) != 1 {
			t.Errorf("399 bytes plus CR: decoding = %d records, %v, want 1 record and nil", len(recs), err)
		}
		_, err = decodeAll(t, gzipString(t, headerJSON+"\n"+padded(t, metricLine(1), 400)+"\r\n"), small(400, 10, 1<<20))
		want{is: wire.ErrLimitExceeded}.check(t, err)
	})

	t.Run("header line over MaxLineBytes", func(t *testing.T) {
		_, err := decodeAll(t, stream(t, padded(t, headerJSON, 500), metricLine(1)), small(400, 10, 1<<20))
		want{is: wire.ErrLimitExceeded, line: 1}.check(t, err)
	})

	t.Run("decompressed size over MaxBatchBytes", func(t *testing.T) {
		lines := make([]string, 100)
		for i := range lines {
			lines[i] = metricLine(i + 1)
		}
		recs, err := decodeAll(t, withHeader(t, lines...), small(1<<20, 1000, 2000))
		want{is: wire.ErrLimitExceeded}.check(t, err)
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
		want{is: wire.ErrLimitExceeded}.check(t, err)
	})

	t.Run("exactly MaxRecords records", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLine(1), metricLine(2), metricLine(3)), small(1<<20, 3, 1<<20))
		if err != nil || len(recs) != 3 {
			t.Errorf("decoding = %d records, %v, want 3 records and nil", len(recs), err)
		}
	})

	t.Run("MaxRecords plus one records", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLine(1), metricLine(2), metricLine(3), metricLine(4)), small(1<<20, 3, 1<<20))
		want{is: wire.ErrLimitExceeded}.check(t, err)
		if len(recs) != 3 {
			t.Errorf("decoded %d records before the error, want 3", len(recs))
		}
	})

	t.Run("zero and negative fields mean the defaults", func(t *testing.T) {
		for name, lim := range map[string]wire.Limits{
			"zero":     {},
			"negative": {MaxLineBytes: -1, MaxBatchBytes: -1, MaxRecords: -1},
		} {
			recs, err := decodeAll(t, withHeader(t, metricLine(1), metricLine(2)), lim)
			if err != nil || len(recs) != 2 {
				t.Errorf("%s limits: decoding = %d records, %v, want 2 records and nil", name, len(recs), err)
			}
		}
	})

	t.Run("default line limit applies when the field is zero", func(t *testing.T) {
		def := wire.DefaultLimits()
		lim := wire.Limits{MaxRecords: 5}
		recs, err := decodeAll(t, withHeader(t, padded(t, metricLine(1), def.MaxLineBytes)), lim)
		if err != nil || len(recs) != 1 {
			t.Errorf("line of %d bytes: decoding = %d records, %v, want 1 record and nil", def.MaxLineBytes, len(recs), err)
		}
		_, err = decodeAll(t, withHeader(t, padded(t, metricLine(1), def.MaxLineBytes+1)), lim)
		want{is: wire.ErrLimitExceeded}.check(t, err)
	})

	t.Run("default record limit applies when the field is zero", func(t *testing.T) {
		lines := make([]string, wire.DefaultLimits().MaxRecords+1)
		for i := range lines {
			lines[i] = metricLine(i + 1)
		}
		recs, err := decodeAll(t, withHeader(t, lines...), wire.Limits{})
		want{is: wire.ErrLimitExceeded}.check(t, err)
		if len(recs) != wire.DefaultLimits().MaxRecords {
			t.Errorf("decoded %d records before the error, want %d", len(recs), wire.DefaultLimits().MaxRecords)
		}
	})

	t.Run("default batch limit applies when the field is zero", func(t *testing.T) {
		def := wire.DefaultLimits()
		lines := make([]string, 0, 18)
		for i := range 17 {
			lines = append(lines, padded(t, metricLine(i+1), def.MaxLineBytes))
		}
		_, err := decodeAll(t, withHeader(t, lines...), wire.Limits{})
		want{is: wire.ErrLimitExceeded}.check(t, err)
	})
}

func TestDecoder_Integrity(t *testing.T) {
	lines := []string{metricLine(1), metricLine(2), metricLine(3), metricLine(4), metricLine(5)}
	intact := withHeader(t, lines...)
	damaged := func(f func(b []byte) []byte) []byte {
		return f(append([]byte(nil), intact...))
	}

	t.Run("corrupted CRC", func(t *testing.T) {
		data := damaged(func(b []byte) []byte { b[len(b)-8] ^= 0xff; return b })
		recs, err := decodeAll(t, data, wire.DefaultLimits())
		want{is: wire.ErrMalformed}.check(t, err)
		if len(recs) != 5 {
			t.Errorf("decoded %d records before the error, want 5", len(recs))
		}
	})

	t.Run("corrupted ISIZE", func(t *testing.T) {
		data := damaged(func(b []byte) []byte { b[len(b)-1] ^= 0xff; return b })
		recs, err := decodeAll(t, data, wire.DefaultLimits())
		want{is: wire.ErrMalformed}.check(t, err)
		if len(recs) != 5 {
			t.Errorf("decoded %d records before the error, want 5", len(recs))
		}
	})

	t.Run("missing trailer", func(t *testing.T) {
		data := damaged(func(b []byte) []byte { return b[:len(b)-8] })
		recs, err := decodeAll(t, data, wire.DefaultLimits())
		want{is: wire.ErrMalformed}.check(t, err)
		if len(recs) != 5 {
			t.Errorf("decoded %d records before the error, want 5", len(recs))
		}
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
		data := damaged(func(b []byte) []byte { return append(b, []byte("this is not a gzip member")...) })
		recs, err := decodeAll(t, data, wire.DefaultLimits())
		want{is: wire.ErrMalformed}.check(t, err)
		if len(recs) != 5 {
			t.Errorf("decoded %d records before the error, want 5", len(recs))
		}
	})

	t.Run("concatenated gzip members", func(t *testing.T) {
		data := append(gzipString(t, joinLines(headerJSON, metricLine(1))), gzipString(t, joinLines(metricLine(2), metricLine(3)))...)
		recs, err := decodeAll(t, data, wire.DefaultLimits())
		if err != nil || len(recs) != 3 {
			t.Errorf("decoding = %d records, %v, want 3 records and nil", len(recs), err)
		}
	})

	t.Run("header only", func(t *testing.T) {
		d, err := wire.NewDecoder(bytes.NewReader(stream(t, headerJSON)), wire.DefaultLimits())
		if err != nil {
			t.Fatalf("NewDecoder() = %v, want nil", err)
		}
		if _, err := d.Next(); !errors.Is(err, wire.ErrEmptyBatch) {
			t.Errorf("Next() = %v, want ErrEmptyBatch", err)
		}
	})
}

func TestDecoder_BatchRules(t *testing.T) {
	t.Run("equal sequence numbers", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLine(1), metricLine(2), metricLine(2)), wire.DefaultLimits())
		want{is: wire.ErrSequence, line: 4}.check(t, err)
		if len(recs) != 2 {
			t.Errorf("decoded %d records before the error, want 2", len(recs))
		}
	})

	t.Run("decreasing sequence numbers", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLine(5), metricLine(4)), wire.DefaultLimits())
		want{is: wire.ErrSequence, line: 3}.check(t, err)
		if len(recs) != 1 {
			t.Errorf("decoded %d records before the error, want 1", len(recs))
		}
	})

	t.Run("gaps in sequence numbers are allowed", func(t *testing.T) {
		recs, err := decodeAll(t, withHeader(t, metricLine(5), metricLine(9), metricLine(1000)), wire.DefaultLimits())
		if err != nil || len(recs) != 3 {
			t.Errorf("decoding = %d records, %v, want 3 records and nil", len(recs), err)
		}
	})

	t.Run("seq zero", func(t *testing.T) {
		_, err := decodeAll(t, withHeader(t, envelope("metric", 0, tsJSON, okData)), wire.DefaultLimits())
		want{is: model.ErrInvalid, field: "seq", line: 2}.check(t, err)
	})

	t.Run("errors are sticky", func(t *testing.T) {
		d, err := wire.NewDecoder(bytes.NewReader(withHeader(t, metricLine(1), metricLine(1), metricLine(2))), wire.DefaultLimits())
		if err != nil {
			t.Fatalf("NewDecoder() = %v, want nil", err)
		}
		if _, err := d.Next(); err != nil {
			t.Fatalf("first Next() = %v, want nil", err)
		}
		_, first := d.Next()
		want{is: wire.ErrSequence, line: 3}.check(t, first)
		for i := range 3 {
			rec, again := d.Next()
			if again == nil || again.Error() != first.Error() || !errors.Is(again, wire.ErrSequence) {
				t.Errorf("Next() #%d after the error = %v, want the same error %v", i+1, again, first)
			}
			if rec.Data != nil {
				t.Errorf("Next() #%d after the error returned a record %+v, want none", i+1, rec)
			}
		}
	})

	t.Run("io.EOF is repeated", func(t *testing.T) {
		d, err := wire.NewDecoder(bytes.NewReader(withHeader(t, metricLine(1))), wire.DefaultLimits())
		if err != nil {
			t.Fatalf("NewDecoder() = %v, want nil", err)
		}
		if _, err := d.Next(); err != nil {
			t.Fatalf("first Next() = %v, want nil", err)
		}
		for i := range 3 {
			if _, err := d.Next(); !errors.Is(err, io.EOF) {
				t.Errorf("Next() #%d at the end = %v, want io.EOF", i+1, err)
			}
		}
	})

	t.Run("empty batch error is sticky", func(t *testing.T) {
		d, err := wire.NewDecoder(bytes.NewReader(stream(t, headerJSON)), wire.DefaultLimits())
		if err != nil {
			t.Fatalf("NewDecoder() = %v, want nil", err)
		}
		for i := range 2 {
			if _, err := d.Next(); !errors.Is(err, wire.ErrEmptyBatch) {
				t.Errorf("Next() #%d = %v, want ErrEmptyBatch", i+1, err)
			}
		}
	})
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
