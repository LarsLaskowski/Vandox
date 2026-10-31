# Plan: Ship the Plesk and web server error logs from the agent

Source: Issue #289 | [spec.md](spec.md)
Status: Revised (1)
Tier: standard — a new parser of external log lines and new values of an existing option in the agent, with no new right, guard or other security control (the Product Manager's option A keeps the agent's rights).
Security areas touched: 6 Agent privileges (`deploy/agent/README.md` gains the by-hand read grant; no right of the unit or the installer changes), 10 Parsing of external input (the new line grammars in `internal/errorlogline`, the new values of `log_shipping.files[].format`, line times bounded for the collector's bookkeeping), 12 Logging and display of external data (four new sources of external text through `fileRecord`)

## Problem / root cause

See [spec.md](spec.md), *Problem*. The collector already receives the format of each file (`internal/agent/agent.go:303`) but ignores
it: `internal/logship/files.go:325` always calls `syslogline.Parse` and `files.go:355` always sets the source `syslog`; the configuration
accepts only `syslog` (`internal/config/logshipping.go`, `logFormatReason`, `checkFiles`).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The format reaches the collector and is ignored there; the configuration refuses every format but `syslog`. | `internal/agent/agent.go:303`, `internal/logship/logship.go:71`, `files.go:325,355`; `internal/config/logshipping.go` (`syslogFormat`, `logFormatReason`, `checkFiles`) | confirmed |
| 2 | The import's grammar: Plesk `[YYYY-MM-DD HH:MM:SS(.1-6 digits)] (1-10 digits:1-64 letters/digits )LEVEL( |end)`, message from after `] `; nginx `YYYY/MM/DD HH:MM:SS [level]( |end)`, message from `[`; Apache `[Www Mmm DD HH:MM:SS(.6 digits) YYYY] [(0-64 of A-Za-z0-9_ then :)level]( |end)` with `trace1`-`trace8` as 7, message from the second `[`. Digits are not range-checked by the grammar. | `src/Vandox.Core/LogParsing/ErrorLogLine.cs:11-16,57-215,216-340` | confirmed |
| 3 | The import resolves a line's time with `SyslogClock.ResolveLocal` (month 1-12, day 1 to the month's days, hour 0-23, minute and second 0-59, year 1677-2262, then the storable range), adds the fraction, checks the storable range again, and only then makes the instant the previous one. | `ErrorLogParseSession.cs:92-123`, `SyslogClock.cs:81-91,160-177`, `SyslogTime.cs:69-72` | confirmed |
| 4 | A raw line of the import gets the time of the record emitted before it, else the file's modification time, else it is skipped; the message is the whole decoded line, cut to 16,384 bytes. | `ErrorLogParseSession.cs:50,133-174` | confirmed |
| 5 | The Go `Clock` resolves only year-less times (`resolve` infers the year) and has no resolve for a time with its year; its storable bounds equal the import's. | `internal/syslogline/clock.go:14-56` | confirmed |
| 6 | Live, a line without a header takes `st.lastTime`, the last line's time or the run's start when the position was taken without a line; the clock is one per configured path and carries over a rotation. | `files.go:31-38,102,120,326-335` | confirmed |
| 7 | The backend accepts any `log_line` source fitting the name pattern. | no source check in `src/Vandox.Backend/Ingest`; `docs/areas/wire-format.md:93` | confirmed |
| 8 | The signature detector matches stored lines by source or program and message, regardless of origin. | `src/Vandox.Core/Signatures/SignatureDetector.cs:132-135` | confirmed |
| 9 | The three samples give no signature event; the signature lines exist only as C# constants. | `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs:806-827` (`BuiltInSignaturesErrorLogFixturesYieldNothing`), `:833-861` | confirmed |
| 10 | `tzCentralEurope()` has Europe/Berlin's 2026 transitions. | `internal/logship/world_test.go:648-660` | confirmed |
| 11 | Golden import records read by Go tests exist already. | `internal/logship/journal_test.go:25-53`; `tests/Vandox.Core.Tests/JournalExportParserTests.cs:1225-1245` | confirmed |
| 12 | Go replaces each invalid byte with one U+FFFD and .NET's decoder each maximal invalid subsequence, so `\xe2\x82A` gives two and one. | to verify: Dev, step 6, a scratch Go test of `procstats.CleanText` and a scratch C# run of `Utf8Text.Decode` on the bytes, before E12 is written into `docs/areas/agent.md` | unverified |
| 13 | Measured by Security on Ubuntu 24.04 (acl 2.3.2, logrotate 3.21.0): with the file's ACL alone, `copytruncate` and `create` without a mode keep the agent's read, `create 0640 root root` and `create 0600 root root` lose it; with a default ACL, `create 0640` keeps it and `create 0600` leaves it without effect; a default ACL replaces the umask for new files (umask 077 gave 0644 in a 0755 directory) and is inherited by new subdirectories; `setfacl -P` leaves a link argument and its target untouched with exit 0, plain `setfacl` grants on the target. | to verify: Dev, step 6, the dry run's matrix on Ubuntu 22.04 (*Approach*); measured so far by Security on the session host | unverified |
| 17 | A line time is used, besides the record, as the file's last time and the run's latest time (`files.go:326-337`), which start and end the gaps of a truncation, a lost rotation (`files.go:162,192,240,333`) and a given-up span (`state.go:106-114`, `logship.go:242-247`), and the state file keeps it (`internal/agentstate/agentstate.go:578,606`). A syslog year-less time is bounded to 24 h ahead (`clock.go:41-52`), an RFC 3339 syslog time and the new formats' times are not. | the files and lines named; Security's finding B1 | confirmed |
| 18 | The existing test `truncationAfterAFutureLine` pins today's gap of a future-dated line, `[line, line + 1 µs)`. | `internal/logship/files_test.go:382-395` | confirmed |
| 14 | The owner, group and mode of the four files and their directories on the server, how Plesk rotates `panel.log` and `sw-cp-server/error_log`, and whether `plesk repair fs` resets ACLs. | to verify: the Product Manager on the server, in the earliest real run; no server access now (escalation answer), so the README marks each statement unverified | unverified |
| 15 | Record 0121 is unreleased, 0092 released. | `git ls-remote --tags origin` (only `v0.1.0`); `git cat-file -e v0.1.0:<record>` | confirmed |
| 16 | The unit hides nothing under `/var/log`. | `deploy/agent/README.md:614`; `deploy/agent/vandox-agent.service` | confirmed |

