package logparse

import (
	"errors"
	"io"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// MaxLineBytes is the longest line LineReader returns; longer lines are cut.
const MaxLineBytes = model.MaxTextBytes

// LineReader reads lines of bounded length from a stream.
type LineReader struct {
	r    io.Reader
	line int64
}

// NewLineReader returns a LineReader that reads from r.
func NewLineReader(r io.Reader) *LineReader {
	return &LineReader{r: r}
}

// Next returns the next line without "\n" or "\r\n", whether it was cut to MaxLineBytes (at a UTF-8
// boundary; the rest of the line is discarded), and io.EOF after the last line. The slice is valid until
// the next call.
func (l *LineReader) Next() (line []byte, truncated bool, err error) {
	return nil, false, errors.New("not implemented")
}

// Line returns the 1-based number of the line Next returned last, 0 before the first.
func (l *LineReader) Line() int64 {
	return 0
}
