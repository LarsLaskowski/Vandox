package kernevents

import (
	"math"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func TestParseMicros(t *testing.T) {
	const maxMicros = math.MaxInt64 / 1000
	tests := []struct {
		name string
		in   string
		want time.Duration
		ok   bool
	}{
		{"zero", "0", 0, true},
		{"one", "1", time.Microsecond, true},
		{"one second", "1000000", time.Second, true},
		{"20 digits with leading zeros", "00000000000000000005", 5 * time.Microsecond, true},
		{"largest accepted", "9223372036854775", time.Duration(maxMicros) * time.Microsecond, true},
		{"one above the largest", "9223372036854776", 0, false},
		{"largest uint64", "18446744073709551615", 0, false},
		{"uint64 overflow", "18446744073709551616", 0, false},
		{"21 digits", "100000000000000000000", 0, false},
		{"21 digits of zeros", "000000000000000000000", 0, false},
		{"empty", "", 0, false},
		{"plus sign", "+5", 0, false},
		{"minus sign", "-5", 0, false},
		{"leading space", " 5", 0, false},
		{"trailing space", "5 ", 0, false},
		{"trailing line feed", "5\n", 0, false},
		{"hexadecimal", "0x10", 0, false},
		{"decimal point", "1.5", 0, false},
		{"letters", "abc", 0, false},
		{"full-width digit", "５", 0, false},
		{"digit then NUL", "5\x00", 0, false},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got, ok := parseMicros([]byte(tc.in))
			if ok != tc.ok || (ok && got != tc.want) {
				t.Errorf("parseMicros(%q) = %d, %v, want %d, %v", tc.in, got, ok, tc.want, tc.ok)
			}
			if ok && got < 0 {
				t.Errorf("parseMicros(%q) = %d, want a duration that is not negative", tc.in, got)
			}
		})
	}
}

func TestRealtimeOK(t *testing.T) {
	tests := []struct {
		us   uint64
		want bool
	}{
		{0, false},
		{1, true},
		{1790000000000000, true},
		{253402300799999999, true},
		{253402300800000000, false},
		{253402300800000001, false},
		{1 << 62, false},
		{math.MaxUint64, false},
	}
	for _, tc := range tests {
		if got := realtimeOK(tc.us); got != tc.want {
			t.Errorf("realtimeOK(%d) = %v, want %v", tc.us, got, tc.want)
		}
	}
}

// requireNewGap checks the gap record newGap made.
func requireNewGap(t *testing.T, rec model.Record, from, wantTo, at time.Time) {
	t.Helper()
	g, ok := rec.Data.(*model.Gap)
	if !ok {
		t.Fatalf("newGap().Data = %T, want *model.Gap", rec.Data)
	}
	if !g.From.Equal(from) || !g.To.Equal(wantTo) {
		t.Errorf("newGap() = [%s, %s), want [%s, %s)", g.From, g.To, from, wantTo)
	}
	if g.From.Location() != time.UTC || g.To.Location() != time.UTC {
		t.Errorf("newGap() times in %v and %v, want UTC", g.From.Location(), g.To.Location())
	}
	if g.Cause != model.GapUnknown || g.Collector != Name || rec.Source != Name {
		t.Errorf("newGap() cause = %q, collector = %q, source = %q, want %q, %q, %q", g.Cause, g.Collector, rec.Source, model.GapUnknown, Name, Name)
	}
	if !rec.CapturedAt.Equal(at) {
		t.Errorf("newGap() CapturedAt = %s, want %s", rec.CapturedAt, at)
	}
	checkValid(t, []model.Record{rec}, at)
}

func TestNewGap(t *testing.T) {
	from := time.Date(2026, time.October, 20, 10, 0, 0, 0, time.UTC)
	at := time.Date(2026, time.October, 22, 12, 0, 0, 0, time.UTC)
	zone := time.FixedZone("test", 5*3600)
	tests := []struct {
		name     string
		from, to time.Time
		wantTo   time.Time
	}{
		{"to after from", from, from.Add(time.Minute), from.Add(time.Minute)},
		{"one microsecond", from, from.Add(time.Microsecond), from.Add(time.Microsecond)},
		{"equal", from, from, from.Add(time.Microsecond)},
		{"reversed", from, from.Add(-time.Hour), from.Add(time.Microsecond)},
		{"zone is UTC", from.In(zone), from.Add(time.Minute).In(zone), from.Add(time.Minute)},
		{"equal in a zone", from.In(zone), from.In(zone), from.Add(time.Microsecond)},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			requireNewGap(t, newGap(tc.from, tc.to, at), tc.from, tc.wantTo, at)
		})
	}
}

// eventRecord returns a kill event captured at at; pid 0 makes it invalid.
func eventRecord(pid int32, at time.Time) model.Record {
	return model.Record{
		Meta: model.Meta{Source: Name, CapturedAt: at},
		Data: &model.KernelEvent{Type: model.KernelEventOOMKill, OOMKill: &model.OOMKill{VictimPID: pid, VictimCommand: "p"}},
	}
}

