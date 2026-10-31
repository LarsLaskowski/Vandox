# Plan: Ship the Plesk and web server error logs from the agent

Source: Issue #289 | [spec.md](spec.md)
Status: Draft
Tier: standard — a new parser of external log lines and new values of an existing option in the agent, with no new right, guard or other security control (the Product Manager's option A keeps the agent's rights).
Security areas touched: 6 Agent privileges (`deploy/agent/README.md` gains the by-hand read grant; no right of the unit or the installer changes), 10 Parsing of external input (the new line grammars in `internal/errorlogline`, the new values of `log_shipping.files[].format`)

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
| 7 | The backend accepts any `log_line` source that fits the name pattern: no source list in ingest; only the forensics report's system-log hours list `journal` and `syslog`. | `grep` of `src/Vandox.Backend/Ingest` (no source check); `docs/areas/wire-format.md:93`; `src/Vandox.Core/Reports/ForensicsAnalysis.cs:21` | confirmed |
| 8 | The signature detector matches stored lines by source or program and message, regardless of origin. | `src/Vandox.Core/Signatures/SignatureDetector.cs:132-135` | confirmed |
| 9 | The three samples give no signature event; the signature lines exist only as C# constants. | `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs:806-827` (`BuiltInSignaturesErrorLogFixturesYieldNothing`), `:833-861` | confirmed |
| 10 | The Go test zone `tzCentralEurope()` has the 2026 transitions of Europe/Berlin (2026-03-29 01:00 UTC, 2026-10-25 01:00 UTC), so it matches NodaTime's `Europe/Berlin` for 2026. | `internal/logship/world_test.go:648-660` | confirmed |
| 11 | Golden records of the import read by the Go tests are an existing pattern. | `internal/logship/journal_test.go:25-53`; `tests/Vandox.Core.Tests/JournalExportParserTests.cs:1225-1245` | confirmed |
| 12 | Go replaces each invalid byte with one U+FFFD and .NET's decoder each maximal invalid subsequence, so `\xe2\x82A` gives two and one. | to verify: Dev, step 6, a scratch Go test of `procstats.CleanText` and a scratch C# run of `Utf8Text.Decode` on the bytes, before E12 is written into `docs/areas/agent.md` | unverified |
| 13 | A default ACL on the directory gives a file that logrotate creates (`create 0640` and `create 0600`) a read grant the agent can use, and `acl` provides `setfacl` on Ubuntu 22.04. | to verify: Dev, step 6, stand-in dry run in an `ubuntu:22.04` container (*Approach*, *Dry run*); without it, marked unverified in the README | unverified |
| 14 | The owner, group and mode of the four files and their directories on the server, how Plesk rotates `panel.log` and `sw-cp-server/error_log`, and whether `plesk repair fs` resets ACLs. | to verify: the Product Manager on the server, in the earliest real run; no server access now (escalation answer), so the README marks each statement unverified | unverified |
| 15 | Record 0121 is unreleased (the only tag is `v0.1.0`, whose tree lacks it), so it is extended in place; 0092 is released and stays untouched. | `git ls-remote --tags origin`; `git cat-file -e v0.1.0:docs/decisions/0121-...` fails, `...0092-...` succeeds | confirmed |
| 16 | The unit hides nothing under `/var/log` (`ProtectSystem=strict` is read-only, `InaccessiblePaths=` names only `/etc/shadow` and `/etc/gshadow`). | `deploy/agent/README.md:614`; `deploy/agent/vandox-agent.service` | confirmed |

## Acceptance criteria