## Acceptance criteria

The feature's criteria are AC1-AC11 in [spec.md](spec.md).

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestParse_PleskPanelLines`, `TestParse_NginxLines`, `TestParse_ApacheLines` (the grammar cases of `ErrorLogParserTests` and every bound of fact 2 at and past its limit) | `internal/errorlogline/errorlogline_test.go` |
| AC1 | `TestFiles_ErrorLogSamplesMapAsTheImportGolden` (subtests `plesk-panel`, `nginx-error`, `apache-error`, and `sw-cp-server-error` on the nginx sample with the source replaced; zone `tzFixed(utcPlus2)`, `min_priority: debug`, file appearing after the first run so it is read from line 1) | `internal/logship/files_test.go` |
| AC1 | `ErrorLogParserParseAsyncParsesTheSamplesAsTheGoldenRecords` (zone +02:00) | `tests/Vandox.Core.Tests/ErrorLogParserTests.cs` |
| AC2 | `TestFiles_WebSignatureLinesMapAsTheImportGolden` (zone UTC) | `internal/logship/files_test.go` |
| AC2 | `ErrorLogParserParseAsyncParsesTheWebSignatureFixturesAsTheGoldenRecords`; adapt `BuiltInSignaturesWebServerErrorLogsYieldPhpAndNginxEvents` to read the two fixtures | `tests/Vandox.Core.Tests/ErrorLogParserTests.cs`, `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs` |
| AC2 | `BuiltInSignaturesGoldenWebSignatureRecordsYieldPhpAndNginxEvents` (the golden records as agent records: the same three events with the same lines as on import) | `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs` |
| AC3 | `TestParse_LinesNotOfTheFormatAreRaw`; `TestClock_ResolveLocalRefusesWhatTheImportRefuses` (month 0 and 13, day 0, 31 April, 29 February of 2025 and of 2024 accepted, hour 24, minute 60, second 60, year 1676 and 2263, a fraction that crosses 2262-04-11T23:47:16.8547758Z) and `TestClock_ResolveLocalKeepsThePreviousInstantOnFailure` | `internal/errorlogline/errorlogline_test.go`, `internal/syslogline/clock_test.go` |
| AC3 | `TestFiles_ErrorLogLinesWithoutAUsableTimeAreShippedRaw` (30 February, hour 24, year 1600, a non-format line: whole line, no priority, the previous line's time, no gap) | `internal/logship/files_test.go` |
| AC4 | adapt `TestLoadAgent_LogShippingAccepted` (one row per format) and `TestLoadAgent_LogShippingErrors` (`json`, `SYSLOG`, `nginx`, `NGINX-ERROR`, empty, each with the exact reason `must be one of syslog, plesk-panel, sw-cp-server-error, nginx-error, apache-error`) | `internal/config/logshipping_test.go` |
| AC4 | `TestLogs_NewRefusesAnUnknownFormat` | `internal/logship/logship_test.go` |
| AC5 | `TestFiles_ErrorLogFileFollowsTheFileRules` (subtests: rotation by rename, rotation while stopped, truncation with its gap, first start at the end, a file appearing later, restart at the saved position, the 1,000-record budget, `permission denied`, and an unreadable new file after a rotation: no record and no gap while it stays unreadable, then the renamed file's rest before the new file's lines) | `internal/logship/files_test.go` |
| AC6 | `TestFiles_ErrorLogLinesAreFilteredAfterMapping` (`info` drops nginx `debug`, Plesk `DEBUG`, Apache `trace3`, keeps a raw line; rules on `log`, `priority`, `message` `^\[warn\] `; a rule with `program` never matches) | `internal/logship/files_test.go` |
| AC7 | `TestFiles_ErrorLogRawLineWithoutAPredecessorTakesTheLastTime` (first start at the end; a file appearing later; after a found rotation the renamed file's last line time) | `internal/logship/files_test.go` |
| AC8 | `TestFiles_ErrorLogLineIsCutAsTheImportCutsIt` (a format line over 16,384 bytes with a 3-byte character across the cut; byte order mark at offset 0; `\r\n`; an empty line) | `internal/logship/files_test.go` |
| AC9 | `TestClock_ResolveLocalFollowsTheDaylightSavingRules`; `TestFiles_ErrorLogDaylightSavingMatchesTheImportGolden` (`tzCentralEurope()`, subtests one run and two runs; a further subtest: across a rotation the previous instant carries over) | `internal/syslogline/clock_test.go`, `internal/logship/files_test.go` |
| AC9 | `ErrorLogParserParseAsyncParsesTheDaylightSavingFixtureAsTheGoldenRecords` (`Europe/Berlin`) | `tests/Vandox.Core.Tests/ErrorLogParserTests.cs` |
| AC10 | `TestFiles_HoldsErrorLogFilesWhileTheZoneIsUnknown` | `internal/logship/files_test.go` |
| AC11 | `TestReadme_ErrorLogGrantIsDocumentedAndMarkedUnverified` (README read through `normalizeLineEnds`: the four paths; the commands of *Approach* as the README finally holds them — written from *Approach* in step 5 and adapted by the Tester in step 6 when the dry run changes them; every `setfacl` with `-P`; the default ACL's reach; `log file not readable` and `link refused`; the data sent; the unverified marker on each statement of fact 14); the Reviewer checks that `docs/areas/agent.md` names E11 and E12 | `deploy/agent/readme_test.go` |
| AC12 | `TestFiles_LineDatedAfterTheRunKeepsTheGapBounds` (subtests for `nginx-error` with `2262/04/11 23:47:16 [error] ...` and for an RFC 3339 `syslog` line in 2262: then a truncation — gap from the run start of the reading run to the truncation run's start; then a lost rotation whose new file starts with such a line — gap ending at that run's start; the record's own `captured_at` is the line's time; a raw line after it gets the run's start); adapt `truncationAfterAFutureLine` to the new bound; `TestLogs_GiveUpAfterALineDatedAfterTheRunCoversTheRealSpan` (a forged line in one file of a dropped run: the give-up gap starts at the committed time of before and ends at or before the run's start, the journal's span included) | `internal/logship/files_test.go`, `internal/logship/logship_test.go` |

The README test reads a repository document: it uses the existing `normalizeLineEnds` (`deploy/agent/service_test.go:680`), which
`TestService_*` already tests with CRLF input. The procedure is no shell script the operator pastes as one block: each command is
for one file, and a failing command changes nothing the next depends on; the test covers presence, the dry run the behavior.

## Approach

**Fixtures (Tester, step 5).** Under `testdata/logs/`: `web-signatures-nginx.log` and `web-signatures-apache.log` (the content of
`BuiltInSignaturesTests.cs:838-845`, byte for byte), `nginx-error-dst.log` (2026-03-29 02:30:00 `[error]`, then on 2026-10-25 `02:30:00`
and `02:50:00` in the first pass, `02:10:00` and `02:40:00` in the second, each `[error] 1#1: *1 dst N`), and one golden file per
log: `plesk-panel.records.jsonl`, `nginx-error.records.jsonl`, `apache-error.records.jsonl`, `web-signatures-nginx.records.jsonl`,
`web-signatures-apache.records.jsonl`, `nginx-error-dst.records.jsonl`. A golden line has the fields of the journal's golden record
**without `log`** (`captured_at_us`, `source`, `host`, `program`, `pid`, `priority`, `message`, `truncated`), one per import record in
file order. The Tester writes them from the C# import's output (the C# test is the check that they are the import's); the samples
themselves stay unchanged, so `BuiltInSignaturesErrorLogFixturesYieldNothing` stays as it is.

