# Plan: Time-series query API

Source: Issue #47 | [spec.md](spec.md)
Tier: standard — new read behavior (a query service, new store reads, a chart state) that creates, changes or removes no security control.
Security areas touched: 10 Parsing of external input (new store reads decode stored agent payloads with `json_extract` and the payload rules, forged values included); 12 Logging and display of external data (marker texts handed to the views; the chart data path in `wwwroot/js/charts.js`)
Plan size: one feature with eleven criteria spanning ten store reads, the query engine with three marker kinds and two gap sources, the read gate and a chart contract change.

## Problem / root cause

See [spec.md](spec.md), *Problem*.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Rollup tables are keyed `(source, name, labels, bucket)`, labels `''` for none; raw `metrics.labels` is NULL or JSON, `{}` folded to `''` | `SchemaMigrator.cs` step 14; `RollupFolder.cs:17,51` | confirmed |
| 2 | The fold watermark is the meta key `rollup_after_id` (highest folded metric record ID) | `RetentionQueries.cs:14` | confirmed |
| 3 | Gap rows: table `gaps` with `kind`, `cause`, `state` (`open`/`closed`/`final`), `from_at`, `to_at`, index `gaps_to_at(to_at)`; time-gap state is derived on read | `SchemaMigrator.cs` steps 12, 14; storage-reading.md *Agent timeline* | confirmed |
| 4 | `IAgentTimelineReader.GapsAsync` pages one agent in ID order, no time range | `IAgentTimelineReader.cs`, `GapQuery.cs` | confirmed |
| 5 | `records.data` is a TEXT column; payload members carry the wire names (`unit`, `active_state`, `active_enter_at`, `restarts`, `type`, `boot`); JSON1 is used already | `SchemaMigrator.cs:20-32`; `ServiceState.cs`, `KernelEvent.cs`; `LogFinderQueries.cs:46` | confirmed |
| 5a | The stored document holds those members at its top level, so `json_extract(data, '$.unit')` reads them | `BatchWriter.cs:111`, `PayloadJson.cs:62` (Devil's Advocate) | confirmed |
| 5b | `active_enter_at` is left out of the stored JSON when absent (`json_extract` gives NULL); `restarts` is a `uint`; payload times are checked only against the storable range | `ServiceState.cs:41-49,99`, `BackupRun.cs:151,165` | confirmed |
| 5c | `records.boot_id` holds the boot ID of the envelope, so two `service_state` rows of different boots differ there | wire-format.md (envelope `boot_id`); `SchemaMigrator.cs:29` | confirmed |
| 6 | Metric sources: collectors `system`, `filesystem`, `clock` write source `system`; `self` writes `agent`; `mariadb` writes `mariadb`; scheduler gap records carry source `agent` and `collector` = job name | `sysstats.go:19-25`, `selfstats.go:152`, `scheduler/runner.go:400`, wire-format-records.md *Metric names* | confirmed |
| 7 | Gap records without numbers come from the scheduler, `kernel`, `systemd`, `logs` and the spool's refused-write episode; the spool's dropped and the sender's given-up gaps name numbers | `runner.go:400`, `kernevents/records.go:58`, `svcstats.go:340`, `logship/records.go:58`, `spool/put.go:345-357`, `sender.go:730` | confirmed |
| 8 | uPlot 1.6.32 breaks a line only at `null` and draws through `undefined` | `uPlot.esm.js` `findGaps` (1958) and the linear builder (2235-2241) | confirmed |
| 9 | `wwwroot/js/**` is already excluded from coverage (record 0098) | `.squad/stack.md`, *Coverage exclusions* | confirmed |
| 10 | The reference workload answers in under one second on the DS918+ | to verify: the `--query` scenario of `tools/Vandox.StorageBenchmark`, Product Manager, step 7 (AC10) | unverified |
| 11 | SQLite streams `ORDER BY captured_at, labels` over `metrics_source_name_time` with a temp B-tree only for the right part | to verify: `EXPLAIN QUERY PLAN` asserted by a store test, Tester, step 5 | unverified |

## Tests per criterion

Store reads: `SqliteStoreTimeSeriesTests`; engine: `TimeSeriesServiceTests` (real `SqliteStore` in `TempDirectory`, a `FakeTimeProvider`), collector mapping: `SeriesGapTests`.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `QueryAsyncShortRangeWithinTargetIsRaw`, `QueryAsyncRawOverTargetTimesChoosesFiveMinuteStep`, `QueryAsyncYearChoosesTwelveHourStepFromHourlyRollups`, `QueryAsyncStepChoiceKeepsOverlappingStepsWithinTarget` (table: 1 h/24 h/7 d/30 d/1 y/3 y at targets 1, 100, 1000, 2000), `QueryAsyncRangeBeforeRawPeriodIsNeverRaw`, `QueryAsyncRangeBeforeFiveMinutePeriodUsesHourSteps`, `QueryAsyncRangeBeforeHourlyPeriodFlagsEmptyLines`, `QueryAsyncLabelledSeriesHasOneLinePerLabelSet`, `QueryAsyncSeriesWithoutValuesHasNoLine`, `QueryAsyncSeriesOfOneCollectorSharingTimesStayRaw` (two series with identical times: union fits the target, the sum does not) | `tests/Vandox.Storage.Tests/TimeSeriesServiceTests.cs` |
| AC1 | `RawValuesAsyncStopsAfterOneTimeOverMaxTimes`, `RawValuesAsyncQueryPlanStreamsByCaptureTime` | `tests/Vandox.Storage.Tests/SqliteStoreTimeSeriesTests.cs` |
| AC2 | `QueryAsyncStepTimesAreEpochMultiplesFromStepHoldingFrom`, `QueryAsyncStepPointMergesBucketsCountWeighted`, `QueryAsyncStepPointCoversWholeStepBeyondRange`, `QueryAsyncRawAxisIsUnionOfLineTimes`, `QueryAsyncRawDuplicateTimeMergesIntoOnePoint` | `TimeSeriesServiceTests.cs` |
| AC2 | `RollupStepsAsyncMergesMinMaxCountAndLatestLast`, `RollupStepsAsyncClampsSumToFiniteRange` | `SqliteStoreTimeSeriesTests.cs` |
| AC3 | `QueryAsyncEmptyStepIsGap`, `QueryAsyncRawTimeOfOtherLineIsBetweenSamples`, `QueryAsyncRawConcerningGapBreaksLineAndAddsTime`, `QueryAsyncRawGapOfOtherCollectorDoesNotBreakLine`, `QueryAsyncRawEmptyAlignedFiveMinutesBreaksLine`, `QueryAsyncRawFourMinuteSpacingStaysConnected`, `QueryAsyncRawGapAcrossSixtyFourLinesAddsOneTime` (64 lines broken by one gap in one interval: one added time, axis at most 2 × target − 1) | `TimeSeriesServiceTests.cs` |
| AC4 | `QueryAsyncListsOpenAndFinalGapsWithoutClosed`, `QueryAsyncAgentGapRecordIsFinal`, `QueryAsyncGapRecordInsideSequenceGapOfSameCauseListedOnce`, `QueryAsyncGapRecordFoundOnlyInsideCaptureWindow` | `TimeSeriesServiceTests.cs` |
| AC4 | `ConcernsMapsCollectorToLines` (table: `self`→`agent` lines only; `filesystem`→`filesystem_*` not `mem_*`; `clock`; `system`→other `system` names; `mariadb`; `logs`/`kernel`→none; no collector and `future`→every line) | `tests/Vandox.Storage.Tests/SeriesGapTests.cs` |
| AC4 | `GapsInRangeAsyncReturnsOverlappingOpenAndFinalOfAllAgents`, `GapsInRangeAsyncReportsDerivedTimeGapState`, `GapRecordsAsyncProjectsSpanCauseCollectorAndNumbers` | `SqliteStoreTimeSeriesTests.cs` |
| AC5 | `QueryAsyncOomKillsOncePerIdentity`, `QueryAsyncBootsOncePerBootId`, `QueryAsyncServiceRestartRules` (table: restarts rose; enter moved from `active` and from `failed`; no enter time before, one now, from `failed`; from `inactive`; boot between by boot ID while the boot record is captured after the unit's first record; boot between by boot record; hourly re-record; restarts fell; previous record before `from`; previous record beyond the 10,000 rows examined), `QueryAsyncRestartTime` (table: new enter time in range; enter time after the query time and before `from` → capture time), `QueryAsyncBackupRunSpans` (table: ended in range; started a day before; still running; unknown end after a later boot; start written twice; `gone_by` and `last_seen_at` after the query time → clamped; `started_at` after the query time → dropped), `QueryAsyncMarkersInTimeOrder` | `TimeSeriesServiceTests.cs` |
| AC5 | `ServiceChangesAsyncReturnsFirstPerUnitAndChangesWithPrevious` (a missing enter time against a present one is a change), `PreviousServiceStatesAsyncScansAtMostTenThousandRows` (300 new units over a long history: one call, cap respected, unfound units absent), `BootTimesAsyncReturnsOnlyBoots`, `KernelEventsAsyncProjectsFieldsWithoutMessage`, `BackupRunsAsyncProjectsFields`, `EventReadsRefuseLimitOverOneThousandOne` | `SqliteStoreTimeSeriesTests.cs` |
| AC6 | `QueryAsyncReturnsNothingAfterQueryTime`, `QueryAsyncCompleteUntilIsEarliestUnfoldedCapture` (stalled fold: values stored, no fold step), `QueryAsyncCompleteUntilIsQueryTimeWhenAllFolded`, `QueryAsyncRawHasNoCompleteUntil` | `TimeSeriesServiceTests.cs` |
| AC6 | `EarliestUnfoldedAsyncReadsAboveWatermark` | `SqliteStoreTimeSeriesTests.cs` |
| AC7 | `QueryAsyncOverSixtyFourLinesCutsInSeriesThenUtf8ByteOrder`, `QueryAsyncOverThousandMarkersKeepsEarliestWithCompleteUntil`, `QueryAsyncOverThousandGapsKeepsEarliestWithCompleteUntil`, `QueryAsyncFullRecordPageBoundsCompleteUntil`, `QueryAsyncEventReadsUseAtMostTenPagesOfOneThousandOne` (a `CountingTimeSeriesReader` records every limit and call) | `TimeSeriesServiceTests.cs` |
| AC8 | `QueryAsyncInvalidQueryRefusedBeforeStoreRead` (table of the six exact texts, a `CountingTimeSeriesReader` that must see zero calls), `QueryAsyncStorableEdgesReadWithoutError` (from = `StorableTime.Min`, to and query time = `StorableTime.Max`: every derived bound clamped, no window error) | `TimeSeriesServiceTests.cs` |
| AC9 | `QueryAsyncCancelledThrowsWithoutResult` | `TimeSeriesServiceTests.cs` |
| AC9 | `EveryCallHoldsTheGateOnlyWhileItRuns`, `CancelledWaitForSlotThrowsAndHoldsNothing` | `tests/Vandox.Backend.Tests/GatedTimeSeriesReaderTests.cs` |
| AC9 | `ServeRegistersTimeSeriesServiceOverTheGate` | `tests/Vandox.Backend.Tests/ServeTests.cs` |
| AC10 | manual: Product Manager, DS918+ SSD volume, `tools/Vandox.StorageBenchmark --query` published from the stamped branch head by the *Procedure* of `docs/BENCHMARKS.md` (as for #46); fallback if the Product Manager cannot publish: the orchestrator publishes it in the session and attaches the zipped folder to the early draft PR. Development-host run: Dev, step 6, recorded as order of magnitude | — |
| AC11 | `ConstructorRefusesBridgedPointWithValue`, `ConstructorRefusesBridgeCountMismatch`, `ConstructorRefusesBridgedStackedSeries` | `tests/Vandox.Backend.Tests/ChartDataTests.cs` |
| AC11 | `RenderAsyncPassesBridgedIndexesOnCreateAndUpdate` | `tests/Vandox.Backend.Tests/ChartInteropTests.cs` |
| AC11 | manual: Reviewer, step 8, reads `charts.js` (bridged indexes set to `undefined`, nothing else); CI: *Image build check* (Dev, step 6) for the changed static asset | — |

## Approach

**Placement.** The engine `TimeSeriesService` lives in `Vandox.Storage` (flat namespace, like the other readers) and depends only on
`ITimeSeriesReader`, `RetentionPolicy` and `TimeProvider`, so the benchmark tool runs it without ASP.NET. `vandoxd`
registers one singleton over `GatedTimeSeriesReader` (on the shared `WebReaderGate`), with
`RetentionService.CreatePolicy(config)`; Blazor components inject it. No route, page or option.

**Validation (AC8)**, before any read, `InvalidQueryException` with exactly: `store: invalid query: at least one series is required`,
`store: invalid query: at most 16 series`, `store: invalid query: source and name are required`, `store: invalid query: target must be
1 to 2000`, `store: invalid query: from must be before to`, `store: invalid query: from and to must be within the storable range`. A
series listed twice is used once.

**Range.** `now` from the clock; `end = min(to, now)`; `from >= now` gives an empty result. Nothing after `now` is returned (AC6).
Every bound the engine derives (`from − 1 h`, `from − 1 d`, `end + 1 d`, `end + 7 d`, aligned step bounds) is clamped to
[`StorableTime.Min`, `StorableTime.Max`], so no read refuses a window after validation.

**Resolution (AC1).** Raw is tried when `policy.Raw` is null or `from >= now − Raw`: `RawValuesAsync` per series in order, each with
`MaxTimes` = target + 1; the engine builds the union of times and abandons raw once a series reports `TimesExceeded` or the union
exceeds the target, so series of one collector that share their times count once. Otherwise the ladder 5, 10, 15,
30 min (only while `from >= now − FiveMinuteRollups` or that period is null), 1, 2, 3, 6, 12 h, 1 d, then k days: the smallest step `s`
with `floor((end − 1 tick)/s) − floor(from/s) + 1 <= target`, the k-day step computed directly. `StartsBeforeRollups` is set when the
hourly period is not null and `from < now − HourlyRollups`. Steps under one hour read the 5-minute tier, the rest the hourly tier.

**Step points (AC2).** `RollupStepsAsync` reads `[floor(from/s)·s, (floor((end−1)/s)+1)·s)`: the label sets of the series by a loose
index scan over the primary key (`min(labels) > previous`), at most 1,024 sets examined, then one primary-key range seek per set,
merging buckets per step in one pass: min, max, sum clamped to ±`double.MaxValue` as the fold does, count, last by (`last_at`,
`last_record_id`). A set without a bucket is no line; lines stop at `MaxLines` + 1 (`LinesCut`). Label sets are ordered by SQLite's
BINARY collation (UTF-8 bytes) and never re-sorted in C#. The axis is every step start of the
range; a line's missing step is `PointState.Gap`. Average = sum / count.

**Raw points (AC2, AC3).** `RawValuesAsync` returns rows ordered by (`captured_at`, labels, record ID), labels normalized as the fold
does (NULL and `{}` → `''`). Rows of one line at one time merge like buckets. Axis = union of times. Per line: own time → value;
another line's time → `BetweenSamples`; then for each pair of consecutive own values `t1 < t2`, when a gap that `Concerns` the line
overlaps `(t1, t2)` or a whole UTC 5-minute span lies inside `(t1, t2)`, every axis time in `(t1, t2)` becomes `Gap`, and only if the
current axis (with the times added so far) has none there, the time `max(t1 + 1 tick, start of that gap or span)` is added — at most
one per interval between two neighbouring times of the original axis, so the axis holds at most 2 × target − 1 times (the other lines get `BetweenSamples` or, inside a
gap of theirs, `Gap`). Axis times inside a concerning gap are `Gap` also before the first and after the last value.

**Gaps (AC4).** Rows: `GapsInRangeAsync(From = from, To = end, Limit = 1,001)` — `to_at > from AND from_at < to AND` derived state not
`closed`, all agents, ordered by `from_at, id`, through the derived-state SQL of `GapReadSql`. Records: `GapRecordsAsync([from − 1 h,
end + 7 d))` (record 0216), a projection (agent ID, capture time, `from`, `to`, cause, collector, numbers; no other member), span
filtered to the range and its end clipped to `now`, reported `final`, skipped when it names numbers inside a returned sequence row of
its agent and cause; a span that does not parse is skipped. Sorted by start, then rows before records, then ID; cut at 1,000. `GapsCompleteUntil`:
the start of the 1,001st, a full row page's 1,001st start, the last capture read − 7 days when the ten pages ran out — the earliest; null when
complete. `SeriesGap.Concerns(source, name)`: the mapping of spec AC4 in one internal table.

**Event reads.** Gap records, kernel events and backup runs are read by projections (`json_extract` of the named members only, never
the payload or `message`), in pages of at most 1,001 rows keyed on (capture time, ID) with `AfterId`, at most ten pages per kind and
query; a limit over 1,001 is refused (`store: invalid query: limit must be 1 to 1001`). Only the fields below are kept.

**Markers (AC5).** Kernel events: `KernelEventsAsync([from, end))` (type, boot ID, kernel time, PID, victim command); `oom_kill` once
per (boot ID, kernel time, PID) when all three exist, else per record; boot once per boot ID. Restarts: `ServiceChangesAsync([from,
end), 2,001)` — SQL over `records_kind_time` with `json_extract` and `LAG … OVER (PARTITION BY unit ORDER BY captured_at, id)`,
returning each unit's first row in range and every row whose `restarts` differs or whose `active_enter_at` `IS NOT` its predecessor's
(so absent against present is a change), with the predecessor and both rows' `records.boot_id`; for the first rows one
`PreviousServiceStatesAsync(units, from)`: a single backward scan of at most 10,000 `service_state` rows on the kind index, filtered to
the units (`json_each`), latest per unit; a unit not found has no previous record (no restart). The rule (record 0215) on parsed
values: `restarts` as `long`; `active_enter_at` parsed as an instant (unparseable: no enter move; absent before and present now: moved);
a boot lies between when the two rows' boot IDs differ or `BootTimesAsync([predecessor capture, row capture), 1)` finds one. The
restart is timed at the new `active_enter_at` when it lies in `[from, end)` and not after its record's capture time, else at the
capture time. Backups: `BackupRunsAsync([from − 1 d, end + 1 d))` (phase, boot ID, `started_at`, `last_seen_at`, `gone_by`, command),
paired by (boot ID, `started_at`), duplicates dropped; a run with `started_at >= end` is dropped; it is kept when `gone_by > from` or it
has no end; `EndsAt` and `LastSeenAt` are clamped to `now`; without an end, `BootTimesAsync((start, now], 1)` decides `Running` or
`Unknown`. All markers sorted by `At`, kind, record ID, cut at 1,000; `MarkersCompleteUntil` as for gaps (backups − 1 d, restarts the
2,001st row's capture).

**Completeness (AC6).** At a step, `EarliestUnfoldedAsync`: `SELECT min(captured_at) FROM metrics WHERE record_id > watermark` (primary
key range; empty or no watermark row → null); `CompleteUntil = min(now, that)` or `now`. Raw: null.

**Chart (AC11).** `ChartSeries` gains `Bridged` (`IReadOnlyList<bool>`, empty by default). `ChartData` refuses: a bridge list whose
length is neither 0 nor the time count — `The series '<label>' has <n> bridge flags for <m> points in time.`; a bridged point with a
value — `The series '<label>' has a value at bridged point <i>.`; a bridged stacked series — `The stacked series '<label>' cannot
bridge points.` `ChartData.ToBridges()` gives per series the bridged indexes; `ChartInterop` passes them as a fifth argument of
`create`/`createStacked` and a second of `update`; `charts.js` sets those cells to `undefined` before handing columns to uPlot. Nulls
stay gaps. Layout of the chart is unchanged.

**Bounds (N2).** Lines ≤ 64; times ≤ 2 × target − 1; raw rows per series ≤ (target + 1) distinct times; markers and gaps ≤ 1,000
kept; event reads ≤ ten pages of 1,001 projected rows per kind (each row at most about 2.5 KiB: a 1 KiB command and a unit of 256
bytes); restart rows ≤ 2,001 plus one backward scan of ≤ 10,000 rows. The caller's token interrupts every store call (`ReaderLease`).

## Options considered

- Engine in `Vandox.Backend` — lost: the benchmark would need the ASP.NET runtime on the reference host, and nothing of it is web.
- A resolution per line, steps only, raw by collector interval — lost (record 0214).
- A table of gap record spans written at ingest — lost: write-path and retention change for a rare case (record 0216).
- Completeness from a stored fold time — lost: needs a new write in the fold; the earliest unfolded capture is exact and read-only.
- Restarts by decoding every `service_state` record in C# — lost: 87,600 decodes a year on the Celeron against N1; the SQL prefilter
  returns changes only, and C# decides on parsed values.
- Event markers from decoded `StoredRecord`s through `IRecordReader` — lost: a page of 10,000 kernel events with 16 KiB messages
  holds hundreds of MiB; projections keep only the marker fields.
- *Between samples* as a sentinel number in the value columns — lost: any number can be a real value; a separate index list keeps
  `double?` columns unchanged.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `ITimeSeriesReader`, `TimeRangeQuery`, `RawValueQuery`, `RollupStepQuery`, `LinePage`, `MetricLine`, `LinePoint`, `ServiceStateRow`, `BootTime`, `GapRecordRow`, `KernelEventRow`, `BackupRunRow` | new store read API |
| Vandox.Storage | `TimeSeriesQueries` (internal SQL), `SqliteStore` | implements `ITimeSeriesReader` |
| Vandox.Storage | `TimeSeriesService`, `TimeSeriesQuery`, `SeriesKey`, `TimeSeriesResult`, `TimeSeriesResolution`, `SeriesLine`, `SeriesPoint`, `PointState`, `SeriesGap`, `SeriesMarker`, `MarkerKind`, `BackupRunEnd` | new engine |
| Vandox.Backend | `TimeSeries/GatedTimeSeriesReader.cs`, `Hosting/ServeCommand.cs` | gate, registration |
| Vandox.Backend | `Components/Charts/ChartSeries.cs`, `ChartData.cs`, `ChartInterop.cs`, `wwwroot/js/charts.js` | bridged points |
| tools | `Vandox.StorageBenchmark/QueryBenchmark.cs`, `Program.cs` | `--query` scenario |

## Signatures (for the Dev's skeleton)

Properties have get and init accessors unless said otherwise; every class is public and sealed; their types stand in parentheses, outside the code spans.

Vandox.Storage, store reads:

- `ITimeSeriesReader` (public interface) with `Task<LinePage> RawValuesAsync(RawValueQuery query, CancellationToken cancellationToken)`, `Task<LinePage> RollupStepsAsync(RollupStepQuery query, CancellationToken cancellationToken)`, `Task<IReadOnlyList<AgentGap>> GapsInRangeAsync(TimeRangeQuery query, CancellationToken cancellationToken)`, `Task<DateTimeOffset?> EarliestUnfoldedAsync(CancellationToken cancellationToken)`, `Task<IReadOnlyList<ServiceStateRow>> ServiceChangesAsync(TimeRangeQuery query, CancellationToken cancellationToken)`, `Task<IReadOnlyList<ServiceStateRow>> PreviousServiceStatesAsync(IReadOnlyList<string> units, DateTimeOffset before, CancellationToken cancellationToken)`, `Task<IReadOnlyList<BootTime>> BootTimesAsync(TimeRangeQuery query, CancellationToken cancellationToken)`, `Task<IReadOnlyList<GapRecordRow>> GapRecordsAsync(TimeRangeQuery query, CancellationToken cancellationToken)`, `Task<IReadOnlyList<KernelEventRow>> KernelEventsAsync(TimeRangeQuery query, CancellationToken cancellationToken)`, `Task<IReadOnlyList<BackupRunRow>> BackupRunsAsync(TimeRangeQuery query, CancellationToken cancellationToken)`; `SqliteStore` implements all ten publicly.
- `TimeRangeQuery`: `TimeRangeQuery.From`, `TimeRangeQuery.To` (DateTimeOffset), `TimeRangeQuery.Limit` (int), `TimeRangeQuery.AfterId` (long; rows at the capture time `From` with an ID at most it are skipped).
- `RawValueQuery`: `RawValueQuery.Source`, `RawValueQuery.Name` (string), `RawValueQuery.From`, `RawValueQuery.To` (DateTimeOffset), `RawValueQuery.MaxTimes`, `RawValueQuery.MaxLines` (int).
- `RollupStepQuery`: `RollupStepQuery.Tier` (RollupTier), `RollupStepQuery.Source`, `RollupStepQuery.Name` (string), `RollupStepQuery.From`, `RollupStepQuery.To` (DateTimeOffset), `RollupStepQuery.Step` (TimeSpan), `RollupStepQuery.MaxLines` (int).
- `LinePage`: `LinePage.Lines` (IReadOnlyList of MetricLine), `LinePage.LinesCut`, `LinePage.TimesExceeded` (bool).
- `MetricLine`: `MetricLine.Labels` (string), `MetricLine.Points` (IReadOnlyList of LinePoint).
- `LinePoint`: `LinePoint.At`, `LinePoint.LastAt` (DateTimeOffset), `LinePoint.Minimum`, `LinePoint.Average`, `LinePoint.Maximum`, `LinePoint.Last` (double), `LinePoint.Count` (long).
- `ServiceStateRow`: `ServiceStateRow.Unit`, `ServiceStateRow.ActiveState` (string), `ServiceStateRow.ActiveEnterAt` (string, nullable), `ServiceStateRow.CapturedAt` (DateTimeOffset), `ServiceStateRow.Restarts` (long), `ServiceStateRow.BootId` (string, empty for none), `ServiceStateRow.Previous` (ServiceStateRow, nullable).
- `BootTime`: `BootTime.CapturedAt` (DateTimeOffset), `BootTime.BootId` (string).
- `GapRecordRow`: `GapRecordRow.Id` (long), `GapRecordRow.AgentId`, `GapRecordRow.Cause`, `GapRecordRow.Collector`, `GapRecordRow.SpanFrom`, `GapRecordRow.SpanTo` (string, as stored), `GapRecordRow.CapturedAt` (DateTimeOffset), `GapRecordRow.FirstSeq`, `GapRecordRow.LastSeq` (long, nullable).
- `KernelEventRow`: `KernelEventRow.Id` (long), `KernelEventRow.CapturedAt` (DateTimeOffset), `KernelEventRow.Type`, `KernelEventRow.BootId`, `KernelEventRow.VictimCommand` (string), `KernelEventRow.KernelTimeNs`, `KernelEventRow.VictimPid` (long, nullable).
- `BackupRunRow`: `BackupRunRow.Id` (long), `BackupRunRow.CapturedAt` (DateTimeOffset), `BackupRunRow.Phase`, `BackupRunRow.BootId`, `BackupRunRow.StartedAt`, `BackupRunRow.LastSeenAt`, `BackupRunRow.GoneBy`, `BackupRunRow.Command` (string, times as stored, empty when absent).

Vandox.Storage, engine:

- `TimeSeriesService`: `TimeSeriesService.MaxSeries` (16), `TimeSeriesService.MaxLines` (64), `TimeSeriesService.MaxTarget` (2000), `TimeSeriesService.MaxMarkers` (1000), `TimeSeriesService.MaxGaps` (1000), all public int constants; `public TimeSeriesService(ITimeSeriesReader reader, RetentionPolicy policy, TimeProvider clock)`; `public Task<TimeSeriesResult> QueryAsync(TimeSeriesQuery query, CancellationToken cancellationToken)`.
- `SeriesKey`: `public SeriesKey(string source, string name)`; `SeriesKey.Source`, `SeriesKey.Name` (string, get only).
- `TimeSeriesQuery`: `TimeSeriesQuery.Series` (IReadOnlyList of SeriesKey), `TimeSeriesQuery.From`, `TimeSeriesQuery.To` (DateTimeOffset), `TimeSeriesQuery.Target` (int).
- `TimeSeriesResolution` (public enum) with `TimeSeriesResolution.Raw`, `TimeSeriesResolution.Step`.
- `PointState` (public enum) with `PointState.Value`, `PointState.Gap`, `PointState.BetweenSamples`.
- `MarkerKind` (public enum) with `MarkerKind.OomKill`, `MarkerKind.Boot`, `MarkerKind.ServiceRestart`, `MarkerKind.BackupRun`.
- `BackupRunEnd` (public enum) with `BackupRunEnd.Ended`, `BackupRunEnd.Running`, `BackupRunEnd.Unknown`.
- `SeriesPoint`: `SeriesPoint.State` (PointState), `SeriesPoint.Minimum`, `SeriesPoint.Average`, `SeriesPoint.Maximum`, `SeriesPoint.Last` (double), `SeriesPoint.LastAt` (DateTimeOffset), `SeriesPoint.Count` (long).
- `SeriesLine`: `SeriesLine.Source`, `SeriesLine.Name`, `SeriesLine.Labels` (string), `SeriesLine.Points` (IReadOnlyList of SeriesPoint, one per axis time).
- `SeriesGap`: `SeriesGap.AgentId`, `SeriesGap.Kind` (record, sequence or time), `SeriesGap.Cause`, `SeriesGap.State`, `SeriesGap.Collector` (string, empty for none), `SeriesGap.From`, `SeriesGap.To` (DateTimeOffset); `public bool Concerns(string source, string name)`.
- `SeriesMarker`: `SeriesMarker.Kind` (MarkerKind), `SeriesMarker.At` (DateTimeOffset), `SeriesMarker.EndsAt`, `SeriesMarker.LastSeenAt` (DateTimeOffset, nullable), `SeriesMarker.End` (BackupRunEnd, nullable), `SeriesMarker.Text` (string: victim command, unit or backup command), `SeriesMarker.Pid` (long, nullable), `SeriesMarker.BootId` (string).
- `TimeSeriesResult`: `TimeSeriesResult.Resolution` (TimeSeriesResolution), `TimeSeriesResult.Step` (TimeSpan, nullable), `TimeSeriesResult.Times` (IReadOnlyList of DateTimeOffset), `TimeSeriesResult.Lines` (of SeriesLine), `TimeSeriesResult.CutSeries` (of SeriesKey), `TimeSeriesResult.StartsBeforeRollups` (bool), `TimeSeriesResult.CompleteUntil`, `TimeSeriesResult.GapsCompleteUntil`, `TimeSeriesResult.MarkersCompleteUntil` (DateTimeOffset, nullable), `TimeSeriesResult.Gaps` (of SeriesGap), `TimeSeriesResult.Markers` (of SeriesMarker).

Vandox.Backend:

- `GatedTimeSeriesReader` (implements ITimeSeriesReader): `public GatedTimeSeriesReader(ITimeSeriesReader inner, WebReaderGate gate)`; the ten members, one gate slot per call.
- `public ChartSeries(string label, IReadOnlyList<double?> values, IReadOnlyList<bool> bridged)` and `ChartSeries.Bridged` (IReadOnlyList of bool, get only); the two-argument constructor stays and gives an empty list.
- `internal int[][] ToBridges()` on ChartData.

Names checked against *Writing code* (no enum suffix the analyzers refuse, no nested type). Existing files the skeleton rewrites:
none whole; SqliteStore.cs gains a region, ServeCommand.cs two registrations.

## Test files

`tests/Vandox.Storage.Tests/TimeSeriesServiceTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreTimeSeriesTests.cs`,
`tests/Vandox.Storage.Tests/SeriesGapTests.cs`, `tests/Vandox.Backend.Tests/GatedTimeSeriesReaderTests.cs`, and the existing
`tests/Vandox.Backend.Tests/ChartDataTests.cs`, `ChartInteropTests.cs`, `ServeTests.cs`. New double: `CountingTimeSeriesReader`, wrapping a
real store and recording every call and limit (Storage tests); samples in `TimeSeriesSamples.cs` (Storage tests).

Existing test code that calls a changed signature: `ChartInteropTests` and `RecordingJsRuntime` assertions on the `create`/`update`
argument lists — adapted by the Tester. `ChartSeries`' two-argument constructor is unchanged.

Files no unit test can cover: `src/Vandox.Backend/wwwroot/js/charts.js` (browser script, already excluded, record 0098);
`tools/Vandox.StorageBenchmark/QueryBenchmark.cs` (console program in no test report, like the existing scenarios). No new exclusion.

## Areas

- New area **Time-series queries**, `docs/areas/time-series-query.md`. Scope: the in-process query of metric lines with resolution,
  axis, point states, gap list and collector mapping, markers, completeness, caps, errors and the performance criterion. Not here: the
  store reads (storage reading), the rollup fold (retention), the chart (web UI), the views.
- Storage reading, Web UI, Retention and rollups: see *Documentation updates*.

## Documentation updates

All by the Dev, after the code is green:

- `docs/areas/time-series-query.md` (new, from `_template.md`) and its index row in `docs/areas/README.md`: every rule of *Approach*
  as behavior (texts of AC8, ladder, windows, caps, clamping), linking records 0214, 0215, 0216 and 0028, with this paragraph verbatim:
  "Marker texts (SeriesMarker.Text: victim command, unit, backup command), line labels (SeriesLine.Labels), boot IDs and gap
  collectors are external text from the agent, returned as stored; a view shows them, and a chart label built from them, only as
  [Web UI](web-ui.md) *External text* writes external text."
- `docs/areas/storage-reading.md`: *Scope* names time-series reads; new section *Time-series reads* with the ten reads, limits (each read refuses a limit above its own maximum: 1,001 for gap rows and the event projections, 2,001 for service changes; `PreviousServiceStatesAsync` refuses more than 2,001 units, takes them as one bound JSON parameter, and limits the 10,000 most recent `service_state` rows before `from` before it filters them by unit) and
  orders (UTF-8 byte order of labels, 1,024 label sets examined, pages of 1,001 projected rows, ten pages per kind, the
  10,000-row backward scan for previous unit states).
- `docs/areas/web-ui.md`: *Charts* — the *between samples* state and the three new error texts; *Store reads of the views* — the
  time-series service shares the gate; *Implementation* — `GatedTimeSeriesReader`.
- `docs/areas/retention.md`, *Rollups*: the readers line adds the time-series query.
- `docs/ARCHITECTURE.md`: backend component — the time-series service the views call in process; *Interfaces* list adds
  `ITimeSeriesReader`; records 0214–0216 linked.
- `docs/BENCHMARKS.md`: section *Time-series query* — criterion (spec N1), workload (with the hourly `service_state` re-records), `--query` procedure, results table (the
  development-host row by the Dev, the DS918+ row from the Product Manager's run, written in by the Dev).
- `CHANGELOG.md`: an `[Unreleased]` *Added* entry for the time-series query service.
- `.squad/project.md` *Test doubles*: the Tester reports a row for `CountingTimeSeriesReader`; the orchestrator writes it in.

## Architecture check

Record 0028 holds: a span without data is `Gap`, never bridged; *between samples* exists only where another line has a sample and the
line itself has no recorded gap or empty 5-minute span there. Live-only alerting, the agent and ingest are untouched. The store's
four readers keep two free: every new read takes the shared gate per call.

## Security considerations

- [x] Limits apply to stored, already decoded values: projected rows never carry `message` or the payload; pages of 1,001 rows,
  ten per kind; the backward unit scan examines 10,000 rows; added axis times one per interval. A member a forged record lacks reads as
  NULL; an unparseable time is no enter move or a skipped gap; payload times outside `[from, now]` are clamped or replaced (Approach).
- [x] Exceptions: `InvalidQueryException` (the AC8 texts) and `StoreException` from a failing read reach the calling view, which maps
  them to its own message (#48–#52); `OperationCanceledException` on cancellation. No raw text reaches a user here.

## Decision records

- `docs/decisions/0214-time-series-queries-share-one-resolution-and-one-time-axis-with-bridged-samples.md` (Proposed)
- `docs/decisions/0215-a-service-restart-is-a-rise-of-nrestarts-or-a-new-active-enter-time-of-a-running-or-failed-unit.md` (Proposed)
- `docs/decisions/0216-time-series-context-is-read-in-fixed-windows-around-the-range.md` (Proposed)

Their area link resolves once the Dev adds `docs/areas/time-series-query.md`; `decision-check.py` fails until then.

## Challenge

- M1 (raw check double-counts shared times). Accepted: every series is read with `MaxTimes` = target + 1 and the engine counts the
  union, abandoning raw once it exceeds the target (*Resolution*); N2 states one time more than the target per series; test
  `QueryAsyncSeriesOfOneCollectorSharingTimesStayRaw`.
- m1 (absent `active_enter_at`). Accepted: the SQL compares with `IS NOT`, and absent before / present now is a moved enter time
  (*Markers*, record 0215); a row of `QueryAsyncServiceRestartRules` and of `ServiceChangesAsyncReturnsFirstPerUnitAndChangesWithPrevious`.
- m2 (boot captured after the unit's first post-boot record). Accepted: `ServiceStateRow.BootId` from `records.boot_id`; differing boot
  IDs are a boot between, `BootTimesAsync` stays the second check; test row added.
- m3 (derived bounds below the storable minimum). Accepted with S6: every derived bound is clamped; `QueryAsyncStorableEdgesReadWithoutError`.
- m4 (benchmark without service re-records). Accepted: spec N1 and task 10 write the hourly re-records of 10 units (87,600 a year).
- m5 (`restarts` is a uint). Accepted: `ServiceStateRow.Restarts` is a `long`, compared as an integer; record 0215 drops the 2^53
  consequence.
- m6 (one statement of the label order). Accepted: UTF-8 byte order (SQLite BINARY), never re-sorted in C#, in spec AC7 and E5, the
  plan, the area document and the test name `QueryAsyncOverSixtyFourLinesCutsInSeriesThenUtf8ByteOrder`.
- Note on fact 5a: confirmed and marked so.

## Security review

- S1 (decoded records of 10,000 per page). Accepted: gap records, kernel events and backup runs are read by projections without the
  payload or `message`, in pages of at most 1,001 rows, at most ten per kind; a limit over 1,001 is refused; the engine no longer takes
  an `IRecordReader`. N2 states the bound; `QueryAsyncEventReadsUseAtMostTenPagesOfOneThousandOne` and
  `EventReadsRefuseLimitOverOneThousandOne` pin it.
- S2 (unbounded backward seek). Accepted, first option: one backward scan for all first-row units together, at most 10,000
  `service_state` rows; a unit not found has no previous record and gives no restart. Stated in spec E12 and N2, record 0215, the area
  document and storage-reading; test `PreviousServiceStatesAsyncScansAtMostTenThousandRows` (300 new units).
- S3 (unbounded added axis times). Accepted: a time is added only when the current axis has none in the interval, so at most one per
  interval and at most 2 × target − 1 times; *Raw points*, N2, the area document; test `QueryAsyncRawGapAcrossSixtyFourLinesAddsOneTime`.
- S4 (forged payload times). Accepted: a restart is timed at its new enter time only inside `[from, end)` and not after its capture,
  else at the capture time; a backup with `started_at` at or after `end` is dropped, its `EndsAt` and `LastSeenAt` clamped to `now`; a
  gap record's end is clipped to `now`. Test rows in `QueryAsyncRestartTime` and `QueryAsyncBackupRunSpans`.
- S5 (external-text paragraph). Accepted verbatim in *Documentation updates* (task 11).
- S6 (derived windows outside the storable range). Accepted: every derived bound is clamped; N2's second clause takes the suggested
  wording; `QueryAsyncStorableEdgesReadWithoutError`.

## Out of scope / follow-ups

See spec *Out of scope*. A view needing log-shipping gaps reported more than seven days late revisits record 0216.

## Security notes

All six of my findings are resolved in the revised plan, and I'm approving it. The revision adds no blocking issue; I have two small non-blocking notes.

I reviewed the revised `/home/user/Vandox/specs/feature-47-time-series-query/plan.md` (new *Security review* section, *Approach*, *Bounds*, *Signatures*, *Tests per criterion*) and the spec diff against `c8eed5f`, at branch head `69c21c9`. I ran no new measurement: last round's probe of the bundled SQLite 3.53.4 (json_extract on a missing member gives NULL; the window sort spills to a temp file) still covers what the revision relies on.

## Previous findings

- **S1 (event reads kept whole decoded records) — resolved.**
  - Gap records, kernel events and backup runs are now read through projections (`GapRecordsAsync`, `KernelEventsAsync`, `BackupRunsAsync`). These never carry the payload or `message`.
  - Reads come in pages of at most 1,001 rows, at most ten pages per kind, and a larger limit is refused.
  - The engine no longer takes an `IRecordReader`.
  - The bound holds: about 10,010 rows × about 2.5 KiB per kind. The longest stored fields are the 1 KiB `victim_command` and backup `command` (`Check.RequiredShort`) and the 256-byte unit.
  - Pinned by `QueryAsyncEventReadsUseAtMostTenPagesOfOneThousandOne` and `EventReadsRefuseLimitOverOneThousandOne`.

- **S2 (unbounded backward search for a unit's previous record) — resolved.**
  - `PreviousServiceStatesAsync` now does one backward scan of at most 10,000 `service_state` rows. A unit not found within it has no previous record, so it counts as no restart.
  - Spec E12 and N2 and record 0215 state this.
  - The test row "previous record beyond the 10,000 rows examined" in `QueryAsyncServiceRestartRules` tells a cap on rows examined apart from a `LIMIT` on rows that match. That makes the bound testable (see note 1).

- **S3 (unbounded added axis times) — resolved.** A time is added only when the current axis has none in the interval. That gives at most one per interval and at most 2 × target − 1 times in total. Stated in *Raw points*, *Bounds* and N2; pinned by `QueryAsyncRawGapAcrossSixtyFourLinesAddsOneTime`.

- **S4 (forged payload times) — resolved.**
  - A restart is timed at its new `active_enter_at` only when that lies within `[from, end)` and not after its capture time; otherwise it is timed at the capture time, which always lies in the range.
  - A backup with `started_at >= end` is dropped, and its `EndsAt`/`LastSeenAt` are clamped to `now`.
  - A gap record's end is clipped to `now`, and a span that does not parse is skipped.
  - Test rows are in `QueryAsyncRestartTime` and `QueryAsyncBackupRunSpans`.
  - This now holds AC5 and AC6/E2 even against forged values that pass the storable-range check.

- **S5 (external-text paragraph) — resolved.** The paragraph is in *Documentation updates*, verbatim. The *External text* heading exists in `docs/areas/web-ui.md:84`.

- **S6 (derived windows outside the storable range) — resolved.** Every derived bound is clamped to [`StorableTime.Min`, `StorableTime.Max`], and N2 takes the suggested wording. Pinned by `QueryAsyncStorableEdgesReadWithoutError`.

## Is the revision introducing new issues?

Checked:
- The restart prefilter's `IS NOT` comparison: a NULL against a present value is now a change. That only widens the C# check; it does not skip it.
- `records.boot_id` comes from the envelope the backend stored, and is only compared for equality.
- `restarts` is read as `long` from a `uint`.
- Paging uses `AfterId` on (capture time, ID), so a page of 1,001 rows that share one capture time still moves forward.
- Units may only contain `[A-Za-z0-9:_.@\-]` (`ServiceState.cs:59`), so the `json_each` units list cannot break out of its JSON array.

1. [NON-BLOCKING] **Neither the per-read maximums nor the "rows examined" meaning are stated precisely.**
   - The refused limit ("a limit over 1,001 is refused") is stated for the event reads only. `ServiceChangesAsync` uses `TimeRangeQuery` with limit 2,001, and `PreviousServiceStatesAsync` takes a units list whose length nothing bounds: up to 2,001 names today, from the rows of one `ServiceChangesAsync` page.
   - "A single backward scan of at most 10,000 `service_state` rows … filtered to the units" can still be built as a `LIMIT` on the rows that match, which scans the whole history for a unit that is not found.
   - Smallest fix: in *Event reads* or *Markers*, and in the storage-reading section, add verbatim:
     "Each read refuses a limit above its own maximum: 1,001 for gap rows and the event projections, 2,001 for
     service changes; `PreviousServiceStatesAsync` refuses more than 2,001 units, takes them as one bound JSON
     parameter, and limits the 10,000 most recent `service_state` rows before `from` before it filters them by unit."
   - In addition, the Tester builds the "beyond the 10,000 rows examined" row with 10,000 rows of other units between the previous record and `from`.
   - Recommendation: fix now; it is one sentence plus test data.

2. [NON-BLOCKING] **One line in *Documentation updates* is out of date.** The storage-reading entry still says "the seven reads", but there are now ten. Smallest fix: replace "with the ten reads" with "with the ten reads", verbatim. Recommendation: fix now.
