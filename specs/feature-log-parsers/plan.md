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
- [ ] AC-J4 Bounds: a `MESSAGE` over 16,384 bytes (text and binary form) is cut at a UTF-8 boundary to at most 16,384
  bytes with `Truncated = true`; a kept short field (`_HOSTNAME`, `SYSLOG_IDENTIFIER`, `_COMM`) over 1,024 bytes is cut
  the same way; a 64 MiB binary field of an unkept name is skipped and the next entry is read; a binary length larger
  than the remaining input or at least 2^63 skips the entry with "truncated entry" and ends the parse without an
  exception.
- [ ] AC-J5 Malformed input: a field name that is empty, longer than 64 bytes, starts with a digit or holds a character
  outside `A-Z 0-9 _` skips its entry with "malformed field" and parsing resumes after the next empty line; an entry
  without `__REALTIME_TIMESTAMP`, with a non-decimal or out-of-range one, or without `MESSAGE` is skipped with "entry
  without __REALTIME_TIMESTAMP", "invalid __REALTIME_TIMESTAMP" or "entry without MESSAGE"; a repeated field keeps its
  first value; several empty lines between entries and a last entry without the closing empty line are accepted.
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
  an inferred non-leap year ("invalid date"); each skipped with its 1-based line number, the parse continues.
- [ ] AC-S4 Year (anchor = a valid `-YYYYMMDD` suffix of the base name, before an optional `.gz` already removed by the
  importer, meaning the end of that local day; else `ModTime`): mtime 2026-03-02, `Mar  1` → 2026; mtime 2026-01-03 with
  `Dec 30 ...` then `Jan  2 ...` → 2025-12-30 then 2026-01-02; a first line up to one day after the anchor stays in the
  anchor's year, more than one day after goes to the year before; a line a few seconds earlier than its predecessor does
  not advance the year; a line more than 180 days before its predecessor advances it; Feb 29 resolves in 2028, is skipped
  in 2026; `syslog-20260103` with mtime 2027-05-01 (a plain copy) and `Dec 30 ...` → 2025-12-30 (the name wins);
  `syslog-20260230` (no such date) with mtime 2026-03-02 and `Mar  1` → 2026 (falls back to mtime); without both,
  every year-less line is skipped with "year unknown: the file has no modification time", RFC 3339 lines of the same
  file are still read.
- [ ] AC-S5 Time zones: zone `UTC` keeps the wall time; `Europe/Berlin` `Jul  1 12:00:00` → 10:00Z, `Jan 15 12:00:00` →
  11:00Z; the skipped hour `Mar 29 02:30:00` (2026) → 01:30Z (shifted forward); the repeated hour on 2026-10-25: a local
  time in it takes the earlier offset (+02:00) unless that puts it more than `SyslogClock.BackwardTolerance` (10 minutes)
  before the previous line's instant, then the later offset (+01:00). `[DataRow]` per file order: `02:59:59`,
  `02:00:01`, `02:30:00` → 00:59:59Z, 01:00:01Z, 01:30:00Z; `02:59:59`, `02:59:58`, `02:00:01` → 00:59:59Z, 00:59:58Z,
  01:00:01Z (a step back within the tolerance stays in the first pass); `02:59:59`, `02:10:00`, `02:09:58` → 00:59:59Z,
  01:10:00Z, 01:09:58Z (a step back inside the second pass stays there); the first line of a file in the repeated hour takes the earlier
  offset. New Year in the zone: mtime 2025-12-31T23:30Z (00:30 local on Jan 1) with `Jan  1 00:10:00` →
  2025-12-31T23:10Z; RFC 3339 lines ignore the zone.
- [ ] AC-S6 A line cut by `LogLineReader` (over 16 KiB) gives `Truncated = true`.
- [ ] AC-S7 Zone not set (`new SyslogParser(null)`): a file of RFC 3339 lines is parsed completely; a file whose
  first year-less (traditional) line comes after two RFC 3339 lines emits those two records and then `ParseAsync` throws
  `InvalidOperationException` with the message `SyslogParser.TimeZoneNotSet` (`import.time_zone is not set`, no input
  text); the year-less line is neither emitted nor skipped; a line that is not a syslog line before it is still skipped
  as in AC-S3. The check comes before the anchor: a file without anchor and zone fails the same way.

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
  line, and has `Truncated = true`. A report without its end line ends, and is emitted as it is, at a new start line,
  at a same-host kernel line more than 60 s after the report's first line, after 2,000 lines, or at the end of input.
