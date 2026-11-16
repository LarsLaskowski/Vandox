# Plan: Overview page

Source: Issue #48 | [spec.md](spec.md)
Tier: standard — a new page with new store reads changes behavior but creates, changes or removes no security control (the page stays under the existing `[Authorize]` and shows external text through the existing display).
Security areas touched: 3 Web UI login (a new page under `Components/Pages/`), 12 Logging and display of external text (unit names, mount points, victims on a page)

## Problem / root cause

See [spec.md](spec.md): the start page `/` (`Components/Pages/Home.razor`) shows a fixed text and links, no state.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Agent metrics carry the sources `system`, `mariadb`, `agent`; imported legacy metrics `legacy-monitor` | wire-format-records.md:66-70; log-parsers-legacy-monitoring.md:79 | confirmed |
| 2 | The metric names of AC5 are written by the agent | `internal/sysstats/memory.go:132,138`, `pressure.go:128`; wire-format-records.md:31-57 | confirmed |
| 3 | `mariadb_status` keys are stored exactly as `Threads_connected`, `Memory_used`, `max_connections` | exact-name maps `internal/mariadb/sample.go:28-40,81-87` | confirmed |
| 4 | Every record has `received_at`; records are not indexed by it; `records_agent_seq(agent_id, seq)` and `agent_streams.max_seq` per agent exist | `SchemaMigrator.cs:40-56,224-238` | confirmed |
| 5 | Open sequence gaps are indexed per agent (`gaps_seq_open`) with `from_at`, `to_at` | `SchemaMigrator.cs:263-281` | confirmed |
| 6 | A sequence gap can span numbers never issued (clock-bound start) | agent-spool.md:98-107, storage-writing.md:115-117 (spec M1) | confirmed |
| 7 | The live stream goes first and sends every 4 s; the backfill waits for its first acknowledgement | agent-sending.md:50-60 | confirmed |
| 8 | `records_kind_time(kind, captured_at)` and `metrics_source_name_time(source, name, captured_at)` serve newest-first reads; `live` is a column of `records` | `SchemaMigrator.cs:55-68,226` | confirmed |
| 9 | `records_service_state` covers `captured_at`, unit, restarts, `active_enter_at`, `active_state`, not `sub_state` | `SchemaMigrator.cs:20-30` | confirmed |
| 10 | Unit names are ASCII (`^[A-Za-z0-9:_.@\-]+$`), so SQLite's binary order is ordinal order | wire-format-records.md:164 | confirmed |
| 11 | Incident windows never overlap, so at most one incident has `End > now` and `FirstKillAt <= now`; summaries come newest first | incidents.md:24-26; storage-reading.md:74 | confirmed |
| 12 | Every page under `Components/Pages` carries `[Authorize]` | `Pages/_Imports.razor` | confirmed |
| 13 | The views' gate is `WebReaderGate(2)`, one slot per call | `ServeCommand.cs:247`, web-ui.md:109-116 | confirmed |
| 14 | `HomePageTests`, `RoutesTests:36` (`typeof(Home)`) and `ServeTests:490` (`"Vandox backend"`) depend on the old page | the files | confirmed |
| 15 | A `CircuitHandler`'s `OnConnectionDownAsync`/`OnConnectionUpAsync` run for the circuit's own connection; tests may pass `null!` for the circuit | to verify: the Dev reads the ASP.NET Core source of `CircuitHost` in step 4 and reports (`SessionCircuitHandlerTests.cs:43` already passes `null!`) | unverified |
| 16 | The issue's claims (services, key figures, incidents, freshness, backfill, spool are stored) | facts 1-5, 9; agent.md:149; "open incidents" and backfill "progress" refined in the spec | confirmed |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `SqliteStoreOverviewTests.FreshnessReturnsNewestLiveMetricAndHighestNumberReceiveTime`, `FreshnessReportsOldestScannedTimeWhenNoLiveRecordInScan`; `OverviewViewTests.BuildShowsLiveDataAndReceivedTimeWithAges`, `BuildMarksLiveDataStaleAfterFiveMinutes`, `BuildShowsBeforeWhenNoLiveRecordWasScanned`, `BuildShowsAheadByForTimeAfterNow`, `BuildShowsNoLiveDataTextWithoutAgentRecord` | `tests/Vandox.Storage.Tests/SqliteStoreOverviewTests.cs`, `tests/Vandox.Backend.Tests/OverviewViewTests.cs` |
| AC2 | `SqliteStoreOverviewTests.FreshnessReturnsSpanOfOpenSequenceGapsAfterJump`, `FreshnessIgnoresOpenGapsOfOtherAgentIds`; `OverviewViewTests.BuildShowsBackfillRunningWithSpanAndNoCount`, `BuildShowsBackfillStalledWithoutDataForFiveMinutes`, `BuildShowsNoBackfillRunningWithoutOpenGap` | as AC1 |
| AC3 | `OverviewViewTests.BuildShowsSpoolSizeAndOldestUnsentRecord`, `BuildShowsNothingUnsentForAgeZero`, `BuildMarksSpoolSampleStale`, `BuildShowsSpoolUnknownWithoutSample` | `OverviewViewTests.cs` |
| AC4 | `SqliteStoreOverviewTests.LatestServiceStatesReturnsLatestPerUnitWithinRetirement`, `LatestServiceStatesOrdersNonActiveFirstAndCountsTotal`; `OverviewViewTests.BuildListsNonActiveUnitsFirstAndMarksFailed`, `BuildMarksServicesStaleWithoutBatchForFiveMinutes`, `BuildMarksUnitStaleAfterOneHourFiveMinutes`, `BuildShowsMoreUnitsBeyondTheCap`; `OverviewPageTests.OverviewPageShowsUnitNameAsExternalText` | Storage and Backend files above, `tests/Vandox.Backend.Tests/OverviewPageTests.cs` |
| AC5 | `SqliteStoreOverviewTests.LatestValuesReturnsNewestPerLabelSetWithinWindow`, `LatestValuesCountsLabelSetsBeyondTheLimit`, `LatestMariaDbStatusReturnsSelectedMembers`; `OverviewViewTests.BuildShowsKeyFiguresWithTimes`, `BuildMarksFigureStaleAfterFiveMinutes`, `BuildShowsUnknownForMissingFigure`, `BuildShowsNoSwapAndNoShareForZeroTotal`, `BuildShowsMountsUpToTheCapWithMore`; `OverviewTextTests.SizeWritesBinaryUnitsWithOneDecimal`, `ShareWritesPercentWithOneDecimal`, `AgeWritesTheUnitOfItsRange` | as above, `tests/Vandox.Backend.Tests/OverviewTextTests.cs` |
| AC6 | `OverviewViewTests.BuildListsOpenIncidentWithLink`, `BuildShowsNoOpenIncidentAndLatestIncident`, `BuildNeverOpensIncidentWithKillAfterNow`, `BuildShowsNoIncidentsText` | `OverviewViewTests.cs` |
| AC7 | `OverviewRefresherTests.RefresherRefreshesAtStartAndEveryThirtySeconds`, `RefresherSkipsDueRefreshWhileOneRuns`, `RefresherStartsNoRefreshWhileDisconnected`, `RefresherRefreshesWhenConnectionIsRestored`, `RefresherCancelsRefreshAfterTenSeconds`, `RefresherCancelsRunningRefreshOnDispose`; `CircuitConnectionTests.CircuitConnectionRaisesRestoredWhenConnectionComesBack`; `OverviewPageTests.OverviewPageShowsUpdatedTimeAfterRefresh`, `OverviewPageKeepsValuesAndShowsFailureLine` | `tests/Vandox.Backend.Tests/OverviewRefresherTests.cs`, `CircuitConnectionTests.cs`, `OverviewPageTests.cs` |
| AC8 | `OverviewPageTests.OverviewPagePrerenderedShowsLoadingAndReadsNothing`, `OverviewPageRefreshMakesFiveStoreCalls`; `GatedOverviewReaderTests.GatedOverviewReaderTakesOneGateSlotPerCall` | `OverviewPageTests.cs`, `tests/Vandox.Backend.Tests/GatedOverviewReaderTests.cs` |