**`internal/syslogline`.** `Clock.ResolveLocal` ports `SyslogClock.ResolveLocal` plus the fraction step of
`ErrorLogParseSession.Resolve`: the integer checks of fact 3 before any `time.Date`, then `instant(year, ts, true)` (the existing
skipped- and repeated-hour rules), the fraction added, the storable check, and `prev` set only on success.

**`internal/errorlogline`** (new package, hand-written, linear, ASCII byte comparisons only). `Parse(format, line, clock)` ports
`ErrorLogLine.TryParse` on bytes; a line of the format whose time `ResolveLocal` accepts returns `syslogline.Line{Header: true, Time,
Priority, Message}` with empty host and program and pid 0; any other line returns `Line{Message: string(line)}`, as `syslogline.Parse`
does for a line without a header. It allocates nothing but the message string.

**`internal/logship`.** `formatOf` maps the five names to a source and a parse function (`syslog` keeps `syslogline.Parse`); `New`
resolves every file's format once and refuses an unknown one; `filesRun.line` calls the file's parse function instead of
`syslogline.Parse` and passes the source to `fileRecord`. **Bookkeeping times (E14, B1):** in `filesRun.line` the record keeps the
line's time, but the file's last time, the run's latest time (`r.st.last`) and the end of a pending lost-rotation gap take
`minTime(at, r.start)`: a line on the same host cannot be written after the run that reads it began, except after a clock step or a
forgery, so the bound only ever makes a gap start earlier or end at the run's start (more span marked unknown, never less). This
also fixes the same defect for RFC 3339 `syslog` lines. Nothing else in the file handling changes; the state file's format is
unchanged and a time saved before the fix is bounded at the next line read.

