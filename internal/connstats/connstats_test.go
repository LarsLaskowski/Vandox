package connstats

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"io/fs"
	"log/slog"
	"net/netip"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"slices"
	"strconv"
	"strings"
	"syscall"
	"testing"
	"testing/fstest"
	"testing/synctest"
	"time"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	sshdConnection = "198.51.100.7:25249"
	newBackdoor    = "0.0.0.0:4444"
)

// What the owned world's runs must produce (the captured server tables and the processes of ownedWorld).
var (
	ownedStates = []model.StateCount{
		{Proto: model.ProtoTCP, State: "ESTABLISHED", Count: 1},
		{Proto: model.ProtoTCP, State: "LISTEN", Count: 26},
		{Proto: model.ProtoTCP, State: "TIME_WAIT", Count: 1},
		{Proto: model.ProtoTCP6, State: "LISTEN", Count: 25},
		{Proto: model.ProtoUDP, Count: 7},
		{Proto: model.ProtoUDP6, Count: 6},
	}
	ownedProcesses = []model.ProcessConnections{
		{PID: 400, Command: cmdMaster, Count: 3},
		{PID: 100, Command: cmdSSHD, Count: 2},
		{PID: 200, Command: cmdNginx, Count: 2},
		{PID: 201, Command: cmdNginx, Count: 2},
		{PID: 300, Command: cmdNamed, Count: 2},
		{PID: 301, Command: cmdNamed, Count: 1},
		{PID: 600, Command: cmdNamed, Count: 1},
		{PID: 601, Command: cmdNamed, Count: 1},
	}
	ownedRemotes = []model.RemoteCount{
		{Addr: netip.MustParseAddr("192.0.2.53"), Count: 2},
		{Addr: netip.MustParseAddr("198.51.100.7"), Count: 1},
		{Addr: netip.MustParseAddr("198.51.100.20"), Count: 1},
	}
	// The UIDs are those of the captured tables: the sshd connection is root's, the two UDP sockets are UID 30's (no line in the captured
	// passwd, so the decimal number is the name), and the TIME_WAIT line has inode 0, so it has no UID.
	ownedConnections = []model.Connection{
		{Proto: model.ProtoTCP, Local: addrPort("192.0.2.10:22"), Remote: addrPort(sshdConnection), State: "ESTABLISHED", PID: 100, Command: cmdSSHD, UID: uidOf(0), User: "root"},
		{Proto: model.ProtoTCP, Local: addrPort("192.0.2.10:56518"), Remote: addrPort("198.51.100.20:443"), State: "TIME_WAIT"},
		{Proto: model.ProtoUDP, Local: addrPort("192.0.2.10:11651"), Remote: addrPort("192.0.2.53:53"), PID: 300, Command: cmdNamed, UID: uidOf(30), User: "30"},
		{Proto: model.ProtoUDP, Local: addrPort("192.0.2.10:59433"), Remote: addrPort("192.0.2.53:53"), UID: uidOf(30), User: "30"},
	}
)

const ownedListeners = 51 // distinct protocol, address and port of the captured listeners

// listenerAt returns the listeners of the snapshot on one local endpoint.
func listenerAt(snap *model.ConnectionSnapshot, proto model.Proto, local string) []model.Listener {
	var out []model.Listener
	for _, l := range snap.Listeners {
		if l.Proto == proto && l.Local == addrPort(local) {
			out = append(out, l)
		}
	}
	return out
}

// uidOf returns a pointer to v, the form of an optional UID of an entry.
func uidOf(v uint32) *uint32 {
	return &v
}

// uidText writes an optional UID for a failure message.
func uidText(u *uint32) string {
	if u == nil {
		return "none"
	}
	return strconv.FormatUint(uint64(*u), 10)
}

// sameUID reports whether two optional UIDs are both absent or hold the same number.
func sameUID(a, b *uint32) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	return *a == *b
}

// sameListener compares two listener entries, the UID by value.
func sameListener(a, b model.Listener) bool {
	return a.Proto == b.Proto && a.Local == b.Local && a.PID == b.PID && a.Command == b.Command &&
		sameUID(a.UID, b.UID) && a.User == b.User && a.MixedUIDs == b.MixedUIDs
}

// sameConnection compares two connection entries, the UID by value.
func sameConnection(a, b model.Connection) bool {
	return a.Proto == b.Proto && a.Local == b.Local && a.Remote == b.Remote && a.State == b.State && a.PID == b.PID && a.Command == b.Command &&
		sameUID(a.UID, b.UID) && a.User == b.User
}

// listenerText writes a listener entry for a failure message, the UID as its number.
func listenerText(l model.Listener) string {
	return fmt.Sprintf("{%s %v pid=%d command=%q uid=%s user=%q mixed_uids=%v}", l.Proto, l.Local, l.PID, l.Command, uidText(l.UID), l.User, l.MixedUIDs)
}

// connectionText writes a connection entry for a failure message, the UID as its number.
func connectionText(c model.Connection) string {
	return fmt.Sprintf("{%s %v -> %v %s pid=%d command=%q uid=%s user=%q}", c.Proto, c.Local, c.Remote, c.State, c.PID, c.Command, uidText(c.UID), c.User)
}

// listenersText writes a list of listener entries.
func listenersText(list []model.Listener) string {
	parts := make([]string, len(list))
	for i, l := range list {
		parts[i] = listenerText(l)
	}
	return "[" + strings.Join(parts, " ") + "]"
}

// connectionsText writes a list of connection entries.
func connectionsText(list []model.Connection) string {
	parts := make([]string, len(list))
	for i, c := range list {
		parts[i] = connectionText(c)
	}
	return "[" + strings.Join(parts, " ") + "]"
}

// wantListeners compares two lists of listener entries in order.
func wantListeners(t *testing.T, what string, got, want []model.Listener) {
	t.Helper()

	if !slices.EqualFunc(got, want, sameListener) {
		t.Errorf("%s = %s, want %s", what, listenersText(got), listenersText(want))
	}
}

// wantConnections compares two lists of connection entries in order.
func wantConnections(t *testing.T, what string, got, want []model.Connection) {
	t.Helper()

	if !slices.EqualFunc(got, want, sameConnection) {
		t.Errorf("%s = %s, want %s", what, connectionsText(got), connectionsText(want))
	}
}

// hasConnection reports whether the list holds the connection entry.
func hasConnection(list []model.Connection, want model.Connection) bool {
	return slices.ContainsFunc(list, func(c model.Connection) bool { return sameConnection(c, want) })
}

// rootListener is the listener entry of a generated socket without owner as the collector reports it: the helpers of the world give
// their sockets the UID 0, and the captured passwd names it root.
func rootListener(proto model.Proto, local string) model.Listener {
	return model.Listener{Proto: proto, Local: addrPort(local), UID: uidOf(0), User: "root"}
}

// withOwner returns the listener entry with its owner.
func withOwner(l model.Listener, pid int32, command string) model.Listener {
	l.PID, l.Command = pid, command
	return l
}

// wantListener checks that exactly one listener sits on the endpoint and that it has the owner, the UID and the user name, and that
// the endpoint has no mixed UIDs.
func wantListener(t *testing.T, snap *model.ConnectionSnapshot, proto model.Proto, local string, pid int32, command string, uid uint32, user string) {
	t.Helper()

	got := listenerAt(snap, proto, local)
	want := model.Listener{Proto: proto, Local: addrPort(local), PID: pid, Command: command, UID: uidOf(uid), User: user}
	if len(got) != 1 || !sameListener(got[0], want) {
		t.Errorf("listeners on %s %s = %s, want exactly %s", proto, local, listenersText(got), listenerText(want))
	}
}

// wantFlags checks the three flags that tell a regular from a full snapshot and its completeness.
func wantFlags(t *testing.T, snap *model.ConnectionSnapshot, aggregatesOnly, complete, aggregatesIncomplete bool, check model.ListenerCheck) {
	t.Helper()

	if snap.AggregatesOnly != aggregatesOnly || snap.Complete != complete || snap.AggregatesIncomplete != aggregatesIncomplete || snap.ListenerCheck != check {
		t.Errorf("snapshot flags aggregates_only=%v complete=%v aggregates_incomplete=%v listener_check=%q, want %v %v %v %q",
			snap.AggregatesOnly, snap.Complete, snap.AggregatesIncomplete, snap.ListenerCheck, aggregatesOnly, complete, aggregatesIncomplete, check)
	}
}

// allTableProtos are the protocols of the four tables, in table order.
var allTableProtos = []model.Proto{model.ProtoTCP, model.ProtoTCP6, model.ProtoUDP, model.ProtoUDP6}

// wantInventory checks the inventory state of the record's listeners list and the protocols of the tables the run read.
func wantInventory(t *testing.T, snap *model.ConnectionSnapshot, state model.InventoryState, tables ...model.Proto) {
	t.Helper()

	if snap.Inventory != state {
		t.Errorf("inventory = %q, want %q", snap.Inventory, state)
	}
	if !slices.Equal(snap.Tables, tables) {
		t.Errorf("tables = %v, want %v", snap.Tables, tables)
	}
}

// wantNewListeners checks the reported new listeners and the number of the omitted ones.
func wantNewListeners(t *testing.T, snap *model.ConnectionSnapshot, omitted uint32, want ...model.Listener) {
	t.Helper()

	wantListeners(t, "new_listeners", snap.NewListeners, want)
	if snap.NewListenersOmitted != omitted {
		t.Errorf("new_listeners_omitted = %d, want %d", snap.NewListenersOmitted, omitted)
	}
}

// addRemoteConnections adds count established connections to the addresses 203.0.113.1 and on; nobody holds them.
func addRemoteConnections(w *world, count int) {
	for i := 1; i <= count; i++ {
		w.addSocket("tcp", "192.0.2.10:"+strconv.Itoa(40000+i), "203.0.113."+strconv.Itoa(i)+":443", stateEstablished, 91000000+uint64(i))
	}
}

func TestConnections_RegularSnapshotHoldsAggregatesAndInventory(t *testing.T) {
	w := ownedWorld(t)
	c := newCollector(w)
	snapshotOf(t, c, runAt(0, false))

	_, snap := snapshotOf(t, c, runAt(1, false))

	wantFlags(t, snap, true, false, false, model.ListenerCheckAll)
	wantInventory(t, snap, model.InventoryAll, allTableProtos...)
	wantList(t, "states", snap.States, ownedStates)
	wantList(t, "processes", snap.Processes, ownedProcesses)
	wantList(t, "remotes", snap.Remotes, ownedRemotes)
	if len(snap.Connections) != 0 {
		t.Errorf("connections = %v, want none in a regular snapshot", snap.Connections)
	}
	wantNewListeners(t, snap, 0)
	if len(snap.Listeners) != ownedListeners {
		t.Errorf("listeners = %d entries, want %d (one per protocol, address and port)", len(snap.Listeners), ownedListeners)
	}
	wantListener(t, snap, model.ProtoTCP, sshdListener, 100, cmdSSHD, 0, "root")
	wantListener(t, snap, model.ProtoTCP, nginxListener, 200, cmdNginx, 0, "root")
	wantListener(t, snap, model.ProtoTCP, "0.0.0.0:25", 400, cmdMaster, 0, "root")
	wantListener(t, snap, model.ProtoTCP, "0.0.0.0:110", 0, "", 0, "root")
	wantListener(t, snap, model.ProtoUDP6, "[::1]:53", 0, "", 116, "116")

	t.Run("the remotes are the top 25", func(t *testing.T) {
		w := ownedWorld(t)
		c := newCollector(w)
		snapshotOf(t, c, runAt(0, false))
		addRemoteConnections(w, 30)

		_, snap := snapshotOf(t, c, runAt(1, false))

		want := slices.Clone(ownedRemotes)
		for i := 1; i <= 22; i++ {
			want = append(want, model.RemoteCount{Addr: netip.MustParseAddr("203.0.113." + strconv.Itoa(i)), Count: 1})
		}
		wantList(t, "remotes", snap.Remotes, want)
		wantFlags(t, snap, true, false, false, model.ListenerCheckAll)
	})
}

func TestConnections_ListOrders(t *testing.T) {
	w := ownedWorld(t)
	_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

	wantList(t, "states (by protocol, then state)", snap.States, ownedStates)
	wantList(t, "processes (by count descending, then PID)", snap.Processes, ownedProcesses)
	wantList(t, "remotes (by count descending, then address)", snap.Remotes, ownedRemotes)
	wantConnections(t, "connections (by protocol, local, remote)", snap.Connections, ownedConnections)

	if len(snap.Listeners) != ownedListeners {
		t.Fatalf("listeners = %d entries, want %d", len(snap.Listeners), ownedListeners)
	}
	privileged := 0
	for i, l := range snap.Listeners {
		if l.Local.Port() < PrivilegedPorts {
			privileged++
			if privileged != i+1 {
				t.Errorf("listener %d (%v) is privileged after an unprivileged one, want ports 1 to 1023 first", i, l.Local)
			}
		}
		if i == 0 {
			continue
		}
		prev, cur := snap.Listeners[i-1], l
		if !lessListener(listenerKey{prev.Proto, prev.Local.Addr(), prev.Local.Port()}, listenerKey{cur.Proto, cur.Local.Addr(), cur.Local.Port()}) {
			t.Errorf("listener %d %s %v follows %s %v, want the inventory order (privileged ports first, then protocol, address, port)",
				i, cur.Proto, cur.Local, prev.Proto, prev.Local)
		}
	}
	if privileged != 37 {
		t.Errorf("privileged listeners = %d, want 37 of the captured tables", privileged)
	}
	if first := snap.Listeners[0]; first.Proto != model.ProtoTCP || first.Local != addrPort(sshdListener) {
		t.Errorf("first listener = %s %v, want tcp %s", first.Proto, first.Local, sshdListener)
	}
}

func TestConnections_ReusePortEndpointIsOneListener(t *testing.T) {
	w := ownedWorld(t)
	_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

	seen := map[string]int{}
	for _, l := range snap.Listeners {
		seen[string(l.Proto)+" "+l.Local.String()]++
	}
	for endpoint, n := range seen {
		if n != 1 {
			t.Errorf("listener entries on %s = %d, want 1 (sockets sharing an endpoint are one listener)", endpoint, n)
		}
	}
	// Two sockets on tcp 127.0.0.1:53: PID 601 holds the first one of the table, PID 600 the second; the lowest owning PID names the listener.
	wantListener(t, snap, model.ProtoTCP, "127.0.0.1:53", 600, cmdNamed, 116, "116")
	wantListener(t, snap, model.ProtoUDP, "192.0.2.10:53", 300, cmdNamed, 116, "116")
	wantList(t, "states (every socket counts)", snap.States, ownedStates)
}

func TestConnections_MappedAddressesKept(t *testing.T) {
	w := newWorld(t)
	const (
		mappedLocal = "[::ffff:192.0.2.10]:443"
		peer        = "198.51.100.9"
	)
	w.addSocket("tcp6", mappedLocal, "[::ffff:"+peer+"]:40000", stateEstablished, 92000001)
	w.addSocket("tcp6", mappedLocal, "[::ffff:"+peer+"]:40001", stateEstablished, 92000002)
	w.addSocket("tcp", "192.0.2.10:443", peer+":40002", stateEstablished, 92000003)

	_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

	mapped, plain := netip.MustParseAddr("::ffff:"+peer), netip.MustParseAddr(peer)
	counts := map[netip.Addr]uint32{}
	for _, r := range snap.Remotes {
		counts[r.Addr] = r.Count
	}
	if counts[mapped] != 2 || counts[plain] != 1 {
		t.Errorf("remotes of %s = %d and of %s = %d, want 2 and 1 (a mapped address is not unmapped, so one peer is two entries)", mapped, counts[mapped], plain, counts[plain])
	}
	want := model.Connection{Proto: model.ProtoTCP6, Local: addrPort(mappedLocal), Remote: addrPort("[::ffff:" + peer + "]:40000"), State: "ESTABLISHED", UID: uidOf(0), User: "root"}
	if !hasConnection(snap.Connections, want) {
		t.Errorf("connections = %s, want one with the mapped endpoints %s", connectionsText(snap.Connections), connectionText(want))
	}
	linkLocal := addrPort("[fe80::200:5eff:fe00:5301]:53")
	if got := listenerAt(snap, model.ProtoUDP6, linkLocal.String()); len(got) != 1 || got[0].Local.Addr().Zone() != "" {
		t.Errorf("listeners on udp6 %v = %+v, want one without a zone", linkLocal, got)
	}
}

