// Package importer imports log files, directories and archives into the store.
package importer

import (
	"archive/tar"
	"bufio"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"hash"
	"io"
	"time"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store"
	"github.com/LarsLaskowski/Vandox/internal/logparse"
	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	// MaxFiles is the number of entries one run handles: directory entries of any kind and tar headers except
	// PAX global headers; enforced while scanning.
	MaxFiles = 20000
	// MaxPathBytes is the longest relative path or entry name.
	MaxPathBytes = 1024
	// DefaultBatchRecords is the number of records per batch.
	DefaultBatchRecords = 2000
	// DefaultBatchBytes is the number of input bytes per batch.
	DefaultBatchBytes = 4 << 20
	// MaxProblems is the number of problems kept per file.
	MaxProblems = 10
	// ProgressLines is the number of lines between EventFileProgress.
	ProgressLines = 100000
	// DefaultProgressBytes is the number of decompressed bytes between EventScanProgress while a file is hashed.
	DefaultProgressBytes = 64 << 20
)

// ErrTooManyFiles is wrapped by the error Run returns when the input holds more than MaxFiles entries.
var ErrTooManyFiles = errors.New("importer: too many files")

// Store is the part of the database the importer writes to.
type Store interface {
	store.Writer
	store.ImportTracker
}

// Options configure Run.
type Options struct {
	Parsers       *logparse.Registry // required
	Store         Store              // required
	Now           func() time.Time   // required; times are converted to UTC
	Progress      func(Progress)     // optional; called synchronously
	BatchRecords  int                // 0: DefaultBatchRecords; at most store.MaxBatchRecords
	BatchBytes    int64              // 0: DefaultBatchBytes
	ProgressBytes int64              // 0: DefaultProgressBytes
}

// Outcome is what happened to a file.
type Outcome string

const (
	// OutcomeImported means the file's records were stored in this run.
	OutcomeImported Outcome = "imported"
	// OutcomeAlreadyImported means the file's content was imported completely before.
	OutcomeAlreadyImported Outcome = "already_imported"
	// OutcomeUnrecognized means the file was listed but not imported.
	OutcomeUnrecognized Outcome = "unrecognized"
	// OutcomeFailed means the file could not be imported.
	OutcomeFailed Outcome = "failed"
)

// Problem is input a parser skipped or a record the store would refuse.
type Problem struct {
	Line   int64 // 1-based, 0 when unknown
	Reason string
}

// FileResult is the result of one file.
type FileResult struct {
	Path         string // display path: relative to the root; archive entries as "<archive path>:<entry name>"
	SourceType   string // "" when not recognized
	Outcome      Outcome
	Reason       string // why unrecognized or failed
	Lines        int64
	Records      int64 // stored in this run
	Skipped      int64
	ResumedAfter int64 // records an earlier, interrupted run had stored
	First, Last  time.Time
	Problems     []Problem // at most MaxProblems
}

// Summary is the result of a run.
type Summary struct {
	Root                    string
	Started, Finished       time.Time
	Files                   []FileResult // every file found, in input order
	Lines, Records, Skipped int64
	First, Last             time.Time // capture-time range of the records stored; zero when none
	Interrupted             bool
}

// Count returns the number of files with outcome o.
func (s *Summary) Count(o Outcome) int {
	n := 0
	for i := range s.Files {
		if s.Files[i].Outcome == o {
			n++
		}
	}
	return n
}

// Event names a progress event.
type Event string

const (
	// EventScanProgress is reported in pass 1 with Path and Bytes of a file being hashed.
	EventScanProgress Event = "scan_progress"
	// EventScanned is reported when pass 1 is done.
	EventScanned Event = "scanned"
	// EventFileStarted is reported when pass 2 starts a file.
	EventFileStarted Event = "file_started"
	// EventFileProgress is reported every ProgressLines lines of a file.
	EventFileProgress Event = "file_progress"
	// EventFileFinished is reported when a file is finished or listed.
	EventFileFinished Event = "file_finished"
)

