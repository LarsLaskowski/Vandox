package zstdfield_test

import (
	"bytes"
	"encoding/binary"
	"errors"
	"runtime"
	"strings"
	"testing"

	"github.com/klauspost/compress/zstd"

	"github.com/LarsLaskowski/Vandox/internal/zstdfield"
)

// magic is the magic number that starts every zstd frame.
var magic = []byte{0x28, 0xb5, 0x2f, 0xfd}

// maxRawBlock is the size of the raw blocks the test frames are made of (at most 128 KiB - 1).
const maxRawBlock = 100_000

// sizeField returns the descriptor flag bits and the bytes of a frame content size field of a single-segment frame.
func sizeField(size uint64) (flag byte, field []byte) {
	switch {
	case size < 256:
		return 0, []byte{byte(size)}
	case size < 256+65536:
		return 1, binary.LittleEndian.AppendUint16(nil, uint16(size-256))
	case size < 1<<32:
		return 2, binary.LittleEndian.AppendUint32(nil, uint32(size))
	default:
		return 3, binary.LittleEndian.AppendUint64(nil, size)
	}
}

// rawBlocks returns payload as raw blocks, the last one marked as the last.
func rawBlocks(payload []byte) []byte {
	var out []byte
	for {
		n := min(len(payload), maxRawBlock)
		last := uint32(0)
		if n == len(payload) {
			last = 1
		}
		header := uint32(n)<<3 | last                                      // block type raw is 0
		out = append(out, byte(header), byte(header>>8), byte(header>>16)) // #nosec G115 -- the low bytes of a 24-bit block header
		out = append(out, payload[:n]...)
		payload = payload[n:]
		if last == 1 {
			return out
		}
	}
}

// singleSegmentFrame returns a frame of the server's form (single segment, stated content size, no checksum, no dictionary) that
// states size as its content size and holds payload as raw blocks; size and len(payload) differ for a frame that lies.
func singleSegmentFrame(size uint64, payload []byte) []byte {
	flag, field := sizeField(size)
	out := append([]byte{}, magic...)
	out = append(out, flag<<6|1<<5)
	out = append(out, field...)
	return append(out, rawBlocks(payload)...)
}

// text returns n bytes of compressible text.
func text(n int) []byte {
	return []byte(strings.Repeat("MESSAGE=fixture message 00042 of a server journal ", n/48+1)[:n])
}

// encoded returns payload compressed by the library in the form journald writes (one frame, single segment, no checksum).
func encoded(t *testing.T, payload []byte) []byte {
	t.Helper()
	enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderCRC(false), zstd.WithSingleSegment(true))
	if err != nil {
		t.Fatalf("zstd.NewWriter() error = %v, want nil", err)
	}
	defer func() { _ = enc.Close() }()
	return enc.EncodeAll(payload, nil)
}

func newDecoder(t *testing.T) *zstdfield.Decoder {
	t.Helper()
	d, err := zstdfield.New()
	if err != nil {
		t.Fatalf("New() error = %v, want nil", err)
	}
	return d
}

func TestDecoder_DecodesTheServerForm(t *testing.T) {
	small := text(100)
	medium := text(600)
	large := text(19_710)
	tests := []struct {
		name    string
		frame   []byte
		limit   int
		wantOut []byte
	}{
		{"raw block, one byte size field", singleSegmentFrame(uint64(len(small)), small), 1 << 20, small},
		{"raw block, two byte size field", singleSegmentFrame(uint64(len(medium)), medium), 1 << 20, medium},
		{"library frame of the server's form", encoded(t, large), 1 << 20, large},
		{"several raw blocks", singleSegmentFrame(250_000, text(250_000)), 1 << 20, text(250_000)},
		{"the output is cut at the limit", singleSegmentFrame(uint64(len(medium)), medium), 10, medium[:10]},
		{"a limit of zero", singleSegmentFrame(uint64(len(medium)), medium), 0, nil},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			d := newDecoder(t)

			got, err := d.Decompress(nil, tt.frame, tt.limit)

			if err != nil {
				t.Fatalf("Decompress() error = %v, want nil", err)
			}
			if !bytes.Equal(got, tt.wantOut) {
				t.Errorf("Decompress() = %d bytes %.40q, want %d bytes %.40q", len(got), got, len(tt.wantOut), tt.wantOut)
			}
		})
	}
}

