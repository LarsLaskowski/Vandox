# System and mail log parsers

## Scope

The import parsers for the system logs of the monitored server: the journal export (`journal`), the syslog files
(`syslog`) with their grouped kernel reports and classified Postfix and Dovecot lines, and Dovecot's own log file
(`dovecot`). They fulfill the [Log import](log-import.md) parser contract, which holds the rules shared by all
parsers (claims, record fields, text limits, storable range, local time, missing time zone).

## Detection

- `journal` claims a file by content: a head that begins with a `__CURSOR=` or `__REALTIME_TIMESTAMP=` line and holds a
  `__REALTIME_TIMESTAMP=` line. The file name never matters.
- `syslog` claims weakly (name match) a file with a syslog name ([Log import](log-import.md), *Registration and
  detection*) or whose first line has a syslog header.
- `mail.log`, `mail.err`, `maillog` and their rotations are claimed by their first line, like `auth.log`; they are no
  syslog names, so a copy whose first line is cut is not recognized. `mail.log` and the other rsyslog files keep the
  source type `syslog`; a journal export keeps `journal`.
- `dovecot`: *Dovecot log file* below.

## Journal export

The format is `journalctl -o export`. Entries are blocks of fields separated by an empty line. Text fields are
`NAME=value\n`, binary fields `NAME\n`, a 64-bit little-endian length, the bytes and `\n`.

- Mapping to a `log_line` record: `captured_at` from `__REALTIME_TIMESTAMP` (microseconds since the Unix epoch, UTC, 1
  to 20 decimal digits, greater than 0 and at most 9,223,372,036,854,775, bound-checked while reading), `host` from
  `_HOSTNAME`, `program` from `SYSLOG_IDENTIFIER`, else `_COMM`, `pid` from `_PID`, else `SYSLOG_PID` (1 to
  2,147,483,647, else 0), `priority` from `PRIORITY` when it is one digit 0 to 7, `message` from `MESSAGE`, `log` and
  `source` `journal`.
- A repeated field keeps its first value. Only these eight fields are kept; every other field, text or binary, is read
  through a reused buffer and discarded, and a declared binary length never sizes an allocation.
- A field name must be 1 to 64 bytes of `A-Z 0-9 _` and not start with a digit, otherwise the entry is skipped as
  "malformed field" and reading resumes after the next empty line (scanned in the reused buffer).
- An entry without a time stamp, with an invalid one (an instant outside the storable range included) or without
  `MESSAGE` is skipped as "entry without __REALTIME_TIMESTAMP", "invalid __REALTIME_TIMESTAMP" or "entry without
  MESSAGE". An entry cut off by the end of input (a text value without `\n`, a binary length larger than the remaining
  input or at least 2^63) is skipped as "truncated entry" and ends the parse. A last entry without the closing empty
  line is accepted. The skip line number is 0.
- A value longer than its raw read bound (16,384 bytes for `MESSAGE`, 1,024 for the short fields) keeps the first
  bytes (a character split by the cut is dropped whole) and sets `truncated`; the rest is discarded in the reused
  buffer.

## Syslog

- Files: `syslog`, `kern.log` and their rotations, and any file whose first line has a syslog header.
- A line is `[<PRI>]TIMESTAMP HOST [TAG] MESSAGE` in the traditional format (`Mmm dd HH:MM:SS`, day space- or
  zero-padded) or the RFC 3339 format (`YYYY-MM-DDTHH:MM:SS[.fraction]` and `Z` or an offset of at most 14:00,
  upper-case `T` and `Z` only).
- `priority` is the `<PRI>` number modulo 8 (0 to 191), `null` without it. `TAG` is `PROGRAM[PID]:` or `PROGRAM:`;
  without a tag the program is empty and the pid 0; a process ID above 2,147,483,647 gives pid 0 with the tag kept.
  The message follows the colon and at most one space. `source` is `syslog`.
- The syntactic limits of `HOST` (1 to 255) and `PROGRAM` (1 to 128) are counted in UTF-8 bytes of the decoded line
  and lie below the record's limits, so they are never cut: a longer host makes the line "not a syslog line", a longer
  program means no tag.
- RFC 5424 lines and other text are "not a syslog line"; an empty line is "empty line". A UTF-8 byte order mark at the
  start of a file is ignored.
- An instant outside the storable range is skipped as "time outside the storable range", a date that does not exist
  as "invalid date".

