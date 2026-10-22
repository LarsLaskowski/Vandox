package kernevents

import (
	"math"
	"strconv"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	// realtimeLimit is 1 Jan 10000 UTC in µs, the first time time.Time.MarshalJSON refuses.
	realtimeLimit = 253402300800000000
	// maxMicros is the largest stamp in µs that converts to a time.Duration.
	maxMicros = math.MaxInt64 / 1000
	// maxStampDigits is the most digits of a stamp text.
	maxStampDigits = 20
)

// checkRecord is the record check checkRecords calls; the hook tests swap it and never run in parallel.
var checkRecord = wire.CheckRecord

// realtimeOK reports whether us is a usable realtime stamp: 0 < us < 253402300800000000.
func realtimeOK(us uint64) bool {
	return us > 0 && us < realtimeLimit
}

// parseMicros parses 1-20 ASCII digits of microseconds, overflow checked, at most MaxInt64/1000 µs.
func parseMicros(b []byte) (time.Duration, bool) {
	s := string(b)
	if len(s) == 0 || len(s) > maxStampDigits || !allDigits(s) {
		return 0, false
	}
	us, err := strconv.ParseUint(s, 10, 64)
	if err != nil || us > maxMicros {
		return 0, false
	}
	return time.Duration(us) * time.Microsecond, true
}

// micros turns a stamp that realtimeOK accepted into a UTC time.
func micros(us uint64) time.Time {
	if us > math.MaxInt64 {
		us = math.MaxInt64
	}
	return time.UnixMicro(int64(us)).UTC()
}

// newGap returns a gap record for [from, to) detected at at; To is From + 1 µs when to is not after from.
func newGap(from, to, at time.Time) model.Record {
	from, to = from.UTC(), to.UTC()
	if !to.After(from) {
		to = from.Add(time.Microsecond)
	}
	return model.Record{
		Meta: model.Meta{Source: Name, CapturedAt: at},
		Data: &model.Gap{From: from, To: to, Cause: model.GapUnknown, Collector: Name},
	}
}

// checkRecords replaces every record that the record check refuses with a gap over the time of its entry, starting at gapFrom(i); a
// gap that is refused becomes [at - 1 µs, at), so every record returned passes the check.
func checkRecords(records []model.Record, at time.Time, gapFrom func(i int) time.Time) []model.Record {
	for i := range records {
		if accepted(&records[i], at) {
			continue
		}
		if _, isGap := records[i].Data.(*model.Gap); !isGap {
			replacement := newGap(gapFrom(i), records[i].CapturedAt, at)
			if accepted(&replacement, at) {
				records[i] = replacement
				continue
			}
		}
		records[i] = newGap(at.Add(-time.Microsecond), at, at)
	}
	return records
}

// accepted reports whether the record check takes r as the scheduler would stamp it.
func accepted(r *model.Record, at time.Time) bool {
	c := *r
	c.Origin, c.Seq = model.OriginAgent, math.MaxInt64
	if c.CapturedAt.IsZero() {
		c.CapturedAt = at
	}
	_, err := checkRecord(&c)
	return err == nil
}