func TestConnections_RegularIsNeverComplete(t *testing.T) {
	w := ownedWorld(t)
	c := newCollector(w)
	steps := []struct {
		name     string
		pressure bool
		complete bool
	}{
		{"first run is full", false, true},
		{"regular", false, false},
		{"regular again", false, false},
		{"pressure is full", true, true},
		{"regular after pressure", false, false},
	}
	for i, s := range steps {
		_, snap := snapshotOf(t, c, runAt(i, s.pressure))
		if snap.Complete != s.complete || snap.AggregatesOnly == s.complete {
			t.Errorf("%s: complete = %v and aggregates_only = %v, want complete %v (a regular snapshot is never complete)", s.name, snap.Complete, snap.AggregatesOnly, s.complete)
		}
	}

	t.Run("empty tables", func(t *testing.T) {
		w := newWorld(t)
		w.clear()
		c := newCollector(w)
		snapshotOf(t, c, runAt(0, false))

		_, snap := snapshotOf(t, c, runAt(1, false))

		wantFlags(t, snap, true, false, false, model.ListenerCheckAll)
	})
}

func TestConnections_FullSnapshotOnFirstRun(t *testing.T) {
	w := ownedWorld(t)
	addRemoteConnections(w, 30)

	_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

	wantFlags(t, snap, false, true, false, model.ListenerCheckStart)
	wantInventory(t, snap, model.InventoryAll, allTableProtos...)
	wantNewListeners(t, snap, 0)
	if len(snap.Connections) != 34 {
		t.Errorf("connections = %d entries, want all 34", len(snap.Connections))
	}
	if len(snap.Remotes) != 33 {
		t.Errorf("remotes = %d entries, want every one of the 33 addresses (a regular snapshot holds 25)", len(snap.Remotes))
	}
	if len(snap.Listeners) != ownedListeners || len(snap.Processes) != len(ownedProcesses) {
		t.Errorf("listeners = %d and processes = %d, want %d and %d", len(snap.Listeners), len(snap.Processes), ownedListeners, len(ownedProcesses))
	}
}

func TestConnections_FullSnapshotUnderPressure(t *testing.T) {
	w := ownedWorld(t)
	c := newCollector(w)
	snapshotOf(t, c, runAt(0, false))

	_, regular := snapshotOf(t, c, runAt(1, false))
	_, pressed := snapshotOf(t, c, runAt(2, true))
	_, after := snapshotOf(t, c, runAt(3, false))

	wantFlags(t, regular, true, false, false, model.ListenerCheckAll)
	wantFlags(t, pressed, false, true, false, model.ListenerCheckAll)
	wantConnections(t, "connections under pressure", pressed.Connections, ownedConnections)
	wantList(t, "remotes under pressure", pressed.Remotes, ownedRemotes)
	wantFlags(t, after, true, false, false, model.ListenerCheckAll)
	if len(after.Connections) != 0 {
		t.Errorf("connections after the pressure ended = %v, want none (the mode at the run's start decides)", after.Connections)
	}
}

func TestConnections_FullSnapshotOnNewListener(t *testing.T) {
	t.Run("a new TCP listener is reported", func(t *testing.T) {
		w := ownedWorld(t)
		c := newCollector(w)
		snapshotOf(t, c, runAt(0, false))
		w.addListener("tcp", newBackdoor, 99000001)
		w.process(700, "evil", "/tmp/evil\x00")
		w.holdAll(700, 99000001)

		rec, snap := snapshotOf(t, c, runAt(1, false))

		wantFlags(t, snap, false, true, false, model.ListenerCheckAll)
		wantNewListeners(t, snap, 0, withOwner(rootListener(model.ProtoTCP, newBackdoor), 700, "evil"))
		wantConnections(t, "connections of the full snapshot", snap.Connections, ownedConnections)
		if !rec.CapturedAt.Equal(runAt(1, false).Start) {
			t.Errorf("captured at %v, want the run's start", rec.CapturedAt)
		}

		_, next := snapshotOf(t, c, runAt(2, false))

		wantFlags(t, next, true, false, false, model.ListenerCheckAll)
		wantNewListeners(t, next, 0)
	})

	t.Run("an ephemeral UDP socket is no reason", func(t *testing.T) {
		w := ownedWorld(t)
		c := newCollector(w)
		snapshotOf(t, c, runAt(0, false))
		w.addListener("udp", "0.0.0.0:40000", 99000002)

		_, snap := snapshotOf(t, c, runAt(1, false))

		wantFlags(t, snap, true, false, false, model.ListenerCheckAll)
		wantNewListeners(t, snap, 0)
		if got := listenerAt(snap, model.ProtoUDP, "0.0.0.0:40000"); len(got) != 1 {
			t.Errorf("listeners on udp 0.0.0.0:40000 = %+v, want it in the inventory", got)
		}
	})

	t.Run("a UDP listener outside the ephemeral range is reported", func(t *testing.T) {
		w := ownedWorld(t)
		c := newCollector(w)
		snapshotOf(t, c, runAt(0, false))
		w.addListener("udp", "0.0.0.0:5353", 99000003)

		_, snap := snapshotOf(t, c, runAt(1, false))

		wantFlags(t, snap, false, true, false, model.ListenerCheckAll)
		wantNewListeners(t, snap, 0, rootListener(model.ProtoUDP, "0.0.0.0:5353"))
	})
}

func TestConnections_FailedRunIsNotCompared(t *testing.T) {
	w := ownedWorld(t)
	faulty := newFaultFS(w)
	c := newCollector(faulty)
	snapshotOf(t, c, runAt(0, false))
	w.addListener("tcp", newBackdoor, 99000001)

	faulty.dirErr["."] = syscall.EIO // the tables are read, the listing of the processes fails
	records, err := c.Collect(context.Background(), runAt(1, false))
	if err == nil || !strings.Contains(err.Error(), "listing /proc") || len(records) != 0 {
		t.Fatalf("Collect() = %d records, %v, want no records and the listing error", len(records), err)
	}
	faulty.dirErr = map[string]error{}
	faulty.readErr["net/tcp"] = syscall.EIO
	if _, err := c.Collect(context.Background(), runAt(2, false)); err == nil {
		t.Fatal("Collect() error = nil, want the table error")
	}
	faulty.readErr = map[string]error{}

	_, snap := snapshotOf(t, c, runAt(3, false))

	wantFlags(t, snap, false, true, false, model.ListenerCheckAll)
	wantNewListeners(t, snap, 0, rootListener(model.ProtoTCP, newBackdoor))
}

func TestConnections_LateRunKeepsNoInventory(t *testing.T) {
	w := ownedWorld(t)
	last := newBlockingFS(t, w, fdinfoName(601, 3))
	last.armed.Store(false)
	c := newCollector(last)
	snapshotOf(t, c, runAt(0, false))
	w.addListener("tcp", newBackdoor, 99000001)
	last.armed.Store(true)

	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(cancel)
	type result struct {
		records []model.Record
		err     error
	}
	done := make(chan result, 1)
	go func() {
		records, err := c.Collect(ctx, runAt(1, false))
		done <- result{records, err}
	}()
	select {
	case <-last.reached:
	case r := <-done:
		t.Fatalf("Collect() = %d records, %v before reading the last descriptor of the last process, want it to block there", len(r.records), r.err)
	}
	cancel() // the context ends during the read of the last PID's fdinfo, which is then released
	last.open()
	r := <-done

	if !errors.Is(r.err, context.Canceled) || len(r.records) != 0 {
		t.Errorf("Collect() = %d records, %v, want no records and the context's error", len(r.records), r.err)
	}
	last.armed.Store(false)
	_, snap := snapshotOf(t, c, runAt(2, false))
	wantFlags(t, snap, false, true, false, model.ListenerCheckAll)
	wantNewListeners(t, snap, 0, rootListener(model.ProtoTCP, newBackdoor))
}

func TestConnections_ReportsAtMost64NewListeners(t *testing.T) {
	w := newWorld(t)
	c := newCollector(w)
	snapshotOf(t, c, runAt(0, false))
	w.addListeners("0.0.0.0", 1000, 3, 98000000)
	w.addListeners("0.0.0.0", 5000, 100, 98001000)

	_, snap := snapshotOf(t, c, runAt(1, false))

	var want []model.Listener
	for _, port := range slices.Concat([]uint16{1000, 1001, 1002}, portsFrom(5000, 61)) {
		want = append(want, rootListener(model.ProtoTCP, netip.AddrPortFrom(netip.IPv4Unspecified(), port).String()))
	}
	wantFlags(t, snap, false, true, false, model.ListenerCheckAll)
	wantNewListeners(t, snap, 39, want...)
}

// portsFrom returns count ports from first on.
func portsFrom(first, count uint16) []uint16 {
	ports := make([]uint16, 0, count)
	for i := range count {
		ports = append(ports, first+i)
	}
	return ports
}

// longCommand is a command line word the process rules keep whole (1,000 bytes).
var longCommand = strings.Repeat("a", 1000)

func TestConnections_SizeCutOfListenersKeepsTheComparison(t *testing.T) {
	w := newWorld(t)
	w.clear()
	w.addListeners("0.0.0.0", 2000, 3000, 97000000)
	w.process(700, "big", longCommand+"\x00")
	w.holdRange(700, 3, 97000000, 3000)
	c := newCollector(w)

	_, first := snapshotOf(t, c, runAt(0, false))
	_, second := snapshotOf(t, c, runAt(1, false))

	if len(first.Listeners) == 0 || len(first.Listeners) >= 3000 || !first.AggregatesIncomplete || first.Complete {
		t.Errorf("first run: %d listeners, aggregates_incomplete = %v, complete = %v, want a cut list and incomplete aggregates", len(first.Listeners), first.AggregatesIncomplete, first.Complete)
	}
	// The comparison uses the inventory as the collector kept it, not the list as the record's size budget cut it.
	wantFlags(t, second, true, false, true, model.ListenerCheckAll)
	wantNewListeners(t, second, 0)
}

func TestConnections_OwnerlessSockets(t *testing.T) {
	w := ownedWorld(t)
	w.addSocket("tcp", "192.0.2.10:50000", "203.0.113.50:443", stateEstablished, 93000001) // its process ended
	w.process(800, "idle", "/usr/bin/idle\x00")
	w.addRaw(800, 3, readFdinfoSample(t, "unix"))
	w.addRaw(800, 4, readFdinfoSample(t, "pipe"))
	w.add(800, 5, socketMount, 999999999) // a socket of no table

	_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

	ownerless := []model.Connection{
		{Proto: model.ProtoTCP, Local: addrPort("192.0.2.10:50000"), Remote: addrPort("203.0.113.50:443"), State: "ESTABLISHED", UID: uidOf(0), User: "root"},
		ownedConnections[1],
	}
	for _, want := range ownerless {
		if !hasConnection(snap.Connections, want) {
			t.Errorf("connections = %s, want %s without PID and command", connectionsText(snap.Connections), connectionText(want))
		}
	}
	remotes := map[netip.Addr]uint32{}
	for _, r := range snap.Remotes {
		remotes[r.Addr] = r.Count
	}
	if remotes[netip.MustParseAddr("203.0.113.50")] != 1 || remotes[netip.MustParseAddr("198.51.100.20")] != 1 {
		t.Errorf("remotes = %+v, want the ownerless sockets' addresses counted", snap.Remotes)
	}
	wantList(t, "processes", snap.Processes, ownedProcesses)
	if got := snap.States[2]; got != (model.StateCount{Proto: model.ProtoTCP, State: "TIME_WAIT", Count: 1}) {
		t.Errorf("states[2] = %+v, want the TIME_WAIT socket counted", got)
	}
}

func TestConnections_InodeZeroLinesAreSeparateSockets(t *testing.T) {
	w := newWorld(t)
	w.clear()
	for range 3 {
		w.addSocket("tcp", "192.0.2.10:40000", "198.51.100.20:443", stateTimeWait, 0)
	}
	w.process(900, "holder", "/usr/bin/holder\x00")
	w.add(900, 3, socketMount, 0) // a descriptor with inode 0 matches no socket

	_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

	wantList(t, "states", snap.States, []model.StateCount{{Proto: model.ProtoTCP, State: "TIME_WAIT", Count: 3}})
	wantList(t, "remotes", snap.Remotes, []model.RemoteCount{{Addr: netip.MustParseAddr("198.51.100.20"), Count: 3}})
	if len(snap.Connections) != 3 {
		t.Errorf("connections = %d entries, want 3 (lines with inode 0 are never merged)", len(snap.Connections))
	}
	for _, conn := range snap.Connections {
		if conn.PID != 0 || conn.Command != "" {
			t.Errorf("connection %s has an owner, want none for inode 0", connectionText(conn))
		}
		if conn.UID != nil || conn.User != "" {
			t.Errorf("connection %s has a UID, want none for inode 0", connectionText(conn))
		}
	}
	if len(snap.Processes) != 0 {
		t.Errorf("processes = %+v, want none", snap.Processes)
	}
	wantFlags(t, snap, false, true, false, model.ListenerCheckStart)
	wantInventory(t, snap, model.InventoryAll, allTableProtos...)
}

func TestConnections_UnreadableDescriptorsLogOnce(t *testing.T) {
	w := ownedWorld(t)
	faulty := newFaultFS(w)
	c, logs := newRecordingCollector(faulty, SocketMount{ID: socketMount})
	faulty.openErr[fdinfoName(200, 3)] = syscall.EACCES

	var snap *model.ConnectionSnapshot
	for i := range 3 {
		_, snap = snapshotOf(t, c, runAt(i, false))
	}

	warns := wantLogged(t, logs, slog.LevelWarn, "socket owners partly unknown", 1)
	if len(warns) == 1 && (warns[0].attrs["processes"] != "1" || warns[0].attrs["err"] != "connstats: 200/fdinfo: permission denied") {
		t.Errorf("warning attributes = %v, want processes=1 and err=connstats: 200/fdinfo: permission denied", warns[0].attrs)
	}
	wantLogged(t, logs, slog.LevelInfo, "socket owners known again", 0)
	if !snap.AggregatesIncomplete {
		t.Error("aggregates_incomplete = false, want true while a process cannot be read")
	}
	wantListener(t, snap, model.ProtoTCP, nginxListener, 201, cmdNginx, 0, "root") // PID 200 could not be read, so its twin owns the socket
	if slices.ContainsFunc(snap.Processes, func(p model.ProcessConnections) bool { return p.PID == 200 }) {
		t.Errorf("processes = %+v, want none for the unreadable PID 200", snap.Processes)
	}

	delete(faulty.openErr, fdinfoName(200, 3))
	_, snap = snapshotOf(t, c, runAt(3, false))
	snapshotOf(t, c, runAt(4, false))

	wantLogged(t, logs, slog.LevelInfo, "socket owners known again", 1)
	wantLogged(t, logs, slog.LevelWarn, "socket owners partly unknown", 1)
	wantListener(t, snap, model.ProtoTCP, nginxListener, 200, cmdNginx, 0, "root")
	if snap.AggregatesIncomplete {
		t.Error("aggregates_incomplete = true after the process could be read again, want false")
	}
}

func TestConnections_FdinfoWithoutInoIsUnreadable(t *testing.T) {
	w := ownedWorld(t)
	w.addRaw(200, 3, "pos:\t0\nflags:\t02000002\nmnt_id:\t10\n") // a kernel before 5.14 writes no ino: line
	c, logs := newRecordingCollector(w, SocketMount{ID: socketMount})

	_, snap := snapshotOf(t, c, runAt(0, false))
	snapshotOf(t, c, runAt(1, false))

	warns := wantLogged(t, logs, slog.LevelWarn, "socket owners partly unknown", 1)
	if len(warns) == 1 && (warns[0].attrs["processes"] != "1" || warns[0].attrs["err"] != "connstats: 200/fdinfo: not in the expected format") {
		t.Errorf("warning attributes = %v, want processes=1 and err=connstats: 200/fdinfo: not in the expected format", warns[0].attrs)
	}
	if !snap.AggregatesIncomplete || snap.Complete {
		t.Errorf("aggregates_incomplete = %v and complete = %v, want true and false", snap.AggregatesIncomplete, snap.Complete)
	}
}

