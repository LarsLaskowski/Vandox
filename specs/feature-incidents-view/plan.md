# Plan: Incidents view

Source: Issue #27 | [spec.md](spec.md)
Status: Draft
Tier: security — the change adds the first user write of the web UI (a note posted behind the login with an antiforgery
token, security area 3), shows stored external text (victims, programs, hosts, timeline texts, templates) in new pages and
in chart labels (area 12), adds a schema step and changes the incident merge transaction (storage, area 10 bounds on
reading stored incident data), and changes the reader gate the logs view relies on.

## Problem / root cause

Feature; summary of [spec.md](spec.md): a list of the stored incidents and one page per incident with header, kills,
hypothesis, timeline, three charts of the window (memory per program stacked, available memory and swap, connections),
the stored connection peak, metric extremes and templates, every time linked to the logs of that moment, and a note per
incident that survives re-runs and merges.

Claims of the issue and of the earlier check, verified against the code (`main` a36d976):

- *Incidents exist and are stored with timeline, hypothesis, metrics, connections* — **confirmed**: `Incident`
  (`src/Vandox.Core/Incidents/Incident.cs`) holds `kills`, `timeline` (with `backup_first_seen`/`backup_last_seen`
  entries, `oom_kill`, `boot`, service events), `hypothesis`, `metrics`, `connections`, `new_templates`,
  `template_spikes`; `IncidentRepository` stores it as JSON (`data`, at most 8 MiB).
- *The chart data is not in the incident document* — **confirmed**: the document keeps only extremes
  (`MetricExtreme`) and the connection peak (`ConnectionPeak`); per-snapshot memory per program, the
  `mem_available_bytes`/`swap_used_bytes` series and the socket counts must be read from `records`/`metrics` of the window
  (`RecordQueries.Build`: series by kind, source and name; snapshots by kind and optional source; keyset `AfterId`).
- *Schema version* — **confirmed 9** (`StorageLimits.SchemaVersion = 9`, `SchemaMigrator` step 9 adds `incidents`); the
  notes table is step **10**.
- *Merges delete incidents* — **confirmed**: `IncidentRepository.SaveAsync` (lines 84–90) deletes every overlapped
  incident but the oldest with `DELETE FROM incidents WHERE id = $id`; an update keeps `id` and `created_at`. So a note keyed
  by the incident number survives an update and an unchanged save but must be moved before that `DELETE`. `foreign_keys` is on
  for the writer, so a note table with a foreign key to `incidents(id)` makes a delete without the move fail instead of losing
  notes.
- *Open limit #213* — **confirmed**: `docs/areas/storage.md` line 162 leaves the page limit of `IncidentsAsync` open for #27;
  `IncidentsAsync` decodes every incident of a page (up to 8 MiB each). This plan does not page the list through it (below).
- *Login and antiforgery from #25* — **confirmed**: the fallback policy requires an authenticated user for every endpoint
  (`WebAuthentication.AddServices` line 82), `POST /logout` is the existing pattern of a form post with
  `RequiredAntiforgery` and an `IAntiforgeryValidationFeature` check (`WebAuthentication.LogoutAsync`), and the form limits
  are 16 KiB per value, 64 values, 64 KiB multipart (`MaxFormValueLength` etc.). A plain `<form method="post">` with
  `<AntiforgeryToken />` inside the interactive layout already works (`MainLayout.razor` sign-out form).
- *URL filter format of the logs view (#26)* — **confirmed**: `LogsAddress` (`from`/`to` as `yyyy-MM-ddTHH:mm[:ss]` UTC,
  `from` inclusive, `to` exclusive, validated by `ToQuery()`; `FormatTime` drops sub-seconds, so a time is floored to the
  second before it is written).
- *Two concurrent log-view calls across the service* — **confirmed**: `GatedLogExplorer` with
  `LogsView.MaxConcurrentQueries = 2`, registered in `ServeCommand.Build` line 209; the store has four readers.
- *uPlot writes labels and title as text* — **confirmed by reading the vendored file**: `uPlot.esm.js` line 2944
  (`title.textContent = opts.title`) and lines 3250–3253 (a label that is not an `HTMLElement` goes to `textContent`; a label
  passed through JS interop is a string).
- *The chart module draws lines only* — **confirmed**: `charts.js` has no fill, no bands, four series colors
  (`seriesColorCount = 4`); a stacked chart needs a new export.

Related defects found on the way: none in the code. One stale statement: record 0096, *Consequences*, says the incidents view
pages `IncidentsAsync` with a small limit; this plan reads summaries instead, so the Lead edits that consequence in place
(0096 is unreleased).

## Acceptance criteria

Each criterion names its tests (the Tester writes exactly these first; files under *Test files*).

- [ ] **AC1 — schema 10 adds the notes table.** A database at version 9 is migrated to 10: table `incident_notes` and index
  `incident_notes_incident` exist and are empty, stored incidents and log lines are kept, version 11 is refused with
  `store: schema version 11 is not supported, this build uses 10`.
  Test: `SqliteStoreOpenTests.SqliteStoreOpenMigratesVersionNineAndAddsIncidentNotes`.
- [ ] **AC2 — notes are stored (issue AC 3).** `SaveIncidentNoteAsync` stores, reads back, replaces and (with an empty text)
  deletes the note of an incident; the note written for a merged incident can be changed only through the incident it now
  belongs to; an unknown incident or a foreign note gives `NotFound` and changes nothing; every rule is refused with its exact
  text (*Rules* below), a text of 1,800 UTF-16 characters is accepted, 1,801 refused.
  Tests: `SqliteStoreIncidentBrowserTests.SqliteStoreSaveIncidentNoteStoresReadsAndReplacesNote`,
  `SqliteStoreSaveIncidentNoteWithEmptyTextDeletesNote`, `SqliteStoreSaveIncidentNoteForUnknownIncidentOrForeignNoteIsNotFound`,
  `SqliteStoreSaveIncidentNoteRefusesInvalidEdits` (one `DataRow` per rule), `SqliteStoreIncidentNotesReadsAtMostSixtyFour`
  (65 rows → 64 and `More`).
