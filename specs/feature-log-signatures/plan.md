# Plan: Signature detection for known events

Source: Issue #21 | [spec.md](spec.md)
Status: Draft (revised after the challenge and the Security plan review)
Tier: security — hostile log text (security area 10, *Parsing of external input*) reaches a new pattern engine and
number conversions, and the change edits a build file (`src/Vandox.Core/Vandox.Core.csproj`, embedded resource).

## Problem / root cause

Summary of [spec.md](spec.md): one data-defined catalog of signatures and one detector that recognizes known event
types in stored `log_line` records of every source, returns events that list the record IDs of their lines, and counts
the copies of one event (the same kernel line in `syslog`, `kern.log` and the journal) once.

Claims of the issue and its comment, checked against the code:

- *Dependencies #16 to #19 are in main* — **confirmed**: `src/Vandox.Core/LogParsing` holds the journal export, syslog
  and `kern.log` parsers with the kernel report grouper (#16), the MariaDB error log parser and the MariaDB stage of the
  journal and syslog parsers (#17, #165), the mail classification and Dovecot's own log (#18, #194), the Plesk and web
  server error logs (#19) and the legacy monitoring log (#20); `BuiltInParsers.Create` registers them all.
- *OOM kill with victim, anon-RSS and `oom_score_adj`* — **confirmed** in the kernel source: `mm/oom_kill.c` of v5.15
  (line 893) and v6.8 (line 946) print `%s: Killed process %d (%s) total-vm:%lukB, anon-rss:%lukB, file-rss:%lukB,
  shmem-rss:%lukB, UID:%u pgtables:%lukB oom_score_adj:%hd`, with the prefixes `Out of memory`, `Memory cgroup out of
  memory` and `Out of memory (oom_kill_allocating_task)` (v5.15 lines 1099, 1117-1118). `victim->comm` is up to 15
  bytes and may hold `)` and spaces. The grouped report keeps the kill line (it is the end line, in the kept tail).
- *MariaDB crash and recovery* — **confirmed** as existing parser events: `mariadb.abort`, `mariadb.recovery_start`,
  `mariadb.recovery_end` (`MariaDbEvents.cs`), set for the error log and for MariaDB lines of the journal and syslog.
- *Postfix/Dovecot fatal errors* — **confirmed** as existing events `postfix.fatal`, `postfix.panic`, `dovecot.fatal`,
  `dovecot.panic` (`MailEvents.cs`).
- *Service start/stop/failure (systemd)* — **confirmed with a limit**: systemd v249.11 (Ubuntu 22.04) logs `Started %s.`
  and `Stopped %s.` with the unit's *description* (`src/core/job.c` lines 576 and 586, `unit_status_string`; the
  combined form `<unit> - <description>` only with `StatusUnitFormat=combined`, the default of later versions), and
  `<unit>: Failed with result '%s'.` with the unit name (`src/core/unit.c` line 5576). The journal reader keeps only
  eight fields (`JournalExportReader.cs` lines 35-42), not `UNIT`, so start and stop of 22.04 carry no unit name.
- *Disk full* and *Too many connections* — **confirmed with a limit** (revised, Security B1): the error log parsers set
  no `program` and keep the whole line from the level bracket on as the message (`ErrorLogLine.cs`, `ParseNginx` and
  `ParseApache`: `Message = line[bracket..]`), so a web server error line carries client-supplied text (nginx
  `request:`, `host:`, `referrer:`, a requested path; Apache `referer:`; PHP output in FastCGI stderr, over several
  lines). A pattern that matches anywhere in a message can therefore be triggered by any remote HTTP client. The
  signatures are narrowed to writers whose message starts with server-written text (see *Approach*, *Remote text*).
  The MariaDB message forms `Disk full (%s); waiting for someone to free some space... (errno: %M)` and `Disk is full
  writing '%s' (Errcode: %M). Waiting for someone to free space...` are taken from MariaDB's `mysys` error texts; whether
  MariaDB puts `<program>: ` in front of them in the error log is **unverified**, so the pattern accepts both. Postfix's
  `warning: connect to mysql server %s: %s` (`dict_mysql.c`) and Dovecot's
  `auth-worker(N): Error: mysql(host): Connect failed to database (db): ...` (the form of `testdata/logs/dovecot.log`
  line 3) are taken from the AC2 examples of the first plan; on the server **unverified**.
- *Each detected event links to its source lines* — **feasible**: stored records have IDs (`StoredRecord.Id`) and the
  record reader returns log lines ordered by capture time, then ID (`IRecordReader`, `RecordQueries.Build`).
- Comment: *program `kernel` is no proof of origin; `_TRANSPORT` is this issue's decision* — **confirmed**: the journal
  reader keeps `SYSLOG_IDENTIFIER`, not `_TRANSPORT` (`JournalExportReader.cs` lines 35-42), and the grouper recognizes
  kernel lines by program alone (`KernelReportGrouper.cs` line 26). Decided in record 0094 (option 13): not kept.
- Comment: *Ubuntu's rsyslog writes kernel lines to `syslog` and `kern.log`, so a kill is stored twice* — **unverified**
  on the server (record 0086 states it from the packaged configuration); the plan merges such copies either way.
- Earlier records: 0086 says "signature detection of the OOM kill works on the stored records" — **kept** (option 2 of
  0094). 0088 documents that a client of MariaDB can forge whole error log entries through a failed login's user name —
  **kept** and inherited by the MariaDB-based signatures (see *Security considerations*).

**Related defect found:** `KernelReportGrouper.cs` line 39 does not end an OOM report at the kill line of
`vm.oom_kill_allocating_task` — opened as #202 by the orchestrator. **Stale documentation found:** `docs/ARCHITECTURE.md`
(*Components*, the `internal/` bullet) lists "signatures" among the agent's Go packages; no such package exists. Fixed
in this change (*Documentation updates*).

## Acceptance criteria

Test names follow `{Class}{Scenario}{ExpectedResult}`. "One event" below means: `Complete` (after the `Add` calls) yields
exactly the events listed, in that order, and no other. A line feed inside a message below is written `\n` (in C# the
escape in a regular string literal).

- [ ] **AC1 — built-in catalog.** `BuiltInSignatures.Catalog` loads the embedded `signatures.yaml` once and lists the
  signature IDs `oom_kill`, `mariadb_crash`, `mariadb_recovery_start`, `mariadb_recovery_end`,
  `mariadb_too_many_connections`, `service_start`, `service_stop`, `service_failed`, `disk_full`, `postfix_fatal`,
  `dovecot_fatal`, `boot` in this order; every pattern is compiled with exactly `Signature.PatternOptions`
  (`NonBacktracking | Multiline | CultureInvariant | ExplicitCapture`); no pattern contains the Unicode digit class
  (the two characters backslash and `d`); every signature has an event condition or an origin condition (a non-empty
  `Programs` or `Sources`), and every signature without an event condition whose pattern is to read a message that can
  hold remote text (`disk_full`, `mariadb_too_many_connections`) starts its pattern with `\A`.
  Tests: `BuiltInSignaturesCatalogListsTheSignaturesInOrder`, `BuiltInSignaturesPatternsRunWithoutBacktracking`,
  `BuiltInSignaturesPatternsUseAsciiDigitsOnly`, `BuiltInSignaturesEverySignatureHasAnEventOrOriginCondition`.

- [ ] **AC2 — each signature has positive and negative cases** (issue AC 1). Each line below is one `log_line` record
  (record `source`, `program`, `host`, `event`, `message` as given; empty when not given) fed alone to a detector on
  `BuiltInSignatures.Catalog`. Positive: exactly one event of that signature with exactly the fields given (a field not
  listed is absent from both `Texts` and `Numbers`). Negative: no event at all.

  | Signature | Positive (fields) | Negative |
  | --------- | ----------------- | -------- |
  | `oom_kill` | program `kernel`, a two-line message whose second line is the v5.15 kill line of `testdata/logs/kern.log-oom` with its `[123456.789105] ` prefix (`victim_pid` 4242, `victim_command` `mariadbd`, `anon_rss_bytes` 7,577,600,000, `oom_score_adj` 0); the same kill line without the prefix (journal form); `Memory cgroup out of memory: Killed process 42 (php-fpm) total-vm:8kB, anon-rss:7kB, file-rss:0kB, shmem-rss:0kB, UID:1 pgtables:1kB oom_score_adj:-1000` (42, `php-fpm`, 7,168, -1000); `Out of memory (oom_kill_allocating_task): Killed process 42 (a (b)) total-vm:8kB, anon-rss:7kB, file-rss:0kB, shmem-rss:0kB` (42, `a (b)`, 7,168, no `oom_score_adj`); a comm holding the delimiter: `Out of memory: Killed process 42 (x) total-vm:1kB) total-vm:8kB, anon-rss:7kB, file-rss:0kB, shmem-rss:0kB, UID:1 pgtables:1kB oom_score_adj:-17` (42, `x) total-vm:1kB`, 7,168, -17) | the kill line of the fixture with program `sshd`; program `kernel` with `oom_reaper: reaped process 4242 (mariadbd), now anon-rss:0kB, file-rss:0kB, shmem-rss:0kB`; `Out of memory: OOM victim 42 (x) is already exiting. Skip killing the task`; `Out of memory and no killable processes...`; the journal-form kill line with the pid written as the Arabic-Indic digits U+0664 U+0662 (written in C# as escapes; the test first asserts that the input holds a character above U+007F for which `char.IsDigit` is true, so a lost backslash fails the test instead of passing it) |
  | `mariadb_crash` | source `mariadb`, event `mariadb.abort`, message `mysqld got signal 6 ;\nThis could be because you hit a bug.` (`signal` 6); the same with source `journal`, program `mariadbd`, host `web-1` (`signal` 6) | message `mysqld got signal 6 ;` without an event; event `mariadb.start` with message `mysqld got signal 6 ;` |
  | `mariadb_recovery_start` | event `mariadb.recovery_start`, message `InnoDB: Starting crash recovery from checkpoint LSN=1` (no fields) | that message without an event; event `mariadb.recovery_end` |
  | `mariadb_recovery_end` | event `mariadb.recovery_end`, message `InnoDB: 10.6.12 started; log sequence number 1; transaction id 2` | that message without an event; event `mariadb.recovery_start` |
  | `mariadb_too_many_connections` | program `postfix/proxymap`: `warning: connect to mysql server localhost: Too many connections`; program `postfix/cleanup`: the same; program `dovecot`: `auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Too many connections - waiting for 1 seconds before retry`; source `dovecot` (program empty): the same Dovecot message (no fields) | program `postfix/postscreen`: `NOQUEUE: reject: CONNECT from [192.0.2.9]:4242: 421 4.7.0 Error: too many connections from 192.0.2.9`; program `sshd`: `warning: connect to mysql server localhost: Too many connections` (origin); program `postfix/smtpd`: `NOQUEUE: reject: RCPT from x[192.0.2.9]: 554 5.7.1 <warning: connect to mysql server x: Too many connections>: Recipient address rejected` (not at the start); program `dovecot`: `imap-login: Info: Disconnected: user=<auth-worker(1): Error: mysql(x): Connect failed to database (y): Too many connections>` (not at the start); program `dovecot`: `master: Info: x\nauth-worker(1): Error: mysql(x): Connect failed to database (y): Too many connections` (second line, `\A` not `^`); source `apache-error`: `[php:warn] [pid 901] [client 198.51.100.7:51234] PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/index.php on line 3` (PHP out of scope); source `nginx-error`: `[alert] 1021#1021: accept4() failed (24: Too many open files)` |
  | `service_start` | program `systemd`: `Started MariaDB 10.6.12 database server.` (`description` `MariaDB 10.6.12 database server`, no `unit`); `Started mariadb.service - MariaDB 10.11.6 database server.` (`unit` `mariadb.service`, `description` `MariaDB 10.11.6 database server`) | program `systemd`: `Starting MariaDB 10.6.12 database server...`; `Finished Daily apt upgrade and clean activities.`; program `logger`: `Started MariaDB 10.6.12 database server.` |
  | `service_stop` | program `systemd`: `Stopped MariaDB 10.6.12 database server.` (`description`); `Stopped mariadb.service - MariaDB 10.11.6 database server.` (`unit`, `description`) | program `systemd`: `Stopping MariaDB 10.6.12 database server...`; `Stopped (with error) MariaDB 10.6.12 database server.`; `mariadb.service: Deactivated successfully.` |
  | `service_failed` | program `systemd`: `mariadb.service: Failed with result 'oom-kill'.` (`unit` `mariadb.service`, `result` `oom-kill`); `plesk-php82-fpm.service: Failed with result 'exit-code'.` (`unit`, `result` `exit-code`) | program `systemd`: `session-3.scope: Failed with result 'oom-kill'.`; `mariadb.service: Main process exited, code=killed, status=9/KILL`; program `cron`: `mariadb.service: Failed with result 'oom-kill'.`; program `systemd`: `x.service: Failed with result 'a\nb'.` (the result may not hold a line feed) |
  | `disk_full` | source `mariadb`: `mariadbd: Disk full (./ibtmp1); waiting for someone to free some space... (errno: 28 "No space left on device")`; source `mariadb`: `Disk full (./ibtmp1); waiting for someone to free some space... (errno: 28 "No space left on device")`; source `mariadb`: `/usr/sbin/mariadbd: Disk is full writing './mysql-bin.000042' (Errcode: 28 "No space left on device"). Waiting for someone to free space...`; source `journal`, program `mariadbd`: the first message; source `syslog`, program `mysqld`: the first message (no fields) | source `mariadb`: `Disk quota exceeded`; source `mariadb`: `no space left on device`; source `mariadb`: `Access denied for user 'a'@'h'\nmariadbd: Disk full (x); waiting` (second line); source `plesk-panel`: `ERR [panel] file_put_contents(): Write of 4096 bytes failed with errno=28 No space left on device` (other writers out of scope); program `postfix/cleanup`: `mariadbd: Disk full (x); waiting` (origin); source `nginx-error`: `[crit] 1021#1021: *16 pwrite() "/var/lib/nginx/body/0000000001" failed (28: No space left on device), client: 198.51.100.7, server: example.org` (out of scope) |
  | `postfix_fatal` | event `postfix.fatal` with `fatal: the Postfix mail system is not running`; event `postfix.panic` with `panic: myfree: corrupt or unallocated memory block` | event `dovecot.fatal`; event `postfix.database_unavailable`; message `fatal: the Postfix mail system is not running` without an event |
  | `dovecot_fatal` | event `dovecot.fatal` with `auth: Fatal: master: service(auth): child 2290 returned error 89`; event `dovecot.panic` with `Panic: file mail-index.c: line 42` | event `postfix.fatal`; event `dovecot.database_unavailable`; message `Fatal: x` without an event |
  | `boot` | program `kernel`: `[    0.000000] Linux version 5.15.0-91-generic (buildd@lcy02-amd64-045) (gcc (Ubuntu 11.4.0-1ubuntu1~22.04) 11.4.0, GNU ld (GNU Binutils for Ubuntu) 2.38) #101-Ubuntu SMP Tue Nov 14 13:30:08 UTC 2023` (`kernel_version` `5.15.0-91-generic`); the same without the prefix | the same text with program `sshd`; program `kernel`: `Command line: BOOT_IMAGE=/vmlinuz-5.15.0-91-generic root=UUID=1 ro`; program `kernel`: `x Linux version 5.15.0-91-generic (gcc)` |

  Tests (one positive and one negative method per signature, the cases as `DataRow`s):
  `BuiltInSignaturesOomKillReadsTheKillLine`, `BuiltInSignaturesOomKillIgnoresOtherLines`,
  `BuiltInSignaturesMariaDbCrashReadsTheSignal`, `BuiltInSignaturesMariaDbCrashIgnoresOtherLines`,
  `BuiltInSignaturesMariaDbRecoveryStartMatchesItsEvent`, `BuiltInSignaturesMariaDbRecoveryStartIgnoresOtherLines`,
  `BuiltInSignaturesMariaDbRecoveryEndMatchesItsEvent`, `BuiltInSignaturesMariaDbRecoveryEndIgnoresOtherLines`,
  `BuiltInSignaturesTooManyConnectionsMatchesClientErrors`, `BuiltInSignaturesTooManyConnectionsIgnoresOtherLines`,
  `BuiltInSignaturesServiceStartReadsDescriptionAndUnit`, `BuiltInSignaturesServiceStartIgnoresOtherLines`,
  `BuiltInSignaturesServiceStopReadsDescriptionAndUnit`, `BuiltInSignaturesServiceStopIgnoresOtherLines`,
  `BuiltInSignaturesServiceFailedReadsUnitAndResult`, `BuiltInSignaturesServiceFailedIgnoresOtherLines`,
  `BuiltInSignaturesDiskFullMatchesMariaDbWriteErrors`, `BuiltInSignaturesDiskFullIgnoresOtherLines`,
  `BuiltInSignaturesPostfixFatalMatchesFatalAndPanic`, `BuiltInSignaturesPostfixFatalIgnoresOtherLines`,
  `BuiltInSignaturesDovecotFatalMatchesFatalAndPanic`, `BuiltInSignaturesDovecotFatalIgnoresOtherLines`,
  `BuiltInSignaturesBootReadsTheKernelVersion`, `BuiltInSignaturesBootIgnoresOtherLines`. Plus:
  - `BuiltInSignaturesLineMatchingTwoSignaturesYieldsBoth`: source `mariadb`, event `mariadb.abort`, message
    `mariadbd: Disk full (./ibtmp1); waiting for someone to free some space... (errno: 28 "No space left on device")\nmysqld got signal 6 ;`
    yields `mariadb_crash` (`signal` 6), then `disk_full` (catalog order).
  - `BuiltInSignaturesClientTextInWebServerLogsYieldsNothing` (Security B1): each record below, alone, yields no event of
    any signature. The messages are what the error log parser stores (the line from the level bracket on, program
    empty, `ErrorLogLine.ParseNginx` / `ParseApache`):
    source `nginx-error`, `[error] 1021#1021: *15 open() "/var/www/vhosts/example.org/httpdocs/x" failed (2: No such file or directory), client: 198.51.100.7, server: example.org, request: "GET /x HTTP/1.1", host: "example.org", referrer: "No space left on device"`;
    the same with `referrer: "mariadbd: Disk full (x); Too many connections"`;
    source `nginx-error`, `[crit] 1021#1021: *16 open() "/var/www/vhosts/example.org/httpdocs/x" failed (28: No space left on device)" failed (2: No such file or directory), client: 198.51.100.7, server: example.org, request: "GET /x%22%20failed HTTP/1.1"` (a forged path);
    source `nginx-error`, `[error] 1021#1021: *15 FastCGI sent in stderr: "PHP message: x\nDisk full (y); waiting\nwarning: connect to mysql server x: Too many connections" while reading response header from upstream, client: 198.51.100.7` (lines of PHP output);
    source `apache-error`, `[core:error] [pid 902] [client 198.51.100.7:51234] AH00126: Invalid URI in request GET /x HTTP/1.1, referer: Disk is full writing './x'; Too many connections`;
    source `apache-error`, `[php:warn] [pid 901] [client 198.51.100.7:51234] PHP Warning:  x, referer: auth-worker(1): Error: mysql(x): Connect failed to database (y): Too many connections`.

- [ ] **AC3 — events reference their source lines, copies are one event, across real sources** (issue AC 2 and the
  comment). Records come from the real parsers on the fixtures, numbered by the test, and are fed in the order the store
  returns them (capture time, then ID).
  - `testdata/logs/kern.log-oom` parsed by `SyslogParser` once as `kern.log` (IDs 1 to *n*) and once as `syslog` (IDs
    continue): exactly one `oom_kill` event, host `web-1`, `victim_pid` 4242, `victim_command` `mariadbd`,
    `anon_rss_bytes` 7,577,600,000, `oom_score_adj` 0, `RecordIds` the ID of the report record of `kern.log`, then that
    of `syslog`, `LineCount` 2; no other event (the SYN-flooding and the `oom_reaper` lines match nothing).
    Test: `BuiltInSignaturesOomReportInSyslogAndKernLogIsOneEvent`.
  - `testdata/logs/mariadb-error.log` parsed by `MariaDbErrorLogParser`: exactly `mariadb_recovery_start`,
    `mariadb_recovery_end`, `mariadb_crash` (`signal` 6), `mariadb_recovery_start`, `mariadb_recovery_end`, each with the
    one ID of its entry; no `boot` event although the crash report quotes `Linux version` (program not `kernel`).
    Test: `BuiltInSignaturesMariaDbErrorLogYieldsCrashAndRecoveries`. (The expected list is read from the fixture and
    the event rules of the log import area; the Tester confirms it on the real parse.)
  - `testdata/logs/mail.log` parsed by `SyslogParser`: exactly one `postfix_fatal` event (host `mail-1`, the IDs of the
    03:12:09 `fatal:` and the 03:12:11 `panic:` lines, `LineCount` 2) and one `dovecot_fatal` event (the IDs of the
    03:12:17 `Panic:` and the 03:12:18 `Fatal:` lines), in this order; the forged `user=<x: Panic: y>` line yields
    nothing. Test: `BuiltInSignaturesMailLogYieldsOneEventPerDaemon`.
  - `testdata/logs/dovecot.log` parsed by `DovecotLogParser` (which tags the 03:12:17 `Panic:` line `dovecot.panic` and
    the 03:12:18 `Fatal:` line `dovecot.fatal`, pinned in `DovecotLogParserTests`): exactly one `dovecot_fatal` event
    whose `RecordIds` are the IDs of those two lines, `LineCount` 2; the forged 03:12:20 `user=<x: Panic: y>` line and the
    03:12:14 `Connect failed to database (mail): Can't connect ...` line (not `Too many connections`) yield nothing.
    Test: `BuiltInSignaturesDovecotLogYieldsOneFatalEvent`. (Lead decision in step 6: the fixture holds real panic and
    fatal lines, so the earlier "no event" statement for this file was a plan defect.)
  - `testdata/logs/nginx-error.log`, `apache-error.log` and `plesk-panel.log`, each parsed by `ErrorLogParser`: no event
    at all. Test: `BuiltInSignaturesErrorLogFixturesYieldNothing`.

- [ ] **AC4 — detector rules** (catalogs parsed from test text, independent of the built-in one). Test class
  `SignatureDetectorTests`:
  - The event lists the IDs of its lines in input order, `LineCount` their number, `SignatureId` the signature's ID and
    `CapturedAt` the first line's capture time: `SignatureDetectorEventListsTheRecordIdsOfItsLines`.
  - Merge: a line joins an open event of the same signature with equal fields when `line.CapturedAt − first.CapturedAt`
    is at most 60 s (exactly 60 s: merged; 60 s plus one tick: two events): `SignatureDetectorMergesCopiesWithinTheWindow`,
    `SignatureDetectorSeparatesLinesAfterTheWindow`; near the end of time (first line at `DateTimeOffset.MaxValue` minus
    30 s, second at `DateTimeOffset.MaxValue`) the lines merge and nothing throws (Security N1):
    `SignatureDetectorMergesNearTheLatestTime`; different fields, different signatures or different non-empty hosts are
    never merged: `SignatureDetectorSeparatesDifferentFields`, `SignatureDetectorNeverMergesDifferentSignatures`,
    `SignatureDetectorSeparatesDifferentHosts`; fields are compared one by one, never as a joined text: a two-text-field
    signature (`(?<a>[^;]*);(?<b>[^;]*);`) with `a|b;c;` and `a;b|c;` gives two events (Security N2):
    `SignatureDetectorComparesFieldsOneByOne`; an empty host merges with any host, and the event's host is the first
    non-empty host of its lines: `SignatureDetectorMergesAnEmptyHostWithAnyHost`; a field absent on one line and present
    on the other is a difference: `SignatureDetectorSeparatesAbsentFromPresentFields`.
  - Conditions: the origin condition holds when the signature names no program and no source, or the line's `program`
    is listed under `program`, or its record `source` is listed under `source` (either suffices); the event condition
    when no event is listed or the line's `event` is listed; the message condition when there is no pattern or it
    matches; every condition must hold; comparisons are ordinal and case-sensitive:
    `SignatureDetectorRequiresEveryCondition`, `SignatureDetectorMatchesAnyListedProgramOrEvent`,
    `SignatureDetectorMatchesAListedProgramOrAListedSource` (cases: program listed, source not; source listed, program
    not; neither listed: no event; only `source` given and the program anything: event). Records whose payload is not a
    `LogLine` are ignored, also for the order check: `SignatureDetectorIgnoresRecordsThatAreNoLogLines`.
  - Emission: `Add` adds to `closed` every event with `line.CapturedAt − first.CapturedAt > 60 s`, oldest first;
    `Complete` adds the rest in opening order; events opened by one line are in catalog order:
    `SignatureDetectorEmitsEventsWhenTheirWindowCloses`, `SignatureDetectorOrdersEventsOfOneLineByCatalog`.
  - Errors with exact texts: a log line earlier than the previous log line →
    `InvalidOperationException` "signature detection: lines must be added in capture order"; `Add` or `Complete` after
    `Complete` → `InvalidOperationException` "signature detection: already complete"; a null argument →
    `ArgumentNullException`. Tests: `SignatureDetectorRefusesLinesOutOfCaptureOrder`,
    `SignatureDetectorRefusesUseAfterComplete`.
  - Fields: `integer` and `kibibytes` per the number guard in *Security considerations*, with every case of its table
    (`SignatureDetectorConvertsIntegerFields`, `SignatureDetectorOmitsIntegerFieldsThatDoNotConvert`,
    `SignatureDetectorConvertsKibibytesToBytes`, `SignatureDetectorOmitsKibibytesThatDoNotConvert`). The test catalog
    captures with `^(?<v>[^;]*);` (the group takes every character but `;`, line feed and NUL included), so the capture
    is exactly the text before the first `;`. A field whose group did not take part or matched the empty string is
    absent, and the event is still detected (`SignatureDetectorOmitsFieldsWithoutText`); a `text` field is cut to 1,024
    UTF-8 bytes at a character boundary (2,000 times `é` gives 512 characters; 1,023 ASCII characters plus `é` gives the
    1,023 characters): `SignatureDetectorCutsTextFieldsAt1024Bytes`. The non-ASCII digit cases (Arabic-Indic, U+2212,
    full-width) carry a guard that fails when the escape was lost: the test first asserts the input holds a character
    above U+007F.
  - Output: the lists of an emitted event (`RecordIds`) and of a catalog (`Signatures`, `Programs`, `Sources`, `Events`,
    `Fields`) cannot be cast to `List<T>` or `T[]` (Security N7); an emitted event's collections are not changed by later
    `Add` calls: `SignatureDetectorEmitsEventsItNoLongerChanges`, `SignatureCatalogListsAreReadOnly`.

- [ ] **AC5 — a new signature without an engine change** (issue AC 3): `SignatureCatalog.Parse` on a text with the
  signature `segfault` (program `kernel`, message `^(?<command>[^ \[\n]+)\[(?<pid>[0-9]+)\]: segfault at `, fields
  `command: text`, `pid: integer`) and a detector on it turn the kernel line
  `nginx[1234]: segfault at 0 ip 00007f sp 00007f error 4 in libc.so.6` into one `segfault` event with `command` `nginx`
  and `pid` 1234. Test: `SignatureCatalogParseAcceptsASignatureTheEngineHasNeverSeen`.

- [ ] **AC6 — catalog validation.** `SignatureCatalog.Parse("test.yaml", text)` reads every key
  (`SignatureCatalogParseReadsEveryKey`: ID, description, programs, sources, events, pattern options, fields with their
  types in document order) and refuses each rule break with `SignatureException` and exactly the text below (one break
  per test document; `N` is the line given). Test: `SignatureCatalogParseRefusesInvalidCatalogs` (one `DataRow` per
  row and per listed construct), plus `SignatureCatalogParseKeepsTheYamlErrorAsInner` (the not-valid-YAML case has a
  `ConfigException` as inner exception).

  | Rule break | Message |
  | ---------- | ------- |
  | text that is not one valid YAML document (syntax error, second document, nesting deeper than 64) | `signatures: test.yaml: not valid YAML` |
  | empty text, text with comments only, or a document whose root is empty (null) | `signatures: test.yaml: no signatures` |
  | root not a mapping, a root key other than `signatures`, or no key `signatures` (N: line of the root, or of the other key) | `signatures: test.yaml:N: the document must be a mapping with the single key signatures` |
  | `signatures` not a non-empty sequence (N: line of the key `signatures`) | `signatures: test.yaml:N: signatures must be a non-empty list` |
  | an entry that is not a mapping (N: its line) | `signatures: test.yaml:N: a signature must be a mapping` |
  | an anchor, alias or tag on any node (N: that node's line) | `signatures: test.yaml:N: anchors, aliases and tags are not supported` |
  | a key repeated in one mapping (N: line of the second) | `signatures: test.yaml:N: duplicate key` |
  | an unknown key in a signature (N: line of the key) | `signatures: test.yaml:N: unknown key` |
  | no `id` / no `description` (N: line of the signature) | `signatures: test.yaml:N: id is required` / `signatures: test.yaml:N: description is required` |
  | an ID that is not a scalar matching `^[a-z][a-z0-9_]{0,63}$` (N: line of the key `id`) | `signatures: test.yaml:N: id must match ^[a-z][a-z0-9_]{0,63}$` |
  | an ID used before (N: line of the key `id`) | `signatures: test.yaml:N: duplicate id` |
  | a description that is not a scalar of 1 to 1,024 UTF-8 bytes | `signatures: test.yaml:N: description must be text of 1 to 1024 bytes` |
  | `program` not a sequence of 1 to 16 scalars of 1 to 1,024 UTF-8 bytes | `signatures: test.yaml:N: program must be a list of 1 to 16 names of 1 to 1024 bytes` |
  | `source` not a sequence of 1 to 16 scalars matching the wire format's name pattern | `signatures: test.yaml:N: source must be a list of 1 to 16 source names` |
  | `event` not a sequence of 1 to 16 scalars matching the wire format's name pattern | `signatures: test.yaml:N: event must be a list of 1 to 16 event names` |
  | `message` not a scalar of 1 to 4,096 characters | `signatures: test.yaml:N: message must be a pattern of 1 to 4096 characters` |
  | `message` that the regular expression parser refuses, or a construct the non-backtracking engine does not support — one `DataRow` each for lookahead `(?=a)b`, lookbehind `(?<=a)b`, atomic group `(?>a)`, backreference to an existing group `(?<a>x)\k<a>` (with field `a: text`), conditional `(?<a>x)?(?(a)y\|z)` (with field `a: text`) (Security N8) | `signatures: test.yaml:N: message is not a valid pattern` |
  | neither `event` nor `message` (N: line of the signature) | `signatures: test.yaml:N: a signature needs an event or a message` |
  | `fields` without `message` | `signatures: test.yaml:N: fields need a message` |
  | `fields` not a mapping of 1 to 8 entries | `signatures: test.yaml:N: fields must map 1 to 8 names to types` |
  | a field name not matching `^[a-z][a-z0-9_]{0,63}$` (N: line of that field) | `signatures: test.yaml:N: field name must match ^[a-z][a-z0-9_]{0,63}$` |
  | a field type other than `text`, `integer`, `kibibytes` (N: line of that field) | `signatures: test.yaml:N: field type must be text, integer or kibibytes` |
  | a field that is no named group of `message` (N: line of that field) | `signatures: test.yaml:N: field is not a named group of message` |
  | a named group of `message` that is no field (N: line of the key `message`) | `signatures: test.yaml:N: a named group of message is not a field` |

  Unless stated otherwise, N is the line of the key whose value breaks the rule. The key names `signatures`, `id`,
  `description`, `program`, `source`, `event`, `message`, `fields` are the whole schema; no message contains document
  text. The pattern is compiled before the fields are compared with its groups, so a refused construct reports "message
  is not a valid pattern" even when its fields are declared. (In the YAML of the conditional row the `|` is written
  inside a quoted scalar; the table escapes it only for Markdown.)

- [ ] **AC7 — bounded on hostile input** (spec AC-S5). Tests:
  - `SignatureDetectorClosesTheOldestEventBeyondTheOpenLimit` (1,025 lines with distinct fields within one window:
    adding the 1,025th puts the first event into `closed`);
  - `SignatureDetectorCountsLinesBeyondTheIdLimit` (300 copies: `RecordIds` holds the first 256 IDs, `LineCount` 300);
  - `SignatureDetectorRetainsBoundedMemoryUnderAFlood` (heap bound, revised for Security B2): a test catalog with one
    signature, message `^(?<t>[^;]*);`, field `t: text`. Each of 3,000 lines within one window has a message of
    16,380 ASCII bytes: a text capture of 16,000 characters that starts with the line's number in eight digits (so the
    first 1,024 bytes, and the events, differ), then `;`, then filler. Lines are built one at a time and not kept by the
    test; closed events go to a collection that counts and discards them. The baseline is taken after one warm-up line
    of the same shape was added to and completed on a separate detector on the same catalog (the pattern's lazily
    built DFA cache exists before the baseline); the detector under test is created before the baseline. The memory
    retained after the last `Add` minus the baseline stays below 8 MiB, measured with the `RetainedBytes` helper form
    of `.squad/stack.md` (*Writing tests*). With 1,024 open events, an implementation that keeps an uncut field, the
    message, the `Match` or the record per open event retains at least 1,024 × 32,000 bytes (UTF-16) and fails; the
    correct one keeps about 1,024 × 2 KiB of text plus small per-event state. The test also asserts that 1,976 events
    (3,000 − 1,024) were closed. (Revised in step 6 from 20,000 lines: 3,000 still fill all 1,024 open events and evict
    1,976, which is all the bound needs, since the retained state is capped by the open limit and not by the line count;
    20,000 lines took 8 to 10 s; 3,000 is the smallest input that still detects the failure.)
  - and AC1's `BuiltInSignaturesPatternsRunWithoutBacktracking`. No wall-clock bound is tested.

## Approach

Everything new lives in `src/Vandox.Core/Signatures` (namespace `Vandox.Core.Signatures`), next to the parsers whose
records it reads; Vandox.Core already has YamlDotNet and the strict YAML tree reader. No new project, package, storage
step or wire change.

**Catalog (`signatures.yaml`, embedded resource `Vandox.Core.Signatures.signatures.yaml`).** The Dev writes it exactly
as below (the comments are part of the file). Loaded once by `BuiltInSignatures.Catalog` through `SignatureCatalog.Parse`.

```yaml
# Signatures of known events: decision record 0094, rules in docs/areas/detection-and-reports.md (Log signatures).
# A signature matches one log line: every given condition must hold. program and source together are one condition
# (the line's program or its record source is listed); event and message are one condition each.
# Patterns run with RegexOptions NonBacktracking, Multiline, CultureInvariant and ExplicitCapture; write [0-9], never
# the Unicode digit class, and exclude \n from character classes. Every named group is a field, and every field is a
# named group. A signature that reads a message which can hold remote text (web server lines, mail addresses, user
# names) anchors its pattern with \A at server-written text, never with ^ (which matches after every line feed).
# Adding a signature: an entry here plus a positive and a negative test in BuiltInSignaturesTests.
signatures:
  - id: oom_kill
    description: The kernel's OOM killer killed a process.
    program: [kernel]
    message: '^(?:\[ *[0-9]+\.[0-9]+\] )?(?:Out of memory(?: \(oom_kill_allocating_task\))?|Memory cgroup out of memory): Killed process (?<victim_pid>[0-9]+) \((?<victim_command>.*)\) total-vm:[0-9]+kB, anon-rss:(?<anon_rss_bytes>[0-9]+)kB(?:.* oom_score_adj:(?<oom_score_adj>-?[0-9]+))?'
    fields:
      victim_pid: integer
      victim_command: text
      anon_rss_bytes: kibibytes
      oom_score_adj: integer
  - id: mariadb_crash
    description: MariaDB stopped on a fatal signal.
    event: [mariadb.abort]
    message: 'got signal (?<signal>[0-9]+) ;$'
    fields:
      signal: integer
  - id: mariadb_recovery_start
    description: MariaDB started a crash recovery.
    event: [mariadb.recovery_start]
  - id: mariadb_recovery_end
    description: MariaDB finished a crash recovery.
    event: [mariadb.recovery_end]
  - id: mariadb_too_many_connections
    description: Postfix or Dovecot could not connect to MariaDB because of Too many connections (error 1040).
    program: [postfix/proxymap, postfix/cleanup, postfix/smtpd, postfix/trivial-rewrite, postfix/local, postfix/virtual, dovecot]
    source: [dovecot]
    message: '\A(?:warning: connect to mysql server [^ \n]+: Too many connections|auth-worker\([0-9]+\): Error: mysql\([^)\n]*\): Connect failed to database \([^)\n]*\): Too many connections)'
  - id: service_start
    description: systemd started a unit (on systemd 249 named by its description).
    program: [systemd]
    message: '^Started (?:(?<unit>[^ \n]+\.service) - )?(?<description>.+)\.$'
    fields:
      unit: text
      description: text
  - id: service_stop
    description: systemd stopped a unit (on systemd 249 named by its description).
    program: [systemd]
    message: '^Stopped (?:(?<unit>[^ \n]+\.service) - )?(?<description>[^(\n].*)\.$'
    fields:
      unit: text
      description: text
  - id: service_failed
    description: A systemd service failed.
    program: [systemd]
    message: '^(?<unit>[^ \n]+\.service): Failed with result ''(?<result>[^''\n]+)''\.$'
    fields:
      unit: text
      result: text
  - id: disk_full
    description: MariaDB could not write because a file system was full.
    program: [mariadbd, mysqld]
    source: [mariadb]
    message: '\A(?:[^ \n]+: )?(?:Disk full \(|Disk is full writing '')'
  - id: postfix_fatal
    description: Postfix logged a fatal error or a panic.
    event: [postfix.fatal, postfix.panic]
  - id: dovecot_fatal
    description: Dovecot logged a fatal error or a panic.
    event: [dovecot.fatal, dovecot.panic]
  - id: boot
    description: The kernel started (the first kernel line of a boot).
    program: [kernel]
    message: '^(?:\[ *[0-9]+\.[0-9]+\] )?Linux version (?<kernel_version>[^ \n]+) '
    fields:
      kernel_version: text
```

The first plan's patterns were exercised once in a scratch program on .NET 10 (`NonBacktracking` with the options above,
against the default engine): the OOM pattern gives the same captures on both engines for the fixture's kill line, a comm
holding `) total-vm:1kB`, a comm holding ` oom_score_adj:`, a kill line without `oom_score_adj` (group absent) and a
second line that ends in `oom_score_adj:5` (not read: `.` stops at the line feed); the stop pattern refuses
`Stopped (with error) ...`; a lookahead and an atomic group throw `NotSupportedException`, a backreference to a missing
group `RegexParseException`; the default `\d` class matches U+0663. The revised patterns (`\A`, `[^ \n]`, the two
narrowed signatures) and the further refused constructs of AC6 were **not** run again; the Tester's tests are the
verification.

**Remote text (Security B1, decided: restrict).** A signature that has no event condition and reads text that a
remote party can put into a message must (1) carry an origin condition naming the writers whose lines it reads, and
(2) anchor its pattern with `\A` at text that writer prints before any external text. `disk_full` reads MariaDB's own
error messages (error log source `mariadb`, or program `mariadbd`/`mysqld` in the journal and syslog), and
`mariadb_too_many_connections` the MySQL client errors of Postfix and Dovecot. The other writers the first plan matched
(PHP warnings in the web server and Plesk logs, nginx's own write errors, Postfix write errors) are out of scope: their
messages start with server text but carry external text before the error (a requested path in nginx's `open() "..."`,
FastCGI stderr with application output over several lines), and each needs a pattern per format that is checked against
the real format first — follow-up issue below. The remaining reach is documented: any local process can write lines
with these programs (as for every signature), and a client of MariaDB can forge a whole MariaDB entry through a failed
login's user name (record 0088), so `disk_full` and the `mariadb_*` signatures inherit that limitation.

**Loading (`SignatureCatalog.Parse`).** Reads the text with `YamlTreeReader.Read` (a `ConfigException` becomes the
"not valid YAML" error with it as inner exception), walks the tree and applies the AC6 rules. A pattern is compiled with
`Signature.PatternOptions` before its fields are checked; `ArgumentException` (including `RegexParseException`) and
`NotSupportedException` from the constructor become "message is not a valid pattern". Group names are
`Regex.GetGroupNames()` without `0`. Field order is document order. Every list the catalog exposes is a
`ReadOnlyCollection<T>` over an array no one else holds (`Array.AsReadOnly`), so it cannot be cast back to a mutable
list.

**Matching (`SignatureDetector`).** For each `Add`: a null argument throws; after `Complete` any record throws; a record
whose `Data` is not a `LogLine` is ignored. For a log line:
1. If its `CapturedAt` is before the previous log line's, throw. The detector keeps only the previous log line's
   `CapturedAt` for this check.
2. Close (add to `closed`) every open event for which `line.CapturedAt − first.CapturedAt > MergeWindow`, oldest first
   (a subtraction, never `first + MergeWindow`, which overflows near `DateTimeOffset.MaxValue`). With one window for
   every signature, these are always a prefix of the open events in opening order.
3. For each signature in catalog order: the origin condition holds when `Programs` and `Sources` are both empty, or
   `Programs` contains `LogLine.Program`, or `Sources` contains `DataRecord.Source` (ordinal); the event condition when
   `Events` is empty or contains `LogLine.Event`; the message condition when there is no pattern or
   `Pattern.Match(LogLine.Message)` succeeds (the first match only). When all hold, convert the fields (rules in
   *Security considerations*), then look up the open events with the same signature and equal fields and take the
   oldest whose host equals the line's host or where either host is empty. If found, append the record ID (while fewer
   than 256 are kept), add 1 to the line count and, when the event's host is empty, take the line's host. Otherwise open
   a new event; when 1,024 events are open, first close the oldest.
4. The merge key is a structured value: the signature and, per field in catalog order, whether it is present and its
   value (text compared ordinally, number compared as `long`); equality and hash code go field by field, never through a
   joined string. A dictionary maps a key to the open events with that key (they differ only by host). Per matching
   signature a line costs one dictionary lookup plus a scan of that key's open events, at most 1,024 (an empty host is
   compatible with every host, so the candidates cannot be narrowed by host further); this bound, not independence from
   the number of open events, is the claim.
