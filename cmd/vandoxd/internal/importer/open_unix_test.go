//go:build unix

package importer

import (
	"context"
	"io"
	"net"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"syscall"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store/storetest"
)

// mkfifo creates a FIFO at path.
func mkfifo(t *testing.T, path string) {
	t.Helper()
	if err := syscall.Mkfifo(path, 0o600); err != nil {
		t.Skipf("Mkfifo(%q) error = %v, the file system does not support FIFOs", path, err)
	}
}

// withinGuard runs f and fails the test when it does not return within guard (a blocked open of a FIFO).
func withinGuard(t *testing.T, what string, f func()) {
	t.Helper()
	done := make(chan struct{})
	go func() {
		defer close(done)
		f()
	}()
	select {
	case <-done:
	case <-time.After(guard):
		t.Fatalf("%s did not return, want it to refuse a FIFO without blocking", what)
	}
}

func openRoot(t *testing.T, dir string) *os.Root {
	t.Helper()
	root, err := os.OpenRoot(dir)
	if err != nil {
		t.Fatalf("OpenRoot(%q) error = %v, want nil", dir, err)
	}
	t.Cleanup(func() { _ = root.Close() })
	return root
}

func TestOpenSource_RefusesFIFOAndSocketWithoutOpeningThem(t *testing.T) {
	dir := t.TempDir()
	fifo := filepath.Join(dir, "pipe")
	mkfifo(t, fifo)
	socket := filepath.Join(dir, "sock")
	l, err := net.Listen("unix", socket)
	if err != nil {
		t.Skipf("Listen(unix) error = %v, no unix sockets here", err)
	}
	defer func() { _ = l.Close() }()
	for name, path := range map[string]string{"FIFO": fifo, "socket": socket} {
		t.Run(name, func(t *testing.T) {
			withinGuard(t, "openSource", func() {
				src, err := openSource(path)
				if err == nil {
					_ = src.root.Close()
					t.Errorf("openSource(%s) error = nil, want an error", name)
				}
			})
		})
	}
}

func TestRun_FIFOAsRootFailsWithoutBlocking(t *testing.T) {
	fifo := filepath.Join(t.TempDir(), "pipe")
	mkfifo(t, fifo)
	fake := &storetest.Fake{}
	o := testOptions(t, fake, claimAll())

	withinGuard(t, "Run", func() {
		if _, err := Run(context.Background(), fifo, o); err == nil {
			t.Error("Run(FIFO) error = nil, want an error")
		}
	})
	if len(fake.ImportStarts()) != 0 {
		t.Errorf("BeginImport calls = %d, want none", len(fake.ImportStarts()))
	}
}

func TestRun_FIFOInTheDirectoryIsListedAndNeverOpened(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	writeFile(t, root, "a.log", []byte(textAlpha))
	mkfifo(t, filepath.Join(root, "b-pipe"))
	writeFile(t, root, "c.log", []byte(textBeta))
	fake := &storetest.Fake{}
	o := testOptions(t, fake, claimAll())
	var sum Summary
	var err error

	withinGuard(t, "Run", func() { sum, err = Run(context.Background(), root, o) })

	if err != nil {
		t.Fatalf("Run() error = %v, want nil", err)
	}

	pipe := resultFor(t, sum, "b-pipe")
	if pipe.Outcome != OutcomeUnrecognized || pipe.Reason != "not a regular file" {
		t.Errorf("b-pipe = %+v, want unrecognized with the reason %q", pipe, "not a regular file")
	}
	if got, want := fakeMessages(fake), append(linesOf(textAlpha), linesOf(textBeta)...); !slices.Equal(got, want) {
		t.Errorf("stored messages = %q, want the regular files %q", got, want)
	}
}

