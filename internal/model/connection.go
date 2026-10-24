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

// ListenerCheck says how far the listener inventory was compared with the previous run.
type ListenerCheck string

// Listener checks.
const (
	ListenerCheckAll        ListenerCheck = "all"
	ListenerCheckPrivileged ListenerCheck = "privileged"
	ListenerCheckNone       ListenerCheck = "none"
	ListenerCheckStart      ListenerCheck = "start"
)

// InventoryState says how much of the run's listener inventory the record's own listeners list holds.
type InventoryState string

// Inventory states.
const (
	InventoryAll        InventoryState = "all"
	InventoryPrivileged InventoryState = "privileged"
	InventoryCut        InventoryState = "cut"
	InventorySkipped    InventoryState = "skipped"
)

// ConnectionSnapshot is the socket overview at one moment.
type ConnectionSnapshot struct {
	Complete             bool                 `json:"complete"`
	AggregatesOnly       bool                 `json:"aggregates_only,omitempty"`
	AggregatesIncomplete bool                 `json:"aggregates_incomplete,omitempty"`
	ListenerCheck        ListenerCheck        `json:"listener_check,omitempty"`
	States               []StateCount         `json:"states,omitempty"`
	Processes            []ProcessConnections `json:"processes,omitempty"`
	Remotes              []RemoteCount        `json:"remotes,omitempty"`
	Listeners            []Listener           `json:"listeners,omitempty"`
	Connections          []Connection         `json:"connections,omitempty"`
	NewListeners         []Listener           `json:"new_listeners,omitempty"`
	NewListenersOmitted  uint32               `json:"new_listeners_omitted,omitempty"`
	Inventory            InventoryState       `json:"inventory,omitempty"`
	Tables               []Proto              `json:"tables,omitempty"`
	// Trigger names the kills that caused this snapshot; nil for a scheduled one.
	Trigger *SnapshotTrigger `json:"trigger,omitempty"`
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
	// UID is the socket's UID from the kernel table; nil means unknown, 0 is root.
	UID *uint32 `json:"uid,omitempty"`
	// User is the name of UID, or its decimal form when no name is known.
	User string `json:"user,omitempty"`
	// MixedUIDs is true when sockets of several UIDs share the endpoint; UID is then the lowest.
	MixedUIDs bool `json:"mixed_uids,omitempty"`
}

// Connection is a socket with a remote endpoint.
type Connection struct {
	Proto   Proto          `json:"proto"`
	Local   netip.AddrPort `json:"local"`
	Remote  netip.AddrPort `json:"remote"`
	State   string         `json:"state,omitempty"`
	PID     int32          `json:"pid,omitempty"`
	Command string         `json:"command,omitempty"`
	// UID is the socket's UID from the kernel table; nil means unknown, 0 is root.
	UID *uint32 `json:"uid,omitempty"`
	// User is the name of UID, or its decimal form when no name is known.
	User string `json:"user,omitempty"`
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
	if err := s.validateCounts(); err != nil {
		return err
	}
	if err := s.validateFlags(); err != nil {
		return err
	}
	if err := s.validateInventory(); err != nil {
		return err
	}
	if err := s.validateStates(); err != nil {
		return err
	}
	if err := s.validateProcesses(); err != nil {
		return err
	}
	if err := s.validateRemotes(); err != nil {
		return err
	}
	if err := checkListeners("listeners", s.Listeners); err != nil {
		return err
	}
	if err := s.validateConnections(); err != nil {
		return err
	}
	if err := checkListeners("new_listeners", s.NewListeners); err != nil {
		return err
	}
	return validateTrigger(s.Trigger)
}

