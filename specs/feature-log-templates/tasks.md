# Tasks: Log pattern normalization and frequency tracking

Plan: [plan.md](plan.md) — Status: Draft

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Record 0095 (Proposed) and its index row; *Log templates* in the detection area; storage area (schema 8, tables, write rule, reader); `ARCHITECTURE.md` | `docs/decisions/0095-…md`, `docs/decisions/README.md`, `docs/areas/detection-and-reports.md`, `docs/areas/storage.md`, `docs/ARCHITECTURE.md` | — | Lead | [x] |
| 2 | Skeleton (step 4): every new type and member of *Signatures* with XML docs, bodies `throw new NotImplementedException();`, the skeleton pragma; `SqliteStore` declares `ILogTemplateReader`; *Format*, *Build* | `src/Vandox.Core/Templates/LogTemplateNormalizer.cs`, `src/Vandox.Storage/{ILogTemplateReader,LogTemplate,TemplateHour,TemplateSpike,NewTemplateQuery,TemplateHourQuery,TemplateSpikeQuery,TemplateTally,TemplateQueries}.cs`, `StorageTime.cs`, `StorageLimits.cs` (new constants only; `SchemaVersion` stays 7 until task 6), `BatchWriter.cs`, `SqliteStore.cs`, `tools/Vandox.StorageBenchmark/TemplateBenchmark.cs` | — | Dev | [ ] |
| 3 | Tests first (step 5): normalizer | `tests/Vandox.Core.Tests/LogTemplateNormalizerTests.cs` | AC1, AC2, AC3 (AC4 in step 6) | Tester | [ ] |
| 4 | Tests first (step 5): hour start | `tests/Vandox.Storage.Tests/StorageTimeTests.cs` | AC5 | Tester | [ ] |
| 5 | Tests first (step 5): statistics, queries, migration; adapt the existing migration tests (`UndoStepEight`, version literals, version-9 refusal) as the plan lists | `tests/Vandox.Storage.Tests/SqliteStoreTemplateTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`, `tests/Vandox.Storage.Tests/Samples.cs` | AC6, AC7, AC8 | Tester | [ ] |
| 6 | Implement the normalizer, `HourStart`, schema step 8 and `SchemaVersion = 8`, the tally and `WriteTemplates` in the write path, the four queries with validation and exact error texts; remove the skeleton pragmas | `src/Vandox.Core/Templates/`, `src/Vandox.Storage/` files of task 2, `SchemaMigrator.cs` | AC1–AC8 | Dev | [ ] |
| 7 | Register `ILogTemplateReader` in the host | `src/Vandox.Backend/Hosting/ServeCommand.cs` | covered by existing `ServeTests` | Dev | [ ] |
| 8 | Benchmark mode `--log-lines N [--size N]` with the generated corpus and the printed figures; run it on the development host in Release with N = 1,000,000 and paste the output into `log.md`; run the metric measurement once as well | `tools/Vandox.StorageBenchmark/Program.cs`, `tools/Vandox.StorageBenchmark/TemplateBenchmark.cs` | AC9 | Dev | [ ] |
| 9 | `BENCHMARKS.md`: section *Log templates* (criterion, procedure, development-host row, reference-host row "not measured yet") and the metric row after this change | `docs/BENCHMARKS.md` | AC9 | Dev | [ ] |
| 10 | `.squad/project.md` security areas 10 and 12 (normalizer, template queries, record 0095); `.squad/stack.md` *Layout* | `.squad/project.md`, `.squad/stack.md` | — | Dev | [ ] |
| 11 | Coverage pass (step 6): `LogTemplateNormalizerTimeGrowsLinearly`; tests only for lines the *Coverage gate* reports uncovered | `tests/Vandox.Core.Tests/LogTemplateNormalizerTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreTemplateTests.cs` | AC4 | Tester | [ ] |
| 12 | Reference-host measurement on the DS918+ (`--log-lines 1000000`, SSD volume) and its row in `BENCHMARKS.md`, or the follow-up issue the plan names | `docs/BENCHMARKS.md` | AC9 | Product Manager / Orchestrator | [ ] |
