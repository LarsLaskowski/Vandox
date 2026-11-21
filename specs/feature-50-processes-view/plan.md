# Plan: Processes view

Source: Issue #50, [spec.md](spec.md)
Plan size: a new view of ten types, a page, a chart mode and a benchmark, with the panel-source rule and the write-side checks the plan challenge and the Security review added.
Tier: standard — a new read-only page behind the existing login, with its address parser and external-text display built from the existing guards; it creates, changes or removes no security control.
Security areas touched: 3 Web UI login (new page `/processes` under `Components/Pages/`, `[Authorize]` from `_Imports.razor`); 10 Parsing of external input (`ProcessesAddress`, the numbers the chart reports through `HistoryChartEvents`); 12 Logging and display of external data (program, user and command names in chart labels and tables, the log line `processes view: reading failed`)

## Problem / root cause

Feature: [spec.md](spec.md), built on #458's per-program rollups (record 0223).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The rollup read returns `Times`, top-*n* `Programs` with nullable points (average, minimum, maximum), `Others`, `Incomplete`, `MemoryKey`, `EarliestUnfolded`, `StartsBeforeBuckets`; no total row in the range is an empty `Times` | `ProgramRollupQueries.cs:102-158`, `ProgramRollupResult.cs` | confirmed |
| 2 | The source list returns sources by SQLite byte order with `LatestBucket`, at most 256 and `More` | `ProgramRollupQueries.cs:196-199`, `program-rollups.md` *Reading* | confirmed |
| 3 | `SeriesLadder.Choose` picks the smallest step with at most target steps overlapping, below one hour only while `from >= now - rollups_5m`, then whole days; it is internal | `src/Vandox.Storage/SeriesLadder.cs:25-34` | confirmed |
| 4 | The stacked chart refuses a gap and reports no drag or click; the history chart reports both and shows min/avg/max | `ChartData.cs:103-165`, `charts.js:69-175` and `:330-482` | confirmed |
| 5 | The stamp read filters by kind only; `records_kind_source_time (kind, source, captured_at)` exists | `HistoryQueries.cs:54-59,173-199`, `SchemaMigrator.cs:55` | confirmed |
| 6 | #458's incomplete rule (no program, or counts differ from `total`, or no `total` and not `complete`) is inline in the fold | `ProgramRollupFolder.cs:138` | confirmed |
| 7 | `HistoryChartEvents` validates `Select` and `Pick` and reports `HistoryRange` / `HistoryPick`; `HistoryView.Pick` with no markers floors the time | `HistoryChartEvents.cs`, `HistoryView.cs:277` | confirmed |
| 8 | The nav entries are *Overview*, *History*, *Logs*, *Incidents*, *Report*, each a `NavLink` with `Match.Prefix` | `MainLayout.razor:24-28` | confirmed |
| 9 | Event ID 78 is free; 75 is `history view: reading failed` | `BackendLog.cs:588`, grep of `EventId = 78` | confirmed |
| 10 | Agent snapshots come every 60 s (15 s under pressure), so a 5-minute step holds about five | `agent-system-metrics.md` *Processes* | confirmed |
| 11 | The benchmark tool is referenced by no test project, so the coverage gate finds no line of it | `tests/*/*.csproj`; `coverage-check.py:286-298` | confirmed |
| 12 | The three chart reads of a 30-day and a 1-year range finish within 10 s on the DS918+ | to verify: `--processes-view` of the storage benchmark on the DS918+ SSD volume, Product Manager, before step 9 (#458's one-year read took 1,270 ms there) | unverified |
| 13 | `HistoryPage` reads the moment again only when `at` changes and shows `Loading…` while no moment and no failure is held; it reads the chart only once a width is known, guarded by `SameChart` | `HistoryPage.razor.cs:332,378-404,581-587`, `HistoryPage.razor:139-146` | confirmed |
| 14 | `HistoryChartView.WithMoment` copies each property by name; the chart host's `aria-label` is fixed to `History chart` | `HistoryChartView.cs:103-118`, `HistoryChart.razor:1` | confirmed |
| 15 | `HistoryAddress.ToQuery` validates again, throws `InvalidOperationException` and escapes the source; `SeriesLadder.DayStep` divides by `target`, which only `TimeSeriesService` bounds (1 to `MaxTarget`, 2,000) | `HistoryAddress.cs:138-152`, `SeriesLadder.cs:52-61`, `TimeSeriesService.cs:24,147` | confirmed |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `ProcessesChartViewBuildsTheMemoryStackOfTheProgramsAndOthersInMiB`, `ProcessesChartViewLabelsThePssAxis`, `ProcessesChartViewAddsTheRssNoteForAnRssRange`; `ProcessesChartReaderReadsTenProgramsByMemoryAndByCpu`; `HistoryChartViewWithMomentKeepsStacked`; `HistoryChartLabelsTheHostWithItsLabel` (default and given); `ProcessesPageRendersBothChartHostsBeforeTheFirstRead`, `ProcessesPageReadsTheChartsOnceForBothWidths`, `ProcessesPageNamesEachChart` | `tests/Vandox.Backend.Tests/ProcessesChartViewTests.cs`, `ProcessesChartReaderTests.cs`, `HistoryChartViewTests.cs`, `HistoryChartTests.cs`, `ProcessesPageTests.cs`; manual: stacking drawn, Product Manager on a local publish of the branch head |
| AC2 | `ProcessesChartViewBuildsTheCpuStackWithoutNote` | `ProcessesChartViewTests.cs` |
| AC3 | `SeriesLadderChooseTakesTheSmallestStepWithinTheTarget`, `SeriesLadderChooseSkipsStepsBelowAnHourBeforeTheFiveMinutePeriod`, `SeriesLadderChooseRefusesATargetBelowOne` (0, -1), `SeriesLadderChooseRefusesATargetAboveTheMaximum`; `ProcessesChartReaderChoosesTheStepFromTheWidth`; `ProcessesChartViewCarriesMinimumAndMaximumPerProgramButNotForOthers`, `ProcessesChartViewWritesTheStepNote`; `HistoryChartInteropSendsTheStackedFlag` | `tests/Vandox.Storage.Tests/SeriesLadderTests.cs`, `ProcessesChartReaderTests.cs`, `ProcessesChartViewTests.cs`, `HistoryChartInteropTests.cs`; manual: legend, Product Manager |
| AC4 | `ProcessesAddressParse…` cases: each parameter valid, empty, repeated, invalid (source pattern, 129 bytes, a time), `compare` without `at`, error order; `ProcessesAddressToQueryWritesTheParametersInOrder`, `ProcessesAddressToQueryEscapesTheSource` (`a+b` round-trips), `ProcessesAddressToQueryRefusesAnInvalidSource`, `ProcessesAddressToQueryRefusesAnUnstorableCompare`; `ProcessesPageIgnoresAnUnlistedSourceChoice`, `ProcessesPageWritesANewEntryForPresetApplyDragAndSource`, `ProcessesPageDropsTheMomentsWhenTheSourceChanges`, `ProcessesPageShowsOnlyTheControlsOnAnAddressError`, `ProcessesPageReadsNothingForAFutureRange`; `HistoryAddressTests` unchanged | `ProcessesAddressTests.cs`, `ProcessesPageTests.cs` |
| AC5 | `ProcessesMomentReaderReadsTheClosestSnapshotOfTheSource`, `ProcessesMomentReaderOffersTheSnapshotBefore`; `SnapshotStampOfASourceSkipsOtherSources`, `SnapshotStampOfASourceSeeksTheKindSourceTimeIndex`, `SnapshotStampRefusesAnEmptySource`; `ProcessesPageClickSetsTheMomentAndReplacesTheEntry`, `ProcessesPageShowsTopProcessesWithTheHistoryLink`, `ProcessesPageSaysNoSnapshotWithinTenMinutes`; E12: `ProcessesPageReadsTheMomentOfTheAddressSourceAtOnce`, `ProcessesPageReadsTheMomentAgainWhenTheDefaultSourceChanges`, `ProcessesPageSaysLoadingUntilTheChartsChooseASource`, `ProcessesPageSaysNoSourceInTheRange`, `ProcessesPageSaysTheSourceCouldNotBeRead` (failure and deadline), `ProcessesPageShowsNoPanelForAFutureRange`; `HistoryPageTests` unchanged (shared panel) | `ProcessesMomentReaderTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreHistoryTests.cs`, `ProcessesPageTests.cs` |
| AC6 | `ProgramComparisonViewOrdersByMemoryChangeThenName`, `…MarksNewAndGone`, `…CutsAtTwentyRowsWithNewAndGoneCounts`, `…UsesPssOnlyWhenBothSnapshotsHaveIt`, `…MarksNothingWhenASnapshotDoesNotListEveryProgram`; `ProcessSnapshotListsEveryProgram…` (empty, counts equal and unequal to `total`, no `total` with `complete` true and false); `ProcessesMomentReaderComparesTheEarlierPointAsBase`, `…SaysWhichPointHasNoSnapshot`, `…SaysBothPointsShowOneSnapshot`; `ProcessesPageSecondClickWritesCompare`, `ProcessesPageComparesAgainWhenTheDefaultSourceChanges` | `ProgramComparisonViewTests.cs`, `tests/Vandox.Core.Tests/ProcessSnapshotTests.cs`, `ProcessesMomentReaderTests.cs`, `ProcessesPageTests.cs`; `SqliteStoreProgramRollupTests` unchanged |
| AC7 | `ProcessesChartReaderDefaultsToTheSourceWithTheLatestBucket`, `…TakesTheFirstListedOnATie`, `…KeepsTheAddressSource`, `…NotesACutSourceList` | `ProcessesChartReaderTests.cs` |
| AC8 | `ProcessesChartViewLeavesEveryLineNullAtAGapStep`; one test per note E1, E3, E4, E5, each present exactly in its case and absent otherwise; `ProcessesChartViewAddsNoNoteForCutPrograms`; `ProcessesPageLeavesTheChartsOutWithoutData` | `ProcessesChartViewTests.cs`; manual: a gap breaks every band, Product Manager |
| AC9 | `GatedProgramRollupReaderTakesASlotPerCall`; `ServeSharesOneReaderGateBetweenLogsAndIncidents` extended: the registered `ProcessesChartReader` holds a `GatedProgramRollupReader` on the same `WebReaderGate` that wraps the store; `ProgramComparisonViewShowsProgramNamesAsExternalText` (a name with `\u202E` and `\u0007` gives escaped runs); `ProcessesPageShowsTheDeadlineText`, `…ShowsTheFailureTextAndLogsWithoutAddressValue`, `…EscapesProgramLabels`, `…ShowsSnapshotTextsAsExternalText` (both panels), `…CancelsReadsWhenLeft`; `RoutesProcessesPageRequiresAuthorization` | `GatedProgramRollupReaderTests.cs`, `ServeTests.cs`, `ProgramComparisonViewTests.cs`, `ProcessesPageTests.cs`, `RoutesTests.cs` |
| AC10 | `MainLayoutMarksProcessesOnTheProcessesPage` (with and without a query, not on `/history`); manual: `--processes-view` on the DS918+ SSD volume, Product Manager, before step 9, each median of five under 10,000 ms | `MainLayoutTests.cs`; `docs/BENCHMARKS.md` |

Manual checks run on a local publish of the branch head (`dotnet publish src/Vandox.Backend`) with a database the benchmark's `--processes-view` leaves, or the Product Manager's own.

## Approach

**Storage.** `SnapshotQuery.Source` (null: any source, as today): when set, `ReadStampAsync` uses two new statements with `AND source = $source`, which seek `records_kind_source_time`; `ValidateStamp` refuses an empty source (`source must not be empty`). `SeriesLadder` becomes public with an overload on plain times, which the internal one calls; it throws `ArgumentOutOfRangeException` for a `target` below 1 or above `TimeSeriesService.MaxTarget` (fact 15).

**Core.** `ProcessSnapshot.ListsEveryProgram()` holds the rule of fact 6; `ProgramRollupFolder` calls it (no behavior change).

**Address.** `History/AddressTimes` takes the reading, checks and writing of `range`, `from`, `to`, `at` out of `HistoryAddress` (whose public API and tests stay), and the name pattern check. `ProcessesAddress` reads `source`, `range`, `from`, `to`, `at`, `compare` (first error wins), then the range rules, then `The address needs both a moment and a second point for a comparison.`. `ToQuery` validates again as `HistoryAddress.ToQuery` does (fact 15): `Source` against the name pattern, `Compare` against the storable range and the rule above, throwing `InvalidOperationException`; it writes the parameters in that order, `source` through `Uri.EscapeDataString`.

**Charts.** `ProcessesChartReader.ReadAsync`: source list over the range; the source is the address's or the listed one with the latest bucket (first on a tie); the step from `SeriesLadder.Choose(start, end, now, HistoryView.Target(width), policy)`; two reads, *n* = 10. Three gated calls per chart action. `ServeCommand` registers `ProcessesChartReader` as a singleton over `new GatedProgramRollupReader(store, gate)` with the shared `WebReaderGate`. `ProcessesChartView.Build` makes a `HistoryChartView` with `Stacked = true` (which `WithMoment` copies): lines in rank order then `Other programs` (no minimum or maximum), labels through `LogText.Escape(…, 64)`, memory in MiB (bytes / 1,048,576), axis `Memory (PSS), MiB` / `Memory (RSS), MiB` / `CPU %`. Notes are decided only from the memory result: E1 when `Times` is empty, E3 when `StartsBeforeBuckets`, E4 when any `Incomplete` is above 0, E5 when `EarliestUnfolded` is before the range end.

**Script.** `createHistory` with `setup.stacked`: one scale from 0, cumulative sums in line order, each line filled down to the one below (bands); at a step where a line is null every cumulative value is null; the legend shows each line's own value with its extremes. `HistoryChartInterop` sends `stacked`; for a stacked view it also requires every line on scale 0. `createStacked`, `ChartData` and the incidents chart stay unchanged. `HistoryChart` takes a `Label` (default `History chart`) for its host's `aria-label`; the page passes `Memory per program` and `CPU per program`.

**Panels.** `HistoryMomentReader.ClosestAsync(kind, source, at)` takes the stamp logic of `ReadKindAsync`, which calls it. `ProcessesMomentReader.MomentAsync` gives the history view's process part for one source; `CompareAsync` reduces the earlier point's snapshot to `ProgramFigures` before the later one is read, so one decoded snapshot is held at a time. At most six gated calls per action.

**Panel source (spec E12).** The moment action is keyed on (panel source, `at`, `compare`) and runs again when any of them changes; a newer run supersedes the running one. The panel source is the address's `source`, so the action starts at once, or without it `ProcessesCharts.Source` of the charts shown, so it starts after a chart result and again when a range change moves the default. Without an address source: no chart result yet → `Loading…`; a result with an empty `Source` (no source in the range) → `ProcessesView.NoSourceText`; the chart runner's failure or deadline (its fail callback marks the source failed until the next chart action) → `ProcessesView.SourceFailureText`, below the chart's E9 text; the moment runner is cancelled in the last two. A future range or an address error shows no panel and reads nothing.

**Page layout** (`/processes`, heading `Processes`): presets, *From (UTC)*, *To (UTC)*, *Apply*, then *Source* (a select: the list, an address source missing from it added as chosen; its handler takes only a listed source or the address's own and ignores any other value); the control text; the notes as paragraphs in the order E1, `The list shows the first 256 sources.`, E3, E4, E5, step note; `h2` *Memory per program* with the RSS note below it, the chart; `h2` *CPU per program*, the chart. Both chart hosts render with `View` null before the first result and leave only for E1; the first width starts the chart action, the `SameChart` guard (fact 13) stops the second. Then `h2` *Top processes*: `Moment <time> UTC`, the link *History of this moment*, the button *Compare from here* (while armed: `Click a second point in a chart to compare.` and *Cancel*), the snapshot through `ProcessSnapshotPanel` (`HistoryPage.razor:149-174` moved into a component both pages use; the history page's output unchanged). With `compare`: `h2` *Comparison* in its place: `<earlier> UTC to <later> UTC (<source>)`, the table (Program, Memory (PSS) or (RSS) at each point, Change signed in binary units, CPU % at each point, State; `--` for an absent side), the closing line, the E11 note. Panel texts of E6 and E9 show in the panel.

**Deadline and log.** Two `PageRunner`s (chart, moment), each 10 s with the E9 texts, logging `ProcessesReadFailed` (event 78, error, exception only). Control handlers that differ from `HistoryPage`'s only in the address type stay per page; the *Sonar gate* on the draft pull request checks duplication on new code (Dev, before step 8), and a reported block moves into a shared helper.

**Memory per circuit** (derived from the fields, not measured): two results of 11 lines of at most 1,000 points (about 0.5 MiB) and two chart views (about 1 MiB); 256 sources; 20 rows per panel; one decoded snapshot (up to the 16 MiB payload limit plus its parsed document) and one `ProgramFigures` of the earlier snapshot (at most 4,096 programs of names up to 1,024 bytes, about 8.5 MiB at most), and while the comparison is built at most 8,192 rows before the cut to 20.

## Options considered

The seven options of [0224](../../docs/decisions/0224-the-processes-view-stacks-program-rollups-on-the-history-chart-and-compares-programs-of-one-source.md),
each with why it lost: `createStacked` with gaps, a third chart, snapshots while kept, the any-source panel, processes, and a click
that writes the source into the address (*Challenge*, M1). An address helper and a snapshot panel shared by both pages (chosen)
against copies (the quality gate's duplication condition).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `Model/ProcessSnapshot.cs` | `ListsEveryProgram` |
| Vandox.Storage | `SnapshotQuery.cs`, `HistoryQueries.cs`, `SeriesLadder.cs`, `ProgramRollupFolder.cs` | source filter, public step choice, shared rule |
| Vandox.Backend | `History/AddressTimes.cs` (new), `HistoryAddress.cs`, `HistoryChartView.cs`, `HistoryMomentReader.cs`, `ClosestSnapshot.cs` (new) | shared address part, stacked flag, closest read |
| Vandox.Backend | `Processes/` (new): `ProcessesAddress`, `ProcessesView`, `ProcessesCharts`, `ProcessesChartReader`, `ProcessesChartView`, `ProcessesMomentReader`, `ProgramFigures`, `ProgramFigure`, `ProgramComparisonView`, `GatedProgramRollupReader` | the view |
| Vandox.Backend | `Components/Pages/ProcessesPage.razor(.cs)`, `ProcessSnapshotPanel.razor(.cs)` (new), `HistoryPage.razor`, `Components/Charts/HistoryChart.razor(.cs)`, `HistoryChartInterop.cs`, `wwwroot/js/charts.js`, `Components/Layout/MainLayout.razor`, `Hosting/BackendLog.cs`, `Hosting/ServeCommand.cs` | page, chart, nav, log, registration |
| tools | `Vandox.StorageBenchmark/ProcessesViewBenchmark.cs` (new), `Program.cs` | `--processes-view` |

## Signatures (for the Dev's skeleton)

Properties are written as type and member, their type in prose; every other member as its declaration.

- Vandox.Core: `public bool ListsEveryProgram()` on `ProcessSnapshot`.
- Vandox.Storage: `SnapshotQuery.Source` (string?, init; null is any source); class `SeriesLadder` becomes public with `public static TimeSpan Choose(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now, int target, RetentionPolicy policy)` (throws ArgumentOutOfRangeException).
- Vandox.Backend, internal class `AddressTimes` (History): `AddressTimes.Preset` (HistoryPreset?), `AddressTimes.From`, `AddressTimes.To`, `AddressTimes.At` (DateTimeOffset?, get and set); `internal string ReadParameters(Dictionary<string, StringValues> values)`, `internal string ValidateRange()`, `internal string Validate()`, `internal void AppendTo(List<string> parts)`, `internal HistoryRange Range(DateTimeOffset now)`, `internal bool SameRange(AddressTimes other)`, `internal AddressTimes Copy()`, `internal static bool IsValidName(string? name)`, `internal static string Invalid(string name)`, `internal static string Repeated(string name)`.
- `HistoryChartView.Stacked` (bool, init). `HistoryChart.Label` (string, parameter, get and set, default `History chart`).
- Component `ProcessSnapshotPanel` (Components/Pages): `ProcessSnapshotPanel.Process` (ProcessSnapshotView?), `ProcessSnapshotPanel.Before` (DateTimeOffset?), `ProcessSnapshotPanel.OnBefore` (EventCallback of DateTimeOffset), each a parameter, get and set.
- Class `ClosestSnapshot` (History): `public ClosestSnapshot(StoredRecord record, DateTimeOffset? before)`, `ClosestSnapshot.Record` (StoredRecord), `ClosestSnapshot.Before` (DateTimeOffset?), both get-only.
- On `HistoryMomentReader`: `public Task<ClosestSnapshot?> ClosestAsync(string kind, string? source, DateTimeOffset at, CancellationToken cancellationToken)`.
- Class `ProcessesAddress` (Processes): `ProcessesAddress.Source` (string?), `ProcessesAddress.Preset` (HistoryPreset?), `ProcessesAddress.From`, `ProcessesAddress.To`, `ProcessesAddress.At`, `ProcessesAddress.Compare` (DateTimeOffset?), `ProcessesAddress.Error` (string), all get and set; `public static ProcessesAddress Parse(string query)`, `public ProcessesAddress Copy()`, `public HistoryRange Range(DateTimeOffset now)`, `public string ToQuery()` (throws InvalidOperationException), `public bool SameChart(ProcessesAddress other)`.
- Static class `ProcessesView`: the spec's texts as constants, `ProcessesView.Programs` (const int 10), `public static string ProcessesHref(ProcessesAddress address)`.
- Class `ProcessesCharts`: `ProcessesCharts.Sources` (IReadOnlyList of string), `ProcessesCharts.Source` (string), `ProcessesCharts.Memory` and `ProcessesCharts.Cpu` (HistoryChartView?), `ProcessesCharts.MemoryNote` (string), `ProcessesCharts.Notes` (IReadOnlyList of string), all init.
- Class `ProcessesChartReader`: `public ProcessesChartReader(IProgramRollupReader reader, RetentionPolicy policy, TimeProvider clock)`, `public Task<ProcessesCharts> ReadAsync(string? source, HistoryRange range, double chartWidth, CancellationToken cancellationToken)`.
- Static class `ProcessesChartView`: `public static HistoryChartView Build(ProgramRollupResult result, ProgramMeasure measure, HistoryRange range, DateTimeOffset? moment)`, `public static IReadOnlyList<string> Notes(ProgramRollupResult memory, ProgramSourceList sources, HistoryRange range, TimeSpan stepLength)`.
- Class `ProcessesMomentReader`: `public ProcessesMomentReader(IHistoryReader reader)`, `public Task<HistoryMoment> MomentAsync(string source, DateTimeOffset at, CancellationToken cancellationToken)`, `public Task<ProgramComparisonView> CompareAsync(string source, DateTimeOffset first, DateTimeOffset second, CancellationToken cancellationToken)`.
- `public readonly record struct ProgramFigure(double Rss, double? Pss, double Cpu)`.
- Class `ProgramFigures`: `public static ProgramFigures FromRecord(StoredRecord record)`, `ProgramFigures.CapturedAt` (DateTimeOffset), `ProgramFigures.Source` (string), `ProgramFigures.ListsEveryProgram` (bool), `ProgramFigures.Programs` (IReadOnlyDictionary of string to ProgramFigure), all init.
- Class `ProgramComparisonView`: `public static ProgramComparisonView Compare(ProgramFigures earlier, ProgramFigures later)`, `public static ProgramComparisonView Missing(string text)`, `ProgramComparisonView.Text`, `ProgramComparisonView.Heading`, `ProgramComparisonView.Closing`, `ProgramComparisonView.Note` (string), `ProgramComparisonView.Table` (SnapshotTableView), all init.
- `public GatedProgramRollupReader(IProgramRollupReader inner, WebReaderGate gate)`, implementing `IProgramRollupReader`.
- `internal static partial void ProcessesReadFailed(this ILogger logger, Exception exception)` (event 78).

No parameter is named after a keyword of another .NET language (CA1716), no type is nested, no member takes more than five parameters.

## Test files

As in *Tests per criterion*, all in `tests/Vandox.Backend.Tests/` unless named; helpers `ProcessesPageHarness.cs`, `FakeProgramRollupReader.cs` (Tester).

Existing test code: `FakeHistoryReader.Choose` filters by `SnapshotQuery.Source` when set (Tester); `HistoryChartInteropTests` gains the flag and keeps its cases (Tester); `ServeTests.ServeSharesOneReaderGateBetweenLogsAndIncidents` gains the rollup reader's assertions (Tester); `HistoryPageTests` stay unchanged and pin the panel's output for the history page. No signature in use changes.

Files no unit test can cover: `wwwroot/js/charts.js` (no browser test runner; excluded from both coverage measures already, record 0098; read in review and tried by hand); `tools/Vandox.StorageBenchmark/ProcessesViewBenchmark.cs` and `Program.cs` (a measurement for the reference host, in no coverage report, fact 11).

## Areas

- `docs/areas/web-ui-processes-view.md` (new, +1,800 words): area *Processes view*.
- `docs/areas/web-ui.md` (+150 words): views list, navigation, gate list, *Charts* stacked mode of the history chart.
- `docs/areas/web-ui-history-view.md` (+20 words): processes view leaves *Not here*.
- `docs/areas/storage-reading.md` (+40 words): the stamp read of one source.
- `docs/areas/program-rollups.md` (+10 words): *Not here* names the processes view; `ListsEveryProgram` in *Implementation*.

## Documentation updates

All by the Dev, once green:
- the five area documents above; `docs/areas/README.md`: index row of *Processes view* (scope: the page, address, charts, notes, panels, calls, caps; not here: the reads, the chart in general, the history view);
- `docs/ARCHITECTURE.md`: the views list (line 10-11), a sentence after the history view (line 139-144) linking 0224, `IHistoryReader` (line 488) "of any source or of one";
- `README.md`: a section *See which programs use the memory* after *Trace metrics over time*;
- `CHANGELOG.md`: entry *Processes view* (issue #50);
- `docs/BENCHMARKS.md`: section *Processes view* with procedure and results table (the Product Manager's figures);
- `.squad/project.md`, reported to the orchestrator as exact text: area 3 "(among them `HistoryPage`, `/history`, and `ProcessesPage`, `/processes`)"; area 10 after the `/history` addresses "the `/processes` address (`ProcessesAddress`: source, range, moment and second point)"; area 12 after `HistoryChart`'s parenthesis "and `ProcessesPage` (program labels through `LogText.Escape`, snapshot texts through `ExternalText`, the log line `processes view: reading failed` without an address value)"; *Test doubles*, row *database (C#)* after `FakeHistoryReader`'s parenthesis ", `FakeProgramRollupReader` (Backend tests: scripted rollup results and source lists, records calls, can hold or fail a call)", row *Blazor (C#)* after `HistoryPageHarness`'s parenthesis "and `ProcessesPageHarness` (processes page over `FakeProgramRollupReader`, `FakeHistoryReader`, a fake clock, a recording navigation manager and a script runtime that answers the widths of both charts)";
- `web-ui-processes-view.md` states *Memory per circuit* of *Approach* in full, the `ProgramFigures` bound and the 8,192 rows included, and the panel source of E12.

## Architecture check

No guarantee of `.squad/project.md` is weakened: the page is behind the login on the web listener, reads only, through the shared gate and its deadline, with no new listener, route outside Blazor or option.

## Security considerations

- [x] `source`: 1 to 128 bytes of the ASCII name pattern, checked after percent-decoding (ASCII only, so bytes equal characters); times in the logs view's fixed formats; the chart's numbers through `HistoryChartEvents` as today.
- [x] Exceptions: a store error, a refused read and a timeout reach the user only as the E9 texts; the log line carries the exception only.

## Decision records

- `docs/decisions/0224-the-processes-view-stacks-program-rollups-on-the-history-chart-and-compares-programs-of-one-source.md` (Proposed); `decision-check.py` fails on its area until the Dev's index row for *Processes view* exists.

## Out of scope / follow-ups

The spec's *Out of scope*; a stacked chart with gaps for the incidents view.

## Challenge

- M1 (panel source, major) — accepted, option (b): the moment action is keyed on (panel source, `at`, `compare`); the panel source
  is the address's `source` or the charts' default, so a range change that moves the default reads the panels again; the texts
  for no source in the range and for a failed chart action are spec E12 (amendment of AC5 and AC6), *Approach* *Panel source*,
  six page tests under AC5 and AC6. Option (a) lost: it still needs (b)'s rule for an `at` without `source` (a bookmark, a typed
  address) and for a failed chart action, it pins the source on the first click so the default stops following the latest
  source, and it changes the address the spec settled (S4); record 0224, option 7.
- Minor 1 (`Stacked` in `WithMoment`) — accepted: copied there; `HistoryChartViewWithMomentKeepsStacked`.
- Minor 2 (chart hosts before the first read) — accepted: both hosts render with `View` null, left out only for E1 once a
  result is known; one `SameChart` guard; `ProcessesPageRendersBothChartHostsBeforeTheFirstRead`,
  `ProcessesPageReadsTheChartsOnceForBothWidths`, `ProcessesPageLeavesTheChartsOutWithoutData`.
- Minor 3 (page duplication) — accepted: `ProcessSnapshotPanel` takes the process part of the history page's moment markup and
  serves both pages; the handlers that differ only in the address type stay per page, and the *Sonar gate* on the draft pull
  request decides (Dev, before step 8).
- Minor 4 (accessible name) — accepted: `HistoryChart.Label`, default `History chart`; `HistoryChartLabelsTheHostWithItsLabel`,
  `ProcessesPageNamesEachChart`.
- Minor 5 (test doubles) — accepted: the *Test doubles* text for `.squad/project.md` is in *Documentation updates*.

## Security review

- 1 (gate wiring of the rollup reader) — taken: `ServeCommand` registers `ProcessesChartReader` over
  `GatedProgramRollupReader(store, gate)`; `ServeSharesOneReaderGateBetweenLogsAndIncidents` asserts the same gate and the
  wrapped store, under AC9.
- 2 (write-side checks) — taken: `ToQuery` checks `Source` and `Compare` again and throws as `HistoryAddress.ToQuery`, escapes
  `source`; the select handler takes only a listed source or the address's own; `ProcessesAddressToQueryEscapesTheSource`,
  `…RefusesAnInvalidSource`, `…RefusesAnUnstorableCompare`, `ProcessesPageIgnoresAnUnlistedSourceChoice` under AC4. The accepted
  forms of `source` are unchanged (the review's list), and the write side now holds the same pattern.
- 3 (comparison names as external text) — taken: `ProgramComparisonViewShowsProgramNamesAsExternalText` (`\u202E`, `\u0007`)
  and the page test names both panels, under AC9.
- 4 (size of `ProgramFigures`) — taken: *Memory per circuit* and the area document text in *Documentation updates*.
- 5 (`SeriesLadder.Choose` target) — taken: `ArgumentOutOfRangeException` below 1 and above `TimeSeriesService.MaxTarget`;
  `SeriesLadderChooseRefusesATargetBelowOne`, `…AboveTheMaximum` under AC3.
- Not raised (label cut marker, a program named `Other programs` with U+00A0): no change in this plan.
