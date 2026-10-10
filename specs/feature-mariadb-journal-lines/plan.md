# Plan: Classify MariaDB lines from the journal and syslog

Source: issue #165 | [spec.md](spec.md)
Status: Approved (step 9, see *Approval*)
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
| Each crash report line becomes a record of its own | **Confirmed.** `KernelReportGrouper.cs:81` groups only program `kernel`. The signal handler writes each line separately to fd 2 (`sql/signal_handler.cc:181-277`). Empty lines of the report do **not** reach the journal: systemd's `stdout_stream_log` (`src/journal/journald-stream.c`, v249 line 284 and main c673d99) returns on an empty line before it stores or forwards it (checked in a sparse clone of `systemd/systemd`). |
| The message starts with MariaDB's own time stamp | **Confirmed** for server lines: `sql/log.cc:9387` writes `"%d-%02d-%02d %2d:%02d:%02d %lu [%s] ..."` to stderr. **Confirmed** for the signal header: `signal_handler.cc:181,196` write `YYMMDD HH:MM:SS [ERROR] <prog> got signal N ;`. Crash report lines have no time stamp, which makes them continuation lines. |
| "Keep a crash report together ... with the bounds of the error log parser" | **Feasible, with the size bound kept and the cut replaced by a split.** A record holds at most 16,384 UTF-8 bytes, as in the error log, and memory does not depend on the input. A line that would exceed the bound ends the entry and is stored as a plain line, instead of being counted in `[N lines omitted]` (0088 option 38, chosen after the plan security review, see *Security review (plan)*). `MariaDbMessage` gets `TryAdd` for decoded text. |
| Implied: journal priority | Not stated in the issue. The upstream unit sets no `SyslogLevel=`, so by systemd's default every line of the service's stderr has `PRIORITY=6`. This is **unverified on the server**, which is why a MariaDB level replaces the line's priority (record 0088, option 33). |

Related defect found on the way: a consequence in record 0086 said "The built-in list is journal, then syslog", which has
been stale since #17 put `mariadb` between them. The Lead fixed it in place, since 0086 is unreleased and has no `v*` tag.

## Acceptance criteria

The rules are those of `docs/areas/log-import.md`, *MariaDB error log*, *Lines from the journal and syslog*. Field
rules refer to *Entries*, *Bounds* and *Events* there. "Unchanged" means the record the parser maps today, with its
message, priority, program and empty `event`. The **key** of a line is its host, program and pid.

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
  - a `mariadbd` line without a header after the open entry was ended by a header of another pid or by the time bound
    of AC6 (e).
- [ ] AC5 — **Crash report.** Lines 24 to 52 of `testdata/logs/mariadb-error.log` become one record. They are fed as
  journal entries of `mariadbd` with one host, one pid and the header's time; empty lines are entries with an empty
  `MESSAGE` (synthetic: journald itself stores no empty line, see AC13, but a native client can send one). The record has
  event `mariadb.abort` and priority 3. Its message is `mysqld got signal 6 ;` followed by lines 25 to 51, joined by
  `\n`. The inner empty lines 28, 30 and 50 are kept and the trailing empty line 52 is dropped. This is the message
  `MariaDbErrorLogParser` builds for that entry.
- [ ] AC6 — **Interleaving, one open entry and the time bound.** Between a header and its continuation lines, the input
  holds:
  - (a) a line of another program;
  - (b) a `mariadbd` line without a header from another pid;
  - (c) a `mariadbd` line without a header from another host.

  Each of these is emitted unchanged, before the entry's record, and the entry holds only its own continuation lines.
  (d) A `mariadbd` header of another pid emits the open entry and opens its own entry; a later continuation line of the
  first pid is emitted unchanged.

  The time bound is `MariaDbLineGrouper.MaxSpan`, 60 seconds, counted from the header record's `CapturedAt`:
  - (e) A line of the entry's key without a header exactly 60 s after the header joins. One at 60 s plus 1 µs (journal)
    or 61 s (syslog, second resolution) does not: the entry's record is emitted first, then the line unchanged, and a
    following line of the key is emitted unchanged too.
  - (f) The same holds backwards: a line 60 s before the header's time joins, one 60 s plus 1 µs before ends the entry the
    same way.
  - (g) The bound counts from the header, not from the previous member: with lines at header + 30 s and header + 59 s
    joined, a line at header + 61 s ends the entry, although it is 2 s after the previous member.

  (e) is also tested end to end through both parsers.
