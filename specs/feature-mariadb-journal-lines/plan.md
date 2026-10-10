# Plan: Classify MariaDB lines from the journal and syslog

Source: issue #165 | [spec.md](spec.md)
Status: Draft
Tier: security. The change adds a bounded multi-line grouping of hostile input to the journal and syslog parsers, which
is security area 10, *Parsing of external input* (`.squad/project.md`).

## Problem / root cause

Summary of `spec.md`: a default server writes MariaDB's error log to the journal. The journal and syslog parsers store
those lines as plain records of program `mariadbd`, without the MariaDB rules of record 0088. This change applies those
rules in a grouping stage inside both parsers, which runs before the kernel-report stage.

Claims of the issue, checked against the code and the MariaDB source (10.6.22, sparse clone of `MariaDB/server` at tag
`mariadb-10.6.22`):

| Claim | Result |
| ----- | ------ |
| Debian/Ubuntu packaging sends the error log to the journal under systemd, with `#log_error = /var/log/mysql/error.log` commented out in `50-server.cnf` | **Confirmed for the Debian packaging in the MariaDB tree.** `debian/additions/mariadb.conf.d/50-server.cnf:53-57` says "When running under systemd, error logging goes via stdout/stderr to journald" and has `#log_error`. `sql/mysqld.cc:4818-4840` redirects stderr to a file only when `opt_error_log` is set. Ubuntu's own package and the server's Plesk configuration are **unverified** (as in 0088). |
| Its lines arrive through the `journal` and `syslog` parsers as plain `log_line` records of program `mariadbd` | **Confirmed in code.** `JournalExportParser.cs:83` sets program from `SYSLOG_IDENTIFIER`, else `_COMM`. `SyslogParser.cs:136` sets program from the tag. Neither sets `Event`; only `MariaDbParseSession.cs:172` does. The name `mariadbd` follows from `support-files/mariadb.service.in:92` (`ExecStart=@sbindir@/mariadbd`, no `SyslogIdentifier=`) and systemd's default identifier, the process name. That default is **unverified on the server**. `50-mysqld_safe.cnf:12-17` suggests a drop-in with `SyslogIdentifier = mysqld` and `SyslogLevel = err`. |
| Without `event` | **Confirmed** (see above). |
| Each crash report line becomes a record of its own | **Confirmed.** `KernelReportGrouper.cs:81` groups only program `kernel`. The signal handler writes each line separately to fd 2 (`sql/signal_handler.cc:181-277`). |
| The message starts with MariaDB's own time stamp | **Confirmed** for server lines: `sql/log.cc:9387` writes `"%d-%02d-%02d %2d:%02d:%02d %lu [%s] ..."` to stderr. **Confirmed** for the signal header: `signal_handler.cc:181,196` write `YYMMDD HH:MM:SS [ERROR] <prog> got signal N ;`. Crash report lines have no time stamp, which makes them continuation lines. |
| "Keep a crash report together ... with the bounds of the error log parser" | **Feasible as stated.** `MariaDbMessage` provides the bounds (16,384 / 16,320 bytes plus `[N lines omitted]`). It gets an overload for decoded text. |
| Implied: journal priority | Not stated in the issue. The upstream unit sets no `SyslogLevel=`, so by systemd's default every line of the service's stderr has `PRIORITY=6`. This is **unverified on the server**, which is why a MariaDB level replaces the line's priority (record 0088, option 33). |

Related defect found on the way: a consequence in record 0086 said "The built-in list is journal, then syslog", which has
been stale since #17 put `mariadb` between them. The Lead fixed it in place, since 0086 is unreleased and has no `v*` tag.

## Acceptance criteria

The rules are those of `docs/areas/log-import.md`, *MariaDB error log*, *Lines from the journal and syslog*. Field
rules refer to *Entries*, *Bounds* and *Events* there. "Unchanged" means the record the parser maps today, with its
message, priority, program and empty `event`.

