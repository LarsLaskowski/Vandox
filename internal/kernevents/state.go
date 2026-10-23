package kernevents

import (
	"encoding/hex"
	"maps"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
)

const (
	// driftEntries is the number of kernel entries the drift window holds.
	driftEntries = 64
	// driftSpan is how far before an entry the lines count that give the drift of the kernel clock.
	driftSpan = 600 * time.Second
	// maxTracked bounds the files the reading state remembers.
	maxTracked = 1024
	// uuidDash separates the groups of the text form of an ID.
	uuidDash = "-"
)

// place is a position in the journal: the last entry read of a file whose seqnum ID is id.
type place struct {
	valid bool
	id    [16]byte
	seq   uint64
	rt    time.Time
}

// same reports whether two positions name the same entry.
func (p place) same(o place) bool {
	return p.valid == o.valid && p.id == o.id && p.seq == o.seq
}

// positionOf turns the saved position into a place; a position that does not decode is none.
func positionOf(j *agentstate.JournalPosition) place {
	if j == nil {
		return place{}
	}
	id, ok := parseUUID(j.SeqnumID)
	if !ok {
		return place{}
	}
	return place{valid: true, id: id, seq: j.Seqnum, rt: j.Realtime.UTC()}
}

// journalPosition returns the saved form of p.
func (p place) journalPosition() *agentstate.JournalPosition {
	return &agentstate.JournalPosition{SeqnumID: formatUUID(p.id), Seqnum: p.seq, Realtime: p.rt.UTC()}
}

// parseUUID decodes the 36 character form of an ID.
func parseUUID(s string) ([16]byte, bool) {
	var id [16]byte
	if len(s) != 36 || s[8] != '-' || s[13] != '-' || s[18] != '-' || s[23] != '-' {
		return id, false
	}
	raw := s[0:8] + s[9:13] + s[14:18] + s[19:23] + s[24:36]
	if _, err := hex.Decode(id[:], []byte(raw)); err != nil {
		return id, false
	}
	return id, true
}

// formatUUID returns the 36 character, lower-case form of an ID.
func formatUUID(id [16]byte) string {
	h := hex.EncodeToString(id[:])
	return h[0:8] + uuidDash + h[8:12] + uuidDash + h[12:16] + uuidDash + h[16:20] + uuidDash + h[20:32]
}

// driftWindow holds the receipt time and lag of the last kernel lines of one boot.
type driftWindow struct {
	boot  [16]byte
	count int
	next  int
	mono  [driftEntries]time.Duration
	lag   [driftEntries]time.Duration
}

// delay returns the delay of a line received at mono with the given lag and keeps the line: the lag less the smallest lag among the
// line and the lines of the window within driftSpan before it. A line of another boot empties the window first.
func (w *driftWindow) delay(boot [16]byte, mono, lag time.Duration) time.Duration {
	if w.boot != boot {
		*w = driftWindow{boot: boot}
	}
	drift := lag
	for i := 0; i < w.count; i++ {
		if w.mono[i] <= mono && mono-w.mono[i] <= driftSpan {
			drift = min(drift, w.lag[i])
		}
	}
	w.mono[w.next], w.lag[w.next] = mono, lag
	w.next = (w.next + 1) % driftEntries
	w.count = min(w.count+1, driftEntries)
	return lag - drift
}

// readState is what a run reaches. It is kept in memory and committed as one unit at the start of the next run; a re-read starts
// from the committed copy. Only the position goes into the state file.
type readState struct {
	pos   place
	drift driftWindow
	// last is the time of the last readable kernel entry read.
	last time.Time
	// spanEnd is the time of the last readable kernel entry read by the run that made this state, the end of a span that is given up.
	spanEnd time.Time
	// done holds the files of other seqnum IDs read to their end, by file ID.
	done map[[16]byte]struct{}
	// resume holds, per file of another seqnum ID cut by a limit, the last sequence number read there.
	resume map[[16]byte]uint64
	// refused holds the files the reader refuses, by name, with the end of the last gap recorded for them.
	refused map[string]time.Time
	// lostID is the seqnum ID whose loss was recorded as a gap.
	lostID [16]byte
	lost   bool
	// vacuumed is the position for which the entries lost to a vacuum were recorded as a gap.
	vacuumed place
}

// fork returns a copy a run may change.
func (s *readState) fork() readState {
	c := *s
	c.done = maps.Clone(s.done)
	c.resume = maps.Clone(s.resume)
	c.refused = maps.Clone(s.refused)
	if c.done == nil {
		c.done = map[[16]byte]struct{}{}
	}
	if c.resume == nil {
		c.resume = map[[16]byte]uint64{}
	}
	if c.refused == nil {
		c.refused = map[string]time.Time{}
	}
	c.spanEnd = time.Time{}
	return c
}

// markDone records that a file was read to its end.
func (s *readState) markDone(id [16]byte) {
	delete(s.resume, id)
	if len(s.done) < maxTracked {
		s.done[id] = struct{}{}
	}
}

// remember records the last sequence number read in a file that a limit cut.
func (s *readState) remember(id [16]byte, seq uint64) {
	if _, known := s.resume[id]; known || len(s.resume) < maxTracked {
		s.resume[id] = seq
	}
}
