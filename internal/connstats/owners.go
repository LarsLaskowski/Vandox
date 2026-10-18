package connstats

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"strconv"

	"github.com/LarsLaskowski/Vandox/internal/procstats"
)

// Bounds of the descriptor scan.
const (
	maxFdinfo    = 256 // bytes read of one fdinfo file
	dirChunk     = 256 // entries read per ReadDir call
	maxMountDig  = 10  // digits of a mount ID
	fdinfoLines  = 4
	prefixPos    = "pos:\t"
	prefixFlags  = "flags:\t"
	prefixMount  = "mnt_id:\t"
	prefixInode  = "ino:\t"
	errPrefixFmt = "connstats: %d/fdinfo: %w"
)

// Fdinfo holds the values of the first four lines of a descriptor's fdinfo file that the collector uses.
type Fdinfo struct {
	MountID int32
	Inode   uint64
}

// ParseFdinfo parses the first four lines of an fdinfo file (pos, flags, mnt_id, ino); later lines are not read. Its errors name
// a fixed reason and never hold file content.
func ParseFdinfo(data []byte) (Fdinfo, error) {
	var vals [fdinfoLines][]byte
	prefixes := [fdinfoLines]string{prefixPos, prefixFlags, prefixMount, prefixInode}
	rest := data
	for i, prefix := range prefixes {
		end := bytes.IndexByte(rest, '\n')
		if end < 0 {
			return Fdinfo{}, errFormat
		}
		val, ok := bytes.CutPrefix(rest[:end], []byte(prefix))
		if !ok || !allDigits(val) {
			return Fdinfo{}, errFormat
		}
		vals[i] = val
		rest = rest[end+1:]
	}
	if len(vals[2]) > maxMountDig {
		return Fdinfo{}, errFormat
	}
	mount, err := strconv.ParseInt(string(vals[2]), decimalBase, 32)
	if err != nil || mount == 0 {
		return Fdinfo{}, errFormat
	}
	inode, err := parseInode(vals[3])
	if err != nil {
		return Fdinfo{}, err
	}
	return Fdinfo{MountID: int32(mount), Inode: inode}, nil
}

// owners holds, per tracked socket, its owner PID (0: none) and the last PID that counted it, and per PID its distinct sockets.
type owners struct {
	owner, last []int32
	perPID      map[int32]uint32
}

// scanState is the progress of one scan.
type scanState struct {
	proc       fs.FS
	index      map[uint64]int32
	mount      int32
	o          owners
	unreadable int
	firstErr   error
	buf        [maxFdinfo]byte
}

// scanOwners reads the fdinfo files of pids and assigns each socket of index to the lowest PID that holds it on mount. index maps
// an inode to a slot 0..len(index)-1; owners are indexed by slot. A PID that cannot be read completely is counted once in
// unreadable and its scan stops; firstErr is the first such failure. A canceled ctx stops the scan; the caller reads ctx.Err().
func scanOwners(ctx context.Context, proc fs.FS, pids []int32, index map[uint64]int32, mount int32) (o owners, unreadable int, firstErr error) {
	s := &scanState{
		proc:  proc,
		index: index,
		mount: mount,
		o:     owners{owner: make([]int32, len(index)), last: make([]int32, len(index)), perPID: make(map[int32]uint32)},
	}
	for _, pid := range pids {
		if ctx.Err() != nil {
			break
		}
		if err := s.scanPID(ctx, pid); err != nil {
			s.unreadable++
			if s.firstErr == nil {
				s.firstErr = fmt.Errorf(errPrefixFmt, pid, err)
			}
		}
	}
	return s.o, s.unreadable, s.firstErr
}

// scanPID reads all descriptors of pid. It returns nil for an ended process and the reason when the process is unreadable.
func (s *scanState) scanPID(ctx context.Context, pid int32) error {
	dirName := strconv.Itoa(int(pid)) + "/fdinfo"
	dir, err := s.proc.Open(dirName)
	if err != nil {
		return skipEnded(err)
	}
	defer func() { _ = dir.Close() }()
	rd, ok := dir.(fs.ReadDirFile)
	if !ok {
		return errFormat
	}
	for ctx.Err() == nil {
		entries, err := rd.ReadDir(dirChunk)
		for _, e := range entries {
			if ferr := s.scanFile(pid, dirName+"/"+e.Name()); ferr != nil {
				return ferr
			}
		}
		if errors.Is(err, io.EOF) {
			return nil
		}
		if err != nil {
			return skipEnded(err)
		}
	}
	return nil
}

// skipEnded turns the failure of an ended process into nil and any other into its cause.
func skipEnded(err error) error {
	if procstats.Ended(err) {
		return nil
	}
	return cause(err)
}

// scanFile reads one fdinfo file and counts its socket; a file closed meanwhile is skipped.
func (s *scanState) scanFile(pid int32, name string) error {
	f, err := s.proc.Open(name)
	if err != nil {
		return skipEnded(err)
	}
	n, err := io.ReadFull(f, s.buf[:])
	_ = f.Close()
	if err != nil && !errors.Is(err, io.EOF) && !errors.Is(err, io.ErrUnexpectedEOF) {
		return cause(err)
	}
	info, err := ParseFdinfo(s.buf[:n])
	if err != nil {
		return err
	}
	if info.MountID != s.mount {
		return nil
	}
	slot, ok := s.index[info.Inode]
	if !ok || slot < 0 || int(slot) >= len(s.o.owner) || s.o.last[slot] == pid {
		return nil
	}
	s.o.last[slot] = pid
	if s.o.owner[slot] == 0 {
		s.o.owner[slot] = pid
	}
	s.o.perPID[pid]++
	return nil
}
