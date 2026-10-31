# Spec: Classify live log lines

Source: Issue #290
Status: Draft

## Problem

The agent ships journal lines and the lines of configured `syslog` files as `log_line` records with the import's mapping
(#37, record 0121), but without the three stages the import's journal and syslog parsers run after the mapping
([Log import](../../docs/areas/log-import.md), *System log parsers*): the mail classification, MariaDB entries and kernel
reports. A live line therefore has no `event`, a Postfix or Dovecot line keeps the priority its transport gave it, a MariaDB
crash report arrives as many single lines with the journal's priority `info`, and a kernel OOM report as many kernel lines
([Agent](../../docs/areas/agent.md), *Log shipping*, residual "Live lines carry no classification"). The signatures that need an
event (`mariadb_crash`, `mariadb_recovery_start`, `mariadb_recovery_end`, `postfix_fatal`, `dovecot_fatal`;
`src/Vandox.Core/Signatures/signatures.yaml`) never match live data, and the same line is stored differently live and on import.
The operator who reads the logs view or an incident sees a crash only when the logs were imported afterwards.

## Scenarios

The "live record" is the stored record of a line the agent shipped; the "import's record" is the record `vandoxd import` stores
for the same line in a journal export or a syslog file. "Stored" means visible in the logs view and to the detector.

- S1: Given Postfix writes `fatal: ...` (or `panic: ...`) to the journal, when the agent ships the line, then the live record has
  priority 2 and event `postfix.fatal` (`postfix.panic`), and the detector finds a `postfix_fatal` event for it.
- S2: Given Dovecot writes `auth-worker(1234): Error: mysql(localhost): Connect failed to database (mail): Can't connect to local
  server ...`, `Panic: ...` or `imap(u): Warning: ...`, when the agent ships the line, then the live record has the marker's
  priority and the import's event (`dovecot.database_unavailable`, `dovecot.panic`, none), and the `Panic:` line gives a
  `dovecot_fatal` event.
- S3: Given a configured `syslog` file such as `/var/log/mail.log` holds Postfix and Dovecot lines, when the agent ships them, then
  each live record carries the priority and event the import gives the same line of that file: a marker's priority replaces
  the `<PRI>` or journal priority, and a line without a marker keeps its priority and has no event.
- S4: Given MariaDB under systemd writes a crash report to standard error (the header `... [ERROR] mariadbd got signal 11 ;`
  followed by stack lines of the same pid within 60 seconds), when the agent ships these journal lines, then one live record
  holds the entry: the header's message after its prefix and the stack lines joined by line feeds, priority 3, event
  `mariadb.abort`; the detector finds a `mariadb_crash` event with `signal` 11.
- S5: Given MariaDB starts a crash recovery and ends it (`InnoDB: Starting crash recovery ...`, later `InnoDB: ... started; log
  sequence number ...`), when the agent ships both entries, then they get `mariadb.recovery_start` and `mariadb.recovery_end`, and
  the detector finds `mariadb_recovery_start` and `mariadb_recovery_end` events.
- S6: Given the kernel writes an OOM report (`... invoked oom-killer: ...` to `Out of memory: Killed process ...`), when the agent
  ships those kernel lines, then one live record holds the report as the import's *Kernel reports* rule builds it, and
  `oom_kill` matches it as on import.
- S7: Given the lines of one MariaDB entry or kernel report are read by different collector runs and sent in different batches,
  when they are stored, then they form the same one record as when all of them arrive at once.
- S8: Given a MariaDB entry or kernel report is open, when lines of other programs (or other hosts' kernel lines) arrive, then
  those are stored as they come, before the open entry's record, without waiting for it, as the import orders them.

## Errors and edge cases

- E1: No further line comes after a MariaDB entry or kernel report started (MariaDB writes nothing more, the kernel report has no
  end line): the record is stored once no line can join it any more, 60 seconds after its first line was read, never held
  indefinitely; live input has no "normal end of input" that closes it.
- E2: The agent is stopped (SIGTERM, an update) or the backend restarts while an entry or report is open: every line of it is still
  stored exactly once. The entry may be stored as two records (before and after the restart), and a recovery end read after the
  restart may lack `mariadb.recovery_end`; the Agent area names which. An unclean end keeps today's residual (record 0121: up to
  12 seconds of lines shipped again), the repeated lines classified alike.
- E3: The filter (`log_shipping.min_priority`, `log_shipping.drop`) judges each line as mapped, before classification, as today:
  a Postfix `warning:` line sent with priority `info` is filtered by its transport priority; under `min_priority` stricter than
  `info` the journal's MariaDB lines (all `info`, the unit's `SyslogLevel=`) are not shipped, crash reports included; a drop
  rule that removes some lines of an entry leaves the entry without them. The configuration area and `deploy/agent/agent.yaml`
  state the MariaDB consequence next to `min_priority`.
