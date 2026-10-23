package syslogline_test

import (
	"bufio"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/syslogline"
)

func prio(n uint8) *uint8 { return &n }

// wantLine is the expected result of Parse for a line that has a syslog header.
type wantLine struct {
	time     time.Time
	host     string
	program  string
	pid      int32
	priority *uint8
	message  string
}

func requireLine(t *testing.T, in string, got syslogline.Line, want wantLine) {
	t.Helper()
	if !got.Header {
		t.Errorf("Parse(%.80q).Header = false, want true", in)
		return
	}
	if !got.Time.Equal(want.time) || got.Time.Location() != time.UTC {
		t.Errorf("Parse(%.80q).Time = %v, want %v in UTC", in, got.Time, want.time)
	}
	if got.Host != want.host || got.Program != want.program || got.PID != want.pid || got.Message != want.message {
		t.Errorf("Parse(%.80q) = host %q program %q pid %d message %.60q, want host %q program %q pid %d message %.60q",
			in, got.Host, got.Program, got.PID, got.Message, want.host, want.program, want.pid, want.message)
	}
	if (got.Priority == nil) != (want.priority == nil) || (got.Priority != nil && *got.Priority != *want.priority) {
		t.Errorf("Parse(%.80q).Priority = %v, want %v", in, describePriority(got.Priority), describePriority(want.priority))
	}
}

func describePriority(p *uint8) string {
	if p == nil {
		return "none"
	}
	return string(rune('0' + *p))
}

// parseNow is the time of reading of the Parse tests: a year-less time resolves in 2026.
var parseNow = utc(2026, time.October, 23, 7, 20, 0)

func TestParse_ReadsTheImportsSyslogForms(t *testing.T) {
	long := strings.Repeat("p", 129)
	tests := []struct {
		name string
		line string
		want wantLine
	}{
		{"traditional time, host and tag with a process ID", "Oct 23 07:13:02 web-1 postfix/smtpd[4711]: connect from unknown[192.0.2.10]",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "postfix/smtpd", 4711, nil, "connect from unknown[192.0.2.10]"}},
		{"a day with a leading space", "Oct  9 03:12:01 mail-1 postfix/master[812]: daemon started",
			wantLine{utc(2026, time.October, 9, 3, 12, 1), "mail-1", "postfix/master", 812, nil, "daemon started"}},
		{"a day with a leading zero", "Oct 09 03:12:01 mail-1 cron[8]: m",
			wantLine{utc(2026, time.October, 9, 3, 12, 1), "mail-1", "cron", 8, nil, "m"}},
		{"a priority is taken modulo 8", "<13>Oct 23 07:13:02 web-1 sshd[1]: m",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "sshd", 1, prio(5), "m"}},
		{"priority 0", "<0>Oct 23 07:13:02 web-1 sshd[1]: m",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "sshd", 1, prio(0), "m"}},
		{"priority 191", "<191>Oct 23 07:13:02 web-1 sshd[1]: m",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "sshd", 1, prio(7), "m"}},
		{"a tag without a process ID", "Oct 23 07:13:02 mail-1 dovecot: master: Dovecot v2.3.16 starting up",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "mail-1", "dovecot", 0, nil, "master: Dovecot v2.3.16 starting up"}},
		{"a line without a tag", "Oct 23 07:13:02 web-1 just some text",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "", 0, nil, "just some text"}},
		{"only one space after the colon is dropped", "Oct 23 07:13:02 web-1 prog:  two spaces",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "prog", 0, nil, " two spaces"}},
		{"no space after the colon", "Oct 23 07:13:02 web-1 prog:text",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "prog", 0, nil, "text"}},
		{"an empty message", "Oct 23 07:13:02 web-1 prog[3]: ",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "prog", 3, nil, ""}},
		{"the largest process ID", "Oct 23 07:13:02 web-1 prog[2147483647]: m",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "prog", 2147483647, nil, "m"}},
		{"a process ID above the largest gives 0 and keeps the tag", "Oct 23 07:13:02 web-1 prog[4294967296]: m",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "prog", 0, nil, "m"}},
		{"a program with several slashes", "Oct 23 07:13:02 mail-1 postfix/submission/smtpd[2214]: connect",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "mail-1", "postfix/submission/smtpd", 2214, nil, "connect"}},
		{"a program of more than 128 bytes is no tag", "Oct 23 07:13:02 web-1 " + long + ": m",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "", 0, nil, long + ": m"}},
		{"a program of 128 bytes is a tag", "Oct 23 07:13:02 web-1 " + long[:128] + ": m",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", long[:128], 0, nil, "m"}},
		{"a host of 255 bytes", "Oct 23 07:13:02 " + strings.Repeat("h", 255) + " x: m",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), strings.Repeat("h", 255), "x", 0, nil, "m"}},
		{"RFC 3339 time with a zone letter", "2026-10-23T07:13:02Z web-1 sshd[9]: m",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "sshd", 9, nil, "m"}},
		{"RFC 3339 time with an offset ignores the zone of the file", "2026-10-23T07:13:02+02:00 web-1 sshd[9]: m",
			wantLine{utc(2026, time.October, 23, 5, 13, 2), "web-1", "sshd", 9, nil, "m"}},
		{"RFC 3339 time with a negative offset and a fraction", "2026-10-23T07:13:02.123456-05:30 web-1 sshd[9]: m",
			wantLine{time.Date(2026, time.October, 23, 12, 43, 2, 123456000, time.UTC), "web-1", "sshd", 9, nil, "m"}},
		{"RFC 3339 time with a priority", "<30>2026-10-23T07:13:02Z web-1 sshd[9]: m",
			wantLine{utc(2026, time.October, 23, 7, 13, 2), "web-1", "sshd", 9, prio(6), "m"}},
		{"RFC 3339 time with the largest offset", "2026-10-23T07:13:02+14:00 web-1 x: m",
			wantLine{utc(2026, time.October, 22, 17, 13, 2), "web-1", "x", 0, nil, "m"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := syslogline.Parse([]byte(tt.line), syslogline.NewClock(time.UTC), parseNow)

			requireLine(t, tt.line, got, tt.want)
		})
	}
}

