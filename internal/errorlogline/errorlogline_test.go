package errorlogline_test

import (
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/errorlogline"
	"github.com/LarsLaskowski/Vandox/internal/syslogline"
)

// zonePlusTwo is the zone the lines of the tests are written in: two hours ahead of UTC all year.
var zonePlusTwo = time.FixedZone("TST", 2*60*60)

// parseCase is one line, the instant and priority it must resolve to and the message it must carry. A zero want is a line that is
// returned whole and raw.
type parseCase struct {
	name     string
	line     string
	want     time.Time
	priority uint8
	message  string
}

// at returns the UTC instant of a local time of zonePlusTwo given as UTC wall-clock fields, with the fraction in nanoseconds.
func at(year int, month time.Month, day, hour, minute, second, nanos int) time.Time {
	return time.Date(year, month, day, hour, minute, second, nanos, time.UTC)
}

// raw returns a case of a line that is not of the format or whose time does not resolve.
func raw(name, line string) parseCase {
	return parseCase{name: name, line: line}
}

// runCases parses every line with a fresh clock and checks the result.
func runCases(t *testing.T, format errorlogline.Format, cases []parseCase) {
	t.Helper()
	for _, tt := range cases {
		t.Run(tt.name, func(t *testing.T) {
			got := errorlogline.Parse(format, []byte(tt.line), syslogline.NewClock(zonePlusTwo))
			requireLine(t, tt, got)
		})
	}
}

// requireLine checks the parsed line against the case.
func requireLine(t *testing.T, tt parseCase, got syslogline.Line) {
	t.Helper()
	if got.Host != "" || got.Program != "" || got.PID != 0 {
		t.Errorf("Parse(%q) host %q program %q pid %d, want all empty", tt.line, got.Host, got.Program, got.PID)
	}
	if tt.want.IsZero() {
		if got.Header || !got.Time.IsZero() || got.Priority != nil || got.Message != tt.line {
			t.Errorf("Parse(%q) = header %v time %v priority %v message %q, want a raw line: no header, zero time, no priority, the whole line", tt.line, got.Header, got.Time, got.Priority, got.Message)
		}
		return
	}
	if !got.Header {
		t.Errorf("Parse(%q) header = false, want true", tt.line)
	}
	if !got.Time.Equal(tt.want) || got.Time.Location() != time.UTC {
		t.Errorf("Parse(%q) time = %v (%v), want %v in UTC", tt.line, got.Time, got.Time.Location(), tt.want)
	}
	if got.Priority == nil || *got.Priority != tt.priority {
		t.Errorf("Parse(%q) priority = %v, want %d", tt.line, got.Priority, tt.priority)
	}
	if got.Message != tt.message {
		t.Errorf("Parse(%q) message = %q, want %q", tt.line, got.Message, tt.message)
	}
}

