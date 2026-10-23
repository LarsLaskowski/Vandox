package model

import (
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
	kills := path + ".kills"
	switch {
	case len(t.Kills) == 0:
		return invalid(kills, "at least one kill required")
	case len(t.Kills) > MaxTriggerKills:
		return invalid(kills, "too many entries")
	}
	seen := make(map[KillRef]struct{}, len(t.Kills))
	for i := range t.Kills {
		k := t.Kills[i]
		if err := k.validate(indexed(kills, i)); err != nil {
			return err
		}
		if _, dup := seen[k]; dup {
			return invalid(indexed(kills, i), "duplicate kill")
		}
		seen[k] = struct{}{}
	}
	return nil
}

func (k *KillRef) validate(path string) error {
	if err := checkUUID(path+".boot_id", k.BootID); err != nil {
		return err
	}
	if k.KernelTime < 0 {
		return invalid(path+".kernel_time_ns", "must not be negative")
	}
	if k.VictimPID <= 0 {
		return invalid(path+".victim_pid", "must be greater than 0")
	}
	return nil
}

// validateTrigger checks an optional trigger under the field name "trigger".
func validateTrigger(t *SnapshotTrigger) error {
	if t == nil {
		return nil
	}
	return t.validate("trigger")
}
