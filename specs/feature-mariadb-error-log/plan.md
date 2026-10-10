# Plan: Parser for the MariaDB error log

Source: Issue #17 | [spec.md](spec.md) | [tasks.md](tasks.md)
Status: Draft
Tier: security — the change adds a parser of external input (log files, *Security areas* 10 in `.squad/project.md`, which
names the MariaDB log) and a field that the ingest wire decoder accepts from agents (area 10, wire format), each of which
is `security`.

## Problem / root cause

Summary of [spec.md](spec.md): `vandoxd import` gets a third built-in parser, `mariadb`, for the MariaDB error log. It
recognizes the log by content, turns every entry (a header line and the lines without a header after it) into one
`log_line` record with its local time resolved in `import.time_zone`, maps the level to the priority, and classifies
lifecycle entries into a new optional `log_line` field `event`, added in Go, C#, the golden wire batch and storage (schema
version 5). Record [0088](../../docs/decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md).

Claims of the issue, checked against the code and the MariaDB source (read with plain Git from
`https://github.com/MariaDB/server`, tags `mariadb-10.3.39`, `-10.5.22`, `-10.6.7`, `-10.6.11`, `-10.6.12`, `-10.6.22`,
`-10.6.28`, `-10.11.9`; that Ubuntu 22.04 released with 10.6.7 is the Devil's Advocate's statement, *unverified* here
because the Ubuntu archive is not reachable from the session — the parser accepts both start wordings either way):

- "Depends on #15" — **confirmed done**: #15 was closed on 2026-10-06 (PR #132), and #16 (journal and syslog parsers,
  PR #152) is on `main` too. The framework this parser plugs into exists: `ILogParser`, `ParserRegistry`, `LogLineReader`,
  `Utf8Text`, `SyslogClock`, `BuiltInParsers` in `src/Vandox.Core/LogParsing`; `ImportCommand.cs:55` builds the registry from
  `BuiltInParsers.Create(config.Import.TimeZone)`, so registering the parser there is the only wiring needed.
- "Parse the MariaDB error log format" — **confirmed missing**: no parser claims a MariaDB line today, and
  `tests/Vandox.Core.Tests/BuiltInParsersTests.cs` pins exactly that (row `mariadb.err` expects no parser).
- "Ubuntu default path `/var/log/mysql/error.log`" — **refuted as a default**: MariaDB's Debian packaging
  (`debian/additions/mariadb.conf.d/50-server.cnf` at 10.6.12 and 10.6.28) has `#log_error = /var/log/mysql/error.log`
  commented out with "When running under systemd, error logging goes via stdout/stderr to journald". Ubuntu 22.04's own
  package and the server's Plesk configuration were **not checked** (Launchpad is not reachable from the session); the
  file exists where `log_error` is set. Consequence: the parser is content-based and looks for no path; on a server with
  the packaged default it finds no error log and imports nothing, and MariaDB's lines in the journal are classified only
  by follow-up #165.
- "path configurable" — **requirement dropped, not applicable to the import**: the scanner hands parsers a cleaned
  relative name of an arbitrary copy or archive entry (`LogFile.Name`), so a configured server path matches nothing;
  detection by content finds the file under any name. No option is added. Configured log paths belong to the agent's
  shipping (#37, "configured log files"). The spec names both deviations from the issue's wording (*Deviations from the
  issue*).
- "Classify start, normal shutdown, abort by signal, crash recovery start/end" — **gap confirmed**: `log_line` has no field
  for a classification (`src/Vandox.Core/Model/LogLine.cs`, `internal/model/logline.go`: log, host, program, pid,
  priority, message, truncated). The plan adds `event` (record 0088, options 1-4). The **start line has two wordings**:
  10.6.12 and later (also 10.3.39, 10.5.22) write `Starting MariaDB %s source revision %s as process %lu`
  (`sql/mysqld.cc:4874` at 10.6.12; 10.6.22 and later add `server_uid %s`); 10.6.7 to 10.6.11 write
  `%s (server %s) starting as process %lu ...` or, with `--version` set, `%s (server %s as %s) starting as process %lu ...`
  (`sql/mysqld.cc:3955-3961` at 10.6.7, `:4018-4024` at 10.6.11, in `init_common_variables`, before InnoDB starts), for
  example `/usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 1234 ...`. Both are `mariadb.start`.
  The other lifecycle texts are the same in 10.6.7 and 10.6.12 (`ER_STARTUP`, `ER_NORMAL_SHUTDOWN`, `ER_SHUTDOWN_COMPLETE`
  in `sql/share/errmsg-utf8.txt`, `[ERROR] mysqld got signal %d ;` in `sql/signal_handler.cc`, `Starting crash recovery
  from checkpoint LSN=` in `log0recv.cc`, `<version> started; log sequence number` in `srv0start.cc`, the table crash
  recovery lines in `sql/handler.cc`), and so is the header format of `print_buffer_to_file` (`sql/log.cc`).
- "warnings and errors" — **confirmed** as the level tags `Warning` and `ERROR` (`sql/log.cc:9388`,
  `print_buffer_to_file`, format `"%d-%02d-%02d %2d:%02d:%02d %lu [%s] %.*s%.*s\n"`); mapped to priorities 4 and 3.
- "Keep multi-line stack traces as one record" — **confirmed format**: `handle_fatal_signal` (`sql/signal_handler.cc`)
  writes `YYMMDD %2d:MM:SS [ERROR] <program> got signal N ;` and then the crash report and stack trace on lines without a
  time stamp; the query of the crashing connection is written raw (`my_safe_print_str`, `mysys/stacktrace.c:95`), up to
  64 KiB. `ER_STARTUP` (`"%s: ready for connections.\nVersion: ..."`) and `ER_SHUTDOWN_COMPLETE` (`"%s: Shutdown
  complete\n"`) also continue on a second line.
- "Its error log shows starts, crash recovery and aborts" — **partly**: an OOM kill is SIGKILL, which the signal handler
  cannot catch, so it leaves no line; the evidence is the next start and `InnoDB: Starting crash recovery from checkpoint
  LSN=...` (`storage/innobase/log/log0recv.cc`). InnoDB 10.6 writes no "recovery finished" line; its redo recovery ends
  before `InnoDB: <version> started; log sequence number ...` (`srv0start.cc`), which the plan uses (record 0088, option 16).
- "Tests with shortened real samples" — the production server's log is **not available** to the squad; the samples below
  are built line by line from the MariaDB 10.6 source format strings (the series Ubuntu 22.04 ships: the fixture from
  10.6.12, AC-E7 from 10.6.7) and shortened. Marked as such in the spec.

Related observations (not fixed, not part of this change):

- By the documented syslog header rules, a MySQL 8 error log line (`2026-03-01T12:00:00.123456Z 0 [System] [MY-010116] ...`)
  reads as an RFC 3339 syslog line with host `0` and no tag, so the generic syslog parser claims MySQL 8 error logs weakly.
  Not run, inferred from `docs/areas/log-import.md`; no MySQL 8 runs on the monitored server.
