package logstages

import (
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func note(msg string) string { return headerNote + msg }

var headerText = strings.Repeat("h", 100)

func TestMariaDB_JoinsTheLinesOfTheSameProcessIntoOneRecord(t *testing.T) {
	f := newFeeder(t)

	if out := f.header(0, "2026-10-31 10:00:00 0 [ERROR] mysqld got signal 11 ;"); len(out) != 0 {
		t.Fatalf("header gave %d outputs, want 0", len(out))
	}

	for i, text := range []string{"This could be because you hit a bug.", "Attempting backtrace.", "0x55d1 mariadbd(my_print_stacktrace+0x2e)[0x55d1]"} {
		if out := f.add(time.Duration(i+1)*time.Second, "mariadbd", 2345, text); len(out) != 0 {
			t.Fatalf("continuation %d gave %d outputs, want 0", i, len(out))
		}
	}

	out := f.header(5*time.Second, note("Starting MariaDB 10.6.12 source revision  as process 2346"))

	wantMessages(t, "next header ends the entry", out, "mysqld got signal 11 ;\nThis could be because you hit a bug.\nAttempting backtrace.\n0x55d1 mariadbd(my_print_stacktrace+0x2e)[0x55d1]")

	rec := out[0].Rec
	l := logOf(t, out[0])

	if rec.Origin != model.OriginAgent || rec.Source != "journal" || rec.Seq != 101 || !rec.CapturedAt.Equal(base) {
		t.Errorf("entry meta = %+v, want the meta of the header record (agent, journal, 101, %v)", rec.Meta, base)
	}

	if l.Log != "journal" || l.Host != "web-1" || l.Program != "mariadbd" || l.PID != 2345 {
		t.Errorf("entry log line = %+v, want the key of the header line", *l)
	}

	if l.Truncated {
		t.Errorf("Truncated = true, want false")
	}

	if l.Event != "mariadb.abort" {
		t.Errorf("Event = %q, want mariadb.abort", l.Event)
	}

	wantPriority(t, "entry", out[0], u8(3))

	if !out[0].From.Equal(base.Add(-time.Second)) {
		t.Errorf("From = %v, want the time of the line before the header, %v", out[0].From, base.Add(-time.Second))
	}

	if !f.s.Open() {
		t.Errorf("Open() = false, want true: the new header opened an entry")
	}
}

func TestMariaDB_EndsAtAHeaderASameKeyLineThatDoesNotJoinOrTheClose(t *testing.T) {
	t.Run("a header", func(t *testing.T) {
		f := newFeeder(t)
		f.header(0, note("first"))
		f.add(time.Second, "mariadbd", 2345, "continuation")

		wantMessages(t, "header", f.header(2*time.Second, note("second")), "first\ncontinuation")
		wantMessages(t, "close", f.s.Close(nil), "second")
	})

	t.Run("a header of another process", func(t *testing.T) {
		f := newFeeder(t)
		f.header(0, note("first"))

		out := f.add(time.Second, "mariadbd", 9999, note("other process"))

		wantMessages(t, "header of the other pid", out, "first")
		wantMessages(t, "close", f.s.Close(nil), "other process")
	})

	t.Run("a same-key line more than 60 seconds later", func(t *testing.T) {
		f := newFeeder(t)
		f.header(0, note("first"))
		f.add(time.Second, "mariadbd", 2345, "continuation")

		out := f.add(61*time.Second, "mariadbd", 2345, "stray line")

		wantMessages(t, "stray line", out, "first\ncontinuation", "stray line")

		if f.s.Open() {
			t.Errorf("Open() = true after the entry ended, want false")
		}

		wantMessages(t, "a later continuation has no entry to join", f.add(62*time.Second, "mariadbd", 2345, "alone"), "alone")
	})

	t.Run("the close", func(t *testing.T) {
		f := newFeeder(t)
		f.header(0, note("first"))
		f.add(time.Second, "mariadbd", 2345, "continuation")

		wantMessages(t, "close", f.s.Close(nil), "first\ncontinuation")
		wantMessages(t, "second close", f.s.Close(nil))
	})
}

func TestMariaDB_LinesOfOtherKeysNeverEndTheEntry(t *testing.T) {
	f := newFeeder(t)
	f.header(0, note("entry"))

	other := f.line(time.Second, "mariadbd", 2345, "other host")
	logOf(t, Output{Rec: other.Rec}).Host = "web-2"

	for _, out := range [][]Output{
		f.add(time.Second, "sshd", 812, "Accepted publickey for alice"),
		f.add(2*time.Second, "mariadbd", 9999, "other pid"),
		f.put(other),
		f.add(3*time.Second, "mysqld_safe", 2345, "other program"),
		f.add(4*time.Second, "mariadbd", 2345, strings.Repeat("a", 3)+"\nsecond line in the message"),
	} {
		if len(out) != 1 {
			t.Fatalf("a line of another key gave %d outputs, want 1 at once", len(out))
		}
	}

	f.add(5*time.Second, "mariadbd", 2345, "joins")

	wantMessages(t, "entry", f.s.Close(nil), "entry\njoins")
}

func TestMariaDB_SixtySecondsBeforeOrAfterTheHeaderJoin(t *testing.T) {
	cases := []struct {
		name   string
		offset time.Duration
		joins  bool
	}{
		{"60 s after", 60 * time.Second, true},
		{"60 s and 1 us after", 60*time.Second + time.Microsecond, false},
		{"59 s after", 59 * time.Second, true},
		{"same time", 0, true},
		{"50 s before, a backlog line", -50 * time.Second, true},
		{"60 s before", -60 * time.Second, true},
		{"60 s and 1 us before", -60*time.Second - time.Microsecond, false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newFeeder(t)
			f.header(0, note("header"))

			out := f.add(tc.offset, "mariadbd", 2345, "line")

			if tc.joins {
				wantMessages(t, "joined", out)
				wantMessages(t, "entry", f.s.Close(nil), "header\nline")

				return
			}

			wantMessages(t, "not joined: the entry, then the line", out, "header", "line")
		})
	}
}

