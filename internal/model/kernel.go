package model

import (
	"errors"
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
func (e *KernelEvent) Kind() Kind { return "" }

// Validate checks the payload.
func (e *KernelEvent) Validate() error { return errors.New("not implemented") }
