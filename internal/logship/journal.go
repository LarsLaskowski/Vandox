package logship

import (
	"context"
	"errors"
	"io"
	"io/fs"
	"slices"
	"strings"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/journal"
	"github.com/LarsLaskowski/Vandox/internal/logstages"
)

const (
	// setAsideSuffix ends the name of a file journald set aside after an unclean shutdown.
	setAsideSuffix = "~"

	reasonUnsupported = "unsupported layout"
	reasonMalformed   = "malformed file"
	reasonRead        = "read failed"
	labelOnline       = "online"
	labelArchived     = "archived"

	problemMachineID = "journal: machine ID not readable"
	problemDirectory = "journal: directory not readable"
	problemNotFound  = "journal: no journal directory"
)

// fileInfo is a journal file with the header read when the files were listed.
type fileInfo struct {
	name   string
	hdr    journal.Header
	online bool
}

// kind names the state of the file in log texts.
func (f fileInfo) kind() string {
	if f.online {
		return labelOnline
	}
	return labelArchived
}

// idGroup holds the files of one journal sequence number ID, by head sequence number.
type idGroup struct {
	id    [16]byte
	files []fileInfo
}

// first returns the file with the lowest sequence numbers, and last the one with the highest.
func (g *idGroup) first() fileInfo { return g.files[0] }

func (g *idGroup) last() fileInfo { return g.files[len(g.files)-1] }

// hasOnline tells whether a file of the ID is the one journald writes to.
func (g *idGroup) hasOnline() bool {
	return slices.ContainsFunc(g.files, func(f fileInfo) bool { return f.online })
}

// isOnlineName tells whether name is the journal file journald is writing to: system.journal or user-<UID>.journal.
func isOnlineName(name string) bool {
	return name == "system.journal" || (strings.HasPrefix(name, "user-") && strings.HasSuffix(name, ".journal") && !strings.Contains(name, "@"))
}

// openFile is an open journal file and the handle behind it.
type openFile struct {
	f    *journal.File
	file fs.File
}

// close releases the handle.
func (o *openFile) close() { _ = o.file.Close() }

// size returns the current size of the file.
func (o *openFile) size() (int64, error) {
	st, err := o.file.Stat()
	if err != nil {
		return 0, err
	}
	return st.Size(), nil
}

// openJournal opens the named file of dir for the reader.
func openJournal(dir fs.FS, name string) (*openFile, error) {
	file, err := dir.Open(name)
	if err != nil {
		return nil, err
	}
	ra, ok := file.(io.ReaderAt)
	if !ok {
		_ = file.Close()
		return nil, journal.ErrRead
	}
	st, err := file.Stat()
	if err != nil {
		_ = file.Close()
		return nil, err
	}
	f, err := journal.Open(ra, st.Size(), strings.HasSuffix(name, setAsideSuffix))
	if err != nil {
		_ = file.Close()
		return nil, err
	}
	return &openFile{f: f, file: file}, nil
}

// reason returns the fixed text for an error of the reader; it never carries a path or content.
func reason(err error) string {
	switch {
	case errors.Is(err, journal.ErrUnsupported):
		return reasonUnsupported
	case errors.Is(err, journal.ErrMalformed):
		return reasonMalformed
	default:
		return reasonRead
	}
}

// refusal tells whether err means that the reader cannot read the file at all.
func refusal(err error) bool {
	return errors.Is(err, journal.ErrUnsupported) || errors.Is(err, journal.ErrMalformed)
}

// noteProblem keeps the first fixed reason why the journal could not be read.
func (r *run) noteProblem(f fileInfo, err error) {
	if r.jproblem == "" {
		r.jproblem = "journal: " + f.kind() + ": " + reason(err)
	}
}

// readJournal reads the new entries of the journal files of every sequence number ID.
func (r *run) readJournal(ctx context.Context) {
	if !r.l.cfg.Journal {
		r.st.journal = map[[16]byte]*jpos{}
		return
	}
	dir, names, ok := r.locate()
	if !ok {
		return
	}
	files, ok := r.scan(dir, names)
	if !ok {
		return
	}
	r.journalRead = true
	groups := groupFiles(files)
	r.forgetLost(groups)
	for i := range groups {
		if r.st.journal[groups[i].id] == nil {
			r.startPosition(&groups[i])
		}
	}
	for i := range groups {
		if r.stopped {
			break
		}
		r.readGroup(ctx, dir, &groups[i])
	}
	r.settle(groups)
}

