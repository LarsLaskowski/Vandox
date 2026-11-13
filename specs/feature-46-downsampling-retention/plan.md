# Plan: Downsampling and retention

Source: Issue #46 | [spec.md](spec.md)
Tier: security — the change alters a security control: the deduplication of resent agent records (security area 1, record 0211), besides deleting data in `vandox.db` (area 9) and parsing new options (area 10).
Security areas touched: 1 (ingest: `BatchWriter`, `AgentTimeline` skip a resend whose copy was removed), 9 (`vandox.db`: bounded deletes, new tables), 10 (`retention.*` options in `BackendConfigLoader`), 12 (new log lines of `vandoxd`: counts and store errors only)

## Problem / root cause

See [spec.md](spec.md), *Problem*: nothing is ever removed, and no rollups exist.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Nothing removes stored data today; retention is planned only | `docs/areas/storage.md:119-140`; no `DELETE FROM records` in `src/` | confirmed |
| 2 | All writes share one writer connection behind one `SemaphoreSlim` | `src/Vandox.Storage/SqliteStore.cs:69`, `:488`, `:735` | confirmed |
| 3 | Duplicates are prevented only by the unique index on `(agent_id, seq)` and `ON CONFLICT DO NOTHING` | `SchemaMigrator.cs:34`, `BatchWriter.cs:49` | confirmed |
| 4 | The timeline counts `floor <= seq < max` as an arrival and fills open and final gaps (`state <> 'closed'`) | `AgentTimeline.cs:345`, `AgentTimelineQueries.cs:33`; spec E11 corrected to "open or final" | confirmed |
| 5 | `records.id` is a plain `INTEGER PRIMARY KEY`, so the highest deleted ID is given again | `SchemaMigrator.cs:21`; scratch check with Python `sqlite3`: insert 3 rows, delete id 3, insert → id 3 | confirmed |
| 6 | `vandoxd templates` clears all statistics before recounting | `TemplateRebuilder.cs:21` (`ClearStatements`) | confirmed |
| 7 | Incidents are built only by `vandoxd incidents`, which detects kills with `SignatureDetector` and `IncidentSignatures.OomKill` over every line | `docs/areas/incidents.md:12-15`, `IncidentReconstructor.cs:105-124` | confirmed |
| 8 | The report counts an hour observed from lines of source `journal`/`syslog`; agent journal lines have source `journal`; every outage counts in the time-of-day rule | `forensics-report.md:34-38, 66`, `agent-log-shipping.md:41`, `ForensicsAnalysis.cs:217` | confirmed |
| 9 | No hosted background service exists in `vandoxd`; the store is disposed only after the app (`await using` around `ServeAsync`) | `ServeCommand.cs:95-101, 166`; no `BackgroundService` in `src/` | confirmed |
| 10 | The database runs without `auto_vacuum`; deleted pages are reused, the file does not shrink | no `vacuum` in `src/Vandox.Storage`; SQLite documentation (`PRAGMA auto_vacuum`, default NONE) | confirmed |
| 11 | Async waiters of `SemaphoreSlim` are released in FIFO order, so a queued ingest write runs before the next retention step | to verify: Tester, step 5, test of AC6 (fails if a second step overtakes the queued write) | unverified |
| 12 | The bundled SQLite of Microsoft.Data.Sqlite offers the `dbstat` table, used to size the allowance of N3 | to verify: Tester, step 5; without it the allowance is the summed `length()` of the kept tables' columns plus 25 % | unverified |
| 13 | Records of origin `import` are told apart by `records.origin`, and `StoredRecord.Record.Origin` carries it to the reporter | `SchemaMigrator.cs:23`, `DataRecord.cs:13` | confirmed |
| 14 | Record 0177 is released (v0.2.0) and expects retention to let ingest resume at the floor | `git tag --contains` → `v0.2.0`; `0177:39`; amended by 0212 | confirmed |
| 15 | Record 0107 (released) asks retention to move or keep notes; spec keeps them | `0107:88`; spec E7 | confirmed |
| 16 | A tool file under `tools/` appears in no coverage report (no test assembly loads it), as for `BackfillBenchmark.cs` | `ls tests` (no tool references); earlier benchmark PRs passed the gate | confirmed |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `RetentionPeriodTryParseAcceptsDaysAndUnlimited`, `RetentionPeriodTryParseRefusesOtherForms` (`0d`, `10000d`, `30`, `30D`, `1y`, `+30d`, ` 30d`, `030d`, `Unlimited`, empty) | `tests/Vandox.Core.Tests/RetentionPeriodTests.cs` |
| AC1 | `RetentionConfigDefaultsMatchTheSpec`, `RetentionConfigDefaultsAreValidPeriods` | `tests/Vandox.Core.Tests/RetentionConfigTests.cs` |
| AC1 | `LoadRefusesInvalidRetentionPeriodWithReason` (each of the seven keys, exact E1 text), `LoadReadsRetentionSection` | `tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs` |
| AC1 | `RetentionServiceCreatePolicyReadsEveryKey` (unlimited → null, kill window from `incidents.*`) | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC2 | `FoldRollupsMatchesComputationFromAllValues` (several series and labels, both tiers, ties of last by time then record ID), `FoldRollupsEmptyLabelsAndNullLabelsAreOneSeries` | `tests/Vandox.Storage.Tests/SqliteStoreRollupTests.cs` |
| AC2 | `RetentionServiceFoldsWithinFiveMinutesOfABucketEnd` (`FakeTimeProvider`) | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC3 | `FoldRollupsCorrectsBucketsWithRawValuesStored`, `FoldRollupsMergesIntoBucketsWhoseRawValuesWereRemoved`, `FoldRollupsTouchesOnlyBucketsOfNewRows`, `FoldRollupsSkipsBucketsPastTheirTier` | `tests/Vandox.Storage.Tests/SqliteStoreRollupTests.cs` |
| AC3 | `WriteBatchSkipsResendWhoseCopyWasRemoved` (bucket unchanged, nothing stored, batch acknowledged), `WriteBatchStoresNumberInOpenOrFinalGap` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC4 | `RemoveExpiredKeepsOneSecondBeforeAndRemovesOneSecondAfter` (each kind), `RemoveExpiredKeepsIncidentsByDefault`, `RemoveExpiredRemovesExpiredIncidentWithoutNote`, `RemoveExpiredKeepsExpiredIncidentWithNote`, `RemoveExpiredKeepsFutureCaptureTime` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC5 | `RemoveExpiredNeverRemovesMetricsAboveTheWatermark`, `RemoveExpiredFailedStepLeavesDatabaseUnchanged` (a test trigger raises on delete), `RemoveExpiredBusyStepThrowsAndChangesNothing` (second connection holds `BEGIN IMMEDIATE`), `RemoveExpiredKeepsTheHighestRecordId`, `MigrationFromVersion13FoldsBeforeRemoval` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC5 | `RetentionServiceLogsBusyAndContinuesNextPass`, `RetentionServiceStopsBetweenSteps` | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC5 | `ServeRunsRetentionServiceOnlyInTheService`, `CommandsOtherThanServeRemoveNothing` (import, incidents, report, templates on a store with expired data) | `tests/Vandox.Backend.Tests/ServeTests.cs`, `tests/Vandox.Backend.Tests/CommandLineTests.cs` |
| AC6 | `WriteBatchWaitsAtMostOneRetentionStep` (store step hook; the queued batch runs before the next step), `RemoveExpiredStepTouchesAtMostTheStepBound`, `FoldRollupsStepReadsAtMostTheStepBound` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC6 | manual: Product Manager, `--retention` scenario of the storage benchmark published from the branch head (`docs/BENCHMARKS.md`, *Retention steps*) on the DS918+ SSD volume, before step 9: median removal and fold step at or below the median batch of the same run | — |
| AC7 | `RetentionKeepsSizeWithinAllowanceUnderConstantLoad` (P = 2 days, hourly passes, metrics every 10 s for 20 series, log lines, snapshots, one event and one gap per hour) | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC8 | `SearchReturnsNoRemovedLine`, `RemoveExpiredReportsNoGapForRemovedNumbers`, `RemoveExpiredKeepsTemplateHoursHoldingLines`, `RemoveExpiredRemovesTemplateHoursPastHourlyPeriod` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC8 | `RebuildKeepsStatisticsOfHoursWithoutStoredLines`, `RebuildRecountsHoursWithStoredLines` | `tests/Vandox.Storage.Tests/SqliteStoreTemplateRebuildTests.cs` |
| AC9 | `RemoveExpiredKeepsImportedRecords`, `RemoveExpiredKeepsEventsUntilEventsPeriod` (each event kind, closed and final gap rows, open gap never), `RemoveExpiredKeepsListenerFindingsWithTheirSnapshots`, `RemoveExpiredKeepsNewestConnectionMarkPerAgent`, `RemoveExpiredKeepsRecordsInIncidentWindowsAndHolds` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC10 | `RetentionServiceLogsRollupAndRemovalCounts`, `RetentionServiceLogsNothingForAPassWithoutChange`, `RetentionServiceLogsFailedStepWithOperation` | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC11 | `ScanHoldsWindowOfCatalogKill`, `ScanHoldsWindowOfKernelEventKill`, `ScanCatchesLateLines`, `IncidentAfterRemovalEqualsIncidentBefore` (real store, reconstructor run on an unchanged copy and after removal) | `tests/Vandox.Backend.Tests/KillHoldScannerTests.cs` |
| AC12 | `ReportSkipsAgentLinesAndSnapshotsPastTheirPeriods`, `ReportKeepsImportedHours` | `tests/Vandox.Backend.Tests/ForensicsReporterTests.cs` |
| AC12 | `AddIncidentDoesNotJudgeOutageInUnobservedHourBeforeHorizon`, `AddIncidentJudgesAsBeforeWithoutHorizon` | `tests/Vandox.Core.Tests/ForensicsAnalysisTests.cs` |