Store tests write real batches through `SqliteStore` (`TempDirectory`), the jump of AC2 as two agent batches 10^15 numbers apart. Page
and view tests use `FakeTimeProvider`; the Tester writes the double `FakeOverviewReader` (records calls, scripted results, can hold a call).

## Approach

**Store (`Vandox.Storage`).** `IOverviewReader`, implemented by `SqliteStore` with the SQL in a new `OverviewQueries`; each method is one
reader lease and one gate slot (record 0218):

- `FreshnessAsync`: (1) the agent of `agent_streams` whose `max_seq` record (`records_agent_seq`) has the latest `received_at`, with that
  time; (2) `count(*)`, `min(from_at)`, `max(to_at)` of its open sequence gaps (`gaps_seq_open`); (3) the newest `liveScanLimit` rows of
  `records` of kind `metric` newest first (`records_kind_time`), the first with `live = 1` gives `LastLiveAt`, none gives
  `ScannedBackTo`, the oldest row read.
- `LatestValuesAsync`: per series `max(captured_at)`, then at most 10,000 rows of `[newest - Window, newest]` newest first; the first row
  per stored label text is kept, label sets counted, the first `MaxLabelSets` in ordinal order of the label text returned with labels
  parsed.
- `LatestMariaDbStatusAsync`: the newest `mariadb_status` record of source `mariadb` (`records_kind_source_time`) with `json_extract` of
  `availability`, `status.Threads_connected`, `status.Memory_used`, `variables.max_connections`; the payload is never decoded whole.
