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

func TestClock_SetZoneKeepsThePreviousInstant(t *testing.T) {
	now := utc(2026, time.October, 26, 0, 0, 0)
	clock := syslogline.NewClock(centralEuropeLocation(t))
	first := syslogline.Parse([]byte("Oct 25 02:50:00 web-1 x: m"), clock, now)

	clock.SetZone(centralEuropeLocation(t)) // the same rules, loaded again
	second := syslogline.Parse([]byte("Oct 25 02:05:00 web-1 x: m"), clock, now)

	if want := utc(2026, time.October, 25, 0, 50, 0); !first.Time.Equal(want) {
		t.Fatalf("first line = %v, want %v", first.Time, want)
	}
	if want := utc(2026, time.October, 25, 1, 5, 0); !second.Time.Equal(want) {
		t.Errorf("line after SetZone = %v, want %v: the previous instant 00:50 UTC is kept, so the repeated hour takes the later offset", second.Time, want)
	}
}

func TestClock_ResolveLocalRefusesWhatTheImportRefuses(t *testing.T) {
	tests := []struct {
		name                                         string
		year, month, day, hour, minute, second, nano int
		want                                         time.Time // zero when the time is refused
	}{
		{"a valid time", 2026, 10, 9, 3, 12, 12, 0, utc(2026, time.October, 9, 3, 12, 12)},
		{"the fraction is added", 2026, 10, 9, 3, 12, 12, 123456000, utc(2026, time.October, 9, 3, 12, 12).Add(123456000)},
		{"month 0", 2026, 0, 9, 3, 12, 12, 0, time.Time{}},
		{"month 13", 2026, 13, 9, 3, 12, 12, 0, time.Time{}},
		{"day 0", 2026, 10, 0, 3, 12, 12, 0, time.Time{}},
		{"day 32", 2026, 10, 32, 3, 12, 12, 0, time.Time{}},
		{"30 February", 2026, 2, 30, 3, 12, 12, 0, time.Time{}},
		{"31 April", 2026, 4, 31, 3, 12, 12, 0, time.Time{}},
		{"29 February of 2025", 2025, 2, 29, 3, 12, 12, 0, time.Time{}},
		{"29 February of 2024", 2024, 2, 29, 3, 12, 12, 0, utc(2024, time.February, 29, 3, 12, 12)},
		{"29 February of 2100", 2100, 2, 29, 3, 12, 12, 0, time.Time{}},
		{"29 February of 2000", 2000, 2, 29, 3, 12, 12, 0, utc(2000, time.February, 29, 3, 12, 12)},
		{"hour 24", 2026, 10, 9, 24, 0, 0, 0, time.Time{}},
		{"hour -1", 2026, 10, 9, -1, 0, 0, 0, time.Time{}},
		{"minute 60", 2026, 10, 9, 3, 60, 0, 0, time.Time{}},
		{"second 60", 2026, 10, 9, 3, 12, 60, 0, time.Time{}},
		{"year 1676", 1676, 12, 31, 23, 59, 59, 0, time.Time{}},
		{"year 2263", 2263, 1, 1, 0, 0, 0, 0, time.Time{}},
		{"year 1600", 1600, 1, 1, 0, 0, 0, 0, time.Time{}},
		{"the last second before the storable range", 1677, 9, 21, 0, 12, 42, 0, time.Time{}},
		{"a fraction that stays before the storable range", 1677, 9, 21, 0, 12, 43, 100000000, time.Time{}},
		{"a fraction that would reach the storable range from a whole second before it", 1677, 9, 21, 0, 12, 43, 200000000, time.Time{}},
		{"a fraction on the first whole second of the storable range", 1677, 9, 21, 0, 12, 44, 200000000, utc(1677, time.September, 21, 0, 12, 44).Add(200000000)},
		{"the first second of the storable range", 1677, 9, 21, 0, 12, 44, 0, utc(1677, time.September, 21, 0, 12, 44)},
		{"the last whole second of the storable range", 2262, 4, 11, 23, 47, 16, 0, utc(2262, time.April, 11, 23, 47, 16)},
		{"a fraction that stays inside the storable range", 2262, 4, 11, 23, 47, 16, 500000000, utc(2262, time.April, 11, 23, 47, 16).Add(500000000)},
		{"a fraction that crosses the end of the storable range", 2262, 4, 11, 23, 47, 16, 999999000, time.Time{}},
		{"the second past the end of the storable range", 2262, 4, 11, 23, 47, 17, 0, time.Time{}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			clock := syslogline.NewClock(time.UTC)

			got, ok := clock.ResolveLocal(tt.year, tt.month, tt.day, tt.hour, tt.minute, tt.second, tt.nano)

			if wantOK := !tt.want.IsZero(); ok != wantOK {
				t.Fatalf("ResolveLocal(%d-%d-%d %d:%d:%d.%d) ok = %v, want %v", tt.year, tt.month, tt.day, tt.hour, tt.minute, tt.second, tt.nano, ok, wantOK)
			}
			if !ok {
				if !got.IsZero() {
					t.Errorf("ResolveLocal() = %v on refusal, want the zero time", got)
				}
				return
			}
			if !got.Equal(tt.want) || got.Location() != time.UTC {
				t.Errorf("ResolveLocal() = %v (%v), want %v in UTC", got, got.Location(), tt.want)
			}
		})
	}
}

