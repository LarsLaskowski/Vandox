package logship

import (
	"errors"
	"reflect"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/journal"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

var recordsAt = time.Date(2026, time.October, 23, 7, 20, 0, 0, time.UTC)

// goodRecord returns a log line record that passes the record check.
func goodRecord(message string, captured time.Time) model.Record {
	return model.Record{
		Meta: model.Meta{Source: sourceJournal, CapturedAt: captured},
		Data: &model.LogLine{Log: logJournal, Host: "web-1", Program: "app", PID: 7, Message: message},
	}
}

// refusedRecord returns a log line record the record check refuses: its process ID is negative.
func refusedRecord(captured time.Time) model.Record {
	return model.Record{
		Meta: model.Meta{Source: sourceJournal, CapturedAt: captured},
		Data: &model.LogLine{Log: logJournal, Host: "web-1", Program: "app", PID: -1, Message: "refused"},
	}
}

// swapCheck makes the record check refuse the records for which refuse answers true, and restores it after the test.
func swapCheck(t *testing.T, refuse func(r *model.Record) bool) {
	t.Helper()
	original := checkRecord
	t.Cleanup(func() { checkRecord = original })
	checkRecord = func(r *model.Record) (int, error) {
		if refuse(r) {
			return 0, errors.New("refused by the test")
		}
		return wire.CheckRecord(r)
	}
}

func requireGapRecord(t *testing.T, rec model.Record, from, to, captured time.Time) {
	t.Helper()
	g, ok := rec.Data.(*model.Gap)
	if !ok {
		t.Fatalf("record = %T, want a gap", rec.Data)
	}
	if g.Collector != Name || g.Cause != model.GapUnknown {
		t.Errorf("gap collector %q cause %q, want collector %q cause %q", g.Collector, g.Cause, Name, model.GapUnknown)
	}
	if !g.From.Equal(from) || !g.To.Equal(to) {
		t.Errorf("gap = [%v, %v), want [%v, %v)", g.From, g.To, from, to)
	}
	if !rec.CapturedAt.Equal(captured) || rec.Source != Name {
		t.Errorf("gap record source %q captured %v, want source %q captured %v", rec.Source, rec.CapturedAt, Name, captured)
	}
}

func TestRecords_RefusedRecordBecomesAGap(t *testing.T) {
	t.Run("a refused log line becomes a gap up to its capture time", refusedLogLineBecomesAGap)
	t.Run("a gap that would end before it starts is one microsecond long", func(t *testing.T) {
		captured := recordsAt.Add(-time.Hour)
		from := recordsAt.Add(-time.Minute)

		got := checkRecords([]model.Record{refusedRecord(captured)}, recordsAt, func(int) time.Time { return from })

		requireGapRecord(t, got[0], from, from.Add(time.Microsecond), recordsAt)
	})
	t.Run("a gap from a local time zone is stored in UTC", func(t *testing.T) {
		zone := time.FixedZone("", 2*3600)
		captured := recordsAt.Add(-time.Minute).In(zone)

		got := checkRecords([]model.Record{refusedRecord(captured)}, recordsAt.In(zone), func(int) time.Time { return recordsAt.Add(-time.Hour).In(zone) })

		g, ok := got[0].Data.(*model.Gap)
		if !ok {
			t.Fatalf("record = %T, want a gap", got[0].Data)
		}
		if g.From.Location() != time.UTC || g.To.Location() != time.UTC || got[0].CapturedAt.Location() != time.UTC {
			t.Errorf("gap locations %v, %v, %v, want UTC", g.From.Location(), g.To.Location(), got[0].CapturedAt.Location())
		}
	})
	t.Run("a record without a capture time is checked as stamped with the run time", func(t *testing.T) {
		unstamped := goodRecord("unstamped", time.Time{})

		got := checkRecords([]model.Record{unstamped}, recordsAt, func(int) time.Time { return recordsAt })

		if !reflect.DeepEqual(got[0], unstamped) {
			t.Errorf("checkRecords() = %+v, want the record unchanged: the scheduler stamps it", got[0])
		}
	})
	t.Run("a gap that is refused becomes the shortest gap that ends at the run time", refusedGapBecomesTheShortestGap)
	t.Run("a replacement that is refused too becomes the shortest gap that ends at the run time", func(t *testing.T) {
		swapCheck(t, func(r *model.Record) bool {
			g, isGap := r.Data.(*model.Gap)
			return isGap && g.To.Equal(recordsAt.Add(-time.Minute))
		})

		got := checkRecords([]model.Record{refusedRecord(recordsAt.Add(-time.Minute))}, recordsAt, func(int) time.Time { return recordsAt.Add(-time.Hour) })

		requireGapRecord(t, got[0], recordsAt.Add(-time.Microsecond), recordsAt, recordsAt)
	})
}

func refusedLogLineBecomesAGap(t *testing.T) {
	captured := recordsAt.Add(-time.Minute)
	from := recordsAt.Add(-time.Hour)
	first, last := goodRecord("first", recordsAt.Add(-2*time.Minute)), goodRecord("last", recordsAt.Add(-30*time.Second))
	records := []model.Record{first, refusedRecord(captured), last}
	var asked []int

	got := checkRecords(records, recordsAt, func(i int) time.Time {
		asked = append(asked, i)
		return from
	})

	if len(got) != 3 {
		t.Fatalf("checkRecords() returned %d records, want 3", len(got))
	}
	if !reflect.DeepEqual(got[0], first) || !reflect.DeepEqual(got[2], last) {
		t.Errorf("checkRecords() changed the records that pass the check: %+v and %+v, want %+v and %+v", got[0], got[2], first, last)
	}
	requireGapRecord(t, got[1], from, captured, recordsAt)
	if !reflect.DeepEqual(asked, []int{1}) {
		t.Errorf("gapFrom asked for %v, want only the index 1 of the refused record", asked)
	}
}

func refusedGapBecomesTheShortestGap(t *testing.T) {
	swapCheck(t, func(r *model.Record) bool {
		_, isGap := r.Data.(*model.Gap)
		return isGap && r.CapturedAt.Equal(recordsAt.Add(-time.Hour))
	})
	bad := model.Record{
		Meta: model.Meta{Source: Name, CapturedAt: recordsAt.Add(-time.Hour)},
		Data: &model.Gap{From: recordsAt.Add(-2 * time.Hour), To: recordsAt.Add(-time.Hour), Cause: model.GapUnknown, Collector: Name},
	}

	got := checkRecords([]model.Record{bad}, recordsAt, func(int) time.Time {
		t.Error("gapFrom was asked for a record that is a gap, want a gap never replaced by a gap from its own start")
		return recordsAt
	})

	requireGapRecord(t, got[0], recordsAt.Add(-time.Microsecond), recordsAt, recordsAt)
}

func TestRecords_JournalEntryWithoutAUsableTimeMapsToNothing(t *testing.T) {
	message := []journal.Field{{Name: fieldMessage, Value: []byte("text")}}
	tests := []struct {
		name  string
		entry journal.Entry
		want  bool
	}{
		{"a usable entry", journal.Entry{RealtimeUS: 1, Realtime: recordsAt, Fields: message}, true},
		{"the largest storable time", journal.Entry{RealtimeUS: maxRealtimeUS, Realtime: recordsAt, Fields: message}, true},
		{"no time", journal.Entry{Fields: message}, false},
		{"a time beyond the storable range", journal.Entry{RealtimeUS: maxRealtimeUS + 1, Realtime: recordsAt, Fields: message}, false},
		{"no message", journal.Entry{RealtimeUS: 1, Realtime: recordsAt}, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			rec, ok := journalRecord(&tt.entry)

			if ok != tt.want || (ok && lineOf(rec) == nil) {
				t.Errorf("journalRecord() = %+v, %v, want ok %v", rec, ok, tt.want)
			}
		})
	}
}
