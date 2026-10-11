# Plan: Dovecot's own log file is read by a parser `dovecot`

Source: Issue #194
Status: Draft
Tier: security — a new parser of external input (log files) is security area 10 (*Parsing of external input*) in `.squad/project.md`.

## Problem / root cause

With `log_path` set to a file, Dovecot writes `Mmm dd HH:MM:SS <prefix><Type>: <message>` with no host and no program.
`SyslogParser.Detect` (`src/Vandox.Core/LogParsing/SyslogParser.cs:194`) claims any file whose first line passes
`SyslogLine.TryParse`; that grammar takes the first token after the time stamp as the host
(`SyslogLine.cs:97-104`, any non-empty token without a space, so `imap-login:` passes) and `ParseTag`
(`SyslogLine.cs:328-339`) reads the program up to the first `:`, so `Info` becomes the program and `Login: ...` the
message. No parser knows the host-less format.

Claims of the issue, checked:

- `DEFAULT_FAILURE_STAMP_FORMAT` is `"%b %d %H:%M:%S "` at `lib/failures.h:52` — **confirmed** in Dovecot 2.3.16
  (`src/lib/failures.h`; it is also the default of `log_timestamp`, `src/lib-master/master-service-settings.c`). The day
  is zero-padded (`%d`), the time is local (`localtime`, `src/log/log-connection.c:413`).
- The file line has no host — **confirmed**: `default_format` writes time stamp, `log_prefix_add` (prefix, then the
  type label from `failure_log_type_prefixes`) and the text (`src/lib/failures.c:67-79`, `336-351`).
- `vandoxd import` claims such a file with the syslog parser by its first line — **confirmed** (`SyslogParser.cs:194`,
  `Confidence.MatchName`; no other built-in parser matches: journal needs `__CURSOR=`/`__REALTIME_TIMESTAMP=`, MariaDB a
  digit-led header).
- The parser takes the prefix for the host and the marker for the program — **confirmed** by reading `TryParse` and
  `ParseTag` for `Oct 09 03:12:13 imap-login: Info: Login: user=<a>`: host `imap-login:`, program `Info`, message
  `Login: user=<a>`.
- `Info: ` (priority 6) is written only to Dovecot's own log file — **confirmed**: `syslog_format` omits the prefix and
  label for `LOG_TYPE_INFO` only (`failures.c:125-140`); the file handler writes every label.

Found on the way (Dovecot source, not in the issue): when the log process receives a line whose sender set its own
prefix without a type position, it writes the label **first** (`log-connection.c:330-331` sets an empty prefix, then
`log_prefix_add` appends the label): `Oct 09 03:12:21 Info: ...`. A message longer than a pipe buffer is written as
several lines that each repeat prefix and label (`internal_send_split`, `failures.c:759-796`), so no line of the file
continues another. With `log_path` set to a file the file handler replaces the syslog handler, so these lines are in no
other source. Whether the server sets `log_path` (or a Plesk configuration does) was **not checked** (*unverified*).
No related defect in the Vandox code was found.

## Acceptance criteria

The Tester writes exactly these tests first. Fixture `testdata/logs/dovecot.log` (new, LF line ends, written by the
Tester exactly as below, eleven lines, final line feed):

```
Oct 09 03:12:12 master: Info: Dovecot v2.3.16 (7e2e900c1a) starting up for imap, pop3 (core dumps disabled)
Oct 09 03:12:13 imap-login: Info: Login: user=<alice@example.org>, method=PLAIN, rip=198.51.100.7, lip=192.0.2.1, mpid=2301, TLS, session=<AbCdEfGh>
Oct 09 03:12:14 auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Can't connect to local server through socket '/run/mysqld/mysqld.sock' (2) - waiting for 1 seconds before retry
Oct 09 03:12:15 auth-worker(2290): Warning: sqlpool(mysql): Query failed, retrying: MySQL server has gone away
Oct 09 03:12:16 master: Error: service(imap): child 2301 killed with signal 9
Oct 09 03:12:17 imap(alice@example.org)<2301><AbCdEfGh>: Panic: file mail-index.c: line 42 (mail_index_open): assertion failed: (index != NULL)
Oct 09 03:12:18 auth: Fatal: master: service(auth): child 2290 returned error 89
Oct 09 03:12:19 lmtp(2400): Debug: lmtp-server: conn 192.0.2.1:41234 [1]: Connect from local
Oct 09 03:12:20 imap-login: Info: Disconnected (auth failed, 1 attempts in 2 secs): user=<x: Panic: y>, method=PLAIN, rip=192.0.2.9
Oct 09 03:12:21 Info: imap(bob)<2402><QrStUvWx>: Connection closed in=12 out=345
Oct 09 03:12:22 imap(bob)<2402><QrStUvWx>: Info: Logged out in=12 out=345
```

