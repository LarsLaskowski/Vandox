# Plan: Downsampling and retention

Source: Issue #46 | [spec.md](spec.md)
Tier: security — the change alters a security control: the deduplication of resent agent records (security area 1, record 0211), besides deleting data in `vandox.db` (area 9) and parsing new options (area 10).
Security areas touched: 1 (ingest: `BatchWriter`, `AgentTimeline` skip a resend whose copy was removed), 9 (`vandox.db`: bounded deletes, new tables), 10 (`retention.*` options in `BackendConfigLoader`), 12 (new log lines of `vandoxd`: counts and store errors only)

## Problem / root cause

See [spec.md](spec.md).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 2 | All writes share one writer connection behind one `SemaphoreSlim` | `src/Vandox.Storage/SqliteStore.cs:69`, `:488`, `:735` | confirmed |
| 3 | Duplicates are prevented only by the unique index on `(agent_id, seq)` and `ON CONFLICT DO NOTHING` | `SchemaMigrator.cs:34`, `BatchWriter.cs:49` | confirmed |
| 4 | The timeline counts `floor <= seq < max` as an arrival and fills open and final gaps (`state <> 'closed'`) | `AgentTimeline.cs:345`, `AgentTimelineQueries.cs:33`; spec E11 corrected to "open or final" | confirmed |
| 5 | `records.id` is a plain `INTEGER PRIMARY KEY`, so the highest deleted ID is given again | `SchemaMigrator.cs:21`; scratch check with Python `sqlite3` | confirmed |
| 6 | `vandoxd templates` clears all statistics before recounting | `TemplateRebuilder.cs:21` (`ClearStatements`) | confirmed |
| 7 | Incidents are built only by `vandoxd incidents`, which detects kills with `SignatureDetector` and `IncidentSignatures.OomKill` over every line | `docs/areas/incidents.md:12-15`, `IncidentReconstructor.cs:105-124` | confirmed |
| 8 | The report counts an hour observed from lines of source `journal`/`syslog`; agent journal lines have source `journal`; every outage counts in the time-of-day rule | `forensics-report.md:34-38, 66`, `agent-log-shipping.md:41`, `ForensicsAnalysis.cs:217` | confirmed |
| 9 | No hosted background service exists in `vandoxd`; the store is disposed only after the app (`await using` around `ServeAsync`) | `ServeCommand.cs:95-101, 166`; no `BackgroundService` in `src/` | confirmed |
| 11 | Async waiters of `SemaphoreSlim` are released in FIFO order, so a queued ingest write runs before the next retention step | Security's scratch measurement: 0 overtakes in 2,000 runs; AC6's test keeps it | confirmed |
| 12 | The bundled SQLite of Microsoft.Data.Sqlite offers the `dbstat` table, used to size the allowance of N3 | Tester, step 5: no `dbstat`; allowance: summed `length()` of the kept tables' columns and index entries' columns, plus 25 % | refuted |
| 17 | Only `records(kind, captured_at)` serves a removal by time; `origin` is not indexed; `gaps` has no index on `to_at` | `SchemaMigrator.cs:36`, `:260-263` | confirmed |
| 18 | Time gaps are stored `open`; `final` is computed when read | `AgentTimeline.cs:792-806`, `GapReadSql.cs:12-14` | confirmed |
| 19 | A metric's labels reach about 200 KB of stored JSON | `wire-format.md:93, 98, 154` | confirmed |
| 20 | Only the kernel writes a `kernel_event` OOM kill: the collector takes kernel-transport entries of facility 0, and `/dev/kmsg` writes get another facility; any local process can write a journal line that the catalog reads as a kill | `agent-kernel-events.md:56-61, 183`; `agent-log-shipping.md:244`, `log-signatures.md:110-115` | confirmed |
| 21 | `BackendConfig.Keys()` lists every key and tests enumerate it | `BackendConfig.cs:59-62`, `BackendConfigLoaderTests.cs:46, 184-193, 789-796` | confirmed |
| 16 | A file under `tools/` appears in no coverage report | no test references the tool, as for `BackfillBenchmark.cs` | confirmed |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `RetentionPeriodTryParseAcceptsDaysAndUnlimited`, `RetentionPeriodTryParseRefusesOtherForms` (`0d`, `10000d`, `30`, `30D`, `1y`, `+30d`, a leading and a trailing space, `030d`, full-width and Arabic-Indic digits, `Unlimited`, `UNLIMITED`, `unlimited` with a trailing space, empty) | `tests/Vandox.Core.Tests/RetentionPeriodTests.cs` |
| AC1 | `RetentionConfigDefaultsMatchTheSpec`, `RetentionConfigDefaultsAreValidPeriods` | `tests/Vandox.Core.Tests/RetentionConfigTests.cs` |
| AC1 | `LoadRefusesInvalidRetentionPeriodWithReason` (each of the seven keys, exact E1 text), `LoadReadsRetentionSection` | `tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs` |
| AC1 | `RetentionServiceCreatePolicyReadsEveryKey` (unlimited → null, kill window from `incidents.*`) | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC2 | `FoldRollupsMatchesComputationFromAllValues` (several series and labels, both tiers, ties of last by time then record ID), `FoldRollupsEmptyLabelsAndNullLabelsAreOneSeries` | `tests/Vandox.Storage.Tests/SqliteStoreRollupTests.cs` |
| AC2 | `RetentionServiceFoldsWithinFiveMinutesOfABucketEnd` (`FakeTimeProvider`) | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC3 | `FoldRollupsCorrectsBucketsWithRawValuesStored`, `FoldRollupsMergesIntoBucketsWhoseRawValuesWereRemoved`, `FoldRollupsTouchesOnlyBucketsOfNewRows`, `FoldRollupsSkipsBucketsPastTheirTier`, `FoldRollupsStopsAtTheLabelByteBudget` (rows with labels at the wire limit escaped to six bytes per character: at most budget plus one row read, watermark committed, next step continues), `FoldRollupsClampsOverflowingSumsAndAdvances` (+1e308 twice in step 1, −1e308 twice in step 2, one bucket: step 2 succeeds, the watermark advances, the sum is finite) | `tests/Vandox.Storage.Tests/SqliteStoreRollupTests.cs` |
| AC3 | `WriteBatchSkipsResendWhoseCopyWasRemoved` (bucket unchanged, nothing stored, batch acknowledged), `WriteBatchStoresNumberInOpenOrFinalGap`, `WriteBatchMixedResendFillAndNewStoresOnlyFillAndNew` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC4 | `RemoveExpiredKeepsOneSecondBeforeAndRemovesOneSecondAfter` (each kind), `RemoveExpiredKeepsIncidentsByDefault`, `RemoveExpiredRemovesExpiredIncidentWithoutNote`, `RemoveExpiredKeepsExpiredIncidentWithNote`, `RemoveExpiredKeepsFutureCaptureTime` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC5 | `RemoveExpiredNeverRemovesMetricsAboveTheWatermark`, `RemoveExpiredFailedStepLeavesDatabaseUnchanged` (a test trigger raises on delete), `RemoveExpiredBusyStepThrowsAndChangesNothing` (second connection holds `BEGIN IMMEDIATE`), `RemoveExpiredKeepsTheHighestRecordId`, `MigrationFromVersion13FoldsBeforeRemoval` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC5 | `RetentionServiceLogsBusyAndContinuesNextPass`, `RetentionServiceStopsBetweenSteps` | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC5 | `ServeRunsRetentionServiceOnlyInTheService`, `CommandsOtherThanServeRemoveNothing` (import, incidents, report, templates on a store with expired data) | `tests/Vandox.Backend.Tests/ServeTests.cs`, `tests/Vandox.Backend.Tests/CommandLineTests.cs` |
| AC6 | `WriteBatchWaitsAtMostOneRetentionStep` (store step hook; the queued batch runs before the next step; imported lines and held windows below the horizon), `RemoveExpiredStepTouchesAtMostTheStepBound`, `RemoveExpiredReachesRowsBehindKeptRows` (imported and held rows below the horizon, more than one step's worth, before removable ones), `RemoveExpiredQueriesUseIndexSearches` (`EXPLAIN QUERY PLAN` of every removal statement shows `SEARCH`, no `SCAN`), `FoldRollupsStepReadsAtMostTheStepBound` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC6 | manual: Product Manager, `--retention` scenario of the storage benchmark published from the branch head (`docs/BENCHMARKS.md`, *Retention steps*) on the DS918+ SSD volume, before step 9: median removal and fold step at or below the median batch of the same run | — |
| AC7 | `RetentionKeepsSizeWithinAllowanceUnderConstantLoad` (P = 12 hours, metrics every 10 s for 5 series, log lines, snapshots, one event and one gap record per hour, origin `agent` with sequence numbers, so sample times and gap rows count; allowance as fact 12; at least three periods; besides the total, after every pass each kind's stored count at most what its period plus one pass interval holds, and the summed `length(block)` of `log_fts_data` at the end at most 1.25 times its value after the first period, so neither a kind left unremoved nor FTS growth hides behind the metrics, Reviewer R3) | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC8 | `SearchReturnsNoRemovedLine`, `RemoveExpiredReportsNoGapForRemovedNumbers`, `RemoveExpiredKeepsTemplateHoursHoldingLines`, `RemoveExpiredRemovesTemplateHoursPastHourlyPeriod`, `WriteBatchLateValueBelowTheSampleFloorOpensNoTimeGap` (narrows a containing time gap, opens none), `RemoveExpiredRaisesTheSampleFloorBeforeTheFirstPrune` (more than 20,000 sample times; a late value in the range the first step deleted, written between two steps, opens no time gap) | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC8 | `RebuildKeepsStatisticsOfHoursBeforeTheLogPeriod`, `RebuildKeepsStatisticsOfPartlyRemovedHours`, `RebuildRecountsHoursInTheLogPeriod`, `RebuildWithoutLogPeriodRecountsAll` | `tests/Vandox.Storage.Tests/SqliteStoreTemplateRebuildTests.cs` |
| AC9 | `RemoveExpiredKeepsImportedRecords`, `RemoveExpiredKeepsEventsUntilEventsPeriod` (each event kind), `RemoveExpiredRemovesGapRowsOnlyPastTheGapHorizon` (closed sequence gap, time gap stored open that reads final, both past the longest period; with `events` shorter than `raw` kept until `raw` passes; never in a window or hold), `RemoveExpiredNeverRemovesOpenOrFinalSequenceGap`, `RemoveExpiredKeepsListenerFindingsWithTheirSnapshots`, `RemoveExpiredKeepsNewestConnectionMarkPerAgent`, `RemoveExpiredKeepsRecordsInHolds`, `RemoveExpiredKeepsHoldOverlappingIncidentWithNote`, `RemoveExpiredPrunesSampleTimesBelowTheRawHorizonKeepingTheNewest`, `WriteBatchAfterSilencePastTheRawPeriodOpensTimeGap` | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC10 | `RetentionServiceLogsRollupAndRemovalCounts` (events 69, 70, 73, each count in its field), `RetentionServiceLogsOnlyRecordsLineWhenNoDerivedDataRemoved`, `RetentionServiceLogsNothingForAPassWithoutChange`, `RetentionServiceLogsFailedStepWithOperation` (no stored message or label text) | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` |
| AC11 | `ScanHoldsWindowOfKernelEventKill`, `ScanMakesNoHoldFromCatalogKillLine`, `ScanCatchesLateKernelEvents`, `ScanHoldCoversWindowOfKillLineSecondsApart` (kill line and `kernel_event` 40 s apart either way: the hold contains the line's window), `SaveMergesOverlappingStoredHolds`, `IncidentAfterRemovalEqualsIncidentBefore` (real store, reconstructor run on an unchanged copy and after removal; kill line and `kernel_event` seconds apart) | `tests/Vandox.Backend.Tests/KillHoldScannerTests.cs` |
| AC13 | `RemoveExpiredRemovesWindowDataOfIncidentWithoutKernelKill` (incident row stays) | `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs` |
| AC13 | `RunLeavesIncidentBeforeLogPeriodUnchanged`, `RunSavesNoNewAgentIncidentBeforeLogPeriodOutsideHolds`, `RunBuildsHeldClusterBeforeLogPeriod`, `RunBuildsImportedClusterBeforeLogPeriod`, `RunBuildsImportedClusterReachingIntoTheLogPeriod` (imported kills before and after start + before + after in one cluster), `RunBuildsHeldClusterWhoseKernelEventIsSecondsApart`, `RunWithoutLogPeriodRebuildsAsBefore` | `tests/Vandox.Backend.Tests/IncidentReconstructorTests.cs` |
| AC13 | `RecentAgentLinesForgetsLinesOlderThanTheSpan` (lines over hours: the count never exceeds the lines of 2 minutes), `RecentAgentLinesIgnoresImportedLines`, `RecentAgentLinesFindsKillOfAHeldLine` | `tests/Vandox.Backend.Tests/RecentAgentLinesTests.cs` |
| AC12 | `ReportSkipsAgentLinesAndSnapshotsPastTheirPeriods`, `ReportKeepsImportedHours` | `tests/Vandox.Backend.Tests/ForensicsReporterTests.cs` |
| AC12 | `AddIncidentDoesNotJudgeOutageInUnobservedHourBeforeHorizon`, `AddIncidentJudgesAsBeforeWithoutHorizon` | `tests/Vandox.Core.Tests/ForensicsAnalysisTests.cs` |

## Approach

**Configuration (Core).** A `retention:` section with seven keys (spec N1), form and E1 reason as built and written in
`configuration-options.md`; `RetentionService.CreatePolicy` maps them to `RetentionPolicy`, the kill window from `incidents.*`.

**Schema step 14 (Storage).** As built and written in `storage.md` (*Schema*, *Migration steps*).

**Fold step (record 0208).** Under the writer lock, one transaction: read at most `StorageLimits.RollupStepRows` (10,000) `metrics`
rows with `record_id > rollup_after_id` in record ID order, stopping after the row that takes the summed UTF-8 length of the labels
read past `StorageLimits.RollupStepLabelBytes` (2 MiB; the first row is always taken), fold each into its 5-minute and hourly bucket (min, max, sum, count; last
by `(captured_at, record_id)`), upsert the touched buckets, set `rollup_after_id`. Sums are clamped to ±`double.MaxValue`: each
addition in `RollupBucketTotals.Add`, and the stored sum in both upserts (`MIN(MAX(sum_value + excluded.sum_value, $low), $high)`,
bounds as parameters), so finite values never fold to NaN and stall the watermark (Security B1, decide round 1; record 0208). A bucket already past its tier's period is not
created. All origins are folded. Worst case held per step: 2 MiB plus one row of labels (about 200 KB) and at most 20,000 bucket
entries.

**Kill scan (record 0210), in the Backend.** `KillHoldScanner` runs before each removal pass. It reads, on a reader connection outside
the writer lock, the `kernel_event` records with ID above `kill_scan_after_id` (a range of the row ID, pages of 1,000; the kind filter written
`+r.kind = 'kernel_event'` so the plan walks the row ID range, Security N1); each of type `oom_kill` gives
`[captured − before − 5 min, captured + after + 5 min]`: the 5-minute hold margin covers the offset between the kill's log line
(capture time of the report's first line, up to 60 s earlier) and its `kernel_event` (receipt minus the delay), Reviewer R1, decide
round 1, record 0210. `SaveKillScanAsync` merges each new hold with the stored holds it overlaps
into one row and sets `kill_scan_after_id` to the highest record ID that existed when the pass began, in one write per page. A log
line that reads as a kill makes no hold (any local process can write one, fact 20).

**Removal step (records 0209, 0210, 0212).** One step removes at most `StorageLimits.RemovalStepRecords` (5,000) records of one
kind, or as many rows of one derived table, and examines at most `StorageLimits.RemovalStepExamined` (20,000) index entries and 1,000
windows, in one transaction, in this order until nothing is left: raw metrics, log lines, snapshots, events, gap rows, 5-minute
rollups, hourly rollups, template hours, incidents, holds. A cursor (kind, capture time, ID) carries a pass from step to step. Records
are walked through `records_removable` in capture order up to `now − period of its kind`, between the holds (spec S9, option B):
the hold with the greatest `started_at` at or below the position is found, and the walk seeks past its end. The exemptions (unscanned, unfolded, highest ID,
newest mark, listener finding), the kinds per period and the delete order are as built and written in `retention.md`; a step that
removed log lines ends with an FTS5 incremental merge of at most 64 pages (`'merge', -64`), so the `delete` entries free index pages
(record 0212). The metric steps prune `agent_sample_times` below the raw horizon except each agent's newest row; the first statement of
`PruneSampleTimes`, before any delete, raises `sample_time_floor` to it (never lowered), so a step that ends after 20,000 rows has
already committed the floor (Security B2, decide round 1); below the floor a sample time only narrows a time gap (record 0213). **Gap horizon** (Security B4): `now −` the longest of
`events`, `raw`, `logs`, `snapshots`, `rollups_5m`, `rollups_1h`, none while one is unlimited. Gap rows through `gaps_to_at`: a
sequence gap only when `closed`, a time gap when its read state (`GapReadSql.State`) is `closed` or `final`, `to_at` before the gap
horizon, and its span overlaps no hold; a sequence gap that is `open` or `final` is never removed. Template hours, incidents and holds as
written in `retention.md`. No `VACUUM`.

**Resend after removal (record 0211).** `AgentTimeline.WasReceived(seq)`, decided against the rows as loaded before the batch's
walk; such records are skipped and counted as duplicates (`storage-writing.md`).

**Template rebuild (DA 4).** With a finite `retention.logs`, hours before the first full hour of the log period are kept, later
ones recounted (`log-templates.md`).

**Service (Backend).** `RetentionService : BackgroundService`, registered only by `ServeCommand`; pass timing, cancellation
between steps and failure handling as built and written in `retention.md`, *The job*.

**Log lines (layout fixed).** Events 69 to 73 as built and written in `retention.md`, *Log lines* (AC10 amended).

**Re-run of incidents (spec S11).** `IncidentReconstructor` gets the log period start (`now − retention.logs`, none when unlimited)
and the holds (`IRetentionStore.HoldsAsync`) from `IncidentsCommand`. A kill cluster whose window starts before that start is built
and saved only when one hold contains its whole window or every kill of it comes from a record of origin `import` (imported data
is kept, Q1 a); otherwise it is skipped: the stored incidents it overlaps stay unchanged and no
new incident is saved. Skipped clusters count in `ReconstructionResult` as unchanged. Origin per kill, never by a time bound
(Reviewer R2, Security B3, decide round 1): `RecentAgentLines` holds the IDs of the non-imported lines captured at most 2 minutes
(twice the detector's 60-second merge window) before the line last read; per line the run adds it, passes it to the detector, drains
the closed kills — a kill is an agent kill when one of its `RecordIds` is held — and then forgets the lines older than 2 minutes before
it. Memory follows the lines of 2 minutes, not all lines.

**Report (spec S10).** `ForensicsReporter` takes a `ReportRetention` (logs and snapshots periods) and skips non-imported lines and
process snapshots captured before `now −` those periods. `ForensicsAnalysis.JudgedFrom` (the
logs horizon): an outage whose first kill is before it and whose kill hour is not observed is counted as an outage but in no rule.
No new text. `ReportService` and `ReportCommand` pass the configured periods.

**Benchmark.** `--retention [--size N]` of the storage benchmark (`docs/BENCHMARKS.md`, *Retention steps*).

## Options considered

- Rollups, retention by kind, kept windows, resends, vacuum: records 0208 to 0212.
- Partial index `where origin = 'agent'` (Security): `backend` is a valid origin under the same periods, so `origin <> 'import'`.
- Refusing `events` shorter than a data period: rejected, the gap horizon makes every valid setting safe (Security B4).
- A `vandoxd retention` sub-command: the container has no scheduler.
- Steps bounded by time: untestable without a real clock.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `Configuration/RetentionConfig.cs`, `RetentionPeriod.cs` (new); `BackendConfig.cs`, `BackendConfigLoader.cs` | section, parsing, validation |
| Vandox.Core | `Reports/ForensicsAnalysis.cs` | `JudgedFrom` |
| Vandox.Storage | `IRetentionStore.cs`, `IRollupReader.cs`, `RetentionPolicy.cs`, `RollupTier.cs`, `RollupBucket.cs`, `RollupQuery.cs`, `RollupStepResult.cs`, `RemovalStepResult.cs`, `KillScanQuery.cs`, `KillScanProgress.cs`, `RetentionHold.cs`, `RemovalCursor.cs`, `RollupFolder.cs`, `RetentionRemover.cs`, `RetentionQueries.cs` (new) | the store side |
| Vandox.Storage | `SqliteStore.cs`, `SchemaMigrator.cs`, `StorageLimits.cs`, `AgentTimeline.cs`, `TemplateRebuilder.cs`, `GapReadSql.cs` (read) | interfaces, step 14, constants, resend skip, rebuild |
| Vandox.Backend | `Retention/RetentionService.cs`, `Retention/KillHoldScanner.cs`, `Reports/ReportRetention.cs` (new); `Hosting/ServeCommand.cs`, `Hosting/BackendLog.cs`, `Reports/ForensicsReporter.cs`, `Reports/ReportService.cs`, `Cli/ReportCommand.cs`, `Cli/TemplatesCommand.cs`, `Cli/IncidentsCommand.cs`, `Incidents/IncidentReconstructor.cs` | job, wiring, logs, report |
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
  `Task<RemovalStepResult> RemoveExpiredAsync(RetentionPolicy policy, DateTimeOffset now, RemovalCursor? cursor, CancellationToken cancellationToken)`,
  `Task<IReadOnlyList<RetentionHold>> HoldsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)` (reader connection)
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
  `RemovalStepResult.Holds: int`, `RemovalStepResult.More: bool`, `RemovalStepResult.Next: RemovalCursor?`
- `class KillScanQuery`: `KillScanQuery.AboveId: long`, `KillScanQuery.Limit: int` (kind kernel_event, read on a reader connection)
- `class KillScanProgress`: `KillScanProgress.ScannedId: long`, `KillScanProgress.HighestId: long`
- `class RemovalCursor`: `RemovalCursor.Kind: string`, `RemovalCursor.CapturedAt: DateTimeOffset`, `RemovalCursor.Id: long`
- `class RetentionHold`: `RetentionHold.Start: DateTimeOffset`, `RetentionHold.End: DateTimeOffset`
- `StorageLimits.RollupStepRows: internal const int = 10000`, `StorageLimits.RemovalStepRecords: internal const int = 5000`, `StorageLimits.RemovalStepExamined: internal const int = 20000`,
  `StorageLimits.RollupStepLabelBytes: internal const int = 2097152`,
  `StorageLimits.SchemaVersion: const int = 14` (changed)
- `internal bool WasReceived(long seq)` on AgentTimeline
- `public Task<TemplateRebuildResult> RebuildTemplatesAsync(int batchLines, DateTimeOffset? keepBefore, Func<long, CancellationToken, Task>? afterBatch, CancellationToken cancellationToken)` on SqliteStore (new overload; the old one passes null)
- `SqliteStore.RetentionStepStarted: internal Func<Task>?` ({ get; set; }, test hook of AC6, called with the lock held)

Backend (Vandox.Backend.Retention, Vandox.Backend.Reports, Vandox.Backend.Hosting):

- `class RetentionService` (a BackgroundService): `public RetentionService(IRetentionStore store, RetentionPolicy policy, TimeProvider clock, ILogger logger)`,
  `internal Task RollUpAsync(CancellationToken cancellationToken)`, `internal Task RemoveAsync(CancellationToken cancellationToken)`,
  `internal static RetentionPolicy CreatePolicy(BackendConfig config)`
- `class RecentAgentLines` (internal, Vandox.Backend.Incidents): `internal void Add(StoredRecord record)` (keeps the ID only of a non-imported line),
  `internal void ForgetBefore(DateTimeOffset capturedAt)`, `internal bool ContainsAny(IReadOnlyList<long> recordIds)`, `RecentAgentLines.Count: int` (internal, get-only)
- `IncidentReconstructor.LogPeriodStart: DateTimeOffset?` and `IncidentReconstructor.Holds: IRetentionStore?` (init; both null keep today's behavior)
- `class KillHoldScanner` (internal): `internal KillHoldScanner(IRetentionStore store)`,
  `internal Task<int> ScanAsync(RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)` (the holds added)
- `class ReportRetention`: `ReportRetention.Logs: TimeSpan?`, `ReportRetention.Snapshots: TimeSpan?`
- `public ForensicsReporter(IRecordReader records, IIncidentStore incidents, IListenerHistoryReader listeners, SignatureCatalog catalog, TimeSpan lookback, ReportRetention retention, TimeProvider clock)`
  (new overload; the old constructor stays and passes an empty `ReportRetention`)
- `internal static partial void RetentionRolledUp(this ILogger logger, int values, int buckets)`,
  `internal static partial void RetentionRecordsRemoved(this ILogger logger, int metricValues, int logLines, int snapshots, int events, int gaps)`,
  `internal static partial void RetentionDerivedRemoved(this ILogger logger, int fiveMinuteValues, int hourlyValues, int templateHours, int incidents, int holds)`,
  `internal static partial void RetentionStepBusy(this ILogger logger, string operation)`,
  `internal static partial void RetentionStepFailed(this ILogger logger, string operation, string error)` in BackendLog

Existing files the skeleton rewrites (read, not final): BackendConfig.cs, SqliteStore.cs (interface list), StorageLimits.cs,
ForensicsReporter.cs, ForensicsAnalysis.cs, BackendLog.cs, AgentTimeline.cs.

## Test files

`tests/Vandox.Core.Tests/RetentionPeriodTests.cs`, `RetentionConfigTests.cs`, `BackendConfigLoaderTests.cs`, `ForensicsAnalysisTests.cs`;
`tests/Vandox.Storage.Tests/SqliteStoreRollupTests.cs`, `SqliteStoreRetentionTests.cs`, `SqliteStoreTemplateRebuildTests.cs`;
`tests/Vandox.Backend.Tests/RetentionServiceTests.cs`, `KillHoldScannerTests.cs`, `RecentAgentLinesTests.cs`, `ForensicsReporterTests.cs`, `ServeTests.cs`,
`CommandLineTests.cs`. A retention store double for `RetentionServiceTests` (`ScriptedRetentionStore`, Backend tests) is the Tester's,
with its *Test doubles* row reported for `.squad/project.md`.

Existing call sites of changed signatures: `BackendConfig.Keys()` gains the seven `retention.*` keys (Dev); the Tester adapts
`BackendConfigLoaderTests.cs:46` (the loop over all keys), `:184-193` and `:789-796`. `ForensicsReporterTests.cs:502` and
`ReportPageTests.cs:74` keep the old constructor. The Tester adapts `SqliteStoreOpenTests` asserting
version 13. `TemplateRebuilderTests`/`SqliteStoreTemplateRebuildTests` cases that expect a full clear keep passing (every hour of their data
holds lines); the Tester confirms.

Code no unit test can cover: `tools/Vandox.StorageBenchmark/RetentionBenchmark.cs` and its option in `Program.cs`, a measurement
program in no coverage report (fact 16); no exclusion.

## Areas

- New area *Retention and rollups* (`docs/areas/retention.md`, Lead decision): scope "which data each period covers and what outlives
  it, the rollups and their folding, kill holds, passes and steps, failure and shutdown, log lines"; not here: the schema (Storage), the
  options' form (Configuration options), the report's rules (Forensics report).
- Storage, Storage writing, Storage reading, Configuration options, Log templates, Incidents, Forensics report, Ingest endpoint,
  Listener changes, Log import, Backend host: see *Documentation updates*.

## Documentation updates

All by the Dev, after the code is green:

- `docs/areas/retention.md` (new) from `_template.md`: the content of *Approach* as rules (periods per kind, imports, events, windows
  and holds, folding, step bounds, pass timing, busy and failure, shutdown, log lines, E8, the sample-time floor (raised before the step's first delete), the FTS5 merge of each log step, the
  5-minute hold margin in *Windows of OOM kills*, the clamped sums), links records 0208-0213; `docs/areas/README.md`:
  its index row.
- `docs/areas/storage.md`: *Retention (planned)* replaced by a short paragraph linking `retention.md`; schema version 14; the tables
  `metric_rollups_5m`, `metric_rollups_1h`, `retention_holds`, the indexes `records_removable` and `gaps_to_at` and the `meta` keys; migration step **14**; *Implementation* names
  `RollupFolder`, `RetentionRemover`; the `agent_sample_times` row and line 136: sample times follow `retention.raw`, the newest per
  agent kept, `meta` key `sample_time_floor` (record 0213); related decisions 0208-0213.
- `docs/areas/storage-writing.md`: line 44 (deletes keep `log_fts`, now done by retention); deduplication: the resend rule of record
  0211, with the residual of record 0211 verbatim; line 179 (notes: kept, record 0209); the template rebuild keeps hours before the log
  period; *Time gaps* (line 112): a sample time below `sample_time_floor` adds no row and opens no time gap, it only narrows one (record 0213).
- `docs/areas/storage-reading.md`: the rollup reader `IRollupReader`.
- `docs/areas/configuration-options.md`: the seven `retention.*` rows and a paragraph with the form and the E1 error text; in it
  (lines 266-267, Reviewer R4) the text from `retention.events` to the end of the sentence reads exactly: "`retention.events` kernel
  events, service states, backup runs and listener findings (gap records and rows stay until the longest of the periods other than
  `incidents` has passed, for good while one of them is `unlimited`), and the rollup keys the 5-minute and hourly buckets (the hourly
  period also the template statistics)."
- `docs/areas/log-templates.md` lines 66-67 and 123-124: the statistics follow `retention.rollups_1h`, kept while an hour holds a line;
  rebuild behavior.
- `docs/areas/incidents.md` lines 113 and 119-120 and *Re-running*: only windows of agent `kernel_event` kills are kept (record 0210);
  the window data of other incidents follows the periods; the re-run rule of S11; forged kill lines no longer keep data; line 104
  (memory): with a log period the run also holds the IDs of the agent lines of the last 2 minutes.
- `docs/areas/forensics-report.md`: *Observed hours* and *What it reads*: agent lines and snapshots past their periods are skipped; an
  outage before the logs horizon in an unobserved hour is in no rule.
- `docs/areas/ingest-endpoint.md` line 149: retention bounds the database by age but frees no space on the volume (record 0212).
- `docs/areas/listener-changes.md`: findings and the newest mark per agent kept for the events period.
- `docs/areas/log-import.md` line 166: imported records are never removed by retention.
- `docs/areas/backend-host.md`: the service runs the retention job; its five log lines (events 69 to 73).
- `docs/ARCHITECTURE.md`: line 15 drop "retention" from the planned list; *Storage and retention*: the table (no longer planned, add
  events 3 years, imported data kept, windows of agent `kernel_event` OOM kills kept), the migrations sentence extended with versions 11 to 14 from
  `storage.md`, *Interfaces* adds `IRetentionStore` and `IRollupReader`, records 0208-0212; *Components* (`src/Vandox.Backend`): the
  retention job.
- `README.md` *Backend options*: the seven keys; the `retention.events` row (line 114, Reviewer R4) reads exactly: "| `retention.events` |
  `1095d` | How long kernel events, service states, backup runs and listener findings are kept; gaps stay until the longest of the
  periods other than `incidents` has passed, for good while one of them is `unlimited`. |"; `deploy/backend/vandoxd.yaml`: a commented `retention:` section with the defaults.
- `docs/BENCHMARKS.md`: *Retention steps* (AC6).
- `.squad/project.md` (orchestrator writes the reported text): security area 9 adds "retention deletes only through bounded steps of
  `RetentionRemover`"; *Test doubles* row for `ScriptedRetentionStore` (Tester's text). `CHANGELOG.md`: none, written at release
  (`docs/CONTRIBUTING.md:128`).

## Architecture check

- *No data gaps unless explicitly recorded*: gap records and closed or final gap rows stay until the gap horizon (the longest finite period), never inside
  a hold; open and final sequence gaps forever; and
  no removed number is reported missing (stream row kept, record 0211); a summarized span keeps its gaps.
- *Backfilled data never raises an alert by itself*: `records.live` is untouched; rollups alert nothing.
- Ingest stays bounded: a step is no larger than one batch (AC6); 0212 amends 0177.

## Security considerations

- [x] Limits: a period is checked on the YAML string after decoding (four ASCII digits and `d`, or `unlimited`); no other parsed input.
- [x] Exceptions reaching a user-visible text: a configuration error shows only the E1 reason with the key; a store failure reaches only
  the log, as `StoreException.Message` (operation text and SQLite's error, never stored data). Holds and the scan bound what a
  compromised agent can pin to its span (record 0210 residual); every statement binds values as parameters.

## Decision records

- `docs/decisions/0208-metric-rollups-are-folded-from-new-rows-by-record-id-never-recomputed-from-raw.md` (Proposed)
- `docs/decisions/0209-retention-removes-by-capture-time-per-kind-imports-kept-events-kept-three-years.md` (Proposed)
- `docs/decisions/0210-data-in-the-windows-of-the-agents-kernel-oom-kills-is-kept-as-long-as-their-incident.md` (Proposed)
- `docs/decisions/0211-a-resent-record-whose-stored-copy-was-removed-is-not-stored-again.md` (Proposed)
- `docs/decisions/0212-retention-frees-space-inside-the-database-file-not-on-the-volume.md` (Proposed, amends released 0177)
- `docs/decisions/0213-agent-sample-times-follow-the-raw-period-below-a-floor-that-opens-no-time-gap.md` (Proposed, step 6)

## Out of scope / follow-ups

Spec *Out of scope*. Follow-up issue: "[Backend] Rollups of process and connection snapshots per program" (#50 needs memory per
program beyond 30 days).

## Challenge

- DA 1 (major, removal not bounded): accepted. Partial index `records_removable` (`origin <> 'import'`, so imported rows are never
  walked), a walk between windows that seeks past each window's end, holds merged with stored overlapping holds on save (so one lookup
  of the greatest `started_at` decides), a cursor per pass, at most 20,000 entries and 1,000 windows examined per step. Tests
  `RemoveExpiredReachesRowsBehindKeptRows` and `RemoveExpiredQueriesUseIndexSearches`; AC6's test and the benchmark get imported lines
  and held windows below the horizon.
- DA 2 (minor, time gaps stored open): accepted. Time gaps are judged by their read state (`GapReadSql.State`); tested with a stored-open,
  read-final time gap; an open or final sequence gap is never removed (also Security B4).
- DA 3 (minor): accepted; call sites under *Test files*.
- DA 4 (minor, rebuild of partly kept hours): accepted. The rebuild keeps every hour before the first full hour of the log period and
  recounts only later ones; spec S7 and AC8 amended; `RebuildKeepsStatisticsOfPartlyRemovedHours`.
- DA 5 (minor): accepted; `docs/areas/retention.md`.

## Security review

- B1 (fold step unbounded in bytes): accepted. `RollupStepLabelBytes` (2 MiB of labels, at least one row) bounds a step; worst case
  stated in *Approach* and in `retention.md`; `FoldRollupsStopsAtTheLabelByteBudget`.
- B2 (removal work unbounded, no gap index): accepted, as DA 1, plus `gaps_to_at`.
- B3 (forged kills, wrong actor): partly decided, partly escalated. Holds now come only from agent `kernel_event` OOM kills, which only
  the kernel writes (fact 20) or a token holder forges (bounded by `ingest.min_free`); a log line that reads as a kill makes no hold
  (spec S9, AC11 amended; `ScanMakesNoHoldFromCatalogKillLine`). Record 0210 now names the actor. Stored incidents: escalated with the
  corrected residual; the Product Manager chose option B (2026-11-13): a stored incident's window keeps data only through the holds of
  its `kernel_event` kills, and a re-run leaves an incident before the log period unchanged unless wholly held (spec S11, AC13;
  *Approach*, *Re-run of incidents*).
- B4 (`events` shorter than a data period): accepted. Gap horizon = the longest of all finite periods; never inside a window or hold;
  open and final sequence gaps never removed, so `WasReceived` cannot swallow a late fill. Spec N8, AC9 amended; tests named in AC9.
- N1: accepted; decided against the rows as loaded, before the batch's walk (*Approach*, record 0211);
  `WriteBatchMixedResendFillAndNewStoresOnlyFillAndNew`.
- N2: accepted as a residual in record 0211 and `storage-writing.md` (wording of the Security delta review): a number below `floor_seq` whose copy was removed and is resent is
  stored and folded again; it needs a lost acknowledgement kept past the period or a token holder.
- N3, N4, N5: accepted (*Approach*; the test cases of AC1 and AC10).
