package store

import (
	"context"
	"errors"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// MaxBatchRecords is the largest number of records WriteBatch accepts in one batch.
const MaxBatchRecords = 20000

// ErrInvalidBatch is wrapped by the error WriteBatch returns for a batch it rejects before writing.
var ErrInvalidBatch = errors.New("store: invalid batch")

// Batch is a set of records written in one transaction, with the context shared by all of them.
type Batch struct {
	AgentID     string         // required when a record has origin agent; "" otherwise allowed
	BootID      string         // optional; stored as NULL when ""
	ClockOffset *time.Duration // optional
	ReceivedAt  time.Time      // required, UTC
	Records     []model.Record
}

// WriteResult counts the records of a batch that were stored and those already present.
type WriteResult struct {
	Stored     int
	Duplicates int
}

// WriteBatch validates b and stores its records in one transaction; records already present are counted as
// duplicates and never overwritten.
func (s *Store) WriteBatch(ctx context.Context, b Batch) (WriteResult, error) {
	return WriteResult{}, errors.New("not implemented")
}
