# Tasks: Parsers for Plesk and web server error logs

Plan: [plan.md](plan.md) — Status: Draft

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Area documents and decision record (with the plan; revised for the challenge and the Security review: raw-line time only from a storable modification time, tie of two path options, RHEL Apache path, refused raw records) | `docs/areas/log-import.md`, `docs/areas/configuration-and-secrets.md`, `docs/decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md`, `docs/decisions/README.md` | — | Lead | [x] |
| 2 | Skeleton: new types and members of *Signatures*; `BuiltInParsers.Create(string?, ErrorLogPaths)`; `IRecordEmitter.Warn` in `RecordEmitter`; mechanical adaptation of the `Create` call sites (`JournalExportParserTests`, `SyslogParserTests`, `BuiltInParsersTests`) and of `RecordingEmitter` / `CountingEmitter` (`Warn`) | `src/Vandox.Core/LogParsing/*`, `src/Vandox.Core/Configuration/{ImportConfig,BackendConfig,BackendConfigLoader}.cs`, `src/Vandox.Import/{RecordEmitter,FileResult,ImportSummary}.cs`, `src/Vandox.Backend/Cli/ImportCommand.cs`, `src/Vandox.Backend/Hosting/BackendLog.cs`, the listed test files | — | Dev | [x] |
| 3 | Tests first (fail on the skeleton) and fixtures | `tests/Vandox.Core.Tests/{ErrorLogParserTests,ErrorLogLineTests,LogPathTests,BuiltInParsersTests,BackendConfigLoaderTests}.cs`, `tests/Vandox.Import.Tests/{ImporterStoreTests,FileResultTests,LineParser}.cs`, `tests/Vandox.Backend.Tests/{ImportSummaryWriterTests,ImportProgressLoggerTests,ImportCommandTests}.cs`, `testdata/logs/{plesk-panel.log,nginx-error.log,apache-error.log}` | AC1–AC6, AC8–AC14 (and AC7 except its step-6 tests) | Tester | [x] |
| 4 | `ErrorLogLine`, `LogPath`, `ErrorLogParser` (raw-line fallback time only from a storable `file.ModTime`), `BuiltInParsers` | `src/Vandox.Core/LogParsing/*` | AC1–AC10 | Dev | [ ] |
| 5 | Options: defaults, loading, validation, `Keys`, example file (comments: RHEL Apache path, Plesk paths not verified) | `src/Vandox.Core/Configuration/*`, `deploy/backend/vandoxd.yaml` | AC11 | Dev | [ ] |
| 6 | Importer `Warn`, totals, snapshot; summary, warning log line; `ImportCommand` wiring | `src/Vandox.Import/*`, `src/Vandox.Backend/Cli/*`, `src/Vandox.Backend/Hosting/BackendLog.cs` | AC12–AC14 | Dev | [ ] |
| 7 | Step-6 tests (determinism, heap bound, registry path level) and coverage | `tests/Vandox.Core.Tests/{ErrorLogParserTests,ParserRegistryTests}.cs` | AC7, AC10 | Tester | [ ] |
| 8 | README: backend options rows (Apache row names `/var/log/httpd/error_log` for RHEL-family servers), *Import logs* (sources, summary, limits, time zone); Plesk panel format and time zone, `sw-cp-server` format and Plesk paths worded as not verified | `README.md` | — | Dev | [ ] |
| 9 | ARCHITECTURE: parser lists (lines 10, 46), line-framed list (228), record link 0092 | `docs/ARCHITECTURE.md` | — | Dev | [ ] |
| 10 | Project knowledge: *Security areas* 10 and *Test doubles* | `.squad/project.md` | — | Dev | [ ] |
