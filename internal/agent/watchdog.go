package agent

import (
	"context"
	"log/slog"
	"time"
)

// notifier is the part of *sdnotify.Notifier the watchdog uses.
type notifier interface {
	Notify(state string) error
}

// runWatchdog sends sdnotify.Watchdog every interval while healthy(now) holds, until ctx is done.
func runWatchdog(ctx context.Context, n notifier, interval time.Duration, healthy func(time.Time) bool, log *slog.Logger) {
}
