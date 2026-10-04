package wire

import (
	"errors"
	"io"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// Decoder reads a batch record by record.
type Decoder struct{}

// NewDecoder reads and validates the header of the batch in r.
func NewDecoder(r io.Reader, lim Limits) (*Decoder, error) {
	return nil, errors.New("not implemented")
}

// Header returns the validated header.
func (d *Decoder) Header() Header { return Header{} }

// Next returns the next record, io.EOF after the last one.
func (d *Decoder) Next() (model.Record, error) {
	return model.Record{}, errors.New("not implemented")
}

// Close closes the gzip reader; it does not close the underlying reader.
func (d *Decoder) Close() error { return errors.New("not implemented") }
