# 0088: MariaDB error log: entries detected by content and kept with their continuation lines, lifecycle events in an optional log_line field; the same rules for MariaDB lines of the journal and syslog

- **Status:** Proposed
- **Date:** 2026-10-10
- **Area:** Log import
- **Source:** Issue #17, issue #165
- **Supersedes:** —

## Context

Issue #17 asks for a parser of the MariaDB error log that classifies start, normal shutdown, abort by signal and crash
recovery start/end, recognizes warnings and errors, and keeps multi-line stack traces as one record. MariaDB was the most
frequent victim of the OOM killer on the monitored server; a SIGKILL leaves no line of its own, so the restart, the crash
recovery and the time until the server is ready again are the evidence in this log. Signature detection (#21) and outage
reconstruction (#23) build on the stored records.

What the format is, from the MariaDB source (10.3.39, 10.5.22, 10.6.7, 10.6.11, 10.6.12, 10.6.22, 10.6.28, 10.11.9; Ubuntu
22.04 ships the 10.6 series, the version on the server was not checked): a server line is `YYYY-MM-DD HH:MM:SS <thread> [ERROR|Warning|Note] <message>` with a space-padded hour
(`sql/log.cc`, `print_buffer_to_file`); the start line reads `Starting MariaDB <version> source revision <rev> as process N`
from 10.6.12 on (also in 10.3.39 and 10.5.22), and `<program> (server <version>) starting as process N ...` in 10.6.7 to
10.6.11, while the other lifecycle lines are the same in all of them; the fatal signal handler writes `YYMMDD HH:MM:SS [ERROR] <program> got signal N ;`
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

Issue #165 asks to apply these rules where a default server actually writes MariaDB's output, and to keep a crash report
together across journal entries with the bounds of the error log. Checked in the MariaDB source (10.6.22):
`print_buffer_to_file` (`sql/log.cc`) writes every server line with its header to standard error, the fatal signal handler
writes its header and the crash report to the same descriptor (`sql/signal_handler.cc`), and `sql/mysqld.cc` redirects
standard error to a file only when `log_error` is set, which the Debian `50-server.cnf` leaves commented out ("When running
under systemd, error logging goes via stdout/stderr to journald"). The upstream `mariadb.service` starts
`/usr/sbin/mariadbd` and sets neither `SyslogIdentifier=` nor `SyslogLevel=`, so by systemd's defaults (not checked on the
server) every line becomes a journal entry of its own with the identifier `mariadbd`, the server's `_PID` and the priority
`info` for every line, and rsyslog writes it to `syslog` as `mariadbd[<pid>]: <line>`. An empty line gets no entry: journald's
`stdout_stream_log` (`src/journal/journald-stream.c`, checked in systemd 249, the version of Ubuntu 22.04, and in the current
source) returns for an empty line before it stores or forwards anything, so the empty lines of a crash report reach neither
the journal nor a syslog file that journald feeds. MariaDB's `50-mysqld_safe.cnf`
suggests a drop-in with `SyslogIdentifier = mysqld` and `SyslogLevel = err`, and servers before 10.5 run as `mysqld`. The
journal and syslog parsers stored each of these lines as a record of its own, with the header in the message and no event:
only program `kernel` lines were grouped.

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
   stays 1.0; the agent may leave it empty; #21 can match on it instead of on message text. Go gets the field although
   only the backend fills it for now: the C# decoder keeps the rules of the Go decoder and the Go encoder's golden batch
   pins the contract ([0075](0075-wire-contract-pinned-by-golden-fixtures.md)), so a field only in C# could not be pinned,
   the Go decoder would drop it as an unknown key, and the wire format would describe a record its reference
   implementation does not have; a variant with the field in C# and storage only was rejected for these reasons.
4. **The classification in `program` or in the message** — no model change; overloads fields that mean something else.

What `event` may hold:

5. **A fixed list in the model** — strict; every parser that adds events changes the model in both languages.
6. **The name rule of the wire format, values defined by the producer** (chosen) — the same rule as metric names; the
   MariaDB values are listed in the area document.

How the file is recognized:

7. **By the path `mysql/error.log`, with an option for other paths (as the issue asks)** — the import reads copies and
   archives of any layout, a name says nothing about the content, and a default server does not write this file at all.
8. **By content: the first non-empty line of the file head is a MariaDB entry header, and the name is not one the syslog
   parser claims; no option** (chosen) — finds the log wherever it was copied (`error.log`, `<host>.err`, inside a tar).
   The header forms are specific enough that no other log of a saved `/var/log` begins with one (dpkg, the general and
   slow query logs, nginx, fail2ban and MySQL 8 lines were compared). A content claim outranks the syslog parser's weak
   claim ([0086](0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md)), so the rule must not let a
   syslog file become a MariaDB file: the first-line grammars are disjoint (a syslog line starts with `<`, a month name or
   `DDDD-DD-DDT`, a header with `DDDD-DD-DD ` or `DDDDDD `), so a file the syslog parser claims by its first line is
   never claimed here, and a syslog name (`syslog`, `kern.log`, with `.N` or `-YYYYMMDD`, the syslog parser's own name
   rule) is refused whatever the content. A variant that accepted a header on **any** line of the head (to find a copy
   that starts inside an entry) was rejected in the security review: one header-shaped line that anyone with a raw line
   feed gets into the first 4 KiB of a syslog file, or of any other file, would move the whole file to this parser, its
   lines before skipped and those after read as continuation lines of one forged entry. The price of the chosen rule is
   that a copy starting inside an entry (`tail` output) is not recognized.

How multi-line output stays together:

9. **One record per line** — breaks "stack traces stay one record".
10. **An entry is a header line and the lines without a header after it** (chosen) — this is how MariaDB writes multi-line
    messages and crash reports.

How a long entry is cut (a crash report with a long query exceeds the 16 KiB text limit):

11. **Head and tail, as kernel reports** (0086 option 8) — the tail of a crash report (resource limits, kernel version)
    matters less than its head (signal, server version, stack trace).
12. **The head with a line naming how many lines were left out** (chosen) — memory is bounded by the kept head; the marker
    is the same as for kernel reports. Only an entry beyond the text limit is cut: an entry within it is kept whole, its
    empty lines included, because a marker that replaces lines of a message that fits would lose content and could make
    the message longer than the whole one. Empty lines are held back as a count until a line follows them, so a run of
    them costs memory only within the text limit, not per line. Rejected with it: keeping empty lines only within the
    marker's room (16,320 bytes) even when the whole entry fits.

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

19. **Accept and document** (chosen) — MariaDB copies client text into its log unescaped, so a line break in it followed
    by a fake header becomes an entry of its own with a forged time, level and event. Two paths were verified in the
    source: the query of a crash report, written raw, which needs a client that can crash the server; and the user name of
    a failed login, which `login_failed_error` (`sql/sql_acl.cc`) logs as `Access denied for user '<name>'@'<host>' ...`
    whenever `log_warnings` is above 1, the default being 2. The handshake's user name is cut to 128 characters but not
    filtered, so any client that reaches the port, a local web application included, can forge an entry without
    credentials and without crashing anything. No escaping exists in the format to tell such lines apart; as in 0086
    option 19, an event is a classification of text, not proof of origin. The same holds for the file: the source type
    `mariadb` says which parser read it, not that MariaDB wrote it. Any file of the imported tree whose first non-empty
    line has a header's shape is read as a MariaDB error log with events, such as a file a web-space user wrote under a
    saved `/var/www` or an application log whose first line is client text; `log` keeps its real path.

Where the rules apply to MariaDB's output in the journal and syslog (#165):

20. **A parser of its own** — a journal export or a syslog file holds every program's lines, so no file can be claimed for
    MariaDB.
21. **A stage in the journal and syslog parsers, before kernel reports are grouped** (chosen) — the place of the kernel
    report grouping ([0086](0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md) option 8); the two
    stages see disjoint programs.

Which lines are MariaDB lines:

22. **Program `mariadbd` only, as the issue names it** — misses servers before 10.5 and the drop-in MariaDB itself suggests.
23. **Program `mariadbd` or `mysqld`** (chosen) — MySQL 8's `mysqld` writes another header (`YYYY-MM-DDTHH:MM:SS.ffffffZ`),
    which is no entry header, so its lines stay plain; MySQL 5.5 and 5.6 write MariaDB's header forms and would get
    MariaDB's events, accepted because no supported Ubuntu ships them.
24. **Every line whose message has a header's shape, whatever its program** — any program's text that starts with a date
    would lose it and could get an event.
25. **Only journal entries with the trusted `_TRANSPORT=stdout` and `_SYSTEMD_UNIT`** — closes forging by name in the
    journal, but syslog files carry no such field, so the two parsers would classify the same lines differently (the reason
    of 0086 option 18), and the journal parser would have to keep more fields.
26. **A message that holds a line feed is never a MariaDB line** (chosen, with 23) — standard error arrives one line per
    journal entry and per syslog line; only a native journal client writes such a message, and the header and event rules
    are defined per line.

Which time:

27. **MariaDB's local time stamp, read in `import.time_zone`** (the rule of the error log) — a journal export, which carries
    UTC microseconds and needs no option today, would fail with "import.time_zone is not set" at its first MariaDB line, and
    an exact time would be replaced by a local one to the second.
28. **The time of the journal entry or syslog line** (chosen) — exact in the journal, resolved by the syslog rules in a
    syslog file; MariaDB's time stamp only marks the header and is neither read nor checked, so a header whose date does
    not exist still opens an entry.

How lines join an entry:

29. **Every following MariaDB line up to the next header**, as in the file — a journal holds other writers under the same
    name, and the lines of two processes can interleave.
30. **The lines of the same host, program and pid; one open entry, ended by the next MariaDB header of any key, by a line
    of its key beyond the bound of option 31 or by the end of input** (chosen) — in the journal the pid is journald's
    `_PID`, which the sender cannot choose, so another process logging as `mariadbd` cannot add lines to the server's
    entry; a single open entry bounds memory like the single open kernel report; the lines of others are emitted as they
    come, before the entry (0086 option 11).
31. **A time bound: a line joins only when its time is at most 60 seconds before or after the header line's** (chosen) —
    unlike a continuation line of the error log, every journal entry and syslog line has a time of its own, and without a bound a
    line of the same host, program and pid hours later would join the entry and be stored at its header's time. The bound
    counts from the header, like the span of a kernel report ([0086](0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md)),
    so no member of an entry lies more than 60 seconds from the record's `captured_at`; it holds backwards too, against a
    clock that was set back. A line of the entry's key beyond it ends the entry and is stored as a plain line. Rejected with
    it: no bound, as in the error log, for the reason above (the text bound of option 12 bounds memory, not time); a gap
    from the previous member line, which a line every 59 seconds keeps open, so its lines are misdated without bound; a
    line count, which memory does not need. The price: a crash report whose lines reach the journal more than 60 seconds
    after its header (a slow stack trace resolution, a stalled journald) is split, its later lines stored as plain lines.

Which fields:

32. **The fields of the error log** (program empty, the time from the header, a priority only from the level) — the journal
    and syslog know the program, the host, the pid and an exact time.
33. **Message, level and event from the header, every other field from the line** (chosen) — the priority is the level's
    where there is one, because the journal's priority of a service's standard error is the unit's `SyslogLevel=`, the
    same for every line, and the line's own otherwise; the message loses the header prefix as in the error log, so the same
    output gives the same message from the file, the journal and syslog, except for its empty lines, which journald does
    not store (see *Context*).

Crash recovery when an input holds several hosts (a merged journal, a central syslog):

34. **One recovery state for the whole input, in input order, as in the error log** (chosen) — the classifier of #17
    unchanged, one instance per parse. Vandox reads one server, whose journal export carries one `_HOSTNAME`. In an input
    with several hosts, another host's `InnoDB: ... started` line can end a recovery it did not start and get
    `mariadb.recovery_end`, and another host's start closes a recovery without an end; this is documented as a limitation.
35. **A recovery per host** — not asked for by #165, and no gain for one server. Its simplest form, one tracked
    recovery that belongs to the host of its start entry, is wrong when two hosts' recoveries interleave: a
    start of host B drops host A's open recovery. A state for every host needs a map that hostile input with many host
    names grows, and so a bound of its own. Both need a second `Classify` overload and a changed state type.

Forged lines in the journal and syslog:

36. **Accept and document** (chosen) — any local process can log under the name `mariadbd` (`logger -t`, `systemd-cat -t`,
    `openlog()` in a web-space user's PHP script) and so create entries with any level and event, or end the server's open
    entry with a forged header. Option 30 keeps such a process's lines without a header out of the server's entry in the
    journal; in a syslog file the pid is what the line says, so such lines can join the server's entry within the 60
    seconds of option 31. As in option 19 and 0086 option 19, a program name and an event classify text and prove no
    origin.

## Decision

Options 3, 6, 8, 10, 12, 14, 16, 18 and 19: the parser `mariadb` recognizes the error log by content (its first non-empty
line is an entry header; a file with a syslog name is never claimed), turns every entry (a
header line and its continuation lines) into one `log_line` record with its local time resolved in `import.time_zone`, keeps
the head of a long entry with an omitted-lines marker, and classifies lifecycle entries into the new optional `log_line`
field `event` (name rule, stored in `log_lines.event`); no path option is added. For MariaDB's output in the journal and
syslog (#165), options 21, 23, 26, 28, 30, 31, 33, 34 and 36: the journal and syslog parsers apply the header, bounds and
event rules to the lines of program `mariadbd` or `mysqld` before kernel reports are grouped, join a header line and the
following lines without a header of the same host, program and pid within 60 seconds of the header line into one record
that keeps the time, host, program and pid of its header line, and track a crash recovery in input order as in the error
log. The rules are in [Log import](../areas/log-import.md) (*MariaDB error log*, with *Lines from the journal and syslog*), the field in
[Wire format](../areas/wire-format.md) and [Storage](../areas/storage.md).

## Consequences

- On a server where MariaDB logs to the journal (the packaged default under systemd) there is no error log file and this
  parser imports nothing; MariaDB's entries come from a journal export or a syslog file, classified like the error log but
  with the source type `journal` or `syslog`, so a query for them selects by `event` or program, not by source type
  `mariadb`.
- A journal export with MariaDB lines still needs no `import.time_zone` (option 28).
- Importing a journal export and the syslog file of the same period stores each MariaDB entry twice, as every line
  (0086); signature detection (#21) must not count such an event twice.
- Content imported before #165 keeps its plain MariaDB lines (an import is idempotent per content). A journal or syslog
  file whose interrupted import is resumed by a version with #165 can lose or repeat records near the resume point, because
  the grouping changes the record count and a changed parser version is not noticed (the import's existing limit).
- A local process that logs as `mariadbd` or `mysqld` can forge MariaDB entries and events in the journal and syslog, and
  end the server's open entry with a forged header, after which the rest of that entry is stored as plain lines (option 36).
- A MySQL 5.5 or 5.6 server logging as `mysqld` would get MariaDB's events (option 23).
- An entry from a real journal, or from a syslog file that journald feeds, has no empty lines: a crash report's message
  lacks the inner empty lines that the error log keeps (option 33).
- A crash report whose lines arrive more than 60 seconds after its header is split; its later lines are stored as plain
  lines (option 31).
- In an input with several hosts, one host's crash recovery can be ended by another host's start entry, or by its InnoDB
  `started` entry, which then gets `mariadb.recovery_end` (option 34).
- The issue's default path and path option are not built (option 8 instead of 7); a path for live shipping belongs to #37.
- `event` is stored, not indexed; a query by event needs its own change (#21, #23).
- The 16 KiB text limit stays; a crash report with a long query loses its tail and says so.
- A file whose first non-empty line is not an entry header (a copy that starts inside an entry, a log whose first line
  a library wrote without a header) is listed as not recognized, and so is a MariaDB error log under a syslog name.
- A syslog file keeps its parser whatever lines it holds: a raw line in it with the shape of an entry header is no syslog
  line, and only the messages of `mariadbd` and `mysqld` syslog lines are read with the MariaDB rules (option 23).
- Lines of a time zone change between an interrupted import and its resume are shifted, as for syslog files (0085).
- The agent's live shipping (#37) may send `event` or leave it empty; the backend accepts both.
- Signature detection (#21) and outage reconstruction (#23) must not treat an `event` as proof that MariaDB wrote the line
  (option 19): any client that reaches the database port can forge one through a failed login. Nor may they treat the
  source type `mariadb` as proof that the file was a MariaDB error log (option 19).
