# Plan: Rollups of process snapshots per program

Source: Issue #458 | [spec.md](spec.md)
Tier: standard — a new fold, schema step, removal rule and store read; it creates, changes or removes no security control (the caps and step bounds are input handling).
Security areas touched: 9 File writes and paths derived from external input (`vandox.db`, deletes through `RetentionRemover`), 10 Parsing of external input (the fold decodes stored agent and import payloads with hostile program names)

## Problem / root cause

See [spec.md](spec.md). Spec fix made while planning: S1, S2 and AC2 no longer store a last value or a process count per program, since
no read of S5 returns them (record 0223, *Consequences*).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | A program carries `pss_bytes` only when every process of it has one | `internal/procstats/snapshot.go:71-79` | confirmed |
| 2 | Regular agent snapshots have `complete` false; the size fallback can cut `programs` to none | `internal/procstats/procstats.go:236-241` | confirmed |
| 3 | `ProcessSnapshot.Validate` refuses only a snapshot with neither processes nor programs; at most 4,096 programs, names short text (1,024 bytes) | `src/Vandox.Core/Model/ProcessSnapshot.cs:61-68`, `ModelLimits.MaxItems`, `docs/areas/wire-format-records.md` | confirmed |
| 4 | Program names reach the store without unpaired surrogates (wire check; the import builds names from decoded text) | `src/Vandox.Core/Wire/WireTextCheck.cs`, `LegacyProcessTable.Build` | confirmed |
| 5 | The metric fold reads `metrics` by record ID above `rollup_after_id`; removal keeps metrics above it; the highest record ID is never removed | `RollupFolder.Fold`, `RetentionRemover.IsRemovable` | confirmed |
| 6 | Imported records are never removed | `RetentionQueries.WalkRecords` (`origin <> 'import'`) | confirmed |
| 7 | A stored payload is up to 16 MiB (`StorageLimits.MaxPayloadBytes`) | `StorageLimits.cs:28` | confirmed |
| 8 | Without `INDEXED BY`, `kind = 'process_snapshot' AND id > ? ORDER BY id` uses `records_kind_time` plus a sort; with a partial index `records(id) WHERE kind = 'process_snapshot'` named by `INDEXED BY` it is one index search | `EXPLAIN QUERY PLAN`, SQLite 3.53.1 (Python, scratch) | confirmed |
| 9 | The query's steps: 5, 10, 15, 30 min, 1, 2, 3, 6, 12 h, then whole days; steps below one hour read 5-minute rollups | `SeriesLadder`, `docs/areas/time-series-query.md:27-28` | confirmed |
| 10 | Log events 69–75 are taken; 76 and 77 are free | `grep EventId src` | confirmed |
| 11 | Which exception `JsonDocument.Parse` throws on invalid UTF-8 inside a string | to verify: the Tester's undecodable-row test with such bytes, step 5 | unverified |
| 12 | Disk growth per day and the read's time on the reference database | to verify: AC7, Dev (development host) and Product Manager (DS918+), step 6 | unverified |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `SqliteStoreFoldRollupsFoldsProgramsIntoBothTiers`, `SqliteStoreFoldRollupsFoldsEachSnapshotOnce`, `SqliteStoreFoldRollupsFoldsImportedSnapshots`, `SqliteStoreFoldRollupsSkipsUndecodableSnapshotsAndCountsThem` (data `not json`, NULL, `{"programs":"x"}`, invalid UTF-8; the next snapshot folds), `SqliteStoreFoldRollupsCountsIncompleteSnapshots` (rows: total equal, total different, total absent and `complete` false or true, no programs), `SqliteStoreFoldRollupsStopsAtTheSnapshotBudgets` (501 rows; the byte budget), `SqliteStoreFoldRollupsSkipsProgramBucketsPastTheirTier` | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC1 | `SqliteStoreOpenMigratesVersion15ToProgramRollups` (tables, indexes, stored snapshots folded before removal) | `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` |
| AC2 | `SqliteStoreProgramRollupsCountsMissingProgramAsZero` (minimum 0, averages add up to the total), `SqliteStoreFoldRollupsCutsToFiftyProgramsPerKey` (60 programs, a tie at rank 50 by ordinal name, a PSS snapshot cut by PSS), `SqliteStoreFoldRollupsCapsNamedProgramsInFoldOrder` (the 501st name into *other programs*, a named program stays named in a later step) | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC3 | `SqliteStoreRemoveExpiredKeepsUnfoldedProcessSnapshots`, `SqliteStoreRemoveExpiredRemovesSkippedUndecodableSnapshot`, `SqliteStoreRemoveExpiredRemovesConnectionSnapshotsWithoutProgramFold`, `SqliteStoreRemoveExpiredRemovesProgramBucketsPastTheirPeriods` (counts) | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC3 | `SqliteStoreFoldRollupsMergesLateSnapshotIntoBucket` (its snapshots removed), `SqliteStoreFoldRollupsCancelledStepCommitsNothing` | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC3 | `RetentionServiceLogsProgramRollupAndRemovalCounts` | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC4 | `SqliteStoreProgramRollupsReturnsTopProgramsByMeasure` (memory, CPU, ties), `SqliteStoreProgramRollupsReadsRssWhenRangeMixesPssAndRss` (and PSS for an all-PSS range, its denominator the PSS snapshots), `SqliteStoreProgramRollupsReturnsOthersAndIncompletePerStep`, `SqliteStoreProgramRollupsMarksStepsWithoutSnapshotsAsGaps`, `SqliteStoreProgramRollupsReadsTheTierOfTheStep` (30-minute step over a range older than the 5-minute period: gaps, flag set), `SqliteStoreProgramRollupsReturnsEarliestUnfoldedAndStartsBefore`, `SqliteStoreProgramRollupsReturnsEmptyResult` (unknown source, empty range, no bucket), `SqliteStoreProgramRollupsRejectsInvalidQuery` (count 0 and 51, step 7 minutes, a year at 5 minutes, a bound outside the storable range: exact texts), `SqliteStoreProgramRollupsHonorsCancellation` | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC5 | `SqliteStoreProgramSourcesListsLatestBucketInRange` (two sources, a bucket after the range left out), `SqliteStoreProgramSourcesCapsAtTheLimit` (257 sources), `SqliteStoreProgramSourcesRejectsRangeOutsideStorableRange` | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
| AC6 | `SqliteStoreFoldRollupsClampsOverflowingProgramSums`, `SqliteStoreFoldRollupsStoresHostileProgramNamesAsReceived` (1,024 bytes, quotes, `%`, SQL text, `\u0001`, `\u202e`, an emoji) | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` |
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
`PayloadRegistry.Deserialize`); NULL data, a `JsonException`, an `InvalidOperationException` or a result that is no `ProcessSnapshot`
skips it (counted). A decoded snapshot: duplicate names merged; PSS when it has a program and all carry `pss_bytes`; incomplete per E2;
its RSS, PSS (PSS snapshot) and CPU sums go to the total of its source's bucket per tier; the named programs are all, or above 50 the
union of the 50 highest in its key and the 50 highest CPU, ties by `string.CompareOrdinal`. Buckets before a tier's horizon are not
written. Accumulators are kept per bucket in first-met order (record ID, then ordinal name), which is **E4's fold order**. Write per
bucket: read `named`; a program whose row exists merges; otherwise it is inserted while `named` < `MaxNamedPrograms` (500), else
dropped (its values stay in the total); then the total row merges. Sums clamp to ±`double.MaxValue` at every addition, in memory and
in SQL. The watermark moves to the last row read, skipped rows included. `RollupFolder.Floor` and `HorizonOf` become `internal` and are
shared, not copied.

**Removal**: `IsRemovable` keeps a process snapshot above `program_rollup_after_id`; new stages after `HourlyBuckets`: program rows
5 minutes, totals 5 minutes, program rows hourly, totals hourly, by `bucket` below the tier's horizon, each a derived table within the
step bound. `RemovalStepResult` counts rows per tier.

**Read** (`ProgramRollupQueries`, reader connection, context `store: reading program rollups`): validate (texts below); `from >= to`
returns the empty result. Tier: 5-minute below one hour, else hourly. Query 1 groups the totals rows of the range per step (≤ 2,000
rows); no row → empty result. Memory key PSS when the summed `pss_snapshots` equal `snapshots`. Query 2 ranks programs by the range's
sum in the key (or CPU), `ORDER BY` sum descending, then `program` (SQLite byte order), `LIMIT n`. Query 3 groups the rows of those
names (`json_each`) per program and step. A step's average is the sum over its snapshots; minimum 0 when its `seen` < `snapshots`,
else the minimum; a step without a total is a gap (`null`); a program absent from a step with snapshots reads 0. *Other programs* is
the total minus the named sums, over the snapshots, at least 0. `EarliestUnfolded`: the earliest capture time of the source's process
snapshots above the watermark (`INDEXED BY records_process_fold`); `StartsBeforeBuckets`: `from` before the source's oldest total row
of the tier. Memory: n + 1 lines of at most 2,000 points. Source list: the totals of both tiers in the range, `max(bucket)` per source,
by source, at most 256 with `More`.

Exact errors (`store: invalid query: ` and): `count must be 1 to 50`, `step must be one of the query's steps`, `the range holds more
than 2000 steps`, `from and to must be within the storable range`, `unknown measure`.

**Job**: `RetentionService.RollUpAsync` sums `Snapshots`, `SkippedSnapshots`, `ProgramBuckets` and logs event 76 `retention program
rollup {Snapshots} {Skipped} {Buckets}` when snapshots or skipped > 0; `RetentionRemovalTotals` logs event 77 `retention removal
program rollups {FiveMinuteRows} {HourlyRows}` when one > 0. Both information level, counts only.

**Bounds' seams**: the fold step's bounds through `RollupStepResult.Snapshots`/`More`; the read's through the result's sizes.

**Benchmark** (`tools/Vandox.StorageBenchmark`, option `--programs`): five batches of 10,000 metrics and 500 snapshots each of 80
programs; times five fold steps against the batches (N2); a day of one snapshot per minute, `page_count` before and after its fold (N4);
a year of hourly snapshots, then the median of five one-year reads at a 6-hour step, memory, n = 10 (N3).

New user-visible output: two log lines, layout as above.

## Options considered

See record 0223 (metric series per program, hourly only, write-path fold, a separate store call). Also weighed: ranking from the totals
alone — impossible, the totals hold no names; a key (`source`, `program`, `bucket`) — a range read would scan every bucket of the
source per program.

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
- Made internal (were private): `internal static long RollupFolder.Floor(long nanoseconds, long width)`, `internal static long RollupFolder.HorizonOf(TimeSpan? period, DateTimeOffset now)`.
- `internal static partial void RetentionProgramsRolledUp(this ILogger logger, long snapshots, long skipped, long buckets)` (BackendLog, event 76), `internal static partial void RetentionProgramRollupsRemoved(this ILogger logger, int fiveMinuteRows, int hourlyRows)` (event 77).

## Test files

`tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` (new), `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs`,
`tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`, `tests/Vandox.Backend.Tests/RetentionServiceTests.cs`.

Existing call sites: an existing test that removes process snapshots without `RetentionSamples.FoldAllAsync`/`RetainAsync` first now
keeps them (`SqliteStoreRetentionTests`, `KillHoldScannerTests`): the Tester adapts them in step 5. `IRetentionStore` is unchanged, so
`ScriptedRetentionStore` stays. The benchmark's fold loops fold snapshots too: the Dev.

Code a unit test cannot cover: `tools/Vandox.StorageBenchmark/ProgramBenchmark.cs` — a console measurement, run by hand (AC7), as the
tool's other scenarios.

## Areas

- `docs/areas/program-rollups.md` (new, +1200 words): scope; buckets and totals; the fold, E2's rule, E3's cut, E4's cap and fold order
  and what they cost a reader; undecodable snapshots; the read, its tiers, key, gaps, errors and caps; the source list; measured growth.
- `docs/areas/retention.md` (+150 words): process snapshots removed only below their watermark; the fold step's program part; the
  walk's new stages; events 76 and 77.
- `docs/areas/storage.md` (+200 words): the four tables, their indexes, `records_process_fold`, the watermark key, schema step 16.
- `docs/areas/storage-reading.md` (+40 words): scope and a pointer to the per-program read.
- `docs/areas/backend-host.md` (+40 words): events 76 and 77.

## Documentation updates

All by the Dev once green: the five area documents above; `docs/areas/README.md` (index row *Program rollups*);
`docs/ARCHITECTURE.md` (the job also folds process snapshots; schema version 16; `IProgramRollupReader` in *Interfaces*; record 0223
beside 0208); `docs/BENCHMARKS.md` (`--programs`: procedure, development-host row, the DS918+ row from the Product Manager);
`CHANGELOG.md` (*Added*: per-program rollups of process snapshots, issue #458, no option). README: none (no option).

## Architecture check

The guarantees of `.squad/project.md` hold: no data gap is hidden (a step without a folded snapshot reads as a gap; skipped snapshots
are counted in the log); the agent is untouched; backfill classification is untouched. The write path is unchanged (record 0208).

## Security considerations

- [x] Limits: the fold's 2 MiB counts stored UTF-8 bytes before decoding; a single row may reach 16 MiB (fact 7); names keep the wire's
  1,024-byte limit, checked at ingest on UTF-8 bytes; 500 named programs per bucket; 50 programs per read; 2,000 steps; 256 sources.
- [x] Exceptions: decoding errors are caught and counted, never logged with text; a failing step logs `retention step failed fold`
  with the store's text (event 72); the read's errors are the `InvalidQueryException` texts above, or `store: reading program rollups`
  with SQLite's message; no page shows them yet (#50).

## Decision records

- `docs/decisions/0223-process-snapshots-are-folded-into-per-program-buckets-beside-a-source-total.md` (Proposed)

## Out of scope / follow-ups

The processes view (#50); connection history (#51).

## Challenge notes

Devil's Advocate, plan challenge: feature #458 (specs/feature-458-process-rollups/plan.md, spec.md, tasks.md, record 0223)

The plan holds up on its main points. I checked these against the code and they are right:
- Removal walk (`RetentionRemover.IsRemovable`, src/Vandox.Storage/RetentionRemover.cs:504-529): each pass starts over, so a snapshot kept as unfolded is looked at again on the next pass. Retention.md:95-96 says the same.
- Imported records are never walked (`RetentionQueries.WalkRecords`, `origin <> 'import'`).
- `ReadNumber` returns 0 when the key is missing (RetentionSession.cs:72-77), so after the upgrade the stored snapshots fold first (E8).
- Log events 76 and 77 are free; 60-75 are taken.
- Stored payloads have already passed `Validate()` at ingest (RecordRules.cs:32).
- Every agent's process snapshots carry source `processes` (internal/procstats/procstats.go:33). Legacy imports use their own source, so imported and live snapshots never mix in one bucket.
- There is no no-change outcome: no existing code keeps per-program history past `retention.snapshots`.
- The tier (`standard`, areas 9 and 10) is right.

The objections below are all minor: each is a detail the Dev or Tester can follow without changing the plan.

minor 1: catching decode errors and dead code. Why it matters: the program fold now runs in the metric fold's transaction, so any exception it does not catch stops both folds on every tick.
- Evidence: `RunRetentionAsync` (src/Vandox.Storage/SqliteStore.cs:510-537) turns only a `SqliteException` into a `StoreException`. `RetentionService.RollUpAsync` (src/Vandox.Backend/Retention/RetentionService.cs:117-125) catches only `StoreException` and cancellation. The plan's skip list (Approach, *Fold*) names only NULL data, `JsonException`, `InvalidOperationException` and a payload that is not a `ProcessSnapshot`. `JsonSerializer` can also throw `NotSupportedException`.
- Instruction for the Dev: skip and count a row on any exception from decoding that one row, not only the two named types. Also skip a decoded snapshot whose `Validate()` returns an error, which covers a null entry in `programs`.
- Instruction for the Dev: drop the "duplicate names merged" branch, or keep it with a comment and a test. It cannot be reached, because `ProcessSnapshot.Validate` refuses duplicate program names (src/Vandox.Core/Model/ProcessSnapshot.cs, the `names` HashSet loop) before anything is stored. As written it is an untested branch that counts against the *Coverage gate*.

minor 2: PSS columns must merge correctly when they are NULL. Why it matters: a NOT NULL failure here would stop the fold for good.
- Evidence: in SQLite 3.53.1, `min(NULL,5)`, `max(NULL,5)` and `NULL+5` all return NULL (I ran this). The plan leaves `pss_min`/`pss_max` NULL when there is no PSS snapshot, and copies the metric clamp `MIN(MAX(sum + excluded.sum, $low), $high)` (RetentionQueries.cs, `UpsertFiveMinutes`).
- Instruction for the Dev: write the upserts for `pss_min`, `pss_max` and `pss_sum` (program rows and totals) so a NULL on either side keeps the other value, for example with `coalesce`/`CASE`. Declare `pss_sum` so a step of RSS snapshots only writes 0, not NULL.
- Instruction for the Tester: add a test that folds one bucket in two steps, first only RSS snapshots, then a PSS snapshot, and the reverse order. Assert that the step commits and that the RSS columns and totals stay right.

minor 3: the empty result against the tier test.
- Evidence: plan *Read* says "Query 1 … no row → empty result", before `StartsBeforeBuckets` and `EarliestUnfolded` are worked out. *Tests per criterion* for AC4 expects `SqliteStoreProgramRollupsReadsTheTierOfTheStep` ("30-minute step over a range older than the 5-minute period: gaps, flag set"). If the range lies wholly before the 5-minute period it has no 5-minute totals row, so by the plan's own rule the result is empty and the flag is not set.
- Instruction for the Dev: compute `StartsBeforeBuckets` and `EarliestUnfolded` for the empty result too, so a caller can tell "nothing stored" from "use a coarser step".
- Instruction for the Tester: in that test, let the range start before the 5-minute horizon and end inside it (gaps first, then values, flag set). Add one case with a range wholly before the horizon: empty lines, flag set.

minor 4: the step axis is not specified.
- Evidence: the plan defines `ProgramRollupResult.Times` only as "the step starts". docs/areas/time-series-query.md (*Points*, "At a step") fixes the times as multiples of the step since the Unix epoch (UTC), the first being the step that holds `from`, each point covering its whole step. #50 draws these lines next to the metric lines.
- Instruction for the Dev: align the steps exactly that way.
- Instruction for the Tester: count the 2,000-step cap as steps overlapping the range on that grid. Assert the first time for a `from` that is not on a step boundary.

minor 5: the benchmark for N2/N3 measures only typical data. Why it matters: the bound in retention.md (lines 90-93, "a step is meant to stay within the work of one ingest batch") now covers a metric step plus a program step under one write lock.
- Evidence: plan *Benchmark* times fold steps over snapshots of 80 programs, and the read over 80 programs. The worst step is 10,000 metric rows plus a program part that hits the 2 MiB budget, or a single first row of up to 16 MiB (plan fact 7) with up to 4,096 programs and 1,024-byte names (fact 3).
- Instruction for the Dev: add to `--programs` one worst-case fold step (a full metric step plus the largest snapshots the wire allows). Report it next to the typical figures in docs/BENCHMARKS.md, so AC7 states the bound and not only the typical case.

minor 6: "The write path is unchanged" is not quite true.
- Evidence: plan *Architecture check*. Schema step 16 adds the partial index `records_process_fold` on `records`. It is updated on every insert and delete of a process snapshot, and step 16 builds it with a scan of the whole `records` table at start-up. `records_removable` (SchemaMigrator.cs:337) is the precedent.
- Instruction for the Dev: say this in storage.md (index cost and the one-time build at migration). Measure how long step 16 takes on the benchmark database.