- **Defect in the C# wire decoder, found while checking AC-W3:** a JSON `null` for a string field of a payload is assigned
  as `null` (`PayloadRegistry.Options` sets no `RespectNullableAnnotations`; checked in a scratch program with the same
  options: `{"host":null}` gives `Host == null`), and validation then throws instead of returning a field error:
  `Check.Short` → `Encoding.UTF8.GetByteCount(null)` throws `ArgumentNullException` (`"host":null`, `"program":null`),
  `Check.RequiredShort` and `Check.OptionalName` read `value.Length` (`"log":null`, and `"event":null` once this change
  adds the field). `BatchDecoder.DecodeRecord` (`src/Vandox.Core/Wire/BatchDecoder.cs:241-310`) catches only
  `JsonException`, so the exception leaves `NextAsync`, whose contract is `WireException`. The Go decoder ignores such a
  `null` (`encoding/json` leaves a string unchanged). Inferred from the code and the scratch program, not run through
  `BatchDecoder`. `BatchDecoder` has no production caller yet (the ingest API, #40, is not built), so nothing is exposed
  today. The general fix needs every non-nullable property of every kind checked against what the Go encoder writes (a nil
  slice without `omitempty` encodes as `null`), so it is a follow-up of its own (*Out of scope / follow-ups*), not part of
  this change; `event` behaves like `host` until then.

## Acceptance criteria

Every line is at least one unit test; `[DataRow]` wherever several inputs share one behavior. Lines in backticks are the
exact input (two spaces where shown are two spaces; `\t` means a tab character in the C# string). The fixture of AC-E2 is
the file `testdata/logs/mariadb-error.log` given in full under *Fixture*.

### Header lines (`MariaDbLine.TryParse`)

- [ ] AC-H1 Header forms, `[DataRow]` per line, checking `Time` (year, month, day, hour, minute, second), `Level`,
  `Program`, `Message`:
  - `2026-03-01 12:30:15 0 [Note] InnoDB: Buffer pool(s) load completed at 260301 12:30:15` → 2026-03-01 12:30:15, `Note`,
    empty, `InnoDB: Buffer pool(s) load completed at 260301 12:30:15`
  - `2026-03-02  3:12:40 0 [Note] Starting MariaDB 10.6.12-MariaDB-0ubuntu0.22.04.1 source revision  as process 3456`
    (space-padded hour) → hour 3, `Note`, message from `Starting` to `3456` (the two spaces inside kept)
  - `2026-03-02 03:12:40 0 [Note] x` (zero-padded hour) → hour 3
  - `2026-03-02  3:15:02 18446744073709551615 [Warning] Aborted connection 5 to db: 'wp' user: 'wp' host: 'localhost' (Got an error reading communication packets)`
    (20-digit thread) → `Warning`
  - `2026-03-01 12:00:00 12 [ERROR] Master 'backup': Slave I/O: error connecting to master` → `ERROR`, message
    `Master 'backup': Slave I/O: error connecting to master`
  - `2026-03-01 12:00:00 0 [Note]` and `2026-03-01 12:00:00 0 [Note] ` → `Note`, empty message
  - `2026-03-02 10:10:10 0x7f3a2c1fe640  InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836`
    → level empty, program empty, message `InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836`
  - `2026-03-02 10:10:10 0x7f3a2c1fe640 INNODB MONITOR OUTPUT` (one space) → message `INNODB MONITOR OUTPUT`
  - `260302 10:10:10 [ERROR] mysqld got signal 6 ;` → 2026-03-02 10:10:10, `ERROR`, message `mysqld got signal 6 ;`
  - `260302  9:05:01 [ERROR] /usr/sbin/mariadbd got signal 11 ;` → hour 9, message `/usr/sbin/mariadbd got signal 11 ;`
  - `260301 12:00:00 mysqld_safe Starting mariadbd daemon with databases from /var/lib/mysql` → level empty, program
    `mysqld_safe`, message `Starting mariadbd daemon with databases from /var/lib/mysql`
  - `2026-13-45 25:61:61 0 [Note] x` → a header with month 13, day 45, hour 25 (ranges are not checked here)
  - a header line whose message holds bytes `0xFF` → message with U+FFFD, the prefix parsed
- [ ] AC-H2 Not headers (`TryParse` returns `null`, no exception), `[DataRow]` per line:
  `Version: '10.6.12-MariaDB-0ubuntu0.22.04.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04`;
  `InnoDB: Failing assertion: page_is_leaf(block->page.frame)`; `??:0(my_print_stacktrace)[0x55d0b1c1d2a2]`; the empty
  line; `2026-03-01 12:00:00 0 [Info] x`; `2026-03-01 12:00:00 0 [note] x`; `2026-03-01 12:00:00 0 [Note]x`;
  `2026-03-01 12:00:00 [Note] x` (four-digit date without thread); `260301 12:00:00 0 [Note] x` (two-digit date with
  thread); `2026-03-01T12:00:00.123456Z 0 [System] [MY-010116] [Server] /usr/sbin/mysqld (mysqld 8.0.36) starting as process 1`;
  `2026/03/01 12:00:00 [error] 1234#1234: *5 open() "/var/www/x" failed`; `2026-3-01 12:00:00 0 [Note] x`;
  `2026-03-01 12:00:00,123 fail2ban.filter [1234]: INFO [sshd] Found 203.0.113.5`;
  `2026-03-01 12:00:00 status installed mariadb-server:amd64 1:10.6.12-0ubuntu0.22.04.1`;
  `260301 12:00:00\t    5 Connect\troot@localhost on  using Socket`; `# Time: 260301 12:00:00`;
  `2026-03-01 12:00:00  0 [Note] x` (two spaces before the thread); `2026-03-01 12:00:00 0  [Note] x` (two spaces before
  `[`); `2026-03-01 12:00:00 0x  InnoDB: x`; `2026-03-01 12:00:00 0X7F  InnoDB: x`; `2026-03-01 12:00:00 0x7F  InnoDB: x`
  (upper-case hex); `2026-03-01 12:00:00 0x` followed by 17 hex digits and two spaces; a thread of 21 digits;
  `2026-03-01 12:00:00 0x7f3a2c1fe640` (no space after the thread); `260301 12:00:00 mysqld_safeX y`;
  a header whose first digit is U+0660 (Arabic-Indic zero); `2026-03-01 1:00:00 0 [Note] x` (one-digit hour without its
  padding space).

### Entries and record fields (`MariaDbErrorLogParser`, `MariaDbMessage`)

- [ ] AC-E1 Record fields of an entry: `Origin` = `import`, `Source` = `mariadb`, `Seq` = 0, `CapturedAt` = the header's
  local time resolved in the zone (AC-T1), `Log` = `LogFile.Name` unchanged (`backup/var/log/mysql/error.log.1` stays so),
  `Host` empty, `Program` empty (forms A, B, C) or `mysqld_safe` (form D), `Pid` 0, `Priority` 6 for `Note`, 4 for
  `Warning`, 3 for `ERROR`, `null` without a level, `Event` per AC-C1.
- [ ] AC-E2 The fixture `testdata/logs/mariadb-error.log` (57 lines, *Fixture*) parsed with zone `UTC` gives exactly 22
  records and no skip, in file order, with these header lines, events and priorities:
  1 (L1) `mariadb.shutdown` 6; 2-4 (L2-L4) none 6; 5 (L5) `mariadb.shutdown_complete` 6, message
  `/usr/sbin/mariadbd: Shutdown complete` (the empty L6 dropped); 6 (L7) `mariadb.start`; 7 (L8) none (no recovery open);
  8 (L9) `mariadb.ready`, message `/usr/sbin/mariadbd: ready for connections.` + `\n` + L10; 9 (L11) `mariadb.start`;
  10 (L12) none; 11 (L13) `mariadb.recovery_start`; 12-13 (L14-L15) none; 14 (L16) `mariadb.recovery_end`; 15 (L17)
  `mariadb.ready` with L18; 16 (L19) none, priority 4; 17 (L20) none, priority `null`, message = L20's message and L21-L23
  joined by `\n`; 18 (L24) `mariadb.abort`, priority 3, message = `mysqld got signal 6 ;` and L25-L51 joined by `\n` (the
  stack frames L40-L43 in order, the empty L28, L30 and L50 kept as empty lines, the empty L52 dropped, ending with the
  `Kernel version:` line), `Truncated` false, `CapturedAt` 2026-03-02T10:10:10Z; 19 (L53) `mariadb.start`; 20 (L54)
  `mariadb.recovery_start`; 21 (L55) `mariadb.recovery_end`; 22 (L56) `mariadb.ready` with L57. Every record passes the
  validating `RecordingEmitter`.
- [ ] AC-E3 Empty lines: inner empty lines of an entry are kept, trailing ones dropped: header message `h` followed by the
  lines empty, `x`, empty, empty → message `h` + `\n\n` + `x`; a header followed only by empty lines → the header message
  alone.
- [ ] AC-E4 Lines before the first entry: an empty line is skipped with "empty line", any other line with "line before
  the first entry", each with its 1-based line number; the first header then opens an entry. A UTF-8 BOM is removed from
  line 1 only: `EF BB BF` + a form A header on line 1 is a header; the same bytes at the start of line 2 make line 2 a
  continuation line.
- [ ] AC-E5 Bounds, counted in UTF-8 bytes of the decoded text (a byte `0xFF` decodes to U+FFFD, three bytes), with
  `Truncated` and the validating `RecordingEmitter` (no "record refused" skip):
  - an entry whose joined message is exactly 16,384 bytes is kept whole, `Truncated` false;
  - an entry with 40 continuation lines of 1,000 `a` (joined about 40,000 bytes) → the header message, then the largest
    number of whole continuation lines whose joined text stays within 16,320 bytes, then the line `[N lines omitted]` with
    the exact N, total at most 16,384 bytes, `Truncated` true;
  - the same with continuation lines of 1,000 bytes `0xFF` (3,000 UTF-8 bytes each after decoding);
  - a single header line whose decoded message exceeds 16,384 bytes (a header and 16,000 bytes `0xFF`) and no continuation
    → the message cut at a character boundary to at most 16,384 bytes, no marker, `Truncated` true;
  - the same header followed by one continuation line `x` → the first line cut to at most 16,320 bytes, then
    `[1 lines omitted]`, `Truncated` true;
  - a header line longer than 16,384 bytes (cut by `LogLineReader`) → `Truncated` true.
- [ ] AC-E6 An entry whose header has a time that cannot be stored is skipped once, with the header's line number and the
  reason of AC-T2; its continuation lines are neither records nor separate skips; the record of the entry before it and
  of the next header are emitted.
- [ ] AC-E7 The start sequence of MariaDB 10.6.7 to 10.6.11 (built from the 10.6.7 format strings; the version suffix is
  illustrative), parsed with zone `UTC`, inline in the test, five lines:
  `2026-02-14  8:01:12 0 [Note] /usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 812 ...`;
  `2026-02-14  8:01:12 0 [Note] InnoDB: Starting crash recovery from checkpoint LSN=42540,42540`;
  `2026-02-14  8:01:13 0 [Note] InnoDB: 10.6.7 started; log sequence number 42564; transaction id 14`;
  `2026-02-14  8:01:13 0 [Note] /usr/sbin/mariadbd: ready for connections.`;
  `Version: '10.6.7-MariaDB-2ubuntu1.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04` → four records with
  the events `mariadb.start`, `mariadb.recovery_start`, `mariadb.recovery_end`, `mariadb.ready`, priority 6 each, the
  first `CapturedAt` 2026-02-14T08:01:12Z, the last message `/usr/sbin/mariadbd: ready for connections.` + `\n` + the
  `Version:` line; no skip.

### Time (`SyslogClock.ResolveLocal`, through the parser)

- [ ] AC-T1 Zone: `UTC` keeps the wall time; `Europe/Berlin` `2026-07-01 12:00:00` → 10:00Z, `2026-01-15 12:00:00` →
  11:00Z, `260115 12:00:00 [Note] x` → 2026-01-15T11:00:00Z; the skipped hour `2026-03-29 02:30:00` → 01:30Z; the repeated
  hour on 2026-10-25 takes the earlier offset unless that puts it more than `SyslogClock.BackwardTolerance` (10 minutes)
  before the previous resolved header, `[DataRow]` per file order: `02:59:59`, `02:00:01`, `02:30:00` → 00:59:59Z,
  01:00:01Z, 01:30:00Z; `02:59:59`, `02:59:58`, `02:00:01` → 00:59:59Z, 00:59:58Z, 01:00:01Z; `02:59:59`, `02:10:00`,
  `02:09:58` → 00:59:59Z, 01:10:00Z, 01:09:58Z; `02:59:59`, `2026-09-31 12:00:00` (skipped), `02:49:00` → 00:59:59Z, skip,
  01:49:00Z (a skipped header leaves the previous instant unchanged). The first header of a file in the repeated hour takes
  the earlier offset.
- [ ] AC-T2 Times that cannot be stored, none of which throws, `[DataRow]` per header: month 13, day 32, hour 24, minute
  60, second 60, `2026-02-29`, `2026-04-31`, `260230` → "invalid date"; `2028-02-29` → stored; with zone `UTC`:
  `0000-01-01 00:00:00`, `1676-12-31 23:59:59`, `1677-09-21 00:12:43`, `2262-04-11 23:47:17`, `2263-01-01 00:00:00`,
  `9999-12-31 23:59:59` → "time outside the storable range"; `1677-09-21 00:12:44` and `2262-04-11 23:47:16` → stored.
  `ResolveLocal` called directly gives the same results (null or the reason, `instant` set only on success).
- [ ] AC-T3 Zone not set (`new MariaDbErrorLogParser(null)`): a file of an empty line, a continuation line and then a
  header → the two lines are skipped as in AC-E4, then `ParseAsync` throws `InvalidOperationException` with the message
  `import.time_zone is not set` (`SyslogParser.TimeZoneNotSet`); nothing is emitted, the header is neither emitted nor
  skipped; a header with an invalid date (`2026-02-30 ...`) as the first header throws the same (the zone is checked before
  the date).

### Classification (`MariaDbEventClassifier`)

- [ ] AC-C1 Event by level and first line of the message, `[DataRow]` per header (level, message → event):
  - `Note`, `Starting MariaDB 10.6.12-MariaDB-0ubuntu0.22.04.1 source revision  as process 2345` → `mariadb.start`
  - `Note`, `Starting MariaDB 10.6.22-MariaDB-0ubuntu0.22.04.1 source revision 3d0a5b1c server_uid 7mT4hXc0QvG2kz9pW8sYbN1eJ+U= as process 2345`
    → `mariadb.start`
  - `Note`, `/usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 1234 ...` → `mariadb.start`
    (10.6.7 to 10.6.11)
  - `Note`, `/usr/sbin/mariadbd (server 10.6.11-MariaDB-0ubuntu0.22.04.1 as 10.6.11-custom) starting as process 1234 ...`
    → `mariadb.start` (the form with `--version` set)
  - `Note`, `/usr/sbin/mysqld (server 10.6.7-MariaDB) starting as process 1 ...` → `mariadb.start`
  - `Note`, `/usr/sbin/mariadbd: ready for connections.` → `mariadb.ready`
  - `Note`, `/usr/sbin/mariadbd (initiated by: unknown): Normal shutdown` → `mariadb.shutdown`
  - `Note`, `/usr/sbin/mariadbd (initiated by: root[root] @ localhost []): Normal shutdown` → `mariadb.shutdown`
  - `Note`, `/usr/sbin/mariadbd: Shutdown complete` → `mariadb.shutdown_complete`
  - `ERROR`, `mysqld got signal 6 ;` → `mariadb.abort`; `ERROR`, `/usr/sbin/mariadbd got signal 11 ;` → `mariadb.abort`
  - `Note`, `InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234` → `mariadb.recovery_start`
  - `Note`, `InnoDB: Starting crash recovery.` → `mariadb.recovery_start`
  - `Note`, `Starting table crash recovery...` → `mariadb.recovery_start`
  - `Note`, `Crash table recovery finished.` → `mariadb.recovery_end`
  - no event: `Note`, `InnoDB: Starting shutdown...`; `Note`, `InnoDB: Shutdown completed; log sequence number 8394829; transaction id 5600`;
    `Warning`, `Aborted connection 5 to db: 'wp' user: 'wp' host: 'localhost' (Got an error reading communication packets)`;
    `ERROR`, `/usr/sbin/mariadbd: ready for connections.`; `Warning`, `mysqld got signal 6 ;`; `ERROR`, `mysqld got signal ;`;
    `ERROR`, `mysqld got signal 6`; `ERROR`, `mysqld got signal 6 ; x`; `ERROR`, `mysqld got signal 1234 ;`;
    `ERROR`, ` got signal 6 ;` (nothing before ` got signal `); `Note`, `mysqld did an expected abort`;
    `Note`, `starting MariaDB 10.6.12 as process 1`; `Note`, `Starting MariaDB 10.6.12`;
    `Note`, `/usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 1234` (no ` ...` at the end);
    `Note`, `/usr/sbin/mariadbd starting as process 1234 ...` (no ` (server `); `Note`,
    ` (server 10.6.7) starting as process 1 ...` (nothing before ` (server `); `Note`,
    `/usr/sbin/mariadbd (server 10.6.7) starting as process 1 ... x` (text after ` ...`); `Note`,
    `/usr/sbin/mysqld (mysqld 5.7.44) starting as process 1 ...` (MySQL's wording); `Warning`,
    `/usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 1234 ...`;
    `Note`, `InnoDB: Starting final batch to recover 210 pages from redo log.`; level empty (form D),
    `Starting mariadbd daemon with databases from /var/lib/mysql`; level empty (form B),
    `InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836`;
    `Note`, `InnoDB: 10.6.12 started; log sequence number 8401234; transaction id 5678` with no recovery open.
- [ ] AC-C2 Recovery end, in order on one classifier: recovery start, then `InnoDB: 10.6.12 started; log sequence number 1; transaction id 2`
  → `mariadb.recovery_end`, then the same `started` line again → none; recovery start, then `Starting MariaDB ... as process 1`
  (`mariadb.start`, closes the recovery), then a `started` line → none; recovery start, then
  `/usr/sbin/mariadbd (server 10.6.7-MariaDB-2ubuntu1.1) starting as process 1 ...` (`mariadb.start`, closes the
  recovery), then a `started` line → none; recovery start, `started` (end), `Starting table
  crash recovery...` (start), `Crash table recovery finished.` (end); a `started` line at level `Warning` while a recovery is
  open → none. A header skipped for its time (AC-E6) is not classified and does not change the state.
- [ ] AC-C3 Only the header's first line counts: an entry whose continuation line reads `/usr/sbin/mariadbd: ready for connections.`
  has the event of its header. A line break in client text (documented limitation, record 0088 option 19), through the
  parser with zone `UTC`, one test per case:
  - a crash report whose continuation line is `2026-03-02 10:10:11 0 [Note] /usr/sbin/mariadbd: ready for connections.`
    (as a forged query would produce) is split there, and that line becomes an entry of its own with `mariadb.ready`;
  - an access-denied warning whose user name holds two line breaks (any client that reaches the port can send one, without
    credentials), three lines: `2026-03-02 10:20:00 7 [Warning] Access denied for user 'x`;
    `2026-03-02 10:20:01 0 [Note] /usr/sbin/mariadbd: ready for connections.`; `'@'203.0.113.5' (using password: NO)` →
    two records: `Warning`, message `Access denied for user 'x`, no event; then `Note`, `mariadb.ready`, `CapturedAt`
    2026-03-02T10:20:01Z, message `/usr/sbin/mariadbd: ready for connections.` + `\n` +
    `'@'203.0.113.5' (using password: NO)`.

### Detection and registration

- [ ] AC-D1 `MariaDbErrorLogParser.Detect` returns `MatchContent` when any line of the head (split at `\n`, a `\r` before
  `\n` removed, a UTF-8 BOM at the start of the head removed, the last line possibly cut by the end of the head) is a
  header of AC-H1; the file name never matters (`notes.txt`, `mysql/error.log`, `web-1.err` give the same result).
  `[DataRow]` for `MatchContent`: a form A, B, C and D line as the first line; two continuation lines (`InnoDB: ...`,
  `??:0(abort)[0x7f3a3c4287f3]`) and then a form A line; an empty first line and then a header; a head of 4,096 bytes whose
  last, cut line is a complete form A prefix. `NoMatch`: an empty head; every line of AC-H2 alone; a journal export head;
  `Mar  1 12:30:15 web-1 sshd[1234]: Accepted publickey for root`; `2026-03-01T12:30:15.123456+01:00 web-1 sshd[1234]: Accepted publickey for root`;
  prose.
- [ ] AC-D2 `BuiltInParsers.Create("UTC")` and `Create(null)` return `journal`, `mariadb`, `syslog` in this order
  (`JournalExportParser`, `MariaDbErrorLogParser`, `SyslogParser`); the registry accepts them; an unknown zone still throws
  `ArgumentException` without the zone text. Through the registry: the existing `mariadb.err` row now gives `mariadb` with
  `MatchContent`; a journal export head stays `journal`, the traditional and RFC 3339 syslog heads stay `syslog`;
  `JournalExportParser` and `SyslogParser` return `NoMatch` for a form A, B, C and D head, and `MariaDbErrorLogParser`
  returns `NoMatch` for the journal and syslog heads.

### Cross-cutting

- [ ] AC-X1 Determinism: parsing the fixture twice with the same `LogFile` and zone gives equal record sequences.
- [ ] AC-X2 Cancellation and emitter errors: a cancelled token ends `ParseAsync` with `OperationCanceledException`; an
  emitter that throws on the record of L19 of the fixture ends `ParseAsync` with that exception, and `RecordAsync` is not
  called again (the open entry of L20 is not emitted; `RecordingEmitter` counts the calls).
- [ ] AC-X3 Every skip reason is one of `empty line`, `line before the first entry`, `invalid date`, `time outside the
  storable range` (no input text).

### Bounded memory (heap-bound tests)

Input is produced lazily by `PatternStream` (a header, then a repeated pattern); allocation is measured with
`GC.GetTotalAllocatedBytes(true)`, retention with one private `RetainedBytes()` helper per test class that wraps
`GC.GetTotalMemory(true)` in `#pragma warning disable S1215` / `restore` (`.squad/stack.md`, *Writing tests*).

- [ ] AC-M1 `MariaDbMessage` started with a header message and fed 100,000 continuation lines of 1,000 bytes `a` (one
  reused byte array): retained memory, measured with the builder alive after the last `Add`, grows by less than 1 MiB over
  the value before the first `Add` (keeping the lines would be about 200 MB of UTF-16); `Build` then gives a message of at
  most 16,384 UTF-8 bytes ending in `[N lines omitted]` with the exact N.
- [ ] AC-M2 `MariaDbErrorLogParser` over a header line followed by 64 MiB of continuation lines (1,023 `a` and `\n`,
  repeated): one record, message at most 16,384 UTF-8 bytes ending in `[N lines omitted]` with N = the number of
  continuation lines minus those kept, `Truncated` true; the parse allocates less than 8 MiB (a parser that decodes every
  line allocates more than 128 MiB). The test stays under 10 s.

### Record field, wire and storage

- [ ] AC-W1 Go: `model.LogLine.Event` (`json:"event,omitempty"`), validated after `program` as an optional name
  (`checkOptionalName`): empty, `mariadb.start` and 128 bytes of `a` are valid; 129 bytes of `a` → field `event`, reason
  `too long`; `mariadb start`, `-x`, `é`, `a\n` → field `event`, reason `invalid characters` (table rows in
  `internal/model/logline_test.go`). The worst-case size row of `internal/wire/encode_test.go` sets `Event` to 128 bytes and
  still fits.
- [ ] AC-W2 The golden batch's `log_line` record carries `"event":"auth.failure"` (set in `allKindRecords`,
  `internal/wire/encode_test.go`, fixture regenerated with `VANDOX_UPDATE_GOLDEN=1`); `TestEncodeBatchGolden` passes, and
  `WireContractTests.DecodeGoldenBatchReadsEveryLogLineField` asserts `Event` = `auth.failure`.
- [ ] AC-W3 C#: `LogLine.Event` (JSON `event`) with the same validation rows (`LogLine.Validate`, field `event`; through
  `DataRecord.Validate` the field is `data.event`); `PayloadRegistry.Serialize` writes `"event":"mariadb.start"` when set
  and `"event":""` when empty, as it writes `host` and `program` today (`WhenWritingDefault` omits a string only when it
  is `null`); `PayloadRegistry.Deserialize` of a `log_line` JSON without `event`, and of one with `"event":""`, gives an
  empty `Event` that validates; a round trip keeps `mariadb.start`. Only the Go encoder writes batches, and it omits an
  empty `event` (AC-W1, `omitempty`); C# serialization of a log line is not on the wire (`BatchWriter` serializes
  payloads only for kinds without a typed table). `"event":null` is not part of this criterion (*Related observations*,
  follow-up).
