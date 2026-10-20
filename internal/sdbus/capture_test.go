package sdbus

import (
	"bytes"
	"encoding/binary"
	"errors"
	"fmt"
	"os"
	"testing"
)

// Message types, flags and header field codes of the D-Bus specification, as the tests need them.
const (
	typeMethodCall   byte = 1
	typeMethodReturn byte = 2
	typeError        byte = 3
	typeSignal       byte = 4

	flagNoReplyExpected byte = 0x1
	flagNoAutoStart     byte = 0x2

	fieldPath        byte = 1
	fieldInterface   byte = 2
	fieldMember      byte = 3
	fieldErrorName   byte = 4
	fieldReplySerial byte = 5
	fieldDestination byte = 6
	fieldSender      byte = 7
	fieldSignature   byte = 8
	fieldUnixFDs     byte = 9
)

// Files and well-known values of the measurements the tests replay.
const (
	capturePath     = "../../testdata/systemd/capture-systemd249.pcap"
	helloPath       = "../../testdata/systemd/hello-dbus-1.14.hex"
	captureMessages = 74
	linkTypeDBus    = 231

	busName      = "org.freedesktop.DBus"
	systemdName  = "org.freedesktop.systemd1"
	managerPath  = "/org/freedesktop/systemd1"
	managerIface = "org.freedesktop.systemd1.Manager"
	unitIface    = "org.freedesktop.systemd1.Unit"
	serviceIface = "org.freedesktop.systemd1.Service"
	propsIface   = "org.freedesktop.DBus.Properties"

	mariadbPath  = "/org/freedesktop/systemd1/unit/mariadb_2eservice"
	notFoundPath = "/org/freedesktop/systemd1/unit/vandox_2ddoes_2dnot_2dexist_2eservice"
	maskedPath   = "/org/freedesktop/systemd1/unit/cloud_2dconfig_2eservice"
	pleskPath    = "/org/freedesktop/systemd1/unit/plesk_2dphp85_2dfpm_2eservice"
)

var errMalformedMessage = errors.New("malformed message")

// busMessage is one D-Bus message as the tests read it: the fixed header, the header fields the tests know and the body.
type busMessage struct {
	Raw         []byte
	Order       binary.ByteOrder
	Type        byte
	Flags       byte
	Serial      uint32
	ReplySerial uint32
	// ReplySerialAt is the offset of the reply_serial value in Raw, 0 when the message has none.
	ReplySerialAt int
	Path          string
	Interface     string
	Member        string
	ErrorName     string
	Destination   string
	Sender        string
	Signature     string
	Body          []byte
}

// byteOrderOf returns the byte order a message announces in its first byte.
func byteOrderOf(marker byte) (binary.ByteOrder, error) {
	switch marker {
	case 'l':
		return binary.LittleEndian, nil
	case 'B':
		return binary.BigEndian, nil
	default:
		return nil, fmt.Errorf("%w: byte order %q", errMalformedMessage, marker)
	}
}

// messageSize returns the length of the whole message whose first 16 bytes are header.
func messageSize(header []byte) (int, error) {
	if len(header) < 16 {
		return 0, fmt.Errorf("%w: %d bytes of header", errMalformedMessage, len(header))
	}
	order, err := byteOrderOf(header[0])
	if err != nil {
		return 0, err
	}
	fieldsEnd := 16 + int(order.Uint32(header[12:16]))
	return (fieldsEnd+7)&^7 + int(order.Uint32(header[4:8])), nil
}

// parseMessage decodes raw, which must hold exactly one message, with the header fields of codes 1 to 9.
func parseMessage(raw []byte) (busMessage, error) {
	size, err := messageSize(raw)
	if err != nil {
		return busMessage{}, err
	}
	if size != len(raw) {
		return busMessage{}, fmt.Errorf("%w: %d bytes for a message of %d", errMalformedMessage, len(raw), size)
	}
	order, err := byteOrderOf(raw[0])
	if err != nil {
		return busMessage{}, err
	}
	msg := busMessage{Raw: raw, Order: order, Type: raw[1], Flags: raw[2], Serial: order.Uint32(raw[8:12])}
	fieldsEnd := 16 + int(order.Uint32(raw[12:16]))
	for pos := 16; pos < fieldsEnd; {
		pos = (pos + 7) &^ 7
		if pos >= fieldsEnd {
			break
		}
		if pos, err = msg.readField(pos, fieldsEnd); err != nil {
			return busMessage{}, err
		}
	}
	msg.Body = raw[(fieldsEnd+7)&^7:]
	return msg, nil
}

// readField decodes the header field at pos and returns the offset behind it.
func (m *busMessage) readField(pos, end int) (int, error) {
	raw := m.Raw
	if pos+4 > end {
		return 0, fmt.Errorf("%w: truncated header field", errMalformedMessage)
	}
	code, sig := raw[pos], raw[pos+2]
	pos += 4 // code, signature length, signature character, NUL
	switch sig {
	case 'u':
		value := m.Order.Uint32(raw[pos : pos+4])
		if code == fieldReplySerial {
			m.ReplySerial, m.ReplySerialAt = value, pos
		}
		return pos + 4, nil
	case 'g':
		n := int(raw[pos])
		m.setText(code, string(raw[pos+1:pos+1+n]))
		return pos + 2 + n, nil
	case 'o', 's':
		n := int(m.Order.Uint32(raw[pos : pos+4]))
		m.setText(code, string(raw[pos+4:pos+4+n]))
		return pos + 5 + n, nil
	default:
		return 0, fmt.Errorf("%w: header field type %q", errMalformedMessage, sig)
	}
}

