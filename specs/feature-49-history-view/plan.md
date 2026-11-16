# Plan: History view

Source: Issue #49 | [spec.md](spec.md)
Tier: standard — a new page with new store reads and chart interaction; it creates, changes or removes no security control (the page sits behind the existing login, the address and the chart callbacks are validated input handling, external text goes through the existing display).
Security areas touched: 3 Web UI login (a new page under `Components/Pages`), 10 Parsing of external input (the `/history` address and the values the browser sends through the chart callbacks), 12 Logging and display of external data (line labels, marker texts, snapshot fields, the new log line)
Plan size: one page with its own address, chart interaction, two new store reads and the snapshot panel; eleven criteria with their tests and the documentation sweep do not fit 2,500 words.

## Problem / root cause

Feature: see [spec.md](spec.md) (AC1 to AC11).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The time-series service is registered once, over the gated reader, and answers in process | `src/Vandox.Backend/Hosting/ServeCommand.cs:257` | confirmed |
| 2 | Query caps: 16 series, 64 lines, target 1 to 2,000, 1,000 markers; at most `2 x target - 1` times | `src/Vandox.Storage/TimeSeriesService.cs:14-34`, `docs/areas/time-series-query.md:100-102` | confirmed |
| 3 | A query without series is refused, so a view without metrics must not call it | `docs/areas/time-series-query.md:109` | confirmed |
| 4 | No store read lists the stored metrics | `grep -rn "DISTINCT\|ListSeries" src/Vandox.Storage` finds none for metrics | confirmed |
| 5 | Indexes `metrics(source, name, captured_at)` and the primary key `metric_rollups_1h(source, name, labels, bucket)` exist | `docs/areas/storage.md:67,72` | confirmed |
| 6 | A skip scan of one index seek per entry is the store's precedent for a bounded distinct list | `src/Vandox.Storage/LogExplorerQueries.cs:90-95`, `docs/areas/storage-reading.md:241` | confirmed |
| 7 | Each statement of the metric skip scan and of the snapshot read is a `SEARCH` of the named index, never a `SCAN` | to verify: `EXPLAIN QUERY PLAN` tests `StoredMetricsSeeksTheIndexes` and `SnapshotSeeksTheKindTimeIndex`, Tester in step 5, Dev makes them pass in step 6 | unverified |
| 8 | `records(kind, captured_at)` exists for a kind-wide time read | `docs/areas/storage.md:72` | confirmed |
| 9 | The agent takes process and connection snapshots every 60 s (15 s under memory pressure) | `internal/agent/agent.go:56-64` | confirmed |
| 10 | A regular process snapshot lists the union of the top 25 by memory and by CPU and carries `total`; programs count every process read | `docs/areas/agent-system-metrics.md:122-129` | confirmed |
| 11 | A regular connection snapshot is `aggregates_only`, its remotes are the top 25 | `docs/areas/agent-connections.md:56-60`, `docs/areas/wire-format-records.md:124-126` | confirmed |
| 12 | A stored payload may reach 16 MiB, so one decoded snapshot can be large | `src/Vandox.Storage/StorageLimits.cs:28` | confirmed |
| 13 | `js/charts.js` has one y axis and no drag, click or callback today | `src/Vandox.Backend/wwwroot/js/charts.js:35-50`, whole file | confirmed |
| 14 | uPlot 1.6.32 reports a drag only beyond `cursor.drag.dist` pixels, sets the selection on mouseup, and its capture click handler stops the click after any pointer move | `src/Vandox.Backend/wwwroot/lib/uplot/uPlot.esm.js:1377-1390, 5579, 5833-5848, 2952-2957` | confirmed |
| 15 | Drag, click and the right axis below 480 px behave in a browser as the chart module intends | to verify: the Product Manager's browser check (*Tests per criterion*, AC2, AC3, AC5) on the stamped head before step 8 | unverified |
| 16 | No component calls into .NET from the browser yet (`JSInvokable`, `DotNetObjectReference`) | `grep -rn "JSInvokable" src` finds none | confirmed |
| 17 | Browser scripts have no unit tests and are outside the coverage measure (record 0098) | `docs/UNIT_TESTS.md:180`, `.squad/stack.md` *Coverage exclusions* | confirmed |
| 18 | Every page needs `[Authorize]`, checked for every routable component | `src/Vandox.Backend/Components/Pages/_Imports.razor`, `tests/Vandox.Backend.Tests/RoutesTests.cs:24-37` | confirmed |
| 19 | `PageRunner` shows the logs view's refused-search text for an `InvalidQueryException` | `src/Vandox.Backend/Logs/PageRunner.cs:111-114` | confirmed |
| 20 | `IncidentsView.LogsHref` writes the logs link of a moment exactly as AC8 asks | `src/Vandox.Backend/Incidents/IncidentsView.cs:218-232` | confirmed |
| 21 | `QueryHelpers.ParseQuery` matches keys without regard to case; `LogsAddress` relies on it | `src/Vandox.Backend/Logs/LogsAddress.cs:96-104`, `docs/areas/web-ui-logs-view.md:35` | confirmed |
| 22 | Snapshots follow `retention.snapshots` (30 d) except in kill holds, listener findings and imports | `docs/areas/retention.md:12-29, 52-59` | confirmed |
| 23 | Log event id 75 is free | `src/Vandox.Backend/Hosting/BackendLog.cs:580` (74 is the last) | confirmed |
| 24 | No release tag contains records 0221 and 0222 (new in this branch) | `git ls-remote --tags origin`: v0.1.0, v0.2.0-rc.1, v0.2.0 | confirmed |

## Tests per criterion

