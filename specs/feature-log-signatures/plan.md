# Plan: Signature detection for known events

Source: Issue #21 | [spec.md](spec.md)
Status: Draft
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
- *Each detected event links to its source lines* — **feasible**: stored records have IDs (`StoredRecord.Id`) and the
  record reader returns log lines ordered by capture time, then ID (`IRecordReader`, `RecordQueries.Build`).
- Comment: *program `kernel` is no proof of origin; `_TRANSPORT` is this issue's decision* — **confirmed**: the journal
  reader keeps `SYSLOG_IDENTIFIER`, not `_TRANSPORT` (`JournalExportReader.cs` lines 35-42), and the grouper recognizes
  kernel lines by program alone (`KernelReportGrouper.cs` line 26). Decided in record 0094 (option 13): not kept.
- Comment: *Ubuntu's rsyslog writes kernel lines to `syslog` and `kern.log`, so a kill is stored twice* — **unverified**
  on the server (record 0086 states it from the packaged configuration); the plan merges such copies either way.
- Earlier records: 0086 says "signature detection of the OOM kill works on the stored records" — **kept** (option 2 of
  0094).

**Related defect found:** `KernelReportGrouper.cs` line 39 ends an OOM report only at `Out of memory: Killed process`,
`Memory cgroup out of memory: Killed process` or `Out of memory and no killable processes`; the kill line of
`vm.oom_kill_allocating_task` (`Out of memory (oom_kill_allocating_task): Killed process`) does not end it, so such a
report runs on to the 60-second or 2,000-line bound and its kill line can fall into the omitted middle. Not fixed here
(the grouper is the log import area); follow-up issue below. **Stale documentation found:** `docs/ARCHITECTURE.md`
(*Components*, the `internal/` bullet) lists "signatures" among the agent's Go packages; no such package exists. Fixed
in this change (*Documentation updates*).

## Acceptance criteria

Test names follow `{Class}{Scenario}{ExpectedResult}`. "One event" below means: `Complete` (after the `Add` calls) yields
exactly the events listed, in that order, and no other.

- [ ] **AC1 — built-in catalog.** `BuiltInSignatures.Catalog` loads the embedded `signatures.yaml` once and lists the
  signature IDs `oom_kill`, `mariadb_crash`, `mariadb_recovery_start`, `mariadb_recovery_end`,
  `mariadb_too_many_connections`, `service_start`, `service_stop`, `service_failed`, `disk_full`, `postfix_fatal`,
  `dovecot_fatal`, `boot` in this order; every pattern is compiled with exactly `Signature.PatternOptions`
  (`NonBacktracking | Multiline | CultureInvariant | ExplicitCapture`); no pattern contains the Unicode digit class
  (the two characters backslash and `d`).
  Tests: `BuiltInSignaturesCatalogListsTheSignaturesInOrder`, `BuiltInSignaturesPatternsRunWithoutBacktracking`,
  `BuiltInSignaturesPatternsUseAsciiDigitsOnly`.

