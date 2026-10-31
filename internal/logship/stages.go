package logship

import (
	"context"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/logstages"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// stage hands a mapped, filtered line of the source key to its stages and emits the records that became ready.
func (r *run) stage(key stageKey, line logstages.Line, captured time.Time) {
}

// closeStages closes the groups of the source key and emits their records.
func (r *run) closeStages(key stageKey) {
}

// Flush closes every open group of a fork of the committed state and returns the records. It commits nothing itself: only
// Kept(true) for the returned run adopts the fork and saves the shipped positions.
func (l *Logs) Flush(ctx context.Context, run scheduler.Run) []model.Record {
	return nil
}