func TestConnections_UnknownSocketMountLeavesNoOwners(t *testing.T) {
	w := ownedWorld(t)
	rec := &recordingFS{FS: w}
	probeErr := errors.New("sockmount: socket: permission denied")
	c, logs := newRecordingCollector(rec, SocketMount{Err: probeErr})

	_, first := snapshotOf(t, c, runAt(0, false))
	_, second := snapshotOf(t, c, runAt(1, false))

	for _, name := range rec.opened() {
		if strings.Contains(name, "fdinfo") {
			t.Errorf("collector opened %q, want no fdinfo when the sockets' mount is unknown", name)
		}
	}
	for i, snap := range []*model.ConnectionSnapshot{first, second} {
		if len(snap.Processes) != 0 || !snap.AggregatesIncomplete || snap.Complete {
			t.Errorf("run %d: %d processes, aggregates_incomplete = %v, complete = %v, want none, true, false", i, len(snap.Processes), snap.AggregatesIncomplete, snap.Complete)
		}
		for _, l := range snap.Listeners {
			if l.PID != 0 || l.Command != "" {
				t.Errorf("run %d: listener %+v has an owner, want none", i, l)
			}
		}
		wantList(t, "states", snap.States, ownedStates)
	}
	warns := wantLogged(t, logs, slog.LevelWarn, "socket owners unknown", 1)
	if len(warns) == 1 && warns[0].attrs["err"] != probeErr.Error() {
		t.Errorf("warning err = %q, want %q", warns[0].attrs["err"], probeErr)
	}
}

func TestConnections_SnapshotCarriesInventoryAndTables(t *testing.T) {
	t.Run("all four tables", func(t *testing.T) {
		c := newCollector(ownedWorld(t))

		_, first := snapshotOf(t, c, runAt(0, false))
		_, second := snapshotOf(t, c, runAt(1, false))

		wantInventory(t, first, model.InventoryAll, model.ProtoTCP, model.ProtoTCP6, model.ProtoUDP, model.ProtoUDP6)
		wantInventory(t, second, model.InventoryAll, model.ProtoTCP, model.ProtoTCP6, model.ProtoUDP, model.ProtoUDP6)
	})
	t.Run("without the IPv6 tables", func(t *testing.T) {
		w := newWorld(t)
		w.dropTable("tcp6")
		w.dropTable("udp6")

		_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

		wantInventory(t, snap, model.InventoryAll, model.ProtoTCP, model.ProtoUDP)
		for _, p := range snap.Tables {
			if p == model.ProtoTCP6 || p == model.ProtoUDP6 {
				t.Errorf("tables = %v, want no IPv6 protocol (those tables were not read)", snap.Tables)
			}
		}
	})
	t.Run("without the UDP tables", func(t *testing.T) {
		w := newWorld(t)
		w.dropTable("udp")
		w.dropTable("udp6")

		_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

		wantInventory(t, snap, model.InventoryAll, model.ProtoTCP, model.ProtoTCP6)
	})
	t.Run("a skipped line", func(t *testing.T) {
		w := newWorld(t)
		w.replaceLine("tcp", 3, "garbage")
		w.dropTable("tcp6")

		_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

		wantInventory(t, snap, model.InventorySkipped, model.ProtoTCP, model.ProtoUDP, model.ProtoUDP6)
	})
	t.Run("the record passes the wire check with both fields", func(t *testing.T) {
		rec, _ := snapshotOf(t, newCollector(ownedWorld(t)), runAt(0, false))

		checkWire(t, rec)
	})
}

func TestConnections_SizeCutOfListenersSetsInventory(t *testing.T) {
	t.Run("a cut after the privileged part", func(t *testing.T) {
		w := newWorld(t)
		w.clear()
		w.addListeners("0.0.0.0", 2000, 3000, 97000000)
		w.process(700, "big", longCommand+"\x00")
		w.holdRange(700, 3, 97000000, 3000)

		_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

		if len(snap.Listeners) == 0 || len(snap.Listeners) >= 3000 {
			t.Fatalf("listeners = %d entries, want a list cut by the size budget (the precondition of this case)", len(snap.Listeners))
		}
		wantInventory(t, snap, model.InventoryPrivileged, allTableProtos...)
	})
	t.Run("a cut inside the privileged part", func(t *testing.T) {
		w := newWorld(t)
		w.clear()
		w.addListeners("0.0.0.0", 1, 1000, 98000000)
		w.addListeners("0.0.0.0", 2000, 2000, 98100000)
		w.process(700, "big", longCommand+"\x00")
		w.holdRange(700, 3, 98000000, 1000)
		w.holdRange(700, 1003, 98100000, 2000)

		_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

		privileged := 0
		for _, l := range snap.Listeners {
			if l.Local.Port() < PrivilegedPorts {
				privileged++
			}
		}
		if len(snap.Listeners) >= 3000 || privileged >= 1000 {
			t.Fatalf("listeners = %d entries, %d privileged, want a list cut inside its privileged part (the precondition of this case)", len(snap.Listeners), privileged)
		}
		wantInventory(t, snap, model.InventoryCut, allTableProtos...)
	})
}

func TestConnections_MissingIPv6TablesLeaveProtocolsOut(t *testing.T) {
	w := newWorld(t)
	w.dropTable("tcp6")
	w.dropTable("udp6")
	c, logs := newRecordingCollector(w, SocketMount{ID: socketMount})

	_, first := snapshotOf(t, c, runAt(0, false))
	_, second := snapshotOf(t, c, runAt(1, false))

	wantStates := []model.StateCount{
		{Proto: model.ProtoTCP, State: "ESTABLISHED", Count: 1},
		{Proto: model.ProtoTCP, State: "LISTEN", Count: 26},
		{Proto: model.ProtoTCP, State: "TIME_WAIT", Count: 1},
		{Proto: model.ProtoUDP, Count: 7},
	}
	wantList(t, "states without the IPv6 tables", first.States, wantStates)
	if len(first.Listeners) != 26 {
		t.Errorf("listeners = %d entries, want the 23 TCP and 3 UDP endpoints of the IPv4 tables", len(first.Listeners))
	}
	wantFlags(t, second, true, false, false, model.ListenerCheckAll)
	unavailable := wantLogged(t, logs, slog.LevelInfo, "socket tables unavailable", 1)
	if len(unavailable) == 1 && unavailable[0].attrs["tables"] != "tcp6,udp6" {
		t.Errorf("tables = %q, want tcp6,udp6", unavailable[0].attrs["tables"])
	}

	w.lines["tcp6"] = tableLines(t, serverNetDir, "tcp6")
	w.lines["udp6"] = tableLines(t, serverNetDir, "udp6")
	w.dirty = true
	_, back := snapshotOf(t, c, runAt(2, false))
	snapshotOf(t, c, runAt(3, false))

	wantLogged(t, logs, slog.LevelInfo, "socket tables available", 1)
	wantLogged(t, logs, slog.LevelInfo, "socket tables unavailable", 1)
	// Another set of tables than in the last run: nothing is compared, so the 47 IPv6 listeners are not reported and no full snapshot is taken.
	wantFlags(t, back, true, false, false, model.ListenerCheckNone)
	wantInventory(t, back, model.InventoryAll, allTableProtos...)
	wantInventory(t, first, model.InventoryAll, model.ProtoTCP, model.ProtoUDP)
	wantInventory(t, second, model.InventoryAll, model.ProtoTCP, model.ProtoUDP)
	wantNewListeners(t, back, 0)
	if len(back.Listeners) != ownedListeners {
		t.Errorf("listeners after the tables returned = %d, want %d", len(back.Listeners), ownedListeners)
	}

	t.Run("one table", func(t *testing.T) {
		w := newWorld(t)
		w.dropTable("tcp6")
		c, logs := newRecordingCollector(w, SocketMount{ID: socketMount})

		snapshotOf(t, c, runAt(0, false))

		found := wantLogged(t, logs, slog.LevelInfo, "socket tables unavailable", 1)
		if len(found) == 1 && found[0].attrs["tables"] != "tcp6" {
			t.Errorf("tables = %q, want tcp6", found[0].attrs["tables"])
		}
	})
}

func TestConnections_AllTablesMissingFails(t *testing.T) {
	w := newWorld(t)
	for _, name := range tableNames {
		w.dropTable(name)
	}

	records, err := newCollector(w).Collect(context.Background(), runAt(0, false))

	if err == nil || err.Error() != "connstats: no socket table found" || len(records) != 0 {
		t.Errorf("Collect() = %d records, %v, want no records and the error %q", len(records), err, "connstats: no socket table found")
	}
}

func TestConnections_UnreadableLineLeftOut(t *testing.T) {
	w := newWorld(t)
	original := slices.Clone(w.lines["tcp"])
	w.replaceLine("tcp", 1, setField(t, original[1], 3, "0D"))                                 // physical line 3: an unknown state
	w.replaceLine("tcp", 4, "garbage")                                                         // physical line 6
	w.replaceLine("tcp", 7, strings.TrimRight(original[7], " ")+" "+strings.Repeat("x", 5000)) // longer than the reader's buffer
	w.replaceLine("tcp", 10, strings.TrimRight(original[10], " ")+" "+strings.Repeat("x", 600))
	c, logs := newRecordingCollector(w, SocketMount{ID: socketMount})

	_, first := snapshotOf(t, c, runAt(0, false))
	snapshotOf(t, c, runAt(1, false))

	wantFlags(t, first, false, false, first.AggregatesIncomplete, model.ListenerCheckStart)
	wantInventory(t, first, model.InventorySkipped, allTableProtos...)
	if got := first.States[1]; got != (model.StateCount{Proto: model.ProtoTCP, State: "LISTEN", Count: 22}) {
		t.Errorf("states[1] = %+v, want the 22 readable LISTEN sockets (4 lines left out; the line after a long one is read)", got)
	}
	warns := wantLogged(t, logs, slog.LevelWarn, "socket lines not read", 1)
	if len(warns) == 1 && (warns[0].attrs["lines"] != "4" || warns[0].attrs["err"] != "connstats: net/tcp: line 3: unknown state") {
		t.Errorf("warning attributes = %v, want lines=4 and err=connstats: net/tcp: line 3: unknown state", warns[0].attrs)
	}

	t.Run("a left-out line stops the comparison", func(t *testing.T) {
		w.addListener("tcp", newBackdoor, 99000001)

		_, snap := snapshotOf(t, c, runAt(2, false))

		wantFlags(t, snap, true, false, snap.AggregatesIncomplete, model.ListenerCheckNone)
		wantInventory(t, snap, model.InventorySkipped, allTableProtos...)
		wantNewListeners(t, snap, 0)
	})

	t.Run("the lines read again", func(t *testing.T) {
		for _, i := range []int{1, 4, 7, 10} {
			w.replaceLine("tcp", i, original[i])
		}

		snapshotOf(t, c, runAt(3, false))
		snapshotOf(t, c, runAt(4, false))

		wantLogged(t, logs, slog.LevelInfo, "socket lines read again", 1)
		wantLogged(t, logs, slog.LevelWarn, "socket lines not read", 1)
	})
}

func TestConnections_TableReadErrorFails(t *testing.T) {
	cases := []struct {
		name  string
		setup func(f *faultFS)
		want  string
	}{
		{"reading tcp fails", func(f *faultFS) { f.readErr["net/tcp"] = syscall.EIO }, "connstats: net/tcp: input/output error"},
		{"opening udp6 fails", func(f *faultFS) { f.openErr["net/udp6"] = syscall.EACCES }, "connstats: net/udp6: permission denied"},
		{"reading udp fails", func(f *faultFS) { f.readErr["net/udp"] = syscall.EIO }, "connstats: net/udp: input/output error"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			faulty := newFaultFS(newWorld(t))
			tc.setup(faulty)

			records, err := newCollector(faulty).Collect(context.Background(), runAt(0, false))

			if err == nil || err.Error() != tc.want || len(records) != 0 {
				t.Errorf("Collect() = %d records, %v, want no records and the error %q", len(records), err, tc.want)
			}
		})
	}
}

func TestConnections_LargeRecordIsReduced(t *testing.T) {
	w := newWorld(t)
	w.clear()
	w.process(1000, "big", longCommand+"\x00")
	for i := range 4500 {
		w.addSocket("tcp", "192.0.2.10:"+strconv.Itoa(1024+i), "100.64.0."+strconv.Itoa(i%200+1)+":443", stateEstablished, 94000000+uint64(i))
	}
	w.holdRange(1000, 3, 94000000, 4500)

	rec, snap := snapshotOf(t, newCollector(w), runAt(0, true))

	checkWire(t, rec)
	if snap.Complete || len(snap.Connections) == 0 || len(snap.Connections) >= 4096 {
		t.Errorf("complete = %v with %d connections, want a reduced list and not complete", snap.Complete, len(snap.Connections))
	}
	var counted uint32
	for _, s := range snap.States {
		counted += s.Count
	}
	if counted != 4500 || len(snap.Processes) != 1 || len(snap.Remotes) != 200 || snap.AggregatesIncomplete {
		t.Errorf("states count %d sockets, processes = %d, remotes = %d, aggregates_incomplete = %v, want 4500, 1, 200, false (the connections go first)",
			counted, len(snap.Processes), len(snap.Remotes), snap.AggregatesIncomplete)
	}
}

func TestConnections_SocketsBeyondInodeCapUnattributed(t *testing.T) {
	const total = MaxSockets + 10
	w := newWorld(t)
	w.clear()
	w.process(1, "init", "/sbin/init\x00")
	w.add(1, 3, socketMount, 10000000)
	w.add(1, 4, socketMount, 10000000+total-1)
	gen := &genFS{base: w, tables: map[string]*tableGen{"net/tcp": newTableGen(t, "tcp", total, func(i uint32) sockSpec {
		// Descending addresses: the last lines have the lowest keys, so they head the listener list.
		return sockSpec{local: netip.AddrPortFrom(netip.AddrFrom4(addr4(0x0A000000+total-1-i)), 8000), remote: netip.AddrPortFrom(netip.IPv4Unspecified(), 0),
			state: stateListen, inode: 10000000 + uint64(i)}
	})}}

	_, snap := snapshotOf(t, newCollector(gen), runAt(0, false))

	wantList(t, "processes (the first socket is tracked, the last one is beyond the cap)", snap.Processes, []model.ProcessConnections{{PID: 1, Command: "init", Count: 1}})
	if len(snap.Listeners) == 0 || snap.Listeners[0].Local != addrPort("10.0.0.0:8000") || snap.Listeners[0].PID != 0 {
		t.Errorf("first listener = %+v, want 10.0.0.0:8000 without an owner", snap.Listeners)
	}
	if !snap.AggregatesIncomplete || snap.Complete {
		t.Errorf("aggregates_incomplete = %v and complete = %v, want true and false", snap.AggregatesIncomplete, snap.Complete)
	}
	wantList(t, "states (every socket counts)", snap.States, []model.StateCount{{Proto: model.ProtoTCP, State: "LISTEN", Count: total}})
}

// addr4 returns the IPv4 address of a 32-bit number.
func addr4(v uint32) [4]byte {
	var a [4]byte
	binary.BigEndian.PutUint32(a[:], v)
	return a
}

func TestConnections_ReusePortDuplicatesDoNotCutTheInventory(t *testing.T) {
	w := newWorld(t)
	w.clear()
	w.addListeners("0.0.0.0", 1100, 4000, 96000000)
	w.addListeners("0.0.0.0", 1100, 4000, 96100000) // the same 4,000 endpoints again: 8,000 sockets, 4,000 listeners
	c := newCollector(w)
	_, first := snapshotOf(t, c, runAt(0, false))
	w.addListener("tcp", "0.0.0.0:6000", 96200000)

	_, second := snapshotOf(t, c, runAt(1, false))

	if len(first.Listeners) != 4000 || first.AggregatesIncomplete {
		t.Errorf("first run: %d listeners, aggregates_incomplete = %v, want 4000 and false", len(first.Listeners), first.AggregatesIncomplete)
	}
	// Duplicates do not count toward the cut, so the whole inventory is compared.
	wantFlags(t, second, false, true, false, model.ListenerCheckAll)
	wantNewListeners(t, second, 0, rootListener(model.ProtoTCP, "0.0.0.0:6000"))
	if len(second.Listeners) != 4001 {
		t.Errorf("second run: %d listeners, want 4001", len(second.Listeners))
	}
}

