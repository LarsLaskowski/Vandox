# Stack: Go agent and .NET backend

The toolchain and the exact commands of this repository, which has two languages: the agent
(`vandox-agent`) is Go, the backend (`vandoxd`) is .NET 10 with Blazor (record 0073). Every squad member,
skill and instruction file refers to the entries below by their *italic name*; each command covers both
languages. Seeded from the Squad-Spec-Repository-Template `go` profile, extended for .NET and owned by this
repository (record 0074): keep it true when the build changes.

## Toolchain

- Go `1.27` (module `github.com/LarsLaskowski/Vandox`; agent only); `go.mod` names the minor version without a
  patch, so `setup-go` uses the newest 1.27.x available on the runner. `gofmt` as the formatter, `go vet` and
  **golangci-lint** `v2.14.0` (`.golangci.yml`) as the analyzers, `govulncheck` (a `tool` dependency in
  `go.mod`) for known vulnerabilities.
- .NET SDK `10.0` (`global.json`, `rollForward: latestFeature`), solution `Vandox.slnx`, central package
  management (`Directory.Packages.props`), shared build settings in `Directory.Build.props`. Analyzers:
  `Reihitsu.Analyzer` and `SonarAnalyzer.CSharp`, every diagnostic an error (`TreatWarningsAsErrors`). Formatter:
  `reihitsu-format` (local tool, `dotnet-tools.json`). Tests: MSTest 4 with coverlet (`coverlet.runsettings`).
- The SessionStart hook `.claude/hooks/session-start.sh` runs the hook of each profile in remote
  sessions: `session-start-go.sh` (`go mod download`, the pinned golangci-lint) and `session-start-dotnet.sh`
  (installs `reihitsu-format` globally if missing, unpinned, and restores the solution). Run `dotnet tool restore`
  to get the formatter version pinned in `dotnet-tools.json`.

## Layout

- Go (agent): `cmd/vandox-agent`, `internal/` (model, wire, config, cli, version); tests colocated as
  `foo_test.go` next to `foo.go`.
- .NET (backend): `src/Vandox.Core` (model, wire decoder, configuration, safe file access, log parsing),
  `src/Vandox.Storage`, `src/Vandox.Import`, `src/Vandox.Backend` (host, CLI, Blazor; assembly `vandoxd`).
  `tools/Vandox.StorageBenchmark` (console program for the storage write measurement, `docs/BENCHMARKS.md`). Tests in `tests/<Project>.Tests`, mirroring the namespace and file of the class under test
  (`Foo.cs` → `FooTests.cs`).
- Shared by both: `testdata/` (including the golden wire batch `testdata/wire/all-kinds.jsonl`, record 0075).

## Commands

| Name | Command |
| ---- | ------- |
| *Restore* | `go mod download && dotnet tool restore && dotnet restore Vandox.slnx` |
| *Format* (Code Officer only in the squad) | `gofmt -w . && reihitsu-format src tests tools` (or `dotnet tool run reihitsu-format src tests tools`) |
| *Format check* | `test -z "$(gofmt -l .)" && reihitsu-format --check src tests tools` |
| *Build* | `go build ./... && dotnet build Vandox.slnx` |
| *Test* | `go test ./... -race && dotnet test Vandox.slnx` |
| *Single test* | Go: `go test ./<package> -run '^TestName$'`; .NET: `dotnet test tests/<Project>.Tests --filter <ClassOrMethodName>` |
| *Test with coverage* | `rm -rf TestResults coverage.out && go test ./... -race -coverprofile=coverage.out && python3 .squad/tools/go-coverage-to-cobertura.py && dotnet test Vandox.slnx --settings coverlet.runsettings --results-directory TestResults` (clears the results first) |
| *Coverage gate* | `python3 .squad/tools/coverage-check.py` |
| *Analyzer gate* | `python3 .squad/tools/analyzer-check.py` |

`reihitsu-format` needs `DOTNET_ROOT` when it runs as a global tool outside the SDK's directory (for example
`DOTNET_ROOT=/usr/lib/dotnet`).

## Analyzer gate

`analyzer-check.py` runs `analyzer-check-go.py` and `analyzer-check-dotnet.py`; each must pass, and both also
run `shellcheck` on changed shell scripts when it is installed. The .NET part needs *Restore* first.

