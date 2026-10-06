package importer

import (
	"context"
	"errors"
	"os"

	"github.com/LarsLaskowski/Vandox/internal/logparse"
)

// source is the opened import root.
type source struct {
	root *os.Root // the root directory, or the directory holding a root file
	file string   // the root file's name in root; "" when the root is a directory
}

// openSource resolves path once with filepath.EvalSymlinks, checks it with os.Lstat and opens it: a directory as
// the root, a regular file through the directory holding it. Anything else is an error, and nothing is opened
// before Lstat reported a directory or a regular file. The caller closes source.root.
func openSource(path string) (source, error) {
	return source{}, errors.New("not implemented")
}

// location tells where the content of a found file is read again.
type location struct {
	fsPath      string // the file, or the archive containing the entry; a name in source.root
	entry       int    // ordinal of the entry among the archive's headers; -1 for a plain file
	archiveGzip bool   // the archive is gzip-compressed
	gzip        bool   // the file or entry content is gzip-compressed
}

// found is one file of pass 1.
type found struct {
	result FileResult // Path; Outcome and Reason when it is not imported
	file   logparse.File
	loc    location
	parser logparse.Parser // nil when not to be imported
	sum    [32]byte
	size   int64
}

// scan walks src (pass 1), lists every file and hashes the recognized ones, detecting with opts.Parsers and
// reporting EventFileFinished and EventScanProgress (every opts.ProgressBytes) to opts.Progress. It returns an
// error wrapping ErrTooManyFiles as soon as entry MaxFiles+1 is counted, without reading further.
func scan(ctx context.Context, src source, opts Options) ([]found, error) {
	return nil, errors.New("not implemented")
}