func TestDecoder_AppendsToTheStartOfDst(t *testing.T) {
	payload := text(700)
	dst := bytes.Repeat([]byte("x"), 4096)
	d := newDecoder(t)

	got, err := d.Decompress(dst, singleSegmentFrame(uint64(len(payload)), payload), 1<<20)

	if err != nil {
		t.Fatalf("Decompress() error = %v, want nil", err)
	}
	if !bytes.Equal(got, payload) {
		t.Fatalf("Decompress() = %d bytes, want the %d bytes of the payload", len(got), len(payload))
	}
	if &got[0] != &dst[0] {
		t.Errorf("Decompress() did not write to dst[:0]: the result lies in another array although cap(dst) = %d holds %d bytes", cap(dst), len(payload))
	}
}

func TestDecoder_RefusesFramesNotOfTheServerForm(t *testing.T) {
	payload := text(600)
	window := byte(0x58) // window descriptor: exponent 11, mantissa 0, a window of 4 MiB
	notSingle := append(append([]byte{}, magic...), 0x40, window, 0x58, 0x02)
	notSingle = append(notSingle, rawBlocks(payload)...)
	noSize := append(append([]byte{}, magic...), 0x00, window)
	noSize = append(noSize, rawBlocks(payload)...)
	dictionary := append(append([]byte{}, magic...), 0x21, 0x07, 0x58) // single segment, dictionary ID of one byte, size 88
	dictionary = append(dictionary, rawBlocks(payload[:88])...)
	tests := []struct {
		name  string
		frame []byte
	}{
		{"no stated content size", noSize},
		{"stated content size but not a single segment", notSingle},
		{"a dictionary", dictionary},
		{"a stated size over 4 MiB", singleSegmentFrame(zstdfield.MaxFrameBytes+1, payload)},
		{"a stated size far over 4 MiB", singleSegmentFrame(1<<40, payload)},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			d := newDecoder(t)

			got, err := d.Decompress(nil, tt.frame, 1<<20)

			if !errors.Is(err, zstdfield.ErrFrameRefused) {
				t.Errorf("Decompress() error = %v, want ErrFrameRefused", err)
			}
			if len(got) != 0 {
				t.Errorf("Decompress() returned %d bytes with the error, want none", len(got))
			}
		})
	}
}

func TestDecoder_RefusesDamagedInput(t *testing.T) {
	payload := text(600)
	good := singleSegmentFrame(uint64(len(payload)), payload)
	tests := []struct {
		name  string
		frame []byte
	}{
		{"empty input", nil},
		{"only the magic", magic},
		{"not a zstd frame", []byte("MESSAGE=this is not a frame at all, it is plain text of some length")},
		{"a frame cut inside its block", good[:len(good)/2]},
		{"a frame cut inside its header", good[:6]},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			d := newDecoder(t)

			got, err := d.Decompress(nil, tt.frame, 1<<20)

			if err == nil {
				t.Errorf("Decompress() = %d bytes, error nil, want an error", len(got))
			}
			again, err := d.Decompress(nil, good, 1<<20)
			if err != nil || !bytes.Equal(again, payload) {
				t.Errorf("Decompress() of a good frame after the damaged one = %d bytes, error %v, want the payload and no error", len(again), err)
			}
		})
	}
}

// allocated returns the bytes allocated so far, after a collection.
func allocated() uint64 {
	runtime.GC()
	var m runtime.MemStats
	runtime.ReadMemStats(&m)
	return m.TotalAlloc
}

// live returns the bytes of live heap after a collection.
func live() uint64 {
	runtime.GC()
	var m runtime.MemStats
	runtime.ReadMemStats(&m)
	return m.HeapAlloc
}

// bufferBound is the size of the output buffer of a decoder: the largest frame, one block of overshoot and the library's 16 bytes.
const bufferBound = zstdfield.MaxFrameBytes + 128<<10 + 16

func TestDecoder_HeapStaysBoundedForTheLargestFrame(t *testing.T) {
	t.Run("the largest accepted frame allocates its output buffer once", heapOfTheLargestFrame)
	t.Run("a frame without a stated size allocates nothing", heapOfAFrameWithoutAStatedSize)
	t.Run("a frame whose stated size is too small", heapOfAFrameThatStatesTooLittle)
}

