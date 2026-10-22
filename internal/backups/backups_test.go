package backups

import (
	"os"
	"strings"
	"testing"
	"testing/fstest"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// capturedStat is a stat file of a Linux 6.18 kernel, the model of the harness's stat files.
const capturedStat = "../../testdata/proc/6508/stat"

func TestWorld_ModelsTheProcFilesTheCollectorsRead(t *testing.T) {
	data, err := os.ReadFile(capturedStat)
	if err != nil {
		t.Fatalf("os.ReadFile(%q) error = %v, want nil", capturedStat, err)
	}
	captured := string(data)
	capturedFields := strings.Fields(captured[strings.LastIndex(captured, ") ")+2:])
	w := newWorld(bootA, at(-1000))
	w.add(77, "pz stat) x", "/usr/bin/pzstd\x00-p\x004\x00", at(-30))

	modelled := string(w.fs["77/stat"].Data)
	if got := strings.Fields(modelled[strings.LastIndex(modelled, ") ")+2:]); len(got) != len(capturedFields) {
		t.Errorf("modelled stat has %d fields after the name, the captured file %d, want the same", len(got), len(capturedFields))
	}

	boot, err := procstats.ReadBootTime(w.fs)
	if err != nil || !boot.Equal(at(-1000)) {
		t.Fatalf("ReadBootTime() = %v, %v, want %v", boot, err, at(-1000))
	}
	info, err := procstats.NewCommandReader(w.fs).Process(77, boot)
	if err != nil {
		t.Fatalf("Process(77) error = %v, want nil", err)
	}
	if info.PID != 77 || info.Command != "pzstd" || info.Cmdline != "/usr/bin/pzstd -p 4" || !info.StartedAt.Equal(at(-30)) {
		t.Errorf("Process(77) = %+v, want pid 77, command pzstd, command line %q, started %v", info, "/usr/bin/pzstd -p 4", at(-30))
	}
	if raw := string(w.fs["sys/kernel/random/boot_id"].Data); raw != bootA+"\n" {
		t.Errorf("boot_id file = %q, want the UUID and one line feed as the kernel writes it", raw)
	}
	if pids, seen, err := procstats.ListPIDs(w.fs); err != nil || seen != 1 || len(pids) != 1 || pids[0] != 77 {
		t.Errorf("ListPIDs() = %v, %d, %v, want [77], 1, nil", pids, seen, err)
	}
}

func TestBackups_StartWithinFifteenSeconds(t *testing.T) {
	e := newEnv(t, pzstdRule)
	e.quietRuns(0, 10)
	e.w.addPzstd(4242, at(12))

	got := e.run(20)

	requireOneStart(t, got, startWant{boot: bootA, started: at(12), pid: 4242, command: "pzstd", processes: 1})
	requireOpenRun(t, e.store, agentstate.OpenRun{BootID: bootA, StartedAt: at(12), LastSeen: at(20), MaxProcesses: 1})

	e.quiet(30)
	requireOpenRun(t, e.store, agentstate.OpenRun{BootID: bootA, StartedAt: at(12), LastSeen: at(30), MaxProcesses: 1})
}

func TestBackups_StartNamesTheProcessThatStartedFirst(t *testing.T) {
	e := newEnv(t, pzstdRule)
	e.w.add(90, "gzip", "gzip\x00-9\x00", at(-500))
	e.w.addPzstd(100, at(-30))
	e.w.addPzstd(101, at(-50))
	e.w.addPzstd(102, at(-10))

	got := e.run(0)

	requireOneStart(t, got, startWant{boot: bootA, started: at(-50), pid: 101, command: "pzstd", processes: 3})
}

func TestBackups_EndAfterSixtySeconds(t *testing.T) {
	e := newEnv(t, pzstdRule)
	e.w.addPzstd(100, at(-5))
	requireOneStart(t, e.run(0), startWant{boot: bootA, started: at(-5), pid: 100, command: "pzstd", processes: 1})
	e.quietRuns(10, 20)
	e.w.remove(100)

	e.quietRuns(30, 40, 50, 60, 70)
	requireOneEnd(t, e.run(80), endWant{boot: bootA, started: at(-5), lastSeen: at(20), goneBy: at(30), max: 1})
	e.quiet(90)
	requireNoOpenRun(t, e.store)
	e.quiet(100)

	e.w.addPzstd(200, at(150))
	requireOneStart(t, e.run(160), startWant{boot: bootA, started: at(150), pid: 200, command: "pzstd", processes: 1})
}

func TestBackups_ShortPauseKeepsOneRun(t *testing.T) {
	e := newEnv(t, pzstdRule)
	e.w.addPzstd(100, at(-5))
	requireOneStart(t, e.run(0), startWant{boot: bootA, started: at(-5), pid: 100, command: "pzstd", processes: 1})
	e.quiet(10)
	e.w.remove(100)

	e.quietRuns(20, 30, 40, 50, 60)
	e.w.addPzstd(200, at(65))
	e.quiet(70)
	e.w.remove(200)
	e.quiet(80)

	requireOneEnd(t, e.run(150), endWant{boot: bootA, started: at(-5), lastSeen: at(70), goneBy: at(80), max: 1})
}

func TestBackups_SingleSightingGivesStartAndEnd(t *testing.T) {
	e := newEnv(t, pzstdRule)
	e.quiet(0)
	e.w.addPzstd(300, at(8))
	requireOneStart(t, e.run(10), startWant{boot: bootA, started: at(8), pid: 300, command: "pzstd", processes: 1})
	e.w.remove(300)
	e.quiet(20)

	requireOneEnd(t, e.run(100), endWant{boot: bootA, started: at(8), lastSeen: at(10), goneBy: at(20), max: 1})
	requireNoOpenRun(t, e.store)
}

func TestBackups_CountsWithoutKeepingProcesses(t *testing.T) {
	e := newEnv(t, pzstdRule)
	e.w.addPzstd(100, at(-30))
	e.w.addPzstd(101, at(-50))
	e.w.addPzstd(102, at(-10))
	requireOneStart(t, e.run(0), startWant{boot: bootA, started: at(-50), pid: 101, command: "pzstd", processes: 3})

	for pid := 110; pid < 114; pid++ {
		e.w.addPzstd(pid, at(5))
	}
	e.quiet(10)
	e.w.removeAll(100, 102, 110, 111, 112, 113)
	e.quiet(20)
	e.w.remove(101)
	e.quiet(30)

	requireOneEnd(t, e.run(120), endWant{boot: bootA, started: at(-50), lastSeen: at(20), goneBy: at(30), max: 7})
}

func TestBackups_ThousandsOfProcessesAreCounted(t *testing.T) {
	const count = 1500
	e := newEnv(t, pzstdRule)
	for i := 0; i < count; i++ {
		e.w.addPzstd(1000+i, at(-1000+i))
	}

	got := e.run(0)

	requireOneStart(t, got, startWant{boot: bootA, started: at(-1000), pid: 1000, command: "pzstd", processes: count})
	stamped := model.Record{Meta: model.Meta{Origin: model.OriginAgent, Source: Name, Seq: 1, CapturedAt: got[0].StartedAt}, Data: got[0]}
	size, err := wire.CheckRecord(&stamped)
	if err != nil || size > 1024 {
		t.Errorf("wire.CheckRecord(start of %d processes) = %d bytes, %v, want a record of at most 1024 bytes (no list of processes)", count, size, err)
	}
}

func TestBackups_RestartContinuesRun(t *testing.T) {
	restarts := map[string]func(e *env){
		"state kept in memory": func(e *env) { e.restart() },
		"state read from file": func(e *env) { e.reopen(20) },
	}
	for name, restart := range restarts {
		t.Run(name, func(t *testing.T) {
			e := newEnv(t, pzstdRule)
			e.w.addPzstd(100, at(-20))
			e.w.addPzstd(101, at(-10))
			requireOneStart(t, e.run(0), startWant{boot: bootA, started: at(-20), pid: 100, command: "pzstd", processes: 2})
			e.quiet(10)
			e.w.remove(101)
			e.quiet(20)

			restart(e)
			e.quietRuns(30, 40)
			e.w.remove(100)
			e.quiet(50)

			requireOneEnd(t, e.run(130), endWant{boot: bootA, started: at(-20), lastSeen: at(40), goneBy: at(50), max: 2})
			requireNoOpenRun(t, e.store)
		})
	}
}

// openRunEnv returns an environment in which a run started at -20 s ran until 10 s, the agent saved nothing since.
func openRunEnv(t *testing.T) *env {
	t.Helper()
	e := newEnv(t, pzstdRule)
	e.w.addPzstd(100, at(-20))
	requireOneStart(t, e.run(0), startWant{boot: bootA, started: at(-20), pid: 100, command: "pzstd", processes: 1})
	e.quiet(10)
	return e
}

func TestBackups_RunEndedAcrossStopOrReboot(t *testing.T) {
	oldRun := endWant{boot: bootA, started: at(-20), lastSeen: at(10), goneBy: at(300), max: 1}

	t.Run("stop and no match at the first check", func(t *testing.T) {
		e := openRunEnv(t)
		e.w.remove(100)
		e.reopen(10)

		requireOneEnd(t, e.run(300), oldRun)
		requireNoOpenRun(t, e.store)
		e.quiet(310)
	})

	t.Run("reboot and no match at the first check", func(t *testing.T) {
		e := openRunEnv(t)
		e.reopen(10)
		e.w.reboot(bootB, at(100))
		e.restart()

		requireOneEnd(t, e.run(300), oldRun)
		requireNoOpenRun(t, e.store)
		e.quiet(310)
	})

	t.Run("reboot and a backup of the new boot", func(t *testing.T) {
		e := openRunEnv(t)
		e.reopen(10)
		e.w.reboot(bootB, at(100))
		e.w.addPzstd(100, at(250))
		e.restart()

		starts, ends := phases(e.run(260))

		if len(starts) != 1 || len(ends) != 1 {
			t.Fatalf("first run of the new boot returned %d starts and %d ends, want one of each: the old run ends and the new one starts", len(starts), len(ends))
		}
		oldEnd := oldRun
		oldEnd.goneBy = at(260)
		requireEnd(t, ends[0], oldEnd)
		requireStart(t, starts[0], startWant{boot: bootB, started: at(250), pid: 100, command: "pzstd", processes: 1})
		requireOpenRun(t, e.store, agentstate.OpenRun{BootID: bootB, StartedAt: at(250), LastSeen: at(260), MaxProcesses: 1})

		e.quiet(270)
		e.w.remove(100)
		e.quiet(280)
		requireOneEnd(t, e.run(400), endWant{boot: bootB, started: at(250), lastSeen: at(270), goneBy: at(280), max: 1})
	})
}

func TestBackups_RunEndedAcrossStopKeepsTheLargestCount(t *testing.T) {
	e := newEnv(t, pzstdRule)
	for pid := 100; pid < 104; pid++ {
		e.w.addPzstd(pid, at(-20))
	}
	requireOneStart(t, e.run(0), startWant{boot: bootA, started: at(-20), pid: 100, command: "pzstd", processes: 4})
	e.w.removeAll(101, 102, 103)
	e.quiet(10)
	e.w.remove(100)

	e.restart()

	requireOneEnd(t, e.run(200), endWant{boot: bootA, started: at(-20), lastSeen: at(10), goneBy: at(200), max: 4})
}

// literalCase is a process and a rule list; match tells whether the process counts as a backup process.
type literalCase struct {
	name    string
	rules   []Rule
	comm    string
	cmdline string
	command string // the command of the process when it matches
	match   bool
}

func TestBackups_RulesAreLiteral(t *testing.T) {
	const nice = "nice\x00-n\x0019\x00pzstd\x00"
	tests := []literalCase{
		{"command equals the command", []Rule{pzstdRule}, "pzstd", "/usr/bin/pzstd\x00-p\x004\x00", "pzstd", true},
		{"another command whose command line mentions it", []Rule{pzstdRule}, "grep", "grep\x00pzstd\x00", "", false},
		{"shell command line that runs it", []Rule{pzstdRule}, "sh", "sh\x00-c\x00pzstd -p 4\x00", "", false},
		{"command that starts with it", []Rule{pzstdRule}, "pzstd-helper", "pzstd-helper\x00-x\x00", "", false},
		{"command in another case", []Rule{pzstdRule}, "PZSTD", "PZSTD\x00", "", false},
		{"command that is a prefix of the rule", []Rule{pzstdRule}, "pzst", "pzst\x00", "", false},
		{"command derived from the command line, not from comm", []Rule{pzstdRule}, "pzstd", nice, "", false},
		{"wildcard in the rule", []Rule{{Command: "pz*"}}, "pzstd", "pzstd\x00", "", false},
		{"alternation in the rule", []Rule{{Command: "pzstd|gzip"}}, "gzip", "gzip\x00", "", false},
		{"asterisk in the process name", []Rule{{Command: "pz*"}}, "pz*", "pz*\x00", "pz*", true},
		{"prefix of the command line", []Rule{{CmdlinePrefix: "/usr/bin/pzstd "}}, "pzstd", "/usr/bin/pzstd\x00-p\x004\x00", "pzstd", true},
		{"prefix that is not at the start", []Rule{{CmdlinePrefix: "/usr/bin/pzstd"}}, "grep", "grep\x00/usr/bin/pzstd\x00x\x00", "", false},
		{"prefix with a space beyond the word", []Rule{{CmdlinePrefix: "/usr/bin/pzstd "}}, "pzstd2", "/usr/bin/pzstd2\x00-x\x00", "", false},
		{"prefix is literal text, not a word", []Rule{{CmdlinePrefix: "/usr/bin/pzstd"}}, "pzstd2", "/usr/bin/pzstd2\x00-x\x00", "pzstd2", true},
		{"pattern as prefix", []Rule{{CmdlinePrefix: ".*"}}, "pzstd", "pzstd\x00", "", false},
		{"prefix of a command line that starts with another word", []Rule{{CmdlinePrefix: "pzstd"}}, "nice", nice, "", false},
		{"second rule matches", []Rule{{Command: "borg"}, {CmdlinePrefix: "/opt/b/"}}, "run", "/opt/b/run\x00", "run", true},
		{"no rule at all matches nothing", nil, "pzstd", "pzstd\x00", "", false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) { requireLiteral(t, tt) })
	}
}