func TestMariaDB_SixtySecondsAreMeasuredFromTheHeaderNotFromTheLastLine(t *testing.T) {
	f := newFeeder(t)
	f.header(0, note("header"))
	f.add(40*time.Second, "mariadbd", 2345, "line 1")

	// The second line is 40 s after the first one but 80 s after the header.
	out := f.add(80*time.Second, "mariadbd", 2345, "line 2")

	wantMessages(t, "entry and the line that did not join", out, "header\nline 1", "line 2")
}

func TestMariaDB_ProgramMysqldIsAServerToo(t *testing.T) {
	f := newFeeder(t)
	f.add(0, "mysqld", 77, note("legacy header"))
	f.add(time.Second, "mysqld", 77, "continuation")

	wantMessages(t, "entry", f.s.Close(nil), "legacy header\ncontinuation")
}

func TestMariaDB_OnlyTheProgramsMariadbdAndMysqldAreGrouped(t *testing.T) {
	for _, program := range []string{"mariadb", "mysqld_safe", "MariaDBd", "mariadbd2", "sshd", ""} {
		f := newFeeder(t)

		out := f.add(0, program, 2345, note("header of "+program))

		wantMessages(t, program, out, note("header of "+program))

		if f.s.Open() {
			t.Errorf("Open() = true after a header of program %q, want false", program)
		}
	}
}

func TestMariaDB_AMessageWithALineFeedIsNotJoined(t *testing.T) {
	f := newFeeder(t)
	f.header(0, note("header"))

	out := f.add(time.Second, "mariadbd", 2345, "two\nlines")

	wantMessages(t, "multi-line message passes at once", out, "two\nlines")

	out = f.add(2*time.Second, "mariadbd", 2345, note("header\nwith a second line"))

	wantMessages(t, "a header with a line feed is no header", out, note("header\nwith a second line"))
	wantMessages(t, "the entry is still open", f.s.Close(nil), "header")
}

func TestMariaDB_EmptyLinesInsideAreKeptAndTrailingOnesDropped(t *testing.T) {
	f := newFeeder(t)
	f.header(0, note("header"))

	for _, text := range []string{"a", "", "", "b", "", ""} {
		f.add(time.Second, "mariadbd", 2345, text)
	}

	wantMessages(t, "entry", f.s.Close(nil), "header\na\n\n\nb")
}

func TestMariaDB_TheMessageStaysWithinTheLimit(t *testing.T) {
	for _, tc := range []struct {
		name string
		run  func(*testing.T)
	}{
		{"a line that fills the limit exactly joins", limitLineThatFillsIt},
		{"a line one byte longer does not join and stands alone", limitLineOneByteLonger},
		{"empty lines count against the limit", limitEmptyLinesCount},
		{"an entry that ended at the limit lets the next line stand alone", limitEntryThatEnded},
	} {
		t.Run(tc.name, tc.run)
	}
}

