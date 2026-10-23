package logship_test

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/journal/journaltest"
	"github.com/LarsLaskowski/Vandox/internal/model"
)

// goldenRecord is a line of testdata/journal/live-records.jsonl: the record the journal mapping of the import makes of an entry of
// testdata/journal/live.export.
type goldenRecord struct {
	CapturedAtUS int64  `json:"captured_at_us"`
	Log          string `json:"log"`
	Source       string `json:"source"`
	Host         string `json:"host"`
	Program      string `json:"program"`
	PID          int32  `json:"pid"`
	Priority     *uint8 `json:"priority"`
	Message      string `json:"message"`
	Truncated    bool   `json:"truncated"`
}

func journalFixture(name string) string {
	return filepath.Join("..", "..", "testdata", "journal", name)
}

func readGolden(t *testing.T) []goldenRecord {
	t.Helper()
	data, err := os.ReadFile(journalFixture("live-records.jsonl"))
	if err != nil {
		t.Fatalf("reading live-records.jsonl: %v", err)
	}
	var out []goldenRecord
	for _, line := range bytes.Split(bytes.TrimSpace(data), []byte("\n")) {
		var g goldenRecord
		if err := json.Unmarshal(line, &g); err != nil {
			t.Fatalf("live-records.jsonl line %q: %v", line, err)
		}
		out = append(out, g)
	}
	return out
}

func TestJournal_EntriesMapAsTheImportGolden(t *testing.T) {
	f, err := os.Open(journalFixture("live.export"))
	if err != nil {
		t.Fatalf("opening live.export: %v", err)
	}
	defer func() { _ = f.Close() }()
	export, err := journaltest.ReadExport(f)
	if err != nil {
		t.Fatalf("ReadExport(live.export) error = %v, want nil", err)
	}
	golden := readGolden(t)
	if len(export) != 21 || len(golden) != 21 {
		t.Fatalf("live.export holds %d entries and live-records.jsonl %d records, want 21 each", len(export), len(golden))
	}
	w := newWorld(t)
	id := seqID(0x0f)
	w.writeJournal(journalSpec{name: "system@0000000000000000000000000000000f-00000000000003e8-0005f8d1b1a2c3d4.journal", seqnumID: id, entries: export[:10], compress: true})
	w.writeJournal(journalSpec{name: "system.journal", seqnumID: id, online: true, entries: export[10:], compress: true})
	w.cfg.MinPriority = 7
	w.positionAt(id, 999, time.UnixMicro(golden[0].CapturedAtUS-1_000_000).UTC())
	w.start()

	recs := w.drain(5)

	got := logLines(recs)
	if len(got) != len(golden) || len(recs) != len(golden) {
		t.Fatalf("the collector returned %d records with %d log lines, want the %d log lines of live-records.jsonl", len(recs), len(got), len(golden))
	}
	for i, want := range golden {
		requireGolden(t, i, recs[i], want)
	}
}

// requireGolden checks a record against the line of live-records.jsonl.
func requireGolden(t *testing.T, i int, rec model.Record, want goldenRecord) {
	t.Helper()
	l, ok := rec.Data.(*model.LogLine)
	if !ok {
		t.Errorf("record %d is a %T, want a log line", i, rec.Data)
		return
	}
	same := l.Log == want.Log && l.Host == want.Host && l.Program == want.Program && l.PID == want.PID && l.Message == want.Message &&
		l.Truncated == want.Truncated && l.Event == "" && (l.Priority == nil) == (want.Priority == nil) &&
		(want.Priority == nil || *l.Priority == *want.Priority)
	if !same {
		t.Errorf("record %d = log %q host %q program %q pid %d priority %v truncated %v message %.40q (%d bytes), want %+v", i, l.Log, l.Host, l.Program, l.PID,
			l.Priority, l.Truncated, l.Message, len(l.Message), want)
	}
	if rec.Source != want.Source || !rec.CapturedAt.Equal(time.UnixMicro(want.CapturedAtUS)) || rec.CapturedAt.Location() != time.UTC {
		t.Errorf("record %d has the source %q and was captured at %v, want %q at %v in UTC", i, rec.Source, rec.CapturedAt, want.Source, time.UnixMicro(want.CapturedAtUS).UTC())
	}
}