func TestParse_PleskPanelLines(t *testing.T) {
	const stamp = "[2026-10-09 03:12:12"
	local := at(2026, time.October, 9, 1, 12, 12, 0)
	cases := []parseCase{
		{"with process and request", stamp + ".123] 4821:5f3a9c1e INFO [panel] Session opened", at(2026, time.October, 9, 1, 12, 12, 123000000), 6, "4821:5f3a9c1e INFO [panel] Session opened"},
		{"without process and request", stamp + "] ERR [panel] failed", local, 3, "ERR [panel] failed"},
		{"the level ends the line", stamp + "] WARN", local, 4, "WARN"},
		{"the message keeps tabs and more spaces", stamp + "] INFO\tx  y", local, 6, "INFO\tx  y"},
		{"the message keeps non-ASCII text", stamp + "] NOTICE Größe ü", local, 5, "NOTICE Größe ü"},
		{"one fraction digit", stamp + ".5] INFO x", at(2026, time.October, 9, 1, 12, 12, 500000000), 6, "INFO x"},
		{"six fraction digits", stamp + ".123456] INFO x", at(2026, time.October, 9, 1, 12, 12, 123456000), 6, "INFO x"},
		{"a process of ten digits", stamp + "] 1234567890:abc INFO x", local, 6, "1234567890:abc INFO x"},
		{"a request of 64 characters", stamp + "] 1:" + strings.Repeat("aZ09", 16) + " INFO x", local, 6, "1:" + strings.Repeat("aZ09", 16) + " INFO x"},
		raw("a process of eleven digits", stamp+"] 12345678901:abc INFO x"),
		raw("a request of 65 characters", stamp+"] 1:"+strings.Repeat("a", 65)+" INFO x"),
		raw("an empty request", stamp+"] 1: INFO x"),
		raw("a process without a colon", stamp+"] 4821 INFO x"),
		raw("a process and request without a space before the level", stamp+"] 4821:abcINFO"),
		raw("a request with an underscore", stamp+"] 4821:a_b INFO x"),
		raw("a prefix that is not digits", stamp+"] abc INFO x"),
		raw("seven fraction digits", stamp+".1234567] INFO x"),
		raw("a fraction without digits", stamp+".] INFO x"),
		raw("a fraction with a letter", stamp+".12a] INFO x"),
		raw("a lower-case level", stamp+"] info x"),
		raw("an unknown level", stamp+"] FATAL x"),
		raw("a level with a suffix", stamp+"] INFOx"),
		raw("no space behind the bracket", stamp+"]INFO x"),
		raw("no level", stamp+"] "),
		raw("no closing bracket", stamp+" INFO x"),
		raw("a space before the bracket", " "+stamp+"] INFO x"),
		raw("slashes in the date", "[2026/10/09 03:12:12] INFO x"),
		raw("a date with two digit year", "[26-10-09 03:12:12] INFO x"),
		raw("a time without seconds", "[2026-10-09 03:12] INFO x"),
		raw("an empty line", ""),
		raw("only the bracket", "["),
	}
	for i, level := range []string{"EMERG", "ALERT", "CRIT", "ERR", "WARN", "NOTICE", "INFO", "DEBUG"} {
		cases = append(cases, parseCase{"level " + level, stamp + "] " + level + " m", local, uint8(i), level + " m"}) // #nosec G115 -- eight levels
	}
	runCases(t, errorlogline.PleskPanel, cases)
}

func TestParse_NginxLines(t *testing.T) {
	const stamp = "2026/10/09 03:12:12"
	local := at(2026, time.October, 9, 1, 12, 12, 0)
	cases := []parseCase{
		{"a typical line", stamp + " [error] 1021#1021: *15 connect() failed", local, 3, "[error] 1021#1021: *15 connect() failed"},
		{"the level ends the line", stamp + " [warn]", local, 4, "[warn]"},
		{"a bracket in the message", stamp + " [info] a [b] c]", local, 6, "[info] a [b] c]"},
		{"the message keeps non-ASCII text", stamp + " [crit] Größe ü", local, 2, "[crit] Größe ü"},
		raw("an upper-case level", stamp+" [ERROR] x"),
		raw("a level nginx does not have", stamp+" [trace1] x"),
		raw("a level with a suffix", stamp+" [errors] x"),
		raw("a level with a space", stamp+" [error ] x"),
		raw("text behind the bracket", stamp+" [error]x"),
		raw("no bracket", stamp+" error x"),
		raw("no closing bracket", stamp+" [error x"),
		raw("two spaces before the bracket", stamp+"  [error] x"),
		raw("a fraction", "2026/10/09 03:12:12.5 [error] x"),
		raw("dashes in the date", "2026-10-09 03:12:12 [error] x"),
		raw("a date with two digit year", "26/10/09 03:12:12 [error] x"),
		raw("a leading space", " "+stamp+" [error] x"),
		raw("a time without seconds", "2026/10/09 03:12 [error] x"),
		raw("an empty line", ""),
	}
	for i, level := range []string{"emerg", "alert", "crit", "error", "warn", "notice", "info", "debug"} {
		cases = append(cases, parseCase{"level " + level, stamp + " [" + level + "] m", local, uint8(i), "[" + level + "] m"}) // #nosec G115 -- eight levels
	}
	runCases(t, errorlogline.Nginx, cases)
}

