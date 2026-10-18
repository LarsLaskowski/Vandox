package connstats

import (
	"net/netip"

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
	return false
}

// compare returns how far the inventories were compared and the keys of cur that prev lacks.
func compare(prev, cur *inventory) (check model.ListenerCheck, added []listenerKey) {
	return model.ListenerCheckNone, nil
}

// reportable reports whether a new listener with key k is reported.
func reportable(k listenerKey) bool {
	return false
}
