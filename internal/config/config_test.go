package config

import (
	"errors"
	"io"
	"io/fs"
	"net"
	"os"
	"path/filepath"
	"strings"
	"syscall"
	"testing"
)

// sentinel marks a value that must never appear in an error text.
const sentinel = "S3NT1NEL"

// anyKey and anyLine tell requireKeyError not to compare that field.
const (
	anyKey  = "\x00any"
	anyLine = -1
)

// writeTemp writes content to a new file in a temporary directory and returns its path.
func writeTemp(t *testing.T, content string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "config.yaml")
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatalf("os.WriteFile(%q) = %v, want nil", path, err)
	}
	return path
}

// makeFifo creates a FIFO without a writer or reader in a temporary directory and returns its path.
func makeFifo(t *testing.T) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "fifo")
	if err := syscall.Mkfifo(path, 0o600); err != nil {
		t.Fatalf("syscall.Mkfifo(%q) = %v, want nil", path, err)
	}
	return path
}

// makeSocket binds a Unix socket in a temporary directory and returns its path. The socket is closed with the test.
func makeSocket(t *testing.T) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "sock")
	l, err := net.Listen("unix", path)
	if err != nil {
		t.Skipf("unix sockets not available: %v", err)
	}
	t.Cleanup(func() { _ = l.Close() })
	return path
}

// openResult holds what openRegular returned.
type openResult struct {
	file *os.File
	err  error
}

// openForTest calls openRegular(path) and closes a returned file with the test. A blocking open shows as a
// test that hangs until the go test timeout.
func openForTest(t *testing.T, path string) openResult {
	t.Helper()
	f, err := openRegular(path)
	r := openResult{file: f, err: err}
	if r.file != nil {
		t.Cleanup(func() { _ = r.file.Close() })
	}
	return r
}

// requireKeyError fails the test unless err is a *KeyError with the given key and line.
func requireKeyError(t *testing.T, err error, key string, line int) *KeyError {
	t.Helper()
	if err == nil {
		t.Fatalf("got nil error, want a *KeyError for key %q at line %d", key, line)
	}
	var ke *KeyError
	if !errors.As(err, &ke) {
		t.Fatalf("got %T (%v), want a *KeyError for key %q at line %d", err, err, key, line)
	}
	if key != anyKey && ke.Key != key {
		t.Errorf("KeyError.Key = %q, want %q (error: %v)", ke.Key, key, err)
	}
	if line != anyLine && ke.Line != line {
		t.Errorf("KeyError.Line = %d, want %d (error: %v)", ke.Line, line, err)
	}
	return ke
}

// requireNoLeak fails the test if the text of err contains any of the forbidden strings.
func requireNoLeak(t *testing.T, err error, forbidden ...string) {
	t.Helper()
	if err == nil {
		return
	}
	for _, f := range forbidden {
		if strings.Contains(err.Error(), f) {
			t.Errorf("error text %q contains %q, want it absent", err.Error(), f)
		}
	}
}

// requireNoLeakBesidesFile replaces every occurrence of file in the text of err with "<file>" and fails the
// test if the remaining text contains any of the forbidden strings. It returns early when err is nil.
func requireNoLeakBesidesFile(t *testing.T, err error, file string, forbidden ...string) {
	t.Helper()
	if err == nil {
		return
	}
	text := strings.ReplaceAll(err.Error(), file, "<file>")
	for _, f := range forbidden {
		if strings.Contains(text, f) {
			t.Errorf("error text %q contains %q, want it absent", text, f)
		}
	}
}

// requireSecretError fails the test unless err is a *SecretError. When v is not anyKey, the variable must match.
func requireSecretError(t *testing.T, err error, v string) *SecretError {
	t.Helper()
	if err == nil {
		t.Fatalf("got nil error, want a *SecretError for %q", v)
	}
	var se *SecretError
	if !errors.As(err, &se) {
		t.Fatalf("got %T (%v), want a *SecretError for %q", err, err, v)
	}
	if v != anyKey && se.Var != v {
		t.Errorf("SecretError.Var = %q, want %q (error: %v)", se.Var, v, err)
	}
	return se
}

