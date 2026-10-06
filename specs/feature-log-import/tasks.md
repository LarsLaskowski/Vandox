# Tasks: Import framework for log archives

Plan: [plan.md](plan.md) — Status: Draft

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Skeleton: all new types, constants and functions with the signatures of the plan, bodies returning zero values or `errors.New("not implemented")`; `Batch.Import` field; `ImportTracker` and compile-time assertions; `storetest.Fake.BeginImport`/`ImportStarts`; `run` dispatch stub; no migration step 3 yet | `internal/logparse/{logparse,registry,lines}.go`, `internal/logparse/logparsetest/parser.go`, `cmd/vandoxd/internal/store/{import,write,repository}.go`, `cmd/vandoxd/internal/store/storetest/fake.go`, `cmd/vandoxd/internal/importer/{importer,scan,archive,sniff,open_unix,open_other}.go`, `cmd/vandoxd/{main,import}.go` | — (*Build* passes) | Dev | [ ] |
| 2 | Tests first for the parser interface, registry, line reader and test parser | `internal/logparse/registry_test.go`, `internal/logparse/lines_test.go`, `internal/logparse/logparsetest/parser_test.go` | AC-P1–AC-P5 | Tester | [ ] |
| 3 | Tests first for the store: migration 3, `BeginImport`, `Batch.Import`, `CheckImportRecord`, fake; update `TestOpen_CreatesSchema` to version 3 and `import_files` | `cmd/vandoxd/internal/store/{import,write,migrate}_test.go`, `cmd/vandoxd/internal/store/storetest/fake_test.go` | AC-S1–AC-S8 | Tester | [ ] |
| 4 | Tests first for the importer: input forms, listed forms, idempotency and resume against a real store, summary, failures, limits, batching, flat memory with a generated 64 MiB file, progress, sniffing, `openRegular`, `eachEntry`, `scan` | `cmd/vandoxd/internal/importer/{importer,scan,archive,sniff,open_unix}_test.go` | AC-I1–AC-I13 | Tester | [ ] |
| 5 | Tests first for the command: dispatch, flags, summary text and quoting, exit codes, progress logging, `importParsers` | `cmd/vandoxd/import_test.go`, `cmd/vandoxd/main_test.go` | AC-C1–AC-C6 | Tester | [ ] |
| 6 | Implement `internal/logparse` and `logparsetest` | files of task 2 | AC-P1–AC-P5 | Dev | [ ] |
| 7 | Implement the store changes: migration step 3, `SchemaVersion` 3, `BeginImport`, import step in `WriteBatch`, `CheckImportRecord`, fake | files of task 3 | AC-S1–AC-S8 | Dev | [ ] |
| 8 | Implement the importer: scan (pass 1), archive iteration, sniffing, `openRegular`, import (pass 2) with hashing/counting/context reader, batching, resume, summary, progress | `cmd/vandoxd/internal/importer/*.go` | AC-I1–AC-I13 | Dev | [ ] |
| 9 | Implement `vandoxd import`: dispatch and usage in `run`, `importCommand`, `writeSummary`, `importParsers` | `cmd/vandoxd/main.go`, `cmd/vandoxd/import.go` | AC-C1–AC-C6 | Dev | [ ] |
| 10 | Documentation: README (*Binaries*, *Layout*, *Import logs*), `docs/ARCHITECTURE.md` (status, components, data flow, storage, links 0069–0072, 0072 for 0058), `.squad/project.md` (security areas 9 and 10, test doubles, integration surface "a new log parser") | `README.md`, `docs/ARCHITECTURE.md`, `.squad/project.md` | — | Dev | [ ] |
| 11 | Coverage ≥ 80 % on new/changed code and overall; add tests for uncovered branches | test files above | all | Tester | [ ] |
| 12 | Format and analyzer gate (incl. gocognit on `scan` and pass 2) | changed files | — | Code Officer | [ ] |
