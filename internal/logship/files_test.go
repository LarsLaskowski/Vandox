package logship_test

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"regexp"
	"slices"
	"strings"
	"syscall"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/logship"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// fileMsgs returns the messages of the file lines among the records.
func fileMsgs(recs []model.Record) []string { return ofSource(recs, "syslog") }

// mailWorld returns a world that ships mail.log, whose first lines are 1 and 2, and has started the collector and made its first run.
func mailWorld(t *testing.T) *world {
	t.Helper()
	w := newWorld(t)
	w.useFiles("mail.log")
	w.writeLog("mail.log", fileLines(1, 2))
	w.start()
	if first := w.step(); len(first) != 0 {
		t.Fatalf("the first run returned %d records, want none", len(first))
	}
	return w
}

func TestFiles_ShipsLinesAsTheImportsSyslogMappingDoes(t *testing.T) {
	invalid := "Oct 23 07:13:04 web-1 app[2]: bad " + string([]byte{0xff}) + " byte\n"
	bidi := string(rune(0x202e))
	w := newWorld(t)
	w.useFiles("mail.log")
	w.writeLog("mail.log", "")
	w.start()
	w.step()
	w.appendLog("mail.log", "Oct 23 07:13:02 web-1 postfix/smtpd[4711]: connect from unknown[192.0.2.10]\n"+
		"<13>Oct 23 07:13:03 web-1 sshd[1]: with a priority\n"+
		"a continuation line without a header\n"+
		"\n"+
		invalid+
		"Oct 23 07:13:05 web-1 app[3]: bidi "+bidi+" kept\n")

	recs := w.drain(5)

	lines := logLines(recs)
	if len(lines) != 5 {
		t.Fatalf("the collector returned %d records %v, want 5 (the empty line is not shipped)", len(lines), head(messages(recs)))
	}
	path := w.logPath("mail.log")
	at := func(h, m, s int) time.Time { return time.Date(2026, time.October, 23, h, m, s, 0, time.UTC) }
	want := []struct {
		at                    time.Time
		host, program, prefix string
		pid                   int32
		priority              *uint8
		message               string
	}{
		{at(5, 13, 2), "web-1", "postfix/smtpd", "", 4711, nil, "connect from unknown[192.0.2.10]"},
		{at(5, 13, 3), "web-1", "sshd", "", 1, prio(5), "with a priority"},
		{at(5, 13, 3), "", "", "", 0, nil, "a continuation line without a header"}, // the time of the line before
		{at(5, 13, 4), "web-1", "app", "", 2, nil, "bad " + string(rune(0xfffd)) + " byte"},
		{at(5, 13, 5), "web-1", "app", "", 3, nil, "bidi " + bidi + " kept"},
	}
	for i, w := range want {
		l, r := lines[i], recs[i]
		if l.Log != path || r.Source != "syslog" || l.Host != w.host || l.Program != w.program || l.PID != w.pid || l.Message != w.message || l.Truncated || l.Event != "" ||
			(l.Priority == nil) != (w.priority == nil) || (w.priority != nil && *l.Priority != *w.priority) || !r.CapturedAt.Equal(w.at) {
			t.Errorf("record %d = source %q log %q host %q program %q pid %d priority %v message %q captured %v, want host %q program %q pid %d priority %v message %q captured %v",
				i, r.Source, l.Log, l.Host, l.Program, l.PID, l.Priority, l.Message, r.CapturedAt, w.host, w.program, w.pid, w.priority, w.message, w.at)
		}
	}
}

func prio(n uint8) *uint8 { return &n }

func TestFiles_HoldsSyslogFilesWhileTheZoneIsUnknown(t *testing.T) {
	tests := []struct {
		name   string
		damage func(w *world)
	}{
		{"the zone file is missing", func(w *world) { w.etc.remove("localtime") }},
		{"the zone file is no zone", func(w *world) { w.etc.set("localtime", []byte("not a zone file")) }},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			w := mailWorld(t)
			tt.damage(w)
			w.appendLog("mail.log", fileLines(3, 4))

			var during []model.Record
			for range 3 {
				during = append(during, w.step()...)
			}

			requireMessages(t, "file lines while the zone is unknown", fileMsgs(during), nil)
			requireNoGaps(t, "the runs while the zone is unknown", during)
			if n := len(w.logsOf("time zone not readable")); n != 1 {
				t.Errorf("%q was logged %d times in three runs, want once", "time zone not readable", n)
			}
			w.etc.set("localtime", tzFixed(utcPlus2))
			w.appendLog("mail.log", fileLines(5, 5))

			after := w.drain(5)

			requireMessages(t, "file lines after the zone is known again", fileMsgs(after), []string{"file 3", "file 4", "file 5"})
			requireNoGaps(t, "the runs after the zone is known again", after)
			if n := len(w.logsOf("time zone readable again")); n != 1 {
				t.Errorf("%q was logged %d times, want once", "time zone readable again", n)
			}
		})
	}
}

func TestFiles_ShipsTheJournalWhileTheZoneIsUnknown(t *testing.T) {
	w := mailWorld(t)
	w.online(seqID(1), jentries(1, 2))
	w.step()
	w.etc.remove("localtime")
	w.online(seqID(1), jentries(1, 4))

	recs := w.drain(5)

	requireMessages(t, "journal lines while the zone is unknown", ofSource(recs, "journal"), []string{"line 3", "line 4"})
}

// rotate returns a world in front of a rotation by rename: the run before it has shipped line 3, line 4 was written to the old file
// and is not shipped yet, the old file is renamed to mail.log.1 and the new mail.log holds line 10. all holds the records of the run
// before the rotation.
func rotate(t *testing.T) (w *world, all []model.Record) {
	t.Helper()
	w = mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 3))
	all = append(all, w.step()...)
	w.appendLog("mail.log", fileLines(4, 4))
	w.rename("mail.log", "mail.log.1")
	w.writeLog("mail.log", fileLines(10, 10))
	return w, all
}

