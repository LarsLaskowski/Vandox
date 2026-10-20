package sdbus

import (
	"bytes"
	"encoding/binary"
	"errors"
	"math"
	"unicode/utf8"
)

// Sizes and values of the D-Bus message format that the client uses.
const (
	fixedHeaderSize = 16
	protocolVersion = 1
	maxErrorName    = 255
	maxNameElement  = 255

	msgTypeCall   byte = 1
	msgTypeReturn byte = 2
	msgTypeError  byte = 3

	callNoAutoStart byte = 0x2

	hfPath        byte = 1
	hfInterface   byte = 2
	hfMember      byte = 3
	hfErrorName   byte = 4
	hfReplySerial byte = 5
	hfDestination byte = 6
	hfSender      byte = 7
	hfSignature   byte = 8
	hfUnixFDs     byte = 9
)

// fieldTypes holds the one basic type each header field code 1 to 9 may have; every other code has none.
var fieldTypes = [...]byte{
	hfPath:        'o',
	hfInterface:   's',
	hfMember:      's',
	hfErrorName:   's',
	hfReplySerial: 'u',
	hfDestination: 's',
	hfSender:      's',
	hfSignature:   'g',
	hfUnixFDs:     'u',
}

// protocolError is a message of the bus that the client refuses; its text is one of the fixed reasons below.
type protocolError struct {
	reason string
}

// Error returns the fixed text of the refusal.
func (e *protocolError) Error() string {
	return "sdbus: message: " + e.reason
}

var (
	errUnknownByteOrder = &protocolError{"unknown byte order"}
	errUnknownVersion   = &protocolError{"unsupported protocol version"}
	errMessageTooLarge  = &protocolError{"message too large"}
	errFieldsTooLarge   = &protocolError{"header fields too large"}
	errBadFields        = &protocolError{"malformed header fields"}
	errFieldType        = &protocolError{"unexpected header field"}
	errNoReplySerial    = &protocolError{"missing reply serial"}
	errNoErrorName      = &protocolError{"missing error name"}
)

var (
	errTooLarge        = errors.New("reply too large")
	errMalformed       = errors.New("malformed body")
	errSignature       = errors.New("unexpected signature")
	errInvalidArgument = errors.New("invalid argument")
)

// request is a method call to send.
type request struct {
	member      string
	destination string
	path        string
	iface       string
	signature   string
	flags       byte
	body        []byte
}

// reply is an awaited method return or error with its undecoded body.
type reply struct {
	order     binary.ByteOrder
	isError   bool
	errorName []byte
	signature string
	body      []byte
}

// header is the validated fixed header of a received message; all sizes are within MaxMessage.
type header struct {
	order     binary.ByteOrder
	typ       byte
	fieldsLen int
	padding   int
	bodyLen   int
}

// replyFields are the header fields the client reads from a method return or error.
type replyFields struct {
	replySerial  uint32
	hasSerial    bool
	errorName    []byte
	hasErrorName bool
	signature    string
}

// encoder writes little-endian values aligned as the D-Bus marshalling requires.
type encoder struct {
	b   []byte
	err error
}

// align pads with zero bytes to a multiple of n.
func (e *encoder) align(n int) {
	for len(e.b)%n != 0 {
		e.b = append(e.b, 0)
	}
}

// length converts a size to the wire; a size that cannot be a message size marks the encoder as failed.
func (e *encoder) length(n int) uint32 {
	if n < 0 || n > MaxMessage {
		e.err = errInvalidArgument
		return 0
	}

	return uint32(n)
}

// u32 writes a uint32.
func (e *encoder) u32(v uint32) {
	e.align(4)
	e.b = binary.LittleEndian.AppendUint32(e.b, v)
}

// str writes a string or an object path; text that D-Bus cannot carry marks the encoder as failed.
func (e *encoder) str(s string) {
	if validText([]byte(s)) {
		e.u32(e.length(len(s)))
		e.b = append(e.b, s...)
		e.b = append(e.b, 0)
	} else {
		e.err = errInvalidArgument
	}
}

