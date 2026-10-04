# Plan: Unit tests for the scaffold so the overall coverage gate passes on main

Source: Issue #98
Status: Draft
Tier: security — the change rewires how both binaries parse their command-line arguments (a private
`flag.FlagSet` with `ContinueOnError` replaces the global `flag.CommandLine` with `ExitOnError`), and CLI
arguments are listed under security area 10 (*Parsing of external input*) in `.squad/project.md`. The risk
is low, but when in doubt the higher tier applies.

## Problem / root cause

The *Coverage gate* fails on `main` because no Go file in the repository has a test (`git ls-files` lists
no `_test.go`), and the only logic of both binaries sits in `main()`, which a unit test cannot call (it
uses the global `flag.CommandLine`, writes to `os.Stdout` and its parse errors end the process through
`flag.ExitOnError`):

- `cmd/vandox-agent/main.go:11-20` and `cmd/vandoxd/main.go:11-20`: `main()` defines `-version` on the
  global flag set, calls `flag.Parse()`, prints `version.String(...)` with `fmt.Println` and otherwise
  calls `flag.Usage()`.
- `internal/version/version.go:16-18`: `String` is untested.

Claims of the issue, checked against the code:

- "`coverage-check.py` reports 0/21 lines overall on main" — **confirmed**. With *Test with coverage* on
  `55a4426` the profile has 7 blocks, all with count 0; the gate counts each block's line range: 3 lines
  in `version.go` (16–18) plus 9 lines in each `main.go` (11–19) = 21.
- "`cmd/vandox-agent/main.go`, `cmd/vandoxd/main.go` and `internal/version/version.go` have no tests" —
  **confirmed** (no `_test.go` file exists anywhere).
- "Add table-driven unit tests for `internal/version` and move testable logic out of both main functions
  so that overall coverage reaches at least 80 %" — **confirmed as necessary**: tests for
  `internal/version` alone reach only 3/21 = 14 %; the `main` logic must move behind a testable function.
