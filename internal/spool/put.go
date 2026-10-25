package spool

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	// gapReserve is the size kept for every gap record a hand-over may write.
	gapReserve = 1024
	// seqMargin is added to the size of every encoded record: its final number may have more digits than the
	// placeholder it was encoded with.
	seqMargin = 20
	// placeholderSeq numbers a record while it is encoded before its final number is known.
	placeholderSeq = 1
	// sourceAgent is the source of the gaps the spool writes.
	sourceAgent = "agent"
)

// handover is the records of one Put, encoded and measured.
type handover struct {
	lines [][]byte
	spans [][2]int64
	bytes int64
	// earliest and latest are the capture times, for the gap of a refusal.
	earliest, latest time.Time
}

// prepare encodes the records with a placeholder number and measures them.
func prepare(records []model.Record) (*handover, error) {
	h := &handover{lines: make([][]byte, len(records)), spans: make([][2]int64, len(records))}
	for i := range records {
		rec := records[i]
		rec.Seq = placeholderSeq
		lo, hi, ok := recordSpan(&rec)
		if !ok {
			return nil, errors.New("spool: record: time out of range")
		}
		line, err := wire.MarshalRecord(&rec)
		if err != nil {
			return nil, fmt.Errorf("spool: record: %w", err)
		}
		h.lines[i], h.spans[i] = line, [2]int64{lo, hi}
		h.bytes += frameHeadLen + seqLen + int64(len(line)) + seqMargin
		if i == 0 || rec.CapturedAt.Before(h.earliest) {
			h.earliest = rec.CapturedAt
		}
		if i == 0 || rec.CapturedAt.After(h.latest) {
			h.latest = rec.CapturedAt
		}
	}

	return h, nil
}

// Put appends records to the spool.
func (s *Spool) Put(ctx context.Context, records []model.Record) error {
	if len(records) == 0 {
		return nil
	}
	h, err := prepare(records)
	if err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closing {
		return ErrClosed
	}
	if err := s.place(ctx, h); err != nil {
		return s.refuse(ctx, h, len(records), err)
	}

	return nil
}

// refuse records a hand-over the spool could not keep and returns its error.
func (s *Spool) refuse(ctx context.Context, h *handover, n int, err error) error {
	if s.refused.count == 0 || h.earliest.Before(s.refused.earliest) {
		s.refused.earliest = h.earliest
	}
	if s.refused.count == 0 || h.latest.After(s.refused.latest) {
		s.refused.latest = h.latest
	}
	s.refused.count += n
	if !s.failing {
		s.failing = true
		s.log.WarnContext(ctx, "spool write failed", slog.String("err", err.Error()))
	}

	return err
}

// place makes room for the hand-over and writes it.
func (s *Spool) place(ctx context.Context, h *handover) error {
	if err := s.heal(); err != nil {
		return err
	}
	if s.exhausted(len(h.lines) + s.owed()) {
		return errExhausted
	}
	s.putPend = len(s.pend)
	dropped, reason, err := s.makeRoom(h)
	s.noteDrops(ctx, dropped, reason, err)
	if err != nil {
		return err
	}

	return s.write(h)
}

// noteDrops logs the start and the end of a drop episode.
func (s *Spool) noteDrops(ctx context.Context, dropped int64, reason string, err error) {
	switch {
	case dropped > 0:
		s.droppedTotal += dropped
		if !s.dropping {
			s.dropping = true
			s.log.WarnContext(ctx, "spool data dropped", slog.String("reason", reason), slog.Int64("records", s.droppedTotal))
		}
	case s.dropping && err == nil:
		s.dropping = false
		s.log.InfoContext(ctx, "spool data no longer dropped", slog.Int64("records", s.droppedTotal))
		s.droppedTotal = 0
	}
}

// heal cuts the active file back to its last good frame after an earlier cut failed.
func (s *Spool) heal() error {
	if !s.healTail || s.actF == nil {
		return nil
	}
	if err := s.actF.Truncate(s.act.size); err != nil {
		return writeError(err)
	}
	s.healTail = false

	return nil
}

