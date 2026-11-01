package logship_test

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"sort"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/journal/journaltest"
	"github.com/LarsLaskowski/Vandox/internal/logship"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	oomStart  = "[1.0] example invoked oom-killer: gfp_mask=0x100cca(GFP_HIGHUSER_MOVABLE), order=0, oom_score_adj=0"
	oomTrace  = "[1.1] Call Trace:"
	oomKilled = "[1.9] Out of memory: Killed process 4242 (example) total-vm:8400000kB"
	// hdrPrefix starts the message of a MariaDB error log header.
	hdrPrefix = "2026-10-23 06:00:00 0 ["
)

// set moves the clock to t.
func (c *fakeClock) set(t time.Time) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = t
}

// at moves the clock to d after the entry with the sequence number seq.
func (w *world) at(seq uint64, d time.Duration) {
	w.clock.set(entryTime(seq).Add(d))
}

// flush calls Flush of the collector as the scheduler does when the agent stops; the caller reports with keep or refuse.
func (w *world) flush() []model.Record {
	w.t.Helper()
	run := scheduler.Run{Start: w.clock.peek().UTC(), PreviousLost: w.lost}
	recs := w.l.Flush(context.Background(), run)
	w.run = run
	return recs
}

// journalFeed is a journal file of one ID that grows: add appends entries and writes the file again.
type journalFeed struct {
	w   *world
	id  [16]byte
	all []map[string][]byte
}

func (f *journalFeed) add(entries ...map[string][]byte) {
	f.w.t.Helper()
	f.all = append(f.all, entries...)
	f.w.online(f.id, f.all)
}

// mariaEntry returns a journal entry of mariadbd (process 2345).
func mariaEntry(seq uint64, message string, kv ...string) map[string][]byte {
	return jentry(seq, message, append([]string{"SYSLOG_IDENTIFIER", "mariadbd", "_PID", "2345"}, kv...)...)
}

// header returns the message of a MariaDB error log header with the level and the text.
func header(level, text string) string { return hdrPrefix + level + "] " + text }

// kernelEntry returns a journal entry of the kernel.
func kernelEntry(seq uint64, message string, kv ...string) map[string][]byte {
	return jentry(seq, message, append([]string{"SYSLOG_IDENTIFIER", "kernel", "_PID", ""}, kv...)...)
}

// fileLineAs returns the syslog line number n of the program prog with the process ID pid.
func fileLineAs(n int, prog string, pid int, message string) string {
	local := fileLineTime(n).In(time.FixedZone("", utcPlus2))
	return fmt.Sprintf("%s web-1 %s[%d]: %s\n", local.Format("Jan _2 15:04:05"), prog, pid, message)
}

// requireNoLines fails when the records hold a log line.
func requireNoLines(t *testing.T, what string, recs []model.Record) {
	t.Helper()
	if got := messages(recs); len(got) != 0 {
		t.Errorf("%s = %v, want no log line", what, head(got))
	}
}

// ofProgram returns the log lines of the records whose program is prog.
func ofProgram(recs []model.Record, prog string) []*model.LogLine {
	var out []*model.LogLine
	for _, l := range logLines(recs) {
		if l.Program == prog {
			out = append(out, l)
		}
	}
	return out
}

// requireOneLine fails unless the records hold exactly one log line of the program, and returns it.
func requireOneLine(t *testing.T, what string, recs []model.Record, prog string) *model.LogLine {
	t.Helper()
	got := ofProgram(recs, prog)
	if len(got) != 1 {
		t.Fatalf("%s holds %d log lines of %s %v, want 1", what, len(got), prog, head(messages(recs)))
	}
	return got[0]
}

func TestLogs_GroupSpansRunsAndBatches(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	feed := &journalFeed{w: w, id: id}
	feed.add(mariaEntry(10, header("ERROR", "mysqld got signal 11 ;")), kernelEntry(11, oomStart))
	w.position(id, 9)
	w.start()
	w.at(11, time.Second)
	first := w.step()
	feed.add(mariaEntry(12, "This could be because you hit a bug."), kernelEntry(13, oomTrace))
	w.at(13, time.Second)
	second := w.step()
	feed.add(mariaEntry(14, "Attempting backtrace."), kernelEntry(15, oomKilled), jentry(16, "other"))
	w.at(16, time.Second)

	third := w.step()
	w.at(10, 61*time.Second)
	fourth := w.step()

	requireNoLines(t, "lines of the run that opened both groups", first)
	requireNoLines(t, "lines of the run that added to both", second)
	requireMessages(t, "lines of the run that ended the report", messages(third), []string{oomStart + "\n" + oomTrace + "\n" + oomKilled, "other"})
	entry := requireOneLine(t, "the run that closed the entry", fourth, "mariadbd")
	if entry.Message != "mysqld got signal 11 ;\nThis could be because you hit a bug.\nAttempting backtrace." || entry.Event != "mariadb.abort" || entry.Priority == nil || *entry.Priority != 3 {
		t.Errorf("entry = message %q event %q priority %v, want the three lines in one record with the event mariadb.abort and priority 3", entry.Message, entry.Event, entry.Priority)
	}
	if n := len(logLines(fourth)); n != 1 {
		t.Errorf("the run that closed the entry returned %d log lines, want 1 (every line once)", n)
	}
}