- "Accepted temporarily for issue #7 in decision record 0033" — **confirmed**: 0033 accepts the red
  overall gate for the issue #7 change only and names this follow-up issue. 0033 is not superseded by this
  change (it was scoped to #7 and stays true); 0034 refers to it.

Current behavior, observed with `go run` and preserved by this change (except the usage header, see
*Approach*):

| Invocation | stdout | stderr | exit |
| ---------- | ------ | ------ | ---- |
| `-version` / `--version` | `<name> dev (commit unknown, built unknown)` | — | 0 |
| no arguments, or `-version=false` | — | `Usage of <argv0>:` and the flag list | 0 |
| `-h` / `-help` / `--help` | — | usage | 0 (`flag.ExitOnError` exits 0 on `flag.ErrHelp`) |
| unknown flag, e.g. `-bogus` | — | `flag provided but not defined: -bogus` and usage | 2 |
| positional argument, e.g. `extra` | — | usage | 0 |

Related observation (not a defect of this change, preserved deliberately): a positional argument is
silently ignored and a call without arguments prints the usage and exits 0. This is the scaffold's
placeholder until a binary gets a real default action; changing it is a user-visible behavior change and
out of scope for a test-only issue (see *Out of scope*).

## Acceptance criteria

`internal/version` (`internal/version/version_test.go`):

- [ ] AC1: `version.String(name)` with the package defaults (`Version="dev"`, `Commit="unknown"`,
  `Date="unknown"`) returns exactly `"<name> dev (commit unknown, built unknown)"`, e.g. for
  `"vandox-agent"` and `"vandoxd"`.
- [ ] AC2: with `Version`, `Commit` and `Date` set as `-ldflags -X` would set them (e.g. `v0.1.0`,
  `abc1234`, `2026-10-04T00:00:00Z`), `String("vandoxd")` returns
  `"vandoxd v0.1.0 (commit abc1234, built 2026-10-04T00:00:00Z)"`. Table-driven (defaults, all set, an
  empty name); the test restores the three variables with `t.Cleanup` and does not use `t.Parallel`.

`internal/cli` (`internal/cli/cli_test.go`), table-driven over the invocations, each case asserting the
exit code, stdout and stderr (got and want in the failure message):

- [ ] AC3: `Run(name, []string{"-version"}, …)` and `Run(name, []string{"--version"}, …)` return 0, write
  exactly `version.String(name) + "\n"` to stdout and nothing to stderr.
- [ ] AC4: `Run(name, nil, …)`, `Run(name, []string{}, …)` and `Run(name, []string{"-version=false"}, …)`
  return 0, write nothing to stdout, and write a usage to stderr that contains `"Usage of <name>:"` and
  `"-version"`.
- [ ] AC5: `-h`, `-help` and `--help` return 0, write nothing to stdout and the usage (as in AC4) to
  stderr.
- [ ] AC6: an undefined flag (`-bogus`) returns 2, writes nothing to stdout, and writes
  `"flag provided but not defined: -bogus"` and the usage to stderr.
- [ ] AC7: when writing the version line to stdout fails (a writer whose `Write` returns an error),
  `Run(name, []string{"-version"}, …)` returns 1.
- [ ] AC8: `Run` does not use the global `flag.CommandLine`: calling it several times in one test process
  (the table above) neither panics with "flag redefined" nor carries a parsed value from one call into the
  next (a `-version` case followed by a no-argument case still prints the usage).

Binaries (`cmd/vandox-agent/main_test.go`, `cmd/vandoxd/main_test.go`):

- [ ] AC9: `run([]string{"-version"}, &stdout, &stderr)` in `cmd/vandox-agent` returns 0 and writes
  exactly `version.String("vandox-agent") + "\n"`; in `cmd/vandoxd` exactly
  `version.String("vandoxd") + "\n"` — pinning that each binary passes its own name.
- [ ] AC10: `run([]string{"-bogus"}, …)` in each binary returns 2 and its stderr contains
  `"Usage of vandox-agent:"` resp. `"Usage of vandoxd:"`.

Gates:

- [ ] AC11: *Test with coverage* then *Coverage gate* pass: new/changed code ≥ 80 % and overall ≥ 80 %.
  Expected (measured on a prototype of this design): overall ≈ 80.6 % (25/31 lines), new/changed
  ≈ 83 %; the only uncovered lines are the bodies of the two `main()` functions, accepted in 0034.
- [ ] AC12: the built binaries behave as in the table under *Problem*, except that the usage header names
  the binary (`Usage of vandox-agent:`) instead of the invoked path (Dev checks with `go run` in step 6).

## Approach

1. New package `internal/cli` with `Run`, which owns the flag handling both binaries share today: a
   private `flag.NewFlagSet(name, flag.ContinueOnError)` whose output goes to `stderr`, the `-version`
   flag (same help text as today), and the mapping to exit codes — `flag.ErrHelp` → 0, any other parse
   error → 2 (the codes `flag.ExitOnError` uses today), version line written to `stdout` with
   `fmt.Fprintln` and its error checked (→ 1), otherwise `flags.Usage()` and 0. The usage, printed by the
   flag set's default `Usage`, now starts with `Usage of <name>:` instead of `Usage of <argv0>:`.
2. Each `main.go` keeps a package constant with its binary name, a one-statement `main()` —
   `os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))` — and an unexported `run` that calls
   `cli.Run(binaryName, args, stdout, stderr)`. `run` is the seam later binary-specific wiring grows
   into and is what each binary's test calls.
3. Why a shared package and not the logic copied into both `main` packages: an identical ~20-line `run`
   in both binaries is a duplicated block that SonarQube Cloud (the CI system of record, 0001) would
   count as duplicated new code; why the thin per-binary `run`: without it the `main` packages hold only
   uncovered lines and the overall gate stays red (≈ 76 %). Both reasons and the uncovered `main()` lines
   are recorded in 0034.
4. Tests as listed under *Test files*; doc updates as listed below.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `internal/cli` | `internal/cli/cli.go` (new) | package `cli` with `Run` |
| `cmd/vandox-agent` | `cmd/vandox-agent/main.go` | `binaryName` constant, `main` reduced to `os.Exit(run(...))`, new `run` |
| `cmd/vandoxd` | `cmd/vandoxd/main.go` | same as the agent |
| `internal/version` | `internal/version/version.go` | unchanged |
| docs | `docs/ARCHITECTURE.md`, `docs/UNIT_TESTS.md` | see *Documentation updates* |

## Signatures (for the Dev's skeleton)

`internal/cli/cli.go` (new):

```go
// Package cli holds the command-line handling shared by vandox-agent and vandoxd.
package cli

// Run parses args (the command-line arguments without the program name) for the binary called name,
// writes its regular output to stdout and usage and parse errors to stderr, and returns the process
// exit code: 0 on success, on -h/-help and when only the usage is printed; 1 when writing the output
// fails; 2 when the arguments cannot be parsed.
func Run(name string, args []string, stdout, stderr io.Writer) int
```

`cmd/vandox-agent/main.go` (and `cmd/vandoxd/main.go` with `"vandoxd"`):

```go
// binaryName is the name the binary reports in its version line and usage.
const binaryName = "vandox-agent"

func main() // body: os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))

// run executes the command with args and returns the process exit code.
func run(args []string, stdout, stderr io.Writer) int // body: return cli.Run(binaryName, args, stdout, stderr)
```

`internal/version`: none (unchanged). Skeleton: `cli.Run` with body `panic("not implemented")`; `main`
and both `run` functions already in their final form (one statement each), so the module builds and the
tests compile and fail.

## Test files

- `internal/version/version_test.go` — package `version_test` (exercises only the exported API; sets
  the exported variables and restores them with `t.Cleanup`).
- `internal/cli/cli_test.go` — package `cli_test` (exported `Run` only); the failing writer for AC7 is a
  small type local to this file.
- `cmd/vandox-agent/main_test.go` — package `main` (white-box, `run` is unexported).
- `cmd/vandoxd/main_test.go` — package `main` (white-box).

Existing test code that calls a changed signature: none (no test exists).

## Documentation updates

Made by the Dev:

- `docs/ARCHITECTURE.md`, *Components*, the `internal/` bullet: add "command-line handling" to the list
  of shared packages ("… log parsing, signatures, version information, command-line handling.").
- `docs/UNIT_TESTS.md`, *Code coverage*: after the sentence on lines that cannot be covered, add that a
  binary's `main` only calls `os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))`, that `run` is tested in
  `main_test.go`, and that the `main` body is the accepted uncovered wiring (link record 0034).
- `README.md`: none (`--version` behaves as documented; the build instructions are unchanged).

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` / `.squad/project.md` is touched: no collector, spool, backfill
or alerting code exists or changes. The integration surface for a new module (`.squad/project.md`) is
covered: exported interface `cli.Run` in `internal/cli`, wired up in both `cmd/` packages, no test double
for callers needed (callers test through `run` with buffers), component list in `docs/ARCHITECTURE.md`
updated.

## Security considerations

- Area 10 (parsing of external input — CLI arguments): parsing stays with the standard library's `flag`
  package; no new flag, no new input. Malformed arguments yield exit code 2 and a message, never a panic
  (AC6); `ContinueOnError` must not turn a parse error into a silent success (AC6 pins exit 2).
- The version line contains only build metadata set at link time; no secret is read, printed or logged
  (area 8 unaffected).
- No dependency added (standard library only).

## Decision records

- `docs/decisions/0034-entry-points-delegate-to-a-testable-run-function.md` (Proposed) — shared
  `internal/cli.Run` behind a thin per-binary `run`; the `main()` bodies are accepted as uncovered wiring;
  exit-code contract; the usage header now names the binary.

## Out of scope / follow-ups

- Changing what a call without arguments or with positional arguments does (today: usage, exit 0). That
  is user-visible behavior and belongs to the first feature that gives a binary a real default action; no
  follow-up issue, as that feature replaces the placeholder anyway.
- Covering `main()` itself (e.g. re-executing the test binary in a subprocess): rejected in 0034.
