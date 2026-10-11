# Tasks: Mail log lines of Postfix and Dovecot become classified records

Plan: [plan.md](plan.md) — Status: Draft

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Decision record, index row, in-place edit of the unreleased 0086 (option 2, first consequence); revised after the challenge (no `Info` marker, option 16) and after security review round 1 (database anchors narrowed, options 19 and 20) | `docs/decisions/0091-mail-lines-classified-in-the-syslog-and-journal-parsers.md`, `docs/decisions/README.md`, `docs/decisions/0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md` | — | Lead | [x] |
| 2 | Skeleton: `MailEvents` (real constants) and `MailLineClassifier` (three members throwing under the skeleton pragma); *Build* passes | `src/Vandox.Core/LogParsing/MailEvents.cs`, `src/Vandox.Core/LogParsing/MailLineClassifier.cs` | — | Dev | [x] |
| 3 | Fixture of 20 lines exactly as in the plan | `testdata/logs/mail.log` | AC1.3, AC5.2 | Tester | [x] |
| 4 | Counting emitter test double | `tests/Vandox.Core.Tests/CountingEmitter.cs` | AC4 | Tester | [x] |
| 5 | Classifier tests: program rules, marker tables, database rule (incl. the remote-text and segment rows of AC3.2), fatal/panic precedence, payload unchanged apart from priority and event | `tests/Vandox.Core.Tests/MailLineClassifierTests.cs` | AC1.1, AC1.2, AC1.4, AC2, AC3 | Tester | [x] |
| 6 | Stage order and pass-through tests | `tests/Vandox.Core.Tests/SystemLogGrouperTests.cs` | AC5.3 | Tester | [x] |
| 7 | Syslog parser tests: fixture, mail names are no syslog names, heap-bound large mail log | `tests/Vandox.Core.Tests/SyslogParserTests.cs` | AC1.3, AC1.6, AC4, AC5.2 | Tester | [x] |
| 8 | Journal parser tests: mail entries, parity with the fixture | `tests/Vandox.Core.Tests/JournalExportParserTests.cs` | AC5.1, AC5.2 | Tester | [x] |
| 9 | Registry detection of mail log heads, cut first line under `mail.log` not claimed | `tests/Vandox.Core.Tests/BuiltInParsersTests.cs` | AC1.5, AC1.6 | Tester | [x] |
| 10 | Implement `MailLineClassifier`; call it in `SystemLogGrouper.Add` before the MariaDB stage; class summaries of `SystemLogGrouper` and `SyslogParser`; remove the skeleton pragma | `src/Vandox.Core/LogParsing/MailLineClassifier.cs`, `src/Vandox.Core/LogParsing/SystemLogGrouper.cs`, `src/Vandox.Core/LogParsing/SyslogParser.cs` | AC1–AC5 | Dev | [ ] |
| 11 | Area document: three stages, subsection *Mail lines (Postfix and Dovecot)* (rules, claim of mail logs, earlier imports stay unclassified, forged lines), *Duplicates*, *Related decisions*, *Implementation* | `docs/areas/log-import.md` | — | Dev | [ ] |
| 12 | README *Supported sources*: `mail.log` and the mail classification | `README.md` | — | Dev | [ ] |
| 13 | ARCHITECTURE *Components* (`Vandox.Core` item) and *Data flow* records (0091) | `docs/ARCHITECTURE.md` | — | Dev | [ ] |
| 14 | project.md: security area 10 (`MailLineClassifier`, 0091) and the *Test doubles* row "log parsers (C#)" (`CountingEmitter`) | `.squad/project.md` | — | Dev | [ ] |
| 15 | Coverage gate: tests until 80 % on new and changed lines | as needed in `tests/Vandox.Core.Tests/` | — | Tester | [ ] |
| 16 | Follow-up issue "[Logs] Dovecot's own log file is misread as syslog" (text in the plan) | GitHub | — | Orchestrator | [ ] |
