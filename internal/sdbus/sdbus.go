// Package sdbus is a minimal, read-only D-Bus client for the system bus: it lists systemd units and reads two
// unit properties, and skips everything else the bus sends.
package sdbus

import (
	"bufio"
	"context"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"strconv"
	"time"
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

const (
	maxAuthLine     = 512
	guidDigits      = 32
	maxNamesPerCall = 64
	readBufferSize  = 4096

	destBus      = "org.freedesktop.DBus"
	pathBus      = "/org/freedesktop/DBus"
	destSystemd  = "org.freedesktop.systemd1"
	pathManager  = "/org/freedesktop/systemd1"
	ifaceManager = "org.freedesktop.systemd1.Manager"
	ifaceProps   = "org.freedesktop.DBus.Properties"

	memberHello      = "Hello"
	memberByPatterns = "ListUnitsByPatterns"
	memberByNames    = "ListUnitsByNames"
	memberGet        = "Get"

	unitListSignature = "a(ssssssouso)"
)

var (
	errAuthRefused = errors.New("sdbus: authentication refused")
	errClosed      = fmt.Errorf("sdbus: %w", net.ErrClosed)
)

// Session is one authenticated connection to the bus.
type Session struct {
	conn   net.Conn
	r      *bufio.Reader
	hdr    [fixedHeaderSize]byte
	serial uint32
	uid    int
	err    error
	closed bool
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

// Unit is the state of one unit as List and Read return it.
type Unit struct {
	ID          string
	LoadState   string
	ActiveState string
	SubState    string
	ActiveEnter uint64 // microseconds since the epoch, 0 = never
	Restarts    uint32
}

// List returns the units matching patterns with their states; ActiveEnter and Restarts are not read.
func (s *Session) List(ctx context.Context, patterns []string) ([]Unit, error) {
	return nil, errors.New("sdbus: not implemented")
}

// Read returns one Unit per name, in order.
func (s *Session) Read(ctx context.Context, names []string) ([]Unit, error) {
	return nil, errors.New("sdbus: not implemented")
}

// Dial connects to the unix stream socket at path, authenticates as the current user and says Hello.
func Dial(ctx context.Context, path string) (*Session, error) {
	var dialer net.Dialer

	conn, err := dialer.DialContext(ctx, "unix", path)
	if err != nil {
		return nil, fmt.Errorf("sdbus: connect: %w", err)
	}

	return Open(ctx, conn, os.Geteuid())
}

// Open authenticates as uid over conn and says Hello; conn is closed by Close.
func Open(ctx context.Context, conn net.Conn, uid int) (*Session, error) {
	s := &Session{conn: conn, r: bufio.NewReaderSize(conn, readBufferSize), uid: uid}

	if err := s.authenticate(ctx); err != nil {
		_ = conn.Close()

		return nil, err
	}

	if err := s.hello(ctx); err != nil {
		_ = s.Close()

		return nil, err
	}

	return s, nil
}

// Close closes the connection.
func (s *Session) Close() error {
	if s.closed {
		return nil
	}

	s.closed = true

	if s.err == nil {
		s.err = errClosed
	}

	return s.conn.Close()
}

// ListUnitsByPatterns lists the loaded units whose primary name matches one of the patterns.
func (s *Session) ListUnitsByPatterns(ctx context.Context, patterns []string) ([]UnitInfo, error) {
	var e encoder

	e.stringArray(nil)
	e.stringArray(patterns)

	return s.listUnits(ctx, e, request{member: memberByPatterns, signature: "asas"})
}

// ListUnitsByNames loads and lists the named units, one entry per name in order; calls carry at most 64 names.
func (s *Session) ListUnitsByNames(ctx context.Context, names []string) ([]UnitInfo, error) {
	var units []UnitInfo

	for start := 0; start < len(names); start += maxNamesPerCall {
		chunk := names[start:min(start+maxNamesPerCall, len(names))]

		var e encoder

		e.stringArray(chunk)

		got, err := s.listUnits(ctx, e, request{member: memberByNames, signature: "as"})
		if err != nil {
			return nil, err
		}

		if len(got) != len(chunk) {
			return nil, memberError(memberByNames, errMalformed)
		}

		units = append(units, got...)
	}

	return units, nil
}

// GetUint64 reads a uint64 property of the object at path through Properties.Get.
func (s *Session) GetUint64(ctx context.Context, path, iface, property string) (uint64, error) {
	r, err := s.getProperty(ctx, path, iface, property)
	if err != nil {
		return 0, err
	}

	v, err := decodeVariantUint64(r.order, r.body)
	if err != nil {
		return 0, memberError(memberGet, err)
	}

	return v, nil
}

// GetUint32 reads a uint32 property of the object at path through Properties.Get.
func (s *Session) GetUint32(ctx context.Context, path, iface, property string) (uint32, error) {
	r, err := s.getProperty(ctx, path, iface, property)
	if err != nil {
		return 0, err
	}

	v, err := decodeVariantUint32(r.order, r.body)
	if err != nil {
		return 0, memberError(memberGet, err)
	}

	return v, nil
}

// memberError names the method a failure belongs to.
func memberError(member string, err error) error {
	return fmt.Errorf("sdbus: %s: %w", member, err)
}

// listUnits sends a unit listing call whose body is in e and decodes the reply.
func (s *Session) listUnits(ctx context.Context, e encoder, q request) ([]UnitInfo, error) {
	if e.err != nil {
		return nil, memberError(q.member, e.err)
	}

	q.destination, q.path, q.iface, q.flags, q.body = destSystemd, pathManager, ifaceManager, callNoAutoStart, e.b

	r, err := s.call(ctx, q, unitListSignature)
	if err != nil {
		return nil, err
	}

	units, err := decodeUnits(r.order, r.body)
	if err != nil {
		return nil, memberError(q.member, err)
	}

	return units, nil
}

// getProperty sends a Properties.Get call and returns the reply holding a variant.
func (s *Session) getProperty(ctx context.Context, path, iface, property string) (reply, error) {
	var e encoder

	if validObjectPath(path) {
		e.str(iface)
		e.str(property)
	} else {
		e.err = errInvalidArgument
	}

	if e.err != nil {
		return reply{}, memberError(memberGet, e.err)
	}

	return s.call(ctx, request{
		member: memberGet, destination: destSystemd, path: path, iface: ifaceProps,
		signature: "ss", flags: callNoAutoStart, body: e.b,
	}, "v")
}

// authenticate runs the EXTERNAL authentication for the user id and begins the message stream.
func (s *Session) authenticate(ctx context.Context) error {
	stop, err := s.arm(ctx)
	if err != nil {
		return fmt.Errorf("sdbus: authentication: %w", err)
	}

	defer stop()

	line := "\x00AUTH EXTERNAL " + hex.EncodeToString([]byte(strconv.Itoa(s.uid))) + "\r\n"

	if _, err = s.conn.Write([]byte(line)); err != nil {
		return ioError(ctx, "authentication", err)
	}

	answer, err := s.authLine()
	if err != nil {
		if errors.Is(err, errAuthRefused) {
			return err
		}

		return ioError(ctx, "authentication", err)
	}

	if !validAuthOK(answer) {
		return errAuthRefused
	}

	if _, err = s.conn.Write([]byte("BEGIN\r\n")); err != nil {
		return ioError(ctx, "authentication", err)
	}

	return nil
}

// authLine reads one line of the server, cut at 512 bytes before anything is compared.
func (s *Session) authLine() ([]byte, error) {
	line := make([]byte, 0, maxAuthLine)

	for len(line) < maxAuthLine {
		c, err := s.r.ReadByte()
		if err != nil {
			return nil, err
		}

		line = append(line, c)

		if c == '\n' {
			return line, nil
		}
	}

	return nil, errAuthRefused
}

// validAuthOK reports whether line is "OK " and 32 hex digits and CR LF, and nothing else.
func validAuthOK(line []byte) bool {
	const prefix = "OK "

	if len(line) != len(prefix)+guidDigits+2 || string(line[:len(prefix)]) != prefix || string(line[len(line)-2:]) != "\r\n" {
		return false
	}

	for _, c := range line[len(prefix) : len(line)-2] {
		if c < '0' || c > '9' && c < 'A' || c > 'F' && c < 'a' || c > 'f' {
			return false
		}
	}

	return true
}

// hello says Hello to the bus daemon.
func (s *Session) hello(ctx context.Context) error {
	r, err := s.call(ctx, request{member: memberHello, destination: destBus, path: pathBus, iface: destBus}, "s")
	if err != nil {
		return err
	}

	if err = decodeUniqueName(r.order, r.body); err != nil {
		return memberError(memberHello, err)
	}

	return nil
}

// arm applies the deadline of ctx to the connection and ends a blocked read or write when ctx is cancelled; the
// returned function releases it.
func (s *Session) arm(ctx context.Context) (func(), error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}

	deadline, _ := ctx.Deadline()

	_ = s.conn.SetDeadline(deadline)

	stop := context.AfterFunc(ctx, func() { _ = s.conn.SetDeadline(time.Now()) })

	return func() { stop() }, nil
}

// ioError wraps a failed read or write; the context's error replaces the one it caused, and a deadline of the
// connection is only reported once the context has ended.
func ioError(ctx context.Context, what string, err error) error {
	if _, hasDeadline := ctx.Deadline(); hasDeadline && errors.Is(err, os.ErrDeadlineExceeded) {
		<-ctx.Done() // the connection's deadline and the context's timer fire together; report the context's error
	}

	if ctxErr := ctx.Err(); ctxErr != nil {
		err = ctxErr
	}

	return memberError(what, err)
}

// call sends q and returns the awaited method return, whose signature must be wantSignature. A failure of the
// stream ends the session; an error reply or a body that does not decode leaves it usable.
func (s *Session) call(ctx context.Context, q request, wantSignature string) (reply, error) {
	if s.err != nil {
		return reply{}, s.err
	}

	s.serial++
	if s.serial == 0 {
		s.serial = 1
	}

	msg, err := q.marshal(s.serial)
	if err != nil {
		return reply{}, memberError(q.member, err)
	}

	stop, err := s.arm(ctx)
	if err != nil {
		return reply{}, memberError(q.member, err)
	}

	defer stop()

	if _, err = s.conn.Write(msg); err != nil {
		return reply{}, s.fail(ctx, q.member, err)
	}

	r, err := s.awaitReply(s.serial)
	if err != nil {
		return reply{}, s.fail(ctx, q.member, err)
	}

	if r.isError {
		return reply{}, errorReply(q.member, r.errorName)
	}

	if r.signature != wantSignature {
		return reply{}, memberError(q.member, errSignature)
	}

	return r, nil
}

// errorReply is the failure for an error reply: its name when it follows the grammar, never its body.
func errorReply(member string, name []byte) error {
	if validErrorName(name) {
		return fmt.Errorf("sdbus: %s: error reply %s", member, name)
	}

	return fmt.Errorf("sdbus: %s: error reply", member)
}

// fail ends the session after a failure of the stream and returns the error to report.
func (s *Session) fail(ctx context.Context, member string, err error) error {
	var refusal *protocolError

	switch {
	case errors.As(err, &refusal):
	case errors.Is(err, errTooLarge):
		err = memberError(member, err)
	default:
		err = ioError(ctx, member, err)
	}

	s.err = err
	s.closed = true
	_ = s.conn.Close()

	return err
}

// awaitReply reads messages until the method return or error for serial arrives. Every other message is skipped by
// its length: signals, calls and unknown types without a look at their header fields, returns and errors of other
// calls after their header fields were read and checked.
func (s *Session) awaitReply(serial uint32) (reply, error) {
	for {
		h, err := s.readHeader()
		if err != nil {
			return reply{}, err
		}

		r, found, err := s.readMessage(h, serial)
		if err != nil || found {
			return r, err
		}
	}
}

// readMessage reads the rest of the message behind h; found reports that it is the awaited reply.
func (s *Session) readMessage(h header, serial uint32) (r reply, found bool, err error) {
	if h.typ != msgTypeReturn && h.typ != msgTypeError {
		return reply{}, false, s.discard(h.fieldsLen + h.padding + h.bodyLen)
	}

	f, err := s.readFields(h)
	if err != nil {
		return reply{}, false, err
	}

	if f.replySerial != serial {
		return reply{}, false, s.discard(h.bodyLen)
	}

	r, err = s.readBody(h, f)

	return r, true, err
}

// readHeader reads the fixed header of the next message.
func (s *Session) readHeader() (header, error) {
	if _, err := io.ReadFull(s.r, s.hdr[:]); err != nil {
		return header{}, err
	}

	return parseHeader(&s.hdr)
}

// readFields reads and decodes the header fields of a method return or error.
func (s *Session) readFields(h header) (replyFields, error) {
	if h.fieldsLen > MaxHeaderFields {
		return replyFields{}, errFieldsTooLarge
	}

	raw := make([]byte, h.fieldsLen+h.padding)

	if _, err := io.ReadFull(s.r, raw); err != nil {
		return replyFields{}, err
	}

	f, err := parseFields(h.order, raw[:h.fieldsLen])
	if err != nil {
		return replyFields{}, err
	}

	if !f.hasSerial {
		return replyFields{}, errNoReplySerial
	}

	if h.typ == msgTypeError && !f.hasErrorName {
		return replyFields{}, errNoErrorName
	}

	return f, nil
}

// readBody finishes the awaited reply: an error's body is dropped, a return's body is read if it is within the bound.
func (s *Session) readBody(h header, f replyFields) (reply, error) {
	if h.typ == msgTypeError {
		return reply{isError: true, errorName: f.errorName}, s.discard(h.bodyLen)
	}

	if h.bodyLen > MaxReplyBody {
		return reply{}, errTooLarge
	}

	body := make([]byte, h.bodyLen)

	if _, err := io.ReadFull(s.r, body); err != nil {
		return reply{}, err
	}

	return reply{order: h.order, signature: f.signature, body: body}, nil
}

// discard drops n bytes of the stream without keeping them.
func (s *Session) discard(n int) error {
	_, err := s.r.Discard(n)

	return err
}