- [ ] AC-W4 Storage: schema version 5; a log line's `Event` (`mariadb.abort`, and empty) is written and read back; a
  database at version 4 is migrated (its log lines get an empty event, new ones keep theirs); the existing migration tests
  from versions 2 and 3 end at the current version.
- [ ] AC-W5 End to end: `vandoxd import` without a `Parsers` hook, with `import.time_zone: Europe/Berlin`, over a directory
  holding the fixture as `mysql/error.log` → exit code 0, outcome imported, 22 records of source `mariadb` read back from
  the database with their events in fixture order and L1 at 2026-03-01T22:00:01Z; without `import.time_zone` the file is
  failed with the reason `import.time_zone is not set`, nothing of it is stored and the exit code is 1.

## Accepted forms

What the parser does with every input form (Security reviews this list).

### Lines

The line reader removes `\n` or `\r\n` and cuts a line at 16,384 bytes. A line is an **entry header** when its bytes begin
with one of these prefixes, checked byte by byte on the raw line before any decoding (ASCII only; `D` is a byte `0`-`9`,
`SP` one byte 0x20, hour is `DD` or `SP D`); every other line is a **continuation line**.

| Form | Prefix | Level | Program | Message |
| ---- | ------ | ----- | ------- | ------- |
| A: server line (`sql_print_*`, MariaDB 10.1 and later) | `DDDD-DD-DD` SP hour `:DD:DD` SP thread SP `[` level `]`, then the end of the line or SP | the level | empty | the rest after `] ` (may be empty) |
| B: InnoDB time stamp (`ut_print_timestamp`) | `DDDD-DD-DD` SP hour `:DD:DD` SP `0x` and 1-16 bytes `0-9a-f`, then one or more SP | none | empty | the rest after the spaces |
| C: signal handler, servers before 10.1 | `DDDDDD` SP hour `:DD:DD` SP `[` level `]`, then the end of the line or SP | the level | empty | the rest after `] ` |
| D: `mysqld_safe` | `DDDDDD` SP hour `:DD:DD` SP `mysqld_safe` SP | none | `mysqld_safe` | the rest after `mysqld_safe ` |