Each test carries `[TestCategory("ACn")]`. Backend tests are in `tests/Vandox.Backend.Tests`, storage tests in `tests/Vandox.Storage.Tests`.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `HistoryAddressTests.ParseReadsEveryParameter`, `ParseWithoutRangeIsTheLastDay`, `ParseRefusesRepeatedParameter` (each name; `metric` repeats are allowed), `ParseRefusesInvalidValue` (DataRows: metric without comma, with two commas, an empty part, a part of 129 bytes, a part outside the name pattern, `range=2h`, `from=2026-13-01T00:00`, `at=x`), `ParseRefusesMoreThanEightMetrics`, `ParseUsesRepeatedMetricOnce`, `ParseRefusesPresetWithTimes`, `ParseRefusesOneTime`, `ParseRefusesReversedRange`, `ParseIgnoresUnknownKeysAndCase`, `ToQueryReadsBackTheSameAddress`; `HistoryPageTests.HistoryPageWithAddressErrorCallsNoStore`, `HistoryPageReloadShowsTheSameSnapshots` | `HistoryAddressTests.cs`, `HistoryPageTests.cs` |
| AC2 | `HistoryAddressTests.RangeOfPresetEndsAtNow`; `HistoryViewTests.SelectionWidensToWholeSeconds`, `SelectionRefusesInvalidValues`; `HistoryChartEventsTests.SelectReportsValidSelection`, `SelectIgnoresInvalidValues`; `HistoryPageTests.HistoryPagePresetWritesNewEntry`, `HistoryPageFromToWritesFixedRange`, `HistoryPageInvalidFieldShowsTextAndStays`, `HistoryPageSelectionWritesFixedRangeAsNewEntry`; manual: Product Manager, browser check below (drag of 5 px or more zooms, a shorter press is a click) | `HistoryAddressTests.cs`, `HistoryViewTests.cs`, `HistoryChartEventsTests.cs`, `HistoryPageTests.cs` |
| AC3 | `HistoryChartViewTests.FromGivesEachMetricItsScaleAndTheFirstTwoAxes`, `FromNamesLinesWithSourceNameAndLabels`, `FromEscapesAndCapsLabels` (a label holding U+202E, built from the code point), `FromAssignsColorsAndDashesFromTheSeventhLine`, `FromKeepsGapsAndBridges`; `HistoryViewTests.TargetIsTheChartWidthAtMostThousand`; `HistoryChartInteropTests.RenderAsyncPassesScalesAxesStylesAndMarks`; `HistoryPageTests.HistoryPageQueriesWithTheChartWidth`, `HistoryPageRemovingMetricWritesAddressWithoutIt`; manual: Product Manager, browser check below (two axes, one below 480 px, dashed lines) | `HistoryChartViewTests.cs`, `HistoryViewTests.cs`, `HistoryChartInteropTests.cs`, `HistoryPageTests.cs` |
| AC4 | `SqliteStoreHistoryTests.StoredMetricsListsRawAndHourlyInOrder`, `StoredMetricsStopsAtTheLimitAndSetsMore`, `StoredMetricsSeeksTheIndexes`, `StoredMetricsRefusesLimitOutOfRange`; `HistoryViewTests.MetricOptionsDropsEntriesTheAddressRefuses`; `HistoryPageTests.HistoryPageShowsListCutText`, `HistoryPageKeepsAddressMetricOutsideTheList`, `HistoryPageWithoutMetricRunsNoQuery`, `HistoryPageRefusesNinthMetric` | `SqliteStoreHistoryTests.cs`, `HistoryViewTests.cs`, `HistoryPageTests.cs` |
| AC5 | `HistoryChartViewTests.FromAtStepCarriesExtremesAndStepNote`, `FromAtRawHasNoExtremesAndNoNote`; `HistoryViewTests.StepTextNamesTheStep` (DataRows 5 minutes to 3 days); manual: Product Manager, browser check below (legend shows minimum, average, maximum) | `HistoryChartViewTests.cs`, `HistoryViewTests.cs` |
| AC6 | `HistoryChartViewTests.FromMarksKillsBootsRestartsAndBackupSpans`, `FromListsEventsWithKindAndText`, `FromCapsEventTexts` (300 characters with a U+202E, 16 runs), `FromShowsEventsCutText`; `HistoryPageTests.HistoryPageEventTimeSetsTheMoment` | `HistoryChartViewTests.cs`, `HistoryPageTests.cs` |
| AC7 | `HistoryChartViewTests.FromNotesMetricWithoutValues`, `FromNotesCutMetricsWithoutNoValuesText`, `FromNotesRangeBeforeRollups`, `FromNotesIncompletePoints`, `FromHasNoNotesForACompleteResult`; `HistoryPageTests.HistoryPageFutureRangeShowsTextAndReadsNothing` | `HistoryChartViewTests.cs`, `HistoryPageTests.cs` |
| AC8 | `HistoryViewTests.PickSnapsToTheNearestMarkerWithinTolerance`, `PickTakesTheEarlierMarkerOnATie`, `PickUsesThePointerTimeWithoutMarker`, `PickFloorsToTheSecond`; `HistoryChartEventsTests.PickIgnoresInvalidValues`; `HistoryChartViewTests.FromMarksTheMomentOnlyInsideTheRange`; `HistoryPageTests.HistoryPageClickReplacesEntryAndReadsOnlySnapshots`, `HistoryPageMomentShowsTheLogsLink` | `HistoryViewTests.cs`, `HistoryChartEventsTests.cs`, `HistoryChartViewTests.cs`, `HistoryPageTests.cs` |
| AC9 | `SqliteStoreHistoryTests.SnapshotReadsTheClosestRowOfEachSide`, `SnapshotSeeksTheKindTimeIndex`, `SnapshotRefusesInvalidQuery`; `HistoryMomentReaderTests.ClosestPrefersNearerThenEarlierThenLowerId`, `ReadAsyncOffersTheSnapshotBefore`, `ReadAsyncReadsBothSidesWithinTenMinutes`; `ProcessSnapshotViewTests.FromListsTwentyProgramsAndProcessesByMemory`, `FromCountsMoreAndListedOfTotal`, `FromUsesPssOnlyWhenEveryEntryHasIt`, `FromNotesIncompleteWithoutTotal`, `FromEscapesCommandAndUser`; `HistoryPageTests.HistoryPageShowsNoSnapshotTexts` | `SqliteStoreHistoryTests.cs`, `HistoryMomentReaderTests.cs`, `ProcessSnapshotViewTests.cs`, `HistoryPageTests.cs` |
| AC10 | `ConnectionSnapshotViewTests.FromListsStatesTopProcessesAndRemotes`, `FromHeadsTheRemotesOfACountsOnlySnapshot`, `FromNotesIncompleteCounts`, `FromCountsMore` | `ConnectionSnapshotViewTests.cs` |
| AC11 | `HistoryPageTests.HistoryPagePrerenderedReadsNothing`, `HistoryPageRangeChangeCancelsTheChartRead`, `HistoryPageMomentChangeKeepsTheChartRead`, `HistoryPageDeadlineShowsTexts`, `HistoryPageFailureShowsTextAndLogsNoAddress`, `HistoryPageRefusedQueryShowsFailureText`; `GatedHistoryReaderTests.GatedHistoryReaderTakesOneGateSlotPerCall`; `HistoryChartViewTests.FromStaysWithinTheCaps` (64 lines of 1,999 times, 1,000 markers); `MainLayoutTests.MainLayoutMarksHistoryOnTheHistoryPage`; the existing `RoutesTests.RoutesEveryPageExceptLoginRequiresAuthorization` and `WebUiContentTests` cover the new page unchanged | `HistoryPageTests.cs`, `GatedHistoryReaderTests.cs`, `HistoryChartViewTests.cs`, `MainLayoutTests.cs` |