func requireLogPath(t *testing.T, recs []model.Record, path string) {
	t.Helper()
	for _, l := range logLines(recs) {
		if l.Log != path {
			t.Errorf("record %q has the log %q, want %q (the configured path, also for lines of the renamed file)", l.Message, l.Log, path)
		}
	}
}

func TestFiles_RotationByRenameWhileRunning(t *testing.T) {
	w, all := rotate(t)

	r2 := w.step() // the run between the rename and the writer's last append to the renamed file
	w.appendLog("mail.log.1", fileLines(5, 5))
	w.appendLog("mail.log", fileLines(11, 11))
	r3 := w.step()
	w.appendLog("mail.log", fileLines(12, 12))
	r4 := w.step() // the renamed file did not grow during this run: the writer has moved
	w.appendLog("mail.log.1", fileLines(6, 6))
	w.appendLog("mail.log", fileLines(13, 13))
	r5 := w.step()

	requireMessages(t, "run after the rename", fileMsgs(r2), []string{"file 4", "file 10"})
	requireMessages(t, "run with a late append to the renamed file", fileMsgs(r3), []string{"file 5", "file 11"})
	requireMessages(t, "run in which the writer has moved", fileMsgs(r4), []string{"file 12"})
	requireMessages(t, "run after the renamed file was finished", fileMsgs(r5), []string{"file 13"})
	for name, recs := range map[string][]model.Record{"r2": r2, "r3": r3, "r4": r4, "r5": r5} {
		requireNoGaps(t, name, recs)
		requireLogPath(t, recs, w.logPath("mail.log"))
	}
	if len(all) != 1 {
		t.Errorf("the run before the rotation returned %d records, want line 3 only", len(all))
	}
}

func TestFiles_RenamedFileIsReadUntilTheWriterHasMoved(t *testing.T) {
	w := mailWorld(t)
	w.rename("mail.log", "mail.log.1")
	w.writeLog("mail.log", "") // the new file has no data yet
	var quiet []model.Record
	for range 4 {
		quiet = append(quiet, w.step()...)
	}
	w.appendLog("mail.log.1", fileLines(3, 3)) // the writer has not moved: it still writes to the renamed file
	late := w.step()
	w.appendLog("mail.log", fileLines(10, 10))
	moved := w.step()
	w.appendLog("mail.log", fileLines(11, 11))
	after := w.step()

	if len(quiet) != 0 {
		t.Errorf("runs while the renamed file did not grow and the new file had no data returned %v, want nothing", head(messages(quiet)))
	}
	requireMessages(t, "run with a late append to the renamed file", fileMsgs(late), []string{"file 3"})
	requireMessages(t, "run in which the new file got data", fileMsgs(moved), []string{"file 10"})
	requireMessages(t, "run after it", fileMsgs(after), []string{"file 11"})
	requireNoGaps(t, "the runs", append(append(late, moved...), after...))
}

func TestFiles_RenamedFileSurvivesARestart(t *testing.T) {
	w, _ := rotate(t)
	r2 := w.step()
	w.appendLog("mail.log.1", fileLines(5, 5))
	w.appendLog("mail.log", fileLines(11, 11))

	w.restart() // the renamed file is still being read
	r3 := w.step()
	w.appendLog("mail.log.1", fileLines(6, 6))
	r4 := w.step()

	requireMessages(t, "run after the rename", fileMsgs(r2), []string{"file 4", "file 10"})
	requireMessages(t, "run after the restart", fileMsgs(r3), []string{"file 5", "file 11"})
	requireMessages(t, "run with a further late append", fileMsgs(r4), []string{"file 6"})
	requireNoGaps(t, "the runs", append(append(r2, r3...), r4...))
}

func TestFiles_RotationSearchChecksTheOpenedFile(t *testing.T) {
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 3))
	w.step()
	w.stop()
	// while the agent is stopped the file it was reading gets a name the search does not know, and another file takes the name .1
	w.rename("mail.log", "mail.log.5")
	w.writeLog("mail.log.1", fileLines(50, 51))
	w.writeLog("mail.log", fileLines(10, 11))
	w.reopen()
	w.start()

	recs := w.drain(5)

	requireMessages(t, "file lines", fileMsgs(recs), []string{"file 10", "file 11"})
	if gaps := gapsOf(recs); len(gaps) != 1 {
		t.Fatalf("the collector returned %d gaps, want 1: the file with the name .1 is not the file the agent was reading", len(gaps))
	} else {
		requireGap(t, gaps[0], fileLineTime(3), fileLineTime(10))
	}
}

func TestFiles_RotationSearchRefusesALinkInTheDirectoryPath(t *testing.T) {
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 3))
	w.step()
	w.stop()
	w.appendLog("mail.log", fileLines(4, 4))
	w.rename("mail.log", "mail.log.1")
	w.writeLog("mail.log", fileLines(10, 11))
	moved, link := w.logDir+".moved", w.logDir
	if err := os.Rename(link, moved); err != nil {
		t.Fatalf("moving the log directory: %v", err)
	}
	if err := os.Symlink(moved, link); err != nil {
		t.Fatalf("linking the log directory: %v", err)
	}
	w.reopen()
	w.start()

	var refused []model.Record
	for range 2 {
		refused = append(refused, w.step()...)
	}

	if len(refused) != 0 {
		t.Errorf("runs with a link in the directory path returned %v and %d gaps, want nothing: the position stays", head(messages(refused)), len(gapsOf(refused)))
	}
	logs := w.logsOf("log file not readable")
	if len(logs) != 1 || logs[0].attrs["err"] != "link refused" || logs[0].attrs["file"] != "0" {
		t.Errorf("warnings %q = %+v, want one with file 0 and err %q", "log file not readable", logs, "link refused")
	}
	if err := os.Remove(link); err != nil {
		t.Fatalf("removing the link: %v", err)
	}
	if err := os.Rename(moved, link); err != nil {
		t.Fatalf("restoring the log directory: %v", err)
	}

	recs := w.drain(5)

	requireMessages(t, "file lines once the directory is no link", fileMsgs(recs), []string{"file 4", "file 10", "file 11"})
	requireNoGaps(t, "the runs once the directory is no link", recs)
}

