# Tasks: Overview page

Plan: [plan.md](plan.md)

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: `IOverviewReader` (`FreshnessAsync`, `LatestValuesAsync`, `LatestMariaDbStatusAsync`, `LatestServiceStatesAsync`), `AgentFreshness`, `LatestValueQuery`, `LatestSeries`, `LatestValue`, `MariaDbLatest`, `LatestServiceQuery`, `ServiceStatePage`, `LatestServiceState`, the four `SqliteStore` methods | `src/Vandox.Storage/IOverviewReader.cs`, `AgentFreshness.cs`, `LatestValueQuery.cs`, `LatestSeries.cs`, `LatestValue.cs`, `MariaDbLatest.cs`, `LatestServiceQuery.cs`, `ServiceStatePage.cs`, `LatestServiceState.cs`, `SqliteStore.cs` | AC1, AC2, AC4, AC5 | — | Dev | [ ] |
| 2 | G1 | Tests first: the `SqliteStoreOverviewTests` of the plan | `tests/Vandox.Storage.Tests/SqliteStoreOverviewTests.cs` | AC1, AC2, AC4, AC5 | 1 | Tester | [ ] |
| 3 | G1 | Implement the reads and their checks (`OverviewQueries`) | `src/Vandox.Storage/OverviewQueries.cs`, `src/Vandox.Storage/SqliteStore.cs` | AC1, AC2, AC4, AC5 | 2 | Dev | [ ] |
| 4 | G2 | Skeleton: `GatedOverviewReader`, `CircuitConnection`, `OverviewRefresher`, `OverviewView.Build`, `OverviewText` (`Age`, `Size`, `Share`), `OverviewData`, `OverviewModel`, `OverviewUnitRow`, `OverviewFigureRow`, `OverviewIncidentRow` | `src/Vandox.Backend/Overview/*.cs` | AC1, AC2, AC3, AC4, AC5, AC6, AC7, AC8 | 3 | Dev | [ ] |
| 5 | G2 | Tests first: `OverviewViewTests`, `OverviewTextTests`, `OverviewRefresherTests`, `CircuitConnectionTests`, `GatedOverviewReaderTests`; double `FakeOverviewReader` | `tests/Vandox.Backend.Tests/Overview*Tests.cs`, `CircuitConnectionTests.cs`, `GatedOverviewReaderTests.cs`, `FakeOverviewReader.cs` | AC1, AC2, AC3, AC4, AC5, AC6, AC7, AC8 | 4 | Tester | [ ] |
| 6 | G2 | Implement the view, texts, refresher, connection state and gated reader | `src/Vandox.Backend/Overview/*.cs` | AC1, AC2, AC3, AC4, AC5, AC6, AC7, AC8 | 5 | Dev | [ ] |
| 7 | G3 | Skeleton: `OverviewPage` without `@page`, `BackendLog.OverviewReadFailed` | `src/Vandox.Backend/Components/Pages/OverviewPage.razor`, `OverviewPage.razor.cs`, `src/Vandox.Backend/Hosting/BackendLog.cs` | AC4, AC7, AC8 | 6 | Dev | [ ] |
| 8 | G3 | Tests first: `OverviewPageTests`; delete `HomePageTests.cs`; adapt `RoutesTests.cs:36`, `ServeTests.cs:490` | `tests/Vandox.Backend.Tests/OverviewPageTests.cs`, `HomePageTests.cs`, `RoutesTests.cs`, `ServeTests.cs` | AC4, AC7, AC8 | 7 | Tester | [ ] |
| 9 | G3 | Implement the page and layout, `@page "/"`, delete `Home.razor`, register reader and `CircuitConnection`, styles; run the *Image build check* | `src/Vandox.Backend/Components/Pages/*`, `src/Vandox.Backend/Hosting/ServeCommand.cs`, `src/Vandox.Backend/wwwroot/app.css` | AC4, AC7, AC8 | 8 | Dev | [ ] |
| 10 | G3 | Documentation updates of the plan | `docs/areas/web-ui-overview.md`, `docs/areas/README.md`, `docs/areas/web-ui.md`, `docs/areas/storage-reading.md`, `docs/areas/time-series-query.md`, `docs/ARCHITECTURE.md`, `CHANGELOG.md` | AC1, AC2, AC3, AC4, AC5, AC6, AC7, AC8 | 9 | Dev | [ ] |
| 11 | G3 | Text of the `FakeOverviewReader` entry for `.squad/project.md` *Test doubles* | `.squad/project.md` | AC8 | 8 | Orchestrator | [ ] |