func limitLineThatFillsIt(t *testing.T) {
	f := newFeeder(t)
	f.header(0, note(headerText))

	fill := strings.Repeat("f", model.MaxTextBytes-len(headerText)-1)
	if out := f.add(time.Second, "mariadbd", 2345, fill); len(out) != 0 {
		t.Fatalf("a line that fits gave %d outputs, want 0", len(out))
	}

	out := f.s.Close(nil)
	needOutputs(t, out, 1)

	wantMessages(t, "entry", out, headerText+"\n"+fill)

	if got := len(logOf(t, out[0]).Message); got != model.MaxTextBytes {
		t.Errorf("message length = %d, want %d", got, model.MaxTextBytes)
	}

	if logOf(t, out[0]).Truncated {
		t.Errorf("Truncated = true, want false for a message that fits")
	}
}

func limitLineOneByteLonger(t *testing.T) {
	f := newFeeder(t)
	f.header(0, note(headerText))

	fill := strings.Repeat("f", model.MaxTextBytes-len(headerText))
	out := f.add(time.Second, "mariadbd", 2345, fill)

	wantMessages(t, "entry, then the line", out, headerText, fill)

	if f.s.Open() {
		t.Errorf("Open() = true after the entry ended, want false")
	}
}

func limitEmptyLinesCount(t *testing.T) {
	f := newFeeder(t)
	f.header(0, note(headerText))
	f.add(time.Second, "mariadbd", 2345, "")
	f.add(time.Second, "mariadbd", 2345, "")

	// 100 + 2 empty lines + 1 line feed + the line: 100 + 2 + 1 + size.
	fits := strings.Repeat("f", model.MaxTextBytes-len(headerText)-3)
	tooLong := fits + "f"

	if out := f.add(2*time.Second, "mariadbd", 2345, tooLong); len(out) != 2 {
		t.Fatalf("a line that exceeds with the empty lines gave %d outputs, want the entry and the line", len(out))
	}

	g := newFeeder(t)
	g.header(0, note(headerText))
	g.add(time.Second, "mariadbd", 2345, "")
	g.add(time.Second, "mariadbd", 2345, "")

	if out := g.add(2*time.Second, "mariadbd", 2345, fits); len(out) != 0 {
		t.Fatalf("a line that fits with the empty lines gave %d outputs, want 0", len(out))
	}

	wantMessages(t, "entry", g.s.Close(nil), headerText+"\n\n\n"+fits)
}

func limitEntryThatEnded(t *testing.T) {
	f := newFeeder(t)
	f.header(0, note(headerText))

	var out []Output

	for i := 0; i < 5; i++ {
		out = f.add(time.Duration(i+1)*time.Second, "mariadbd", 2345, strings.Repeat(string(rune('a'+i)), 4000))
	}

	if len(out) != 2 {
		t.Fatalf("the fifth line of 4,000 bytes gave %d outputs, want the entry and the line", len(out))
	}

	if got := len(logOf(t, out[0]).Message); got > model.MaxTextBytes {
		t.Errorf("entry message length = %d, want at most %d", got, model.MaxTextBytes)
	}

	if want := strings.Repeat("e", 4000); logOf(t, out[1]).Message != want {
		t.Errorf("the line that did not fit was changed")
	}

	wantMessages(t, "the next line has no entry", f.add(10*time.Second, "mariadbd", 2345, "alone"), "alone")
}

