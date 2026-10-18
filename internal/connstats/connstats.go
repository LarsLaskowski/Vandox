// Package connstats collects the socket overview from /proc/net and the descriptors of the processes.
package connstats

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"sync"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Name and Source of the collector.
const (
	Name   = "connections"
	Source = "connections"
)

// Limits of one run.
const (
	TopRemotes      = 25    // remote addresses in a regular snapshot
	MaxSockets      = 65536 // sockets with an inode tracked for owners per run
	MaxRemotes      = 4096  // remote addresses counted per run
	MaxNewListeners = 64    // new listeners reported per run
	PrivilegedPorts = 1024  // ports below are privileged
	EphemeralFirst  = 32768 // first port of the ephemeral range
	EphemeralLast   = 60999 // last port of the ephemeral range
)

// SocketMount is the mount of the kernel's sockets, learned at the agent's start; the zero value is not probed.
type SocketMount struct {
	ID  int32
	Err error
}

// Sources are the places the collector reads; a nil Proc is os.DirFS("/proc").
type Sources struct {
	Proc        fs.FS
	SocketMount SocketMount
}

// Connections is the connection collector.
type Connections struct {
	src  Sources
	log  *slog.Logger
	mu   sync.Mutex
	prev *inventory
}

// New returns the collector over src; it logs through log.
func New(src Sources, log *slog.Logger) *Connections {
	return &Connections{src: src, log: log}
}

// Name returns Name.
func (c *Connections) Name() string {
	return Name
}

// Collect takes one connection snapshot.
func (c *Connections) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	return nil, errors.New("not implemented")
}
