package connstats

import (
	"net/netip"
	"slices"
	"strconv"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const allTables = 0b1111 // tcp, tcp6, udp, udp6 read

// key builds a listener key.
func key(proto model.Proto, addr string, port uint16) listenerKey {
	return listenerKey{proto: proto, addr: netip.MustParseAddr(addr), port: port}
}

// inv builds an inventory over keys, which the caller lists in inventory order (ports 1 to 1023 first, then protocol, address, port).
func inv(keys ...listenerKey) *inventory {
	return &inventory{keys: keys, tables: allTables}
}

func checkCompare(t *testing.T, prev, cur *inventory, wantCheck model.ListenerCheck, wantAdded []listenerKey) {
	t.Helper()

	check, added := compare(prev, cur)
	if check != wantCheck {
		t.Errorf("compare() check = %q, want %q", check, wantCheck)
	}
	if !slices.Equal(added, wantAdded) {
		t.Errorf("compare() added = %v, want %v", added, wantAdded)
	}
}

func TestCompare_NewListenerReported(t *testing.T) {
	ssh := key(model.ProtoTCP, "0.0.0.0", 22)
	http := key(model.ProtoTCP, "0.0.0.0", 80)
	telnet := key(model.ProtoTCP, "0.0.0.0", 23)
	backdoor := key(model.ProtoTCP, "0.0.0.0", 4444)
	alt := key(model.ProtoTCP, "0.0.0.0", 8080)
	mdns := key(model.ProtoUDP, "0.0.0.0", 5353)
	web6 := key(model.ProtoTCP6, "::", 8080)

	t.Run("one new listener", func(t *testing.T) {
		checkCompare(t, inv(ssh, http), inv(ssh, http, backdoor), model.ListenerCheckAll, []listenerKey{backdoor})
	})
	t.Run("several in inventory order, privileged first", func(t *testing.T) {
		checkCompare(t, inv(ssh, http, alt), inv(ssh, telnet, http, backdoor, alt, web6, mdns),
			model.ListenerCheckAll, []listenerKey{telnet, backdoor, web6, mdns})
	})
	t.Run("a new UDP listener below the ephemeral range", func(t *testing.T) {
		checkCompare(t, inv(ssh), inv(ssh, mdns), model.ListenerCheckAll, []listenerKey{mdns})
	})
	t.Run("a listener that went away is not reported", func(t *testing.T) {
		checkCompare(t, inv(ssh, http), inv(ssh), model.ListenerCheckAll, nil)
	})
	t.Run("the same protocol and port on another address is new", func(t *testing.T) {
		loop := key(model.ProtoTCP, "127.0.0.1", 8080)
		checkCompare(t, inv(alt), inv(loop, alt), model.ListenerCheckAll, []listenerKey{loop})
	})
	t.Run("the same address and port under another protocol is new", func(t *testing.T) {
		udp80 := key(model.ProtoUDP, "0.0.0.0", 80)
		checkCompare(t, inv(http), inv(http, udp80), model.ListenerCheckAll, []listenerKey{udp80})
	})
	t.Run("nothing changed", func(t *testing.T) {
		checkCompare(t, inv(ssh, http, alt), inv(ssh, http, alt), model.ListenerCheckAll, nil)
	})
	t.Run("an empty previous inventory still compares", func(t *testing.T) {
		checkCompare(t, inv(), inv(ssh), model.ListenerCheckAll, []listenerKey{ssh})
	})
}

func TestCompare_FirstRunReportsStart(t *testing.T) {
	ssh := key(model.ProtoTCP, "0.0.0.0", 22)
	backdoor := key(model.ProtoTCP, "0.0.0.0", 4444)

	t.Run("no previous inventory", func(t *testing.T) {
		checkCompare(t, nil, inv(ssh, backdoor), model.ListenerCheckStart, nil)
	})
	t.Run("no previous inventory and an empty current one", func(t *testing.T) {
		checkCompare(t, nil, inv(), model.ListenerCheckStart, nil)
	})
	t.Run("no previous inventory and a skipped line", func(t *testing.T) {
		cur := inv(ssh)
		cur.skipped = true
		checkCompare(t, nil, cur, model.ListenerCheckStart, nil)
	})
	t.Run("a previous inventory does compare", func(t *testing.T) {
		checkCompare(t, inv(ssh), inv(ssh, backdoor), model.ListenerCheckAll, []listenerKey{backdoor})
	})
}

func TestInventoryState(t *testing.T) {
	ssh := key(model.ProtoTCP, "0.0.0.0", 22)
	http := key(model.ProtoTCP, "0.0.0.0", 80)
	alt := key(model.ProtoTCP, "0.0.0.0", 8080)
	other := key(model.ProtoTCP, "0.0.0.0", 9090)
	flags := func(i *inventory, cut, skipped bool) *inventory {
		i.cut, i.skipped = cut, skipped
		return i
	}
	cases := []struct {
		name   string
		inv    *inventory
		listed int
		want   model.InventoryState
	}{
		{"no cut, every key listed", inv(ssh, http, alt), 3, model.InventoryAll},
		{"no cut and no listener", inv(), 0, model.InventoryAll},
		{"no cut, only unprivileged keys, all listed", inv(alt, other), 2, model.InventoryAll},
		{"inventory cut with unprivileged keys kept, all of them listed", flags(inv(ssh, http, alt), true, false), 3, model.InventoryPrivileged},
		{"inventory cut with a whole privileged part and no more", flags(inv(ssh, http, alt), true, false), 2, model.InventoryPrivileged},
		{"inventory cut before any privileged key", flags(inv(alt, other), true, false), 2, model.InventoryPrivileged},
		{"size cut after the privileged part", inv(ssh, http, alt, other), 3, model.InventoryPrivileged},
		{"size cut right after the privileged part", inv(ssh, http, alt, other), 2, model.InventoryPrivileged},
		{"size cut of a list without privileged keys", inv(alt, other), 0, model.InventoryPrivileged},
		{"size cut inside the privileged part", inv(ssh, http, alt, other), 1, model.InventoryCut},
		{"size cut of every entry with privileged keys", inv(ssh, http, alt), 0, model.InventoryCut},
		{"inventory cut inside the privileged part", flags(inv(ssh, http), true, false), 2, model.InventoryCut},
		{"inventory cut inside the privileged part and the list cut", flags(inv(ssh, http), true, false), 1, model.InventoryCut},
		{"a skipped line", flags(inv(ssh, http, alt), false, true), 3, model.InventorySkipped},
		{"a skipped line and a cut inventory", flags(inv(ssh, http, alt), true, true), 3, model.InventorySkipped},
		{"a skipped line and a cut inside the privileged part", flags(inv(ssh, http), true, true), 1, model.InventorySkipped},
		{"a skipped line and a size cut", flags(inv(ssh, http, alt), false, true), 1, model.InventorySkipped},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := inventoryState(c.inv, c.listed); got != c.want {
				t.Errorf("inventoryState(%d keys, cut %v, skipped %v, listed %d) = %q, want %q",
					len(c.inv.keys), c.inv.cut, c.inv.skipped, c.listed, got, c.want)
			}
		})
	}
}

