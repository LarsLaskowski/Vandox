package syslogline_test

import (
	"encoding/binary"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/syslogline"
)

// be32 returns the four bytes of the two's complement of v, big-endian, as a time zone file stores a signed or unsigned 32-bit number.
func be32(v int64) []byte {
	return binary.BigEndian.AppendUint32(nil, uint32(v)) // #nosec G115 -- the two's complement of a 32-bit number is wanted
}

// tzif returns a version 1 time zone file with the given zone types and transitions (instants and type indexes).
func tzif(types []zoneType, instants []int32, indexes []byte) []byte {
	var names []byte
	var infos []byte
	for _, ty := range types {
		infos = append(infos, be32(int64(ty.offset))...)
		dst := byte(0)
		if ty.dst {
			dst = 1
		}
		infos = append(infos, dst, byte(len(names))) // #nosec G115 -- a handful of short names
		names = append(append(names, ty.name...), 0)
	}
	b := append([]byte("TZif"), 0)
	b = append(b, make([]byte, 15)...)
	for _, n := range []int{0, 0, 0, len(instants), len(types), len(names)} {
		b = append(b, be32(int64(n))...)
	}
	for _, at := range instants {
		b = append(b, be32(int64(at))...)
	}
	b = append(b, indexes...)
	b = append(b, infos...)
	return append(b, names...)
}

type zoneType struct {
	name   string
	offset int32
	dst    bool
}

// fixedZone returns the time zone file of a zone with one offset and no transition.
func fixedZone(offset int32) []byte {
	return tzif([]zoneType{{"TST", offset, false}}, nil, nil)
}

// centralEurope returns the time zone file of a zone that moves to +02:00 on 2026-03-29 at 01:00 UTC and back to +01:00 on
// 2026-10-25 at 01:00 UTC, as the zone of the monitored server does in 2026.
func centralEurope() []byte {
	return tzif(
		[]zoneType{{"CET", 3600, false}, {"CEST", 7200, true}},
		[]int32{unix32(time.Date(2026, 3, 29, 1, 0, 0, 0, time.UTC)), unix32(time.Date(2026, 10, 25, 1, 0, 0, 0, time.UTC))},
		[]byte{1, 0},
	)
}

// unix32 returns the Unix time of t as the 32-bit number of a version 1 time zone file.
func unix32(t time.Time) int32 {
	return int32(t.Unix()) // #nosec G115 -- the instants of 2026 fit 32 bits
}

func centralEuropeLocation(t *testing.T) *time.Location {
	t.Helper()
	loc, err := time.LoadLocationFromTZData("Test/CentralEurope", centralEurope())
	if err != nil {
		t.Fatalf("LoadLocationFromTZData() error = %v, want nil", err)
	}
	return loc
}

func utc(year int, month time.Month, day, hour, minute, second int) time.Time {
	return time.Date(year, month, day, hour, minute, second, 0, time.UTC)
}

// clockLine is a line fed to one clock and the instant it must resolve to; a zero want is a line without a header time.
type clockLine struct {
	line string
	want time.Time
}

