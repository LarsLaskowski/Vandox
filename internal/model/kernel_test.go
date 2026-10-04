package model_test

import (
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	bootIDA = "0b6f9b0c-2d1e-4c43-9a4e-7f1b2c3d4e5f"
	bootIDB = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d"
)

func validOOMKillEvent() *model.KernelEvent {
	return &model.KernelEvent{
		Type: model.KernelEventOOMKill,
		OOMKill: &model.OOMKill{
			VictimPID:     4242,
			VictimCommand: "mysqld",
			AnonRSSBytes:  ptr(uint64(1 << 30)),
			OOMScoreAdj:   ptr(int16(-900)),
		},
		Message: "Out of memory: Killed process 4242 (mysqld)",
	}
}

func validBootEvent() *model.KernelEvent {
	return &model.KernelEvent{
		Type: model.KernelEventBoot,
		Boot: &model.Boot{
			BootID:         bootIDA,
			PreviousBootID: bootIDB,
			BootedAt:       testTime,
			PreviousUptime: ptr(36 * time.Hour),
		},
		Message: "system booted",
	}
}

func TestKernelEvent_Kind(t *testing.T) {
	if got := validOOMKillEvent().Kind(); got != model.KindKernelEvent {
		t.Errorf("Kind() = %q, want %q", got, model.KindKernelEvent)
	}
}

func TestKernelEvent_Validate_Valid(t *testing.T) {
	tests := []struct {
		name string
		p    *model.KernelEvent
	}{
		{"oom kill fully populated", validOOMKillEvent()},
		{"boot fully populated", validBootEvent()},
		{"oom kill minimal", &model.KernelEvent{Type: model.KernelEventOOMKill, OOMKill: &model.OOMKill{VictimPID: 1, VictimCommand: "x"}}},
		{"boot minimal", &model.KernelEvent{Type: model.KernelEventBoot, Boot: &model.Boot{BootID: bootIDA}}},
		{"message of max text", &model.KernelEvent{Type: model.KernelEventBoot, Boot: &model.Boot{BootID: bootIDA}, Message: strings.Repeat("m", model.MaxTextBytes)}},
		{"oom_score_adj upper bound", &model.KernelEvent{Type: model.KernelEventOOMKill, OOMKill: &model.OOMKill{VictimPID: 1, VictimCommand: "x", OOMScoreAdj: ptr(int16(1000))}}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { requireValid(t, tc.p) })
	}
}

func TestKernelEvent_Validate_InvalidOOMKill(t *testing.T) {
	cases := []invalidCase[*model.KernelEvent]{
		{"type empty", func(p *model.KernelEvent) { p.Type = "" }, "type"},
		{"type unknown", func(p *model.KernelEvent) { p.Type = "panic" }, "type"},
		{"type upper case", func(p *model.KernelEvent) { p.Type = "OOM_KILL" }, "type"},
		{"oom_kill missing", func(p *model.KernelEvent) { p.OOMKill = nil }, "oom_kill"},
		{"boot set next to oom_kill", func(p *model.KernelEvent) { p.Boot = &model.Boot{BootID: bootIDA} }, "boot"},
		{"message too long", func(p *model.KernelEvent) { p.Message = strings.Repeat("m", model.MaxTextBytes+1) }, "message"},
		{"victim_pid zero", func(p *model.KernelEvent) { p.OOMKill.VictimPID = 0 }, "oom_kill.victim_pid"},
		{"victim_pid negative", func(p *model.KernelEvent) { p.OOMKill.VictimPID = -1 }, "oom_kill.victim_pid"},
		{"victim_command empty", func(p *model.KernelEvent) { p.OOMKill.VictimCommand = "" }, "oom_kill.victim_command"},
		{"victim_command too long", func(p *model.KernelEvent) {
			p.OOMKill.VictimCommand = strings.Repeat("c", model.MaxShortTextBytes+1)
		}, "oom_kill.victim_command"},
		{"oom_score_adj too high", func(p *model.KernelEvent) { p.OOMKill.OOMScoreAdj = ptr(int16(1001)) }, "oom_kill.oom_score_adj"},
		{"oom_score_adj too low", func(p *model.KernelEvent) { p.OOMKill.OOMScoreAdj = ptr(int16(-1001)) }, "oom_kill.oom_score_adj"},
	}
	runInvalid(t, validOOMKillEvent, cases)
}

func TestKernelEvent_Validate_InvalidBoot(t *testing.T) {
	cases := []invalidCase[*model.KernelEvent]{
		{"boot missing", func(p *model.KernelEvent) { p.Boot = nil }, "boot"},
		{"oom_kill set next to boot", func(p *model.KernelEvent) { p.OOMKill = &model.OOMKill{VictimPID: 1, VictimCommand: "x"} }, "oom_kill"},
		{"boot_id empty", func(p *model.KernelEvent) { p.Boot.BootID = "" }, "boot.boot_id"},
		{"boot_id upper case", func(p *model.KernelEvent) { p.Boot.BootID = strings.ToUpper(bootIDA) }, "boot.boot_id"},
		{"boot_id without dashes", func(p *model.KernelEvent) { p.Boot.BootID = strings.ReplaceAll(bootIDA, "-", "") }, "boot.boot_id"},
		{"boot_id with braces", func(p *model.KernelEvent) { p.Boot.BootID = "{" + bootIDA + "}" }, "boot.boot_id"},
		{"boot_id as urn", func(p *model.KernelEvent) { p.Boot.BootID = "urn:uuid:" + bootIDA }, "boot.boot_id"},
		{"boot_id with trailing newline", func(p *model.KernelEvent) { p.Boot.BootID = bootIDA + "\n" }, "boot.boot_id"},
		{"previous_boot_id malformed", func(p *model.KernelEvent) { p.Boot.PreviousBootID = "not-a-uuid" }, "boot.previous_boot_id"},
		{"previous_boot_id upper case", func(p *model.KernelEvent) { p.Boot.PreviousBootID = strings.ToUpper(bootIDB) }, "boot.previous_boot_id"},
		{"booted_at with offset", func(p *model.KernelEvent) { p.Boot.BootedAt = time.Date(2026, 3, 1, 14, 0, 0, 0, plusTwo) }, "boot.booted_at"},
		{"previous_uptime negative", func(p *model.KernelEvent) { p.Boot.PreviousUptime = ptr(-time.Second) }, "boot.previous_uptime_ns"},
	}
	runInvalid(t, validBootEvent, cases)
}
