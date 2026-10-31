package logship

import (
	"crypto/sha256"
	"encoding/hex"
	"slices"
	"strings"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/logstages"
)

const (
	// uuidDash separates the groups of the text form of an ID.
	uuidDash = "-"
	// uuidLen is the length of the text form of an ID.
	uuidLen = 36
)

// parseUUID decodes the 36 character form of an ID.
func parseUUID(s string) ([16]byte, bool) {
	var id [16]byte
	if len(s) != uuidLen || s[8] != '-' || s[13] != '-' || s[18] != '-' || s[23] != '-' {
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
	return strings.Join([]string{h[0:8], h[8:12], h[12:16], h[16:20], h[20:32]}, uuidDash)
}

// pathHash returns the 64 lower-case hex digits of the SHA-256 of a configured path: the key of a saved file position.
func pathHash(path string) string {
	sum := sha256.Sum256([]byte(path))
	return hex.EncodeToString(sum[:])
}

// restore builds the committed state from the saved positions: the positions of the configured files only.
func restore(logs *agentstate.LogPositions, files []FileSpec) *state {
	s := &state{journal: map[[16]byte]*jpos{}, files: make([]fileState, len(files))}
	if logs == nil {
		return s
	}
	for _, j := range logs.Journal {
		if id, ok := parseUUID(j.SeqnumID); ok {
			s.journal[id] = &jpos{seq: j.Seqnum, rt: j.Realtime.UTC()}
		}
	}
	for i, spec := range files {
		hash := pathHash(spec.Path)
		for _, p := range logs.Files {
			if p.PathSHA256 == hash {
				s.files[i] = restoreFile(p)
				break
			}
		}
	}
	return s
}

// restoreFile builds the reading state of a file from its saved position.
func restoreFile(p agentstate.FilePosition) fileState {
	f := fileState{
		have:     true,
		id:       identity{dev: p.Device, ino: p.Inode},
		line:     lineState{start: p.Offset, read: p.Offset},
		headLen:  p.HeadLen,
		headSum:  p.HeadSHA256,
		lastTime: p.LastTime.UTC(),
	}
	if r := p.Renamed; r != nil {
		f.renamed = &renamedFile{id: identity{dev: r.Device, ino: r.Inode}, line: lineState{start: r.Offset, read: r.Offset}, size: r.Size}
	}
	return f
}

// positionsOf returns the positions to save for the state: at most 256 journal positions (those of IDs with an online file first, then
// the others, the newest first) and the position of every configured file that has one; nil when there is none.
func positionsOf(s *state, files []FileSpec) *agentstate.LogPositions {
	logs := &agentstate.LogPositions{}
	type entry struct {
		id [16]byte
		p  *jpos
	}
	entries := make([]entry, 0, len(s.journal))
	for id, p := range s.journal {
		entries = append(entries, entry{id, p})
	}
	slices.SortFunc(entries, func(a, b entry) int {
		switch {
		case a.p.online != b.p.online:
			if a.p.online {
				return -1
			}
			return 1
		case !a.p.rt.Equal(b.p.rt):
			return b.p.rt.Compare(a.p.rt)
		default:
			return slices.Compare(a.id[:], b.id[:])
		}
	})
	for _, e := range entries[:min(len(entries), agentstate.MaxLogJournals)] {
		seq, rt := e.p.seq, e.p.rt
		if held, ok := heldFrom(s, journalKey(e.id)); ok {
			seq, rt = held.At, held.Before // a source that holds lines is saved before its oldest held line
		}
		logs.Journal = append(logs.Journal, agentstate.JournalPosition{SeqnumID: formatUUID(e.id), Seqnum: seq, Realtime: rt.UTC()})
	}
	for i := range s.files {
		if s.files[i].have {
			logs.Files = append(logs.Files, heldBack(s, i, filePosition(&s.files[i], files[i].Path)))
		}
	}
	if len(logs.Journal) == 0 && len(logs.Files) == 0 {
		return nil
	}
	return logs
}

// filePosition returns the saved form of the reading state of the file at path.
func filePosition(f *fileState, path string) agentstate.FilePosition {
	p := agentstate.FilePosition{
		PathSHA256: pathHash(path),
		Device:     f.id.dev,
		Inode:      f.id.ino,
		Offset:     f.line.start,
		HeadSHA256: f.headSum,
		HeadLen:    f.headLen,
		LastTime:   f.lastTime.UTC(),
	}
	if p.HeadSHA256 == "" {
		p.HeadSHA256 = pathHash("")
	}
	if r := f.renamed; r != nil {
		p.Renamed = &agentstate.RenamedFile{Device: r.id.dev, Inode: r.id.ino, Offset: r.line.start, Size: max(r.size, 0)}
	}
	return p
}

// sameLogs reports whether two sets of saved positions hold the same values.
func sameLogs(a, b *agentstate.LogPositions) bool {
	if a == nil || b == nil {
		return a == b
	}
	if len(a.Journal) != len(b.Journal) || len(a.Files) != len(b.Files) {
		return false
	}
	for i := range a.Journal {
		x, y := a.Journal[i], b.Journal[i]
		if x.SeqnumID != y.SeqnumID || x.Seqnum != y.Seqnum || !x.Realtime.Equal(y.Realtime) {
			return false
		}
	}
	for i := range a.Files {
		if !sameFile(a.Files[i], b.Files[i]) {
			return false
		}
	}
	return true
}

// sameFile reports whether two saved file positions are equal.
func sameFile(x, y agentstate.FilePosition) bool {
	if x.PathSHA256 != y.PathSHA256 || x.Device != y.Device || x.Inode != y.Inode || x.Offset != y.Offset || x.HeadSHA256 != y.HeadSHA256 ||
		x.HeadLen != y.HeadLen || !x.LastTime.Equal(y.LastTime) || (x.Renamed == nil) != (y.Renamed == nil) {
		return false
	}
	return x.Renamed == nil || *x.Renamed == *y.Renamed
}

// heldFrom returns the mark of the oldest line the stages of a source hold, and false when the source holds none.
func heldFrom(s *state, key stageKey) (logstages.Mark, bool) {
	if st := s.stages[key]; st != nil {
		return st.HeldFrom()
	}
	return logstages.Mark{}, false
}

// heldBack moves the saved position of file i before the oldest line its stages hold: in the renamed part or in the file at the path,
// as the line was read.
func heldBack(s *state, i int, p agentstate.FilePosition) agentstate.FilePosition {
	held, ok := heldFrom(s, fileKey(i))
	if !ok {
		return p
	}
	p.LastTime = held.Before.UTC()
	switch {
	case held.Renamed && p.Renamed != nil:
		p.Renamed.Offset = int64(held.At) // #nosec G115 -- a file offset
	case !held.Renamed:
		p.Offset = int64(held.At) // #nosec G115 -- a file offset
	}
	return p
}