## Approach

**Configuration (Core).** A `retention:` section with seven keys (spec N1). `RetentionPeriod.TryParse` accepts one to four ASCII digits
without a leading zero followed by `d`, from `1d` to `9999d`, or exactly `unlimited` (null). `BackendConfigLoader` refuses anything
else with the spec's E1 reason `must be a whole number of days from 1d to 9999d, such as 30d, or unlimited`. `RetentionService.CreatePolicy`
(Backend) parses the seven keys into the store's `RetentionPolicy`, with `incidents.window_before`/`after` as the kill window.

**Schema step 14 (Storage).** `metric_rollups_5m` and `metric_rollups_1h` (`STRICT`, `WITHOUT ROWID`): `source`, `name`, `labels`
(text, `''` for none), `bucket` (start, ns), `min_value`, `max_value`, `sum_value` (REAL), `count` (INTEGER ≥ 1), `last_value`,
`last_at`, `last_record_id`; primary key `(source, name, labels, bucket)`, index on `bucket`. `retention_holds`: `id`, `started_at`,
`ended_at` (`CHECK (ended_at > started_at)`), index on `started_at`. `meta` keys `rollup_after_id`, `kill_scan_before`,
`kill_scan_after_id` (absent = 0 / minimum). `StorageLimits.SchemaVersion` becomes 14.

