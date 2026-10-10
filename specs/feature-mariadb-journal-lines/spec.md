# Spec: Classify MariaDB lines from the journal and syslog

Status: Approved by Lead

Source: issue #165 (builds on #17, merged as PR #167, record 0088)

## Problem / motivation

Under systemd the packaged MariaDB writes its error log to standard error, which the journal stores line by line: the
Debian `50-server.cnf` leaves `log_error` commented out, and `sql/mysqld.cc` redirects standard error to a file only when
`log_error` is set. A default server therefore has no MariaDB error log file, and the parser of #17 imports nothing there.
MariaDB's output reaches `vandoxd import` through a journal export or a syslog file instead, where the journal and syslog
parsers store every line as a plain `log_line` of program `mariadbd`: the MariaDB header stays in the message, no line gets
an `event`, the priority is the journal's priority of a service's standard error (`info` for every line) rather than
MariaDB's level, and a crash report becomes dozens of records. The outage MariaDB was the victim of (the OOM killer, then a
restart with crash recovery) cannot be read from these records the way #17 made it readable from the error log file.

## Behavior

When a journal export or a syslog file is imported, the lines of the programs `mariadbd` and `mysqld` are read with the
MariaDB rules of record 0088. The normative text is in `docs/areas/log-import.md`, *MariaDB error log*, *Lines from the
journal and syslog*. In short:

- A line of program `mariadbd` or `mysqld` whose message begins with one of MariaDB's four entry header forms (*Entries* in
  the area document) opens an entry. The following lines of the same host, program and process ID that have no header
  and are logged within 60 seconds of the header line belong to it, as a crash report belongs to its `got signal` line.
  An entry ends at the next MariaDB header line, or at the end of the input. It also ends at a line of its host, program
  and process ID that lies more than 60 seconds from the header line, or that would make the entry longer than 16,384
  UTF-8 bytes. Such a line, and the following lines of that process until the next header, are stored as plain lines.
  Only one entry is open at a time.
- An entry becomes one record. Its message is the header's message (without the time stamp, thread and level) and the
  following lines, joined by line feeds, at most 16,384 UTF-8 bytes: the size bound of the error log. A longer entry is
  split, not cut, so no line is counted away. The error log cannot store a continuation line on its own, so there the
  same entry keeps its beginning and `[N lines omitted]`. Its priority comes from the MariaDB level (`ERROR` 3, `Warning` 4,
  `Note` 6), and its `event` comes from the event rules of the error log (start, ready, shutdown, shutdown complete, abort,
  crash recovery start and end).
- Time, host, program, process ID, `log` and source type stay those of the journal entry or syslog line. The MariaDB time
  stamp is only used to recognize the header, so a journal export still needs no `import.time_zone`.
- journald stores no entry for an empty line of standard error, so an entry from a real journal, or from a syslog file
  that journald feeds, has no empty lines: a crash report's message lacks the inner empty lines the error log keeps.
- Lines of other programs, and lines of `mariadbd` that belong to no open entry, are stored unchanged. A line written
  while an entry is open is stored before the entry's record.
- Crash recovery is tracked as in the error log, in input order. In an input with several hosts, another host's line can
  end a recovery it did not start (a documented limitation; Vandox reads one server).
- Forged lines are possible, and this is documented rather than prevented: any local process can log under the name
  `mariadbd`. In the journal its lines cannot join the server's entry, because the process ID is journald's own. In a
  syslog file it can write the server's process ID, which allows two more things, each within 60 seconds. Its lines can
  fill the server's entry, which then ends, and the server's following lines are stored as plain lines. Its own header
  can take in the server's following lines under a forged level and event. Either way every server line is stored with
  its whole text.

What does not change: which parser claims a file, the source types, the error log parser, the wire format and storage
(`event` exists since #17), and the handling of kernel reports.

## Acceptance criteria

The criteria are listed with test-level detail in [plan.md](plan.md), *Acceptance criteria* (AC1 to AC15). In summary:

- [ ] Lines of `mariadbd` and `mysqld` with a MariaDB header become entries with the header's message, MariaDB's priority
  and event, and keep the time, host, program, process ID, `log` and source type of their line (AC1, AC2).
- [ ] Other programs, `mariadbd` lines with a line feed in the message, and lines that belong to no open entry stay
  unchanged (AC3, AC4).
- [ ] A crash report is one record within the error log's 16,384-byte bound; a longer entry is split, never cut (AC5,
  AC7).
- [ ] Lines that another process injects under the server's name and process ID can split the server's entry but never
  reduce one of its lines to a count (AC16).
- [ ] Interleaved lines of other programs, hosts and processes are emitted unchanged before the entry and do not break it
  (AC6). Only one entry is open, and a line more than 60 seconds from its header does not join it (AC6).
- [ ] No time zone is needed for a journal export, and the MariaDB time stamp is never read (AC8).
- [ ] Recovery is tracked in input order as in the error log, which keeps its behavior (AC9).
- [ ] Skipped lines are not seen (AC10). The open entry is emitted only at the normal end, and the output is deterministic
  (AC11). Memory is bounded, through both stages (AC12).
- [ ] The MariaDB error log fixture gives the same messages and events through the journal, the syslog file and the error
  log parser when the input carries its empty lines; without them, as journald stores the output, the messages differ by
  the empty lines only (AC13).
- [ ] Kernel reports and MariaDB entries are grouped side by side in a fixed stage order (AC14).
- [ ] The README, the area document, record 0088 and `.squad/project.md` describe the behavior (AC15).

## Out of scope

- A JSON `null` in a string field of the C# wire decoder (#166).
- The agent's live log shipping (#37) and whether the agent fills `event`.
- Queries by `event`, signature detection and outage reconstruction (#21, #23), including how they deal with one entry
  that is stored from both the journal and syslog.
- Trusting the journal's `_TRANSPORT` or `_SYSTEMD_UNIT` to tell the real server apart from a forger (record 0088,
  option 25). That decision belongs to #21.
- MySQL 8's log format. Lines of `mysqld_safe` in syslog carry no MariaDB header and stay plain.
- Re-importing content that was imported before this change.

## Open questions

None. The time source, the program names, the process-ID key, the time bound, the recovery state and the split at the
size bound are decided in record 0088, options 20 to 38.
