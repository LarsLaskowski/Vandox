# Plan: Make deadlines interrupt running SQLite queries

Source: Issue #210
Status: Draft
Tier: security — the change replaces the hang bound of security area 10 (the log search text and the template queries, where
external log text decides the cost) with a native SQLite callback, and makes its entry in `.squad/project.md` untrue.

## Problem / root cause

`SqliteStore` (`src/Vandox.Storage/SqliteStore.cs`) passes the caller's token only to Microsoft.Data.Sqlite, which never hands it to
SQLite:

- `QueryAsync` (SqliteStore.cs:238-266), `QueryTemplatesAsync` (:277-305), `IncidentsAsync` (:465-497), `CountIncidentsAsync`
  (:501-516), `FindImportBasesAsync` (:544-559) and `SchemaMigrator.ReadVersionAsync` (SchemaMigrator.cs:185-201, used by
  `PingAsync`) call `ExecuteReaderAsync` / `ExecuteScalarAsync` / `ReadAsync` with the token. In Microsoft.Data.Sqlite 10.0.12 these
  are the base-class implementations (the assembly overrides only `ExecuteReaderAsync`/`ExecuteDbDataReaderAsync`; it references
  neither `sqlite3_interrupt` nor `sqlite3_progress_handler`, checked in
  `~/.nuget/packages/microsoft.data.sqlite.core/10.0.12/lib/net8.0/Microsoft.Data.Sqlite.dll`): each checks the token once, then
  calls into SQLite synchronously. `SqliteCommand.Cancel` does nothing.
- `LeaseReaderAsync` (:209-230) opens a reader connection with no progress handler, so nothing interrupts a running `sqlite3_step`.
- The expensive part of both queries the issue names runs inside the **first** step: the log search
  (`RecordQueries.Build(LogSearch)`, RecordQueries.cs:135-153) does `MATCH`, two joins and `ORDER BY r.captured_at, r.id` before
  `LIMIT`; the usual-level query (`TemplateSpikesAsync`, :425-432) aggregates before its first row. So a deadline does not shorten
  them at all; it is checked only before the call and before each later row.

Claims of the issue:

| Claim | Result |
| ----- | ------ |
| Microsoft.Data.Sqlite 10.0.12 calls neither `sqlite3_interrupt` nor a progress handler | **confirmed** (assembly metadata, see above) |
| A token or deadline does not stop a running statement; it is only checked between rows | **confirmed**, with a precision: it is checked before the first step and before each later row; the whole sort/aggregate runs in the first step |
| `docs/areas/storage.md`, *Log search*, claims a deadline bounds it | **confirmed** (storage.md:149-151; also record 0066 and `docs/ARCHITECTURE.md` *Log search*) |
| The usual-level template query is affected (record 0095) | **confirmed**; 0095 option 15 and its consequence already state the gap; `docs/areas/detection-and-reports.md`:249-251, storage.md:130-131 and `.squad/project.md` area 10 say so too |
| SQLitePCLRaw offers `sqlite3_interrupt` and `sqlite3_progress_handler` | **confirmed** in `SQLitePCLRaw.core` 2.1.12 (transitive package of Microsoft.Data.Sqlite.Core; it also keeps the hook delegate per connection, `hook_handles`) |

Related defect found on the way: `PingAsync` (SqliteStore.cs:88-93) calls `LeaseReaderAsync` outside any `try`, so a `SqliteException`
while opening the reader connection or running its pragmas escapes unwrapped, against its documented `StoreException`. It is fixed
here because the same catch sites are rewritten (AC7).

## Acceptance criteria

Raw-connection tests open `Data Source=:memory:` (or a file in `TempDirectory`) and register a test function with
`connection.CreateFunction` that calls `Cancel()` on the test's `CancellationTokenSource` when its argument equals a given value and
returns 0. The long statement is a recursive CTE of 20,000,000 rows, e.g.
`WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 20000000) SELECT count(*) FROM c WHERE cancel_at(x, 1) = 1`
(cancellation inside the first step), or a row query that returns row 1 and cancels inside the second step,
`... SELECT x FROM c WHERE x = 1 OR x = 20000000 OR cancel_at(x, 2) = 1`. Without a handler such a statement runs to its end
and returns a result, which fails the test; no real clock and no sleep are involved.

