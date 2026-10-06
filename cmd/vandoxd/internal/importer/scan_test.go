package importer

import (
	"archive/tar"
	"compress/gzip"
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store/storetest"
	"github.com/LarsLaskowski/Vandox/internal/logparse"
	"github.com/LarsLaskowski/Vandox/internal/logparse/logparsetest"
)

// alphaParser claims the files whose content starts with "alpha".
func alphaParser() *logparsetest.Parser {
	return &logparsetest.Parser{
		TypeName:   "syslog",
		DetectFunc: logparsetest.HeadPrefix("alpha", logparse.MatchContent),
		ParseFunc:  logparsetest.Lines("syslog", testBase),
	}
}

// scanOf scans root with the parsers; the returned error is the scan's.
func scanOf(t *testing.T, root string, parsers ...logparse.Parser) ([]found, error) {
	t.Helper()
	return scanWith(t, root, testOptions(t, &storetest.Fake{}, parsers...))
}

// scanWith scans root with o.
func scanWith(t *testing.T, root string, o Options) ([]found, error) {
	t.Helper()
	src, err := openSource(root)
	if err != nil {
		t.Fatalf("openSource(%q) error = %v, want nil", root, err)
	}
	defer func() { _ = src.root.Close() }()
	if o.ProgressBytes == 0 {
		o.ProgressBytes = DefaultProgressBytes
	}
	return scan(context.Background(), src, o)
}

// wantResult is the expected result of one file of a scan; a recognized file has the outcome "recognized".
type wantResult struct {
	path    string
	outcome Outcome
	reason  string
}

const recognized Outcome = "recognized"

// resultsOf summarizes what scan found.
func resultsOf(fs []found) []wantResult {
	out := make([]wantResult, 0, len(fs))
	for _, f := range fs {
		w := wantResult{path: f.result.Path, outcome: f.result.Outcome, reason: f.result.Reason}
		if f.parser != nil {
			w.outcome, w.reason = recognized, ""
		}
		out = append(out, w)
	}
	return out
}

func TestOpenSource(t *testing.T) {
	dir := t.TempDir()
	resolved, err := filepath.EvalSymlinks(dir)
	if err != nil {
		t.Fatal(err)
	}
	writeFile(t, dir, "real/file.log", []byte(textAlpha))
	writeFile(t, dir, "plain.log", []byte(textAlpha))
	for link, target := range map[string]string{"linkdir": "real", "linkfile": "plain.log"} {
		if err := os.Symlink(filepath.Join(dir, target), filepath.Join(dir, link)); err != nil {
			t.Fatalf("Symlink() error = %v, want nil", err)
		}
	}
	tests := []struct {
		name     string
		path     string
		wantRoot string
		wantFile string
	}{
		{"directory", dir, resolved, ""},
		{"subdirectory", filepath.Join(dir, "real"), filepath.Join(resolved, "real"), ""},
		{"regular file through its directory", filepath.Join(dir, "plain.log"), resolved, "plain.log"},
		{"symbolic link to a directory is resolved once", filepath.Join(dir, "linkdir"), filepath.Join(resolved, "real"), ""},
		{"symbolic link to a file is resolved once", filepath.Join(dir, "linkfile"), resolved, "plain.log"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			src, err := openSource(tc.path)
			if err != nil {
				t.Fatalf("openSource(%q) error = %v, want nil", tc.path, err)
			}
			defer func() { _ = src.root.Close() }()

			if src.root.Name() != tc.wantRoot || src.file != tc.wantFile {
				t.Errorf("openSource(%q) = root %q, file %q, want root %q, file %q", tc.path, src.root.Name(), src.file, tc.wantRoot, tc.wantFile)
			}
		})
	}
}

func TestOpenSource_Refuses(t *testing.T) {
	dir := t.TempDir()
	if err := os.Symlink(filepath.Join(dir, "nowhere"), filepath.Join(dir, "dangling")); err != nil {
		t.Fatalf("Symlink() error = %v, want nil", err)
	}
	for name, path := range map[string]string{
		"missing":                  filepath.Join(dir, "missing"),
		"empty path":               "",
		"dangling symbolic link":   filepath.Join(dir, "dangling"),
		"below a regular file":     filepath.Join(writeFile(t, dir, "plain", nil), "x"),
		"missing parent directory": filepath.Join(dir, "no", "such", "file"),
	} {
		t.Run(name, func(t *testing.T) {
			src, err := openSource(path)

			if err == nil {
				_ = src.root.Close()
				t.Errorf("openSource(%q) error = nil, want an error", path)
			}
		})
	}
}

