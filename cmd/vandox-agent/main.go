// Command vandox-agent runs on the monitored Linux server and ships data to the backend.
package main

import (
	"io"
	"os"

	"github.com/LarsLaskowski/Vandox/internal/cli"
)

// binaryName is the name the binary reports in its version line and usage.
const binaryName = "vandox-agent"

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

// run executes the command with args and returns the process exit code.
func run(args []string, stdout, stderr io.Writer) int {
	return cli.Run(binaryName, args, stdout, stderr)
}