**Browser check (manual: Product Manager).** On a local build of the stamped head (`docker build -f deploy/backend/Dockerfile .` and the
compose file of `deploy/backend/`, or `dotnet run --project src/Vandox.Backend` with a test `vandoxd.yaml`), with a database that
holds agent data: drag of 5 px or more zooms and adds a history entry; a shorter press sets the moment and replaces the entry; a click
within 8 px of a kill marker takes its time; two axes, the right one gone below 480 px of chart width; dashed lines from the seventh;
the legend shows minimum, average and maximum at a step; the page at 360 px without horizontal scrolling. The orchestrator first asks
whether the Product Manager can build and run it; if not, the Reviewer's reading of `charts.js` (step 8) stands alone and the check
becomes an `after merge` check on the next release candidate, logged as such.

## Approach

**Store (`Vandox.Storage`).** A new interface `IHistoryReader`, implemented by `SqliteStore`, SQL in an internal `HistoryQueries`:

- `StoredMetricsAsync(limit)`: limit 1 to `StorageLimits.MaxStoredMetrics` (256), else `store: invalid query: limit must be 1 to
  256`. A skip scan of each table, `metrics` and `metric_rollups_1h`: the first entry is `SELECT source, min(name) FROM t WHERE source
  = (SELECT min(source) FROM t)`, each next one `SELECT min(name) FROM t WHERE source = $source AND name > $name`, and when that is
  `NULL` the first name of `(SELECT min(source) FROM t WHERE source > $source)`; at most `limit + 1` entries per table, each one index
  seek (fact 7). The two ordered lists are merged in ordinal order without repeats; the first `limit` are returned with `More` when a
  further entry exists. One reader lease for the whole call.
- `SnapshotAsync(query)`: `Kind` must be `process_snapshot` or `connection_snapshot` (`store: invalid query: kind must be a snapshot
  kind`), `Window` above 0 and at most one day (`store: invalid query: window must be positive and at most one day`), `At` within the
  storable range (`store: invalid query: at must be within the storable range`). `AtOrBefore`: `kind = $kind AND captured_at <= $at
  AND captured_at >= $at - window ORDER BY captured_at DESC, id LIMIT 1`; `After`: `captured_at > $at AND captured_at <= $at + window
  ORDER BY captured_at, id LIMIT 1` (bounds clamped to the storable range), returned as a `StoredRecord` through the existing record
  row reading, so the payload is decoded once.

**Gate.** `GatedHistoryReader` (Backend, `History/`) takes one slot of `WebReaderGate` per call, as `GatedOverviewReader` does;
registered in `ServeCommand` as the singleton `IHistoryReader` next to the other gated readers.

**Address (`HistoryAddress`).** Parsed from `new Uri(Navigation.Uri).Query` with `QueryHelpers.ParseQuery`, in the order `metric`,
`range`, `from`, `to`, `at`; the first error wins, then the combination rules in the order of E1 (both preset and time, one time only,
start not before end). An empty value is absent, as in the logs view; an empty `metric` value is skipped. A metric value splits at its
one comma into source and name, each 1 to 128 bytes matching `^[A-Za-z0-9][A-Za-z0-9._:/@+-]*$`; distinct metrics keep the order of
their first appearance; a ninth distinct one is `The address names more than 8 metrics.` Times parse as in `LogsAddress` (both formats,
storable range, UTC). `Range(now)` gives `from`/`to`, else the preset (default `OneDay`) up to `now`. `ToQuery` writes the parameters
in the same order with `LogsAddress.FormatTime`, `range` only when a preset is set, and percent-encodes every value. `SameChart`
compares metrics, preset, `from` and `to`, so the page tells a moment change from a chart change.

**Page `HistoryPage` (`/history`).** Two `PageRunner`s (chart, moment), each with the 10-second deadline and the texts of E14,
logging `history view: reading failed` (`BackendLog.HistoryReadFailed`, event 75, error level, the exception only). Prerendered it shows
`Loading…`. Interactive, on the first render: the chart component reports its width, then the chart action reads the metric list
(first load only) and, when the address has metrics and the range does not begin at or after now, the time-series query with
`HistoryView.Target(width)`; a range that begins at or after now shows `This range lies in the future.` and reads nothing. The query is
wrapped so that an `InvalidQueryException` becomes an `InvalidOperationException` (fact 19), which shows the failure text and is logged.
`LocationChanged` re-parses the address: a chart change (`SameChart` false) starts a chart action, which cancels the running one; a
changed `At` alone starts a moment action only. Navigation: metric add or remove, a preset, *From*/*To* and a selection call
`NavigateTo("history" + query)` (a new entry); a click, an event time and *Snapshot before this moment* call
`NavigateTo(…, replace: true)`. The *From*/*To* fields are validated with the address rules and show the address texts without
navigating.

**Chart.** `HistoryChartView.From(result, metrics, range, moment)` builds what the chart and the event list show: one line per
`SeriesLine` in result order, its `Scale` the index of its metric in the address, `Label` `<source> <name> <labels>` (labels as stored,
left out when empty) through `LogText.Escape(…, 64)`, colors `index % 6`, `Dashed` from index 6 on; values the average at a step and
the value at raw, `null` for a gap, bridged flags for between samples; `Minimums`/`Maximums` only at a step. `Axes` holds the escaped
`<source> <name>` of the first two metrics. Marks: each marker with kind, time and, for a backup run, its end (`EndsAt`, or the range
end while running or unknown). Events: time, kind text and text runs (`LogText.Runs(…, 256, 16)`): kill `<command> (PID <pid>)`,
boot the boot ID, restart the unit, backup `<command> until <time> UTC`, `<command>, running` or `<command>, end unknown`. Notes in the
order E3, E4, E5, E6, E7; `StepNote` with `HistoryView.StepText(step)` (`<n> minutes`, `1 hour`, `<n> hours`, `1 day`, `<n> days`).
`Moment` is set only inside the range.