// listedForm is one input whose files scan lists, with what it must list.
type listedForm struct {
	name  string
	build func(t *testing.T, dir string) string // returns the root path
	want  []wantResult
}

func tarForm(name string, entries func(t *testing.T) []tarEntry, want ...wantResult) listedForm {
	return listedForm{name, func(t *testing.T, dir string) string {
		return writeFile(t, dir, "a.tar", buildTar(t, entries(t)...))
	}, want}
}

func fileForm(name string, data func(t *testing.T) []byte, reason string) listedForm {
	return listedForm{name, func(t *testing.T, dir string) string { return writeFile(t, dir, "f", data(t)) },
		[]wantResult{{"f", OutcomeUnrecognized, reason}}}
}

func static(s string) func(*testing.T) []byte { return func(*testing.T) []byte { return []byte(s) } }

func listedForms() []listedForm {
	entry := func(name string, typ byte) func(*testing.T) []tarEntry {
		return func(*testing.T) []tarEntry { return []tarEntry{{name: name, typ: typ, link: "target"}} }
	}
	const notRegular = "not a regular file"
	return []listedForm{
		fileForm("empty file", static(""), "empty"),
		fileForm("gzip of nothing", func(t *testing.T) []byte { return gz(t, "") }, "empty"),
		fileForm("bzip2", static("BZh91AY&SYxxxxxxxx"), "unsupported format: bzip2"),
		fileForm("xz", static("\xfd7zXZ\x00xxxxxxxx"), "unsupported format: xz"),
		fileForm("zstd", static("\x28\xb5\x2f\xfdxxxxxxxx"), "unsupported format: zstd"),
		fileForm("lz4", static("\x04\x22\x4d\x18xxxxxxxx"), "unsupported format: lz4"),
		fileForm("zip", static("PK\x03\x04xxxxxxxx"), "unsupported format: zip"),
		fileForm("7z", static("7z\xbc\xaf\x27\x1cxxxxxxxx"), "unsupported format: 7z"),
		fileForm("gzip of a zstd stream", func(t *testing.T) []byte { return gz(t, "\x28\xb5\x2f\xfdxxxxxxxx") }, "unsupported format: zstd"),
		fileForm("gzip twice", func(t *testing.T) []byte { return gz(t, string(gz(t, textAlpha))) }, "compressed twice"),
		fileForm("no parser claims the content", static("beta 1\n"), "no parser recognized the file"),
		fileForm("binary content nobody claims", static("\x00\x01\x02\xff\xfe"), "no parser recognized the file"),
		fileForm("tar without a magic (V7) is not an archive", func(t *testing.T) []byte { return withoutMagic(buildTar(t, tarEntry{name: "x", content: textAlpha})) },
			"no parser recognized the file"),
		tarForm("symbolic link entry", entry("l", tar.TypeSymlink), wantResult{"a.tar:l", OutcomeUnrecognized, "symbolic link (not followed)"}),
		tarForm("hard link entry", entry("h", tar.TypeLink), wantResult{"a.tar:h", OutcomeUnrecognized, "hard link (the content is in the linked entry)"}),
		tarForm("character device entry", entry("c", tar.TypeChar), wantResult{"a.tar:c", OutcomeUnrecognized, notRegular}),
		tarForm("block device entry", entry("b", tar.TypeBlock), wantResult{"a.tar:b", OutcomeUnrecognized, notRegular}),
		tarForm("fifo entry", entry("f", tar.TypeFifo), wantResult{"a.tar:f", OutcomeUnrecognized, notRegular}),
		tarForm("contiguous file entry", entry("k", tar.TypeCont), wantResult{"a.tar:k", OutcomeUnrecognized, notRegular}),
		tarForm("unknown type entry", entry("z", 'Z'), wantResult{"a.tar:z", OutcomeUnrecognized, notRegular}),
		tarForm("directories and a global header are not listed", func(*testing.T) []tarEntry {
			return []tarEntry{
				{name: "g", typ: tar.TypeXGlobalHeader, global: map[string]string{"comment": "x"}},
				{name: "d/", typ: tar.TypeDir},
				{name: "d/f", content: textAlpha},
			}
		}, wantResult{"a.tar:d/f", recognized, ""}),
		tarForm("tar inside a tar", func(t *testing.T) []tarEntry {
			return []tarEntry{{name: "inner.tar", content: string(buildTar(t, tarEntry{name: "x", content: textAlpha}))}}
		}, wantResult{"a.tar:inner.tar", OutcomeUnrecognized, "archive inside an archive (not opened)"}),
		tarForm("tar.gz inside a tar", func(t *testing.T) []tarEntry {
			return []tarEntry{{name: "inner.tgz", content: string(gz(t, string(buildTar(t, tarEntry{name: "x", content: textAlpha}))))}}
		}, wantResult{"a.tar:inner.tgz", OutcomeUnrecognized, "archive inside an archive (not opened)"}),
		tarForm("entry compressed twice", func(t *testing.T) []tarEntry {
			return []tarEntry{{name: "x.gz", content: string(gz(t, string(gz(t, textAlpha))))}}
		}, wantResult{"a.tar:x.gz", OutcomeUnrecognized, "compressed twice"}),
		tarForm("empty entry", func(*testing.T) []tarEntry { return []tarEntry{{name: "empty"}} },
			wantResult{"a.tar:empty", OutcomeUnrecognized, "empty"}),
		tarForm("unsupported entry", func(*testing.T) []tarEntry { return []tarEntry{{name: "x.zip", content: "PK\x03\x04xxxx"}} },
			wantResult{"a.tar:x.zip", OutcomeUnrecognized, "unsupported format: zip"}),
		tarForm("listed and recognized entries keep their order", func(*testing.T) []tarEntry {
			return []tarEntry{{name: "a", content: textAlpha}, {name: "b", content: "beta\n"}, {name: "c", content: textAlpha}}
		}, wantResult{"a.tar:a", recognized, ""}, wantResult{"a.tar:b", OutcomeUnrecognized, "no parser recognized the file"}, wantResult{"a.tar:c", recognized, ""}),
	}
}