- [ ] **AC1** `ReaderQuery.Attach` with a cancellable token makes SQLite stop a statement inside its first step once the token is
  cancelled: the call throws `SqliteException` with `SqliteErrorCode` 9 (`SQLITE_INTERRUPT`).
  Test: `ReaderQueryAttachInterruptsFirstStepWhenCancelled`.
- [ ] **AC2** `ReaderQuery.ReadAsync` on a connection with the handler, when the token is cancelled inside a step, throws
  `OperationCanceledException` whose `CancellationToken` is the call's token, whose `InnerException` is a `SqliteException` with code
  9, and whose message is exactly `<context>: cancelled` (context `store: reading records` in the test → `store: reading records:
  cancelled`); this holds inside the first step and inside a later step (after row 1 was read).
  Tests: `ReaderQueryReadThrowsCanceledWhenInterruptedInFirstStep`, `ReaderQueryReadThrowsCanceledWhenInterruptedInLaterStep`.
- [ ] **AC3** No spurious interruption: with `CancellationToken.None` (no handler is installed) and with a cancellable token that is
  never cancelled, the same statement runs to its end and `ReadAsync` returns the full result (count 20,000,000, or the rows `1` and
  `20000000`). Test: `ReaderQueryReadCompletesWhenNotCancelled` (data rows: none, uncancelled).
- [ ] **AC4** `ReaderQuery.Translate` returns `OperationCanceledException` (message `<context>: cancelled`, the token, the exception as
  inner) exactly when `SqliteErrorCode` is 9 and the token is cancelled. Test: `ReaderQueryTranslateMapsInterruptToCanceled`.
- [ ] **AC5** Otherwise `Translate` returns a `StoreException` with the exact text `<context>: <SqliteException.Message>` and the
  exception as inner — rows: code 1 with a cancelled token, code 9 with an uncancelled token, code 5 with `CancellationToken.None`
  (a `SqliteException` is built with `new SqliteException(message, errorCode)`). So the existing error texts (`store: reading
  records: …`, `store: reading templates: …`, `store: reading incidents: …`, `store: finding import bases: …`, `store: reading
  schema version: …`) are unchanged. Test: `ReaderQueryTranslateReturnsStoreExceptionOtherwise`.
- [ ] **AC6** Every reader connection the store leases carries the handler: `SqliteStore.LeaseReaderAsync(token)` on a store opened
  in a `TempDirectory`, then the test function registered on `lease.Connection` and the first-step statement run through
  `ReaderQuery.ReadAsync` → `OperationCanceledException` with inner code 9. The lease still runs `query_only` (an `INSERT` on the
  leased connection fails with `SqliteException`, as before). Tests (in `SqliteStoreQueryTests`):
  `SqliteStoreLeaseReaderInterruptsRunningStatement`, `SqliteStoreLeaseReaderStaysQueryOnly`.
- [ ] **AC7** Every reader path of the store maps an interruption through `ReaderQuery`: `RecordsAsync`, `SearchLogsAsync`,
  `FindTemplateAsync`, `TemplateHoursAsync`, `NewTemplatesAsync`, `TemplateSpikesAsync` and `IncidentsAsync` read through the one
  generic `QueryAsync<T>` that calls `ReaderQuery.ReadAsync`; `CountIncidentsAsync`, `FindImportBasesAsync`, `PingAsync` (now with a
  `try` around the lease) and `SchemaMigrator.ReadVersionAsync` catch `SqliteException` with `throw ReaderQuery.Translate(...)`. No
  `catch (SqliteException` on a reader path builds a `StoreException` itself. Verified by the Reviewer against this list (a
  deterministic store-level test of a running statement would need a seam that puts a test function into the store's own SQL;
  AC6 covers the lease, AC2/AC4/AC5 the mapping). Pins (step 6, they pass today and must keep passing): a token cancelled before
  the call makes `SearchLogsAsync` and `TemplateSpikesAsync` throw `OperationCanceledException` —
  `SqliteStoreSearchLogsThrowsCanceledForCancelledToken` (in `SqliteStoreQueryTests`),
  `SqliteStoreTemplateSpikesThrowsCanceledForCancelledToken` (in `SqliteStoreTemplateTests`). All existing storage tests stay green
  unchanged.
