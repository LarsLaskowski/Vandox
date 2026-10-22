package model

import (
	"errors"
	"time"
)

// BackupPhase names the phase of a BackupRun record.
type BackupPhase string

// Backup phases.
const (
	BackupPhaseStart BackupPhase = "start"
	BackupPhaseEnd   BackupPhase = "end"
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
	return errors.New("not implemented")
}
