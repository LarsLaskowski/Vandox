package kernevents

import (
	"errors"
	"io/fs"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/journal"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
)

const (
	fieldTransport = "_TRANSPORT"
	fieldFacility  = "SYSLOG_FACILITY"
	fieldMessage   = "MESSAGE"
	fieldSource    = "_SOURCE_MONOTONIC_TIMESTAMP"

	transportKernel = "kernel"
	transportDriver = "driver"
	facilityKernel  = "0"
)

// wantFields are the items of an entry that are read in full.
var wantFields = []string{fieldTransport, fieldFacility, fieldMessage, fieldSource}

var (
	// kernelList is the data object whose entry list holds the entries of the kernel transport.
	kernelList = []byte("_TRANSPORT=kernel")
	// missedList is the data object whose entry list holds journald's entries about missed kernel messages.
	missedList = []byte("MESSAGE_ID=e9bf28e6e834481bb6f48f548ad13606")
)

// stream is the entry list of one data object of a file with the entry read ahead.
type stream struct {
	cur    journal.Cursor
	listed bool // the data object exists
	ent    journal.Entry
	have   bool // ent holds an entry that was not taken yet
	end    bool // the list ended
	torn   bool // the list ended at a cut-off tail
}

// result tells how reading a file ended.
type result int

const (
	resEnd  result = iota // the lists ended
	resStop               // a limit ended the run
	resTorn               // the lists ended at a cut-off tail
	resFail               // the reader failed
)

// listCursor is the cursor of a list at the end of a file that was read to its end.
type listCursor struct {
	found bool
	cur   journal.Cursor
}

// cursorCache keeps the cursors of the file the position lies in, at the end of its lists, so that the next poll continues there
// instead of looking the lists up again. It is only used when the position is the one it stands at.
type cursorCache struct {
	valid  bool
	fileID [16]byte
	seq    uint64
	kern   listCursor
	miss   listCursor
}

// fileReader reads the kernel entries and the missed-message entries of one file in the order of their sequence numbers.
type fileReader struct {
	r        *pass
	fi       fileInfo
	o        *openFile
	mode     fileMode
	kern     stream
	miss     stream
	lastSeq  uint64
	consumed bool
	lastRT   time.Time // the last readable kernel entry read in this file
}

// readFile reads one file and reports whether the run goes on with the next one.
func (r *pass) readFile(dir fs.FS, fi fileInfo, mode fileMode) bool {
	if r.limitReached() {
		r.stopped = true
		return false
	}
	o, err := openJournal(dir, fi.name)
	if err != nil {
		return r.failure(&fileReader{r: r, fi: fi, mode: mode}, err)
	}
	defer o.close()

	fr := &fileReader{r: r, fi: fi, o: o, mode: mode}
	res, err := fr.read()
	if mode.moving {
		r.k.cache = fr.cursors(res)
	}
	switch res {
	case resStop:
		r.stopped = true
		fr.suspend()
		return false
	case resTorn:
		r.endBroken(fr, fi.hdr)
		return true
	case resFail:
		return r.failure(fr, err)
	default:
		fr.finish()
		return true
	}
}

// cursors returns the cache to keep after reading the file: the cursors when both lists were read to their end, else nothing.
func (fr *fileReader) cursors(res result) cursorCache {
	if res != resEnd {
		return cursorCache{}
	}
	return cursorCache{
		valid:  true,
		fileID: fr.fi.hdr.FileID,
		seq:    max(fr.mode.after, fr.lastSeq),
		kern:   listCursor{found: fr.kern.listed, cur: fr.kern.cur},
		miss:   listCursor{found: fr.miss.listed, cur: fr.miss.cur},
	}
}

// failure handles an error of the reader and reports whether the run goes on. A file the reader cannot decode ends there; a failing
// read stops the run until the third run in a row fails at the same position, then the file is given up.
func (r *pass) failure(fr *fileReader, err error) bool {
	online := fr.fi.online
	if refusal(err) && !online {
		r.noteProblem(fr.fi, err)
		r.endBroken(fr, fr.fi.hdr)
		return true
	}
	if !errors.Is(err, journal.ErrMalformed) {
		r.noteProblem(fr.fi, err)
	}
	fr.suspend()
	if !r.countFailure(fr.fi.name) {
		return false
	}
	r.noteProblem(fr.fi, err)
	r.endBroken(fr, fr.latestHeader())
	return true
}

// read reads the lists to their end or until a limit or an error stops it.
func (fr *fileReader) read() (result, error) {
	if err := fr.open(); err != nil {
		return resFail, err
	}
	for {
		if fr.r.limitReached() {
			return resStop, nil
		}
		e, kernel, err := fr.nextEntry()
		if err != nil {
			return resFail, err
		}
		if e == nil {
			break
		}
		fr.consume(e, kernel)
	}
	if fr.kern.torn || fr.miss.torn {
		return resTorn, nil
	}
	return resEnd, nil
}

