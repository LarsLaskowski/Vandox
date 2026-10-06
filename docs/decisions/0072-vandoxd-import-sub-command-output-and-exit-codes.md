# 0072: vandoxd gets the sub-command import; progress as JSON on stderr, the summary as text on stdout, exit code 1 when a file failed

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Issue #15
- **Supersedes:** 0058

## Context

Record 0058 made `vandoxd` run the service without arguments, with its own flag set (`-config`,
`-healthcheck`, `-version`), JSON logging with `slog`, a 10 s shutdown deadline, and exit code 2 for a flag
error **or a positional argument**. Issue #15 requires the trigger "CLI sub-command (`vandoxd import
<path>`)", which makes one positional argument meaningful and so changes 0058's decision. The image's
`ENTRYPOINT` is `["/vandoxd"]`, the distroless image has no shell, and the compose file mounts the import
directory at `/import` (0060), so the operator runs `docker exec vandoxd /vandoxd import /import/<name>`;
the process inherits the container's environment (`VANDOX_*` secrets checked by `config.LoadBackend`) and the
configuration file at the default path. Issue #15 asks for progress reporting and a final summary with
files, lines, time range, errors and unrecognized files; the web UI trigger is a later issue, so the result
must exist as data (`importer.Summary`), not only as text.

## Options considered

1. **Command form**: `vandoxd import <path>` as the first positional argument (what the issue names), or a
   flag (`-import <path>`). The sub-command was chosen: the issue names it, and it leaves room for further
   sub-commands. Flags of the sub-command: none of its own besides `-config`, accepted before and after
   `import`, because `vandoxd import -config f p` is what users type and `vandoxd -config f import p` is
   what the global flag set already parses.
2. **Where output goes**: everything as JSON log lines on stderr (uniform, but the summary becomes hard to
   read for a person at a terminal); everything as text (progress is hard to filter); or **progress as JSON lines on stderr through the same `slog` handler as the service,
   the summary as text on stdout** — readable at the end, filterable during the run, and stdout carries only
   the result. For input-derived values the JSON handler's escaping alone was considered sufficient at
   first; the security plan review showed it writes U+009B and U+202E raw, so those values are quoted
   before they reach the handler (see *Decision*).
3. **Exit codes**: 0 also when files failed (the summary tells), or **1 whenever a file failed or the run was
   interrupted**, so scripts notice; "not recognized" alone stays 0, because a saved `/var/log` always holds
   files no parser claims (e.g. `wtmp`, `lastlog`).

## Decision

Carried over from 0058 unchanged: `main()` is the single statement
`os.Exit(run(context.Background(), os.Args[1:], os.Environ(), os.Stdout, os.Stderr, (&net.ListenConfig{}).Listen))`;
`run(ctx, args, environ, stdout, stderr, listen) int` parses its own `flag.FlagSet` (`ContinueOnError`, usage
header `Usage of vandoxd:`) with `-config` (default `/etc/vandox/vandoxd.yaml`), `-healthcheck` and
`-version`; `-version` wins over everything; without `-version`, `-healthcheck` (0059) or a sub-command `run`
serves as 0058 describes (SIGTERM/SIGINT, default handling restored after the first signal, configuration,
JSON `slog` at `log.level` with an `info` bootstrap logger, store, listeners, 10 s shutdown deadline, store
closed after the listeners; errors as attributes; secrets never logged); each binary has a `binaryName`
constant, `main()` is the accepted uncovered line, and `run` is tested in `main_test.go`. `vandox-agent` keeps
`internal/cli.Run` until #30.

Changed:

- The first positional argument `import` starts the sub-command (`importCommand`, `cmd/vandoxd/import.go`)
  with the remaining arguments; `-healthcheck` together with `import` is a usage error. Any other positional
  argument is a usage error as before. The usage text lists `vandoxd [flags] import [-config file] <path>`.
- `vandoxd import` has its own flag set with `-config` (default: the value of the global `-config`) and
  requires exactly one path; `-h` prints its usage and exits 0.
- It registers SIGINT and SIGTERM like the service, loads the configuration with `config.LoadBackend`, logs
  JSON lines to stderr at `log.level` (an `info` bootstrap logger before the configuration is loaded), opens
  the store with `store.Open` (a second process next to the service, 0064, 0065), and runs `importer.Run`
  with the parsers of `importParsers()` (0070). Progress is logged with `slog`: one line per file finished
  (path, outcome, source type, reason, lines, records, skipped), one `hashing` line per
  `importer.DefaultProgressBytes` (64 MiB) of a file hashed in the scan (path, bytes), one when the scan is
  done (files, pending), one per `importer.ProgressLines` lines of a large file. Names are attributes, never
  part of the message. The JSON handler alone does not make them safe for a terminal: it escapes `"`, `\`,
  characters below U+0020 and U+2028/U+2029, but writes C1 controls (U+009B is a CSI that terminals act on),
  DEL and format characters such as U+202E raw. Every attribute derived from the input or the command line —
  `path`, `reason` and the text of a run-level error — is therefore logged as `strconv.Quote(value)`, which
  escapes every rune of categories Cc, Cf, Zl and Zp and invalid UTF-8; the other attributes are fixed
  texts, validated source types or numbers.
- The summary is written to stdout as text: counts per outcome, lines read, records stored, lines skipped,
  the time range of the stored records in RFC 3339 UTC (or that none were stored), whether the run was
  interrupted, then the files not recognized and the files that failed, each with its reason, and the files
  with skipped lines with their first problems. Every path and reason is quoted with `%q`.
- Exit codes: 0 when every file was imported, already imported or not recognized; 1 when a file failed, the
  run was interrupted or stopped by an error (the summary is still printed), or the configuration, the
  database or the root could not be opened (an error line on stderr); 2 for a usage error.

## Consequences

- `docker exec -it vandoxd /vandoxd import /import/<name>` imports from the mounted directory; the README
  documents it. `-it` is needed for Ctrl-C to reach the import (without a terminal, `docker exec` forwards no
  signal, and closing the client leaves the import running); stopping the container kills the import, which
  loses nothing committed and resumes on the next run (0069, 0071). The import runs as the container's user
  65532, so the README tells the operator to grant read access to that user only (`chown -R 65532:65532`
  plus `chmod -R u+rX`, or `setfacl -R -m u:65532:rX`, on `import/<name>`) — a copied `/var/log` holds
  `0640 root:adm` files such as `auth.log` and `mail.log`, which must not become world-readable (0071). The
  import shares the container's memory limit with the service,
  which the bounded batches of 0071 allow for.
- Scripts can rely on stdout holding only the summary and on exit code 1 for any failed file.
- The web UI (later issue) calls `importer.Run` and renders `importer.Summary` itself; the text format is the
  command's, not an API.
- A future sub-command follows the same pattern: dispatch in `run`, its own flag set, `-version` still wins.
