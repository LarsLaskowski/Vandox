# Plan: Rollups of process snapshots per program

Source: Issue #458 | [spec.md](spec.md)
Tier: standard — a new fold, schema step, removal rule and store read; it creates, changes or removes no security control (the caps and step bounds are input handling).
Security areas touched: 9 File writes and paths derived from external input (`vandox.db`, deletes through `RetentionRemover`), 10 Parsing of external input (the fold decodes stored agent and import payloads with hostile program names)

## Problem / root cause

See [spec.md](spec.md); S1 stores no last value or process count, as no read returns them (record 0223).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | A program carries `pss_bytes` only when every process of it has one | `internal/procstats/snapshot.go:71-79` | confirmed |
| 3 | `ProcessSnapshot.Validate` accepts processes without programs (absent or empty) and refuses a null entry, an empty or duplicate name; at most 4,096 programs, names up to 1,024 bytes; any finite non-negative `cpu_percent` (`Check.Rate`) | `ProcessSnapshot.cs:61-120`, `Check.cs:221-231`, `ModelLimits.MaxItems` | confirmed |
| 4 | Program names reach the store without unpaired surrogates (wire check; the import builds names from decoded text) | `src/Vandox.Core/Wire/WireTextCheck.cs`, `LegacyProcessTable.Build` | confirmed |
| 7 | A stored payload is up to 16 MiB (`StorageLimits.MaxPayloadBytes`) | `StorageLimits.cs:28` | confirmed |
| 8 | Without `INDEXED BY`, `kind = 'process_snapshot' AND id > ? ORDER BY id` uses `records_kind_time` plus a sort; with a partial index `records(id) WHERE kind = 'process_snapshot'` named by `INDEXED BY` it is one index search | `EXPLAIN QUERY PLAN`, SQLite 3.53.1 (Python, scratch) | confirmed |
| 9 | The query's steps: 5, 10, 15, 30 min, 1, 2, 3, 6, 12 h, then whole days; steps below one hour read 5-minute rollups | `SeriesLadder`, `docs/areas/time-series-query.md:27-28` | confirmed |
| 11 | Which exception `JsonDocument.Parse` throws on invalid UTF-8 inside a string | to verify: the Tester's undecodable-row test with such bytes, step 5 | unverified |
| 13 | SQLite's `sum()` of several 1e308 values is Infinity; Inf − Inf is NULL in SQL | Security's measurement, plan review | confirmed |
| 14 | A store read runs with `ReaderQuery.Attach` (progress handler bound to the token) on its leased connection | `SqliteStore.cs:282`, `ReaderQuery.cs:24-33` | confirmed |
| 12 | Disk growth per day and the read's time on the reference database | to verify: AC7, Dev (development host) and Product Manager (DS918+), step 6 | unverified |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `SqliteStoreFoldRollupsFoldsProgramsIntoBothTiers`, `SqliteStoreFoldRollupsFoldsEachSnapshotOnce`, `SqliteStoreFoldRollupsFoldsImportedSnapshots`, `SqliteStoreFoldRollupsSkipsUndecodableSnapshotsAndCountsThem` (data `not json`, NULL, `{"programs":"x"}`, `{"programs":[null]}`, `{"programs":[{"program":null}]}`, invalid UTF-8; the next snapshot folds), `SqliteStoreFoldRollupsCountsIncompleteSnapshots` (rows: total equal, total different, total absent and `complete` false or true, `programs` absent, `programs` empty, counts whose sum passes 2^32), `SqliteStoreFoldRollupsStopsAtTheSnapshotBudgets` (501 rows; the byte budget), `SqliteStoreFoldRollupsSkipsProgramBucketsPastTheirTier`, `SqliteStoreFoldRollupsMergesPssIntoRssOnlyBucket` (RSS-only then PSS, and the reverse, in two steps: committed, RSS columns and totals right) | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC1 | `SqliteStoreOpenMigratesVersion15ToProgramRollups` (tables, indexes, stored snapshots folded before removal) | `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` |
| AC2 | `SqliteStoreProgramRollupsCountsMissingProgramAsZero` (minimum 0, averages add up to the total), `SqliteStoreFoldRollupsCutsToFiftyProgramsPerKey` (60 programs, a tie at rank 50 by ordinal name, a PSS snapshot cut by PSS), `SqliteStoreFoldRollupsCapsNamedProgramsInFoldOrder` (the 501st name into *other programs*, a named program stays named in a later step) | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC3 | `SqliteStoreRemoveExpiredKeepsUnfoldedProcessSnapshots`, `SqliteStoreRemoveExpiredRemovesSkippedUndecodableSnapshot`, `SqliteStoreRemoveExpiredRemovesConnectionSnapshotsWithoutProgramFold`, `SqliteStoreRemoveExpiredRemovesProgramBucketsPastTheirPeriods` (counts) | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC3 | `SqliteStoreFoldRollupsMergesLateSnapshotIntoBucket` (its snapshots removed), `SqliteStoreFoldRollupsCancelledStepCommitsNothing` | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC3 | `RetentionServiceLogsProgramRollupAndRemovalCounts` | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC4 | `SqliteStoreProgramRollupsReturnsTopProgramsByMeasure` (memory, CPU, ties), `SqliteStoreProgramRollupsReadsRssWhenRangeMixesPssAndRss` (and PSS for an all-PSS range, its denominator the PSS snapshots), `SqliteStoreProgramRollupsReturnsOthersAndIncompletePerStep`, `SqliteStoreProgramRollupsMarksStepsWithoutSnapshotsAsGaps`, `SqliteStoreProgramRollupsReadsTheTierOfTheStep` (30-minute step over a range starting before the 5-minute horizon and ending inside it: gaps, then values, flag set; a range wholly before it: no lines, flag set), `SqliteStoreProgramRollupsReturnsEarliestUnfoldedAndStartsBefore`, `SqliteStoreProgramRollupsReturnsEmptyResult` (unknown source, empty range, no bucket), `SqliteStoreProgramRollupsAlignsStepsToTheEpoch` (a `from` off a step boundary: the first time is the step holding it), `SqliteStoreProgramRollupsRejectsInvalidQuery` (count 0 and 51, step 7 minutes, a year at 5 minutes counted on the epoch grid, a bound outside the storable range: exact texts), `SqliteStoreProgramRollupsHonorsCancellation` (token cancelled while a statement runs: `OperationCanceledException` through `ReaderQuery`) | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC5 | `SqliteStoreProgramSourcesListsLatestBucketInRange` (two sources, a bucket after the range left out), `SqliteStoreProgramSourcesCapsAtTheLimit` (257 sources), `SqliteStoreProgramSourcesRejectsRangeOutsideStorableRange` | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC6 | `SqliteStoreFoldRollupsClampsOverflowingProgramSums`, `SqliteStoreProgramRollupsClampsOverflowingSums` (CPU 1e308 in several buckets of one step: every average, minimum, maximum and *other programs* finite), `SqliteStoreFoldRollupsStoresHostileProgramNamesAsReceived` (1,024 bytes, quotes, `%`, SQL text, `\u0000` ranked with its values, `\u0001`, `\u202e`, an emoji) | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC6 | `RetentionServiceLogsProgramCountsWithoutNames` | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC7 | manual: the Product Manager runs `--programs` of the storage benchmark on the DS918+ SSD volume, published from the branch head, after step 6; the Dev runs it on the development host first | — |