// open finds and positions the two lists, or takes the cursors the previous run left at their end.
func (fr *fileReader) open() error {
	c := fr.r.k.cache
	reuse := fr.mode.moving && c.valid && c.fileID == fr.fi.hdr.FileID && c.seq == fr.mode.after
	var err error
	if fr.kern, err = fr.seekList(kernelList, c.kern, reuse); err != nil {
		return err
	}
	fr.miss, err = fr.seekList(missedList, c.miss, reuse)
	return err
}

// seekList finds the data object with the payload and positions a cursor after the first entry to read; a missing object is an empty
// list. A cursor of the cache is used as it is.
func (fr *fileReader) seekList(payload []byte, cached listCursor, reuse bool) (stream, error) {
	if reuse && cached.found {
		return stream{cur: cached.cur, listed: true}, nil
	}
	s := stream{end: true}
	err := fr.do(func() error {
		d, found, err := fr.o.f.Lookup(payload)
		if err != nil || !found {
			return err
		}
		cur, err := fr.o.f.Seek(d, fr.mode.after)
		if err != nil {
			return err
		}
		s = stream{cur: cur, listed: true}
		return nil
	})
	return s, err
}

// do runs a read of the file. A tear in the online file may be the file growing since it was opened: the size is read again and the
// read repeated once.
func (fr *fileReader) do(op func() error) error {
	err := op()
	if !errors.Is(err, journal.ErrMalformed) || !fr.fi.online {
		return err
	}
	size, serr := fr.o.size()
	if serr != nil || fr.o.f.Refresh(size) != nil {
		return err
	}
	return op()
}

// nextEntry returns the entry with the lowest sequence number of the two lists; kernel tells which list it is from. A nil entry means
// both lists ended. The entry stays valid until the next call.
func (fr *fileReader) nextEntry() (*journal.Entry, bool, error) {
	if err := fr.fill(&fr.kern); err != nil {
		return nil, false, err
	}
	if err := fr.fill(&fr.miss); err != nil {
		return nil, false, err
	}
	switch {
	case fr.kern.have && fr.miss.have && fr.miss.ent.Seqnum < fr.kern.ent.Seqnum:
		fr.miss.have = false
		return &fr.miss.ent, false, nil
	case fr.kern.have:
		if fr.miss.have && fr.miss.ent.Seqnum == fr.kern.ent.Seqnum {
			fr.miss.have = false
		}
		fr.kern.have = false
		return &fr.kern.ent, true, nil
	case fr.miss.have:
		fr.miss.have = false
		return &fr.miss.ent, false, nil
	default:
		return nil, false, nil
	}
}

// fill reads the next entry of a list unless one is waiting.
func (fr *fileReader) fill(s *stream) error {
	if s.have || s.end {
		return nil
	}
	var ok bool
	err := fr.do(func() error {
		var err error
		ok, err = fr.o.f.Next(&s.cur, wantFields, &s.ent)
		return err
	})
	if err != nil {
		return err
	}
	if ok {
		s.have = true
		return nil
	}
	s.end, s.torn = true, s.cur.Torn()
	return nil
}

// consume handles one entry and moves the position past it.
func (fr *fileReader) consume(e *journal.Entry, kernel bool) {
	if kernel {
		fr.r.kernelEntry(fr, e)
	} else {
		fr.r.missedEntry(e)
	}
	fr.lastSeq, fr.consumed = e.Seqnum, true
	if fr.mode.moving {
		fr.r.movePosition(fr.fi.hdr.SeqnumID, e)
	}
}

// movePosition sets the position to the entry; the time never moves back.
func (r *pass) movePosition(id [16]byte, e *journal.Entry) {
	rt := r.cur.pos.rt
	if realtimeOK(e.RealtimeUS) && e.Realtime.After(rt) {
		rt = e.Realtime
	}
	r.cur.pos = place{valid: true, id: id, seq: e.Seqnum, rt: rt}
}

// finish records that a file of another seqnum ID was read to its end.
func (fr *fileReader) finish() {
	if !fr.mode.moving {
		fr.r.cur.markDone(fr.fi.hdr.FileID)
	}
}

// suspend remembers where reading a file that does not move the position stopped.
func (fr *fileReader) suspend() {
	if !fr.mode.moving && fr.consumed {
		fr.r.cur.remember(fr.fi.hdr.FileID, fr.lastSeq)
	}
}

// latestHeader returns the header of the file; the online file's is read again.
func (fr *fileReader) latestHeader() journal.Header {
	if fr.o != nil && fr.fi.online {
		if size, err := fr.o.size(); err == nil && fr.o.f.Refresh(size) == nil {
			return fr.o.f.Header()
		}
	}
	return fr.fi.hdr
}