- [ ] AC1 — **Journal entry.** Two journal entries with host `web-1`, identifier `mariadbd`, `_PID` 2345 and `PRIORITY`
  6 form one record. The first has message `2026-03-01 23:00:05 0 [Note] /usr/sbin/mariadbd: ready for connections.`.
  The second has message `Version: '10.6.12-MariaDB-0ubuntu0.22.04.1'  socket: '/run/mysqld/mysqld.sock'  port: 3306  Ubuntu 22.04`.
  The record has message `/usr/sbin/mariadbd: ready for connections.\nVersion: ...` and event `mariadb.ready`. Its
  priority is 6, its `captured_at` is the first entry's `__REALTIME_TIMESTAMP`, and its host, program and pid are
  `web-1`, `mariadbd` and 2345. `log` and `source` are `journal`, origin is `import`, sequence is 0, and `truncated` is
  false. The other forms and levels:
  - `[ERROR]` gives priority 3 and `[Warning]` gives 4, whatever `PRIORITY` says.
  - The InnoDB form `2026-03-02 10:10:10 0x7f3a2c1fe640  InnoDB: ...` and the form `YYMMDD HH:MM:SS mysqld_safe ...` have
    no level. They keep the entry's own `PRIORITY`, have no event, and keep program `mariadbd`.
  - The message is always the text after the header prefix, as in *Entries*.
- [ ] AC2 — **Syslog line and `mysqld`.** The syslog parser gives the same result as AC1 for lines such as
  `2026-03-01T23:00:05Z web-1 mariadbd[2345]: 2026-03-01 23:00:05 0 [Note] /usr/sbin/mariadbd: ready for connections.`
  followed by `2026-03-01T23:00:05Z web-1 mariadbd[2345]: Version: ...`. `log` is the file name as listed, the source is
  `syslog`, and a header without a level keeps the line's priority (`<PRI>` modulo 8, or none). Program `mysqld` behaves
  like `mariadbd` in both parsers.
- [ ] AC3 — **Not MariaDB lines.** A header-shaped message is emitted unchanged when its program is `mariadb`, `MariaDBd`,
  `mysqld_safe`, `kernel`, `sshd` or empty. A `mariadbd` journal entry whose binary `MESSAGE` holds a line feed is also
  emitted unchanged, even when it begins with a header. It does not open, join or end an entry: a continuation line of
  the open entry after it still joins that entry.
- [ ] AC4 — **No open entry.** These lines are emitted unchanged:
  - a `mariadbd` line without a header at the start of the input;
  - a MySQL 8 line of program `mysqld` (`2026-03-01T12:00:00.123456Z 0 [System] [MY-010116] [Server] /usr/sbin/mysqld (mysqld 8.0.36) starting as process 1`),
    which is no header;
  - a `mariadbd` line without a header after the open entry was ended by a header of another pid.
- [ ] AC5 — **Crash report.** Lines 24 to 52 of `testdata/logs/mariadb-error.log` become one record. They are fed as
  journal entries of `mariadbd` with one host and one pid; empty lines are entries with an empty `MESSAGE`. The record
  has event `mariadb.abort` and priority 3. Its message is `mysqld got signal 6 ;` followed by lines 25 to 51, joined by
  `\n`. The inner empty lines 28, 30 and 50 are kept and the trailing empty line 52 is dropped. This is the message
  `MariaDbErrorLogParser` builds for that entry.
- [ ] AC6 — **Interleaving and one open entry.** Between a header and its continuation lines, the input holds:
  - (a) a line of another program;
  - (b) a `mariadbd` line without a header from another pid;
  - (c) a `mariadbd` line without a header from another host.

  Each of these is emitted unchanged, before the entry's record, and the entry holds only its own continuation lines.
  (d) A `mariadbd` header of another pid emits the open entry and opens its own entry; a later continuation line of the
  first pid is emitted unchanged.
- [ ] AC7 — **Bounds.** Sizes are counted in UTF-8 bytes of the decoded text; an invalid byte becomes U+FFFD and counts
  3.
  - An entry of exactly 16,384 bytes is kept whole.
  - A longer entry is cut to its head within 16,320 bytes, then `\n[N lines omitted]` with the exact N. It is at most
    16,384 bytes and `truncated` is set.
  - A header or continuation record that already has `truncated` set makes the entry `truncated`.
  - At unit level, `MariaDbMessage.Add(string, bool)` gives the same `Build` result as `Add(ReadOnlySpan<byte>, bool)`
    for the same lines: empty lines held back, inner ones kept, trailing ones dropped, the limit exactly, overflow, and
    the truncated flag.
