// Command vandoxd is the Vandox backend with web UI.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/config"
	"github.com/LarsLaskowski/Vandox/internal/version"
)

// binaryName is the name of this binary in usage and version output.
const binaryName = "vandoxd"

// listenFunc opens a listener for network and address; main passes (&net.ListenConfig{}).Listen.
type listenFunc func(ctx context.Context, network, address string) (net.Listener, error)

func main() {
	os.Exit(run(context.Background(), os.Args[1:], os.Environ(), os.Stdout, os.Stderr, (&net.ListenConfig{}).Listen))
}

// run executes vandoxd with args (without the program name) and returns the process exit code: 0 on success,
// 1 on a runtime or start-up failure, 2 on a usage error.
func run(ctx context.Context, args, environ []string, stdout, stderr io.Writer, listen listenFunc) int {
	fs := flag.NewFlagSet(binaryName, flag.ContinueOnError)
	fs.SetOutput(stderr)
	configPath := fs.String("config", config.DefaultBackendFile, "path of the configuration file")
	healthFlag := fs.Bool("healthcheck", false, "probe /healthz of the running service and exit 0 when it is healthy")
	versionFlag := fs.Bool("version", false, "print the version and exit")
	if err := fs.Parse(args); err != nil {
		if errors.Is(err, flag.ErrHelp) {
			return 0
		}
		return 2
	}
	if *versionFlag {
		if _, err := fmt.Fprintln(stdout, version.String(binaryName)); err != nil {
			return 1
		}
		return 0
	}
	if fs.Arg(0) == "import" && !*healthFlag {
		return importCommand(ctx, fs.Args()[1:], *configPath, environ, stdout, stderr, importEnv{parsers: importParsers(), now: time.Now})
	}
	if fs.NArg() > 0 {
		_, _ = fmt.Fprintf(stderr, "vandoxd: unexpected argument %q\n", fs.Arg(0))
		fs.Usage()
		return 2
	}
	if *healthFlag {
		return healthcheck(ctx, *configPath, stderr)
	}
	return serve(ctx, *configPath, environ, stderr, listen)
}
