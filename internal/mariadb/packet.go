package mariadb

import (
	"bufio"
	"context"
	"encoding/binary"
	"errors"
	"io"
	"net"
)

// maxFrame is the payload size of a packet that another packet continues.
const maxFrame = 0xffffff

// Reasons a reply is refused with.
const (
	reasonMalformed    = "malformed packet"
	reasonSequence     = "packet out of sequence"
	reasonTooLarge     = "packet too large"
	reasonUnexpected   = "unexpected reply"
	reasonColumns      = "unexpected columns"
	reasonCellTooLong  = "cell too long"
	reasonLocalFile    = "local file request refused"
	reasonMoreResults  = "more results refused"
	reasonSwitchDenied = "authentication switch refused"
)

// packetReader reads the packets of the protocol.
type packetReader struct {
	r         *bufio.Reader
	seq       byte   // the sequence number the next packet must have
	remaining uint64 // unread bytes of the current frame
	more      bool   // the current frame is full-size, so another frame follows
	header    [4]byte
	num       [8]byte
	bufs      [][]byte // one scratch buffer per cell position of a row
	idx       int      // the cell position the next cell of the row takes
}

// newPacketReader returns a packetReader over r.
func newPacketReader(r io.Reader) *packetReader {
	return &packetReader{r: bufio.NewReader(r)}
}

// stepError is an error of a step of a run that wraps a fixed reason.
type stepError struct {
	step string
	err  error
}

// Error returns the text of the error.
func (e *stepError) Error() string { return "mariadb: " + e.step + ": " + e.err.Error() }

// Unwrap returns the reason.
func (e *stepError) Unwrap() error { return e.err }

// ioError turns a failure of the connection into the error of the step; it never carries the text of err.
func ioError(step string, err error) error {
	var ne net.Error
	if errors.Is(err, context.DeadlineExceeded) || (errors.As(err, &ne) && ne.Timeout()) {
		return &stepError{step: step, err: errTimeout}
	}
	return &stepError{step: step, err: errClosed}
}

// malformed returns the error of a packet that breaks the layout.
func malformed(step string) error { return &protocolError{step: step, reason: reasonMalformed} }

// readHeader reads a packet header, checks its sequence number and returns the length of the payload.
func (p *packetReader) readHeader(step string) (uint64, error) {
	if _, err := io.ReadFull(p.r, p.header[:]); err != nil {
		return 0, ioError(step, err)
	}
	if p.header[3] != p.seq {
		return 0, &protocolError{step: step, reason: reasonSequence}
	}
	p.seq++
	return uint64(p.header[0]) | uint64(p.header[1])<<8 | uint64(p.header[2])<<16, nil
}

// next reads a whole control packet of at most limit bytes.
func (p *packetReader) next(step string, limit int) ([]byte, error) {
	p.remaining, p.more = 0, false
	n, err := p.readHeader(step)
	if err != nil {
		return nil, err
	}
	if limit < 0 || n > uint64(limit) {
		return nil, &protocolError{step: step, reason: reasonTooLarge}
	}
	payload := make([]byte, n)
	if _, err := io.ReadFull(p.r, payload); err != nil {
		return nil, ioError(step, err)
	}
	return payload, nil
}

// beginRow starts a row or reads an end packet and returns its first byte; the byte is not consumed. The unread rest
// of the previous row is discarded.
func (p *packetReader) beginRow(step string) (first byte, err error) {
	p.idx = 0
	if err = p.discardRest(step); err != nil {
		return 0, err
	}
	n, err := p.readHeader(step)
	if err != nil {
		return 0, err
	}
	p.remaining, p.more = n, n == maxFrame
	if n == 0 {
		return 0, malformed(step)
	}
	head, err := p.r.Peek(1)
	if err != nil {
		return 0, ioError(step, err)
	}
	return head[0], nil
}

// rest reads the unread rest of the current packet, which must not exceed limit.
func (p *packetReader) rest(step string, limit int) ([]byte, error) {
	if limit < 0 || p.remaining > uint64(limit) {
		return nil, &protocolError{step: step, reason: reasonTooLarge}
	}
	payload := make([]byte, p.remaining)
	if _, err := io.ReadFull(p.r, payload); err != nil {
		return nil, ioError(step, err)
	}
	p.remaining = 0
	return payload, nil
}

