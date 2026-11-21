# Tasks: Rollups of process snapshots per program

Plan: [plan.md](plan.md)

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: schema step 16 and `SchemaVersion`; `ProgramRollupStepRows`, `ProgramRollupStepBytes`, `MaxNamedPrograms`, `ProgramCutPerKey`, `ProgramRollupWatermarkKey`; `ProgramRollupFolder.Fold`; `ProgramBucketTotals.Add`, `ProgramSourceTotals.Include`, `SourceBucketKey`, `RetentionProgramRow`; `RollupStepResult.Snapshots`, `SkippedSnapshots`, `ProgramBuckets`; `RemovalStepResult.ProgramFiveMinuteRows`, `ProgramHourlyRows`; `RollupFolder.Floor` and `HorizonOf` made internal | `src/Vandox.Storage/` | AC1, AC2, AC3, AC6 | — | Dev | [ ] |
| 2 | G1 | Tests first: fold, cut, cap, clamp, names, removal, migration; existing removal tests that remove process snapshots without a fold get one | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreRetentionTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`, `tests/Vandox.Backend.Tests/KillHoldScannerTests.cs` | AC1, AC2, AC3, AC6 | 1 | Tester | [ ] |
| 3 | G1 | Fold in `FoldRollupsAsync`, removal stages and watermark rule | `src/Vandox.Storage/` | AC1, AC2, AC3, AC6 | 2 | Dev | [ ] |
| 4 | G2 | Skeleton: `IProgramRollupReader` (`ProgramRollupsAsync`, `ProgramSourcesAsync`) on `SqliteStore`; `ProgramRollupQuery` (`Source`, `From`, `To`, `Step`, `Measure`, `Count`); `ProgramMeasure`, `ProgramMemoryKey`; `ProgramRollupResult` (`Times`, `Programs`, `Others`, `Incomplete`, `MemoryKey`, `EarliestUnfolded`, `StartsBeforeBuckets`); `ProgramRollupLine` (`Program`, `Points`); `ProgramRollupPoint`; `ProgramSourceQuery`; `ProgramSourceList` (`Sources`, `More`); `ProgramSource` (`LatestBucket`); `MaxProgramRollupCount`, `MaxProgramRollupSteps`, `MaxProgramSources`; `ProgramRollupQueries.Validate`, `ReadAsync`, `ValidateSources`, `ReadSourcesAsync` | `src/Vandox.Storage/` | AC4, AC5 | 3 | Dev | [ ] |
| 5 | G2 | Tests first: per-program read and source list | `tests/Vandox.Storage.Tests/SqliteStoreProgramRollupTests.cs` | AC2, AC4, AC5 | 4 | Tester | [ ] |
| 6 | G2 | Read and source list | `src/Vandox.Storage/` | AC2, AC4, AC5 | 5 | Dev | [ ] |
| 7 | G3 | Skeleton: `RetentionProgramsRolledUp` (event 76) and `RetentionProgramRollupsRemoved` (event 77) in `BackendLog` | `src/Vandox.Backend/Hosting/BackendLog.cs` | AC3, AC6 | 3 | Dev | [ ] |
| 8 | G3 | Tests first: the job's program log lines | `tests/Vandox.Backend.Tests/RetentionServiceTests.cs` | AC3, AC6 | 7 | Tester | [ ] |
| 9 | G3 | Log lines in `RetentionService` and `RetentionRemovalTotals` | `src/Vandox.Backend/Retention/` | AC3, AC6 | 8 | Dev | [ ] |
| 10 | G4 | Benchmark scenario `--programs` (no new API), run on the development host | `tools/Vandox.StorageBenchmark/` | AC7 | 6, 9 | Dev | [ ] |
| 11 | PM | Run `--programs` on the DS918+ SSD volume, report the figures | — | AC7 | 10 | Product Manager | [ ] |
| 12 | G4 | Documentation: `docs/areas/program-rollups.md`, `retention.md`, `storage.md`, `storage-reading.md`, `backend-host.md`, `docs/areas/README.md`, `docs/ARCHITECTURE.md`, `docs/BENCHMARKS.md`, `CHANGELOG.md` | `docs/`, `CHANGELOG.md` | AC7 | 11 | Dev | [ ] |
