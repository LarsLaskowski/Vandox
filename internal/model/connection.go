package model

import (
	"errors"
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
func (s *ConnectionSnapshot) Kind() Kind { return "" }

// Validate checks the payload.
func (s *ConnectionSnapshot) Validate() error { return errors.New("not implemented") }
