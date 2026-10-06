package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

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
	if err := validateRecordQuery(q); err != nil {
		return nil, err
	}
	query, args := recordsQuery(q)
	return s.query(ctx, query, args)
}

// SearchLogs returns the log lines matching q, ordered by capture time then ID.
func (s *Store) SearchLogs(ctx context.Context, q LogSearch) ([]StoredRecord, error) {
	if err := validateWindow(q.From, q.To, q.Limit); err != nil {
		return nil, err
	}
	match, err := ftsQuery(q.Text)
	if err != nil {
		return nil, err
	}
	query := selectColumns(false, true) + " FROM log_fts JOIN log_lines l ON l.record_id = log_fts.rowid JOIN records r ON r.id = l.record_id" +
		" WHERE log_fts MATCH ? AND r.captured_at >= ? AND r.captured_at < ?"
	args := []any{match, q.From.UnixNano(), q.To.UnixNano()}
	if q.Source != "" {
		query += " AND r.source = ?"
		args = append(args, q.Source)
	}
	query += " ORDER BY r.captured_at, r.id LIMIT ?"
	args = append(args, q.Limit)
	return s.query(ctx, query, args)
}

// invalidQuery returns an error wrapping ErrInvalidQuery that names the rule broken, never the query text.
func invalidQuery(rule string) error {
	return fmt.Errorf("%w: %s", ErrInvalidQuery, rule)
}

// validateWindow checks the time range and the limit shared by both queries.
func validateWindow(from, to time.Time, limit int) error {
	switch {
	case from.IsZero() || to.IsZero():
		return invalidQuery("from and to are required")
	case !inStorableRange(from) || !inStorableRange(to):
		return invalidQuery("from and to must be within the storable range")
	case !from.Before(to):
		return invalidQuery("from must be before to")
	case limit < 1 || limit > MaxQueryLimit:
		return invalidQuery("limit must be between 1 and " + strconv.Itoa(MaxQueryLimit))
	}
	return nil
}

func validateRecordQuery(q RecordQuery) error {
	if err := validateWindow(q.From, q.To, q.Limit); err != nil {
		return err
	}
	if !knownKind(q.Kind) {
		return invalidQuery("unknown kind")
	}
	if q.Name != "" && q.Kind != model.KindMetric {
		return invalidQuery("name needs the kind metric")
	}
	if q.Name != "" && q.Source == "" {
		return invalidQuery("name needs a source")
	}
	return nil
}

func knownKind(k model.Kind) bool {
	switch k {
	case model.KindMetric, model.KindProcessSnapshot, model.KindConnectionSnapshot, model.KindServiceState,
		model.KindMariaDBStatus, model.KindKernelEvent, model.KindLogLine, model.KindGap:
		return true
	}
	return false
}

const (
	recordColumns = "SELECT r.id, r.kind, r.origin, r.source, r.agent_id, r.seq, r.captured_at, r.received_at, r.boot_id, r.clock_offset_ns, r.data, "
	metricColumns = "m.name, m.value, m.unit, m.labels, "
	logColumns    = "l.log, l.program, l.pid, l.priority, l.message, l.truncated"
	noMetric      = "NULL, NULL, NULL, NULL, "
	noLog         = "NULL, NULL, NULL, NULL, NULL, NULL"
)

// selectColumns returns the column list scanRecord reads; the columns of the metric or log line table are
// NULL when the query does not join it.
func selectColumns(metric, logLine bool) string {
	cols := recordColumns + noMetric + noLog
	if metric {
		cols = recordColumns + metricColumns + noLog
	}
	if logLine {
		cols = recordColumns + noMetric + logColumns
	}
	return cols
}

// recordsQuery builds the SQL and arguments for q, which is already validated.
func recordsQuery(q RecordQuery) (query string, args []any) {
	from, to := q.From.UnixNano(), q.To.UnixNano()
	if q.Name != "" {
		return selectColumns(true, false) + " FROM metrics m JOIN records r ON r.id = m.record_id" +
			" WHERE m.source = ? AND m.name = ? AND m.captured_at >= ? AND m.captured_at < ?" +
			" ORDER BY m.captured_at, m.record_id LIMIT ?", []any{q.Source, q.Name, from, to, q.Limit}
	}
	query = selectColumns(q.Kind == model.KindMetric, q.Kind == model.KindLogLine) + " FROM records r"
	switch q.Kind {
	case model.KindMetric:
		query += " LEFT JOIN metrics m ON m.record_id = r.id"
	case model.KindLogLine:
		query += " LEFT JOIN log_lines l ON l.record_id = r.id"
	}
	query += " WHERE r.kind = ?"
	args = []any{string(q.Kind)}
	if q.Source != "" {
		query += " AND r.source = ?"
		args = append(args, q.Source)
	}
	query += " AND r.captured_at >= ? AND r.captured_at < ? ORDER BY r.captured_at, r.id LIMIT ?"
	return query, append(args, from, to, q.Limit)
}

