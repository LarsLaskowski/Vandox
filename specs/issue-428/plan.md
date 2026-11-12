# Plan: Meet CA2100 (SQL command text review) from the shared .NET rule baseline

Source: Issue #428
Tier: trivial — analyzer suppressions with a reason and a rule-severity setting, no change to behavior or control flow; needs a decision record (0204).
Security areas touched: build configuration (`.editorconfig` rule block); Ingest authentication (`BatchWriter`, `AgentTimeline`'s `TimelineStatement`), Parsing of external input (log search, `LogFinder`), File writes (`vandox.db`, `src/Vandox.Storage/`) — one pragma pair per helper, no logic

## Problem / root cause

CA2100 reports a `CommandText` assignment whose value its data-flow analysis cannot prove constant. The storage helpers take
the text as a parameter, so each fires whatever its callers pass; test helpers do the same with their own statements.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | 44 findings (issue) | Release build of bfca3cf with the gate's SARIF logs (scratch worktree): 39 results. #431 (f7f525a) already removed the 5 in `SchemaMigrator`, `SqliteStore`, `TemplateRebuilder` (2), `TemplateBenchmark` | refuted |
| 2 | The other 34 issue locations are current | same SARIF: identical file and line; plus 5 more (row 3) | confirmed |
| 3 | Split of the 39 | 8 production (`TimelineStatement.cs:31`, `BatchWriter.cs:194`, `ListenerMarkWriter.cs:166`, `IncidentNoteRepository.cs:184`, `IncidentRepository.cs:317`, `TemplateTally.cs:167`, `LogFinder.cs:415`, `ReaderQuery.cs:59`); 20 test sites unsuppressed (issue's test list minus row 4); 11 already in a bare `#pragma` (`SqliteStoreWriteTests` 637, `SqliteStoreTemplateRebuildTests` 541/563, `SqliteStoreTemplateTests` 1121/1159/1193, `SqliteStoreLogExplorerTests` 2335/2395/2424, `TemplatesCommandTests` 228/250) | confirmed |
| 4 | A pragma-suppressed result does not fail the build or the gate | SARIF marks the 11 as `suppressions`; `analyzer-check-dotnet.py:65` skips them | confirmed |
| 5 | Every text reaching the 8 production sites is constant or built from constant fragments, values bound as parameters | read: `AgentTimelineQueries`, `GapReadSql`, `BatchWriter.cs:46-56`, `ListenerMarkWriter`, `TemplateTally`, `IncidentRepository.cs:134,215,272`, `IncidentNoteRepository.cs:67`, `LogFinderQueries` (`Keep`: `$word{index}` names only), `RecordQueries`, `LogExplorerQueries.cs:112-132,179`, `TemplateQueries`, `ListenerQueries.cs:180-194`; `FtsQuery` output goes to `$match` | confirmed |
| 6 | Removing the pending entry makes CA2100 `warning`, an error under `TreatWarningsAsErrors` | `.squad/dotnet-rules.editorconfig:187`; `Directory.Build.props:10` | confirmed |

## Acceptance criteria

- AC1: `DOTNET_RULES_PENDING` has no `CA2100` entry and `.editorconfig` holds the block `dotnet-rules.py` writes from it (`dotnet_diagnostic.CA2100.severity = warning`).
- AC2: The Release build of `Vandox.slnx` reports no unsuppressed CA2100 result.
- AC3: Every CA2100 suppression in `src/`, `tests/` and `tools/` is `#pragma warning disable CA2100 // <where the text comes from>` around exactly one `CommandText` assignment, closed by `#pragma warning restore CA2100`; none is bare.
- AC4: Every statement that reaches a suppressed production site is constant or composed of constant fragments, every value a bound parameter.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | check: `python3 .squad/tools/dotnet-rules.py --check` and `dotnet_config.py` in the step-7 *Analyzer gate* | — |
| AC2 | check: step-7 *Analyzer gate* (its build fails on any); CI Release build | — |
| AC3 | check: `grep -rnE '#pragma warning disable CA2100[[:space:]]*$' src tests tools` prints nothing; Reviewer reads each pair (step 8) | — |
| AC4 | check: Security (step 3) and Reviewer (step 8) read the callers of row 5 | — |

## Approach

Order in step 6: the orchestrator removes `"CA2100": "#428"` from `DOTNET_RULES_PENDING` and runs `python3
.squad/tools/dotnet-rules.py` (members cannot write `.squad/`); then the Dev wraps the 8 production assignments and the
Tester the 20 test assignments and adds a reason to the 11 bare pragmas. Reason wording is free, but names the source of
the text (production: constant statements of the callers, values as parameters; tests: the test's own fixed statements).
Nothing else changes: no assignment moves, no text changes. Options and why: record 0204.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | the 8 files of row 3 | pragma pair |
| Vandox.Storage.Tests, Vandox.Backend.Tests | the files of row 3 | pragma pair or reason |
| repository | `.squad/tools/squad_settings.py`, `.editorconfig` | pending entry removed, block regenerated |

## Signatures (for the Dev's skeleton)

None: no API changes.

## Test files

No new tests; existing test call sites: none affected. Files no unit test can cover: none (pragma lines are not executable).

## Areas

Storage (`docs/areas/storage.md`): states the existing rule that SQL text is fixed and values are bound parameters; no behavior change.

## Documentation updates

- `docs/areas/storage.md`, *Database file and connections* (Dev): one bullet — every statement is a constant or composed of constant fragments, and every value from a request, batch, file or address is bound as a parameter; a helper that takes the text suppresses CA2100 with that reason (record 0204). *Related decisions*: add 0204.
- `.squad/stack.md`, *Writing code*, C# list (Dev reports the text, orchestrator writes it): assign constant SQL to `CommandText` directly where possible; a helper that takes the text wraps its one assignment in a CA2100 pragma pair with the reason, tests included (record 0204).

## Architecture check

No guarantee of `.squad/project.md` or `docs/ARCHITECTURE.md` changes; the rule tightens the build. Sweep: no document enumerates the pending rules (only record 0203 describes the mechanism, unchanged).

## Security considerations

- [x] No limits.
- [x] No exception reaches a user: none.

## Decision records

- `docs/decisions/0204-sql-command-text-meets-ca2100-through-justified-suppressions-at-the-command-helpers.md` (Proposed)

## Out of scope / follow-ups

Moving assignments to callers (record 0204, option 1).

## Security notes

Security (plan review, APPROVED), non-blocking: the AC3 check `grep -rnE '#pragma warning disable CA2100[[:space:]]*$'` misses two forms that also hide CA2100 without a reason: a pragma with an empty comment (`... CA2100 //`) and a pragma with no rule ID (`#pragma warning disable` with nothing after it, which turns off every rule, CA2100 included). Use `grep -rnE '#pragma warning disable([[:space:]]+CA2100)?[[:space:]]*(//[[:space:]]*)?$' src tests tools` (prints nothing) for AC3.
