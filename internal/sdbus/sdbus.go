// Package sdbus is a minimal, read-only D-Bus client for the system bus: it lists systemd units and reads two
// unit properties, and skips everything else the bus sends.
package sdbus

import (
	"context"
	"errors"
	"net"
)

const (
	// SystemBusPath is the only socket the client connects to.
	SystemBusPath = "/run/dbus/system_bus_socket"
	// MaxReplyBody is the largest body of an awaited reply, in bytes.
	MaxReplyBody = 1 << 20
	// MaxHeaderFields is the largest header-field array of a method return or error, in bytes.
	MaxHeaderFields = 64 << 10
	// MaxMessage is the largest message the reader skips or reads, in bytes.
	MaxMessage = 1 << 27
)

var errNotImplemented = errors.New("not implemented")

// Session is one authenticated connection to the bus.
type Session struct {
	conn   net.Conn
	serial uint32
	uid    int
}

// UnitInfo is one entry of a unit listing.
type UnitInfo struct {
	// ID is the primary name of the unit.
	ID string
	// LoadState is the load state, for example loaded or not-found.
	LoadState string
	// ActiveState is the active state, for example active or inactive.
	ActiveState string
	// SubState is the sub state, for example running or dead.
	SubState string
	// Path is the object path, checked against the D-Bus path grammar.
	Path string
}

// Dial connects to the unix stream socket at path, authenticates as the current user and says Hello.
func Dial(ctx context.Context, path string) (*Session, error) {
	return nil, errNotImplemented
}

// Open authenticates as uid over conn and says Hello; conn is closed by Close.
func Open(ctx context.Context, conn net.Conn, uid int) (*Session, error) {
	return &Session{conn: conn, uid: uid}, errNotImplemented
}

// Close closes the connection.
func (s *Session) Close() error {
	return errNotImplemented
}

// ListUnitsByPatterns lists the loaded units whose primary name matches one of the patterns.
func (s *Session) ListUnitsByPatterns(ctx context.Context, patterns []string) ([]UnitInfo, error) {
	return nil, errNotImplemented
}

// ListUnitsByNames loads and lists the named units, one entry per name in order; at most 64 names per call.
func (s *Session) ListUnitsByNames(ctx context.Context, names []string) ([]UnitInfo, error) {
	return nil, errNotImplemented
}

// GetUint64 reads a uint64 property of the object at path through Properties.Get.
func (s *Session) GetUint64(ctx context.Context, path, iface, property string) (uint64, error) {
	return 0, errNotImplemented
}

// GetUint32 reads a uint32 property of the object at path through Properties.Get.
func (s *Session) GetUint32(ctx context.Context, path, iface, property string) (uint32, error) {
	return 0, errNotImplemented
}
