# Plan: Services and MariaDB view

Source: Issue #52 | [spec.md](spec.md)
Tier: standard — a new read-only page behind the existing login, new store reads, one index migration and a benchmark scenario; it reuses
the login, the address rules and the external-text display without changing any security control.
Security areas touched: 3 (Web UI login: a new page under `Components/Pages/`), 9 (`vandox.db`: schema step 18 adds an index), 10 (parsing of
external input: the `/services` address, `ServicesAddress`; stored agent JSON read by `json_extract`/`json_each`), 12 (display of external
text: unit names and MariaDB thread texts, statements with possible credentials)
Plan size: three page sections over two new store interfaces, a schema step and a benchmark scenario need their signatures and rules here.

## Problem / root cause

Feature: see [spec.md](spec.md), AC1–AC10.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `service_state` follows `retention.events` (1095d), `mariadb_status` `retention.snapshots` (30d) | `docs/areas/retention.md:12-19`; `RetentionConfig.cs:38`; `RetentionPolicy.Snapshots` is `TimeSpan?` (null = unlimited) | confirmed |
| 2 | The overview's "in use" is `$.status.Memory_used` | `OverviewQueries.cs:81` | confirmed |
| 3 | `records_service_state` covers capture time, unit, restarts, enter time, active state, boot ID, kind | `SchemaMigrator.cs:20-29` | confirmed |
| 4 | The existing service stream finds only restart/enter-time changes, not `active_state` changes | `TimeSeriesQueries.cs:96`, `ReadServiceChangesAsync` (`:339`) | confirmed |
| 5 | `ReadServiceChangesAsync` keeps at most 2,001 units in `last`: each unit seen first adds a `found` entry, and the loop ends at the limit 2,001 | `TimeSeriesQueries.cs:29,339-376`; `SeriesMarkerReader.cs:141` | confirmed |
| 6 | `SeriesGap.Concerns` returns false for collector `systemd`; an unknown or empty collector concerns everything | `SeriesGap.cs:64-75` | confirmed |
| 7 | At a step the axis is built from the range, also without values | `SeriesLineReader.cs:118,131` | confirmed |
| 8 | Raw is tried whenever the range lies in the raw period | `SeriesLineReader.cs:21-29` | confirmed |
| 9 | The address default is 24 h and the controls mark *24 h* when unbounded | `AddressTimes.cs:273`; `HistoryRangeControls.razor.cs:54` | confirmed |
| 10 | Every page requires login through `_Imports.razor` (`@attribute [Authorize]`) | `Components/Pages/_Imports.razor` | confirmed |
| 11 | The highest log event ID is 81 | `BackendLog.cs:643` | confirmed |
| 12 | Schema version is 17; tests assert "17" | `docs/areas/storage.md:44`; `SqliteStoreOpenTests.cs:491,550,610,684,740`; `SqliteStoreRetentionTests.cs:514`; `SqliteStoreTimeSeriesTests.cs:1243` | confirmed |
| 13 | The navigation order is asserted | `MainLayoutTests.cs:204` | confirmed |
| 14 | Outside stacked mode the history chart fits each scale (`auto`) | `wwwroot/js/charts.js:314-330`; the spec is fixed | confirmed |
| 15 | `OverviewText.Share` rounds to nearest, `Age` gives `2 h 0 min` and `0 s` | `OverviewText.cs:38-92` | confirmed |
| 16 | `tools/` has no test project; the coverage gate counts only report lines | `coverage-check.py` (`merged_hits`); #51 added `NetworkViewBenchmark.cs` the same way | confirmed |
| 17 | `json_extract`, `json_array_length` are deterministic and usable in an index over expressions; SQLite uses such a partial index as covering only when it holds every column the query reads, `kind` included | record 0217; `SchemaMigrator.cs:16-19` | confirmed |
| 18 | The services, MariaDB and thread actions of a 30-day and 1-year range finish within 10 s on the reference host | to verify: `--services-view` (AC10), Dev on the development host in step 6, Product Manager on the reference host before step 9 | unverified |
| 19 | Migration 18 on 30 days of status records (one in 100 near 1 MiB) takes seconds, not minutes | to verify: Dev times the `CREATE INDEX` in `--services-view` (step 6) and records it in `storage.md` | unverified |
| 20 | The time-series gap read takes gaps of every collector, at most 10 pages of 1,001 gap records, applies the 1,000 cap before any collector filter and merges nothing | `SeriesGapReader.cs:25-64`; `EventPages.cs:13-18` | confirmed |
| 21 | While one unit's state is refused, `systemd` writes one gap per run, from the run's start over one interval, so they touch edge to edge | `docs/areas/agent-services.md:49-53` | confirmed |
| 22 | `Vandox.Core.Model.MariaDbThread` maps the thread keys (`db` → `Db`, `info` → `Info`), `Id` and `TimeSeconds` are `ulong`, defaults are left out when written | `src/Vandox.Core/Model/MariaDbThread.cs:15-71`; `docs/areas/agent-mariadb.md:89-91` | confirmed |
| 23 | `json_extract` of an integer above `long.MaxValue` is a REAL, `CAST … AS INTEGER` gives `long.MaxValue`, an object's text keeps the digits; `TimeSpan.FromSeconds` throws from about 9.22e11 | probe on SQLite 3.53 (Lead); Security's measurement; Storage tests pin it on the bundled SQLite | confirmed |
| 24 | `count(DISTINCT …)` over `records_service_state` builds a temporary b-tree in `/tmp`, a tmpfs in the container | Security's measurement; `deploy/backend/docker-compose.yml:83-86`; precedent `ConnectionRollupQueries.cs:132` | confirmed |
| 25 | The history reads check `at`/`from`/`to` with `at must be within the storable range`, `from must be before to`, `from and to must be within the storable range`; `StorageTime.ToNanoseconds` is unchecked; `SeriesTime.Add` clamps to the storable range | `HistoryQueries.cs:133-148`; `TimeSeriesService.cs:148-149`; `StorageTime.cs:50-53`; `SeriesTime.cs:34-39` | confirmed |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `ServicesAddressParsesRangeFromToAt`, `ServicesAddressRefusesSourceAsUnknownKeyIgnored`, `ServicesAddressDefaultsToThirtyDays`, `ServicesAddressReportsHistoryErrors` (DataRows: repeat, invalid, preset with times, start not before end), `ServicesAddressWritesCheckedValuesOnly` | `tests/Vandox.Backend.Tests/ServicesAddressTests.cs` |
| AC1 | `AddressTimesRangeUsesFallbackPreset` | `tests/Vandox.Backend.Tests/AddressTimesTests.cs` |
| AC1 | `ServicesPagePrerendersHeadingControlsAndLoading`, `ServicesPageMarksThirtyDaysByDefault`, `ServicesPageShowsAddressErrorAndReadsNothing`, `ServicesPageShowsFutureRangeAndReadsNothing`, `ServicesPageSelectionWritesRange`, `ServicesPageClickWritesAtAndReadsOnlyThreads` | `tests/Vandox.Backend.Tests/ServicesPageTests.cs` |
| AC1 | `MainLayoutShowsServicesBetweenNetworkAndLogs`; adapted `MainLayoutShowsNetworkBetweenProcessesAndLogs` | `tests/Vandox.Backend.Tests/MainLayoutTests.cs` |
| AC1 | `RoutesServicesPageRequiresAuthorization` | `tests/Vandox.Backend.Tests/RoutesTests.cs` |
| AC2 | `ServicesTableViewOrdersByRestartsThenNotAvailableThenName`, `ServicesTableViewWritesColumnsAndLatestState`, `ServicesTableViewSaysMoreUnitsNotEvaluated`, `ServicesTableViewShowsNoServiceState`, `ServicesTableViewEscapesUnitNames` | `tests/Vandox.Backend.Tests/ServicesTableViewTests.cs` |
| AC2 | `SqliteStoreServiceAvailabilityEvaluatesFirst256UnitsByName`, `…CountsDistinctUnitsOnlyPast256`, `…CarriesStateFromBeforeTheRange`, `…SkipsUnitWhoseHoldEndsAtFrom` (a unit recorded only at exactly `from − 65 min`, its name sorting first: not listed, not counted; one at `from − 64 min`: listed), `…ReadsLatestStateOfEachUnit`, `…UsesCoveringIndex` (EXPLAIN QUERY PLAN), `…RefusesInvalidQuery` (DataRows: from not before to, from or to outside the storable range), `…ClampsLeadInAtStorableMin` | `tests/Vandox.Storage.Tests/SqliteStoreServiceHistoryTests.cs` |
| AC3 | `ServiceAvailabilityFolderHoldsStateUntilNextRecord`, `…CapsHoldAtOneHourFiveMinutes`, `…CountsGapTimeAsUnknown`, `…CountsTimeAfterGapsCompleteUntilAsUnknown`, `…UnknownBeforeFirstRecord`, `…AvailableStatesAreActiveReloadingRefreshing`, `…DurationsAddUpToRange`, `…EvictsLargestNameBeyond256`, `…ReportsDropped` | `tests/Vandox.Storage.Tests/ServiceAvailabilityFolderTests.cs` |
| AC3 | `UnknownSpansKnownPartsSkipSpansAndCompleteUntil` | `tests/Vandox.Storage.Tests/UnknownSpansTests.cs` |
| AC3, AC5 | `GapSpanSetMergesOverlappingAndTouchingSpans`, `…MergesSpansAddedOutOfOrder`, `…KeepsFirstThousandAndBoundsAtNext`, `…IgnoresSpansAfterBound` | `tests/Vandox.Storage.Tests/GapSpanSetTests.cs` |
| AC3 | `SeriesGapConcernsCollector` (DataRows: `systemd`, `mariadb`, empty, unnamed, another named) | `tests/Vandox.Storage.Tests/SeriesGapTests.cs` |
| AC3, AC5 | `SqliteStoreServiceAvailabilityMergesPerRunSystemdGaps` (2,880 touching `systemd` gap records over a day: one unknown span, `GapsCompleteUntil` null, `SystemdGaps` true), `SqliteStoreMariaDbStatusIgnoresSystemdGaps` (the same records: MariaDB's time fully known), `…ReadsGapRowsAndUnnamedCollectors`, `…SkipsOtherCollectors` | `tests/Vandox.Storage.Tests/SqliteStoreServiceHistoryTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreMariaDbHistoryTests.cs` |
| AC3 | `ServicesViewShareDownNeverShowsHundredWithTimeNotAvailable` (DataRows), `ServicesTableViewShowsSystemdGapNote`, `ServicesTableViewShowsGapCapNote` | `tests/Vandox.Backend.Tests/ServicesTableViewTests.cs` |
| AC4 | `TimeSeriesServiceRestartsMatchQueryMarkers`, `TimeSeriesServiceRestartsCapAtThousandWithCompleteUntil`, `TimeSeriesServiceRestartsUndecidedBootBoundsList` | `tests/Vandox.Storage.Tests/TimeSeriesServiceViewReadsTests.cs` |
| AC4 | `ServicesTableViewCountsRestartsPerUnit`, `ServicesTableViewListsRestartsInTimeOrder`, `ServicesTableViewSaysRestartsNotCounted`, `ServicesReaderReadsRestartsAndAvailability` | `tests/Vandox.Backend.Tests/ServicesTableViewTests.cs`, `tests/Vandox.Backend.Tests/ServicesReaderTests.cs` |
| AC5 | `MariaDbStatusFolderWeighsByTimeCappedAtTwoMinutes`, `…SharesPerStep`, `…GapTimeUnknown`, `…CarriesRecordFromBeforeRange`, `…NoKnownTimeIsGap` | `tests/Vandox.Storage.Tests/MariaDbStatusFolderTests.cs` |
| AC5 | `SqliteStoreMariaDbStatusFoldsOnSteps`, `…ReadsOnlySourceMariadb`, `…ReportsOldest`, `…UsesCoveringIndex`, `…ReadsStatusValueAboveLongMax` (`Memory_used` 18446744073709551615), `…ClampsLeadInAtStorableMin`, `…RefusesInvalidQuery` (DataRows of the texts below) | `tests/Vandox.Storage.Tests/SqliteStoreMariaDbHistoryTests.cs` |
| AC5 | `MariaDbChartViewWritesSummaryRoundedDown`, `…ShowsRetentionNoteOnlyPastSnapshots` (DataRows: unlimited, fresh install, past the cut), `…ShowsNoMariaDbData` (DataRows: no line; only `mem_total_bytes` with values) | `tests/Vandox.Backend.Tests/MariaDbChartViewTests.cs` |
| AC6 | `MariaDbChartViewWritesBudgetLine`, `…SaysMaximumExceedsRam`, `…LeavesComparisonOutWhenValueUnknown`, `…DrawsBudgetLinesInGiB`; `MariaDbStatusFolderAveragesMemoryOfUpRecords` | `tests/Vandox.Backend.Tests/MariaDbChartViewTests.cs`, `tests/Vandox.Storage.Tests/MariaDbStatusFolderTests.cs` |
| AC7 | `MariaDbChartViewChartsShareTimes`, `…WritesStepNoteAndQueryNotes`, `…GapsBreakLines`; `TimeSeriesServiceStepsOnlySkipsRaw`, `TimeSeriesServiceSkipMarkersReadsNoMarkers`; `MariaDbReaderQueriesStepsOnlyWithoutMarkers` | `tests/Vandox.Backend.Tests/MariaDbChartViewTests.cs`, `tests/Vandox.Storage.Tests/TimeSeriesServiceViewReadsTests.cs`, `tests/Vandox.Backend.Tests/MariaDbReaderTests.cs` |
| AC8 | `SqliteStoreThreadSnapshotsNewestFirstWithCounts`, `…ClosestWithinWindowPrefersEarlierOnTie`, `…ClosestClampsWindowAtStorableMin`, `…ThreadRowsSkipSleepInAgentOrder`, `…ThreadRowsMapDbAndInfoAndKeepFullIds` (`id` and `time_seconds` 18446744073709551615, absent `user`/`db`/`info`/`truncated`), `…StampClampsLongestSeconds` (time_seconds 18446744073709551615 → `MaxSeconds`), `…CapsRowsAt101`, `…RefusesInvalidQuery` (DataRows: limit, window, at outside the storable range, from not before to, from or to outside the storable range), `…ThreadRowThatDoesNotParseIsStoreError`; `ThreadSnapshotViewWritesNoteListAndPanel`, `…MarksShortenedStatement`, `…SaysSleepingRecorded`, `…EscapesThreadTexts`, `…ShowsNoSnapshotTexts`, `…WritesTimeAtCapAsOrMore` (DataRows: thread cell and *Longest running* at 18446744073709551615 and at `MaxSeconds`, `MaxSeconds − 1` as `Age`); `ThreadSnapshotReaderUsesLatestWithoutAt`, `…UsesClosestWithAt` | `tests/Vandox.Storage.Tests/SqliteStoreMariaDbHistoryTests.cs`, `tests/Vandox.Backend.Tests/ThreadSnapshotViewTests.cs`, `tests/Vandox.Backend.Tests/ThreadSnapshotReaderTests.cs` |
| AC9 | `ServicesPageSectionsFailAlone` (DataRows: timeout and store error per section, texts of E12/E13), `ServicesPageLogsOnlyException`, `ServicesPageLeavingCancelsActions`; `GatedServiceHistoryReaderTakesOneSlotPerCall`, `GatedMariaDbHistoryReaderTakesOneSlotPerCall` | `tests/Vandox.Backend.Tests/ServicesPageTests.cs`, `tests/Vandox.Backend.Tests/GatedServiceHistoryReaderTests.cs`, `tests/Vandox.Backend.Tests/GatedMariaDbHistoryReaderTests.cs` |
| AC9 | `SqliteStoreOpenMigratesToVersion18WithMariaDbStatusIndex`; adapted version asserts | `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` |
| AC10 | manual: Dev runs `--services-view` (workload below, 256 units included) on the development host in step 6 (order of magnitude, `docs/BENCHMARKS.md`); the Product Manager runs it on the reference host before step 9 (the orchestrator asks, *Criteria verified only in CI*) | `tools/Vandox.StorageBenchmark/ServicesViewBenchmark.cs` |

## Approach

**Page** `/services` (`ServicesPage`), heading `Services and MariaDB`, nav *Services* (`services`) between *Network* and *Logs*.
Layout, top to bottom: the range controls (`HistoryRangeControls` with `DefaultPreset="ThirtyDays"`), the address error or the future
text; `<h2>Services</h2>`, its notes as paragraphs, the table *Services* (`HistorySnapshotTable`), the more-units line, `<h3>Service
restarts</h3>` with a table *Time*, *Unit*; `<h2>MariaDB</h2>`, its notes, the summary paragraph, `<h3>Availability</h3>` chart,
`<h3>Memory budget</h3>` with the budget paragraph, the exceed paragraph (only when it applies) and chart, `<h3>Connections</h3>`,
`<h3>Queries per second</h3>`, `<h3>InnoDB buffer pool</h3>` charts; `<h2>Long-running queries</h2>`, the agent note, `<h3>Snapshots</h3>`
table whose *Time* cell is a button, `<h3>Threads of <time> UTC</h3>`, the thread table, the sleeping line. Each section shows
`Loading…` until its action ends. Notes of a section appear in the order of the spec's edge cases (E3 note, E15, E5; E6, E7, E15, the
time-series notes, the step text). Tables: unit and thread cells through `SnapshotCells.Runs` (256 characters, 16 runs), the statement
through `LogText.Runs(text, 2048, 256)` followed by `[statement shortened by the agent]` when `truncated`. Times `yyyy-MM-dd HH:mm:ss`,
durations `OverviewText.Age`; a thread's *Time* and *Longest running* `ServicesView.Seconds`: `OverviewText.Age` below
`ThreadSnapshot.MaxSeconds` (3,153,600,000), else `36500 d 0 h or more`; sizes `OverviewText.Size`, shares `ServicesView.ShareDown` (floor of 1000 × part / total, over 10, `0.0 %`).

