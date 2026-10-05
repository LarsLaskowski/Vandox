package main

import (
	"context"
	"errors"
	"io"
	"net/http"
)

// healthcheck probes /healthz of the web listener configured in configPath and returns the exit code. It
// loads the configuration without environment, so it reads no secret.
func healthcheck(ctx context.Context, configPath string, stderr io.Writer) int {
	return 1
}

// healthURL returns the /healthz URL for the web listen address listen, using loopback for an empty or
// unspecified host.
func healthURL(listen string) (string, error) {
	return "", errors.New("not implemented")
}

// newHealthClient returns the HTTP client of the health check: 4 s timeout, no proxy, no redirects.
func newHealthClient() *http.Client {
	return &http.Client{}
}

// probe sends GET url with client and returns nil only for status 200.
func probe(ctx context.Context, client *http.Client, url string) error {
	return errors.New("not implemented")
}