func TestFiles_RotationWhileStoppedFindsTheRenamedFile(t *testing.T) {
	for _, renamed := range []string{"mail.log.1", "mail.log-20261022"} {
		t.Run(renamed, func(t *testing.T) {
			w := mailWorld(t)
			w.appendLog("mail.log", fileLines(3, 3))
			w.step()
			w.stop()
			w.appendLog("mail.log", fileLines(4, 4)) // written while the agent is stopped
			w.rename("mail.log", renamed)
			w.writeLog("mail.log", fileLines(10, 11))
			w.reopen()
			w.start()

			recs := w.drain(5)

			requireMessages(t, "file lines", fileMsgs(recs), []string{"file 4", "file 10", "file 11"})
			requireNoGaps(t, "the runs", recs)
			requireLogPath(t, recs, w.logPath("mail.log"))
		})
	}
}

func TestFiles_RotationWithoutTheOldFileIsAGap(t *testing.T) {
	tests := []struct {
		name string
		hide func(w *world)
	}{
		{"the old file was deleted", func(w *world) {
			if err := os.Remove(w.logPath("mail.log")); err != nil {
				t.Fatalf("removing the old file: %v", err)
			}
		}},
		{"the old file was compressed and renamed otherwise", func(w *world) { w.rename("mail.log", "mail.log.2.gz") }},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			w := mailWorld(t)
			w.appendLog("mail.log", fileLines(3, 3))
			w.step()
			w.stop()
			tt.hide(w)
			w.writeLog("mail.log", fileLines(10, 11))
			w.reopen()
			w.start()

			recs := w.drain(5)

			requireMessages(t, "file lines", fileMsgs(recs), []string{"file 10", "file 11"})
			if gaps := gapsOf(recs); len(gaps) != 1 {
				t.Fatalf("the collector returned %d gaps, want 1", len(gaps))
			} else {
				requireGap(t, gaps[0], fileLineTime(3), fileLineTime(10))
			}
		})
	}
}

func TestFiles_TruncationRestartsWithOneGap(t *testing.T) {
	t.Run("after shipped lines", truncationAfterShippedLines)
	t.Run("nothing shipped yet", truncationBeforeAnyLine)
	t.Run("the last line is dated after the start of the run", truncationAfterAFutureLine)
}

func truncationAfterShippedLines(t *testing.T) {
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 5))
	if got := fileMsgs(w.step()); !slices.Equal(got, []string{"file 3", "file 4", "file 5"}) {
		t.Fatalf("lines before the truncation = %v, want file 3 to file 5", got)
	}
	w.writeLog("mail.log", fileLines(20, 20)) // truncated in place, then written to again

	recs := w.step()

	requireMessages(t, "file lines", fileMsgs(recs), []string{"file 20"})
	requireGap(t, oneGap(t, "the run that noticed the truncation", recs), fileLineTime(5), w.run.Start)
	w.appendLog("mail.log", fileLines(21, 21))
	next := w.step()
	requireMessages(t, "file lines of the next run", fileMsgs(next), []string{"file 21"})
	requireNoGaps(t, "the next run", next)
}

func truncationBeforeAnyLine(t *testing.T) {
	w := mailWorld(t) // the first run took the position at the end of the two lines and shipped none
	taken := w.run.Start
	w.writeLog("mail.log", fileLines(20, 20)) // shorter than the two lines before

	recs := w.step()

	requireMessages(t, "file lines", fileMsgs(recs), []string{"file 20"})
	requireGap(t, oneGap(t, "the run that noticed the truncation", recs), taken, w.run.Start)
}

func truncationAfterAFutureLine(t *testing.T) {
	future := fileLineTime(5400) // ten minutes after the time of reading
	if !future.After(startTime.Add(5 * time.Minute)) {
		t.Fatalf("the test line is dated %v, want it well after the start %v", future, startTime)
	}
	w := mailWorld(t)
	w.appendLog("mail.log", fileLine(5400, "future")+fileLine(5401, "later"))
	w.step()
	w.writeLog("mail.log", fileLine(20, "x"))

	recs := w.step()

	requireGap(t, oneGap(t, "the run that noticed the truncation", recs), fileLineTime(5401), fileLineTime(5401).Add(time.Microsecond))
}

func TestFiles_TruncationRefilledPastThePositionIsDetected(t *testing.T) {
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 5))
	w.step()
	w.writeLog("mail.log", fileLines(30, 45)) // longer than the position when the next run looks at the file

	recs := w.step()

	requireMessages(t, "file lines", fileMsgs(recs), numbered("file", 30, 45))
	if gaps := gapsOf(recs); len(gaps) != 1 {
		t.Fatalf("the run holds %d gaps, want 1", len(gaps))
	} else {
		requireGap(t, gaps[0], fileLineTime(5), w.run.Start)
	}
}

