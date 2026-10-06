package logparse_test

import (
	"errors"
	"io"
	"runtime"
	"slices"
	"strings"
	"testing"
	"testing/iotest"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/logparse"
)

// readerWrappers return the same content in different read sizes; the lines must not depend on them.
var readerWrappers = []struct {
	name string
	wrap func(io.Reader) io.Reader
}{
	{"plain", func(r io.Reader) io.Reader { return r }},
	{"one byte per read", iotest.OneByteReader},
	{"half reads", iotest.HalfReader},
	{"data with the error", iotest.DataErrReader},
}

// readLines reads r to EOF and returns the lines and truncation flags; it fails the test on another error and
// when Line() does not count from 1.
func readLines(t *testing.T, r io.Reader) (lines []string, truncated []bool) {
	t.Helper()
	lr := logparse.NewLineReader(r)
	if got := lr.Line(); got != 0 {
		t.Errorf("Line() before the first Next = %d, want 0", got)
	}
	for {
		line, trunc, err := lr.Next()
		if errors.Is(err, io.EOF) {
			return lines, truncated
		}
		if err != nil {
			t.Fatalf("Next() error = %v, want a line or io.EOF", err)
		}
		lines = append(lines, string(line))
		truncated = append(truncated, trunc)
		if got, want := lr.Line(), int64(len(lines)); got != want {
			t.Errorf("Line() after line %d = %d, want %d", len(lines), got, want)
		}
		if len(lines) > 1000 {
			t.Fatal("Next() did not reach io.EOF within 1000 lines")
		}
	}
}

// checkLines reads r completely and compares the lines with want; no line may be reported truncated.
func checkLines(t *testing.T, r io.Reader, input string, want []string) {
	t.Helper()
	got, truncated := readLines(t, r)
	if len(got) != len(want) {
		t.Fatalf("lines of %q = %q, want %q", input, got, want)
	}
	for i := range got {
		if got[i] != want[i] {
			t.Errorf("line %d of %q = %q, want %q", i+1, input, got[i], want[i])
		}
		if truncated[i] {
			t.Errorf("line %d of %q reported truncated, want false", i+1, input)
		}
	}
}

func TestLineReader_Lines(t *testing.T) {
	tests := []struct {
		name  string
		input string
		want  []string
	}{
		{"empty input", "", nil},
		{"one line", "a\n", []string{"a"}},
		{"two lines", "a\nb\n", []string{"a", "b"}},
		{"last line without newline", "a\nb", []string{"a", "b"}},
		{"only a last line", "abc", []string{"abc"}},
		{"CRLF", "a\r\nb\r\n", []string{"a", "b"}},
		{"CRLF and last line without newline", "a\r\nb", []string{"a", "b"}},
		{"lone CR stays", "a\rb\n", []string{"a\rb"}},
		{"CR at the end without LF stays", "a\r", []string{"a\r"}},
		{"two CRs before the LF: only the last is removed", "a\r\r\n", []string{"a\r"}},
		{"empty lines", "\n\n", []string{"", ""}},
		{"empty line between lines", "a\n\nb\n", []string{"a", "", "b"}},
		{"CRLF alone is an empty line", "\r\n", []string{""}},
		{"NUL and invalid UTF-8 pass through", "a\x00\xff\nb\n", []string{"a\x00\xff", "b"}},
	}
	for _, tc := range tests {
		for _, w := range readerWrappers {
			t.Run(tc.name+"/"+w.name, func(t *testing.T) {
				checkLines(t, w.wrap(strings.NewReader(tc.input)), tc.input, tc.want)
			})
		}
	}
}

func TestLineReader_EOFIsSticky(t *testing.T) {
	lr := logparse.NewLineReader(strings.NewReader("a\n"))
	if _, _, err := lr.Next(); err != nil {
		t.Fatalf("first Next() error = %v, want nil", err)
	}

	for i := range 2 {
		if _, _, err := lr.Next(); !errors.Is(err, io.EOF) {
			t.Errorf("Next() call %d after the last line error = %v, want io.EOF", i+1, err)
		}
	}
}

// readUntilError calls Next until it fails (at most four times) and returns the lines and the error.
func readUntilError(lr *logparse.LineReader) (lines []string, err error) {
	for range 4 {
		var line []byte
		line, _, err = lr.Next()
		if err != nil {
			return lines, err
		}
		lines = append(lines, string(line))
	}
	return lines, err
}

func TestLineReader_ReadError(t *testing.T) {
	boom := errors.New("boom")
	tests := []struct {
		name      string
		input     io.Reader
		wantFirst string // the first line before the error, "" when there is none
	}{
		{"immediately", iotest.ErrReader(boom), ""},
		{"after a complete line", io.MultiReader(strings.NewReader("ab\n"), iotest.ErrReader(boom)), "ab"},
		{"in the middle of a line", io.MultiReader(strings.NewReader("ab\ncd"), iotest.ErrReader(boom)), "ab"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			lines, err := readUntilError(logparse.NewLineReader(tc.input))

			if !errors.Is(err, boom) || errors.Is(err, io.EOF) {
				t.Fatalf("Next() error = %v, want it to wrap the read error %v and not be io.EOF", err, boom)
			}
			if tc.wantFirst != "" && (len(lines) == 0 || lines[0] != tc.wantFirst) {
				t.Errorf("lines before the error = %q, want to start with %q", lines, tc.wantFirst)
			}
			if slices.Contains(lines, "cd") {
				t.Errorf("lines before the error = %q, want the unfinished line %q not returned as a line", lines, "cd")
			}
		})
	}
}