func TestJournal_ReadsUserFilesAndTheVolatileDirectory(t *testing.T) {
	w := newWorld(t)
	empty := filepath.Join(w.root, "persistent")
	if err := os.MkdirAll(empty, 0o700); err != nil {
		t.Fatalf("MkdirAll() error = %v, want nil", err)
	}
	w.journals = []fs.FS{os.DirFS(empty), os.DirFS(w.jroot)} // the persistent directory has no machine directory
	system, user, archivedUser := seqID(1), seqID(2), seqID(3)
	w.online(system, jentries(1, 4))
	w.writeJournal(journalSpec{name: "user-1000.journal", seqnumID: user, online: true, entries: []map[string][]byte{
		jentry(101, "user 101"), jentry(102, "user 102"), jentry(103, "user 103")}})
	w.writeJournal(journalSpec{name: "user-1001@0000000000000000000000000000000-0000000000000001-0000000000000001.journal", seqnumID: archivedUser,
		entries: []map[string][]byte{jentry(201, "archived user 201")}})
	w.position(system, 2)
	w.position(user, 100)
	w.position(archivedUser, 200)
	w.start()

	got := messages(w.drain(5))

	for _, line := range []string{"line 3", "line 4", "user 101", "user 102", "user 103", "archived user 201"} {
		if !slices.Contains(got, line) {
			t.Errorf("the collector returned %v, want it to include %q", head(got), line)
		}
	}
	if len(got) != 6 {
		t.Errorf("the collector returned %d lines %v, want 6 (the system file, the user file and the archived user file of the volatile directory)", len(got), head(got))
	}
}

func TestJournal_PrefersThePersistentDirectory(t *testing.T) {
	w := newWorld(t)
	volatile := filepath.Join(w.root, "volatile")
	w.writeJournalIn(volatile, journalSpec{name: "system.journal", seqnumID: seqID(1), online: true, entries: []map[string][]byte{jentry(1, "volatile 1"), jentry(2, "volatile 2")}})
	w.online(seqID(2), []map[string][]byte{jentry(10, "persistent 10"), jentry(11, "persistent 11")})
	w.journals = []fs.FS{os.DirFS(w.jroot), os.DirFS(volatile)}
	w.position(seqID(1), 0)
	w.position(seqID(2), 9)
	w.start()

	got := messages(w.drain(5))

	if !slices.Equal(got, []string{"persistent 10", "persistent 11"}) {
		t.Errorf("the collector returned %v, want only the lines of the persistent directory", head(got))
	}
}

func TestJournal_CutsLongFieldsWithTruncated(t *testing.T) {
	euro := string(rune(0x20ac)) // three bytes
	replacement := string(rune(0xfffd))
	if len(euro) != 3 || len(replacement) != 3 {
		t.Fatalf("the test characters have %d and %d bytes, want 3 each", len(euro), len(replacement))
	}
	w := newWorld(t)
	w.online(seqID(1), []map[string][]byte{
		jentry(1, strings.Repeat("m", 20000), "_HOSTNAME", strings.Repeat("h", 1500), "SYSLOG_IDENTIFIER", strings.Repeat("p", 1200)),
		jentry(2, "short", "_HOSTNAME", strings.Repeat(euro, 400)),
		jentry(3, "aa"+strings.Repeat(euro, 7000)),
		jentry(4, string(bytes.Repeat([]byte{0xff}, 6000))),
		jentry(5, strings.Repeat("m", 16384), "_HOSTNAME", strings.Repeat("h", 1024), "SYSLOG_IDENTIFIER", strings.Repeat("p", 1024)),
		jentry(6, "ok"),
	})
	w.position(seqID(1), 0)
	w.start()

	got := logLines(w.drain(5))

	if len(got) != 6 {
		t.Fatalf("the collector returned %d records, want 6", len(got))
	}
	if l := got[0]; l.Message != strings.Repeat("m", 16384) || l.Host != strings.Repeat("h", 1024) || l.Program != strings.Repeat("p", 1024) || !l.Truncated {
		t.Errorf("an entry with a message of 20,000 bytes, a host of 1,500 and a program of 1,200 = message %d host %d program %d truncated %v, want 16,384, 1,024 and 1,024 bytes (the first bytes of each) and truncated",
			len(l.Message), len(l.Host), len(l.Program), l.Truncated)
	}
	if h := got[1].Host; len(h) != 1023 || !utf8.ValidString(h) || h != strings.Repeat(euro, 341) || !got[1].Truncated {
		t.Errorf("a host of 400 three-byte characters = %d bytes valid %v truncated %v, want 1,023 bytes (341 characters, cut at a character boundary) and truncated", len(h), utf8.ValidString(h), got[1].Truncated)
	}
	if m := got[2].Message; len(m) != 16382 || !utf8.ValidString(m) || !got[2].Truncated {
		t.Errorf("a message of 21,002 bytes = %d bytes valid %v truncated %v, want 16,382 bytes (the last whole character below 16,384) and truncated", len(m), utf8.ValidString(m), got[2].Truncated)
	}
	if m := got[3].Message; m != strings.Repeat(replacement, 5461) || !got[3].Truncated {
		t.Errorf("a message of 6,000 invalid bytes = %d bytes truncated %v, want 5,461 replacement characters (16,383 bytes) and truncated", len(m), got[3].Truncated)
	}
	if l := got[4]; len(l.Message) != 16384 || len(l.Host) != 1024 || len(l.Program) != 1024 || l.Truncated {
		t.Errorf("fields at their limits = message %d host %d program %d truncated %v, want 16,384, 1,024, 1,024 and not truncated", len(l.Message), len(l.Host), len(l.Program), l.Truncated)
	}
	if l := got[5]; l.Message != "ok" || l.Truncated {
		t.Errorf("a short entry = %q truncated %v, want ok and not truncated", l.Message, l.Truncated)
	}
}