// owed returns the number of gap records the next write carries besides its records.
func (s *Spool) owed() int {
	n := len(s.pend)
	if s.failing {
		n++
	}

	return n
}

// exhausted reports whether n more numbers would reach 2^63 - 1.
func (s *Spool) exhausted(n int) bool {
	if s.next >= maxSeq || n < 0 {
		return true
	}

	return uint64(n) > maxSeq-s.next
}

// rollNeeded reports whether a write of the given size starts a new data file.
func (s *Spool) rollNeeded(frames int64) bool {
	return s.act == nil || s.act.bad || (s.act.count > 0 && s.act.size+frames > s.fileMax)
}

// needBytes returns the bytes the hand-over adds to the files, gaps and a new file's header and trailer included.
func (s *Spool) needBytes(frames int64) int64 {
	if !s.rollNeeded(frames) {
		return frames
	}
	frames += headerMax
	if s.act != nil && s.act.count > 0 {
		frames += trailerLen
	}

	return frames
}

// shortage returns why the hand-over does not fit now: "file_limit", "size_bound", "disk_space", or "" when it fits.
func (s *Spool) shortage(h *handover) (string, error) {
	frames := h.bytes + int64(s.owed())*gapReserve
	need := s.needBytes(frames)
	if s.rollNeeded(frames) && len(s.files) >= maxFiles {
		return "file_limit", nil
	}
	if s.diskUsed()+metaSlack+need > s.opts.MaxBytes {
		return "size_bound", nil
	}
	if s.opts.MinFreeBytes <= 0 {
		return "", nil
	}
	avail, err := s.available()
	if err != nil {
		return "", writeError(err)
	}
	if !fitsDisk(avail, need, s.opts.MinFreeBytes) {
		return "disk_space", nil
	}

	return "", nil
}

// makeRoom drops the oldest data files until the hand-over fits. It returns the number of records dropped and the reason
// of the first drop.
func (s *Spool) makeRoom(h *handover) (int64, string, error) {
	if h.bytes+int64(s.owed())*gapReserve+metaSlack > s.opts.MaxBytes {
		return 0, "", errNoRoom
	}
	var dropped int64
	first := ""
	for {
		reason, err := s.shortage(h)
		if err != nil || reason == "" {
			return dropped, first, err
		}
		n, err := s.dropOldest()
		if err != nil {
			return dropped, first, err
		}
		dropped += n
		if first == "" {
			first = reason
		}
	}
}

// dropOldest deletes the oldest data file and owes a gap for each run of its unacknowledged numbers. It returns the
// number of records dropped.
func (s *Spool) dropOldest() (int64, error) {
	if len(s.files) == 0 || s.files[0] == s.act {
		return 0, errNoRoom
	}
	f := s.files[0]
	var dropped int64
	var owed []pendingGap
	var runs []ackRange
	if f.last != 0 {
		runs = gaps(s.acked, f.first, f.last)
	}
	for _, run := range runs {
		from, to := s.runSpan(f, run)
		owed = append(owed, pendingGap{first: run.First, last: run.Last, from: from, to: to, cause: model.GapSpoolDropped, drop: true})
		dropped += countOf(run)
	}
	if err := s.removeFile(f); err != nil {
		return 0, writeError(err)
	}
	s.files = s.files[1:]
	if lo := s.oldestKept(); lo != 0 {
		s.acked = trimBelow(s.acked, lo)
	}
	for _, g := range owed {
		s.owe(g)
	}

	return dropped, nil
}

// owe adds a gap to the ones owed; a run that follows the previous drop of this hand-over directly extends its gap.
func (s *Spool) owe(g pendingGap) {
	if n := len(s.pend); n > s.putPend {
		prev := &s.pend[n-1]
		if prev.drop && prev.last+1 == g.first {
			prev.last = g.last
			prev.from = minTimeOf(prev.from, g.from)
			prev.to = maxTimeOf(prev.to, g.to)

			return
		}
	}
	s.pend = append(s.pend, g)
}