- [ ] **AC2 — each signature has positive and negative cases** (issue AC 1). Each line below is one `log_line` record
  (`source`, `program`, `host`, `event`, `message` as given; empty when not given) fed alone to a detector on
  `BuiltInSignatures.Catalog`. Positive: exactly one event of that signature with exactly the fields given (a field not
  listed is absent from both `Texts` and `Numbers`). Negative: no event at all.

  | Signature | Positive (fields) | Negative |
  | --------- | ----------------- | -------- |
  | `oom_kill` | program `kernel`, a two-line message whose second line is the v5.15 kill line of `testdata/logs/kern.log-oom` with its `[123456.789105] ` prefix (`victim_pid` 4242, `victim_command` `mariadbd`, `anon_rss_bytes` 7,577,600,000, `oom_score_adj` 0); the same kill line without the prefix (journal form); `Memory cgroup out of memory: Killed process 42 (php-fpm) total-vm:8kB, anon-rss:7kB, file-rss:0kB, shmem-rss:0kB, UID:1 pgtables:1kB oom_score_adj:-1000` (42, `php-fpm`, 7,168, -1000); `Out of memory (oom_kill_allocating_task): Killed process 42 (a (b)) total-vm:8kB, anon-rss:7kB, file-rss:0kB, shmem-rss:0kB` (42, `a (b)`, 7,168, no `oom_score_adj`); a comm holding the delimiter: `Out of memory: Killed process 42 (x) total-vm:1kB) total-vm:8kB, anon-rss:7kB, file-rss:0kB, shmem-rss:0kB, UID:1 pgtables:1kB oom_score_adj:-17` (42, `x) total-vm:1kB`, 7,168, -17) | the kill line of the fixture with program `sshd`; program `kernel` with `oom_reaper: reaped process 4242 (mariadbd), now anon-rss:0kB, file-rss:0kB, shmem-rss:0kB`; `Out of memory: OOM victim 42 (x) is already exiting. Skip killing the task`; `Out of memory and no killable processes...`; the journal-form kill line with the pid written as the Arabic-Indic digits U+0664 U+0662 (written in C# as escapes; the test first asserts that the input holds a character above U+007F for which `char.IsDigit` is true, so a lost backslash fails the test instead of passing it) |
  | `mariadb_crash` | source `mariadb`, event `mariadb.abort`, a two-line message: `mysqld got signal 6 ;`, a line feed, `This could be because you hit a bug.` (`signal` 6); the same with source `journal`, program `mariadbd`, host `web-1` (`signal` 6) | message `mysqld got signal 6 ;` without an event; event `mariadb.start` with message `mysqld got signal 6 ;` |
  | `mariadb_recovery_start` | event `mariadb.recovery_start`, message `InnoDB: Starting crash recovery from checkpoint LSN=1` (no fields) | that message without an event; event `mariadb.recovery_end` |
  | `mariadb_recovery_end` | event `mariadb.recovery_end`, message `InnoDB: 10.6.12 started; log sequence number 1; transaction id 2` | that message without an event; event `mariadb.recovery_start` |
  | `mariadb_too_many_connections` | source `apache-error`: `PHP Warning:  mysqli_connect(): (HY000/1040): Too many connections in /var/www/vhosts/example.org/index.php on line 3`; program `postfix/proxymap`: `warning: connect to mysql server localhost: Too many connections`; program `dovecot`: `auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Too many connections - waiting for 1 seconds before retry` (no fields) | program `postfix/postscreen`: `NOQUEUE: reject: CONNECT from [192.0.2.9]:4242: 421 4.7.0 Error: too many connections from 192.0.2.9`; source `nginx-error`: `accept4() failed (24: Too many open files)` |
  | `service_start` | program `systemd`: `Started MariaDB 10.6.12 database server.` (`description` `MariaDB 10.6.12 database server`, no `unit`); `Started mariadb.service - MariaDB 10.11.6 database server.` (`unit` `mariadb.service`, `description` `MariaDB 10.11.6 database server`) | program `systemd`: `Starting MariaDB 10.6.12 database server...`; `Finished Daily apt upgrade and clean activities.`; program `logger`: `Started MariaDB 10.6.12 database server.` |
  | `service_stop` | program `systemd`: `Stopped MariaDB 10.6.12 database server.` (`description`); `Stopped mariadb.service - MariaDB 10.11.6 database server.` (`unit`, `description`) | program `systemd`: `Stopping MariaDB 10.6.12 database server...`; `Stopped (with error) MariaDB 10.6.12 database server.`; `mariadb.service: Deactivated successfully.` |
  | `service_failed` | program `systemd`: `mariadb.service: Failed with result 'oom-kill'.` (`unit` `mariadb.service`, `result` `oom-kill`); `plesk-php82-fpm.service: Failed with result 'exit-code'.` (`unit`, `result` `exit-code`) | program `systemd`: `session-3.scope: Failed with result 'oom-kill'.`; `mariadb.service: Main process exited, code=killed, status=9/KILL`; program `cron`: `mariadb.service: Failed with result 'oom-kill'.` |
  | `disk_full` | source `mariadb`: `Disk full (./ibtmp1); waiting for someone to free some space... (errno: 28 "No space left on device")`; source `mariadb`: `Disk is full writing './mysql-bin.000042' (Errcode: 28 "No space left on device"). Waiting for someone to free space...`; source `plesk-panel`: `file_put_contents(): Write of 4096 bytes failed with errno=28 No space left on device` (no fields) | `Disk quota exceeded`; `no space left on device` (lower case) |
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
  `BuiltInSignaturesDiskFullMatchesWriteErrors`, `BuiltInSignaturesDiskFullIgnoresOtherLines`,
  `BuiltInSignaturesPostfixFatalMatchesFatalAndPanic`, `BuiltInSignaturesPostfixFatalIgnoresOtherLines`,
  `BuiltInSignaturesDovecotFatalMatchesFatalAndPanic`, `BuiltInSignaturesDovecotFatalIgnoresOtherLines`,
  `BuiltInSignaturesBootReadsTheKernelVersion`, `BuiltInSignaturesBootIgnoresOtherLines`. Plus
  `BuiltInSignaturesLineMatchingTwoSignaturesYieldsBoth`: event `postfix.fatal`, message
  `fatal: open database /etc/postfix/x.db: No space left on device` yields `disk_full`, then `postfix_fatal` (catalog
  order).

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

- [ ] **AC4 — detector rules** (catalogs parsed from test text, independent of the built-in one). Test class
  `SignatureDetectorTests`:
  - The event lists the IDs of its lines in input order, `LineCount` their number, `SignatureId` the signature's ID and `CapturedAt`
    the first line's capture time: `SignatureDetectorEventListsTheRecordIdsOfItsLines`.
  - Merge: a line joins an open event of the same signature with equal fields whose first line is at most 60 s earlier
    (exactly 60 s: merged; 60 s plus one tick: two events): `SignatureDetectorMergesCopiesWithinTheWindow`,
    `SignatureDetectorSeparatesLinesAfterTheWindow`; different fields, different signatures or different non-empty hosts
    are never merged: `SignatureDetectorSeparatesDifferentFields`, `SignatureDetectorNeverMergesDifferentSignatures`,
    `SignatureDetectorSeparatesDifferentHosts`; an empty host merges with any host, and the event's host is the first
    non-empty host of its lines: `SignatureDetectorMergesAnEmptyHostWithAnyHost`; a field absent on one line and present
    on the other is a difference: `SignatureDetectorSeparatesAbsentFromPresentFields`.
  - Conditions: every given condition must hold (program, event, message), any listed program or event satisfies its
    condition, comparisons are ordinal and case-sensitive: `SignatureDetectorRequiresEveryCondition`,
    `SignatureDetectorMatchesAnyListedProgramOrEvent`. Records whose payload is not a `LogLine` are ignored, also for the
    order check: `SignatureDetectorIgnoresRecordsThatAreNoLogLines`.
  - Emission: `Add` adds to `closed` every event whose first line lies more than 60 s before the added line, oldest
    first; `Complete` adds the rest in opening order; events opened by one line are in catalog order:
    `SignatureDetectorEmitsEventsWhenTheirWindowCloses`, `SignatureDetectorOrdersEventsOfOneLineByCatalog`.
  - Errors with exact texts: a log line earlier than the previous log line →
    `InvalidOperationException` "signature detection: lines must be added in capture order"; `Add` or `Complete` after
    `Complete` → `InvalidOperationException` "signature detection: already complete"; a null argument →
    `ArgumentNullException`. Tests: `SignatureDetectorRefusesLinesOutOfCaptureOrder`,
    `SignatureDetectorRefusesUseAfterComplete`.
  - Fields: `integer` and `kibibytes` per the table in *Security considerations* (`SignatureDetectorConvertsIntegerFields`,
    `SignatureDetectorOmitsIntegerFieldsThatDoNotConvert`, `SignatureDetectorConvertsKibibytesToBytes`,
    `SignatureDetectorOmitsKibibytesThatOverflow`); a field whose group did not take part or matched the empty string is
    absent, and the event is still detected (`SignatureDetectorOmitsFieldsWithoutText`); a `text` field is cut to 1,024
    UTF-8 bytes at a character boundary (2,000 times `é` gives 512 characters; 1,023 ASCII characters plus `é` gives the
    1,023 characters): `SignatureDetectorCutsTextFieldsAt1024Bytes`. The non-ASCII digit cases carry the same
    "character above U+007F that `char.IsDigit` accepts" guard as AC2.

- [ ] **AC5 — a new signature without an engine change** (issue AC 3): `SignatureCatalog.Parse` on a text with the
  signature `segfault` (program `kernel`, message `^(?<command>[^ \[]+)\[(?<pid>[0-9]+)\]: segfault at `, fields
  `command: text`, `pid: integer`) and a detector on it turn the kernel line
  `nginx[1234]: segfault at 0 ip 00007f sp 00007f error 4 in libc.so.6` into one `segfault` event with `command` `nginx`
  and `pid` 1234. Test: `SignatureCatalogParseAcceptsASignatureTheEngineHasNeverSeen`.

- [ ] **AC6 — catalog validation.** `SignatureCatalog.Parse("test.yaml", text)` reads every key
  (`SignatureCatalogParseReadsEveryKey`: ID, description, programs, events, pattern options, fields with their types in
  document order) and refuses each rule break with `SignatureException` and exactly the text below (one break per test
  document; `N` is the line given). Test: `SignatureCatalogParseRefusesInvalidCatalogs` (one `DataRow` per row), plus
  `SignatureCatalogParseKeepsTheYamlErrorAsInner` (the not-valid-YAML case has a `ConfigException` as inner exception).

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
  | `event` not a sequence of 1 to 16 scalars matching the wire format's name pattern | `signatures: test.yaml:N: event must be a list of 1 to 16 event names` |
  | `message` not a scalar of 1 to 4,096 characters | `signatures: test.yaml:N: message must be a pattern of 1 to 4096 characters` |
  | `message` that the regular expression parser refuses, or a construct the non-backtracking engine does not support (lookahead `(?=`, atomic group `(?>`) | `signatures: test.yaml:N: message is not a valid pattern` |
  | neither `event` nor `message` (N: line of the signature) | `signatures: test.yaml:N: a signature needs an event or a message` |
  | `fields` without `message` | `signatures: test.yaml:N: fields need a message` |
  | `fields` not a mapping of 1 to 8 entries | `signatures: test.yaml:N: fields must map 1 to 8 names to types` |
  | a field name not matching `^[a-z][a-z0-9_]{0,63}$` (N: line of that field) | `signatures: test.yaml:N: field name must match ^[a-z][a-z0-9_]{0,63}$` |
  | a field type other than `text`, `integer`, `kibibytes` (N: line of that field) | `signatures: test.yaml:N: field type must be text, integer or kibibytes` |
  | a field that is no named group of `message` (N: line of that field) | `signatures: test.yaml:N: field is not a named group of message` |
  | a named group of `message` that is no field (N: line of the key `message`) | `signatures: test.yaml:N: a named group of message is not a field` |

  Unless stated otherwise, N is the line of the key whose value breaks the rule. The key names `signatures`, `id`,
  `description`, `program`, `event`, `message`, `fields` are the whole schema; no message contains document text.

- [ ] **AC7 — bounded on hostile input** (spec AC-S5). Tests: `SignatureDetectorClosesTheOldestEventBeyondTheOpenLimit`
  (1,025 lines with distinct fields within one window: adding the 1,025th puts the first event into `closed`);
  `SignatureDetectorCountsLinesBeyondTheIdLimit` (300 copies: `RecordIds` holds the first 256 IDs, `LineCount` 300);
  `SignatureDetectorRetainsBoundedMemoryUnderAFlood` (heap-bound: 20,000 lines with distinct 1,000-character text fields
  within one window, added through a collection that counts and discards closed events; the memory retained by the
  detector after the last `Add` stays below 8 MiB, measured with the `RetainedBytes` helper form of `.squad/stack.md`,
  *Writing tests*); and AC1's `BuiltInSignaturesPatternsRunWithoutBacktracking`. No wall-clock bound is tested.

## Approach

Everything new lives in `src/Vandox.Core/Signatures` (namespace `Vandox.Core.Signatures`), next to the parsers whose
records it reads; Vandox.Core already has YamlDotNet and the strict YAML tree reader. No new project, package, storage
step or wire change.

**Catalog (`signatures.yaml`, embedded resource `Vandox.Core.Signatures.signatures.yaml`).** The Dev writes it exactly
as below (the comments are part of the file). Loaded once by `BuiltInSignatures.Catalog` through `SignatureCatalog.Parse`.

```yaml
# Signatures of known events: decision record 0094, rules in docs/areas/detection-and-reports.md (Log signatures).
# A signature matches one log line: every given condition must hold (program, event, message).
# Patterns run with RegexOptions NonBacktracking, Multiline, CultureInvariant and ExplicitCapture; write [0-9], never
# the Unicode digit class. Every named group is a field, and every field is a named group.
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
    description: MariaDB refused a client with Too many connections (error 1040).
    message: 'Too many connections'
  - id: service_start
    description: systemd started a unit (on systemd 249 named by its description).
    program: [systemd]
    message: '^Started (?:(?<unit>[^ ]+\.service) - )?(?<description>.+)\.$'
    fields:
      unit: text
      description: text
  - id: service_stop
    description: systemd stopped a unit (on systemd 249 named by its description).
    program: [systemd]
    message: '^Stopped (?:(?<unit>[^ ]+\.service) - )?(?<description>[^(].*)\.$'
    fields:
      unit: text
      description: text
  - id: service_failed
    description: A systemd service failed.
    program: [systemd]
    message: '^(?<unit>[^ ]+\.service): Failed with result ''(?<result>[^'']+)''\.$'
    fields:
      unit: text
      result: text
  - id: disk_full
    description: A program could not write because a file system was full.
    message: 'No space left on device|Disk full \(|Disk is full writing'
  - id: postfix_fatal
    description: Postfix logged a fatal error or a panic.
    event: [postfix.fatal, postfix.panic]
  - id: dovecot_fatal
    description: Dovecot logged a fatal error or a panic.
    event: [dovecot.fatal, dovecot.panic]
  - id: boot
    description: The kernel started (the first kernel line of a boot).
    program: [kernel]
    message: '^(?:\[ *[0-9]+\.[0-9]+\] )?Linux version (?<kernel_version>[^ ]+) '
    fields:
      kernel_version: text
```

The patterns were exercised once in a scratch program on .NET 10 (`NonBacktracking` with the options above, against the
default engine): the OOM pattern gives the same captures on both engines for the fixture's kill line, a comm holding
`) total-vm:1kB`, a comm holding ` oom_score_adj:`, a kill line without `oom_score_adj` (group absent) and a second line
that ends in `oom_score_adj:5` (not read: `.` stops at the line feed); the stop pattern refuses
`Stopped (with error) ...`; a lookahead and an atomic group throw `NotSupportedException`, a backreference to a missing
group `RegexParseException`; the default `\d` class matches U+0663. The Tester's tests are the verification.

**Loading (`SignatureCatalog.Parse`).** Reads the text with `YamlTreeReader.Read` (a `ConfigException` becomes the
"not valid YAML" error with it as inner exception), walks the tree and applies the AC6 rules. A pattern is compiled with
`Signature.PatternOptions`; `ArgumentException` (including `RegexParseException`) and `NotSupportedException` from the
constructor become "message is not a valid pattern". Group names are `Regex.GetGroupNames()` without `0`. Field order
is document order.

**Matching (`SignatureDetector`).** For each `Add`: a null argument throws; after `Complete` any record throws; a record
whose `Data` is not a `LogLine` is ignored. For a log line:
1. If its `CapturedAt` is before the previous log line's, throw.
2. Close (add to `closed`) every open event whose first line's `CapturedAt` plus 60 s is before this line's, oldest
   first. With one window for every signature, these are always a prefix of the open events in opening order.
3. For each signature in catalog order: the program condition holds when `Programs` is empty or contains
   `LogLine.Program` (ordinal); the event condition likewise with `LogLine.Event`; the message condition when there is
   no pattern or `Pattern.Match(LogLine.Message)` succeeds (the first match only). When all hold, convert the fields
   (rules in *Security considerations*), then look up an open event with the same signature and equal fields (the same
   field names present, texts equal ordinally, numbers equal) whose host equals the line's host or where either host is
   empty; take the oldest such event. If found, append the record ID (while fewer than 256 are kept), add 1 to the line
   count and, when the event's host is empty, take the line's host. Otherwise open a new event; when 1,024 events are
   open, first close the oldest.
4. The lookup is keyed by signature and fields (not a scan of all open events), so the work per line does not grow with
   the number of open events.

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

Further internal helper types (for example an open-event type, or a reader class that keeps `SignatureCatalog.Parse`
below the cognitive complexity limit of 15) are the Dev's choice, one type per file in `Signatures/`, named in the Dev's
report.

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

    public IReadOnlyList<Signature> Signatures { get; }

    /// <exception cref="SignatureException">The text breaks a catalog rule</exception>
    public static SignatureCatalog Parse(string name, string text);
}

