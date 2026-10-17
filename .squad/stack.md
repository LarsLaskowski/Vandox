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

Cloud session notes (found when a cloud image lacked the .NET SDK):

- Without a .NET SDK on the image, install the one `global.json` asks for (a run used SDK 10.0.401 under
  `/opt/dotnet`) and put it on the `PATH`.
- If clean builds fail with RZ1021 or CS8802 because the Razor build server is flaky, build with
  `UseRazorBuildServer=false UseSharedCompilation=false` (as MSBuild properties).
- If `reihitsu-format` is not on the `PATH`, run it as a local tool:
  `dotnet tool run reihitsu-format --check --force src tests tools`.

## Layout

- Go (agent): `cmd/vandox-agent`, `internal/` (model, wire, config, cli, version, agent, scheduler, selfstats, sdnotify, sysstats); tests colocated as
  `foo_test.go` next to `foo.go`.
- .NET (backend): `src/Vandox.Core` (model, wire decoder, configuration, safe file access, log parsing, log signatures, log templates),
  `src/Vandox.Storage`, `src/Vandox.Import`, `src/Vandox.Backend` (host, CLI, Blazor; assembly `vandoxd`).
  `tools/Vandox.StorageBenchmark` (console program for the storage write measurement, `docs/BENCHMARKS.md`). Tests in `tests/<Project>.Tests`, mirroring the namespace and file of the class under test
  (`Foo.cs` → `FooTests.cs`).
- Shared by both: `testdata/` (including the golden wire batch `testdata/wire/all-kinds.jsonl`, record 0075).

## Commands

| Name | Command |
| ---- | ------- |
| *Restore* | `go mod download && dotnet tool restore && dotnet restore Vandox.slnx` |
| *Format* (in the squad: by the member that changed the files, `.squad/team.md`) | `gofmt -w . && reihitsu-format src tests tools --force` (or `dotnet tool run reihitsu-format src tests tools --force`) |
| *Format check* | `test -z "$(gofmt -l .)" && reihitsu-format --check src tests tools` |
| *Build* | `go build ./... && dotnet build Vandox.slnx` |
| *Test* | `go test ./... -race && dotnet test Vandox.slnx` |
| *Single test* | Go: `go test ./<package> -run '^TestName$'`; .NET: `dotnet test tests/<Project>.Tests --filter <ClassOrMethodName>` |
| *Test (project)* | Go: `go test ./<package>/... -race`; .NET: `dotnet test tests/<Project>.Tests` (the package or test project of the changed code; a `patch` that changes only test code, `.squad/routing.md` *Gates of a patch* — it writes no coverage report, so a `patch` that changes production code runs *Test with coverage* instead) |
| *Test with coverage* | `rm -rf TestResults coverage.out && go test ./... -race -coverprofile=coverage.out && python3 .squad/tools/go-coverage-to-cobertura.py && dotnet test Vandox.slnx --settings coverlet.runsettings --results-directory TestResults` (clears the results first) |
| *Coverage gate* | `python3 .squad/tools/coverage-check.py` |
| *Analyzer gate* | `python3 .squad/tools/analyzer-check.py` |
| *Image build check* | .NET: `docker build -f deploy/backend/Dockerfile .` where Docker runs, otherwise the restore-order reproduction under *Image build check* below (`.squad/routing.md`, *Criteria verified only in CI*); Go: none, the agent builds no image |

`reihitsu-format` needs `DOTNET_ROOT` when it runs as a global tool outside the SDK's directory (for example
`DOTNET_ROOT=/usr/lib/dotnet`), for *Format* and *Format check* alike. Without `--force` it asks for confirmation
when a run covers more than 25 files (the count is every `.cs` file under the given paths outside `bin/` and `obj/`,
not only those that need formatting, so the prompt also fires on a clean tree); in a non-interactive session it
cannot ask, so it formats nothing and exits with code 2. `--force` only skips that prompt: the run still rewrites
just the files that need formatting and leaves every other file untouched.

