# Plan: Parsers for Plesk and web server error logs

Source: Issue #19 | [spec.md](spec.md)
Status: Draft
Tier: security — the change adds parsers of external input (log files: Plesk, web server) and four configuration
options, both named in *Security areas* 10 of `.squad/project.md`, and changes the import's warning output
(area 12).

## Problem / root cause

Summary of `spec.md`: four more sources (Plesk panel log, `sw-cp-server` error log, nginx and Apache error logs)
become `log_line` records with time, priority and message; their server paths come from four new options; lines
that are not in the expected format are stored as raw records and reported as a warning.

Claims of the issue, checked against the code and the upstream sources:

- *No parser for these logs exists* — confirmed. `BuiltInParsers.Create` (`src/Vandox.Core/LogParsing/BuiltInParsers.cs:25`)
  returns journal, mariadb, dovecot and syslog only. None of them claims a first line of these formats: syslog needs
  `<`, a month name or `DDDD-DD-DDT` (`SyslogLine.TryParse`), MariaDB `DDDD-DD-DD ` or `DDDDDD ` (`MariaDbLine`),
  Dovecot a month name, the journal `__CURSOR=`/`__REALTIME_TIMESTAMP=`. A saved `/var/log/nginx/error.log` is
  therefore listed as "no parser recognized the file" today.
- *Paths are not configurable* — confirmed: `ImportConfig` (`src/Vandox.Core/Configuration/ImportConfig.cs`) has
  only `time_zone`; the parsers of 0088 and 0091 deliberately have no path option ("a configurable path has no
  meaning for an import that reads arbitrary copies and archives", 0088 *Context*). For these sources a path has a
  meaning (see *Options considered*, 1): the `sw-cp-server` error log has the nginx format, so only its path tells
  it apart, and a file in an unknown format can be claimed by nothing but its path.
- *Plesk versions move them* — **unverified**: from Plesk's documentation as recalled (the panel log was
  `/usr/local/psa/admin/logs/panel.log` before Plesk 17 and is `/var/log/plesk/panel.log` since); Plesk is closed
  source and docs.plesk.com is not reachable from the session (proxy 403). Not checked on the server either.
- *Unknown formats are dropped today* — confirmed: every parser skips a line it cannot read ("not a syslog line",
  "not a Dovecot line", "line before the first entry"), and a file no parser claims is "not recognized".
- *Dependency #15 is merged* — confirmed (issue closed; `Vandox.Import` exists).

Formats, checked in the sources where they are open:

- **nginx** (checked in nginx 1.18.0, the Ubuntu 22.04 version, `src/core/ngx_log.c` `ngx_log_error_core` and
  `src/core/ngx_times.c`): `YYYY/MM/DD HH:MM:SS [level] PID#TID: [*CID ]message`, local time (`ngx_localtime`),
  levels `emerg alert crit error warn notice info debug`. `FastCGI sent in stderr: "%*s"` copies the upstream's
  standard error raw (`ngx_vslprintf`, case `s`, `src/core/ngx_string.c`), so line feeds in PHP output produce
  continuation lines without a time stamp: raw lines are real, not hypothetical.
- **sw-cp-server**: Plesk's panel web server is an nginx build, so its `error_log` has the nginx format —
  **unverified** against Plesk (binaries not inspectable here); the raw-line fallback keeps every line if not.
- **Apache** (checked in httpd 2.4.52, the Ubuntu 22.04 version, `server/log.c` `do_errorlog_default`, `log_ctime`,
  `server/util_time.c` `ap_recent_ctime_ex`; and 2.2.34 `server/log.c`): 2.4 writes
  `[Www Mmm DD HH:MM:SS.uuuuuu YYYY] [module:level] [pid N(:tid T)] ...`, the day zero-padded, local time
  (`ap_explode_recent_localtime`), `[:level]` when the module has no short name (`cpystrn` writes nothing for
  `NULL`); 2.2 writes `[Www Mmm DD HH:MM:SS YYYY] [level] ...`. Levels `emerg alert crit error warn notice info
  debug trace1`…`trace8`. Startup messages (`info->startup`) carry neither time nor level: raw lines. Whether the
  server sets an `ErrorLogFormat` was not checked (Ubuntu's `apache2.conf` does not, as recalled).
- **Plesk panel log** — **unverified** (closed source): taken from the line shape of Plesk documentation examples
  as recalled, `[YYYY-MM-DD HH:MM:SS.mmm] PID:REQUESTID LEVEL [channel] message` (Obsidian) and
  `[YYYY-MM-DD HH:MM:SS] LEVEL ...` (older), levels the Laminas/Zend log names `EMERG ALERT CRIT ERR WARN NOTICE
  INFO DEBUG`, written in the server's local time (also unverified: PHP's `date.timezone` of `sw-engine` was not
  checked). The grammar below takes both shapes; any other is kept raw.

Related observation (no defect today): `SyslogClock.ResolveLocal` (`SyslogClock.cs:81`) builds its local time from
whole seconds and ignores `SyslogTime.FractionTicks`; the MariaDB parser has no fraction, so nothing is lost there.
This change adds the fraction in the new parser after `ResolveLocal` (see *Approach*) and leaves `ResolveLocal`
unchanged.

## Acceptance criteria

