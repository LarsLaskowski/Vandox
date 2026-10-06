package importer

import (
	"archive/tar"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store"
	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store/storetest"
	"github.com/LarsLaskowski/Vandox/internal/logparse"
	"github.com/LarsLaskowski/Vandox/internal/logparse/logparsetest"
	"github.com/LarsLaskowski/Vandox/internal/model"
)

// guard bounds every wait that could hang if the code under test blocks; it is never asserted.
const guard = 30 * time.Second

var (
	// testBase is the instant the test parsers' records are built on.
	testBase = time.Date(2026, time.October, 6, 12, 0, 0, 0, time.UTC)
	// fixedNow is the clock of the importer under test.
	fixedNow = time.Date(2026, time.October, 7, 8, 0, 0, 0, time.UTC)
	// modA, modB, modC are whole-second modification times (tar headers store whole seconds).
	modA = time.Date(2026, time.September, 1, 10, 20, 30, 0, time.UTC)
	modB = time.Date(2026, time.September, 2, 11, 21, 31, 0, time.UTC)
	modC = time.Date(2026, time.September, 3, 12, 22, 32, 0, time.UTC)
)

// ---- parsers and options ----

// claimAll returns a parser of type "syslog" that claims every file and emits one record per non-comment line.
func claimAll() *logparsetest.Parser {
	return &logparsetest.Parser{
		TypeName:   "syslog",
		DetectFunc: func(logparse.File, []byte) logparse.Confidence { return logparse.MatchContent },
		ParseFunc:  logparsetest.Lines("syslog", testBase),
	}
}

// newRegistry returns a registry of ps and fails the test when it is refused.
func newRegistry(t *testing.T, ps ...logparse.Parser) *logparse.Registry {
	t.Helper()
	reg, err := logparse.NewRegistry(ps...)
	if err != nil {
		t.Fatalf("NewRegistry() error = %v, want nil", err)
	}
	return reg
}

// testOptions returns options with a fixed clock that import into st with the parsers ps.
func testOptions(t *testing.T, st Store, ps ...logparse.Parser) Options {
	t.Helper()
	return Options{Parsers: newRegistry(t, ps...), Store: st, Now: func() time.Time { return fixedNow }}
}

// runOK runs the import of root and fails the test on an error.
func runOK(t *testing.T, root string, o Options) Summary {
	t.Helper()
	sum, err := Run(context.Background(), root, o)
	if err != nil {
		t.Fatalf("Run(%q) error = %v, want nil", root, err)
	}
	return sum
}

// ---- stores ----

// openStore opens a real store in a new directory.
func openStore(t *testing.T) *store.Store {
	t.Helper()
	s, err := store.Open(context.Background(), t.TempDir())
	if err != nil {
		t.Fatalf("store.Open() error = %v, want nil", err)
	}
	t.Cleanup(func() { _ = s.Close() })
	return s
}

// storedRecords returns all stored log line records, ordered by capture time and then by insertion.
func storedRecords(t *testing.T, s *store.Store) []store.StoredRecord {
	t.Helper()
	recs, err := s.Records(context.Background(), store.RecordQuery{
		Kind: model.KindLogLine, From: time.Date(2020, 1, 1, 0, 0, 0, 0, time.UTC), To: time.Date(2100, 1, 1, 0, 0, 0, 0, time.UTC),
		Limit: store.MaxQueryLimit,
	})
	if err != nil {
		t.Fatalf("Records() error = %v, want nil", err)
	}
	return recs
}

// storedMessages returns the messages of all stored log lines, ordered by capture time and then by insertion.
func storedMessages(t *testing.T, s *store.Store) []string {
	t.Helper()
	recs := storedRecords(t, s)
	out := make([]string, 0, len(recs))
	for _, r := range recs {
		out = append(out, r.Record.Data.(*model.LogLine).Message)
	}
	return out
}

// sortedCopy returns the sorted copy of s.
func sortedCopy(s []string) []string {
	c := slices.Clone(s)
	slices.Sort(c)
	return c
}

// fakeMessages returns the messages of every record the fake received, in batch order.
func fakeMessages(f *storetest.Fake) []string {
	var out []string
	for _, b := range f.Batches() {
		for _, r := range b.Records {
			out = append(out, r.Data.(*model.LogLine).Message)
		}
	}
	return out
}

// cloneBatch returns b with its own records and import step.
func cloneBatch(b store.Batch) store.Batch {
	b.Records = slices.Clone(b.Records)
	if b.Import != nil {
		step := *b.Import
		b.Import = &step
	}
	return b
}

// spyStore delegates to a real store and records every batch; after is called with the 1-based number of a batch
// once the store accepted it.
type spyStore struct {
	Store
	mu      sync.Mutex
	batches []store.Batch
	after   func(n int)
}

func (s *spyStore) WriteBatch(ctx context.Context, b store.Batch) (store.WriteResult, error) {
	res, err := s.Store.WriteBatch(ctx, b)
	s.mu.Lock()
	s.batches = append(s.batches, cloneBatch(b))
	n := len(s.batches)
	s.mu.Unlock()
	if err == nil && s.after != nil {
		s.after(n)
	}
	return res, err
}

func (s *spyStore) recorded() []store.Batch {
	s.mu.Lock()
	defer s.mu.Unlock()
	return slices.Clone(s.batches)
}

// discardStore accepts everything and keeps nothing; onWrite runs inside every WriteBatch.
type discardStore struct {
	onWrite func()
	records atomic.Int64
	nextID  atomic.Int64
}

func (d *discardStore) WriteBatch(_ context.Context, b store.Batch) (store.WriteResult, error) {
	d.records.Add(int64(len(b.Records)))
	if d.onWrite != nil {
		d.onWrite()
	}
	return store.WriteResult{Stored: len(b.Records)}, nil
}

func (d *discardStore) BeginImport(_ context.Context, f store.ImportFileStart) (store.ImportFile, error) {
	return store.ImportFile{
		ID: d.nextID.Add(1), SHA256: f.SHA256, Size: f.Size, Name: f.Name, FileName: f.FileName, ModTime: f.ModTime,
		SourceType: f.SourceType, StartedAt: f.StartedAt,
	}, nil
}

// ---- building inputs ----

// numbered returns n lines "prefix 1" … "prefix n", each ended by a newline.
func numbered(prefix string, n int) string {
	var b strings.Builder
	for i := 1; i <= n; i++ {
		b.WriteString(prefix + " " + strconv.Itoa(i) + "\n")
	}
	return b.String()
}

// linesOf returns the lines of text without their newlines.
func linesOf(text string) []string {
	return strings.Split(strings.TrimSuffix(text, "\n"), "\n")
}

// gz returns the gzip compression of the parts, one member per part.
func gz(t *testing.T, parts ...string) []byte {
	t.Helper()
	var buf bytes.Buffer
	for _, p := range parts {
		w := gzip.NewWriter(&buf)
		if _, err := w.Write([]byte(p)); err != nil {
			t.Fatalf("gzip write error = %v, want nil", err)
		}
		if err := w.Close(); err != nil {
			t.Fatalf("gzip close error = %v, want nil", err)
		}
	}
	return buf.Bytes()
}

// tarEntry describes one entry of a test archive.
type tarEntry struct {
	name    string
	content string
	typ     byte // 0: a regular file
	link    string
	mod     time.Time // zero: modA
	format  tar.Format
	global  map[string]string // PAX global header records
}

// buildTar returns a tar archive of the entries, including its trailer.
func buildTar(t *testing.T, entries ...tarEntry) []byte {
	t.Helper()
	var buf bytes.Buffer
	writeTarEntries(t, tar.NewWriter(&buf), entries...)
	return buf.Bytes()
}

// tarHeader returns the header of the entry e.
func tarHeader(e tarEntry) *tar.Header {
	if e.typ == tar.TypeXGlobalHeader {
		return &tar.Header{Name: e.name, Typeflag: e.typ, PAXRecords: e.global, Format: tar.FormatPAX}
	}
	h := &tar.Header{Name: e.name, Typeflag: e.typ, Linkname: e.link, Mode: 0o644, ModTime: e.mod, Format: e.format}
	if h.Typeflag == 0 {
		h.Typeflag = tar.TypeReg
	}
	if h.ModTime.IsZero() {
		h.ModTime = modA
	}
	switch h.Typeflag {
	case tar.TypeDir:
		h.Mode = 0o755
	case tar.TypeReg:
		h.Size = int64(len(e.content))
	}
	return h
}

// writeTarEntries writes the entries to w and closes it.
func writeTarEntries(t *testing.T, w *tar.Writer, entries ...tarEntry) {
	t.Helper()
	for _, e := range entries {
		h := tarHeader(e)
		if err := w.WriteHeader(h); err != nil {
			t.Fatalf("tar WriteHeader(%q) error = %v, want nil", e.name, err)
		}
		if h.Size == 0 {
			continue
		}
		if _, err := w.Write([]byte(e.content)); err != nil {
			t.Fatalf("tar Write(%q) error = %v, want nil", e.name, err)
		}
	}
	if err := w.Close(); err != nil {
		t.Fatalf("tar Close() error = %v, want nil", err)
	}
}

// writeFile writes data to path below dir (creating directories) and returns the path.
func writeFile(t *testing.T, dir, rel string, data []byte) string {
	t.Helper()
	path := filepath.Join(dir, filepath.FromSlash(rel))
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatalf("MkdirAll(%q) error = %v, want nil", filepath.Dir(path), err)
	}
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatalf("WriteFile(%q) error = %v, want nil", path, err)
	}
	return path
}

// writeTimed writes data like writeFile and sets the modification time.
func writeTimed(t *testing.T, dir, rel string, data []byte, mod time.Time) string {
	t.Helper()
	path := writeFile(t, dir, rel, data)
	if err := os.Chtimes(path, mod, mod); err != nil {
		t.Fatalf("Chtimes(%q) error = %v, want nil", path, err)
	}
	return path
}

// sha256Of returns the SHA-256 of s.
func sha256Of(s string) [32]byte { return sha256.Sum256([]byte(s)) }

// resultFor returns the result for the display path p.
func resultFor(t *testing.T, sum Summary, p string) FileResult {
	t.Helper()
	for _, r := range sum.Files {
		if r.Path == p {
			return r
		}
	}
	paths := make([]string, 0, len(sum.Files))
	for _, r := range sum.Files {
		paths = append(paths, r.Path)
	}
	t.Fatalf("Summary.Files has no result for %q, paths: %q", p, paths)
	return FileResult{}
}

// sameInstant reports whether a and b are the same instant, and whether both are in UTC.
func sameInstant(a, b time.Time) bool {
	_, offA := a.Zone()
	_, offB := b.Zone()
	return a.Equal(b) && offA == 0 && offB == 0
}

// ---- AC-I1: input forms ----