| Part | Accepted | Otherwise |
| ---- | -------- | --------- |
| thread (form A) | 1-20 bytes `D` (not converted to a number, not stored) | continuation line |
| level | exactly `ERROR`, `Warning` or `Note` | continuation line |
| `DDDDDD` (forms C, D) | `YYMMDD`, year 2000 + `YY` | — |
| digits of month, day, hour, minute, second | any digits (the grammar does not check ranges) | invalid values: the entry is skipped with "invalid date" |
| year 1677 to 2262, date valid, instant inside the storable range | stored | "invalid date" (no such day, month 0 or 13, hour 24, minute or second 60) or "time outside the storable range" |
| other separators (`T`, `/`, `,`, tab, two spaces where one is required), upper-case hex, non-ASCII digits | — | continuation line |
| UTF-8 BOM | removed at the start of line 1 only | a BOM on another line makes it a continuation line |
| message bytes | decoded with U+FFFD for invalid bytes; control characters kept | — |
| empty line | a continuation line (empty) | — |

### Entries

| Input | Result |
| ----- | ------ |
| header + following continuation lines up to the next header or the end of input | one record; message = header message and continuation lines joined by `\n`, trailing empty lines removed, inner empty lines kept |
| joined message at most 16,384 UTF-8 bytes (decoded) | kept whole |
| more, with at least one continuation line | the first line (cut at a character boundary to 16,320 bytes if longer), then the following whole lines while the text stays within 16,320 bytes, then `\n[N lines omitted]` (N the exact number of lines left out, trailing empty lines not counted); `Truncated` |
| more, header line alone | cut at a character boundary to 16,384 bytes, no marker; `Truncated` |
| any line cut by the line reader | `Truncated` |
| continuation lines before the first header | skipped: "empty line" for an empty one, "line before the first entry" for any other; 1-based line number |
| header whose time is invalid or outside the storable range | the entry (header and continuation lines) skipped once with the header's line number; no record |
| zone not set | the first header line throws `InvalidOperationException("import.time_zone is not set")` before its time is checked |
| end of input | the open entry is emitted |
| cancellation, an emitter exception, the unset zone | the parse ends with that exception; the open entry is **not** emitted (the records before a failure are a prefix of a complete parse) |

