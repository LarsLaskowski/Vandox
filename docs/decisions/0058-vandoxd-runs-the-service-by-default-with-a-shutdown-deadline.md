# 0058: vandoxd runs the service without arguments, logs JSON with slog and stops within a 10 s deadline

- **Status:** Proposed
- **Date:** 2026-10-05
- **Source:** Issue #13
- **Supersedes:** 0034

## Context

Record 0034 made both entry points delegate to a testable `run(args, stdout, stderr)` that calls the shared
`internal/cli.Run`: `-version` prints the version, and everything else prints the usage and exits 0. It left
the "real default action" to the first feature that gives a binary one. Issue #13 is that feature for
`vandoxd`: it must run as a service with configuration from `internal/config`, structured logging with
`log/slog`, and a graceful shutdown on SIGTERM with a deadline. The image's `ENTRYPOINT` is `["/vandoxd"]`
without `CMD`, and CI runs `docker run --rm vandox:local --version`.

`main()` must stay uncovered wiring of one statement (0034, `docs/UNIT_TESTS.md`), so everything the
process boundary supplies (context, arguments, environment, output, the way listeners are opened) has to
reach `run` as arguments.

## Options considered

1. **A subcommand (`vandoxd serve`) with `CMD ["serve"]` in the image** — explicit, but `docker run image`
   with any argument override (`--version`) replaces the `CMD`, and a NAS UI that edits the command line
   breaks it easily. No arguments printing the usage is of no use in a container.
2. **No arguments run the service** — what the container needs; `-version` and `-h` keep working;
   a positional argument becomes a usage error.
3. **Keep `cli.Run` and give it a hook for extra flags** — keeps one flag parser for both binaries, but adds
   an abstraction for a single user; the agent's default action (#30) is still unknown.
4. **Log format: text handler or JSON handler** — text is easier to read by eye; JSON is unambiguous, escapes
   control characters in every value and can be filtered in `docker logs`/NAS log viewers.
5. **Shutdown deadline: Docker's default stop timeout (10 s) as the only limit, or an own deadline below
   the compose `stop_grace_period`** — without an own deadline a hanging request ends in SIGKILL and an
   unclean database close.

## Decision

Option 2 with an own flag set (option 3 rejected), the JSON handler (option 4) and an own deadline
(option 5):

- `cmd/vandoxd/main.go`: `main()` is the single statement
  `os.Exit(run(context.Background(), os.Args[1:], os.Environ(), os.Stdout, os.Stderr, (&net.ListenConfig{}).Listen))`.
  `run(ctx, args, environ, stdout, stderr, listen) int` parses its own `flag.FlagSet` (`ContinueOnError`,
  usage header `Usage of vandoxd:`) with `-config` (default `/etc/vandox/vandoxd.yaml`), `-healthcheck` and
  `-version`. Exit codes: 0 for the version line, `-h`/`-help` and a clean shutdown; 1 for a start-up or
  runtime failure (and a failed version write); 2 for a flag error or a positional argument. `-version` wins
  over the other flags.
- Without `-version` or `-healthcheck` (0059), `run` serves: it registers SIGTERM and SIGINT with
  `signal.NotifyContext`, restores the default handling after the first signal (so a second one terminates
  at once), loads the configuration with `config.LoadBackend(path, environ)`, logs JSON lines to stderr with
  `slog` at `log.level` (a bootstrap logger at `info` before the configuration is loaded), opens the store,
  opens the web and ingest listeners through `listen`, and serves until the signal. In-flight requests get
  10 s; after that their connections are closed and the exit code is 1. The store is closed after the
  listeners. Errors are logged as attributes, never concatenated into the message; secrets are never
  logged.
- `vandox-agent` keeps `internal/cli.Run` and 0034's behavior unchanged until #30 gives it a default
  action.

Carried over from 0034 unchanged: each binary has a `binaryName` constant, `main()` holds no logic and is
the accepted uncovered line, and `run` is tested in `main_test.go`.

## Consequences

- `docker run networlddev/vandox` starts the service; `--version` still works for the release checks.
- Running `vandoxd` without a configuration file now fails with a configuration error instead of printing
  the usage.
- The 10 s deadline and the 2 s health ping timeout are constants. Making them configurable is a new option
  under 0049 and a new record.
- The compose file's `stop_grace_period` must stay above 10 s (0060).
- Tests drive shutdown by cancelling `ctx` and once by a real SIGTERM to the test process.