// withoutMagic returns the tar archive b with the magic of its first header removed and the checksum fixed, so that
// it looks like a V7 tar.
func withoutMagic(b []byte) []byte {
	c := slices.Clone(b)
	for i := 257; i < 265; i++ {
		c[i] = 0
	}
	for i := 148; i < 156; i++ {
		c[i] = ' '
	}
	sum := 0
	for _, v := range c[:512] {
		sum += int(v)
	}
	copy(c[148:156], []byte(strings.Repeat("0", 6-len(octal(sum)))+octal(sum)+"\x00 "))
	return c
}

func octal(n int) string {
	if n == 0 {
		return "0"
	}
	var s []byte
	for ; n > 0; n /= 8 {
		s = append([]byte{byte('0' + n%8)}, s...)
	}
	return string(s)
}

func TestScan_ListsWhatItDoesNotImport(t *testing.T) {
	for _, tc := range listedForms() {
		t.Run(tc.name, func(t *testing.T) {
			root := tc.build(t, t.TempDir())

			got, err := scanOf(t, root, alphaParser())

			if err != nil {
				t.Fatalf("scan() error = %v, want nil", err)
			}
			if res := resultsOf(got); !slices.Equal(res, tc.want) {
				t.Errorf("scan() results = %+v, want %+v", res, tc.want)
			}
		})
	}
}

func TestScan_ListsLinksInADirectoryWithoutFollowingThem(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	writeFile(t, root, "real/inside.log", []byte(textAlpha))
	writeFile(t, root, "target.log", []byte(textAlpha))
	for link, target := range map[string]string{"a-linkdir": "real", "b-linkfile": "target.log", "c-dangling": "nowhere"} {
		if err := os.Symlink(target, filepath.Join(root, link)); err != nil {
			t.Fatalf("Symlink() error = %v, want nil", err)
		}
	}
	reason := "symbolic link (not followed)"

	got, err := scanOf(t, root, alphaParser())

	if err != nil {
		t.Fatalf("scan() error = %v, want nil", err)
	}
	want := []wantResult{
		{"a-linkdir", OutcomeUnrecognized, reason},
		{"b-linkfile", OutcomeUnrecognized, reason},
		{"c-dangling", OutcomeUnrecognized, reason},
		{"real/inside.log", recognized, ""},
		{"target.log", recognized, ""},
	}
	if res := resultsOf(got); !slices.Equal(res, want) {
		t.Errorf("scan() results = %+v, want %+v", res, want)
	}
}