// signature writes a signature.
func (e *encoder) signature(s string) {
	if len(s) > math.MaxUint8 {
		e.err = errInvalidArgument

		return
	}

	e.b = append(e.b, byte(len(s)&math.MaxUint8))
	e.b = append(e.b, s...)
	e.b = append(e.b, 0)
}

// stringArray writes an array of strings.
func (e *encoder) stringArray(items []string) {
	e.u32(0)
	lengthAt := len(e.b) - 4
	start := len(e.b)

	for _, item := range items {
		e.str(item)
	}

	binary.LittleEndian.PutUint32(e.b[lengthAt:], e.length(len(e.b)-start))
}

// field writes one header field of a basic type.
func (e *encoder) field(code, typ byte, value string) {
	e.align(8)
	e.b = append(e.b, code, 1, typ, 0)

	if typ == 'g' {
		e.signature(value)
	} else {
		e.str(value)
	}
}

// marshal returns the little-endian method call message with the given serial.
func (q request) marshal(serial uint32) ([]byte, error) {
	e := &encoder{b: make([]byte, 0, 256+len(q.body))}

	e.b = append(e.b, 'l', msgTypeCall, q.flags, protocolVersion)
	e.u32(e.length(len(q.body)))
	e.u32(serial)
	e.u32(0)
	e.field(hfPath, 'o', q.path)
	e.field(hfInterface, 's', q.iface)
	e.field(hfMember, 's', q.member)
	e.field(hfDestination, 's', q.destination)

	if q.signature != "" {
		e.field(hfSignature, 'g', q.signature)
	}

	binary.LittleEndian.PutUint32(e.b[12:], e.length(len(e.b)-fixedHeaderSize))
	e.align(8)
	e.b = append(e.b, q.body...)

	if e.err != nil {
		return nil, e.err
	}

	return e.b, nil
}

// parseHeader validates the fixed header: the byte order, the protocol version and the size of the whole message,
// all before a byte of the rest is read.
func parseHeader(raw *[fixedHeaderSize]byte) (header, error) {
	var order binary.ByteOrder

	switch raw[0] {
	case 'l':
		order = binary.LittleEndian
	case 'B':
		order = binary.BigEndian
	default:
		return header{}, errUnknownByteOrder
	}

	if raw[3] != protocolVersion {
		return header{}, errUnknownVersion
	}

	bodyLen := order.Uint32(raw[4:8])
	fieldsLen := order.Uint32(raw[12:16])

	if uint64(fieldsLen)+uint64(bodyLen) > MaxMessage {
		return header{}, errMessageTooLarge
	}

	padding := (8 - (fixedHeaderSize+fieldsLen)%8) % 8

	if uint64(fixedHeaderSize+fieldsLen+padding)+uint64(bodyLen) > MaxMessage {
		return header{}, errMessageTooLarge
	}

	return header{order: order, typ: raw[1], fieldsLen: int(fieldsLen), padding: int(padding), bodyLen: int(bodyLen)}, nil
}

// parseFields decodes the header fields: only the codes 1 to 9, each with its one basic type; anything else is refused.
func parseFields(order binary.ByteOrder, b []byte) (replyFields, error) {
	d := &decoder{b: b, order: order}

	var f replyFields

	for d.pos < len(b) {
		if err := f.read(d); err != nil {
			return replyFields{}, err
		}
	}

	return f, nil
}

