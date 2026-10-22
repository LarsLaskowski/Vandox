package kernevents

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/journal/journaltest"
	"github.com/LarsLaskowski/Vandox/internal/model"
)

// killLine returns a kill line of the kernel with the process name comm and the values of the test.
func killLine(comm string) string {
	return "Out of memory: Killed process 4242 (" + comm + ") total-vm:1000kB, anon-rss:2048kB, file-rss:0kB, shmem-rss:0kB, UID:0 pgtables:12kB oom_score_adj:-7"
}

// mustParse parses a message that must be a kill line.
func mustParse(t *testing.T, msg string) model.OOMKill {
	t.Helper()
	got, ok := parseKill([]byte(msg))
	if !ok {
		t.Fatalf("parseKill(%.120q) ok = false, want true", msg)
	}
	return got
}

// requireValues checks the pid, rss and score of a parsed kill; a nil pointer in the want means absent.
func requireValues(t *testing.T, name string, got model.OOMKill, pid int32, rss *uint64, adj *int16) {
	t.Helper()
	if got.VictimPID != pid {
		t.Errorf("%s: VictimPID = %d, want %d", name, got.VictimPID, pid)
	}
	switch {
	case rss == nil && got.AnonRSSBytes != nil:
		t.Errorf("%s: AnonRSSBytes = %d, want absent", name, *got.AnonRSSBytes)
	case rss != nil && got.AnonRSSBytes == nil:
		t.Errorf("%s: AnonRSSBytes absent, want %d", name, *rss)
	case rss != nil && *got.AnonRSSBytes != *rss:
		t.Errorf("%s: AnonRSSBytes = %d, want %d", name, *got.AnonRSSBytes, *rss)
	}
	switch {
	case adj == nil && got.OOMScoreAdj != nil:
		t.Errorf("%s: OOMScoreAdj = %d, want absent", name, *got.OOMScoreAdj)
	case adj != nil && got.OOMScoreAdj == nil:
		t.Errorf("%s: OOMScoreAdj absent, want %d", name, *adj)
	case adj != nil && *got.OOMScoreAdj != *adj:
		t.Errorf("%s: OOMScoreAdj = %d, want %d", name, *got.OOMScoreAdj, *adj)
	}
}

func u64p(v uint64) *uint64 { return &v }

func i16p(v int16) *int16 { return &v }

// requireValidEvent checks that a parsed kill makes a valid kernel event.
func requireValidEvent(t *testing.T, name string, o model.OOMKill) {
	t.Helper()
	ev := &model.KernelEvent{Type: model.KernelEventOOMKill, OOMKill: &o}
	if err := ev.Validate(); err != nil {
		t.Errorf("%s: the event of %+v: Validate() error = %v, want nil", name, o, err)
	}
}

// realLines returns the real MESSAGE lines of the committed export (those not marked as hand-made).
func realLines(t *testing.T) [][]byte {
	t.Helper()
	f, err := os.Open(filepath.Join("..", "..", "testdata", "journal", "kernel.export"))
	if err != nil {
		t.Fatalf("opening the export: %v", err)
	}
	defer func() { _ = f.Close() }()
	entries, err := journaltest.ReadExport(f)
	if err != nil {
		t.Fatalf("ReadExport() error = %v, want nil", err)
	}
	var out [][]byte
	for _, e := range entries {
		if string(e["VANDOX_FIXTURE"]) != "handmade" {
			out = append(out, e["MESSAGE"])
		}
	}
	return out
}

func TestParseKill_RealLines(t *testing.T) {
	want := map[int32]struct {
		comm string
		kb   uint64
	}{3021: {"/usr/sbin/amavi", 145456}, 352448: {"pzstd", 192940}, 351416: {"mariadbd", 175188}}
	kills, others := 0, 0
	for _, msg := range realLines(t) {
		got, ok := parseKill(msg)
		if !bytes.HasPrefix(msg, []byte("Out of memory: Killed process")) {
			others++
			if ok {
				t.Errorf("parseKill(%.80q) ok = true, want false for a real line that is no kill line", msg)
			}
			continue
		}
		kills++
		w, known := want[got.VictimPID]
		if !ok || !known {
			t.Fatalf("parseKill(%.80q) = %+v, %v, want one of the real kills 3021, 352448, 351416", msg, got, ok)
		}
		requireValues(t, w.comm, got, got.VictimPID, u64p(w.kb*1024), i16p(0))
		if got.VictimCommand != w.comm {
			t.Errorf("parseKill(%.80q) VictimCommand = %q, want %q", msg, got.VictimCommand, w.comm)
		}
		requireValidEvent(t, w.comm, got)
	}
	if kills != 3 || others != 4 {
		t.Errorf("the export holds %d real kill lines and %d other real lines, want 3 and 4", kills, others)
	}
}

