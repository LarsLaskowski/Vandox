package store

import "context"

// Writer stores batches of records.
type Writer interface {
	WriteBatch(ctx context.Context, b Batch) (WriteResult, error)
}

// RecordReader reads stored records by kind, source, name and time range.
type RecordReader interface {
	Records(ctx context.Context, q RecordQuery) ([]StoredRecord, error)
}

// LogSearcher searches stored log lines.
type LogSearcher interface {
	SearchLogs(ctx context.Context, q LogSearch) ([]StoredRecord, error)
}

var (
	_ Writer       = (*Store)(nil)
	_ RecordReader = (*Store)(nil)
	_ LogSearcher  = (*Store)(nil)
)