- `LatestServiceStatesAsync`: newest `captured_at` of `service_state` (`records_kind_time`); over `records_service_state` from
  `newest - Retirement`, the latest row per unit (`max(captured_at)` grouped by unit), `sub_state` read for those rows only, ordered
  `active_state <> 'active'` first then unit, at most `Limit`, `Total` by `count(*) OVER ()`.
- Refused before any read with `InvalidQueryException`: `store: invalid query: live scan limit must be 1 to 20000`, `... at least one
  series is required`, `... at most 16 series`, `... window must be positive and at most one day`, `... label sets must be 1 to 64`,
  `... retirement must be positive and at most one day`, `... limit must be 1 to 1024`.

**Backend (`src/Vandox.Backend/Overview/`).** `GatedOverviewReader` takes the `WebReaderGate` per call (as `GatedTimeSeriesReader`).
`OverviewView.Build(data, now)` is pure and turns the reads into `OverviewModel` with every rule of the spec (constants on
`OverviewView`: interval 30 s, deadline 10 s, stale 5 min, unit stale 1 h 5 min, unit retirement 2 h, mount window 10 min, live scan
20,000, 256 units, 16 mounts, 20 incident summaries; the series list). External text becomes runs through `LogText.Runs` with
`LogText.MaxShortChars` and 16 runs (`IncidentsView.MaxTextRuns`). Times through `LogsView.ShowSeconds`; ages, sizes, shares through
`OverviewText` (invariant culture).

`CircuitConnection` is a `CircuitHandler` registered scoped and also as `CircuitHandler` (same instance); `IsConnected` starts true
(prerender). `OverviewRefresher` (record 0219): `Start` runs one refresh, then a timer from `clock.CreateTimer` every 30 s; a tick while a
refresh runs or while disconnected starts none; `Restored` starts one unless one runs; each refresh gets a token cancelled after 10 s on
the clock or by `DisposeAsync`, which also stops the timer and waits for the running refresh. Exceptions other than a cancellation go to
the `fault` callback (the page passes them to `DispatchExceptionAsync`).

