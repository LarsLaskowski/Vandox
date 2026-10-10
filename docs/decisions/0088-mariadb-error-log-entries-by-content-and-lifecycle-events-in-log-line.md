# 0088: MariaDB error log: entries detected by content and kept with their continuation lines, lifecycle events in an optional log_line field

- **Status:** Proposed
- **Date:** 2026-10-10
- **Area:** Log import
- **Source:** Issue #17
- **Supersedes:** —

## Context

Issue #17 asks for a parser of the MariaDB error log that classifies start, normal shutdown, abort by signal and crash
recovery start/end, recognizes warnings and errors, and keeps multi-line stack traces as one record. MariaDB was the most
frequent victim of the OOM killer on the monitored server; a SIGKILL leaves no line of its own, so the restart, the crash
recovery and the time until the server is ready again are the evidence in this log. Signature detection (#21) and outage
reconstruction (#23) build on the stored records.

What the format is, from the MariaDB source (10.3.39, 10.6.12, 10.6.22, 10.6.28, 10.11.9; Ubuntu 22.04 ships the 10.6
series, the version on the server was not checked): a server line is `YYYY-MM-DD HH:MM:SS <thread> [ERROR|Warning|Note] <message>` with a space-padded hour
(`sql/log.cc`, `print_buffer_to_file`); the fatal signal handler writes `YYMMDD HH:MM:SS [ERROR] <program> got signal N ;`
in the older two-digit form and then a crash report of dozens of lines without a time stamp, including the stack trace and
the query of the crashing connection, written raw up to 64 KiB (`sql/signal_handler.cc`, `my_safe_print_str`); InnoDB
assertions are prefixed with a hexadecimal thread ID and no level; messages with a line break (`ready for connections.` is
followed by a `Version:` line) continue on lines without a header. All times are the server's local time without a zone
(`localtime_r`). InnoDB of 10.6 writes no "recovery finished" line; the end of its redo recovery is the
`InnoDB: <version> started; log sequence number ...` line that follows `InnoDB: Starting crash recovery ...`.

Two claims of the issue did not hold as stated. The Debian packaging of MariaDB 10.6 (`50-server.cnf`, which Ubuntu's
package follows; the Ubuntu package and the server's Plesk configuration were not checked) leaves `log_error =
/var/log/mysql/error.log` commented out and sends the error log to the journal under systemd, so the file exists only where
it was enabled. And a configurable path has no meaning for an import that reads arbitrary copies and archives; the agent's
configured log files (#37) are where a path belongs.

The `log_line` record had no field for a classification ([0063](0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md),
[0084](0084-log-line-record-gets-an-optional-host-field.md)), and the system log parsers emit log lines only
([0086](0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md)). No version of the wire format or
the schema has been released (no `v*` tag).

## Options considered

Where the classification lives:

1. **No classification; plain log lines, events left to signature detection (#21)** — no model change and the same rule as
   0086; but the issue's first acceptance criterion is not met, and #21 would re-derive from text what the parser already
   knows from the format.
2. **A new record kind (`mariadb_event`) emitted next to the line** — explicit; but every lifecycle line becomes two
   records, a new kind in both languages and the golden batch, the event cannot point to its line (records carry no ID
   before they are stored), and it anticipates the cross-source events #21 builds.
3. **An optional `event` field on `log_line`** (chosen) — the line stays one record and carries its classification; an
   additive field in Go, C#, the golden batch and a new `log_lines.event` column, like `host` in 0084, so the wire version
   stays 1.0; the agent may leave it empty; #21 can match on it instead of on message text.
4. **The classification in `program` or in the message** — no model change; overloads fields that mean something else.

What `event` may hold:

5. **A fixed list in the model** — strict; every parser that adds events changes the model in both languages.
6. **The name rule of the wire format, values defined by the producer** (chosen) — the same rule as metric names; the
   MariaDB values are listed in the area document.

How the file is recognized:

7. **By the path `mysql/error.log`, with an option for other paths (as the issue asks)** — the import reads copies and
   archives of any layout, a name says nothing about the content, and a default server does not write this file at all.
8. **By content: a line of the file head is a MariaDB entry header; the name never matters; no option** (chosen) — finds
   the log wherever it was copied (`error.log`, `<host>.err`, inside a tar), also when the head starts inside a multi-line
   entry. The header forms are specific enough that no other log of a saved `/var/log` matches (dpkg, the general and slow
   query logs, nginx, fail2ban and MySQL 8 lines were compared).

How multi-line output stays together:

9. **One record per line** — breaks "stack traces stay one record".
10. **An entry is a header line and the lines without a header after it** (chosen) — this is how MariaDB writes multi-line
    messages and crash reports.

How a long entry is cut (a crash report with a long query exceeds the 16 KiB text limit):

11. **Head and tail, as kernel reports** (0086 option 8) — the tail of a crash report (resource limits, kernel version)
    matters less than its head (signal, server version, stack trace).
12. **The head with a line naming how many lines were left out** (chosen) — memory is bounded by the kept head; the marker
    is the same as for kernel reports.

The time zone:

13. **Assume UTC** — wrong by one or two hours on a server that logs in local time, and not repairable after the import.
14. **`import.time_zone`, with the rules of year-less syslog times** (chosen) — the option has no default, a file fails with
    "import.time_zone is not set" while it is unset, the repeated hour uses the 10-minute tolerance, and dates are checked as
    integers before any value is built ([0085](0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md), options 4,
    13 and 15).

The end of crash recovery:

15. **A fixed message** — none exists for InnoDB in 10.6.
16. **The `InnoDB: ... started; log sequence number` line while a recovery started in the same file is open** (chosen) —
    deterministic in file order; a server start closes an open recovery without an end. The table crash recovery of the
    binary log has its own start and end lines, which are classified as well.

Fields that the format does not carry:

17. **Program `mariadbd`** — a guess (older servers are `mysqld`, and the crash header names whatever `argv[0]` was).
18. **Program empty for server lines, `mysqld_safe` for its lines, source type `mariadb`; pid 0, no host; priority from the
    level** (chosen) — nothing is invented; `Note` maps to 6, `Warning` to 4, `ERROR` to 3 as in syslog, lines without a
    level have none.

Forged lines:

19. **Accept and document** (chosen) — MariaDB copies client text into its log (verified for the query in a crash report,
    written raw), so a client that can crash the server, or make it log its text, can insert a line break and a fake header,
    which becomes an entry of its own with a forged time, level and event. No escaping exists in the format to tell them
    apart; as in 0086 option 19, an event is a classification of text, not proof of origin.

## Decision

Options 3, 6, 8, 10, 12, 14, 16, 18 and 19: the parser `mariadb` recognizes the error log by content, turns every entry (a
header line and its continuation lines) into one `log_line` record with its local time resolved in `import.time_zone`, keeps
the head of a long entry with an omitted-lines marker, and classifies lifecycle entries into the new optional `log_line`
field `event` (name rule, stored in `log_lines.event`); no path option is added. The rules are in
[Log import](../areas/log-import.md) (*MariaDB error log*), the field in [Wire format](../areas/wire-format.md) and
[Storage](../areas/storage.md).

## Consequences

- On a server where MariaDB logs to the journal (the packaged default under systemd), its lines arrive through the journal
  and syslog parsers as plain log lines with program `mariadbd`, without `event` and with each crash report line a record
  of its own; classifying those is a follow-up.
- `event` is stored, not indexed; a query by event needs its own change (#21, #23).
- The 16 KiB text limit stays; a crash report with a long query loses its tail and says so.
- A file whose head holds no entry header (it starts with more than 4 KiB of crash report) is listed as not recognized.
- Lines of a time zone change between an interrupted import and its resume are shifted, as for syslog files (0085).
- The agent's live shipping (#37) may send `event` or leave it empty; the backend accepts both.
- Signature detection (#21) must not treat an `event` as proof that MariaDB wrote the line (option 19).