- [ ] **AC3 — notes survive re-runs and follow merges (issue AC 3).** An updated and an unchanged save keep the incident's
  notes; a save that merges incidents moves the notes of every deleted incident to the kept one in the same transaction,
  each keeping `written_for`; `vandoxd incidents` run twice over the same data keeps a note.
  Tests: `SqliteStoreIncidentTests.SqliteStoreSaveIncidentKeepsNotesWhenUpdatedOrUnchanged`,
  `SqliteStoreIncidentTests.SqliteStoreSaveIncidentMovesNotesOfMergedIncidents`,
  `IncidentReconstructorTests.IncidentReconstructorKeepsNotesAcrossReruns`.
- [ ] **AC4 — the list reads bounded summaries.** `IncidentSummariesAsync` returns incidents newest first (window start
  descending, then number descending), continues after `Before` without gap or repeat also across equal starts, carries
  first victim, last kill, hypothesis, note flag and times, refuses `limit` outside 1–100, `Before` outside the storable range
  and `Before.Id` ≤ 0 with the exact texts; `IncidentAsync` returns one incident or `null`, refuses an ID ≤ 0; undecodable data
  is `store: reading incidents: decoding incident data`.
  Tests: `SqliteStoreIncidentBrowserTests.SqliteStoreIncidentSummariesPageNewestFirst`,
  `SqliteStoreIncidentSummariesCarryVictimHypothesisAndNoteFlag`, `SqliteStoreIncidentSummariesRefuseInvalidQueries`,
  `SqliteStoreIncidentReadsOneIncidentOrNone`, `SqliteStoreIncidentSummariesReportUndecodableData`.
- [ ] **AC5 — the list page (spec AC-S4).** `/incidents` shows 20 rows newest first with number link, first-kill time,
  duration (`H:MM:SS`, first to last kill), kill count, victim (+ further kills), hypothesis (growth list / `unknown` / `none`,
  backup `running` / `not running` / `unknown`) and a note marker; *Older incidents* asks for the next page with `Before` = the
  last row and `Limit` 20; it queries nothing while prerendered; it shows the exact empty, deadline and failure texts.
  Tests: `IncidentsPageTests.IncidentsPageShowsRowsNewestFirstWithTimeVictimDurationHypothesis`,
  `IncidentsPageTests.IncidentsPagePagesTwentyAtATime`, `IncidentsPageTests.IncidentsPageQueriesNothingWhilePrerendered`,
  `IncidentsPageTests.IncidentsPageShowsEmptyDeadlineAndFailureTexts`,
  `IncidentRowViewTests.IncidentRowViewFormatsDurationVictimAndHypothesis`.
- [ ] **AC6 — a time opens the logs of that moment (issue AC 2).** `IncidentsView.LogsHref(t)` is `logs` plus the
  `ToQuery()` of a `LogsAddress` with `From` = `t` floored to the whole second minus 30 s and `To` = that second plus 5 min,
  clipped to the first and last whole second of the storable range; the result parses back through `LogsAddress.Parse` without
  error. Every time on the incident page (kills, first/last kill, timeline entries, last boot, metric extremes, connection peak,
  backup first/last seen) links there.
  Tests: `IncidentsViewTests.IncidentsViewLogsHrefSpansThirtySecondsBeforeToFiveMinutesAfter` (`DataRow`s: whole second,
  fractional second, near the minimum, near the maximum), `IncidentPageTests.IncidentPageLinksEveryTimeToTheLogsOfThatMoment`.
- [ ] **AC7 — memory per program in the window (issue AC 1).** `IncidentWindowReader` reads the process snapshots of
  `[start, end)` of one source (the source of the earliest process snapshot in the window, else of the earliest metric record)
  and `ProgramMemorySeries` turns them into a stacked chart: one point per snapshot, the five programs with the highest peak RSS
  in the window (ties by ordinal name) and `Other programs`, values in MiB, a program absent from a snapshot is 0, labels are
  `LogText.Escape(program, 64)`, title `Memory per program (MiB, stacked)`; snapshots before the start, at the end and of
  another source are not used; a snapshot not later than the previous one is skipped; more than 4,096 programs go to
  `Other programs`; after 300 snapshots the chart stops and names the last time.
  Tests: `ProgramMemorySeriesTests.ProgramMemorySeriesStacksTopFiveProgramsAndOthersInMib`,
  `ProgramMemorySeriesTests.ProgramMemorySeriesRanksByPeakAndFillsMissingProgramsWithZero`,
  `ProgramMemorySeriesTests.ProgramMemorySeriesEscapesAndCapsProgramLabels`,
  `ProgramMemorySeriesTests.ProgramMemorySeriesSkipsSnapshotsThatDoNotAdvance`,
  `ProgramMemorySeriesTests.ProgramMemorySeriesCountsProgramsBeyondTheCapAsOther`,
  `IncidentWindowReaderTests.IncidentWindowReaderChartsMemoryPerProgramWithinTheWindow`,
  `IncidentWindowReaderTests.IncidentWindowReaderUsesTheSourceOfTheFirstSnapshot`,
  `IncidentWindowReaderTests.IncidentWindowReaderStopsAtTheSnapshotCap`,
  `IncidentPageTests.IncidentPageRendersTheMemoryChartWithFixedTitleAndStackedData`.
- [ ] **AC8 — available memory, swap and connections of the window (spec AC-S5).** The second chart has the series
  `Available memory` and `Swap used` (MiB) at the union of their times, `null` where a series has no point and for a value
  that is not finite, at most 1,000 points per series; the third chart has `Sockets` (sum of the state counts) per connection
  snapshot, at most 300; no data gives no chart and the text `No snapshot or metric was stored in this window.`
  Tests: `IncidentWindowReaderTests.IncidentWindowReaderChartsAvailableMemorySwapAndConnections`,
  `IncidentWindowReaderTests.IncidentWindowReaderReturnsNoChartsWithoutData`.
