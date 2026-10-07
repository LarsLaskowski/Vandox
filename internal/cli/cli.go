// Package cli holds the command-line handling of vandox-agent. vandoxd parses its own flags (record 0072).
package cli

import (
	"errors"
	"flag"
	"fmt"
	"io"

	"github.com/LarsLaskowski/Vandox/internal/version"
)

// Run parses args (the command-line arguments without the program name) for the binary called name,
// writes its regular output to stdout and usage and parse errors to stderr, and returns the process
// exit code: 0 on success, on -h/-help and when only the usage is printed; 1 when writing the output
// fails; 2 when the arguments cannot be parsed.
func Run(name string, args []string, stdout, stderr io.Writer) int {
	flags := flag.NewFlagSet(name, flag.ContinueOnError)
	flags.SetOutput(stderr)
	showVersion := flags.Bool("version", false, "print version, commit and build date and exit")

	if err := flags.Parse(args); err != nil {
		if errors.Is(err, flag.ErrHelp) {
			return 0
		}
		return 2
	}

	if !*showVersion {
		flags.Usage()
		return 0
	}
	if _, err := fmt.Fprintln(stdout, version.String(name)); err != nil {
		return 1
	}
	return 0
}
