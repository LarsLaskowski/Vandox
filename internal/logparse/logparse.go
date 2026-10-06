// Package logparse defines the interface of the log parsers and the registry that picks one per file.
package logparse

import (
	"context"
	"io"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// SniffBytes is the most content Detect is given: the first SniffBytes bytes of the decompressed file.
const SniffBytes = 4096

// File describes the file a parser reads.
type File struct {
	// Name is the slash-separated path relative to the import root or inside the archive, cleaned, without a
	// leading "/", and without a trailing ".gz" when the importer decompressed the file. A label only.
	Name string
	// ModTime is the modification time of the file or archive entry in UTC; zero when unknown.
	ModTime time.Time
}

// Confidence tells how well a parser matches a file; the registry picks the highest.
type Confidence int

const (
	// NoMatch means the parser cannot read the file.
	NoMatch Confidence = iota
	// MatchName means only the name fits, or the content is not specific.
	MatchName
	// MatchContent means the content carries the format's signature.
	MatchContent
)

// Emitter receives what a parser reads from one file.
type Emitter interface {
	// Record takes the next record in file order. A non-nil error stops the import; Parse must return it.
	Record(r model.Record) error
	// Skip reports input that was not turned into a record: line is the 1-based line number, 0 when
	// unknown; reason is a fixed description that never contains input text.
	Skip(line int64, reason string)
}

// Parser reads one log format. Parse must be deterministic — the same content and File give the same records
// in the same order (an interrupted import is resumed by count) — must bound its memory independently of the
// input size, and must honor ctx.
type Parser interface {
	// Type returns the source type, unique in a registry and accepted by CheckType, e.g. "syslog".
	Type() string
	// Detect rates the file from its name and head, the first up to SniffBytes bytes of its content.
	Detect(f File, head []byte) Confidence
	// Parse reads r, the file's whole decompressed content, and passes every record to out.Record in order.
	// Records have origin import and UTC capture times.
	Parse(ctx context.Context, f File, r io.Reader, out Emitter) error
}