**Year of a year-less time.** The anchor is a valid `-YYYYMMDD` date at the end of the file's base name (the end of
that local day), else the modification time; a name date is preferred because a plain copy replaces the modification
time but keeps the name. An anchor outside the storable range is not usable. The year of the first year-less line is
the anchor's year, or the year before when the line lies more than a day after the anchor. Every later line keeps the
predecessor's year and advances it by one when it lies more than 180 days before the predecessor (a small step back
within the same year does not advance it); the year stops advancing once it is past the storable range. The
predecessor is the last year-less line that was resolved, whatever the outcome (a line skipped as "invalid date" or
"time outside the storable range" counts); lines that are not syslog lines, empty lines and RFC 3339 lines never are.
Without a usable anchor every year-less line is skipped with "year unknown: the file has no usable date", and RFC 3339
lines are still read.

**Time zone.** A year-less time is local time in `import.time_zone` ([Log import](log-import.md), *Local time*); the
last resolved instant that decides the repeated hour is that of the last year-less line (RFC 3339 lines and skipped
lines do not count). RFC 3339 lines ignore the zone. With the option unset, the first year-less line fails the parse
([Log import](log-import.md), *Missing time zone*) before its anchor is looked at.

## Stages of the journal and syslog parsers

Both parsers pass their `log_line` records through three stages in this order: the classification of mail lines
(*Mail lines* below), MariaDB entries ([MariaDB error log parser](log-parsers-mariadb.md), *Lines from the journal and
syslog*), then kernel reports (below). The agent's `logs` collector runs a port of the same three stages on its live
lines, bound to the same golden records, with the differences named in [Agent log shipping](agent-log-shipping.md).

### Kernel reports

- A `kernel` line containing `invoked oom-killer:` or `------------[ cut here ]------------` opens a report; kernel
  lines of the same host join it.
- It ends after a line containing `Out of memory: Killed process`, `Out of memory (oom_kill_allocating_task): Killed
  process`, `Memory cgroup out of memory: Killed process` or `Out of memory and no killable processes` (OOM), or
  `---[ end trace ` (`cut here`), or at a new start line, at a same-host kernel line more than 60 seconds after the
  first line, after 2,000 lines, or at the normal end of input.
- The record has program `kernel`, pid 0, the host, `log` and time of the first line, the member messages joined by
  `\n` in file order and the lowest member priority. A single-line report is emitted unchanged.
- Lines of other programs and kernel lines of other hosts written inside the report are emitted as their own records
  before the report record.
- The message keeps the first lines up to 8,192 UTF-8 bytes and the last whole lines that fit into 16,384 bytes, with
  the line `[N lines omitted]` (the exact count) between them and `truncated` set; memory is independent of the report
  length.
- **Forged lines (limitation).** Kernel lines are recognized by program `kernel` alone, without checking the journal
  transport, so a local process that logs as `kernel` can open a report that takes in the real kernel lines of the
  same host for up to 60 seconds or 2,000 lines.

### Mail lines (Postfix and Dovecot)

The classification sets only the priority and the event of a record, never adds, drops or reorders a record, holds no
state and is linear in the length of a message. The message, program, pid, host, `truncated` and `log` are never
changed.

*Programs.* A Postfix program begins with `postfix/` or `postfix-`, contains a `/` and has at least one character after
the last `/` (`postfix/smtpd`, `postfix/submission/smtpd`, `postfix-out/smtp`); the Dovecot program is `dovecot` only.
Any other program is left unchanged, whatever its message carries.

*Severity markers.* The marker sets the priority of the record; a line without a marker keeps the priority and the
event it has.

| Family | Position | Marker, priority (event) |
| ------ | -------- | ------------------------ |
| Postfix | the message begins with it | `warning: ` 4, `error: ` 3, `fatal: ` 2 (`postfix.fatal`), `panic: ` 2 (`postfix.panic`) |
| Dovecot | the message begins with it, or the message is `S: ` + marker + text where `S`, the text before the first `: `, is non-empty and holds no space | `Debug: ` 7, `Warning: ` 4, `Error: ` 3, `Fatal: ` 2 (`dovecot.fatal`), `Panic: ` 2 (`dovecot.panic`) |

The markers include their trailing space. For Dovecot the start of the message is checked first. Dovecot's syslog
format has no `Info: ` marker, so such a line keeps its priority; only Dovecot's own log file reads `Info: `.

