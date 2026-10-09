# 0074: Two-language toolchain: Go for the agent, .NET for the backend, one set of quality gates

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** —
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

Records [0001](0001-quality-gates-before-the-pull-request.md), [0035](0035-format-check-step-in-ci-coverage-gate-stays-local.md)
and [0047](0047-gocognit-as-local-stand-in-for-sonar-cognitive-complexity.md) define the gates for the Go module.
The squad template this repository was seeded from has one profile per repository (`go` or `dotnet`), and its
scripts (`analyzer-check.py`, `coverage-check.py`) know one analyzer set and one coverage format.

## Options considered

1. **Pick one profile and let the other language ungated** — simple, but half the code would escape the gates.
2. **Run two sets of commands by hand** — error prone; the squad needs one named command per gate.
3. **Combine the gates in the repository-owned files** — `.squad/stack.md` names both toolchains,
   `.squad/tools/analyzer-check.py` runs the Go and the .NET steps, and the Go coverage profile is converted to
   Cobertura so one gate merges both languages.

## Decision

Option 3.
- .NET: SDK from `global.json`, `Vandox.slnx`, central package management, `Reihitsu.Analyzer` and
  `SonarAnalyzer.CSharp` with every diagnostic an error (`TreatWarningsAsErrors`, info level included),
  `reihitsu-format` as formatter (local tool in `dotnet-tools.json`), MSTest 4, coverlet for coverage
  (`coverlet.runsettings`: Cobertura for the squad gate, OpenCover for SonarQube Cloud, which reads no Cobertura for C#).
- Go: unchanged for the agent (`gofmt`, `go vet`, golangci-lint, govulncheck, 0047).
- *Analyzer gate* = `go vet`, golangci-lint on changed files and a forced `dotnet build` of the solution.
- *Coverage gate* = `.squad/tools/coverage-check.py` over `TestResults/**/coverage.cobertura.xml`: the .NET
  reports and the Go profile converted by `.squad/tools/go-coverage-to-cobertura.py`.
- Two analyzer rules collide and are resolved once for the whole code base: Reihitsu RH3001 forbids the
  negation operator `!`, SonarAnalyzer S1125 forbids comparing a boolean with a literal. Code uses positive
  conditions, early returns and small `Require`-style helpers instead of either form.
- `Program.Main` and similar wiring stay thin and are the only uncovered production lines, as in
  [0072](0072-vandoxd-import-sub-command-output-and-exit-codes.md); the testable logic lives in classes.
- CI has a Go job and a .NET job, one SonarQube Cloud analysis for both languages, `govulncheck` and a NuGet
  vulnerability check.

## Consequences

- Both coverage values (new code and overall) are measured over Go and C# together; thresholds stay at 80 %.
- The `.NET` build is the analyzer run: a stale build can hide diagnostics, so the gate builds with
  `--no-incremental`.
- The template's single-profile assumption is worked around locally; whether the template should support
  several profiles is a question for the template repository, not fixed here.
