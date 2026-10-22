package kernevents

import (
	"context"
	"io/fs"
	"slices"
	"strings"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	// maxKills is the most kills one run returns.
	maxKills = 256
	// readBudget is how long one run reads, half the timeout of the job.
	readBudget = time.Second
	// refusedGapEvery is the spacing of the further gaps while the online file is refused.
	refusedGapEvery = 600 * time.Second
	// giveUpAfter is the number of runs in a row that fail at the same position before the file is given up.
	giveUpAfter = 3
)

// pass is one pass over the journal.
type pass struct {
	k        *Kernel
	ctx      context.Context
	start    time.Time
	begin    time.Time
	cur      readState
	startPos place
	records  []model.Record
	froms    []time.Time
	kills    int
	pending  bool
	pendFrom time.Time
	problem  string
	failed   bool
	stopped  bool
	live     []model.KillRef
	uptime   time.Duration
	uptimeOK bool
	uptimeAt bool
}

// emit adds a record; from is the start of the gap that replaces the record if the record check refuses it.
func (r *pass) emit(rec model.Record, from time.Time) {
	r.records = append(r.records, rec)
	r.froms = append(r.froms, from)
}

// emitGap adds a gap over [from, to).
func (r *pass) emitGap(from, to time.Time) {
	r.emit(newGap(from, to, r.start), time.Time{})
}

// openGap starts a gap that the next readable kernel entry, or the end of the run, closes.
func (r *pass) openGap(from time.Time) {
	if !r.pending {
		r.pending, r.pendFrom = true, from
	}
}

// closeGap ends the open gap at to.
func (r *pass) closeGap(to time.Time) {
	if r.pending {
		r.emitGap(r.pendFrom, to)
		r.pending = false
	}
}

// gapFrom returns where a gap starts that begins at the entry being read: the last readable kernel entry, else the committed position.
func (r *pass) gapFrom() time.Time {
	switch {
	case !r.cur.last.IsZero():
		return r.cur.last
	case !r.startPos.rt.IsZero():
		return r.startPos.rt
	default:
		return r.start
	}
}

// limitReached reports whether the run has read enough: 256 kills, one second, or a cancelled context.
func (r *pass) limitReached() bool {
	return r.kills >= maxKills || r.ctx.Err() != nil || time.Since(r.begin) >= readBudget
}

// noteProblem keeps the first fixed reason why the journal could not be read.
func (r *pass) noteProblem(f fileInfo, err error) {
	if r.problem == "" {
		r.problem = "journal: " + f.kind() + ": " + reason(err)
	}
}

// countFailure counts the runs in a row that fail at the same position and reports whether the file is given up now.
func (r *pass) countFailure(name string) bool {
	k := r.k
	r.failed = true
	key := failKey{name: name, id: r.cur.pos.id, seq: r.cur.pos.seq}
	if k.fail.key == key {
		k.fail.n++
	} else {
		k.fail = failure{key: key, n: 1}
	}
	if k.fail.n < giveUpAfter {
		return false
	}
	k.fail = failure{}
	return true
}

// uptimeNow returns the uptime, read once per run.
func (r *pass) uptimeNow() (time.Duration, bool) {
	if !r.uptimeAt {
		r.uptime, r.uptimeOK = agentstate.ReadUptime(r.k.src.Proc)
		r.uptimeAt = true
	}
	return r.uptime, r.uptimeOK
}

// readJournal reads the new entries of the journal files.
func (r *pass) readJournal() {
	dir, names, ok := r.locate()
	if !ok {
		return
	}
	files, ok := r.scan(dir, names)
	if !ok {
		return
	}
	if !r.cur.pos.valid {
		r.startAtTail(files)
		return
	}
	r.detectLost(files)
	r.readFiles(dir, files)
}

// startAtTail sets the position to the end of the newest file: a first start reads no history.
func (r *pass) startAtTail(files []fileInfo) {
	newest, ok := newestFile(files)
	if !ok {
		return
	}
	rt := newest.hdr.TailRealtime
	if rt.IsZero() {
		rt = newest.hdr.HeadRealtime
	}
	if rt.IsZero() {
		rt = r.start
	}
	r.cur.pos = place{valid: true, id: newest.hdr.SeqnumID, seq: newest.hdr.TailSeqnum, rt: rt}
	for _, f := range files {
		if !f.online {
			r.cur.markDone(f.hdr.FileID)
		}
	}
}

// newestFile returns the online file, else the file with the latest tail time.
func newestFile(files []fileInfo) (fileInfo, bool) {
	var best fileInfo
	found := false
	for _, f := range files {
		switch {
		case f.online:
			return f, true
		case !found || !f.hdr.TailRealtime.Before(best.hdr.TailRealtime):
			best, found = f, true
		}
	}
	return best, found
}

// detectLost opens a gap when no file of the saved seqnum ID is left, once per ID.
func (r *pass) detectLost(files []fileInfo) {
	id := r.cur.pos.id
	for _, f := range files {
		if f.hdr.SeqnumID == id {
			return
		}
	}
	if r.cur.lost && r.cur.lostID == id {
		return
	}
	r.cur.lost, r.cur.lostID = true, id
	r.openGap(r.cur.pos.rt)
}

