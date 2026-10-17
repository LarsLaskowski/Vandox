package agent

import (
	"context"
	"errors"
	"log/slog"
	"slices"
	"sync"
	"testing"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/sdnotify"
)

// ping is one call of Notify.
type ping struct {
	at    time.Time
	state string
}

// recordingNotifier is a notifier that records every call; every call fails with err when it is set.
type recordingNotifier struct {
	mu    sync.Mutex
	pings []ping
	err   error
}

func (n *recordingNotifier) Notify(state string) error {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.pings = append(n.pings, ping{at: time.Now(), state: state})
	return n.err
}

func (n *recordingNotifier) calls() []ping {
	n.mu.Lock()
	defer n.mu.Unlock()
	return append([]ping(nil), n.pings...)
}

// watchdogRun starts runWatchdog in a bubble and returns the base time, its log, a function that stops it and waits
// for its return.
func watchdogRun(
	t *testing.T, n notifier, interval time.Duration, healthy func(time.Time) bool,
) (base time.Time, logs *logStore, stop func()) {
	t.Helper()

	logs = &logStore{}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	base = time.Now()
	go func() {
		defer close(done)
		runWatchdog(ctx, n, interval, healthy, slog.New(&recordingHandler{store: logs}))
	}()
	stop = func() {
		cancel()
		select {
		case <-done:
		case <-time.After(time.Second):
			t.Fatalf("runWatchdog did not return within 1s after its context was cancelled")
		}
	}
	return base, logs, stop
}

func always(time.Time) bool { return true }

func requirePings(t *testing.T, n *recordingNotifier, base time.Time, want []time.Duration) {
	t.Helper()

	got := n.calls()
	if len(got) != len(want) {
		t.Fatalf("notifications = %d, want %d (at %v)", len(got), len(want), want)
	}
	for i, w := range want {
		if got[i].state != sdnotify.Watchdog || got[i].at.Sub(base) != w {
			t.Errorf("notification %d = %q at %v, want %q at %v", i, got[i].state, got[i].at.Sub(base), sdnotify.Watchdog, w)
		}
	}
}

func TestWatchdog_PingsWhileHealthy(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		n := &recordingNotifier{}
		base, _, stop := watchdogRun(t, n, 10*time.Second, always)

		time.Sleep(35 * time.Second)
		synctest.Wait()
		stop()

		requirePings(t, n, base, []time.Duration{10 * time.Second, 20 * time.Second, 30 * time.Second})
	})
}

func TestWatchdog_SkipsWhileUnhealthy(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		n := &recordingNotifier{}
		var base time.Time
		healthy := func(now time.Time) bool {
			off := now.Sub(base)
			return off < 15*time.Second || off >= 35*time.Second
		}
		base = time.Now()
		_, logs, stop := watchdogRun(t, n, 10*time.Second, healthy)

		time.Sleep(45 * time.Second)
		synctest.Wait()
		stop()

		requirePings(t, n, base, []time.Duration{10 * time.Second, 40 * time.Second})
		withheld := logs.find(slog.LevelWarn, "watchdog withheld")
		if len(withheld) != 1 || withheld[0].at.Sub(base) != 20*time.Second {
			t.Errorf(`warnings "watchdog withheld" = %v, want exactly one at 20s`, withheld)
		}
		resumed := logs.find(slog.LevelInfo, "watchdog resumed")
		if len(resumed) != 1 || resumed[0].at.Sub(base) != 40*time.Second {
			t.Errorf(`info lines "watchdog resumed" = %v, want exactly one at 40s`, resumed)
		}
	})
}

func TestWatchdog_NotifyErrorLoggedOnce(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		n := &recordingNotifier{err: errors.New("socket gone")}
		_, logs, stop := watchdogRun(t, n, 10*time.Second, always)

		time.Sleep(35 * time.Second)
		synctest.Wait()
		stop()

		if got := len(n.calls()); got != 3 {
			t.Errorf("notifications over three ticks = %d, want 3", got)
		}
		failed := logs.find(slog.LevelWarn, "watchdog notification failed")
		if len(failed) != 1 {
			t.Errorf(`warnings "watchdog notification failed" for three failing ticks = %d, want 1`, len(failed))
		}
	})
}

// logEntry is one log line a recordingHandler stored.
type logEntry struct {
	at    time.Time
	level slog.Level
	msg   string
	attrs map[string]string
}

// logStore collects log entries.
type logStore struct {
	mu      sync.Mutex
	entries []logEntry
}

// find returns the entries of level with message msg.
func (l *logStore) find(level slog.Level, msg string) []logEntry {
	l.mu.Lock()
	defer l.mu.Unlock()

	var out []logEntry
	for _, e := range l.entries {
		if e.level == level && e.msg == msg {
			out = append(out, e)
		}
	}
	return out
}

// recordingHandler is a slog.Handler that stores entries (groups are flattened).
type recordingHandler struct {
	store *logStore
	attrs []slog.Attr
}

func (h *recordingHandler) Enabled(context.Context, slog.Level) bool { return true }

func (h *recordingHandler) Handle(_ context.Context, r slog.Record) error {
	e := logEntry{at: time.Now(), level: r.Level, msg: r.Message, attrs: map[string]string{}}
	for _, a := range h.attrs {
		e.attrs[a.Key] = a.Value.Resolve().String()
	}
	r.Attrs(func(a slog.Attr) bool {
		e.attrs[a.Key] = a.Value.Resolve().String()
		return true
	})
	h.store.mu.Lock()
	h.store.entries = append(h.store.entries, e)
	h.store.mu.Unlock()
	return nil
}

func (h *recordingHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &recordingHandler{store: h.store, attrs: append(slices.Clone(h.attrs), attrs...)}
}

func (h *recordingHandler) WithGroup(string) slog.Handler { return h }
