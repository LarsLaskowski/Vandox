# Plan: Rebuild the log template statistics from stored lines

Source: Issue #209
Status: Draft
Tier: security — the rebuild is a new consumer of stored external text (every stored log line, forged ones included) whose memory
and transaction bounds are the point of the change, the same kind of bound security area 10 of `.squad/project.md` lists for the
template normalizer and the incident reconstruction; it also adds a sub-command and a second writer of the template tables next to the
write path.

## Problem / root cause

The template statistics (`log_templates`, `log_template_hours`, `log_source_hours`) are written only by the storage write path:
`BatchWriter.WriteLogLine` counts each stored line (`src/Vandox.Storage/BatchWriter.cs:235`, `_tally.Add`) and
`SqliteStore.WriteBatchAsync` writes the sums in the batch's transaction (`src/Vandox.Storage/SqliteStore.cs:344`,
`writer.WriteTemplates`). Schema step 8 (`src/Vandox.Storage/SchemaMigrator.cs:107-139`) only creates the three tables, empty. Nothing
ever counts lines that were stored before, or recounts them under changed rules.

Claims of the issue, checked against the code:

- "Log template statistics (issue #22, record 0095) are kept by the write path" — **confirmed** (lines above; storage area, *Writing*).
- "After an upgrade from schema version 7 … the template tables only cover lines stored afterwards" — **confirmed**: step 8 creates
  empty tables and writes no rows; `docs/areas/detection-and-reports.md` (*Statistics*) states it. Note: no release tag exists yet
  (`git tag` is empty), so only pre-release databases are affected by this case; the rule-change case remains for every database.
- "… or after a change of the normalization rules" — **confirmed**: `LogTemplateNormalizer.Normalize` is applied only in
  `TemplateTally.Add` on the write path; 0095 *Consequences* names the rebuild as a follow-up.
- "the three template tables" — **confirmed**: exactly the three tables above.
- "Decide whether it runs as a sub-command or as a migration step" — decided: a sub-command, `vandoxd templates` (0095 options 16–18).
  A migration step is one transaction by construction (`SchemaMigrator.ApplyStepAsync`, `SchemaMigrator.cs:212-249`: statements and
  version in one transaction), which contradicts "bounded transactions".

Related limitation found on the way (not a defect today): the statistics are history kept 3 years while log lines are kept 90 days
(planned retention, #46). A rebuild counts only lines still stored, so once retention exists a rebuild would lose the history of
expired lines. Retention is not implemented; the limit is recorded in 0095 and in the storage area's *Retention (planned)* paragraph as
an open point for #46.

## Acceptance criteria

Storage (`SqliteStore.RebuildTemplatesAsync`), tests in `tests/Vandox.Storage.Tests/SqliteStoreTemplateRebuildTests.cs`. "Equal to the
write path" always means: the three tables compared **by key, not by `id`** — `log_templates` as (source, program, template,
first_seen, last_seen, count), `log_template_hours` joined to its template as (source, program, template, hour, count),
`log_source_hours` as (source, hour, count), each ordered — against the same lines written through `WriteBatchAsync` into the tables.

