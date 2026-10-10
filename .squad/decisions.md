# Squad Decisions

Process decisions about how the squad works in this repository — not decisions about the product code
(those are decision records in `docs/decisions/`). Append-only: add a dated entry, never rewrite an old
one. Changed only in squad-maintenance PRs.

- 2026-10-03 — Squad adopted from Squad-Spec-Repository-Template (`adopt-template`), stack profile
  `go`. Reason: one shared, stack-neutral squad and rule set across all repositories; project
  knowledge lives in `.squad/stack.md`, `.squad/project.md` and the `<!-- project:… -->` sections of the
  instruction files.
- 2026-10-06 — Repository becomes two-language: the agent stays Go, the backend moves to .NET 10 with
  Blazor (decision records 0073 and 0074). `.squad/stack.md` names both toolchains with one command per gate;
  `analyzer-check.py` runs `go vet`, golangci-lint and a warnings-as-errors `dotnet build`;
  `coverage-check.py` merges coverlet's Cobertura reports with the Go profile converted by
  `go-coverage-to-cobertura.py`; `squad_settings.py` lists `*.go`, `*.cs` and `*.razor` as production code. The
  template has one stack profile per repository and its refresh (`adopt-template`) rewrites the managed files
  `.squad/tools/analyzer-check.py`, `.claude/hooks/session-start.sh`, `.squad/template.json` and the `stack:` blocks
  of the instruction files, so a refresh removes the .NET extensions: they must be restored by hand until the template
  supports several profiles (LarsLaskowski/Squad-Spec-Repository-Template#43). The new files
  `.squad/tools/go-coverage-to-cobertura.py` and the `squad_settings.py` globs are not touched by a refresh. The
  squad agents, skills and routing are stack-neutral and unchanged.
- 2026-10-07 — Squad refreshed from Squad-Spec-Repository-Template (`adopt-template`), stack profiles `go` and
  `dotnet` (the template now supports several profiles, LarsLaskowski/Squad-Spec-Repository-Template#43). The hand-made
  extensions of 2026-10-06 are replaced by the template's own: `analyzer-check.py` dispatches to
  `analyzer-check-go.py` and `analyzer-check-dotnet.py`, `session-start.sh` runs `session-start-go.sh` and
  `session-start-dotnet.sh`, and the `stack:` blocks of the instruction files carry both profiles. `squad_settings.py`
  lists one `COVERAGE_REPORTS` entry per report kind. `.squad/tools/go-coverage-to-cobertura.py` stays project-specific.
  The seeded `sonar-project.properties` was not adopted: the scanner for .NET refuses a repository that has one, the CI
  workflow passes the settings as arguments.
