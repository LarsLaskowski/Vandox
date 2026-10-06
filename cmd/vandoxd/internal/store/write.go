package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
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
	Import      *ImportStep // optional: every record has origin import, AgentID is ""; the step is applied in the same transaction
}

// WriteResult counts the records of a batch that were stored and those already present.
type WriteResult struct {
	Stored     int
	Duplicates int
}

const (
	insertRecord = "INSERT INTO records(kind, origin, source, agent_id, seq, captured_at, received_at, boot_id, clock_offset_ns, data) " +
		"VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?) ON CONFLICT DO NOTHING"
	insertMetric  = "INSERT INTO metrics(record_id, source, name, captured_at, value, unit, labels) VALUES (?, ?, ?, ?, ?, ?, ?)"
	insertLogLine = "INSERT INTO log_lines(record_id, log, program, pid, priority, message, truncated) VALUES (?, ?, ?, ?, ?, ?, ?)"
	insertLogFTS  = "INSERT INTO log_fts(rowid, message) VALUES (?, ?)"
)

var (
	// storableMin and storableMax bound the instants stored as nanoseconds since the Unix epoch.
	storableMin = time.Unix(0, math.MinInt64)
	storableMax = time.Unix(0, math.MaxInt64)
)

// inStorableRange reports whether t can be stored as an int64 count of nanoseconds.
func inStorableRange(t time.Time) bool {
	return !t.Before(storableMin) && !t.After(storableMax)
}

// invalidBatch returns an error wrapping ErrInvalidBatch that names the rule broken, never a payload value.
func invalidBatch(format string, args ...any) error {
	return fmt.Errorf("%w: "+format, append([]any{ErrInvalidBatch}, args...)...)
}

// validateBatch checks b completely before anything is written.
func validateBatch(b Batch) error {
	if len(b.Records) == 0 && (b.Import == nil || !b.Import.Complete) {
		return invalidBatch("no records")
	}
	if len(b.Records) > MaxBatchRecords {
		return invalidBatch("more than %d records", MaxBatchRecords)
	}
	if b.ReceivedAt.IsZero() {
		return invalidBatch("received_at is required")
	}
	if _, offset := b.ReceivedAt.Zone(); offset != 0 {
		return invalidBatch("received_at must be UTC")
	}
	if !inStorableRange(b.ReceivedAt) {
		return invalidBatch("received_at is outside the storable range")
	}
	if b.AgentID != "" {
		if err := wire.ValidateAgentID(b.AgentID); err != nil {
			return fmt.Errorf("%w: %w", ErrInvalidBatch, err)
		}
	}
	if b.Import != nil {
		return validateImportBatch(b)
	}
	for i := range b.Records {
		if err := validateRecord(&b.Records[i], b.AgentID != ""); err != nil {
			return fmt.Errorf("%w: records[%d]: %w", ErrInvalidBatch, i, err)
		}
	}
	return nil
}

// validateImportBatch checks the rules of a batch with Import: no agent, a valid step, import records only.
func validateImportBatch(b Batch) error {
	if b.AgentID != "" {
		return invalidBatch("an import batch has no agent ID")
	}
	if b.Import.FileID <= 0 {
		return invalidBatch("import file ID must be positive")
	}
	if b.Import.Done < 0 {
		return invalidBatch("import done must not be negative")
	}
	for i := range b.Records {
		if err := CheckImportRecord(&b.Records[i]); err != nil {
			return fmt.Errorf("%w: records[%d]: %w", ErrInvalidBatch, i, err)
		}
	}
	return nil
}

// validateRecord checks r with the model rules and the limits of the database; hasAgent tells whether the
// batch names an agent.
func validateRecord(r *model.Record, hasAgent bool) error {
	if err := r.Validate(); err != nil {
		return err
	}
	if !inStorableRange(r.CapturedAt) {
		return errors.New("captured_at is outside the storable range")
	}
	if r.Seq > math.MaxInt64 {
		return errors.New("seq is above the storable range")
	}
	if r.Origin == model.OriginAgent && !hasAgent {
		return errors.New("origin agent needs an agent ID in the batch")
	}
	return nil
}

