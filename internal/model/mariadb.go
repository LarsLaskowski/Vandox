package model

import (
	"errors"
	"time"
)

// MariaDBAvailability tells whether the database answered.
type MariaDBAvailability string

// MariaDB availabilities.
const (
	MariaDBUp           MariaDBAvailability = "up"
	MariaDBDown         MariaDBAvailability = "down"
	MariaDBNotAnswering MariaDBAvailability = "not_answering"
)

// MariaDBStatus is the state of the database at one moment.
type MariaDBStatus struct {
	Availability MariaDBAvailability `json:"availability"`
	PingLatency  *time.Duration      `json:"ping_latency_ns,omitempty"`
	Status       map[string]uint64   `json:"status,omitempty"`
	Variables    map[string]string   `json:"variables,omitempty"`
	Threads      []MariaDBThread     `json:"threads,omitempty"`
	Complete     bool                `json:"complete"`
}

// MariaDBThread is one entry of the database's process list.
type MariaDBThread struct {
	ID          uint64 `json:"id"`
	User        string `json:"user,omitempty"`
	Host        string `json:"host,omitempty"`
	DB          string `json:"db,omitempty"`
	Command     string `json:"command,omitempty"`
	TimeSeconds uint64 `json:"time_seconds"`
	State       string `json:"state,omitempty"`
	Info        string `json:"info,omitempty"`
	Truncated   bool   `json:"truncated,omitempty"`
}

// Kind returns KindMariaDBStatus.
func (s *MariaDBStatus) Kind() Kind { return "" }

// Validate checks the payload.
func (s *MariaDBStatus) Validate() error { return errors.New("not implemented") }