func TestRun_ListsEveryFormOfTheInputThatIsNotImported(t *testing.T) {
	root := filepath.Join(t.TempDir(), "in")
	writeFile(t, root, "a-empty", nil)
	writeFile(t, root, "b-bzip2", []byte("BZh91AY&SYxxxx"))
	writeFile(t, root, "c-twice.gz", gz(t, string(gz(t, textAlpha))))
	if err := os.Symlink("a-empty", filepath.Join(root, "d-link")); err != nil {
		t.Fatal(err)
	}
	mkfifo(t, filepath.Join(root, "e-fifo"))
	writeFile(t, root, "f-unclaimed", []byte("beta\n"))
	parser := alphaParser()

	sum := runOK(t, root, testOptions(t, &storetest.Fake{}, parser))

	want := map[string]string{
		"a-empty":     "empty",
		"b-bzip2":     "unsupported format: bzip2",
		"c-twice.gz":  "compressed twice",
		"d-link":      "symbolic link (not followed)",
		"e-fifo":      "not a regular file",
		"f-unclaimed": "no parser recognized the file",
	}
	if len(sum.Files) != len(want) {
		t.Fatalf("listed %d files, want %d: %+v", len(sum.Files), len(want), sum.Files)
	}
	for path, reason := range want {
		if r := resultFor(t, sum, path); r.Outcome != OutcomeUnrecognized || r.Reason != reason {
			t.Errorf("%s = %q, %q, want unrecognized with the reason %q", path, r.Outcome, r.Reason, reason)
		}
	}
	if sum.Count(OutcomeUnrecognized) != len(want) {
		t.Errorf("Count(unrecognized) = %d, want %d", sum.Count(OutcomeUnrecognized), len(want))
	}
}

// openCase is one case of the open tests: refused, or the expectation of a successful open.
type openCase struct {
	name    string
	file    string
	want    string // content of a file, or the sorted names of a directory
	refused bool
}

// checkRefused checks that open returns an error, without blocking.
func checkRefused(t *testing.T, what, name string, open func() (*os.File, error)) {
	t.Helper()
	withinGuard(t, what, func() {
		f, err := open()
		if err == nil {
			_ = f.Close()
			t.Errorf("%s(%q) error = nil, want an error", what, name)
		}
	})
}

// checkOpenedContent checks that open succeeds and the file holds want.
func checkOpenedContent(t *testing.T, name string, open func() (*os.File, error), want string) {
	t.Helper()
	withinGuard(t, "openRegular", func() {
		f, err := open()
		if err != nil {
			t.Errorf("openRegular(%q) error = %v, want nil", name, err)
			return
		}
		defer func() { _ = f.Close() }()
		if data, err := io.ReadAll(f); err != nil || string(data) != want {
			t.Errorf("content of openRegular(%q) = %q, %v, want %q", name, data, err, want)
		}
	})
}

// openRegularFixture creates the root of the openRegular cases and returns it with a directory outside of it.
func openRegularFixture(t *testing.T) (root *os.Root, outside string) {
	t.Helper()
	dir := t.TempDir()
	outside = t.TempDir()
	writeFile(t, outside, "secret.txt", []byte("outside"))
	rootDir := filepath.Join(dir, "root")
	writeFile(t, rootDir, "plain.log", []byte("plain content"))
	writeFile(t, rootDir, "sub/nested.log", []byte("nested content"))
	writeFile(t, rootDir, "real/inner.log", []byte("inner content"))
	mkfifo(t, filepath.Join(rootDir, "pipe"))
	links := map[string]string{
		"link-to-inside":  "plain.log",
		"link-to-outside": "../../" + filepath.Base(outside) + "/secret.txt",
		"link-absolute":   filepath.Join(outside, "secret.txt"),
		"link-to-dir":     "real",
		"link-swapped":    outside,
	}
	for name, target := range links {
		if err := os.Symlink(target, filepath.Join(rootDir, name)); err != nil {
			t.Fatalf("Symlink(%s) error = %v, want nil", name, err)
		}
	}
	return openRoot(t, rootDir), outside
}

