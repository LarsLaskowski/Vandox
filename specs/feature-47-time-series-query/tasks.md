# Tasks: Time-series query API

Plan: [plan.md](plan.md)

Each task names its criteria, the tasks it waits for and its group (steps 4–6 run per group, one agent per member
and mode; rows consecutive). A group has a "Skeleton" row for its API in *Signatures*, or says "no new API". Each
documentation edit is in one row, owned by the Dev (test documentation: the Tester; an edit under `.squad/`: the
Orchestrator, from the text its member reports), in the last group. Tasks of the Product Manager and the Lead alone
(a probe, a survey run, a fact correction) go to group `PM`, which steps 4–6 skip.

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: `ITimeSeriesReader` (RawValuesAsync, RollupStepsAsync, GapsInRangeAsync, EarliestUnfoldedAsync, ServiceChangesAsync, PreviousServiceStateAsync, BootTimesAsync), `TimeRangeQuery` (From, To, Limit), `RawValueQuery` (Source, Name, MaxTimes, MaxLines), `RollupStepQuery` (Tier, Step), `LinePage` (Lines, LinesCut, TimesExceeded), `MetricLine` (Labels, Points), `LinePoint` (At, LastAt, Minimum, Average, Maximum, Last, Count), `ServiceStateRow` (Unit, ActiveState, ActiveEnterAt, CapturedAt, Restarts, Previous), `BootTime` (BootId); `SqliteStore` stubs | `src/Vandox.Storage/` | AC1, AC2, AC4, AC5, AC6 | — | Dev | [ ] |
| 2 | G1 | Tests of the store reads (facts 5a, 11) | `tests/Vandox.Storage.Tests/SqliteStoreTimeSeriesTests.cs`, `TimeSeriesSamples.cs` | AC1, AC2, AC4, AC5, AC6 | 1 | Tester | [ ] |
| 3 | G1 | `TimeSeriesQueries` and the seven `SqliteStore` reads | `src/Vandox.Storage/TimeSeriesQueries.cs`, `SqliteStore.cs` | AC1, AC2, AC4, AC5, AC6 | 2 | Dev | [ ] |
| 4 | G2 | Skeleton: `TimeSeriesService` (MaxSeries, MaxLines, MaxTarget, MaxMarkers, MaxGaps, QueryAsync), `TimeSeriesQuery` (Series, From, To, Target), `SeriesKey` (Source, Name), `TimeSeriesResult` (Resolution, Step, Times, Lines, CutSeries, StartsBeforeRollups, CompleteUntil, GapsCompleteUntil, MarkersCompleteUntil, Gaps, Markers), `TimeSeriesResolution` (Raw, Step), `SeriesLine` (Labels, Points), `SeriesPoint` (State, Minimum, Average, Maximum, Last, LastAt, Count), `PointState` (Value, Gap, BetweenSamples), `SeriesGap` (AgentId, Kind, Cause, State, Collector, Concerns), `SeriesMarker` (Kind, At, EndsAt, LastSeenAt, End, Text, Pid, BootId), `MarkerKind` (OomKill, Boot, ServiceRestart, BackupRun), `BackupRunEnd` (Ended, Running, Unknown) | `src/Vandox.Storage/` | AC1–AC9 | 3 | Dev | [ ] |
| 5 | G2 | Engine tests and `CountingTimeSeriesReader` | `tests/Vandox.Storage.Tests/TimeSeriesServiceTests.cs`, `SeriesGapTests.cs`, `CountingTimeSeriesReader.cs` | AC1–AC9 | 4 | Tester | [ ] |
| 6 | G2 | Engine: validation, resolution, points, gaps, markers, completeness, caps | `src/Vandox.Storage/TimeSeriesService.cs` and its types | AC1–AC9 | 5 | Dev | [ ] |
| 7 | G3 | Skeleton: `GatedTimeSeriesReader`, `ChartSeries` bridged constructor and `Bridged`, `ChartData.ToBridges` | `src/Vandox.Backend/TimeSeries/`, `Components/Charts/` | AC9, AC11 | 6 | Dev | [ ] |
| 8 | G3 | Gate, wiring and chart tests; adapt `ChartInteropTests` | `tests/Vandox.Backend.Tests/GatedTimeSeriesReaderTests.cs`, `ServeTests.cs`, `ChartDataTests.cs`, `ChartInteropTests.cs` | AC9, AC11 | 7 | Tester | [ ] |
| 9 | G3 | Gate, `ServeCommand` registration, chart validation and interop, `charts.js`; *Image build check* | `src/Vandox.Backend/TimeSeries/GatedTimeSeriesReader.cs`, `Hosting/ServeCommand.cs`, `Components/Charts/*.cs`, `wwwroot/js/charts.js` | AC9, AC11 | 8 | Dev | [ ] |
| 10 | G4 | `--query` scenario (no new API): workload of spec N1, five runs, median; development-host run | `tools/Vandox.StorageBenchmark/QueryBenchmark.cs`, `Program.cs` | AC10 | 6 | Dev | [ ] |
| 11 | G4 | Documentation updates of the plan (area document and index, storage-reading, web-ui, retention, ARCHITECTURE, BENCHMARKS, CHANGELOG) | `docs/`, `CHANGELOG.md` | AC1–AC11 | 9, 10 | Dev | [ ] |
| 12 | G4 | *Test doubles* row for `CountingTimeSeriesReader`: text reported by the Tester | `.squad/project.md` | AC8 | 5 | Orchestrator | [ ] |
| 13 | PM | Run `--query` on the DS918+ SSD volume (fallback build: orchestrator, early draft PR) and report the five times | — | AC10 | 10 | Product Manager | [ ] |