// minTimeOf returns the earlier of two times.
func minTimeOf(a, b time.Time) time.Time {
	if b.Before(a) {
		return b
	}

	return a
}

// maxTimeOf returns the later of two times.
func maxTimeOf(a, b time.Time) time.Time {
	if b.After(a) {
		return b
	}

	return a
}

// runSpan returns the time span of the records of a run in file f: from the earliest to the latest capture time, the
// spans of dropped gap records included, and at least one second long.
func (s *Spool) runSpan(f *segFile, run ackRange) (time.Time, time.Time) {
	lo, hi := f.earliest, f.latest
	if run.First > f.first || run.Last < f.last {
		if l, h, ok := s.scanSpan(f, run); ok {
			lo, hi = l, h
		}
	}

	return spanOf(nanoTime(lo), nanoTime(hi))
}

// scanSpan reads the frames of a run and returns the span of their times.
func (s *Spool) scanSpan(f *segFile, run ackRange) (lo, hi int64, found bool) {
	rf, err := s.openForRead(f)
	if err != nil {
		return 0, 0, false
	}
	defer func() { _ = rf.Close() }()
	s.reader.reset(rf, f.hdr, f.dataEnd())
	defer s.reader.release()
	for {
		fr, err := s.reader.next()
		if err != nil || fr.seq > run.Last {
			return lo, hi, found
		}
		if fr.seq < run.First {
			continue
		}
		_, l, h, err := decodeFrame(&fr)
		if err != nil {
			return lo, hi, found
		}
		if !found {
			lo, hi, found = l, h, true

			continue
		}
		lo, hi = min(lo, l), max(hi, h)
	}
}

// gapRecords returns the gaps the next write carries: the episode's gap about unknown losses first, then the ones owed.
func (s *Spool) gapRecords() []pendingGap {
	var out []pendingGap
	if s.failing {
		from, to := spanOf(s.refused.earliest, s.refused.latest)
		out = append(out, pendingGap{from: from, to: to, cause: model.GapUnknown})
	}

	return append(out, s.pend...)
}

// gapRecord builds the record of a gap, numbered with the placeholder.
func (s *Spool) gapRecord(g pendingGap) model.Record {
	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: sourceAgent, Seq: placeholderSeq, CapturedAt: s.now().UTC()},
		Data: &model.Gap{From: g.from, To: g.to, Cause: g.cause, FirstSeq: g.first, LastSeq: g.last},
	}
}

// bufCapacity returns the capacity reserved for the frames of a hand-over of the given size and the owed gaps. It is a
// hint, bounded by maxPrealloc; the buffer grows by appending when it needs more.
func bufCapacity(handBytes int64, gaps int) int {
	if handBytes < 0 || gaps < 0 {
		return maxPrealloc
	}
	frames := int(min(handBytes, maxPrealloc))
	reserve := min(gaps, maxPrealloc/gapReserve) * gapReserve

	return min(frames+reserve, maxPrealloc)
}

// write numbers the owed gaps and the records, appends them as one write and updates the files' figures.
func (s *Spool) write(h *handover) error {
	owed := s.gapRecords()
	if s.exhausted(len(h.lines) + len(owed)) {
		return errExhausted
	}
	first := s.next
	buf := make([]byte, 0, bufCapacity(h.bytes, len(owed)))
	spans := make([][2]int64, 0, min(len(h.lines), maxPrealloc))
	seq := first
	for _, g := range owed {
		rec := s.gapRecord(g)
		line, err := wire.MarshalRecord(&rec)
		if err != nil {
			return fmt.Errorf("spool: gap: %w", err)
		}
		lo, hi, _ := recordSpan(&rec)
		buf = appendFrame(buf, seq, withSeq(line, seq))
		spans = append(spans, [2]int64{lo, hi})
		seq++
	}
	for i, line := range h.lines {
		buf = appendFrame(buf, seq, withSeq(line, seq))
		seq++
		spans = append(spans, h.spans[i])
	}
	if err := s.ensureActive(int64(len(buf)), first); err != nil {
		return err
	}
	if err := s.appendBuf(buf); err != nil {
		return err
	}
	s.commit(first, spans, int64(len(buf)))
	if s.failing {
		s.failing = false
		s.refused = refusedStats{}
		s.log.Info("spool writes again")
	}

	return nil
}

