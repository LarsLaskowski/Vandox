package importer

import (
	"archive/tar"
	"bufio"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"hash"
	"io"
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"slices"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/logparse"
)

// dirChunk is the number of directory entries read at a time while scanning.
const dirChunk = 256

// Reasons a file is listed without being imported.
const (
	reasonSymlink    = "symbolic link (not followed)"
	reasonHardLink   = "hard link (the content is in the linked entry)"
	reasonNotRegular = "not a regular file"
	reasonEmpty      = "empty"
	reasonTwice      = "compressed twice"
	reasonNested     = "archive inside an archive (not opened)"
	reasonNoParser   = "no parser recognized the file"
	reasonTooLong    = "path too long"
)

// source is the opened import root.
type source struct {
	root *os.Root // the root directory, or the directory holding a root file
	file string   // the root file's name in root; "" when the root is a directory
}

// openSource resolves path once with filepath.EvalSymlinks, checks it with os.Lstat and opens it: a directory as
// the root, a regular file through the directory holding it. Anything else is an error, and nothing is opened
// before Lstat reported a directory or a regular file. The caller closes source.root.
func openSource(path string) (source, error) {
	if path == "" {
		return source{}, errors.New("empty path")
	}
	resolved, err := filepath.EvalSymlinks(path)
	if err != nil {
		return source{}, err
	}
	info, err := os.Lstat(resolved)
	if err != nil {
		return source{}, err
	}
	switch {
	case info.IsDir():
		root, err := os.OpenRoot(resolved)
		return source{root: root}, err
	case info.Mode().IsRegular():
		root, err := os.OpenRoot(filepath.Dir(resolved))
		return source{root: root, file: filepath.Base(resolved)}, err
	}
	return source{}, errors.New("neither a directory nor a regular file")
}

// location tells where the content of a found file is read again.
type location struct {
	fsPath      string // the file, or the archive containing the entry; a name in source.root
	entry       int    // ordinal of the entry among the archive's headers; -1 for a plain file
	archiveGzip bool   // the archive is gzip-compressed
	gzip        bool   // the file or entry content is gzip-compressed
}

// found is one file of pass 1.
type found struct {
	result FileResult // Path; Outcome and Reason when it is not imported
	file   logparse.File
	loc    location
	parser logparse.Parser // nil when not to be imported
	sum    [32]byte
	size   int64
}

// scan walks src (pass 1), lists every file and hashes the recognized ones, detecting with opts.Parsers and
// reporting EventFileFinished and EventScanProgress (every opts.ProgressBytes) to opts.Progress. It returns an
// error wrapping ErrTooManyFiles as soon as entry MaxFiles+1 is counted, without reading further.
func scan(ctx context.Context, src source, opts Options) ([]found, error) {
	s := &scanner{src: src, opts: opts}
	if s.opts.ProgressBytes <= 0 {
		s.opts.ProgressBytes = DefaultProgressBytes
	}
	var err error
	if src.file != "" {
		err = s.regularFile(ctx, src.file)
	} else {
		err = s.walkDir(ctx, ".")
	}
	return s.found, err
}

// scanner holds the state of pass 1.
type scanner struct {
	src     source
	opts    Options
	found   []found
	entries int // entries counted against MaxFiles
}

// item is a file or archive entry the scanner is about to examine.
type item struct {
	display   string    // path shown to the operator
	name      string    // logparse.File.Name before a ".gz" is removed
	mod       time.Time // UTC
	loc       location
	inArchive bool
	trk       *errTracker // the raw content of an archive entry; nil for a file
}

// count counts one more entry and returns an error wrapping ErrTooManyFiles when it exceeds MaxFiles.
func (s *scanner) count() error {
	s.entries++
	if s.entries > MaxFiles {
		return fmt.Errorf("%w: more than %d entries; split the input", ErrTooManyFiles, MaxFiles)
	}
	return nil
}

// report passes p to the progress callback, if any.
func (s *scanner) report(p Progress) {
	if s.opts.Progress != nil {
		s.opts.Progress(p)
	}
}

// listed is one directory entry with what ReadDir reported about it.
type listed struct {
	name string
	mode fs.FileMode
}

// walkDir lists the directory rel of the root, counting its entries while it reads them, and examines them in
// lexical order.
func (s *scanner) walkDir(ctx context.Context, rel string) error {
	entries, err := s.readDir(ctx, rel)
	if err != nil {
		if rel == "." || ctx.Err() != nil || errors.Is(err, ErrTooManyFiles) {
			return err
		}
		s.list(item{display: cutPath(rel), loc: location{fsPath: rel, entry: -1}}, OutcomeFailed, reasonOf(err))
		return nil
	}
	for _, e := range entries {
		if err := s.dirEntry(ctx, rel, e); err != nil {
			return err
		}
	}
	return nil
}

