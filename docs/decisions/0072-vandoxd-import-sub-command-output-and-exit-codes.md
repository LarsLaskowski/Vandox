# 0072: vandoxd gets the sub-command import; progress as JSON on stderr, the summary as text on stdout, exit code 1 when a file failed

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Log import
- **Source:** Issue #15
- **Supersedes:** —

## Context

Both entry points delegate to a testable `run` function behind a one-statement `main()`: the coverage gate (0001)
needs 80 % line coverage on new code, and the old `main()` used the global `flag.CommandLine` with
`flag.ExitOnError` and printed to `os.Stdout`, so no unit test could call it. Issue #13 gave `vandoxd` a real
default action: it runs as a service without arguments, with its own flag set (`-config`, `-healthcheck`,
`-version`), JSON logging with `slog`, a 10 s shutdown deadline, and exit code 2 for a flag error or a
positional argument. Issue #15 requires the trigger "CLI sub-command (`vandoxd import <path>`)", which makes one
positional argument meaningful. The image's
`ENTRYPOINT` is `["/vandoxd"]`, the distroless image has no shell, and the compose file mounts the import
directory at `/import` (0060), so the operator runs `docker exec vandoxd /vandoxd import /import/<name>`;
the process inherits the container's environment (`VANDOX_*` secrets checked by `config.LoadBackend`) and the
configuration file at the default path. Issue #15 asks for progress reporting and a final summary with
files, lines, time range, errors and unrecognized files; the web UI trigger is a later issue, so the result
must exist as data (`importer.Summary`), not only as text.

## Options considered

Entry point (decided with issues #98 and #13):

- **A shared `internal/cli.Run` behind a thin per-binary `run`** — chosen over an unexported `run` copied into
  each `main` package (about 20 duplicated lines that SonarQube Cloud counts as duplicated new code), over
  calling `cli.Run` straight from `main()` (the `main` packages then hold only uncovered lines), over covering
  `main()` by re-executing the test binary (needs `GOCOVERDIR` and a second coverage format for two lines of
  wiring) and over accepting the gap again (0001).
- **No arguments run the service**, not a `serve` sub-command with `CMD ["serve"]` (any argument override such
  as `--version` replaces the `CMD`, and a NAS UI that edits the command line breaks it easily; printing the
  usage is of no use in a container), with an own flag set rather than a hook for extra flags in `cli.Run`.
- **JSON log handler** rather than text (unambiguous, escapes control characters in every value, filterable in
  `docker logs` and NAS log viewers), and **an own shutdown deadline** below the compose `stop_grace_period`
  rather than Docker's default stop timeout alone (a hanging request would end in SIGKILL and an unclean
  database close).

Import (issue #15):

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

Entry point: `vandoxd` without arguments runs the service, with its own flag set (`-config`, `-healthcheck`,
`-version`; `-version` wins) and exit codes 0, 1 and 2, behind a thin function that tests can call. This part
moves to the ingest and backend host area when that area is written.

Import: the first positional argument `import` starts the sub-command (option 1). Progress goes as JSON lines to
standard error through the service's log handler and the summary as text to standard output (option 2). The exit
code is 1 whenever a file failed or the run was interrupted, while "not recognized" alone stays 0 (option 3).
Input-derived values are quoted before they are logged, because the JSON handler writes some control and format
characters raw. The command, its output and its exit codes are described in [Log import](../areas/log-import.md),
*Command* and *Result*.

## Consequences

- Scripts can rely on stdout holding only the summary and on exit code 1 for any failed file.
- The web UI (a later issue) runs the import itself and renders the summary on its own; the text format is the
  command's, not an API.
- `docker run networlddev/vandox` starts the service and `--version` still works for the release checks; without a
  configuration file `vandoxd` fails with a configuration error instead of printing the usage.
- The 10 s shutdown deadline and the 2 s health ping timeout are constants; making them configurable is a new
  option under 0049. The compose file's `stop_grace_period` must stay above 10 s (0060).
- A future sub-command follows the same pattern: dispatch in the entry point, its own flag set, `-version` still wins.
