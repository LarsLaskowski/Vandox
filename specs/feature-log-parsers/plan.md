# Plan: Parsers for journal export, syslog and kern.log

Source: Issue #16 | [spec.md](spec.md) | [tasks.md](tasks.md)
Status: Draft
Tier: security — the change adds parsers of external input (*Security areas* 10 in `.squad/project.md`: log files),
changes a wire format record (area 10: ingest wire format) and adds a dependency (NodaTime), each of which is `security`.

## Problem / root cause

Summary of [spec.md](spec.md): `vandoxd import` has a parser framework but no parser. This feature adds a parser for
`journalctl -o export` and a generic parser for rsyslog files (`syslog`, `kern.log`, rotations), registers both as the
built-in parser list, adds the optional `host` field to the `log_line` record in Go, C#, the golden fixture and storage,
adds the backend option `import.time_zone` (no default) for year-less local time stamps, and keeps multi-line kernel
reports (OOM, `cut here`) in one record.

Claims of the issue, checked against the code:

- "Parsers implement `ILogParser` in `src/Vandox.Core/LogParsing`" — **confirmed** (`ILogParser.cs`, `ParserRegistry.cs`,
  `LogLineReader.cs` there).
- "registered where `ImportCommand` builds the `ParserRegistry`" — **partly refuted**: `src/Vandox.Backend/Cli/ImportCommand.cs:54`
  builds `new ParserRegistry(hooks.Parsers ?? [])`, and `Program.Main` passes `new ServeHooks()` whose `Parsers` is
  `null`, so production has no parser list at all (`ServeHooks.cs:27-29`: "none when `null`"). The plan adds a built-in
  list (`BuiltInParsers`) used when no hook replaces it.
- "Fields: timestamp (UTC), host, program, PID, severity, message" — **gap**: `log_line` has no host field in either
  language (`src/Vandox.Core/Model/LogLine.cs`, `internal/model/logline.go`); severity maps to `priority` (0-7),
  timestamp to `captured_at`. The plan adds `host` (record 0084).
- "the two must agree on the fields of the `log_line` record (golden wire fixture, 0075)" — **confirmed**: line 9 of
  `testdata/wire/all-kinds.jsonl` pins `log_line` without `host`; the fixture is regenerated with it.
- "The Go agent ... does its own minimal line parsing (#37)" — **confirmed as future work**: `internal/` has no log
  shipping code yet; nothing to align beyond the record.
- "Timestamps without a year ... using the file's rotation and modification time" — **confirmed possible**: `LogFile`
  carries the cleaned name (with `.gz` removed, so the rotation suffix is visible) and the UTC modification time, and a
  resumed content is parsed with the name and time of its first import (`src/Vandox.Import/ImportPass.cs:414`), so an
  inferred year cannot change in the middle of a file. The modification time is in practice always present: the scanner
  sets it for a regular file (`src/Vandox.Import/Scanner.cs:267`) and for a tar entry (`Scanner.cs:514`); it is only
  wrong, not missing, after a plain `cp`. A `-YYYYMMDD` rotation date in the name survives such a copy, so it is
  preferred when present (AC-S4).
- Failing a file from a parser (AC-S7) — **verified**: an exception of `ParseAsync` fails the file with the exception's
  message as reason after storing the records parsed before it (`ImportPass.cs:414-470`, `SettleAsync` at line 103;
  test `ImporterFailsFileWhenParserFails`), and a file that is not complete is resumed by count on the next run
  (`ImportPass.cs:330`), so a re-run after setting the option completes it.
- "Year boundary and time zones handled" — needs the server's zone, which no log line carries. **Verified constraint:**
  the pinned runtime image (`mcr.microsoft.com/dotnet/aspnet@sha256:48e51f2f...`, linux/amd64 manifest
  `sha256:8c20ba8c...`) has no `zoneinfo`/`tzdata` path in any of its five layers (listed with `tar -tzf` from the
  registry blobs on 2026-10-09), so `TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")` cannot work in the container.
  The plan uses NodaTime's embedded time zone database (record 0085).