**Fold step (record 0208).** Under the writer lock, one transaction: read at most `StorageLimits.RollupStepRows` (10,000) `metrics`
rows with `record_id > rollup_after_id` in record ID order, fold each into its 5-minute and hourly bucket (min, max, sum, count; last
by `(captured_at, record_id)`), upsert the touched buckets, set `rollup_after_id`. A bucket already past its tier's period is not
created. All origins are folded.

**Kill scan (record 0210), in the Backend.** `KillHoldScanner` runs before each removal pass. Target `T` = the latest finite removal
horizon among raw, logs, snapshots and events (`now − period`) plus `window_before`. (a) Forward: `log_line` and `kernel_event` records
with `kill_scan_before <= captured_at < T` in capture order (then ID), pages of 1,000, one `SignatureDetector` across the pages; (b)
late: records of those kinds with ID above `kill_scan_after_id` and capture time below `kill_scan_before`, in capture order, a fresh
detector. Each `oom_kill` event or `kernel_event` of type `oom_kill` gives `[kill − before, kill + after]`; overlapping windows merge.
Holds and the new position (`kill_scan_before = T`, `kill_scan_after_id` = highest record ID read when the pass began) are saved per
page in one write. 

**Removal step (records 0209, 0210, 0212).** One step removes at most `StorageLimits.RemovalStepRecords` (5,000) records of one
kind, or as many rows of one derived table, in one transaction, in this order until nothing is left: raw metrics, log lines,
snapshots, events, gap rows, 5-minute rollups, hourly rollups, template hours, incidents, holds. A record is removed only when all
hold: origin is not `import`; `captured_at < min(now − period of its kind, kill_scan_before − window_before)`; `id <=
kill_scan_after_id`; for metrics `record_id <= rollup_after_id`; `id` is not the highest record ID; its capture time lies in no stored
incident's window and no hold; a connection snapshot is not the agent's newest mark and has no listener finding (`new_count > 0`,
`changed_count > 0`, or a comparison with `finding = 1`), which move it to the events period. Kinds: `metric` → raw; `log_line` →
logs; `process_snapshot`, `connection_snapshot`, `mariadb_status` → snapshots; `kernel_event`, `service_state`, `backup_run`, `gap` →
events. Per record, in order: `log_fts` `delete` command with the old message, `log_lines`/`metrics`, `connection_marks`,
`listener_comparisons`, `records`; `agent_sample_times` rows below the raw horizon go in the metric steps. Gap rows: `closed` or
`final` with `to_at` before the events horizon; `open` never. Template hours and source hours: `hour` before `now − rollups_1h` where
no stored log line of that source lies in that hour; a template with no hour left is removed (its `count`, `first_seen`, `last_seen`
are history and stay unchanged otherwise). Incidents: `ended_at` before `now − incidents` and no row in `incident_notes`. Holds:
`ended_at` before the same horizon. `agent_streams` and `agent_clock_samples` are never touched. No `VACUUM`.