// foundRow is what the scan of the pass-two test must find for one file.
type foundRow struct {
	path, name string
	mod        time.Time
	loc        location
	content    string // the decompressed content; "" when the file is not recognized
}

// checkFound compares one found file with its row.
func checkFound(t *testing.T, i int, f found, w foundRow) {
	t.Helper()
	if f.result.Path != w.path || f.file.Name != w.name || !sameInstant(f.file.ModTime, w.mod) {
		t.Errorf("found[%d] = Path %q, Name %q, ModTime %v, want %q, %q, %v", i, f.result.Path, f.file.Name, f.file.ModTime, w.path, w.name, w.mod)
	}
	if f.loc != w.loc {
		t.Errorf("found[%d] (%s) loc = %+v, want %+v", i, w.path, f.loc, w.loc)
	}
	if w.content == "" {
		if f.parser != nil || f.result.Outcome != OutcomeUnrecognized {
			t.Errorf("found[%d] (%s) = parser %v, outcome %q, want no parser and unrecognized", i, w.path, f.parser, f.result.Outcome)
		}
		return
	}
	if f.parser == nil || f.parser.Type() != "syslog" {
		t.Errorf("found[%d] (%s) parser = %v, want the syslog parser", i, w.path, f.parser)
	}
	if f.sum != sha256Of(w.content) || f.size != int64(len(w.content)) {
		t.Errorf("found[%d] (%s) hash %x, size %d, want %x, %d of the decompressed content", i, w.path, f.sum, f.size, sha256Of(w.content), len(w.content))
	}
}

func TestScan_FoundHoldsWhatPassTwoNeeds(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	writeTimed(t, root, "a.log", []byte(textAlpha), modA)
	writeTimed(t, root, "b.log.gz", gz(t, textBeta+"alpha extra\n"), modB)
	writeTimed(t, root, "t.tar.gz", gz(t, string(buildTar(t,
		tarEntry{name: "d/", typ: tar.TypeDir},
		tarEntry{name: "d/x", content: "alpha x\n", mod: modC},
		tarEntry{name: "d/y.gz", content: string(gz(t, "alpha y\n")), mod: modA},
	))), modA)
	writeTimed(t, root, "u.txt", []byte("beta\n"), modB)
	parser := alphaParser()
	parser.DetectFunc = func(f logparse.File, head []byte) logparse.Confidence {
		if strings.HasPrefix(string(head), "beta") && strings.HasSuffix(f.Name, ".txt") {
			return logparse.NoMatch
		}
		return logparse.MatchContent
	}
	want := []foundRow{
		{"a.log", "a.log", modA, location{fsPath: "a.log", entry: -1}, textAlpha},
		{"b.log.gz", "b.log", modB, location{fsPath: "b.log.gz", entry: -1, gzip: true}, textBeta + "alpha extra\n"},
		{"t.tar.gz:d/x", "d/x", modC, location{fsPath: "t.tar.gz", entry: 1, archiveGzip: true}, "alpha x\n"},
		{"t.tar.gz:d/y.gz", "d/y", modA, location{fsPath: "t.tar.gz", entry: 2, archiveGzip: true, gzip: true}, "alpha y\n"},
		{"u.txt", "u.txt", modB, location{fsPath: "u.txt", entry: -1}, ""},
	}

	got, err := scanOf(t, root, parser)

	if err != nil {
		t.Fatalf("scan() error = %v, want nil", err)
	}
	if len(got) != len(want) {
		t.Fatalf("scan() found %d files, want %d: %+v", len(got), len(want), resultsOf(got))
	}
	for i, w := range want {
		checkFound(t, i, got[i], w)
	}
}

func TestScan_ContextCancelled(t *testing.T) {
	root := writeFile(t, t.TempDir(), "a.log", []byte(textAlpha))
	src, err := openSource(root)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = src.root.Close() }()
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	o := testOptions(t, &storetest.Fake{}, alphaParser())
	o.ProgressBytes = DefaultProgressBytes

	_, err = scan(ctx, src, o)

	if !errors.Is(err, context.Canceled) {
		t.Errorf("scan(cancelled ctx) error = %v, want context.Canceled", err)
	}
}