func TestClock_ResolveLocalKeepsThePreviousInstantOnFailure(t *testing.T) {
	failures := []struct {
		name                                         string
		year, month, day, hour, minute, second, nano int
	}{
		{"month 13", 2026, 13, 25, 2, 0, 0, 0},
		{"day 0", 2026, 10, 0, 2, 0, 0, 0},
		{"31 April", 2026, 4, 31, 2, 0, 0, 0},
		{"hour 24", 2026, 10, 25, 24, 0, 0, 0},
		{"year 1676", 1676, 10, 25, 2, 0, 0, 0},
		{"year 2263", 2263, 10, 25, 2, 0, 0, 0},
		{"a fraction that crosses the end of the storable range", 2262, 4, 12, 0, 47, 16, 999999999},
	}
	for _, f := range failures {
		t.Run(f.name, func(t *testing.T) {
			clock := syslogline.NewClock(centralEuropeLocation(t))
			if _, ok := clock.ResolveLocal(2026, 10, 25, 2, 50, 0, 0); !ok {
				t.Fatal("ResolveLocal() of the first line was refused, want accepted")
			}

			if got, ok := clock.ResolveLocal(f.year, f.month, f.day, f.hour, f.minute, f.second, f.nano); ok {
				t.Fatalf("ResolveLocal() = %v, want a refusal", got)
			}

			got, ok := clock.ResolveLocal(2026, 10, 25, 2, 5, 0, 0)
			if want := utc(2026, time.October, 25, 1, 5, 0); !ok || !got.Equal(want) {
				t.Errorf("ResolveLocal() after the refusal = %v (ok %v), want %v: the previous instant 00:50 UTC is still the clock's, so the repeated hour takes the later offset", got, ok, want)
			}
		})
	}
}

func TestClock_ResolveLocalFollowsTheDaylightSavingRules(t *testing.T) {
	type call struct {
		month, day, hour, minute, second, nanos int
		want                                    time.Time
	}
	tests := []struct {
		name  string
		calls []call
	}{
		{"a time in winter uses the winter offset", []call{
			{1, 20, 7, 13, 2, 0, utc(2026, time.January, 20, 6, 13, 2)},
		}},
		{"a time in summer uses the summer offset and the fraction is added", []call{
			{10, 9, 3, 12, 12, 123000000, utc(2026, time.October, 9, 1, 12, 12).Add(123000000)},
		}},
		{"a time in the skipped hour is shifted forward by the gap", []call{
			{3, 29, 1, 59, 59, 0, utc(2026, time.March, 29, 0, 59, 59)},
			{3, 29, 2, 30, 0, 0, utc(2026, time.March, 29, 1, 30, 0)},
			{3, 29, 3, 0, 0, 0, utc(2026, time.March, 29, 1, 0, 0)},
		}},
		{"a time in the repeated hour without a previous instant takes the earlier offset", []call{
			{10, 25, 2, 30, 0, 0, utc(2026, time.October, 25, 0, 30, 0)},
		}},
		{"a time in the repeated hour more than ten minutes before the previous instant takes the later offset", []call{
			{10, 25, 2, 50, 0, 0, utc(2026, time.October, 25, 0, 50, 0)},
			{10, 25, 2, 5, 0, 0, utc(2026, time.October, 25, 1, 5, 0)},
			{10, 25, 2, 55, 0, 0, utc(2026, time.October, 25, 0, 55, 0)}, // exactly ten minutes before the previous instant
			{10, 25, 3, 10, 0, 0, utc(2026, time.October, 25, 2, 10, 0)},
		}},
		{"the earlier offset stays when the line lies less than ten minutes before the previous instant", []call{
			{10, 25, 2, 50, 0, 0, utc(2026, time.October, 25, 0, 50, 0)},
			{10, 25, 2, 45, 0, 0, utc(2026, time.October, 25, 0, 45, 0)},
		}},
		{"the fraction does not decide the repeated hour", []call{
			{10, 25, 2, 59, 59, 900000000, utc(2026, time.October, 25, 0, 59, 59).Add(900000000)},
			{10, 25, 2, 10, 0, 0, utc(2026, time.October, 25, 1, 10, 0)},
		}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			clock := syslogline.NewClock(centralEuropeLocation(t))

			for i, c := range tt.calls {
				got, ok := clock.ResolveLocal(2026, c.month, c.day, c.hour, c.minute, c.second, c.nanos)

				if !ok || !got.Equal(c.want) || got.Location() != time.UTC {
					t.Errorf("call %d ResolveLocal(2026-%d-%d %d:%d:%d.%d) = %v (%v) ok %v, want %v in UTC", i, c.month, c.day, c.hour, c.minute, c.second, c.nanos, got, got.Location(), ok, c.want)
				}
			}
		})
	}
}
