// Package spool is the agent's on-disk spool of records waiting to be sent.
package spool

import (
	"context"
	"errors"
	"log/slog"
	"math"
	"math/bits"
	"os"
	"sync"
	"syscall"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// MaxAckRanges is the largest number of acknowledged ranges the spool keeps.
const MaxAckRanges = 64

// maxFiles is the largest number of segment files the spool lists.
const maxFiles = 1024

const (
	// minFileBytes and maxFileBytes bound the size at which a data file is sealed.
	minFileBytes = 1 << 20
	maxFileBytes = 64 << 20
	// fileDivisor divides spool.max_size into the size of a data file.
	fileDivisor = 128
	// defaultSyncInterval is the interval between syncs when none is configured.
	defaultSyncInterval = time.Second
)

// ErrTooManyRanges reports that more than MaxAckRanges acknowledged ranges would be held.
var ErrTooManyRanges = errors.New("spool: more than 64 acknowledged ranges")

var (
	errClosed    = errors.New("spool: closed")
	errExhausted = errors.New("spool: sequence numbers exhausted")
	errNoRoom    = errors.New("spool: no room for the records")
)

// writeFile is the seam the tests replace to make writes fail or stop short.
var writeFile = func(f *os.File, b []byte, off int64) (int, error) {
	return f.WriteAt(b, off)
}

// Options configures a Spool.
type Options struct {
	// Dir is the spool directory.
	Dir string
	// AgentID identifies the agent in every batch.
	AgentID string
	// BootID identifies the current boot of the monitored server.
	BootID string
	// MaxBytes is the size limit of all spool files.
	MaxBytes int64
	// MinFreeBytes is the free space that must stay available on the file system.
	MinFreeBytes int64
	// SyncInterval is the interval between syncs; 0 means one second.
	SyncInterval time.Duration
	// Now returns the current time; nil means time.Now.
	Now func() time.Time
	// Available returns the bytes available in a directory; nil means statfs f_bavail times f_bsize.
	Available func(dir string) (uint64, error)
}

// Batch is a run of spooled records with the identity it is sent under.
type Batch struct {
	// AgentID identifies the agent.
	AgentID string
	// BootID identifies the boot the records were captured in.
	BootID string
	// ClockOffsetNS is the clock offset in nanoseconds, if known.
	ClockOffsetNS *int64
	// Records are the records of the batch.
	Records []model.Record
}

// pendingGap is a gap record the spool owes the data: it is written with the next hand-over.
type pendingGap struct {
	first, last uint64
	from, to    time.Time
	cause       model.GapCause
	// drop marks a gap made by dropping records in the current hand-over, which a later drop may extend.
	drop bool
}

// refusedStats are the capture times of the records refused in a write failure episode.
type refusedStats struct {
	count            int
	earliest, latest time.Time
}

// Spool is the on-disk queue of records.
type Spool struct {
	opts Options
	log  *slog.Logger
	dir  *dirFS
	// ident is the capture context of the records written by this process; idents are the contexts found at Open.
	ident   identity
	idents  []*identity
	fileMax int64

	mu sync.Mutex
	// files are the usable data files, oldest first; act is the one being appended to, if any.
	files []*segFile
	act   *segFile
	actF  *os.File
	// next is the number of the next record.
	next  uint64
	acked []ackRange
	// pend are gaps waiting for the next hand-over; putPend is where the drops of the current hand-over start.
	pend    []pendingGap
	putPend int
	// failing is set during a write failure episode; refused holds what the episode refused.
	failing bool
	refused refusedStats
	// dropping is set during a drop episode; droppedTotal counts its records.
	dropping     bool
	droppedTotal int64
	// healTail is set when the active file could not be cut back to its last good frame.
	healTail bool

	metaF    *os.File
	metaSize int64
	metaNeed bool
	dirNeed  bool

	// gen counts the writes; syncedGen is the count a finished sync covered.
	gen, syncedGen uint64
	run            *syncRun
	stop           context.CancelFunc
	loopDone       chan struct{}
	closing        bool
	closed         bool

	reader  frameReader
	cursors [2]cursor
	indexes [2]*fileIndex
	stamp   uint64
	oldest  oldestCache
}

// now returns the current time.
func (s *Spool) now() time.Time {
	if s.opts.Now != nil {
		return s.opts.Now()
	}

	return time.Now()
}

// LastSeq returns the sequence number of the newest record.
func (s *Spool) LastSeq() uint64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.next == 0 {
		return 0
	}

	return s.next - 1
}

// SpoolBytes returns the size of all spool files.
func (s *Spool) SpoolBytes(ctx context.Context) (int64, error) {
	if err := ctx.Err(); err != nil {
		return 0, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.diskUsed() + s.metaSize, nil
}

// diskUsed returns the size of the data files.
func (s *Spool) diskUsed() int64 {
	var sum int64
	for _, f := range s.files {
		sum += f.size
	}

	return sum
}

// available returns the bytes available to the spool on its file system.
func (s *Spool) available() (uint64, error) {
	if s.opts.Available != nil {
		return s.opts.Available(s.opts.Dir)
	}

	return statAvailable(s.opts.Dir)
}

// statAvailable returns f_bavail times f_bsize of the file system holding dir.
func statAvailable(dir string) (uint64, error) {
	var st syscall.Statfs_t
	if err := syscall.Statfs(dir, &st); err != nil {
		return 0, err
	}
	if st.Bsize <= 0 {
		return 0, nil
	}
	hi, lo := bits.Mul64(st.Bavail, uint64(st.Bsize))
	if hi != 0 {
		return math.MaxUint64, nil
	}

	return lo, nil
}

// fitsDisk reports whether a write of need bytes passes the free-space check: the space is at least the write and what
// remains is at least the reserve, compared without overflow.
func fitsDisk(avail uint64, need, reserve int64) bool {
	if need < 0 || reserve < 0 {
		return false
	}
	n := uint64(need)

	return avail >= n && avail-n >= uint64(reserve)
}
