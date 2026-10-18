package procstats

import (
	"strings"
	"testing"
	"testing/fstest"
	"time"
)

func TestReadBootTime(t *testing.T) {
	line := func(n int) string { return strings.Repeat("1 ", n/2) + "\n" }
	tests := []struct {
		name string
		stat string
		want time.Time
		fail bool
	}{
		{"captured proc/stat", readFixture(t, "stat"), time.Unix(fixtureBtime, 0).UTC(), false},
		{"btime alone", "btime 1000\n", time.Unix(1000, 0).UTC(), false},
		{"btime without a final line feed", "cpu 1 2\nbtime 1000", time.Unix(1000, 0).UTC(), false},
		{"a line of 70 KiB before it", "intr " + line(70*1024) + "btime 1000\n", time.Unix(1000, 0).UTC(), false},
		{"a line of 70 KiB after it", "btime 1000\nintr " + line(70*1024) + "ctxt 5\n", time.Unix(1000, 0).UTC(), false},
		{"lines with the prefix of btime", "btimes 5\nbtime 1000\nxbtime 7\n", time.Unix(1000, 0).UTC(), false},
		{"missing", "cpu 1 2 3\nctxt 5\n", time.Time{}, true},
		{"empty", "", time.Time{}, true},
		{"twice", "btime 1000\nbtime 1000\n", time.Time{}, true},
		{"twice with a long line between", "btime 1000\nintr " + line(70*1024) + "btime 2000\n", time.Time{}, true},
		{"without a number", "btime \n", time.Time{}, true},
		{"not a number", "btime 10x\n", time.Time{}, true},
		{"with a sign", "btime +10\n", time.Time{}, true},
		{"two spaces", "btime  10\n", time.Time{}, true},
		{"a tab", "btime\t10\n", time.Time{}, true},
		{"21 digits", "btime 100000000000000000000\n", time.Time{}, true},
		{"beyond 64 bits", "btime 18446744073709551616\n", time.Time{}, true},
		{"after more than 1 MiB", strings.Repeat(strings.Repeat("a", 999)+"\n", 1100) + "btime 1000\n", time.Time{}, true},
		{"before more than 1 MiB in total", "btime 1000\n" + strings.Repeat(strings.Repeat("a", 999)+"\n", 1100), time.Time{}, true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			proc := fstest.MapFS{"stat": &fstest.MapFile{Data: []byte(tt.stat)}}

			got, err := readBootTime(proc)

			if (err != nil) != tt.fail {
				t.Fatalf("readBootTime() error = %v, want an error: %v", err, tt.fail)
			}
			if !tt.fail && !got.Equal(tt.want) {
				t.Errorf("readBootTime() = %v, want %v", got, tt.want)
			}
		})
	}

	t.Run("stat file missing", func(t *testing.T) {
		if _, err := readBootTime(fstest.MapFS{}); err == nil {
			t.Errorf("readBootTime(empty /proc) error = nil, want an error")
		}
	})
}