Related observations (no defect fixed here): `README.md` (*Import logs*, last paragraph) states that the import
recognizes no file until #16-#20 land, which this feature makes stale (Documentation updates). Not verified against the
production server: that Ubuntu 22.04's rsyslog writes the traditional file format (`RSYSLOG_TraditionalFileFormat`,
the packaged `/etc/rsyslog.conf` default) and that Ubuntu 24.04 writes the RFC 3339 format; both are supported either way.
Also from the packaged configuration, not checked on the server: Ubuntu's `/etc/rsyslog.d/50-default.conf` writes
`kern.*` to `kern.log` and `*.*;auth,authpriv.none` to `syslog`, so every kernel line is in both files and an import
of both stores it twice (stated in 0086 and the area document, for #21).

## Acceptance criteria

Each line is at least one unit test. Sample lines are real, shortened lines in the formats of systemd 249, rsyslog 8.2112
and Linux 5.15 (Ubuntu 22.04); the Tester takes them from those formats, shortened, never invented shapes. `[DataRow]`
wherever several inputs share one behavior.

### Journal export parser (`journal`)

- [ ] AC-J1 Detect: `MatchContent` when the head begins with a `__CURSOR=` line or a `__REALTIME_TIMESTAMP=` line and
  contains a `__REALTIME_TIMESTAMP=` line; `NoMatch` for an empty head, a syslog line, the binary journal signature
  `LPKSHHRH`, and text that has `__CURSOR=` later than the first byte. The file name never matters.
- [ ] AC-J2 Fields, `[DataRow]` per real entry (sshd, systemd, CRON, kernel): `CapturedAt` = `__REALTIME_TIMESTAMP`
  microseconds since the Unix epoch in UTC; `Host` = `_HOSTNAME`; `Program` = `SYSLOG_IDENTIFIER`, else `_COMM`, else
  empty; `Pid` = `_PID`, else `SYSLOG_PID`, else 0; `Priority` = `PRIORITY` when it is one digit 0-7, else `null`;
  `Message` = `MESSAGE`; `Log` = `journal`; `Source` = `journal`; `Origin` = `import`; `Seq` = 0.
- [ ] AC-J3 Binary-safe: a binary `MESSAGE` (name line, 64-bit little-endian length, bytes, `\n`) with embedded `\n`,
  NUL and invalid UTF-8 is kept (invalid bytes become U+FFFD); a binary field that is not kept is skipped and the
  fields after it are read.
- [ ] AC-J4 Bounds (every limit counts the **UTF-8 bytes of the decoded text**, see *Text limits*): a `MESSAGE` over
  16,384 bytes (text and binary form) gives a message of at most 16,384 UTF-8 bytes, cut at a character boundary, with
  `Truncated = true`; a kept short field (`_HOSTNAME`, `SYSLOG_IDENTIFIER`, `_COMM`) over 1,024 bytes is cut the same way
  to at most 1,024 UTF-8 bytes. `[DataRow]`s with invalid bytes, each giving a record whose field is at most its limit in
  UTF-8 bytes, `Truncated = true`, and no "record refused" skip through a validating emitter (`RecordingEmitter` checks
  `DataRecord.Validate()`): text `MESSAGE` of 16,384 bytes `0xFF`; binary `MESSAGE` of 16,384 and of 20,000 bytes
  `0xFF`; `_HOSTNAME`, `SYSLOG_IDENTIFIER` and `_COMM` each of 1,024 bytes `0xFF`. A valid multi-byte character split
  by the raw cut is dropped whole (no trailing U+FFFD from the cut). A 64 MiB binary field of an unkept name is skipped
  and the next entry is read; a binary length larger than the remaining input or at least 2^63 skips the entry with
  "truncated entry" and ends the parse without an exception; a text value ended by the end of input instead of `\n`
  skips its entry with "truncated entry".
- [ ] AC-J5 Malformed input: a field name that is empty, longer than 64 bytes, starts with a digit or holds a character
  outside `A-Z 0-9 _` skips its entry with "malformed field" and parsing resumes after the next empty line; an entry
  without `__REALTIME_TIMESTAMP`, with a non-decimal or out-of-range one, or without `MESSAGE` is skipped with "entry
  without __REALTIME_TIMESTAMP", "invalid __REALTIME_TIMESTAMP" or "entry without MESSAGE"; a repeated field keeps its
  first value; several empty lines between entries and a last entry without the closing empty line are accepted.
  `[DataRow]`s for "invalid __REALTIME_TIMESTAMP", none of which throws: `0`, `12a4`, `-1`, `+1`, ` 1`, 21 digits,
  `99999999999999999999` (20 digits, above `UInt64.MaxValue`), `18446744073709551615` (`UInt64.MaxValue`),
  `9223372036854776` (one microsecond after the storable maximum); `9223372036854775` (the storable maximum) is
  accepted as 2262-04-11T23:47:16.854775Z.
- [ ] AC-J6 Every skip reason is one of the fixed texts above (no input text), passed with line 0.

### Syslog parser (`syslog`)

- [ ] AC-S1 Detect: `MatchName` when the first line of the head (UTF-8 BOM removed, `\r` before `\n` removed, or the
  whole head if it has no `\n`) has a syslog header in either form, or when the base name is `syslog` or `kern.log`
  with an optional `.N` or `-YYYYMMDD` suffix (`a/b/syslog.1`, `kern.log.2`, `syslog-20260301`); `NoMatch` otherwise
  (journal export head, MariaDB error log line `2026-03-01 12:00:00 0 [Note] ...`, empty head, `other.txt` with prose).
- [ ] AC-S2 Header forms, `[DataRow]` per real line (see *Accepted forms*): traditional with space- and zero-padded day,
  RFC 3339 with fraction and `+01:00` / `Z`, optional `<PRI>` (priority = PRI mod 8, PRI 0-191), tags `kernel:`,
  `systemd[1]:`, `postfix/smtpd[2210]:`, `CRON[1234]:`, a line without a tag (`last message repeated 3 times`, program
  empty), a PID above 2,147,483,647 (pid 0, tag still parsed). Fields: `Host`, `Program`, `Pid`, `Priority` (`null`
  without `<PRI>`), `Message` (after `tag:` and one optional space), `Log` = `LogFile.Name` unchanged (the path as the
  import lists it, rotation suffix included: `backup/var/log/syslog.1` stays `backup/var/log/syslog.1`), `Source` = `syslog`.
- [ ] AC-S3 Lines that are not records: an empty line ("empty line"), a line without a valid header including an unknown
  month, hour 24, minute 60, an RFC 3339 offset beyond ±14:00 or a lower-case `t`/`z` ("not a syslog line"), Feb 29 in
  an inferred non-leap year and an RFC 3339 date beyond its month (`2026-02-30`, `2026-04-31`) ("invalid date"); each
  skipped with its 1-based line number, the parse continues. RFC 3339 times outside the storable range (*Text limits and
  time range*) are skipped with "time outside the storable range" and never throw, `[DataRow]` per form:
  `0001-01-01T00:00:00+01:00` (before year 1 in UTC), `9999-12-31T23:59:59-01:00` (after year 9999 in UTC),
  `0000-01-01T00:00:00Z` (year 0), `1677-09-21T00:12:43Z` (before the minimum), `2262-04-11T23:47:17Z` (after the
  maximum); `1677-09-21T00:12:44Z` and `2262-04-11T23:47:16Z` are accepted. Each row's file also holds a valid line after
  it, which is emitted.
- [ ] AC-S4 Year (anchor = a valid `-YYYYMMDD` suffix of the base name, before an optional `.gz` already removed by the
  importer, meaning the end of that local day; else `ModTime`): mtime 2026-03-02, `Mar  1` → 2026; mtime 2026-01-03 with
  `Dec 30 ...` then `Jan  2 ...` → 2025-12-30 then 2026-01-02; a first line up to one day after the anchor stays in the
  anchor's year, more than one day after goes to the year before; a line a few seconds earlier than its predecessor does
  not advance the year; a line more than 180 days before its predecessor advances it; Feb 29 resolves in 2028, is skipped
  in 2026; `syslog-20260103` with mtime 2027-05-01 (a plain copy) and `Dec 30 ...` → 2025-12-30 (the name wins);
  `syslog-20260230` (no such date) with mtime 2026-03-02 and `Mar  1` → 2026 (falls back to mtime); without a usable
  anchor, every year-less line is skipped with "year unknown: the file has no usable date", RFC 3339 lines of the same
  file are still read. An anchor is usable only inside the storable range, and no anchor, year or instant outside it is
  ever built as a `DateTimeOffset` or NodaTime value (no exception), `[DataRow]` per form: `syslog-00010101` with mtime
  2026-03-02 and `Dec 30 ...` → the name date is unusable, the mtime gives 2025-12-30; `syslog-99991231` with mtime
  2026-03-02 and `Mar  1 ...` → mtime, 2026 (the end of that local day would overflow); `syslog-00000101` → mtime;
  with zone `UTC` and `syslog-16770922`: `Sep 21 00:00:00` → skipped with "time outside the storable range" (before
  1677-09-21T00:12:43Z), `Sep 21 00:13:00` → stored as 1677-09-21T00:13:00Z, `Oct  1 00:00:00` (more than a day after
  the anchor, so year 1676) → "time outside the storable range"; `syslog-22620410` with `Apr 10 12:00:00` → stored as
  2262-04-10T12:00:00Z; `syslog-22620412` (the end of that day is past the maximum) with mtime 2026-03-02 and
  `Mar  1 ...` → mtime, 2026; no name date and mtime 9000-01-01 → no usable anchor, "year unknown: the file has
  no usable date". Year advance at the upper bound: with zone `UTC`, mtime 2026-07-02 and 1,000 lines alternating
  `Jul  1 00:00:00` and `Jan  1 00:00:00` (starting with `Jul  1`), the year advances at every `Jan  1` line, lines 1-472
  are records (the last one 2262-01-01T00:00:00Z), lines 473-1,000 are skipped with "time outside the storable range",
  and nothing throws; the inferred year stops advancing once it is past the storable range. These counts hold only
  under the predecessor rule (*Text limits and time range*): line 473 (`Jul  1` 2262, skipped) is the predecessor of
  line 474, which therefore advances to 2263; if skipped lines did not count, every later `Jan  1` line would compare
  with line 472, stay in 2262 and be emitted. Predecessor rows (zone `UTC`, mtime 2026-07-02): `Jul  1 ...`, an RFC
  3339 line `2026-12-31T00:00:00Z`, `Jun  1 ...` → 2026-07-01, 2026-12-31, 2026-06-01 (the RFC 3339 line is no
  predecessor; compared with it, `Jun  1` would advance to 2027); `Jul  1 ...`, `Nov 31 ...`, `May  1 ...` →
  2026-07-01, skip "invalid date", 2027-05-01 (the skipped `Nov 31` is the predecessor, and `May  1` is more than 180
  days before it); `Jul  1 ...`, a line that is not a syslog line, an empty line, `Jun  1 ...` → 2026-07-01, two
  skips, 2026-06-01.
- [ ] AC-S5 Time zones: zone `UTC` keeps the wall time; `Europe/Berlin` `Jul  1 12:00:00` → 10:00Z, `Jan 15 12:00:00` →
  11:00Z; the skipped hour `Mar 29 02:30:00` (2026) → 01:30Z (shifted forward); the repeated hour on 2026-10-25: a local
  time in it takes the earlier offset (+02:00) unless that puts it more than `SyslogClock.BackwardTolerance` (10 minutes)
  before the previous line's instant, then the later offset (+01:00). `[DataRow]` per file order: `02:59:59`,
  `02:00:01`, `02:30:00` → 00:59:59Z, 01:00:01Z, 01:30:00Z; `02:59:59`, `02:59:58`, `02:00:01` → 00:59:59Z, 00:59:58Z,
  01:00:01Z (a step back within the tolerance stays in the first pass); `02:59:59`, `02:10:00`, `02:09:58` → 00:59:59Z,
  01:10:00Z, 01:09:58Z (a step back inside the second pass stays there); `Oct 25 02:59:59`, the RFC 3339 line
  `2026-10-25T00:55:00Z`, `Oct 25 02:49:00` → 00:59:59Z, 00:55:00Z, 01:49:00Z (the "previous line" is the last
  year-less line that got an instant, not the RFC 3339 line: 00:49Z would be 10:59 before 00:59:59Z, but only 6 minutes
  before 00:55:00Z); `Oct 25 02:59:59`, `Sep 31 12:00:00` (skipped, "invalid date", no instant), `Oct 25 02:49:00` →
  00:59:59Z, skip, 01:49:00Z (a skipped line leaves the previous instant unchanged); the first line of a file in the
  repeated hour takes the earlier offset. New Year in the zone: mtime 2025-12-31T23:30Z (00:30 local on Jan 1) with `Jan  1 00:10:00` →
  2025-12-31T23:10Z; RFC 3339 lines ignore the zone.
- [ ] AC-S6 A line cut by `LogLineReader` (over 16 KiB) gives `Truncated = true`. Limits count UTF-8 bytes of the decoded
  text: a valid traditional header followed by `0xFF` bytes up to a line of exactly 16,384 bytes (not cut by the
  reader; the decoded message is about 49,000 UTF-8 bytes) gives a message of at most 16,384 UTF-8 bytes, `Truncated =
  true`, and no "record refused" skip through the validating `RecordingEmitter`; the same with an RFC 3339 header.
  `HOST` and `PROGRAM` are limited syntactically, in UTF-8 bytes of the decoded line (*Accepted forms*: 1-255 and
  1-128), which is below the model's 1,024, so they are never cut: `[DataRow]` per form, each followed by a valid line
  that is emitted — host of 255 `a` → `Host` of 255 bytes; host of 256 `a` → "not a syslog line"; host of 85 bytes
  `0xFF` (decoded: 85 U+FFFD, exactly 255 UTF-8 bytes) → `Host` of 85 U+FFFD; host of 86 bytes `0xFF` (258 UTF-8 bytes)
  → "not a syslog line"; host of 255 bytes `0xFF` (765 UTF-8 bytes) → "not a syslog line"; tag `PROGRAM:` with 128 `a`
  → `Program` of 128 bytes; with 129 `a` → no tag (program empty, pid 0, message = the rest after the host); with 42
  bytes `0xFF` and `ab` (exactly 128 UTF-8 bytes) → `Program` of 42 U+FFFD and `ab`; with 43 bytes `0xFF` (129 UTF-8
  bytes) → no tag; with 128 bytes `0xFF` → no tag. Every emitted record passes the validating `RecordingEmitter`.
- [ ] AC-S7 Zone not set (`new SyslogParser(null)`): a file of RFC 3339 lines is parsed completely; a file whose
  first year-less (traditional) line comes after two RFC 3339 lines emits those two records and then `ParseAsync` throws
  `InvalidOperationException` with the message `SyslogParser.TimeZoneNotSet` (`import.time_zone is not set`, no input
  text); the year-less line is neither emitted nor skipped; a line that is not a syslog line before it is still skipped
  as in AC-S3. The check comes before the anchor: a file without anchor and zone fails the same way. An open kernel
  report is **not** flushed when the parse ends by this exception (only a normal end of input runs
  `KernelReportGrouper.Finish`): a file of an RFC 3339 OOM start line (`kernel: ... invoked oom-killer: ...`), an RFC
  3339 `sshd[...]` line and then a year-less line emits exactly the `sshd` record before throwing; a second parse of the
  same content and `LogFile` with zone `UTC` emits a sequence whose first element equals that record (the resume by
  count drops exactly it).

### Kernel reports (both parsers)

- [ ] AC-K1 The fixture `testdata/logs/kern.log-oom` (a real, shortened OOM report: `invoked oom-killer`, `CPU:`,
  `Call Trace`, `Mem-Info:`, `Node 0 ...`, `Tasks state (memory values in pages):`, task rows, `oom-kill:constraint=...`,
  `Out of memory: Killed process ... (mariadbd) ...`, with ordinary lines before and after) gives exactly one report
  record: program `kernel`, pid 0, the time of its first line, `Message` = the member messages joined by `\n` in file
  order, `Truncated = false`; the lines before and after are separate records.
- [ ] AC-K2 The same report as journal export entries (`SYSLOG_IDENTIFIER=kernel`, `PRIORITY=4` for the start, `3` for
  the kill line) gives one record with `Priority` = the lowest member priority (3).
- [ ] AC-K3 Lines of another program, and kernel lines of another host, written inside the report are emitted as their
  own records (before the report record, so the emitted order is deterministic but not file order) and do not end the
  report.
- [ ] AC-K4 Bounds: a report whose joined message exceeds 16,384 bytes yields a message of at most 16,384 bytes that
  begins with the first member line, contains the line `[N lines omitted]` with the exact count, ends with the kill
  line, and has `Truncated = true`. The budgets (`HeadBytes`, the total of 16,384, the marker line) count UTF-8 bytes of
  the decoded member messages: a report whose members carry `0xFF` bytes (decoded to U+FFFD, three UTF-8 bytes each),
  from a syslog file and from a journal export, yields a message of at most 16,384 UTF-8 bytes, `Truncated = true`,
  and no "record refused" skip through the validating `RecordingEmitter`. A report without its end line ends, and is
  emitted as it is, at a new start line, at a same-host kernel line more than 60 s after the report's first line, after
  2,000 lines, or at the normal end of input (never when the parse ends by an exception, AC-S7, AC-X1).
- [ ] AC-K5 A `------------[ cut here ]------------` ... `---[ end trace 0000000000000000 ]---` report is grouped the same way.

### Record, wire and storage

- [ ] AC-H1 Go: `model.LogLine.Host` (`json:"host,omitempty"`) validated as short text (at most 1,024 bytes); valid and
  invalid table rows in `internal/model/logline_test.go`; the worst-case size test still fits with `Host` at maximum.
- [ ] AC-H2 The golden batch's `log_line` record carries `"host":"web-1"`; `internal/wire/golden_test.go` passes with the
  regenerated fixture and `WireContractTests` asserts every `log_line` field including `Host`.
- [ ] AC-H3 C#: `LogLine.Host` (JSON `host`) validated as short text (`PayloadValidationTests`); the decoder accepts a
  `log_line` without `host` (empty host). Whether the C# serializer writes an empty `host` is not specified, the same as
  for `program` today (Lead decision 2026-10-09: the C# side only decodes the wire format; the Go encoder omits it).
- [ ] AC-H4 Storage: schema version 4; `Host` is written and read back; a database at version 3 is migrated (its log
  lines get an empty host); the existing migration test from version 2 ends at the current version.

### Configuration and wiring

- [ ] AC-C1 `import.time_zone`: no default (`null` when the key is absent, also with an `import:` section that is null
  or only holds comments); accepted: `UTC`, `Etc/UTC`, `Europe/Berlin`; refused with the error `import.time_zone: must be
  a time zone of the IANA time zone database, such as UTC or Europe/Berlin` (file and line as for every key, never the
  value): empty, `Europe/Nowhere`, `+01:00`, `W. Europe Standard Time`, `europe/berlin` (IDs compared ordinally); a null
  value (`time_zone: ~`) is refused as "has no value" like every string option. `Keys()` lists it. The repository
  example `deploy/backend/vandoxd.yaml` holds it commented out (`  # time_zone: Europe/Berlin`), so it loads with
  `TimeZone == null`; the existing test `BackendConfigLoaderLoadsRepositoryExample` (every key of `Keys()` set
  explicitly) is adapted by the **Tester**: it exempts `import.time_zone`, asserts `null` for it and asserts that the
  example text contains the commented line.
- [ ] AC-C2 `BuiltInParsers.Create("UTC")` and `BuiltInParsers.Create(null)` return `journal`, then `syslog`;
  `new ParserRegistry(...)` accepts them; an unknown zone throws `ArgumentException` without the zone text.
- [ ] AC-C3 Detection matrix through the registry of the built-in list: a journal export head → `journal`, a
  traditional and an RFC 3339 syslog head → `syslog`, a binary journal head and prose → none; neither parser returns
  more than `NoMatch` for the other's sample.
- [ ] AC-C4 `vandoxd import` without a `Parsers` hook imports a `syslog` file and a journal export (summary "imported",
  records with source types `syslog` and `journal`), and with `import.time_zone: Europe/Berlin` stores a traditional
  `Jul  1 12:00:00` line at 10:00Z. Without `import.time_zone`, a run over a traditional syslog file and a journal
  export imports the export, lists the syslog file as failed with the reason `import.time_zone is not set` and exits
  with 1; a second run over the same root with `import.time_zone: Europe/Berlin` imports the syslog file (outcome
  imported, the line at 10:00Z) and lists the export as already imported.
- [ ] AC-D1 Determinism: parsing the same content and `LogFile` twice with each parser yields equal record sequences
  (the import resumes by count).
- [ ] AC-X1 Both parsers honor cancellation (`OperationCanceledException`) and pass on an exception of the emitter.
  Neither flushes an open kernel report on such an exit: with a report open, cancelling the token, or an emitter that
  throws on the record of an `sshd` line written inside the report, ends `ParseAsync` with that exception and
  `RecordAsync` is not called again afterwards (the `RecordingEmitter` counts the calls).

### Bounded memory (heap-bound tests, *Memory claims* of the Tester charter)

Each test fails when the claim is false. Input is produced lazily by the test helper `PatternStream` (a header, then a
repeated byte pattern up to a given length, reads completing synchronously), so the input itself is never in memory.
Allocation is measured with `GC.GetTotalAllocatedBytes(precise: true)` before and after the parse, retention with
`GC.GetTotalMemory(forceFullCollection: true)`; the test assemblies run without parallelization (no `Parallelize`
attribute in `tests/`, checked 2026-10-09), so the process-wide counters are not disturbed by other tests. Each test
stays under the 10 s runtime budget.

Lead decisions 2026-10-09 (after tests-first):

- The retention measurement needs a full collection, which Sonar S1215 flags. It stays in one private helper
  (`KernelReportGrouperTests.RetainedBytes()`) with `#pragma warning disable S1215` / `restore` around the single call,
  as `SqliteStoreWriteTests` does for CA2100. The dotnet analyzer gate counts only non-suppressed SARIF results, so the
  changed file stays free of diagnostics; no other suppression is allowed in this change.
- The 64 MiB inputs stay: they separate the 8 MiB bound from a buffering implementation by a wide margin. The binding
  budget is 10 s per test, checked in the green run of step 6 (test durations from the TRX/console output); only a test
  over that budget may be reduced, by the Tester, to an input of at least 32 MiB with the 8 MiB bound unchanged.

- [ ] AC-M1 `JournalExportReader` over one entry with a 64 MiB binary field of an unkept name, then `MESSAGE` and
  `__REALTIME_TIMESTAMP`: the entry is read, and the parse allocates less than 8 MiB (a reader that buffers the field
  allocates at least 64 MiB).
- [ ] AC-M2 `JournalExportReader` over a 64 MiB text value without `\n`, once under an unkept name and once as `MESSAGE`:
  the entry is skipped with "truncated entry", and the parse allocates less than 8 MiB.
- [ ] AC-M3 `JournalExportReader` over an entry whose binary `MESSAGE` declares the length 2^62 and is followed by 100
  bytes: the entry is skipped with "truncated entry", the parse ends without an exception, and it allocates less than
  8 MiB (the declared length never sizes an allocation).
- [ ] AC-M5 `JournalExportReader` resynchronizing after a malformed field (header `bad-name=x\n`, then 64 MiB without an
  empty line, `[DataRow]` per pattern: 1,023 bytes `a` and `\n` repeated, so many lines but no empty one; and `a` only,
  without any `\n`): the entry is skipped with "malformed field" exactly once, no record and no other skip follow, the
  parse ends at the end of input without an exception, and it allocates less than 8 MiB (the resync scans in the reused
  buffer and never collects the skipped bytes).
- [ ] AC-M4 `KernelReportGrouper` fed a report of 2,000 kernel lines of 16 KiB each (a start line, then lines without an
  end line; each record built per line and dropped by the test, `ready` cleared after each `Add`): the retained memory
  measured after line 1,999, with the grouper kept alive, grows by less than 1 MiB over the value before the first
  `Add` (head, tail and one line are about 64 KiB of UTF-16; retaining the lines would be at least 32 MiB); line 2,000
  closes the report into one record of at most 16,384 UTF-8 bytes.

### Storable range and text limits

- [ ] AC-T1 `StorableTime.Min` is 1677-09-21T00:12:43.1452242Z and `StorableTime.Max` 2262-04-11T23:47:16.8547758Z
  (the int64-nanosecond range of storage); `StorableTime.Contains` is `true` at both bounds and `false` one tick outside;
  `StorageTime.InStorableRange` and `IsOutsideStorableRange` give the same answers for those four instants (new
  `tests/Vandox.Storage.Tests/StorageTimeTests.cs`; the existing storage tests stay green).
- [ ] AC-T2 `Utf8Text.Decode`: valid UTF-8 within the limit is returned unchanged with `truncated = false`; invalid
  bytes become U+FFFD; the result never exceeds the limit in UTF-8 bytes and never ends in half a surrogate pair
  (`[DataRow]`s: 16,384 bytes `0xFF` with limit 16,384; a 4-byte character straddling the limit; limit 0).
  `Utf8Text.Cut` gives the same cut for a string.

## Accepted forms

The parsers' behavior on every input form (Security reviews this list).

### Journal export

| Input | Result |
| ----- | ------ |
| Text field `NAME=value\n` | value is everything up to `\n`, nothing stripped (also no `\r`) |
| Binary field `NAME\n` + uint64 LE length + bytes + `\n` | value = the bytes; a byte other than `\n` after them → "malformed field" |
| Length ≥ 2^63, or more than the remaining input | "truncated entry", parse ends |
| Field name: 1-64 bytes of `A-Z 0-9 _`, not starting with a digit | accepted (address fields `__*` included) |
| Any other field name (lower case, `-`, space, empty, over 64 bytes, non-ASCII) | "malformed field"; resynchronize after the next empty line, scanning in the reused buffer (bounded memory, AC-M5); the end of input while resynchronizing ends the parse with no further skip |
| A value longer than its raw keep bound (16,384 bytes for `MESSAGE`, 1,024 for the short fields, 32 for the numeric ones) | the first bytes up to the bound (at a UTF-8 boundary) are kept, the rest read in a reused buffer and discarded; `Truncated` for `MESSAGE` and the short fields; an over-long numeric value is invalid |
| A kept text whose decoded form exceeds its limit in UTF-8 bytes (invalid bytes grow to three bytes each) | cut at a character boundary to the limit (*Text limits and time range*), `Truncated` |
| Text value ended by the end of input instead of `\n` | "truncated entry", parse ends |
| Empty line(s) | end the entry; repeated ones ignored |
| EOF inside an entry made of whole fields | the entry is parsed |
| Repeated field | first value wins |
| `__REALTIME_TIMESTAMP` | 1-20 ASCII decimal digits, > 0, at most 9,223,372,036,854,775 µs (the storable maximum), accumulated with a bound check before every step (no `checked` overflow, no throwing parse); else "invalid __REALTIME_TIMESTAMP" |
| `PRIORITY` | exactly one digit 0-7, else `null` |
| `_PID` / `SYSLOG_PID` | 1-10 decimal digits, 1 to 2,147,483,647, else ignored (fall-through to the next source, then 0) |
| Kept value invalid UTF-8 | decoded with U+FFFD |
| Fields other than the eight kept ones | read and discarded in bounded chunks |
| UTF-8 BOM at the start | not a journal export (detection `NoMatch`) |

### Syslog line

`[<PRI>]TIMESTAMP SP HOST SP [TAG] MESSAGE`; a UTF-8 BOM is removed from line 1 only, `\r\n` handled by `LogLineReader`.

| Part | Accepted | Otherwise |
| ---- | -------- | --------- |
| `<PRI>` | optional; `<` 1-3 digits `>`, 0-191; priority = PRI mod 8 | "not a syslog line" |
| Traditional time | `Mmm` (exactly `Jan`...`Dec`) SP day (1-31, a one-digit day preceded by a second space, or two digits `01`-`31`) SP `HH:MM:SS` (00-23, 00-59, 00-59) | "not a syslog line" |
| RFC 3339 time | `YYYY-MM-DDTHH:MM:SS`, optional `.` and 1-9 digits (kept to 100 ns), then `Z` or `±HH:MM` up to ±14:00; upper-case `T`/`Z` only | "not a syslog line" |
| RFC 3339 instant outside the storable range (year 0000 included) | — | "time outside the storable range" |
| Separator | exactly one space after the time and after the host | "not a syslog line" |
| `HOST` | 1-255 UTF-8 bytes of the decoded line (an invalid byte counts as the three bytes of its U+FFFD), without space; never cut, the limit is below the model's 1,024 | "not a syslog line" |
| `TAG` | next token matching `PROGRAM[PID]:` or `PROGRAM:`, `PROGRAM` 1-128 UTF-8 bytes of the decoded line (counted as for `HOST`, never cut) without space, `[`, `]`, `:`; `PID` 1-10 digits (over 2^31-1 → 0) | no tag: program empty, pid 0, message = rest after host |
| `MESSAGE` | rest of the line after `:` and at most one space; may be empty; invalid UTF-8 → U+FFFD; control characters kept; cut to 16,384 UTF-8 bytes (`Truncated`) | — |
| Feb 29 / day beyond the month in the resolved (or RFC 3339) year | — | "invalid date" |
| Year-less time whose inferred year or instant is outside the storable range | — | "time outside the storable range" |
| Year-less time without a usable anchor | — | "year unknown: the file has no usable date" |
| Empty line | — | "empty line" |
| RFC 5424 (`<PRI>1 TIMESTAMP ...`) | not supported | "not a syslog line" (the `1` is not a month) |

Parsing is hand-written, linear in the line length. A regular expression, if one is used at all, is constructed with
`RegexOptions.NonBacktracking` (as a `Regex` field built once, or `GeneratedRegex` with that option if the generator
accepts it — not verified here); no backtracking regular expression anywhere in the parsers.

### Text limits and time range

These rules hold for both parsers and the grouper, so a record they build never fails `DataRecord.Validate()` or
`RecordRules.CheckImportRecord` ("record refused: ...") because of the input:

- **Text limits count UTF-8 bytes of the decoded text.** `Check.Length` (`src/Vandox.Core/Model/Check.cs:130`)
  counts `Encoding.UTF8.GetByteCount` of the string, and every invalid byte decodes to U+FFFD (three UTF-8 bytes), so a
  raw bound alone lets 16,384 bytes `0xFF` grow to 49,152. Every kept text is decoded with replacement and then cut at
  a character (rune) boundary to its limit with `Utf8Text` (the same algorithm as `PathText.Cut`,
  `src/Vandox.Import/PathText.cs:49-70`, which lives in `Vandox.Import` and is not reused from `Vandox.Core`): `Message`
  16,384 (`ModelLimits.MaxTextBytes`), `Host` and `Program` 1,024 (`ModelLimits.MaxShortTextBytes`; the syslog parser's
  syntactic limits of 255 and 128 UTF-8 bytes lie below it, so a syslog `Host` or `Program` is accepted whole or the
  line is "not a syslog line" / has no tag, never cut). A cut sets
  `Truncated`. The raw read bounds stay as well (the line reader's 16 KiB, the journal reader's keep bounds), so a
  decoded string never holds more characters than the raw bound. The grouper measures `HeadBytes`, the total and the
  marker line in UTF-8 bytes of the decoded member messages.
- **Every instant stays inside the storable range** of storage (int64 nanoseconds since the Unix epoch:
  1677-09-21T00:12:43.1452242Z to 2262-04-11T23:47:16.8547758Z), now defined once in `Vandox.Core` as `StorableTime`
  (`StorageTime` in `Vandox.Storage` delegates its two range checks to it). Before a `DateTimeOffset`, `DateTime`,
  `LocalDate`, `LocalDateTime` or `Instant` is built from input, its year is checked as an integer against 1677-2262
  and its day against the days of that month; the built instant is then checked with `StorableTime.Contains`. Outside:
  the line is skipped with "time outside the storable range" (syslog) or "invalid __REALTIME_TIMESTAMP" (journal).
  An anchor (name date or modification time) outside the range is not usable. The inferred year stops advancing once it
  has left the range.
- **Predecessor (year advance and repeated hour).** The year-advance rule compares a year-less line with the last
  year-less line before it in the file that reached `SyslogClock.Resolve`, whatever its outcome — a line skipped as
  "invalid date" or "time outside the storable range" counts, with its inferred year and its month, day and time as
  written — while lines that are not syslog lines, empty lines and RFC 3339 lines are never predecessors and leave it
  unchanged; the repeated-hour rule compares with the last instant `Resolve` returned successfully (a skipped line has
  none and leaves it unchanged), likewise ignoring RFC 3339 lines. Rows in AC-S4 and AC-S5.
- **No exceptions for out-of-range dates.** No parser path throws for an out-of-range date: `ImportPass.JudgeAsync`
  (`src/Vandox.Import/ImportPass.cs:470`) would otherwise report the exception's own message, which is not a fixed text.
- **NodaTime only through non-throwing calls:** local times are mapped with `DateTimeZone.MapLocal` and the mapping's
  `Count` (0: skipped hour, shifted forward by the gap; 1; 2: repeated hour, AC-S5 rule) handled in code; never
  `InZoneStrictly`, `AtStrictly`, `InZoneLeniently` on unchecked values, or a resolver that throws.

## Approach

1. **Record:** add `host` to `log_line` (Go model and validation, C# model and validation), regenerate the golden batch,
   add schema step 4 (`ALTER TABLE log_lines ADD COLUMN host TEXT NOT NULL DEFAULT ''`) and write/read the column. The
   wire version stays 1.0 because no version has been released (record 0084).
2. **Time zone:** `import.time_zone` in `BackendConfig` (`ImportConfig`), no default, validated when set by
   `SourceTimeZone.Find` against NodaTime's TZDB IDs (ordinal); NodaTime 3.3.5 via central package management in
   `Vandox.Core` (record 0085). Unset, the syslog parser fails a file at its first year-less line (AC-S7) instead of
   guessing a zone: the import cannot be redone once stored, and the failure is visible (outcome failed, exit 1) and
   repaired by a re-run after setting the option.
3. **Syslog:** `SyslogLine.TryParse` (header, digits only, no date built), `SyslogTime.TryGetInstant` (RFC 3339 time,
   range-checked), `SyslogClock` (anchor, year, zone, DST, range-checked), `SyslogParser` (reads lines with
   `LogLineReader`, decodes and cuts with `Utf8Text`, builds records, passes them through the grouper). Shared helpers
   in `Vandox.Core`: `StorableTime` (the storable range, used by `StorageTime` too) and `Utf8Text` (decode with
   replacement, cut to a UTF-8 byte limit) — *Text limits and time range*.
4. **Journal:** `JournalExportReader` (bounded entry reader keeping eight fields), `JournalExportParser` (maps an entry,
   passes records through the grouper).
5. **Kernel reports:** `KernelReportGrouper` works on `DataRecord`s with a `LogLine` payload: a `kernel` line whose message
   contains `invoked oom-killer:` or `------------[ cut here ]------------` opens a report; same-host `kernel` lines join
   it; it closes after a line containing `Out of memory: Killed process`, `Memory cgroup out of memory: Killed process`,
   `Out of memory and no killable processes` (OOM) or `---[ end trace ` (cut here), or by the bounds of AC-K4. The
   message keeps a head of up to `HeadBytes` (8,192) and a tail of the last whole lines that fit into the rest of 16,384
   bytes minus the marker line, all counted in UTF-8 bytes of the decoded text; a line longer than its budget is cut at
   a character boundary (`Utf8Text.Cut`). Memory: head + tail + one line, independent of the report length (AC-M4).
   Priority = lowest member priority, `Truncated` if cut or any member was. `Finish` is called by a parser only when its
   input ended normally; a parse that ends by an exception (cancellation, an emitter error, AC-S7) drops the open
   report, because the importer resumes by dropping the first `state.Records` emitted records
   (`_drop`, `src/Vandox.Import/RecordEmitter.cs:45`, used in `RecordAsync`), so the records emitted before a failure must be a prefix
   of what a later complete parse emits; a report flushed early would not be.
6. **Wiring:** `BuiltInParsers.Create(timeZone)` returns `[new JournalExportParser(), new SyslogParser(zone)]`;
   `ImportCommand` uses it when `hooks.Parsers` is `null`, inside the existing `ArgumentException` handling.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go | `internal/model/logline.go` | `Host` field and validation |
| Go | `testdata/wire/all-kinds.jsonl` | regenerated (Tester, with the test sample) |
| Vandox.Core | `Model/LogLine.cs` | `Host` property and validation |
| Vandox.Core | `Configuration/ImportConfig.cs` (new), `BackendConfig.cs`, `BackendConfigLoader.cs` | option `import.time_zone` |
| Vandox.Core | `LogParsing/SourceTimeZone.cs`, `BuiltInParsers.cs`, `JournalExportParser.cs`, `JournalExportReader.cs`, `JournalEntry.cs`, `SyslogParser.cs`, `SyslogLine.cs`, `SyslogTime.cs`, `SyslogClock.cs`, `KernelReportGrouper.cs` (all new) | parsers |
| Vandox.Core | `LogParsing/Utf8Text.cs`, `Model/StorableTime.cs` (both new) | decode and cut to UTF-8 limits; storable range |
| Vandox.Core | `Vandox.Core.csproj`; `Directory.Packages.props` | `PackageReference Include="NodaTime"`; `PackageVersion Include="NodaTime" Version="3.3.5"` |
| Vandox.Storage | `StorageLimits.cs`, `SchemaMigrator.cs`, `BatchWriter.cs`, `RecordQueries.cs` | schema 4, `host` column |
| Vandox.Storage | `StorageTime.cs` | `InStorableRange` / `IsOutsideStorableRange` delegate to `StorableTime.Contains` (behavior unchanged) |
| Vandox.Backend | `Cli/ImportCommand.cs`, `Hosting/ServeHooks.cs` | built-in parsers when no hook; doc comment of `Parsers` |
| Vandox.Core | `LogParsing/IRecordEmitter.cs`, `LogParsing/ILogParser.cs` | doc comments: deterministic order instead of file order |
| deploy | `deploy/backend/vandoxd.yaml` | `import:` with `# time_zone: Europe/Berlin` commented out and a comment (no default; required for traditional syslog files); header sentence "the values below are the defaults" names the exception |

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
}
```

C# (namespaces as the folders; every type with XML docs and regions):

```csharp
// Vandox.Core.Model.LogLine — new property
[JsonPropertyName("host")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
public string Host { get; set; } = string.Empty;

// Vandox.Core.Configuration
public sealed class ImportConfig
{
    [ConfigKey("time_zone")]
    public string? TimeZone { get; set; }                       // no default; null when the key is absent
}
// BackendConfig — new property; Keys() appends "import.time_zone" after "log.level"
[ConfigKey("import")]
public ImportConfig Import { get; } = new();

// Vandox.Core.Model
public static class StorableTime
{
    public static readonly DateTimeOffset Min;                  // 1677-09-21T00:12:43.1452242Z (ticks 529122247631452242, UTC)
    public static readonly DateTimeOffset Max;                  // 2262-04-11T23:47:16.8547758Z (ticks 713589688368547758, UTC)
    public static bool Contains(DateTimeOffset instant);        // Min <= instant <= Max, compared by UtcTicks
}

// Vandox.Core.LogParsing
internal static class Utf8Text
{
    internal static string Decode(ReadOnlySpan<byte> bytes, int limit, out bool truncated);  // decode with U+FFFD, then Cut
    internal static string Cut(string text, int limit, out bool truncated);                  // at most limit UTF-8 bytes, rune boundary
}

internal static class SourceTimeZone
{
    internal static DateTimeZone? Find(string name);            // ordinal match against DateTimeZoneProviders.Tzdb.Ids
}

public static class BuiltInParsers
{
    public static IReadOnlyList<ILogParser> Create(string? timeZone);  // null: syslog parser without zone; ArgumentException for an unknown zone
}

public sealed class JournalExportParser : ILogParser
{
    public const string ParserType = "journal";
    public string Type { get; }
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head);
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken);
}

public sealed class SyslogParser : ILogParser
{
    public const string ParserType = "syslog";
    public const string TimeZoneNotSet = "import.time_zone is not set";   // message of the InvalidOperationException (AC-S7)
    public SyslogParser(DateTimeZone? timeZone);                          // null: year-less lines fail the file
    public string Type { get; }
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head);
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken);
}

