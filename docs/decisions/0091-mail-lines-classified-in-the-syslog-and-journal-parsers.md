# 0091: Postfix and Dovecot lines are classified in the syslog and journal parsers: priority from the daemon's severity marker, events for fatal, panic and an unavailable database; no parser of its own

- **Status:** Accepted
- **Date:** 2026-10-11
- **Area:** Log import
- **Source:** Issue #18
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

## Decision

Options 2, 5, 9, 11, 13, 14, 16 and 18: the journal and syslog parsers classify the lines of Postfix and Dovecot in a stage before
MariaDB entries and kernel reports, with no parser type of their own. The daemon's severity marker at its anchored
position (only the markers the daemons write to syslog, so no Dovecot `Info: `) sets the priority the daemon gives that class in syslog; fatal and panic lines get `postfix.fatal`,
`postfix.panic`, `dovecot.fatal` or `dovecot.panic`, and the anchored MySQL connection failures get
`postfix.database_unavailable` or `dovecot.database_unavailable`. The rules, and the claim of a mail log by its first line as for every rsyslog file, are in [Log import](../areas/log-import.md)
(*Mail lines (Postfix and Dovecot)*).

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
- Dovecot's own log file (`log_path` set to a file) is not read correctly: its lines have no host, so the syslog grammar
  takes the process prefix for the host and the marker for the program. A follow-up issue covers it.
- Signature detection and outage reconstruction must not treat a mail event or priority as proof that Postfix or Dovecot
  wrote the line (option 14). A marker's priority replaces the transport's (a journal `PRIORITY`, a `<PRI>`), also a
  more severe one; the stored priority is the daemon's class read from the text.
- Database failures logged by Dovecot outside `auth-worker` (SQL quota or dict over the dict process) and lines whose
  `mail_log_prefix` or `deliver_log_format` puts other text first get no database event (options 13, 19).
