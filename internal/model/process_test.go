package model_test

import (
	"math"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func validProcessSnapshot() *model.ProcessSnapshot {
	return &model.ProcessSnapshot{
		Complete: true,
		Total:    ptr(uint32(120)),
		Processes: []model.ProcessSample{
			{
				PID:         1,
				PPID:        0,
				User:        "root",
				Command:     "systemd",
				Cmdline:     "/usr/lib/systemd/systemd --system",
				Truncated:   true,
				State:       "S",
				StartedAt:   testTime,
				CPUPercent:  1.5,
				RSSBytes:    4096,
				PSSBytes:    ptr(uint64(2048)),
				SwapBytes:   ptr(uint64(0)),
				OOMScoreAdj: ptr(int16(-1000)),
			},
			{PID: 2, Command: "kthreadd"},
		},
		Programs: []model.ProgramAggregate{
			{
				Program:        "php-fpm",
				Count:          3,
				CPUPercent:     2.5,
				RSSBytes:       8192,
				PSSBytes:       ptr(uint64(4096)),
				RSSOvercounted: true,
			},
			{Program: "nginx", Count: 1},
		},
	}
}

func TestProcessSnapshot_Kind(t *testing.T) {
	if got := validProcessSnapshot().Kind(); got != model.KindProcessSnapshot {
		t.Errorf("Kind() = %q, want %q", got, model.KindProcessSnapshot)
	}
}

func TestProcessSnapshot_Validate_Valid(t *testing.T) {
	maxItems := make([]model.ProcessSample, model.MaxItems)
	for i := range maxItems {
		maxItems[i] = model.ProcessSample{PID: int32(i + 1), Command: "c"}
	}
	tests := []struct {
		name string
		p    *model.ProcessSnapshot
	}{
		{"fully populated", validProcessSnapshot()},
		{"minimal one process", &model.ProcessSnapshot{Processes: []model.ProcessSample{{PID: 1, Command: "init"}}}},
		{"minimal one program", &model.ProcessSnapshot{Programs: []model.ProgramAggregate{{Program: "nginx", Count: 1}}}},
		{"max items", &model.ProcessSnapshot{Processes: maxItems}},
		{"cmdline of max text", &model.ProcessSnapshot{Processes: []model.ProcessSample{{PID: 1, Command: "c", Cmdline: strings.Repeat("x", model.MaxTextBytes)}}}},
		{"oom_score_adj upper bound", &model.ProcessSnapshot{Processes: []model.ProcessSample{{PID: 1, Command: "c", OOMScoreAdj: ptr(int16(1000))}}}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { requireValid(t, tc.p) })
	}
}

func TestProcessSnapshot_Validate_Invalid(t *testing.T) {
	tooMany := make([]model.ProcessSample, model.MaxItems+1)
	for i := range tooMany {
		tooMany[i] = model.ProcessSample{PID: int32(i + 1), Command: "c"}
	}
	tooManyPrograms := make([]model.ProgramAggregate, model.MaxItems+1)
	for i := range tooManyPrograms {
		tooManyPrograms[i] = model.ProgramAggregate{Program: "p" + strconv.Itoa(i), Count: 1}
	}
	proc := func(f func(s *model.ProcessSample)) func(p *model.ProcessSnapshot) {
		return func(p *model.ProcessSnapshot) { f(&p.Processes[0]) }
	}
	prog := func(f func(a *model.ProgramAggregate)) func(p *model.ProcessSnapshot) {
		return func(p *model.ProcessSnapshot) { f(&p.Programs[0]) }
	}
	cases := []invalidCase[*model.ProcessSnapshot]{
		{"no process and no program", func(p *model.ProcessSnapshot) { p.Processes, p.Programs = nil, nil }, "processes"},
		{"too many processes", func(p *model.ProcessSnapshot) { p.Processes = tooMany }, "processes"},
		{"too many programs", func(p *model.ProcessSnapshot) { p.Programs = tooManyPrograms }, "programs"},
		{"duplicate pid", func(p *model.ProcessSnapshot) { p.Processes[1].PID = p.Processes[0].PID }, "processes[1].pid"},
		{"duplicate program", func(p *model.ProcessSnapshot) { p.Programs[1].Program = p.Programs[0].Program }, "programs[1].program"},

		{"pid zero", proc(func(s *model.ProcessSample) { s.PID = 0 }), "processes[0].pid"},
		{"pid negative", proc(func(s *model.ProcessSample) { s.PID = -4 }), "processes[0].pid"},
		{"ppid negative", proc(func(s *model.ProcessSample) { s.PPID = -1 }), "processes[0].ppid"},
		{"user too long", proc(func(s *model.ProcessSample) { s.User = strings.Repeat("u", model.MaxShortTextBytes+1) }), "processes[0].user"},
		{"command empty", proc(func(s *model.ProcessSample) { s.Command = "" }), "processes[0].command"},
		{"command too long", proc(func(s *model.ProcessSample) { s.Command = strings.Repeat("c", model.MaxShortTextBytes+1) }), "processes[0].command"},
		{"cmdline too long", proc(func(s *model.ProcessSample) { s.Cmdline = strings.Repeat("c", model.MaxTextBytes+1) }), "processes[0].cmdline"},
		{"state two letters", proc(func(s *model.ProcessSample) { s.State = "SS" }), "processes[0].state"},
		{"state digit", proc(func(s *model.ProcessSample) { s.State = "1" }), "processes[0].state"},
		{"state non-ASCII letter", proc(func(s *model.ProcessSample) { s.State = "é" }), "processes[0].state"},
		{"started_at with offset", proc(func(s *model.ProcessSample) { s.StartedAt = time.Date(2026, 3, 1, 14, 0, 0, 0, plusTwo) }), "processes[0].started_at"},
		{"cpu_percent NaN", proc(func(s *model.ProcessSample) { s.CPUPercent = math.NaN() }), "processes[0].cpu_percent"},
		{"cpu_percent infinite", proc(func(s *model.ProcessSample) { s.CPUPercent = math.Inf(1) }), "processes[0].cpu_percent"},
		{"cpu_percent negative", proc(func(s *model.ProcessSample) { s.CPUPercent = -0.5 }), "processes[0].cpu_percent"},
		{"oom_score_adj too high", proc(func(s *model.ProcessSample) { s.OOMScoreAdj = ptr(int16(1001)) }), "processes[0].oom_score_adj"},
		{"oom_score_adj too low", proc(func(s *model.ProcessSample) { s.OOMScoreAdj = ptr(int16(-1001)) }), "processes[0].oom_score_adj"},

		{"program empty", prog(func(a *model.ProgramAggregate) { a.Program = "" }), "programs[0].program"},
		{"program too long", prog(func(a *model.ProgramAggregate) { a.Program = strings.Repeat("p", model.MaxShortTextBytes+1) }), "programs[0].program"},
		{"program count zero", prog(func(a *model.ProgramAggregate) { a.Count = 0 }), "programs[0].count"},
		{"program cpu_percent NaN", prog(func(a *model.ProgramAggregate) { a.CPUPercent = math.NaN() }), "programs[0].cpu_percent"},
		{"program cpu_percent negative", prog(func(a *model.ProgramAggregate) { a.CPUPercent = -1 }), "programs[0].cpu_percent"},
	}
	runInvalid(t, validProcessSnapshot, cases)
}