- [ ] AC1: After the three tables were emptied (the state of a database upgraded from schema version 7), a rebuild with
  `batchLines` 2 restores exactly what the write path had written for the stored lines. The lines cover two sources, two programs and
  three hours, one line written in a later batch with an earlier capture time, a resent agent record (stored once, counted once), a
  metric record between log lines (no template), a message with numbers and an IP address, a message longer than 1,024 UTF-8 bytes
  (template cut), and a message containing a lone surrogate U+D800 (the stored text holds U+FFFD; the template must be the same as the
  write path's). Result: `Lines` = number of stored log lines, `Batches` = ceil(lines / 2), `Templates` = rows of `log_templates`;
  afterwards `meta` holds no row with key `template_rebuild` (the run marker is removed by a completed run).
  Test: `SqliteStoreRebuildTemplatesRestoresWritePathStatistics`.
- [ ] AC2: Stale statistics are replaced: rows that no stored line produces (a template of "old rules" with its hour, a source hour of a
  source without lines) are gone, and counts, first and last seen changed by hand are back to the write path's values.
  Test: `SqliteStoreRebuildTemplatesReplacesStaleStatistics`.
- [ ] AC3: The lines are counted in batches of at most `batchLines`, one committed transaction each, in record-ID order: 5 log lines
  with `batchLines` 2 call `afterBatch` with 2, 4 and 5 (the lines counted so far), and the result is `Lines` 5, `Batches` 3. Inside the
  callback after the first batch, a reader of the database file (a separate `SqliteConnection`) sees the first batch's counts only
  (sum of `log_templates.count` = 2), which shows each batch is committed on its own.
  Test: `SqliteStoreRebuildTemplatesCountsInBatchesOfAtMostBatchLines`.
- [ ] AC4: A database without log lines (metrics only, and stale template rows) ends with all three tables empty, result `Lines` 0,
  `Batches` 0, `Templates` 0, and the callback is never called.
  Test: `SqliteStoreRebuildTemplatesWithoutLogLinesEmptiesTables`.
- [ ] AC5: Lines stored by the write path **during** a rebuild are counted exactly once: 4 lines, `batchLines` 2; after the first batch
  the callback writes a batch of 2 further lines through `store.WriteBatchAsync` (one of a template the rebuild already counted, one of
  a template it has not reached yet). Afterwards the tables equal those of a second database into which all 6 lines were written
  through the write path, and the result's `Lines` is 4 (only lines up to the high-water ID). The callback runs outside the writer
  lock, so the write does not deadlock. Test: `SqliteStoreRebuildTemplatesCountsLinesWrittenDuringRebuildOnce`.
- [ ] AC6: A rebuild cancelled after its first batch (the callback cancels the token) throws `OperationCanceledException`, keeps the
  committed first batch (sum of counts = 2) and writes nothing of the second; the `template_rebuild` row stays in `meta` (the
  incomplete state is recorded in the database); a following complete rebuild restores the write path's statistics and removes the
  row. Test: `SqliteStoreRebuildTemplatesCancelledKeepsCommittedBatchesAndRerunRestores`.
- [ ] AC7: `batchLines` outside 1 to 20,000 (`StorageLimits.MaxBatchRecords`) — 0, -1 and 20,001 — throws
  `ArgumentOutOfRangeException` before anything is written (the tables keep their rows); 1 and 20,000 are accepted.
  Test: `SqliteStoreRebuildTemplatesRefusesBatchLinesOutOfRange` (data rows).
- [ ] AC8: A database failure is a `StoreException` whose message starts with `store: rebuilding templates: ` (the test drops
  `log_source_hours` through a separate connection first). Test: `SqliteStoreRebuildTemplatesWrapsDatabaseFailure`.
- [ ] AC13 (numbered after the sub-command criteria so the earlier numbers stay stable): two runs at once never count a line twice;
  the run that cleared last wins and the other fails. 6 log lines, run A with `batchLines` 2.
  (a) A's callback, after A's first batch, awaits a complete run B (`batchLines` 2, no callback) on the same store. A then throws
  `StoreException` whose message is exactly `store: rebuilding templates: superseded by another rebuild`, its callback was called once,
  and the tables equal the write path's for the 6 lines (B's counts, nothing of A added on top), with no `template_rebuild` row in
  `meta`. Test: `SqliteStoreRebuildTemplatesSupersededByCompletedRunFails`.
  (b) A's callback, after A's first batch, runs B whose own callback cancels B after B's first batch (B throws
  `OperationCanceledException`). A then throws the same `StoreException` (the marker is present but B's), the sum of
  `log_templates.count` is 2 (B's first batch only), and the `template_rebuild` row is present; a third, complete run restores the
  write path's statistics and removes the row. Test: `SqliteStoreRebuildTemplatesSupersededByInterruptedRunFails`.
  (c) Run A with `batchLines` 10 (one batch holds all 6 lines, so A's callback runs after its last batch and before its finish
  transaction); the callback runs B whose own callback cancels B after B's first batch (`batchLines` 2). A then throws the same
  `StoreException`, its callback was called once, the `template_rebuild` row is still present (B's marker, not deleted by A's finish)
  and the sum of `log_templates.count` is 2 (B's first batch only). Test: `SqliteStoreRebuildTemplatesSupersededBeforeFinishFails`
  (added at PR approval for the Security diff finding N1).
  The guard these tests pin: without the marker check in the batch transaction, (a) ends with counts above the write path's and (b)
  with a sum above 2; without the marker check in the finish transaction, (c) returns normally and deletes B's marker, so the
  incomplete statistics would no longer be recorded.

Sub-command `vandoxd templates`:

- [ ] AC9: Parsing, in `tests/Vandox.Backend.Tests/CommandLineTests.cs`, test `CommandLineParsesTemplatesSubCommand` (the same cases as
  `CommandLineParsesIncidentsSubCommand`): `templates` sets `Templates`, `UsageFor` `templates`, default config path, no error, no
  import, `Incidents` false; `-config x templates`, `templates -config x` and `templates -config=x` set the path; `templates -h` sets
  `Help` with `UsageFor` `templates`; `templates extra` → `vandoxd templates: no argument is expected`; `templates -verbose` →
  `flag provided but not defined: -verbose`; `-healthcheck templates` → `unexpected argument "templates"`; `templates -config` →
  `flag needs an argument: -config`. `CommandLine.Usage("templates")` is exactly
  `Usage of vandoxd templates [-config file]:\n  -config string\n    \tpath of the configuration file\n`, and `CommandLine.Usage("")`
  contains `  vandoxd [flags] templates [-config file]\n    \trebuild the log template statistics from the stored log lines\n`.
- [ ] AC10: A run rebuilds and prints the summary: with 3 stored log lines of 2 templates and emptied template tables,
  `vandoxd templates` exits 0, standard output is exactly
  `Log lines:           3\nBatches:             1\nTemplates:           2\n`, standard error contains `template rebuild started` and
  `template rebuild progress`, and the tables equal the write path's. Test file `tests/Vandox.Backend.Tests/TemplatesCommandTests.cs`,
  test `TemplatesCommandRebuildsAndPrintsSummary`.
- [ ] AC11: Failures exit 1 with nothing on standard output: an invalid configuration (`configuration invalid` logged) —
  `TemplatesCommandExitsOneOnInvalidConfiguration`; a storage directory that does not exist (`opening database failed`) —
  `TemplatesCommandExitsOneWhenStorageCannotBeOpened`; a token cancelled before the run (`template rebuild interrupted`) —
  `TemplatesCommandExitsOneWhenInterrupted`; a token cancelled during the run, after `template rebuild started` was logged
  (`template rebuild interrupted`, no summary) — `TemplatesCommandExitsOneWhenInterruptedDuringRun`; a store failure during the run
  (`log_source_hours` dropped: `"msg":"template rebuild failed"` and `store: rebuilding templates: ` in the log, no summary) —
  `TemplatesCommandExitsOneWhenStoreFailsDuringRun`.
- [ ] AC12: `vandoxd incidents` behaves as before after its start-up moved into the shared helper: the existing
  `IncidentsCommandTests` and `CommandLineTests` pass unchanged (no new test).

## Approach

**Storage.** New internal static class `TemplateRebuilder` holds the SQL; `SqliteStore.RebuildTemplatesAsync` runs it:

1. Validate `batchLines` (1 to `StorageLimits.MaxBatchRecords`; `ArgumentOutOfRangeException`). Create the **run marker**: 16 random
   bytes from `RandomNumberGenerator.GetBytes` as upper-case hex (`Convert.ToHexString`), 32 characters, new for every run.
2. **Clear transaction** (writer lock + `BeginTransactionAsync(Serializable)`, i.e. `BEGIN IMMEDIATE`, as every write):
   `DELETE FROM log_template_hours`, `DELETE FROM log_templates`, `DELETE FROM log_source_hours` (this order: the hours reference the
   templates), the marker written as `INSERT INTO meta(key, value) VALUES ('template_rebuild', $marker) ON CONFLICT(key) DO UPDATE SET
   value = excluded.value`, then `SELECT coalesce(max(id), 0) FROM records` as the **high-water ID**; commit.
3. **Batches**, `after` starting at 0: per batch take the writer lock, begin a transaction, first read
   `SELECT value FROM meta WHERE key = 'template_rebuild'`; when the row is missing or its value is not ordinally equal to the run's
   marker, roll back and throw
   `StoreException("store: rebuilding templates: superseded by another rebuild")` (nothing of the batch is counted). Otherwise run
   `SELECT l.record_id, r.source, l.program, l.message, r.captured_at FROM log_lines l JOIN records r ON r.id = l.record_id
   WHERE l.record_id > $after AND l.record_id <= $high ORDER BY l.record_id LIMIT $limit`, feed every row into a new
   `TemplateTally` for this transaction (`Add(source, program, message, captured_at)`, the write path's own counting code, unchanged),
   dispose the reader, `tally.Write`, commit, release the lock. Then, **outside the lock**, add the lines, count the batch, set `after`
   to the last record ID and call `afterBatch(linesSoFar, token)` when it is not null. A batch with 0 lines rolls back and ends the
   loop; a batch with fewer than `batchLines` lines is the last. `after` grows strictly (`>` and `ORDER BY`), so the loop ends.
4. **Finish transaction** (writer lock, `BEGIN IMMEDIATE`): the same marker check (superseded → the same `StoreException`), then
   `DELETE FROM meta WHERE key = 'template_rebuild'` and `Templates` = `SELECT count(*) FROM log_templates`; commit.
5. `SqliteException` anywhere → `StoreException("store: rebuilding templates: " + exception.Message, exception)`. Cancellation:
   the token is checked before each batch and by the reader (`ReadAsync(token)`) and `TemplateTally.Write`; an uncommitted batch rolls
   back with its transaction. An interrupted or failed run leaves the marker row in `meta`: the database itself records that the
   statistics are incomplete (from the clear until a run completes); the next run overwrites it.

Why this is correct next to the write path (0095 option 19): a record stored after the clear gets an ID above the high-water ID
(`records.id` is the row ID; nothing deletes records), so the write path counts it and the rebuild never reads it; a record stored before
the clear has an ID up to the high-water ID, its earlier counts were deleted in the clear, and the rebuild counts it once. Both writers
only add counts and widen first and last seen (`TemplateTally`'s upserts), so their order does not matter. In-process writers are
serialized by `_writeLock`, other processes by `BEGIN IMMEDIATE`.

Why this is correct next to **another rebuild** (0095 option 22): `_writeLock` and `BEGIN IMMEDIATE` serialize single transactions, not
runs. Every rebuild transaction that writes counts first checks, inside that transaction, that the marker is its own. A later run's
clear deletes every count an earlier run committed and replaces the marker in the same transaction, so after it the earlier run can
commit no further count: its next batch or finish transaction finds a foreign or missing marker and fails with exit 1. The counts
present at any time therefore come from the run that cleared last plus the write path, each line once. Without the guard, run A
(high-water H, cursor C) would add (C, H] again on top of a run B started meanwhile, and both would exit 0 (Security finding 1).

**Batch size.** `StorageLimits.TemplateRebuildBatchLines = 10000`. The bound on how long a batch holds the writer lock is by
construction, not by a measurement: a batch reads and normalizes at most 10,000 stored messages of at most 16 KiB each
(`ModelLimits.MaxTextBytes`, about 160 MiB in the forged worst case) and runs at most 3 × 10,000 upserts, which is no more work than the
write path's largest batch (20,000 such records, normalized and counted the same way, plus the inserts of the records, the log lines and
their `log_fts` entries). Whatever lock time that largest batch has on a host, a rebuild batch stays below it; whether it stays below
another process's 5-second busy timeout on the reference host is the same open measurement as for the write path (`docs/BENCHMARKS.md`,
*Log templates*; the development host's medians of 504 ms and, hostile case, 509 ms per batch are for short messages, not the forged
worst case).

**Sub-command.** `vandoxd [flags] templates [-config file]`, parsed and dispatched like `incidents`. Its start-up (load the
configuration, create the logger, open the store, map `StoreException`/cancellation at open) is the same as `IncidentsCommand`'s, so it
moves into a shared helper `StoreCommand.RunAsync` that both commands call (no duplicated block for SonarQube). `TemplatesCommand` logs
the start, logs progress after every batch, writes the summary to standard output and returns 0; `StoreException` → the failure log line,
1; cancellation → the interruption log line, 1.

## Options considered

Recorded in [0095](../../docs/decisions/0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md),
options 16–23:

- **Sub-command** (chosen) vs **migration step** (one transaction per step by rule, so either unbounded or a step that commits before its
  version; delays every opener; a rule change is no schema change and would need a step each time) vs **automatic at service start on a
  stored rules version** (a hand-kept rules version and a background job the service does not have; incomplete statistics nobody sees).
- **In place behind a high-water record ID** (chosen) vs **shadow tables swapped in at the end** (one swap transaction as large as the
  statistics plus a catch-up, holding the writer lock past other processes' busy timeout; renamed tables cannot keep the index names and
  schema text of a migrated database).
- **Start over on every run** (chosen) vs **resume from a cursor in `meta`** (state and a rule for a resume by a build with other rules,
  to save minutes).
- **A run marker in `meta`, checked in every counting transaction** (chosen, added after the Security review) vs **a documented rule
  "never run two at once"** (two runs would both exit 0 with lines counted twice, which nobody notices; the guard costs one row and one
  read per batch) vs **a lock file or an advisory lock held for the whole run** (SQLite has no lock that outlives a transaction; a lock
  file needs stale-lock handling after a crash, which the marker does not: a stale marker only makes the next run overwrite it).
- Reuse of `TemplateTally` (one option: a second counting implementation could drift from the write path, which AC1 pins).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `TemplateRebuilder.cs` (new) | the run marker and the SQL of the clear, the marker check, the batch and the finish |
| Vandox.Storage | `TemplateRebuildResult.cs` (new) | result of a rebuild |
| Vandox.Storage | `SqliteStore.cs` | new public method `RebuildTemplatesAsync` (region *Methods*) |
| Vandox.Storage | `StorageLimits.cs` | new constant `TemplateRebuildBatchLines` |
| Vandox.Storage | `TemplateTally.cs`, `BatchWriter.cs` | read and final: unchanged |
| Vandox.Backend | `Cli/TemplatesCommand.cs` (new) | the sub-command |
| Vandox.Backend | `Cli/StoreCommand.cs` (new) | shared start-up of `incidents` and `templates` |
| Vandox.Backend | `Cli/IncidentsCommand.cs` | `RunAsync` calls `StoreCommand.RunAsync`; signature and behavior unchanged |
| Vandox.Backend | `Cli/CommandLine.cs` | `templates` sub-command, usage texts |
| Vandox.Backend | `Cli/CommandOptions.cs` | property `Templates`; `UsageFor` doc names `templates` |
| Vandox.Backend | `Hosting/BackendApp.cs` | dispatch `options.Templates` to `TemplatesCommand.RunAsync` (after `Incidents`) |
| Vandox.Backend | `Hosting/BackendLog.cs` | four `[LoggerMessage]` methods, event IDs 34–37 |

## Signatures (for the Dev's skeleton)

`src/Vandox.Storage/StorageLimits.cs`:

```csharp
/// <summary>
/// The number of stored log lines a template rebuild counts in one transaction.
/// </summary>
public const int TemplateRebuildBatchLines = 10000;
```

`src/Vandox.Storage/TemplateRebuildResult.cs`:

```csharp
namespace Vandox.Storage;

public sealed class TemplateRebuildResult
{
    public long Lines { get; init; }      // stored log lines counted (record IDs up to the high-water ID)
    public int Batches { get; init; }     // committed batch transactions
    public long Templates { get; init; }  // rows of log_templates after the last batch
}
```

`src/Vandox.Storage/SqliteStore.cs`:

```csharp
/// Rebuilds the template statistics from the stored log lines: empties the three tables, writes the run marker and fixes the
/// high-water record ID in one transaction, then counts the lines up to it in batches of at most batchLines, one transaction each,
/// and removes the marker; a run whose marker was replaced by another rebuild fails.
/// <exception cref="ArgumentOutOfRangeException">batchLines is outside 1 to StorageLimits.MaxBatchRecords</exception>
/// <exception cref="StoreException">The database fails, or another rebuild superseded this one</exception>
public async Task<TemplateRebuildResult> RebuildTemplatesAsync(int batchLines, Func<long, CancellationToken, Task>? afterBatch, CancellationToken cancellationToken)
```

`src/Vandox.Storage/TemplateRebuilder.cs`:

```csharp
internal static class TemplateRebuilder
{
    internal const string MarkerKey = "template_rebuild";   // key of the run marker in meta
    internal static string NewMarker();   // 16 random bytes (RandomNumberGenerator) as 32 upper-case hex characters
    internal static Task<long> ClearAsync(SqliteConnection connection, SqliteTransaction transaction, string marker, CancellationToken cancellationToken);   // empties the three tables, writes the marker, returns the high-water record ID
    internal static Task<bool> HoldsMarkerAsync(SqliteConnection connection, SqliteTransaction transaction, string marker, CancellationToken cancellationToken);   // false when the row is missing or holds another value
    internal static Task<(long LastId, int Lines)> CountBatchAsync(SqliteConnection connection, SqliteTransaction transaction, long afterId, long highId, int batchLines, CancellationToken cancellationToken);   // (afterId, 0) when no line is left
    internal static Task<long> FinishAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken);   // deletes the marker row, returns the rows of log_templates
}
```

`SqliteStore.RebuildTemplatesAsync` throws the superseded `StoreException` itself when `HoldsMarkerAsync` returns false (before
`CountBatchAsync` and before `FinishAsync`), so every message of the rebuild is built in one method.

`src/Vandox.Backend/Cli/StoreCommand.cs`:

```csharp
internal static class StoreCommand
{
    /// Loads the configuration, creates the logger and opens the store, then runs the command on it; logs and returns 1 when the
    /// configuration is invalid (ConfigurationInvalid), the store cannot be opened (OpeningDatabaseFailed) or the open is cancelled
    /// (logInterrupted).
    internal static Task<int> RunAsync(string configPath, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stderr, ServeHooks hooks, Action<ILogger> logInterrupted, Func<SqliteStore, BackendConfig, ILogger, TimeProvider, Task<int>> run, CancellationToken cancellationToken);
}
```

`src/Vandox.Backend/Cli/TemplatesCommand.cs`:

```csharp
internal static class TemplatesCommand
{
    internal static Task<int> RunAsync(string configPath, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken);
    internal static string FormatSummary(TemplateRebuildResult result);   // "Log lines:           {Lines}\nBatches:             {Batches}\nTemplates:           {Templates}\n", invariant culture
}
```

`src/Vandox.Backend/Cli/CommandOptions.cs`: `public bool Templates { get; set; }` ("whether the sub-command `templates` was given").

`src/Vandox.Backend/Cli/CommandLine.cs`: `private const string TemplatesName = "templates";` and a private parser for it; the Dev may merge
`ParseIncidents` and the new one into one private method that takes the name (keep `ParseArgument` under S3776). Public members unchanged.

`src/Vandox.Backend/Hosting/BackendLog.cs`:

```csharp
[LoggerMessage(EventId = 34, Level = LogLevel.Information, Message = "template rebuild started")]
internal static partial void TemplateRebuildStarted(this ILogger logger);

[LoggerMessage(EventId = 35, Level = LogLevel.Warning, Message = "template rebuild interrupted; the template statistics are incomplete until a rebuild completes")]
internal static partial void TemplateRebuildInterrupted(this ILogger logger);

[LoggerMessage(EventId = 36, Level = LogLevel.Error, Message = "template rebuild failed {Error}")]
internal static partial void TemplateRebuildFailed(this ILogger logger, Exception exception, string error);

[LoggerMessage(EventId = 37, Level = LogLevel.Information, Message = "template rebuild progress {Lines}")]
internal static partial void TemplateRebuildProgress(this ILogger logger, long lines);
```

## Test files

- `tests/Vandox.Storage.Tests/SqliteStoreTemplateRebuildTests.cs` (new; the store's tests are split by topic, as
  `SqliteStoreTemplateTests.cs`, `SqliteStoreIncidentTests.cs`): AC1–AC8 and AC13. Samples from `Samples.LogAt`, `Samples.Metric`,
  `Samples.AgentBatch`; tables read through a separate `SqliteConnection` on the file, as `SqliteStoreTemplateTests.RowsAsync`.
- `tests/Vandox.Backend.Tests/TemplatesCommandTests.cs` (new, `[OSCondition(OperatingSystems.Linux)]` like
  `IncidentsCommandTests`, using `BackendFixture`): AC10, AC11.
- `tests/Vandox.Backend.Tests/CommandLineTests.cs` (existing): AC9.

Existing test code that calls a changed signature: none. `IncidentsCommand.RunAsync`, `CommandLine.Parse/Usage` and `BackendApp.RunAsync`
keep their signatures; `CommandLineTests.CommandLineParsesIncidentsSubCommand` checks the main usage with `Contains`, which the added line
does not break.

## Areas

- `docs/areas/storage.md` (Dev): *Schema* — step 8's "so log lines stored before it have no template statistics" gets "until
  `vandoxd templates` rebuilds them"; *Writing* — "The template tables are written only by this path" becomes "only by this path and the
  rebuild"; the `meta` row of the *Tables* table becomes "key and value; the schema version, and the run marker `template_rebuild` from
  the start of a template rebuild until one completes"; a new bullet **Template rebuild** with the rules: clear transaction (three
  `DELETE`s, the run marker, high-water ID = the highest record ID), batches of at most 10,000 log lines by record ID up to it, one
  transaction each, each first checking the marker, the write path's counting, lines stored meanwhile counted by the write path, the
  finish transaction that removes the marker; two runs at once: the one that cleared last wins, the other fails with
  `store: rebuilding templates: superseded by another rebuild`; incomplete statistics during and after an interrupted run, recorded by
  the marker row left in `meta` — hours then may hold **partial counts** (batches go by record ID, not by time), so they look covered
  with counts too low, not unknown; every run starts over; the clear transaction grows with the statistics: it holds the writer lock
  that long (another process may hit its busy timeout and is run again) and writes the deleted pages to the WAL, which grows by about
  the size of the three tables until the next checkpoint; the `store: rebuilding templates: ` errors;
  *Retention (planned)* — the open limit for #46: a rebuild counts only lines still stored, so after retention it would lose the
  history of expired lines (retention must make the rebuild refuse or keep that history); *Implementation* — `TemplateRebuilder`.
- `docs/areas/detection-and-reports.md` (Dev): *Statistics*, last bullet — lines stored before schema version 8 are not counted
  until `vandoxd templates` runs; *Limits of templates* — replace "Rebuilding the statistics from the stored lines is not offered yet"
  with: after a rule change `vandoxd templates` recounts every stored line under the current rules, then `vandoxd incidents` is run
  again; plus one bullet: while a rebuild runs and after an interrupted one (the `template_rebuild` row in `meta`, storage area), hours
  may hold partial counts — they count as covered with counts too low, so new-template and usual-level results are wrong, not unknown,
  until a run completes (the "unknown" rule of *Queries* covers only hours with no line of the source, as after the schema-7 upgrade);
  *Implementation* — `TemplateRebuilder`, `SqliteStore.RebuildTemplatesAsync`.
- `docs/areas/backend-host.md` (Dev): *Command line and exit codes* — `templates` as a third positional sub-command; a bullet for
  `vandoxd [flags] templates [-config file]` with its flags, usage error text, no `-healthcheck`, the summary lines on standard output,
  exit 0 / 1 (interrupted: the statistics are incomplete until a run completes; run again, it starts over; superseded by a second run:
  `template rebuild failed` with `superseded by another rebuild`, the other run continues); *Implementation* /
  related decisions as the section lists them.

## Documentation updates

- `README.md` (Dev): the intro sentence of commands (lines 18–24) names `vandoxd templates`; a new section **Rebuild template
  statistics** after *Reconstruct incidents*: when to run it (after upgrading a database from before the template statistics, or after
  a release that changes the normalization rules), `docker exec -it vandoxd /vandoxd templates`, the summary example, and that
  `vandoxd incidents` is run again afterwards; best run while no import runs; start it once: a second run started meanwhile makes the
  first one stop with `superseded by another rebuild` (exit 1) and itself counts everything, so after an exit 1 of any kind, run it
  again until it exits 0.
- `.squad/project.md` (Dev), security area 10: one sentence — `vandoxd templates` (`SqliteStore.RebuildTemplatesAsync`,
  `TemplateRebuilder`) reads stored log lines in batches of 10,000 by record ID up to the high-water ID fixed when the tables are
  emptied, one transaction each, with the write path's normalizer and tally, so a batch's tally holds at most `batchLines` keys of
  source (at most 128 UTF-8 bytes), program (at most 1,024) and template (at most 1,024), plus as many source hours, each with its own
  source string (about 46 MB of UTF-16 strings at 10,000, plus dictionary overhead), and reads one message (at most 16 KiB) at a time;
  every counting transaction checks the run marker in `meta`, so a run superseded by a later one counts nothing more and fails; the
  transaction that empties the tables grows with the statistics; add 0095 where the records are listed (it is already there).
  *Integration surface* needs no entry.
- `docs/ARCHITECTURE.md` (Lead, done in this plan commit): backend binary paragraph names `vandoxd templates`; the *Log templates*
  storage bullet names the rebuild.
- `docs/decisions/0095-…md` and its index row (Lead, done): extended with options 16–23, Decision and Consequences; status `Proposed`.

## Architecture check

- *No data gaps unless explicitly recorded*: during a rebuild and after an interrupted one the statistics are incomplete. Batches run
  in record-ID order, not time order, so an hour can hold a `log_source_hours` row with a **partial** count: it looks covered, with
  counts too low, and usual-level, spike and new-template results are **wrong, not unknown** (the "unknown" reasoning holds only for
  the schema-7 upgrade case, where an hour has no row at all). The incomplete state is recorded explicitly: in the database by the
  `template_rebuild` row in `meta` (from the clear until a run completes), and to the operator by exit code 1 and the warning line;
  the areas state it. No reader consults the marker yet and no alerting consumes templates; `vandoxd incidents` run during or after an
  interrupted rebuild carries that state, which is why the README says to run it again after a completed rebuild. Follow-up below:
  the first consumer that shows template results (web UI, alerting) checks the marker.
- *Backfilled data never raises an alert by itself*: untouched (no alerting).
- *A hanging collector never blocks the agent*: agent untouched.
- Storage invariants: `log_lines` and `log_fts` are not written; the one-writer rule and `BEGIN IMMEDIATE` are kept for every rebuild
  transaction; no schema change (version stays 9; the marker is a row of the existing `meta` table, which `SchemaMigrator` reads only
  by the key `schema_version`).

## Security considerations

- [x] Byte or length limits: no new input is parsed. The messages are the stored ones; the template is cut at 1,024 UTF-8 bytes by the
  unchanged normalizer, after the text is a .NET string (a lone surrogate counts as U+FFFD, 3 bytes, as on the write path).
  Memory per batch: rows are read one at a time (one message of at most 16 KiB, `ModelLimits.MaxTextBytes`, live at a time); the
  tally (`TemplateTally.cs:29-31`) holds at most `batchLines` (10,000) keys of (source ≤ 128 UTF-8 bytes, `Check.Name`; program
  ≤ 1,024, `Check.Short`; template ≤ 1,024), as many template hours (value keys) and as many source hours, whose key keeps a source
  string of its own because the reader returns a new string per row. Worst case 10,000 × (1,024 + 1,024 + 128) × 2 B + 10,000 × 128
  × 2 B ≈ 46 MB (44 MiB) of UTF-16 strings plus dictionary overhead, linear in `batchLines`, which AC3 pins; nothing is kept across
  batches but three counters and the marker. Transactions: each batch reads at most 10,000 lines (at most about 160 MiB of forged
  message text) and runs one marker read plus at most 3 × 10,000 upserts, no more than the write path's largest batch (see *Batch
  size*); the clear transaction is three whole-table `DELETE`s and grows with the statistics (stated, not bounded): it holds the writer
  lock that long and grows the WAL by about the size of the three tables until the next checkpoint. The loop terminates (`after`
  strictly increases, bounded by the high-water ID). Concurrent runs: the run marker makes a superseded run fail before it counts
  again (AC13), so no line is counted twice.
- [x] Exceptions that reach a user-visible message: `StoreException` `store: rebuilding templates: <SQLite message>` (no payload value)
  in the `template rebuild failed` log line, as a JSON string through the JSON line provider; configuration and open failures through
  the existing `configuration invalid` / `opening database failed` lines. Standard output carries only the three numbers. No stored text
  (template, message, source, program) is printed or logged; the superseded message is a constant, and the marker value is never
  printed or logged.

## Decision records

- `docs/decisions/0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md` — extended in place
  (unreleased): options 16–23, the decision for the sub-command, the in-place rebuild and the run marker, consequences; status back to
  `Proposed`, index row `Proposed`.

## Out of scope / follow-ups

- Retention (#46) must account for the rebuild losing the history of expired lines (recorded in 0095 and the storage area).
- A benchmark of the rebuild on the reference host: not a criterion of the issue; the per-batch cost is the write path's measured
  template cost minus the inserts of the lines.
- Running the rebuild automatically (0095 option 18): to be revisited with the ingest service.
- The first consumer that shows template results to a user (web UI, alerting) must check the `template_rebuild` marker and show the
  results as incomplete while it is present (recorded in 0095 *Consequences*); nothing reads templates for a user yet.

## Challenge

The Devil's Advocate returned no objections.

## Security review

Plan review at head 2d316a0, verdict CHANGES_REQUIRED; every finding answered:

1. **Two rebuilds at once double-count (blocking)** — accepted, guarded as recommended: a random run marker in `meta`, written in the
   clear transaction and checked inside every batch and the finish transaction; a run that finds a missing or foreign marker fails with
   `store: rebuilding templates: superseded by another rebuild` (exit 1) before it counts again, and the run that cleared last wins
   (*Approach*, steps 1–4 and "Why this is correct next to another rebuild"; AC13 with an interleaved run that completes and one that
   is interrupted; AC1 and AC6 pin the marker's removal and its survival; 0095 option 22). README, storage and backend-host areas
   state the behavior.
2. **Memory bound left out source and program (blocking)** — accepted: *Security considerations* now bounds the whole key and the
   source hours' own source strings (about 46 MB of UTF-16 strings at 10,000, linear in `batchLines`), and the `.squad/project.md`
   sentence is phrased as a bound on the whole key plus the source hours.
3. **Batch time rested on the median (non-blocking)** — accepted: *Batch size* bounds a batch by construction against the write
   path's largest batch with forged 16 KiB messages and names the reference-host measurement as open, as for the write path.
4. **"Unknown, never quiet" is wrong for the rebuild (non-blocking)** — accepted: the *Architecture check* now says partial counts are
   wrong, not unknown; the incomplete state is recorded by the marker row, exit code and warning line; the detection area gets a
   bullet saying so; 0095 keeps "unknown" only for the schema-7 upgrade case.
5. **WAL growth of the clear transaction (non-blocking)** — accepted: named in *Security considerations*, the storage area bullet and
   0095 *Consequences*.
6. **Retention makes the rebuild lossy (non-blocking)** — already an open point for #46 (0095, storage *Retention (planned)*);
   unchanged.

Re-check of the guard's cases (the marker is not a parser guard; its "forms" are the states of the `meta` row a counting transaction
can find): own marker → counts; another run's marker (a later run running, interrupted or failed) → superseded; no row (a later run
completed, or the row was deleted by hand) → superseded; the row's value is compared ordinally as the whole string, so a prefix or a
case variant is another marker.