func TestScan_PathLimit(t *testing.T) {
	t.Run("archive entry name over MaxPathBytes fails and is shown cut", testLongEntryName)
	t.Run("archive entry name of exactly MaxPathBytes is read", testExactEntryName)
	t.Run("relative directory path over MaxPathBytes fails", testLongDirectoryPath)
}

// checkTooLong checks that f failed with the reason path too long and is shown with at most limit bytes.
func checkTooLong(t *testing.T, f found, limit int) {
	t.Helper()
	r := f.result
	if r.Outcome != OutcomeFailed || !strings.Contains(r.Reason, "path too long") || f.parser != nil {
		t.Errorf("result = %+v, want failed with the reason path too long", r)
	}
	if len(r.Path) > limit {
		t.Errorf("shown path has %d bytes, want it cut to at most %d", len(r.Path), limit)
	}
}

func testLongEntryName(t *testing.T) {
	long := "a/" + strings.Repeat("b", MaxPathBytes+100)
	root := writeFile(t, t.TempDir(), "long.tar", buildTar(t, tarEntry{name: long, content: textAlpha}, tarEntry{name: "next", content: textAlpha}))

	got, err := scanOf(t, root, alphaParser())

	if err != nil || len(got) != 2 {
		t.Fatalf("scan() = %d files, error %v, want 2 files and nil", len(got), err)
	}
	checkTooLong(t, got[0], len("long.tar:")+MaxPathBytes)
	if !strings.HasPrefix(got[0].result.Path, "long.tar:a/bbb") {
		t.Errorf("shown path = %.30q, want it to start with the archive and the name", got[0].result.Path)
	}
	if got[1].parser == nil {
		t.Error("the entry after the long one was not recognized, want the scan to continue")
	}
}

func testExactEntryName(t *testing.T) {
	root := writeFile(t, t.TempDir(), "exact.tar", buildTar(t, tarEntry{name: strings.Repeat("c", MaxPathBytes), content: textAlpha}))

	got, err := scanOf(t, root, alphaParser())

	if err != nil || len(got) != 1 || got[0].parser == nil {
		t.Errorf("scan() = %+v, error %v, want the entry recognized", resultsOf(got), err)
	}
}

func testLongDirectoryPath(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	deep := root
	for range 6 {
		deep = filepath.Join(deep, strings.Repeat("d", 200))
	}
	writeFile(t, deep, "f.log", []byte(textAlpha))
	writeFile(t, root, "z.log", []byte(textAlpha))

	got, err := scanOf(t, root, alphaParser())

	if err != nil || len(got) != 2 {
		t.Fatalf("scan() = %+v, error %v, want 2 files and nil", resultsOf(got), err)
	}
	checkTooLong(t, got[0], MaxPathBytes+16)
	if got[1].parser == nil {
		t.Error("the file after the long path was not recognized, want the scan to continue")
	}
}

// writeLongNameArchive writes a tar.gz with count regular entries whose PAX path is nameBytes long.
func writeLongNameArchive(t *testing.T, path string, count, nameBytes int) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatalf("MkdirAll(%q) error = %v, want nil", filepath.Dir(path), err)
	}
	f, err := os.Create(path)
	if err != nil {
		t.Fatalf("Create(%q) error = %v, want nil", path, err)
	}
	zw := gzip.NewWriter(f)
	tw := tar.NewWriter(zw)
	for i := range count {
		name := fmt.Sprintf("%05d", i) + strings.Repeat("a", nameBytes-5)
		h := &tar.Header{Name: name, Typeflag: tar.TypeReg, Mode: 0o644, ModTime: modA, Format: tar.FormatPAX}
		if err := tw.WriteHeader(h); err != nil {
			t.Fatalf("WriteHeader(entry %d) error = %v, want nil", i, err)
		}
	}
	if err := tw.Close(); err != nil {
		t.Fatalf("tar Close() error = %v, want nil", err)
	}
	if err := zw.Close(); err != nil {
		t.Fatalf("gzip Close() error = %v, want nil", err)
	}
	if err := f.Close(); err != nil {
		t.Fatalf("Close(%q) error = %v, want nil", path, err)
	}
}

// heapBoundEntries is the number of oversized entries the heap-bound tests list. With names or headers of 1 MB each,
// a scan that pins them grows the live heap by about 38 MiB, well above the 16 MiB bound, and the count stays small
// because these tests are slow under the race detector.
const heapBoundEntries = 40

