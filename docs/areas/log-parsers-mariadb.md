# MariaDB error log parser

## Scope

How the import reads MariaDB's error log (parser `mariadb`): which file it claims, how lines are grouped into entries,
the lifecycle events it classifies and how it times them; and how the journal and syslog parsers group the MariaDB
lines they carry into the same entries. The rules shared by all parsers are in the [Log import](log-import.md) parser
contract; the detection and the incident analysis rely on the events.

## Recognition

- The parser has no path option and finds the file by its content. Detection returns a content match only when the
  file's name is no syslog name **and** the **first non-empty line** of the head is an entry header (below), claimed as
  [Log import](log-import.md), *Claim by the first line*, states. Empty lines before the first non-empty line are
  passed over, and a header cut by the end of the head counts when its prefix is complete.
- A syslog file stays with the syslog parser whatever lines it holds, and a file the syslog parser claims by its first
  line is never claimed here (a syslog line starts with `<`, a month name or `DDDD-DD-DDT`, a header with
  `DDDD-DD-DD ` or `DDDDDD `).
- The price: a copy that starts inside an entry (the output of `tail`), a MariaDB log whose first line has no header
  and a MariaDB error log under a syslog name are listed as not recognized. Neither the source type `mariadb` nor an
  event proves that the file was a MariaDB error log ([Log import](log-import.md), *The shape is no proof*).

## Entries

- A line is an **entry header** when its bytes begin with one of four ASCII forms, checked on the raw line before any
  decoding (the hour is two digits or a space and one digit):
  - `YYYY-MM-DD HH:MM:SS <thread> [ERROR|Warning|Note] ` (the server line; the thread is 1 to 20 digits, not stored);
  - `YYYY-MM-DD HH:MM:SS 0x<1 to 16 lower-case hex digits>` and one or more spaces (an InnoDB time stamp, no level);
  - `YYMMDD HH:MM:SS [ERROR|Warning|Note] ` (the signal handler, year 2000 + YY);
  - `YYMMDD HH:MM:SS mysqld_safe ` (the start script, program `mysqld_safe`).

  Digits of month, day, hour, minute and second are not range-checked by the grammar. Every other line, an empty one
  included, is a continuation line.
- A header and the continuation lines after it up to the next header or the end of input are **one record**:
  `source` `mariadb`, `host` empty, `pid` 0, `program` empty (`mysqld_safe` for the start script), `priority` 3 for
  `ERROR`, 4 for `Warning`, 6 for `Note` and none without a level, the message the header's message (the text after
  the header prefix) and the continuation lines joined by `\n`. Inner empty lines are kept, trailing ones are dropped.
- Continuation lines before the first header are skipped: "empty line" for an empty one, "line before the first entry"
  for any other.

## Bounds

- A message of at most 16,384 UTF-8 bytes (decoded; invalid bytes count three) is kept whole, empty lines included.
- A longer entry with at least one continuation line keeps its first line (cut at a character boundary to 16,320
  bytes if longer) and the following whole lines while the text stays within 16,320 bytes, then `\n[N lines omitted]`
  with the exact number of lines left out (trailing empty lines not counted), and `truncated`. The kept part is the
  longest run of whole lines, empty ones included, that fits in 16,320 bytes, so the cut can fall inside a run of empty
  lines; empty lines beyond are counted, never stored, so millions of empty lines cost a counter.
- A long header line alone is cut to 16,384 bytes without a marker.
- A continuation line is decoded only when it is kept, so memory does not depend on the input size. An entry that is
  cut or whose line was cut by the line reader has `truncated` set.

## Events

The record field `event` is set from the level and the header's first line:

| Event | Level | Header message |
| ----- | ----- | -------------- |
| `mariadb.start` | `Note` | starts with `Starting MariaDB ` and contains ` as process ` (10.6.12 and later, also 10.3.39 and 10.5.22); or a non-empty text, ` (server `, later `) starting as process ` and ends with ` ...` (10.6.7 to 10.6.11) |
| `mariadb.ready` | `Note` | ends with `: ready for connections.` |
| `mariadb.shutdown` | `Note` | ends with `: Normal shutdown` |
| `mariadb.shutdown_complete` | `Note` | ends with `: Shutdown complete` |
| `mariadb.abort` | `ERROR` | a non-empty text, ` got signal `, 1 to 3 digits, ` ;` at the end |
| `mariadb.recovery_start` | `Note` | starts with `InnoDB: Starting crash recovery`, or is exactly `Starting table crash recovery...` |
| `mariadb.recovery_end` | `Note` | is exactly `Crash table recovery finished.`, or starts with `InnoDB: ` and contains ` started; log sequence number ` while a recovery is open |

- Anything else, and every entry without a level, has no event. Only the header's first line counts.
- A recovery is open from a `mariadb.recovery_start` entry until the next `mariadb.recovery_end` or `mariadb.start`
  entry in file order (in the journal and syslog, in input order over the entries of every host, below); an entry
  skipped for its time is not classified and does not change that.
