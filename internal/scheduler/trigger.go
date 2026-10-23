package scheduler

import "github.com/LarsLaskowski/Vandox/internal/model"

// Trigger asks for an extra run of a collector and names the kills that caused it.
type Trigger struct {
	Kills        []model.KillRef // the kills, at most model.MaxTriggerKills
	KillsOmitted uint32          // how many kills beyond the list were merged into the request
}

// Trigger requests an extra run of the named collector and reports whether the request was accepted; it is false only
// for an unknown collector. Requests made before the extra run starts merge into one: the first model.MaxTriggerKills
// kills are listed, the rest is counted in KillsOmitted. A busy runner starts the extra run when its current run ends.
func (s *Scheduler) Trigger(collector string, t Trigger) bool {
	for _, r := range s.runners {
		if r.name == collector {
			r.request(t)
			return true
		}
	}
	return false
}

// request merges t into the waiting trigger and wakes the runner.
func (r *runner) request(t Trigger) {
	r.trigMu.Lock()
	if r.trig == nil {
		r.trig = &Trigger{}
	}
	merged := r.trig
	room := max(model.MaxTriggerKills-len(merged.Kills), 0)
	take := min(room, len(t.Kills))
	merged.Kills = append(merged.Kills, t.Kills[:take]...)
	merged.KillsOmitted = addCount(merged.KillsOmitted, uint32(len(t.Kills)-take)) // #nosec G115 -- a slice length fits
	merged.KillsOmitted = addCount(merged.KillsOmitted, t.KillsOmitted)
	r.trigMu.Unlock()

	select {
	case r.trigWake <- struct{}{}:
	default:
	}
}

// addCount returns a+b, capped at the largest uint32.
func addCount(a, b uint32) uint32 {
	if sum := uint64(a) + uint64(b); sum < 1<<32 {
		return uint32(sum)
	}
	return 1<<32 - 1
}
