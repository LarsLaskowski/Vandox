package connstats

import (
	"net/netip"
	"strings"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// listenerKey identifies a listener endpoint.
type listenerKey struct {
	proto model.Proto
	addr  netip.Addr
	port  uint16
}

// inventory is the listener inventory of one run.
type inventory struct {
	keys    []listenerKey
	cut     bool
	skipped bool
	tables  uint8
}

// lessListener orders listener keys: ports 1 to 1023 first, then protocol, address, port.
func lessListener(a, b listenerKey) bool {
	if pa, pb := a.port < PrivilegedPorts, b.port < PrivilegedPorts; pa != pb {
		return pa
	}
	if c := strings.Compare(string(a.proto), string(b.proto)); c != 0 {
		return c < 0
	}
	if c := a.addr.Compare(b.addr); c != 0 {
		return c < 0
	}
	return a.port < b.port
}

// compare returns how far the inventories were compared and the keys of cur that prev lacks, in inventory order and without the
// ones reportable excludes.
func compare(prev, cur *inventory) (check model.ListenerCheck, added []listenerKey) {
	if prev == nil {
		return model.ListenerCheckStart, nil
	}
	if prev.skipped || cur.skipped || prev.tables != cur.tables {
		return model.ListenerCheckNone, nil
	}
	if cutInPrivilegedPart(prev) || cutInPrivilegedPart(cur) {
		return model.ListenerCheckNone, nil
	}
	check = model.ListenerCheckAll
	if prev.cut || cur.cut {
		check = model.ListenerCheckPrivileged
	}
	known := make(map[listenerKey]struct{}, len(prev.keys))
	for _, k := range prev.keys {
		known[k] = struct{}{}
	}
	for _, k := range cur.keys {
		if check == model.ListenerCheckPrivileged && k.port >= PrivilegedPorts {
			continue
		}
		if _, ok := known[k]; !ok && reportable(k) {
			added = append(added, k)
		}
	}
	return check, added
}

// cutInPrivilegedPart reports whether a cut inventory keeps only privileged keys, so it was cut inside the privileged part.
func cutInPrivilegedPart(i *inventory) bool {
	if !i.cut {
		return false
	}
	for _, k := range i.keys {
		if k.port >= PrivilegedPorts {
			return false
		}
	}
	return true
}

// reportable reports whether a new listener with key k is reported: every listener except an unconnected UDP socket in the
// ephemeral port range.
func reportable(k listenerKey) bool {
	udp := k.proto == model.ProtoUDP || k.proto == model.ProtoUDP6
	if udp {
		return k.port < EphemeralFirst || k.port > EphemeralLast
	}
	return true
}

// inventoryState returns how much of the inventory inv the record's listeners list, which holds listed entries, covers.
func inventoryState(inv *inventory, listed int) model.InventoryState {
	if inv.skipped {
		return model.InventorySkipped
	}
	if cutInPrivilegedPart(inv) {
		return model.InventoryCut
	}
	privileged := 0
	for _, k := range inv.keys {
		if k.port < PrivilegedPorts {
			privileged++
		}
	}
	if listed < privileged {
		return model.InventoryCut
	}
	if inv.cut || listed < len(inv.keys) {
		return model.InventoryPrivileged
	}
	return model.InventoryAll
}
