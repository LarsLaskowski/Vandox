# Spec: Parser for the MariaDB error log

Status: Draft

Source: Issue #17 (depends on #15, closed; the import framework and the system log parsers of #16 are on `main`).

## Problem / motivation

MariaDB was the most frequent victim of the OOM killer on the monitored server. A process killed with SIGKILL writes
nothing, so the outage shows in the MariaDB error log as what comes after it: a new start, InnoDB crash recovery, and the
moment the server is ready for connections again; real crashes show as an abort by a signal followed by a crash report
with a stack trace. `vandoxd import` recognizes journal exports and rsyslog files today, but not the MariaDB error log, so
these facts cannot be imported, and nothing marks a line as a start, a shutdown, an abort or a recovery.

## Behavior

- `vandoxd import` recognizes a MariaDB error log by its content, wherever it lies in the input and whatever its name
  (`mysql/error.log`, its rotations `error.log.1` and `error.log.2.gz`, `<host>.err`, inside a `.tar.gz`). There is no
  option for its path: the import reads copies and archives, and detection by content finds the file in any of them.
  The summary lists such a file with source type `mariadb`.
- Each **entry** of the log becomes one record: a line that starts with a MariaDB time stamp, together with the lines
  without a time stamp that follow it. So a crash report with its stack trace is one record, and so is `ready for
  connections.` with its `Version:` line. A very long entry (a crash report with a long query) keeps its beginning and says
  how many lines were left out.
- The record holds the time of the entry in UTC, the severity (`ERROR`, `Warning`, `Note` as syslog priorities 3, 4 and 6;
  none for lines without a level), the message and, for lifecycle entries, an **event**:
  `mariadb.start`, `mariadb.ready`, `mariadb.shutdown`, `mariadb.shutdown_complete`, `mariadb.abort`,
  `mariadb.recovery_start`, `mariadb.recovery_end`. Other entries have no event. The start line changed its wording in
  10.6.12 (`Starting MariaDB ... as process N`; before: `<program> (server <version>) starting as process N ...`); both
  are a start.
- An event is a classification of the text, not proof that MariaDB wrote the line: MariaDB writes some client text raw
  into its log (the query of a crash report, the user name of a failed login), so a client can forge an entry with a line
  break. This is documented, not prevented; the format has no escaping that would tell such a line apart.
- MariaDB writes local time without a zone. The import reads it in the zone of the existing option `import.time_zone`,
  exactly as for traditional syslog files: without the option the file is listed as failed with "import.time_zone is not
  set" and the run exits with 1; setting the option and importing again completes the file.
- Lines that cannot be stored are reported as skipped with a fixed reason (lines before the first entry, an entry with a
  date that does not exist or lies outside the storable range); nothing is dropped silently.
- The `log_line` record gains the optional field `event` in both languages, in the wire format (still version 1.0) and in
  the database (schema version 5). Existing records and agent batches without it stay valid.
- Importing the same file again stores nothing, as for every other source.

## Acceptance criteria

- [ ] AC1: A MariaDB 10.6 error log is recognized by content under any name and imported with source type `mariadb`;
  journal exports and syslog files are still recognized as before, and no other file of a saved `/var/log` is claimed.
- [ ] AC2: Start, ready, normal shutdown, shutdown complete, abort by signal, crash recovery start and crash recovery end
  are classified as the events above; other entries have none.
- [ ] AC3: Warnings and errors are recognized as priorities 4 and 3; notes are 6.
- [ ] AC4: A crash report with its stack trace stays one record, and every continuation line stays with its entry.
- [ ] AC5: Times are converted from `import.time_zone` to UTC, including daylight saving changes; without the option the
  file fails with "import.time_zone is not set" and a later run with the option completes it.
- [ ] AC6: Tests use shortened samples in the exact formats MariaDB 10.6 writes, including a start after a crash with
  recovery, a normal shutdown and a crash report, and the start line of 10.6.7 to 10.6.11 as well as of 10.6.12 and later.
- [ ] AC7: Memory stays bounded for any input: a file with one entry followed by 64 MiB of continuation lines is parsed
  into one record of at most 16 KiB.
- [ ] AC8: The `event` field round-trips through the wire format (Go encoder, C# decoder) and the database.

## Formats checked

The formats come from the MariaDB source, read at the tags 10.3.39, 10.5.22, 10.6.7, 10.6.11, 10.6.12, 10.6.22, 10.6.28
and 10.11.9. Within these, the lifecycle lines differ only in the start line (see *Behavior*); a lifecycle line that a
version outside this list words differently gets no event, and the entry is still imported.

## Deviations from the issue

Issue #17 asks to "parse the MariaDB error log format (Ubuntu default path `/var/log/mysql/error.log`, path
configurable)". Two parts of that are not done, decided by the Lead (record 0088):

- **No default path.** The parser does not look for `/var/log/mysql/error.log` or any other name; it recognizes the file
  by its content. MariaDB's Debian packaging, which Ubuntu follows, does not write that file by default under systemd: the
  line is commented out and the error log goes to the journal.
- **No path option.** `vandoxd import` reads copies and archives in any layout, so a server path matches nothing there,
  while detection by content finds the file under any name. A configured path belongs to the agent's log shipping (#37).

## Out of scope

- MariaDB lines in the journal or in `syslog` (the packaged default under systemd sends the error log there): they stay
  plain log lines of program `mariadbd` without an event; classifying them is #165.
- A configuration option for the path of the error log (the agent's configured log files are #37).
- The MariaDB slow query log and general query log (different formats, not recognized).
- MySQL 5.7 and 8 error logs (ISO 8601 time stamps; not recognized).
- Detected events across sources, incidents and queries by event (#21, #23).
- The `mysqld_safe` "ended" line (written only without systemd) is imported but not classified.

## Open questions

No question blocks the work. For the Product Manager, as information (repeated in the pull request):

- **This parser may import nothing from the production server.** The Debian packaging of MariaDB 10.6 (followed by
  Ubuntu 22.04's package; neither the Ubuntu package nor the server's Plesk configuration was checked) does not write
  `/var/log/mysql/error.log` by default but sends the error log to the journal. If the saved `/var/log` of the server holds
  no MariaDB error log, the outage evidence of MariaDB is in the journal, and #165 is the change that classifies it.
  Whether `/var/log/mysql/error.log` exists on the server can be checked with `ls -l /var/log/mysql/`.
- The two deviations from the issue's wording above (no default path, no path option).
- The samples in the tests are built from MariaDB's own source format strings, not taken from the production server's log,
  which the squad does not have. A shortened excerpt of the server's real `error.log`, if one exists, can be added as a test
  fixture.
