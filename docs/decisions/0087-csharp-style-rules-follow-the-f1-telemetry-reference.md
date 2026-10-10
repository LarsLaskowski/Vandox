# 0087: C# style rules follow the F1-Telemetry reference, adapted to Vandox's analyzers and Linux tooling

- **Status:** Accepted
- **Date:** 2026-10-10
- **Area:** —
- **Source:** Product Manager request: align the C# style rules with the `.editorconfig` of the F1-Telemetry repository
- **Supersedes:** —

## Context

The C# rules in `.editorconfig` were a short subset of the conventions the Product Manager uses in F1-Telemetry, a
C#-only repository whose `.editorconfig` is the reference. Vandox builds differently: SonarAnalyzer.CSharp runs inside the
build, every diagnostic is an error and the analyzer gate fails on info-level findings too
([0074](0074-two-language-toolchain-and-combined-quality-gates.md)). F1-Telemetry has no SonarAnalyzer in its build, so a
rule taken over unchanged can collide with a Sonar rule there is no local evidence for. Vandox also has Go, shell and
container files that must keep LF.

## Options considered

- **Copy the reference file unchanged** — rejected: IDE0046 (`dotnet_style_prefer_conditional_expression_over_return`)
  rewrites an `if` chain that ends in a return into nested ternaries, which SonarAnalyzer S3358 forbids. A trial run of its
  code fix on this code base produced 25 S3358 findings plus Reihitsu layout findings, and further IDE0046 findings on the
  rewritten code. F1-specific suppressions (CS8618, IDE0076, IDE0290, CA1822, CA2254) answer problems Vandox does not have
  (EF Core entities, a linked `GlobalSuppressions.cs`) and would only weaken checks.
- **Keep the short Vandox subset** — rejected: the two repositories would keep drifting apart in style.
- **Take the reference over and settle each collision once** — chosen.

## Decision

The C# section of `.editorconfig` follows the F1-Telemetry reference, with IDE0046 set to `silent`: where it and S3358
collide, an `if` with an early return is the accepted form. Naming rules are enforced in the build through IDE1006 as
errors, with a constants rule (PascalCase) next to the `_camelCase` rule for private fields; MSTEST0084 is an error. C#, Razor
and Markdown files are checked out with CRLF through `.gitattributes` (the repository stores LF), every other file stays LF;
the F1-specific suppressions are not taken over, and settings that were already stricter in Vandox stay.

## Consequences

- IDE1006 only reports in the build when it has a severity of its own; without the constants rule the private-field rule
  flagged every private constant.
- Multi-line raw string literals in C# (the schema DDL in `SchemaMigrator`) contain CRLF on every platform; SQLite treats
  the carriage return as whitespace and the migration compares only the schema version.
- Revisiting IDE0046 needs either a SonarAnalyzer version without S3358 in the build or a decision to accept nested
  ternaries, which would be a change to this record.