// requireLiteral runs the collector with the rules of tt over one process and checks whether it starts a run.
func requireLiteral(t *testing.T, tt literalCase) {
	t.Helper()
	e := newEnv(t, tt.rules...)
	e.w.add(700, tt.comm, tt.cmdline, at(-5))

	got := e.run(0)

	if !tt.match {
		if len(got) != 0 {
			t.Errorf("run with rules %+v over comm %q and command line %q returned %s, want no record", tt.rules, tt.comm, tt.cmdline, describe(got))
		}
		return
	}
	requireOneStart(t, got, startWant{boot: bootA, started: at(-5), pid: 700, command: tt.command, processes: 1})
}

func TestBackups_NoRulesNoCheck(t *testing.T) {
	for name, rules := range map[string][]Rule{"nil list": nil, "empty list": {}} {
		t.Run(name, func(t *testing.T) {
			e := newEnv(t)
			e.rules = rules
			opens := 0
			e.w.addPzstd(100, at(-5))
			e.b = New(countingFS{FS: e.w.fs, opens: &opens}, rules, e.store, e.log)

			e.quietRuns(0, 10, 100)

			if opens != 0 {
				t.Errorf("collector without rules opened %d files of /proc, want none (no check runs)", opens)
			}
			if e.logs.Len() != 0 {
				t.Errorf("collector without rules wrote the log %q, want no line", e.logs.String())
			}
			requireNoOpenRun(t, e.store)
		})
	}
}

