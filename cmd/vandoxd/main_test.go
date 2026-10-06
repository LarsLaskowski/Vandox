package main

import (
	"bytes"
	"context"
	"errors"
	"net"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store"
	"github.com/LarsLaskowski/Vandox/internal/version"
)

func TestRun_Version(t *testing.T) {
	var stdout, stderr bytes.Buffer

	got := run(context.Background(), []string{"-version"}, nil, &stdout, &stderr, func(context.Context, string, string) (net.Listener, error) {
		t.Fatal("listen must not be called")
		return nil, nil
	})

	want := version.String("vandoxd") + "\n"
	if got != 0 {
		t.Errorf("run([-version]) exit code = %d, want 0", got)
	}
	if stdout.String() != want {
		t.Errorf("run([-version]) stdout = %q, want %q", stdout.String(), want)
	}
	if stderr.Len() != 0 {
		t.Errorf("run([-version]) stderr = %q, want empty", stderr.String())
	}
}

func TestRun_UndefinedFlag(t *testing.T) {
	var stdout, stderr bytes.Buffer

	got := run(context.Background(), []string{"-bogus"}, nil, &stdout, &stderr, func(context.Context, string, string) (net.Listener, error) {
		t.Fatal("listen must not be called")
		return nil, nil
	})

	want := "Usage of vandoxd:"
	if got != 2 {
		t.Errorf("run([-bogus]) exit code = %d, want 2", got)
	}
	if !strings.Contains(stderr.String(), want) {
		t.Errorf("run([-bogus]) stderr = %q, want it to contain %q", stderr.String(), want)
	}
}

// noListen is a listenFunc that fails the test when it is called.
func noListen(t *testing.T) listenFunc {
	t.Helper()
	return func(context.Context, string, string) (net.Listener, error) {
		t.Error("listen was called, want no listener")
		return nil, net.ErrClosed
	}
}

func TestRun_VersionWins(t *testing.T) {
	missing := filepath.Join(t.TempDir(), "absent.yaml")
	tests := []struct {
		name string
		args []string
	}{
		{"single dash", []string{"-version"}},
		{"double dash", []string{"--version"}},
		{"with healthcheck", []string{"-version", "-healthcheck", "-config", missing}},
		{"after config", []string{"-config", missing, "-version"}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			var stdout, stderr bytes.Buffer

			got := run(t.Context(), tc.args, nil, &stdout, &stderr, noListen(t))

			want := version.String("vandoxd") + "\n"
			if got != 0 {
				t.Errorf("run(%v) exit code = %d, want 0", tc.args, got)
			}
			if stdout.String() != want {
				t.Errorf("run(%v) stdout = %q, want %q", tc.args, stdout.String(), want)
			}
			if stderr.Len() != 0 {
				t.Errorf("run(%v) stderr = %q, want empty", tc.args, stderr.String())
			}
		})
	}
}

func TestRun_Help(t *testing.T) {
	for _, flagName := range []string{"-h", "-help", "--help"} {
		t.Run(flagName, func(t *testing.T) {
			var stdout, stderr bytes.Buffer

			got := run(t.Context(), []string{flagName}, nil, &stdout, &stderr, noListen(t))

			if got != 0 {
				t.Errorf("run(%s) exit code = %d, want 0", flagName, got)
			}
			if !strings.HasPrefix(stderr.String(), "Usage of vandoxd:") {
				t.Errorf("run(%s) stderr = %q, want it to start with %q", flagName, stderr.String(), "Usage of vandoxd:")
			}
			for _, name := range []string{"-config", "-healthcheck", "-version"} {
				if !strings.Contains(stderr.String(), name) {
					t.Errorf("run(%s) stderr = %q, want it to list %s", flagName, stderr.String(), name)
				}
			}
		})
	}
}

func TestRun_UsageErrors(t *testing.T) {
	tests := []struct {
		name string
		args []string
		want string
	}{
		{"undefined flag", []string{"-bogus"}, "flag provided but not defined: -bogus"},
		{"positional argument", []string{"extra"}, "vandoxd: unexpected argument"},
		{"positional after flag", []string{"-config", "/nonexistent/vandoxd.yaml", "extra"}, "vandoxd: unexpected argument"},
		{"positional with healthcheck", []string{"-healthcheck", "extra"}, "vandoxd: unexpected argument"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			var stdout, stderr bytes.Buffer

			got := run(t.Context(), tc.args, nil, &stdout, &stderr, noListen(t))

			if got != 2 {
				t.Errorf("run(%v) exit code = %d, want 2", tc.args, got)
			}
			if !strings.Contains(stderr.String(), tc.want) {
				t.Errorf("run(%v) stderr = %q, want it to contain %q", tc.args, stderr.String(), tc.want)
			}
			if !strings.Contains(stderr.String(), "Usage of vandoxd:") {
				t.Errorf("run(%v) stderr = %q, want it to contain the usage", tc.args, stderr.String())
			}
		})
	}
}

