// Package mariadb collects the state of the local MariaDB server over its Unix socket.
package mariadb

import (
	"context"
	"encoding/binary"
	"errors"
	"net"
	"strconv"
	"syscall"
	"time"
)

// User is the name of the MariaDB account the collector logs in with.
const User = "vandox-agent"

// Layout of the protocol the client asks for and accepts.
const (
	protocolVersion = 10
	maxGreeting     = 1024
	maxControl      = 64 * 1024
	maxSwitch       = 1024
	quitWait        = 250 * time.Millisecond

	cmdQuit  = 0x01
	cmdQuery = 0x03
	cmdPing  = 0x0e

	// The captured client's capabilities without LOCAL_FILES, MULTI_STATEMENTS and CONNECT_ATTRS: PROTOCOL_41,
	// SECURE_CONNECTION, SESSION_TRACK, PLUGIN_AUTH and its length-encoded data, but not DEPRECATE_EOF.
	clientCapabilities = 0x00aea204
	// The captured client's extended capabilities without progress reports and bulk operations: extended and cache metadata.
	clientExtended  = 0x18
	clientMaxPacket = 0x00100000
	clientCharset   = 45
	pluginName      = "mysql_native_password"

	flagMySQL          = 0x00000001
	flagProtocol41     = 0x00000200
	flagSecureConn     = 0x00008000
	flagPluginAuth     = 0x00080000
	flagSessionTrack   = 0x00800000
	flagMetadata       = 0x18
	flagMoreResults    = 0x0008
	greetingFixedBytes = 4 + 8 + 1 + 2 + 1 + 2 + 2 + 1 + 6 + 4
)

// Dialer opens a connection to the database.
type Dialer func(ctx context.Context) (net.Conn, error)

// UnixDialer returns a Dialer for the Unix socket at path.
func UnixDialer(path string) Dialer {
	return func(ctx context.Context) (net.Conn, error) {
		var d net.Dialer
		return d.DialContext(ctx, "unix", path)
	}
}

// dialError is a failed connection attempt.
type dialError struct{ errno syscall.Errno }

// Error returns the text of the error; it never holds the path.
func (e *dialError) Error() string {
	if e.errno == 0 {
		return "mariadb: connect: failed"
	}
	return "mariadb: connect: " + e.errno.Error()
}

// serverError is an error packet of the server.
type serverError struct {
	step string
	code uint16
}

// Error returns the text of the error; it never holds the message of the server.
func (e *serverError) Error() string {
	return "mariadb: " + e.step + ": server error " + strconv.Itoa(int(e.code))
}

// protocolError is a reply that breaks the protocol.
type protocolError struct{ step, reason string }

// Error returns the text of the error.
func (e *protocolError) Error() string { return "mariadb: " + e.step + ": " + e.reason }

var (
	errTimeout = errors.New("no answer in time")
	errClosed  = errors.New("connection closed")
)

// conn is a logged-in connection.
type conn struct {
	nc     net.Conn
	pr     *packetReader
	cid    uint32
	closed bool
}

// errorOf returns the error an error packet stands for.
func errorOf(step string, payload []byte) error {
	if len(payload) < 3 {
		return malformed(step)
	}
	return &serverError{step: step, code: binary.LittleEndian.Uint16(payload[1:])}
}

// dialFailure returns the error of a failed dial: the errno or a timeout, nothing else.
func dialFailure(ctx context.Context, err error) error {
	var errno syscall.Errno
	if errors.As(err, &errno) {
		return &dialError{errno: errno}
	}
	var ne net.Error
	if ctx.Err() != nil || (errors.As(err, &ne) && ne.Timeout()) {
		return &stepError{step: "connect", err: errTimeout}
	}
	return &dialError{}
}

// open connects, reads the greeting by greetingBy and logs in.
func open(ctx context.Context, dial Dialer, greetingBy time.Time) (*conn, error) {
	nc, err := dial(ctx)
	if err != nil {
		return nil, dialFailure(ctx, err)
	}
	c := &conn{nc: nc, pr: newPacketReader(nc)}
	stop := context.AfterFunc(ctx, c.expire)
	defer stop()
	deadline := greetingBy
	if d, ok := ctx.Deadline(); ok && d.Before(deadline) {
		deadline = d
	}
	_ = nc.SetDeadline(deadline)
	if err = c.greet(); err == nil {
		c.arm(ctx)
		err = c.login()
	}
	if err != nil {
		_ = nc.Close()
		return nil, err
	}
	return c, nil
}

