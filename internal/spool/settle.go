package spool

import (
	"log/slog"
	"os"
)

// doomedFile is a trusted data file to delete once the gaps for its numbers are written.
type doomedFile struct {
	name string
	id   fileID
}

// settle brings the files found at Open into their final state: damaged and foreign files are doomed, files without a
// trailer are cut at their last good frame and sealed, and the newest file of this context becomes the active one.
func (s *Spool) settle(ents []*found) ([]doomedFile, error) {
	var doomed []doomedFile
	var keep []*found
	var foreign uint64
	for _, e := range ents {
		switch e.kind {
		case kindRefused:
			s.warnDamaged(dataName(e.num), errDamageRefused)
		case kindOther:
			foreign += e.res.count
			doomed = append(doomed, doom(e))
		case kindRejected, kindWhole:
			s.warnDamaged(e.seg.name(), e.reason)
			doomed = append(doomed, doom(e))
		default:
			if e.kind == kindPartial {
				s.warnDamaged(e.seg.name(), e.reason)
			}
			keep = append(keep, e)
		}
	}
	if foreign > 0 {
		s.log.Warn("spool records of another agent dropped", slog.Uint64("records", foreign))
	}
	for i, e := range keep {
		if err := s.keepFile(e, i == len(keep)-1, &doomed); err != nil {
			return nil, err
		}
	}

	return doomed, nil
}

// doom names a trusted file for deletion.
func doom(e *found) doomedFile {
	return doomedFile{name: e.seg.name(), id: e.seg.id}
}

// canAppend reports whether the file can take the records of this process.
func (s *Spool) canAppend(e *found) bool {
	seg := e.seg
	if !seg.ident.equal(&s.ident) || seg.size >= s.fileMax || (e.kind == kindPartial && !e.res.torn) {
		return false
	}

	return seg.count > 0 || s.next == seg.num
}

// keepFile makes one good file part of the spool.
func (s *Spool) keepFile(e *found, newest bool, doomed *[]doomedFile) error {
	seg := e.seg
	switch {
	case e.kind == kindEmpty && (!newest || !s.canAppend(e)):
		*doomed = append(*doomed, doom(e))

		return nil
	case seg.sealed:
		s.files = append(s.files, seg)

		return nil
	case newest && s.canAppend(e):
		s.files = append(s.files, seg)

		return s.activate(e)
	}
	s.files = append(s.files, seg)

	return s.sealFound(e)
}

// openRW opens a trusted file for writing, which must still be the file that was examined.
func (s *Spool) openRW(seg *segFile) (*os.File, error) {
	f, st, err := s.dir.openEntry(seg.name(), true)
	if err != nil {
		return nil, writeError(err)
	}
	if st.Dev != seg.id.dev || st.Ino != seg.id.ino {
		_ = f.Close()

		return nil, writeError(errDamageRefused)
	}

	return f, nil
}

// activate opens the file for appending, cut back to its last good frame.
func (s *Spool) activate(e *found) error {
	f, err := s.openRW(e.seg)
	if err != nil {
		return err
	}
	if e.res.goodEnd < e.seg.size {
		if err := f.Truncate(e.res.goodEnd); err != nil {
			_ = f.Close()

			return writeError(err)
		}
		e.seg.size = e.res.goodEnd
	}
	s.act, s.actF = e.seg, f
	s.gen++

	return nil
}

// sealFound cuts the file at its last good frame and seals it with a trailer.
func (s *Spool) sealFound(e *found) error {
	f, err := s.openRW(e.seg)
	if err != nil {
		return err
	}
	defer func() { _ = f.Close() }()
	if e.res.goodEnd < e.seg.size {
		if err := f.Truncate(e.res.goodEnd); err != nil {
			return writeError(err)
		}
		e.seg.size = e.res.goodEnd
	}

	return s.writeTrailer(f, e.seg)
}

// writeTrailer seals the file with the trailer of its figures and syncs it.
func (s *Spool) writeTrailer(f *os.File, seg *segFile) error {
	if err := writeAll(f, appendTrailer(nil, seg.summary()), seg.size); err != nil {
		_ = f.Truncate(seg.size)

		return writeError(err)
	}
	seg.size += trailerLen
	seg.sealed, seg.sumLast, seg.sumCount = true, seg.last, seg.count
	s.gen++
	if err := f.Sync(); err != nil {
		return writeError(err)
	}

	return nil
}

// removeDoomed deletes the files that were damaged as a whole. A file that cannot be deleted is found again at the next
// start.
func (s *Spool) removeDoomed(doomed []doomedFile) {
	for _, d := range doomed {
		id := d.id
		if s.dir.removeTrusted(d.name, &id) == nil {
			s.dirNeed = true
			s.gen++
		}
	}
}
