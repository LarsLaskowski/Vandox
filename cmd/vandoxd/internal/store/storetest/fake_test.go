package storetest_test

import (
	"context"
	"errors"
	"reflect"
	"sync"
	"testing"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store"
	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store/storetest"
	"github.com/LarsLaskowski/Vandox/internal/model"
)

var baseTime = time.Date(2026, time.October, 6, 12, 0, 0, 0, time.UTC)

func metric(seq uint64) model.Record {
	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: "node", Seq: seq, CapturedAt: baseTime},
		Data: &model.MetricPoint{Name: "cpu.load", Value: 1},
	}
}

func batchOf(seqs ...uint64) store.Batch {
	b := store.Batch{AgentID: "agent-1", ReceivedAt: baseTime}
	for _, s := range seqs {
		b.Records = append(b.Records, metric(s))
	}
	return b
}

func recordQuery(source string) store.RecordQuery {
	return store.RecordQuery{Kind: model.KindMetric, Source: source, From: baseTime, To: baseTime.Add(time.Hour), Limit: 10}
}

func logSearch(text string) store.LogSearch {
	return store.LogSearch{Text: text, From: baseTime, To: baseTime.Add(time.Hour), Limit: 10}
}

func TestFake_WithoutHooks(t *testing.T) {
	f := &storetest.Fake{}
	ctx := context.Background()

	res, err := f.WriteBatch(ctx, batchOf(1, 2, 3))
	if err != nil || res != (store.WriteResult{Stored: 3}) {
		t.Errorf("WriteBatch() = %+v, %v, want {Stored: 3}, nil", res, err)
	}
	recs, err := f.Records(ctx, recordQuery("a"))
	if err != nil || len(recs) != 0 {
		t.Errorf("Records() = %v, %v, want no records and a nil error", recs, err)
	}
	hits, err := f.SearchLogs(ctx, logSearch("oom"))
	if err != nil || len(hits) != 0 {
		t.Errorf("SearchLogs() = %v, %v, want no records and a nil error", hits, err)
	}
}

func TestFake_RecordsCallsInOrder(t *testing.T) {
	f := &storetest.Fake{}
	ctx := context.Background()
	for _, b := range []store.Batch{batchOf(1), batchOf(2, 3)} {
		if _, err := f.WriteBatch(ctx, b); err != nil {
			t.Fatalf("WriteBatch() error = %v, want nil", err)
		}
	}
	for _, src := range []string{"a", "b"} {
		if _, err := f.Records(ctx, recordQuery(src)); err != nil {
			t.Fatalf("Records() error = %v, want nil", err)
		}
	}
	for _, text := range []string{"x", "y"} {
		if _, err := f.SearchLogs(ctx, logSearch(text)); err != nil {
			t.Fatalf("SearchLogs() error = %v, want nil", err)
		}
	}

	if got, want := f.Batches(), []store.Batch{batchOf(1), batchOf(2, 3)}; !reflect.DeepEqual(got, want) {
		t.Errorf("Batches() = %+v, want %+v", got, want)
	}
	if got, want := f.RecordQueries(), []store.RecordQuery{recordQuery("a"), recordQuery("b")}; !reflect.DeepEqual(got, want) {
		t.Errorf("RecordQueries() = %+v, want %+v", got, want)
	}
	if got, want := f.LogSearches(), []store.LogSearch{logSearch("x"), logSearch("y")}; !reflect.DeepEqual(got, want) {
		t.Errorf("LogSearches() = %+v, want %+v", got, want)
	}
}

