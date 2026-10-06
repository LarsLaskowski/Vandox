# Tasks: SQLite storage layer

Plan: [plan.md](plan.md) — Status: Draft

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Skeleton: every signature of *Signatures* in plan.md with doc comments; bodies return zero values or `errors.New("not implemented")`; `SchemaVersion = 2`, `dsn(path, query)`, `migrations` list with versions 1 and 2 and the step SQL may already be filled in; `go build ./...` passes | `cmd/vandoxd/internal/store/store.go`, `migrate.go`, `write.go`, `read.go`, `repository.go`, `storetest/fake.go` | — | Dev | [ ] |
| 2 | Tests first: adapt `TestOpen_CreatesDatabase` and `TestOpen_RefusesNewerSchema`; connection tests | `cmd/vandoxd/internal/store/store_test.go` | AC-C1, AC-C2, AC-C4, AC-M4 | Tester | [ ] |
| 3 | Tests first: migrations | `cmd/vandoxd/internal/store/migrate_test.go` | AC-M1–AC-M3, AC-M5–AC-M8 | Tester | [ ] |
| 4 | Tests first: writing, concurrency, benchmark | `cmd/vandoxd/internal/store/write_test.go` | AC-W1–AC-W9, AC-C3, AC-B1 | Tester | [ ] |
| 5 | Tests first: reading and log search, incl. the guard table | `cmd/vandoxd/internal/store/read_test.go` | AC-R1–AC-R4, AC-S1–AC-S5 | Tester | [ ] |
| 6 | Tests first: fake | `cmd/vandoxd/internal/store/storetest/fake_test.go` | AC-F2–AC-F5 | Tester | [ ] |
| 7 | Implement two pools, `Open`, `initialize`, `Ping`, `Close` | `store.go` | AC-C1–AC-C4 | Dev | [ ] |
| 8 | Implement `migrate`, `currentVersion`, step 2 schema | `migrate.go` | AC-M1–AC-M8 | Dev | [ ] |
| 9 | Implement batch validation and `WriteBatch` (dedup via `RowsAffected`, never `LastInsertId` alone) | `write.go` | AC-W1–AC-W9, AC-C3 | Dev | [ ] |
| 10 | Implement `Records`, `recordsQuery`, `SearchLogs`, `ftsQuery` | `read.go` | AC-R1–AC-R4, AC-S1–AC-S5 | Dev | [ ] |
| 11 | Implement `Fake` | `storetest/fake.go` | AC-F1–AC-F5 | Dev | [ ] |
| 12 | Coverage ≥ 80 % on new/changed code and overall | — | all | Dev, Tester | [ ] |
| 13 | Run the benchmark once on the development host and record CPU, date and results | `docs/BENCHMARKS.md` | AC-B2 | Dev | [ ] |
| 14 | Documentation updates from plan.md | `docs/ARCHITECTURE.md`, `docs/WIRE_FORMAT.md`, `docs/BENCHMARKS.md`, `docs/UNIT_TESTS.md`, `.squad/project.md` | AC-B2 | Dev | [ ] |
| 15 | Follow-up issue "[Tests] Measure the storage write benchmark on the DS918+" (title and body in plan.md); its number into `docs/BENCHMARKS.md` and the PR | — | AC-B2 | Orchestrator, Dev | [ ] |
| 16 | Format and analyzer gate | changed files | — | Code Officer | [ ] |
| 17 | At approval: decision records 0063–0068 `Accepted`, 0057 `Superseded by 0065`, index updated | `docs/decisions/` | — | Lead | [ ] |