func TestMariaDB_TruncatedAndPriority(t *testing.T) {
	t.Run("a cut continuation line marks the entry truncated", func(t *testing.T) {
		f := newFeeder(t)
		f.header(0, note("header"))

		cut := f.line(time.Second, "mariadbd", 2345, "cut line")
		logOf(t, Output{Rec: cut.Rec}).Truncated = true
		f.put(cut)

		out := f.s.Close(nil)
		needOutputs(t, out, 1)

		if !logOf(t, out[0]).Truncated {
			t.Errorf("Truncated = false, want true")
		}
	})

	t.Run("a cut header marks the entry truncated", func(t *testing.T) {
		f := newFeeder(t)
		header := f.line(0, "mariadbd", 2345, note("header"))
		logOf(t, Output{Rec: header.Rec}).Truncated = true
		f.put(header)

		if !logOf(t, closeOne(t, f.s)).Truncated {
			t.Errorf("Truncated = false, want true")
		}
	})

	t.Run("a cut empty line marks the entry truncated", func(t *testing.T) {
		f := newFeeder(t)
		f.header(0, note("header"))

		cut := f.line(time.Second, "mariadbd", 2345, "")
		logOf(t, Output{Rec: cut.Rec}).Truncated = true
		f.put(cut)

		if !logOf(t, closeOne(t, f.s)).Truncated {
			t.Errorf("Truncated = false, want true")
		}
	})

	t.Run("the level sets the priority and the lines do not", func(t *testing.T) {
		f := newFeeder(t)
		f.header(0, "2026-10-31 10:00:00 0 [Warning] header")

		low := f.line(time.Second, "mariadbd", 2345, "line")
		logOf(t, Output{Rec: low.Rec}).Priority = u8(2)
		f.put(low)

		wantPriority(t, "entry", closeOne(t, f.s), u8(4))
	})

	t.Run("a header without a level keeps the priority of its line", func(t *testing.T) {
		f := newFeeder(t)
		header := f.line(0, "mariadbd", 2345, "2026-10-31 10:00:00 0x7f3a2c1fe640  InnoDB: Assertion failure")
		logOf(t, Output{Rec: header.Rec}).Priority = nil
		f.put(header)

		wantPriority(t, "entry", closeOne(t, f.s), nil)
	})
}

func TestMariaDB_Events(t *testing.T) {
	cases := []struct {
		name    string
		message string
		event   string
	}{
		{"start", note("Starting MariaDB 10.6.12-MariaDB-0ubuntu0.22.04.1 source revision  as process 2345"), "mariadb.start"},
		{"old start wording of 10.6.7 to 10.6.11", note("mariadbd (server 10.6.8-MariaDB) starting as process 812 ..."), "mariadb.start"},
		{"old start wording without the server part", note("/usr/sbin/mysqld (mysqld 10.6.7-MariaDB-2ubuntu1) starting as process 812 ..."), ""},
		{"ready", note("/usr/sbin/mariadbd: ready for connections."), "mariadb.ready"},
		{"shutdown", note("/usr/sbin/mariadbd (initiated by: unknown): Normal shutdown"), "mariadb.shutdown"},
		{"shutdown complete", note("/usr/sbin/mariadbd: Shutdown complete"), "mariadb.shutdown_complete"},
		{"abort", "2026-10-31 10:00:00 0 [ERROR] mysqld got signal 11 ;", "mariadb.abort"},
		{"abort with one digit", "2026-10-31 10:00:00 0 [ERROR] /usr/sbin/mariadbd got signal 6 ;", "mariadb.abort"},
		{"abort with three digits", "2026-10-31 10:00:00 0 [ERROR] mysqld got signal 127 ;", "mariadb.abort"},
		{"abort with four digits", "2026-10-31 10:00:00 0 [ERROR] mysqld got signal 1270 ;", ""},
		{"abort without digits", "2026-10-31 10:00:00 0 [ERROR] mysqld got signal  ;", ""},
		{"abort needs the semicolon at the end", "2026-10-31 10:00:00 0 [ERROR] mysqld got signal 11 ; and more", ""},
		{"abort needs a program before it", "2026-10-31 10:00:00 0 [ERROR]  got signal 11 ;", ""},
		{"abort as a note", note("mysqld got signal 11 ;"), ""},
		{"abort as a warning", "2026-10-31 10:00:00 0 [Warning] mysqld got signal 11 ;", ""},
		{"ready as an error", "2026-10-31 10:00:00 0 [ERROR] /usr/sbin/mariadbd: ready for connections.", ""},
		{"ready as a warning", "2026-10-31 10:00:00 0 [Warning] /usr/sbin/mariadbd: ready for connections.", ""},
		{"recovery start", note("InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234"), "mariadb.recovery_start"},
		{"table recovery start", note("Starting table crash recovery..."), "mariadb.recovery_start"},
		{"table recovery end", note("Crash table recovery finished."), "mariadb.recovery_end"},
		{"table recovery start with a suffix", note("Starting table crash recovery... now"), ""},
		{"other note", note("InnoDB: Completed initialization of buffer pool"), ""},
		{"other error", "2026-10-31 10:00:00 0 [ERROR] InnoDB: Cannot open datafile", ""},
		{"innodb time stamp line", "2026-10-31 10:00:00 0x7f3a2c1fe640  InnoDB: Assertion failure", ""},
		{"short note ready", "261031 10:00:00 [Note] /usr/sbin/mariadbd: ready for connections.", "mariadb.ready"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newFeeder(t)
			f.header(0, tc.message)

			out := f.s.Close(nil)
			needOutputs(t, out, 1)
			if len(out) != 1 {
				t.Fatalf("got %d outputs, want 1", len(out))
			}

			if got := logOf(t, out[0]).Event; got != tc.event {
				t.Errorf("Event of %q = %q, want %q", tc.message, got, tc.event)
			}
		})
	}
}