## Approach

**Schema step 16** (`SchemaMigrator`, `StorageLimits.SchemaVersion` 16): `program_rollups_5m` and `program_rollups_1h` (`source`,
`bucket`, `program`, `seen` (snapshots naming the program), `rss_min`, `rss_max`, `rss_sum`, `pss_min`, `pss_max` (NULL without a PSS
snapshot), `pss_sum`, `cpu_min`, `cpu_max`, `cpu_sum`; key (`source`, `bucket`, `program`), `STRICT, WITHOUT ROWID`) and
`program_rollup_totals_5m`/`_1h` (`source`, `bucket`, `snapshots`, `pss_snapshots`, `incomplete`, `named`, `rss_sum`, `pss_sum`,
`cpu_sum`; key (`source`, `bucket`)); an index on `bucket` of each of the four; `records_process_fold ON records(id) WHERE kind =
'process_snapshot'`. The watermark `program_rollup_after_id` is absent, so stored snapshots fold first (E8).

**Fold** (`ProgramRollupFolder.Fold`, called by `SqliteStore.FoldRollupsAsync` after `RollupFolder.Fold` in the same session; the
results combine, `More` when either has more): read `id, source, captured_at, data` `INDEXED BY records_process_fold` above the
watermark by ID, at most `ProgramRollupStepRows` (500), stopping after the row that takes the `data` bytes read (UTF-8, before
decoding) past `ProgramRollupStepBytes` (2 MiB); the first row is always taken. Each row is decoded alone (`JsonDocument` and
`PayloadRegistry.Deserialize`); NULL data, any exception of decoding that row, a result that is no `ProcessSnapshot` or one whose
`Validate()` reports an error skips it (counted). A validated snapshot is folded without throwing: `programs` absent reads as empty,
counts are summed in `long`; only SQLite errors and cancellation leave `ProgramRollupFolder.Fold` (`RunRetentionAsync` maps only
`SqliteException`, the job catches only `StoreException`). No duplicate-name branch (`Validate` refuses duplicates). A snapshot: PSS
when it has a program and all carry `pss_bytes`; incomplete per E2;
its RSS, PSS (PSS snapshot) and CPU sums go to the total of its source's bucket per tier; the named programs are all, or above 50 the
union of the 50 highest in its key and the 50 highest CPU, ties by `string.CompareOrdinal`. Buckets before a tier's horizon are not
written. Accumulators are kept per bucket in first-met order (record ID, then ordinal name), which is **E4's fold order**. Write per
bucket: read `named`; a program whose row exists merges; otherwise it is inserted while `named` < `MaxNamedPrograms` (500), else
dropped (its values stay in the total); then the total row merges. Sums clamp to ±`double.MaxValue` at every addition, in memory and
in SQL. `pss_sum` is NOT NULL DEFAULT 0; the upserts of `pss_min`/`pss_max` keep the non-NULL side (`coalesce`/`CASE`) so RSS-only
and PSS steps merge in either order. The watermark moves to the last row read, skipped rows included. `RollupFolder.Floor` and `HorizonOf` become `internal` and are
shared, not copied.

