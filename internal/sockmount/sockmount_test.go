package sockmount

import (
	"go/ast"
	"go/parser"
	"go/token"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"syscall"
	"testing"
	"testing/fstest"
)

var mountLine = regexp.MustCompile(`(?m)^mnt_id:\t([0-9]+)$`)

// mountOf reads the mnt_id of one of the test's own descriptors straight from the real /proc.
func mountOf(t *testing.T, fd int) int32 {
	t.Helper()

	data, err := fs.ReadFile(os.DirFS("/proc"), "self/fdinfo/"+strconv.Itoa(fd))
	if err != nil {
		t.Fatalf("reading /proc/self/fdinfo/%d: %v", fd, err)
	}
	m := mountLine.FindSubmatch(data)
	if m == nil {
		t.Fatalf("/proc/self/fdinfo/%d = %q, want a mnt_id line", fd, data)
	}
	id, err := strconv.ParseInt(string(m[1]), 10, 32)
	if err != nil {
		t.Fatalf("mnt_id %q of descriptor %d: %v", m[1], fd, err)
	}
	return int32(id)
}

// openFDs lists the descriptors of this process.
func openFDs(t *testing.T) []string {
	t.Helper()

	entries, err := os.ReadDir("/proc/self/fd")
	if err != nil {
		t.Fatalf("listing /proc/self/fd: %v", err)
	}
	names := make([]string, 0, len(entries))
	for _, e := range entries {
		names = append(names, e.Name())
	}
	slices.Sort(names)
	return names
}

// fdinfoFS answers every self/fdinfo/<n> with the same content, an open error or a read error.
type fdinfoFS struct {
	data    string
	openErr error
	readErr error
}

func (f fdinfoFS) Open(name string) (fs.File, error) {
	rest, ok := strings.CutPrefix(name, "self/fdinfo/")
	if _, err := strconv.Atoi(rest); !ok || err != nil {
		return nil, &fs.PathError{Op: "open", Path: name, Err: fs.ErrNotExist}
	}
	if f.openErr != nil {
		return nil, &fs.PathError{Op: "open", Path: name, Err: f.openErr}
	}
	file, err := fstest.MapFS{"f": &fstest.MapFile{Data: []byte(f.data)}}.Open("f")
	if err != nil {
		return nil, err
	}
	if f.readErr != nil {
		return failingFile{File: file, err: &fs.PathError{Op: "read", Path: name, Err: f.readErr}}, nil
	}
	return file, nil
}

type failingFile struct {
	fs.File
	err error
}

func (f failingFile) Read([]byte) (int, error) { return 0, f.err }

func TestProbe_ReturnsTheSocketMount(t *testing.T) {
	pair, err := syscall.Socketpair(syscall.AF_UNIX, syscall.SOCK_STREAM|syscall.SOCK_CLOEXEC, 0)
	if err != nil {
		t.Fatalf("Socketpair: %v", err)
	}
	t.Cleanup(func() { _ = syscall.Close(pair[0]); _ = syscall.Close(pair[1]) })
	var pipe [2]int
	if err := syscall.Pipe2(pipe[:], syscall.O_CLOEXEC); err != nil {
		t.Fatalf("Pipe2: %v", err)
	}
	t.Cleanup(func() { _ = syscall.Close(pipe[0]); _ = syscall.Close(pipe[1]) })
	want := mountOf(t, pair[0])
	pipeMount := mountOf(t, pipe[0])

	got, err := Probe(os.DirFS("/proc"))
	if err != nil {
		t.Fatalf("Probe(/proc) error = %v, want nil", err)
	}
	if got != want {
		t.Errorf("Probe(/proc) = %d, want %d (the mnt_id of a socket of this process)", got, want)
	}
	if got == pipeMount {
		t.Errorf("Probe(/proc) = %d, equal to the mnt_id of a pipe, want the sockets' mount", got)
	}
	again, err := Probe(os.DirFS("/proc"))
	if err != nil || again != got {
		t.Errorf("second Probe(/proc) = %d, %v, want %d, nil", again, err, got)
	}
}

func TestProbe_UnreadableFdinfoFails(t *testing.T) {
	cases := []struct {
		name string
		proc fs.FS
	}{
		{"entry missing", fstest.MapFS{}},
		{"permission denied", fdinfoFS{openErr: syscall.EACCES}},
		{"read error", fdinfoFS{data: "pos:\t0\n", readErr: syscall.EIO}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			id, err := Probe(c.proc)
			if err == nil || !strings.HasPrefix(err.Error(), "sockmount: self/fdinfo: ") {
				t.Errorf("Probe() = %d, %v, want an error starting with %q", id, err, "sockmount: self/fdinfo: ")
			}
			if id != 0 {
				t.Errorf("Probe() mount = %d with an error, want 0", id)
			}
		})
	}
}