func TestLogs_DroppedRunJoinsEachLineOnce(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	feed := &journalFeed{w: w, id: id}
	feed.add(mariaEntry(10, header("Note", "first")), mariaEntry(11, "second"))
	w.position(id, 9)
	w.start()
	w.at(11, time.Second)
	dropped := w.collect()
	w.refuse()
	feed.add(mariaEntry(12, "third"))
	w.at(12, time.Second)

	kept := w.step()
	w.at(10, 61*time.Second)
	closed := w.step()

	requireNoLines(t, "lines of the dropped run", dropped)
	requireNoLines(t, "lines of the run that is kept while the entry is open", kept)
	requireMessages(t, "lines of the run that closed the entry", messages(closed), []string{"first\nsecond\nthird"})
}

func TestLogs_CaughtUpEntryClosesAfterSixtySeconds(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	feed := &journalFeed{w: w, id: id}
	feed.add(mariaEntry(10, header("Note", "caught up")))
	w.position(id, 9)
	w.start()
	w.at(10, time.Second)
	opened := w.step()
	w.at(10, 60*time.Second)

	atSixty := w.collect()
	w.keep()
	w.at(10, 60*time.Second+time.Microsecond)
	after := w.step()

	requireNoLines(t, "lines of the run that opened the entry", opened)
	requireNoLines(t, "lines of the run at exactly 60 s", atSixty)
	requireMessages(t, "lines of the run at 60 s and 1 us", messages(after), []string{"caught up"})
}

func TestLogs_BacklogLineJoinsByCaptureTime(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	feed := &journalFeed{w: w, id: id}
	feed.add(mariaEntry(20, header("Note", "backlog")), mariaEntry(70, "joined 50 s later"))
	w.position(id, 19)
	w.start()
	w.at(70, 90*time.Second)

	recs := w.step()

	requireMessages(t, "lines of the run", messages(recs), []string{"backlog\njoined 50 s later"})
}

func TestLogs_LineSixtyOneSecondsLaterClosesTheEntry(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	feed := &journalFeed{w: w, id: id}
	feed.add(mariaEntry(20, header("Note", "first part")), mariaEntry(81, "61 s later"))
	w.position(id, 19)
	w.start()
	w.at(20, time.Second)

	recs := w.step()

	requireMessages(t, "lines of the run", messages(recs), []string{"first part", "61 s later"})
}

func TestLogs_FutureDatedGroupClosesSixtySecondsAfterItsRun(t *testing.T) {
	w := newWorld(t)
	w.useFiles("forged.log")
	w.writeLog("forged.log", fileLines(1, 1))
	w.start()
	w.step()
	future := time.Date(2027, time.October, 23, 6, 0, 0, 0, time.UTC).Format("2006-01-02T15:04:05.000000-07:00")
	w.appendLog("forged.log", future+" web-1 kernel: "+oomStart+"\n"+future+" web-1 kernel: "+oomTrace+"\n")
	started := w.clock.peek()
	opened := w.collect()
	w.keep()
	w.clock.set(started.Add(60 * time.Second))

	atSixty := w.collect()
	w.keep()
	w.clock.set(started.Add(60*time.Second + time.Microsecond))
	after := w.step()

	requireNoLines(t, "lines of the run that read the forged report", opened)
	requireNoLines(t, "lines of the run 60 s after it", atSixty)
	requireMessages(t, "lines of the run 60 s and 1 us after the run that read it", messages(after), []string{"[1.0] example invoked oom-killer: gfp_mask=0x100cca(GFP_HIGHUSER_MOVABLE), order=0, oom_score_adj=0\n" + oomTrace})
}

