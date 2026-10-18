package procstats

import (
	"strings"
	"testing"
)

func TestParseStatus(t *testing.T) {
	const head = "Name:\tx\nUmask:\t0022\nState:\tS (sleeping)\nTgid:\t5\n"
	const uid = "Uid:\t1000\t33\t1000\t1000\n"
	tests := []struct {
		name string
		data string
		want statusFields
		fail bool
	}{
		{"captured process", readFixture(t, "6508/status"), statusFields{euid: 0, rss: 93300 * kib, swap: 0, hasRSS: true, hasSwap: true}, false},
		{"captured process with another effective UID", readFixture(t, "6514/status"), statusFields{euid: 33, rss: 89732 * kib, hasRSS: true, hasSwap: true}, false},
		{"captured name that holds an escaped line feed", readFixture(t, "6510/status"), statusFields{euid: 0, rss: 9952 * kib, hasRSS: true, hasSwap: true}, false},
		{"captured kernel thread without VmRSS", readFixture(t, "12/status"), statusFields{euid: 0}, false},
		{"captured zombie without VmRSS", readFixture(t, "6513/status"), statusFields{euid: 0}, false},
		{"effective UID is the second value", head + uid + "VmRSS:\t100 kB\n", statusFields{euid: 33, rss: 100 * kib, hasRSS: true}, false},
		{"VmRSS and VmSwap with tabs and spaces", head + uid + "VmRSS:\t  \t12 kB\nVmSwap:  7 kB\n", statusFields{euid: 33, rss: 12 * kib, swap: 7 * kib, hasRSS: true, hasSwap: true}, false},
		{"largest VmRSS that fits 64 bits", head + uid + "VmRSS:\t18014398509481983 kB\n", statusFields{euid: 33, rss: 18014398509481983 * kib, hasRSS: true}, false},
		{"largest UIDs", head + "Uid:\t4294967295\t4294967295\t4294967295\t4294967295\nVmRSS:\t1 kB\n", statusFields{euid: 4294967295, rss: kib, hasRSS: true}, false},
		{"other fields are not read", head + uid + "VmRSSx:\t9 kB\nVmSwapx:\t9 kB\nXUid:\t1\t2\t3\t4\n VmRSS:\t9 kB\nVmRSS:\t5 kB\n", statusFields{euid: 33, rss: 5 * kib, hasRSS: true}, false},
		{"no trailing line feed", head + uid + "VmRSS:\t3 kB", statusFields{euid: 33, rss: 3 * kib, hasRSS: true}, false},
		{"second Uid", head + uid + uid + "VmRSS:\t1 kB\n", statusFields{}, true},
		{"second VmRSS", head + uid + "VmRSS:\t1 kB\nVmRSS:\t1 kB\n", statusFields{}, true},
		{"second VmSwap", head + uid + "VmSwap:\t1 kB\nVmSwap:\t1 kB\n", statusFields{}, true},
		{"VmRSS times 1024 overflows", head + uid + "VmRSS:\t18014398509481984 kB\n", statusFields{}, true},
		{"VmSwap times 1024 overflows", head + uid + "VmSwap:\t18014398509481984 kB\n", statusFields{}, true},
		{"VmRSS of 21 digits", head + uid + "VmRSS:\t100000000000000000000 kB\n", statusFields{}, true},
		{"VmRSS without a unit", head + uid + "VmRSS:\t100\n", statusFields{}, true},
		{"VmRSS with another unit", head + uid + "VmRSS:\t100 mB\n", statusFields{}, true},
		{"VmRSS without a number", head + uid + "VmRSS:\t kB\n", statusFields{}, true},
		{"VmRSS with a sign", head + uid + "VmRSS:\t+1 kB\n", statusFields{}, true},
		{"VmRSS with trailing text", head + uid + "VmRSS:\t1 kB x\n", statusFields{}, true},
		{"Uid with three values", head + "Uid:\t1\t2\t3\n", statusFields{}, true},
		{"Uid with five values", head + "Uid:\t1\t2\t3\t4\t5\n", statusFields{}, true},
		{"Uid separated by spaces", head + "Uid: 1 2 3 4\n", statusFields{}, true},
		{"Uid beyond 32 bits", head + "Uid:\t1\t4294967296\t1\t1\n", statusFields{}, true},
		{"Uid that is not a number", head + "Uid:\t1\tx\t1\t1\n", statusFields{}, true},
		{"negative Uid", head + "Uid:\t1\t-1\t1\t1\n", statusFields{}, true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := parseStatus(tt.data)

			if (err != nil) != tt.fail {
				t.Fatalf("parseStatus(%q) error = %v, want an error: %v", tt.data, err, tt.fail)
			}
			if !tt.fail && got != tt.want {
				t.Errorf("parseStatus(%q) = %+v, want %+v", tt.data, got, tt.want)
			}
		})
	}
}

func TestParseStatus_CapturedNameCannotStartALine(t *testing.T) {
	data := readFixture(t, "6510/status")

	if !strings.Contains(data, `Name:`+"\t"+`a\nUid:`+"\t9\t9\n") {
		t.Fatalf("test setup: the capture does not hold the escaped line feed of the name; status:\n%s", data)
	}
	got, err := parseStatus(data)

	if err != nil || got.euid != 0 {
		t.Errorf("parseStatus(capture) = %+v, %v, want the effective UID 0 (the name %q holds no line start) and no error", got, err, `a\nUid:`)
	}
}