func TestKeyError_Error(t *testing.T) {
	tests := []struct {
		name string
		err  KeyError
		want string
	}{
		{"file line key reason", KeyError{File: "f.yaml", Line: 3, Key: "backend.url", Reason: "bad"}, "config: f.yaml:3: backend.url: bad"},
		{"line zero omits line", KeyError{File: "f.yaml", Key: "backend.url", Reason: "bad"}, "config: f.yaml: backend.url: bad"},
		{"empty key omits key", KeyError{File: "f.yaml", Line: 3, Reason: "bad"}, "config: f.yaml:3: bad"},
		{"empty key and line", KeyError{File: "f.yaml", Reason: "bad"}, "config: f.yaml: bad"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := tt.err.Error(); got != tt.want {
				t.Errorf("KeyError%+v.Error() = %q, want %q", tt.err, got, tt.want)
			}
		})
	}
}

func TestOpenRegular(t *testing.T) {
	t.Run("regular file is opened", func(t *testing.T) {
		path := writeTemp(t, "content")
		r := openForTest(t, path)
		if r.err != nil {
			t.Fatalf("openRegular(%q) error = %v, want nil", path, r.err)
		}
		if r.file == nil {
			t.Fatalf("openRegular(%q) file = nil, want an open file", path)
		}
		data, err := io.ReadAll(r.file)
		if err != nil || string(data) != "content" {
			t.Errorf("reading the opened file = %q, %v, want %q, nil", data, err, "content")
		}
	})

	t.Run("FIFO without a writer is refused without blocking", func(t *testing.T) {
		requireNotRegular(t, openForTest(t, makeFifo(t)), "FIFO")
	})
	t.Run("directory is refused", func(t *testing.T) {
		requireNotRegular(t, openForTest(t, t.TempDir()), "directory")
	})
	t.Run("device file is refused", func(t *testing.T) {
		requireNotRegular(t, openForTest(t, os.DevNull), "device file")
	})

	t.Run("Unix socket is refused without blocking", func(t *testing.T) {
		requireOpenFails(t, openForTest(t, makeSocket(t)), syscall.ENXIO, nil)
	})

	t.Run("missing file", func(t *testing.T) {
		path := filepath.Join(t.TempDir(), "missing.yaml")
		requireOpenFails(t, openForTest(t, path), fs.ErrNotExist, errNotRegular)
	})
}

// requireOpenFails checks that r is a nil file whose error matches want and, when not is not nil, does not match not.
func requireOpenFails(t *testing.T, r openResult, want, not error) {
	t.Helper()
	if r.file != nil {
		t.Errorf("openRegular file = %v, want nil", r.file)
	}
	if !errors.Is(r.err, want) {
		t.Errorf("openRegular error = %v, want errors.Is %v", r.err, want)
	}
	if not != nil && errors.Is(r.err, not) {
		t.Errorf("openRegular error = %v, want it not to match %v", r.err, not)
	}
}

// requireNotRegular checks that r is a nil file with errNotRegular.
func requireNotRegular(t *testing.T, r openResult, kind string) {
	t.Helper()
	if r.file != nil {
		t.Errorf("openRegular(%s) file = %v, want nil", kind, r.file)
	}
	if !errors.Is(r.err, errNotRegular) {
		t.Errorf("openRegular(%s) error = %v, want errors.Is errNotRegular", kind, r.err)
	}
}

