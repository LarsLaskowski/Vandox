package logship

import (
	"errors"
	"io/fs"
	"os"
	"strings"
	"syscall"
)

const (
	// oPath is the O_PATH flag of Linux, which the standard library does not name: the descriptor only names the directory, so that a
	// directory without read permission can still be passed.
	oPath = 0x200000

	// maxDirEntries bounds the names read from the directory of a log file when its rotated file is looked for.
	maxDirEntries = 1 << 16
	dirChunk      = 256
)

// The fixed reasons a configured file is not readable; they are logged as they are and never carry a path.
const (
	reasonMissing    = "missing"
	reasonNotRegular = "not a regular file"
	reasonLink       = "link refused"
	reasonDenied     = "permission denied"
	reasonReadFailed = "read failed"
)

// pathError is the error of opening a configured file; its text is one of the fixed reasons.
type pathError struct{ reason string }

func (e *pathError) Error() string { return e.reason }

// reasonOf returns the fixed reason of an error of opening a file.
func reasonOf(err error) string {
	var pe *pathError
	switch {
	case errors.As(err, &pe):
		return pe.reason
	case errors.Is(err, fs.ErrNotExist):
		return reasonMissing
	case errors.Is(err, fs.ErrPermission):
		return reasonDenied
	default:
		return reasonReadFailed
	}
}

// classify turns the error number of an open into a fixed reason.
func classify(err error) *pathError {
	switch {
	case errors.Is(err, syscall.ENOENT), errors.Is(err, syscall.ENOTDIR):
		return &pathError{reasonMissing}
	case errors.Is(err, syscall.ELOOP):
		return &pathError{reasonLink}
	case errors.Is(err, syscall.EACCES), errors.Is(err, syscall.EPERM):
		return &pathError{reasonDenied}
	default:
		return &pathError{reasonReadFailed}
	}
}

// openat opens name below the directory descriptor dirfd, repeating the call when a signal interrupts it.
func openat(dirfd int, name string, flags int) (int, error) {
	for {
		fd, err := syscall.Openat(dirfd, name, flags, 0)
		if !errors.Is(err, syscall.EINTR) {
			return fd, err
		}
	}
}

// splitPath returns the directory elements and the base name of an absolute, clean path.
func splitPath(path string) (dirs []string, base string, err error) {
	if !strings.HasPrefix(path, "/") || path == "/" || strings.HasSuffix(path, "/") {
		return nil, "", &pathError{reasonMissing}
	}
	elems := strings.Split(path[1:], "/")
	for _, e := range elems {
		if e == "" || e == "." || e == ".." {
			return nil, "", &pathError{reasonMissing}
		}
	}
	return elems[:len(elems)-1], elems[len(elems)-1], nil
}

// walkDir opens the directory with the given elements below "/" without following a link in any element; every directory is opened
// with O_PATH, so a directory that can only be searched works. The caller closes the descriptor.
func walkDir(elems []string) (int, error) {
	fd, err := openat(-1, "/", oPath|syscall.O_DIRECTORY|syscall.O_NOFOLLOW|syscall.O_CLOEXEC)
	if err != nil {
		return -1, classify(err)
	}
	for _, e := range elems {
		next, err := openat(fd, e, oPath|syscall.O_DIRECTORY|syscall.O_NOFOLLOW|syscall.O_CLOEXEC)
		if err != nil {
			err = directoryError(fd, e, err)
			_ = syscall.Close(fd)
			return -1, err
		}
		_ = syscall.Close(fd)
		fd = next
	}
	return fd, nil
}

// directoryError explains why the element name of the directory dirfd could not be opened as a directory: a link there is refused,
// anything else that is no directory is missing.
func directoryError(dirfd int, name string, cause error) *pathError {
	if !errors.Is(cause, syscall.ENOTDIR) && !errors.Is(cause, syscall.ELOOP) {
		return classify(cause)
	}
	fd, err := openat(dirfd, name, oPath|syscall.O_NOFOLLOW|syscall.O_CLOEXEC)
	if err != nil {
		return classify(err)
	}
	defer func() { _ = syscall.Close(fd) }()
	var st syscall.Stat_t
	if syscall.Fstat(fd, &st) == nil && st.Mode&syscall.S_IFMT == syscall.S_IFLNK {
		return &pathError{reasonLink}
	}
	return &pathError{reasonMissing}
}