*Unavailable database.* A warning or error line gets `postfix.database_unavailable` or
`dovecot.database_unavailable` when the text *T* after the marker carries a client error *C* at an anchored position.
*C* starts with one of eight phrases (ASCII apostrophe, case-sensitive): `Can't connect to local MySQL server`,
`Can't connect to MySQL server`, `MySQL server has gone away`, `Lost connection to MySQL server` (libmysqlclient) and
`Can't connect to local server`, `Can't connect to server`, `Server has gone away`, `Lost connection to server`
(Connector/C).

| Family | Anchor in *T* | *C* |
| ------ | ------------- | --- |
| Postfix | *T* is `connect to mysql server ` + *H* + `: ` + *C*, where *H* is the text up to the first `: ` after the anchor, non-empty and without a space | as written; no `: ` after the anchor means no event |
| Postfix | *T* is `mysql:` + *N* + `: query failed: ` + *C*, where *N* is the text up to the first `: ` after `mysql:`, non-empty and without a space | as written |
| Dovecot | the message begins with `auth-worker(` + one or more ASCII digits + `): ` + marker, and *T* is one to three driver segments + `Connect failed to database (` + ... | the text after the first `): ` that follows the anchor; none means no event |
| Dovecot | as above, with `Query failed, retrying: ` + *C* after the segments | as written |

A driver segment is exactly `mysql: `, or `mysql(` + *P* + `): `, or `sqlpool(` + *P* + `): `, where *P* is the text up
to the first `)` after the opening parenthesis, non-empty and without a space; anything else (another name, another
case, an empty or spaced *P*, a `)` not followed by `: `) ends the walk without an event. The walk reads at most three
segments, each by a prefix comparison; zero segments and a fourth segment give no event. Only the `auth-worker`
process is read, which holds Dovecot's MySQL connections; lines of `lmtp`, `imap` and the other processes, which log
remote text after the marker, are never matched. The text of Postfix's table and host contains no space, while the
remote lookup key of `mysql:<table> lookup error for "<key>"` follows a space before any `: `, so it never reaches the
anchor. Fatal and panic win: a `fatal:` or `Panic:` line gets only its fatal or panic event.

*Forged lines (limitation).* (a) Any local process can log under `postfix/<x>` or `dovecot` with any marker and any
database message. (b) A marker's priority replaces the priority the transport gave the line (a journal `PRIORITY`, a
`<PRI>`), also a more severe one, so a classified priority is the daemon's class read from the text, not the priority
the line was sent with. (c) Remote text reaches a marker position only through a user name in Dovecot's first segment
(`imap(<user>)...`): the default `auth_username_chars` keeps spaces and colons out of it, and a configuration whose
`auth_username_chars` allows a space and a colon lets a remote user forge a Dovecot marker, and with it the priority
and `dovecot.fatal` or `dovecot.panic` (not `dovecot.database_unavailable`: such a first segment begins with the
logging process, e.g. `imap(`, never with `auth-worker(<digits>)`). A user name reaches the database rule in one
configuration only: an `auth-worker` line of a passdb or userdb whose `name` setting is `mysql` or `sqlpool` carries
the auth prefix `<name>(<user>,<ip>): ` where the rule reads a driver segment, so with an `auth_username_chars` that
allows a space (and the colon and parentheses of such a name) a remote user name such as
`a): Connect failed to database (d): Server has gone away` yields
`auth-worker(1): Error: mysql(a): Connect failed to database (d): Server has gone away,1.2.3.4): ...` and
`dovecot.database_unavailable`. (d) Apart from that configuration the database rules read remote text nowhere: a
sender's address, lookup key or Message-ID cannot produce a database event.

*Earlier imports.* Contents imported by a version without the classification keep their records unclassified, and a
resumed import has unclassified records before its resume point ([Log import](log-import.md), *Parsing*).

## Dovecot log file

