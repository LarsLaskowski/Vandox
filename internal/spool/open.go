package spool

import (
	"errors"
	"log/slog"
	"os"
	"slices"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// foundKind classifies a data file found at Open.
type foundKind int

const (
	// kindClean is a file whose records are all good.
	kindClean foundKind = iota
	// kindEmpty is a file with a good header and no record.
	kindEmpty
	// kindPartial is a file with good records followed by damage.
	kindPartial
	// kindWhole is a file that is damaged as a whole.
	kindWhole
	// kindRefused is an entry the spool refuses to open.
	kindRefused
	// kindRejected is a file whose name breaks the rules of the numbers.
	kindRejected
	// kindOther is a file of another agent ID.
	kindOther
)

// found is a data file found in the directory.
type found struct {
	num    uint64
	kind   foundKind
	reason error
	seg    *segFile
	res    scanResult
	// skip marks a file whose name broke the rules of the numbers.
	skip bool
}

// metaState is what the meta file said at Open.
type metaState struct {
	doc     metaDoc
	present bool
	valid   bool
}

// nameSet keeps the highest data file numbers found in the directory, at most limit of them.
type nameSet struct {
	limit   int
	held    []uint64
	dropped bool
	lowest  uint64
}

// add offers a number; the lowest one over the limit is remembered as dropped.
func (n *nameSet) add(num uint64) {
	if len(n.held) >= n.limit {
		if num <= n.held[0] {
			n.drop(num)

			return
		}
		n.drop(n.held[0])
		n.held = n.held[1:]
	}
	i, _ := slices.BinarySearch(n.held, num)
	n.held = slices.Insert(n.held, i, num)
}

// drop remembers a number that is not held.
func (n *nameSet) drop(num uint64) {
	if !n.dropped || num < n.lowest {
		n.lowest = num
	}
	n.dropped = true
}

// newSpool checks the options and returns a spool that is not open yet.
func newSpool(opts Options, log *slog.Logger) (*Spool, error) {
	if len(opts.AgentID) < 1 || len(opts.AgentID) > maxAgentLen {
		return nil, errors.New("spool: agent id: not between 1 and 64 bytes")
	}
	if !validBootID(opts.BootID) {
		return nil, errors.New("spool: boot id: not a lower-case UUID")
	}
	if opts.MaxBytes <= 0 {
		return nil, errors.New("spool: max size: not positive")
	}
	if log == nil {
		log = slog.New(slog.DiscardHandler)
	}

	return &Spool{
		opts:    opts,
		log:     log.With("component", "spool"),
		ident:   identity{agent: opts.AgentID, boot: opts.BootID},
		fileMax: min(max(opts.MaxBytes/fileDivisor, minFileBytes), maxFileBytes),
	}, nil
}

// Open opens the spool in opts.Dir and recovers its state.
func Open(opts Options, log *slog.Logger) (*Spool, error) {
	s, err := newSpool(opts, log)
	if err != nil {
		return nil, err
	}
	dir, err := openDir(opts.Dir)
	if err != nil {
		return nil, err
	}
	s.dir = dir
	if err := s.recoverState(); err != nil {
		s.abandon()

		return nil, err
	}
	s.startLoop()

	return s, nil
}

// abandon releases what a failed Open acquired.
func (s *Spool) abandon() {
	for _, f := range []*os.File{s.actF, s.metaF} {
		if f != nil {
			_ = f.Close()
		}
	}
	s.dir.close()
}

// recoverState reads the directory and brings the spool to a state it can run in.
func (s *Spool) recoverState() error {
	if err := s.checkMetaNames(); err != nil {
		return err
	}
	set, err := s.listData()
	if err != nil {
		return err
	}
	meta, err := s.loadMeta()
	if err != nil {
		return err
	}
	if set.dropped {
		s.warnDamaged(dataName(set.lowest), errDamageTooMany)
	}
	ents, err := s.inspectAll(set.held)
	if err != nil {
		return err
	}
	s.chain(ents)
	if meta.valid && meta.doc.AgentID == s.opts.AgentID {
		s.acked = slices.Clone(meta.doc.Acked)
	}
	gaps := s.damageGaps(ents)
	s.pickNext(meta, ents)
	doomed, err := s.settle(ents)
	if err != nil {
		return err
	}
	s.pend = gaps
	if err := s.flushPending(false); err != nil {
		return err
	}
	s.removeDoomed(doomed)
	s.reap()
	if err := s.writeMeta(false, true); err != nil {
		return err
	}
	if !meta.present && len(set.held) == 0 {
		s.log.Info("spool created")
	}

	return nil
}

// listData lists the data file names, keeping the highest maxFiles + 2 numbers.
func (s *Spool) listData() (*nameSet, error) {
	set := &nameSet{limit: maxFiles + 2}
	err := s.dir.list(func(name string) {
		if n, ok := dataNumber(name); ok {
			set.add(n)
		}
	})

	return set, err
}

// loadMeta reads the meta file; damage is logged and leaves the state as for a missing file.
func (s *Spool) loadMeta() (metaState, error) {
	doc, err := s.readMeta()
	switch {
	case err == nil:
		return metaState{doc: doc, present: true, valid: true}, nil
	case errors.Is(err, errAbsent):
		return metaState{}, nil
	case isDamage(err) && !errors.Is(err, errDamageRefused):
		s.warnDamaged(metaFileName, err)

		return metaState{present: true}, nil
	default:
		return metaState{}, metaError(err)
	}
}

// warnDamaged logs the damage found in a file.
func (s *Spool) warnDamaged(file string, reason error) {
	s.log.Warn("spool data damaged", slog.String("file", file), slog.String("err", reason.Error()))
}

// inspectAll examines every data file, oldest first.
func (s *Spool) inspectAll(nums []uint64) ([]*found, error) {
	ents := make([]*found, 0, len(nums))
	for i, num := range nums {
		var nextNum uint64
		if i+1 < len(nums) {
			nextNum = nums[i+1]
		}
		e, err := s.inspect(num, nextNum)
		if err != nil {
			return nil, err
		}
		if e != nil {
			ents = append(ents, e)
		}
	}

	return ents, nil
}

// inspect opens one data file read-only, checks it and classifies it. A nil result means the file vanished.
func (s *Spool) inspect(num, nextNum uint64) (*found, error) {
	name := dataName(num)
	f, st, err := s.dir.openEntry(name, false)
	switch {
	case errors.Is(err, errAbsent):
		return nil, nil
	case errors.Is(err, errDamageRefused):
		return &found{num: num, kind: kindRefused, reason: errDamageRefused}, nil
	case err != nil:
		return nil, err
	}
	defer func() { _ = f.Close() }()
	e := &found{num: num, seg: &segFile{num: num, id: fileID{dev: st.Dev, ino: st.Ino}, size: st.Size}}
	ident, hdr, err := readHeader(f, st.Size)
	if err != nil {
		return damagedWhole(e, err), nil
	}
	e.seg.hdr, e.seg.ident = int64(hdr), s.intern(&ident)
	if err := s.examine(e, f, nextNum); err != nil {
		return nil, err
	}
	s.classify(e)

	return e, nil
}

// damagedWhole marks a file as damaged as a whole.
func damagedWhole(e *found, reason error) *found {
	e.kind, e.reason = kindWhole, reason

	return e
}

// readHeader reads and checks the header of a file of the given size.
func readHeader(f *os.File, size int64) (identity, int, error) {
	buf := make([]byte, min(size, headerMax))
	if n, err := f.ReadAt(buf, 0); n != len(buf) && err != nil {
		return identity{}, 0, readFailure(err)
	}

	return parseHeader(buf)
}

// intern returns the shared copy of a context.
func (s *Spool) intern(id *identity) *identity {
	if id.equal(&s.ident) {
		return &s.ident
	}
	for _, known := range s.idents {
		if known.equal(id) {
			return known
		}
	}
	s.idents = append(s.idents, id)

	return id
}

// examine reads the file's frames, or only its trailer and first record when it is sealed and plausible.
func (s *Spool) examine(e *found, f *os.File, nextNum uint64) error {
	seg := e.seg
	res, ok, err := s.reader.quick(f, e.num, seg.hdr, seg.size, nextNum)
	if err == nil && !ok {
		res, err = s.reader.scan(f, e.num, seg.hdr, seg.size)
	}
	if err != nil {
		return err
	}
	e.res = res
	seg.first, seg.last, seg.count = res.first, res.last, res.count
	seg.earliest, seg.latest = res.earliest, res.latest
	if res.tr != nil {
		seg.sealed, seg.sumLast, seg.sumCount = true, res.tr.last, res.tr.count
	}
	e.reason = res.damage

	return nil
}

// classify sets the kind of a file from what was found in it.
func (s *Spool) classify(e *found) {
	switch {
	case e.seg.ident.agent != s.opts.AgentID:
		e.kind = kindOther
	case e.res.badName || (e.res.count == 0 && e.res.damage != nil):
		e.kind = kindWhole
	case e.res.damage != nil:
		e.kind = kindPartial
	case e.res.count == 0:
		e.kind = kindEmpty
	default:
		e.kind = kindClean
	}
}

// chain applies the rules of the names: a number below the largest is the later file's fault, and none may reach
// 2^63 - 1. A file that breaks them is rejected and neither raises the next number nor gets a gap.
func (s *Spool) chain(ents []*found) {
	var prevLast uint64
	for _, e := range ents {
		if e.kind == kindOther {
			continue
		}
		if e.num >= maxSeq || (prevLast != 0 && e.num <= prevLast) {
			e.skip = true
			if e.kind != kindRefused {
				e.kind, e.reason = kindRejected, errDamageInconsistent
			}

			continue
		}
		switch e.kind {
		case kindClean, kindPartial:
			prevLast = e.res.last
		default:
			prevLast = e.num
		}
	}
}

// valid reports whether the entry's name passed the rules of the names.
func (e *found) valid() bool {
	return !e.skip && e.kind != kindOther
}

// damageGaps returns the gaps for the numbers damaged files and refused entries took with them.
func (s *Spool) damageGaps(ents []*found) []pendingGap {
	var out []pendingGap
	for i, e := range ents {
		if e.kind != kindWhole && e.kind != kindRefused && e.kind != kindPartial {
			continue
		}
		if !e.valid() {
			continue
		}
		start := e.num
		if e.kind == kindPartial {
			start = e.res.last + 1
		}
		end := damageEnd(ents, i, start)
		if end == 0 || end < start {
			continue
		}
		from, to := s.damageSpan(ents, i, start)
		for _, run := range gaps(s.acked, start, end) {
			out = append(out, pendingGap{first: run.First, last: run.Last, from: from, to: to, cause: model.GapSpoolDropped})
		}
	}

	return out
}

// nextValid returns the first valid entry after i, or nil.
func nextValid(ents []*found, i int) *found {
	for _, e := range ents[i+1:] {
		if e.valid() {
			return e
		}
	}

	return nil
}

// damageEnd returns the last number the damage of entry i took, or 0 when it is not known: the trailer's last number
// when the trailer is intact and in range, else the number below the next file's first.
func damageEnd(ents []*found, i int, start uint64) uint64 {
	if t := intactTail(ents, i, start); t != nil {
		return t.last
	}
	if n := nextValid(ents, i); n != nil {
		return n.num - 1
	}

	return 0
}

// intactTail returns the trailer at the end of entry i when it can end the run that starts at start: it lies in range.
func intactTail(ents []*found, i int, start uint64) *trailer {
	var nextNum uint64
	if n := nextValid(ents, i); n != nil {
		nextNum = n.num
	}
	if t := ents[i].res.tail; t != nil && t.last >= start && (nextNum == 0 || t.last < nextNum) {
		return t
	}

	return nil
}

// damageFrom returns the latest capture time before the damage of entry i: of its own good records, else of the
// nearest file before it.
func damageFrom(ents []*found, i int) (int64, bool) {
	if ents[i].kind == kindPartial {
		return ents[i].res.latest, true
	}
	for j := i - 1; j >= 0; j-- {
		if p := ents[j]; p.kind == kindClean || p.kind == kindPartial {
			return p.res.latest, true
		}
	}

	return 0, false
}

// damageTo returns the time the damage of entry i ended: the intact trailer's latest, else the next file's earliest.
func damageTo(ents []*found, i int, start uint64) (int64, bool) {
	if t := intactTail(ents, i, start); t != nil {
		return t.latest, true
	}
	if n := nextValid(ents, i); n != nil && (n.kind == kindClean || n.kind == kindPartial) {
		return n.res.earliest, true
	}

	return 0, false
}

// damageSpan returns the time span of the gap for the damage of entry i.
func (s *Spool) damageSpan(ents []*found, i int, start uint64) (time.Time, time.Time) {
	from, haveFrom := damageFrom(ents, i)
	to, haveTo := damageTo(ents, i, start)

	return s.finishSpan(from, haveFrom, to, haveTo)
}

// finishSpan builds the span of a gap from the sides that are known; a missing side is the other one one second away,
// both missing the spool's clock, and the span is at least one second.
func (s *Spool) finishSpan(from int64, haveFrom bool, to int64, haveTo bool) (time.Time, time.Time) {
	switch {
	case haveFrom && haveTo:
		return spanOf(nanoTime(from), nanoTime(to))
	case haveFrom:
		return spanOf(nanoTime(from), nanoTime(from))
	case haveTo:
		return spanOf(nanoTime(to).Add(-time.Second), nanoTime(to))
	default:
		now := s.now().UTC()

		return spanOf(now.Add(-time.Second), now)
	}
}

// spanOf returns from and to with to at least one second after from and both inside the stored range.
func spanOf(from, to time.Time) (time.Time, time.Time) {
	if to.Before(from.Add(time.Second)) {
		to = from.Add(time.Second)
	}
	if !inRange(to) {
		to = maxTime
		from = to.Add(-time.Second)
	}
	if !inRange(from) {
		from = minTime
		to = from.Add(time.Second)
	}

	return from, to
}

// pickNext chooses the number of the next record: the largest of the stored counter, the numbers the files hold and,
// where the counter cannot be trusted, the clock bound.
func (s *Spool) pickNext(meta metaState, ents []*found) {
	fresh := !meta.valid || meta.doc.AgentID != s.opts.AgentID || (!meta.doc.Clean && meta.doc.BootID != s.opts.BootID)
	var next uint64
	if !fresh {
		next = meta.doc.Next
	}
	for _, e := range ents {
		next = max(next, e.floor())
	}
	if newest := newestValid(ents); newest != nil && newest.unknownTail() {
		fresh = true
	}
	if fresh {
		next = max(next, ClockBound(s.now()))
	}
	s.next = max(next, 1)
}

// floor returns the lowest number the next record may have given this file.
func (e *found) floor() uint64 {
	if !e.valid() {
		return 0
	}
	switch e.kind {
	case kindClean, kindPartial:
		return e.res.last + 1
	case kindEmpty:
		return e.num
	default:
		return e.num + 1
	}
}

// newestValid returns the newest entry that took part in the numbering.
func newestValid(ents []*found) *found {
	for i := len(ents) - 1; i >= 0; i-- {
		if ents[i].valid() {
			return ents[i]
		}
	}

	return nil
}

// unknownTail reports that the numbers at the end of the file cannot be known.
func (e *found) unknownTail() bool {
	return e.kind == kindWhole || e.kind == kindRefused || (e.kind == kindPartial && !e.res.torn)
}
