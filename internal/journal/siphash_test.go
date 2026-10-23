package journal

import (
	"encoding/binary"
	"math/bits"
	"testing"
)

// sipVectors are the SipHash-2-4 values of the reference implementation (vectors.h, key 00 01 .. 0f) for the
// inputs 00, 00 01, .. 00 01 .. 0f, and for the 17-byte input; the same algorithm reproduces the stored hash of
// 2,000 data objects of the real server journal, keyed with its file ID.
var sipVectors = []struct {
	n    int
	hash uint64
}{
	{1, 0x74f839c593dc67fd},
	{2, 0x0d6c8009d9a94f5a},
	{3, 0x85676696d7fb7e2d},
	{4, 0xcf2794e0277187b7},
	{5, 0x18765564cd99a68d},
	{6, 0xcbc9466e58fee3ce},
	{7, 0xab0200f58b01d137},
	{8, 0x93f5f5799a932462},
	{9, 0x9e0082df0ba9e4b0},
	{10, 0x7a5dbbc594ddb9f3},
	{11, 0xf4b32f46226bada7},
	{12, 0x751e8fbc860ee5fb},
	{13, 0x14ea5627c0843d90},
	{14, 0xf723ca908e7af2ee},
	{15, 0xa129ca6149be45e5},
	{16, 0x3f2acc7f57c29bdb},
	{17, 0x699ae9f52cbe4794},
}

// refSipHash24 is the test's own SipHash-2-4, used to place the data objects of built files.
func refSipHash24(key [16]byte, data []byte) uint64 {
	k0, k1 := binary.LittleEndian.Uint64(key[:8]), binary.LittleEndian.Uint64(key[8:])
	v0, v1 := k0^0x736f6d6570736575, k1^0x646f72616e646f6d
	v2, v3 := k0^0x6c7967656e657261, k1^0x7465646279746573
	round := func() {
		v0 += v1
		v1 = bits.RotateLeft64(v1, 13) ^ v0
		v0 = bits.RotateLeft64(v0, 32)
		v2 += v3
		v3 = bits.RotateLeft64(v3, 16) ^ v2
		v0 += v3
		v3 = bits.RotateLeft64(v3, 21) ^ v0
		v2 += v1
		v1 = bits.RotateLeft64(v1, 17) ^ v2
		v2 = bits.RotateLeft64(v2, 32)
	}
	n := len(data)
	for len(data) >= 8 {
		m := binary.LittleEndian.Uint64(data)
		v3 ^= m
		round()
		round()
		v0 ^= m
		data = data[8:]
	}
	last := uint64(n&0xff) << 56
	for i, c := range data {
		last |= uint64(c) << (8 * i)
	}
	v3 ^= last
	round()
	round()
	v0 ^= last
	v2 ^= 0xff
	round()
	round()
	round()
	round()
	return v0 ^ v1 ^ v2 ^ v3
}

func sipInput(n int) []byte {
	in := make([]byte, n)
	for i := range in {
		in[i] = byte(i)
	}
	return in
}

func sipKey() [16]byte {
	var k [16]byte
	for i := range k {
		k[i] = byte(i)
	}
	return k
}

// vectorFile builds a file whose data objects carry the reference vectors as their stored hash, so that only a
// reader that computes the same keyed hash finds them.
func vectorFile(fileID [16]byte) []byte {
	byLen := map[int]uint64{}
	for _, v := range sipVectors {
		byLen[v.n] = v.hash
	}
	items := make([]bItem, 0, len(sipVectors))
	for _, v := range sipVectors {
		items = append(items, bItem{payload: sipInput(v.n)})
	}
	e := bEntry{seqnum: 1, realtime: testBase, mono: testMono, boot: hexID(testBoot), items: items}
	b, _ := buildFile([]bEntry{e}, bOptions{
		fileID: fileID, buckets: 8,
		hash: func(p []byte) uint64 { return byLen[len(p)] },
	})
	return b
}

func TestLookup_FindsPayloadsByKeyedSipHash24(t *testing.T) {
	file := vectorFile(sipKey())
	f, err := Open(newRefReader(file), int64(len(file)), false)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	for _, v := range sipVectors {
		_, found, err := f.Lookup(sipInput(v.n))
		if err != nil || !found {
			t.Errorf("Lookup(%d-byte payload) = found %v, err %v, want found true (hash %#016x of the key 00..0f)", v.n, found, err, v.hash)
		}
		other := sipInput(v.n)
		other[v.n-1] ^= 0x40
		if _, found, err := f.Lookup(other); err != nil || found {
			t.Errorf("Lookup(changed %d-byte payload) = found %v, err %v, want found false and no error", v.n, found, err)
		}
	}
}

func TestLookup_UsesFileIDAsKey(t *testing.T) {
	key := sipKey()
	key[0] ^= 0x01
	file := vectorFile(key)
	f, err := Open(newRefReader(file), int64(len(file)), false)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	for _, v := range sipVectors {
		if _, found, err := f.Lookup(sipInput(v.n)); err != nil || found {
			t.Errorf("Lookup(%d-byte payload) with another file ID = found %v, err %v, want found false: the hash is keyed with the file ID", v.n, found, err)
		}
	}
}
