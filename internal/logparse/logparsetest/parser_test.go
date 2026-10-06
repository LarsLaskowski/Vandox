package logparsetest_test

import (
	"context"
	"errors"
	"io"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/logparse"
	"github.com/LarsLaskowski/Vandox/internal/logparse/logparsetest"
	"github.com/LarsLaskowski/Vandox/internal/model"
)

// ctxKey is the key of the marker value in a test context.
type ctxKey struct{}

var base = time.Date(2026, time.October, 6, 12, 0, 0, 0, time.UTC)

// collector is a logparse.Emitter that keeps what it receives; failAt makes the record with that 1-based
// ordinal return err, and cancelAt cancels the context after the record with that ordinal.
type collector struct {
	records  []model.Record
	skips    []skip
	failAt   int
	err      error
	cancelAt int
	cancel   context.CancelFunc
}

type skip struct {
	line   int64
	reason string
}

func (c *collector) Record(r model.Record) error {
	c.records = append(c.records, r)
	if c.failAt > 0 && len(c.records) == c.failAt {
		return c.err
	}
	if c.cancelAt > 0 && len(c.records) == c.cancelAt {
		c.cancel()
	}
	return nil
}

func (c *collector) Skip(line int64, reason string) {
	c.skips = append(c.skips, skip{line, reason})
}

// countingReader counts the bytes read from it.
type countingReader struct {
	r io.Reader
	n int
}

func (c *countingReader) Read(p []byte) (int, error) {
	n, err := c.r.Read(p)
	c.n += n
	return n, err
}

func TestParser_Type(t *testing.T) {
	p := &logparsetest.Parser{TypeName: "syslog"}

	if got := p.Type(); got != "syslog" {
		t.Errorf("Type() = %q, want %q", got, "syslog")
	}
}

func TestParser_Detect(t *testing.T) {
	t.Run("nil DetectFunc is NoMatch", func(t *testing.T) {
		p := &logparsetest.Parser{TypeName: "t"}

		if got := p.Detect(logparse.File{Name: "x"}, []byte("head")); got != logparse.NoMatch {
			t.Errorf("Detect() = %v, want NoMatch", got)
		}
	})
	t.Run("DetectFunc gets file and head and decides", func(t *testing.T) {
		var gotFile logparse.File
		var gotHead []byte
		p := &logparsetest.Parser{TypeName: "t", DetectFunc: func(f logparse.File, head []byte) logparse.Confidence {
			gotFile, gotHead = f, head
			return logparse.MatchContent
		}}
		file := logparse.File{Name: "var/log/syslog", ModTime: base}

		got := p.Detect(file, []byte("head"))

		if got != logparse.MatchContent {
			t.Errorf("Detect() = %v, want MatchContent", got)
		}
		if gotFile != file || string(gotHead) != "head" {
			t.Errorf("DetectFunc got %+v, %q, want %+v, %q", gotFile, gotHead, file, "head")
		}
	})
}