// locate finds the journal directory of this machine and lists its files. The first root that has the directory wins.
func (r *run) locate() (fs.FS, []string, bool) {
	id, err := journal.MachineDir(r.l.src.Etc)
	if err != nil {
		r.jproblem = problemMachineID
		return nil, nil, false
	}
	problem := problemNotFound
	for _, root := range r.l.src.Journal {
		dir, err := fs.Sub(root, id)
		if err != nil {
			problem = problemDirectory
			continue
		}
		names, err := journal.ListAllFiles(dir)
		switch {
		case err == nil:
			return dir, names, true
		case !errors.Is(err, fs.ErrNotExist):
			problem = problemDirectory
		}
	}
	r.jproblem = problem
	return nil, nil, false
}

// scan reads the headers of the listed files. A file the reader refuses is recorded as a gap and left out; a file that cannot be read
// for another reason stops the journal for this run, so that no position is passed. It returns false when the run must stop reading.
func (r *run) scan(dir fs.FS, names []string) ([]fileInfo, bool) {
	files := make([]fileInfo, 0, len(names))
	for _, name := range names {
		info := fileInfo{name: name, online: isOnlineName(name)}
		o, err := openJournal(dir, name)
		switch {
		case err == nil:
			info.hdr = o.f.Header()
			o.close()
			files = append(files, info)
		case errors.Is(err, fs.ErrNotExist):
			// vacuumed or rotated since the listing; the position rules cover what it held
		case refusal(err):
			r.noteProblem(info, err)
			r.refuse(info)
		default:
			r.noteProblem(info, err)
			return files, false
		}
	}
	return files, true
}

// refuse records the gap for a file the reader cannot read: one the first time this process meets it, and for the online file another
// one for every refusedGapEvery after the last, so that a journal the reader cannot read stays covered.
func (r *run) refuse(f fileInfo) {
	refused := r.st.refused
	last, met := refused[f.name]
	from := r.newest
	if from.IsZero() {
		from = r.start
	}
	switch {
	case !met:
		r.closeJournalStages() // the refused file may hold lines of any ID
		r.emitGap(from, r.start)
	case f.online && r.start.Sub(last) >= refusedGapEvery:
		r.closeJournalStages()
		r.emitGap(last, r.start)
	default:
		return
	}
	if met || len(refused) < maxTracked {
		refused[f.name] = r.start
	}
}

// groupFiles groups the files by sequence number ID, the groups by the head time of their first file and the files of a group by head
// sequence number.
func groupFiles(files []fileInfo) []idGroup {
	byID := map[[16]byte]*idGroup{}
	var groups []*idGroup
	for _, f := range files {
		g := byID[f.hdr.SeqnumID]
		if g == nil {
			g = &idGroup{id: f.hdr.SeqnumID}
			byID[g.id] = g
			groups = append(groups, g)
		}
		g.files = append(g.files, f)
	}
	out := make([]idGroup, 0, len(groups))
	for _, g := range groups {
		slices.SortFunc(g.files, func(a, b fileInfo) int {
			if c := compareUint(a.hdr.HeadSeqnum, b.hdr.HeadSeqnum); c != 0 {
				return c
			}
			return strings.Compare(a.name, b.name)
		})
		out = append(out, *g)
	}
	slices.SortFunc(out, func(a, b idGroup) int {
		if c := a.first().hdr.HeadRealtime.Compare(b.first().hdr.HeadRealtime); c != 0 {
			return c
		}
		return slices.Compare(a.id[:], b.id[:])
	})
	return out
}

// compareUint compares two numbers.
func compareUint(a, b uint64) int {
	switch {
	case a < b:
		return -1
	case a > b:
		return 1
	default:
		return 0
	}
}

// forgetLost forgets the position of an ID none of whose files is left, with one gap from the position's time to the first file that
// starts after it (else to the start of the run).
func (r *run) forgetLost(groups []idGroup) {
	present := map[[16]byte]bool{}
	for i := range groups {
		present[groups[i].id] = true
	}
	for id, p := range r.st.journal {
		if present[id] {
			continue
		}
		to := r.start
		for i := range groups {
			if head := groups[i].first().hdr.HeadRealtime; head.After(p.rt) && head.Before(to) {
				to = head
			}
		}
		r.closeStages(journalKey(id))
		delete(r.st.recovery, journalKey(id))
		r.emitGap(p.rt, to)
		delete(r.st.journal, id)
	}
}