func TestCompare_EphemeralUDPNotReported(t *testing.T) {
	ssh := key(model.ProtoTCP, "0.0.0.0", 22)
	for _, proto := range []model.Proto{model.ProtoUDP, model.ProtoUDP6} {
		addr := "0.0.0.0"
		if proto == model.ProtoUDP6 {
			addr = "::"
		}
		for _, port := range []uint16{EphemeralFirst, 40000, EphemeralLast} {
			k := key(proto, addr, port)
			t.Run(string(proto)+" port "+portName(port), func(t *testing.T) {
				if reportable(k) {
					t.Errorf("reportable(%v) = true, want false (an unconnected UDP socket in the ephemeral range)", k)
				}
				checkCompare(t, inv(ssh), inv(ssh, k), model.ListenerCheckAll, nil)
			})
		}
		for _, port := range []uint16{53, 1023, 1024, EphemeralFirst - 1, EphemeralLast + 1, 65535} {
			k := key(proto, addr, port)
			t.Run(string(proto)+" port "+portName(port)+" is reported", func(t *testing.T) {
				if !reportable(k) {
					t.Errorf("reportable(%v) = false, want true (outside 32768 to 60999)", k)
				}
				checkCompare(t, inv(ssh), inv(ssh, k), model.ListenerCheckAll, []listenerKey{k})
			})
		}
	}

	t.Run("an ephemeral UDP socket among other new listeners", func(t *testing.T) {
		eph := key(model.ProtoUDP, "0.0.0.0", 40000)
		backdoor := key(model.ProtoTCP, "0.0.0.0", 4444)
		checkCompare(t, inv(ssh), inv(ssh, backdoor, eph), model.ListenerCheckAll, []listenerKey{backdoor})
	})
}

