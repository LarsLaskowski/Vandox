package model

import (
	"time"
)

// BackupPhase names the phase of a BackupRun record.
type BackupPhase string

// Backup phases.
const (
	BackupPhaseStart BackupPhase = "start"
	BackupPhaseEnd   BackupPhase = "end"
)

// Validation messages of the phase-specific fields of a BackupRun.
const (
	msgNotAllowedForStart = "not allowed for phase start"
	msgNotAllowedForEnd   = "not allowed for phase end"
)

// BackupRun is the start or the end of a backup run.
type BackupRun struct {
	Phase        BackupPhase `json:"phase"`
	BootID       string      `json:"boot_id"`
	StartedAt    time.Time   `json:"started_at"`
	PID          int32       `json:"pid,omitempty"`
	Command      string      `json:"command,omitempty"`
	Processes    uint32      `json:"processes,omitempty"`
	LastSeenAt   time.Time   `json:"last_seen_at,omitzero"`
	GoneBy       time.Time   `json:"gone_by,omitzero"`
	MaxProcesses uint32      `json:"max_processes,omitempty"`
}

// Kind returns KindBackupRun.
func (b *BackupRun) Kind() Kind { return KindBackupRun }

// Validate checks the payload.
func (b *BackupRun) Validate() error {
	if b == nil {
		return nilReceiver()
	}
	if err := checkOneOf("phase", b.Phase, BackupPhaseStart, BackupPhaseEnd); err != nil {
		return err
	}
	if err := checkUUID("boot_id", b.BootID); err != nil {
		return err
	}
	if err := checkTime("started_at", b.StartedAt); err != nil {
		return err
	}
	if b.Phase == BackupPhaseStart {
		return b.validateStart()
	}
	return b.validateEnd()
}

func (b *BackupRun) validateStart() error {
	if !b.LastSeenAt.IsZero() {
		return invalid("last_seen_at", "not allowed for phase start")
	}
	if !b.GoneBy.IsZero() {
		return invalid("gone_by", "not allowed for phase start")
	}
	if b.MaxProcesses != 0 {
		return invalid("max_processes", "not allowed for phase start")
	}
	if b.PID <= 0 {
		return invalid("pid", "must be greater than 0")
	}
	if err := checkRequiredShort("command", b.Command); err != nil {
		return err
	}
	if b.Processes < 1 {
		return invalid("processes", "must be at least 1")
	}
	return nil
}

func (b *BackupRun) validateEnd() error {
	if b.PID != 0 {
		return invalid("pid", "not allowed for phase end")
	}
	if b.Command != "" {
		return invalid("command", "not allowed for phase end")
	}
	if b.Processes != 0 {
		return invalid("processes", "not allowed for phase end")
	}
	if err := checkTime("last_seen_at", b.LastSeenAt); err != nil {
		return err
	}
	if b.LastSeenAt.Before(b.StartedAt) {
		return invalid("last_seen_at", "must not be before started_at")
	}
	if err := checkTime("gone_by", b.GoneBy); err != nil {
		return err
	}
	if b.GoneBy.Before(b.LastSeenAt) {
		return invalid("gone_by", "must not be before last_seen_at")
	}
	if b.MaxProcesses < 1 {
		return invalid("max_processes", "must be at least 1")
	}
	return nil
}