// endBroken ends a file that cannot be read on: the span up to the end of the file is a gap, and the position moves past the file so
// that it is not read again.
func (r *pass) endBroken(fr *fileReader, hdr journal.Header) {
	from := fr.lastRT
	if from.IsZero() {
		from = r.startPos.rt
	}
	if from.IsZero() {
		from = r.start
	}
	switch tail := hdr.TailRealtime; {
	case tail.IsZero():
		r.openGap(from)
	case tail.After(from):
		r.emitGap(from, tail)
	}
	if fr.mode.moving {
		seq := max(fr.lastSeq, hdr.TailSeqnum)
		rt := r.cur.pos.rt
		if hdr.TailRealtime.After(rt) {
			rt = hdr.TailRealtime
		}
		r.cur.pos = place{valid: true, id: fr.fi.hdr.SeqnumID, seq: seq, rt: rt}
		return
	}
	r.cur.markDone(fr.fi.hdr.FileID)
}

// entryInfo is what a readable kernel entry gives.
type entryInfo struct {
	rt        time.Time
	mono      time.Duration
	src       time.Duration
	hasSrc    bool
	facility0 bool
	message   []byte
}

// readKernel decodes a kernel entry; false means the entry is unreadable: it has an item of unknown name or beyond the kept
// fields, or a stamp outside the range.
func readKernel(e *journal.Entry) (entryInfo, bool) {
	var info entryInfo
	if e.Omitted > 0 || !realtimeOK(e.RealtimeUS) || e.MonotonicUS > maxMicros {
		return info, false
	}
	info.rt, info.mono = micros(e.RealtimeUS), e.Monotonic
	var transports, facilities, messages, sources int
	var transport, facility, source []byte
	for _, f := range e.Fields {
		switch {
		case f.Compressed:
			return info, false
		case f.Name == fieldTransport:
			transports, transport = transports+1, f.Value
		case f.Name == fieldFacility:
			facilities, facility = facilities+1, f.Value
		case f.Name == fieldMessage:
			messages, info.message = messages+1, f.Value
		case f.Name == fieldSource:
			sources, source = sources+1, f.Value
		}
	}
	if transports != 1 || string(transport) != transportKernel || sources > 1 {
		return info, false
	}
	if sources == 1 {
		var ok bool
		if info.src, ok = parseMicros(source); !ok {
			return info, false
		}
		info.hasSrc = true
	}
	info.facility0 = facilities == 1 && string(facility) == facilityKernel
	if messages != 1 {
		info.message = nil
	}
	return info, true
}

// kernelEntry handles an entry of the kernel transport: gaps, the drift of the kernel clock and kills.
func (r *pass) kernelEntry(fr *fileReader, e *journal.Entry) {
	info, ok := readKernel(e)
	if !ok {
		r.openGap(r.gapFrom())
		return
	}
	r.closeGap(info.rt)
	from := r.gapFrom()
	r.cur.last, r.cur.spanEnd, fr.lastRT = info.rt, info.rt, info.rt
	if info.facility0 {
		r.countedEntry(e, info, from)
	}
}

// countedEntry handles an entry with the kernel facility: it feeds the drift window and may be a kill.
func (r *pass) countedEntry(e *journal.Entry, info entryInfo, from time.Time) {
	var delay time.Duration
	if info.hasSrc {
		delay = r.cur.drift.delay(e.BootID, info.mono, max(info.mono-info.src, 0))
	}
	kill, ok := parseKill(info.message)
	if !ok {
		return
	}
	boot := formatUUID(e.BootID)
	identity := info.mono
	if info.hasSrc {
		identity = info.src
	}
	kill.BootID, kill.KernelTime = boot, &identity
	text, _ := procstats.CleanText(string(info.message), model.MaxTextBytes)
	r.emit(model.Record{
		Meta: model.Meta{Source: Name, CapturedAt: info.rt.Add(-delay)},
		Data: &model.KernelEvent{Type: model.KernelEventOOMKill, OOMKill: &kill, Message: text},
	}, from)
	r.kills++
	ref := model.KillRef{BootID: boot, KernelTime: identity, VictimPID: kill.VictimPID}
	if r.isLive(e, info, delay) && !r.k.trig.knows(ref) {
		r.live = append(r.live, ref)
	}
}

// isLive reports whether a kill is recent enough for extra snapshots: of the current boot and at most a minute old on the monotonic
// clock, the delay included.
func (r *pass) isLive(e *journal.Entry, info entryInfo, delay time.Duration) bool {
	if !r.k.curBootOK || e.BootID != r.k.curBoot {
		return false
	}
	uptime, ok := r.uptimeNow()
	if !ok {
		return false
	}
	return max(uptime-info.mono+delay, 0) <= liveWithin
}

// missedEntry handles an entry of the missed-message list: journald's own entry opens a gap.
func (r *pass) missedEntry(e *journal.Entry) {
	if e.Omitted > 0 || isDriver(e) {
		r.openGap(r.gapFrom())
	}
}

// isDriver reports whether an entry has exactly one transport item and it is the driver transport; an entry with an item of unknown
// name counts, since the transport cannot be ruled out.
func isDriver(e *journal.Entry) bool {
	transports, driver := 0, false
	for _, f := range e.Fields {
		if f.Compressed {
			return true
		}
		if f.Name == fieldTransport {
			transports, driver = transports+1, string(f.Value) == transportDriver
		}
	}
	return transports == 1 && driver
}
