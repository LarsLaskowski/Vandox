package connstats

import (
	"context"
	"errors"
	"io/fs"
)

// Fdinfo holds the values of the first four lines of a descriptor's fdinfo file that the collector uses.
type Fdinfo struct {
	MountID int32
	Inode   uint64
}

// ParseFdinfo parses the first four lines of an fdinfo file (pos, flags, mnt_id, ino).
func ParseFdinfo(data []byte) (Fdinfo, error) {
	return Fdinfo{}, errors.New("not implemented")
}

// owners holds, per tracked socket, its owner PID (0: none) and the last PID that counted it, and per PID its distinct sockets.
type owners struct {
	owner, last []int32
	perPID      map[int32]uint32
}

// scanOwners reads the fdinfo files of pids and assigns each socket of index to the lowest PID that holds it on mount.
func scanOwners(ctx context.Context, proc fs.FS, pids []int32, index map[uint64]int32, mount int32) (o owners, unreadable int, firstErr error) {
	return owners{}, 0, errors.New("not implemented")
}
