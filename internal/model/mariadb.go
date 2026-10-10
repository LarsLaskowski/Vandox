package model

import (
	"regexp"
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
func (s *MariaDBStatus) Kind() Kind { return KindMariaDBStatus }

var statusKeyPattern = regexp.MustCompile(`^[A-Za-z][A-Za-z0-9_]*$`)

// Validate checks the payload.
func (s *MariaDBStatus) Validate() error {
	if s == nil {
		return nilReceiver()
	}
	if err := checkOneOf("availability", s.Availability, MariaDBUp, MariaDBDown, MariaDBNotAnswering); err != nil {
		return err
	}
	if s.PingLatency != nil && *s.PingLatency < 0 {
		return invalid("ping_latency_ns", "must not be negative")
	}
	for _, c := range []struct {
		name string
		n    int
	}{{"status", len(s.Status)}, {"variables", len(s.Variables)}, {"threads", len(s.Threads)}} {
		if err := checkCount(c.name, c.n); err != nil {
			return err
		}
	}
	if s.Availability != MariaDBUp && (len(s.Status) > 0 || len(s.Variables) > 0 || len(s.Threads) > 0) {
		return invalid("availability", "status, variables and threads must be empty unless up")
	}
	for _, k := range sortedKeys(s.Status) {
		if err := checkPattern(keyed("status", k), k, statusKeyPattern, MaxNameBytes); err != nil {
			return err
		}
	}
	for _, k := range sortedKeys(s.Variables) {
		path := keyed("variables", k)
		if err := checkPattern(path, k, statusKeyPattern, MaxNameBytes); err != nil {
			return err
		}
		if err := checkShort(path, s.Variables[k]); err != nil {
			return err
		}
	}
	for i := range s.Threads {
		if err := s.Threads[i].validate(indexed("threads", i)); err != nil {
			return err
		}
	}
	return nil
}

func (t *MariaDBThread) validate(path string) error {
	for _, f := range []struct{ name, value string }{
		{"user", t.User}, {"host", t.Host}, {"db", t.DB}, {"command", t.Command}, {"state", t.State},
	} {
		if err := checkShort(path+"."+f.name, f.value); err != nil {
			return err
		}
	}
	return checkText(path+".info", t.Info)
}
