package sysstats

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math/bits"
	"slices"
	"sync"
	"syscall"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// FSStats is what statfs reports about a file system; the counts are in units of BlockSize.
type FSStats struct {
	BlockSize       uint64
	Blocks          uint64
	BlocksFree      uint64
	BlocksAvailable uint64
	Inodes          uint64
	InodesFree      uint64
}

// Errors of the statfs values.
var (
	errNegativeBlockSize = errors.New("negative block size")
	errFreeAboveTotal    = errors.New("free above total")
	errSizeAbove64Bits   = errors.New("size above 64 bits")
)

// StatFS returns the statistics of the file system that holds path; its error is the bare errno, without the path.
func StatFS(path string) (FSStats, error) {
	var st syscall.Statfs_t
	if err := syscall.Statfs(path, &st); err != nil {
		return FSStats{}, err
	}
	size := st.Frsize
	if size == 0 {
		size = st.Bsize
	}
	if size < 0 {
		return FSStats{}, errNegativeBlockSize
	}
	return FSStats{
		BlockSize:       uint64(size),
		Blocks:          st.Blocks,
		BlocksFree:      st.Bfree,
		BlocksAvailable: st.Bavail,
		Inodes:          st.Files,
		InodesFree:      st.Ffree,
	}, nil
}

// Filesystem collects the use of the configured mount points; it implements scheduler.Collector.
type Filesystem struct {
	src         Sources
	mountPoints []string
	log         *slog.Logger

	mu         sync.Mutex
	notMounted map[string]bool // the paths last logged as not mounted
}

// NewFilesystem returns the file system collector for mountPoints, which it copies; a nil log logs nothing.
func NewFilesystem(src Sources, mountPoints []string, log *slog.Logger) *Filesystem {
	return &Filesystem{src: src.withDefaults(), mountPoints: slices.Clone(mountPoints), log: logger(log)}
}

// Name returns FilesystemName.
func (c *Filesystem) Name() string {
	return FilesystemName
}

// Collect takes one sample of the configured mount points that are mounted.
func (c *Filesystem) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	c.mu.Lock()
	logged := c.notMounted
	c.mu.Unlock()

	mounted, err := readMounted(c.src.Proc, c.mountPoints)
	if err != nil {
		return nil, err
	}
	r := &recorder{run: run}
	notMounted := make(map[string]bool)
	for i, path := range c.mountPoints {
		if !mounted[path] {
			notMounted[path] = true
			continue
		}
		st, err := c.src.StatFS(path)
		if err == nil {
			err = addUsage(r, path, st)
		}
		if err != nil {
			return nil, fmt.Errorf("sysstats: filesystem.mount_points[%d]: statfs: %w", i, err)
		}
	}

	c.mu.Lock()
	c.notMounted = notMounted
	c.mu.Unlock()
	c.logMounts(logged, notMounted)
	return r.records, nil
}

// logMounts logs the paths that stopped or started being mounted since the last sample, in configuration order.
func (c *Filesystem) logMounts(was, now map[string]bool) {
	for _, path := range c.mountPoints {
		switch {
		case now[path] && !was[path]:
			c.log.Warn("mount point not mounted", "collector", FilesystemName, "mount", path)
		case was[path] && !now[path]:
			c.log.Info("mount point mounted", "collector", FilesystemName, "mount", path)
		}
	}
}

// addUsage checks the statistics of the mount point path and adds its records: size, used, free and available bytes and,
// when the file system has a fixed inode count, the inode values.
func addUsage(r *recorder, path string, st FSStats) error {
	if st.BlocksFree > st.Blocks || st.BlocksAvailable > st.Blocks || st.InodesFree > st.Inodes {
		return errFreeAboveTotal
	}
	high, size := bits.Mul64(st.Blocks, st.BlockSize)
	if high != 0 {
		return errSizeAbove64Bits
	}
	free := st.BlocksFree * st.BlockSize
	r.add("filesystem_size_bytes", float64(size), "bytes", "mount", path)
	r.add("filesystem_used_bytes", float64(size-free), "bytes", "mount", path)
	r.add("filesystem_free_bytes", float64(free), "bytes", "mount", path)
	r.add("filesystem_available_bytes", float64(st.BlocksAvailable*st.BlockSize), "bytes", "mount", path)
	if st.Inodes == 0 {
		return nil
	}
	r.add("filesystem_inodes", float64(st.Inodes), "", "mount", path)
	r.add("filesystem_inodes_used", float64(st.Inodes-st.InodesFree), "", "mount", path)
	r.add("filesystem_inodes_free", float64(st.InodesFree), "", "mount", path)
	return nil
}
