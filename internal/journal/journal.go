// Package journal reads the binary journal files of systemd-journald through io.ReaderAt, without mmap.
//
// It reads the non-compact layout of systemd 249 (the layout of a 256-byte header, KEYED-HASH, optionally compressed items) and never
// decompresses anything. Every offset and size of a file is checked against the file's bounds before it is used, chains are followed
// only to strictly larger offsets, and what is read per call is limited, so a damaged or hostile file ends in ErrMalformed and never in
// a loop or a large allocation. A File and the Cursors and Entries used with it are not safe for concurrent use.
package journal

import (
	"encoding/binary"
	"errors"
	"io"
	"math"
	"time"
)

var (
	// ErrUnsupported reports a file layout the reader does not read.
	ErrUnsupported = errors.New("journal: unsupported layout")
	// ErrMalformed reports a file that violates the layout's bounds.
	ErrMalformed = errors.New("journal: malformed file")
	// ErrRead reports that the underlying reader failed; errors.Is also finds the reader's own error.
	ErrRead = errors.New("journal: read failed")

	// errTorn marks an object that lies inside the header's arena but beyond the end of the file: the tail was cut. It never leaves
	// the package: archived and set-aside files end there, online files report ErrMalformed (retried at the next poll).
	errTorn = errors.New("journal: torn tail")
)

// readError wraps the error of the underlying reader; its text never holds a path or an offset.
type readError struct{ cause error }

func (e *readError) Error() string        { return ErrRead.Error() }
func (e *readError) Unwrap() error        { return e.cause }
func (e *readError) Is(target error) bool { return target == ErrRead }

// Layout constants of the journal file format (systemd 249, JOURNAL_FILE_FORMAT.md).
const (
	signature     = "LPKSHHRH"
	minHeaderSize = 256
	maxHeaderSize = 4096
	alignment     = 8

	typeData  = 1
	typeEntry = 3
	typeArray = 6

	flagsXZ     = 1 << 0
	flagsLZ4    = 1 << 1
	flagsKeyed  = 1 << 2
	flagsZSTD   = 1 << 3
	objCompress = 7 // XZ, LZ4 or ZSTD in the flags byte of an object header

	headerOnline = 1

	atIncompat     = 12
	atState        = 16
	atFileID       = 24
	atMachineID    = 40
	atSeqnumID     = 72
	atHeaderSize   = 88
	atArenaSize    = 96
	atTableOffset  = 104
	atTableSize    = 112
	atTailSeqnum   = 160
	atHeadSeqnum   = 168
	atHeadRealtime = 184
	atTailRealtime = 192

	dataFixed     = 64 // object header and the fixed part of a data object
	entryFixed    = 64
	arrayFixed    = 24
	itemSize      = 16 // size of an item of an entry
	bucketSize    = 16 // size of a bucket of the data hash table
	tableHeader   = 16
	maxArrays     = 64   // entry arrays of one chain; they double in size, so 64 cover any file
	maxItems      = 512  // items of one entry
	maxPayload    = 4096 // bytes of a payload read
	namePrefix    = 64   // bytes of a payload read to see its name
	maxChain      = 1 << 16
	maxKept       = 16 // fields of one entry handed to the caller
	itemBufSize   = maxItems * itemSize
	scratchSize   = 256
	realtimeLimit = 253402300800000000 // 1 Jan 10000 UTC in µs, the first time time.Time.MarshalJSON refuses
	monotonicMax  = math.MaxInt64 / 1000
)

var byteOrder = binary.LittleEndian

// Header holds the parts of a journal file's header the reader keeps. A head or tail time that is zero or outside the range the wire
// format accepts (up to year 9999) is the zero time.Time.
type Header struct {
	FileID, MachineID, SeqnumID [16]byte
	Online, SetAside            bool
	HeadSeqnum, TailSeqnum      uint64
	HeadRealtime, TailRealtime  time.Time
}

// File is one open journal file.
type File struct {
	r        io.ReaderAt
	setAside bool
	fileSize uint64
	hs       uint64 // header size
	declEnd  uint64 // end of the arena as the header declares it
	tableOff uint64 // offset of the first bucket of the data hash table
	tableLen uint64 // bytes of the data hash table
	hdr      Header
	scratch  [scratchSize]byte
	items    [itemBufSize]byte
}

// Open reads the header of the journal file r of the given size; setAside marks a *.journal~ file.
func Open(r io.ReaderAt, size int64, setAside bool) (*File, error) {
	f := &File{r: r, setAside: setAside}
	if err := f.load(size); err != nil {
		return nil, err
	}
	return f, nil
}

// Header returns the header read by Open or Refresh.
func (f *File) Header() Header {
	return f.hdr
}

// Refresh re-reads the header after the file grew to size. On an error the File keeps what it knew before.
func (f *File) Refresh(size int64) error {
	next := *f
	if err := next.load(size); err != nil {
		return err
	}
	*f = next
	return nil
}