- [ ] AC7 — **Bounds: split, never cut.** Sizes are counted in UTF-8 bytes of the decoded text; an invalid byte becomes
  U+FFFD and counts 3. The size of an entry is its message: the header's message, then per joined line `\n` and its
  text, with the held-back empty lines before a line counted as one byte each.
  - An entry of exactly 16,384 bytes is one record, whole.
  - (a) A non-empty line of the key within the time bound that would make the message 16,385 bytes or more ends the
    entry. The entry's record is emitted first, whole, at most 16,384 bytes. Its message contains no `lines omitted`,
    and its `truncated` flag comes only from its own lines. Then the line is emitted unchanged, then a following short
    line of the key, unchanged too (no entry is open).
  - (b) The same with two empty lines held back before the line that does not fit: the record's message does not end in
    `\n`, and the two empty lines are not emitted (trailing empty lines of the entry).
  - (c) A first continuation line of 16,000 characters after a short header joins. A second one ends the entry as in (a).
  - (d) A header or continuation record that already has `truncated` set makes the entry `truncated`. A line that does
    not fit leaves the entry's flag unchanged and keeps its own.
  - (e) No text is lost. The input is one key: a header, continuation lines of mixed sizes (1 to 16,000 characters,
    with empty lines between them), a second header and more continuation lines. Take the emitted records in order,
    split each entry's message at `\n` and drop the empty parts. The result is the input's non-empty messages in input
    order, each exactly once, where a header's message is its text after the header prefix.
  - At unit level, `MariaDbMessage.TryAdd(string, bool)` behaves like the raw overload while lines fit. As long as it
    returns `true`, `Build` gives the same result as `Add(ReadOnlySpan<byte>, bool)` for the same lines: empty lines held
    back, inner ones kept, trailing ones dropped, the limit exactly, and the truncated flag. It returns `false` for the
    first non-empty line for which the raw `Add` would call `Overflow`, that is, when the held-back empty lines, the line
    feed and the line would make the text longer than 16,384 bytes. After `false`, `Build` returns the same text and
    flag as before the call. An empty line always returns `true`. After the constructor has cut a header longer than
    16,384 bytes, every non-empty line returns `false`.
- [ ] AC8 — **Time.** A journal export with MariaDB lines parses without exception with the journal parser of
  `BuiltInParsers.Create(null)`.
  - `captured_at` is the header entry's `__REALTIME_TIMESTAMP`, not the time in the message. For example, message
    `2026-03-01 23:00:05 ...` with realtime `2026-03-01T22:00:05.123456Z` gives `captured_at` 22:00:05.123456Z.
  - A header whose date does not exist (`2026-02-30 12:00:00 0 [Note] x`, or hour `24`) opens an entry and is
    classified.
  - An RFC 3339 syslog file with MariaDB lines needs no zone either.
  - A year-less syslog file without a zone still fails with "import.time_zone is not set" at its first year-less line,
    unchanged.
- [ ] AC9 — **Recovery in input order.** Each grouper owns one `MariaDbEventClassifier`, and each parse creates its own
  grouper, so the recovery state is that of the error log, in input order over all MariaDB entries:
  - (a) In one input, a recovery start of host A (`InnoDB: Starting crash recovery from checkpoint LSN=...`), then
    `InnoDB: 10.6.12 started; log sequence number ...` of host B: B's entry gets `mariadb.recovery_end`. This pins the
    documented multi-host limitation, so a change to it is deliberate.
  - (b) A recovery start, then `Starting MariaDB ... as process N`, then a `started` line: the last gets no event.
  - (c) Two parses with the same parser instance: the first input ends with an open recovery (its last entry is a
    recovery start), and the second input's first entry is a `started` line, which gets no event. Tested in both parsers.

  `MariaDbEventClassifier` is not changed, and every existing test of `MariaDbEventClassifierTests` and
  `MariaDbErrorLogParserTests` stays green unchanged.
- [ ] AC10 — **Skipped lines are not seen.** In a syslog file, a header line of `mariadbd` is followed by a `mariadbd`
  line that is skipped as "invalid date" (`Feb 30`), and then by a continuation line. The continuation line joins the
  entry the header opened. A journal entry without `MESSAGE` that is skipped likewise neither opens nor ends an entry.
- [ ] AC11 — **Emission and determinism.** The open entry is emitted only at the normal end of input. Cancellation or an
  emitter exception while an entry is open does not emit it, so the records emitted so far are a prefix of a complete
  parse. Two parses of the same input emit equal records in the same order, in both parsers.
- [ ] AC12 — **Memory.** Retained bytes are measured as in `KernelReportGrouperTests.RetainedBytes`, with
  `GC.GetTotalMemory(true)` before and after. `ready` is cleared after every call, as the parsers' `EmitAsync` does. The
  continuation records have the same key and the header's time.
  - (a) `MariaDbLineGrouper`, one header whose message is under 384 bytes, then 2,000 continuation records of 16,000
    characters each: it retains less
    than 1 MiB, where retaining the lines would take at least 32 MiB. The records it emits are the entry (the header and
    the first line) and the other 1,999 lines unchanged, each at most 16,384 UTF-8 bytes.
  - (b) `MariaDbLineGrouper`, one header, then 100,000 empty continuation records: less than 1 MiB. `Finish` emits the
    header's message alone.
  - (c) `MariaDbLineGrouper`, one header, then 100,000 continuation records of 100 characters: less than 1 MiB. No
    emitted record is over 16,384 bytes, and no line is lost, as in AC7 (e).
  - (d) **Through `SystemLogGrouper`** (security review, finding 2): 100,000 records that cycle through `sshd` lines,
    `kernel` lines (an OOM start, members and its `Killed process` end) and `mariadbd` header lines are followed by 100,000
    `mariadbd` continuation records of 100 characters. These have the key and time of the last header. Together they
    retain less than 1 MiB. Each record is built with its own message string, so a staging list that kept what it
    forwarded would retain at least 15 MB.