// heldEntryWorld returns a world with the entries before (seq 1), a held MariaDB entry (seq 2 and 3) and a later line (seq 4), and
// the first run made: it shipped "before" and "later".
func heldEntryWorld(t *testing.T) (*world, *journalFeed) {
	t.Helper()
	w := newWorld(t)
	feed := &journalFeed{w: w, id: seqID(1)}
	feed.add(jentry(1, "before"), mariaEntry(2, header("Note", "entry")), mariaEntry(3, "cont"), jentry(4, "later"))
	w.position(feed.id, 0)
	w.start()
	w.at(4, time.Second)
	first := w.step()
	requireMessages(t, "lines of the first run (the held entry waits, the later line does not)", messages(first), []string{"before", "later"})
	w.stop()
	return w, feed
}

func TestLogs_StopFlushSendsOpenGroupsAndSavesTheShippedPositions(t *testing.T) {
	w, feed := heldEntryWorld(t)
	if pos := w.savedJournal(feed.id); pos == nil || pos.Seqnum != 1 {
		t.Fatalf("journal position while the entry is held = %+v, want the sequence number 1 (the line before the entry)", pos)
	}

	flushed := w.flush()
	w.keep()

	requireMessages(t, "lines of the flush", messages(flushed), []string{"entry\ncont"})
	if pos := w.savedJournal(feed.id); pos == nil || pos.Seqnum != 4 {
		t.Errorf("journal position after a kept flush = %+v, want the sequence number 4", pos)
	}
	w.restart()
	feed.add(jentry(5, "after"))
	w.at(5, time.Second)
	next := w.step()
	requireMessages(t, "lines of the new collector", messages(next), []string{"after"})
}

func TestLogs_KillWithoutFlushReadsAgainFromTheOldestHeldLine(t *testing.T) {
	w, _ := heldEntryWorld(t)

	w.crash()
	w.at(4, 2*time.Second)
	again := w.step()
	w.at(2, 61*time.Second)
	closed := w.step()

	requireMessages(t, "lines read again after the kill", messages(again), []string{"later"})
	requireMessages(t, "lines of the run that closed the entry", messages(closed), []string{"entry\ncont"})
}

func TestLogs_FlushNotKeptLeavesThePositionsHeldBack(t *testing.T) {
	w, feed := heldEntryWorld(t)

	flushed := w.flush()
	w.refuse()

	requireMessages(t, "lines of the flush", messages(flushed), []string{"entry\ncont"})
	if pos := w.savedJournal(feed.id); pos == nil || pos.Seqnum != 1 {
		t.Fatalf("journal position after a flush that was not kept = %+v, want the sequence number 1 (held back)", pos)
	}
	w.crash()
	w.at(4, 2*time.Second)
	w.step()
	w.at(2, 61*time.Second)
	closed := w.step()
	requireMessages(t, "lines of the run that closed the entry after the restart", messages(closed), []string{"entry\ncont"})
}

func TestLogs_RestartWithANewDropRuleLosesNoLine(t *testing.T) {
	w, _ := heldEntryWorld(t)
	w.cfg.Drop = []logship.Rule{{Program: "mariadbd", Message: regexp.MustCompile("^" + regexp.QuoteMeta(header("Note", "entry")) + "$")}}

	w.crash()
	w.at(4, 2*time.Second)
	again := w.step()

	requireMessages(t, "lines after the restart with the rule", messages(again), []string{"cont", "later"})
}

func TestLogs_FilterDecidesBeforeClassification(t *testing.T) {
	t.Run("a Postfix line below the minimum priority", func(t *testing.T) {
		w := newWorld(t)
		w.cfg.MinPriority = 5
		w.online(seqID(1), []map[string][]byte{
			jentry(1, "warning: dropped", "SYSLOG_IDENTIFIER", "postfix/smtpd"),
			jentry(2, "fatal: dropped", "SYSLOG_IDENTIFIER", "postfix/postfix-script"),
			jentry(3, "fatal: kept", "SYSLOG_IDENTIFIER", "postfix/postfix-script", "PRIORITY", "5"),
		})
		w.position(seqID(1), 0)
		w.start()
		w.at(3, time.Second)

		recs := w.step()

		requireMessages(t, "lines of the run", messages(recs), []string{"fatal: kept"})
		if l := logLines(recs); len(l) == 1 && (l[0].Event != "postfix.fatal" || l[0].Priority == nil || *l[0].Priority != 2) {
			t.Errorf("the kept line = event %q priority %v, want postfix.fatal and 2: the line is classified after the filter", l[0].Event, l[0].Priority)
		}
	})
	t.Run("a MariaDB header below the minimum priority", func(t *testing.T) {
		w := newWorld(t)
		w.cfg.MinPriority = 4
		w.online(seqID(1), []map[string][]byte{
			mariaEntry(1, header("ERROR", "dropped header")),
			mariaEntry(2, header("Note", "kept header"), "PRIORITY", "4"),
			mariaEntry(3, "cont", "PRIORITY", "4"),
		})
		w.position(seqID(1), 0)
		w.start()
		w.at(3, time.Second)
		opened := w.step()
		w.at(2, 61*time.Second)

		closed := w.step()

		requireNoLines(t, "lines of the run that opened the entry", opened)
		requireMessages(t, "lines of the run that closed the entry", messages(closed), []string{"kept header\ncont"})
	})
}