**Resend after removal (record 0211).** `AgentTimeline.WasReceived(seq)` is true for `floor_seq <= seq <= max_seq` when no
non-closed sequence gap contains `seq`; `SqliteStore.WriteBatchAsync` skips such records before the insert (not stored, not placed,
counted in `WriteResult` as duplicates, as a conflict is today).

**Template rebuild.** `TemplateRebuilder` clears only the template and source hours of `(source, hour)` pairs that hold a stored log
line, recounts the stored lines, and keeps the rest; templates are recomputed from their remaining hours; a template without hours is
removed. The run marker rules stay.

**Service (Backend).** `RetentionService : BackgroundService`, registered only by `ServeCommand`. It runs at start and on a 5-minute
timer of the injected `TimeProvider`: fold steps until a step reports no more; when an hour has passed since the last removal pass
(or none ran yet), the kill scan, then removal steps until none is left. Between steps it checks the stopping token; a step itself is
never cancelled once it holds the lock (the writer is never interrupted). A `StoreException` for a busy database logs the warning and
ends the pass; any other `StoreException` logs the error and ends the pass; the next timer tick retries.

**Log lines (layout fixed, `[LoggerMessage]` in `BackendLog`).** Event 69, Information, `retention rollup {Values} {Buckets}` after a
fold pass that folded at least one value; event 70, Information, `retention removal {MetricValues} {LogLines} {Snapshots} {Events}
{Gaps} {FiveMinuteValues} {HourlyValues} {TemplateHours} {Incidents} {Holds}` after a removal pass that removed or held anything;
event 71, Warning, `retention step busy {Operation}`; event 72, Error, `retention step failed {Operation} {Error}`. `{Operation}` is
one of `fold`, `scan`, `remove`; `{Error}` is the `StoreException` message.

**Report (spec S10).** `ForensicsReporter` gains a `ReportRetention` (the logs and snapshots periods). It computes the horizons from
its clock and skips lines and process snapshots of origin other than `import` captured before them. `ForensicsAnalysis.JudgedFrom` (the
logs horizon): an outage whose first kill is before it and whose kill hour is not observed is counted as an outage but in no rule.
No new text. `ReportService` and `ReportCommand` pass the configured periods.

**Benchmark.** `tools/Vandox.StorageBenchmark --retention [--size N]`: writes five batches of N records, ages them by a fixed clock,
times five fold and five removal steps and prints the three medians.