// readDir reads the entries of the directory rel in chunks and returns them sorted by name. The entry that
// exceeds MaxFiles ends the read at once.
func (s *scanner) readDir(ctx context.Context, rel string) ([]listed, error) {
	d, err := openDir(s.src.root, rel)
	if err != nil {
		return nil, err
	}
	defer func() { _ = d.Close() }()
	var entries []listed
	for {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		chunk, err := d.ReadDir(dirChunk)
		for _, e := range chunk {
			if err := s.count(); err != nil {
				return nil, err
			}
			entries = append(entries, listed{e.Name(), e.Type()})
		}
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return nil, err
		}
	}
	slices.SortFunc(entries, func(a, b listed) int { return strings.Compare(a.name, b.name) })
	return entries, nil
}

// dirEntry examines one entry of the directory dir.
func (s *scanner) dirEntry(ctx context.Context, dir string, e listed) error {
	rel := e.name
	if dir != "." {
		rel = dir + "/" + e.name
	}
	it := item{display: cutPath(rel), name: rel, loc: location{fsPath: rel, entry: -1}}
	switch {
	case len(rel) > MaxPathBytes:
		s.list(it, OutcomeFailed, reasonTooLong)
	case e.mode.IsDir():
		return s.walkDir(ctx, rel)
	case e.mode&fs.ModeSymlink != 0:
		s.list(it, OutcomeUnrecognized, reasonSymlink)
	case e.mode.IsRegular():
		return s.regularFile(ctx, rel)
	default:
		s.list(it, OutcomeUnrecognized, reasonNotRegular)
	}
	return nil
}

// regularFile opens the file rel of the root and examines its content.
func (s *scanner) regularFile(ctx context.Context, rel string) error {
	it := item{display: rel, name: rel, loc: location{fsPath: rel, entry: -1}}
	f, err := openRegular(s.src.root, rel)
	if err != nil {
		return s.fail(ctx, it, err)
	}
	defer func() { _ = f.Close() }()
	info, err := f.Stat()
	if err != nil {
		return s.fail(ctx, it, err)
	}
	it.mod = info.ModTime().UTC()
	return s.stream(ctx, it, f)
}

// list records a file that is not imported and reports it as finished.
func (s *scanner) list(it item, outcome Outcome, reason string) {
	f := found{
		result: FileResult{Path: it.display, Outcome: outcome, Reason: reason},
		file:   logparse.File{Name: cutPath(it.name), ModTime: it.mod},
		loc:    it.loc,
	}
	s.found = append(s.found, f)
	res := f.result
	s.report(Progress{Event: EventFileFinished, Path: res.Path, Result: &res})
}

// fail lists it as failed with the reason err. It returns the error that must stop the scan instead when the
// context is done or when the raw content of the archive entry could not be read (which ends the archive).
func (s *scanner) fail(ctx context.Context, it item, err error) error {
	if cerr := ctx.Err(); cerr != nil {
		return cerr
	}
	if it.trk != nil && it.trk.err != nil {
		return it.trk.err
	}
	s.list(it, OutcomeFailed, reasonOf(err))
	return nil
}

// stream examines the content raw of it: it recognizes one gzip layer, then lists, opens as an archive or hands
// the content to the parsers.
func (s *scanner) stream(ctx context.Context, it item, raw io.Reader) error {
	br := bufio.NewReaderSize(&ctxReader{check: ctx.Err, r: raw}, logparse.SniffBytes)
	head, err := br.Peek(logparse.SniffBytes)
	if err != nil && !errors.Is(err, io.EOF) {
		return s.fail(ctx, it, err)
	}
	kind, unsupported := sniffFormat(head)
	content := br
	if kind == formatGzip {
		it.loc.gzip = true
		it.name = trimGzip(it.name)
		zr, err := gzip.NewReader(br)
		if err != nil {
			return s.fail(ctx, it, err)
		}
		content = bufio.NewReaderSize(zr, logparse.SniffBytes)
		if head, err = content.Peek(logparse.SniffBytes); err != nil && !errors.Is(err, io.EOF) {
			return s.fail(ctx, it, err)
		}
		kind, unsupported = sniffFormat(head)
	}
	return s.classify(ctx, it, kind, unsupported, head, content)
}

// classify acts on the format of the (decompressed) content.
func (s *scanner) classify(ctx context.Context, it item, kind format, unsupported string, head []byte, content *bufio.Reader) error {
	switch kind {
	case formatGzip:
		s.list(it, OutcomeUnrecognized, reasonTwice)
	case formatEmpty:
		s.list(it, OutcomeUnrecognized, reasonEmpty)
	case formatUnsupported:
		s.list(it, OutcomeUnrecognized, "unsupported format: "+unsupported)
	case formatTar:
		if it.inArchive {
			s.list(it, OutcomeUnrecognized, reasonNested)
			return nil
		}
		return s.archive(ctx, it, content)
	default:
		return s.recognize(ctx, it, head, content)
	}
	return nil
}