1. `go vet ./...` for the whole module.
2. `golangci-lint run --new-from-merge-base=origin/main --whole-files ./...`, which reports every issue
   anywhere in a Go file changed since the merge base with `origin/main`. `gocognit` (threshold 15,
   `.golangci.yml`) stands in for SonarQube Cloud's cognitive-complexity rule `go:S3776`. The two measures are
   close but not identical: in every case measured (PR #110, `main`) gocognit flagged at least what
   SonarQube flagged, and it also counts an `if` on the bare `err != nil` check, which SonarQube does not, so
   it can flag a function SonarQube accepts. Such a finding is fixed like any other diagnostic; a new gocognit
   exclusion — a rule in `.golangci.yml` or a `//nolint:gocognit` directive — needs a Lead decision record (two
   existing validators are excluded by name, without a complexity cap, record 0047).
3. `analyzer-check-dotnet.py`: a Release build of `Vandox.slnx` (`--no-restore --no-incremental`) with a SARIF log
   per project. Reihitsu and SonarAnalyzer run inside the build with every rule, info level included, and every
   diagnostic of any severity in a changed C# file fails the gate (diagnostics in unchanged files are listed, not gating).
   Fixable style findings are the Code Officer's; findings that need a code change go to the Dev or Tester.
   The `.editorconfig` raises the diagnostics that SonarQube Cloud lists but the compiler reports only at info level
   (`MSTEST0037`, `MSTEST0068`, `ASP0015`, `SYSLIB1092`, `IDE0028`) to errors; add an id there when SonarQube reports a
   new one that the build did not.
   Never trust a test result after a build that failed on analyzer errors: the test run uses the stale DLLs.

Other SonarQube Cloud findings (further rules, duplication, hotspots) have no local equivalent and arrive in
squad step 11.

Changed shell scripts (`*.sh`) are checked with `shellcheck` when it is installed; the script says so when it
skips them. Without it, SonarQube Cloud's shell rules (`shelldre:*`) only report in squad step 11.

Two analyzer rules collide and are settled once (record 0074): RH3001 forbids the negation operator `!`,
S1125 forbids comparing a boolean with a literal (`== false`, `is false`). Write positive conditions, early
returns or a small `Require`-style helper; never a workaround such as `== default(bool)`.
IDE0046 (prefer a conditional expression over an `if` that returns) collides with S3358 (no nested ternaries)
and stays silent (record 0087): an `if` with an early return is the accepted form.

## Coverage gate

`coverage-check.py` merges every `TestResults/**/coverage.cobertura.xml`: coverlet's report of each .NET test
project and `TestResults/go/coverage.cobertura.xml`, which `go-coverage-to-cobertura.py` writes from
`coverage.out`. Production code is every `*.go`, `*.cs` and `*.razor` file outside `tests/` and `*_test.go`
(`.squad/tools/squad_settings.py`). The thresholds are 80 % on new/changed code and 80 % overall.

## Writing code

Go (agent):
- `gofmt` formatting; package names short and lower-case; exported identifiers documented with a comment
  that starts with the identifier's name.
- Errors are returned and wrapped with context (`fmt.Errorf("…: %w", err)`), never ignored; no `panic` in
  library code.
- `context.Context` is the first parameter of anything that does I/O or can block, and is honored.
- The agent prints its build information through `internal/version`, set via `-ldflags`.

C# (backend): follow the surrounding code and the analyzers; in particular
- file-scoped namespaces, `#region` sections and XML documentation on every member as in the existing files
  (Reihitsu enforces the layout; run *Format* before building);
- nullable reference types on, `CancellationToken` as the last parameter of anything that does I/O or can
  block, and honored;
- logging through `[LoggerMessage]` methods (`BackendLog`), never string interpolation in a log call;
- exceptions carry context in their message and are never swallowed; `Program.Main` and similar wiring stay
  thin and hold no logic (record 0074);
- the backend prints its build information from the assembly metadata set by `-p:VandoxVersion`,
  `-p:VandoxCommit` and `-p:VandoxDate`.

## Writing tests

See `docs/UNIT_TESTS.md`. Go: the standard `testing` package, table-driven tests with `t.Run`, `t.Helper()` in
helpers, `t.TempDir()` for files, failure messages that state got and want. .NET: MSTest, one test class per
class under test, Arrange/Act/Assert comments, helper classes for temporary directories and fakes.

Pitfalls of leak and error-text tests (Go): never put a leak sentinel into a subtest name or any other name
that becomes a path (a `t.TempDir()` directory) when the error under test prints that path — strip the path
from the error text before the leak check. Where the plan fixes the error format, compare the exact text
instead of forbidding substrings, so a forbidden substring never overlaps required text (`yaml:` against the
required prefix `config: test.yaml:`). A claim of bounded memory gets a heap-bound test (`runtime.MemStats`
or `testing.AllocsPerRun`) with the smallest input that still detects the failure.

## Skeleton

Go: new functions and methods with their full signature and doc comment, bodies
return a zero value or an error (`errors.New("not implemented")`) where the signature allows it, and
`panic("not implemented")` only where it does not (a `panic` aborts the whole test binary), so the module
builds and the tests compile and fail. C#: new types and members with their full signature and XML
documentation, bodies `throw new NotImplementedException();`, so the solution builds and the tests compile and fail.

## Dependencies

Go: `go get <module>@<version>`, then `go mod tidy`; commit `go.mod` and `go.sum` together. .NET: add the
package version to `Directory.Packages.props` and a `PackageReference` without a version to the project; the
restore must stay reproducible. A new dependency in either language is a `security`-tier change.

## Concurrency

Builds and test runs share the Go build cache safely, but run one squad member that builds or tests at a
time so results are attributable. Two `dotnet build` or `dotnet test` runs at once on the same solution
collide on `obj/` and `bin/`.

## Known pitfalls

- `golangci-lint` must be on the PATH; the version in CI (`.github/workflows/ci.yml`) is the reference.
  It refuses to run ("the Go language version used to build golangci-lint is lower than the targeted Go
  version") when it was built with an older Go than `go.mod` targets. A plain
  `go install github.com/golangci/golangci-lint/v2/cmd/golangci-lint@<version>` does not avoid this: with
  `GOTOOLCHAIN=auto` it builds with the Go version from golangci-lint's own `go.mod`, which can be older
  than this module's. Use the binary from the golangci-lint GitHub release, or force a toolchain at least
  as new as `go.mod` (`GOTOOLCHAIN=go1.27.<n> go install ...@<version>`); `golangci-lint version` shows
  the Go it was built with. The session-start hook does this for remote sessions, using the version pinned
  in `.github/workflows/ci.yml`.
- With `GOTOOLCHAIN=auto`, a local Go older than `go.mod` switches to the `.0` release of that minor
  version (`go 1.27` -> `go1.27.0`). `go tool govulncheck ./...` can then report standard-library
  vulnerabilities that the current patch, the one CI uses, already fixes. Select the current patch
  (`GOTOOLCHAIN=go1.27.<n>`) before trusting a local scan.
