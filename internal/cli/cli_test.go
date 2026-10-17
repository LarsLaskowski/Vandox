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
	versionFlag = "-version"
	configFlag  = "-config"
	defaultPath = "/etc/vandox/agent.yaml"
)

// failingWriter is an io.Writer whose Write always fails.
type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) {
	return 0, errors.New("write failed")
}

func usageHeader(name string) string {
	return "Usage of " + name + ":"
}

func TestParse(t *testing.T) {
	usage := []string{usageHeader(agentName), configFlag, versionFlag}
	tests := []struct {
		name       string
		args       []string
		wantDone   bool
		wantCode   int
		wantConfig string // checked when the agent is to run (done is false)
		wantStdout string
		// wantStderr lists substrings stderr must contain; an empty list means stderr must be empty.
		wantStderr []string
	}{
		{name: "no arguments", args: nil, wantConfig: defaultPath},
		{name: "empty arguments", args: []string{}, wantConfig: defaultPath},
		{name: "single dash config and value", args: []string{"-config", "/x/a.yaml"}, wantConfig: "/x/a.yaml"},
		{name: "single dash config=value", args: []string{"-config=/x/a.yaml"}, wantConfig: "/x/a.yaml"},
		{name: "double dash config and value", args: []string{"--config", "/x/a.yaml"}, wantConfig: "/x/a.yaml"},
		{name: "double dash config=value", args: []string{"--config=/x/a.yaml"}, wantConfig: "/x/a.yaml"},
		{
			name: "single dash version", args: []string{"-version"},
			wantDone: true, wantStdout: version.String(agentName) + "\n",
		},
		{
			name: "double dash version", args: []string{"--version"},
			wantDone: true, wantStdout: version.String(agentName) + "\n",
		},
		{name: "version false runs the agent", args: []string{"-version=false"}, wantConfig: defaultPath},
		{name: "-h prints usage", args: []string{"-h"}, wantDone: true, wantStderr: usage},
		{name: "-help prints usage", args: []string{"-help"}, wantDone: true, wantStderr: usage},
		{name: "--help prints usage", args: []string{"--help"}, wantDone: true, wantStderr: usage},
		{
			name: "undefined flag", args: []string{"-bogus"}, wantDone: true, wantCode: 2,
			wantStderr: append([]string{"flag provided but not defined: -bogus"}, usage...),
		},
		{
			name: "positional argument", args: []string{"extra"}, wantDone: true, wantCode: 2,
			wantStderr: append([]string{agentName + ": unexpected argument"}, usage...),
		},
		{
			name: "positional argument after a flag", args: []string{"-config", "/x/a.yaml", "extra"}, wantDone: true, wantCode: 2,
			wantStderr: append([]string{agentName + ": unexpected argument"}, usage...),
		},
		{
			name: "empty config", args: []string{"-config", ""}, wantDone: true, wantCode: 2,
			wantStderr: append([]string{agentName + ": -config must not be empty"}, usage...),
		},
		{
			name: "empty config=", args: []string{"-config="}, wantDone: true, wantCode: 2,
			wantStderr: append([]string{agentName + ": -config must not be empty"}, usage...),
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			var stdout, stderr bytes.Buffer

			cmd, gotCode, gotDone := cli.Parse(agentName, tc.args, &stdout, &stderr)

			if gotDone != tc.wantDone {
				t.Errorf("Parse(%q) done = %v, want %v", tc.args, gotDone, tc.wantDone)
			}
			if gotCode != tc.wantCode {
				t.Errorf("Parse(%q) exit code = %d, want %d", tc.args, gotCode, tc.wantCode)
			}
			if !tc.wantDone && cmd.ConfigPath != tc.wantConfig {
				t.Errorf("Parse(%q) ConfigPath = %q, want %q", tc.args, cmd.ConfigPath, tc.wantConfig)
			}
			if stdout.String() != tc.wantStdout {
				t.Errorf("Parse(%q) stdout = %q, want %q", tc.args, stdout.String(), tc.wantStdout)
			}
			assertStderr(t, tc.args, stderr.String(), tc.wantStderr)
		})
	}
}

// assertStderr checks that stderr contains every wanted substring, or is empty when none are wanted.
func assertStderr(t *testing.T, args []string, stderr string, want []string) {
	t.Helper()

	if len(want) == 0 && stderr != "" {
		t.Errorf("Parse(%q) stderr = %q, want empty", args, stderr)
	}
	for _, w := range want {
		if !strings.Contains(stderr, w) {
			t.Errorf("Parse(%q) stderr = %q, want it to contain %q", args, stderr, w)
		}
	}
}

func TestParse_VersionWriteFails(t *testing.T) {
	var stderr bytes.Buffer

	_, gotCode, gotDone := cli.Parse(agentName, []string{versionFlag}, failingWriter{}, &stderr)

	if !gotDone || gotCode != 1 {
		t.Errorf("Parse(%q, [%s]) with failing stdout = done %v, code %d, want done true, code 1", agentName, versionFlag, gotDone, gotCode)
	}
}

func TestParse_NoSharedFlagState(t *testing.T) {
	const calls = 3

	for i := range calls {
		var stdout, stderr bytes.Buffer

		_, gotCode, gotDone := cli.Parse(agentName, []string{versionFlag}, &stdout, &stderr)
		if !gotDone || gotCode != 0 || stdout.String() != version.String(agentName)+"\n" {
			t.Fatalf("call %d: Parse(%q, [%s]) = done %v, code %d, stdout %q, want done, 0, %q",
				i, agentName, versionFlag, gotDone, gotCode, stdout.String(), version.String(agentName)+"\n")
		}

		stdout.Reset()
		stderr.Reset()
		cmd, gotCode, gotDone := cli.Parse(agentName, []string{configFlag, "/x/a.yaml"}, &stdout, &stderr)
		if gotDone || gotCode != 0 || cmd.ConfigPath != "/x/a.yaml" {
			t.Fatalf("call %d: Parse(%q, [%s /x/a.yaml]) = done %v, code %d, ConfigPath %q, want not done, 0, /x/a.yaml",
				i, agentName, configFlag, gotDone, gotCode, cmd.ConfigPath)
		}

		cmd, gotCode, gotDone = cli.Parse(agentName, nil, &stdout, &stderr)
		if gotDone || gotCode != 0 || cmd.ConfigPath != defaultPath {
			t.Fatalf("call %d: Parse(%q, nil) after -config = done %v, code %d, ConfigPath %q, want not done, 0, %q",
				i, agentName, gotDone, gotCode, cmd.ConfigPath, defaultPath)
		}
	}
}
