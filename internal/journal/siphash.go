package journal

import (
	"encoding/binary"
	"math/bits"
)

// sipHash24 is SipHash-2-4 of data keyed with key, the hash systemd 249 stores in the data objects of a file with the KEYED-HASH flag
// (the key is the file ID).
func sipHash24(key [16]byte, data []byte) uint64 {
	k0, k1 := binary.LittleEndian.Uint64(key[:8]), binary.LittleEndian.Uint64(key[8:])
	s := sipState{
		v0: k0 ^ 0x736f6d6570736575,
		v1: k1 ^ 0x646f72616e646f6d,
		v2: k0 ^ 0x6c7967656e657261,
		v3: k1 ^ 0x7465646279746573,
	}
	total := len(data)
	for len(data) >= 8 {
		s.block(binary.LittleEndian.Uint64(data))
		data = data[8:]
	}
	last := uint64(total&0xff) << 56
	for i, c := range data {
		last |= uint64(c) << (8 * i)
	}
	s.block(last)
	s.v2 ^= 0xff
	s.round()
	s.round()
	s.round()
	s.round()
	return s.v0 ^ s.v1 ^ s.v2 ^ s.v3
}

// sipState is the internal state of SipHash.
type sipState struct {
	v0, v1, v2, v3 uint64
}

// block mixes one 64-bit word into the state with two rounds.
func (s *sipState) block(m uint64) {
	s.v3 ^= m
	s.round()
	s.round()
	s.v0 ^= m
}

func (s *sipState) round() {
	s.v0 += s.v1
	s.v1 = bits.RotateLeft64(s.v1, 13) ^ s.v0
	s.v0 = bits.RotateLeft64(s.v0, 32)
	s.v2 += s.v3
	s.v3 = bits.RotateLeft64(s.v3, 16) ^ s.v2
	s.v0 += s.v3
	s.v3 = bits.RotateLeft64(s.v3, 21) ^ s.v0
	s.v2 += s.v1
	s.v1 = bits.RotateLeft64(s.v1, 17) ^ s.v2
	s.v2 = bits.RotateLeft64(s.v2, 32)
}
