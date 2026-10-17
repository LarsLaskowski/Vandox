package agent

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"log/slog"
	"os"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
	"unicode/utf8"
)

// Log size limits of NewLogger.
const (
	MaxLogValueBytes = 4 << 10  // the message and each string, error or fmt.Stringer value, cut at a rune boundary
	MaxLogLineBytes  = 32 << 10 // one written line, priority prefix and newline included; journald splits at 48 KiB
)

// envJournalStream is the variable systemd sets to the device and inode of the stream it connected to stderr.
const envJournalStream = "JOURNAL_STREAM"

// maxStreamDigits is the most digits of one number of JOURNAL_STREAM (a uint64 has 20).
const maxStreamDigits = 20

// NewLogger returns the agent's logger: slog text lines on w at level (debug, info, warn or error; anything else is info).
// In journal mode every line starts with its syslog priority (<7> debug, <6> info, <4> warn, <3> error) and has no time.
// The message and every string, error and fmt.Stringer value are cut to MaxLogValueBytes, each line to MaxLogLineBytes.
func NewLogger(w io.Writer, level string, journal bool) *slog.Logger {
	out := &lineOutput{w: w}
	inner := slog.NewTextHandler(&out.buf, &slog.HandlerOptions{
		Level:       parseLevel(level),
		ReplaceAttr: cutAttr,
	})
	return slog.New(&lineHandler{out: out, inner: inner, journal: journal})
}

// parseLevel maps the configuration's level names to slog levels; anything else is info.
func parseLevel(level string) slog.Level {
	switch level {
	case "debug":
		return slog.LevelDebug
	case "warn":
		return slog.LevelWarn
	case "error":
		return slog.LevelError
	}
	return slog.LevelInfo
}

// cutAttr cuts the message and every string, error and fmt.Stringer value to MaxLogValueBytes.
func cutAttr(groups []string, a slog.Attr) slog.Attr {
	if len(groups) == 0 && a.Key == slog.LevelKey {
		return a
	}
	v := a.Value.Resolve()
	switch v.Kind() {
	case slog.KindString:
		a.Value = slog.StringValue(cutRunes(v.String(), MaxLogValueBytes))
	case slog.KindAny:
		switch x := v.Any().(type) {
		case error:
			a.Value = slog.StringValue(cutRunes(x.Error(), MaxLogValueBytes))
		case fmt.Stringer:
			a.Value = slog.StringValue(cutRunes(x.String(), MaxLogValueBytes))
		}
	}
	return a
}

// cutRunes returns s cut to at most limit bytes at a rune boundary.
func cutRunes(s string, limit int) string {
	if len(s) <= limit {
		return s
	}
	n := limit
	for n > 0 && !utf8.RuneStart(s[n]) {
		n--
	}
	return s[:n]
}

// lineOutput is the writer and the formatting buffer that the handlers derived from one logger share.
type lineOutput struct {
	mu  sync.Mutex
	buf bytes.Buffer
	w   io.Writer
}

// lineHandler formats a record with the text handler into the shared buffer and writes it as one prefixed line.
type lineHandler struct {
	out     *lineOutput
	inner   slog.Handler
	journal bool
}

// Enabled reports whether the level is logged.
func (h *lineHandler) Enabled(ctx context.Context, level slog.Level) bool {
	return h.inner.Enabled(ctx, level)
}

// Handle writes the record as one line of at most MaxLogLineBytes.
func (h *lineHandler) Handle(ctx context.Context, r slog.Record) error {
	if h.journal {
		r.Time = time.Time{}
	}
	h.out.mu.Lock()
	defer h.out.mu.Unlock()

	h.out.buf.Reset()
	if err := h.inner.Handle(ctx, r); err != nil {
		return err
	}
	prefix := ""
	if h.journal {
		prefix = priorityPrefix(r.Level)
	}
	body := bytes.TrimSuffix(h.out.buf.Bytes(), []byte{'\n'})
	body = cutBytesRunes(body, MaxLogLineBytes-len(prefix)-1)

	line := make([]byte, 0, len(prefix)+len(body)+1)
	line = append(line, prefix...)
	line = append(line, body...)
	line = append(line, '\n')
	_, err := h.out.w.Write(line)
	return err
}

// WithAttrs returns a handler that adds attrs to every record.
func (h *lineHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &lineHandler{out: h.out, inner: h.inner.WithAttrs(attrs), journal: h.journal}
}

// WithGroup returns a handler that nests the attributes under name.
func (h *lineHandler) WithGroup(name string) slog.Handler {
	return &lineHandler{out: h.out, inner: h.inner.WithGroup(name), journal: h.journal}
}

// cutBytesRunes returns b cut to at most limit bytes at a rune boundary.
func cutBytesRunes(b []byte, limit int) []byte {
	if len(b) <= limit {
		return b
	}
	n := limit
	for n > 0 && !utf8.RuneStart(b[n]) {
		n--
	}
	return b[:n]
}

// priorityPrefix returns the syslog priority prefix systemd reads from the start of a line.
func priorityPrefix(level slog.Level) string {
	switch {
	case level >= slog.LevelError:
		return "<3>"
	case level >= slog.LevelWarn:
		return "<4>"
	case level >= slog.LevelInfo:
		return "<6>"
	}
	return "<7>"
}

// journalStream reports whether JOURNAL_STREAM in environ, set once as <dev>:<ino>, names the device and inode of
// stderr, which must be an *os.File.
func journalStream(environ []string, stderr io.Writer) bool {
	file, ok := stderr.(*os.File)
	if !ok {
		return false
	}
	value, ok := lookupOnce(environ, envJournalStream)
	if !ok {
		return false
	}
	devText, inoText, ok := strings.Cut(value, ":")
	if !ok {
		return false
	}
	dev, okDev := parseStreamNumber(devText)
	ino, okIno := parseStreamNumber(inoText)
	if !okDev || !okIno {
		return false
	}
	info, err := file.Stat()
	if err != nil {
		return false
	}
	st, ok := info.Sys().(*syscall.Stat_t)
	return ok && st.Dev == dev && st.Ino == ino
}

// parseStreamNumber parses 1 to 20 ASCII digits into a uint64.
func parseStreamNumber(s string) (uint64, bool) {
	if s == "" || len(s) > maxStreamDigits {
		return 0, false
	}
	for i := range len(s) {
		if s[i] < '0' || s[i] > '9' {
			return 0, false
		}
	}
	v, err := strconv.ParseUint(s, 10, 64)
	return v, err == nil
}

// lookupOnce returns the value of key in environ; false when key is missing, empty or set more than once.
func lookupOnce(environ []string, key string) (string, bool) {
	var value string
	count := 0
	for _, e := range environ {
		if v, ok := strings.CutPrefix(e, key+"="); ok {
			value = v
			count++
		}
	}
	return value, count == 1 && value != ""
}
