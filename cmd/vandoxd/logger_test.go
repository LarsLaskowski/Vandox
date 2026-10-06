package main

import (
	"bytes"
	"encoding/json"
	"log/slog"
	"strings"
	"testing"
)

// logLines parses every non-empty line of out as a JSON object.
func logLines(t *testing.T, out string) []map[string]any {
	t.Helper()
	var lines []map[string]any
	for _, line := range strings.Split(out, "\n") {
		if line == "" {
			continue
		}
		var m map[string]any
		if err := json.Unmarshal([]byte(line), &m); err != nil {
			t.Fatalf("log line %q is not a JSON object: %v", line, err)
		}
		lines = append(lines, m)
	}
	return lines
}

func TestNewLogger_Levels(t *testing.T) {
	tests := []struct {
		level string
		at    slog.Level // a record at the level is written, one level step below it is dropped
		name  string
	}{
		{"debug", slog.LevelDebug, "DEBUG"},
		{"info", slog.LevelInfo, "INFO"},
		{"warn", slog.LevelWarn, "WARN"},
		{"error", slog.LevelError, "ERROR"},
	}
	for _, tc := range tests {
		t.Run(tc.level, func(t *testing.T) {
			var buf bytes.Buffer
			logger, err := newLogger(&buf, tc.level)
			if err != nil {
				t.Fatalf("newLogger(w, %q) error = %v, want nil", tc.level, err)
			}

			logger.Log(t.Context(), tc.at-4, "below")
			logger.Log(t.Context(), tc.at, "at level")

			lines := logLines(t, buf.String())
			if len(lines) != 1 {
				t.Fatalf("newLogger(w, %q) wrote %d lines %q, want 1 (the record below the level dropped)", tc.level, len(lines), buf.String())
			}
			if got := lines[0]["level"]; got != tc.name {
				t.Errorf("level of the written line = %v, want %q", got, tc.name)
			}
			if got := lines[0]["msg"]; got != "at level" {
				t.Errorf("msg of the written line = %v, want %q", got, "at level")
			}
		})
	}
}

func TestNewLogger_EscapesControlCharacters(t *testing.T) {
	var buf bytes.Buffer
	logger, err := newLogger(&buf, "info")
	if err != nil {
		t.Fatalf("newLogger() error = %v, want nil", err)
	}

	logger.Info("event", "value", "a\nb\x1b[31m")

	lines := logLines(t, buf.String())
	if len(lines) != 1 {
		t.Fatalf("log output %q has %d lines, want 1 (a newline in a value must not split the line)", buf.String(), len(lines))
	}
	if got := lines[0]["value"]; got != "a\nb\x1b[31m" {
		t.Errorf("value attribute = %q, want %q", got, "a\nb\x1b[31m")
	}
}

func TestNewLogger_UnknownLevel(t *testing.T) {
	for _, level := range []string{"verbose", "", "INFO", "warning", "info+1", " info", "trace"} {
		t.Run(level, func(t *testing.T) {
			var buf bytes.Buffer

			logger, err := newLogger(&buf, level)

			if err == nil {
				t.Errorf("newLogger(w, %q) error = nil, want an error", level)
			}
			if logger != nil {
				t.Errorf("newLogger(w, %q) logger = %v, want nil on error", level, logger)
			}
		})
	}
}
