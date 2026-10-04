// Package wire implements the versioned, compressed batch format the agent sends to the backend.
package wire

import (
	"errors"
	"fmt"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// Format version written by this package.
const (
	MajorVersion = 1
	MinorVersion = 0
)

// Mode is a hint whether a batch carries current or backfilled records.
type Mode string

// Batch modes.
const (
	ModeLive     Mode = "live"
	ModeBackfill Mode = "backfill"
)

// Errors returned by the codec.
var (
	ErrUnsupportedVersion = errors.New("unsupported format version")
	ErrMalformed          = errors.New("malformed batch")
	ErrUnknownKind        = errors.New("unknown record kind")
	ErrSequence           = errors.New("sequence numbers not strictly increasing")
	ErrEmptyBatch         = errors.New("batch has no records")
	ErrLimitExceeded      = errors.New("batch limit exceeded")
	ErrRecordTooLarge     = fmt.Errorf("%w: record too large", ErrLimitExceeded)
)

// Header is the first line of a batch.
type Header struct {
	FormatMajor int    `json:"format_major"`
	FormatMinor int    `json:"format_minor"`
	AgentID     string `json:"agent_id"`
	// BootID is the boot in which every record of the batch was captured.
	BootID string `json:"boot_id"`
	// ClockOffset is the clock offset estimate valid for the capture of every record.
	ClockOffset *time.Duration `json:"clock_offset_ns,omitempty"`
	Mode        Mode           `json:"mode"`
}

// RecordSizeError reports a record whose encoded line exceeds the line limit.
type RecordSizeError struct {
	Index int // position in Batch.Records; -1 when returned by CheckRecord
	Size  int // encoded line length in bytes, without the trailing '\n'
	Limit int // DefaultLimits().MaxLineBytes
}

// Error returns "wire: records[<Index>]: encoded record is <Size> bytes, limit <Limit>".
func (e *RecordSizeError) Error() string { return "" }

// Unwrap returns ErrRecordTooLarge.
func (e *RecordSizeError) Unwrap() error { return nil }

// NewHeader returns a valid header of the current format version.
func NewHeader(agentID, bootID string, mode Mode) Header { return Header{} }

// Validate checks the header.
func (h *Header) Validate() error { return errors.New("not implemented") }

// Batch is a header with its records.
type Batch struct {
	Header  Header
	Records []model.Record
}

// Validate checks the header, the records and the batch rules.
func (b *Batch) Validate() error { return errors.New("not implemented") }

// Limits bounds what the decoder accepts.
type Limits struct {
	MaxLineBytes  int
	MaxBatchBytes int64
	MaxRecords    int
}

// DefaultLimits returns the default limits.
func DefaultLimits() Limits { return Limits{} }

// DecodeError locates a decoding error in the stream.
type DecodeError struct {
	Line int
	Err  error
}

// Error returns "wire: line <Line>: <Err>".
func (e *DecodeError) Error() string { return "" }

// Unwrap returns the wrapped error.
func (e *DecodeError) Unwrap() error { return nil }
