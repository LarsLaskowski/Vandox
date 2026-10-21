package procstats

import (
	"bytes"
	"unicode/utf8"
)

// maxTextBytes is the longest command line, command, program, comm and appended word the collector keeps; it is the limit
// the model puts on a short text.
const maxTextBytes = 1024

// maxCmdlineRead is how much of a command line is read per process.
const maxCmdlineRead = 4096

// parseCmdline turns the raw content of /proc/<pid>/cmdline into text; more tells that the file holds more than raw.
//
// Trailing NUL bytes are removed and every other NUL becomes a space. Every byte that does not start a valid UTF-8
// sequence becomes U+FFFD, and the text is cut to maxTextBytes at a character boundary, while it is built: the result
// never holds more than that. Control characters other than NUL are kept as written.
func parseCmdline(raw []byte, more bool) (text string, truncated bool) {
	raw = bytes.TrimRight(raw, "\x00")
	text, cut := cleanWith(len(raw), maxTextBytes, func(i int) (rune, int) {
		if raw[i] == 0 {
			return ' ', 1
		}
		return utf8.DecodeRune(raw[i:])
	})
	return text, more || cut
}

// CleanText makes s valid UTF-8 and cuts it to at most limit bytes at a character boundary; cut tells that text is shorter
// than s.
func CleanText(s string, limit int) (text string, cut bool) {
	return cleanText(s, limit)
}

// cleanText makes s valid UTF-8 and cuts it to at most limit bytes at a character boundary; cut tells that text is shorter
// than s. Every byte that does not start a valid sequence becomes U+FFFD; control characters are kept.
func cleanText(s string, limit int) (text string, cut bool) {
	if len(s) <= limit && utf8.ValidString(s) {
		return s, false
	}
	return cleanWith(len(s), limit, func(i int) (rune, int) { return utf8.DecodeRuneInString(s[i:]) })
}

// cleanWith builds the clean text of n bytes that decode returns the rune and size at an offset of. It stops before the
// character that would take the text beyond limit bytes.
func cleanWith(n, limit int, decode func(i int) (rune, int)) (text string, cut bool) {
	out := make([]byte, 0, min(n, limit))
	for i := 0; i < n; {
		r, size := decode(i)
		if r == utf8.RuneError && size <= 1 {
			r, size = utf8.RuneError, 1
		}
		if len(out)+utf8.RuneLen(r) > limit {
			return string(out), true
		}
		out = utf8.AppendRune(out, r)
		i += size
	}
	return string(out), false
}
