package logship_test

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"log/slog"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"slices"
	"strings"
	"testing"
	"testing/fstest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/logship"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// ofSource returns the messages of the log lines of source ("journal" or "syslog") among the records.
func ofSource(recs []model.Record, source string) []string {
	var out []string
	for _, r := range recs {
		if l, ok := r.Data.(*model.LogLine); ok && r.Source == source {
			out = append(out, l.Message)
		}
	}
	return out
}

func requireMessages(t *testing.T, what string, got, want []string) {
	t.Helper()
	if !slices.Equal(got, want) {
		t.Errorf("%s = %v, want %v", what, head(got), head(want))
	}
}

func requireNoGaps(t *testing.T, what string, recs []model.Record) {
	t.Helper()
	if gaps := gapsOf(recs); len(gaps) != 0 {
		t.Errorf("%s holds %d gaps %v, want none", what, len(gaps), gaps)
	}
}

func TestLogs_FirstStartShipsNothingOlder(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.online(seqID(1), jentries(1, 5))
	w.writeLog("mail.log", fileLines(1, 3))
	w.start()

	first := w.step()

	if len(first) != 0 {
		t.Errorf("the first run returned %d records %v, want none: the lines written before the start are not shipped", len(first), head(messages(first)))
	}
	w.online(seqID(1), jentries(1, 7))
	w.appendLog("mail.log", fileLines(4, 5))
	second := w.step()
	requireMessages(t, "journal lines of the second run", ofSource(second, "journal"), []string{"line 6", "line 7"})
	requireMessages(t, "file lines of the second run", ofSource(second, "syslog"), []string{"file 4", "file 5"})
	requireNoGaps(t, "the second run", second)
}

func TestLogs_StateWithoutLogPositionsStartsAtTheEndsAndKeepsTheRest(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.online(seqID(1), jentries(1, 3))
	w.writeLog("mail.log", fileLines(1, 2))
	booted := agentstate.Boot{ID: "0f8fad5b-d9cb-469f-a165-70867728950e", Uptime: time.Hour, BootedAt: startTime.Add(-time.Hour)}
	kernel := agentstate.JournalPosition{SeqnumID: uuid(seqID(9)), Seqnum: 42, Realtime: entryTime(42)}
	backup := agentstate.OpenRun{BootID: booted.ID, StartedAt: startTime.Add(-time.Minute), LastSeen: startTime.Add(-time.Second), MaxProcesses: 3}
	w.store.Update(true, func(s *agentstate.State) { s.Boot, s.Journal, s.Backup = &booted, &kernel, &backup })
	w.stop()
	w.reopen()
	w.start()

	first := w.step()
	w.online(seqID(1), jentries(1, 4))
	w.appendLog("mail.log", fileLines(3, 3))
	second := w.step()

	if len(first) != 0 {
		t.Errorf("the first run returned %d records %v, want none: a state file without log positions starts at the ends", len(first), head(messages(first)))
	}
	requireMessages(t, "journal lines of the second run", ofSource(second, "journal"), []string{"line 4"})
	requireMessages(t, "file lines of the second run", ofSource(second, "syslog"), []string{"file 3"})
	st := w.store.State()
	if st.Boot == nil || *st.Boot != booted || st.Journal == nil || *st.Journal != kernel || st.Backup == nil || *st.Backup != backup {
		t.Errorf("State() = boot %+v journal %+v backup %+v, want the boot, the kernel position and the open backup run kept beside the log positions", st.Boot, st.Journal, st.Backup)
	}
	if st.Logs == nil || len(st.Logs.Journal) != 1 || len(st.Logs.Files) != 1 {
		t.Errorf("State().Logs = %+v, want one journal position and one file position after a kept run", st.Logs)
	}
}

func TestLogs_StopAndStartShipEveryLineOnce(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.online(seqID(1), jentries(1, 3))
	w.writeLog("mail.log", fileLines(1, 2))
	w.start()
	var all []model.Record
	all = append(all, w.step()...)

	// lines before the stop
	w.online(seqID(1), jentries(1, 6))
	w.appendLog("mail.log", fileLines(3, 4))
	all = append(all, w.step()...)
	w.online(seqID(1), jentries(1, 8))
	w.appendLog("mail.log", fileLines(5, 5))
	all = append(all, w.step()...)
	w.stop()

	// lines written while the agent is stopped
	w.online(seqID(1), jentries(1, 10))
	w.appendLog("mail.log", fileLines(6, 7))
	w.reopen()
	w.start()

	// lines after the start
	all = append(all, w.step()...)
	w.online(seqID(1), jentries(1, 12))
	w.appendLog("mail.log", fileLines(8, 8))
	all = append(all, w.step()...)
	all = append(all, w.step()...)

	requireMessages(t, "journal lines", ofSource(all, "journal"), numbered("line", 4, 12))
	requireMessages(t, "file lines", ofSource(all, "syslog"), numbered("file", 3, 8))
	requireNoGaps(t, "all runs", all)
}