func TestLogs_DroppedHeaderLeavesItsLinesToThePreviousEntry(t *testing.T) {
	w := newWorld(t)
	w.cfg.Drop = []logship.Rule{{Message: regexp.MustCompile("two-dropped")}}
	w.online(seqID(1), []map[string][]byte{mariaEntry(1, header("Note", "one")), mariaEntry(2, header("Note", "two-dropped")), mariaEntry(3, "cont")})
	w.position(seqID(1), 0)
	w.start()
	w.at(3, time.Second)
	opened := w.step()
	w.at(1, 61*time.Second)

	closed := w.step()

	requireNoLines(t, "lines of the run that read the entry", opened)
	requireMessages(t, "lines of the run that closed the entry", messages(closed), []string{"one\ncont"})
}

func TestLogs_ForgedFloodHoldsOneEntryAndOneReportPerSource(t *testing.T) {
	const cycles = 3333
	w := newWorld(t)
	var entries []map[string][]byte
	for i := range cycles {
		seq := uint64(3*i + 1) // #nosec G115 -- small test values
		entries = append(entries,
			mariaEntry(seq, header("Note", fmt.Sprintf("h%d", i))),
			kernelEntry(seq+1, fmt.Sprintf("[1.0] x%d invoked oom-killer: gfp_mask=0x1", i)),
			jentry(seq+2, fmt.Sprintf("app %d", i)))
	}
	w.online(seqID(1), entries)
	w.position(seqID(1), 0)
	w.start()
	w.at(uint64(3*cycles), time.Second) // #nosec G115 -- small test values
	first := w.collect()
	w.keep()
	all := slices.Clone(first)
	for range 20 {
		more := w.step()
		all = append(all, more...)
		if len(more) == 0 {
			break
		}
	}

	held := w.flush()

	if n := len(logLines(held)); n != 2 {
		t.Errorf("the flush returned %d log lines %v, want 2: one entry and one report", n, head(messages(held)))
	}
	order := messages(first)
	app, entry := slices.Index(order, "app 0"), slices.Index(order, "h0")
	if app < 0 || entry < 0 || app > entry {
		t.Errorf("positions in the first run: line of another program %d, entry of the header h0 %d, want the other program's record first (it is not held back)", app, entry)
	}
	if n := len(ofProgram(all, "app")); n != cycles {
		t.Errorf("records of the other program = %d, want %d: no line is lost while groups open and close", n, cycles)
	}
}

func TestLogs_JournalAndFileNeverJoin(t *testing.T) {
	t.Run("a journal header and a file line", func(t *testing.T) {
		w := newWorld(t)
		w.useFiles("mail.log")
		w.writeLog("mail.log", "")
		w.online(seqID(1), []map[string][]byte{jentry(9, "before")})
		w.position(seqID(1), 9)
		w.start()
		w.step()
		w.online(seqID(1), []map[string][]byte{jentry(9, "before"), mariaEntry(10, header("Note", "journal header"))})
		w.appendLog("mail.log", fileLineAs(11, "mariadbd", 2345, "file line"))
		w.at(11, time.Second)

		recs := w.step()
		held := w.flush()

		requireMessages(t, "lines of the run", messages(recs), []string{"file line"})
		requireMessages(t, "lines of the flush", messages(held), []string{"journal header"})
	})
	t.Run("a file header and a journal line", func(t *testing.T) {
		w := newWorld(t)
		w.useFiles("mail.log")
		w.writeLog("mail.log", "")
		w.online(seqID(1), []map[string][]byte{jentry(9, "before")})
		w.position(seqID(1), 9)
		w.start()
		w.step()
		w.online(seqID(1), []map[string][]byte{jentry(9, "before"), mariaEntry(10, "journal line")})
		w.appendLog("mail.log", fileLineAs(11, "mariadbd", 2345, header("Note", "file header")))
		w.at(11, time.Second)

		recs := w.step()
		held := w.flush()

		requireMessages(t, "lines of the run", messages(recs), []string{"journal line"})
		requireMessages(t, "lines of the flush", messages(held), []string{"file header"})
	})
}

