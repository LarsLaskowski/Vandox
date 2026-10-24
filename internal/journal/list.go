package journal

import "errors"

// Data is a data object found by Lookup: its offset, the offsets that start its entry list and the number of entries in it.
type Data struct {
	off, first, head, n uint64
}

// Cursor is a position in the entry list of a data object. The list is the data object's first entry followed by the items of its
// chain of entry arrays. Its zero value is not usable; Seek makes one.
type Cursor struct {
	data, first, head, n uint64
	idx                  uint64 // list index of the next entry
	arr, arrBase, arrCap uint64 // the array that holds chain index idx-1 (0 when unknown), the chain index of its first item, its capacity
	arrNo                int    // 1-based place of arr in the chain
	torn                 bool   // the list ended at a cut-off tail of an archived or set-aside file
	main                 bool   // the list is the file's main entry array (SeekAll), not the entry list of a data object
}

// Torn reports whether the list ended at a cut-off tail of an archived or set-aside file, as opposed to the normal end of the list.
func (c *Cursor) Torn() bool {
	return c.torn
}

// Lookup finds the data object with the given payload through the data hash table. A payload that is empty or longer than 4,096
// bytes is never found.
func (f *File) Lookup(payload []byte) (Data, bool, error) {
	d, found, err := f.lookup(payload)
	if errors.Is(err, errTorn) {
		return Data{}, false, nil
	}
	return d, found, err
}

func (f *File) lookup(payload []byte) (Data, bool, error) {
	if len(payload) == 0 || len(payload) > maxPayload {
		return Data{}, false, nil
	}
	if f.tableLen < bucketSize || f.tableLen%bucketSize != 0 {
		return Data{}, false, ErrMalformed
	}
	if err := f.check(f.tableOff, f.tableLen); err != nil {
		return Data{}, false, err
	}
	hash := sipHash24(f.hdr.FileID, payload)
	head, err := f.readUint64(f.tableOff + bucketSize*(hash%(f.tableLen/bucketSize)))
	if err != nil {
		return Data{}, false, err
	}
	return f.walkChain(head, hash, payload)
}

// walkChain follows a hash chain from off to strictly larger offsets and returns the data object with the hash and payload.
func (f *File) walkChain(off, hash uint64, payload []byte) (Data, bool, error) {
	var prev uint64
	for steps := 0; off != 0; steps++ {
		if steps >= maxChain || off <= prev {
			return Data{}, false, ErrMalformed
		}
		obj, size, err := f.object(off, typeData, f.payloadOff, f.payloadOff)
		if err != nil {
			return Data{}, false, err
		}
		next := byteOrder.Uint64(obj[24:])
		if byteOrder.Uint64(obj[16:]) == hash && obj[1]&objCompress == 0 && size == f.payloadOff+uint64(len(payload)) {
			d := Data{
				off:   off,
				first: byteOrder.Uint64(obj[40:]),
				head:  byteOrder.Uint64(obj[48:]),
				n:     byteOrder.Uint64(obj[56:]),
			}
			same, err := f.samePayload(off, payload)
			if err != nil || same {
				return d, same, err
			}
		}
		prev, off = off, next
	}
	return Data{}, false, nil
}

// samePayload compares the payload of the data object at off with want.
func (f *File) samePayload(off uint64, want []byte) (bool, error) {
	got := f.items[:len(want)]
	if err := f.readAt(off+f.payloadOff, got); err != nil {
		return false, err
	}
	return string(got) == string(want), nil
}

// Seek positions a cursor on the first entry of d with a seqnum greater than after. Entries that are cut off at the end of an
// archived file count as greater, so the cursor then stands at the end of the readable list. The work is a bisection through the
// doubling entry arrays: a constant number of reads, whatever the length of the list.
func (f *File) Seek(d Data, after uint64) (Cursor, error) {
	c, err := f.seek(d, after)
	if err != nil {
		return Cursor{}, err
	}
	return c, nil
}

func (f *File) seek(d Data, after uint64) (Cursor, error) {
	return f.seekList(Cursor{data: d.off, first: d.first, head: d.head, n: d.n}, after)
}

// seekList positions the cursor c, which holds the start of its list, on the first entry with a seqnum greater than after. The first
// entry of a data object's list is outside its chain of arrays; the main entry array has no such entry.
func (f *File) seekList(c Cursor, after uint64) (Cursor, error) {
	if c.n == 0 {
		return c, nil
	}
	skip, done, err := f.skipFirst(c, after)
	if err != nil || done {
		return c, err
	}
	off, base, remaining := c.head, uint64(0), c.n-skip
	for ord := 1; remaining > 0; ord++ {
		if off == 0 {
			c.idx = skip + base
			return c, nil
		}
		capacity, next, err := f.array(off, ord)
		if errors.Is(err, errTorn) {
			c.idx = skip + base
			return c, nil
		}
		if err != nil {
			return c, err
		}
		used := min(capacity, remaining)
		greater, err := f.itemAfter(off, used-1, after)
		if err != nil {
			return c, err
		}
		if greater {
			k, err := f.bisect(off, used-1, after)
			c.idx, c.arr, c.arrBase, c.arrCap, c.arrNo = skip+base+k, off, base, capacity, ord
			return c, err
		}
		remaining -= used
		base += capacity
		off = next
	}
	c.idx = c.n
	return c, nil
}

