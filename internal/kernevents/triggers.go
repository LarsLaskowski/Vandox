package kernevents

import (
	"context"
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

// knows reports whether a kill was handed out before or is waiting for its turn.
func (t *triggerState) knows(ref model.KillRef) bool {
	return slices.Contains(t.seen, ref)
}

// remember keeps a kill; the last 64 kills are remembered.
func (t *triggerState) remember(ref model.KillRef) {
	if slices.Contains(t.seen, ref) {
		return
	}
	if len(t.seen) >= model.MaxTriggerKills {
		t.seen = append(t.seen[:0], t.seen[1:]...)
	}
	t.seen = append(t.seen, ref)
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
// A run whose context ended queues and sends nothing, and its kills stay unremembered so that a re-read can trigger them.
func (k *Kernel) requestSnapshots(ctx context.Context, start time.Time, kills []model.KillRef) {
	if k.trigger == nil || ctx.Err() != nil {
		return
	}
	var fresh []model.KillRef
	for _, ref := range kills {
		if k.trig.knows(ref) {
			continue
		}
		k.trig.remember(ref)
		fresh = append(fresh, ref)
	}
	k.trig.add(fresh)
	nothing := len(k.trig.waiting) == 0 && k.trig.omitted == 0
	if nothing || !k.trig.due(start) {
		return
	}
	request := k.trig.take(start)
	for _, collector := range []string{procstats.Name, connstats.Name} {
		k.trigger(collector, scheduler.Trigger{Kills: slices.Clone(request.Kills), KillsOmitted: request.KillsOmitted})
	}
}