5. What is retained: an open event and its key hold only the converted fields (texts already cut to 1,024 bytes,
   numbers), the host string, the record IDs (at most 256), the first line's `CapturedAt` and the line count — never the
   `Match`, a `Group`, the message, the `LogLine` or the `DataRecord`. A `text` field is cut with `Utf8Text.Cut`
   before it is stored, so the stored string does not share the message's memory (`Group.Value` allocates a new string;
   the cut takes a substring of it).
6. An emitted `DetectedEvent` gets collections of its own (`RecordIds` as a `ReadOnlyCollection<long>` over a new
   array, `Texts` and `Numbers` as new dictionaries); the detector holds no reference to them after emitting.

`Complete` closes every open event in opening order and marks the detector complete.

## Options considered

Weighed in full in [record 0094](../../docs/decisions/0094-log-signatures-embedded-yaml-linear-patterns-on-stored-lines.md):

- Where detection runs: in the parsers as a further `event` (lost: earlier imports are never parsed again, copies in
  different files cannot be merged); **on stored lines, on demand, nothing stored (chosen)**; events in a table of their
  own (lost for now: schema step, stale events; #23 decides what it stores); `kernel_event` records (lost: no kind for
  most types, records cannot reference records).
- Pattern language: code per signature (excluded by the issue); **YAML with regular expressions on the non-backtracking
  engine (chosen)**; a hand-written template language (lost: alternation, optional parts and delimiter choice would be
  new engine code to secure); operator-configured signature files (lost: new option, untrusted and untested patterns).
