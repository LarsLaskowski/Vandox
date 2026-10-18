// Package sockmount learns the mount that holds the kernel's sockets from a socket of the agent's own.
package sockmount

import (
	"errors"
	"io/fs"
)

// Probe opens one unbound AF_UNIX datagram socket, reads its self/fdinfo/<fd> through proc, closes both and returns its mount ID.
func Probe(proc fs.FS) (int32, error) {
	return 0, errors.New("not implemented")
}