func TestParser_Parse(t *testing.T) {
	t.Run("nil ParseFunc reads nothing and returns nil", func(t *testing.T) {
		p := &logparsetest.Parser{TypeName: "t"}
		r := &countingReader{r: strings.NewReader("some content\n")}
		out := &collector{}

		err := p.Parse(context.Background(), logparse.File{Name: "x"}, r, out)

		if err != nil {
			t.Errorf("Parse() error = %v, want nil", err)
		}
		if r.n != 0 {
			t.Errorf("Parse() read %d bytes, want none", r.n)
		}
		if len(out.records) != 0 || len(out.skips) != 0 {
			t.Errorf("Parse() emitted %d records and %d skips, want none", len(out.records), len(out.skips))
		}
	})
	t.Run("ParseFunc gets its arguments and its error is returned", func(t *testing.T) {
		boom := errors.New("boom")
		var gotFile logparse.File
		var gotCtx context.Context
		var gotReader io.Reader
		var gotOut logparse.Emitter
		p := &logparsetest.Parser{TypeName: "t", ParseFunc: func(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error {
			gotCtx, gotFile, gotReader, gotOut = ctx, f, r, out
			return boom
		}}
		ctx := context.WithValue(context.Background(), ctxKey{}, "marker")
		file := logparse.File{Name: "a/b.log", ModTime: base}
		r := strings.NewReader("x")
		out := &collector{}

		err := p.Parse(ctx, file, r, out)

		if !errors.Is(err, boom) {
			t.Errorf("Parse() error = %v, want %v", err, boom)
		}
		if gotCtx != ctx || gotFile != file || gotReader != io.Reader(r) || gotOut != logparse.Emitter(out) {
			t.Errorf("ParseFunc got (%v, %+v, %v, %v), want the arguments of Parse", gotCtx, gotFile, gotReader, gotOut)
		}
	})
}

func TestParser_Parsed(t *testing.T) {
	t.Run("none before the first call", func(t *testing.T) {
		p := &logparsetest.Parser{TypeName: "t"}

		if got := p.Parsed(); len(got) != 0 {
			t.Errorf("Parsed() = %v, want none", got)
		}
	})
	t.Run("every call in order, also with a nil ParseFunc and with an error", func(t *testing.T) {
		files := []logparse.File{{Name: "one", ModTime: base}, {Name: "two"}, {Name: "three", ModTime: base.Add(time.Hour)}}
		plain := &logparsetest.Parser{TypeName: "t"}
		failing := &logparsetest.Parser{TypeName: "t", ParseFunc: func(context.Context, logparse.File, io.Reader, logparse.Emitter) error {
			return errors.New("fails")
		}}

		for _, p := range []*logparsetest.Parser{plain, failing} {
			for _, f := range files {
				_ = p.Parse(context.Background(), f, strings.NewReader(""), &collector{})
			}
			if got := p.Parsed(); !reflect.DeepEqual(got, files) {
				t.Errorf("Parsed() = %+v, want %+v", got, files)
			}
		}
	})
}

func TestHeadPrefix(t *testing.T) {
	tests := []struct {
		name   string
		prefix string
		head   string
		want   logparse.Confidence
	}{
		{"head starts with the prefix", "<?xml", "<?xml version", logparse.MatchContent},
		{"head equals the prefix", "abc", "abc", logparse.MatchContent},
		{"other content", "abc", "abd", logparse.NoMatch},
		{"prefix inside the head only", "abc", "xabc", logparse.NoMatch},
		{"head shorter than the prefix", "abc", "ab", logparse.NoMatch},
		{"empty head", "abc", "", logparse.NoMatch},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			detect := logparsetest.HeadPrefix(tc.prefix, logparse.MatchContent)
			if detect == nil {
				t.Fatal("HeadPrefix() = nil, want a DetectFunc")
			}

			got := detect(logparse.File{Name: "ignored"}, []byte(tc.head))

			if got != tc.want {
				t.Errorf("HeadPrefix(%q)(head %q) = %v, want %v", tc.prefix, tc.head, got, tc.want)
			}
		})
	}
	t.Run("reports the given confidence", func(t *testing.T) {
		detect := logparsetest.HeadPrefix("a", logparse.MatchName)
		if detect == nil {
			t.Fatal("HeadPrefix() = nil, want a DetectFunc")
		}

		if got := detect(logparse.File{}, []byte("a")); got != logparse.MatchName {
			t.Errorf("HeadPrefix(a, MatchName)(a) = %v, want MatchName", got)
		}
	})
}

// linesFunc returns the ParseFunc of Lines, failing the test when it is nil.
func linesFunc(t *testing.T, source string) func(context.Context, logparse.File, io.Reader, logparse.Emitter) error {
	t.Helper()
	fn := logparsetest.Lines(source, base)
	if fn == nil {
		t.Fatal("Lines() = nil, want a ParseFunc")
	}
	return fn
}

// checkLineRecord checks that rec is the record Lines emits for the given line number and text.
func checkLineRecord(t *testing.T, rec model.Record, line int64, text string) {
	t.Helper()
	if rec.Origin != model.OriginImport || rec.Source != "syslog" {
		t.Errorf("line %d origin, source = %q, %q, want %q, %q", line, rec.Origin, rec.Source, model.OriginImport, "syslog")
	}
	if want := base.Add(time.Duration(line) * time.Second); !rec.CapturedAt.Equal(want) {
		t.Errorf("line %d CapturedAt = %v, want %v (base plus the line number in seconds)", line, rec.CapturedAt, want)
	}
	if _, off := rec.CapturedAt.Zone(); off != 0 {
		t.Errorf("line %d CapturedAt = %v, want UTC", line, rec.CapturedAt)
	}
	ll, ok := rec.Data.(*model.LogLine)
	if !ok {
		t.Fatalf("line %d data = %T, want *model.LogLine", line, rec.Data)
	}
	if ll.Log != "syslog" || ll.Message != text || ll.Truncated {
		t.Errorf("line %d log line = %+v, want Log %q, Message %q, not truncated", line, *ll, "syslog", text)
	}
	if err := rec.Validate(); err != nil {
		t.Errorf("line %d Validate() = %v, want nil", line, err)
	}
}