## Options considered

- Rollups in the write transaction, or recompute from raw on dirty marks: lost to folding by watermark (record 0208).
- Remove by receive time or every origin alike; drop events with the raw data: lost to the Product Manager's answers (record 0209).
- Freeze old incidents or reconstruct them inside the job: lost to holds (record 0210).
- Tombstones per removed record, or accept duplicates: lost to the stream check (record 0211).
- Vacuum after removal or switching `auto_vacuum`: lost to plain deletes, #55 owns shrinking (record 0212).
- A `vandoxd retention` sub-command run by the operator's scheduler: the container has no scheduler, and a second process waits on
  the 5-second busy timeout while ingest answers `503`; inside the service the steps queue on the one writer lock with ingest.
- Steps bounded by a time budget instead of a row count: not testable without a real clock; the row bound plus the reference-host
  measurement (AC6) gives the same guarantee.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `Configuration/RetentionConfig.cs`, `RetentionPeriod.cs` (new); `BackendConfig.cs`, `BackendConfigLoader.cs` | section, parsing, validation |
| Vandox.Core | `Reports/ForensicsAnalysis.cs` | `JudgedFrom` |
| Vandox.Storage | `IRetentionStore.cs`, `IRollupReader.cs`, `RetentionPolicy.cs`, `RollupTier.cs`, `RollupBucket.cs`, `RollupQuery.cs`, `RollupStepResult.cs`, `RemovalStepResult.cs`, `KillScanQuery.cs`, `KillScanProgress.cs`, `RetentionHold.cs`, `RollupFolder.cs`, `RetentionRemover.cs`, `RetentionQueries.cs` (new) | the store side |
| Vandox.Storage | `SqliteStore.cs`, `SchemaMigrator.cs`, `StorageLimits.cs`, `AgentTimeline.cs`, `TemplateRebuilder.cs` | interfaces, step 14, constants, resend skip, rebuild |
| Vandox.Backend | `Retention/RetentionService.cs`, `Retention/KillHoldScanner.cs`, `Reports/ReportRetention.cs` (new); `Hosting/ServeCommand.cs`, `Hosting/BackendLog.cs`, `Reports/ForensicsReporter.cs`, `Reports/ReportService.cs`, `Cli/ReportCommand.cs` | job, wiring, logs, report |
| tools | `Vandox.StorageBenchmark/RetentionBenchmark.cs` (new), `Program.cs` | `--retention` |
| deploy | `backend/vandoxd.yaml` | commented `retention:` section |

## Signatures (for the Dev's skeleton)

All types sealed unless an enum or interface; properties { get; init; } unless stated; one declaration per span, a property as
Type.Member: type.

Core (Vandox.Core.Configuration, Vandox.Core.Reports):

- `class RetentionConfig`, its properties { get; set; } with [ConfigKey] and default: `RetentionConfig.Raw: string` (raw,
  "30d"), `RetentionConfig.FiveMinuteRollups: string` (rollups_5m, "365d"), `RetentionConfig.HourlyRollups: string` (rollups_1h,
  "1095d"), `RetentionConfig.Logs: string` (logs, "90d"), `RetentionConfig.Snapshots: string` (snapshots, "30d"),
  `RetentionConfig.Events: string` (events, "1095d"), `RetentionConfig.Incidents: string` (incidents, "unlimited")
- `static class RetentionPeriod`, `RetentionPeriod.Unlimited: const string = "unlimited"`, `public static bool TryParse(string text, out TimeSpan? value)`
- `BackendConfig.Retention: RetentionConfig` (attribute ConfigKey("retention"), get-only, a new instance)
- `ForensicsAnalysis.JudgedFrom: DateTimeOffset?`

Storage (Vandox.Storage):

- `interface IRetentionStore`, implemented by SqliteStore:
  `Task<RollupStepResult> FoldRollupsAsync(RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)`,
  `Task<KillScanProgress> KillScanProgressAsync(CancellationToken cancellationToken)`,
  `Task<IReadOnlyList<StoredRecord>> ScanRecordsAsync(KillScanQuery query, CancellationToken cancellationToken)`,
  `Task SaveKillScanAsync(IReadOnlyList<RetentionHold> holds, KillScanProgress progress, CancellationToken cancellationToken)`,
  `Task<RemovalStepResult> RemoveExpiredAsync(RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)`