func TestFiles_AddedFileStartsAtItsEnd(t *testing.T) {
	w := mailWorld(t)
	w.writeLog("other.log", fileLines(1, 3))
	w.appendLog("mail.log", fileLines(3, 3))
	w.step()
	w.stop()
	w.appendLog("mail.log", fileLines(4, 4))

	w.useFiles("mail.log", "other.log") // other.log is added to the configuration
	w.reopen()
	w.start()
	first := w.drain(5)
	w.appendLog("other.log", fileLines(4, 4))
	second := w.drain(5)

	requireMessages(t, "lines of the first runs after the start", fileMsgs(first), []string{"file 4"})
	requireMessages(t, "lines of the added file written after the start", fileMsgs(second), []string{"file 4"})
	if len(logLines(second)) != 1 || logLines(second)[0].Log != w.logPath("other.log") {
		t.Errorf("records of the later runs = %v, want the one line of the added file", head(messages(second)))
	}

	// a removed file's position is forgotten: when it is added again it starts at its end
	w.stop()
	w.useFiles("other.log")
	w.reopen()
	w.start()
	w.step()
	w.appendLog("mail.log", fileLines(5, 6))
	w.stop()
	w.useFiles("mail.log", "other.log")
	w.reopen()
	w.start()
	again := w.drain(5)
	if len(again) != 0 {
		t.Errorf("runs after the file was removed and added again returned %v, want nothing: it starts at its end", head(messages(again)))
	}
}

func TestFiles_FileThatAppearsLaterIsReadFromItsStart(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.start()
	for range 2 {
		if recs := w.step(); len(recs) != 0 {
			t.Fatalf("a run with a missing file returned %d records, want none", len(recs))
		}
	}
	logs := w.logsOf("log file not readable")
	if len(logs) != 1 || logs[0].attrs["file"] != "0" || logs[0].attrs["err"] != "missing" {
		t.Errorf("warnings %q = %+v, want one with file 0 and err %q", "log file not readable", logs, "missing")
	}
	w.writeLog("mail.log", fileLines(1, 3))

	recs := w.drain(5)

	requireMessages(t, "file lines", fileMsgs(recs), []string{"file 1", "file 2", "file 3"})
	requireNoGaps(t, "the runs", recs)
	if n := len(w.logsOf("log file readable again")); n != 1 {
		t.Errorf("%q was logged %d times, want once", "log file readable again", n)
	}
}

// blocker puts an obstacle in the place of mail.log, which the test has renamed to mail.log.keep.
type blocker func(t *testing.T, w *world)

func blockNothing(*testing.T, *world) {}

func blockWithADirectory(t *testing.T, w *world) {
	t.Helper()
	if err := os.Mkdir(w.logPath("mail.log"), 0o700); err != nil {
		t.Fatalf("making a directory: %v", err)
	}
}

func blockWithAFIFO(t *testing.T, w *world) {
	t.Helper()
	if err := syscall.Mkfifo(w.logPath("mail.log"), 0o600); err != nil {
		t.Fatalf("making a FIFO: %v", err)
	}
}

func blockWithALink(t *testing.T, w *world) {
	t.Helper()
	if err := os.Symlink(w.logPath("mail.log.keep"), w.logPath("mail.log")); err != nil {
		t.Fatalf("making a link: %v", err)
	}
}

func blockWithAHardLink(t *testing.T, w *world) {
	t.Helper()
	w.writeLog("other.txt", "a file that must not be shipped\n")
	if err := os.Link(w.logPath("other.txt"), w.logPath("mail.log")); err != nil {
		t.Fatalf("making a hard link: %v", err)
	}
}

func TestFiles_UnreadableFileKeepsItsPosition(t *testing.T) {
	tests := []struct {
		name    string
		wantErr string
		block   blocker
	}{
		{"missing", "missing", blockNothing},
		{"a directory", "not a regular file", blockWithADirectory},
		{"a FIFO", "not a regular file", blockWithAFIFO},
		{"a symbolic link", "link refused", blockWithALink},
		{"a hard link to another file", "link refused", blockWithAHardLink},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) { runBlockedFile(t, tt.wantErr, tt.block) })
	}
}

// runBlockedFile hides mail.log behind an obstacle for three runs, checks that nothing is shipped or recorded and that one warning
// with the reason is logged, and then gives the file back: the lines written meanwhile are shipped once, without a gap.
func runBlockedFile(t *testing.T, wantErr string, block blocker) {
	t.Helper()
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 3))
	w.step()
	w.rename("mail.log", "mail.log.keep")
	block(t, w)
	w.appendLog("mail.log.keep", fileLines(4, 4))

	var hidden []model.Record
	for range 3 {
		hidden = append(hidden, w.step()...)
	}

	if len(hidden) != 0 {
		t.Errorf("runs on an unreadable file returned %v and %d gaps, want nothing", head(messages(hidden)), len(gapsOf(hidden)))
	}
	logs := w.logsOf("log file not readable")
	if len(logs) != 1 || logs[0].attrs["file"] != "0" || logs[0].attrs["err"] != wantErr {
		t.Errorf("warnings %q = %+v, want one with file 0 and err %q", "log file not readable", logs, wantErr)
	}
	if err := os.RemoveAll(w.logPath("mail.log")); err != nil {
		t.Fatalf("removing the obstacle: %v", err)
	}
	w.rename("mail.log.keep", "mail.log")
	w.appendLog("mail.log", fileLines(5, 5))

	recs := w.drain(5)

	requireMessages(t, "file lines once the file is readable again", fileMsgs(recs), []string{"file 4", "file 5"})
	requireNoGaps(t, "the runs once the file is readable again", recs)
	if n := len(w.logsOf("log file readable again")); n != 1 {
		t.Errorf("%q was logged %d times, want once", "log file readable again", n)
	}
}

func TestFiles_PartialLineWaitsForItsLineFeed(t *testing.T) {
	w := mailWorld(t)
	line := fileLine(3, "partial line")
	w.appendLog("mail.log", line[:len(line)-8]) // the writer is in the middle of the line

	var waiting []model.Record
	for range 3 {
		waiting = append(waiting, w.step()...)
	}
	w.appendLog("mail.log", line[len(line)-8:])
	done := w.step()

	if len(waiting) != 0 {
		t.Errorf("runs on a partial line returned %v, want nothing", head(messages(waiting)))
	}
	requireMessages(t, "file lines once the line feed arrived", fileMsgs(done), []string{"partial line"})
	if again := w.step(); len(again) != 0 {
		t.Errorf("the next run returned %v, want nothing (the line is shipped once)", head(messages(again)))
	}
}

