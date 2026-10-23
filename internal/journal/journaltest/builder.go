package journaltest

import (
	"encoding/binary"
	"errors"
	"fmt"
	"math/bits"
)

var le = binary.LittleEndian

// u converts a size or index that is never negative.
func u(n int) uint64 {
	if n < 0 {
		return 0
	}
	return uint64(n)
}

// chain is a chain of entry arrays that double in size.
type chain struct {
	arrays []uint64
	caps   []int
	n      int
}

// record is a data object written to the file.
type record struct {
	off, hash, n uint64
	list         chain
}

type builder struct {
	buf      []byte
	opts     Options
	buckets  uint64
	table    uint64
	fields   uint64
	records  map[string]*record
	main     chain
	mainHead uint64
	objects  uint64
	arrays   uint64
	tail     uint64
	err      error
}

func newBuilder(entries []parsedEntry, opts Options) *builder {
	unique := map[string]bool{}
	for _, e := range entries {
		for _, it := range e.items {
			unique[string(it)] = true
		}
	}
	buckets := max(minBuckets, len(unique)*3/2)
	b := &builder{buf: make([]byte, headerSize), opts: opts, buckets: u(buckets), records: map[string]*record{}}
	b.table = b.alloc(objectDataHash, 0, 16+16*buckets)
	b.fields = b.alloc(objectFieldTab, 0, 16+16*4)
	return b
}

// alloc appends an object of the given type and size at an 8-aligned offset and returns its offset.
func (b *builder) alloc(typ, flags byte, size int) uint64 {
	for len(b.buf)%8 != 0 {
		b.buf = append(b.buf, 0)
	}
	off := u(len(b.buf))
	b.buf = append(b.buf, make([]byte, size)...)
	b.buf[off] = typ
	b.buf[off+1] = flags
	le.PutUint64(b.buf[off+8:], u(size))
	b.objects++
	b.tail = off
	return off
}

func (b *builder) put(off, v uint64) { le.PutUint64(b.buf[off:], v) }

func (b *builder) get(off uint64) uint64 { return le.Uint64(b.buf[off:]) }

// payloadFor returns the bytes stored for a payload and the object flags; with Options.Compress a payload of 512 bytes and more is
// stored as the bytes Compress returns, else as a zstd frame of one raw block.
func payloadFor(p []byte, compress func(payload []byte) ([]byte, error)) ([]byte, byte, error) {
	if len(p) < compressMin {
		return p, 0, nil
	}
	if compress != nil {
		out, err := compress(p)
		if err != nil {
			return nil, 0, fmt.Errorf("journaltest: compressing a field: %w", err)
		}
		return out, flagZSTD, nil
	}
	if len(p) > maxRawFrame {
		return nil, 0, errors.New("journaltest: field too long")
	}
	n := u(len(p))
	out := []byte{0x28, 0xb5, 0x2f, 0xfd}
	if n < 256 {
		out = append(out, 0x20, byte(n&0xff))
	} else {
		out = append(out, 0x60, byte((n-256)&0xff), byte((n-256)>>8&0xff))
	}
	block := n<<3 | 1 // a last block of type raw
	out = append(out, byte(block&0xff), byte(block>>8&0xff), byte(block>>16&0xff))
	return append(out, p...), flagZSTD, nil
}

func (b *builder) data(payload []byte) *record {
	if r, ok := b.records[string(payload)]; ok {
		return r
	}
	stored, flags, err := payloadFor(payload, b.opts.Compress)
	if err != nil {
		b.err = err
		stored = payload
	}
	hash := sipHash24(b.opts.FileID, payload)
	off := b.alloc(objectData, flags, 64+len(stored))
	b.put(off+16, hash)
	copy(b.buf[off+64:], stored)
	bucket := b.table + 16 + 16*(hash%b.buckets)
	if tail := b.get(bucket + 8); tail == 0 {
		b.put(bucket, off)
	} else {
		b.put(tail+24, off)
	}
	b.put(bucket+8, off)
	r := &record{off: off, hash: hash}
	b.records[string(payload)] = r
	return r
}

// appendArray puts entryOff into the next free slot of the chain, adding an array (twice the size of the last) when it is full;
// first is told the offset of the chain's first array.
func (b *builder) appendArray(ch *chain, entryOff uint64, first func(off uint64)) {
	i, acc := 0, 0
	for i < len(ch.caps) && ch.n >= acc+ch.caps[i] {
		acc += ch.caps[i]
		i++
	}
	if i == len(ch.caps) {
		capacity := firstArrayCap << min(i, 30)
		off := b.alloc(objectArray, 0, 24+8*capacity)
		b.arrays++
		if i == 0 {
			first(off)
		} else {
			b.put(ch.arrays[i-1]+16, off)
		}
		ch.arrays = append(ch.arrays, off)
		ch.caps = append(ch.caps, capacity)
	}
	b.put(ch.arrays[i]+24+u(8*(ch.n-acc)), entryOff)
	ch.n++
}

