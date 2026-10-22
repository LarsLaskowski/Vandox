package journal

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"math"
	"strconv"
)

// Object types, flags and header states of the non-compact journal layout (systemd 249, JOURNAL_FILE_FORMAT.md).
const (
	objData       = 1
	objEntry      = 3
	objDataTable  = 4
	objFieldTable = 5
	objArray      = 6

	incompatXZ      = 1 << 0
	incompatLZ4     = 1 << 1
	incompatKeyed   = 1 << 2
	incompatZSTD    = 1 << 3
	incompatCompact = 1 << 4

	objFlagZSTD = 4

	stateOnline   = 1
	stateArchived = 2

	// Header field offsets.
	hdrIncompat     = 12
	hdrState        = 16
	hdrFileID       = 24
	hdrMachineID    = 40
	hdrBootID       = 56
	hdrSeqnumID     = 72
	hdrHeaderSize   = 88
	hdrArenaSize    = 96
	hdrDataTable    = 104
	hdrDataTableSz  = 112
	hdrFieldTable   = 120
	hdrFieldTableSz = 128
	hdrTailObject   = 136
	hdrNObjects     = 144
	hdrNEntries     = 152
	hdrTailSeqnum   = 160
	hdrHeadSeqnum   = 168
	hdrEntryArray   = 176
	hdrHeadRealtime = 184
	hdrTailRealtime = 192
	hdrTailMono     = 200
	hdrNData        = 208
	hdrNEntryArrays = 232

	fixedHeaderSize = 256
)

var le = binary.LittleEndian

// u64, u32, u16 and lowByte convert a non-negative size that fits the target type.
func u64(n int) uint64 {
	if n < 0 {
		panic("negative size")
	}
	return uint64(n)
}

func u32(n int) uint32 {
	if n < 0 || n > math.MaxUint32 {
		panic("size out of range")
	}
	return uint32(n)
}

func u16(n int) uint16 {
	if n < 0 || n > math.MaxUint16 {
		panic("size out of range")
	}
	return uint16(n)
}

func lowByte(n int) byte {
	if n < 0 || n > math.MaxUint8 {
		panic("size out of range")
	}
	return byte(n)
}

// i64 and asInt convert an offset or time of a built file.
func i64(v uint64) int64 {
	if v > math.MaxInt64 {
		panic("value out of range")
	}
	return int64(v)
}

func asInt(v uint64) int {
	if v > math.MaxInt32 {
		panic("value out of range")
	}
	return int(v)
}

// bItem is one item (field) of a built entry; compressed stores the payload as a zstd frame of one raw block.
type bItem struct {
	payload    []byte
	compressed bool
}

// bEntry is one entry of a built journal file.
type bEntry struct {
	seqnum, realtime, mono uint64
	boot                   [16]byte
	items                  []bItem
}

// bOptions configures buildFile. The zero value builds an archived file of the server's layout.
type bOptions struct {
	fileID, machineID, seqnumID [16]byte
	online                      bool
	headerSize                  int                         // 0 takes 256
	incompat                    uint32                      // 0 takes KEYED-HASH and ZSTD
	buckets                     int                         // buckets of the data hash table, 0 takes 16
	arrayCap                    func(i int) int             // capacity of the i-th array of a chain, nil doubles from 4
	hash                        func(payload []byte) uint64 // nil takes SipHash-2-4 keyed with the file ID
}

// layout tells a test where the objects of a built file lie.
type layout struct {
	headerSize  int
	size        int
	buckets     int
	tableItems  uint64
	dataOrder   []uint64
	data        map[string]uint64
	dataArrays  map[string][]uint64
	entries     []uint64
	mainArrays  []uint64
	entryCounts map[string]uint64
}

type chain struct {
	arrays []uint64
	caps   []int
	n      int
}

type dataRec struct {
	off   uint64
	hash  uint64
	n     uint64
	chain chain
}

type fileBuilder struct {
	b         []byte
	o         bOptions
	hs        int
	table     uint64
	records   map[string]*dataRec
	order     []string
	main      chain
	mainFirst uint64
	lay       *layout
	nObjects  uint64
	tail      uint64
	nArrays   uint64
}

func (w *fileBuilder) alloc(typ, flags byte, size int) uint64 {
	for len(w.b)%8 != 0 {
		w.b = append(w.b, 0)
	}
	off := uint64(len(w.b))
	w.b = append(w.b, make([]byte, size)...)
	w.b[off] = typ
	w.b[off+1] = flags
	le.PutUint64(w.b[off+8:], u64(size))
	w.nObjects++
	w.tail = off
	return off
}

func (w *fileBuilder) set(off, v uint64) { le.PutUint64(w.b[off:], v) }

