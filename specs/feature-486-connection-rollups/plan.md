# Plan: Rollups of connection snapshots

Source: Issue #486 | [spec.md](spec.md)
Tier: standard — a new fold, schema step, removal stages and store reads that create, change or remove no security control.
Security areas touched: Ingest authentication (disk bound only: the rollups grow from authenticated, possibly forged snapshots; `BatchWriter` is unchanged); File writes and paths derived from external input (the `RetentionRemover` guard and its four new removal stages); Parsing of external input (the fold decodes stored connection snapshots and cuts and caps their entries); Logging and display of external data (events 79 and 80, counts only; commands returned as received)
Plan size: a schema step, a third fold part with a trim, four removal stages, two reads, two log lines and a benchmark scenario, each with its rules.

## Problem / root cause

See [spec.md](spec.md), *Problem*: connection snapshots leave nothing behind once `retention.snapshots` removes them.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The fold step runs the metric and the program fold in one transaction and combines their results. | `SqliteStore.FoldRollupsAsync` and `Combine` (`src/Vandox.Storage/SqliteStore.cs:363`, `:1093`) | confirmed |
| 2 | Connection snapshots are removed by `retention.snapshots` without a fold guard; the newest mark and a snapshot with a listener finding (until `retention.events`) are kept. | `RetentionRemover.IsRemovable` (`RetentionRemover.cs:530-562`) | confirmed |
| 3 | The program fold reads at most 500 snapshots or 2 MiB of stored data per step, skips an undecodable one, caps names with a stored `named` count and writes in dictionary order. | `ProgramRollupFolder.Fold`, `Write`; `StorageLimits.ProgramRollupStepRows`, `ProgramRollupStepBytes`, `MaxNamedPrograms` | confirmed |
| 4 | A connection snapshot carries `States` (`Proto`, `State`, `Count`), `Processes` (`Pid`, `Command`, `Count` as `uint`), `Remotes` (`Addr` as `IPAddress`, `Count`) and `AggregatesIncomplete`; a UDP entry's state is optional. | `src/Vandox.Core/Model/ConnectionSnapshot.cs`, `StateCount.cs`, `ProcessConnections.cs`, `RemoteCount.cs` | confirmed |
| 5 | A remote address is parsed into an `IPAddress` (no zone, no brackets) and written back with `ToString()`; `::ffff:a.b.c.d` stays IPv6. | `IpJson.TryParseAddress`, `IpAddressJsonConverter.Write`, `Check.Address` | confirmed |
| 6 | The program read validates count, measure, step (`_steps` or whole days up to 36,500), storable range and at most 2,000 steps, reads on one reader connection and clamps sums in SQL. | `ProgramRollupQueries.Validate`, `ReadAsync` | confirmed |
| 7 | The schema is at version 16; step 16 created the program tables and `records_process_fold`. | `StorageLimits.SchemaVersion`, `SchemaMigrator` step 16 | confirmed |
| 8 | Event IDs up to 78 are used; 79 and 80 are free. | `grep -rn "EventId = " src` | confirmed |
| 9 | `RetentionSamples.RetainAsync` folds before it removes; `SqliteStoreRemoveExpiredRemovesConnectionSnapshotsWithoutProgramFold` removes a connection snapshot without a fold. | `tests/Vandox.Storage.Tests/RetentionSamples.cs:345`, `SqliteStoreRetentionTests.cs:1135` | confirmed |
| 10 | A year of hourly buckets with 1,500 addresses each is 13.1 million rows (682 MB); the top 20 took 5.4–6.2 s and the distinct count 6.0–6.5 s; at 615 addresses per hour 2.0–2.1 s for the top 20. | Lead's probe on the development host, Python `sqlite3`, the planned key `(source, bucket, kind, name)` `WITHOUT ROWID` (scratch copy, deleted); development host only | confirmed |
| 11 | After the trim a year is at most 8,760 × 100 address rows (1,095 days: 2,628,000), and the reads of N2 meet 10 s on the reference host. Extrapolated from fact 10 by row count: one year about 0.33 s top 20 plus 0.42 s distinct count on the development host, 1,095 days about 1.0 + 1.25 s; at the program read's factor of 3.3 (`docs/BENCHMARKS.md`, *Program rollups*: 779 against 2,550 ms) about 2.5 s and 7.4 s on the DS918+. **Fallback** if a read misses 10 s: `KeptAddressesHourly` 50 (rows halved, about 3.7 s for 1,095 days), changing E14, record 0225 and the `_1h` partial index literal of step 17 (unreleased, edited in place) together; the Lead revises, the Product Manager approves the number. | to verify: `--connections` (N2, N7) on the DS918+ SSD volume, by the Product Manager, after group G3, before step 9 | unverified |
| 12 | Imported connection snapshots carry the source `legacy-monitor` and no `aggregates_incomplete`. | issue #486; `docs/areas/wire-format-records.md` (absent fields read as complete aggregates) | confirmed |
| 13 | A fold pass repeats steps while `More` is true; the program part's `More` is "byte budget spent or 500 rows read", so a backlog bucket can span steps. | `RetentionService.RollUpAsync` (`RetentionService.cs:97-121`), `ProgramRollupFolder.cs:86` | confirmed |