// wantFile is what a parser must receive for one file of an input.
type wantFile struct {
	path  string // display path in the summary
	name  string // logparse.File.Name
	mod   time.Time
	lines []string
}

type inputForm struct {
	name  string
	build func(t *testing.T, dir string) string // returns the root path
	want  []wantFile
}

var (
	textAlpha = numbered("alpha", 3)
	textBeta  = numbered("beta", 2)
	textGamma = numbered("gamma", 4)
)

// archiveWant is the expectation for the entries of a standard two-entry archive.
func archiveWant(archive string) []wantFile {
	return []wantFile{
		{path: archive + ":var/log/syslog", name: "var/log/syslog", mod: modA, lines: linesOf(textAlpha)},
		{path: archive + ":var/log/mail.log", name: "var/log/mail.log", mod: modB, lines: linesOf(textBeta)},
	}
}

func standardEntries(format tar.Format) []tarEntry {
	return []tarEntry{
		{name: "var/log/syslog", content: textAlpha, mod: modA, format: format},
		{name: "var/log/mail.log", content: textBeta, mod: modB, format: format},
	}
}

func inputForms() []inputForm {
	return []inputForm{
		{"directory tree in lexical order", func(t *testing.T, dir string) string {
			root := filepath.Join(dir, "in")
			writeTimed(t, root, "e.log", []byte(textGamma), modC)
			writeTimed(t, root, "dir/c.log", []byte(textBeta), modB)
			writeTimed(t, root, "b.log", []byte(textBeta), modB)
			writeTimed(t, root, "dir/a.log", []byte(textAlpha), modA)
			writeTimed(t, root, "a.log", []byte(textAlpha), modA)
			return root
		}, []wantFile{
			{"a.log", "a.log", modA, linesOf(textAlpha)},
			{"b.log", "b.log", modB, linesOf(textBeta)},
			{"dir/a.log", "dir/a.log", modA, linesOf(textAlpha)},
			{"dir/c.log", "dir/c.log", modB, linesOf(textBeta)},
			{"e.log", "e.log", modC, linesOf(textGamma)},
		}},
		{"single plain file", func(t *testing.T, dir string) string {
			return writeTimed(t, dir, "syslog", []byte(textAlpha), modA)
		}, []wantFile{{"syslog", "syslog", modA, linesOf(textAlpha)}}},
		{"single gzip file", func(t *testing.T, dir string) string {
			return writeTimed(t, dir, "syslog.1.gz", gz(t, textAlpha), modB)
		}, []wantFile{{"syslog.1.gz", "syslog.1", modB, linesOf(textAlpha)}}},
		{"gzip file with an upper case suffix", func(t *testing.T, dir string) string {
			return writeTimed(t, dir, "SYSLOG.GZ", gz(t, textAlpha), modB)
		}, []wantFile{{"SYSLOG.GZ", "SYSLOG", modB, linesOf(textAlpha)}}},
		{"gzip file without a suffix is recognized by content", func(t *testing.T, dir string) string {
			return writeTimed(t, dir, "rotated", gz(t, textAlpha), modB)
		}, []wantFile{{"rotated", "rotated", modB, linesOf(textAlpha)}}},
		{"multi-member gzip file", func(t *testing.T, dir string) string {
			return writeTimed(t, dir, "syslog.1.gz", gz(t, "alpha 1\nalpha 2\n", "alpha 3\n", "alpha 4\n"), modA)
		}, []wantFile{{"syslog.1.gz", "syslog.1", modA, linesOf("alpha 1\nalpha 2\nalpha 3\nalpha 4\n")}}},
		{"tar USTAR", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "logs.tar", buildTar(t, standardEntries(tar.FormatUSTAR)...))
		}, archiveWant("logs.tar")},
		{"tar PAX", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "logs.tar", buildTar(t, standardEntries(tar.FormatPAX)...))
		}, archiveWant("logs.tar")},
		{"tar GNU", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "logs.tar", buildTar(t, standardEntries(tar.FormatGNU)...))
		}, archiveWant("logs.tar")},
		{"tar.gz", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "logs.tar.gz", gz(t, string(buildTar(t, standardEntries(tar.FormatUSTAR)...))))
		}, archiveWant("logs.tar.gz")},
		{"tgz", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "logs.tgz", gz(t, string(buildTar(t, standardEntries(tar.FormatPAX)...))))
		}, archiveWant("logs.tgz")},
		{"tar.gz of the GNU format", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "logs.tar.gz", gz(t, string(buildTar(t, standardEntries(tar.FormatGNU)...))))
		}, archiveWant("logs.tar.gz")},
		{"tar without a file name suffix", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "logs.bin", buildTar(t, standardEntries(tar.FormatUSTAR)...))
		}, archiveWant("logs.bin")},
		{"tar.gz without a file name suffix", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "logs.dat", gz(t, string(buildTar(t, standardEntries(tar.FormatUSTAR)...))))
		}, archiveWant("logs.dat")},
		{"tar.gz inside the root directory", func(t *testing.T, dir string) string {
			root := filepath.Join(dir, "in")
			writeTimed(t, root, "a.log", []byte(textGamma), modC)
			writeFile(t, root, "sub/logs.tar.gz", gz(t, string(buildTar(t, standardEntries(tar.FormatUSTAR)...))))
			return root
		}, append([]wantFile{{"a.log", "a.log", modC, linesOf(textGamma)}}, archiveWant("sub/logs.tar.gz")...)},
		{"gzip-compressed entries inside a tar", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "logs.tar", buildTar(t,
				tarEntry{name: "var/log/syslog.2.gz", content: string(gz(t, textAlpha)), mod: modA},
				tarEntry{name: "var/log/mail.log", content: textBeta, mod: modB},
			))
		}, []wantFile{
			{"logs.tar:var/log/syslog.2.gz", "var/log/syslog.2", modA, linesOf(textAlpha)},
			{"logs.tar:var/log/mail.log", "var/log/mail.log", modB, linesOf(textBeta)},
		}},
		{"tar directory entries and a PAX global header are not files", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "logs.tar", buildTar(t,
				tarEntry{name: "global", typ: tar.TypeXGlobalHeader, global: map[string]string{"comment": "x"}},
				tarEntry{name: "var/", typ: tar.TypeDir},
				tarEntry{name: "var/log/syslog", content: textAlpha, mod: modA},
			))
		}, []wantFile{{"logs.tar:var/log/syslog", "var/log/syslog", modA, linesOf(textAlpha)}}},
	}
}

// checkInputForm imports the input of tc and compares what the parser received and what was stored.
func checkInputForm(t *testing.T, tc inputForm) {
	t.Helper()
	root := tc.build(t, t.TempDir())
	fake := &storetest.Fake{}
	parser := claimAll()

	sum := runOK(t, root, testOptions(t, fake, parser))

	parsed := parser.Parsed()
	if len(sum.Files) != len(tc.want) || len(parsed) != len(tc.want) {
		t.Fatalf("Run(%s) listed %d files and parsed %d, want %d: %+v", tc.name, len(sum.Files), len(parsed), len(tc.want), sum.Files)
	}
	var wantLines []string
	for i, w := range tc.want {
		if got := sum.Files[i]; got.Path != w.path || got.Outcome != OutcomeImported || got.SourceType != "syslog" {
			t.Errorf("Files[%d] = %q %q %q, want %q %q %q", i, got.Path, got.Outcome, got.SourceType, w.path, OutcomeImported, "syslog")
		}
		if parsed[i].Name != w.name || !sameInstant(parsed[i].ModTime, w.mod) {
			t.Errorf("Parse call %d got File{%q, %v}, want File{%q, %v} (UTC)", i, parsed[i].Name, parsed[i].ModTime, w.name, w.mod)
		}
		wantLines = append(wantLines, w.lines...)
	}
	if got := fakeMessages(fake); !slices.Equal(got, wantLines) {
		t.Errorf("stored messages = %q, want %q", got, wantLines)
	}
}

func TestRun_InputForms(t *testing.T) {
	for _, tc := range inputForms() {
		t.Run(tc.name, func(t *testing.T) { checkInputForm(t, tc) })
	}
}

func TestRun_SummaryRoot(t *testing.T) {
	dir := t.TempDir()
	writeFile(t, dir, "a.log", []byte(textAlpha))

	sum := runOK(t, dir, testOptions(t, &storetest.Fake{}, claimAll()))

	if sum.Root != dir {
		t.Errorf("Summary.Root = %q, want %q", sum.Root, dir)
	}
}

func TestRun_EntryNamesAreCleanedLabels(t *testing.T) {
	tests := []struct {
		entry string
		want  string
	}{
		{"./var/log/x", "var/log/x"},
		{"/abs/x", "abs/x"},
		{"a//b", "a/b"},
		{"a/./b/../c", "a/c"},
		{"../x", "../x"},
		{"with space and \x01 control \xff", "with space and \x01 control \xff"},
	}
	for _, tc := range tests {
		t.Run(strconv.Quote(tc.entry), func(t *testing.T) {
			root := writeFile(t, t.TempDir(), "n.tar", buildTar(t, tarEntry{name: tc.entry, content: textAlpha}))
			parser := claimAll()

			sum := runOK(t, root, testOptions(t, &storetest.Fake{}, parser))

			parsed := parser.Parsed()
			if len(parsed) != 1 || parsed[0].Name != tc.want {
				t.Errorf("Parse got %+v, want one file named %q", parsed, tc.want)
			}
			if len(sum.Files) != 1 || sum.Files[0].Outcome != OutcomeImported {
				t.Errorf("Files = %+v, want one imported file", sum.Files)
			}
		})
	}
}

func TestRun_FileNamesAreLabelsOnly(t *testing.T) {
	dir := t.TempDir()
	name := "a\x01\xffb\n\u009b.log"
	writeFile(t, dir, "in/"+name, []byte(textAlpha))
	parser := claimAll()

	sum := runOK(t, filepath.Join(dir, "in"), testOptions(t, &storetest.Fake{}, parser))

	if parsed := parser.Parsed(); len(parsed) != 1 || parsed[0].Name != name {
		t.Errorf("Parse got %+v, want one file named %q unchanged", parsed, name)
	}
	if len(sum.Files) != 1 || sum.Files[0].Path != name || sum.Files[0].Outcome != OutcomeImported {
		t.Errorf("Files = %+v, want one imported file with the unchanged path", sum.Files)
	}
}

func TestRun_DuplicateEntryNamesAreHandledByContent(t *testing.T) {
	root := writeFile(t, t.TempDir(), "d.tar", buildTar(t,
		tarEntry{name: "x", content: textAlpha},
		tarEntry{name: "x", content: textBeta},
		tarEntry{name: "x", content: textAlpha},
	))
	s := openStore(t)

	sum := runOK(t, root, testOptions(t, s, claimAll()))

	if got := []Outcome{sum.Files[0].Outcome, sum.Files[1].Outcome, sum.Files[2].Outcome}; !slices.Equal(got, []Outcome{OutcomeImported, OutcomeImported, OutcomeAlreadyImported}) {
		t.Errorf("outcomes of three entries named x (alpha, beta, alpha) = %v, want imported, imported, already_imported", got)
	}
	if got := sortedCopy(storedMessages(t, s)); !slices.Equal(got, sortedCopy(append(linesOf(textAlpha), linesOf(textBeta)...))) {
		t.Errorf("stored messages = %q, want alpha and beta once each", got)
	}
}