func TestLogs_UncleanEndRepeatsOnlyLinesAfterTheSavedPosition(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.online(seqID(1), jentries(1, 2))
	w.writeLog("mail.log", fileLines(1, 1))
	w.start()
	w.step()
	w.online(seqID(1), jentries(1, 4))
	w.appendLog("mail.log", fileLines(2, 3))
	before := w.step()
	requireMessages(t, "journal lines before the save", ofSource(before, "journal"), []string{"line 3", "line 4"})
	w.stop() // the state is saved up to line 4 and file line 3

	w.online(seqID(1), jentries(1, 7))
	w.appendLog("mail.log", fileLines(4, 5))
	unsaved := w.step()
	requireMessages(t, "journal lines read and kept after the save", ofSource(unsaved, "journal"), []string{"line 5", "line 6", "line 7"})
	w.online(seqID(1), jentries(1, 8))
	w.appendLog("mail.log", fileLines(6, 6))

	w.crash() // no save since: the next start knows the saved position only
	after := w.step()
	rest := w.step()

	requireMessages(t, "journal lines after the unclean end", ofSource(append(after, rest...), "journal"), numbered("line", 5, 8))
	requireMessages(t, "file lines after the unclean end", ofSource(append(after, rest...), "syslog"), numbered("file", 4, 6))
	requireNoGaps(t, "the runs after the unclean end", append(after, rest...))
}

func TestLogs_DroppedRunIsReadAgain(t *testing.T) {
	tests := []struct {
		name string
		drop func(w *world)
	}{
		{"the sink refused the records", func(w *world) { w.refuse() }},
		{"the run timed out", func(w *world) { w.timeout() }},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			w := newWorld(t)
			w.useFiles("mail.log")
			w.online(seqID(1), jentries(1, 2))
			w.writeLog("mail.log", fileLines(1, 1))
			w.start()
			w.step()
			w.online(seqID(1), jentries(1, 4))
			w.appendLog("mail.log", fileLines(2, 3))

			lost := w.collect()
			tt.drop(w)
			again := w.collect()
			w.keep()
			next := w.collect()
			w.keep()

			requireMessages(t, "journal lines of the dropped run", ofSource(lost, "journal"), []string{"line 3", "line 4"})
			requireMessages(t, "journal lines of the run after it", ofSource(again, "journal"), []string{"line 3", "line 4"})
			requireMessages(t, "file lines of the run after it", ofSource(again, "syslog"), []string{"file 2", "file 3"})
			if len(next) != 0 {
				t.Errorf("the run after the kept one returned %d records %v, want none", len(next), head(messages(next)))
			}
			requireNoGaps(t, "the runs", append(lost, again...))
		})
	}
}

func TestLogs_AbandonedRunNeverMovesAPosition(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.online(seqID(1), jentries(1, 2))
	w.writeLog("mail.log", fileLines(1, 1))
	w.start()
	w.step()
	w.online(seqID(1), jentries(1, 3))
	w.appendLog("mail.log", fileLines(2, 2))
	w.step()
	w.stop()
	saved := w.store.State().Logs
	if saved == nil || len(saved.Journal) != 1 || len(saved.Files) != 1 {
		t.Fatalf("saved log positions = %+v, want one journal position and one file position after a kept run", saved)
	}
	w.online(seqID(1), jentries(1, 6))
	w.appendLog("mail.log", fileLines(3, 4))

	abandoned := w.collect()
	w.timeout()
	again := w.collect()
	w.timeout()

	if len(messages(abandoned)) == 0 || len(messages(again)) == 0 {
		t.Fatalf("abandoned runs returned %d and %d lines, want the lines after the position each time", len(messages(abandoned)), len(messages(again)))
	}
	if got := w.store.State().Logs; !reflect.DeepEqual(got, saved) {
		t.Errorf("log positions after abandoned runs = %+v, want the saved %+v: an abandoned run never moves a position", got, saved)
	}
	w.stop()
	w.reopen()
	if got := w.store.State().Logs; !reflect.DeepEqual(got, saved) {
		t.Errorf("log positions in the state file after abandoned runs = %+v, want the saved %+v", got, saved)
	}
	w.start()
	kept := w.step()
	requireMessages(t, "journal lines after the restart", ofSource(kept, "journal"), []string{"line 4", "line 5", "line 6"})
	w.stop()
	w.reopen()
	if got := w.store.State().Logs; reflect.DeepEqual(got, saved) {
		t.Error("log positions after a kept run are still the old ones, want the kept run to move them")
	}
}

// oneGap returns the only gap among the records and fails the test when there is none or more than one.
func oneGap(t *testing.T, what string, recs []model.Record) *model.Gap {
	t.Helper()
	gaps := gapsOf(recs)
	if len(gaps) != 1 {
		t.Fatalf("%s holds %d gaps %v, want 1", what, len(gaps), gaps)
	}
	return gaps[0]
}