// frameByte names a byte of a stored zstd frame or of the data object that holds it.
type frameByte int

const (
	objectFlags     frameByte = iota // the flags of the data object: 1 XZ, 2 LZ4, 4 ZSTD
	frameMagic                       // the first byte of the zstd magic number
	frameDescriptor                  // the frame header descriptor
)

// patchFrame changes one byte of the stored frame of the compressed message in the journal file name of the world. The writer stores a
// payload of 512 bytes and more as a data object (64-byte header) that holds a zstd frame of one raw block (10-byte frame header
// in front of the text).
func patchFrame(t *testing.T, w *world, file, message string, at frameByte, value byte) {
	t.Helper()
	path := filepath.Join(w.jroot, machineID, file)
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("reading %s: %v", file, err)
	}
	const frameHeader, objectHeader = 10, 64
	text := bytes.Index(data, []byte("MESSAGE="+message))
	if text < 0 {
		t.Fatalf("the journal file holds no stored message %.20q", message)
	}
	frame := text - frameHeader
	object := frame - objectHeader
	if data[object] != 1 || data[object+1] != 4 || !bytes.Equal(data[frame:frame+4], []byte{0x28, 0xb5, 0x2f, 0xfd}) {
		t.Fatalf("the bytes in front of the message are no data object with a zstd frame: type %d flags %d magic % x", data[object], data[object+1], data[frame:frame+4])
	}
	switch at {
	case objectFlags:
		data[object+1] = value
	case frameMagic:
		data[frame] = value
	case frameDescriptor:
		data[frame+4] = value
	}
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatalf("writing %s: %v", file, err)
	}
}

// longMessage returns a message of 700 bytes that journald stores compressed.
func longMessage(n uint64) string { return fmt.Sprintf("%03d", n) + strings.Repeat("z", 697) }

// brokenJournal writes the online journal with the entries 1 to 5, whose entries in broken have a message that is stored compressed,
// and applies patch to each of them; the world is positioned before the first entry.
func brokenJournal(t *testing.T, broken []uint64, patch func(w *world, message string)) *world {
	t.Helper()
	w := newWorld(t)
	var entries []map[string][]byte
	for seq := uint64(1); seq <= 5; seq++ {
		message := fmt.Sprintf("line %d", seq)
		if slices.Contains(broken, seq) {
			message = longMessage(seq)
		}
		entries = append(entries, jentry(seq, message))
	}
	w.online(seqID(1), entries)
	for _, seq := range broken {
		patch(w, longMessage(seq))
	}
	w.position(seqID(1), 0)
	w.start()
	return w
}

func requireOneGapOver(t *testing.T, recs []model.Record, wantLines []string, from, to time.Time) {
	t.Helper()
	if got := messages(recs); !slices.Equal(got, wantLines) {
		t.Errorf("lines = %v, want %v", head(got), wantLines)
	}
	gaps := gapsOf(recs)
	if len(gaps) != 1 {
		t.Fatalf("the collector returned %d gaps %v, want 1", len(gaps), gaps)
	}
	requireGap(t, gaps[0], from, to)
}

