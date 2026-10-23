package logship

import (
	"maps"
	"os"
	"slices"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/syslogline"
)

// jpos is the position in the files of one journal sequence number ID: the last entry read and the time of the last readable entry.
type jpos struct {
	seq uint64
	rt  time.Time
	// online tells that a file of the ID is the online file; it decides which positions are saved.
	online bool
}

// lineState is where the reading of a file stands in its lines.
type lineState struct {
	// start is the offset of the first byte of the line that is not shipped yet; it is the offset that is saved.
	start int64
	// read is the offset up to which the file was read; it is more than start while the first bytes of an unfinished line are held.
	read int64
	// held are the bytes of the unfinished line read so far, at most lineKeep, and cut tells that more of the line followed.
	held []byte
	cut  bool
}

// renamedFile is a rotated file that is still read.
type renamedFile struct {
	f    *os.File
	id   identity
	line lineState
	// size is the size seen at the last run, -1 when it is not known yet.
	size int64
}

// fileState is the reading state of one configured file.
type fileState struct {
	// have tells that the file has a position; waiting that the file was missing when it was looked for first, so that it is read from
	// its start once it appears.
	have, waiting bool
	id            identity
	line          lineState
	headLen       int
	headSum       string
	// lastTime is the capture time of the last line read from the file, the run start when the position was taken.
	lastTime time.Time
	// cur is the descriptor of the file kept across runs, nil after a restart until the file is opened.
	cur     *os.File
	renamed *renamedFile
	// clock resolves the year-less times of the file; it keeps the previous instant.
	clock *syslogline.Clock
}

// state is what the collector knows about its sources: the committed state, or the fork a run works on.
type state struct {
	journal map[[16]byte]*jpos
	files   []fileState
	// last is the latest time of an entry or line the run read, the end of a span that is given up.
	last time.Time
	// refused holds the journal files the reader refuses, by name, with the end of the last gap recorded for them.
	refused map[string]time.Time
}

// clone returns a copy a run may change. The descriptors are shared; the files of the copy that are not in the original are the run's.
func (s *state) clone() *state {
	c := &state{journal: make(map[[16]byte]*jpos, len(s.journal)), files: slices.Clone(s.files), refused: maps.Clone(s.refused)}
	if c.refused == nil {
		c.refused = map[string]time.Time{}
	}
	for id, p := range s.journal {
		cp := *p
		c.journal[id] = &cp
	}
	for i := range c.files {
		f := &c.files[i]
		f.line.held = slices.Clone(f.line.held)
		if f.clock != nil {
			k := *f.clock
			f.clock = &k
		}
		if f.renamed != nil {
			r := *f.renamed
			r.line.held = slices.Clone(r.line.held)
			f.renamed = &r
		}
	}
	return c
}

// newestJournal returns the time of the newest journal position, and whether there is a position.
func (s *state) newestJournal() (time.Time, bool) {
	var newest time.Time
	for _, p := range s.journal {
		if p.rt.After(newest) {
			newest = p.rt
		}
	}
	return newest, len(s.journal) > 0
}

// committedTime returns the time of the newest position of the state: the point from which a span that is given up starts.
func (s *state) committedTime() time.Time {
	newest, _ := s.newestJournal()
	for i := range s.files {
		if s.files[i].have && s.files[i].lastTime.After(newest) {
			newest = s.files[i].lastTime
		}
	}
	return newest
}

// descriptors returns the open files of the state.
func (s *state) descriptors() map[*os.File]struct{} {
	set := map[*os.File]struct{}{}
	for i := range s.files {
		if f := s.files[i].cur; f != nil {
			set[f] = struct{}{}
		}
		if r := s.files[i].renamed; r != nil && r.f != nil {
			set[r.f] = struct{}{}
		}
	}
	return set
}

// release closes the descriptors of s that none of the states in keep holds.
func (s *state) release(keep ...*state) {
	if s == nil {
		return
	}
	held := map[*os.File]struct{}{}
	for _, k := range keep {
		if k != nil {
			maps.Copy(held, k.descriptors())
		}
	}
	for f := range s.descriptors() {
		if _, ok := held[f]; !ok {
			_ = f.Close()
		}
	}
}
