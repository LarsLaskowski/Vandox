package model

import (
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
func (s *ProcessSnapshot) Kind() Kind { return KindProcessSnapshot }

// Validate checks the payload.
func (s *ProcessSnapshot) Validate() error {
	if s == nil {
		return nilReceiver()
	}
	if len(s.Processes) == 0 && len(s.Programs) == 0 {
		return invalid("processes", "at least one process or program required")
	}
	if err := checkCount("processes", len(s.Processes)); err != nil {
		return err
	}
	if err := checkCount("programs", len(s.Programs)); err != nil {
		return err
	}
	pids := make(map[int32]struct{}, len(s.Processes))
	for i := range s.Processes {
		p := &s.Processes[i]
		if err := p.validate(indexed("processes", i)); err != nil {
			return err
		}
		if _, dup := pids[p.PID]; dup {
			return invalid(indexed("processes", i)+".pid", "duplicate pid")
		}
		pids[p.PID] = struct{}{}
	}
	names := make(map[string]struct{}, len(s.Programs))
	for i := range s.Programs {
		a := &s.Programs[i]
		if err := a.validate(indexed("programs", i)); err != nil {
			return err
		}
		if _, dup := names[a.Program]; dup {
			return invalid(indexed("programs", i)+".program", "duplicate program")
		}
		names[a.Program] = struct{}{}
	}
	return nil
}

func (p *ProcessSample) validate(path string) error {
	if p.PID <= 0 {
		return invalid(path+".pid", "must be greater than 0")
	}
	if p.PPID < 0 {
		return invalid(path+".ppid", "must not be negative")
	}
	if err := checkShort(path+".user", p.User); err != nil {
		return err
	}
	if err := checkRequiredShort(path+".command", p.Command); err != nil {
		return err
	}
	if err := checkText(path+".cmdline", p.Cmdline); err != nil {
		return err
	}
	if p.State != "" && !isASCIILetter(p.State) {
		return invalid(path+".state", "must be one ASCII letter")
	}
	if err := checkOptionalTime(path+".started_at", p.StartedAt); err != nil {
		return err
	}
	if err := checkRate(path+".cpu_percent", p.CPUPercent); err != nil {
		return err
	}
	return checkOOMScoreAdj(path+".oom_score_adj", p.OOMScoreAdj)
}

func (a *ProgramAggregate) validate(path string) error {
	if err := checkRequiredShort(path+".program", a.Program); err != nil {
		return err
	}
	if a.Count < 1 {
		return invalid(path+".count", "must be at least 1")
	}
	return checkRate(path+".cpu_percent", a.CPUPercent)
}

func isASCIILetter(s string) bool {
	return len(s) == 1 && (s[0] >= 'a' && s[0] <= 'z' || s[0] >= 'A' && s[0] <= 'Z')
}