func TestJournal_XzOrLz4ItemIsAGap(t *testing.T) {
	for name, flag := range map[string]byte{"XZ": 1, "LZ4": 2} {
		t.Run(name+" item", func(t *testing.T) {
			w := brokenJournal(t, []uint64{3}, func(w *world, message string) { patchFrame(t, w, "system.journal", message, objectFlags, flag) })

			recs := w.drain(5)

			requireOneGapOver(t, recs, []string{"line 1", "line 2", "line 4", "line 5"}, entryTime(2), entryTime(4))
		})
	}
	t.Run("several in a row give one gap", func(t *testing.T) {
		w := brokenJournal(t, []uint64{2, 3}, func(w *world, message string) { patchFrame(t, w, "system.journal", message, objectFlags, 1) })

		recs := w.drain(5)

		requireOneGapOver(t, recs, []string{"line 1", "line 4", "line 5"}, entryTime(1), entryTime(4))
	})
}

func TestJournal_UnreadableEntriesGiveOneGap(t *testing.T) {
	tests := []struct {
		name  string
		at    frameByte
		value byte
	}{
		{"a frame without the zstd magic", frameMagic, 0x00},
		{"a frame that is not single-segment", frameDescriptor, 0x00},
		{"a frame with a dictionary", frameDescriptor, 0x61},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			w := brokenJournal(t, []uint64{3}, func(w *world, message string) { patchFrame(t, w, "system.journal", message, tt.at, tt.value) })

			recs := w.drain(5)

			requireOneGapOver(t, recs, []string{"line 1", "line 2", "line 4", "line 5"}, entryTime(2), entryTime(4))
		})
	}
	t.Run("the position moves over the entry", func(t *testing.T) {
		w := brokenJournal(t, []uint64{5}, func(w *world, message string) { patchFrame(t, w, "system.journal", message, frameMagic, 0x00) })
		first := w.drain(5)
		w.online(seqID(1), jentries(1, 6))

		second := w.drain(5)

		if got := messages(second); !slices.Equal(got, []string{"line 6"}) {
			t.Errorf("lines after the unreadable last entry = %v, want only line 6 (nothing read again)", got)
		}
		if len(gapsOf(first)) != 1 || len(gapsOf(second)) != 0 {
			t.Errorf("gaps = %d before and %d after the new entry, want 1 and 0", len(gapsOf(first)), len(gapsOf(second)))
		}
	})
}

func TestJournal_EntryWithoutMessageIsSkipped(t *testing.T) {
	w := newWorld(t)
	w.online(seqID(1), []map[string][]byte{
		jentry(1, "line 1"),
		jentry(2, "", "MESSAGE", ""),
		jentry(3, "zero time", "__REALTIME_TIMESTAMP", "0"),
		jentry(4, "beyond the storable range", "__REALTIME_TIMESTAMP", "9223372036854776"),
		jentry(5, "year 10000", "__REALTIME_TIMESTAMP", "253402300800000000"),
		jentry(6, "line 6"),
	})
	w.position(seqID(1), 0)
	w.start()

	recs := w.drain(5)

	if got := messages(recs); !slices.Equal(got, []string{"line 1", "line 6"}) {
		t.Errorf("lines = %v, want only line 1 and line 6: an entry without MESSAGE or with a time outside the storable range is skipped", got)
	}
	requireNoGaps(t, "the runs", recs)
	if again := w.step(); len(again) != 0 {
		t.Errorf("the next run returned %d records, want none (the skipped entries are not read again)", len(again))
	}
}

func TestJournal_VacuumedPositionIsOneGap(t *testing.T) {
	t.Run("entries after the position are gone", func(t *testing.T) {
		w := newWorld(t)
		w.online(seqID(1), jentries(20, 23))
		w.position(seqID(1), 10)
		w.start()

		recs := w.drain(5)

		requireOneGapOver(t, recs, []string{"line 20", "line 21", "line 22", "line 23"}, entryTime(10), entryTime(20))
		if more := w.step(); len(more) != 0 {
			t.Errorf("the next run returned %d records, want none (one gap per position)", len(more))
		}
	})

	t.Run("the first remaining entry directly follows the position", func(t *testing.T) {
		w := newWorld(t)
		w.online(seqID(1), jentries(11, 13))
		w.position(seqID(1), 10)
		w.start()

		recs := w.drain(5)

		requireMessages(t, "lines", messages(recs), []string{"line 11", "line 12", "line 13"})
		requireNoGaps(t, "the runs", recs)
	})
}