func TestFiles_PartialLineIsShippedWhenTheWriterHasMoved(t *testing.T) {
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 3))
	w.step()
	partial := fileLine(4, "tail")
	w.appendLog("mail.log", partial[:len(partial)-1]) // no line feed yet
	w.rename("mail.log", "mail.log.1")
	w.writeLog("mail.log", fileLines(10, 10))

	r2 := w.step()
	w.appendLog("mail.log", fileLines(11, 11))
	r3 := w.step()
	w.appendLog("mail.log", fileLines(12, 12))
	r4 := w.step()

	got := append(append(fileMsgs(r2), fileMsgs(r3)...), fileMsgs(r4)...)
	early := []string{"tail", "file 10", "file 11", "file 12"}
	late := []string{"file 10", "tail", "file 11", "file 12"}
	if !slices.Equal(got, early) && !slices.Equal(got, late) {
		t.Errorf("file lines of the three runs after the rotation = %v, want the partial last line of the renamed file shipped once as it is, before the lines the new file got after the writer moved, in %v or %v", got, early, late)
	}
	requireNoGaps(t, "the runs", append(append(r2, r3...), r4...))
}

func TestFiles_DroppedRunResolvesTheRepeatedHourAlike(t *testing.T) {
	w := newWorld(t)
	w.etc.set("localtime", tzCentralEurope())
	w.clock.now = time.Date(2026, time.October, 26, 8, 0, 0, 0, time.UTC) // past the fall-back of 25 October
	w.useFiles("mail.log")
	w.writeLog("mail.log", "")
	w.start()
	w.step()
	w.appendLog("mail.log", "Oct 25 02:30:00 web-1 app[7]: first\nOct 25 02:05:00 web-1 app[7]: second\n")

	refused := w.collect()
	w.refuse()
	again := w.collect()
	w.keep()

	wantTimes := []time.Time{time.Date(2026, time.October, 25, 0, 30, 0, 0, time.UTC), time.Date(2026, time.October, 25, 1, 5, 0, 0, time.UTC)}
	for name, recs := range map[string][]model.Record{"refused run": refused, "re-read": again} {
		if got := messages(recs); !slices.Equal(got, []string{"first", "second"}) {
			t.Fatalf("lines of the %s = %v, want first and second", name, got)
		}
		for i, r := range recs {
			if !r.CapturedAt.Equal(wantTimes[i]) {
				t.Errorf("line %d of the %s captured at %v, want %v: a dropped run must not move the previous instant the re-read starts from", i, name, r.CapturedAt, wantTimes[i])
			}
		}
	}
}

func TestFiles_BacklogIsShippedAcrossRunsWithoutRepeats(t *testing.T) {
	const backlog = 1500
	t.Run("1,500 lines ship as 1,000 and 500", func(t *testing.T) {
		w := mailWorld(t)
		w.appendLog("mail.log", fileLines(3, backlog+2))

		first, second, third := w.step(), w.step(), w.step()

		requireMessages(t, "lines of the first run", fileMsgs(first), numbered("file", 3, 1002))
		requireMessages(t, "lines of the second run", fileMsgs(second), numbered("file", 1003, backlog+2))
		if len(third) != 0 {
			t.Errorf("the third run returned %v, want nothing: no line is shipped twice", head(messages(third)))
		}
		requireNoGaps(t, "the runs", append(append(first, second...), third...))
	})
	t.Run("a partial last line waits behind the stop", func(t *testing.T) {
		w := mailWorld(t)
		partial := fileLine(backlog+3, "partial line")
		w.appendLog("mail.log", fileLines(3, backlog+2)+partial[:len(partial)-8])

		first, second := w.step(), w.step()
		w.appendLog("mail.log", partial[len(partial)-8:])
		third, fourth := w.step(), w.step()

		requireMessages(t, "lines of the first run", fileMsgs(first), numbered("file", 3, 1002))
		requireMessages(t, "lines of the second run", fileMsgs(second), numbered("file", 1003, backlog+2))
		requireMessages(t, "lines of the run after the line feed arrived", fileMsgs(third), []string{"partial line"})
		if len(fourth) != 0 {
			t.Errorf("the run after it returned %v, want nothing", head(messages(fourth)))
		}
	})
	t.Run("a restart between the runs continues behind the 1,000th line", func(t *testing.T) {
		w := mailWorld(t)
		w.appendLog("mail.log", fileLines(3, backlog+2))
		first := w.step()

		w.restart()
		rest := w.drain(5)

		requireMessages(t, "lines of the first run", fileMsgs(first), numbered("file", 3, 1002))
		requireMessages(t, "lines after the restart", fileMsgs(rest), numbered("file", 1003, backlog+2))
		requireNoGaps(t, "the runs", append(first, rest...))
	})
	t.Run("a dropped run reads its 1,000 lines again", func(t *testing.T) {
		w := mailWorld(t)
		w.appendLog("mail.log", fileLines(3, backlog+2))
		w.collect()
		w.refuse()

		again := w.step()
		rest := w.step()

		requireMessages(t, "lines of the run that repeats the dropped one", fileMsgs(again), numbered("file", 3, 1002))
		requireMessages(t, "lines of the run after it", fileMsgs(rest), numbered("file", 1003, backlog+2))
	})
}

