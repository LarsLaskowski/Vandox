package kernevents

import (
	"context"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// healthyJournal gives the environment a journal with a few plain lines before a kill, and a saved position before them.
func healthyJournal(e *env) (killPID int32) {
	var c seqCounter
	e.j.add(e.t, kindArchived, seqIDA, []entry{
		c.line(bootB, 1),
		c.line(bootB, 2),
		c.kill(bootB, 3, 4711, "pzstd", lagUS),
	})
	return 4711
}

func TestBoot_NewBootRecordsPreviousBootAndUptime(t *testing.T) {
	e := newEnv(t)
	healthyJournal(e)
	prevUptime := 3*24*time.Hour + 5*time.Minute
	e.store.Update(false, func(s *agentstate.State) {
		s.Boot = &agentstate.Boot{ID: dashed(bootA), Uptime: prevUptime, BootedAt: bootTime.Add(-72 * time.Hour)}
		s.Journal = before()
	})

	recs := e.run(false)
	e.keep()

	boots := bootsOf(recs)
	if len(boots) != 1 {
		t.Fatalf("a start in a new boot made %d boot events, want 1", len(boots))
	}
	b := boots[0].ev.Boot
	if b.BootID != dashed(bootB) || b.PreviousBootID != dashed(bootA) {
		t.Errorf("boot event boot_id = %q, previous_boot_id = %q, want %q and %q", b.BootID, b.PreviousBootID, dashed(bootB), dashed(bootA))
	}
	if !b.BootedAt.Equal(bootTime) || !boots[0].rec.CapturedAt.Equal(bootTime) {
		t.Errorf("boot event booted_at = %s, captured_at = %s, want both %s (btime of /proc/stat)", b.BootedAt, boots[0].rec.CapturedAt, bootTime)
	}
	if b.PreviousUptime == nil || *b.PreviousUptime != prevUptime {
		t.Errorf("boot event previous_uptime_ns = %v, want %v (the uptime the state held)", b.PreviousUptime, prevUptime)
	}
	if boots[0].rec.Source != Name {
		t.Errorf("boot event source = %q, want %q", boots[0].rec.Source, Name)
	}
	checkValid(t, recs, t0)
	st := e.store.State()
	if st.Boot == nil || st.Boot.ID != dashed(bootB) || !st.Boot.BootedAt.Equal(bootTime) || st.Boot.Uptime != 5_000_000*time.Second {
		t.Errorf("state after the boot event = %+v, want the current boot %s booted at %s with the uptime of /proc/uptime, 5,000,000 s", st.Boot, dashed(bootB), bootTime)
	}
	if again := bootsOf(e.run(false)); len(again) != 0 {
		t.Errorf("the second run made %d boot events, want none", len(again))
	}
}

func TestBoot_SameBootRecordsNothing(t *testing.T) {
	e := newEnv(t)
	healthyJournal(e)
	e.seed(dashed(bootB), before())

	first := e.run(false)
	second := e.run(false)
	e.newKernel() // the agent is restarted within the same boot
	third := e.run(false)

	for i, recs := range [][]model.Record{first, second, third} {
		if n := len(bootsOf(recs)); n != 0 {
			t.Errorf("run %d in the same boot made %d boot events, want none", i+1, n)
		}
	}
	if got := pids(killsOf(first)); len(got) != 1 || got[0] != 4711 {
		t.Errorf("the first run's kills = %v, want [4711] (the kernel collector works while no boot is recorded)", got)
	}
}

func TestBoot_FirstStartWithoutPrevious(t *testing.T) {
	e := newEnv(t)
	healthyJournal(e)
	if e.store.State().Boot != nil {
		t.Fatalf("a fresh state directory holds a boot: %+v", e.store.State().Boot)
	}

	recs := e.run(false)

	boots := bootsOf(recs)
	if len(boots) != 1 {
		t.Fatalf("the first start made %d boot events, want 1", len(boots))
	}
	b := boots[0].ev.Boot
	if b.BootID != dashed(bootB) || b.PreviousBootID != "" || b.PreviousUptime != nil {
		t.Errorf("first boot event = %+v (previous uptime %v), want boot_id %s without previous_boot_id and previous_uptime_ns", b, b.PreviousUptime, dashed(bootB))
	}
	checkValid(t, recs, t0)
	e.newKernel()
	if again := bootsOf(e.run(false)); len(again) != 0 {
		t.Errorf("the restart in the same boot made %d boot events, want none", len(again))
	}
}

// stateWriters are the unusable state files of TestBoot_WithoutStateEveryStart.
var stateWriters = []struct {
	name string
	data string
	perm os.FileMode
}{
	{"corrupt file", "not json {", 0o600},
	{"file of another program", `{"version":7,"other":true}`, 0o600},
	{"file others can read", `{"version":1}`, 0o644},
}

// writeStateFile writes the state file of a test with exactly the permissions perm.
func writeStateFile(t *testing.T, path, data string, perm os.FileMode) {
	t.Helper()
	if err := os.WriteFile(path, []byte(data), perm); err != nil {
		t.Fatalf("writing the state file: %v", err)
	}
	if err := os.Chmod(path, perm); err != nil {
		t.Fatalf("changing the state file's permissions: %v", err)
	}
}

func TestBoot_WithoutStateEveryStart(t *testing.T) {
	for _, tc := range stateWriters {
		t.Run(tc.name, func(t *testing.T) {
			e := newEnv(t)
			healthyJournal(e)
			writeStateFile(t, filepath.Join(e.dir, agentstate.FileName), tc.data, tc.perm)
			for start := 1; start <= 3; start++ {
				e.openStore() // every start of the agent reads the unusable file again
				e.newKernel()
				e.seed("", before())

				boots := bootsOf(e.run(false))

				requireFirstBootEvent(t, start, boots)
			}
		})
	}
}

// requireFirstBootEvent checks that a start made one boot event of the current boot without previous fields.
func requireFirstBootEvent(t *testing.T, start int, boots []killOf) {
	t.Helper()
	if len(boots) != 1 {
		t.Fatalf("start %d without usable state made %d boot events, want 1", start, len(boots))
	}
	if b := boots[0].ev.Boot; b.BootID != dashed(bootB) || b.PreviousBootID != "" || b.PreviousUptime != nil {
		t.Errorf("start %d: boot event = %+v, want boot_id %s and no previous fields", start, b, dashed(bootB))
	}
}

// bootIDCases are the contents of /proc/sys/kernel/random/boot_id of TestBoot_BadBootIDRecordsNothing; a nil raw removes the file.
func bootIDCases() []struct {
	name string
	raw  *string
	ok   bool
} {
	good := dashed(bootB)
	return []struct {
		name string
		raw  *string
		ok   bool
	}{
		{"valid", &good, true},
		{"missing file", nil, false},
		{"empty", strPtr(""), false},
		{"not a UUID", strPtr(leak + "-not-a-uuid"), false},
		{"upper case", strPtr(strings.ToUpper(good)), false},
		{"without dashes", strPtr(bootB), false},
		{"two lines", strPtr(good + "\n" + good), false},
		{"trailing text", strPtr(good + " " + leak), false},
		{"too long", strPtr(strings.Repeat(good, 3)), false},
	}
}

func TestBoot_BadBootIDRecordsNothing(t *testing.T) {
	for _, tc := range bootIDCases() {
		t.Run(tc.name, func(t *testing.T) {
			e := newEnv(t)
			healthyJournal(e)
			e.seed("", before())
			if tc.raw == nil {
				delete(e.proc, "sys/kernel/random/boot_id")
			} else {
				e.setBoot(*tc.raw)
			}

			recs := append(e.run(false), e.run(false)...)

			requireBootIDOutcome(t, e, recs, tc.ok)
		})
	}
}

// requireBootIDOutcome checks the records and the log of two runs with a boot ID that is usable or not.
func requireBootIDOutcome(t *testing.T, e *env, recs []model.Record, usable bool) {
	t.Helper()
	warns := e.logs.find(slog.LevelWarn, "boot ID not readable")
	if usable {
		if len(bootsOf(recs)) != 1 || len(warns) != 0 {
			t.Errorf("a valid boot ID gave %d boot events and %d warnings, want 1 and 0", len(bootsOf(recs)), len(warns))
		}
		return
	}
	if n := len(bootsOf(recs)); n != 0 {
		t.Errorf("an unusable boot ID gave %d boot events, want none", n)
	}
	if len(warns) != 1 {
		t.Errorf("an unusable boot ID logged %d warnings %v, want exactly one", len(warns), warns)
	}
	if strings.Contains(e.logs.text(), leak) {
		t.Errorf("the log %q holds the content of the boot ID file, want no content", e.logs.text())
	}
	if got := pids(killsOf(recs)); len(got) != 1 || got[0] != 4711 {
		t.Errorf("kills with an unusable boot ID = %v, want [4711] (the other features run)", got)
	}
}

func strPtr(s string) *string { return &s }

func TestBoot_UptimeIsZeroWhenProcUptimeIsUnreadable(t *testing.T) {
	e := newEnv(t)
	healthyJournal(e)
	e.seed("", before())
	delete(e.proc, "uptime")

	recs := e.run(false)
	e.keep()

	if len(bootsOf(recs)) != 1 {
		t.Fatalf("boot events = %d, want 1", len(bootsOf(recs)))
	}
	if st := e.store.State(); st.Boot == nil || st.Boot.ID != dashed(bootB) || st.Boot.Uptime != 0 {
		t.Errorf("state after the boot event = %+v, want the current boot with uptime 0", st.Boot)
	}
}

// requireSavedBoot checks the boot of the state.
func requireSavedBoot(t *testing.T, e *env, id string, uptime time.Duration, when string) {
	t.Helper()
	st := e.store.State()
	if st.Boot == nil || st.Boot.ID != id || st.Boot.Uptime != uptime {
		t.Errorf("state boot %s = %+v, want boot %s with uptime %v", when, st.Boot, id, uptime)
	}
}

// requireOneBootEvent checks that recs hold one boot event of bootB and returns it.
func requireOneBootEvent(t *testing.T, recs []model.Record, when string) *model.Boot {
	t.Helper()
	boots := bootsOf(recs)
	if len(boots) != 1 || boots[0].ev.Boot.BootID != dashed(bootB) {
		t.Fatalf("%s made %d boot events, want one of boot %s", when, len(boots), dashed(bootB))
	}
	return boots[0].ev.Boot
}

func TestBoot_SaveBeforeTheHandOverKeepsThePreviousBoot(t *testing.T) {
	t.Run("a previous boot saved", func(t *testing.T) {
		checkSaveBeforeHandOver(t, true)
	})
	t.Run("no boot saved, first start", func(t *testing.T) {
		checkSaveBeforeHandOver(t, false)
	})
}

// checkSaveBeforeHandOver runs the collector with a previous boot saved (previous) or with no boot saved, saves the state
// between the run and the hand-over of its records, and checks the boot event of a collector restarted on the saved file.
func checkSaveBeforeHandOver(t *testing.T, previous bool) {
	t.Helper()
	e := newEnv(t)
	healthyJournal(e)
	if previous {
		e.seed(dashed(bootA), before())
	} else {
		e.seed("", before())
	}

	first := requireOneBootEvent(t, e.run(false), "the first run")

	// A state save between the run and the hand-over of its records.
	if previous {
		requireSavedBoot(t, e, dashed(bootA), time.Hour, "before the run was kept")
	} else if e.store.State().Boot != nil {
		t.Errorf("state boot before the run was kept = %+v, want none", e.store.State().Boot)
	}
	e.restartOnSavedFile()

	again := requireOneBootEvent(t, e.run(false), "a collector started on the saved file in the same boot")
	if again.PreviousBootID != first.PreviousBootID || again.BootID != first.BootID {
		t.Errorf("repeated boot event boot_id = %q, previous_boot_id = %q, want %q and %q", again.BootID, again.PreviousBootID, first.BootID, first.PreviousBootID)
	}
	requireRepeatedBootPrevious(t, again, previous)
}

// requireRepeatedBootPrevious checks the previous-boot fields of a repeated boot event: the saved uptime of the previous
// boot when previous is true, and no previous fields at all for a first start.
func requireRepeatedBootPrevious(t *testing.T, again *model.Boot, previous bool) {
	t.Helper()
	if previous {
		if again.PreviousUptime == nil || *again.PreviousUptime != time.Hour {
			t.Errorf("repeated boot event previous_uptime_ns = %v, want %v (the saved uptime of the previous boot)", again.PreviousUptime, time.Hour)
		}
		return
	}
	if again.PreviousBootID != "" || again.PreviousUptime != nil {
		t.Errorf("repeated first boot event = %+v, want no previous fields", again)
	}
}

func TestBoot_StartUpHeartbeatBeforeTheFirstRunKeepsThePreviousUptime(t *testing.T) {
	e := newEnv(t)
	healthyJournal(e)
	e.seed(dashed(bootA), before())

	// The state job's first run is a heartbeat and may come before the kernel's first run.
	if _, err := agentstate.NewSaver(e.store, e.proc).Collect(context.Background(), scheduler.Run{Start: e.start}); err != nil {
		t.Fatalf("Saver.Collect() error = %v, want nil", err)
	}
	saved, _ := agentstate.Open(e.dir, slog.New(e.logs))
	if b := saved.State().Boot; b == nil || b.ID != dashed(bootA) || b.Uptime != time.Hour {
		t.Fatalf("file after the first heartbeat = %+v, want the previous boot %s with its uptime %v", b, dashed(bootA), time.Hour)
	}

	b := requireOneBootEvent(t, e.run(false), "the first run")

	if b.PreviousBootID != dashed(bootA) || b.PreviousUptime == nil || *b.PreviousUptime != time.Hour {
		t.Errorf("boot event previous_boot_id = %q, previous_uptime_ns = %v, want %q and %v (the uptime the previous boot saved)", b.PreviousBootID, b.PreviousUptime, dashed(bootA), time.Hour)
	}
}

func TestBoot_KeptRunSavesTheCurrentBoot(t *testing.T) {
	cases := []struct {
		name   string
		setup  func(e *env)
		uptime time.Duration
	}{
		{"uptime readable", func(e *env) { e.setUptime(5_000_100) }, 5_000_100 * time.Second},
		{"proc uptime missing", func(e *env) { delete(e.proc, "uptime") }, 0},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			e := newEnv(t)
			healthyJournal(e)
			e.seed(dashed(bootA), before())
			requireOneBootEvent(t, e.run(false), "the first run")
			tc.setup(e) // the uptime is read when the run is reported kept

			e.keep()

			st := e.store.State()
			if st.Boot == nil || st.Boot.ID != dashed(bootB) || !st.Boot.BootedAt.Equal(bootTime) || st.Boot.Uptime != tc.uptime {
				t.Fatalf("state boot after the report = %+v, want boot %s booted at %s with uptime %v", st.Boot, dashed(bootB), bootTime, tc.uptime)
			}
			if again := bootsOf(e.run(false)); len(again) != 0 {
				t.Errorf("the next run made %d boot events, want none", len(again))
			}
			e.restartOnSavedFile()
			if again := bootsOf(e.run(false)); len(again) != 0 {
				t.Errorf("a collector started on the saved file in the same boot made %d boot events, want none", len(again))
			}
		})
	}
}