**`internal/config`.** The format must be one of the five names, compared exactly; the reason is the spec's E1 text.

**README procedure** (Dev, `deploy/agent/README.md`, a subsection *Error logs of Plesk and the web servers* under *What the log
collector reads and what it needs*). Commands, as root, for a configured file `<dir>/<file>`, written from fact 13 and confirmed or changed by the dry run:

```
namei -l <dir>/<file>
setfacl -P -m u:vandox-agent:x <element>
setfacl -P -m u:vandox-agent:r <dir>/<file>
setfacl -P -d -m u::rw,u:vandox-agent:r,g::r,o::- <dir>
sudo -u vandox-agent head -c 0 <dir>/<file>
```

The section says, in this order: `namei -l` first shows every element's owner and mode and any link; a link anywhere in the path is
refused by the agent (`link refused`) and no grant fixes it, so the operator stops there. The second command grants search on each
element `namei` shows denying it to the agent, every ancestor included, not only `<dir>`. The third grants the file. `-P` never follows a
link, and skips one silently with exit 0, so the last command is the check after the grant (no output means readable; `head` follows a
link the agent refuses, which is why `namei` comes first). The default ACL (fourth) is only for a log rotated with `create <mode>`,
where the file's ACL is lost (fact 13): it applies to **every** file and subdirectory later created in that directory and replaces the
umask for them, so its `g::` and `o::` entries are set no wider than the directory's current files (the command shows them for files of
mode `0640`, to be adapted), and with `create 0600` it has no effect at all. The grant widens what the agent, and so a compromise of
it, can read. A rotation, a permission reset or a Plesk repair can drop the grant; the standing `log file not readable`
(`permission denied`) is the sign, and the renamed file's rest is read once the path opens again; without read right on the directory a
restart finds only `<file>.1`. **What is sent:** these logs carry client IP addresses, request lines with query strings, host names
and PHP output that can hold request data, all shipped to the backend as written. Every statement of fact 14 carries "(not verified on
a Plesk server)", and each statement of fact 13 "(measured in an Ubuntu 22.04 container, not on a Plesk server)" after the dry run, else
"(not verified on a Plesk server)".