// droppedSpan is the lines 3 to 5, which every run in front of the give-up reads and none keeps.
var droppedSpan = []string{"line 3", "line 4", "line 5"}

// droppedWorld returns a world whose committed position is the entry 2 and whose journal then holds the entries 3 to 5.
func droppedWorld(t *testing.T) *world {
	t.Helper()
	w := newWorld(t)
	w.online(seqID(1), jentries(1, 2))
	w.start()
	w.step()
	w.online(seqID(1), jentries(1, 5))
	return w
}

func TestLogs_FourthDroppedRunGivesUpTheSpan(t *testing.T) {
	t.Run("four sink refusals", giveUpAfterRefusals)
	t.Run("four timeouts", giveUpAfterTimeouts)
	t.Run("refusals and timeouts mixed", giveUpAfterMixedLosses)
	t.Run("three dropped runs give up nothing", giveUpNothingAfterThree)
	t.Run("the run that gives up is dropped too: the next kept run carries the gap", giveUpRunDroppedToo)
	t.Run("the run that gives up is dropped too, for a file", giveUpRunOfAFileDroppedToo)
	t.Run("every run that starts from the given up span carries the gap until one is kept", giveUpGapIsCarriedByEveryRunUntilOneIsKept)
}

func giveUpGapIsCarriedByEveryRunUntilOneIsKept(t *testing.T) {
	w := droppedWorld(t)
	for range 4 {
		w.collect()
		w.refuse()
	}
	w.online(seqID(1), jentries(1, 6))

	for i, drop := range []func(){w.refuse, w.timeout, w.refuse} {
		recs := w.collect()
		gap := oneGap(t, fmt.Sprintf("dropped run %d from the given up span", i+1), recs)
		requireGap(t, gap, entryTime(2), entryTime(5))
		requireMessages(t, fmt.Sprintf("lines of dropped run %d from the span", i+1), messages(recs), []string{"line 6"})
		drop()
	}

	requireOneGapInTheFirstKeptRun(t, w, entryTime(2), entryTime(5), "line 6")
}

func giveUpRunDroppedToo(t *testing.T) {
	w := droppedWorld(t)
	for range 4 {
		w.collect()
		w.refuse()
	}
	w.online(seqID(1), jentries(1, 6))
	w.collect() // the run that gives up the span, and its result is lost
	w.refuse()

	requireOneGapInTheFirstKeptRun(t, w, entryTime(2), entryTime(5), "line 6")
}

func giveUpRunOfAFileDroppedToo(t *testing.T) {
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 3))
	w.step()
	w.appendLog("mail.log", fileLines(4, 5))
	for range 4 {
		w.collect()
		w.refuse()
	}
	w.appendLog("mail.log", fileLines(6, 6))
	w.collect()
	w.refuse()

	requireOneGapInTheFirstKeptRun(t, w, fileLineTime(3), fileLineTime(5), "file 6")
}

// requireOneGapInTheFirstKeptRun runs the collector five times with kept results and checks that the first run carries one gap that
// starts at from and does not end before minTo, that no later run carries one, and that the line last is shipped once: the span that
// was given up is covered by a gap even though the run that first gave it up was dropped.
func requireOneGapInTheFirstKeptRun(t *testing.T, w *world, from, minTo time.Time, last string) {
	t.Helper()
	var runs [][]model.Record
	for range 5 {
		runs = append(runs, w.step())
	}
	gaps := gapsOf(runs[0])
	if len(gaps) != 1 || !gaps[0].From.Equal(from) || gaps[0].To.Before(minTo) {
		t.Fatalf("the first kept run holds the gaps %v, want exactly one from %v to at least %v", gaps, from, minTo)
	}
	var lines []string
	for i, recs := range runs {
		if i > 0 && len(gapsOf(recs)) != 0 {
			t.Errorf("kept run %d holds %d gaps, want none: the gap is carried by the first kept run only", i+1, len(gapsOf(recs)))
		}
		lines = append(lines, messages(recs)...)
	}
	if n := strings.Count(strings.Join(lines, "\n")+"\n", last+"\n"); n != 1 {
		t.Errorf("the line %q was shipped %d times in %v, want once", last, n, head(lines))
	}
}

func giveUpAfterRefusals(t *testing.T) {
	w := droppedWorld(t)
	for i := 1; i <= 4; i++ {
		recs := w.collect()
		requireMessages(t, fmt.Sprintf("lines of dropped run %d", i), messages(recs), droppedSpan)
		requireNoGaps(t, fmt.Sprintf("dropped run %d", i), recs)
		w.refuse()
	}
	w.online(seqID(1), jentries(1, 6))

	giveUp := w.collect()
	w.keep()

	requireMessages(t, "lines of the run that gives up the span", messages(giveUp), []string{"line 6"})
	requireGap(t, oneGap(t, "the run that gives up", giveUp), entryTime(2), entryTime(5))
	if next := w.step(); len(next) != 0 {
		t.Errorf("the run after the give-up returned %d records, want none (the span is not read again)", len(next))
	}
}

