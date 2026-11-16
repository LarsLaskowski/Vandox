# Plan: Overview page

Source: Issue #48 | [spec.md](spec.md)
Tier: security — the session check changes (Security B1): activity of a circuit that shows the self-refreshing overview no longer extends the session.
Security areas touched: 3 Web UI login (a new page under `Components/Pages/`; the session extension in `Security/`), 10 Parsing of external input (label JSON and `json_extract` of stored agent records in the new store reads), 12 Logging and display of external text (unit names, mount points, victims on a page)

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
| 17 | A render acknowledgement passes the inbound-activity handler, whose session check calls `SessionStore.Touch`, so a self-refreshing page extends its session every 30 s; the handler's context carries no kind of activity | `SessionCircuitHandler.cs:61,83-90`, `SessionStore.cs:96-101`, record 0103 (context paragraph); Security's IL reading of `CircuitHost` 11.0.0 | confirmed |
| 18 | Stored `variables` values are any short text; `active_state`, `sub_state` and `availability` are pinned by the decoder at write | `MariaDbStatus.cs:127-128,160`, `ServiceState.cs:80-91` | confirmed |
| 19 | A record can carry a capture time far after now (a clock ahead is stored, backfilled) | storage.md:64, detection-and-reports.md (*The offset*) | confirmed |
| 16 | The issue's claims (services, key figures, incidents, freshness, backfill, spool are stored) | facts 1-5, 9; agent.md:149; "open incidents" and backfill "progress" refined in the spec | confirmed |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `SqliteStoreOverviewTests.FreshnessReturnsNewestLiveMetricAndHighestNumberReceiveTime`, `FreshnessReportsOldestScannedTimeWhenNoLiveRecordInScan`; `OverviewViewTests.BuildShowsLiveDataAndReceivedTimeWithAges`, `BuildMarksLiveDataStaleAfterFiveMinutes`, `BuildShowsBeforeWhenNoLiveRecordWasScanned`, `BuildShowsAheadByForTimeAfterNow`, `BuildShowsNoLiveDataTextWithoutAgent`, `BuildShowsReceivedUnknownForAgentWithoutRecords`; `SqliteStoreOverviewTests.FreshnessKeepsAgentWhoseRecordsWereRemoved`, `FreshnessIgnoresRecordsAfterUntil` | `tests/Vandox.Storage.Tests/SqliteStoreOverviewTests.cs`, `tests/Vandox.Backend.Tests/OverviewViewTests.cs` |
| AC2 | `SqliteStoreOverviewTests.FreshnessReturnsSpanOfOpenSequenceGapsAfterJump`, `FreshnessIgnoresOpenGapsOfOtherAgentIds`; `OverviewViewTests.BuildShowsBackfillRunningWithSpanAndNoCount`, `BuildShowsBackfillStalledWithoutDataForFiveMinutes`, `BuildShowsNoBackfillRunningWithoutOpenGap` | as AC1 |
| AC3 | `OverviewViewTests.BuildShowsSpoolSizeAndOldestUnsentRecord`, `BuildShowsNothingUnsentForAgeZero`, `BuildMarksSpoolSampleStale`, `BuildShowsSpoolUnknownWithoutSample` | `OverviewViewTests.cs` |
| AC4 | `SqliteStoreOverviewTests.LatestServiceStatesReturnsLatestPerUnitWithinRetirement`, `LatestServiceStatesOrdersNonActiveFirstAndCountsTotal`, `LatestServiceStatesIgnoresRecordAfterUntil` (current units plus one record a day ahead); `OverviewViewTests.BuildListsNonActiveUnitsFirstAndMarksFailed`, `BuildMarksServicesStaleWithoutBatchForFiveMinutes`, `BuildMarksUnitStaleAfterOneHourFiveMinutes`, `BuildShowsMoreUnitsBeyondTheCap`; `OverviewPageTests.OverviewPageShowsUnitNameAsExternalText` | Storage and Backend files above, `tests/Vandox.Backend.Tests/OverviewPageTests.cs` |
| AC5 | `SqliteStoreOverviewTests.LatestValuesReturnsNewestPerLabelSetWithinWindow`, `LatestValuesCountsLabelSetsBeyondTheLimit`, `LatestValuesIgnoresValueAfterUntil`, `LatestValuesKeepsOnlyTheLimitOfLargeLabelSets` (300 distinct sets of 32 labels of 1,000 bytes, limit 4), `LatestMariaDbStatusReturnsSelectedMembers`, `LatestMariaDbStatusIgnoresRecordAfterUntil`; `LabelSetTallyTests.TallyKeepsTheSmallestUpToTheLimitAndCountsEverySet` (10,000 distinct adds, at most the limit kept); `OverviewViewTests.BuildShowsKeyFiguresWithTimes`, `BuildMarksFigureStaleAfterFiveMinutes`, `BuildShowsUnknownForMissingFigure`, `BuildShowsNoSwapAndNoShareForZeroTotal`, `BuildShowsMountsUpToTheCapWithMore`, `BuildShowsMaxConnectionsUnknownWhenNotAnInteger` (the value `5` followed by U+202E, built in the test from the code point with `char.ConvertFromUtf32`, shows `unknown`); `OverviewTextTests.SizeWritesBinaryUnitsWithOneDecimal`, `ShareWritesPercentWithOneDecimal`, `AgeWritesTheUnitOfItsRange` | as above, `tests/Vandox.Backend.Tests/OverviewTextTests.cs` |
| AC6 | `OverviewViewTests.BuildListsOpenIncidentWithLink`, `BuildShowsNoOpenIncidentAndLatestIncident`, `BuildNeverOpensIncidentWithKillAfterNow`, `BuildShowsNoIncidentsText` | `OverviewViewTests.cs` |
| AC7 | `OverviewRefresherTests.RefresherRefreshesAtStartAndEveryThirtySeconds`, `RefresherSkipsDueRefreshWhileOneRuns`, `RefresherStartsNoRefreshWhileDisconnected`, `RefresherRefreshesWhenConnectionIsRestored`, `RefresherCancelsRefreshAfterTenSeconds`, `RefresherCancelsRunningRefreshOnDispose`, `RefresherIgnoresRestoredAfterDispose`; `CircuitConnectionTests.CircuitConnectionRaisesRestoredWhenConnectionComesBack`, `CircuitConnectionRaisesNothingOnFirstConnection`; `OverviewPageTests.OverviewPageShowsUpdatedTimeAfterRefresh`, `OverviewPageKeepsValuesAndShowsFailureLine` | `tests/Vandox.Backend.Tests/OverviewRefresherTests.cs`, `CircuitConnectionTests.cs`, `OverviewPageTests.cs` |
| AC9 | `SessionCircuitHandlerTests.InboundActivityDoesNotExtendSessionWhilePaused` (`FakeTimeProvider`: activity every 30 s while paused, the check after 1 h since the last unpaused activity throws `SessionEndedException`), `InboundActivityExtendsSessionAfterPauseEnds`; `SessionActivityTests.PauseHoldsUntilItsLastScopeIsDisposed`; `OverviewPageTests.OverviewPagePausesSessionExtensionWhileShown` | `tests/Vandox.Backend.Tests/SessionCircuitHandlerTests.cs`, `SessionActivityTests.cs`, `OverviewPageTests.cs` |
| AC8 | `OverviewPageTests.OverviewPagePrerenderedShowsLoadingAndReadsNothing`, `OverviewPageRefreshMakesFiveStoreCalls`; `GatedOverviewReaderTests.GatedOverviewReaderTakesOneGateSlotPerCall` | `OverviewPageTests.cs`, `tests/Vandox.Backend.Tests/GatedOverviewReaderTests.cs` |

