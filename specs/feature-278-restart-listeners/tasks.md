# Tasks: Report listeners that appeared while the agent was stopped

Plan: [plan.md](plan.md) — Status: Draft

Each task names the acceptance criteria of `spec.md` it delivers (`plan-check.py` reports a criterion no task
covers) and the tasks it waits for; steps 4–6 run the task groups in this order. Every documentation edit is
in exactly one row.

| # | Task | Files | Criteria | Depends on | Owner | Done |
| - | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | Agent and Go model: `start`, `InventoryState`, `Inventory`, `Tables` with validation; `compare` returns `start` without a previous inventory; `inventoryState`, `tableList`, `fitRecord` with the inventory; regenerate the golden batch with a `start` snapshot and both fields | `internal/model/connection.go`, `internal/connstats/inventory.go`, `connstats.go`, `snapshot.go`; tests `internal/model/connection_test.go`, `internal/connstats/inventory_test.go`, `connstats_test.go`, `internal/wire/golden_test.go`, `testdata/wire/all-kinds.jsonl` | AC1, AC2, AC3, AC4, AC9 | — | Dev (code), Tester (tests, fixture) | [x] |
| 2 | C# model: `Inventory`, `Tables`, `start` with validation; the contract test reads the regenerated golden batch | `src/Vandox.Core/Model/ConnectionSnapshot.cs`; tests `tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs`, `WireContractTests.cs` | AC9 | 1 | Dev (code), Tester (tests) | [x] |
| 3 | Comparison: `ListenerComparer`, `ListenerComparison`, `ListenerOutcome` and the section's data types (`ListenerFinding`, `ListenerFindingKind`, `ListenerTally`, `ListenerReport`, `ForensicsSummary.Listeners`) | `src/Vandox.Core/Reports/*`; tests `tests/Vandox.Core.Tests/ListenerComparerTests.cs` | AC2, AC3, AC4, AC5, AC6, AC10 | 2 | Dev (code), Tester (tests) | [x] |
| 4 | Storage: schema step 11, `ListenerMarkWriter` in `BatchWriter.Write`, `IListenerHistoryReader` on `SqliteStore` with `ListenerQueries`, `ListenerFindingQuery`, `ListenerTallyPage`, `ListenerFindingPage`; measure the write throughput before and after (plan fact 15) and the section's reads on a one-year database (fact 14) | `src/Vandox.Storage/*`; tests `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs`, `SqliteStoreOpenTests.cs` | AC1, AC5, AC7, AC8, AC9, AC12, AC13, AC14 | 3 | Dev (code, measurements), Tester (tests) | [x] |
| 5 | Report: `ForensicsReporter` reads the section, `ReportComposer` section and notes, `ReportText` texts in both languages, `GatedListenerHistoryReader`, the reader passed by `ReportService`, `ReportCommand`, `ServeCommand`; adapt the constructor call sites, `ReportSamples`, the golden reports, `ScriptedListenerHistoryReader` | `src/Vandox.Backend/Reports/*`, `Cli/ReportCommand.cs`, `Hosting/ServeCommand.cs`; tests `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`, `ReportContentTests.cs`, `ForensicsReporterTests.cs`, `GatedListenerHistoryReaderTests.cs`, `ReportServiceTests.cs`, `ReportPageHarness.cs`, `ReportDownloadEndpointTests.cs`, `ReportSamples.cs`, `testdata/report/forensics-{en,de}.md` | AC4, AC6, AC10, AC11, AC12 | 4 | Dev (code), Tester (tests, fixtures) | [ ] |
| 6 | Area documents: agent, wire format, storage, detection and reports (with the measured time), web UI | `docs/areas/agent.md`, `wire-format.md`, `storage.md`, `detection-and-reports.md`, `web-ui.md` | AC1, AC11, AC14 | 5 | Dev | [ ] |
| 7 | `docs/ARCHITECTURE.md` (*Data flow*, forensics report paragraph, link 0116) and `docs/BENCHMARKS.md` (the section's measurement) | `docs/ARCHITECTURE.md`, `docs/BENCHMARKS.md` | AC14 | 4 | Dev | [ ] |
| 8 | `.squad/project.md` security areas 3, 10 and 12 | `.squad/project.md` | AC10 | 5 | Dev | [ ] |
| 9 | `.squad/project.md` *Test doubles*, database row: `ScriptedListenerHistoryReader` | `.squad/project.md` | AC11 | 5 | Tester | [ ] |
| 10 | Decision record 0116, its index row, the residual of 0115 | `docs/decisions/0116-…md`, `docs/decisions/README.md`, `docs/decisions/0115-…md` | AC1 | — | Lead | [x] |