- Counting copies: every line an event (lost: counts a kill up to three times); identical message only (lost: journal
  and syslog differ in the printk prefix, MariaDB entries in grouping); **same signature, compatible host, equal fields,
  within 60 s of the first line (chosen)**. A per-signature window was considered and dropped: one window keeps the
  closing order a simple prefix and needs no tuning per entry; the cost is that real repeats with equal fields within a
  minute fold into one event whose lines are all linked and counted.
- Kernel origin: keep `_TRANSPORT` (lost: new wire, model and storage field, and syslog cannot tell either);
  **program `kernel` as a classification, documented (chosen)**.
- Message text a remote party controls (new, Security B1): a message-only signature matching anywhere (lost: any HTTP
  client triggers it through a referrer or a path); accept that reach and document it with a pinning test (lost: false
  events from ordinary traffic — a referrer or search text quoting an error — in the reconstruction of an outage, and
  0091 already keeps remote text from mail events); **an origin condition (`program` or `source`) and a `\A` anchor at
  server-written text, the signatures narrowed to writers whose format allows that (chosen)**. Within it: `source`
  ANDed with `program` (lost: MariaDB's lines arrive with source `mariadb` and no program from the error log, but with
  program `mariadbd` and source `journal`/`syslog` from the journal, so one signature could not cover both) versus one
  origin condition met by either (chosen).