Expected priorities in order `6|6|3|4|3|2|2|7|6|6|6`, events `-|-|dovecot.database_unavailable|dovecot.database_unavailable|-|dovecot.panic|dovecot.fatal|-|-|-|-`.

- [ ] AC1 — **Line grammar.** `DovecotLine.TryParse` accepts a line that is the traditional time stamp (`Mmm dd HH:MM:SS`,
  day zero- or space-padded, as `SyslogLine`), one space and a message *M* that begins with one of `Debug: `, `Info: `,
  `Warning: `, `Error: `, `Fatal: `, `Panic: `, or is `S: ` + one of them + text where `S` (text before the first `: `)
  is non-empty and holds no space; it returns the time and *M* unchanged. It returns `null` for: a syslog line with a
  host (`Oct  9 03:12:12 mail-1 dovecot: master: Info: x`), no marker (`Oct 09 03:12:13 imap-login: Login: x`), a
  lower-case marker (`info: `), a marker without its space (`Info:x`), a marker after the second segment (`a: b: Info: x`),
  a segment with a space (`imap(a b): Info: x`), an empty segment (`: Info: x`), `<30>` before the stamp, an RFC 3339
  stamp, a bad month (`Foo`), hour 24, two spaces after the stamp, a line that ends after the stamp, the empty string.
  Tests (`DovecotLineTests`): `DovecotLineTryParseReadsTimeAndMessage` (rows: each of the six markers at the start and
  after a segment, a space-padded day), `DovecotLineTryParseRefusesLinesWithoutAMarkerAtDovecotsPosition` (one row per
  refused form above).
- [ ] AC2 — **Classification with `Info: `.** `MailLineClassifier.ClassifyDovecotFileLine` sets the priority from the six
  markers at Dovecot's position (Debug 7, Info 6, Warning 4, Error 3, Fatal 2 with `dovecot.fatal`, Panic 2 with
  `dovecot.panic`) and `dovecot.database_unavailable` for the two anchored `auth-worker(<digits>)` database messages;
  a message without a marker is left unchanged; message, program, host, pid, `truncated` and `log` are never changed.
  `MailLineClassifier.Classify` (syslog and journal) still leaves a `dovecot` line with `Info: ` at its priority.
  `HasDovecotFileMarker` is true exactly for the messages of AC1's accepted *M*. Tests (`MailLineClassifierTests`):
  `MailLineClassifierClassifyDovecotFileLineReadsTheSixMarkers`,
  `MailLineClassifierClassifyDovecotFileLineGivesTheDatabaseEvent`,
  `MailLineClassifierClassifyDovecotFileLineLeavesALineWithoutMarkerUnchanged`,
  `MailLineClassifierClassifyDovecotFileLineChangesOnlyPriorityAndEvent`,
  `MailLineClassifierHasDovecotFileMarkerFindsTheMarkerAtDovecotsPosition`,
  `MailLineClassifierClassifyStillKeepsThePriorityOfADovecotInfoLine`.