func TestFake_RecordedCallsAreCopies(t *testing.T) {
	t.Run("batch records changed by the caller", func(t *testing.T) {
		f := &storetest.Fake{}
		b := batchOf(1, 2)
		if _, err := f.WriteBatch(context.Background(), b); err != nil {
			t.Fatalf("WriteBatch() error = %v, want nil", err)
		}

		b.Records[0] = metric(99)

		if got := f.Batches(); len(got) != 1 || !reflect.DeepEqual(got[0], batchOf(1, 2)) {
			t.Errorf("Batches() after the caller changed its slice = %+v, want the batch as written", got)
		}
	})

	t.Run("returned slices changed by the caller", func(t *testing.T) {
		f := &storetest.Fake{}
		ctx := context.Background()
		_, _ = f.WriteBatch(ctx, batchOf(1, 2))
		_, _ = f.Records(ctx, recordQuery("a"))
		_, _ = f.SearchLogs(ctx, logSearch("x"))

		got, queries, searches := f.Batches(), f.RecordQueries(), f.LogSearches()
		if len(got) != 1 || len(got[0].Records) != 2 || len(queries) != 1 || len(searches) != 1 {
			t.Fatalf("recorded calls = %d batches, %d queries, %d searches, want 1 of each", len(got), len(queries), len(searches))
		}
		got[0].Records[0] = metric(99)
		got[0] = batchOf(77)
		queries[0] = recordQuery("changed")
		searches[0] = logSearch("changed")

		if again := f.Batches(); !reflect.DeepEqual(again, []store.Batch{batchOf(1, 2)}) {
			t.Errorf("Batches() after the caller changed the result = %+v, want the batch as written", again)
		}
		if again := f.RecordQueries(); !reflect.DeepEqual(again, []store.RecordQuery{recordQuery("a")}) {
			t.Errorf("RecordQueries() after the caller changed the result = %+v, want the query as made", again)
		}
		if again := f.LogSearches(); !reflect.DeepEqual(again, []store.LogSearch{logSearch("x")}) {
			t.Errorf("LogSearches() after the caller changed the result = %+v, want the search as made", again)
		}
	})
}

var errBoom = errors.New("boom")

func TestFake_OnWrite(t *testing.T) {
	var seen store.Batch
	f := &storetest.Fake{OnWrite: func(b store.Batch) (store.WriteResult, error) {
		seen = b
		return store.WriteResult{Stored: 1, Duplicates: 4}, errBoom
	}}

	res, err := f.WriteBatch(context.Background(), batchOf(5))

	if res != (store.WriteResult{Stored: 1, Duplicates: 4}) || !errors.Is(err, errBoom) {
		t.Errorf("WriteBatch() = %+v, %v, want {Stored: 1, Duplicates: 4}, %v", res, err, errBoom)
	}
	if !reflect.DeepEqual(seen, batchOf(5)) {
		t.Errorf("OnWrite received %+v, want %+v", seen, batchOf(5))
	}
	if got := f.Batches(); len(got) != 1 {
		t.Errorf("Batches() = %+v, want the call recorded", got)
	}
}

func TestFake_OnRecords(t *testing.T) {
	want := []store.StoredRecord{{ID: 7, Record: metric(1), AgentID: "agent-1", ReceivedAt: baseTime}}
	var seen store.RecordQuery
	f := &storetest.Fake{OnRecords: func(q store.RecordQuery) ([]store.StoredRecord, error) {
		seen = q
		return want, errBoom
	}}

	got, err := f.Records(context.Background(), recordQuery("a"))

	if !reflect.DeepEqual(got, want) || !errors.Is(err, errBoom) {
		t.Errorf("Records() = %+v, %v, want %+v, %v", got, err, want, errBoom)
	}
	if seen != recordQuery("a") {
		t.Errorf("OnRecords received %+v, want %+v", seen, recordQuery("a"))
	}
	if got := f.RecordQueries(); len(got) != 1 {
		t.Errorf("RecordQueries() = %+v, want the call recorded", got)
	}
}

func TestFake_OnSearch(t *testing.T) {
	want := []store.StoredRecord{{ID: 8, Record: metric(2)}}
	var seen store.LogSearch
	f := &storetest.Fake{OnSearch: func(q store.LogSearch) ([]store.StoredRecord, error) {
		seen = q
		return want, nil
	}}

	got, err := f.SearchLogs(context.Background(), logSearch("oom"))

	if !reflect.DeepEqual(got, want) || err != nil {
		t.Errorf("SearchLogs() = %+v, %v, want %+v, nil", got, err, want)
	}
	if seen != logSearch("oom") {
		t.Errorf("OnSearch received %+v, want %+v", seen, logSearch("oom"))
	}
	if got := f.LogSearches(); len(got) != 1 {
		t.Errorf("LogSearches() = %+v, want the call recorded", got)
	}
}