- [ ] AC8 — **Time.** A journal export with MariaDB lines parses without exception with the journal parser of
  `BuiltInParsers.Create(null)`.
  - `captured_at` is the header entry's `__REALTIME_TIMESTAMP`, not the time in the message. For example, message
    `2026-03-01 23:00:05 ...` with realtime `2026-03-01T22:00:05.123456Z` gives `captured_at` 22:00:05.123456Z.
  - A header whose date does not exist (`2026-02-30 12:00:00 0 [Note] x`, or hour `24`) opens an entry and is
    classified.
  - An RFC 3339 syslog file with MariaDB lines needs no zone either.
  - A year-less syslog file without a zone still fails with "import.time_zone is not set" at its first year-less line,
    unchanged.
- [ ] AC9 — **Recovery per host.** The cases below are in one input:
  - (a) A recovery start of host A (`InnoDB: Starting crash recovery from checkpoint LSN=...`), then
    `InnoDB: 10.6.12 started; log sequence number ...` of host B: B's line gets no event, and the same line of host A then
    gets `mariadb.recovery_end`.
  - (b) `Starting MariaDB ... as process N` of host B leaves A's recovery open. The same line of host A closes it, and a
    later A `started` line gets no event.
  - (c) A recovery start of host B replaces A's recovery: a later A `started` line gets no event.
  - (d) `Crash table recovery finished.` is `mariadb.recovery_end` in any scope.

  The new overload `Classify(MariaDbLine, string)` is tested at unit level. `Classify(MariaDbLine)` keeps its behavior,
  and every existing test of `MariaDbEventClassifierTests` and `MariaDbErrorLogParserTests` stays green unchanged.
- [ ] AC10 — **Skipped lines are not seen.** In a syslog file, a header line of `mariadbd` is followed by a `mariadbd`
  line that is skipped as "invalid date" (`Feb 30`), and then by a continuation line. The continuation line joins the
  entry the header opened. A journal entry without `MESSAGE` that is skipped likewise neither opens nor ends an entry.
- [ ] AC11 — **Emission and determinism.** The open entry is emitted only at the normal end of input. Cancellation or an
  emitter exception while an entry is open does not emit it, so the records emitted so far are a prefix of a complete
  parse. Two parses of the same input emit equal records in the same order, in both parsers.
- [ ] AC12 — **Memory.** `MariaDbLineGrouper` gets one header and then continuation records with the same host, program
  and pid.
  - After 2,000 continuation records of 16,000 characters each, it retains less than 1 MiB. This is measured as in
    `KernelReportGrouperTests.RetainedBytes` with `GC.GetTotalMemory(true)` before and after; retaining the lines would
    take at least 32 MiB.
  - After 100,000 empty continuation records, it also retains less than 1 MiB.
  - The record that `Finish` emits is at most 16,384 UTF-8 bytes and carries the exact omitted count.
- [ ] AC13 — **Parity with the error log.** The lines of `testdata/logs/mariadb-error.log` are fed in two ways:
  - as journal entries: host `web-1`, identifier `mariadbd`, pid 1001 for lines 1 to 6, 2345 for lines 7 to 10, 3456 for
    lines 11 to 52 and 4567 for lines 53 to 57. A header line gets its own time; a continuation line gets the time of its
    header; an empty line becomes an empty `MESSAGE`;
  - as a syslog file of lines `<time>Z web-1 mariadbd[<pid>]: <line>`, without `<PRI>`.

  Both give the same sequence of entries as `MariaDbErrorLogParser` with zone `UTC`: the same messages and events in the
  same order. Priorities are equal for the headers with a level. For headers without a level, the syslog file gives
  none, as the error log does, and the journal gives the entry's `PRIORITY`.
- [ ] AC14 — **Stage order.** In `SystemLogGrouper`:
  - With kernel lines and MariaDB lines interleaved, a kernel OOM report and a MariaDB entry are both grouped.
  - An entry that ends while a kernel report is open is emitted before the report record.
  - A kernel report that ends while an entry is open is emitted before the entry record.
  - `Finish` emits the open entry before the open report.

  Every existing kernel-report test of `JournalExportParserTests`, `SyslogParserTests` and `KernelReportGrouperTests`
  stays green unchanged.
