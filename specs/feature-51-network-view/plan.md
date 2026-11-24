# Plan: Network view

Source: Issue #51 | [spec.md](spec.md)
Tier: standard — a new page behind the existing login, over existing and one new bounded read; it creates, changes or removes no security control.
Security areas touched: 3 (Web UI login: a new page under `Components/Pages/`), 10 (parsing of external input: the `/network` address, `NetworkAddress`), 12 (display of external text: commands, user names, addresses, sources on `NetworkPage`)

## Problem / root cause

Feature: [spec.md](spec.md).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The rollup reads return states (ordinal name order, `UDP` for udp/udp6), 10 programs, `Others` (null at a gap), `Incomplete`, `EarliestUnfolded`, `StartsBeforeBuckets`; addresses with `Average`, `Maximum`, `Further`, `Gaps`, `Capped` | `src/Vandox.Storage/ConnectionRollupResult.cs`, `ConnectionAddressResult.cs`, `ConnectionRollupFolder.cs:34,178`; `docs/areas/connection-rollups.md`, *Reading* | confirmed |
| 2 | No page reads the rollups; nothing registers `IConnectionRollupReader` | `connection-rollups.md` (*Reading*, last item); `Hosting/ServeCommand.cs:240-262` | confirmed |
| 3 | Points are `ProgramRollupPoint?`, the type of the processes view's lines | `ConnectionRollupLine.cs`; `Processes/ProcessesChartView.cs` (`BuildLine`) | confirmed |
| 4 | The stamp read's window is at most one day, inclusive `[at − window, at]` on the *at or before* side, optionally per source | `HistoryQueries.cs:54-69,186-208`; `storage-reading.md:160-165` | confirmed |
| 5 | `records_kind_source_time ON records(kind, source, captured_at)` exists | `SchemaMigrator.cs:55` | confirmed |
| 6 | `ListenerComparer.Compare` caps new listeners at 64, treats a missing `inventory` as not comparable, and has the agent's key, ephemeral-UDP and privileged rules | `src/Vandox.Core/Reports/ListenerComparer.cs:64-100,197-247` | confirmed |
| 7 | `processes` counts a socket once per holding process | `internal/connstats/owners.go:172-179`; `connection-rollups.md`, *Programs* | confirmed |
| 8 | Event IDs end at 80 | `Hosting/BackendLog.cs:635` | confirmed |
| 9 | `SeriesLadder.Choose` gives the processes view's steps from 5 minutes, which the rollup reads accept | `ProcessesChartReader.cs`; `SeriesLadder.cs:37`; `connection-rollups.md`, *Reading* | confirmed |
| 10 | `ProcessSnapshotView` and `ConnectionSnapshotView` hold identical private `Runs`, `Plain`, `Row` | `History/ProcessSnapshotView.cs:123-156`; `ConnectionSnapshotView.cs:124-140` | confirmed |
| 11 | The two rollup reads of a year at a 6-hour step took 1,525 + 2,697 ms on the reference host, of 1,095 days at 1 day 4,251 + 8,022 ms (over 10 s together) | `connection-rollups.md`, *Disk and time* | confirmed |
| 11a | The chart action and the address action, each on its own (30 days at 1 h, a year at 12 h, 1,095 days at 2 days for 1,000 points), finish within 10 s on the reference host | to verify: `--network-view`, Product Manager, before step 9 | unverified |
| 11b | Both rollup reads take `Incomplete` from the same totals column, so the rollups' count alone decides E4 | `ConnectionRollupQueries.cs:90,147,211` | confirmed |
| 12 | The *latest in range* read uses the index without a temporary tree | to verify: Tester's query-plan test (as in `SqliteStoreHistoryTests.cs:755`), step 5 | unverified |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `NetworkChartViewStateChartStacksEveryStateInNameOrder`, `NetworkChartReaderReadsRollupsOfChosenSource` | `NetworkChartViewTests.cs`, `NetworkChartReaderTests.cs` |
| AC2 | `NetworkChartViewProgramChartStacksTopProgramsAndOthers`, `NetworkPageShowsProgramNote` | `NetworkChartViewTests.cs`, `NetworkPageTests.cs` |
| AC3 | `NetworkChartViewLinesCarryMinimumAndMaximum`, `NetworkChartReaderChoosesLadderStep`, `NetworkPageClickReplacesEntryWithMoment`, `NetworkPageSelectionWritesRange` | `NetworkChartViewTests.cs`, `NetworkChartReaderTests.cs`, `NetworkPageTests.cs` |
| AC4 | `NetworkChartViewAddressTableListsTopTwentyWithFurther`, `NetworkChartViewAddressTableNotesNameCapped`, `NetworkAddressReaderReadsAtChartStep`, `NetworkPageReadsAddressesAfterCharts` | `NetworkChartViewTests.cs`, `NetworkAddressReaderTests.cs`, `NetworkPageTests.cs` |
| AC5 | `NetworkListenerReaderReadsLatestInRange`, `NetworkListenerReaderReadsClosestToMoment`, `NetworkListenerReaderNoSnapshotShowsText`, `NetworkListenerViewBuildListsRowsWithCap`, `NetworkListenerViewBuildNotesPrivilegedList`, `NetworkPagePanelFollowsChartSource`, `SqliteStoreLatestSnapshotStamp*` | `NetworkListenerReaderTests.cs`, `NetworkListenerViewTests.cs`, `NetworkPageTests.cs`, `SqliteStoreHistoryTests.cs` |
| AC6 | `NetworkListenerViewBuildMarksNewFirst`, `NetworkListenerViewBuildNoComparisonSnapshot`, `NetworkListenerViewBuildNotComparable`, `NetworkListenerViewBuildPrivilegedOnly`, `NetworkListenerViewBuildWithoutInventory`, `NetworkListenerReaderReadsComparisonWindow`, `NetworkListenerReaderSkipsComparisonWithoutInventory`, `ListenerComparerCompareWithLimitKeepsEveryNewListener` | `NetworkListenerViewTests.cs`, `NetworkListenerReaderTests.cs`, `ListenerComparerTests.cs` |
| AC7 | `NetworkAddressParse*`, `NetworkAddressToQuery*`, `NetworkPageSourceChoiceDropsMoment` | `NetworkAddressTests.cs`, `NetworkPageTests.cs` |
| AC8 | `NetworkChartViewGapBreaksEveryBand`, `NetworkChartViewChartNotes*` | `NetworkChartViewTests.cs` |
| AC9 | `GatedConnectionRollupReader*`, `GatedHistoryReaderLatestSnapshotStampTakesSlot`, `NetworkPageTimeoutShowsText`, `NetworkPageAddressTimeoutKeepsCharts`, `NetworkPageFailureLogsWithoutText`, `NetworkPageShowsHostileTextEscaped`, `NetworkPageEscapesProgramLabels` | `GatedConnectionRollupReaderTests.cs`, `GatedHistoryReaderTests.cs`, `NetworkPageTests.cs` |
| AC10 | `MainLayoutShowsNetworkBetweenProcessesAndLogs`; manual: Product Manager runs `--network-view` on the reference host (self-contained publish of the branch head, as `docs/BENCHMARKS.md` says; fallback: the orchestrator publishes and hands over the binary) and checks drag, click and the stacked charts in a browser on a local build of the head | `MainLayoutTests.cs` |

