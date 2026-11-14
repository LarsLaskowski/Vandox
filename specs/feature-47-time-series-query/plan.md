# Plan: Time-series query API

Source: Issue #47 | [spec.md](spec.md)
Tier: standard — new read behavior (a query service, new store reads, a chart state) that creates, changes or removes no security control.
Security areas touched: 10 Parsing of external input (new store reads decode stored agent payloads with `json_extract` and the payload rules, forged values included); 12 Logging and display of external data (marker texts handed to the views; the chart data path in `wwwroot/js/charts.js`)
Plan size: one feature with eleven criteria spanning seven store reads, the query engine with three marker kinds and two gap sources, the read gate and a chart contract change.

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
| 5a | The stored document holds those members at its top level, so `json_extract(data, '$.unit')` reads them | to verify: the first `SqliteStoreTimeSeriesTests` case over a written batch, Tester, step 5 | unverified |
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
| AC1 | `QueryAsyncShortRangeWithinTargetIsRaw`, `QueryAsyncRawOverTargetTimesChoosesFiveMinuteStep`, `QueryAsyncYearChoosesTwelveHourStepFromHourlyRollups`, `QueryAsyncStepChoiceKeepsOverlappingStepsWithinTarget` (table: 1 h/24 h/7 d/30 d/1 y/3 y at targets 1, 100, 1000, 2000), `QueryAsyncRangeBeforeRawPeriodIsNeverRaw`, `QueryAsyncRangeBeforeFiveMinutePeriodUsesHourSteps`, `QueryAsyncRangeBeforeHourlyPeriodFlagsEmptyLines`, `QueryAsyncLabelledSeriesHasOneLinePerLabelSet`, `QueryAsyncSeriesWithoutValuesHasNoLine` | `tests/Vandox.Storage.Tests/TimeSeriesServiceTests.cs` |
| AC1 | `RawValuesAsyncStopsAfterOneTimeOverMaxTimes`, `RawValuesAsyncQueryPlanStreamsByCaptureTime` | `tests/Vandox.Storage.Tests/SqliteStoreTimeSeriesTests.cs` |
| AC2 | `QueryAsyncStepTimesAreEpochMultiplesFromStepHoldingFrom`, `QueryAsyncStepPointMergesBucketsCountWeighted`, `QueryAsyncStepPointCoversWholeStepBeyondRange`, `QueryAsyncRawAxisIsUnionOfLineTimes`, `QueryAsyncRawDuplicateTimeMergesIntoOnePoint` | `TimeSeriesServiceTests.cs` |
| AC2 | `RollupStepsAsyncMergesMinMaxCountAndLatestLast`, `RollupStepsAsyncClampsSumToFiniteRange` | `SqliteStoreTimeSeriesTests.cs` |
| AC3 | `QueryAsyncEmptyStepIsGap`, `QueryAsyncRawTimeOfOtherLineIsBetweenSamples`, `QueryAsyncRawConcerningGapBreaksLineAndAddsTime`, `QueryAsyncRawGapOfOtherCollectorDoesNotBreakLine`, `QueryAsyncRawEmptyAlignedFiveMinutesBreaksLine`, `QueryAsyncRawFourMinuteSpacingStaysConnected` | `TimeSeriesServiceTests.cs` |
| AC4 | `QueryAsyncListsOpenAndFinalGapsWithoutClosed`, `QueryAsyncAgentGapRecordIsFinal`, `QueryAsyncGapRecordInsideSequenceGapOfSameCauseListedOnce`, `QueryAsyncGapRecordFoundOnlyInsideCaptureWindow` | `TimeSeriesServiceTests.cs` |
| AC4 | `ConcernsMapsCollectorToLines` (table: `self`→`agent` lines only; `filesystem`→`filesystem_*` not `mem_*`; `clock`; `system`→other `system` names; `mariadb`; `logs`/`kernel`→none; no collector and `future`→every line) | `tests/Vandox.Storage.Tests/SeriesGapTests.cs` |
| AC4 | `GapsInRangeAsyncReturnsOverlappingOpenAndFinalOfAllAgents`, `GapsInRangeAsyncReportsDerivedTimeGapState` | `SqliteStoreTimeSeriesTests.cs` |
| AC5 | `QueryAsyncOomKillsOncePerIdentity`, `QueryAsyncBootsOncePerBootId`, `QueryAsyncServiceRestartRules` (table: restarts rose; enter moved from `active` and from `failed`; from `inactive`; boot between; hourly re-record; restarts fell; previous record before `from`), `QueryAsyncRestartTimedAtNewActiveEnter`, `QueryAsyncBackupRunSpans` (table: ended in range; started a day before; still running; unknown end after a later boot; start written twice), `QueryAsyncMarkersInTimeOrder` | `TimeSeriesServiceTests.cs` |
| AC5 | `ServiceChangesAsyncReturnsFirstPerUnitAndChangesWithPrevious`, `PreviousServiceStateAsyncFindsLastRecordOfUnitOfAnyAge`, `BootTimesAsyncReturnsOnlyBoots` | `SqliteStoreTimeSeriesTests.cs` |
| AC6 | `QueryAsyncReturnsNothingAfterQueryTime`, `QueryAsyncCompleteUntilIsEarliestUnfoldedCapture` (stalled fold: values stored, no fold step), `QueryAsyncCompleteUntilIsQueryTimeWhenAllFolded`, `QueryAsyncRawHasNoCompleteUntil` | `TimeSeriesServiceTests.cs` |
| AC6 | `EarliestUnfoldedAsyncReadsAboveWatermark` | `SqliteStoreTimeSeriesTests.cs` |
| AC7 | `QueryAsyncOverSixtyFourLinesCutsInSeriesThenByteOrder`, `QueryAsyncOverThousandMarkersKeepsEarliestWithCompleteUntil`, `QueryAsyncOverThousandGapsKeepsEarliestWithCompleteUntil`, `QueryAsyncFullRecordPageBoundsCompleteUntil` | `TimeSeriesServiceTests.cs` |
| AC8 | `QueryAsyncInvalidQueryRefusedBeforeStoreRead` (table of the six exact texts, a `CountingTimeSeriesReader` and a counting record reader that must see zero calls) | `TimeSeriesServiceTests.cs` |
| AC9 | `QueryAsyncCancelledThrowsWithoutResult` | `TimeSeriesServiceTests.cs` |
| AC9 | `EveryCallHoldsTheGateOnlyWhileItRuns`, `CancelledWaitForSlotThrowsAndHoldsNothing` | `tests/Vandox.Backend.Tests/GatedTimeSeriesReaderTests.cs` |
| AC9 | `ServeRegistersTimeSeriesServiceOverTheGate` | `tests/Vandox.Backend.Tests/ServeTests.cs` |
| AC10 | manual: Product Manager, DS918+ SSD volume, `tools/Vandox.StorageBenchmark --query` published from the stamped branch head by the *Procedure* of `docs/BENCHMARKS.md` (as for #46); fallback if the Product Manager cannot publish: the orchestrator publishes it in the session and attaches the zipped folder to the early draft PR. Development-host run: Dev, step 6, recorded as order of magnitude | — |
| AC11 | `ConstructorRefusesBridgedPointWithValue`, `ConstructorRefusesBridgeCountMismatch`, `ConstructorRefusesBridgedStackedSeries` | `tests/Vandox.Backend.Tests/ChartDataTests.cs` |
| AC11 | `RenderAsyncPassesBridgedIndexesOnCreateAndUpdate` | `tests/Vandox.Backend.Tests/ChartInteropTests.cs` |
| AC11 | manual: Reviewer, step 8, reads `charts.js` (bridged indexes set to `undefined`, nothing else); CI: *Image build check* (Dev, step 6) for the changed static asset | — |

## Approach

**Placement.** The engine `TimeSeriesService` lives in `Vandox.Storage` (flat namespace, like the other readers) and depends only on
`ITimeSeriesReader`, `IRecordReader`, `RetentionPolicy` and `TimeProvider`, so the benchmark tool runs it without ASP.NET. `vandoxd`
registers one singleton over `GatedTimeSeriesReader` and `GatedRecordReader` (both on the shared `WebReaderGate`), with
`RetentionService.CreatePolicy(config)`; Blazor components inject it. No route, page or option.

**Validation (AC8)**, before any read, `InvalidQueryException` with exactly: `store: invalid query: at least one series is required`,
`store: invalid query: at most 16 series`, `store: invalid query: source and name are required`, `store: invalid query: target must be
1 to 2000`, `store: invalid query: from must be before to`, `store: invalid query: from and to must be within the storable range`. A
series listed twice is used once.

**Range.** `now` from the clock; `end = min(to, now)`; `from >= now` gives an empty result. Nothing after `now` is returned (AC6).

**Resolution (AC1).** Raw is tried when `policy.Raw` is null or `from >= now − Raw`: `RawValuesAsync` per series in order, with
`MaxTimes` = target + 1 − (distinct times so far, at least 1); the first `TimesExceeded` abandons raw. Otherwise the ladder 5, 10, 15,
30 min (only while `from >= now − FiveMinuteRollups` or that period is null), 1, 2, 3, 6, 12 h, 1 d, then k days: the smallest step `s`
with `floor((end − 1 tick)/s) − floor(from/s) + 1 <= target`, the k-day step computed directly. `StartsBeforeRollups` is set when the
hourly period is not null and `from < now − HourlyRollups`. Steps under one hour read the 5-minute tier, the rest the hourly tier.

**Step points (AC2).** `RollupStepsAsync` reads `[floor(from/s)·s, (floor((end−1)/s)+1)·s)`: the label sets of the series by a loose
index scan over the primary key (`min(labels) > previous`), at most 1,024 sets examined, then one primary-key range seek per set,
merging buckets per step in one pass: min, max, sum clamped to ±`double.MaxValue` as the fold does, count, last by (`last_at`,
`last_record_id`). A set without a bucket is no line; lines stop at `MaxLines` + 1 (`LinesCut`). The axis is every step start of the
range; a line's missing step is `PointState.Gap`. Average = sum / count.

**Raw points (AC2, AC3).** `RawValuesAsync` returns rows ordered by (`captured_at`, labels, record ID), labels normalized as the fold
does (NULL and `{}` → `''`). Rows of one line at one time merge like buckets. Axis = union of times. Per line: own time → value;
another line's time → `BetweenSamples`; then for each pair of consecutive own values `t1 < t2`, when a gap that `Concerns` the line
overlaps `(t1, t2)` or a whole UTC 5-minute span lies inside `(t1, t2)`, every axis time in `(t1, t2)` becomes `Gap`, and if there is
none, the time `max(t1 + 1 tick, start of that gap or span)` is added to the axis (the other lines get `BetweenSamples` or, inside a
gap of theirs, `Gap`). Axis times inside a concerning gap are `Gap` also before the first and after the last value.

**Gaps (AC4).** Rows: `GapsInRangeAsync(From = from, To = end, Limit = 1,001)` — `to_at > from AND from_at < to AND` derived state not
`closed`, all agents, ordered by `from_at, id`, through the derived-state SQL of `GapReadSql`. Records: `RecordsAsync(kind gap, [from −
1 h, end + 7 d), limit 10,000)` (record 0216), span filtered to the range, reported `final`, skipped when it names numbers inside a
returned sequence row of its agent and cause. Sorted by start, then rows before records, then ID; cut at 1,000. `GapsCompleteUntil`:
the start of the 1,001st, a full row page's 1,001st start, a full record page's last capture − 7 days — the earliest; null when
complete. `SeriesGap.Concerns(source, name)`: the mapping of spec AC4 in one internal table.

**Markers (AC5).** Kernel events: `RecordsAsync(kind kernel_event, [from, end), 10,000)`; `oom_kill` once per (boot ID, kernel time,
PID) when all three exist, else per record; boot once per boot ID. Restarts: `ServiceChangesAsync([from, end), 10,001)` — SQL over
`records_kind_time` with `json_extract` and `LAG … OVER (PARTITION BY unit ORDER BY captured_at, id)`, returning each unit's first row
in range and every row whose `restarts` or `active_enter_at` text differs from its predecessor, with the predecessor; for first rows
`PreviousServiceStateAsync(unit, from)` (a backward seek on the kind index, first 256 units by first appearance); boots between come
from `BootTimesAsync([earliest predecessor, end), 10,000)`. The rule (record 0215) is applied on parsed values: `restarts` as double;
`active_enter_at` parsed as an instant (unparseable: no enter move). Backups: `RecordsAsync(kind backup_run, [from − 1 d, end + 1 d),
10,000)`, paired by (boot ID, `started_at`), duplicates dropped; a run is kept when `started_at < end` and (`gone_by > from` or no end);
without an end, `BootTimesAsync((start, now], 1)` decides `Running` or `Unknown`. All markers sorted by `At` then kind then record
ID, cut at 1,000; `MarkersCompleteUntil` as for gaps, with a full page's last capture (− 1 d for backups, the last row for restarts).

**Completeness (AC6).** At a step, `EarliestUnfoldedAsync`: `SELECT min(captured_at) FROM metrics WHERE record_id > watermark` (primary
key range; empty or no watermark row → null); `CompleteUntil = min(now, that)` or `now`. Raw: null.

**Chart (AC11).** `ChartSeries` gains `Bridged` (`IReadOnlyList<bool>`, empty by default). `ChartData` refuses: a bridge list whose
length is neither 0 nor the time count — `The series '<label>' has <n> bridge flags for <m> points in time.`; a bridged point with a
value — `The series '<label>' has a value at bridged point <i>.`; a bridged stacked series — `The stacked series '<label>' cannot
bridge points.` `ChartData.ToBridges()` gives per series the bridged indexes; `ChartInterop` passes them as a fifth argument of
`create`/`createStacked` and a second of `update`; `charts.js` sets those cells to `undefined` before handing columns to uPlot. Nulls
stay gaps. Layout of the chart is unchanged.

**Bounds (N2).** Lines ≤ 64, times ≤ target + inserted gap times, markers and gaps ≤ 1,000 each, record reads one page per kind; the
caller's token interrupts every store call (`ReaderLease`).

## Options considered

- Engine in `Vandox.Backend` — lost: the benchmark would need the ASP.NET runtime on the reference host, and nothing of it is web.
- A resolution per line, steps only, raw by collector interval — lost (record 0214).
- A table of gap record spans written at ingest — lost: write-path and retention change for a rare case (record 0216).
- Completeness from a stored fold time — lost: needs a new write in the fold; the earliest unfolded capture is exact and read-only.
- Restarts by decoding every `service_state` record in C# — lost: 87,600 decodes a year on the Celeron against N1; the SQL prefilter
  returns changes only, and C# decides on parsed values.
- *Between samples* as a sentinel number in the value columns — lost: any number can be a real value; a separate index list keeps
  `double?` columns unchanged.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `ITimeSeriesReader`, `TimeRangeQuery`, `RawValueQuery`, `RollupStepQuery`, `LinePage`, `MetricLine`, `LinePoint`, `ServiceStateRow`, `BootTime` | new store read API |
| Vandox.Storage | `TimeSeriesQueries` (internal SQL), `SqliteStore` | implements `ITimeSeriesReader` |
| Vandox.Storage | `TimeSeriesService`, `TimeSeriesQuery`, `SeriesKey`, `TimeSeriesResult`, `TimeSeriesResolution`, `SeriesLine`, `SeriesPoint`, `PointState`, `SeriesGap`, `SeriesMarker`, `MarkerKind`, `BackupRunEnd` | new engine |
| Vandox.Backend | `TimeSeries/GatedTimeSeriesReader.cs`, `Hosting/ServeCommand.cs` | gate, registration |
| Vandox.Backend | `Components/Charts/ChartSeries.cs`, `ChartData.cs`, `ChartInterop.cs`, `wwwroot/js/charts.js` | bridged points |
| tools | `Vandox.StorageBenchmark/QueryBenchmark.cs`, `Program.cs` | `--query` scenario |

## Signatures (for the Dev's skeleton)

Properties have get and init accessors unless said otherwise; every class is public and sealed; their types stand in parentheses, outside the code spans.

Vandox.Storage, store reads:

- `ITimeSeriesReader` (public interface) with `Task<LinePage> RawValuesAsync(RawValueQuery query, CancellationToken cancellationToken)`, `Task<LinePage> RollupStepsAsync(RollupStepQuery query, CancellationToken cancellationToken)`, `Task<IReadOnlyList<AgentGap>> GapsInRangeAsync(TimeRangeQuery query, CancellationToken cancellationToken)`, `Task<DateTimeOffset?> EarliestUnfoldedAsync(CancellationToken cancellationToken)`, `Task<IReadOnlyList<ServiceStateRow>> ServiceChangesAsync(TimeRangeQuery query, CancellationToken cancellationToken)`, `Task<ServiceStateRow?> PreviousServiceStateAsync(string unit, DateTimeOffset before, CancellationToken cancellationToken)`, `Task<IReadOnlyList<BootTime>> BootTimesAsync(TimeRangeQuery query, CancellationToken cancellationToken)`; `SqliteStore` implements all seven publicly.
- `TimeRangeQuery`: `TimeRangeQuery.From`, `TimeRangeQuery.To` (DateTimeOffset), `TimeRangeQuery.Limit` (int).
- `RawValueQuery`: `RawValueQuery.Source`, `RawValueQuery.Name` (string), `RawValueQuery.From`, `RawValueQuery.To` (DateTimeOffset), `RawValueQuery.MaxTimes`, `RawValueQuery.MaxLines` (int).
- `RollupStepQuery`: `RollupStepQuery.Tier` (RollupTier), `RollupStepQuery.Source`, `RollupStepQuery.Name` (string), `RollupStepQuery.From`, `RollupStepQuery.To` (DateTimeOffset), `RollupStepQuery.Step` (TimeSpan), `RollupStepQuery.MaxLines` (int).
- `LinePage`: `LinePage.Lines` (IReadOnlyList of MetricLine), `LinePage.LinesCut`, `LinePage.TimesExceeded` (bool).
- `MetricLine`: `MetricLine.Labels` (string), `MetricLine.Points` (IReadOnlyList of LinePoint).
- `LinePoint`: `LinePoint.At`, `LinePoint.LastAt` (DateTimeOffset), `LinePoint.Minimum`, `LinePoint.Average`, `LinePoint.Maximum`, `LinePoint.Last` (double), `LinePoint.Count` (long).
- `ServiceStateRow`: `ServiceStateRow.Unit`, `ServiceStateRow.ActiveState` (string), `ServiceStateRow.ActiveEnterAt` (string, nullable), `ServiceStateRow.CapturedAt` (DateTimeOffset), `ServiceStateRow.Restarts` (double), `ServiceStateRow.Previous` (ServiceStateRow, nullable).
- `BootTime`: `BootTime.CapturedAt` (DateTimeOffset), `BootTime.BootId` (string).

Vandox.Storage, engine:

- `TimeSeriesService`: `TimeSeriesService.MaxSeries` (16), `TimeSeriesService.MaxLines` (64), `TimeSeriesService.MaxTarget` (2000), `TimeSeriesService.MaxMarkers` (1000), `TimeSeriesService.MaxGaps` (1000), all public int constants; `public TimeSeriesService(ITimeSeriesReader reader, IRecordReader records, RetentionPolicy policy, TimeProvider clock)`; `public Task<TimeSeriesResult> QueryAsync(TimeSeriesQuery query, CancellationToken cancellationToken)`.
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

- `GatedTimeSeriesReader` (implements ITimeSeriesReader): `public GatedTimeSeriesReader(ITimeSeriesReader inner, WebReaderGate gate)`; the seven members, one gate slot per call.
- `public ChartSeries(string label, IReadOnlyList<double?> values, IReadOnlyList<bool> bridged)` and `ChartSeries.Bridged` (IReadOnlyList of bool, get only); the two-argument constructor stays and gives an empty list.
- `internal int[][] ToBridges()` on ChartData.

Names checked against *Writing code* (no enum suffix the analyzers refuse, no nested type). Existing files the skeleton rewrites:
none whole; SqliteStore.cs gains a region, ServeCommand.cs two registrations.

## Test files

`tests/Vandox.Storage.Tests/TimeSeriesServiceTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreTimeSeriesTests.cs`,
`tests/Vandox.Storage.Tests/SeriesGapTests.cs`, `tests/Vandox.Backend.Tests/GatedTimeSeriesReaderTests.cs`, and the existing
`tests/Vandox.Backend.Tests/ChartDataTests.cs`, `ChartInteropTests.cs`, `ServeTests.cs`. New doubles: `CountingTimeSeriesReader` and a
counting `IRecordReader` (Storage tests); samples in `TimeSeriesSamples.cs` (Storage tests).

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
  as behavior (texts of AC8, ladder, windows, caps), linking records 0214, 0215, 0216 and 0028.
- `docs/areas/storage-reading.md`: *Scope* names time-series reads; new section *Time-series reads* with the seven reads, limits and
  orders (byte order of labels, 1,024 label sets examined, 256 units sought).
- `docs/areas/web-ui.md`: *Charts* — the *between samples* state and the three new error texts; *Store reads of the views* — the
  time-series service shares the gate; *Implementation* — `GatedTimeSeriesReader`.
- `docs/areas/retention.md`, *Rollups*: the readers line adds the time-series query.
- `docs/ARCHITECTURE.md`: backend component — the time-series service the views call in process; *Interfaces* list adds
  `ITimeSeriesReader`; records 0214–0216 linked.
- `docs/BENCHMARKS.md`: section *Time-series query* — criterion (spec N1), workload, `--query` procedure, results table (the
  development-host row by the Dev, the DS918+ row from the Product Manager's run, written in by the Dev).
- `CHANGELOG.md`: an `[Unreleased]` *Added* entry for the time-series query service.
- `.squad/project.md` *Test doubles*: the Tester reports a row for `CountingTimeSeriesReader`; the orchestrator writes it in.

## Architecture check

Record 0028 holds: a span without data is `Gap`, never bridged; *between samples* exists only where another line has a sample and the
line itself has no recorded gap or empty 5-minute span there. Live-only alerting, the agent and ingest are untouched. The store's
four readers keep two free: every new read takes the shared gate per call.

## Security considerations

- [x] No limits on parsed input: the reads parse stored, validated payloads; `json_extract` on a member a forged record lacks gives
  NULL (row skipped as no change); an unparseable `active_enter_at` counts as no move; caps bound every list.
- [x] Exceptions: `InvalidQueryException` (the AC8 texts) and `StoreException` from a failing read reach the calling view, which maps
  them to its own message (#48–#52); `OperationCanceledException` on cancellation. No raw text reaches a user here.

## Decision records

- `docs/decisions/0214-time-series-queries-share-one-resolution-and-one-time-axis-with-bridged-samples.md` (Proposed)
- `docs/decisions/0215-a-service-restart-is-a-rise-of-nrestarts-or-a-new-active-enter-time-of-a-running-or-failed-unit.md` (Proposed)
- `docs/decisions/0216-time-series-context-is-read-in-fixed-windows-around-the-range.md` (Proposed)

Their area link resolves once the Dev adds `docs/areas/time-series-query.md`; `decision-check.py` fails until then.

## Out of scope / follow-ups

See spec *Out of scope*. A view needing log-shipping gaps reported more than seven days late revisits record 0216.
