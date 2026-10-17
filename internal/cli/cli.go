// Package cli holds the command-line handling of vandox-agent. vandoxd parses its own flags (record 0072).
package cli

import (
	"errors"
	"flag"
	"fmt"
	"io"

	"github.com/LarsLaskowski/Vandox/internal/config"
	"github.com/LarsLaskowski/Vandox/internal/version"
)

// Command is what the command line asks the agent to run.
type Command struct {
	ConfigPath string // the configuration file; default config.DefaultAgentFile
}

// Parse parses args (without the program name) for the binary called name. done reports that the process ends with
// code without running the agent: after -version (0, or 1 when writing stdout fails), -h/-help (0), and a usage error
// (2: an undefined flag, a positional argument, an empty -config). Usage and errors go to stderr.
func Parse(name string, args []string, stdout, stderr io.Writer) (cmd Command, code int, done bool) {
	flags := flag.NewFlagSet(name, flag.ContinueOnError)
	flags.SetOutput(stderr)
	configPath := flags.String("config", config.DefaultAgentFile, "path of the agent configuration file")
	showVersion := flags.Bool("version", false, "print version, commit and build date and exit")

	if err := flags.Parse(args); err != nil {
		if errors.Is(err, flag.ErrHelp) {
			return Command{}, 0, true
		}
		return Command{}, 2, true
	}
	if *showVersion {
		if _, err := fmt.Fprintln(stdout, version.String(name)); err != nil {
			return Command{}, 1, true
		}
		return Command{}, 0, true
	}
	if flags.NArg() > 0 {
		return usageError(flags, stderr, name+": unexpected argument")
	}
	if *configPath == "" {
		return usageError(flags, stderr, name+": -config must not be empty")
	}
	return Command{ConfigPath: *configPath}, 0, false
}

// usageError writes message and the usage to stderr and returns the result of Parse for a usage error.
func usageError(flags *flag.FlagSet, stderr io.Writer, message string) (Command, int, bool) {
	_, _ = fmt.Fprintln(stderr, message)
	flags.Usage()
	return Command{}, 2, true
}
