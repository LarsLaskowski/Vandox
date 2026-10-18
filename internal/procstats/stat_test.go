package procstats

import (
	"strconv"
	"strings"
	"testing"
)

// statTail builds the part of a stat file after "<pid> (<comm>) " from the fields 3 to 22 and returns it with n fields.
func statTail(n int, set map[int]string) string {
	fields := make([]string, n)
	for i := range fields {
		fields[i] = "0"
	}
	fields[0] = "S"
	for field, v := range set {
		if field-3 < n {
			fields[field-3] = v
		}
	}
	return strings.Join(fields, " ") + "\n"
}

func TestParseStat(t *testing.T) {
	valid := map[int]string{3: "S", 4: "7", 9: "4194304", 14: "11", 15: "22", 22: "33"}
	line := func(pid int, comm string) string {
		return strconv.Itoa(pid) + " (" + comm + ") " + statTail(50, valid)
	}
	want := func(comm string) statFields {
		return statFields{pid: 100, ppid: 7, comm: comm, state: 'S', flags: 4194304, utime: 11, stime: 22, starttime: 33}
	}
	tests := []struct {
		name string
		pid  int32
		data string
		want statFields
		fail bool
	}{
		{"captured process", pidFpmMaster, readFixture(t, "6508/stat"),
			statFields{pid: 6508, ppid: 6507, comm: "php-fpm: pool e", state: 'S', flags: 4194304, utime: 219, stime: 8, starttime: 192559}, false},
		{"captured comm with a line feed and a tab", pidPrctl, readFixture(t, "6510/stat"),
			statFields{pid: 6510, ppid: 6507, comm: "a\nUid:\t9\t9", state: 'S', flags: 4194304, utime: 2, stime: 0, starttime: 192559}, false},
		{"captured empty comm", pidUnnamed, readFixture(t, "6511/stat"),
			statFields{pid: 6511, ppid: 6507, comm: "", state: 'S', flags: 4194304, starttime: 192559}, false},
		{"captured kernel thread", pidKthread, readFixture(t, "12/stat"),
			statFields{pid: 12, ppid: 2, comm: "kworker/u16:0-kvfree_rcu_reclaim", state: 'I', flags: 69239136, utime: 0, stime: 22, starttime: 35}, false},
		{"captured zombie", pidZombie, readFixture(t, "6513/stat"),
			statFields{pid: 6513, ppid: 6512, comm: "perl", state: 'Z', flags: 4227148, starttime: 192559}, false},
		{"comm with a closing parenthesis", 100, line(100, "a) b"), want("a) b"), false},
		{"comm with a parenthesis and a space", 100, line(100, "x) (y"), want("x) (y"), false},
		{"comm with an opening parenthesis", 100, line(100, "(("), want("(("), false},
		{"comm that imitates later fields", 100, line(100, "x) R 1 1 1"), want("x) R 1 1 1"), false},
		{"comm with a line feed", 100, line(100, "a\nUid:\t9\t9"), want("a\nUid:\t9\t9"), false},
		{"empty comm", 100, line(100, ""), want(""), false},
		{"comm of 4000 bytes", 100, line(100, strings.Repeat("c", 4000)), want(strings.Repeat("c", 4000)), false},
		{"exactly 20 fields after comm", 100, "100 (a) " + statTail(20, valid), want("a"), false},
		{"largest 32-bit values", 100,
			"100 (a) " + statTail(20, map[int]string{3: "R", 4: "2147483647", 9: "4294967295", 14: "18446744073709551615", 15: "0", 22: "18446744073709551615"}),
			statFields{pid: 100, ppid: 2147483647, comm: "a", state: 'R', flags: 4294967295, utime: 18446744073709551615, starttime: 18446744073709551615}, false},
		{"ppid 0", 100, "100 (a) " + statTail(20, map[int]string{3: "S", 4: "0"}), statFields{pid: 100, comm: "a", state: 'S'}, false},
		{"19 fields after comm", 100, "100 (a) " + statTail(19, valid), statFields{}, true},
		{"no comm", 100, "100 " + statTail(20, valid), statFields{}, true},
		{"no closing parenthesis", 100, "100 (a S 1 2 3", statFields{}, true},
		{"empty", 100, "", statFields{}, true},
		{"pid differs from the directory", 101, line(100, "a"), statFields{}, true},
		{"pid 0", 0, line(0, "a"), statFields{}, true},
		{"negative ppid", 100, "100 (a) " + statTail(20, map[int]string{4: "-1"}), statFields{}, true},
		{"ppid beyond 32 bits", 100, "100 (a) " + statTail(20, map[int]string{4: "2147483648"}), statFields{}, true},
		{"flags beyond 32 bits", 100, "100 (a) " + statTail(20, map[int]string{9: "4294967296"}), statFields{}, true},
		{"utime not a number", 100, "100 (a) " + statTail(20, map[int]string{14: "1x"}), statFields{}, true},
		{"stime with a sign", 100, "100 (a) " + statTail(20, map[int]string{15: "+1"}), statFields{}, true},
		{"starttime of 21 digits", 100, "100 (a) " + statTail(20, map[int]string{22: "100000000000000000000"}), statFields{}, true},
		{"starttime beyond 64 bits", 100, "100 (a) " + statTail(20, map[int]string{22: "18446744073709551616"}), statFields{}, true},
		{"empty field from a double space", 100, "100 (a) S  " + statTail(19, valid), statFields{}, true},
		{"comm with a NUL", 100, line(100, "a\x00b"), statFields{}, true},
		{"comm not followed by a space", 100, "100 (a)S " + statTail(19, valid), statFields{}, true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := parseStat(tt.pid, tt.data)

			if (err != nil) != tt.fail {
				t.Fatalf("parseStat(%d, %q) error = %v, want an error: %v", tt.pid, tt.data, err, tt.fail)
			}
			if !tt.fail && got != tt.want {
				t.Errorf("parseStat(%d, %q) = %+v, want %+v", tt.pid, tt.data, got, tt.want)
			}
		})
	}
}