// query runs query on the reader pool and scans the rows.
func (s *Store) query(ctx context.Context, query string, args []any) ([]StoredRecord, error) {
	rows, err := s.read.QueryContext(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("store: reading records: %w", err)
	}
	defer func() { _ = rows.Close() }()
	out := []StoredRecord{}
	for rows.Next() {
		rec, err := scanRecord(rows)
		if err != nil {
			return nil, fmt.Errorf("store: reading records: %w", err)
		}
		out = append(out, rec)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("store: reading records: %w", err)
	}
	return out, nil
}

// scanRecord reads one row of selectColumns.
func scanRecord(rows *sql.Rows) (StoredRecord, error) {
	var (
		sr                                   StoredRecord
		kind, origin, source                 string
		agentID, bootID, data                sql.NullString
		seq, offset                          sql.NullInt64
		capturedAt, receivedAt               int64
		name, unit, labels, logName, program sql.NullString
		message                              sql.NullString
		value                                sql.NullFloat64
		pid, priority, truncated             sql.NullInt64
	)
	if err := rows.Scan(&sr.ID, &kind, &origin, &source, &agentID, &seq, &capturedAt, &receivedAt, &bootID, &offset, &data,
		&name, &value, &unit, &labels, &logName, &program, &pid, &priority, &message, &truncated); err != nil {
		return StoredRecord{}, err
	}
	payload, err := decodePayload(model.Kind(kind), data, metricRow{name, value, unit, labels},
		logRow{logName, program, pid, priority, message, truncated})
	if err != nil {
		return StoredRecord{}, err
	}
	sr.Record = model.Record{
		Meta: model.Meta{Origin: model.Origin(origin), Source: source, Seq: uint64(seq.Int64), CapturedAt: time.Unix(0, capturedAt).UTC()},
		Data: payload,
	}
	sr.AgentID = agentID.String
	sr.BootID = bootID.String
	sr.ReceivedAt = time.Unix(0, receivedAt).UTC()
	if offset.Valid {
		d := time.Duration(offset.Int64)
		sr.ClockOffset = &d
	}
	return sr, nil
}

type metricRow struct {
	name   sql.NullString
	value  sql.NullFloat64
	unit   sql.NullString
	labels sql.NullString
}

type logRow struct {
	log, program  sql.NullString
	pid, priority sql.NullInt64
	message       sql.NullString
	truncated     sql.NullInt64
}

// decodePayload builds the payload of kind from the typed rows or the JSON column.
func decodePayload(kind model.Kind, data sql.NullString, m metricRow, l logRow) (model.Payload, error) {
	switch kind {
	case model.KindMetric:
		p := &model.MetricPoint{Name: m.name.String, Value: m.value.Float64, Unit: m.unit.String}
		if m.labels.Valid {
			if err := json.Unmarshal([]byte(m.labels.String), &p.Labels); err != nil {
				return nil, fmt.Errorf("decoding metric labels: %w", err)
			}
		}
		return p, nil
	case model.KindLogLine:
		p := &model.LogLine{Log: l.log.String, Program: l.program.String, PID: int32(l.pid.Int64),
			Message: l.message.String, Truncated: l.truncated.Int64 != 0}
		if l.priority.Valid {
			v := uint8(l.priority.Int64)
			p.Priority = &v
		}
		return p, nil
	}
	var p model.Payload
	switch kind {
	case model.KindProcessSnapshot:
		p = &model.ProcessSnapshot{}
	case model.KindConnectionSnapshot:
		p = &model.ConnectionSnapshot{}
	case model.KindServiceState:
		p = &model.ServiceState{}
	case model.KindMariaDBStatus:
		p = &model.MariaDBStatus{}
	case model.KindKernelEvent:
		p = &model.KernelEvent{}
	case model.KindGap:
		p = &model.Gap{}
	default:
		return nil, fmt.Errorf("unknown record kind %q", kind)
	}
	if err := json.Unmarshal([]byte(data.String), p); err != nil {
		return nil, fmt.Errorf("decoding %s payload: %w", kind, err)
	}
	return p, nil
}

// ftsQuery turns a search text into an FTS5 expression of quoted literal terms; its errors wrap
// ErrInvalidQuery.
func ftsQuery(text string) (string, error) {
	if !utf8.ValidString(text) {
		return "", invalidQuery("search text is not valid UTF-8")
	}
	if len(text) > MaxSearchBytes {
		return "", invalidQuery("search text is longer than " + strconv.Itoa(MaxSearchBytes) + " bytes")
	}
	for _, r := range text {
		if !unicode.IsSpace(r) && (unicode.Is(unicode.Cc, r) || unicode.Is(unicode.Cf, r)) {
			return "", invalidQuery("search text contains a control or format character")
		}
	}
	terms := strings.Fields(text)
	if len(terms) == 0 {
		return "", invalidQuery("search text has no terms")
	}
	if len(terms) > MaxSearchTerms {
		return "", invalidQuery("search text has more than " + strconv.Itoa(MaxSearchTerms) + " terms")
	}
	quoted := make([]string, len(terms))
	for i, term := range terms {
		if !strings.ContainsFunc(term, func(r rune) bool { return unicode.IsLetter(r) || unicode.IsDigit(r) }) {
			return "", invalidQuery("every search term needs a letter or a digit")
		}
		quoted[i] = `"` + strings.ReplaceAll(term, `"`, `""`) + `"`
	}
	return strings.Join(quoted, " "), nil
}
