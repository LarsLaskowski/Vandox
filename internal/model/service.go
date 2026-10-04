package model

import (
	"regexp"
	"time"
)

// ServiceState is the state of one systemd unit.
type ServiceState struct {
	Unit          string    `json:"unit"`
	LoadState     string    `json:"load_state"`
	ActiveState   string    `json:"active_state"`
	SubState      string    `json:"sub_state,omitempty"`
	ActiveEnterAt time.Time `json:"active_enter_at,omitzero"`
	Restarts      uint32    `json:"restarts"`
}

// Kind returns KindServiceState.
func (s *ServiceState) Kind() Kind { return KindServiceState }

var (
	unitPattern     = regexp.MustCompile(`^[A-Za-z0-9:_.@\-]+$`)
	subStatePattern = regexp.MustCompile(`^[a-z0-9-]+$`)
)

// Validate checks the payload.
func (s *ServiceState) Validate() error {
	if s == nil {
		return nilReceiver()
	}
	if err := checkPattern("unit", s.Unit, unitPattern, 256); err != nil {
		return err
	}
	if err := checkOneOf("load_state", s.LoadState, "loaded", "not-found", "bad-setting", "error", "masked",
		"stub", "merged"); err != nil {
		return err
	}
	if err := checkOneOf("active_state", s.ActiveState, "active", "reloading", "inactive", "failed",
		"activating", "deactivating", "maintenance", "refreshing"); err != nil {
		return err
	}
	if s.SubState != "" {
		if err := checkPattern("sub_state", s.SubState, subStatePattern, 64); err != nil {
			return err
		}
	}
	return checkOptionalTime("active_enter_at", s.ActiveEnterAt)
}
