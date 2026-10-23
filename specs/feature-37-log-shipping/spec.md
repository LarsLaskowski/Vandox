# Spec: Continuous log shipping in the agent

Source: Issue #37
Status: Approved by Product Manager (scope of the file formats: option A)

## Problem

The operator of the monitored server learns about an outage today only from the logs he copies to the backend host by
hand and imports with `vandoxd import`. `vandox-agent` already runs on the server and records metrics, snapshots, service
states and kernel events, but no log line: the journal's system, mail, MariaDB and systemd lines and the log files written
next to it stay on the server, and with the server's 2 GB of RAM a burst of lines must not cost the server memory. The
backend's analysis (log signatures, templates, observed hours, incidents, the forensics report) works from stored log
lines, so without a live feed it cannot explain an outage of today until someone imports the logs.

What happens today, checked against the code:

- The agent has no log collector. The only journal reader is the kernel event collector (`internal/kernevents`,
  `internal/journal`): it reads only the entries of the kernel transport and of journald's missed-message entry, keeps
  only four items per entry, lists only the system journal files (`internal/journal/dir.go`, `isJournalName`: no
  `user-<UID>.journal`), and decompresses nothing (record 0119), so an entry whose `MESSAGE` journald stored
  compressed is unreadable to it. The server's journal is written with `COMPRESSED-ZSTD`, and journald compresses every
  field whose `NAME=value` payload is at least 512 bytes (record 0119, measured on a copy of the server's journal).
- The agent's state file (`internal/agentstate`, `agent-state.json` in `spool.directory`) holds the boot, the kernel
  collector's journal position and the open backup run; no log position. It is saved by the `state` job at most every 10
  seconds when something urgent changed, else every 50 seconds, and **not** when the agent stops (`internal/agent`).
- A position is committed only after the scheduler reports that the previous run's records were kept
  (`Run.PreviousLost`, Agent area *Scheduling*); the kernel collector re-reads otherwise.
- The spool (#38) and the sender (#39) do not exist: the agent checks every record and drops it. "Kept" in this spec
  means accepted by the scheduler's sink, as for every collector; the spool takes this role when it exists.
- The importer's journal and syslog mappings (Log import area, *System log parsers*) define how the backend reads the same
  lines from files; the backend's signatures match on `program`, `source` and the message as those parsers produce them.

## Scenarios

- S1: Given the agent runs with the default configuration, when a service writes a journal entry of priority `info` or
  more severe, then within about 2 seconds the agent produces a `log_line` record with `log` and `source` `journal`, the
  capture time from the entry's `__REALTIME_TIMESTAMP`, `host` from `_HOSTNAME`, `program` from `SYSLOG_IDENTIFIER` (else
  `_COMM`), `pid` from `_PID` (else `SYSLOG_PID`, else 0), `priority` from `PRIORITY`, `message` from `MESSAGE` and an
  empty `event` — the same fields the journal mapping of `vandoxd import` produces for that entry from a `journalctl -o
  export` file, before the import's mail, MariaDB and kernel report stages. The journal read is the one `journalctl` shows
  as root: the persistent directory `/var/log/journal/<machine-id>`, else the volatile `/run/log/journal/<machine-id>`
  (the first that can be listed, as for the kernel collector), with the system files (`system.journal`, `system@*.journal`,
  `system@*.journal~`) **and** the user files (`user-<UID>.journal`, `user-<UID>@*.journal`, `user-<UID>@*.journal~`), into
  which journald's default `SplitMode=uid` puts the entries of processes of users with a UID of 1000 or more (on Plesk the
  web-space users: their cron jobs, `logger` calls and PHP command-line runs).
- S2: Given `log_shipping.files` names `/var/log/mail.log` with `format: syslog`, when Postfix appends the line
  `Oct 23 07:13:02 web-1 postfix/smtpd[4711]: connect from unknown[192.0.2.10]`, then the agent produces a `log_line`
  record with `log` `/var/log/mail.log`, `source` `syslog`, `host` `web-1`, `program` `postfix/smtpd`, `pid` 4711, no
  priority, the message `connect from unknown[192.0.2.10]` and the capture time 07:13:02 of that day in the server's time
  zone, converted to UTC. The server's time zone is the one `/etc/localtime` describes (the time zone file systemd and the
  C library use; the agent's unit sets no `TZ`).
