//go:build !unix

package importer

import (
	"errors"
	"os"
)

// openRegular returns errors.ErrUnsupported: opening without blocking needs a unix system.
func openRegular(root *os.Root, name string) (*os.File, error) {
	return nil, errors.ErrUnsupported
}

// openDir returns errors.ErrUnsupported: opening without blocking needs a unix system.
func openDir(root *os.Root, name string) (*os.File, error) {
	return nil, errors.ErrUnsupported
}
