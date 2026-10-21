package mariadb

import (
	"bytes"
	"errors"
	"io"
	"runtime"
	"strings"
	"testing"
)

// frames puts payloads into packets with the sequence numbers 0, 1, 2 and so on, as a standalone reader expects them.
func frames(payloads ...[]byte) []byte {
	var out []byte
	for i, p := range payloads {
		out = append(out, encodePacket(byte(i&0xff), p)...)
	}
	return out
}

// lazyRow is a stream of packets that carries one row payload of a prefix and then a filler of any size, split into
// packets of 0xffffff bytes, without holding the payload in memory; an EOF packet follows.
type lazyRow struct {
	prefix  []byte
	total   int64
	emitted int64
	seq     byte
	header  []byte
	frame   int
	tail    []byte
}

// newLazyRow returns a lazyRow whose payload is the prefix and then filler bytes of 'x'.
func newLazyRow(prefix []byte, filler int64) *lazyRow {
	return &lazyRow{prefix: prefix, total: int64(len(prefix)) + filler}
}

var fillerBlock = bytes.Repeat([]byte{'x'}, 4096)

// Read produces the stream.
func (l *lazyRow) Read(p []byte) (int, error) {
	switch {
	case len(l.header) > 0:
		n := copy(p, l.header)
		l.header = l.header[n:]
		return n, nil
	case l.frame > 0:
		return l.payload(p), nil
	case l.emitted < l.total:
		size := int(min(l.total-l.emitted, maxFramePayload))
		l.header = []byte{byte(size & 0xff), byte(size >> 8 & 0xff), byte(size >> 16 & 0xff), l.seq}
		l.seq++
		l.frame = size
		return l.Read(p)
	}
	return l.eof(p)
}

// payload copies bytes of the current frame.
func (l *lazyRow) payload(p []byte) int {
	n := min(len(p), l.frame)
	if l.emitted < int64(len(l.prefix)) {
		n = min(n, len(l.prefix)-int(l.emitted))
		copy(p, l.prefix[l.emitted:])
	} else {
		n = min(n, len(fillerBlock))
		copy(p, fillerBlock[:n])
	}
	l.emitted += int64(n)
	l.frame -= n
	return n
}

// eof sends the EOF packet that follows the row, with the next sequence number.
func (l *lazyRow) eof(p []byte) (int, error) {
	if l.tail == nil {
		l.tail = encodePacket(l.seq, eofPayload(statusCaptured))
	}
	if len(l.tail) == 0 {
		return 0, io.EOF
	}
	n := copy(p, l.tail)
	l.tail = l.tail[n:]
	return n, nil
}

// statusNameColumn and statusValueColumn are the columns of SHOW GLOBAL STATUS as the statement fixes them.
var (
	statusNameColumn  = column{name: "Variable_name", limit: 64, cut: true}
	statusValueColumn = column{name: "Value", limit: 1025, cut: true}
)

// bigRowPrefix starts a row of a name and a value of n bytes.
func bigRowPrefix(name string, n uint64) []byte {
	return append(rowPayload(text(name)), lenencBytes(n)...)
}

// allocatedBy returns the bytes a function allocates.
func allocatedBy(f func()) uint64 {
	var before, after runtime.MemStats
	runtime.GC()
	runtime.ReadMemStats(&before)
	f()
	runtime.ReadMemStats(&after)
	return after.TotalAlloc - before.TotalAlloc
}

// bigCellResult is what reading a row with a cell of 64 MiB delivered.
type bigCellResult struct {
	name, value cell
	nameText    string
	first       byte
	err         error
	allocated   uint64
}

