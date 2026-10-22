package model_test

import (
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func validBackupStart() *model.BackupRun {
	return &model.BackupRun{
		Phase:     model.BackupPhaseStart,
		BootID:    bootIDA,
		StartedAt: testTime,
		PID:       4242,
		Command:   "pzstd",
		Processes: 3,
	}
}

func validBackupEnd() *model.BackupRun {
	return &model.BackupRun{
		Phase:        model.BackupPhaseEnd,
		BootID:       bootIDA,
		StartedAt:    testTime,
		LastSeenAt:   testTime.Add(5 * time.Minute),
		GoneBy:       testTime.Add(5*time.Minute + 10*time.Second),
		MaxProcesses: 4,
	}
}

func TestBackupRun_Kind(t *testing.T) {
	if got := validBackupStart().Kind(); got != model.KindBackupRun {
		t.Errorf("Kind() = %q, want %q", got, model.KindBackupRun)
	}
	if got := model.KindBackupRun; got != "backup_run" {
		t.Errorf("KindBackupRun = %q, want %q", got, "backup_run")
	}
}

func TestBackupRun_Validate(t *testing.T) {
	valid := []struct {
		name string
		p    *model.BackupRun
	}{
		{"start fully populated", validBackupStart()},
		{"end fully populated", validBackupEnd()},
		{"start with one process", func() *model.BackupRun { p := validBackupStart(); p.Processes = 1; return p }()},
		{"start with command of max length", func() *model.BackupRun {
			p := validBackupStart()
			p.Command = strings.Repeat("c", model.MaxShortTextBytes)
			return p
		}()},
		{"end seen at the start (single sighting)", func() *model.BackupRun {
			p := validBackupEnd()
			p.LastSeenAt = p.StartedAt
			p.GoneBy = p.StartedAt.Add(time.Second)
			p.MaxProcesses = 1
			return p
		}()},
		{"end gone by equals last seen", func() *model.BackupRun {
			p := validBackupEnd()
			p.GoneBy = p.LastSeenAt
			return p
		}()},
	}
	for _, tc := range valid {
		t.Run("valid/"+tc.name, func(t *testing.T) { requireValid(t, tc.p) })
	}

	startCases := []invalidCase[*model.BackupRun]{
		{"phase empty", func(p *model.BackupRun) { p.Phase = "" }, "phase"},
		{"phase unknown", func(p *model.BackupRun) { p.Phase = "middle" }, "phase"},
		{"phase upper case", func(p *model.BackupRun) { p.Phase = "START" }, "phase"},
		{"boot_id empty", func(p *model.BackupRun) { p.BootID = "" }, "boot_id"},
		{"boot_id malformed", func(p *model.BackupRun) { p.BootID = "not-a-uuid" }, "boot_id"},
		{"boot_id upper case", func(p *model.BackupRun) { p.BootID = strings.ToUpper(bootIDA) }, "boot_id"},
		{"boot_id with trailing newline", func(p *model.BackupRun) { p.BootID = bootIDA + "\n" }, "boot_id"},
		{"started_at zero", func(p *model.BackupRun) { p.StartedAt = time.Time{} }, "started_at"},
		{"started_at with offset", func(p *model.BackupRun) { p.StartedAt = time.Date(2026, 3, 1, 14, 0, 0, 0, plusTwo) }, "started_at"},
		{"pid zero", func(p *model.BackupRun) { p.PID = 0 }, "pid"},
		{"pid negative", func(p *model.BackupRun) { p.PID = -1 }, "pid"},
		{"command empty", func(p *model.BackupRun) { p.Command = "" }, "command"},
		{"command too long", func(p *model.BackupRun) { p.Command = strings.Repeat("c", model.MaxShortTextBytes+1) }, "command"},
		{"processes zero", func(p *model.BackupRun) { p.Processes = 0 }, "processes"},
	}
	runInvalid(t, validBackupStart, startCases)

	endCases := []invalidCase[*model.BackupRun]{
		{"phase empty", func(p *model.BackupRun) { p.Phase = "" }, "phase"},
		{"boot_id empty", func(p *model.BackupRun) { p.BootID = "" }, "boot_id"},
		{"boot_id malformed", func(p *model.BackupRun) { p.BootID = "not-a-uuid" }, "boot_id"},
		{"started_at zero", func(p *model.BackupRun) { p.StartedAt = time.Time{} }, "started_at"},
		{"started_at with offset", func(p *model.BackupRun) { p.StartedAt = time.Date(2026, 3, 1, 14, 0, 0, 0, plusTwo) }, "started_at"},
		{"last_seen_at zero", func(p *model.BackupRun) { p.LastSeenAt = time.Time{} }, "last_seen_at"},
		{"last_seen_at with offset", func(p *model.BackupRun) { p.LastSeenAt = time.Date(2026, 3, 1, 14, 5, 0, 0, plusTwo) }, "last_seen_at"},
		{"gone_by zero", func(p *model.BackupRun) { p.GoneBy = time.Time{} }, "gone_by"},
		{"gone_by with offset", func(p *model.BackupRun) { p.GoneBy = time.Date(2026, 3, 1, 14, 6, 0, 0, plusTwo) }, "gone_by"},
		{"gone_by before last_seen_at", func(p *model.BackupRun) { p.GoneBy = p.LastSeenAt.Add(-time.Nanosecond) }, "gone_by"},
		{"gone_by before started_at", func(p *model.BackupRun) { p.LastSeenAt, p.GoneBy = p.StartedAt, p.StartedAt.Add(-time.Second) }, "gone_by"},
		{"max_processes zero", func(p *model.BackupRun) { p.MaxProcesses = 0 }, "max_processes"},
	}
	runInvalid(t, validBackupEnd, endCases)
}

func TestBackupRun_Validate_Reasons(t *testing.T) {
	cases := []struct {
		name   string
		p      func() *model.BackupRun
		mutate func(p *model.BackupRun)
		field  string
		reason string
	}{
		{"start with last_seen_at", validBackupStart, func(p *model.BackupRun) { p.LastSeenAt = testTime.Add(time.Minute) }, "last_seen_at", "not allowed for phase start"},
		{"start with gone_by", validBackupStart, func(p *model.BackupRun) { p.GoneBy = testTime.Add(time.Minute) }, "gone_by", "not allowed for phase start"},
		{"start with max_processes", validBackupStart, func(p *model.BackupRun) { p.MaxProcesses = 2 }, "max_processes", "not allowed for phase start"},
		{"end with pid", validBackupEnd, func(p *model.BackupRun) { p.PID = 4242 }, "pid", "not allowed for phase end"},
		{"end with command", validBackupEnd, func(p *model.BackupRun) { p.Command = "pzstd" }, "command", "not allowed for phase end"},
		{"end with processes", validBackupEnd, func(p *model.BackupRun) { p.Processes = 2 }, "processes", "not allowed for phase end"},
		{"end with last_seen_at before started_at", validBackupEnd, func(p *model.BackupRun) { p.LastSeenAt = p.StartedAt.Add(-time.Nanosecond) }, "last_seen_at", "must not be before started_at"},
		{"start with boot_id missing", validBackupStart, func(p *model.BackupRun) { p.BootID = "" }, "boot_id", "required"},
		{"start with started_at missing", validBackupStart, func(p *model.BackupRun) { p.StartedAt = time.Time{} }, "started_at", "required"},
		{"start with started_at in a zone", validBackupStart, func(p *model.BackupRun) { p.StartedAt = time.Date(2026, 3, 1, 14, 0, 0, 0, plusTwo) }, "started_at", "must be UTC"},
		{"phase unknown", validBackupStart, func(p *model.BackupRun) { p.Phase = "middle" }, "phase", "unknown value"},
		{"command too long", validBackupStart, func(p *model.BackupRun) { p.Command = strings.Repeat("c", model.MaxShortTextBytes+1) }, "command", "too long"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			p := tc.p()
			tc.mutate(p)
			fe := fieldErrorOf(t, p.Validate(), tc.field)
			if fe.Reason != tc.reason {
				t.Errorf("Validate() reason = %q, want %q", fe.Reason, tc.reason)
			}
		})
	}
}

func TestBackupRun_Validate_NilReceiver(t *testing.T) {
	var p *model.BackupRun
	requireFieldError(t, p.Validate(), "")
}
