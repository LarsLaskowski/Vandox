package main

import (
	"bytes"
	"context"
	"net"
	"strings"
	"testing"

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
