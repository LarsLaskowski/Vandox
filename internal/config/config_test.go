package config

import (
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
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

func TestCheckListen(t *testing.T) {
	lines := map[string]int{"web.listen": 2}
	valid := []struct {
		value string
		port  uint16
	}{
		{":8080", 8080},
		{"0.0.0.0:8080", 8080},
		{"[::]:8081", 8081},
		{"192.168.1.10:8080", 8080},
		{"[::1]:80", 80},
		{"[fe80::1%eth0]:80", 80},
		{":1", 1},
		{":65535", 65535},
	}
	for _, tt := range valid {
		t.Run("valid "+tt.value, func(t *testing.T) {
			got, err := checkListen("f.yaml", lines, "web.listen", tt.value)
			if err != nil || got != tt.port {
				t.Errorf("checkListen(%q) = %d, %v, want %d, nil", tt.value, got, err, tt.port)
			}
		})
	}
	invalid := []string{
		"", "8080", ":0", ":65536", ":http", ":+80", "[::1]:+80", ":-1", ": 80", ":808080", ":", "localhost:8080",
		"[::1]:x", sentinel + ":80", ":" + sentinel, "1.2.3.4.5:80", "[::1]8080",
	}
	for _, v := range invalid {
		t.Run("invalid "+v, func(t *testing.T) {
			got, err := checkListen("f.yaml", lines, "web.listen", v)
			if got != 0 {
				t.Errorf("checkListen(%q) port = %d, want 0 on error", v, got)
			}
			_ = requireKeyError(t, err, "web.listen", 2)
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