- E4: Hostile local writers (`logger -t mariadbd`, `-t postfix/smtpd`, `-t kernel`, a forged `SYSLOG_PID`) get the import's
  classification and its documented limitations (forged events, a forged header ending the server's entry); a flood of such
  lines never holds more than one open MariaDB entry and one open kernel report per source, each within the import's bounds
  (16,384 bytes; 60 seconds; 2,000 lines), so it cannot grow memory or delay other lines.
- E5: An entry that would exceed 16,384 bytes, a crash report whose lines come more than 60 seconds after its header, and a kernel
  report over 2,000 lines or 16,384 bytes are split or cut exactly as on import (*Lines from the journal and syslog*, *Kernel
  reports*).
- E6: Lines the backend stored before this version stay unclassified (a stored record is never updated). An agent and a backend
  of different versions keep exchanging batches without a refusal; whether such a pair classifies is stated in the Agent area.
- E7: Lines that the import does not run through these stages stay unchanged: the error log formats (`plesk-panel`,
  `nginx-error`, `sw-cp-server-error`, `apache-error`) and the raw lines of a `syslog` file (no syslog header, an impossible time).
- E8: Lines of different sources (the journal; each configured file) are never grouped or ordered together, as the import groups
  only within one file: a MariaDB entry in the journal and its copy in `/var/log/syslog` are two records, as today.

## Non-functional requirements

- Delay: a line that can join an open entry or report is stored at most about 60 seconds plus the shipping delay after its
  entry's first line was read; every other line is not delayed. The 10-minute allowance of live records
  ([Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Live data and backfill*) keeps such records live.
- Memory: the classification's held state is bounded independently of the input (E4); the agent's log collector bound of
  24 MiB ([Agent](../../docs/areas/agent.md), *Log shipping*, *Memory*) and the backend's ingest bounds stay.
- Security: classification is a reading of text, not a trust boundary (the import's *Forged lines* limitations hold live); the
  agent gains no right and starts no program (records 0019, 0030); no new text reaches a display unescaped.
- Compatibility: wire format version unchanged (`log_line.event` exists since 0088); no storage migration of stored records; no new
  configuration option; the meaning of `min_priority` and drop rules unchanged (E3).
- Logs: no log line per classified line.

## Acceptance criteria

- [ ] AC1: For every line of a classification fixture shipped live from the journal and from a configured `syslog` file, the
  live records equal the import's records of the same input in every field except `log`, origin and record identity; the fixture
  covers Postfix and Dovecot markers (both Dovecot positions, the database rules, a line without a marker), a MariaDB crash
  report with stack lines, a recovery start and end, a `mariadb.start`, an entry ended by the 16,384-byte bound, a kernel OOM report
  with an interleaved line of another program, plain lines, error log lines and raw lines (S1, S2, S3, S4, S5, S6, S8, E5, E7).
- [ ] AC2: On the live records of the fixture the detector finds `postfix_fatal`, `dovecot_fatal`, `mariadb_crash` (with
  `signal`), `mariadb_recovery_start` and `mariadb_recovery_end` events with the same fields as on the import's records (S1, S2,
  S4, S5).
- [ ] AC3: An entry and a kernel report whose lines are read in separate collector runs and arrive in separate batches are stored
  as the same one record as when read in one run (S7).
- [ ] AC4: An open entry or report with no further line is stored once 60 seconds have passed since its first line was read,
  and not before (E1).
- [ ] AC5: A clean agent stop and start, and a backend restart, while an entry or report is open store every line of it exactly
  once (E2).
- [ ] AC6: `min_priority` and drop rules decide on the mapped line before classification: a test shows a Postfix `warning:` line
  with transport priority `info` dropped under `min_priority: notice`, and a MariaDB journal line dropped under `min_priority:
  warning`; the configuration area and the example file name the MariaDB consequence (E3).
- [ ] AC7: A flood of forged `mariadbd` and `kernel` lines keeps at most one open entry and one open report per source within the
  import's bounds, and lines of other programs are stored without waiting (E4, S8).
- [ ] AC8: Lines of the journal and of a configured file are never joined into one record (E8).
- [ ] AC9: A batch of the previous agent version is accepted by the new backend and a batch of the new agent by the previous
  backend's decoder; records stored before the change are not altered (E6).

## Out of scope

- Live shipping and classification of MariaDB's and Dovecot's own log files (import-only, record 0121).
- Classifying records stored before this change, and deduplicating live and imported copies of a line (log-import *Duplicates*).
- Changing what the filter judges (E3); alerting on events (#56, #61); the agent's `kernel_event` collector.

## Open questions

- Where does classification run, in the agent or at ingest? Answered: the issue leaves it to the squad; the plan decides it and
  records the decision. This spec is written so that either location can meet it.
- Do the grouping stages (MariaDB entries, kernel reports) apply live, or only events and priorities? Answered: all three; the
  issue names the three stages and requires the live record to equal the import's.
- Does the filter see the classified priority? Answered: no. The configuration area defines `min_priority` and drop rules on the
  mapped line; keeping that leaves every existing configuration's meaning intact, and MariaDB journal lines under a stricter
  `min_priority` are already dropped today (log-import *Lines from the journal and syslog*: `PRIORITY` is the unit's
  `SyslogLevel=`).
- What replaces the import's "normal end of input" live? Answered: the import's own 60-second bound, measured from when the
  first line was read (E1).
- Do the issue's claims about today's behavior hold? Answered: yes; live records have no `event` (agent area *Journal entries*, *Syslog lines*; wire-format
  `log_line`) and that the five signatures need an event (`signatures.yaml`; `mariadb_crash` also needs its message pattern).
  #37 is closed.

## Area documents

- [Agent](../../docs/areas/agent.md), *Log shipping*: live lines get the import's three stages (or are sent for them), the
  held state, its 60-second close, stop and restart behavior, version mix; the residual "no classification" goes.
- [Log import](../../docs/areas/log-import.md), *Duplicates*: live copies now carry the same classification; *System log
  parsers* names the live path as a second user of the stages.
- [Wire format](../../docs/areas/wire-format.md), `log_line`: the agent's `event` is no longer always empty (or stays empty if
  classification runs at ingest, with the reason).
- [Configuration and secrets](../../docs/areas/configuration-and-secrets.md), `log_shipping.min_priority`: the MariaDB journal
  consequence of E3.
- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Log signatures*: event signatures match live lines.
