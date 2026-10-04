package model

import (
	"errors"
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
func (s *ServiceState) Kind() Kind { return "" }

// Validate checks the payload.
func (s *ServiceState) Validate() error { return errors.New("not implemented") }
