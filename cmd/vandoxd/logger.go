package main

import (
	"fmt"
	"io"
	"log/slog"
)

// newLogger returns a JSON slog logger writing to w at level (debug, info, warn or error). JSON escapes
// control characters in values and is machine-readable in the container log.
func newLogger(w io.Writer, level string) (*slog.Logger, error) {
	var lvl slog.Level
	switch level {
	case "debug", "info", "warn", "error":
		if err := lvl.UnmarshalText([]byte(level)); err != nil {
			return nil, fmt.Errorf("log level %q: %w", level, err)
		}
	default:
		return nil, fmt.Errorf("unknown log level %q", level)
	}
	return slog.New(slog.NewJSONHandler(w, &slog.HandlerOptions{Level: lvl})), nil
}
