// Package storetest provides a scripted fake of the store's repository interfaces.
package storetest

import (
	"context"
	"errors"
	"sync"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store"
)

// Fake is a scripted stand-in for store.Writer, store.RecordReader and store.LogSearcher. It is safe for
// concurrent use; set the hook and Block fields before the first call.
type Fake struct {
	OnWrite   func(b store.Batch) (store.WriteResult, error)          // nil: every record stored
	OnRecords func(q store.RecordQuery) ([]store.StoredRecord, error) // nil: no records
	OnSearch  func(q store.LogSearch) ([]store.StoredRecord, error)   // nil: no hits
	Block     chan struct{}                                           // non-nil: every call waits until closed or ctx is done

	mu       sync.Mutex
	batches  []store.Batch
	queries  []store.RecordQuery
	searches []store.LogSearch
}

// WriteBatch records b and returns the scripted result.
func (f *Fake) WriteBatch(ctx context.Context, b store.Batch) (store.WriteResult, error) {
	return store.WriteResult{}, errors.New("not implemented")
}

// Records records q and returns the scripted result.
func (f *Fake) Records(ctx context.Context, q store.RecordQuery) ([]store.StoredRecord, error) {
	return nil, errors.New("not implemented")
}

// SearchLogs records q and returns the scripted result.
func (f *Fake) SearchLogs(ctx context.Context, q store.LogSearch) ([]store.StoredRecord, error) {
	return nil, errors.New("not implemented")
}

// Batches returns copies of the batches written so far, in call order.
func (f *Fake) Batches() []store.Batch {
	return nil
}

// RecordQueries returns the record queries made so far, in call order.
func (f *Fake) RecordQueries() []store.RecordQuery {
	return nil
}

// LogSearches returns the log searches made so far, in call order.
func (f *Fake) LogSearches() []store.LogSearch {
	return nil
}

var (
	_ store.Writer       = (*Fake)(nil)
	_ store.RecordReader = (*Fake)(nil)
	_ store.LogSearcher  = (*Fake)(nil)
)
