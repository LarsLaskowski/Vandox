package sysstats_test

import (
	"slices"
	"strings"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/sysstats"
)

// mountLine returns a mountinfo line whose mount point is point.
func mountLine(point string) string {
	return "60 28 8:1 / " + point + " rw,relatime - ext4 /dev/vdb rw"
}

// escapeSources returns sources that read the fixture mount table with lines appended, and a fake statfs for the paths.
func escapeSources(t *testing.T, lines []string, paths ...string) (sysstats.Sources, *statFake) {
	t.Helper()

	tr := loadTree(t)
	table := text(t, tr.proc, "self/mountinfo")
	for _, l := range lines {
		table += l + "\n"
	}
	set(tr.proc, "self/mountinfo", table)
	fake := newStatFake()
	for _, p := range paths {
		fake.stats[p] = rootStats
	}
	src := tr.sources()
	src.StatFS = fake.statFS
	return src, fake
}

func TestFilesystem_MountinfoEscapes(t *testing.T) {
	t.Run("escaped space and backslash match the configured path", requireEscapesMatch)
	t.Run("escaped tab and line feed are read without error", requireControlEscapesRead)
	t.Run("a bad line fails the sample", requireBadLinesFail)
	t.Run("a line of 65536 bytes is read", func(t *testing.T) {
		long := mountLine("/srv/" + strings.Repeat("a", 65536-len(mountLine("/srv/"))))
		if len(long) != 65536 {
			t.Fatalf("test line has %d bytes, want 65536", len(long))
		}
		src, _ := escapeSources(t, []string{long}, "/")

		got := mustCollect(t, sysstats.NewFilesystem(src, []string{"/"}, nil), runStart)

		requireRecords(t, runStart, got, rootRecords())
	})
	t.Run("the failing line is numbered", func(t *testing.T) {
		src, _ := escapeSources(t, []string{mountLine("/srv/ok"), "x"}, "/")

		requireFails(t, sysstats.NewFilesystem(src, []string{"/"}, nil), runStart, "sysstats: self/mountinfo: line 7: not a mountinfo line")
	})
}

func requireEscapesMatch(t *testing.T) {
	matches := []struct {
		name       string
		mountPoint string // as written in the mount table
		configured string
	}{
		{"escaped space", `/srv/a\040b`, "/srv/a b"},
		{"escaped backslash", `/srv/b\134x`, `/srv/b\x`},
	}
	for _, tt := range matches {
		t.Run(tt.name, func(t *testing.T) {
			if !strings.Contains(tt.mountPoint, `\`) {
				t.Fatalf("test input %q lost its backslash", tt.mountPoint)
			}
			src, fake := escapeSources(t, []string{mountLine(tt.mountPoint)}, tt.configured)
			c := sysstats.NewFilesystem(src, []string{tt.configured}, nil)

			got := mustCollect(t, c, runStart)

			sizes := find(got, "filesystem_size_bytes")
			if len(sizes) != 1 || sizes[0].labels["mount"] != tt.configured {
				t.Errorf("filesystem_size_bytes records = %v, want one with the configured mount point", sizes)
			}
			if calls := fake.called(); !slices.Equal(calls, []string{tt.configured}) {
				t.Errorf("statfs was asked for %q, want %q", calls, []string{tt.configured})
			}
		})
	}
}

func requireControlEscapesRead(t *testing.T) {
	lines := []string{mountLine(`/srv/t\011x`), mountLine(`/srv/n\012x`)}
	for _, l := range lines {
		if !strings.Contains(l, `\0`) {
			t.Fatalf("test input %q lost its backslash", l)
		}
	}
	src, _ := escapeSources(t, lines, "/")
	c := sysstats.NewFilesystem(src, []string{"/"}, nil)

	got := mustCollect(t, c, runStart)

	requireRecords(t, runStart, got, rootRecords())
}

func requireBadLinesFail(t *testing.T) {
	const line6 = "sysstats: self/mountinfo: line 6: "
	const notALine = line6 + "not a mountinfo line"
	badLines := []struct {
		name string
		line string
		want string
	}{
		{"two octal digits", mountLine(`/srv/a\04`), notALine},
		{"one octal digit", mountLine(`/srv/a\0`), notALine},
		{"backslash at the end of the field", mountLine(`/srv/a\`), notALine},
		{"backslash and 8", mountLine(`/srv/a\8`), notALine},
		{"backslash and 9", mountLine(`/srv/a\9`), notALine},
		{"octal digit then 8", mountLine(`/srv/a\089`), notALine},
		{"4 fields", "60 28 8:1 /", notALine},
		{"line of 65537 bytes", mountLine("/srv/" + strings.Repeat("a", 65537-len(mountLine("/srv/")))), line6 + "longer than 65536 bytes"},
	}
	for _, tt := range badLines {
		t.Run(tt.name, func(t *testing.T) {
			src, _ := escapeSources(t, []string{tt.line}, "/")

			requireFails(t, sysstats.NewFilesystem(src, []string{"/"}, nil), runStart, tt.want)
		})
	}
}
