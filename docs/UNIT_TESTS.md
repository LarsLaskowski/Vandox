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

## Deploy scripts (Go tests of shell scripts)

`deploy/agent` is a test-only Go package: its `_test.go` files test the shell scripts `common.sh`, `install.sh`
and `uninstall.sh` and the files they ship (`vandox-agent.service`, `agent.yaml`, `mariadb-user.sql`,
`mariadb-user-remove.sql`), and it holds no production Go code.

- **Harness.** `common_test.go` starts `bash`, sources the scripts and calls one function at a time
  (`parse_args`, `read_spool_directory`, `verify_checksum`, ...), then compares exit status, standard output and
  standard error exactly. Every external command (`curl`, `plesk`, `mariadb`, `systemctl`, `getent`, `useradd`,
  `setpriv`, ...) is a stub script on `PATH` that records its arguments and environment; `PATH` holds the stubs and
  a short list of real tools only, so a function that calls an unstubbed command fails. Nothing needs root, the
  network or systemd, and each bash run has a deadline.
- **Fixtures.** Bytes that an editor or formatter could change (a BOM, a tab, a CR, the ASCII escape, a
  backslash) are built from byte values in the test, never typed into the source. A stub that copies a real
  tool's output (`getent`, `id`, `passwd -S`) names its measurement in the test.
- **Install bundle and entry check.** The scripts ship in a release bundle, so the tests also cover its rules:
  `stat` is a stub keyed by path (`statTable`), which lets a test give any directory or file an owner and a mode
  without root. `TestEntryCheck_RefusesBeforeSourcing` runs a copy of `install.sh` as a program beside a stub
  `common.sh` that prints `sourced`, so a refusal is visible as the missing word; `TestCheckBundleDir_*` and
  `TestReadBundleVersion_*` call the functions directly; `TestEntryCheck_IsTheSameInBothScripts` compares the
  marked block of both scripts; `TestInstallDocuments_UseTheReleaseBundle` reads the documented install chain.
- **Not unit-testable.** Systemd behavior (enabling and starting the unit, `Type=notify`, the restart loop, the
  address allow-list, `CapEff`, the seccomp filter, `systemd-analyze security`), the real user and file
  ownership changes and a real download need a systemd 249 host as root. The CI job `agent-install` runs
  `.github/scripts/check-agent-install.sh` for them: install, repeat install, uninstall, reinstall and purge on the
  runner, with exact message lines asserted. The Go tests are the local substitute, that script the authority.

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
  Both decoders also run the shared decoder cases in `testdata/wire/decoder-cases.json` (Go
  `TestDecoder_SharedCases`, .NET `WireContractTests.DecodeSharedCaseGivesExpectedResult`); a new JSON-level
  decoding rule (key matching, duplicate keys, nesting, header shape) gets a case there, so both decoders are
  proven to read a line alike.
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
  A unix datagram socket bound in `t.TempDir()` (or an abstract name) stands in for the systemd notification socket,
  and a `net.Pipe` or a unix stream socket in `t.TempDir()` stands in for the system bus (`internal/sdbus`); both are
  local IPC, not network, and tests on them with real timers follow decision 0062 (below).
- Go: timeouts of `context` and `net/http` cannot be driven by an own fake clock, so
  [decision 0062](decisions/0062-timeout-tests-use-synctest-or-injected-durations.md) applies: without a
  network, test them in a `testing/synctest` bubble (fake time, `synctest.Wait`); over loopback listeners,
  inject short durations (where the timeout must fire) or a long one (where it must not), synchronize on
  channels and recorded events, never `time.Sleep`, and assert no elapsed time.
- One narrow exception to "no real clock", in both languages: a test that guards against a quadratic cost may
  compare two durations measured in the same test, never an absolute duration. It warms both sides up once,
  takes the best of three runs of each, and bounds the ratio of the code under test to a plain reference (Go
  `json.Unmarshal`, .NET `JsonDocument.Parse` of the same bytes) by at least three times the highest ratio
  measured for the correct code, instrumentation (race detector, coverage) included, so a loaded CI host does
  not make it flaky ([0090](decisions/0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md)).
  Where no plain reference fits, the same rules bound the ratio of the code under test on a multiple of the base
  input (for example twice the base input) to the same code on the base input.

## Code coverage

**Threshold: at least 80 % line coverage on new or changed production code, and at least 80 % overall** —
the same measure as SonarQube's "coverage on new code". Both values are set in `.squad/tools/squad_settings.py`
(`COVERAGE_THRESHOLD`, `COVERAGE_OVERALL_THRESHOLD`); the overall one may start lower in a repository adopted
with a coverage debt and is only ever raised. Check it locally before a push with *Test with coverage* and the
*Coverage gate* from [`.squad/stack.md`](../.squad/stack.md). Lines that genuinely
cannot be covered by a unit test (for example `main` wiring) need an explicit, recorded decision. The
measure covers Go and C# together (`*.go`, `*.cs`, `*.razor`; `.squad/tools/squad_settings.py`). The own
browser scripts (`src/Vandox.Backend/wwwroot/js/`) have no unit tests, as the project has no Node build;
SonarQube Cloud analyzes them but excludes them from its coverage measure (`sonar.coverage.exclusions`,
[0098](decisions/0098-web-ui-serves-only-its-own-static-web-assets.md)). In C#,
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
- [ ] Go: passes with `-race`; no real network or clock except the ratio of two durations (see Structure; timeouts: `synctest` or injected durations, decision 0062). .NET: no real network, clock or sleep, again except such a ratio; the whole solution builds without any analyzer diagnostic.
- [ ] *Format* from `.squad/stack.md` run before committing.