func TestRun_SymbolicLinkAsRootIsResolvedOnce(t *testing.T) {
	dir := t.TempDir()
	writeFile(t, dir, "real/a.log", []byte(textAlpha))
	link := filepath.Join(dir, "link")
	if err := os.Symlink(filepath.Join(dir, "real"), link); err != nil {
		t.Fatalf("Symlink() error = %v, want nil", err)
	}
	fake := &storetest.Fake{}

	sum := runOK(t, link, testOptions(t, fake, claimAll()))

	if len(sum.Files) != 1 || sum.Files[0].Path != "a.log" || sum.Files[0].Outcome != OutcomeImported {
		t.Errorf("Files = %+v, want a.log imported", sum.Files)
	}
}

func TestRun_BeginImportGetsTheContentAndTheFile(t *testing.T) {
	dir := t.TempDir()
	content := numbered("zeta", 5)
	writeTimed(t, dir, "in/sub/x.log.gz", gz(t, content), modB)
	fake := &storetest.Fake{}
	zone := time.FixedZone("plus2", 7200)
	o := testOptions(t, fake, claimAll())
	o.Now = func() time.Time { return fixedNow.In(zone) }

	runOK(t, filepath.Join(dir, "in"), o)

	starts := fake.ImportStarts()
	if len(starts) != 1 {
		t.Fatalf("BeginImport was called %d times, want 1", len(starts))
	}
	got := starts[0]
	if got.SHA256 != sha256Of(content) || got.Size != int64(len(content)) {
		t.Errorf("BeginImport got hash %x and size %d, want the hash %x and size %d of the decompressed content", got.SHA256, got.Size, sha256Of(content), len(content))
	}
	if got.Name != "sub/x.log.gz" || got.FileName != "sub/x.log" || got.SourceType != "syslog" {
		t.Errorf("BeginImport got Name %q, FileName %q, SourceType %q, want %q, %q, %q", got.Name, got.FileName, got.SourceType, "sub/x.log.gz", "sub/x.log", "syslog")
	}
	if !sameInstant(got.ModTime, modB) || !sameInstant(got.StartedAt, fixedNow) {
		t.Errorf("BeginImport got ModTime %v and StartedAt %v, want %v and %v, both UTC", got.ModTime, got.StartedAt, modB, fixedNow)
	}
}

// ---- AC-I3: idempotency ----

func outcomes(sum Summary) []Outcome {
	out := make([]Outcome, 0, len(sum.Files))
	for _, f := range sum.Files {
		out = append(out, f.Outcome)
	}
	return out
}

func TestRun_SecondRunStoresNothing(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	writeFile(t, root, "a.log", []byte(textAlpha))
	writeFile(t, root, "b.log.gz", gz(t, textBeta))
	writeFile(t, root, "t.tar.gz", gz(t, string(buildTar(t,
		tarEntry{name: "x", content: textGamma},
		tarEntry{name: "y", content: numbered("delta", 2)},
	))))
	s := openStore(t)
	o := testOptions(t, s, claimAll())

	first := runOK(t, root, o)
	storedAfterFirst := storedMessages(t, s)
	second := runOK(t, root, o)

	if got := first.Count(OutcomeImported); got != 4 {
		t.Fatalf("first run imported %d files, want 4: %v", got, outcomes(first))
	}
	if got := second.Count(OutcomeAlreadyImported); got != 4 || second.Count(OutcomeImported) != 0 {
		t.Errorf("second run outcomes = %v, want four already_imported", outcomes(second))
	}
	if second.Records != 0 {
		t.Errorf("second run Summary.Records = %d, want 0", second.Records)
	}
	if got := storedMessages(t, s); !slices.Equal(got, storedAfterFirst) {
		t.Errorf("stored messages after the second run = %d, want unchanged %d", len(got), len(storedAfterFirst))
	}
	if want := len(linesOf(textAlpha)) + len(linesOf(textBeta)) + len(linesOf(textGamma)) + 2; len(storedAfterFirst) != want {
		t.Errorf("stored messages after the first run = %d, want %d", len(storedAfterFirst), want)
	}
}

func TestRun_SameContentUnderAnotherNameOrCompressionIsAlreadyImported(t *testing.T) {
	dir := t.TempDir()
	s := openStore(t)
	o := testOptions(t, s, claimAll())
	writeFile(t, dir, "one/syslog.1", []byte(textAlpha))
	first := runOK(t, filepath.Join(dir, "one"), o)
	stored := storedMessages(t, s)
	if first.Count(OutcomeImported) != 1 {
		t.Fatalf("first run outcomes = %v, want one imported", outcomes(first))
	}
	forms := map[string]func() string{
		"gzip-compressed under another name": func() string { return writeFile(t, dir, "two/syslog.2.gz", gz(t, textAlpha)) },
		"multi-member gzip":                  func() string { return writeFile(t, dir, "three/s.gz", gz(t, "alpha 1\n", "alpha 2\nalpha 3\n")) },
		"entry of a tar": func() string {
			return writeFile(t, dir, "four/l.tar", buildTar(t, tarEntry{name: "var/log/messages", content: textAlpha}))
		},
		"gzip-compressed entry of a tar.gz": func() string {
			return writeFile(t, dir, "five/l.tgz", gz(t, string(buildTar(t, tarEntry{name: "m.gz", content: string(gz(t, textAlpha))}))))
		},
	}

	for name, build := range forms {
		t.Run(name, func(t *testing.T) {
			sum := runOK(t, build(), o)

			if got := outcomes(sum); !slices.Equal(got, []Outcome{OutcomeAlreadyImported}) {
				t.Errorf("outcomes = %v, want one already_imported", got)
			}
			if got := storedMessages(t, s); !slices.Equal(got, stored) {
				t.Errorf("stored messages = %d, want unchanged %d", len(got), len(stored))
			}
		})
	}
}

func TestRun_IdenticalFilesInOneRunAreStoredOnce(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	writeFile(t, root, "a.log", []byte(textAlpha))
	writeFile(t, root, "b.log", []byte(textAlpha))
	s := openStore(t)

	sum := runOK(t, root, testOptions(t, s, claimAll()))

	if got := outcomes(sum); !slices.Equal(got, []Outcome{OutcomeImported, OutcomeAlreadyImported}) {
		t.Errorf("outcomes = %v, want imported, already_imported", got)
	}
	if got := storedMessages(t, s); !slices.Equal(got, linesOf(textAlpha)) {
		t.Errorf("stored messages = %q, want %q once", got, linesOf(textAlpha))
	}
}

// ---- AC-I4: resume ----

// interrupted returns a context that is cancelled once the store accepted its first batch, and the spy store
// around s that does it.
func interrupted(s *store.Store) (context.Context, *spyStore) {
	ctx, cancel := context.WithCancel(context.Background())
	spy := &spyStore{Store: s, after: func(n int) {
		if n == 1 {
			cancel()
		}
	}}
	return ctx, spy
}

func TestRun_ResumesAnInterruptedImport(t *testing.T) {
	root := writeFile(t, t.TempDir(), "a.log", []byte(numbered("line", 10)))
	s := openStore(t)
	ctx, spy := interrupted(s)
	o := testOptions(t, spy, claimAll())
	o.BatchRecords = 3

	first, err := Run(ctx, root, o)

	if !errors.Is(err, context.Canceled) || !first.Interrupted {
		t.Fatalf("interrupted Run() = Interrupted %v, error %v, want true and context.Canceled", first.Interrupted, err)
	}
	if got := len(storedMessages(t, s)); got != 3 {
		t.Fatalf("records stored by the interrupted run = %d, want 3 (one batch)", got)
	}

	second := runOK(t, root, o)

	if len(second.Files) != 1 || second.Files[0].Outcome != OutcomeImported {
		t.Fatalf("second run Files = %+v, want one imported file", second.Files)
	}
	if r := second.Files[0]; r.ResumedAfter != 3 || r.Records != 7 || r.Lines != 10 {
		t.Errorf("second run result ResumedAfter, Records, Lines = %d, %d, %d, want 3, 7, 10", r.ResumedAfter, r.Records, r.Lines)
	}
	if got, want := storedMessages(t, s), linesOf(numbered("line", 10)); !slices.Equal(got, want) {
		t.Errorf("stored messages after the resume = %q, want every line once: %q", got, want)
	}
	third := runOK(t, root, o)
	if got := outcomes(third); !slices.Equal(got, []Outcome{OutcomeAlreadyImported}) {
		t.Errorf("third run outcomes = %v, want already_imported (the resumed file is complete)", got)
	}
}

