// Package cli holds the command-line handling of vandox-agent. vandoxd parses its own flags (record 0072).
package cli

import (
	"io"
)

// Command is what the command line asks the agent to run.
type Command struct {
	ConfigPath string // the configuration file; default config.DefaultAgentFile
}

// Parse parses args (without the program name) for the binary called name. done reports that the process ends with
// code without running the agent: after -version (0, or 1 when writing stdout fails), -h/-help (0), and a usage error
// (2: an undefined flag, a positional argument, an empty -config). Usage and errors go to stderr.
func Parse(name string, args []string, stdout, stderr io.Writer) (cmd Command, code int, done bool) {
	return Command{}, 0, false
}