// expire makes every pending and future wait of the connection end at once.
func (c *conn) expire() { _ = c.nc.SetDeadline(time.Unix(1, 0)) }

// arm sets the deadline of the connection to the one of ctx.
func (c *conn) arm(ctx context.Context) {
	d, _ := ctx.Deadline()
	_ = c.nc.SetDeadline(d)
}

// send writes a packet of the client; the next packet to read is the one after it.
func (c *conn) send(step string, seq byte, payload []byte) error {
	c.pr.seq = seq + 1
	if err := writePacket(c.nc, seq, payload); err != nil {
		return ioError(step, err)
	}
	return nil
}

// greet reads and checks the greeting.
func (c *conn) greet() error {
	const step = "greeting"
	p, err := c.pr.next(step, maxGreeting)
	if err != nil {
		return err
	}
	if len(p) > 0 && p[0] == 0xff {
		return errorOf(step, p)
	}
	end := 1
	for end < len(p) && p[end] != 0 {
		end++
	}
	if len(p) < 1 || p[0] != protocolVersion || len(p) < end+1+greetingFixedBytes {
		return &protocolError{step: step, reason: reasonUnexpected}
	}
	b := p[end+1:]
	caps := uint32(binary.LittleEndian.Uint16(b[13:])) | uint32(binary.LittleEndian.Uint16(b[18:]))<<16
	ext := binary.LittleEndian.Uint32(b[27:])
	const need = flagProtocol41 | flagSecureConn | flagPluginAuth | flagSessionTrack
	if caps&flagMySQL != 0 || caps&need != need || ext&flagMetadata != flagMetadata {
		return &protocolError{step: step, reason: reasonUnexpected}
	}
	c.cid = binary.LittleEndian.Uint32(b)
	return nil
}

// handshakeResponse builds the handshake response: the user, no authentication data and the plugin's name.
func handshakeResponse() []byte {
	out := binary.LittleEndian.AppendUint32(make([]byte, 0, 64), clientCapabilities)
	out = binary.LittleEndian.AppendUint32(out, clientMaxPacket)
	out = append(out, clientCharset)
	out = append(out, make([]byte, 19)...)
	out = binary.LittleEndian.AppendUint32(out, clientExtended)
	out = append(out, User...)
	out = append(out, 0, 0)
	out = append(out, pluginName...)
	return append(out, 0)
}

// isSwitch reports whether p asks to switch the authentication method: 0xfe, a plugin name and its data.
func isSwitch(p []byte) bool {
	return len(p) > 1 && p[0] == 0xfe && p[1] != 0
}

// login sends the handshake response and reads the answer. A request to switch the method gets one empty answer
// and no credential; the answer after it counts as the login's.
func (c *conn) login() error {
	const step = "login"
	if err := c.send(step, 1, handshakeResponse()); err != nil {
		return err
	}
	switched := false
	for {
		p, err := c.pr.next(step, maxControl)
		if err != nil {
			return err
		}
		switch {
		case len(p) == 0:
			return &protocolError{step: step, reason: reasonUnexpected}
		case p[0] == 0x00:
			return nil
		case p[0] == 0xff:
			return errorOf(step, p)
		case switched && (p[0] == 0xfe || p[0] == 0x01):
			return &protocolError{step: step, reason: reasonSwitchDenied}
		case p[0] == 0x01 && len(p) > 2:
			return &protocolError{step: step, reason: reasonSwitchDenied}
		case len(p) > maxSwitch && isSwitch(p):
			return &protocolError{step: step, reason: reasonTooLarge}
		case isSwitch(p):
			switched = true
			if err = c.send(step, c.pr.seq, nil); err != nil {
				return err
			}
		default:
			return &protocolError{step: step, reason: reasonUnexpected}
		}
	}
}

// id returns the connection id the server assigned.
func (c *conn) id() uint32 { return c.cid }

// begin arms the deadline of ctx for one command and makes a cancellation end its waits; end releases it.
func (c *conn) begin(ctx context.Context) (end func()) {
	c.arm(ctx)
	stop := context.AfterFunc(ctx, c.expire)
	return func() { stop() }
}

// closeOnError closes the connection when a command failed: what is left of its reply cannot be told apart from
// the next one.
func (c *conn) closeOnError(err *error) {
	if *err != nil {
		c.close()
	}
}