// fileParser returns a parser of type "syslog" claiming every file; its records carry the File it was given in
// the message ("name: line") and in the capture time (ModTime plus the line number in seconds).
func fileParser() *logparsetest.Parser {
	return &logparsetest.Parser{
		TypeName:   "syslog",
		DetectFunc: func(logparse.File, []byte) logparse.Confidence { return logparse.MatchContent },
		ParseFunc: func(_ context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error {
			lr := logparse.NewLineReader(r)
			for {
				line, _, err := lr.Next()
				if errors.Is(err, io.EOF) {
					return nil
				}
				if err != nil {
					return err
				}
				rec := model.Record{
					Meta: model.Meta{Origin: model.OriginImport, Source: "syslog", CapturedAt: f.ModTime.Add(time.Duration(lr.Line()) * time.Second)},
					Data: &model.LogLine{Log: "syslog", Message: f.Name + ": " + string(line)},
				}
				if err := out.Record(rec); err != nil {
					return err
				}
			}
		},
	}
}

// checkResumedRecords checks that the 10 stored records are one parse of the first run's File.
func checkResumedRecords(t *testing.T, recs []store.StoredRecord) {
	t.Helper()
	if len(recs) != 10 {
		t.Fatalf("stored records = %d, want 10", len(recs))
	}
	for i, r := range recs {
		wantMsg := "orig.log: line " + strconv.Itoa(i+1)
		if got := r.Record.Data.(*model.LogLine).Message; got != wantMsg {
			t.Errorf("record %d message = %q, want %q (one parse under the first File)", i, got, wantMsg)
		}
		if want := modA.Add(time.Duration(i+1) * time.Second); !r.Record.CapturedAt.Equal(want) {
			t.Errorf("record %d CapturedAt = %v, want %v", i, r.Record.CapturedAt, want)
		}
	}
}

// checkParsedFile checks that the parser was called once, with the first run's File.
func checkParsedFile(t *testing.T, run string, got []logparse.File) {
	t.Helper()
	if len(got) != 1 || got[0].Name != "orig.log" || !sameInstant(got[0].ModTime, modA) {
		t.Errorf("%s parsed %+v, want one call with File{orig.log, %v}", run, got, modA)
	}
}

// checkResumeUnderAnotherFile interrupts the import of "orig.log" and resumes it from the input second.
func checkResumeUnderAnotherFile(t *testing.T, content string, second func(t *testing.T, dir string) string) {
	t.Helper()
	dir := t.TempDir()
	s := openStore(t)
	first := writeTimed(t, dir, "orig.log", []byte(content), modA)
	ctx, spy := interrupted(s)
	firstParser := fileParser()
	o := testOptions(t, spy, firstParser)
	o.BatchRecords = 3
	if _, err := Run(ctx, first, o); !errors.Is(err, context.Canceled) {
		t.Fatalf("interrupted Run() error = %v, want context.Canceled", err)
	}
	secondParser := fileParser()
	o2 := testOptions(t, s, secondParser)
	o2.BatchRecords = 3

	sum := runOK(t, second(t, filepath.Join(dir, "second")), o2)

	checkParsedFile(t, "the first run", firstParser.Parsed())
	checkParsedFile(t, "the resumed run", secondParser.Parsed())
	if len(sum.Files) != 1 || sum.Files[0].Outcome != OutcomeImported || sum.Files[0].ResumedAfter != 3 {
		t.Errorf("resumed run Files = %+v, want one imported file with ResumedAfter 3", sum.Files)
	}
	checkResumedRecords(t, storedRecords(t, s))
}

func TestRun_ResumeParsesWithTheFirstRunsFile(t *testing.T) {
	content := numbered("line", 10)
	tests := []struct {
		name  string
		build func(t *testing.T, dir string) string
	}{
		{"renamed plain file with another time", func(t *testing.T, dir string) string {
			return writeTimed(t, dir, "renamed.log", []byte(content), modC)
		}},
		{"renamed gzip file with a newer time", func(t *testing.T, dir string) string {
			return writeTimed(t, dir, "renamed.log.gz", gz(t, content), modC)
		}},
		{"entry of a tar", func(t *testing.T, dir string) string {
			return writeFile(t, dir, "x.tar", buildTar(t, tarEntry{name: "other/name.log", content: content, mod: modC}))
		}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { checkResumeUnderAnotherFile(t, content, tc.build) })
	}
}

func TestRun_ResumeAfterTheStoredCount(t *testing.T) {
	content := "l1\n#comment\nl2\nl3\nl4\nl5\n"
	root := writeFile(t, t.TempDir(), "current.log", []byte(content))
	fake := &storetest.Fake{OnBeginImport: func(f store.ImportFileStart) (store.ImportFile, error) {
		return store.ImportFile{
			ID: 5, SHA256: f.SHA256, Size: f.Size, Name: "first/run.log", FileName: "stored/name.log", ModTime: modC,
			SourceType: "syslog", Records: 2, StartedAt: modA,
		}, nil
	}}
	parser := claimAll()

	sum := runOK(t, root, testOptions(t, fake, parser))

	if got := parser.Parsed(); len(got) != 1 || got[0].Name != "stored/name.log" || !sameInstant(got[0].ModTime, modC) {
		t.Errorf("Parse got %+v, want the stored File{stored/name.log, %v}", got, modC)
	}
	if got, want := fakeMessages(fake), []string{"l3", "l4", "l5"}; !slices.Equal(got, want) {
		t.Errorf("stored messages = %q, want %q (the first two valid records are dropped, the comment is not counted)", got, want)
	}
	batches := fake.Batches()
	if len(batches) == 0 || batches[0].Import == nil || batches[0].Import.FileID != 5 || batches[0].Import.Done != 2 {
		t.Errorf("first batch = %+v, want an Import step of file 5 with Done 2", batches)
	}
	if r := sum.Files[0]; r.ResumedAfter != 2 || r.Records != 3 || r.Skipped != 1 {
		t.Errorf("result ResumedAfter, Records, Skipped = %d, %d, %d, want 2, 3, 1", r.ResumedAfter, r.Records, r.Skipped)
	}
}

func TestRun_FirstImportPassesTheCurrentFile(t *testing.T) {
	far := time.Date(2300, time.January, 2, 3, 4, 5, 0, time.UTC)
	past := time.Date(1500, time.January, 2, 3, 4, 5, 0, time.UTC)
	root := writeFile(t, t.TempDir(), "odd.tar", buildTar(t,
		tarEntry{name: "future.log", content: textAlpha, mod: far, format: tar.FormatPAX},
		tarEntry{name: "past.log", content: textBeta, mod: past, format: tar.FormatPAX},
		tarEntry{name: "normal.log", content: textGamma, mod: modA, format: tar.FormatPAX},
	))
	var mu sync.Mutex
	detected := map[string]time.Time{}
	parser := claimAll()
	parser.DetectFunc = func(f logparse.File, _ []byte) logparse.Confidence {
		mu.Lock()
		detected[f.Name] = f.ModTime
		mu.Unlock()
		return logparse.MatchContent
	}

	runOK(t, root, testOptions(t, openStore(t), parser))

	for name, want := range map[string]time.Time{"future.log": far, "past.log": past, "normal.log": modA} {
		if got := detected[name]; !sameInstant(got, want) {
			t.Errorf("Detect saw ModTime %v for %s, want %v", got, name, want)
		}
	}
	for _, f := range parser.Parsed() {
		want := modA
		if f.Name != "normal.log" {
			want = time.Time{}
		}
		if !f.ModTime.Equal(want) || f.ModTime.IsZero() != want.IsZero() {
			t.Errorf("Parse got ModTime %v for %s, want %v (unknown when outside the storable range)", f.ModTime, f.Name, want)
		}
	}
}

func TestRun_InterruptedBeforeAnythingIsStored(t *testing.T) {
	root := writeFile(t, t.TempDir(), "a.log", []byte(textAlpha))
	fake := &storetest.Fake{}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	sum, err := Run(ctx, root, testOptions(t, fake, claimAll()))

	if !errors.Is(err, context.Canceled) || !sum.Interrupted {
		t.Errorf("Run(cancelled) = Interrupted %v, error %v, want true and context.Canceled", sum.Interrupted, err)
	}
	if len(fake.Batches()) != 0 {
		t.Errorf("batches written after a cancelled context = %d, want 0", len(fake.Batches()))
	}
}

// ---- AC-I5: summary ----

// stepClock returns fixedNow plus one second per call.
type stepClock struct{ calls atomic.Int64 }

func (c *stepClock) now() time.Time {
	return fixedNow.Add(time.Duration(c.calls.Add(1)-1) * time.Second)
}

// checkSummaryFiles checks the per-file results of the summary test.
func checkSummaryFiles(t *testing.T, sum Summary) {
	t.Helper()
	a := resultFor(t, sum, "a.log")
	if a.Outcome != OutcomeImported || a.SourceType != "syslog" || a.Lines != 4 || a.Records != 3 || a.Skipped != 1 {
		t.Errorf("a.log = %+v, want imported, syslog, 4 lines, 3 records, 1 skipped", a)
	}
	if !a.First.Equal(testBase.Add(time.Second)) || !a.Last.Equal(testBase.Add(4*time.Second)) {
		t.Errorf("a.log First, Last = %v, %v, want %v, %v", a.First, a.Last, testBase.Add(time.Second), testBase.Add(4*time.Second))
	}
	if want := []Problem{{Line: 2, Reason: "comment line"}}; !slices.Equal(a.Problems, want) {
		t.Errorf("a.log Problems = %+v, want %+v", a.Problems, want)
	}
	b := resultFor(t, sum, "b.log")
	if b.Outcome != OutcomeImported || b.Lines != 2 || b.Records != 2 || b.Skipped != 0 || len(b.Problems) != 0 {
		t.Errorf("b.log = %+v, want imported, 2 lines, 2 records, nothing skipped", b)
	}
	if got := resultFor(t, sum, "c.log"); got.Outcome != OutcomeAlreadyImported || got.SourceType != "syslog" {
		t.Errorf("c.log = %+v, want already_imported with the source type", got)
	}
	d := resultFor(t, sum, "d.bin")
	if d.Outcome != OutcomeUnrecognized || d.SourceType != "" || d.Reason != "no parser recognized the file" {
		t.Errorf("d.bin = %+v, want unrecognized, no source type, reason %q", d, "no parser recognized the file")
	}
	if e := resultFor(t, sum, "e.log"); e.Outcome != OutcomeUnrecognized || e.Reason != "empty" {
		t.Errorf("e.log = %+v, want unrecognized with reason %q", e, "empty")
	}
}

// checkSummaryTotals checks counts, time range and clock of the summary test.
func checkSummaryTotals(t *testing.T, sum Summary) {
	t.Helper()
	checkTotals(t, sum)
	if sum.Count(OutcomeImported) != 2 || sum.Count(OutcomeAlreadyImported) != 1 || sum.Count(OutcomeUnrecognized) != 2 || sum.Count(OutcomeFailed) != 0 {
		t.Errorf("Count(imported, already, unrecognized, failed) = %d, %d, %d, %d, want 2, 1, 2, 0",
			sum.Count(OutcomeImported), sum.Count(OutcomeAlreadyImported), sum.Count(OutcomeUnrecognized), sum.Count(OutcomeFailed))
	}
	if !sum.First.Equal(testBase.Add(time.Second)) || !sum.Last.Equal(testBase.Add(4*time.Second)) {
		t.Errorf("Summary First, Last = %v, %v, want %v, %v", sum.First, sum.Last, testBase.Add(time.Second), testBase.Add(4*time.Second))
	}
	if !sameInstant(sum.Started, fixedNow) || !sum.Finished.After(sum.Started) {
		t.Errorf("Summary Started, Finished = %v, %v, want Started %v (UTC) and Finished after it", sum.Started, sum.Finished, fixedNow)
	}
}

func TestRun_SummaryHoldsPerFileAndTotals(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	writeFile(t, root, "a.log", []byte("alpha 1\n#comment\nalpha 2\nalpha 3\n"))
	writeFile(t, root, "b.log", []byte(textBeta))
	writeFile(t, root, "c.log", []byte("alpha 1\n#comment\nalpha 2\nalpha 3\n"))
	writeFile(t, root, "d.bin", []byte("not claimed\n"))
	writeFile(t, root, "e.log", nil)
	parser := claimAll()
	parser.DetectFunc = func(f logparse.File, _ []byte) logparse.Confidence {
		if strings.HasSuffix(f.Name, ".log") {
			return logparse.MatchName
		}
		return logparse.NoMatch
	}
	clock := &stepClock{}
	o := testOptions(t, openStore(t), parser)
	o.Now = clock.now

	sum := runOK(t, root, o)

	checkSummaryFiles(t, sum)
	checkSummaryTotals(t, sum)
}

// checkTotals checks that the run's totals are the sums over its files and that every file has one outcome.
func checkTotals(t *testing.T, sum Summary) {
	t.Helper()
	var lines, records, skipped int64
	for _, f := range sum.Files {
		lines += f.Lines
		records += f.Records
		skipped += f.Skipped
	}
	if sum.Lines != lines || sum.Records != records || sum.Skipped != skipped {
		t.Errorf("Summary Lines, Records, Skipped = %d, %d, %d, want the sums over the files %d, %d, %d", sum.Lines, sum.Records, sum.Skipped, lines, records, skipped)
	}
	total := sum.Count(OutcomeImported) + sum.Count(OutcomeAlreadyImported) + sum.Count(OutcomeUnrecognized) + sum.Count(OutcomeFailed)
	if total != len(sum.Files) {
		t.Errorf("sum of Count over the four outcomes = %d, want len(Files) = %d", total, len(sum.Files))
	}
}

func TestRun_SummaryWithoutRecordsHasNoTimeRange(t *testing.T) {
	root := writeFile(t, t.TempDir(), "x.bin", []byte("nobody wants this\n"))
	parser := claimAll()
	parser.DetectFunc = nil

	sum := runOK(t, root, testOptions(t, &storetest.Fake{}, parser))

	if !sum.First.IsZero() || !sum.Last.IsZero() || sum.Records != 0 {
		t.Errorf("Summary First, Last, Records = %v, %v, %d, want zero times and 0 records", sum.First, sum.Last, sum.Records)
	}
	if len(sum.Files) != 1 || sum.Files[0].Outcome != OutcomeUnrecognized {
		t.Errorf("Files = %+v, want one unrecognized file", sum.Files)
	}
}

func TestRun_LinesAreNewlinesPlusOneForALastLineWithoutOne(t *testing.T) {
	tests := []struct {
		content string
		want    int64
	}{
		{"a\nb\n", 2}, {"a\nb", 2}, {"a\n\n", 2}, {"\n", 1}, {"\n\n\n", 3}, {"abc", 1}, {"a\r\nb\r\n", 2}, {"x", 1},
	}
	for _, tc := range tests {
		t.Run(strconv.Quote(tc.content), func(t *testing.T) {
			root := writeFile(t, t.TempDir(), "f.log", []byte(tc.content))
			parser := claimAll()
			parser.ParseFunc = nil

			sum := runOK(t, root, testOptions(t, &storetest.Fake{}, parser))

			if len(sum.Files) != 1 || sum.Files[0].Lines != tc.want || sum.Lines != tc.want {
				t.Errorf("Lines of %q = %+v (total %d), want %d", tc.content, sum.Files, sum.Lines, tc.want)
			}
		})
	}
}

func TestRun_KeepsAtMostMaxProblemsPerFile(t *testing.T) {
	root := writeFile(t, t.TempDir(), "f.log", []byte(textAlpha))
	parser := claimAll()
	parser.ParseFunc = func(_ context.Context, _ logparse.File, _ io.Reader, out logparse.Emitter) error {
		for i := 1; i <= 25; i++ {
			out.Skip(int64(i), "bad line "+strconv.Itoa(i))
		}
		return nil
	}

	sum := runOK(t, root, testOptions(t, &storetest.Fake{}, parser))

	r := sum.Files[0]
	if r.Skipped != 25 || sum.Skipped != 25 {
		t.Errorf("Skipped = %d (total %d), want 25", r.Skipped, sum.Skipped)
	}
	if len(r.Problems) != MaxProblems {
		t.Fatalf("len(Problems) = %d, want MaxProblems (%d)", len(r.Problems), MaxProblems)
	}
	for i, p := range r.Problems {
		if want := (Problem{Line: int64(i + 1), Reason: "bad line " + strconv.Itoa(i+1)}); p != want {
			t.Errorf("Problems[%d] = %+v, want the first problems in order: %+v", i, p, want)
		}
	}
}

// ---- AC-I6: per-file failures, the run continues ----

var errBoom = errors.New("boom")

// failedReason returns the reason of the failed file at path p and fails the test when it did not fail.
func failedReason(t *testing.T, sum Summary, p string) string {
	t.Helper()
	r := resultFor(t, sum, p)
	if r.Outcome != OutcomeFailed {
		t.Fatalf("result for %q = %+v, want outcome failed", p, r)
	}
	if r.Reason == "" {
		t.Errorf("result for %q has no reason, want the failure described", p)
	}
	return r.Reason
}

func TestRun_CorruptCompressedFileFailsAndTheRunContinues(t *testing.T) {
	full := gz(t, numbered("x", 5000))
	flipped := slices.Clone(full)
	flipped[len(flipped)/2] ^= 0xff
	tests := []struct {
		name string
		data []byte
	}{
		{"truncated gzip", full[:len(full)/2]},
		{"gzip without its trailer", full[:len(full)-8]},
		{"corrupt gzip data", flipped},
		{"trailing garbage after the gzip member", append(slices.Clone(full), []byte("garbage that is no gzip member")...)},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			root := filepath.Join(t.TempDir(), "in")
			writeFile(t, root, "a-bad.gz", tc.data)
			writeFile(t, root, "b-good.log", []byte(textAlpha))
			fake := &storetest.Fake{}

			sum := runOK(t, root, testOptions(t, fake, claimAll()))

			failedReason(t, sum, "a-bad.gz")
			if good := resultFor(t, sum, "b-good.log"); good.Outcome != OutcomeImported {
				t.Errorf("b-good.log = %+v, want imported (the run continues)", good)
			}
			if got := fakeMessages(fake); !slices.Equal(got, linesOf(textAlpha)) {
				t.Errorf("stored messages = %d, want only the good file's %d (nothing of the corrupt file)", len(got), len(linesOf(textAlpha)))
			}
			if starts := fake.ImportStarts(); len(starts) != 1 {
				t.Errorf("BeginImport calls = %d, want 1 (the corrupt file is never begun)", len(starts))
			}
		})
	}
}