**Removal**: `IsRemovable` keeps a process snapshot above `program_rollup_after_id`; new stages after `HourlyBuckets`: program rows
5 minutes, totals 5 minutes, program rows hourly, totals hourly, by `bucket` below the tier's horizon, each a derived table within the
step bound. `RemovalStepResult` counts rows per tier.

**Read** (`ProgramRollupQueries`, reader connection, context `store: reading program rollups`): validate (texts below); `from >= to`
returns the empty result. Tier: 5-minute below one hour, else hourly. Steps are multiples of the step since the Unix epoch (UTC), the first the one holding
`from`, as in the time-series query; the 2,000 cap counts the steps of that grid overlapping the range. Query 1 groups the totals rows
of the range per step (≤ 2,000 rows); no row → no lines, but `EarliestUnfolded` and `StartsBeforeBuckets` are still set. Memory key PSS when the summed `pss_snapshots` equal `snapshots`. Query 2 ranks programs by the range's
sum in the key (or CPU), `ORDER BY` sum descending, then `program` (SQLite byte order), `LIMIT n`. Query 3 groups the rows of those
names (`json_each`) per program and step. A step's average is the sum over its snapshots; minimum 0 when its `seen` < `snapshots`,
else the minimum; a step without a total is a gap (`null`); a program absent from a step with snapshots reads 0. Every sum the read computes is clamped to
±`double.MaxValue`: in SQL around each `sum()` (`MIN(MAX(…))`), and in C# with `TimeSeriesQueries.ClampSum` (made internal, shared) at
each addition; no subtraction runs in SQL. *Other programs* is computed in C# from the clamped total minus the clamped named sums, over
the snapshots, within 0 and `double.MaxValue`, so every returned value is finite. `EarliestUnfolded`: the earliest capture time of the source's process
snapshots above the watermark (`INDEXED BY records_process_fold`); `StartsBeforeBuckets`: `from` before the source's oldest total row
of the tier. Memory: n + 1 lines of at most 2,000 points. The read runs inside
`SqliteStore.ReadAsync`, whose lease attaches `ReaderQuery` (fact 14), so cancellation interrupts a running statement. Source list: the totals of both tiers in the range, `max(bucket)` per source,
by source, at most 256 with `More`.