func TestConnections_RemotesBeyondCapNotCounted(t *testing.T) {
	w := newWorld(t)
	w.clear()
	for i := range MaxRemotes {
		w.addSocket("tcp", "192.0.2.10:"+strconv.Itoa(1024+i), "100.64."+strconv.Itoa(i/256)+"."+strconv.Itoa(i%256)+":443", stateEstablished, 95000000+uint64(i))
	}
	for i := range 10 { // a heavy address first seen after the cap
		w.addSocket("tcp", "192.0.2.10:"+strconv.Itoa(20000+i), "203.0.113.99:443", stateEstablished, 95100000+uint64(i))
	}
	c := newCollector(w)
	heavy := netip.MustParseAddr("203.0.113.99")

	_, full := snapshotOf(t, c, runAt(0, false))
	_, regular := snapshotOf(t, c, runAt(1, false))

	if full.Complete {
		t.Error("complete = true, want false when remote addresses were not counted")
	}
	for name, snap := range map[string]*model.ConnectionSnapshot{"full": full, "regular": regular} {
		if slices.ContainsFunc(snap.Remotes, func(r model.RemoteCount) bool { return r.Addr == heavy }) {
			t.Errorf("%s remotes hold %v, want an address first seen beyond the cap of %d not counted", name, heavy, MaxRemotes)
		}
	}
	if len(regular.Remotes) != TopRemotes || len(full.Remotes) > MaxRemotes {
		t.Errorf("remotes = %d (regular) and %d (full), want %d and at most %d", len(regular.Remotes), len(full.Remotes), TopRemotes, MaxRemotes)
	}
	if got := full.States[0]; got.Count != MaxRemotes+10 {
		t.Errorf("states[0] = %+v, want every socket counted", got)
	}
}

func TestConnections_LateRunLogsNoUserNames(t *testing.T) {
	stuck := newBlockingFS(t, fstest.MapFS{}, "passwd") // a passwd that cannot be read, once the read returns
	c, logs := newRecordingCollectorWith(ownedWorld(t), stuck, SocketMount{ID: socketMount})
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(cancel)
	done := make(chan error, 1)
	go func() {
		_, err := c.Collect(ctx, runAt(0, false))
		done <- err
	}()
	select {
	case <-stuck.reached:
	case err := <-done:
		t.Fatalf("Collect() = %v before reading the passwd, want it to block there", err)
	}
	cancel()
	stuck.open()

	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Errorf("Collect() error = %v, want the context's error", err)
	}
	if got := logs.all(); len(got) != 0 {
		t.Errorf("a late run logged %+v, want nothing (its state is not kept)", got)
	}
	snapshotOf(t, c, runAt(1, false))
	wantLogged(t, logs, slog.LevelWarn, "user names unavailable", 1)
}

func TestConnections_BlockingReadIsAbandonedAsGap(t *testing.T) {
	cases := []struct{ name, blocked string }{
		{"a table", "net/tcp"},
		{"one descriptor", fdinfoName(300, 3)},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				stuck := newBlockingFS(t, ownedWorld(t), tc.blocked)
				runWithBlockedRead(t, newCollectorWith(stuck, etcOf(rootPasswd)), stuck, tc.blocked)
			})
		})
	}
}