`HistoryChart` (component) owns a `HistoryChartInterop` and a `DotNetObjectReference<HistoryChartEvents>`. On the first render it
calls `width(host)` of `js/charts.js` and raises `OnWidth`; each new view is drawn by `createHistory(host, setup, columns, events)`
(the old chart destroyed first, since the number of series changes), a moment change by `chart.mark(seconds or null)`. A lost
connection follows record 0200 as `ChartInterop` does. `HistoryChartEvents` receives `Select(startSeconds, endSeconds)` and
`Pick(atSeconds, toleranceSeconds)` from the browser; it drops a value that is not finite, outside the storable range, a selection not
ascending or a tolerance outside 0 to one day, and passes the rest on as `HistoryRange` (widened outward to whole seconds,
`HistoryView.Selection`) or `HistoryPick`. The page snaps a pick with `HistoryView.Pick(markers, pick)`: the marker whose time is
nearest within the tolerance, the earlier on a tie, else the pointer time, floored to the second.

`charts.js` gains `width` and `createHistory`: one scale per metric (`y0` … `y7`), axes for scales 0 (left) and 1 (right; hidden while
the plot is narrower than 480 CSS px, re-checked on resize), series stroke from `--vandox-chart-<color+1>` and `dash` for dashed lines,
`cursor.drag` `{ x: true, y: false, dist: 5, setScale: false }` with a `setSelect` hook that sends the selection's start and end, a
`mousedown`/`mouseup` pair on the plot's over element that sends a pick when the pointer moved less than 5 px (with the time span of 8
px as tolerance), a `draw` hook for the marks (vertical lines in the ink color, kills solid, boots dashed, restarts dotted, backup runs
a shaded span) and the moment (a line in the brand color), and legend values that show `min / avg / max` at a step. Nothing in it
writes HTML; the scan rules of *Limits and errors* in `docs/areas/web-ui.md` apply.

**Moment.** `HistoryMomentReader.ReadAsync(at)` makes four calls (`AtOrBefore` and `After` for each kind, window 10 minutes) and
keeps per kind `Closest(before, after, at)`: the nearer, the before one on equal distance (records of one capture time are already
ordered by ID in the read). When the process snapshot shown is the after one and a before one exists, `ProcessBefore` is its time.
`ProcessSnapshotView.FromProcesses` and `ConnectionSnapshotView.FromConnections` build the panel: lists sorted by the memory key (PSS when every entry of
that list has one, else RSS) or count, descending, ties by name or PID; at most 20 rows; `and <n> more programs`/`processes`/`more`
from the list lengths; `<listed> of <total> processes are listed.` when `Total` is above the listed count; the notes of E9; the
remotes heading of S8 when `AggregatesOnly`. Text cells through `LogText.Runs(…, 256, 16)`, memory through `OverviewText.Size`, CPU
with one decimal. Only the views are kept; the decoded record is dropped after the build.

**Layout** (order fixed, styling free for the Tester, approved in step 9): heading *History*; presets (the current one marked), *From*,
*To* and *Apply*; the metric list (a select with *Add*) and the chosen metrics, each with *Remove*; the address or list texts; the notes
of E3 to E7 and the step note as paragraphs; the chart; *Events* as a table (time as a button, kind, text); the moment panel: heading
`Moment <time> UTC`, the link *Logs of this moment* (`IncidentsView.LogsHref`), *Process snapshot* with its heading line, the two tables
and notes, then *Connection snapshot* likewise. Without metrics only the controls and `Choose one or more metrics to draw.`

**Memory per circuit** (for the area document): chart data at most 64 lines × 1,999 times × 3 doubles (about 3 MiB) plus the result
while it is built (about 9 MiB of points); 1,000 events of 256 characters in 16 runs (about 1.5 MiB); 256 metric keys; one decoded
snapshot (bounded by the 16 MiB payload) while a panel is built, then lists of 20 rows. The Dev states the measured figures.

## Options considered

- **Chart:** two drawn axes and legend extremes against eight axes, one axis per unit, small multiples, bands or averages only — record
  0221.
- **Moment and metric list:** own bounded reads against links to the future views, the existing record read and `SELECT DISTINCT` —
  record 0222.
- **Snapping in the browser** (the chart finds the nearest marker itself): keeps one round trip less, but puts the tie rule into an
  untested script; the server gets the pointer time and the tolerance instead.
- **The existing `TimeSeriesChart`/`ChartInterop`** extended with scales and callbacks: its single-axis charts in the incidents and
  logs views would change with it; a component of its own leaves them untouched.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `IHistoryReader.cs`, `StoredMetricList.cs`, `SnapshotQuery.cs`, `SnapshotSide.cs`, `HistoryQueries.cs` | new |
| Vandox.Storage | `SqliteStore.cs`, `StorageLimits.cs` | implements `IHistoryReader`; `MaxStoredMetrics` |
| Vandox.Backend | `History/` (`GatedHistoryReader`, `HistoryAddress`, `HistoryPreset`, `HistoryRange`, `HistoryPick`, `HistoryView`, `HistoryChartView`, `HistoryLine`, `HistoryMark`, `HistoryEventRow`, `HistoryMomentReader`, `HistoryMoment`, `ProcessSnapshotView`, `ConnectionSnapshotView`, `SnapshotTableView`, `SnapshotRowView`) | new |
| Vandox.Backend | `Components/Charts/HistoryChart.razor(.cs)`, `HistoryChartInterop.cs`, `HistoryChartEvents.cs` | new |
| Vandox.Backend | `Components/Pages/HistoryPage.razor(.cs)` | new |
| Vandox.Backend | `Components/Layout/MainLayout.razor`, `Hosting/ServeCommand.cs`, `Hosting/BackendLog.cs`, `wwwroot/js/charts.js`, `wwwroot/app.css` | nav entry, registration, log method, chart module, styles |

## Signatures (for the Dev's skeleton)

All public unless marked. A data class is written as one declaration that lists its properties (type and name); the accessors follow
the note after it.

Vandox.Storage:

- `interface IHistoryReader`
- `Task<StoredMetricList> StoredMetricsAsync(int limit, CancellationToken cancellationToken)` (on IHistoryReader)
- `Task<StoredRecord?> SnapshotAsync(SnapshotQuery query, CancellationToken cancellationToken)` (on IHistoryReader; both implemented by SqliteStore and GatedHistoryReader)
- `class StoredMetricList { IReadOnlyList<SeriesKey> Metrics; bool More }` (sealed; init, Metrics defaults to empty)
- `class SnapshotQuery { string Kind; DateTimeOffset At; TimeSpan Window; SnapshotSide Side }` (sealed; init)
- `enum SnapshotSide { AtOrBefore, After }`
- `StorageLimits.MaxStoredMetrics = 256` (a public integer constant)
- `class HistoryQueries` (internal static; its statement constants are read by the query plan tests)

Vandox.Backend, namespace Vandox.Backend.History:

- `GatedHistoryReader(IHistoryReader inner, WebReaderGate gate)` (sealed class, implements IHistoryReader)
- `enum HistoryPreset { OneHour, OneDay, SevenDays, ThirtyDays, OneYear }`
- `HistoryRange(DateTimeOffset start, DateTimeOffset end)` (sealed class with get-only Start and End)
- `HistoryPick(DateTimeOffset at, TimeSpan tolerance)` (sealed class with get-only At and Tolerance)
- `class HistoryAddress { IReadOnlyList<SeriesKey> Metrics; HistoryPreset? Preset; DateTimeOffset? From; DateTimeOffset? To; DateTimeOffset? At; string Error }` (sealed; get and set, Metrics empty and Error empty by default)
- `static HistoryAddress Parse(string query)`
- `HistoryAddress Copy()`
- `HistoryRange Range(DateTimeOffset now)`
- `string ToQuery()` (throws InvalidOperationException for an address with an error)
- `bool SameChart(HistoryAddress other)`
- `static class HistoryView` (one public string constant per text of the spec, names free for the Dev; the integer constants MaxMetrics 8, MaxTarget 1000, ListRows 20, LabelChars 64, TextChars 256, TextRuns 16; a static read-only MomentWindow of 10 minutes)
- `static string PresetValue(HistoryPreset preset)` (1h and so on)
- `static string PresetLabel(HistoryPreset preset)` (1 h and so on)
- `static TimeSpan PresetSpan(HistoryPreset preset)`
- `static string StepText(TimeSpan step)`
- `static int Target(double chartWidth)`
- `static HistoryRange? Selection(double startSeconds, double endSeconds)`
- `static DateTimeOffset Pick(IReadOnlyList<SeriesMarker> markers, HistoryPick pick)`
- `static IReadOnlyList<SeriesKey> MetricOptions(StoredMetricList list)`
- `static string HistoryHref(HistoryAddress address)`
- `class HistoryLine { string Label; int Scale; int Color; bool Dashed; IReadOnlyList<double?> Values; IReadOnlyList<bool> Bridged; IReadOnlyList<double?> Minimums; IReadOnlyList<double?> Maximums }` (sealed; init)
- `class HistoryMark { MarkerKind Kind; DateTimeOffset At; DateTimeOffset? EndsAt }` (sealed; init)
- `class HistoryEventRow { DateTimeOffset At; string Kind; IReadOnlyList<TextRun> Text }` (sealed; init)
- `class HistoryChartView { IReadOnlyList<DateTimeOffset> Times; IReadOnlyList<HistoryLine> Lines; IReadOnlyList<string> Axes; IReadOnlyList<HistoryMark> Marks; IReadOnlyList<HistoryEventRow> Events; IReadOnlyList<string> Notes; string StepNote; DateTimeOffset? Moment }` (sealed; init)
- `static HistoryChartView From(TimeSeriesResult result, IReadOnlyList<SeriesKey> metrics, HistoryRange range, DateTimeOffset? moment)` (on HistoryChartView)
- `class SnapshotRowView { IReadOnlyList<IReadOnlyList<TextRun>> Cells }` (sealed; init)
- `class SnapshotTableView { string Heading; IReadOnlyList<string> Columns; IReadOnlyList<SnapshotRowView> Rows; string More }` (sealed; init)
- `class ProcessSnapshotView { string Heading; SnapshotTableView Programs; SnapshotTableView Processes; string ListedOfTotal; string Note }` (sealed; init)
- `static ProcessSnapshotView FromProcesses(StoredRecord record)` (on ProcessSnapshotView)
- `class ConnectionSnapshotView { string Heading; SnapshotTableView States; SnapshotTableView Processes; SnapshotTableView Remotes; string Note }` (sealed; init)
- `static ConnectionSnapshotView FromConnections(StoredRecord record)` (on ConnectionSnapshotView)
- `class HistoryMoment { DateTimeOffset At; ProcessSnapshotView? Process; DateTimeOffset? ProcessBefore; ConnectionSnapshotView? Connection }` (sealed; init)
- `HistoryMomentReader(IHistoryReader reader)` (sealed class)
- `Task<HistoryMoment> ReadAsync(DateTimeOffset at, CancellationToken cancellationToken)` (on HistoryMomentReader)
- `static StoredRecord? Closest(StoredRecord? atOrBefore, StoredRecord? after, DateTimeOffset at)` (on HistoryMomentReader)

Vandox.Backend, namespace Vandox.Backend.Components.Charts:

- `HistoryChartEvents(Func<HistoryRange, Task> selected, Func<HistoryPick, Task> picked)` (sealed class)
- `Task Select(double startSeconds, double endSeconds)` (JSInvokable, on HistoryChartEvents)
- `Task Pick(double atSeconds, double toleranceSeconds)` (JSInvokable, on HistoryChartEvents)
- `HistoryChartInterop(IJSRuntime jsRuntime)` (sealed class, asynchronously disposable)
- `Task<double> WidthAsync(ElementReference host, CancellationToken cancellationToken)`
- `Task<bool> RenderAsync(ElementReference host, HistoryChartView view, DotNetObjectReference<HistoryChartEvents> events, CancellationToken cancellationToken)`
- `Task MarkAsync(DateTimeOffset? moment, CancellationToken cancellationToken)`
- `class HistoryChart` (sealed partial component, asynchronously disposable; parameters View of type HistoryChartView, and the event callbacks OnWidth with a double, OnSelect with a HistoryRange and OnPick with a HistoryPick)

Vandox.Backend, other:

- `class HistoryPage` (sealed partial component in Components.Pages, route /history, disposable; injects the time-series service, the history reader, the clock, the navigation manager and its logger)
- `static partial void HistoryReadFailed(this ILogger logger, Exception exception)` (internal, on BackendLog, event 75)

Checked against *Writing code*: no nested type, no parameter named after a keyword of another .NET language, at most four parameters,
no enum whose name ends in Flags or Enum; no generic type, so no braces in a file name.

## Test files

`tests/Vandox.Backend.Tests`: `HistoryAddressTests.cs`, `HistoryViewTests.cs`, `HistoryChartViewTests.cs`, `HistoryChartEventsTests.cs`,
`HistoryChartInteropTests.cs`, `HistoryMomentReaderTests.cs`, `ProcessSnapshotViewTests.cs`, `ConnectionSnapshotViewTests.cs`,
`GatedHistoryReaderTests.cs`, `HistoryPageTests.cs` with `HistoryPageHarness.cs`, `FakeHistoryReader.cs` (scripted metric list and
snapshots, records calls, can hold or fail a call) and `ScriptedTimeSeriesReader.cs` (scripted lines, markers and failures behind a
real `TimeSeriesService`), and the existing `MainLayoutTests.cs` (a new test, the existing ones unchanged).
`tests/Vandox.Storage.Tests`: `SqliteStoreHistoryTests.cs`.