func TestCheckRecords_RefusedRecordBecomesGap(t *testing.T) {
	at := time.Date(2026, time.October, 22, 12, 0, 0, 0, time.UTC)
	before := at.Add(-time.Hour)
	entryTime := at.Add(-30 * time.Minute)
	valid := eventRecord(5, entryTime.Add(-time.Minute))
	good := newGap(before, entryTime, at)
	in := []model.Record{valid, eventRecord(0, entryTime), good, eventRecord(-3, entryTime.Add(time.Second))}
	froms := map[int]time.Time{1: before, 3: entryTime.Add(time.Minute)}

	got := checkRecords(in, at, func(i int) time.Time { return froms[i] })

	if len(got) != len(in) {
		t.Fatalf("checkRecords() returned %d records, want %d", len(got), len(in))
	}
	if got[0].Data != valid.Data || got[2].Data != good.Data {
		t.Errorf("checkRecords() changed a valid record: got %v and %v, want the records unchanged", got[0].Data, got[2].Data)
	}
	gaps := gapsOf(got)
	if len(gaps) != 3 {
		t.Fatalf("checkRecords() returned %d gaps, want 3 (the valid one and the two replacements)", len(gaps))
	}
	requireGap(t, gaps[0], before, entryTime)
	requireGap(t, gaps[1], before, entryTime)
	requireGap(t, gaps[2], entryTime.Add(time.Minute), entryTime.Add(time.Minute+time.Microsecond))
	for i := range gaps {
		if !gaps[i].rec.CapturedAt.Equal(at) {
			t.Errorf("gap %d CapturedAt = %s, want %s", i, gaps[i].rec.CapturedAt, at)
		}
	}
	checkValid(t, got, at)
}

func TestCheckRecords_RefusedGapBecomesFallbackGap(t *testing.T) {
	at := time.Date(2026, time.October, 22, 12, 0, 0, 0, time.UTC)
	bad := []model.Record{
		{Meta: model.Meta{Source: Name, CapturedAt: at}, Data: &model.Gap{From: at, To: at, Cause: model.GapUnknown, Collector: Name}},
		{Meta: model.Meta{Source: Name, CapturedAt: at}, Data: &model.Gap{From: at, To: at.Add(-time.Hour), Cause: model.GapUnknown, Collector: Name}},
		{Meta: model.Meta{Source: Name, CapturedAt: at}, Data: &model.Gap{From: time.Time{}, To: at, Cause: model.GapUnknown, Collector: Name}},
		{Meta: model.Meta{Source: Name, CapturedAt: at}, Data: &model.Gap{From: at.Add(-time.Hour), To: time.Date(10000, time.January, 1, 0, 0, 0, 0, time.UTC), Cause: model.GapUnknown, Collector: Name}},
	}

	got := checkRecords(bad, at, func(int) time.Time { return at.Add(-2 * time.Hour) })

	if len(got) != len(bad) {
		t.Fatalf("checkRecords() returned %d records, want %d", len(got), len(bad))
	}
	gaps := gapsOf(got)
	if len(gaps) != len(bad) {
		t.Fatalf("checkRecords() returned %d gaps, want %d", len(gaps), len(bad))
	}
	for _, g := range gaps {
		requireGap(t, g, at.Add(-time.Microsecond), at)
	}
	checkValid(t, got, at)
}

func TestCheckRecords_EverythingValidIsUnchanged(t *testing.T) {
	at := time.Date(2026, time.October, 22, 12, 0, 0, 0, time.UTC)
	in := []model.Record{eventRecord(5, at.Add(-time.Minute)), newGap(at.Add(-time.Hour), at.Add(-time.Minute), at), eventRecord(6, at.Add(-time.Second))}

	got := checkRecords(in, at, func(int) time.Time {
		t.Errorf("checkRecords() asked for the start of a gap although no record was refused")
		return at
	})

	if len(got) != len(in) {
		t.Fatalf("checkRecords() returned %d records, want %d", len(got), len(in))
	}
	for i := range in {
		if got[i].Data != in[i].Data || got[i].Meta != in[i].Meta {
			t.Errorf("checkRecords() record %d = %+v, want the input %+v", i, got[i], in[i])
		}
	}
	if got := checkRecords(nil, at, nil); len(got) != 0 {
		t.Errorf("checkRecords(nil) = %d records, want none", len(got))
	}
}

func TestCheckRecords_OversizedRecordBecomesGap(t *testing.T) {
	at := time.Date(2026, time.October, 22, 12, 0, 0, 0, time.UTC)
	huge := model.Record{
		Meta: model.Meta{Source: Name, CapturedAt: at.Add(-time.Minute)},
		Data: &model.KernelEvent{
			Type:    model.KernelEventOOMKill,
			OOMKill: &model.OOMKill{VictimPID: 5, VictimCommand: "p"},
			Message: strings.Repeat("a", model.MaxTextBytes+1),
		},
	}

	got := checkRecords([]model.Record{huge}, at, func(int) time.Time { return at.Add(-time.Hour) })

	gaps := gapsOf(got)
	if len(got) != 1 || len(gaps) != 1 {
		t.Fatalf("checkRecords() = %d records with %d gaps, want one gap in place of the record", len(got), len(gaps))
	}
	requireGap(t, gaps[0], at.Add(-time.Hour), at.Add(-time.Minute))
}