- [ ] **AC8** The writer connection gets no handler: `ReaderQuery.Attach` is called only in `LeaseReaderAsync`, never in
  `OpenInAsync` or on `_writer`. Verified by the Reviewer (not observable without interrupting a writer statement, which this change
  must not make possible).
- [ ] **AC9** The documents state the new behavior with the texts under *Documentation updates*; verified by the Reviewer.

## Approach

1. New internal static class `ReaderQuery` (`src/Vandox.Storage/ReaderQuery.cs`):
   - `Attach` installs, only when `cancellationToken.CanBeCanceled`, `raw.sqlite3_progress_handler(connection.Handle,
     ProgressInstructions, Progress, cancellationToken)` (`using SQLitePCL;`). `Progress(object state)` returns 1 when
     `((CancellationToken)state).IsCancellationRequested`, else 0. It must never throw: a managed exception inside a native
     callback ends the process. SQLite calls it on the thread that runs the statement, so no race with disposal of the connection.
   - `ReadAsync<T>` creates the command, adds the parameters, runs `ExecuteReaderAsync` and the `ReadAsync` loop with `map`, all
     inside one `try` that also encloses the disposal of command and reader, and catches `SqliteException` with
     `throw Translate(exception, context, cancellationToken)`.
   - `Translate`: `SqliteErrorCode == raw.SQLITE_INTERRUPT && cancellationToken.IsCancellationRequested` →
     `new OperationCanceledException($"{context}: cancelled", exception, cancellationToken)`; else
     `new StoreException($"{context}: {exception.Message}", exception)`.
2. `SqliteStore`:
   - `LeaseReaderAsync` becomes `internal` and calls `ReaderQuery.Attach(connection, cancellationToken)` after the pragmas (the
     pragmas themselves run without it). The handler lives as long as the leased connection (no pooling; disposed with the lease).
   - `QueryAsync` and `QueryTemplatesAsync` (and the loop in `IncidentsAsync`) become one `QueryAsync<T>(sql, parameters, map,
     context, cancellationToken)`: lease inside a `try`, `ReaderQuery.ReadAsync(lease.Connection, …)`, and
     `catch (SqliteException exception) { throw ReaderQuery.Translate(exception, context, cancellationToken); }` for the lease.
     Contexts: `store: reading records` (records, search), `store: reading templates` (template queries and `FindFirstAsync`),
     `store: reading incidents` (`IncidentsAsync`).
   - `CountIncidentsAsync`, `FindImportBasesAsync` use `ReaderQuery.Translate` in their existing catch; `PingAsync` gets
     `try { lease; ReadVersionAsync } catch (SqliteException) { throw ReaderQuery.Translate(…, "store: reading schema version", …) }`.
   - Writer paths (`WriteBatchAsync`, `SaveIncidentAsync`, the import tracker writes, `OpenInAsync`) are unchanged.
3. `SchemaMigrator.ReadVersionAsync`: its catch becomes `throw ReaderQuery.Translate(exception, "store: reading schema version",
   cancellationToken)` (same text as today for every non-interrupt error; on the writer an interrupt cannot occur).
4. No new package reference: `SQLitePCLRaw.core` is already a transitive dependency of `Microsoft.Data.Sqlite.Core` and its types
   are visible at compile time (`SqliteConnection.Handle` is of its type `sqlite3`). If the build cannot see `SQLitePCL.raw`, the Dev
   stops and reports — adding a direct `PackageReference` is a dependency change that needs a Lead decision.