// read decodes the next header field.
func (f *replyFields) read(d *decoder) error {
	d.align(8)

	if d.bad || len(d.b)-d.pos < 4 {
		return errBadFields
	}

	code, sigLen, typ, nul := d.b[d.pos], d.b[d.pos+1], d.b[d.pos+2], d.b[d.pos+3]
	d.pos += 4

	if sigLen != 1 || nul != 0 {
		return errBadFields
	}

	if int(code) >= len(fieldTypes) || typ == 0 || fieldTypes[code] != typ {
		return errFieldType
	}

	switch typ {
	case 'u':
		f.setNumber(code, d.u32())
	case 'g':
		f.signature = string(d.signature())
	default:
		f.setText(code, d.text())
	}

	if d.bad {
		return errBadFields
	}

	return nil
}

// setNumber keeps the reply serial.
func (f *replyFields) setNumber(code byte, v uint32) {
	if code == hfReplySerial {
		f.replySerial, f.hasSerial = v, true
	}
}

// setText keeps the error name.
func (f *replyFields) setText(code byte, v []byte) {
	if code == hfErrorName {
		f.errorName, f.hasErrorName = v, true
	}
}

// decoder reads values of a message body or of the header fields in the byte order of the message; the first failure
// sticks in bad and every later read returns the zero value.
type decoder struct {
	b     []byte
	pos   int
	order binary.ByteOrder
	bad   bool
}

// align moves to the next multiple of n, a power of two.
func (d *decoder) align(n int) {
	d.pos = (d.pos + n - 1) &^ (n - 1)

	if d.pos > len(d.b) {
		d.bad = true
	}
}

// u32 reads a uint32.
func (d *decoder) u32() uint32 {
	d.align(4)

	if d.bad || len(d.b)-d.pos < 4 {
		d.bad = true

		return 0
	}

	v := d.order.Uint32(d.b[d.pos:])
	d.pos += 4

	return v
}

// u64 reads a uint64.
func (d *decoder) u64() uint64 {
	d.align(8)

	if d.bad || len(d.b)-d.pos < 8 {
		d.bad = true

		return 0
	}

	v := d.order.Uint64(d.b[d.pos:])
	d.pos += 8

	return v
}

// text reads a string or an object path: its length, the bytes and the NUL that must follow them.
func (d *decoder) text() []byte {
	n := d.u32()

	if d.bad || int(n) < 0 || int(n) >= len(d.b)-d.pos || d.b[d.pos+int(n)] != 0 {
		d.bad = true

		return nil
	}

	s := d.b[d.pos : d.pos+int(n)]
	d.pos += int(n) + 1

	return s
}

// signature reads a signature: a length byte, the bytes and the NUL that must follow them.
func (d *decoder) signature() []byte {
	if d.bad || d.pos >= len(d.b) {
		d.bad = true

		return nil
	}

	n := int(d.b[d.pos])

	if len(d.b)-d.pos < n+2 || d.b[d.pos+1+n] != 0 {
		d.bad = true

		return nil
	}

	s := d.b[d.pos+1 : d.pos+1+n]
	d.pos += n + 2

	return s
}

// str reads a string that is valid UTF-8 without NUL.
func (d *decoder) str() string {
	s := d.text()

	if d.bad {
		return ""
	}

	if validText(s) {
		return string(s)
	}

	d.bad = true

	return ""
}

// skip reads a string that is valid UTF-8 without NUL and drops it.
func (d *decoder) skip() {
	s := d.text()

	if !d.bad && !validText(s) {
		d.bad = true
	}
}

// done reports whether the whole input was read without failure.
func (d *decoder) done() bool {
	return !d.bad && d.pos == len(d.b)
}

// validText reports whether s may be a D-Bus string: valid UTF-8 without a NUL.
func validText(s []byte) bool {
	return utf8.Valid(s) && bytes.IndexByte(s, 0) < 0
}

// decodeUnits decodes the body of type a(ssssssouso).
func decodeUnits(order binary.ByteOrder, body []byte) ([]UnitInfo, error) {
	d := &decoder{b: body, order: order}
	n := d.u32()

	if d.bad {
		return nil, errMalformed
	}

	if n == 0 {
		if len(body) == 4 || len(body) == 8 {
			return nil, nil
		}

		return nil, errMalformed
	}

	d.align(8)

	if d.bad || int(n) < 0 || int(n) != len(body)-d.pos {
		return nil, errMalformed
	}

	var units []UnitInfo

	for !d.bad && d.pos < len(body) {
		units = append(units, decodeUnit(d))
	}

	if !d.done() {
		return nil, errMalformed
	}

	return units, nil
}

