// Package kernevents collects the kernel events of the monitored server from the systemd journal files: OOM kills,
// boots and the gaps in what the journal still holds.
package kernevents

import (
	"context"
	"io/fs"
	"log/slog"
	"os"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Name is the collector name and the source of every record.
const Name = "kernel"

// maxRereads caps the re-reads of a journal position before the position is committed.
const maxRereads = 3

const (
	msgNotReadable   = "kernel messages not readable"
	msgReadableAgain = "kernel messages readable again"
)

// Sources are the file trees the collector reads.
type Sources struct {
	Journal []fs.FS // default os.DirFS("/var/log/journal"), os.DirFS("/run/log/journal")
	Etc     fs.FS   // default os.DirFS("/etc")
	Proc    fs.FS   // default os.DirFS("/proc")
}

// TriggerFunc requests an extra run of the named collector and reports whether the request was accepted.
type TriggerFunc func(collector string, t scheduler.Trigger) bool

// failKey names the position where reading a file keeps failing.
type failKey struct {
	name string
	id   [16]byte
	seq  uint64
}

// failure counts the runs in a row that failed at one position.
type failure struct {
	key failKey
	n   int
}

// Kernel is the kernel event collector.
type Kernel struct {
	src     Sources
	store   *agentstate.Store
	log     *slog.Logger
	trigger TriggerFunc

	inited    bool
	committed readState
	mem       readState
	memKills  bool
	rereads   int
	span      *readState
	boot      *model.Record
	curBoot   [16]byte
	curBootOK bool
	trig      triggerState
	fail      failure
	unread    bool
	cache     cursorCache
}

// New returns a Kernel reading src and keeping its position in store.
func New(src Sources, store *agentstate.Store, log *slog.Logger) *Kernel {
	if src.Journal == nil {
		src.Journal = []fs.FS{os.DirFS("/var/log/journal"), os.DirFS("/run/log/journal")}
	}
	if src.Etc == nil {
		src.Etc = os.DirFS("/etc")
	}
	if src.Proc == nil {
		src.Proc = os.DirFS("/proc")
	}
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

// Collect reads the new journal entries and returns the kernel event records. It never returns an error: a journal that cannot be
// read is logged once and, where something is lost, a gap.
func (k *Kernel) Collect(ctx context.Context, sr scheduler.Run) ([]model.Record, error) {
	st, giveUp := k.prepare(sr)
	p := &pass{k: k, start: sr.Start, begin: time.Now(), cur: st, startPos: st.pos}
	if k.boot != nil {
		p.emit(*k.boot, time.Time{})
	}
	if giveUp {
		p.giveUpSpan()
	}
	p.readJournal(ctx)
	return k.finish(ctx, p), nil
}

// prepare commits or drops what the previous run reached and returns the state this run starts from; giveUp tells that the run
// starts after a span it gives up.
func (k *Kernel) prepare(sr scheduler.Run) (readState, bool) {
	switch {
	case !k.inited:
		k.inited = true
		k.committed = readState{pos: k.savedPosition()}
		k.mem = k.committed
		k.boot = k.bootCheck(sr.Start)
	case !sr.PreviousLost:
		k.commit()
	case k.span == nil:
		k.dropped()
	}
	if k.span != nil {
		return k.span.fork(), true
	}
	return k.committed.fork(), false
}

// savedPosition reads the journal position from the saved state.
func (k *Kernel) savedPosition() place {
	if k.store == nil {
		return place{}
	}
	return positionOf(k.store.State().Journal)
}

// commit makes what the previous run reached the committed state: the run's result was kept.
func (k *Kernel) commit() {
	pos := k.mem.pos
	if k.store != nil && pos.valid && !pos.same(k.committed.pos) {
		k.store.Update(k.memKills, func(s *agentstate.State) { s.Journal = pos.journalPosition() })
	}
	k.committed = k.mem
	k.rereads, k.span, k.boot = 0, nil, nil
}

// dropped counts a run whose result was dropped. A run that read nothing is not counted. After maxRereads re-reads the span from
// the committed position to where the last dropped run got is given up.
func (k *Kernel) dropped() {
	if !k.mem.pos.same(k.committed.pos) {
		k.rereads++
	}
	if k.rereads > maxRereads {
		span := k.mem
		k.span = &span
	}
}

// giveUpSpan records the span that is given up as a gap: from the committed position to the last readable entry the dropped run read.
func (p *pass) giveUpSpan() {
	from, to := p.k.committed.pos.rt, p.k.span.spanEnd
	if from.IsZero() {
		from = p.start
	}
	if to.IsZero() {
		to = p.start
	}
	p.emitGap(from, to)
}

// finish ends the run: the state it reached waits for the next run to commit, the log follows its outcome, the extra snapshots are
// requested and every record is checked.
func (k *Kernel) finish(ctx context.Context, p *pass) []model.Record {
	p.closeGap(p.start)
	k.mem, k.memKills = p.cur, p.kills > 0
	if !p.failed {
		k.fail = failure{}
	}
	k.logOutcome(p.problem)
	k.requestSnapshots(ctx, p.start, p.live)
	return checkRecords(p.records, p.start, func(i int) time.Time { return p.froms[i] })
}

// logOutcome logs a change between a readable and an unreadable journal, once.
func (k *Kernel) logOutcome(problem string) {
	switch {
	case problem != "" && !k.unread:
		k.log.Warn(msgNotReadable, "err", problem)
		k.unread = true
	case problem == "" && k.unread:
		k.log.Info(msgReadableAgain)
		k.unread = false
	}
}
