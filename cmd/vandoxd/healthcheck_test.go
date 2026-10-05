package main

import (
	"bytes"
	"context"
	"errors"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"sync/atomic"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/config"
)

func TestHealthURL(t *testing.T) {
	tests := []struct {
		listen string
		want   string
	}{
		{":8080", "http://127.0.0.1:8080/healthz"},
		{"0.0.0.0:8080", "http://127.0.0.1:8080/healthz"},
		{"[::]:8080", "http://[::1]:8080/healthz"},
		{"[::ffff:0.0.0.0]:8080", "http://127.0.0.1:8080/healthz"},
		{"127.0.0.1:9000", "http://127.0.0.1:9000/healthz"},
		{"192.0.2.10:8080", "http://192.0.2.10:8080/healthz"},
		{"[::1]:8080", "http://[::1]:8080/healthz"},
		{"[fe80::1%eth0]:8080", "http://[fe80::1%25eth0]:8080/healthz"},
	}
	for _, tc := range tests {
		t.Run(tc.listen, func(t *testing.T) {
			got, err := healthURL(tc.listen)

			if err != nil {
				t.Fatalf("healthURL(%q) error = %v, want nil", tc.listen, err)
			}
			if got != tc.want {
				t.Errorf("healthURL(%q) = %q, want %q", tc.listen, got, tc.want)
			}
		})
	}
}

func TestHealthURL_WithoutPort(t *testing.T) {
	for _, listen := range []string{"127.0.0.1", "", "localhost"} {
		t.Run(listen, func(t *testing.T) {
			got, err := healthURL(listen)

			if err == nil {
				t.Errorf("healthURL(%q) = %q, want an error", listen, got)
			}
		})
	}
}

func TestNewHealthClient(t *testing.T) {
	client := newHealthClient()

	if client.Timeout.Seconds() != 4 {
		t.Errorf("newHealthClient().Timeout = %v, want 4s", client.Timeout)
	}
	transport, ok := client.Transport.(*http.Transport)
	if !ok {
		t.Fatalf("newHealthClient().Transport = %T, want *http.Transport", client.Transport)
	}
	if transport.Proxy != nil {
		t.Error("newHealthClient() transport has a Proxy function, want nil (HTTP_PROXY must be ignored)")
	}
	if client.CheckRedirect == nil {
		t.Fatal("newHealthClient().CheckRedirect = nil, want a function returning http.ErrUseLastResponse")
	}
	if err := client.CheckRedirect(&http.Request{}, nil); !errors.Is(err, http.ErrUseLastResponse) {
		t.Errorf("CheckRedirect() = %v, want http.ErrUseLastResponse", err)
	}
}

func TestProbe(t *testing.T) {
	tests := []struct {
		status  int
		wantErr string
	}{
		{http.StatusOK, ""},
		{http.StatusServiceUnavailable, "unexpected status 503"},
		{http.StatusNotFound, "unexpected status 404"},
		{http.StatusNoContent, "unexpected status 204"},
	}
	for _, tc := range tests {
		t.Run(strconv.Itoa(tc.status), func(t *testing.T) {
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				w.WriteHeader(tc.status)
			}))
			t.Cleanup(srv.Close)

			err := probe(t.Context(), newHealthClient(), srv.URL)

			switch {
			case tc.wantErr == "" && err != nil:
				t.Errorf("probe(status %d) error = %v, want nil", tc.status, err)
			case tc.wantErr != "" && (err == nil || err.Error() != tc.wantErr):
				t.Errorf("probe(status %d) error = %v, want %q", tc.status, err, tc.wantErr)
			}
		})
	}
}

func TestProbe_Failures(t *testing.T) {
	t.Run("cancelled context", func(t *testing.T) {
		srv := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
		t.Cleanup(srv.Close)
		ctx, cancel := context.WithCancel(t.Context())
		cancel()

		if err := probe(ctx, newHealthClient(), srv.URL); err == nil {
			t.Error("probe(cancelled ctx) error = nil, want an error")
		}
	})

	t.Run("connection refused", func(t *testing.T) {
		srv := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
		addr := srv.URL
		srv.Close()

		if err := probe(t.Context(), newHealthClient(), addr); err == nil {
			t.Error("probe(closed server) error = nil, want an error")
		}
	})
}

// writeBackendConfig writes a vandoxd.yaml with the given web listen address, a different ingest port, the
// storage directory dir and log level level, and returns its path.
func writeBackendConfig(t *testing.T, web, dir, level string) string {
	t.Helper()
	ingest := ":8081"
	if _, port, err := net.SplitHostPort(web); err == nil {
		n, _ := strconv.Atoi(port)
		ingest = "127.0.0.1:" + strconv.Itoa(n%64000+1)
	}
	content := "web:\n  listen: \"" + web + "\"\ningest:\n  listen: \"" + ingest + "\"\nstorage:\n  directory: " + dir + "\nlog:\n  level: " + level + "\n"
	path := filepath.Join(t.TempDir(), "vandoxd.yaml")
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}

