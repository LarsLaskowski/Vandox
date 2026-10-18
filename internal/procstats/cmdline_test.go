package procstats

import (
	"strings"
	"testing"
	"unicode/utf8"
)

func TestParseCmdline(t *testing.T) {
	replacement := "\uFFFD"
	tests := []struct {
		name      string
		raw       string
		more      bool
		want      string
		truncated bool
	}{
		{"NUL-separated capture", readFixture(t, "6509/cmdline"), false, "/usr/bin/perl -e sleep 600 -- -k start", false},
		{"setproctitle capture with one trailing NUL", readFixture(t, "6508/cmdline"), false, "php-fpm: pool example.com", false},
		{"capture with line feeds", readFixture(t, "6510/cmdline"), false, pythonCmdline, false},
		{"capture of an empty command line", readFixture(t, "6511/cmdline"), false, "", false},
		{"trailing NULs only", "\x00\x00\x00", false, "", false},
		{"empty", "", false, "", false},
		{"trailing NULs removed, inner NULs become spaces", "a\x00\x00b\x00\x00", false, "a  b", false},
		{"embedded line feed kept", "a\nb\x00c\x00", false, "a\nb c", false},
		{"no NUL at all", "a b", false, "a b", false},
		{"more than the 4 KiB read", "abc", true, "abc", true},
		{"invalid byte in the middle", "a\x80b", false, "a" + replacement + "b", false},
		{"two invalid bytes", "\xff\xfe", false, replacement + replacement, false},
		{"sequence cut at the end", "a\xc3", false, "a" + replacement, false},
		{"valid multi-byte text", "caf\u00e9 \u20ac\x00", false, "caf\u00e9 \u20ac", false},
		{"1024 invalid bytes", strings.Repeat("\xff", 1024), false, strings.Repeat(replacement, 341), true},
		{"1024 bytes of text fit", strings.Repeat("a", 1024), false, strings.Repeat("a", 1024), false},
		{"1025 bytes of text are cut", strings.Repeat("a", 1025), false, strings.Repeat("a", 1024), true},
		{"character across byte 1024 is dropped", strings.Repeat("a", 1023) + "\u00e9", false, strings.Repeat("a", 1023), true},
		{"character ending at byte 1024 stays", strings.Repeat("a", 1022) + "\u00e9", false, strings.Repeat("a", 1022) + "\u00e9", false},
		{"4096 bytes of text", strings.Repeat("a", 4096), false, strings.Repeat("a", 1024), true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, truncated := parseCmdline([]byte(tt.raw), tt.more)

			if got != tt.want || truncated != tt.truncated {
				t.Errorf("parseCmdline(%q, %v) = %q, %v, want %q, %v", tt.raw, tt.more, got, truncated, tt.want, tt.truncated)
			}
			if len(got) > 1024 || !utf8.ValidString(got) {
				t.Errorf("parseCmdline(%q, %v) = %d bytes, valid UTF-8 %v, want at most 1024 bytes of valid UTF-8", tt.raw, tt.more, len(got), utf8.ValidString(got))
			}
		})
	}
}

func TestParseCmdline_CapturedEscapesStayOnDisk(t *testing.T) {
	raw := readFixture(t, "6510/cmdline")

	// The capture holds the characters backslash and n inside the Python source, besides the real line feeds.
	if !strings.Contains(raw, `\n`) || !strings.Contains(raw, "\n") {
		t.Fatalf("test setup: the capture lacks a backslash escape or a line feed: %q", raw)
	}
	got, _ := parseCmdline([]byte(raw), false)

	if !strings.Contains(got, `b"a\nUid:\t9\t9"`) {
		t.Errorf("parseCmdline(capture) = %q, want the escapes kept as written", got)
	}
}

func TestCleanText(t *testing.T) {
	tests := []struct {
		name  string
		s     string
		limit int
		want  string
		cut   bool
	}{
		{"short text", "abc", 10, "abc", false},
		{"text at the limit", "abc", 3, "abc", false},
		{"text over the limit", "abcd", 3, "abc", true},
		{"invalid byte", "a\xffb", 10, "a\uFFFDb", false},
		{"replacement over the limit", "a\xffb", 3, "a", true},
		{"character across the limit", "a\u00e9", 2, "a", true},
		{"character that fits", "a\u00e9", 3, "a\u00e9", false},
		{"empty", "", 5, "", false},
		{"valid text of 3-byte characters", "\u20ac\u20ac", 6, "\u20ac\u20ac", false},
		{"3-byte characters cut at a boundary", "\u20ac\u20ac\u20ac", 7, "\u20ac\u20ac", true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, cut := cleanText(tt.s, tt.limit)

			if got != tt.want || cut != tt.cut {
				t.Errorf("cleanText(%q, %d) = %q, %v, want %q, %v", tt.s, tt.limit, got, cut, tt.want, tt.cut)
			}
		})
	}
}