func TestParseKill_AllKillForms(t *testing.T) {
	tests := []struct {
		name   string
		prefix string
	}{
		{"global", "Out of memory: Killed process "},
		{"memory cgroup", "Memory cgroup out of memory: Killed process "},
		{"allocating task", "Out of memory (oom_kill_allocating_task): Killed process "},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			msg := tc.prefix + "3141 (mariadbd) total-vm:1099584kB, anon-rss:175188kB, file-rss:0kB, shmem-rss:0kB, UID:114 pgtables:572kB oom_score_adj:-900"
			got := mustParse(t, msg)
			requireValues(t, tc.name, got, 3141, u64p(175188*1024), i16p(-900))
			if got.VictimCommand != "mariadbd" {
				t.Errorf("parseKill(%q) VictimCommand = %q, want %q", msg, got.VictimCommand, "mariadbd")
			}
			requireValidEvent(t, tc.name, got)
		})
	}
}

func TestParseKill_HostileNames(t *testing.T) {
	long := strings.Repeat("l", 3000)
	repl := string(rune(0xFFFD))
	tests := []struct {
		name string
		comm string
		want string
	}{
		{"spaces", "my proc name", "my proc name"},
		{"closing parenthesis", "a)b", "a)b"},
		{"parenthesis and space", "a) b", "a) b"},
		{"opening parenthesis", "(x(", "(x("},
		{"imitated field", "x) anon-rss:1kB", "x) anon-rss:1kB"},
		{"imitated delimiter and fields", "a) total-vm:5kB, anon-rss:1kB, oom_score_adj:99", "a) total-vm:5kB, anon-rss:1kB, oom_score_adj:99"},
		{"line feed", "line\nfeed", "line\nfeed"},
		{"invalid UTF-8", "bad\xff\xfeutf8", "bad" + repl + repl + "utf8"},
		{"truncated multibyte character", "cut\xe2\x82", "cut" + repl + repl},
		{"empty", "", "(unnamed)"},
		{"too long", long, long[:1024]},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			msg := killLine(tc.comm)
			got := mustParse(t, msg)
			requireValues(t, tc.name, got, 4242, u64p(2048*1024), i16p(-7))
			if got.VictimCommand != tc.want {
				t.Errorf("parseKill(%.100q) VictimCommand = %.100q (%d bytes), want %.100q (%d bytes)", msg, got.VictimCommand, len(got.VictimCommand), tc.want, len(tc.want))
			}
			requireValidEvent(t, tc.name, got)
		})
	}
}

func TestParseKill_MissingFields(t *testing.T) {
	const head = "Out of memory: Killed process 77 (p) total-vm:1000kB"
	tests := []struct {
		name string
		tail string
		rss  *uint64
		adj  *int16
	}{
		{"both", ", anon-rss:5kB, file-rss:0kB, shmem-rss:0kB, UID:0 pgtables:4kB oom_score_adj:3", u64p(5 * 1024), i16p(3)},
		{"no anon-rss", ", file-rss:0kB, shmem-rss:0kB, UID:0 pgtables:4kB oom_score_adj:3", nil, i16p(3)},
		{"no oom_score_adj", ", anon-rss:5kB, file-rss:0kB, shmem-rss:0kB, UID:0", u64p(5 * 1024), nil},
		{"neither", ", file-rss:0kB, shmem-rss:0kB", nil, nil},
		{"nothing after total-vm", "", nil, nil},
		{"zero is a value", ", anon-rss:0kB, oom_score_adj:0", u64p(0), i16p(0)},
		{"lowest score", ", anon-rss:1kB oom_score_adj:-1000", u64p(1024), i16p(-1000)},
		{"highest score", ", anon-rss:1kB oom_score_adj:1000", u64p(1024), i16p(1000)},
		{"score above range", ", anon-rss:1kB oom_score_adj:1001", u64p(1024), nil},
		{"score below range", ", anon-rss:1kB oom_score_adj:-1001", u64p(1024), nil},
		{"score of five digits", ", anon-rss:1kB oom_score_adj:00001", u64p(1024), nil},
		{"score with a plus", ", anon-rss:1kB oom_score_adj:+5", u64p(1024), nil},
		{"score without digits", ", anon-rss:1kB oom_score_adj:", u64p(1024), nil},
		{"rss in another unit", ", anon-rss:5MB oom_score_adj:1", nil, i16p(1)},
		{"rss without unit", ", anon-rss:5 oom_score_adj:1", nil, i16p(1)},
		{"rss without digits", ", anon-rss:kB oom_score_adj:1", nil, i16p(1)},
		{"negative rss", ", anon-rss:-5kB oom_score_adj:1", nil, i16p(1)},
		{"largest rss that fits", ", anon-rss:18014398509481983kB oom_score_adj:1", u64p(18014398509481983 * 1024), i16p(1)},
		{"rss that overflows when multiplied", ", anon-rss:18014398509481984kB oom_score_adj:1", nil, i16p(1)},
		{"anon-rss twice", ", anon-rss:5kB, anon-rss:7kB oom_score_adj:3", nil, i16p(3)},
		{"anon-rss twice with the same value", ", anon-rss:5kB, anon-rss:5kB oom_score_adj:3", nil, i16p(3)},
		{"oom_score_adj twice", ", anon-rss:5kB oom_score_adj:3 oom_score_adj:4", u64p(5 * 1024), nil},
		{"both twice", ", anon-rss:5kB, anon-rss:7kB oom_score_adj:3 oom_score_adj:3", nil, nil},
		{"rss of 21 digits", ", anon-rss:100000000000000000000kB oom_score_adj:1", nil, i16p(1)},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			msg := head + tc.tail
			got := mustParse(t, msg)
			requireValues(t, tc.name, got, 77, tc.rss, tc.adj)
			requireValidEvent(t, tc.name, got)
		})
	}
}

