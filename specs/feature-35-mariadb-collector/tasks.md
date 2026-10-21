# Tasks: MariaDB collector

Plan: [plan.md](plan.md) — Status: Draft (revised with the plan)

Each task names the criteria of `spec.md` it delivers, the tasks it waits for and its group: one launch per member in steps 4–6, in
table order, its rows consecutive. Every documentation edit is in exactly one row.

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Check only (0093b4f fixed it): decode every field of both captures, no `144` prefix, `server_uid` the placeholder, no other real value; report, do not edit | `testdata/mariadb/select-status-variables-processlist.txt`, `testdata/mariadb/account-check.txt` | AC17 | — | Tester | [ ] |
| 2 | G2 | `error_code` in the Go model and the C# model with its checks | `internal/model/mariadb.go`, `src/Vandox.Core/Model/MariaDbStatus.cs` | AC6 | — | Dev | [ ] |
| 3 | G2 | Model, golden and shared-case tests; regenerate `all-kinds.jsonl`; adapt the expected kinds and case counts | `internal/model/mariadb_test.go`, `internal/wire/encode_test.go`, `internal/wire/decode_test.go`, `testdata/wire/all-kinds.jsonl`, `testdata/wire/decoder-cases.json`, `tests/Vandox.Core.Tests/PayloadValidationTests.cs`, `tests/Vandox.Core.Tests/WireContractTests.cs` | AC6 | 2 | Tester | [ ] |
| 4 | G3 | Section `mariadb` of the agent configuration | `internal/config/agent.go` | AC14 | — | Dev | [ ] |
| 5 | G3 | Exported readers `MemTotal` and `TCPListeners`, exported `procstats.CleanText` wrapper | `internal/sysstats/memory.go`, `internal/connstats/table.go`, `internal/procstats/cmdline.go` | AC8, AC9, AC11 | — | Dev | [ ] |
| 6 | G3 | Configuration and reader tests | `internal/config/agent_test.go`, `internal/sysstats/memory_test.go`, `internal/connstats/table_test.go` | AC9, AC11, AC14 | 4, 5 | Tester | [ ] |
| 7 | G4 | Protocol client: packets, statements, open, ping, query, close, error texts | `internal/mariadb/client.go`, `internal/mariadb/packet.go`, `internal/mariadb/statement.go` | AC3, AC5, AC12, AC13, AC17 | 1 | Dev | [ ] |
| 8 | G4 | Capture reader, fake server and client tests, SQL file test | `internal/mariadb/capture_test.go`, `internal/mariadb/fakeserver_test.go`, `internal/mariadb/client_test.go`, `internal/mariadb/packet_test.go` | AC12, AC13, AC15, AC17 | 1, 7 | Tester | [ ] |
| 9 | G5 | Collector: classification, selection, threads, derived metrics, account check, log state | `internal/mariadb/collector.go`, `internal/mariadb/sample.go`, `internal/mariadb/derived.go` | AC1, AC2, AC3, AC5, AC7, AC8, AC9, AC10, AC11, AC16 | 5, 7 | Dev | [ ] |
| 10 | G5 | Collector tests | `internal/mariadb/collector_test.go`, `internal/mariadb/sample_test.go`, `internal/mariadb/derived_test.go` | AC1, AC2, AC3, AC4, AC5, AC7, AC8, AC9, AC10, AC11, AC16 | 8, 9 | Tester | [ ] |
| 11 | G6 | Agent job and schedule; SQL file, example configuration, README options, deploy README, `.squad/project.md` security areas | `internal/agent/agent.go`, `deploy/agent/mariadb-user.sql`, `deploy/agent/agent.yaml`, `README.md`, `deploy/agent/README.md`, `.squad/project.md` | AC4, AC14, AC15 | 4, 9 | Dev | [ ] |
| 12 | G6 | Agent job test; `.squad/project.md` *Test doubles* row | `internal/agent/agent_test.go`, `.squad/project.md` | AC14 | 11 | Tester | [ ] |
| 13 | G7 | Area documents and `docs/ARCHITECTURE.md` as the plan's *Areas* | `docs/areas/agent.md`, `docs/areas/wire-format.md`, `docs/areas/configuration-and-secrets.md`, `docs/areas/network-and-security-model.md`, `docs/ARCHITECTURE.md` | AC1, AC6, AC14, AC15 | 11 | Lead | [ ] |