func TestReadFile(t *testing.T) {
	t.Run("regular file within the limit is returned", func(t *testing.T) {
		path := writeTemp(t, "12345678")
		got, err := readFile(path, 8)
		if err != nil {
			t.Fatalf("readFile(%q, 8) error = %v, want nil", path, err)
		}
		if string(got) != "12345678" {
			t.Errorf("readFile(%q, 8) = %q, want %q", path, got, "12345678")
		}
	})
	t.Run("empty file is returned empty", func(t *testing.T) {
		path := writeTemp(t, "")
		got, err := readFile(path, 8)
		if err != nil || len(got) != 0 {
			t.Errorf("readFile(empty) = %q, %v, want empty and nil", got, err)
		}
	})

	failures := []struct {
		name   string
		path   func(t *testing.T) string
		wantIs error
	}{
		{"missing file", func(t *testing.T) string { return filepath.Join(t.TempDir(), "missing.yaml") }, fs.ErrNotExist},
		{"directory", func(t *testing.T) string { return t.TempDir() }, nil},
		{"device file", func(*testing.T) string { return os.DevNull }, nil},
		{"FIFO", makeFifo, nil},
		{"Unix socket", makeSocket, nil},
		{"file larger than the limit", func(t *testing.T) string { return writeTemp(t, "123456789") }, nil},
	}
	for _, tt := range failures {
		t.Run(tt.name, func(t *testing.T) {
			requireReadFileFails(t, tt.path(t), tt.wantIs)
		})
	}
}

// requireReadFileFails checks that readFile rejects path with a plain file error naming it and,
// when wantIs is not nil, matching errors.Is.
func requireReadFileFails(t *testing.T, path string, wantIs error) {
	t.Helper()
	got, err := readFile(path, 8)
	if err == nil {
		t.Fatalf("readFile(%q, 8) = %q, nil, want an error", path, got)
	}
	if !strings.Contains(err.Error(), path) {
		t.Errorf("readFile(%q, 8) error = %q, want it to contain the path", path, err)
	}
	if wantIs != nil && !errors.Is(err, wantIs) {
		t.Errorf("readFile(%q, 8) error = %v, want errors.Is %v", path, err, wantIs)
	}
	var ke *KeyError
	if errors.As(err, &ke) {
		t.Errorf("readFile(%q, 8) error is a *KeyError, want a plain file error", path)
	}
}

func TestReadFileFIFOText(t *testing.T) {
	path := makeFifo(t)
	_, err := readFile(path, 8)
	want := "config: " + path + ": not a regular file"
	if err == nil || err.Error() != want {
		t.Errorf("readFile(FIFO) error = %v, want %q", err, want)
	}
}

func TestCheckDirectory(t *testing.T) {
	lines := map[string]int{"spool.directory": 7}
	tests := []struct {
		value   string
		wantErr bool
	}{
		{"/var/lib/vandox/spool", false},
		{"/", false},
		{"", true},
		{"spool", true},
		{"/var/lib/../x", true},
		{"/var/lib/vandox/", true},
		{"/a//b", true},
		{"/a/./b", true},
		{sentinel + "/relative", true},
	}
	for _, tt := range tests {
		t.Run(tt.value, func(t *testing.T) {
			err := checkDirectory("f.yaml", lines, "spool.directory", tt.value)
			if !tt.wantErr {
				if err != nil {
					t.Errorf("checkDirectory(%q) = %v, want nil", tt.value, err)
				}
				return
			}
			ke := requireKeyError(t, err, "spool.directory", 7)
			if ke.File != "f.yaml" {
				t.Errorf("KeyError.File = %q, want %q", ke.File, "f.yaml")
			}
			requireNoLeak(t, err, sentinel)
		})
	}
}

func TestCheckLogLevel(t *testing.T) {
	lines := map[string]int{"log.level": 9}
	for _, v := range []string{"debug", "info", "warn", "error"} {
		t.Run("valid "+v, func(t *testing.T) {
			if err := checkLogLevel("f.yaml", lines, "log.level", v); err != nil {
				t.Errorf("checkLogLevel(%q) = %v, want nil", v, err)
			}
		})
	}
	for _, v := range []string{"INFO", "Info", "trace", "", "warning", " info", sentinel} {
		t.Run("invalid "+v, func(t *testing.T) {
			err := checkLogLevel("f.yaml", lines, "log.level", v)
			_ = requireKeyError(t, err, "log.level", 9)
			requireNoLeak(t, err, sentinel)
		})
	}
}