// load reads and checks the header of a file of the given size.
func (f *File) load(size int64) error {
	if size < minHeaderSize {
		return ErrUnsupported
	}
	f.fileSize = nonNegative(size)
	b := f.scratch[:minHeaderSize]
	if err := f.readAt(0, b); err != nil {
		if errors.Is(err, errTorn) {
			return ErrMalformed
		}
		return err
	}
	if string(b[:len(signature)]) != signature {
		return ErrUnsupported
	}
	hs := byteOrder.Uint64(b[atHeaderSize:])
	incompat := byteOrder.Uint32(b[atIncompat:])
	known := uint32(flagsXZ | flagsLZ4 | flagsKeyed | flagsZSTD)
	if hs < minHeaderSize || hs > maxHeaderSize || hs%alignment != 0 || hs > f.fileSize ||
		incompat&^known != 0 || incompat&flagsKeyed == 0 {
		return ErrUnsupported
	}
	f.hs = hs
	f.declEnd = hs + min(byteOrder.Uint64(b[atArenaSize:]), math.MaxInt64-hs)
	f.tableOff = byteOrder.Uint64(b[atTableOffset:])
	f.tableLen = byteOrder.Uint64(b[atTableSize:])
	f.hdr = Header{
		Online:       b[atState] == headerOnline,
		SetAside:     f.setAside,
		HeadSeqnum:   byteOrder.Uint64(b[atHeadSeqnum:]),
		TailSeqnum:   byteOrder.Uint64(b[atTailSeqnum:]),
		HeadRealtime: realtimeOf(byteOrder.Uint64(b[atHeadRealtime:])),
		TailRealtime: realtimeOf(byteOrder.Uint64(b[atTailRealtime:])),
	}
	copy(f.hdr.FileID[:], b[atFileID:])
	copy(f.hdr.MachineID[:], b[atMachineID:])
	copy(f.hdr.SeqnumID[:], b[atSeqnumID:])
	return nil
}

// live reports whether the file may still be written to: a tear in it is not the end of the file but a race to retry.
func (f *File) live() bool {
	return f.hdr.Online && !f.setAside
}

// short is the error for an object beyond the end of the file.
func (f *File) short() error {
	if f.live() {
		return ErrMalformed
	}
	return errTorn
}

// check tests that the length bytes at off are an aligned range inside the arena and the file.
func (f *File) check(off, length uint64) error {
	end := off + length
	if off%alignment != 0 || off < f.hs || end < off || end > f.declEnd {
		return ErrMalformed
	}
	if end > f.fileSize {
		return f.short()
	}
	return nil
}

// readAt fills p from off; a short read is a tear, any other failure is a read error.
func (f *File) readAt(off uint64, p []byte) error {
	n, err := f.r.ReadAt(p, toInt64(off))
	if n == len(p) {
		return nil
	}
	if err == nil || errors.Is(err, io.EOF) || errors.Is(err, io.ErrUnexpectedEOF) {
		return f.short()
	}
	return &readError{cause: err}
}

// readUint64 reads the little-endian number at off.
func (f *File) readUint64(off uint64) (uint64, error) {
	b := f.scratch[:8]
	if err := f.readAt(off, b); err != nil {
		return 0, err
	}
	return byteOrder.Uint64(b), nil
}

// object checks the object of the given type at off and returns up to n bytes of it (at most scratchSize, valid until the next read)
// and its full size.
func (f *File) object(off uint64, typ byte, minSize, n uint64) ([]byte, uint64, error) {
	if err := f.check(off, 16); err != nil {
		return nil, 0, err
	}
	rn := min(n, scratchSize, min(f.declEnd, f.fileSize)-off)
	b := f.scratch[:rn]
	if err := f.readAt(off, b); err != nil {
		return nil, 0, err
	}
	size := byteOrder.Uint64(b[8:])
	if b[0] != typ || size < minSize {
		return nil, 0, ErrMalformed
	}
	if err := f.check(off, size); err != nil {
		return nil, 0, err
	}
	return b[:min(rn, size)], size, nil
}

// realtimeOf turns a real-time stamp in µs into a time; zero and stamps the wire format refuses give the zero time.
func realtimeOf(us uint64) time.Time {
	if us == 0 || us >= realtimeLimit {
		return time.Time{}
	}
	return time.UnixMicro(toInt64(us)).UTC()
}

// monotonicOf turns a monotonic stamp in µs into a duration; a stamp that would overflow gives zero.
func monotonicOf(us uint64) time.Duration {
	if us > monotonicMax {
		return 0
	}
	return time.Duration(toInt64(us)) * time.Microsecond
}

func toInt64(v uint64) int64 {
	if v > math.MaxInt64 {
		return math.MaxInt64
	}
	return int64(v)
}

func nonNegative(v int64) uint64 {
	if v < 0 {
		return 0
	}
	return uint64(v)
}

func toInt(v uint64) int {
	if v > math.MaxInt32 {
		return math.MaxInt32
	}
	return int(v)
}