// recognize lets the registry pick a parser for the file and, when one claims it, reads the content to its end
// to hash it.
func (s *scanner) recognize(ctx context.Context, it item, head []byte, content io.Reader) error {
	file := logparse.File{Name: it.name, ModTime: it.mod}
	parser, _ := s.opts.Parsers.Detect(file, head)
	if parser == nil {
		s.list(it, OutcomeUnrecognized, reasonNoParser)
		return nil
	}
	h := sha256.New()
	hr := &hashReader{r: content, h: h, step: s.opts.ProgressBytes, next: s.opts.ProgressBytes}
	hr.emit = func(mark int64) { s.report(Progress{Event: EventScanProgress, Path: it.display, Bytes: mark}) }
	size, err := io.Copy(io.Discard, hr)
	if err != nil {
		return s.fail(ctx, it, err)
	}
	f := found{
		result: FileResult{Path: it.display, SourceType: parser.Type()},
		file:   file,
		loc:    it.loc,
		parser: parser,
		size:   size,
	}
	h.Sum(f.sum[:0])
	s.found = append(s.found, f)
	return nil
}

// archive reads the tar archive it and examines its entries; an error of the archive itself is listed as the
// archive's failure.
func (s *scanner) archive(ctx context.Context, it item, content io.Reader) error {
	err := eachEntry(ctx, content, func(index int, h *tar.Header, c io.Reader) error {
		return s.tarEntry(ctx, it, index, h, c)
	})
	if err == nil {
		return nil
	}
	if ctx.Err() != nil || errors.Is(err, ErrTooManyFiles) {
		return err
	}
	s.list(item{display: it.display, name: it.name, mod: it.mod, loc: location{fsPath: it.loc.fsPath, entry: -1}},
		OutcomeFailed, reasonOf(err))
	return nil
}

// tarEntry examines one header of the archive it.
func (s *scanner) tarEntry(ctx context.Context, arc item, index int, h *tar.Header, content io.Reader) error {
	if h.Typeflag == tar.TypeXGlobalHeader {
		return nil
	}
	if err := s.count(); err != nil {
		return err
	}
	if h.Typeflag == tar.TypeDir {
		return nil
	}
	name := cleanName(h.Name)
	e := item{
		display:   arc.display + ":" + cutPath(name),
		name:      name,
		mod:       h.ModTime.UTC(),
		loc:       location{fsPath: arc.loc.fsPath, entry: index, archiveGzip: arc.loc.gzip},
		inArchive: true,
	}
	switch {
	case len(name) > MaxPathBytes:
		s.list(e, OutcomeFailed, reasonTooLong)
	case h.Typeflag == tar.TypeReg:
		e.trk = &errTracker{r: content}
		return s.stream(ctx, e, e.trk)
	case h.Typeflag == tar.TypeSymlink:
		s.list(e, OutcomeUnrecognized, reasonSymlink)
	case h.Typeflag == tar.TypeLink:
		s.list(e, OutcomeUnrecognized, reasonHardLink)
	default:
		s.list(e, OutcomeUnrecognized, reasonNotRegular)
	}
	return nil
}

// cleanName returns the entry name n cleaned and without a leading slash; it is a label only. The result is an
// independent copy, so a kept name does not pin the memory of the raw header name.
func cleanName(n string) string {
	return strings.Clone(strings.TrimLeft(path.Clean(n), "/"))
}

// trimGzip removes a trailing ".gz" (any case) from name.
func trimGzip(name string) string {
	if len(name) > len(".gz") && strings.EqualFold(name[len(name)-len(".gz"):], ".gz") {
		return name[:len(name)-len(".gz")]
	}
	return name
}

// cutPath returns p cut to MaxPathBytes bytes at a rune boundary. A cut copy does not pin the memory of p.
func cutPath(p string) string {
	if len(p) <= MaxPathBytes {
		return p
	}
	n := MaxPathBytes
	for n > 0 && !utf8.RuneStart(p[n]) {
		n--
	}
	return strings.Clone(p[:n])
}

// reasonOf returns the text of err for the summary, without the path of an operating system error.
func reasonOf(err error) string {
	var pe *fs.PathError
	if errors.As(err, &pe) {
		return pe.Err.Error()
	}
	return err.Error()
}

// ctxReader fails every read once check reports an error (the context is done).
type ctxReader struct {
	check func() error
	r     io.Reader
}

func (c *ctxReader) Read(p []byte) (int, error) {
	if err := c.check(); err != nil {
		return 0, err
	}
	return c.r.Read(p)
}

// errTracker remembers the first error other than io.EOF that its reader returned.
type errTracker struct {
	r   io.Reader
	err error
}

func (t *errTracker) Read(p []byte) (int, error) {
	n, err := t.r.Read(p)
	if err != nil && !errors.Is(err, io.EOF) && t.err == nil {
		t.err = err
	}
	return n, err
}

// hashReader hashes what it reads and calls emit with every multiple of step bytes it passes.
type hashReader struct {
	r          io.Reader
	h          hash.Hash
	n          int64
	step, next int64
	emit       func(mark int64)
}

func (r *hashReader) Read(p []byte) (int, error) {
	n, err := r.r.Read(p)
	if n > 0 {
		r.h.Write(p[:n])
		r.n += int64(n)
		for r.n >= r.next {
			r.emit(r.next)
			r.next += r.step
		}
	}
	return n, err
}