// WriteBatch validates b and stores its records in one transaction; records already present are counted as
// duplicates and never overwritten.
func (s *Store) WriteBatch(ctx context.Context, b Batch) (WriteResult, error) {
	if err := validateBatch(b); err != nil {
		return WriteResult{}, err
	}
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return WriteResult{}, fmt.Errorf("store: writing batch: %w", err)
	}
	defer func() { _ = tx.Rollback() }()
	w, err := newBatchWriter(ctx, tx)
	if err != nil {
		return WriteResult{}, fmt.Errorf("store: writing batch: %w", err)
	}
	defer w.close()
	if b.Import != nil {
		if err := advanceImport(ctx, tx, *b.Import, len(b.Records), b.ReceivedAt); err != nil {
			return WriteResult{}, fmt.Errorf("store: writing batch: %w", err)
		}
	}
	var res WriteResult
	for i := range b.Records {
		stored, err := w.write(ctx, &b, &b.Records[i])
		if err != nil {
			return WriteResult{}, fmt.Errorf("store: writing batch: %w", err)
		}
		if stored {
			res.Stored++
		} else {
			res.Duplicates++
		}
	}
	if err := tx.Commit(); err != nil {
		return WriteResult{}, fmt.Errorf("store: writing batch: %w", err)
	}
	return res, nil
}

// batchWriter holds the prepared statements of one write transaction.
type batchWriter struct {
	record, metric, logLine, logFTS *sql.Stmt
}

func newBatchWriter(ctx context.Context, tx *sql.Tx) (*batchWriter, error) {
	w := &batchWriter{}
	for _, p := range []struct {
		dst   **sql.Stmt
		query string
	}{
		{&w.record, insertRecord}, {&w.metric, insertMetric}, {&w.logLine, insertLogLine}, {&w.logFTS, insertLogFTS},
	} {
		stmt, err := tx.PrepareContext(ctx, p.query)
		if err != nil {
			w.close()
			return nil, err
		}
		*p.dst = stmt
	}
	return w, nil
}

func (w *batchWriter) close() {
	for _, stmt := range []*sql.Stmt{w.record, w.metric, w.logLine, w.logFTS} {
		if stmt != nil {
			_ = stmt.Close()
		}
	}
}

// nullString returns s, or nil (NULL) when s is empty.
func nullString(s string) any {
	if s == "" {
		return nil
	}
	return s
}

// write inserts r and its payload rows. It reports false, writing nothing more, when the record is a
// duplicate of a stored one.
func (w *batchWriter) write(ctx context.Context, b *Batch, r *model.Record) (stored bool, err error) {
	var seq, offset any
	if r.Origin == model.OriginAgent {
		seq = int64(r.Seq)
	}
	if b.ClockOffset != nil {
		offset = int64(*b.ClockOffset)
	}
	var data any
	switch r.Data.(type) {
	case *model.MetricPoint, *model.LogLine:
	default:
		raw, err := json.Marshal(r.Data)
		if err != nil {
			return false, fmt.Errorf("encoding %s payload: %w", r.Kind(), err)
		}
		data = string(raw)
	}
	res, err := w.record.ExecContext(ctx, string(r.Kind()), string(r.Origin), r.Source, nullString(b.AgentID), seq,
		r.CapturedAt.UnixNano(), b.ReceivedAt.UnixNano(), nullString(b.BootID), offset, data)
	if err != nil {
		return false, err
	}
	n, err := res.RowsAffected()
	if err != nil {
		return false, err
	}
	if n == 0 {
		return false, nil
	}
	id, err := res.LastInsertId()
	if err != nil {
		return false, err
	}
	switch p := r.Data.(type) {
	case *model.MetricPoint:
		err = w.writeMetric(ctx, id, r, p)
	case *model.LogLine:
		err = w.writeLogLine(ctx, id, p)
	}
	return err == nil, err
}

func (w *batchWriter) writeMetric(ctx context.Context, id int64, r *model.Record, p *model.MetricPoint) error {
	var labels any
	if len(p.Labels) > 0 {
		raw, err := json.Marshal(p.Labels)
		if err != nil {
			return fmt.Errorf("encoding metric labels: %w", err)
		}
		labels = string(raw)
	}
	_, err := w.metric.ExecContext(ctx, id, r.Source, p.Name, r.CapturedAt.UnixNano(), p.Value, p.Unit, labels)
	return err
}

// writeLogLine inserts the log line and its full-text index entry; WriteBatch is the only code that does,
// which keeps log_fts in step with log_lines.
func (w *batchWriter) writeLogLine(ctx context.Context, id int64, p *model.LogLine) error {
	var priority any
	if p.Priority != nil {
		priority = int64(*p.Priority)
	}
	if _, err := w.logLine.ExecContext(ctx, id, p.Log, p.Program, int64(p.PID), priority, p.Message, p.Truncated); err != nil {
		return err
	}
	_, err := w.logFTS.ExecContext(ctx, id, p.Message)
	return err
}
