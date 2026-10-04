// Package wire implements the versioned, compressed batch format the agent sends to the backend.
package wire

import (
	"errors"
	"fmt"
	"regexp"
	"strconv"
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
func (e *RecordSizeError) Error() string {
	where := "wire: "
	if e.Index >= 0 {
		where += "records[" + strconv.Itoa(e.Index) + "]: "
	}
	return where + "encoded record is " + strconv.Itoa(e.Size) + " bytes, limit " + strconv.Itoa(e.Limit)
}

// Unwrap returns ErrRecordTooLarge.
func (e *RecordSizeError) Unwrap() error { return ErrRecordTooLarge }

var (
	agentIDPattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]*$`)
	bootIDPattern  = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)
)

const maxAgentIDBytes = 64

// NewHeader returns a valid header of the current format version.
func NewHeader(agentID, bootID string, mode Mode) Header {
	return Header{
		FormatMajor: MajorVersion,
		FormatMinor: MinorVersion,
		AgentID:     agentID,
		BootID:      bootID,
		Mode:        mode,
	}
}

// Validate checks the header.
func (h *Header) Validate() error {
	if h.FormatMajor != MajorVersion {
		return fmt.Errorf("%w: major version %d", ErrUnsupportedVersion, h.FormatMajor)
	}
	if h.FormatMinor < 0 {
		return &model.FieldError{Field: "format_minor", Reason: "must not be negative"}
	}
	if h.AgentID == "" || len(h.AgentID) > maxAgentIDBytes || !agentIDPattern.MatchString(h.AgentID) {
		return &model.FieldError{Field: "agent_id", Reason: "must be 1 to 64 characters of [A-Za-z0-9._-], starting with a letter or digit"}
	}
	if !bootIDPattern.MatchString(h.BootID) {
		return &model.FieldError{Field: "boot_id", Reason: "must be a lower-case UUID"}
	}
	if h.Mode != ModeLive && h.Mode != ModeBackfill {
		return &model.FieldError{Field: "mode", Reason: "must be live or backfill"}
	}
	return nil
}

// Batch is a header with its records.
type Batch struct {
	Header  Header
	Records []model.Record
}

// Validate checks the header, the records and the batch rules.
func (b *Batch) Validate() error {
	if err := b.Header.Validate(); err != nil {
		return prefixField("header", err)
	}
	if len(b.Records) == 0 {
		return ErrEmptyBatch
	}
	if len(b.Records) > DefaultLimits().MaxRecords {
		return fmt.Errorf("%w: %d records, limit %d", ErrLimitExceeded, len(b.Records), DefaultLimits().MaxRecords)
	}
	var last uint64
	for i := range b.Records {
		r := &b.Records[i]
		path := "records[" + strconv.Itoa(i) + "]"
		if r.Origin != model.OriginAgent {
			return &model.FieldError{Field: path + ".origin", Reason: "must be agent"}
		}
		if err := r.Validate(); err != nil {
			return prefixField(path, err)
		}
		if r.Seq <= last {
			return fmt.Errorf("%w: %s.seq %d after %d", ErrSequence, path, r.Seq, last)
		}
		last = r.Seq
	}
	return nil
}

// prefixField prepends path to the field of a *model.FieldError; other errors are returned unchanged.
func prefixField(path string, err error) error {
	var fe *model.FieldError
	if !errors.As(err, &fe) {
		return err
	}
	return &model.FieldError{Field: path + "." + fe.Field, Reason: fe.Reason}
}

// Limits bounds what the decoder accepts. A MaxLineBytes above math.MaxInt-1 is clamped to that value.
type Limits struct {
	MaxLineBytes  int
	MaxBatchBytes int64
	MaxRecords    int
}

// DefaultLimits returns the default limits.
func DefaultLimits() Limits {
	return Limits{MaxLineBytes: 1 << 20, MaxBatchBytes: 16 << 20, MaxRecords: 20000}
}

// DecodeError locates a decoding error in the stream.
type DecodeError struct {
	Line int
	Err  error
}

// Error returns "wire: line <Line>: <Err>".
func (e *DecodeError) Error() string {
	return "wire: line " + strconv.Itoa(e.Line) + ": " + e.Err.Error()
}

// Unwrap returns the wrapped error.
func (e *DecodeError) Unwrap() error { return e.Err }
