package model

import (
	"net/netip"
)

// Proto is a socket protocol.
type Proto string

// Socket protocols.
const (
	ProtoTCP  Proto = "tcp"
	ProtoTCP6 Proto = "tcp6"
	ProtoUDP  Proto = "udp"
	ProtoUDP6 Proto = "udp6"
)

// ConnectionSnapshot is the socket overview at one moment.
type ConnectionSnapshot struct {
	Complete    bool                 `json:"complete"`
	States      []StateCount         `json:"states,omitempty"`
	Processes   []ProcessConnections `json:"processes,omitempty"`
	Remotes     []RemoteCount        `json:"remotes,omitempty"`
	Listeners   []Listener           `json:"listeners,omitempty"`
	Connections []Connection         `json:"connections,omitempty"`
}

// StateCount counts sockets per protocol and state.
type StateCount struct {
	Proto Proto  `json:"proto"`
	State string `json:"state,omitempty"`
	Count uint32 `json:"count"`
}

// ProcessConnections counts sockets per process.
type ProcessConnections struct {
	PID     int32  `json:"pid"`
	Command string `json:"command"`
	Count   uint32 `json:"count"`
}

// RemoteCount counts sockets per remote address.
type RemoteCount struct {
	Addr  netip.Addr `json:"addr"`
	Count uint32     `json:"count"`
}

// Listener is a socket without a remote endpoint.
type Listener struct {
	Proto   Proto          `json:"proto"`
	Local   netip.AddrPort `json:"local"`
	PID     int32          `json:"pid,omitempty"`
	Command string         `json:"command,omitempty"`
}

// Connection is a socket with a remote endpoint.
type Connection struct {
	Proto   Proto          `json:"proto"`
	Local   netip.AddrPort `json:"local"`
	Remote  netip.AddrPort `json:"remote"`
	State   string         `json:"state,omitempty"`
	PID     int32          `json:"pid,omitempty"`
	Command string         `json:"command,omitempty"`
}

// Kind returns KindConnectionSnapshot.
func (s *ConnectionSnapshot) Kind() Kind { return KindConnectionSnapshot }

var tcpStates = []string{
	"ESTABLISHED", "SYN_SENT", "SYN_RECV", "FIN_WAIT1", "FIN_WAIT2", "TIME_WAIT", "CLOSE", "CLOSE_WAIT",
	"LAST_ACK", "LISTEN", "CLOSING", "NEW_SYN_RECV",
}

// Validate checks the payload.
func (s *ConnectionSnapshot) Validate() error {
	if s == nil {
		return nilReceiver()
	}
	for _, c := range []struct {
		name string
		n    int
	}{
		{"states", len(s.States)}, {"processes", len(s.Processes)}, {"remotes", len(s.Remotes)},
		{"listeners", len(s.Listeners)}, {"connections", len(s.Connections)},
	} {
		if err := checkCount(c.name, c.n); err != nil {
			return err
		}
	}
	for i := range s.States {
		e := &s.States[i]
		path := indexed("states", i)
		if err := checkProto(path, e.Proto, e.State); err != nil {
			return err
		}
		if err := checkCountValue(path, e.Count); err != nil {
			return err
		}
	}
	for i := range s.Processes {
		e := &s.Processes[i]
		path := indexed("processes", i)
		if e.PID <= 0 {
			return invalid(path+".pid", "must be greater than 0")
		}
		if err := checkRequiredShort(path+".command", e.Command); err != nil {
			return err
		}
		if err := checkCountValue(path, e.Count); err != nil {
			return err
		}
	}
	for i := range s.Remotes {
		e := &s.Remotes[i]
		path := indexed("remotes", i)
		if err := checkAddr(path+".addr", e.Addr); err != nil {
			return err
		}
		if err := checkCountValue(path, e.Count); err != nil {
			return err
		}
	}
	for i := range s.Listeners {
		e := &s.Listeners[i]
		path := indexed("listeners", i)
		if err := checkProtoOnly(path, e.Proto); err != nil {
			return err
		}
		if err := checkAddrPort(path+".local", e.Local); err != nil {
			return err
		}
		if err := checkProcess(path, e.PID, e.Command); err != nil {
			return err
		}
	}
	for i := range s.Connections {
		e := &s.Connections[i]
		path := indexed("connections", i)
		if err := checkProto(path, e.Proto, e.State); err != nil {
			return err
		}
		if err := checkAddrPort(path+".local", e.Local); err != nil {
			return err
		}
		if err := checkAddrPort(path+".remote", e.Remote); err != nil {
			return err
		}
		if err := checkProcess(path, e.PID, e.Command); err != nil {
			return err
		}
	}
	return nil
}

func checkProtoOnly(path string, p Proto) error {
	return checkOneOf(path+".proto", p, ProtoTCP, ProtoTCP6, ProtoUDP, ProtoUDP6)
}

// checkProto checks the protocol and the state: required for TCP, optional for UDP.
func checkProto(path string, p Proto, state string) error {
	if err := checkProtoOnly(path, p); err != nil {
		return err
	}
	if state == "" && (p == ProtoUDP || p == ProtoUDP6) {
		return nil
	}
	if err := checkOneOf(path+".state", state, tcpStates...); err != nil {
		if state == "" {
			return invalid(path+".state", "required")
		}
		return err
	}
	return nil
}

func checkCountValue(path string, n uint32) error {
	if n < 1 {
		return invalid(path+".count", "must be at least 1")
	}
	return nil
}

func checkProcess(path string, pid int32, command string) error {
	if pid < 0 {
		return invalid(path+".pid", "must not be negative")
	}
	return checkShort(path+".command", command)
}
