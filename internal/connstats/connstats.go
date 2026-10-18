// Package connstats collects the socket overview from /proc/net and the descriptors of the processes.
package connstats

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"os"
	"slices"
	"strings"
	"sync"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// Name and Source of the collector.
const (
	Name   = "connections"
	Source = "connections"
)

// Limits of one run.
const (
	TopRemotes      = 25    // remote addresses in a regular snapshot
	MaxSockets      = 65536 // sockets with an inode tracked for owners per run
	MaxRemotes      = 4096  // remote addresses counted per run
	MaxNewListeners = 64    // new listeners reported per run
	PrivilegedPorts = 1024  // ports below are privileged
	EphemeralFirst  = 32768 // first port of the ephemeral range
	EphemeralLast   = 60999 // last port of the ephemeral range
)

// SocketMount is the mount of the kernel's sockets, learned at the agent's start; the zero value is not probed.
type SocketMount struct {
	ID  int32
	Err error
}

// Sources are the places the collector reads; a nil Proc is os.DirFS("/proc").
type Sources struct {
	Proc        fs.FS
	SocketMount SocketMount
}

// payloadBudget is the room the entries of a record may take: the wire format's line limit less a slack for the envelope.
var payloadBudget = wire.DefaultLimits().MaxLineBytes - payloadSlack

// socketTables are the tables the collector reads, in the order of the bits of collection.read.
var socketTables = [...]table{
	{name: "tcp", proto: model.ProtoTCP, words: 1},
	{name: "tcp6", proto: model.ProtoTCP6, words: 4},
	{name: "udp", proto: model.ProtoUDP, words: 1},
	{name: "udp6", proto: model.ProtoUDP6, words: 4},
}

var (
	errNoTable     = errors.New("connstats: no socket table found")
	errMountNotSet = errors.New("connstats: socket mount not learned")
)

// logState is what the collector has logged, so a line is written once per change.
type logState struct {
	missing          uint8
	linesBad         bool
	ownersUnknown    bool
	ownersUnreadable bool
}

// Connections is the connection collector.
type Connections struct {
	src   Sources
	log   *slog.Logger
	mu    sync.Mutex
	prev  *inventory
	state logState
}

// New returns the collector over src; it logs through log.
func New(src Sources, log *slog.Logger) *Connections {
	if src.Proc == nil {
		src.Proc = os.DirFS("/proc")
	}
	if log == nil {
		log = slog.New(slog.DiscardHandler)
	}
	return &Connections{src: src, log: log.With("collector", Name)}
}

// Name returns Name.
func (c *Connections) Name() string {
	return Name
}

// runResult is what a run read besides the tables: the owners of the sockets and the commands of their processes.
type runResult struct {
	commands      map[int32]string
	counts        map[int32]uint32
	unreadable    int
	firstErr      error
	pidCapped     bool
	ownersUnknown error
}

// Collect takes one connection snapshot.
func (c *Connections) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	c.mu.Lock()
	prev := c.prev
	c.mu.Unlock()

	col, err := c.readTables(ctx)
	if err != nil {
		return nil, err
	}
	res, err := c.readOwners(ctx, col)
	if err != nil {
		return nil, err
	}

	cur := col.inventory()
	check, added := compare(prev, cur)
	full := run.Pressure || prev == nil || len(added) > 0
	snap := buildSnapshot(col, res, check, added, full)

	record, err := fitRecord(run.Start, snap)
	if err != nil {
		return nil, err
	}
	if !c.commit(ctx, cur, col, res) {
		return nil, ctx.Err()
	}
	return []model.Record{record}, nil
}

// readTables reads the four tables; a missing table is left out, all four missing fail the run.
func (c *Connections) readTables(ctx context.Context) (*collection, error) {
	col := newCollection()
	for i, t := range socketTables {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		missing, err := readTable(c.src.Proc, t, col.add, func(line int, err error) { col.bad(t.name, line, err) })
		if err != nil {
			return nil, err
		}
		if missing {
			col.missing |= 1 << i
		} else {
			col.read |= 1 << i
		}
	}
	if col.read == 0 {
		return nil, errNoTable
	}
	return col, nil
}

