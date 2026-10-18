package procstats

import (
	"strings"
	"testing"
)

// withoutLines returns data without the lines that start with one of the prefixes.
func withoutLines(data string, prefixes ...string) string {
	var kept []string
	for _, line := range strings.SplitAfter(data, "\n") {
		drop := false
		for _, p := range prefixes {
			drop = drop || strings.HasPrefix(line, p)
		}
		if !drop {
			kept = append(kept, line)
		}
	}
	return strings.Join(kept, "")
}

func TestParseSmapsRollup(t *testing.T) {
	const rollup = "00400000-7ffd1a94b000 ---p 00000000 00:00 0                              [rollup]\n"
	captured := readFixture(t, "6508/smaps_rollup")
	// The 5.15 shape: the 6.x lines are derived from the 6.18 capture by removing them.
	old := withoutLines(captured, "Pss_Dirty:", "Pss_Anon:", "Pss_File:", "Pss_Shmem:", "KSM:")
	tests := []struct {
		name      string
		data      string
		rss, pss  uint64
		wantError bool
	}{
		{"6.18 capture", captured, 93300 * kib, 45355 * kib, false},
		{"5.15 shape without Pss_Dirty", old, 93300 * kib, 45355 * kib, false},
		{"another capture", readFixture(t, "6514/smaps_rollup"), 89732 * kib, 44525 * kib, false},
		{"Pss_Dirty is not Pss", rollup + "Pss_Dirty: 9 kB\nRss: 5 kB\nPss: 3 kB\n", 5 * kib, 3 * kib, false},
		{"Pss before Rss", rollup + "Pss: 3 kB\nRss: 5 kB\n", 5 * kib, 3 * kib, false},
		{"zero values", rollup + "Rss: 0 kB\nPss: 0 kB\n", 0, 0, false},
		{"the first line is not read", "Rss: 1 kB [rollup]\n" + "Rss: 5 kB\nPss: 3 kB\n", 5 * kib, 3 * kib, false},
		{"largest values that fit 64 bits", rollup + "Rss: 18014398509481983 kB\nPss: 18014398509481983 kB\n", 18014398509481983 * kib, 18014398509481983 * kib, false},
		{"no trailing line feed", rollup + "Rss: 5 kB\nPss: 3 kB", 5 * kib, 3 * kib, false},
		{"missing Pss", rollup + "Rss: 5 kB\nPss_Dirty: 3 kB\n", 0, 0, true},
		{"missing Rss", rollup + "Pss: 5 kB\n", 0, 0, true},
		{"second Rss", rollup + "Rss: 5 kB\nPss: 3 kB\nRss: 5 kB\n", 0, 0, true},
		{"second Pss", rollup + "Rss: 5 kB\nPss: 3 kB\nPss: 3 kB\n", 0, 0, true},
		{"Rss times 1024 overflows", rollup + "Rss: 18014398509481984 kB\nPss: 3 kB\n", 0, 0, true},
		{"Pss times 1024 overflows", rollup + "Rss: 5 kB\nPss: 18014398509481984 kB\n", 0, 0, true},
		{"Rss of 21 digits", rollup + "Rss: 100000000000000000000 kB\nPss: 3 kB\n", 0, 0, true},
		{"Rss with another unit", rollup + "Rss: 5 mB\nPss: 3 kB\n", 0, 0, true},
		{"Rss without a unit", rollup + "Rss: 5\nPss: 3 kB\n", 0, 0, true},
		{"Pss that is not a number", rollup + "Rss: 5 kB\nPss: x kB\n", 0, 0, true},
		{"Pss with a sign", rollup + "Rss: 5 kB\nPss: -3 kB\n", 0, 0, true},
		{"empty", "", 0, 0, true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			rss, pss, err := parseSmapsRollup(tt.data)

			if (err != nil) != tt.wantError {
				t.Fatalf("parseSmapsRollup(%q) error = %v, want an error: %v", tt.data, err, tt.wantError)
			}
			if !tt.wantError && (rss != tt.rss || pss != tt.pss) {
				t.Errorf("parseSmapsRollup(%q) = rss %d, pss %d, want %d, %d", tt.data, rss, pss, tt.rss, tt.pss)
			}
		})
	}
}

func TestParseSmapsRollup_OldShapeDiffersFromTheCapture(t *testing.T) {
	captured := readFixture(t, "6508/smaps_rollup")

	old := withoutLines(captured, "Pss_Dirty:", "Pss_Anon:", "Pss_File:", "Pss_Shmem:", "KSM:")

	if strings.Contains(old, "Pss_Dirty") || !strings.Contains(captured, "Pss_Dirty") || !strings.Contains(old, "Pss:") {
		t.Errorf("test setup: the derived 5.15 shape must lack Pss_Dirty and keep Pss, the capture must hold Pss_Dirty")
	}
}