- [ ] AC-K5 A `------------[ cut here ]------------` ... `---[ end trace 0000000000000000 ]---` report is grouped the same way.

### Record, wire and storage

- [ ] AC-H1 Go: `model.LogLine.Host` (`json:"host,omitempty"`) validated as short text (at most 1,024 bytes); valid and
  invalid table rows in `internal/model/logline_test.go`; the worst-case size test still fits with `Host` at maximum.
- [ ] AC-H2 The golden batch's `log_line` record carries `"host":"web-1"`; `internal/wire/golden_test.go` passes with the
  regenerated fixture and `WireContractTests` asserts every `log_line` field including `Host`.
- [ ] AC-H3 C#: `LogLine.Host` (JSON `host`, omitted when empty) validated as short text (`PayloadValidationTests`).
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

## Accepted forms

The parsers' behavior on every input form (Security reviews this list).

### Journal export

| Input | Result |
| ----- | ------ |
| Text field `NAME=value\n` | value is everything up to `\n`, nothing stripped (also no `\r`) |
| Binary field `NAME\n` + uint64 LE length + bytes + `\n` | value = the bytes; a byte other than `\n` after them → "malformed field" |
| Length ≥ 2^63, or more than the remaining input | "truncated entry", parse ends |
| Field name: 1-64 bytes of `A-Z 0-9 _`, not starting with a digit | accepted (address fields `__*` included) |
| Any other field name (lower case, `-`, space, empty, over 64 bytes, non-ASCII) | "malformed field"; resynchronize after the next empty line |
| A value longer than its keep limit (16,384 bytes for `MESSAGE`, 1,024 for the short fields, 32 for the numeric ones) | cut at a UTF-8 boundary, the rest read and discarded; `Truncated` for `MESSAGE` and the short fields; an over-long numeric value is invalid |
| Empty line(s) | end the entry; repeated ones ignored |
| EOF inside an entry made of whole fields | the entry is parsed |
| Repeated field | first value wins |
| `__REALTIME_TIMESTAMP` | 1-20 decimal digits, > 0, and inside the storable range; else "invalid __REALTIME_TIMESTAMP" |
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
| Separator | exactly one space after the time and after the host | "not a syslog line" |
| `HOST` | 1-255 bytes without space | "not a syslog line" |
| `TAG` | next token matching `PROGRAM[PID]:` or `PROGRAM:`, `PROGRAM` 1-128 bytes without space, `[`, `]`, `:`; `PID` 1-10 digits (over 2^31-1 → 0) | no tag: program empty, pid 0, message = rest after host |
| `MESSAGE` | rest of the line after `:` and at most one space; may be empty; invalid UTF-8 → U+FFFD; control characters kept | — |
| Feb 29 / day beyond the month in the resolved year | — | "invalid date" |
| Empty line | — | "empty line" |
| RFC 5424 (`<PRI>1 TIMESTAMP ...`) | not supported | "not a syslog line" (the `1` is not a month) |

Parsing is hand-written or uses `GeneratedRegex` without nested quantifiers, linear in the line length.

## Approach

