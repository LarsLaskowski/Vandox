package model

import (
	"errors"
	"time"
)

// ProcessSnapshot is the process list at one moment.
type ProcessSnapshot struct {
	Complete  bool               `json:"complete"`
	Total     *uint32            `json:"total,omitempty"`
	Processes []ProcessSample    `json:"processes,omitempty"`
	Programs  []ProgramAggregate `json:"programs,omitempty"`
}

// ProcessSample is one process of a ProcessSnapshot.
type ProcessSample struct {
	PID         int32     `json:"pid"`
	PPID        int32     `json:"ppid,omitempty"`
	User        string    `json:"user,omitempty"`
	Command     string    `json:"command"`
	Cmdline     string    `json:"cmdline,omitempty"`
	Truncated   bool      `json:"truncated,omitempty"`
	State       string    `json:"state,omitempty"`
	StartedAt   time.Time `json:"started_at,omitzero"`
	CPUPercent  float64   `json:"cpu_percent"`
	RSSBytes    uint64    `json:"rss_bytes"`
	PSSBytes    *uint64   `json:"pss_bytes,omitempty"`
	SwapBytes   *uint64   `json:"swap_bytes,omitempty"`
	OOMScoreAdj *int16    `json:"oom_score_adj,omitempty"`
}

// ProgramAggregate sums the processes of one program.
type ProgramAggregate struct {
	Program        string  `json:"program"`
	Count          uint32  `json:"count"`
	CPUPercent     float64 `json:"cpu_percent"`
	RSSBytes       uint64  `json:"rss_bytes"`
	PSSBytes       *uint64 `json:"pss_bytes,omitempty"`
	RSSOvercounted bool    `json:"rss_overcounted,omitempty"`
}

// Kind returns KindProcessSnapshot.
func (s *ProcessSnapshot) Kind() Kind { return "" }

// Validate checks the payload.
func (s *ProcessSnapshot) Validate() error { return errors.New("not implemented") }
