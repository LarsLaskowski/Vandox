package logship_test

import (
	"io"
	"os"
	"path/filepath"
	"strings"
	"syscall"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/logship"
)

// linkTree is a directory tree of regular files and links below a directory that holds no link in its own path.
type linkTree struct {
	root string
}

func newLinkTree(t *testing.T) linkTree {
	t.Helper()
	root, err := filepath.EvalSymlinks(t.TempDir())
	if err != nil {
		t.Fatalf("EvalSymlinks() error = %v, want nil", err)
	}
	return linkTree{root: root}
}

func (l linkTree) path(parts ...string) string {
	return filepath.Join(append([]string{l.root}, parts...)...)
}

func (l linkTree) write(t *testing.T, content string, parts ...string) string {
	t.Helper()
	path := l.path(parts...)
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatalf("MkdirAll() error = %v, want nil", err)
	}
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatalf("WriteFile() error = %v, want nil", err)
	}
	return path
}

func TestOpenNoLinks_OpensARegularFileForReading(t *testing.T) {
	tree := newLinkTree(t)
	path := tree.write(t, "line 1\nline 2\n", "a", "b", "mail.log")

	f, err := logship.OpenNoLinks(path)

	if err != nil || f == nil {
		t.Fatalf("OpenNoLinks(%s) = %v, %v, want an open file", path, f, err)
	}
	defer func() { _ = f.Close() }()
	got, err := io.ReadAll(f)
	if err != nil || string(got) != "line 1\nline 2\n" {
		t.Errorf("reading the opened file = %q, %v, want its content from the start", got, err)
	}
	if _, err := f.Write([]byte("x")); err == nil {
		t.Error("writing to the opened file succeeded, want a file opened for reading only")
	}
}

// linker makes a link or a link-like path below the tree and returns the path to open.
type linker func(t *testing.T, tree linkTree, secret string) string

func symlinkAt(t *testing.T, target, link string) {
	t.Helper()
	if err := os.Symlink(target, link); err != nil {
		t.Fatalf("Symlink() error = %v, want nil", err)
	}
}

func linkToTheFile(t *testing.T, tree linkTree, secret string) string {
	t.Helper()
	symlinkAt(t, secret, tree.path("link-file.log"))
	return tree.path("link-file.log")
}

func linkWithARelativeTarget(t *testing.T, tree linkTree, _ string) string {
	t.Helper()
	symlinkAt(t, "secret/shadow", tree.path("relative.log"))
	return tree.path("relative.log")
}

func linkAsTheLastDirectory(t *testing.T, tree linkTree, _ string) string {
	t.Helper()
	symlinkAt(t, tree.path("secret"), tree.path("real", "dir-link"))
	return tree.path("real", "dir-link", "shadow")
}

func linkAsADirectoryInTheMiddle(t *testing.T, tree linkTree, _ string) string {
	t.Helper()
	symlinkAt(t, tree.path("real"), tree.path("mid-link"))
	return tree.path("mid-link", "dir", "mail.log")
}

func linkAtTheTopOfTheTree(t *testing.T, tree linkTree, _ string) string {
	t.Helper()
	symlinkAt(t, tree.root, tree.path("top"))
	return filepath.Join(tree.path("top"), "real", "dir", "mail.log")
}

func hardLinkToAnotherFile(t *testing.T, tree linkTree, secret string) string {
	t.Helper()
	if err := os.Link(secret, tree.path("hard.log")); err != nil {
		t.Fatalf("Link() error = %v, want nil", err)
	}
	return tree.path("hard.log")
}

func TestOpenNoLinks_RefusesALinkInAnyElement(t *testing.T) {
	tree := newLinkTree(t)
	secret := tree.write(t, "the content of another file\n", "secret", "shadow")
	tree.write(t, "ordinary content\n", "real", "dir", "mail.log")
	tests := []struct {
		name string
		make linker
	}{
		{"the file is a link", linkToTheFile},
		{"a link to a file with a relative target", linkWithARelativeTarget},
		{"the last directory is a link", linkAsTheLastDirectory},
		{"a directory in the middle is a link", linkAsADirectoryInTheMiddle},
		{"a link at the top of the tree", linkAtTheTopOfTheTree},
		{"a hard link to another file", hardLinkToAnotherFile},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			path := tt.make(t, tree, secret)

			f, err := logship.OpenNoLinks(path)

			if f != nil {
				_ = f.Close()
			}
			if err == nil || f != nil {
				t.Errorf("OpenNoLinks(%s) = %v, %v, want a nil file and an error", path, f, err)
			}
		})
	}
	t.Run("the plain path still opens", func(t *testing.T) {
		f, err := logship.OpenNoLinks(tree.path("real", "dir", "mail.log"))

		if err != nil || f == nil {
			t.Fatalf("OpenNoLinks() of the file the links point past = %v, %v, want an open file", f, err)
		}
		_ = f.Close()
	})
}