func TestRun_CorruptTarHeaderFailsTheArchiveAfterItsGoodEntries(t *testing.T) {
	good := buildTar(t, tarEntry{name: "one", content: textAlpha}, tarEntry{name: "two", content: textBeta})
	corrupt := append(slices.Clone(good[:len(good)-1024]), bytes.Repeat([]byte("x"), 512)...)
	root := writeFile(t, t.TempDir(), "bad.tar", corrupt)
	fake := &storetest.Fake{}

	sum := runOK(t, root, testOptions(t, fake, claimAll()))

	failedReason(t, sum, "bad.tar")
	for _, p := range []string{"bad.tar:one", "bad.tar:two"} {
		if r := resultFor(t, sum, p); r.Outcome != OutcomeImported {
			t.Errorf("%s = %+v, want imported", p, r)
		}
	}
	if got, want := fakeMessages(fake), append(linesOf(textAlpha), linesOf(textBeta)...); !slices.Equal(got, want) {
		t.Errorf("stored messages = %q, want the two entries %q", got, want)
	}
}

func TestRun_TruncatedTarFailsTheArchive(t *testing.T) {
	good := buildTar(t, tarEntry{name: "one", content: textAlpha}, tarEntry{name: "two", content: strings.Repeat("filler\n", 200)})
	root := writeFile(t, t.TempDir(), "cut.tar", good[:1024+100])

	sum := runOK(t, root, testOptions(t, &storetest.Fake{}, claimAll()))

	failedReason(t, sum, "cut.tar")
	if r := resultFor(t, sum, "cut.tar:one"); r.Outcome != OutcomeImported {
		t.Errorf("cut.tar:one = %+v, want imported", r)
	}
}

// failAfter returns a ParseFunc that emits the first n lines like logparsetest.Lines and then returns err.
func failAfter(n int, err error) func(context.Context, logparse.File, io.Reader, logparse.Emitter) error {
	return func(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error {
		return logparsetest.Lines("syslog", testBase)(ctx, f, r, &limitedEmitter{Emitter: out, remaining: n, err: err})
	}
}

// limitedEmitter passes n records on and then fails every Record with err.
type limitedEmitter struct {
	logparse.Emitter
	remaining int
	err       error
}

func (l *limitedEmitter) Record(r model.Record) error {
	if l.remaining == 0 {
		return l.err
	}
	l.remaining--
	return l.Emitter.Record(r)
}

func TestRun_ParserErrorFailsTheFileAndKeepsEarlierRecords(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	writeFile(t, root, "a.log", []byte(numbered("alpha", 6)))
	writeFile(t, root, "b.log", []byte(textBeta))
	parser := claimAll()
	calls := 0
	inner := failAfter(2, errors.New("parser exploded"))
	parser.ParseFunc = func(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error {
		calls++
		if calls == 1 {
			return inner(ctx, f, r, out)
		}
		return logparsetest.Lines("syslog", testBase)(ctx, f, r, out)
	}
	fake := &storetest.Fake{}

	sum := runOK(t, root, testOptions(t, fake, parser))

	if reason := failedReason(t, sum, "a.log"); !strings.Contains(reason, "parser exploded") {
		t.Errorf("a.log reason = %q, want it to contain the parser's error", reason)
	}
	if a := resultFor(t, sum, "a.log"); a.Records != 2 {
		t.Errorf("a.log Records = %d, want 2 (the records emitted before the error)", a.Records)
	}
	if b := resultFor(t, sum, "b.log"); b.Outcome != OutcomeImported {
		t.Errorf("b.log = %+v, want imported", b)
	}
	if got, want := fakeMessages(fake), []string{"alpha 1", "alpha 2", "beta 1", "beta 2"}; !slices.Equal(got, want) {
		t.Errorf("stored messages = %q, want %q", got, want)
	}
	for _, b := range fake.Batches() {
		if b.Import != nil && b.Import.FileID == 1 && b.Import.Complete {
			t.Errorf("batch %+v completes the failed file, want it left incomplete", b.Import)
		}
	}
}

func TestRun_RefusedRecordsAreSkippedWithAProblem(t *testing.T) {
	root := writeFile(t, t.TempDir(), "a.log", []byte(textAlpha))
	rec := func(msg string, origin model.Origin, at time.Time) model.Record {
		seq := uint64(0)
		if origin == model.OriginAgent {
			seq = 1
		}
		return model.Record{
			Meta: model.Meta{Origin: origin, Source: "syslog", Seq: seq, CapturedAt: at},
			Data: &model.LogLine{Log: "syslog", Message: msg},
		}
	}
	parser := claimAll()
	parser.ParseFunc = func(_ context.Context, _ logparse.File, _ io.Reader, out logparse.Emitter) error {
		for _, r := range []model.Record{
			rec("good 1", model.OriginImport, testBase),
			rec("agent origin", model.OriginAgent, testBase),
			rec("good 2", model.OriginImport, testBase.Add(time.Second)),
			rec("too far", model.OriginImport, time.Date(2300, 1, 1, 0, 0, 0, 0, time.UTC)),
			rec("good 3", model.OriginImport, testBase.Add(2*time.Second)),
		} {
			if err := out.Record(r); err != nil {
				return err
			}
		}
		return nil
	}
	s := openStore(t)

	sum := runOK(t, root, testOptions(t, s, parser))

	r := sum.Files[0]
	if r.Outcome != OutcomeImported || r.Records != 3 || r.Skipped != 2 || len(r.Problems) != 2 {
		t.Errorf("result = %+v, want imported, 3 records, 2 skipped, 2 problems", r)
	}
	for _, p := range r.Problems {
		if p.Reason == "" {
			t.Errorf("problem %+v has no reason, want the refusal described", p)
		}
	}
	if got, want := storedMessages(t, s), []string{"good 1", "good 2", "good 3"}; !slices.Equal(got, want) {
		t.Errorf("stored messages = %q, want %q", got, want)
	}
}

func TestRun_StoredImportOfAnotherSourceType(t *testing.T) {
	tests := []struct {
		name     string
		complete bool
		want     Outcome
	}{
		{"incomplete import of another type fails", false, OutcomeFailed},
		{"complete import is already imported whatever its type", true, OutcomeAlreadyImported},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			root := writeFile(t, t.TempDir(), "a.log", []byte(textAlpha))
			fake := &storetest.Fake{OnBeginImport: func(f store.ImportFileStart) (store.ImportFile, error) {
				return store.ImportFile{ID: 3, SHA256: f.SHA256, Size: f.Size, FileName: "a.log", SourceType: "mail", Records: 1, Complete: tc.complete}, nil
			}}

			sum := runOK(t, root, testOptions(t, fake, claimAll()))

			if got := sum.Files[0].Outcome; got != tc.want {
				t.Errorf("outcome = %q, want %q", got, tc.want)
			}
			if tc.want == OutcomeFailed && sum.Files[0].Reason == "" {
				t.Error("failed result has no reason, want the other source type named")
			}
			if len(fake.Batches()) != 0 {
				t.Errorf("batches written = %d, want none", len(fake.Batches()))
			}
		})
	}
}

