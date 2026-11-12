# 0204: SQL command text meets CA2100 through justified suppressions at the command helpers

- **Status:** Accepted
- **Date:** 2026-11-12
- **Area:** Storage
- **Source:** Issue #428
- **Supersedes:** —

## Context

The template's shared .NET rule baseline ([0203](0203-two-language-toolchain-on-the-templates-shared-dotnet-rules.md))
sets CA2100 (*Review SQL queries for security vulnerabilities*) at `warning`, which the build treats as an error. CA2100
reports every `CommandText` assignment whose value its data-flow analysis cannot prove constant. In `src/Vandox.Storage`
the statements are created by a few command helpers (`BatchWriter`, `TemplateTally`, `ListenerMarkWriter`,
`IncidentRepository`, `IncidentNoteRepository`, `TimelineStatement`, `ReaderQuery`, `LogFinder`) that take the text as a
parameter, so the analysis sees an unknown value in each helper whatever its callers pass. Several readers compose their
statement from fixed fragments (`StringBuilder`, `string.Join`, a choice between two constants), with every value from
outside bound as a parameter. The test projects run their own fixed statements through similar helpers.

## Options considered

1. **Move every `CommandText` assignment to the caller, so the analysis sees the constant** — rejected: the composed
   statements of the readers (log search, record, template and incident lists) stay unprovable to the analysis, so they
   need suppressions anyway; the cached statements of `TimelineStatement` and the prepared commands of the writers keep
   their text as a parameter by design; and it rewrites the write and read paths of the storage layer without a change
   in behavior.
2. **Switch CA2100 off, or exclude the helpers, in `.editorconfig`** — rejected: the rules and their options are set
   only in the template's blocks (`.squad/stack.md`, *Analyzer gate*), the same in every .NET repository.
3. **A justified `#pragma warning disable CA2100` around the one assignment in each helper, after reviewing every
   caller** — chosen.
4. **`[SuppressMessage]` with a `Justification` on the helper method** — rejected: it hides the rule for the whole
   method instead of the one line, and the repository's existing suppressions are pragmas with a reason.

## Decision

Each `CommandText` assignment that receives its text as a parameter is wrapped in `#pragma warning disable CA2100 //
<where the text comes from>` and `#pragma warning restore CA2100`, in production and test code alike. A production
helper is suppressed only because every statement that reaches it is constant or composed of constant fragments, with
every value bound as a parameter ([Storage](../areas/storage.md), *Database file and connections*).

## Consequences

- The analyzer no longer checks what a new caller of a suppressed helper passes: a change that adds a caller, or builds
  statement text, is reviewed against the parameter rule of the area document instead.
- A statement assigned directly as a constant needs no suppression and stays checked by the analyzer.
- Revisit when the template's baseline changes CA2100 or the analysis learns to follow a parameter into its callers.