func TestScan_MemoryStaysBoundedForHugeEntryNames(t *testing.T) {
	const (
		entries   = heapBoundEntries
		nameBytes = 1_000_000
		maxGrowth = 16 << 20
	)
	root := filepath.Join(t.TempDir(), "in")
	writeLongNameArchive(t, filepath.Join(root, "names.tar.gz"), entries, nameBytes)
	runtime.GC()
	var before runtime.MemStats
	runtime.ReadMemStats(&before)

	got, err := scanOf(t, root, alphaParser())

	runtime.GC()
	var after runtime.MemStats
	runtime.ReadMemStats(&after)
	runtime.KeepAlive(got)
	if err != nil || len(got) != entries {
		t.Fatalf("scan() = %d files, error %v, want %d files and nil", len(got), err, entries)
	}
	for i, f := range got {
		if f.result.Outcome != OutcomeFailed || !strings.Contains(f.result.Reason, "path too long") || f.parser != nil {
			t.Fatalf("file %d result = %+v, want failed with the reason path too long", i, f.result)
		}
	}
	if growth := int64(after.HeapAlloc) - int64(before.HeapAlloc); growth > maxGrowth {
		t.Errorf("live heap grew by %d MiB while %d entries with names of %d bytes were listed, want less than %d MiB",
			growth>>20, entries, nameBytes, maxGrowth>>20)
	}
}

// tarEntryOf is one entry of an archive written by writeEntriesArchive.
type tarEntryOf struct {
	hdr     tar.Header
	content string
}

// writeEntriesArchive writes a tar.gz with count entries made by entry(i).
func writeEntriesArchive(t *testing.T, path string, count int, entry func(i int) tarEntryOf) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatalf("MkdirAll(%q) error = %v, want nil", filepath.Dir(path), err)
	}
	f, err := os.Create(path)
	if err != nil {
		t.Fatalf("Create(%q) error = %v, want nil", path, err)
	}
	zw := gzip.NewWriter(f)
	tw := tar.NewWriter(zw)
	for i := range count {
		e := entry(i)
		h := e.hdr
		h.Mode, h.ModTime, h.Format, h.Size = 0o644, modA, tar.FormatPAX, int64(len(e.content))
		if err := tw.WriteHeader(&h); err != nil {
			t.Fatalf("WriteHeader(entry %d) error = %v, want nil", i, err)
		}
		if _, err := tw.Write([]byte(e.content)); err != nil {
			t.Fatalf("Write(entry %d) error = %v, want nil", i, err)
		}
	}
	if err := tw.Close(); err != nil {
		t.Fatalf("tar Close() error = %v, want nil", err)
	}
	if err := zw.Close(); err != nil {
		t.Fatalf("gzip Close() error = %v, want nil", err)
	}
	if err := f.Close(); err != nil {
		t.Fatalf("Close(%q) error = %v, want nil", path, err)
	}
}

// scanHeapGrowth scans root with the alpha parser and returns the result and the growth of the live heap in bytes.
func scanHeapGrowth(t *testing.T, root string) (got []found, growth int64, err error) {
	t.Helper()
	runtime.GC()
	var before runtime.MemStats
	runtime.ReadMemStats(&before)

	got, err = scanOf(t, root, alphaParser())

	runtime.GC()
	var after runtime.MemStats
	runtime.ReadMemStats(&after)
	runtime.KeepAlive(got)
	return got, int64(after.HeapAlloc) - int64(before.HeapAlloc), err
}

// shortNameWant is what a scan of the short-named entries must list.
type shortNameWant struct {
	name       func(i int) string
	wantParser bool
	wantReason string
}

// checkShortNames checks that got lists entries under their short names.
func checkShortNames(t *testing.T, got []found, w shortNameWant) {
	t.Helper()
	for i, f := range got {
		wantName := w.name(i)
		wantPath := "names.tar.gz:" + wantName
		if f.result.Path != wantPath || f.file.Name != wantName {
			t.Fatalf("file %d = path %q, name %q, want path %q, name %q", i, f.result.Path, f.file.Name, wantPath, wantName)
		}
		if (f.parser != nil) != w.wantParser || f.result.Reason != w.wantReason {
			t.Fatalf("file %d = parser %v, reason %q, want parser %v, reason %q",
				i, f.parser != nil, f.result.Reason, w.wantParser, w.wantReason)
		}
	}
}