*Test* and *Test with coverage* build and run the .NET tests in the Debug configuration (no `-c`); CI
(`.github/workflows/ci.yml`) builds and runs them in Release. The *Analyzer gate* builds Release but runs no test,
so a test that fails only in Release shows first in CI on the pull request. Write tests that hold in both
configurations, and check one in Release locally with `-c Release` (for example
`dotnet test tests/<Project>.Tests -c Release --filter <ClassOrMethodName>`); *Writing tests* has the consequence
for allocation bounds.

Test time limits: CI runs `go test` without `-timeout`, so Go's default of 10 minutes per package binary applies,
under `-race` as locally. A package whose tests take more than a minute locally is a warning sign; size the inputs
down (the smallest input that still detects the failure) before it nears the limit. The .NET tests have no limit of
their own beyond GitHub's job timeout.

## Analyzer gate

`analyzer-check.py` runs `analyzer-check-go.py` and `analyzer-check-dotnet.py`; each must pass, and both also
run the lint pass over changed shell scripts, workflows and Dockerfiles described below. The .NET part needs
*Restore* first. `analyzer-check.py --lint-only` runs the lint pass alone, with no build and no Go or .NET
analysis (the gate of a `patch` that touches only scripts, workflows or Dockerfiles).

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
   new one that the build did not. SonarQube Cloud analyses this project with the built-in C# profile *Sonar way
   comprehensive* (376 rules), while the SonarAnalyzer package runs by default only the rules of *Sonar way core*
   (166), so `.editorconfig` switches on every `csharpsquid` rule the comprehensive profile has beyond core (204 ids,
   generated on 2026-10-12 from the two profiles' active rules through `api/qualityprofiles/search` and
   `api/rules/search?qprofile=…&activation=true` of `sonarcloud.io`, organization `networlddev`) as errors — among
   them the cognitive-complexity rule `S3776` (threshold 15, the same as SonarQube Cloud): keep methods below it while
   writing them instead of splitting them after the gate fails. `tests/**` is excluded from the cloud analysis
   (`ci.yml`, `sonar.exclusions`), so `S107` (more than seven parameters) is switched off there, where data-driven
   test methods need them. A rule that runs only under the scanner stays cloud-only even when listed: `S1192`
   (duplicated string literals) is one, found on PR #214 and reported by no local build. When the cloud profile
   changes, or SonarQube reports a `csharpsquid` rule the local build did not, regenerate the block the same way and
   note a scanner-only rule here.
   Never trust a test result after a build that failed on analyzer errors: the test run uses the stale DLLs.

Other SonarQube Cloud findings (further rules, duplication, hotspots) have no local equivalent and arrive in
squad step 11.

Own browser scripts (`src/Vandox.Backend/wwwroot/js/*.js`, no Node build) have no local analyzer: SonarQube
Cloud's JavaScript rules report on them only in squad step 11, so write them to those rules from the start. The
one that bit (`javascript:S2486`): a `catch` block that ignores the exception holds exactly one whole-line
comment and nothing else — a comment before a `return` inside the block is still flagged.

Changed shell scripts (`*.sh`), GitHub workflows (`.github/workflows/*.yml`) and Dockerfiles are checked with
`shellcheck`, `actionlint` and `hadolint`; the SessionStart hook installs the three as pinned, checksum-verified
binaries (`.claude/hooks/install-linters.sh`), and the gate reports NOT RUN for one that is missing (a refused
download, for example). The gate itself checks two SonarQube Cloud shell rules shellcheck does not report:
`shelldre:S7679` (a positional parameter `$1`…`$9` used as a word of its own inside a function — assign it to a
local variable first) and `shelldre:S7688` (`[ … ]` instead of `[[ … ]]`). Other `shelldre:*` findings only report
in squad step 11.

### Not checkable in a cloud session