### Events

Matched with ordinal, case-sensitive string operations (`StartsWith`, `EndsWith`, `Contains`, `IndexOf`), linear in the
length of the header's message (its first line only); no regular expression.

| Event | Level | Header message |
| ----- | ----- | -------------- |
| `mariadb.start` | `Note` | starts with `Starting MariaDB ` and contains ` as process ` (10.6.12 and later); or a non-empty text, then ` (server `, then later `) starting as process `, and ends with ` ...` (10.6.7 to 10.6.11) |
| `mariadb.ready` | `Note` | ends with `: ready for connections.` |
| `mariadb.shutdown` | `Note` | ends with `: Normal shutdown` |
| `mariadb.shutdown_complete` | `Note` | ends with `: Shutdown complete` |
| `mariadb.abort` | `ERROR` | a non-empty text, then ` got signal `, 1-3 bytes `D`, then ` ;` at the end |
| `mariadb.recovery_start` | `Note` | starts with `InnoDB: Starting crash recovery`, or is exactly `Starting table crash recovery...` |
| `mariadb.recovery_end` | `Note` | is exactly `Crash table recovery finished.`, or starts with `InnoDB: ` and contains ` started; log sequence number ` while a recovery is open |
| none | — | anything else, and every entry without a level (forms B, D) |

A recovery is open after a `mariadb.recovery_start` entry until the next `mariadb.recovery_end` or `mariadb.start` entry, in
file order; skipped entries are not classified and do not change it.

## Fixture

`testdata/logs/mariadb-error.log`, 57 lines, every line ending in `\n`, no trailing spaces, no tab. `(empty)` is an empty
line. Shortened from the MariaDB 10.6.12 formats (Ubuntu 22.04 package version string). The start line of 10.6.7 to
10.6.11 is covered by AC-E7 (inline) and AC-C1/AC-C2, so the fixture and every line number that refers to it stay as
they are.

```
L1  2026-03-01 23:00:01 0 [Note] /usr/sbin/mariadbd (initiated by: unknown): Normal shutdown
L2  2026-03-01 23:00:01 0 [Note] InnoDB: FTS optimize thread exiting.
L3  2026-03-01 23:00:01 0 [Note] InnoDB: Starting shutdown...
L4  2026-03-01 23:00:02 0 [Note] InnoDB: Shutdown completed; log sequence number 8394829; transaction id 5600
L5  2026-03-01 23:00:02 0 [Note] /usr/sbin/mariadbd: Shutdown complete
L6  (empty)
L7  2026-03-01 23:00:05 0 [Note] Starting MariaDB 10.6.12-MariaDB-0ubuntu0.22.04.1 source revision  as process 2345
L8  2026-03-01 23:00:05 0 [Note] InnoDB: 10.6.12 started; log sequence number 8394829; transaction id 5601
L9  2026-03-01 23:00:05 0 [Note] /usr/sbin/mariadbd: ready for connections.
L10 Version: '10.6.12-MariaDB-0ubuntu0.22.04.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04
L11 2026-03-02  3:12:40 0 [Note] Starting MariaDB 10.6.12-MariaDB-0ubuntu0.22.04.1 source revision  as process 3456
L12 2026-03-02  3:12:40 0 [Note] InnoDB: Completed initialization of buffer pool
L13 2026-03-02  3:12:40 0 [Note] InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234
L14 2026-03-02  3:12:41 0 [Note] InnoDB: Starting final batch to recover 210 pages from redo log.
L15 2026-03-02  3:12:41 0 [Note] InnoDB: 128 rollback segments are active.
L16 2026-03-02  3:12:41 0 [Note] InnoDB: 10.6.12 started; log sequence number 8409876; transaction id 5678
L17 2026-03-02  3:12:41 0 [Note] /usr/sbin/mariadbd: ready for connections.
L18 Version: '10.6.12-MariaDB-0ubuntu0.22.04.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04
L19 2026-03-02  3:15:02 5 [Warning] Aborted connection 5 to db: 'wp' user: 'wp' host: 'localhost' (Got an error reading communication packets)
L20 2026-03-02 10:10:10 0x7f3a2c1fe640  InnoDB: Assertion failure in file ./storage/innobase/btr/btr0cur.cc line 836
L21 InnoDB: Failing assertion: page_is_leaf(block->page.frame)
L22 InnoDB: We intentionally generate a memory trap.
L23 InnoDB: Submit a detailed bug report to https://jira.mariadb.org/
L24 260302 10:10:10 [ERROR] mysqld got signal 6 ;
L25 This could be because you hit a bug. It is also possible that this binary
L26 or one of the libraries it was linked against is corrupt, improperly built,
L27 or misconfigured. This error can also be caused by malfunctioning hardware.
L28 (empty)
L29 To report this bug, see https://mariadb.com/kb/en/reporting-bugs
L30 (empty)
L31 Server version: 10.6.12-MariaDB-0ubuntu0.22.04.1
L32 key_buffer_size=134217728
L33 read_buffer_size=131072
L34 max_used_connections=12
L35 Thread pointer: 0x0
L36 Attempting backtrace. You can use the following information to find out
L37 where mysqld died. If you see no messages after this, something went
L38 terribly wrong...
L39 stack_bottom = 0x0 thread_stack 0x49000
L40 ??:0(my_print_stacktrace)[0x55d0b1c1d2a2]
L41 ??:0(handle_fatal_signal)[0x55d0b17a4b05]
L42 libc_sigaction.c:0(__restore_rt)[0x7f3a3c442520]
L43 ??:0(abort)[0x7f3a3c4287f3]
L44 Writing a core file...
L45 Working directory at /var/lib/mysql
L46 Resource Limits:
L47 Limit                     Soft Limit           Hard Limit           Units
L48 Max open files            32768                32768                files
L49 Core pattern: |/usr/share/apport/apport -p%p -s%s -c%c -d%d -P%P -u%u -g%g -- %E
L50 (empty)
L51 Kernel version: Linux version 5.15.0-91-generic (buildd@lcy02-amd64-010) #101-Ubuntu SMP Tue Nov 14 13:30:08 UTC 2023
L52 (empty)
L53 2026-03-02 10:10:15 0 [Note] Starting MariaDB 10.6.12-MariaDB-0ubuntu0.22.04.1 source revision  as process 4567
L54 2026-03-02 10:10:15 0 [Note] InnoDB: Starting crash recovery from checkpoint LSN=8410000,8410000
L55 2026-03-02 10:10:16 0 [Note] InnoDB: 10.6.12 started; log sequence number 8410100; transaction id 5690
L56 2026-03-02 10:10:16 0 [Note] /usr/sbin/mariadbd: ready for connections.
L57 Version: '10.6.12-MariaDB-0ubuntu0.22.04.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04
```

