// Package agent runs vandox-agent: configuration, logging, the scheduler with its collectors and the systemd notification.
package agent

import (
	"context"
	"io"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/selfstats"
)

// Schedule of the self-metrics collector.
const (
	SelfInterval = 60 * time.Second
	SelfTimeout  = 10 * time.Second
)

// Options are the process boundaries Run works with.
type Options struct {
	ConfigPath string            // the configuration file
	Environ    []string          // the environment, in os.Environ form
	Stderr     io.Writer         // the log output
	Sink       scheduler.Sink    // nil: scheduler.DiscardSink
	Self       selfstats.Sources // the self-metrics sources; zero fields take their defaults
}

// Run runs the agent until ctx is done and returns the exit code: 0 after a clean stop, 1 when it cannot start.
func Run(ctx context.Context, opts Options) int {
	return 1
}