func TestBackups_ProcessThatEndedWhileListedIsSkipped(t *testing.T) {
	e := newEnv(t, pzstdRule)
	e.w.fs["800/cmdline"] = &fstest.MapFile{Data: []byte("pzstd\x00")}
	e.w.addPzstd(810, at(-5))

	got := e.run(0)

	requireOneStart(t, got, startWant{boot: bootA, started: at(-5), pid: 810, command: "pzstd", processes: 1})
}

func TestBackups_RecordsAreValid(t *testing.T) {
	tests := []struct {
		name    string
		comm    string
		cmdline string
	}{
		{"invalid UTF-8 in the first word", "pzstd", "/x/b\xff\xfe\x00arg\x00"},
		{"line feed and tab in the first word", "pzstd", "/x/b\nk\tz\x00arg\x00"},
		{"command line of 100 KiB", "pzstd", "/x/" + strings.Repeat("a", 100<<10) + "\x00arg\x00"},
		{"empty command line, name from comm", "pzstd", ""},
		{"comm with a parenthesis and a space", "pz) stat", "/x/pz) stat\x00"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			e := newEnv(t, Rule{Command: "pzstd"}, Rule{CmdlinePrefix: "/x/"})
			e.w.add(900, tt.comm, tt.cmdline, at(-5))

			got := e.run(0)

			if len(got) != 1 {
				t.Fatalf("run over comm %q returned %s, want one start", tt.comm, describe(got))
			}
			if got[0].Command == "" || !utf8.ValidString(got[0].Command) {
				t.Errorf("start command = %q, want a non-empty valid UTF-8 text", got[0].Command)
			}
			e.w.remove(900)
			e.quiet(10)
			requireOneEnd(t, e.run(100), endWant{boot: bootA, started: at(-5), lastSeen: at(0), goneBy: at(10), max: 1})
		})
	}
}

