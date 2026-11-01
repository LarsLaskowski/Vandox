package logstages

import (
	"strings"
	"testing"
)

type headerCase struct {
	name    string
	message string
	// header is false for a line that is no header: the stages pass it through unchanged.
	header bool
	// want is the message of the entry, without the header prefix.
	want string
	// level is the priority of the header's level, keep for a header without one: the line keeps its priority.
	level uint8
	event string
}

func headerCases() []headerCase {
	return []headerCase{
		// Form A: date with dashes, thread, level.
		{"server note", "2026-10-31 10:00:00 0 [Note] Starting MariaDB 10.6.12-MariaDB source revision  as process 2345", true, "Starting MariaDB 10.6.12-MariaDB source revision  as process 2345", 6, "mariadb.start"},
		{"server error", "2026-10-31 10:00:00 0 [ERROR] InnoDB: Cannot open datafile", true, "InnoDB: Cannot open datafile", 3, ""},
		{"server warning", "2026-10-31 10:00:00 5 [Warning] Aborted connection 5 to db: 'wp' user: 'wp' host: 'localhost' (Got an error reading communication packets)", true, "Aborted connection 5 to db: 'wp' user: 'wp' host: 'localhost' (Got an error reading communication packets)", 4, ""},
		{"thread of several digits", "2026-10-31 10:00:00 123456 [Note] Thread message", true, "Thread message", 6, ""},
		{"thread of twenty digits", "2026-10-31 10:00:00 " + strings.Repeat("7", 20) + " [Note] Thread message", true, "Thread message", 6, ""},
		{"hour padded with a space", "2026-03-02  3:12:40 0 [Note] InnoDB: Completed initialization of buffer pool", true, "InnoDB: Completed initialization of buffer pool", 6, ""},
		{"level without message", "2026-10-31 10:00:00 0 [Note]", true, "", 6, ""},
		{"ranges are not checked", "2026-99-99 99:99:99 0 [Note] x", true, "x", 6, ""},

		// Form B: date with dashes and an InnoDB time stamp.
		{"innodb time stamp", "2026-10-31 10:00:00 0x7f3a2c1fe640  InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836", true, "InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836", keep, ""},
		{"innodb time stamp with one hex digit", "2026-10-31 10:00:00 0xa InnoDB: x", true, "InnoDB: x", keep, ""},
		{"innodb time stamp with sixteen hex digits", "2026-10-31 10:00:00 0x" + strings.Repeat("a", 16) + " InnoDB: x", true, "InnoDB: x", keep, ""},
		{"innodb time stamp without message", "2026-10-31 10:00:00 0x7f3a2c1fe640  ", true, "", keep, ""},

		// Form C: date without dashes, level.
		{"signal handler", "261031 10:00:00 [ERROR] mysqld got signal 11 ;", true, "mysqld got signal 11 ;", 3, "mariadb.abort"},
		{"short note", "261031 10:00:00 [Note] Plugin 'FEEDBACK' is disabled.", true, "Plugin 'FEEDBACK' is disabled.", 6, ""},
		{"short warning", "261031  3:00:00 [Warning] Using unique option prefix", true, "Using unique option prefix", 4, ""},

		// Form D: date without dashes, start script.
		{"start script", "261031 10:00:00 mysqld_safe Starting mariadbd daemon with databases from /var/lib/mysql", true, "Starting mariadbd daemon with databases from /var/lib/mysql", keep, ""},

		// Lines that are no header.
		{"continuation", "Version: '10.6.12-MariaDB'  socket: '/run/mysqld/mysqld.sock'  port: 3306", false, "", 0, ""},
		{"stack frame", "0x55d1 mariadbd(my_print_stacktrace+0x2e)[0x55d1]", false, "", 0, ""},
		{"unknown level", "2026-10-31 10:00:00 0 [Info] x", false, "", 0, ""},
		{"lower case level", "2026-10-31 10:00:00 0 [note] x", false, "", 0, ""},
		{"no space after the level", "2026-10-31 10:00:00 0 [Note]x", false, "", 0, ""},
		{"no message after the clock", "2026-10-31 10:00:00", false, "", 0, ""},
		{"t instead of a space", "2026-10-31T10:00:00 0 [Note] x", false, "", 0, ""},
		{"leading space", " 2026-10-31 10:00:00 0 [Note] x", false, "", 0, ""},
		{"digits only", "12345", false, "", 0, ""},
		{"empty", "", false, "", 0, ""},
		{"thread of twenty-one digits", "2026-10-31 10:00:00 " + strings.Repeat("7", 21) + " [Note] x", false, "", 0, ""},
		{"start script after a long date", "2026-10-31 10:00:00 mysqld_safe x", false, "", 0, ""},
		{"short date with a thread", "261031 10:00:00 0 [Note] x", false, "", 0, ""},
		{"short level without space", "261031 10:00:00 [Note]x", false, "", 0, ""},
		{"upper case hex digits", "2026-10-31 10:00:00 0x7F3A InnoDB: x", false, "", 0, ""},
		{"seventeen hex digits", "2026-10-31 10:00:00 0x" + strings.Repeat("a", 17) + " InnoDB: x", false, "", 0, ""},
		{"hex without digits", "2026-10-31 10:00:00 0x InnoDB: x", false, "", 0, ""},
		{"hex without space", "2026-10-31 10:00:00 0x7f3a", false, "", 0, ""},
		{"date of five digits", "26103 10:00:00 [Note] x", false, "", 0, ""},
	}
}

func TestParseHeader(t *testing.T) {
	for _, tc := range headerCases() {
		for _, linePrio := range []*uint8{u8(6), nil} {
			t.Run(tc.name+" priority "+prioString(linePrio), func(t *testing.T) { checkHeaderCase(t, tc, linePrio) })
		}
	}
}

func checkHeaderCase(t *testing.T, tc headerCase, linePrio *uint8) {
	t.Helper()

	f := newFeeder(t)
	line := f.line(0, "mariadbd", 2345, tc.message)
	logOf(t, Output{Rec: line.Rec}).Priority = linePrio

	out := f.put(line)

	if !tc.header {
		wantMessages(t, "no header, passes at once", out, tc.message)
		wantPriority(t, "no header keeps the priority", out[0], linePrio)

		if f.s.Open() {
			t.Errorf("Open() = true after %q, want false: no header", tc.message)
		}

		return
	}

	if len(out) != 0 {
		t.Fatalf("header %q gave %d outputs, want 0 while the entry is open", tc.message, len(out))
	}

	if !f.s.Open() {
		t.Fatalf("Open() = false after the header %q, want true", tc.message)
	}

	closed := f.s.Close(nil)

	wantMessages(t, "entry", closed, tc.want)

	wantLevel := linePrio
	if tc.level != keep {
		wantLevel = u8(tc.level)
	}

	wantPriority(t, "entry", closed[0], wantLevel)

	if got := logOf(t, closed[0]).Event; got != tc.event {
		t.Errorf("Event of %q = %q, want %q", tc.message, got, tc.event)
	}
}

func TestParseHeader_ALongMessageIsReadWhole(t *testing.T) {
	// A header the line reader cut at the limit still parses, and the rest of the message is kept.
	text := strings.Repeat("m", 16384-len(headerNote))
	f := newFeeder(t)

	if out := f.header(0, headerNote+text); len(out) != 0 {
		t.Fatalf("header gave %d outputs, want 0", len(out))
	}

	closed := f.s.Close(nil)

	wantMessages(t, "entry", closed, text)
}