**Dry run.** Stand-in for the Product Manager, who has no server access: the Dev, in step 6 before the documentation is reviewed, in an
`ubuntu:22.04` container (Docker on the session host; the only software needed is the image and its packages `acl`, `logrotate` and
`sudo`, `util-linux` for `namei`): a directory `0750 root:root` below an ancestor `0750 root:root`, a file `0640 root:root`, a user
`vandox-agent`; the matrix {file ACL only, file ACL plus default ACL} × {`create 0640 root root`, `create 0600 root root`, `create`
without a mode, `copytruncate`}, checking the read after each rotation; a file created with umask 077 in the directory with the default
ACL (its mode and ACL); the ancestor's denied search shown by `namei -l` and fixed by the second command; and a link case (the file
replaced by a link to a `0600` file: `setfacl -P` leaves the target unchanged, the check fails). The Dev reports the results in the
hand-back and writes the README's statements from them; when the container cannot be run (no image, no network), fact 13 stays unverified and the README
uses the first marker. The real-server facts (14) are for the Product Manager's earliest real run.

New user-visible output: the configuration reason (exact text fixed by E1); the README section, laid out as above.

## Options considered

Recorded in record 0121, options 9 to 14: detection by content, the grammars in `internal/syslogline` or with a clock of their own,
raw-line times, hand-written expectations against golden records with or without `log`, file access (decided by the Product Manager),
and the bound of the bookkeeping times.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| agent | `internal/errorlogline/errorlogline.go` (new) | the three grammars |
| agent | `internal/syslogline/clock.go` | `ResolveLocal` |
| agent | `internal/logship/records.go`, `files.go`, `logship.go` | sources, format table, dispatch, `New` check |
| agent | `internal/config/logshipping.go` | the five formats, reason |
| tests | `testdata/logs/*` (new fixtures and golden files) | see *Approach* |
| docs | see *Documentation updates* | |

## Signatures (for the Dev's skeleton)

`internal/syslogline/clock.go`:

- `(*Clock).ResolveLocal(year, month, day, hour, minute, second, nanos int) (time.Time, bool)`, a method with the receiver c — the local time with its year in
  the clock's zone as a UTC instant with the fraction; false for a date that does not exist or an instant outside the storable range; on
  success it becomes the previous instant.

`internal/errorlogline/errorlogline.go` (package `errorlogline`):

