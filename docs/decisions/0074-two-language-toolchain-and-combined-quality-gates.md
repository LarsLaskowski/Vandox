# 0074: Two-language toolchain: Go for the agent, .NET for the backend, one set of quality gates

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** —
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

The gates of the Go module are defined by [0001](0001-quality-gates-before-the-pull-request.md) and
[0047](0047-gocognit-as-local-stand-in-for-sonar-cognitive-complexity.md). With the backend in .NET (0073) the repository has two
languages, and the gates must cover both under the same named commands (*Format*, *Analyzer gate*, *Coverage gate*).

## Options considered

- **Gate only one language** — rejected: half the code would escape the gates.
- **Two sets of commands run by hand** — rejected: error prone; the squad needs one named command per gate.
- **One set of named gates that runs both toolchains** — chosen. The Go coverage profile is converted to Cobertura so one gate merges
  both languages.

## Decision

One set of gates for both languages. .NET: `Reihitsu.Analyzer` and `SonarAnalyzer.CSharp` with every diagnostic an error (info level
included), `reihitsu-format` as formatter, MSTest, and coverlet (Cobertura for the squad gate, OpenCover for SonarQube Cloud, which
reads no Cobertura for C#). Go: unchanged for the agent (0047). The *Analyzer gate* runs `go vet`, golangci-lint on changed files and
a forced full `dotnet build`; the *Coverage gate* reads the .NET Cobertura reports and the converted Go profile. CI has a Go job and a
.NET job, one SonarQube Cloud analysis for both languages, `govulncheck` and a NuGet vulnerability check. The tool list is in
`.squad/stack.md`.

Two analyzer rules collide and are resolved once for the whole code base: Reihitsu RH3001 forbids the negation operator `!`,
SonarAnalyzer S1125 forbids comparing a boolean with a literal. Code uses positive conditions, early returns and small `Require`-style
helpers instead of either form. `Program.Main` and similar wiring stay thin and are the only uncovered production lines (0072).

## Consequences

- Both coverage values (new code and overall) are measured over Go and C# together; the thresholds stay at 80 %.
- The .NET build is the analyzer run, and a stale build can hide diagnostics, so the gate builds with `--no-incremental`.
- The Go-to-Cobertura converter (`.squad/tools/go-coverage-to-cobertura.py`) is repository-owned, not part of the template.
- The template's seeded `sonar-project.properties` is not adopted: the SonarScanner for .NET refuses a repository that has one,
  so the CI workflow passes the Sonar settings as scanner arguments.
