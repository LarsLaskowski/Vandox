package logship

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
	"os"
	"syscall"
	"time"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/syslogline"
)

const (
	// maxHead is the most bytes of the start of a file whose hash is kept to notice that the file was replaced in place.
	maxHead = 256
	// bom is the UTF-8 byte order mark that may start a file.
	bom = "\xef\xbb\xbf"
)

// emitLine hands one line of a file over; it returns true when the run must stop.
type emitLine func(line []byte, cut bool, begin int64) bool

// clockOf returns the clock of the file, which keeps the previous instant across runs, set to the zone of this run. It belongs to the
// state a run works on, so a run that is dropped never moves the instant the re-read starts from.
func (st *fileState) clockOf(loc *time.Location) *syslogline.Clock {
	if st.clock == nil {
		st.clock = syslogline.NewClock(loc)
	} else {
		st.clock.SetZone(loc)
	}
	return st.clock
}

// readBuffer returns the buffer files are read through.
func (l *Logs) readBuffer() []byte {
	if l.buf == nil {
		l.buf = make([]byte, readChunk)
	}
	return l.buf
}

// statOf returns the size and the identity of an open file.
func statOf(f *os.File) (int64, identity, bool) {
	info, err := f.Stat()
	if err != nil {
		return 0, identity{}, false
	}
	sys, ok := info.Sys().(*syscall.Stat_t)
	if !ok {
		return 0, identity{}, false
	}
	return info.Size(), identityOf(sys), true
}

// filesRun is the reading of one configured file in one run.
type filesRun struct {
	r     *run
	i     int
	st    *fileState
	spec  FileSpec
	clock *syslogline.Clock
	size  int64
	// lost is a gap over lines lost to a rotation; the first line read from the new file closes it.
	lostPending bool
	lostFrom    time.Time
}

// readFile reads the new lines of the configured file i. A file that cannot be opened keeps its position and is reported once; while
// the zone of the server is unknown no file is read.
func (r *run) readFile(i int) {
	loc := r.zoneOf()
	if loc == nil {
		return
	}
	st := &r.st.files[i]
	f, err := r.l.src.Open(r.l.cfg.Files[i].Path)
	if err != nil {
		r.fproblems[i] = reasonOf(err)
		if reasonOf(err) == reasonMissing && !st.have {
			st.waiting = true
		}
		return
	}
	adopted := false
	defer func() {
		if !adopted {
			_ = f.Close()
		}
	}()
	size, id, ok := statOf(f)
	if !ok {
		r.fproblems[i] = reasonReadFailed
		return
	}
	r.fproblems[i] = "" // opened: a later failure of the reading sets its reason
	fr := &filesRun{r: r, i: i, st: st, spec: r.l.cfg.Files[i], clock: st.clockOf(loc), size: size}
	if !st.have {
		fromStart := st.waiting
		fr.begin(f, id, fromStart)
		adopted = true
		if !fromStart {
			return
		}
	} else if !fr.resume(f, id, &adopted) {
		return
	}
	fr.read()
	fr.endLostGap()
	fr.updateHead()
}

// begin takes the first position in a file: its start when it did not exist before, else its end. The file becomes the one kept.
func (fr *filesRun) begin(f *os.File, id identity, fromStart bool) {
	*fr.st = fileState{have: true, id: id, cur: f, lastTime: fr.r.start, clock: fr.clock}
	if !fromStart {
		fr.st.line = lineState{start: fr.size, read: fr.size}
		fr.updateHead()
	}
}

// resume continues the reading of a file that has a position. f is the file now at the path: when it is another file the old one was
// rotated, and a file whose first bytes changed or that became shorter was truncated. It returns false when the run must not read the
// file.
func (fr *filesRun) resume(f *os.File, id identity, adopted *bool) bool {
	st := fr.st
	if st.renamed != nil && st.renamed.f == nil && !fr.findRenamed() {
		return false
	}
	switch {
	case id == st.id && st.cur != nil:
		// the kept descriptor names the file at the path
	case id == st.id:
		st.cur, *adopted = f, true
	default:
		if !fr.rotate(f, id, adopted) {
			return false
		}
		return true
	}
	if fr.truncated() {
		fr.truncate()
	}
	return true
}

// findRenamed looks for the rotated file saved with the position, which a restart left without a descriptor. It returns false when the
// directory cannot be reached, which keeps the position.
func (fr *filesRun) findRenamed() bool {
	r, st := fr.r, fr.st
	f, err := findRotated(fr.spec.Path, st.renamed.id)
	switch {
	case err != nil:
		r.fproblems[fr.i] = reasonOf(err)
		return false
	case f == nil:
		r.emitGap(st.lastTime, r.start)
		st.renamed = nil
	default:
		st.renamed.f = f
	}
	return true
}