func TestJournal_UnreadableJournalKeepsThePosition(t *testing.T) {
	const notReadable, readableAgain = "journal not readable", "journal readable again"
	w := newWorld(t)
	w.online(seqID(1), jentries(1, 3))
	w.position(seqID(1), 3)
	w.start()

	w.etc.remove("machine-id")
	w.online(seqID(1), jentries(1, 5))
	var seen []model.Record
	for range 3 {
		seen = append(seen, w.step()...)
	}

	if len(seen) != 0 {
		t.Errorf("runs on an unreadable journal returned %d records, want none and no gap", len(seen))
	}
	if n := len(w.logsOf(notReadable)); n != 1 {
		t.Errorf("%q was logged %d times in three runs, want once per change", notReadable, n)
	}
	w.etc.set("machine-id", []byte(machineID+"\n"))

	recs := w.drain(5)

	requireMessages(t, "lines after the journal is readable again", messages(recs), []string{"line 4", "line 5"})
	requireNoGaps(t, "the runs after the journal is readable again", recs)
	if n := len(w.logsOf(readableAgain)); n != 1 {
		t.Errorf("%q was logged %d times, want once", readableAgain, n)
	}
	if pos := w.savedJournal(seqID(1)); pos == nil || pos.Seqnum != 5 {
		t.Errorf("journal position = %+v, want the sequence number 5", pos)
	}
}

func TestJournal_NewSeqnumIDIsReadFromItsStart(t *testing.T) {
	w := newWorld(t)
	first, second := seqID(1), seqID(2)
	w.online(first, jentries(1, 3))
	w.start()
	w.step()
	w.online(first, jentries(1, 4))
	w.step()

	// journald starts a new sequence number ID: the online file becomes an archived file, a new online file begins
	w.archived(first, jentries(1, 5))
	w.online(second, []map[string][]byte{jentry(10, "second 10"), jentry(11, "second 11"), jentry(12, "second 12")})
	recs := w.drain(5)

	got := messages(recs)
	slices.Sort(got)
	requireMessages(t, "lines after the new ID appeared", got, []string{"line 5", "second 10", "second 11", "second 12"})
	requireNoGaps(t, "the runs", recs)
	if pos := w.savedJournal(second); pos == nil || pos.Seqnum != 12 {
		t.Errorf("position of the new ID = %+v, want the sequence number 12", pos)
	}
}

func TestJournal_IDWithoutPositionIsReadOnlyWhenNewer(t *testing.T) {
	w := newWorld(t)
	known, newer, older := seqID(1), seqID(2), seqID(3)
	w.online(known, jentries(48, 50))
	w.position(known, 50)
	w.writeJournal(journalSpec{name: "user-1000.journal", seqnumID: newer, online: true, entries: []map[string][]byte{
		jentry(100, "newer 100"), jentry(101, "newer 101"), jentry(102, "newer 102")}})
	w.writeJournal(journalSpec{name: "user-1001.journal", seqnumID: older, online: true, entries: []map[string][]byte{
		jentry(10, "older 10"), jentry(11, "older 11"), jentry(12, "older 12")}})
	w.start()

	recs := w.drain(5)

	requireMessages(t, "lines", messages(recs), []string{"newer 100", "newer 101", "newer 102"})
	requireNoGaps(t, "the runs (the older ID ends before the newest position)", recs)
	w.writeJournal(journalSpec{name: "user-1001.journal", seqnumID: older, online: true, entries: []map[string][]byte{
		jentry(10, "older 10"), jentry(11, "older 11"), jentry(12, "older 12"), jentry(13, "older 13")}})
	if got := messages(w.drain(5)); !slices.Equal(got, []string{"older 13"}) {
		t.Errorf("lines after a new entry of the older ID = %v, want only older 13 (the ID continues at its end)", got)
	}
}