func TestClock_YearAndDaylightSavingRules(t *testing.T) {
	tests := []struct {
		name  string
		now   time.Time
		lines []clockLine
	}{
		{"the current year", utc(2026, time.October, 23, 7, 20, 0), []clockLine{
			{"Oct 23 07:13:02 web-1 postfix/smtpd[4711]: connect", utc(2026, time.October, 23, 5, 13, 2)},
		}},
		{"a time in winter uses the winter offset", utc(2026, time.January, 20, 12, 0, 0), []clockLine{
			{"Jan 20 07:13:02 web-1 x: m", utc(2026, time.January, 20, 6, 13, 2)},
		}},
		{"the year before when the line would lie more than a day ahead", utc(2026, time.January, 2, 12, 0, 0), []clockLine{
			{"Dec 31 23:59:59 web-1 x: m", utc(2025, time.December, 31, 22, 59, 59)},
		}},
		{"the current year for a line less than a day ahead", utc(2026, time.January, 2, 12, 0, 0), []clockLine{
			{"Jan  3 12:00:00 web-1 x: m", utc(2026, time.January, 3, 11, 0, 0)},
		}},
		{"the year before for a line just over a day ahead", utc(2026, time.January, 2, 12, 0, 0), []clockLine{
			{"Jan  3 14:00:00 web-1 x: m", utc(2025, time.January, 3, 13, 0, 0)},
		}},
		{"the leap day of the current year", utc(2028, time.March, 1, 0, 0, 0), []clockLine{
			{"Feb 29 12:00:00 web-1 x: m", utc(2028, time.February, 29, 11, 0, 0)},
		}},
		{"a time in the hour skipped at the start of daylight saving time is shifted forward by the gap", utc(2026, time.March, 30, 0, 0, 0), []clockLine{
			{"Mar 29 01:59:59 web-1 x: m", utc(2026, time.March, 29, 0, 59, 59)},
			{"Mar 29 02:30:00 web-1 x: m", utc(2026, time.March, 29, 1, 30, 0)},
			{"Mar 29 03:00:00 web-1 x: m", utc(2026, time.March, 29, 1, 0, 0)},
		}},
		{"a time in the repeated hour takes the earlier offset", utc(2026, time.October, 26, 0, 0, 0), []clockLine{
			{"Oct 25 02:30:00 web-1 x: m", utc(2026, time.October, 25, 0, 30, 0)},
		}},
		{"a time in the repeated hour that lies more than ten minutes before the previous line takes the later offset", utc(2026, time.October, 26, 0, 0, 0), []clockLine{
			{"Oct 25 02:50:00 web-1 x: m", utc(2026, time.October, 25, 0, 50, 0)},
			{"Oct 25 02:05:00 web-1 x: m", utc(2026, time.October, 25, 1, 5, 0)},
			{"Oct 25 02:55:00 web-1 x: m", utc(2026, time.October, 25, 0, 55, 0)}, // exactly ten minutes before the previous line
			{"Oct 25 03:10:00 web-1 x: m", utc(2026, time.October, 25, 2, 10, 0)},
		}},
		{"a line without a header time does not move the previous instant", utc(2026, time.October, 26, 0, 0, 0), []clockLine{
			{"Oct 25 02:50:00 web-1 x: m", utc(2026, time.October, 25, 0, 50, 0)},
			{"this line has no header", time.Time{}},
			{"Oct 25 02:05:00 web-1 x: m", utc(2026, time.October, 25, 1, 5, 0)},
		}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			clock := syslogline.NewClock(centralEuropeLocation(t))

			for i, l := range tt.lines {
				requireClockLine(t, i, l, syslogline.Parse([]byte(l.line), clock, tt.now), tt.now)
			}
		})
	}
}

// requireClockLine checks the result of Parse for line l, which was parsed at now.
func requireClockLine(t *testing.T, i int, l clockLine, got syslogline.Line, now time.Time) {
	t.Helper()
	if l.want.IsZero() {
		if got.Header || !got.Time.IsZero() {
			t.Errorf("line %d Parse(%q) = header %v time %v, want no header and the zero time", i, l.line, got.Header, got.Time)
		}
		return
	}
	if !got.Header || !got.Time.Equal(l.want) || got.Time.Location() != time.UTC {
		t.Errorf("line %d Parse(%q) at %v = header %v time %v (%v), want %v in UTC", i, l.line, now, got.Header, got.Time, got.Time.Location(), l.want)
	}
}

func TestClock_KeepsItsPreviousInstantPerClock(t *testing.T) {
	loc := centralEuropeLocation(t)
	now := utc(2026, time.October, 26, 0, 0, 0)
	first, second := syslogline.NewClock(loc), syslogline.NewClock(loc)

	syslogline.Parse([]byte("Oct 25 02:50:00 web-1 x: m"), first, now)
	got := syslogline.Parse([]byte("Oct 25 02:05:00 web-1 x: m"), second, now)

	if want := utc(2026, time.October, 25, 0, 5, 0); !got.Time.Equal(want) {
		t.Errorf("Parse() on a clock that saw no earlier line = %v, want %v (the earlier offset: the other clock's line is not its previous line)", got.Time, want)
	}
}
