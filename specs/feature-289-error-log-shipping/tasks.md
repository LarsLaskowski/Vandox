# Tasks: Ship the Plesk and web server error logs from the agent

Plan: [plan.md](plan.md) — Status: Draft

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton: `ResolveLocal`, `Format`, `PleskPanel`, `Nginx`, `Apache`, `Parse` | `internal/syslogline/clock.go`, `internal/errorlogline/errorlogline.go` | AC1, AC3, AC9 | — | Dev | [x] |
| 2 | G1 | Tests first: grammar and clock tests | `internal/errorlogline/errorlogline_test.go`, `internal/syslogline/clock_test.go` | AC1, AC3, AC9 | 1 | Tester | [x] |
| 3 | G1 | Port `ErrorLogLine.TryParse` and `SyslogClock.ResolveLocal` with the fraction step | `internal/errorlogline/errorlogline.go`, `internal/syslogline/clock.go` | AC1, AC3, AC9 | 2 | Dev | [ ] |
| 4 | G2 | Skeleton: `logFormats`, `logFormatReason` | `internal/config/logshipping.go` | AC4 | — | Dev | [ ] |
| 5 | G2 | Tests first: adapt the accepted and refused format rows with the exact reason | `internal/config/logshipping_test.go` | AC4 | 4 | Tester | [ ] |
| 6 | G2 | Accept the five formats | `internal/config/logshipping.go` | AC4 | 5 | Dev | [ ] |
| 7 | G3 | Skeleton: `sourcePleskPanel`, `sourceSwCpServerError`, `sourceNginxError`, `sourceApacheError`, `fileFormat`, `formatOf`, `fileRecord`, `minTime`, `New`, the fields `formats` and `format` | `internal/logship/records.go`, `files.go`, `logship.go` | AC1, AC2, AC3, AC4, AC5, AC6, AC7, AC8, AC9, AC10, AC12 | 3 | Dev | [ ] |
| 8 | G3 | Fixtures and golden records from the C# import; C# golden and signature tests; adapt `BuiltInSignaturesWebServerErrorLogsYieldPhpAndNginxEvents` | `testdata/logs/*`, `tests/Vandox.Core.Tests/ErrorLogParserTests.cs`, `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs` | AC1, AC2, AC9 | — | Tester | [ ] |
| 9 | G3 | Tests first: collector tests of the error log formats, the bookkeeping rule of E14 with the budget and give-up-end cases (adapting `truncationAfterAFutureLine`) and `TestLogs_NewRefusesAnUnknownFormat` | `internal/logship/files_test.go`, `internal/logship/logship_test.go` | AC1, AC2, AC3, AC4, AC5, AC6, AC7, AC8, AC9, AC10, AC12 | 7, 8 | Tester | [ ] |
| 10 | G3 | Dispatch by format, sources, `New` check, bookkeeping rule of E14 | `internal/logship/records.go`, `files.go`, `logship.go` | AC1, AC2, AC3, AC4, AC5, AC6, AC7, AC8, AC9, AC10, AC12 | 9 | Dev | [ ] |
| 11 | G4 | No new API: README test | `deploy/agent/readme_test.go` | AC11 | — | Tester | [ ] |
| 12 | G4 | Measure fact 12; stand-in dry run of fact 13 | scratch only | AC11 | 10 | Dev | [ ] |
| 13 | G4 | Documentation updates of the plan, README procedure included | `docs/areas/agent.md`, `docs/areas/configuration-and-secrets.md`, `docs/areas/wire-format.md`, `docs/areas/log-import.md`, `docs/areas/detection-and-reports.md`, `docs/ARCHITECTURE.md`, `.squad/stack.md`, `.squad/project.md`, `README.md`, `deploy/agent/agent.yaml`, `deploy/agent/README.md` | AC11 | 11, 12 | Dev | [ ] |
