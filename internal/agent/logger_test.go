package agent

import (
	"bytes"
	"context"
	"errors"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"testing"
	"unicode/utf8"
)

var modes = []struct {
	name    string
	journal bool
}{
	{"journal", true},
	{"plain", false},
}

// render logs through a new logger and returns everything it wrote.
func render(level string, journal bool, log func(*slog.Logger)) string {
	var buf bytes.Buffer
	log(NewLogger(&buf, level, journal))
	return buf.String()
}

// longestRun returns the length of the longest run of c in s.
func longestRun(s string, c byte) int {
	longest, run := 0, 0
	for i := 0; i < len(s); i++ {
		if s[i] == c {
			run++
			longest = max(longest, run)
		} else {
			run = 0
		}
	}
	return longest
}

func logAll(l *slog.Logger) {
	l.Debug("mdebug")
	l.Info("minfo")
	l.Warn("mwarn")
	l.Error("merror")
}

func TestNewLogger(t *testing.T) {
	t.Run("levels", testLoggerLevels)
	t.Run("journal priorities", testLoggerPriorities)
	t.Run("plain lines", testLoggerPlainLines)
	t.Run("line breaks are escaped", testLoggerEscapes)
	t.Run("values are cut", testLoggerValueCap)
	t.Run("cut at a rune boundary", testLoggerRuneBoundary)
	t.Run("line is cut", testLoggerLineCap)
}

// writtenMessages returns the messages of logAll that out holds, comma separated.
func writtenMessages(out string) string {
	var got []string
	for _, name := range []string{"mdebug", "minfo", "mwarn", "merror"} {
		if strings.Contains(out, "msg="+name) {
			got = append(got, name)
		}
	}
	return strings.Join(got, ",")
}

func testLoggerLevels(t *testing.T) {
	tests := []struct {
		level string
		want  string
	}{
		{"debug", "mdebug,minfo,mwarn,merror"},
		{"info", "minfo,mwarn,merror"},
		{"warn", "mwarn,merror"},
		{"error", "merror"},
		{"unknown", "minfo,mwarn,merror"},
		{"", "minfo,mwarn,merror"},
	}
	for _, tc := range tests {
		for _, m := range modes {
			t.Run(m.name+" "+tc.level, func(t *testing.T) {
				out := render(tc.level, m.journal, logAll)

				if got := writtenMessages(out); got != tc.want {
					t.Errorf("NewLogger(level %q, journal %v) wrote %q, want %q; output %q", tc.level, m.journal, got, tc.want, out)
				}
			})
		}
	}
}

func testLoggerPriorities(t *testing.T) {
	out := render("debug", true, logAll)

	lines := strings.Split(strings.TrimSuffix(out, "\n"), "\n")
	wantPrefix := []string{"<7>", "<6>", "<4>", "<3>"}
	if len(lines) != len(wantPrefix) {
		t.Fatalf("journal mode wrote %d lines, want %d: %q", len(lines), len(wantPrefix), out)
	}
	for i, line := range lines {
		if !strings.HasPrefix(line, wantPrefix[i]) {
			t.Errorf("journal line %d = %q, want prefix %q", i, line, wantPrefix[i])
		}
		if strings.Contains(line, "time=") {
			t.Errorf("journal line %d = %q, want no time attribute", i, line)
		}
	}
}

func testLoggerPlainLines(t *testing.T) {
	out := render("debug", false, logAll)

	lines := strings.Split(strings.TrimSuffix(out, "\n"), "\n")
	if len(lines) != 4 {
		t.Fatalf("plain mode wrote %d lines, want 4: %q", len(lines), out)
	}
	for i, line := range lines {
		if strings.HasPrefix(line, "<") {
			t.Errorf("plain line %d = %q, want no priority prefix", i, line)
		}
		if !strings.Contains(line, "time=") {
			t.Errorf("plain line %d = %q, want a time attribute", i, line)
		}
	}
}

func testLoggerEscapes(t *testing.T) {
	for _, m := range modes {
		t.Run(m.name, func(t *testing.T) {
			out := render("info", m.journal, func(l *slog.Logger) {
				l.Info("first\nsecond\rthird", "key", "x\ny\rz", "err", errors.New("e\nf\rg"))
			})

			if got := strings.Count(out, "\n"); got != 1 || !strings.HasSuffix(out, "\n") {
				t.Errorf("output %q has %d line feeds, want exactly one at the end", out, got)
			}
			if strings.Contains(out, "\r") {
				t.Errorf("output %q holds a raw carriage return, want it escaped", out)
			}
			for _, escaped := range []string{`first\nsecond\rthird`, `x\ny\rz`, `e\nf\rg`} {
				if !strings.Contains(out, escaped) {
					t.Errorf("output %q does not hold the escaped text %q", out, escaped)
				}
			}
		})
	}
}

func testLoggerValueCap(t *testing.T) {
	long := strings.Repeat("a", 5000)
	tests := []struct {
		name string
		log  func(*slog.Logger)
	}{
		{"message", func(l *slog.Logger) { l.Info(long) }},
		{"string attribute", func(l *slog.Logger) { l.Info("m", "key", long) }},
		{"error attribute", func(l *slog.Logger) { l.Info("m", "err", errors.New(long)) }},
	}
	for _, tc := range tests {
		for _, m := range modes {
			t.Run(tc.name+" "+m.name, func(t *testing.T) {
				out := render("info", m.journal, tc.log)

				if got := longestRun(out, 'a'); got != MaxLogValueBytes {
					t.Errorf("longest run of the 5000 byte %s = %d bytes, want exactly %d", tc.name, got, MaxLogValueBytes)
				}
			})
		}
	}
}