func TestJournal_SkippedNewerPartOfAnIDIsAGap(t *testing.T) {
	w := newWorld(t)
	known, stepped := seqID(1), seqID(2)
	w.online(known, jentries(48, 50))
	w.position(known, 50)
	// a user journal whose first entry is older than the newest position and whose tail is newer
	w.writeJournal(journalSpec{name: "user-1000.journal", seqnumID: stepped, online: true, entries: []map[string][]byte{
		jentry(40, "stepped 40"), jentry(45, "stepped 45"), jentry(60, "stepped 60")}})
	w.start()

	first := w.drain(5)

	requireOneGapOver(t, first, nil, entryTime(50), entryTime(60))
	w.writeJournal(journalSpec{name: "user-1000.journal", seqnumID: stepped, online: true, entries: []map[string][]byte{
		jentry(40, "stepped 40"), jentry(45, "stepped 45"), jentry(60, "stepped 60"), jentry(61, "stepped 61")}})
	second := w.drain(5)
	requireMessages(t, "lines after the gap", messages(second), []string{"stepped 61"})
	requireNoGaps(t, "the runs after the gap", second)
}

func TestJournal_SetAsideFileOfAForgottenIDIsNotReadAgain(t *testing.T) {
	w := newWorld(t)
	forgotten, current := seqID(1), seqID(2)
	w.archived(forgotten, jentries(1, 5))
	w.online(current, jentries(10, 12))
	w.start()
	w.step()
	w.online(current, jentries(10, 14))
	if got := messages(w.step()); !slices.Equal(got, []string{"line 13", "line 14"}) {
		t.Fatalf("lines of the first run after the start = %v, want line 13 and line 14", got)
	}
	// the files of an ID that is no longer written to are set aside after a crash of journald
	w.writeJournal(journalSpec{name: "system@00000000000000000000000000000001-0000000000000001-0005f8d1b1a2c3d5.journal~", seqnumID: forgotten, entries: jentries(1, 5)})

	w.restart()
	recs := w.drain(5)

	if len(recs) != 0 {
		t.Errorf("the collector returned %v and %d gaps after the restart, want nothing: the files of the forgotten ID are older than the newest position", head(messages(recs)), len(gapsOf(recs)))
	}
	w.online(current, jentries(10, 15))
	requireMessages(t, "lines after a new entry", messages(w.drain(5)), []string{"line 15"})
}

// idOf returns the sequence number ID of user number n.
func idOf(n int) [16]byte {
	var id [16]byte
	id[14], id[15] = byte(n>>8), byte(n) // #nosec G115 -- n is below 65536
	return id
}

// userJournals writes the user journals 1000 to 1000+users-1, each with its own sequence number ID and the entries of the rounds 1 to
// round; the entry of round r of user i has the sequence number r*1000+i and the message "round<r> user<i>".
func userJournals(w *world, users, round int) {
	w.t.Helper()
	for i := range users {
		var entries []map[string][]byte
		for r := 1; r <= round; r++ {
			entries = append(entries, jentry(uint64(r*1000+i), fmt.Sprintf("round%d user%d", r, i))) // #nosec G115 -- small positive numbers
		}
		w.writeJournal(journalSpec{name: fmt.Sprintf("user-%d.journal", 1000+i), seqnumID: idOf(i + 1), online: true, entries: entries})
	}
}

// requireShippedOrCovered checks that the entry of round 3 of each of the users is a shipped message or lies inside a gap.
func requireShippedOrCovered(t *testing.T, users int, recs []model.Record) {
	t.Helper()
	shipped := map[string]bool{}
	for _, m := range messages(recs) {
		shipped[m] = true
	}
	gaps := gapsOf(recs)
	for i := range users {
		if shipped[fmt.Sprintf("round3 user%d", i)] {
			continue
		}
		at := entryTime(uint64(3000 + i)) // #nosec G115 -- small positive numbers
		covered := false
		for _, g := range gaps {
			covered = covered || (!g.From.After(at) && !g.To.Before(at))
		}
		if !covered {
			t.Errorf("the entry of user %d written while the agent was stopped is neither shipped nor inside one of the %d gaps", i, len(gaps))
		}
	}
}

