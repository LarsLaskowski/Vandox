# Tasks: Parsers for journal export, syslog and kern.log

Plan: [plan.md](plan.md) — Status: Draft

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Skeleton: Go `LogLine.Host`; C# `LogLine.Host`, `ImportConfig`, `BackendConfig.Import`; every new LogParsing type with full signatures (bodies throw `NotImplementedException`); NodaTime package added; *Build* passes | `internal/model/logline.go`, `src/Vandox.Core/Model/LogLine.cs`, `src/Vandox.Core/Configuration/ImportConfig.cs`, `BackendConfig.cs`, `src/Vandox.Core/LogParsing/*.cs` (new types of the plan), `src/Vandox.Core/Vandox.Core.csproj`, `Directory.Packages.props` | — | Dev | [ ] |
| 2 | Tests first: journal reader and parser (incl. `JournalExportBuilder`, `RecordingEmitter`) | `tests/Vandox.Core.Tests/JournalExportReaderTests.cs`, `JournalExportParserTests.cs`, `JournalExportBuilder.cs`, `RecordingEmitter.cs` | AC-J1-J6, AC-K2, AC-D1, AC-X1 | Tester | [ ] |
| 3 | Tests first: syslog line, clock and parser | `tests/Vandox.Core.Tests/SyslogLineTests.cs`, `SyslogClockTests.cs`, `SyslogParserTests.cs` | AC-S1-S6, AC-D1, AC-X1 | Tester | [ ] |
| 4 | Tests first: kernel report grouping and the OOM fixture | `tests/Vandox.Core.Tests/KernelReportGrouperTests.cs`, `testdata/logs/kern.log-oom` | AC-K1, AC-K3-K5 | Tester | [ ] |
| 5 | Tests first: time zone lookup, built-in list, detection matrix, configuration option | `tests/Vandox.Core.Tests/SourceTimeZoneTests.cs`, `BuiltInParsersTests.cs`, `BackendConfigLoaderTests.cs` | AC-C1-C3 | Tester | [ ] |
| 6 | Tests first: `host` in model, wire and storage; golden sample with `Host: "web-1"` and the regenerated fixture; migration assertion moved to the current version | `internal/model/logline_test.go`, `internal/wire/encode_test.go`, `testdata/wire/all-kinds.jsonl`, `tests/Vandox.Core.Tests/PayloadValidationTests.cs`, `WireContractTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`, `SqliteStoreWriteTests.cs`, `Samples.cs` | AC-H1-H4 | Tester | [ ] |
| 7 | Tests first: `vandoxd import` with the built-in parsers and a Berlin configuration | `tests/Vandox.Backend.Tests/ImportCommandTests.cs` | AC-C4 | Tester | [ ] |
| 8 | Implement `host`: Go validation, C# validation, schema step 4, writer and queries | `internal/model/logline.go`, `src/Vandox.Core/Model/LogLine.cs`, `src/Vandox.Storage/StorageLimits.cs`, `SchemaMigrator.cs`, `BatchWriter.cs`, `RecordQueries.cs` | AC-H1-H4 | Dev | [ ] |
| 9 | Implement `import.time_zone` and `SourceTimeZone`; example file | `src/Vandox.Core/Configuration/BackendConfigLoader.cs`, `src/Vandox.Core/LogParsing/SourceTimeZone.cs`, `deploy/backend/vandoxd.yaml` | AC-C1 | Dev | [ ] |
| 10 | Implement the journal reader and parser | `src/Vandox.Core/LogParsing/JournalExportReader.cs`, `JournalEntry.cs`, `JournalExportParser.cs` | AC-J1-J6 | Dev | [ ] |
| 11 | Implement syslog line, clock and parser | `src/Vandox.Core/LogParsing/SyslogLine.cs`, `SyslogTime.cs`, `SyslogClock.cs`, `SyslogParser.cs` | AC-S1-S6 | Dev | [ ] |
| 12 | Implement the kernel report grouper and use it in both parsers | `src/Vandox.Core/LogParsing/KernelReportGrouper.cs` | AC-K1-K5 | Dev | [ ] |
| 13 | Built-in list and wiring | `src/Vandox.Core/LogParsing/BuiltInParsers.cs`, `src/Vandox.Backend/Cli/ImportCommand.cs`, `src/Vandox.Backend/Hosting/ServeHooks.cs` | AC-C2-C4 | Dev | [ ] |
| 14 | Coverage: ≥ 80 % on new/changed code and overall; add tests where needed | tests above | all | Tester | [ ] |
| 15 | Doc: `README.md` — *Backend options* row `import.time_zone`; *Import logs* supported sources, binary journal export, preserved times, time zone, stale "recognizes no file" sentence | `README.md` | — | Dev | [ ] |
| 16 | Doc: area *Log import* — section *System log parsers*, determinism wording in *Parsers*, `import.time_zone` in *Command* | `docs/areas/log-import.md` | — | Dev | [ ] |
| 17 | Doc: area *Wire format* — `log_line.host` | `docs/areas/wire-format.md` | — | Dev | [ ] |
| 18 | Doc: area *Storage* — schema version 4, `log_lines.host` | `docs/areas/storage.md` | — | Dev | [ ] |
| 19 | Doc: area *Configuration and secrets* — option `import.time_zone` | `docs/areas/configuration-and-secrets.md` | — | Dev | [ ] |
| 20 | Doc: `docs/ARCHITECTURE.md` — status sentence (line 10), `Vandox.Core` entry, schema version 4, links to 0084-0086 | `docs/ARCHITECTURE.md` | — | Dev | [ ] |
| 21 | Doc: `.squad/project.md` — *Security areas* 10 types and option; *Test doubles* row "log parsers (C#)" | `.squad/project.md` | — | Dev | [ ] |
| 22 | Code check: *Format*, *Analyzer gate* (Go and .NET), same tests green | changed files | — | Code Officer | [ ] |
| 23 | Approval: records 0084-0086 and their index rows (added as `Proposed` with the plan) to `Accepted` | `docs/decisions/0084-*.md`, `0085-*.md`, `0086-*.md`, `docs/decisions/README.md` | — | Lead | [ ] |