`LabelSetTallyTests` is in `tests/Vandox.Storage.Tests/LabelSetTallyTests.cs`. Store tests write real batches through `SqliteStore` (`TempDirectory`), the jump of AC2 as two agent batches 10^15 numbers apart. Page
and view tests use `FakeTimeProvider`; the Tester writes the double `FakeOverviewReader` (records calls, scripted results, can hold a call).

## Approach

**Store (`Vandox.Storage`).** `IOverviewReader`, implemented by `SqliteStore` with the SQL in a new `OverviewQueries`; each method is one
reader lease and one gate slot (record 0218):

Every read takes `until` (the page passes its now plus 90 seconds, the live allowance) and ignores records captured after it, so a
clock far ahead hides no current data (DA major 1).

- `FreshnessAsync`: (1) the agent of `agent_streams` whose `max_seq` record (`records_agent_seq`) has the latest `received_at`, with that
  time; an agent row whose record is gone (retention) is still returned, the one with the latest `max_captured_at`, with `ReceivedAt`
  null; no row gives an empty `AgentId`; (2) `count(*)`, `min(from_at)`, `max(to_at)` of its open sequence gaps (`gaps_seq_open`); (3) the newest `liveScanLimit` rows of
  `records` of kind `metric` captured at or before `until`, newest first (`records_kind_time`), the first with `live = 1` gives `LastLiveAt`, none gives
  `ScannedBackTo`, the oldest row read.