The normative rules are written in `docs/areas/log-import.md`, section *Plesk and web server error logs* (written with
this plan), and the option rules in `docs/areas/configuration-and-secrets.md`. Exact texts the tests compare:
warning reasons `not a Plesk panel log line`, `not an nginx error log line`, `not an Apache error log line`,
`invalid date`, `time outside the storable range`; skip reasons `empty line`, `raw line without a time`; time zone
failure `import.time_zone is not set`; option error
`must be an absolute, clean path of a file in a directory (no trailing slash, no . or .. elements)`; summary line
`Lines kept raw:      <n>` and section heading `Lines kept as raw lines (format not recognized):`; log message
`raw lines kept {Path} {SourceType} {Raw}` at level Warning, event ID 29.

Tests marked *(step 6)* pass on the skeleton (pins, guards, bounds) and are written in step 6, not step 5.

- [ ] **AC1 — Plesk panel lines.** `ErrorLogLine.TryParse(ErrorLogFormat.PleskPanel, line)` reads
  `[YYYY-MM-DD HH:MM:SS(.D{1,6})] (D{1,10}:A{1,64} )LEVEL( |end)` (`D` an ASCII digit, `A` an ASCII letter or digit,
  parentheses optional parts) and returns time digits, the fraction in ticks,
  the priority (`EMERG` 0 … `DEBUG` 7) and the message (the text after the `] ` that closes the time stamp); every
  other shape gives `null`. `ErrorLogParser` of type `plesk-panel` maps a line to a record: source `plesk-panel`,
  origin import, sequence 0, `captured_at` the local time in `import.time_zone` plus the fraction, `log` the file
  name, `host`/`program` empty, `pid` 0, `event` empty, `truncated` as below.
  Tests: `ErrorLogLineTests.ErrorLogLineTryParseReadsPleskPanelLines`,
  `ErrorLogLineTests.ErrorLogLineTryParseRefusesOtherPleskPanelShapes`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncFillsTheFieldsOfAPleskPanelRecord`.
- [ ] **AC2 — nginx and sw-cp-server lines.** `TryParse(ErrorLogFormat.Nginx, …)` reads
  `YYYY/MM/DD HH:MM:SS [LEVEL]( |end)` (`emerg` 0 … `debug` 7), message from the `[` of the level on. Parsers of
  types `nginx-error` and `sw-cp-server-error` map lines as in AC1 with their own source type.
  Tests: `ErrorLogLineTests.ErrorLogLineTryParseReadsNginxLines`,
  `ErrorLogLineTests.ErrorLogLineTryParseRefusesOtherNginxShapes`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncFillsTheFieldsOfAnNginxRecord` (DataRow for both types).
- [ ] **AC3 — Apache lines.** `TryParse(ErrorLogFormat.Apache, …)` reads
  `[WWW MMM DD HH:MM:SS(.D{6}) YYYY] [(M{0,64}:)LEVEL]( |end)` with English weekday and month abbreviations,
  module of `A-Z a-z 0-9 _`, levels `emerg` 0 … `debug` 7 and `trace1`…`trace8` 7; message from the `[` of the
  level bracket on. Type `apache-error`.
  Tests: `ErrorLogLineTests.ErrorLogLineTryParseReadsApacheLines` (2.4 with and without module and fraction, 2.2),
  `ErrorLogLineTests.ErrorLogLineTryParseRefusesOtherApacheShapes` (space-padded day, five fraction digits,
  lower-case weekday, two-digit year, `warning`, a hyphen in the module, a 65-character module, a startup line),
  `ErrorLogParserTests.ErrorLogParserParseAsyncFillsTheFieldsOfAnApacheRecord`.
- [ ] **AC4 — fixtures.** One shortened sample file per format under `testdata/logs/` (`plesk-panel.log`,
  `nginx-error.log`, `apache-error.log`), each with entries of several levels and one raw continuation line, is
  read with the expected priorities, times, messages and one warning. The nginx and Apache samples follow the
  source formats above; the Plesk sample is constructed from the documented shape and says so in a comment of the
  test, not of the file. Test: `ErrorLogParserTests.ErrorLogParserParseAsyncReadsTheFixtures` (DataRow per file).
- [ ] **AC5 — time.** Times are local in `import.time_zone` through `SyslogClock.ResolveLocal` with the instant of
  the previous recognized line as `previous` (repeated hour), the fraction added afterwards; a time that does not
  exist or is outside the storable range keeps the line as a raw line with the reason `invalid date` or
  `time outside the storable range`; without a zone the first line of the format throws
  `InvalidOperationException` "import.time_zone is not set" (raw lines before it are emitted).
  Tests: `ErrorLogParserTests.ErrorLogParserParseAsyncResolvesTheRepeatedHourFromThePreviousEntry`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncKeepsLinesWithAnInvalidOrUnstorableTimeAsRawLines`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncWithoutZoneFailsAtTheFirstLineOfTheFormat`.
- [ ] **AC6 — raw lines with a warning (parser).** A non-empty line that `TryParse` refuses, or whose time does
  not resolve, becomes a record with `priority` null, `message` the whole line (byte order mark removed on line 1
  only), `captured_at` the capture time of the record emitted before it in this parse, else the file's
  modification time **when `StorableTime.Contains(file.ModTime)`**; the parser calls
  `IRecordEmitter.Warn(lineNumber, reason)` for it, then emits the record. With neither time (no record before it and
  a modification time that is `null` or outside the storable range) the line is skipped as `raw line without a time`
  with no `Warn` and no record. An empty line is skipped as `empty line`. A whole file of an unknown format at a
  configured path becomes raw records only, also when that content is another known format (Apache content given to
  the `nginx-error` parser gives raw lines with `not an nginx error log line`).
  Tests: `ErrorLogParserTests.ErrorLogParserParseAsyncKeepsUnreadableLinesAsRawRecordsWithAWarning`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncGivesRawLinesBeforeTheFirstEntryTheModificationTime` (DataRows: an
  ordinary time, exactly `StorableTime.Min` and exactly `StorableTime.Max`, each the record's `captured_at`),
  `ErrorLogParserTests.ErrorLogParserParseAsyncSkipsRawLinesWithoutAnyTime` (DataRows: `ModTime` null,
  `2300-01-01T00:00:00Z`, `1600-01-01T00:00:00Z`, and `StorableTime.Max` plus one tick; each: no record, no warning,
  one skip `raw line without a time` per raw line before the first line of the format, and the line of the format after
  them stored normally with the following raw line taking its time),
  `ErrorLogParserTests.ErrorLogParserParseAsyncSkipsEmptyLines`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncReadsAWholeFileOfAnUnknownFormatAsRawLines` (DataRows: text of no
  format; the Apache fixture lines given to the `nginx-error` parser),
  `ErrorLogParserTests.ErrorLogParserParseAsyncRemovesTheByteOrderMarkOfLineOne`.
- [ ] **AC7 — bounds and robustness.** A message is cut to 16,384 UTF-8 bytes of the decoded text (invalid bytes
  count three) with `truncated`; a line the reader cut at 16 KiB sets `truncated`; the parser honors cancellation
  and passes on the emitter's exception; reasons are the fixed texts above; parsing twice gives the same records;
  memory stays bounded for any number of raw lines.
  Tests: `ErrorLogParserTests.ErrorLogParserParseAsyncCutsALongMessageAndMarksItTruncated`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncMarksALineCutByTheReaderAsTruncated`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncHonorsACancelledToken`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncPassesOnTheExceptionOfTheEmitter`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncReportsOnlyFixedReasons`,
  `ErrorLogParserTests.ErrorLogParserParseAsyncIsDeterministic` *(step 6)*,
  `ErrorLogParserTests.ErrorLogParserParseAsyncRetainsBoundedMemoryForManyRawLines` *(step 6; `PatternStream`,
  `CountingEmitter`, the `RetainedBytes` helper form of `.squad/stack.md`)*.
- [ ] **AC8 — recognition.** `Detect` returns `MatchPath` when `LogPath.Matches(configuredPath, file.Name)`, whatever
  the head; otherwise, for `plesk-panel`, `nginx-error` and `apache-error` only, `MatchContent` when the name is not a
  syslog name (`SyslogParser.HasSyslogName`) and the first line of the head (byte order mark and a `\r` before `\n`
  removed, a line cut by the end of the head counts) is accepted by `TryParse`; else `NoMatch`. `sw-cp-server-error`
  never claims by content. `Type` is the constructor's type and `LineFramed` is true.
  Tests: `ErrorLogParserTests.ErrorLogParserDetectClaimsAFileByItsFirstLine`,
  `ErrorLogParserTests.ErrorLogParserDetectNeverClaimsContentUnderASyslogName`,
  `ErrorLogParserTests.ErrorLogParserDetectClaimsAFileAtItsConfiguredPathWhateverItsContent` (DataRows include an
  Apache first line at `nginx/error.log` for the `nginx-error` parser: `MatchPath`),
  `ErrorLogParserTests.ErrorLogParserDetectOfSwCpServerClaimsOnlyByPath`,
  `ErrorLogParserTests.ErrorLogParserIsLineFramedAndKeepsItsType`.
- [ ] **AC9 — path match.** `LogPath.Matches(configuredPath, fileName)` is true when the configured path has at
  least a directory and a file name, the file name's parent directory equals the configured path's parent directory,
  and its base name equals the configured base name, alone or followed by `.` and one or more ASCII digits or by `-`
  and exactly eight ASCII digits; ordinal and case-sensitive.
  Test: `LogPathTests.LogPathMatchesParentDirectoryAndBaseNameWithRotationSuffix` (DataRows: `nginx/error.log`,
  `var/log/nginx/error.log`, `server-2026-10/nginx/error.log`, `nginx/error.log.1`, `nginx/error.log.12`,
  `nginx/error.log-20261010` true; `nginx/error.log.`, `nginx/error.log.1a`, `nginx/error.log-2026101`,
  `nginx/error.log.old`, `error.log`, `apache2/error.log`, `Nginx/error.log`, `nginx/Error.log` false;
  configured `/error.log` and `/` never match; `/usr/local/psa/admin/logs/panel.log` matches `logs/panel.log`).
- [ ] **AC10 — registry.** `BuiltInParsers.Create(timeZone, paths)` returns journal, mariadb, dovecot, syslog,
  `plesk-panel`, `sw-cp-server-error`, `nginx-error`, `apache-error` in this order; the registry gives every head to
  exactly the parser expected (a configured path beats a content match, a content match beats the syslog name match;
  a syslog name never goes to an error log parser by content; a single-file root `error.log` is claimed by content
  only), and the existing parsers do not claim the new heads nor the new ones the old heads. A path match beats a
  content match in `ParserRegistry`.
  Tests: `BuiltInParsersTests.BuiltInParsersCreateReturnsSystemParsersThenErrorLogParsers` (replaces
  `BuiltInParsersCreateReturnsJournalThenMariaDbThenDovecotThenSyslog`),
  `BuiltInParsersTests.BuiltInParsersCreateListIsAcceptedByTheRegistry` (updated sequence),
  `BuiltInParsersTests.BuiltInParsersRegistryDetectsFilesByContentAndName` (new DataRows, among them
  `nginx/error.log` with an Apache first line → `nginx-error`),
  `BuiltInParsersTests.BuiltInParsersRegistryGivesAFileAtTwoConfiguredPathsToTheEarlierParser` (paths
  `new ErrorLogPaths(DefaultPleskPanel, "/var/log/nginx/error.log", DefaultNginx, DefaultApache)`: `nginx/error.log`
  with an nginx first line → `sw-cp-server-error`; with `SwCpServer` and `Apache` both `/srv/x/error_log`:
  `x/error_log` with an Apache first line → `sw-cp-server-error`),
  `BuiltInParsersTests.BuiltInParsersSystemParsersIgnoreErrorLogHeads`,
  `BuiltInParsersTests.BuiltInParsersErrorLogParsersIgnoreSystemLogHeads`,
  `ParserRegistryTests.ParserRegistryPrefersAPathMatchOverAContentMatch` *(step 6)*.
- [ ] **AC11 — options.** `import.plesk_panel_log`, `import.sw_cp_server_error_log`, `import.nginx_error_log` and
  `import.apache_error_log` default to the paths of the spec, are read from the file, are refused with the exact
  option error above (key and line, never the value) unless the value is an absolute, clean path with at least one
  directory, and are refused as `has no value` when null; `BackendConfig.Keys()` lists them after `log.level` and
  before `import.time_zone`; the example file sets them.
  Tests: `BackendConfigLoaderTests.BackendConfigLoaderDefaultsErrorLogPaths`,
  `BackendConfigLoaderTests.BackendConfigLoaderReadsErrorLogPaths`,
  `BackendConfigLoaderTests.BackendConfigLoaderRefusesInvalidErrorLogPathWithoutShowingIt` (DataRows per key and
  `var/log/x.log`, `/error.log`, `/`, `/var/log/nginx/`, `/var/log/../x.log`, `/var//log/x.log`, `""`),
  `BackendConfigLoaderTests.BackendConfigKeysListErrorLogPathsBeforeTimeZone`, and the existing
  `BackendConfigLoaderLoadsRepositoryExample` (extended with the four default values).
- [ ] **AC12 — warning in the importer.** `RecordEmitter.Warn` counts the line in `FileResult.Raw` and keeps the
  first `ImportLimits.MaxProblems` as `ImportProblem` in `FileResult.RawProblems`, with the line number counted in the
  whole file for a continued file (`LineOffset`, as for skips); `Importer` sums `ImportSummary.Raw`;
  `FileResult.Snapshot` copies both.
  Tests: `ImporterStoreTests.ImporterCountsRawLinesAndKeepsTheirFirstReasons`,
  `ImporterStoreTests.ImporterNumbersRawLinesOfAContinuedFileInTheWholeFile`,
  `FileResultTests.FileResultSnapshotCopiesTheRawLines`. (`LineParser` in `tests/Vandox.Import.Tests` gets a `raw`
  line prefix that calls `Warn` and emits the record — Tester.)
- [ ] **AC13 — warning in the output.** The summary prints `Lines kept raw:      <n>` right after the skipped line
  and, after the section of skipped lines, the section `Lines kept as raw lines (format not recognized):` listing
  every file with raw lines as `  "<path>": <n> lines` and its reasons as `    line <n>: "<reason>"` (quoted, as the
  skipped lines); no section without raw lines. A finished file with raw lines logs `RawLinesKept` (Warning) after
  `file finished`; none without.
  Tests: `ImportSummaryWriterTests.ImportSummaryWriterCountsAndListsRawLines`,
  `ImportSummaryWriterTests.ImportSummaryWriterPrintsNoRawSectionWithoutRawLines`,
  `ImportProgressLoggerTests.ImportProgressLoggerWarnsAboutTheRawLinesOfAFinishedFile`,
  `ImportProgressLoggerTests.ImportProgressLoggerDoesNotWarnWithoutRawLines`.
- [ ] **AC14 — end to end.** `vandoxd import` with no hook and a configuration that sets `import.time_zone` and
  `import.nginx_error_log: /srv/web/proxy.log` imports `web/proxy.log` (content of an unknown format) as
  `nginx-error` raw lines, `sw-cp-server/error_log` (nginx content) as `sw-cp-server-error`, `vhost/error_log`
  (Apache content) as `apache-error`, prints `Lines kept raw:` with the count, logs `"msg":"raw lines kept"` on
  standard error and exits 0.
  Test: `ImportCommandTests.ImportCommandReadsErrorLogsByConfiguredPathAndContent`.

No content was supplied verbatim by the issue.

## Approach

1. `Confidence.MatchPath = 3` above `MatchContent`; `ParserRegistry` is unchanged (it takes the highest).
2. `IRecordEmitter.Warn(long line, string reason)`: "a line kept as a raw record because the parser could not read
   it as a line of its format". The parser calls it, then `RecordAsync` with the raw record, for the same line.
3. `ErrorLogLine.TryParse(format, line)`: a hand-written, linear parser over the decoded line, ordinal and
   ASCII only, building a `SyslogTime` (year from the line, `OffsetMinutes` null) without building a date. Digits of
   month, day, hour, minute and second are not range-checked by the grammar (`ResolveLocal` does).
4. `ErrorLogParser` (one class, four instances): per line, as in `DovecotLogParser.Map`: byte order mark on line 1,
   empty line skip, `Utf8Text.Decode(bytes, int.MaxValue, out _)`, `TryParse`; a match throws without a zone
   (`SyslogParser.TimeZoneNotSet`), else `SyslogClock.ResolveLocal(zone, time, previousRecognized, out instant)`,
   then `instant.AddTicks(FractionTicks)` checked with `StorableTime.Contains` (else `time outside the storable
   range`); message `Utf8Text.Cut(…, ModelLimits.MaxTextBytes, out cut)`. A raw line takes the capture time of
   the last emitted record, else `file.ModTime` when `file.ModTime is { } time && StorableTime.Contains(time)` (the
   check of `SyslogClock.AnchorFromModTime`, `SyslogClock.cs:270-272`), computed once per parse; otherwise it is
   skipped as `raw line without a time` before any `Warn`. State: two `DateTimeOffset?`.
5. `LogPath.Matches` compares the last two components of the configured path with the last two of the listed
   name; the configured paths are never opened or resolved.
6. `BuiltInParsers.Create(string? timeZone, ErrorLogPaths paths)` appends the four parsers after syslog;
   `ImportCommand` passes `config.Import.GetErrorLogPaths()`.
7. Options in `ImportConfig` with defaults from `ErrorLogPaths.Default*`; `BackendConfigLoader.Validate` checks them
   with `IsLogFilePath` (`IsCleanAbsolutePath` plus at least two elements).
8. Importer, summary, progress logger as in AC12–AC13.

## Options considered

The options and why they lost are recorded in
[`docs/decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md`](../../docs/decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md).
In short:

1. **Paths:** content only, no option (0088, 0091) — loses: an AC of the issue, and content cannot tell the
   `sw-cp-server` log from nginx's nor claim a file in an unknown format; full absolute path compared with the listed
   name — loses: copies and archives have any root; every component of the shorter path — loses:
   `server-2026-10/nginx/error.log` of a copy root does not align; **parent directory and base name with rotation
   suffix** (chosen); glob patterns or lists per source — loses: the strict YAML decoder has no sequences, per-domain
   logs are found by content.
2. **Precedence of a path:** a name match (1) — loses to nginx's content match for the `sw-cp-server` log; parsers
   that decline content at another parser's path — couples the parsers; **a new level above content** (chosen).
3. **Unreadable lines:** skip (as the other parsers) — the issue forbids it; join to the entry before (as the MariaDB
   error log) — bounded-entry logic for every format, and a raw line already keeps its text and the time of its
   entry; a time from the import run — not deterministic, breaks resume; **one raw record per line with the time of
   the record before, else the modification time** (chosen).
4. **The warning:** report via `Skip` — the line is stored, so "skipped" would be false; an `event` on the record —
   no operator sees it, and an event is a recognized occurrence; **a `Warn` call counted per file, a summary line and
   section, and a warning log line** (chosen).
5. **Message:** the text after the level (pid, request ID and module dropped) — lossy and inconsistent across the
   formats (Plesk's level follows the pid); the whole line — repeats the time; **the text after the time stamp**
   (chosen: lossless apart from the time, which is `captured_at`). Pid, module and channel stay in the message.
6. **Scope of "unknown formats":** every file no parser claims imported as raw lines — would store `wtmp`, `lastlog`
   and other binary files of a saved `/var/log` and contradicts the outcome "not recognized" of the import area;
   **lines and files of these four sources** (chosen).
7. **Type names:** `nginx`, `apache` — leave no room for the access log parsers of #90; **`nginx-error`,
   `apache-error`, `sw-cp-server-error`, `plesk-panel`** (chosen).
8. **Parser classes:** one per source — four near-identical classes; **one class with a format** (chosen).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `LogParsing/Confidence.cs` | new member `MatchPath` |
| Vandox.Core | `LogParsing/IRecordEmitter.cs` | new member `Warn` |
| Vandox.Core | `LogParsing/ErrorLogFormat.cs` | new enum |
| Vandox.Core | `LogParsing/ErrorLogPaths.cs` | new record |
| Vandox.Core | `LogParsing/ErrorLogLine.cs` | new internal class |
| Vandox.Core | `LogParsing/LogPath.cs` | new internal static class |
| Vandox.Core | `LogParsing/ErrorLogParser.cs` | new parser |
| Vandox.Core | `LogParsing/BuiltInParsers.cs` | `Create` takes the paths, appends four parsers (rewritten) |
| Vandox.Core | `Configuration/ImportConfig.cs` | four options, `GetErrorLogPaths` |
| Vandox.Core | `Configuration/BackendConfig.cs` | `Keys()` |
| Vandox.Core | `Configuration/BackendConfigLoader.cs` | validation of the four options |
| Vandox.Import | `RecordEmitter.cs`, `FileResult.cs`, `ImportSummary.cs`, `Importer.cs` | `Warn`, `Raw`, `RawProblems`, `Snapshot`, `Summarize` |
| Vandox.Backend | `Cli/ImportCommand.cs` | passes the paths |
| Vandox.Backend | `Cli/ImportSummaryWriter.cs`, `Cli/ImportProgressLogger.cs`, `Hosting/BackendLog.cs` | raw line total, section, warning log line |
| deploy | `deploy/backend/vandoxd.yaml` | four options, time zone comment |

## Signatures (for the Dev's skeleton)

```csharp
// Vandox.Core.LogParsing
public enum Confidence { NoMatch = 0, MatchName = 1, MatchContent = 2, MatchPath = 3 }

public interface IRecordEmitter
{
    ValueTask RecordAsync(DataRecord record, CancellationToken cancellationToken);   // unchanged
    void Skip(long line, string reason);                                              // unchanged
    void Warn(long line, string reason);                                              // new
}

public enum ErrorLogFormat { PleskPanel = 0, Nginx = 1, Apache = 2 }

public sealed record ErrorLogPaths(string PleskPanel, string SwCpServer, string Nginx, string Apache)
{
    public const string DefaultPleskPanel = "/var/log/plesk/panel.log";
    public const string DefaultSwCpServer = "/var/log/sw-cp-server/error_log";
    public const string DefaultNginx = "/var/log/nginx/error.log";
    public const string DefaultApache = "/var/log/apache2/error.log";
    public static ErrorLogPaths Default { get; }
}

internal sealed class ErrorLogLine
{
    internal SyslogTime Time { get; init; }
    internal byte Priority { get; init; }
    internal string Message { get; init; } = string.Empty;
    internal static ErrorLogLine? TryParse(ErrorLogFormat format, string line);
}

internal static class LogPath
{
    internal static bool Matches(string configuredPath, string fileName);
}

public sealed class ErrorLogParser : ILogParser
{
    public const string PleskPanelType = "plesk-panel";
    public const string SwCpServerType = "sw-cp-server-error";
    public const string NginxType = "nginx-error";
    public const string ApacheType = "apache-error";

    public ErrorLogParser(string type, ErrorLogFormat format, string path, bool claimsContent, DateTimeZone? timeZone);

    public string Type { get; }
    public bool LineFramed { get; }   // true
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head);
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken);
}

public static class BuiltInParsers
{
    public static IReadOnlyList<ILogParser> Create(string? timeZone, ErrorLogPaths paths);   // replaces Create(string?)
}

// Vandox.Core.Configuration
public sealed class ImportConfig
{
    [ConfigKey("plesk_panel_log")] public string PleskPanelLog { get; set; } = ErrorLogPaths.DefaultPleskPanel;
    [ConfigKey("sw_cp_server_error_log")] public string SwCpServerErrorLog { get; set; } = ErrorLogPaths.DefaultSwCpServer;
    [ConfigKey("nginx_error_log")] public string NginxErrorLog { get; set; } = ErrorLogPaths.DefaultNginx;
    [ConfigKey("apache_error_log")] public string ApacheErrorLog { get; set; } = ErrorLogPaths.DefaultApache;
    [ConfigKey("time_zone")] public string? TimeZone { get; set; }                       // unchanged
    public ErrorLogPaths GetErrorLogPaths();
}
// BackendConfig.Keys(): ["web.listen", "ingest.listen", "storage.directory", "log.level",
//   "import.plesk_panel_log", "import.sw_cp_server_error_log", "import.nginx_error_log", "import.apache_error_log",
//   "import.time_zone"]
// BackendConfigLoader: private static bool IsLogFilePath(string value);

// Vandox.Import
public sealed class FileResult
{
    public long Raw { get; set; }
    public List<ImportProblem> RawProblems { get; } = [];
}
public sealed class ImportSummary { public long Raw { get; set; } }
internal sealed class RecordEmitter { public void Warn(long line, string reason); }   // IRecordEmitter

// Vandox.Backend
// BackendLog:
[LoggerMessage(EventId = 29, Level = LogLevel.Warning, Message = "raw lines kept {Path} {SourceType} {Raw}")]
internal static partial void RawLinesKept(this ILogger logger, string path, string sourceType, long raw);
// ImportSummaryWriter: private static void WriteRaw(StringBuilder text, List<FileResult> files);
```

Files the skeleton rewrites: `BuiltInParsers.cs` (read; final apart from `Create`), `ImportConfig.cs`,
`BackendConfig.cs` (`Keys`), `IRecordEmitter.cs`, `Confidence.cs`, `RecordEmitter.cs` (`Warn` added), `FileResult.cs`,
`ImportSummary.cs`, `ImportCommand.cs` (one call), `BackendLog.cs` (one method).

## Test files

New: `tests/Vandox.Core.Tests/ErrorLogParserTests.cs`, `tests/Vandox.Core.Tests/ErrorLogLineTests.cs`,
`tests/Vandox.Core.Tests/LogPathTests.cs`; fixtures `testdata/logs/plesk-panel.log`, `testdata/logs/nginx-error.log`,
`testdata/logs/apache-error.log` (Tester).
Extended: `tests/Vandox.Core.Tests/BuiltInParsersTests.cs`, `tests/Vandox.Core.Tests/ParserRegistryTests.cs`,
`tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs`, `tests/Vandox.Import.Tests/ImporterStoreTests.cs` (the
importer's skip-counting tests live there), `tests/Vandox.Import.Tests/FileResultTests.cs`,
`tests/Vandox.Import.Tests/LineParser.cs` (a `raw` prefix), `tests/Vandox.Backend.Tests/ImportSummaryWriterTests.cs`,
`tests/Vandox.Backend.Tests/ImportProgressLoggerTests.cs`, `tests/Vandox.Backend.Tests/ImportCommandTests.cs`.

Existing test code affected by changed signatures — the **Dev** adapts it in step 4, mechanically:

- `BuiltInParsers.Create(string?)` goes away: `tests/Vandox.Core.Tests/JournalExportParserTests.cs:877` and `:900`,
  `tests/Vandox.Core.Tests/SyslogParserTests.cs:887`, `tests/Vandox.Core.Tests/BuiltInParsersTests.cs:41`, `:62`,
  `:83`, `:139`, `:157` → `BuiltInParsers.Create(<same>, ErrorLogPaths.Default)`. Indices `[0]` and `[3]` stay valid
  (the new parsers come after syslog).
- `IRecordEmitter.Warn` is added: the implementers `tests/Vandox.Core.Tests/RecordingEmitter.cs` (a
  `Warnings` list of `(long Line, string Reason)`, filled by `Warn`, mirroring `Skips`) and
  `tests/Vandox.Core.Tests/CountingEmitter.cs` (a `Warnings` count) get the member. The Tester checks these edits in
  its coverage step.

The **Tester** changes the assertions of `BuiltInParsersCreateReturnsJournalThenMariaDbThenDovecotThenSyslog`
(replaced by `BuiltInParsersCreateReturnsSystemParsersThenErrorLogParsers`) and of
`BuiltInParsersCreateListIsAcceptedByTheRegistry` in step 5 (new behavior, not a mechanical adaptation).

## Areas

- `docs/areas/log-import.md` — new section *Plesk and web server error logs*; *Command* (the four options and the
  time zone), *Repeatable import* and *Parsers* (`LineFramed` lists, the path level of `Detect`, `Warn` in the
  contract), *System log parsers* (the registered list), *Result* (raw lines in the summary and the warning log
  line), *Related decisions*, *Implementation*. Written by the Lead with this plan.
- `docs/areas/configuration-and-secrets.md` — four rows of the backend option table and their rule; the time zone
  bullet names the error logs. Written by the Lead with this plan.

No new area.

## Documentation updates

| File | Change | Owner |
| ---- | ------ | ----- |
| `docs/areas/log-import.md`, `docs/areas/configuration-and-secrets.md` | see *Areas* | Lead (done with the plan) |
| `docs/decisions/0092-…md`, `docs/decisions/README.md` | record and index row (Proposed) | Lead (done with the plan) |
| `README.md` | *Backend options*: four rows (key, default, description) and the `import.time_zone` row names the error logs; the `import.apache_error_log` row names `/var/log/httpd/error_log` as the RHEL-family path; *Import logs*: a bullet of supported sources for the four logs (types, paths and the path rule, content claim, raw lines), the summary sentence names the lines kept raw, the "grew" limit and the time zone paragraph name the error logs. The Plesk panel log's format and time zone, the `sw-cp-server` format and the Plesk default paths are stated as **not verified** (as in the area document), never as fact | Dev |
| `deploy/backend/vandoxd.yaml` | the four options set to their defaults with a comment each (server path, the parent-directory-and-name rule); the `apache_error_log` comment names `/var/log/httpd/error_log` for RHEL-family servers; the `plesk_panel_log` and `sw_cp_server_error_log` comments say the Plesk paths are not verified; the `time_zone` comment names the error logs | Dev |
| `docs/ARCHITECTURE.md` | lines 10 and 46 (parser lists), line 228 (line-framed list), the record list after the import paragraph (link 0092) | Dev |
| `.squad/project.md` | *Security areas* 10: `ErrorLogParser`, `ErrorLogLine`, `LogPath` and the four options, record 0092; *Test doubles*: `RecordingEmitter` and `CountingEmitter` record warnings | Dev |
| `src/Vandox.Core/LogParsing/BuiltInParsers.cs` XML doc | registry order | Dev |

## Architecture check

No guarantee of `docs/ARCHITECTURE.md` or `.squad/project.md` changes: imported records keep origin `import` and
never alert (0022); the agent is not touched. The import's flow (two passes, detection by the registry, resume by
count, continuation of line-framed files, containment) is unchanged; the new parsers are line-framed and
deterministic, so continuation and resume apply as for syslog. The registry gains a confidence level, which no
existing parser returns.

## Security considerations

- [x] Limits: the line reader cuts a line at 16,384 **raw** bytes before decoding (existing); the message is cut to
  16,384 UTF-8 bytes **after** decoding (U+FFFD counts three), so a stored message never exceeds the model limit.
  The grammar's bounds (fraction 1 to 6 or exactly 6 digits, pid 1 to 10 digits, request ID 1 to 64, module 0 to 64)
  count ASCII characters of the decoded line, which equal bytes; no limit depends on decoding. Configuration values
  are bounded by the file size limit of the loader (existing).
- [x] Exceptions reaching a user: `InvalidOperationException` "import.time_zone is not set" (the file's failure
  reason, existing text); `ConfigException` `config: <file>:<line>: import.<key>: must be an absolute, clean path of
  a file in a directory (no trailing slash, no . or .. elements)` and `… has no value` (never the value). No runtime
  exception text reaches the summary: `TryParse` and `Matches` never throw for input (no `int.Parse` on unchecked
  text, no date built before the range checks of `ResolveLocal`).
- Paths: the configured paths are compared as text with the cleaned listed names and never opened, resolved or
  joined with a file system path, so they add no file access (security area 9 unchanged).
- Parsing: hand-written, linear in the line length, ordinal ASCII comparisons, no regular expression on input;
  state per parse is two instants, so memory is independent of the input.
- Output: warning reasons are fixed texts; the summary quotes path and reason through `Terminal.Quote`; the
  warning log line carries path and source type as structured attributes (JSON-escaped, area 12).
- Forged lines (limitation, documented in the area): these logs hold client and application text. nginx writes
  FastCGI standard error unescaped, so a web application (or remote input echoed into a PHP message) can write a
  line feed followed by a line in the log's format, which becomes an entry with any time and priority; any local
  process that may write to the files can do the same. The priority is a classification of text, not proof of
  origin, as for the other parsers (0086, 0088, 0091).

## Challenge

Devil's Advocate (no major objections; four minor):

1. *Two path options can match the same file; the registry silently picks the earlier.* **Accepted (document and
   test), refusal rejected.** The area document already stated "the one registered first wins"; it now names the
   example, says the configuration does not refuse it, and record 0092 gives the reason. A refusal in
   `BackendConfigLoader.Validate` lost: equal paths are easy, but overlaps through a rotation suffix
   (`/x/error.log` and `/x/error.log.1`) also tie on some files, so the check would need a second matching rule in the
   loader, for a misconfiguration whose result is deterministic (registry order, `ParserRegistry.Detect` takes the
   first parser with the highest confidence, `ParserRegistry.cs:62`) and visible in the summary's source type. Pinned
   by `BuiltInParsersRegistryGivesAFileAtTwoConfiguredPathsToTheEarlierParser` (AC10).
2. *Apache's default path is Debian/Ubuntu only.* **Accepted.** README row and the `vandoxd.yaml` comment name
   `/var/log/httpd/error_log` for RHEL-family servers (*Documentation updates*); the area document says so after the
   defaults table. The default stays `/var/log/apache2/error.log` (the monitored server runs Ubuntu 22.04, `docs/ARCHITECTURE.md:19`).
3. *A file at the nginx path with Apache content must be `nginx-error` raw lines.* **Accepted.** DataRows in
   `ErrorLogParserDetectClaimsAFileAtItsConfiguredPathWhateverItsContent` (AC8),
   `BuiltInParsersRegistryDetectsFilesByContentAndName` (AC10) and
   `ErrorLogParserParseAsyncReadsAWholeFileOfAnUnknownFormatAsRawLines` (AC6); the area document names the case.
4. *Unverified claims must stay unverified in user-facing text.* **Accepted.** The area document already words them
   as not verified; the README and `vandoxd.yaml` rows of *Documentation updates* now require the same wording, and the
   Reviewer checks it there.

## Security review

1. **(Blocking) Raw-line fallback time from an unchecked `file.ModTime`.** **Accepted, revised.** Confirmed in the code:
   `Scanner.cs:203` (`entry.ModificationTime.ToUniversalTime()`) and `Scanner.cs:395` (`File.GetLastWriteTimeUtc`)
   pass the time into `LogFile` (`Scanner.cs:420`, `:542`) without a range check, and `RecordEmitter.RecordAsync`
   (`RecordEmitter.cs:189`) refuses a record outside the range through `RecordRules.CheckImportRecord`
   (`src/Vandox.Storage/RecordRules.cs:28`) as a skip, after the parser's `Warn` had already counted it as raw. The
   fallback now uses `file.ModTime` only when `StorableTime.Contains` holds, as `SyslogClock.AnchorFromModTime` does;
   otherwise the line is skipped as `raw line without a time` with no warning (AC6, *Approach* step 4, area *Raw
   lines*, record 0092 *Consequences*). Pinned by DataRows on `ErrorLogParserParseAsyncSkipsRawLinesWithoutAnyTime`
   (year 2300, year 1600, `StorableTime.Max` plus one tick, null) and the bounds `StorableTime.Min`/`Max` on
   `ErrorLogParserParseAsyncGivesRawLinesBeforeTheFirstEntryTheModificationTime`. The other time source of a raw line,
   the capture time of the record before it, is always storable because the parser emits only records whose instant
   passed `StorableTime.Contains` (or raw records whose time is itself such an instant or a checked modification time).
2. **(Non-blocking) State that a refused raw record counts as skipped.** **Accepted.** The area's *Raw lines* now says
   that should the importer refuse a raw record for another rule, its line appears among the skipped lines and in the
   raw count (the warning comes first) and nothing is stored; with the time and message checks of the parser this is
   not expected to happen.

## Decision records

- [`docs/decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md`](../../docs/decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md)
  (Proposed, indexed).

## Out of scope / follow-ups

- Joining raw continuation lines (nginx's multi-line FastCGI output) to their entry; the raw records keep the text
  and the entry's time, so a later change can group them without losing anything already stored. No follow-up issue
  unless the Product Manager asks.
- Access logs (#90), signature events for these logs (#21), live shipping (#37).
- Verifying the Plesk panel log format, its time zone and the Plesk default paths on the server: noted as unverified
  in the area document; a differing format is kept as raw lines and an operator can set the path options.