// skipFirst tells how many entries of the list lie outside its chain of arrays: one for the entry list of a data object, none for the main
// entry array. done is true when the first entry of a data object's list is already after the sequence number, so the cursor stays at
// the start.
func (f *File) skipFirst(c Cursor, after uint64) (skip uint64, done bool, err error) {
	if c.main {
		return 0, false, nil
	}
	greater, err := f.entryAfter(c.first, after)
	return 1, greater, err
}

// bisect returns the index of the first item of the array at arr, among items 0..last, whose entry is after seqnum after; item last
// is known to be.
func (f *File) bisect(arr, last, after uint64) (uint64, error) {
	lo, hi := uint64(0), last
	for lo < hi {
		mid := lo + (hi-lo)/2
		greater, err := f.itemAfter(arr, mid, after)
		if err != nil {
			return 0, err
		}
		if greater {
			hi = mid
		} else {
			lo = mid + 1
		}
	}
	return lo, nil
}

// itemAfter tells whether the entry in item k of the array at arr has a seqnum greater than after.
func (f *File) itemAfter(arr, k, after uint64) (bool, error) {
	off, err := f.arraySlot(arr, k)
	if err != nil {
		return false, err
	}
	return f.entryAfter(off, after)
}

// entryAfter tells whether the entry at off has a seqnum greater than after; an empty or cut-off item is treated as greater.
func (f *File) entryAfter(off, after uint64) (bool, error) {
	if off == 0 {
		return true, nil
	}
	obj, _, err := f.object(off, typeEntry, entryFixed, 24)
	if errors.Is(err, errTorn) {
		return true, nil
	}
	if err != nil {
		return false, err
	}
	return byteOrder.Uint64(obj[16:]) > after, nil
}

// array checks the entry array at off, the ord-th of its chain, and returns its capacity and the offset of the next array.
func (f *File) array(off uint64, ord int) (uint64, uint64, error) {
	if ord > maxArrays {
		return 0, 0, ErrMalformed
	}
	obj, size, err := f.object(off, typeArray, arrayFixed+f.slotWidth, arrayFixed)
	if err != nil {
		return 0, 0, err
	}
	next := byteOrder.Uint64(obj[16:])
	if (size-arrayFixed)%f.slotWidth != 0 || (next != 0 && next <= off) {
		return 0, 0, ErrMalformed
	}
	return (size - arrayFixed) / f.slotWidth, next, nil
}

// Next reads the entry at c into e, keeping only the fields named in want, and advances c; false means the list ended. At the cut-off
// end of an archived or set-aside file the list ends; the same tear in an online file is ErrMalformed, and c stays where it was so
// that the read can be repeated after Refresh.
func (f *File) Next(c *Cursor, want []string, e *Entry) (bool, error) {
	return f.advance(c, func(off uint64) error { return f.readEntry(off, want, e) })
}

// advance reads the entry at the cursor with read and moves the cursor behind it; a tear in an archived or set-aside file ends the
// list, and an error leaves the cursor where it was.
func (f *File) advance(c *Cursor, read func(off uint64) error) (bool, error) {
	ok, err := f.next(c, read)
	if errors.Is(err, errTorn) {
		c.torn = true
		return false, nil
	}
	return ok, err
}

func (f *File) next(c *Cursor, read func(off uint64) error) (bool, error) {
	cur := *c
	if cur.idx >= cur.n {
		if err := f.refreshList(&cur); err != nil {
			return false, err
		}
		if cur.idx >= cur.n {
			*c = cur
			return false, nil
		}
	}
	off, err := f.listItem(&cur)
	if err != nil {
		return false, err
	}
	if off == 0 {
		return false, f.short()
	}
	if err := read(off); err != nil {
		return false, err
	}
	cur.idx++
	*c = cur
	return true, nil
}

// refreshList re-reads the length and the starting offsets of the list from its data object, which grows in an online file.
func (f *File) refreshList(c *Cursor) error {
	if c.main {
		c.head, c.n = f.mainHead, f.mainN
		return nil
	}
	obj, _, err := f.object(c.data, typeData, f.payloadOff, f.payloadOff)
	if err != nil {
		return err
	}
	c.first, c.head, c.n = byteOrder.Uint64(obj[40:]), byteOrder.Uint64(obj[48:]), byteOrder.Uint64(obj[56:])
	return nil
}

// listItem returns the entry offset at list index c.idx and leaves c positioned on its array.
func (f *File) listItem(c *Cursor) (uint64, error) {
	j := c.idx
	if !c.main {
		if c.idx == 0 {
			return c.first, nil
		}
		j = c.idx - 1
	}
	if err := f.position(c, j); err != nil {
		return 0, err
	}
	return f.arraySlot(c.arr, j-c.arrBase)
}

// position moves c to the array that holds chain index j, from the head of the chain or onwards from the current array.
func (f *File) position(c *Cursor, j uint64) error {
	if c.arr == 0 || j < c.arrBase {
		if c.head == 0 {
			return f.short()
		}
		capacity, _, err := f.array(c.head, 1)
		if err != nil {
			return err
		}
		c.arr, c.arrBase, c.arrCap, c.arrNo = c.head, 0, capacity, 1
	}
	for j >= c.arrBase+c.arrCap {
		next, err := f.readUint64(c.arr + 16)
		if err != nil {
			return err
		}
		if next == 0 {
			return f.short()
		}
		if next <= c.arr {
			return ErrMalformed
		}
		capacity, _, err := f.array(next, c.arrNo+1)
		if err != nil {
			return err
		}
		c.arr, c.arrBase, c.arrCap, c.arrNo = next, c.arrBase+c.arrCap, capacity, c.arrNo+1
	}
	return nil
}