func giveUpAfterTimeouts(t *testing.T) {
	w := droppedWorld(t)
	for range 4 {
		w.collect()
		w.timeout()
	}

	giveUp := w.collect()
	start := w.run.Start
	w.timeout()

	requireGap(t, oneGap(t, "the run after four timeouts", giveUp), entryTime(2), start)
	for i := range 2 {
		if more := gapsOf(w.collect()); len(more) != 0 {
			t.Errorf("run %d after the gap holds %d more gaps, want none: the gap is made once per committed state", i, len(more))
		}
		w.timeout()
	}
}

func giveUpAfterMixedLosses(t *testing.T) {
	w := droppedWorld(t)
	w.collect()
	w.refuse()
	w.collect()
	w.refuse()
	w.collect()
	w.timeout()
	w.collect()
	w.timeout()

	giveUp := w.collect()
	w.keep()

	requireMessages(t, "lines of the run that gives up the span", messages(giveUp), nil)
	requireGap(t, oneGap(t, "the run after four dropped runs", giveUp), entryTime(2), entryTime(5))
}

func giveUpNothingAfterThree(t *testing.T) {
	w := droppedWorld(t)
	for range 3 {
		w.collect()
		w.refuse()
	}

	recs := w.collect()
	w.keep()

	requireMessages(t, "lines of the fourth run", messages(recs), droppedSpan)
	requireNoGaps(t, "the fourth run", recs)
}

// textBytes counts the UTF-8 bytes of the log, host, program and message of the record, the text a run budget counts.
func textBytes(l *model.LogLine) int {
	return len(l.Log) + len(l.Host) + len(l.Program) + len(l.Message)
}

func TestLogs_RunBudgets(t *testing.T) {
	t.Run("a run ends at 1,000 records", budgetOfRecords)
	t.Run("a run ends at the record that crosses 4 MiB of text", budgetOfBytes)
	t.Run("a run ends after one second of reading", budgetOfTime)
	t.Run("filtered entries count only against the second", budgetOfFilteredEntries)
	t.Run("filtered entries that need decoding look at the clock at each entry", budgetOfFilteredDecodedEntries)
}

func budgetOfRecords(t *testing.T) {
	w := newWorld(t)
	w.online(seqID(1), jentries(1, 2500))
	w.position(seqID(1), 0)
	w.start()

	first, second, third := w.step(), w.step(), w.step()

	if len(logLines(first)) != 1000 || len(logLines(second)) != 1000 || len(logLines(third)) != 500 {
		t.Fatalf("runs returned %d, %d and %d records, want 1000, 1000 and 500", len(logLines(first)), len(logLines(second)), len(logLines(third)))
	}
	requireMessages(t, "all lines", messages(append(append(first, second...), third...)), numbered("line", 1, 2500))
	if more := w.step(); len(more) != 0 {
		t.Errorf("the fourth run returned %d records, want none", len(more))
	}
}

func budgetOfBytes(t *testing.T) {
	const limit = 4 << 20
	w := newWorld(t)
	var entries []map[string][]byte
	for seq := uint64(1); seq <= 400; seq++ {
		entries = append(entries, jentry(seq, fmt.Sprintf("%04d", seq)+strings.Repeat("m", 16384-4)))
	}
	w.online(seqID(1), entries)
	w.position(seqID(1), 0)
	w.start()

	first := logLines(w.step())

	if len(first) == 0 || len(first) >= 400 {
		t.Fatalf("the first run returned %d records, want some, but not all 400: it stops at the byte budget", len(first))
	}
	total := 0
	for _, l := range first {
		total += textBytes(l)
	}
	if last := textBytes(first[len(first)-1]); total < limit || total-last >= limit {
		t.Errorf("the run returned %d records of %d bytes of text (%d without the last), want the last record to be the one that crosses %d bytes", len(first), total, total-last, limit)
	}
	rest := logLines(w.drain(20))
	if got := len(first) + len(rest); got != 400 {
		t.Errorf("all runs returned %d records, want 400", got)
	}
	if next := rest[0].Message[:4]; next != fmt.Sprintf("%04d", len(first)+1) {
		t.Errorf("the second run starts with entry %s, want %04d (the first record after the crossing one)", next, len(first)+1)
	}
}

func budgetOfTime(t *testing.T) {
	w := newWorld(t)
	w.online(seqID(1), jentries(1, 600))
	w.position(seqID(1), 0)
	w.clock.step = time.Second
	w.start()

	first := w.step()

	if n := len(logLines(first)); n == 0 || n >= 500 {
		t.Fatalf("the first run returned %d records on a clock that moves a second per reading, want some, but far fewer than 600", n)
	}
	rest := w.drain(700)
	requireMessages(t, "all lines", messages(append(first, rest...)), numbered("line", 1, 600))
}

