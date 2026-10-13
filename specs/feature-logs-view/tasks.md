# Tasks: Logs view

Plan: [plan.md](plan.md) — Status: Draft

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Skeleton: the public and internal storage types and `ILogExplorer` on `SqliteStore`, the `StorageLimits` constants, the backend `Logs/` types, the two pages, `ExternalText`, `BackendLog.LogsReadFailed`, `ServeHooks.Built`, the `RecordQueries.ValidateWindow` overload (signatures of the plan, `#pragma warning disable RH2003, S2325`), *Build* passes | `src/Vandox.Storage/*`, `src/Vandox.Backend/Logs/*`, `src/Vandox.Backend/Components/**`, `src/Vandox.Backend/Hosting/ServeHooks.cs` | — | Dev | [x] |
| 2 | Test doubles `FakeLogExplorer`, `RecordingNavigationManager` (and an interactive `RendererInfo` for `ComponentTestRenderer` if needed) | `tests/Vandox.Backend.Tests/*` | — | Tester | [x] |
| 3 | Tests first for AC-1 to AC-18 and AC-21 (the named tests only; they compile and fail on the skeleton) | `tests/Vandox.Storage.Tests/SqliteStoreLogExplorerTests.cs`, `TemplateTermsTests.cs`; `tests/Vandox.Backend.Tests/LogsAddressTests.cs`, `SeveritiesTests.cs`, `LogTextTests.cs`, `LogLineViewTests.cs`, `GatedLogExplorerTests.cs`, `ExternalTextTests.cs`, `TemplateHourSeriesTests.cs`, `LogsPageTests.cs`, `LogTemplatesPageTests.cs`, `MainLayoutTests.cs`, `ServeTests.cs` | AC-1–AC-18, AC-21 | Tester | [x] |
| 4 | Storage: `RecordQueries.ValidateWindow` overload, `LogFilterRules`, `TemplateTerms`, `LogFinder` (selectivity probe, index mode, scan mode with the per-line `EXISTS` only, template check, `Next`), `LogExplorerQueries` (context, sources, template list, source hours), `SqliteStore` methods | `src/Vandox.Storage/*` | AC-1–AC-11 | Dev | [ ] |
| 5 | Backend: `LogsAddress` (with `Validate`), `Severities`, `LogText`/`TextRun` (character caps, cut marker), `LogLineView`, `GatedLogExplorer`, `TemplateHourSeries`, `LogsView`, `ExternalText`, `LogsPage`, `LogTemplatesPage`, navigation entry, pages with one store call at a time, `ServeCommand` registrations (gated `ILogExplorer`, `TimeProvider`, `hooks.Built`), `BackendLog`, styles in `app.css`, `Home.razor` wording | `src/Vandox.Backend/**` | AC-12–AC-18, AC-21 | Dev | [ ] |
| 6 | Benchmark `--search-lines` with the twelve cases; run on the development host with 5,000,000 lines; tune `SelectiveMatches` and `LogsView.ScanRows` within the plan's ranges | `tools/Vandox.StorageBenchmark/SearchBenchmark.cs`, `Program.cs`, `TemplateBenchmark.cs` | AC-19 | Dev | [ ] |
| 7 | Coverage pass: tests for lines the *Coverage gate* reports uncovered; confirm or contest the plan's list of files no unit test can cover | `tests/**` | all | Tester | [ ] |
| 8 | *Image build check* (restore-order reproduction) before the review | — | AC-20 | Dev | [ ] |
| 9 | Area documents: *Logs view* in web UI, *Log exploration* in storage, *Log templates* in detection and reports (final tuned values included) | `docs/areas/web-ui.md`, `docs/areas/storage.md`, `docs/areas/detection-and-reports.md` | — | Dev | [ ] |
| 10 | `docs/ARCHITECTURE.md` (intro, components, *Log search* bullet with link to 0103) | `docs/ARCHITECTURE.md` | — | Dev | [ ] |
| 11 | `README.md`: *Explore the logs*; note in *Rebuild template statistics* | `README.md` | — | Dev | [ ] |
| 12 | `docs/BENCHMARKS.md`: *Log search* section with the development-host results | `docs/BENCHMARKS.md` | AC-19 | Dev | [ ] |
| 13 | Reference-host run of `--search-lines 5000000` by the Product Manager (requested by the orchestrator before step 8); the Dev enters the row in `docs/BENCHMARKS.md` | `docs/BENCHMARKS.md` | AC-19 | Product Manager (run), Dev (row) | [ ] |
| 14 | `.squad/project.md` *Security areas* 3, 10 and 12 | `.squad/project.md` | — | Dev | [ ] |
| 15 | `.squad/project.md` *Test doubles*, row *Blazor components* | `.squad/project.md` | — | Tester | [x] |
| 16 | Decision record 0103, the sentence in 0066, the index row | `docs/decisions/0103-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md`, `docs/decisions/0066-log-search-takes-literal-terms-only.md`, `docs/decisions/README.md` | — | Lead | [x] |