// runWithBlockedRead runs the collector c, whose reads go through stuck, under the scheduler with the file blocked blocked and
// checks the gaps.
func runWithBlockedRead(t *testing.T, c *Connections, stuck *blockingFS, blocked string) {
	t.Helper()

	sink := &gapSink{}
	schedule := scheduler.Schedule{Interval: 10 * time.Second, Timeout: 2 * time.Second}
	jobs := []scheduler.Job{{Collector: c, Schedule: schedule}, {Collector: &okCollector{}, Schedule: schedule}}
	sched, err := scheduler.New(jobs, sink, quietLogger())
	if err != nil {
		t.Fatalf("scheduler.New() error = %v, want nil", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		sched.Run(ctx)
	}()
	t.Cleanup(func() {
		stuck.open()
		cancel()
		<-done
	})

	time.Sleep(35 * time.Second)
	synctest.Wait()

	gaps := sink.gapsOf(Name)
	if len(gaps) != 4 {
		t.Errorf("gaps of %q = %d, want 4 (the first run and the 3 skipped runs)", Name, len(gaps))
	}
	for i, gap := range gaps {
		if gap.Cause != model.GapCollectorTimeout {
			t.Errorf("gap %d has cause %q, want %q", i, gap.Cause, model.GapCollectorTimeout)
		}
	}
	if got := stuck.entered.Load(); got != 1 {
		t.Errorf("the blocked read of %s was entered %d times, want 1 (a stuck collector is not started again)", blocked, got)
	}
	if got := sink.samples(); got != 4 {
		t.Errorf("samples of the other collector = %d, want 4 (one per interval)", got)
	}
}

func TestConnections_HostileNameYieldsValidRecord(t *testing.T) {
	cases := []struct{ name, comm, cmdline string }{
		{"control characters and invalid UTF-8", "ev\x01il", "/tmp/ev\x1b[31m\x01il\xff\xfe name\x00arg\x00"},
		{"a very long word", "long", strings.Repeat("a", 3000) + "\x00"},
		{"a newline in the name", "a\nUid:\t9\t9", "a\nUid:\t9\t9\x00"},
		{"no name at all", "", ""},
	}
	w := newWorld(t)
	w.clear()
	for i, tc := range cases {
		pid, inode := int32(100+i), uint64(90000000+i)
		w.process(pid, tc.comm, tc.cmdline)
		w.addListener("tcp", "0.0.0.0:"+strconv.Itoa(3000+i), inode)
		w.holdAll(pid, inode)
	}

	rec, snap := snapshotOf(t, newCollector(w), runAt(0, false))

	checkWire(t, rec)
	oracle := procstats.NewCommandReader(w)
	for i, tc := range cases {
		pid := int32(100 + i)
		want, err := oracle.Command(pid)
		if err != nil {
			t.Fatalf("%s: the process collector's Command(%d) error = %v", tc.name, pid, err)
		}
		if want == "" || len(want) > 1024 || !utf8.ValidString(want) {
			t.Fatalf("%s: the process collector's command %q is not valid (test setup)", tc.name, want)
		}
		wantListener(t, snap, model.ProtoTCP, "0.0.0.0:"+strconv.Itoa(3000+i), pid, want, 0, "root")
		if !slices.Contains(snap.Processes, model.ProcessConnections{PID: pid, Command: want, Count: 1}) {
			t.Errorf("%s: processes = %+v, want PID %d with command %q", tc.name, snap.Processes, pid, want)
		}
	}
}

func TestConnections_LogsHoldNoProcessTextOrAddress(t *testing.T) {
	const (
		sentinelProcess = "SENTINELPROC"
		sentinelLine    = "LINECONTENT42"
		sentinelUser    = "SENTINELUSER"
	)
	namedEtc := etcOf(sentinelUser + ":x:0:0::/:/bin/sh\n" + sentinelUser + "2:x:116:116::/:/bin/sh\n" + sentinelUser + "3:x:998:998::/:/bin/sh\n")
	w := ownedWorld(t)
	w.process(200, sentinelProcess, "/usr/bin/"+sentinelProcess+"\x00")
	w.replaceLine("tcp", 5, sentinelLine+" 192.0.2.10:1 198.51.100.7:1")
	w.dropTable("tcp6")
	faulty := newFaultFS(w)
	faulty.openErr[fdinfoName(200, 3)] = syscall.EACCES
	c, logs := newRecordingCollectorWith(faulty, namedEtc, SocketMount{ID: socketMount})

	_, snap := snapshotOf(t, c, runAt(0, false))
	snapshotOf(t, c, runAt(1, false))

	if got := listenerAt(snap, model.ProtoTCP, "127.0.0.1:53"); len(got) != 1 || got[0].User != sentinelUser+"2" {
		t.Fatalf("listeners on tcp 127.0.0.1:53 = %s, want the user %q the sentinel passwd gives UID 116 (test setup)", listenersText(got), sentinelUser+"2")
	}
	for _, msg := range []string{"socket owners partly unknown", "socket lines not read", "socket tables unavailable"} {
		if len(logs.find(msg)) != 1 {
			t.Errorf("log holds %d lines %q, want 1 (test setup)", len(logs.find(msg)), msg)
		}
	}
	text := logs.text()
	for _, leak := range []string{sentinelProcess, sentinelLine, sentinelUser, "192.0.2", "198.51.100", "0A0200C0", "076433C6", "sshd", "nginx", "116", "998"} {
		if strings.Contains(text, leak) {
			t.Errorf("log holds %q, want no process text, address or line content; log:\n%s", leak, text)
		}
	}

	unknown, unknownLogs := newRecordingCollector(w, SocketMount{Err: errors.New("sockmount: socket: permission denied")})
	snapshotOf(t, unknown, runAt(0, false))
	if strings.Contains(unknownLogs.text(), "192.0.2") || strings.Contains(unknownLogs.text(), sentinelProcess) {
		t.Errorf("log of the unknown socket mount holds an address or process text:\n%s", unknownLogs.text())
	}

	// A passwd that cannot be read names its reason, never a user or a UID.
	brokenEtc := etcOf(sentinelUser + ":x:0:0::/:/bin/sh\nx:x:116:" + strings.Repeat("g", 5000) + "\n")
	unnamed, unnamedLogs := newRecordingCollectorWith(w, brokenEtc, SocketMount{ID: socketMount})
	snapshotOf(t, unnamed, runAt(0, false))
	if len(unnamedLogs.find("user names unavailable")) != 1 {
		t.Fatalf("log holds %d lines %q, want 1 (test setup)", len(unnamedLogs.find("user names unavailable")), "user names unavailable")
	}
	for _, leak := range []string{sentinelUser, "116", "192.0.2"} {
		if strings.Contains(unnamedLogs.text(), leak) {
			t.Errorf("log of the unreadable passwd holds %q, want no user name, UID or address; log:\n%s", leak, unnamedLogs.text())
		}
	}
}

// checkWire runs a record through the producer check of the wire format, as the scheduler does.
func checkWire(t *testing.T, rec model.Record) {
	t.Helper()

	rec.Origin, rec.Seq = model.OriginAgent, 1
	n, err := wire.CheckRecord(&rec)
	if err != nil {
		t.Errorf("wire.CheckRecord() error = %v, want nil", err)
	}
	if limit := wire.DefaultLimits().MaxLineBytes; n > limit+1 {
		t.Errorf("wire.CheckRecord() = %d bytes, want at most %d", n, limit+1)
	}
}

func TestConnections_RecordPassesWireCheck(t *testing.T) {
	w := ownedWorld(t)
	c := newCollector(w)

	first, _ := snapshotOf(t, c, runAt(0, false))
	regular, _ := snapshotOf(t, c, runAt(1, false))
	w.addListener("tcp", newBackdoor, 99000001)
	reported, snap := snapshotOf(t, c, runAt(2, false))
	pressed, _ := snapshotOf(t, c, runAt(3, true))

	for name, rec := range map[string]model.Record{"first": first, "regular": regular, "new listener": reported, "pressure": pressed} {
		t.Run(name, func(t *testing.T) {
			checkWire(t, rec)
		})
	}
	if len(snap.NewListeners) != 1 {
		t.Errorf("new listeners = %+v, want the one the wire check covers", snap.NewListeners)
	}
}

// allowedFiles are the names the collector may open (AC11).
var allowedFiles = regexp.MustCompile(`^(\.|net/(tcp|tcp6|udp|udp6)|[1-9][0-9]*/(fdinfo|fdinfo/[0-9]+|stat|cmdline))$`)

func TestConnections_ReadsOnlyNamedFiles(t *testing.T) {
	rec := &recordingFS{FS: ownedWorld(t)}
	c := newCollector(rec)

	snapshotOf(t, c, runAt(0, false))
	snapshotOf(t, c, runAt(1, true))

	seen := map[string]bool{}
	for _, name := range rec.opened() {
		if !allowedFiles.MatchString(name) {
			t.Errorf("collector opened %q, want only /proc, net/{tcp,tcp6,udp,udp6}, <pid>/fdinfo and its files, <pid>/stat and <pid>/cmdline", name)
		}
		seen[name] = true
	}
	for _, name := range []string{".", "net/tcp", "net/tcp6", "net/udp", "net/udp6", "100/fdinfo", fdinfoName(100, 3), "100/stat", "100/cmdline"} {
		if !seen[name] {
			t.Errorf("collector never opened %q, want it read", name)
		}
	}
}

func TestConnections_ListenerFloodCausesNoFullSnapshot(t *testing.T) {
	w := newWorld(t)
	w.clear()
	w.addListeners("0.0.0.0", 3000, 4100, 89000000) // 4,100 unprivileged listeners held across runs
	c := newCollector(w)
	snapshotOf(t, c, runAt(0, false))

	w.addListener("tcp", "0.0.0.0:2222", 89900001)
	_, unprivileged := snapshotOf(t, c, runAt(1, false))
	w.addListener("tcp", "0.0.0.0:23", 89900002)
	_, privileged := snapshotOf(t, c, runAt(2, false))

	wantFlags(t, unprivileged, true, false, true, model.ListenerCheckPrivileged)
	wantInventory(t, unprivileged, model.InventoryPrivileged, allTableProtos...)
	wantNewListeners(t, unprivileged, 0)
	if len(unprivileged.Listeners) != 4096 {
		t.Errorf("listeners = %d entries, want the 4,096 lowest", len(unprivileged.Listeners))
	}
	wantFlags(t, privileged, false, false, true, model.ListenerCheckPrivileged)
	wantInventory(t, privileged, model.InventoryPrivileged, allTableProtos...)
	wantNewListeners(t, privileged, 0, rootListener(model.ProtoTCP, "0.0.0.0:23"))
}

// newTableGen returns the generated IPv4 table name of n sockets.
func newTableGen(t *testing.T, name string, n uint32, spec func(i uint32) sockSpec) *tableGen {
	t.Helper()

	return &tableGen{header: []byte(tableHeader(t, serverNetDir, name) + "\n"), tp: newLineTemplate(t, name), n: n, spec: spec}
}

// Sizes and limits of the bounded-memory test (plan fact 17).
const (
	liveSlack            = 1 << 20  // bytes the live data of a run may differ between the two sizes
	liveLimit            = 16 << 20 // bytes of live data of one run, also at its peak with the user map of a 1 MiB passwd
	garbagePerLine       = 16.0     // bytes allocated per extra table line
	garbagePerDescriptor = 1024.0   // bytes allocated per extra descriptor
	maxListEntries       = 4096
)

// memRun is what one run held and allocated.
type memRun struct {
	live  float64 // heap in use at PID 1's last descriptor read (the inode index and owner map alive), less the heap before the run, after a collection
	peak  float64 // the larger of live and the same measure when the passwd read ends (the user map alive), the run's peak
	alloc float64 // bytes allocated by the run
	snap  *model.ConnectionSnapshot
}

// lineSpec is the i-th socket of the generated table: listeners and connections of distinct endpoints and inodes.
func lineSpec(i uint32) sockSpec {
	local := netip.AddrPortFrom(netip.AddrFrom4(addr4(0x0A000000+i)), 8000)
	if i%2 == 0 {
		return sockSpec{local: local, remote: netip.AddrPortFrom(netip.IPv4Unspecified(), 0), state: stateListen, inode: 10000000 + uint64(i)}
	}
	return sockSpec{
		local:  netip.AddrPortFrom(netip.AddrFrom4(addr4(0x0A000000+i)), 443),
		remote: netip.AddrPortFrom(netip.AddrFrom4(addr4(0x64400000+i)), 443),
		state:  stateEstablished,
		inode:  10000000 + uint64(i),
	}
}

// closeHookFS runs hook when the file name is closed.
type closeHookFS struct {
	fs.FS
	name string
	hook func()
}

func (c *closeHookFS) Open(name string) (fs.File, error) {
	f, err := c.FS.Open(name)
	if err != nil || name != c.name {
		return f, err
	}
	return &closeHookFile{File: f, hook: c.hook}, nil
}

// closeHookFile runs hook once when it is closed.
type closeHookFile struct {
	fs.File
	hook func()
}

func (f *closeHookFile) Close() error {
	f.hook()
	return f.File.Close()
}

// settleHeap collects twice, so that objects a first collection only queued for finalization or sweeping no longer count as live.
func settleHeap() {
	runtime.GC()
	runtime.GC()
}

// measureRun runs the collector once over g and etc and measures the live data when the collector opens hookName, the last fdinfo
// file it reads for PID 1, when the inode index and the owner map are still referenced, and when it closes the passwd, when the
// user map is referenced as well (the run's peak), and the bytes the run allocated.
func measureRun(t *testing.T, g *genFS, hookName string, etc fs.FS) memRun {
	t.Helper()

	var before, mid, end, after runtime.MemStats
	g.hookName = hookName
	g.hook = func() {
		settleHeap()
		runtime.ReadMemStats(&mid)
	}
	passwdRead := false
	etc = &closeHookFS{FS: etc, name: "passwd", hook: func() {
		passwdRead = true
		settleHeap()
		runtime.ReadMemStats(&end)
	}}
	c := newCollectorWith(g, etc)
	settleHeap()
	runtime.ReadMemStats(&before)
	_, snap := snapshotOf(t, c, runAt(0, false))
	runtime.ReadMemStats(&after)
	if !g.hooked || !passwdRead {
		t.Fatalf("collector never opened %s (%v) or closed the passwd (%v), want the last descriptor of PID 1 and the passwd read", hookName, g.hooked, passwdRead)
	}
	return memRun{
		live:  float64(mid.HeapAlloc) - float64(before.HeapAlloc),
		peak:  max(float64(mid.HeapAlloc), float64(end.HeapAlloc)) - float64(before.HeapAlloc),
		alloc: float64(after.TotalAlloc) - float64(before.TotalAlloc),
		snap:  snap,
	}
}

// measureTableLines runs over a generated table of n lines and one process that holds five of its sockets.
func measureTableLines(t *testing.T, n uint32, etc fs.FS) memRun {
	t.Helper()

	w := newWorld(t)
	w.clear()
	w.process(1, "init", "/sbin/init\x00")
	w.holdRange(1, 3, 10000000, 5)
	return measureRun(t, &genFS{base: w, tables: map[string]*tableGen{"net/tcp": newTableGen(t, "tcp", n, lineSpec)}}, "1/fdinfo/7", etc)
}

// measureDescriptors runs over a table of 2,000 listeners and one process with n generated descriptors, half of them sockets of the table.
func measureDescriptors(t *testing.T, n uint32, etc fs.FS) memRun {
	t.Helper()

	w := newWorld(t)
	w.clear()
	w.addListeners("0.0.0.0", 2000, 2000, 10000000)
	w.process(1, "init", "/sbin/init\x00")
	fds := &fdGen{dir: "1/fdinfo", count: n, inode: func(i uint32) uint64 { return 10000000 + uint64(i%4000) }}
	// fdGen names the descriptors 3 to count+2 and the collector reads them all.
	return measureRun(t, &genFS{base: w, fds: fds}, fds.dir+"/"+strconv.Itoa(2+int(n)), etc)
}

// checkBounded checks that the live data did not grow from the small to the large run and that the garbage per extra unit stays small.
func checkBounded(t *testing.T, unit string, small, large memRun, extra, maxGarbage float64) {
	t.Helper()

	if grown := large.live - small.live; grown > liveSlack {
		t.Errorf("live data of the large run is %.0f bytes above the small run's (%.0f and %.0f), want at most %d (it must not grow with the %s count)", grown, large.live, small.live, liveSlack, unit)
	}
	if large.live > liveLimit {
		t.Errorf("live data of the large run = %.0f bytes, want at most %d", large.live, liveLimit)
	}
	if grown := large.peak - small.peak; grown > liveSlack {
		t.Errorf("peak of the large run is %.0f bytes above the small run's (%.0f and %.0f), want at most %d (it must not grow with the %s count)", grown, large.peak, small.peak, liveSlack, unit)
	}
	if large.peak > liveLimit {
		t.Errorf("peak of the large run = %.0f bytes, want at most %d", large.peak, liveLimit)
	}
	if per := (large.alloc - small.alloc) / extra; per > maxGarbage {
		t.Errorf("garbage per extra %s = %.1f bytes, want at most %.0f", unit, per, maxGarbage)
	}
	for _, snap := range []*model.ConnectionSnapshot{small.snap, large.snap} {
		for name, n := range map[string]int{"states": len(snap.States), "processes": len(snap.Processes), "remotes": len(snap.Remotes),
			"listeners": len(snap.Listeners), "connections": len(snap.Connections), "new_listeners": len(snap.NewListeners)} {
			if n > maxListEntries {
				t.Errorf("%s holds %d entries, want at most %d", name, n, maxListEntries)
			}
		}
	}
}

// totalCounted adds the counts of the states.
func totalCounted(snap *model.ConnectionSnapshot) uint64 {
	var total uint64
	for _, s := range snap.States {
		total += uint64(s.Count)
	}
	return total
}

// shortLinesEtc returns an /etc whose passwd is as large as the agent reads it (1 MiB) and holds the most distinct UIDs: the shortest
// lines "a::N" in ascending order.
func shortLinesEtc(t *testing.T) fs.FS {
	t.Helper()

	var b strings.Builder
	b.Grow(1 << 20)
	n := 0
	for uid := 0; ; uid++ {
		line := "a::" + strconv.Itoa(uid) + "\n"
		if b.Len()+len(line) > 1<<20 {
			break
		}
		b.WriteString(line)
		n++
	}
	users, err := procstats.ReadUsers(etcOf(b.String()))
	if err != nil || len(users) != n {
		t.Fatalf("ReadUsers(short lines) = %d users, %v, want %d users and no error (test setup)", len(users), err, n)
	}
	t.Logf("passwd of %d bytes with %d users", b.Len(), n)
	return etcOf(b.String())
}

func TestConnections_ManySocketsStayWithinCaps(t *testing.T) {
	etcs := []struct {
		name         string
		etc          fs.FS
		small, large uint32
	}{
		{"table lines", fixtureEtc(), 100000, 400000},
		{"table lines with a 1 MiB passwd of short lines", shortLinesEtc(t), 100000, 200000}, // the passwd costs the time, not the lines
	}
	for _, e := range etcs {
		t.Run(e.name, func(t *testing.T) {
			small, large := measureTableLines(t, e.small, e.etc), measureTableLines(t, e.large, e.etc)
			extra := float64(e.large - e.small)

			t.Logf("small run: live %.0f, peak %.0f, allocated %.0f bytes; large run: live %.0f, peak %.0f, allocated %.0f bytes; garbage per extra line %.2f bytes",
				small.live, small.peak, small.alloc, large.live, large.peak, large.alloc, (large.alloc-small.alloc)/extra)
			checkBounded(t, "table line", small, large, extra, garbagePerLine)
			for _, run := range []struct {
				snap *model.ConnectionSnapshot
				want uint64
			}{{small.snap, uint64(e.small)}, {large.snap, uint64(e.large)}} {
				if got := totalCounted(run.snap); got != run.want || run.snap.Complete || !run.snap.AggregatesIncomplete {
					t.Errorf("states count %d sockets, complete = %v, aggregates_incomplete = %v, want every one of %d counted, not complete and incomplete aggregates",
						got, run.snap.Complete, run.snap.AggregatesIncomplete, run.want)
				}
			}
		})
	}

	t.Run("descriptors of one process", func(t *testing.T) {
		small, large := measureDescriptors(t, 50000, fixtureEtc()), measureDescriptors(t, 200000, fixtureEtc())

		t.Logf("small run: live %.0f, peak %.0f, allocated %.0f bytes; large run: live %.0f, peak %.0f, allocated %.0f bytes; garbage per extra descriptor %.2f bytes",
			small.live, small.peak, small.alloc, large.live, large.peak, large.alloc, (large.alloc-small.alloc)/150000)
		checkBounded(t, "descriptor", small, large, 150000, garbagePerDescriptor)
		for _, snap := range []*model.ConnectionSnapshot{small.snap, large.snap} {
			wantList(t, "processes", snap.Processes, []model.ProcessConnections{{PID: 1, Command: "init", Count: 2000}})
		}
	})
}

// benchmarkProc builds the benchmark's /proc: the captured server tables and 300 processes of 30 descriptors, three of them sockets of the tables.
func benchmarkProc(b *testing.B) fs.FS {
	b.Helper()

	files := fstest.MapFS{}
	for _, name := range tableNames {
		data, err := os.ReadFile(filepath.Join(serverNetDir, name))
		if err != nil {
			b.Fatalf("reading the fixture %s: %v", name, err)
		}
		files["net/"+name] = &fstest.MapFile{Data: data}
	}
	inodes := []uint64{16363918, 16364675, 16364676, 16366396, 16366506, 16366497, 16364553, 16364552, 16955396, 17280839, 16364551, 16364547}
	for pid := 1; pid <= 300; pid++ {
		n := strconv.Itoa(pid)
		files[n+"/stat"] = &fstest.MapFile{Data: []byte(n + " (worker) S " + strings.Repeat("0 ", 48) + "0\n")}
		files[n+"/cmdline"] = &fstest.MapFile{Data: []byte("worker job" + n + "\x00")}
		for fd := 3; fd < 33; fd++ {
			content := fdinfoText(16, 6090) // a pipe
			if fd%10 == 3 {
				content = fdinfoText(socketMount, inodes[(pid*7+fd)%len(inodes)])
			}
			files[n+"/fdinfo/"+strconv.Itoa(fd)] = &fstest.MapFile{Data: []byte(content)}
		}
	}
	return files
}

// fdinfoText writes the first lines of an fdinfo file.
func fdinfoText(mount int32, ino uint64) string {
	return "pos:\t0\nflags:\t02000002\nmnt_id:\t" + strconv.Itoa(int(mount)) + "\nino:\t" + strconv.FormatUint(ino, 10) + "\n"
}

// BenchmarkConnections_Collect measures one run over the captured server tables and 300 processes with 30 descriptors each; every
// second run is a full snapshot. It asserts no duration; docs/BENCHMARKS.md holds the result.
func BenchmarkConnections_Collect(b *testing.B) {
	c := newCollector(benchmarkProc(b))
	ctx := context.Background()

	i := 0
	for b.Loop() {
		if _, err := c.Collect(ctx, runAt(i, i%2 == 0)); err != nil {
			b.Fatalf("Collect() error = %v, want nil", err)
		}
		i++
	}
}

// allowedImports are the packages the non-test files of internal/connstats import (AC11): no program, socket, cgo or unsafe.
var allowedImports = []string{
	"bufio", "bytes", "cmp", "container/heap", "context", "encoding/binary", "encoding/json", "errors", "fmt", "io", "io/fs",
	"log/slog", "math", "net/netip", "os", "slices", "sort", "strconv", "strings", "sync", "time",
	"github.com/LarsLaskowski/Vandox/internal/model", "github.com/LarsLaskowski/Vandox/internal/procstats",
	"github.com/LarsLaskowski/Vandox/internal/scheduler", "github.com/LarsLaskowski/Vandox/internal/wire",
}

// allowedSelectors maps a package to the only selectors of it the non-test files may use: os only for DirFS, no syscall at all.
var allowedSelectors = map[string][]string{
	"os":      {"DirFS"},
	"syscall": {},
}

// restrictedNames returns the local name of every import of file whose path is a key of allowedSelectors, and the violations for
// dot imports of such a package; a blank import binds no name and is fine.
func restrictedNames(name string, file *ast.File) (names map[string]string, violations []string) {
	names = map[string]string{}
	for _, imp := range file.Imports {
		path, err := strconv.Unquote(imp.Path.Value)
		if _, restricted := allowedSelectors[path]; err != nil || !restricted {
			continue
		}
		local := path[strings.LastIndex(path, "/")+1:]
		if imp.Name != nil {
			local = imp.Name.Name
		}
		switch local {
		case "_":
		case ".":
			violations = append(violations, name+": dot import of "+path+" is not allowed")
		default:
			names[local] = path
		}
	}
	return names, violations
}

// disallowedSelectors returns the uses in file of selectors of a restricted package, under whatever local name it is imported, that
// allowedSelectors does not list, and the dot imports of a restricted package.
func disallowedSelectors(name string, file *ast.File) []string {
	names, violations := restrictedNames(name, file)
	ast.Inspect(file, func(n ast.Node) bool {
		sel, ok := n.(*ast.SelectorExpr)
		if !ok {
			return true
		}
		id, ok := sel.X.(*ast.Ident)
		if !ok {
			return true
		}
		path, bound := names[id.Name]
		if bound && id.Obj == nil && !slices.Contains(allowedSelectors[path], sel.Sel.Name) {
			violations = append(violations, name+": "+id.Name+"."+sel.Sel.Name+" is not allowed")
		}
		return true
	})
	return violations
}

// disallowedImports returns the imports in file that allowedImports does not list.
func disallowedImports(name string, file *ast.File) []string {
	var violations []string
	for _, imp := range file.Imports {
		path, err := strconv.Unquote(imp.Path.Value)
		if err != nil || !slices.Contains(allowedImports, path) {
			violations = append(violations, name+": imports "+imp.Path.Value+", want only "+strings.Join(allowedImports, " "))
		}
	}
	return violations
}

func TestConnstats_ImportsOnlyAllowedPackages(t *testing.T) {
	files, err := filepath.Glob("*.go")
	if err != nil || len(files) == 0 {
		t.Fatalf("Glob(*.go) = %v, %v, want the package's files", files, err)
	}
	checked := 0
	for _, name := range files {
		if strings.HasSuffix(name, "_test.go") {
			continue
		}
		checked++
		file, err := parser.ParseFile(token.NewFileSet(), name, nil, 0)
		if err != nil {
			t.Fatalf("parsing %s: %v", name, err)
		}
		for _, v := range append(disallowedImports(name, file), disallowedSelectors(name, file)...) {
			t.Error(v)
		}
	}
	if checked < 4 {
		t.Errorf("checked %d production files, want the collector's files", checked)
	}
}

func TestNew_TakesDefaults(t *testing.T) {
	c := New(Sources{}, nil)

	if c.src.Proc == nil || c.src.Etc == nil || c.log == nil || c.Name() != Name {
		t.Errorf("New(Sources{}, nil) = proc %v, etc %v, log %v, name %q, want the host's /proc and /etc, a logger and %q", c.src.Proc, c.src.Etc, c.log, c.Name(), Name)
	}
	w := ownedWorld(t)
	quiet := New(Sources{Proc: w, Etc: fixtureEtc(), SocketMount: SocketMount{ID: socketMount}}, nil)
	snapshotOf(t, quiet, runAt(0, false)) // logs through the default logger without failing
}

func TestConnections_SocketMountNotLearnedLeavesNoOwners(t *testing.T) {
	rec := &recordingFS{FS: ownedWorld(t)}
	c, logs := newRecordingCollector(rec, SocketMount{})

	_, snap := snapshotOf(t, c, runAt(0, false))
	snapshotOf(t, c, runAt(1, false))

	for _, name := range rec.opened() {
		if strings.Contains(name, "fdinfo") {
			t.Errorf("collector opened %q, want no fdinfo without a learned socket mount", name)
		}
	}
	if len(snap.Processes) != 0 || !snap.AggregatesIncomplete {
		t.Errorf("%d processes, aggregates_incomplete = %v, want none and true", len(snap.Processes), snap.AggregatesIncomplete)
	}
	warns := wantLogged(t, logs, slog.LevelWarn, "socket owners unknown", 1)
	if len(warns) == 1 && warns[0].attrs["err"] != "connstats: socket mount not learned" {
		t.Errorf("warning err = %q, want %q", warns[0].attrs["err"], "connstats: socket mount not learned")
	}
}

func TestConnections_SkippedLineMakesAggregatesIncomplete(t *testing.T) {
	w := newWorld(t)
	w.replaceLine("tcp", 3, "garbage")

	_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

	wantFlags(t, snap, false, false, true, model.ListenerCheckStart)
	wantInventory(t, snap, model.InventorySkipped, allTableProtos...)
}

func TestConnections_LateRunLogsNothing(t *testing.T) {
	w := ownedWorld(t)
	last := newBlockingFS(t, w, fdinfoName(601, 3))
	c, logs := newRecordingCollector(last, SocketMount{ID: socketMount})
	w.dropTable("tcp6") // a change the first kept run would log
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(cancel)
	done := make(chan error, 1)
	go func() {
		_, err := c.Collect(ctx, runAt(0, false))
		done <- err
	}()
	select {
	case <-last.reached:
	case err := <-done:
		t.Fatalf("Collect() = %v before reading the last descriptor, want it to block there", err)
	}
	cancel()
	last.open()

	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Errorf("Collect() error = %v, want the context's error", err)
	}
	if got := logs.all(); len(got) != 0 {
		t.Errorf("a late run logged %+v, want nothing (its state is not kept)", got)
	}
	snapshotOf(t, c, runAt(1, false))
	wantLogged(t, logs, slog.LevelInfo, "socket tables unavailable", 1)
}