- [ ] **AC9 — the incident page content (spec AC-S5, AC-S6).** Header, kills, hypothesis (unknown where the incident says
  so), timeline in stored order with `timeline_omitted`, connection peak, metric extremes and templates; the row of an
  `oom_kill`, `boot`, `backup_first_seen` or `backup_last_seen` entry gets the class `mark-oom`, `mark-boot` or
  `mark-backup` from a fixed map (any other type, also one with quotes or markup, gets no class and is shown as escaped
  text); every external text goes through `ExternalText` (timeline texts capped at 512 characters, other fields at 2,048);
  an unknown number shows `This incident does not exist. It may have been merged into another one by vandoxd incidents.`
  Tests: `IncidentPageTests.IncidentPageShowsHeaderKillsHypothesisAndTimeline`,
  `IncidentPageTests.IncidentPageMarksOomKillsBootsAndBackupActivity`, `IncidentPageTests.IncidentPageShowsUnknownIncident`,
  `IncidentPageTests.IncidentPageWritesExternalTextOnlyAsEscapedText`,
  `IncidentDetailViewTests.IncidentDetailViewCapsTimelineTexts`.
- [ ] **AC10 — notes on the page and the note endpoint (spec AC-S6).** The page shows one form for the incident's own note
  and one for each note moved to it (labelled `Written for incident #<n>`), each a plain `POST` to `incidents/<id>/notes` with
  `<AntiforgeryToken />`, a hidden `written_for` and a `textarea name="note" maxlength="1800"`; `POST /incidents/{id}/notes`
  needs a session (anonymous: `401`) and a valid token (`400`), reads exactly one `note` and one `written_for` (1 to 18 ASCII
  digits), normalizes line ends (CR LF and CR to LF) and treats blank text as empty, refuses more than 1,800 characters
  (`400`), answers `303` with `Location: /incidents/<id>` on `Saved`/`Deleted`, `404` on `NotFound` and for an ID ≤ 0, `500`
  with no text on a store error (logged); a refused post stores nothing.
  Tests: `IncidentPageTests.IncidentPageShowsNoteFormsWithAntiforgeryToken`,
  `IncidentNoteEndpointTests.IncidentNoteEndpointSavesNoteAndRedirects`,
  `IncidentNoteEndpointTests.IncidentNoteEndpointRefusesWithoutSessionOrToken`,
  `IncidentNoteEndpointTests.IncidentNoteEndpointRefusesInvalidFields` (`DataRow`s in *Security considerations*),
  `IncidentNoteEndpointTests.IncidentNoteEndpointAcceptsTheLongestNoteInTheWorstEncoding` (1,800 × `€` URL-encoded),
  `IncidentNoteEndpointTests.IncidentNoteEndpointAnswersNotFoundForUnknownIncident`,
  `IncidentNoteTextTests.IncidentNoteTextNormalizesLineEndsAndBlankText`,
  `IncidentNoteTextTests.IncidentNoteTextAcceptsOnlyPlainDigitsForWrittenFor`.
- [ ] **AC11 — one reader gate for the logs and incidents views.** `WebReaderGate` runs at most its slot count (1–3) of calls
  at once, a wait ends with the token, a failed call frees its slot; `GatedIncidentBrowser` and `GatedRecordReader` take reads
  through the gate and `SaveIncidentNoteAsync` past it; the service registers one gate of `LogsView.MaxConcurrentQueries` slots
  shared by the logs explorer, the incident browser and the window reader.
  Tests: `WebReaderGateTests.WebReaderGateRunsAtMostTheGivenNumberOfCallsAtOnce`, `WebReaderGateTests.WebReaderGateWaitEndsWithTheToken`,
  `WebReaderGateTests.WebReaderGateReleasesTheSlotAfterAFailure`, `WebReaderGateTests.WebReaderGateRefusesAnInvalidSlotCount`,
  `GatedIncidentBrowserTests.GatedIncidentBrowserSharesTheGateForReadsButNotForNoteSaves`,
  `GatedRecordReaderTests.GatedRecordReaderRunsThroughTheGate`, `ServeTests.ServeSharesOneReaderGateBetweenLogsAndIncidents`.
- [ ] **AC12 — deadline.** Each page action runs under the 10-second deadline (`LogsView.Deadline`): the list shows
  `Reading the incidents took longer than 10 seconds and was stopped.`, the incident page the same for incident and notes and
  `Reading the data of the window took longer than 10 seconds and was stopped.` for the charts (the rest of the page stays).
  Test: `IncidentPageTests.IncidentPageStopsReadsAtTheDeadline` (with `IncidentsPageShowsEmptyDeadlineAndFailureTexts` for the list).
- [ ] **AC13 — navigation and stacked chart plumbing.** The main navigation has *Incidents* (`incidents`, prefix match, marked
  on `/incidents` and `/incidents/<n>`, not on `/logs`); `ChartData` with `stacked: true` refuses a `null` value with
  `The stacked series '<label>' has no value at point <i>.`; `ChartInterop` creates a stacked chart with the module function
  `createStacked` and the same four arguments, a line chart with `create` as before.
  Tests: `MainLayoutTests.MainLayoutMarksIncidentsOnTheIncidentPages`, `ChartDataTests.ChartDataStackedRefusesMissingValues`,
  `ChartInteropTests.ChartInteropCreatesStackedChartWithCreateStacked`.
- [ ] **AC14 — the image still contains the pages and assets.** New `.razor` files and changed `wwwroot/` files are in the
  published image. Verification: the *Image build check* (restore-order reproduction, `.squad/stack.md`), run by the **Dev**
  before the review; `ls publish/wwwroot/_framework publish/wwwroot/js` shows `blazor.web.js` and `charts.js`. The existing
  `WebUiContentTests` keep every new component and the changed script and style sheet within the source rules (no new test).

## Approach

**Storage (`src/Vandox.Storage`).**