**Address** (`ServicesAddress`): `AddressTimes` for `range`, `from`, `to`, `at` with the history view's texts; any other key is ignored;
`Range(now)` uses `AddressTimes.Range(now, HistoryPreset.ThirtyDays)`. A change of range, preset, `from` or `to` reruns all three
actions; a change of `at` only the thread action. Drag selection: new history entry with the range, `at` kept; click: replaces the
entry, `at` = time under the pointer floored to the second (`HistoryView.Pick` with no markers); the snapshot button replaces the entry
with `at` = its capture time floored. Charts draw the moment line via `HistoryChartView.WithMoment`.

**Actions** (three `PageRunner`s, each `CancellationTokenSource` with 10 s including the gate wait; errors E12/E13 in the section,
`BackendLog.ServicesReadFailed` (event 82, error) with the exception only; a superseded action ends silently):

1. *Services action* (`ServicesReader.ReadAsync`): end = min(range end, now); `TimeSeriesService.RestartsAsync(from, end)`, then
   `ServiceAvailabilityAsync(from, end)` (its own gaps); `ServicesTableView.Build` sorts and texts; E3's note when `SystemdGaps`, E15
   when `GapsCompleteUntil`.
2. *MariaDB action* (`MariaDbReader.ReadAsync`): `TimeSeriesService.QueryAsync` of `mariadb/mariadb_memory_max_bytes`,
   `system/mem_total_bytes`, `mariadb/mariadb_questions_per_second`, `mariadb/mariadb_innodb_buffer_pool_used_percent`,
   `mariadb/mariadb_innodb_buffer_pool_read_hit_percent` with `StepsOnly` and `SkipMarkers`, target `HistoryView.Target(width)`; then
   `MariaDbStatusAsync` at the result's step (its own `mariadb` gaps; E15 from its `GapsCompleteUntil`; the metric lines keep the
   query's gaps and notes, E9). The status times equal the query's `Times` (multiples of the step since the epoch overlapping the range).
   `MariaDbChartView.Build` writes: summary `Up <s>, down <s>, not answering <s>, unknown <s> of this range.` (shares of the range up to
   now); budget `In use <size> of a theoretical maximum of <size>; RAM <size>.` from the latest `In use` of the range and the `Last` of
   the latest value point of the two metric lines (`unknown` each when missing); exceed `The theoretical maximum exceeds the RAM by
   <size>.` only with both values and max > RAM. Memory lines in GiB (factor 1/2^30, axis `GiB`), one scale for the three; *Connections*
   axis `Connections`; *Queries per second* axis `Queries/s`; *InnoDB buffer pool* lines `Used`, `Read hit` on one scale, axis `%`;
   *Availability* line `Up`, axis `%`. E7 only when `policy.Snapshots` is set and `now - Snapshots > from`, with `Oldest`; E6 when
   `Records == 0` and no line of source `mariadb` has a value (not `mem_total_bytes`). Starts-before-rollups and values-not-complete notes reuse `HistoryView` texts.
