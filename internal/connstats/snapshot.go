package connstats

import (
	"errors"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// lists are the ordered lists of a run before they are cut to the payload budget.
type lists struct{}

// fill fills snap entry by entry within budget bytes and reports whether aggregates or any list were cut.
func fill(snap *model.ConnectionSnapshot, lists *lists, budget int) (cutAggregates, cut bool) {
	return false, false
}

// fitRecord returns the record of snap, shortened until it fits the wire limit.
func fitRecord(start time.Time, snap *model.ConnectionSnapshot) (model.Record, error) {
	return model.Record{}, errors.New("not implemented")
}