- `interface IRollupReader`, implemented by SqliteStore: `Task<IReadOnlyList<RollupBucket>> RollupsAsync(RollupQuery query, CancellationToken cancellationToken)`
- `class RetentionPolicy`: `RetentionPolicy.Raw: TimeSpan?`, `RetentionPolicy.FiveMinuteRollups: TimeSpan?`,
  `RetentionPolicy.HourlyRollups: TimeSpan?`, `RetentionPolicy.Logs: TimeSpan?`, `RetentionPolicy.Snapshots: TimeSpan?`,
  `RetentionPolicy.Events: TimeSpan?`, `RetentionPolicy.Incidents: TimeSpan?` (null = unlimited), `RetentionPolicy.KillWindowBefore: TimeSpan`,
  `RetentionPolicy.KillWindowAfter: TimeSpan`
- `enum RollupTier` with `RollupTier.FiveMinutes`, `RollupTier.Hour`
- `class RollupBucket`: `RollupBucket.Tier: RollupTier`, `RollupBucket.Source: string`, `RollupBucket.Name: string`,
  `RollupBucket.Labels: string` (empty when none), `RollupBucket.Start: DateTimeOffset`, `RollupBucket.Minimum: double`,
  `RollupBucket.Average: double`, `RollupBucket.Maximum: double`, `RollupBucket.Last: double`, `RollupBucket.LastAt: DateTimeOffset`,
  `RollupBucket.Count: long`
- `class RollupQuery`: `RollupQuery.Tier: RollupTier`, `RollupQuery.Source: string`, `RollupQuery.Name: string`,
  `RollupQuery.From: DateTimeOffset`, `RollupQuery.To: DateTimeOffset`, `RollupQuery.Limit: int`
- `class RollupStepResult`: `RollupStepResult.Values: int`, `RollupStepResult.Buckets: int`, `RollupStepResult.More: bool`
- `class RemovalStepResult`: `RemovalStepResult.MetricValues: int`, `RemovalStepResult.LogLines: int`, `RemovalStepResult.Snapshots: int`,
  `RemovalStepResult.EventRecords: int`, `RemovalStepResult.Gaps: int`, `RemovalStepResult.FiveMinuteValues: int`,
  `RemovalStepResult.HourlyValues: int`, `RemovalStepResult.TemplateHours: int`, `RemovalStepResult.RemovedIncidents: int`,
  `RemovalStepResult.Holds: int`, `RemovalStepResult.More: bool`
- `class KillScanQuery`: `KillScanQuery.From: DateTimeOffset`, `KillScanQuery.To: DateTimeOffset`, `KillScanQuery.AboveId: long`,
  `KillScanQuery.AfterCapturedAt: DateTimeOffset`, `KillScanQuery.AfterId: long`, `KillScanQuery.Limit: int`
- `class KillScanProgress`: `KillScanProgress.ScannedBefore: DateTimeOffset`, `KillScanProgress.ScannedId: long`,
  `KillScanProgress.HighestId: long`
- `class RetentionHold`: `RetentionHold.Start: DateTimeOffset`, `RetentionHold.End: DateTimeOffset`
- `StorageLimits.RollupStepRows: internal const int = 10000`, `StorageLimits.RemovalStepRecords: internal const int = 5000`,
  `StorageLimits.SchemaVersion: const int = 14` (changed)
- `internal bool WasReceived(long seq)` on AgentTimeline
- `SqliteStore.RetentionStepStarted: internal Func<Task>?` ({ get; set; }, test hook of AC6, called with the lock held)

Backend (Vandox.Backend.Retention, Vandox.Backend.Reports, Vandox.Backend.Hosting):

- `class RetentionService` (a BackgroundService): `public RetentionService(IRetentionStore store, RetentionPolicy policy, SignatureCatalog catalog, TimeProvider clock, ILogger logger)`,
  `internal Task RollUpAsync(CancellationToken cancellationToken)`, `internal Task RemoveAsync(CancellationToken cancellationToken)`,
  `internal static RetentionPolicy CreatePolicy(BackendConfig config)`