func TestParse_ApacheLines(t *testing.T) {
	const stamp = "[Fri Oct 09 03:12:12"
	local := at(2026, time.October, 9, 1, 12, 12, 0)
	module64 := strings.Repeat("aZ_9", 16)
	cases := []parseCase{
		{"with module and fraction", stamp + ".123456 2026] [core:error] [pid 1] AH00 x", at(2026, time.October, 9, 1, 12, 12, 123456000), 3, "[core:error] [pid 1] AH00 x"},
		{"without fraction", stamp + " 2026] [error] x", local, 3, "[error] x"},
		{"the level ends the line", stamp + " 2026] [warn]", local, 4, "[warn]"},
		{"a module of 64 characters", stamp + " 2026] [" + module64 + ":notice] x", local, 5, "[" + module64 + ":notice] x"},
		{"an empty module", stamp + " 2026] [:info] x", local, 6, "[:info] x"},
		{"a weekday that does not belong to the date", "[Mon Oct 09 03:12:12 2026] [error] x", local, 3, "[error] x"},
		{"the message keeps non-ASCII text", stamp + " 2026] [core:crit] Größe ü", local, 2, "[core:crit] Größe ü"},
		{"trace1", stamp + " 2026] [trace1] x", local, 7, "[trace1] x"},
		{"trace8 with a module", stamp + " 2026] [core:trace8] x", local, 7, "[core:trace8] x"},
		raw("trace0", stamp+" 2026] [trace0] x"),
		raw("trace9", stamp+" 2026] [trace9] x"),
		raw("trace without a number", stamp+" 2026] [trace] x"),
		raw("trace with two digits", stamp+" 2026] [trace10] x"),
		raw("a module of 65 characters", stamp+" 2026] ["+module64+"a:notice] x"),
		raw("a module with a dash", stamp+" 2026] [mod-x:error] x"),
		raw("two modules", stamp+" 2026] [a:b:error] x"),
		raw("an upper-case level", stamp+" 2026] [ERROR] x"),
		raw("text behind the bracket", stamp+" 2026] [error]x"),
		raw("no level bracket", stamp+" 2026] error x"),
		raw("no space between the brackets", stamp+" 2026][error] x"),
		raw("one fraction digit", stamp+".5 2026] [error] x"),
		raw("five fraction digits", stamp+".12345 2026] [error] x"),
		raw("seven fraction digits", stamp+".1234567 2026] [error] x"),
		raw("an unknown weekday", "[Xyz Oct 09 03:12:12 2026] [error] x"),
		raw("an unknown month", "[Fri Foo 09 03:12:12 2026] [error] x"),
		raw("a lower-case month", "[Fri oct 09 03:12:12 2026] [error] x"),
		raw("a day of one digit", "[Fri Oct  9 03:12:12 2026] [error] x"),
		raw("a year of three digits", stamp+" 202] [error] x"),
		raw("a year of five digits", stamp+" 20260] [error] x"),
		raw("no closing bracket", stamp+" 2026 [error] x"),
		raw("a leading space", " "+stamp+" 2026] [error] x"),
		raw("an empty line", ""),
		raw("only the bracket", "["),
	}
	for i, level := range []string{"emerg", "alert", "crit", "error", "warn", "notice", "info", "debug"} {
		cases = append(cases, parseCase{"level " + level, stamp + " 2026] [" + level + "] m", local, uint8(i), "[" + level + "] m"}) // #nosec G115 -- eight levels
	}
	runCases(t, errorlogline.Apache, cases)
}

