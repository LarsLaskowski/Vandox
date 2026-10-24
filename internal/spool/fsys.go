package spool

import (
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"syscall"
)

// Damage reasons: the fixed texts of the err attribute of "spool data damaged".
var (
	errDamageChecksum     = errors.New("checksum mismatch")
	errDamageCutShort     = errors.New("file cut short")
	errDamageNotFormat    = errors.New("not the spool format")
	errDamageVersion      = errors.New("unknown format version")
	errDamageInconsistent = errors.New("inconsistent value")
	errDamageRefused      = errors.New("refused entry")
	errDamageTooMany      = errors.New("too many files")
	// errAbsent reports that a name does not exist in the directory.
	errAbsent = errors.New("absent entry")
)

const (
	// fileLabel names every os.File the spool makes, so no error text carries a path.
	fileLabel = "spool"
	// dirErrPrefix starts the errors of the directory checks.
	dirErrPrefix = "spool: directory: "
	// readErrPrefix starts the errors of reading a spool file.
	readErrPrefix = "spool: read: "
	// openFlags are the flags every open below the directory carries.
	openFlags = syscall.O_NOFOLLOW | syscall.O_NONBLOCK | syscall.O_CLOEXEC
	// dirChunk is the number of names read from the directory at a time.
	dirChunk = 256
	// fileMode is the mode of the files the spool creates.
	fileMode = 0o600
	// atFDCWD makes openat resolve a path against the working directory (linux AT_FDCWD).
	atFDCWD = -100
	// groupOtherBits are the permission bits no spool file may carry.
	groupOtherBits = 0o077
)

// fileID names an inode.
type fileID struct {
	dev, ino uint64
}

// isDamage reports whether err is one of the damage reasons.
func isDamage(err error) bool {
	for _, reason := range []error{errDamageChecksum, errDamageCutShort, errDamageNotFormat, errDamageVersion, errDamageInconsistent, errDamageRefused, errDamageTooMany} {
		if errors.Is(err, reason) {
			return true
		}
	}

	return false
}

// errText returns the fixed text of an error: the system error text for an errno, never a path.
func errText(err error) string {
	var errno syscall.Errno
	if errors.As(err, &errno) {
		return errno.Error()
	}
	var pathErr *os.PathError
	if errors.As(err, &pathErr) {
		return errText(pathErr.Err)
	}
	var linkErr *os.LinkError
	if errors.As(err, &linkErr) {
		return errText(linkErr.Err)
	}

	return err.Error()
}

// retry runs fn again while it is interrupted.
func retry(fn func() error) error {
	for {
		err := fn()
		if !errors.Is(err, syscall.EINTR) {
			return err
		}
	}
}

// openat opens name below dirfd.
func openat(dirfd int, name string, flags int, mode uint32) (int, error) {
	var fd int
	err := retry(func() error {
		var openErr error
		fd, openErr = syscall.Openat(dirfd, name, flags, mode)

		return openErr
	})

	return fd, err
}

// newFile wraps a descriptor that passed its checks in an os.File without a path.
func newFile(fd int) *os.File {
	if fd < 0 {
		return nil
	}

	return os.NewFile(uintptr(fd), fileLabel)
}

// effectiveUID returns the effective user ID of the process.
func effectiveUID() uint32 {
	uid := syscall.Geteuid()
	if uid < 0 || uid > math.MaxUint32 {
		return math.MaxUint32
	}

	return uint32(uid)
}

// dirFS is the spool directory: its descriptor and the operations on plain names below it. Every open, create, remove
// and rename goes through the descriptor, never through a path, and no link is followed.
type dirFS struct {
	fd  int
	uid uint32
}

// openDir checks the directory path and opens it.
func openDir(path string) (*dirFS, error) {
	if !filepath.IsAbs(path) || filepath.Clean(path) != path {
		return nil, errors.New(dirErrPrefix + "not an absolute, clean path")
	}
	info, err := os.Lstat(path)
	if err != nil {
		return nil, errors.New(dirErrPrefix + errText(err))
	}
	if info.Mode()&os.ModeSymlink != 0 {
		return nil, errors.New(dirErrPrefix + "symbolic link")
	}
	if !info.IsDir() {
		return nil, errors.New(dirErrPrefix + "not a directory")
	}
	fd, err := openat(atFDCWD, path, syscall.O_RDONLY|syscall.O_DIRECTORY|syscall.O_NOFOLLOW|syscall.O_CLOEXEC, 0)
	if errors.Is(err, syscall.ELOOP) || errors.Is(err, syscall.ENOTDIR) {
		return nil, errors.New(dirErrPrefix + "symbolic link")
	}
	if err != nil {
		return nil, errors.New(dirErrPrefix + errText(err))
	}

	return &dirFS{fd: fd, uid: effectiveUID()}, nil
}