- `LatestValuesAsync`: per series the newest `captured_at` not after `Until`, then at most 10,000 rows of `[newest - Window, newest]`
  newest first, streamed into a `LabelSetTally` (Security B3): it keeps at most `MaxLabelSets` label texts, the smallest in ordinal order
  (a new text smaller than the largest kept evicts it), each with its first (newest) row, and counts every distinct text by the first 8
  bytes of its SHA-256 without keeping the text; each row's text is read, hashed and dropped. A read thus holds at most 16 texts of at
  most about 74 KB and 10,000 hashes, never every text of the scan. The kept labels are parsed at the end.
- `LatestMariaDbStatusAsync`: the newest `mariadb_status` record of source `mariadb` not after `until` (`records_kind_source_time`) with `json_extract` of
  `availability`, `status.Threads_connected`, `status.Memory_used`, `variables.max_connections`; the payload is never decoded whole. `MaxConnections` is the text parsed as a non-negative integer
  (invariant culture, at most 20 ASCII digits) or null (Security B2), so no stored text reaches the page outside the external-text display.
- `LatestServiceStatesAsync`: newest `captured_at` of `service_state` not after `Until` (`records_kind_time`); over `records_service_state` from
  `[newest - Retirement, newest]`, the latest row per unit (`max(captured_at)` grouped by unit), `sub_state` read for those rows only, ordered
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
(prerender); `Restored` is raised by `OnConnectionUpAsync` only after an `OnConnectionDownAsync` (the first connection also calls up). `OverviewRefresher` (record 0219): `Start` runs one refresh, then a timer from `clock.CreateTimer` every 30 s; a tick while a
refresh runs or while disconnected starts none; `Restored` starts one unless one runs; each refresh gets a token cancelled after 10 s on
the clock or by `DisposeAsync`, which also unsubscribes from `Restored` (the scoped connection outlives the page), stops the timer and
waits for the running refresh. Timer and `Restored` callbacks run off the circuit's dispatcher: the page's refresh delegate reads off it
and applies the model and `StateHasChanged` through `InvokeAsync`. Exceptions other than a cancellation go to
the `fault` callback (the page passes them to `DispatchExceptionAsync`).

`OverviewPage` (`@page "/"`, `[Authorize]` from `_Imports`) injects `IOverviewReader`, `IIncidentBrowser`, `TimeProvider`,
`CircuitConnection`, `ILogger<OverviewPage>`; prerendered it reads nothing; once interactive it starts the refresher. A refresh makes the
five calls in turn (freshness, latest values, MariaDB, services, `IncidentSummariesAsync` with limit 20), then builds the model at the
clock's now and sets `Updated`. A deadline, `StoreException` or `InvalidQueryException` keeps the model and shows the failure line; the
latter two are logged with `OverviewReadFailed` (`overview: reading failed`, EventId 74). Before any success the line reads `Refresh
failed at <time>; no values have been read yet.` `ServeCommand` registers `IOverviewReader` as `GatedOverviewReader(store, gate)` and
`CircuitConnection`. `active_enter_at` is parsed as a time and shown through `LogsView.ShowSeconds`, `unknown` when it does not parse.