Exact errors (`store: invalid query: ` and): `count must be 1 to 50`, `step must be one of the query's steps`, `the range holds more
than 2000 steps`, `from and to must be within the storable range`, `unknown measure`.

**Job**: `RetentionService.RollUpAsync` sums `Snapshots`, `SkippedSnapshots`, `ProgramBuckets` and logs event 76 `retention program
rollup {Snapshots} {Skipped} {Buckets}` when snapshots or skipped > 0; `RetentionRemovalTotals` logs event 77 `retention removal
program rollups {FiveMinuteRows} {HourlyRows}` when one > 0. Both information level, counts only.

**Bounds' seams**: the fold step's bounds through `RollupStepResult.Snapshots`/`More`; the read's through the result's sizes.

**Benchmark** (`tools/Vandox.StorageBenchmark`, option `--programs`): five batches of 10,000 metrics and 500 snapshots each of 80
programs; times five fold steps against the batches (N2); a day of one snapshot per minute, `page_count` before and after its fold (N4);
a year of hourly snapshots, then the median of five one-year reads at a 6-hour step, memory, n = 10 (N3); one worst-case fold step
(10,000 metrics plus 2 MiB of snapshots of 4,096 programs with 1,024-byte names, and one 16 MiB snapshot), reported beside the typical
figures; the duration of schema step 16 on the benchmark database.

New user-visible output: the two log lines above.

## Options considered

See record 0223. Also weighed: the key (`source`, `program`, `bucket`) — a range read would scan every bucket of the source per program.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `SchemaMigrator`, `StorageLimits`, `RetentionQueries`, `RollupFolder`, `RetentionRemover`, `RemovalStepResult`, `RollupStepResult`, `SqliteStore` | changed |
| Vandox.Storage | `ProgramRollupFolder`, `ProgramBucketTotals`, `ProgramSourceTotals`, `SourceBucketKey`, `RetentionProgramRow`, `ProgramRollupQueries`, `IProgramRollupReader`, `ProgramRollupQuery`, `ProgramMeasure`, `ProgramMemoryKey`, `ProgramRollupResult`, `ProgramRollupLine`, `ProgramRollupPoint`, `ProgramSourceQuery`, `ProgramSourceList`, `ProgramSource` | new |
| Vandox.Backend | `BackendLog`, `RetentionService`, `RetentionRemovalTotals` | changed |
| Vandox.StorageBenchmark | `ProgramBenchmark` (new), `Program` (option) | new, changed |

## Signatures (for the Dev's skeleton)

Types are public sealed classes unless stated; properties have get and init, lists default to empty, texts to the empty string.

