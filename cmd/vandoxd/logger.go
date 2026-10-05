package main

import (
	"errors"
	"io"
	"log/slog"
)

// newLogger returns a JSON slog logger writing to w at level (debug, info, warn or error).
func newLogger(w io.Writer, level string) (*slog.Logger, error) {
	return nil, errors.New("not implemented")
}
