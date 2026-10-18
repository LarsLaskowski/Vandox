# Tasks: Network connection collector (replaces `lsof -ni`)

Plan: [plan.md](plan.md) — Status: Draft

| # | Task | Files | Criteria | Depends on | Owner | Done |
| - | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | Copy the anonymized samples byte for byte into the test data (`cmp` against `samples/`) and run the full Go suite once (fact 19) | `testdata/proc/net/{tcp,tcp6,udp,udp6}`, `testdata/connections/sandbox/{tcp,udp}` | AC1, AC6 | — | Tester | [ ] |
| 2 | Wire fields: Go model fields, `ListenerCheck` and rules; `MinorVersion = 1` in Go and C#; C# properties and rules | `internal/model/connection.go`, `internal/wire/wire.go`, `src/Vandox.Core/Model/ConnectionSnapshot.cs`, `src/Vandox.Core/Wire/WireFormat.cs` | AC10 | — | Dev | [ ] |
| 3 | Tests of the wire fields, the golden batch regenerated, the version tests adapted, storage round trip and connection peak | `internal/model/connection_test.go`, `internal/wire/{golden,encode,wire}_test.go`, `testdata/wire/all-kinds.jsonl`, `tests/Vandox.Core.Tests/{ConnectionSnapshotTests,WireContractTests,IncidentBuilderTests}.cs`, `tests/Vandox.Storage.Tests/SqliteStoreWriteTests.cs` | AC10 | 2 (skeleton) | Tester | [ ] |
| 4 | `procstats` exports `ListPIDs`, `CommandReader`, `Ended` and their tests | `internal/procstats/command.go`, `internal/procstats/command_test.go` | AC9 | — | Dev (code), Tester (tests) | [ ] |
| 5 | Table reader and parser with tests on the real samples | `internal/connstats/table.go`, `table_test.go` | AC1, AC6 | 1 | Dev (code), Tester (tests) | [ ] |
| 6 | Owner scan with tests | `internal/connstats/owners.go`, `owners_test.go` | AC4, AC5 | 1, 4 | Dev (code), Tester (tests) | [ ] |
| 7 | Inventory order and check with tests | `internal/connstats/inventory.go`, `inventory_test.go` | AC3, AC12 | 5 | Dev (code), Tester (tests) | [ ] |
| 8 | Collector, snapshot fill, logs; collector tests, import allow-list, benchmark | `internal/connstats/connstats.go`, `snapshot.go`, `connstats_test.go` | AC1, AC2, AC3, AC4, AC5, AC6, AC7, AC8, AC9, AC10, AC11, AC12 | 2, 5, 6, 7 | Dev (code), Tester (tests) | [ ] |
| 9 | Agent job and `Options.Connections`; `TestJobs_Schedules` adapted | `internal/agent/agent.go`, `internal/agent/agent_test.go` | AC2 | 8 | Dev (code), Tester (test) | [ ] |
| 10 | Measurements: capabilities and `hidepid` (facts 9, 10), memory figure (fact 17), benchmark (fact 18) | — (results go into rows 11 and 12) | AC5, AC7 | 8 | Dev | [ ] |
| 11 | Agent area *Connections*, *Rights*, *Status*, *Related decisions*, *Implementation*; wire format area | `docs/areas/agent.md`, `docs/areas/wire-format.md` | AC1, AC3, AC5, AC7, AC12 | 10 | Dev | [ ] |
| 12 | Rights per collector, benchmark figure, project and stack entries | `deploy/agent/README.md`, `docs/BENCHMARKS.md`, `.squad/project.md`, `.squad/stack.md` | AC5, AC7 | 10 | Dev | [ ] |
| 13 | Architecture and detection area, decision record 0115 with its index row | `docs/ARCHITECTURE.md`, `docs/areas/detection-and-reports.md`, `docs/decisions/0115-…md`, `docs/decisions/README.md` | AC10 | — | Lead | [x] |
