package agent

import (
	"context"
	"log/slog"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/sdnotify"
)

// notifier is the part of *sdnotify.Notifier the watchdog uses.
type notifier interface {
	Notify(state string) error
}

// watchdogState is what the watchdog loop remembers between ticks, to log a change once.
type watchdogState struct {
	withheld bool // the last tick found the scheduler unhealthy
	failing  bool // the last notification failed
}

// runWatchdog sends sdnotify.Watchdog every interval while healthy(now) holds, until ctx is done.
func runWatchdog(ctx context.Context, n notifier, interval time.Duration, healthy func(time.Time) bool, log *slog.Logger) {
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	var st watchdogState
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			st.tick(n, healthy(time.Now()), log)
		}
	}
}

// tick handles one tick of the watchdog loop.
func (st *watchdogState) tick(n notifier, healthy bool, log *slog.Logger) {
	if !healthy {
		if !st.withheld {
			log.Warn("watchdog withheld", "reason", "a collector runner is past its liveness deadline")
		}
		st.withheld = true
		return
	}
	if st.withheld {
		log.Info("watchdog resumed")
		st.withheld = false
	}
	err := n.Notify(sdnotify.Watchdog)
	if err != nil && !st.failing {
		log.Warn("watchdog notification failed", "err", err)
	}
	st.failing = err != nil
}
