package sdbus

import (
	"bufio"
	"bytes"
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"io"
	"math"
	"net"
	"os"
	"strings"
	"sync"
	"testing"
	"time"
)

// testUID is the user the fake bus authenticates in most tests: the one of the measurement (nobody).
const testUID = 65534

// testGuard bounds a call that must not block in a correct client; it never elapses in a passing run.
const testGuard = 30 * time.Second

// zeroPiece is the shared source of the lazily generated bodies, allocated once outside any measurement.
var zeroPiece = make([]byte, 64<<10)

// toU32 converts a length for the wire.
func toU32(n int) uint32 {
	if n < 0 || int64(n) > math.MaxUint32 {
		panic("length does not fit a uint32")
	}
	return uint32(n)
}

// byteOrder is a byte order that also appends.
type byteOrder interface {
	binary.ByteOrder
	binary.AppendByteOrder
}

// wire builds a message in a byte order, aligning as the D-Bus marshalling does.
type wire struct {
	order byteOrder
	b     []byte
}

func newWire(order byteOrder) *wire {
	return &wire{order: order}
}

func (w *wire) align(n int) {
	for len(w.b)%n != 0 {
		w.b = append(w.b, 0)
	}
}

func (w *wire) u32(v uint32) {
	w.align(4)
	w.b = w.order.AppendUint32(w.b, v)
}

// str writes a string or an object path.
func (w *wire) str(s string) {
	w.u32(toU32(len(s)))
	w.b = append(w.b, s...)
	w.b = append(w.b, 0)
}

// sig writes a signature.
func (w *wire) sig(s string) {
	if len(s) > math.MaxUint8 {
		panic("signature does not fit")
	}
	w.b = append(w.b, byte(len(s)&math.MaxUint8))
	w.b = append(w.b, s...)
	w.b = append(w.b, 0)
}

// headerField is one entry of the header-field array: a code and a variant holding a value.
type headerField struct {
	Code byte
	Sig  string
	// Value is a string for the types o, s and g, a uint32 for u, and a []byte written as is (after aligning to Align).
	Value any
	Align int
}

// fieldsBytes marshals the header-field array content (without its length).
func fieldsBytes(order byteOrder, fields []headerField) []byte {
	w := newWire(order)
	for _, f := range fields {
		w.align(8)
		w.b = append(w.b, f.Code)
		w.sig(f.Sig)
		switch v := f.Value.(type) {
		case string:
			if f.Sig == "g" {
				w.sig(v)
			} else {
				w.str(v)
			}
		case uint32:
			w.u32(v)
		case []byte:
			w.align(max(f.Align, 1))
			w.b = append(w.b, v...)
		}
	}
	return w.b
}

// msgSpec describes a message; zero values give a valid little-endian version 1 message.
type msgSpec struct {
	Order   byte
	Version byte
	Type    byte
	Flags   byte
	Serial  uint32
	Fields  []headerField
	// RawFields replaces the marshalled Fields when not nil.
	RawFields []byte
	// FieldsLen and BodyLen replace the lengths written in the fixed header when not zero.
	FieldsLen uint32
	BodyLen   uint32
	Body      []byte
}

// header returns everything before the body: the fixed header, the fields and the padding to 8 bytes.
func (s msgSpec) header() []byte {
	order := byteOrder(binary.LittleEndian)
	marker := byte('l')
	if s.Order != 0 {
		marker = s.Order
		if marker == 'B' {
			order = binary.BigEndian
		}
	}
	fields := s.RawFields
	if fields == nil {
		fields = fieldsBytes(order, s.Fields)
	}
	version := s.Version
	if version == 0 {
		version = 1
	}
	fieldsLen, bodyLen := toU32(len(fields)), toU32(len(s.Body))
	if s.FieldsLen != 0 {
		fieldsLen = s.FieldsLen
	}
	if s.BodyLen != 0 {
		bodyLen = s.BodyLen
	}
	w := newWire(order)
	w.b = append(w.b, marker, s.Type, s.Flags, version)
	w.u32(bodyLen)
	w.u32(s.Serial)
	w.u32(fieldsLen)
	w.b = append(w.b, fields...)
	w.align(8)
	return w.b
}

// bytes returns the whole message.
func (s msgSpec) bytes() []byte {
	return append(s.header(), s.Body...)
}

// frame is something the fake bus writes: raw bytes, then an optional number of zero bytes generated lazily.
type frame struct {
	raw      []byte
	streamed int64
}

