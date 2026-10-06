package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/importer"
	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store"
	"github.com/LarsLaskowski/Vandox/internal/config"
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
	fs := flag.NewFlagSet(binaryName+" import", flag.ContinueOnError)
	fs.SetOutput(stderr)
	configFlag := fs.String("config", configPath, "path of the configuration file")
	fs.Usage = func() {
		_, _ = fmt.Fprintf(fs.Output(), "Usage of %s import [-config file] <path>:\n  <path> is a directory, a .tar or .tar.gz archive, a .gz file or a log file\n", binaryName)
		fs.PrintDefaults()
	}
	if err := fs.Parse(args); err != nil {
		if errors.Is(err, flag.ErrHelp) {
			return 0
		}
		return 2
	}
	if fs.NArg() != 1 {
		_, _ = fmt.Fprintf(stderr, "%s import: exactly one path is required\n", binaryName)
		fs.Usage()
		return 2
	}
	ctx, stop := signal.NotifyContext(ctx, syscall.SIGTERM, os.Interrupt)
	defer stop()
	// After the first signal the default handling returns, so a second signal ends the process at once.
	context.AfterFunc(ctx, stop)
	return openAndImport(ctx, fs.Arg(0), *configFlag, environ, stdout, stderr, env)
}

// openAndImport loads the configuration, opens the database and imports root; it returns the exit code.
func openAndImport(ctx context.Context, root, configPath string, environ []string, stdout, stderr io.Writer, env importEnv) int {
	bootstrap, _ := newLogger(stderr, "info")
	cfg, err := config.LoadBackend(configPath, environ)
	if err != nil {
		logError(ctx, bootstrap, "configuration invalid", err)
		return 1
	}
	logger, err := newLogger(stderr, cfg.Log.Level)
	if err != nil {
		logError(ctx, bootstrap, "configuration invalid", err)
		return 1
	}
	registry, err := logparse.NewRegistry(env.parsers...)
	if err != nil {
		logError(ctx, logger, "parsers invalid", err)
		return 1
	}
	db, err := store.Open(ctx, cfg.Storage.Directory)
	if err != nil {
		logError(ctx, logger, "opening database failed", err)
		return 1
	}
	code := importInto(ctx, db, registry, root, logger, stdout, env)
	if err := db.Close(); err != nil {
		logError(ctx, logger, "closing database failed", err)
		code = 1
	}
	return code
}

// importInto runs the import of root into db, prints the summary and returns the exit code.
func importInto(ctx context.Context, db *store.Store, registry *logparse.Registry, root string, logger *slog.Logger, stdout io.Writer, env importEnv) int {
	logger.InfoContext(ctx, "import started", slog.String("path", strconv.Quote(root)))
	sum, err := importer.Run(ctx, root, importer.Options{
		Parsers:  registry,
		Store:    db,
		Now:      env.now,
		Progress: func(p importer.Progress) { logProgress(ctx, logger, p) },
	})
	code := 0
	switch {
	case sum.Interrupted:
		logger.WarnContext(ctx, "import interrupted; run it again to continue")
		code = 1
	case err != nil:
		logError(ctx, logger, "import failed", err)
		code = 1
	case sum.Count(importer.OutcomeFailed) > 0:
		code = 1
	}
	if err == nil || len(sum.Files) > 0 || sum.Interrupted {
		if werr := writeSummary(stdout, sum); werr != nil {
			logError(ctx, logger, "writing the summary failed", werr)
			code = 1
		}
	}
	return code
}

// logError logs err at error level. The text of an error can hold input (a path, a file name), so it is logged
// quoted: the JSON handler writes C1 controls, DEL and format characters raw.
func logError(ctx context.Context, logger *slog.Logger, msg string, err error) {
	logger.ErrorContext(ctx, msg, slog.String("error", strconv.Quote(err.Error())))
}