func TestBackups_LogsHoldNoProcessText(t *testing.T) {
	rule := Rule{CmdlinePrefix: "/opt/" + leak + "/"}
	e := newEnv(t, rule)
	cmdline := "/opt/" + leak + "/bin/job\x00--password=" + leak + "\x00"
	e.w.add(100, leak+"-comm", cmdline, at(-20))
	e.w.add(150, leak, "\xff"+leak+"\x00", at(-20))
	e.w.fs["160/cmdline"] = e.w.fs["100/cmdline"]
	if got := e.run(0); len(got) != 1 {
		t.Fatalf("run returned %s, want one start", describe(got))
	}
	e.reopen(10)
	e.quiet(20)

	e.restart()
	e.quiet(30)
	e.w.reboot(bootB, at(100))
	e.w.add(100, leak, cmdline, at(250))
	e.restart()
	if starts, ends := phases(e.run(260)); len(starts) != 1 || len(ends) != 1 {
		t.Fatalf("run after the reboot returned %d starts and %d ends, want one of each", len(starts), len(ends))
	}
	e.w.remove(100)
	e.quiet(270)
	if got := e.run(400); len(got) != 1 {
		t.Fatalf("run at the end returned %s, want one end", describe(got))
	}

	if strings.Contains(e.logs.String(), leak) {
		t.Errorf("log holds process text: %q, want no command, command line or rule value", e.logs.String())
	}
	if e.logs.Len() != 0 {
		t.Errorf("log of runs that only start and end backup runs = %q, want no line (no line per backup run)", e.logs.String())
	}
}