- [ ] AC13 — **Parity with the error log.** The lines of `testdata/logs/mariadb-error.log` are fed in three ways:
  - as journal entries: host `web-1`, identifier `mariadbd`, pid 1001 for lines 1 to 6, 2345 for lines 7 to 10, 3456 for
    lines 11 to 52 and 4567 for lines 53 to 57. A header line gets its own time; a continuation line gets the time of its
    header; an empty line becomes an empty `MESSAGE`;
  - as a syslog file of lines `<time>Z web-1 mariadbd[<pid>]: <line>`, without `<PRI>` (an empty line ends after the
    colon, which `SyslogLine` reads as an empty message, `SyslogLineTests.cs:42`);
  - as journal entries as in the first feed, but without the empty lines 6, 28, 30, 50 and 52, as journald stores this
    output.

  The first two give the same sequence of entries as `MariaDbErrorLogParser` with zone `UTC`: the same messages and
  events in the same order. Priorities are equal for the headers with a level. For headers without a level, the syslog
  file gives none, as the error log does, and the journal gives the entry's `PRIORITY`. The third gives the same entries
  and events, and each message equals the error log's with its empty lines removed (the crash report's message lacks
  lines 28, 30 and 50). The parity therefore holds for input that carries the empty lines; a real journal loses them.
- [ ] AC14 — **Stage order.** In `SystemLogGrouper`:
  - With kernel lines and MariaDB lines interleaved, a kernel OOM report and a MariaDB entry are both grouped.
  - An entry that ends while a kernel report is open is emitted before the report record.
  - A kernel report that ends while an entry is open is emitted before the entry record.
  - `Finish` emits the open entry before the open report.
  - Every record is emitted exactly once. Five `sshd` lines through `SystemLogGrouper`, one call each, leave exactly
    those five records in `ready`, in order. The same holds for a `mariadbd` entry ended by a header: the entry's record
    appears once.

  Every existing kernel-report test of `JournalExportParserTests`, `SyslogParserTests` and `KernelReportGrouperTests`
  stays green unchanged.
- [ ] AC15 — **Documentation.**
  - Already in the diff (Lead): `docs/areas/log-import.md` and record 0088 describe the behavior.
  - Still to come (Dev): README *Supported sources* and `.squad/project.md` security area 10.

  The Reviewer verifies this; it has no test.
- [ ] AC16 — **Injected lines (security review, finding 1).** The server's header is line 24 of the fixture
  (`260302 10:10:10 [ERROR] mysqld got signal 6 ;`, message `mysqld got signal 6 ;`, 21 bytes). It is at
  T = 2026-03-02T10:10:10Z, host `web-1`, program `mariadbd`, pid 2345. Injected lines are lines without a header of
  1,000 `A` characters.
  - (a) Syslog. After the header at T come 20 injected lines at T + 1 s with tag `mariadbd[2345]:`, then at T + 2 s the
    server lines `Query (0x7f3a2c0d9e10): SELECT 1` and `Connection ID (thread ID): 42`. The first record is the entry
    with event `mariadb.abort`. Its message is the header's message and the first 16 injected lines (16,037 bytes; a
    17th would make 17,038). Then come, unchanged and in input order, injected lines 17 to 20 and the `Query` and
    `Connection ID` lines. No emitted message contains `lines omitted`. Each input message appears exactly once, in the
    entry or as a plain record.
  - (b) Journal. The same lines arrive as journal entries. The injected entries carry `_PID` 9999 and `SYSLOG_PID` 2345.
    They are emitted unchanged, before the entry. The entry holds the header, `Query` and `Connection ID` lines, because
    `_PID` decides the key, not `SYSLOG_PID`.
  - (c) Syslog. Right after the server's header comes a forged header at T + 1 s with tag `mariadbd[2345]:` and message
    `2026-03-02 10:10:11 0 [Note] /usr/sbin/mariadbd: ready for connections.`. The `Query` and `Connection ID` lines
    follow at T + 2 s. They join the forged entry, which gets event `mariadb.ready`, and the server's entry holds its
    header alone. This pins the documented limitation (0088 option 36), so a change to it is deliberate.

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
     `MariaDbLine`, `_classifier.Classify(header)` (the existing method, unchanged) and
     `new MariaDbMessage(header.Message, line.Truncated)`.
   - **Not a header.** If an entry is open and host, program and pid are all equal (ordinal and int):
     - when `(record.CapturedAt - header.CapturedAt).Duration() <= MaxSpan` and
       `_message.TryAdd(line.Message, line.Truncated)` returns `true`, the line has joined;
     - otherwise (beyond the time bound, or the line does not fit) the open entry is flushed to `ready`, then the record
       passes to `ready`. There is no open entry afterwards, so the following lines of the key pass through until the
       next header.

     Without an open entry, or with another key, the record passes to `ready` and the open entry stays open. The time
     check comes first, so a line beyond the bound never touches the message.
   - **Flush.** Flush builds a new `DataRecord`:
     - from the header record: `Origin`, `Source`, `Seq` and `CapturedAt`;
     - a new `LogLine` with `Log`, `Host`, `Program` and `Pid` from the header line;
     - `Priority = header.Priority ?? line.Priority`;
     - `Message = _message.Build(out truncated)` and `Truncated = truncated`;
     - `Event = _event`.

     It then resets the open entry. `Finish` flushes.
2. **`SystemLogGrouper`** (new) owns a `MariaDbLineGrouper`, a `KernelReportGrouper` and one reused staging list
   `_staged` (a `List<DataRecord>` field, never handed out).
   - `Add(record, ready)` calls `_mariaDb.Add(record, _staged)`, then `_kernel.Add(staged, ready)` for each staged record
     in order, then `_staged.Clear()`.
   - `Finish(ready)` calls `_mariaDb.Finish(_staged)`, forwards the staged records the same way, calls `_staged.Clear()`,
     then `_kernel.Finish(ready)`.
   - The staging list is empty whenever `Add` or `Finish` returns. During a call it holds at most two records: the
     entry that the record ends and the record itself. No stage receives a record twice (AC12 (d), AC14). After an
     exception the grouper is discarded with its parse.
