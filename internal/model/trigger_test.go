package model_test

import (
	"errors"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func validKill() model.KillRef {
	return model.KillRef{BootID: bootIDA, KernelTime: 123456789, VictimPID: 4242}
}

func validTrigger() *model.SnapshotTrigger {
	return &model.SnapshotTrigger{Kills: []model.KillRef{validKill(), {BootID: bootIDB, KernelTime: 99, VictimPID: 7}}, KillsOmitted: 3}
}

// distinctKills returns n kills that differ in their kernel time.
func distinctKills(n int) []model.KillRef {
	kills := make([]model.KillRef, n)
	for i := range kills {
		kills[i] = model.KillRef{BootID: bootIDA, KernelTime: time.Duration(1000 + i), VictimPID: 100}
	}
	return kills
}

// triggerHolder lets one table drive both snapshot kinds that carry a trigger.
type triggerHolder struct {
	name string
	with func(tr *model.SnapshotTrigger) model.Payload
}

func triggerHolders() []triggerHolder {
	return []triggerHolder{
		{"process_snapshot", func(tr *model.SnapshotTrigger) model.Payload {
			p := validProcessSnapshot()
			p.Trigger = tr
			return p
		}},
		{"connection_snapshot", func(tr *model.SnapshotTrigger) model.Payload {
			c := validConnectionSnapshot()
			c.Trigger = tr
			return c
		}},
	}
}

func TestSnapshotTrigger_Validate(t *testing.T) {
	valid := []struct {
		name string
		tr   func() *model.SnapshotTrigger
	}{
		{"two kills with omitted count", validTrigger},
		{"one kill", func() *model.SnapshotTrigger { return &model.SnapshotTrigger{Kills: []model.KillRef{validKill()}} }},
		{"64 distinct kills", func() *model.SnapshotTrigger {
			return &model.SnapshotTrigger{Kills: distinctKills(model.MaxTriggerKills)}
		}},
		{"kernel_time_ns zero", func() *model.SnapshotTrigger {
			return &model.SnapshotTrigger{Kills: []model.KillRef{{BootID: bootIDA, KernelTime: 0, VictimPID: 1}}}
		}},
		{"same time and boot, other pid", func() *model.SnapshotTrigger {
			return &model.SnapshotTrigger{Kills: []model.KillRef{{BootID: bootIDA, KernelTime: 5, VictimPID: 1}, {BootID: bootIDA, KernelTime: 5, VictimPID: 2}}}
		}},
		{"same time and pid, other boot", func() *model.SnapshotTrigger {
			return &model.SnapshotTrigger{Kills: []model.KillRef{{BootID: bootIDA, KernelTime: 5, VictimPID: 1}, {BootID: bootIDB, KernelTime: 5, VictimPID: 1}}}
		}},
		{"kills_omitted at its maximum", func() *model.SnapshotTrigger {
			return &model.SnapshotTrigger{Kills: []model.KillRef{validKill()}, KillsOmitted: 4294967295}
		}},
	}
	for _, holder := range triggerHolders() {
		for _, tc := range valid {
			t.Run(holder.name+"/valid/"+tc.name, func(t *testing.T) { requireValid(t, holder.with(tc.tr())) })
		}
	}

	field := func(i int, name string) string { return "trigger.kills[" + strconv.Itoa(i) + "]." + name }
	invalidAt := []struct {
		name   string
		mutate func(tr *model.SnapshotTrigger)
		field  string
	}{
		{"boot_id empty", func(tr *model.SnapshotTrigger) { tr.Kills[0].BootID = "" }, field(0, "boot_id")},
		{"boot_id malformed", func(tr *model.SnapshotTrigger) { tr.Kills[0].BootID = "not-a-uuid" }, field(0, "boot_id")},
		{"boot_id upper case", func(tr *model.SnapshotTrigger) { tr.Kills[0].BootID = strings.ToUpper(bootIDA) }, field(0, "boot_id")},
		{"boot_id with trailing newline", func(tr *model.SnapshotTrigger) { tr.Kills[0].BootID = bootIDA + "\n" }, field(0, "boot_id")},
		{"second boot_id malformed", func(tr *model.SnapshotTrigger) { tr.Kills[1].BootID = "x" }, field(1, "boot_id")},
		{"kernel_time_ns negative", func(tr *model.SnapshotTrigger) { tr.Kills[0].KernelTime = -1 }, field(0, "kernel_time_ns")},
		{"second kernel_time_ns negative", func(tr *model.SnapshotTrigger) { tr.Kills[1].KernelTime = -1 }, field(1, "kernel_time_ns")},
		{"victim_pid zero", func(tr *model.SnapshotTrigger) { tr.Kills[0].VictimPID = 0 }, field(0, "victim_pid")},
		{"victim_pid negative", func(tr *model.SnapshotTrigger) { tr.Kills[0].VictimPID = -3 }, field(0, "victim_pid")},
		{"second victim_pid zero", func(tr *model.SnapshotTrigger) { tr.Kills[1].VictimPID = 0 }, field(1, "victim_pid")},
	}
	for _, holder := range triggerHolders() {
		for _, tc := range invalidAt {
			t.Run(holder.name+"/invalid/"+tc.name, func(t *testing.T) {
				tr := validTrigger()
				tc.mutate(tr)
				requireFieldError(t, holder.with(tr).Validate(), tc.field)
			})
		}
	}
}

// TestSnapshotTrigger_Validate_Lists covers the rules of the list itself: one to 64 kills, all distinct. The plan fixes no
// reason and no element index, so the field must be "trigger.kills" or one of its elements.
func TestSnapshotTrigger_Validate_Lists(t *testing.T) {
	cases := []struct {
		name string
		tr   *model.SnapshotTrigger
	}{
		{"no kills", &model.SnapshotTrigger{}},
		{"empty kills with omitted count", &model.SnapshotTrigger{Kills: []model.KillRef{}, KillsOmitted: 2}},
		{"65 kills", &model.SnapshotTrigger{Kills: distinctKills(model.MaxTriggerKills + 1)}},
		{"duplicate kill", &model.SnapshotTrigger{Kills: []model.KillRef{validKill(), validKill()}}},
		{"duplicate kill apart", &model.SnapshotTrigger{Kills: []model.KillRef{validKill(), {BootID: bootIDB, KernelTime: 1, VictimPID: 1}, validKill()}}},
	}
	for _, holder := range triggerHolders() {
		for _, tc := range cases {
			t.Run(holder.name+"/"+tc.name, func(t *testing.T) {
				err := holder.with(tc.tr).Validate()
				var fe *model.FieldError
				if !errors.As(err, &fe) {
					t.Fatalf("Validate() = %v, want a *model.FieldError for trigger.kills", err)
				}
				if fe.Field != "trigger.kills" && !strings.HasPrefix(fe.Field, "trigger.kills[") {
					t.Errorf("Validate() field = %q (reason %q), want trigger.kills or one of its elements", fe.Field, fe.Reason)
				}
			})
		}
	}
}

func TestSnapshotTrigger_MaxTriggerKills(t *testing.T) {
	if model.MaxTriggerKills != 64 {
		t.Errorf("MaxTriggerKills = %d, want 64", model.MaxTriggerKills)
	}
}
