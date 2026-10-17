package main

import (
	"bytes"
	"context"
	"path/filepath"
	"strings"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/version"
)

func TestRun_Version(t *testing.T) {
	var stdout, stderr bytes.Buffer

	got := run(context.Background(), []string{"-version"}, nil, &stdout, &stderr)

	want := version.String("vandox-agent") + "\n"
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

	got := run(context.Background(), []string{"-bogus"}, nil, &stdout, &stderr)

	want := "Usage of vandox-agent:"
	if got != 2 {
		t.Errorf("run([-bogus]) exit code = %d, want 2", got)
	}
	if !strings.Contains(stderr.String(), want) {
		t.Errorf("run([-bogus]) stderr = %q, want it to contain %q", stderr.String(), want)
	}
}

func TestRun_MissingConfigExitsOne(t *testing.T) {
	var stdout, stderr bytes.Buffer
	missing := filepath.Join(t.TempDir(), "none.yaml")

	got := run(context.Background(), []string{"-config", missing}, nil, &stdout, &stderr)

	if got != 1 {
		t.Errorf("run([-config <missing file>]) exit code = %d, want 1", got)
	}
	if stdout.Len() != 0 {
		t.Errorf("run([-config <missing file>]) stdout = %q, want empty", stdout.String())
	}
	for _, want := range []string{"agent cannot start", missing} {
		if !strings.Contains(stderr.String(), want) {
			t.Errorf("run([-config <missing file>]) stderr = %q, want it to contain %q", stderr.String(), want)
		}
	}
}
