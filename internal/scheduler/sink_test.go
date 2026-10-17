package scheduler_test

import (
	"context"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

func TestDiscardSink_Put(t *testing.T) {
	tests := []struct {
		name    string
		records []model.Record
	}{
		{"nil", nil},
		{"empty", []model.Record{}},
		{"records", []model.Record{metricRecord("a", 1), metricRecord("b", 2)}},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			err := scheduler.DiscardSink{}.Put(context.Background(), tc.records)

			if err != nil {
				t.Errorf("DiscardSink.Put(%d records) = %v, want nil", len(tc.records), err)
			}
		})
	}
}
