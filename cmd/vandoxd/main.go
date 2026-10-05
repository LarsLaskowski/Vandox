// Command vandoxd is the Vandox backend with web UI.
package main

import (
	"context"
	"io"
	"net"
	"os"
)

// listenFunc opens a listener for network and address; main passes (&net.ListenConfig{}).Listen.
type listenFunc func(ctx context.Context, network, address string) (net.Listener, error)

func main() {
	os.Exit(run(context.Background(), os.Args[1:], os.Environ(), os.Stdout, os.Stderr, (&net.ListenConfig{}).Listen))
}

// run executes vandoxd with args (without the program name) and returns the process exit code: 0 on success,
// 1 on a runtime or start-up failure, 2 on a usage error.
func run(ctx context.Context, args, environ []string, stdout, stderr io.Writer, listen listenFunc) int {
	return 1
}