func TestProbe_MalformedFdinfoFails(t *testing.T) {
	const secret = "TOPSECRETVALUE"
	cases := []struct {
		name, data string
	}{
		{"empty", ""},
		{"garbage", "this is not an fdinfo file\n"},
		{"three lines", "pos:\t0\nflags:\t02000002\nmnt_id:\t10\n"},
		{"mnt_id zero", "pos:\t0\nflags:\t02000002\nmnt_id:\t0\nino:\t6087\n"},
		{"mnt_id above int32", "pos:\t0\nflags:\t02000002\nmnt_id:\t4294967296\nino:\t6087\n"},
		{"ino not digits", "pos:\t0\nflags:\t02000002\nmnt_id:\t10\nino:\t" + secret + "\n"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			id, err := Probe(fdinfoFS{data: c.data})
			if err == nil || !strings.HasPrefix(err.Error(), "sockmount: ") || strings.HasPrefix(err.Error(), "sockmount: socket") {
				t.Fatalf("Probe() = %d, %v, want an error that reports the parse failure with the prefix %q", id, err, "sockmount: ")
			}
			if id != 0 {
				t.Errorf("Probe() mount = %d with an error, want 0", id)
			}
			if strings.Contains(err.Error(), secret) {
				t.Errorf("Probe() error %q holds the file's content", err)
			}
		})
	}
}

func TestProbe_ClosesTheSocket(t *testing.T) {
	_ = openFDs(t) // the first listing may open the runtime's poller

	t.Run("on success", func(t *testing.T) {
		before := openFDs(t)
		if _, err := Probe(os.DirFS("/proc")); err != nil {
			t.Fatalf("Probe(/proc) error = %v, want nil", err)
		}
		if after := openFDs(t); !slices.Equal(before, after) {
			t.Errorf("descriptors after Probe = %v, want %v", after, before)
		}
	})

	failures := []struct {
		name string
		proc fs.FS
		want string
	}{
		{"fdinfo unreadable", fstest.MapFS{}, "sockmount: self/fdinfo: "},
		{"fdinfo read fails", fdinfoFS{readErr: syscall.EIO}, "sockmount: self/fdinfo: "},
		{"fdinfo malformed", fdinfoFS{data: "garbage\n"}, "sockmount: "},
	}
	for _, c := range failures {
		t.Run("on failure: "+c.name, func(t *testing.T) {
			before := openFDs(t)
			_, err := Probe(c.proc)
			if err == nil || !strings.HasPrefix(err.Error(), c.want) {
				t.Errorf("Probe() error = %v, want one starting with %q", err, c.want)
			}
			if after := openFDs(t); !slices.Equal(before, after) {
				t.Errorf("descriptors after a failed Probe = %v, want %v", after, before)
			}
		})
	}
}

func TestSockmount_ImportsOnlyAllowedPackages(t *testing.T) {
	allowedImports := []string{
		"errors", "fmt", "io", "io/fs", "strconv", "syscall",
		"github.com/LarsLaskowski/Vandox/internal/connstats",
	}
	// The probe opens an unbound Unix datagram socket and closes it; nothing binds, connects, sends or listens.
	wantSyscall := []string{"AF_UNIX", "Close", "SOCK_CLOEXEC", "SOCK_DGRAM", "Socket"}

	imports, selectors := productionUses(t)
	for _, path := range imports {
		if !slices.Contains(allowedImports, path) {
			t.Errorf("the package imports %q, want only %v", path, allowedImports)
		}
	}
	if !slices.Equal(selectors, wantSyscall) {
		t.Errorf("syscall selectors used = %v, want exactly %v", selectors, wantSyscall)
	}
}

// productionUses returns the import paths and the sorted syscall selectors of the package's non-test files.
func productionUses(t *testing.T) (imports, selectors []string) {
	t.Helper()

	files, err := filepath.Glob("*.go")
	if err != nil {
		t.Fatalf("listing the package: %v", err)
	}
	fset := token.NewFileSet()
	used := map[string]bool{}
	sources := 0
	for _, name := range files {
		if strings.HasSuffix(name, "_test.go") {
			continue
		}
		sources++
		file, err := parser.ParseFile(fset, name, nil, 0)
		if err != nil {
			t.Fatalf("parsing %s: %v", name, err)
		}
		paths, local := importsOf(t, file)
		imports = append(imports, paths...)
		for _, sel := range selectorsOf(file, local) {
			used[sel] = true
		}
	}
	if sources == 0 {
		t.Fatal("no production file found in the package")
	}
	for name := range used {
		selectors = append(selectors, name)
	}
	slices.Sort(selectors)
	return imports, selectors
}

// importsOf returns the import paths of file and the name under which it imports syscall ("" when it does not).
func importsOf(t *testing.T, file *ast.File) (paths []string, syscallName string) {
	t.Helper()

	for _, imp := range file.Imports {
		path, err := strconv.Unquote(imp.Path.Value)
		if err != nil {
			t.Fatalf("import path %s: %v", imp.Path.Value, err)
		}
		paths = append(paths, path)
		if path == "syscall" {
			syscallName = "syscall"
			if imp.Name != nil {
				syscallName = imp.Name.Name
			}
		}
	}
	return paths, syscallName
}

// selectorsOf returns the names selected from the package imported as pkg.
func selectorsOf(file *ast.File, pkg string) []string {
	var names []string
	if pkg == "" {
		return names
	}
	ast.Inspect(file, func(n ast.Node) bool {
		if sel, ok := n.(*ast.SelectorExpr); ok {
			if id, ok := sel.X.(*ast.Ident); ok && id.Name == pkg {
				names = append(names, sel.Sel.Name)
			}
		}
		return true
	})
	return names
}