// readOwners finds the owners of the tracked sockets and the commands of their processes, and gives the list entries their owner.
func (c *Connections) readOwners(ctx context.Context, col *collection) (*runResult, error) {
	res := &runResult{commands: map[int32]string{}}
	switch {
	case c.src.SocketMount.Err != nil:
		res.ownersUnknown = c.src.SocketMount.Err
		return res, nil
	case c.src.SocketMount.ID == 0:
		res.ownersUnknown = errMountNotSet
		return res, nil
	case len(col.index) == 0:
		return res, nil
	}
	pids, seen, err := procstats.ListPIDs(c.src.Proc)
	if err != nil {
		return nil, err
	}
	res.pidCapped = seen > len(pids)
	owned, unreadable, firstErr := scanOwners(ctx, c.src.Proc, pids, col.index, c.src.SocketMount.ID)
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	col.index = nil // the descriptors are read; the commands need only the owners
	res.unreadable, res.firstErr, res.counts = unreadable, firstErr, owned.perPID
	c.readCommands(res)
	col.assignOwners(owned.owner, func(pid int32) bool { _, ok := res.commands[pid]; return ok })
	return res, nil
}

// readCommands looks up the command of every process that holds a socket; an ended process is dropped, an unreadable one is
// dropped and counted.
func (c *Connections) readCommands(res *runResult) {
	reader := procstats.NewCommandReader(c.src.Proc)
	pids := make([]int32, 0, len(res.counts))
	for pid := range res.counts {
		pids = append(pids, pid)
	}
	slices.Sort(pids)
	for _, pid := range pids {
		command, err := reader.Command(pid)
		switch {
		case err == nil:
			res.commands[pid] = command
		case procstats.Ended(err):
			delete(res.counts, pid)
		default:
			delete(res.counts, pid)
			res.unreadable++
			if res.firstErr == nil {
				res.firstErr = err
			}
		}
	}
}

// inventory returns the listener inventory of the run.
func (col *collection) inventory() *inventory {
	entries := col.listeners.sorted()
	keys := make([]listenerKey, len(entries))
	for i := range entries {
		keys[i] = entries[i].key
	}
	return &inventory{keys: keys, cut: col.listeners.cut, skipped: col.badLines > 0, tables: col.read}
}

// commit keeps the inventory and writes the log lines of a run, unless the run's context has ended: a run that is late keeps
// nothing, so the next run compares with the last one that finished in time.
func (c *Connections) commit(ctx context.Context, cur *inventory, col *collection, res *runResult) bool {
	c.mu.Lock()
	defer c.mu.Unlock()

	if ctx.Err() != nil {
		return false
	}
	c.prev = cur
	c.logChanges(col, res)
	return true
}

// logChanges writes a line for every state that changed since the last kept run.
func (c *Connections) logChanges(col *collection, res *runResult) {
	st := &c.state
	if col.missing != st.missing {
		if col.missing != 0 {
			c.log.Info("socket tables unavailable", "tables", missingNames(col.missing))
		} else {
			c.log.Info("socket tables available")
		}
		st.missing = col.missing
	}
	if bad := col.badLines > 0; bad != st.linesBad {
		if bad {
			c.log.Warn("socket lines not read", "lines", col.badLines, "err", col.firstBad)
		} else {
			c.log.Info("socket lines read again")
		}
		st.linesBad = bad
	}
	if res.ownersUnknown != nil {
		if !st.ownersUnknown {
			c.log.Warn("socket owners unknown", "err", res.ownersUnknown)
			st.ownersUnknown = true
		}
		return
	}
	if unreadable := res.unreadable > 0; unreadable != st.ownersUnreadable {
		if unreadable {
			c.log.Warn("socket owners partly unknown", "processes", res.unreadable, "err", res.firstErr)
		} else {
			c.log.Info("socket owners known again")
		}
		st.ownersUnreadable = unreadable
	}
}

// missingNames lists the missing tables in table order, comma-separated.
func missingNames(mask uint8) string {
	var names []string
	for i, t := range socketTables {
		if mask&(1<<i) != 0 {
			names = append(names, t.name)
		}
	}
	return strings.Join(names, ",")
}