func TestJournal_ManyUserIDsAreNeverReadTwice(t *testing.T) {
	const users = 300
	w := newWorld(t)
	userJournals(w, users, 1)
	w.start()
	if first := w.step(); len(first) != 0 {
		t.Fatalf("the first run returned %d records, want none (a first start begins at the ends)", len(first))
	}
	userJournals(w, users, 2)
	second := w.drain(5)
	if len(logLines(second)) != users {
		t.Fatalf("the collector returned %d lines for one new entry in each of %d user journals, want %d", len(logLines(second)), users, users)
	}
	w.stop()
	if saved := w.store.State().Logs; saved == nil || len(saved.Journal) > agentstate.MaxLogJournals || len(saved.Journal) == 0 {
		t.Fatalf("saved journal positions = %+v, want between 1 and %d", saved, agentstate.MaxLogJournals)
	}

	userJournals(w, users, 3)
	w.restart()
	after := w.drain(5)

	for _, m := range messages(after) {
		if strings.HasPrefix(m, "round1") || strings.HasPrefix(m, "round2") {
			t.Errorf("the collector shipped %q after the restart, want no line read twice", m)
		}
	}
	requireShippedOrCovered(t, users, after)
}

func TestJournal_TurnedOnLaterStartsAtTheTails(t *testing.T) {
	w := newWorld(t)
	w.cfg.Journal = false
	w.online(seqID(1), jentries(1, 5))
	w.start()
	for range 2 {
		if recs := w.step(); len(recs) != 0 {
			t.Fatalf("a run with the journal turned off returned %d records, want none", len(recs))
		}
	}
	w.online(seqID(1), jentries(1, 8))
	w.step()

	w.cfg.Journal = true
	w.restart()
	recs := w.drain(5)

	if len(recs) != 0 {
		t.Errorf("the first runs with the journal turned on returned %v and %d gaps, want nothing: the journal starts at its end", head(messages(recs)), len(gapsOf(recs)))
	}
	w.online(seqID(1), jentries(1, 9))
	requireMessages(t, "lines written after the start", messages(w.drain(5)), []string{"line 9"})
}

func TestJournal_ReadableAfterAFirstStartStartsAtTheTails(t *testing.T) {
	w := newWorld(t)
	w.etc.remove("machine-id")
	w.online(seqID(1), jentries(1, 5))
	w.start()
	for range 2 {
		w.step()
	}

	w.etc.set("machine-id", []byte(machineID+"\n"))
	recs := w.drain(5)

	if len(recs) != 0 {
		t.Errorf("the first runs on a journal that became readable returned %v and %d gaps, want nothing: it starts at its end", head(messages(recs)), len(gapsOf(recs)))
	}
	w.online(seqID(1), jentries(1, 6))
	requireMessages(t, "lines written after it became readable", messages(w.drain(5)), []string{"line 6"})
}

// entryOffset returns the offset of the entry object with the sequence number seq in the journal file bytes.
func entryOffset(t *testing.T, data []byte, seq uint64) int {
	t.Helper()
	for off := 256; off+24 <= len(data); off += 8 {
		size := binary.LittleEndian.Uint64(data[off+8:])
		if data[off] == 3 && size >= 64 && size < 1<<20 && binary.LittleEndian.Uint64(data[off+16:]) == seq {
			return off
		}
	}
	t.Fatalf("the journal file holds no entry object with the sequence number %d", seq)
	return 0
}

// damageJournal changes one byte of the journal file in the world: the type of the entry object seq (when item is empty) or the
// type of the data object of the item payload.
func damageJournal(t *testing.T, w *world, file string, seq uint64, item string) {
	t.Helper()
	path := filepath.Join(w.jroot, machineID, file)
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("reading %s: %v", file, err)
	}
	if item == "" {
		data[entryOffset(t, data, seq)] = 0
	} else {
		at := bytes.Index(data, []byte(item))
		if at < 64 {
			t.Fatalf("the journal file holds no stored item %q", item)
		}
		data[at-64] = 0 // the type of the data object in front of the payload
	}
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatalf("writing %s: %v", file, err)
	}
}

func TestJournal_DamagedFileIsGivenUpWithOneGap(t *testing.T) {
	t.Run("an archived file with a damaged entry object in the middle", givenUpArchivedFile)
	t.Run("an online file", givenUpOnlineFile)
	t.Run("a damaged item object is one gap over its entry only", damagedItemObject)
}