// repeatByte returns n copies of b followed by the tails.
func repeatByte(b byte, n int, tails ...string) string {
	return strings.Repeat(string(b), n) + strings.Join(tails, "")
}

// longLine is one case of the long line tests.
type longLine struct {
	name      string
	input     string
	wantLine  string
	wantTrunc bool
	wantNext  string // the second line, "" when the input has none
}

// checkLongLine reads the first line of r and compares it, and then the rest, with the case.
func checkLongLine(t *testing.T, r io.Reader, tc longLine) {
	t.Helper()
	lr := logparse.NewLineReader(r)

	line, trunc, err := lr.Next()

	if err != nil {
		t.Fatalf("Next() error = %v, want nil", err)
	}
	if len(line) > logparse.MaxLineBytes || !utf8.Valid(line) {
		t.Errorf("Next() returned %d bytes (valid UTF-8: %v), want at most %d bytes cut at a rune boundary", len(line), utf8.Valid(line), logparse.MaxLineBytes)
	}
	if string(line) != tc.wantLine {
		t.Errorf("Next() line has %d bytes and differs from the expected %d bytes (want a prefix cut at the limit)", len(line), len(tc.wantLine))
	}
	if trunc != tc.wantTrunc {
		t.Errorf("Next() truncated = %v, want %v", trunc, tc.wantTrunc)
	}
	if tc.wantNext == "" {
		if _, _, err := lr.Next(); !errors.Is(err, io.EOF) {
			t.Errorf("second Next() error = %v, want io.EOF", err)
		}
		return
	}
	next, nextTrunc, err := lr.Next()
	if err != nil || string(next) != tc.wantNext || nextTrunc {
		t.Errorf("second Next() = %q, %v, %v, want %q, false, nil", next, nextTrunc, err, tc.wantNext)
	}
	if got := lr.Line(); got != 2 {
		t.Errorf("Line() after the second line = %d, want 2 (a cut line is one line)", got)
	}
}

func TestLineReader_LongLines(t *testing.T) {
	const limit = logparse.MaxLineBytes
	tests := []longLine{
		{"exactly MaxLineBytes is kept whole", repeatByte('a', limit, "\nnext\n"), repeatByte('a', limit), false, "next"},
		{"one byte over is cut", repeatByte('a', limit+1, "\nnext\n"), repeatByte('a', limit), true, "next"},
		{"far over is cut and the rest discarded", repeatByte('a', 5*limit+7, "\nnext\n"), repeatByte('a', limit), true, "next"},
		{"long last line without newline", repeatByte('a', limit+10), repeatByte('a', limit), true, ""},
		{"long line ended by CRLF", repeatByte('a', limit+10, "\r\nnext\n"), repeatByte('a', limit), true, "next"},
		{"2-byte rune straddling the limit", repeatByte('a', limit-1, "\u00e9tail\nnext\n"), repeatByte('a', limit-1), true, "next"},
		{"3-byte rune straddling the limit", repeatByte('a', limit-1, "\u20actail\nnext\n"), repeatByte('a', limit-1), true, "next"},
		{"3-byte rune straddling at the second byte", repeatByte('a', limit-2, "\u20actail\nnext\n"), repeatByte('a', limit-2), true, "next"},
		{"4-byte rune straddling the limit", repeatByte('a', limit-1, "\U0001F600tail\nnext\n"), repeatByte('a', limit-1), true, "next"},
		{"4-byte rune straddling at the third byte", repeatByte('a', limit-3, "\U0001F600tail\nnext\n"), repeatByte('a', limit-3), true, "next"},
		{"rune ending exactly at the limit is kept", repeatByte('a', limit-4, "\U0001F600tail\nnext\n"), repeatByte('a', limit-4, "\U0001F600"), true, "next"},
	}
	for _, tc := range tests {
		for _, w := range readerWrappers {
			t.Run(tc.name+"/"+w.name, func(t *testing.T) {
				checkLongLine(t, w.wrap(strings.NewReader(tc.input)), tc)
			})
		}
	}
}

// repeatReader yields n bytes of 'x' without allocating.
type repeatReader struct{ remaining int64 }

func (r *repeatReader) Read(p []byte) (int, error) {
	if r.remaining == 0 {
		return 0, io.EOF
	}
	n := int64(len(p))
	if n > r.remaining {
		n = r.remaining
	}
	for i := range p[:n] {
		p[i] = 'x'
	}
	r.remaining -= n
	return int(n), nil
}

func TestLineReader_HugeLineAllocatesLittle(t *testing.T) {
	const size = 64 << 20
	lr := logparse.NewLineReader(&repeatReader{remaining: size})

	var before, after runtime.MemStats
	runtime.GC()
	runtime.ReadMemStats(&before)
	line, trunc, err := lr.Next()
	n := len(line)
	_, _, errEnd := lr.Next()
	runtime.ReadMemStats(&after)

	if err != nil || !trunc || n != logparse.MaxLineBytes {
		t.Fatalf("Next() on a 64 MiB line = %d bytes, %v, %v, want %d bytes, true, nil", n, trunc, err, logparse.MaxLineBytes)
	}
	if !errors.Is(errEnd, io.EOF) {
		t.Errorf("Next() after the 64 MiB line error = %v, want io.EOF", errEnd)
	}
	if got := after.TotalAlloc - before.TotalAlloc; got >= 1<<20 {
		t.Errorf("reading one 64 MiB line allocated %d bytes, want less than 1 MiB", got)
	}
}