func rawFrame(b []byte) frame {
	return frame{raw: b}
}

// isZero reports whether f is the zero frame, which stands for "the default".
func (f frame) isZero() bool {
	return f.raw == nil && f.streamed == 0
}

// write sends f to w in pieces of at most piece bytes (all at once when piece is 0).
func (f frame) write(w io.Writer, piece int) error {
	data := f.raw
	for len(data) > 0 {
		n := len(data)
		if piece > 0 && n > piece {
			n = piece
		}
		if _, err := w.Write(data[:n]); err != nil {
			return err
		}
		data = data[n:]
	}
	for left := f.streamed; left > 0; {
		n := min(left, int64(len(zeroPiece)))
		if _, err := w.Write(zeroPiece[:n]); err != nil {
			return err
		}
		left -= n
	}
	return nil
}

// methodReturnFrame builds a method return for the call with the given serial.
func methodReturnFrame(replySerial uint32, signature string, body []byte) frame {
	fields := []headerField{
		{Code: fieldReplySerial, Sig: "u", Value: replySerial},
		{Code: fieldDestination, Sig: "s", Value: ":1.1"},
		{Code: fieldSender, Sig: "s", Value: ":1.1580"},
	}
	if signature != "" {
		fields = append(fields, headerField{Code: fieldSignature, Sig: "g", Value: signature})
	}
	return rawFrame(msgSpec{Type: typeMethodReturn, Flags: flagNoReplyExpected, Serial: 77, Fields: fields, Body: body}.bytes())
}

// errorFrame builds an error reply with a text in its body.
func errorFrame(replySerial uint32, name, text string) frame {
	w := newWire(binary.LittleEndian)
	w.str(text)
	fields := []headerField{
		{Code: fieldErrorName, Sig: "s", Value: name},
		{Code: fieldReplySerial, Sig: "u", Value: replySerial},
		{Code: fieldDestination, Sig: "s", Value: ":1.1"},
		{Code: fieldSignature, Sig: "g", Value: "s"},
	}
	return rawFrame(msgSpec{Type: typeError, Flags: flagNoReplyExpected, Serial: 78, Fields: fields, Body: w.b}.bytes())
}

// signalFields are the header fields of a signal with a path, an interface and a member.
func signalFields(path, iface, member string) []headerField {
	return []headerField{
		{Code: fieldPath, Sig: "o", Value: path},
		{Code: fieldInterface, Sig: "s", Value: iface},
		{Code: fieldMember, Sig: "s", Value: member},
	}
}

// hugeSignal is a signal whose declared body of n bytes is generated lazily and never held.
func hugeSignal(n int64) frame {
	spec := msgSpec{Type: typeSignal, Flags: flagNoReplyExpected, Serial: 79, Fields: signalFields("/org/example", "org.example.Flood", "Burst"), BodyLen: toU32(int(n))}
	return frame{raw: spec.header(), streamed: n}
}

// pathEscape escapes a unit name as systemd does for its object path: letters and digits stay (a leading digit is
// escaped), every other byte becomes _ and two lower-case hex digits.
func pathEscape(name string) string {
	var out strings.Builder
	for i := range len(name) {
		c := name[i]
		isLetter := c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z'
		if isLetter || c >= '0' && c <= '9' && i > 0 {
			out.WriteByte(c)
			continue
		}
		fmt.Fprintf(&out, "_%02x", c)
	}
	return out.String()
}

// unitEntry is one element of the a(ssssssouso) reply of ListUnits*: id, description, load, active and sub state,
// following unit, object path, job id, job type and job path.
type unitEntry struct {
	ID, Description, Load, Active, Sub, Following, Path string
	JobID                                               uint32
	JobType, JobPath                                    string
}

// unitListBody marshals entries in the layout of the captured ListUnitsByPatterns reply.
func unitListBody(entries []unitEntry) []byte {
	w := newWire(binary.LittleEndian)
	w.u32(0)
	w.align(8)
	start := len(w.b)
	for _, e := range entries {
		w.align(8)
		for _, s := range []string{e.ID, e.Description, e.Load, e.Active, e.Sub, e.Following, e.Path} {
			w.str(s)
		}
		w.u32(e.JobID)
		w.str(e.JobType)
		w.str(e.JobPath)
	}
	w.order.PutUint32(w.b[:4], toU32(len(w.b)-start))
	return w.b
}