The `Lnn` labels and the padding after them are not part of the file. Lines L11-L19 have two spaces between the date and
the one-digit hour; L10, L18 and L57 have two spaces before `socket:`, `port:` and `Ubuntu`; L7, L11 and L53 have two
spaces in `revision  as`; L20 has two spaces before `InnoDB:`.

## Approach

1. **Record field.** `event` on `log_line`: Go model field and validation (optional name, after `program`), C# property
   and validation in the same order, the golden batch's `log_line` gets `auth.failure`, schema step 5
   (`ALTER TABLE log_lines ADD COLUMN event TEXT NOT NULL DEFAULT ''`), `BatchWriter` binds it, `RecordQueries` reads it.
   The wire version stays 1.0 (nothing released; record 0088 option 3, as 0084).
2. **Time.** `SyslogClock.ResolveLocal` (new, static) applies the rules of 0085 to a local time that has a year: clock
   digits, day of the month, year 1677-2262 as integers, `MapLocal` with the repeated-hour tolerance against the previous
   instant, then `StorableTime.Contains`. `SyslogClock`'s own year-less path may call it; its behavior and the existing
   `SyslogClockTests` stay unchanged.
3. **Header.** `MariaDbLine.TryParse(ReadOnlySpan<byte>)` checks the prefix of the four forms on the raw bytes and decodes
   only the message part (`Utf8Text.Decode(..., int.MaxValue, out _)`), so a continuation line costs no allocation. Keep
   every method below the cognitive complexity of 15 (Sonar S3776): one helper per form or per field.
4. **Message.** `MariaDbMessage` holds the header message and the kept continuation lines (at most 16,384 UTF-8 bytes in
   total, the first line at most 16,384), counts held-back empty lines and omitted lines (a `long`), takes continuation
   lines as raw bytes and decodes a line only when it is kept, and builds the message by the *Entries* rules.
5. **Events.** `MariaDbEventClassifier` applies the *Events* table and holds the open-recovery flag.
6. **Parser.** `MariaDbErrorLogParser.ParseAsync` reads lines with `LogLineReader`, removes a BOM from line 1, skips lines
   before the first header, resolves each header (throwing for the unset zone first), emits the previous entry's record when
   a header arrives, feeds continuation lines to the open entry (or drops them for a skipped entry), checks the token per
   line, and emits the open entry only at the normal end of input. `Detect` applies `TryParse` to every line of the head.
7. **Registration.** `BuiltInParsers.Create` returns `[new JournalExportParser(), new MariaDbErrorLogParser(zone), new SyslogParser(zone)]`
   (specific before generic; a tie with the journal parser, both `MatchContent`, goes to the journal parser registered
   first). `ImportCommand`, `ParserRegistry` and the importer do not change.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go | `internal/model/logline.go` | `Event` field and validation |
| Go (test data) | `testdata/wire/all-kinds.jsonl`, `testdata/logs/mariadb-error.log` | regenerated golden batch; new fixture (Tester) |
| Vandox.Core | `Model/LogLine.cs` | `Event` property and validation |
| Vandox.Core | `LogParsing/MariaDbErrorLogParser.cs`, `MariaDbLine.cs`, `MariaDbMessage.cs`, `MariaDbEventClassifier.cs`, `MariaDbEvents.cs` (all new) | the parser |
| Vandox.Core | `LogParsing/SyslogClock.cs` | new static `ResolveLocal`; behavior of the rest unchanged |
| Vandox.Core | `LogParsing/BuiltInParsers.cs` | list `journal`, `mariadb`, `syslog`; XML doc comment |
| Vandox.Storage | `StorageLimits.cs`, `SchemaMigrator.cs`, `BatchWriter.cs`, `RecordQueries.cs` | schema 5, `log_lines.event` |

## Signatures (for the Dev's skeleton)

Go (`internal/model/logline.go`):

```go
type LogLine struct {
    Log       string `json:"log"`
    Host      string `json:"host,omitempty"`
    Program   string `json:"program,omitempty"`
    PID       int32  `json:"pid,omitempty"`
    Priority  *uint8 `json:"priority,omitempty"`
    Message   string `json:"message"`
    Truncated bool   `json:"truncated,omitempty"`
    Event     string `json:"event,omitempty"` // the event a producer recognized in the line, e.g. mariadb.start; empty for none
}
// Validate: ... checkShort("program") then checkOptionalName("event", l.Event), then pid, priority, message as today
```

C# (namespaces as the folders; XML documentation and `#region` blocks on every member):

```csharp
// Vandox.Core.Model.LogLine — new property after Truncated
[JsonPropertyName("event")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
public string Event { get; set; } = string.Empty;
// Validate(): Check.RequiredShort("log", Log) ?? Check.Short("host", Host) ?? Check.Short("program", Program)
//             ?? Check.OptionalName("event", Event), then pid, priority, message as today

// Vandox.Core.LogParsing
public static class MariaDbEvents
{
    public const string Start = "mariadb.start";
    public const string Ready = "mariadb.ready";
    public const string Shutdown = "mariadb.shutdown";
    public const string ShutdownComplete = "mariadb.shutdown_complete";
    public const string Abort = "mariadb.abort";
    public const string RecoveryStart = "mariadb.recovery_start";
    public const string RecoveryEnd = "mariadb.recovery_end";
}

public sealed class MariaDbErrorLogParser : ILogParser
{
    public const string ParserType = "mariadb";
    public MariaDbErrorLogParser(DateTimeZone? timeZone);   // null: the first header fails the file with SyslogParser.TimeZoneNotSet
    public string Type { get; }                             // ParserType
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head);   // MatchContent or NoMatch; the name is ignored
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken);
}

internal sealed class MariaDbLine
{
    internal const string LevelError = "ERROR";
    internal const string LevelWarning = "Warning";
    internal const string LevelNote = "Note";
    internal const string SafeProgram = "mysqld_safe";
    internal SyslogTime Time { get; init; }                 // Year set (YYMMDD: 2000 + YY), OffsetMinutes null, FractionTicks 0; digits only, ranges not checked
    internal string Level { get; init; } = string.Empty;    // one of the level constants, or empty (forms B and D)
    internal string Program { get; init; } = string.Empty;  // SafeProgram for form D, else empty
    internal string Message { get; init; } = string.Empty;  // the rest of the line after the prefix, decoded with U+FFFD, not cut
    internal byte? Priority { get; }                        // ERROR 3, Warning 4, Note 6, else null
    internal static MariaDbLine? TryParse(ReadOnlySpan<byte> line);   // null: a continuation line; never throws
}

internal sealed class MariaDbMessage
{
    internal const int KeptBytes = 16320;                   // ModelLimits.MaxTextBytes - 64: room for "\n[N lines omitted]"
    internal MariaDbMessage(string first, bool truncated);  // the header's message; truncated: the reader cut the header line
    internal void Add(ReadOnlySpan<byte> line, bool truncated);   // a continuation line, raw; decoded only when kept
    internal string Build(out bool truncated);              // the message by the Entries rules; truncated: any cut or omitted line
}

internal sealed class MariaDbEventClassifier
{
    internal string Classify(MariaDbLine line);             // a MariaDbEvents value or string.Empty; tracks an open recovery in call order
}

// Vandox.Core.LogParsing.SyslogClock — new static member; everything else unchanged
internal static string? ResolveLocal(DateTimeZone timeZone, SyslogTime time, DateTimeOffset? previous, out DateTimeOffset instant);
// time.Year is used, time.OffsetMinutes ignored. null on success, else SyslogTime.InvalidDate ("invalid date") or
// SyslogTime.OutsideRange ("time outside the storable range"); never throws; instant is default unless null is returned.
// Repeated hour: the earlier offset unless it lies more than BackwardTolerance before previous; skipped hour: shifted forward.

// Vandox.Core.LogParsing.BuiltInParsers.Create — signature unchanged, returns journal, mariadb, syslog
```

