// Package logparsetest provides a scripted logparse.Parser for tests.
package logparsetest

import (
	"bytes"
	"context"
	"errors"
	"io"
	"slices"
	"sync"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/logparse"
	"github.com/LarsLaskowski/Vandox/internal/model"
)

// Parser is a scripted logparse.Parser; safe for concurrent use. Set the fields before the first call.
type Parser struct {
	TypeName   string
	DetectFunc func(f logparse.File, head []byte) logparse.Confidence                              // nil: NoMatch
	ParseFunc  func(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error // nil: reads nothing

	mu     sync.Mutex
	parsed []logparse.File
}

// Type returns TypeName.
func (p *Parser) Type() string {
	return p.TypeName
}

// Detect calls DetectFunc, or returns NoMatch when it is nil.
func (p *Parser) Detect(f logparse.File, head []byte) logparse.Confidence {
	if p.DetectFunc == nil {
		return logparse.NoMatch
	}
	return p.DetectFunc(f, head)
}

// Parse records f and calls ParseFunc, or reads nothing and returns nil when it is nil.
func (p *Parser) Parse(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error {
	p.mu.Lock()
	p.parsed = append(p.parsed, f)
	p.mu.Unlock()
	if p.ParseFunc == nil {
		return nil
	}
	return p.ParseFunc(ctx, f, r, out)
}

// Parsed returns the files Parse was called with, in call order.
func (p *Parser) Parsed() []logparse.File {
	p.mu.Lock()
	defer p.mu.Unlock()
	return slices.Clone(p.parsed)
}

// HeadPrefix returns a DetectFunc that reports c when head starts with prefix, else NoMatch.
func HeadPrefix(prefix string, c logparse.Confidence) func(f logparse.File, head []byte) logparse.Confidence {
	return func(_ logparse.File, head []byte) logparse.Confidence {
		if bytes.HasPrefix(head, []byte(prefix)) {
			return c
		}
		return logparse.NoMatch
	}
}

// Lines returns a ParseFunc that reads r with logparse.LineReader and emits per line a record of origin
// import, Source source, CapturedAt base plus the line number in seconds, and a *model.LogLine with Log
// source, Message the line and Truncated as reported; a line starting with "#" is reported with
// out.Skip(line, "comment line") instead. It returns ctx.Err() once ctx is done.
func Lines(source string, base time.Time) func(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error {
	return func(ctx context.Context, _ logparse.File, r io.Reader, out logparse.Emitter) error {
		lr := logparse.NewLineReader(r)
		for {
			if err := ctx.Err(); err != nil {
				return err
			}
			line, truncated, err := lr.Next()
			if errors.Is(err, io.EOF) {
				return nil
			}
			if err != nil {
				return err
			}
			if err := emitLine(out, source, base, lr.Line(), line, truncated); err != nil {
				return err
			}
		}
	}
}

// emitLine reports the line with the 1-based number n as a skipped comment or emits its record.
func emitLine(out logparse.Emitter, source string, base time.Time, n int64, line []byte, truncated bool) error {
	if bytes.HasPrefix(line, []byte("#")) {
		out.Skip(n, "comment line")
		return nil
	}
	return out.Record(model.Record{
		Meta: model.Meta{Origin: model.OriginImport, Source: source, CapturedAt: base.Add(time.Duration(n) * time.Second)},
		Data: &model.LogLine{Log: source, Message: string(line), Truncated: truncated},
	})
}