func TestFiles_SecondRotationFinishesTheRenamedFileFirst(t *testing.T) {
	w, _ := rotate(t)
	r2 := w.step() // line 4 of the first renamed file and line 10 of the second file
	partial := fileLine(6, "last words")
	w.appendLog("mail.log.1", fileLines(5, 5)+partial[:len(partial)-1]) // the writer never ended its last line
	w.appendLog("mail.log", fileLines(11, 11))
	w.rename("mail.log.1", "mail.log.2")
	w.rename("mail.log", "mail.log.1")
	w.writeLog("mail.log", fileLines(20, 20)) // a second rotation, before the first renamed file was finished

	r3 := w.step()
	w.appendLog("mail.log.1", fileLines(12, 12)) // the writer is late again, now with the second file
	w.appendLog("mail.log", fileLines(21, 21))
	r4 := w.step()
	w.appendLog("mail.log", fileLines(22, 22))
	r5 := w.step()
	r6 := w.step()

	requireMessages(t, "run after the first rotation", fileMsgs(r2), []string{"file 4", "file 10"})
	requireMessages(t, "run after the second rotation", fileMsgs(r3), []string{"file 5", "last words", "file 11", "file 20"})
	requireMessages(t, "run with a late append to the second renamed file", fileMsgs(r4), []string{"file 12", "file 21"})
	requireMessages(t, "run in which the second writer has moved", fileMsgs(r5), []string{"file 22"})
	if len(r6) != 0 {
		t.Errorf("the run after it returned %v, want nothing: no line is shipped twice", head(messages(r6)))
	}
	requireNoGaps(t, "the runs", append(append(append(r2, r3...), r4...), r5...))
	requireLogPath(t, append(append(r3, r4...), r5...), w.logPath("mail.log"))
}

func TestFiles_LostRotationWithoutALineYetEndsTheGapAtTheRunStart(t *testing.T) {
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 3))
	w.step()
	w.stop()
	if err := os.Remove(w.logPath("mail.log")); err != nil {
		t.Fatalf("removing the old file: %v", err)
	}
	w.writeLog("mail.log", "") // the new file has no line yet
	w.reopen()
	w.start()

	first := w.step()
	start := w.run.Start
	w.appendLog("mail.log", fileLines(10, 10))
	second := w.step()

	requireMessages(t, "lines of the run without a line in the new file", fileMsgs(first), nil)
	requireGap(t, oneGap(t, "the run without a line in the new file", first), fileLineTime(3), start)
	requireMessages(t, "lines of the next run", fileMsgs(second), []string{"file 10"})
	requireNoGaps(t, "the next run", second)
}

func TestFiles_RenamedFileThatIsGoneAtARestartIsAGap(t *testing.T) {
	t.Run("the renamed file was deleted", func(t *testing.T) {
		w, _ := rotate(t)
		w.step() // the saved position now holds the renamed file
		w.stop()
		if err := os.Remove(w.logPath("mail.log.1")); err != nil {
			t.Fatalf("removing the renamed file: %v", err)
		}
		w.appendLog("mail.log", fileLines(11, 11))
		w.reopen()
		w.start()

		first := w.step()
		start := w.run.Start
		second := w.step()

		requireMessages(t, "lines of the first run", fileMsgs(first), []string{"file 11"})
		requireGap(t, oneGap(t, "the first run", first), fileLineTime(10), start)
		requireNoGaps(t, "the next run", second)
	})
	t.Run("the renamed file is behind a link", func(t *testing.T) {
		w, _ := rotate(t)
		w.step()
		w.stop()
		moved := w.logDir + ".moved"
		if err := os.Rename(w.logDir, moved); err != nil {
			t.Fatalf("moving the log directory: %v", err)
		}
		if err := os.Symlink(moved, w.logDir); err != nil {
			t.Fatalf("linking the log directory: %v", err)
		}
		w.reopen()
		w.start()

		var refused []model.Record
		for range 2 {
			refused = append(refused, w.step()...)
		}

		if len(refused) != 0 {
			t.Errorf("runs with a link in the directory path returned %v and %d gaps, want nothing: the position stays", head(messages(refused)), len(gapsOf(refused)))
		}
		logs := w.logsOf("log file not readable")
		if len(logs) != 1 || logs[0].attrs["err"] != "link refused" {
			t.Errorf("warnings %q = %+v, want one with err %q", "log file not readable", logs, "link refused")
		}
	})
}

// openRecorder makes the world open its files through OpenNoLinks and keep the files it was given; failWith, when set, is the error
// of every open.
type openRecorder struct {
	opened   []*os.File
	failWith error
	// closeFirst makes the open hand over a file that was closed already.
	closeFirst bool
}

func (o *openRecorder) open(path string) (*os.File, error) {
	if o.failWith != nil {
		return nil, o.failWith
	}
	f, err := logship.OpenNoLinks(path)
	if err == nil {
		o.opened = append(o.opened, f)
		if o.closeFirst {
			_ = f.Close()
		}
	}
	return f, err
}

func TestFiles_OpenErrorsAreReportedWithFixedReasons(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want string
	}{
		{"a file that does not exist", fs.ErrNotExist, "missing"},
		{"a file without permission", fs.ErrPermission, "permission denied"},
		{"any other error", errors.New("input/output error at /secret/path"), "read failed"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			w := newWorld(t)
			w.useFiles("mail.log")
			w.writeLog("mail.log", fileLines(1, 2))
			rec := &openRecorder{}
			w.openFiles = rec.open
			w.start()
			w.step()
			rec.failWith = tt.err
			w.appendLog("mail.log", fileLines(3, 3))

			var during []model.Record
			for range 3 {
				during = append(during, w.step()...)
			}
			rec.failWith = nil
			after := w.drain(5)

			if len(during) != 0 {
				t.Errorf("runs on a file that cannot be opened returned %v and %d gaps, want nothing", head(messages(during)), len(gapsOf(during)))
			}
			logs := w.logsOf("log file not readable")
			if len(logs) != 1 || logs[0].attrs["file"] != "0" || logs[0].attrs["err"] != tt.want {
				t.Errorf("warnings %q = %+v, want one with file 0 and err %q", "log file not readable", logs, tt.want)
			}
			requireMessages(t, "file lines once the file opens again", fileMsgs(after), []string{"file 3"})
			requireNoGaps(t, "the runs once the file opens again", after)
		})
	}
}

