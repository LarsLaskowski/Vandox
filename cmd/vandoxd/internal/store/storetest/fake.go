// Package storetest provides a scripted fake of the store's repository interfaces.
package storetest

import (
	"context"
	"slices"
	"sync"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store"
)

// Fake is a scripted stand-in for store.Writer, store.RecordReader, store.LogSearcher and store.ImportTracker. It is safe for
// concurrent use; set the hook and Block fields before the first call.
type Fake struct {
	OnWrite   func(b store.Batch) (store.WriteResult, error)          // nil: every record stored
	OnRecords func(q store.RecordQuery) ([]store.StoredRecord, error) // nil: no records
	OnSearch  func(q store.LogSearch) ([]store.StoredRecord, error)   // nil: no hits
	// OnBeginImport scripts BeginImport; nil: a new, incomplete file with IDs 1, 2, …
	OnBeginImport func(f store.ImportFileStart) (store.ImportFile, error)
	Block         chan struct{} // non-nil: every call waits until closed or ctx is done

	mu       sync.Mutex
	batches  []store.Batch
	queries  []store.RecordQuery
	searches []store.LogSearch
	starts   []store.ImportFileStart
}

// WriteBatch records b and returns the scripted result.
func (f *Fake) WriteBatch(ctx context.Context, b store.Batch) (store.WriteResult, error) {
	f.mu.Lock()
	f.batches = append(f.batches, cloneBatch(b))
	f.mu.Unlock()
	if err := f.wait(ctx); err != nil {
		return store.WriteResult{}, err
	}
	if f.OnWrite == nil {
		return store.WriteResult{Stored: len(b.Records)}, nil
	}
	return f.OnWrite(b)
}

// BeginImport records s and returns the scripted import state.
func (f *Fake) BeginImport(ctx context.Context, s store.ImportFileStart) (store.ImportFile, error) {
	f.mu.Lock()
	f.starts = append(f.starts, s)
	id := int64(len(f.starts))
	f.mu.Unlock()
	if err := f.wait(ctx); err != nil {
		return store.ImportFile{}, err
	}
	if f.OnBeginImport != nil {
		return f.OnBeginImport(s)
	}
	return store.ImportFile{
		ID: id, SHA256: s.SHA256, Size: s.Size, Name: s.Name, FileName: s.FileName, ModTime: s.ModTime,
		SourceType: s.SourceType, StartedAt: s.StartedAt,
	}, nil
}

// ImportStarts returns the BeginImport arguments so far, in call order.
func (f *Fake) ImportStarts() []store.ImportFileStart {
	f.mu.Lock()
	defer f.mu.Unlock()
	return slices.Clone(f.starts)
}

// Records records q and returns the scripted result.
func (f *Fake) Records(ctx context.Context, q store.RecordQuery) ([]store.StoredRecord, error) {
	f.mu.Lock()
	f.queries = append(f.queries, q)
	f.mu.Unlock()
	if err := f.wait(ctx); err != nil {
		return nil, err
	}
	if f.OnRecords == nil {
		return nil, nil
	}
	return f.OnRecords(q)
}

// SearchLogs records q and returns the scripted result.
func (f *Fake) SearchLogs(ctx context.Context, q store.LogSearch) ([]store.StoredRecord, error) {
	f.mu.Lock()
	f.searches = append(f.searches, q)
	f.mu.Unlock()
	if err := f.wait(ctx); err != nil {
		return nil, err
	}
	if f.OnSearch == nil {
		return nil, nil
	}
	return f.OnSearch(q)
}

// wait blocks until Block is closed or ctx is done; it returns at once when Block is nil.
func (f *Fake) wait(ctx context.Context) error {
	if f.Block == nil {
		return nil
	}
	select {
	case <-f.Block:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// cloneBatch returns b with its own Records slice.
func cloneBatch(b store.Batch) store.Batch {
	b.Records = slices.Clone(b.Records)
	if b.Import != nil {
		step := *b.Import
		b.Import = &step
	}
	return b
}

// Batches returns copies of the batches written so far, in call order.
func (f *Fake) Batches() []store.Batch {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]store.Batch, len(f.batches))
	for i, b := range f.batches {
		out[i] = cloneBatch(b)
	}
	return out
}

// RecordQueries returns the record queries made so far, in call order.
func (f *Fake) RecordQueries() []store.RecordQuery {
	f.mu.Lock()
	defer f.mu.Unlock()
	return slices.Clone(f.queries)
}

// LogSearches returns the log searches made so far, in call order.
func (f *Fake) LogSearches() []store.LogSearch {
	f.mu.Lock()
	defer f.mu.Unlock()
	return slices.Clone(f.searches)
}

var (
	_ store.Writer        = (*Fake)(nil)
	_ store.RecordReader  = (*Fake)(nil)
	_ store.LogSearcher   = (*Fake)(nil)
	_ store.ImportTracker = (*Fake)(nil)
)