// stubEntry is what systemd answers for a name that is not loadable: not-found, inactive, dead.
func stubEntry(name string) unitEntry {
	return unitEntry{ID: name, Description: name, Load: "not-found", Active: "inactive", Sub: "dead", Path: "/org/freedesktop/systemd1/unit/" + pathEscape(name), JobPath: "/"}
}

// capturedUnits builds the entries of the units of the capture from its Get replies (Id, LoadState, ActiveState,
// SubState) and the object paths of its LoadUnit replies, keyed by every name that resolves to the unit.
func capturedUnits(c capture) map[string]unitEntry {
	read := func(pathReply int, calls [4]int) unitEntry {
		e := unitEntry{Path: c.objectPath(c.at(pathReply)), JobPath: "/"}
		for i, want := range []struct {
			property string
			into     *string
		}{{"Id", &e.ID}, {"LoadState", &e.Load}, {"ActiveState", &e.Active}, {"SubState", &e.Sub}} {
			call := calls[i]
			if got := c.property(call); got != want.property {
				c.t.Fatalf("capture message %d asks for %q, want %q", call, got, want.property)
			}
			*want.into = c.variantString(c.at(call + 1))
		}
		e.Description = e.ID
		return e
	}
	mariadb, notFound, masked := read(4, [4]int{5, 7, 9, 11}), read(18, [4]int{21, 25, 29, 33}), read(46, [4]int{49, 53, 57, 61})
	return map[string]unitEntry{mariadb.ID: mariadb, notFound.ID: notFound, masked.ID: masked, "mysql.service": mariadb}
}

// helloFixture holds the measured authentication reply and the Hello exchange of testdata/systemd/hello-dbus-1.14.hex.
type helloFixture struct {
	authOK       string
	methodReturn []byte
	nameAcquired []byte
}

// loadHello reads the hex file: comment lines start with #, the line starting with "OK " is the daemon's answer to
// AUTH, the remaining lines are the hex of the Hello method return followed by the NameAcquired signal.
func loadHello(t *testing.T) helloFixture {
	t.Helper()
	data, err := os.ReadFile(helloPath)
	if err != nil {
		t.Fatalf("reading %s: %v", helloPath, err)
	}
	var fix helloFixture
	var digits strings.Builder
	for _, line := range strings.Split(string(data), "\n") {
		line = strings.TrimSpace(line)
		switch {
		case line == "" || strings.HasPrefix(line, "#"):
		case strings.HasPrefix(line, "OK "):
			fix.authOK = line
		default:
			digits.WriteString(line)
		}
	}
	raw, err := hex.DecodeString(digits.String())
	if err != nil {
		t.Fatalf("%s: %v", helloPath, err)
	}
	first, err := messageSize(raw)
	if err != nil || first >= len(raw) {
		t.Fatalf("%s: first message of %d bytes in %d (%v), want two messages", helloPath, first, len(raw), err)
	}
	if second, err := messageSize(raw[first:]); err != nil || first+second != len(raw) {
		t.Fatalf("%s: second message of %d bytes behind %d of %d (%v), want the end", helloPath, second, first, len(raw), err)
	}
	fix.methodReturn, fix.nameAcquired = raw[:first], raw[first:]
	if len(fix.authOK) != len("OK ")+32 {
		t.Fatalf("%s: auth line %q, want OK and 32 hex digits", helloPath, fix.authOK)
	}
	return fix
}

// patchReplySerial returns a copy of the captured reply with its reply_serial replaced.
func patchReplySerial(reply busMessage, serial uint32) []byte {
	raw := bytes.Clone(reply.Raw)
	reply.Order.PutUint32(raw[reply.ReplySerialAt:], serial)
	return raw
}

// respondFunc answers a request itself; handled false leaves the request to the default behaviour.
type respondFunc func(req busMessage) (frames []frame, handled bool)

// onMember answers the calls of one member with the frames fn returns for the request.
func onMember(member string, fn func(req busMessage) []frame) respondFunc {
	return func(req busMessage) ([]frame, bool) {
		if req.Member != member {
			return nil, false
		}
		return fn(req), true
	}
}

// replyWith answers the calls of member with a method return of the given signature and body.
func replyWith(member, signature string, body []byte) respondFunc {
	return onMember(member, func(req busMessage) []frame {
		return []frame{methodReturnFrame(req.Serial, signature, body)}
	})
}