// ping sends a ping and reads the answer.
func (c *conn) ping(ctx context.Context) (err error) {
	const step = "ping"
	defer c.begin(ctx)()
	defer c.closeOnError(&err)
	if err := c.send(step, 0, []byte{cmdPing}); err != nil {
		return err
	}
	p, err := c.pr.next(step, maxControl)
	if err != nil {
		return err
	}
	switch {
	case len(p) > 0 && p[0] == 0x00:
		return nil
	case len(p) > 0 && p[0] == 0xff:
		return errorOf(step, p)
	}
	return &protocolError{step: step, reason: reasonUnexpected}
}

// query runs s and calls row for every row; row reads the cells of that row.
func (c *conn) query(ctx context.Context, s statement, row func(p *packetReader) error) (err error) {
	step := s.step()
	defer c.begin(ctx)()
	defer c.closeOnError(&err)
	if err := c.send(step, 0, append([]byte{cmdQuery}, s.text()...)); err != nil {
		return err
	}
	if err := c.readHeaderOfResult(step, s.columns()); err != nil {
		return err
	}
	for {
		first, err := c.pr.beginRow(step)
		if err != nil {
			return err
		}
		if done, err := c.rowEnd(step, first); done || err != nil {
			return err
		}
		if err = row(c.pr); err != nil {
			return err
		}
	}
}

// rowEnd reports whether the packet that began with first ends the rows, and with what error.
func (c *conn) rowEnd(step string, first byte) (bool, error) {
	if first != 0xff && (first != 0xfe || c.pr.remaining >= 9) {
		return false, nil
	}
	p, err := c.pr.rest(step, maxControl)
	if err != nil {
		return true, err
	}
	if first == 0xff {
		return true, errorOf(step, p)
	}
	return true, endOf(step, p)
}

// endOf checks the EOF packet that ends a part of a result and returns nil when the result goes on as expected.
func endOf(step string, p []byte) error {
	if len(p) > 0 && p[0] == 0xff {
		return errorOf(step, p)
	}
	if len(p) < 5 || p[0] != 0xfe || len(p) >= 9 {
		return &protocolError{step: step, reason: reasonUnexpected}
	}
	if binary.LittleEndian.Uint16(p[3:])&flagMoreResults != 0 {
		return &protocolError{step: step, reason: reasonMoreResults}
	}
	return nil
}

// readHeaderOfResult reads the column count, the column definitions and the EOF packet after them.
func (c *conn) readHeaderOfResult(step string, cols []column) error {
	p, err := c.pr.next(step, maxControl)
	if err != nil {
		return err
	}
	switch {
	case len(p) == 0:
		return malformed(step)
	case p[0] == 0xff:
		return errorOf(step, p)
	case p[0] == 0xfb:
		return &protocolError{step: step, reason: reasonLocalFile}
	case p[0] == 0x00 || p[0] >= 0xfb || len(p) != 2 || p[1] != 1:
		return &protocolError{step: step, reason: reasonUnexpected}
	case int(p[0]) != len(cols):
		return &protocolError{step: step, reason: reasonColumns}
	}
	for _, col := range cols {
		if p, err = c.pr.next(step, maxControl); err != nil {
			return err
		}
		if err = checkDefinition(step, p, col); err != nil {
			return err
		}
	}
	if p, err = c.pr.next(step, maxControl); err != nil {
		return err
	}
	return endOf(step, p)
}

// takeString takes a length-encoded string off the front of b.
func takeString(b []byte) (s, rest []byte, ok bool) {
	if len(b) == 0 || b[0] >= 0xfb {
		return nil, nil, false
	}
	n := int(b[0])
	if len(b) < 1+n {
		return nil, nil, false
	}
	return b[1 : 1+n], b[1+n:], true
}

// checkDefinition checks the name of a column definition: catalog, schema, table, original table, name.
func checkDefinition(step string, def []byte, col column) error {
	if len(def) > 0 && def[0] == 0xff {
		return errorOf(step, def)
	}
	rest := def
	var name []byte
	for i := 0; i < 5; i++ {
		var ok bool
		if name, rest, ok = takeString(rest); !ok {
			return malformed(step)
		}
	}
	if string(name) != col.name {
		return &protocolError{step: step, reason: reasonColumns}
	}
	return nil
}

// close closes the connection.
func (c *conn) close() {
	if c.closed {
		return
	}
	c.closed = true
	_ = c.nc.SetWriteDeadline(time.Now().Add(quitWait))
	_ = writePacket(c.nc, 0, []byte{cmdQuit})
	_ = c.nc.Close()
}
