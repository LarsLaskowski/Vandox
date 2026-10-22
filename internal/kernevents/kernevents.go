// Package kernevents collects the kernel events of the monitored server from the systemd journal files: OOM kills,
// boots and the gaps in what the journal still holds.
package kernevents

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Name is the collector name and the source of every record.
const Name = "kernel"

// maxRereads caps the re-reads of a journal position before the position is committed.
const maxRereads = 3

// Sources are the file trees the collector reads.
type Sources struct {
	Journal []fs.FS // default os.DirFS("/var/log/journal"), os.DirFS("/run/log/journal")
	Etc     fs.FS   // default os.DirFS("/etc")
	Proc    fs.FS   // default os.DirFS("/proc")
}

// TriggerFunc requests an extra run of the named collector and reports whether the request was accepted.
type TriggerFunc func(collector string, t scheduler.Trigger) bool

// Kernel is the kernel event collector.
type Kernel struct {
	src     Sources
	store   *agentstate.Store
	log     *slog.Logger
	trigger TriggerFunc
}

// New returns a Kernel reading src and keeping its position in store.
func New(src Sources, store *agentstate.Store, log *slog.Logger) *Kernel {
	return &Kernel{src: src, store: store, log: log}
}

// SetTrigger sets the function that requests the extra snapshot runs after a kill.
func (k *Kernel) SetTrigger(fn TriggerFunc) {
	k.trigger = fn
}

// Name returns Name.
func (k *Kernel) Name() string {
	return Name
}

// Collect reads the new journal entries and returns the kernel event records.
func (k *Kernel) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	return nil, errors.New("not implemented")
}

// parseKill parses the message of an OOM kill line.
func parseKill(message []byte) (model.OOMKill, bool) {
	return model.OOMKill{}, false
}

// newGap returns a gap record for [from, to) detected at at; To is From + 1 µs when to is not after from.
func newGap(from, to, at time.Time) model.Record {
	return model.Record{}
}

// checkRecords replaces every record that validation refuses with a gap; a refused gap becomes [at - 1 µs, at).
func checkRecords(records []model.Record, at time.Time, gapFrom func(i int) time.Time) []model.Record {
	return nil
}

// realtimeOK reports whether us is a usable realtime stamp: 0 < us < 253402300800000000.
func realtimeOK(us uint64) bool {
	return false
}

// parseMicros parses 1-20 ASCII digits of microseconds, overflow checked, at most MaxInt64/1000 µs.
func parseMicros(b []byte) (time.Duration, bool) {
	return 0, false
}
