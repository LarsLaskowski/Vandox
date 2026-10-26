# Tasks: Recognize backfilled data from the data itself

Plan: [plan.md](plan.md) — Status: Draft

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: `TimelineLimits`, `GapKind`, `GapState`, `LiveRules`, `ClockSample`, `ClockEstimator` (throwing bodies) | `src/Vandox.Storage/TimelineLimits.cs`, `GapKind.cs`, `GapState.cs`, `LiveRules.cs`, `ClockSample.cs`, `ClockEstimator.cs` | AC1, AC6 | — | Dev | [x] |
| 2 | G1 | Tests first: `LiveRulesTests`, `ClockEstimatorTests` | `tests/Vandox.Storage.Tests/LiveRulesTests.cs`, `ClockEstimatorTests.cs` | AC1, AC6 | 1 | Tester | [ ] |
| 3 | G1 | Implement the live rule and the estimator | the files of task 1 | AC1, AC6 | 2 | Dev | [ ] |
| 4 | G2 | Skeleton: `WriteResult.Timeline`, `TimelineChange`, `StoredRecord.Live`, `AgentGap`, `AgentClock`, `GapQuery` (`State`, `AfterId`, `Limit`, refused with `InvalidQueryException`), `IAgentTimelineReader` on `SqliteStore`, `AgentTimeline`, `AgentTimelineQueries`, `BatchWriter.Write(…, live, …)` (and its call in `SqliteStoreWriteTests.cs:226`), `StorageLimits.SchemaVersion` 12 | `src/Vandox.Storage/WriteResult.cs`, `AgentTimelineQueries.cs`, `tests/Vandox.Storage.Tests/SqliteStoreWriteTests.cs`, `TimelineChange.cs`, `StoredRecord.cs`, `AgentGap.cs`, `AgentClock.cs`, `GapQuery.cs`, `IAgentTimelineReader.cs`, `AgentTimeline.cs`, `BatchWriter.cs`, `SqliteStore.cs`, `StorageLimits.cs` | AC2, AC3, AC4, AC5, AC7, AC8, AC9, AC10 | 3 | Dev | [ ] |
| 5 | G2 | Tests first: `AgentReplay` (send lag derived as the sender does), `SqliteStoreTimelineTests`, `AgentTimelineQueriesTests`; the migration test in `SqliteStoreOpenTests` | `tests/Vandox.Storage.Tests/AgentReplay.cs`, `SqliteStoreTimelineTests.cs`, `AgentTimelineQueriesTests.cs`, `SqliteStoreOpenTests.cs` | AC2, AC3, AC4, AC5, AC6, AC7, AC8, AC9, AC10 | 4 | Tester | [ ] |
| 6 | G2 | Implement schema step 12, `AgentTimeline` with its overlay and `AgentTimelineQueries`, the write path, the restart set after commit, `live` in the record readers, `IAgentTimelineReader` | `src/Vandox.Storage/SchemaMigrator.cs`, `AgentTimeline.cs`, `AgentTimelineQueries.cs`, `BatchWriter.cs`, `SqliteStore.cs`, `RecordQueries.cs` | AC2, AC3, AC4, AC5, AC6, AC7, AC8, AC9, AC10 | 5 | Dev | [ ] |
| 7 | G3 | Skeleton: the three `BackendLog` methods | `src/Vandox.Backend/Hosting/BackendLog.cs` | AC10 | 6 | Dev | [ ] |
| 8 | G3 | Tests first: `IngestEndpointTimelineTests` | `tests/Vandox.Backend.Tests/IngestEndpointTimelineTests.cs` | AC1, AC10 | 7 | Tester | [ ] |
| 9 | G3 | Implement the log lines in `IngestEndpoint.Acknowledge` | `src/Vandox.Backend/Ingest/IngestEndpoint.cs`, `Hosting/BackendLog.cs` | AC1, AC10 | 8 | Dev | [ ] |
| 10 | G3 | Coverage pass; check the 15 whole-value `WriteResult` assertions and the ingest log expectations | `tests/Vandox.Storage.Tests/*`, `tests/Vandox.Backend.Tests/*` | AC8, AC10 | 9 | Tester | [ ] |
| 11 | G3 | Add the backfill scenario to `tools/Vandox.StorageBenchmark`; run it before and after on the development host | `tools/Vandox.StorageBenchmark/Program.cs`, `docs/BENCHMARKS.md` | AC8 | 9 | Dev | [ ] |
| 12 | G3 | Documentation updates of the plan | `docs/areas/detection-and-reports.md`, `docs/areas/storage.md`, `docs/areas/backend-host.md`, `docs/areas/wire-format.md`, `docs/areas/agent.md`, `docs/ARCHITECTURE.md`, `.squad/project.md` | AC10 | 10, 11 | Dev | [ ] |
