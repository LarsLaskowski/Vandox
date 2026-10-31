# Spec: Classify live log lines

Source: Issue #290
Status: Approved by Lead

## Problem

Live `log_line` records (#37, record 0121) skip the three stages the import runs after the mapping
([Log import](../../docs/areas/log-import.md), *System log parsers*): mail classification, MariaDB entries, kernel reports. So
they have no `event`, mail lines keep their transport priority, and a MariaDB crash report or kernel OOM report arrives as many
single lines. The event signatures (`mariadb_crash`, `mariadb_recovery_start`, `mariadb_recovery_end`, `postfix_fatal`,
`dovecot_fatal`) never match live data. An operator sees such a crash only after importing the logs.

## Scenarios

"Live record": the stored record of a shipped line; "import's record": what `vandoxd import` stores for the same line.

- S1: Given Postfix writes `fatal: ...` to the journal, when the agent ships it, then the live record has priority 2 and event
  `postfix.fatal`, and the detector finds a `postfix_fatal` event.
- S2: Given Dovecot writes a `Panic: ...` line or an `auth-worker(N): Error: mysql(...): Connect failed to database ...` line,
  when the agent ships it, then the live record has the marker's priority and the import's event (`dovecot.panic`, which gives
  `dovecot_fatal`, or `dovecot.database_unavailable`).
- S3: Given a configured `syslog` file such as `/var/log/mail.log`, when the agent ships its mail lines, then each live record has
  the import's priority and event: a marker's priority replaces the transport priority, and an unmarked line keeps its own.
- S4: Given MariaDB writes a crash report to the journal (`... [ERROR] mariadbd got signal 11 ;` and stack lines), when the agent
  ships it, then one live record holds the entry as the import builds it (priority 3, `mariadb.abort`), and `mariadb_crash`
  matches with `signal` 11.
- S5: Given MariaDB starts and ends a crash recovery, when the agent ships the entries, then they get `mariadb.recovery_start`
  and `mariadb.recovery_end`, and both signatures match.
- S6: Given the kernel writes an OOM report, when the agent ships its lines, then one live record holds the report as the
  import's *Kernel reports* rule builds it, and `oom_kill` matches.
- S7: Given the lines of one MariaDB entry or kernel report are read by different collector runs and sent in different batches,
  when they are stored, then they form the same one record as when all of them arrive at once.
- S8: Given a MariaDB entry or kernel report is open, when lines of other programs (or other hosts' kernel lines) arrive, then
  those are sent and stored without waiting for the open entry: their records get a lower record identity than the entry's,
  as the import emits them before it. (Views ordered by capture time still show the entry at its header's earlier time.)

## Errors and edge cases

- E1: Live input has no "normal end of input". The import's bounds count capture time, and so do the live ones. An open MariaDB
  entry or kernel report closes at the import's own ends: a line that ends it, the 16,384-byte or 2,000-line bound, or a line of
  its source captured more than 60 seconds after its first line. It also closes when its source has been read to its end and
  the (injected) clock is more than 60 seconds past the earlier of the first line's capture time and the start of the run that
  read it. A forged line dated in the future is therefore never held longer. It is never held indefinitely. In a backlog
  (after a restart, or runs cut by their 1,000-record budget), a continuation line captured within 60 seconds but read later still
  joins, as on import. Two closes differ from the import, which keeps such an entry open until a later line ends it, and both are
  named in the Agent area. First, a line captured within 60 seconds of the header that arrives after a line already more than 60
  seconds later no longer joins; that happens only when the clock steps back. Second, an entry still open when its journal ID or
  renamed file has been read to its end is closed there.
- E2: The agent or the backend ends while an entry or report is open, cleanly (SIGTERM, an update) or uncleanly (SIGKILL, a crash,
  a power loss). **No line is lost silently.** A line the backend acknowledged survives a backend crash, and a line whose
  position the agent saved has reached the backend or the durable spool. After an unclean end of the agent, the lines repeated
  are at most those of the open entry's source captured from that entry's first line on, which spans up to 60 seconds of
  capture time, plus today's residual of up to 12 seconds (record 0121). They are stored again without a duplicate mark, as
  today. A clean stop repeats nothing. In both cases the entry may be stored as two records, before and after the restart. A
  recovery end read after the restart may lack `mariadb.recovery_end`. The Agent area names both.
- E3: The filter (`log_shipping.min_priority`, `log_shipping.drop`) judges each line as mapped, before classification, as today.
  A Postfix `warning:` line sent with priority `info` is filtered by its transport priority. Under a `min_priority` stricter than
  `info` the journal's MariaDB lines are not shipped, crash reports included, because they are all `info` (the unit's
  `SyslogLevel=`). The configuration area and `deploy/agent/agent.yaml` state this next to `min_priority`. **A filtered line is
  invisible to the stages.** It neither opens nor ends an entry or report and joins none. A drop rule that removes some lines of
  an entry leaves the entry without them. A dropped MariaDB header leaves its continuation lines to the previous open entry of the
  same key within 60 seconds, or as plain lines; the configuration area says so for drop rules.
- E4: Hostile local writers (`logger -t mariadbd`, `-t postfix/smtpd`, `-t kernel`) get the import's classification and its
  *Forged lines* limitations. A flood holds at most one open entry and one open report per source, within the import's bounds.
- E5: Entries and reports over the import's bounds are split or cut exactly as on import.
- E6: Lines the backend stored before this version stay unclassified (a stored record is never updated). An agent and a backend
  of different versions keep exchanging batches without a refusal; whether such a pair classifies is stated in the Agent area.
- E7: Lines that the import does not run through these stages stay unchanged: the error log formats (`plesk-panel`,
  `nginx-error`, `sw-cp-server-error`, `apache-error`) and the raw lines of a `syslog` file (no syslog header, an impossible time).
- E8: Lines of different sources (the journal; each configured file) are never grouped or ordered together, as the import groups
  only within one file. In the journal each sequence-number ID is a source of its own, with its own recovery state. An entry read
  from a user file (`user-<UID>.journal`, web-space users under journald's `SplitMode=uid`) gets the mail classification only:
  no MariaDB entries and no kernel reports. That holds whatever ID journald gives the file. They never join or end the server's entries. journald writes
  the MariaDB server's and the kernel's lines to the system files, so this differs from the import only for `mariadbd` or
  `kernel` lines that a user forged in a user file. The import groups those; live they stay single lines. The Agent area names
  the difference.
- E9: A line is read again after a dropped run: it never joins an entry twice, and the entry is the one a single reading gives.
- E10: The logs collector records a gap in a source (a given-up span, an unreadable entry, a truncation, a lost rotation, a
  vacuumed journal span): the open entry and report of that source end at the gap, and lines after it never join them.

## Non-functional requirements

- Delay: with the source caught up, the lines of an open entry or report are stored at most about 60 seconds plus the shipping
  delay after the first line's capture time (E1); every other line is not delayed. The 10-minute allowance of live records
  ([Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Live data and backfill*) keeps such records live.
- Memory: the held state is bounded independently of the input (E4); the agent's 24 MiB log collector bound and the backend's
  ingest bounds stay.
- Security: classification is a reading of text, not a trust boundary (the import's *Forged lines* limitations hold live); the
  agent gains no right and starts no program (records 0019, 0030); no new text reaches a display unescaped.
- Compatibility: wire format version unchanged (`log_line.event` exists since 0088); no storage migration of stored records; no new
  configuration option; the meaning of `min_priority` and drop rules unchanged (E3).
- Logs: no log line per classified line.

## Acceptance criteria

- [ ] AC1: A classification fixture is shipped live from the journal and from a configured `syslog` file. The fixture is
  valid UTF-8, its syslog file is read in one zone, and its journal part has one sequence-number ID. Every line that has an import
  record gives a live record equal to the import's record of the same input in every field except `log`, origin and record
  identity. Its raw and error log lines give today's live record unchanged. The fixture covers:
  - Postfix and Dovecot markers: both Dovecot positions, the database rules, and a line without a marker;
  - MariaDB: a crash report with stack lines, a recovery start and end, a `mariadb.start`, and an entry ended by the 16,384-byte
    bound;
  - a kernel OOM report with an interleaved line of another program;
  - plain lines, error log lines and raw lines.
  (S1, S2, S3, S4, S5, S6, E5, E7)
- [ ] AC2: On the live records of the fixture the detector finds `postfix_fatal`, `dovecot_fatal`, `mariadb_crash` (with
  `signal`), `mariadb_recovery_start` and `mariadb_recovery_end` events with the same fields as on the import's records (S1, S2,
  S4, S5).
- [ ] AC3: An entry and a kernel report whose lines are read in separate collector runs and arrive in separate batches are stored
  as the same one record as when read in one run. A line read again after a dropped run joins only once (S7, E9).
- [ ] AC4: The cases of E1, on an injected clock:
  - an entry with no further line, its source read to its end, is stored once the clock is more than 60 seconds past its first
    line's capture time, and not before;
  - in a backlog, a continuation line captured 50 seconds after the header but read 90 seconds later joins the entry;
  - a line of the source captured 61 seconds after the header closes the entry, even if it is read at once;
  - a header dated after the start of the run that reads it closes 60 seconds after that run's start, by the clock.
  (E1)
- [ ] AC5: An entry or report is open, and one of these ends happens:
  - a clean agent stop and start: every line of it is stored exactly once;
  - an agent killed without a flush after later lines of the source were sent: every line of it is stored at least once, and
    the repeated lines are only lines of that source captured from the entry's first line on;
  - a backend restart, or a backend killed after it acknowledged a batch with lines of the open entry: every acknowledged line
    is stored.
  (E2)
- [ ] AC6: `min_priority` and drop rules decide on the mapped line before classification, and a filtered line is invisible to the
  stages. Tests show:
  - a Postfix `warning:` line with transport priority `info` dropped under `min_priority: notice`;
  - a MariaDB journal line dropped under `min_priority: warning`;
  - a MariaDB header removed by a drop rule ends no open entry, so its continuation lines join the previous open entry of their
    key within 60 seconds.
  The configuration area and the example file name these consequences (E3).
- [ ] AC7: A flood of forged `mariadbd` and `kernel` lines keeps at most one open entry and one open report per source within the
  import's bounds. Lines of other programs are stored without waiting for the open entry: their records have a lower record
  identity than the entry's (E4, S8).
- [ ] AC8: Lines of the journal and of a configured file are never joined into one record. A `mariadbd` header and a kernel OOM
  start line in a user journal file stay single lines with the mail classification only, also when the user file shares its ID
  with a system file, and do not end an open entry of the system files (E8).
- [ ] AC9: A batch of the previous agent version is accepted by the new backend and a batch of the new agent by the previous
  backend's decoder; records stored before the change are not altered (E6).
- [ ] AC10: A gap the logs collector records in a source ends that source's open entry and report; the next line does not join
  them (E10).

## Out of scope

- Live shipping and classification of MariaDB's and Dovecot's own log files (import-only, record 0121).
- Classifying records stored before this change, and deduplicating live and imported copies of a line (log-import *Duplicates*).
- Changing what the filter judges (E3); alerting on events (#56, #61); the agent's `kernel_event` collector.

## Open questions

- Where does classification run? Answered: the plan decides it and records the decision (issue). This spec works for either
  location.
- Do the grouping stages apply live? Answered: yes, all three. The issue names them and requires equality with the import.
- Does the filter see the classified priority? Answered: no. Keeping the filter as it is keeps every existing configuration's
  meaning. MariaDB journal lines are already dropped today under a stricter `min_priority` (log-import *Lines from the journal and
  syslog*).
- What replaces "normal end of input" live? Answered: E1.
- Do the issue's claims about today's behavior hold? Answered: yes. Live records have no `event` (agent area *Log shipping*;
  wire-format `log_line`), and the five signatures need one (`signatures.yaml`). #37 is closed.

## Area documents

- [Agent](../../docs/areas/agent.md), *Log shipping*: the stages for live lines, the held state and when it closes, stop, crash
  and version mix, the journal order of E8. The residual "no classification" is removed.
- [Log import](../../docs/areas/log-import.md), *Duplicates* and *System log parsers*: live copies are classified alike.
- [Wire format](../../docs/areas/wire-format.md), `log_line`: whether the agent's `event` is still empty.
- [Configuration and secrets](../../docs/areas/configuration-and-secrets.md), `log_shipping`: the consequences of E3.
- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Log signatures*: event signatures match live lines.

## Challenge

Devil's Advocate, spec challenge. Every objection was accepted.

- Major 1, an unclean end. Accepted. E2 now says no line is lost silently, on either side. The bound on repeated lines is the open
  entry's source from its first line on, plus today's 12 seconds. I chose repetition over a gap, because a gap would drop
  crash-report lines the journal still holds. AC5 adds an agent kill without a flush and a backend kill after an acknowledgement.
- Minor 1, read time or capture time. Accepted. The bound is now capture time, plus a clock-based close once the source is
  caught up (E1). AC4 adds the backlog case.
- Minor 2, filtered lines and the grouping. Accepted. A filtered line is invisible to the stages, which works in either
  location. E3 and AC6 cover a dropped header.
- Minor 3, journal order per ID. Accepted, and changed again when the plan was written. In the spec revision every ID was grouped
  in shipping order. While planning (mode `plan`), two problems showed:
  - Replaying held lines after a restart is deterministic only within one ID.
  - One grouping state per user journal ID would make memory grow with the number of web-space users.

  E8 and AC8 now give each system-file ID its own grouping state and give user journal entries the mail classification only.
  This also takes away the forgery that the revision accepted. The AC1 fixture uses one system ID. After the plan challenge, a user
  file is decided by the file it comes from, not by its ID.
- Minor 4, lines without an equal import record. Accepted. AC1 compares only lines that have an import record; raw and error
  log lines must equal today's live record. The fixture is valid UTF-8 and read in one zone.
- Minor 5, dropped runs and gaps. Accepted. E9 with AC3: a re-read line never joins twice. E10 with AC10: a gap ends the open
  entry.
- Minor 6, an order that cannot be observed. Accepted. S8 and AC7 now use the record identity, not the order a view shows.
