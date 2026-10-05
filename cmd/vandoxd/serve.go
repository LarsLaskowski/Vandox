package main

import (
	"context"
	"io"
)

// serve runs the backend service until ctx is done or SIGTERM/SIGINT arrives and returns the exit code.
func serve(ctx context.Context, configPath string, environ []string, stderr io.Writer, listen listenFunc) int {
	return 1
}
