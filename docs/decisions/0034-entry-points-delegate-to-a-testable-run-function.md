# 0034: Entry points delegate to a testable run function; main stays uncovered wiring

- **Status:** Accepted
- **Date:** 2026-10-04
- **Source:** Issue #98
- **Supersedes:** —

## Context

The coverage gate (0001) requires at least 80 % line coverage on new or changed production code and
overall. On `main` the overall value was 0 of 21 lines: no Go file had a test, and the only logic of both
binaries sat in `main()`, which used the global `flag.CommandLine` with `flag.ExitOnError` and printed to
`os.Stdout`, so no unit test could call it. 0033 accepted that gap for the documentation-only issue #7
change and named issue #98 as the follow-up. `docs/UNIT_TESTS.md` requires an explicit, recorded decision
for lines that genuinely cannot be covered by a unit test, such as `main` wiring.

Both binaries handled their arguments identically (a `-version` flag, otherwise the usage). SonarQube Cloud
is the system of record for the CI quality gate (0001) and counts duplicated blocks in new code.

## Options considered

1. **Move the logic into an unexported `run` in each `main` package** — simplest, about 85 % overall, but
   the same ~20-line function twice is a duplicated block that SonarQube Cloud reports as duplicated new
   code.
2. **Shared `internal/cli.Run`, called directly from each `main()`** — no duplication, but the `main`
   packages then contain only uncovered lines and the local gate stays red (about 76 % overall).
3. **Shared `internal/cli.Run` behind a thin per-binary `run`** — no duplication; each binary's `run` is
   covered by a test that pins the name it reports; only the one-statement `main()` bodies stay
   uncovered (about 82 % overall at the time of the change). `run` is also where binary-specific wiring
   will grow.
4. **Cover `main()` as well by re-executing the test binary in a subprocess** — removes the last
   uncovered lines, but needs `GOCOVERDIR` and merging of a second coverage format that the gate does not
   read, for two lines of wiring.
5. **Accept the gap again** — contradicts the purpose of issue #98 and 0033, which accepted it once only.

## Decision

Option 3.

- `internal/cli.Run(name string, args []string, stdout, stderr io.Writer) int` parses the arguments with
  its own `flag.FlagSet` (`ContinueOnError`, output to `stderr`), never the global `flag.CommandLine`.
  Exit codes: 0 for the version line, `-h`/`-help` and the usage; 2 for a parse error (the codes
  `flag.ExitOnError` used before); 1 when writing the version line to `stdout` fails.
- `cmd/vandox-agent/main.go` and `cmd/vandoxd/main.go` each have a `binaryName` constant, an unexported
  `run(args []string, stdout, stderr io.Writer) int` that calls `cli.Run(binaryName, ...)`, and a `main()`
  whose only statement is `os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))`.
- The `main()` bodies are the accepted uncovered lines: they hold no logic, only the process boundary
  (`os.Args`, `os.Stdout`, `os.Stderr`, `os.Exit`).
- The usage header names the binary (`Usage of vandox-agent:`) instead of the path it was invoked by.

## Consequences

- The overall coverage gate passes on `main` again; 0033's acceptance is no longer needed by any change.
- The margin is small at first: the two `main()` bodies count as six uncovered lines in the local gate
  (it counts each block's full line range) against 33 lines in total (27 of 33 covered, 81.8 %). It grows as covered code is
  added.
- A new binary follows the same pattern: `main` only calls `os.Exit(run(...))`, and `run` is tested in
  `main_test.go`.
- A future flag that only one binary needs either becomes a parameter of `cli.Run` or moves the flag set
  into that binary's `run`; whichever is chosen, `main()` stays a single statement.
- Calls without arguments or with positional arguments still print the usage and exit 0; changing that is
  left to the first feature that gives a binary a real default action.
