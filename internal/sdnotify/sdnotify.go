// Package sdnotify implements the systemd notification protocol (sd_notify) with the standard library.
package sdnotify

import (
	"errors"
	"fmt"
	"net"
	"strconv"
	"strings"
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

const (
	envSocket = "NOTIFY_SOCKET"
	envUsec   = "WATCHDOG_USEC"
	envPID    = "WATCHDOG_PID"

	// maxSocketBytes is the longest path, and the longest abstract name: sun_path holds 108 bytes with its NUL.
	maxSocketBytes = 107
	// maxUsecDigits is the most digits of WATCHDOG_USEC; the largest accepted value has 16.
	maxUsecDigits = 16
	// maxUsec is the largest WATCHDOG_USEC whose nanoseconds fit a time.Duration.
	maxUsec = 9223372036854775
	// maxPIDDigits is the most digits of WATCHDOG_PID; the largest accepted value has 10.
	maxPIDDigits = 10
	// maxPID is the largest process ID accepted.
	maxPID = 2147483647
)

// Notifier sends notifications to the service manager. The zero value and a notifier without a socket are disabled.
type Notifier struct {
	socket string
}

// FromEnviron returns a notifier for the socket NOTIFY_SOCKET in environ names; a disabled one when it is unset or empty,
// and a disabled one with an error when the address is not supported or the variable is set twice.
func FromEnviron(environ []string) (*Notifier, error) {
	value, err := lookup(environ, envSocket)
	if err != nil {
		return &Notifier{}, err
	}
	if value == "" {
		return &Notifier{}, nil
	}
	if !validSocket(value) {
		return &Notifier{}, errors.New("sdnotify: " + envSocket + ": unsupported address " +
			"(an absolute path of at most 107 bytes or @ and an abstract name of 1 to 107 bytes)")
	}
	return &Notifier{socket: value}, nil
}

// validSocket reports whether address is an absolute path of at most maxSocketBytes bytes or an @ followed by an
// abstract name of 1 to maxSocketBytes bytes.
func validSocket(address string) bool {
	switch {
	case strings.HasPrefix(address, "/"):
		return len(address) <= maxSocketBytes
	case strings.HasPrefix(address, "@"):
		return len(address) >= 2 && len(address) <= maxSocketBytes+1
	}
	return false
}

// lookup returns the value of key in environ; an error when key appears more than once.
func lookup(environ []string, key string) (string, error) {
	var value string
	found := false
	for _, e := range environ {
		v, ok := strings.CutPrefix(e, key+"=")
		if !ok {
			continue
		}
		if found {
			return "", errors.New("sdnotify: " + key + ": set more than once")
		}
		value, found = v, true
	}
	return value, nil
}

// Enabled reports whether the notifier has a socket.
func (n *Notifier) Enabled() bool {
	return n.socket != ""
}

// Notify sends state as one datagram; a disabled notifier sends nothing and returns nil.
func (n *Notifier) Notify(state string) error {
	if !n.Enabled() {
		return nil
	}
	dialer := net.Dialer{Timeout: WriteTimeout}
	conn, err := dialer.Dial("unixgram", n.socket)
	if err != nil {
		return fmt.Errorf("sdnotify: %w", err)
	}
	defer func() { _ = conn.Close() }()

	if err := conn.SetWriteDeadline(time.Now().Add(WriteTimeout)); err != nil {
		return fmt.Errorf("sdnotify: %w", err)
	}
	if _, err := conn.Write([]byte(state)); err != nil {
		return fmt.Errorf("sdnotify: %w", err)
	}
	return nil
}

// WatchdogInterval returns half of WATCHDOG_USEC in environ, or 0 when the watchdog is off or meant for another PID than pid.
func WatchdogInterval(environ []string, pid int) (time.Duration, error) {
	raw, err := lookup(environ, envUsec)
	if err != nil || raw == "" {
		return 0, err
	}
	usec, ok := parseDigits(raw, maxUsecDigits, int64(MinWatchdogPeriod/time.Microsecond), maxUsec)
	if !ok {
		return 0, errors.New("sdnotify: " + envUsec + ": must be a number of microseconds from 100000 to 9223372036854775 in ASCII digits")
	}
	target, err := lookup(environ, envPID)
	if err != nil {
		return 0, err
	}
	if target != "" {
		forPID, ok := parseDigits(target, maxPIDDigits, 1, maxPID)
		if !ok {
			return 0, errors.New("sdnotify: " + envPID + ": must be a process ID from 1 to 2147483647 in ASCII digits")
		}
		if forPID != int64(pid) {
			return 0, nil
		}
	}
	return time.Duration(usec) * time.Microsecond / 2, nil
}

// parseDigits parses s, which must be 1 to maxDigits ASCII digits, and checks it against lo and hi.
func parseDigits(s string, maxDigits int, lo, hi int64) (int64, bool) {
	if s == "" || len(s) > maxDigits {
		return 0, false
	}
	for i := range len(s) {
		if s[i] < '0' || s[i] > '9' {
			return 0, false
		}
	}
	v, err := strconv.ParseInt(s, 10, 64)
	return v, err == nil && v >= lo && v <= hi
}