- `public interface IProgramRollupReader`, implemented by SqliteStore: `Task<ProgramRollupResult> ProgramRollupsAsync(ProgramRollupQuery query, CancellationToken cancellationToken)`, `Task<ProgramSourceList> ProgramSourcesAsync(ProgramSourceQuery query, CancellationToken cancellationToken)`.
- `ProgramRollupQuery`: `ProgramRollupQuery.Source` (string), `ProgramRollupQuery.From` (DateTimeOffset), `ProgramRollupQuery.To` (DateTimeOffset), `ProgramRollupQuery.Step` (TimeSpan), `ProgramRollupQuery.Measure` (ProgramMeasure), `ProgramRollupQuery.Count` (int).
- `public enum ProgramMeasure { Memory, Cpu }`, `public enum ProgramMemoryKey { Rss, Pss }`.
- `ProgramRollupResult`: `ProgramRollupResult.Times` (IReadOnlyList of DateTimeOffset, the step starts), `ProgramRollupResult.Programs` (IReadOnlyList of ProgramRollupLine), `ProgramRollupResult.Others` (IReadOnlyList of double?), `ProgramRollupResult.Incomplete` (IReadOnlyList of int), `ProgramRollupResult.MemoryKey` (ProgramMemoryKey?, null for CPU), `ProgramRollupResult.EarliestUnfolded` (DateTimeOffset?), `ProgramRollupResult.StartsBeforeBuckets` (bool).
- `ProgramRollupLine`: `ProgramRollupLine.Program` (string), `ProgramRollupLine.Points` (IReadOnlyList of ProgramRollupPoint?, null a gap).
- `public readonly record struct ProgramRollupPoint(double Average, double Minimum, double Maximum)`.
- `ProgramSourceQuery`: `ProgramSourceQuery.From`, `ProgramSourceQuery.To` (DateTimeOffset).
- `ProgramSourceList`: `ProgramSourceList.Sources` (IReadOnlyList of ProgramSource), `ProgramSourceList.More` (bool).
- `ProgramSource`: `ProgramSource.Source` (string), `ProgramSource.LatestBucket` (DateTimeOffset, the start of the latest bucket of either tier in the range).
- New properties (int): `RollupStepResult.Snapshots`, `RollupStepResult.SkippedSnapshots`, `RollupStepResult.ProgramBuckets`, `RemovalStepResult.ProgramFiveMinuteRows`, `RemovalStepResult.ProgramHourlyRows`.
- Constants (int): `StorageLimits.SchemaVersion` (16), public `StorageLimits.MaxProgramRollupCount` (50), `StorageLimits.MaxProgramRollupSteps` (2000), `StorageLimits.MaxProgramSources` (256); internal `StorageLimits.ProgramRollupStepRows` (500), `StorageLimits.ProgramRollupStepBytes` (2097152), `StorageLimits.MaxNamedPrograms` (500), `StorageLimits.ProgramCutPerKey` (50); internal string `RetentionQueries.ProgramRollupWatermarkKey` (program_rollup_after_id).
- `internal static RollupStepResult ProgramRollupFolder.Fold(RetentionSession session, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)` (internal static class).
- Internal static class ProgramRollupQueries: `internal static void Validate(ProgramRollupQuery query)`, `internal static Task<ProgramRollupResult> ReadAsync(SqliteConnection connection, ProgramRollupQuery query, CancellationToken cancellationToken)`, `internal static void ValidateSources(ProgramSourceQuery query)`, `internal static Task<ProgramSourceList> ReadSourcesAsync(SqliteConnection connection, ProgramSourceQuery query, CancellationToken cancellationToken)`.
- Internal sealed classes: `internal void ProgramBucketTotals.Add(double rss, double? pss, double cpu)`, `internal void ProgramSourceTotals.Include(double rss, double? pss, double cpu, bool incomplete)`.
- `internal readonly record struct SourceBucketKey(string Source, long Bucket)`, `internal readonly record struct RetentionProgramRow(string Source, long Bucket, string Program)`.
- Made internal (were private): `internal static long RollupFolder.Floor(long nanoseconds, long width)`, `internal static long RollupFolder.HorizonOf(TimeSpan? period, DateTimeOffset now)`, `internal static double TimeSeriesQueries.ClampSum(double sum)`.
- `internal static partial void RetentionProgramsRolledUp(this ILogger logger, long snapshots, long skipped, long buckets)` (BackendLog, event 76), `internal static partial void RetentionProgramRollupsRemoved(this ILogger logger, int fiveMinuteRows, int hourlyRows)` (event 77).

## Test files

`tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` (new), `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs`,
`tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`, `tests/Vandox.Backend.Tests/RetentionServiceTests.cs`.

Existing call sites: tests that remove process snapshots without folding first now keep them (`SqliteStoreRetentionTests`,
`KillHoldScannerTests`): the Tester adapts them in step 5. `IRetentionStore` is unchanged, so
`ScriptedRetentionStore` stays. The benchmark's fold loops fold snapshots too: the Dev.

Code a unit test cannot cover: `tools/Vandox.StorageBenchmark/ProgramBenchmark.cs`, a console measurement run by hand (AC7).

## Areas