// Progress reports the state of a run.
type Progress struct {
	Event          Event
	Files, Pending int // EventScanned: files found, files to import
	Path           string
	SourceType     string
	Lines, Records int64
	Bytes          int64       // EventScanProgress: decompressed bytes of the file read so far
	Result         *FileResult // EventFileFinished
}

// Run imports root and returns the summary; the error reports what stopped the run (the summary covers what
// was done until then).
func Run(ctx context.Context, root string, opts Options) (Summary, error) {
	if err := opts.normalize(); err != nil {
		return Summary{}, err
	}
	sum := Summary{Root: root, Started: opts.Now().UTC()}
	src, err := openSource(root)
	if err != nil {
		sum.Finished = opts.Now().UTC()
		return sum, fmt.Errorf("importer: opening the input: %w", err)
	}
	defer func() { _ = src.root.Close() }()
	r := &runner{src: src, opts: opts}
	err = r.run(ctx)
	sum.Interrupted = err != nil && ctx.Err() != nil && errors.Is(err, ctx.Err())
	r.summarize(&sum)
	sum.Finished = opts.Now().UTC()
	return sum, err
}

// normalize checks the options and replaces zero bounds by their defaults.
func (o *Options) normalize() error {
	switch {
	case o.Parsers == nil || o.Store == nil || o.Now == nil:
		return errors.New("importer: parsers, store and clock are required")
	case o.BatchRecords < 0 || o.BatchRecords > store.MaxBatchRecords:
		return fmt.Errorf("importer: BatchRecords must be between 0 and %d", store.MaxBatchRecords)
	case o.BatchBytes < 0 || o.ProgressBytes < 0:
		return errors.New("importer: BatchBytes and ProgressBytes must not be negative")
	}
	if o.BatchRecords == 0 {
		o.BatchRecords = DefaultBatchRecords
	}
	if o.BatchBytes == 0 {
		o.BatchBytes = DefaultBatchBytes
	}
	if o.ProgressBytes == 0 {
		o.ProgressBytes = DefaultProgressBytes
	}
	return nil
}

// runner holds the state of a run.
type runner struct {
	src   source
	opts  Options
	items []found
	fatal error // ends the run: the context is done or the store failed
}

// run executes pass 1 and pass 2.
func (r *runner) run(ctx context.Context) error {
	items, err := scan(ctx, r.src, r.opts)
	r.items = items
	if err != nil {
		return err
	}
	pending := 0
	for i := range items {
		if items[i].parser != nil {
			pending++
		}
	}
	r.report(Progress{Event: EventScanned, Files: len(items), Pending: pending})
	return r.importAll(ctx)
}

// report passes p to the progress callback, if any.
func (r *runner) report(p Progress) {
	if r.opts.Progress != nil {
		r.opts.Progress(p)
	}
}

// summarize fills the files and totals of sum from the files that have an outcome.
func (r *runner) summarize(sum *Summary) {
	for i := range r.items {
		res := r.items[i].result
		if res.Outcome == "" {
			continue
		}
		sum.Files = append(sum.Files, res)
		sum.Lines += res.Lines
		sum.Records += res.Records
		sum.Skipped += res.Skipped
		sum.First, sum.Last = widen(sum.First, sum.Last, res.First, res.Last)
	}
}

// widen extends the range [first, last] by [from, to]; a zero time is no bound.
func widen(first, last, from, to time.Time) (time.Time, time.Time) {
	if !from.IsZero() && (first.IsZero() || from.Before(first)) {
		first = from
	}
	if !to.IsZero() && to.After(last) {
		last = to
	}
	return first, last
}

// errDone stops the iteration of an archive once its last wanted entry has been handled.
var errDone = errors.New("importer: archive done")