// appendBuf writes the frames at the end of the active file and cuts the file back when that fails.
func (s *Spool) appendBuf(buf []byte) error {
	err := writeAll(s.actF, buf, s.act.size)
	if err == nil {
		return nil
	}
	if s.actF.Truncate(s.act.size) != nil {
		s.healTail = true
	}

	return writeError(err)
}

// commit makes the written records part of the active file.
func (s *Spool) commit(first uint64, spans [][2]int64, written int64) {
	f := s.act
	for i, sp := range spans {
		f.add(first+uint64(i), sp[0], sp[1])
	}
	f.size += written
	s.next = first + uint64(len(spans))
	s.pend = nil
	s.putPend = 0
	s.gen++
}

// ensureActive makes sure there is an active file with room for the frames, sealing the full one and creating the
// next. The new file is named after the first number it will hold.
func (s *Spool) ensureActive(frames int64, first uint64) error {
	if !s.rollNeeded(frames) {
		return nil
	}
	if s.act != nil {
		if err := s.sealActive(); err != nil {
			return err
		}
	}

	return s.newActive(first)
}

// sealActive seals the active file with its trailer and syncs it before the next file is created. A file without a
// record is removed instead; it is never sealed.
func (s *Spool) sealActive() error {
	f, file := s.act, s.actF
	s.act, s.actF = nil, nil
	defer func() { _ = file.Close() }()
	if f.count == 0 || f.bad {
		return s.discard(f)
	}

	return s.writeTrailer(file, f)
}

// discard removes a file the spool holds that has no usable record.
func (s *Spool) discard(f *segFile) error {
	if err := s.removeFile(f); err != nil {
		return writeError(err)
	}
	for i, x := range s.files {
		if x == f {
			s.files = append(s.files[:i], s.files[i+1:]...)

			break
		}
	}

	return nil
}

// newActive creates the data file whose first record will have the number first.
func (s *Spool) newActive(first uint64) error {
	name := dataName(first)
	file, id, err := s.dir.create(name)
	if err != nil {
		return writeError(err)
	}
	header := s.ident.encodeHeader()
	if err := writeAll(file, header, 0); err != nil {
		_ = file.Close()
		_ = s.dir.removeTrusted(name, &id)

		return writeError(err)
	}
	seg := &segFile{num: first, id: id, ident: &s.ident, hdr: int64(len(header)), size: int64(len(header))}
	s.files = append(s.files, seg)
	s.act, s.actF = seg, file
	s.dirNeed = true
	s.gen++

	return nil
}

// flushPending writes the gaps the spool owes. With check set it writes only when that needs neither a drop nor a new
// file beyond the limits; otherwise the gaps stay owed for the next hand-over.
func (s *Spool) flushPending(check bool) error {
	if len(s.pend) == 0 {
		return nil
	}
	h := &handover{}
	if check && !s.canFlush(h) {
		return nil
	}

	return s.write(h)
}

// canFlush reports whether the owed gaps fit without dropping anything.
func (s *Spool) canFlush(h *handover) bool {
	reason, err := s.shortage(h)

	return err == nil && reason == "" && !s.exhausted(s.owed())
}

// countOf returns the number of numbers in a range, capped at the largest int64.
func countOf(r ackRange) int64 {
	n := r.Last - r.First + 1
	if n > math.MaxInt64 {
		return math.MaxInt64
	}

	return int64(n)
}
