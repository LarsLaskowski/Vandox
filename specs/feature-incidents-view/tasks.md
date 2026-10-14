# Tasks: Incidents view

Plan: [plan.md](plan.md) — Status: Revised (1)

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Skeleton: storage types and `IIncidentBrowser` on `SqliteStore`, schema constants; backend gate, gated wrappers, window reader, series, view types, note text and endpoint, `RecordPager`/`PageRunner`/`GatedLogExplorer`/`ChartData` overloads, two log messages, empty pages and note form; *Build* passes | `src/Vandox.Storage/*`, `src/Vandox.Backend/*` as in *Signatures* | — | Dev | [x] |
| 2 | Tests first: the tests named in AC1–AC13, the doubles `FakeIncidentBrowser`, `ScriptedRecordReader`, `IncidentPageHarness`; adapt the migration chains of `SqliteStoreOpenTests` | `tests/Vandox.Storage.Tests/*`, `tests/Vandox.Backend.Tests/*` | AC1–AC13 | Tester | [x] |
| 3 | Schema step 10, note repository, note move in the merge, summaries, one incident, notes read | `SchemaMigrator`, `StorageLimits`, `IncidentRepository`, `IncidentNoteRepository`, `SqliteStore` | AC1–AC4 | Dev | [x] |
| 4 | Reader gate shared by logs and incidents, gated browser and record reader, wiring in `ServeCommand` | `WebReaderGate`, `GatedLogExplorer`, `GatedIncidentBrowser`, `GatedRecordReader`, `ServeCommand` | AC11 | Dev | [x] |
| 5 | Window reader (source per chart, `RecordPager` overload carrying `Source` and `Name`), program series, stacked `ChartData`/`ChartInterop`, `createStacked` in `charts.js`, two chart colors | `IncidentWindowReader`, `IncidentWindowData`, `ProgramMemorySeries`, `RecordPager`, `ChartData`, `ChartInterop`, `wwwroot/js/charts.js`, `wwwroot/app.css` | AC7, AC8, AC13 | Dev | [ ] |
| 6 | View helpers and view models, list page, incident page, note form, navigation entry, incident styles and mark classes | `IncidentsView`, `IncidentRowView`, `IncidentDetailView`, `IncidentsPage`, `IncidentPage`, `IncidentNoteForm`, `PageRunner`, `MainLayout.razor`, `BackendLog`, `app.css` | AC5, AC6, AC9, AC10, AC12, AC13 | Dev | [ ] |
| 7 | Note endpoint (antiforgery feature first, form only, U+0000 refused, log carries no note text) and note text rules | `IncidentNoteEndpoint`, `IncidentNoteText`, `ServeCommand` | AC10 | Dev | [ ] |
| 8 | *Image build check* (restore-order reproduction) before the review; result in `log.md` | — | AC14 | Dev | [ ] |
| 9 | Coverage pass: uncovered lines, the bounds the plan names (list limit, 64 notes, 300 snapshots, 1,000 metric points, 4,096 programs, 512/2,048/64 caps) | `tests/*` | — | Tester | [ ] |
| 10 | Area documents: *Incidents view* and the chart, navigation and gate changes in Web UI; schema 10, notes, reading and retention in Storage; *Re-running* in Detection; the note form under *Cross-site requests* in Web login | `docs/areas/web-ui.md`, `docs/areas/storage.md`, `docs/areas/detection-and-reports.md`, `docs/areas/web-login.md` | — | Dev | [ ] |
| 11 | README section *Look at incidents* | `README.md` | — | Dev | [ ] |
| 12 | Security areas 3, 10 and 12 | `.squad/project.md` (*Security areas*) | — | Dev | [ ] |
| 13 | Test doubles rows | `.squad/project.md` (*Test doubles*) | — | Tester | [x] |
| 14 | Decision record 0107 (Proposed) and its index row, 0096 consequences, `docs/ARCHITECTURE.md` | `docs/decisions/0107-…`, `docs/decisions/README.md`, `docs/decisions/0096-…`, `docs/ARCHITECTURE.md` | — | Lead | [x] |
