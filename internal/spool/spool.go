// Package spool is the agent's on-disk spool of records waiting to be sent.
package spool

import (
	"context"
	"errors"
	"log/slog"
	"os"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// MaxAckRanges is the largest number of acknowledged ranges the spool keeps.
const MaxAckRanges = 64

// maxFiles is the largest number of segment files the spool lists.
const maxFiles = 1024

// ErrTooManyRanges reports that more than MaxAckRanges acknowledged ranges would be held.
var ErrTooManyRanges = errors.New("spool: more than 64 acknowledged ranges")

// errNotImplemented is returned by every stub until the spool is implemented.
var errNotImplemented = errors.New("not implemented")

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

// Spool is the on-disk queue of records.
type Spool struct {
	opts Options
	log  *slog.Logger
	// limit is the largest number of segment files listed.
	limit int
	// ext is the extension of segment files.
	ext string
}

// Open opens the spool in opts.Dir and recovers its state.
func Open(opts Options, log *slog.Logger) (*Spool, error) {
	_ = &Spool{opts: opts, log: log, limit: maxFiles, ext: segmentExt}

	return nil, errNotImplemented
}

// Put appends records to the spool.
func (s *Spool) Put(_ context.Context, _ []model.Record) error {
	return errNotImplemented
}

// Read returns the records after the sequence number after, bounded by maxRecords and maxBytes.
func (s *Spool) Read(_ context.Context, _ uint64, _ int, _ int64) (Batch, error) {
	return Batch{}, errNotImplemented
}

// Ack acknowledges the sequence numbers first to last.
func (s *Spool) Ack(_ context.Context, _, _ uint64) error {
	return errNotImplemented
}

// LastSeq returns the sequence number of the newest record.
func (s *Spool) LastSeq() uint64 {
	return 0
}

// Sync flushes the spool's files to stable storage.
func (s *Spool) Sync(_ context.Context) error {
	return errNotImplemented
}

// Close syncs and closes the spool.
func (s *Spool) Close(_ context.Context) error {
	return errNotImplemented
}

// SpoolBytes returns the size of all spool files.
func (s *Spool) SpoolBytes(_ context.Context) (int64, error) {
	return 0, errNotImplemented
}

// OldestCapture returns the capture time of the oldest unacknowledged record; the bool is false when there is none.
func (s *Spool) OldestCapture(_ context.Context) (time.Time, bool, error) {
	return time.Time{}, false, errNotImplemented
}
