// Package cli holds the command-line handling shared by vandox-agent and vandoxd.
package cli

import "io"

// Run parses args (the command-line arguments without the program name) for the binary called name,
// writes its regular output to stdout and usage and parse errors to stderr, and returns the process
// exit code: 0 on success, on -h/-help and when only the usage is printed; 1 when writing the output
// fails; 2 when the arguments cannot be parsed.
func Run(name string, args []string, stdout, stderr io.Writer) int {
	panic("not implemented")
}
