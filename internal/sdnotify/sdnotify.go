// Package sdnotify implements the systemd notification protocol (sd_notify) with the standard library.
package sdnotify

import (
	"errors"
	"time"
)

// Notification states.
const (
	Ready    = "READY=1"
	Watchdog = "WATCHDOG=1"
	Stopping = "STOPPING=1"
)

// WriteTimeout bounds the dial and the write of one notification.
const WriteTimeout = time.Second

// MinWatchdogPeriod is the smallest WATCHDOG_USEC accepted; a smaller one is an error and leaves the watchdog off.
const MinWatchdogPeriod = 100 * time.Millisecond

var errNotImplemented = errors.New("not implemented")

// Notifier sends notifications to the service manager. The zero value and a notifier without a socket are disabled.
type Notifier struct {
	socket string
}

// FromEnviron returns a notifier for the socket NOTIFY_SOCKET in environ names; a disabled one when it is unset or empty,
// and a disabled one with an error when the address is not supported or the variable is set twice.
func FromEnviron(environ []string) (*Notifier, error) {
	return &Notifier{}, nil
}

// Enabled reports whether the notifier has a socket.
func (n *Notifier) Enabled() bool {
	return false
}

// Notify sends state as one datagram; a disabled notifier sends nothing and returns nil.
func (n *Notifier) Notify(state string) error {
	return errNotImplemented
}

// WatchdogInterval returns half of WATCHDOG_USEC in environ, or 0 when the watchdog is off or meant for another PID than pid.
func WatchdogInterval(environ []string, pid int) (time.Duration, error) {
	return 0, errNotImplemented
}
