# Unit Tests

This document describes how unit tests are written in this repository. It is binding for human
contributors and AI agents alike: **unit tests are mandatory for newly written code**, new tests follow the
conventions below, and existing tests are the reference implementation — when in doubt, look at a
neighboring test file before inventing a new pattern.

## Test stack

- The standard `testing` package (plus `net/http/httptest` for handlers). No assertion or mocking
  library — hand-written fakes behind small interfaces, listed in `.squad/project.md` (*Test doubles*).

## Where tests live

- Colocated `_test.go` files, one per source file under test (`foo.go` → `foo_test.go`), in the same
  package (white-box) unless the test exercises only the exported API (`package foo_test`).

## Naming

- `TestFunction_Scenario` or `TestType_Method` with `t.Run` subtests named after the scenario, e.g.
  `t.Run("empty input returns error", …)`.

## Structure

- Table-driven tests for several inputs: a slice of cases, one `t.Run` per case.
- Arrange / Act / Assert inside each case; one act per case.
- Failure messages state what was called, what came back and what was expected:
  `t.Errorf("Parse(%q) = %v, want %v", in, got, want)`.
- Leak tests (a secret must not appear in an error or log): keep the sentinel out of subtest names —
  `t.TempDir()` embeds the sanitized subtest name in the path, and an error that legitimately shows the
  path then contains the sentinel. Strip the file path from the error text before the leak check.
- Where the plan fixes an error format, assert the exact text instead of forbidding substrings; a
  forbidden substring must not overlap text the format requires.
- `t.Helper()` in helpers; `t.TempDir()` for files; no real network, no real clock (inject a clock or a
  fixture path); tests pass under `-race`.

## Code coverage

**Threshold: at least 80 % line coverage on new or changed production code, and at least 80 % overall** —
the same measure as SonarQube's "coverage on new code". Check it locally before a push with *Test with
coverage* and the *Coverage gate* from [`.squad/stack.md`](../.squad/stack.md). Lines that genuinely
cannot be covered by a unit test (for example `main` wiring) need an explicit, recorded decision. A
binary's `main` therefore only calls `os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))`; `run` is tested in
the package's `main_test.go`, and the `main` body is the accepted uncovered wiring
([decision 0034](decisions/0034-entry-points-delegate-to-a-testable-run-function.md)).

## Checklist for new tests

- [ ] New production code has accompanying unit tests — mandatory, not optional.
- [ ] The *Analyzer gate* passes.
- [ ] At least 80 % line coverage on new/changed production code and overall (*Coverage gate*).
- [ ] Table-driven where there is more than one input; failure messages with got and want.
- [ ] Passes with `-race`; no real network or clock.
- [ ] *Format* from `.squad/stack.md` run before committing.
