# Plan: BackendFixture reads captured standard error while the host writes it

Source: Issue #382
Tier: tests — the fix and its tests are test code only (`tests/Vandox.Backend.Tests`), beyond the `patch` line budget.
Security areas touched: none

## Problem / root cause

`BackendFixture.Error` is a plain `StringWriter` (`tests/Vandox.Backend.Tests/BackendFixture.cs:95`), handed to
the running host as standard error (`:195`). `LogEntries()` calls `Error.ToString()` (`:160`) on the test thread
without any lock while the host's loggers append from request threads; `StringBuilder` is not thread-safe, so
`ToString()` can see a chunk mid-append and throw `ArgumentOutOfRangeException ('chunkLength')`. The writer side
locks only a private, per-provider `_gate` (`src/Vandox.Backend/Logging/JsonLineLoggerProvider.cs:46`), which the
fixture cannot reach. Related: `serve` creates two providers on the same writer (`BackendApp.cs:97`, `:111`),
each with its own gate, so writes from the two are not even serialized against each other in the fixture.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `LogEntries()` reads `Error.ToString()` unlocked | `BackendFixture.cs:156-168` | confirmed |
| 2 | The host's log writes lock a private gate per provider | `JsonLineLoggerProvider.cs:17`, `:44-51` | confirmed |
| 3 | Issue's fix "read under the same lock the log writer uses" is feasible from the test | the gate is private; two providers per run (`BackendApp.cs:97`, `:111`); direct `stderr` writes outside any gate (`BackendApp.cs:32`, `:39-40`); the capture must lock itself | refuted |
| 4 | Production is unaffected: real standard error is `Console.Error` | `src/Vandox.Backend/Program.cs:49`; .NET documents `Console.Error` as a synchronized writer | confirmed |
| 5 | Other tests read `fixture.Error.ToString()` while a service runs (same race) | `ServeTests.cs:113-115`, `:406`, `:777`; fixed by the same change | confirmed |
| 6 | Two call sites use `fixture.Error.GetStringBuilder().Clear()` | `ImportCommandTests.cs:432`, `ReportCommandTests.cs:40` | confirmed |
| 7 | Every `TextWriter` overload not overridden (spans, `StringBuilder`, async) ends in `Write(char)`, `Write(char[], int, int)` or `Write(string)` of the base class | to verify: AC2's test (Tester, step 6) — the private buffer is touched only by the overrides, so text from any overload that reaches it went through the lock | unverified |
| 8 | `Out` is not written by a running service | `BackendApp.ServeAsync` takes no `stdout` (`BackendApp.cs:94`) | confirmed |

## Acceptance criteria

- AC1: `LockedTextWriter.ToString()` called on one thread while another thread writes lines with
  `WriteLine(string)` never throws; every snapshot is empty or ends with a line break (whole lines only); after
  the writer finishes the text holds every line once, in order.
- AC2: text written through `Write(char)`, `Write(string)`, `Write(ReadOnlySpan<char>)`, `WriteAsync(string)`,
  `WriteLine(string)` and `WriteLineAsync(string)` all appears in `ToString()` in write order; `Clear()` empties it.
- AC3: `BackendFixture.Error` is a `LockedTextWriter`, and `LogEntries()` called repeatedly while another thread
  writes JSON lines to `Error` never throws and returns one entry per line written so far, the last call all of them.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `ToStringWhileWritingReturnsWholeLines` | `tests/Vandox.Backend.Tests/LockedTextWriterTests.cs` |
| AC2 | `WriteOverloadsAppendInOrder`, `ClearEmptiesText` | `tests/Vandox.Backend.Tests/LockedTextWriterTests.cs` |
| AC3 | `LogEntriesWhileLoggingParsesEveryLine` | `tests/Vandox.Backend.Tests/BackendFixtureTests.cs` |

The concurrency tests use a fixed count of writes (say 20,000 lines) and loop reads until the writer task
completes — no clock, no sleep; with the fix they cannot fail.

## Approach

New test helper `LockedTextWriter : TextWriter`: a private `StringBuilder` and a `Lock`; the overrides below append
under the lock, `WriteLine(string?)` appends value and `CoreNewLine` in one locked step, `ToString()` and `Clear()`
take the lock. `BackendFixture.Error` becomes a `LockedTextWriter`; `LogEntries()` stays as it is.

## Options considered

Tier `tests`: none required. Weighed: `TextWriter.Synchronized(Error)` passed to the host and a reader locking the
wrapper — relies on `SyncTextWriter` locking on itself, an undocumented detail; a `StringWriter` subclass —
every one of its ~20 direct-append overrides would need the lock, one miss keeps the race.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Backend.Tests | `LockedTextWriter.cs` | new |
| Vandox.Backend.Tests | `BackendFixture.cs` | `Error` type and doc |
| Vandox.Backend.Tests | `ImportCommandTests.cs:432`, `ReportCommandTests.cs:40` | `fixture.Error.Clear()` |

## Signatures (for the Dev's skeleton)

```csharp
internal sealed class LockedTextWriter : TextWriter
public override Encoding Encoding { get; }          // UTF-16, as StringWriter
public override void Write(char value)
public override void Write(char[] buffer, int index, int count)
public override void Write(string? value)
public override void WriteLine(string? value)
public override string ToString()
internal void Clear()
// BackendFixture
internal LockedTextWriter Error { get; } = new();
```

## Test files

`tests/Vandox.Backend.Tests/LockedTextWriterTests.cs`, `tests/Vandox.Backend.Tests/BackendFixtureTests.cs` (new).

Call sites: `ImportCommandTests.cs:432` and `ReportCommandTests.cs:40` (`GetStringBuilder().Clear()` →
`Clear()`), Tester. Others pass `fixture.Error` as `TextWriter` or call `ToString()`: unchanged.

Files no unit test can cover: none.

## Areas

none (no change in behavior).

## Documentation updates

none: test code only; `LockedTextWriter` is an internal part of `BackendFixture`, already listed in
`.squad/project.md` (*Test doubles*), not a new double of a surface. No changelog entry (no user-visible change).

## Architecture check

No guarantee touched; production code unchanged.

## Security considerations

- [x] no limits.
- [x] none.

## Decision records

- none: no decision beyond the obvious fix.

## Out of scope / follow-ups

`BackendFixture.Out` stays a `StringWriter`: no running service writes it (fact 8).