- `docs/areas/program-rollups.md` (new, +1200 words): the spec's *Area documents* entry, with E4's fold order, the step grid and the clamping.
- `docs/areas/retention.md` (+150 words): process snapshots removed only below their watermark; the fold step's program part; the
  walk's new stages; events 76 and 77.
- `docs/areas/storage.md` (+230 words): the four tables, their indexes, `records_process_fold` with its write cost and build, the watermark key, schema step 16.
- `docs/areas/storage-reading.md` (+40 words): scope and a pointer to the per-program read.
- `docs/areas/backend-host.md` (+40 words): events 76 and 77.

## Documentation updates

All by the Dev once green: the five area documents above; `docs/areas/README.md` (index row *Program rollups*);
`docs/ARCHITECTURE.md` (the job also folds process snapshots; schema version 16; `IProgramRollupReader` in *Interfaces*; record 0223
beside 0208); `docs/BENCHMARKS.md` (`--programs`: procedure, development-host row, the DS918+ row from the Product Manager);
`CHANGELOG.md` (*Added*: per-program rollups of process snapshots, issue #458, no option). README: none (no option).

## Architecture check

The guarantees of `.squad/project.md` hold: a step without a folded snapshot reads as a gap, skipped snapshots are counted; the agent
and the backfill classification are untouched. The write path gains only the
partial index `records_process_fold`, updated per process snapshot inserted or removed and built by one scan of `records` in step 16
(precedent `records_removable`); `storage.md` states both, the benchmark measures the build.

## Security considerations

- [x] Limits: the fold's 2 MiB counts stored UTF-8 bytes before decoding; a single row may reach 16 MiB (fact 7); names keep the wire's
  1,024-byte limit, checked at ingest on UTF-8 bytes; a NUL in a name is stored and bound as received; 500 named programs per bucket; 50 programs per read; 2,000 steps; 256 sources.
- [x] Sums: clamped in the fold and in the read, so no read returns Infinity or NaN (E5).
- [x] Exceptions: decoding errors are counted, never logged with text; a failing step logs event 72 with the store's text; the read's errors are the `InvalidQueryException` texts above, or `store: reading program rollups`
  with SQLite's message; no page shows them yet (#50).

## Decision records

- `docs/decisions/0223-process-snapshots-are-folded-into-per-program-buckets-beside-a-source-total.md` (Proposed)

## Out of scope / follow-ups

The processes view (#50); connection history (#51).

## Challenge

- Minor 1 (decode errors, dead branch) — accepted: any exception of decoding a row, and a failed `Validate()`, skip it; the
  duplicate-name branch is dropped (*Fold*); test rows added.
- Minor 2 (NULL PSS merges) — accepted: `pss_sum` NOT NULL DEFAULT 0, NULL-keeping upserts; `SqliteStoreFoldRollupsMergesPssIntoRssOnlyBucket`.
- Minor 3 (empty result and the tier test) — accepted: the flags are set for an empty result too; the tier test has both ranges.
- Minor 4 (step axis) — accepted: the epoch grid of the time-series query, the cap counted on it; `SqliteStoreProgramRollupsAlignsStepsToTheEpoch`.
- Minor 5 (worst-case benchmark) — accepted: one worst-case fold step and the step-16 duration in `--programs` and `docs/BENCHMARKS.md`.
- Minor 6 (write path) — accepted: *Architecture check* and `storage.md` name the index's write cost and build.

## Security review

- 1 (read sums unclamped, blocking) — resolved: every sum of the read is clamped in SQL and in C#, *other programs* is computed in C#,
  no SQL subtraction; E5 and AC6 extended to the read (spec amendment); `SqliteStoreProgramRollupsClampsOverflowingSums`.
- 2 (exception boundary) — resolved: a validated snapshot folds without throwing, only SQLite errors and cancellation leave the fold;
  rows for absent and empty `programs`, a null entry, a null name; counts summed in `long`.
- 3 (NUL in names) — resolved: a `\u0000` name in the hostile-names test, ranked with its values.
- 4 (cancellation path) — resolved: `ReaderQuery` named (fact 14); the test cancels while a statement runs.
