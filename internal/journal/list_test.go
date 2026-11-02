package journal

import (
	"errors"
	"strconv"
	"testing"
)

// onlineKernelFile builds an online file of 30 kernel entries (with syslog entries between them) and opens it through a counting reader.
func onlineKernelFile(t *testing.T, compact bool) ([]byte, *layout, *refReader, *File) {
	t.Helper()
	o := testOptions()
	o.compact, o.online = compact, true
	b, lay := buildFile(kernelEntries(30, true), o)
	r := newRefReader(b)
	f, err := r.openRef(false)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	return b, lay, r, f
}

// expectNextMalformedInArena calls Next on c and expects false and ErrMalformed, an unchanged cursor and no read past the arena.
func expectNextMalformedInArena(t *testing.T, f *File, r *refReader, c Cursor) {
	t.Helper()
	before := c
	r.maxEnd = 0
	var e Entry

	ok, err := f.Next(&c, wantNames, &e)

	if ok || !errors.Is(err, ErrMalformed) {
		t.Errorf("Next() = %v, %v, want false and %v", ok, err, ErrMalformed)
	}
	if err != nil && err.Error() != "journal: malformed file" {
		t.Errorf("Next() error text = %q, want %q", err.Error(), "journal: malformed file")
	}
	if c != before {
		t.Errorf("Next() moved the cursor to %+v, want it unchanged at %+v", c, before)
	}
	if r.maxEnd > i64(f.declEnd) {
		t.Errorf("Next() read up to offset %d, want no read ending beyond the arena's end %d", r.maxEnd, f.declEnd)
	}
}

func TestFile_NextAfterTheArenaShrankBeforeTheNextSlotIsMalformed(t *testing.T) {
	for _, compact := range []bool{false, true} {
		t.Run("compact="+strconv.FormatBool(compact), func(t *testing.T) {
			b, lay, r, f := onlineKernelFile(t, compact)
			c := cursorAfter(t, f, 3) // read the first entry and chain indexes 0 and 1: the next is slot 2 of the first array
			arr := lay.dataArrays[kernelPayload][0]
			w := lay.slotWidth()
			copy(b[lay.slotAt(arr, 2):lay.slotAt(arr, 2)+w], b[lay.slotAt(arr, 0):lay.slotAt(arr, 0)+w]) // an earlier, valid entry
			refreshWithArenaEnd(t, f, r, b, lay.headerSize, lay.slotAt(arr, 2))

			expectNextMalformedInArena(t, f, r, c)
		})
	}
}

func TestFile_NextAfterTheArenaShrankBeforeTheArrayLinkIsMalformed(t *testing.T) {
	for _, compact := range []bool{false, true} {
		t.Run("compact="+strconv.FormatBool(compact), func(t *testing.T) {
			b, lay, r, f := onlineKernelFile(t, compact)
			c := cursorAfter(t, f, 5) // read the first entry and chain indexes 0 to 3: all four slots of the first array
			arr := lay.dataArrays[kernelPayload][0]
			if c.arr != arr || c.arrCap != 4 {
				t.Fatalf("cursor stands on array %d of capacity %d, want the first array %d of capacity 4", c.arr, c.arrCap, arr)
			}
			refreshWithArenaEnd(t, f, r, b, lay.headerSize, arr+16)

			expectNextMalformedInArena(t, f, r, c)
		})
	}
}
