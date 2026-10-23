package kernevents

import (
	"io/fs"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
)

const (
	msgBootUnreadable = "boot ID not readable"
)

// bootCheck reads the current boot ID once and returns the boot event when the boot differs from the saved one. The saved boot is
// then replaced by the current one; with an unreadable boot ID it is left alone.
func (k *Kernel) bootCheck(start time.Time) *model.Record {
	raw, ok := readBootID(k.src.Proc)
	if !ok {
		k.log.Warn(msgBootUnreadable)
		return nil
	}
	id, _ := parseUUID(raw)
	k.curBoot, k.curBootOK = id, true

	var saved *agentstate.Boot
	if k.store != nil {
		saved = k.store.State().Boot
	}
	if saved != nil && saved.ID == raw {
		return nil
	}

	bootedAt, err := procstats.ReadBootTime(k.src.Proc)
	if err != nil {
		bootedAt = time.Time{}
	}
	event := &model.Boot{BootID: raw, BootedAt: bootedAt}
	if saved != nil {
		uptime := saved.Uptime
		event.PreviousBootID, event.PreviousUptime = saved.ID, &uptime
	}
	k.saveBoot(raw, bootedAt)

	captured := bootedAt
	if captured.IsZero() {
		captured = start
	}
	return &model.Record{
		Meta: model.Meta{Source: Name, CapturedAt: captured},
		Data: &model.KernelEvent{Type: model.KernelEventBoot, Boot: event},
	}
}

// saveBoot makes the current boot the saved one.
func (k *Kernel) saveBoot(id string, bootedAt time.Time) {
	if k.store == nil {
		return
	}
	uptime, _ := agentstate.ReadUptime(k.src.Proc)
	k.store.Update(true, func(s *agentstate.State) {
		s.Boot = &agentstate.Boot{ID: id, Uptime: uptime, BootedAt: bootedAt}
	})
}

// readBootID reads the boot ID: a lower-case UUID and at most one line feed.
func readBootID(proc fs.FS) (string, bool) {
	id, err := procstats.ReadBootID(proc)
	return id, err == nil
}