func heapOfTheLargestFrame(t *testing.T) {
	largest := text(zstdfield.MaxFrameBytes)
	frame := singleSegmentFrame(uint64(len(largest)), largest)
	before := allocated()
	d := newDecoder(t)
	got, err := d.Decompress(nil, frame, zstdfield.MaxFrameBytes)
	first := allocated() - before
	if err != nil || len(got) != len(largest) {
		t.Fatalf("Decompress() = %d bytes, error %v, want %d bytes and no error", len(got), err, len(largest))
	}
	if first > bufferBound+1<<20 {
		t.Errorf("the first decoding of a 4 MiB frame allocated %d bytes, want at most %d (one output buffer of %d bytes)", first, bufferBound+1<<20, bufferBound)
	}

	before = allocated()
	const rounds = 5
	for range rounds {
		if _, err := d.Decompress(nil, frame, zstdfield.MaxFrameBytes); err != nil {
			t.Fatalf("Decompress() error = %v, want nil", err)
		}
	}
	if again := allocated() - before; again > rounds*256<<10 {
		t.Errorf("%d further decodings allocated %d bytes, want the buffer reused (at most %d bytes each)", rounds, again, 256<<10)
	}
}

func heapOfAFrameWithoutAStatedSize(t *testing.T) {
	big := text(6 << 20)
	noSize := append(append([]byte{}, magic...), 0x00, 0x58)
	noSize = append(noSize, rawBlocks(big)...)
	d := newDecoder(t)
	before := allocated()

	_, err := d.Decompress(nil, noSize, zstdfield.MaxFrameBytes)

	if !errors.Is(err, zstdfield.ErrFrameRefused) {
		t.Errorf("Decompress() error = %v, want ErrFrameRefused", err)
	}
	if grown := allocated() - before; grown > 1<<20 {
		t.Errorf("refusing a frame without a stated size allocated %d bytes, want it refused before any output (at most 1 MiB)", grown)
	}
}

func heapOfAFrameThatStatesTooLittle(t *testing.T) {
	lying := singleSegmentFrame(100, text(6<<20))
	d := newDecoder(t)
	if _, err := d.Decompress(nil, singleSegmentFrame(1000, text(1000)), 1<<20); err != nil {
		t.Fatalf("Decompress() of a small frame error = %v, want nil", err)
	}
	before := allocated()

	got, err := d.Decompress(nil, lying, zstdfield.MaxFrameBytes)

	if err == nil {
		t.Errorf("Decompress() = %d bytes, error nil, want an error for a frame that holds more than it states", len(got))
	}
	if grown := allocated() - before; grown > bufferBound+1<<20 {
		t.Errorf("a frame that states 100 bytes and holds 6 MiB allocated %d bytes, want at most %d", grown, bufferBound+1<<20)
	}
}

func TestDecoder_NeverKeepsAGrownBuffer(t *testing.T) {
	big := text(zstdfield.MaxFrameBytes)
	frames := []struct {
		name   string
		frame  []byte
		wantOK bool
	}{
		{"the largest accepted frame", singleSegmentFrame(uint64(len(big)), big), true},
		{"a frame that holds more than it states", singleSegmentFrame(100, text(6<<20)), false},
		{"a stated size over the bound", singleSegmentFrame(uint64(zstdfield.MaxFrameBytes+1), big), false},
		{"a small frame", singleSegmentFrame(600, text(600)), true},
		{"a library frame", encoded(t, text(19_710)), true},
	}
	base := live()
	d := newDecoder(t)

	for round := range 3 {
		for _, f := range frames {
			_, err := d.Decompress(nil, f.frame, zstdfield.MaxFrameBytes)
			if (err == nil) != f.wantOK {
				t.Fatalf("round %d: Decompress() of %s error = %v, want an error: %v", round, f.name, err, !f.wantOK)
			}
		}
		if kept := live() - base; kept > bufferBound+2<<20 {
			t.Fatalf("round %d: the decoder keeps %d bytes live after frames of every size and error, want at most %d (one fixed buffer)", round, kept, bufferBound+2<<20)
		}
	}
	runtime.KeepAlive(d)
}