// floodedWorld returns a world whose journal holds 1,200 entries of the debug priority and then the three wanted entries 1201 to 1203.
func floodedWorld(t *testing.T, step time.Duration) *world {
	t.Helper()
	w := newWorld(t)
	var entries []map[string][]byte
	for seq := uint64(1); seq <= 1200; seq++ {
		entries = append(entries, jentry(seq, fmt.Sprintf("debug %d", seq), "PRIORITY", "7"))
	}
	for seq := uint64(1201); seq <= 1203; seq++ {
		entries = append(entries, jentry(seq, fmt.Sprintf("wanted %d", seq)))
	}
	w.online(seqID(1), entries)
	w.position(seqID(1), 0)
	w.clock.step = step
	w.start()
	return w
}

func budgetOfFilteredDecodedEntries(t *testing.T) {
	w := newWorld(t)
	var entries []map[string][]byte
	for seq := uint64(1); seq <= 20; seq++ {
		entries = append(entries, jentry(seq, fmt.Sprintf("%02d", seq)+strings.Repeat("d", 698), "PRIORITY", "7"))
	}
	for seq := uint64(21); seq <= 23; seq++ {
		entries = append(entries, jentry(seq, fmt.Sprintf("wanted %d", seq)))
	}
	w.online(seqID(1), entries)
	w.position(seqID(1), 0)
	w.clock.step = 250 * time.Millisecond
	w.start()

	first := w.step()

	requireMessages(t, "lines of the first run", messages(first), nil)
	pos := w.savedJournal(seqID(1))
	if pos == nil || pos.Seqnum == 0 || pos.Seqnum >= 20 {
		t.Fatalf("journal position after the first run = %+v, want a sequence number above 0 and below 20: the clock is looked at after each entry that was decoded", pos)
	}
	var got []model.Record
	for range 10 {
		got = append(got, w.step()...)
	}
	requireMessages(t, "lines of ten further runs", messages(got), []string{"wanted 21", "wanted 22", "wanted 23"})
	requireNoGaps(t, "the runs", got)
}

func budgetOfFilteredEntries(t *testing.T) {
	wanted := []string{"wanted 1201", "wanted 1202", "wanted 1203"}

	t.Run("on a clock that stands still the wanted lines come in the first run", func(t *testing.T) {
		w := floodedWorld(t, 0)

		first := w.step()

		requireMessages(t, "lines of the first run", messages(first), wanted)
	})

	t.Run("on a clock that moves they come in later runs", func(t *testing.T) {
		w := floodedWorld(t, time.Second)

		first := w.step()
		rest := w.drain(1500)

		requireMessages(t, "lines of all runs", messages(append(first, rest...)), wanted)
		requireNoGaps(t, "all runs", append(first, rest...))
	})
}

// liveHeap returns the bytes of live heap after a collection.
func liveHeap() uint64 {
	runtime.GC()
	var m runtime.MemStats
	runtime.ReadMemStats(&m)
	return m.HeapAlloc
}

// peakLive runs the collector until a run returns nothing (at most limit runs) and returns the most live heap above base that it
// saw right after a run, while the records of the run are still referenced, and the lines of the records that were cut.
func peakLive(w *world, base uint64, limit int) (peak uint64, records int, cut int) {
	w.t.Helper()
	for range limit {
		recs := w.collect()
		live := liveHeap()
		runtime.KeepAlive(recs)
		peak = max(peak, live-min(live, base))
		records += len(logLines(recs))
		for _, l := range logLines(recs) {
			if l.Truncated && len(l.Message) == 16384-len(strings.TrimSuffix(fileLine(1, ""), "\n")) {
				cut++
			}
		}
		w.keep()
		if len(recs) == 0 {
			break
		}
	}
	return peak, records, cut
}

// liveLimit is the live heap the collector may hold: 24 MiB.
const liveLimit = 24 << 20

func TestLogs_HeapStaysBounded(t *testing.T) {
	t.Run("records at the maximum of every field", heapOfMaximalRecords)
	t.Run("a journal of 100,000 entries", heapOfALargeJournal)
	t.Run("a file of lines over 16 KiB", heapOfLongLines)
}

func heapOfMaximalRecords(t *testing.T) {
	w := newWorld(t)
	func() {
		var entries []map[string][]byte
		for seq := uint64(1); seq <= 600; seq++ {
			entries = append(entries, jentry(seq, fmt.Sprintf("%04d", seq)+strings.Repeat("m", 16384-4),
				"_HOSTNAME", strings.Repeat("h", 1024), "SYSLOG_IDENTIFIER", strings.Repeat("p", 1024)))
		}
		w.online(seqID(1), entries)
	}()
	w.position(seqID(1), 0)
	w.start()
	base := liveHeap()

	peak, records, _ := peakLive(w, base, 10)

	if records != 600 {
		t.Errorf("the runs returned %d records, want 600", records)
	}
	if peak > liveLimit {
		t.Errorf("the collector held %d bytes of live heap above the base, want at most %d", peak, liveLimit)
	}
}