internal sealed class JournalEntry
{
    internal string? Realtime { get; set; }          // __REALTIME_TIMESTAMP
    internal string? Hostname { get; set; }          // _HOSTNAME
    internal string? SyslogIdentifier { get; set; }  // SYSLOG_IDENTIFIER
    internal string? Comm { get; set; }              // _COMM
    internal string? Pid { get; set; }               // _PID
    internal string? SyslogPid { get; set; }         // SYSLOG_PID
    internal string? Priority { get; set; }          // PRIORITY
    internal string? Message { get; set; }           // MESSAGE
    internal bool Truncated { get; set; }            // MESSAGE (or a kept short field) was cut
    internal string? Problem { get; set; }           // fixed skip reason when the entry is malformed or cut off
}

internal sealed class JournalExportReader
{
    internal const int MaxFieldNameBytes = 64;
    internal JournalExportReader(Stream input);
    internal ValueTask<JournalEntry?> ReadAsync(CancellationToken cancellationToken);   // null after the last entry
}
// Reader contract (Lead decision 2026-10-09): a skipped entry is returned as a JournalEntry with Problem set to
// "malformed field" or "truncated entry" (other fields unspecified); after "truncated entry" the next call returns
// null; after "malformed field" reading resumes after the next empty line (null if the input ends first). The checks
// for a missing/invalid __REALTIME_TIMESTAMP and a missing MESSAGE belong to the parser, not the reader. An empty
// `MESSAGE=` is a present, valid MESSAGE (empty message).

