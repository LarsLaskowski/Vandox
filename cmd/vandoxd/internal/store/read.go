package store

import (
	"context"
	"errors"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	// MaxQueryLimit is the largest Limit of a RecordQuery or LogSearch.
	MaxQueryLimit = 10000
	// MaxSearchTerms is the largest number of terms in a log search text.
	MaxSearchTerms = 16
	// MaxSearchBytes is the largest size of a log search text.
	MaxSearchBytes = model.MaxShortTextBytes
)

// ErrInvalidQuery is wrapped by the error Records and SearchLogs return for a query they reject.
var ErrInvalidQuery = errors.New("store: invalid query")

// RecordQuery selects records of one kind in the half-open time range [From, To).
type RecordQuery struct {
	Kind   model.Kind // required
	Source string     // optional
	Name   string     // optional, metric name; only with Kind metric and a Source
	From   time.Time  // required
	To     time.Time  // required, after From
	Limit  int        // 1 to MaxQueryLimit
}

// LogSearch selects log lines that contain every term of Text in the half-open time range [From, To).
type LogSearch struct {
	Text   string    // literal terms, see ftsQuery
	Source string    // optional
	From   time.Time // required
	To     time.Time // required, after From
	Limit  int       // 1 to MaxQueryLimit
}

// StoredRecord is a record as stored, with its storage context.
type StoredRecord struct {
	ID          int64
	Record      model.Record
	AgentID     string
	BootID      string
	ClockOffset *time.Duration
	ReceivedAt  time.Time
}

// Records returns the records matching q, ordered by capture time then ID.
func (s *Store) Records(ctx context.Context, q RecordQuery) ([]StoredRecord, error) {
	return nil, errors.New("not implemented")
}

// SearchLogs returns the log lines matching q, ordered by capture time then ID.
func (s *Store) SearchLogs(ctx context.Context, q LogSearch) ([]StoredRecord, error) {
	return nil, errors.New("not implemented")
}

// recordsQuery builds the SQL and arguments for q, which is already validated.
func recordsQuery(q RecordQuery) (query string, args []any) {
	return "", nil
}

// ftsQuery turns a search text into an FTS5 expression of quoted literal terms; its errors wrap
// ErrInvalidQuery.
func ftsQuery(text string) (string, error) {
	return "", errors.New("not implemented")
}