- S3: Given `log_shipping.min_priority: warning`, when the journal receives an entry of priority `info` and one of
  priority `err`, then only the `err` entry is shipped; given a drop rule `{program: dovecot, message: '^imap-login: Login:
  '}`, a Dovecot login line is not shipped while every other Dovecot line is.
- S4: Given the agent shipped lines up to some point, when it is stopped with SIGTERM (for example `systemctl restart
  vandox-agent`) while services keep logging and is started again, then the first records after the start continue with the
  first journal entry and the first file line after the last one shipped before the stop: none is missing, none is
  shipped twice.
- S5: Given a configured file is rotated by renaming (logrotate `create`: `mail.log` becomes `mail.log.1`, a new
  `mail.log` is created), when the writer still appends lines to the renamed file before it reopens (rsyslog reopens only
  at the `HUP` after all renames), then the agent keeps reading the renamed file in every run until the writer has moved
  (the new file has data and the renamed one did not grow during a whole run), ships each run's lines of the renamed file
  before those of the new file, and then reads only the new file; no line is lost or shipped twice, the renamed file's
  position survives a restart, and the records keep `log` `/var/log/mail.log`.
- S6: Given a configured file is truncated in place (logrotate `copytruncate`), when the writer goes on writing, then the
  agent notices the truncation and reads the file again from its start, and records one gap (collector `logs`, cause
  `unknown`) from the capture time of the last line it shipped from the file to the start of the run that noticed the
  truncation, the span in which lines may have been written and truncated before the agent read them.
- S7: Given the agent starts for the first time (or its state file is not usable), when it begins shipping, then it starts
  at the current end of the journal and of each configured file and ships only lines written from then on; older lines are
  imported with `vandoxd import`.
- S8: Given a configured file did not exist when the agent started, when it is created later, then the agent ships it from
  its first line.
