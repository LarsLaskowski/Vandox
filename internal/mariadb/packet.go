package mariadb

import (
	"bufio"
	"errors"
	"io"
)

// packetReader reads the packets of the protocol.
type packetReader struct {
	r         *bufio.Reader
	seq       byte
	remaining int
}

// newPacketReader returns a packetReader over r.
func newPacketReader(r io.Reader) *packetReader {
	return &packetReader{r: bufio.NewReader(r)}
}

// next reads a whole control packet of at most limit bytes.
func (p *packetReader) next(step string, limit int) ([]byte, error) {
	_, _ = step, limit
	return nil, errors.New("not implemented")
}

// beginRow starts a row or reads an end packet and returns its first byte.
func (p *packetReader) beginRow(step string) (first byte, err error) {
	_ = step
	return 0, errors.New("not implemented")
}

// readCell reads a length-encoded cell of the current row.
func (p *packetReader) readCell(step string, col column) (cell, error) {
	_, _ = step, col
	return cell{}, errors.New("not implemented")
}

// writePacket writes payload as a packet with sequence number seq.
func writePacket(w io.Writer, seq byte, payload []byte) error {
	_, _, _ = w, seq, payload
	return errors.New("not implemented")
}
