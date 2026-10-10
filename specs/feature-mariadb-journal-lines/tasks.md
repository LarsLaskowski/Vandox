# Tasks: Classify MariaDB lines from the journal and syslog

Plan: [plan.md](plan.md) — Status: Revised (1)

Each documentation edit appears in exactly one row. The Lead's approval only touches the status, the index rows and
bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Area document: stage order, duplicates, recovery in input order (multi-host limitation), new subsection *Lines from the journal and syslog* (key, 60-second bound, empty lines journald drops), related decisions, implementation list | `docs/areas/log-import.md` | AC15 | Lead (step 2, revised) | [x] |
| 2 | Record 0088 extended (context, options 20 to 36, decision, consequences; options 31 and 34 chosen in the revision) and its index row; both set to `Proposed` | `docs/decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md`, `docs/decisions/README.md` | AC15 | Lead (step 2, revised) | [x] |
| 3 | Stale consequence of 0086 about the built-in list corrected (related defect) | `docs/decisions/0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md` | n/a | Lead (step 2) | [x] |
| 4 | Skeleton: `MariaDbLineGrouper` (with `MaxSpan`) and `SystemLogGrouper` (new files), overload `MariaDbMessage.Add(string, bool)`, method bodies `throw new NotImplementedException();` with the skeleton pragma; existing members and the two parsers untouched; *Build* passes | `src/Vandox.Core/LogParsing/MariaDbLineGrouper.cs`, `SystemLogGrouper.cs`, `MariaDbMessage.cs` | n/a | Dev (step 4) | [ ] |
| 5 | Tests for the grouper: header and continuation, other programs, line feed, no open entry, crash report, interleaving, one open entry and the time bound, bounds, recovery in input order, retained memory | `tests/Vandox.Core.Tests/MariaDbLineGrouperTests.cs` | AC1–AC7, AC9 (a)(b), AC12 | Tester (step 5) | [ ] |
| 6 | Tests for the stage order with kernel reports | `tests/Vandox.Core.Tests/SystemLogGrouperTests.cs` | AC14 | Tester (step 5) | [ ] |
| 7 | Tests for `Add(string, bool)`, equal to the raw overload | `tests/Vandox.Core.Tests/MariaDbMessageTests.cs` | AC7 | Tester (step 5) | [ ] |
| 8 | End-to-end tests through the journal parser: mapping, binary `MESSAGE` with a line feed, time bound, no zone, no recovery state shared between parses, skipped entry, cancellation, emitter exception, determinism, parity with the error log fixture with and without empty lines | `tests/Vandox.Core.Tests/JournalExportParserTests.cs` | AC1, AC3, AC6 (e), AC8, AC9 (c), AC10, AC11, AC13 | Tester (step 5) | [ ] |
| 9 | End-to-end tests through the syslog parser: mapping, `mysqld`, time bound, RFC 3339 and year-less zones, no recovery state shared between parses, skipped "invalid date" line, cancellation, determinism, parity with the error log fixture | `tests/Vandox.Core.Tests/SyslogParserTests.cs` | AC2, AC6 (e), AC8, AC9 (c), AC10, AC11, AC13 | Tester (step 5) | [ ] |
| 10 | Implement `MariaDbLineGrouper` (with the 60-second bound from the header) and `SystemLogGrouper`; `MariaDbMessage.Add(string, bool)` sharing the raw overload's tail; remove the skeleton pragmas | `src/Vandox.Core/LogParsing/MariaDbLineGrouper.cs`, `SystemLogGrouper.cs`, `MariaDbMessage.cs` | AC1–AC12, AC14 | Dev (step 6) | [ ] |
| 11 | Rewire both parsers to `SystemLogGrouper` | `src/Vandox.Core/LogParsing/JournalExportParser.cs`, `src/Vandox.Core/LogParsing/SyslogParser.cs` | AC1, AC2, AC6 (e), AC8, AC9 (c), AC10, AC11, AC13, AC14 | Dev (step 6) | [ ] |
| 12 | README *Import logs*, MariaDB bullet: replace the sentence about plain journal lines (text in plan, *Documentation updates*) | `README.md` | AC15 | Dev (step 6) | [ ] |
| 13 | *Security areas* 10: add `SystemLogGrouper` and `MariaDbLineGrouper` (text in plan, *Documentation updates*) | `.squad/project.md` | AC15 | Dev (step 6) | [ ] |
| 14 | *Test doubles*, row *log parsers (C#)*: name a new shared helper, only if one is added | `.squad/project.md` | n/a | Tester (step 5/6) | [ ] |
| 15 | Coverage gate: 80 % on new or changed lines and overall | n/a | all | Tester (step 6) | [ ] |
