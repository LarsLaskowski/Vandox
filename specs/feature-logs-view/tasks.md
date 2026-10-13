# Tasks: Logs view

Plan: [plan.md](plan.md) — Status: Draft

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Skeleton: the public and internal storage types and `ILogExplorer` on `SqliteStore`, the `StorageLimits` constants, the backend `Logs/` types, the two pages, `ExternalText`, `BackendLog.LogsReadFailed`, `ServeHooks.Built`, the `RecordQueries.ValidateWindow` overload (signatures of the plan, `#pragma warning disable RH2003, S2325`), *Build* passes | `src/Vandox.Storage/*`, `src/Vandox.Backend/Logs/*`, `src/Vandox.Backend/Components/**`, `src/Vandox.Backend/Hosting/ServeHooks.cs` | — | Dev | [x] |
| 2 | Test doubles `FakeLogExplorer`, `RecordingNavigationManager` (and an interactive `RendererInfo` for `ComponentTestRenderer` if needed) | `tests/Vandox.Backend.Tests/*` | — | Tester | [x] |
| 3 | Tests first for AC-1 to AC-18 and AC-21 (the named tests only; they compile and fail on the skeleton) | `tests/Vandox.Storage.Tests/SqliteStoreLogExplorerTests.cs`, `TemplateTermsTests.cs`; `tests/Vandox.Backend.Tests/LogsAddressTests.cs`, `SeveritiesTests.cs`, `LogTextTests.cs`, `LogLineViewTests.cs`, `GatedLogExplorerTests.cs`, `ExternalTextTests.cs`, `TemplateHourSeriesTests.cs`, `LogsPageTests.cs`, `LogTemplatesPageTests.cs`, `MainLayoutTests.cs`, `ServeTests.cs` | AC-1–AC-18, AC-21 | Tester | [x] |
| 4 | Storage: `RecordQueries.ValidateWindow` overload, `LogFilterRules`, `TemplateTerms`, `LogFinder` (selectivity probe, index mode, scan mode with the per-line `EXISTS` only, template check, `Next`), `LogExplorerQueries` (context, sources, template list, source hours), `SqliteStore` methods | `src/Vandox.Storage/*` | AC-1–AC-11 | Dev | [x] |
| 5 | Backend: `LogsAddress` (with `Validate`), `Severities`, `LogText`/`TextRun` (character caps, cut marker), `LogLineView`, `GatedLogExplorer`, `TemplateHourSeries`, `LogsView`, `ExternalText`, `LogsPage`, `LogTemplatesPage`, navigation entry, pages with one store call at a time, `ServeCommand` registrations (gated `ILogExplorer`, `TimeProvider`, `hooks.Built`), `BackendLog`, styles in `app.css`, `Home.razor` wording | `src/Vandox.Backend/**` | AC-12–AC-18, AC-21 | Dev | [x] |
| 6 | Benchmark `--search-lines` with the twelve cases; run on the development host with 5,000,000 lines; tune `SelectiveMatches` and `LogsView.ScanRows` within the plan's ranges | `tools/Vandox.StorageBenchmark/SearchBenchmark.cs`, `Program.cs`, `TemplateBenchmark.cs` | AC-19 | Dev | [x] |
| 7 | Coverage pass: tests for lines the *Coverage gate* reports uncovered; confirm or contest the plan's list of files no unit test can cover | `tests/**` | all | Tester | [x] |
| 8 | *Image build check* (restore-order reproduction) before the review | — | AC-20 | Dev | [x] |
| 9 | Area documents: *Logs view* in web UI, *Log exploration* in storage, *Log templates* in detection and reports (final tuned values included) | `docs/areas/web-ui.md`, `docs/areas/storage.md`, `docs/areas/detection-and-reports.md` | — | Dev | [x] |
| 10 | `docs/ARCHITECTURE.md` (intro, components, *Log search* bullet with link to 0103) | `docs/ARCHITECTURE.md` | — | Dev | [x] |
| 11 | `README.md`: *Explore the logs*; note in *Rebuild template statistics* | `README.md` | — | Dev | [x] |
| 12 | `docs/BENCHMARKS.md`: *Log search* section with the development-host results | `docs/BENCHMARKS.md` | AC-19 | Dev | [x] |
| 13 | Reference-host run of `--search-lines 5000000` by the Product Manager (requested by the orchestrator after task 20, before the review); the Dev enters the row in `docs/BENCHMARKS.md` | `docs/BENCHMARKS.md` | AC-19 | Product Manager (run), Dev (row) | [ ] |
| 14 | `.squad/project.md` *Security areas* 3, 10 and 12 | `.squad/project.md` | — | Dev | [x] |
| 15 | `.squad/project.md` *Test doubles*, row *Blazor components* | `.squad/project.md` | — | Tester | [x] |
| 16 | Decision record 0103, the sentence in 0066, the index row | `docs/decisions/0103-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md`, `docs/decisions/0066-log-search-takes-literal-terms-only.md`, `docs/decisions/README.md` | — | Lead | [x] |
| 17 | Skeleton of the Lead decision on the benchmark misses: `LogFinderQueries.BuildIndex(LogCriteria, IReadOnlyList<long>)` as a stub next to the old overload; *Build* passes | `src/Vandox.Storage/LogFinderQueries.cs` | — | Dev | [x] |
| 18 | Tests first: `FindLogsIndexModeReadsLinesByRecordId`, `TemplatesSumsTemplateHoursInKeyOrder` (fail on the skeleton; existing tests unchanged) | `tests/Vandox.Storage.Tests/SqliteStoreLogExplorerTests.cs` (and a shared plan helper if moved from `SqliteStoreTemplateTests.cs`) | AC-22, AC-23 | Tester | [x] |
| 19 | Probe keeps the IDs, index mode reads by record ID (empty page without IDs), old `BuildIndex` overload removed; template list summed in key order; benchmark adapted if needed | `src/Vandox.Storage/LogFinder.cs`, `LogFinderQueries.cs`, `LogExplorerQueries.cs`, `tools/Vandox.StorageBenchmark/SearchBenchmark.cs` | AC-22, AC-23, AC-19 | Dev | [x] |
| 20 | Benchmark again on the development host (5,000,000 lines), old and new template list compared row by row for cases 11 and 11b; results, *Tuning* and *Notes* updated; *Log exploration* updated (probe IDs, index read once, template list cost) | `docs/BENCHMARKS.md`, `docs/areas/storage.md` | AC-19 | Dev | [x] |
| 21 | Record 0103: options 16 and 17, option 3 wording, *Decision* | `docs/decisions/0103-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md` | — | Lead | [x] |
| 22 | Tests first: `LogsPageSubmitWithUnchangedValuesSearchesAgain`, `TemplatesPageSubmitWithUnchangedValuesSearchesAgain` (fail on the current code) | `tests/Vandox.Backend.Tests/LogsPageTests.cs`, `tests/Vandox.Backend.Tests/LogTemplatesPageTests.cs` | AC-24 | Tester | [ ] |
| 23 | `Submit` clears `_handledQuery` before navigating on both pages; web-ui area bullet on the repeated submit; *Tuning* sentence on the `ScanRows` copy | `src/Vandox.Backend/Components/Pages/LogsPage.razor.cs`, `src/Vandox.Backend/Components/Pages/LogTemplatesPage.razor.cs`, `docs/areas/web-ui.md`, `docs/BENCHMARKS.md` | AC-24 | Dev | [ ] |
| 24 | Lead decision *non-blocking code findings of review round 1* | `specs/feature-logs-view/plan.md`, `specs/feature-logs-view/tasks.md` | — | Lead | [x] |

Order: 17, 18, 19, 20 (with task 7's coverage pass after 19), then task 13. The orchestrator asks the Product Manager for the reference-host run only after task 20 (Lead decision *benchmark misses* in `plan.md`).
After review round 1: 22, 23, then the gates of step 6 and a Reviewer delta round (Lead decision *non-blocking code findings of review round 1*).