func givenUpArchivedFile(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	name := w.archived(id, jentries(1, 6))
	w.online(id, jentries(7, 9))
	damageJournal(t, w, name, 4, "")
	w.position(id, 0)
	w.start()

	recs := w.drain(5)

	requireMessages(t, "lines", messages(recs), []string{"line 1", "line 2", "line 3", "line 7", "line 8", "line 9"})
	requireGap(t, oneGap(t, "the runs", recs), entryTime(3), entryTime(6))
	logs := w.logsOf("journal not readable")
	if len(logs) == 0 || logs[0].attrs["err"] != "journal: archived: malformed file" {
		t.Errorf("warnings %q = %+v, want one with err %q", "journal not readable", logs, "journal: archived: malformed file")
	}
}

func givenUpOnlineFile(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	w.online(id, jentries(1, 6))
	damageJournal(t, w, "system.journal", 4, "")
	w.position(id, 0)
	w.start()

	first, second, third := w.step(), w.step(), w.step()

	requireMessages(t, "lines of the first run", messages(first), []string{"line 1", "line 2", "line 3"})
	requireNoGaps(t, "the first run", first)
	if len(second) != 0 {
		t.Errorf("the second run returned %v and %d gaps, want nothing: the online file may only have been read while it was written", head(messages(second)), len(gapsOf(second)))
	}
	requireMessages(t, "lines of the third run", messages(third), nil)
	requireGap(t, oneGap(t, "the third run", third), entryTime(3), entryTime(6))

	// journald rotates: the damaged file is set aside and the ID goes on in a new online file
	w.rename("system.journal", "system@00000000000000000000000000000001-0000000000000001-0005f8d1b1a2c3d4.journal")
	w.online(id, jentries(7, 9))
	next := w.drain(5)
	requireMessages(t, "lines of the next online file", messages(next), []string{"line 7", "line 8", "line 9"})
	requireNoGaps(t, "the runs after the give-up", next)
}

func damagedItemObject(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	entries := jentries(1, 5)
	entries[2] = jentry(3, "line 3", "_HOSTNAME", "damaged-host")
	w.online(id, entries)
	damageJournal(t, w, "system.journal", 0, "_HOSTNAME=damaged-host")
	w.position(id, 0)
	w.start()

	recs := w.drain(5)

	requireMessages(t, "lines", messages(recs), []string{"line 1", "line 2", "line 4", "line 5"})
	requireGap(t, oneGap(t, "the runs", recs), entryTime(2), entryTime(4))
}

// pidCase is an entry with the given _PID (nil: absent) and SYSLOG_PID (nil: absent) and the pid its record must have.
type pidCase struct {
	name   string
	pid    *string
	syslog *string
	want   int32
}

func pidCases() []pidCase {
	str := func(s string) *string { return &s }
	forms := []struct {
		name string
		pid  *string
	}{{"0", str("0")}, {"2147483648", str("2147483648")}, {"empty", str("")}, {"a non-digit", str("12a")}, {"11 digits", str("12345678901")}, {"absent", nil}}
	var cases []pidCase
	for _, f := range forms {
		cases = append(cases,
			pidCase{"_PID " + f.name + " with a valid SYSLOG_PID", f.pid, str("42"), 42},
			pidCase{"_PID " + f.name + " with an invalid SYSLOG_PID", f.pid, str("0"), 0},
		)
	}
	return append(cases, pidCase{"the largest _PID is kept", str("2147483647"), str("42"), 2147483647})
}

func TestJournal_PidFallsBackToSyslogPid(t *testing.T) {
	tests := pidCases()
	w := newWorld(t)
	var entries []map[string][]byte
	for i, tt := range tests {
		e := jentry(uint64(i+1), tt.name) // #nosec G115 -- a small index
		delete(e, "_PID")
		if tt.pid != nil {
			e["_PID"] = []byte(*tt.pid)
		}
		if tt.syslog != nil {
			e["SYSLOG_PID"] = []byte(*tt.syslog)
		}
		entries = append(entries, e)
	}
	w.online(seqID(1), entries)
	w.position(seqID(1), 0)
	w.start()

	got := logLines(w.drain(5))

	if len(got) != len(tests) {
		t.Fatalf("the collector returned %d records, want %d", len(got), len(tests))
	}
	for i, tt := range tests {
		if got[i].Message != tt.name || got[i].PID != tt.want {
			t.Errorf("entry %q has the pid %d, want %d", tt.name, got[i].PID, tt.want)
		}
	}
}