func TestOpenNoLinks_RefusesNonRegularFiles(t *testing.T) {
	tree := newLinkTree(t)
	fifo := tree.path("fifo")
	if err := syscall.Mkfifo(fifo, 0o600); err != nil {
		t.Fatalf("Mkfifo() error = %v, want nil", err)
	}
	if err := os.Mkdir(tree.path("dir"), 0o700); err != nil {
		t.Fatalf("Mkdir() error = %v, want nil", err)
	}
	tests := []struct {
		name string
		path string
	}{
		{"a directory", tree.path("dir")},
		{"a FIFO", fifo},
		{"a device", "/dev/null"},
		{"a file that does not exist", tree.path("dir", "absent.log")},
		{"a directory that does not exist", tree.path("absent", "mail.log")},
		{"the root", "/"},
		{"a relative path", "relative/mail.log"},
		{"an empty path", ""},
	}
	t.Run("a regular file opens", func(t *testing.T) {
		f, err := logship.OpenNoLinks(tree.write(t, "x\n", "dir", "regular.log"))

		if err != nil || f == nil {
			t.Fatalf("OpenNoLinks() of a regular file = %v, %v, want an open file", f, err)
		}
		_ = f.Close()
	})
	// the call returns at once for a FIFO without a writer: the file is opened without blocking
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			f, err := logship.OpenNoLinks(tt.path)

			if err == nil || f != nil {
				if f != nil {
					_ = f.Close()
				}
				t.Errorf("OpenNoLinks(%q) = %v, %v, want a nil file and an error", tt.path, f, err)
			}
		})
	}
}

func TestOpenNoLinks_ReportsOneFixedReasonWithoutThePath(t *testing.T) {
	tree := newLinkTree(t)
	secret := tree.write(t, "content\n", "secret", "shadow")
	tree.write(t, "plain\n", "dir", "plain.log")
	tree.write(t, "not a directory\n", "plain-file")
	if err := syscall.Mkfifo(tree.path("fifo"), 0o600); err != nil {
		t.Fatalf("Mkfifo() error = %v, want nil", err)
	}
	symlinkAt(t, secret, tree.path("link.log"))
	symlinkAt(t, tree.path("secret"), tree.path("dir-link"))
	if err := os.Link(secret, tree.path("hard.log")); err != nil {
		t.Fatalf("Link() error = %v, want nil", err)
	}
	tooLong := strings.Repeat("n", 300)
	tests := []struct {
		name string
		path string
		want string
	}{
		{"a file that does not exist", tree.path("dir", "absent.log"), "missing"},
		{"a directory that does not exist", tree.path("absent", "mail.log"), "missing"},
		{"a regular file as a directory element", tree.path("plain-file", "mail.log"), "missing"},
		{"a regular file as the last directory", tree.path("dir", "plain.log", "mail.log"), "missing"},
		{"a relative path", "relative/mail.log", "missing"},
		{"a parent element", tree.path("dir") + "/../dir/plain.log", "missing"},
		{"a current directory element", tree.path("dir") + "/./plain.log", "missing"},
		{"an empty element", tree.path("dir") + "//plain.log", "missing"},
		{"a trailing slash", tree.path("dir", "plain.log") + "/", "missing"},
		{"a link as the file", tree.path("link.log"), "link refused"},
		{"a link as a directory element", tree.path("dir-link", "shadow"), "link refused"},
		{"a hard link", tree.path("hard.log"), "link refused"},
		{"a directory", tree.path("dir"), "not a regular file"},
		{"a FIFO", tree.path("fifo"), "not a regular file"},
		{"a file name that is too long", tree.path("dir", tooLong), "read failed"},
		{"a directory name that is too long", tree.path(tooLong, "mail.log"), "read failed"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			f, err := logship.OpenNoLinks(tt.path)

			if f != nil {
				_ = f.Close()
			}
			if err == nil || err.Error() != tt.want {
				t.Errorf("OpenNoLinks(path) error = %v, want exactly %q", err, tt.want)
			}
		})
	}
}

func TestOpenNoLinks_ReportsPermissionDeniedForWhatItCannotSearchOrRead(t *testing.T) {
	if os.Geteuid() == 0 {
		t.Skip("the superuser passes every permission check")
	}
	tree := newLinkTree(t)
	file := tree.write(t, "x\n", "locked", "inner", "mail.log")
	unreadable := tree.write(t, "x\n", "open", "unreadable.log")
	if err := os.Chmod(unreadable, 0); err != nil {
		t.Fatalf("Chmod() error = %v, want nil", err)
	}
	if err := os.Chmod(tree.path("locked"), 0); err != nil {
		t.Fatalf("Chmod() error = %v, want nil", err)
	}
	t.Cleanup(func() { _ = os.Chmod(tree.path("locked"), 0o700) })
	tests := []struct {
		name string
		path string
	}{
		{"a directory without search permission", file},
		{"a file without read permission", unreadable},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			f, err := logship.OpenNoLinks(tt.path)

			if f != nil {
				_ = f.Close()
			}
			if err == nil || err.Error() != "permission denied" {
				t.Errorf("OpenNoLinks(path) error = %v, want exactly %q", err, "permission denied")
			}
		})
	}
}