// readBigRow reads a row of a name and a value of 64 MiB with the value column, and then the packet after the row.
func readBigRow(valueColumn column) bigCellResult {
	const size = 64 << 20
	p := newPacketReader(newLazyRow(bigRowPrefix("Aborted_clients", size), size))
	var r bigCellResult
	r.allocated = allocatedBy(func() {
		if _, r.err = p.beginRow("status"); r.err != nil {
			return
		}
		if r.name, r.err = p.readCell("status", statusNameColumn); r.err != nil {
			return
		}
		r.nameText = string(r.name.value)
		if r.value, r.err = p.readCell("status", valueColumn); r.err != nil {
			return
		}
		r.first, r.err = p.beginRow("status")
	})
	return r
}

func TestPacketReader_DiscardsBeyondTheLimitWithBoundedMemory(t *testing.T) {
	const bound = 4 << 20
	t.Run("a cell that is cut", func(t *testing.T) {
		r := readBigRow(statusValueColumn)

		if r.err != nil {
			t.Fatalf("reading the row and the packet after it = %v, want no error", r.err)
		}
		if r.nameText != "Aborted_clients" || r.name.cut {
			t.Errorf("name = %q cut %v, want Aborted_clients not cut", r.nameText, r.name.cut)
		}
		if !r.value.cut || r.value.null || !bytes.Equal(r.value.value, bytes.Repeat([]byte{'x'}, 1025)) {
			t.Errorf("value = %d bytes cut %v null %v, want the first 1,025 bytes, cut", len(r.value.value), r.value.cut, r.value.null)
		}
		if r.first != 0xfe {
			t.Errorf("the packet after the row starts with %#x, want the EOF packet 0xfe", r.first)
		}
		if r.allocated > bound {
			t.Errorf("reading a cell of 64 MiB allocated %d bytes, want at most %d", r.allocated, bound)
		}
	})
	t.Run("a strict cell", func(t *testing.T) {
		r := readBigRow(column{name: "Value", limit: 1025})

		wantError(t, "readCell()", r.err, "mariadb: status: cell too long")
		if r.allocated > bound {
			t.Errorf("refusing a cell of 64 MiB allocated %d bytes, want at most %d", r.allocated, bound)
		}
	})
}

func TestPacketReader_CellLongerThanTheRowIsMalformed(t *testing.T) {
	const claimed = 64 << 20
	p := newPacketReader(newLazyRow(bigRowPrefix("Aborted_clients", claimed), 1<<20))
	var err error

	allocated := allocatedBy(func() {
		if _, err = p.beginRow("status"); err != nil {
			return
		}
		if _, err = p.readCell("status", statusNameColumn); err != nil {
			return
		}
		_, err = p.readCell("status", statusValueColumn)
	})

	wantError(t, "readCell()", err, "mariadb: status: malformed packet")
	if allocated > 4<<20 {
		t.Errorf("allocated %d bytes, want at most %d", allocated, 4<<20)
	}
}

// rowCells reads a row with the given columns from a stream that holds one row packet.
func rowCells(t *testing.T, payload []byte, cols ...column) ([]cell, error) {
	t.Helper()
	p := newPacketReader(bytes.NewReader(frames(payload)))
	if _, err := p.beginRow("status"); err != nil {
		return nil, err
	}
	var out []cell
	for _, col := range cols {
		c, err := p.readCell("status", col)
		if err != nil {
			return out, err
		}
		c.value = bytes.Clone(c.value) // a value is valid only until the next read
		out = append(out, c)
	}
	return out, nil
}

func TestPacketReader_ReadsLengthEncodedCells(t *testing.T) {
	wide := column{name: "Value", limit: 1 << 20, cut: false}
	for _, tc := range []struct {
		name string
		size int
	}{
		{"empty", 0}, {"one byte", 1}, {"250 bytes", 250}, {"251 bytes, two length bytes", 251},
		{"65,535 bytes", 65535}, {"65,536 bytes, three length bytes", 65536},
	} {
		t.Run(tc.name, func(t *testing.T) {
			value := strings.Repeat("v", tc.size)

			cells, err := rowCells(t, rowPayload(text("name"), text(value), text("after")), statusNameColumn, wide, statusNameColumn)

			if err != nil || len(cells) != 3 {
				t.Fatalf("cells = %d, error %v, want 3 and none", len(cells), err)
			}
			if string(cells[0].value) != "name" || string(cells[1].value) != value || string(cells[2].value) != "after" {
				t.Errorf("cells = %q, %d bytes, %q, want name, %d bytes, after", cells[0].value, len(cells[1].value), cells[2].value, tc.size)
			}
		})
	}
}