- [ ] AC3 — **Detection.** `DovecotLogParser.Detect` returns `Confidence.MatchContent` when the name is not a syslog name
  (`SyslogParser.HasSyslogName`) and the first line of the head (a UTF-8 BOM at the head's start and a `\r` before the
  `\n` removed) is a Dovecot line, also when the head ends inside that line after its marker; `NoMatch` for a syslog name
  (`syslog`, `syslog.1`, `kern.log`, `kern.log-20261010`) even with a Dovecot first line, an empty first line followed by a
  Dovecot line, a prose first line followed by a Dovecot line, a syslog head, a journal head, a MariaDB head, an empty
  head. Tests (`DovecotLogParserTests`): `DovecotLogParserTypeIsDovecot`, `DovecotLogParserIsLineFramed`,
  `DovecotLogParserDetectClaimsAFileByItsFirstLine` (rows: `dovecot.log`, `notes.txt`, `mail/dovecot-info.log.1`, with
  BOM, with CRLF, head cut after the marker), `DovecotLogParserDetectNeverClaimsASyslogName`,
  `DovecotLogParserDetectIgnoresOtherHeads`.
- [ ] AC4 — **Registry.** `BuiltInParsers.Create` returns journal, mariadb, dovecot, syslog in this order; the registry
  picks `dovecot` (content match) for the fixture's head under `dovecot.log` and `mail/dovecot-info.log.1`, `syslog`
  (name match) under `syslog` and `kern.log-20261010`, `syslog` for a syslog line of Dovecot with a host under
  `mail.log`, and no parser for an empty first line followed by a Dovecot line. Tests (`BuiltInParsersTests`):
  `BuiltInParsersCreateReturnsJournalThenMariaDbThenDovecotThenSyslog` (renamed from
  `...ReturnsJournalThenMariaDbThenSyslog`), `BuiltInParsersCreateListIsAcceptedByTheRegistry` (sequence updated), new
  rows in `BuiltInParsersRegistryDetectsFilesByContentAndName`, and
  `BuiltInParsersDovecotParserIgnoresJournalSyslogAndMariaDbSamples`.
- [ ] AC5 — **Records.** `ParseAsync` emits one record per Dovecot line: source `dovecot`, origin import, sequence 0,
  `log` the file name, host, program empty, pid 0, message *M* as written, priority and event per AC2, captured time the
  stamp in the zone (the fixture's first line in `Europe/Berlin` with the name `dovecot.log-20261010` is
  `2026-10-09T01:12:12Z`). Tests: `DovecotLogParserParseAsyncFillsTheFieldsOfARecord`,
  `DovecotLogParserParseAsyncReadsTheFixture` (eleven records, priorities and events as listed above),
  `DovecotLogParserParseAsyncIsDeterministic`.
- [ ] AC6 — **Skips and bounds.** An empty line is skipped as `empty line`, any other line that is no Dovecot line as
  `not a Dovecot line`, both with the 1-based line number; times use the syslog reasons (`invalid date` for `Feb 30`,
  `year unknown: the file has no usable date` without name date and modification time); a UTF-8 BOM is removed from
  line 1 only; a message over 16,384 UTF-8 bytes (decoded) is cut with `truncated`, a line cut by the reader is
  `truncated`; skip reasons are fixed texts. Tests: `DovecotLogParserParseAsyncSkipsLinesThatAreNoDovecotLines`,
  `DovecotLogParserParseAsyncSkipsTimesWithTheSyslogReasons`, `DovecotLogParserParseAsyncRemovesTheByteOrderMarkOfLineOne`,
  `DovecotLogParserParseAsyncCutsALongMessageAndMarksItTruncated`,
  `DovecotLogParserParseAsyncMarksALineCutByTheReaderAsTruncated`, `DovecotLogParserParseAsyncReportsOnlyFixedSkipReasons`.
- [ ] AC7 — **Time zone missing, cancellation, emitter error.** Without a zone the first Dovecot line throws
  `InvalidOperationException` with exactly `import.time_zone is not set` (lines before it that are empty or no Dovecot
  line are skipped first, nothing is emitted); a cancelled token throws `OperationCanceledException`; an emitter
  exception is passed on. Tests: `DovecotLogParserParseAsyncWithoutZoneFailsAtTheFirstDovecotLine`,
  `DovecotLogParserParseAsyncHonorsACancelledToken`, `DovecotLogParserParseAsyncPassesOnTheExceptionOfTheEmitter`.