func TestCompare_TCPOnEphemeralPortReported(t *testing.T) {
	ssh := key(model.ProtoTCP, "0.0.0.0", 22)
	for _, proto := range []model.Proto{model.ProtoTCP, model.ProtoTCP6} {
		addr := "0.0.0.0"
		if proto == model.ProtoTCP6 {
			addr = "::"
		}
		for _, port := range []uint16{EphemeralFirst, 40000, EphemeralLast} {
			k := key(proto, addr, port)
			t.Run(string(proto)+" port "+portName(port), func(t *testing.T) {
				if !reportable(k) {
					t.Errorf("reportable(%v) = false, want true (a TCP listener is reported whatever its port)", k)
				}
				checkCompare(t, inv(ssh), inv(ssh, k), model.ListenerCheckAll, []listenerKey{k})
			})
		}
	}
}

func TestCompare_ReappearingListenerIsNewAgain(t *testing.T) {
	ssh := key(model.ProtoTCP, "0.0.0.0", 22)
	backdoor := key(model.ProtoTCP, "0.0.0.0", 4444)

	first := inv(ssh, backdoor)
	second := inv(ssh)
	third := inv(ssh, backdoor)

	checkCompare(t, first, second, model.ListenerCheckAll, nil)
	checkCompare(t, second, third, model.ListenerCheckAll, []listenerKey{backdoor})
}

func TestCompare_OwnerChangeIsNotNew(t *testing.T) {
	// A key names protocol, address and port only: the process that holds the socket is not part of the inventory,
	// so a listener handed to another owner is the same key in both runs.
	ssh := key(model.ProtoTCP, "0.0.0.0", 22)
	web := key(model.ProtoTCP6, "::", 443)
	checkCompare(t, inv(ssh, web), inv(ssh, web), model.ListenerCheckAll, nil)

	again := key(model.ProtoTCP, "0.0.0.0", 22)
	if again != ssh {
		t.Errorf("key(tcp, 0.0.0.0, 22) built twice differs: %v and %v, want equal keys", again, ssh)
	}
}

func TestCompare_CutInventoryComparesPrivilegedOnly(t *testing.T) {
	ssh := key(model.ProtoTCP, "0.0.0.0", 22)
	http := key(model.ProtoTCP, "0.0.0.0", 80)
	telnet := key(model.ProtoTCP, "0.0.0.0", 23)
	first := key(model.ProtoTCP, "0.0.0.0", 3000)
	second := key(model.ProtoTCP, "0.0.0.0", 4444)
	third := key(model.ProtoTCP, "0.0.0.0", 5000)

	cut := func(i *inventory) *inventory {
		i.cut = true
		return i
	}
	// The cut inventory holds the whole privileged part and some unprivileged keys.
	cases := []struct {
		name      string
		prev, cur *inventory
		added     []listenerKey
	}{
		{"current cut, new privileged", inv(ssh, http, first), cut(inv(ssh, telnet, http, first, second)), []listenerKey{telnet}},
		{"previous cut, new privileged", cut(inv(ssh, http, first)), inv(ssh, telnet, http, first, second), []listenerKey{telnet}},
		{"both cut, new privileged", cut(inv(ssh, http, first)), cut(inv(ssh, telnet, http, second, third)), []listenerKey{telnet}},
		{"current cut, only unprivileged ones are new", inv(ssh, http, first), cut(inv(ssh, http, first, second, third)), nil},
		{"previous cut, only unprivileged ones are new", cut(inv(ssh, http, first)), inv(ssh, http, first, second, third), nil},
		{"unprivileged keys that differ between the runs are not compared", cut(inv(ssh, http, first)), cut(inv(ssh, http, second)), nil},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			checkCompare(t, c.prev, c.cur, model.ListenerCheckPrivileged, c.added)
		})
	}

	t.Run("an uncut inventory compares everything", func(t *testing.T) {
		checkCompare(t, inv(ssh, http, first), inv(ssh, http, first, second), model.ListenerCheckAll, []listenerKey{second})
	})
}

