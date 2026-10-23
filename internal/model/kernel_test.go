package model_test

import (
	"encoding/json"
	"strconv"
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

// oomKillEventFromJSON builds an OOM kill event with the identity fields from JSON, so that the test compiles whether or not
// the struct already has them; a build without the fields drops them and the identity tests fail on the missing behavior.
func oomKillEventFromJSON(t *testing.T, bootID string, kernelTimeNs string) *model.KernelEvent {
	t.Helper()
	fields := ""
	if bootID != "" {
		fields += `,"boot_id":` + strconv.Quote(bootID)
	}
	if kernelTimeNs != "" {
		fields += `,"kernel_time_ns":` + kernelTimeNs
	}
	raw := `{"type":"oom_kill","oom_kill":{"victim_pid":4242,"victim_command":"mysqld"` + fields + `},"message":"Out of memory"}`
	var e model.KernelEvent
	if err := json.Unmarshal([]byte(raw), &e); err != nil {
		t.Fatalf("test setup: json.Unmarshal(%s) = %v, want nil", raw, err)
	}
	return &e
}

func TestKernelEvent_Validate_KillIdentity(t *testing.T) {
	t.Run("valid", func(t *testing.T) {
		for name, e := range map[string]*model.KernelEvent{
			"boot_id and kernel_time_ns": oomKillEventFromJSON(t, bootIDA, "123456789"),
			"kernel_time_ns zero":        oomKillEventFromJSON(t, bootIDA, "0"),
			"no identity":                oomKillEventFromJSON(t, "", ""),
		} {
			t.Run(name, func(t *testing.T) { requireValid(t, e) })
		}
	})

	invalid := []struct {
		name   string
		event  *model.KernelEvent
		field  string
		reason string
	}{
		{"boot_id malformed", oomKillEventFromJSON(t, "not-a-uuid", "5"), "oom_kill.boot_id", "invalid characters"},
		{"boot_id upper case", oomKillEventFromJSON(t, strings.ToUpper(bootIDA), "5"), "oom_kill.boot_id", "invalid characters"},
		{"boot_id with trailing newline", oomKillEventFromJSON(t, bootIDA[:35]+"\n", "5"), "oom_kill.boot_id", "invalid characters"},
		{"boot_id 37 bytes", oomKillEventFromJSON(t, bootIDA+"c", "5"), "oom_kill.boot_id", "too long"},
		{"boot_id 37 bytes with trailing newline", oomKillEventFromJSON(t, bootIDA+"\n", "5"), "oom_kill.boot_id", "too long"},
		{"kernel_time_ns negative", oomKillEventFromJSON(t, bootIDA, "-1"), "oom_kill.kernel_time_ns", "must not be negative"},
	}
	for _, tc := range invalid {
		t.Run("invalid/"+tc.name, func(t *testing.T) {
			fe := fieldErrorOf(t, tc.event.Validate(), tc.field)
			if fe.Reason != tc.reason {
				t.Errorf("Validate() reason = %q, want %q", fe.Reason, tc.reason)
			}
		})
	}
}

// marshalEvent encodes e as JSON text.
func marshalEvent(t *testing.T, e *model.KernelEvent) string {
	t.Helper()
	out, err := json.Marshal(e)
	if err != nil {
		t.Fatalf("json.Marshal() = %v, want nil", err)
	}
	return string(out)
}

func TestKernelEvent_OOMKillIdentityIsWrittenWithAZeroKernelTime(t *testing.T) {
	out := marshalEvent(t, oomKillEventFromJSON(t, bootIDA, "0"))
	for _, want := range []string{`"boot_id":"` + bootIDA + `"`, `"kernel_time_ns":0`} {
		if !strings.Contains(out, want) {
			t.Errorf("json.Marshal() = %s, want it to contain %s", out, want)
		}
	}
}

func TestKernelEvent_OOMKillWithoutIdentityOmitsIt(t *testing.T) {
	out := marshalEvent(t, oomKillEventFromJSON(t, "", ""))
	for _, absent := range []string{"boot_id", "kernel_time_ns"} {
		if strings.Contains(out, absent) {
			t.Errorf("json.Marshal() = %s, want %s omitted", out, absent)
		}
	}
}

func TestKernelEvent_OOMKillKernelTimeKeepsAllDigits(t *testing.T) {
	out := marshalEvent(t, oomKillEventFromJSON(t, bootIDB, "1718000000123456789"))
	if !strings.Contains(out, `"kernel_time_ns":1718000000123456789`) {
		t.Errorf("json.Marshal() = %s, want kernel_time_ns 1718000000123456789", out)
	}
}