func (w *fileBuilder) get(off uint64) uint64 { return le.Uint64(w.b[off:]) }

func (w *fileBuilder) hashOf(payload []byte) uint64 {
	if w.o.hash != nil {
		return w.o.hash(payload)
	}
	return refSipHash24(w.o.fileID, payload)
}

func (w *fileBuilder) capFor(i int) int {
	if w.o.arrayCap != nil {
		return w.o.arrayCap(i)
	}
	return 4 << i
}

// zstdRawFrame wraps p (fewer than 65,792 bytes) into a zstd frame made of one raw block.
func zstdRawFrame(p []byte) []byte {
	out := []byte{0x28, 0xb5, 0x2f, 0xfd}
	if len(p) < 256 {
		out = append(out, 0x20, lowByte(len(p)))
	} else {
		out = append(out, 0x60)
		out = binary.LittleEndian.AppendUint16(out, u16(len(p)-256))
	}
	blk := binary.LittleEndian.AppendUint32(nil, u32(len(p))<<3|1)
	out = append(out, blk[:3]...)
	return append(out, p...)
}

func (w *fileBuilder) addData(it bItem) *dataRec {
	key := string(it.payload)
	if r, ok := w.records[key]; ok {
		return r
	}
	hash := w.hashOf(it.payload)
	stored, flags := it.payload, byte(0)
	if it.compressed {
		stored, flags = zstdRawFrame(it.payload), objFlagZSTD
	}
	off := w.alloc(objData, flags, 64+len(stored))
	w.set(off+16, hash)
	copy(w.b[off+64:], stored)
	bucket := w.table + 16 + 16*(hash%u64(w.lay.buckets))
	if tail := w.get(bucket + 8); tail == 0 {
		w.set(bucket, off)
	} else {
		w.set(tail+24, off)
	}
	w.set(bucket+8, off)
	r := &dataRec{off: off, hash: hash}
	w.records[key] = r
	w.order = append(w.order, key)
	w.lay.dataOrder = append(w.lay.dataOrder, off)
	w.lay.data[key] = off
	return r
}

func (w *fileBuilder) appendArray(ch *chain, entryOff uint64, first func(off uint64)) {
	idx := ch.n
	i, acc := 0, 0
	for i < len(ch.caps) && idx >= acc+ch.caps[i] {
		acc += ch.caps[i]
		i++
	}
	if i == len(ch.caps) {
		c := w.capFor(i)
		off := w.alloc(objArray, 0, 24+8*c)
		w.nArrays++
		if i == 0 {
			first(off)
		} else {
			w.set(ch.arrays[i-1]+16, off)
		}
		ch.arrays = append(ch.arrays, off)
		ch.caps = append(ch.caps, c)
	}
	w.set(ch.arrays[i]+24+u64(8*(idx-acc)), entryOff)
	ch.n++
}

func (w *fileBuilder) addEntry(e bEntry) {
	recs := make([]*dataRec, len(e.items))
	for i, it := range e.items {
		recs[i] = w.addData(it)
	}
	off := w.alloc(objEntry, 0, 64+16*len(e.items))
	w.set(off+16, e.seqnum)
	w.set(off+24, e.realtime)
	w.set(off+32, e.mono)
	copy(w.b[off+40:], e.boot[:])
	var xor uint64
	seen := map[*dataRec]bool{}
	for i, r := range recs {
		w.set(off+64+u64(16*i), r.off)
		w.set(off+72+u64(16*i), r.hash)
		xor ^= r.hash
		if seen[r] {
			continue
		}
		seen[r] = true
		if r.n == 0 {
			w.set(r.off+40, off)
		} else {
			w.appendArray(&r.chain, off, func(a uint64) { w.set(r.off+48, a) })
		}
		r.n++
		w.set(r.off+56, r.n)
	}
	w.set(off+56, xor)
	w.appendArray(&w.main, off, func(a uint64) { w.mainFirst = a })
	w.lay.entries = append(w.lay.entries, off)
}

// sizeHint estimates the size of the built file so that the buffer is allocated once.
func sizeHint(entries []bEntry) int {
	n := 4096
	for _, e := range entries {
		n += 64 + 16*len(e.items) + 48
		for _, it := range e.items {
			n += len(it.payload) + 8
		}
	}
	return n
}