func TestPacketReader_ReadsNullAndLeavesTheNextCellIntact(t *testing.T) {
	cells, err := rowCells(t, rowPayload(text("a"), nullCell, text("c")), statusNameColumn, statusValueColumn, statusNameColumn)

	if err != nil || len(cells) != 3 {
		t.Fatalf("cells = %d, error %v, want 3 and none", len(cells), err)
	}
	if !cells[1].null || cells[0].null || cells[2].null || string(cells[2].value) != "c" {
		t.Errorf("cells = %+v, want a value, NULL and a value", cells)
	}
}

func TestPacketReader_CutsAndFlagsLongCells(t *testing.T) {
	small := column{name: "Value", limit: 10, cut: true}
	strict := column{name: "Value", limit: 10}
	long := strings.Repeat("0123456789", 10)
	exact := long[:10]

	cut, err := rowCells(t, rowPayload(text(long), text("next")), small, statusNameColumn)
	if err != nil || len(cut) != 2 {
		t.Fatalf("cut cells = %d, error %v", len(cut), err)
	}
	if string(cut[0].value) != exact || !cut[0].cut || string(cut[1].value) != "next" || cut[1].cut {
		t.Errorf("cut = %q %v, then %q %v, want %q cut and then next", cut[0].value, cut[0].cut, cut[1].value, cut[1].cut, exact)
	}
	atLimit, err := rowCells(t, rowPayload(text(exact)), small)
	if err != nil || atLimit[0].cut || string(atLimit[0].value) != exact {
		t.Errorf("a cell at the limit = %q cut %v error %v, want it whole and not cut", atLimit[0].value, atLimit[0].cut, err)
	}
	_, err = rowCells(t, rowPayload(text(long)), strict)
	wantError(t, "readCell()", err, "mariadb: status: cell too long")
	atStrictLimit, err := rowCells(t, rowPayload(text(exact)), strict)
	if err != nil || string(atStrictLimit[0].value) != exact {
		t.Errorf("a strict cell at the limit = %q error %v, want it whole", atStrictLimit[0].value, err)
	}
}

func TestPacketReader_RefusesMalformedCells(t *testing.T) {
	for _, tc := range []struct {
		name    string
		payload []byte
	}{
		{"a length of 0xff", append(rowPayload(text("a")), 0xff, 'x')},
		{"a two-byte length cut short", append(rowPayload(text("a")), 0xfc, 0x01)},
		{"a three-byte length cut short", append(rowPayload(text("a")), 0xfd, 0x01, 0x00)},
		{"an eight-byte length cut short", append(rowPayload(text("a")), 0xfe, 0x01, 0x00, 0x00)},
		{"a cell longer than the packet", append(rowPayload(text("a")), 200, 'x', 'y')},
		{"a row that ends before its last cell", rowPayload(text("a"))},
	} {
		t.Run(tc.name, func(t *testing.T) {
			_, err := rowCells(t, tc.payload, statusNameColumn, statusValueColumn)

			wantError(t, "readCell()", err, "mariadb: status: malformed packet")
		})
	}
}

func TestPacketReader_ChecksTheSequenceOfRows(t *testing.T) {
	stream := append(encodePacket(0, rowPayload(text("a"), text("1"))), encodePacket(5, rowPayload(text("b"), text("2")))...)
	p := newPacketReader(bytes.NewReader(stream))
	if _, err := p.beginRow("variables"); err != nil {
		t.Fatalf("beginRow() = %v, want nil for the first row", err)
	}
	if _, err := p.readCell("variables", statusNameColumn); err != nil {
		t.Fatalf("readCell() = %v", err)
	}
	if _, err := p.readCell("variables", statusValueColumn); err != nil {
		t.Fatalf("readCell() = %v", err)
	}

	_, err := p.beginRow("variables")

	wantError(t, "beginRow()", err, "mariadb: variables: packet out of sequence")
}