func TestConnections_CommandLookupErrors(t *testing.T) {
	t.Run("an ended process is dropped silently", func(t *testing.T) {
		w := ownedWorld(t)
		delete(w.files, "100/stat")
		c, logs := newRecordingCollector(w, SocketMount{ID: socketMount})

		_, snap := snapshotOf(t, c, runAt(0, false))

		if slices.ContainsFunc(snap.Processes, func(p model.ProcessConnections) bool { return p.PID == 100 }) {
			t.Errorf("processes = %+v, want none for the ended PID 100", snap.Processes)
		}
		wantListener(t, snap, model.ProtoTCP, sshdListener, 0, "", 0, "root")
		wantLogged(t, logs, slog.LevelWarn, "socket owners partly unknown", 0)
	})

	t.Run("an unreadable process is dropped and counted", func(t *testing.T) {
		w := ownedWorld(t)
		faulty := newFaultFS(w)
		faulty.openErr["100/stat"] = syscall.EACCES
		c, logs := newRecordingCollector(faulty, SocketMount{ID: socketMount})

		_, snap := snapshotOf(t, c, runAt(0, false))

		if slices.ContainsFunc(snap.Processes, func(p model.ProcessConnections) bool { return p.PID == 100 }) {
			t.Errorf("processes = %+v, want none for the unreadable PID 100", snap.Processes)
		}
		wantListener(t, snap, model.ProtoTCP, sshdListener, 0, "", 0, "root")
		if !snap.AggregatesIncomplete {
			t.Error("aggregates_incomplete = false, want true")
		}
		warns := wantLogged(t, logs, slog.LevelWarn, "socket owners partly unknown", 1)
		if len(warns) == 1 && (warns[0].attrs["processes"] != "1" || !strings.Contains(warns[0].attrs["err"], "100/stat") || !strings.Contains(warns[0].attrs["err"], "permission denied")) {
			t.Errorf("warning attributes = %v, want processes=1 and the procstats error of 100/stat", warns[0].attrs)
		}
	})
}

// plainFile is a file that is no directory.
type plainFile struct{ fs.File }

// fdinfoFileFS answers the fdinfo directory of PID 10 with a plain file.
type fdinfoFileFS struct{ fs.FS }

func (f fdinfoFileFS) Open(name string) (fs.File, error) {
	if name == "10/fdinfo" {
		file, err := f.FS.Open("10/fdinfo/3")
		return plainFile{file}, err
	}
	return f.FS.Open(name)
}

func TestScanOwners_DirectoryThatCannotBeListedIsUnreadable(t *testing.T) {
	p := newProcTree(t)
	p.add(10, 3, socketMount, 100)

	_, unreadable, err := scan(t, fdinfoFileFS{p.files}, []int32{10}, indexOf(100))

	if unreadable != 1 {
		t.Errorf("scanOwners() unreadable = %d, want 1", unreadable)
	}
	wantErrPrefix(t, err, "connstats: 10/fdinfo: ")
}

// plainErrorProc fails the open of one table with an error that is no path error.
type plainErrorProc struct{ fs.FS }

func (p plainErrorProc) Open(name string) (fs.File, error) {
	if name == "net/udp" {
		return nil, errors.New("boom")
	}
	return p.FS.Open(name)
}

func TestConnections_PlainTableErrorFails(t *testing.T) {
	records, err := newCollector(plainErrorProc{newWorld(t)}).Collect(context.Background(), runAt(0, false))

	if err == nil || err.Error() != "connstats: net/udp: boom" || len(records) != 0 {
		t.Errorf("Collect() = %d records, %v, want no records and the error %q", len(records), err, "connstats: net/udp: boom")
	}
}

func TestConnections_LastTableLineWithoutNewline(t *testing.T) {
	w := newWorld(t)
	w.clear()
	w.addListener("tcp", "0.0.0.0:22", 1)
	w.dirty = false
	w.flush()
	file := w.files["net/tcp"]
	file.Data = []byte(strings.TrimSuffix(string(file.Data), "\n"))

	_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

	wantList(t, "states", snap.States, []model.StateCount{{Proto: model.ProtoTCP, State: "LISTEN", Count: 1}})
}

func TestFitRecord_ShortensUntilItFits(t *testing.T) {
	cmd := longCommand
	build := func() *model.ConnectionSnapshot {
		snap := &model.ConnectionSnapshot{}
		for i := range uint16(4000) {
			local := netip.AddrPortFrom(netip.AddrFrom4(addr4(0x0A000000+uint32(i))), 443)
			snap.Connections = append(snap.Connections, model.Connection{Proto: model.ProtoTCP, Local: local, Remote: local, State: "ESTABLISHED", PID: 1, Command: cmd})
			snap.Listeners = append(snap.Listeners, model.Listener{Proto: model.ProtoTCP, Local: local, PID: 1, Command: cmd})
			snap.Processes = append(snap.Processes, model.ProcessConnections{PID: int32(i) + 1, Command: cmd, Count: 1})
		}
		return snap
	}
	t.Run("connections first", func(t *testing.T) {
		snap := build()
		snap.Listeners, snap.Processes = nil, nil
		snap.Complete = true

		rec, err := fitRecord(runBase, snap, inv())

		if err != nil || snap.Complete || len(snap.Connections) >= 4000 || len(snap.Connections) == 0 {
			t.Fatalf("fitRecord() = %v, complete %v, %d connections, want a halved list and not complete", err, snap.Complete, len(snap.Connections))
		}
		checkWire(t, rec)
	})
	t.Run("then processes and listeners", func(t *testing.T) {
		snap := build()
		snap.Connections = nil

		rec, err := fitRecord(runBase, snap, inv())

		if err != nil || !snap.AggregatesIncomplete || len(snap.Processes) >= 4000 || len(snap.Listeners) >= 4000 {
			t.Fatalf("fitRecord() = %v, aggregates_incomplete %v, %d processes, %d listeners, want both halved and incomplete aggregates",
				err, snap.AggregatesIncomplete, len(snap.Processes), len(snap.Listeners))
		}
		checkWire(t, rec)
	})
	t.Run("an invalid snapshot is refused", func(t *testing.T) {
		_, err := fitRecord(runBase, &model.ConnectionSnapshot{States: []model.StateCount{{Proto: "icmp", Count: 1}}}, inv())

		if err == nil || !strings.HasPrefix(err.Error(), "connstats: record refused: ") {
			t.Errorf("fitRecord() error = %v, want one starting with %q", err, "connstats: record refused: ")
		}
	})
}

// tcpKeysOn returns count TCP listener keys on 10.0.0.1 from port first on.
func tcpKeysOn(first, count uint16) []listenerKey {
	keys := make([]listenerKey, 0, count)
	for i := range count {
		keys = append(keys, key(model.ProtoTCP, "10.0.0.1", first+i))
	}
	return keys
}

// listedSnapshot returns a snapshot that lists every key with a long command.
func listedSnapshot(keys []listenerKey) *model.ConnectionSnapshot {
	snap := &model.ConnectionSnapshot{Tables: []model.Proto{model.ProtoTCP}}
	for _, k := range keys {
		snap.Listeners = append(snap.Listeners, model.Listener{Proto: k.proto, Local: netip.AddrPortFrom(k.addr, k.port), PID: 1, Command: longCommand})
	}
	return snap
}

func TestFitRecord_SetsTheInventoryState(t *testing.T) {
	// 2,500 unprivileged keys follow the privileged ones; the record lists all of them with a long command and is cut by its size.
	cases := []struct {
		name       string
		privileged uint16
		want       model.InventoryState
	}{
		{"the privileged keys all stay", 10, model.InventoryPrivileged},
		{"the cut reaches into the privileged keys", 1000, model.InventoryCut},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			keys := append(tcpKeysOn(1, c.privileged), tcpKeysOn(2000, 2500)...)
			snap := listedSnapshot(keys)

			rec, err := fitRecord(runBase, snap, &inventory{keys: keys, tables: 0b0001})

			if err != nil || len(snap.Listeners) >= len(keys) {
				t.Fatalf("fitRecord() = %v, %d listeners of %d, want a cut list (the precondition of this case)", err, len(snap.Listeners), len(keys))
			}
			if snap.Inventory != c.want {
				t.Errorf("inventory = %q after %d of %d listeners were kept, want %q", snap.Inventory, len(snap.Listeners), len(keys), c.want)
			}
			checkWire(t, rec)
		})
	}
}

func TestFitRecord_SetsTheInventoryStateWithoutACut(t *testing.T) {
	t.Run("an uncut list of an uncut inventory is all", func(t *testing.T) {
		keys := tcpKeysOn(22, 2)
		snap := listedSnapshot(keys)

		_, err := fitRecord(runBase, snap, &inventory{keys: keys, tables: allTables})

		if err != nil || snap.Inventory != model.InventoryAll {
			t.Errorf("fitRecord() = %v, inventory %q, want nil and %q", err, snap.Inventory, model.InventoryAll)
		}
	})
	t.Run("a skipped line", func(t *testing.T) {
		snap := &model.ConnectionSnapshot{}

		_, err := fitRecord(runBase, snap, &inventory{skipped: true, tables: allTables})

		if err != nil || snap.Inventory != model.InventorySkipped {
			t.Errorf("fitRecord() = %v, inventory %q, want nil and %q", err, snap.Inventory, model.InventorySkipped)
		}
	})
}

func TestEntrySize_RefusesWhatCannotBeEncoded(t *testing.T) {
	if n, ok := entrySize(make(chan int)); ok || n != 0 {
		t.Errorf("entrySize(chan) = %d, %v, want 0 and false", n, ok)
	}
	if n, ok := entrySize(model.StateCount{Proto: model.ProtoTCP, State: "LISTEN", Count: 1}); !ok || n < 10 {
		t.Errorf("entrySize(state) = %d, %v, want its encoded size", n, ok)
	}
}

func TestDisallowedSelectors_ReportsEverySelectorOutsideTheAllowList(t *testing.T) {
	src := "package p\n\nimport (\n\t\"os\"\n\t\"syscall\"\n)\n\nvar _ = os.DirFS\nvar _ = os.Open\nvar _ = syscall.EACCES\n"
	file, err := parser.ParseFile(token.NewFileSet(), "p.go", src, 0)
	if err != nil {
		t.Fatalf("parsing the sample: %v", err)
	}

	got := disallowedSelectors("p.go", file)

	want := []string{"p.go: os.Open is not allowed", "p.go: syscall.EACCES is not allowed"}
	if !slices.Equal(got, want) {
		t.Errorf("disallowedSelectors() = %q, want %q", got, want)
	}
}

func TestDisallowedSelectors_ResolvesTheLocalNameOfTheImport(t *testing.T) {
	tests := []struct {
		name string
		src  string
		want []string
	}{
		{"aliased forbidden selector", "package p\n\nimport o \"os\"\n\nvar _ = o.Open\n", []string{"p.go: o.Open is not allowed"}},
		{"aliased allowed selector", "package p\n\nimport o \"os\"\n\nvar _ = o.DirFS\n", nil},
		{"dot import", "package p\n\nimport . \"syscall\"\n\nvar _ = EACCES\n", []string{"p.go: dot import of syscall is not allowed"}},
		{"blank import", "package p\n\nimport _ \"os\"\n", nil},
		{"local variable named like the package", "package p\n\nimport \"fmt\"\n\ntype t struct{ Open int }\n\nfunc f() int {\n\tos := t{}\n\t_ = fmt.Sprint()\n\treturn os.Open\n}\n", nil},
		{"alias shadowing another restricted name", "package p\n\nimport os \"syscall\"\n\nvar _ = os.EIO\n", []string{"p.go: os.EIO is not allowed"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			file, err := parser.ParseFile(token.NewFileSet(), "p.go", tt.src, 0)
			if err != nil {
				t.Fatalf("parsing the sample: %v", err)
			}

			got := disallowedSelectors("p.go", file)

			if !slices.Equal(got, tt.want) {
				t.Errorf("disallowedSelectors() = %q, want %q", got, tt.want)
			}
		})
	}
}

// killsFor returns n distinct kills for the triggered runs.
func killsFor(n int) []model.KillRef {
	kills := make([]model.KillRef, n)
	pid := int32(900)
	for i := range kills {
		kills[i] = model.KillRef{BootID: "0f1e2d3c-4b5a-4968-8776-655443322110", KernelTime: time.Duration(i+1) * time.Second, VictimPID: pid}
		pid++
	}
	return kills
}

// triggeredAt returns the i-th run, started by the kills with omitted more kills beyond them.
func triggeredAt(i int, pressure bool, kills []model.KillRef, omitted uint32) scheduler.Run {
	run := runAt(i, pressure)
	run.Trigger = &scheduler.Trigger{Kills: kills, KillsOmitted: omitted}
	return run
}

func TestConnections_TriggeredRunIsFullAndNamesKills(t *testing.T) {
	tests := []struct {
		name     string
		pressure bool
		kills    []model.KillRef
		omitted  uint32
	}{
		{"one kill without pressure", false, killsFor(1), 0},
		{"the most kills with omitted ones", false, killsFor(model.MaxTriggerKills), 7},
		{"one kill under pressure", true, killsFor(1), 0},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			w := ownedWorld(t)
			c := newCollector(w)
			snapshotOf(t, c, runAt(0, false))
			_, regular := snapshotOf(t, c, runAt(1, false))

			rec, snap := snapshotOf(t, c, triggeredAt(2, tc.pressure, tc.kills, tc.omitted))

			wantFlags(t, regular, true, false, false, model.ListenerCheckAll)
			wantFlags(t, snap, false, true, false, model.ListenerCheckAll)
			wantConnections(t, "connections of the triggered run", snap.Connections, ownedConnections)
			wantList(t, "remotes of the triggered run", snap.Remotes, ownedRemotes)
			if len(snap.Listeners) != ownedListeners {
				t.Errorf("listeners of the triggered run = %d, want %d", len(snap.Listeners), ownedListeners)
			}
			if snap.Trigger == nil {
				t.Fatalf("snapshot trigger = nil, want %d kills and %d omitted", len(tc.kills), tc.omitted)
			}
			if !slices.Equal(snap.Trigger.Kills, tc.kills) || snap.Trigger.KillsOmitted != tc.omitted {
				t.Errorf("snapshot trigger = %+v, want kills %+v and %d omitted", snap.Trigger, tc.kills, tc.omitted)
			}
			if err := snap.Validate(); err != nil {
				t.Errorf("Validate() of the triggered snapshot = %v, want nil", err)
			}
			checkWire(t, rec)
		})
	}
}

func TestConnections_TriggeredFirstRunNamesKills(t *testing.T) {
	kills := killsFor(2)

	_, snap := snapshotOf(t, newCollector(ownedWorld(t)), triggeredAt(0, false, kills, 3))

	wantFlags(t, snap, false, true, false, model.ListenerCheckStart)
	if snap.Trigger == nil || !slices.Equal(snap.Trigger.Kills, kills) || snap.Trigger.KillsOmitted != 3 {
		t.Errorf("snapshot trigger = %+v, want kills %+v and 3 omitted", snap.Trigger, kills)
	}
}

func TestConnections_TriggeredRunKeepsTheListenerComparison(t *testing.T) {
	w := ownedWorld(t)
	c := newCollector(w)
	snapshotOf(t, c, runAt(0, false))
	snapshotOf(t, c, triggeredAt(1, false, killsFor(1), 0))
	w.addListener("tcp", newBackdoor, 99999999)
	w.flush()

	_, snap := snapshotOf(t, c, runAt(2, false))

	if len(snap.NewListeners) != 1 || snap.NewListeners[0].Local != addrPort(newBackdoor) {
		t.Errorf("new listeners after a triggered run = %+v, want the one on %s (the triggered run is the predecessor)", snap.NewListeners, newBackdoor)
	}
	if snap.Trigger != nil {
		t.Errorf("trigger of the run after a triggered one = %+v, want nil", snap.Trigger)
	}
}

// Passwd files of the tests of the socket's UID.
const (
	rootPasswd  = "root:x:0:0:root:/root:/bin/bash\n"
	usersPasswd = rootPasswd + "www-data:x:33:33:www-data:/var/www:/usr/sbin/nologin\nalice:x:1001:1001::/home/alice:/bin/bash\n"
)