// importAll imports the recognized files in input order. The entries of one archive are imported in a single
// pass over the archive.
func (r *runner) importAll(ctx context.Context) error {
	for i := 0; i < len(r.items); {
		it := &r.items[i]
		switch {
		case it.parser == nil:
			i++
		case it.loc.entry < 0:
			r.importPlain(ctx, it)
			i++
		default:
			i = r.importArchive(ctx, i)
		}
		if r.fatal != nil {
			return r.fatal
		}
	}
	return nil
}

// importPlain imports the file it, a file of the root that may be gzip-compressed.
func (r *runner) importPlain(ctx context.Context, it *found) {
	file, ok := r.begin(ctx, it)
	if !ok {
		return
	}
	f, err := openRegular(r.src.root, it.loc.fsPath)
	if err != nil {
		r.failFile(it, reasonOf(err))
		return
	}
	defer func() { _ = f.Close() }()
	content, err := decompress(f, it.loc.gzip)
	if err != nil {
		r.failFile(it, reasonOf(err))
		return
	}
	r.importContent(ctx, it, file, content)
}

// importArchive imports the recognized entries of the archive that holds r.items[first] in one pass over the
// archive and returns the index of the first item after the archive's items.
func (r *runner) importArchive(ctx context.Context, first int) int {
	end := first
	var group []int
	for end < len(r.items) && r.items[end].loc.fsPath == r.items[first].loc.fsPath && r.items[end].loc.entry >= 0 {
		if r.items[end].parser != nil {
			group = append(group, end)
		}
		end++
	}
	pos, err := r.readArchive(ctx, r.items[first].loc, group)
	if r.fatal == nil && ctx.Err() != nil {
		r.fatal = ctx.Err()
	}
	if r.fatal != nil {
		return end
	}
	reason := "the archive changed while it was imported"
	if err != nil {
		reason = reasonOf(err)
	}
	for _, i := range group[pos:] {
		r.failFile(&r.items[i], reason)
	}
	return end
}

// readArchive opens the archive at loc and imports the entries of group, which are in ascending order of
// their ordinals. It returns how many entries of group were handled and the error that ended the reading
// before that, if any.
func (r *runner) readArchive(ctx context.Context, loc location, group []int) (int, error) {
	f, err := openRegular(r.src.root, loc.fsPath)
	if err != nil {
		return 0, err
	}
	defer func() { _ = f.Close() }()
	raw, err := decompress(f, loc.archiveGzip)
	if err != nil {
		return 0, err
	}
	pos := 0
	err = eachEntry(ctx, raw, func(index int, _ *tar.Header, content io.Reader) error {
		it := &r.items[group[pos]]
		if it.loc.entry != index {
			return nil
		}
		pos++
		r.importEntry(ctx, it, content)
		if r.fatal != nil {
			return r.fatal
		}
		if pos == len(group) {
			return errDone
		}
		return nil
	})
	if errors.Is(err, errDone) || r.fatal != nil {
		err = nil
	}
	return pos, err
}

// importEntry imports the archive entry it, whose raw content is content.
func (r *runner) importEntry(ctx context.Context, it *found, content io.Reader) {
	file, ok := r.begin(ctx, it)
	if !ok {
		return
	}
	body, err := decompress(content, it.loc.gzip)
	if err != nil {
		r.failFile(it, reasonOf(err))
		return
	}
	r.importContent(ctx, it, file, body)
}

// decompress returns r, or its gzip decompression when gzipped is set.
func decompress(r io.Reader, gzipped bool) (io.Reader, error) {
	if !gzipped {
		return r, nil
	}
	return gzip.NewReader(bufio.NewReader(r))
}

