# Plan: History view

Source: Issue #49 | [spec.md](spec.md)
Tier: standard — a new page with new store reads and chart interaction; it creates, changes or removes no security control (the page sits behind the existing login, the address and the chart callbacks are validated input handling, external text goes through the existing display).
Security areas touched: 3 Web UI login (a new page under `Components/Pages`), 10 Parsing of external input (the `/history` address and the values the browser sends through the chart callbacks), 12 Logging and display of external data (line labels, marker texts, snapshot fields, the new log line)
Plan size: one page with its own address, chart interaction, three new store reads and the snapshot panel; eleven criteria with their tests and the documentation sweep do not fit 2,500 words.

## Problem / root cause

Feature: see [spec.md](spec.md) (AC1 to AC11).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The time-series service is registered once, over the gated reader, and answers in process | `src/Vandox.Backend/Hosting/ServeCommand.cs:257` | confirmed |
| 2 | Query caps: 16 series, 64 lines, target 1 to 2,000, 1,000 markers; at most `2 x target - 1` times; a query without series is refused | `src/Vandox.Storage/TimeSeriesService.cs:14-34`, `docs/areas/time-series-query.md:100-109` | confirmed |
| 3 | No store read lists the stored metrics or reads a record by ID for a view | `grep -rn "DISTINCT\|ListSeries" src/Vandox.Storage`; `RecordQueries.SelectColumns` is the shared row reading | confirmed |
| 4 | Indexes `metrics(source, name, captured_at)`, `records(kind, captured_at)` and the primary key `metric_rollups_1h(source, name, labels, bucket)` exist; a skip scan is the store's precedent for a bounded distinct list | `docs/areas/storage.md:67,72`, `src/Vandox.Storage/LogExplorerQueries.cs:90-95` | confirmed |
| 5 | Each new statement is a `SEARCH`, never a `SCAN`; the `AtOrBefore` stamp read adds a temp B-tree for its last order term | to verify: `EXPLAIN QUERY PLAN` on the bundled e_sqlite3 in tests `StoredMetricsSeeksTheIndexes`, `SnapshotReadsSeekTheIndexes` (Tester, step 5; measured on SQLite 3.53.1 by the Devil's Advocate and Security) | unverified |
| 6 | Snapshots every 60 s (15 s under memory pressure); capture times are sub-second | `internal/agent/agent.go:56-64`, `internal/sysstats/sysstats.go:133` | confirmed |
| 7 | Process snapshots list the top 25 by memory and CPU with `total`; regular connection snapshots are `aggregates_only` with the top 25 remotes | `docs/areas/agent-system-metrics.md:122-129`, `docs/areas/agent-connections.md:56-60` | confirmed |
| 8 | A stored payload may reach 16 MiB | `src/Vandox.Storage/StorageLimits.cs:28` | confirmed |
| 9 | `js/charts.js` has one y axis and no drag, click or callback; uPlot 1.6.32 reports a drag only beyond `cursor.drag.dist` and stops the click after a pointer move | `src/Vandox.Backend/wwwroot/js/charts.js:35-50`, `wwwroot/lib/uplot/uPlot.esm.js:1377-1390, 5833-5848, 2952-2957` | confirmed |
| 10 | Drag, click and the right axis below 480 px behave in a browser as intended | to verify: the Product Manager's browser check below (AC2, AC3, AC5) on the stamped head before step 8 | unverified |
| 11 | No `JSInvokable` or `DotNetObjectReference` exists; circuit options are the defaults (32 KB message) | `grep -rn "JSInvokable" src`, `ServeCommand.cs:263` | confirmed |
| 12 | Browser scripts are outside the coverage measure (record 0098) | `docs/UNIT_TESTS.md:180` | confirmed |
| 13 | Every page needs `[Authorize]`, checked for every routable component | `Components/Pages/_Imports.razor`, `tests/Vandox.Backend.Tests/RoutesTests.cs:24-37` | confirmed |
| 14 | `PageRunner` shows the refused-search text for an `InvalidQueryException`; `IncidentsView.LogsHref` writes the link AC8 asks for | `src/Vandox.Backend/Logs/PageRunner.cs:111-114`, `src/Vandox.Backend/Incidents/IncidentsView.cs:218-232` | confirmed |
| 15 | `LogsAddress` reads keys without regard to case; its time reading `TryReadTime` is private | `src/Vandox.Backend/Logs/LogsAddress.cs:96-104, 274` | confirmed |
| 16 | The wire name pattern ends in `\z`; .NET's `$` accepts a trailing line feed | `src/Vandox.Core/Model/Check.cs:298` (internal to Core); Security's measurement | confirmed |
| 17 | `ChartData` refuses unordered times, length mismatches and non-finite values | `src/Vandox.Backend/Components/Charts/ChartData.cs:107-127` | confirmed |
| 18 | Log event id 75 is free; no release tag contains records 0221 and 0222 | `Hosting/BackendLog.cs:580`; `git ls-remote --tags origin`: v0.1.0, v0.2.0-rc.1, v0.2.0 | confirmed |

## Tests per criterion

Each test carries `[TestCategory("ACn")]`; backend tests in `tests/Vandox.Backend.Tests`, store tests in `tests/Vandox.Storage.Tests`.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `HistoryAddressTests.ParseReadsEveryParameter`, `ParseWithoutRangeIsTheLastDay`, `ParseRefusesRepeatedParameter`, `ParseRefusesInvalidValue` (DataRows: metric without comma, two commas, an empty part, a part of 129 bytes, a part outside the pattern, a part ending in `%0A`, one ending in `%0D`, `range=2h`, `from=2026-13-01T00:00`, `at=x`), `ParseRefusesMoreThanEightMetrics`, `ParseUsesRepeatedMetricOnce`, `ParseRefusesPresetWithTimes`, `ParseRefusesOneTime`, `ParseRefusesReversedRange`, `ParseIgnoresUnknownKeysAndCase`, `ToQueryReadsBackTheSameAddress`, `ToQueryRefusesInvalidValues` (a metric outside the pattern, nine metrics, a reversed range, a preset with a time); `HistoryPageTests.HistoryPageWithAddressErrorCallsNoStore`, `HistoryPageReloadShowsTheSameSnapshots` | `HistoryAddressTests.cs`, `HistoryPageTests.cs` |
| AC2 | `HistoryAddressTests.RangeOfPresetEndsAtNow`; `HistoryViewTests.SelectionWidensToWholeSeconds`, `SelectionRefusesInvalidValues` (NaN, ±Infinity, ±1e300, an end of the storable maximum minus 0.5 s, a descending pair); `HistoryChartEventsTests.SelectReportsValidSelection`, `SelectIgnoresInvalidValues`, `StopIgnoresLaterCalls`; `HistoryPageTests.HistoryPagePresetWritesNewEntry`, `HistoryPageFromToWritesFixedRange`, `HistoryPageInvalidFieldShowsTextAndStays`, `HistoryPageSelectionWritesFixedRangeAsNewEntry`; manual: browser check | `HistoryAddressTests.cs`, `HistoryViewTests.cs`, `HistoryChartEventsTests.cs`, `HistoryPageTests.cs` |
| AC3 | `HistoryChartViewTests.FromGivesEachMetricItsScaleAndTheFirstTwoAxes`, `FromNamesLinesWithSourceNameAndLabels`, `FromEscapesAndCapsLabels` (a label with U+202E built from the code point), `FromAssignsColorsAndDashesFromTheSeventhLine`, `FromKeepsGapsAndBridges`; `HistoryViewTests.TargetIsTheChartWidthAtMostThousand` (rows 0, -1, NaN, ±Infinity give 1; 1,500 gives 1,000); `HistoryChartInteropTests.RenderAsyncPassesScalesAxesStylesAndMarks`, `RenderAsyncRefusesInvalidData` (times not ascending, a length mismatch, NaN or Infinity in values, minimums or maximums); `HistoryPageTests.HistoryPageQueriesWithTheChartWidth`, `HistoryPageRemovingMetricWritesAddressWithoutIt`; manual: browser check | `HistoryChartViewTests.cs`, `HistoryViewTests.cs`, `HistoryChartInteropTests.cs`, `HistoryPageTests.cs` |
| AC4 | `SqliteStoreHistoryTests.StoredMetricsListsRawAndHourlyInOrder`, `StoredMetricsStopsAtTheLimitAndSetsMore`, `StoredMetricsSeeksTheIndexes`, `StoredMetricsRefusesLimitOutOfRange`; `HistoryViewTests.MetricOptionsDropsEntriesTheAddressRefuses`; `HistoryPageTests.HistoryPageWithoutMetricReadsTheList`, `HistoryPageShowsListCutText`, `HistoryPageKeepsAddressMetricOutsideTheList`, `HistoryPageWithoutMetricRunsNoQuery`, `HistoryPageRefusesNinthMetric`, `HistoryPageForgedMetricChoiceAddsNothing` | `SqliteStoreHistoryTests.cs`, `HistoryViewTests.cs`, `HistoryPageTests.cs` |
| AC5 | `HistoryChartViewTests.FromAtStepCarriesExtremesAndStepNote`, `FromAtRawHasNoExtremesAndNoNote`; `HistoryViewTests.StepTextNamesTheStep` (5 minutes to 3 days); manual: browser check | `HistoryChartViewTests.cs`, `HistoryViewTests.cs` |
| AC6 | `HistoryChartViewTests.FromMarksKillsBootsRestartsAndBackupSpans`, `FromListsEventsWithKindAndText`, `FromCapsEventTexts` (300 characters with U+202E, 16 runs), `FromShowsEventsCutText`; `HistoryPageTests.HistoryPageEventTimeSetsTheMoment` | `HistoryChartViewTests.cs`, `HistoryPageTests.cs` |
| AC7 | `HistoryChartViewTests.FromNotesMetricWithoutValues`, `FromNotesCutMetricsWithoutNoValuesText`, `FromNotesRangeBeforeRollups`, `FromNotesIncompletePoints`, `FromHasNoNotesForACompleteResult`; `HistoryPageTests.HistoryPageFutureRangeShowsTextAndReadsNothing` | `HistoryChartViewTests.cs`, `HistoryPageTests.cs` |
| AC8 | `HistoryViewTests.PickSnapsToTheNearestMarkerWithinTolerance`, `PickTakesTheEarlierMarkerOnATie`, `PickUsesThePointerTimeWithoutMarker`, `PickFloorsToTheSecond`, `RoundUpToSecondKeepsWholeSeconds`; `HistoryChartEventsTests.PickIgnoresInvalidValues` (the rows of `SelectionRefusesInvalidValues` and a negative tolerance); `HistoryChartViewTests.FromMarksTheMomentOnlyInsideTheRange`; `HistoryPageTests.HistoryPageClickReplacesEntryAndReadsOnlySnapshots`, `HistoryPageChartAndMomentChangeStartBoth`, `HistoryPageMomentShowsTheLogsLink` | `HistoryViewTests.cs`, `HistoryChartEventsTests.cs`, `HistoryChartViewTests.cs`, `HistoryPageTests.cs` |
| AC9 | `SqliteStoreHistoryTests.SnapshotStampReadsTheClosestRowOfEachSide`, `SnapshotReadsTheRecordOfTheStamp`, `SnapshotReadsSeekTheIndexes`, `SnapshotStampRefusesInvalidQuery`; `HistoryMomentReaderTests.ClosestPrefersNearerThenEarlierThenLowerId`, `ReadAsyncOffersTheSnapshotBefore`, `ReadAsyncReadsBothSidesWithinTenMinutes`, `ReadAsyncDecodesOneSnapshotAtATime`; `ProcessSnapshotViewTests.FromListsTwentyProgramsAndProcessesByMemory`, `FromCountsMoreAndListedOfTotal`, `FromUsesPssOnlyWhenEveryEntryHasIt`, `FromNotesIncompleteWithoutTotal`, `FromEscapesSourceCommandAndUser`; `HistoryPageTests.HistoryPageShowsNoSnapshotTexts`, `HistoryPageSnapshotBeforeShowsThatSnapshot` (sub-second capture times; the button is not offered again) | `SqliteStoreHistoryTests.cs`, `HistoryMomentReaderTests.cs`, `ProcessSnapshotViewTests.cs`, `HistoryPageTests.cs` |
| AC10 | `ConnectionSnapshotViewTests.FromListsStatesTopProcessesAndRemotes`, `FromHeadsTheRemotesOfACountsOnlySnapshot`, `FromNotesIncompleteCounts`, `FromCountsMore`, `FromEscapesSource` | `ConnectionSnapshotViewTests.cs` |
| AC11 | `HistoryPageTests.HistoryPagePrerenderedReadsNothing`, `HistoryPageRangeChangeCancelsTheChartRead`, `HistoryPageMomentChangeKeepsTheChartRead`, `HistoryPageDeadlineShowsTexts`, `HistoryPageFailureShowsTextAndLogsNoAddress`, `HistoryPageRefusedQueryShowsFailureText`; `GatedHistoryReaderTests.GatedHistoryReaderTakesOneGateSlotPerCall`; `HistoryChartViewTests.FromStaysWithinTheCaps` (64 lines of 1,999 times, 1,000 markers); `HistoryMomentReaderTests.ReadAsyncDecodesOneSnapshotAtATime`; `MainLayoutTests.MainLayoutMarksHistoryOnTheHistoryPage`; the existing `RoutesTests.RoutesEveryPageExceptLoginRequiresAuthorization` and `WebUiContentTests` cover the page unchanged | `HistoryPageTests.cs`, `GatedHistoryReaderTests.cs`, `HistoryChartViewTests.cs`, `HistoryMomentReaderTests.cs`, `MainLayoutTests.cs` |

**Browser check (manual: Product Manager)** on a local build of the stamped head (the image of `deploy/backend/Dockerfile` with its
compose file, or `dotnet run --project src/Vandox.Backend`) over agent data: a drag of 5 px or more zooms as a new entry, a shorter
press sets the moment and replaces it, a click within 8 px of a kill takes its time; two axes, the right one gone below 480 px; dashes
from the seventh line; legend extremes at a step; a theme change redraws; 360 px without horizontal scrolling. If the Product Manager
cannot build it, the Reviewer's reading of `charts.js` (step 8) stands and the check becomes `after merge` on the next release
candidate, logged as such.

## Approach

**Store (`Vandox.Storage`).** A new interface `IHistoryReader`, implemented by `SqliteStore`, SQL in an internal `HistoryQueries`:

- `StoredMetricsAsync(limit)`: limit 1 to `StorageLimits.MaxStoredMetrics` (256), else `store: invalid query: limit must be 1 to
  256`. A skip scan of `metrics` and of `metric_rollups_1h`: first `SELECT source, min(name) FROM t WHERE source = (SELECT min(source)
  FROM t)`, then `SELECT min(name) FROM t WHERE source = $source AND name > $name`, and on `NULL` the first name of the next source
  (`SELECT min(source) FROM t WHERE source > $source`); at most `limit + 1` entries per table, one seek each. Both lists are merged in
  ordinal order without repeats; the first `limit` are returned, `More` when a further one exists. One reader lease per call.
- `SnapshotStampAsync(query)` reads **only `id` and `captured_at`**: `Kind` `process_snapshot` or `connection_snapshot` (`store:
  invalid query: kind must be a snapshot kind`), `Window` above 0 and at most one day (`store: invalid query: window must be positive
  and at most one day`), `At` storable (`store: invalid query: at must be within the storable range`). `AtOrBefore`: `SELECT id,
  captured_at FROM records WHERE kind = $kind AND captured_at <= $at AND captured_at >= $at - window ORDER BY captured_at DESC, id
  LIMIT 1`; `After`: `captured_at > $at AND captured_at <= $at + window ORDER BY captured_at, id LIMIT 1` (bounds clamped). `id` stays
  ascending (the spec's lower ID on a tie); its sort holds only these two columns.
- `SnapshotAsync(stamp)`: `RecordQueries.SelectColumns(false, false) FROM records r WHERE r.id = $id AND r.kind = $kind` (kind checked
  as above), the existing row reading, so one payload is decoded; `null` when retention removed the row in between (shown as E8).

**Gate.** `GatedHistoryReader` (Backend, `History/`) takes one slot of `WebReaderGate` per call, as `GatedOverviewReader` does;
registered in `ServeCommand` as the singleton `IHistoryReader`.

**Address (`HistoryAddress`).** Parsed from `new Uri(Navigation.Uri).Query` with `QueryHelpers.ParseQuery` in the order `metric`,
`range`, `from`, `to`, `at`; the first error wins, then the combination rules in E1's order. An empty value is absent; an empty `metric`
value is skipped. A metric value splits at its one comma; each part is 1 to 128 UTF-8 bytes after percent-decoding and matches
`^[A-Za-z0-9][A-Za-z0-9._:/@+\-]*\z` (`RegexOptions.CultureInvariant`, a generated regex of `HistoryAddress`); distinct metrics keep
their first order; a ninth is `The address names more than 8 metrics.` Times are read by `LogsAddress.TryReadTime`, made internal and
reused (not copied). `Range(now)` gives `from`/`to`, else the preset (default `OneDay`) up to `now`. `ToQuery` first checks every value
again (pattern, byte length, at most 8 distinct, storable times, the combination rules) and throws `InvalidOperationException` on a
failure, as `LogsAddress.ToQuery` does, then writes the parameters in order with `LogsAddress.FormatTime`, percent-encoded. `SameChart`
compares metrics, preset, `from` and `to`.

**Page `HistoryPage` (`/history`).** Two `PageRunner`s (chart, moment) with the 10-second deadline and E14's texts, logging `history
view: reading failed` (`BackendLog.HistoryReadFailed`, event 75, error level, the exception only). Prerendered: `Loading…`. On the first
interactive render the chart action reads the metric list **whatever the address holds**; the chart host is rendered whenever the
address has metrics and no error, reports its width, and only then the time-series query runs with `HistoryView.Target(width)`. A range
that begins at or after now shows `This range lies in the future.` and reads nothing. An `InvalidQueryException` is wrapped into an
`InvalidOperationException` (fact 14): failure text and log. `LocationChanged` re-parses: when `SameChart` is false a chart action
starts (cancelling the running one), and **independently** when `At` differs a moment action starts; both may start. New entries: Add,
Remove, a preset, *From*/*To*, a selection; `replace: true`: a click, an event time, *Snapshot before this moment*. The select's value is
an index into `HistoryView.MetricOptions(list)`; *Add* takes that entry when the index is valid, else nothing. *From*/*To* are
validated with the address rules and show the address texts without navigating.

**Chart.** `HistoryChartView.From(result, metrics, range, moment)`: one line per `SeriesLine` in result order, `Scale` the metric's index
in the address, `Label` `<source> <name> <labels>` through `LogText.Escape(…, 64)`, colors `index % 6`, `Dashed` from 6; values the
average at a step, the value at raw, `null` for a gap, bridged flags; `Minimums`/`Maximums` only at a step. `Axes`: the escaped `<source>
<name>` of the first two metrics. Marks: kind, time, and for a backup run its end (`EndsAt`, or the range end while running or
unknown). Events: time, kind text, runs of `LogText.Runs(…, 256, 16)`: kill `<command> (PID <pid>)`, boot ID, unit, backup `<command>
until <time> UTC`, `<command>, running` or `<command>, end unknown`. Notes in the order E3 to E7; `StepNote` from `HistoryView.StepText`
(`<n> minutes`, `1 hour`, `<n> hours`, `1 day`, `<n> days`). `Moment` only inside the range. `HistoryView.Target(width)`: 1 for a width
that is not finite or below 1, else the floor, at most 1,000.

`HistoryChart` owns a `HistoryChartInterop` and a `DotNetObjectReference<HistoryChartEvents>`; on the first render it calls `width(host)`
and raises `OnWidth`; each view is drawn by `createHistory(host, setup, columns, events)` (the old chart destroyed first), a moment by
`chart.mark(seconds or null)`; a lost connection follows record 0200. `RenderAsync` refuses, before any call into the browser, times not
strictly ascending, a length mismatch and a non-finite value in values, minimums or maximums (`ArgumentException` naming the line index,
never its label), as `ChartData` does. `DisposeAsync` calls `HistoryChartEvents.Stop()` and disposes the reference; after `Stop` both
callbacks do nothing. Only instance `[JSInvokable]` methods on that reference are added, **never a static one**. `Select(start, end)` and
`Pick(at, tolerance)` drop a value that is not finite or outside the storable range **before any conversion**, a pair not ascending
and a tolerance outside 0 to one day; `HistoryView.Selection` widens outward to whole seconds and checks the storable range again
after widening. The page snaps a pick with `HistoryView.Pick(markers, pick)`: the nearest marker within the tolerance, the earlier on a
tie, else the pointer time, floored to the second.

`charts.js` gains `width` and `createHistory`: scales `y0` … `y7`; axes for scales 0 (left) and 1 (right, hidden below 480 CSS px,
re-checked on resize); strokes from `--vandox-chart-<color+1>`, `dash` for dashed lines; `cursor.drag` `{ x: true, y: false, dist: 5,
setScale: false }` with a `setSelect` hook sending start and end; a `mousedown`/`mouseup` pair on the over element sending a pick when
the pointer moved less than 5 px (tolerance: the span of 8 px); a `draw` hook for the marks (kills solid, boots dashed, restarts dotted,
backup runs shaded) and the moment (brand color); legend `min / avg / max` at a step. On `vandox-themechange` and a color-scheme change
it rebuilds scales, axes, hooks, marks and moment with the same data, as `createChart`'s `build()` does. It writes no HTML; the scan
rules of `docs/areas/web-ui.md`, *Limits and errors*, apply.

**Moment.** `HistoryMomentReader.ReadAsync(at)` handles one kind after the other: the two stamp reads (window 10 minutes), `Closest(before,
after, at)` (the nearer, the before one on equal distance; equal times are already ordered by ID), then `SnapshotAsync` of that stamp
only, the view built and the record dropped **before the next kind is read**: one decoded snapshot at a time (N2). Call order per moment:
process stamps, process record, connection stamps, connection record. When the process stamp shown is the after one and a before one
exists, `ProcessBefore` is that one's time; *Snapshot before this moment* sets the moment to
`HistoryView.RoundUpToSecond(ProcessBefore)`, so the `AtOrBefore` read finds it (sub-second capture times, fact 6).
`ProcessSnapshotView.FromProcesses` and `ConnectionSnapshotView.FromConnections` build the panel: lists sorted by memory (PSS when every
entry of that list has it, else RSS) or count, descending, ties by name or PID; at most 20 rows; `and <n> more programs`/`processes`/`more`;
`<listed> of <total> processes are listed.` when `Total` is higher; E9's notes; S8's remotes heading when `AggregatesOnly`. Text cells and
the heading's source through `LogText.Runs(…, 256, 16)`, memory through `OverviewText.Size`, CPU with one decimal.

**Layout** (order fixed, styling free for the Tester, approved in step 9): heading *History*; presets (current marked), *From*, *To*,
*Apply*; the metric select with *Add* and the chosen metrics with *Remove*; address or list texts; notes and step note as paragraphs; the
chart; *Events* table (time as a button, kind, text); the panel: `Moment <time> UTC`, *Logs of this moment* (`IncidentsView.LogsHref`),
*Process snapshot* (heading with the source through `ExternalText`, two tables, notes), *Connection snapshot* likewise. Without metrics
only the controls and `Choose one or more metrics to draw.`

**Memory per circuit** (for the area document): chart data at most 64 × 1,999 × 3 doubles (about 3 MiB) plus the result while built
(about 9 MiB); 1,000 events of 256 characters in 16 runs (about 1.5 MiB); 256 metric keys; one decoded snapshot (16 MiB payload) while a
panel is built, then lists of 20. The Dev states the measured figures.

## Options considered

- **Chart:** two drawn axes and legend extremes against eight axes, one axis per unit, small multiples, bands or averages only — record 0221.
- **Moment and metric list:** own bounded reads against links to the future views, the existing record read and `SELECT DISTINCT` — record 0222.
- **Snapshot read:** stamps first, then the chosen record (one decode) against both sides decoded (two payloads held per kind, Security
  finding 2) — record 0222.
- **Snapping in the browser:** one round trip less, but the tie rule in an untested script.
- **The existing `TimeSeriesChart`/`ChartInterop`** extended: its single-axis charts in the incidents and logs views would change with it.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `IHistoryReader.cs`, `StoredMetricList.cs`, `SnapshotQuery.cs`, `SnapshotSide.cs`, `SnapshotStamp.cs`, `HistoryQueries.cs` | new |
| Vandox.Storage | `SqliteStore.cs`, `StorageLimits.cs` | implements `IHistoryReader`; `MaxStoredMetrics` |
| Vandox.Backend | `History/` (`GatedHistoryReader`, `HistoryAddress`, `HistoryPreset`, `HistoryRange`, `HistoryPick`, `HistoryView`, `HistoryChartView`, `HistoryLine`, `HistoryMark`, `HistoryEventRow`, `HistoryMomentReader`, `HistoryMoment`, `ProcessSnapshotView`, `ConnectionSnapshotView`, `SnapshotTableView`, `SnapshotRowView`) | new |
| Vandox.Backend | `Components/Charts/HistoryChart.razor(.cs)`, `HistoryChartInterop.cs`, `HistoryChartEvents.cs`; `Components/Pages/HistoryPage.razor(.cs)` | new |
| Vandox.Backend | `Logs/LogsAddress.cs` | `TryReadTime` private → internal |
| Vandox.Backend | `Components/Layout/MainLayout.razor`, `Hosting/ServeCommand.cs`, `Hosting/BackendLog.cs`, `wwwroot/js/charts.js`, `wwwroot/app.css` | nav entry, registration, log method, chart module, styles |

## Signatures (for the Dev's skeleton)

All public unless marked. A data class is one declaration listing its properties; the accessors follow the note after it.

Vandox.Storage:

- `interface IHistoryReader`, implemented by SqliteStore and GatedHistoryReader:
- `Task<StoredMetricList> StoredMetricsAsync(int limit, CancellationToken cancellationToken)`
- `Task<SnapshotStamp?> SnapshotStampAsync(SnapshotQuery query, CancellationToken cancellationToken)`
- `Task<StoredRecord?> SnapshotAsync(SnapshotStamp stamp, CancellationToken cancellationToken)`
- `class StoredMetricList { IReadOnlyList<SeriesKey> Metrics; bool More }` (sealed; init, Metrics empty by default)
- `class SnapshotQuery { string Kind; DateTimeOffset At; TimeSpan Window; SnapshotSide Side }` (sealed; init)
- `class SnapshotStamp { long Id; string Kind; DateTimeOffset CapturedAt }` (sealed; init)
- `enum SnapshotSide { AtOrBefore, After }`
- `StorageLimits.MaxStoredMetrics = 256` (public integer constant)
- `class HistoryQueries` (internal static; statement constants read by the query plan tests)

Vandox.Backend, namespace Vandox.Backend.Logs:

- `internal static bool TryReadTime(string value, out DateTimeOffset time)` (on LogsAddress, was private)

Vandox.Backend, namespace Vandox.Backend.History:

- `GatedHistoryReader(IHistoryReader inner, WebReaderGate gate)` (sealed class, implements IHistoryReader)
- `enum HistoryPreset { OneHour, OneDay, SevenDays, ThirtyDays, OneYear }`
- `HistoryRange(DateTimeOffset start, DateTimeOffset end)` (sealed class, get-only Start and End)
- `HistoryPick(DateTimeOffset at, TimeSpan tolerance)` (sealed class, get-only At and Tolerance)
- `class HistoryAddress { IReadOnlyList<SeriesKey> Metrics; HistoryPreset? Preset; DateTimeOffset? From; DateTimeOffset? To; DateTimeOffset? At; string Error }` (sealed; get and set, Metrics and Error empty by default)
- `static HistoryAddress Parse(string query)`
- `HistoryAddress Copy()`
- `HistoryRange Range(DateTimeOffset now)`
- `string ToQuery()` (throws InvalidOperationException for an error or a value the checks refuse)
- `bool SameChart(HistoryAddress other)`
- `static class HistoryView` (one public string constant per spec text, names free; integer constants MaxMetrics 8, MaxTarget 1000, ListRows 20, LabelChars 64, TextChars 256, TextRuns 16; a static read-only MomentWindow of 10 minutes)
- `static string PresetValue(HistoryPreset preset)`
- `static string PresetLabel(HistoryPreset preset)`
- `static TimeSpan PresetSpan(HistoryPreset preset)`
- `static string StepText(TimeSpan step)`
- `static int Target(double chartWidth)`
- `static HistoryRange? Selection(double startSeconds, double endSeconds)`
- `static DateTimeOffset Pick(IReadOnlyList<SeriesMarker> markers, HistoryPick pick)`
- `static DateTimeOffset RoundUpToSecond(DateTimeOffset time)`
- `static IReadOnlyList<SeriesKey> MetricOptions(StoredMetricList list)`
- `static string HistoryHref(HistoryAddress address)`
- `class HistoryLine { string Label; int Scale; int Color; bool Dashed; IReadOnlyList<double?> Values; IReadOnlyList<bool> Bridged; IReadOnlyList<double?> Minimums; IReadOnlyList<double?> Maximums }` (sealed; init)
- `class HistoryMark { MarkerKind Kind; DateTimeOffset At; DateTimeOffset? EndsAt }` (sealed; init)
- `class HistoryEventRow { DateTimeOffset At; string Kind; IReadOnlyList<TextRun> Text }` (sealed; init)
- `class HistoryChartView { IReadOnlyList<DateTimeOffset> Times; IReadOnlyList<HistoryLine> Lines; IReadOnlyList<string> Axes; IReadOnlyList<HistoryMark> Marks; IReadOnlyList<HistoryEventRow> Events; IReadOnlyList<string> Notes; string StepNote; DateTimeOffset? Moment }` (sealed; init)
- `static HistoryChartView From(TimeSeriesResult result, IReadOnlyList<SeriesKey> metrics, HistoryRange range, DateTimeOffset? moment)` (on HistoryChartView)
- `class SnapshotRowView { IReadOnlyList<IReadOnlyList<TextRun>> Cells }` (sealed; init)
- `class SnapshotTableView { string Heading; IReadOnlyList<string> Columns; IReadOnlyList<SnapshotRowView> Rows; string More }` (sealed; init)
- `class ProcessSnapshotView { DateTimeOffset CapturedAt; IReadOnlyList<TextRun> Source; SnapshotTableView Programs; SnapshotTableView Processes; string ListedOfTotal; string Note }` (sealed; init)
- `static ProcessSnapshotView FromProcesses(StoredRecord record)` (on ProcessSnapshotView)
- `class ConnectionSnapshotView { DateTimeOffset CapturedAt; IReadOnlyList<TextRun> Source; SnapshotTableView States; SnapshotTableView Processes; SnapshotTableView Remotes; string Note }` (sealed; init)
- `static ConnectionSnapshotView FromConnections(StoredRecord record)` (on ConnectionSnapshotView)
- `class HistoryMoment { DateTimeOffset At; ProcessSnapshotView? Process; DateTimeOffset? ProcessBefore; ConnectionSnapshotView? Connection }` (sealed; init)
- `HistoryMomentReader(IHistoryReader reader)` (sealed class)
- `Task<HistoryMoment> ReadAsync(DateTimeOffset at, CancellationToken cancellationToken)` (on HistoryMomentReader)
- `static SnapshotStamp? Closest(SnapshotStamp? atOrBefore, SnapshotStamp? after, DateTimeOffset at)` (on HistoryMomentReader)

Vandox.Backend, namespace Vandox.Backend.Components.Charts:

- `HistoryChartEvents(Func<HistoryRange, Task> selected, Func<HistoryPick, Task> picked)` (sealed class)
- `Task Select(double startSeconds, double endSeconds)` (instance JSInvokable, on HistoryChartEvents)
- `Task Pick(double atSeconds, double toleranceSeconds)` (instance JSInvokable, on HistoryChartEvents)
- `void Stop()` (on HistoryChartEvents)
- `HistoryChartInterop(IJSRuntime jsRuntime)` (sealed class, asynchronously disposable)
- `Task<double> WidthAsync(ElementReference host, CancellationToken cancellationToken)`
- `Task<bool> RenderAsync(ElementReference host, HistoryChartView view, DotNetObjectReference<HistoryChartEvents> events, CancellationToken cancellationToken)`
- `Task MarkAsync(DateTimeOffset? moment, CancellationToken cancellationToken)`
- `class HistoryChart` (sealed partial component, asynchronously disposable; parameters View of type HistoryChartView and the event callbacks OnWidth with a double, OnSelect with a HistoryRange, OnPick with a HistoryPick)

Vandox.Backend, other:

- `class HistoryPage` (sealed partial component in Components.Pages, route /history, disposable; injects the time-series service, the history reader, the clock, the navigation manager and its logger)
- `static partial void HistoryReadFailed(this ILogger logger, Exception exception)` (internal, on BackendLog, event 75)

Checked against *Writing code*: no nested type, no parameter named after a keyword of another .NET language, at most four parameters,
no enum ending in Flags or Enum, no generic type.

## Test files

`tests/Vandox.Backend.Tests`: `HistoryAddressTests.cs`, `HistoryViewTests.cs`, `HistoryChartViewTests.cs`, `HistoryChartEventsTests.cs`,
`HistoryChartInteropTests.cs`, `HistoryMomentReaderTests.cs`, `ProcessSnapshotViewTests.cs`, `ConnectionSnapshotViewTests.cs`,
`GatedHistoryReaderTests.cs`, `HistoryPageTests.cs` with `HistoryPageHarness.cs`, `FakeHistoryReader.cs` (scripted metric list, stamps
and records; records the call sequence; can hold or fail a call) and `ScriptedTimeSeriesReader.cs` (scripted lines, markers and failures
behind a real `TimeSeriesService`), and the existing `MainLayoutTests.cs` (a new test, the others unchanged).
`tests/Vandox.Storage.Tests`: `SqliteStoreHistoryTests.cs`. The query plan tests fail on any `SCAN` and on a temp B-tree in a metric
statement; they accept `USING PRIMARY KEY` for `metric_rollups_1h` (a WITHOUT ROWID table) and `USE TEMP B-TREE FOR LAST TERM OF ORDER
BY` for the `AtOrBefore` stamp read (`id` stays ascending).

Existing test code that calls a changed signature: none (`TryReadTime` was private; `SqliteStore` gains an interface).

Files no unit test can cover: the additions to `src/Vandox.Backend/wwwroot/js/charts.js`, already outside the coverage measure (record
0098); no new exclusion. The Tester confirms or contests this.

## Areas

- New area *History view*, `docs/areas/web-ui-history-view.md`: the page, address, controls, chart, markers, event list, moment panel,
  texts, calls, deadline and memory per circuit. Not here: the query, the store reads, the general web UI and login.
- `web-ui.md`, `time-series-query.md`, `storage-reading.md`: see *Documentation updates*.
- Split (Lead decision; *History reads* took `storage-reading.md` past `AREA_MAX_WORDS`): new area *Log search and exploration*,
  `docs/areas/log-exploration.md`, takes *Log search*, *Log exploration* and their limits; `storage-reading.md` stays as *Storage reading*.

## Documentation updates

All by the Dev after the code is green, unless marked.

- `docs/areas/web-ui-history-view.md` (new) and its row in `docs/areas/README.md`.
- `docs/areas/web-ui.md`: *Scope* "history views are planned" becomes the link; *Layout and navigation* "four entries" becomes five with
  *History*; *Store reads of the views* adds the history view to "the overview page, the logs view, the incidents view and the report
  page"; *Charts* gains scales, drawn axes, selection, click, marks, legend extremes and the browser callbacks (instance only, disposed
  with the chart); *Static assets* the `width`/`createHistory` exports; *Related decisions* 0221; *Not here*, *Implementation*.
- `docs/areas/time-series-query.md`: *Scope* "(planned)" and *Not here* "planned" name the history view.
- `docs/areas/storage-reading.md`: *Scope* list and a section *History reads* (metric list, stamp read, record read; limits, errors, one
  seek per entry); *Related decisions* 0222; *Implementation* `IHistoryReader`, `HistoryQueries`.
- `docs/ARCHITECTURE.md`: lines 10-11 and the views paragraph (lines 132-149) gain the history view with links to 0221 and 0222; the store
  interface list (lines 481-482) gains `IHistoryReader`.
- `README.md`: a section *Trace metrics over time* after *Explore the logs*. `CHANGELOG.md`: *Added* **History view** (#49) under
  *Unreleased*.
- `.squad/project.md` (Dev reports the text, the orchestrator writes it): areas 3 and 12 gain `HistoryPage`; area 10 the `/history`
  address and the chart callbacks beside `/logs` and `/report`, and `web-ui-history-view`. *Test doubles* (Tester reports):
  `FakeHistoryReader`, `ScriptedTimeSeriesReader`.
- The split (Dev): as the Lead's `decide` result lists, file by file.
- Records 0221 and 0222 (Lead; the orchestrator may apply it verbatim): `Area:` `Web UI` → `History view` once the index row exists.

## Architecture check

No guarantee of `.squad/project.md` changes: backfilled data raises nothing (the view only reads), a gap is never drawn as data (record
0028), no agent path is touched. The page follows the web UI's rules: login, read gate, deadline, external-text display, self-only policy
(no new file, no inline script or style).

## Security considerations

- [x] Limits on parsed input: metric parts 1 to 128 UTF-8 bytes after percent-decoding, pattern anchored with `\z`; at most 8 distinct;
  `ToQuery` re-checks every value. Browser callbacks carry numbers, checked for finiteness and range before conversion; the select's
  value is an index into the offered list. The message size stays the framework's 32 KB.
- [x] Exceptions that reach the page: never as their message; fixed E14 texts; the log holds the exception only, never an address value
  or a line label.
- External text (labels, marker texts, sources, programs, users, commands, addresses) reaches the page only through `ExternalText` runs
  and the chart only through `LogText.Escape`; `charts.js` writes no HTML.
- Callbacks: instance `[JSInvokable]` only, on a reference the chart disposes; no static one.
- Links: `history` plus `ToQuery`, and `IncidentsView.LogsHref`.

## Decision records

- `docs/decisions/0221-the-history-view-draws-every-metric-on-its-own-scale-with-two-axes-and-step-extremes-in-the-legend.md` (Proposed)
- `docs/decisions/0222-the-history-view-reads-the-moments-snapshots-and-the-metric-list-through-its-own-bounded-reads.md` (Proposed; this revision adds the stamp-first read)

## Challenge

Devil's Advocate, plan round: no major, 7 minor objections, all accepted.

1. Metric list behind the width — accepted: the list is read on the first interactive render whatever the address holds; the host renders
   only with metrics (*Page*); test `HistoryPageWithoutMetricReadsTheList`.
2. Chart and moment change together — accepted: both actions start independently (*Page*); test `HistoryPageChartAndMomentChangeStartBoth`.
3. *Snapshot before this moment* loop — accepted: the moment is `RoundUpToSecond(ProcessBefore)` (*Moment*); tests
   `RoundUpToSecondKeepsWholeSeconds`, `HistoryPageSnapshotBeforeShowsThatSnapshot`; spec S7 says so.
4. `\z` and time reading — accepted: pattern ends in `\z`; `LogsAddress.TryReadTime` becomes internal and is reused (*Signatures*,
   *Affected*); DataRows `%0A`, `%0D`.
5. Query plan expectations — accepted as stated (*Test files*); `id` stays ascending.
6. Chart guarantees and theme rebuild — accepted: `RenderAsync` refuses invalid data, `createHistory` rebuilds on a theme change
   (*Chart*); test `RenderAsyncRefusesInvalidData`; the browser check includes a theme change.
7. `Target` clamps — accepted: 1 for a non-finite width or one below 1 (*Chart*); rows added to `TargetIsTheChartWidthAtMostThousand`.

## Security review

Security, plan round: `CHANGES_REQUIRED`, two blocking and four non-blocking findings.

1. [BLOCKING] Pattern with `$` — resolved: `\z` with `CultureInvariant` (*Address*); DataRows `%0A` and `%0D` in `ParseRefusesInvalidValue`.
2. [BLOCKING] Two decoded snapshots per kind — resolved with option (a): `SnapshotStampAsync` reads `id` and `captured_at` only, the
   reader picks the closest and `SnapshotAsync` decodes that one record; the first kind's view is built and its record dropped before
   the second kind is read, so N2's "one decoded snapshot at a time" holds unchanged. `ReadAsyncDecodesOneSnapshotAtATime` asserts the
   fake's call sequence (process stamps, process record, connection stamps, connection record) and that one record is read per kind.
   Record 0222 updated.
3. Add value and `ToQuery` — fixed now: the select's value is an index into `MetricOptions`; `ToQuery` re-checks every value; tests
   `HistoryPageForgedMetricChoiceAddsNothing`, `ToQueryRefusesInvalidValues`.
4. Selection and pick checks, disposal — fixed now: finiteness and storable range before conversion and after widening; `Stop()` and
   disposal of the reference in `DisposeAsync`; no static `[JSInvokable]` (*Chart*, *Security considerations*); DataRows as proposed,
   test `StopIgnoresLaterCalls` (on the events object, which is what a late callback reaches).
5. Heading source — fixed now: `CapturedAt` and `Source` runs replace `Heading` in both snapshot views, rendered through `ExternalText`;
   tests `FromEscapesSourceCommandAndUser`, `FromEscapesSource`.
6. `AtOrBefore` sorts full rows — fixed by 2: the sort holds `id` and `captured_at` only.

## Out of scope / follow-ups

As the spec's *Out of scope*: self-healing markers (#71), the gap list (#54), the processes (#50) and network (#51) views, bands, touch
drag, automatic refresh and links into the view. No further follow-up issue.

## Security notes

All six findings from my plan review are resolved in the revision, so the plan is approved; one new non-blocking finding came out of the delta.

I read the change since my round at `FETCH_HEAD` 8e25b04 on `origin/claude/busy-tesla-im0m00` (from cfa5900) with `git show` and `git diff`, without checking it out. That covers `plan.md`, `spec.md` (S7), `tasks.md` and record 0222.

## My earlier findings

1. **[BLOCKING] Pattern ended in `$`: resolved.** *Address* now uses `^[A-Za-z0-9][A-Za-z0-9._:/@+\-]*\z` as a generated regex with `RegexOptions.CultureInvariant`. `ParseRefusesInvalidValue` has the `%0A` and `%0D` DataRows. In my first round, .NET printed `$ : True  \z : False` for `"cpu_percent\n"`, so `\z` closes the bypass. Times are now read by reusing `LogsAddress.TryReadTime` (made internal) rather than a copy.
2. **[BLOCKING] Two decoded snapshots per kind: resolved with option (a).**
   - `SnapshotStampAsync` reads only `id` and `captured_at`.
   - `SnapshotAsync(stamp)` reads one record by ID and kind.
   - *Moment* runs the kinds one after the other and drops the first record before the second kind is read.
   - `ReadAsyncDecodesOneSnapshotAtATime` checks the call order and that one record is read per kind; it is listed under AC9 and AC11.
   - N2 and *Memory per circuit* ("one decoded snapshot") are now true, and record 0222 (lines 36 and 42) matches.
   - I re-ran the query plans with `python3 -I /tmp/claude-0/-home-user-Vandox/7d628ade-087e-57b9-93c6-af0ce179da7a/scratchpad/eqp2.py` (SQLite 3.53.1, schema from `SchemaMigrator.cs`). Output:
     - `AtOrBefore`: `SEARCH records USING COVERING INDEX records_kind_time (kind=? AND captured_at>? AND captured_at<?)` and `USE TEMP B-TREE FOR LAST TERM OF ORDER BY`
     - `After`: `SEARCH records USING COVERING INDEX records_kind_time (...)`
     - record read: `SEARCH r USING INTEGER PRIMARY KEY (rowid=?)`
3. **Value of the *Add* choice, and `ToQuery` not re-checking: resolved.** The choice is now an index into `MetricOptions`, and an invalid index adds nothing. `ToQuery` checks pattern, byte length, the count, times and the combination rules again, and throws as `LogsAddress.ToQuery` does. Tests: `HistoryPageForgedMetricChoiceAddsNothing` and `ToQueryRefusesInvalidValues`.
4. **Selection and pick checks, and disposal: resolved.**
   - Values are checked for finiteness and the storable range before any conversion, and again after widening.
   - `DisposeAsync` calls `Stop()` and disposes the reference; later calls do nothing.
   - The plan says no static `[JSInvokable]` will be added.
   - The DataRows are as I proposed, plus a negative tolerance and `StopIgnoresLaterCalls`.
5. **Source in the snapshot headings: resolved.** Both snapshot views now carry `CapturedAt` and `Source` as `IReadOnlyList<TextRun>`, rendered through `ExternalText`. Tests: `FromEscapesSourceCommandAndUser` and `FromEscapesSource`.
6. **The `AtOrBefore` sort held full rows: resolved.** The read is now covering, so the temp B-tree holds only `id` and `captured_at` (EXPLAIN output above).

## New finding in the delta

- [NON-BLOCKING] **`HistoryView.RoundUpToSecond` can step past the storable range.** `StorableTime.Max` is not a whole second (`src/Vandox.Core/Model/StorableTime.cs:18`, ticks `713589688368547758`).
  - If `ProcessBefore` falls within the last fraction of a second below `Max`, rounding up passes `Max`.
  - `ToQuery` would then throw `InvalidOperationException` in the *Snapshot before this moment* handler and end that user's circuit.
  - It needs a stored snapshot captured near the end of the storable range, so it is practically unreachable.
  - Smallest fix: clamp the result to the last whole second at or below `Max`, as `IncidentsView.LogsHref` does with `lastSecond` (`src/Vandox.Backend/Incidents/IncidentsView.cs:222`). Add a DataRow at `Max` to `RoundUpToSecondKeepsWholeSeconds`.
  - Recommendation: fix now, in the Dev's implementation; no plan revision is needed.