func testLoggerRuneBoundary(t *testing.T) {
	value := strings.Repeat("a", MaxLogValueBytes-1) + "\u00e4"
	tests := []struct {
		name string
		log  func(*slog.Logger)
	}{
		{"message", func(l *slog.Logger) { l.Info(value) }},
		{"string attribute", func(l *slog.Logger) { l.Info("m", "key", value) }},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			out := render("info", false, tc.log)

			if got := longestRun(out, 'a'); got != MaxLogValueBytes-1 {
				t.Errorf("longest run of a = %d, want %d", got, MaxLogValueBytes-1)
			}
			if !utf8.ValidString(out) || strings.Contains(out, "\u00e4") || strings.Contains(out, `\xc3`) {
				t.Errorf("output holds part of the cut rune or invalid UTF-8; output ends %q", out[max(0, len(out)-40):])
			}
		})
	}
}

func testLoggerLineCap(t *testing.T) {
	raw := strings.Repeat("\x01", 4096)
	for _, m := range modes {
		t.Run(m.name, func(t *testing.T) {
			out := render("info", m.journal, func(l *slog.Logger) {
				args := make([]any, 0, 20)
				for i := range 10 {
					args = append(args, "k"+strconv.Itoa(i), raw)
				}
				l.Log(context.Background(), slog.LevelInfo, "many", args...)
			})

			if len(out) > MaxLogLineBytes {
				t.Errorf("line has %d bytes, want at most %d", len(out), MaxLogLineBytes)
			}
			if !strings.HasSuffix(out, "\n") || strings.Count(out, "\n") != 1 {
				t.Errorf("output ends %q with %d line feeds, want exactly one, at the end",
					out[max(0, len(out)-20):], strings.Count(out, "\n"))
			}
			if !utf8.ValidString(out) {
				t.Errorf("output is not valid UTF-8")
			}
		})
	}
}

func TestJournalStream(t *testing.T) {
	file, err := os.Create(filepath.Join(t.TempDir(), "stderr"))
	if err != nil {
		t.Fatalf("creating the stderr file: %v", err)
	}
	t.Cleanup(func() { _ = file.Close() })
	info, err := file.Stat()
	if err != nil {
		t.Fatalf("Stat: %v", err)
	}
	st, ok := info.Sys().(*syscall.Stat_t)
	if !ok {
		t.Fatalf("Stat().Sys() = %T, want *syscall.Stat_t", info.Sys())
	}
	dev := strconv.FormatUint(st.Dev, 10)
	ino := strconv.FormatUint(st.Ino, 10)
	match := "JOURNAL_STREAM=" + dev + ":" + ino

	tests := []struct {
		name    string
		environ []string
		stderr  io.Writer
		want    bool
	}{
		{"matching device and inode", []string{match}, file, true},
		{"other variables around it", []string{"A=1", match, "B=2"}, file, true},
		{"other inode", []string{"JOURNAL_STREAM=" + dev + ":" + strconv.FormatUint(st.Ino+1, 10)}, file, false},
		{"unset", nil, file, false},
		{"empty", []string{"JOURNAL_STREAM="}, file, false},
		{"stderr is a buffer", []string{match}, &bytes.Buffer{}, false},
		{"one number", []string{"JOURNAL_STREAM=8"}, file, false},
		{"missing inode", []string{"JOURNAL_STREAM=8:"}, file, false},
		{"missing device", []string{"JOURNAL_STREAM=:8"}, file, false},
		{"three parts", []string{"JOURNAL_STREAM=8:1:2"}, file, false},
		{"letters", []string{"JOURNAL_STREAM=a:1"}, file, false},
		{"negative device", []string{"JOURNAL_STREAM=-1:1"}, file, false},
		{"plus sign", []string{"JOURNAL_STREAM=+1:1"}, file, false},
		{"leading space", []string{"JOURNAL_STREAM= 8:1"}, file, false},
		{"trailing space", []string{"JOURNAL_STREAM=8:1 "}, file, false},
		{"21 digit device", []string{"JOURNAL_STREAM=" + strings.Repeat("1", 21) + ":1"}, file, false},
		{"device above uint64", []string{"JOURNAL_STREAM=18446744073709551616:1"}, file, false},
		{"leading space on the real value", []string{"JOURNAL_STREAM= " + dev + ":" + ino}, file, false},
		{"trailing space on the real value", []string{match + " "}, file, false},
		{"plus sign on the real value", []string{"JOURNAL_STREAM=+" + dev + ":" + ino}, file, false},
		{"third part on the real value", []string{match + ":2"}, file, false},
		{"matching value set twice", []string{match, match}, file, false},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := journalStream(tc.environ, tc.stderr); got != tc.want {
				t.Errorf("journalStream(%q, %T) = %v, want %v", tc.environ, tc.stderr, got, tc.want)
			}
		})
	}
}