func TestLogs_UserJournalGetsMailClassificationOnly(t *testing.T) {
	w := newWorld(t)
	user := seqID(2)
	w.writeJournal(journalSpec{name: "user-1000.journal", seqnumID: user, online: true, entries: []map[string][]byte{
		jentry(101, "fatal: user", "SYSLOG_IDENTIFIER", "postfix/postfix-script"),
		mariaEntry(102, header("ERROR", "user header")),
		mariaEntry(103, "user cont"),
		kernelEntry(104, oomStart),
	}})
	w.position(user, 100)
	w.start()
	w.at(104, time.Second)

	recs := w.step()
	held := w.flush()

	requireMessages(t, "lines of the run", messages(recs), []string{"fatal: user", header("ERROR", "user header"), "user cont", oomStart})
	if l := logLines(recs); len(l) == 4 {
		if l[0].Event != "postfix.fatal" || l[0].Priority == nil || *l[0].Priority != 2 {
			t.Errorf("the Postfix line = event %q priority %v, want postfix.fatal and 2: user files get the mail classification", l[0].Event, l[0].Priority)
		}
		if l[1].Event != "" {
			t.Errorf("the MariaDB header of a user file has the event %q, want none", l[1].Event)
		}
	}
	requireNoLines(t, "lines of the flush", held)
}

func TestLogs_UserFileEntriesOfASharedIDAreNotGrouped(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	w.writeJournal(journalSpec{name: "system.journal", seqnumID: id, online: true, entries: []map[string][]byte{
		mariaEntry(2, header("Note", "system header")), mariaEntry(3, "system cont")}})
	w.writeJournal(journalSpec{name: "user-1000.journal", seqnumID: id, online: true, entries: []map[string][]byte{
		mariaEntry(4, header("Note", "user header")), mariaEntry(5, "user cont")}})
	w.position(id, 1)
	w.start()
	w.at(5, time.Second)

	recs := w.step()
	held := w.flush()

	got := messages(recs)
	slices.Sort(got)
	want := []string{header("Note", "user header"), "user cont"}
	slices.Sort(want)
	requireMessages(t, "lines of the run (the user file's lines are shipped as they are)", got, want)
	requireMessages(t, "lines of the flush", messages(held), []string{"system header\nsystem cont"})
}

func TestLogs_GapEndsTheOpenGroups(t *testing.T) {
	t.Run("a truncated file", func(t *testing.T) {
		w := newWorld(t)
		w.useFiles("mail.log")
		w.writeLog("mail.log", fileLines(1, 2))
		w.clock.set(fileLineTime(3))
		w.start()
		w.step()
		w.appendLog("mail.log", fileLineAs(5, "mariadbd", 2345, header("Note", "entry"))+fileLineAs(6, "mariadbd", 2345, "cont"))
		opened := w.step()
		w.writeLog("mail.log", fileLine(20, "x"))

		truncated := w.step()
		w.appendLog("mail.log", fileLineAs(21, "mariadbd", 2345, "after"))
		after := w.step()

		requireNoLines(t, "lines of the run that opened the entry", opened)
		got := messages(truncated)
		if len(got) < 2 || got[0] != "entry\ncont" {
			t.Fatalf("lines of the run that noticed the truncation = %v, want the entry first, then the lines of the new file", head(got))
		}
		firstGap := slices.IndexFunc(truncated, func(r model.Record) bool { _, ok := r.Data.(*model.Gap); return ok })
		firstLine := slices.IndexFunc(truncated, func(r model.Record) bool { _, ok := r.Data.(*model.LogLine); return ok })
		if firstGap < 0 || firstLine < 0 || firstLine > firstGap {
			t.Errorf("positions in the run: first entry record %d, gap %d, want the entry sent before the gap", firstLine, firstGap)
		}
		requireMessages(t, "lines of the next run", messages(after), []string{"after"})
	})
	t.Run("a give-up", func(t *testing.T) {
		w := newWorld(t)
		id := seqID(1)
		feed := &journalFeed{w: w, id: id}
		feed.add(jentry(1, "before"), mariaEntry(2, header("Note", "entry")), mariaEntry(3, "cont"))
		w.position(id, 0)
		w.start()
		w.at(3, time.Second)
		for range 4 {
			w.collect()
			w.refuse()
		}

		recs := w.collect()

		firstGap := slices.IndexFunc(recs, func(r model.Record) bool { _, ok := r.Data.(*model.Gap); return ok })
		entry := slices.IndexFunc(recs, func(r model.Record) bool {
			l, ok := r.Data.(*model.LogLine)
			return ok && l.Message == "entry\ncont"
		})
		if firstGap < 0 || entry < 0 || entry > firstGap {
			t.Errorf("positions in the run after the give-up: entry %d, gap %d, want the entry sent before the give-up gap; lines %v", entry, firstGap, head(messages(recs)))
		}
	})
}