func TestFiles_FileIsOpenedAgainEveryRunAndTheKeptOneIsNotLeaked(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.writeLog("mail.log", "")
	rec := &openRecorder{}
	w.openFiles = rec.open
	w.start()

	for range 3 {
		w.step()
	}

	if len(rec.opened) != 3 {
		t.Fatalf("the collector opened the file %d times in three runs, want once per run", len(rec.opened))
	}
	if _, err := rec.opened[0].Stat(); err != nil {
		t.Errorf("the kept file reports %v, want it still open: the first open is the one kept", err)
	}
	for i, f := range rec.opened[1:] {
		if _, err := f.Stat(); err == nil {
			t.Errorf("open %d of the file is still open, want the file opened again to be closed at the end of the run", i+1)
		}
	}
}

func TestFiles_ReadErrorOfTheKeptFileShipsNothingAndRecordsNoGap(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.writeLog("mail.log", "") // nothing read yet, so that a failed read cannot look like a truncation
	rec := &openRecorder{}
	w.openFiles = rec.open
	w.start()
	w.step()
	if err := rec.opened[0].Close(); err != nil { // the kept descriptor fails every read from now on
		t.Fatalf("closing the kept file: %v", err)
	}
	w.appendLog("mail.log", fileLines(3, 3))

	var recs []model.Record
	for range 2 {
		recs = append(recs, w.step()...)
	}

	if len(recs) != 0 {
		t.Errorf("runs on a file that cannot be read returned %v and %d gaps, want nothing", head(messages(recs)), len(gapsOf(recs)))
	}
}

func TestFiles_ReadErrorOfTheRenamedFileKeepsTheNewFileWaiting(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.writeLog("mail.log", "")
	rec := &openRecorder{}
	w.openFiles = rec.open
	w.start()
	w.step()
	if err := rec.opened[0].Close(); err != nil {
		t.Fatalf("closing the kept file: %v", err)
	}
	w.rename("mail.log", "mail.log.1")
	w.writeLog("mail.log", "")

	recs := w.step() // the renamed file cannot be statted or read

	if len(recs) != 0 {
		t.Errorf("the run returned %v and %d gaps, want nothing", head(messages(recs)), len(gapsOf(recs)))
	}
}

func TestFiles_CanceledRunReadsNothingAndLosesNothing(t *testing.T) {
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 5))
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	run := scheduler.Run{Start: w.clock.peek().UTC()}

	canceled, err := w.l.Collect(ctx, run)
	if err != nil {
		t.Fatalf("Collect() error = %v, want nil", err)
	}
	w.l.Kept(run, true)
	w.clock.advance(runInterval)
	recs := w.drain(5)

	if len(canceled) != 0 {
		t.Errorf("the canceled run returned %v, want nothing", head(messages(canceled)))
	}
	requireMessages(t, "file lines of the runs after it", fileMsgs(recs), []string{"file 3", "file 4", "file 5"})
	requireNoGaps(t, "the runs after it", recs)
}

func TestFiles_TimeUpAfterAChunkOfFilteredLinesContinuesBehindIt(t *testing.T) {
	const noise = 2500
	w := newWorld(t)
	w.useFiles("mail.log")
	w.writeLog("mail.log", "")
	w.cfg.Drop = []logship.Rule{{Message: regexp.MustCompile(`^noise`)}}
	w.start()
	w.step()
	var text strings.Builder
	for n := 1; n <= noise; n++ {
		text.WriteString(fileLine(n, fmt.Sprintf("noise %d %s", n, strings.Repeat("x", 80))))
	}
	text.WriteString(fileLine(noise+1, "wanted 1") + fileLine(noise+2, "wanted 2"))
	w.appendLog("mail.log", text.String())
	w.clock.step = 1100 * time.Millisecond // every look at the clock finds the second of reading used up

	var all []model.Record
	runs := 0
	for ; runs < 40 && len(fileMsgs(all)) < 2; runs++ {
		all = append(all, w.step()...)
	}

	requireMessages(t, "file lines of all runs", fileMsgs(all), []string{"wanted 1", "wanted 2"})
	requireNoGaps(t, "the runs", all)
	if runs < 3 {
		t.Errorf("the 2,500 filtered lines of more than 64 KiB were read in %d runs, want the time budget to end a run after a chunk", runs)
	}
}

// linkTheLogDirectory moves the log directory aside and puts a link to it in its place; the returned function undoes that.
func linkTheLogDirectory(t *testing.T, w *world) (restore func()) {
	t.Helper()
	moved, link := w.logDir+".moved", w.logDir
	if err := os.Rename(link, moved); err != nil {
		t.Fatalf("moving the log directory: %v", err)
	}
	if err := os.Symlink(moved, link); err != nil {
		t.Fatalf("linking the log directory: %v", err)
	}
	return func() {
		if err := os.Remove(link); err != nil {
			t.Fatalf("removing the link: %v", err)
		}
		if err := os.Rename(moved, link); err != nil {
			t.Fatalf("restoring the log directory: %v", err)
		}
	}
}

