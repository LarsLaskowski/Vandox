// Package journaltest writes journal files in the server's layout and reads journal export text; tests only.
package journaltest

import (
	"errors"
	"io"
)

var errNotImplemented = errors.New("not implemented")

// Options configures Write.
type Options struct {
	FileID, MachineID, SeqnumID [16]byte
	Online                      bool
}

// ReadExport parses journal export text into entries.
func ReadExport(r io.Reader) ([]map[string][]byte, error) {
	return nil, errNotImplemented
}

// Write writes entries as a journal file to w.
func Write(w io.Writer, entries []map[string][]byte, opts Options) error {
	return errNotImplemented
}