// holderPID is the process of the UID tests that holds their sockets.
const holderPID = int32(9000)

// aliceWorld returns a /proc with a listener on 0.0.0.0:2222 and an established connection, both sockets of UID 1001 (alice), held by
// the process holderPID, which calls itself sshd.
func aliceWorld(t *testing.T) *world {
	t.Helper()

	w := newWorld(t)
	w.clear()
	w.addListenerUID("tcp", "0.0.0.0:2222", 1001, 96100001)
	w.addSocketUID("tcp", "192.0.2.10:2222", "198.51.100.9:50000", stateEstablished, 1001, 96100002)
	w.process(holderPID, "sshd", "/tmp/sshd\x00")
	w.holdAll(holderPID, 96100001, 96100002)
	return w
}

// wantAliceEntries checks the listener and the connection of aliceWorld: the owner as given, the UID and user of the table lines.
func wantAliceEntries(t *testing.T, snap *model.ConnectionSnapshot, pid int32, command string) {
	t.Helper()

	wantListener(t, snap, model.ProtoTCP, "0.0.0.0:2222", pid, command, 1001, "alice")
	want := model.Connection{Proto: model.ProtoTCP, Local: addrPort("192.0.2.10:2222"), Remote: addrPort("198.51.100.9:50000"), State: "ESTABLISHED",
		PID: pid, Command: command, UID: uidOf(1001), User: "alice"}
	if !hasConnection(snap.Connections, want) {
		t.Errorf("connections = %s, want %s", connectionsText(snap.Connections), connectionText(want))
	}
}

// hidingFS leaves one process directory out of the listing of /proc, as hidepid=2 does for the processes of other users; the files
// of the process stay readable.
type hidingFS struct {
	fs.FS
	hide string
}

func (h hidingFS) Open(name string) (fs.File, error) {
	file, err := h.FS.Open(name)
	if err != nil || name != "." {
		return file, err
	}
	dir, ok := file.(fs.ReadDirFile)
	if !ok {
		return file, nil
	}
	return hidingDir{ReadDirFile: dir, hide: h.hide}, nil
}

// hidingDir is the root directory of a hidingFS.
type hidingDir struct {
	fs.ReadDirFile
	hide string
}

func (d hidingDir) ReadDir(n int) ([]fs.DirEntry, error) {
	entries, err := d.ReadDirFile.ReadDir(n)
	return slices.DeleteFunc(entries, func(e fs.DirEntry) bool { return e.Name() == d.hide }), err
}

func TestConnections_ListenersCarryTheSocketUID(t *testing.T) {
	w := ownedWorld(t)
	// The process that holds the sshd socket runs as www-data; the collector reads no status file, so the entry keeps the socket's UID.
	w.files["100/status"] = &fstest.MapFile{Data: []byte("Name:\tsshd\nUid:\t33\t33\t33\t33\n")}
	rec := &recordingFS{FS: w}

	_, snap := snapshotOf(t, newCollector(rec), runAt(0, false))

	cases := []struct {
		proto   model.Proto
		local   string
		pid     int32
		command string
		uid     uint32
		user    string
	}{
		{model.ProtoTCP, sshdListener, 100, cmdSSHD, 0, "root"},
		{model.ProtoTCP, nginxListener, 200, cmdNginx, 0, "root"},
		{model.ProtoTCP, "0.0.0.0:110", 0, "", 0, "root"},
		{model.ProtoTCP, "127.0.0.1:3306", 0, "", 114, "114"},
		{model.ProtoTCP, "0.0.0.0:2224", 0, "", 998, "998"},
		{model.ProtoTCP, "127.0.0.1:53", 600, cmdNamed, 116, "116"},
		{model.ProtoTCP, "127.0.0.53:53", 0, "", 102, "102"},
		{model.ProtoUDP, "192.0.2.10:53", 300, cmdNamed, 116, "116"},
		{model.ProtoUDP, "127.0.0.53:53", 0, "", 102, "102"},
		{model.ProtoTCP6, "[::]:106", 0, "", 0, "root"},
		{model.ProtoTCP6, "[::1]:53", 0, "", 116, "116"},
		{model.ProtoUDP6, "[::1]:53", 0, "", 116, "116"},
	}
	for _, c := range cases {
		t.Run(string(c.proto)+" "+c.local, func(t *testing.T) {
			wantListener(t, snap, c.proto, c.local, c.pid, c.command, c.uid, c.user)
		})
	}
	for _, l := range snap.Listeners {
		if l.UID == nil || l.User == "" || l.MixedUIDs {
			t.Errorf("listener %s, want a UID, a user and no mixed UIDs (every captured socket has an inode and one UID per endpoint)", listenerText(l))
		}
	}
	for _, name := range rec.opened() {
		if strings.HasSuffix(name, "/status") {
			t.Errorf("collector opened %q, want the UID taken from the socket tables only", name)
		}
	}

	t.Run("uid 0 is written", func(t *testing.T) {
		got := listenerAt(snap, model.ProtoTCP, sshdListener)
		if len(got) != 1 {
			t.Fatalf("listeners on tcp %s = %s, want one", sshdListener, listenersText(got))
		}
		data, err := json.Marshal(got[0])

		const want = `{"proto":"tcp","local":"0.0.0.0:22","pid":100,"command":"sshd","uid":0,"user":"root"}`
		if err != nil || string(data) != want {
			t.Errorf("json.Marshal(sshd listener) = %s, %v, want %s", data, err, want)
		}
	})
}

func TestConnections_UIDWithoutOwners(t *testing.T) {
	probeErr := errors.New("sockmount: socket: permission denied")

	t.Run("the probe of the socket mount failed", func(t *testing.T) {
		c, _ := newRecordingCollectorWith(aliceWorld(t), etcOf(usersPasswd), SocketMount{Err: probeErr})

		_, snap := snapshotOf(t, c, runAt(0, false))

		wantAliceEntries(t, snap, 0, "")
	})
	t.Run("the socket mount was not learned", func(t *testing.T) {
		c, _ := newRecordingCollectorWith(aliceWorld(t), etcOf(usersPasswd), SocketMount{})

		_, snap := snapshotOf(t, c, runAt(0, false))

		wantAliceEntries(t, snap, 0, "")
	})
	t.Run("the descriptors cannot be read", func(t *testing.T) {
		faulty := newFaultFS(aliceWorld(t))
		faulty.openErr[fdinfoName(holderPID, 3)] = syscall.EACCES
		c := newCollectorWith(faulty, etcOf(usersPasswd))

		_, snap := snapshotOf(t, c, runAt(0, false))

		wantAliceEntries(t, snap, 0, "")
	})
	t.Run("the holder is not listed", func(t *testing.T) {
		c := newCollectorWith(hidingFS{FS: aliceWorld(t), hide: strconv.Itoa(int(holderPID))}, etcOf(usersPasswd))

		_, snap := snapshotOf(t, c, runAt(0, false))

		wantAliceEntries(t, snap, 0, "")
	})
	t.Run("the holder is beyond the PID cap", func(t *testing.T) {
		w := aliceWorld(t)
		for pid := 1; pid <= procstats.MaxProcesses; pid++ {
			w.files[strconv.Itoa(pid)+"/stat"] = &fstest.MapFile{Data: []byte("x")}
		}
		c := newCollectorWith(w, etcOf(usersPasswd))

		_, snap := snapshotOf(t, c, runAt(0, false))

		wantAliceEntries(t, snap, 0, "")
		if !snap.AggregatesIncomplete {
			t.Error("aggregates_incomplete = false, want true (the precondition of this case: the holder was not scanned)")
		}
	})
	t.Run("the holder is scanned", func(t *testing.T) {
		c := newCollectorWith(aliceWorld(t), etcOf(usersPasswd))

		_, snap := snapshotOf(t, c, runAt(0, false))

		wantAliceEntries(t, snap, holderPID, "sshd")
	})
	t.Run("the socket is beyond MaxSockets", func(t *testing.T) {
		const total = MaxSockets + 10
		w := newWorld(t)
		w.clear()
		w.process(1, "init", "/sbin/init\x00")
		w.add(1, 3, socketMount, 10000000)
		gen := &genFS{base: w, tables: map[string]*tableGen{"net/tcp": newTableGen(t, "tcp", total, func(i uint32) sockSpec {
			// Descending addresses: the last lines have the lowest keys, so they head the listener list.
			return sockSpec{local: netip.AddrPortFrom(netip.AddrFrom4(addr4(0x0A000000+total-1-i)), 8000), remote: netip.AddrPortFrom(netip.IPv4Unspecified(), 0),
				state: stateListen, uid: 1001, inode: 10000000 + uint64(i)}
		})}}

		_, snap := snapshotOf(t, newCollectorWith(gen, etcOf(usersPasswd)), runAt(0, false))

		want := model.Listener{Proto: model.ProtoTCP, Local: addrPort("10.0.0.0:8000"), UID: uidOf(1001), User: "alice"}
		if len(snap.Listeners) == 0 || !sameListener(snap.Listeners[0], want) {
			t.Errorf("first listener = %s, want %s (beyond the inode index: no owner, but its line's UID)", listenersText(snap.Listeners[:min(1, len(snap.Listeners))]), listenerText(want))
		}
	})
	t.Run("the inode is repeated", func(t *testing.T) {
		w := newWorld(t)
		w.clear()
		w.addListenerUID("tcp", "0.0.0.0:7001", 1001, 96200001)
		w.addListenerUID("tcp", "0.0.0.0:7002", 33, 96200001)
		w.process(holderPID, "holder", "/usr/bin/holder\x00")
		w.holdAll(holderPID, 96200001)

		_, snap := snapshotOf(t, newCollectorWith(w, etcOf(usersPasswd)), runAt(0, false))

		wantListener(t, snap, model.ProtoTCP, "0.0.0.0:7001", holderPID, "holder", 1001, "alice")
		wantListener(t, snap, model.ProtoTCP, "0.0.0.0:7002", 0, "", 33, "www-data")
	})
}

func TestConnections_NewListenerCarriesTheInventoryUID(t *testing.T) {
	t.Run("one UID", func(t *testing.T) {
		w := ownedWorld(t)
		c := newCollectorWith(w, etcOf(usersPasswd))
		snapshotOf(t, c, runAt(0, false))
		w.addListenerUID("tcp", "0.0.0.0:2222", 1001, 99000010)
		w.process(710, "sshd", "/tmp/sshd\x00")
		w.holdAll(710, 99000010)

		_, snap := snapshotOf(t, c, runAt(1, false))

		want := model.Listener{Proto: model.ProtoTCP, Local: addrPort("0.0.0.0:2222"), PID: 710, Command: cmdSSHD, UID: uidOf(1001), User: "alice"}
		wantNewListeners(t, snap, 0, want)
		wantListener(t, snap, model.ProtoTCP, "0.0.0.0:2222", 710, cmdSSHD, 1001, "alice")
	})
	t.Run("two UIDs on the endpoint", func(t *testing.T) {
		w := ownedWorld(t)
		c := newCollectorWith(w, etcOf(usersPasswd))
		snapshotOf(t, c, runAt(0, false))
		w.addListenerUID("tcp", "0.0.0.0:2222", 1001, 99000010)
		w.addListenerUID("tcp", "0.0.0.0:2222", 33, 99000011)
		w.process(710, "sshd", "/tmp/sshd\x00")
		w.holdAll(710, 99000010)

		_, snap := snapshotOf(t, c, runAt(1, false))

		want := model.Listener{Proto: model.ProtoTCP, Local: addrPort("0.0.0.0:2222"), PID: 710, Command: cmdSSHD, UID: uidOf(33), User: "www-data", MixedUIDs: true}
		wantNewListeners(t, snap, 0, want)
		inventory := listenerAt(snap, model.ProtoTCP, "0.0.0.0:2222")
		if len(inventory) != 1 || !sameListener(inventory[0], want) {
			t.Errorf("listeners on tcp 0.0.0.0:2222 = %s, want exactly the new listener %s", listenersText(inventory), listenerText(want))
		}
	})
}

func TestConnections_ConnectionsCarryTheSocketUID(t *testing.T) {
	w := ownedWorld(t)
	w.addSocketUID("tcp6", "[2001:db8::10]:443", "[2001:db8::99]:50000", stateEstablished, 33, 99100001) // held by a process
	w.addSocketUID("tcp6", "[2001:db8::10]:443", "[2001:db8::99]:50001", stateEstablished, 33, 99100002) // nobody holds it
	w.process(720, "apache2", "/usr/sbin/apache2\x00")
	w.holdAll(720, 99100001)
	c := newCollector(w)

	_, full := snapshotOf(t, c, runAt(0, false))
	_, regular := snapshotOf(t, c, runAt(1, false))

	wants := []model.Connection{
		{Proto: model.ProtoTCP6, Local: addrPort("[2001:db8::10]:443"), Remote: addrPort("[2001:db8::99]:50000"), State: "ESTABLISHED", PID: 720, Command: "apache2", UID: uidOf(33), User: "www-data"},
		{Proto: model.ProtoTCP6, Local: addrPort("[2001:db8::10]:443"), Remote: addrPort("[2001:db8::99]:50001"), State: "ESTABLISHED", UID: uidOf(33), User: "www-data"},
	}
	for _, want := range append(wants, ownedConnections...) {
		if !hasConnection(full.Connections, want) {
			t.Errorf("connections of the full snapshot = %s, want %s", connectionsText(full.Connections), connectionText(want))
		}
	}
	if len(regular.Connections) != 0 {
		t.Errorf("connections of the regular snapshot = %s, want none", connectionsText(regular.Connections))
	}
}

func TestConnections_InodeZeroEntriesCarryNoUID(t *testing.T) {
	t.Run("connections", inodeZeroConnections)
	t.Run("a UDP endpoint of one socket without inode and one of UID 33, inode 0 first", func(t *testing.T) { inodeZeroUDPEndpoint(t, true) })
	t.Run("a UDP endpoint of one socket without inode and one of UID 33, inode 0 last", func(t *testing.T) { inodeZeroUDPEndpoint(t, false) })
	t.Run("a UDP endpoint of only a socket without inode", func(t *testing.T) {
		w := newWorld(t)
		w.clear()
		w.addListenerUID("udp", "0.0.0.0:5454", 33, 0)

		_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

		want := model.Listener{Proto: model.ProtoUDP, Local: addrPort("0.0.0.0:5454")}
		if got := listenerAt(snap, model.ProtoUDP, "0.0.0.0:5454"); len(got) != 1 || !sameListener(got[0], want) {
			t.Errorf("listeners on udp 0.0.0.0:5454 = %s, want exactly %s without UID, user and mixed UIDs", listenersText(got), listenerText(want))
		}
	})
}

// inodeZeroConnections checks the connections of lines with inode 0, which the kernel prints with a UID that is no owner.
func inodeZeroConnections(t *testing.T) {
	w := ownedWorld(t)
	// The pending connection with its listener's UID, a time-wait form of FIN_WAIT2 with UID 0, and orphaned sockets with the UID
	// they had (6.x) or root (5.15).
	w.addSocketUID("tcp", "192.0.2.10:443", "203.0.113.11:40001", "03", 116, 0)  // SYN_RECV
	w.addSocketUID("tcp", "192.0.2.10:443", "203.0.113.12:40002", "05", 0, 0)    // FIN_WAIT2
	w.addSocketUID("tcp", "192.0.2.10:443", "203.0.113.13:40003", "04", 0, 0)    // FIN_WAIT1, orphaned
	w.addSocketUID("tcp", "192.0.2.10:443", "203.0.113.14:40004", "09", 1001, 0) // LAST_ACK, orphaned
	c := newCollectorWith(w, etcOf(usersPasswd))

	_, snap := snapshotOf(t, c, runAt(0, false))

	for _, want := range []model.Connection{
		{Proto: model.ProtoTCP, Local: addrPort("192.0.2.10:56518"), Remote: addrPort("198.51.100.20:443"), State: "TIME_WAIT"},
		{Proto: model.ProtoTCP, Local: addrPort("192.0.2.10:443"), Remote: addrPort("203.0.113.11:40001"), State: "SYN_RECV"},
		{Proto: model.ProtoTCP, Local: addrPort("192.0.2.10:443"), Remote: addrPort("203.0.113.12:40002"), State: "FIN_WAIT2"},
		{Proto: model.ProtoTCP, Local: addrPort("192.0.2.10:443"), Remote: addrPort("203.0.113.13:40003"), State: "FIN_WAIT1"},
		{Proto: model.ProtoTCP, Local: addrPort("192.0.2.10:443"), Remote: addrPort("203.0.113.14:40004"), State: "LAST_ACK"},
	} {
		if !hasConnection(snap.Connections, want) {
			t.Errorf("connections = %s, want %s without UID and user", connectionsText(snap.Connections), connectionText(want))
		}
	}
	withUID := 0
	for _, conn := range snap.Connections {
		if conn.UID != nil {
			withUID++
		}
	}
	if withUID != 3 {
		t.Errorf("connections with a UID = %d, want the 3 with an inode (sshd's and the two UDP sockets) of %s", withUID, connectionsText(snap.Connections))
	}
}