**Session (Security B1, record 0220).** `SessionActivity` (`Security/`, scoped per circuit): `Pause()` returns a scope; while one is open
`IsPaused` is true. `SessionCircuitHandler.CheckSessionAsync` uses `SessionStore.IsActive` (check without extending) while paused and
`Touch` otherwise, so an ended session still ends the circuit. `OverviewPage` takes the pause when it starts interactively and disposes
it in `DisposeAsync`, so no activity of a circuit showing the overview — refresh acknowledgements, and also a click on a navigation link
of the page — extends the session; the next page's render after navigation does again. An overview left open ends its session 1 hour
after the last activity elsewhere (HTTP request, another page, another tab); the page's 30-second session check then goes to `/login`, which disposes the page and its pause (the check of an
acknowledgement is discarded, record 0103). The handler cannot tell an acknowledgement from user input (fact 17), so pausing per page is
the only exact form; stopping the refresh after a quiet period would still extend the session by that period. `WebAuthentication`
registers `SessionActivity` scoped. The skeleton adds `OverviewPage` without `@page`; the implementation adds it and deletes `Home.razor`.

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
5. **Session: let refreshes extend the session** (rule changed to "an open overview keeps its session") — lost: an unattended tab would
   keep a session for 12 hours against the idle rule (record 0220).
6. **Session: stop refreshing after a quiet period** — lost: the page sees no user input, and the last refresh still extends the
   session by the period (record 0220).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `IOverviewReader`, `AgentFreshness`, `LatestValueQuery`, `LatestSeries`, `LatestValue`, `MariaDbLatest`, `LatestServiceQuery`, `ServiceStatePage`, `LatestServiceState`, `LabelSetTally`, `OverviewQueries` | new |
| Vandox.Backend | `Security/SessionActivity` | new |
| Vandox.Backend | `Security/SessionCircuitHandler`, `Security/WebAuthentication` | the pause in the session check; registration |
| Vandox.Storage | `SqliteStore` | implements `IOverviewReader` |
| Vandox.Backend | `Overview/GatedOverviewReader`, `CircuitConnection`, `OverviewRefresher`, `OverviewView`, `OverviewText`, `OverviewData`, `OverviewModel`, `OverviewUnitRow`, `OverviewFigureRow`, `OverviewIncidentRow` | new |
| Vandox.Backend | `Components/Pages/OverviewPage.razor`, `.razor.cs` | new; `Home.razor` deleted |
| Vandox.Backend | `Hosting/ServeCommand.cs`, `Hosting/BackendLog.cs`, `wwwroot/app.css` | registrations, `OverviewReadFailed`, classes `stale`, `failed`, `failure` |

## Signatures (for the Dev's skeleton)

Each type is public sealed with { get; init; } properties unless stated; a type's properties are listed in its span.

Vandox.Storage:

- `IOverviewReader { Task<AgentFreshness> FreshnessAsync(int liveScanLimit, DateTimeOffset until, CancellationToken cancellationToken); Task<IReadOnlyList<LatestSeries>> LatestValuesAsync(LatestValueQuery query, CancellationToken cancellationToken); Task<MariaDbLatest?> LatestMariaDbStatusAsync(DateTimeOffset until, CancellationToken cancellationToken); Task<ServiceStatePage> LatestServiceStatesAsync(LatestServiceQuery query, CancellationToken cancellationToken) }` (public interface; SqliteStore implements all four, public)
- `AgentFreshness { string AgentId; DateTimeOffset? ReceivedAt; DateTimeOffset? LastLiveAt; DateTimeOffset? ScannedBackTo; int OpenSequenceGaps; DateTimeOffset? MissingStart; DateTimeOffset? MissingEnd }`
- `LatestValueQuery { IReadOnlyList<SeriesKey> Series; TimeSpan Window; int MaxLabelSets; DateTimeOffset Until }`
- `LatestSeries { SeriesKey Series; IReadOnlyList<LatestValue> Values; int LabelSets }`
- `LatestValue { IReadOnlyDictionary<string, string> Labels; DateTimeOffset CapturedAt; double Value }`
- `MariaDbLatest { DateTimeOffset CapturedAt; string Availability; double? ThreadsConnected; double? MemoryUsed; long? MaxConnections }`
- `LatestServiceQuery { TimeSpan Retirement; int Limit; DateTimeOffset Until }`
- `LabelSetTally(int limit)` (internal sealed class; internal void Add(string labels, DateTimeOffset capturedAt, double value), internal int Count { get; }, internal IReadOnlyList<(string Labels, DateTimeOffset CapturedAt, double Value)> Kept { get; } in ordinal order of the labels)
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