// failingWriter fails every write.
type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) { return 0, errors.New("write failed") }

func TestRun_VersionWriteFailure(t *testing.T) {
	var stderr bytes.Buffer

	got := run(t.Context(), []string{"-version"}, nil, failingWriter{}, &stderr, noListen(t))

	if got != 1 {
		t.Errorf("run(-version) with a failing stdout exit code = %d, want 1", got)
	}
}

// ---- AC-C4: usage ----

func TestRun_ImportUsageErrors(t *testing.T) {
	cfg := newServiceConfig(t)
	cfgPath := cfg.write(t)
	tests := []struct {
		name string
		args []string
	}{
		{"no path", []string{"-config", cfgPath, "import"}},
		{"two paths", []string{"-config", cfgPath, "import", "a", "b"}},
		{"undefined flag", []string{"-config", cfgPath, "import", "-bogus", "a"}},
		{"healthcheck with import", []string{"-healthcheck", "import", "x"}},
		{"healthcheck and config with import", []string{"-healthcheck", "-config", cfgPath, "import", "x"}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			var stdout, stderr syncBuffer

			code := run(t.Context(), tc.args, nil, &stdout, &stderr, noListen(t))

			if code != 2 {
				t.Errorf("run(%v) exit code = %d, want 2", tc.args, code)
			}
			if !strings.Contains(stderr.String(), "Usage") {
				t.Errorf("run(%v) stderr = %q, want the usage", tc.args, stderr.String())
			}
			if stdout.String() != "" {
				t.Errorf("run(%v) stdout = %q, want nothing", tc.args, stdout.String())
			}
			if _, err := os.Stat(filepath.Join(cfg.dir, store.FileName)); err == nil {
				t.Errorf("run(%v) opened the database, want a usage error before anything is opened", tc.args)
			}
		})
	}
}

func TestRun_ImportUndefinedFlagIsNamed(t *testing.T) {
	var stderr syncBuffer

	code := run(t.Context(), []string{"import", "-bogus", "x"}, nil, &syncBuffer{}, &stderr, noListen(t))

	if code != 2 || !strings.Contains(stderr.String(), "flag provided but not defined: -bogus") {
		t.Errorf("run(import -bogus) = %d, stderr %q, want 2 and the undefined flag named", code, stderr.String())
	}
}

func TestRun_ImportHelp(t *testing.T) {
	for _, flagName := range []string{"-h", "-help"} {
		t.Run(flagName, func(t *testing.T) {
			var stdout, stderr syncBuffer

			code := run(t.Context(), []string{"import", flagName}, nil, &stdout, &stderr, noListen(t))

			if code != 0 {
				t.Errorf("run(import %s) exit code = %d, want 0", flagName, code)
			}
			usage := stdout.String() + stderr.String()
			for _, want := range []string{"-config", "import"} {
				if !strings.Contains(usage, want) {
					t.Errorf("run(import %s) output = %q, want the import usage to contain %q", flagName, usage, want)
				}
			}
		})
	}
}

func TestRun_UsageListsTheImportSubCommand(t *testing.T) {
	for _, args := range [][]string{{"-h"}, {"-bogus"}, {"extra"}} {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			var stderr syncBuffer

			run(t.Context(), args, nil, &syncBuffer{}, &stderr, noListen(t))

			if !strings.Contains(stderr.String(), "import") {
				t.Errorf("run(%v) stderr = %q, want the usage to list the import sub-command", args, stderr.String())
			}
		})
	}
}

func TestRun_VersionWinsOverImport(t *testing.T) {
	var stdout, stderr syncBuffer

	code := run(t.Context(), []string{"-version", "import", "x"}, nil, &stdout, &stderr, noListen(t))

	if code != 0 || !strings.Contains(stdout.String(), "vandoxd") {
		t.Errorf("run(-version import x) = %d, stdout %q, want 0 and the version", code, stdout.String())
	}
}