// ensure makes sure that the current frame has an unread byte, continuing into the next frame of a long payload.
func (p *packetReader) ensure(step string) error {
	for p.remaining == 0 {
		if !p.more {
			return malformed(step)
		}
		n, err := p.readHeader(step)
		if err != nil {
			return err
		}
		p.remaining, p.more = n, n == maxFrame
	}
	return nil
}

// readN fills buf from the current payload.
func (p *packetReader) readN(step string, buf []byte) error {
	for got := 0; got < len(buf); {
		if err := p.ensure(step); err != nil {
			return err
		}
		take := len(buf) - got
		if p.remaining < maxFrame && int(p.remaining) < take {
			take = int(p.remaining)
		}
		if _, err := io.ReadFull(p.r, buf[got:got+take]); err != nil {
			return ioError(step, err)
		}
		got += take
		p.remaining -= uint64(take)
	}
	return nil
}

// discardRest discards the unread rest of the current payload, with its continuation frames.
func (p *packetReader) discardRest(step string) error {
	for {
		if err := p.discard(step, p.remaining); err != nil {
			return err
		}
		if !p.more {
			return nil
		}
		if err := p.ensure(step); err != nil {
			return err
		}
	}
}

// skip discards n bytes of the current payload.
func (p *packetReader) skip(step string, n uint64) error {
	for n > 0 {
		if err := p.ensure(step); err != nil {
			return err
		}
		take := min(n, p.remaining)
		if err := p.discard(step, take); err != nil {
			return err
		}
		n -= take
	}
	return nil
}

// discard drops n bytes of the current frame, which has at least n unread bytes.
func (p *packetReader) discard(step string, n uint64) error {
	if n > maxFrame {
		return malformed(step)
	}
	if _, err := p.r.Discard(int(n)); err != nil {
		return ioError(step, err)
	}
	p.remaining -= n
	return nil
}

// lenenc reads a length-encoded integer; null is set for the NULL marker of a row.
func (p *packetReader) lenenc(step string) (n uint64, null bool, err error) {
	if err = p.ensure(step); err != nil {
		return 0, false, err
	}
	first, err := p.r.ReadByte()
	if err != nil {
		return 0, false, ioError(step, err)
	}
	p.remaining--
	size := 0
	switch {
	case first < 0xfb:
		return uint64(first), false, nil
	case first == 0xfb:
		return 0, true, nil
	case first == 0xfc:
		size = 2
	case first == 0xfd:
		size = 3
	case first == 0xfe:
		size = 8
	default:
		return 0, false, malformed(step)
	}
	p.num = [8]byte{}
	if err = p.readN(step, p.num[:size]); err != nil {
		return 0, false, err
	}
	return binary.LittleEndian.Uint64(p.num[:]), false, nil
}

// readCell reads a length-encoded cell of the current row.
func (p *packetReader) readCell(step string, col column) (cell, error) {
	n, null, err := p.lenenc(step)
	if err != nil {
		return cell{}, err
	}
	pos := p.idx
	p.idx++
	if null {
		return cell{null: true}, nil
	}
	keep, cut := n, false
	if col.limit < 0 || n > uint64(col.limit) {
		if !col.cut {
			return cell{}, &protocolError{step: step, reason: reasonCellTooLong}
		}
		keep, cut = uint64(max(col.limit, 0)), true
	}
	buf := p.scratch(pos, col.limit)[:keep]
	if err = p.readN(step, buf); err != nil {
		return cell{}, err
	}
	if err = p.skip(step, n-keep); err != nil {
		return cell{}, err
	}
	return cell{value: buf, cut: cut}, nil
}

// scratch returns the buffer of a cell position, allocated once per position with room for limit bytes.
func (p *packetReader) scratch(pos, limit int) []byte {
	for len(p.bufs) <= pos {
		p.bufs = append(p.bufs, nil)
	}
	if len(p.bufs[pos]) < limit {
		p.bufs[pos] = make([]byte, limit)
	}
	return p.bufs[pos]
}

// writePacket writes payload as a packet with sequence number seq.
func writePacket(w io.Writer, seq byte, payload []byte) error {
	if len(payload) >= maxFrame {
		return errors.New("mariadb: payload too large")
	}
	n := len(payload)
	out := make([]byte, 4, 4+n)
	out[0], out[1], out[2], out[3] = byte(n&0xff), byte(n>>8&0xff), byte(n>>16&0xff), seq
	_, err := w.Write(append(out, payload...))
	return err
}
