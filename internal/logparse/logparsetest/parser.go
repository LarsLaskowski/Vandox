// Package logparsetest provides a scripted logparse.Parser for tests.
package logparsetest

import (
	"context"
	"errors"
	"io"
	"sync"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/logparse"
)

// Parser is a scripted logparse.Parser; safe for concurrent use. Set the fields before the first call.
type Parser struct {
	TypeName   string
	DetectFunc func(f logparse.File, head []byte) logparse.Confidence                               // nil: NoMatch
	ParseFunc  func(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error // nil: reads nothing

	mu     sync.Mutex
	parsed []logparse.File
}

// Type returns TypeName.
func (p *Parser) Type() string {
	return ""
}

// Detect calls DetectFunc, or returns NoMatch when it is nil.
func (p *Parser) Detect(f logparse.File, head []byte) logparse.Confidence {
	return logparse.NoMatch
}

// Parse records f and calls ParseFunc, or reads nothing and returns nil when it is nil.
func (p *Parser) Parse(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error {
	return errors.New("not implemented")
}

// Parsed returns the files Parse was called with, in call order.
func (p *Parser) Parsed() []logparse.File {
	return nil
}

// HeadPrefix returns a DetectFunc that reports c when head starts with prefix, else NoMatch.
func HeadPrefix(prefix string, c logparse.Confidence) func(f logparse.File, head []byte) logparse.Confidence {
	return nil
}

// Lines returns a ParseFunc that reads r with logparse.LineReader and emits per line a record of origin
// import, Source source, CapturedAt base plus the line number in seconds, and a *model.LogLine with Log
// source, Message the line and Truncated as reported; a line starting with "#" is reported with
// out.Skip(line, "comment line") instead. It returns ctx.Err() once ctx is done.
func Lines(source string, base time.Time) func(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error {
	return nil
}