func TestRun_ImportConflictFailsTheFileAndTheRunContinues(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	writeFile(t, root, "a.log", []byte(textAlpha))
	writeFile(t, root, "b.log", []byte(textBeta))
	var calls atomic.Int64
	fake := &storetest.Fake{OnWrite: func(b store.Batch) (store.WriteResult, error) {
		if calls.Add(1) == 1 {
			return store.WriteResult{}, fmt.Errorf("writing: %w", store.ErrImportConflict)
		}
		return store.WriteResult{Stored: len(b.Records)}, nil
	}}

	sum := runOK(t, root, testOptions(t, fake, claimAll()))

	failedReason(t, sum, "a.log")
	if b := resultFor(t, sum, "b.log"); b.Outcome != OutcomeImported {
		t.Errorf("b.log = %+v, want imported", b)
	}
}

// changeBetweenPasses returns a Progress callback that runs change when the scan is done.
func changeBetweenPasses(t *testing.T, change func()) func(Progress) {
	t.Helper()
	return func(p Progress) {
		if p.Event == EventScanned {
			change()
		}
	}
}

// writeString replaces the content of path with s.
func writeString(t *testing.T, path, s string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(s), 0o600); err != nil {
		t.Fatal(err)
	}
}

// truncateTo shortens path to n bytes.
func truncateTo(t *testing.T, path string, n int64) {
	t.Helper()
	if err := os.Truncate(path, n); err != nil {
		t.Fatal(err)
	}
}

// checkChangedFile imports a.log, which change modifies between the passes, and b.log.
func checkChangedFile(t *testing.T, original string, change func(t *testing.T, path string)) {
	t.Helper()
	root := filepath.Join(t.TempDir(), "in")
	a := writeFile(t, root, "a.log", []byte(original))
	writeFile(t, root, "b.log", []byte(textBeta))
	fake := &storetest.Fake{}
	o := testOptions(t, fake, claimAll())
	o.Progress = changeBetweenPasses(t, func() { change(t, a) })

	sum := runOK(t, root, o)

	if reason := failedReason(t, sum, "a.log"); !strings.Contains(reason, "changed") {
		t.Errorf("a.log reason = %q, want it to say the file changed", reason)
	}
	if b := resultFor(t, sum, "b.log"); b.Outcome != OutcomeImported {
		t.Errorf("b.log = %+v, want imported (the run continues)", b)
	}
	for _, b := range fake.Batches() {
		if b.Import != nil && b.Import.FileID == 1 && b.Import.Complete {
			t.Errorf("batch %+v completes the changed file, want it left incomplete", b.Import)
		}
	}
}

func TestRun_ContentChangedBetweenThePasses(t *testing.T) {
	original := numbered("line", 20)
	tests := []struct {
		name   string
		change func(t *testing.T, path string)
	}{
		{"same size, other bytes", func(t *testing.T, path string) {
			writeString(t, path, strings.Replace(original, "line 1\n", "LINE 1\n", 1))
		}},
		{"shrunk", func(t *testing.T, path string) { truncateTo(t, path, int64(len(original)/2)) }},
		{"emptied", func(t *testing.T, path string) { truncateTo(t, path, 0) }},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { checkChangedFile(t, original, tc.change) })
	}
}

func TestRun_ContentThatOnlyGrewBetweenThePassesIsImportedUpToTheHashedSize(t *testing.T) {
	original := numbered("line", 20)
	root := filepath.Join(t.TempDir(), "in")
	a := writeFile(t, root, "a.log", []byte(original))
	fake := &storetest.Fake{}
	o := testOptions(t, fake, claimAll())
	o.Progress = changeBetweenPasses(t, func() {
		f, err := os.OpenFile(a, os.O_APPEND|os.O_WRONLY, 0)
		if err != nil {
			t.Error(err)
			return
		}
		defer func() { _ = f.Close() }()
		if _, err := f.WriteString("extra 1\nextra 2\n"); err != nil {
			t.Error(err)
		}
	})

	sum := runOK(t, root, o)

	r := sum.Files[0]
	if r.Outcome != OutcomeImported || r.Records != 20 || r.Lines != 20 {
		t.Errorf("result = %+v, want imported with 20 records and 20 lines (the appended bytes are not read)", r)
	}
	if got := fakeMessages(fake); !slices.Equal(got, linesOf(original)) {
		t.Errorf("stored messages = %q, want exactly the first 20 lines", got)
	}
	batches := fake.Batches()
	if len(batches) == 0 || !batches[len(batches)-1].Import.Complete {
		t.Errorf("batches = %+v, want the last one to complete the file", batches)
	}
}

// ---- AC-I7: run-level errors ----

func TestRun_MissingRoot(t *testing.T) {
	fake := &storetest.Fake{}

	sum, err := Run(context.Background(), filepath.Join(t.TempDir(), "missing"), testOptions(t, fake, claimAll()))

	if err == nil {
		t.Error("Run(missing root) error = nil, want an error")
	}
	if len(sum.Files) != 0 || len(fake.ImportStarts()) != 0 {
		t.Errorf("Run(missing root) listed %d files and began %d imports, want none", len(sum.Files), len(fake.ImportStarts()))
	}
}

// checkOptions runs the import of root with the options changed by change and checks whether it is accepted.
func checkOptions(t *testing.T, root string, change func(o *Options), ok bool) {
	t.Helper()
	fake := &storetest.Fake{}
	var events int
	o := testOptions(t, fake, claimAll())
	o.Progress = func(Progress) { events++ }
	change(&o)

	_, err := Run(context.Background(), root, o)

	if ok {
		if err != nil {
			t.Errorf("Run() error = %v, want nil", err)
		}
		return
	}
	if err == nil {
		t.Error("Run() error = nil, want an error")
	}
	if events != 0 || len(fake.ImportStarts()) != 0 {
		t.Errorf("Run() with invalid options reported %d events and began %d imports, want nothing read", events, len(fake.ImportStarts()))
	}
}

func TestRun_InvalidOptions(t *testing.T) {
	root := writeFile(t, t.TempDir(), "a.log", []byte(textAlpha))
	tests := []struct {
		name   string
		change func(o *Options)
		ok     bool
	}{
		{"nil registry", func(o *Options) { o.Parsers = nil }, false},
		{"nil store", func(o *Options) { o.Store = nil }, false},
		{"nil clock", func(o *Options) { o.Now = nil }, false},
		{"BatchRecords above the store's limit", func(o *Options) { o.BatchRecords = store.MaxBatchRecords + 1 }, false},
		{"negative BatchRecords", func(o *Options) { o.BatchRecords = -1 }, false},
		{"negative BatchBytes", func(o *Options) { o.BatchBytes = -1 }, false},
		{"negative ProgressBytes", func(o *Options) { o.ProgressBytes = -1 }, false},
		{"BatchRecords at the store's limit", func(o *Options) { o.BatchRecords = store.MaxBatchRecords }, true},
		{"defaults", func(*Options) {}, true},
		{"one byte per batch and per progress step", func(o *Options) { o.BatchBytes, o.ProgressBytes = 1, 1 }, true},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { checkOptions(t, root, tc.change, tc.ok) })
	}
}

func TestRun_StoreErrorsStopTheRun(t *testing.T) {
	tests := []struct {
		name string
		fake *storetest.Fake
	}{
		{"BeginImport fails", &storetest.Fake{OnBeginImport: func(store.ImportFileStart) (store.ImportFile, error) {
			return store.ImportFile{}, fmt.Errorf("database: %w", errBoom)
		}}},
		{"WriteBatch fails", &storetest.Fake{OnWrite: func(store.Batch) (store.WriteResult, error) {
			return store.WriteResult{}, fmt.Errorf("database: %w", errBoom)
		}}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			root := filepath.Join(t.TempDir(), "in")
			writeFile(t, root, "a.log", []byte(textAlpha))
			writeFile(t, root, "b.log", []byte(textBeta))

			sum, err := Run(context.Background(), root, testOptions(t, tc.fake, claimAll()))

			if !errors.Is(err, errBoom) {
				t.Errorf("Run() error = %v, want it to wrap %v", err, errBoom)
			}
			if got := len(tc.fake.ImportStarts()); got != 1 {
				t.Errorf("BeginImport calls = %d, want 1 (the run stops at the first store error)", got)
			}
			if sum.Interrupted {
				t.Error("Summary.Interrupted = true for a database error, want false")
			}
			if sum.Started.IsZero() {
				t.Error("Summary.Started is zero, want it set although the run stopped")
			}
		})
	}
}