// startPosition gives an ID without a position its first one. With no position held at all, a first start, the ID starts at its tail.
// Otherwise it is read from its start when its first file starts after the newest position held at the start of the run; else it starts
// at its tail, with one gap up to its tail time when that is later than the newest position, because the skipped part may hold lines
// newer than every position.
func (r *run) startPosition(g *idGroup) {
	first, last := g.first(), g.last()
	tail := &jpos{seq: last.hdr.TailSeqnum, rt: tailTime(last.hdr, r.start)}
	if r.havePos {
		if head := first.hdr.HeadRealtime; !head.IsZero() && head.After(r.newest) {
			r.st.journal[g.id] = &jpos{seq: first.hdr.HeadSeqnum - min(first.hdr.HeadSeqnum, 1), rt: head}
			return
		}
		if t := last.hdr.TailRealtime; t.After(r.newest) {
			r.emitGap(r.newest, t)
		}
	}
	r.st.journal[g.id] = tail
}

// tailTime returns the time of the end of a file: its tail time, else its head time, else def.
func tailTime(h journal.Header, def time.Time) time.Time {
	switch {
	case !h.TailRealtime.IsZero():
		return h.TailRealtime
	case !h.HeadRealtime.IsZero():
		return h.HeadRealtime
	default:
		return def
	}
}

// settle marks the IDs with an online file and forgets the position of an ID without one that was read to its end, unless it is the
// newest position: the files of a forgotten ID must stay older than the newest position held, or the time rule would read them again.
func (r *run) settle(groups []idGroup) {
	newest, _ := r.st.newestJournal()
	for i := range groups {
		g := &groups[i]
		p := r.st.journal[g.id]
		if p == nil {
			continue
		}
		p.online = g.hasOnline()
		if !p.online && p.seq >= g.last().hdr.TailSeqnum && p.rt.Before(newest) {
			r.closeStages(journalKey(g.id))
			delete(r.st.recovery, journalKey(g.id))
			delete(r.st.journal, g.id)
		}
	}
}

// readGroup reads the files of one ID from its position.
func (r *run) readGroup(ctx context.Context, dir fs.FS, g *idGroup) {
	p := r.st.journal[g.id]
	if p == nil {
		return
	}
	r.detectVacuumed(g, p)
	done := true
	for _, fi := range g.files {
		if r.stopped || !r.readJournalFile(ctx, dir, g, fi, p) {
			done = false
			break
		}
	}
	r.endGap()
	if done && !r.stopped {
		r.endOfID(g)
	}
}

// endOfID handles an ID that was read to its end in this run: its groups expire by the clock, and unless an online system file may
// still grow them, they close, because the import groups per file.
func (r *run) endOfID(g *idGroup) {
	key := journalKey(g.id)
	r.expireStages(key)
	if !slices.ContainsFunc(g.files, func(f fileInfo) bool { return f.online && f.name == systemJournal }) {
		r.closeStages(key)
	}
}

// endGap closes the pending gap of an ID at the end of its reading: at the last unreadable entry when the run stopped before the end,
// else at the start of the run.
func (r *run) endGap() {
	to := r.start
	if r.stopped && !r.lastBad.IsZero() {
		to = r.lastBad
	}
	r.closeGapAt(to)
	r.lastBad = time.Time{}
}

// readJournalFile reads one file of the ID from the position and reports whether the ID goes on with its next file.
func (r *run) readJournalFile(ctx context.Context, dir fs.FS, g *idGroup, fi fileInfo, p *jpos) bool {
	h := fi.hdr
	if h.TailSeqnum != 0 && h.TailSeqnum <= p.seq {
		return true
	}
	o, err := openJournal(dir, fi.name)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return true
		}
		r.noteProblem(fi, err)
		return false
	}
	defer o.close()
	rd := &fileReader{r: r, g: g, fi: fi, o: o, p: p}
	return rd.read(ctx)
}

// detectVacuumed records the entries lost when files of the ID remain but none holds the position and one starts after it: one gap from
// the position's time to the head time of the first such file, unless that file starts right after the position. The position then
// moves up to the file, so the gap is made once. The sequence numbers of the files of one ID are not contiguous (the user files draw
// from the same counter), so a hole between two files that hold the position is not a loss.
func (r *run) detectVacuumed(g *idGroup, p *jpos) {
	var next *fileInfo
	for i := range g.files {
		h := g.files[i].hdr
		if h.HeadSeqnum <= p.seq && p.seq <= h.TailSeqnum {
			return
		}
		if next == nil && h.HeadSeqnum > p.seq {
			next = &g.files[i]
		}
	}
	if next == nil || next.hdr.HeadSeqnum == p.seq+1 {
		return
	}
	r.closeStages(journalKey(g.id))
	if head := next.hdr.HeadRealtime; head.After(p.rt) {
		r.emitGap(p.rt, head)
	} else {
		r.openGapFrom(p.rt)
	}
	p.seq = next.hdr.HeadSeqnum - 1
}