// listenAddress returns host:port of an httptest server.
func listenAddress(t *testing.T, srv *httptest.Server) string {
	t.Helper()
	u, err := url.Parse(srv.URL)
	if err != nil {
		t.Fatal(err)
	}
	return u.Host
}

func statusServer(t *testing.T, status int) *httptest.Server {
	t.Helper()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(status)
	}))
	t.Cleanup(srv.Close)
	return srv
}

func TestRun_Healthcheck(t *testing.T) {
	tests := []struct {
		name       string
		status     int
		wantCode   int
		wantStderr string
	}{
		{"status 200 is healthy", http.StatusOK, 0, ""},
		{"status 503 is unhealthy", http.StatusServiceUnavailable, 1, "vandoxd: health check failed: unexpected status 503\n"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			srv := statusServer(t, tc.status)
			dir := t.TempDir()
			cfg := writeBackendConfig(t, listenAddress(t, srv), dir, "info")
			var stdout, stderr bytes.Buffer

			got := run(t.Context(), []string{"-healthcheck", "-config", cfg}, nil, &stdout, &stderr, noListen(t))

			if got != tc.wantCode {
				t.Errorf("run(-healthcheck) exit code = %d, want %d (stderr %q)", got, tc.wantCode, stderr.String())
			}
			if stdout.Len() != 0 {
				t.Errorf("run(-healthcheck) stdout = %q, want empty", stdout.String())
			}
			if stderr.String() != tc.wantStderr {
				t.Errorf("run(-healthcheck) stderr = %q, want %q", stderr.String(), tc.wantStderr)
			}
			assertNoDatabase(t, dir)
		})
	}
}

func assertNoDatabase(t *testing.T, dir string) {
	t.Helper()
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 0 {
		t.Errorf("storage directory holds %d entries, want none (the health check creates no database)", len(entries))
	}
}

func TestRun_HealthcheckDoesNotFollowRedirects(t *testing.T) {
	var targetRequests atomic.Int32
	target := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		targetRequests.Add(1)
	}))
	t.Cleanup(target.Close)
	redirecting := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, target.URL, http.StatusFound)
	}))
	t.Cleanup(redirecting.Close)
	cfg := writeBackendConfig(t, listenAddress(t, redirecting), t.TempDir(), "info")
	var stdout, stderr bytes.Buffer

	got := run(t.Context(), []string{"-healthcheck", "-config", cfg}, nil, &stdout, &stderr, noListen(t))

	if got != 1 {
		t.Errorf("run(-healthcheck) with a redirect exit code = %d, want 1", got)
	}
	if n := targetRequests.Load(); n != 0 {
		t.Errorf("redirect target received %d requests, want 0 (redirects are not followed)", n)
	}
}

func TestRun_HealthcheckInvalidConfiguration(t *testing.T) {
	dir := t.TempDir()
	cfg := writeBackendConfig(t, "127.0.0.1:18080", dir, "verbose")
	var stdout, stderr bytes.Buffer

	got := run(t.Context(), []string{"-healthcheck", "-config", cfg}, nil, &stdout, &stderr, noListen(t))

	if got != 1 {
		t.Errorf("run(-healthcheck) with an invalid configuration exit code = %d, want 1", got)
	}
	if !bytes.Contains(stderr.Bytes(), []byte("log.level")) {
		t.Errorf("stderr = %q, want the configuration error naming log.level", stderr.String())
	}
	assertNoDatabase(t, dir)
}

func TestRun_HealthcheckMissingConfiguration(t *testing.T) {
	var stdout, stderr bytes.Buffer
	missing := filepath.Join(t.TempDir(), "absent.yaml")

	got := run(t.Context(), []string{"-healthcheck", "-config", missing}, nil, &stdout, &stderr, noListen(t))

	if got != 1 {
		t.Errorf("run(-healthcheck) with a missing file exit code = %d, want 1", got)
	}
	if stderr.Len() == 0 {
		t.Error("stderr is empty, want the configuration error")
	}
}

func TestRun_HealthcheckIgnoresEnvironment(t *testing.T) {
	srv := statusServer(t, http.StatusOK)
	cfg := writeBackendConfig(t, listenAddress(t, srv), t.TempDir(), "info")
	environ := []string{
		config.EnvAgentToken + config.FileSuffix + "=" + filepath.Join(t.TempDir(), "missing-token"),
		"VANDOX_BOGUS=1",
	}
	var stdout, stderr bytes.Buffer

	got := run(t.Context(), []string{"-healthcheck", "-config", cfg}, environ, &stdout, &stderr, noListen(t))

	if got != 0 {
		t.Errorf("run(-healthcheck) with a broken environment exit code = %d, want 0 (stderr %q)", got, stderr.String())
	}
	if stderr.Len() != 0 {
		t.Errorf("run(-healthcheck) stderr = %q, want empty", stderr.String())
	}
}