func TestFiles_RotationSearchBehindALinkKeepsThePositionOfAnOpenableFile(t *testing.T) {
	t.Run("the rotated file is searched after a rotation while stopped", func(t *testing.T) {
		w := mailWorld(t)
		w.appendLog("mail.log", fileLines(3, 3))
		w.step()
		w.stop()
		w.appendLog("mail.log", fileLines(4, 4))
		w.rename("mail.log", "mail.log.1")
		w.writeLog("mail.log", fileLines(10, 11))
		restore := linkTheLogDirectory(t, w)
		w.openFiles = os.Open // a function that follows links, which the link-free walk would refuse
		w.reopen()
		w.start()

		var refused []model.Record
		for range 2 {
			refused = append(refused, w.step()...)
		}
		restore()
		recs := w.drain(5)

		if len(refused) != 0 {
			t.Errorf("runs with a link in the directory path returned %v and %d gaps, want nothing: the position stays", head(messages(refused)), len(gapsOf(refused)))
		}
		logs := w.logsOf("log file not readable")
		if len(logs) != 1 || logs[0].attrs["err"] != "link refused" {
			t.Errorf("warnings %q = %+v, want one with err %q", "log file not readable", logs, "link refused")
		}
		requireMessages(t, "file lines once the directory is no link", fileMsgs(recs), []string{"file 4", "file 10", "file 11"})
		requireNoGaps(t, "the runs once the directory is no link", recs)
	})
	t.Run("the renamed file of a saved position is searched after a restart", func(t *testing.T) {
		w, _ := rotate(t)
		w.step() // the saved position now holds the renamed file
		w.appendLog("mail.log.1", fileLines(5, 5))
		w.stop()
		restore := linkTheLogDirectory(t, w)
		w.openFiles = os.Open
		w.reopen()
		w.start()

		var refused []model.Record
		for range 2 {
			refused = append(refused, w.step()...)
		}
		restore()
		recs := w.drain(5)

		if len(refused) != 0 {
			t.Errorf("runs with a link in the directory path returned %v and %d gaps, want nothing: the position stays", head(messages(refused)), len(gapsOf(refused)))
		}
		logs := w.logsOf("log file not readable")
		if len(logs) != 1 || logs[0].attrs["err"] != "link refused" {
			t.Errorf("warnings %q = %+v, want one with err %q", "log file not readable", logs, "link refused")
		}
		requireMessages(t, "file lines once the directory is no link", fileMsgs(recs), []string{"file 5"})
		requireNoGaps(t, "the runs once the directory is no link", recs)
	})
}

func TestFiles_OnlyRotatedNamesAreSearched(t *testing.T) {
	names := []string{"mail.log-2026102x", "mail.log-202610221", "mail.log-2026102", "mail.log.2", "mail.log.10", "mail.log~", "other.log.1", "mail.log-20261022.gz"}
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			w := mailWorld(t)
			w.appendLog("mail.log", fileLines(3, 3))
			w.step()
			w.stop()
			w.rename("mail.log", name)
			w.writeLog("mail.log", fileLines(10, 11))
			w.reopen()
			w.start()

			recs := w.drain(5)

			requireMessages(t, "file lines", fileMsgs(recs), []string{"file 10", "file 11"})
			requireGap(t, oneGap(t, "the runs", recs), fileLineTime(3), fileLineTime(10))
		})
	}
}

func TestFiles_TruncationShipsTheHeldPartialLineFirst(t *testing.T) {
	w := mailWorld(t)
	partial := fileLine(3, "cut off before its line feed")
	w.appendLog("mail.log", partial[:len(partial)-1])
	if held := w.step(); len(held) != 0 {
		t.Fatalf("the run on a partial line returned %v, want nothing", head(messages(held)))
	}
	w.writeLog("mail.log", fileLines(20, 20)) // shorter than what was read: truncated in place

	recs := w.step()

	requireMessages(t, "file lines", fileMsgs(recs), []string{"cut off before its line feed", "file 20"})
	requireGap(t, oneGap(t, "the run that noticed the truncation", recs), fileLineTime(3), w.run.Start)
	if again := w.step(); len(again) != 0 {
		t.Errorf("the next run returned %v, want nothing: the partial line is shipped once", head(messages(again)))
	}
}

func TestFiles_RenamedBacklogIsShippedBeforeTheNewFileAcrossRuns(t *testing.T) {
	w := mailWorld(t)
	w.appendLog("mail.log", fileLines(3, 1502))
	w.rename("mail.log", "mail.log.1")
	w.writeLog("mail.log", fileLines(3000, 3001))

	first := w.step()
	second := w.step()
	third := w.step()

	requireMessages(t, "lines of the first run", fileMsgs(first), numbered("file", 3, 1002))
	requireMessages(t, "lines of the second run", fileMsgs(second), append(numbered("file", 1003, 1502), "file 3000", "file 3001"))
	if len(third) != 0 {
		t.Errorf("the third run returned %v, want nothing", head(messages(third)))
	}
	requireNoGaps(t, "the runs", append(append(first, second...), third...))
}

func TestFiles_FileThatCannotBeStattedIsNotReadable(t *testing.T) {
	w := newWorld(t)
	w.useFiles("mail.log")
	w.writeLog("mail.log", fileLines(1, 2))
	rec := &openRecorder{}
	w.openFiles = rec.open
	w.start()
	w.step()
	rec.closeFirst = true
	w.appendLog("mail.log", fileLines(3, 3))

	var during []model.Record
	for range 2 {
		during = append(during, w.step()...)
	}
	rec.closeFirst = false
	after := w.drain(5)

	if len(during) != 0 {
		t.Errorf("runs on a file that cannot be statted returned %v and %d gaps, want nothing", head(messages(during)), len(gapsOf(during)))
	}
	logs := w.logsOf("log file not readable")
	if len(logs) != 1 || logs[0].attrs["err"] != "read failed" {
		t.Errorf("warnings %q = %+v, want one with err %q", "log file not readable", logs, "read failed")
	}
	requireMessages(t, "file lines once the file can be statted", fileMsgs(after), []string{"file 3"})
}