- `type Format uint8`
- `const PleskPanel Format = 1`, `const Nginx Format = 2`, `const Apache Format = 3` (one block of iota + 1)
- `func Parse(format Format, line []byte, clock *syslogline.Clock) syslogline.Line`

`internal/logship`:

- `const sourcePleskPanel = "plesk-panel"`, `const sourceSwCpServerError = "sw-cp-server-error"`, `const sourceNginxError = "nginx-error"`,
  `const sourceApacheError = "apache-error"` (`records.go`, beside the syslog source constant)
- `type fileFormat struct` (`files.go`) with the fields source (a string) and parse (a function of the line, the syslog clock
  and the time of reading that returns a syslog line, the shape of syslogline.Parse)
- `func formatOf(name string) (fileFormat, bool)` (`files.go`)
- the struct Logs gains the field formats (a slice of fileFormat), the struct filesRun the field format (a fileFormat)
- `func New(src Sources, cfg Config, store *agentstate.Store, log *slog.Logger) (*Logs, error)` — unchanged signature; returns the
  error "logship: log file <i> has an unknown format" for an unknown format
- `func fileRecord(source, path string, p syslogline.Line, at time.Time, cut bool) (model.Record, *model.LogLine)` (was without the source parameter)
- `func minTime(a, b time.Time) time.Time` (`files.go`): the earlier of two times, for the bookkeeping bound

`internal/config/logshipping.go`:

- `var logFormats = []string{"syslog", "plesk-panel", "sw-cp-server-error", "nginx-error", "apache-error"}` replacing the constant
  syslogFormat; `const logFormatReason = "must be one of syslog, plesk-panel, sw-cp-server-error, nginx-error, apache-error"`

## Test files

`internal/errorlogline/errorlogline_test.go` (new), `internal/syslogline/clock_test.go`, `internal/logship/files_test.go`,
`internal/logship/logship_test.go`, `internal/config/logshipping_test.go`, `deploy/agent/readme_test.go` (new),
`tests/Vandox.Core.Tests/ErrorLogParserTests.cs`, `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs`, and the fixtures of *Approach*.

Existing test code that calls a changed signature: none (`fileRecord` has its only call in `files.go:339`). Existing tests whose input a
criterion handles differently, adapted by the Tester: the format rows of `TestLoadAgent_LogShippingErrors` (new reason) and
`BuiltInSignaturesWebServerErrorLogsYieldPhpAndNginxEvents` (reads the fixtures) and `truncationAfterAFutureLine` (fact 18: the gap now
runs from the reading run's start to the truncation run's start). The other syslog tests date their lines before the run start
(`fileLineTime` 06:00 against `startTime` 07:20) and keep their input.

Files no unit test can cover: none.

## Areas

Agent, Configuration and secrets, Wire format, Log import, Detection, alerts and reports: what changes in each is under
*Documentation updates*.

## Documentation updates

Dev, after the code is green, against it (sweep: `grep -n -i "syslog" docs/areas/*.md docs/ARCHITECTURE.md README.md deploy/agent/*`):

- `docs/areas/agent.md` *Log shipping*: line 1111 and *Configured files* (1178) name the formats; a new paragraph *Error log lines*
  after *Syslog lines* (1201-1208): grammar and mapping by reference to [Log import](../../docs/areas/log-import.md), raw lines with the
  file's last time, the zone of `/etc/localtime` with the year from the line, the repeated-hour rule with the clock carried across a
  rotation, no warning per raw line, `debug` levels and `min_priority`; the differences E4, E6, E11 (backfilled, record 0130), E12 (with
  the measured counts of fact 12) and E13 (in *Rotation* or the residuals); line 1368 loses "the error log formats follow in #289";
  *Rights* (1820-1823) and 1962 point to the README grant; *A file's last time* (1246) and *Gaps of the log collector* state the
  bound of E14 for every file format.
- `docs/areas/configuration-and-secrets.md`: table row 57, the `log_shipping.files` text 193-196 (five formats, `sw-cp-server-error`
  only by configuration), the reason row 218, the record line 348 (0121 now covers the error log files).
