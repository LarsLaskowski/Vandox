package connstats

import (
	"net/netip"
	"slices"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// changedCollection returns a collection with count TCP listeners of UID 1001 on the ports from first on, and the changes of all of
// them from UID 110.
func changedCollection(first uint16, count int) (*collection, []uidChange) {
	col := newCollection()
	var changes []uidChange
	for i := range count {
		local := netip.AddrPortFrom(netip.IPv4Unspecified(), first+uint16(i))
		col.add(socket{proto: model.ProtoTCP, local: local, remote: netip.AddrPortFrom(netip.IPv4Unspecified(), 0), state: "LISTEN", inode: uint64(1000 + i), uid: 1001})
		changes = append(changes, uidChange{key: listenerKey{proto: model.ProtoTCP, addr: local.Addr(), port: local.Port()}, before: su(110, i%2 == 1)})
	}
	return col, changes
}

func TestBuildSnapshot_ChangedListenersCappedAndCounted(t *testing.T) {
	col, changes := changedCollection(5000, MaxChangedListeners+1)

	snap := buildSnapshot(col, &runResult{}, model.ListenerCheckAll, nil, changes, true)

	if len(snap.ChangedListeners) != MaxChangedListeners || snap.ChangedListenersOmitted != 1 {
		t.Fatalf("changed_listeners = %d entries, omitted %d, want %d and 1", len(snap.ChangedListeners), snap.ChangedListenersOmitted, MaxChangedListeners)
	}
	for i, got := range snap.ChangedListeners {
		wantPort := uint16(5000 + i)
		if got.Local.Port() != wantPort {
			t.Fatalf("changed_listeners[%d] port = %d, want %d (inventory order)", i, got.Local.Port(), wantPort)
		}
		if got.UID == nil || *got.UID != 1001 || got.User != "1001" {
			t.Errorf("changed_listeners[%d] uid/user = %v/%q, want 1001/1001", i, got.UID, got.User)
		}
		if got.PreviousUID == nil || *got.PreviousUID != 110 || got.PreviousMixedUIDs != (i%2 == 1) {
			t.Errorf("changed_listeners[%d] previous = %v mixed %v, want 110 mixed %v", i, got.PreviousUID, got.PreviousMixedUIDs, i%2 == 1)
		}
	}
}

func TestBuildSnapshot_ChangedListenersAtTheCapAreNotOmitted(t *testing.T) {
	col, changes := changedCollection(5000, MaxChangedListeners)

	snap := buildSnapshot(col, &runResult{}, model.ListenerCheckAll, nil, changes, true)

	if len(snap.ChangedListeners) != MaxChangedListeners || snap.ChangedListenersOmitted != 0 {
		t.Errorf("changed_listeners = %d entries, omitted %d, want %d and 0", len(snap.ChangedListeners), snap.ChangedListenersOmitted, MaxChangedListeners)
	}
}

// fillFixture is the lists of the fill tests: one new listener, six changed ones and two inventory entries, each list with a count of
// entries it already left out.
type fillFixture struct {
	newOne  model.Listener
	changed []model.ChangedListener
	listed  []model.Listener
}

func newFillFixture() fillFixture {
	f := fillFixture{
		newOne: model.Listener{Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:23")},
		listed: []model.Listener{{Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:22")}, {Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:80")}},
	}
	for i := range 6 {
		f.changed = append(f.changed, model.ChangedListener{
			Listener:    model.Listener{Proto: model.ProtoTCP, Local: netip.AddrPortFrom(netip.IPv4Unspecified(), uint16(3000+i)), UID: uidOf(1001), User: "1001"},
			PreviousUID: uidOf(110),
		})
	}
	return f
}

// fillWith fills a snapshot from the fixture's lists within budget.
func (f fillFixture) fillWith(budget int) (snap *model.ConnectionSnapshot, cut bool) {
	snap = &model.ConnectionSnapshot{}
	l := &lists{newListeners: []model.Listener{f.newOne}, omitted: 2, changedListeners: slices.Clone(f.changed), changedOmitted: 3, listeners: slices.Clone(f.listed)}
	_, cut = fill(snap, l, budget)
	return snap, cut
}

// size returns the bytes an entry takes in the budget.
func sizeOf(t *testing.T, e any) int {
	t.Helper()

	n, ok := entrySize(e)
	if !ok {
		t.Fatalf("entrySize(%v) failed", e)
	}
	return n
}

// wantFilled checks the lengths of the three lists and the two omitted counts of a filled snapshot.
func wantFilled(t *testing.T, snap *model.ConnectionSnapshot, newListeners, changed, listeners int, newOmitted, changedOmitted uint32) {
	t.Helper()

	if len(snap.NewListeners) != newListeners || len(snap.ChangedListeners) != changed || len(snap.Listeners) != listeners {
		t.Errorf("new_listeners=%d changed_listeners=%d listeners=%d entries, want %d %d %d",
			len(snap.NewListeners), len(snap.ChangedListeners), len(snap.Listeners), newListeners, changed, listeners)
	}
	if snap.NewListenersOmitted != newOmitted || snap.ChangedListenersOmitted != changedOmitted {
		t.Errorf("omitted new=%d changed=%d, want %d and %d", snap.NewListenersOmitted, snap.ChangedListenersOmitted, newOmitted, changedOmitted)
	}
}

func TestFill_ChangedListenersFitIntoALargeBudget(t *testing.T) {
	snap, cut := newFillFixture().fillWith(1 << 20)

	if cut {
		t.Error("fill() cut = true, want false")
	}
	wantFilled(t, snap, 1, 6, 2, 2, 3)
}

func TestFill_ChangedListenersBetweenNewListenersAndListeners(t *testing.T) {
	f := newFillFixture()
	budget := sizeOf(t, f.newOne) + sizeOf(t, f.changed[0]) + sizeOf(t, f.changed[1]) + sizeOf(t, f.changed[2]) - 1

	snap, cut := f.fillWith(budget)

	if !cut {
		t.Error("fill() cut = false, want true")
	}
	wantFilled(t, snap, 1, 2, 0, 2, 3+4)
	inv := &inventory{keys: []listenerKey{key(model.ProtoTCP, "0.0.0.0", 22), key(model.ProtoTCP, "0.0.0.0", 80)}}
	if got := inventoryState(inv, len(snap.Listeners)); got != model.InventoryCut {
		t.Errorf("inventory from the list as sent = %q, want %q", got, model.InventoryCut)
	}
}

func TestFill_CutNewListenersDropTheChangedList(t *testing.T) {
	f := newFillFixture()

	snap, _ := f.fillWith(sizeOf(t, f.newOne) - 1)

	wantFilled(t, snap, 0, 0, 0, 3, 3+6)
}

func TestFill_ChangedListThatFitsLeavesNoRoomForListeners(t *testing.T) {
	f := newFillFixture()

	snap, _ := f.fillWith(sizeOf(t, f.newOne) + 6*sizeOf(t, f.changed[0]))

	wantFilled(t, snap, 1, 6, 0, 2, 3)
}