- [ ] AC15 — **Documentation.**
  - Already in the diff (Lead): `docs/areas/log-import.md` and record 0088 describe the behavior.
  - Still to come (Dev): README *Supported sources* and `.squad/project.md` security area 10.

  The Reviewer verifies this; it has no test.

No content was supplied verbatim by the issue. The error log fixture is the repository's own and was read.

## Approach

1. **`MariaDbLineGrouper`** (new, `Vandox.Core.LogParsing`) works like `KernelReportGrouper`: `Add(record, ready)` and
   `Finish(ready)`.
   - **Pass-through.** A record passes to `ready` at once when its payload is not a `LogLine`, when its `Program` is
     neither `mariadbd` nor `mysqld` (ordinal), or when its `Message` contains `'\n'`.
   - **Header check.** The grouper encodes the message as UTF-8 into a reused buffer of `ModelLimits.MaxTextBytes` bytes
     with the non-throwing `Encoding.UTF8.TryGetBytes`. A message that does not fit is no header; the two parsers cut
     messages to that size, so this cannot happen from them. The grouper then calls the existing
     `MariaDbLine.TryParse(ReadOnlySpan<byte>)`. A cheap pre-check is allowed: a header starts with an ASCII digit.
   - **Header.** The open entry is flushed to `ready`. The grouper then keeps the header record, its `LogLine`, the
     `MariaDbLine`, `_classifier.Classify(header, line.Host)` and
     `new MariaDbMessage(header.Message, line.Truncated)`.
   - **Not a header.** If an entry is open and host, program and pid are all equal (ordinal and int), the line goes to
     `_message.Add(line.Message, line.Truncated)`. Otherwise the record passes to `ready`.
   - **Flush.** Flush builds a new `DataRecord`:
     - from the header record: `Origin`, `Source`, `Seq` and `CapturedAt`;
     - a new `LogLine` with `Log`, `Host`, `Program` and `Pid` from the header line;
     - `Priority = header.Priority ?? line.Priority`;
     - `Message = _message.Build(out truncated)` and `Truncated = truncated`;
     - `Event = _event`.

     It then resets the open entry. `Finish` flushes.
2. **`SystemLogGrouper`** (new) owns a `MariaDbLineGrouper`, a `KernelReportGrouper` and a staging list.
   - `Add` passes the record to the MariaDB stage and forwards everything that stage emits, in order, to the kernel stage,
     which appends to `ready`.
   - `Finish` first finishes the MariaDB stage, forwards its output to the kernel stage, then finishes the kernel stage.
3. **`MariaDbEventClassifier`** gets `Classify(MariaDbLine line, string scope)`. The state `bool _recoveryOpen` becomes
   `string? _recoveryScope`; `null` means no open recovery.
   - A recovery start sets the scope.
   - A start closes the recovery when the scope is equal.
   - An InnoDB `started` line is `mariadb.recovery_end` only when the scope is equal, and then closes the recovery.
   - `Crash table recovery finished.` is always `mariadb.recovery_end` and closes the recovery only when the scope is
     equal.
   - `Classify(MariaDbLine line)` becomes `Classify(line, string.Empty)`. `MariaDbParseSession` is not changed.
4. **`MariaDbMessage`** gets `Add(string line, bool truncated)`, with the same bounds as the raw overload. Empty text is a
   held-back empty line. After an overflow, the line only counts as omitted. Otherwise its size is the UTF-8 byte count,
   then the `Exceeds` / `Overflow` / append path runs. The shared tail of both overloads is factored into one private
   method.
5. **`JournalExportParser.ParseAsync`** and **`SyslogParser.ParseAsync`** use `new SystemLogGrouper()` in place of
   `new KernelReportGrouper()`. `KernelReportGrouper.EmitAsync` stays as it is. This happens in step 6, not in the
   skeleton (see *Signatures*).

