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
| 11a | The view's chart action (a year at 12 h for 1,000 points, 30 days at 1 h) finishes within 10 s on the reference host | to verify: `--network-view`, Product Manager, before step 9 | unverified |
| 12 | The *latest in range* read uses the index without a temporary tree | to verify: Tester's query-plan test (as in `SqliteStoreHistoryTests.cs:755`), step 5 | unverified |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `NetworkChartViewStateChartStacksEveryStateInNameOrder`, `NetworkChartReaderReadsRollupsOfChosenSource` | `NetworkChartViewTests.cs`, `NetworkChartReaderTests.cs` |
| AC2 | `NetworkChartViewProgramChartStacksTopProgramsAndOthers`, `NetworkPageShowsProgramNote` | `NetworkChartViewTests.cs`, `NetworkPageTests.cs` |
| AC3 | `NetworkChartViewLinesCarryMinimumAndMaximum`, `NetworkChartReaderChoosesLadderStep`, `NetworkPageClickReplacesEntryWithMoment`, `NetworkPageSelectionWritesRange` | `NetworkChartViewTests.cs`, `NetworkChartReaderTests.cs`, `NetworkPageTests.cs` |
| AC4 | `NetworkChartViewAddressTableListsTopTwentyWithFurther`, `NetworkChartViewAddressTableNotesNameCapped` | `NetworkChartViewTests.cs` |
| AC5 | `NetworkListenerReaderReadsLatestInRange`, `NetworkListenerReaderReadsClosestToMoment`, `NetworkListenerReaderNoSnapshotShowsText`, `NetworkListenerViewBuildListsRowsWithCap`, `NetworkListenerViewBuildNotesPrivilegedList`, `NetworkPagePanelFollowsChartSource`, `SqliteStoreLatestSnapshotStamp*` | `NetworkListenerReaderTests.cs`, `NetworkListenerViewTests.cs`, `NetworkPageTests.cs`, `SqliteStoreHistoryTests.cs` |
| AC6 | `NetworkListenerViewBuildMarksNewFirst`, `NetworkListenerViewBuildNoComparisonSnapshot`, `NetworkListenerViewBuildNotComparable`, `NetworkListenerViewBuildPrivilegedOnly`, `NetworkListenerViewBuildWithoutInventory`, `NetworkListenerReaderReadsComparisonWindow`, `ListenerComparerCompareWithLimitKeepsEveryNewListener` | `NetworkListenerViewTests.cs`, `NetworkListenerReaderTests.cs`, `ListenerComparerTests.cs` |
| AC7 | `NetworkAddressParse*`, `NetworkAddressToQuery*`, `NetworkPageSourceChoiceDropsMoment` | `NetworkAddressTests.cs`, `NetworkPageTests.cs` |
| AC8 | `NetworkChartViewGapBreaksEveryBand`, `NetworkChartViewChartNotes*` | `NetworkChartViewTests.cs` |
| AC9 | `GatedConnectionRollupReader*`, `GatedHistoryReaderLatestSnapshotStampTakesSlot`, `NetworkPageTimeoutShowsText`, `NetworkPageFailureLogsWithoutText`, `NetworkPageShowsHostileTextEscaped` | `GatedConnectionRollupReaderTests.cs`, `GatedHistoryReaderTests.cs`, `NetworkPageTests.cs` |
| AC10 | `MainLayoutShowsNetworkBetweenProcessesAndLogs`; manual: Product Manager runs `--network-view` on the reference host (self-contained publish of the branch head, as `docs/BENCHMARKS.md` says; fallback: the orchestrator publishes and hands over the binary) and checks drag, click and the stacked charts in a browser on a local build of the head | `MainLayoutTests.cs` |

## Approach

**Storage.** `IHistoryReader.LatestSnapshotStampAsync` returns the stamp of the latest snapshot of a kind and source with `From <=
captured_at < To`: statement one `SELECT captured_at … ORDER BY captured_at DESC LIMIT 1` on `records_kind_source_time`, statement two
the lowest `id` at that time, so two seeks whatever the range (record 0226). Refusals before any read: kind not a snapshot kind, empty
source, a bound outside the storable range (the existing texts); `From >= To` returns null.