// call is one of the three fake methods and the recorded-call counter of its kind.
type call struct {
	name     string
	run      func(ctx context.Context, f *storetest.Fake) error
	recorded func(f *storetest.Fake) int
}

func calls() []call {
	return []call{
		{"WriteBatch", func(ctx context.Context, f *storetest.Fake) error {
			_, err := f.WriteBatch(ctx, batchOf(1))
			return err
		}, func(f *storetest.Fake) int { return len(f.Batches()) }},
		{"Records", func(ctx context.Context, f *storetest.Fake) error {
			_, err := f.Records(ctx, recordQuery("a"))
			return err
		}, func(f *storetest.Fake) int { return len(f.RecordQueries()) }},
		{"SearchLogs", func(ctx context.Context, f *storetest.Fake) error {
			_, err := f.SearchLogs(ctx, logSearch("x"))
			return err
		}, func(f *storetest.Fake) int { return len(f.LogSearches()) }},
	}
}

// finished reports the result of the call started by run, or false when it has not returned yet.
func finished(done <-chan error) (ok bool, err error) {
	select {
	case err = <-done:
		return true, err
	default:
		return false, nil
	}
}

func TestFake_Block_ProceedsWhenClosed(t *testing.T) {
	for _, c := range calls() {
		t.Run(c.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				f := &storetest.Fake{Block: make(chan struct{})}
				done := make(chan error, 1)
				go func() { done <- c.run(context.Background(), f) }()

				synctest.Wait()
				recorded := c.recorded(f)
				early, _ := finished(done)
				close(f.Block)
				synctest.Wait()
				late, err := finished(done)

				if recorded != 1 {
					t.Errorf("recorded %s calls while blocked = %d, want 1", c.name, recorded)
				}
				if early {
					t.Errorf("%s returned while Block was open, want it to wait", c.name)
				}
				if !late || err != nil {
					t.Errorf("%s after Block was closed = %v (returned: %v), want nil and returned", c.name, err, late)
				}
			})
		})
	}
}

func TestFake_Block_ReturnsContextErrorWhenCancelled(t *testing.T) {
	for _, c := range calls() {
		t.Run(c.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				f := &storetest.Fake{Block: make(chan struct{})}
				ctx, cancel := context.WithCancel(context.Background())
				defer cancel()
				done := make(chan error, 1)
				go func() { done <- c.run(ctx, f) }()

				synctest.Wait()
				cancel()
				synctest.Wait()
				returned, err := finished(done)

				if !returned || !errors.Is(err, context.Canceled) {
					t.Errorf("%s after cancel = %v (returned: %v), want context.Canceled and returned", c.name, err, returned)
				}
				if got := c.recorded(f); got != 1 {
					t.Errorf("recorded %s calls = %d, want 1", c.name, got)
				}
			})
		})
	}
}

func TestFake_ConcurrentUse(t *testing.T) {
	const goroutines = 16
	f := &storetest.Fake{
		OnWrite: func(b store.Batch) (store.WriteResult, error) { return store.WriteResult{Stored: len(b.Records)}, nil },
	}
	var wg sync.WaitGroup
	for i := range goroutines {
		wg.Add(1)
		go func() {
			defer wg.Done()
			ctx := context.Background()
			_, _ = f.WriteBatch(ctx, batchOf(uint64(i+1)))
			_, _ = f.Records(ctx, recordQuery("a"))
			_, _ = f.SearchLogs(ctx, logSearch("x"))
			_ = f.Batches()
			_ = f.RecordQueries()
			_ = f.LogSearches()
		}()
	}
	wg.Wait()

	if got := len(f.Batches()); got != goroutines {
		t.Errorf("recorded batches = %d, want %d", got, goroutines)
	}
	if got := len(f.RecordQueries()); got != goroutines {
		t.Errorf("recorded record queries = %d, want %d", got, goroutines)
	}
	if got := len(f.LogSearches()); got != goroutines {
		t.Errorf("recorded log searches = %d, want %d", got, goroutines)
	}
}
