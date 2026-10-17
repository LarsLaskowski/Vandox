package procstats

import (
	"cmp"
	"encoding/json"
	"errors"
	"math"
	"slices"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// errEntryRefused is the fixed reason an entry is left out of the snapshot; it holds no text of the entry.
var errEntryRefused = errors.New("processes: entry refused")

// previousRun is what the CPU percentage of the next run is measured against: the processes of the last successful run.
type previousRun struct {
	start time.Time
	procs map[int32]tickCount
}

// tickCount identifies a process by its start and holds its CPU ticks.
type tickCount struct {
	start, ticks uint64
}

// cpuPercent returns the CPU use of a process with starttime start that has used ticks clock ticks, as a percentage of one
// CPU. With a predecessor (same PID and start in the previous successful run, runs moving forward, counters not fallen) it
// is the use between the two runs; else it is the average since the process started, and 0 when it has no age.
func cpuPercent(pid int32, start, ticks uint64, startedAt, runStart time.Time, prev *previousRun) float64 {
	if prev != nil && runStart.After(prev.start) {
		if old, ok := prev.procs[pid]; ok && old.start == start && ticks >= old.ticks {
			return float64(ticks-old.ticks) / ClockTicks / runStart.Sub(prev.start).Seconds() * 100
		}
	}
	age := runStart.Sub(startedAt).Seconds()
	if age <= 0 {
		return 0
	}
	return float64(ticks) / ClockTicks / age * 100
}

// satAdd returns a + b, or the largest value when the sum does not fit.
func satAdd(a, b uint64) uint64 {
	if sum := a + b; sum >= a {
		return sum
	}
	return math.MaxUint64
}

// aggregate sums the processes per program: the count, the CPU use, the RSS and, when every member has one, the PSS.
// program[i] is the program of procs[i].
func aggregate(program []string, procs []model.ProcessSample) []model.ProgramAggregate {
	index := make(map[string]int, len(procs))
	var progs []model.ProgramAggregate
	var partial []bool // whether a member of the program at the same index has no PSS
	for i := range procs {
		p := &procs[i]
		at, found := index[program[i]]
		if !found {
			at = len(progs)
			index[program[i]] = at
			progs = append(progs, model.ProgramAggregate{Program: program[i], PSSBytes: new(uint64)})
			partial = append(partial, false)
		}
		a := &progs[at]
		a.Count++
		a.CPUPercent += p.CPUPercent
		a.RSSBytes = satAdd(a.RSSBytes, p.RSSBytes)
		if p.PSSBytes == nil {
			partial[at] = true
		} else {
			*a.PSSBytes = satAdd(*a.PSSBytes, *p.PSSBytes)
		}
	}
	for i := range progs {
		if partial[i] {
			progs[i].PSSBytes = nil
		}
		progs[i].RSSOvercounted = progs[i].Count > 1
	}
	return progs
}

// processKey returns the memory key of a process: the PSS when usePSS (every process of the run has one), else the RSS, so
// one key ranks the whole run.
func processKey(usePSS bool) func(p *model.ProcessSample) uint64 {
	return func(p *model.ProcessSample) uint64 {
		if usePSS && p.PSSBytes != nil {
			return *p.PSSBytes
		}
		return p.RSSBytes
	}
}

// programKey is processKey for programs.
func programKey(usePSS bool) func(a *model.ProgramAggregate) uint64 {
	return func(a *model.ProgramAggregate) uint64 {
		if usePSS && a.PSSBytes != nil {
			return *a.PSSBytes
		}
		return a.RSSBytes
	}
}

// allHavePSS reports whether every process has a PSS.
func allHavePSS(procs []model.ProcessSample) bool {
	return !slices.ContainsFunc(procs, func(p model.ProcessSample) bool { return p.PSSBytes == nil })
}

// allProgramsHavePSS reports whether every program has a PSS.
func allProgramsHavePSS(progs []model.ProgramAggregate) bool {
	return !slices.ContainsFunc(progs, func(a model.ProgramAggregate) bool { return a.PSSBytes == nil })
}

// sortProcesses orders procs by the memory key descending, then by PID.
func sortProcesses(procs []model.ProcessSample, usePSS bool) {
	key := processKey(usePSS)
	slices.SortFunc(procs, func(a, b model.ProcessSample) int {
		return cmp.Or(cmp.Compare(key(&b), key(&a)), cmp.Compare(a.PID, b.PID))
	})
}

// sortPrograms orders progs by the memory key descending, then by name.
func sortPrograms(progs []model.ProgramAggregate, usePSS bool) {
	key := programKey(usePSS)
	slices.SortFunc(progs, func(a, b model.ProgramAggregate) int {
		return cmp.Or(cmp.Compare(key(&b), key(&a)), cmp.Compare(a.Program, b.Program))
	})
}

// selectTop returns the union of the first TopProcesses of procs, which are ordered by memory, and the TopProcesses with the
// highest CPU use (ties by PID), in the order of procs.
func selectTop(procs []model.ProcessSample) []model.ProcessSample {
	if len(procs) <= TopProcesses {
		return procs
	}
	byCPU := slices.Clone(procs)
	slices.SortFunc(byCPU, func(a, b model.ProcessSample) int {
		return cmp.Or(cmp.Compare(b.CPUPercent, a.CPUPercent), cmp.Compare(a.PID, b.PID))
	})
	chosen := make(map[int32]struct{}, 2*TopProcesses)
	for i := range TopProcesses {
		chosen[procs[i].PID] = struct{}{}
		chosen[byCPU[i].PID] = struct{}{}
	}
	return slices.DeleteFunc(slices.Clone(procs), func(p model.ProcessSample) bool {
		_, keep := chosen[p.PID]
		return !keep
	})
}

// checkProcessEntry validates and encodes p alone and returns its encoded size, with the separator of the list.
func checkProcessEntry(p *model.ProcessSample) (size int, err error) {
	one := model.ProcessSnapshot{Processes: []model.ProcessSample{*p}}
	if one.Validate() != nil {
		return 0, errEntryRefused
	}
	data, err := json.Marshal(p)
	if err != nil {
		return 0, errEntryRefused
	}
	return len(data) + 1, nil
}

// checkProgramEntry validates and encodes a alone and returns its encoded size, with the separator of the list.
func checkProgramEntry(a *model.ProgramAggregate) (size int, err error) {
	one := model.ProcessSnapshot{Programs: []model.ProgramAggregate{*a}}
	if one.Validate() != nil {
		return 0, errEntryRefused
	}
	data, err := json.Marshal(a)
	if err != nil {
		return 0, errEntryRefused
	}
	return len(data) + 1, nil
}

// fillSnapshot keeps entries within budget bytes (programs first, at most budget/2); complete is false when one was left out.
func fillSnapshot(procs []model.ProcessSample, progs []model.ProgramAggregate, budget int) (snap model.ProcessSnapshot, complete bool) {
	snap, _, complete = fill(procs, progs, budget)
	snap.Complete = complete
	return snap, complete
}

// fill is fillSnapshot that also counts the entries it refused. The programs are taken in their order up to half the
// budget, then the processes in theirs up to the budget; each list stops at the first entry that does not fit, and an
// entry that is refused is skipped.
func fill(procs []model.ProcessSample, progs []model.ProgramAggregate, budget int) (snap model.ProcessSnapshot, refused int, complete bool) {
	complete = true
	used := 0
	for i := range progs {
		size, err := checkProgramEntry(&progs[i])
		if err != nil {
			refused++
			complete = false
			continue
		}
		if used+size > budget/2 {
			complete = false
			break
		}
		snap.Programs = append(snap.Programs, progs[i])
		used += size
	}
	for i := range procs {
		size, err := checkProcessEntry(&procs[i])
		if err != nil {
			refused++
			complete = false
			continue
		}
		if used+size > budget {
			complete = false
			break
		}
		snap.Processes = append(snap.Processes, procs[i])
		used += size
	}
	return snap, refused, complete
}
