package sdnotify_test

import (
	"crypto/rand"
	"net"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/sdnotify"
)

const (
	errSocket = "sdnotify: NOTIFY_SOCKET: unsupported address " +
		"(an absolute path of at most 107 bytes or @ and an abstract name of 1 to 107 bytes)"
	errUsec = "sdnotify: WATCHDOG_USEC: must be a number of microseconds from 100000 to 9223372036854775 in ASCII digits"
	errPID  = "sdnotify: WATCHDOG_PID: must be a process ID from 1 to 2147483647 in ASCII digits"

	// receiveLimit is the safety net of a read on a socket; a datagram that was sent arrives at once.
	receiveLimit = 10 * time.Second
)

func TestFromEnviron(t *testing.T) {
	tests := []struct {
		name        string
		environ     []string
		wantEnabled bool
		wantErr     string
	}{
		{"unset", nil, false, ""},
		{"other variables only", []string{"HOME=/root", "NOTIFY_SOCKETX=/run/x"}, false, ""},
		{"empty", []string{"NOTIFY_SOCKET="}, false, ""},
		{"absolute path", []string{"NOTIFY_SOCKET=/run/systemd/notify"}, true, ""},
		{"107 byte path", []string{"NOTIFY_SOCKET=/" + strings.Repeat("a", 106)}, true, ""},
		{"abstract name", []string{"NOTIFY_SOCKET=@abstract"}, true, ""},
		{"abstract name of 107 bytes", []string{"NOTIFY_SOCKET=@" + strings.Repeat("a", 107)}, true, ""},
		{"relative path", []string{"NOTIFY_SOCKET=run/notify"}, false, errSocket},
		{"dot relative path", []string{"NOTIFY_SOCKET=./notify"}, false, errSocket},
		{"at sign alone", []string{"NOTIFY_SOCKET=@"}, false, errSocket},
		{"108 byte path", []string{"NOTIFY_SOCKET=/" + strings.Repeat("a", 107)}, false, errSocket},
		{"abstract name of 108 bytes", []string{"NOTIFY_SOCKET=@" + strings.Repeat("a", 108)}, false, errSocket},
		{"vsock", []string{"NOTIFY_SOCKET=vsock:2:1234"}, false, errSocket},
		{"vsock-stream", []string{"NOTIFY_SOCKET=vsock-stream:2:1234"}, false, errSocket},
		{"vsock-dgram", []string{"NOTIFY_SOCKET=vsock-dgram:2:1234"}, false, errSocket},
		{"leading space", []string{"NOTIFY_SOCKET= /run/x"}, false, errSocket},
		{
			"set twice",
			[]string{"NOTIFY_SOCKET=/run/a", "NOTIFY_SOCKET=/run/b"},
			false, "sdnotify: NOTIFY_SOCKET: set more than once",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			n, err := sdnotify.FromEnviron(tc.environ)

			switch {
			case tc.wantErr == "" && err != nil:
				t.Errorf("FromEnviron() error = %v, want nil", err)
			case tc.wantErr != "" && (err == nil || err.Error() != tc.wantErr):
				t.Errorf("FromEnviron() error = %v, want %q", err, tc.wantErr)
			}
			if n == nil {
				t.Fatalf("FromEnviron() notifier = nil, want a notifier (disabled on an error)")
			}
			if n.Enabled() != tc.wantEnabled {
				t.Errorf("FromEnviron().Enabled() = %v, want %v", n.Enabled(), tc.wantEnabled)
			}
		})
	}
}

// listen binds a unixgram socket at address ("@name" for the abstract namespace).
func listen(t *testing.T, address string) net.PacketConn {
	t.Helper()

	conn, err := net.ListenPacket("unixgram", address)
	if err != nil {
		t.Fatalf("binding the unixgram socket %q: %v", address, err)
	}
	t.Cleanup(func() { _ = conn.Close() })
	return conn
}

// receive reads one datagram.
func receive(t *testing.T, conn net.PacketConn) string {
	t.Helper()

	if err := conn.SetReadDeadline(time.Now().Add(receiveLimit)); err != nil {
		t.Fatalf("SetReadDeadline: %v", err)
	}
	buf := make([]byte, 256)
	n, _, err := conn.ReadFrom(buf)
	if err != nil {
		t.Fatalf("reading a datagram: %v", err)
	}
	return string(buf[:n])
}

func notifierFor(t *testing.T, address string) *sdnotify.Notifier {
	t.Helper()

	n, err := sdnotify.FromEnviron([]string{"NOTIFY_SOCKET=" + address})
	if err != nil || !n.Enabled() {
		t.Fatalf("FromEnviron(%q) = enabled %v, error %v, want an enabled notifier", address, n.Enabled(), err)
	}
	return n
}

func TestNotifier_Notify(t *testing.T) {
	sockets := map[string]func(t *testing.T) string{
		"path socket": func(t *testing.T) string { return filepath.Join(t.TempDir(), "n.sock") },
		"abstract socket": func(*testing.T) string {
			return "@vandox-test-" + rand.Text()
		},
	}

	for name, address := range sockets {
		t.Run(name, func(t *testing.T) {
			addr := address(t)
			conn := listen(t, addr)
			n := notifierFor(t, addr)

			for _, state := range []string{sdnotify.Ready, sdnotify.Watchdog, sdnotify.Stopping} {
				if err := n.Notify(state); err != nil {
					t.Fatalf("Notify(%q) error = %v, want nil", state, err)
				}
				if got := receive(t, conn); got != state {
					t.Errorf("datagram after Notify(%q) = %q, want exactly %q", state, got, state)
				}
			}
		})
	}
}