- API shape: `Add`/`Complete` with a caller's collection, as `KernelReportGrouper` does (chosen) versus returning all
  events at the end (lost: memory grows with the number of events over a whole archive).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `Vandox.Core` | `Signatures/BuiltInSignatures.cs` | new: the embedded catalog, loaded once |
| `Vandox.Core` | `Signatures/SignatureCatalog.cs` | new: parses and checks a catalog text |
| `Vandox.Core` | `Signatures/Signature.cs`, `Signatures/SignatureField.cs`, `Signatures/SignatureFieldType.cs` | new: a loaded signature, its fields and field types |
| `Vandox.Core` | `Signatures/SignatureException.cs` | new: catalog error |
| `Vandox.Core` | `Signatures/DetectedEvent.cs` | new: one detected event |
| `Vandox.Core` | `Signatures/SignatureDetector.cs` | new: matches, merges and emits |
| `Vandox.Core` | `Signatures/signatures.yaml` | new: the built-in catalog (content above) |
| `Vandox.Core` | `Vandox.Core.csproj` | add `<EmbeddedResource Include="Signatures/signatures.yaml" LogicalName="Vandox.Core.Signatures.signatures.yaml" />` in an `ItemGroup` |

Further internal helper types (for example an open-event type, the merge key, the number converter, or a reader class
that keeps `SignatureCatalog.Parse` below the cognitive complexity limit of 15) are the Dev's choice, one type per file
in `Signatures/`, named in the Dev's report.