// tarGzOfOneByteEntries returns a tar.gz of n regular one-byte entries, preceded by the extra entries.
func tarGzOfOneByteEntries(t *testing.T, n int, extra ...tarEntry) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	entries := slices.Clone(extra)
	for i := range n {
		entries = append(entries, tarEntry{name: fmt.Sprintf("e%05d", i), content: "x"})
	}
	writeTarEntries(t, tar.NewWriter(zw), entries...)
	if err := zw.Close(); err != nil {
		t.Fatalf("gzip close error = %v, want nil", err)
	}
	return buf.Bytes()
}

// countingParser returns a parser that claims every file and counts its Detect calls.
func countingParser() (*logparsetest.Parser, *atomic.Int64) {
	var calls atomic.Int64
	p := claimAll()
	p.DetectFunc = func(logparse.File, []byte) logparse.Confidence {
		calls.Add(1)
		return logparse.MatchContent
	}
	return p, &calls
}

func TestRun_EntryLimitStopsTheScanInATarGz(t *testing.T) {
	root := writeFile(t, t.TempDir(), "many.tar.gz", tarGzOfOneByteEntries(t, MaxFiles+3))
	parser, detects := countingParser()
	fake := &storetest.Fake{}

	_, err := Run(context.Background(), root, testOptions(t, fake, parser))

	if !errors.Is(err, ErrTooManyFiles) {
		t.Fatalf("Run() error = %v, want an error wrapping ErrTooManyFiles", err)
	}
	if got := detects.Load(); got != MaxFiles {
		t.Errorf("Detect was called %d times, want exactly %d (nothing after entry %d is read)", got, MaxFiles, MaxFiles+1)
	}
	if len(fake.ImportStarts()) != 0 || len(fake.Batches()) != 0 {
		t.Errorf("BeginImport calls = %d, batches = %d, want nothing written", len(fake.ImportStarts()), len(fake.Batches()))
	}
}

func TestRun_EntryLimitStopsTheScanOfADirectory(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	if err := os.Mkdir(root, 0o700); err != nil {
		t.Fatal(err)
	}
	for i := range MaxFiles + 1 {
		if err := os.WriteFile(filepath.Join(root, fmt.Sprintf("f%05d", i)), []byte("x"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	parser, detects := countingParser()
	fake := &storetest.Fake{}

	_, err := Run(context.Background(), root, testOptions(t, fake, parser))

	if !errors.Is(err, ErrTooManyFiles) {
		t.Fatalf("Run() error = %v, want an error wrapping ErrTooManyFiles", err)
	}
	if got := detects.Load(); got != 0 {
		t.Errorf("Detect was called %d times, want 0 (the directory's entries are counted before any file is opened)", got)
	}
	if len(fake.ImportStarts()) != 0 || len(fake.Batches()) != 0 {
		t.Errorf("BeginImport calls = %d, batches = %d, want nothing written", len(fake.ImportStarts()), len(fake.Batches()))
	}
}

func TestRun_EntryLimitCountsTarHeadersExceptPAXGlobalHeaders(t *testing.T) {
	global := tarEntry{name: "global", typ: tar.TypeXGlobalHeader, global: map[string]string{"comment": "x"}}
	dir := tarEntry{name: "d/", typ: tar.TypeDir}
	tests := []struct {
		name      string
		regular   int
		wantError bool
	}{
		{"MaxFiles headers: a directory counts, the global header does not", MaxFiles - 1, false},
		{"one more header", MaxFiles, true},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			root := writeFile(t, t.TempDir(), "many.tar.gz", tarGzOfOneByteEntries(t, tc.regular, global, dir))
			parser := &logparsetest.Parser{TypeName: "syslog"}

			sum, err := Run(context.Background(), root, testOptions(t, &storetest.Fake{}, parser))

			if tc.wantError {
				if !errors.Is(err, ErrTooManyFiles) {
					t.Errorf("Run() error = %v, want an error wrapping ErrTooManyFiles", err)
				}
				return
			}
			if err != nil {
				t.Fatalf("Run() error = %v, want nil", err)
			}
			if got := len(sum.Files); got != tc.regular {
				t.Errorf("listed files = %d, want %d (directory entries and the global header are not listed)", got, tc.regular)
			}
		})
	}
}

// ---- AC-I8: batching ----

// checkBatch checks one import batch of a file: the step, the size and the records.
func checkBatch(t *testing.T, i int, b store.Batch, last bool, fileID, done int64, maxRecords int) {
	t.Helper()
	if b.Import == nil || b.Import.FileID != fileID || b.Import.Done != done {
		t.Errorf("batch %d Import = %+v, want file %d with Done %d", i, b.Import, fileID, done)
		return
	}
	if b.Import.Complete != last {
		t.Errorf("batch %d Complete = %v, want %v (only the last batch completes the file)", i, b.Import.Complete, last)
	}
	if len(b.Records) == 0 && !last {
		t.Errorf("batch %d is empty but not the last one", i)
	}
	if len(b.Records) > maxRecords {
		t.Errorf("batch %d holds %d records, want at most %d", i, len(b.Records), maxRecords)
	}
	if b.AgentID != "" || !sameInstant(b.ReceivedAt, fixedNow) {
		t.Errorf("batch %d AgentID = %q, ReceivedAt = %v, want no agent and %v (UTC)", i, b.AgentID, b.ReceivedAt, fixedNow)
	}
	for j := range b.Records {
		if b.Records[j].Origin != model.OriginImport {
			t.Errorf("batch %d record %d origin = %q, want %q", i, j, b.Records[j].Origin, model.OriginImport)
		}
	}
}

// checkBatches checks the invariants every import batch of one file must keep: the step carries the file and the
// number of records stored before it, only the last batch completes the file and may be empty, no batch holds more
// than maxRecords records, every record has origin import, and ReceivedAt is the clock's time in UTC. It returns the
// number of records in the batches.
func checkBatches(t *testing.T, batches []store.Batch, fileID, done0 int64, maxRecords int) int {
	t.Helper()
	if len(batches) == 0 {
		t.Fatal("no batch was written, want at least the completing one")
	}
	done := done0
	for i, b := range batches {
		checkBatch(t, i, b, i == len(batches)-1, fileID, done, maxRecords)
		done += int64(len(b.Records))
	}
	return int(done - done0)
}

func TestRun_BatchesAreFlushedAtBatchRecords(t *testing.T) {
	tests := []struct {
		name         string
		lines        int
		batchRecords int // 0: the default
		per          int // records per batch except the last
	}{
		{"records in three batches", 8, 3, 3},
		{"an exact multiple of the batch size", 6, 3, 3},
		{"fewer records than the batch size", 2, 3, 3},
		{"one record per batch", 4, 1, 1},
		{"the default batch size", DefaultBatchRecords + 5, 0, DefaultBatchRecords},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			root := writeFile(t, t.TempDir(), "a.log", []byte(numbered("line", tc.lines)))
			fake := &storetest.Fake{}
			o := testOptions(t, fake, claimAll())
			o.BatchRecords = tc.batchRecords
			o.Now = func() time.Time { return fixedNow.In(time.FixedZone("plus2", 7200)) }

			runOK(t, root, o)

			batches := fake.Batches()
			if got := checkBatches(t, batches, 1, 0, tc.per); got != tc.lines {
				t.Errorf("records in the batches = %d, want %d", got, tc.lines)
			}
			for i, b := range batches[:len(batches)-1] {
				if len(b.Records) != tc.per {
					t.Errorf("batch %d holds %d records, want %d (flushed when it holds BatchRecords records)", i, len(b.Records), tc.per)
				}
			}
		})
	}
}

func TestRun_BatchesAreFlushedAtBatchBytes(t *testing.T) {
	const lines = 20000
	line := strings.Repeat("y", 99) + "\n"
	root := writeFile(t, t.TempDir(), "a.log", []byte(strings.Repeat(line, lines)))
	fake := &storetest.Fake{}
	o := testOptions(t, fake, claimAll())
	o.BatchBytes = 16 << 10

	runOK(t, root, o)

	batches := fake.Batches()
	if got := checkBatches(t, batches, 1, 0, DefaultBatchRecords); got != lines {
		t.Errorf("records in the batches = %d, want %d", got, lines)
	}
	longest := 0
	for _, b := range batches {
		longest = max(longest, len(b.Records))
	}
	if longest >= DefaultBatchRecords {
		t.Errorf("largest batch = %d records, want fewer than %d (the 16 KiB byte bound flushes first)", longest, DefaultBatchRecords)
	}
	if len(batches) < 20 {
		t.Errorf("batches = %d, want at least 20 for 2 MB of input with a 16 KiB byte bound", len(batches))
	}
}

func TestRun_ResumeDroppingMoreThanBatchBytesWritesNoEmptyBatch(t *testing.T) {
	content := strings.Repeat(strings.Repeat("z", 99)+"\n", 300)
	root := writeFile(t, t.TempDir(), "a.log", []byte(content))
	s := openStore(t)
	ctx, spy := interrupted(s)
	o := testOptions(t, spy, claimAll())
	o.BatchRecords = 150
	if _, err := Run(ctx, root, o); !errors.Is(err, context.Canceled) {
		t.Fatalf("interrupted Run() error = %v, want context.Canceled", err)
	}
	if got := len(storedMessages(t, s)); got != 150 {
		t.Fatalf("records stored by the interrupted run = %d, want 150", got)
	}
	second := &spyStore{Store: s}
	o2 := testOptions(t, second, claimAll())
	o2.BatchBytes = 1000

	sum, err := Run(context.Background(), root, o2)

	if err != nil {
		t.Fatalf("resumed Run() error = %v, want nil (the store refuses an empty batch that does not complete the file)", err)
	}
	if got := checkBatches(t, second.recorded(), 1, 150, DefaultBatchRecords); got != 150 {
		t.Errorf("records in the resumed run's batches = %d, want 150", got)
	}
	if r := sum.Files[0]; r.ResumedAfter != 150 || r.Records != 150 {
		t.Errorf("result ResumedAfter, Records = %d, %d, want 150, 150", r.ResumedAfter, r.Records)
	}
	if got := len(storedMessages(t, s)); got != 300 {
		t.Errorf("stored records = %d, want 300", got)
	}
}

// skipEverything returns a ParseFunc that reads all lines and reports every one as skipped.
func skipEverything() func(context.Context, logparse.File, io.Reader, logparse.Emitter) error {
	return func(_ context.Context, _ logparse.File, r io.Reader, out logparse.Emitter) error {
		lr := logparse.NewLineReader(r)
		for {
			_, _, err := lr.Next()
			if errors.Is(err, io.EOF) {
				return nil
			}
			if err != nil {
				return err
			}
			out.Skip(lr.Line(), "not a record")
		}
	}
}