- `class KillHoldScanner` (internal): `internal KillHoldScanner(IRetentionStore store, SignatureCatalog catalog)`,
  `internal Task<int> ScanAsync(RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)` (the holds added)
- `class ReportRetention`: `ReportRetention.Logs: TimeSpan?`, `ReportRetention.Snapshots: TimeSpan?`
- `public ForensicsReporter(IRecordReader records, IIncidentStore incidents, IListenerHistoryReader listeners, SignatureCatalog catalog, TimeSpan lookback, ReportRetention retention, TimeProvider clock)`
  (new overload; the old constructor stays and passes an empty `ReportRetention`)
- `internal static partial void RetentionRolledUp(this ILogger logger, int values, int buckets)`,
  `internal static partial void RetentionRemoved(this ILogger logger, int metricValues, int logLines, int snapshots, int events, int gaps, int fiveMinuteValues, int hourlyValues, int templateHours, int incidents, int holds)`,
  `internal static partial void RetentionStepBusy(this ILogger logger, string operation)`,
  `internal static partial void RetentionStepFailed(this ILogger logger, string operation, string error)` in BackendLog

Existing files the skeleton rewrites (read, not final): BackendConfig.cs, SqliteStore.cs (interface list), StorageLimits.cs,
ForensicsReporter.cs, ForensicsAnalysis.cs, BackendLog.cs, AgentTimeline.cs.

## Test files

`tests/Vandox.Core.Tests/RetentionPeriodTests.cs`, `RetentionConfigTests.cs`, `BackendConfigLoaderTests.cs`, `ForensicsAnalysisTests.cs`;
`tests/Vandox.Storage.Tests/SqliteStoreRollupTests.cs`, `SqliteStoreRetentionTests.cs`, `SqliteStoreTemplateRebuildTests.cs`;
`tests/Vandox.Backend.Tests/RetentionServiceTests.cs`, `KillHoldScannerTests.cs`, `ForensicsReporterTests.cs`, `ServeTests.cs`,
`CommandLineTests.cs`. A retention store double for `RetentionServiceTests` (`ScriptedRetentionStore`, Backend tests) is the Tester's,
with its *Test doubles* row reported for `.squad/project.md`.

Existing call sites of changed signatures: `ForensicsReporter` keeps its constructor, so `ForensicsReporterTests.cs:502` and
`ReportPageTests.cs:74` need no change. `SchemaMigrator` tests that assert the version 13 (`SqliteStoreOpenTests`) are adapted by the
Tester. `TemplateRebuilderTests`/`SqliteStoreTemplateRebuildTests` cases that expect a full clear keep passing (every hour of their data
holds lines); the Tester confirms.

Code no unit test can cover: `tools/Vandox.StorageBenchmark/RetentionBenchmark.cs` and its option in `Program.cs` — a measurement
program for the reference host; no test assembly loads the tool, so it appears in no coverage report (fact 16), as the other benchmark
files. No exclusion is added.

## Areas

- New area *Retention and rollups* (`docs/areas/retention.md`, Lead decision): scope "which data each period covers and what outlives
  it, the rollups and their folding, kill holds, passes and steps, failure and shutdown, log lines"; not here: the schema (Storage), the
  options' form (Configuration options), the report's rules (Forensics report).
- Storage, Storage writing, Storage reading, Configuration options, Log templates, Incidents, Forensics report, Ingest endpoint,
  Listener changes, Log import, Backend host: see *Documentation updates*.

## Documentation updates

All by the Dev, after the code is green:

- `docs/areas/retention.md` (new) from `_template.md`: the content of *Approach* as rules (periods per kind, imports, events, windows
  and holds, folding, step bounds, pass timing, busy and failure, shutdown, log lines, E8), links records 0208-0212; `docs/areas/README.md`:
  its index row.
- `docs/areas/storage.md`: *Retention (planned)* replaced by a short paragraph linking `retention.md`; schema version 14; the tables
  `metric_rollups_5m`, `metric_rollups_1h`, `retention_holds` and the `meta` keys; migration step **14**; *Implementation* names
  `RollupFolder`, `RetentionRemover`; related decisions 0208-0212.