// openRegular opens the regular file name of the directory dirfd for reading without blocking and without following a link; a file
// with more than one link is refused too. It returns the file and its status.
func openRegular(dirfd int, name, label string) (*os.File, syscall.Stat_t, error) {
	var st syscall.Stat_t
	fd, err := openat(dirfd, name, syscall.O_RDONLY|syscall.O_NOFOLLOW|syscall.O_NONBLOCK|syscall.O_CLOEXEC)
	if err != nil {
		return nil, st, classify(err)
	}
	if err := syscall.Fstat(fd, &st); err != nil {
		_ = syscall.Close(fd)
		return nil, st, &pathError{reasonReadFailed}
	}
	if st.Mode&syscall.S_IFMT != syscall.S_IFREG {
		_ = syscall.Close(fd)
		return nil, st, &pathError{reasonNotRegular}
	}
	if st.Nlink != 1 {
		_ = syscall.Close(fd)
		return nil, st, &pathError{reasonLink}
	}
	if err := syscall.SetNonblock(fd, false); err != nil {
		_ = syscall.Close(fd)
		return nil, st, &pathError{reasonReadFailed}
	}
	return os.NewFile(uintptr(fd), label), st, nil // #nosec G115 -- a descriptor is never negative here
}

// OpenNoLinks opens the regular file at path, which must be absolute, through a walk from "/" that refuses a link in any element.
//
// Every directory is opened with openat(O_PATH|O_DIRECTORY|O_NOFOLLOW), the file with O_RDONLY|O_NOFOLLOW|O_NONBLOCK, and the opened
// descriptor must be a regular file with one link only, so a hard link to another file is refused as well. The error text is one of
// the fixed reasons and never holds the path.
func OpenNoLinks(path string) (*os.File, error) {
	dirs, base, err := splitPath(path)
	if err != nil {
		return nil, err
	}
	dirfd, err := walkDir(dirs)
	if err != nil {
		return nil, err
	}
	defer func() { _ = syscall.Close(dirfd) }()
	f, _, err := openRegular(dirfd, base, path)
	if err != nil {
		return nil, err
	}
	return f, nil
}

// identity names a file by device and inode.
type identity struct{ dev, ino uint64 }

// identityOf returns the identity of a file status.
func identityOf(st *syscall.Stat_t) identity {
	return identity{dev: st.Dev, ino: st.Ino}
}

// findRotated looks in the directory of path, reached by the same link-free walk, for the file with the identity want among the
// regular files named <base>.1 and <base>-YYYYMMDD; the file is accepted only when the opened descriptor shows that identity. It
// returns nil when there is no such file.
func findRotated(path string, want identity) (*os.File, error) {
	dirs, base, err := splitPath(path)
	if err != nil {
		return nil, err
	}
	dirfd, err := walkDir(dirs)
	if err != nil {
		return nil, err
	}
	defer func() { _ = syscall.Close(dirfd) }()
	for _, name := range rotatedNames(dirfd, base) {
		f, st, err := openRegular(dirfd, name, name)
		if err != nil {
			continue
		}
		if identityOf(&st) == want {
			return f, nil
		}
		_ = f.Close()
	}
	return nil, nil
}

// rotatedNames returns the names a rotated copy of base may have: base.1 and the names of the directory made of base, a dash and
// eight digits. A directory that cannot be listed gives base.1 only.
func rotatedNames(dirfd int, base string) []string {
	names := []string{base + ".1"}
	fd, err := openat(dirfd, ".", syscall.O_RDONLY|syscall.O_DIRECTORY|syscall.O_CLOEXEC)
	if err != nil {
		return names
	}
	dir := os.NewFile(uintptr(fd), "dir") // #nosec G115 -- a descriptor is never negative here
	defer func() { _ = dir.Close() }()
	for seen := 0; seen < maxDirEntries; {
		chunk, err := dir.Readdirnames(dirChunk)
		for _, n := range chunk {
			if isDatedName(n, base) {
				names = append(names, n)
			}
		}
		seen += len(chunk)
		if err != nil {
			break
		}
	}
	return names
}

// isDatedName tells whether name is base, a dash and eight digits.
func isDatedName(name, base string) bool {
	rest, ok := strings.CutPrefix(name, base+"-")
	if !ok || len(rest) != 8 {
		return false
	}
	for i := 0; i < len(rest); i++ {
		if rest[i] < '0' || rest[i] > '9' {
			return false
		}
	}
	return true
}
