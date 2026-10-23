// Package zstdfield decodes the zstd frames of compressed journal items under fixed bounds: only frames of the form journald writes
// are accepted, and the output goes into one buffer that is allocated once and never grown.
package zstdfield

import (
	"errors"

	"github.com/klauspost/compress/zstd"
)

// MaxFrameBytes is the largest content size of a frame that is decoded.
const MaxFrameBytes = 4 << 20

// ErrFrameRefused reports a frame that is not of the form the decoder accepts.
var ErrFrameRefused = errors.New("zstdfield: frame refused")

var errNotImplemented = errors.New("zstdfield: not implemented")

// Decoder decodes frames with one library decoder and one fixed output buffer. It is not safe for concurrent use.
type Decoder struct {
	dec *zstd.Decoder
	buf []byte
}

// New returns a decoder.
func New() (*Decoder, error) {
	return &Decoder{}, nil
}

// Decompress appends the decoded frame src to dst[:0], at most limit bytes; an error makes the item undecoded.
func (d *Decoder) Decompress(dst, src []byte, limit int) ([]byte, error) {
	return nil, errNotImplemented
}