func TestLines_EmitsOneRecordPerLine(t *testing.T) {
	out := &collector{}

	err := linesFunc(t, "syslog")(context.Background(), logparse.File{Name: "x"}, strings.NewReader("alpha\nbeta\n#comment\ngamma"), out)

	if err != nil {
		t.Fatalf("ParseFunc error = %v, want nil", err)
	}
	wantLines := []struct {
		line int64
		text string
	}{{1, "alpha"}, {2, "beta"}, {4, "gamma"}}
	if len(out.records) != len(wantLines) {
		t.Fatalf("records = %d, want %d", len(out.records), len(wantLines))
	}
	for i, w := range wantLines {
		checkLineRecord(t, out.records[i], w.line, w.text)
	}
	if want := []skip{{3, "comment line"}}; !reflect.DeepEqual(out.skips, want) {
		t.Errorf("skips = %+v, want %+v", out.skips, want)
	}
}

func TestLines_OnlyALeadingHashIsAComment(t *testing.T) {
	out := &collector{}

	err := linesFunc(t, "t")(context.Background(), logparse.File{}, strings.NewReader(" #indented\na#b\n#\n"), out)

	if err != nil {
		t.Fatalf("ParseFunc error = %v, want nil", err)
	}
	if len(out.records) != 2 {
		t.Errorf("records = %d, want 2 (only a line starting with # is a comment)", len(out.records))
	}
	if want := []skip{{3, "comment line"}}; !reflect.DeepEqual(out.skips, want) {
		t.Errorf("skips = %+v, want %+v", out.skips, want)
	}
}

func TestLines_ReportsTruncatedLines(t *testing.T) {
	long := strings.Repeat("x", logparse.MaxLineBytes+50)
	out := &collector{}

	err := linesFunc(t, "t")(context.Background(), logparse.File{}, strings.NewReader(long+"\nshort\n"), out)

	if err != nil {
		t.Fatalf("ParseFunc error = %v, want nil", err)
	}
	if len(out.records) != 2 {
		t.Fatalf("records = %d, want 2", len(out.records))
	}
	first, second := out.records[0].Data.(*model.LogLine), out.records[1].Data.(*model.LogLine)
	if !first.Truncated || len(first.Message) != logparse.MaxLineBytes {
		t.Errorf("long line: Truncated = %v, Message length = %d, want true, %d", first.Truncated, len(first.Message), logparse.MaxLineBytes)
	}
	if second.Truncated || second.Message != "short" {
		t.Errorf("next line = %+v, want %q not truncated", *second, "short")
	}
}

func TestLines_EmptyInput(t *testing.T) {
	out := &collector{}

	err := linesFunc(t, "t")(context.Background(), logparse.File{}, strings.NewReader(""), out)

	if err != nil || len(out.records) != 0 || len(out.skips) != 0 {
		t.Errorf("ParseFunc on empty input = %v with %d records and %d skips, want nil, 0, 0", err, len(out.records), len(out.skips))
	}
}

func TestLines_StopsWithTheContext(t *testing.T) {
	input := "a\nb\nc\nd\n"
	t.Run("already done", func(t *testing.T) {
		ctx, cancel := context.WithCancel(context.Background())
		cancel()
		out := &collector{}

		err := linesFunc(t, "t")(ctx, logparse.File{}, strings.NewReader(input), out)

		if !errors.Is(err, context.Canceled) {
			t.Errorf("ParseFunc error = %v, want context.Canceled", err)
		}
		if len(out.records) != 0 {
			t.Errorf("records = %d, want none", len(out.records))
		}
	})
	t.Run("done while parsing", func(t *testing.T) {
		ctx, cancel := context.WithCancel(context.Background())
		defer cancel()
		out := &collector{cancelAt: 1, cancel: cancel}

		err := linesFunc(t, "t")(ctx, logparse.File{}, strings.NewReader(input), out)

		if !errors.Is(err, context.Canceled) {
			t.Errorf("ParseFunc error = %v, want context.Canceled", err)
		}
		if len(out.records) != 1 {
			t.Errorf("records = %d, want 1 (stopped before the second line)", len(out.records))
		}
	})
}

func TestLines_ReturnsTheEmittersError(t *testing.T) {
	boom := errors.New("emitter failed")
	out := &collector{failAt: 2, err: boom}

	err := linesFunc(t, "t")(context.Background(), logparse.File{}, strings.NewReader("a\nb\nc\nd\n"), out)

	if !errors.Is(err, boom) {
		t.Errorf("ParseFunc error = %v, want %v", err, boom)
	}
	if len(out.records) != 2 {
		t.Errorf("records = %d, want 2 (nothing after the failing record)", len(out.records))
	}
}