// begin asks the store about the content of it. It returns the stored state and true when the content is to be
// imported (again); otherwise the result of it is final. A store error ends the run.
func (r *runner) begin(ctx context.Context, it *found) (store.ImportFile, bool) {
	file, err := r.opts.Store.BeginImport(ctx, store.ImportFileStart{
		SHA256:     it.sum,
		Size:       it.size,
		Name:       it.result.Path,
		FileName:   it.file.Name,
		ModTime:    it.file.ModTime,
		SourceType: it.parser.Type(),
		StartedAt:  r.opts.Now().UTC(),
	})
	switch {
	case err != nil:
		r.failFile(it, r.storeReason(ctx, err))
		return file, false
	case file.Complete:
		it.result.Outcome = OutcomeAlreadyImported
		r.finished(it)
		return file, false
	case file.SourceType != it.parser.Type():
		r.failFile(it, fmt.Sprintf("the content was imported before as source type %q", file.SourceType))
		return file, false
	}
	return file, true
}

// failFile ends the import of it as failed with reason.
func (r *runner) failFile(it *found, reason string) {
	it.result.Outcome = OutcomeFailed
	it.result.Reason = reason
	r.finished(it)
}

// finished reports it as finished.
func (r *runner) finished(it *found) {
	res := it.result
	r.report(Progress{Event: EventFileFinished, Path: res.Path, SourceType: res.SourceType, Lines: res.Lines, Records: res.Records, Result: &res})
}

// errChanged is the failure of a file whose hashed content changed between the two passes.
var errChanged = errors.New("the content changed while it was imported")

// importContent parses the content of it and stores its records.
func (r *runner) importContent(ctx context.Context, it *found, file store.ImportFile, content io.Reader) {
	res := &it.result
	res.ResumedAfter = file.Records
	r.report(Progress{Event: EventFileStarted, Path: res.Path, SourceType: res.SourceType})
	cr := &contentReader{check: ctx.Err, r: io.LimitReader(content, it.size), h: sha256.New(), next: ProgressLines}
	cr.emit = func(lines int64) {
		r.report(Progress{Event: EventFileProgress, Path: res.Path, SourceType: res.SourceType, Lines: lines, Records: res.Records})
	}
	write := func(b store.Batch) error {
		_, err := r.opts.Store.WriteBatch(ctx, b)
		return err
	}
	em := &emitter{r: r, write: write, it: it, cr: cr, step: store.ImportStep{FileID: file.ID, Done: file.Records}, drop: file.Records}
	parseErr := it.parser.Parse(ctx, logparse.File{Name: file.FileName, ModTime: file.ModTime}, cr, em)
	reason := r.judge(ctx, em, parseErr)
	res.Lines = cr.lines()
	if reason != "" {
		r.failFile(it, reason)
		return
	}
	res.Outcome = OutcomeImported
	r.finished(it)
}

// judge finishes the import of a content after Parse returned parseErr and returns why it failed, or "" when it
// was imported completely.
func (r *runner) judge(ctx context.Context, em *emitter, parseErr error) string {
	if em.err == nil {
		parseErr = r.settle(ctx, em, parseErr)
	}
	switch {
	case em.err != nil:
		return r.storeReason(ctx, em.err)
	case parseErr == nil:
		return ""
	case ctx.Err() != nil:
		r.fatal = ctx.Err()
		return "interrupted"
	}
	return parseErr.Error()
}

// settle stores what the emitter still holds: after a parser error what was parsed before it, otherwise, once
// the whole hashed content was read again unchanged, the last batch that completes the file. It returns the
// error that failed the file, if any.
func (r *runner) settle(ctx context.Context, em *emitter, parseErr error) error {
	if parseErr != nil {
		if ctx.Err() == nil {
			_ = em.flush(false)
		}
		return parseErr
	}
	if err := em.verify(); err != nil {
		return err
	}
	_ = em.flush(true)
	return nil
}

// storeReason returns the reason for a failed store call. A done context is an interruption and a store error
// other than a conflict ends the run.
func (r *runner) storeReason(ctx context.Context, err error) string {
	switch {
	case ctx.Err() != nil:
		r.fatal = ctx.Err()
		return "interrupted"
	case errors.Is(err, store.ErrImportConflict):
		return "the import state changed (another import of the same content is running?)"
	}
	r.fatal = fmt.Errorf("importer: writing to the database: %w", err)
	return "database error"
}

