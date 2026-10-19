package connstats

import (
	"cmp"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"net/netip"
	"slices"
	"strings"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// Bounds of the snapshot.
const (
	maxEntries    = model.MaxItems // entries of a list, the wire format's limit
	payloadSlack  = 8 << 10        // bytes of the line kept free for the envelope and the field names
	maxHalvings   = 64             // rounds of the safety net that shortens a record
	kindNone      = 0              // a tracked socket that no list shows
	kindListener  = 1              // a tracked socket that belongs to an inventory entry
	kindConnected = 2              // a tracked socket that belongs to a connection entry
)

// lists are the ordered lists of a run before they are cut to the payload budget.
type lists struct {
	states       []model.StateCount
	newListeners []model.Listener
	omitted      uint32
	listeners    []model.Listener
	processes    []model.ProcessConnections
	remotes      []model.RemoteCount
	connections  []model.Connection
}

// filler is the progress of fill: the bytes left and whether a list has been cut.
type filler struct {
	left          int
	stopped       bool
	cut           bool
	cutAggregates bool
}

// fill fills snap entry by entry within budget bytes and reports whether aggregates or any list were cut.
//
// The lists are filled in the order states, new listeners, listeners, processes, remotes, connections; at the first entry that
// does not fit, that list and every later one stop.
func fill(snap *model.ConnectionSnapshot, lists *lists, budget int) (cutAggregates, cut bool) {
	f := &filler{left: budget}
	fillList(f, &snap.States, lists.states, true)
	dropped := fillList(f, &snap.NewListeners, lists.newListeners, false)
	snap.NewListenersOmitted = lists.omitted + uint32(min(max(dropped, 0), math.MaxUint32))
	fillList(f, &snap.Listeners, lists.listeners, true)
	fillList(f, &snap.Processes, lists.processes, true)
	fillList(f, &snap.Remotes, lists.remotes, false)
	fillList(f, &snap.Connections, lists.connections, false)
	return f.cutAggregates, f.cut
}

// fillList appends the entries of src to dst while they fit and returns how many were left out.
func fillList[T any](f *filler, dst *[]T, src []T, aggregate bool) int {
	if len(src) == 0 {
		return 0
	}
	dropped := len(src)
	if !f.stopped {
		out := make([]T, 0, len(src))
		for _, e := range src {
			size, ok := entrySize(e)
			if !ok || size > f.left {
				break
			}
			f.left -= size
			out = append(out, e)
		}
		dropped = len(src) - len(out)
		if len(out) > 0 {
			*dst = out
		}
	}
	if dropped > 0 {
		f.stopped = true
		f.cut = true
		f.cutAggregates = f.cutAggregates || aggregate
	}
	return dropped
}

// entrySize returns the bytes an entry adds to the encoded line: its own encoding and a separator.
func entrySize(e any) (int, bool) {
	b, err := json.Marshal(e)
	if err != nil {
		return 0, false
	}
	return len(b) + 1, true
}

// fitRecord returns the record of snap, shortened until it fits the wire limit, carrying the inventory state of inv.
func fitRecord(start time.Time, snap *model.ConnectionSnapshot, inv *inventory) (model.Record, error) {
	record := model.Record{Meta: model.Meta{Source: Source, CapturedAt: start.UTC()}, Data: snap}
	for round := 0; ; round++ {
		snap.Inventory = inventoryState(inv, len(snap.Listeners))
		checked := record
		checked.Origin, checked.Seq = model.OriginAgent, 1
		_, err := wire.CheckRecord(&checked)
		var tooLong *wire.RecordSizeError
		switch {
		case err == nil:
			return record, nil
		case !errors.As(err, &tooLong) || round >= maxHalvings || !shorten(snap):
			return model.Record{}, fmt.Errorf("connstats: record refused: %w", err)
		}
		snap.Complete = false
	}
}

// shorten halves the first list that holds more than one entry, connections first, and reports whether there was one.
func shorten(snap *model.ConnectionSnapshot) bool {
	switch {
	case halve(&snap.Connections):
	case halve(&snap.Remotes):
	case halve(&snap.Processes):
		snap.AggregatesIncomplete = true
	case halve(&snap.Listeners):
		snap.AggregatesIncomplete = true
	default:
		return false
	}
	return true
}

// halve keeps the first half of s and reports whether s held more than one entry.
func halve[T any](s *[]T) bool {
	if len(*s) < 2 {
		return false
	}
	*s = (*s)[:len(*s)/2]
	return true
}

// stateKey identifies the sockets counted together: a protocol and, for TCP, a state.
type stateKey struct {
	proto model.Proto
	state string
}

// slotRef names the list entry a tracked socket belongs to; gen tells whether the entry still is the one the socket joined.
type slotRef struct {
	kind uint8
	id   int32
	gen  uint32
}

// listenerEntry is an inventory entry: one listener endpoint and its owner (0: none).
type listenerEntry struct {
	key   listenerKey
	owner int32
}

// connEntry is a connection of the full snapshot and its owner (0: none).
type connEntry struct {
	proto         model.Proto
	local, remote netip.AddrPort
	state         string
	owner         int32
}

// lessConn orders connections by protocol, local endpoint, remote endpoint.
func lessConn(a, b connEntry) bool {
	if c := strings.Compare(string(a.proto), string(b.proto)); c != 0 {
		return c < 0
	}
	if c := a.local.Compare(b.local); c != 0 {
		return c < 0
	}
	return a.remote.Compare(b.remote) < 0
}

// topN keeps the limit lowest entries of a stream. Entries live in a pool and keep their ID until they are replaced, which
// bumps the ID's generation, so a reference made earlier can tell that its entry is gone.
type topN[T any] struct {
	items []T
	gens  []uint32
	heap  []int32 // IDs, a max-heap by less
	limit int
	less  func(a, b T) bool
	cut   bool // an entry was refused or replaced
}

// admits reports whether v would be kept.
func (h *topN[T]) admits(v T) bool {
	return len(h.heap) < h.limit || h.less(v, h.items[h.heap[0]])
}

// worst returns the entry that put replaces when the heap is full, and false when the heap has room.
func (h *topN[T]) worst() (*T, bool) {
	if len(h.heap) < h.limit {
		return nil, false
	}
	return &h.items[h.heap[0]], true
}

// put keeps v, which must be admitted, and returns its ID and generation.
func (h *topN[T]) put(v T) (id int32, gen uint32) {
	if len(h.heap) < h.limit {
		id = int32(min(len(h.items), math.MaxInt32))
		h.items = append(h.items, v)
		h.gens = append(h.gens, 1)
		h.heap = append(h.heap, id)
		h.siftUp(len(h.heap) - 1)
		return id, 1
	}
	h.cut = true
	id = h.heap[0]
	h.items[id] = v
	h.gens[id]++
	h.siftDown(0)
	return id, h.gens[id]
}

// valid reports whether the entry id still is the one generation gen refers to.
func (h *topN[T]) valid(id int32, gen uint32) bool {
	return id >= 0 && int(id) < len(h.items) && h.gens[id] == gen
}

func (h *topN[T]) siftUp(i int) {
	for i > 0 {
		p := (i - 1) / 2
		if !h.less(h.items[h.heap[p]], h.items[h.heap[i]]) {
			return
		}
		h.heap[p], h.heap[i] = h.heap[i], h.heap[p]
		i = p
	}
}

func (h *topN[T]) siftDown(i int) {
	n := len(h.heap)
	for {
		l := 2*i + 1
		if l >= n {
			return
		}
		big := l
		if r := l + 1; r < n && h.less(h.items[h.heap[l]], h.items[h.heap[r]]) {
			big = r
		}
		if !h.less(h.items[h.heap[i]], h.items[h.heap[big]]) {
			return
		}
		h.heap[i], h.heap[big] = h.heap[big], h.heap[i]
		i = big
	}
}

// sorted returns the kept entries in ascending order, as copies.
func (h *topN[T]) sorted() []T {
	out := slices.Clone(h.items)
	slices.SortFunc(out, func(a, b T) int {
		switch {
		case h.less(a, b):
			return -1
		case h.less(b, a):
			return 1
		}
		return 0
	})
	return out
}

// collection is what one run learns from the socket tables.
type collection struct {
	states     map[stateKey]uint32
	index      map[uint64]int32 // inode to slot, for the sockets that can have an owner
	refs       []slotRef        // per slot, the list entry of the socket
	listeners  topN[listenerEntry]
	endpoints  map[listenerKey]int32 // listener endpoint to its entry ID
	conns      topN[connEntry]
	remotes    map[netip.Addr]uint32
	remotesCut bool
	untracked  bool // a socket got no slot (inode cap, repeated inode)
	read       uint8
	missing    uint8
	badLines   int
	firstBad   error
}

// newCollection returns an empty collection.
func newCollection() *collection {
	return &collection{
		states:    make(map[stateKey]uint32),
		index:     make(map[uint64]int32),
		endpoints: make(map[listenerKey]int32),
		remotes:   make(map[netip.Addr]uint32),
		listeners: topN[listenerEntry]{limit: maxEntries, less: func(a, b listenerEntry) bool { return lessListener(a.key, b.key) }},
		conns:     topN[connEntry]{limit: maxEntries, less: lessConn},
	}
}

// bad records an unreadable line of a table.
func (c *collection) bad(name string, line int, err error) {
	c.badLines++
	if c.firstBad == nil {
		c.firstBad = fmt.Errorf("connstats: net/%s: line %d: %w", name, line, err)
	}
}

// add counts one socket and files it as a listener or a connection.
func (c *collection) add(s socket) {
	c.states[stateKey{s.proto, s.state}]++
	var ref slotRef
	switch {
	case isListener(&s):
		ref = c.addListener(&s)
	case isConnection(&s):
		ref = c.addConnection(&s)
	}
	c.track(s.inode, ref)
}

// isListener reports whether s is a TCP socket in state LISTEN or a UDP socket without a remote endpoint.
func isListener(s *socket) bool {
	if s.proto == model.ProtoTCP || s.proto == model.ProtoTCP6 {
		return s.state == "LISTEN"
	}
	return hasNoRemote(s)
}

// isConnection reports whether s has a remote endpoint and is no listener.
func isConnection(s *socket) bool {
	return !hasNoRemote(s)
}

// hasNoRemote reports whether the remote address is all zeros and the port 0.
func hasNoRemote(s *socket) bool {
	return s.remote.Port() == 0 && s.remote.Addr().IsUnspecified()
}

// addListener files a listener; sockets on one endpoint share an entry.
func (c *collection) addListener(s *socket) slotRef {
	key := listenerKey{proto: s.proto, addr: s.local.Addr(), port: s.local.Port()}
	if id, ok := c.endpoints[key]; ok {
		return slotRef{kind: kindListener, id: id, gen: c.listeners.gens[id]}
	}
	entry := listenerEntry{key: key}
	if !c.listeners.admits(entry) {
		c.listeners.cut = true
		return slotRef{}
	}
	if worst, full := c.listeners.worst(); full {
		delete(c.endpoints, worst.key)
	}
	id, gen := c.listeners.put(entry)
	c.endpoints[key] = id
	return slotRef{kind: kindListener, id: id, gen: gen}
}

// addConnection files a connection and counts its remote address.
func (c *collection) addConnection(s *socket) slotRef {
	c.countRemote(s.remote.Addr())
	entry := connEntry{proto: s.proto, local: s.local, remote: s.remote, state: s.state}
	if !c.conns.admits(entry) {
		c.conns.cut = true
		return slotRef{}
	}
	id, gen := c.conns.put(entry)
	return slotRef{kind: kindConnected, id: id, gen: gen}
}

// countRemote counts a connection to addr; addresses beyond MaxRemotes are not counted.
func (c *collection) countRemote(addr netip.Addr) {
	if n, ok := c.remotes[addr]; ok {
		c.remotes[addr] = n + 1
		return
	}
	if len(c.remotes) >= MaxRemotes {
		c.remotesCut = true
		return
	}
	c.remotes[addr] = 1
}

// track gives a socket with an inode a slot, so a descriptor can name it; inode 0 never matches a descriptor.
func (c *collection) track(inode uint64, ref slotRef) {
	if inode == 0 {
		return
	}
	if _, repeated := c.index[inode]; repeated || len(c.index) >= MaxSockets {
		c.untracked = true
		return
	}
	c.index[inode] = int32(min(len(c.refs), math.MaxInt32))
	c.refs = append(c.refs, ref)
}

// assignOwners gives every list entry the lowest PID among the holders of its sockets that is in known.
func (c *collection) assignOwners(owner []int32, known func(pid int32) bool) {
	for slot, ref := range c.refs {
		pid := owner[slot]
		if pid == 0 || !known(pid) {
			continue
		}
		switch {
		case ref.kind == kindListener && c.listeners.valid(ref.id, ref.gen):
			e := &c.listeners.items[ref.id]
			e.owner = lowest(e.owner, pid)
		case ref.kind == kindConnected && c.conns.valid(ref.id, ref.gen):
			e := &c.conns.items[ref.id]
			e.owner = lowest(e.owner, pid)
		}
	}
}

// lowest returns the smaller PID, 0 meaning none.
func lowest(a, b int32) int32 {
	if a == 0 {
		return b
	}
	return min(a, b)
}

// stateList returns the state counts by protocol, then state.
func (c *collection) stateList() []model.StateCount {
	out := make([]model.StateCount, 0, len(c.states))
	for k, n := range c.states {
		out = append(out, model.StateCount{Proto: k.proto, State: k.state, Count: n})
	}
	slices.SortFunc(out, func(a, b model.StateCount) int {
		return cmp.Or(strings.Compare(string(a.Proto), string(b.Proto)), strings.Compare(a.State, b.State))
	})
	return out
}

// remoteList returns the counted remote addresses by count descending, then address.
func (c *collection) remoteList() []model.RemoteCount {
	out := make([]model.RemoteCount, 0, len(c.remotes))
	for a, n := range c.remotes {
		out = append(out, model.RemoteCount{Addr: a, Count: n})
	}
	slices.SortFunc(out, func(a, b model.RemoteCount) int {
		return cmp.Or(cmp.Compare(b.Count, a.Count), a.Addr.Compare(b.Addr))
	})
	return out
}

// buildSnapshot assembles the snapshot of a run and reports whether its aggregates (states, listeners, processes) are incomplete.
func buildSnapshot(col *collection, res *runResult, check model.ListenerCheck, added []listenerKey, full bool) *model.ConnectionSnapshot {
	var l lists
	l.states = col.stateList()
	l.listeners = col.listenerList(res.commands)
	l.processes = processList(res)
	l.remotes = col.remoteList()
	if full {
		l.connections = col.connectionList(res.commands)
	} else {
		l.remotes = l.remotes[:min(TopRemotes, len(l.remotes))]
	}
	reported := added[:min(MaxNewListeners, len(added))]
	for _, key := range reported {
		if id, ok := col.endpoints[key]; ok {
			l.newListeners = append(l.newListeners, listenerOf(&col.listeners.items[id], res.commands))
		}
	}
	l.omitted = uint32(min(max(len(added)-len(reported), 0), math.MaxUint32))

	snap := &model.ConnectionSnapshot{AggregatesOnly: !full, ListenerCheck: check, Tables: tableList(col.read)}
	cutAggregates, cut := fill(snap, &l, payloadBudget)
	snap.AggregatesIncomplete = cutAggregates || col.badLines > 0 || col.untracked || col.listeners.cut || res.pidCapped ||
		res.unreadable > 0 || res.ownersUnknown != nil
	snap.Complete = full && !snap.AggregatesIncomplete && !cut && !col.conns.cut && !col.remotesCut
	return snap
}

// listenerOf returns the listener entry with its owner's command.
func listenerOf(e *listenerEntry, commands map[int32]string) model.Listener {
	l := model.Listener{Proto: e.key.proto, Local: netip.AddrPortFrom(e.key.addr, e.key.port)}
	if e.owner != 0 {
		l.PID, l.Command = e.owner, commands[e.owner]
	}
	return l
}

// listenerList returns the inventory in inventory order.
func (c *collection) listenerList(commands map[int32]string) []model.Listener {
	entries := c.listeners.sorted()
	out := make([]model.Listener, len(entries))
	for i := range entries {
		out[i] = listenerOf(&entries[i], commands)
	}
	return out
}

// connectionList returns the kept connections by protocol, local endpoint, remote endpoint.
func (c *collection) connectionList(commands map[int32]string) []model.Connection {
	entries := c.conns.sorted()
	out := make([]model.Connection, len(entries))
	for i := range entries {
		e := &entries[i]
		out[i] = model.Connection{Proto: e.proto, Local: e.local, Remote: e.remote, State: e.state}
		if e.owner != 0 {
			out[i].PID, out[i].Command = e.owner, commands[e.owner]
		}
	}
	return out
}

// processList returns the processes that hold sockets by count descending, then PID.
func processList(res *runResult) []model.ProcessConnections {
	out := make([]model.ProcessConnections, 0, len(res.counts))
	for pid, n := range res.counts {
		out = append(out, model.ProcessConnections{PID: pid, Command: res.commands[pid], Count: n})
	}
	slices.SortFunc(out, func(a, b model.ProcessConnections) int {
		return cmp.Or(cmp.Compare(b.Count, a.Count), cmp.Compare(a.PID, b.PID))
	})
	return out
}
