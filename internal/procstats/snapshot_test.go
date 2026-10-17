package procstats

import (
	"encoding/json"
	"math"
	"strings"
	"syscall"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// cpuProc is the process of the CPU tests: it started 10 seconds after boot, which is at tick 1000.
func cpuProc(start, utime, stime uint64) procSpec {
	return procSpec{pid: 100, comm: "p", start: start, utime: utime, stime: stime, rssKB: 10, pssKB: 5, cmdline: "p\x00"}
}

func TestProcesses_CPUPercent(t *testing.T) {
	first := bootTime.Add(110 * time.Second) // the process is 100 seconds old and has used 4 seconds
	tests := []struct {
		name        string
		second      time.Time
		secondProc  procSpec
		failBetween bool // a run that fails lies between the two
		want        float64
	}{
		{"second run with a predecessor is the delta", first.Add(60 * time.Second), cpuProc(1000, 900, 100), false, 10},
		{"delta sums user and system time", first.Add(60 * time.Second), cpuProc(1000, 500, 500), false, 10},
		{"reused PID takes the average since its start", first.Add(60 * time.Second), cpuProc(11000, 3000, 0), false, 50},
		{"counters that fell take the average since the start", first.Add(60 * time.Second), cpuProc(1000, 100, 0), false, 100.0 / 100 / 160 * 100},
		{"clock set back takes the average since the start", first.Add(-10 * time.Second), cpuProc(1000, 900, 100), false, 1000.0 / 100 / 90 * 100},
		{"start equal to the previous start takes the average", first, cpuProc(1000, 900, 100), false, 1000.0 / 100 / 100 * 100},
		{"process that starts after the run has no age", bootTime.Add(5 * time.Second), cpuProc(1000, 900, 100), false, 0},
		{"a failed run is no predecessor", first.Add(120 * time.Second), cpuProc(1000, 1600, 0), true, 10},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			tr := synthTree()
			tr.add(cpuProc(1000, 300, 100))
			c, _ := newCollector(tr, nil)
			if got := snapshotOf(t, c, first, true).Processes[0].CPUPercent; !near(got, 4) {
				t.Fatalf("first run cpu_percent = %v, want 4 (400 ticks = 4 s in the 100 s since the start)", got)
			}
			if tt.failBetween {
				tr.proc.openErr["."] = syscall.EIO
				if _, err := collectAt(c, first.Add(60*time.Second), true); err == nil {
					t.Fatalf("Collect() with a failing listing error = nil, want an error")
				}
				delete(tr.proc.openErr, ".")
			}
			tr.add(tt.secondProc)

			snap := snapshotOf(t, c, tt.second, true)

			if got := snap.Processes[0].CPUPercent; !near(got, tt.want) {
				t.Errorf("cpu_percent of the second run = %v, want %v", got, tt.want)
			}
		})
	}
}

// validProcess returns a valid process sample.
func validProcess() model.ProcessSample {
	return model.ProcessSample{PID: 5, PPID: 1, User: "root", Command: "x", Cmdline: "x -a", State: "S", StartedAt: runStart, CPUPercent: 1.5, RSSBytes: 4096, PSSBytes: u64p(2048)}
}

// validProgram returns a valid program aggregate.
func validProgram() model.ProgramAggregate {
	return model.ProgramAggregate{Program: "x", Count: 2, CPUPercent: 1.5, RSSBytes: 4096, PSSBytes: u64p(2048), RSSOvercounted: true}
}

func TestCheckEntry_Refuses(t *testing.T) {
	bst := time.FixedZone("x", 3600)
	processes := []struct {
		name   string
		mutate func(p *model.ProcessSample)
		fail   bool
	}{
		{"valid", func(*model.ProcessSample) {}, false},
		{"command of 1024 bytes", func(p *model.ProcessSample) { p.Command = strings.Repeat("c", 1024) }, false},
		{"empty command", func(p *model.ProcessSample) { p.Command = "" }, true},
		{"command of 1025 bytes", func(p *model.ProcessSample) { p.Command = strings.Repeat("c", 1025) }, true},
		{"cmdline of 16385 bytes", func(p *model.ProcessSample) { p.Cmdline = strings.Repeat("c", 16385) }, true},
		{"NaN cpu_percent", func(p *model.ProcessSample) { p.CPUPercent = math.NaN() }, true},
		{"infinite cpu_percent", func(p *model.ProcessSample) { p.CPUPercent = math.Inf(1) }, true},
		{"negative cpu_percent", func(p *model.ProcessSample) { p.CPUPercent = -1 }, true},
		{"started_at in the year 10000", func(p *model.ProcessSample) { p.StartedAt = time.Date(10000, 1, 1, 0, 0, 0, 0, time.UTC) }, true},
		{"started_at not in UTC", func(p *model.ProcessSample) { p.StartedAt = runStart.In(bst) }, true},
		{"pid 0", func(p *model.ProcessSample) { p.PID = 0 }, true},
		{"negative ppid", func(p *model.ProcessSample) { p.PPID = -1 }, true},
		{"state of two letters", func(p *model.ProcessSample) { p.State = "SS" }, true},
	}
	for _, tt := range processes {
		t.Run("process "+tt.name, func(t *testing.T) {
			p := validProcess()
			tt.mutate(&p)

			size, err := checkProcessEntry(&p)

			if (err != nil) != tt.fail {
				t.Fatalf("checkProcessEntry() = %d, %v, want an error: %v", size, err, tt.fail)
			}
			if !tt.fail {
				requireEncodedSize(t, size, &p)
			}
		})
	}
	programs := []struct {
		name   string
		mutate func(a *model.ProgramAggregate)
		fail   bool
	}{
		{"valid", func(*model.ProgramAggregate) {}, false},
		{"program of 1024 bytes", func(a *model.ProgramAggregate) { a.Program = strings.Repeat("c", 1024) }, false},
		{"empty program", func(a *model.ProgramAggregate) { a.Program = "" }, true},
		{"program of 1025 bytes", func(a *model.ProgramAggregate) { a.Program = strings.Repeat("c", 1025) }, true},
		{"count 0", func(a *model.ProgramAggregate) { a.Count = 0 }, true},
		{"NaN cpu_percent", func(a *model.ProgramAggregate) { a.CPUPercent = math.NaN() }, true},
		{"negative cpu_percent", func(a *model.ProgramAggregate) { a.CPUPercent = -0.5 }, true},
	}
	for _, tt := range programs {
		t.Run("program "+tt.name, func(t *testing.T) {
			a := validProgram()
			tt.mutate(&a)

			size, err := checkProgramEntry(&a)

			if (err != nil) != tt.fail {
				t.Fatalf("checkProgramEntry() = %d, %v, want an error: %v", size, err, tt.fail)
			}
			if !tt.fail {
				requireEncodedSize(t, size, &a)
			}
		})
	}
}