// fileMode says how a file is read.
type fileMode struct {
	// moving: the file moves the position.
	moving bool
	// after: only entries with a greater sequence number are read.
	after uint64
}

// readFiles reads the files in the order of the plan: the files of the position's seqnum ID, then the older files of other IDs by head
// time, then those without a head time, then the online file of another ID.
func (r *pass) readFiles(dir fs.FS, files []fileInfo) {
	id, from := r.cur.pos.id, r.startPos.rt
	if r.readSameID(dir, files, id) && r.readOlder(dir, files, id, from) && r.readNoHead(dir, files, id) {
		r.readOnline(dir, files, id)
	}
}

// readSameID reads the files of the seqnum ID of the position, from the position.
func (r *pass) readSameID(dir fs.FS, files []fileInfo, id [16]byte) bool {
	same := selectFiles(files, func(f fileInfo) bool { return f.hdr.SeqnumID == id })
	slices.SortFunc(same, func(a, b fileInfo) int {
		return cmpUint(a.hdr.HeadSeqnum, b.hdr.HeadSeqnum)
	})
	r.detectVacuumed(same)
	for _, f := range same {
		if f.hdr.TailSeqnum <= r.cur.pos.seq {
			continue
		}
		if !r.readFile(dir, f, fileMode{moving: true, after: r.cur.pos.seq}) {
			return false
		}
	}
	return true
}

// detectVacuumed records the entries lost when files of the position's seqnum ID remain but none holds the position and one starts
// after it: one gap from the position's time to the head time of the first such file, once per position; sequence number 0 is no entry, so nothing before it can be lost. Without a readable head time
// the next readable kernel entry, else the end of the run, closes the gap. same is sorted by head sequence number.
func (r *pass) detectVacuumed(same []fileInfo) {
	pos := r.cur.pos
	if pos.seq == 0 || r.cur.vacuumed.same(pos) {
		return
	}
	var next *fileInfo
	for i := range same {
		f := &same[i]
		if f.hdr.HeadSeqnum <= pos.seq && pos.seq <= f.hdr.TailSeqnum {
			return
		}
		if next == nil && f.hdr.HeadSeqnum > pos.seq {
			next = f
		}
	}
	if next == nil {
		return
	}
	r.cur.vacuumed = pos
	if head := next.hdr.HeadRealtime; head.After(pos.rt) {
		r.emitGap(pos.rt, head)
		return
	}
	r.openGap(pos.rt)
}

// readOlder reads the archived and set-aside files of other IDs whose head time is not before the position's time, by head time.
func (r *pass) readOlder(dir fs.FS, files []fileInfo, id [16]byte, from time.Time) bool {
	others := selectFiles(files, func(f fileInfo) bool {
		return !f.online && f.hdr.SeqnumID != id && !f.hdr.HeadRealtime.IsZero() && !f.hdr.HeadRealtime.Before(from) && !r.isDone(f)
	})
	slices.SortFunc(others, func(a, b fileInfo) int {
		if c := a.hdr.HeadRealtime.Compare(b.hdr.HeadRealtime); c != 0 {
			return c
		}
		return strings.Compare(a.name, b.name)
	})
	for _, f := range others {
		mode := fileMode{moving: !f.hdr.HeadRealtime.Before(r.cur.pos.rt), after: r.cur.resume[f.hdr.FileID]}
		if !r.readFile(dir, f, mode) {
			return false
		}
	}
	return true
}

// readNoHead reads the files of other IDs without a head time, by name; they never move the position.
func (r *pass) readNoHead(dir fs.FS, files []fileInfo, id [16]byte) bool {
	others := selectFiles(files, func(f fileInfo) bool {
		return !f.online && f.hdr.SeqnumID != id && f.hdr.HeadRealtime.IsZero() && !r.isDone(f)
	})
	slices.SortFunc(others, func(a, b fileInfo) int { return strings.Compare(a.name, b.name) })
	for _, f := range others {
		if !r.readFile(dir, f, fileMode{after: r.cur.resume[f.hdr.FileID]}) {
			return false
		}
	}
	return true
}

// readOnline reads the online file when its seqnum ID is another, from its head, whatever its head time.
func (r *pass) readOnline(dir fs.FS, files []fileInfo, id [16]byte) bool {
	for _, f := range files {
		if f.online && f.hdr.SeqnumID != id && !r.readFile(dir, f, fileMode{moving: true}) {
			return false
		}
	}
	return true
}

// isDone reports whether the file was read to its end in this process.
func (r *pass) isDone(f fileInfo) bool {
	_, done := r.cur.done[f.hdr.FileID]
	return done
}

// selectFiles returns the files for which keep is true.
func selectFiles(files []fileInfo, keep func(fileInfo) bool) []fileInfo {
	var out []fileInfo
	for _, f := range files {
		if keep(f) {
			out = append(out, f)
		}
	}
	return out
}

// cmpUint compares two numbers.
func cmpUint(a, b uint64) int {
	switch {
	case a < b:
		return -1
	case a > b:
		return 1
	default:
		return 0
	}
}