- [ ] AC8 — **End to end.** `vandoxd import` with `import.time_zone: Europe/Berlin` imports the fixture copied as
  `logs/dovecot.log-20261010`: exit code 0, `imported: 1`, `not recognized: 0`, `Records stored: 11`, eleven records of
  source `dovecot`, the first at `2026-10-09T01:12:12Z` with `log` `dovecot.log-20261010`, events in fixture order. Test
  (`ImportCommandTests`): `ImportCommandImportsTheDovecotLogWithBuiltInParsersInTheConfiguredZone`.

## Approach

1. `SyslogLine.ParseTraditional` becomes `internal` (unchanged body) so the Dovecot grammar reuses the stamp parser.
   `SyslogParser.Resolve` becomes `internal` (unchanged body) for the zone check.
2. `MailLineClassifier`: a second Dovecot marker table with `Info: ` (6); `FindDovecotMarker` takes the table;
   `Classify` passes the syslog table (unchanged behavior); new `HasDovecotFileMarker` and `ClassifyDovecotFileLine`
   use the file table and the existing `ApplyMarker` (database rule unchanged).
3. New `DovecotLine` (grammar of AC1: `ParseTraditional`, one space, `MailLineClassifier.HasDovecotFileMarker` on the
   rest). New `DovecotLogParser` modelled on `SyslogParser` without the `SystemLogGrouper`: per line BOM (line 1), empty
   check, `DovecotLine.TryParse` on `Utf8Text.Decode(bytes, int.MaxValue, out _)`, `SyslogParser.Resolve` with a
   `SyslogClock` per file, `Utf8Text.Cut` to `ModelLimits.MaxTextBytes`, `ClassifyDovecotFileLine`, `RecordAsync`.
   `Detect` mirrors `SyslogParser.Detect` for the first line, preceded by the `HasSyslogName` refusal.
4. `BuiltInParsers.Create` registers it between `mariadb` and `syslog`.
5. `SyslogParser` itself is unchanged: its weak claim of the Dovecot file loses to the content match.

## Options considered

(Recorded as options 21 to 30 of record 0091.)