func TestLogs_RefusedGroupBecomesAGapOverTheGroup(t *testing.T) {
	restore := logship.SwapRecordCheck(func(r *model.Record) (int, error) {
		if l, ok := r.Data.(*model.LogLine); ok && strings.Contains(l.Message, "poison") {
			return 0, fmt.Errorf("refused by the test")
		}
		return wire.CheckRecord(r)
	})
	t.Cleanup(restore)
	w := newWorld(t)
	w.online(seqID(1), []map[string][]byte{jentry(1, "before"), mariaEntry(2, header("Note", "poison")), mariaEntry(3, "cont")})
	w.position(seqID(1), 0)
	w.start()
	w.at(3, time.Second)
	first := w.step()
	w.at(2, 61*time.Second)

	closed := w.step()

	requireMessages(t, "lines of the first run", messages(first), []string{"before"})
	requireNoLines(t, "lines of the run that closed the group", closed)
	requireGap(t, oneGap(t, "the run that closed the group", closed), entryTime(1), entryTime(2))
}

// classified is a line of the golden files classify-records.jsonl and classify-syslog.records.jsonl.
type classified struct {
	CapturedAtUS int64  `json:"captured_at_us"`
	Source       string `json:"source"`
	Host         string `json:"host"`
	Program      string `json:"program"`
	PID          int32  `json:"pid"`
	Priority     *uint8 `json:"priority"`
	Message      string `json:"message"`
	Truncated    bool   `json:"truncated"`
	Event        string `json:"event"`
}

func readClassified(t *testing.T, path string) []classified {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("reading %s: %v", path, err)
	}
	var out []classified
	for _, line := range bytes.Split(bytes.TrimSpace(data), []byte("\n")) {
		var c classified
		if err := json.Unmarshal(line, &c); err != nil {
			t.Fatalf("%s line %.80q: %v", path, line, err)
		}
		out = append(out, c)
	}
	return out
}

// requireClassifiedGolden compares the log lines of recs with the golden records in order of capture time, then program, then
// message, on every field but the log, the origin and the sequence number.
func requireClassifiedGolden(t *testing.T, recs []model.Record, golden []classified) {
	t.Helper()
	var got []classified
	for _, r := range recs {
		if l, ok := r.Data.(*model.LogLine); ok {
			got = append(got, classified{CapturedAtUS: r.CapturedAt.UnixMicro(), Source: r.Source, Host: l.Host, Program: l.Program, PID: l.PID,
				Priority: l.Priority, Message: l.Message, Truncated: l.Truncated, Event: l.Event})
		}
	}
	order := func(s []classified) {
		sort.SliceStable(s, func(i, j int) bool {
			a, b := s[i], s[j]
			if a.CapturedAtUS != b.CapturedAtUS {
				return a.CapturedAtUS < b.CapturedAtUS
			}
			if a.Program != b.Program {
				return a.Program < b.Program
			}
			return a.Message < b.Message
		})
	}
	order(got)
	order(golden)
	if len(got) != len(golden) {
		t.Fatalf("the collector returned %d log lines, want the %d records of the golden file", len(got), len(golden))
	}
	for i, want := range golden {
		g := got[i]
		same := g.CapturedAtUS == want.CapturedAtUS && g.Source == want.Source && g.Host == want.Host && g.Program == want.Program && g.PID == want.PID &&
			g.Message == want.Message && g.Truncated == want.Truncated && g.Event == want.Event && (g.Priority == nil) == (want.Priority == nil) &&
			(want.Priority == nil || *g.Priority == *want.Priority)
		if !same {
			t.Errorf("record %d = at %d source %q program %q pid %d priority %v truncated %v event %q message %.60q (%d bytes), want at %d source %q program %q pid %d priority %v truncated %v event %q message %.60q (%d bytes)",
				i, g.CapturedAtUS, g.Source, g.Program, g.PID, g.Priority, g.Truncated, g.Event, g.Message, len(g.Message),
				want.CapturedAtUS, want.Source, want.Program, want.PID, want.Priority, want.Truncated, want.Event, want.Message, len(want.Message))
		}
	}
}