- Schema step 10:
  ```sql
  CREATE TABLE incident_notes (
    written_for INTEGER PRIMARY KEY,
    incident_id INTEGER NOT NULL REFERENCES incidents(id),
    text TEXT NOT NULL CHECK (length(text) BETWEEN 1 AND 1800),
    updated_at INTEGER NOT NULL
  ) STRICT
  CREATE INDEX incident_notes_incident ON incident_notes(incident_id, written_for)
  ```
  `written_for` is the incident number the note was written for (numbers are never reused, `AUTOINCREMENT`); `incident_id` is
  the incident it belongs to now. No `ON DELETE` action: a delete of an incident that still has notes fails.
- `IncidentRepository.SaveAsync`: for each overlapped incident it deletes, first
  `UPDATE incident_notes SET incident_id = $kept WHERE incident_id = $other`, then the `DELETE` — same transaction.
- `IncidentNoteRepository` (new, internal static): validation, save (one immediate writer transaction under the write lock:
  incident must exist, else `NotFound`; empty text deletes the row `written_for = $w AND incident_id = $i` → `Deleted`, also
  when there was none and `$w = $i`; non-empty with `$w = $i` upserts (`INSERT … ON CONFLICT(written_for) DO UPDATE`),
  with `$w ≠ $i` updates only a row of that incident, else `NotFound`), read (`… WHERE incident_id = $id ORDER BY written_for
  LIMIT 65`, 64 returned, `More` when the 65th exists).
- Summaries: `SELECT i.id, i.started_at, i.ended_at, i.first_kill_at, i.kill_count, i.created_at, i.updated_at, i.data,
  EXISTS (SELECT 1 FROM incident_notes n WHERE n.incident_id = i.id) FROM incidents i [WHERE i.started_at < $before OR
  (i.started_at = $before AND i.id < $beforeId)] ORDER BY i.started_at DESC, i.id DESC LIMIT $limit`; each row's `data` is
  read as UTF-8 bytes and deserialized with `IncidentJson.Options` into an internal projection (`last_kill_at`, `kills`,
  `hypothesis`; the timeline and the rest are skipped by the reader), so a page of 20 holds one row's bytes at a time.
- One incident: the existing column list `WHERE id = $id`, mapped by `IncidentRepository.Read`.
- `SqliteStore` implements the new `IIncidentBrowser`; the existing `IIncidentStore` and its three test doubles stay as they are.

**Backend (`src/Vandox.Backend`).**

- `WebReaderGate` (the semaphore of `GatedLogExplorer`, extracted); `GatedLogExplorer` gets a constructor that takes a gate
  it does not own (the old constructor keeps creating and owning its own); `GatedIncidentBrowser`, `GatedRecordReader` wrap the
  store with the same gate. `ServeCommand.Build` registers one `WebReaderGate(LogsView.MaxConcurrentQueries)` singleton and
  builds `ILogExplorer`, `IIncidentBrowser` and `IncidentWindowReader` (over a `GatedRecordReader`) on it, and maps
  `IncidentNoteEndpoint`.
- `IncidentWindowReader.ReadAsync(start, end)`: source = `RecordsAsync(kind process_snapshot, [start,end), Limit 1)` first
  record's source, else `RecordsAsync(kind metric, …, Limit 1)`; none → empty data. Process snapshots of that source one per
  page through `RecordPager` (new overload with a record cap) up to 300, fed to `ProgramMemorySeries`
  (`MemoryAnalysis.Programs`); `mem_available_bytes` and `swap_used_bytes` as series queries (source + name, one page of
  1,000 each); connection snapshots of the source one per page up to 300, summing `States` counts. A page that does not lead
  past the previous one is the existing `store: reading records: page did not advance`.
- `ProgramMemorySeries`: per-program RSS arrays for at most 4,096 programs (others summed into one "other" array), times
  strictly ascending, then top five by peak (ties by ordinal name) and `Other programs` (everything else summed), in MiB
  (`bytes / 1,048,576.0`), stacked `ChartData`.
- `ChartData` gains `Stacked` (a stacked series must have a value at every point); `ChartInterop.RenderAsync` calls
  `createStacked` instead of `create` when it is set. `charts.js` exports `createStacked(element, title, labels, columns)`:
  draws the cumulative sums with each band filled between its series and the one below (uPlot `bands` and `fill`), shows the
  raw value of each program in the legend, same resize/theme handling and `update`/`destroy` object; the series colors grow
  to six (`--vandox-chart-5`, `--vandox-chart-6` in the four theme blocks of `app.css`).
- Pages: `IncidentsPage` (`/incidents`) and `IncidentPage` (`/incidents/{Id:long}`), both on `PageRunner` (new constructor
  overload that takes the failure logger: `IncidentsReadFailed`), querying only once interactive like `LogsPage`; the
  incident and its notes in one action, the window data in a second action so that a slow chart read leaves the rest of the
  page. View models `IncidentRowView`, `IncidentDetailView` build capped `TextRun` lists from the stored incident, which the
  page then drops. `IncidentNoteForm` component renders one note form.
- `IncidentNoteEndpoint`: `MapPost("/incidents/{id:long}/notes", SaveAsync).WithMetadata(RequiredAntiforgery.Instance)` (no
  `AllowAnonymous`, so the fallback policy demands a session); handler as in AC10; `IncidentNoteText` normalizes and checks
  the fields. Failures are logged with `IncidentNoteSaveFailed`.
- `MainLayout.razor`: `<NavLink href="incidents" Match="NavLinkMatch.Prefix">Incidents</NavLink>` after *Logs*.