- `docs/areas/wire-format.md` 326-329: the agent's sources include the four error log types, `log` the configured path.
- `docs/areas/log-import.md` *Duplicates* 350-353: live error log records and an import of the same period are stored twice.
- `docs/areas/detection-and-reports.md` (the paragraph after the signature table): the web signatures also read the agent's live
  records of these sources.
- `docs/ARCHITECTURE.md` lines 14, 58-59 and 135: the configured error log files and the package `internal/errorlogline`.
- `.squad/stack.md` *Layout* line 39: `errorlogline` in the Go package list.
- `.squad/project.md` area 6 (the log shipping collector's packages; the by-hand grant in `deploy/agent/README.md`, no new right of the
  agent, but the operator's grant widens what it can read) and
  area 10 (the error log lines of `internal/errorlogline`: hand-written, linear on bytes, times through `Clock.ResolveLocal`).
- `README.md` line 80: `format` takes the five values; the error log times carry their year.
- `deploy/agent/agent.yaml` 129-139: the five formats; commented examples for `/var/log/nginx/error.log` (`nginx-error`),
  `/var/log/apache2/error.log` (`apache-error`), `/var/log/plesk/panel.log` (`plesk-panel`), `/var/log/sw-cp-server/error_log`
  (`sw-cp-server-error`) and a per-domain log under `/var/www/vhosts/system/<domain>/logs/`, with the pointer to the README grant.
- `deploy/agent/README.md`: *Configured files* (473-483) names the formats; the new subsection of *Approach*; the manual-check table
  (around 695) gains a row for the readability of the configured error logs.

## Architecture check

No guarantee of `.squad/project.md` changes. *No data gaps unless explicitly recorded*: the new formats use the file handling of #37,
a raw line is shipped, never skipped, and the bookkeeping bound (E14, AC12) keeps a line time the writer controls from moving a gap
past the lines it must cover; it widens a gap at most to the run's start, never narrows one. *A hanging collector never blocks the agent*: parsing is in-memory and linear
within the existing run budgets. *Backfilled data never alerts*: E11 is that rule applied, not a change. `docs/ARCHITECTURE.md` changes
only its enumerations (Dev, above); record 0121 is already linked there.

## Security considerations

- [ ] Limits: the line is cut to 16,384 **raw bytes** before parsing (`cutLine`, unchanged); the grammar's counts (PID 10 digits,
  request ID 64, module 64, fraction 6) are on raw ASCII bytes; the message is made valid UTF-8 and then cut to 16,384 bytes **after**
  decoding (`cleaned`), so a U+FFFD expansion never exceeds the record bound.
- [ ] Exceptions reaching a user: none from parsing (no panic, no error path); `New`'s fixed text `logship: log file <i> has an unknown
  format` behind `agent cannot start`, unreachable after the configuration check; the configuration reason of E1.

## Decision records

- `docs/decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md` (Accepted, unreleased,
  extended in place for #289: context, options 9-13, decision and consequences; index title updated).

## Out of scope / follow-ups

As in [spec.md](spec.md), *Out of scope*. No follow-up issue.

## Challenge

1. Minor, README commands fixed before the dry run. Accepted: they rest on Security's measurement (fact 13), the dry run runs the matrix
   of the objection, the README states the default ACL's reach and recommends it only where the file's ACL is lost, and the README test
   checks the final commands (*Approach*, AC11 row).
2. Minor, search on every ancestor and links. Accepted: `namei -l` first, `x` on each denying element, `link refused` named as needing
   no grant, per-domain modes marked unverified (*Approach*).

## Security review

- B1: resolved by the bookkeeping bound (*Approach*, spec E14 and AC12, record 0121 option 14, chosen over the 24-hour tolerance, which
  would still let a gap start a day ahead); it also fixes RFC 3339 `syslog` lines.
- B2: resolved: `setfacl -P` after `namei -l`, the check after the grant, a link case in the dry run, `-P` required by the README test.
- B3: resolved: the README states the default ACL's reach and umask effect, recommends it only for `create <mode>` with narrow
  `g::`/`o::`, the dry run adds the umask-077 file, area 6 names the widening.
- N1: area 12 added. N2, N3: in the README (*Approach*). N4: no change.
