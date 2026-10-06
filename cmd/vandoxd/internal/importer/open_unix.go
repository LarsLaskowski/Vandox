//go:build unix

package importer

import (
	"errors"
	"os"
	"syscall"
)

// openRegular opens name in root read-only with O_NONBLOCK and returns an error unless the opened file is
// regular (Fstat on the descriptor). os.Root resolves a symbolic link only inside root. On non-unix systems
// it returns errors.ErrUnsupported.
func openRegular(root *os.Root, name string) (*os.File, error) {
	f, err := root.OpenFile(name, os.O_RDONLY|syscall.O_NONBLOCK, 0)
	if err != nil {
		return nil, err
	}
	info, err := f.Stat()
	if err != nil {
		_ = f.Close()
		return nil, err
	}
	if !info.Mode().IsRegular() {
		_ = f.Close()
		return nil, errors.New("not a regular file")
	}
	return f, nil
}

// openDir opens the directory name in root read-only with O_DIRECTORY|O_NONBLOCK, so a FIFO or a file swapped
// in fails without blocking. On non-unix systems it returns errors.ErrUnsupported.
func openDir(root *os.Root, name string) (*os.File, error) {
	d, err := root.OpenFile(name, os.O_RDONLY|syscall.O_DIRECTORY|syscall.O_NONBLOCK, 0)
	if err != nil {
		return nil, err
	}
	info, err := d.Stat()
	if err != nil {
		_ = d.Close()
		return nil, err
	}
	if !info.IsDir() {
		_ = d.Close()
		return nil, errors.New("not a directory")
	}
	return d, nil
}