// rotate handles a file at the path that is another file than the one kept: the kept file, found again by its identity after a restart,
// becomes the renamed file that is read until the writer has moved, and the file at the path is read from its start. A kept file that
// cannot be found is a gap from the last line read to the first line of the new file. An older renamed file that is still read is
// finished first; when the run stops or a read fails before that, nothing changes, it returns false and the next runs go on with the
// renamed file.
func (fr *filesRun) rotate(f *os.File, id identity, adopted *bool) bool {
	r, st := fr.r, fr.st
	if st.renamed != nil && !fr.finishRenamed() {
		return false
	}
	old := st.cur
	if old == nil {
		found, err := findRotated(fr.spec.Path, st.id)
		if err != nil {
			r.fproblems[fr.i] = reasonOf(err)
			return false
		}
		old = found
	}
	if old != nil {
		st.renamed = &renamedFile{f: old, id: st.id, line: st.line, size: -1}
	} else {
		fr.lostPending, fr.lostFrom = true, st.lastTime
	}
	st.cur, st.id, st.line, st.headLen, st.headSum, *adopted = f, id, lineState{}, 0, "", true
	return true
}

// finishRenamed reads the rest of the renamed file, ships its unfinished last line as it is and lets the file go. It returns false, and
// keeps the file, when the run stopped before the end or a read failed (then the reason is set).
func (fr *filesRun) finishRenamed() bool {
	rn := fr.st.renamed
	done, err := fr.r.pump(rn.f, &rn.line, true, fr.emitter())
	if err != nil {
		fr.r.fproblems[fr.i] = reasonReadFailed
		return false
	}
	if !done {
		return false
	}
	fr.st.renamed = nil
	return true
}

// emitter returns the function that turns the lines of the file into records.
func (fr *filesRun) emitter() emitLine {
	return func(line []byte, cut bool, begin int64) bool { return fr.line(line, cut, begin) }
}

// truncated tells whether the file at the path is the kept file but was truncated or replaced in place: it is shorter than what was
// read, or its first bytes are not the ones whose hash was kept.
func (fr *filesRun) truncated() bool {
	st := fr.st
	if fr.size < st.line.read {
		return true
	}
	if st.headLen == 0 {
		return false
	}
	sum, ok := fr.hashOfHead(st.headLen)
	return !ok || sum != st.headSum
}

// truncate restarts the reading at the start of the file: the unfinished line that was held is shipped as it is and one gap covers the
// span from the last line read to the start of the run.
func (fr *filesRun) truncate() {
	r, st := fr.r, fr.st
	if len(st.line.held) > 0 {
		fr.line(st.line.held, st.line.cut, st.line.start)
	}
	r.emitGap(st.lastTime, r.start)
	st.line, st.headLen, st.headSum = lineState{}, 0, ""
}

// hashOfHead returns the SHA-256 of the first n bytes of the kept file; false when it holds fewer.
func (fr *filesRun) hashOfHead(n int) (string, bool) {
	buf := make([]byte, n)
	if got, err := fr.st.cur.ReadAt(buf, 0); got < n || (err != nil && !errors.Is(err, io.EOF)) {
		return "", false
	}
	sum := sha256.Sum256(buf)
	return hex.EncodeToString(sum[:]), true
}

// updateHead keeps the hash of the first bytes of the file, as many as were read up to 256.
func (fr *filesRun) updateHead() {
	st := fr.st
	if st.cur == nil {
		return
	}
	want := int(min(st.line.start, maxHead))
	if st.headLen >= want {
		return
	}
	if sum, ok := fr.hashOfHead(want); ok {
		st.headLen, st.headSum = want, sum
	}
}

// read ships the lines of the renamed file, then those of the file at the path.
func (fr *filesRun) read() {
	r, st := fr.r, fr.st
	if rn := st.renamed; rn != nil && !fr.readRenamed(rn) {
		return
	}
	if r.stopped || st.cur == nil {
		return
	}
	if _, err := r.pump(st.cur, &st.line, false, fr.emitter()); err != nil {
		r.fproblems[fr.i] = reasonReadFailed
	}
}

// readRenamed reads the renamed file to its end and returns false when a read failed, which stops the reading of the file in this run.
// It is finished, with its unfinished last line shipped as it is, in the run in which the file at the path has data and the renamed file did not grow since the previous run: the writer has moved to the new file.
func (fr *filesRun) readRenamed(rn *renamedFile) bool {
	r, st := fr.r, fr.st
	size, _, ok := statOf(rn.f)
	if !ok {
		size = rn.size
	}
	finish := fr.size > 0 && rn.size == size
	eof, err := r.pump(rn.f, &rn.line, finish, fr.emitter())
	if err != nil {
		r.fproblems[fr.i] = reasonReadFailed
		return false
	}
	if finish && eof {
		st.renamed = nil
		return true
	}
	rn.size = size
	return true
}

// endLostGap closes the gap over lines lost to a rotation when the file at the path had no line to close it with.
func (fr *filesRun) endLostGap() {
	if fr.lostPending {
		fr.lostPending = false
		fr.r.emitGap(fr.lostFrom, fr.r.start)
	}
}