The cloud sessions lack some tools and network access, so these checks cannot run locally. The check in the last
column, on the pull request head, is then the gate; the member who would run the check writes "not verified
locally, <authoritative check>" once in `log.md` and does not repeat it as an open point in every round.

| Change | Missing locally | Local substitute | Authoritative check |
| ------ | --------------- | ---------------- | ------------------- |
| shell scripts (`.github/scripts/*.sh`, `deploy/**/*.sh`) | `shfmt` (`shellcheck` runs in the *Analyzer gate*) | `bash -n <script>`; the *Analyzer gate* runs shellcheck and checks the shell pitfalls below | SonarQube Cloud (`shelldre:*`, step 11) and the CI step that runs the script |
| workflows (`.github/workflows/*.yml`) | a GitHub Actions runner (`actionlint` runs in the *Analyzer gate*) | the *Analyzer gate* (actionlint) | the workflow's own run on the pull request |
| `deploy/backend/Dockerfile`, `docker-compose.yml`, `.dockerignore`, image contents | a Docker daemon (`hadolint` runs in the *Analyzer gate* on the Dockerfile) | the *Image build check* (restore-order reproduction below), `.github/scripts/check-base-image-pinning.sh`, `.github/scripts/check-builder-dotnet-version.sh`, `bash -n` on the smoke script | CI job *Release build check*, steps "Build and verify image" and "Smoke test backend container" |
| own browser scripts and style sheets (`src/Vandox.Backend/wwwroot/js/*.js`, `wwwroot/*.css`) | a JavaScript and CSS analyzer | `WebUiContentTests` (`WebSourceScanner` rules) | SonarQube Cloud (step 11) |
| known vulnerabilities (Go) | `govulncheck` (the proxy blocks `vuln.go.dev`, HTTP 403) | none | CI job *Vulnerability scan* |
| C# conditions on a local that a lambda assigns (SonarQube Cloud `csharpsquid:S2583`, "condition always evaluates to False", and the other symbolic-execution rules) | the local SonarAnalyzer.CSharp build does not report them (run #27: the quality gate failed in CI with a C reliability rating on a local assigned only inside a callback passed to `PageRunner.RunAsync`) | the captured-local rule under *Writing code*, checked by reading every new lambda that assigns an outer local | SonarQube Cloud (step 11) |
| values passed to a logger or other sink (CodeQL "Clear text storage of sensitive information") | CodeQL runs only in CI | the logger-argument rule below, checked by grepping the new logger calls | CodeQL code scanning on the pull request |
| Go conversions of a parsed number to a narrower or signed integer type (CodeQL `go/incorrect-integer-conversion`) | CodeQL runs only in CI; `go vet` and golangci-lint do not report it | the integer-conversion rule under *Writing code*, checked by grepping the new `strconv.ParseUint`, `strconv.ParseInt` and `strconv.Atoi` calls and every conversion of their results | CodeQL code scanning on the pull request |

When the *Analyzer gate* reports shellcheck, actionlint or hadolint as NOT RUN (a refused download), that tool is
missing locally for the session: note it in `log.md` as above, and parse a changed workflow as YAML instead
(`python3 -c 'import sys, yaml; yaml.safe_load(open(sys.argv[1]))' <file>`).

Shell pitfalls that shellcheck does not report and the *Analyzer gate* checks itself (SonarQube Cloud reports
them too): use `[[ … ]]`, never `[ … ]` (`shelldre:S7688`), and assign every positional parameter to a named
`local` variable before using it inside a function (`shelldre:S7679`).