func TestNotifier_NotifyDisabled(t *testing.T) {
	conn := listen(t, filepath.Join(t.TempDir(), "n.sock"))
	disabled, err := sdnotify.FromEnviron(nil)
	if err != nil {
		t.Fatalf("FromEnviron(nil) error = %v, want nil", err)
	}
	notifiers := map[string]*sdnotify.Notifier{"from an empty environment": disabled, "zero value": {}}

	for name, n := range notifiers {
		t.Run(name, func(t *testing.T) {
			if err := n.Notify(sdnotify.Watchdog); err != nil {
				t.Errorf("Notify() on a disabled notifier = %v, want nil", err)
			}
			if n.Enabled() {
				t.Errorf("Enabled() = true, want false")
			}
		})
	}

	// A notifier that is enabled for the socket sends after the disabled ones did not: the next datagram is its own.
	enabled := notifierFor(t, conn.LocalAddr().String())
	if err := enabled.Notify(sdnotify.Ready); err != nil {
		t.Fatalf("Notify() error = %v, want nil", err)
	}
	if got := receive(t, conn); got != sdnotify.Ready {
		t.Errorf("first datagram on the socket = %q, want %q (the disabled notifiers sent nothing)", got, sdnotify.Ready)
	}
}

func TestNotifier_NotifyNoListener(t *testing.T) {
	n := notifierFor(t, filepath.Join(t.TempDir(), "missing.sock"))

	err := n.Notify(sdnotify.Ready)

	if err == nil || !strings.HasPrefix(err.Error(), "sdnotify: ") {
		t.Errorf("Notify() to a path where no socket listens = %v, want an error starting with %q", err, "sdnotify: ")
	}
}

func TestWatchdogInterval(t *testing.T) {
	const pid = 4242
	usec := func(v string) []string { return []string{"WATCHDOG_USEC=" + v} }
	usecAndPID := func(u, p string) []string { return []string{"WATCHDOG_USEC=" + u, "WATCHDOG_PID=" + p} }

	tests := []struct {
		name    string
		environ []string
		want    time.Duration
		wantErr string
	}{
		{"unset", nil, 0, ""},
		{"empty", usec(""), 0, ""},
		{"for another process", usecAndPID("1000000", "4243"), 0, ""},
		{"floor", usec("100000"), 50 * time.Millisecond, ""},
		{"largest", usec("9223372036854775"), 4611686018427387500, ""},
		{"for this process", usecAndPID("30000000", "4242"), 15 * time.Second, ""},
		{"without PID", usec("30000000"), 15 * time.Second, ""},
		{"usec zero", usec("0"), 0, errUsec},
		{"usec one", usec("1"), 0, errUsec},
		{"usec below floor", usec("99999"), 0, errUsec},
		{"usec negative", usec("-1"), 0, errUsec},
		{"usec plus", usec("+5"), 0, errUsec},
		{"usec leading space", usec(" 5"), 0, errUsec},
		{"usec trailing space", usec("5 "), 0, errUsec},
		{"usec exponent", usec("1e6"), 0, errUsec},
		{"usec hex", usec("0x10"), 0, errUsec},
		{"usec letters", usec("abc"), 0, errUsec},
		{"usec above the largest", usec("9223372036854776"), 0, errUsec},
		{"usec 20 digits", usec("12345678901234567890"), 0, errUsec},
		{"usec full-width digit", usec("５"), 0, errUsec},
		{"pid zero", usecAndPID("1000000", "0"), 0, errPID},
		{"pid negative", usecAndPID("1000000", "-1"), 0, errPID},
		{"pid letters", usecAndPID("1000000", "abc"), 0, errPID},
		{"pid above the largest", usecAndPID("1000000", "2147483648"), 0, errPID},
		{
			"usec twice", []string{"WATCHDOG_USEC=1000000", "WATCHDOG_USEC=1000000"},
			0, "sdnotify: WATCHDOG_USEC: set more than once",
		},
		{
			"pid twice", []string{"WATCHDOG_USEC=1000000", "WATCHDOG_PID=4242", "WATCHDOG_PID=4242"},
			0, "sdnotify: WATCHDOG_PID: set more than once",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got, err := sdnotify.WatchdogInterval(tc.environ, pid)

			switch {
			case tc.wantErr == "" && err != nil:
				t.Errorf("WatchdogInterval(%q, %d) error = %v, want nil", tc.environ, pid, err)
			case tc.wantErr != "" && (err == nil || err.Error() != tc.wantErr):
				t.Errorf("WatchdogInterval(%q, %d) error = %v, want %q", tc.environ, pid, err, tc.wantErr)
			}
			if got != tc.want {
				t.Errorf("WatchdogInterval(%q, %d) = %v, want %v", tc.environ, pid, got, tc.want)
			}
		})
	}
}
