# Plan: Mail log lines of Postfix and Dovecot become classified records

Source: [spec.md](spec.md) (issue #18)
Status: Draft
Tier: security — the change adds parsing rules for external input (log files) in `Vandox.Core.LogParsing`, security area
10 of `.squad/project.md` (*Parsing of external input*, which names mail logs).

## Problem / root cause

Summary of `spec.md`: `vandoxd import` already reads `mail.log` (and the same lines in `syslog` and a journal export), but
recognizes nothing in Postfix and Dovecot lines. The feature adds a per-line classification of these lines to the syslog
and journal parsers: the daemon's severity marker sets the priority, fatal and panic lines and failed or lost MySQL
connections get an event. No parser type of its own (record 0091, options 1 and 2).

Claims of the issue, checked:

- *"Assign lines to Postfix sub-services and Dovecot."* — **Already provided by the syslog grammar for the program, confirmed:**
  `SyslogLine.ParseTag` (`src/Vandox.Core/LogParsing/SyslogLine.cs`, the loop that stops only at space, `[`, `]` and `:`)
  keeps `/`, so `postfix/smtpd[2210]:` gives program `postfix/smtpd`, pid 2210 (pinned by
  `SyslogLineTests` row 26 and `SyslogParserTests` row 129); the journal takes `SYSLOG_IDENTIFIER`
  (`JournalExportParser.Program`). Postfix composes the program as `<syslog_name>/<daemon>` (`global/mail_task.c`,
  Postfix 3.6.4), Dovecot logs under its `instance_name`, default `dovecot` (`lib-master/master-service.c:482`,
  `master/master-settings.c:209`, Dovecot 2.3.16), with the process as the first message segment. What is missing is
  any use of it: no line is classified.
- *"Recognize fatal, panic, warning and errors such as lost database connections."* — **Not done today, confirmed:**
  `SyslogParser.Map` sets `Priority = line.Priority`, which is `null` without `<PRI>`, and a traditional `mail.log` has
  none; no event is set for any program but `mariadbd`/`mysqld`. Postfix writes `warning: `, `error: `, `fatal: `,
  `panic: ` at the start of the message with the syslog levels warning, err, crit, crit (`util/msg_syslog.c:162-181`);
  Dovecot writes `Debug: `, `Warning: `, `Error: `, `Fatal: `, `Panic: ` after the process prefix, never `Info: ` in
  syslog, with debug, warning, err, crit, crit (`lib/failures.c:25-32`, `125-139`, `143-169`, `336-352`;
  `lib/event-log.c:350-355` puts appended prefixes after the marker). **Fatal and panic share one priority**, so the
  priority alone cannot satisfy the acceptance criterion (record 0091 option 6). The database messages are in
  `global/dict_mysql.c:568` and `:699` (Postfix) and `lib-sql/driver-mysql.c:163`, `driver-sqlpool.c:694` (Dovecot);
  the client error texts in `libmysql/errmsg.cc` (MySQL 8.0.28) and `libmariadb/ma_errmsg.c` (Connector/C 3.3.1).
  *Unverified:* whether the server's Postfix or Dovecot uses an SQL lookup at all (Plesk's configuration is not in the
  repository), and which client library its packages link (both are covered).