The feature's criteria are AC1-AC11 in [spec.md](spec.md).

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestParse_PleskPanelLines`, `TestParse_NginxLines`, `TestParse_ApacheLines` (grammar cases ported from `ErrorLogParserTests`: every level, fraction 1-6 digits for Plesk and exactly 6 for Apache, PID 10/11 digits, request ID 64/65 characters, module 64/65 characters, `[:warn]`, `trace1`/`trace8`/`trace9`, level followed by a space, the end or another character, case) | `internal/errorlogline/errorlogline_test.go` |
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
| AC11 | `TestReadme_ErrorLogGrantIsDocumentedAndMarkedUnverified` (README read through `normalizeLineEnds`: the four paths, the check and the three grant commands of *Approach*, `log file not readable` as the sign of a lost grant, the unverified marker on each statement of fact 14); the Reviewer checks that `docs/areas/agent.md` names E11 and E12 | `deploy/agent/readme_test.go` |

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
`syslogline.Parse` and passes the source to `fileRecord`. Nothing else in the file handling changes, so rotation, positions, gaps,
filters, budgets and the memory bound hold as they are, and the state file is unchanged.

**`internal/config`.** The format must be one of the five names, compared exactly; the reason is the spec's E1 text.

**README procedure** (Dev, `deploy/agent/README.md`, a subsection *Error logs of Plesk and the web servers* under *What the log
collector reads and what it needs*). Commands, as root, for a configured file `<dir>/<file>`:

```
sudo -u vandox-agent head -c 0 <dir>/<file>
setfacl -m u:vandox-agent:x <dir>
setfacl -m u:vandox-agent:r <dir>/<file>
setfacl -d -m u:vandox-agent:r <dir>
```

The first checks the read (no output means readable); the second is needed only when the directory denies search to the agent; the
third grants the current file and the fourth, a default ACL, the files a rotation creates. The section says: a rotation that creates the
file with a mode whose group bits are empty (`create 0600`) leaves the default ACL without effect; a tool that resets permissions can
remove the grant; the standing `log file not readable` (`permission denied`) is the sign, and the renamed file's rest is read once the
path opens again; without read right on the directory a restart finds only `<file>.1`. Every statement of fact 14 carries the marker
"(not verified on a Plesk server)", and each statement of fact 13 either "(measured in an Ubuntu 22.04 container, not on a Plesk
server)" or the first marker.

**Dry run.** Stand-in for the Product Manager, who has no server access: the Dev, in step 6 before the documentation is reviewed, in an
`ubuntu:22.04` container (Docker on the session host; the only software needed is the image and its packages `acl`, `logrotate` and
`sudo`): a directory `0750 root:root` with a file `0640 root:root`, a user `vandox-agent`, the four commands, then logrotate with
`create 0640 root root`, `create 0600 root root` and `copytruncate`, checking the read after each with the first command. The Dev
reports the results in the hand-back; when the container cannot be run (no image, no network), fact 13 stays unverified and the README
uses the first marker. The real-server facts (14) are for the Product Manager's earliest real run.

New user-visible output: the configuration reason (exact text fixed by E1); no other.

## Options considered

1. **The error log grammars in `internal/syslogline`** — no new package and no change to `.squad/stack.md`'s layout; lost because that
   package's name and `Parse` are about syslog headers. Chosen: `internal/errorlogline` with the shared `Clock`.
2. **`errorlogline` with its own clock** — no change to `syslogline`; lost because the skipped- and repeated-hour rules would exist
   twice in Go.
3. **Golden records with `log`** (as the journal's) — one record type in both languages; lost because the agent's `log` is the
   configured path and the import's the listed name, so every Go comparison would have to override it.
4. **Detection by content, raw lines with the file's modification time, hand-written Go expectations, an installer grant** — weighed
   and recorded in record 0121, options 9 to 13 (the last decided by the Product Manager).

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

`internal/config/logshipping.go`:

- `var logFormats = []string{"syslog", "plesk-panel", "sw-cp-server-error", "nginx-error", "apache-error"}` replacing the constant
  syslogFormat; `const logFormatReason = "must be one of syslog, plesk-panel, sw-cp-server-error, nginx-error, apache-error"`

## Test files

`internal/errorlogline/errorlogline_test.go` (new), `internal/syslogline/clock_test.go`, `internal/logship/files_test.go`,
`internal/logship/logship_test.go`, `internal/config/logshipping_test.go`, `deploy/agent/readme_test.go` (new),
`tests/Vandox.Core.Tests/ErrorLogParserTests.cs`, `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs`, and the fixtures of *Approach*.

Existing test code that calls a changed signature: none (`fileRecord` has its only call in `files.go:339`). Existing tests whose input a
criterion handles differently, adapted by the Tester: the format rows of `TestLoadAgent_LogShippingErrors` (new reason) and
`BuiltInSignaturesWebServerErrorLogsYieldPhpAndNginxEvents` (reads the fixtures). The syslog tests of `files_test.go` keep their input.

Files no unit test can cover: none.

## Areas

- Agent: the error log formats in *Log shipping* and the README grant in *Rights*.
- Configuration and secrets: the five formats and the reason.
- Wire format: the agent's `log_line` sources.
- Log import: *Duplicates* names live error log records.
- Detection, alerts and reports: the web signatures read live records too.

## Documentation updates

Dev, after the code is green, against it (sweep: `grep -n -i "syslog" docs/areas/*.md docs/ARCHITECTURE.md README.md deploy/agent/*`):

- `docs/areas/agent.md` *Log shipping*: line 1111 and *Configured files* (1178) name the formats; a new paragraph *Error log lines*
  after *Syslog lines* (1201-1208): grammar and mapping by reference to [Log import](../../docs/areas/log-import.md), raw lines with the
  file's last time, the zone of `/etc/localtime` with the year from the line, the repeated-hour rule with the clock carried across a
  rotation, no warning per raw line, `debug` levels and `min_priority`; the differences E4, E6, E11 (backfilled, record 0130), E12 (with
  the measured counts of fact 12) and E13 (in *Rotation* or the residuals); line 1368 loses "the error log formats follow in #289";
  *Rights* (1820-1823) and 1962 point to the README grant.
- `docs/areas/configuration-and-secrets.md`: table row 57, the `log_shipping.files` text 193-196 (five formats, `sw-cp-server-error`
  only by configuration), the reason row 218, the record line 348 (0121 now covers the error log files).
- `docs/areas/wire-format.md` 326-329: the agent's sources include the four error log types, `log` the configured path.
- `docs/areas/log-import.md` *Duplicates* 350-353: live error log records and an import of the same period are stored twice.
- `docs/areas/detection-and-reports.md` (the paragraph after the signature table): the web signatures also read the agent's live
  records of these sources.
- `docs/ARCHITECTURE.md` lines 14, 58-59 and 135: the configured error log files and the package `internal/errorlogline`.
- `.squad/stack.md` *Layout* line 39: `errorlogline` in the Go package list.
- `.squad/project.md` area 6 (the log shipping collector's packages, the by-hand grant in `deploy/agent/README.md`, no new right) and
  area 10 (the error log lines of `internal/errorlogline`: hand-written, linear on bytes, times through `Clock.ResolveLocal`).
- `README.md` line 80: `format` takes the five values; the error log times carry their year.
- `deploy/agent/agent.yaml` 129-139: the five formats; commented examples for `/var/log/nginx/error.log` (`nginx-error`),
  `/var/log/apache2/error.log` (`apache-error`), `/var/log/plesk/panel.log` (`plesk-panel`), `/var/log/sw-cp-server/error_log`
  (`sw-cp-server-error`) and a per-domain log under `/var/www/vhosts/system/<domain>/logs/`, with the pointer to the README grant.
- `deploy/agent/README.md`: *Configured files* (473-483) names the formats; the new subsection of *Approach*; the manual-check table
  (around 695) gains a row for the readability of the configured error logs.

## Architecture check

No guarantee of `.squad/project.md` changes. *No data gaps unless explicitly recorded*: the new formats use the file handling of #37
unchanged, and a raw line is shipped, never skipped. *A hanging collector never blocks the agent*: parsing is in-memory and linear
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
