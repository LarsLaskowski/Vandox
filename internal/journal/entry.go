package journal

import (
	"bytes"
	"time"
)

// Entry is one decoded journal entry, reused between calls of Next.
//
// Realtime is the zero time when the entry's real-time stamp is zero or outside the range the wire format accepts (before year 10000),
// Monotonic is zero when the stamp does not fit a time.Duration; RealtimeUS and MonotonicUS always hold the stamps as stored, in µs,
// so that a caller can tell an unreadable stamp from a zero one.
type Entry struct {
	Seqnum      uint64
	Realtime    time.Time
	Monotonic   time.Duration
	BootID      [16]byte
	Items       int
	Fields      []Field // only the names in want, values read up to 4,096 bytes into reused buffers
	RealtimeUS  uint64  // the real-time stamp as stored, in µs
	MonotonicUS uint64  // the monotonic stamp as stored, in µs

	bufs [][]byte
}

// Field is one field of an entry. A Compressed field is an item stored compressed: it is not decompressed and its name is not known,
// so Name and Value are empty.
type Field struct {
	Name       string
	Value      []byte // without the NAME= prefix; valid until the next call of Next with this Entry
	Compressed bool
}

// Field returns the fields of e with the given name, in the order of the entry's items.
func (e *Entry) Field(name string) ([]Field, bool) {
	var out []Field
	for _, f := range e.Fields {
		if f.Name == name && !f.Compressed {
			out = append(out, f)
		}
	}
	return out, len(out) > 0
}

// reset empties e for the next entry and keeps its buffers.
func (e *Entry) reset() {
	e.Fields = e.Fields[:0]
	e.Realtime, e.Monotonic, e.Items = time.Time{}, 0, 0
}

// buffer returns the k-th payload buffer of e, made when first needed.
func (e *Entry) buffer(k int) []byte {
	for len(e.bufs) <= k {
		e.bufs = append(e.bufs, make([]byte, maxPayload))
	}
	return e.bufs[k]
}

// readEntry reads the entry object at off into e: its stamps and the fields named in want.
func (f *File) readEntry(off uint64, want []string, e *Entry) error {
	obj, size, err := f.object(off, typeEntry, entryFixed, entryFixed)
	if err != nil {
		return err
	}
	count := (size - entryFixed) / itemSize
	if (size-entryFixed)%itemSize != 0 || count > maxItems {
		return ErrMalformed
	}
	e.reset()
	e.Seqnum = byteOrder.Uint64(obj[16:])
	e.RealtimeUS = byteOrder.Uint64(obj[24:])
	e.MonotonicUS = byteOrder.Uint64(obj[32:])
	copy(e.BootID[:], obj[40:56])
	e.Items = toInt(count)
	e.Realtime = realtimeOf(e.RealtimeUS)
	e.Monotonic = monotonicOf(e.MonotonicUS)
	if count == 0 {
		return nil
	}
	table := f.items[:count*itemSize]
	if err := f.readAt(off+entryFixed, table); err != nil {
		return err
	}
	for i := uint64(0); i < count; i++ {
		if err := f.readItem(byteOrder.Uint64(table[i*itemSize:]), want, e); err != nil {
			return err
		}
	}
	return nil
}

// readItem reads the data object an item points to: its name from the first bytes of its payload, and the payload (up to 4,096
// bytes) when the name is wanted.
func (f *File) readItem(off uint64, want []string, e *Entry) error {
	obj, size, err := f.object(off, typeData, dataFixed, dataFixed+namePrefix)
	if err != nil {
		return err
	}
	if len(e.Fields) >= maxKept {
		return nil
	}
	if obj[1]&objCompress != 0 {
		e.Fields = append(e.Fields, Field{Compressed: true})
		return nil
	}
	head := obj[dataFixed:]
	eq := bytes.IndexByte(head, '=')
	if eq <= 0 {
		return nil
	}
	name, ok := wanted(want, head[:eq])
	if !ok {
		return nil
	}
	take := min(size-dataFixed, maxPayload)
	buf := e.buffer(len(e.Fields))[:take]
	have := min(take, uint64(len(head)))
	copy(buf, head[:have])
	if have < take {
		if err := f.readAt(off+dataFixed+have, buf[have:]); err != nil {
			return err
		}
	}
	e.Fields = append(e.Fields, Field{Name: name, Value: buf[eq+1:]})
	return nil
}

// wanted returns the entry of want equal to name.
func wanted(want []string, name []byte) (string, bool) {
	for _, w := range want {
		if string(name) == w {
			return w, true
		}
	}
	return "", false
}