public sealed class Signature
{
    internal const RegexOptions PatternOptions = RegexOptions.NonBacktracking | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    internal Signature(string id, string description, IReadOnlyList<string> programs, IReadOnlyList<string> events, Regex? pattern, IReadOnlyList<SignatureField> fields);

    public string Id { get; }
    public string Description { get; }
    public IReadOnlyList<string> Programs { get; }
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
    public IReadOnlyList<long> RecordIds { get; set; } = [];
    public int LineCount { get; set; }
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

Every member gets XML documentation and `#region` blocks as the analyzers require; the comments above are guidance,
not the final documentation. No existing file is rewritten except `Vandox.Core.csproj` (one `ItemGroup` added; read
and found final otherwise).

## Test files

- `tests/Vandox.Core.Tests/SignatureCatalogTests.cs` — AC5, AC6.
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
  conditions and fields (the table of AC2's positives as prose or a table: ID, what it recognizes, fields with units —
  `anon_rss_bytes` in bytes, converted from the kernel's kB); the matching rules (conditions, first match per signature,
  a line may yield several events in catalog order); the field rules (types, digit and range rules, 1,024-byte cut,
  absent fields); the merge rule (same signature, equal fields, compatible host with the empty host matching any, within
  60 s of the first line, the oldest candidate; the first non-empty host); emission and order (lines in capture order,
  events emitted when their 60 s pass or at completion, in opening order); the bounds (1,024 open events, 256 IDs per
  event with the full count); that nothing is stored and no record is changed, and that no command or view shows events
  yet; the limitations (an event is a classification, not proof of origin — any local process can log as `kernel`,
  `systemd`, `mariadbd`, `postfix/*` or `dovecot`, and `_TRANSPORT` is not kept; on systemd 249 start and stop name the
  description, so scopes and timers match too; real repeats with equal fields within 60 s are one event; the kill line of
  `oom_kill_allocating_task` is recognized, but its report is not ended by the grouper; `Too many connections` is read
  case-sensitively anywhere in a message). *Related decisions*: add 0094. *Not here*: incidents (#23). Add an
  *Implementation* line pointing at `Vandox.Core.Signatures`.

## Documentation updates

| File | Change | Owner |
| ---- | ------ | ----- |
| `docs/areas/detection-and-reports.md` | as in *Areas* | Dev |
| `docs/ARCHITECTURE.md` | intro paragraph: the log signature catalog and its detector exist (not called by the service yet); *Components*, the `Vandox.Core` list: "the log signatures, an embedded catalog, and the detector that matches them on stored log lines"; the `internal/` bullet: remove the stale "signatures,"; *Data flow* records list: add 0094 | Dev |
| `README.md` | *Layout*: the `src/Vandox.Core/` line adds "log signatures" | Dev |
| `.squad/stack.md` | *Layout*: `src/Vandox.Core` list adds "log signatures" | Dev |
| `.squad/project.md` | *Security areas* 10: add the signature detection (`SignatureCatalog`, `SignatureDetector`, `BuiltInSignatures`: embedded catalog, patterns on the non-backtracking engine in linear time with unsupported constructs refused at load, integer fields of ASCII digits range-checked before use, text fields cut to 1,024 bytes, at most 1,024 open events and 256 IDs per event; an event is a classification, not proof of origin; record 0094); *Test doubles*, row "log parsers (C#)": add `SignatureSamples` if the Tester creates it | Dev |
| `docs/decisions/0094-...md` and the index row | written (Proposed); set to Accepted at approval | Lead |

## Architecture check

- Deterministic detection (0008): signatures are fixed data and the detector is deterministic for the same input order;
  no AI. Kept.
- Records are never updated (storage *Writing*, security area 1): the detector only reads records it is given and writes
  nothing. Kept.
- Backfilled or imported data never alerts (0022): no alert exists; detected events are analysis only. Kept.
- No data gaps unless recorded, a hanging collector never blocks the agent: not touched (backend only, no agent code).
- `docs/ARCHITECTURE.md` changes only as listed (a new part of `Vandox.Core`, a stale word removed); no guarantee or flow
  changes, so no Product Manager decision is needed.

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
  refused when the catalog loads, so no pattern can fall back to backtracking; no match timeout is needed. Per line, only
  the signatures whose program and event conditions hold run a pattern, each at most once.
- Memory: at most 1,024 open events, each with at most 256 IDs and at most 8 fields of at most 1,024 bytes; closed events
  go to the caller's collection. AC7 pins the open-event and ID caps and a heap bound.
- Number guard (a validation on captured text before conversion). The forms a capture can hold are whatever the pattern
  admits, so the guard is in the converter, not in the patterns:

  | Captured text | `integer` | `kibibytes` |
  | ------------- | --------- | ----------- |
  | 1 to 19 ASCII digits `0`-`9` (leading zeros allowed) | the value, when it is at most 2^63 - 1 | value times 1,024, when it is at most 2^63 - 1 |
  | `-` followed by 1 to 19 ASCII digits | the negative value, when it is at least -2^63 | absent |
  | more than 19 digits, or a value out of range | absent | absent |
  | `+` sign, white space, thousands separators, decimal point, exponent | absent | absent |
  | any non-ASCII digit (Unicode `Nd`, e.g. U+0660 to U+0669) | absent | absent |
  | empty, or the group did not take part | absent | absent |

  An absent field never suppresses the event. Cases for the tests: `42`, `0042`, `-17`, `9223372036854775807`,
  `-9223372036854775808` (integer, present); `9223372036854775808`, `+5`, ` 5`, `1,000`, `4.2`, the Arabic-Indic digits
  (absent); `7400000` gives 7,577,600,000, `9007199254740991` gives 9,223,372,036,854,774,784,
  `9007199254740992` and `-1` are absent (kibibytes).
- Forged lines: an event is a classification of text (record 0094, option 13; 0086 option 19; 0088; 0091) and says so in
  the area document; events list their record IDs so a consumer can weigh them.
- The catalog is trusted data compiled into the binary; `SignatureCatalog.Parse` is nevertheless strict (AC6), and the
  YAML tree reader bounds nesting and never expands aliases.

## Decision records

- `docs/decisions/0094-log-signatures-embedded-yaml-linear-patterns-on-stored-lines.md` (Proposed) — where detection runs,
  the pattern language, how copies are counted, `_TRANSPORT` not kept.

## Out of scope / follow-ups

- Calling the detector over the store, storing events or incidents, and paging the record reader past 10,000 lines that
  share one capture time (a keyset on the record ID): #23.
- A command or view that shows events: #27, #28. Live detection on agent data, and agent `kernel_event` OOM kills
  alongside log-detected ones: #36, #53.
- Follow-up issue for the orchestrator to open — title: `[Logs] The kernel report grouper does not end an OOM report at
  the oom_kill_allocating_task kill line`; body: "`KernelReportGrouper` ends an OOM report only at `Out of memory: Killed
  process`, `Memory cgroup out of memory: Killed process` or `Out of memory and no killable processes`
  (`src/Vandox.Core/LogParsing/KernelReportGrouper.cs`, `_oomEnds`). With `vm.oom_kill_allocating_task` set, the kernel
  prints `Out of memory (oom_kill_allocating_task): Killed process ...` (`mm/oom_kill.c`, v5.15 line 1099), so such a
  report is not ended at its kill line and runs on to the 60-second or 2,000-line bound; past 16 KiB the kill line can end
  up in the omitted middle and the `oom_kill` signature (#21) then misses the kill. Add the prefix to the end markers,
  with a test in `KernelReportGrouperTests` and the sentence in `docs/areas/log-import.md` (*Kernel reports*). Found
  while planning #21; the sysctl is off by default."
