// Command vandox-agent runs on the monitored Linux server and ships data to the backend.
package main

import (
	"context"
	"io"
	"os"
	"os/signal"
	"syscall"

	"github.com/LarsLaskowski/Vandox/internal/agent"
	"github.com/LarsLaskowski/Vandox/internal/cli"
)

// binaryName is the name the binary reports in its version line and usage.
const binaryName = "vandox-agent"

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	code := run(ctx, os.Args[1:], os.Environ(), os.Stdout, os.Stderr)
	stop()
	os.Exit(code)
}

// run executes the command with args and returns the process exit code.
func run(ctx context.Context, args, environ []string, stdout, stderr io.Writer) int {
	cmd, code, done := cli.Parse(binaryName, args, stdout, stderr)
	if done {
		return code
	}
	return agent.Run(ctx, agent.Options{ConfigPath: cmd.ConfigPath, Environ: environ, Stderr: stderr})
}