`OverviewPage` (`@page "/"`, `[Authorize]` from `_Imports`) injects `IOverviewReader`, `IIncidentBrowser`, `TimeProvider`,
`CircuitConnection`, `ILogger<OverviewPage>`; prerendered it reads nothing; once interactive it starts the refresher. A refresh makes the
five calls in turn (freshness, latest values, MariaDB, services, `IncidentSummariesAsync` with limit 20), then builds the model at the
clock's now and sets `Updated`. A deadline, `StoreException` or `InvalidQueryException` keeps the model and shows the failure line; the
latter two are logged with `OverviewReadFailed` (`overview: reading failed`, EventId 74). Before any success the line reads `Refresh
failed at <time>; no values have been read yet.` `ServeCommand` registers `IOverviewReader` as `GatedOverviewReader(store, gate)` and
`CircuitConnection`. The skeleton adds `OverviewPage` without `@page`; the implementation adds it and deletes `Home.razor`.

**Layout** (the page's markup; classes in `app.css`, no `style` attributes):

1. `<h1>Overview</h1>`, then `<p>Loading…</p>` prerendered, else `<p>Updated <time></p>` and, after a failure, `<p class="failure">…</p>`.
2. `<h2>Data freshness</h2>` with a `<dl>`: *Last live data*, *Last data received*, *Backfill*, *Agent spool*; a value is `<time> (<age>
   ago)` or `<time> (ahead by <age>)`, followed by `<span class="stale">stale</span>` when stale. Spool: `<size>, oldest unsent record
   <time>` or `<size>, nothing unsent`, then `as of <time>`.
3. `<h2>Services</h2>`: `<p class="stale">stale: no data received since <time></p>` when due; a table *Unit*, *State* (`<active_state>
   (<sub_state>)`), *Active since*, *Restarts*, *Recorded* (time, stale mark); a failed unit's row has class `failed`; `<p>and <n>
   more</p>`; or the E1 text.
4. `<h2>Key figures</h2>`: a table *Figure*, *Value*, *Time* in this row order: *Available memory* (`<size> (<share> of <total>)`),
   *Swap* (`<used> of <total> used (<share>)` or `no swap`), *Memory pressure* (`some <x> %, full <y> %`), *CPU pressure* (`some <x>
   %`), *I/O pressure* (as memory), *Disk* plus the mount as external text per mount (`<available> available (<share>) of <size>`),
   *MariaDB* (`up`, `down`, `not answering`), *MariaDB connections* (`<n> of <max>`), *MariaDB memory* (`<used> of at most <max>`);
   `unknown` for a missing value; `<p>and <n> more mounts</p>`.
5. `<h2>Incidents</h2>`: `<ul>` of open incidents (`<a href="incidents/<n>">#<n></a>, open, first kill <time>, <victim>`) or `<p>No open
   incident.</p>`; then `<p>Latest incident: <a>#<n></a>, first kill <time>, <victim></p>` with ` dated in the future` when due; or
   the E1 text.

## Options considered

1. **The time-series query of #47 for the figures** — lost: a range answer, not a latest value (an old value becomes missing, not stale),
   rollup lag at longer ranges, no `mariadb_status` or live flag, one gate slot per inner read (record 0218).
2. **A write-path column or partial index for the newest live record** — lost: changes the batch write path (security area 1) and
   its throughput for a read-only page; the bounded scan answers with a lower bound instead (record 0218).
3. **Push on ingest instead of a timer** — lost: couples ingest to circuits and fans out every 4 s (record 0219).
4. **Reuse `PageRunner`** — lost: it cancels the running read on a new one, so a slow store would never finish a refresh (record 0219).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `IOverviewReader`, `AgentFreshness`, `LatestValueQuery`, `LatestSeries`, `LatestValue`, `MariaDbLatest`, `LatestServiceQuery`, `ServiceStatePage`, `LatestServiceState`, `OverviewQueries` | new |
| Vandox.Storage | `SqliteStore` | implements `IOverviewReader` |
| Vandox.Backend | `Overview/GatedOverviewReader`, `CircuitConnection`, `OverviewRefresher`, `OverviewView`, `OverviewText`, `OverviewData`, `OverviewModel`, `OverviewUnitRow`, `OverviewFigureRow`, `OverviewIncidentRow` | new |
| Vandox.Backend | `Components/Pages/OverviewPage.razor`, `.razor.cs` | new; `Home.razor` deleted |
| Vandox.Backend | `Hosting/ServeCommand.cs`, `Hosting/BackendLog.cs`, `wwwroot/app.css` | registrations, `OverviewReadFailed`, classes `stale`, `failed`, `failure` |

## Signatures (for the Dev's skeleton)

Each type is public sealed with { get; init; } properties unless stated; a type's properties are listed in its span.

Vandox.Storage:

- `IOverviewReader { Task<AgentFreshness> FreshnessAsync(int liveScanLimit, CancellationToken cancellationToken); Task<IReadOnlyList<LatestSeries>> LatestValuesAsync(LatestValueQuery query, CancellationToken cancellationToken); Task<MariaDbLatest?> LatestMariaDbStatusAsync(CancellationToken cancellationToken); Task<ServiceStatePage> LatestServiceStatesAsync(LatestServiceQuery query, CancellationToken cancellationToken) }` (public interface; SqliteStore implements all four, public)
- `AgentFreshness { string AgentId; DateTimeOffset? ReceivedAt; DateTimeOffset? LastLiveAt; DateTimeOffset? ScannedBackTo; int OpenSequenceGaps; DateTimeOffset? MissingStart; DateTimeOffset? MissingEnd }`
- `LatestValueQuery { IReadOnlyList<SeriesKey> Series; TimeSpan Window; int MaxLabelSets }`
- `LatestSeries { SeriesKey Series; IReadOnlyList<LatestValue> Values; int LabelSets }`
- `LatestValue { IReadOnlyDictionary<string, string> Labels; DateTimeOffset CapturedAt; double Value }`
- `MariaDbLatest { DateTimeOffset CapturedAt; string Availability; double? ThreadsConnected; double? MemoryUsed; string? MaxConnections }`
- `LatestServiceQuery { TimeSpan Retirement; int Limit }`
- `ServiceStatePage { IReadOnlyList<LatestServiceState> States; int Total; DateTimeOffset? Newest }`
- `LatestServiceState { string Unit; string ActiveState; string SubState; string? ActiveEnterAt; long Restarts; DateTimeOffset CapturedAt }`

Vandox.Backend, namespace Vandox.Backend.Overview:

- `GatedOverviewReader(IOverviewReader inner, WebReaderGate gate)` (the constructor of GatedOverviewReader : IOverviewReader)
- `CircuitConnection { bool IsConnected { get; }; event EventHandler? Restored; override OnConnectionDownAsync, OnConnectionUpAsync }` (: CircuitHandler; the two overrides with the framework's signature Task (Circuit circuit, CancellationToken cancellationToken))
- `OverviewRefresher(TimeProvider clock, CircuitConnection connection, Func<CancellationToken, Task> refresh, Action<Exception> fault)` (the constructor of OverviewRefresher : IAsyncDisposable, with void Start() and ValueTask DisposeAsync())
- `OverviewView.Build(OverviewData data, DateTimeOffset now)` (public static class OverviewView, returns OverviewModel; also the static IReadOnlyList<SeriesKey> Series { get; } and the constants of *Approach*)
- `OverviewText { static string Age(TimeSpan age); static string Size(double bytes); static string Share(double part, double total) }` (public static class)
- `OverviewData { AgentFreshness Freshness; IReadOnlyList<LatestSeries> Values; MariaDbLatest? MariaDb; ServiceStatePage Services; IReadOnlyList<IncidentSummary> Incidents }` (settable)
- `OverviewModel { string LiveData; string Received; string Backfill; string Spool; string ServicesStale; string ServicesText; IReadOnlyList<OverviewUnitRow> Units; string MoreUnits; IReadOnlyList<OverviewFigureRow> Figures; string MoreMounts; IReadOnlyList<OverviewIncidentRow> OpenIncidents; OverviewIncidentRow? LatestIncident; string IncidentsText }`
- `OverviewUnitRow { IReadOnlyList<TextRun> Unit; string State; string ActiveSince; string Restarts; string Recorded; bool Failed; bool Stale }`
- `OverviewFigureRow { string Figure; IReadOnlyList<TextRun> Mount; string Value; string Time; bool Stale }`
- `OverviewIncidentRow { long Number; string Href; string FirstKill; IReadOnlyList<TextRun> Victim; bool Future }`
- `OverviewPage` (public sealed partial class in Components/Pages, : ComponentBase, IAsyncDisposable)
- `BackendLog.OverviewReadFailed(this ILogger logger, Exception exception)` (internal static partial)

Deleted: Home.razor (Components/Pages) is removed in step 6. Names checked: no nested type, at most four parameters, no parameter
named after another language's keyword, no enum.

## Test files

`tests/Vandox.Storage.Tests/SqliteStoreOverviewTests.cs`; `tests/Vandox.Backend.Tests/OverviewViewTests.cs`, `OverviewTextTests.cs`,
`OverviewRefresherTests.cs`, `CircuitConnectionTests.cs`, `OverviewPageTests.cs`, `GatedOverviewReaderTests.cs`; double
`tests/Vandox.Backend.Tests/FakeOverviewReader.cs`.

Existing test code: the Tester, in step 5, deletes `tests/Vandox.Backend.Tests/HomePageTests.cs` (its assertions are the replaced page),
changes `RoutesTests.cs:36` to `typeof(OverviewPage)` and `ServeTests.cs:490` to the heading `Overview` (both fail until the route
moves). No signature changes.

Code no unit test can cover: none (`app.css` holds no code; the page is rendered in tests).

## Areas

- New area *Overview page*, `docs/areas/web-ui-overview.md`: the page `/`, its sections, texts, sources, staleness, backfill, refresh,
  caps and memory per circuit (Lead decision; index row: scope as said, not here: the reads (storage reading), the gate (web UI)).
- `web-ui.md`, `storage-reading.md`, `time-series-query.md`: below.

## Documentation updates

All by the Dev, after the code is green:

- `docs/areas/web-ui-overview.md` new, from `_template.md`, with records 0218, 0219; its row in `docs/areas/README.md`.
- `docs/areas/web-ui.md`: *Scope* line 9 (overview exists), *Layout and navigation* line 35-38 (`Overview` is the overview page),
  *Store reads of the views* line 109 (the overview's calls share the gate), *Circuits*, *Not here* and *Implementation*
  (`GatedOverviewReader`, `CircuitConnection`).
- `docs/areas/storage-reading.md`: *Scope* and a section *Overview reads* (the four reads, bounds, errors).
- `docs/areas/time-series-query.md` line 7: "the overview and the history views" becomes "the history views".
- `docs/ARCHITECTURE.md` lines 10-11 (the views the service reads through), 132-140 (a sentence on the overview with 0218, 0219) and
  474-475 (`IOverviewReader` in the store interfaces).
- `CHANGELOG.md` *Unreleased*, *Added*: the overview page.
- `.squad/project.md` *Test doubles*, Blazor row: `FakeOverviewReader` — the Tester reports the text, the orchestrator writes it.
- `docs/areas/README.md` index row of *Web UI* (scope "the views": add the overview).

## Architecture check

No guarantee is weakened: missing data shows `unknown` or `stale`, never normal (0028); the page reads only; backfilled data alerts nothing.
The read gate bounds the page's load (record 0219 states the cost).

## Security considerations

- [x] No limits on parsed input: the page parses nothing external; label JSON read from the store is bounded by the wire format (32
  labels) and parsed for at most 64 label sets per series.
- [x] Exceptions: `StoreException`, `InvalidQueryException` and the deadline reach the user only as `Refresh failed at <time>; …`
  (no exception text); others go to the framework's error banner.
- External text (units, mounts, victims) only through `ExternalText` runs; links only `incidents/<number>` from `IncidentsView.IncidentHref`.

## Decision records

- `docs/decisions/0218-the-overview-reads-latest-values-through-its-own-bounded-store-reads.md` (Proposed)
- `docs/decisions/0219-the-overview-refreshes-on-a-timer-that-skips-overlaps-and-pauses-while-disconnected.md` (Proposed)

## Out of scope / follow-ups

Spec *Out of scope*. Image build check (`.squad/stack.md`) for the new `.razor` files and `app.css`: run by the Dev before the review.