## Signatures (for the Dev's skeleton)

```csharp
namespace Vandox.Core.Signatures;

public static class BuiltInSignatures
{
    internal const string ResourceName = "Vandox.Core.Signatures.signatures.yaml";

    // Loaded once (Lazy<SignatureCatalog>); a missing resource is InvalidOperationException
    // "signatures: the built-in catalog is missing".
    public static SignatureCatalog Catalog { get; }
}

public sealed class SignatureCatalog
{
    internal const int MaxPatternChars = 4096;
    internal const int MaxListEntries = 16;
    internal const int MaxFields = 8;
    internal const int MaxDescriptionBytes = 1024;

    private SignatureCatalog(IReadOnlyList<Signature> signatures);

    // A ReadOnlyCollection<Signature>.
    public IReadOnlyList<Signature> Signatures { get; }

    /// <exception cref="SignatureException">The text breaks a catalog rule</exception>
    public static SignatureCatalog Parse(string name, string text);
}

public sealed class Signature
{
    internal const RegexOptions PatternOptions = RegexOptions.NonBacktracking | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    internal Signature(string id, string description, IReadOnlyList<string> programs, IReadOnlyList<string> sources, IReadOnlyList<string> events, Regex? pattern, IReadOnlyList<SignatureField> fields);

    public string Id { get; }
    public string Description { get; }
    // Each list a ReadOnlyCollection<T>; empty when the key is absent.
    public IReadOnlyList<string> Programs { get; }
    public IReadOnlyList<string> Sources { get; }
    public IReadOnlyList<string> Events { get; }
    public IReadOnlyList<SignatureField> Fields { get; }
    internal Regex? Pattern { get; }
}

public sealed class SignatureField
{
    internal SignatureField(string name, SignatureFieldType type);

    public string Name { get; }
    public SignatureFieldType Type { get; }
}

public enum SignatureFieldType
{
    Text,
    Integer,
    Kibibytes
}

public sealed class SignatureException : Exception
{
    public SignatureException(string message);
    public SignatureException(string message, Exception innerException);
}

public sealed class DetectedEvent
{
    public string SignatureId { get; set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; set; }
    public string Host { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> Texts { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, long> Numbers { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);
    public IReadOnlyList<long> RecordIds { get; set; } = Array.AsReadOnly(Array.Empty<long>());
    public long LineCount { get; set; }
}

public sealed class SignatureDetector
{
    internal const int MaxOpenEvents = 1024;
    internal const int MaxRecordIds = 256;
    internal const int MaxTextBytes = ModelLimits.MaxShortTextBytes;
    internal static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(60);

    public SignatureDetector(SignatureCatalog catalog);

    /// <exception cref="ArgumentNullException">record or closed is null</exception>
    /// <exception cref="InvalidOperationException">a log line before the previous one, or after Complete</exception>
    public void Add(long recordId, DataRecord record, ICollection<DetectedEvent> closed);

    /// <exception cref="InvalidOperationException">called after Complete</exception>
    public void Complete(ICollection<DetectedEvent> closed);
}
```