Callers need no change: `PingChecker` already turns an `OperationCanceledException` of its deadline into its `TimeoutException`;
`vandoxd incidents` and `vandoxd import` already treat it as "interrupted" when their token is cancelled.

## Options considered

Recorded in [0097](../../docs/decisions/0097-reader-statements-interrupted-by-a-progress-handler-bound-to-the-token.md):

1. **Progress handler per reader connection checking the token** (chosen) — same-thread callback, no race with disposal, lives with
   the leased connection; one callback per 1,000 instructions for cancellable tokens.
2. **`sqlite3_interrupt` from `CancellationToken.Register`** — lost: the callback runs on the cancelling thread and can reach the
   connection while it is disposed (undefined behavior) unless registrations are disposed first; an interrupt between two statements
   of one call is a no-op, so the next statement runs unbounded.
3. **Correct only the documents** — lost: the issue asks for the bound, and the queries stay unbounded under hostile text.
4. **Interrupt the writer too** — lost: batches are already bounded; an interrupted migration or batch only fails work that must be
   repeated. Out of the issue's scope.
5. **`OperationCanceledException` for an interrupt with a cancelled token** (chosen) vs. 6. **`StoreException`** — lost: callers would
   report a cancellation as a database failure (the health check as a failure instead of its timeout).

Interval: 1,000 instructions — the common choice; latency is the time of 1,000 instructions plus at most one long instruction, the
cost one native-to-managed callback per 1,000 instructions. Not measured (see *Out of scope*).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `ReaderQuery.cs` (new) | progress handler, read loop with mapping, exception translation |
| Vandox.Storage | `SqliteStore.cs` (rewrite of the reader paths) | `LeaseReaderAsync` internal and attaches the handler; one `QueryAsync<T>`; every reader catch through `ReaderQuery.Translate`; `PingAsync` wraps the lease |
| Vandox.Storage | `SchemaMigrator.cs` | `ReadVersionAsync` catch through `ReaderQuery.Translate` |

## Signatures (for the Dev's skeleton)

```csharp
// src/Vandox.Storage/ReaderQuery.cs (new)
namespace Vandox.Storage;

/// Runs statements on reader connections so that a cancelled token interrupts them.
internal static class ReaderQuery
{
    /// The number of SQLite virtual-machine instructions between two checks of the token.
    internal const int ProgressInstructions = 1000;

    /// Installs the progress handler that interrupts the connection's running statement once the token is cancelled;
    /// does nothing for a token that cannot be cancelled.
    internal static void Attach(SqliteConnection connection, CancellationToken cancellationToken);

    /// Runs a query and maps its rows; an interrupted statement ends in OperationCanceledException, any other SQLite error in
    /// StoreException with the context.
    internal static Task<IReadOnlyList<T>> ReadAsync<T>(SqliteConnection connection, string sql, IReadOnlyDictionary<string, object> parameters, Func<SqliteDataReader, T> map, string context, CancellationToken cancellationToken);

    /// Maps a SQLite error of a reader call to the exception the store throws.
    internal static Exception Translate(SqliteException exception, string context, CancellationToken cancellationToken);
}

// src/Vandox.Storage/SqliteStore.cs (changed members)
internal async Task<ReaderLease> LeaseReaderAsync(CancellationToken cancellationToken);   // was private
private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Dictionary<string, object> parameters, Func<SqliteDataReader, T> map, string context, CancellationToken cancellationToken);
// replaces QueryAsync(string, Dictionary<string, object>, CancellationToken) and QueryTemplatesAsync<T>(...); public members unchanged
```

`ReaderQuery` is a plain static class; the skeleton bodies throw `NotImplementedException` under the skeleton pragma
(`.squad/stack.md`, *Skeleton*). The `SqliteStore` and `SchemaMigrator` rewrites are implementation (step 6); in the skeleton only
`LeaseReaderAsync` changes to `internal` so the tests compile.