Storage: no public signature changes (`StorageLimits.SchemaVersion` becomes 5; the SQL of `BatchWriter` and
`RecordQueries` gains the column).

## Test files

New (Tester):

- `tests/Vandox.Core.Tests/MariaDbLineTests.cs` — AC-H1, AC-H2
- `tests/Vandox.Core.Tests/MariaDbMessageTests.cs` — AC-E3 (builder rows), AC-E5 (builder rows), AC-M1
- `tests/Vandox.Core.Tests/MariaDbEventClassifierTests.cs` — AC-C1, AC-C2
- `tests/Vandox.Core.Tests/MariaDbErrorLogParserTests.cs` — AC-E1-E7, AC-T1-T3 (through the parser), AC-C2 (skipped
  header), AC-C3, AC-D1, AC-X1-X3, AC-M2
- `tests/Vandox.Core.Tests/LogLineTests.cs` — AC-W3
- `testdata/logs/mariadb-error.log` — the fixture
- `tests/Vandox.Backend.Tests/RepositoryFiles.cs` — the helper of `Vandox.Core.Tests` (`RepositoryFiles.Path`), so the
  end-to-end test of AC-W5 copies the same fixture (helpers per test project, as `TempDirectory`)

Existing (Tester):

- `tests/Vandox.Core.Tests/SyslogClockTests.cs` — AC-T2 (`ResolveLocal` directly), AC-T1 rows of `ResolveLocal`
- `tests/Vandox.Core.Tests/BuiltInParsersTests.cs` — AC-D2
- `tests/Vandox.Core.Tests/WireContractTests.cs` — AC-W2
- `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`, `SqliteStoreWriteTests.cs` — AC-W4
- `tests/Vandox.Backend.Tests/ImportCommandTests.cs` — AC-W5 (a new test method; the existing ones stay)
- `internal/model/logline_test.go`, `internal/wire/encode_test.go`, `testdata/wire/all-kinds.jsonl` — AC-W1, AC-W2

`MariaDbEvents` holds constants only (no executable line); its values are pinned by `MariaDbEventClassifierTests`.

Existing test code that calls a changed signature: **none** (every changed signature is additive). Existing tests whose
expected behavior changes, adapted by the **Tester** in step 5:

- `BuiltInParsersTests.BuiltInParsersCreateReturnsJournalThenSyslog` (three parsers; rename to name the new order),
  `BuiltInParsersCreateListIsAcceptedByTheRegistry` (types `journal`, `mariadb`, `syslog`),
  `BuiltInParsersRegistryDetectsFilesByContentAndName` (row `mariadb.err` expects `mariadb`).
- `SqliteStoreOpenTests.SqliteStoreOpenMigratesOlderSchema` (also drops `log_lines.event` before setting version 2) and
  `SqliteStoreOpenMigratesVersionThreeAndAddsHost` (also drops `event`, asserts the current version instead of the literal
  `4`, summary text updated); otherwise step 5 of the migration would fail on the existing column.
- `internal/wire` golden: `TestEncodeBatchGolden` compares against the regenerated fixture.

## Areas

- **Log import** (`docs/areas/log-import.md`): *Command* — `import.time_zone` is used for year-less syslog times and for
  MariaDB error log times; *System log parsers* first paragraph — the built-in list is `journal`, `mariadb`, `syslog`; new
  section *MariaDB error log* — recognition by content (no path option), the header forms, entries and continuation lines,
  the message bounds and marker, the fields (program, pid, host, priority), the events table (both wordings of the start
  line, with the versions that write them) and the recovery rule, the time rules (local time, `import.time_zone`, the
  unset-zone failure, skip reasons), the forged-line limitation (0088 option 19: the query of a crash report and the user
  name of a failed login are written raw, so any client that reaches the port can forge an entry; an event is a
  classification of text, not proof that MariaDB wrote it); the MariaDB versions whose formats were checked; that a server
  with the packaged default logs to the journal, where its lines are classified only by #165;
  *Related decisions* — 0088; *Implementation* — the new types.
- **Wire format** (`docs/areas/wire-format.md`): `log_line` gains `event` (optional name, the event a producer recognized,
  empty for none; additive, version stays 1.0; link 0088); as for `host`: the Go encoder omits an empty `event`, the C#
  decoder reads a line without it as an empty event.
- **Storage** (`docs/areas/storage.md`): schema version 5 adds `log_lines.event` (`TEXT NOT NULL DEFAULT ''`); the
  `log_lines` row of the table lists it.
- **Configuration and secrets** (`docs/areas/configuration-and-secrets.md`): while `import.time_zone` is unset,
  `vandoxd import` also fails a MariaDB error log at its first entry.

No new area.

## Documentation updates

Every edit has one owner, the **Dev** (as the area documents above); the Lead's approval only sets 0088 and its index row to
`Accepted`.

- `README.md`: *Backend options* row `import.time_zone` — also required to import MariaDB error logs; *Import logs* —
  a *Supported sources* bullet for the MariaDB error log (source type `mariadb`, recognized by content under any name,
  entries with their continuation lines, the events, MariaDB 10.x formats; logs that MariaDB sent to the journal arrive
  through the journal and syslog parsers without events) and the time zone paragraph naming MariaDB error logs.
- `deploy/backend/vandoxd.yaml`: the comment above `# time_zone: Europe/Berlin` also names MariaDB error logs (local time
  without offset). The commented line itself stays as it is.
- `docs/ARCHITECTURE.md`: the status sentence (line 10) and the `Vandox.Core` component entry name the MariaDB error log
  parser; *Storage and retention*, *Migrations*: version 5 adds `log_lines.event`; *Data flow* records list links 0088.
- `.squad/project.md`, *Security areas* 10: add `MariaDbErrorLogParser`, `MariaDbLine` and `MariaDbMessage` (hand-written
  header parser on raw bytes, entries bounded to 16 KiB) next to the syslog types, and record 0088.
- `src/Vandox.Core/LogParsing/BuiltInParsers.cs`: the XML summary names the new order.

## Architecture check

- *Backfilled data never raises an alert*: imported records have origin `import` and are never live; unchanged.
- *No data gaps unless recorded*, *a hanging collector never blocks the agent*: not touched (backend import only; the Go
  agent gains an optional model field it does not fill).
- Wire format: an additive optional field; the decoder already ignores unknown keys, so older agents and the golden contract
  (0075) stay compatible; the version stays 1.0 because nothing is released (0043, as 0084).
- Storage: a new migration step; released steps are never edited (none is released).
- `docs/ARCHITECTURE.md` changes only in its component description and schema version (*Documentation updates*); no
  guarantee changes, nothing for the Product Manager.

## Security considerations

- [x] Every byte or length limit on parsed input says whether it applies before or after decoding:
  - the line reader's 16,384 bytes: raw, before decoding;
  - the header prefix: raw bytes, ASCII only, fixed widths (date 10 or 6 bytes, time 8, thread at most 20 digits or `0x` and
    16 hex digits, level at most 7 bytes), checked before any decoding; no number is parsed from an unbounded digit run
    (the thread is not converted at all; date and time fields are fixed two- or four-digit fields);
  - the message bounds 16,384 and 16,320: UTF-8 bytes of the decoded text (each invalid byte counts as the three bytes of
    U+FFFD), cut at a character boundary, so no record is refused for its length (AC-E5);
  - `event` on the wire: the name rule, at most 128 bytes of the decoded JSON string, checked by the model in both
    languages; the parser only writes the fixed ASCII constants of `MariaDbEvents`.