func TestPacketReader_NextReturnsWholeControlPackets(t *testing.T) {
	p := newPacketReader(bytes.NewReader(frames([]byte("abc"), nil, bytes.Repeat([]byte{'z'}, 8))))

	for i, want := range [][]byte{[]byte("abc"), {}, bytes.Repeat([]byte{'z'}, 8)} {
		got, err := p.next("login", 8)
		if err != nil || !bytes.Equal(got, want) {
			t.Errorf("next() #%d = %q, %v, want %q", i, got, err, want)
		}
	}
}

func TestPacketReader_NextRefusals(t *testing.T) {
	for _, tc := range []struct {
		name   string
		stream []byte
		limit  int
		want   string
	}{
		{"above the limit", frames(bytes.Repeat([]byte{'a'}, 9)), 8, "mariadb: greeting: packet too large"},
		{"a header above the limit and no payload", []byte{0x01, 0x00, 0x01, 0x00}, 64 * 1024, "mariadb: greeting: packet too large"},
		{"the first packet with sequence number 1", encodePacket(1, []byte("a")), 8, "mariadb: greeting: packet out of sequence"},
		{"the second packet with sequence number 0", append(encodePacket(0, []byte("a")), encodePacket(0, []byte("b"))...), 8, "mariadb: greeting: packet out of sequence"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			p := newPacketReader(bytes.NewReader(tc.stream))
			var err error
			for range 2 {
				if _, err = p.next("greeting", tc.limit); err != nil {
					break
				}
			}

			wantError(t, "next()", err, tc.want)
		})
	}
}

func TestPacketReader_SequenceNumbersWrapAt256(t *testing.T) {
	var stream []byte
	for i := range 260 {
		stream = append(stream, encodePacket(byte(i&0xff), []byte{byte(i & 0xff)})...)
	}
	p := newPacketReader(bytes.NewReader(stream))

	for i := range 260 {
		got, err := p.next("status", 4)
		if err != nil || len(got) != 1 || got[0] != byte(i&0xff) {
			t.Fatalf("next() #%d = %v, %v, want one byte %d", i, got, err, byte(i&0xff))
		}
	}
}

func TestWritePacket_FramesThePayload(t *testing.T) {
	for _, tc := range []struct {
		name    string
		seq     byte
		payload []byte
		want    []byte
	}{
		{"empty answer", 3, nil, []byte{0, 0, 0, 3}},
		{"a ping", 0, []byte{commandPing}, []byte{1, 0, 0, 0, commandPing}},
		{"a payload of 300 bytes", 7, bytes.Repeat([]byte{'q'}, 300), append([]byte{0x2c, 0x01, 0x00, 7}, bytes.Repeat([]byte{'q'}, 300)...)},
	} {
		t.Run(tc.name, func(t *testing.T) {
			var buf bytes.Buffer

			err := writePacket(&buf, tc.seq, tc.payload)

			if err != nil || !bytes.Equal(buf.Bytes(), tc.want) {
				t.Errorf("writePacket() wrote %x, %v, want %x", buf.Bytes(), err, tc.want)
			}
		})
	}
}

// failingWriter fails every write.
type failingWriter struct{ err error }

func (w failingWriter) Write([]byte) (int, error) { return 0, w.err }

func TestWritePacket_ReturnsTheWriteError(t *testing.T) {
	errBroken := errors.New("broken pipe")

	err := writePacket(failingWriter{err: errBroken}, 0, []byte{commandPing})

	if !errors.Is(err, errBroken) {
		t.Errorf("writePacket() = %v, want the error of the writer", err)
	}
}
