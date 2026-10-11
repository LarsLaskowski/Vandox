# Plan: Parser for the legacy top/lsof monitoring log

Source: Issue #20
Status: Draft
Tier: security — a new parser of external input (log files) is *Security area* 10 of `.squad/project.md` (parsing of
external input: no crash, no unbounded allocation, no hang), and it maps untrusted text to IP addresses through a guard
in front of `IPAddress.TryParse`.

## Problem / root cause

Feature. Before the agent exists, a shell script appends a snapshot to a log file (every 5 minutes at first, every
minute now): a `date` header, the output of `top -b -n 1`, and in the new format the sections `meminfo`, `pressure`,
`vmstat` and `lsof`. `vandoxd import` has no parser for it, so the only per-process memory history before the outages
cannot be imported. The model already has the payloads (`ProcessSnapshot`, `ProgramAggregate`, `ConnectionSnapshot`,
`MetricPoint`; `src/Vandox.Core/Model`), and `docs/areas/wire-format.md` (*Producer mapping for connection endpoints*)
already names "the legacy `lsof -ni` parser #20" as a producer. The import framework of #15 is merged.

Claims of the issue, checked:

- *Dependency #15 merged* — confirmed: `vandoxd import`, `ILogParser`, `ParserRegistry`, `BuiltInParsers` exist
  (`src/Vandox.Core/LogParsing`, `src/Vandox.Import`); `ImportCommand.cs:55` registers `BuiltInParsers.Create(...)`.
- *"Same model the agent will use"* — confirmed for the payloads (Go `internal/model/process.go`, `connection.go`,
  `metric.go` mirror the C# types). Refuted for metric names: no metric name is defined anywhere yet (the agent
  collectors #31 to #33 are open). This plan defines them (record 0093, option 15).
- *Header format `===== Fri Oct  2 09:15:01 CEST 2026 =====`, CEST/CET* — format taken from the issue; Oct 2, 2026 is a
  Friday (consistent). That NodaTime's TZDB names the `Europe/Berlin` intervals `CEST` (+02) and `CET` (+01), that
  `MapLocal` of 2026-10-25 02:30 has two mappings (early `CEST`, late `CET`) and of 2026-03-29 02:30 none — **confirmed**
  by a scratch run against NodaTime 3.3.5 (the version in `Directory.Packages.props`); `Etc/UTC` is named `UTC`,
  `Asia/Istanbul` `+03`.
- *top summary lines, process table columns, RES suffixes `m`/`g`, truncated names with `+`, `-w 512 -c` in the new
  format* — taken from the issue. Not checkable here: the sample is not available (it holds real addresses). The
  suffix set `k m g t p e` and the scaling (no suffix = KiB) follow procps-ng 3.3.x `scale_mem`, **unverified** against
  the sample; that a `-c` command line cut by `-w 512` ends in `+` is **unverified**; that the number of table rows
  equals `Tasks: N total` is **unverified** (so it only sets `complete`, never skips a snapshot).
- *pressure lines `memory: some avg10=… full avg10=…` or `n/a`* — the exact line shape is **unverified**; the plan
  reads the issue's one-line form and the two-line form `echo "memory: $(cat /proc/pressure/memory)"` produces.
- *`lsof -ni` output with the state in parentheses, `\x20` escapes* — taken from the issue; ports are service names
  (no `-P`), as the wire format area already states.
- *356 snapshots, 8 MB sample must not be committed* — confirmed as a constraint; AC9 checks the fixtures.

Related defect found on the way: none in the code. `docs/areas/wire-format.md` says an unmapped socket is left out
"with a warning"; in the importer a *warning* means a line kept as a raw record (`IRecordEmitter.Warn`, counted as
"lines kept raw"), so for the import it is a **skip**. The wording is corrected (see *Documentation updates*).

`.NET`'s `IPAddress.TryParse` accepts far more than `lsof` prints — **confirmed** by a scratch run: `127.1` → 127.0.0.1,
`1` → 0.0.0.1, `0x7f.0.0.1` → 127.0.0.1, `010.0.0.1` → 8.0.0.1 (octal), `2130706433` → 127.0.0.1, `[::1]` and
`[::1]:80` → ::1, `fe80::1%eth0` → a scope ID looked up **on the host** (4 here), `FE80::1`, `::ffff:1.2.3.4`; it refuses
`1.2.3.256`, `1.2.3.4.5`, surrounding spaces. Hence the guard in *Approach*, step 6.

## Acceptance criteria

Every test lives in the file named under *Test files*; names are `{Class}{Scenario}{ExpectedResult}`.

- [ ] **AC1 Registration and detection.** `BuiltInParsers.Create` returns the eight parsers as before, then
  `LegacyMonitorParser` (type `legacy-monitor`, `LineFramed` true); the registry accepts it. `Detect` returns
  `MatchContent` when the file's name is not a syslog name and the first line of the head (a UTF-8 BOM and a `\r`
  before `\n` removed) is a valid header; otherwise `NoMatch` (an empty head, a first line that is not a header, a header
  on line 2, a syslog name with a header). No other built-in parser claims a legacy head, and the legacy parser claims
  none of the existing heads.
  Tests: `BuiltInParsersTests.BuiltInParsersCreateReturnsSystemParsersThenErrorLogParsers` and
  `BuiltInParsersCreateListIsAcceptedByTheRegistry` (adapted to nine types), new DataRows in
  `BuiltInParsersRegistryDetectsFilesByContentAndName` (`monitor.log` and `logs/top.log` with the legacy head →
  `legacy-monitor`, `MatchContent`), `BuiltInParsersTests.BuiltInParsersOtherParsersIgnoreLegacyMonitorHead`,
  `LegacyMonitorParserTests.LegacyMonitorParserDetectClaimsHeaderFirstLine`,
  `LegacyMonitorParserDetectRefusesOtherHeads` (DataRows: empty, header on line 2, syslog line, journal, MariaDB,
  Dovecot, nginx, Apache, Plesk panel heads, `syslog` and `kern.log.1` with a legacy head).
- [ ] **AC2 Header grammar.** `LegacyMonitorHeader.TryParse` accepts exactly `===== ` + weekday (`Mon`..`Sun`) + ` ` +
  month (`Jan`..`Dec`) + ` ` + a two-character day (space or digit, then digit) + ` ` + `HH:MM:SS` (two digits each) +
  ` ` + a zone of 1 to 16 characters of `A-Z a-z 0-9 + -` + ` ` + four-digit year + ` =====`, ASCII and case-sensitive,
  nothing before or after; a header line (`IsHeaderLine`) is any line beginning with `===== `.
  Tests: `LegacyMonitorHeaderTests.LegacyMonitorHeaderTryParseReadsDateOutput` (DataRows: `Fri Oct  2 09:15:01 CEST
  2026`, `Sun Oct 25 02:30:00 CET 2026`, `Mon Jan 05 00:00:00 UTC 2026`, zone `+03`),
  `LegacyMonitorHeaderTryParseRefusesOtherText` (DataRows: lower-case month, `Oct 2` with one space, three-digit day,
  `9:15:01`, missing year, zone of 17 characters, zone with `_`, trailing space, missing ` =====`, German
  `Fr 2. Okt`, full-width digits `２０２６`), `LegacyMonitorHeaderIsHeaderLineMatchesPrefixOnly`.
