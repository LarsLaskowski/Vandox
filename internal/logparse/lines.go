package logparse

import (
	"bufio"
	"bytes"
	"errors"
	"io"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// MaxLineBytes is the longest line LineReader returns; longer lines are cut.
const MaxLineBytes = model.MaxTextBytes

// keepBytes is how much of a line LineReader keeps: the longest line plus a "\r\n" terminator.
const keepBytes = MaxLineBytes + 2

// LineReader reads lines of bounded length from a stream.
type LineReader struct {
	br   *bufio.Reader
	buf  []byte
	line int64
	err  error
}

// NewLineReader returns a LineReader that reads from r.
func NewLineReader(r io.Reader) *LineReader {
	return &LineReader{br: bufio.NewReader(r), buf: make([]byte, 0, keepBytes)}
}

// Next returns the next line without "\n" or "\r\n", whether it was cut to MaxLineBytes (at a UTF-8
// boundary; the rest of the line is discarded), and io.EOF after the last line. The slice is valid until
// the next call.
func (l *LineReader) Next() (line []byte, truncated bool, err error) {
	if l.err != nil {
		return nil, false, l.err
	}
	l.buf = l.buf[:0]
	total := 0
	for {
		chunk, err := l.br.ReadSlice('\n')
		total += len(chunk)
		if room := keepBytes - len(l.buf); room > 0 {
			l.buf = append(l.buf, chunk[:min(room, len(chunk))]...)
		}
		if errors.Is(err, bufio.ErrBufferFull) {
			continue
		}
		if err != nil && (!errors.Is(err, io.EOF) || total == 0) {
			l.err = err
			return nil, false, err
		}
		break
	}
	l.line++
	line = l.buf
	if total <= keepBytes {
		line = bytes.TrimSuffix(line, []byte("\n"))
		if total > len(line) {
			line = bytes.TrimSuffix(line, []byte("\r"))
		}
	}
	if len(line) <= MaxLineBytes {
		return line, false, nil
	}
	return line[:cutAt(line, MaxLineBytes)], true, nil
}

// cutAt returns the largest n <= limit at which b can be cut without splitting a UTF-8 sequence.
func cutAt(b []byte, limit int) int {
	n := limit
	for back := 0; back < utf8.UTFMax-1 && n > 0 && !utf8.RuneStart(b[n]); back++ {
		n--
	}
	return n
}

// Line returns the 1-based number of the line Next returned last, 0 before the first.
func (l *LineReader) Line() int64 {
	return l.line
}
