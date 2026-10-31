package syslogline

import "time"

const (
	// futureTolerance is how far ahead of the time of reading a line may lie before it belongs to the year before.
	futureTolerance = 24 * time.Hour
	// backwardTolerance is how far back a local time in the repeated hour may step and still take the earlier offset.
	backwardTolerance = 10 * time.Minute
	// probe is how far from a local time the zone's offsets before and after a transition around it are read.
	probe = 24 * time.Hour
)

// The range of instants the storage holds (1677-09-21T00:12:43.1452242Z to 2262-04-11T23:47:16.8547758Z), as the import checks it.
var (
	storableMin = time.Unix(0, -9223372036854775800).UTC()
	storableMax = time.Unix(0, 9223372036854775800).UTC()
)

// The first and the last year the storage can hold.
const (
	firstStorableYear = 1677
	lastStorableYear  = 2262
)

// Clock turns the time of a traditional syslog header (no year, no zone) into an instant. One Clock serves one file and keeps the
// previous line's instant.
type Clock struct {
	loc  *time.Location
	prev time.Time
}

// NewClock returns a clock for lines written in loc.
func NewClock(loc *time.Location) *Clock {
	return &Clock{loc: loc}
}

// SetZone changes the zone the clock reads the times of the file in, and keeps its previous instant.
func (c *Clock) SetZone(loc *time.Location) {
	c.loc = loc
}

// resolve turns a year-less local time into a UTC instant. The year is the current year of now in the zone, or the year before when the
// line would lie more than a day ahead of now. A time in the hour skipped at the start of daylight saving time is shifted forward by the
// gap; a time in the repeated hour takes the earlier offset unless that lies more than ten minutes before the previous line.
func (c *Clock) resolve(ts stamp, now time.Time) (time.Time, bool) {
	year := now.In(c.loc).Year()
	if c.instant(year, ts, false).After(now.Add(futureTolerance)) {
		year--
	}
	if ts.day > daysIn(year, ts.month) {
		return time.Time{}, false
	}
	instant := c.instant(year, ts, true)
	if instant.Before(storableMin) || instant.After(storableMax) {
		return time.Time{}, false
	}
	c.prev = instant
	return instant, true
}

// instant maps the local time of ts in year to an instant; a day beyond the end of the month rolls over into the next. withPrev lets the
// previous instant of the clock decide a time in the repeated hour.
func (c *Clock) instant(year int, ts stamp, withPrev bool) time.Time {
	wall := time.Date(year, time.Month(ts.month), ts.day, ts.hour, ts.minute, ts.second, 0, time.UTC)
	before, after := offsetAt(c.loc, wall.Add(-probe)), offsetAt(c.loc, wall.Add(probe))
	first := wall.Add(-seconds(before))
	firstOK := offsetAt(c.loc, first) == before
	if before == after {
		return first
	}
	second := wall.Add(-seconds(after))
	secondOK := offsetAt(c.loc, second) == after
	switch {
	case firstOK && secondOK:
		early, late := first, second
		if early.After(late) {
			early, late = late, early
		}
		if withPrev && !c.prev.IsZero() && early.Before(c.prev.Add(-backwardTolerance)) {
			return late
		}
		return early
	case secondOK:
		return second
	default:
		return first // the time was skipped: counted with the offset before the gap, it lies behind the gap
	}
}

// seconds returns n seconds as a duration.
func seconds(n int) time.Duration { return time.Duration(n) * time.Second }

// offsetAt returns the offset of loc in seconds east of UTC at the instant t.
func offsetAt(loc *time.Location, t time.Time) int {
	_, offset := t.In(loc).Zone()
	return offset
}

// daysIn returns the number of days of the month in the year.
func daysIn(year, month int) int {
	switch month {
	case int(time.February):
		if year%4 == 0 && (year%100 != 0 || year%400 == 0) {
			return 29
		}
		return 28
	case int(time.April), int(time.June), int(time.September), int(time.November):
		return 30
	default:
		return 31
	}
}

// resolveAbsolute turns an RFC 3339 time into a UTC instant; false means the date does not exist or the instant is not storable.
func resolveAbsolute(ts stamp) (time.Time, bool) {
	if ts.day > daysIn(ts.year, ts.month) {
		return time.Time{}, false
	}
	instant := time.Date(ts.year, time.Month(ts.month), ts.day, ts.hour, ts.minute, ts.second, ts.nanos, time.UTC).Add(-time.Duration(ts.offset) * time.Minute)
	if instant.Before(storableMin) || instant.After(storableMax) {
		return time.Time{}, false
	}
	return instant, true
}

// ResolveLocal turns a local time with its year, in the zone of the clock, into a UTC instant with the fraction nanos. False means the
// date does not exist or the instant, with or without the fraction, is not storable. On success the instant becomes the previous instant of the clock.
func (c *Clock) ResolveLocal(year, month, day, hour, minute, second, nanos int) (time.Time, bool) {
	if month < 1 || month > 12 || day < 1 || hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59 {
		return time.Time{}, false
	}
	if year < firstStorableYear || year > lastStorableYear || day > daysIn(year, month) {
		return time.Time{}, false
	}
	whole := c.instant(year, stamp{month: month, day: day, hour: hour, minute: minute, second: second}, true)
	if whole.Before(storableMin) || whole.After(storableMax) { // the import checks the whole second first, then again with the fraction
		return time.Time{}, false
	}
	instant := whole.Add(time.Duration(nanos))
	if instant.Before(storableMin) || instant.After(storableMax) {
		return time.Time{}, false
	}
	c.prev = instant
	return instant, true
}
