# Tasks: Report a known listener endpoint whose socket UID changes

Plan: [plan.md](plan.md) — Status: Draft

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: `ChangedListener`, the two snapshot fields, `checkChangedListeners` (Go); `Listener` unsealed with virtual `Validate`, `ChangedListener`, the two properties (C#) | `internal/model/connection.go`, `src/Vandox.Core/Model/Listener.cs`, `src/Vandox.Core/Model/ChangedListener.cs`, `src/Vandox.Core/Model/ConnectionSnapshot.cs` | AC6 | — | Dev | [x] |
| 2 | G1 | Tests first: validation tables, decoder cases, golden batch entry | `internal/model/connection_test.go`, `tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs`, `testdata/wire/decoder-cases.json`, `testdata/wire/all-kinds.jsonl` and its `.gz` forms | AC6 | 1 | Tester | [x] |
| 3 | G1 | Implement the wire rules in both languages | files of 1 | AC6 | 2 | Dev | [x] |
| 4 | G2 | Skeleton: `socketUIDs`, `uidChange`, `inventory.uids`, three-value `compare`, `MaxChangedListeners`, `lists` fields, `buildSnapshot` parameter | `internal/connstats/inventory.go`, `internal/connstats/connstats.go`, `internal/connstats/snapshot.go` | AC1, AC2, AC3, AC4, AC5 | 3 | Dev | [x] |
| 5 | G2 | Tests first: `compare` cases, two-run collector cases, cap and fill order; adapt the two `compare` call sites | `internal/connstats/inventory_test.go`, `internal/connstats/connstats_test.go`, `internal/connstats/snapshot_test.go` | AC1, AC2, AC3, AC4, AC5 | 4 | Tester | [x] |
| 6 | G2 | Implement the agent rule, the full snapshot, the list and its place in `fill` | files of 4 | AC1, AC2, AC3, AC4, AC5 | 5 | Dev | [x] |
| 7 | G3 | Skeleton: `ListenerComparer.MaxChangedListeners`, `ListenerComparison`/`ListenerFinding` changes, `ListenerFindingKind.AgentUidChanges`, `ListenerReport` third page, `SerializeChangedListeners`, schema step 13 | `src/Vandox.Core/Reports/*.cs` of the plan, `src/Vandox.Storage/SchemaMigrator.cs`, `src/Vandox.Storage/ListenerQueries.cs`, `src/Vandox.Storage/ListenerMarkWriter.cs` | AC7, AC8 | 3 | Dev | [x] |
| 8 | G3 | Tests first: comparer changes, mark writer, history reads, migration; adapt `ScriptedListenerHistoryReader` and the schema-version assertions | `tests/Vandox.Core.Tests/ListenerComparerTests.cs`, `tests/Vandox.Storage.Tests/ListenerMarkWriterTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`, `tests/Vandox.Backend.Tests/ScriptedListenerHistoryReader.cs` | AC7, AC8 | 7 | Tester | [x] |
| 9 | G3 | Implement the comparison, the storage step, writes and reads | files of 7 | AC7, AC8 | 8 | Dev | [x] |
| 10 | G4 | Skeleton: `ForensicsReporter` third read, `ReportText` texts, `ReportComposer` entry points | `src/Vandox.Backend/Reports/ForensicsReporter.cs`, `src/Vandox.Backend/Reports/ReportText.cs`, `src/Vandox.Backend/Reports/ReportComposer.cs` | AC7 | 9 | Dev | [x] |
| 11 | G4 | Tests first: report layout in both languages, cap of the third kind, golden reports | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`, `tests/Vandox.Backend.Tests/ForensicsReporterTests.cs`, `testdata/report/forensics-en.md`, `testdata/report/forensics-de.md` | AC7 | 10 | Tester | [ ] |
| 12 | G4 | Implement the layout of the plan's *Approach* | files of 10 | AC7 | 11 | Dev | [ ] |
| 13 | G5 | Documentation updates of the plan (agent, wire format, detection and reports, storage, web UI, `.squad/project.md`, `docs/ARCHITECTURE.md`), the page figure of fact 14 and the run figure of fact 15 recomputed against the built code | `docs/areas/agent.md`, `docs/areas/wire-format.md`, `docs/areas/detection-and-reports.md`, `docs/areas/storage.md`, `docs/areas/web-ui.md`, `.squad/project.md`, `docs/ARCHITECTURE.md` | AC1, AC6, AC7, AC8 | 6, 12 | Dev | [ ] |

## Signatures

| Group | Signatures |
| ----- | ---------- |
| G1 | `model.ChangedListener`, `ConnectionSnapshot.ChangedListeners`/`ChangedListenersOmitted`, `checkChangedListeners`; C# `Listener.Validate` (virtual), `ChangedListener`, `ConnectionSnapshot.ChangedListeners`/`ChangedListenersOmitted` |
| G2 | `socketUIDs`, `uidChange`, `inventory.uids`, `compare` (three results), `MaxChangedListeners`, `buildSnapshot` (`changed` parameter), `lists.changedListeners`/`changedOmitted` |
| G3 | `ListenerComparer.MaxChangedListeners`, `ListenerComparison.ChangedListeners`/`ChangedOmitted`, `ListenerFinding.ChangedListeners`/`ChangedOmitted`, `ListenerFindingKind.AgentUidChanges`, `ListenerReport.AgentChangeFindings`/`AgentChangeFindingsTotal`, `ListenerQueries.SerializeChangedListeners` |
| G4 | `ReportText.ListenerChanged`, `ListenerChangedEntry`, `ListenerBeforeUid`, `CapAgentChangeFindingsNote` |
| G5 | no new API |