func TestOpenRegular(t *testing.T) {
	r, outside := openRegularFixture(t)
	secret := filepath.Join(outside, "secret.txt")
	tests := []openCase{
		{"regular file", "plain.log", "plain content", false},
		{"file in a subdirectory", "sub/nested.log", "nested content", false},
		{"symbolic link to a regular file inside the root", "link-to-inside", "plain content", false},
		{"directory", "sub", "", true},
		{"FIFO", "pipe", "", true},
		{"missing", "missing.log", "", true},
		{"symbolic link leaving the root", "link-to-outside", "", true},
		{"symbolic link with an absolute target", "link-absolute", "", true},
		{"symbolic link to a directory", "link-to-dir", "", true},
		{"file below a symbolic link to a directory outside the root", "link-swapped/secret.txt", "", true},
		{"name leaving the root with ..", "../root/../../" + filepath.Base(outside) + "/secret.txt", "", true},
		{"name starting with ..", "../plain.log", "", true},
		{"absolute name", secret, "", true},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			open := func() (*os.File, error) { return openRegular(r, tc.file) }
			if tc.refused {
				checkRefused(t, "openRegular", tc.file, open)
				return
			}
			checkOpenedContent(t, tc.file, open, tc.want)
		})
	}
}

// checkOpenedDir checks that open succeeds and the directory holds the names in want, joined by a slash.
func checkOpenedDir(t *testing.T, name string, open func() (*os.File, error), want []string) {
	t.Helper()
	withinGuard(t, "openDir", func() {
		d, err := open()
		if err != nil {
			t.Errorf("openDir(%q) error = %v, want nil", name, err)
			return
		}
		defer func() { _ = d.Close() }()
		names, err := d.Readdirnames(-1)
		slices.Sort(names)
		if err != nil || !slices.Equal(names, want) {
			t.Errorf("entries of openDir(%q) = %q, %v, want %q", name, names, err, want)
		}
	})
}

func TestOpenDir(t *testing.T) {
	rootDir := filepath.Join(t.TempDir(), "root")
	writeFile(t, rootDir, "sub/a.log", nil)
	writeFile(t, rootDir, "sub/deeper/b.log", nil)
	writeFile(t, rootDir, "plain.log", []byte("x"))
	mkfifo(t, filepath.Join(rootDir, "pipe"))
	if err := os.Symlink(t.TempDir(), filepath.Join(rootDir, "link-outside")); err != nil {
		t.Fatal(err)
	}
	r := openRoot(t, rootDir)
	opened := []struct {
		name string
		dir  string
		want []string
	}{
		{"directory in the root", "sub", []string{"a.log", "deeper"}},
		{"nested directory", "sub/deeper", []string{"b.log"}},
		{"the root itself", ".", []string{"link-outside", "pipe", "plain.log", "sub"}},
	}
	for _, tc := range opened {
		t.Run(tc.name, func(t *testing.T) {
			checkOpenedDir(t, tc.dir, func() (*os.File, error) { return openDir(r, tc.dir) }, tc.want)
		})
	}
	refused := map[string]string{
		"regular file": "plain.log", "FIFO": "pipe", "missing": "missing",
		"symbolic link to a directory outside the root": "link-outside", "name leaving the root": "../root/..",
	}
	for name, dir := range refused {
		t.Run(name, func(t *testing.T) {
			checkRefused(t, "openDir", dir, func() (*os.File, error) { return openDir(r, dir) })
		})
	}
}

func TestRun_SwappedDirectoryLinkNeverReadsOutsideTheRoot(t *testing.T) {
	outside := t.TempDir()
	writeFile(t, outside, "secret.log", []byte("alpha secret outside the root\n"))
	root := filepath.Join(t.TempDir(), "in")
	writeFile(t, root, "sub/x.log", []byte(textAlpha))
	fake := &storetest.Fake{}
	o := testOptions(t, fake, claimAll())
	o.Progress = func(p Progress) {
		if p.Event != EventScanned {
			return
		}
		// Between the passes replace the directory by a link that leaves the root.
		if err := os.RemoveAll(filepath.Join(root, "sub")); err != nil {
			t.Error(err)
			return
		}
		if err := os.Symlink(outside, filepath.Join(root, "sub")); err != nil {
			t.Error(err)
		}
	}

	sum := runOK(t, root, o)

	for _, m := range fakeMessages(fake) {
		if strings.Contains(m, "secret") {
			t.Errorf("stored message %q comes from outside the root, want nothing read there", m)
		}
	}
	if r := resultFor(t, sum, "sub/x.log"); r.Outcome != OutcomeFailed {
		t.Errorf("sub/x.log = %+v, want failed (the directory was swapped for a link leaving the root)", r)
	}
}