internal readonly record struct SyslogTime(int Year, int Month, int Day, int Hour, int Minute, int Second, int FractionTicks, int? OffsetMinutes)
{
    internal bool HasYear => OffsetMinutes is not null;                 // RFC 3339; Year is 0 for a traditional time
    internal string? TryGetInstant(out DateTimeOffset instant);         // RFC 3339 only; null on success, else "invalid date" or
                                                                        // "time outside the storable range"; never throws
}
// Only the parsed digits; no date or instant is built by SyslogLine.TryParse

internal sealed class SyslogLine
{
    internal byte? Priority { get; init; }
    internal SyslogTime Time { get; init; }
    internal string Host { get; init; } = string.Empty;
    internal string Program { get; init; } = string.Empty;
    internal int Pid { get; init; }
    internal string Message { get; init; } = string.Empty;
    internal static SyslogLine? TryParse(string line);   // null: not a syslog line
}

internal sealed class SyslogClock                       // created by SyslogParser only when a zone is set
{
    internal static readonly TimeSpan BackwardTolerance = TimeSpan.FromMinutes(10);   // repeated-hour rule (AC-S5)
    internal SyslogClock(DateTimeZone timeZone, LogFile file);
    internal bool HasAnchor { get; }                     // a name date or modification time inside the storable range
    internal string? Resolve(SyslogTime time, out DateTimeOffset instant);
    // year-less time -> UTC instant; null on success, else the fixed reason "invalid date",
    // "time outside the storable range" or "year unknown: the file has no usable date"; never throws
}