// line turns one line of the file into a record, unless it is empty or filtered; it returns true when the run must stop. A line without a
// syslog header gets the capture time of the line before it.
func (fr *filesRun) line(line []byte, cut bool, begin int64) bool {
	r, st := fr.r, fr.st
	line, over := cutLine(line)
	cut = cut || over
	if begin == 0 {
		line = bytes.TrimPrefix(line, []byte(bom))
	}
	if len(line) == 0 {
		return r.tick(false)
	}
	parsed := syslogline.Parse(line, fr.clock, r.start)
	at := parsed.Time
	if !parsed.Header {
		at = st.lastTime
	}
	from := st.lastTime
	if fr.lostPending {
		fr.lostPending = false
		r.emitGap(fr.lostFrom, at)
	}
	st.lastTime = at
	if at.After(r.st.last) {
		r.st.last = at
	}
	rec, text := fileRecord(fr.spec.Path, parsed, at, cut)
	if r.l.filter.drops(text) {
		return r.tick(false)
	}
	r.emit(rec, from)
	return r.tick(true)
}

// fileRecord maps a parsed syslog line to a log line record.
func fileRecord(path string, p syslogline.Line, at time.Time, cut bool) (model.Record, *model.LogLine) {
	l := &model.LogLine{Log: path, PID: p.PID, Priority: p.Priority}
	var hostCut, programCut, messageCut bool
	l.Host, hostCut = cleaned([]byte(p.Host), maxShortBytes)
	l.Program, programCut = cleaned([]byte(p.Program), maxShortBytes)
	l.Message, messageCut = cleaned([]byte(p.Message), maxMessageBytes)
	l.Truncated = cut || hostCut || programCut || messageCut
	return model.Record{Meta: model.Meta{Source: sourceSyslog, CapturedAt: at.UTC()}, Data: l}, l
}

// cutLine cuts a line longer than maxMessageBytes as the import's line reader does: to at most maxMessageBytes bytes, moved back over at
// most three UTF-8 continuation bytes so that no character is split; cut tells that the line was longer.
func cutLine(line []byte) (kept []byte, cut bool) {
	if len(line) <= maxMessageBytes {
		return line, false
	}
	n := maxMessageBytes
	for back := 0; back < utf8.UTFMax-1 && n > 1 && line[n]&0xC0 == 0x80; back++ {
		n--
	}
	return line[:n], true
}

// heldAppend adds seg to the unfinished line, which keeps at most lineKeep bytes; cut tells that more of the line followed.
func (ls *lineState) heldAppend(seg []byte) {
	room := lineKeep - len(ls.held)
	if len(seg) > room {
		ls.cut = true
		seg = seg[:max(room, 0)]
	}
	ls.held = append(ls.held, seg...)
}

// pump reads f from where ls stands to its end, or until the run must stop, and hands every line with its line feed to emit. With
// finish the unfinished last line is handed over too. It reports whether the end of the file was reached. The offset in ls.start is
// that of the first line not handed over, so an unfinished line is read again after a restart. A stop by time or context keeps the
// offset read and the held line for the next run; a stop at the record or text budget goes back to the next line, because the rest of
// the buffer is read again.
func (r *run) pump(f *os.File, ls *lineState, finish bool, emit emitLine) (bool, error) {
	buf := r.l.readBuffer()
	for {
		if r.stopped || r.ctx.Err() != nil {
			return false, nil // the offset and the held line carry into the next run
		}
		n, err := f.ReadAt(buf, ls.read)
		if err != nil && !errors.Is(err, io.EOF) {
			return false, err
		}
		if splitLines(ls, emit, buf[:n]) {
			ls.rewind()
			r.stopped = true
			return false, nil
		}
		ls.read += int64(n)
		if n < len(buf) {
			break
		}
		r.stopped = r.timeUp()
	}
	if finish {
		return handOver(ls, emit), nil
	}
	return true, nil
}

// handOver hands over the unfinished last line of a file as it is; it returns false when the run must stop.
func handOver(ls *lineState, emit emitLine) bool {
	if len(ls.held) == 0 {
		return true
	}
	stopped := emit(ls.held, ls.cut, ls.start)
	ls.start = ls.read
	ls.held, ls.cut = ls.held[:0], false
	return !stopped
}

// splitLines hands the lines of data, which starts at the offset ls.read, to emit; it returns true when the run must stop.
func splitLines(ls *lineState, emit emitLine, data []byte) bool {
	off := ls.read
	for len(data) > 0 {
		idx := bytes.IndexByte(data, '\n')
		if idx < 0 {
			ls.heldAppend(data)
			return false
		}
		ls.heldAppend(data[:idx])
		begin := ls.start
		ls.start = off + int64(idx) + 1
		line := ls.held
		if !ls.cut && len(line) > 0 && line[len(line)-1] == '\r' {
			line = line[:len(line)-1] // the import's reader removes the \r of a \r\n that fits its buffer
		}
		stop := emit(line, ls.cut, begin)
		ls.held, ls.cut = ls.held[:0], false
		off += int64(idx) + 1
		data = data[idx+1:]
		if stop {
			return true
		}
	}
	return false
}

// rewind drops what was read after the last line handed over, so that the next run reads it again.
func (ls *lineState) rewind() {
	ls.read, ls.held, ls.cut = ls.start, ls.held[:0], false
}