`LineCount` is a `long` (Security N6): it cannot overflow for any input the store can hold. Every member gets XML
documentation and `#region` blocks as the analyzers require; the comments above are guidance, not the final
documentation. No existing file is rewritten except `Vandox.Core.csproj` (one `ItemGroup` added; read and found final
otherwise).

## Test files

- `tests/Vandox.Core.Tests/SignatureCatalogTests.cs` — AC5, AC6, `SignatureCatalogListsAreReadOnly`.
- `tests/Vandox.Core.Tests/SignatureDetectorTests.cs` — AC4, AC7.
- `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs` — AC1, AC2, AC3.
- If the Tester needs a builder for log line records shared by these classes, it is
  `tests/Vandox.Core.Tests/SignatureSamples.cs` (internal static class); fixtures are read with `RepositoryFiles.Path`,
  the parsers driven through `RecordingEmitter.ParseAsync`.

Existing test code that calls a changed signature: none (no existing signature changes).

## Areas

- `docs/areas/detection-and-reports.md` (owner: Dev): *Scope* — the signatures are implemented, the rules and thresholds
  not yet; a new section **Log signatures** stating, normatively and without class names as a contract: the catalog is
  data embedded in the backend, and a new signature is an entry plus tests; the signature list with each one's
  conditions and fields (the table of AC2's positives as prose or a table: ID, what it recognizes, which writers —
  `disk_full` MariaDB only, `mariadb_too_many_connections` Postfix and Dovecot only —, fields with units —
  `anon_rss_bytes` in bytes, converted from the kernel's kB); the matching rules (the origin condition met by a listed
  program or a listed source, event and message conditions, first match per signature, a line may yield several events
  in catalog order, the `\A` anchoring rule for messages that can hold remote text); the field rules (types, the number
  guard, the 1,024-byte cut, absent fields); the merge rule (same signature, equal fields compared one by one,
  compatible host with the empty host matching any, within 60 s of the first line, the oldest candidate; the first
  non-empty host); emission and order (lines in capture order, events emitted when their 60 s pass or at completion, in
  opening order); the bounds (1,024 open events, 256 IDs per event with the full count); that nothing is stored and no
  record is changed, and that no command or view shows events yet. The limitations:
  - an event is a classification, not proof of origin: any local process can log as `kernel`, `systemd`, `mariadbd`,
    `postfix/*` or `dovecot`, and `_TRANSPORT` is not kept; a client of MariaDB can forge a whole MariaDB entry through
    a failed login's user name (see *Log import*, *MariaDB error log*), which reaches `disk_full` and the `mariadb_*`
    signatures;
  - disk-full and Too many connections lines of other writers (PHP, nginx, Apache, Plesk) are not recognized;
  - (DA 1) `postfix_fatal` and `dovecot_fatal` cover both the fatal and the panic event and have no field, so a fatal
    and a panic of one daemon within 60 s are one event, and nothing in the event tells which of the two it was; the
    linked lines and their `event` do;
  - (DA 2) merging assumes the copies' capture times are comparable: a syslog file read in a wrong `import.time_zone`
    (or across a DST change the zone does not describe) lies hours off its journal copy, so the two are more than 60 s
    apart and counted as two events;
  - (Security N4) a flood of 1,024 or more distinct matching lines within 60 s (which any local process, and for some
    signatures a MariaDB client, can write) closes events early, so the later copy of a real event can become an event
    of its own;
  - on systemd 249 start and stop name the description, so scopes and timers match too; real repeats with equal fields
    within 60 s are one event; the kill line of `oom_kill_allocating_task` is recognized, but its report is not ended by
    the grouper (#202).
  - (Security N5) field texts are external text: they may hold control characters (a `text` field's pattern may span
    a line feed only where the catalog allows it, and the built-in patterns exclude it), and showing them falls under
    security area 12 (*Logging and display of external data*).
  *Related decisions*: add 0094. *Not here*: incidents (#23). Add an *Implementation* line pointing at
  `Vandox.Core.Signatures`.

## Documentation updates

| File | Change | Owner |
| ---- | ------ | ----- |
| `docs/areas/detection-and-reports.md` | as in *Areas* | Dev |
| `docs/ARCHITECTURE.md` | intro paragraph: the log signature catalog and its detector exist (not called by the service yet); *Components*, the `Vandox.Core` list: "the log signatures, an embedded catalog, and the detector that matches them on stored log lines"; the `internal/` bullet: remove the stale "signatures,"; *Data flow* records list: add 0094 | Dev |
| `README.md` | *Layout*: the `src/Vandox.Core/` line adds "log signatures" | Dev |
| `.squad/stack.md` | *Layout*: `src/Vandox.Core` list adds "log signatures" | Dev |
| `.squad/project.md` | *Security areas* 10: add the signature detection (`SignatureCatalog`, `SignatureDetector`, `BuiltInSignatures`: embedded catalog, patterns on the non-backtracking engine in linear time with unsupported constructs refused at load, a signature without an event condition carries an origin condition and a `\A` anchor at server-written text so remote text in web server or mail lines cannot trigger it, integer fields accepted only as ASCII digits by an ordinal scan and range-checked before use, text fields cut to 1,024 bytes, at most 1,024 open events and 256 IDs per event; an event is a classification, not proof of origin, and MariaDB entries forged through a login name reach the MariaDB signatures; record 0094); *Security areas* 12: add the text fields of detected events; *Test doubles*, row "log parsers (C#)": add `SignatureSamples` if the Tester creates it | Dev |
| `docs/decisions/0094-...md` and the index row | written (Proposed), revised for B1; set to Accepted at approval | Lead |

## Architecture check

- Deterministic detection (0008): signatures are fixed data and the detector is deterministic for the same input order;
  no AI. Kept.
- Records are never updated (storage *Writing*, security area 1): the detector only reads records it is given and writes
  nothing. Kept.
- Backfilled or imported data never alerts (0022): no alert exists; detected events are analysis only. Kept.
- No data gaps unless recorded, a hanging collector never blocks the agent: not touched (backend only, no agent code).
- `docs/ARCHITECTURE.md` changes only as listed (a new part of `Vandox.Core`, a stale word removed); no guarantee or flow
  changes, so no Product Manager decision is needed. Narrowing `disk_full` and `mariadb_too_many_connections` to the
  writers named above narrows a feature not yet released and not yet consumed; the other writers are a follow-up.

## Security considerations

- [x] Limits and whether they apply before or after decoding: the detector reads decoded `string` messages of stored
  records, already bounded to 16,384 UTF-8 bytes by the record rules; it imposes no byte bound of its own on them. A
  `text` field is cut to 1,024 **UTF-8 bytes of the decoded string** (a U+FFFD from an invalid input byte counts three)
  at a character boundary (`Utf8Text.Cut`). Pattern length 4,096 characters, description 1,024 UTF-8 bytes, 16 entries
  per list, 8 fields: catalog limits, on the YAML scalar values.
- [x] Exceptions that can reach a user: none — no command, endpoint or view calls the detector or loads a catalog in
  this change. `SignatureException` texts carry the catalog name, a line number and the fixed reasons of AC6, never a
  pattern or a value; the detector's `InvalidOperationException` texts are the fixed texts of AC4.
- Time: every pattern runs on the non-backtracking engine, linear in the message length; constructs it cannot run are
  refused when the catalog loads (AC6 lists lookahead, lookbehind, atomic groups, backreferences, conditionals), so no
  pattern can fall back to backtracking; no match timeout is needed. Per line, only the signatures whose origin and
  event conditions hold run a pattern, each at most once; the merge lookup per matching signature is one dictionary
  lookup and a scan of at most 1,024 candidates. The window check is a subtraction of two `DateTimeOffset` values, which
  cannot overflow (their difference is at most about 10,000 years, far inside `TimeSpan`).
- Memory: at most 1,024 open events, each with at most 256 IDs and at most 8 fields of at most 1,024 bytes; open events
  keep nothing else of a line (*Approach*, step 5); closed events go to the caller's collection. AC7 pins the open-event
  and ID caps and a heap bound that an uncut field, a kept message or a kept `Match` exceeds.
- Remote text (B1): see *Approach*, *Remote text*. Message-only matching anywhere is not used; the two signatures
  without an event condition that read such messages are restricted by origin and anchored with `\A`; AC2's
  `BuiltInSignaturesClientTextInWebServerLogsYieldsNothing` pins referrer, path, FastCGI and Apache referer cases, and
  the second-line cases pin `\A` against `^`.
- Number guard (a validation on captured text before conversion). The forms a capture can hold are whatever the pattern
  admits, so the guard is in the converter, not in the patterns. It is an ordinal scan over the **whole** captured
  string, with no parsing API before it:
  1. `integer` only: if the first character is `-` (U+002D), note the sign and start after it. `kibibytes` accepts no
     sign.
  2. The remaining length must be 1 to 19, and every remaining character must be in `'0'..'9'` (U+0030 to U+0039,
     compared as `char` values); anything else, anywhere, makes the field absent.
  3. Convert in the same loop: accumulate `value = value * 10 + (c - '0')` in a `ulong` (19 digits cannot overflow it).
     `integer`: without a sign the field is present when `value <= 9,223,372,036,854,775,807` (`long.MaxValue`); with a
     sign when `value <= 9,223,372,036,854,775,808` (the result is `long.MinValue` for exactly that value, otherwise
     `-(long)value`). `kibibytes`: present when `value <= 9,007,199,254,740,991` (`long.MaxValue / 1024`), the result
     `(long)value * 1024`. (`long.Parse` with `NumberStyles.None` and `CultureInfo.InvariantCulture` is allowed only
     after the scan has passed; the plan prefers the loop.)

  | Captured text | `integer` | `kibibytes` |
  | ------------- | --------- | ----------- |
  | `42`, `0042` | 42 | 43,008 |
  | `0` | 0 | 0 |
  | `-0` | 0 | absent |
  | `-17` | -17 | absent |
  | `9223372036854775807` | 9,223,372,036,854,775,807 | absent |
  | `-9223372036854775808` | -9,223,372,036,854,775,808 | absent |
  | `9223372036854775808`, `-9223372036854775809`, `00000000000000000001` (20 digits) | absent | absent |
  | `7400000` | 7,400,000 | 7,577,600,000 |
  | `9007199254740991` | 9,007,199,254,740,991 | 9,223,372,036,854,774,784 |
  | `9007199254740992` | 9,007,199,254,740,992 | absent |
  | `-1` | -1 | absent |
  | `+5`, `-+5`, `--5`, `-`, `5 ` (trailing space), ` 5` (leading space), `5\n` (line feed), `5\0` (NUL), `1,000`, `4.2`, `1e3`, `0x1F`, `(5)` | absent | absent |
  | `−5` (U+2212 MINUS SIGN then `5`), `５` (U+FF15 FULLWIDTH DIGIT FIVE), the Arabic-Indic digits U+0664 U+0662 | absent | absent |
  | empty, or the group did not take part | absent | absent |

  The non-ASCII rows are written in the tests as escapes (`\u2212`, `\uFF15`, `\u0664\u0662`) with the guard of AC4
  (the input holds a character above U+007F, so a lost backslash fails). `5\n` and `5\0` are the C# escapes for a line
  feed and a NUL. An absent field never suppresses the event.
- Forged lines: an event is a classification of text (record 0094, option 13; 0086 option 19; 0088; 0091) and says so in
  the area document; events list their record IDs so a consumer can weigh them.
- The catalog is trusted data compiled into the binary; `SignatureCatalog.Parse` is nevertheless strict (AC6), and the
  YAML tree reader bounds nesting and never expands aliases.

## Decision records

- `docs/decisions/0094-log-signatures-embedded-yaml-linear-patterns-on-stored-lines.md` (Proposed) — where detection runs,
  the pattern language, how copies are counted, `_TRANSPORT` not kept, how message text a remote party controls is kept
  out (origin condition and `\A` anchor, the narrowed writers).

## Challenge

Devil's Advocate (0 major, 2 minor):

1. *Fatal and panic merge into one `postfix_fatal`/`dovecot_fatal` event within 60 s, no field distinguishes them.* —
   **Accepted** as proposed: no field added; the area document states it (*Areas*, limitation DA 1).
2. *Copy merging assumes comparable capture times; a wrong import time zone or DST puts syslog and journal copies more
   than 60 s apart.* — **Accepted**: stated under the limitations of the area document (*Areas*, limitation DA 2).

## Security review

Plan review, CHANGES_REQUIRED:

- **B1 (blocking), remote-triggerable `disk_full` and `mariadb_too_many_connections`.** — **Accepted, option (a)
  restrict.** The schema gains a `source` condition that forms one origin condition with `program`; the two signatures
  carry origin conditions and `\A`-anchored patterns at server-written text: `disk_full` reads MariaDB's own messages
  (source `mariadb`, program `mariadbd`/`mysqld`), `mariadb_too_many_connections` the MySQL client errors of Postfix and
  Dovecot. PHP, nginx, Apache and Plesk writers are out of scope (follow-up issue): their formats put external text
  before the error and need patterns checked per format. AC1 adds `BuiltInSignaturesEverySignatureHasAnEventOrOriginCondition`
  and the `\A` requirement; AC2 adds `BuiltInSignaturesClientTextInWebServerLogsYieldsNothing` (nginx `referrer:`, a
  forged nginx path, FastCGI stderr lines, Apache `referer:`) and second-line negatives pinning `\A` against `^`; AC3
  adds the real error-log fixtures. The remaining reach (local processes; MariaDB entries forged through a login name,
  0088) is documented in the area document, `.squad/project.md` area 10 and record 0094.
- **B2 (blocking), AC7 heap test too weak.** — **Accepted.** The flood now uses messages of 16,380 bytes with 16,000-
  character captures; *Approach* step 5 states what an open event and its key keep (never `Match`, message, `LogLine`
  or record; the order check keeps only the previous `CapturedAt`); the baseline is taken after a warm-up match on the
  same catalog (DFA cache).
- **B3 (blocking), number guard bypass forms.** — **Accepted.** The guard is specified as an ordinal scan over the
  whole string with the conversion in the same loop (*Security considerations*), and the table carries every listed
  case: `5\n`, `5\0`, `5 `, ` 5`, `-`, `--5`, `-+5`, `−5` (U+2212), `５` (U+FF15), `(5)`, `0x1F`, `1e3`, `-0` (0 for
  integer), `0` (0 for kibibytes). The test pattern `^(?<v>[^;]*);` delivers these captures.
- **N1** window check by subtraction — **accepted**: *Approach* step 2, test `SignatureDetectorMergesNearTheLatestTime`.
- **N2** merge key field by field — **accepted**: *Approach* step 4, test `SignatureDetectorComparesFieldsOneByOne`.
- **N3** the "work does not grow with open events" claim — **accepted, corrected**: the key is (signature, fields); the
  candidates with that key differ only by host and are scanned, at most 1,024 per matching signature (*Approach* step
  4, *Security considerations*, Time). Keying by host as well was rejected: an empty host must merge with any host, so
  host cannot be part of an exact key.
- **N4** an early-closing flood splits real copies — **accepted**: limitation in the area document.
- **N5** field texts are external text — **accepted**: the area document puts them under security area 12, and
  `.squad/project.md` area 12 names them; the built-in patterns use `[^ \n]` / `[^'\n]` / `[^)\n]` in their classes.
- **N6** `LineCount` overflow — **accepted**: `LineCount` is a `long`.
- **N7** public lists castable to mutable lists — **accepted**: `ReadOnlyCollection<T>` over private arrays, emitted
  collections not shared with the detector; tests `SignatureCatalogListsAreReadOnly`,
  `SignatureDetectorEmitsEventsItNoLongerChanges`.
- **N8** more refused constructs — **accepted**: AC6 adds lookbehind, a backreference to an existing group and a
  conditional, each with its fields declared.

## Out of scope / follow-ups

- Calling the detector over the store, storing events or incidents, and paging the record reader past 10,000 lines that
  share one capture time (a keyset on the record ID): #23.
- A command or view that shows events: #27, #28. Live detection on agent data, and agent `kernel_event` OOM kills
  alongside log-detected ones: #36, #53.
- The grouper's `oom_kill_allocating_task` end marker: #202 (opened).
- Follow-up issue for the orchestrator to open — title: `[Detection] Recognize disk-full and Too many connections lines
  of PHP, nginx, Apache and Plesk`; body: "The log signatures of #21 recognize a full disk only in MariaDB's own
  messages and `Too many connections` only in Postfix's and Dovecot's MySQL client errors. Web server and Plesk error
  logs carry client-supplied text (nginx `request:`, `host:`, `referrer:` and requested paths, Apache `referer:`, PHP
  output in FastCGI stderr over several lines), so a pattern that matches anywhere in such a message can be triggered by
  any HTTP client (record 0094). Add signatures, or extend `disk_full` and `mariadb_too_many_connections`, for PHP's
  `mysqli`/PDO 1040 errors, PHP's `errno=28` write errors and nginx's `(28: No space left on device)`, each with a
  `source` condition and a pattern anchored with `\A` at the server-written start of the message, after checking each
  format against the real logs; include negative tests with the trigger text in every client-supplied field and in a
  requested path."