// checkBoundedScan scans an archive of entries entries made by entry and checks the names and the heap growth.
func checkBoundedScan(t *testing.T, entries int, entry func(i int) tarEntryOf, w shortNameWant) {
	t.Helper()
	const maxGrowth = 16 << 20
	root := filepath.Join(t.TempDir(), "in")
	writeEntriesArchive(t, filepath.Join(root, "names.tar.gz"), entries, entry)

	got, growth, err := scanHeapGrowth(t, root)

	if err != nil || len(got) != entries {
		t.Fatalf("scan() = %d files, error %v, want %d files and nil", len(got), err, entries)
	}
	checkShortNames(t, got, w)
	if growth > maxGrowth {
		t.Errorf("live heap grew by %d MiB while %d entries with oversized headers were kept, want less than %d MiB",
			growth>>20, entries, maxGrowth>>20)
	}
}

// TestScan_MemoryStaysBoundedForHugeNamesThatCleanToShortOnes checks that a short cleaned name does not pin the
// 1 MB raw header name it was cut from, both for a listed entry and for a recognized one.
func TestScan_MemoryStaysBoundedForHugeNamesThatCleanToShortOnes(t *testing.T) {
	const padPairs = 500_000
	tests := []struct {
		name       string
		content    string
		wantParser bool
		wantReason string
	}{
		{"listed as empty", "", false, reasonEmpty},
		{"recognized", "alpha line\n", true, ""},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			pad := strings.Repeat("/.", padPairs)
			entry := func(i int) tarEntryOf {
				return tarEntryOf{hdr: tar.Header{Name: fmt.Sprintf("%05d", i) + pad, Typeflag: tar.TypeReg}, content: tc.content}
			}
			w := shortNameWant{func(i int) string { return fmt.Sprintf("%05d", i) }, tc.wantParser, tc.wantReason}
			checkBoundedScan(t, heapBoundEntries, entry, w)
		})
	}
}

// TestScan_MemoryStaysBoundedForShortNamesWithHugePAXRecords checks that a short name that is already clean, and
// so arrives as a substring of a PAX header of up to 1 MiB, does not pin that header.
func TestScan_MemoryStaysBoundedForShortNamesWithHugePAXRecords(t *testing.T) {
	tests := []struct {
		name       string
		typeflag   byte
		linkname   string
		content    string
		wantParser bool
		wantReason string
	}{
		{"symbolic link", tar.TypeSymlink, "target", "", false, reasonSymlink},
		{"recognized file", tar.TypeReg, "", "alpha line\n", true, ""},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			huge := map[string]string{"comment": strings.Repeat("c", 1_000_000)}
			entry := func(i int) tarEntryOf {
				h := tar.Header{Name: fmt.Sprintf("é%05d.log", i), Typeflag: tc.typeflag, Linkname: tc.linkname, PAXRecords: huge}
				return tarEntryOf{hdr: h, content: tc.content}
			}
			w := shortNameWant{func(i int) string { return fmt.Sprintf("é%05d.log", i) }, tc.wantParser, tc.wantReason}
			checkBoundedScan(t, heapBoundEntries, entry, w)
		})
	}
}

func TestScan_UnreadableFilesAndDirectoriesAreListedAsFailed(t *testing.T) {
	if os.Geteuid() == 0 {
		t.Skip("file permissions do not apply to root")
	}
	root := filepath.Join(t.TempDir(), "in")
	locked := writeFile(t, root, "a-locked.log", []byte(textAlpha))
	writeFile(t, root, "b-dir/inside.log", []byte(textAlpha))
	writeFile(t, root, "c-good.log", []byte(textAlpha))
	if err := os.Chmod(locked, 0); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(filepath.Join(root, "b-dir"), 0); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chmod(filepath.Join(root, "b-dir"), 0o700) })

	got, err := scanOf(t, root, alphaParser())

	if err != nil {
		t.Fatalf("scan() error = %v, want nil", err)
	}
	res := resultsOf(got)
	if len(res) != 3 || res[0].outcome != OutcomeFailed || res[1].outcome != OutcomeFailed || res[2].outcome != recognized {
		t.Fatalf("scan() results = %+v, want the file and the directory failed and the good file recognized", res)
	}
	for _, r := range res[:2] {
		if !strings.Contains(r.reason, "permission denied") || strings.Contains(r.reason, root) {
			t.Errorf("reason of %q = %q, want the OS error without the path repeated", r.path, r.reason)
		}
	}
}