**Fixed texts** (in `IncidentsView`, compared by the tests): page titles `Incidents` and `Incident #<n>`; `Loading…`; empty
list `No incidents are stored. Run vandoxd incidents after an import.`; `Older incidents`, `Newest incidents`,
`No older incidents.`; list deadline/failure `Reading the incidents took longer than 10 seconds and was stopped.` /
`The incidents could not be read. The service log has the details.`; unknown incident (AC9); window deadline (AC12) and
failure `The data of the window could not be read. The service log has the details.`; no data (AC8); cut
`The chart shows the first {0} snapshots, up to {1} UTC.`; chart titles `Memory per program (MiB, stacked)`,
`Available memory and swap (MiB)`, `Connections`; series `Other programs`, `Available memory`, `Swap used`, `Sockets`;
`Written for incident #{0}`, `Save note`, `Further notes of merged incidents exist and are not shown.`; growth `unknown` /
`none`; backup `running` / `not running` / `unknown`. Times as `LogsView.ShowSeconds` (UTC), memory `<n> MiB` rounded half
away from zero, growth `+<n> MiB`, duration `H:MM:SS` (hours unbounded, `0:00:00` for one kill).

## Options considered

1. **Where notes live.** (a) *Own table, one row per incident number written for, re-pointed on merge, foreign key without
   cascade* — chosen: survives updates (number kept) and merges (moved in the merge transaction), no text is lost or
   concatenated past a bound, a forgotten move fails loudly. (b) A field of the incident JSON — replaced by every rebuild.
   (c) One note per incident, merged notes concatenated — breaks the 1,800-character bound or cuts user text. (d) `ON DELETE
   CASCADE` — a merge would silently delete notes.
2. **How a note is saved.** (a) *Plain HTML form `POST` to a minimal endpoint with the antiforgery token, `303` back* —
   chosen: the pattern of `/logout`, covered by the login area's token rule ("every form post"), works without the live
   connection. (b) Submit over the interactive circuit (`@onsubmit`) — in-page errors, but a write without a token, resting
   only on the circuit's session binding; the login area's rules are written for form posts. (c) A static-SSR form with
   `[SupplyParameterFromForm]` — the pages render interactively, so the framework would not handle the post on them.
3. **What the list reads.** (a) *Summaries: columns plus a projection of `data` decoded row by row, 20 per page, keyset by
   start and number* — chosen: settles #213 without loading whole incidents. (b) `IncidentsAsync` with a small limit — still
   up to 8 MiB decoded per incident, all of a page at once. (c) Summary columns in the schema — needs a backfill or a rebuild
   for stored rows. (d) SQLite `json_extract` — keeps the bytes out of managed memory but parses with another JSON reader than
   the one that wrote and reads the document.
4. **Chart labels.** (a) *Program names escaped with `LogText.Escape` and capped at 64 characters, written by uPlot as text*
   — chosen: readable legend, no markup path (verified in the vendored source), visible escapes for direction controls.
   (b) Fixed labels (`Program 1` …) with a separate Razor legend table — keeps "fixed texts only" but needs color swatches
   without `style` attributes and splits the legend from the chart.
5. **Reader slots.** (a) *One gate of two slots shared by both views* — chosen: keeps two of four readers for `/healthz` and
   other readers (0106 option 14). (b) A separate gate for the incidents view — three readers busy at once, one left.
6. **The span of a time link.** (a) *30 s before to 5 min after* — chosen: the oldest-first logs page starts just before the
   moment and shows what followed. (b) ±1 minute — the moment lands on the second page of a busy log.
7. **Duration in the list.** (a) *First to last kill* — chosen; (b) window length — mostly the configured padding (1 h 30 min
   by default), the same for every single-kill incident.
8. **Chart data cap.** (a) *First 300 snapshots, cut named* — chosen; (b) evenly thinned snapshots — needs a count first, and
   thins the moments just before a kill.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `SchemaMigrator` | step 10 |
| Vandox.Storage | `StorageLimits` | `SchemaVersion = 10`, `MaxNoteChars`, `MaxIncidentListLimit`, `MaxNotesRead` |
| Vandox.Storage | `IncidentRepository` | notes moved before a merge delete; list SQL, summary projection, read by ID |
| Vandox.Storage | `IIncidentBrowser`, `IncidentListQuery`, `IncidentPosition`, `IncidentSummary`, `IncidentNote`, `IncidentNotePage`, `IncidentNoteEdit`, `IncidentNoteOutcome`, `InvalidNoteException`, `IncidentNoteRepository` | new |
| Vandox.Storage | `SqliteStore` | implements `IIncidentBrowser` |
| Vandox.Backend | `Logs/WebReaderGate`, `Logs/GatedLogExplorer`, `Logs/PageRunner` | new gate; new constructors |
| Vandox.Backend | `Incidents/GatedIncidentBrowser`, `GatedRecordReader`, `IncidentWindowReader`, `IncidentWindowData`, `ProgramMemorySeries`, `IncidentsView`, `IncidentRowView`, `IncidentDetailView` (+ its row types), `IncidentNoteText`, `IncidentNoteEndpoint` | new |
| Vandox.Backend | `Incidents/RecordPager` | overload with a record cap |
| Vandox.Backend | `Components/Pages/IncidentsPage.razor(.cs)`, `IncidentPage.razor(.cs)`, `Components/Incidents/IncidentNoteForm.razor(.cs)` | new |
| Vandox.Backend | `Components/Charts/ChartData`, `ChartInterop` | stacked |
| Vandox.Backend | `Components/Layout/MainLayout.razor` | navigation entry |
| Vandox.Backend | `Hosting/ServeCommand`, `Hosting/BackendLog` | wiring, two log messages |
| Vandox.Backend | `wwwroot/js/charts.js`, `wwwroot/app.css` | `createStacked`, two colors, incident styles and the three mark classes |

Files the skeleton rewrites that the Lead read and found final in shape: `IncidentRepository.cs`, `SqliteStore.cs`,
`StorageLimits.cs`, `SchemaMigrator.cs`, `GatedLogExplorer.cs`, `PageRunner.cs`, `RecordPager.cs`, `ChartData.cs`,
`ChartInterop.cs`, `ServeCommand.cs`, `MainLayout.razor`.

## Signatures (for the Dev's skeleton)

`Vandox.Storage` (all public unless marked):

