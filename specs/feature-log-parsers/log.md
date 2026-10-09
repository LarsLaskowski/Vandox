# Log: feature log parsers (issue #16)

One line per pipeline step: date, step, member, result. Lead decisions and escalations are quoted in full.

| Date | Step | Member | Result |
| ---- | ---- | ------ | ------ |
| 2026-10-09 | 1 Intake | Orchestrator | Issue #16 read (open, dependency #15 merged); branch feature-log-parsers off main |
| 2026-10-09 | 2 Plan | squad-lead | DONE: tier security; spec, plan, tasks, records 0084-0086 (Proposed, indexed); no escalation |
| 2026-10-09 | 2 Plan challenge | squad-devils-advocate | OBJECTIONS 1 major (UTC default mis-times silently), 5 minor (repeated-hour tolerance, syslog+kern.log duplicate, log label normalization, name-date fallback dead, record order contract) |
| 2026-10-09 | 2 Plan revise | squad-lead | All 6 objections accepted; no import.time_zone default (fail file when unset), tolerance 10 min, name date before mtime, log value documented, order contract loosened; tier stays security |
| 2026-10-09 | 3 Plan security review | squad-security | CHANGES_REQUIRED (1st): B1 length bounds on UTF-8 after U+FFFD replacement; B2 out-of-range dates must skip not throw; B3 no flush of open kernel report on exception; B4 heap-bound tests for memory claims; non-blocking N1 NonBacktracking regex, N2 20-digit timestamp row, N3 note on #37 |
| 2026-10-09 | 3 Plan revise | squad-lead | B1-B4 and N1-N3 accepted: Utf8Text limits on decoded text, StorableTime range in Core, no flush on failed parse, heap-bound ACs M1-M4; tier security |
| 2026-10-09 | 3 Plan security review (2) | squad-security | APPROVED; non-blocking: N1 AC-S6 vs Accepted-forms contradiction (raw vs decoded host/tag limit), N2 define predecessor for year/repeated-hour rules, N3 journal resync memory row |