Existing test code that calls a changed signature: none (`SqliteStore` gains an interface; no existing member changes).

Files no unit test can cover: the additions to `src/Vandox.Backend/wwwroot/js/charts.js`, a browser script without a test runner,
already outside the coverage measure (record 0098, *Coverage exclusions*); no new exclusion. The Tester confirms or contests this in its
report.

## Areas

- New area *History view*, `docs/areas/web-ui-history-view.md`: the page `/history` with its address, controls, chart, markers and event
  list, the moment panel, texts, calls, deadline and memory per circuit. Index row: scope as above; not here: the query (time-series
  queries), the store reads (storage reading), the general web UI and login.
- `web-ui.md`, `time-series-query.md`, `storage-reading.md`: see *Documentation updates*.

## Documentation updates

All by the Dev after the code is green, unless marked.

- `docs/areas/web-ui-history-view.md` (new) and its row in `docs/areas/README.md` (index block).
- `docs/areas/web-ui.md`: *Scope* "history views are planned" becomes the link; *Layout and navigation* "four entries" becomes five
  with *History* (`history`, marked on `/history`); *Store reads of the views* adds the history view to "the overview page, the logs
  view, the incidents view and the report page" and its calls; *Charts* gains several scales, the drawn axes, selection, click, marks
  and legend extremes; *Static assets* the `width`/`createHistory` exports of `js/charts.js`; *Related decisions* 0221;
  *Not here* and *Implementation* the new types.
- `docs/areas/time-series-query.md`: *Scope* "The history views of the web UI (planned)" and *Not here* "The views that call the query:
  planned" name the history view.
- `docs/areas/storage-reading.md`: *Scope* list and a section *History reads* (the metric list and the snapshot read, limits, errors,
  one seek per entry); *Related decisions* 0222; *Implementation* `IHistoryReader`, `HistoryQueries`.
- `docs/ARCHITECTURE.md`: lines 10-11 (the views that exist and read the store) and the views paragraph (lines 132-149) gain the
  history view with links to 0221 and 0222; the store interface list (lines 481-482) gains `IHistoryReader`.
