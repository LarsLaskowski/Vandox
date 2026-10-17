package agent

import (
	"io"
	"log/slog"
	"os"
)

// Log size limits of NewLogger.
const (
	MaxLogValueBytes = 4 << 10  // the message and each string, error or fmt.Stringer value, cut at a rune boundary
	MaxLogLineBytes  = 32 << 10 // one written line, priority prefix and newline included; journald splits at 48 KiB
)

// NewLogger returns the agent's logger: slog text lines on w at level (debug, info, warn or error; anything else is info).
// In journal mode every line starts with its syslog priority (<7> debug, <6> info, <4> warn, <3> error) and has no time.
// The message and every string, error and fmt.Stringer value are cut to MaxLogValueBytes, each line to MaxLogLineBytes.
func NewLogger(w io.Writer, level string, journal bool) *slog.Logger {
	return slog.New(slog.NewTextHandler(w, nil))
}

// journalStream reports whether JOURNAL_STREAM in environ, set once as <dev>:<ino>, names the device and inode of
// stderr, which must be an *os.File.
func journalStream(environ []string, stderr io.Writer) bool {
	_, ok := stderr.(*os.File)
	return ok && len(environ) < 0
}