Vandox.Backend, namespace Vandox.Backend.Security:

- `SessionActivity.Pause()` (public sealed class SessionActivity with public IDisposable Pause() and public bool IsPaused { get; })
- `SessionCircuitHandler(AuthenticationStateProvider authenticationStateProvider, SessionStore sessions, SessionActivity activity)` (the changed constructor)

Deleted: Home.razor (Components/Pages) is removed in step 6. Names checked: no nested type, at most four parameters, no parameter
named after another language's keyword, no enum.

## Test files

`tests/Vandox.Storage.Tests/SqliteStoreOverviewTests.cs`; `tests/Vandox.Backend.Tests/OverviewViewTests.cs`, `OverviewTextTests.cs`,
`OverviewRefresherTests.cs`, `CircuitConnectionTests.cs`, `OverviewPageTests.cs`, `GatedOverviewReaderTests.cs`; double
`tests/Vandox.Backend.Tests/FakeOverviewReader.cs`.

Existing test code: the Tester, in step 5, deletes `tests/Vandox.Backend.Tests/HomePageTests.cs` (its assertions are the replaced page),
changes `RoutesTests.cs:36` to `typeof(OverviewPage)` and `ServeTests.cs:490` to assert `<h1>Overview</h1>` (the navigation link
already says *Overview*; both fail until the route moves). The changed `SessionCircuitHandler` constructor: the Dev adds `new
SessionActivity()` at `SessionCircuitHandlerTests.cs:32,70,71,100,145,204,238` in step 4, mechanically.

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
- `docs/areas/web-login.md` *Sessions* (lines 79-80): the activity of a live connection extends the session except while it shows the
  overview, whose refreshes are no activity; link record 0220.
- `docs/areas/storage-reading.md` *Overview reads*: the `until` bound and the label-set tally with its memory bound.

## Architecture check

No guarantee is weakened: missing data shows `unknown` or `stale`, never normal (0028); the page reads only; backfilled data alerts nothing.
The read gate bounds the page's load (record 0219 states the cost).

## Security considerations

- [x] Limits on parsed input: label JSON read from the store is bounded by the wire format (32 labels, values of at most 1,024 UTF-8
  bytes, checked at decode) and parsed for at most 16 kept label sets per series; a read holds at most 16 label texts and 10,000
  8-byte hashes (B3); `max_connections` is parsed from at most 20 ASCII digits, else null.
- [x] Exceptions: `StoreException`, `InvalidQueryException` and the deadline reach the user only as `Refresh failed at <time>; …`
  (no exception text); others go to the framework's error banner.
- External text (units, mounts, victims) only through `ExternalText` runs; links only `incidents/<number>` from `IncidentsView.IncidentHref`.
- Plain strings only for values the decoder pins at write (`active_state`, `sub_state`, `availability`: `ServiceState.cs:80-91`,
  `MariaDbStatus.cs:160`) and for numbers and times the view formats itself (`active_enter_at` parsed, never shown as stored;
  `max_connections` as a parsed integer) (N2).
- Sessions: an ended session still ends the overview's circuit (the paused check uses `IsActive`); the pause covers only circuits
  showing the overview and ends with the page (B1, record 0220).

## Decision records

