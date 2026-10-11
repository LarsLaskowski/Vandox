# 0091: Postfix and Dovecot lines are classified by the daemon's severity marker, with events for fatal, panic and an unavailable database: in the syslog and journal parsers, and in a parser of its own for Dovecot's log file

- **Status:** Accepted
- **Date:** 2026-10-11
- **Area:** Log import
- **Source:** Issues #18, #194
- **Supersedes:** —

## Context

Issue #18 asks that mail service events become structured records: lines assigned to the Postfix sub-services and to
Dovecot, the classes `fatal`, `panic` and `warning` and errors such as lost database connections recognized, and large
mail logs read as a stream. Signature detection (#21, "Postfix/Dovecot fatal errors") and outage reconstruction (#23)
build on the stored records.

What the input is, from the sources of the versions Ubuntu 22.04 ships (Postfix 3.6.4, Dovecot 2.3.16; the versions and
the configuration on the server were not checked):

- Both daemons log through `syslog()` with the facility `mail`. rsyslog's packaged Ubuntu configuration writes `mail.*`
  to `mail.log` and every facility except auth to `syslog` (not checked on the server; Plesk is said to write a
  `maillog` as well, not checked either), and journald stores every line. A `mail.log` is a plain rsyslog file in the
  traditional or the RFC 3339 format, which the syslog parser of
  [0086](0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md) already claims by its first line and
  reads with its program and pid.
- Postfix's program is `<syslog_name>/<daemon>` (`mail_task`, `global/mail_task.c`): `syslog_name` defaults to `postfix`
  or the instance name, which `postmulti` requires to begin with `postfix-`, and the shipped `master.cf` sets
  `postfix/submission`, `postfix/smtps` and `postfix/$service_name`. Dovecot's program is its `instance_name`, by default
  `dovecot` (`master-service.c`, `master-settings.c`), and its process is the first segment of the message
  (`imap-login: `, `auth-worker(2290): `, `imap(user)<pid><session>: ` from the default `mail_log_prefix`).
- Postfix writes `warning: `, `error: `, `fatal: ` or `panic: ` at the start of the message and no marker for info, with
  the syslog levels warning, err, crit and crit (`util/msg_syslog.c`). Dovecot writes `Debug: `, `Warning: `, `Error: `,
  `Fatal: ` or `Panic: ` after the process prefix, no `Info: ` in syslog, with the levels debug, warning, err, crit and
  crit (`lib/failures.c`: `syslog_format`, `log_prefix_add`, `syslog_write`); prefixes an event appends (`mysql(host): `)
  follow the marker (`lib/event-log.c`). So fatal and panic share one syslog priority, and a traditional `mail.log` has
  no `<PRI>` at all: today every line of it is stored without a priority.
- A failed or lost connection to MySQL or MariaDB is logged by Postfix's lookup table as `warning: connect to mysql server
  <host>: <client error>` and `warning: mysql:<file>: query failed: <client error>` (`global/dict_mysql.c`) and by
  Dovecot's SQL driver as `Error: mysql(<host>): Connect failed to database (<db>): <client error> - waiting for N
  seconds before retry` (`mysql: ` without a configured host) and `Warning: sqlpool(mysql): Query failed, retrying:
  <client error>` (`lib-sql/driver-mysql.c`, `driver-sqlpool.c`). An event's own prefix is replaced when set again and a
  pooled connection's event hangs off the pool's parent (`lib/lib-event.c`, `driver-sqlpool.c`), so exactly one driver
  prefix precedes these messages; MySQL is a blocking driver, so its connections and messages live in the
  `auth-worker(<pid>)` processes (`auth/passdb-sql.c`). The
  client error is the text of the client library: `Can't connect to local MySQL server through socket ...`, `Can't
  connect to MySQL server on ...`, `MySQL server has gone away`, `Lost connection to MySQL server ...` in libmysqlclient
  8.0 (`libmysql/errmsg.cc`, 8.0.28), and the same with `server` for `MySQL server` in MariaDB Connector/C 3.3
  (`libmariadb/ma_errmsg.c`, 3.3.1). Whether the server's Postfix or Dovecot uses an SQL lookup at all was not checked.
- Remote text reaches mail log lines: Postfix logs an SMTP client's address syntax in warnings (`Illegal address syntax
  from ... in MAIL command: <...>`) and a remote lookup key right after a MySQL table's name (`mysql:<table> lookup
  error for "<key>"`, `global/maps.c`; `mysql:<table> 'domain' pattern match failed for '<key>'`, `dict_mysql.c`);
  Dovecot logs login user names, user names in auth prefixes (`sql(<user>,<ip>): `) and, through the default
  `deliver_log_format` `msgid=%m: %$` (`lib-lda/lda-settings.c`), a sender's Message-ID after the marker of Sieve
  results (`sieve: msgid=<...>: `), whose quoted local part keeps spaces and colons (`lib-lda/mail-deliver.c`). Dovecot's
  default `auth_username_chars` (letters, digits, `.-_@`) keeps spaces and colons out of a user name; an administrator
  can widen it.

- Dovecot's own log file (issue #194, Dovecot 2.3.16): with `log_path` (or `info_log_path`, `debug_log_path`) set to a
  file, the file handler replaces the syslog handler, so Dovecot writes nothing to syslog or the journal. Each line is
  the `log_timestamp` (default `DEFAULT_FAILURE_STAMP_FORMAT`, `"%b %d %H:%M:%S "`, `lib/failures.h:52`, local time with
  a zero-padded day), the process prefix and the type label, and no host or program (`default_format`, `log_prefix_add`
  in `lib/failures.c`). Unlike the syslog format, the file gets the label for every type, `Info: ` included
  (`syslog_format` leaves only that one out). A prefix the log process receives without a type position gets the label
  first (`Oct 09 03:12:21 Info: ...`, `log/log-connection.c`), and a message longer than a pipe buffer is sent and
  written as several lines that each repeat prefix and label (`internal_send_split`). The syslog parser claims such a file by its
  first line and reads the prefix (`imap-login:`) as the host and the label (`Info`) as the program
  (`SyslogLine.TryParse`, `ParseTag`). Whether the server sets `log_path` was not checked.

## Options considered

Where the classification lives:

1. **A parser `mail` of its own**, claiming `mail.log`, `mail.err`, `maillog` and their rotations by name and files whose
   first line is a Postfix or Dovecot line, registered before `syslog` (the place 0086 kept free for it) — a source type
   that names the mail log. But the format is the syslog format, so its parse would be the syslog parser's with another
   label; the same lines are in `syslog` and in the journal, where they need the classification as well; and bases of a
   continuation and containers of an older copy are matched per source type
   ([0069](0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md)), so a copy of the mail log that one
   parser claims and the other does not (another name with another program on its first line, a rotation Plesk renames)
   would be imported again in full instead of continued or contained, and a `mail.log` imported before would change its
   type.
2. **A stage in the syslog and journal parsers, before MariaDB entries and kernel reports** (chosen) — the place of the
   MariaDB lines of the journal and syslog ([0088](0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md)
   option 21); every source gets the same classification; `mail.log` keeps the source type `syslog`, its file is named in
   `log`.

How a line is attributed to its service:

3. **A new `service` field on `log_line`** — explicit; but an additive field in both languages, the golden batch and the
   schema ([0084](0084-log-line-record-gets-an-optional-host-field.md), 0088 option 3) for what `program` already holds:
   Postfix's program names the daemon, and the issue asks for Dovecot as a whole.
4. **Rewrite the program (`dovecot/imap-login`) or strip the prefix from the message** — no model change; changes the text
   the source wrote ([0021](0021-no-pseudonymization-of-log-data.md)) and differs from the journal's identifier (0088
   option 4).
5. **The program as the source names it** (chosen) — a Postfix line is one whose program begins with `postfix/` or
   `postfix-` and names a daemon after its last `/`, a Dovecot line one whose program is exactly `dovecot`; the program,
   the message and every other field stay as read.

How the severity is stored:

6. **The priority only, mapped as the daemons map it** — fatal and panic both become 2 and cannot be told apart, which the
   issue requires.
7. **The priority with an own mapping (panic 1, fatal 2)** — tells them apart, but the same line then has another priority
   in a mail log than in the journal, which stores the daemon's level, and than the agent will ship (#37).
8. **An event for every class (`postfix.warning`, ...)** — repeats the priority, and a line carries one event, so a warning
   about the database would lose one of them.
9. **The priority as the daemon maps it, replacing the line's own, and an event for fatal and panic** (chosen) — the same
   priority from `mail.log` (which has no `<PRI>`), `syslog` and the journal; the events mark the two classes after which
   the process ends, as `mariadb.abort` does (0088).

Where the marker is looked for:

10. **Anywhere in the message** — remote text in a Postfix warning or a Dovecot login line would forge a fatal or panic
    line from the network.
11. **Where the daemon writes it** (chosen) — Postfix at the start of the message, Dovecot at the start or right after the
    first segment (the text before the first `: `, non-empty and without a space). Remote text reaches that position only
    through a Dovecot user name in the first segment, and the default `auth_username_chars` keeps the `: ` of a marker out
    of it; a value that allows a space and a colon lets a remote user forge a Dovecot marker (documented, option 14). A `mail_log_prefix` with more than one segment, or a Postfix `syslog_name` that does not begin with `postfix`,
    leaves lines unclassified.

Which errors count as a lost database connection:

12. **Any warning or error that contains a client error text** — Postfix logs the client's text in warnings, so a remote
    SMTP client could forge the event.
13. **The four messages of Postfix's and Dovecot's MySQL code, with a client error that begins with a connection text of
    libmysqlclient or MariaDB Connector/C** (chosen) — anchored like the markers and read only in the exact form the
    daemons write: Postfix's `connect to mysql server <host>: ` and `mysql:<table>: query failed: ` with a host or table
    name that holds no space before the first `: `; Dovecot's `Connect failed to database (` and `Query failed,
    retrying: ` only on `auth-worker(<pid>)` lines and only after one to three of the driver's fixed segments
    (`mysql: `, `mysql(<host>): `, `sqlpool(<driver>): `). Under the default `auth_username_chars` no remote text
    reaches these positions: Postfix's remote keys follow a space, a Message-ID is no driver segment, a user name in the
    default auth prefix (`sql(<user>,<ip>): `) is none either, and a forged first segment begins with the logging
    process, never `auth-worker(<pid>)`. One configuration is the exception: a passdb or userdb whose `name` setting is
    `mysql` or `sqlpool` writes its auth prefix as `<name>(<user>,<ip>): ` (`auth/auth-request.c:2435-2443`), so with an
    `auth_username_chars` that allows a space a remote user name such as `a): Connect failed to database (d): Server
    has gone away` reads as a driver segment and an anchor and yields `dovecot.database_unavailable` (documented,
    option 14). Covers both client libraries
    whichever the packages link, and leaves access and syntax errors without the event; PostgreSQL, other drivers and the
    dict process's SQL lines (`conn ...: ` prefix) are not read (the server runs MariaDB; auth is where a lost database
    stops mail).

Forged lines:

14. **Accept and document** (chosen) — as for kernel and MariaDB lines (0086 option 19, 0088 option 36), any local process
    can log under the name `postfix/smtpd` or `dovecot` (`logger -t`, Postfix's own `postlog -t`, `openlog()` in a PHP
    script) with any marker; a priority or event is a classification of text, not proof that Postfix or Dovecot wrote the
    line. Remote users can forge in two non-default Dovecot configurations only: an `auth_username_chars` that allows a
    space and a colon forges a marker through the first segment (option 11), and the same with a passdb or userdb named
    `mysql` or `sqlpool` also forges `dovecot.database_unavailable` (option 13).

Which markers are read:

15. **Also Dovecot's `Info: ` (priority 6)** — complete against Dovecot's list of log types; but Dovecot's `syslog_format`
    leaves `Info: ` out (`lib/failures.c`), so in syslog and the journal the marker never comes from Dovecot, and the
    mapping would only fire on text that merely looks like it (a journal error whose message reads `x: Info: ...`),
    lowering the priority the source gave the line. It belongs to Dovecot's own log file, a follow-up.
16. **Only the markers the daemons write to syslog** (chosen) — Postfix `warning: `, `error: `, `fatal: `, `panic: `;
    Dovecot `Debug: `, `Warning: `, `Error: `, `Fatal: `, `Panic: `. An info line keeps the priority its source gave it.

How a mail log is claimed:

17. **By name too: `mail.log`, `mail.err`, `maillog` and their rotations as syslog names** (`SyslogParser.HasSyslogName`)
    — would also claim a copy whose first line is cut; but 0086 (option 2) claims every rsyslog file other than `syslog`
    and `kern.log` by its first line only (`auth.log`, `daemon.log` alike), rsyslog writes whole lines and a rotation
    begins with one, so only a manual partial copy (`tail -c`) starts mid-line; the function is also the list of names the
    MariaDB parser refuses, which would widen with it; and `maillog` was not checked on the server.
18. **By the first line, as every rsyslog file** (chosen) — no change to the claim; a mail log whose first line is cut is
    listed as not recognized instead of being read.

How the database messages are anchored (added after the security review of the plan):

19. **Dovecot: zero or more segments of any non-empty text without a space before the anchor** — tolerant of any prefix
    chain, but steerable from the internet: a sender's Message-ID `<"a: Query failed, retrying: MySQL server has gone
    away"@x>` yields `lmtp(...): Error: sieve: msgid=<a: Query failed, retrying: MySQL server has gone away@x>: ...`, the
    walk accepts `sieve` and `msgid=<a` and forges `dovecot.database_unavailable` on any Sieve error or warning; a
    widened `auth_username_chars` re-opens the same path through `sql(<user>,<ip>): `. The chosen fixed segments close
    that path for the default auth prefix, not for a passdb or userdb named `mysql` or `sqlpool`, whose prefix
    `mysql(<user>,<ip>): ` begins like a driver segment (accepted, options 13 and 14). Zero segments was also rejected:
    no Dovecot 2.3.16 database message lacks a driver segment.
20. **Postfix: `mysql:` at the start and `: query failed: ` anywhere after it** — matches the real line, but also
    `mysql:<table> lookup error for "<key>"` with a remote key such as `a: query failed: MySQL server has gone away@x`,
    forging `postfix.database_unavailable` from an SMTP recipient.

How Dovecot's own log file is read (issue #194):

21. **Refuse it: the syslog parser declines a Dovecot-shaped first line, and the file is listed as not recognized** — the
    smallest change and no wrong records; but a server with `log_path` set to a file has its Dovecot history only there,
    so the lines this record classifies (fatal, panic, the `auth-worker` database failures) could not be imported at all.
22. **A host-less line form inside the syslog parser** — no new source type; but the syslog grammar would become
    ambiguous on every line of every rsyslog file (a host token that ends in `:`), `Info: ` would have to be read there
    (option 15), and the source type `syslog` would name a format the file does not have.
23. **A parser `dovecot` of its own** (chosen) — the format is not the syslog format, so it gets its own type, as the
    MariaDB error log did ([0088](0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md)); it
    reuses the traditional time stamp, the year and zone rules ([0085](0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md))
    and the Dovecot rules of this record, and goes between the MariaDB and the syslog parser (0086).

How it is claimed:

24. **By name (`dovecot.log`, `dovecot-info.log`)** — `log_path` is free, and copies and archives keep any layout.
25. **By its first line, never for a syslog name** (chosen) — a content match that beats the syslog parser's weak claim;
    as for the MariaDB error log (0088), a file named `syslog` or `kern.log` stays with the syslog parser whatever it
    holds, and only the first line is looked at.

Which lines it reads:

26. **Every line with the time stamp, the marker optional** — would keep the lines of a `mail_log_prefix` that holds a
    space; but every foreign line with a time stamp (a syslog line in a file both write) would be stored as Dovecot's,
    with its host and tag inside the message.
27. **The time stamp and a Dovecot marker at the position of option 11** (chosen) — the same grammar as the claim; every
    line Dovecot writes to the file carries its label at that position under the default prefixes, and any other line
    is skipped as "not a Dovecot line", visible in the summary.

Which fields it sets:

28. **Program `dovecot`, as in syslog and the journal** — one program for every source; but the file does not name the
    instance (`instance_name` can be another), and 0088 (option 18) sets nothing the format does not carry.
29. **Program, host and pid empty, source type `dovecot`** (chosen) — the source type and the events name the file's
    owner; the message keeps prefix and label as written ([0021](0021-no-pseudonymization-of-log-data.md)).

Which markers it reads:

30. **`Info: ` (priority 6) in this parser only, with the other five Dovecot markers** (chosen) — option 15 kept `Info: `
    out of syslog and the journal because Dovecot never writes it there and it would lower a transport's priority on
    text that merely looks like it; in the own log file every line carries its label and there is no transport
    priority, so neither objection applies. The syslog and journal parsers keep option 16.

## Decision

Options 2, 5, 9, 11, 13, 14, 16 and 18: the journal and syslog parsers classify the lines of Postfix and Dovecot in a stage before
MariaDB entries and kernel reports, with no parser type of their own. The daemon's severity marker at its anchored
position (only the markers the daemons write to syslog, so no Dovecot `Info: `) sets the priority the daemon gives that class in syslog; fatal and panic lines get `postfix.fatal`,
`postfix.panic`, `dovecot.fatal` or `dovecot.panic`, and the anchored MySQL connection failures get
`postfix.database_unavailable` or `dovecot.database_unavailable`. Options 23, 25, 27, 29 and 30: Dovecot's own log file
is read by a parser `dovecot`, claimed by its first line and never under a syslog name, with the Dovecot rules plus
`Info: ` (6). The rules, and the claim of a mail log by its first line as for every rsyslog file, are in [Log import](../areas/log-import.md)
(*Mail lines (Postfix and Dovecot)* and *Dovecot log file*).

## Consequences

- `mail.log` keeps the source type `syslog`. Mail records are selected by program and event, the file by `log`.
- The stage only sets the priority and the event of a record, never adds, drops or reorders one, and holds no state, so
  resuming by count is unaffected and memory does not depend on the input. Contents imported before keep their
  unclassified records (an import is idempotent per content and a changed parser version is not noticed); a file whose
  interrupted import is resumed by a version with this stage has unclassified records before the resume point and
  classified ones after it. No release has imported mail logs before this change (no version tag exists).
- Importing `mail.log`, `syslog` and a journal export of the same period stores each mail line up to three times with the
  same classification (0086); signature detection (#21) must not count such an event more than once.
- An OOM kill of a mail process (`process ... killed by signal 9` from Postfix's master, `service(imap): child N killed
  with signal 9` from Dovecot's) gets the priority of its warning or error only; recognizing it is #21's.
- Dovecot's own log file is imported with the source type `dovecot`; a query for Dovecot's lines selects by program
  `dovecot` (syslog, journal) or source type `dovecot`, or by event. Its lines appear in no other source (option 21), so
  they are never stored twice.
- A Dovecot log under a syslog name, a copy cut inside its first line, and a file written with another `log_timestamp`
  are listed as not recognized or stay with the syslog parser; lines of a `mail_log_prefix` or user name with a space,
  and foreign lines in the file, are skipped as "not a Dovecot line". Any file whose first line has the shape is read as
  Dovecot's (as for MariaDB, 0088). In the real file the forging limits of option 14 hold for the text Dovecot's
  processes log (remote text through a widened `auth_username_chars`); `logger -t dovecot` does not reach it, since only
  Dovecot's log process writes it.
- Signature detection and outage reconstruction must not treat a mail event or priority as proof that Postfix or Dovecot
  wrote the line (option 14). A marker's priority replaces the transport's (a journal `PRIORITY`, a `<PRI>`), also a
  more severe one; the stored priority is the daemon's class read from the text.
- Database failures logged by Dovecot outside `auth-worker` (SQL quota or dict over the dict process) and lines whose
  `mail_log_prefix` or `deliver_log_format` puts other text first get no database event (options 13, 19).