// goldenClock is the time of reading of the classification fixtures, after the last line of both.
var goldenClock = time.Date(2026, time.October, 31, 10, 30, 0, 0, time.UTC)

func TestLogs_ClassifiedJournalEqualsTheImport(t *testing.T) {
	f, err := os.Open(journalFixture("classify.export"))
	if err != nil {
		t.Fatalf("opening classify.export: %v", err)
	}
	defer func() { _ = f.Close() }()
	export, err := journaltest.ReadExport(f)
	if err != nil {
		t.Fatalf("ReadExport(classify.export) error = %v, want nil", err)
	}
	golden := readClassified(t, journalFixture("classify-records.jsonl"))
	w := newWorld(t)
	id := seqID(0x0e)
	w.cfg.MinPriority = 7
	w.online(id, export)
	w.positionAt(id, 0, time.UnixMicro(golden[0].CapturedAtUS-1_000_000).UTC())
	w.clock.set(goldenClock)
	w.start()

	recs := w.drain(10)
	recs = append(recs, w.flush()...)

	requireClassifiedGolden(t, recs, golden)
}

func TestLogs_ClassifiedSyslogFileEqualsTheImport(t *testing.T) {
	data, err := os.ReadFile(fmt.Sprintf("%s/%s", "../../testdata/logs", "classify-syslog.log"))
	if err != nil {
		t.Fatalf("reading classify-syslog.log: %v", err)
	}
	golden := readClassified(t, "../../testdata/logs/classify-syslog.records.jsonl")
	w := newWorld(t)
	w.cfg.MinPriority = 7
	w.useFiles("classify.log")
	w.writeLog("classify.log", "")
	w.clock.set(goldenClock)
	w.start()
	w.step()
	w.appendLog("classify.log", string(data))

	recs := w.drain(10)
	recs = append(recs, w.flush()...)

	raw := classified{CapturedAtUS: time.Date(2026, time.October, 31, 10, 3, 10, 250000000, time.UTC).UnixMicro(), Source: "syslog", Message: "this line is no syslog line at all"}
	requireClassifiedGolden(t, recs, append(golden, raw))
}

func TestLogs_RecoveryEndAfterTheStartEntryExpiredGetsItsEvent(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	feed := &journalFeed{w: w, id: id}
	feed.add(mariaEntry(10, header("Note", "InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234")))
	w.position(id, 9)
	w.start()
	w.at(10, time.Second)
	opened := w.step()
	w.at(10, 61*time.Second)
	started := w.step()
	feed.add(mariaEntry(200, header("Note", "InnoDB: 10.6.12 started; log sequence number 8409876; transaction id 5678")))
	w.at(200, time.Second)
	opened2 := w.step()
	w.at(200, 61*time.Second)

	ended := w.step()

	requireNoLines(t, "lines of the run that opened the start entry", opened)
	if l := requireOneLine(t, "the run that closed the start entry", started, "mariadbd"); l.Event != "mariadb.recovery_start" {
		t.Errorf("start entry event = %q, want mariadb.recovery_start", l.Event)
	}
	requireNoLines(t, "lines of the run that opened the end entry", opened2)
	if l := requireOneLine(t, "the run that closed the end entry", ended, "mariadbd"); l.Event != "mariadb.recovery_end" {
		t.Errorf("end entry event = %q, want mariadb.recovery_end: the recovery flag lives as long as the source, not the stages", l.Event)
	}
}

func TestLogs_RefusedJournalFileClosesTheGroups(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	feed := &journalFeed{w: w, id: id}
	feed.add(jentry(1, "before"), mariaEntry(2, header("Note", "entry")), mariaEntry(3, "cont"))
	w.position(id, 0)
	w.start()
	w.at(3, time.Second)
	opened := w.step()
	// a second file of the machine directory that the reader refuses
	w.writeJournal(journalSpec{name: "system@00000000000000000000000000000009-0000000000000001-0005f8d1b1a2c3d4.journal", seqnumID: seqID(9), entries: jentries(1, 2)})
	bad := filepath.Join(w.jroot, machineID, "system@00000000000000000000000000000009-0000000000000001-0005f8d1b1a2c3d4.journal")
	if err := os.WriteFile(bad, []byte("not a journal file"), 0o600); err != nil {
		t.Fatalf("writing the damaged journal file: %v", err)
	}

	recs := w.step()

	requireMessages(t, "lines of the run that opened the entry", messages(opened), []string{"before"})
	if got := messages(recs); !slices.Contains(got, "entry\ncont") {
		t.Errorf("lines of the run that met the refused file = %v, want the open entry sent (the refused file may hold lines of any ID)", head(got))
	}
}

