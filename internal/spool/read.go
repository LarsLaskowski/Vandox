package spool

import (
	"context"
	"encoding/binary"
	"errors"
	"io"
	"math"
	"os"
	"sort"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	// indexParts is the number of parts a file is divided into by its sparse index.
	indexParts = 64
	// minIndexStep is the smallest distance between two entries of a sparse index.
	minIndexStep = 4096
	// indexProbe is the number of bytes read to find the number of the frame at an offset.
	indexProbe = frameHeadLen + seqLen
)

// cursor is a read position: a later read after any number from lo to hi continues at off of the file num.
type cursor struct {
	valid  bool
	num    uint64
	lo, hi uint64
	off    int64
	count  int64
	stamp  uint64
}

// idxEntry is the number of a frame and where it starts.
type idxEntry struct {
	seq uint64
	off int64
}

// fileIndex is a sparse index of one data file.
type fileIndex struct {
	num     uint64
	entries []idxEntry
	stamp   uint64
}

// oldestCache remembers the capture time of the oldest unacknowledged record.
type oldestCache struct {
	valid bool
	seq   uint64
	at    time.Time
}

// readJob is one Read in progress.
type readJob struct {
	after      uint64
	maxRecords int
	maxBytes   int64
	bytes      int64
	batch      Batch
	ident      *identity
	full       bool
}

// pass is the state of one pass over the frames of a file.
type pass struct {
	f   *segFile
	off int64
	// count is the number of frames before off, -1 when unknown.
	count int64
	// good is the number of the last frame that passed the checks.
	good uint64
	// latest is the latest capture time seen in this pass.
	latest     int64
	haveLatest bool
}