func TestParse_LineWithoutHeaderIsRaw(t *testing.T) {
	tests := []struct {
		name string
		line string
	}{
		{"an RFC 5424 line", "<34>1 2026-10-23T07:13:02Z web-1 su 1234 ID47 - 'su root' failed"},
		{"a continuation line", "    at com.example.Main.run(Main.java:42)"},
		{"plain text", "hello world"},
		{"an empty line", ""},
		{"a time without a host", "Oct 23 07:13:02"},
		{"a time with a space and no host", "Oct 23 07:13:02 "},
		{"an unknown month", "Foo 23 07:13:02 web-1 x: m"},
		{"a priority above 191", "<192>Oct 23 07:13:02 web-1 x: m"},
		{"an empty priority", "<>Oct 23 07:13:02 web-1 x: m"},
		{"a priority of four digits", "<1234>Oct 23 07:13:02 web-1 x: m"},
		{"a priority that is not closed", "<13 Oct 23 07:13:02 web-1 x: m"},
		{"day 32", "Oct 32 07:13:02 web-1 x: m"},
		{"hour 24", "Oct 23 24:00:00 web-1 x: m"},
		{"minute 60", "Oct 23 07:60:00 web-1 x: m"},
		{"second 60", "Oct 23 07:00:60 web-1 x: m"},
		{"a lower-case t in an RFC 3339 time", "2026-10-23t07:13:02Z web-1 x: m"},
		{"a lower-case z in an RFC 3339 time", "2026-10-23T07:13:02z web-1 x: m"},
		{"an RFC 3339 time without a zone", "2026-10-23T07:13:02 web-1 x: m"},
		{"an offset of more than 14 hours", "2026-10-23T07:13:02+14:30 web-1 x: m"},
		{"an offset of 15 hours", "2026-10-23T07:13:02+15:00 web-1 x: m"},
		{"a fraction of ten digits", "2026-10-23T07:13:02.1234567890Z web-1 x: m"},
		{"a point without fraction digits", "2026-10-23T07:13:02.Z web-1 x: m"},
		{"a host of 256 bytes", "Oct 23 07:13:02 " + strings.Repeat("h", 256) + " x: m"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := syslogline.Parse([]byte(tt.line), syslogline.NewClock(time.UTC), parseNow)

			if got.Header || !got.Time.IsZero() || got.Host != "" || got.Program != "" || got.PID != 0 || got.Priority != nil {
				t.Errorf("Parse(%.60q) = header %v time %v host %q program %q pid %d priority %v, want a line without header fields",
					tt.line, got.Header, got.Time, got.Host, got.Program, got.PID, got.Priority)
			}
			if got.Message != tt.line {
				t.Errorf("Parse(%.60q).Message = %.60q, want the whole line", tt.line, got.Message)
			}
		})
	}
}