- The formats were checked in the MariaDB source at 10.3.39, 10.5.22, 10.6.7, 10.6.11, 10.6.12, 10.6.22, 10.6.28 and
  10.11.9; a lifecycle line that another version words differently gets no event and the entry is still imported. The
  `mysqld_safe` "ended" line is not classified.

## Time

- MariaDB writes the server's local time without a zone. It is read in `import.time_zone` with the rules of
  [Log import](log-import.md), *Local time*; the instant that decides the repeated hour is that of the previous
  resolved header. The year is part of the header.
- The digits are checked as integers before any date is built, and the file is skipped entry by entry: an entry whose
  time does not exist (month 13, `2026-02-30`, hour 24, minute or second 60) is skipped once with the header's line
  number as "invalid date", and one outside the storable range as "time outside the storable range"; its continuation
  lines are neither records nor skips of their own, and a skipped header leaves the previous instant unchanged.
- With `import.time_zone` unset, the first header fails the parse before its time is looked at ([Log
  import](log-import.md), *Missing time zone*).

## Forged lines (limitation)

MariaDB writes some client text raw into its log: the query of a crash report (up to 64 KiB, MariaDB's
`my_safe_print_str`) and, without any credentials, the user name of a failed login (`Access denied for user
'<name>'@...` at the default `log_warnings` of 2, cut to 128 characters but not filtered). A line break in that text
followed by a fake header splits the entry there and creates an entry of its own with any time, level and event. The
format has no escaping that would tell such a line apart, so this is documented, not prevented: later analysis
(signature detection, outage reconstruction) must not treat an event as proof that MariaDB wrote the line. The journal
and syslog add a second way to forge entries (below).

## Lines from the journal and syslog

Under systemd the packaged MariaDB writes its error log to standard error, which the journal stores line by line (the
Debian packaging leaves `log_error` commented out), so a server with the packaged default has no error log file and
this parser imports nothing; its MariaDB output reaches the import through a journal export or a syslog file. The
journal and syslog parsers apply these rules to the records they map, as the second of their stages ([System and mail
log parsers](log-parsers-system.md), *Stages of the journal and syslog parsers*), before kernel reports are grouped.

### Grouping

- A **MariaDB line** is a record whose `program` is exactly `mariadbd` or `mysqld` and whose message holds no line
  feed. A line the parser skips is not seen by these rules: it neither opens nor ends an entry.
- A MariaDB line whose message begins with an entry header (one of the four forms of *Entries*, checked on the UTF-8
  bytes of the decoded message) opens an **entry**. The following MariaDB lines that are no header, have the same
  `host`, `program` and `pid` as the header line (its **key**) and whose time is at most 60 seconds before or after the
  header line's are its continuation lines, as long as the entry's message with them stays within 16,384 UTF-8 bytes.
- One entry is open at a time. It ends at the next MariaDB header line, of any key, or at the normal end of input. It
  also ends at a MariaDB line of its key that is no header and either lies more than 60 seconds before or after the
  header line or would make the message longer than 16,384 bytes; that last case counts the line feed and the empty
  lines held back before the line.
- A line that ends the entry without being a header is emitted unchanged after the entry's record. Each following line
  of the key is then emitted unchanged too, since no entry is open until the next header. The empty lines held back
  before such a line are trailing lines of the entry and are dropped, as in *Bounds*.
- The time bound counts from the header line, not from the previous member, so no member of an entry lies more than 60
  seconds from the record's `captured_at`. A crash report whose lines arrive later than that, or that is longer than
  16,384 bytes, is split, and its later lines are emitted as plain lines. Unlike in the error log, no line's text is
  dropped or reduced to a count.
- Every other record, a MariaDB line that is no header and of another key or without an open entry included, is
  emitted unchanged as it comes, before the record of the open entry.

### The entry's record

- The record of an entry is the header line's record with four fields replaced: `message` is the header's message
  and the messages of the continuation lines joined by `\n`, at most 16,384 UTF-8 bytes (decoded, invalid bytes count
  three) with inner empty lines kept and trailing ones dropped. The message is never cut and carries no
  `[N lines omitted]` marker, because a line that does not fit ends the entry. `truncated` is set when the header line
  or a member line was truncated; `priority` is 3 for `ERROR`, 4 for `Warning` and 6 for `Note`, and the header line's
  own priority for a header without a level; `event` is set as in *Events*. `captured_at`, `log`, `source`, `origin`,
  the sequence, `host`, `program` and `pid` stay those of the header line, also for the `mysqld_safe` form.
- The records keep the source type `journal` or `syslog`, not `mariadb` (for a journal export and syslog files of the
  same period see [System and mail log parsers](log-parsers-system.md), *Duplicates*).