// fileReader reads the entries of one journal file.
type fileReader struct {
	r      *run
	g      *idGroup
	fi     fileInfo
	o      *openFile
	p      *jpos
	lastRT time.Time // the last readable entry read from this file in this run
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

// read reads the entries after the position and reports whether the ID goes on with its next file.
func (fr *fileReader) read(ctx context.Context) bool {
	r := fr.r
	var c journal.Cursor
	err := fr.do(func() error {
		var err error
		c, err = fr.o.f.SeekAll(fr.p.seq)
		return err
	})
	if err != nil {
		return fr.failure(err)
	}
	opt := r.l.readOptions()
	e := &r.l.entry
	for {
		if r.stopped {
			return false
		}
		var ok bool
		before := r.l.dec.calls
		err := fr.do(func() error {
			var err error
			ok, err = fr.o.f.NextWith(&c, wantFields, e, opt)
			return err
		})
		if err != nil {
			return fr.failure(err)
		}
		if !ok {
			if c.Torn() {
				fr.endBroken(false)
			}
			return true
		}
		fr.entry(ctx, e)
		if r.l.dec.calls > before && !r.stopped {
			r.stopped = r.timeUp() // decoding costs time: look at the clock after every entry that needed it
		}
	}
}

// entry handles one entry and moves the position over it.
func (fr *fileReader) entry(ctx context.Context, e *journal.Entry) {
	r, p := fr.r, fr.p
	from := p.rt
	if from.IsZero() {
		from = r.start
	}
	before := p.seq
	switch {
	case e.Undecoded > 0:
		r.closeStages(journalKey(fr.g.id))
		r.openGapFrom(from)
		p.seq = e.Seqnum
		if !e.Realtime.IsZero() {
			r.lastBad = e.Realtime
		}
		r.tick(ctx, false)
		return
	case e.RealtimeUS == 0 || e.RealtimeUS > maxRealtimeUS:
		p.seq = e.Seqnum
		r.tick(ctx, false)
		return
	}
	r.closeGapAt(e.Realtime)
	p.seq = e.Seqnum
	if e.Realtime.After(p.rt) {
		p.rt = e.Realtime
	}
	fr.lastRT = e.Realtime
	if e.Realtime.After(r.st.last) {
		r.st.last = e.Realtime
	}
	rec, ok := journalRecord(e)
	if !ok || r.l.filter.drops(lineOf(rec)) {
		r.tick(ctx, false)
		return
	}
	line := logstages.Line{Rec: rec, Mark: logstages.Mark{At: before, Before: from}, RunStart: r.start, MailOnly: isUserFile(fr.fi.name)}
	r.stage(journalKey(fr.g.id), line, rec.CapturedAt)
	r.tick(ctx, true)
}

// failure handles an error of the reader and reports whether the ID goes on with its next file. A structural damage gives an archived
// file up at once and an online file when the third run in a row stops at the same position; any other failure stops the ID, and the
// position stays.
func (fr *fileReader) failure(err error) bool {
	r := fr.r
	if !errors.Is(err, journal.ErrMalformed) {
		r.noteProblem(fr.fi, err)
		return false
	}
	if fr.fi.online {
		r.failed = true
		if !r.l.countFailure(fr.fi.name, fr.g.id, fr.p.seq) {
			return false
		}
	}
	r.noteProblem(fr.fi, err)
	fr.endBroken(true)
	return true
}

// endBroken gives up the rest of the file: one gap from the last entry read from it (else the position's time, else the start of the
// run) to its tail time, and the position moves to the tail.
func (fr *fileReader) endBroken(damaged bool) {
	r, p := fr.r, fr.p
	h := fr.fi.hdr
	if damaged && fr.fi.online {
		if size, err := fr.o.size(); err == nil && fr.o.f.Refresh(size) == nil {
			h = fr.o.f.Header()
		}
	}
	from := fr.lastRT
	if from.IsZero() {
		from = p.rt
	}
	if from.IsZero() {
		from = r.start
	}
	if r.gap.pending {
		from, r.gap.pending = r.gap.from, false
	}
	to := h.TailRealtime
	if to.IsZero() {
		to = r.start
	}
	r.closeStages(journalKey(fr.g.id))
	r.emitGap(from, to)
	p.seq = max(p.seq, h.TailSeqnum)
	if h.TailRealtime.After(p.rt) {
		p.rt = h.TailRealtime
	}
}
