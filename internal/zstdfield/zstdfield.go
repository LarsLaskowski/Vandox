// Package zstdfield decodes the zstd frames of compressed journal items under fixed bounds: only frames of the form journald writes
// are accepted, and the output goes into one buffer that is allocated once and never grown.
package zstdfield

import (
	"encoding/binary"
	"errors"

	"github.com/klauspost/compress/zstd"
)

const (
	// MaxFrameBytes is the largest content size of a frame that is decoded.
	MaxFrameBytes = 4 << 20

	// bufferBytes is the size of the output buffer: the largest frame, one block of overshoot and the library's 16 bytes.
	bufferBytes = MaxFrameBytes + 128<<10 + 16

	// frame header layout (RFC 8878, section 3.1.1)
	magicBytes         = 4
	descriptorSingle   = 1 << 5
	descriptorReserve  = 1 << 3
	descriptorDictID   = 3
	descriptorSizeIdx  = 6
	descriptorChecksum = 1 << 2
	blockHeaderBytes   = 3
	maxBlockBytes      = 128 << 10
	checksumBytes      = 4
	blockRLE           = 1
	blockReserved      = 3
)

// ErrFrameRefused reports a frame that is not of the form the decoder accepts.
var ErrFrameRefused = errors.New("zstdfield: frame refused")

// magic is the magic number that starts every zstd frame.
var magic = [magicBytes]byte{0x28, 0xb5, 0x2f, 0xfd}

// Decoder decodes frames with one library decoder and one fixed output buffer. It is not safe for concurrent use.
type Decoder struct {
	dec *zstd.Decoder
	buf []byte
}

// New returns a decoder.
func New() (*Decoder, error) {
	dec, err := zstd.NewReader(nil,
		zstd.WithDecoderConcurrency(1),
		zstd.WithDecoderLowmem(true),
		zstd.WithDecoderMaxMemory(MaxFrameBytes),
		zstd.WithDecoderMaxWindow(MaxFrameBytes),
		zstd.WithDecodeAllCapLimit(true))
	if err != nil {
		return nil, err
	}
	return &Decoder{dec: dec}, nil
}

// Decompress appends the decoded frame src to dst[:0], at most limit bytes; an error makes the item undecoded. When dst is too small
// for the result, the result is the decoder's own output buffer, valid until the next call, and nothing is allocated for it.
//
// A frame is decoded only when src holds exactly one frame, which is single-segment, states its content size (at most MaxFrameBytes) and
// uses no dictionary; src is checked up to its last block before any output is made, so no second frame follows the first.
func (d *Decoder) Decompress(dst, src []byte, limit int) ([]byte, error) {
	if err := checkFrame(src); err != nil {
		return nil, err
	}
	if d.buf == nil {
		d.buf = make([]byte, 0, bufferBytes)
	}
	out, err := d.dec.DecodeAll(src, d.buf[:0])
	if err != nil {
		return nil, err
	}
	if len(out) > 0 && &out[0] != &d.buf[:1][0] {
		return nil, ErrFrameRefused
	}
	out = out[:min(len(out), max(limit, 0))]
	if cap(dst) < len(out) {
		return out, nil
	}
	return append(dst[:0], out...), nil
}

// checkFrame reads the header and the block headers of the frame in src and refuses what the decoder does not accept.
func checkFrame(src []byte) error {
	if len(src) < magicBytes+2 || [magicBytes]byte(src[:magicBytes]) != magic {
		return ErrFrameRefused
	}
	desc := src[magicBytes]
	if desc&descriptorSingle == 0 || desc&descriptorReserve != 0 || desc&descriptorDictID != 0 {
		return ErrFrameRefused
	}
	rest := src[magicBytes+1:]
	var size uint64
	switch desc >> descriptorSizeIdx {
	case 0:
		size = uint64(rest[0])
	case 1:
		if len(rest) < 2 {
			return ErrFrameRefused
		}
		size = uint64(binary.LittleEndian.Uint16(rest)) + 256
	case 2:
		if len(rest) < 4 {
			return ErrFrameRefused
		}
		size = uint64(binary.LittleEndian.Uint32(rest))
	default:
		if len(rest) < 8 {
			return ErrFrameRefused
		}
		size = binary.LittleEndian.Uint64(rest)
	}
	if size > MaxFrameBytes {
		return ErrFrameRefused
	}
	return checkBlocks(src, magicBytes+1+sizeFieldBytes(desc>>descriptorSizeIdx), desc&descriptorChecksum != 0)
}

// sizeFieldBytes returns the bytes of the content size field of a single-segment frame for the flag bits of the descriptor.
func sizeFieldBytes(flag byte) int {
	return [...]int{1, 2, 4, 8}[flag&3]
}

// checkBlocks walks the block headers of the frame that starts after its header at pos and requires that the last block, and the
// checksum when the frame has one, end exactly at the end of src: src holds one frame, so the decoder finds no second frame behind it.
func checkBlocks(src []byte, pos int, checksum bool) error {
	for {
		if pos+blockHeaderBytes > len(src) {
			return ErrFrameRefused
		}
		h := uint32(src[pos]) | uint32(src[pos+1])<<8 | uint32(src[pos+2])<<16
		size := int(h >> 3)
		pos += blockHeaderBytes
		switch h >> 1 & 3 {
		case blockRLE:
			size = 1
		case blockReserved:
			return ErrFrameRefused
		}
		if size > maxBlockBytes || pos+size > len(src) {
			return ErrFrameRefused
		}
		pos += size
		if h&1 == 1 {
			break
		}
	}
	if checksum {
		pos += checksumBytes
	}
	if pos != len(src) {
		return ErrFrameRefused
	}
	return nil
}