- `docs/areas/storage-writing.md`: line 44 (deletes keep `log_fts`, now done by retention); deduplication: the resend rule of record
  0211; line 179 (notes: kept, record 0209); template rebuild keeps hours without stored lines.
- `docs/areas/storage-reading.md`: the rollup reader `IRollupReader`; line 94 "whole retention" stays true.
- `docs/areas/configuration-options.md`: the seven `retention.*` rows and a paragraph with the form and the E1 error text.
- `docs/areas/log-templates.md` lines 66-67 and 123-124: the statistics follow `retention.rollups_1h`, kept while an hour holds a line;
  rebuild behavior.
- `docs/areas/incidents.md` lines 113 and 119-120: windows and kill holds are kept (record 0210); forged kills residual.
- `docs/areas/forensics-report.md`: *Observed hours* and *What it reads*: agent lines and snapshots past their periods are skipped; an
  outage before the logs horizon in an unobserved hour is in no rule.
- `docs/areas/ingest-endpoint.md` line 149: retention bounds the database by age but frees no space on the volume (record 0212).
- `docs/areas/listener-changes.md`: findings and the newest mark per agent kept for the events period.
- `docs/areas/log-import.md` line 166: imported records are never removed by retention.
- `docs/areas/backend-host.md`: the service runs the retention job; its four log lines.
- `docs/ARCHITECTURE.md`: line 15 drop "retention" from the planned list; *Storage and retention*: the table (no longer planned, add
  events 3 years, imported data kept, incident and kill windows kept), the migrations sentence extended with versions 11 to 14 from
  `storage.md`, *Interfaces* adds `IRetentionStore` and `IRollupReader`, records 0208-0212; *Components* (`src/Vandox.Backend`): the
  retention job.
- `README.md` *Backend options*: the seven keys; `deploy/backend/vandoxd.yaml`: a commented `retention:` section with the defaults.
- `docs/BENCHMARKS.md`: *Retention steps*, procedure and result table for AC6.
- `.squad/project.md` (orchestrator writes the reported text): security area 9 adds "retention deletes only through bounded steps of
  `RetentionRemover`"; *Test doubles* row for `ScriptedRetentionStore` (Tester's text). `CHANGELOG.md`: none, written at release
  (`docs/CONTRIBUTING.md:128`).

## Architecture check

- *No data gaps unless explicitly recorded*: gap records and closed/final gap rows stay for the events period, open gaps forever, and
  no removed number is reported missing (stream row kept, record 0211); a summarized span keeps its gaps.
- *Backfilled data never raises an alert by itself*: `records.live` is untouched; rollups alert nothing.
- *A hanging collector never blocks the agent*: agent untouched.
- Ingest stays bounded: a step is no larger than one batch (AC6); the floor rule of 0177 stays, amended by 0212.

## Security considerations

- [x] Limits: a period is checked on the YAML string after decoding (four ASCII digits and `d`, or `unlimited`); no other parsed input.
- [x] Exceptions reaching a user-visible text: a configuration error shows only the E1 reason with the key; a store failure reaches only
  the log, as `StoreException.Message` (operation text and SQLite's error, never stored data). Holds and the scan bound what a
  compromised agent can pin to its span (record 0210 residual); every statement binds values as parameters.

## Decision records

- `docs/decisions/0208-metric-rollups-are-folded-from-new-rows-by-record-id-never-recomputed-from-raw.md` (Proposed)
- `docs/decisions/0209-retention-removes-by-capture-time-per-kind-imports-kept-events-kept-three-years.md` (Proposed)
- `docs/decisions/0210-data-in-incident-windows-and-detected-kill-windows-is-kept-with-the-incident.md` (Proposed)
- `docs/decisions/0211-a-resent-record-whose-stored-copy-was-removed-is-not-stored-again.md` (Proposed)
- `docs/decisions/0212-retention-frees-space-inside-the-database-file-not-on-the-volume.md` (Proposed, amends released 0177)

## Out of scope / follow-ups

Spec *Out of scope*. Follow-up issue: "[Backend] Rollups of process and connection snapshots per program" (#50 needs memory per
program beyond 30 days).