1. **Record:** add `host` to `log_line` (Go model and validation, C# model and validation), regenerate the golden batch,
   add schema step 4 (`ALTER TABLE log_lines ADD COLUMN host TEXT NOT NULL DEFAULT ''`) and write/read the column. The
   wire version stays 1.0 because no version has been released (record 0084).
2. **Time zone:** `import.time_zone` in `BackendConfig` (`ImportConfig`), no default, validated when set by
   `SourceTimeZone.Find` against NodaTime's TZDB IDs (ordinal); NodaTime 3.3.5 via central package management in
   `Vandox.Core` (record 0085). Unset, the syslog parser fails a file at its first year-less line (AC-S7) instead of
   guessing a zone: the import cannot be redone once stored, and the failure is visible (outcome failed, exit 1) and
   repaired by a re-run after setting the option.
3. **Syslog:** `SyslogLine.TryParse` (header), `SyslogClock` (anchor, year, zone, DST), `SyslogParser` (reads lines with
   `LogLineReader`, builds records, passes them through the grouper).
4. **Journal:** `JournalExportReader` (bounded entry reader keeping eight fields), `JournalExportParser` (maps an entry,
   passes records through the grouper).
5. **Kernel reports:** `KernelReportGrouper` works on `DataRecord`s with a `LogLine` payload: a `kernel` line whose message
   contains `invoked oom-killer:` or `------------[ cut here ]------------` opens a report; same-host `kernel` lines join
   it; it closes after a line containing `Out of memory: Killed process`, `Memory cgroup out of memory: Killed process`,
   `Out of memory and no killable processes` (OOM) or `---[ end trace ` (cut here), or by the bounds of AC-K4. The
   message keeps a head of up to `HeadBytes` (8,192) and a tail of the last whole lines that fit into the rest of 16,384
   bytes minus the marker line; a line longer than its budget is cut at a UTF-8 boundary. Memory: head + tail + one
   line, independent of the report length. Priority = lowest member priority, `Truncated` if cut or any member was.
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
| Vandox.Core | `Vandox.Core.csproj`; `Directory.Packages.props` | `PackageReference Include="NodaTime"`; `PackageVersion Include="NodaTime" Version="3.3.5"` |
| Vandox.Storage | `StorageLimits.cs`, `SchemaMigrator.cs`, `BatchWriter.cs`, `RecordQueries.cs` | schema 4, `host` column |
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

// Vandox.Core.LogParsing
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

internal readonly record struct SyslogTime(DateTimeOffset? Instant, int Month, int Day, int Hour, int Minute, int Second);
// Instant: UTC instant of an RFC 3339 time stamp; null for a traditional one (then Month..Second are set)

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
    internal bool HasAnchor { get; }
    internal DateTimeOffset? Resolve(SyslogTime time);   // UTC; null when the date does not exist in the inferred year or a year-less time has no anchor
}

internal sealed class KernelReportGrouper
{
    internal const int MaxLines = 2000;
    internal const int HeadBytes = 8192;
    internal static readonly TimeSpan MaxSpan = TimeSpan.FromSeconds(60);
    internal void Add(DataRecord record, List<DataRecord> ready);   // appends the records to emit now, in order
    internal void Finish(List<DataRecord> ready);                   // flushes an open report at the end of input
}
```

Existing files the skeleton rewrites: `ImportCommand.cs` keeps its logic until step 6 (the skeleton only adds types);
`LogLine.cs`, `BackendConfig.cs` and `logline.go` get the new members in the skeleton (they compile without behavior).
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
`SourceTimeZoneTests.cs`; helpers `RecordingEmitter.cs` (records and skips; optional exception) and
`JournalExportBuilder.cs` (text and binary fields as bytes). Fixture: `testdata/logs/kern.log-oom`.

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
  the re-run that completes the file, kernel reports and their bounds, the built-in list and its order, and that
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
  `RecordingEmitter` and `JournalExportBuilder`.

The Lead edits only record status and the index at approval.

## Architecture check

- *Backfilled data never raises an alert*: imported records keep origin `import` (validated by `RecordRules.CheckImportRecord`).
- *No data gaps unless recorded*: every input line is a record, part of a report record or a counted skip with a reason;
  the omitted middle of an over-long report is stated in the record itself (`[N lines omitted]`, `Truncated`).
- Repeatable import (0069): parsing depends only on content, `LogFile` (first import's name and time on resume) and
  `import.time_zone`; changing the option between an interrupted run and its resume shifts the rest of that file — stated
  in the area document and record 0085. Setting it after a failure "import.time_zone is not set" shifts nothing: the
  records stored before the failure come from RFC 3339 lines, which do not use the zone, so the resume by count stays
  exact.
- *No data gaps unless recorded*, for the unset option: the file is listed as failed with the reason, never imported
  with guessed times.
- Wire versioning (0043): `host` is an additive optional field; the version stays 1.0 because nothing is released (0084).
- Storage (0063, 0077): a new migration step, no change to deduplication or the FTS invariant.

## Security considerations

- Area 10 (parsing): memory per parse is bounded by constants (one line of 16 KiB plus a head/tail report buffer of 16 KiB;
  the journal reader keeps eight fields of at most 16 KiB / 1 KiB and reads everything else in fixed chunks); a declared
  binary length never sizes an allocation; parsing is linear (no backtracking patterns); every loop reads input or ends;
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
