# 0203: Two-language toolchain with one set of quality gates, the .NET rules from the template's shared baseline

- **Status:** Accepted
- **Date:** 2026-11-12
- **Area:** —
- **Source:** Issue #421 (the template's shared .NET rule baseline)
- **Supersedes:** [0074](0074-two-language-toolchain-and-combined-quality-gates.md)

## Context

[0074](0074-two-language-toolchain-and-combined-quality-gates.md) set one set of quality gates for the Go agent and the
.NET backend, with the C# analyzer rules written by hand in this repository's `.editorconfig`. It settled the collision
of Reihitsu RH3001 (no negation operator `!`) and SonarAnalyzer S1125 (no comparison of a boolean with a literal) by
positive conditions, so the `dotnet` profile's `== false` rule did not apply here. The template now gives every .NET
repository the same analyzer rules and build structure (LarsLaskowski/Squad-Spec-Repository-Template#337); its owner
decided that `== false` is the binding style and that S1125 is off in every repository (#338). SonarQube Cloud's
built-in *Sonar way comprehensive* profile cannot switch S1125 off; with the rule off in the source it reports nothing.

## Options considered

- **Keep this repository's own C# rules and S1125** — rejected: Vandox would be the one .NET repository whose rules and
  boolean style differ from the template's, and every refresh would have to work around it.
- **Take the template's shared baseline, S1125 off, `== false` as in every .NET repository** — chosen.

## Decision

One set of gates for both languages, as in 0074: the Go side unchanged; the .NET side builds with `Reihitsu.Analyzer`
and `SonarAnalyzer.CSharp`, every diagnostic an error, `reihitsu-format`, MSTest and coverlet, and its rules and their
severities come from the template's baseline, written into the `squad:dotnet-rules` and `squad:sonar-rules` blocks of
`.editorconfig` (`.squad/stack.md`, *Analyzer gate*). S1125 is off and a negated condition is written `== false`;
`Program.Main` and similar wiring stay thin and are the only uncovered production lines (0072).

## Consequences

- The existing positive conditions stay; new code may use `== false`. RH3001 still forbids `!`.
- A baseline rule the code does not meet yet is listed in `DOTNET_RULES_PENDING` of `squad_settings.py` with its issue
  and reported as `suggestion` until that issue is done.
- The settings of [0087](0087-csharp-style-rules-follow-the-standard-settings.md) (IDE0046 silent against S3358, IDE1006
  and MSTEST0084 as errors, CRLF for C#) are held by the baseline now; a change to them is made in the template.
- The Go-to-Cobertura converter (`.squad/tools/go-coverage-to-cobertura.py`) stays repository-owned, and the CI workflow
  passes the Sonar settings as scanner arguments instead of a `sonar-project.properties`.