func TestParseKill_PIDs(t *testing.T) {
	tests := []struct {
		pid  string
		ok   bool
		want int32
	}{
		{"1", true, 1},
		{"4194304", true, 4194304},
		{"2147483647", true, 2147483647},
		{"0", false, 0},
		{"2147483648", false, 0},
		{"4294967295", false, 0},
		{"4294967296", false, 0},
		{"99999999999", false, 0},
		{"", false, 0},
		{"abc", false, 0},
		{"-5", false, 0},
		{"+5", false, 0},
		{"5x", false, 0},
	}
	for _, tc := range tests {
		t.Run("pid "+tc.pid, func(t *testing.T) {
			msg := "Out of memory: Killed process " + tc.pid + " (p) total-vm:1kB, anon-rss:1kB oom_score_adj:0"
			got, ok := parseKill([]byte(msg))
			if ok != tc.ok {
				t.Fatalf("parseKill(%q) ok = %v, want %v", msg, ok, tc.ok)
			}
			if ok && got.VictimPID != tc.want {
				t.Errorf("parseKill(%q) VictimPID = %d, want %d", msg, got.VictimPID, tc.want)
			}
		})
	}
}

func TestParseKill_IgnoresOtherLines(t *testing.T) {
	// The control: a genuine kill line is parsed, so the refusals below are not an accident of a parser that parses nothing.
	genuine := killLine("pzstd")
	mustParse(t, genuine)

	tests := []struct {
		name string
		msg  string
	}{
		{"no killable processes", "Out of memory and no killable processes..."},
		{"invoked oom-killer", "systemd invoked oom-killer: gfp_mask=0x1100cca(GFP_HIGHUSER_MOVABLE), order=0, oom_score_adj=0"},
		{"oom_reaper", "oom_reaper: reaped process 352448 (pzstd), now anon-rss:0kB, file-rss:0kB, shmem-rss:0kB"},
		{"memory info", "Mem-Info:"},
		{"task dump header", "[  pid  ]   uid  tgid total_vm      rss pgtables_bytes swapents oom_score_adj name"},
		{"oom-kill constraint line", "oom-kill:constraint=CONSTRAINT_NONE,nodemask=(null),cpuset=/,mems_allowed=0,global_oom,task_memcg=/x.service,task=pzstd,pid=1,uid=0"},
		{"empty", ""},
		{"leading text", "x " + genuine},
		{"leading space", " " + genuine},
		{"lower case", strings.ToLower(genuine)},
		{"without the prefix", "Killed process 4242 (pzstd) total-vm:1kB"},
		{"prefix without space", "Out of memory: Killed process4242 (pzstd) total-vm:1kB"},
		{"without total-vm", "Out of memory: Killed process 5 (x)"},
		{"without opening parenthesis", "Out of memory: Killed process 5 x) total-vm:1kB"},
		{"unknown reason", "Out of memory (something_else): Killed process 5 (x) total-vm:1kB"},
		{"prefix only", "Out of memory: Killed process "},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got, ok := parseKill([]byte(tc.msg)); ok {
				t.Errorf("parseKill(%.100q) = %+v, true, want false", tc.msg, got)
			}
		})
	}
	if _, ok := parseKill(nil); ok {
		t.Errorf("parseKill(nil) ok = true, want false")
	}
	for i := range len(genuine) {
		_, _ = parseKill([]byte(genuine[:i])) // every prefix of a kill line is handled without a panic
	}
}