// fakeBusConfig changes what the fake bus does; the zero value is the measured system bus with the captured replies.
type fakeBusConfig struct {
	// AuthReply replaces the daemon's answer to AUTH (a hostile server, not a measured one).
	AuthReply frame
	// Respond answers a request before the default behaviour does; it also sees Hello.
	Respond respondFunc
	// Preamble returns the frames sent before the reply of every call except Hello (messages nobody asked for).
	Preamble func(req busMessage) []frame
	// Piece splits every write of a raw frame into pieces of this many bytes.
	Piece int
}

// fakeBus is the server end of a connection to a system bus: EXTERNAL authentication with the measured answer, Hello
// with the measured method return and unicast NameAcquired signal, and for the calls of the collector the captured
// replies of systemd 249 with the reply serial patched, or a ListUnitsByNames reply built in the captured layout.
// It records the authentication lines and every request.
type fakeBus struct {
	t       *testing.T
	cfg     fakeBusConfig
	hello   helloFixture
	replays map[string]busMessage
	units   map[string]unitEntry
	out     chan frame
	wg      sync.WaitGroup
	closed  chan struct{}

	mu        sync.Mutex
	authLines []string
	requests  []busMessage
}

// newFakeBus creates a fake bus; it serves a connection once pipe or listen hands it one.
func newFakeBus(t *testing.T, cfg fakeBusConfig) *fakeBus {
	t.Helper()
	c := loadCapture(t)
	return &fakeBus{t: t, cfg: cfg, hello: loadHello(t), replays: c.replies(), units: capturedUnits(c), out: make(chan frame, 4096), closed: make(chan struct{})}
}

// pipe returns the client end of a net.Pipe whose other end the fake bus serves.
func (b *fakeBus) pipe() net.Conn {
	client, server := net.Pipe()
	b.serve(server)
	return client
}

// listen serves the first connection to a unix stream socket and returns the socket's path, which is relative to the
// working directory of the test (t.Chdir into a temporary directory keeps it far below the 108 bytes of a socket path).
func (b *fakeBus) listen() string {
	b.t.Helper()
	b.t.Chdir(b.t.TempDir())
	const path = "bus"
	lis, err := net.Listen("unix", path)
	if err != nil {
		b.t.Fatalf("listening on %s: %v", path, err)
	}
	b.wg.Add(1)
	go func() {
		defer b.wg.Done()
		conn, err := lis.Accept()
		_ = lis.Close()
		if err != nil {
			return
		}
		b.serve(conn)
	}()
	b.t.Cleanup(func() { _ = lis.Close(); b.wg.Wait() })
	return path
}

// serve starts the goroutines of one connection and closes it when the test ends.
func (b *fakeBus) serve(conn net.Conn) {
	b.wg.Add(2)
	go b.read(conn)
	go b.write(conn)
	b.t.Cleanup(func() {
		_ = conn.Close()
		b.wg.Wait()
	})
}

// write sends the queued frames in order.
func (b *fakeBus) write(conn net.Conn) {
	defer b.wg.Done()
	for f := range b.out {
		if err := f.write(conn, b.cfg.Piece); err != nil {
			for range b.out { // keep draining so that the reader never blocks
			}
			return
		}
	}
}

// read authenticates the client and answers its requests.
func (b *fakeBus) read(conn net.Conn) {
	defer b.wg.Done()
	defer close(b.out)
	defer close(b.closed)
	r := bufio.NewReader(conn)
	if !b.authenticate(r) {
		return
	}
	for {
		raw, err := readRaw(r)
		if err != nil {
			return
		}
		req, err := parseMessage(raw)
		if err != nil {
			b.t.Errorf("fake bus: unreadable request: %v", err)
			return
		}
		b.mu.Lock()
		b.requests = append(b.requests, req)
		b.mu.Unlock()
		b.answer(req)
	}
}

// readRaw reads one whole message.
func readRaw(r io.Reader) ([]byte, error) {
	head := make([]byte, 16)
	if _, err := io.ReadFull(r, head); err != nil {
		return nil, err
	}
	size, err := messageSize(head)
	if err != nil {
		return nil, err
	}
	raw := make([]byte, size)
	copy(raw, head)
	_, err = io.ReadFull(r, raw[16:])
	return raw, err
}

