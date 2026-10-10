# Unit Tests

This document describes how unit tests are written in this repository. It is binding for human
contributors and AI agents alike: **unit tests are mandatory for newly written code**, new tests follow the
conventions below, and existing tests are the reference implementation — when in doubt, look at a
neighboring test file before inventing a new pattern.

The repository has two languages (the Go agent, the .NET backend); each section below says which language it
covers, and the rules under *Code coverage* and the checklist apply to both.

## Test stack

- Go (agent): the standard `testing` package (plus `net/http/httptest` for handlers). No assertion or mocking
  library — hand-written fakes behind small interfaces, listed in `.squad/project.md` (*Test doubles*).
- .NET (backend): MSTest 4 (`[TestClass]`, `[TestMethod]`, `[DataRow]`) with the `MSTest` assertions, coverlet for
  coverage (`coverlet.runsettings`) and `Microsoft.Extensions.TimeProvider.Testing` for the clock. No mocking
  library either: hand-written fakes behind small interfaces (*Test doubles*).

## Where tests live

- .NET: one test project per production project under `tests/` (`src/Vandox.Core` → `tests/Vandox.Core.Tests`),
  one test class per class under test (`Foo.cs` → `FooTests.cs`) in the namespace `<Project>.Tests`. Internal
  members are tested through `InternalsVisibleTo`. Shared helpers (`TempDirectory`, fakes, builders) are
  `internal` classes of the test project.
- Go: colocated `_test.go` files, one per source file under test (`foo.go` → `foo_test.go`), in the same
  package (white-box) unless the test exercises only the exported API (`package foo_test`).

## Naming

- .NET: `<TypeUnderTest><Behavior>` in plain English, e.g. `PingCheckerSharesRunningPingAndTimesOut`; every
  test method has an XML summary that states the behavior in one sentence. Async tests return `Task`.
- Go: `TestFunction_Scenario` or `TestType_Method` with `t.Run` subtests named after the scenario, e.g.
  `t.Run("empty input returns error", …)`.

## Structure

- .NET: `// Arrange`, `// Act`, `// Assert` comments in every test; several inputs through `[DataRow]` (or
  `[DynamicData]`); assertion messages name what is checked (`Assert.AreEqual(expected, actual, "kinds in
  order")`). Use the test's `TestContext.CancellationToken` for cancellation, `TempDirectory` for files,
  `RepositoryFiles.Path` for fixtures in the repository, `FakeTimeProvider` for time, and
  `Assert.ThrowsExactlyAsync` for expected exceptions. Never `Thread.Sleep` or a real delay to wait for a
  result: await the task or signal the fake.
- The wire format is covered by a contract test: `WireContractTests` decodes the golden batch the Go encoder
  writes (`testdata/wire/all-kinds.jsonl`; [0075](decisions/0075-wire-contract-pinned-by-golden-fixtures.md)).
- Go: table-driven tests for several inputs: a slice of cases, one `t.Run` per case.
- Arrange / Act / Assert inside each case; one act per case.
- Failure messages state what was called, what came back and what was expected:
  `t.Errorf("Parse(%q) = %v, want %v", in, got, want)`.
- Leak tests (a secret must not appear in an error or log): keep the sentinel out of subtest names —
  `t.TempDir()` embeds the sanitized subtest name in the path, and an error that legitimately shows the
  path then contains the sentinel. Strip the file path from the error text before the leak check.
- Where the plan fixes an error format, assert the exact text instead of forbidding substrings; a
  forbidden substring must not overlap text the format requires.
- Go: benchmarks live in the `_test.go` file of the code they measure and never assert a duration; their
  reference-host results go into [`BENCHMARKS.md`](BENCHMARKS.md)
  ([0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md)).
- Go: `t.Helper()` in helpers; `t.TempDir()` for files; no real network, no real clock (inject a clock or a
  fixture path); tests pass under `-race`. Loopback listeners, as `httptest` uses them, are the only network.
- Go: timeouts of `context` and `net/http` cannot be driven by an own fake clock, so
  [decision 0062](decisions/0062-timeout-tests-use-synctest-or-injected-durations.md) applies: without a
  network, test them in a `testing/synctest` bubble (fake time, `synctest.Wait`); over loopback listeners,
  inject short durations (where the timeout must fire) or a long one (where it must not), synchronize on
  channels and recorded events, never `time.Sleep`, and assert no elapsed time.

## Code coverage

**Threshold: at least 80 % line coverage on new or changed production code, and at least 80 % overall** —
the same measure as SonarQube's "coverage on new code". Check it locally before a push with *Test with
coverage* and the *Coverage gate* from [`.squad/stack.md`](../.squad/stack.md). Lines that genuinely
cannot be covered by a unit test (for example `main` wiring) need an explicit, recorded decision. The
measure covers Go and C# together (`*.go`, `*.cs`, `*.razor`; `.squad/tools/squad_settings.py`). In C#,
`Program.cs` only hands the process boundaries (arguments, environment, standard streams, cancellation) to a
testable entry method, and that top-level wiring is the accepted uncovered code
([0074](decisions/0074-two-language-toolchain-and-combined-quality-gates.md)). In Go, a
binary's `main` therefore only calls `os.Exit(run(...))` with the process boundaries (context, arguments,
environment, standard streams and, for `vandoxd`, the listen function) as arguments; `run` is tested in the
package's `main_test.go`, and the `main` body is the accepted uncovered wiring
([decision 0072](decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md), which carries over
the pattern for `vandoxd`).

## Checklist for new tests

- [ ] New production code has accompanying unit tests — mandatory, not optional.
- [ ] The *Analyzer gate* passes.
- [ ] At least 80 % line coverage on new/changed production code and overall (*Coverage gate*).
- [ ] Table-driven where there is more than one input; failure messages with got and want.
- [ ] Go: passes with `-race`; no real network or clock (timeouts: `synctest` or injected durations, decision 0062). .NET: no real network, clock or sleep; the whole solution builds without any analyzer diagnostic.
- [ ] *Format* from `.squad/stack.md` run before committing.