// requireEncodedSize checks that size is the JSON size of v, give or take a separator.
func requireEncodedSize(t *testing.T, size int, v any) {
	t.Helper()

	data, err := json.Marshal(v)
	if err != nil {
		t.Fatalf("json.Marshal(%+v) error = %v, want nil", v, err)
	}
	if size < len(data) || size > len(data)+1 {
		t.Errorf("size = %d, want the encoded size %d (or one more for a separator)", size, len(data))
	}
}

// namedProcess returns a valid process with pid and a command of the given length.
func namedProcess(pid int32, commandBytes int) model.ProcessSample {
	p := validProcess()
	p.PID = pid
	p.Command = strings.Repeat("c", commandBytes)
	return p
}

func TestFillSnapshot_LeavesOutRefusedEntry(t *testing.T) {
	refused := namedProcess(2, 1)
	refused.Command = ""
	procs := []model.ProcessSample{namedProcess(1, 1), refused, namedProcess(3, 1)}

	snap, complete := fillSnapshot(procs, []model.ProgramAggregate{validProgram()}, 1<<20)

	if got := pidsOf(&snap); len(got) != 2 || got[0] != 1 || got[1] != 3 {
		t.Errorf("kept PIDs = %v, want [1 3] (the refused entry is left out, the others stay in order)", got)
	}
	if complete {
		t.Errorf("complete = true, want false (an entry was left out)")
	}
	if len(snap.Programs) != 1 {
		t.Errorf("kept %d programs, want 1", len(snap.Programs))
	}
}

func TestFillSnapshot_KeepsEverythingThatFits(t *testing.T) {
	procs := []model.ProcessSample{namedProcess(1, 1), namedProcess(2, 1)}
	progs := []model.ProgramAggregate{validProgram()}

	snap, complete := fillSnapshot(procs, progs, 1<<20)

	if !complete || len(snap.Processes) != 2 || len(snap.Programs) != 1 {
		t.Errorf("fillSnapshot() kept %d processes and %d programs, complete %v, want 2, 1, true", len(snap.Processes), len(snap.Programs), complete)
	}
}

func TestFillSnapshot_StopsAtTheFirstEntryThatDoesNotFit(t *testing.T) {
	small, big := namedProcess(1, 1), namedProcess(2, 1024)
	big.Cmdline = strings.Repeat("c", 1024)
	smallSize, err := checkProcessEntry(&small)
	if err != nil {
		t.Fatalf("checkProcessEntry(small) error = %v, want nil", err)
	}
	progSize, err := checkProgramEntry(&model.ProgramAggregate{Program: "x", Count: 1})
	if err != nil {
		t.Fatalf("checkProgramEntry() error = %v, want nil", err)
	}
	third := namedProcess(3, 1)
	budget := 2*progSize + 2*smallSize + 64

	snap, complete := fillSnapshot([]model.ProcessSample{small, big, third}, []model.ProgramAggregate{{Program: "x", Count: 1}}, budget)

	if got := pidsOf(&snap); len(got) != 1 || got[0] != 1 {
		t.Errorf("kept PIDs = %v, want [1] (the fill stops at the first entry that does not fit, the entry after it is not tried)", got)
	}
	if complete {
		t.Errorf("complete = true, want false")
	}
}

func TestFillSnapshot_ProgramsTakeAtMostHalfTheBudget(t *testing.T) {
	var progs []model.ProgramAggregate
	for i := range 20 {
		progs = append(progs, model.ProgramAggregate{Program: "prog" + string(rune('a'+i)), Count: 1})
	}
	progSize, err := checkProgramEntry(&progs[0])
	if err != nil {
		t.Fatalf("checkProgramEntry() error = %v, want nil", err)
	}
	budget := 8 * progSize
	procs := []model.ProcessSample{{PID: 1, Command: "c", StartedAt: runStart}}

	snap, complete := fillSnapshot(procs, progs, budget)

	if kept := len(snap.Programs); kept == 0 || kept > 4 {
		t.Errorf("kept %d programs of budget for 8, want at most 4 (half the budget) and at least one", kept)
	}
	for i := range snap.Programs {
		if snap.Programs[i].Program != progs[i].Program {
			t.Errorf("kept program %d = %q, want %q (the input order)", i, snap.Programs[i].Program, progs[i].Program)
		}
	}
	if complete {
		t.Errorf("complete = true, want false (programs were left out)")
	}
	if len(snap.Processes) != 1 {
		t.Errorf("kept %d processes, want the one that fits beside the programs", len(snap.Processes))
	}
}