func TestBoot_UnkeptRunLeavesTheSavedBoot(t *testing.T) {
	cases := []struct {
		name string
		stop func(e *env)
	}{
		{"reported not kept", func(e *env) { e.drop() }},
		{"never reported", func(e *env) { e.abandon() }},
		{"report for another start", func(e *env) {
			e.k.Kept(scheduler.Run{Start: e.last.Start.Add(time.Second)}, true)
			e.abandon()
		}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			e := newEnv(t)
			healthyJournal(e)
			e.seed(dashed(bootA), before())
			requireOneBootEvent(t, e.run(false), "the first run")

			tc.stop(e)

			requireSavedBoot(t, e, dashed(bootA), time.Hour, "after the unkept run")
			// Whatever the next run's PreviousLost says, the event comes again.
			b := requireOneBootEvent(t, e.run(false), "the run after the unkept one")
			if b.PreviousBootID != dashed(bootA) || b.PreviousUptime == nil || *b.PreviousUptime != time.Hour {
				t.Errorf("repeated boot event previous_boot_id = %q, previous_uptime_ns = %v, want %q and %v", b.PreviousBootID, b.PreviousUptime, dashed(bootA), time.Hour)
			}
			e.keep()
			requireSavedBoot(t, e, dashed(bootB), 5_000_000*time.Second, "after a later run was kept")
			if again := bootsOf(e.run(false)); len(again) != 0 {
				t.Errorf("the run after the kept one made %d boot events, want none", len(again))
			}
		})
	}
}

func TestBoot_ReportForAnEarlierRunThatReturnedTheEventIsIgnored(t *testing.T) {
	e := newEnv(t)
	healthyJournal(e)
	e.seed(dashed(bootA), before())
	requireOneBootEvent(t, e.run(false), "the first run")
	first := *e.last
	e.abandon()
	requireOneBootEvent(t, e.run(false), "the second run")

	e.k.Kept(first, true)

	requireSavedBoot(t, e, dashed(bootA), time.Hour, "after the report for the first run")
	e.keep()
	requireSavedBoot(t, e, dashed(bootB), 5_000_000*time.Second, "after the report for the second run")
}