// trusted reports whether the descriptor's file is a regular file of the effective user without group or other
// permission bits and with exactly one link.
func (d *dirFS) trusted(st *syscall.Stat_t) bool {
	return st.Mode&syscall.S_IFMT == syscall.S_IFREG && st.Uid == d.uid && st.Mode&groupOtherBits == 0 && st.Nlink == 1
}

// openEntry opens name without following a link and trusts it only after Fstat on the descriptor. The entry is opened
// read-only unless write is set; a refused entry is never opened for writing by the caller's request, because the
// caller opens read-only first.
func (d *dirFS) openEntry(name string, write bool) (*os.File, *syscall.Stat_t, error) {
	flags := openFlags | syscall.O_RDONLY
	if write {
		flags = openFlags | syscall.O_RDWR
	}
	fd, err := openat(d.fd, name, flags, 0)
	switch {
	case errors.Is(err, syscall.ENOENT):
		return nil, nil, errAbsent
	case errors.Is(err, syscall.ELOOP), errors.Is(err, syscall.ENXIO), errors.Is(err, syscall.EACCES),
		errors.Is(err, syscall.EPERM), errors.Is(err, syscall.EISDIR), errors.Is(err, syscall.ENOTDIR):
		return nil, nil, errDamageRefused
	case err != nil:
		return nil, nil, errors.New(readErrPrefix + errText(err))
	}
	var st syscall.Stat_t
	if err := retry(func() error { return syscall.Fstat(fd, &st) }); err != nil {
		_ = syscall.Close(fd)

		return nil, nil, errors.New(readErrPrefix + errText(err))
	}
	if !d.trusted(&st) {
		_ = syscall.Close(fd)

		return nil, nil, errDamageRefused
	}
	if err := syscall.SetNonblock(fd, false); err != nil {
		_ = syscall.Close(fd)

		return nil, nil, errors.New(readErrPrefix + errText(err))
	}

	return newFile(fd), &st, nil
}

// create makes the file name, which must not exist, with mode 0600.
func (d *dirFS) create(name string) (*os.File, fileID, error) {
	fd, err := openat(d.fd, name, syscall.O_RDWR|syscall.O_CREAT|syscall.O_EXCL|syscall.O_NOFOLLOW|syscall.O_CLOEXEC, fileMode)
	if err != nil {
		return nil, fileID{}, err
	}
	var st syscall.Stat_t
	if err := retry(func() error { return syscall.Fstat(fd, &st) }); err != nil {
		_ = syscall.Close(fd)

		return nil, fileID{}, err
	}
	if !d.trusted(&st) {
		_ = syscall.Close(fd)

		return nil, fileID{}, errDamageRefused
	}

	return newFile(fd), fileID{dev: st.Dev, ino: st.Ino}, nil
}

// removeTrusted unlinks name when it is absent or a trusted file; with want set, only the file with that identity.
func (d *dirFS) removeTrusted(name string, want *fileID) error {
	f, st, err := d.openEntry(name, false)
	if errors.Is(err, errAbsent) {
		return nil
	}
	if err != nil {
		return err
	}
	_ = f.Close()
	if want != nil && (want.dev != st.Dev || want.ino != st.Ino) {
		return errDamageRefused
	}

	return retry(func() error { return syscall.Unlinkat(d.fd, name) })
}

// rename moves the plain name from to the plain name to.
func (d *dirFS) rename(from, to string) error {
	return retry(func() error { return syscall.Renameat(d.fd, from, d.fd, to) })
}

// sync flushes the directory.
func (d *dirFS) sync() error {
	return retry(func() error { return syscall.Fsync(d.fd) })
}

// close releases the directory descriptor.
func (d *dirFS) close() {
	_ = syscall.Close(d.fd)
}

// list calls visit for every name in the directory, reading dirChunk names at a time through a second descriptor.
func (d *dirFS) list(visit func(name string)) error {
	fd, err := openat(d.fd, ".", syscall.O_RDONLY|syscall.O_DIRECTORY|syscall.O_NOFOLLOW|syscall.O_CLOEXEC, 0)
	if err != nil {
		return errors.New(readErrPrefix + errText(err))
	}
	f := newFile(fd)
	defer func() { _ = f.Close() }()
	for {
		names, err := f.Readdirnames(dirChunk)
		for _, name := range names {
			visit(name)
		}
		if errors.Is(err, io.EOF) {
			return nil
		}
		if err != nil {
			return errors.New(readErrPrefix + errText(err))
		}
	}
}

// writeAll writes b at off through the write seam.
func writeAll(f *os.File, b []byte, off int64) error {
	n, err := writeFile(f, b, off)
	if err != nil {
		return err
	}
	if n != len(b) {
		return io.ErrShortWrite
	}

	return nil
}

// writeError wraps a failed write.
func writeError(err error) error {
	return fmt.Errorf("spool: write: %s", errText(err))
}