func TestBackups_LostRunIsDecidedAgain(t *testing.T) {
	t.Run("dropped start is written again while the process runs", func(t *testing.T) {
		e := newEnv(t, pzstdRule)
		e.w.addPzstd(100, at(-5))
		want := startWant{boot: bootA, started: at(-5), pid: 100, command: "pzstd", processes: 1}
		requireOneStart(t, e.run(0), want)

		requireOneStart(t, e.runLost(10), want)

		e.quiet(20)
		requireOpenRun(t, e.store, agentstate.OpenRun{BootID: bootA, StartedAt: at(-5), LastSeen: at(20), MaxProcesses: 1})
	})

	t.Run("dropped start gives nothing once the process has ended", func(t *testing.T) {
		e := newEnv(t, pzstdRule)
		e.w.addPzstd(100, at(-5))
		requireOneStart(t, e.run(0), startWant{boot: bootA, started: at(-5), pid: 100, command: "pzstd", processes: 1})
		e.w.remove(100)

		if got := e.runLost(10); len(got) != 0 {
			t.Errorf("lost run after a dropped start returned %s, want no record (no open run, no match)", describe(got))
		}
		requireNoOpenRun(t, e.store)
		e.quiet(20)
	})

	t.Run("dropped end is written again with the gone_by of the last kept check", func(t *testing.T) {
		e := newEnv(t, pzstdRule)
		e.w.addPzstd(100, at(-5))
		requireOneStart(t, e.run(0), startWant{boot: bootA, started: at(-5), pid: 100, command: "pzstd", processes: 1})
		e.quiet(10)
		e.w.remove(100)
		e.quietRuns(20, 30)
		want := endWant{boot: bootA, started: at(-5), lastSeen: at(10), goneBy: at(20), max: 1}
		requireOneEnd(t, e.run(100), want)

		requireOneEnd(t, e.runLost(110), want)

		requireNoOpenRun(t, e.store)
		e.quiet(120)
	})

	t.Run("dropped first check after a restart is repeated", func(t *testing.T) {
		e := openRunEnv(t)
		e.w.remove(100)
		e.restart()
		requireOneEnd(t, e.run(300), endWant{boot: bootA, started: at(-20), lastSeen: at(10), goneBy: at(300), max: 1})

		requireOneEnd(t, e.runLost(310), endWant{boot: bootA, started: at(-20), lastSeen: at(10), goneBy: at(310), max: 1})

		requireNoOpenRun(t, e.store)
		e.quiet(320)
	})
}

func TestBackups_MalformedBootIDFailsTheRun(t *testing.T) {
	const want = "backups: procstats: boot_id: not a lower-case UUID"
	malformed := map[string]string{
		"upper case":  strings.ToUpper(bootA) + "\n",
		"not a UUID":  "not-a-uuid\n",
		"37 bytes":    bootA + "c\n",
		"empty":       "",
		"two newline": bootA + "\n\n",
	}
	for name, content := range malformed {
		t.Run(name, func(t *testing.T) {
			e := newEnv(t, pzstdRule)
			e.w.addPzstd(810, at(-5))
			e.w.fs["sys/kernel/random/boot_id"] = &fstest.MapFile{Data: []byte(content)}

			records, err := e.b.Collect(t.Context(), scheduler.Run{Start: at(0)})

			if err == nil || err.Error() != want {
				t.Errorf("Collect() error = %v, want %q", err, want)
			}
			if len(records) != 0 {
				t.Errorf("Collect() = %d records, want none", len(records))
			}
			if open := e.store.State().Backup; open != nil {
				t.Errorf("state Backup = %+v, want nil: no open run may carry a malformed boot ID", *open)
			}
		})
	}
}