// Read returns the records after the sequence number after, bounded by maxRecords and maxBytes.
func (s *Spool) Read(ctx context.Context, after uint64, maxRecords int, maxBytes int64) (Batch, error) {
	if err := ctx.Err(); err != nil {
		return Batch{}, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closing {
		return Batch{}, errClosed
	}
	job := &readJob{after: after, maxRecords: maxRecords, maxBytes: maxBytes}
	if maxRecords <= 0 || maxBytes <= 0 {
		return Batch{}, nil
	}
	err := s.collect(job)
	s.afterRead()
	if err != nil {
		return Batch{}, err
	}

	return job.batch, nil
}

// afterRead deletes what a read found unusable and writes the gaps it owes, as far as that needs no drop.
func (s *Spool) afterRead() {
	s.reap()
	// A failure leaves the gaps owed: the next hand-over writes them and reports the failure.
	_ = s.flushPending(true)
}

// collect fills the job from the files in order, ending a batch where the capture context changes.
func (s *Spool) collect(job *readJob) error {
	for i := 0; i < len(s.files) && !job.full; i++ {
		f := s.files[i]
		if f.last == 0 || f.last <= job.after {
			continue
		}
		if job.ident != nil && !job.ident.equal(f.ident) {
			return nil
		}
		if err := s.readFile(f, job); err != nil {
			return err
		}
	}

	return nil
}

// openForRead opens a data file read-only; it must still be the file the spool knows.
func (s *Spool) openForRead(f *segFile) (*os.File, error) {
	rf, st, err := s.dir.openEntry(f.name(), false)
	if err != nil {
		return nil, err
	}
	if st.Dev != f.id.dev || st.Ino != f.id.ino {
		_ = rf.Close()

		return nil, errDamageRefused
	}

	return rf, nil
}

// readFile reads the records of one file that come after the job's position.
func (s *Spool) readFile(f *segFile, job *readJob) error {
	rf, err := s.openForRead(f)
	if err != nil {
		return readError(err)
	}
	defer func() { _ = rf.Close() }()
	p, err := s.startPass(f, rf, job.after)
	if err != nil {
		return err
	}
	s.reader.reset(rf, p.off, f.dataEnd())
	defer s.reader.release()
	for {
		fr, err := s.reader.next()
		if err != nil {
			return s.endPass(p, job, err)
		}
		stop, err := s.consume(p, job, &fr)
		if err != nil || stop {
			return err
		}
	}
}

// readError words an error of opening a data file for reading.
func readError(err error) error {
	return errors.New(readErrPrefix + errText(err))
}

// consume judges one frame: it is skipped, ends the batch, joins it or is damage. The bool is true when the read of
// this file is over.
func (s *Spool) consume(p *pass, job *readJob, fr *frame) (bool, error) {
	f := p.f
	if fr.seq >= maxSeq || fr.seq <= p.good || (p.good == 0 && fr.seq != f.num) {
		s.onDamage(p, errDamageInconsistent)

		return true, nil
	}
	if fr.seq > f.last {
		s.onDamage(p, errDamageInconsistent)

		return true, nil
	}
	if fr.seq <= job.after || isAcked(s.acked, fr.seq) {
		p.good = fr.seq
		p.count = bump(p.count)

		return false, nil
	}
	if len(job.batch.Records) >= job.maxRecords || job.bytes+int64(len(fr.line)) > job.maxBytes {
		job.full = true
		s.saveCursor(p, job, fr.off)

		return true, nil
	}
	rec, _, hi, err := decodeFrame(fr)
	if err != nil {
		s.onDamage(p, err)

		return true, nil
	}
	s.take(p, job, rec, fr)
	if !p.haveLatest || hi > p.latest {
		p.latest, p.haveLatest = hi, true
	}

	return false, nil
}

// bump counts one more frame unless the count is unknown.
func bump(count int64) int64 {
	if count < 0 {
		return count
	}

	return count + 1
}

// take adds a record to the batch.
func (s *Spool) take(p *pass, job *readJob, rec model.Record, fr *frame) {
	if job.ident == nil {
		job.ident = p.f.ident
		job.batch.AgentID, job.batch.BootID = p.f.ident.agent, p.f.ident.boot
		if o := p.f.ident.offset; o != nil {
			v := *o
			job.batch.ClockOffsetNS = &v
		}
	}
	job.batch.Records = append(job.batch.Records, rec)
	job.bytes += int64(len(fr.line))
	job.after = fr.seq
	p.good = fr.seq
	p.count = bump(p.count)
}

// endPass handles the end of the frames of a file: the end itself, a trailer where a frame belongs, or damage.
func (s *Spool) endPass(p *pass, job *readJob, err error) error {
	switch {
	case errors.Is(err, io.EOF):
		s.finishFile(p, job)

		return nil
	case errors.Is(err, errMarker) || isDamage(err):
		s.onDamage(p, damageOf(err))

		return nil
	default:
		return err
	}
}

// damageOf maps a trailer marker where a frame belongs to inconsistent data.
func damageOf(err error) error {
	if errors.Is(err, errMarker) {
		return errDamageInconsistent
	}

	return err
}

// finishFile checks a sealed file's summary against what the pass saw when it reached the end.
func (s *Spool) finishFile(p *pass, job *readJob) {
	f := p.f
	if f.sealed && !f.bad && p.good != 0 {
		if p.good != f.sumLast || (p.count >= 0 && uint64(p.count) != f.sumCount) {
			s.onDamage(p, errDamageInconsistent)

			return
		}
	}
	s.setCursor(cursor{valid: true, num: f.num, lo: job.after, hi: max(p.good, job.after), off: f.dataEnd(), count: p.count})
}

// saveCursor remembers where a pass stopped before a frame that did not fit the batch.
func (s *Spool) saveCursor(p *pass, job *readJob, off int64) {
	s.setCursor(cursor{valid: true, num: p.f.num, lo: job.after, hi: max(p.good, job.after), off: off, count: p.count})
}

// setCursor stores a read position, replacing the one of the same stream or else the least recently used.
func (s *Spool) setCursor(c cursor) {
	s.stamp++
	c.stamp = s.stamp
	slot := 0
	for i := range s.cursors {
		cur := &s.cursors[i]
		if cur.valid && cur.num == c.num && cur.lo <= c.hi && c.lo <= cur.hi {
			s.cursors[i] = c

			return
		}
		if !cur.valid || cur.stamp < s.cursors[slot].stamp {
			slot = i
		}
	}
	s.cursors[slot] = c
}

// findCursor returns the stored position that a read after the number after can continue from.
func (s *Spool) findCursor(num, after uint64) *cursor {
	for i := range s.cursors {
		c := &s.cursors[i]
		if c.valid && c.num == num && c.lo <= after && after <= c.hi {
			s.stamp++
			c.stamp = s.stamp

			return c
		}
	}

	return nil
}

// forget drops what is cached of a data file.
func (s *Spool) forget(num uint64) {
	for i := range s.cursors {
		if s.cursors[i].num == num {
			s.cursors[i].valid = false
		}
	}
	for i, ix := range s.indexes {
		if ix != nil && ix.num == num {
			s.indexes[i] = nil
		}
	}
}

// startPass finds where to begin reading the file after the number after.
func (s *Spool) startPass(f *segFile, rf *os.File, after uint64) (*pass, error) {
	p := &pass{f: f, off: f.hdr}
	if after < f.first {
		return p, nil
	}
	if c := s.findCursor(f.num, after); c != nil {
		p.off, p.count, p.good = c.off, c.count, c.hi
		if c.off >= f.dataEnd() {
			p.off = f.dataEnd()
		}

		return p, nil
	}
	ix, err := s.indexFor(f, rf)
	if err != nil {
		return nil, err
	}
	p.count = -1
	i := sort.Search(len(ix.entries), func(i int) bool { return ix.entries[i].seq > after })
	if i > 0 {
		e := ix.entries[i-1]
		p.off = e.off
		if e.seq > f.first {
			p.good = e.seq - 1
		}
	}

	return p, nil
}

// indexFor returns the sparse index of the file, building it when it is not among the two kept.
func (s *Spool) indexFor(f *segFile, rf *os.File) (*fileIndex, error) {
	s.stamp++
	for _, ix := range s.indexes {
		if ix != nil && ix.num == f.num {
			ix.stamp = s.stamp

			return ix, nil
		}
	}
	ix := &fileIndex{num: f.num, stamp: s.stamp}
	if err := buildIndex(ix, rf, f); err != nil {
		return nil, err
	}
	slot := 0
	for i, other := range s.indexes {
		if other == nil {
			slot = i

			break
		}
		if other.stamp < s.indexes[slot].stamp {
			slot = i
		}
	}
	s.indexes[slot] = ix

	return ix, nil
}

// buildIndex hops from frame to frame reading only their headers and notes the number every size/64 bytes.
func buildIndex(ix *fileIndex, rf *os.File, f *segFile) error {
	end := f.dataEnd()
	step := max(f.size/indexParts, minIndexStep)
	var probe [indexProbe]byte
	for off, sample := f.hdr, f.hdr; off+indexProbe <= end; {
		if n, err := rf.ReadAt(probe[:], off); n != indexProbe && err != nil {
			if errors.Is(err, io.EOF) {
				return nil
			}

			return readError(err)
		}
		size := int64(binary.LittleEndian.Uint32(probe[:4]))
		if size < minPayload || size > maxPayload || binary.LittleEndian.Uint32(probe[:4]) == trailerMark {
			return nil
		}
		if off >= sample {
			ix.entries = append(ix.entries, idxEntry{seq: binary.LittleEndian.Uint64(probe[frameHeadLen:]), off: off})
			sample = off + step
		}
		off += frameHeadLen + size
	}

	return nil
}

// onDamage handles damage found in a file while reading it: the records from the damage on are gone, the spool owes
// a gap for their numbers, and the damage is logged once per file.
func (s *Spool) onDamage(p *pass, reason error) {
	f := p.f
	if f.bad && p.good >= f.last {
		return
	}
	if !f.warned {
		f.warned = true
		s.warnDamaged(f.name(), reason)
	}
	start := p.good + 1
	if p.good == 0 {
		start = f.num
	}
	if end := s.damageRunEnd(f, start); end != 0 && end >= start {
		from, to := s.readDamageSpan(p)
		for _, run := range gaps(s.acked, start, end) {
			s.owe(pendingGap{first: run.First, last: run.Last, from: from, to: to, cause: model.GapSpoolDropped})
		}
	}
	f.bad = true
	if p.good < f.last {
		f.last = p.good
		if p.good == 0 {
			f.first, f.count = 0, 0
		}
	}
	if f == s.act {
		s.detachActive()
	}
	s.forget(f.num)
}

// detachActive stops appending to the active file, which has been found damaged; the next start cuts it.
func (s *Spool) detachActive() {
	if s.actF != nil {
		_ = s.actF.Close()
	}
	s.act, s.actF = nil, nil
}

// fileAfter returns the file after f in the list, or nil.
func (s *Spool) fileAfter(f *segFile) *segFile {
	for i, x := range s.files {
		if x == f && i+1 < len(s.files) {
			return s.files[i+1]
		}
	}

	return nil
}

// filesBefore returns the file before f in the list, or nil.
func (s *Spool) fileBefore(f *segFile) *segFile {
	for i, x := range s.files {
		if x == f && i > 0 {
			return s.files[i-1]
		}
	}

	return nil
}

// damageRunEnd returns the last number damage in f took, or 0 when it cannot be known.
func (s *Spool) damageRunEnd(f *segFile, start uint64) uint64 {
	var nextNum uint64
	if n := s.fileAfter(f); n != nil {
		nextNum = n.num
	}
	if f.sealed && f.sumLast >= start && (nextNum == 0 || f.sumLast < nextNum) {
		return f.sumLast
	}
	if nextNum != 0 {
		return nextNum - 1
	}

	return 0
}

// readDamageSpan returns the time span of the gap for damage found in a pass.
func (s *Spool) readDamageSpan(p *pass) (time.Time, time.Time) {
	f := p.f
	var from, to int64
	var haveFrom, haveTo bool
	switch {
	case p.haveLatest:
		from, haveFrom = p.latest, true
	case p.good != 0 && f.count > 0:
		from, haveFrom = f.earliest, true
	default:
		if prev := s.fileBefore(f); prev != nil && prev.last != 0 {
			from, haveFrom = prev.latest, true
		}
	}
	if f.sealed {
		to, haveTo = f.latest, true
	} else if next := s.fileAfter(f); next != nil && next.last != 0 {
		to, haveTo = next.earliest, true
	}

	return s.finishSpan(from, haveFrom, to, haveTo)
}

// oldestUnacked returns the lowest kept number that is not acknowledged.
func (s *Spool) oldestUnacked() (uint64, bool) {
	for _, f := range s.files {
		if f.last == 0 {
			continue
		}
		n := f.first
		for _, r := range s.acked {
			if r.Last < n {
				continue
			}
			if r.First > n {
				break
			}
			n = r.Last + 1
		}
		if n <= f.last {
			return n, true
		}
	}

	return 0, false
}

// OldestCapture returns the capture time of the oldest unacknowledged record; the bool is false when there is none.
func (s *Spool) OldestCapture(ctx context.Context) (time.Time, bool, error) {
	if err := ctx.Err(); err != nil {
		return time.Time{}, false, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closing {
		return time.Time{}, false, errClosed
	}
	seq, ok := s.oldestUnacked()
	if !ok {
		return time.Time{}, false, nil
	}
	if s.oldest.valid && s.oldest.seq == seq {
		return s.oldest.at, true, nil
	}
	job := &readJob{after: seq - 1, maxRecords: 1, maxBytes: math.MaxInt64}
	err := s.collect(job)
	s.afterRead()
	if err != nil {
		return time.Time{}, false, err
	}
	if len(job.batch.Records) == 0 {
		return time.Time{}, false, nil
	}
	rec := job.batch.Records[0]
	s.oldest = oldestCache{valid: true, seq: rec.Seq, at: rec.CapturedAt}

	return rec.CapturedAt, true, nil
}