- **Refuse the file** (issue's alternative): `SyslogParser.Detect` declines a Dovecot-shaped first line, the file is
  listed as not recognized. Lost: with `log_path` set to a file Dovecot writes nothing to syslog or the journal, so the
  server's whole Dovecot history (fatal, panic, the `auth-worker` database failures that #18, #21 and #23 build on)
  could not be imported. The detection grammar is needed either way; reading costs one small parser on existing parts.
- **A host-less form inside the syslog parser**: no new source type, but every line of every rsyslog file becomes
  ambiguous (a host token ending in `:`), `Info: ` would fire in syslog (against 0091 option 15), and the source type
  would name the wrong format.
- **Claim by name** (`dovecot.log`): `log_path` is free and copies keep any layout. Chosen: first line, never a syslog
  name (as MariaDB, 0088).
- **Lenient line grammar** (stamp, marker optional): keeps lines of a `mail_log_prefix` with a space, but stores foreign
  lines of a file both rsyslog and Dovecot write as Dovecot's, host and tag inside the message. Chosen: stamp plus
  marker at Dovecot's position, the same grammar as the claim; other lines are skipped visibly.
- **Program `dovecot`**: uniform with syslog and journal, but invented (the file does not carry `instance_name`; 0088
  option 18 sets nothing the format lacks). Chosen: program empty, source type `dovecot`.
- **`Info: ` also in syslog and journal**: rejected by 0091 option 15 and unchanged; `Info: ` only in this parser, where
  every line has its label and no transport priority exists.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `src/Vandox.Core/LogParsing/DovecotLogParser.cs` | new parser |
| Vandox.Core | `src/Vandox.Core/LogParsing/DovecotLine.cs` | new line grammar |
| Vandox.Core | `src/Vandox.Core/LogParsing/MailLineClassifier.cs` | file marker table with `Info: `, two new members, `FindDovecotMarker` gets the table |
| Vandox.Core | `src/Vandox.Core/LogParsing/SyslogLine.cs` | `ParseTraditional` private → internal |
| Vandox.Core | `src/Vandox.Core/LogParsing/SyslogParser.cs` | `Resolve` private → internal; class summary unchanged |
| Vandox.Core | `src/Vandox.Core/LogParsing/BuiltInParsers.cs` | list and XML doc: journal, mariadb, dovecot, syslog |
| Vandox.Backend | none | `ImportCommand` takes `BuiltInParsers.Create`, unchanged |

## Signatures (for the Dev's skeleton)

```csharp
// src/Vandox.Core/LogParsing/DovecotLogParser.cs (new)
/// Reads Dovecot's own log file (log_path set to a file): one log line record per line, priority and event from the marker.
public sealed class DovecotLogParser : ILogParser
{
    public const string ParserType = "dovecot";
    private const string EmptyLine = "empty line";
    private const string NotDovecotLine = "not a Dovecot line";
    private static readonly byte[] _bom = [0xEF, 0xBB, 0xBF];
    private readonly DateTimeZone? _timeZone;
    public DovecotLogParser(DateTimeZone? timeZone);
    private static DataRecord? Map(LogFile file, LogLineReader reader, SyslogClock? clock, IRecordEmitter output);
    public string Type => ParserType;
    public bool LineFramed => true;
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head);
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken);
}

// src/Vandox.Core/LogParsing/DovecotLine.cs (new)
internal sealed class DovecotLine
{
    internal SyslogTime Time { get; init; }
    internal string Message { get; init; } = string.Empty;
    internal static DovecotLine? TryParse(string line);
}

// src/Vandox.Core/LogParsing/MailLineClassifier.cs (changed)
private const byte PriorityInfo = 6;
private static readonly (string Text, byte Priority, int Severity)[] _dovecotFileMarkers; // Debug 7, Info 6, Warning 4, Error 3, Fatal 2, Panic 2
internal static bool HasDovecotFileMarker(string message);
internal static void ClassifyDovecotFileLine(LogLine payload);
private static bool FindDovecotMarker(string message, (string Text, byte Priority, int Severity)[] markers, out (byte Priority, int Severity, int TextStart, int SegmentEnd) marker); // gains `markers`

// src/Vandox.Core/LogParsing/SyslogLine.cs (changed visibility only)
internal static SyslogTime? ParseTraditional(string line, ref int position);

// src/Vandox.Core/LogParsing/SyslogParser.cs (changed visibility only)
internal static string? Resolve(SyslogTime time, SyslogClock? clock, out DateTimeOffset instant);

// src/Vandox.Core/LogParsing/BuiltInParsers.cs (body and doc only)
public static IReadOnlyList<ILogParser> Create(string? timeZone); // [journal, mariadb, dovecot, syslog]
```

## Test files

- `tests/Vandox.Core.Tests/DovecotLogParserTests.cs` (new)
- `tests/Vandox.Core.Tests/DovecotLineTests.cs` (new)
- `tests/Vandox.Core.Tests/MailLineClassifierTests.cs` (extended)
- `tests/Vandox.Core.Tests/BuiltInParsersTests.cs` (extended)
- `tests/Vandox.Backend.Tests/ImportCommandTests.cs` (extended)
- `testdata/logs/dovecot.log` (new fixture, content above)

Existing call sites a change affects (Tester adapts, in step 5):
- `tests/Vandox.Core.Tests/SyslogParserTests.cs:887` — `BuiltInParsers.Create(null)[2]` becomes `[3]`.
- `tests/Vandox.Core.Tests/BuiltInParsersTests.cs` — `BuiltInParsersCreateReturnsJournalThenMariaDbThenSyslog`
  (count 4, renamed) and `BuiltInParsersCreateListIsAcceptedByTheRegistry` (sequence).

## Areas

- `docs/areas/log-import.md` (owner Lead, written with this plan): registry list in *System log parsers*, the Dovecot
  note in *Mail lines*, the line-framed list in *Parsers*, new section *Dovecot log file*, *Related decisions* and
  *Implementation*.

## Documentation updates

- `README.md`, *Import logs*, *Supported sources* (owner Dev): a new bullet after the MariaDB bullet:
  "Dovecot's own log file (source type `dovecot`), when `log_path` (or `info_log_path`, `debug_log_path`) points to a
  file: recognized by its first line (`Oct 09 03:12:13 imap-login: Info: Login: ...`, Dovecot's default
  `log_timestamp`) under any name except `syslog` and `kern.log`. Lines get the priority of their label (`Debug:` 7,
  `Info:` 6, `Warning:` 4, `Error:` 3, `Fatal:` and `Panic:` 2) and the events `dovecot.fatal`, `dovecot.panic` and
  `dovecot.database_unavailable` as above; host and program are empty. Times are read in `import.time_zone` like the
  traditional syslog format. Lines without a time stamp and a Dovecot label are skipped." Also the sentence in the
  time-zone paragraph "MariaDB error logs also write local time without a zone ..." gets "and Dovecot's log file" after
  "MariaDB error logs".
- `docs/ARCHITECTURE.md` (owner Lead, written with this plan): component list and line-framed list name the Dovecot log.
- `docs/decisions/0091-...md` and its index row (owner Lead, done).

## Architecture check

No guarantee in `.squad/project.md` is touched: imported records stay origin `import` and never alert; the import's
idempotency, resume by count and continuation rely on `LineFramed` and the source type, both generic (`Scanner.cs:553`,
`ImportPass.cs:389`). No schema, wire or configuration change; `import.time_zone` is reused. ARCHITECTURE.md gets the new
parser in its lists.

## Security considerations

- [x] Limits: the line reader's 16 KiB cut is on raw bytes (bounded memory); the grammar runs on the decoded string;
  the message is cut to 16,384 UTF-8 bytes **after** decoding (`Utf8Text.Cut`), so U+FFFD expansion never yields a
  refused record. The parser keeps no state beyond the `SyslogClock`; memory is independent of input size. The grammar
  is linear: one fixed-length stamp check, one `IndexOf(": ")`, one `IndexOf(' ')` on the segment, prefix comparisons.
- [x] Exceptions reaching a user: only `InvalidOperationException` with the fixed text `import.time_zone is not set`
  (as syslog and MariaDB); skip reasons are the fixed texts `empty line`, `not a Dovecot line` and the clock's reasons.
  No input text enters a reason.
- Guard forms (the detection is a claim, not a trust boundary): the stamp accepts exactly what `ParseTraditional`
  accepts (English month abbreviations, day `dd` or ` d` in 1-31, `HH:MM:SS` range-checked); the marker check accepts
  exactly six case-sensitive texts with their trailing space at offset 0 of *M* or right after the first `: ` when the
  text before it is non-empty and space-free. A syslog line cannot match: its host is followed by a space, so `S` would
  contain it and a space, unless the host token itself ends in `:` and is followed by a marker (never written by
  rsyslog). Overlap with the journal (`__` prefix) and MariaDB (digit-led header) is impossible by the first character.
- Forging: as documented in the area (*Forged lines* of *Dovecot log file*), any file with the first-line shape is read
  as Dovecot's; in the real file remote text reaches a marker only in the non-default configurations of 0091 option 14.

## Decision records

- `docs/decisions/0091-mail-lines-classified-in-the-syslog-and-journal-parsers.md` — unreleased (no tag exists),
  extended in place (title, Source, a Context paragraph, options 21 to 30, Decision, Consequences), status set back to
  Proposed with its index row.

## Out of scope / follow-ups

- Non-default `log_timestamp` formats and a `mail_log_prefix` with a space (lines skipped, visibly).
- A file that both rsyslog and Dovecot write (*unverified* whether any server does this): it goes to whichever parser
  its first line selects, and the other format's lines are skipped (Dovecot) or misread (syslog). No follow-up issue
  unless such a file is seen.
- `log_path = /dev/stderr` under systemd (Dovecot's file format inside journal messages): not handled.