3. **`MariaDbEventClassifier`** is not changed. The grouper owns one instance, so the recovery state lives as long as one
   parse, in input order over all MariaDB entries, as in `MariaDbParseSession`.
4. **`MariaDbMessage`** gets `bool TryAdd(string line, bool truncated)`, the decoded counterpart of the raw `Add`
   without its overflow path.
   - Empty text is a held-back empty line (`_pendingEmpty++`, the flag or-ed in) and returns `true`.
   - Otherwise its size is `Encoding.UTF8.GetByteCount(line)`. When `_overflow` is set (only after the constructor cut a
     header) or `Exceeds(size)` holds, it returns `false` and changes nothing, the flag included.
   - Otherwise the held-back empty lines and the line are appended as in `Add`, the flag is or-ed in, and it returns
     `true`.
   - `TryAdd` never sets `_overflow` or `_omitted`, so a message built only through it never has a marker. The append
     tail shared with the raw overload (`AppendEmpty`, the line feed and text, `_bytes`, `_lines`, `UpdateMark`) is
     factored into one private method.
   - `Add(ReadOnlySpan<byte>, bool)` keeps its behavior, and the error log is unchanged.
5. **`JournalExportParser.ParseAsync`** and **`SyslogParser.ParseAsync`** use `new SystemLogGrouper()` in place of
   `new KernelReportGrouper()`, created inside `ParseAsync` as today (never a field of the parser, AC9 (c)).
   `KernelReportGrouper.EmitAsync` stays as it is. This happens in step 6, not in the skeleton (see *Signatures*).

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
| Vandox.Core | `LogParsing/MariaDbMessage.cs` | new method `TryAdd(string, bool)`; shared append tail factored out |
| Vandox.Core | `LogParsing/JournalExportParser.cs`, `LogParsing/SyslogParser.cs` | `ParseAsync` uses `SystemLogGrouper` (bodies only) |
| docs | `docs/areas/log-import.md`, `docs/decisions/0088-...md`, `docs/decisions/0086-...md`, `docs/decisions/README.md` | done by the Lead in step 2, revised in this round |
| repo | `README.md`, `.squad/project.md` | Dev, step 6 |

`LogParsing/MariaDbEventClassifier.cs` is final as it stands: it was read, and its `Classify(MariaDbLine)` is used
unchanged.

## Signatures (for the Dev's skeleton)

```csharp
// src/Vandox.Core/LogParsing/MariaDbLineGrouper.cs (new)
/// <summary>
/// Joins the MariaDB lines that the journal and syslog parsers read (program <c>mariadbd</c> or <c>mysqld</c>) into entries: a
/// header line and the following lines without a header of the same host, program and process within <see cref="MaxSpan"/> of
/// the header line, as long as the message stays within the text limit, with the header and event rules of the MariaDB error
/// log. A line that does not join is passed on unchanged, so no text is dropped. One entry is open at a time, so memory does
/// not depend on the input.
/// </summary>
internal sealed class MariaDbLineGrouper
{
    /// <summary>
    /// The most time a line of an entry may lie before or after its header line.
    /// </summary>
    internal static readonly TimeSpan MaxSpan = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Takes the next record and appends the records to emit now, in order: the record of the open entry when this record ends
    /// it, and this record when it does not join an entry.
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

// src/Vandox.Core/LogParsing/MariaDbMessage.cs (new method; Add(ReadOnlySpan<byte>, bool) stays)
/// <summary>
/// Adds a continuation line that is already decoded when the message with it, and with the empty lines held back before it,
/// stays within <see cref="ModelLimits.MaxTextBytes"/> UTF-8 bytes; an empty line is held back and always taken.
/// </summary>
/// <param name="line">The text of the line</param>
/// <param name="truncated"><c>true</c> when the line was cut before</param>
/// <returns><c>true</c> when the line was taken; <c>false</c> when it does not fit, and the message is unchanged</returns>
internal bool TryAdd(string line, bool truncated);
```

The skeleton adds the two new files, with `MaxSpan` as written above (a value, not behavior), and the new method
`MariaDbMessage.TryAdd(string, bool)`, each method body `throw new NotImplementedException();`, plus
`#pragma warning disable RH2003, S2325` as *Skeleton* in `.squad/stack.md` describes. It does **not** touch the existing
members. `Add(ReadOnlySpan<byte>, bool)` keeps its body, and `JournalExportParser` and `SyslogParser` are not rewired in
step 4. That way every existing test stays green, and the new tests fail on assertions or `NotImplementedException`. In
step 6 the Dev factors the shared tail of `MariaDbMessage` and rewires both parsers. No existing file the change touches
is final as it stands: `MariaDbMessage.cs`, `JournalExportParser.cs` and `SyslogParser.cs` change in step 6.

## Test files

- `tests/Vandox.Core.Tests/MariaDbLineGrouperTests.cs` (new): AC1 to AC7 (AC6 with the time bound (e) to (g), AC7 (a)
  to (e) with the size end), AC9 (a) and (b), AC12 (a) to (c).
- `tests/Vandox.Core.Tests/SystemLogGrouperTests.cs` (new): AC12 (d) and AC14, with its own private `RetainedBytes`.
- `tests/Vandox.Core.Tests/MariaDbMessageTests.cs` (extend): AC7 at unit level, `TryAdd(string, bool)`.
- `tests/Vandox.Core.Tests/JournalExportParserTests.cs` (extend): AC1, AC3 (binary `MESSAGE` with a line feed), AC6 (e),
  AC8, AC9 (c), AC10, AC11, AC13 (the first and third feed) and AC16 (b) end to end through the parser.