```csharp
public interface IIncidentBrowser
{
    Task<IReadOnlyList<IncidentSummary>> IncidentSummariesAsync(IncidentListQuery query, CancellationToken cancellationToken);
    Task<StoredIncident?> IncidentAsync(long id, CancellationToken cancellationToken);
    Task<IncidentNotePage> IncidentNotesAsync(long incidentId, CancellationToken cancellationToken);
    Task<IncidentNoteOutcome> SaveIncidentNoteAsync(IncidentNoteEdit edit, CancellationToken cancellationToken);
}
public sealed class IncidentListQuery { public IncidentPosition? Before { get; set; } public int Limit { get; set; } }
public sealed class IncidentPosition { public DateTimeOffset StartedAt { get; set; } public long Id { get; set; } }
public sealed class IncidentSummary
{
    public long Id { get; set; } public DateTimeOffset Start { get; set; } public DateTimeOffset End { get; set; }
    public DateTimeOffset FirstKillAt { get; set; } public DateTimeOffset LastKillAt { get; set; } public long KillCount { get; set; }
    public string FirstVictim { get; set; } = string.Empty; public IncidentHypothesis Hypothesis { get; set; } = new();
    public bool HasNotes { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class IncidentNote { public long WrittenFor { get; set; } public long IncidentId { get; set; } public string Text { get; set; } = string.Empty; public DateTimeOffset UpdatedAt { get; set; } }
public sealed class IncidentNotePage { public IReadOnlyList<IncidentNote> Notes { get; set; } = []; public bool More { get; set; } }
public sealed class IncidentNoteEdit { public long IncidentId { get; set; } public long WrittenFor { get; set; } public string Text { get; set; } = string.Empty; public DateTimeOffset SavedAt { get; set; } }
public enum IncidentNoteOutcome { Saved, Deleted, NotFound }
public sealed class InvalidNoteException : StoreException { public InvalidNoteException(string message); public InvalidNoteException(string message, Exception innerException); }
// StorageLimits
public const int SchemaVersion = 10; public const int MaxNoteChars = 1800; public const int MaxIncidentListLimit = 100; public const int MaxNotesRead = 64;
// internal
internal static class IncidentNoteRepository
{
    internal static void Validate(IncidentNoteEdit edit);
    internal static Task<IncidentNoteOutcome> SaveAsync(SqliteConnection connection, SqliteTransaction transaction, IncidentNoteEdit edit, CancellationToken cancellationToken);
    internal static Task MoveAsync(SqliteConnection connection, SqliteTransaction transaction, long fromIncident, long toIncident, CancellationToken cancellationToken);
    internal static (string Sql, Dictionary<string, object> Parameters) BuildRead(long incidentId);
    internal static IncidentNote Read(SqliteDataReader reader);
}
// IncidentRepository additions
internal static void ValidateList(IncidentListQuery query);
internal static (string Sql, Dictionary<string, object> Parameters) BuildList(IncidentListQuery query);
internal static IncidentSummary ReadSummary(SqliteDataReader reader);
internal static (string Sql, Dictionary<string, object> Parameters) BuildOne(long id);
```

`Vandox.Backend`:

```csharp
public sealed class WebReaderGate : IDisposable
{
    public WebReaderGate(int maxConcurrent);                                   // 1..3, else ArgumentOutOfRangeException
    public Task<TResult> RunAsync<TResult>(Func<Task<TResult>> call, CancellationToken cancellationToken);
    public void Dispose();
}
public GatedLogExplorer(ILogExplorer inner, WebReaderGate gate);              // new; the existing (inner, int) stays
public sealed class GatedIncidentBrowser : IIncidentBrowser { public GatedIncidentBrowser(IIncidentBrowser inner, WebReaderGate gate); }
public sealed class GatedRecordReader : IRecordReader { public GatedRecordReader(IRecordReader inner, WebReaderGate gate); }
public sealed class IncidentWindowReader
{
    public IncidentWindowReader(IRecordReader records);
    public Task<IncidentWindowData> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);
}
public sealed class IncidentWindowData
{
    public string Source { get; init; } = string.Empty;
    public ChartData? Memory { get; init; } public ChartData? MemoryAndSwap { get; init; } public ChartData? Connections { get; init; }
    public DateTimeOffset? MemoryCutAt { get; init; } public DateTimeOffset? ConnectionsCutAt { get; init; }
}
public sealed class ProgramMemorySeries
{
    public ProgramMemorySeries();
    public int Count { get; }
    public bool Add(DateTimeOffset capturedAt, IReadOnlyDictionary<string, ProgramUsage> programs);   // false: skipped (not later)
    public ChartData? ToChart();                                                                       // null without a snapshot
}
public static class IncidentsView
{
    public const int PageIncidents = 20; public const int MaxChartSnapshots = 300; public const int MaxChartConnections = 300;
    public const int MaxMetricPoints = 1000; public const int MaxChartPrograms = 4096; public const int ChartPrograms = 5;
    public const int MaxLabelChars = 64; public const int MaxTimelineTextChars = 512;
    public static readonly TimeSpan LogsBefore;  // 30 s
    public static readonly TimeSpan LogsAfter;   // 5 min
    // the fixed texts of *Approach* as public const string
    public static string LogsHref(DateTimeOffset moment);
    public static string IncidentHref(long id);           // "incidents/<id>"
    public static string NoteAction(long id);             // "incidents/<id>/notes"
    public static string Duration(TimeSpan span);         // "H:MM:SS", negative as "0:00:00"
    public static string Mib(ulong bytes);                // "<n> MiB"
    public static string Growth(ulong bytes);             // "+<n> MiB"
}
public sealed class IncidentRowView { public static IncidentRowView From(IncidentSummary summary); /* Id, Href, FirstKillAt, Duration, KillCount, Victim (IReadOnlyList<TextRun>), MoreKills, Growth (IReadOnlyList<ProgramAmountView>), GrowthState, Backup, HasNotes */ }
public sealed class IncidentDetailView { public static IncidentDetailView From(StoredIncident stored); /* header, kills, hypothesis, timeline rows with Marker, peak, extremes, templates — all external text as IReadOnlyList<TextRun> */ }
public static class IncidentNoteText
{
    public static string Normalize(string text);                                 // CR LF and CR to LF; blank to ""
    public static bool TryParseWrittenFor(string value, out long writtenFor);    // 1..18 ASCII digits, > 0
}
internal static class IncidentNoteEndpoint
{
    internal const string Pattern = "/incidents/{id:long}/notes";
    internal static void Map(IEndpointRouteBuilder endpoints);
    internal static Task SaveAsync(HttpContext context, long id, IIncidentBrowser incidents, TimeProvider clock, ILoggerFactory loggerFactory);
}
internal static Task<int> PageAsync(IRecordReader records, RecordQuery range, int limit, int maxRecords, Func<StoredRecord, Task> handle, CancellationToken cancellationToken);   // RecordPager; returns the records handled; stops at maxRecords
internal PageRunner(TimeProvider clock, Action<Exception> logFailure, string deadlineText, string failureText, Action<string> fail);
public ChartData(IReadOnlyList<DateTimeOffset> times, IReadOnlyList<ChartSeries> series, bool stacked);  public bool Stacked { get; }
[LoggerMessage(Level = LogLevel.Error, Message = "incidents view: reading failed")] internal static partial void IncidentsReadFailed(this ILogger logger, Exception exception);
[LoggerMessage(Level = LogLevel.Error, Message = "incidents view: saving a note failed")] internal static partial void IncidentNoteSaveFailed(this ILogger logger, Exception exception);
```

