package cli_test

import (
	"bytes"
	"errors"
	"strings"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/cli"
	"github.com/LarsLaskowski/Vandox/internal/version"
)

const (
	agentName   = "vandox-agent"
	backendName = "vandoxd"
	versionFlag = "-version"
)

// failingWriter is an io.Writer whose Write always fails.
type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) {
	return 0, errors.New("write failed")
}

func usageHeader(name string) string {
	return "Usage of " + name + ":"
}

func TestRun(t *testing.T) {
	tests := []struct {
		name       string
		binary     string
		args       []string
		wantCode   int
		wantStdout string
		// wantStderr lists substrings stderr must contain; an empty list means stderr must be empty.
		wantStderr []string
	}{
		{
			name:       "single dash version",
			binary:     agentName,
			args:       []string{"-version"},
			wantCode:   0,
			wantStdout: version.String(agentName) + "\n",
		},
		{
			name:       "double dash version",
			binary:     backendName,
			args:       []string{"--version"},
			wantCode:   0,
			wantStdout: version.String(backendName) + "\n",
		},
		{
			name:       "no arguments print usage",
			binary:     agentName,
			args:       nil,
			wantCode:   0,
			wantStderr: []string{usageHeader(agentName), versionFlag},
		},
		{
			name:       "empty arguments print usage",
			binary:     backendName,
			args:       []string{},
			wantCode:   0,
			wantStderr: []string{usageHeader(backendName), versionFlag},
		},
		{
			name:       "version false prints usage",
			binary:     agentName,
			args:       []string{"-version=false"},
			wantCode:   0,
			wantStderr: []string{usageHeader(agentName), versionFlag},
		},
		{
			name:       "positional argument prints usage",
			binary:     agentName,
			args:       []string{"extra"},
			wantCode:   0,
			wantStderr: []string{usageHeader(agentName), versionFlag},
		},
		{
			name:       "-h prints usage",
			binary:     agentName,
			args:       []string{"-h"},
			wantCode:   0,
			wantStderr: []string{usageHeader(agentName), versionFlag},
		},
		{
			name:       "-help prints usage",
			binary:     backendName,
			args:       []string{"-help"},
			wantCode:   0,
			wantStderr: []string{usageHeader(backendName), versionFlag},
		},
		{
			name:       "--help prints usage",
			binary:     agentName,
			args:       []string{"--help"},
			wantCode:   0,
			wantStderr: []string{usageHeader(agentName), versionFlag},
		},
		{
			name:     "undefined flag fails with exit code 2",
			binary:   agentName,
			args:     []string{"-bogus"},
			wantCode: 2,
			wantStderr: []string{
				"flag provided but not defined: -bogus",
				usageHeader(agentName),
				versionFlag,
			},
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			var stdout, stderr bytes.Buffer

			gotCode := cli.Run(tc.binary, tc.args, &stdout, &stderr)

			if gotCode != tc.wantCode {
				t.Errorf("Run(%q, %q) exit code = %d, want %d", tc.binary, tc.args, gotCode, tc.wantCode)
			}
			if stdout.String() != tc.wantStdout {
				t.Errorf("Run(%q, %q) stdout = %q, want %q", tc.binary, tc.args, stdout.String(), tc.wantStdout)
			}
			assertStderr(t, tc.binary, tc.args, stderr.String(), tc.wantStderr)
		})
	}
}

// assertStderr checks that stderr contains every wanted substring, or is empty when none are wanted.
func assertStderr(t *testing.T, binary string, args []string, stderr string, want []string) {
	t.Helper()

	if len(want) == 0 && stderr != "" {
		t.Errorf("Run(%q, %q) stderr = %q, want empty", binary, args, stderr)
	}
	for _, w := range want {
		if !strings.Contains(stderr, w) {
			t.Errorf("Run(%q, %q) stderr = %q, want it to contain %q", binary, args, stderr, w)
		}
	}
}

func TestRun_StdoutWriteFails(t *testing.T) {
	var stderr bytes.Buffer

	got := cli.Run(agentName, []string{versionFlag}, failingWriter{}, &stderr)

	if got != 1 {
		t.Errorf("Run(%q, [%s]) with failing stdout = %d, want 1", agentName, versionFlag, got)
	}
}

func TestRun_NoSharedFlagState(t *testing.T) {
	const calls = 3

	for i := range calls {
		var stdout, stderr bytes.Buffer

		gotVersion := cli.Run(agentName, []string{versionFlag}, &stdout, &stderr)
		if gotVersion != 0 || stdout.String() != version.String(agentName)+"\n" {
			t.Fatalf("call %d: Run(%q, [%s]) = %d, stdout %q, want 0, %q",
				i, agentName, versionFlag, gotVersion, stdout.String(), version.String(agentName)+"\n")
		}

		stdout.Reset()
		stderr.Reset()
		gotUsage := cli.Run(agentName, nil, &stdout, &stderr)
		if gotUsage != 0 || stdout.Len() != 0 || !strings.Contains(stderr.String(), usageHeader(agentName)) {
			t.Fatalf("call %d: Run(%q, nil) = %d, stdout %q, stderr %q, want 0, empty stdout, usage on stderr",
				i, agentName, gotUsage, stdout.String(), stderr.String())
		}
	}
}
