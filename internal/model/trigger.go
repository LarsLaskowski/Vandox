package model

import (
	"errors"
	"time"
)

// MaxTriggerKills is the most kills a SnapshotTrigger lists.
const MaxTriggerKills = 64

// KillRef identifies one out-of-memory kill.
type KillRef struct {
	BootID     string        `json:"boot_id"`
	KernelTime time.Duration `json:"kernel_time_ns"`
	VictimPID  int32         `json:"victim_pid"`
}

// SnapshotTrigger names the kills that caused a snapshot.
type SnapshotTrigger struct {
	Kills        []KillRef `json:"kills"`
	KillsOmitted uint32    `json:"kills_omitted,omitempty"`
}

// validate checks the trigger; path is the field path of the trigger in the record.
func (t *SnapshotTrigger) validate(path string) error {
	return errors.New("not implemented")
}