Components: `IncidentsPage` (`@page "/incidents"`), `IncidentPage` (`@page "/incidents/{Id:long}"`, `[Parameter] public long Id`),
`IncidentNoteForm` (`[Parameter, EditorRequired] long IncidentId`, `long WrittenFor`, `string Text`, `DateTimeOffset? UpdatedAt`,
`bool Merged`). `charts.js`: `export function createStacked(element, title, labels, columns)`. The new `BackendLog` event IDs are
the next free ones.

**Rules** (exact texts): list — `store: invalid query: limit must be between 1 and 100`, `store: invalid query: before must be
within the storable range`, `store: invalid query: before id must be positive`; one incident and notes — `store: invalid query:
incident ID must be positive`; note — `store: invalid note: incident ID must be positive`, `written for must be positive`,
`text is longer than 1800 characters`, `text is not valid UTF-16`, `saved at must be within the storable range`; a null query,
edit or text is an `ArgumentNullException`; database errors are `store: saving note: …` and `store: reading incidents: …`.

## Test files

New: `tests/Vandox.Storage.Tests/SqliteStoreIncidentBrowserTests.cs`; `tests/Vandox.Backend.Tests/WebReaderGateTests.cs`,
`GatedIncidentBrowserTests.cs`, `GatedRecordReaderTests.cs`, `IncidentWindowReaderTests.cs`, `ProgramMemorySeriesTests.cs`,
`IncidentsViewTests.cs`, `IncidentRowViewTests.cs`, `IncidentDetailViewTests.cs`, `IncidentNoteTextTests.cs`,
`IncidentNoteEndpointTests.cs`, `IncidentsPageTests.cs`, `IncidentPageTests.cs`. New test doubles (Tester):
`FakeIncidentBrowser` (scripted results per method, copies of every call with its token, a `Before` hook, scripted failures),
`ScriptedRecordReader` (`IRecordReader` answering scripted pages), `IncidentPageHarness` (renderer, fake browser, window
reader, clock, logger, like `LogPageHarness`). Existing files that get tests: `SqliteStoreOpenTests.cs`,
`SqliteStoreIncidentTests.cs`, `IncidentReconstructorTests.cs`, `ChartDataTests.cs`, `ChartInteropTests.cs`,
`MainLayoutTests.cs`, `ServeTests.cs`.

Existing test code affected:

- `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`: the seven undo chains (lines 166, 194, 228, 273, 314, 346, 386) need
  `DROP TABLE incident_notes` before `UndoStepNine` (a constant `UndoStepTen`), and the version-8 test's literal `"9"` and
  `version 10 … uses 9` (lines 395, 405) and the version-7 test's `version 10` (lines 361–367) move to the new version —
  **Tester**, step 5 (assertions change, not a signature).
- `ServeTests` reflection test (lines 956–975): `GatedLogExplorer` gains a gate field; `fields.OfType<ILogExplorer>().Single()`
  still holds — **Tester** confirms in step 5, no edit expected.
- No changed signature: `GatedLogExplorer`, `PageRunner`, `RecordPager`, `ChartData` keep their old members; `IIncidentStore`
  and its doubles (`ScriptedIncidentStore`, `RecordingIncidentStore`, `RefusingIncidentStore`) are untouched.

Files of the change no unit test can cover: `src/Vandox.Backend/wwwroot/js/charts.js` (browser script, no test runner) —
already excluded from the coverage measure by record 0098 (`sonar.coverage.exclusions` `src/Vandox.Backend/wwwroot/js/**`;
not in `COVERAGE_PATHSPECS`); no new exclusion. `app.css` is not production code for the gate. Everything else is C# or Razor
and covered by the tests above.

## Areas

- [Web UI](../../docs/areas/web-ui.md) — new section *Incidents view* (pages, list, incident page, time links, charts with
  their caps and source rule, notes form, texts, deadline); *Layout and navigation* gets the third entry; *Charts* gets
  stacked charts and labels that hold escaped external text; *Calls and deadline* of the logs view states the gate shared with
  the incidents view; *Limits and errors* the incidents view behind the login and its memory per circuit.
- [Storage](../../docs/areas/storage.md) — schema 10 and `incident_notes`; *Writing* → *Incident notes* (rules, texts, bounds:
  at most 1,800 UTF-16 characters, at most 5,400 UTF-8 bytes, one row per incident number ever given); *Incidents*: notes move
  on merge; *Reading*: summaries, one incident, notes, and the open limit for #27 replaced by what the view reads; *Retention*:
  #46 must move or keep notes (an incident with notes cannot be deleted).
- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md) — *Re-running*: notes stay with an updated incident and
  follow a merge (link to Storage); the incidents are shown in the web UI (link).
- [Web login](../../docs/areas/web-login.md) — *Cross-site requests*: the note form is a form post under the same token rule;
  `/incidents/{id}/notes` is an authenticated endpoint (no new exception under *Access*).

## Documentation updates

| File | Change | Owner |
| ---- | ------ | ----- |
| `docs/areas/web-ui.md`, `storage.md`, `detection-and-reports.md`, `web-login.md` | as *Areas* | Dev |
| `README.md` | new section *Look at incidents* after *Reconstruct incidents* (list, page, time links, notes and that they survive re-runs) | Dev |
| `.squad/project.md` | security area 3 (incidents view behind the login, the shared gate, the note endpoint with token), area 10 (the incident browser's bounds: list limit, summary projection, 64 notes, chart caps), area 12 (incidents view external text, chart labels through `LogText.Escape`, mark classes from a fixed map); *Integration surface* unchanged | Dev |
| `.squad/project.md` *Test doubles* | `FakeIncidentBrowser`, `ScriptedRecordReader`, `IncidentPageHarness` | Tester |
| `docs/ARCHITECTURE.md` | status line, components, schema list, *Incidents*, *Interfaces* | Lead (done with this plan) |
| `docs/decisions/0107-…`, index, `0096` consequences | | Lead (done with this plan) |

## Architecture check

- Guarantees of `.squad/project.md` (no silent gaps, a hanging collector never blocks, backfill never alerts) are not touched.
- Flow change: the service writes to the store for the first time outside an import (a note, one short immediate transaction
  on the writer connection under the write lock). `vandoxd incidents` in another process takes the same file lock per
  incident; a note save that waits longer than the busy timeout (for example behind the clear transaction of `vandoxd
  templates`) fails with `500` and a log line, nothing half-written. Recorded in `docs/ARCHITECTURE.md` and 0107.
- Reader bound of 0106 (two web reads at once) is kept and now spans both views.
- No new dependency, no new static file (the existing `charts.js` and `app.css` change), no new configuration option or secret.

## Security considerations

- [ ] Limits and when they apply: the note limit (1,800 UTF-16 characters) applies **after** form decoding and line-end
  normalization; the form value limit of 16 KiB applies to the value as the form reader measures it — the worst case the
  browser can send under `maxlength="1800"` is 9 bytes per character URL-encoded (a three-byte character as three `%XX`),
  16,200 bytes, pinned by `IncidentNoteEndpointAcceptsTheLongestNoteInTheWorstEncoding`. The label cap (64), timeline text cap
  (512) and field cap (2,048) are UTF-16 characters of the displayed text after escaping, as in the logs view.
- [ ] Exceptions reaching a user: the pages show only the fixed texts above (store errors logged with `IncidentsReadFailed`);
  the endpoint answers bare status codes (`400`, `404`, `500`), never an exception message; an `InvalidNoteException` (only
  reachable by a request that skipped the endpoint's own checks) is `400`.
- [ ] **Guard on `written_for`** (the form reader's forms): the value is matched by an ordinal scan of 1 to 18 ASCII digits and
  must be above 0. Refused (`400`): empty, `0`, `-1`, `+1`, ` 1`, `1 `, `１` (fullwidth digit), `٣` (Arabic-Indic), `1e3`, `0x1`,
  19 digits. The form reader matches keys **ignoring case** and joins repeated keys, so `written_for` and `WRITTEN_FOR` (or
  `note` and `NOTE`) together are two values and refused; a missing field is refused. Multipart bodies reach the same reader and
  the same checks. Test: `IncidentNoteEndpointRefusesInvalidFields` (one `DataRow` per form above).
- [ ] **Route value `{id:long}`**: the route constraint accepts `01`, `+1` and `-1` (invariant `long` parsing); the endpoint and
  the page use only the parsed value, an ID ≤ 0 is `404` / *does not exist*, and the redirect is built from the number
  (`/incidents/<n>`), never from the request path. Test rows in `IncidentNoteEndpointAnswersNotFoundForUnknownIncident`.
- [ ] Note text: any characters are stored (the form decoder already replaced invalid UTF-8 with U+FFFD; the store refuses a
  lone surrogate as a backstop); the note is shown in the `textarea` (Razor-encoded, so `</textarea>` cannot end it) and the
  merged notes' headings use only numbers.
- [ ] External text (victims, hosts, programs, commands, timeline keys and values, templates, sources, metric labels) only
  through `ExternalText` with the logs view's escaping and caps; chart labels through `LogText.Escape(…, 64)` and uPlot's
  `textContent`; CSS classes only from the fixed map of AC9; links only from the fixed paths `incidents/`, `logs` plus numbers
  and `LogsAddress.ToQuery()`; no `MarkupString`, no `style` attribute (`WebUiContentTests`).
- [ ] Memory: the list holds 20 summaries; the incident page decodes one incident (up to 8 MiB of JSON, transient) and keeps
  only capped runs (1,000 timeline entries with texts of at most 512 characters, other fields 2,048), at most 64 notes of 1,800
  characters and charts of at most 300 × 6, 2 × 1,000 and 300 points; the window read holds one snapshot at a time plus at most
  4,096 × 300 program values (about 10 MB) while it builds. The Dev states the per-circuit worst case in the web UI area.

## Decision records

- [`docs/decisions/0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md`](../../docs/decisions/0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md) (Proposed)
- [`docs/decisions/0096-…`](../../docs/decisions/0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md) — unreleased, two consequences edited in place (notes follow a merge; the view does not page `IncidentsAsync`).

## Out of scope / follow-ups

- Clicking inside a chart and kill markers drawn into the charts (spec, *Out of scope*): a possible follow-up issue, not
  required by the issue's acceptance criteria.
- Notes in `vandoxd report`; retention of incidents and their notes (#46); live incidents (#53).