3. *Thread action* (`ThreadSnapshotReader.ReadAsync`): `ThreadSnapshotsAsync` (range, limit 51); without `at` the first stamp,
   with `at` `ClosestThreadSnapshotAsync(at, HistoryView.MomentWindow)`; then `ThreadSnapshotAsync(id)`. At most three store calls.

**Store reads** (`SqliteStore`, one reader lease per call, constant statements with bound parameters, context `store: reading
services` / `store: reading mariadb status`). Each read first validates with the texts of fact 25 (`from must be before to`, `from
and to must be within the storable range`, `at must be within the storable range`); every lead-in and window edge goes through
`SeriesTime.Add`, clamped to the storable range.

- *Collector gaps* (`CollectorGapReader`, record 0227, inside the availability and status reads' lease): stream, without pages, the
  non-closed `gaps` rows overlapping `[from, to)` (no collector: they concern both) and the gap records captured in `[from − 1 h, to +
  7 d)` (projection as `GapRecords`); skip a record whose span does not parse or misses the range, or for which
  `SeriesGap.ConcernsCollector(collector)` is false. Each span, clipped to `[from, to)`, goes into `GapSpanSet`: sorted, merging
  overlapping and touching spans on insert in any order; past 1,000 spans it drops the latest-starting one, lowers `CompleteUntil` to
  its start and ignores spans from there on. `Named`: a kept record names exactly the collector (E3's note).
- *Service availability*: one stream over `records_service_state` of `(from − 65 min, to)` (start excluded, so each streamed record
  lies in the range or carries its state into it) in capture order with ID, unit, active state (the index's expressions), into
  `ServiceAvailabilityFolder`; only when the folder `Dropped` a name, `SELECT count(DISTINCT …unit…)` over the same window and index
  gives `UnitCount`; then the latest records' `active_state`, `sub_state` and capture time by ID (`json_each($ids)`, at most 256).
  Folder: a record's state holds `[t, min(next t, t + 65 min))`, clipped to `[from, to)`; `UnknownSpans.KnownParts` removes the gap
  spans and `[GapsCompleteUntil, to)`; `active`, `reloading`, `refreshing` are available; unknown = range − available − not available.
  At most 256 units in a sorted map; a new name above the largest kept is skipped, below it evicts the largest (an evicted name never
  returns: the largest kept name only falls).
- *MariaDB status*: index `records_mariadb_status`; stream `[from − 2 min, to)`, `source = 'mariadb'`, into `MariaDbStatusFolder`: a
  record holds `[t, min(next t, t + 2 min))` with its availability, apportioned to steps over the known parts; per step `Up` = up time
  / known time × 100 (gap without known time); `In use` and `Connections` min/avg/max of the up records of the step, read with
  `GetDouble` (above `long.MaxValue` a REAL); totals; latest `Memory_used`. `Oldest` = `min(captured_at)` through the index. Also
  `step must be positive`, `at most 2001 steps`.
- *Thread snapshots*: `json_array_length(data, '$.threads') > 0` through the index, newest first by capture time and ID, `LIMIT $limit`
  (else `limit must be 1 to 51`); per stamp `Running` and `LongestSeconds` = `min(max(CAST(json_extract(value, '$.time_seconds') AS
  INTEGER)), $maxSeconds)` over `json_each` of its `threads` where `command <> 'Sleep'`. *Closest*: the latest at or before `at` and
  the earliest after, each within the window, the nearer, the earlier on a tie, the lower ID within one capture time (else `window must
  be positive and at most one day`). *Thread rows* (record 0228): `json_each(data, '$.threads')` of one record in array order, `command`
  not `Sleep` (absent: empty), `LIMIT 101`, each `value` (the object's JSON text) deserialized with `PayloadRegistry.Options` into
  `Vandox.Core.Model.MariaDbThread`; a row that does not deserialize is a `StoreException` (E13); plus counts of threads, running,
  sleeping.
- **Bounds and their seams**: no decoded record is held in C# (one thread object of about 17 KiB at a time; Storage tests assert the
  101-row and 51-stamp caps), at most 256 unit accumulators (`ServiceAvailabilityFolderTests`, 300 names), 1,001 gap spans per read
  (`GapSpanSetTests`), 2,001 steps. The distinct count, past 256 units only, keeps a temporary b-tree of the window's unit names in
  SQLite temporary storage (`/tmp`, a tmpfs, fact 24), bounded by the 10 s interrupt, not the 256 cap; the area document says so.

**Schema step 18**: `CREATE INDEX records_mariadb_status ON records(captured_at, source, json_extract(data, '$.availability'),
json_extract(data, '$.status.Memory_used'), json_extract(data, '$.status.Threads_connected'), json_array_length(data, '$.threads'), kind)
WHERE kind = 'mariadb_status'`; the reads name exactly these expressions with `INDEXED BY`.

**Time-series service**: `TimeSeriesQuery.StepsOnly` skips `TryRawAsync`; `SkipMarkers` returns no markers and no
`MarkersCompleteUntil`. `RestartsAsync` validates `from < to` and the storable range with the query's texts, clamps the end to now,
returns empty for a future range, and reuses `SeriesMarkerReader.ReadRestartsOnlyAsync` (restart checks with their own boot read,
cap 1,000, the same `CompleteUntil` rule).

**Benchmark** `--services-view` (`ServicesViewBenchmark`): fixed clock 2026-12-01 12:00 UTC, default periods; written by SQL into a
fresh database: 86,400 `mariadb_status` records of 30 days at 30 s, every 100th with 60 threads of 16 KiB statements (about 1 MiB),
the five metrics of 30 days raw at 30 s plus 365 days of hourly rollups, `mem_total_bytes` likewise, 256 units (the agent's cap) of
hourly `service_state` records over 365 days (about 2.24 million) with a restart per unit per day, and a refused unit's `systemd` gap
per 30 s run over the last 30 days (86,400 gap records); times the `CREATE INDEX` of step 18 once (dropped and rebuilt), then each
action for 30 days and 1 year, median of five, and prints the verdict against 10,000 ms. `docs/BENCHMARKS.md` and the area document
state the results and whether E12 is expected at 256 units; over 10 s on the development host goes back to the Lead.

Early draft PR: no

## Options considered

- One page or a page each for services and MariaDB: one page, the issue names one view and MariaDB is a tracked service.
- Availability by record share or by systemd accounting: rejected (record 0227).
- Status values by `json_extract` on payloads, by rollups or agent metrics, or at raw resolution: rejected (record 0228).
- Restarts: a new restart rule for the view, or the time-series query's markers through a full `QueryAsync`: rejected; a restart-only
  entry point reuses the one rule (record 0215) without a series.
- Evaluating every distinct unit: rejected (record 0227, MAJOR 2 of the spec challenge).
- A JS option for a scale from 0: rejected, no browser-script change for a view whose lines are not stacked (spec fix).
- Gaps from the time-series query's gap list, a collector filter in SQL, a merge in SQL: rejected (record 0227).
- Thread rows as SQL columns into a new storage type: rejected (record 0228).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `TimeSeriesQuery`, `TimeSeriesService`, `SeriesLineReader`, `SeriesMarkerReader`, `SeriesGap` | steps only, skip markers, restarts entry point, `ConcernsCollector` |
| Vandox.Storage | new `SeriesMarkerList`, `IServiceHistoryReader`, `ServiceAvailabilityQuery`, `ServiceAvailabilityResult`, `ServiceAvailability`, `IMariaDbHistoryReader`, `MariaDbStatusQuery`, `MariaDbStatusResult`, `ThreadSnapshotQuery`, `ThreadSnapshotStamp`, `ThreadSnapshot`; internal `GapSpanSet`, `CollectorGapReader`, `UnknownSpans`, `ServiceAvailabilityFolder`, `MariaDbStatusFolder`, `ServiceHistoryQueries`, `MariaDbHistoryQueries` | new reads |
| Vandox.Storage | `SqliteStore`, `SchemaMigrator` | implements both interfaces; step 18 |
| Vandox.Backend | new folder `ServiceHealth/` (namespace `Vandox.Backend.ServiceHealth`, not `Services`, which would shadow `builder.Services` lookups) | address, readers, views, gated readers |
| Vandox.Backend | `Components/Pages/ServicesPage.razor(.cs)`, `MainLayout.razor`, `HistoryRangeControls.razor.cs`, `History/AddressTimes.cs`, `History/HistoryChartView.cs`, `Hosting/BackendLog.cs`, `Hosting/ServeCommand.cs` | page, nav, default preset, shared line builder, event 82, registrations |
| tools | `Vandox.StorageBenchmark/ServicesViewBenchmark.cs`, `Program.cs` | `--services-view` |

## Signatures (for the Dev's skeleton)

One backticked declaration per member; Type.Member names a property or constant, its type in the note. All new types
use init-only properties and XML documentation, the regions by member kind.

Vandox.Storage:

- `TimeSeriesQuery.StepsOnly` — bool, init; true skips raw resolution
- `TimeSeriesQuery.SkipMarkers` — bool, init; true reads no markers
- `public Task<SeriesMarkerList> RestartsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)` — on TimeSeriesService
- `class SeriesMarkerList` — public sealed; Markers (IReadOnlyList of SeriesMarker, = []), CompleteUntil (DateTimeOffset?)
- `SeriesMarkerList.Markers`
- `SeriesMarkerList.CompleteUntil`
- `public bool ConcernsCollector(string collector)` — on SeriesGap
- `internal static Task<SeriesData> ReadAsync(ITimeSeriesReader reader, RetentionPolicy policy, IReadOnlyList<SeriesKey> series, SeriesRange range, bool stepsOnly, CancellationToken cancellationToken)` — SeriesLineReader; replaces the five-parameter overload
- `internal static Task<(List<SeriesMarker> Markers, DateTimeOffset? CompleteUntil)> ReadRestartsOnlyAsync(ITimeSeriesReader reader, SeriesRange range, CancellationToken cancellationToken)` — SeriesMarkerReader
- `interface IServiceHistoryReader` — public
- `Task<ServiceAvailabilityResult> ServiceAvailabilityAsync(ServiceAvailabilityQuery query, CancellationToken cancellationToken)` — IServiceHistoryReader
- `class ServiceAvailabilityQuery` — public sealed; From, To (DateTimeOffset)
- `ServiceAvailabilityQuery.From`
- `ServiceAvailabilityQuery.To`
- `class ServiceAvailabilityResult` — public sealed; Units (IReadOnlyList of ServiceAvailability), UnitCount (int, distinct units of the window), GapsCompleteUntil (DateTimeOffset?), SystemdGaps (bool)
- `ServiceAvailabilityResult.Units`
- `ServiceAvailabilityResult.UnitCount`
- `ServiceAvailabilityResult.GapsCompleteUntil`
- `ServiceAvailabilityResult.SystemdGaps`
- `class ServiceAvailability` — public sealed; Unit, ActiveState, SubState (string), Available, NotAvailable, Unknown (TimeSpan), StateAt (DateTimeOffset?)
- `ServiceAvailability.Unit`
- `ServiceAvailability.Available`
- `ServiceAvailability.NotAvailable`
- `ServiceAvailability.Unknown`
- `ServiceAvailability.ActiveState`
- `ServiceAvailability.SubState`
- `ServiceAvailability.StateAt`
- `interface IMariaDbHistoryReader` — public
- `Task<MariaDbStatusResult> MariaDbStatusAsync(MariaDbStatusQuery query, CancellationToken cancellationToken)` — IMariaDbHistoryReader
- `Task<IReadOnlyList<ThreadSnapshotStamp>> ThreadSnapshotsAsync(ThreadSnapshotQuery query, CancellationToken cancellationToken)` — IMariaDbHistoryReader, newest first
- `Task<ThreadSnapshotStamp?> ClosestThreadSnapshotAsync(DateTimeOffset at, TimeSpan window, CancellationToken cancellationToken)` — IMariaDbHistoryReader
- `Task<ThreadSnapshot?> ThreadSnapshotAsync(long recordId, CancellationToken cancellationToken)` — IMariaDbHistoryReader
- `class MariaDbStatusQuery` — public sealed; From, To (DateTimeOffset), Step (TimeSpan)
- `MariaDbStatusQuery.From`
- `MariaDbStatusQuery.To`
- `MariaDbStatusQuery.Step`
- `class MariaDbStatusResult` — public sealed; Times (IReadOnlyList of DateTimeOffset), Up, Memory, Connections (IReadOnlyList of SeriesPoint, one per time), UpTime, DownTime, NotAnsweringTime, UnknownTime (TimeSpan), LatestMemory (double?), LatestMemoryAt, Oldest, GapsCompleteUntil (DateTimeOffset?), Records (int)
- `MariaDbStatusResult.Times`
- `MariaDbStatusResult.Up`
- `MariaDbStatusResult.Memory`
- `MariaDbStatusResult.Connections`
- `MariaDbStatusResult.UpTime`
- `MariaDbStatusResult.DownTime`
- `MariaDbStatusResult.NotAnsweringTime`
- `MariaDbStatusResult.UnknownTime`
- `MariaDbStatusResult.LatestMemory`
- `MariaDbStatusResult.LatestMemoryAt`
- `MariaDbStatusResult.Oldest`
- `MariaDbStatusResult.GapsCompleteUntil`
- `MariaDbStatusResult.Records`
- `class ThreadSnapshotQuery` — public sealed; From, To (DateTimeOffset), Limit (int), const MaxLimit = 51
- `ThreadSnapshotQuery.From`
- `ThreadSnapshotQuery.To`
- `ThreadSnapshotQuery.Limit`
- `ThreadSnapshotQuery.MaxLimit`
- `class ThreadSnapshotStamp` — public sealed; RecordId (long), CapturedAt (DateTimeOffset), Threads, Running (int), LongestSeconds (long?)
- `ThreadSnapshotStamp.RecordId`
- `ThreadSnapshotStamp.CapturedAt`
- `ThreadSnapshotStamp.Threads`
- `ThreadSnapshotStamp.Running`
- `ThreadSnapshotStamp.LongestSeconds`
- `class ThreadSnapshot` — public sealed; CapturedAt (DateTimeOffset), Threads, Running, Sleeping (int), Rows (IReadOnlyList of Vandox.Core.Model.MariaDbThread, = []), const MaxRows = 101, const MaxSeconds = 3153600000 (long)
- `ThreadSnapshot.CapturedAt`
- `ThreadSnapshot.Sleeping`
- `ThreadSnapshot.Rows`
- `ThreadSnapshot.MaxRows`
- `ThreadSnapshot.MaxSeconds`
- `public bool ConcernsCollector(string collector)` — on SeriesGap; also the filter of `CollectorGapReader`
- `class GapSpanSet` — internal sealed; const MaxSpans = 1000
- `GapSpanSet.MaxSpans`
- `internal void Add(DateTimeOffset from, DateTimeOffset to)` — GapSpanSet
- `GapSpanSet.Spans` — internal IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)>, get; merged, by start
- `GapSpanSet.CompleteUntil` — internal DateTimeOffset?, get
- `class CollectorGapReader` — internal static
- `internal static Task<(GapSpanSet Gaps, bool Named)> ReadAsync(SqliteConnection connection, string collector, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)` — CollectorGapReader
- `class UnknownSpans` — internal sealed
- `internal UnknownSpans(IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> spans, DateTimeOffset? knownUntil, DateTimeOffset end)`
- `internal IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> KnownParts(DateTimeOffset from, DateTimeOffset to)`
- `class ServiceAvailabilityFolder` — internal sealed; const MaxUnits = 256, static readonly Hold = 65 minutes
- `ServiceAvailabilityFolder.MaxUnits`
- `ServiceAvailabilityFolder.Hold`
- `ServiceAvailabilityFolder.Dropped` — internal bool, get; a name was skipped or evicted
- `internal ServiceAvailabilityFolder(DateTimeOffset from, DateTimeOffset to, UnknownSpans unknown)`
- `internal void Add(long id, DateTimeOffset capturedAt, string unit, string activeState)` — ServiceAvailabilityFolder
- `internal IReadOnlyList<(ServiceAvailability Row, long LastId)> Finish()` — ServiceAvailabilityFolder
- `class MariaDbStatusFolder` — internal sealed; static readonly Hold = 2 minutes
- `MariaDbStatusFolder.Hold`
- `internal MariaDbStatusFolder(MariaDbStatusQuery query, UnknownSpans unknown)`
- `internal void Add(DateTimeOffset capturedAt, string availability, double? memoryUsed, double? threadsConnected)` — MariaDbStatusFolder
- `internal MariaDbStatusResult Finish(DateTimeOffset? oldest)` — MariaDbStatusFolder
- `SchemaMigrator.MariaDbStatusIndexStatement` — internal const string, the statement of step 18
- `class SqliteStore` — implements IServiceHistoryReader and IMariaDbHistoryReader in addition

Vandox.Backend (namespace Vandox.Backend.ServiceHealth except the members of existing types):

- `internal HistoryRange Range(DateTimeOffset now, HistoryPreset fallback)` — AddressTimes; the one-parameter Range calls it with the one-day preset
- `HistoryRangeControls.DefaultPreset` — [Parameter], HistoryPreset, default the one-day preset
- `internal static HistoryLine Line(string label, int scale, int index, IReadOnlyList<SeriesPoint> points, bool step, double factor)` — HistoryChartView; its private line builder calls it with factor 1
- `class ServicesAddress` — public sealed
- `public static ServicesAddress Parse(string query)`
- `ServicesAddress.Error` — string, get and set
- `ServicesAddress.Times` — internal AddressTimes, get
- `public ServicesAddress Copy()`
- `public HistoryRange Range(DateTimeOffset now)` — ServicesAddress
- `public string ToQuery()` — ServicesAddress
- `public bool SameRange(ServicesAddress other)`
- `class ServicesView` — public static; the texts of the spec as public const string (E3, E5 to E7, E10, E12 to E15, the AC8 note, headings)
- `public static string ShareDown(TimeSpan part, TimeSpan total)` — ServicesView
- `public static string Seconds(ulong seconds)` — ServicesView; the *Time* and *Longest running* cells, capped at `ThreadSnapshot.MaxSeconds`
- `class ServicesReader` — public sealed
- `public ServicesReader(TimeSeriesService series, IServiceHistoryReader reader, TimeProvider clock)`
- `public Task<ServicesTable> ReadAsync(HistoryRange range, CancellationToken cancellationToken)` — ServicesReader
- `class ServicesTable` — public sealed; Units, Restarts (SnapshotTableView), Notes (IReadOnlyList of string), Empty (bool)
- `ServicesTable.Units`
- `ServicesTable.Restarts`
- `ServicesTable.Notes`
- `ServicesTable.Empty`
- `class ServicesTableView` — public static
- `public static ServicesTable Build(ServiceAvailabilityResult availability, SeriesMarkerList restarts, HistoryRange range)` — ServicesTableView; the range end already clamped to now
- `class MariaDbReader` — public sealed
- `public MariaDbReader(TimeSeriesService series, IMariaDbHistoryReader reader, RetentionPolicy policy, TimeProvider clock)`
- `public Task<MariaDbCharts> ReadAsync(HistoryRange range, double chartWidth, CancellationToken cancellationToken)` — MariaDbReader
- `class MariaDbCharts` — public sealed; Notes (IReadOnlyList of string), Summary, Budget, BudgetExceeded (string), Availability, Memory, Connections, Queries, BufferPool (HistoryChartView?)
- `MariaDbCharts.Notes`
- `MariaDbCharts.Summary`
- `MariaDbCharts.Budget`
- `MariaDbCharts.BudgetExceeded`
- `MariaDbCharts.Availability`
- `MariaDbCharts.Memory`
- `MariaDbCharts.Connections`
- `MariaDbCharts.Queries`
- `MariaDbCharts.BufferPool`
- `class MariaDbChartView` — public static
- `public static MariaDbCharts Build(TimeSeriesResult series, MariaDbStatusResult status, HistoryRange range, TimeSpan? snapshots, DateTimeOffset now)` — MariaDbChartView
- `class ThreadSnapshotReader` — public sealed
- `public ThreadSnapshotReader(IMariaDbHistoryReader reader)`
- `public Task<ThreadPanel> ReadAsync(HistoryRange range, DateTimeOffset? at, CancellationToken cancellationToken)` — ThreadSnapshotReader
- `class ThreadPanel` — public sealed; Stamps (IReadOnlyList of ThreadStampRow), More, Heading, Sleeping (string), Threads (SnapshotTableView?), Notes (IReadOnlyList of string)
- `ThreadPanel.Stamps`
- `ThreadPanel.More`
- `ThreadPanel.Heading`
- `ThreadPanel.Threads`
- `ThreadPanel.Sleeping`
- `ThreadPanel.Notes`
- `class ThreadStampRow` — public sealed; At (DateTimeOffset), Time, Threads, Running, Longest (string)
- `ThreadStampRow.At`
- `ThreadStampRow.Time`
- `ThreadStampRow.Threads`
- `ThreadStampRow.Running`
- `ThreadStampRow.Longest`
- `class ThreadSnapshotView` — public static
- `public static ThreadPanel Build(IReadOnlyList<ThreadSnapshotStamp> stamps, ThreadSnapshot? shown, DateTimeOffset? at)` — ThreadSnapshotView
- `class GatedServiceHistoryReader` — public sealed, implements IServiceHistoryReader
- `public GatedServiceHistoryReader(IServiceHistoryReader inner, WebReaderGate gate)`
- `class GatedMariaDbHistoryReader` — public sealed, implements IMariaDbHistoryReader
- `public GatedMariaDbHistoryReader(IMariaDbHistoryReader inner, WebReaderGate gate)`
- `internal static partial void ServicesReadFailed(this ILogger logger, Exception exception)` — BackendLog, event 82, error
- `class ServicesPage` — the page, route /services

The skeleton holds for real: the route /services, the nav entry, the registrations in ServeCommand, schema step 18,
`ThreadSnapshot.MaxSeconds` and the ConcernsCollector switch; the Tester defers nothing else.

## Test files

New: `tests/Vandox.Storage.Tests/{UnknownSpansTests, GapSpanSetTests, ServiceAvailabilityFolderTests, MariaDbStatusFolderTests, SqliteStoreServiceHistoryTests, SqliteStoreMariaDbHistoryTests, TimeSeriesServiceViewReadsTests}.cs`;
`tests/Vandox.Backend.Tests/{ServicesAddressTests, ServicesTableViewTests, ServicesReaderTests, MariaDbChartViewTests, MariaDbReaderTests, ThreadSnapshotViewTests, ThreadSnapshotReaderTests, GatedServiceHistoryReaderTests, GatedMariaDbHistoryReaderTests, ServicesPageTests}.cs`,
helpers `ServicesPageHarness.cs`, `ServicesSamples.cs`, `FakeServiceHistoryReader.cs`, `FakeMariaDbHistoryReader.cs`. Extended: `SeriesGapTests.cs`,
`SqliteStoreOpenTests.cs`, `AddressTimesTests.cs`, `MainLayoutTests.cs`, `RoutesTests.cs`.

Existing call sites (Tester adapts): the schema version `"17"` asserts (fact 12) become `"18"`; the navigation order at
`MainLayoutTests.cs:204` gains `Services` after `Network`. `SeriesLineReader.ReadAsync` has its only caller in `TimeSeriesService`
(Dev). No other test calls a changed signature: the new `TimeSeriesQuery` properties default to today's behavior, so the existing
`TimeSeriesServiceTests` stay unchanged.

Files no unit test can cover: `tools/Vandox.StorageBenchmark/ServicesViewBenchmark.cs` and its `Program.cs` switch, a developer tool
without a test project and outside the coverage report (fact 16), run by hand for AC10. No exclusion is needed.

## Areas

- `docs/areas/web-ui-services-view.md` (new, about +2,400 words): area *Services view*, scope "The page `/services`: the address with
  range and moment, the table of the services with availability and restarts, MariaDB's availability, memory budget and charts, the
  thread snapshots, notes, calls, the 10-second deadline, caps and memory per circuit." Index row in `docs/areas/README.md` (+60 words).
- `docs/areas/web-ui.md` (3,402, +70 words): navigation entry, the view in the gate sentence and list, chart users.
- `docs/areas/storage-reading.md` (3,460, +420 words): section *Services view reads* (the four reads, the collector gaps with their
  merge and cap, limits, errors, the distinct count's temporary storage).
- `docs/areas/storage.md` (4,192, +170 words): version 18, the index in the index list, step 18 with write cost and build time.
- `docs/areas/time-series-query.md` (2,087, +90 words): the view as a second caller, `StepsOnly`, `SkipMarkers`, the restarts
  entry point.

## Documentation updates

All by the Dev, after the code is green:

- the five area documents above; `docs/BENCHMARKS.md` section *Services view* (procedure, results table with the reference-host row);
- `docs/ARCHITECTURE.md`: the services view in the list of line 10, a paragraph after the network view's (around line 150) linking
  0227, 0228 and the area, the two interfaces in the store list (line 499);
- `README.md`: a section *Services and MariaDB* after the network view's (around line 423), as that one is written;
- `CHANGELOG.md`: an entry *Services and MariaDB view* (issue #52) under the unreleased version, plus the schema step 18;
- `.squad/project.md (+6 words)`: security area 10 adds `/services` (`ServicesAddress`), area 12 adds `ServicesPage` (reported text,
  written by the orchestrator);
- `docs/UNIT_TESTS.md (Test doubles +2 words)`: `FakeServiceHistoryReader`, `FakeMariaDbHistoryReader` in the *database (C#)* row (Tester);
- decision records 0227 and 0228: `Area:` becomes `Services view` once the area row exists (Lead).

## Architecture check

No guarantee is weakened. *No data gaps unless explicitly recorded*: gap time and time past a hold cap are unknown, never available
(record 0227). *Backfilled data never raises an alert*: the view raises none. The view only reads; the agent and the wire format are
unchanged. `ARCHITECTURE.md` gains the view and its two store interfaces (owner Dev).

## Security considerations

- [x] Limits: the address values are checked on their bytes by `AddressTimes` (unchanged); the display caps (256/2,048 characters) count
  UTF-16 characters of the displayed, escaped text after decoding (`LogText`); the store caps (256 units, 51 stamps, 101 rows, 2,001
  steps, 1,000 merged gap spans) count items, not bytes; a thread's seconds are capped at `ThreadSnapshot.MaxSeconds` before they
  become a `TimeSpan`, so no stored value throws while formatting; statement texts are bounded by the agent at 16,384 bytes and reach no log.
- [x] Exceptions reaching the user: `OperationCanceledException` past the deadline shows E12; `StoreException` and
  `InvalidQueryException` show E13 (a thread row that does not deserialize is a `StoreException`); neither message is shown, only logged (event 82, exception only). Others go to the framework's
  error banner, as on the other views.
- Statements may contain credentials; they are shown only behind the login, as `deploy/agent/README.md` already says, never logged.

## Decision records

- `docs/decisions/0227-availability-is-the-time-a-recorded-state-holds-up-to-a-cap-and-gaps-are-unknown.md` (Proposed)
- `docs/decisions/0228-mariadb-status-members-are-indexed-and-folded-on-the-time-series-step-axis.md` (Proposed)

## Out of scope / follow-ups

- Spec *Out of scope*, with the two follow-up issues of the spec step (threads on a long query; status values beyond
  `retention.snapshots`).

## Challenge

- MAJOR 1 (the shared gap cap breaks E15; unrelated gaps blank MariaDB) — accepted: the services and MariaDB reads each read their own
  gaps inside their lease (*Collector gaps*): only gaps concerning their collector (`ConcernsCollector`), merged when overlapping or
  touching before the 1,000 cap, streamed without the page limit, so `GapsCompleteUntil` comes from merged spans of the one collector.
  `TimeSeriesService.GapsAsync`, `SeriesGapList` and the queries' `Gaps`/`KnownUntil` are gone; facts 20, 21; tests
  `SqliteStoreServiceAvailabilityMergesPerRunSystemdGaps`, `SqliteStoreMariaDbStatusIgnoresSystemdGaps`, `GapSpanSetTests`; the
  benchmark writes a month of per-run gaps. Spec E15 now says "merged gap spans".
- MINOR 2 (fact 5 wrong) — accepted: fact 5 corrected (at most 2,001 units), the follow-up issue dropped.
- MINOR 3 (E6 never fires beside `mem_total_bytes`) — accepted: E6 counts only lines of source `mariadb`; a DataRow with only
  `mem_total_bytes` values.
- MINOR 4 (lead-in-only units) — accepted: the stream and the count exclude the window's start, `(from − 65 min, to)`: a record after
  that start holds past `from` or is followed by one that does, so every streamed unit has a record in the range or a state carried in,
  and the count uses the same predicate. Test `…SkipsUnitWhoseHoldEndsAtFrom`.
- MINOR 5 (benchmark cannot show 256 units) — accepted: the workload has 256 hourly units over a year (about 2.24 million records); the
  results and whether E12 is expected at that cap go into `docs/BENCHMARKS.md` and the area document; the distinct count now runs only
  past 256 units, which removes one pass in the normal case. AC10 stays.
- MINOR 6 (thread keys, values above `long.MaxValue`) — accepted: the thread rows deserialize into the existing
  `Vandox.Core.Model.MariaDbThread` (`db`, `info`, exact `ulong` `id`, `time_seconds`; fact 22), the planned storage type is dropped
  (it would also have shadowed the Core type), status values read as double; tests with 18446744073709551615.

## Security review

- 1 [BLOCKING] hostile `time_seconds` crashes the page — resolved: thread rows keep the exact `ulong`; the stamp's `LongestSeconds` is
  clamped in SQL to `ThreadSnapshot.MaxSeconds` (3,153,600,000); `ServicesView.Seconds` formats a value at or above it as
  `36500 d 0 h or more` and only smaller ones through `TimeSpan`; a thread object that does not deserialize is a `StoreException` (E13).
  DataRows with 18446744073709551615 in `SqliteStoreMariaDbHistoryTests` (row and stamp) and `ThreadSnapshotViewTests` (cell and
  *Longest running*).
- 2 [NON-BLOCKING] the distinct count's temporary b-tree — fixed now: *Bounds and their seams* and the area document state that it lives
  in SQLite temporary storage (the `/tmp` tmpfs), bounded by the 10 s interrupt, not the 256 cap; N4 and AC9 keep the circuit caps.
  It also runs only when more than 256 units exist.
- 3 [NON-BLOCKING] the new reads check less than the history read — fixed now: all three reads validate `at`, `from`/`to` with the texts
  of fact 25, every lead-in and window edge is clamped through `SeriesTime.Add`; DataRows in each `…RefusesInvalidQuery` and the
  `…ClampsLeadInAtStorableMin` / `…ClosestClampsWindowAtStorableMin` tests.