func heapOfALargeJournal(t *testing.T) {
	w := newWorld(t)
	func() {
		entries := make([]map[string][]byte, 0, 100_000)
		for seq := uint64(1); seq <= 100_000; seq++ {
			entries = append(entries, slimEntry(seq))
		}
		w.online(seqID(1), entries)
	}()
	w.position(seqID(1), 0)
	w.start()
	base := liveHeap()

	peak, records, _ := peakLive(w, base, 25)

	if records != 25_000 {
		t.Errorf("25 runs returned %d records, want 25000", records)
	}
	if peak > liveLimit {
		t.Errorf("the collector held %d bytes of live heap above the base, want at most %d", peak, liveLimit)
	}
}

func heapOfLongLines(t *testing.T) {
	w := newWorld(t)
	w.useFiles("big.log")
	w.writeLog("big.log", fileLine(1, "first"))
	w.start()
	w.step()
	for n := 2; n <= 41; n++ {
		w.appendLog("big.log", fileLine(n, fmt.Sprintf("%02d", n)+strings.Repeat("x", 100<<10)))
	}
	base := liveHeap()

	peak, _, cut := peakLive(w, base, 10)

	if cut != 40 {
		t.Errorf("%d records are cut as the import cuts them (the whole line at 16,384 bytes) with truncated set, want all 40 long lines", cut)
	}
	if peak > liveLimit {
		t.Errorf("the collector held %d bytes of live heap above the base, want at most %d", peak, liveLimit)
	}
}

// secret is a value that must reach no log line of the collector: it is part of the path of the log file and of the journal entry.
const secret = "S3CRETVALUE"

// fixedLogTexts are the only messages the collector logs, with the fixed reasons of a file it names.
var fixedLogTexts = map[string]bool{
	"journal not readable": true, "journal readable again": true, "log file not readable": true,
	"log file readable again": true, "time zone not readable": true, "time zone readable again": true,
}

var fixedFileReasons = map[string]bool{"missing": true, "not a regular file": true, "link refused": true, "permission denied": true, "read failed": true}

func TestLogs_LogsHoldNoPathFieldOrMessage(t *testing.T) {
	w := newWorld(t)
	secretDir := filepath.Join(w.root, secret+"dir")
	if err := os.MkdirAll(secretDir, 0o700); err != nil {
		t.Fatalf("MkdirAll() error = %v, want nil", err)
	}
	logFile := filepath.Join(secretDir, secret+".log")
	w.cfg.Files = []logship.FileSpec{{Path: logFile, Format: "syslog"}}
	w.online(seqID(1), []map[string][]byte{jentry(1, secret+" first", "_HOSTNAME", secret+"host", "SYSLOG_IDENTIFIER", secret+"prog")})
	w.start()

	provokeEveryLogText(t, w, logFile)

	requireFixedLogLines(t, w)
}

// provokeEveryLogText changes the journal, the log file and the time zone of the world so that the collector logs every one of its
// texts: the journal without a machine ID, the file missing, a directory, a link and a regular file again, and the missing zone.
func provokeEveryLogText(t *testing.T, w *world, logFile string) {
	t.Helper()
	step := func() {
		w.online(seqID(1), jentries(1, 1))
		w.step()
	}
	line := fileLine(1, secret+" line")
	mustDo := func(what string, err error) {
		t.Helper()
		if err != nil {
			t.Fatalf("%s: %v", what, err)
		}
	}
	step()
	w.etc.remove("machine-id")
	step()
	step()
	w.etc.set("machine-id", []byte(machineID+"\n"))
	step()
	mustDo("writing the log file", os.WriteFile(logFile, []byte(line), 0o600))
	step()
	mustDo("renaming the log file", os.Rename(logFile, logFile+".keep"))
	mustDo("making a directory in place of the log file", os.Mkdir(logFile, 0o700))
	step()
	mustDo("removing the directory", os.Remove(logFile))
	mustDo("making a link in place of the log file", os.Symlink(logFile+".keep", logFile))
	step()
	mustDo("removing the link", os.Remove(logFile))
	mustDo("restoring the log file", os.Rename(logFile+".keep", logFile))
	step()
	w.etc.remove("localtime")
	mustDo("appending to the log file", os.WriteFile(logFile, []byte(line+fileLine(2, secret+" second")), 0o600))
	step()
	w.etc.set("localtime", tzFixed(utcPlus2))
	step()
}