// setText stores the text value of the header field code.
func (m *busMessage) setText(code byte, value string) {
	switch code {
	case fieldPath:
		m.Path = value
	case fieldInterface:
		m.Interface = value
	case fieldMember:
		m.Member = value
	case fieldErrorName:
		m.ErrorName = value
	case fieldDestination:
		m.Destination = value
	case fieldSender:
		m.Sender = value
	case fieldSignature:
		m.Signature = value
	}
}

// callKey identifies a method call by what the bus answers, not by its serial or sender.
func (m busMessage) callKey() string {
	return fmt.Sprintf("%s\x00%s\x00%s\x00%s\x00%s", m.Path, m.Interface, m.Member, m.Signature, m.Body)
}

// capture holds the messages of the pcap in their order; the plan and the tests number them from 1.
type capture struct {
	t        *testing.T
	messages []busMessage
}

// loadCapture reads the classic pcap of the system bus capture (link type 231, one message per record).
func loadCapture(t *testing.T) capture {
	t.Helper()
	data, err := os.ReadFile(capturePath)
	if err != nil {
		t.Fatalf("reading %s: %v", capturePath, err)
	}
	if len(data) < 24 || !bytes.Equal(data[:4], []byte{0xd4, 0xc3, 0xb2, 0xa1}) {
		t.Fatalf("%s: not a little-endian classic pcap", capturePath)
	}
	if link := binary.LittleEndian.Uint32(data[20:24]); link != linkTypeDBus {
		t.Fatalf("%s: link type %d, want %d", capturePath, link, linkTypeDBus)
	}
	c := capture{t: t}
	for pos := 24; pos < len(data); {
		if pos+16 > len(data) {
			t.Fatalf("%s: truncated record header at %d", capturePath, pos)
		}
		size := int(binary.LittleEndian.Uint32(data[pos+8 : pos+12]))
		pos += 16
		if pos+size > len(data) {
			t.Fatalf("%s: truncated record at %d", capturePath, pos)
		}
		msg, err := parseMessage(data[pos : pos+size])
		if err != nil {
			t.Fatalf("%s: message %d: %v", capturePath, len(c.messages)+1, err)
		}
		c.messages = append(c.messages, msg)
		pos += size
	}
	if len(c.messages) != captureMessages {
		t.Fatalf("%s holds %d messages, want %d", capturePath, len(c.messages), captureMessages)
	}
	return c
}

// at returns message n of the capture, numbered from 1 as in the plan.
func (c capture) at(n int) busMessage {
	c.t.Helper()
	if n < 1 || n > len(c.messages) {
		c.t.Fatalf("capture message %d does not exist", n)
	}
	return c.messages[n-1]
}

// replies maps the key of every captured method call to the captured reply that follows it on the same connection.
func (c capture) replies() map[string]busMessage {
	out := map[string]busMessage{}
	for i, call := range c.messages {
		if call.Type != typeMethodCall {
			continue
		}
		for _, next := range c.messages[i+1:] {
			if next.Type == typeMethodReturn && next.ReplySerial == call.Serial && next.Destination == call.Sender {
				out[call.callKey()] = next
				break
			}
		}
	}
	return out
}

// bodyReader reads the basic types of a message body.
type bodyReader struct {
	t     *testing.T
	order binary.ByteOrder
	b     []byte
	pos   int
}

// reader returns a reader over the body of m.
func (c capture) reader(m busMessage) *bodyReader {
	return &bodyReader{t: c.t, order: m.Order, b: m.Body}
}

// align moves to the next multiple of n.
func (r *bodyReader) align(n int) {
	r.pos = (r.pos + n - 1) / n * n
}

// need fails the test when fewer than n bytes are left.
func (r *bodyReader) need(n int) {
	r.t.Helper()
	if r.pos+n > len(r.b) {
		r.t.Fatalf("body of %d bytes read past its end at %d (+%d)", len(r.b), r.pos, n)
	}
}

// u32 reads a uint32.
func (r *bodyReader) u32() uint32 {
	r.t.Helper()
	r.align(4)
	r.need(4)
	v := r.order.Uint32(r.b[r.pos:])
	r.pos += 4
	return v
}

// str reads a string or an object path.
func (r *bodyReader) str() string {
	r.t.Helper()
	n := int(r.u32())
	r.need(n + 1)
	s := string(r.b[r.pos : r.pos+n])
	r.pos += n + 1
	return s
}

// sig reads a signature.
func (r *bodyReader) sig() string {
	r.t.Helper()
	r.need(1)
	n := int(r.b[r.pos])
	r.need(n + 2)
	s := string(r.b[r.pos+1 : r.pos+1+n])
	r.pos += n + 2
	return s
}

// strings reads an array of strings.
func (r *bodyReader) strings() []string {
	r.t.Helper()
	end := int(r.u32())
	r.align(4)
	end += r.pos
	var out []string
	for r.pos < end {
		out = append(out, r.str())
	}
	return out
}

// variant reads the signature of a variant and returns a reader positioned at its value.
func (r *bodyReader) variant() string {
	r.t.Helper()
	return r.sig()
}

// variantString returns the string held by the variant body of m.
func (c capture) variantString(m busMessage) string {
	c.t.Helper()
	r := c.reader(m)
	if sig := r.variant(); sig != "s" {
		c.t.Fatalf("variant of type %q, want s", sig)
	}
	return r.str()
}

// objectPath returns the object path of the body of a LoadUnit reply.
func (c capture) objectPath(m busMessage) string {
	c.t.Helper()
	return c.reader(m).str()
}

// property returns the name of the property a captured Properties.Get call asks for.
func (c capture) property(call int) string {
	c.t.Helper()
	m := c.at(call)
	if m.Member != "Get" {
		c.t.Fatalf("capture message %d is %q, want a Get call", call, m.Member)
	}
	r := c.reader(m)
	r.str()
	return r.str()
}
