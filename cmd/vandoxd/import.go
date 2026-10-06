package main

import (
	"context"
	"io"
	"time"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/importer"
	"github.com/LarsLaskowski/Vandox/internal/logparse"
)

// importEnv is what importCommand takes from the process: the parsers and the clock.
type importEnv struct {
	parsers []logparse.Parser
	now     func() time.Time
}

// importParsers returns the parsers of vandoxd import, in priority order; none until #16.
func importParsers() []logparse.Parser {
	return nil
}

// importCommand runs "vandoxd import" with the arguments after "import" and returns the exit code.
func importCommand(ctx context.Context, args []string, configPath string, environ []string, stdout, stderr io.Writer, env importEnv) int {
	return 1
}

// writeSummary writes s as text to w; every path and reason is quoted with %q.
func writeSummary(w io.Writer, s importer.Summary) error {
	return nil
}