**Core.** `ListenerComparer.Compare(earlier, later, newListenerLimit)`; the existing overload passes `MaxNewListeners`.

**Chart action** (`NetworkChartReader`, at most three gated calls): sources (`ConnectionSourcesAsync`), the step by
`SeriesLadder.Choose` from the chart width, `ConnectionRollupsAsync`, and when it has times `ConnectionAddressesAsync` at the same step.
Default source: `SourceChoice.Latest`, shared with the processes view. `NetworkChartView` builds both charts (`StateChart`, `ProgramChart`) with `StackedLine.Build`
(the processes view's line builder, moved), factor 1, axis `Sockets`, the gap from `Others[step] is null`; states in result order,
programs then `Other programs`. Notes in this order: E1 alone, the 256-source text, E3, E4 (`Incomplete` of either result above 0), E5,
the step text; below the table the cut note of S4 always, E14 when `Capped` is above 0. `Further` gives `and <n> more addresses`.

**Panel action** (`NetworkListenerReader`): shown stamp by `LatestSnapshotStampAsync` over the range, or with `at` by
`HistoryMomentReader.ClosestAsync` of the source; its record; when it has an `inventory`, the comparison stamp
(`SnapshotStampAsync`, *at or before* shown − 24 h, window one day, the source) and its record. At most five calls, two decoded
snapshots. `NetworkListenerView.Build(shown, earlier)` compares with the limit set to the listener count, orders new listeners first,
then the rest in snapshot order, and cuts to 200 rows. A removed record between stamp and read gives E6.

**Page** (`NetworkPage`, the processes page's structure without comparison): runners for chart and panel with event 81; the panel key
is the shown source, the address's range parameters and `at` (E15). Cells use `SnapshotCells` (the identical privates of
`ProcessSnapshotView` and `ConnectionSnapshotView`, moved). `AddressTimes.ReadName` replaces `ProcessesAddress.ReadSource` and reads
`source` for `NetworkAddress`; `compare` is an unknown key there.

**Layout.** Heading `Network` (h1); the range controls and *Source* as on the processes page; the control text; the notes; h2 *Connections
per state* with its chart; h2 *Sockets held per program*, its note, its chart; h2 *Top remote addresses* with the table
(`HistorySnapshotTable`), `and <n> more addresses`, its notes; h2 *Listeners* with `Listeners of <time> UTC (<source>)`, the summary
line, the notes in the order E9, E8, E10, the table, `and <n> more listeners`, or the panel's text. Accessible chart names are the
chart headings.

**Measurement.** `--network-view` of `tools/Vandox.StorageBenchmark`: the chart action over 1,095 days of trimmed hourly and 30 days
of 5-minute buckets written by SQL (as `--connections`), 30 days and one year at the 1,000-point step, and the panel's reads; medians
of five.

Early draft PR: no

## Options considered

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
| Vandox.Backend | `Network/` (new): `NetworkAddress`, `NetworkView`, `GatedConnectionRollupReader`, `NetworkCharts`, `NetworkChartReader`, `NetworkChartView`, `NetworkListenerView`, `NetworkListenerReader` | the view |
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
- sealed class `NetworkCharts` with `Sources` (IReadOnlyList<string>), `Source` (string), `States` (HistoryChartView?), `Programs` (HistoryChartView?), `Addresses` (SnapshotTableView?), `AddressNotes` (IReadOnlyList<string>), `Notes` (IReadOnlyList<string>)
- sealed class `NetworkChartReader` with `public NetworkChartReader(IConnectionRollupReader reader, RetentionPolicy policy, TimeProvider clock)`, `public Task<NetworkCharts> ReadAsync(string? source, HistoryRange range, double chartWidth, CancellationToken cancellationToken)`
- static class `NetworkChartView` with `public static HistoryChartView StateChart(ConnectionRollupResult result)`, `public static HistoryChartView ProgramChart(ConnectionRollupResult result)`, `public static SnapshotTableView AddressTable(ConnectionAddressResult result)`, `public static IReadOnlyList<string> AddressTableNotes(ConnectionAddressResult result)`, `public static IReadOnlyList<string> ChartNotes(ConnectionRollupResult rollups, ConnectionAddressResult? addresses, ProgramSourceList sources, HistoryRange range, TimeSpan stepLength)`
- sealed class `NetworkListenerView` with `CapturedAt` (DateTimeOffset?), `SourceText` (IReadOnlyList<TextRun>), `Summary` (string), `PanelNotes` (IReadOnlyList<string>), `Table` (SnapshotTableView), `Text` (string), `public static NetworkListenerView Build(StoredRecord shown, StoredRecord? earlier)`, `public static NetworkListenerView Empty(string text)`
- sealed class `NetworkListenerReader` with `public NetworkListenerReader(IHistoryReader reader)`, `public Task<NetworkListenerView> ReadAsync(string source, HistoryRange range, DateTimeOffset? at, CancellationToken cancellationToken)`

Page (group G3): `internal static partial void NetworkReadFailed(this ILogger logger, Exception exception)` in `BackendLog` (event 81, error,
message "network view: reading failed"), and sealed partial class `NetworkPage : ComponentBase, IDisposable` on the route /network.

Deleted: the private `ProcessesChartView.BuildLine`, `ProcessesAddress.ReadSource`, `ProcessesChartReader.Latest` and the private `Runs`, `Plain`, `Row` of `ProcessSnapshotView` and `ConnectionSnapshotView` (moved to the helpers). The skeleton holds for real: the route, the nav entry and the registrations in `ServeCommand`.

## Test files

`tests/Vandox.Core.Tests/ListenerComparerTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreHistoryTests.cs`, and in
`tests/Vandox.Backend.Tests/`: `NetworkAddressTests.cs`, `NetworkChartReaderTests.cs`, `NetworkChartViewTests.cs`,
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

The spec's *Out of scope*. A 1,095-day custom range can pass the deadline (fact 11) and shows E12's timeout text.

## Challenge notes

Devil's Advocate, mode `plan`, specs/feature-51-network-view/ (plan.md, spec.md, tasks.md; issue #51 read through the REST API)

The plan's facts mostly check out:
- Facts 1, 4, 6, 7, 8 and 10 match the code:
  - `ConnectionRollupFolder.SumStates` folds udp and udp6 into `UDP` and sorts states by ordinal name.
  - `HistoryQueries.ReadStampAsync`'s *at or before* side covers `[at − window, at]`, and `ValidateWindow` allows a window of at most one day.
  - `ListenerComparer.Compare` caps new listeners at `MaxNewListeners` = 64.
  - The private `Row`, `Runs` and `Plain` of the two snapshot views are identical.
  - Event 80 is the last ID in `BackendLog.cs`.
- The panel action needs at most five calls. With `at`: `HistoryMomentReader.ClosestAsync` makes two stamp calls and one record call (`History/HistoryMomentReader.cs:71-89`), then the comparison stamp and its record.
- The chart builders take no moment (`StateChart(result)`), and that is fine: the processes reader also passes `null` (`Processes/ProcessesChartReader.cs:94,101`), and the page marks the moment.
- The target-host and sample checks do not apply.

**Objection 1 (major): a 1,095-day range would lose the charts and the table, and the plan accepts this without asking the Product Manager.**
- The plan says under *Out of scope / follow-ups*: "A 1,095-day custom range can pass the deadline (fact 11) and shows E12's timeout text."
- This narrows the scope the spec sets:
  - S8 promises "the charts and the table show the whole range as far as the connection rollups are kept (`retention.rollups_1h`)".
  - The Product Manager's answer to Q1 says the view "reads them back to `retention.rollups_1h`".
- Evidence: `docs/areas/connection-rollups.md:149-152` measured the full 1,095 days at 4,251 ms (states and programs) plus 8,022 ms (addresses). That is about 12.3 s in one action, against a 10-second deadline that also includes the wait for the gate (`docs/areas/web-ui.md:122-123`).
- Because both reads share one action, a timeout of the address read also throws away the charts, which alone take about 4.3 s.
- Simpler option the plan did not consider: give *Top remote addresses* its own action, with its own `PageRunner`, its own 10-second deadline and the E12 texts. The processes page already runs two runners (`Components/Pages/ProcessesPage.razor.cs:676-677`). Each read then fits on its own: about 4.3 s and about 8.0 s.
- This needs the Lead to change N4 ("the chart action (sources, states and programs, addresses)") and the plan's *Chart action* and *Page* sections.
- Fact 11a's `--network-view` run should then also measure the 1,095-day range at the step the ladder picks for 1,000 points. That is about 2 days, which connection-rollups.md has not measured. Measure each action on its own, with the panel's reads running in parallel, since the gate has only two slots.
- If the Lead keeps a single action, the Product Manager has to accept the narrowed S8 explicitly (*Escalation*). A line under *Out of scope* is not enough.
- Why major: as planned, the page fails S8 and the Q1 answer for the longest range the rollups keep.

**Objection 2 (minor): a comparison snapshot without `inventory` must show E9, not E8.**
- In `src/Vandox.Core/Reports/ListenerComparer.cs`, `IsNotComparable` returns `NotComparable` for `earlier.Inventory is null`. That is the same outcome as a `skipped` inventory or different `tables`.
- So `NetworkListenerView.Build` has to check `earlier`'s `inventory` itself before turning the outcome into a text. Otherwise E9 shows up as E8's `No listener is marked new: the inventories are not comparable.`
- Instruction for Dev and Tester: `NetworkListenerViewBuildWithoutInventory` covers both sides.
  - The shown snapshot has no `inventory`: no comparison is read and only E9 appears. E7 must not appear, even though `earlier` is null.
  - The comparison snapshot has no `inventory`: E9 appears, not E8.
- Why minor: the Dev can follow this without a plan change.

## Security notes

Security plan review: specs/feature-51-network-view/ (spec.md, plan.md). Mode: plan.

**Result:** I approve the plan. There are no blocking findings and one non-blocking one.

**Tier and listed areas.** `standard` is the right tier. The plan creates no new security control, changes none and removes none:
- **Login (area 3).** The new `NetworkPage` goes under `Components/Pages/`, so it inherits `[Authorize]` from `src/Vandox.Backend/Components/Pages/_Imports.razor:2`. The authenticated-user fallback policy also applies (`src/Vandox.Backend/Security/WebAuthentication.cs:84`). `RoutesTests` (`tests/Vandox.Backend.Tests/RoutesTests.cs:31-37`) already fails for any page without `[Authorize]`. No new endpoint or listener is added.
- **Moved code.** `ReadSource` becomes `AddressTimes.ReadName`, and the escape helpers become the shared `SnapshotCells`. Both are moves with behavior unchanged, and the existing `ProcessesAddressTests`, `ProcessSnapshotViewTests` and `ConnectionSnapshotViewTests` keep their inputs. So no guard changes.
- **Areas 3, 10 and 12** are listed correctly. The new `ListenerComparer.Compare` overload sits in `src/Vandox.Core/Reports` (area 10), which is already listed. Its input is bounded by the decoder (at most 4,096 listeners per snapshot, `docs/areas/wire-format-records.md`), and the report path keeps `MaxNewListeners`. I found no unlisted area.

**What I checked**
- **Address parsing (area 10).** `source` is checked with `AddressTimes.IsValidName`: 1 to 128 characters, the name regex, on the decoded value. A repeated parameter is refused, and `ToQuery` checks every value again before `Uri.EscapeDataString` (`src/Vandox.Backend/Processes/ProcessesAddress.cs:187,243-270`). Unknown keys such as `compare` are ignored, as the processes view documents (`docs/areas/web-ui-processes-view.md:36`). The store refusals behind the new `LatestSnapshotStampAsync` follow `HistoryQueries.ValidateStamp`: fixed texts with no values, SQL parameters only (`src/Vandox.Storage/HistoryQueries.cs:109-126`).
- **Logging (E12).** Event 81 takes only the exception, like event 78 for the processes view (`BackendLog.cs:615`). The store's invalid-query texts carry no source or address value.
- **Bounds (N3): what the page keeps between reads.**
  - Store caps: 13 states (the wire enum), 10 programs plus others, 20 addresses, 256 sources (`docs/areas/connection-rollups.md`, *Reading*, *Memory*).
  - The panel returns only `NetworkListenerView`, cut to 200 rows; the two decoded `StoredRecord`s are not retained.
  - The comparison's added list is bounded by the snapshot (at most 4,096).
  - Tests that pin these caps: `NetworkListenerViewBuildListsRowsWithCap`, `NetworkChartViewAddressTableListsTopTwentyWithFurther`, `NetworkChartReaderChoosesLadderStep`.
- **Fact 12 (the latest-stamp read seeks the index rather than scanning).** I checked it once on an empty schema copy with Python's sqlite3 3.53.1, not the SQLite bundled with Microsoft.Data.Sqlite. Command: `python3 -I -c "…EXPLAIN QUERY PLAN SELECT captured_at FROM records WHERE kind=? AND source=? AND captured_at>=? AND captured_at<? ORDER BY captured_at DESC LIMIT 1…"` with `records_kind_source_time`, `records_kind_time` and `records_connection_fold` created. Output: `SEARCH records USING COVERING INDEX records_kind_source_time (kind=? AND source=? AND captured_at>? AND captured_at<?)`. The second statement (`… captured_at=? ORDER BY id LIMIT 1`) gave `SEARCH records USING COVERING INDEX records_kind_source_time (kind=? AND source=? AND captured_at=?)`. Neither used a temp B-tree. The Tester's query-plan test in step 5 remains the binding check. Hangs are bounded by the 10-second deadline in any case (N2).

**Finding**

[NON-BLOCKING] plan.md, *Approach* (Page, "Cells use `SnapshotCells`") and *Tests per criterion* (AC9, `NetworkPageShowsHostileTextEscaped`).
- **Defect.** The plan does not say which shown fields are external text and must go through `SnapshotCells.Runs` (or `LogText.Escape` for a chart label), and which may use `Plain`. `Plain(string)` writes text without the visible escape of format and direction characters. It was private in `ProcessSnapshotView` and `ConnectionSnapshotView`; this change makes it public in a shared helper.
- **Fields this view adds that are external text:**
  - the remote address (`ConnectionAddress.Address`, a string; an import's `legacy-monitor` writes it too)
  - the listener's `proto`
  - the command in *Owner*
  - the user name in *Socket UID* (up to 1,024 bytes, `wire-format-records.md`)
  - the source in `Listeners of <time> UTC (<source>)`
  - the program labels of *Sockets held per program*
- **Test gap.** The single test named for AC9 does not show that every one of these is covered. The processes view's equivalent needed two tests: `ProcessesPageShowsHostileTextEscaped` and `ProcessesPageEscapesProgramLabels` (`tests/Vandox.Backend.Tests/ProcessesPageTests.cs:899,939`).
- **Smallest fix.** Add to *Approach* (Page) the sentence: "Remote addresses, listener protocols, owner commands, socket user names and the panel's source are built with `SnapshotCells.Runs`, program labels with `StackedLine.Build` (`LogText.Escape`, 64 characters); `Plain` only for numbers and the page's own texts." Extend `NetworkPageShowsHostileTextEscaped` to cover each of those fields, and add `NetworkPageEscapesProgramLabels` to AC9.

Recommendation: fix now

**Files**
- /home/user/Vandox/specs/feature-51-network-view/plan.md
- /home/user/Vandox/specs/feature-51-network-view/spec.md
- /home/user/Vandox/src/Vandox.Backend/Processes/ProcessesAddress.cs
- /home/user/Vandox/src/Vandox.Backend/History/ConnectionSnapshotView.cs
- /home/user/Vandox/src/Vandox.Storage/HistoryQueries.cs
- /home/user/Vandox/src/Vandox.Core/Reports/ListenerComparer.cs
- /home/user/Vandox/tests/Vandox.Backend.Tests/RoutesTests.cs
- /home/user/Vandox/tests/Vandox.Backend.Tests/ProcessesPageTests.cs