// decodeUnit decodes one struct (ssssssouso) of a unit listing.
func decodeUnit(d *decoder) UnitInfo {
	d.align(8)

	var u UnitInfo

	u.ID = d.str()
	d.skip()
	u.LoadState = d.str()
	u.ActiveState = d.str()
	u.SubState = d.str()
	d.skip()
	u.Path = d.str()
	d.u32()
	d.skip()

	if !validObjectPath(d.str()) || !validObjectPath(u.Path) {
		d.bad = true
	}

	return u
}

// decodeUniqueName checks the body of the Hello reply, a single string.
func decodeUniqueName(order binary.ByteOrder, body []byte) error {
	d := &decoder{b: body, order: order}
	d.str()

	if d.done() {
		return nil
	}

	return errMalformed
}

// variantDecoder returns a decoder positioned at the value of a body that is a variant holding a basic type typ.
func variantDecoder(order binary.ByteOrder, body []byte, typ byte, alignment int) (*decoder, error) {
	d := &decoder{b: body, order: order}
	sig := d.signature()

	if d.bad {
		return nil, errMalformed
	}

	if len(sig) != 1 || sig[0] != typ {
		return nil, errSignature
	}

	d.align(alignment)

	return d, nil
}

// decodeVariantUint64 decodes the body of a variant holding a uint64.
func decodeVariantUint64(order binary.ByteOrder, body []byte) (uint64, error) {
	d, err := variantDecoder(order, body, 't', 8)
	if err != nil {
		return 0, err
	}

	v := d.u64()

	if !d.done() {
		return 0, errMalformed
	}

	return v, nil
}

// decodeVariantUint32 decodes the body of a variant holding a uint32.
func decodeVariantUint32(order binary.ByteOrder, body []byte) (uint32, error) {
	d, err := variantDecoder(order, body, 'u', 4)
	if err != nil {
		return 0, err
	}

	v := d.u32()

	if !d.done() {
		return 0, errMalformed
	}

	return v, nil
}

// validObjectPath reports whether p follows the D-Bus object path grammar: "/" or elements of letters, digits and
// underscores, each non-empty, behind slashes, with no trailing slash.
func validObjectPath(p string) bool {
	if p == "/" {
		return true
	}

	if len(p) < 2 || p[0] != '/' {
		return false
	}

	elementLen := 0

	for i := 1; i < len(p); i++ {
		switch c := p[i]; {
		case c == '/':
			if elementLen == 0 {
				return false
			}

			elementLen = 0
		case isNameByte(c):
			elementLen++
		default:
			return false
		}
	}

	return elementLen > 0
}

// isNameByte reports whether c is a letter, a digit or an underscore.
func isNameByte(c byte) bool {
	return c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_'
}

// validErrorName reports whether name follows the D-Bus error name grammar: at least two non-empty elements of
// letters, digits and underscores separated by dots, none starting with a digit, at most 255 bytes.
func validErrorName(name []byte) bool {
	if len(name) == 0 || len(name) > maxErrorName {
		return false
	}

	elements := bytes.Split(name, []byte{'.'})

	if len(elements) < 2 {
		return false
	}

	for _, element := range elements {
		if !validElement(element) {
			return false
		}
	}

	return true
}

// validElement reports whether e is a non-empty run of name bytes that does not start with a digit.
func validElement(e []byte) bool {
	if len(e) == 0 || len(e) > maxNameElement || e[0] >= '0' && e[0] <= '9' {
		return false
	}

	for _, c := range e {
		if !isNameByte(c) {
			return false
		}
	}

	return true
}