Analyzer notes for the Dev:
- Regions per member kind and XML documentation on every member.
- No `!` (RH3001) and no comparison with `false` (S1125): write positive conditions.
- Stay below S3776 complexity 15.
- No new suppression.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `LogParsing/MariaDbLineGrouper.cs` | new |
| Vandox.Core | `LogParsing/SystemLogGrouper.cs` | new |
| Vandox.Core | `LogParsing/MariaDbEventClassifier.cs` | new overload `Classify(MariaDbLine, string)`; the recovery state is scoped |
| Vandox.Core | `LogParsing/MariaDbMessage.cs` | new overload `Add(string, bool)` |
| Vandox.Core | `LogParsing/JournalExportParser.cs`, `LogParsing/SyslogParser.cs` | `ParseAsync` uses `SystemLogGrouper` (bodies only) |
| docs | `docs/areas/log-import.md`, `docs/decisions/0088-...md`, `docs/decisions/0086-...md`, `docs/decisions/README.md` | done by the Lead in step 2 |
| repo | `README.md`, `.squad/project.md` | Dev, step 6 |

## Signatures (for the Dev's skeleton)

```csharp
// src/Vandox.Core/LogParsing/MariaDbLineGrouper.cs (new)
/// <summary>
/// Joins the MariaDB lines that the journal and syslog parsers read (program <c>mariadbd</c> or <c>mysqld</c>) into entries: a
/// header line and the following lines without a header of the same host, program and process, with the header, bounds and
/// event rules of the MariaDB error log. One entry is open at a time, so memory does not depend on the input.
/// </summary>
internal sealed class MariaDbLineGrouper
{
    /// <summary>
    /// Takes the next record and appends the records to emit now, in order: a record that does not join the open entry at once,
    /// the record of the open entry when a MariaDB header line ends it.
    /// </summary>
    /// <param name="record">The record with a log line payload</param>
    /// <param name="ready">Receives the records to emit</param>
    internal void Add(DataRecord record, List<DataRecord> ready);

    /// <summary>
    /// Emits the open entry, if any; called only at the normal end of input.
    /// </summary>
    /// <param name="ready">Receives the records to emit</param>
    internal void Finish(List<DataRecord> ready);
}

// src/Vandox.Core/LogParsing/SystemLogGrouper.cs (new)
/// <summary>
/// The stages the journal and syslog parsers pass their records through, in this order: MariaDB entries, then kernel reports.
/// </summary>
internal sealed class SystemLogGrouper
{
    /// <summary>
    /// Takes the next record and appends the records to emit now, in order.
    /// </summary>
    /// <param name="record">The record with a log line payload</param>
    /// <param name="ready">Receives the records to emit</param>
    internal void Add(DataRecord record, List<DataRecord> ready);

    /// <summary>
    /// Emits the open MariaDB entry, then the open kernel report; called only at the normal end of input.
    /// </summary>
    /// <param name="ready">Receives the records to emit</param>
    internal void Finish(List<DataRecord> ready);
}

// src/Vandox.Core/LogParsing/MariaDbEventClassifier.cs (new overload; Classify(MariaDbLine) stays)
/// <summary>
/// Classifies a header of a scope (the host in the journal and syslog): a recovery is closed only by an entry of the scope that
/// opened it, and one recovery is tracked at a time.
/// </summary>
/// <param name="line">The header</param>
/// <param name="scope">The scope, compared ordinally</param>
/// <returns>A value of <see cref="MariaDbEvents"/>, or an empty string for none</returns>
internal string Classify(MariaDbLine line, string scope);

// src/Vandox.Core/LogParsing/MariaDbMessage.cs (new overload; Add(ReadOnlySpan<byte>, bool) stays)
/// <summary>
/// Adds a continuation line that is already decoded, with the bounds of a raw line.
/// </summary>
/// <param name="line">The text of the line</param>
/// <param name="truncated"><c>true</c> when the line was cut before</param>
internal void Add(string line, bool truncated);
```

The skeleton adds the two new files and the two new overloads with
`throw new NotImplementedException();`, plus `#pragma warning disable RH2003, S2325` as *Skeleton* in `.squad/stack.md`
describes. It does **not** touch the existing members. `Classify(MariaDbLine)` and `Add(ReadOnlySpan<byte>, bool)` keep
their bodies, and `JournalExportParser` and `SyslogParser` are not rewired in step 4. That way every existing test stays
green, and the new tests fail on assertions or `NotImplementedException`. In step 6 the Dev makes `Classify(MariaDbLine)`
delegate to the scoped overload and rewires both parsers. No existing file is final as it stands: the four existing
files in *Affected projects and types* change in step 6.