func TestParse_LinesNotOfTheFormatAreRaw(t *testing.T) {
	formats := []struct {
		name   string
		format errorlogline.Format
		lines  []parseCase
	}{
		{"plesk-panel", errorlogline.PleskPanel, []parseCase{
			raw("a syslog line", "Oct  9 03:12:12 web-1 sshd[1]: x"),
			raw("an nginx line", "2026/10/09 03:12:12 [error] x"),
			raw("an apache line", "[Fri Oct 09 03:12:12 2026] [error] x"),
			raw("invalid UTF-8 stays as it is", "\xff\xfe not a line"),
			raw("30 February", "[2026-02-30 03:12:12] INFO x"),
			raw("month 13", "[2026-13-01 03:12:12] INFO x"),
			raw("month 0", "[2026-00-01 03:12:12] INFO x"),
			raw("day 0", "[2026-10-00 03:12:12] INFO x"),
			raw("hour 24", "[2026-10-09 24:00:00] INFO x"),
			raw("minute 60", "[2026-10-09 03:60:00] INFO x"),
			raw("second 60", "[2026-10-09 03:12:60] INFO x"),
			raw("year 1600", "[1600-01-01 00:00:00] INFO x"),
			raw("year 1676", "[1676-12-31 23:59:59] INFO x"),
			raw("year 2263", "[2263-01-01 00:00:00] INFO x"),
			raw("a second past the end of the storable range", "[2262-04-11 23:47:17] INFO x"),
			raw("a fraction that crosses the end of the storable range", "[2262-04-11 23:47:16.999999] INFO x"),
			raw("a second before the start of the storable range", "[1677-09-21 00:12:42] INFO x"),
		}},
		{"nginx", errorlogline.Nginx, []parseCase{
			raw("a syslog line", "Oct  9 03:12:12 web-1 sshd[1]: x"),
			raw("a plesk line", "[2026-10-09 03:12:12] INFO x"),
			raw("an apache line", "[Fri Oct 09 03:12:12 2026] [error] x"),
			raw("invalid UTF-8 stays as it is", "\xff\xfe not a line"),
			raw("30 February", "2026/02/30 03:12:12 [error] x"),
			raw("31 April", "2026/04/31 03:12:12 [error] x"),
			raw("29 February 2025", "2025/02/29 03:12:12 [error] x"),
			raw("month 13", "2026/13/01 03:12:12 [error] x"),
			raw("day 0", "2026/10/00 03:12:12 [error] x"),
			raw("hour 24", "2026/10/09 24:00:00 [error] x"),
			raw("minute 60", "2026/10/09 03:60:00 [error] x"),
			raw("second 60", "2026/10/09 03:12:60 [error] x"),
			raw("year 1600", "1600/01/01 00:00:00 [error] x"),
			raw("year 2300", "2300/01/01 00:00:00 [error] x"),
		}},
		{"apache", errorlogline.Apache, []parseCase{
			raw("a syslog line", "Oct  9 03:12:12 web-1 sshd[1]: x"),
			raw("a plesk line", "[2026-10-09 03:12:12] INFO x"),
			raw("an nginx line", "2026/10/09 03:12:12 [error] x"),
			raw("invalid UTF-8 stays as it is", "\xff\xfe not a line"),
			raw("30 February", "[Fri Feb 30 03:12:12 2026] [error] x"),
			raw("day 99", "[Fri Oct 99 03:12:12 2026] [error] x"),
			raw("day 0", "[Fri Oct 00 03:12:12 2026] [error] x"),
			raw("hour 24", "[Fri Oct 09 24:00:00 2026] [error] x"),
			raw("minute 60", "[Fri Oct 09 03:60:00 2026] [error] x"),
			raw("second 60", "[Fri Oct 09 03:12:60 2026] [error] x"),
			raw("year 1500", "[Fri Oct 09 03:12:12 1500] [error] x"),
			raw("year 2263", "[Fri Oct 09 03:12:12 2263] [error] x"),
			raw("a fraction that crosses the end of the storable range", "[Sat Apr 11 23:47:16.999999 2262] [error] x"),
		}},
	}
	for _, f := range formats {
		t.Run(f.name, func(t *testing.T) {
			runCases(t, f.format, f.lines)
		})
	}
}
