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
  fixture path); tests pass under `-race`. Loopback listeners, as `httptest` uses them, are the only network.
- Timeouts of `context` and `net/http` cannot be driven by an own fake clock, so
  [decision 0062](decisions/0062-timeout-tests-use-synctest-or-injected-durations.md) applies: without a
  network, test them in a `testing/synctest` bubble (fake time, `synctest.Wait`); over loopback listeners,
  inject short durations (where the timeout must fire) or a long one (where it must not), synchronize on
  channels and recorded events, never `time.Sleep`, and assert no elapsed time.

## Code coverage

**Threshold: at least 80 % line coverage on new or changed production code, and at least 80 % overall** —
the same measure as SonarQube's "coverage on new code". Check it locally before a push with *Test with
coverage* and the *Coverage gate* from [`.squad/stack.md`](../.squad/stack.md). Lines that genuinely
cannot be covered by a unit test (for example `main` wiring) need an explicit, recorded decision. A
binary's `main` therefore only calls `os.Exit(run(...))` with the process boundaries (context, arguments,
environment, standard streams and, for `vandoxd`, the listen function) as arguments; `run` is tested in the
package's `main_test.go`, and the `main` body is the accepted uncovered wiring
([decision 0058](decisions/0058-vandoxd-runs-the-service-by-default-with-a-shutdown-deadline.md), which
supersedes [0034](decisions/0034-entry-points-delegate-to-a-testable-run-function.md) for `vandoxd`).

## Checklist for new tests

- [ ] New production code has accompanying unit tests — mandatory, not optional.
- [ ] The *Analyzer gate* passes.
- [ ] At least 80 % line coverage on new/changed production code and overall (*Coverage gate*).
- [ ] Table-driven where there is more than one input; failure messages with got and want.
- [ ] Passes with `-race`; no real network or clock (timeouts: `synctest` or injected durations, decision 0062).
- [ ] *Format* from `.squad/stack.md` run before committing.