// fixtureLines returns the lines of a file below testdata/logs.
func fixtureLines(t *testing.T, name string) []string {
	t.Helper()
	f, err := os.Open(filepath.Join("..", "..", "testdata", "logs", name))
	if err != nil {
		t.Fatalf("opening %s: %v", name, err)
	}
	defer func() { _ = f.Close() }()
	var lines []string
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		lines = append(lines, sc.Text())
	}
	if err := sc.Err(); err != nil || len(lines) == 0 {
		t.Fatalf("reading %s: %d lines, error %v, want lines", name, len(lines), err)
	}
	return lines
}

func TestParse_ReadsTheLinesOfTheImportFixtures(t *testing.T) {
	t.Run("mail.log", func(t *testing.T) {
		lines := fixtureLines(t, "mail.log")
		checks := []struct {
			index int
			want  wantLine
		}{
			{0, wantLine{utc(2026, time.October, 9, 3, 12, 1), "mail-1", "postfix/master", 812, nil, "daemon started -- version 3.6.4, configuration /etc/postfix"}},
			{1, wantLine{utc(2026, time.October, 9, 3, 12, 5), "mail-1", "postfix/smtpd", 2210, nil, "connect from unknown[192.0.2.9]"}},
			{3, wantLine{utc(2026, time.October, 9, 3, 12, 6), "mail-1", "postfix/submission/smtpd", 2214, nil, "connect from client.example.org[198.51.100.7]"}},
			{9, wantLine{utc(2026, time.October, 9, 3, 12, 11), "mail-1", "postfix/cleanup", 2240, nil, "panic: myfree: corrupt or unallocated memory block"}},
			{10, wantLine{utc(2026, time.October, 9, 3, 12, 12), "mail-1", "dovecot", 0, nil, "master: Dovecot v2.3.16 (7e2e900c1a) starting up for imap, pop3 (core dumps disabled)"}},
			{16, wantLine{utc(2026, time.October, 9, 3, 12, 18), "mail-1", "dovecot", 0, nil, "auth: Fatal: master: service(auth): child 2290 returned error 89"}},
			{17, wantLine{utc(2026, time.October, 9, 3, 12, 19), "mail-1", "psa-pc-remote", 1102, nil, "Error: connect to database failed"}},
		}
		for _, c := range checks {
			requireLine(t, lines[c.index], syslogline.Parse([]byte(lines[c.index]), syslogline.NewClock(time.UTC), parseNow), c.want)
		}
		clock := syslogline.NewClock(time.UTC)
		for i, line := range lines {
			if got := syslogline.Parse([]byte(line), clock, parseNow); !got.Header || got.Host != "mail-1" || got.Program == "" || got.Message == "" {
				t.Errorf("line %d Parse(%.70q) = header %v host %q program %q message %.30q, want a line of host mail-1 with a program and a message", i+1, line, got.Header, got.Host, got.Program, got.Message)
			}
		}
	})

	t.Run("kern.log-oom", func(t *testing.T) {
		lines := fixtureLines(t, "kern.log-oom")
		clock := syslogline.NewClock(time.UTC)
		for i, line := range lines {
			got := syslogline.Parse([]byte(line), clock, parseNow)
			if !got.Header || got.Host != "web-1" || got.Program != "kernel" || got.PID != 0 || !strings.HasPrefix(got.Message, "[") {
				t.Errorf("line %d Parse(%.70q) = header %v host %q program %q pid %d message %.30q, want host web-1, program kernel, pid 0 and a message that starts with the kernel time", i+1, line, got.Header, got.Host, got.Program, got.PID, got.Message)
			}
		}
		requireLine(t, lines[1], syslogline.Parse([]byte(lines[1]), syslogline.NewClock(time.UTC), parseNow),
			wantLine{utc(2026, time.March, 1, 12, 30, 15), "web-1", "kernel", 0, nil, strings.TrimPrefix(lines[1], "Mar  1 12:30:15 web-1 kernel: ")})
	})
}
