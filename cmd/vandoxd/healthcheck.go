package main

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/config"
)

// healthTimeout bounds the whole health check, including the connection.
const healthTimeout = 4 * time.Second

// healthcheck probes /healthz of the web listener configured in configPath and returns the exit code. It
// loads the configuration without environment, so it reads no secret.
func healthcheck(ctx context.Context, configPath string, stderr io.Writer) int {
	if err := checkHealth(ctx, configPath); err != nil {
		_, _ = fmt.Fprintf(stderr, "vandoxd: health check failed: %v\n", err)
		return 1
	}
	return 0
}

// checkHealth loads the configuration and probes the web listener's /healthz.
func checkHealth(ctx context.Context, configPath string) error {
	cfg, err := config.LoadBackend(configPath, nil)
	if err != nil {
		return err
	}
	target, err := healthURL(cfg.Web.Listen)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(ctx, healthTimeout)
	defer cancel()
	return probe(ctx, newHealthClient(), target)
}

// healthURL returns the /healthz URL for the web listen address listen, using loopback for an empty or
// unspecified host.
func healthURL(listen string) (string, error) {
	host, port, err := net.SplitHostPort(listen)
	if err != nil {
		return "", fmt.Errorf("web listen address: %w", err)
	}
	if addr, err := netip.ParseAddr(host); err == nil {
		addr = addr.Unmap()
		switch {
		case addr.IsUnspecified() && addr.Is4():
			host = "127.0.0.1"
		case addr.IsUnspecified():
			host = "::1"
		default:
			host = addr.String()
		}
	} else if host == "" {
		host = "127.0.0.1"
	}
	u := url.URL{Scheme: "http", Host: net.JoinHostPort(host, port), Path: "/healthz"}
	return u.String(), nil
}

// newHealthClient returns the HTTP client of the health check: 4 s timeout, no proxy, no redirects.
func newHealthClient() *http.Client {
	return &http.Client{
		Timeout:   healthTimeout,
		Transport: &http.Transport{Proxy: nil, DisableKeepAlives: true},
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
}

// probe sends GET url with client and returns nil only for status 200.
func probe(ctx context.Context, client *http.Client, url string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("unexpected status %d", resp.StatusCode)
	}
	return nil
}