// authenticate reads the NUL byte and the lines up to BEGIN and answers AUTH; it reports whether messages follow.
func (b *fakeBus) authenticate(r *bufio.Reader) bool {
	if first, err := r.ReadByte(); err != nil || first != 0 {
		if err == nil {
			b.t.Errorf("fake bus: the client began with byte %#x, want NUL", first)
		}
		return false
	}
	for {
		line, err := r.ReadString('\n')
		if err != nil {
			return false
		}
		b.mu.Lock()
		b.authLines = append(b.authLines, line)
		b.mu.Unlock()
		switch {
		case strings.HasPrefix(line, "AUTH "):
			b.out <- b.authAnswer()
		case line == "BEGIN\r\n":
			return true
		default:
			b.t.Errorf("fake bus: unexpected line %q during authentication", line)
			b.out <- rawFrame([]byte("ERROR\r\n"))
		}
	}
}

// authAnswer is the measured "OK <guid>" line or the configured replacement.
func (b *fakeBus) authAnswer() frame {
	if b.cfg.AuthReply.isZero() {
		return rawFrame([]byte(b.hello.authOK + "\r\n"))
	}
	return b.cfg.AuthReply
}

// answer queues the frames for one request.
func (b *fakeBus) answer(req busMessage) {
	isHello := req.Destination == busName && req.Member == "Hello"
	if req.Type != typeMethodCall {
		b.t.Errorf("fake bus: the client sent a message of type %d, want only method calls", req.Type)
		return
	}
	if b.cfg.Preamble != nil && !isHello {
		for _, f := range b.cfg.Preamble(req) {
			b.out <- f
		}
	}
	frames, handled := nilFrames, false
	if b.cfg.Respond != nil {
		frames, handled = b.cfg.Respond(req)
	}
	if !handled {
		frames = b.defaultReply(req, isHello)
	}
	for _, f := range frames {
		b.out <- f
	}
}

var nilFrames []frame

// defaultReply is what the fake bus does without a configured answer.
func (b *fakeBus) defaultReply(req busMessage, isHello bool) []frame {
	if isHello {
		reply, err := parseMessage(b.hello.methodReturn)
		if err != nil {
			b.t.Fatalf("hello fixture: %v", err)
		}
		both := append(patchReplySerial(reply, req.Serial), b.hello.nameAcquired...)
		return []frame{rawFrame(both)}
	}
	if req.Interface == managerIface && req.Member == "ListUnitsByNames" {
		return []frame{b.unitsByNames(req)}
	}
	if reply, ok := b.replays[req.callKey()]; ok {
		return []frame{rawFrame(patchReplySerial(reply, req.Serial))}
	}
	b.t.Errorf("fake bus: %s %s.%s(%q) is not in the capture", req.Path, req.Interface, req.Member, req.Body)
	return []frame{errorFrame(req.Serial, "org.freedesktop.DBus.Error.UnknownMethod", "not in the capture")}
}

// unitsByNames answers ListUnitsByNames with one entry per name in order, in the captured layout: the captured units
// (an alias answers with its primary unit) and a not-found stub for any other name.
func (b *fakeBus) unitsByNames(req busMessage) frame {
	r := &bodyReader{t: b.t, order: req.Order, b: req.Body}
	var entries []unitEntry
	for _, name := range r.strings() {
		e, ok := b.units[name]
		if !ok {
			e = stubEntry(name)
		}
		entries = append(entries, e)
	}
	return methodReturnFrame(req.Serial, "a(ssssssouso)", unitListBody(entries))
}

// recorded returns a copy of the requests received so far.
func (b *fakeBus) recorded() []busMessage {
	b.mu.Lock()
	defer b.mu.Unlock()
	return append([]busMessage(nil), b.requests...)
}

// authentication returns a copy of the authentication lines received so far, with their line ends.
func (b *fakeBus) authentication() []string {
	b.mu.Lock()
	defer b.mu.Unlock()
	return append([]string(nil), b.authLines...)
}

// calls returns the recorded requests of one member.
func (b *fakeBus) calls(member string) []busMessage {
	var out []busMessage
	for _, req := range b.recorded() {
		if req.Member == member {
			out = append(out, req)
		}
	}
	return out
}

// waitClosed waits until the client has closed its end of the connection.
func (b *fakeBus) waitClosed() {
	b.t.Helper()
	select {
	case <-b.closed:
	case <-time.After(testGuard):
		b.t.Fatalf("the client did not close the connection within %v", testGuard)
	}
}
