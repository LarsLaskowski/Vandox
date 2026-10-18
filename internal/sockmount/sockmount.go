// Package sockmount learns the mount that holds the kernel's sockets from a socket of the agent's own.
package sockmount

import (
	"errors"
	"fmt"
	"io"
	"io/fs"
	"strconv"
	"syscall"

	"github.com/LarsLaskowski/Vandox/internal/connstats"
)

// maxFdinfo is the most bytes of the fdinfo file that are read.
const maxFdinfo = 256

// Probe opens one unbound AF_UNIX datagram socket, reads its self/fdinfo/<fd> through proc, closes both and returns its mount ID.
func Probe(proc fs.FS) (int32, error) {
	fd, err := syscall.Socket(syscall.AF_UNIX, syscall.SOCK_DGRAM|syscall.SOCK_CLOEXEC, 0)
	if err != nil {
		return 0, fmt.Errorf("sockmount: socket: %w", err)
	}
	// Nothing was sent on the socket, so a failing close loses no data and has no consequence to report.
	defer func() { _ = syscall.Close(fd) }()

	data, err := readFdinfo(proc, fd)
	if err != nil {
		return 0, fmt.Errorf("sockmount: self/fdinfo: %w", err)
	}
	info, err := connstats.ParseFdinfo(data)
	if err != nil {
		return 0, fmt.Errorf("sockmount: %w", err)
	}
	var st syscall.Stat_t
	if syscall.Fstat(fd, &st) != nil || st.Ino != info.Inode {
		return 0, errors.New("sockmount: self/fdinfo: inode mismatch")
	}
	return info.MountID, nil
}

// readFdinfo reads at most maxFdinfo bytes of self/fdinfo/<fd>.
func readFdinfo(proc fs.FS, fd int) ([]byte, error) {
	f, err := proc.Open("self/fdinfo/" + strconv.Itoa(fd))
	if err != nil {
		return nil, cause(err)
	}
	defer func() { _ = f.Close() }()

	buf := make([]byte, maxFdinfo)
	n, err := io.ReadFull(f, buf)
	if err != nil && !errors.Is(err, io.EOF) && !errors.Is(err, io.ErrUnexpectedEOF) {
		return nil, cause(err)
	}
	return buf[:n], nil
}

// cause strips the path of a path error, which the message already names.
func cause(err error) error {
	var pe *fs.PathError
	if errors.As(err, &pe) {
		return pe.Err
	}
	return err
}