// buildFile builds a journal file holding entries (in the given order) and tells where its objects lie.
func buildFile(entries []bEntry, o bOptions) ([]byte, *layout) {
	hs := o.headerSize
	if hs == 0 {
		hs = fixedHeaderSize
	}
	nb := o.buckets
	if nb == 0 {
		nb = 16
	}
	w := &fileBuilder{
		o: o, hs: hs, records: map[string]*dataRec{},
		lay: &layout{headerSize: hs, buckets: nb, data: map[string]uint64{}, dataArrays: map[string][]uint64{}, entryCounts: map[string]uint64{}},
	}
	w.b = make([]byte, hs, hs+sizeHint(entries))
	w.table = w.alloc(objDataTable, 0, 16+16*nb)
	w.lay.tableItems = w.table + 16
	fieldTable := w.alloc(objFieldTable, 0, 16+16*4)
	for _, e := range entries {
		w.addEntry(e)
	}
	w.finish(entries, fieldTable)
	return w.b, w.lay
}

func (w *fileBuilder) finish(entries []bEntry, fieldTable uint64) {
	b := w.b
	copy(b, "LPKSHHRH")
	incompat := w.o.incompat
	if incompat == 0 {
		incompat = incompatKeyed | incompatZSTD
	}
	le.PutUint32(b[hdrIncompat:], incompat)
	state := byte(stateArchived)
	if w.o.online {
		state = stateOnline
	}
	b[hdrState] = state
	copy(b[hdrFileID:], w.o.fileID[:])
	copy(b[hdrMachineID:], w.o.machineID[:])
	copy(b[hdrSeqnumID:], w.o.seqnumID[:])
	w.set(hdrHeaderSize, u64(w.hs))
	w.set(hdrArenaSize, u64(len(b)-w.hs))
	w.set(hdrDataTable, w.table+16)
	w.set(hdrDataTableSz, u64(16*w.lay.buckets))
	w.set(hdrFieldTable, fieldTable+16)
	w.set(hdrFieldTableSz, 16*4)
	w.set(hdrNObjects, w.nObjects)
	w.set(hdrNEntries, uint64(len(entries)))
	w.set(hdrEntryArray, w.mainFirst)
	w.set(hdrNData, uint64(len(w.order)))
	w.set(hdrNEntryArrays, w.nArrays)
	if len(entries) > 0 {
		first, last := entries[0], entries[len(entries)-1]
		w.set(hdrHeadSeqnum, first.seqnum)
		w.set(hdrTailSeqnum, last.seqnum)
		w.set(hdrHeadRealtime, first.realtime)
		w.set(hdrTailRealtime, last.realtime)
		w.set(hdrTailMono, last.mono)
		copy(b[hdrBootID:], last.boot[:])
	}
	w.set(hdrTailObject, w.tail)
	w.lay.size = len(b)
	w.lay.mainArrays = w.main.arrays
	for k, r := range w.records {
		w.lay.dataArrays[k] = r.chain.arrays
		w.lay.entryCounts[k] = r.n
	}
}

// refReader serves a byte slice through io.ReaderAt, counts the reads, refuses to be slurped and can hide a tail.
type refReader struct {
	data     []byte
	limit    int // visible bytes, 0 takes all
	reads    int
	maxRead  int
	violated string
}

func newRefReader(data []byte) *refReader { return &refReader{data: data} }

func (g *refReader) size() int64 {
	if g.limit > 0 {
		return int64(min(g.limit, len(g.data)))
	}
	return int64(len(g.data))
}

// ReadAt implements io.ReaderAt.
func (g *refReader) ReadAt(p []byte, off int64) (int, error) {
	g.reads++
	g.maxRead = max(g.maxRead, len(p))
	if len(p) > 64<<10 && g.violated == "" {
		g.violated = "read of " + strconv.Itoa(len(p)) + " bytes at " + strconv.FormatInt(off, 10)
	}
	if g.reads > 400000 && g.violated == "" {
		g.violated = "more than 400000 reads"
	}
	sz := g.size()
	if off < 0 || off >= sz {
		return 0, io.EOF
	}
	n := copy(p, g.data[off:sz])
	if n < len(p) {
		return n, io.EOF
	}
	return n, nil
}

func hexID(s string) [16]byte {
	var id [16]byte
	for i := range id {
		v, err := strconv.ParseUint(s[2*i:2*i+2], 16, 8)
		if err != nil {
			panic(err)
		}
		id[i] = byte(v)
	}
	return id
}

const (
	testBoot = "c0ffee00c0ffee00c0ffee00c0ffee0a"
	testBase = 1790000000000000 // µs, the real-time stamp of the first built entry
	testMono = 5000000000       // µs
	padText  = "handmade padding of a kernel line to give it some size and a payload that is not tiny "
)

