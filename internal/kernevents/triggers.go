package kernevents

import (
	"slices"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/connstats"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

const (
	// triggerEvery is the least time between two pairs of extra snapshot runs.
	triggerEvery = 15 * time.Second
	// liveWithin is how long after a kill the snapshots are still taken.
	liveWithin = 60 * time.Second
)

// triggerState remembers which kills were handed to the snapshot collectors and which wait for their turn. It is not part of the
// reading state: a run that is read again must not trigger its kills a second time.
type triggerState struct {
	seen    []model.KillRef
	waiting []model.KillRef
	omitted uint32
	last    time.Time
	sent    bool
}

// isNew remembers a kill and reports whether it was not remembered before. The last 64 kills are remembered.
func (t *triggerState) isNew(ref model.KillRef) bool {
	if slices.Contains(t.seen, ref) {
		return false
	}
	if len(t.seen) >= model.MaxTriggerKills {
		t.seen = append(t.seen[:0], t.seen[1:]...)
	}
	t.seen = append(t.seen, ref)
	return true
}

// add queues kills, oldest first, up to the most a request names; the rest is counted.
func (t *triggerState) add(kills []model.KillRef) {
	room := max(model.MaxTriggerKills-len(t.waiting), 0)
	take := min(room, len(kills))
	t.waiting = append(t.waiting, kills[:take]...)
	t.omitted += uint32(len(kills) - take) // #nosec G115 -- a slice length fits
}

// due reports whether a request may be sent at start: nothing was sent less than triggerEvery before.
func (t *triggerState) due(start time.Time) bool {
	return !t.sent || start.Sub(t.last) >= triggerEvery
}

// take returns the queued request and empties the queue.
func (t *triggerState) take(start time.Time) scheduler.Trigger {
	out := scheduler.Trigger{Kills: slices.Clone(t.waiting), KillsOmitted: t.omitted}
	t.waiting, t.omitted = t.waiting[:0], 0
	t.last, t.sent = start, true
	return out
}

// requestSnapshots queues the live kills of a run and, when the 15 seconds since the last request have passed, asks the process and
// the connection collector for one extra full snapshot naming all queued kills.
func (k *Kernel) requestSnapshots(start time.Time, kills []model.KillRef) {
	if k.trigger == nil {
		return
	}
	k.trig.add(kills)
	nothing := len(k.trig.waiting) == 0 && k.trig.omitted == 0
	if nothing || !k.trig.due(start) {
		return
	}
	request := k.trig.take(start)
	for _, collector := range []string{procstats.Name, connstats.Name} {
		k.trigger(collector, scheduler.Trigger{Kills: slices.Clone(request.Kills), KillsOmitted: request.KillsOmitted})
	}
}