func TestRun_SkippingMoreThanBatchBytesWritesOneEmptyCompletingBatch(t *testing.T) {
	for _, batchRecords := range []int{0, 1} {
		t.Run("BatchRecords "+strconv.Itoa(batchRecords), func(t *testing.T) {
			root := writeFile(t, t.TempDir(), "a.log", []byte(strings.Repeat(strings.Repeat("s", 99)+"\n", 100)))
			parser := claimAll()
			parser.ParseFunc = skipEverything()
			fake := &storetest.Fake{}
			o := testOptions(t, fake, parser)
			o.BatchBytes = 500
			o.BatchRecords = batchRecords

			sum := runOK(t, root, o)

			batches := fake.Batches()
			if len(batches) != 1 || len(batches[0].Records) != 0 || batches[0].Import == nil || !batches[0].Import.Complete || batches[0].Import.Done != 0 {
				t.Errorf("batches = %+v, want exactly one empty batch that completes the file", batches)
			}
			if r := sum.Files[0]; r.Outcome != OutcomeImported || r.Skipped != 100 || r.Records != 0 {
				t.Errorf("result = %+v, want imported with 100 skipped and no record", r)
			}
		})
	}
}

// ---- AC-I9: flat memory ----

func TestRun_MemoryStaysFlatForALargeFile(t *testing.T) {
	const (
		lines    = 70000
		lineSize = 1000
	)
	root := filepath.Join(t.TempDir(), "in")
	if err := os.Mkdir(root, 0o700); err != nil {
		t.Fatal(err)
	}
	f, err := os.Create(filepath.Join(root, "big.log.gz"))
	if err != nil {
		t.Fatal(err)
	}
	zw := gzip.NewWriter(f)
	padding := strings.Repeat("x", lineSize-10)
	for i := range lines {
		if _, err := fmt.Fprintf(zw, "%08d %s\n", i, padding); err != nil {
			t.Fatal(err)
		}
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	if err := f.Close(); err != nil {
		t.Fatal(err)
	}

	var mu sync.Mutex
	samples := 0
	var peak uint64
	sample := func() {
		runtime.GC()
		var m runtime.MemStats
		runtime.ReadMemStats(&m)
		mu.Lock()
		samples++
		peak = max(peak, m.HeapAlloc)
		mu.Unlock()
	}
	runtime.GC()
	var base runtime.MemStats
	runtime.ReadMemStats(&base)
	st := &discardStore{onWrite: sample}
	o := testOptions(t, st, claimAll())
	o.ProgressBytes = 4 << 20
	o.Progress = func(p Progress) {
		if p.Event == EventScanProgress {
			sample()
		}
	}

	sum := runOK(t, root, o)

	mu.Lock()
	defer mu.Unlock()
	if samples < 10 {
		t.Fatalf("heap sampled %d times during the import, want at least 10", samples)
	}
	if peak >= base.HeapAlloc+16<<20 {
		t.Errorf("live heap during the import peaked at %d bytes, want below the baseline %d plus 16 MiB", peak, base.HeapAlloc)
	}
	if sum.Lines != lines || sum.Records != lines || st.records.Load() != lines {
		t.Errorf("Lines, Records, stored = %d, %d, %d, want %d each", sum.Lines, sum.Records, st.records.Load(), lines)
	}
}

// ---- AC-I10: progress ----

// progressLog collects the events of a run.
type progressLog struct {
	mu     sync.Mutex
	events []Progress
}

func (l *progressLog) add(p Progress) {
	l.mu.Lock()
	defer l.mu.Unlock()
	if p.Result != nil {
		r := *p.Result
		p.Result = &r
	}
	l.events = append(l.events, p)
}

func (l *progressLog) ofKind(e Event) []Progress {
	l.mu.Lock()
	defer l.mu.Unlock()
	var out []Progress
	for _, p := range l.events {
		if p.Event == e {
			out = append(out, p)
		}
	}
	return out
}

func (l *progressLog) kinds() []Event {
	l.mu.Lock()
	defer l.mu.Unlock()
	out := make([]Event, 0, len(l.events))
	for _, p := range l.events {
		out = append(out, p.Event)
	}
	return out
}

func TestRun_ScanProgressEveryProgressBytes(t *testing.T) {
	tests := []struct {
		name          string
		size          int
		progressBytes int64
		gzip          bool
		want          int
	}{
		{"exact multiple", 1000, 100, false, 10},
		{"remainder does not count", 1050, 100, false, 10},
		{"smaller than one step", 99, 100, false, 0},
		{"exactly one step", 1000, 1000, false, 1},
		{"decompressed bytes of a gzip file", 1000, 100, true, 10},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			content := []byte(strings.Repeat("x", tc.size-1) + "\n")
			name := "f.log"
			if tc.gzip {
				name = "f.log.gz"
				content = gz(t, string(content))
			}
			root := writeFile(t, t.TempDir(), name, content)
			var log progressLog
			o := testOptions(t, &storetest.Fake{}, claimAll())
			o.ProgressBytes = tc.progressBytes
			o.Progress = log.add

			runOK(t, root, o)

			events := log.ofKind(EventScanProgress)
			if len(events) != tc.want {
				t.Fatalf("EventScanProgress events = %d, want %d", len(events), tc.want)
			}
			last := int64(0)
			for i, e := range events {
				if e.Path != name || e.Bytes <= last || e.Bytes > int64(tc.size) {
					t.Errorf("event %d = Path %q, Bytes %d, want Path %q and Bytes increasing within (%d, %d]", i, e.Path, e.Bytes, name, last, tc.size)
				}
				last = e.Bytes
			}
		})
	}
}

func TestRun_ProgressEventsInOrder(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	writeFile(t, root, "a.log", []byte(strings.Repeat("x", 699)+"\n"))
	writeFile(t, root, "b.bin", []byte("unclaimed\n"))
	parser := claimAll()
	parser.DetectFunc = func(f logparse.File, _ []byte) logparse.Confidence {
		if strings.HasSuffix(f.Name, ".log") {
			return logparse.MatchContent
		}
		return logparse.NoMatch
	}
	var log progressLog
	o := testOptions(t, &storetest.Fake{}, parser)
	o.ProgressBytes = 500
	o.Progress = log.add

	runOK(t, root, o)

	want := []Event{EventScanProgress, EventFileFinished, EventScanned, EventFileStarted, EventFileFinished}
	if got := log.kinds(); !slices.Equal(got, want) {
		t.Fatalf("events = %v, want %v", got, want)
	}
	if scan := log.ofKind(EventScanProgress)[0]; scan.Path != "a.log" || scan.Bytes != 500 {
		t.Errorf("scan progress = %+v, want Path a.log and 500 bytes", scan)
	}
	finished := log.ofKind(EventFileFinished)
	if r := finished[0].Result; r == nil || r.Path != "b.bin" || r.Outcome != OutcomeUnrecognized || finished[0].Path != "b.bin" {
		t.Errorf("first EventFileFinished = %+v, want the listed file b.bin as unrecognized", finished[0])
	}
	if scanned := log.ofKind(EventScanned)[0]; scanned.Files != 2 || scanned.Pending != 1 {
		t.Errorf("EventScanned = Files %d, Pending %d, want 2 and 1", scanned.Files, scanned.Pending)
	}
	if started := log.ofKind(EventFileStarted)[0]; started.Path != "a.log" || started.SourceType != "syslog" {
		t.Errorf("EventFileStarted = %+v, want Path a.log and SourceType syslog", started)
	}
	if r := finished[1].Result; r == nil || r.Path != "a.log" || r.Outcome != OutcomeImported || r.Lines != 1 {
		t.Errorf("last EventFileFinished = %+v, want the imported file a.log with 1 line", finished[1])
	}
}

func TestRun_FileProgressEveryProgressLines(t *testing.T) {
	root := writeFile(t, t.TempDir(), "a.log", []byte(strings.Repeat("x\n", 250000)))
	parser := claimAll()
	parser.ParseFunc = func(_ context.Context, _ logparse.File, r io.Reader, _ logparse.Emitter) error {
		_, err := io.Copy(io.Discard, r)
		return err
	}
	var log progressLog
	o := testOptions(t, &storetest.Fake{}, parser)
	o.Progress = log.add

	runOK(t, root, o)

	events := log.ofKind(EventFileProgress)
	if len(events) != 2 {
		t.Fatalf("EventFileProgress events = %d, want 2 (at 100000 and 200000 lines)", len(events))
	}
	for i, e := range events {
		if want := int64(i+1) * ProgressLines; e.Lines != want || e.Path != "a.log" {
			t.Errorf("event %d = Path %q, Lines %d, want a.log and %d", i, e.Path, e.Lines, want)
		}
	}
}

// ---- AC-I11: parser stops early, context ----

func TestRun_ParserThatStopsEarlyStillGetsTheWholeContentHashed(t *testing.T) {
	content := numbered("line", 50)
	root := writeFile(t, t.TempDir(), "a.log", []byte(content))
	parser := claimAll()
	parser.ParseFunc = func(_ context.Context, _ logparse.File, r io.Reader, out logparse.Emitter) error {
		lr := logparse.NewLineReader(r)
		line, _, err := lr.Next()
		if err != nil {
			return err
		}
		return out.Record(model.Record{
			Meta: model.Meta{Origin: model.OriginImport, Source: "syslog", CapturedAt: testBase},
			Data: &model.LogLine{Log: "syslog", Message: string(line)},
		})
	}
	s := openStore(t)
	o := testOptions(t, s, parser)

	first := runOK(t, root, o)

	if r := first.Files[0]; r.Outcome != OutcomeImported || r.Lines != 50 || r.Records != 1 {
		t.Errorf("result = %+v, want imported, 50 lines counted, 1 record", r)
	}
	if second := runOK(t, root, o); second.Files[0].Outcome != OutcomeAlreadyImported {
		t.Errorf("second run outcome = %q, want already_imported (the file was completed)", second.Files[0].Outcome)
	}
}

func TestRun_ParserIgnoringTheContextIsStoppedByTheReader(t *testing.T) {
	const size = 1 << 20
	root := writeFile(t, t.TempDir(), "a.log", bytes.Repeat([]byte("0123456789abcde\n"), size/16))
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	read := 0
	parser := claimAll()
	parser.ParseFunc = func(_ context.Context, _ logparse.File, r io.Reader, _ logparse.Emitter) error {
		buf := make([]byte, 16)
		for {
			n, err := r.Read(buf)
			read += n
			if err != nil {
				return err
			}
			cancel()
		}
	}
	fake := &storetest.Fake{}

	sum, err := Run(ctx, root, testOptions(t, fake, parser))

	if !errors.Is(err, context.Canceled) || !sum.Interrupted {
		t.Errorf("Run() = Interrupted %v, error %v, want true and context.Canceled", sum.Interrupted, err)
	}
	if read >= size {
		t.Errorf("the parser read %d bytes, want the reader to stop it before the end (%d)", read, size)
	}
	for _, b := range fake.Batches() {
		if b.Import != nil && b.Import.Complete {
			t.Errorf("batch %+v completes the interrupted file, want it left incomplete", b.Import)
		}
	}
}