Logger arguments: the C# CodeQL query treats a variable, property or constant as sensitive when its name, ignoring
case, contains `password`, `passwd` or `trusted`, `account` or `accnt` followed by `id` or `key`, `license` or
`licence` followed by `key`, or `voucher` followed by `code`, unless the name also contains `hashed`,
`encrypted`, `crypt` or `invalid`. It does so even when the value is only a name (a failed run: `logger.WebPasswordHashMissing(ConfigConstants.EnvWebPasswordHash)` logged just the
environment variable's name). No local gate reports this. A value passed to a `[LoggerMessage]` method must not come
from an expression named that way; log such a name through a neutrally named constant whose value a test pins
(`ServeCommand.HashSecretVariable`, pinned by `ServeRefusesMissingPasswordHash`). Before the pull request, grep the
arguments of the new logger calls for those names. In Go the query flags names containing `secret`,
`trusted`, `passwd`, `password`, `passcode`, `passphrase`, `username`, `userid` or `puid`, and keys such as `authKey`,
`apiKey` and `secretKey`, unless the name also contains `hash`, `crypt`, `code`, `mask`, `test` or `redact`; avoid those
in logger arguments of the agent too.

Two analyzer rules collide and are settled once (record 0074): RH3001 forbids the negation operator `!`,
S1125 forbids comparing a boolean with a literal (`== false`, `is false`). Write positive conditions, early
returns or a small `Require`-style helper; never a workaround such as `== default(bool)`.
IDE0046 (prefer a conditional expression over an `if` that returns) collides with S3358 (no nested ternaries)
and stays silent (record 0087): an `if` with an early return is the accepted form.

## Image build check

Only the backend builds an image (`deploy/backend/Dockerfile`); the agent is installed as a release binary
(`deploy/agent/`), so the Go side has no image build check. The Dockerfile copies the solution and build files
and the four `.csproj` files first, restores `src/Vandox.Backend/Vandox.Backend.csproj`, then copies `src/` and
publishes with `--no-restore`. The .NET SDK decides at restore time what the publish output holds (content
items, static web assets, the `_framework/` scripts of a Blazor app), so a publish from the full working tree
can contain files the image never gets (Vandox#24: `_framework/blazor.web.js` was in the local publish and
missing from the image, because no `.razor` file existed at restore time). Where Docker runs, build the image.
Where it does not (the cloud sessions), reproduce the Dockerfile's order in a scratch directory; every change
to a `.csproj`, `Directory.*.props`, `Vandox.slnx`, `global.json`, a `.razor` or other content item, a static
web asset under `wwwroot/` or the Dockerfile runs it before the review:

```bash
scratch="$(mktemp -d)"
git ls-files -z -- .editorconfig global.json Directory.Build.props Directory.Packages.props Vandox.slnx 'src/*.csproj' \
  | xargs -0 -I{} cp --parents {} "$scratch"
(cd "$scratch" && dotnet restore src/Vandox.Backend/Vandox.Backend.csproj)
git ls-files -z -- src/ | xargs -0 -I{} cp --parents {} "$scratch"
(cd "$scratch" && dotnet publish src/Vandox.Backend/Vandox.Backend.csproj -c Release --no-restore -o ./publish \
  -p:UseAppHost=false -p:DebugType=none)
ls "$scratch"/publish/wwwroot/_framework "$scratch"/publish/wwwroot/js
```

Keep the first copy equal to the `COPY` lines before `RUN dotnet restore` in the Dockerfile and the second to
`COPY src/ src/` (`.dockerignore` lets nothing else into the build context). A `--no-restore` publish that
fails, or an output that is missing, is the defect the CI job *Release build check* would report after the PR
is open.

## Coverage gate

`coverage-check.py` merges every `TestResults/**/coverage.cobertura.xml`: coverlet's report of each .NET test
project and `TestResults/go/coverage.cobertura.xml`, which `go-coverage-to-cobertura.py` writes from
`coverage.out`. Production code is every `*.go`, `*.cs` and `*.razor` file outside `tests/` and `*_test.go`
(`.squad/tools/squad_settings.py`). The thresholds are 80 % on new/changed code and 80 % overall.

## Coverage exclusions

Two places compute coverage and must agree (`.squad/routing.md`, *Code a unit test cannot cover*): the
*Coverage gate* reads `COVERAGE_PATHSPECS` and `COVERAGE_EXCLUDES` in `.squad/tools/squad_settings.py` (`*.go`,
`*.cs` and `*.razor` outside `tests/`, `*_test.go` and `obj/`, so own browser scripts do not count locally), and
SonarQube Cloud reads `sonar.coverage.exclusions`, passed as `/d:sonar.coverage.exclusions=…` in the
`dotnet-sonarscanner begin` step of `.github/workflows/ci.yml` (there is no `sonar-project.properties`: the
scanner for .NET refuses one; the Go profile reaches it as `sonar.go.coverage.reportPaths=coverage.out`). Today
it excludes `.squad/**`, `.claude/**` and `src/Vandox.Backend/wwwroot/js/**` (record 0098). A file no unit test
can cover (`wwwroot/js/**`, a generated file) is added to that argument, and to `COVERAGE_EXCLUDES` where
`COVERAGE_PATHSPECS` includes it, with a decision record — never to `sonar.exclusions`, which would drop it
from the analysis too. Process startup wiring is not excluded: a Go `main` and `Program.Main` stay thin and are
the accepted uncovered lines (`docs/UNIT_TESTS.md`, records 0072 and 0074).

## Writing code

Go (agent):
- `gofmt` formatting; package names short and lower-case; exported identifiers documented with a comment
  that starts with the identifier's name.
- Errors are returned and wrapped with context (`fmt.Errorf("…: %w", err)`), never ignored; no `panic` in
  library code.
- `context.Context` is the first parameter of anything that does I/O or can block, and is honored.
- A number parsed with `strconv.ParseUint`, `strconv.ParseInt` or `strconv.Atoi` and converted to a narrower
  type, or from unsigned to signed (`int64(v)` of a `ParseUint` result, `int32(n)` of an `Atoi` result), needs
  one of two forms: an upper-bound check against a constant no larger than the target type's maximum in the
  same function, immediately before the conversion (`if v > maxKBValue { return … }`, then `int64(v)`, as in
  `internal/sysstats/memory.go`), or a parse into the target type from the start, with the `bitSize` argument
  set to the target's width and the matching signedness (`strconv.ParseInt(s, 10, 64)` for an `int64`,
  `strconv.ParseUint(s, 10, 16)` for a `uint16`, as in `internal/config/config.go`). CodeQL's
  `go/incorrect-integer-conversion` (high severity) runs only in CI, no local gate reports it, and on PR #265 a
  bound checked elsewhere (a digit-count limit, a check inside a helper) did not count: five alerts on
  `ParseUint` results converted to `int64`.
