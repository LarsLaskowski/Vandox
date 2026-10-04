package model

import (
	"time"
)

// KernelEventType names the type of a KernelEvent.
type KernelEventType string

// Kernel event types.
const (
	KernelEventOOMKill KernelEventType = "oom_kill"
	KernelEventBoot    KernelEventType = "boot"
)

// KernelEvent is an event reported by the kernel.
type KernelEvent struct {
	Type    KernelEventType `json:"type"`
	OOMKill *OOMKill        `json:"oom_kill,omitempty"`
	Boot    *Boot           `json:"boot,omitempty"`
	Message string          `json:"message,omitempty"`
}

// OOMKill describes a process killed by the out-of-memory killer.
type OOMKill struct {
	VictimPID     int32   `json:"victim_pid"`
	VictimCommand string  `json:"victim_command"`
	AnonRSSBytes  *uint64 `json:"anon_rss_bytes,omitempty"`
	OOMScoreAdj   *int16  `json:"oom_score_adj,omitempty"`
}

// Boot describes a system boot.
type Boot struct {
	BootID         string         `json:"boot_id"`
	PreviousBootID string         `json:"previous_boot_id,omitempty"`
	BootedAt       time.Time      `json:"booted_at,omitzero"`
	PreviousUptime *time.Duration `json:"previous_uptime_ns,omitempty"`
}

// Kind returns KindKernelEvent.
func (e *KernelEvent) Kind() Kind { return KindKernelEvent }

// Validate checks the payload.
func (e *KernelEvent) Validate() error {
	if e == nil {
		return nilReceiver()
	}
	if err := checkOneOf("type", e.Type, KernelEventOOMKill, KernelEventBoot); err != nil {
		return err
	}
	switch e.Type {
	case KernelEventOOMKill:
		if e.OOMKill == nil {
			return invalid("oom_kill", "required for type oom_kill")
		}
		if e.Boot != nil {
			return invalid("boot", "not allowed for type oom_kill")
		}
		if err := e.OOMKill.validate(); err != nil {
			return err
		}
	case KernelEventBoot:
		if e.Boot == nil {
			return invalid("boot", "required for type boot")
		}
		if e.OOMKill != nil {
			return invalid("oom_kill", "not allowed for type boot")
		}
		if err := e.Boot.validate(); err != nil {
			return err
		}
	}
	return checkText("message", e.Message)
}

func (o *OOMKill) validate() error {
	if o.VictimPID <= 0 {
		return invalid("oom_kill.victim_pid", "must be greater than 0")
	}
	if err := checkRequiredShort("oom_kill.victim_command", o.VictimCommand); err != nil {
		return err
	}
	return checkOOMScoreAdj("oom_kill.oom_score_adj", o.OOMScoreAdj)
}

func (b *Boot) validate() error {
	if err := checkUUID("boot.boot_id", b.BootID); err != nil {
		return err
	}
	if err := checkOptionalUUID("boot.previous_boot_id", b.PreviousBootID); err != nil {
		return err
	}
	if err := checkOptionalTime("boot.booted_at", b.BootedAt); err != nil {
		return err
	}
	if b.PreviousUptime != nil && *b.PreviousUptime < 0 {
		return invalid("boot.previous_uptime_ns", "must not be negative")
	}
	return nil
}