func TestLogs_RenamedFileClosesItsGroupsAndKeepsThePositionHeldBack(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.writeLog("mail.log", fileLines(1, 2))
	w.clock.set(fileLineTime(3))
	w.start()
	w.step()
	w.appendLog("mail.log", fileLineAs(5, "mariadbd", 2345, header("Note", "entry"))+fileLineAs(6, "mariadbd", 2345, "cont"))
	opened := w.step()
	w.rename("mail.log", "mail.log.1")
	w.writeLog("mail.log", fileLineAs(7, "app", 7, "new file"))

	recs := w.step()

	requireNoLines(t, "lines of the run that opened the entry", opened)
	got := messages(recs)
	if len(got) == 0 || got[0] != "entry\ncont" {
		t.Errorf("lines of the run that read the renamed file to its end = %v, want the entry first (the import groups per file)", head(got))
	}
}

func TestLogs_FlushAfterAGiveUpClosesTheGroupsOfTheDroppedFork(t *testing.T) {
	w := newWorld(t)
	id := seqID(1)
	feed := &journalFeed{w: w, id: id}
	feed.add(jentry(1, "before"), mariaEntry(2, header("Note", "entry")), mariaEntry(3, "cont"))
	w.position(id, 0)
	w.start()
	w.at(3, time.Second)
	for range 4 {
		w.collect()
		w.refuse()
	}

	flushed := w.flush()

	if got := messages(flushed); !slices.Contains(got, "entry\ncont") {
		t.Errorf("lines of the flush after four dropped runs = %v, want the open entry of the state the next run starts from", head(got))
	}
}

func TestLogs_FlushHoldsBackNothingAfterAKeptFlushOfAFile(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.writeLog("mail.log", fileLines(1, 2))
	w.clock.set(fileLineTime(3))
	w.start()
	w.step()
	w.appendLog("mail.log", fileLineAs(5, "mariadbd", 2345, header("Note", "entry"))+fileLineAs(6, "mariadbd", 2345, "cont")+fileLineAs(7, "app", 7, "later"))
	first := w.step()
	held := w.store.State().Logs
	flushed := w.flush()
	w.keep()
	w.restart()
	w.appendLog("mail.log", fileLineAs(8, "app", 7, "after"))
	next := w.step()

	requireMessages(t, "lines of the run that held the entry", messages(first), []string{"later"})
	requireMessages(t, "lines of the flush", messages(flushed), []string{"entry\ncont"})
	requireMessages(t, "lines of the new collector (nothing is repeated)", messages(next), []string{"after"})
	if held == nil || len(held.Files) != 1 || held.Files[0].Offset >= w.store.State().Logs.Files[0].Offset {
		t.Errorf("file positions held back %+v and after the kept flush %+v, want the offset to move on past the entry", held, w.store.State().Logs)
	}
}

func TestLogs_RenamedPartFinishedByTheLaterRunClosesItsGroups(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.writeLog("mail.log", fileLines(1, 2))
	w.clock.set(fileLineTime(3))
	w.start()
	w.step()
	w.rename("mail.log", "mail.log.1")
	w.appendLog("mail.log.1", strings.TrimSuffix(fileLineAs(5, "mariadbd", 2345, header("Note", "entry")), "\n")) // unfinished line
	w.writeLog("mail.log", fileLineAs(7, "app", 7, "new file"))
	runB := w.step()
	w.appendLog("mail.log", fileLineAs(8, "mariadbd", 2345, "cont of the new file"))

	runC := w.step()
	shipped := append(messages(runB), messages(runC)...)
	w.crash()
	w.clock.advance(time.Second)
	shipped = append(shipped, messages(w.step())...)
	w.clock.advance(61 * time.Second)
	shipped = append(shipped, messages(w.step())...)

	if got := messages(runC); !slices.Equal(got, []string{"entry", "cont of the new file"}) {
		t.Errorf("lines of the run that finished the renamed part = %v, want the entry and the line of the new file as two records (the import groups per file)", head(got))
	}
	for _, want := range []string{"entry", "cont of the new file"} {
		if !slices.Contains(shipped, want) {
			t.Errorf("lines shipped before and after a crash = %v, want %q among them: a held line is covered by the saved position", head(shipped), want)
		}
	}
}