- `docs/decisions/0218-the-overview-reads-latest-values-through-its-own-bounded-store-reads.md` (Proposed)
- `docs/decisions/0219-the-overview-refreshes-on-a-timer-that-skips-overlaps-and-pauses-while-disconnected.md` (Proposed)
- `docs/decisions/0220-a-circuit-showing-the-overview-does-not-extend-its-session.md` (Proposed)

## Out of scope / follow-ups

Spec *Out of scope*. Image build check (`.squad/stack.md`) for the new `.razor` files and `app.css`: run by the Dev before the review.

## Challenge

- Major 1 (a record dated after now hides current data) — accepted: every read takes `until` = now + 90 s and anchors on the newest
  record not after it (*Approach*); later records are not shown (spec E13); store tests with current records plus one a day ahead for
  metrics, services and MariaDB (AC1, AC4, AC5 rows).
- Minor 2 (refresher threading, handler leak, first up) — accepted: `InvokeAsync`, unsubscribe in `DisposeAsync`, `Restored` only after a
  down; tests `RefresherIgnoresRestoredAfterDispose`, `CircuitConnectionRaisesNothingOnFirstConnection`.
- Minor 3 (ServeTests passes through the navigation link) — accepted: it asserts `<h1>Overview</h1>`.
- Minor 4 (agent row without records) — accepted: the agent is kept with `ReceivedAt` null, shown `unknown` and `stale`; the E1 text only
  without any agent row (spec E14); tests `FreshnessKeepsAgentWhoseRecordsWereRemoved`, `BuildShowsReceivedUnknownForAgentWithoutRecords`.

## Security review

- B1 (refreshes defeat the idle timeout) — confirmed in the code (fact 17). Decided (a), keep the rule: a circuit showing the overview
  pauses session extension (`SessionActivity`), the check without extension still ends an ended session; spec AC9/E15, tests in the AC9
  row, record 0220, `web-login.md` *Sessions* states the exception. Because this changes the session check, the tier is raised to
  `security`.
- B2 (`max_connections` as plain text) — accepted: `long? MaxConnections`, parsed or `unknown`; test with U+202E (AC5 row); spec AC5.
- B3 (the label scan keeps every distinct text) — accepted: `LabelSetTally` keeps at most the limit and counts by hash; tests
  `LabelSetTallyTests…` and `LatestValuesKeepsOnlyTheLimitOfLargeLabelSets`.
- N1 — accepted: area 10 under *Security areas touched*.
- N2 — accepted: bullet under *Security considerations*.

## Security notes

Security plan review, round 2 (delta check): feature #48, Overview page (`specs/feature-48-overview-page/`, HEAD a9a379e)

All five earlier findings are resolved, and the new `SessionActivity` pause is sound. I have three non-blocking points: one wrong sentence about how the circuit ends, and two small hardening items for the pause.

I read the plan/spec diff, record 0220, `SessionRevalidatingStateProvider.cs`, `WebAuthentication.cs:86-87` and `Components/Routes.razor`. I ran no new measurement. The record-0103 behavior I rely on below (the result of the session check on a render acknowledgement is not awaited) was read from the ASP.NET Core sources when that record was written. My IL probe from round 1 confirms only that the acknowledgement passes the handler, not how its result is handled.

**Earlier findings**
- **B1 (refreshes kept the session alive): resolved.**
  - The design is now route (a), keeping the 1-hour idle rule. The tier is raised to `security`, and E15, AC9 and record 0220 are added.
  - The doc update to web-login.md *Sessions* is listed, and the AC9 tests pin it with `FakeTimeProvider`.
  - While paused, the check uses `SessionStore.IsActive`, which still finds ended sessions and forgets them (`Find(key, false)` runs `IsEnded`). So a paused circuit cannot keep a session alive, and a revoked or signed-out session is no longer found.
- **B2 (`max_connections` as plain text): resolved.**
  - It is now `long? MaxConnections`, parsed invariant from at most 20 ASCII digits, otherwise `unknown`.
  - The U+202E test is in the AC5 row.
  - A 20-digit value overflows `long`. With `TryParse` that gives null, which is safe.