- *Prefixes before the database messages (checked after the security review, Dovecot 2.3.16).* An event's own prefix is
  **replaced**, not extended, when it is set again (`event_set_log_prefix`, `lib/lib-event.c:510-528`): the MySQL
  connection's event gets `mysql: ` at creation (`driver-mysql.c:294`) and `mysql(<host>): ` instead once a `host=` is
  configured (`:116`). A pooled connection's event has the pool's *parent* as parent, not the pool's event
  (`driver-sqlpool.c:290-293`), and the parent in the auth process, `auth_event`, has no prefix (`auth/auth.c:381`,
  `auth/db-sql.c:113`). So Dovecot writes `Connect failed to database (` after exactly `mysql(<host>): ` or `mysql: `,
  and `Query failed, retrying: ` (on the pool's event, `driver-sqlpool.c:694`) after exactly `sqlpool(mysql): `
  (`:527`). **The security review's chains `mysql: mysql(localhost): ` and `sqlpool(mysql): mysql: mysql(localhost): `
  are refuted** for this version; the plan's former fixture line `Warning: mysql(localhost): Query failed, retrying`
  was wrong too and now reads `Warning: sqlpool(mysql): Query failed, retrying`. MySQL is a blocking driver
  (`driver-mysql.c:766`), so its connections live in the `auth-worker` processes only (`auth/passdb-sql.c:282-285`),
  whose process prefix is `auth-worker(<pid>): ` (`lib-master/master-service.c:528-531`, set before the databases are
  initialized, `auth/main.c:382`). The dict process logs SQL errors under its connection's prefix `conn ...: `
  (`lib/connection.c:355-363`), which the rule does not read (*Out of scope*).
- *Remote text near the anchors (security review round 1, checked).* Postfix logs a remote lookup key right after
  `mysql:<table>`: `"%s:%s lookup error for \"%s\""` (`global/maps.c:210`, `:275`) and `"%s:%s 'domain' pattern match
  failed for '%s'"` (`global/dict_mysql.c:362`); its own query failure is `"%s:%s: query failed: %s"` (`:568`), the
  connect failure `"connect to mysql server %s: %s"` (`:699`) with a host from `hosts`, which is split at commas and
  spaces (`:793`) and may read `unix:<path>` or `inet:<host>:<port>` (`:880-888`). Dovecot applies
  `deliver_log_format`, default `msgid=%m: %$` (`lib-lda/lda-settings.c:42`), to Sieve result messages, so a
  sender-chosen Message-ID (sanitized to 200 bytes, spaces kept, `lib-lda/mail-deliver.c:124`) follows the marker of an
  `lmtp(...)` line behind `sieve: msgid=<`. Both confirmed by reading; the Pigeonhole lines were not re-read (source
  not at hand), they only add a second path of the same kind.
- *"Streaming; mail logs can be large."* — **Confirmed for the existing path:** `SyslogParser.ParseAsync` reads through
  `LogLineReader` (16 KiB line bound) and keeps only the grouper state; the new stage holds no state. AC4 pins it.
- *"Mail delivery stopped during the outages. mail.log shows the effect on Postfix and Dovecot."* — *unverified*: no log of
  the server is in the repository.
- *Dependency #15* — confirmed merged (`src/Vandox.Import`, `ParserRegistry`, `BuiltInParsers`).
- Packaging claims used in record 0091 (rsyslog writes `mail.*` to `mail.log` and to `syslog`; Plesk's `maillog`) are
  marked there as not checked on the server; nothing in the plan depends on a file name.

**Related defect found on the way (not fixed here, follow-up issue):** Dovecot's own log file (`log_path` set to a file;
format `"%b %d %H:%M:%S "` from `DEFAULT_FAILURE_STAMP_FORMAT`, `lib/failures.h:52`, then the prefix and the marker,
`Info: ` included) has no host. The syslog parser claims such a file by its first line and misreads every line: in
`Oct 11 12:00:00 imap-login: Info: Login: user=<a@example.org>` the host becomes `imap-login:` and the program `Info`
(`SyslogLine.TryParse`, host = text up to the next space; `ParseTag`, program up to `:`). Settled by reading.

## Acceptance criteria

Every row of the tables below is one test case (`DataRow`) unless it says otherwise. "Unchanged" means priority and event
exactly as the parser read them and the message byte-equal to the input.

**AC1 — attribution.**

- [ ] AC1.1 `MailLineClassifier.IsPostfix` is `true` for `postfix/smtpd`, `postfix/qmgr`, `postfix/submission/smtpd`,
  `postfix/smtps/smtpd`, `postfix-out/smtp`, `postfix/postfix-script`; `false` for `postfix`, `postfix/`,
  `postfix/submission/`, `postfix-out`, `postfixadmin`, `postfixadmin/x` (no `postfix/` or `postfix-` prefix),
  `Postfix/smtpd`, `mypostfix/smtpd`, `dovecot`, the empty string.
- [ ] AC1.2 `MailLineClassifier.IsDovecot` is `true` for `dovecot` only; `false` for `Dovecot`, `dovecot-lda`,
  `dovecot/imap`, `imap-login`, `dovecot ` (trailing space), the empty string.
- [ ] AC1.3 The syslog parser reads the fixture `testdata/logs/mail.log` (below) into one record per line, in file order,
  with source `syslog`, `log` the given file name, program and pid as written (`postfix/submission/smtpd`, pid 2214;
  `dovecot`, pid 0) and the message as written.
- [ ] AC1.4 Lines of other programs keep priority and event unchanged even when their message carries a marker:
  `psa-pc-remote[1102]: Error: ...`, `spamd[900]: fatal: ...`, `opendkim[77]: warning: ...`, `kernel: Panic: ...`.
- [ ] AC1.5 In the built-in registry, the heads `Oct  9 03:12:01 mail-1 postfix/master[812]: daemon started -- ...`
  (traditional) and `2026-10-09T03:12:01.123456+02:00 mail-1 postfix/master[812]: daemon started -- ...` (RFC 3339) under
  the names `mail.log`, `mail.log.1`, `maillog` and `mail.err` are detected as `syslog` (no other parser claims them).
- [ ] AC1.6 The mail names are not syslog names (record 0091 options 17 and 18): `SyslogParser.HasSyslogName` is `false` for
  `mail.log`, `mail.log.1`, `mail.err`, `maillog`; in the built-in registry the head `ail-1 postfix/smtpd[2210]: connect
  from unknown[192.0.2.9]\n` (first line cut, no syslog header) under the name `mail.log` is claimed by no parser
  (`Confidence.NoMatch`), as under `auth.log`.

**AC2 — severity (`MailLineClassifier.Classify` on a `LogLine` with the given program and message, priority before
classification `null`, and once more `6` for the replacement case).**

- [ ] AC2.1 Postfix (program `postfix/smtpd`):

  | Message | Priority | Event |
  | ------- | -------- | ----- |
  | `warning: hostname mx.example.net does not resolve to address 192.0.2.9` | 4 | — |
  | `error: open database /etc/postfix/x.db: No such file or directory` | 3 | — |
  | `fatal: the Postfix mail system is not running` | 2 | `postfix.fatal` |
  | `panic: myfree: corrupt or unallocated memory block` | 2 | `postfix.panic` |
  | `warning: ` (marker, empty text) | 4 | — |
  | `connect from unknown[192.0.2.9]` | unchanged | — |
  | `Warning: x`, `FATAL: x`, `Fatal: x`, `fatal:x`, ` fatal: x`, `x fatal: y`, `info: x` | unchanged | — |
  | `warning: Illegal address syntax from unknown[192.0.2.9] in MAIL command: <fatal: x>` | 4 | — |

- [ ] AC2.2 Dovecot (program `dovecot`):

  | Message | Priority | Event |
  | ------- | -------- | ----- |
  | `auth: Debug: Loading modules from directory: /usr/lib/dovecot/modules/auth` | 7 | — |
  | `master: Warning: Killed with signal 15 (by pid=1 uid=0 code=kill)` | 4 | — |
  | `master: Error: service(imap): child 2301 killed with signal 9` | 3 | — |
  | `auth: Fatal: master: service(auth): child 2290 returned error 89` | 2 | `dovecot.fatal` |
  | `imap(alice@example.org)<2301><AbCdEfGh>: Panic: file mail-index.c: line 42 (mail_index_open): assertion failed: (index != NULL)` | 2 | `dovecot.panic` |
  | `Fatal: Dovecot is already running` (marker at the start) | 2 | `dovecot.fatal` |
  | `imap-login: Login: user=<alice@example.org>, method=PLAIN, rip=198.51.100.7` | unchanged | — |
  | `imap-login: Disconnected (auth failed, 1 attempts in 2 secs): user=<x: Panic: y>, rip=192.0.2.9` | unchanged | — |
  | `imap-login: Disconnected: Panic: x` (marker after the second segment) | unchanged | — |
  | `a b: Panic: x` (first segment with a space), `: Panic: x` (empty first segment) | unchanged | — |
  | `auth: panic: x`, `auth: PANIC: x`, `fatal: x`, `auth: Panic:x` | unchanged | — |
  | `master: Info: x`, `Info: x`, `auth: info: x` (no `Info` marker in syslog) | unchanged | — |

- [ ] AC2.3 With a priority already set (6, as from the journal or a `<PRI>`), every row with a marker sets the table's
  priority and every row without one keeps 6. Also with priority 3 (an error from the journal): `master: Info: x` keeps
  3 (an `Info: ` text never lowers a priority). The event of a row without a marker stays empty. The message, program,
  pid, host, `truncated` and `log` are never changed (compare the whole payload against a copy).

**AC3 — unavailable database (`Classify`; Postfix rows with program `postfix/proxymap`, Dovecot rows with `dovecot`).**

- [ ] AC3.1 Each of the eight client phrases (table *Database rule* below) after each of the four anchors gives the event
  with the marker's priority (32 cases), e.g. `warning: connect to mysql server localhost: Can't connect to local MySQL
  server through socket '/var/run/mysqld/mysqld.sock' (2)` → 4, `postfix.database_unavailable`;
  `warning: mysql:/etc/postfix/mysql-virtual.cf: query failed: Lost connection to MySQL server during query` → 4, same;
  `auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Can't connect to local server through
  socket '/run/mysqld/mysqld.sock' (2) - waiting for 1 seconds before retry` → 3, `dovecot.database_unavailable`;
  `auth-worker(2290): Warning: sqlpool(mysql): Query failed, retrying: MySQL server has gone away` → 4, same. Further
  rows, one case each:

  | Program | Message | Priority | Event |
  | ------- | ------- | -------- | ----- |
  | `postfix/proxymap` | `error: connect to mysql server localhost: Lost connection to MySQL server at 'reading initial communication packet'` | 3 | `postfix.database_unavailable` |
  | `postfix/proxymap` | `warning: connect to mysql server unix:/var/run/mysqld/mysqld.sock: Can't connect to local MySQL server through socket '/var/run/mysqld/mysqld.sock' (2)` | 4 | `postfix.database_unavailable` |
  | `postfix/proxymap` | `warning: connect to mysql server inet:db.example.org:3306: Can't connect to MySQL server on 'db.example.org' (111)` | 4 | `postfix.database_unavailable` |
  | `dovecot` | `auth-worker(2290): Error: mysql: Connect failed to database (mail): Can't connect to local server through socket '/run/mysqld/mysqld.sock' (2) - waiting for 1 seconds before retry` (no `host=`) | 3 | `dovecot.database_unavailable` |
  | `dovecot` | `auth-worker(2290): Error: mysql(/run/mysqld/mysqld.sock): Connect failed to database (mail): Server has gone away` | 3 | `dovecot.database_unavailable` |
  | `dovecot` | `auth-worker(2290): Error: sqlpool(mysql): mysql(localhost): Connect failed to database (mail): Server has gone away` (two segments) | 3 | `dovecot.database_unavailable` |
  | `dovecot` | `auth-worker(2290): Error: sqlpool(mysql): mysql: mysql(localhost): Connect failed to database (mail): Server has gone away` (three segments) | 3 | `dovecot.database_unavailable` |
  | `dovecot` | `auth-worker(2290): Warning: mysql(localhost): Query failed, retrying: MySQL server has gone away` (another accepted segment) | 4 | `dovecot.database_unavailable` |
- [ ] AC3.2 No database event (the marker's priority still set):

  | Program | Message | Priority |
  | ------- | ------- | -------- |
  | `postfix/proxymap` | `warning: connect to mysql server localhost: Access denied for user 'postfix'@'localhost'` | 4 |
  | `postfix/proxymap` | `warning: mysql:/etc/postfix/x.cf: query failed: You have an error in your SQL syntax` | 4 |
  | `postfix/proxymap` | `warning: connect to pgsql server localhost: could not connect` | 4 |
  | `postfix/proxymap` | `warning: connect to mysql server localhost` (no `: ` after the host) | 4 |
  | `postfix/smtpd` | `warning: Illegal address syntax from unknown[192.0.2.9] in MAIL command: <MySQL server has gone away>` | 4 |
  | `postfix/smtpd` | `warning: x connect to mysql server h: Can't connect to MySQL server on 'h' (111)` | 4 |
  | `postfix/proxymap` | `warning: connect to mysql server localhost: can't connect to MySQL server` (lower case) | 4 |
  | `postfix/proxymap` | `connect to mysql server localhost: Can't connect to MySQL server on 'h' (111)` (no marker) | unchanged |
  | `dovecot` | `auth-worker(1): Error: user x: Connect failed to database (d): Can't connect to MySQL server on 'h' (111)` (segment with a space) | 3 |
  | `dovecot` | `auth-worker(1): Error: mysql(localhost): Connect failed to database (mail): Access denied for user 'mail'` | 3 |
  | `dovecot` | `auth-worker(1): Error: mysql(localhost): Connect failed to database (mail` (no `): `) | 3 |
  | `dovecot` | `auth-worker(1): Error: Query failed, aborting: SELECT password FROM users` | 3 |
  | `dovecot` | `auth-worker(1): mysql(localhost): Connect failed to database (mail): Server has gone away` (no marker) | unchanged |
  | `dovecot` | `auth-worker(1): Debug: mysql(localhost): Query failed, retrying: Server has gone away` (debug) | 7 |
  | `postfix/smtpd` | `warning: mysql:/etc/postfix/x.cf lookup error for "a: query failed: MySQL server has gone away@x"` (remote key, `maps.c:210`) | 4 |
  | `postfix/trivial-rewrite` | `warning: mysql:/etc/postfix/x.cf 'domain' pattern match failed for 'a: query failed: MySQL server has gone away@x'` (remote key, `dict_mysql.c:362`) | 4 |
  | `postfix/smtpd` | `warning: mysql:/etc/postfix/x.cf lookup error for ""a: query failed: MySQL server has gone away"@x"` (key with its quotes) | 4 |
  | `postfix/proxymap` | `warning: mysql:: query failed: MySQL server has gone away` (empty table name) | 4 |
  | `postfix/proxymap` | `warning: mysql:/etc/postfix/x.cf: query failed:MySQL server has gone away` (no space after the anchor) | 4 |
  | `postfix/proxymap` | `warning: connect to mysql server : MySQL server has gone away` (empty host) | 4 |
  | `postfix/proxymap` | `warning: connect to mysql server a b: Can't connect to MySQL server on 'h' (111)` (host with a space) | 4 |
  | `dovecot` | `lmtp(alice@example.org)<2301><AbCdEfGh>: Error: sieve: msgid=<a: Query failed, retrying: MySQL server has gone away@x>: failed to store into mailbox 'INBOX': Quota exceeded (mailbox for user is full)` (sender's Message-ID) | 3 |
  | `dovecot` | `lmtp(alice@example.org)<2301><AbCdEfGh>: Error: sieve: msgid=<a@x>: Connect failed to database (x): Server has gone away` | 3 |
  | `dovecot` | `auth-worker(1): Error: sieve: msgid=<a@x>: Connect failed to database (x): Server has gone away` (right process, foreign segments) | 3 |
  | `dovecot` | `auth: Error: sql(a,1.2.3.4): Connect failed to database (mail): Server has gone away` | 3 |
  | `dovecot` | `auth-worker(1): Error: sql(a,1.2.3.4): Connect failed to database (mail): Server has gone away` | 3 |
  | `dovecot` | `imap(alice@example.org)<2301><AbCdEfGh>: Error: mysql(localhost): Connect failed to database (mail): Server has gone away` (not an `auth-worker` process) | 3 |
  | `dovecot` | `Error: mysql(localhost): Connect failed to database (mail): Server has gone away` (marker at the start, no process) | 3 |
  | `dovecot` | `auth-worker: Error: mysql(localhost): ...`, `auth-worker(): Error: ...`, `auth-worker(12a): Error: ...`, `xauth-worker(1): Error: ...`, each continued with `mysql(localhost): Connect failed to database (mail): Server has gone away` | 3 |
  | `dovecot` | `auth-worker(1): Error: Connect failed to database (mail): Server has gone away` (zero segments) | 3 |
  | `dovecot` | `auth-worker(1): Error: sqlpool(mysql): mysql: mysql: mysql(localhost): Connect failed to database (mail): Server has gone away` (four segments) | 3 |
  | `dovecot` | `auth-worker(1): Error: mysql(): Connect failed ...`, `mysql(local host): ...`, `mysql(a)b): ...`, `sqlpool(): ...`, `pgsql(localhost): ...`, `Mysql(localhost): ...`, `mysql : ...`, each continued with `Connect failed to database (mail): Server has gone away` | 3 |

- [ ] AC3.3 Fatal and panic win: `fatal: connect to mysql server localhost: Can't connect to MySQL server on 'h' (111)` →
  2, `postfix.fatal`; `auth-worker(1): Panic: sqlpool(mysql): Query failed, retrying: MySQL server has gone away` → 2,
  `dovecot.panic`.

**AC4 — streaming (heap-bound test, `SyslogParserTests`).**

- [ ] AC4 The syslog parser reads a mail log of 16 MiB built with `PatternStream` (header empty, pattern = one block of the
  fixture's Postfix and Dovecot lines with at least one line of each event, of priority 4 and without classification;
  trailer empty, the pattern length a whole number of blocks) through `CountingEmitter`. Asserted: no skip; the record
  count and the count per event and per priority equal the per-block counts times the number of blocks; the managed
  memory retained at the last record (`GC.GetTotalMemory(true)` through the one `RetainedBytes` helper with the S1215
  pragma, `.squad/stack.md` *Writing tests*) exceeds that at the 1,000th record by less than 4 MiB. A parser or emitter
  that kept every record (at least 200 bytes each, about 100,000 records) retains more than 19 MiB and fails.

**AC5 — same classification from every source.**

- [ ] AC5.1 The journal parser classifies like the syslog parser: entries with `SYSLOG_IDENTIFIER=postfix/smtpd`,
  `PRIORITY=6`, `MESSAGE=fatal: the Postfix mail system is not running` → priority 2, `postfix.fatal`;
  `SYSLOG_IDENTIFIER=dovecot`, `PRIORITY=2`, `MESSAGE=auth-worker(2290): Error: mysql(localhost): Connect failed to database
  (mail): Can't connect to MySQL server on 'db' (111) - waiting for 1 seconds before retry` → priority 3,
  `dovecot.database_unavailable`; `SYSLOG_IDENTIFIER=dovecot`, `PRIORITY=6`, `MESSAGE=imap-login: Login: user=<a>` →
  priority 6, no event.
- [ ] AC5.2 For every line of the fixture, the record the syslog parser emits and the record the journal parser emits for
  an entry with the same program, pid and message have the same priority and event, except where the line has no marker
  (the journal's own priority is kept).
- [ ] AC5.3 `SystemLogGrouper` classifies before the MariaDB and kernel stages and passes mail records through in input
  order, one record out per record in, also when they are interleaved with an open MariaDB entry and an open kernel report
  of the same host (the order of 0086/0088 is unchanged for the other records).

## Approach

1. **`MailEvents`** (public, like `MariaDbEvents`): the six event names as constants.
2. **`MailLineClassifier`** (internal static, ordinal and case-sensitive span operations on the decoded message, no
   regular expression, no per-line allocation, no exception):
   - `IsPostfix(program)`: begins with `postfix/` or `postfix-`, contains `/`, and the character after the last `/` exists.
   - `IsDovecot(program)`: equals `dovecot`.
   - `Classify(payload)`: does nothing unless `IsPostfix` or `IsDovecot`. Finds the marker (rules below); none → returns
     unchanged. Otherwise sets `Priority` to the marker's priority; for fatal or panic sets `Event` to the family's
     fatal/panic event; for warning or error sets `Event` to the family's database event when the database rule matches;
     never touches any other field, and never clears an event.
3. **`SystemLogGrouper.Add`**: calls `MailLineClassifier.Classify` on the record's `LogLine` payload before the MariaDB
   stage. No other change: `SyslogParser`, `JournalExportParser`, `BuiltInParsers` and the registry stay as they are.

**Marker rule.**

| Family | Position | Marker → priority (event) |
| ------ | -------- | ------------------------- |
| Postfix | the message begins with it | `warning: ` → 4, `error: ` → 3, `fatal: ` → 2 (`postfix.fatal`), `panic: ` → 2 (`postfix.panic`) |
| Dovecot | the message begins with it, or the message is `S: ` + marker + text where `S`, the text before the first `: `, is non-empty and holds no space (U+0020) | `Debug: ` → 7, `Warning: ` → 4, `Error: ` → 3, `Fatal: ` → 2 (`dovecot.fatal`), `Panic: ` → 2 (`dovecot.panic`) |

The text after the marker is called *T* below. Markers include their trailing space. For Dovecot the start of the
message is checked first (`Fatal: Panic: x` is fatal). Only the markers the daemons write to syslog are read: Dovecot's
`syslog_format` leaves out `Info: ` (`lib/failures.c`), so `Info: ` is no marker here and a line carrying it keeps its
priority (record 0091 options 15 and 16; Dovecot's own log file, where `Info: ` occurs, is the follow-up).

**Database rule** (only for warning and error markers). The client error *C* starts with one of these eight phrases
(ASCII apostrophe U+0027, case-sensitive):

| # | Phrase | Source |
| - | ------ | ------ |
| 1 | `Can't connect to local MySQL server` | libmysqlclient 8.0, 2002 |
| 2 | `Can't connect to MySQL server` | libmysqlclient 8.0, 2003 |
| 3 | `MySQL server has gone away` | libmysqlclient 8.0, 2006 |
| 4 | `Lost connection to MySQL server` | libmysqlclient 8.0, 2013 and 2055 |
| 5 | `Can't connect to local server` | Connector/C 3.3, 2002 |
| 6 | `Can't connect to server` | Connector/C 3.3, 2003 |
| 7 | `Server has gone away` | Connector/C 3.3, 2006 |
| 8 | `Lost connection to server` | Connector/C 3.3, 2013 and 2055 |

| Family | Anchor in *T* | *C* |
| ------ | ------------- | --- |
| Postfix | *T* is `connect to mysql server ` + *H* + `: ` + *C*, where *H* is the text up to the first `: ` after the anchor, non-empty and without a space (U+0020) | as written; no `: ` after the anchor → no event |
| Postfix | *T* is `mysql:` + *N* + `: query failed: ` + *C*, where *N* is the text up to the first `: ` after `mysql:`, non-empty and without a space | as written |
| Dovecot | the message begins with `auth-worker(` + one or more ASCII digits + `): ` + marker, and *T* is one to three driver segments + `Connect failed to database (` + … | the text after the first `): ` that follows the anchor; none → no event |
| Dovecot | as above, with `Query failed, retrying: ` + *C* after the segments | as written |

A **driver segment** is exactly `mysql: `, or `mysql(` + *P* + `): `, or `sqlpool(` + *P* + `): `, where *P* is the text up
to the first `)` after the opening parenthesis, non-empty and without a space; anything else (another name, another
case, an empty or spaced *P*, a `)` not followed by `: `) ends the walk without an event. The walk reads at most three
segments, each consumed by a prefix comparison, so it is linear in the message length; zero segments and a fourth
segment give no event. Dovecot 2.3.16 writes exactly one segment (`mysql(<host>): ` or `mysql: ` before `Connect failed`,
`sqlpool(mysql): ` before `Query failed`, see the claim check); up to three are accepted because each is a fixed driver
text with a configured host or driver name in it, never remote text, so a version that nests them is still read. The
`auth-worker(<pid>)` condition is the process that holds Dovecot's MySQL connections; it keeps lines of `lmtp`, `imap`
and the other processes that log remote text after the marker (Sieve's `msgid=<...>`, a non-default
`deliver_log_format`) out of the rule altogether. *H* and *N* come from Postfix's configuration (`hosts` is split at
spaces; the table is a file name), while the remote lookup key of `mysql:<table> lookup error for "<key>"` and
`'domain' pattern match failed for '<key>'` follows a space before any `: `, so neither form reaches the anchor.

**Area document text** (the Dev transcribes it into `docs/areas/log-import.md`, see *Areas*): the tables above with the
driver-segment paragraph (the rule, not the source citations), the
program rules of `IsPostfix`/`IsDovecot`, "the classification sets only the priority and the event, never adds, drops or
reorders a record, and holds no state", "`mail.log` and the other rsyslog files keep the source type `syslog`", and the
forged-lines paragraph of record 0091 options 14, 19 and 20 in the style of the MariaDB section, with these statements:
(a) any local process can log under `postfix/<x>` or `dovecot` with any marker and any database message; (b) a marker's
priority replaces the priority the transport gave the line (a journal `PRIORITY`, a `<PRI>`), also a more severe one, so a
classified priority is the daemon's class read from the text, not the priority the line was sent with; (c) remote text
reaches a marker position only through a user name in Dovecot's first segment (`imap(<user>)...`): the default
`auth_username_chars` keeps spaces and colons out of it, and a configuration whose `auth_username_chars` allows a space
and a colon lets a remote user forge a Dovecot marker, and with it the priority and `dovecot.fatal` or `dovecot.panic`
(not `dovecot.database_unavailable`: such a first segment begins with the logging process, e.g. `imap(`, never with
`auth-worker(<digits>)`); (d) the database
rules themselves read remote text nowhere: Postfix's table and host contain no space, Dovecot's rule takes only
`auth-worker` lines and only the driver's fixed segments, so a sender's address, lookup key or Message-ID cannot produce
a database event; (e) an event or priority is a classification, not proof of origin),
the claim of a mail log ("`mail.log`, `mail.err`, `maillog` and their rotations are claimed by their first line, like
`auth.log`; they are no syslog names, so a copy whose first line is cut is not recognized"), and the limit for earlier
imports in the style of the *Stored source type* sentence of *Parser contract* ("contents imported by a version without
the classification keep their records unclassified: an import is idempotent per content and a changed parser version is
not noticed; a file whose interrupted import is resumed by a version with it has unclassified records before the resume
point; `vandoxd import` offers no way to read such a content again").

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `src/Vandox.Core/LogParsing/MailEvents.cs` | new |
| Vandox.Core | `src/Vandox.Core/LogParsing/MailLineClassifier.cs` | new |
| Vandox.Core | `src/Vandox.Core/LogParsing/SystemLogGrouper.cs` | `Add` classifies first; the class summary names three stages |
| Vandox.Core | `src/Vandox.Core/LogParsing/SyslogParser.cs` | class summary only: "Reads rsyslog files such as `syslog`, `kern.log` and `mail.log`." |

## Signatures (for the Dev's skeleton)

```csharp
namespace Vandox.Core.LogParsing;

/// <summary>
/// The events the classification of mail lines recognizes.
/// </summary>
public static class MailEvents
{
    public const string PostfixFatal = "postfix.fatal";
    public const string PostfixPanic = "postfix.panic";
    public const string PostfixDatabaseUnavailable = "postfix.database_unavailable";
    public const string DovecotFatal = "dovecot.fatal";
    public const string DovecotPanic = "dovecot.panic";
    public const string DovecotDatabaseUnavailable = "dovecot.database_unavailable";
}

/// <summary>
/// Classifies the lines of Postfix and Dovecot that the journal and syslog parsers read: the severity marker sets the
/// priority, fatal and panic lines and failed or lost MySQL connections get an event.
/// </summary>
internal static class MailLineClassifier
{
    internal static bool IsPostfix(string program);
    internal static bool IsDovecot(string program);
    internal static void Classify(LogLine payload);
}
```

Constants are real values in the skeleton (a constant cannot throw); the three methods throw `NotImplementedException`
under the skeleton pragma (`.squad/stack.md`, *Skeleton*). `SystemLogGrouper.Add` keeps its signature; its body changes
in step 6. No existing file is rewritten by the skeleton.

## Test files

- `tests/Vandox.Core.Tests/MailLineClassifierTests.cs` — new: AC1.1, AC1.2, AC1.4, AC2, AC3.
- `tests/Vandox.Core.Tests/SystemLogGrouperTests.cs` — extended: AC5.3.
- `tests/Vandox.Core.Tests/SyslogParserTests.cs` — extended: AC1.3, AC1.6 (`HasSyslogName` rows), AC4, AC5.2 (syslog
  side).
- `tests/Vandox.Core.Tests/JournalExportParserTests.cs` — extended: AC5.1, AC5.2 (journal side, entries built with
  `JournalExportBuilder` from the fixture's lines).
- `tests/Vandox.Core.Tests/BuiltInParsersTests.cs` — extended: AC1.5, AC1.6 (registry row).
- `tests/Vandox.Core.Tests/CountingEmitter.cs` — new test double (Tester): validates every record like
  `RecordingEmitter`, keeps none, counts records and skips and counts per event and per priority, and runs an optional
  `Observed` action per accepted record (used to take the retained memory at the 1,000th and the last record).
- `testdata/logs/mail.log` — new fixture (Tester), exactly these lines, constructed from the formats in the sources
  (no server data; documentation addresses and `example.org`):

  ```
  Oct  9 03:12:01 mail-1 postfix/master[812]: daemon started -- version 3.6.4, configuration /etc/postfix
  Oct  9 03:12:05 mail-1 postfix/smtpd[2210]: connect from unknown[192.0.2.9]
  Oct  9 03:12:05 mail-1 postfix/smtpd[2210]: warning: hostname mx.example.net does not resolve to address 192.0.2.9: Name or service not known
  Oct  9 03:12:06 mail-1 postfix/submission/smtpd[2214]: connect from client.example.org[198.51.100.7]
  Oct  9 03:12:07 mail-1 postfix/qmgr[815]: 4F2A11C0123: from=<alice@example.org>, size=1520, nrcpt=1 (queue active)
  Oct  9 03:12:08 mail-1 postfix/proxymap[2230]: warning: connect to mysql server localhost: Can't connect to local MySQL server through socket '/var/run/mysqld/mysqld.sock' (2)
  Oct  9 03:12:08 mail-1 postfix/proxymap[2230]: warning: mysql:/etc/postfix/mysql-virtual.cf: query failed: Lost connection to MySQL server during query
  Oct  9 03:12:09 mail-1 postfix/postfix-script[3001]: fatal: the Postfix mail system is not running
  Oct  9 03:12:10 mail-1 postfix/master[812]: warning: process /usr/lib/postfix/sbin/smtpd pid 2210 killed by signal 9
  Oct  9 03:12:11 mail-1 postfix/cleanup[2240]: panic: myfree: corrupt or unallocated memory block
  Oct  9 03:12:12 mail-1 dovecot: master: Dovecot v2.3.16 (7e2e900c1a) starting up for imap, pop3 (core dumps disabled)
  Oct  9 03:12:13 mail-1 dovecot: imap-login: Login: user=<alice@example.org>, method=PLAIN, rip=198.51.100.7, lip=192.0.2.1, mpid=2301, TLS, session=<AbCdEfGh>
  Oct  9 03:12:14 mail-1 dovecot: auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Can't connect to local server through socket '/run/mysqld/mysqld.sock' (2) - waiting for 1 seconds before retry
  Oct  9 03:12:15 mail-1 dovecot: auth-worker(2290): Warning: sqlpool(mysql): Query failed, retrying: MySQL server has gone away
  Oct  9 03:12:16 mail-1 dovecot: master: Error: service(imap): child 2301 killed with signal 9
  Oct  9 03:12:17 mail-1 dovecot: imap(alice@example.org)<2301><AbCdEfGh>: Panic: file mail-index.c: line 42 (mail_index_open): assertion failed: (index != NULL)
  Oct  9 03:12:18 mail-1 dovecot: auth: Fatal: master: service(auth): child 2290 returned error 89
  Oct  9 03:12:19 mail-1 psa-pc-remote[1102]: Error: connect to database failed
  Oct  9 03:12:20 mail-1 postfix/smtpd[2210]: warning: Illegal address syntax from unknown[192.0.2.9] in MAIL command: <MySQL server has gone away>
  Oct  9 03:12:21 mail-1 dovecot: imap-login: Disconnected (auth failed, 1 attempts in 2 secs): user=<x: Panic: y>, method=PLAIN, rip=192.0.2.9
  ```

  Expected per line (20 lines; AC1.3, AC5.2): priority `null` and no event for lines 1, 2, 4, 5, 11, 12, 18, 20; 4 for
  3, 9, 19;
  4 + `postfix.database_unavailable` for 6 and 7; 2 + `postfix.fatal` for 8; 2 + `postfix.panic` for 10;
  3 + `dovecot.database_unavailable` for 13; 4 + `dovecot.database_unavailable` for 14; 3 for 15;
  2 + `dovecot.panic` for 16; 2 + `dovecot.fatal` for 17. The fixture is parsed in UTC with a `LogFile` name
  `mail.log` and a modification time in October 2026.

Existing test code that calls a changed signature: none (no signature changes). Existing tests with Postfix lines
(`SyslogLineTests` row 26, `SyslogParserTests` row 129: `connect from ...`, no marker) keep their expectations.

## Areas

- `docs/areas/log-import.md` (owner: Dev): *System log parsers* — the first paragraph names three stages in this order:
  the classification of mail lines, MariaDB entries, kernel reports; a new subsection **Mail lines (Postfix and Dovecot)**
  after *Kernel reports* with the text of *Area document text* above; *Duplicates* adds that `mail.log`, `syslog` and a
  journal export of the same period store each mail line up to three times with the same classification; *Related
  decisions* adds 0091; *Implementation* adds `MailLineClassifier` and `MailEvents`.
- No new area.

## Documentation updates

| File | Edit | Owner |
| ---- | ---- | ----- |
| `docs/areas/log-import.md` | as in *Areas* | Dev |
| `README.md` (*Import logs*, *Supported sources*, rsyslog item) | name `mail.log` among the rsyslog files and add: "Lines of Postfix (`postfix/<daemon>`) and Dovecot (`dovecot`) in any rsyslog file or journal export get the priority of their severity (`warning:` 4, `error:` 3, `fatal:` and `panic:` 2; Dovecot also `Debug:` 7) and the events `postfix.fatal`, `postfix.panic`, `dovecot.fatal`, `dovecot.panic`, and `postfix.database_unavailable` or `dovecot.database_unavailable` for a failed or lost MySQL/MariaDB connection." | Dev |
| `docs/ARCHITECTURE.md` (*Components*, `Vandox.Core` item; *Data flow* records list) | add "the classification of Postfix and Dovecot lines" to the built-in parsers' description, and 0091 to the records of *Data flow* | Dev |
| `.squad/project.md` | *Security areas* item 10: add `MailLineClassifier` (Postfix and Dovecot lines of the journal and syslog: severity marker and MySQL connection failures at the anchored positions, ordinal text operations, no state; a classification, not a trust boundary) and record 0091; *Test doubles*, row "log parsers (C#)": add `CountingEmitter` (validates like `RecordingEmitter`, keeps no record, counts per event and priority) | Dev |
| `docs/decisions/0091-...md`, `docs/decisions/README.md` (row), `docs/decisions/0086-...md` (option 2 and the first consequence no longer announce a mail parser) | done in step 2 | Lead |

## Architecture check

No guarantee of `docs/ARCHITECTURE.md` or `.squad/project.md` is touched. Imported records stay origin `import` and never
alert (0022). The import's determinism and resume by count (0069) hold: the stage changes no record count or order. Memory
stays bounded independently of the input (0079): the stage holds no state. The wire format and storage are unchanged:
`event` and `priority` exist (0063, 0088); the event names follow the name pattern. No flow changes, so no
`ARCHITECTURE.md` edit beyond the component description.

## Security considerations

- [x] No new limits. The classification reads the decoded message after the parser's existing cut (16,384 UTF-8 bytes,
  `Utf8Text.Cut`); a marker sits at the start or after the first segment and cannot be cut off; a client phrase cut at the
  end of a long message still matches when the phrase itself is kept. All scans are ordinal, case-sensitive and linear in
  the message length; no regular expression, so no backtracking.
- [x] No exception: the classifier parses no number and builds no value; nothing new reaches a user-visible reason.
- Forging (record 0091 options 10 to 14, 19 and 20). Markers: remote text reaches a marker position only through a
  Dovecot user name in the first segment; the default `auth_username_chars` excludes space and colon, a non-default value
  that allows both lets a remote user forge a Dovecot marker (documented in the area document, statement (c)). Database
  events: no remote text reaches the anchors under any `auth_username_chars`. Postfix's anchors require a table name or
  host without a space before the first `: `, and Postfix's remote lookup keys (`maps.c:210`, `:275`,
  `dict_mysql.c:362`) always follow a space; Dovecot's rule reads only `auth-worker(<digits>)` lines and one to three
  fixed driver segments, so a sender's Message-ID behind `sieve: msgid=<` (any process) and a user name in `sql(...)`
  (in `auth-worker`) end the walk. The former rule, "zero or more segments without a space" and "`mysql:` … contains
  `: query failed: `", let a remote sender forge both database events (security review round 1, B1 and B2); the AC3.2
  rows with remote text pin the fix, per accepted form. Local processes can forge any mail line, as for kernel and
  MariaDB lines; documented, not prevented.
- Accepted forms of the database guard, enumerated: Postfix `connect to mysql server <H>: <C>` with *H* a
  configured host (`localhost`, `unix:<path>`, `inet:<host>:<port>`, a name or address; never a space, `dict_mysql.c:793`)
  and `mysql:<N>: query failed: <C>` with *N* the table's file name; Dovecot `auth-worker(<pid>): Error: <segments>Connect
  failed to database (<db>): <C> - waiting for <n> seconds before retry` and `auth-worker(<pid>): Warning:
  <segments>Query failed, retrying: <C>`, segments as in *Database rule*. Rejected on purpose: everything else,
  including the dict process's SQL lines (`conn ...: ` prefix) and a `mail_log_prefix` or `deliver_log_format` that puts
  any text first.
- The program check is a classification, not a guard: no access decision depends on it.

## Decision records

- `docs/decisions/0091-mail-lines-classified-in-the-syslog-and-journal-parsers.md` (Proposed, indexed; revised after the
  challenge: options 15 to 18, decision and earlier-imports consequence; revised after the security review: context on
  the prefixes and on remote text near the anchors, options 11 and 13 corrected, options 19 and 20 rejected, a forging
  consequence).
- `docs/decisions/0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md` (Accepted, unreleased:
  edited in place, option 2 and the first consequence now point to 0091 instead of announcing a mail parser).

## Challenge

Devil's Advocate: 0 major, 3 minor objections.

1. *Dovecot `Info: ` is dead code in the in-scope sources and lets marker-like text overwrite a real syslog priority with
   6.* — **Accepted.** Dovecot's `syslog_format` omits `Info: ` (`lib/failures.c`), as this plan's own claim check says, so
   the mapping could only ever fire on text that is not Dovecot's marker, e.g. an error-level journal line whose message
   reads `x: Info: ...`, and would lower its priority. `Info: ` is removed from the marker table (*Marker rule*), the
   `master: Info: x` → 6 row of AC2.2 becomes an "unchanged" row together with `Info: x` and `auth: info: x`, AC2.3 adds
   that a priority of 3 is kept for `master: Info: x`; `spec.md` (behavior, AC2) and record 0091 no longer lists it
   (context, new options 15 and 16, decision). The README text in *Documentation updates* already named only `Debug:` for Dovecot. The
   follow-up issue for Dovecot's own log file, where `Info: ` does occur, now names it.
2. *Contents imported before this change stay unclassified; the area document does not say so.* — **Accepted.** Record
   0091 already named it under *Consequences*; the area document text (*Approach*, *Area document text*) now carries the
   sentence next to the *Parser contract* rule that a changed parser version is not noticed. Checked: `git tag` lists no
   tag, so no released version imported mail logs; only imports made with development builds of this repository are
   affected, and `vandoxd import` has no command to read an already imported content again.
3. *`mail.log`, `mail.err`, `maillog` are claimed only by their first line; a copy cut mid-line is not claimed; extending
   `HasSyslogName` affects `MariaDbErrorLogParser`.* — **Rejected, recorded as options 17 (rejected) and 18 (chosen) of 0091 and pinned by AC1.6.**
   Record 0086 (option 2) claims every rsyslog file other than `syslog` and `kern.log` by its first line only (`auth.log`,
   `daemon.log` alike); a mail log is no different. rsyslog writes whole lines and logrotate rotates by rename or
   copy-and-truncate, so a real `mail.log` or rotation begins with a header; a first line cut mid-line comes from a manual
   partial copy (`tail -c`), and such a file is listed as not recognized (exit code 0) rather than read. Widening
   `HasSyslogName` would also widen the names the MariaDB parser refuses (`MariaDbErrorLogParser.Detect`, line 55) and
   rely on file names that were not checked on the server (`maillog`). Changing the claim rule is a change to 0086 for
   every rsyslog file, not for this issue.

Security, plan review round 1 (CHANGES_REQUIRED, 2 blocking, 3 non-blocking):

- **B1** (Dovecot segment walk steerable by a sender's Message-ID; the general walk re-opens the user-name path) —
  **Accepted, and narrowed beyond the request.** Confirmed by reading `lib-lda/lda-settings.c:42` and
  `lib-lda/mail-deliver.c:124`. The walk now accepts only the driver segments `mysql: `, `mysql(<P>): `,
  `sqlpool(<P>): ` (*P* non-empty, no space, ended by the first `)`, which must be followed by `: `), at most three
  (*Database rule*). Not taken: "plus zero segments" — Dovecot 2.3.16 never writes a database message without a driver
  segment (the mysql event has `mysql: ` from its creation, `driver-mysql.c:294`; the pool has `sqlpool(mysql): `,
  `driver-sqlpool.c:527`), so zero segments would accept a form no Dovecot writes. Added on re-checking every accepted
  form: the rule reads only lines whose first segment is `auth-worker(<digits>)`, the one process with MySQL
  connections (blocking driver, `auth/passdb-sql.c:282-285`). Without it a non-default `deliver_log_format` that puts a
  sender's address first (e.g. `%f`) would bring remote text right after an `lmtp` marker, where `mysql(` + text could
  begin. The AC3.2 rows Security named are added (the msgid line, `sieve: msgid=<a@x>: Connect failed to database
  (x): Server has gone away`, `auth: Error: sql(a,1.2.3.4): ...`), each also with the `auth-worker` process so the
  segment rule is tested apart from the process rule, plus zero, four, empty, spaced and foreign segments and the
  process forms. *Security considerations*, the area-document text (statement (d)) and record 0091 (context, options 11
  and 13, new option 19) are corrected.
- **B2** (Postfix `mysql:` anchor reachable through a remote lookup key) — **Accepted.** Confirmed at `maps.c:210`,
  `:275` and `dict_mysql.c:362`. *T* must now be `mysql:` + *N* + `: query failed: ` + *C* with *N*, the text up to
  the first `: `, non-empty and free of spaces (the real format, `dict_mysql.c:568`). The re-check of the other Postfix
  anchor tightened it the same way: the host before the first `: ` must be non-empty and free of spaces (Postfix splits
  `hosts` at spaces, `dict_mysql.c:793`; `unix:<path>` and `inet:<host>:<port>` stay accepted, AC3.1 rows). AC3.2 rows
  for both remote-key forms (the key once unquoted, once with its quotes; written without backslashes because Postfix
  writes the key as is), an empty table name, a missing space after the anchor, an empty and a spaced host. Record
  0091 option 20 records the rejected "contains" anchor.
- **N1** (double prefixes `mysql: mysql(<host>): `, `sqlpool(mysql): mysql: ...`) — **Refuted as to the actual chain,
  accepted as test rows.** `event_set_append_log_prefix` replaces the event's own prefix (`lib/lib-event.c:510-528`), so
  `mysql(<host>): ` replaces `mysql: `; a pooled connection's event is parented to the pool's parent, not to the pool
  (`driver-sqlpool.c:290-293`). Dovecot 2.3.16 writes `mysql(<host>): ` or `mysql: ` before `Connect failed` and
  `sqlpool(mysql): ` before `Query failed, retrying` (claim check). The finding uncovered a plan error: the fixture and
  AC3.1 had `mysql(localhost): Query failed, retrying`; they now read `sqlpool(mysql): `. The nested forms stay accepted
  (up to three fixed segments) and are AC3.1 rows, with `mysql: ` alone as a real form.
- **N2** (marker forging depends on `auth_username_chars`) — **Accepted, wording corrected.** The area document states
  that a non-default `auth_username_chars` allowing space and colon lets a remote user forge a Dovecot marker
  (statement (c)) and no longer ties the database rule to it (statement (d)). The database event cannot be forged that
  way either, because a forged first segment begins with the logging process (`imap(`), never `auth-worker(<digits>)`.
- **N3** (a marker replaces a more severe transport priority) — **Accepted.** Statement (b) of the area document text:
  a classified priority is the daemon's class read from the text, not the priority the line was sent with. AC2.3
  is unchanged.

Additional point (orchestrator): the issue title says "Parser for mail.log", so the PR description must state the choice;
see *Pull request text*.

## Pull request text

The PR description (3–5 sentences, written by the orchestrator) must say that no separate `mail` parser type was added:
`mail.log` keeps being read by the existing syslog parser (source type `syslog`), and Postfix and Dovecot lines are
classified in the syslog and journal parsers so the same line gets the same priority and event from `mail.log`, `syslog`
and a journal export (record 0091). Suggested title: `[Logs] Classify Postfix and Dovecot lines in syslog and journal
imports`.

## Out of scope / follow-ups

- Follow-up issue (the orchestrator opens it): **"[Logs] Dovecot's own log file is misread as syslog"** — "With
  `log_path` set to a file, Dovecot writes `Mmm dd HH:MM:SS <prefix><Type>: <message>` without a host
  (`DEFAULT_FAILURE_STAMP_FORMAT`, `lib/failures.h:52`). `vandoxd import` claims such a file with the syslog parser by its
  first line, which then takes the process prefix (`imap-login:`) for the host and the marker (`Info`) for the program
  (`SyslogLine.TryParse`/`ParseTag`). Recognize the format (by content, never for a syslog name) and read it with the
  marker rules of record 0091 plus `Info: ` (priority 6), which Dovecot writes only to its own log file, or refuse it." Labels `area: logs`, `type: bug`, milestone v0.1.0.
- Lifecycle events and OOM victims among mail processes, Postfix queue analysis: #21.
- PostgreSQL and other drivers; non-default `syslog_name`, `instance_name` or `mail_log_prefix`; Dovecot's SQL errors
  outside `auth-worker` (the dict process's `conn ...: ` prefix, used by SQL quota and dict lookups), which stay with the
  marker's priority only.
