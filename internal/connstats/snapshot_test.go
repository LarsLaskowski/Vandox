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

func TestFill_ChangedListenersBetweenNewListenersAndListeners(t *testing.T) {
	newOne := model.Listener{Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:23")}
	var changed []model.ChangedListener
	for i := range 6 {
		changed = append(changed, model.ChangedListener{
			Listener:    model.Listener{Proto: model.ProtoTCP, Local: netip.AddrPortFrom(netip.IPv4Unspecified(), uint16(3000+i)), UID: uidOf(1001), User: "1001"},
			PreviousUID: uidOf(110),
		})
	}
	listed := []model.Listener{{Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:22")}, {Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:80")}}
	build := func() *lists {
		return &lists{newListeners: []model.Listener{newOne}, omitted: 2, changedListeners: slices.Clone(changed), changedOmitted: 3, listeners: slices.Clone(listed)}
	}
	sizeOf := func(e any) int {
		n, ok := entrySize(e)
		if !ok {
			t.Fatalf("entrySize(%v) failed", e)
		}
		return n
	}

	t.Run("a budget that holds everything keeps the lists and their counts", func(t *testing.T) {
		snap := &model.ConnectionSnapshot{}
		_, cut := fill(snap, build(), 1<<20)

		if cut || len(snap.NewListeners) != 1 || len(snap.ChangedListeners) != 6 || len(snap.Listeners) != 2 {
			t.Errorf("fill() cut=%v new=%d changed=%d listeners=%d, want false 1 6 2", cut, len(snap.NewListeners), len(snap.ChangedListeners), len(snap.Listeners))
		}
		if snap.NewListenersOmitted != 2 || snap.ChangedListenersOmitted != 3 {
			t.Errorf("omitted new=%d changed=%d, want 2 and 3", snap.NewListenersOmitted, snap.ChangedListenersOmitted)
		}
	})
	t.Run("a budget that cuts inside the changed list", func(t *testing.T) {
		budget := sizeOf(newOne) + sizeOf(changed[0]) + sizeOf(changed[1]) + sizeOf(changed[2]) - 1
		snap := &model.ConnectionSnapshot{}
		_, cut := fill(snap, build(), budget)

		if !cut {
			t.Error("fill() cut = false, want true")
		}
		if len(snap.NewListeners) != 1 {
			t.Errorf("new_listeners = %d entries, want 1 (they come before the changed list)", len(snap.NewListeners))
		}
		if len(snap.ChangedListeners) != 2 || snap.ChangedListenersOmitted != 3+4 {
			t.Errorf("changed_listeners = %d entries, omitted %d, want 2 and 7", len(snap.ChangedListeners), snap.ChangedListenersOmitted)
		}
		if len(snap.Listeners) != 0 {
			t.Errorf("listeners = %d entries, want none after the cut", len(snap.Listeners))
		}
		inv := &inventory{keys: []listenerKey{key(model.ProtoTCP, "0.0.0.0", 22), key(model.ProtoTCP, "0.0.0.0", 80)}}
		if got := inventoryState(inv, len(snap.Listeners)); got != model.InventoryCut {
			t.Errorf("inventory from the list as sent = %q, want %q", got, model.InventoryCut)
		}
	})
	t.Run("a budget that cuts the new listeners drops the changed list", func(t *testing.T) {
		snap := &model.ConnectionSnapshot{}
		fill(snap, build(), sizeOf(newOne)-1)

		if len(snap.NewListeners) != 0 || snap.NewListenersOmitted != 3 {
			t.Errorf("new_listeners = %d entries, omitted %d, want 0 and 3", len(snap.NewListeners), snap.NewListenersOmitted)
		}
		if len(snap.ChangedListeners) != 0 || snap.ChangedListenersOmitted != 3+6 {
			t.Errorf("changed_listeners = %d entries, omitted %d, want 0 and 9", len(snap.ChangedListeners), snap.ChangedListenersOmitted)
		}
	})
	t.Run("a budget that holds the changed list but not the listeners", func(t *testing.T) {
		budget := sizeOf(newOne) + 6*sizeOf(changed[0])
		snap := &model.ConnectionSnapshot{}
		fill(snap, build(), budget)

		if len(snap.ChangedListeners) != 6 || snap.ChangedListenersOmitted != 3 || len(snap.Listeners) != 0 {
			t.Errorf("changed=%d omitted=%d listeners=%d, want 6 3 0", len(snap.ChangedListeners), snap.ChangedListenersOmitted, len(snap.Listeners))
		}
	})
}