- **B3 (label scan kept every distinct text): resolved.**
  - `LabelSetTally` keeps at most the limit of texts plus 10,000 hashes of 8 bytes each.
  - It is pinned by `LabelSetTallyTests…` and `LatestValuesKeepsOnlyTheLimitOfLargeLabelSets`.
  - The store accepts up to 64 sets, so the worst case is about 4.7 MB per read. The page's own figure of 16 texts is smaller.
- **N1 (area 10 not listed): resolved.** Area 10 is now under *Security areas touched*.
- **N2 (plain-string fields): resolved.** The bullet is in *Security considerations*. `active_enter_at` is parsed, and shows `unknown` when it does not parse.
- **The Devil's Advocate's `until` fix:** every read now ignores records captured more than 90 seconds after now. That stops a forged future record from hiding current data. I see no new risk; the filter is `captured_at <= until` and the indexes stay usable.

**Review of the `SessionActivity` pause**
- **Can the pause leak?** Only toward a stricter session, never a looser one. A pause that is never released means less extension, so the user is signed out sooner; it never extends a session. Looked at path by path:
  - Leaving the page disposes the component, which releases the pause.
  - The service is scoped per circuit, so a pause cannot move to another circuit. A reconnect resumes the same circuit with the overview still shown, which is correct.
  - Prerendering runs in a different scope and takes no pause.
  - A sign-in again in another tab moves the circuit to the new session, and the pause still applies.
- **Can a paused circuit outlive its session?** No. The 30-second revalidation calls `IsActive` (`SessionRevalidatingStateProvider.cs:45-48`). When it fails, the auth state turns anonymous and `AuthorizeRouteView` shows `RedirectToLogin`, which disposes the page, the refresher and the pause. Any user event in between throws at the check and terminates the circuit. At most one more refresh can run before the redirect; that window already exists and web-login.md:97 documents it.

**[NON-BLOCKING] D1. The plan wrongly says the circuit ends at the next acknowledgement.**
- Plan *Session* paragraph: "the circuit then ends at the next acknowledgement (rejected reconnect dialog, sign-in again)". Record 0220, *Consequences*, says "the circuit then ends".
- Per record 0103:22 and web-login.md:94, the result of the check on an acknowledgement is discarded. An unattended page actually ends through the 30-second revalidation, which redirects to `/login`.
- **Smallest fix, verbatim**, replacing the plan sentence "An overview left open ends its session 1 hour after the last activity elsewhere (HTTP request, another page, another tab), and the circuit then ends at the next acknowledgement (rejected reconnect dialog, sign-in again)." with:
  `An overview left open ends its session 1 hour after the last activity elsewhere (HTTP request, another page, another tab); the page's 30-second session check then goes to `/login`, which disposes the page and its pause (the check of an acknowledgement is discarded, record 0103).`
- The same correction applies to the first *Consequences* bullet of 0220, verbatim:
  `- An open overview ends its session 1 hour after the last activity elsewhere; within 30 seconds the page's session check goes to `/login`, and the user signs in again.`
- Recommendation: fix now; it is a documentation-only change.

**[NON-BLOCKING] D2. A scope released twice could switch the pause off for a later overview page.**
- `SessionActivity` presumably counts open scopes (the test `PauseHoldsUntilItsLastScopeIsDisposed`). If a scope can be released twice, the count can drop below zero. Then the next overview page's `Pause()` brings it back only to 0, and B1 returns for that circuit.
- The count is also read from hub threads (the inbound handler) and changed on the dispatcher.
- **Smallest fix:** each scope releases only once (an `Interlocked.Exchange` flag), and the count uses `Interlocked` / `Volatile`. Add the test `SessionActivityTests.PauseScopeDisposedTwiceReleasesOnce`.
- Recommendation: fix now (one test).

**[NON-BLOCKING] D3. Release the pause first when the page is disposed.**
- `OverviewPage.DisposeAsync` should release the pause in a `finally`, or before awaiting the refresher. Then an exception while the refresher is being disposed cannot leave the circuit paused on the next pages.
- This affects usability only (the user is signed out sooner), not security.
- Recommendation: fix now, as a note in the plan's *Session* paragraph.