- The agent prints its build information through `internal/version`, set via `-ldflags`.

C# (backend): follow the surrounding code and the analyzers; in particular
- file-scoped namespaces, `#region` sections and XML documentation on every member as in the existing files
  (Reihitsu enforces the layout; run *Format* before building);
- a C# file ends without a final newline (Reihitsu `RH5603`) and never mixes line endings (`RH5604`); the
  repository stores `.cs` files with LF and checks them out with CRLF (`.gitattributes`). Do not fix layout by
  hand: *Format* removes the final newline and normalizes the line endings, and the member that wrote the file
  runs it before building (`.squad/team.md`, *Shared rules*);
- nullable reference types on, `CancellationToken` as the last parameter of anything that does I/O or can
  block, and honored;
- logging through `[LoggerMessage]` methods (`BackendLog`), never string interpolation in a log call;
- a local that a callback assigns is not tested after the call: SonarQube Cloud's flow analysis does not
  follow the assignment into the lambda and reports the test as always false (`S2583`), and the local
  build stays silent. Pass the value out through the callback's return value or a field instead;
- exceptions carry context in their message and are never swallowed; `Program.Main` and similar wiring stay
  thin and hold no logic (record 0074);
- the backend prints its build information from the assembly metadata set by `-p:VandoxVersion`,
  `-p:VandoxCommit` and `-p:VandoxDate`.

## Writing tests