func TestCompare_SkippedLineOrChangedTablesComparesNothing(t *testing.T) {
	ssh := key(model.ProtoTCP, "0.0.0.0", 22)
	telnet := key(model.ProtoTCP, "0.0.0.0", 23)
	backdoor := key(model.ProtoTCP, "0.0.0.0", 4444)

	control := inv(ssh, telnet, backdoor)
	prev := inv(ssh)
	if check, added := compare(prev, control); check != model.ListenerCheckAll || len(added) != 2 {
		t.Fatalf("compare(control) = %q, %v, want %q and two new listeners (the cases below would prove nothing)", check, added, model.ListenerCheckAll)
	}

	withSkip := func(i *inventory) *inventory {
		i.skipped = true
		return i
	}
	withTables := func(i *inventory, tables uint8) *inventory {
		i.tables = tables
		return i
	}
	withCut := func(i *inventory) *inventory {
		i.cut = true
		return i
	}
	cases := []struct {
		name      string
		prev, cur *inventory
	}{
		{"line skipped in the current run", inv(ssh), withSkip(inv(ssh, telnet, backdoor))},
		{"line skipped in the previous run", withSkip(inv(ssh)), inv(ssh, telnet, backdoor)},
		{"line skipped in both runs", withSkip(inv(ssh)), withSkip(inv(ssh, telnet, backdoor))},
		{"IPv6 tables missing in the current run", inv(ssh), withTables(inv(ssh, telnet, backdoor), 0b0101)},
		{"IPv6 tables missing in the previous run", withTables(inv(ssh), 0b0101), inv(ssh, telnet, backdoor)},
		{"different tables missing", withTables(inv(ssh), 0b0111), withTables(inv(ssh, telnet, backdoor), 0b1101)},
		{"line skipped and the inventory cut", withSkip(inv(ssh)), withCut(inv(ssh, telnet, backdoor))},
		{"tables changed and the previous inventory cut", withCut(inv(ssh)), withTables(inv(ssh, telnet, backdoor), 0b0101)},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			checkCompare(t, c.prev, c.cur, model.ListenerCheckNone, nil)
		})
	}

	t.Run("the same reduced set of tables in both runs still compares", func(t *testing.T) {
		checkCompare(t, withTables(inv(ssh), 0b0101), withTables(inv(ssh, telnet, backdoor), 0b0101),
			model.ListenerCheckAll, []listenerKey{telnet, backdoor})
	})
}

func TestCompare_CutPrivilegedPartComparesNothing(t *testing.T) {
	ssh := key(model.ProtoTCP, "0.0.0.0", 22)
	telnet := key(model.ProtoTCP, "0.0.0.0", 23)
	http := key(model.ProtoTCP, "0.0.0.0", 80)
	https := key(model.ProtoTCP, "0.0.0.0", 443)
	first := key(model.ProtoTCP, "0.0.0.0", 1024)

	cut := func(i *inventory) *inventory {
		i.cut = true
		return i
	}
	// A cut inventory whose kept keys are all privileged was cut inside the privileged part.
	cases := []struct {
		name      string
		prev, cur *inventory
	}{
		{"current inventory cut inside the privileged part", inv(ssh, http), cut(inv(ssh, telnet, http, https))},
		{"previous inventory cut inside the privileged part", cut(inv(ssh, http)), inv(ssh, telnet, http, https)},
		{"both cut inside the privileged part", cut(inv(ssh, http)), cut(inv(ssh, telnet, http, https))},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			checkCompare(t, c.prev, c.cur, model.ListenerCheckNone, nil)
		})
	}

	t.Run("one unprivileged key after the privileged ones means the privileged part is whole", func(t *testing.T) {
		checkCompare(t, inv(ssh, http, first), cut(inv(ssh, telnet, http, first)), model.ListenerCheckPrivileged, []listenerKey{telnet})
	})
}

// portName writes a port for a subtest name.
func portName(port uint16) string {
	return strconv.Itoa(int(port))
}

func TestTableList(t *testing.T) {
	cases := []struct {
		name string
		mask uint8
		want []model.Proto
	}{
		{"all four tables in table order", allTables, []model.Proto{model.ProtoTCP, model.ProtoTCP6, model.ProtoUDP, model.ProtoUDP6}},
		{"IPv6 tables missing", 0b0101, []model.Proto{model.ProtoTCP, model.ProtoUDP}},
		{"IPv4 tables missing", 0b1010, []model.Proto{model.ProtoTCP6, model.ProtoUDP6}},
		{"one table", 0b0100, []model.Proto{model.ProtoUDP}},
		{"no table", 0, nil},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := tableList(c.mask); !slices.Equal(got, c.want) {
				t.Errorf("tableList(%04b) = %v, want %v", c.mask, got, c.want)
			}
		})
	}
}