## Approach

**Storage.** `IHistoryReader.LatestSnapshotStampAsync` returns the stamp of the latest snapshot of a kind and source with `From <=
captured_at < To`: statement one `SELECT captured_at … ORDER BY captured_at DESC LIMIT 1` on `records_kind_source_time`, statement two
the lowest `id` at that time, so two seeks whatever the range (record 0226). Refusals before any read: kind not a snapshot kind, empty
source, a bound outside the storable range (the existing texts); `From >= To` returns null.

**Core.** `ListenerComparer.Compare(earlier, later, newListenerLimit)`; the existing overload passes `MaxNewListeners`.

**Chart action** (`NetworkChartReader`, at most two gated calls): sources (`ConnectionSourcesAsync`), the step by
`SeriesLadder.Choose` from the chart width (in `NetworkCharts.Step`), `ConnectionRollupsAsync`.

**Address action** (`NetworkAddressReader`, one gated call, its own runner and 10-second deadline): `ConnectionAddressesAsync` of the
charts' source, range and step, started when a chart action ended with times; a new chart action cancels it. The two rollup reads of
1,095 days took 4,251 + 8,022 ms (fact 11), past one deadline, each within its own. The address and panel actions run beside each
other on the gate's two slots; the chart action has ended.
Default source: `SourceChoice.Latest`, shared with the processes view. `NetworkChartView` builds both charts (`StateChart`, `ProgramChart`) with `StackedLine.Build`
(the processes view's line builder, moved), factor 1, axis `Sockets`, the gap from `Others[step] is null`; states in result order,
programs then `Other programs`. Notes in this order: E1 alone, the 256-source text, E3, E4 (the rollups' `Incomplete` above 0, fact
11b), E5, the step text; below the table the cut note of S4 always, E14 when `Capped` is above 0. `Further` gives `and <n> more addresses`.

**Panel action** (`NetworkListenerReader`): shown stamp by `LatestSnapshotStampAsync` over the range, or with `at` by
`HistoryMomentReader.ClosestAsync` of the source; its record; when it has an `inventory`, the comparison stamp
(`SnapshotStampAsync`, *at or before* shown − 24 h, window one day, the source) and its record. At most five calls, two decoded
snapshots. `Build` checks the inventories before the comparer: shown without `inventory` reads no comparison and gives E9 alone (no
E7); a comparison without `inventory` gives E9, not the comparer's `NotComparable` (E8). `NetworkListenerView.Build(shown, earlier)` compares with the limit set to the listener count, orders new listeners first,
then the rest in snapshot order, and cuts to 200 rows. A removed record between stamp and read gives E6.

**Page** (`NetworkPage`, the processes page's structure without comparison): runners for chart, addresses and panel with event 81 and
E12's texts, each failing only its own section; the panel key is the shown source, the address's range parameters and `at` (E15).
Cells use `SnapshotCells` (the identical privates of `ProcessSnapshotView` and `ConnectionSnapshotView`, moved). Remote addresses,
listener protocols, owner commands, socket user names and the panel's source are built with `SnapshotCells.Runs`, program labels with
`StackedLine.Build` (`LogText.Escape`, 64 characters); `Plain` only for numbers and the page's own texts. `AddressTimes.ReadName` replaces `ProcessesAddress.ReadSource` and reads
`source` for `NetworkAddress`; `compare` is an unknown key there.

**Layout.** Heading `Network` (h1); the range controls and *Source* as on the processes page; the control text; the notes; h2 *Connections
per state* with its chart; h2 *Sockets held per program*, its note, its chart; h2 *Top remote addresses* with `Loading…`, the table
(`HistorySnapshotTable`), `and <n> more addresses`, its notes, or E12's text (left out with the charts on E1 or a failed chart read); h2 *Listeners* with `Listeners of <time> UTC (<source>)`, the summary
line, the notes in the order E9, E8, E10, the table, `and <n> more listeners`, or the panel's text. Accessible chart names are the
chart headings.

**Measurement.** `--network-view` of `tools/Vandox.StorageBenchmark` over 1,095 days of trimmed hourly and 30 days of 5-minute buckets
written by SQL (as `--connections`): the chart action and the address action each timed on its own, for 30 days, one year and 1,095
days at the 1,000-point step (1 h, 12 h, 2 days), the address action with the panel's reads running beside it on a second connection;
medians of five, each against 10,000 ms. A median past it fails N4 and comes back to the Lead (mode `decide`).

Early draft PR: no

## Options considered

- One chart action for the charts and the table — a 1,095-day range passes the deadline (fact 11) and loses the charts too; it would
  narrow S8 and the Product Manager's Q1 answer (Devil's Advocate, objection 1).
- The address action started beside the chart action when the address names `source` — it needs the chart's step and E1 first, and
  three actions at once would make the panel wait for a slot.
- Listener marking from the stored findings, against a calendar day, or by widening the stamp window — record 0226, lost there.
- Highlight by a row style instead of the `State` column and the order — needs a new table component and CSS (a static web asset,
  *Image build check*) for no information the text lacks.
- A new page component sharing a base class with the processes page — the pages differ in their panel actions and comparison mode;
  the shared parts are the helpers named in *Approach*.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `Reports/ListenerComparer` | overload with a limit |
| Vandox.Storage | `IHistoryReader`, `LatestSnapshotQuery` (new), `HistoryQueries`, `SqliteStore` | latest stamp in a range |
| Vandox.Backend | `History/AddressTimes`, `StackedLine` (new), `SnapshotCells` (new), `SourceChoice` (new), `GatedHistoryReader`, `ProcessSnapshotView`, `ConnectionSnapshotView` | shared helpers, gate |
| Vandox.Backend | `Processes/ProcessesAddress`, `ProcessesChartView`, `ProcessesChartReader` | use the helpers, behavior unchanged |
| Vandox.Backend | `Network/` (new): `NetworkAddress`, `NetworkView`, `GatedConnectionRollupReader`, `NetworkCharts`, `NetworkChartReader`, `NetworkAddresses`, `NetworkAddressReader`, `NetworkChartView`, `NetworkListenerView`, `NetworkListenerReader` | the view |
| Vandox.Backend | `Components/Pages/NetworkPage.razor(.cs)`, `Components/Layout/MainLayout.razor`, `Hosting/BackendLog`, `Hosting/ServeCommand` | page, nav, event 81, registration |
| tools | `Vandox.StorageBenchmark` | `--network-view` |

## Signatures (for the Dev's skeleton)

Core and Storage (group G1):

- `public static ListenerComparison Compare(ConnectionSnapshot? earlier, ConnectionSnapshot later, int newListenerLimit)`
- sealed class `LatestSnapshotQuery` with `Kind` (string), `Source` (string), `From` (DateTimeOffset), `To` (DateTimeOffset)
- `Task<SnapshotStamp?> LatestSnapshotStampAsync(LatestSnapshotQuery query, CancellationToken cancellationToken);` on `IHistoryReader`, `SqliteStore`, `GatedHistoryReader`

Backend helpers and `Network/` types (group G2):

- `public static ValueTuple<string, string?> ReadName(Dictionary<string, StringValues> values, string name)` on `AddressTimes`, written as the named tuple (Error, Value)
- `public static HistoryLine Build(string label, int index, IReadOnlyList<ProgramRollupPoint?>? points, IReadOnlyList<bool> gaps, double factor, IReadOnlyList<double?>? others)` on static class `StackedLine`
- static class `SnapshotCells` with `public static IReadOnlyList<TextRun> Runs(string text)`, `public static IReadOnlyList<TextRun> Plain(IFormattable value)`, `public static IReadOnlyList<TextRun> Plain(string value)`, `public static SnapshotRowView Row(params IReadOnlyList<TextRun>[] cells)`
- static class `SourceChoice` with `public static string Latest(ProgramSourceList sources)`
- sealed class `NetworkAddress` with `Source` (string?), `Preset` (HistoryPreset?), `From` (DateTimeOffset?), `To` (DateTimeOffset?), `At` (DateTimeOffset?), `Error` (string), `public static NetworkAddress Parse(string query)`, `public NetworkAddress Copy()`, `public HistoryRange Range(DateTimeOffset now)`, `public string ToQuery()`, `public bool SameChart(NetworkAddress other)`
- static class `NetworkView` with the spec's texts as public string constants, `AddressRows` (const int, 20), `ListenerRows` (const int, 200), `public static string NetworkHref(NetworkAddress address)`
- sealed class `GatedConnectionRollupReader : IConnectionRollupReader` with `public GatedConnectionRollupReader(IConnectionRollupReader inner, WebReaderGate gate)`
- sealed class `NetworkCharts` with `Sources` (IReadOnlyList<string>), `Source` (string), `Step` (TimeSpan), `States` (HistoryChartView?), `Programs` (HistoryChartView?), `Notes` (IReadOnlyList<string>)
- sealed class `NetworkChartReader` with `public NetworkChartReader(IConnectionRollupReader reader, RetentionPolicy policy, TimeProvider clock)`, `public Task<NetworkCharts> ReadAsync(string? source, HistoryRange range, double chartWidth, CancellationToken cancellationToken)`
- sealed class `NetworkAddresses` with `Table` (SnapshotTableView), `Notes` (IReadOnlyList<string>)
- sealed class `NetworkAddressReader` with `public NetworkAddressReader(IConnectionRollupReader reader)`, `public Task<NetworkAddresses> ReadAsync(string source, HistoryRange range, TimeSpan step, CancellationToken cancellationToken)`
- static class `NetworkChartView` with `public static HistoryChartView StateChart(ConnectionRollupResult result)`, `public static HistoryChartView ProgramChart(ConnectionRollupResult result)`, `public static SnapshotTableView AddressTable(ConnectionAddressResult result)`, `public static IReadOnlyList<string> AddressTableNotes(ConnectionAddressResult result)`, `public static IReadOnlyList<string> ChartNotes(ConnectionRollupResult rollups, ProgramSourceList sources, HistoryRange range, TimeSpan stepLength)`
- sealed class `NetworkListenerView` with `CapturedAt` (DateTimeOffset?), `SourceText` (IReadOnlyList<TextRun>), `Summary` (string), `PanelNotes` (IReadOnlyList<string>), `Table` (SnapshotTableView), `Text` (string), `public static NetworkListenerView Build(StoredRecord shown, StoredRecord? earlier)`, `public static NetworkListenerView Empty(string text)`
- sealed class `NetworkListenerReader` with `public NetworkListenerReader(IHistoryReader reader)`, `public Task<NetworkListenerView> ReadAsync(string source, HistoryRange range, DateTimeOffset? at, CancellationToken cancellationToken)`

Page (group G3): `internal static partial void NetworkReadFailed(this ILogger logger, Exception exception)` in `BackendLog` (event 81, error,
message "network view: reading failed"), and sealed partial class `NetworkPage : ComponentBase, IDisposable` on the route /network.

Deleted: the private `ProcessesChartView.BuildLine`, `ProcessesAddress.ReadSource`, `ProcessesChartReader.Latest` and the private `Runs`, `Plain`, `Row` of `ProcessSnapshotView` and `ConnectionSnapshotView` (moved to the helpers). The skeleton holds for real: the route, the nav entry and the registrations in `ServeCommand`.

## Test files

`tests/Vandox.Core.Tests/ListenerComparerTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreHistoryTests.cs`, and in
`tests/Vandox.Backend.Tests/`: `NetworkAddressTests.cs`, `NetworkChartReaderTests.cs`, `NetworkAddressReaderTests.cs`, `NetworkChartViewTests.cs`,
`NetworkListenerViewTests.cs`, `NetworkListenerReaderTests.cs`, `NetworkPageTests.cs` (with `NetworkPageHarness.cs`,
`NetworkSamples.cs`), `GatedConnectionRollupReaderTests.cs`, `GatedHistoryReaderTests.cs`, `MainLayoutTests.cs`, and the new double
`FakeConnectionRollupReader.cs`.

Existing call sites: `FakeHistoryReader` implements the new member (Tester). `ProcessesAddressTests`, `ProcessesChartViewTests`,
`ProcessSnapshotViewTests`, `HistoryMomentReaderTests` keep their inputs; no criterion handles one differently.

Files no unit test can cover: none; no browser script changes. The benchmark scenario in `tools/` is run by no test, as `--processes-view` of #480 was; the Tester confirms in step 6 that the coverage gate does not count it.

## Areas

- `docs/areas/web-ui-network-view.md` (new, +2400 words): scope and every rule of the spec as built.
- `docs/areas/README.md` (+60 words): the index row of the new area.
- `docs/areas/web-ui.md` (+90 words): navigation (seven entries), the lists of views, the gate's views, the stacked mode's users, the chart module's users.
- `docs/areas/connection-rollups.md` (+15 words): the network view reads them (*Reading*, *Not here*).
- `docs/areas/storage-reading.md` (+80 words): the latest snapshot stamp in a range, under *History reads*.
- `docs/areas/listener-changes.md` (+40 words): the view's inventory marking in *Scope* and *Not here*.
- `docs/areas/web-ui-history-view.md` (+10 words): connections over time in *Not here*.

## Documentation updates

All by the Dev after the code is green: the area documents above; `docs/ARCHITECTURE.md` (the views paragraph after the processes
view, and `IHistoryReader` and `IConnectionRollupReader` in the interface list name the network view); `README.md` (a section *See the
connections* after the processes section); `CHANGELOG.md` (an entry for #51); `docs/BENCHMARKS.md` (*Network view* with the
`--network-view` figures, after the Product Manager's run); `.squad/project.md` (+12 words: `/network` (`NetworkAddress`) in area 10,
`NetworkPage` in area 12; reported text, written by the orchestrator); *Test doubles* row for `FakeConnectionRollupReader` (+12 words,
the Tester reports it). Record 0226's `Area:` becomes *Network view* once its index row exists (Lead).

## Architecture check

No guarantee of `.squad/project.md` is touched: the view only reads, never alerts, and shows a gap as a gap (record 0028). The read gate
and the deadline bound the store work (N2, N3).

## Security considerations

- [x] Limits: the address's source is checked against the name pattern on the decoded value (1 to 128 bytes), as the processes view;
  external text is capped after decoding at the web UI's 256 characters and 16 runs, chart labels at 64 characters.
- [x] Exceptions: a store error, a refused query and a timeout reach the user only as the two texts of E12; the log line holds the
  exception, never an address value, source, command, user or address.

## Decision records

- `docs/decisions/0226-the-network-view-stacks-connection-rollups-and-marks-listeners-new-against-an-inventory-a-day-older.md` (Proposed)

## Out of scope / follow-ups

The spec's *Out of scope*.

## Challenge

Devil's Advocate, mode `plan`: facts 1, 4, 6, 7, 8 and 10, the five panel calls and the moment-free chart builders were confirmed.

- Objection 1 (major, a 1,095-day range past one deadline, narrowing S8 and Q1): accepted. *Top remote addresses* has its own action,
  runner and deadline (*Address action*, *Page*, `NetworkAddresses`, `NetworkAddressReader`; `ChartNotes` loses its address result, E4
  from the rollups alone, fact 11b). Spec N4 names both actions and the 1,095-day range, E12 the table's own failure text; the
  benchmark times each action at 30 days, a year and 1,095 days (2-day step), the address action beside the panel's reads (fact 11a).
  Tests `NetworkAddressReaderReadsAtChartStep`, `NetworkPageReadsAddressesAfterCharts`, `NetworkPageAddressTimeoutKeepsCharts`. The
  *Out of scope* line is gone; record 0226 lists the single action as a lost option.
- Objection 2 (minor, comparison without `inventory` gives E8): accepted. *Panel action* checks both inventories before the comparer;
  `NetworkListenerViewBuildWithoutInventory` covers both sides (shown: E9 alone, no E7; comparison: E9, not E8), and
  `NetworkListenerReaderSkipsComparisonWithoutInventory` that no comparison is read for a shown snapshot without `inventory`.

## Security review

Security, mode `plan`: approved, tier `standard` and areas 3, 10 and 12 confirmed; the latest-stamp read seeks the index without a
temporary tree on Python's SQLite 3.53.1 (fact 12 stays the Tester's binding check).

- [NON-BLOCKING] which external text goes through `SnapshotCells.Runs`: fixed now. *Page* names the fields (remote address, listener
  protocol, owner command, socket user name, the panel's source: `Runs`; program labels: `StackedLine.Build` with `LogText.Escape`;
  `Plain` only for numbers and the page's own texts). `NetworkPageShowsHostileTextEscaped` covers each `Runs` field with a hostile
  value (format and direction characters, over 256 characters), and `NetworkPageEscapesProgramLabels` (AC9) a hostile program label
  escaped and cut to 64 characters.
