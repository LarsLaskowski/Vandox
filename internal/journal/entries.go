package journal

import (
	"bytes"
	"errors"
	"slices"
)

const (
	// readChunk is the most bytes asked of the underlying reader in one call when a compressed object is read.
	readChunk = 64 << 10
	// minInputBuffer is the size a compressed object's input buffer starts with.
	minInputBuffer = 32 << 10
	// decodedExtra is what the decoder may produce beyond MaxPayload: the longest field name (64 bytes), the '=' and one byte that
	// shows a longer value.
	decodedExtra = 66
	// objZSTD is the ZSTD bit of the flags byte of an object header.
	objZSTD = 4
)

// errEntryStop ends the item loop of an entry that is not decoded further.
var errEntryStop = errors.New("journal: entry not decoded further")

// Decompressor decodes the zstd frame of a compressed item.
type Decompressor interface {
	// Decompress appends the decoded frame src to dst[:0], at most limit bytes; an error makes the item undecoded.
	Decompress(dst, src []byte, limit int) ([]byte, error)
}

// ReadOptions configures NextWith.
type ReadOptions struct {
	MaxPayload         int          // bytes of a wanted payload kept, after decompression
	Decompress         Decompressor // nil: compressed items stay undecoded
	MaxCompressed      int          // compressed items decoded per entry; one more leaves the entry undecoded
	MaxCompressedBytes int          // largest compressed object read; a larger one is undecoded without being read
}

// SeekAll positions a cursor on the file's main entry array at the first entry after the sequence number after.
func (f *File) SeekAll(after uint64) (Cursor, error) {
	c := Cursor{main: true, head: f.mainHead, n: f.mainN}
	if f.hdr.HeadSeqnum > 0 && after < f.hdr.HeadSeqnum {
		return c, nil // every entry of the file is after: nothing to look at, so a damaged entry cannot get in the way
	}
	return f.seekList(c, after)
}

// NextWith reads the next entry of the main entry array into e like Next, with the limits and the decompression of opt. It reports
// whether an entry was read.
//
// MaxPayload counts the bytes of the value after NAME=; a longer value is cut and its Field has Cut set. Compressed items are decoded
// when opt allows it and are then ordinary fields; an item that cannot be read (a damaged data object, an XZ or LZ4 item, a failed or
// refused decoding, an object over MaxCompressedBytes) counts in Entry.Undecoded, and so does the item beyond MaxCompressed, after
// which the rest of the entry is not read. Only the first item of each wanted name is kept, as the import keeps the first value of a
// repeated field; a later one is passed over (a plain one before its value is read) and not counted in Omitted. A damaged entry object or entry array is ErrMalformed and the cursor stays.
func (f *File) NextWith(c *Cursor, want []string, e *Entry, opt *ReadOptions) (bool, error) {
	return f.advance(c, func(off uint64) error { return f.readEntryWith(off, want, e, opt) })
}

// readEntryWith reads the entry object at off into e like readEntry, with the options of NextWith.
func (f *File) readEntryWith(off uint64, want []string, e *Entry, opt *ReadOptions) error {
	count, err := f.beginEntry(off, e)
	if err != nil || count == 0 {
		return err
	}
	table := f.items[:count*itemSize]
	if err := f.readAt(off+entryFixed, table); err != nil {
		return err
	}
	compressed := 0
	for i := uint64(0); i < count; i++ {
		err := f.readItemWith(byteOrder.Uint64(table[i*itemSize:]), want, e, opt, &compressed)
		if errors.Is(err, errEntryStop) {
			return nil
		}
		if err != nil {
			return err
		}
	}
	return nil
}

// holds tells whether e already keeps a field of the name: only the first item of a wanted name is kept, as the import keeps the first
// value of a repeated field.
func (e *Entry) holds(name string) bool {
	return slices.ContainsFunc(e.Fields, func(f Field) bool { return f.Name == name })
}

// readItemWith reads the data object an item points to. A damaged object is counted in e.Undecoded; only a failure of the reader is
// returned.
func (f *File) readItemWith(off uint64, want []string, e *Entry, opt *ReadOptions, compressed *int) error {
	obj, size, err := f.object(off, typeData, dataFixed, dataFixed+namePrefix)
	if errors.Is(err, ErrMalformed) || errors.Is(err, errTorn) {
		e.Undecoded++
		return nil
	}
	if err != nil {
		return err
	}
	if obj[1]&objCompress != 0 {
		return f.readCompressedItem(off, obj[1], size, want, e, opt, compressed)
	}
	head := obj[dataFixed:]
	eq := bytes.IndexByte(head, '=')
	if eq <= 0 {
		return nil
	}
	name, ok := wanted(want, head[:eq])
	if !ok || e.holds(name) {
		return nil
	}
	if len(e.Fields) >= maxKept {
		e.Omitted++
		return nil
	}
	length := size - dataFixed - uint64(eq) - 1
	keep := min(length, uint64(max(opt.MaxPayload, 0)))
	buf := e.sized(len(e.Fields), eq+1+toInt(keep))
	have := min(len(buf), len(head))
	copy(buf, head[:have])
	if have < len(buf) {
		if err := f.readAt(off+dataFixed+uint64(have), buf[have:]); err != nil {
			return err
		}
	}
	e.Fields = append(e.Fields, Field{Name: name, Value: buf[eq+1:], Cut: length > keep})
	return nil
}

// readCompressedItem decodes the compressed data object at off, of the given flags and size, and keeps it as an ordinary field when
// its name is wanted.
func (f *File) readCompressedItem(off uint64, flags byte, size uint64, want []string, e *Entry, opt *ReadOptions, compressed *int) error {
	*compressed++
	if *compressed > opt.MaxCompressed {
		e.Undecoded++
		return errEntryStop
	}
	stored := size - dataFixed
	if flags&objCompress != objZSTD || opt.Decompress == nil || stored > uint64(max(opt.MaxCompressedBytes, 0)) {
		e.Undecoded++
		return nil
	}
	in, err := f.readCompressed(off+dataFixed, stored, e)
	if err != nil {
		return err
	}
	limit := max(opt.MaxPayload, 0) + decodedExtra
	slot := e.sized(len(e.Fields), limit)
	out, derr := opt.Decompress.Decompress(slot, in, limit)
	if derr != nil {
		e.Undecoded++
		return nil
	}
	eq := bytes.IndexByte(out[:min(len(out), namePrefix+1)], '=')
	if eq <= 0 {
		e.Undecoded++
		return nil
	}
	name, ok := wanted(want, out[:eq])
	if !ok || e.holds(name) {
		return nil
	}
	if len(e.Fields) >= maxKept {
		e.Omitted++
		return nil
	}
	value := out[eq+1:]
	cut := len(value) > opt.MaxPayload
	if cut {
		value = value[:opt.MaxPayload]
	}
	if len(value) > 0 && &value[0] != &slot[eq+1] {
		value = append(slot[:0], value...)
	}
	e.Fields = append(e.Fields, Field{Name: name, Value: value, Cut: cut})
	return nil
}

// readCompressed reads the n bytes at off into the entry's input buffer, in chunks.
func (f *File) readCompressed(off, n uint64, e *Entry) ([]byte, error) {
	need := toInt(n)
	if len(e.zin) < need {
		e.zin = make([]byte, max(need, minInputBuffer))
	}
	in := e.zin[:need]
	for done := 0; done < need; {
		chunk := in[done:min(need, done+readChunk)]
		if err := f.readAt(off+uint64(done), chunk); err != nil {
			return nil, err
		}
		done += len(chunk)
	}
	return in, nil
}