// inodeZeroUDPEndpoint checks that a UDP socket without inode adds no UID to an endpoint, whichever line comes first.
func inodeZeroUDPEndpoint(t *testing.T, inodeZeroFirst bool) {
	w := newWorld(t)
	w.clear()
	if inodeZeroFirst {
		w.addListenerUID("udp", "0.0.0.0:5353", 0, 0)
	}
	w.addListenerUID("udp", "0.0.0.0:5353", 33, 96300001)
	if !inodeZeroFirst {
		w.addListenerUID("udp", "0.0.0.0:5353", 0, 0)
	}

	_, snap := snapshotOf(t, newCollector(w), runAt(0, false))

	wantListener(t, snap, model.ProtoUDP, "0.0.0.0:5353", 0, "", 33, "www-data")
}

func TestConnections_MixedUIDsOnOneEndpoint(t *testing.T) {
	cases := []struct {
		name  string
		table string
		local string
		proto model.Proto
		uids  []uint32
		want  uint32
		user  string
		mixed bool
	}{
		{"tcp 1001 then 33", "tcp", "0.0.0.0:8080", model.ProtoTCP, []uint32{1001, 33}, 33, "www-data", true},
		{"tcp 33 then 1001", "tcp", "0.0.0.0:8080", model.ProtoTCP, []uint32{33, 1001}, 33, "www-data", true},
		{"udp 0 then 1001", "udp", "0.0.0.0:5353", model.ProtoUDP, []uint32{0, 1001}, 0, "root", true},
		{"udp 1001 then 0", "udp", "0.0.0.0:5353", model.ProtoUDP, []uint32{1001, 0}, 0, "root", true},
		{"tcp three sockets of two UIDs", "tcp", "0.0.0.0:8080", model.ProtoTCP, []uint32{1001, 33, 1001}, 33, "www-data", true},
		{"tcp6 two UIDs", "tcp6", "[::]:8080", model.ProtoTCP6, []uint32{1001, 33}, 33, "www-data", true},
		{"two sockets of UID 116", "tcp", "0.0.0.0:8081", model.ProtoTCP, []uint32{116, 116}, 116, "116", false},
		{"one socket", "tcp", "0.0.0.0:8082", model.ProtoTCP, []uint32{1001}, 1001, "alice", false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			w := newWorld(t)
			w.clear()
			for i, uid := range tc.uids {
				w.addListenerUID(tc.table, tc.local, uid, 96400001+uint64(i))
			}

			_, snap := snapshotOf(t, newCollectorWith(w, etcOf(usersPasswd)), runAt(0, false))

			want := model.Listener{Proto: tc.proto, Local: addrPort(tc.local), UID: uidOf(tc.want), User: tc.user, MixedUIDs: tc.mixed}
			if got := listenerAt(snap, tc.proto, tc.local); len(got) != 1 || !sameListener(got[0], want) {
				t.Errorf("listeners on %s %s = %s, want exactly %s", tc.proto, tc.local, listenersText(got), listenerText(want))
			}
		})
	}
}

func TestConnections_BadUIDLineIsUnreadable(t *testing.T) {
	w := newWorld(t)
	original := slices.Clone(w.lines["tcp"])
	w.replaceLine("tcp", 3, setField(t, original[3], 7, "4294967296")) // physical line 5
	c, logs := newRecordingCollector(w, SocketMount{ID: socketMount})

	_, first := snapshotOf(t, c, runAt(0, false))
	_, second := snapshotOf(t, c, runAt(1, false))

	wantFlags(t, first, false, false, true, model.ListenerCheckStart)
	wantInventory(t, first, model.InventorySkipped, allTableProtos...)
	wantFlags(t, second, true, false, true, model.ListenerCheckNone)
	wantInventory(t, second, model.InventorySkipped, allTableProtos...)
	if got := first.States[1]; got != (model.StateCount{Proto: model.ProtoTCP, State: "LISTEN", Count: 25}) {
		t.Errorf("states[1] = %+v, want the 25 readable LISTEN sockets (the line with the bad uid is left out)", got)
	}
	warns := wantLogged(t, logs, slog.LevelWarn, "socket lines not read", 1)
	if len(warns) == 1 && (warns[0].attrs["lines"] != "1" || warns[0].attrs["err"] != "connstats: net/tcp: line 5: not in the expected format") {
		t.Errorf("warning attributes = %v, want lines=1 and err=connstats: net/tcp: line 5: not in the expected format", warns[0].attrs)
	}
}

func TestConnections_UserNamesFollowTheProcessRules(t *testing.T) {
	passwd := "first:x:2001:2001::/:/bin/sh\n" +
		"second:x:2001:2001::/:/bin/sh\n" +
		"bad\xffname:x:2002:2002::/:/bin/sh\n" +
		strings.Repeat("n", 300) + ":x:2003:2003::/:/bin/sh\n" +
		strings.Repeat("€", 100) + ":x:2004:2004::/:/bin/sh\n" +
		"ctl\x01\x1bname:x:2005:2005::/:/bin/sh\n"
	cases := []struct {
		uid  uint32
		want string
	}{
		{2001, "first"},
		{2002, "bad�name"},
		{2003, strings.Repeat("n", 256)},
		{2004, strings.Repeat("€", 85)},
		{2005, "ctl\x01\x1bname"},
		{2006, "2006"},
	}
	w := newWorld(t)
	w.clear()
	for i, tc := range cases {
		w.addListenerUID("tcp", "0.0.0.0:"+strconv.Itoa(9001+i), tc.uid, 96500001+uint64(i))
	}
	etc := etcOf(passwd)
	users, err := procstats.ReadUsers(etc)
	if err != nil {
		t.Fatalf("procstats.ReadUsers() error = %v, want nil (test setup)", err)
	}

	rec, snap := snapshotOf(t, newCollectorWith(w, etc), runAt(0, false))

	checkWire(t, rec)
	for i, tc := range cases {
		local := "0.0.0.0:" + strconv.Itoa(9001+i)
		got := listenerAt(snap, model.ProtoTCP, local)
		if len(got) != 1 || got[0].UID == nil || *got[0].UID != tc.uid || got[0].User != tc.want {
			t.Errorf("listeners on tcp %s = %s, want UID %d and user %q", local, listenersText(got), tc.uid, tc.want)
			continue
		}
		if oracle := procstats.UserName(users, tc.uid); got[0].User != oracle {
			t.Errorf("user of UID %d = %q, want %q, the name the process snapshot gives", tc.uid, got[0].User, oracle)
		}
	}
	t.Run("control characters are escaped in the JSON", func(t *testing.T) {
		got := listenerAt(snap, model.ProtoTCP, "0.0.0.0:9005")
		if len(got) != 1 {
			t.Fatalf("listeners on tcp 0.0.0.0:9005 = %s, want one", listenersText(got))
		}
		data, err := json.Marshal(got[0])

		if want := `"user":"ctl\u0001\u001bname"`; err != nil || !strings.Contains(string(data), want) {
			t.Errorf("json.Marshal(listener) = %s, %v, want it to hold %s", data, err, want)
		}
	})
}

// passwdFault is an /etc whose passwd cannot be read and the repair of it.
type passwdFault struct {
	name string
	etc  fs.FS
	fix  func()
}

// passwdFaults returns the ways the passwd can be unreadable within the bounds of the process collector.
func passwdFaults() []passwdFault {
	restore := func(etc fstest.MapFS) func() {
		return func() { etc["passwd"] = &fstest.MapFile{Data: []byte(rootPasswd)} }
	}
	missing := fstest.MapFS{}
	large := fstest.MapFS{"passwd": {Data: []byte(strings.Repeat("a:x:1:1\n", (1<<20)/8+10))}}
	longLine := fstest.MapFS{"passwd": {Data: []byte(rootPasswd + "a:x:1:" + strings.Repeat("g", 5000) + "\n")}}
	denied := newFaultFS(etcOf(rootPasswd))
	denied.openErr["passwd"] = syscall.EACCES
	return []passwdFault{
		{"missing", missing, restore(missing)},
		{"open error", denied, func() { delete(denied.openErr, "passwd") }},
		{"larger than 1 MiB", large, restore(large)},
		{"a line over 4 KiB", longLine, restore(longLine)},
	}
}

// wantDecimalUsers checks that the entries of snap carry the decimal UID as their user.
func wantDecimalUsers(t *testing.T, snap *model.ConnectionSnapshot) {
	t.Helper()

	for _, l := range snap.Listeners {
		if l.UID == nil || l.User != uidText(l.UID) {
			t.Fatalf("listener %s, want the user to be the decimal UID while the passwd is unavailable", listenerText(l))
		}
	}
	for _, conn := range snap.Connections {
		if conn.UID != nil && conn.User != uidText(conn.UID) {
			t.Errorf("connection %s, want the user to be the decimal UID while the passwd is unavailable", connectionText(conn))
		}
	}
}

// wantUnavailableWarning checks the one warning of an unreadable passwd: the error as procstats.ReadUsers returns it and no other
// attribute than the collector's.
func wantUnavailableWarning(t *testing.T, logs *logStore, wantErr error) {
	t.Helper()

	warns := wantLogged(t, logs, slog.LevelWarn, "user names unavailable", 1)
	if len(warns) != 1 {
		return
	}
	if got := warns[0].attrs["err"]; got != wantErr.Error() || !strings.HasPrefix(got, "procstats: passwd: ") {
		t.Errorf("warning err = %q, want %q", got, wantErr)
	}
	if len(warns[0].attrs) != 2 {
		t.Errorf("warning attributes = %v, want only collector and err", warns[0].attrs)
	}
}

func TestConnections_PasswdUnavailableLogsOnce(t *testing.T) {
	for _, tc := range passwdFaults() {
		t.Run(tc.name, func(t *testing.T) {
			_, wantErr := procstats.ReadUsers(tc.etc)
			if wantErr == nil {
				t.Fatal("procstats.ReadUsers() error = nil, want one (test setup)")
			}
			c, logs := newRecordingCollectorWith(ownedWorld(t), tc.etc, SocketMount{ID: socketMount})

			_, first := snapshotOf(t, c, runAt(0, false))
			_, second := snapshotOf(t, c, runAt(1, false))

			wantDecimalUsers(t, first)
			wantDecimalUsers(t, second)
			wantUnavailableWarning(t, logs, wantErr)
			wantLogged(t, logs, slog.LevelInfo, "user names available", 0)

			tc.fix()
			_, third := snapshotOf(t, c, runAt(2, false))
			snapshotOf(t, c, runAt(3, false))

			wantListener(t, third, model.ProtoTCP, sshdListener, 100, cmdSSHD, 0, "root")
			wantLogged(t, logs, slog.LevelInfo, "user names available", 1)
			wantLogged(t, logs, slog.LevelWarn, "user names unavailable", 1)
		})
	}

	t.Run("also while the socket owners are unknown", func(t *testing.T) {
		c, logs := newRecordingCollectorWith(ownedWorld(t), fstest.MapFS{}, SocketMount{Err: errors.New("sockmount: socket: permission denied")})

		snapshotOf(t, c, runAt(0, false))
		snapshotOf(t, c, runAt(1, false))

		wantLogged(t, logs, slog.LevelWarn, "socket owners unknown", 1)
		wantLogged(t, logs, slog.LevelWarn, "user names unavailable", 1)
	})
}

func TestConnections_ReadsPasswdOncePerRun(t *testing.T) {
	etc := &recordingFS{FS: fixtureEtc()}
	c := newCollectorWith(ownedWorld(t), etc)

	for i, run := range []scheduler.Run{runAt(0, false), runAt(1, false), runAt(2, true)} {
		snapshotOf(t, c, run)

		want := slices.Repeat([]string{"passwd"}, i+1)
		if got := etc.opened(); !slices.Equal(got, want) {
			t.Errorf("after run %d the collector opened %q in /etc, want one open of passwd per run: %q", i, got, want)
		}
	}
}

func TestConnections_BlockingPasswdReadIsAbandonedAsGap(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		stuck := newBlockingFS(t, etcOf(rootPasswd), "passwd")
		runWithBlockedRead(t, newCollectorWith(ownedWorld(t), stuck), stuck, "passwd")
	})
}

// longNamesEtc returns an /etc that names the UIDs 1000 to 1007 by 256 control characters each.
func longNamesEtc() fs.FS {
	var passwd strings.Builder
	for i := range longNameUIDs {
		fmt.Fprintf(&passwd, "%s:x:%d:%d::/:/bin/sh\n", strings.Repeat("\x01", 256), 1000+i, 1000+i)
	}
	return etcOf(passwd.String())
}

// longNameUIDs is the number of UIDs of longNamesEtc; longNameEntries the size of the lists of the cap.
const (
	longNameUIDs    = 8
	longNameEntries = 4096
)

// addLongNameConnections adds an established connection of a named UID per entry of the cap.
func addLongNameConnections(w *world) {
	for i := range longNameEntries {
		remote := "100.64." + strconv.Itoa(i/200) + "." + strconv.Itoa(i%200+1) + ":443"
		w.addSocketUID("tcp", "192.0.2.10:"+strconv.Itoa(1024+i), remote, stateEstablished, uint32(1000+i%longNameUIDs), 81000000+uint64(i))
	}
}

func TestConnections_LongUserNamesStillFit(t *testing.T) {
	t.Run("listeners and connections", longNamesCutListeners)
	t.Run("connections only", longNamesCutConnections)
}

// longNamesCutListeners fills the caps with listeners and connections of long names: the listeners are cut and stop the later lists.
func longNamesCutListeners(t *testing.T) {
	w := newWorld(t)
	w.clear()
	addLongNameConnections(w)
	for i := range longNameEntries {
		w.addListenerUID("tcp", "0.0.0.0:"+strconv.Itoa(1024+i), uint32(1000+i%longNameUIDs), 80000000+uint64(i))
	}

	rec, snap := snapshotOf(t, newCollectorWith(w, longNamesEtc()), runAt(0, false))

	checkWire(t, rec)
	if len(snap.Listeners) == 0 || len(snap.Listeners) >= longNameEntries {
		t.Fatalf("listeners = %d entries, want a list cut by the size budget (names of 256 control characters)", len(snap.Listeners))
	}
	if snap.Complete || !snap.AggregatesIncomplete || len(snap.Connections) != 0 {
		t.Errorf("complete = %v, aggregates_incomplete = %v, connections = %d, want false, true and 0 (a cut list stops the later ones)",
			snap.Complete, snap.AggregatesIncomplete, len(snap.Connections))
	}
	wantInventory(t, snap, model.InventoryPrivileged, allTableProtos...)
	if first := snap.Listeners[0]; first.UID == nil || len(first.User) != 256 {
		t.Errorf("first listener = %s, want a user of 256 bytes", listenerText(first))
	}
}

// longNamesCutConnections fills the cap with connections of long names: only the connections are cut.
func longNamesCutConnections(t *testing.T) {
	w := newWorld(t)
	w.clear()
	addLongNameConnections(w)
	w.addListenerUID("tcp", "0.0.0.0:22", 1000, 80000001)

	rec, snap := snapshotOf(t, newCollectorWith(w, longNamesEtc()), runAt(0, false))

	checkWire(t, rec)
	if len(snap.Connections) == 0 || len(snap.Connections) >= longNameEntries {
		t.Fatalf("connections = %d entries, want a list cut by the size budget", len(snap.Connections))
	}
	if snap.Complete || snap.AggregatesIncomplete {
		t.Errorf("complete = %v, aggregates_incomplete = %v, want false and false (only the connections are cut)", snap.Complete, snap.AggregatesIncomplete)
	}
	wantInventory(t, snap, model.InventoryAll, allTableProtos...)
}