## Test files

- `tests/Vandox.Storage.Tests/ReaderQueryTests.cs` (new) — AC1–AC5.
- `tests/Vandox.Storage.Tests/SqliteStoreQueryTests.cs` (existing) — AC6, and the pin `SqliteStoreSearchLogsThrowsCanceledForCancelledToken` (AC7).
- `tests/Vandox.Storage.Tests/SqliteStoreTemplateTests.cs` (existing) — the pin `SqliteStoreTemplateSpikesThrowsCanceledForCancelledToken` (AC7).

Step 5 writes the AC1–AC6 tests (they fail on the skeleton: `Attach`/`ReadAsync`/`Translate` throw, and the lease test's statement
runs to its end without a handler); the two AC7 pins wait for step 6. Every test that needs a cancellable token links it to
`TestContext.CancellationToken` or creates its own source; a test of a 20,000,000-row statement takes at most about a second only when
it fails.

Existing test code that calls a changed signature: none (only private members and the accessibility of `LeaseReaderAsync` change).

## Areas

- `docs/areas/storage.md` — *Database file and connections*: the reader handler, its scope, granularity and the exception; *Reading*
  and *Log search*: the deadline now interrupts. Owner: Dev.
- `docs/areas/detection-and-reports.md` — *Limits of templates*: the usual-level query is now stopped by a deadline. Owner: Dev.

## Documentation updates

All by the **Dev** in step 6, with these texts (wording may be adjusted to the surrounding lines, not the content):

1. `docs/areas/storage.md`, *Database file and connections*, a new bullet after the one on the writer and readers:
   "- A **cancelled token or a passed deadline interrupts a running reader statement.** Every reader connection gets a SQLite progress
   handler bound to the token of its call, checked every 1,000 virtual-machine instructions, so it also stops the work SQLite does
   before the first row (a sort, an aggregate, the `MATCH` of a search). The call then ends with `OperationCanceledException` carrying
   that token and no partial result; every other SQLite error stays a `StoreException` with the message of its operation. A token
   that cannot be cancelled installs no handler. Work inside one instruction (for example one call into the FTS5 module) finishes
   before the next check. The writer connection is never interrupted: a batch, an incident save, an import step and a migration run to
   their commit or rollback."
2. `docs/areas/storage.md`, *Reading*, the template bullet: replace "and a cancellation token does not interrupt a running statement
   (*Limits of templates* there)" with "; a cancelled token or deadline interrupts it (*Database file and connections*; *Limits of
   templates* there)".
3. `docs/areas/storage.md`, *Log search*, last bullet: replace "The bound is time: the search honors its cancellation token, so every
   caller that serves a user must pass a deadline." with "The bound is time: a cancelled token or a passed deadline interrupts the
   running search (*Database file and connections*), so every caller that serves a user must pass a deadline."
4. `docs/areas/storage.md`, *Related decisions*: add "- [0097](../decisions/0097-reader-statements-interrupted-by-a-progress-handler-bound-to-the-token.md) — why a progress handler interrupts reader statements and why the interruption is an `OperationCanceledException`."; *Implementation*: add `ReaderQuery`.
5. `docs/areas/detection-and-reports.md`, *Limits of templates*, usual-level bullet: replace "A deadline or cancellation does **not**
   stop a running query (the SQLite library used does not interrupt a statement): a caller that serves a user must not rely on a
   deadline alone, but narrows the range and the source and runs the query off the request path." with "A deadline or cancellation
   stops a running query (storage, *Database file and connections*) with no partial result: a caller that serves a user bounds it
   with a deadline and narrows the range and the source so that it can finish within it."