// requireFixedLogLines checks that the collector logged only its fixed texts, with the attributes err and file only, and no path, field
// value or message, and that every text was logged at least once.
func requireFixedLogLines(t *testing.T, w *world) {
	t.Helper()
	seen := map[string]int{}
	w.logs.mu.Lock()
	defer w.logs.mu.Unlock()
	for _, e := range w.entries {
		seen[e.msg]++
		if !fixedLogTexts[e.msg] {
			t.Errorf("the collector logged %q, want only the fixed texts of the warnings and their recoveries", e.msg)
		}
		requireFixedAttributes(t, e, w.root)
	}
	for msg := range fixedLogTexts {
		if seen[msg] == 0 {
			t.Errorf("the collector never logged %q, want the scenario to produce every fixed text once per change", msg)
		}
	}
}

func requireFixedAttributes(t *testing.T, e logEntry, root string) {
	t.Helper()
	for k, v := range e.attrs {
		if k != "err" && k != "file" {
			t.Errorf("log line %q has the attribute %q, want only err and file", e.msg, k)
		}
		if strings.Contains(v, secret) || strings.Contains(v, root) || strings.Contains(v, "/") {
			t.Errorf("log line %q attribute %s = %q, want no path, field value or message", e.msg, k, v)
		}
	}
	switch e.msg {
	case "journal not readable":
		if !strings.HasPrefix(e.attrs["err"], "journal: ") {
			t.Errorf("journal not readable err = %q, want a fixed reason that starts with %q", e.attrs["err"], "journal: ")
		}
	case "log file not readable":
		if e.attrs["file"] != "0" || !fixedFileReasons[e.attrs["err"]] {
			t.Errorf("log file not readable file = %q err = %q, want the index 0 and one of the fixed reasons", e.attrs["file"], e.attrs["err"])
		}
	}
}

// refusePoison swaps the record check for one that refuses the log lines with the message "poison" and checks the rest as the
// scheduler does.
func refusePoison(t *testing.T) {
	t.Helper()
	restore := logship.SwapRecordCheck(func(r *model.Record) (int, error) {
		if l, ok := r.Data.(*model.LogLine); ok && l.Message == "poison" {
			return 0, errors.New("refused by the test")
		}
		return wire.CheckRecord(r)
	})
	t.Cleanup(restore)
}

func TestLogs_RefusedRecordBecomesAGapAndTheRunGoesOn(t *testing.T) {
	t.Run("a journal entry", func(t *testing.T) {
		refusePoison(t)
		w := newWorld(t)
		entries := jentries(1, 4)
		entries[2] = jentry(3, "poison")
		w.online(seqID(1), entries)
		w.position(seqID(1), 0)
		w.start()

		first := w.step()
		next := w.step()

		requireMessages(t, "lines of the run", messages(first), []string{"line 1", "line 2", "line 4"})
		requireGap(t, oneGap(t, "the run", first), entryTime(2), entryTime(3))
		if len(next) != 0 {
			t.Errorf("the next run returned %v and %d gaps, want nothing: the refused entry is not read again", head(messages(next)), len(gapsOf(next)))
		}
		if pos := w.savedJournal(seqID(1)); pos == nil || pos.Seqnum != 4 {
			t.Errorf("journal position = %+v, want the sequence number 4: the lines after the refused one are kept", pos)
		}
	})
	t.Run("a file line", func(t *testing.T) {
		refusePoison(t)
		w := mailWorld(t)
		w.appendLog("mail.log", fileLine(3, "ok")+fileLine(4, "poison")+fileLine(5, "after"))

		first := w.step()
		next := w.step()

		requireMessages(t, "lines of the run", fileMsgs(first), []string{"ok", "after"})
		requireGap(t, oneGap(t, "the run", first), fileLineTime(3), fileLineTime(4))
		if len(next) != 0 {
			t.Errorf("the next run returned %v and %d gaps, want nothing", head(messages(next)), len(gapsOf(next)))
		}
	})
	t.Run("a file line that is the first after the position", func(t *testing.T) {
		refusePoison(t)
		w := mailWorld(t)
		taken := w.run.Start
		w.appendLog("mail.log", fileLine(3, "poison"))

		recs := w.step()

		requireMessages(t, "lines of the run", fileMsgs(recs), nil)
		requireGap(t, oneGap(t, "the run", recs), taken, taken.Add(time.Microsecond))
	})
}

// logFilesConfig returns a configuration of n log files.
func logFilesConfig(n int) logship.Config {
	cfg := logship.Config{}
	for i := range n {
		cfg.Files = append(cfg.Files, logship.FileSpec{Path: fmt.Sprintf("/var/log/f%d.log", i), Format: "syslog"})
	}
	return cfg
}

func TestLogs_NewRefusesOneFileMoreThanTheStateFileHolds(t *testing.T) {
	l, err := logship.New(logship.Sources{}, logFilesConfig(agentstate.MaxLogFiles+1), nil, slog.New(slog.DiscardHandler))

	if err == nil || err.Error() != "logship: too many log files" || l != nil {
		t.Errorf("New() = %v, %v, want no collector and the error %q", l, err, "logship: too many log files")
	}
}

