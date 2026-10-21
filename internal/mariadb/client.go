// Package mariadb collects the state of the local MariaDB server over its Unix socket.
package mariadb

import (
	"context"
	"errors"
	"net"
	"syscall"
	"time"
)

// User is the name of the MariaDB account the collector logs in with.
const User = "vandox-agent"

// Dialer opens a connection to the database.
type Dialer func(ctx context.Context) (net.Conn, error)

// UnixDialer returns a Dialer for the Unix socket at path.
func UnixDialer(path string) Dialer {
	_ = path
	return func(context.Context) (net.Conn, error) { return nil, errors.New("not implemented") }
}

// dialError is a failed connection attempt.
type dialError struct{ errno syscall.Errno }

// Error returns the text of the error.
func (e *dialError) Error() string { return "not implemented" }

// serverError is an error packet of the server.
type serverError struct {
	step string
	code uint16
}

// Error returns the text of the error.
func (e *serverError) Error() string { return "not implemented" }

// protocolError is a reply that breaks the protocol.
type protocolError struct{ step, reason string }

// Error returns the text of the error.
func (e *protocolError) Error() string { return "not implemented" }

var (
	errTimeout = errors.New("mariadb: no answer in time")
	errClosed  = errors.New("mariadb: connection closed")
)

// conn is a logged-in connection.
type conn struct {
	nc  net.Conn
	pr  *packetReader
	cid uint32
}

// open connects, reads the greeting by greetingBy and logs in.
func open(ctx context.Context, dial Dialer, greetingBy time.Time) (*conn, error) {
	_, _, _ = ctx, dial, greetingBy
	return nil, errors.New("not implemented")
}

// id returns the connection id the server assigned.
func (c *conn) id() uint32 { return c.cid }

// ping sends a ping and reads the answer.
func (c *conn) ping(ctx context.Context) error {
	_ = ctx
	return errors.New("not implemented")
}

// query runs s and calls row for every row; row reads the cells of that row.
func (c *conn) query(ctx context.Context, s statement, row func(p *packetReader) error) error {
	_, _, _ = ctx, s, row
	return errors.New("not implemented")
}

// close closes the connection.
func (c *conn) close() {}