## Test files

- `tests/Vandox.Core.Tests/MariaDbLineGrouperTests.cs` (new): AC1 to AC7, AC9 (grouper level), AC12.
- `tests/Vandox.Core.Tests/SystemLogGrouperTests.cs` (new): AC14.
- `tests/Vandox.Core.Tests/MariaDbEventClassifierTests.cs` (extend): AC9 at unit level, `Classify(MariaDbLine, string)`.
- `tests/Vandox.Core.Tests/MariaDbMessageTests.cs` (extend): AC7 at unit level, `Add(string, bool)`.
- `tests/Vandox.Core.Tests/JournalExportParserTests.cs` (extend): AC1, AC3 (binary `MESSAGE` with a line feed), AC8,
  AC10, AC11 and AC13 end to end through the parser.
- `tests/Vandox.Core.Tests/SyslogParserTests.cs` (extend): AC2, AC8 (RFC 3339 and year-less), AC10, AC11 and AC13 end to
  end.

Helpers: `JournalExportBuilder` (`Entry`, `Binary`) and `RecordingEmitter` already exist. A shared helper the Tester adds
for building MariaDB records is an `internal` class of `tests/Vandox.Core.Tests` and gets a mention in the *log parsers
(C#)* row of *Test doubles* in `.squad/project.md` (Tester).

How heap bounds are measured (lesson from #17): the tests build Debug (`dotnet test` without `-c Release`).
- AC12 measures retained bytes with one private `RetainedBytes` helper per test class, which wraps
  `GC.GetTotalMemory(true)` in `#pragma warning disable/restore S1215`, as `KernelReportGrouperTests` does.
- Any allocation bound the Tester adds is measured beyond a baseline loop over the same records, for example through
  `KernelReportGrouper` alone. It is never an absolute number.

Existing test code that calls a changed signature: **none**. Only overloads are added; no existing signature changes or
disappears.

## Areas

- **Log import** (`docs/areas/log-import.md`), written by the Lead in step 2:
  - *System log parsers*: the stage order.
  - *Duplicates*: a journal export and syslog files of the same period.
  - *Events*: the recovery per host.
  - *Forged lines*: a pointer to the new section.
  - *Lines from the journal and syslog*: a new subsection under *MariaDB error log*, which replaces *Default
    installations*.
  - *Related decisions* (0088 line) and *Implementation* (the two new types).
- Wire format and Storage: none. `event` exists, its name rule and its column are unchanged.

## Documentation updates

| File | Edit | Owner |
| ---- | ---- | ----- |
| `docs/areas/log-import.md` | as in *Areas* | Lead (done, step 2) |
| `docs/decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md` | extended (context, options 20 to 36, decision, consequences), status `Proposed` | Lead (done, step 2) |
| `docs/decisions/README.md` | row 0088: title extended, status `Proposed` | Lead (done, step 2) |
| `docs/decisions/0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md` | stale consequence about the built-in list corrected (related defect) | Lead (done, step 2) |
| `README.md`, *Import logs*, MariaDB bullet | Replace the last sentence ("MariaDB sends its error log to the journal by default under systemd; those lines arrive through the journal and syslog parsers as plain lines without events."). The new text says that in a journal export or a syslog file, lines of `mariadbd` and `mysqld` that start with MariaDB's time stamp are read with the same rules. An entry with its crash report becomes one record with the priority and event of its header. Time, host and process ID come from the journal or syslog line, and the source type stays `journal` or `syslog`. A journal export needs no `import.time_zone`. | Dev |
| `.squad/project.md`, *Security areas* 10 | add `SystemLogGrouper` and `MariaDbLineGrouper` (MariaDB lines of the journal and syslog joined per host, program and pid into one bounded open entry) next to `KernelReportGrouper`; the record list already names 0088 | Dev |
| `.squad/project.md`, *Test doubles*, row *log parsers (C#)* | only if the Tester adds a shared helper | Tester |
| `docs/ARCHITECTURE.md` | none. No guarantee or flow changes, and the component list stays true. | n/a |

## Architecture check

- **No data gaps unless explicitly recorded.** Not touched: this is the import path, not collection. No input line is
  dropped. Every mapped record is either emitted unchanged or becomes part of an entry, apart from trailing empty lines,
  which belong to an entry as in the error log.
- **A hanging collector never blocks the agent.** Not touched (agent).
- **Backfilled data never alerts by itself.** Not touched: imported records stay origin `import`.
- **Parser contract** (*Parsers* in the area document):
  - The output stays deterministic.
  - Records written inside a combined record are emitted first.
  - The open entry is emitted only at the normal end, so the importer's resume by count keeps working.
  - Memory stays bounded independently of the input.
  - There are no new skip reasons.
- No new parser, source type, configuration option, wire field or schema change, so the *Integration surface* entries
  for a parser, an option or a wire field do not apply.

## Security considerations

- [x] **Limits before or after decoding.** Records reach the stage already decoded and cut by the parsers. The journal
  reader and the line reader keep their raw read bounds of 16 KiB, unchanged. Every new size rule counts UTF-8 bytes of
  the decoded text: the entry text is at most 16,384 bytes, the kept head at most 16,320 bytes, and U+FFFD counts 3. The
  header grammar is checked on the UTF-8 encoding of the decoded message. It is ASCII, so U+FFFD (`EF BF BD`) never
  matches a digit, space or bracket of a header. The encode buffer is 16,384 bytes. A message that does not fit is no
  header and is never cut or thrown on.
- [x] **Exceptions that reach a user-visible reason.** No new ones.
  - The grouper does not throw on input: `MariaDbLine.TryParse` never throws, `TryGetBytes` does not throw, and
    `MariaDbMessage` counts in `long`.
  - The existing ones are unchanged: cancellation, the emitter's exception, and `InvalidOperationException`
    "import.time_zone is not set" for a year-less syslog line. No journal path raises the last one.
- **How the inputs that decide grouping are produced.** They are enumerated from the two parsers' mappings, so the
  forging analysis is not limited to the issue's example:
  - Journal `program`: `SYSLOG_IDENTIFIER`, else `_COMM`. The sender chooses the identifier, for example with
    `syslog(3)`'s `openlog` ident, `sd_journal_send`, `logger -t`, `systemd-cat -t` or a unit's `SyslogIdentifier=`.
    `_COMM` is the process name, which a process can set itself.
  - Journal `pid`: `_PID` (journald, from the sender's credentials, not chosen by a local sender), else `SYSLOG_PID`
    (chosen by the sender). The fallback is used only when `_PID` is missing or invalid.
  - Syslog `program` and `pid`: the tag as written in the file, chosen by the sender.
  - `host`: `_HOSTNAME`, or the syslog `HOST`.
  - A message with a line feed is excluded.

  Consequence: the program filter is a classification and no trust boundary. Every way to forge an entry or event is
  documented as accepted: a header under the name ends the open entry, and in syslog continuation lines can be injected
  with a known pid. This follows 0086 option 19 and 0088 options 19 and 36. The `_PID` key closes only the injection of
  continuation lines into the real server's entry in a live journal.
- **Cost.** The work per line is linear in the message (one encode, ordinal string operations, no regular expression).
  Memory is the open entry's kept text, at most 16 KiB, plus one reused 16 KiB buffer and counters.
- **Display.** Messages are stored as before. Escaping in logs and the web UI is unchanged (security area 12).

## Decision records

- `docs/decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md`: extended in place,
  because it is unreleased (no `v*` tag) and on the same topic. Its status and index row are set to **Proposed** for
  this run, and step 9 sets them back to `Accepted`.
- `docs/decisions/0086-...md`: one stale consequence corrected. The decision is unchanged and the status stays
  `Accepted`.

## Out of scope / follow-ups

- #166: a JSON `null` in a string field of the C# decoder, tracked separately.
- #21 and #23: queries by `event`, counting an entry stored from both journal and syslog once, and whether to keep
  `_TRANSPORT` to tell the server apart from a forger (0088 option 25).
- #37: whether the agent fills `event` when it ships lines live.
- Binary journal files and MySQL 8's format: not read or not classified, unchanged.