// logProgress logs a progress event of the import. Values derived from the input (paths, reasons) are logged
// quoted, see logError.
func logProgress(ctx context.Context, logger *slog.Logger, p importer.Progress) {
	switch p.Event {
	case importer.EventScanProgress:
		logger.InfoContext(ctx, "hashing", slog.String("path", strconv.Quote(p.Path)), slog.Int64("bytes", p.Bytes))
	case importer.EventScanned:
		logger.InfoContext(ctx, "scan finished", slog.Int("files", p.Files), slog.Int("pending", p.Pending))
	case importer.EventFileStarted:
		logger.InfoContext(ctx, "file started", slog.String("path", strconv.Quote(p.Path)), slog.String("source_type", p.SourceType))
	case importer.EventFileProgress:
		logger.InfoContext(ctx, "file progress", slog.String("path", strconv.Quote(p.Path)),
			slog.Int64("lines", p.Lines), slog.Int64("records", p.Records))
	case importer.EventFileFinished:
		logFileFinished(ctx, logger, p.Result)
	}
}

// logFileFinished logs the result of one file.
func logFileFinished(ctx context.Context, logger *slog.Logger, r *importer.FileResult) {
	if r == nil {
		return
	}
	attrs := []slog.Attr{
		slog.String("path", strconv.Quote(r.Path)),
		slog.String("outcome", string(r.Outcome)),
		slog.String("source_type", r.SourceType),
		slog.Int64("lines", r.Lines),
		slog.Int64("records", r.Records),
		slog.Int64("skipped", r.Skipped),
	}
	if r.Reason != "" {
		attrs = append(attrs, slog.String("reason", strconv.Quote(r.Reason)))
	}
	logger.LogAttrs(ctx, slog.LevelInfo, "file finished", attrs...)
}

// writeSummary writes s as text to w; every path and reason is quoted with %q.
func writeSummary(w io.Writer, s importer.Summary) error {
	var b strings.Builder
	fmt.Fprintf(&b, "Import of %q\n", s.Root)
	fmt.Fprintf(&b, "Files found:         %d\n", len(s.Files))
	fmt.Fprintf(&b, "  imported:          %d\n", s.Count(importer.OutcomeImported))
	fmt.Fprintf(&b, "  already imported:  %d\n", s.Count(importer.OutcomeAlreadyImported))
	fmt.Fprintf(&b, "  not recognized:    %d\n", s.Count(importer.OutcomeUnrecognized))
	fmt.Fprintf(&b, "  failed:            %d\n", s.Count(importer.OutcomeFailed))
	fmt.Fprintf(&b, "Lines read:          %d\n", s.Lines)
	fmt.Fprintf(&b, "Records stored:      %d\n", s.Records)
	fmt.Fprintf(&b, "Lines skipped:       %d\n", s.Skipped)
	if s.First.IsZero() {
		b.WriteString("Time range:          no records were stored\n")
	} else {
		fmt.Fprintf(&b, "Time range:          %s to %s\n", s.First.UTC().Format(time.RFC3339), s.Last.UTC().Format(time.RFC3339))
	}
	if s.Interrupted {
		b.WriteString("The import was interrupted; run it again to continue.\n")
	}
	writeListed(&b, "Files not recognized", s.Files, importer.OutcomeUnrecognized)
	writeListed(&b, "Files that failed", s.Files, importer.OutcomeFailed)
	writeSkipped(&b, s.Files)
	_, err := io.WriteString(w, b.String())
	return err
}

// writeListed lists the files with outcome o with their reasons under the heading.
func writeListed(b *strings.Builder, heading string, files []importer.FileResult, o importer.Outcome) {
	first := true
	for _, f := range files {
		if f.Outcome != o {
			continue
		}
		if first {
			fmt.Fprintf(b, "\n%s:\n", heading)
			first = false
		}
		fmt.Fprintf(b, "  %q: %q\n", f.Path, f.Reason)
	}
}

// writeSkipped lists the files with lines a parser skipped, with the first reasons.
func writeSkipped(b *strings.Builder, files []importer.FileResult) {
	first := true
	for _, f := range files {
		if f.Skipped == 0 {
			continue
		}
		if first {
			b.WriteString("\nLines skipped by the parsers:\n")
			first = false
		}
		fmt.Fprintf(b, "  %q: %d lines\n", f.Path, f.Skipped)
		for _, p := range f.Problems {
			if p.Line > 0 {
				fmt.Fprintf(b, "    line %d: %q\n", p.Line, p.Reason)
			} else {
				fmt.Fprintf(b, "    %q\n", p.Reason)
			}
		}
	}
}
