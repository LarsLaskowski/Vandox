package logship_test

import (
	"io"
	"os"
	"path/filepath"
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
