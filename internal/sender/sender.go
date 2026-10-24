// Package sender sends the spooled records to the backend and acknowledges them once they are accepted.
package sender

import (
	"context"
	"log/slog"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/spool"
)

// Limits and timings of the sender.
const (
	// IngestPath is the path of the ingest endpoint.
	IngestPath = "/v1/batches"
	// SendInterval is the interval between two sends.
	SendInterval = 4 * time.Second
	// RequestTimeout is the time one request may take.
	RequestTimeout = 30 * time.Second
	// FirstWait is the first wait after a failure.
	FirstWait = time.Second
	// MaxWait is the longest wait after failures.
	MaxWait = 60 * time.Second
	// MaxBatchBytes is the largest size of a batch.
	MaxBatchBytes = 1 << 20
	// MaxBatchRecords is the largest number of records of a batch.
	MaxBatchRecords = 20000
	// MaxHoles is the largest number of holes the sender keeps.
	MaxHoles = 32
	// MaxGivenUpPerHour is the largest number of batches given up in an hour.
	MaxGivenUpPerHour = 10
	// MaxAnswerBody is the largest number of bytes read from an answer's body.
	MaxAnswerBody = 4096
	// MaxAnswerHeader is the largest size of an answer's header.
	MaxAnswerHeader = 16 << 10
	// RefusedHeader with the value RefusedValue marks a refusal of the batch itself.
	RefusedHeader = "Vandox-Refused"
	// RefusedValue is the value of RefusedHeader that marks a refusal of the batch itself.
	RefusedValue = "batch"
)

// Spool is what the sender needs of the spool.
type Spool interface {
	// Read returns the records after the sequence number after.
	Read(ctx context.Context, after uint64, maxRecords int, maxBytes int64) (spool.Batch, error)
	// Ack acknowledges the sequence numbers first to last.
	Ack(ctx context.Context, first, last uint64) error
	// Put appends records.
	Put(ctx context.Context, records []model.Record) error
	// LastSeq returns the sequence number of the newest record.
	LastSeq() uint64
	// AckRanges returns the number of acknowledged ranges above the oldest kept number.
	AckRanges() int
}

// Options configures a Sender.
type Options struct {
	// Spool is the spool to send from.
	Spool Spool
	// Transport sends the batches.
	Transport Transport
	// Bandwidth is the bytes per second of backfill request bodies.
	Bandwidth int64
	// Rand returns a number in [0, 1); nil: math/rand/v2.Float64.
	Rand func() float64
}

// Sender sends the spooled records to the backend.
type Sender struct {
	opts Options
	log  *slog.Logger
}

// New returns a Sender.
func New(opts Options, log *slog.Logger) *Sender {
	return &Sender{opts: opts, log: log}
}

// Run sends until ctx is done.
func (s *Sender) Run(ctx context.Context) {
}

// SetPressure tells the sender whether the system is under memory pressure.
func (s *Sender) SetPressure(high bool) {
}

// SendLag returns the age of the oldest record not yet acknowledged.
func (s *Sender) SendLag(ctx context.Context) (time.Duration, error) {
	return 0, nil
}