- `README.md`: a section *Trace metrics over time* after *Explore the logs*, linking the area document.
- `CHANGELOG.md`: an *Added* entry **History view** (issue #49) under *Unreleased*.
- `.squad/project.md` (Dev reports the exact text, the orchestrator writes it): area 3 and 12 gain `HistoryPage`; area 10 adds the
  `/history` address and the chart callbacks next to the `/logs` and `/report` addresses, and the `web-ui-history-view` document.
- `.squad/project.md` *Test doubles* (Tester reports the text): `FakeHistoryReader`, `ScriptedTimeSeriesReader` (Backend tests).
- Records 0221 and 0222 (Lead, verbatim fix the orchestrator may apply): `Area:` from `Web UI` to `History view` once the index row
  exists.

## Architecture check

No guarantee of `.squad/project.md` changes: backfilled data still raises nothing (the view reads, it does not alert), a gap is never
drawn as data (lines break at gaps, record 0028), and no collector or agent path is touched. The page follows the web UI's rules: login,
read gate, deadline, external-text display, self-only policy (no new file, no inline script or style).

## Security considerations

- [x] Limits on parsed input: the address parts (1 to 128 bytes of a metric part) count UTF-8 bytes **after** percent-decoding, as the
  logs view does; at most 8 distinct metrics after decoding. The chart callbacks carry numbers only; non-finite or out-of-range values
  are dropped before any use. The browser's message size stays the framework's 32 KB.
- [x] Exceptions that reach the page: none as their message; the store's errors and an `InvalidQueryException` (wrapped) show the fixed
  failure texts of E14, the deadline its text, and the log holds the exception only, never an address value.
- External text (labels, marker texts, programs, users, commands, addresses) reaches the page only through `ExternalText` runs and the
  chart only through `LogText.Escape`; `charts.js` sets labels as text and writes no HTML.
- Links: `history` plus `HistoryAddress.ToQuery` of validated values, and `IncidentsView.LogsHref`; no address is computed from browser
  input without validation.

## Decision records

- `docs/decisions/0221-the-history-view-draws-every-metric-on-its-own-scale-with-two-axes-and-step-extremes-in-the-legend.md` (Proposed)
- `docs/decisions/0222-the-history-view-reads-the-moments-snapshots-and-the-metric-list-through-its-own-bounded-reads.md` (Proposed)

## Out of scope / follow-ups

As the spec's *Out of scope*: self-healing markers (#71), the gap list (#54), the processes (#50) and network (#51) views, bands, touch
drag, automatic refresh and links into the view. No follow-up issue is needed beyond those that exist.

## Challenge notes

Devil's Advocate, mode plan: feature 49, history view (specs/feature-49-history-view)

The plan holds up. I found no major objections and 7 minor ones.

**Assumptions I checked**
- **Fact 7 (index seeks) holds.** I recreated the schema DDL from `src/Vandox.Storage/SchemaMigrator.cs:40-68,296-326` in Python's sqlite3 (SQLite 3.53.1) and ran EXPLAIN QUERY PLAN on the plan's statements:
  - Every metric skip-scan statement is a `SEARCH`. On `metrics` it uses `metrics_source_name_time` (covering). On `metric_rollups_1h` it reports `USING PRIMARY KEY`.
  - With 2,000,000 rows of one source, every statement ran a constant 20 to 38 VM steps (counted with a progress handler). That includes the bare-column form `SELECT source, min(name) … WHERE source = (SELECT min(source) …)`. So each statement really is one seek.
  - Both snapshot statements are `SEARCH records USING INDEX records_kind_time`. The AtOrBefore one also reports `USE TEMP B-TREE FOR LAST TERM OF ORDER BY`, because it orders `captured_at DESC, id` with id ascending.
- **Fact 16 (first JSInvokable) holds.** There is no `JSInvokable` or `DotNetObjectReference` in `src`. `ServeCommand.cs:263` sets no `CircuitOptions`, so the defaults apply.
- **Facts 4, 14, 19 and 20 hold.** No metric-listing read exists among the store interfaces. uPlot's `mouseUp` (`uPlot.esm.js:5833-5848`) calls `setSelect` only when there is a selection and it changed. `PageRunner.cs:111-118` and `IncidentsView.cs:218-232` match the plan.
- **Need:** no `/history` page or nav entry exists yet (`MainLayout.razor:24-27`), so a no-change result does not apply.
- **Reuse:** I checked whether the existing `IRecordReader.RecordsAsync` could serve the snapshots. It only reads forward and decodes every record, so record 0222's reason for a new read stands.

**Minor objections** (instructions for the Dev, Tester and Reviewer)

1. **The metric list can be stuck behind the width report.** The plan reads the metric list only after the chart component reports its width (plan.md:98-99). Without metrics the page shows "only the controls" (plan.md:148). If the chart host is not rendered then, no width arrives, the list is never read, and no first metric can be added.
   - Dev: always render the chart host (empty) and take its width, or read the list on the first interactive render whatever the width.
   - Tester: add a `HistoryPage` test where an address without `metric` still reads the metric list and shows its options.

2. **A chart change and a moment change can arrive together.** plan.md:102-103 does not say what happens then. `at` is kept on range changes (E10) but replaced on clicks. So Back from an entry whose `at` was replaced can change both the chart and `at`. An `if chart … else if at …` build would leave the panel showing the wrong moment.
   - Dev: start the chart action when `SameChart` is false and the moment action whenever `At` differs, independently.
   - Tester: add a `HistoryPage` test for a location change that alters both.

3. **"Snapshot before this moment" can loop.** Agent capture times are sub-second (`internal/sysstats/sysstats.go:133` uses `run.Start`; `internal/scheduler/runner.go:399`). If the moment is set to the before-snapshot's time floored (E1), that snapshot is now *after* the moment. `AtOrBefore` then misses it, and the panel offers *Snapshot before this moment* again for an older snapshot.
   - Dev: set that moment to the snapshot time rounded **up** to the second, so `AtOrBefore` finds it.
   - Tester: write `ReadAsyncOffersTheSnapshotBefore` (or a page test) with sub-second capture times, and assert that following the button shows that snapshot without offering the button again.

4. **Name check and time parsing.**
   - The name rule is written with `$` (plan.md:90). In .NET, `$` also matches before a trailing `\n`, which would accept `%0A`. The model's pattern uses `\z` (`src/Vandox.Core/Model/Check.cs:298`), but it is internal to Vandox.Core and the Backend has no `InternalsVisibleTo`. Dev: write the new regex with `\z`.
   - "Times parse as in `LogsAddress`" (plan.md:91), but `LogsAddress.TryReadTime` is private (`src/Vandox.Backend/Logs/LogsAddress.cs:274`). Dev: make it internal and reuse it rather than copy it. This adds `LogsAddress.cs` to the affected files.

5. **Query-plan tests must match what SQLite actually reports.** In `StoredMetricsSeeksTheIndexes` and `SnapshotSeeksTheKindTimeIndex`:
   - accept `USING PRIMARY KEY` for `metric_rollups_1h`, a WITHOUT ROWID table;
   - accept `USE TEMP B-TREE FOR LAST TERM OF ORDER BY` for the AtOrBefore statement;
   - fail on any `SCAN`, and on a temp B-tree for the metric statements.

   Dev: do not change `id` to `DESC` to get a cleaner plan. Ascending `id` is what the spec's "lower record ID on a tie" requires.

6. **The new chart path must keep the documented chart guarantees** of `docs/areas/web-ui.md:71-74`:
   - times not strictly ascending, a length mismatch or a non-finite value (averages, minimums and maximums included) are refused before anything reaches the browser;
   - the chart is redrawn on a theme change with the same data.

   Dev: on `vandox-themechange` and a color-scheme change, `createHistory` must rebuild its scales, axes, hooks, marks and the moment, as `createChart`'s `build()` does (`charts.js` `build`). Tester: cover the refusal in `HistoryChartViewTests` or `HistoryChartInteropTests`.

7. **`HistoryView.Target` takes a value from the browser** (the width). It must return at least 1 for 0, negative, NaN or infinite widths, or the query is refused with `target must be 1 to 2000`. Tester: add those rows to `TargetIsTheChartWidthAtMostThousand`.

**Files**
- /home/user/Vandox/specs/feature-49-history-view/plan.md
- /home/user/Vandox/src/Vandox.Backend/Logs/LogsAddress.cs
- /home/user/Vandox/src/Vandox.Core/Model/Check.cs
- /home/user/Vandox/src/Vandox.Backend/wwwroot/js/charts.js
- /home/user/Vandox/docs/areas/web-ui.md
- Query-plan check scripts: /tmp/claude-0/-home-user-Vandox/7d628ade-087e-57b9-93c6-af0ce179da7a/scratchpad/eqp.py and /tmp/claude-0/-home-user-Vandox/7d628ade-087e-57b9-93c6-af0ce179da7a/scratchpad/timing.py

## Security notes

Security plan review: feature 49, History view. I read `specs/feature-49-history-view/spec.md` and `plan.md` at cfa5900.

The tier stays `standard`, and I agree with it. The plan adds no authorization, session, header or route change. `[Authorize]` comes from `Components/Pages/_Imports.razor`, and `RoutesTests` covers every routable page. The new `JSInvokable` methods are instance methods on a `DotNetObjectReference`, so only the page's own signed-in circuit can call them. Dropping values that are not finite or out of range is input handling. Areas 3, 10 and 12 are the right list, and I found no area the plan touches without listing it.

## Measurements I ran

1. **How .NET's `$` anchor treats a trailing newline.** I built a console project in the scratchpad (`/tmp/claude-0/-home-user-Vandox/7d628ade-087e-57b9-93c6-af0ce179da7a/scratchpad/rx/Program.cs`, run with `dotnet run`). It matches `"cpu_percent\n"` against the plan's pattern and against the same pattern ending in `\z`. Output: `$ : True  \z : False`.
2. **Query plans of the new statements (fact 7).** Script `/tmp/claude-0/-home-user-Vandox/7d628ade-087e-57b9-93c6-af0ce179da7a/scratchpad/eqp.py`, run with `python3 -I`. It uses the schema of `SchemaMigrator.cs` steps 2 and 14 on SQLite 3.53.1; the store's bundled e_sqlite3 may be a different version, so the Tester's planned test still decides. Every statement of the metric skip scan is a `SEARCH`, for example `SEARCH metrics USING COVERING INDEX metrics_source_name_time (source=? AND name>?)` and `SEARCH metric_rollups_1h USING PRIMARY KEY (source=? AND name>?)`. The `After` read is `SEARCH records USING INDEX records_kind_time (kind=? AND captured_at>? AND captured_at<?)`. The `AtOrBefore` read is the same `SEARCH` plus `USE TEMP B-TREE FOR LAST TERM OF ORDER BY` (see N3).

## Findings

1. [BLOCKING] **The metric name pattern accepts a trailing line feed.** plan.md, *Address*, says each part matches `^[A-Za-z0-9][A-Za-z0-9._:/@+-]*$`. In .NET, `$` also matches before a final `\n`, so `?metric=system,cpu_percent%0A` decodes through `QueryHelpers.ParseQuery` and passes (measurement 1). The value then counts as validated, contrary to E1 and N3. It goes into the bound query parameters, the E3 and E4 texts, the chosen-metric list, the axis labels, and the address that `ToQuery` writes back for sharing (S10). The C# validator of the same wire-format pattern already ends in `\z` (`src/Vandox.Core/Model/Check.cs:298`). None of the AC1 DataRows of `ParseRefusesInvalidValue` would catch this.
   - Smallest fix: in plan.md, *Address*, replace the pattern with `^[A-Za-z0-9][A-Za-z0-9._:/@+\-]*\z` (with `RegexOptions.CultureInvariant`).
   - Add the DataRows "a part ending in a line feed (`%0A`)" and "a part ending in `%0D`" to `ParseRefusesInvalidValue`.

2. [BLOCKING] **The bound of one decoded snapshot at a time does not hold for the planned moment reader.** Spec N2 and the plan's *Memory per circuit* both say "one decoded snapshot (bounded by the 16 MiB payload) while a panel is built".
   - `HistoryMomentReader.ReadAsync` makes the `AtOrBefore` and `After` calls for each kind. `SnapshotAsync` returns a full `StoredRecord` ("the payload is decoded once").
   - `Closest(StoredRecord? atOrBefore, StoredRecord? after, DateTimeOffset at)` then needs both records of a kind, so two decoded payloads of up to 16 MiB each are held at once. If the process record is kept while the connection reads run, more are held.
   - No planned test pins this bound; `FromStaysWithinTheCaps` covers only the chart.
   - Fix, either one:
     - (a) Read each side's `id` and `captured_at` without the payload, pick the closest, then read only that record (one decoded snapshot, as stated).
     - (b) Restate the bound truthfully in spec N2, in *Memory per circuit* and in the area document: "at most two decoded snapshots, the two sides of one kind; the first kind's views are built and its records dropped before the second kind is read".
   - With either fix, `HistoryMomentReaderTests` should get a test that pins the order: the process records are dropped before the connection reads start, for example by asserting on the fake's call sequence and the records it hands out.

3. [NON-BLOCKING] **Values from the browser can reach `ToQuery` unchecked.** Under *Page*, the select's *Add* value is browser-supplied: a circuit can send any string as the bound value. `HistoryAddress.ToQuery` throws only when `Error` is set; unlike `LogsAddress.ToQuery` (`src/Vandox.Backend/Logs/LogsAddress.cs`, which calls `Validate()`), it does not check the values again. A forged value would be percent-encoded into the address and refused when the address is parsed again, so nothing is injected. It still contradicts the plan's "no address is computed from browser input without validation".
   - Smallest fix: *Add* accepts only an entry of `HistoryView.MetricOptions(...)`, matched by index or ordinal equality. `ToQuery` checks the metric pattern, the count and the times, as `LogsAddress.ToQuery` does.
   - Add a test that sends a forged select value.
   - Recommendation: fix now.

4. [NON-BLOCKING] **The selection and pick values need a check before any conversion, and the callback reference needs disposal.**
   - `HistoryView.Selection` and `HistoryChartEvents.Pick` should test finiteness and the storable range on the `double` seconds before any `DateTimeOffset` conversion. They should test it again after widening outward with floor and ceiling, because a ceiling at the storable maximum can step outside it.
   - `HistoryChart.DisposeAsync` should dispose the `DotNetObjectReference`, and a callback that arrives after disposal should do nothing.
   - The plan should state that no static `[JSInvokable]` is added: a static one is callable by any circuit, signed in or not.
   - Smallest fix: one sentence each in plan.md, *Chart*. Add DataRows to `SelectionRefusesInvalidValues` and `PickIgnoresInvalidValues`: NaN, ±Infinity, ±1e300, the storable maximum minus 0.5 s as the end, and a negative tolerance. Add a `HistoryChart` test that a callback after disposal does nothing.
   - Recommendation: fix now.

5. [NON-BLOCKING] **The snapshot headings put the source outside the external-text display.** S7 and S8 headings embed `<source>` in a plain `string Heading` (plan, *Signatures*, `ProcessSnapshotView` and `ConnectionSnapshotView`). `docs/areas/web-ui.md`, *External text*, lists sources as external text, to be shown only through the display. Snapshot sources pass the wire name pattern at ingest today, so this is consistency, not an exploit.
   - Smallest fix: give each heading its source as `IReadOnlyList<TextRun> Source` built with `LogText.Runs(…, 256, 16)` and render it through `ExternalText`.
   - Recommendation: fix now.

6. [NON-BLOCKING] **The `AtOrBefore` statement sorts full rows.** `ORDER BY captured_at DESC, id` against `records_kind_time` needs `USE TEMP B-TREE FOR LAST TERM OF ORDER BY` (measurement 2). The sorter holds `SELECT`ed rows, including `data` of up to 16 MiB, for every snapshot that shares the first capture time. That is bounded and rare, but it is retained payload outside N2.
   - Fix 2(a) removes this: sort only `id, captured_at`. Otherwise mention it in the memory figure.
   - Recommendation: fix with finding 2.

## Checked without finding

- The `/history` address follows the logs view's parser. I checked key case (`ParseQuery` matches keys without regard to case), percent-encoded keys, `+` read as a space (`ToQuery` escapes it), and the fragment (dropped by `Uri.Query`).
- The times use the formats and storable range of `LogsAddress`; `at` is floored before any read.
- Error texts name the parameter, never the value. `InvalidQueryException` messages carry no values (`TimeSeriesService.cs:145-147`, `docs/areas/time-series-query.md`, *Errors*).
- `HistoryReadFailed` logs the exception only, through the JSON-line logger.
- Links are `history` plus `ToQuery`, or `IncidentsView.LogsHref`.
- Chart labels and axes go through `LogText.Escape(…, 64)`. uPlot writes labels with `textContent` and `nodeValue` (`uPlot.esm.js:3253, 5250`) and draws axis labels on the canvas.
- No inline style or script is needed, so the security headers stay unchanged.
- `MaximumReceiveMessageSize` and `DetailedErrors` are not overridden: `ServeCommand.cs:263` uses the defaults, so the 32 KB limit holds and exception details stay out of the browser.
- The gate keeps cross-user concurrency at two, and E15's cancellation leaves each circuit at most one chart action and one moment action.