## Tests per criterion

All storage tests run against a real `SqliteStore` in a `TempDirectory`; a payload comes from `ConnectionSamples` (new) or, once per
fold test class, from the two `connection_snapshot` lines of `testdata/wire/all-kinds.jsonl` (the real wire sample).

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `SqliteStoreFoldRollupsFoldsEachConnectionSnapshotOnce`, `SqliteStoreFoldRollupsFoldsImportedConnectionSnapshots`, `SqliteStoreFoldRollupsSkipsUndecodableConnectionSnapshotAndPassesIt`, `SqliteStoreFoldRollupsMergesLateConnectionSnapshotIntoBucket`, `SqliteStoreFoldRollupsCreatesNoConnectionBucketPastItsPeriod`, `SqliteStoreFoldRollupsStopsConnectionPartAtStepBound` | `tests/Vandox.Storage.Tests/SqliteStoreConnectionRollupTests.cs` |
| AC1 | `SqliteStoreRemoveExpiredKeepsConnectionSnapshotAboveWatermark`, `SqliteStoreRemoveExpiredKeepsListenerFindingsWithTheirSnapshots` (unchanged, still green), `SqliteStoreOpenMigratesToVersionSeventeen` | `SqliteStoreRetentionTests.cs`, `SqliteStoreOpenTests.cs` |
| AC2 | `SqliteStoreFoldRollupsMergesTcpAndTcp6StatesAndUdpAsUdp`, `SqliteStoreFoldRollupsCountsMissingStateAsZero`, `SqliteStoreFoldRollupsAddsDuplicateEntriesWithinSnapshot` | `SqliteStoreConnectionRollupTests.cs` |
| AC3 | `SqliteStoreFoldRollupsSumsProcessCountsPerProgram`, `SqliteStoreFoldRollupsCutsProgramsToFiftyPerSnapshot`, `SqliteStoreFoldRollupsCapsProgramsAtFiveHundredPerBucket`, `SqliteStoreFoldRollupsFoldsSnapshotWithoutProcesses` | `SqliteStoreConnectionRollupTests.cs` |
| AC4 | `SqliteStoreFoldRollupsCutsAddressesToTwentyFivePerSnapshot`, `SqliteStoreFoldRollupsKeepsMappedAddressApart`, `SqliteStoreFoldRollupsCapsAddressesPerTierAndCountsCappedSnapshots`, `SqliteStoreFoldRollupsTrimsEndedBucketToTopAddresses`, `SqliteStoreFoldRollupsLeavesOpenBucketUntrimmed`, `SqliteStoreFoldRollupsTrimsAgainAfterLateMerge`, `SqliteStoreFoldRollupsTrimsNothingInStepAtBound`, `SqliteStoreFoldRollupsTrimsEndedHourOnlyOnceFoldCaughtUp`, `SqliteStoreFoldRollupsCountsIncompleteConnectionSnapshots` | `SqliteStoreConnectionRollupTests.cs` |
| AC5 | `SqliteStoreConnectionRollupsReturnsStatesAndTopProgramsPerStep`, `SqliteStoreConnectionRollupsReturnsOthersAsTotalLessNamed`, `SqliteStoreConnectionRollupsReturnsGapsAndCompleteness`, `SqliteStoreConnectionRollupsReadsFiveMinuteTierBelowOneHour`, `SqliteStoreConnectionRollupsRefusesInvalidQuery` (one data row per error text) | `SqliteStoreConnectionRollupTests.cs` |
| AC6 | `SqliteStoreConnectionAddressesReturnsTopTwentyWithFurtherCount`, `SqliteStoreConnectionAddressesReturnsGapsIncompleteAndCappedPerStep`, `SqliteStoreConnectionAddressesRefusesInvalidQuery`, `SqliteStoreConnectionSourcesReturnsAtMost256WithMore` | `SqliteStoreConnectionRollupTests.cs` |
| AC7 | `SqliteStoreFoldRollupsStoresHostileCommandAsReceived`, `SqliteStoreFoldRollupsClampsConnectionSums`, `SqliteStoreConnectionRollupsClampsSumsInRead`, `RetentionServiceRollUpLogsConnectionCountsOnly`, `RetentionServiceRemoveLogsConnectionRollupRows` | `SqliteStoreConnectionRollupTests.cs`, `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC8 | `SqliteStoreFoldRollupsRollsBackConnectionPartWithStep`, `SqliteStoreRemoveExpiredRemovesConnectionBucketsPerTier` | `SqliteStoreConnectionRollupTests.cs`, `SqliteStoreRetentionTests.cs` |
| AC9 | manual: the Product Manager runs `--connections` on the DS918+ SSD volume (fact 11; a read above 10 s takes the fallback there); the Dev enters the figures in `docs/BENCHMARKS.md` | — |

Cases the tests pin: E2's order (record ID, then name ordinal), E3's order (record ID, then count descending, address ordinal) with a
snapshot that adds an address once the 5-minute bucket holds 500; E14's ties by address text; a trimmed bucket's late merge adds a
new address and the next step trims it to 100 again; the read's 0 minimum when `seen` is below `snapshots`; an ended hour folded across
two steps (501 snapshots, the first step at its 500-row bound): an address with count 1 in the first snapshot, below the top 100 after
step one, and a large count in the 501st keeps the sum of both, so the kept rows equal those of the whole hour folded before one trim.

## Approach

Layout free for the Tester, approved in step 9 (no user-visible output beyond the two log lines below).

**Schema step 17** (`SchemaMigrator`, `StorageLimits.SchemaVersion` 17): per tier `t` in `5m`, `1h`:
`connection_rollups_t (source TEXT, bucket INTEGER, kind INTEGER CHECK (kind IN (0, 1, 2)), name TEXT, seen INTEGER CHECK (seen >= 1),
min_value REAL, max_value REAL, sum_value REAL, PRIMARY KEY (source, bucket, kind, name)) STRICT, WITHOUT ROWID` with an index on
`bucket`; `connection_rollup_totals_t (source, bucket, snapshots CHECK (snapshots >= 1), incomplete, capped, programs, addresses, trimmed
INTEGER, process_sum REAL, PRIMARY KEY (source, bucket)) STRICT, WITHOUT ROWID` with an index on `bucket` and the partial index
`connection_rollup_totals_t_trim (bucket) WHERE addresses > 50` (`> 100` for `1h`); and `records_connection_fold ON records(id) WHERE
kind = 'connection_snapshot'`. Kind 0 is a state, 1 a program, 2 an address. The watermark is `meta` key `connection_rollup_after_id`.
The literals 50 and 100 equal `StorageLimits.KeptAddressesFiveMinutes`/`KeptAddressesHourly`; a test pins them against each other.

**The fold** (`ConnectionRollupFolder.Fold`, called third in `FoldRollupsAsync`'s transaction, `Combine` takes three results):
1. Read rows above the watermark through `records_connection_fold`, at most `ConnectionRollupStepRows` (500), stop after the row that
   takes the data bytes past `ConnectionRollupStepBytes` (2 MiB), first row always taken, as the program fold. Decode with the shared
   `RollupFolder.Decode<TPayload>` (the program fold moves to it); a `null` result is skipped and counted.
2. Per snapshot, before any tier: states keyed `State` for `tcp`/`tcp6`, `UDP` for `udp`/`udp6`; programs keyed `Command` with the sum of
   `Count`; addresses keyed `Addr.ToString()`; every sum by `RollupFolder.Clamp` (moved from `ProgramRollupFolder`). `process_sum` is the
   sum over all processes. Programs above 50: the 50 with the highest sum, ties by name ordinal, then in name ordinal order. Addresses: the
   25 with the highest count, ties by text ordinal, in that order (count descending, text ordinal). Each snapshot gets an ordinal within
   the step.
3. Per tier (`ConnectionRollupTier`, `BucketOf` as `ProgramRollupTier`, horizon by `RollupFolder.HorizonOf`): the bucket's totals add the
   snapshot (`snapshots`, `incomplete` from `AggregatesIncomplete`, `process_sum`); every state, program and address adds its value
   (`seen`, min, max, sum) in a `ConnectionBucket` that keeps the first-met order of programs and addresses in a list, never relying on
   dictionary order, and per address the ordinals of the snapshots that named it.
4. Write per bucket: read the stored `programs` and `addresses` counts (0 for a new bucket); upsert every state; per program in first-met
   order, a name the stored rows lack is written only while `programs` < 500; per address, a new one only while `addresses` is below the
   tier's cap (500 / 1,500), else dropped and its snapshot ordinals join a set; `capped` adds the set's size. The totals upsert adds
   `snapshots`, `incomplete`, `capped`, `process_sum` (clamped) and sets `programs`, `addresses`. Upserts merge `seen +`, `min`, `max`,
   `sum` clamped to ±`double.MaxValue` in SQL, as `ProgramUpsertTail`.
5. **Trim** (after the writes, same step): per tier, read at most `ConnectionTrimBucketsPerStep` (20) totals through the `_trim` index
   whose bucket end (`bucket` + width) lies at or before `now` − 10 minutes; for each, delete the address rows outside the top
   `KeptAddresses` by `sum_value DESC, name` (SQLite's byte order, which is ordinal for UTF-8 text), add the deleted count to `trimmed`,
   set `addresses` to the kept count. `More` is true when either tier had 20 to trim. The trim runs only in a step whose read in
   step 1 took fewer than 500 rows and did not spend the byte budget (the fold has caught up: every stored snapshot above the watermark
   is folded, fact 13), also when it read none; a step at its bound trims nothing and returns `More`. So no bucket is trimmed while
   stored snapshots of it wait in the backlog (E11, the first pass after downtime); a snapshot stored later is a late merge (E14).
   During a catch-up the ended buckets stay untrimmed until it ends (30 days: about 720 × 1,500 hourly and 8,640 × 125 5-minute rows
   per source at the regular cadence), then 20 per tier per step are trimmed.
6. Advance the watermark to the last row read. Result: `ConnectionSnapshots`, `SkippedConnectionSnapshots`, `ConnectionBuckets` (buckets
   written per tier, as programs), `TrimmedAddresses`.
Memory: one step's decoded snapshots are folded one at a time; a bucket holds at most the snapshot ordinals of its addresses (at most
500 × 25 per tier).

**Removal** (`RetentionRemover`): `IsRemovable` keeps a `connection_snapshot` above `connection_rollup_after_id` (checked before the
mark and finding checks). Four stages after `ProgramHourlyTotals`: `connection_rollups_5m`, `connection_rollup_totals_5m`,
`connection_rollups_1h`, `connection_rollup_totals_1h`, each walking rows with `bucket < now − period` through the `bucket` index; rows
through a new `ReadConnectionRows`/`RemoveConnectionRow` (`RetentionConnectionRow`), totals through the existing `ReadProgramTotals` and
`RemoveProgramTotal` with the connection statements. `RemovalStepResult` reports `ConnectionFiveMinuteRows` and `ConnectionHourlyRows`
(rows plus totals per tier). The SQL lives in a new `ConnectionRollupSql` class, not in `RetentionQueries`.

**Reads** (`ConnectionRollupQueries`, on a reader connection like `ProgramRollupQueries`, context `store: reading connection rollups`):
validation by the shared `RollupSteps.Validate` (`step must be one of the query's steps`, `from and to must be within the storable
range`, `the range holds more than 2000 steps`, in that order; `ProgramRollupQueries.Validate` keeps count and measure first, then calls
it). Step below one hour reads `_5m`, else `_1h`. Grid: `ProgramStepGrid`. Per step totals from `connection_rollup_totals_t`
(`snapshots`, `incomplete`, `capped`, clamped `process_sum`).
- `ConnectionRollupsAsync`: states — every state with a row in the range, ordered by name ordinal, a point per step
  `RollupSteps.Point(sum, seen, min, max, snapshots)` (`seen < snapshots` gives minimum 0), `null` for a gap; programs — the 10 by clamped
  `sum(sum_value)` over the range, ties by name, points likewise; `Others` per step `RollupSteps.Others(process_sum, named, snapshots)`;
  `Incomplete` per step; `EarliestUnfolded` from `records_connection_fold` above the watermark for the source; `StartsBeforeBuckets` against
  the tier's oldest total.
- `ConnectionAddressesAsync`: the 20 by clamped `sum(sum_value)`, ties by name, each `Average` = sum / range snapshots, `Maximum` =
  `max(max_value)`; `Further` = `count(DISTINCT name)` − returned (at least 0); `Gaps`, `Incomplete`, `Capped` per step; completeness as
  above. Two statements, not one window query (fact 10: the window form doubled the time).
- `ConnectionSourcesAsync`: `RollupSteps.ReadSourceListAsync` with the connection totals of both tiers, 256 + `More`, as the program list.
A read holds 13 state lines, 10 program lines and *other programs* of at most 2,000 steps, and 20 addresses.

**Shared helpers** (so the quality gate's duplication condition finds no copy): `RollupSteps` takes the step list, `IsStep`, the step
count check, `StepIndex`, `Clamp(expression)`, `ReadNullableAsync`, `Point`, `Others` and the source read out of `ProgramRollupQueries`,
which calls them; `RollupFolder.Decode<TPayload>` and `RollupFolder.Clamp` serve both folders. The program read's behavior and error
texts do not change; `SqliteStoreProgramRollupTests` stays green unchanged.

**Backend**: `BackendLog` event 79 `retention connection rollup {Snapshots} {Skipped} {Buckets} {Trimmed}` (information, after a fold
pass that folded, skipped, wrote or trimmed anything) and 80 `retention removal connection rollups {FiveMinuteRows} {HourlyRows}`
(information, when either is above 0); `RetentionService.RollUpAsync` sums the four counts, `RetentionRemovalTotals` the two. No reader
is registered in the host: #51 adds its consumer and gate.

**Benchmark** `--connections` (`tools/Vandox.StorageBenchmark/ConnectionBenchmark.cs`, fixed clock 2026-12-01 12:00 UTC like
`ProgramBenchmark`, reusing its `Folder`, `CreatePolicy`, `Median`, `TimeStepsAsync`): N1 five batches of 10,000 metric points, 500
process snapshots of 80 programs and 500 regular connection snapshots (13 states, 40 processes of 15 programs, 25 remotes all new, and
the listener inventory of a Plesk host, 30 `listeners` shaped as those of `testdata/wire/all-kinds.jsonl`), fold steps timed against the
median batch; a worst-case step of 10,000 metric points and full snapshots (4,096 remotes and 4,096 `connections` each, listeners as above)
up to the 2 MiB budget, reported beside the typical figures; N3 a day of 1,440 regular snapshots, `page_count` before and after fold and
trim; N2 1,095 days of hourly buckets at the trimmed size (100 addresses, 13 states, 15 programs) written directly into the tables by SQL,
stated as such, then the median of five reads of each kind over the last year at a 6-hour step and over all 1,095 days at a 1-day step;
one untrimmed hour of 1,500 addresses and its trim step timed (N7); and schema step 17 timed on that database with a year of hourly
regular snapshots stored, as `--programs` times step 16. The Dev states in `docs/BENCHMARKS.md` and the area document the yearly disk
cost at the default periods from the measured bytes per row: per source under full churn about 288 × (50 + 13 + 15) rows a day, 8.2
million rows in 365 days of `rollups_5m`, and 24 × (100 + 13 + 15), 1.1 million a year or 3.4 million in 1,095 days of `rollups_1h`
(about 52 bytes a row in fact 10's probe: some 600 MB per source, an estimate the measured row size replaces).

## Options considered

1. **One rows table per tier with a kind column** (chosen) against three tables per tier: one upsert and two removal stages per tier
   instead of six; record 0225.
2. **Trim ended buckets** (chosen) against a first-met cap only (one-year read 5–6 s for the top 20 on the development host, fact 10),
   a lower hourly cap of 500 (about 4.4 million rows a year, near the deadline, more bias) and a daily address tier (a first-met cap
   reached within hours, or a recomputation on every late merge); record 0225.
3. **A separate fold call or job loop** — rejected as in record 0223 (every `IRetentionStore` double changes).
4. **Reusing `ProgramSourceQuery`/`ProgramSourceList`/`ProgramRollupPoint`** (chosen) against connection copies: same shape, no duplicate
   types; the names stay those of the first consumer.
5. **Counting distinct sockets per program** — rejected by the issue (the agent's per-holder measure is kept).
6. **Trim only once the fold has caught up** (chosen, Challenge 1) against trimming per source only buckets ending before that source's
   earliest unfolded capture: per-source progress during a catch-up, but a scan of every unfolded snapshot per step (`records_connection_fold`
   is keyed by ID, not source and capture time) for a backlog that ends within the same pass; record 0225.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `SchemaMigrator`, `StorageLimits` | step 17, constants |
| Vandox.Storage | `ConnectionRollupFolder`, `ConnectionRollupTier`, `ConnectionBucket`, `ConnectionItemTotals`, `ConnectionRollupSql`, `RetentionConnectionRow` (new) | the fold, trim and statements |
| Vandox.Storage | `RollupFolder`, `ProgramRollupFolder` | shared `Decode`, `Clamp` |
| Vandox.Storage | `RetentionRemover`, `RemovalStepResult`, `RollupStepResult`, `SqliteStore` | guard, stages, results, `Combine`, reader |
| Vandox.Storage | `IConnectionRollupReader`, `ConnectionRollupQuery`, `ConnectionRollupResult`, `ConnectionRollupLine`, `ConnectionAddressResult`, `ConnectionAddress`, `ConnectionRollupQueries`, `ConnectionStepTotals`, `RollupSteps` (new); `ProgramRollupQueries` | the reads |
| Vandox.Backend | `BackendLog`, `RetentionService`, `RetentionRemovalTotals` | events 79, 80 |
| tools | `ConnectionBenchmark` (new), `Program` | `--connections` |

## Signatures (for the Dev's skeleton)

Each type in its own file; types and modifiers in plain text after a member.

Public (Vandox.Storage):

- `IConnectionRollupReader` (public interface), implemented by `SqliteStore`:
  `Task<ConnectionRollupResult> ConnectionRollupsAsync(ConnectionRollupQuery query, CancellationToken cancellationToken);`,
  `Task<ConnectionAddressResult> ConnectionAddressesAsync(ConnectionRollupQuery query, CancellationToken cancellationToken);`,
  `Task<ProgramSourceList> ConnectionSourcesAsync(ProgramSourceQuery query, CancellationToken cancellationToken);`
- `ConnectionRollupQuery` (public sealed class), properties with get/init: `ConnectionRollupQuery.Source` (string, default
  string.Empty), `ConnectionRollupQuery.From` and `ConnectionRollupQuery.To` (DateTimeOffset), `ConnectionRollupQuery.Step` (TimeSpan).
- `ConnectionRollupResult` (public sealed class), get/init, lists defaulting to `[]`: `ConnectionRollupResult.Times`
  (IReadOnlyList of DateTimeOffset), `ConnectionRollupResult.States` and `ConnectionRollupResult.Programs` (IReadOnlyList of
  ConnectionRollupLine), `ConnectionRollupResult.Others` (IReadOnlyList of double?), `ConnectionRollupResult.Incomplete` (IReadOnlyList of
  int), `ConnectionRollupResult.EarliestUnfolded` (DateTimeOffset?), `ConnectionRollupResult.StartsBeforeBuckets` (bool).
- `ConnectionRollupLine` (public sealed class): `ConnectionRollupLine.Name` (string, default string.Empty), `ConnectionRollupLine.Points`
  (IReadOnlyList of ProgramRollupPoint?, default `[]`).
- `ConnectionAddressResult` (public sealed class), as `ConnectionRollupResult` for `ConnectionAddressResult.Times`,
  `ConnectionAddressResult.Incomplete`, `ConnectionAddressResult.EarliestUnfolded`, `ConnectionAddressResult.StartsBeforeBuckets`, plus
  `ConnectionAddressResult.Gaps` (IReadOnlyList of bool), `ConnectionAddressResult.Addresses` (IReadOnlyList of ConnectionAddress),
  `ConnectionAddressResult.Further` (long), `ConnectionAddressResult.Capped` (IReadOnlyList of int).
- `ConnectionAddress` (public sealed class): `ConnectionAddress.Address` (string, default string.Empty), `ConnectionAddress.Average`
  and `ConnectionAddress.Maximum` (double).
- `RollupStepResult.ConnectionSnapshots`, `RollupStepResult.SkippedConnectionSnapshots`, `RollupStepResult.ConnectionBuckets`,
  `RollupStepResult.TrimmedAddresses` (public int, get/init).
- `RemovalStepResult.ConnectionFiveMinuteRows`, `RemovalStepResult.ConnectionHourlyRows` (public int, get/init).
- `StorageLimits.MaxConnectionRollupPrograms` (public const int 10), `StorageLimits.MaxConnectionRollupAddresses` (public const int 20),
  `StorageLimits.SchemaVersion` (17); internal const int: `StorageLimits.ConnectionRollupStepRows` 500,
  `StorageLimits.ConnectionRollupStepBytes` 2097152, `StorageLimits.ConnectionProgramCut` 50, `StorageLimits.ConnectionAddressCut` 25,
  `StorageLimits.MaxConnectionPrograms` 500, `StorageLimits.MaxAddressesFiveMinutes` 500, `StorageLimits.MaxAddressesHourly` 1500,
  `StorageLimits.KeptAddressesFiveMinutes` 50, `StorageLimits.KeptAddressesHourly` 100, `StorageLimits.ConnectionTrimBucketsPerStep` 20;
  internal const long `StorageLimits.ConnectionTrimDelayNanoseconds` 600,000,000,000.

Internal (Vandox.Storage):

- `ConnectionRollupFolder` (internal static class): `internal static RollupStepResult Fold(RetentionSession session, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)`
- `ConnectionRollupTier` (internal sealed class): `internal ConnectionRollupTier(long width, long horizon, bool hourly)`,
  `internal ConnectionBucket? BucketOf(string source, long capturedAt)`, and getters `ConnectionRollupTier.Buckets` (Dictionary of
  SourceBucketKey to ConnectionBucket), `ConnectionRollupTier.AddressCap` and `ConnectionRollupTier.KeptAddresses` (int),
  `ConnectionRollupTier.Width` (long), `ConnectionRollupTier.Hourly` (bool).
- `ConnectionBucket` (internal sealed class): `internal void Include(double processSum, bool incomplete)`,
  `internal void Add(int kind, string name, double value)`, `internal void AddAddress(string address, double value, int snapshot)`,
  `internal IReadOnlyList<int> SnapshotsOf(string address)`, and getters `ConnectionBucket.Snapshots`, `ConnectionBucket.Incomplete`
  (long), `ConnectionBucket.ProcessSum` (double), `ConnectionBucket.Items` (IReadOnlyList of (int Kind, string Name, ConnectionItemTotals
  Totals) in first-met order).
- `ConnectionItemTotals` (internal sealed class): `internal ConnectionItemTotals(double value)`, `internal void Add(double value)`,
  getters `ConnectionItemTotals.Seen` (long), `ConnectionItemTotals.Minimum`, `ConnectionItemTotals.Maximum`, `ConnectionItemTotals.Sum`
  (double).
- `ConnectionRollupSql` (internal static class): the statements of *Approach* as internal const string.
- `RetentionConnectionRow` (internal readonly record struct) with `RetentionConnectionRow(string Source, long Bucket, long Kind, string Name)`.
- `ConnectionRollupQueries` (internal static class): `ConnectionRollupQueries.Context` (internal const string
  "store: reading connection rollups"), `internal static Task<ConnectionRollupResult> ReadAsync(SqliteConnection connection, ConnectionRollupQuery query, CancellationToken cancellationToken)`,
  `internal static Task<ConnectionAddressResult> ReadAddressesAsync(SqliteConnection connection, ConnectionRollupQuery query, CancellationToken cancellationToken)`,
  `internal static Task<ProgramSourceList> ReadSourcesAsync(SqliteConnection connection, ProgramSourceQuery query, CancellationToken cancellationToken)`
- `ConnectionStepTotals` (internal sealed class): `internal ConnectionStepTotals(ProgramStepGrid grid)`, getters
  `ConnectionStepTotals.Grid` (ProgramStepGrid), `ConnectionStepTotals.Snapshots`, `ConnectionStepTotals.Incomplete`,
  `ConnectionStepTotals.Capped` (long[]), `ConnectionStepTotals.ProcessSums`, `ConnectionStepTotals.Named` (double[]).
- `RollupSteps` (internal static class): `RollupSteps.StepIndex` (internal const string, the text of ProgramRollupQueries),
  `internal static bool IsStep(TimeSpan width)`, `internal static void Validate(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, TimeSpan width)`,
  `internal static string Clamp(string expression)`,
  `internal static Task<long?> ReadNullableAsync(SqliteConnection connection, string sql, Dictionary<string, object> parameters, string context, CancellationToken cancellationToken)`,
  `internal static ProgramRollupPoint Point(double sum, long seen, double minimum, double maximum, long snapshots)`,
  `internal static List<double?> Others(double[] sums, double[] named, long[] snapshots)`,
  `internal static Task<ProgramSourceList> ReadSourceListAsync(SqliteConnection connection, ProgramSourceQuery query, string sql, string context, CancellationToken cancellationToken)`.
- `RollupFolder.Decode` (internal static, generic: TPayload? Decode of TPayload(byte[]? data, string kind) where TPayload : class,
  IPayload) and `internal static double Clamp(double sum)` in `RollupFolder`.
- `SqliteStore.Combine` (private static, changed): three RollupStepResult parameters metrics, programs, connections.
- `BackendLog` (Vandox.Backend): `internal static partial void RetentionConnectionsRolledUp(this ILogger logger, long snapshots, long skipped, long buckets, long trimmed);` (EventId 79),
  `internal static partial void RetentionConnectionRollupsRemoved(this ILogger logger, int fiveMinuteRows, int hourlyRows);` (EventId 80).
- `ConnectionBenchmark` (internal static class, tool): `ConnectionBenchmark.Option` (internal const string "--connections"),
  `internal static Task RunAsync(string directory, CancellationToken cancellationToken)`

Deleted: the private helpers `ProgramRollupQueries.IsStep`, `Clamp`, `ReadNullableAsync`, `Others` and `ProgramRollupFolder.Decode`, `Clamp` are removed (moved to the shared classes).
The skeleton holds for real: the schema step, the `Combine` call order and the removal stage list; their tests defer to the fold logic.

## Test files

- `tests/Vandox.Storage.Tests/SqliteStoreConnectionRollupTests.cs` (new), `tests/Vandox.Storage.Tests/ConnectionSamples.cs` (new helper:
  payloads, row queries, the watermark query).
- `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs`, `SqliteStoreOpenTests.cs`, `RetentionSamples.cs` (`UndoStepSeventeen`).
- `tests/Vandox.Backend.Tests/RetentionServiceTests.cs`.

Existing test code affected, adapted by the Tester: `SqliteStoreRemoveExpiredRemovesConnectionSnapshotsWithoutProgramFold` (the
connection snapshot now stays until folded: renamed and asserted so); the undo chains of `SqliteStoreOpenTests` (prefix
`UndoStepSeventeen`); `ScriptedRetentionStore` needs no change (new `init` properties default to 0). A test that passes a connection
snapshot through `RemovePassAsync` without `RetainAsync` keeps it now: the Tester runs the class and adapts each.

Files no unit test can cover: `tools/Vandox.StorageBenchmark/ConnectionBenchmark.cs` and the `Program` dispatch line (no test project
references the tool; it is outside the coverage report, as `ProgramBenchmark`).

## Areas

- `docs/areas/connection-rollups.md` (new, +2,000 words): name *Connection rollups*, scope "What remains of connection snapshots per
  state, program and remote address: the 5-minute and hourly buckets per source, the cuts, caps and trim, the fold, the reads and the
  source list, the measured disk growth"; *Not here*: periods and walk (retention), tables (storage), fields (agent connections, wire).
- `docs/areas/retention.md (+260 words)`: the third watermark and part of the fold step, the guard in *Periods* and *Rollups* (replaces
  "Connection snapshots ... removed by `retention.snapshots` as before"), the four stages in the walk order, events 79 and 80.
- `docs/areas/storage.md (+230 words)`: `meta` key, the two table pairs, the three new indexes, migration step 17.
- `docs/areas/storage-reading.md (+70 words)`: a *Connection rollups* subsection beside *Program rollups*, implementation line.
- `docs/areas/README.md (+60 words)`: the index row. `docs/areas/agent-connections.md (+15 words)` and `program-rollups.md (+15 words)`:
  *Not here* pointers.

## Documentation updates

Dev, once the code is green:
- the area documents above, written against the code;
- `docs/ARCHITECTURE.md`: the retention job sentence (line 120) adds connection snapshots per state, program and address; schema version
  17 with its step (lines 451, 454); `IConnectionRollupReader` in the store interface list (line 493); record 0225 in *Records* (line 516);
- `docs/BENCHMARKS.md`: section *Connection rollups* (criteria N1–N3, N7, procedure `--connections`, results table filled from the
  Product Manager's run: typical and worst-case step, day, both reads at one year and 1,095 days, trim step, schema step 17, the yearly
  disk cost at the default periods) and the usage line of the tool;
- `docs/areas/connection-rollups.md` also states the disk bound of *Security considerations* and the yearly disk cost;
- `CHANGELOG.md`, *Unreleased*, *Added*: **Rollups of connection snapshots** (issue #486), schema version 17, `--connections`;
- record 0225: status `Accepted` with the area link once the area document exists (Lead, step 9).

## Architecture check

No guarantee of `.squad/project.md` changes: a step without a folded snapshot is a gap of the read, not missing data; rollups classify
nothing live or backfilled. Retention still frees space inside the file only (record 0212). The one new read surface has no consumer yet.

## Security considerations

- [x] Limits: the 2 MiB step budget counts stored UTF-8 bytes before decoding; the 25 / 50 cuts and 500 / 1,500 caps count entries
  after decoding; names are bound as parameters, never interpreted; a `command` of up to 1,024 bytes is stored as decoded.
- [x] Exceptions: a decode failure is swallowed into the skip count (as the program fold); a SQLite failure reaches only the job's
  error line (`retention step failed`, the store's text) and the read's `store: reading connection rollups: <message>`; validation
  errors are `store: invalid query: <text>` with the three texts above.
- Disk: per source and per day of capture time at most about 288 x 50 + 24 x 100 address rows once trimmed, plus up to 1,500 rows per open hour; the source is the agent's choice and a bucket at or below 50 (100) addresses is never trimmed, so forged sources or backdated snapshots leave rows kept for `retention.rollups_5m` and `retention.rollups_1h`, beyond `retention.snapshots`; `ingest.min_free` stays the bound of last resort against forged churn, as for program rollups.
  `docs/areas/connection-rollups.md` says the same (Dev, *Documentation updates*).

## Decision records

- `docs/decisions/0225-connection-snapshots-are-folded-into-per-state-program-and-address-buckets-ended-buckets-trimmed.md` (Proposed)

## Challenge

1. MAJOR, the trim's wall-clock test cuts buckets that span bounded steps during a catch-up. Accepted: the trim runs only in a step whose
   connection read stayed below its row and byte bound (Approach step 5, fact 13), so every stored snapshot of a bucket is folded before
   it is trimmed; a snapshot stored after that is E14's late merge. Pinned by `SqliteStoreFoldRollupsTrimsEndedHourOnlyOnceFoldCaughtUp`
   and `SqliteStoreFoldRollupsTrimsNothingInStepAtBound`. Changed: Approach, Tests per criterion, Options 6, spec E14 and E11, record 0225.
2. minor, no fallback for fact 11. Accepted: fact 11 holds the extrapolation (about 2.5 s for a year and 7.4 s for 1,095 days on the
   DS918+) and the fallback `KeptAddressesHourly` 50 with what it changes and who approves it; task 10 names it. Unlike the objection
   says, step 17 changes one literal (the `_1h` partial index), edited in place because it is unreleased.
3. minor, the benchmark snapshots understate cost. Accepted: listeners in every regular snapshot, a worst-case step of full snapshots up to
   the 2 MiB budget, and schema step 17 timed (Approach, *Benchmark*; `docs/BENCHMARKS.md`).
4. minor, the yearly disk cost. Accepted: estimated in *Benchmark* and stated from the measured row size in `docs/BENCHMARKS.md` and the
   area document (*Documentation updates*).

## Security review

1. BLOCKING, *Security areas touched* incomplete. Resolved: line 5 is the proposed line verbatim.
2. Disk bound per source and capture day only. Resolved: the *Disk* bullet is the proposed text verbatim, and the area document says the
   same (*Documentation updates*).
3. The default `rollups_1h` is 1,095 days. Resolved: N2 adds a read of all 1,095 days at a 1-day step, held to the same 10-second
   deadline; the benchmark writes 1,095 days and times it, `docs/BENCHMARKS.md` states its median, fact 11 extrapolates it (about 7.4 s on
   the DS918+) and its fallback covers it.

## Out of scope / follow-ups

- The network view and the read gate around `IConnectionRollupReader` (#51).
- What the spec's *Out of scope* lists.