internal sealed class KernelReportGrouper
{
    internal const int MaxLines = 2000;
    internal const int HeadBytes = 8192;                // UTF-8 bytes of the decoded text
    internal static readonly TimeSpan MaxSpan = TimeSpan.FromSeconds(60);
    internal void Add(DataRecord record, List<DataRecord> ready);   // appends the records to emit now, in order
    internal void Finish(List<DataRecord> ready);                   // flushes an open report; called only at the normal end of input
}
```

Existing files the skeleton rewrites: `ImportCommand.cs` keeps its logic until step 6 (the skeleton only adds types);
`LogLine.cs`, `BackendConfig.cs` and `logline.go` get the new members in the skeleton (they compile without behavior).
`StorageTime.cs` keeps its own range checks until the implementation (task 8) makes them delegate to `StorableTime`, so
the skeleton's throwing `StorableTime` does not break the existing storage tests.
Doc comments only, changed by the Dev with the grouper (task 12): `IRecordEmitter.RecordAsync` ("Takes the next record
in file order", `src/Vandox.Core/LogParsing/IRecordEmitter.cs:13`) becomes "Takes the next record, in the parser's
deterministic order (file order except where the parser combines lines into one record)"; the `ParseAsync` summary in
`ILogParser.cs:39` says "in a deterministic order" instead of "in order" (the type summary already only requires "the
same records in the same order"). No caller depends on file order (a search for "file order" and "in order" under
`src/` finds only these two comments and unrelated configuration and schema comments): the importer counts records
for resume and stores them as they come.

## Test files

New (`tests/Vandox.Core.Tests/`): `JournalExportParserTests.cs`, `JournalExportReaderTests.cs`, `SyslogParserTests.cs`,
`SyslogLineTests.cs`, `SyslogClockTests.cs`, `KernelReportGrouperTests.cs`, `BuiltInParsersTests.cs` (AC-C2, AC-C3),
`SourceTimeZoneTests.cs`, `Utf8TextTests.cs` (AC-T2), `StorableTimeTests.cs` (AC-T1); helpers `RecordingEmitter.cs`
(records and skips, call count; optional exception on the n-th record; validates every record with
`DataRecord.Validate()` and `StorableTime.Contains`, recording a failure as the skip "record refused: ..." like the
importer), `JournalExportBuilder.cs` (text and binary fields as bytes) and `PatternStream.cs` (lazily generated input
for the heap-bound tests). The heap-bound tests (AC-M1-M3, AC-M5) go into `JournalExportReaderTests.cs`, AC-M4 into
`KernelReportGrouperTests.cs`. Fixture: `testdata/logs/kern.log-oom`.

New (`tests/Vandox.Storage.Tests/`): `StorageTimeTests.cs` (AC-T1, the delegation).

Extended: `tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs` (AC-C1), `PayloadValidationTests.cs` (AC-H3),
`WireContractTests.cs` (AC-H2); `tests/Vandox.Storage.Tests/SqliteStoreWriteTests.cs` (host round trip) and
`SqliteStoreOpenTests.cs` (AC-H4), `Samples.cs` (sample log line gets a host); `tests/Vandox.Backend.Tests/ImportCommandTests.cs`
(AC-C4); Go `internal/model/logline_test.go`, `internal/wire/encode_test.go` (golden sample and worst case, AC-H1/H2) and
the regenerated `testdata/wire/all-kinds.jsonl`.

Existing test code affected by a changed signature: none — every change adds members. One existing **assertion**
changes meaning: `SqliteStoreOpenMigratesOlderSchema` (`SqliteStoreOpenTests.cs:155`) expects the literal `"3"` after
migrating; the **Tester** changes it in step 5 to the current version (it fails once step 6 raises the schema to 4).
A second one: `BackendConfigLoaderLoadsRepositoryExample` (`BackendConfigLoaderTests.cs:23-44`) requires every key of
`Keys()` to be set in the example file; the **Tester** adapts it in step 5 as AC-C1 states (the option without a
default is exempt and must appear commented out).

## Areas

- `docs/areas/log-import.md` — new section *System log parsers* (the two formats, the field mapping including `log` as
  the listed path with its rotation suffix, the accepted forms in prose, year and zone rules with the name date before
  the modification time and the 10-minute tolerance of the repeated hour, the failure "import.time_zone is not set" and
  the re-run that completes the file, kernel reports and their bounds (in UTF-8 bytes of the decoded text; an open
  report is emitted only at the normal end of input, never when the parse fails), the text limits in UTF-8 bytes of the
  decoded text, the storable time range and the skip reasons "time outside the storable range" and "year unknown: the
  file has no usable date", the built-in list and its order, and that
  Ubuntu's rsyslog writes kernel lines to both `syslog` and `kern.log`, so importing both stores them twice); *Parsers*:
  determinism "for the same content, file and configuration", and records "in a deterministic order: file order, except
  that a parser that combines lines into one record may emit records of lines written inside it first"; *Command*:
  `import.time_zone`.
- `docs/areas/wire-format.md` — `log_line`: `host` (optional short text); `log`: "`journal`, or the file path: the
  absolute path for the agent, the path as the import lists it (relative to the import root or archive, rotation suffix
  included) for imported records".
- `docs/areas/storage.md` — schema version 4, `log_lines.host`.
- `docs/areas/configuration-and-secrets.md` — option `import.time_zone` (no default, accepted values, error text, what
  happens when it is unset); the example-file rule becomes "set every option explicitly (an optional one at its default;
  an option without a default commented out with an example value)".

## Documentation updates

All by the **Dev** (one owner per edit):

- `README.md`: *Backend options* row `import.time_zone` (default: none; required to import traditional syslog files);
  *Import logs*: the supported sources (journal export, syslog and kern.log with rotations), how to export a binary
  journal, copying with preserved times (`cp -a`, `tar`), the time zone option and that a syslog file fails with
  "import.time_zone is not set" until it is set (a re-run then completes it), that `syslog` and `kern.log` hold the same
  kernel lines, and the last paragraph's "until then ... recognizes no file" replaced.
- The four area documents above.
- `docs/ARCHITECTURE.md`: line 10 ("without a parser yet") and the `Vandox.Core` component entry name the parsers;
  *Storage and retention* names schema version 4; links to 0084, 0085, 0086 in the matching *Records* lists.
- `.squad/project.md`: *Security areas* 10 names `JournalExportParser`, `JournalExportReader`, `SyslogParser`,
  `SyslogLine`, `SyslogClock`, `KernelReportGrouper` and `import.time_zone`; *Test doubles* row "log parsers (C#)" adds
  `RecordingEmitter`, `JournalExportBuilder` and `PatternStream`.

The Lead edits only record status and the index at approval.

## Architecture check

- *Backfilled data never raises an alert*: imported records keep origin `import` (validated by `RecordRules.CheckImportRecord`).
- *No data gaps unless recorded*: every input line is a record, part of a report record or a counted skip with a reason;
  the omitted middle of an over-long report is stated in the record itself (`[N lines omitted]`, `Truncated`).
- Repeatable import (0069): parsing depends only on content, `LogFile` (first import's name and time on resume) and
  `import.time_zone`; changing the option between an interrupted run and its resume shifts the rest of that file — stated
  in the area document and record 0085. Setting it after a failure "import.time_zone is not set" shifts nothing: the
  records stored before the failure come from RFC 3339 lines, which do not use the zone, and an open kernel report is
  not flushed by the failure (AC-S7), so the records stored are a prefix of what the completing run emits and the
  resume by count stays exact.
- *No data gaps unless recorded*, for hostile input: an over-long or invalid-byte text is cut and marked `Truncated`,
  never refused by the record rules; an out-of-range time is a counted skip with a fixed reason, never an exception.
- *No data gaps unless recorded*, for the unset option: the file is listed as failed with the reason, never imported
  with guessed times.
- Wire versioning (0043): `host` is an additive optional field; the version stays 1.0 because nothing is released (0084).
- Storage (0063, 0077): a new migration step, no change to deduplication or the FTS invariant.

## Security considerations

- Area 10 (parsing): memory per parse is bounded by constants (one line of 16 KiB plus a head/tail report buffer of 16 KiB;
  the journal reader keeps eight fields of at most 16 KiB / 1 KiB and reads everything else in a reused buffer), each
  claim pinned by a heap-bound test (AC-M1-M5); a declared binary length never sizes an allocation; text limits count
  UTF-8 bytes of the decoded text, so hostile bytes are cut, not refused (*Text limits and time range*); dates are
  range-checked as integers before any time value is built and NodaTime is used only through non-throwing calls, so
  no unexpected exception text becomes a failure reason; parsing is linear (hand-written, or `RegexOptions.NonBacktracking`
  only); every loop reads input or ends;
  cancellation is checked per line or field read; hostile lengths end the parse with a counted skip, never an exception.
- Area 12 (display): messages are stored unchanged, control characters included (0021); skip reasons and the failure
  reason `import.time_zone is not set` are fixed texts; the configuration error never echoes the zone value.
- Dependency: NodaTime 3.3.5 (Apache-2.0; its nuspec lists no dependency for `net8.0`, checked 2026-10-09), pinned in
  `Directory.Packages.props`, covered by the NuGet vulnerability check; it reads its embedded zone data, no file or network.
- Wire: the C# decoder reads `host` under the short-text bound; the Go encoder's worst-case record size still fits (AC-H1).

## Decision records

- `docs/decisions/0084-log-line-record-gets-an-optional-host-field.md` (Proposed)
- `docs/decisions/0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md` (Proposed)
- `docs/decisions/0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md` (Proposed)

## Out of scope / follow-ups

- Event extraction from the records (OOM victim and so on): #21. The other log formats: #17-#20.
- Binary journal files, RFC 5424 files, cross-source de-duplication: not planned; reopen as issues if needed.
- For #37 (the agent's line parsing), to be filed or noted by the orchestrator: Go's `checkLen`
  (`internal/model/model.go:211`) counts `len(s)`, the raw bytes, while `encoding/json` (the Go encoder) replaces
  invalid UTF-8 with U+FFFD (documented behavior of `encoding/json`, not exercised here), so `Host`, `Program` and
  `Message` of an agent record with invalid bytes can grow up to three times on the wire and be refused by the C#
  decoder's UTF-8 byte limits. The agent must cut on the UTF-8 encoding of the replaced text, as the backend parsers do
  here. `Host` gets the same raw check in this change (AC-H1) only because no agent code produces log lines yet.

## Challenge

Devil's Advocate, 2026-10-09: one major and five minor objections, all accepted. The tier stays `security`; the
scope is unchanged.

1. **Major — the default `UTC` mis-times year-less lines silently, and an import cannot be redone.** Accepted. Records do
   not reference their file and imported content is never imported again (*Repeatable import* in
   `docs/areas/log-import.md`), so a wrong zone would be permanent and unnoticed, which also contradicts AC-S4's rule of
   skipping rather than guessing. `import.time_zone` now has no default (AC-C1); with the option unset the syslog parser
   fails a file at its first year-less line with the fixed reason `import.time_zone is not set` (new AC-S7). The
   mechanism exists: a parser exception fails the file after storing what was parsed before it, and the incomplete file
   is resumed by count on the next run (verified, see *Problem / root cause*); AC-C4 now covers the failed run and the
   completing re-run. RFC 3339 files and journal exports do not need the option. The example file holds the option
   commented out, so `BackendConfigLoaderLoadsRepositoryExample` changes (Tester). Record 0085 lists the default `UTC`
   as a rejected option.
2. **Minor — the repeated-hour rule switches to the second pass on any small step back.** Accepted. The rule now keeps the
   earlier offset unless it puts the line more than `SyslogClock.BackwardTolerance` (10 minutes) before the previous
   line, with rows for a one-second step back in the first pass and in the second pass (AC-S5). The remaining ambiguity
   (no line between the end of the first pass and a second-pass line within 10 minutes of it) cannot be resolved without
   an offset in the line and is stated in the area document.
3. **Minor — Ubuntu writes kernel lines to both `syslog` and `kern.log`.** Accepted. Stated (from the packaged rsyslog
   configuration, not checked on the server) in 0086's consequences, the *System log parsers* section of the area
   document and the README; the orchestrator is asked to note it on #21 (see the result). No de-duplication here: it is
   the cross-source de-duplication already out of scope.
4. **Minor — `log` holds the listed path with the rotation suffix, not what the agent will send.** Accepted as
   documentation, not normalization: the import cannot know the server's absolute path (the import root may be any
   copy, e.g. `backup/var/log/syslog.1`), and stripping prefixes or rotation suffixes by pattern would guess. AC-S2 pins
   the value; `docs/areas/wire-format.md` defines `log` for both producers; 0086 records the choice with the rejected
   normalization.
5. **Minor — the `-YYYYMMDD` fallback is nearly dead code.** Accepted in the proposed form: the modification time is
   always set by the scanner (`Scanner.cs:267`, `Scanner.cs:514`), but it is the value a plain copy destroys, while the
   rotation date in the name survives it. A valid name date is now preferred over the modification time; an invalid one
   falls back to it (AC-S4, record 0085).
6. **Minor — grouped reports break "next record in file order".** Accepted. The Dev changes the doc comments of
   `IRecordEmitter.RecordAsync` and `ILogParser.ParseAsync` to a deterministic order (*Signatures*), the area document's
   *Parsers* contract says the same, and AC-K3 states the order.

Security (plan review), 2026-10-09, `CHANGES_REQUIRED`: four blocking and three non-blocking points, all accepted. The
tier stays `security`; the scope grows by two small shared helpers (`Utf8Text`, `StorableTime`) and the delegation in
`StorageTime`.

- **B1 — length bounds on raw bytes, while `Check.Length` counts UTF-8 bytes after U+FFFD replacement.** Accepted and
  verified (`src/Vandox.Core/Model/Check.cs:130`; 16,384 bytes `0xFF` decode to 16,384 U+FFFD, 49,152 UTF-8 bytes,
  which `RecordEmitter.RecordAsync` refuses as "record refused: ..."). Every keep limit and every grouper budget now
  counts UTF-8 bytes of the decoded text, cut at a character boundary by `Utf8Text`; the raw read bounds stay
  (*Text limits and time range*). Rows: AC-J4 (text and binary `MESSAGE`, `_HOSTNAME`, `SYSLOG_IDENTIFIER`, `_COMM`),
  AC-S6 (16,384-byte line, host, tag), AC-K4 (members with invalid bytes, both parsers), AC-T2; the
  `RecordingEmitter` validates every record as the importer does.
- **B2 — out-of-range dates throw, and `JudgeAsync` would report the exception's message.** Accepted and verified
  (`src/Vandox.Import/ImportPass.cs:470` returns `error.Message` for exceptions other than I/O ones). The storable range
  moves to `Vandox.Core` (`StorableTime`, because `StorageTime` is internal to `Vandox.Storage`, which `Vandox.Core`
  cannot reference); years and days are checked as integers before any time value is built; anchors outside the range
  are unusable (name date falls back to the modification time, else "year unknown: the file has no usable date", a
  text changed from "... no modification time" because the reason now covers both anchors); out-of-range lines are
  skipped with "time outside the storable range"; the inferred year stops advancing; NodaTime only through
  `MapLocal`. `SyslogTime` now holds digits only and `SyslogClock.Resolve` returns a reason instead of `null`. Rows:
  AC-S3 (a), AC-S4 (b, and c with the exact counts 472 records / 528 skips), AC-J5.
- **B3 — an open kernel report flushed on an exceptional end breaks the resume prefix.** Accepted. `Finish` runs only
  at the normal end of input (*Approach* 5, `KernelReportGrouper.Finish`), stated in AC-S7, AC-K4 and AC-X1; AC-S7
  gains the reviewer's two-run test (OOM start, `sshd`, year-less line).
- **B4 — memory claims without heap-bound tests.** Accepted: AC-M1-M4 (64 MiB unkept binary field, 64 MiB text value
  without `\n`, binary length 2^62 over a short stream, 2,000-line report of 16 KiB lines), with the measurement method
  and the input helper `PatternStream`; tasks 2 and 4 hold them.
- **N1** Accepted: hand-written parsing, or `RegexOptions.NonBacktracking` only (*Accepted forms*).
- **N2** Accepted: AC-J5 rows for `99999999999999999999` and the other overflow forms, with a bound-checked digit loop.
- **N3** Accepted: noted under *Out of scope / follow-ups* for #37; the orchestrator files it (see the result).

Security (plan review, second round), 2026-10-09, `APPROVED` with three non-blocking points, all accepted; tier and
scope unchanged.

- **N1 — AC-S6 contradicted the syntactic limits of `HOST` and `PROGRAM`.** Accepted: one rule, the decoded-byte limits
  of *Accepted forms* (1-255 and 1-128 UTF-8 bytes of the decoded line, an invalid byte counting three). They lie below
  the model's 1,024, so a syslog host or program is never cut. AC-S6 now expects "not a syslog line" or no tag beyond
  the limits, with rows at the exact limit for ASCII and `0xFF` input; *Text limits and time range* states the same.
- **N2 — "predecessor" undefined.** Accepted: *Text limits and time range* defines it (the last year-less line that
  reached `SyslogClock.Resolve`, skipped ones included, for the year; the last resolved instant for the repeated hour;
  RFC 3339, empty and unparsable lines never count). AC-S4 explains why the 472/528 counts depend on it and gains
  predecessor rows (RFC 3339 line in between, skipped `Nov 31`); AC-S5 gains two rows; task 16 and record 0085 state
  the rule.
- **N3 — unbounded resync not pinned.** Accepted: AC-M5 (64 MiB without an empty line after a malformed field, with and
  without line breaks, under 8 MiB allocated), in tasks 2 and 10; the *Accepted forms* row names the bounded resync.
