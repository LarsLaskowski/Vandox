// Package journal reads the binary journal files of systemd-journald through io.ReaderAt, without mmap.
package journal

import (
	"errors"
	"io"
	"io/fs"
	"time"
)

var (
	// ErrUnsupported reports a file layout the reader does not read.
	ErrUnsupported = errors.New("journal: unsupported layout")
	// ErrMalformed reports a file that violates the layout's bounds.
	ErrMalformed = errors.New("journal: malformed file")

	errNotImplemented = errors.New("not implemented")
)

// Header holds the parts of a journal file's header the reader keeps.
type Header struct {
	FileID, MachineID, SeqnumID [16]byte
	Online, SetAside            bool
	HeadSeqnum, TailSeqnum      uint64
	HeadRealtime, TailRealtime  time.Time
}

// File is one open journal file.
type File struct{}

// Data is a data object found by Lookup: its offset and entry count.
type Data struct{}

// Cursor is a position in the entry list of a data object.
type Cursor struct{}

// Entry is one decoded journal entry, reused between calls of Next.
type Entry struct {
	Seqnum    uint64
	Realtime  time.Time
	Monotonic time.Duration
	BootID    [16]byte
	Items     int
	Fields    []Field // only the names in want, values read up to 4,096 bytes into reused buffers
}

// Field is one field of an entry.
type Field struct {
	Name       string
	Value      []byte
	Compressed bool
}

// Open reads the header of the journal file r of the given size; setAside marks a *.journal~ file.
func Open(r io.ReaderAt, size int64, setAside bool) (*File, error) {
	return nil, errNotImplemented
}

// Header returns the header read by Open or Refresh.
func (f *File) Header() Header {
	return Header{}
}

// Refresh re-reads the header after the file grew to size.
func (f *File) Refresh(size int64) error {
	return errNotImplemented
}

// Lookup finds the data object with the given payload through the data hash table.
func (f *File) Lookup(payload []byte) (Data, bool, error) {
	return Data{}, false, errNotImplemented
}

// Seek positions a cursor on the first entry of d with a seqnum greater than after.
func (f *File) Seek(d Data, after uint64) (Cursor, error) {
	return Cursor{}, errNotImplemented
}

// Next reads the entry at c into e, keeping only the fields named in want, and advances c; false means the list ended.
func (f *File) Next(c *Cursor, want []string, e *Entry) (bool, error) {
	return false, errNotImplemented
}

// Field returns the fields of e with the given name.
func (e *Entry) Field(name string) ([]Field, bool) {
	return nil, false
}

// ListFiles returns the journal files in dir: system.journal, system@*.journal and system@*.journal~.
func ListFiles(dir fs.FS) ([]string, error) {
	return nil, errNotImplemented
}

// MachineDir returns the machine ID directory name read from /etc/machine-id in etc.
func MachineDir(etc fs.FS) (string, error) {
	return "", errNotImplemented
}
