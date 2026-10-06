package importer

import (
	"archive/tar"
	"context"
	"errors"
	"io"
)

// eachEntry reads the tar stream r and calls fn for every header Next returns, with its ordinal and content;
// a header returned together with tar.ErrInsecurePath is a normal header. It checks ctx between entries and
// returns the first error of the reader or of fn, without reading r any further after fn returned an error.
// It adds no read-ahead buffer of its own (the caller's gzip reader is the only buffering layer).
func eachEntry(ctx context.Context, r io.Reader, fn func(index int, h *tar.Header, content io.Reader) error) error {
	return errors.New("not implemented")
}