- `tests/Vandox.Core.Tests/SyslogParserTests.cs` (extend): AC2, AC6 (e), AC8 (RFC 3339 and year-less), AC9 (c), AC10,
  AC11, AC13 (the second feed) and AC16 (a) and (c) end to end.

`MariaDbEventClassifierTests.cs` is not extended: the classifier does not change.

Helpers: `JournalExportBuilder` (`Entry`, `Binary`) and `RecordingEmitter` already exist. A shared helper the Tester adds
for building MariaDB records is an `internal` class of `tests/Vandox.Core.Tests` and gets a mention in the *log parsers
(C#)* row of *Test doubles* in `.squad/project.md` (Tester).

How heap bounds are measured (lesson from #17): the tests build Debug (`dotnet test` without `-c Release`).
- AC12 measures retained bytes with one private `RetainedBytes` helper per test class, which wraps
  `GC.GetTotalMemory(true)` in `#pragma warning disable/restore S1215`, as `KernelReportGrouperTests` does.
- Any allocation bound the Tester adds is measured beyond a baseline loop over the same records, for example through
  `KernelReportGrouper` alone. It is never an absolute number.

Existing test code that calls a changed signature: **none**. Only a method (`TryAdd`), two types and a field are added;
no existing signature changes or disappears.

## Areas

- **Log import** (`docs/areas/log-import.md`), written by the Lead in step 2 and revised in this round:
  - *System log parsers*: the stage order.
  - *Duplicates*: a journal export and syslog files of the same period.
  - *Events*: the recovery state in input order over the entries of every host.
  - *Forged lines*: a pointer to the new section.
  - *Lines from the journal and syslog*: a new subsection under *MariaDB error log*, which replaces *Default
    installations*. It holds the key, the 60-second bound, the size end (a line that does not fit ends the entry, no
    marker), the empty lines journald drops, the multi-host recovery limitation, and the forging cases of a syslog file
    (revised after the plan security review).
  - *Related decisions* (0088 line) and *Implementation* (the two new types).
- Wire format and Storage: none. `event` exists, its name rule and its column are unchanged.

## Documentation updates

| File | Edit | Owner |
| ---- | ---- | ----- |
| `docs/areas/log-import.md` | as in *Areas* | Lead (done, step 2 and this revision) |
| `docs/decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md` | extended (context, options 20 to 38, decision, consequences), status `Proposed`; revision 1 chose options 31 (time bound) and 34 (one recovery state) and added the journald empty-line fact; revision 2 (plan security review) rejected option 37, chose option 38 (split at the text limit) and completed option 36 and the consequences with the syslog forging cases | Lead (done, step 2 and both revisions) |
| `docs/decisions/README.md` | row 0088: title extended, status `Proposed` | Lead (done, step 2) |
| `docs/decisions/0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md` | stale consequence about the built-in list corrected (related defect) | Lead (done, step 2) |
| `README.md`, *Import logs*, MariaDB bullet | Replace the last sentence ("MariaDB sends its error log to the journal by default under systemd; those lines arrive through the journal and syslog parsers as plain lines without events."). The new text says that in a journal export or a syslog file, lines of `mariadbd` and `mysqld` that start with MariaDB's time stamp are read with the same rules. An entry with its crash report becomes one record of at most 16,384 bytes with the priority and event of its header; lines beyond that are stored as plain lines, not cut. Time, host and process ID come from the journal or syslog line, and the source type stays `journal` or `syslog`. A journal export needs no `import.time_zone`. | Dev |
| `.squad/project.md`, *Security areas* 10 | add `SystemLogGrouper` and `MariaDbLineGrouper` next to `KernelReportGrouper`. The text says: MariaDB lines of the journal and syslog are joined per host, program and pid, within 60 seconds of the header line and 16,384 bytes, into one open entry; a line that does not fit ends it and is kept as a plain line. Following finding 4 of the plan security review, it also says that the program filter (`mariadbd`, `mysqld`) is a classification, not a trust boundary, and that local processes can forge entries and events and, in a syslog file, join or split the server's entry (record 0088, option 36). The record list already names 0088. | Dev |
| `.squad/project.md`, *Test doubles*, row *log parsers (C#)* | only if the Tester adds a shared helper | Tester |
| `docs/ARCHITECTURE.md` | none. No guarantee or flow changes, and the component list stays true. | n/a |

## Architecture check

- **No data gaps unless explicitly recorded.** Not touched: this is the import path, not collection. The text of an
  input line is never dropped or reduced to a count. Every mapped record is either emitted unchanged or joins exactly
  one entry, whose message holds its whole text. The one exception holds no text: empty lines held back at the end of an
  entry (trailing empty lines) are dropped, as in the error log, also when a line that does not fit ends the entry
  after them. Two kinds of line end the entry and are emitted unchanged, as are the following lines of the key: a line
  beyond the time bound, and one that would make the entry longer than 16,384 bytes. The revision-1 sentence ("No input
  line is dropped") was wrong for the cut of an oversized entry, which the error log's `[N lines omitted]` rule would
  have caused. Option 38 removes that cut (*Security review (plan)*, finding 1).
- **A hanging collector never blocks the agent.** Not touched (agent).
- **Backfilled data never alerts by itself.** Not touched: imported records stay origin `import`.
- **Parser contract** (*Parsers* in the area document):
  - The output stays deterministic.
  - Records written inside a combined record are emitted first.
  - The open entry is emitted only at the normal end, so the importer's resume by count keeps working.
  - Memory stays bounded independently of the input: the open entry's message is at most 16,384 bytes, and the staging
    list of `SystemLogGrouper` is emptied by every call (AC12).
  - There are no new skip reasons.
- No new parser, source type, configuration option, wire field or schema change, so the *Integration surface* entries
  for a parser, an option or a wire field do not apply.

## Security considerations

- [x] **Limits before or after decoding.** Records reach the stage already decoded and cut by the parsers. The journal
  reader and the line reader keep their raw read bounds of 16 KiB, unchanged. Every new size rule counts UTF-8 bytes of
  the decoded text: the entry text is at most 16,384 bytes, the kept head at most 16,320 bytes, and U+FFFD counts 3. The
  header grammar is checked on the UTF-8 encoding of the decoded message. It is ASCII, so U+FFFD (`EF BF BD`) never
  matches a digit, space or bracket of a header. The encode buffer is 16,384 bytes. A message that does not fit is no
  header and is never cut or thrown on. The time bound is no size limit: it compares two instants that the parsers
  already validated. Reaching the size limit ends the entry instead of cutting it (0088 option 38). The line that does
  not fit is passed on unchanged, so the limit bounds memory without dropping text.
- [x] **Exceptions that reach a user-visible reason.** No new ones.
  - The grouper does not throw on input: `MariaDbLine.TryParse` never throws, `TryGetBytes` does not throw, and
    `MariaDbMessage` counts in `long`. `TimeSpan.Duration()` throws only for `TimeSpan.MinValue`; the difference of two
    `DateTimeOffset` values lies within about 10,000 years, far inside the `TimeSpan` range, so it cannot.
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
  - Time: `__REALTIME_TIMESTAMP` (journald's reception time), or the syslog line's time (written by the syslog daemon).
    It only decides whether a line joins; a line beyond the bound is emitted unchanged, so the time cannot drop a line.
  - A message with a line feed is excluded.

  Consequence: the program filter is a classification and no trust boundary. Each effect a forger can have is listed
  below with where it is documented (area document *Forged lines* under *Lines from the journal and syslog*, 0088
  option 36 and *Consequences*) and the AC that pins it:
  1. **Forge an entry** with any level and event, in the journal and syslog. Accepted (0088 options 19 and 36, 0086
     option 19).
  2. **End the server's open entry** with a forged header of any key. The server's later continuation lines are then
     emitted as plain lines: in the journal always, in syslog when the forged pid differs. Nothing is lost (AC6 (d)).
  3. **Syslog only: join the server's open entry** with lines without a header under the server's pid, within 60
     seconds of its header. They can fill the entry to 16,384 bytes. The server's next line then ends the entry and it,
     like the server's lines after it, is emitted as a plain line, never counted away (AC16 (a)). Revision 1 missed this
     suppression path (finding 1). Under the error log's cut rule the server's lines would have been reduced to
     `[N lines omitted]`.
  4. **Syslog only: take in the server's lines** with a forged header under the server's pid. The header opens an entry
     of the server's key, and the server's following continuation lines join it, within 60 seconds of the forged header
     and 16,384 bytes, under the forged level and event. They are stored, but under a forged classification (AC16 (c)).
     Revision 1 did not state this either; it is now documented.
  5. **End a recovery** with a forged `started` or start line. This is accepted with any event (option 36, AC9).
  6. **A file shaped like a journal export** (for example one a web-space user wrote under a saved `/var/www`) carries
     any `_PID` its writer chose. The grouping and recovery state belong to one parse of one file, so such a file cannot
     reach the lines of another file (AC9 (c) shows that no state crosses parses).

  The `_PID` key closes effects 3 and 4 in a journal that journald wrote (AC16 (b) pins `_PID` over `SYSLOG_PID`). In
  every case a server line is stored with its whole text, in an entry or as a plain line. In a syslog file the lines
  joined in one message can come from different writers, which #21 and #23 must not read as the server's (0088
  *Consequences*).
- **Cost.** The work per line is linear in the message (one encode, one UTF-8 byte count, ordinal string operations, no
  regular expression). Memory is the open entry's text, at most 16 KiB, plus one reused 16 KiB buffer and counters. The
  staging list of `SystemLogGrouper` adds at most two records, and every call empties it.
- **Display.** Messages are stored as before. Escaping in logs and the web UI is unchanged (security area 12).

## Decision records

- `docs/decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md`: extended in place,
  because it is unreleased (no `v*` tag) and on the same topic. Its status and index row are set to **Proposed** for
  this run, and step 9 sets them back to `Accepted`. Revision 1 chose option 31 (a 60-second bound from the header)
  and option 34 (one recovery state, as in the error log), rejected option 35 and the variants of 31 with their reasons,
  and recorded that journald drops empty lines (context, option 33, consequences). Revision 2, after the plan security
  review, made these changes:
  - It added options 37 (the error log's cut, rejected, with the variant "keep the marker and emit the omitted lines")
    and 38 (a line that does not fit ends the entry, chosen).
  - It extended option 30 (the size end), option 33 (the parity holds within the limit) and option 36 (the syslog
    forging cases and the per-file state).
  - It updated the decision paragraph and the consequences (forging, the split of an oversized entry, the line feeds
    that #21 and #23 must not trust).
- `docs/decisions/0086-...md`: one stale consequence corrected. The decision is unchanged and the status stays
  `Accepted`.

## Challenge

Devil's Advocate, round 1: 0 major, 3 minor objections. All three are answered below.

1. **Per-host recovery state is unrequested and wrong when two hosts interleave.** Accepted. Issue #165 does not mention
   hosts, Vandox reads one server, and its journal export carries one `_HOSTNAME`. The one-slot design was indeed wrong
   in the interleaved case: a start of host B dropped host A's open recovery. A correct state for every host needs a map
   that hostile host names grow, and so a bound of its own. Nothing in the issue pays for that. Changes:
   - The recovery state is that of #17: one `MariaDbEventClassifier` per grouper and per parse, unchanged (no
     `Classify(MariaDbLine, string)` overload, no state change).
   - AC9 is rewritten. It now pins the multi-host limitation (a) and checks that two parses share no state (c).
   - The classifier is removed from *Approach*, *Affected projects and types*, *Signatures* and *Test files*.
   - 0088: option 34 is chosen and option 35 is rejected with these reasons. The decision and consequences name the
     limitation.
   - The area document (*Events*, *Lines from the journal and syslog*) and the spec describe it as a limitation.

   The join key keeps the host (AC6 (c)), as `KernelReportGrouper` does. That costs one string comparison and was not
   part of the objection.
2. **No time bound on how long an entry stays open.** Accepted. The reason option 31 gave ("the error log has none") does
   not carry over: every journal entry and syslog line has its own time. `KernelReportGrouper.cs:155` already bounds a
   report by `MaxSpan` (60 s) from its first line. New rule: a line of the entry's key without a header joins only when
   its time is at most `MariaDbLineGrouper.MaxSpan` (60 s) before or after the header line's. Otherwise the entry is
   emitted, and then the line unchanged.
   - The bound counts from the header, not as a gap from the previous member. A line every 59 seconds therefore cannot
     keep an entry open, and no member lies more than 60 seconds from the record's `captured_at`.
   - It holds backwards too, against a clock that was set back.
   - Price, recorded in 0088: a crash report whose lines arrive more than 60 seconds after its header is split, and its
     later lines are stored as plain lines. No line is lost.
   - The syslog injection window shrinks to 60 seconds after the server's header.
   - Changed: AC6 (e) to (g), AC4, AC12 (the records carry the header's time), *Approach* step 1, *Signatures*
     (`MaxSpan`), *Security considerations*, 0088 options 30, 31 and 36 with the decision and consequences, the area
     document and the spec.
3. **Parity "through the journal" shown only for synthetic input.** Accepted in part. The journald claim is confirmed
   and the `SyslogLine` claim is refuted:
   - Confirmed in a sparse clone of `systemd/systemd`: at tag v249 (Ubuntu 22.04) and at main c673d99,
     `stdout_stream_log` in `src/journal/journald-stream.c` returns on `if (isempty(p)) return 0;` (v249 line 284).
     That check comes before the line is stored or forwarded to syslog. A real journal therefore stores no empty line of
     MariaDB's standard error, and neither does a syslog file that journald feeds.
   - Refuted: an rsyslog line that ends at `]:` does not lose its program. In `SyslogLine.ParsePid`
     (`SyslogLine.cs:394`), `index` points at `]`, so `index + 1 >= line.Length` is false for a line ending in `]:`.
     The tag gives program `mariadbd`, the pid and an empty message, which `SyslogLineTests.cs:42` already pins for
     `sshd[1]:`. rsyslog's rendering of an empty message was not checked, because journald never forwards one.
   - Changes: AC13's parity is stated "for input that carries the empty lines", and a third feed without the empty
     lines, as journald stores them, must give the error log's messages with the empty lines removed. AC5 calls its
     empty entries synthetic. The spec, the area document (a new bullet in *Lines from the journal and syslog*) and
     0088 (*Context*, option 33, a consequence) say that a real journal loses these lines.

## Security review (plan)

Security, rejection 1 of 2: CHANGES_REQUIRED, 2 blocking and 2 non-blocking findings. All four are answered below.

1. **Blocking: a new data-suppression path is missing from the forging analysis, so "no line dropped" is wrong.**
   Confirmed, and resolved with option (b), mitigation. Confirmed in `MariaDbMessage.cs`: after `Overflow()` (line 152)
   every later non-empty line only runs `Omit()` (lines 85-90, 173-177). In a syslog file a forger can use the server's
   pid (`SyslogParser.cs:136-137`, the tag as written; the journal prefers `_PID`, `JournalExportParser.cs:84`). Its lines would fill the entry, and the server's own later crash
   report lines would survive only in `[N lines omitted]`. A genuine crash report with a long query would lose its query
   and tail the same way. Before #165 each of these lines was a record of its own.
   - **Why (b) over (a).** The product exists to reconstruct outages from these lines. The guarantee is "No data gaps
     unless explicitly recorded", and recording a gap that the change itself creates is the weaker answer when it can be
     avoided at no cost in memory. The error log has to cut because a continuation line there has no time or key of its
     own. A journal entry or syslog line has both, so it can be stored on its own.
   - **The form of (b).** The reviewer's sketch emits the lines after the overflow as plain records but keeps the
     entry open with its marker. That variant was rejected (0088 option 37). The lines between 16,320 and 16,384 bytes
     are joined before the overflow is known and would still be cut away, and the marker would call stored lines
     "omitted". Chosen instead (0088 option 38): a line that does not fit ends the entry, exactly like a line beyond
     the time bound. The record holds the whole entry so far, without a marker, at most 16,384 bytes. The line and the
     following lines of its key are emitted unchanged.
   - **Changes.** *Approach* steps 1 and 4 (`MariaDbMessage.TryAdd` replaces the planned `Add(string, bool)`),
     *Signatures*, AC7 (rewritten: (a) to (e) and the unit level), AC12 (a) to (c), the new AC16 (the injection
     scenario in syslog and journal, and the forged header under the server's pid), and *Test files*. The
     *Architecture check* sentence is corrected, and *Security considerations* now lists each forging effect with its
     AC.
   - **Related gap found while answering.** In a syslog file a forged *header* under the server's pid takes in the
     server's following continuation lines under the forged level and event. The area document said the rest of the
     entry was "emitted as plain lines", which holds only in the journal. This is now documented as effect 4 and pinned
     by AC16 (c).
   - **Documents.** In the area document: *Lines from the journal and syslog* (the end rules, the record bullet with
     no marker, the parity within 16,384 bytes, memory), *Forged lines* (rewritten) and *Related decisions*. In 0088:
     options 30, 33, 36, 37 and 38, the decision and the consequences. Also the spec, README text and `.squad/project.md`
     text (Dev).
   - **Parity.** It holds for every entry within 16,384 bytes. The fixture has 3,692 bytes in all, so AC5 and AC13 are
     unchanged.
   - **User-visible behavior.** An oversized MariaDB entry from the journal or syslog is split, not cut. This is settled
     here, without the Product Manager. The issue's "bounds of the error log parser" are kept as the size of a record
     and as memory that does not depend on the input. Compared with `main`, which stores every line, more is grouped
     and nothing is lost. The same split already exists for the time bound (option 31, revision 1).
2. **Blocking: the memory bound of `SystemLogGrouper` is claimed but neither specified nor tested.** Accepted.
   *Approach* step 2 now names the staging list `_staged`. It is emptied with `Clear()` after the forward in both `Add`
   and `Finish`, and holds at most two records during a call. New AC12 (d) measures retained bytes through
   `SystemLogGrouper`: 100,000 mixed records and 100,000 `mariadbd` continuation records must retain less than 1 MiB,
   where a kept staging list would retain at least 15 MB. AC14 gains an exactly-once check, which fails if the list is
   forwarded again. *Architecture check* and *Cost* name the staging list.
3. **Non-blocking: the line-feed rule (option 26) is a definition, not a guard.** Agreed; option 26 stays as it is.
   Only the start of a message is checked for a header, so `\r`, NEL (U+0085), U+2028 or U+2029 inside a message create
   no header and no new line for these rules. The actionable part is now a consequence of 0088 and a sentence in the
   area document's *Forged lines*: in a syslog file the `\n`-joined lines of an entry can come from different writers,
   so #21 and #23 must treat them as untrusted when they split a message. It is also listed under *Out of scope /
   follow-ups*.
4. **Non-blocking: the `.squad/project.md` area-10 text should keep the forging limitation and call the program filter
   a classification.** Accepted. The *Documentation updates* row for `.squad/project.md` (Dev) now prescribes both,
   with a reference to 0088 option 36, and so does task 13.

## Out of scope / follow-ups

- #166: a JSON `null` in a string field of the C# decoder, tracked separately.
- #21 and #23: queries by `event`, counting an entry stored from both journal and syslog once, and whether to keep
  `_TRANSPORT` to tell the server apart from a forger (0088 option 25). If they split a stored message at its line
  feeds, the lines are untrusted: in a syslog file they can come from different writers (0088 *Consequences*, security
  review finding 3).
- Kernel reports keep their documented equivalent: a forger logging as `kernel` can push real kernel lines into the
  omitted-lines count of a fake report (0086 option 19, accepted). This change does not touch `KernelReportGrouper`. If
  the split rule of 0088 option 38 should apply there too, that needs its own issue. None is proposed, because the
  kernel report's head-and-tail cut keeps the end line by design.
- #37: whether the agent fills `event` when it ships lines live.
- Binary journal files and MySQL 8's format: not read or not classified, unchanged.
- A recovery state per host for inputs with several hosts: not planned (0088 option 35). It would need a new issue if a
  multi-host import becomes a goal.

## Approval

Lead, step 9 (approve-pr), on head 99d21ba (code as reviewed on 7c87b0e). Deviations from the plan text, all accepted:

- **Priority.** *Approach* step 1 wrote `Priority = header.Priority ?? line.Priority`, which reads inverted against AC1.
  The build takes the MariaDB level first and the line's priority for a header without a level
  (`_line.Priority ?? _header.Priority`), as AC1, AC2, the spec, the area document and 0088 option 33 require. The ACs
  are binding; the formula in *Approach* was a wording defect.
- **Message.** The entry starts from the parsed `MariaDbLine.Message` (the text after the header prefix), as AC1 and
  *Entries* require; the plan text named the header record's message.
- **`MariaDbLineGrouper.IsServer`** is an `internal static` helper added for the Reihitsu member order (RH7103/RH7110);
  no behavior beyond the plan's program filter.

Step 8 decisions confirmed against the build: N1 (pin the `_overflow` guard of `TryAdd`) fixed by the Tester; N2 (the
`SYSLOG_PID` exception in the area document and 0088 options 30/36) fixed by the Lead and covered by delta round 2. The
`.squad/project.md` edits (security area 10, *Test doubles* row) describe the change and weaken no guarantee.
