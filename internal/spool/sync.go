package spool

import (
	"context"
	"errors"
	"os"
	"time"
)

// syncRun is the one flush to stable storage that may be in flight.
type syncRun struct {
	done chan struct{}
	err  error
}

// syncErrPrefix starts the errors of a failed flush.
const syncErrPrefix = "spool: sync: "

// startLoop starts the goroutine that syncs the spool at the configured interval.
func (s *Spool) startLoop() {
	ctx, cancel := context.WithCancel(context.Background())
	s.stop = cancel
	s.loopDone = make(chan struct{})
	interval := s.opts.SyncInterval
	if interval <= 0 {
		interval = defaultSyncInterval
	}
	go s.loop(ctx, interval)
}

// loop syncs the spool every interval until ctx ends. A failed sync leaves the data marked as not durable, so the
// next explicit Sync, which the agent state's barrier calls, runs it again and reports the error.
func (s *Spool) loop(ctx context.Context, interval time.Duration) {
	defer close(s.loopDone)
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			if s.dirty() {
				_ = s.Sync(ctx)
			}
		}
	}
}

// dirty reports whether something was written since the last finished sync.
func (s *Spool) dirty() bool {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.gen > s.syncedGen && !s.closed
}

// Sync flushes the spool's files to stable storage: when it returns nil, everything handed over before the call is
// durable. At most one flush runs at a time, outside the mutex; a caller that finds one running waits for it and runs
// the next one if it does not cover the caller's writes. It returns at the context's end, leaving a hung flush behind.
func (s *Spool) Sync(ctx context.Context) error {
	s.mu.Lock()
	target := s.gen
	s.mu.Unlock()

	return s.syncTo(ctx, target)
}

// syncTo waits until a finished flush covers the writes up to generation target.
func (s *Spool) syncTo(ctx context.Context, target uint64) error {
	for {
		s.mu.Lock()
		if s.syncedGen >= target || s.closed {
			s.mu.Unlock()

			return nil
		}
		run := s.run
		if run == nil {
			run = s.startRunLocked()
		}
		s.mu.Unlock()
		select {
		case <-run.done:
			if run.err != nil {
				return run.err
			}
		case <-ctx.Done():
			return ctx.Err()
		}
	}
}

// flushTargets are the files one flush covers.
type flushTargets struct {
	gen    uint64
	active *os.File
	meta   *os.File
	dir    bool
}

// startRunLocked captures what to flush and starts the flush in a goroutine; the mutex is held.
func (s *Spool) startRunLocked() *syncRun {
	t := flushTargets{gen: s.gen, active: s.actF, dir: s.dirNeed}
	if s.metaNeed {
		t.meta = s.metaF
	}
	s.dirNeed, s.metaNeed = false, false
	run := &syncRun{done: make(chan struct{})}
	s.run = run
	go s.flush(run, t)

	return run
}

// flush syncs the targets outside the mutex and records the result.
func (s *Spool) flush(run *syncRun, t flushTargets) {
	err := s.flushFiles(t)
	s.mu.Lock()
	if err == nil {
		s.syncedGen = max(s.syncedGen, t.gen)
	} else {
		s.dirNeed = s.dirNeed || t.dir
		s.metaNeed = s.metaNeed || t.meta != nil
	}
	s.run = nil
	s.mu.Unlock()
	run.err = err
	close(run.done)
}

// flushFiles syncs the active file, the meta file and the directory. A file that was closed meanwhile was synced when
// it was replaced.
func (s *Spool) flushFiles(t flushTargets) error {
	for _, f := range []*os.File{t.active, t.meta} {
		if f == nil {
			continue
		}
		if err := f.Sync(); err != nil && !errors.Is(err, os.ErrClosed) {
			return errors.New(syncErrPrefix + errText(err))
		}
	}
	if t.dir {
		if err := s.dir.sync(); err != nil {
			return errors.New(syncErrPrefix + errText(err))
		}
	}

	return nil
}

// Close syncs and closes the spool.
func (s *Spool) Close(ctx context.Context) error {
	s.mu.Lock()
	if s.closing {
		s.mu.Unlock()

		return nil
	}
	s.closing = true
	s.stop()
	s.mu.Unlock()

	var first error
	select {
	case <-s.loopDone:
	case <-ctx.Done():
		first = ctx.Err()
	}
	if err := s.Sync(ctx); err != nil && first == nil {
		first = err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.writeMeta(true, true); err != nil && first == nil {
		first = err
	}
	s.release()

	return first
}

// release closes every descriptor the spool holds.
func (s *Spool) release() {
	s.closed = true
	for _, f := range []*os.File{s.actF, s.metaF} {
		if f != nil {
			_ = f.Close()
		}
	}
	s.actF, s.metaF, s.act = nil, nil, nil
	s.dir.close()
}