- [ ] **AC3 Daylight saving change.** A header resolves in `import.time_zone` with the offset of the zone interval
  whose name equals the header's zone (ordinal): `Sun Oct 25 02:30:00 CEST 2026` → 2026-10-25T00:30:00Z,
  `Sun Oct 25 02:30:00 CET 2026` → 01:30:00Z, `Sun Mar 29 01:59:00 CET 2026` → 00:59:00Z, `Sun Mar 29 03:00:00 CEST
  2026` → 01:00:00Z; a local time in the skipped hour (`Sun Mar 29 02:30:00 CET 2026`), month/day/hour/minute/second
  out of range (`Feb 30`, `24:00:00`, `:60`) → "invalid date"; a year outside 1677..2262 or an instant outside
  `StorableTime` → "time outside the storable range" (checked as integers before any date is built); a zone name that is
  not a name of an interval mapping that local time → `LegacyMonitorHeader.ZoneMismatch`. When both mappings carry the
  zone's name, the earlier offset is taken.
  Tests: `LegacyMonitorHeaderTests.LegacyMonitorHeaderResolveUsesOffsetOfNamedInterval` (the four instants above),
  `LegacyMonitorHeaderResolveRefusesInvalidDates`, `LegacyMonitorHeaderResolveRefusesOutsideStorableRange` (years
  `0001`, `1677` January, `9999` December with a negative offset zone — no exception),
  `LegacyMonitorHeaderResolveReportsZoneMismatch` (`CEST` in January, `EST` in `Europe/Berlin`),
  `LegacyMonitorParserTests.LegacyMonitorParserParseOrdersSnapshotsAcrossDaylightSavingEnd` (fixture across the
  repeated hour: every snapshot's `captured_at` strictly increases).
- [ ] **AC4 Time zone option.** With `import.time_zone` unset the first valid header throws
  `InvalidOperationException` with "import.time_zone is not set" (`SyslogParser.TimeZoneNotSet`); a file without a valid
  header does not throw. A zone mismatch before any header of this parse resolved throws `InvalidOperationException`
  with exactly "the time zone of the first snapshot header does not match import.time_zone"; a mismatch after a header
  resolved skips that snapshot with reason "time zone does not match import.time_zone" at the header's line.
  Tests: `LegacyMonitorParserTests.LegacyMonitorParserParseThrowsWithoutTimeZone`,
  `LegacyMonitorParserParseWithoutHeaderDoesNotNeedTimeZone`,
  `LegacyMonitorParserParseThrowsWhenFirstZoneDoesNotMatch`,
  `LegacyMonitorParserParseSkipsLaterSnapshotWithOtherZone`.
- [ ] **AC5 Summary lines → metrics.** For a snapshot whose top part is complete, records of kind `metric`, source
  `legacy-monitor`, origin `import`, sequence 0, `captured_at` the header instant, in this order: `load1`, `load5`,
  `load15` (no unit); `cpu_percent` (unit `percent`, label `mode` = `user`, `system`, `nice`, `idle`, `iowait`, `irq`,
  `softirq`, `steal` from `us sy ni id wa hi si st`); `mem_total_bytes`, `mem_free_bytes`, `mem_used_bytes`,
  `mem_buff_cache_bytes`, `mem_available_bytes`, `swap_total_bytes`, `swap_free_bytes`, `swap_used_bytes` (unit
  `bytes`; the value times 1024^n for `KiB`=1 .. `EiB`=6, rounded half away from zero; `mem_available_bytes` from `avail
  Mem` of the swap line). The memory and swap pairs are read by label in any order, each label exactly once.
  **Tokenizing:** the text after the line's label (`load average:`, `Tasks:`, `%Cpu(s):`, `<unit> Mem :`, `<unit> Swap:`)
  is split at `,` (and, in the swap line, at the `.` directly after the label `used`); each piece, with leading and
  trailing spaces removed, is `NUMBER`, one or more spaces, `LABEL` — so a piece needs no space after the comma or the
  colon (`0.0 us,100.0 id`, `KiB Mem :131926528 total,` where the number fills `top`'s 9-character column).
  **Numbers** (summary, %CPU of rows, pressure averages): an integer part of 1 to 15 ASCII digits and an optional `.`
  with 1 to 6 ASCII digits — nothing else (no sign, exponent, comma); every value read is therefore finite and below
  10^15. A memory or swap value times 1024^n is computed exactly and rounded half away from zero; a result above
  `ulong.MaxValue` makes the line unreadable (a summary line that is not read makes the snapshot "malformed snapshot",
  AC8). **Scaling order** (shared with RES, AC6, in `LegacyTopLine.TryScaleBinary`; no `decimal`, no `double`, no
  intermediate type can overflow): (1) the integer digits I are converted to `UInt128` (at most 20 digits, so below
  2^67); (2) if I > `ulong.MaxValue >> (10*n)` the value is refused before any multiplication; (3) otherwise, with d
  fraction digits F, `UInt128` r = ((I×10^d + F) << (10*n) + 10^d / 2) / 10^d — bounded by (2^64 + 2^60)×10^6 < 2^85,
  far below `UInt128.MaxValue`; (4) r > `ulong.MaxValue` is refused (I passes step 2 but the fraction or the rounding
  carries it over), else r is the byte count.
  Tests: `LegacyTopLineTests.LegacyTopLineTryParseLoadReadsThreeValues`, `LegacyTopLineTryParseTasksReadsTotal`,
  `LegacyTopLineTryParseCpuReadsEightModes` (DataRows: the issue's line, `0.0 us,100.0 id` without a space after the
  comma), `LegacyTopLineTryParseMemoryReadsUnitsAndLabels` (DataRows `KiB Mem :`, `MiB Mem :`, `GiB Mem :`, order
  `total, used, free, buff/cache`, `KiB Mem :131926528 total,  1234567 free,...` with no space after the colon and the
  comma), `LegacyTopLineTryParseSwapReadsAvailMem`,
  `LegacyTopLineTryParseRefusesMalformedSummaryLines` (DataRows: missing label, label twice, `NaN`, `1e3`, `1,5`
  decimal comma, `٣` Arabic-Indic digit, a ninth CPU pair, a load value of 16 integer digits, a fraction of 7 digits,
  `-0.1`, `EiB Mem : 16.0 total` and `GiB Mem : 999999999999999.0 total` (above `ulong.MaxValue` bytes), and at the
  maximum digit bounds `EiB Mem : 999999999999999.999999 total` and `PiB Mem : 999999999999999.999999 total` (refused
  by the order of step 2 without an exception; with `decimal` they would throw `OverflowException`)),
  `LegacyMonitorParserTests.LegacyMonitorParserParseEmitsSummaryMetricsInOrder`.
- [ ] **AC6 Process table → process snapshot, RES suffixes, truncated names.** A row is `PID USER PR NI VIRT RES SHR S
  %CPU %MEM TIME+ COMMAND` (tokens separated by spaces; COMMAND is the rest after the eleventh token and its spaces).
  `rss_bytes` from RES: digits (1 to 20) with an optional fraction (1 to 3 digits) and an optional suffix `k`/none
  (×1024), `m` (×1024²), `g`, `t`, `p`, `e` (×1024³..×1024⁶), exact integer arithmetic in AC5's scaling order
  (`TryScaleBinary`: integer part checked against `ulong.MaxValue >> (10*n)` before multiplying, then `UInt128`),
  rounded half away from zero, a result above 64 bits is no value and never an exception. `cpu_percent` from %CPU, `user` from USER, `state` from S (one ASCII letter),
  `pid` from PID (1..2,147,483,647). A COMMAND ending in `+` is truncated: the `+` is removed and `truncated` set. Old
  format (no section in the snapshot): `command` is the normalized COMMAND (cut to 1,024 bytes), `cmdline` empty. New
  format (at least one section marker in the snapshot): `cmdline` is the normalized COMMAND cut to 2,048 UTF-8 bytes
  (cut sets `truncated`), `command` is the text inside `[...]` for a bracketed COMMAND, else the part after the last `/`
  of the first space-separated word with one trailing `:` removed, cut to 1,024 bytes. **Never empty** (the model
  refuses an empty `command` and `program`, `ProcessSample.cs:121`, `ProgramAggregate.cs:61`, which would refuse the
  whole snapshot record): when the derived name is empty, `command` is the normalized COMMAND; when that is empty too,
  the COMMAND as printed (a non-empty token, e.g. `+`), each cut to 1,024 bytes. The program name (AC7) is likewise the
  command itself when its first word is empty. `total` = `Tasks: N total`;
  `complete` true only when the row count equals `total`, no row was left out (AC8, AC12) and the table was not ended
  by the end of input (AC8). A table with no row emits no process snapshot.
  Tests: `LegacyTopLineTests.LegacyTopLineTryParseMemoryValueConvertsSuffixes` (DataRows: `0`→0, `1234`→1,263,616,
  `123.4m`→129,394,278, `1.234g`→1,324,997,411, `2.000t`→2,199,023,255,552, `1.5p`→1,688,849,860,263,936,
  `1.000e`→1,152,921,504,606,846,976, `0.001m`→1,049 (1,048.576 rounded)),
  `LegacyTopLineTryParseMemoryValueRefusesOtherForms` (`?`, `1.2M`, `1.2345g`, `-1`, 21 digits, `1e3`, `1.`, `.5`,
  `16.000e` overflow, and at the maximum digit bounds `99999999999999999999.999e` and `99999999999999999999.999p`),
  `15.999e`→18,445,591,152,204,944,769 is a further DataRow of `...ConvertsSuffixes` (the largest `e` value with a
  fraction below the bound; I = 15 = `ulong.MaxValue >> 60`), `LegacyTopLineTryParseRowReadsColumns` (PR `rt`, NI `-20`, COMMAND with spaces),
  `LegacyTopLineTryParseRowRefusesMalformedRows`, `LegacyProcessNamesTests.LegacyProcessNamesNormalizeRemovesTrailingPlus`,
  `LegacyProcessNamesCommandOfDerivesNameFromCommandLine` (DataRows: `/usr/sbin/apache2 -k start`→`apache2`,
  `php-fpm: pool www`→`php-fpm`, `[kworker/0:1-events]`→`kworker/0:1-events`, `spamd child`→`spamd`),
  `LegacyProcessNamesCommandOfNeverReturnsEmpty` (DataRows, command line and column: `/opt/x/`→`/opt/x/`, `:`→`:`,
  `[]`→`[]`, `/`→`/`, column `+` with an empty command line→`+`),
  `LegacyProcessNamesOldCommandOfNeverReturnsEmpty` (DataRows: `+`→`+` truncated, `sw-engi+`→`sw-engi` truncated),
  `LegacyProcessNamesProgramOfNeverReturnsEmpty` (DataRows: ` x`→` x`, `apache2 -k`→`apache2`),
  `LegacyMonitorParserTests.LegacyMonitorParserParseReadsOldFormatProcessSnapshot`,
  `LegacyMonitorParserParseReadsNewFormatProcessSnapshot`, `LegacyMonitorParserParseMarksSnapshotIncompleteWhenRowsMissing`,
  `LegacyMonitorParserParseKeepsSnapshotWithEmptyDerivedNames` (old-format COMMAND `+`, new-format COMMANDs `/opt/x/`,
  `:`, `[]`: the process snapshot record is emitted and passes `RecordingEmitter`'s validation).
- [ ] **AC7 Per-program aggregates.** Each process snapshot carries `programs`: one entry per program name (the first
  space-separated word of the process's `command`), in order of first appearance, with `count`, the sums of
  `cpu_percent` and `rss_bytes`, no `pss_bytes`, and `rss_overcounted` true exactly when `count` > 1. The `rss_bytes`
  sum saturates at `ulong.MaxValue` (it never wraps; the project compiles unchecked, so a plain `+=` would wrap).
  Further test: `LegacyMonitorParserTests.LegacyMonitorParserParseSaturatesProgramRssSum` (two `apache2` rows with RES
  `15.999e` → `rss_bytes` = 18,446,744,073,709,551,615).
  Tests: `LegacyProcessNamesTests.LegacyProcessNamesProgramOfTakesFirstWord`,
  `LegacyMonitorParserTests.LegacyMonitorParserParseAggregatesPerProgram` (three `apache2` rows, one `spamd` and one
  `spamd child` row → `apache2` count 3 overcounted, `spamd` count 2 overcounted, a single program not overcounted).
- [ ] **AC8 Robustness: truncated and interleaved snapshots.** A snapshot is skipped whole, with one skip at its
  header's line number and no record, when: the header line is no valid header ("invalid snapshot header"); the
  snapshot ends (next header line or end of input) before its table header ("truncated snapshot"); a non-empty line
  before the table header is not the next expected summary line (order `top -`, `Tasks:`, `%Cpu(s):`, `Mem`, `Swap`,
  then the table header), a summary line is not read (AC5), a section marker comes before the table header, or a line
  after the table header begins like a summary line (`top - `, `Tasks:`, `%Cpu(s):`, `<unit> Mem `, `<unit> Swap:`) or
  is the table header again ("malformed snapshot" — the signs of another run's output mixed in). A **single bad row**
  does not skip the snapshot: a row that does not parse is left out with the skip "process row not read", a row whose
  PID an earlier row of the snapshot has is left out with "duplicate process id", both at the row's line, and the
  process snapshot gets `complete` false. The top part is **accepted** at the first section marker after the table
  header, or at the end of the snapshot; only then are its records emitted and its row skips reported (step 5). A
  snapshot **closed by the end of input** instead of a following header line may be cut (the log is copied while it
  grows): an `lsof` section still open then emits its connection snapshot with `complete` false, and a process table
  still open (old format) its process snapshot with `complete` false; meminfo, pressure and vmstat metrics of a cut
  section are emitted as read. The last snapshot of a whole file is therefore always marked so — the log has no end
  marker. Lines before the first header line are skipped one by one as "line before the first snapshot" (so the rest
  of a cut snapshot at the start of a continued parse is lost, *Areas*). The parse never throws for input (only for the
  time zone, AC4, cancellation and the emitter's errors), and the snapshots before and after a skipped one are emitted.
  Tests: `LegacyMonitorParserTests.LegacyMonitorParserParseSkipsTruncatedLastSnapshot` (file cut inside the table
  header line and inside the summary),
  `LegacyMonitorParserParseSkipsInterleavedSnapshot` (a `top - ` line and a table header among the rows of B: B is
  skipped whole, A's and C's records are emitted),
  `LegacyMonitorParserParseSkipsSnapshotWithInvalidHeader`, `LegacyMonitorParserParseDropsDuplicatePidRow` (second row
  left out with "duplicate process id", the first kept, `complete` false),
  `LegacyMonitorParserParseDropsUnparsableRow` (one row with a non-numeric PID among three: two rows kept, one skip
  "process row not read" at that line, `complete` false, the summary metrics and sections emitted),
  `LegacyMonitorParserParseMarksSnapshotCutAtEndOfInputIncomplete` (DataRows: new format cut inside `lsof` → connection
  snapshot `complete` false; new format cut inside `vmstat` after a closed `lsof` → that connection snapshot `complete`
  true; old format cut inside the rows → process snapshot `complete` false; the same file with one more header line
  appended → `complete` true), `LegacyMonitorParserParseSkipsRestOfCutSnapshotAtContinuation` (the lines after the cut,
  parsed alone, give one "line before the first snapshot" skip each and the records of the next snapshot),
  `LegacyMonitorParserParseSkipsLinesBeforeFirstSnapshot`, `LegacyMonitorParserParseSurvivesHostileInput`
  (DataRows: 20,000 bytes without line feed, a header followed by 10,000 `top - ` lines, invalid UTF-8 in every field,
  a row of 16 KiB, 100,000 `lsof` lines with no header line before them — no exception; with `CountingEmitter` the
  skips never exceed the input's line count and a snapshot's skips never exceed its caps (AC12) plus its marker and
  cap skips).
- [ ] **AC9 New-format sections.** `meminfo`: `Name: N kB` → `meminfo_bytes` (unit `bytes`, N×1024, label `field` =
  Name), `Name: N` → `meminfo` (no unit, label `field`); Name 1 to 64 characters of `A-Z a-z 0-9 _ ( )`; N is 1 to 20
  ASCII digits within `ulong` (converted with `ulong.TryParse`, never a throwing parse), and for `kB` N >
  `ulong.MaxValue >> 10` makes the line unreadable, checked before N is shifted by 10, so N×1024 is never computed
  for a value that does not fit; `TryParseMeminfo`'s `value` is the byte count for `kB`, else N. Pressure `total=N` and
  vmstat `N` are 1 to 20 ASCII digits within `ulong`; averages follow AC5's number rule. `pressure`: a
  line `<res>: some avg10=F avg60=F avg300=F total=N[ full avg10=F avg60=F avg300=F total=N]` with res `memory`, `cpu`
  or `io`, or a line `full avg10=F avg60=F avg300=F total=N` directly after a resource line that had only `some` →
  `pressure_avg10`, `pressure_avg60`, `pressure_avg300` (unit `percent`) and `pressure_stall_microseconds_total` (unit
  `microseconds`), labels `resource` and `kind`; a line `n/a` or `<res>: n/a` gives no metric and no skip. `vmstat`: a
  line `<counter> N` for `pswpin`, `pswpout`, `pgmajfault`, `oom_kill` → `vmstat_total` (no unit, label `counter`) and,
  when the previous accepted snapshot of this parse had the counter at an earlier instant and a value not above N,
  `vmstat_per_second` (unit `per_second`, label `counter`) = (N − previous) / seconds between the two headers; other
  well-formed `<name> N` lines are ignored. The rate **depends on where a parse starts**: the first snapshot of a parse
  has no rate, and a continued import starts with an empty history, so its first snapshot has none either (a gap of one
  rate per continuation; the raw `vmstat_total` is complete, and a rate can be derived from it later; area
  limitation). An unreadable line of these sections is skipped with "meminfo line not
  read", "pressure line not read" or "vmstat line not read" and the rest of the section is read; a repeated meminfo
  field or pressure resource and kind is skipped the same way; an unknown section `----- <name> -----` is skipped with one
  skip "unknown section" at its marker, a known section that appears a second time in the snapshot with one skip
  "repeated section"; the lines of both are not read and give no skip. Empty lines in sections are ignored. Section
  metrics are emitted as their line is read (the top part is accepted by then), skips reported at once.
  Tests: `LegacyProcLineTests.LegacyProcLineTryParseMeminfoReadsKilobytesAndCounts`,
  `LegacyProcLineTryParseMeminfoRefusesOtherForms` (DataRows include 21 digits and `MemTotal: 18014398509481984 kB`,
  whose bytes are 2^64; `18014398509481983 kB` is read in `...ReadsKilobytesAndCounts`),
  `LegacyProcLineTryParsePressureReadsOneAndTwoLineForms`,
  `LegacyProcLineTryParsePressureAcceptsNotAvailable`, `LegacyProcLineTryParsePressureRefusesOtherForms`,
  `LegacyProcLineTryParseVmstatReadsCounters`, `LegacyMonitorParserTests.LegacyMonitorParserParseEmitsSectionMetrics`,
  `LegacyMonitorParserParseConvertsVmstatCountersToRates` (three snapshots one minute apart; a counter that falls, as
  after a reboot, gives no rate for that snapshot; a skipped snapshot is not a predecessor; the first snapshot has no
  rate, and parsing the file from its second header line on gives no rate for that snapshot either — the start
  dependence pinned), `LegacyMonitorParserParseSkipsRepeatedSection`.
- [ ] **AC10 lsof → connection snapshot.** See *Approach*, step 6 for the line grammar and the address guard. Per
  snapshot one `connection_snapshot`: `states` (per protocol and state over unique sockets, ordinal order by protocol then
  state), `processes` (per PID and decoded command over every socket line, order of first appearance), `remotes` (per
  remote address over unique connected sockets, by count descending then address text ordinal), `listeners` (unique
  sockets without a remote endpoint), `connections` (unique sockets with one); a socket is unique by the text of its
  line from the TYPE field on, and its owner is that of its first line. `complete` is false when a line was left out
  (unread, unmapped, too long or beyond the cap) or the section was closed by the end of input (AC8). The connection
  snapshot is emitted when the section ends (next section marker, next header line or end of input).
  An `lsof` section without lines emits no record; one with only the header emits an empty, complete snapshot. Unmapped
  lines are skipped with "lsof line not read" (shape) or "socket address not mapped" (address, port or state).
  Tests: `LegacyLsofLineTests.LegacyLsofLineTryParseReadsTcpAndUdp`, `LegacyLsofLineTryParseMapsWildcardsAndServiceNames`
  (`*:ssh` IPv4 → 0.0.0.0:22, `*:https` IPv6 → [::]:443, `*:*` → port 0, `127.0.0.53:domain` UDP → 53),
  `LegacyLsofLineTryParseReadsIpv6Brackets` (`[2001:db8::1]:https->[2001:db8::2]:51234`, `[fe80::1%eth0]:22` → zone
  stripped, scope 0), `LegacyLsofLineTryParseDecodesCommandEscapes` (`php-fpm\x20` → `php-fpm `),
  `LegacyLsofLineTryParseRefusesLenientAddressForms` (DataRows, each must be refused — they are what
  `IPAddress.TryParse` would accept: `127.1:22`, `1:22`, `0x7f.0.0.1:22`, `010.0.0.1:22`, `2130706433:22`,
  `1.2.3.256:22`, `[1.2.3.4]:22`, `[::1]:80` as IPv4 TYPE, `1.2.3.4:22` as IPv6 TYPE, `[::1]:22]`, `[ ::1]:22`,
  port `65536`, port `0x16`, unknown service `nosuchservice`, TCP without state, TCP state `UNKNOWN`,
  `[::ffff:1.2.3.04]:22`, `[::1.2.3.077]:22`, `[::ffff:1.2.3.256]:22`, `[::ffff:1.2.3]:22`),
  `LegacyLsofLineTryParseReadsIpv6WithEmbeddedIpv4` (`[::ffff:192.0.2.1]:https` → ::ffff:192.0.2.1),
  `LegacyLsofLineIsHeaderMatchesColumnNames`, `LegacyServiceNamesTests.LegacyServiceNamesTryGetPortMapsUbuntuNames`
  (`ssh`/tcp 22, `domain`/udp 53, `imap2`/tcp 143, `submissions`/tcp 465, `mysql`/tcp 3306, `fsp`/udp 21 but not tcp),
  `LegacyServiceNamesTryGetPortRefusesUnknownNames`, `LegacyMonitorParserTests.LegacyMonitorParserParseBuildsConnectionSnapshot`,
  `LegacyMonitorParserParseCountsSharedSocketOnce`.
- [ ] **AC11 Same model, emission order and determinism.** Every record of both fixtures passes `RecordingEmitter`'s
  validation (the importer's `RecordRules.CheckImportRecord` rules), and the records of a snapshot are emitted in the
  order: summary metrics, process snapshot, then per section in file order (meminfo, pressure and vmstat metrics in
  line order, the connection snapshot). Parsing the same content twice gives the same records; a parse cancelled or
  stopped by an emitter error emits a prefix of the complete parse (records of the open snapshot's accepted top part
  and read section lines may be part of that prefix; a record is never emitted that the complete parse would not
  emit at that position).
  Tests: `LegacyMonitorParserTests.LegacyMonitorParserParseFixturesEmitValidRecords` (DataRows: both fixtures),
  `LegacyMonitorParserParseIsDeterministic`, `LegacyMonitorParserParseEmitsPrefixWhenEmitterFails`,
  `LegacyMonitorParserParseHonorsCancellation`.
- [ ] **AC12 Bounds.** Every cap counts **every non-empty line** of its part, read or not (parsed, unparsable,
  duplicate, unmapped, too long, an `lsof` header line); at the first line beyond a cap one skip is reported, and every
  later line of that part is dropped without a skip and without being examined. A snapshot reads at most 4,096 row
  lines (skip "too many processes in a snapshot" at the 4,097th, `complete` false, aggregates over the kept rows), at
  most 4,096 `lsof` lines (skip "too many sockets in a snapshot", `complete` false), and per section at most 128
  meminfo, 16 pressure and 64 vmstat lines (skip "too many lines in a section"). An `lsof` line longer than 1,024 bytes
  (counted on the decoded text, an invalid byte counting three) is "lsof line not read"; row tokens before COMMAND longer than 64 bytes make the row
  unparsable. Skips are never collected beyond these caps: the only skips held back are the row skips of a top part
  not yet accepted (at most 4,097: 4,096 row skips and the cap skip, step 5); every section skip is reported at once. Memory does not grow with the
  number of snapshots or with the number of lines in a snapshot.
  Tests: `LegacyMonitorParserTests.LegacyMonitorParserParseCapsProcessRows` (4,097 unparsable rows count to the cap
  too), `LegacyMonitorParserParseCapsSocketLines` (unmapped and too long lines count, lines after the cap give no
  skip), `LegacyMonitorParserParseCapsSectionLines` (DataRows: meminfo 129, pressure 17, vmstat 65 lines),
  `LegacyMonitorParserParseRetainsBoundedMemory` (retained heap measured with the S1215 helper pattern of
  `KernelReportGrouperTests.RetainedBytes` at the last record of 2,000 generated snapshots minus at the last record of
  20 stays below 1 MiB, with `CountingEmitter`),
  `LegacyMonitorParserParseRetainsBoundedMemoryForSectionLines` (one snapshot with 200,000 malformed `vmstat` lines and
  200,000 unmapped `lsof` lines minus the same with 2,000 each, measured the same way when `CountingEmitter.Observed`
  sees that snapshot's connection snapshot: below 1 MiB; and `Skips` equals the cap skips plus at most 64 + 4,096 line
  skips).
- [ ] **AC13 Anonymized fixtures of both formats; the real file is not in the repository.** `testdata/logs/legacy-monitor-old.log`
  (old format, at least three snapshots five minutes apart, truncated names) and `testdata/logs/legacy-monitor-new.log`
  (new format with all four sections, across the repeated hour of 2026-10-25 in `Europe/Berlin`, one snapshot cut at the
  end) are each smaller than 64 KiB, and every address in their connection snapshots is in 192.0.2.0/24,
  198.51.100.0/24, 203.0.113.0/24, 2001:db8::/32, 127.0.0.0/8, ::1, 0.0.0.0 or ::.
  Tests: `LegacyMonitorParserTests.LegacyMonitorParserFixturesAreSmallAndUseDocumentationAddresses`, plus AC11's fixture test.

## Approach

All new code is in `src/Vandox.Core/LogParsing`; the importer, storage and model do not change.

1. **Reading.** `LegacyMonitorParser.ParseAsync` reads with `LogLineReader` (lines cut at 16 KiB), removes one trailing
   `\r` and, on line 1 only, a UTF-8 BOM. A line beginning with the bytes `===== ` is a header line: it finishes the
   open snapshot (emit or skip, step 5) and opens the next. Before the first header line every line is skipped as "line
   before the first snapshot". Lines are decoded with `Utf8Text.Decode` (invalid bytes → U+FFFD); every grammar below is
   checked on ASCII characters with ordinal comparisons, and numbers are validated as ASCII digit strings before any
   conversion (never `char.IsDigit`, never `double.Parse` on unchecked text; conversion with
   `CultureInfo.InvariantCulture` and `NumberStyles.AllowDecimalPoint` only), with the digit bounds of AC5, AC6 and
   AC9, so no conversion can give an infinite value or overflow unchecked. Byte scaling (memory, swap, RES, meminfo
   `kB`) compares the integer part against `ulong.MaxValue >> (10*n)` before it multiplies and then computes in
   `UInt128` (AC5 *Scaling order*); `decimal` is not used for it, since its range (about 7.9×10^28) is smaller than
   the largest input times 1024^6.
2. **Header and time.** `LegacyMonitorHeader.TryParse` (AC2); `Resolve` (AC3): year range checked as an integer first,
   then day against `SyslogTime.DaysInMonth`, then `LocalDateTime` → `zone.MapLocal`; candidates are `EarlyInterval`
   (count ≥ 1) and `LateInterval` (count 2); the first candidate whose `Name` equals the zone text wins; count 0 →
   "invalid date"; result checked with `StorableTime.Contains`. The parser keeps one flag "a header of this parse
   resolved" for AC4's mismatch rule. An invalid header or an unresolvable time opens a *skipped* snapshot: its lines are
   consumed without further skips.
3. **Top part.** `LegacyMonitorSnapshot` is a state machine over the snapshot's lines: expected summary line 1..5,
   table header, rows; empty lines are ignored throughout. A line `----- <name> -----` (name 1 to 32 lower-case ASCII
   letters) is a section marker and ends the top part; known names are `meminfo`, `pressure`, `vmstat`, `lsof`. The
   snapshot is new-format when any marker (known or not) occurs. Row parsing by `LegacyTopLine.TryParseRow`; command
   normalization by `LegacyProcessNames` (AC6, AC7). An unparsable or duplicate-PID row is left out and its skip held
   back (line number and fixed reason; at most 4,097, because every row line counts toward the row cap and the cap skip is held with them); the PID set
   holds at most 4,096 entries.
4. **Sections.** `LegacyProcLine` reads meminfo, pressure and vmstat lines (AC9); the snapshot keeps the resource of the
   last pressure resource line for a following `full` line. A vmstat value updates the parser's counter history (one
   `LegacyCounter` per counter name, four at most) as its line is read, and its records are handed out at once.
5. **Acceptance and finishing.** The top part is accepted at the first section marker after the table header, or when
   the snapshot ends after the table header. At acceptance the snapshot hands out its summary metrics and its process
   snapshot and reports its held-back row skips; it holds no skip after that: every section line skip is reported to
   the emitter when the line is read, and every section record is handed out when its line is read (a connection
   snapshot when its section ends). The parser emits handed-out records after each call, so it never holds more than
   one snapshot's top part, one `lsof` section's sockets and the handful of records of one line. A snapshot that fails
   AC8 before acceptance reports its single skip and drops what it held; it can no longer fail after acceptance.
   `Finish(endOfInput: true)` sets `complete` false as AC8 states. The open snapshot is never finished when the parse
   ends by an exception (cancellation, emitter error, AC4 throw), so a failed parse emits a prefix of the complete one.
   An invalid or unresolvable header makes the parser skip the snapshot's lines without examining them (no snapshot
   object, no skips).
6. **lsof lines** (`LegacyLsofLine.TryParse`). The header (tokens exactly `COMMAND PID USER FD TYPE DEVICE SIZE/OFF NODE
   NAME`) is ignored wherever it appears. A socket line is split at runs of spaces: token 0 COMMAND, token 1 PID (1 to
   10 ASCII digits, 1..2,147,483,647), TYPE the first token from index 3 on that is `IPv4` or `IPv6`; from the end: an
   optional last token `(STATE)`, then NAME, then NODE `TCP` or `UDP`, which must lie after TYPE. Protocol: `tcp`,
   `tcp6`, `udp`, `udp6` from NODE and TYPE. COMMAND escapes `\xHH` (two hex digits) are decoded to bytes and the result
   read as UTF-8; other text stays as written; cut to 1,024 bytes. NAME is `LOCAL` or `LOCAL->REMOTE`; an endpoint is
   `ADDR:PORT`. The **address guard** accepts only:
   - TYPE `IPv4`: `*` (→ 0.0.0.0) or exactly four dot-separated decimal octets of 1 to 3 ASCII digits, value ≤ 255, no
     leading zero in a multi-digit octet; built from the four bytes, never through `IPAddress.TryParse`. The port is
     after the last `:`.
   - TYPE `IPv6`: `*` (→ ::) or `[` + text + `]` followed by `:PORT`; a `%` and the text after it up to `]` (a zone) is
     removed first (wire format rule: the zone is stripped, the address kept); the remaining text is 2 to 45 characters
     of `0-9 A-F a-f : .` and contains `:`; when it contains `.`, the text after its last `:` must be a dotted quad by
     the IPv4 octet rule above (four octets, 1 to 3 digits, ≤ 255, no leading zero) and no `.` may occur before that
     `:` — so `::ffff:1.2.3.04` and `::1.2.3.077`, which `IPAddress.TryParse` reads as decimal, are refused rather than
     read; then `IPAddress.TryParse` on that text alone, and the result must be `InterNetworkV6` with `ScopeId` 0.
   - Every other form — the IPv4 shortcuts, hex and octal forms and single numbers that `IPAddress.TryParse` accepts
     (`127.1`, `1`, `0x7f.0.0.1`, `010.0.0.1`, `2130706433`), brackets on an IPv4 TYPE, no brackets on an IPv6 TYPE,
     brackets with port inside, spaces, a zone that would be resolved on the host — is "socket address not mapped".
   - PORT: `*` (→ 0), 1 to 5 ASCII digits ≤ 65,535 without leading zero (except `0`), or a service name of 1 to 32
     characters of `A-Z a-z 0-9 . _ + -` found in `LegacyServiceNames` for the protocol (`tcp` for TCP, `udp` for UDP);
     anything else is "socket address not mapped".
   - State: for TCP the `(STATE)` token is required and its inner text must be one of the model's TCP states, else "socket
     address not mapped"; for UDP a state token is ignored and the state is empty.
   A line with the wrong shape (fewer tokens, no TYPE, NODE not TCP/UDP, NODE before TYPE, PID invalid) is "lsof line
   not read".
7. **Service names** (`LegacyServiceNames`): a static table of every `name port/proto` entry **and alias** of the
   `/etc/services` of Ubuntu's `netbase` (the session has netbase 6.4; Ubuntu 22.04 ships 6.3 — a name missing from the
   table only makes its socket unmapped, it never gives a wrong port). The Dev generates the table from the file once
   and commits it as C# source, with a comment naming the netbase version.
8. **Registration.** `BuiltInParsers.Create` appends `new LegacyMonitorParser(zone)` after the four error log parsers;
   its XML doc names it.

## Options considered

The weighed options are recorded in [0093](../../docs/decisions/0093-legacy-monitoring-log-snapshots-by-header-zone-and-whole-snapshot-validity.md):

- Header time: abbreviation within `import.time_zone` (chosen) vs. `SyslogClock.ResolveLocal`'s repeated-hour heuristic
  ignoring the abbreviation (wrong when the script did not run across the change) vs. a fixed abbreviation table
  (ambiguous worldwide, a second zone database).
- Zone mismatch: always fail (one odd header blocks the file forever) vs. always skip (a wrong option completes the
  content with nothing, unrecoverable because imports are idempotent per content) vs. fail before the first match,
  skip after (chosen).
- Broken snapshot: skip whole when the top part's structure is broken (missing or out-of-order summary line, missing
  table header, a summary line or table header among the rows), leave out a single bad or duplicate row with
  `complete` false, sections read on their own (chosen, revised after the challenge) vs. skip whole on any bad row (one
  wrong assumption about an unverified format would drop every snapshot) vs. read every parsable line (rows of one run
  under another run's time).
- Snapshot closed by the end of input: emit it with `complete` false on its open list (chosen) vs. emit nothing
  without a following header (the last snapshot of every file would be lost for good, because a continuation starts
  at the line in which the earlier content ends, not at its last header).
- Connections list included (chosen) vs. only the counts the issue names (`complete` would always be false).
- Port names: Ubuntu's full `/etc/services` table (chosen) vs. a short hand-picked list (drops sockets on forgotten
  ports) vs. a host lookup (forbidden by the wire format area).
- Truncated names: remove `+` and flag (chosen) vs. an alias table of guessed full names vs. names learned from other
  snapshots (results would depend on where a continued parse starts).
- `LineFramed` true (chosen): the log grows every minute and copies are taken while it grows; with `false` every later
  copy would store all earlier snapshots again. The price (documented): a snapshot cut by the copy boundary keeps in
  the earlier import what was read (skipped whole if cut before its table header, else marked incomplete, AC8), and
  its rest is skipped line by line as lines before the first snapshot in the continuation; the vmstat rate restarts.
- vmstat: raw counter and rate (chosen) vs. rate only (the issue's wording; the raw counter keeps an `oom_kill` step
  readable as a count and is what the agent will read).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `LogParsing/LegacyMonitorParser.cs` | new: the parser (`ILogParser`) |
| Vandox.Core | `LogParsing/LegacyMonitorHeader.cs` | new: header grammar and time |
| Vandox.Core | `LogParsing/LegacyMonitorSnapshot.cs` | new: one snapshot's state machine and records |
| Vandox.Core | `LogParsing/LegacyCounter.cs` | new: previous vmstat value |
| Vandox.Core | `LogParsing/LegacyTopLine.cs`, `LogParsing/LegacyTopRow.cs` | new: top summary lines, table header, rows, memory values |
| Vandox.Core | `LogParsing/LegacyProcessNames.cs` | new: command normalization and program name |
| Vandox.Core | `LogParsing/LegacyProcLine.cs`, `LogParsing/LegacyPressure.cs` | new: meminfo, pressure, vmstat lines |
| Vandox.Core | `LogParsing/LegacyLsofLine.cs` | new: lsof line and address guard |
| Vandox.Core | `LogParsing/LegacyServiceNames.cs` | new: service name table |
| Vandox.Core | `LogParsing/BuiltInParsers.cs` | register the parser (body and XML doc) |
| testdata | `logs/legacy-monitor-old.log`, `logs/legacy-monitor-new.log` | new fixtures (Tester) |

Files the skeleton rewrites: only `BuiltInParsers.cs` among existing files (read; final apart from the added entry and
its doc comment).

## Signatures (for the Dev's skeleton)

```csharp
namespace Vandox.Core.LogParsing;

/// Reads the legacy monitoring log of top, /proc and lsof snapshots.
public sealed class LegacyMonitorParser : ILogParser
{
    public const string ParserType = "legacy-monitor";
    public const string ZoneMismatchFirst = "the time zone of the first snapshot header does not match import.time_zone";
    public LegacyMonitorParser(DateTimeZone? timeZone);
    public string Type { get; }                 // ParserType
    public bool LineFramed { get; }             // true
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head);
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken);
}

internal readonly record struct LegacyMonitorHeader(int Year, int Month, int Day, int Hour, int Minute, int Second, string Zone)
{
    internal const string ZoneMismatch = "time zone does not match import.time_zone";
    internal static bool IsHeaderLine(ReadOnlySpan<byte> line);
    internal static LegacyMonitorHeader? TryParse(ReadOnlySpan<byte> line);
    internal string? Resolve(DateTimeZone timeZone, out DateTimeOffset instant);   // null, SyslogTime.InvalidDate, SyslogTime.OutsideRange or ZoneMismatch
}

internal sealed record LegacyCounter(DateTimeOffset At, ulong Value);

internal sealed class LegacyMonitorSnapshot
{
    internal LegacyMonitorSnapshot(long headerLine, DateTimeOffset capturedAt, Dictionary<string, LegacyCounter> counters);
    internal void Add(long lineNumber, string line, bool cut, IRecordEmitter output, List<DataRecord> ready);   // reports skips via output.Skip at once (row skips at acceptance); appends records to emit
    internal void Finish(bool endOfInput, IRecordEmitter output, List<DataRecord> ready);                     // the single AC8 skip, or acceptance and the open lists (complete false when endOfInput)
}

internal sealed record LegacyTopRow(int Pid, string User, string State, double CpuPercent, ulong ResBytes, string Command);

internal static class LegacyTopLine
{
    internal static bool TryParseLoad(string line, out double[] load);          // load1, load5, load15
    internal static bool TryParseTasks(string line, out uint total);
    internal static bool TryParseCpu(string line, out double[] percents);       // us sy ni id wa hi si st
    internal static bool TryParseMemory(string line, out ulong[] memory);       // total, free, used, buff/cache in bytes
    internal static bool TryParseSwap(string line, out ulong[] swap);           // total, free, used, avail Mem in bytes
    internal static bool IsTableHeader(string line);
    internal static LegacyTopRow? TryParseRow(string line);
    internal static bool TryParseMemoryValue(string token, out ulong bytes);
    internal static bool TryScaleBinary(string integerDigits, string fractionDigits, int power, out ulong bytes);  // AC5 scaling order; digits pre-validated ASCII, fractionDigits may be empty, power 0..6
}

internal static class LegacyProcessNames
{
    internal static string Normalize(string column, out bool truncated);           // may be empty ("+")
    internal static string OldCommandOf(string column, out bool truncated);        // never empty: Normalize, else the column as printed; ≤ 1,024 bytes
    internal static string CommandOf(string commandLine, string column);           // never empty: derived name, else commandLine, else column; ≤ 1,024 bytes
    internal static string ProgramOf(string command);                              // never empty for a non-empty command: first word, else command
}

internal sealed record LegacyPressure(string Resource, string Kind, double Avg10, double Avg60, double Avg300, ulong Total);

internal static class LegacyProcLine
{
    internal static bool TryParseMeminfo(string line, out string field, out ulong value, out bool kilobytes);
    internal static bool IsNotAvailable(string line);                              // "n/a" or "<res>: n/a"
    internal static IReadOnlyList<LegacyPressure>? TryParsePressure(string line, string? previousResource);   // null when not read
    internal static bool TryParseVmstat(string line, out string counter, out ulong value);                      // any "<name> N"; the snapshot keeps the four
}

internal sealed class LegacyLsofLine
{
    internal const string NotRead = "lsof line not read";
    internal const string NotMapped = "socket address not mapped";
    internal string Command { get; }
    internal int Pid { get; }
    internal string Proto { get; }
    internal IPEndPoint Local { get; }
    internal IPEndPoint? Remote { get; }
    internal string State { get; }
    internal string SocketKey { get; }
    internal static bool IsHeader(string line);
    internal static LegacyLsofLine? TryParse(string line, out string? reason);   // reason NotRead or NotMapped when null
}

internal static class LegacyServiceNames
{
    internal static bool TryGetPort(string name, bool udp, out int port);
}
```

`LegacyLsofLine` needs a private constructor taking its seven values; members are wrapped in `#region` blocks as the
repository requires, and every skeleton file carries the skeleton pragma of `.squad/stack.md`.

## Test files

- `tests/Vandox.Core.Tests/LegacyMonitorParserTests.cs`
- `tests/Vandox.Core.Tests/LegacyMonitorHeaderTests.cs`
- `tests/Vandox.Core.Tests/LegacyTopLineTests.cs`
- `tests/Vandox.Core.Tests/LegacyProcessNamesTests.cs`
- `tests/Vandox.Core.Tests/LegacyProcLineTests.cs`
- `tests/Vandox.Core.Tests/LegacyLsofLineTests.cs`
- `tests/Vandox.Core.Tests/LegacyServiceNamesTests.cs`
- `tests/Vandox.Core.Tests/BuiltInParsersTests.cs` (existing; AC1)
- Fixtures `testdata/logs/legacy-monitor-old.log`, `testdata/logs/legacy-monitor-new.log` (Tester; anonymized,
  documentation addresses only, never an excerpt of the real file with its addresses). A generator for the
  heap-bound and cap tests, if needed, is `tests/Vandox.Core.Tests/LegacyMonitorSamples.cs` (Tester), listed in the
  *Test doubles* row "log parsers (C#)" of `.squad/project.md` by the Tester.

Existing test code affected: no signature changes. `BuiltInParsersTests.BuiltInParsersCreateReturnsSystemParsersThenErrorLogParsers`
(lines 42-57) and `BuiltInParsersCreateListIsAcceptedByTheRegistry` (lines 64-73) pin the eight types; the **Tester**
extends them to nine in step 5 (they fail on the skeleton, which registers nothing new).

## Areas

- [Log import](../../docs/areas/log-import.md) — new section *Legacy monitoring log* (after *Plesk and web server error
  logs*): recognition, snapshot and header grammar, time and zone rules (AC3, AC4), top part, process snapshot and
  programs (the never-empty name rule), sections, lsof mapping and the address guard (with the embedded-IPv4 rule),
  skips and their reasons, bounds (every line counts toward its cap; worst-case memory as in *Security considerations*;
  a byte value that does not fit 64 bits makes its line unreadable, a program's `rss_bytes` sum saturates),
  the limitations (old/new command names differ, a new-format snapshot cut before its first section reads as old, the
  last snapshot of a file is marked incomplete in its open list because the log has no end marker, the rest of a
  snapshot cut by a copy boundary is skipped line by line in a continued import and lost, `vmstat_per_second` is
  missing for the first snapshot of every parse including each continuation, `top`'s first frame may show since-boot
  CPU averages — unverified, values are as `top` printed them with its rounding, and the formats were built from the
  issue's description without the real sample). *System log parsers* list: add `legacy-monitor` after `apache-error`;
  *Repeatable import*: add the parser to the line-framed list; *Parsers*: "Only the parsers of Plesk and web server error
  logs keep raw lines" stays true; *Implementation*: add the new types; *Related decisions*: 0093. Owner: **Dev**.
- [Wire format](../../docs/areas/wire-format.md) — new subsection *Metric names* under `metric`: the names, units and
  labels of AC5 and AC9, stated as the names every producer of these values uses (the agent's collectors included);
  *Producer mapping for connection endpoints*: "left out with a warning" becomes "left out and reported (the importer
  counts it as a skipped line)". Owner: **Dev**.

## Documentation updates

- `README.md`, *Import logs*, *Supported sources*: a bullet for the legacy monitoring log (source type
  `legacy-monitor`, recognized by its first header line, both formats, needs `import.time_zone`, what becomes of it);
  the `import.time_zone` row of *Backend options* names the legacy log; the continuation sentence ("Limits: a syslog,
  …") adds it. Owner: **Dev**.
- `docs/ARCHITECTURE.md`: the parser lists in the status paragraph (line 10) and in the `Vandox.Core` component bullet
  (lines 44-48) add the legacy monitoring log; record 0093 linked in the *Data flow* records list. Owner: **Dev**.
- `.squad/project.md`, *Security area* 10: add `LegacyMonitorParser`, `LegacyMonitorHeader`, `LegacyMonitorSnapshot`,
  `LegacyTopLine`, `LegacyProcLine`, `LegacyLsofLine` (strict address guard in front of `IPAddress.TryParse`) and
  `LegacyServiceNames`, and record 0093. Owner: **Dev**. The *Test doubles* row: **Tester** (only if a helper is added).
- `docs/decisions/0093-…` and its index row: written by the Lead (Proposed).

## Architecture check

No guarantee in `docs/ARCHITECTURE.md` or `.squad/project.md` changes. Imported records have origin `import`, so they
never raise alerts (0022); the parser adds no file access (it reads the stream the importer gives it, 0079) and no
network or host lookup (port names from a built-in table, IPv6 zones removed before parsing). The parser contract of
the log import area holds: deterministic, bounded memory, cancellation honored, fixed skip reasons without input text,
records emitted as a prefix on failure. The integration-surface checklist *A new log parser* is covered: type and
`ParserTypes.IsValid` (AC1), registration in `BuiltInParsers` (which `ImportCommand` uses), detection tests against the
other parsers (AC1), README sources list (*Documentation updates*).

## Security considerations

- [x] Limits and where they apply: lines are cut at 16 KiB of raw bytes by `LogLineReader` (before decoding); the
  `lsof` line bound of 1,024 bytes counts the decoded text (an invalid byte counts three, so at least as strict as raw bytes); the 64-byte row-token bound, the 1,024-byte
  `command`, the 2,048-byte `cmdline` and the 16-character header zone count UTF-8 bytes of the decoded text (U+FFFD
  counts three), cut at a character boundary with `Utf8Text.Cut`. Per snapshot at most 4,096 row lines, 4,096 `lsof`
  lines, 128 meminfo, 16 pressure and 64 vmstat lines, every non-empty line counted, lines beyond a cap dropped unseen
  and without a skip (AC12); the only skips held back are at most 4,097 row skips (the cap skip included) before acceptance, every other skip
  goes to the emitter at once (which keeps a count and at most `ImportLimits.MaxProblems` = 10 problems,
  `RecordEmitter.cs:219-233`); the counter history holds four entries; the PID set and the socket dedupe set hold at most
  4,096 entries each, a socket key at most 1,024 raw bytes. Worst-case memory is a constant independent of the input
  size: .NET strings take 2 bytes per character, so 4,096 rows with a 2,048-byte `cmdline` and a 1,024-byte `command`
  are about 24 MiB, 4,096 socket keys and decoded commands of up to 1,024 characters about 16 MiB — about 40 MiB in
  all, plus one 16 KiB line (corrected from the earlier 12 MiB, Security N2). The rows are released when the top part
  is accepted, before any section is read, so the two never peak together in practice; the documented bound is the
  sum.
- [x] Exceptions that reach a user-visible reason: only `InvalidOperationException` with the fixed texts
  "import.time_zone is not set" and "the time zone of the first snapshot header does not match import.time_zone"
  (the importer shows `exception.Message`, `ImportPass.cs:538`). No other exception may escape for any input: number
  conversions are done on validated ASCII digit strings with overflow checks, dates are built only after integer range
  checks, `IPAddress.TryParse` is called only on guarded text. Skip reasons are the fixed texts of AC4, AC8, AC9, AC10,
  AC12 and `SyslogTime`'s "invalid date" / "time outside the storable range".
- Forged lines (limitation, documented in the area): any process that can write the log file can forge snapshots with
  any time, process or connection; the content claim makes any file whose first line has a header's shape a legacy
  log. Command and user texts are input; they are stored as they are and escaped where displayed (area 12).

## Security review

- **B1 (unbounded line skips) — accepted, plan revised.** Skips are no longer collected per snapshot: once the top
  part is accepted every section skip goes to the emitter at once, and the only held-back skips are the row skips of a
  top part not yet accepted, at most 4,096 because every row line counts toward the row cap (step 5, AC12). vmstat is
  capped at 64 lines; unread, unmapped, too-long and header lines of `lsof` count toward the 4,096-line socket cap, and
  every line beyond any cap is dropped unseen and without a skip (AC12). The heap test
  `LegacyMonitorParserParseRetainsBoundedMemoryForSectionLines` (200,000 malformed vmstat and 200,000 unmapped lsof
  lines minus 2,000 each, `CountingEmitter`, `RetainedBytes`, below 1 MiB) is added; the memory figure is corrected.
- **B2 (empty derived names) — accepted, plan revised.** `command` and `program` are never empty: derived name, else
  the normalized COMMAND, else the COMMAND as printed (AC6, AC7, `OldCommandOf`/`CommandOf`/`ProgramOf` signatures);
  DataRows for `+`, `/opt/x/`, `:`, `[]` and the parser test `LegacyMonitorParserParseKeepsSnapshotWithEmptyDerivedNames`
  are added.
- **N1 (digit bound, overflow) — accepted.** Decimal numbers: 1 to 15 integer digits, optional fraction of 1 to 6;
  integers 1 to 20 digits within `ulong`; memory × 1024^n and meminfo × 1024 above `ulong.MaxValue` make the line
  unreadable (AC5, AC9, step 1), with refuse DataRows in `LegacyTopLineTryParseRefusesMalformedSummaryLines` and
  `LegacyProcLineTryParseMeminfoRefusesOtherForms`.
- **B3 (intermediate overflow in byte scaling, second review) — accepted by Lead decision, plan fixed.** The earlier
  "computed exactly (decimal)" overflowed `decimal` for `PiB`/`EiB` at the digit bounds and `UInt128` for RES at
  `e`. The plan now fixes the order (AC5 *Scaling order*, `TryScaleBinary`): integer part against
  `ulong.MaxValue >> (10*n)` first, then `UInt128` arithmetic bounded below 2^85, then the `ulong` check after
  rounding; meminfo `kB` checks N against `ulong.MaxValue >> 10` before the shift (AC9). Refuse DataRows at the
  maximum digit bounds: `99999999999999999999.999e` and `99999999999999999999.999p`
  (`LegacyTopLineTryParseMemoryValueRefusesOtherForms`), `EiB Mem : 999999999999999.999999 total` and
  `PiB Mem : 999999999999999.999999 total` (`LegacyTopLineTryParseRefusesMalformedSummaryLines`), plus the boundary
  read `15.999e`. The rest of the number paths was re-checked: decimal numbers (load, CPU, %CPU, pressure averages)
  are at most 15+6 digits and go to `double` without scaling (finite); integers (`Tasks`, PID, pressure `total`,
  vmstat, meminfo without `kB`) use `uint`/`int`/`ulong.TryParse` on at most 20 validated digits, which returns false
  instead of throwing. Related gap found on the way: the per-program `rss_bytes` sum (AC7) would wrap silently in the
  unchecked build; AC7 now makes it saturate at `ulong.MaxValue`, pinned by
  `LegacyMonitorParserParseSaturatesProgramRssSum` (the only other byte arithmetic in the plan).
- **N2 (memory estimate) — accepted.** Corrected to about 40 MiB (UTF-16) in *Security considerations*; the area
  document states the same figure.
- **N3 (embedded IPv4 in IPv6) — accepted, fixed rather than pinned.** A trailing dotted quad must meet the IPv4 octet
  rule (step 6), so `::ffff:1.2.3.04` and `::1.2.3.077` are refused; DataRows in
  `LegacyLsofLineTryParseRefusesLenientAddressForms`, the canonical form in
  `LegacyLsofLineTryParseReadsIpv6WithEmbeddedIpv4`. The whole list of accepted forms was re-checked: IPv4 (`*`, the
  strict dotted quad), IPv6 (`*`, bracketed hex groups with optional zone stripped, optional strict trailing quad),
  port (`*`, strict decimal, table name); the IPv6 text alphabet still excludes letters beyond `A-F`, spaces, `[`,
  `]` and `%` after the zone is removed, so `IPAddress.TryParse` sees no other form.

## Challenge

- **Major 1 (whole snapshot lost for one bad row) — accepted, plan and 0093 revised.** An unparsable row and a
  duplicate-PID row are left out with a skip at their line and `complete` false; the whole-snapshot skip stays only
  for structural signs of a cut or mixed top part (missing or out-of-order summary line, missing table header, a
  summary line or table header among the rows). AC6 and AC8 now agree.
- **Major 2 (cut snapshot reported complete) — accepted.** A snapshot closed by the end of input emits its open
  `lsof` list or old-format process table with `complete` false (AC8, AC10) and its test
  `LegacyMonitorParserParseMarksSnapshotCutAtEndOfInputIncomplete`; the loss of the rest at a continuation is
  documented and pinned by `LegacyMonitorParserParseSkipsRestOfCutSnapshotAtContinuation`. The alternative (no
  emission without a following header) is rejected: the last snapshot of every file would be lost for good.
- **Minor 3 (empty command) — accepted**, see B2.
- **Minor 4 (vmstat rate start dependence) — accepted.** Stated in AC9 and the area limitations, pinned in
  `LegacyMonitorParserParseConvertsVmstatCountersToRates`; 0093 option 14's argument (formerly 11) concerns names that would change,
  a missing first rate is a gap, not a different value, and the raw counter is complete.
- **Minor 5 (skip volume at a continuation) — accepted, checked.** `RecordEmitter.Skip` (`RecordEmitter.cs:219-233`)
  keeps a counter and at most `ImportLimits.MaxProblems` (10) problems, so per-line skips cost no memory;
  `LegacyMonitorParserParseSurvivesHostileInput` bounds the skip count by the line count and the caps (AC8).
- **Minor 6 (fixtures encode guesses) — accepted.** The orchestrator notes in `log.md` and the PR description that
  the parser was built from the issue's description without the real sample; the area limitations say so; the tolerant
  paths of Major 1 are required by AC8.
- **Minor 7 (CPU line without space after a comma) — accepted.** AC5 now states the tokenization and carries the
  DataRows `0.0 us,100.0 id` and a memory line whose number fills `top`'s column.

## Decision records

- [`docs/decisions/0093-legacy-monitoring-log-snapshots-by-header-zone-and-whole-snapshot-validity.md`](../../docs/decisions/0093-legacy-monitoring-log-snapshots-by-header-zone-and-whole-snapshot-validity.md) (Proposed, indexed).

## Out of scope / follow-ups

- The agent collectors #31, #32, #33 reuse the metric names and the process and connection snapshot rules; no new
  issue (their issues already require "same output model as the legacy log parser").
- Showing the imported snapshots in the web UI (later views).
