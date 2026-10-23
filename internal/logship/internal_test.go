package logship

import (
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/journal"
)

func TestStatOf_TellsSizeAndIdentityOfAnOpenFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "mail.log")
	if err := os.WriteFile(path, []byte("12345"), 0o600); err != nil {
		t.Fatalf("WriteFile() error = %v, want nil", err)
	}
	f, err := os.Open(path)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	size, id, ok := statOf(f)
	other, err := os.Open(path)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	defer func() { _ = other.Close() }()
	_, sameID, _ := statOf(other)

	if !ok || size != 5 || id == (identity{}) || id != sameID {
		t.Errorf("statOf() = %d, %+v, %v, want 5, the identity that a second open of the file has too, and true", size, id, ok)
	}
	if err := f.Close(); err != nil {
		t.Fatalf("Close() error = %v, want nil", err)
	}
	if size, id, ok := statOf(f); ok || size != 0 || id != (identity{}) {
		t.Errorf("statOf(closed file) = %d, %+v, %v, want 0, no identity and false", size, id, ok)
	}
}

func TestParseUUID_ReadsTheTextFormOnly(t *testing.T) {
	const valid = "0f8fad5b-d9cb-469f-a165-70867728950e"
	tests := []struct {
		name string
		in   string
		ok   bool
	}{
		{"a valid ID", valid, true},
		{"empty", "", false},
		{"one character short", valid[:35], false},
		{"one character long", valid + "0", false},
		{"no dash after the first group", "0f8fad5bad9cb-469f-a165-70867728950e", false},
		{"a dash missing in the second group", "0f8fad5b-d9cb0469f-a165-70867728950e", false},
		{"a dash missing in the third group", "0f8fad5b-d9cb-469f0a165-70867728950e", false},
		{"a dash missing in the fourth group", "0f8fad5b-d9cb-469f-a16570867728950e0", false},
		{"a digit that is not hexadecimal", "0f8fad5b-d9cb-469f-a165-70867728950g", false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			id, ok := parseUUID(tt.in)

			if ok != tt.ok {
				t.Errorf("parseUUID(%q) = %v, %v, want ok %v", tt.in, id, ok, tt.ok)
			}
			if ok && formatUUID(id) != tt.in {
				t.Errorf("formatUUID(parseUUID(%q)) = %q, want the same text", tt.in, formatUUID(id))
			}
		})
	}
}

func TestPositionsOf_SavesIDsWithAnOnlineFileFirstThenTheNewest(t *testing.T) {
	at := func(m int) time.Time { return time.Date(2026, time.October, 23, 6, m, 0, 0, time.UTC) }
	ids := func(n byte) [16]byte { return [16]byte{15: n} }
	s := &state{journal: map[[16]byte]*jpos{
		ids(1): {seq: 1, rt: at(5)},
		ids(2): {seq: 2, rt: at(9)},
		ids(3): {seq: 3, rt: at(1), online: true},
		ids(4): {seq: 4, rt: at(9), online: true},
		ids(5): {seq: 5, rt: at(9), online: true},
		ids(6): {seq: 6, rt: at(7)},
	}}

	logs := positionsOf(s, nil)

	want := []byte{4, 5, 3, 2, 6, 1}
	if logs == nil || len(logs.Journal) != len(want) {
		t.Fatalf("positionsOf() = %+v, want %d journal positions", logs, len(want))
	}
	for i, n := range want {
		if got := logs.Journal[i].SeqnumID; got != formatUUID(ids(n)) {
			t.Errorf("position %d = %s, want the ID %d (online first, then the newest, then the lower ID)", i, got, n)
		}
	}
}

func TestPositionsOf_KeepsAtMostTheBoundOfJournalPositionsAndNothingWithoutAny(t *testing.T) {
	s := &state{journal: map[[16]byte]*jpos{}}
	if got := positionsOf(s, nil); got != nil {
		t.Errorf("positionsOf(empty) = %+v, want nil", got)
	}
	for i := range agentstate.MaxLogJournals + 20 {
		s.journal[[16]byte{14: byte(i >> 8), 15: byte(i)}] = &jpos{seq: uint64(i), rt: time.Date(2026, time.October, 23, 6, 0, i, 0, time.UTC)}
	}

	logs := positionsOf(s, nil)

	if logs == nil || len(logs.Journal) != agentstate.MaxLogJournals {
		t.Fatalf("positionsOf() holds %v positions, want %d", logs, agentstate.MaxLogJournals)
	}
	if last := logs.Journal[len(logs.Journal)-1]; last.Seqnum != 20 {
		t.Errorf("the last saved position has the sequence number %d, want 20 (the 20 oldest are left out)", last.Seqnum)
	}
}

func TestTailTime_PrefersTheTailThenTheHeadThenTheDefault(t *testing.T) {
	head, tail, def := time.Date(2026, time.October, 23, 6, 0, 0, 0, time.UTC), time.Date(2026, time.October, 23, 6, 5, 0, 0, time.UTC), time.Date(2026, time.October, 23, 7, 0, 0, 0, time.UTC)
	tests := []struct {
		name string
		h    journal.Header
		want time.Time
	}{
		{"both times", journal.Header{HeadRealtime: head, TailRealtime: tail}, tail},
		{"the head only", journal.Header{HeadRealtime: head}, head},
		{"neither", journal.Header{}, def},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := tailTime(tt.h, def); !got.Equal(tt.want) {
				t.Errorf("tailTime() = %v, want %v", got, tt.want)
			}
		})
	}
}
