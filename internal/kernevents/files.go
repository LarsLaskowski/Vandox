package kernevents

import (
	"errors"
	"io"
	"io/fs"
	"strings"

	"github.com/LarsLaskowski/Vandox/internal/journal"
)

const (
	// onlineName is the name of the journal file journald is writing to.
	onlineName = "system.journal"
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

// openFile is an open journal file and the handle behind it.
type openFile struct {
	f    *journal.File
	file fs.File
}

// close releases the handle.
func (o *openFile) close() {
	_ = o.file.Close()
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

// size returns the current size of the file.
func (o *openFile) size() (int64, error) {
	st, err := o.file.Stat()
	if err != nil {
		return 0, err
	}
	return st.Size(), nil
}

// header returns the header of the named file.
func header(dir fs.FS, name string) (journal.Header, error) {
	o, err := openJournal(dir, name)
	if err != nil {
		return journal.Header{}, err
	}
	defer o.close()
	return o.f.Header(), nil
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

// locate finds the journal directory of this machine and lists its files. The first root that has the directory wins.
func (r *pass) locate() (fs.FS, []string, bool) {
	id, err := journal.MachineDir(r.k.src.Etc)
	if err != nil {
		r.problem = problemMachineID
		return nil, nil, false
	}
	problem := problemNotFound
	for _, root := range r.k.src.Journal {
		dir, err := fs.Sub(root, id)
		if err != nil {
			problem = problemDirectory
			continue
		}
		names, err := journal.ListFiles(dir)
		switch {
		case err == nil:
			return dir, names, true
		case !errors.Is(err, fs.ErrNotExist):
			problem = problemDirectory
		}
	}
	r.problem = problem
	return nil, nil, false
}

// scan reads the headers of the listed files. A file the reader refuses is recorded as a gap and left out; a file that cannot be
// opened stops the run unless it has failed for too long. It returns false when the run must stop.
func (r *pass) scan(dir fs.FS, names []string) ([]fileInfo, bool) {
	files := make([]fileInfo, 0, len(names))
	for _, name := range names {
		hdr, err := header(dir, name)
		switch {
		case err == nil:
			files = append(files, fileInfo{name: name, hdr: hdr, online: name == onlineName})
		case errors.Is(err, fs.ErrNotExist):
			// vacuumed or rotated since the listing; the lost-ID rule covers what it held
		case !r.unreadableFile(name, err):
			return files, false
		}
	}
	return files, true
}

// unreadableFile handles a file whose header cannot be read and reports whether the run goes on without it.
func (r *pass) unreadableFile(name string, err error) bool {
	info := fileInfo{name: name, online: name == onlineName}
	r.noteProblem(info, err)
	if refusal(err) {
		r.refuse(info)
		return true
	}
	if _, met := r.cur.refused[name]; met {
		return true
	}
	if r.countFailure(name) {
		r.refuse(info)
		return true
	}
	return false
}

// refuse records the gap for a file the reader cannot read: one the first time this process meets it, and for the online file
// another one for every refusedGapEvery after the last, so that a journal the reader cannot read stays covered.
func (r *pass) refuse(f fileInfo) {
	last, met := r.cur.refused[f.name]
	switch {
	case !met:
		from := r.startPos.rt
		if from.IsZero() {
			from = r.start
		}
		r.emit(newGap(from, r.start, r.start), r.start)
		r.refusedGap = true
	case f.online && r.start.Sub(last) >= refusedGapEvery:
		r.emit(newGap(last, r.start, r.start), r.start)
	default:
		return
	}
	if met || len(r.cur.refused) < maxTracked {
		r.cur.refused[f.name] = r.start
	}
}
