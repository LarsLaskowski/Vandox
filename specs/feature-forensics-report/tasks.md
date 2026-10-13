# Tasks: Forensics report

Plan: [plan.md](plan.md) — Status: Draft

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Area documents, architecture, records 0098–0100 and their index rows | `docs/areas/detection-and-reports.md`, `docs/areas/backend-host.md`, `docs/ARCHITECTURE.md`, `docs/decisions/0098`–`0100`, `docs/decisions/README.md` | — | Lead | [x] |
| 2 | Skeleton (step 4): every new type and member of *Signatures* with XML docs and `#pragma warning disable RH2003, S2325`; `RecordPager` moved out of `IncidentReconstructor` already in the skeleton (the reconstructor's tests must stay green); `CommandOptions` properties; `BackendLog` messages 38–40; *Format*, *Build* | `src/Vandox.Core/Reports/*.cs`, `src/Vandox.Backend/Reports/*.cs`, `src/Vandox.Backend/Incidents/RecordPager.cs`, `src/Vandox.Backend/Incidents/IncidentReconstructor.cs`, `src/Vandox.Backend/Cli/ReportCommand.cs`, `src/Vandox.Backend/Cli/CommandOptions.cs`, `src/Vandox.Backend/Hosting/BackendLog.cs` | — | Dev | [x] |
| 3 | Tests first (step 5): the tests named in AC1–AC17, failing on the skeleton | `tests/Vandox.Core.Tests/{ObservedHours,SnapshotSeries,ConsumerTracker,RecurrenceRules,PanelPrograms,ForensicsAnalysis}Tests.cs`, `tests/Vandox.Backend.Tests/{RecordPager,ForensicsReporter,MarkdownReportWriter,MarkdownText,ReportText,ReportCommand}Tests.cs`, `tests/Vandox.Backend.Tests/CommandLineTests.cs` | AC1–AC17 | Tester | [x] |
| 4 | Analysis: `ObservedHours`, `SnapshotSeries`, `ConsumerTracker`, `RecurrenceRules`, `PanelPrograms`, `ForensicsAnalysis` and the result types | `src/Vandox.Core/Reports/*.cs` | AC5–AC12 | Dev | [ ] |
| 5 | Reader and writer: `ForensicsReporter` (passes, incident paging), `MarkdownReportWriter`, `MarkdownText`, `ReportText` (English and German tables) | `src/Vandox.Backend/Reports/*.cs` | AC3, AC4, AC13–AC15, AC17 | Dev | [ ] |
| 6 | Command: `ReportCommand`, `CommandLine` (`report`, `-lang`, usage, class comment), `BackendApp` dispatch | `src/Vandox.Backend/Cli/ReportCommand.cs`, `src/Vandox.Backend/Cli/CommandLine.cs`, `src/Vandox.Backend/Hosting/BackendApp.cs` | AC1, AC2 | Dev | [ ] |
| 7 | `RecordPager` implementation (moved body) | `src/Vandox.Backend/Incidents/RecordPager.cs` | AC16 | Dev | [ ] |
| 8 | README: *Binaries* paragraph, section *Write the forensics report* (example without `-t`), `incidents.window_before` description | `README.md` | — | Dev | [ ] |
| 9 | `.squad/project.md` *Security areas* 10 and 12 (report reads, caps, `MarkdownText.Code`, `-lang` error through `Terminal.Quote`, records 0098–0100) | `.squad/project.md` | — | Dev | [ ] |
| 10 | `.squad/project.md` *Test doubles*, database (C#) row: the decorators the report tests add | `.squad/project.md` | — | Tester | [x] |
| 11 | Coverage pass (step 6): uncovered lines from the gate, plus the resource bound `ForensicsReporterTests.ForensicsReporterKeepsOneIncidentAtATime` | `tests/Vandox.Backend.Tests/ForensicsReporterTests.cs` and others as the gate reports | AC4 | Tester | [ ] |
| 12 | Records 0098–0100 and their index rows to `Accepted` | `docs/decisions/` | — | Orchestrator (step 9) / Lead | [ ] |