**Recognition.** With `log_path` (or `info_log_path`, `debug_log_path`) set to a file, Dovecot writes its own log
instead of syslog: each line is the time stamp of `log_timestamp` (default `%b %d %H:%M:%S `, the server's local time),
the process prefix and the log type, without host or program. The parser `dovecot` has no path option and claims a file
by its first line ([Log import](log-import.md), *Claim by the first line*): a content match when the name is no syslog
name and the first line is a Dovecot line (below). A first line cut by the end of the head counts when it is a Dovecot
line as far as it goes; an empty first line means no match. The syslog parser claims such a file weakly as well (its
grammar reads the prefix as the host); the content match wins. A Dovecot log under a syslog name stays with the syslog
parser, and a copy cut inside its first line or a file written with another `log_timestamp` is listed as not
recognized.

**Lines.** A **Dovecot line** is the traditional syslog time stamp (`Mmm dd HH:MM:SS`, English month abbreviation, day
zero- or space-padded, hour 00 to 23, minute and second 00 to 59), one space, and a message *M* that carries a Dovecot
marker at Dovecot's position: *M* begins with `Debug: `, `Info: `, `Warning: `, `Error: `, `Fatal: ` or `Panic: `, or
*M* is `S: ` + marker + text, where `S`, the text before the first `: `, is non-empty and holds no space.

- Every Dovecot line is one record: `source` `dovecot`, `host` and `program` empty, `pid` 0, `message` *M* as written
  (prefix and marker included). No lines are joined: Dovecot repeats prefix and type on every line it writes. The
  parser keeps no state beyond the year and time-zone resolution.
- Any other non-empty line (a syslog line with a host, a `<PRI>` or an RFC 3339 time, a line whose prefix holds a
  space) is skipped as "not a Dovecot line", an empty line as "empty line".

**Priority and events.** The rules of the Dovecot family in *Mail lines* apply to every record, with `Info: ` added:
`Debug: ` 7, `Info: ` 6, `Warning: ` 4, `Error: ` 3, `Fatal: ` 2 (`dovecot.fatal`), `Panic: ` 2 (`dovecot.panic`),
and a warning or error of `auth-worker(<digits>)` with the anchored database message gets
`dovecot.database_unavailable`. A Dovecot line always carries a marker, so every record has a priority.

**Time.** The time stamp is local time in `import.time_zone`, with the year rules and the time-zone rules of *Syslog*
and their skip reasons ("invalid date", "time outside the storable range", "year unknown: the file has no usable
date"). With the option unset, the first Dovecot line fails the parse ([Log import](log-import.md), *Missing time
zone*).

**Forged lines (limitation).** Only Dovecot's log process writes the real file, so `logger -t dovecot` does not reach
it. Remote text reaches a marker position or the database rule there only in the configurations named under *Forged
lines* of *Mail lines* (a widened `auth_username_chars`, a passdb or userdb named `mysql` or `sqlpool`).

## Duplicates

There is no deduplication across sources ([Log import](log-import.md), *Per file content*):

- Ubuntu's rsyslog writes `kern.*` to `kern.log` and `*.*` without auth to `syslog` (from the packaged configuration,
  not checked on the server), so every kernel line is in both files and importing both stores it twice.
- Importing a journal export and the syslog files of the same period stores every message twice (sources `journal`
  and `syslog`), MariaDB entries included.
- `mail.log`, `syslog` and a journal export of the same period store each mail line up to three times with the same
  classification.

## Related decisions

- [0084](../decisions/0084-log-line-record-gets-an-optional-host-field.md) — why `log_line` gets an optional `host`.
- [0085](../decisions/0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md) — why `import.time_zone` has no default, the embedded time zone database and the year rules.
- [0086](../decisions/0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md) — why the generic syslog claim and grouped kernel reports (option 19: kernel lines recognized by program alone).
- [0091](../decisions/0091-mail-lines-classified-in-the-syslog-and-journal-parsers.md) — why Postfix and Dovecot lines are classified inside the syslog and journal parsers by their severity marker, which lines count as an unavailable database, and why Dovecot's own log file gets a parser `dovecot` claimed by its first line.

## Not here

- The rules shared by every parser (claims, record fields, line reader, text limits, storable range, local time,
  missing time zone, resume): [Log import](log-import.md), *Parser contract*.
- MariaDB lines of the journal and syslog: [MariaDB error log parser](log-parsers-mariadb.md).
- The other parsers: [Plesk and web server error log parsers](log-parsers-web.md),
  [Legacy monitoring log parser](log-parsers-legacy-monitoring.md).
- The fields of a `log_line` record: [Wire format records](wire-format-records.md).
- The agent's live port of the stages: [Agent log shipping](agent-log-shipping.md).

## Implementation

`Vandox.Core.LogParsing`: `JournalExportParser`, `JournalExportReader`, `SyslogParser`, `SyslogLine`, `SyslogClock`
(`ResolveLocal`, `BackwardTolerance` of ten minutes, NodaTime `MapLocal`), `SystemLogGrouper`, `KernelReportGrouper`,
`MailLineClassifier` (`IsPostfix`, `IsDovecot`), `MailEvents`, `DovecotLogParser`, `DovecotLine`, `Utf8Text`. A missing
time zone surfaces as `InvalidOperationException` from `ParseAsync`.
