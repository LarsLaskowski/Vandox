package scheduler

import "github.com/LarsLaskowski/Vandox/internal/model"

// Trigger asks for an extra run of a collector and names the kills that caused it.
type Trigger struct {
	Kills        []model.KillRef // the kills, at most model.MaxTriggerKills
	KillsOmitted uint32          // how many kills beyond the list were merged into the request
}

// Trigger requests an extra run of the named collector and reports whether the request was accepted.
func (s *Scheduler) Trigger(collector string, t Trigger) bool {
	return false
}