- The time is the journal entry's `__REALTIME_TIMESTAMP` or the syslog line's time. The time stamp in the message only
  marks the header and is never read: a journal export needs no `import.time_zone` for its MariaDB lines, and a header
  whose date does not exist (`2026-02-30`) still opens an entry. A syslog file needs the option for its year-less
  times as before.
- The journal's `PRIORITY` of a service's standard error is the unit's `SyslogLevel=`, the same for every line (`info`,
  6, by systemd's default; not checked on the server); the level of a header replaces it.
- journald stores no entry for an empty line of standard error and forwards none to syslog (`stdout_stream_log` in
  systemd's `src/journal/journald-stream.c`, checked in systemd 249 and the current source). An entry from a real
  journal export, or from a syslog file that journald feeds, therefore has no empty lines, and its message lacks the
  inner empty lines that the error log keeps (a crash report has several); otherwise the same output gives the same
  message as the error log, for every entry within 16,384 bytes. An empty message that does arrive (an entry with an
  empty `MESSAGE`, a syslog line that ends after `mariadbd[<pid>]:`) is a line that is no header, with the empty-line
  rules of *Bounds*.

### Recovery, memory and end of input

- The crash recovery of *Events* is tracked as in the error log: one state per parse, in input order over the MariaDB
  entries of every host, program and pid. Vandox reads one server, whose journal export carries one host. In an input
  with several hosts (a merged journal, a central syslog), another host's `InnoDB: ... started; log sequence number`
  entry can end a recovery it did not start and get `mariadb.recovery_end`, and another host's `mariadb.start` entry
  closes a recovery without an end (limitation).
- The memory these rules hold is the open entry's message, at most 16,384 bytes, and at most two records passed from
  the MariaDB stage to the kernel stage per line, independently of the number and length of the lines. The open entry
  is emitted only at the normal end of input ([Log import](log-import.md), *Parsing*).

### Forged lines (limitation)

- Any local process can log under the name `mariadbd` or `mysqld` (`logger -t mariadbd`, `systemd-cat -t mariadbd`,
  `openlog()` in a PHP script of a web-space user) and so create entries with any level and event, and end the
  server's open entry with a forged header line.
- In the journal, `pid` comes from `_PID` when the entry has it, which journald sets from the sender's credentials. A
  forger's header there opens an entry of its own key, so the rest of the server's entry is emitted as plain lines, and
  a forger's lines without a header never join the server's entry. That holds only while the forger's entries carry
  `_PID`: an entry without it takes `SYSLOG_PID`, which the sender chooses, so such entries can be forged as in a
  syslog file.
- In a syslog file the pid is what the line says, so a forger can use the server's pid. Its lines without a header,
  written within 60 seconds of the server's header line, join the server's open entry and can fill it until the
  server's next line no longer fits. That line ends the entry, and it and the server's following lines are emitted as
  plain lines. Its header line opens an entry of the server's key, which takes in the server's following lines without
  a header within 60 seconds of the forged header, under the forged level and event.
- A server line is therefore always stored, in an entry or as a plain line, and never reduced to a count. In a syslog
  file, though, it is not necessarily in its own entry, and the lines joined in one message can come from different
  writers. Later analysis that splits a message at its line feeds must not take any line of it as the server's.
- A separate file shaped like a journal export (one a web-space user wrote under a saved `/var/www`) carries any `_PID`
  its writer chose. But the grouping state belongs to one parse of one file, so such a file cannot reach the lines of
  another file.

## Related decisions

- [0088](../decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md) — why the MariaDB error log is detected by content, kept as entries and classified into an `event` field, and why MariaDB lines of the journal and syslog are joined by program, host and pid within 60 seconds of their header and 16,384 bytes, split rather than cut, and keep the time of their line (option 36: the program filter is a classification, not a trust boundary).
- [0085](../decisions/0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md) — why local times are read in `import.time_zone`.
- [0092](../decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md) — why only the Plesk and web server error logs, not this log, have path options.

## Not here

- The rules shared by every parser (claims, syslog names, record fields, line reader, storable range, local time,
  missing time zone): [Log import](log-import.md), *Parser contract*.
- The journal and syslog parsers and their other stages: [System and mail log parsers](log-parsers-system.md).
- The other parsers: [Plesk and web server error log parsers](log-parsers-web.md),
  [Legacy monitoring log parser](log-parsers-legacy-monitoring.md).
- The `event` field of a `log_line` record: [Wire format records](wire-format-records.md).
- The signatures that read MariaDB entries: [Log signatures](log-signatures.md).
- The agent's MariaDB collector: [Agent MariaDB collector](agent-mariadb.md).

## Implementation

`Vandox.Core.LogParsing`: `MariaDbErrorLogParser`, `MariaDbParseSession`, `MariaDbLine`, `MariaDbMessage`,
`MariaDbEventClassifier`, `MariaDbEvents`, `MariaDbLineGrouper` (the journal and syslog stage), `SyslogClock.ResolveLocal`,
`Utf8Text`. A missing time zone surfaces as `InvalidOperationException` from `ParseAsync`.