See `docs/UNIT_TESTS.md`. Go: the standard `testing` package, table-driven tests with `t.Run`, `t.Helper()` in
helpers, `t.TempDir()` for files, failure messages that state got and want. .NET: MSTest, one test class per
class under test, Arrange/Act/Assert comments, helper classes for temporary directories and fakes. New C# test
files follow the C# layout in *Writing code*: the Tester does not fix layout by hand but runs *Format* on the files
it wrote before building, so *Format check* finds nothing in them.

Two analyzer rules that cost a build cycle when found only by the build: nested classes are forbidden in C#
tests too (RH2101; put a helper class in its own file, since RH7002 allows one top-level type per file), and
CA1835 forbids calling the `WriteAsync(byte[], int, int, CancellationToken)` and
`ReadAsync(byte[], int, int, CancellationToken)` overloads of a stream directly (use the memory overloads,
`WriteAsync(buffer.AsMemory(), token)` and `ReadAsync(buffer.AsMemory(), token)`).

Heap-bound tests (.NET) measure retention with `GC.GetTotalMemory(true)`, which Sonar `S1215` flags. The accepted
form is one private helper in the test class that wraps exactly that call in `#pragma warning disable S1215` /
`#pragma warning restore S1215` (as `KernelReportGrouperTests.RetainedBytes`); no other suppression of `S1215`.

Allocation-bound tests (.NET) measure with `GC.GetTotalAllocatedBytes(true)` and should hold in Debug, where *Test*
runs them, as well as in Release, where CI runs them. In Debug the compiler emits every async state machine as a
class, so each call of an async method allocates even when it completes synchronously: `LogLineReader.ReadAsync`
allocates about 104 bytes per call there and essentially nothing in Release. An absolute bound smaller than that
per-call allocation times the number of such calls the input causes therefore fails in Debug for every
implementation (64 MiB of empty lines are about 67 million calls, about 7 GB). Either size an absolute bound for
the number of calls the input causes (as `JournalExportReaderTests.AllocationBound`), or measure the allocation
beyond a baseline loop that reads the same input through that method alone and bound the difference (as
`MariaDbErrorLogParserTests.ReaderAllocationAsync`).

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
Constructors store their arguments and never throw: a test harness builds these types, so only member bodies
fail. The skeleton body above is the default; a zero-value stub is allowed only where it would break a test that
exists and passes today: the new tests must still fail on the stub, and the Dev reports it as a deviation.
New skeleton files follow the C# layout in *Writing code* (no final newline); run *Format* on them before *Build*.
Reihitsu `RH2003` forbids `NotImplementedException` and Sonar `S2325` flags instance members that do not use `this`,
so every skeleton file carries `#pragma warning disable RH2003, S2325` before its type and the Dev
removes the line when implementing the file; no skeleton pragma may remain when the *Analyzer gate* runs in step 7.

File names of C# types: the file is named after its type, and for a generic type Reihitsu `RH4001` expects the
type parameters in braces — `NullAsAbsentConverter{T}.cs`, `NullElementListConverter{TItem}.cs`. A name without
them (`NullAsAbsentConverter.cs`) or in backtick form (``NullAsAbsentConverter`1.cs``) fails the build, so a plan
names the files of new generic types this way from the start.

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
- A stale Razor or compiler build server reports bogus `RZ1021`/`RZ9981` errors on a clean or Release build,
  also in files nobody changed. When a Razor error names an untouched file, run `dotnet build-server shutdown`
  first and then *Build* again; only an error that survives the restart is real.
- `reihitsu-format` rewrites a `\u` escape in a C# string literal into the character it names, so a literal
  U+2028 then breaks the compilation and a test about an escape tests the wrong input. After *Format*, check
  the escapes of the changed files on disk (`grep -n '\\u' <file>`, *Escapes in files* in `.squad/team.md`);
  where the formatter keeps rewriting one, build the character at run time (`char.ConvertFromUtf32(0x2028)`)
  instead of writing it as an escape in a literal.