func TestLogs_NewTakesAsManyFilesAsTheStateFileHoldsAndNamesTheCollector(t *testing.T) {
	sources := logship.Sources{Etc: fstestEmpty(), Journal: []fs.FS{fstestEmpty()}}

	l, err := logship.New(sources, logFilesConfig(agentstate.MaxLogFiles), nil, slog.New(slog.DiscardHandler))

	if err != nil || l == nil {
		t.Fatalf("New() = %v, %v, want a collector", l, err)
	}
	if l.Name() != "logs" || logship.Name != "logs" {
		t.Errorf("Name() = %q and the constant %q, want %q", l.Name(), logship.Name, "logs")
	}
}

func TestLogs_NewFillsTheSourcesThatAreNotGiven(t *testing.T) {
	l, err := logship.New(logship.Sources{}, logship.Config{Journal: true, MinPriority: 6}, nil, slog.New(slog.DiscardHandler))

	if err != nil || l == nil {
		t.Fatalf("New() = %v, %v, want a collector that reads the host's /etc, journal, files and clock", l, err)
	}
}

// fstestEmpty returns a file system without a file.
func fstestEmpty() fs.FS { return fstest.MapFS{} }

func TestLogs_WorksWithoutAStateFile(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.online(seqID(1), jentries(1, 2))
	w.writeLog("mail.log", fileLines(1, 2))
	l, err := logship.New(w.sources(), w.cfg, nil, slog.New(slog.DiscardHandler))
	if err != nil {
		t.Fatalf("New() error = %v, want nil", err)
	}
	collect := func() []model.Record {
		run := scheduler.Run{Start: w.clock.peek().UTC()}
		recs, err := l.Collect(context.Background(), run)
		if err != nil {
			t.Fatalf("Collect() error = %v, want nil", err)
		}
		l.Kept(run, true)
		w.clock.advance(runInterval)
		return recs
	}

	first := collect()
	w.online(seqID(1), jentries(1, 4))
	w.appendLog("mail.log", fileLines(3, 3))
	second := collect()
	third := collect()

	if len(first) != 0 {
		t.Errorf("the first run returned %v, want nothing: a first start ships nothing older", head(messages(first)))
	}
	requireMessages(t, "journal lines of the second run", ofSource(second, "journal"), []string{"line 3", "line 4"})
	requireMessages(t, "file lines of the second run", ofSource(second, "syslog"), []string{"file 3"})
	if len(third) != 0 {
		t.Errorf("the third run returned %v, want nothing", head(messages(third)))
	}
}

func TestLogs_ReportOfAnotherRunCommitsNothing(t *testing.T) {
	t.Run("a report for a run that is not the last", func(t *testing.T) {
		w := droppedWorld(t)
		first := w.collect()

		w.l.Kept(scheduler.Run{Start: w.run.Start.Add(-time.Hour)}, true)
		w.clock.advance(runInterval)
		again := w.collect()
		w.keep()

		requireMessages(t, "lines of the first run", messages(first), droppedSpan)
		requireMessages(t, "lines of the run after the report of another run", messages(again), droppedSpan)
	})
	t.Run("a report before any run", func(t *testing.T) {
		w := droppedWorld(t)

		w.l.Kept(scheduler.Run{Start: w.clock.peek()}, true)
		recs := w.step()

		requireMessages(t, "lines of the first run", messages(recs), droppedSpan)
	})
	t.Run("a second report for the same run", func(t *testing.T) {
		w := droppedWorld(t)
		w.collect()
		w.keep()

		w.l.Kept(w.run, false)
		recs := w.step()

		if len(recs) != 0 {
			t.Errorf("the run after a late refusal of a kept run returned %v, want nothing: a run is reported once", head(messages(recs)))
		}
	})
}

func TestLogs_FourthDroppedRunOfFilesGivesUpFromTheLastLine(t *testing.T) {
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 3))
	w.step()
	w.appendLog("mail.log", fileLines(4, 5))
	for range 4 {
		recs := w.collect()
		requireMessages(t, "lines of a dropped run", fileMsgs(recs), []string{"file 4", "file 5"})
		w.refuse()
	}

	giveUp := w.collect()
	w.keep()
	next := w.step()

	requireMessages(t, "lines of the run that gives up", fileMsgs(giveUp), nil)
	requireGap(t, oneGap(t, "the run that gives up", giveUp), fileLineTime(3), fileLineTime(5))
	if len(next) != 0 {
		t.Errorf("the run after the give-up returned %v and %d gaps, want nothing: the span is not read again", head(messages(next)), len(gapsOf(next)))
	}
}

func TestLogs_FourthDroppedRunWithNothingReadGivesAGapAtTheRunStart(t *testing.T) {
	w := newWorld(t)
	w.start()
	for range 4 {
		w.collect()
		w.refuse()
	}

	giveUp := w.collect()
	start := w.run.Start
	w.keep()

	requireGap(t, oneGap(t, "the run that gives up", giveUp), start, start.Add(time.Microsecond))
}