func (b *builder) addEntry(e *parsedEntry) {
	recs := make([]*record, len(e.items))
	for i, it := range e.items {
		recs[i] = b.data(it)
	}
	off := b.alloc(objectEntry, 0, 64+16*len(e.items))
	b.put(off+16, e.seqnum)
	b.put(off+24, e.realtime)
	b.put(off+32, e.mono)
	copy(b.buf[off+40:], e.boot[:])
	var xor uint64
	linked := map[*record]bool{}
	for i, r := range recs {
		b.put(off+64+u(16*i), r.off)
		b.put(off+72+u(16*i), r.hash)
		xor ^= r.hash
		if linked[r] {
			continue
		}
		linked[r] = true
		if r.n == 0 {
			b.put(r.off+40, off)
		} else {
			b.appendArray(&r.list, off, func(a uint64) { b.put(r.off+48, a) })
		}
		r.n++
		b.put(r.off+56, r.n)
	}
	b.put(off+56, xor)
	b.appendArray(&b.main, off, func(a uint64) { b.mainHead = a })
}

// finish pads the arena and writes the header.
func (b *builder) finish(entries []parsedEntry) error {
	if b.err != nil {
		return b.err
	}
	for len(b.buf)%8 != 0 {
		b.buf = append(b.buf, 0)
	}
	h := b.buf
	copy(h, "LPKSHHRH")
	le.PutUint32(h[12:], incompatKeyed|incompatZSTD)
	h[16] = stateArchived
	if b.opts.Online {
		h[16] = stateOnline
	}
	copy(h[24:], b.opts.FileID[:])
	copy(h[40:], b.opts.MachineID[:])
	copy(h[72:], b.opts.SeqnumID[:])
	b.put(88, headerSize)
	b.put(96, u(len(h)-headerSize))
	b.put(104, b.table+16)
	b.put(112, 16*b.buckets)
	b.put(120, b.fields+16)
	b.put(128, 16*4)
	b.put(136, b.tail)
	b.put(144, b.objects)
	b.put(152, u(len(entries)))
	b.put(176, b.mainHead)
	b.put(208, u(len(b.records)))
	b.put(232, b.arrays)
	if len(entries) > 0 {
		first, last := entries[0], entries[len(entries)-1]
		b.put(168, first.seqnum)
		b.put(160, last.seqnum)
		b.put(184, first.realtime)
		b.put(192, last.realtime)
		b.put(200, last.mono)
		copy(h[56:], last.boot[:])
	}
	return nil
}

// sipHash24 is SipHash-2-4 of data keyed with the file ID, the hash of a data object in a file with the KEYED-HASH flag. It is
// written out here and not shared with the reader, so that the writer is an independent check of the reader.
func sipHash24(key [16]byte, data []byte) uint64 {
	k0, k1 := le.Uint64(key[:8]), le.Uint64(key[8:])
	v := [4]uint64{k0 ^ 0x736f6d6570736575, k1 ^ 0x646f72616e646f6d, k0 ^ 0x6c7967656e657261, k1 ^ 0x7465646279746573}
	total := len(data)
	for len(data) >= 8 {
		sipBlock(&v, le.Uint64(data))
		data = data[8:]
	}
	last := u(total&0xff) << 56
	for i, c := range data {
		last |= uint64(c) << (8 * i)
	}
	sipBlock(&v, last)
	v[2] ^= 0xff
	for range 4 {
		sipRound(&v)
	}
	return v[0] ^ v[1] ^ v[2] ^ v[3]
}

func sipBlock(v *[4]uint64, m uint64) {
	v[3] ^= m
	sipRound(v)
	sipRound(v)
	v[0] ^= m
}

func sipRound(v *[4]uint64) {
	v[0] += v[1]
	v[1] = bits.RotateLeft64(v[1], 13) ^ v[0]
	v[0] = bits.RotateLeft64(v[0], 32)
	v[2] += v[3]
	v[3] = bits.RotateLeft64(v[3], 16) ^ v[2]
	v[0] += v[3]
	v[3] = bits.RotateLeft64(v[3], 21) ^ v[0]
	v[2] += v[1]
	v[1] = bits.RotateLeft64(v[1], 17) ^ v[2]
	v[2] = bits.RotateLeft64(v[2], 32)
}