- S9: Given a journal entry whose `MESSAGE` (or another field the mapping reads) journald stored zstd-compressed (a field
  of about 500 bytes or more; the server's journal is written with `COMPRESSED-ZSTD`), when it passes the filter, then it
  is shipped with its message like any other entry, cut to 16,384 bytes with `truncated` set when longer. Only zstd is
  decoded; an item compressed with XZ or LZ4 makes its entry unreadable (E12).
- S10: Given the agent could not keep a run's records (the run timed out, was skipped, or the sink refused them), when the
  next run starts, then it reads again from the last committed position, so the lines return, as long as the journal or the
  file still holds them — up to the give-up rule of E19.

## Errors and edge cases

- E1: The agent ends uncleanly (SIGKILL, a crash, a power loss) — the lines read after the last saved position are shipped
  again after the start: duplicates of at most one state interval plus one collector interval of shipping (10 + 2
  seconds: a run's position is committed at the start of the next run, and the `state` job saves on its own 10-second
  grid), never a lost line. Nothing tells the user
  which records are duplicates (the backend stores them as new records, as for an import of overlapping files).
- E2: A configured file was rotated while the agent was stopped, not readable, or between two runs — the agent looks for
  the file it was reading (same device and inode) among the uncompressed regular files of the same directory whose name is
  the configured name plus `.1` or `-YYYYMMDD`; when found, it ships the rest of it first, then the current file from its
  start. When it is not found (deleted, compressed, renamed otherwise), one gap with collector `logs` and cause `unknown`
  from the capture time of the last line shipped from that file (else the committed position's time) to the capture time
  of the first line read from the current file (else the run's start).
- E3: The journal no longer holds the position (journald vacuumed or rotated away entries the agent had not read, or the
  saved journal is gone) — one gap from the position's time to the next entry read (else the run's start); shipping goes on
  there.
- E4: The journal cannot be read (no machine ID, no journal directory, not listable) or a configured file is missing,
  not readable (permissions) or not a regular file (a directory, a FIFO, a device) — the agent logs one warning per change
  (`journal not readable` with a fixed reason, or `log file not readable` with the file's index in `log_shipping.files` and
  a fixed reason, never the path) and `journal readable again` / `log file readable again` once it reads again; the other
  sources go on. No gap is recorded while the position is kept, because the lines are still there; when the file returns
  with another inode, E2 applies.
- E5: A configured path is, or is replaced by, a symbolic link (in its last or any other element) — the agent does not
  follow it and treats the file as not readable (E4), so a service user who replaces its log file with a link to another
  file (for example `/etc/shadow`, readable to the agent) never gets that file shipped.
- E6: A line longer than 16 KiB — the first 16,384 bytes are shipped at a character boundary with `truncated` set; the rest
  of the line is discarded without reading it into memory. A `host` or `program` longer than 1,024 bytes (a journal
  `_HOSTNAME` or `SYSLOG_IDENTIFIER`, which the sender chooses) is cut to 1,024 bytes at a character boundary with
  `truncated` set, as the import does; lengths count UTF-8 bytes after each invalid byte became U+FFFD, the way the wire
  check measures them.
- E7: The last line of a file has no line feed yet (the writer is in the middle of it) — it is not shipped until its line
  feed arrives; when the file is truncated, or rotated and the writer has moved (S5), before that, it is shipped as it is.
- E8: A line or journal field with invalid UTF-8 — each invalid byte becomes U+FFFD, as in the import; control characters
  and format characters (for example U+202E) are shipped unchanged as text for whoever displays them to escape.
- E9: A line of a `syslog` file that has no syslog header (an RFC 5424 line, a continuation, other text) — shipped with the
  whole line as the message, empty `host` and `program`, `pid` 0, no priority and the capture time of the previous line of
  that file (else the time it was read); an empty line is not shipped.
- E10: A year-less syslog time — local time in the time zone of `/etc/localtime`; the year is the current year, or the year before
  when the line would lie more than a day in the future. A time in the hour skipped at the start of daylight saving time is
  shifted forward by the gap; a time in the repeated hour takes the earlier offset unless that lies more than ten minutes
  before the previous line of the file.
- E11: A flood of lines (a busy service, or any local user writing to the journal with `logger`, which lands in the system
  journal or, for a UID of 1000 or more, in that user's journal; both are shipped) — a run ends when it has produced 1,000
  records, or records with 4 MiB of text (the UTF-8 bytes of `log`, `host`, `program` and `message`; the record that crosses
  the budget is the last), or after one second of reading, whichever comes first. The 1,000 and the 4 MiB count shipped
  records; entries and lines the filter drops count only against the second of reading, so a flood of filtered lines delays
  wanted ones but never stops them. The rest is read by the next runs (a backlog), so the agent's memory does not grow. When
  the flood lasts until the journal or a rotation removes unread lines, E2 or E3 records the gap.
- E12: A journal entry without `MESSAGE`, or with an invalid `__REALTIME_TIMESTAMP` — not shipped and no gap (there is no
  line). An entry that the reader cannot read (a damaged or torn object, an item compressed with XZ or LZ4, a zstd item
  that does not decompress or needs more than the decoder's memory bound) — one gap over that entry (from the previous
  entry read to the next one), and reading goes on; several in a row give one gap.
- E13: A journal file in a layout the reader does not support (`COMPACT`, systemd 252 and later; no keyed hash) — its
  entries are lost to a recorded gap, as for the kernel collector, and the warning names the reason.
- E14: A run that hangs (a file system read that blocks) — the scheduler abandons it at its deadline and records the
  missed sample as a gap; the positions do not move, so the next run reads the same lines again (S10).
- E15: The configuration names `/var/log/syslog` (or another rsyslog file) while the journal is shipped too — every line is
  shipped twice, with sources `journal` and `syslog`; the agent does not deduplicate across sources (as the import does
  not). The example file says so.
- E16: An invalid `log_shipping` option (an unknown priority name, a relative or unclean path, a path listed twice, more
  than 16 files, a format other than `syslog`, a drop rule without a condition or with a pattern that does not compile, more
  than 64 rules, a pattern over 1,024 bytes) — the agent does not start; the error names the file, the line and the key with
  its index and a fixed reason, never the value.
- E17: A file is removed from `log_shipping.files` or added to it between two starts — a removed file's position is
  forgotten; an added file that exists starts at its current end (S7), one that does not exist yet is read from its start
  when it appears (S8).
- E18: The agent's own log lines reach the journal — they are shipped like any other entry; the agent logs only per change,
  never per shipped line, so shipping causes no loop.
- E19: A record that the scheduler's check would refuse, or runs that keep being dropped from the same position — the
  collector checks every record with the scheduler's check before it returns it and puts a gap over that line in its place
  (from the previous line of the same source, else the committed position's time, to the line's time), so a hostile line
  never makes a run be dropped or a span be read again. When runs are still dropped (timeouts, skips, sink refusals), the
  fourth dropped run in a row from the same committed position **gives up** the span from that position to where the last
  dropped run got, as the kernel collector does: one gap from the committed position's time to the capture time of the last
  line that run read (else the run's start), reading goes on after the span, and the span's lines are not shipped. Trade-off,
  as for the kernel collector: a sink refusal lasting more than four runs (8 seconds; later a full spool, #38) gives up lines
  the journal or the file still holds.
- E20: `/etc/localtime` cannot be read or is not a valid time zone file — the agent never falls back to UTC silently: it
  logs one warning `time zone not readable` (and `time zone readable again` once it is), reads no `syslog` file while the
  zone is unknown (their positions stay, so no line is lost and no gap is recorded) and tries again at every run; the
  journal, whose times are absolute, is shipped as usual.
- E21: The state file was written by a version without log positions (the current one) — it stays usable: the boot, the
  kernel collector's position and the open backup run are kept, and shipping starts at the current ends as on a first start
  (S7). A state file with log positions is read by no released agent (the state file and the agent are unreleased), so no
  downgrade path exists to keep; a later released change of the format follows the state file's version rule.
- E22: A journal file of a seqnum ID the agent has no position for (journald started a new ID, a new user journal, an archived
  or set-aside file of an ID whose position was forgotten, or the journal was off or unreadable until now) — it is read from
  its start only when its first entry is newer than the newest journal position the agent holds; otherwise the ID starts at its
  end. So no file is ever shipped a second time, and turning `log_shipping.journal` on, or a journal that becomes readable
  after a first start, starts at the ends as S7 does. Positions of up to 256 IDs with an online file are saved; a further ID is
  read but its position is not saved, so after a restart it starts at its end with one gap from the newest saved journal
  position's time to the run's start — never a re-read.

## Non-functional requirements

- **Memory**: the collector holds at most 24 MiB live, independent of the size of the journal, of the files, of the
  backlog, of an entry and of a line. Worked figure: the records of one run, at most 4 MiB of text plus the one record that
  crosses the budget (at most 16,384 + 3 x 1,024 bytes) and the fixed part of at most 1,000 records (well under 1 MiB); the
  journal reader's buffers and file headers, at most 4 MiB as for the kernel collector (at most 1,024 headers of up to 4 KiB);
  the zstd path, at most 8.5 MiB (one output buffer of 4 MiB + 128 KiB + 16 bytes and one input buffer of at most 4 MiB,
  each allocated once and reused; a frame or compressed object beyond 4 MiB is E12 without being read), with a decompressed
  item kept only up to its field's bound (16,384 bytes for `MESSAGE`, 1,024 for the short fields); the file reader's line
  buffer and positions, under 1 MiB; together about 18.5 MiB, leaving room for the scheduler's check of one record at a time.
  A test asserts the bound with a run of records at the maximum of every field (so the byte budget ends the run after about
  210 records), a journal of 100,000 entries and a file of lines over 16 KiB.
- **Throughput and delay**: without a backlog a line is in the sink within one collector interval (2 seconds) plus the run
  time; sustained at least 500 lines per second of ordinary length (1,000 records per 2-second run, fewer when the lines are
  long enough to reach the 4 MiB budget); more becomes a backlog (E11). The real volume of the server is measured by the
  footprint test (#44).
- **Hang**: the collector runs under the scheduler with a deadline (record 0029); a hung source loses only its sample and
  never moves a position.
- **Durability**: positions are committed when the scheduler reports a run kept (a timed-out or skipped run never commits); they are
  saved at most 10 seconds (one state interval) after they are committed and once more when the agent stops after its last kept run; an
  abandoned run never moves a saved position. After an unclean end the saved position is at most 10 + 2 seconds behind
  the last kept run (E1).
- **State file**: the positions live in the agent's state file with its existing rules (security area 9: mode 0600, written
  by rename, read only as a regular file of the agent's user, decoded strictly); the log positions are an optional part, so
  a file without them stays valid (E21). Paths are stored as their SHA-256; the size bound is raised from 16 KiB to 64 KiB,
  which holds 16 file positions (each with a renamed file still being read, S5) and 256 journal positions (E22).
- **Security**: the journal and the files are external input (security area 10): every read is bounded before parsing, a
  hostile line or entry yields a cut, replaced or unshipped line or a gap, never a crash, a hang or an unbounded allocation;
  decompression is bounded before it starts (only frames that state their size, at most 4 MiB, and at most 4 compressed items
  decoded per entry; no decompression bomb). Drop rule patterns run in linear time (no backtracking), and a pattern whose
  compiled program is large is refused at start-up, so one line costs a bounded amount of matching. No path is followed through a symbolic link (E5). Log lines are never written to the agent's own log.
- **Rights**: no new group, capability or unit setting: the journal is read as a member of `systemd-journal` and the files
  of `/var/log` as a member of `adm` (Agent area, *Rights*); a file readable only through `CAP_DAC_READ_SEARCH` is read
  through it, which `deploy/agent/README.md` states.
- **Compatibility**: no change to the wire format: `log_line` records as wire version 1.1 defines them, covered by the
  golden batches in `testdata/wire`. Existing configuration files stay valid: every new option has a default.
- **New configuration** (section `log_shipping`, next to `log`):
  - `log_shipping.journal` — `true` (default) or `false`: whether the journal is shipped.
  - `log_shipping.min_priority` — `emerg`, `alert`, `crit`, `err`, `warning`, `notice`, `info` (default) or `debug`: a
    line with a known priority less severe than this is not shipped; a line without a priority is never dropped by it.
  - `log_shipping.files` — default `[]`; at most 16 entries, each `{path, format}`: `path` an absolute, clean path of at
    most 1,024 bytes, listed once; `format` `syslog`.
  - `log_shipping.drop` — default `[]`; at most 64 rules, each with at least one of `log` (`journal` or a configured path),
    `program` (compared whole, literally), `priority` (matches a line of that priority or less severe; a line without
    priority never matches) and `message` (an RE2 regular expression in Go syntax, at most 1,024 bytes, searched anywhere in
    the message unless anchored); a line matching every condition of any rule is not shipped.
- **Logs**: fixed texts only, one line per change (E4, E13, E20); never a path, a field value or a message. Gaps carry collector
  `logs` and cause `unknown`.

## Acceptance criteria

- [ ] AC1: a journal entry that passes the filter, from a system or a user journal file of the persistent or (without it)
  the volatile directory, becomes a `log_line` record whose `log`, `source`, capture time, `host`, `program`, `pid`,
  `priority`, `message` and `truncated` equal what the journal mapping of `docs/areas/log-import.md` produces for the same
  entry, before the mail, MariaDB and kernel report stages — also for an entry whose `MESSAGE` is stored zstd-compressed,
  one longer than 16,384 bytes and one whose `_HOSTNAME` or `SYSLOG_IDENTIFIER` is longer than 1,024 bytes; an entry with
  an XZ- or LZ4-compressed item gives the gap of E12 (S1, S9, E6, E8).
- [ ] AC2: a line of a `syslog` file becomes a `log_line` record with `log` the configured path, `source` `syslog` and the
  header fields of the import's syslog mapping, its year-less time resolved in the server's time zone by the rules of E10;
  a line without a syslog header is shipped raw as E9 says, an empty line not at all; while `/etc/localtime` is not
  readable no `syslog` file is read, one warning is logged, the positions stay and no time falls back to UTC (S2, E6, E8,
  E9, E10, E20).
- [ ] AC3: `log_shipping.min_priority` drops exactly the lines with a known priority less severe than it, and a drop rule
  drops exactly the lines that match all of its conditions; a line without a priority passes the priority filter (S3).
- [ ] AC4: after a stop with SIGTERM and a new start, with lines written to the journal and to a configured file before,
  during and after the stop, the shipped records hold every line exactly once and in source order (S4).
- [ ] AC5: after an unclean end (the state saved, then more lines read and kept, then no save), the start ships every line
  after the saved position again and none before it, and the saved position is at most one state interval plus one
  collector interval (10 + 2 seconds) behind the last kept run (E1).
- [ ] AC6: a rotation by rename while the agent runs ships every line of the renamed file, also those appended after a run
  that already saw the new file, before the new file's lines, each line once, also across a restart while the renamed file is
  still being read; a rotation while the agent is stopped or the file is unreadable is followed the same way when the renamed
  file is found under the configured name plus `.1` or `-YYYYMMDD`, and records the gap of E2 when it is not (S5, E2).
- [ ] AC7: a truncation in place is detected, also when the file has grown past the old position again by the next run,
  and reading restarts at the file's start with the one gap of S6 (S6).
- [ ] AC8: on a first start, with no usable state, the agent ships no line written before it started; a file added to the
  configuration starts at its end, a file that appears later is shipped from its first line (S7, S8, E17).
- [ ] AC9: after a run whose records were not kept (timeout, skip, sink refusal) the next run ships the same lines again, and
  an abandoned run never moves a committed or saved position (S10, E14).
- [ ] AC16: a line whose record the scheduler's check would refuse is replaced by the collector with the gap of E19, so the
  run is kept and the lines after it are shipped; and the fourth dropped run in a row from the same committed position
  gives up the span with the one gap of E19 and the next run reads on after it (E19).
- [ ] AC17: a state file without log positions keeps its boot, kernel position and open backup run and starts shipping at
  the current ends; a state file with 16 file positions (each with a renamed file) and 256 journal positions is saved within
  64 KiB and read back intact (E21).
- [ ] AC18: a file of an ID without a position is shipped from its start only when its first entry is newer than the newest
  held journal position; an archived or set-aside file of a forgotten ID, the journal turned on later and a journal readable
  only after a first start ship nothing older; with more than 256 IDs with an online file no file is read twice and a
  restart gives each unsaved ID one gap (E22).
- [ ] AC10: lines the journal no longer holds at the position, and an unreadable entry, each give the one gap of E3 or E12,
  and an entry without `MESSAGE` gives neither a record nor a gap (E3, E12).
- [ ] AC11: a missing, unreadable or non-regular configured file, a path with a symbolic link in any element, and an
  unreadable journal produce no record and no gap, log one warning per change without the path, keep the position, and
  continue there once readable (E4, E5, E13).
- [ ] AC12: a partial last line is held back until its line feed arrives and is shipped as it is at a truncation or when
  the writer has moved after a rotation (E7).
- [ ] AC13: a run ends at 1,000 shipped records, at the record that crosses 4 MiB of text or after one second of reading,
  whichever comes first; filtered entries count only against the second, so wanted lines behind a flood of filtered ones
  are shipped in later runs; and the collector stays within 24 MiB live with records at the maximum of every field, a
  journal of 100,000 entries and a file of lines over 16 KiB (E11).
- [ ] AC14: every invalid `log_shipping` option of E16 stops the agent at start-up with the file, line, key and index and
  without the value, and the example `deploy/agent/agent.yaml` sets every new option at its default (E16).
- [ ] AC15: the agent's own warnings and info lines of the collector are the fixed texts of E4, E13 and E20 and never
  contain a path, a field value or a message (E4, E13, E18, E20).

## Out of scope

- E15: shipping the journal and an rsyslog copy of it twice is the operator's choice; no deduplication across sources.
- Event classification (`event`, the Postfix and Dovecot severity markers that the import's mail classifier applies), the
  grouping of MariaDB entries and of multi-line kernel reports: the agent ships each line as it is (the issue names the
  fields of the minimal parsing; `event` is not among them). Live data therefore carries no `mariadb.*`, `postfix.*` or
  `dovecot.*` events, so the signatures `mariadb_crash`, `mariadb_recovery_start`, `mariadb_recovery_end`,
  `postfix_fatal` and `dovecot_fatal` do not fire on it, and mail lines keep their transport priority instead of the
  marker's, until the follow-up issue #290 (classifying live log lines) is done.
- Formats other than the journal and `syslog` files (the Plesk panel, nginx, `sw-cp-server`, Apache and MariaDB error logs,
  Dovecot's own log file) — the Product Manager chose option A (first open question); the Plesk panel, nginx,
  `sw-cp-server` and Apache error logs follow in #289.
- Reading compressed rotated files (`.gz`) and files of other directories than the configured file's after a rotation.
- Storing and sending the records: the spool (#38), the sender (#39) and the ingest API (#40).
- Shipping history from before the first start: `vandoxd import`.
- Counting filtered lines as metrics.

## Open questions

- Which log files besides the journal does the agent read in this feature, and in which formats? The issue says
  "configured log files" without formats. On Ubuntu 22.04 rsyslog's files (`syslog`, `mail.log`, `kern.log`) are copies
  of the journal, so the logs that only exist as files are the web server and Plesk error logs (and the MariaDB error log
  and Dovecot's log file where they are written to files). The backend's signatures `php_too_many_connections`,
  `php_disk_full` and `nginx_disk_full` match only lines with the sources `nginx-error`, `sw-cp-server-error` and
  `apache-error` in the message form the import's error log parser produces. Options: (A) the journal and `syslog`-format
  files only, the error log formats in a follow-up issue (recommended: it keeps this change to the journal, positions,
  rotation and filters, and `format` is an option so later formats add to it); (B) also the four error logs of the import's
  *Plesk and web server error logs* family, with records equal to the import's for the same line; (C) also a `plain` format
  that ships every line whole with the time it was read, so error logs arrive now but the web signatures do not match them.
  Answered: option A, by the Product Manager — the journal and `syslog`-format files only; the error log formats
  (Plesk panel, nginx, `sw-cp-server`, Apache) follow in #289.
- Does "after an agent restart, without loss or duplicates" include a crash or a power loss? Answered: a restart is a stop
  and a start of the service (S4, AC4), which must be exact. After an unclean end the agent cannot know which read lines
  reached the sink after its last save, so it re-reads them: duplicates bounded to 10 + 2 seconds, never a loss (E1, AC5). The
  guarantee *No data gaps unless explicitly recorded* (`.squad/project.md`) puts loss above duplicates; exactness across a
  crash needs the position to be written with the records, which the spool (#38) can take over.
- Does the agent ship the history it finds at the first start? Answered: no (S7). History is imported with `vandoxd
  import` (record 0014, Log import area), and shipping a journal of weeks at the first start would flood the spool.
- Must compressed journal fields be read, against record 0119's "no decompression"? Answered: yes (S9). Record 0119 rejected
  decompression for the kernel collector because no kill line comes near the 512-byte threshold; shipping every line needs
  the messages above it. Only zstd, the algorithm the server's journal uses; XZ and LZ4 items are a gap (E12). How (a
  library or a decoder of its own, and whether 0119 is superseded or extended) is the plan's.
- Which default filter? Answered: `min_priority: info` and no drop rule. The signatures `service_start` and `service_stop`
  match systemd's `Started …` and `Stopped …` lines, which systemd logs at `info`, and the forensics report counts an hour
  as observed only when a line of source `journal` or `syslog` is stored in it (Detection area, *Observed hours*); a stricter
  default would make quiet hours unknown and lose those events. The example file shows drop rules for routine noise.
- Do lines without a priority (traditional rsyslog files) pass `min_priority`? Answered: yes (AC3); the import stores them
  without a priority as well, and dropping them would drop every line of such a file.
- Does the agent need a new right? Answered: no. `adm` and `systemd-journal` are already the agent's groups (Agent area,
  *Rights*, record 0030).

## Area documents

- `docs/areas/agent.md` — a new section *Log shipping*: the `logs` collector (interval, timeout, place in the job list), what
  it reads (system and user journal files, the time zone from `/etc/localtime`) and how the journal entries and syslog
  lines map to `log_line`, the filter, positions and when they are committed and saved, rotation and truncation, the
  record check and the give-up rule, the gaps, the budgets of a run, memory, logging and residuals (duplicates after an
  unclean end, a give-up during a long sink refusal, forged lines, unsupported journal layouts and compressions); *Agent
  state* gains the log positions and the new size bound; *Rights* names the reads;
  *Status* and *Implementation* name the new packages.
- `docs/areas/configuration-and-secrets.md` — the `log_shipping` options with defaults, limits and error texts.
- `docs/areas/wire-format.md` — under `log_line`, that the agent's records use `log` `journal` or the absolute path and the
  sources `journal` and `syslog`, as the import does.
- `docs/areas/log-import.md` — under *Duplicates*, that shipped and imported lines of the same period are stored twice.

## Challenge

- M1 (memory bound against the run limit): accepted. A run now also ends at the record that crosses 4 MiB of record text
  (`log`, `host`, `program`, `message`), and the bound is 24 MiB with the worked figure under *Non-functional requirements*
  (records about 4 MiB, journal reader 4 MiB, zstd decoder 8 MiB, file reader under 1 MiB: about 18 MiB). AC13 tests both
  budgets and the bound with records at the maximum of every field.
- M2 (no progress for a line that always fails): accepted in all four parts. (a) E6 cuts `host` and `program` to 1,024 bytes
  with `truncated`, as the import does; (b) and (c) the new E19: the collector checks every record with the scheduler's
  check and puts a gap in place of one that would fail, and the fourth dropped run in a row from the same committed position
  gives up the span with one gap, the kernel collector's rule, with its trade-off stated; S10 points to it; (d) AC1 covers the
  long `_HOSTNAME` and `SYSLOG_IDENTIFIER`, the new AC16 covers the record check and the give-up.
- Minor 1 (which journal files): accepted. S1 now names the persistent directory, else the volatile one, and the system and
  the user journal files (`user-<UID>.journal` and its archived and set-aside forms), so the agent reads what `journalctl -o
  export` as root gives the import; E11's `logger` example and AC1 say so. The *Problem* section records that today's reader
  lists only system files.
- Minor 2 (live classification not tracked): accepted. *Out of scope* names the five signatures that match only on events
  and the replaced priority of mail lines, and points to the follow-up issue #290 on classifying live log lines; AC1 now compares with the journal mapping of `docs/areas/log-import.md` before the mail, MariaDB and
  kernel report stages.
- Minor 3 (10-second bound against the commit mechanism): accepted. E1, AC5, *Durability* and the open question now say at
  most one state interval plus one collector interval (10 + 2 seconds); committing when the run is kept is not required,
  because the collector learns that only at the start of its next run (`Run.PreviousLost`).
- Minor 4 (state file compatibility and size): accepted. The new E21: a state file without log positions stays usable and
  shipping starts at the current ends; no released agent reads the state file, so there is no downgrade to keep. *State
  file* raises the 16 KiB bound to what 16 paths of 1,024 bytes at the worst JSON escaping need (or a smaller encoding the
  plan chooses); AC17 tests both.
- Minor 5 (time zone source): accepted. S2 and E10 name `/etc/localtime`; the new E20: when it cannot be read, one warning
  `time zone not readable`, no `syslog` file is read while the zone is unknown (positions kept, no loss, no gap), the agent
  retries every run and never falls back to UTC; the journal is unaffected. AC2 and AC15 cover it.
- Plan challenge M1 (journal ID rules re-ship history): accepted; the new E22 and AC18 hold the time rule and the bound
  (answered in the plan's *Challenge*).
- Plan challenge M2 (rotation loses late appends to the renamed file): accepted; S5, E7, AC6, AC12 and the state file's
  bound now keep reading the renamed file until the writer has moved, across a restart too.
- Plan security review B1–B4: the memory figure and *Security* now name the zstd buffers, the frame rule and the cap of
  compressed items, and the pattern size bound.
- Minor 6 (units and compressions): accepted. E11 and AC13 now count shipped records for the 1,000 and the 4 MiB, and
  filtered entries only against the second of reading, so a flood of filtered lines delays wanted ones but never stops them.
  S9, E12 and the open question say that only zstd is decoded and that an XZ or LZ4 item makes its entry a gap.