- [x] Exceptions that reach a user-visible failure reason (`ImportReasons.Of`, `_ => exception.Message`):
  - `InvalidOperationException` with the fixed text `import.time_zone is not set` → the file's reason
    "import.time_zone is not set";
  - `OperationCanceledException` → the run is interrupted (existing handling);
  - exceptions of the emitter pass through unchanged (the importer's own types and texts).
  No other exception can come from input: no date or time value is built before the integer checks
  (`SyslogClock.ResolveLocal`), NodaTime is used only through `MapLocal`, no `int.Parse`/`DateTime.Parse` on input, no
  regular expression.
- Memory: bounded by the line reader (16 KiB plus its buffer) and one open entry (the first line at most 16,384 UTF-8
  bytes plus kept lines at most 16,384; omitted and held-back empty lines only counted), independent of the input size
  (AC-M1, AC-M2). Time: linear in the input; matching uses ordinal string operations on the first line only.
- Display: messages are stored as read (control characters kept, as for syslog); skip reasons and events are fixed texts;
  the importer never logs a message. Showing messages escaped is the web UI's job (area 12, Razor encodes HTML).
- Forged lines: client text that MariaDB copies into its log can contain a line break and a fake header with a forged
  time, level and event (AC-C3). Two paths, both verified in the source: the query of a crash report, written raw
  (`my_safe_print_str`), which needs a client that can crash the server; and, without any credentials, the user name of a
  failed login: `login_failed_error` (`sql/sql_acl.cc:12979-13001` at 10.6.7, `:13213-13235` at 10.6.28) logs
  `Access denied for user '%s'@'%s' ...` with `sql_print_warning` whenever `log_warnings > 1`, which is the default (2,
  `sql/sys_vars.cc:1505-1511` at 10.6.7); the handshake's user name is converted and cut to 128 characters but not
  filtered (`parse_client_handshake_packet`), and the message is formatted into a 1,024-byte buffer (`vprint_msg_to_log`),
  enough for a fake header and a lifecycle message. So any client that reaches the port, a local web application
  included, can insert an entry with any event, time and level, without crashing anything. Accepted and documented (record
  0088, option 19; the area document): there is no escaping in the format to tell such a line apart. Signature detection
  (#21) and outage reconstruction (#23) must not treat an `event` as proof that MariaDB wrote the line.
- No file access, no path handling in the parser.

## Decision records

- [`docs/decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md`](../../docs/decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md)
  (Proposed, indexed): where the classification lives (`log_line.event`, in Go and C# and why both), the value rule,
  detection by content without a path option, entries with continuation lines, head-with-marker bounds,
  `import.time_zone` for MariaDB times, the recovery end rule, the field mapping, forged lines (crash-report query and
  failed-login user name).

## Challenge

Devil's Advocate, one round (2 major, 3 minor). Every objection accepted or rejected below; the MariaDB source claims were
re-checked at the tags named in *Problem / root cause*.

- **M1 — the start line of 10.6.7 to 10.6.11 is missed.** Accepted. Confirmed at `mariadb-10.6.7` (`sql/mysqld.cc:3955-3961`)
  and `mariadb-10.6.11` (`:4018-4024`); `Starting MariaDB` first appears at 10.6.12 (`:4874`); 10.3.39 and 10.5.22 have
  the new wording. The other lifecycle texts and the header format are identical in 10.6.7 (checked, *Problem / root
  cause*). Revised: the `mariadb.start` rule accepts the old wording too (`Note`; a non-empty text, ` (server `, later
  `) starting as process `, ending with ` ...`), both `(server %s)` and `(server %s as %s)`; AC-C1 has three positive and
  six negative rows for it, AC-C2 a recovery closed by the old start line, the new AC-E7 runs a 10.6.7 restart with crash
  recovery through the parser (inline, so the 57-line fixture and its line numbers stay); the events table, the area
  document content, the spec (formats checked, by minor version) and record 0088 (*Context*) name both wordings.
- **M2 — AC-W3 "omitted when empty" cannot be met with the prescribed signature.** Accepted. Confirmed in a scratch program
  with the options of `PayloadRegistry`: `{"event":""}`; `host` and `program` behave the same today. Revised: AC-W3 now
  states what the prescribed signature does (`"event":""` when empty, read back as empty) and keeps the signature, the
  same attribute as `host` and `program`. A nullable property or a converter was not chosen: C# never writes a log line
  to the wire (only the Go encoder writes batches, and it omits an empty `event`, AC-W1), `BatchWriter` serializes no
  `log_line` payload (typed table), and a nullable `Event` would need a null rule in validation and storage that `host`
  does not have. `WireContractTests` is unaffected: it only decodes the Go golden batch. While checking this, the plan
  found the C# decoder's handling of a JSON `null` in string fields to be a defect for every field (*Related
  observations*); it is a follow-up, not this change.
- **m1 — the forged-line threat is understated.** Accepted. Confirmed: `login_failed_error` logs the handshake's user name
  with `sql_print_warning` at the default `log_warnings` of 2, the name is not filtered for line breaks, and no
  authentication or crash is needed. Revised: *Security considerations* names this path with its sources, AC-C3 has an
  access-denied row with two line breaks in the user name, record 0088 option 19 and *Consequences* cite it and state that
  #21 and #23 must not treat an `event` as proof of origin, and the area document's forged-line paragraph names both paths.
- **m2 — the Go side of `event` is not needed by #17.** Rejected. Record 0075 decides that the C# decoder keeps the
  validation rules of the Go decoder and that the Go encoder's golden batch pins the contract; a field only in C# could
  not be in the golden batch (AC-W2), the Go decoder (`internal/wire/decode.go`) would drop it silently as an unknown key,
  and the wire area document would describe a `log_line` the reference implementation does not have. Record 0084 added
  `host` to both languages for the same reason. The Go part is one field, one validation call and table rows. Record 0088
  option 3 now says why Go is included.
- **m3 — the issue's default path and "path configurable" are dropped silently.** Accepted. Revised: the spec has a section
  *Deviations from the issue* that states both dropped requirements with the reason, and *Open questions* no longer reads
  "none" but names, as information for the Product Manager, that a server with the packaged default may hold no error
  log, so this parser imports nothing there and #165 is the change that classifies MariaDB's lines; the plan's claim
  check and record 0088 (*Consequences*) reference #165. Not escalated: the content-based detection meets the intent of
  "path configurable" (the file is found wherever it lies) and a path has no meaning for an import of copies; the PR
  description repeats the deviation for the Product Manager.

## Out of scope / follow-ups

- **#165** `[Logs] Classify MariaDB lines from the journal and syslog` (opened): MariaDB's lines that the packaged default
  sends to the journal, with the header and event rules of this change.
- **Follow-up issue (proposed, the orchestrator opens it):** title `[Wire] C# decoder: a JSON null in a string field
  throws instead of failing the record`; body: "`PayloadRegistry.Options` assigns a JSON `null` to non-nullable string
  properties, and validation then throws `ArgumentNullException` (`Check.Short`, e.g. `"host":null` in a `log_line`) or
  `NullReferenceException` (`Check.RequiredShort`, `Check.OptionalName`, e.g. `"log":null`, `"event":null`).
  `BatchDecoder.DecodeRecord` catches only `JsonException`, so the exception leaves `NextAsync`, whose contract is
  `WireException`; the Go decoder ignores such a `null`. Decide one rule for both decoders (reject as malformed, or read
  as absent like Go), check every non-nullable property of every kind against what the Go encoder writes (a nil slice
  without `omitempty` encodes as `null`), add the rows to the wire-format area's *Accepted forms* and to
  `BatchDecoderTests`. Must be fixed before the ingest API (#40) calls `BatchDecoder`. Found while planning #17."
- A path option for the error log (agent: #37).
- Slow and general query logs, MySQL 5.7/8 error logs, the `mysqld_safe ... ended` line as an event.
- Queries by `event`, cross-source events and incidents (#21, #23).