// emitter receives the records of a parser, drops those an earlier run stored and writes the rest in batches.
type emitter struct {
	r          *runner
	write      func(store.Batch) error // stores one batch with the context of the run
	it         *found
	cr         *contentReader
	step       store.ImportStep // FileID and Done, the number of records stored so far
	drop       int64            // valid records still to drop
	buf        []model.Record
	startBytes int64 // input consumed when the first buffered record was added
	err        error // the first error of the store; sticky
}

// Record validates rec, drops it or buffers it and flushes a full batch.
func (e *emitter) Record(rec model.Record) error {
	if e.err != nil {
		return e.err
	}
	if err := store.CheckImportRecord(&rec); err != nil {
		e.Skip(0, "record refused: "+err.Error())
		return nil
	}
	if e.drop > 0 {
		e.drop--
		return nil
	}
	if len(e.buf) == 0 {
		e.startBytes = e.cr.n
	}
	e.buf = append(e.buf, rec)
	if len(e.buf) >= e.r.opts.BatchRecords || e.cr.n-e.startBytes >= e.r.opts.BatchBytes {
		return e.flush(false)
	}
	return nil
}

// Skip counts a skipped line and keeps the first MaxProblems reasons.
func (e *emitter) Skip(line int64, reason string) {
	res := &e.it.result
	res.Skipped++
	if len(res.Problems) < MaxProblems {
		res.Problems = append(res.Problems, Problem{Line: line, Reason: reason})
	}
}

// flush writes the buffered records, completing the file when complete is set. It writes nothing when there is
// nothing to write and the file is not completed.
func (e *emitter) flush(complete bool) error {
	if e.err != nil {
		return e.err
	}
	if len(e.buf) == 0 && !complete {
		return nil
	}
	step := e.step
	step.Complete = complete
	batch := store.Batch{ReceivedAt: e.r.opts.Now().UTC(), Records: e.buf, Import: &step}
	if err := e.write(batch); err != nil {
		e.err = err
		return err
	}
	res := &e.it.result
	for i := range e.buf {
		at := e.buf[i].CapturedAt
		res.First, res.Last = widen(res.First, res.Last, at, at)
	}
	res.Records += int64(len(e.buf))
	e.step.Done += int64(len(e.buf))
	clear(e.buf)
	e.buf = e.buf[:0]
	return nil
}

// verify reads the rest of the hashed content, which the parser may have left unread, and returns an error
// unless exactly the content hashed in pass 1 was read.
func (e *emitter) verify() error {
	if _, err := io.Copy(io.Discard, e.cr); err != nil {
		return err
	}
	if e.cr.n < e.it.size || !bytes.Equal(e.cr.h.Sum(nil), e.it.sum[:]) {
		return errChanged
	}
	return nil
}

// contentReader hashes and counts what the parser reads, reports every ProgressLines lines and stops with the
// context.
type contentReader struct {
	check    func() error // reports the error of the done context
	r        io.Reader
	h        hash.Hash
	n        int64 // bytes read
	newlines int64
	last     byte
	next     int64 // the line count of the next progress event
	emit     func(lines int64)
}

func (c *contentReader) Read(p []byte) (int, error) {
	if err := c.check(); err != nil {
		return 0, err
	}
	n, err := c.r.Read(p)
	if n > 0 {
		c.h.Write(p[:n])
		c.n += int64(n)
		c.newlines += int64(bytes.Count(p[:n], []byte{'\n'}))
		c.last = p[n-1]
		for c.newlines >= c.next {
			c.emit(c.next)
			c.next += ProgressLines
		}
	}
	return n, err
}

// lines returns the number of lines read: the newlines plus one for a last line without one.
func (c *contentReader) lines() int64 {
	if c.n > 0 && c.last != '\n' {
		return c.newlines + 1
	}
	return c.newlines
}