6. `.squad/project.md`, security area 10 (product description, allowed in a product PR): replace "and a deadline does not interrupt a
   running statement, so a caller that serves a user must not rely on a deadline alone (record 0095)" with "; a cancelled token or
   deadline interrupts a running reader statement (`ReaderQuery`: a progress handler on every reader connection, checked every
   1,000 instructions), which then ends with `OperationCanceledException`, so a caller that serves a user bounds it with a deadline
   (records 0095, 0097)", and add 0097 to the record list at the end of area 10.

Done by the **Lead** in this step (in the plan commit): record 0097 (Proposed) and its index row; record 0095 option 15 and its
consequence and record 0066's consequence point to 0097 (both unreleased, edited in place); `docs/ARCHITECTURE.md` *Log search*
names the interruption and links 0097.

## Architecture check

No guarantee of `.squad/project.md` changes. The agent guarantee *A hanging collector or database never blocks the agent* is Go-side
and untouched. The storage flow keeps one writer and the query-only readers; reads still never wait for the writer. *No data gaps*:
the writer is not interrupted, so no committed or acknowledged batch can be cut. The change strengthens security area 10's "never a
hang" for the queries that read external text: a deadline now bounds them.

## Security considerations

- [x] Limits on parsed input: no limits (no new input; the existing search and template limits are unchanged).
- [x] Exceptions that can reach a user-visible message: `OperationCanceledException` with `<context>: cancelled` — no caller shows it:
  `/healthz` answers `unavailable` and logs the type name only (`HealthEndpoint`), `PingChecker` turns it into `the database did not
  answer within the timeout`, `vandoxd incidents` and `vandoxd import` log "interrupted". `StoreException` texts are unchanged.
- [x] Native callback safety: the progress callback never throws and touches only the boxed token; SQLitePCLRaw keeps the delegate
  alive for the connection (`hook_handles`), and the connection is disposed with the lease, so no callback outlives it.
- [x] No cross-thread call into SQLite (option 2 rejected for this reason).

## Decision records

- [`docs/decisions/0097-reader-statements-interrupted-by-a-progress-handler-bound-to-the-token.md`](../../docs/decisions/0097-reader-statements-interrupted-by-a-progress-handler-bound-to-the-token.md) (Proposed, indexed).
- 0095 (option 15, consequence) and 0066 (consequence) edited in place to point to 0097 (unreleased: no `v*` tag exists).

## Out of scope / follow-ups

- Re-measuring the usual-level query times in `docs/BENCHMARKS.md` with the handler (the benchmark passes a cancellable token, so the
  callback cost applies there; expected small, not measured). No follow-up issue unless the Reviewer or Security asks for one.
- Interrupting writer statements (option 4).
- A caller that serves a user (logs view, web UI) does not exist yet; it must pass a deadline, as the area documents say.

## Challenge notes

Minor objections of the plan challenge, verbatim:

1. **AC3 test cost (minor).** `ReaderQueryReadCompletesWhenNotCancelled` runs the 20,000,000-row CTE to its end, once per token variant and per statement form. With a handler installed that is seconds each, and the suite should stay fast. Tester: use a smaller completion statement for the "never cancelled" cases, about 1–2 million rows. It only has to run through many progress callbacks (one per 1,000 instructions) without being interrupted. Keep the 20,000,000-row statement only for the cancelled cases, where it ends early.
2. **Reader disposal after an interrupt (minor).** The reader and command are disposed inside the `try` that catches `SqliteException`. Dev: add a test, or at least a check, that `ReaderQuery.ReadAsync` still throws the intended `OperationCanceledException` if disposing the reader after an interrupted step raises a `SqliteException`. If it can, dispose outside the translating catch or swallow it deliberately. The error text must not change in that case.

Non-blocking notes of the plan security review:

1. The deadline does not cover lock waits (existing behavior): the progress handler never runs while a reader waits on a lock. Documentation update 1 may say the bound applies while the statement runs, not while it waits for a lock.
2. A `SqliteException` raised while `PingAsync` opens the reader lease now surfaces as `store: reading schema version: …` instead of `store: opening database: …`. Nothing in the backend or its tests depends on the inner text; the Reviewer notes it.