// validateCounts checks the number of entries of every list.
func (s *ConnectionSnapshot) validateCounts() error {
	for _, c := range []struct {
		name string
		n    int
	}{
		{"states", len(s.States)}, {"processes", len(s.Processes)}, {"remotes", len(s.Remotes)},
		{"listeners", len(s.Listeners)}, {"connections", len(s.Connections)}, {"new_listeners", len(s.NewListeners)},
	} {
		if err := checkCount(c.name, c.n); err != nil {
			return err
		}
	}
	return nil
}

// validateStates checks the entries of states.
func (s *ConnectionSnapshot) validateStates() error {
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
	return nil
}

// validateProcesses checks the entries of processes.
func (s *ConnectionSnapshot) validateProcesses() error {
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
	return nil
}

// validateRemotes checks the entries of remotes.
func (s *ConnectionSnapshot) validateRemotes() error {
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
	return nil
}

// validateConnections checks the entries of connections.
func (s *ConnectionSnapshot) validateConnections() error {
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
		if err := checkSocketUser(path, e.UID, e.User, false); err != nil {
			return err
		}
	}
	return nil
}

// validateFlags checks the rules that tie the aggregate flags, the listener check and the new listeners together.
func (s *ConnectionSnapshot) validateFlags() error {
	if s.AggregatesOnly && s.Complete {
		return invalid("complete", "must be false for an aggregates-only snapshot")
	}
	if s.AggregatesOnly && len(s.Connections) > 0 {
		return invalid("connections", "must be empty in an aggregates-only snapshot")
	}
	if s.AggregatesIncomplete && s.Complete {
		return invalid("complete", "must be false when aggregates are incomplete")
	}
	if s.ListenerCheck != "" {
		if err := checkOneOf("listener_check", s.ListenerCheck, ListenerCheckAll, ListenerCheckPrivileged, ListenerCheckNone, ListenerCheckStart); err != nil {
			return err
		}
	}
	if (len(s.NewListeners) > 0 || s.NewListenersOmitted > 0) && s.ListenerCheck != ListenerCheckAll && s.ListenerCheck != ListenerCheckPrivileged {
		return invalid("listener_check", "must be all or privileged when new listeners are reported")
	}
	return nil
}

// validateInventory checks the inventory state and the protocols of the tables read.
func (s *ConnectionSnapshot) validateInventory() error {
	if s.Inventory != "" {
		if err := checkOneOf("inventory", s.Inventory, InventoryAll, InventoryPrivileged, InventoryCut, InventorySkipped); err != nil {
			return err
		}
	}
	seen := make(map[Proto]struct{}, len(s.Tables))
	for i, p := range s.Tables {
		path := indexed("tables", i)
		if err := checkOneOf(path, p, ProtoTCP, ProtoTCP6, ProtoUDP, ProtoUDP6); err != nil {
			return err
		}
		if _, dup := seen[p]; dup {
			return invalid(path, "duplicate protocol")
		}
		seen[p] = struct{}{}
	}
	return nil
}

// checkListeners checks the entries of the list name.
func checkListeners(name string, listeners []Listener) error {
	for i := range listeners {
		e := &listeners[i]
		path := indexed(name, i)
		if err := checkProtoOnly(path, e.Proto); err != nil {
			return err
		}
		if err := checkAddrPort(path+".local", e.Local); err != nil {
			return err
		}
		if err := checkProcess(path, e.PID, e.Command); err != nil {
			return err
		}
		if err := checkSocketUser(path, e.UID, e.User, e.MixedUIDs); err != nil {
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

// checkSocketUser checks the socket's UID, user name and mixed-UIDs flag of an entry.
func checkSocketUser(path string, uid *uint32, user string, mixed bool) error {
	if uid == nil {
		if user != "" {
			return invalid(path+".user", "requires uid")
		}
		if mixed {
			return invalid(path+".mixed_uids", "requires uid")
		}
	}
	return checkShort(path+".user", user)
}

func checkProcess(path string, pid int32, command string) error {
	if pid < 0 {
		return invalid(path+".pid", "must not be negative")
	}
	return checkShort(path+".command", command)
}