const started = "InnoDB: 10.6.12 started; log sequence number 8409876; transaction id 5678"

// eventOf opens an entry with the text and returns the event the entry gets.
func eventOf(f *feeder, at time.Duration, text string) string {
	f.t.Helper()

	out := f.header(at, note(text))
	if len(out) == 0 {
		out = f.s.Close(nil)
	}

	needOutputs(f.t, out, 1)

	return logOf(f.t, out[0]).Event
}

func TestMariaDB_RecoveryEndNeedsAnOpenRecovery(t *testing.T) {
	for _, tc := range []struct {
		name string
		run  func(*testing.T)
	}{
		{"without a recovery the line has no event", recoveryNoneWithoutAStart},
		{"after a recovery start the line ends it, and only once", recoveryEndsOnce},
		{"a start line cancels an open recovery", recoveryCancelledByAStartLine},
		{"the table recovery needs no recovery start", recoveryTableNeedsNoStart},
	} {
		t.Run(tc.name, tc.run)
	}
}

func recoveryNoneWithoutAStart(t *testing.T) {
	f := newFeeder(t)

	if got := eventOf(f, 0, started); got != "" {
		t.Errorf("Event = %q, want none", got)
	}
}

func recoveryEndsOnce(t *testing.T) {
	f := newFeeder(t)
	f.header(0, note("InnoDB: Starting crash recovery from checkpoint LSN=1,1"))
	f.header(time.Second, note("InnoDB: 128 rollback segments are active."))

	out := f.header(2*time.Second, note(started))
	needOutputs(t, out, 1)
	if got := logOf(t, out[0]).Event; got != "" {
		t.Fatalf("event of the rollback segments line = %q, want none", got)
	}

	out = f.header(3*time.Second, note(started))
	needOutputs(t, out, 1)
	if got := logOf(t, out[0]).Event; got != "mariadb.recovery_end" {
		t.Fatalf("event of the first started line = %q, want mariadb.recovery_end", got)
	}

	out = f.s.Close(nil)
	needOutputs(t, out, 1)

	if got := logOf(t, out[0]).Event; got != "" {
		t.Errorf("event of the second started line = %q, want none: the recovery is over", got)
	}
}

func recoveryCancelledByAStartLine(t *testing.T) {
	f := newFeeder(t)
	f.header(0, note("InnoDB: Starting crash recovery from checkpoint LSN=1,1"))
	f.header(time.Second, note("Starting MariaDB 10.6.12 source revision  as process 9"))

	out := f.header(2*time.Second, note(started))
	needOutputs(t, out, 1)
	if got := logOf(t, out[0]).Event; got != "mariadb.start" {
		t.Fatalf("event = %q, want mariadb.start", got)
	}

	out = f.s.Close(nil)
	needOutputs(t, out, 1)

	if got := logOf(t, out[0]).Event; got != "" {
		t.Errorf("event of the started line after a start = %q, want none", got)
	}
}

func recoveryTableNeedsNoStart(t *testing.T) {
	f := newFeeder(t)

	if got := eventOf(f, 0, "Crash table recovery finished."); got != "mariadb.recovery_end" {
		t.Errorf("Event = %q, want mariadb.recovery_end", got)
	}
}

func TestMariaDB_OneEntryIsHeldHoweverManyForgedHeadersCome(t *testing.T) {
	f := newFeeder(t)

	emitted := 0

	for i := 0; i < 2000; i++ {
		out := f.add(time.Duration(i)*time.Millisecond, "mariadbd", 2345, note(fmt.Sprintf("forged header %d", i)))
		emitted += len(out)

		if i > 0 && len(out) != 1 {
			t.Fatalf("header %d gave %d outputs, want the one entry before it", i, len(out))
		}
	}

	if emitted != 1999 {
		t.Errorf("emitted %d entries, want 1999 with the last one held", emitted)
	}

	held, ok := f.s.HeldFrom()
	if !ok || held.At != f.pos {
		t.Errorf("HeldFrom() = %+v, %v, want the mark of the last header (At %d)", held, ok, f.pos)
	}
}
