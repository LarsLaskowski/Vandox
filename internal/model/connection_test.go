package model_test

import (
	"net/netip"
	"strings"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func validConnectionSnapshot() *model.ConnectionSnapshot {
	return &model.ConnectionSnapshot{
		Complete: true,
		States: []model.StateCount{
			{Proto: model.ProtoTCP, State: "ESTABLISHED", Count: 3},
			{Proto: model.ProtoUDP, Count: 1},
		},
		Processes: []model.ProcessConnections{{PID: 10, Command: "nginx", Count: 2}},
		Remotes: []model.RemoteCount{
			{Addr: netip.MustParseAddr("192.0.2.1"), Count: 4},
			{Addr: netip.MustParseAddr("::ffff:1.2.3.4"), Count: 1},
			{Addr: netip.MustParseAddr("fe80::1"), Count: 1},
		},
		Listeners: []model.Listener{
			{Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:80"), PID: 10, Command: "nginx"},
			{Proto: model.ProtoUDP6, Local: netip.MustParseAddrPort("[::]:53")},
		},
		Connections: []model.Connection{
			{
				Proto:   model.ProtoTCP,
				Local:   netip.MustParseAddrPort("10.0.0.1:80"),
				Remote:  netip.MustParseAddrPort("192.0.2.1:5000"),
				State:   "ESTABLISHED",
				PID:     10,
				Command: "nginx",
			},
			{
				Proto:  model.ProtoUDP,
				Local:  netip.MustParseAddrPort("10.0.0.1:123"),
				Remote: netip.MustParseAddrPort("192.0.2.9:123"),
			},
		},
	}
}

// zoned returns ip with the given zone and fails the test if the zone did not stick.
func zoned(t *testing.T, ip, zone string) netip.Addr {
	t.Helper()
	a := netip.MustParseAddr(ip).WithZone(zone)
	if a.Zone() != zone {
		t.Fatalf("test setup: %s.WithZone(len %d).Zone() has length %d", ip, len(zone), len(a.Zone()))
	}
	return a
}

func TestConnectionSnapshot_Kind(t *testing.T) {
	if got := validConnectionSnapshot().Kind(); got != model.KindConnectionSnapshot {
		t.Errorf("Kind() = %q, want %q", got, model.KindConnectionSnapshot)
	}
}

func TestConnectionSnapshot_Validate_Valid(t *testing.T) {
	tests := []struct {
		name string
		p    *model.ConnectionSnapshot
	}{
		{"fully populated", validConnectionSnapshot()},
		{"entirely empty", &model.ConnectionSnapshot{}},
		{"max items per list", &model.ConnectionSnapshot{
			States:      repeat(model.StateCount{Proto: model.ProtoUDP, Count: 1}, model.MaxItems),
			Processes:   repeat(model.ProcessConnections{PID: 1, Command: "c", Count: 1}, model.MaxItems),
			Remotes:     repeat(model.RemoteCount{Addr: netip.MustParseAddr("192.0.2.1"), Count: 1}, model.MaxItems),
			Listeners:   repeat(model.Listener{Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:80")}, model.MaxItems),
			Connections: repeat(model.Connection{Proto: model.ProtoUDP, Local: netip.MustParseAddrPort("10.0.0.1:1"), Remote: netip.MustParseAddrPort("10.0.0.2:2")}, model.MaxItems),
		}},
		{"udp6 listener without pid", &model.ConnectionSnapshot{Listeners: []model.Listener{{Proto: model.ProtoUDP6, Local: netip.MustParseAddrPort("[::1]:5353")}}}},
		{"tcp6 state in set", &model.ConnectionSnapshot{States: []model.StateCount{{Proto: model.ProtoTCP6, State: "NEW_SYN_RECV", Count: 1}}}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { requireValid(t, tc.p) })
	}

	t.Run("every TCP state is accepted", func(t *testing.T) {
		states := []string{
			"ESTABLISHED", "SYN_SENT", "SYN_RECV", "FIN_WAIT1", "FIN_WAIT2", "TIME_WAIT",
			"CLOSE", "CLOSE_WAIT", "LAST_ACK", "LISTEN", "CLOSING", "NEW_SYN_RECV",
		}
		for _, s := range states {
			p := &model.ConnectionSnapshot{States: []model.StateCount{{Proto: model.ProtoTCP, State: s, Count: 1}}}
			if err := p.Validate(); err != nil {
				t.Errorf("Validate() with state %q = %v, want nil", s, err)
			}
		}
	})

	t.Run("every protocol is accepted", func(t *testing.T) {
		for _, proto := range []model.Proto{model.ProtoTCP, model.ProtoTCP6, model.ProtoUDP, model.ProtoUDP6} {
			p := &model.ConnectionSnapshot{States: []model.StateCount{{Proto: proto, State: "LISTEN", Count: 1}}}
			if err := p.Validate(); err != nil {
				t.Errorf("Validate() with proto %q = %v, want nil", proto, err)
			}
		}
	})
}

func TestConnectionSnapshot_Validate_Invalid(t *testing.T) {
	cases := []invalidCase[*model.ConnectionSnapshot]{
		{"too many states", func(p *model.ConnectionSnapshot) {
			p.States = repeat(model.StateCount{Proto: model.ProtoUDP, Count: 1}, model.MaxItems+1)
		}, "states"},
		{"too many processes", func(p *model.ConnectionSnapshot) {
			p.Processes = repeat(model.ProcessConnections{PID: 1, Command: "c", Count: 1}, model.MaxItems+1)
		}, "processes"},
		{"too many remotes", func(p *model.ConnectionSnapshot) {
			p.Remotes = repeat(model.RemoteCount{Addr: netip.MustParseAddr("192.0.2.1"), Count: 1}, model.MaxItems+1)
		}, "remotes"},
		{"too many listeners", func(p *model.ConnectionSnapshot) {
			p.Listeners = repeat(model.Listener{Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:80")}, model.MaxItems+1)
		}, "listeners"},
		{"too many connections", func(p *model.ConnectionSnapshot) {
			p.Connections = repeat(model.Connection{Proto: model.ProtoUDP, Local: netip.MustParseAddrPort("10.0.0.1:1"), Remote: netip.MustParseAddrPort("10.0.0.2:2")}, model.MaxItems+1)
		}, "connections"},

		{"state proto empty", func(p *model.ConnectionSnapshot) { p.States[0].Proto = "" }, "states[0].proto"},
		{"state proto upper case", func(p *model.ConnectionSnapshot) { p.States[0].Proto = "TCP" }, "states[0].proto"},
		{"state proto unknown", func(p *model.ConnectionSnapshot) { p.States[0].Proto = "sctp" }, "states[0].proto"},
		{"listener proto unknown", func(p *model.ConnectionSnapshot) { p.Listeners[0].Proto = "sctp" }, "listeners[0].proto"},
		{"connection proto unknown", func(p *model.ConnectionSnapshot) { p.Connections[0].Proto = "sctp" }, "connections[0].proto"},

		{"tcp state empty", func(p *model.ConnectionSnapshot) { p.States[0].State = "" }, "states[0].state"},
		{"tcp6 state empty", func(p *model.ConnectionSnapshot) { p.States[0].Proto, p.States[0].State = model.ProtoTCP6, "" }, "states[0].state"},
		{"tcp state unknown", func(p *model.ConnectionSnapshot) { p.States[0].State = "BOGUS" }, "states[0].state"},
		{"tcp state lower case", func(p *model.ConnectionSnapshot) { p.States[0].State = "established" }, "states[0].state"},
		{"udp state unknown", func(p *model.ConnectionSnapshot) { p.States[1].State = "BOGUS" }, "states[1].state"},
		{"connection tcp state empty", func(p *model.ConnectionSnapshot) { p.Connections[0].State = "" }, "connections[0].state"},
		{"connection udp state unknown", func(p *model.ConnectionSnapshot) { p.Connections[1].State = "BOGUS" }, "connections[1].state"},

		{"state count zero", func(p *model.ConnectionSnapshot) { p.States[0].Count = 0 }, "states[0].count"},
		{"process count zero", func(p *model.ConnectionSnapshot) { p.Processes[0].Count = 0 }, "processes[0].count"},
		{"remote count zero", func(p *model.ConnectionSnapshot) { p.Remotes[0].Count = 0 }, "remotes[0].count"},

		{"process pid zero", func(p *model.ConnectionSnapshot) { p.Processes[0].PID = 0 }, "processes[0].pid"},
		{"process command empty", func(p *model.ConnectionSnapshot) { p.Processes[0].Command = "" }, "processes[0].command"},
		{"process command too long", func(p *model.ConnectionSnapshot) {
			p.Processes[0].Command = strings.Repeat("c", model.MaxShortTextBytes+1)
		}, "processes[0].command"},

		{"remote addr invalid", func(p *model.ConnectionSnapshot) { p.Remotes[0].Addr = netip.Addr{} }, "remotes[0].addr"},
		{"listener local invalid", func(p *model.ConnectionSnapshot) { p.Listeners[0].Local = netip.AddrPort{} }, "listeners[0].local"},
		{"listener pid negative", func(p *model.ConnectionSnapshot) { p.Listeners[0].PID = -1 }, "listeners[0].pid"},
		{"listener command too long", func(p *model.ConnectionSnapshot) {
			p.Listeners[0].Command = strings.Repeat("c", model.MaxShortTextBytes+1)
		}, "listeners[0].command"},
		{"connection local invalid", func(p *model.ConnectionSnapshot) { p.Connections[0].Local = netip.AddrPort{} }, "connections[0].local"},
		{"connection remote invalid", func(p *model.ConnectionSnapshot) { p.Connections[0].Remote = netip.AddrPort{} }, "connections[0].remote"},
		{"connection pid negative", func(p *model.ConnectionSnapshot) { p.Connections[0].PID = -1 }, "connections[0].pid"},
		{"connection command too long", func(p *model.ConnectionSnapshot) {
			p.Connections[0].Command = strings.Repeat("c", model.MaxShortTextBytes+1)
		}, "connections[0].command"},
	}
	runInvalid(t, validConnectionSnapshot, cases)
}

func TestConnectionSnapshot_Validate_InvalidAddressReason(t *testing.T) {
	p := validConnectionSnapshot()
	p.Remotes[0].Addr = netip.Addr{}
	fe := fieldErrorOf(t, p.Validate(), "remotes[0].addr")
	if fe.Reason != "invalid address" {
		t.Errorf("Validate() reason = %q, want %q", fe.Reason, "invalid address")
	}
}

func TestConnectionSnapshot_Validate_Zone(t *testing.T) {
	zones := map[string]string{
		"interface name": "eth0",
		"newline":        "\n<b>x]:",
		"5000 bytes":     strings.Repeat("z", 5000),
		"bracket colon":  "x]:",
	}
	const lonely = "fe80::1"
	endpoint := func(t *testing.T, ip, zone string) netip.AddrPort {
		t.Helper()
		return netip.AddrPortFrom(zoned(t, ip, zone), 80)
	}
	for zname, zone := range zones {
		t.Run("remote addr "+zname, func(t *testing.T) {
			p := validConnectionSnapshot()
			p.Remotes[0].Addr = zoned(t, lonely, zone)
			fe := fieldErrorOf(t, p.Validate(), "remotes[0].addr")
			if fe.Reason != "zone not allowed" {
				t.Errorf("Validate() reason = %q, want %q", fe.Reason, "zone not allowed")
			}
		})
		t.Run("listener local "+zname, func(t *testing.T) {
			p := validConnectionSnapshot()
			p.Listeners[0].Local = endpoint(t, lonely, zone)
			fe := fieldErrorOf(t, p.Validate(), "listeners[0].local")
			if fe.Reason != "zone not allowed" {
				t.Errorf("Validate() reason = %q, want %q", fe.Reason, "zone not allowed")
			}
		})
		t.Run("connection local "+zname, func(t *testing.T) {
			p := validConnectionSnapshot()
			p.Connections[0].Local = endpoint(t, lonely, zone)
			fe := fieldErrorOf(t, p.Validate(), "connections[0].local")
			if fe.Reason != "zone not allowed" {
				t.Errorf("Validate() reason = %q, want %q", fe.Reason, "zone not allowed")
			}
		})
		t.Run("connection remote "+zname, func(t *testing.T) {
			p := validConnectionSnapshot()
			p.Connections[0].Remote = endpoint(t, lonely, zone)
			fe := fieldErrorOf(t, p.Validate(), "connections[0].remote")
			if fe.Reason != "zone not allowed" {
				t.Errorf("Validate() reason = %q, want %q", fe.Reason, "zone not allowed")
			}
		})
	}

	t.Run("IPv4-mapped address with zone", func(t *testing.T) {
		p := validConnectionSnapshot()
		p.Remotes[0].Addr = zoned(t, "::ffff:1.2.3.4", "eth0")
		fe := fieldErrorOf(t, p.Validate(), "remotes[0].addr")
		if fe.Reason != "zone not allowed" {
			t.Errorf("Validate() reason = %q, want %q", fe.Reason, "zone not allowed")
		}
	})

	t.Run("same addresses without zone pass", func(t *testing.T) {
		p := &model.ConnectionSnapshot{
			Remotes:   []model.RemoteCount{{Addr: netip.MustParseAddr(lonely), Count: 1}, {Addr: netip.MustParseAddr("::ffff:1.2.3.4"), Count: 1}},
			Listeners: []model.Listener{{Proto: model.ProtoTCP6, Local: netip.MustParseAddrPort("[fe80::1]:80")}},
		}
		requireValid(t, p)
	})
}
