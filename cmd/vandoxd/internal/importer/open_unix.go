//go:build unix

package importer

import (
	"errors"
	"os"
)

// openRegular opens name in root read-only with O_NONBLOCK and returns an error unless the opened file is
// regular (Fstat on the descriptor). os.Root resolves a symbolic link only inside root. On non-unix systems
// it returns errors.ErrUnsupported.
func openRegular(root *os.Root, name string) (*os.File, error) {
	return nil, errors.New("not implemented")
}

// openDir opens the directory name in root read-only with O_DIRECTORY|O_NONBLOCK, so a FIFO or a file swapped
// in fails without blocking. On non-unix systems it returns errors.ErrUnsupported.
func openDir(root *os.Root, name string) (*os.File, error) {
	return nil, errors.New("not implemented")
}
