# Tasks: Classify live log lines

Plan: [plan.md](plan.md) — Status: Draft

Groups in build order. G1 is the Go port with its golden fixtures; G2 is the state file; G3 is the integration in the collector;
G4 is the documentation.

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: package `logstages`: `MaxSpan`, `Pos`, `Line`, `Output`, `Stages`, `New`, `Add`, `Expire`, `Close`, `Held`, `HeldSince`, `Clone`, `ClassifyMail` | `internal/logstages/*.go` | AC1, AC4, AC7 | — | Dev | [ ] |
| 2 | G1 | Golden fixtures and C# golden tests (the import writes the expected records); detector test on them | `testdata/journal/classify.export`, `testdata/journal/classify-records.jsonl`, `testdata/logs/classify-syslog.log`, `testdata/logs/classify-syslog.records.jsonl`, `tests/Vandox.Core.Tests/JournalExportParserTests.cs`, `tests/Vandox.Core.Tests/SyslogParserTests.cs`, `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs` | AC1, AC2 | — | Tester | [ ] |
| 3 | G1 | Package tests: mail table, header forms, MariaDB grouping and events, kernel reports, `Expire`, `Close`, `Held`, `Clone` | `internal/logstages/mail_test.go`, `header_test.go`, `mariadb_test.go`, `kernel_test.go`, `stages_test.go` | AC1, AC4, AC7 | 1 | Tester | [ ] |
| 4 | G1 | Port the stages until the tests pass | `internal/logstages/*.go` | AC1, AC4, AC7 | 3 | Dev | [ ] |
| 5 | G2 | Skeleton: `ReplayMark`, `Replay` (in `LogPositions`), `MaxReplayMarks` | `internal/agentstate/agentstate.go` | AC5 | — | Dev | [ ] |
| 6 | G2 | Tests: strict decoding of marks; worst case with 17 marks within 64 KiB | `internal/agentstate/agentstate_test.go` | AC5 | 5 | Tester | [ ] |
| 7 | G2 | Encode and strictly decode the marks | `internal/agentstate/agentstate.go` | AC5 | 6 | Dev | [ ] |
| 8 | G3 | Skeleton: stages per source in the state, the run method that hands a line to the stages, mark save and restore (unexported only) | `internal/logship/stages.go`, `state.go`, `positions.go` | AC1, AC3, AC5, AC8, AC10 | 4, 7 | Dev | [ ] |
| 9 | G3 | Collector tests of *Tests per criterion*; heap test adapted | `internal/logship/stages_test.go`, `internal/logship/logship_test.go` | AC1, AC3, AC4, AC5, AC6, AC7, AC8, AC10 | 2, 8 | Tester | [ ] |
| 10 | G3 | Integrate: feed after the filter, `Expire` and `Close` rules, gaps, give-up, clone, replay | `internal/logship/stages.go`, `journal.go`, `files.go`, `state.go`, `positions.go`, `logship.go`, `run.go` | AC1, AC3, AC4, AC5, AC6, AC7, AC8, AC10 | 9 | Dev | [ ] |
| 11 | G3 | Confirm the existing compatibility tests stay green, unchanged | `tests/Vandox.Core.Tests/WireContractTests.cs`, `internal/wire/golden_test.go`, `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` | AC9 | 10 | Tester | [ ] |
| 12 | G4 | Documentation updates of the plan (no new API) | `docs/areas/agent.md`, `docs/areas/log-import.md`, `docs/areas/wire-format.md`, `docs/areas/configuration-and-secrets.md`, `docs/areas/detection-and-reports.md`, `docs/ARCHITECTURE.md`, `deploy/agent/agent.yaml`, `.squad/stack.md`, `.squad/project.md` | AC6 | 10 | Dev | [ ] |