// kernelEntry builds the i-th kernel entry (seqnums first, first+3, ... leave holes).
func kernelEntry(i int, first uint64, pad int) bEntry {
	mono := u64(testMono + i*1000000)
	msg := "handmade kernel line " + strconv.Itoa(i)
	for len(msg) < pad {
		msg += " " + padText
	}
	return bEntry{
		seqnum: first + u64(3*i), realtime: testBase + u64(i)*1000000, mono: mono, boot: hexID(testBoot),
		items: []bItem{
			{payload: []byte("_TRANSPORT=kernel")},
			{payload: []byte("SYSLOG_FACILITY=0")},
			{payload: []byte("SYSLOG_IDENTIFIER=kernel")},
			{payload: []byte("PRIORITY=6")},
			{payload: []byte("MESSAGE=" + msg)},
			{payload: []byte("_SOURCE_MONOTONIC_TIMESTAMP=" + strconv.FormatUint(mono-300000, 10))},
			{payload: []byte("_BOOT_ID=" + testBoot)},
		},
	}
}

// otherEntry builds a non-kernel entry that sits between kernel entries.
func otherEntry(i int, seq uint64) bEntry {
	return bEntry{
		seqnum: seq, realtime: testBase + u64(i)*1000000 + 500000, mono: u64(testMono+i*1000000) + 500000, boot: hexID(testBoot),
		items: []bItem{
			{payload: []byte("_TRANSPORT=syslog")},
			{payload: []byte("SYSLOG_FACILITY=1")},
			{payload: []byte("MESSAGE=handmade syslog line " + strconv.Itoa(i))},
			{payload: []byte("_BOOT_ID=" + testBoot)},
		},
	}
}

// kernelEntries builds n kernel entries; with others, a syslog entry follows every fourth one. Seqnums are strictly increasing with holes.
func kernelEntries(n int, others bool) []bEntry {
	out := make([]bEntry, 0, n+n/4)
	for i := 0; i < n; i++ {
		e := kernelEntry(i, 10, 0)
		out = append(out, e)
		if others && i%4 == 3 {
			out = append(out, otherEntry(i, e.seqnum+1))
		}
	}
	return out
}

// echoEntry is what a walk saw of one entry; the buffers of Entry are copied.
type echoEntry struct {
	seqnum  uint64
	items   int
	boot    [16]byte
	message string
	fac     []string
	compOne bool
	names   int
}

func echo(e *Entry) echoEntry {
	out := echoEntry{seqnum: e.Seqnum, items: e.Items, boot: e.BootID, names: len(e.Fields)}
	for _, f := range e.Fields {
		if f.Compressed {
			out.compOne = true
		}
	}
	if m, ok := e.Field("MESSAGE"); ok && len(m) > 0 {
		out.message = string(m[0].Value)
	}
	if fs, ok := e.Field("SYSLOG_FACILITY"); ok {
		for _, f := range fs {
			out.fac = append(out.fac, string(f.Value))
		}
	}
	return out
}

var wantNames = []string{"MESSAGE", "SYSLOG_FACILITY", "_SOURCE_MONOTONIC_TIMESTAMP"}

const walkLimit = 20000

var errRunaway = errors.New("walk did not end")

// walkResult is the outcome of reading the kernel entry list of a file from its start.
type walkResult struct {
	err     error
	found   bool
	entries []echoEntry
	panic   any
}

// walk opens r, looks up the kernel entry list, reads it to its end and looks up a missing payload.
func walk(r io.ReaderAt, size int64, setAside bool) (res walkResult) {
	defer func() {
		if p := recover(); p != nil {
			res.panic = p
		}
	}()
	f, err := Open(r, size, setAside)
	if err != nil {
		res.err = err
		return res
	}
	d, found, err := f.Lookup([]byte("_TRANSPORT=kernel"))
	if err != nil {
		res.err = err
		return res
	}
	res.found = found
	if found {
		res.err = readAll(f, d, 0, &res)
		if res.err != nil {
			return res
		}
	}
	if _, _, err := f.Lookup([]byte("NO_SUCH_FIELD=absent")); err != nil {
		res.err = err
	}
	return res
}

// readAll reads the entries of d after seqnum after into res.
func readAll(f *File, d Data, after uint64, res *walkResult) error {
	c, err := f.Seek(d, after)
	if err != nil {
		return err
	}
	var e Entry
	for i := 0; i < walkLimit; i++ {
		ok, err := f.Next(&c, wantNames, &e)
		if err != nil {
			return err
		}
		if !ok {
			return nil
		}
		res.entries = append(res.entries, echo(&e))
	}
	return errRunaway
}

func (w walkResult) String() string {
	return fmt.Sprintf("err=%v found=%v entries=%d panic=%v", w.err, w.found, len(w.entries), w.panic)
}

// mutated returns a copy of b with the 8-byte little-endian value v written at off.
func mutated(b []byte, off uint64, v uint64) []byte {
	c := append([]byte(nil), b...)
	le.PutUint64(c[off:], v)
	return c
}
