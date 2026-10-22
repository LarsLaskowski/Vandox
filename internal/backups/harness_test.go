package backups

import (
	"bytes"
	"context"
	"fmt"
	"io/fs"
	"log/slog"
	"math"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"testing/fstest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// The boots of the harness and a text no log line may contain; the text is never part of a test name or path.
const (
	bootA = "c0ffee00-c0ff-4e00-8c0f-fee00c0ffee1"
	bootB = "c0ffee00-c0ff-4e00-8c0f-fee00c0ffee2"
	leak  = "zq7leak-backup"
)

// t0 is the time of the first run of every test.
var t0 = time.Date(2026, time.October, 22, 3, 0, 0, 0, time.UTC)

// pzstdRule is the default rule of the option backups.processes.
var pzstdRule = Rule{Command: "pzstd"}

// at returns the time sec seconds after t0, the start of a run or of a process.
func at(sec int) time.Time {
	return t0.Add(time.Duration(sec) * time.Second)
}

// world is a /proc in memory: the boot ID, the boot time of /proc/stat and the processes below it.
type world struct {
	fs    fstest.MapFS
	btime time.Time
}

// newWorld returns a /proc of the boot that started at btime (whole seconds).
func newWorld(boot string, btime time.Time) *world {
	w := &world{fs: fstest.MapFS{}}
	w.reboot(boot, btime)
	return w
}

// reboot replaces the boot and removes every process, as a new boot does; the file system stays the same map.
func (w *world) reboot(boot string, btime time.Time) {
	clear(w.fs)
	w.fs["sys/kernel/random/boot_id"] = &fstest.MapFile{Data: []byte(boot + "\n")}
	w.fs["stat"] = &fstest.MapFile{Data: []byte("cpu  1 2 3 4 5 6 7 8 9 10\nbtime " + strconv.FormatInt(btime.Unix(), 10) + "\nprocesses 5\n")}
	w.btime = btime
}

// statFieldsAfterComm is the number of fields of a stat line after the closing parenthesis of the process name, as the kernel
// writes it from field 3 (state) to field 52.
const statFieldsAfterComm = 50

// statLine returns the stat file of a process: the fields the collectors read (state, parent, starttime) and zeros elsewhere.
func statLine(pid int, comm string, startTicks int64) string {
	fields := make([]string, statFieldsAfterComm)
	for i := range fields {
		fields[i] = "0"
	}
	fields[0] = "S"
	fields[1] = "1"
	fields[22-3] = strconv.FormatInt(startTicks, 10)
	return strconv.Itoa(pid) + " (" + comm + ") " + strings.Join(fields, " ") + "\n"
}

// add starts the process pid at start (whole seconds after the boot's start); cmdline is the raw file with its NUL separators.
func (w *world) add(pid int, comm, cmdline string, start time.Time) {
	ticks := int64(start.Sub(w.btime) / (time.Second / procstats.ClockTicks))
	dir := strconv.Itoa(pid) + "/"
	w.fs[dir+"stat"] = &fstest.MapFile{Data: []byte(statLine(pid, comm, ticks))}
	w.fs[dir+"cmdline"] = &fstest.MapFile{Data: []byte(cmdline)}
}

// addPzstd starts a process of the command pzstd.
func (w *world) addPzstd(pid int, start time.Time) {
	w.add(pid, "pzstd", "/usr/bin/pzstd\x00-p\x004\x00/backup/web.tar\x00", start)
}

// remove ends the process pid.
func (w *world) remove(pid int) {
	delete(w.fs, strconv.Itoa(pid)+"/stat")
	delete(w.fs, strconv.Itoa(pid)+"/cmdline")
}

// removeAll ends the processes pids.
func (w *world) removeAll(pids ...int) {
	for _, pid := range pids {
		w.remove(pid)
	}
}

// env is a collector over a world, with the real state store in a temporary directory and the log in a buffer.
type env struct {
	t     *testing.T
	w     *world
	rules []Rule
	dir   string
	store *agentstate.Store
	logs  *bytes.Buffer
	log   *slog.Logger
	b     *Backups
}

// newEnv returns a collector for rules over a /proc of the boot bootA that started five days before t0.
func newEnv(t *testing.T, rules ...Rule) *env {
	t.Helper()
	e := &env{t: t, w: newWorld(bootA, t0.Add(-120*time.Hour)), rules: rules, dir: t.TempDir(), logs: &bytes.Buffer{}}
	e.log = slog.New(slog.NewTextHandler(e.logs, &slog.HandlerOptions{Level: slog.LevelDebug}))
	store, status := agentstate.Open(e.dir, e.log)
	if status != agentstate.StatusFirstStart {
		t.Fatalf("agentstate.Open(empty directory) status = %v, want %v", status, agentstate.StatusFirstStart)
	}
	e.store = store
	e.restart()
	return e
}

// restart replaces the collector by a new one over the same store, as a restart of the agent does with its state in memory.
func (e *env) restart() {
	e.b = New(e.w.fs, e.rules, e.store, e.log)
}

// reopen saves the state to its file at sec, opens it again and starts a new collector over it, as a restart of the agent does.
func (e *env) reopen(sec int) {
	e.t.Helper()
	if _, err := agentstate.NewSaver(e.store, e.w.fs).Collect(context.Background(), scheduler.Run{Start: at(sec)}); err != nil {
		e.t.Fatalf("Saver.Collect() error = %v, want nil", err)
	}
	if _, err := os.Stat(filepath.Join(e.dir, agentstate.FileName)); err != nil {
		e.t.Fatalf("the state was not saved: %v, want the file %s", err, agentstate.FileName)
	}
	store, status := agentstate.Open(e.dir, e.log)
	if status != agentstate.StatusLoaded {
		e.t.Fatalf("agentstate.Open(saved state) status = %v, want %v", status, agentstate.StatusLoaded)
	}
	e.store = store
	e.restart()
}

// run collects at sec seconds after t0, checks every record and returns the backup run payloads.
func (e *env) run(sec int) []*model.BackupRun {
	e.t.Helper()
	records, err := e.b.Collect(context.Background(), scheduler.Run{Start: at(sec)})
	if err != nil {
		e.t.Fatalf("Collect(run at +%d s) error = %v, want nil", sec, err)
	}
	runs := make([]*model.BackupRun, 0, len(records))
	for i := range records {
		runs = append(runs, requireValidRecord(e.t, sec, &records[i]))
	}
	return runs
}

// requireValidRecord checks a record of a run at sec as the scheduler would check it after it stamps it, and the capture time
// the plan fixes for the phase.
func requireValidRecord(t *testing.T, sec int, r *model.Record) *model.BackupRun {
	t.Helper()
	run, ok := r.Data.(*model.BackupRun)
	if !ok {
		t.Fatalf("run at +%d s: record data is %T, want *model.BackupRun", sec, r.Data)
	}
	if r.Source != Name {
		t.Errorf("run at +%d s: record source = %q, want %q", sec, r.Source, Name)
	}
	wantCaptured := run.StartedAt
	if run.Phase == model.BackupPhaseEnd {
		wantCaptured = run.LastSeenAt
	}
	if !r.CapturedAt.Equal(wantCaptured) {
		t.Errorf("run at +%d s: %s record CapturedAt = %v, want %v (started_at for a start, last_seen_at for an end)", sec, run.Phase, r.CapturedAt, wantCaptured)
	}
	stamped := *r
	stamped.Origin, stamped.Seq = model.OriginAgent, math.MaxInt64
	if _, err := wire.CheckRecord(&stamped); err != nil {
		t.Errorf("run at +%d s: wire.CheckRecord(%+v) error = %v, want nil", sec, *run, err)
	}
	return run
}

// quiet checks that the run at sec returns no record.
func (e *env) quiet(sec int) {
	e.t.Helper()
	if got := e.run(sec); len(got) != 0 {
		e.t.Errorf("run at +%d s returned %s, want no record", sec, describe(got))
	}
}

// quietRuns checks that each run at the given seconds returns no record.
func (e *env) quietRuns(secs ...int) {
	e.t.Helper()
	for _, sec := range secs {
		e.quiet(sec)
	}
}

// describe returns a short text of the payloads for a failure message.
func describe(runs []*model.BackupRun) string {
	parts := make([]string, 0, len(runs))
	for _, r := range runs {
		parts = append(parts, fmt.Sprintf("%+v", *r))
	}
	return strings.Join(parts, ", ")
}

// startWant is a start record.
type startWant struct {
	boot      string
	started   time.Time
	pid       int32
	command   string
	processes uint32
}

// endWant is an end record.
type endWant struct {
	boot     string
	started  time.Time
	lastSeen time.Time
	goneBy   time.Time
	max      uint32
}

// phases splits the payloads of a run into starts and ends.
func phases(runs []*model.BackupRun) (starts, ends []*model.BackupRun) {
	for _, r := range runs {
		if r.Phase == model.BackupPhaseStart {
			starts = append(starts, r)
		} else {
			ends = append(ends, r)
		}
	}
	return starts, ends
}

// requireOneStart checks that runs holds exactly one record, a start as want.
func requireOneStart(t *testing.T, runs []*model.BackupRun, want startWant) {
	t.Helper()
	if len(runs) != 1 {
		t.Fatalf("got %d records %s, want exactly one start", len(runs), describe(runs))
	}
	requireStart(t, runs[0], want)
}

// requireStart checks a start record.
func requireStart(t *testing.T, got *model.BackupRun, want startWant) {
	t.Helper()
	if got.Phase != model.BackupPhaseStart {
		t.Fatalf("phase = %q, want %q: %+v", got.Phase, model.BackupPhaseStart, *got)
	}
	if got.BootID != want.boot {
		t.Errorf("start boot_id = %q, want %q", got.BootID, want.boot)
	}
	if !got.StartedAt.Equal(want.started) {
		t.Errorf("start started_at = %v, want %v (the earliest start time of the matching processes)", got.StartedAt, want.started)
	}
	if got.PID != want.pid || got.Command != want.command {
		t.Errorf("start pid and command = %d %q, want %d %q (those of the earliest process)", got.PID, got.Command, want.pid, want.command)
	}
	if got.Processes != want.processes {
		t.Errorf("start processes = %d, want %d", got.Processes, want.processes)
	}
}

// requireOneEnd checks that runs holds exactly one record, an end as want.
func requireOneEnd(t *testing.T, runs []*model.BackupRun, want endWant) {
	t.Helper()
	if len(runs) != 1 {
		t.Fatalf("got %d records %s, want exactly one end", len(runs), describe(runs))
	}
	requireEnd(t, runs[0], want)
}

// requireEnd checks an end record.
func requireEnd(t *testing.T, got *model.BackupRun, want endWant) {
	t.Helper()
	if got.Phase != model.BackupPhaseEnd {
		t.Fatalf("phase = %q, want %q: %+v", got.Phase, model.BackupPhaseEnd, *got)
	}
	if got.BootID != want.boot {
		t.Errorf("end boot_id = %q, want %q", got.BootID, want.boot)
	}
	if !got.StartedAt.Equal(want.started) {
		t.Errorf("end started_at = %v, want %v (the start time that pairs it with its start)", got.StartedAt, want.started)
	}
	if !got.LastSeenAt.Equal(want.lastSeen) {
		t.Errorf("end last_seen_at = %v, want %v", got.LastSeenAt, want.lastSeen)
	}
	if !got.GoneBy.Equal(want.goneBy) {
		t.Errorf("end gone_by = %v, want %v (the first check that saw no matching process)", got.GoneBy, want.goneBy)
	}
	if got.MaxProcesses != want.max {
		t.Errorf("end max_processes = %d, want %d", got.MaxProcesses, want.max)
	}
}

// requireNoOpenRun checks that the state holds no open run.
func requireNoOpenRun(t *testing.T, store *agentstate.Store) {
	t.Helper()
	if got := store.State().Backup; got != nil {
		t.Errorf("state Backup = %+v, want nil (the run has ended)", *got)
	}
}

// requireOpenRun checks the open run of the state.
func requireOpenRun(t *testing.T, store *agentstate.Store, want agentstate.OpenRun) {
	t.Helper()
	got := store.State().Backup
	if got == nil {
		t.Fatalf("state Backup = nil, want %+v", want)
	}
	if got.BootID != want.BootID || !got.StartedAt.Equal(want.StartedAt) || !got.LastSeen.Equal(want.LastSeen) || got.MaxProcesses != want.MaxProcesses {
		t.Errorf("state Backup = %+v, want %+v", *got, want)
	}
}

// countingFS counts the opens of the file system below it.
type countingFS struct {
	fs.FS
	opens *int
}

// Open counts the open and passes it on.
func (c countingFS) Open(name string) (fs.File, error) {
	*c.opens++
	return c.FS.Open(name)
}
