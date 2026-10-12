# Plan: Log pattern normalization and frequency tracking

Source: Issue #22 | [spec.md](spec.md)
Status: Draft
Tier: security — the normalizer is a new parser of external log text (security area 10, *Parsing of external input*), and the
change adds a schema step and new statements to the storage write path that every stored log line passes.

## Problem / root cause

Summary of [spec.md](spec.md): every stored log line is normalized into a template; per template (source, program, template text)
the backend stores first seen, last seen, total count and count per UTC hour, plus the stored lines per source and hour; four
queries (template of a line, hourly counts, new templates, templates above their usual level) serve later consumers; the cost for
one million lines is measured and noted.

Claims of the issue, checked against the code:

- *Depends on #21* — confirmed: the signature catalog and detector are on `main` (`7b5347a`, PR #204,
  `src/Vandox.Core/Signatures/`). Templates do not build on the detector; they share its input (stored `log_line` records) and
  its principles (deterministic, linear-time, external text, record 0094).
- *Forensics release, OOM failure mode* — confirmed, `docs/ARCHITECTURE.md` (*Monitored server*, *Data flow*).
- *Store per template first seen, last seen, count per hour* — nothing of the kind exists: no table, type or member about
  templates in `src/`, `tests/` or `tools/` (the only hit for "template" is the message template of `JsonLineLogger`, unrelated).
  The current schema is version 7 (`StorageLimits.SchemaVersion`, `src/Vandox.Storage/StorageLimits.cs:20`;
  `SchemaMigrator._steps`, `src/Vandox.Storage/SchemaMigrator.cs:15-112`).
- *Placeholders for numbers, IDs, IPs, e-mail, paths, hex* — settled as fixed token rules in the area document (below).
- *Usual level* — not defined by the issue; settled in record 0095 option 9 (no user-visible surface depends on it yet, so no
  escalation; the defaults are query parameters).
- *Performance on the reference host, a DS918+* — the squad cannot reach the DS918+. What can be measured here: the
  development host (the session has .NET SDK 10.0.401) with `tools/Vandox.StorageBenchmark`, which already runs on the reference
  host with only the runtime (`docs/BENCHMARKS.md`). The plan extends that tool; the reference-host row is recorded once the
  operator has run it (see AC8 and *Out of scope / follow-ups*).

Facts the plan rests on, from the code:

- `BatchWriter` (`src/Vandox.Storage/BatchWriter.cs`) is the only code that writes `log_lines` (`WriteLogLine`, line 213), per
  record, inside the transaction `SqliteStore.WriteBatchAsync` opens (`src/Vandox.Storage/SqliteStore.cs:285-314`); a duplicate
  agent record returns `false` before any typed row is written (`BatchWriter.Write`, line 104), and an import step is advanced
  before the records (`SqliteStore.cs:296`), so a conflict rolls the whole batch back.
- The record source of an imported line is the parser type (`journal`, `syslog`, `mariadb`, `dovecot`, `nginx`, ...), set by each
  parser (`Source = ParserType`).
- Times are stored as Unix nanoseconds (`StorageTime.ToNanoseconds`); the storable range starts at `long.MinValue` ns
  (1677-09-21T00:12:43.145224192Z), so flooring a time of the first, partial hour would underflow — `HourStart` must clamp.
- `Utf8Text.Cut` is internal to `Vandox.Core` (`src/Vandox.Core/LogParsing/Utf8Text.cs:43`); the normalizer lives in
  `Vandox.Core` and may use it.
- `ServeCommand.Build` registers the store under its reader interfaces (`src/Vandox.Backend/Hosting/ServeCommand.cs:149-152`);
  the new reader interface is registered the same way (covered by the existing `ServeTests`, which start the host).
- Related defect found on the way: none in code. `docs/BENCHMARKS.md` *Background* lists record 0077 twice and 0083 twice
  (wording only); the Dev may tidy it while editing that file.

## Acceptance criteria

The Tester writes the tests named here; "step 6" marks a test that cannot fail on the skeleton and is written in the coverage
pass (`.squad/routing.md`, step 5). Exact template texts are those of the area document's rules table; cases with control
characters (U+0000 and the like) are built in code with C# escapes, never written as literal characters into a file.

- [ ] **AC1 — similar lines, one template.** Lines that differ only in numbers, IDs, IPs, e-mail addresses, paths, hex values or
  white space give the same template, equal to the expected text; lines that differ in literal words do not.
  Tests: `LogTemplateNormalizerMapsSimilarLinesToOneTemplate` (data rows, each two real-shaped lines and the expected template, at
  least: Postfix `connect from unknown[<ip>]`; Postfix queue line `<id>: from=<<email>>, size=<num>`; Dovecot login with
  `rip=`/`lip=` addresses and a session ID that holds a digit; the kernel OOM kill line of the area document's example; a MariaDB
  `Aborted connection <num> to db:` line; an nginx error with a client address and a request path; a systemd line with a pid),
  `LogTemplateNormalizerKeepsLinesWithDifferentTextApart`.
- [ ] **AC2 — every token rule.** Every row a to j of the rules table in `docs/areas/detection-and-reports.md` (*Log templates*,
  *Normalization*) gives its placeholder for each listed example, and rule order holds.
  Tests: `LogTemplateNormalizerReplacesEachTokenForm` (one data row per example of the table), and
  `LogTemplateNormalizerLeavesNearMissesToLaterRules` with at least: `/` → `/`; `a@` → `a@`; `a@b@c` → `a@b@c`;
  `1.2.3.4:123456` → `<num>`; `1.2.3.4/33` → `<num>`; `256.1.1.1` → `<num>`; `10:15:32` → `<num>`; `:::1` → `<num>`;
  `fe80:1` → `<id>:<num>`; `fe80::` → `<id>::`; `0x` → `<num>x`; `deadbeef` → `deadbeef`; `abcdef12` → `<id>`; a UUID with an 11-digit last group → `<id>`; `12345kBytes` → `<id>`; `00:1a:2b:3c:4d:5e` → `<num>:<num>a:<num>b:<num>c:<num>d:<num>e`; Arabic-Indic digits
  (U+0661 U+0662 U+0663) and fullwidth digits (U+FF11 U+FF12 U+FF13) kept as they are.
- [ ] **AC3 — separators, delimiters, text.** Runs of white space and control characters become one space, trimmed at both ends;
  every delimiter is kept and ends a token; trailing `.`/`:` are kept after the replacement; a lone surrogate becomes U+FFFD; the
  template is cut at a character boundary to at most 1,024 UTF-8 bytes counted on the finished template; an empty message (or one
  of separators only) gives the empty template; `null` is refused.
  Tests: `LogTemplateNormalizerCollapsesSeparators` (tab, LF, CR, U+0000, U+0085, U+00A0, leading and trailing runs; U+200B, a
  format character, is kept inside its token), `LogTemplateNormalizerKeepsDelimitersAndTrailingPunctuation` (each of the 22
  delimiters between two numbers; `port 25.`; `4F2A31C0B2:` → `<id>:`; `memory:`; `a...`),
  `LogTemplateNormalizerReplacesLoneSurrogates`, `LogTemplateNormalizerCutsTemplatesAtTheByteLimit` (ASCII over the limit gives
  exactly 1,024 bytes; a three-byte character straddling the limit is dropped whole; a 3,000-byte message that normalizes to
  fewer than 1,024 bytes is not cut), `LogTemplateNormalizerGivesTheEmptyTemplateForAnEmptyMessage`,
  `LogTemplateNormalizerRefusesNull`.
- [ ] **AC4 — linear time (step 6).** Normalization time grows linearly with the message length on hostile shapes.
  Test: `LogTemplateNormalizerTimeGrowsLinearly` — ratio test as in `docs/UNIT_TESTS.md` (*Structure*, the one clock exception):
  the time on twice the base input against the base input, best of three after one warm-up, bounded by three times the highest
  ratio measured for the correct code with coverage on; inputs of 8 KiB base: repeated `1:`, repeated `a@`, repeated `0.`,
  repeated `::1 `, and one token of hex digits and colons without separators.
- [ ] **AC5 — hour start.** `HourStart` floors a nanosecond time to the start of its UTC hour, also before 1970, and clamps a time
  of the first, partial hour of the storable range to `long.MinValue`.
  Tests: `StorageTimeHourStartFloorsToTheHour` (data rows: an exact hour, mid-hour, −1 ns → −3,600,000,000,000, a mid-hour time
  before 1970), `StorageTimeHourStartClampsTheFirstPartialHour` (`long.MinValue` and `long.MinValue + 1` → `long.MinValue`).
- [ ] **AC6 — statistics kept by the write path.** Writing a batch counts every stored log line under (source, program, template):
  count, first seen (earliest), last seen (latest), the template's hour and the source's hour; across batches first seen moves
  earlier for older lines; a resent agent record and a failed batch count nothing; a batch without log lines writes no template
  row; a template is found from a line's source, program and message; hourly counts are returned in order for the hours that
  start between the hour of `from` and `to`.
  Tests: `SqliteStoreWriteBatchCountsTemplatesPerHour`, `SqliteStoreWriteBatchKeysTemplatesBySourceAndProgram`,
  `SqliteStoreWriteBatchMovesFirstSeenEarlierForOlderLines`, `SqliteStoreWriteBatchDoesNotCountDuplicateAgentRecords`,
  `SqliteStoreWriteBatchCountsNothingWhenTheBatchFails` (an import step whose expected count does not match: conflict, and the
  three template tables are unchanged), `SqliteStoreWriteBatchWithoutLogLinesWritesNoTemplate`,
  `SqliteStoreWriteBatchCountsLinesPerSourceAndHour` (reads `log_source_hours` directly), `SqliteStoreFindTemplateReturnsTheTemplateOfAMessage`
  (and `null` for an unknown one), `SqliteStoreTemplateHoursReturnsHourlyCountsInOrder` (from mid-hour includes that hour, `to`
  exclusive, limit).
- [ ] **AC7 — new templates and templates above the usual level.** As specified in *Queries* of the area document:
  Tests: `SqliteStoreNewTemplatesReturnsTemplatesFirstSeenInTheRange`, `SqliteStoreNewTemplatesSkipsSourcesWithoutEarlierLines`,
  `SqliteStoreNewTemplatesFiltersBySourceAndLimits`; `SqliteStoreTemplateSpikesReportsHoursAboveTheUsualLevel` (asserts the usual
  level and the covered hours exactly; boundary: with 48 lines over 24 covered hours, level 2, factor 3, minimum count 1, a count
  of 6 is not reported and 7 is), `SqliteStoreTemplateSpikesLeavesOutHoursWithoutLinesOfTheSource` (counts that would be a spike if
  uncovered hours counted as zero are not one), `SqliteStoreTemplateSpikesNeedsTheMinimumBaselineHours`,
  `SqliteStoreTemplateSpikesNeedsTheMinimumCount`, `SqliteStoreTemplateSpikesReportsATemplateAbsentFromTheBaseline` (usual level 0),
  `SqliteStoreTemplateSpikesOrdersAndLimitsResults`, `SqliteStoreTemplateSpikesClipsTheBaselineAtTheStorableRange` (`from` at the
  start of the storable range: no exception, the window is clipped);
  `SqliteStoreTemplateQueriesRefuseInvalidQueries` (data rows per query and rule, comparing the exact texts below).
- [ ] **AC8 — schema step 8.** Opening a version-7 database migrates it to 8: the three tables and two indexes exist and are empty
  (stored lines get no template rows); a version-9 database is refused.
  Test: `SqliteStoreOpenMigratesVersionSevenAndAddsTemplateTables` (asserts the message
  `store: schema version 9 is not supported, this build uses 8`).
- [ ] **AC9 — one million lines measured (no unit test).** `tools/Vandox.StorageBenchmark --log-lines N` writes N generated log
  lines with their templates and prints the times listed under *Approach*; the development-host result for N = 1,000,000 is in
  `docs/BENCHMARKS.md`, with the procedure for the reference host and its row marked "not measured yet" until the operator runs it.
  Verified by: the Dev runs it in step 6 and pastes the output into `log.md`; the Reviewer checks `docs/BENCHMARKS.md` against
  that output; the Product Manager runs the published tool on the DS918+ (*Out of scope / follow-ups*).

Exact error texts (`InvalidQueryException`, prefix `store: invalid query: ` as `RecordQueries.Require` writes it):

| Rule | Text after the prefix |
| ---- | --------------------- |
| range and limit (all four queries except *find*) | the existing texts of `RecordQueries.ValidateWindow`: `from and to are required`, `from and to must be within the storable range`, `from must be before to`, `limit must be between 1 and 10000` |
| hourly counts: template ID ≤ 0 | `template ID must be positive` |
| usual level: `to - from` above 744 hours | `the range must not exceed 744 hours` |
| usual level: baseline hours outside 1–2,160 | `baseline hours must be between 1 and 2160` |
| usual level: minimum baseline hours outside 1–baseline hours | `minimum baseline hours must be between 1 and the baseline hours` |
| usual level: factor not within 1–1,000 (NaN included) | `factor must be between 1 and 1000` |
| usual level: minimum count below 1 | `minimum count must be at least 1` |

A `null` query, source, program or message is an `ArgumentNullException`. A `SqliteException` in a template query becomes
`StoreException` with `store: reading templates: <message>` (as `QueryAsync` does for records).

## Approach

1. **Normalizer** (`Vandox.Core.Templates.LogTemplateNormalizer`): a static, stateless, hand-written single forward pass over the
   message, exactly the rules of the area document (*Log templates*, *Normalization*), ordinal ASCII comparisons only, no regular
   expression, no culture. Each token is classified by rules a to j in order, each rule a scan of the token bounded by its length;
   rule i classifies the parts of one token with the same rules (no recursion beyond one level). **No shared mutable state:** the
   class has no static field other than constants and read-only tables (no static buffer, no `[ThreadStatic]` buffer), because
   `BatchWriter` (under `_writeLock`) and `FindTemplateAsync` (on reader leases) call `Normalize` concurrently. The output buffer
   is local to the call: either a local `StringBuilder`, or a span over `stackalloc char[StackBufferChars]` with the private
   constant `StackBufferChars = 1024` that, when the output outgrows it, continues in an array rented from
   `ArrayPool<char>.Shared` and returned in a `finally`. A `stackalloc` sized from the input is not allowed (the output can be
   about three times the input, `1 1 1` → `<num> <num> <num>`, and a stack overflow cannot be caught). One string per call; the
   result is cut with `Utf8Text.Cut(…, MaxTemplateBytes, out _)` after lone surrogates are replaced. The Dev may move the token
   classification into an internal static helper in the same namespace, tested through `LogTemplateNormalizerTests`.
2. **Schema step 8** (`SchemaMigrator`), `StorageLimits.SchemaVersion = 8`:
   ```sql
   CREATE TABLE log_templates (
     id INTEGER PRIMARY KEY,
     source TEXT NOT NULL,
     program TEXT NOT NULL,
     template TEXT NOT NULL,
     first_seen INTEGER NOT NULL,
     last_seen INTEGER NOT NULL,
     count INTEGER NOT NULL CHECK (count > 0),
     UNIQUE (source, program, template)
   ) STRICT;
   CREATE INDEX log_templates_source_first_seen ON log_templates(source, first_seen);
   CREATE TABLE log_template_hours (
     template_id INTEGER NOT NULL REFERENCES log_templates(id),
     hour INTEGER NOT NULL,
     count INTEGER NOT NULL CHECK (count > 0),
     PRIMARY KEY (template_id, hour)
   ) STRICT, WITHOUT ROWID;
   CREATE INDEX log_template_hours_hour ON log_template_hours(hour);
   CREATE TABLE log_source_hours (
     source TEXT NOT NULL,
     hour INTEGER NOT NULL,
     count INTEGER NOT NULL CHECK (count > 0),
     PRIMARY KEY (source, hour)
   ) STRICT, WITHOUT ROWID;
   ```
3. **Write path**: `BatchWriter.WriteLogLine` gets the record and adds `(record.Source, line.Program, line.Message,
   capture time)` to a per-transaction `TemplateTally`, which normalizes and sums per key: count, min and max capture time, count
   per `StorageTime.HourStart`, and per (source, hour). `SqliteStore.WriteBatchAsync` calls `writer.WriteTemplates(…)` after the
   records and before the commit; it runs, with prepared, reused commands and bound parameters only:
   `INSERT INTO log_templates(source, program, template, first_seen, last_seen, count) VALUES (…) ON CONFLICT(source, program,
   template) DO UPDATE SET first_seen = min(first_seen, excluded.first_seen), last_seen = max(last_seen, excluded.last_seen),
   count = count + excluded.count RETURNING id`, then an upsert per template hour and per source hour adding the count. An empty
   tally runs nothing (the metric write criterion of record 0083 is untouched).
4. **Queries** (`ILogTemplateReader`, implemented by `SqliteStore` on a reader lease, SQL and validation in `TemplateQueries`):
   *find* refuses a message of more than `ModelLimits.MaxTextBytes` (16,384) UTF-8 bytes (no stored line can be longer, the
   model refuses it), then normalizes the message and reads the row by its unique key; *hourly counts* reads
   `log_template_hours` by primary key range; *new templates* selects `first_seen` in range with `EXISTS (SELECT 1 FROM
   log_templates p WHERE p.source = t.source AND p.first_seen < $from)`; *usual level* is one statement of this shape (names
   and layout are the Dev's, the structure is fixed):
   ```sql
   WITH candidates AS (            -- template-hour rows of the range that reach the minimum count
     SELECT h.template_id, h.hour, h.count, t.source
     FROM log_template_hours h JOIN log_templates t ON t.id = h.template_id
     WHERE h.hour >= $fromHour AND h.hour < $to AND h.count >= $minCount AND ($source = '' OR t.source = $source)),
   windows AS (                    -- once per distinct (source, hour), never per candidate
     SELECT source, hour,
            CASE WHEN hour < $minTime + $baselineNs THEN $minTime ELSE hour - $baselineNs END AS window_start
     FROM (SELECT DISTINCT source, hour FROM candidates)),
   covered AS (
     SELECT w.source, w.hour, w.window_start,
            (SELECT count(*) FROM log_source_hours s
             WHERE s.source = w.source AND s.hour >= w.window_start AND s.hour < w.hour) AS covered_hours
     FROM windows w)
   SELECT … FROM candidates c JOIN covered v ON v.source = c.source AND v.hour = c.hour
   WHERE v.covered_hours >= $minBaselineHours
     AND c.count * v.covered_hours > $factor * (SELECT coalesce(sum(b.count), 0) FROM log_template_hours b
                                               WHERE b.template_id = c.template_id AND b.hour >= v.window_start AND b.hour < c.hour)
   ORDER BY c.hour, c.count DESC, c.template_id LIMIT $limit
   ```
   `$fromHour = HourStart(from)`, `$minTime = long.MinValue`, `$baselineNs = baseline hours · 3,600 · 10^9` (at most 2,160 hours,
   no overflow); the `CASE` keeps `hour − $baselineNs` from underflowing (`$minTime + $baselineNs` cannot overflow). The usual
   level returned is `sum / covered_hours`. Rows map to `LogTemplate`, `TemplateHour` and `TemplateSpike`.
   **Cost, stated as it is** (also in the area document and *Security considerations*): the candidates are every
   `log_template_hours` row in the up to 744 hours of the range with a count of at least `minCount` (index
   `log_template_hours_hour`); neither the range nor the limit bounds their number, and `ORDER BY` evaluates all of them before
   `LIMIT`. The work is (candidates × the template's rows in its baseline window, at most `baseline hours` each, by primary key)
   plus (distinct source-hours of the candidates, at most sources × 744, × the source's rows in the window, by primary key).
   Microsoft.Data.Sqlite 10.0.12 calls neither `sqlite3_interrupt` nor a progress handler, so a cancellation token or deadline
   is checked only before the statement starts and between rows, and the first row arrives only after the sort: **a deadline
   does not stop a running usual-level query.** There is no candidate cap (see *Options considered*).
5. **Wiring**: `ServeCommand.Build` registers `ILogTemplateReader` like `ILogSearcher`.
6. **Benchmark** (`tools/Vandox.StorageBenchmark`): `Program.TryParse` accepts `--log-lines N` (1 to 10,000,000) with an optional
   `--size N` (batch size, default 10,000); `--batches` or `--warmup` together with `--log-lines` is a usage error. The new
   `TemplateBenchmark.RunAsync` builds the lines deterministically from the line index (no `Random`): at least 20 message shapes
   modeled on the real formats (Postfix connect/disconnect/queue lines, Dovecot logins, the kernel OOM kill line, MariaDB aborted
   connections, nginx errors with client address and path, systemd start/stop), each with its source (`syslog`, `journal`,
   `mariadb`, `nginx`) and program, numbers, addresses, IDs and e-mail addresses varying with the index, capture times in order over
   30 days, origin `import`, no agent ID, no import step. It prints: runtime and host line; the time to normalize the N messages in
   memory (lines/s); the time to write them (total, median batch, lines/s); the number of templates and template hours; the time of
   a new-templates query and of a usual-level query over the last 744 hours; and, for N = 1,000,000 with the default batch size, the
   verdict against the criterion "1,000,000 log lines written with their templates in under 10 minutes on the reference host"
   (stating that only the reference host counts). `[ExcludeFromCodeCoverage]` like `Program`.

## Options considered

Record 0095 weighs them; in short:

- **Normalization:** fixed hand-written token rules (chosen; a separate `<hex>` rule for hex strings without prefix was dropped
  because a random ID would flip between `<hex>` and `<id>` with its characters) against a list of non-backtracking regular expressions (one pass
  per expression, order-dependent overlaps, slower on the J3455) and online clustering such as Drain (stateful and order-dependent,
  so first seen would not be a fact of the data; against record 0008).
- **Where the statistics live:** the write path in the batch's transaction (chosen: counted exactly once, consistent with dedup,
  rollback and import resume) against on-demand scans like the signatures (one million lines per question, first seen limited to
  the 90-day log retention) and a separate catch-up pass with a cursor (needs a trigger and its own resume state, stale counts).
- **Key:** source, program and template (chosen) against the template alone (copies in `syslog`, `kern.log` and the journal add
  up, and a source that starts later looks like a rise).
- **Usual level:** mean over covered hours times a factor with a minimum count (chosen) against every hour counted as zero (turns
  unknown into quiet, against the gap guarantee), median or seasonal baselines (more data and per-hour lists; later, with
  alerting) and fixed thresholds (no basis to choose them).
- **Measurement:** extend the existing benchmark tool (chosen; it already runs on the DS918+ with the runtime only) against an
  opt-in test (needs the SDK on the reference host).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `src/Vandox.Core/Templates/LogTemplateNormalizer.cs` | new |
| Vandox.Storage | `src/Vandox.Storage/ILogTemplateReader.cs` | new |
| Vandox.Storage | `src/Vandox.Storage/LogTemplate.cs`, `TemplateHour.cs`, `TemplateSpike.cs` | new result types |
| Vandox.Storage | `src/Vandox.Storage/NewTemplateQuery.cs`, `TemplateHourQuery.cs`, `TemplateSpikeQuery.cs` | new query types |
| Vandox.Storage | `src/Vandox.Storage/TemplateTally.cs` | new, internal |
| Vandox.Storage | `src/Vandox.Storage/TemplateQueries.cs` | new, internal |
| Vandox.Storage | `src/Vandox.Storage/StorageTime.cs` | add `HourStart` |
| Vandox.Storage | `src/Vandox.Storage/StorageLimits.cs` | `SchemaVersion` 8, new limits |
| Vandox.Storage | `src/Vandox.Storage/SchemaMigrator.cs` | step 8 |
| Vandox.Storage | `src/Vandox.Storage/BatchWriter.cs` | tally, `WriteTemplates`, `WriteLogLine` gets the record |
| Vandox.Storage | `src/Vandox.Storage/SqliteStore.cs` | implements `ILogTemplateReader`; calls `WriteTemplates` |
| Vandox.Backend | `src/Vandox.Backend/Hosting/ServeCommand.cs` | registers `ILogTemplateReader` |
| tools | `tools/Vandox.StorageBenchmark/Program.cs` | `--log-lines` |
| tools | `tools/Vandox.StorageBenchmark/TemplateBenchmark.cs` | new |

`SqliteStore.cs`, `BatchWriter.cs`, `SchemaMigrator.cs`, `StorageTime.cs`, `StorageLimits.cs`, `ServeCommand.cs` and
`Program.cs` were read and are rewritten only where listed.

## Signatures (for the Dev's skeleton)

```csharp
// src/Vandox.Core/Templates/LogTemplateNormalizer.cs
namespace Vandox.Core.Templates;
public static class LogTemplateNormalizer
{
    public const int MaxTemplateBytes = 1024;
    public const string NumberPlaceholder = "<num>";
    public const string HexPlaceholder = "<hex>";
    public const string IdPlaceholder = "<id>";
    public const string IpPlaceholder = "<ip>";
    public const string EmailPlaceholder = "<email>";
    public const string PathPlaceholder = "<path>";
    /// <exception cref="ArgumentNullException">message is null</exception>
    public static string Normalize(string message);
}

// src/Vandox.Storage/ILogTemplateReader.cs
namespace Vandox.Storage;
public interface ILogTemplateReader
{
    Task<LogTemplate?> FindTemplateAsync(string source, string program, string message, CancellationToken cancellationToken);
    Task<IReadOnlyList<TemplateHour>> TemplateHoursAsync(TemplateHourQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<LogTemplate>> NewTemplatesAsync(NewTemplateQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<TemplateSpike>> TemplateSpikesAsync(TemplateSpikeQuery query, CancellationToken cancellationToken);
}

public sealed class LogTemplate          // get; set; properties, defaults string.Empty
{
    public long Id { get; set; }
    public string Source { get; set; }
    public string Program { get; set; }
    public string Template { get; set; }
    public DateTimeOffset FirstSeen { get; set; }   // UTC
    public DateTimeOffset LastSeen { get; set; }    // UTC
    public long Count { get; set; }
}

public sealed class TemplateHour
{
    public DateTimeOffset Hour { get; set; }        // UTC start of the hour
    public long Count { get; set; }
}

public sealed class TemplateSpike
{
    public LogTemplate Template { get; set; } = new();
    public DateTimeOffset Hour { get; set; }
    public long Count { get; set; }
    public double UsualLevel { get; set; }
    public int CoveredHours { get; set; }
}

public sealed class NewTemplateQuery
{
    public string Source { get; set; } = string.Empty;   // empty: every source
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public int Limit { get; set; }
}

public sealed class TemplateHourQuery
{
    public long TemplateId { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public int Limit { get; set; }
}

public sealed class TemplateSpikeQuery
{
    public string Source { get; set; } = string.Empty;   // empty: every source
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public int BaselineHours { get; set; } = StorageLimits.DefaultBaselineHours;
    public int MinBaselineHours { get; set; } = StorageLimits.DefaultMinBaselineHours;
    public double Factor { get; set; } = StorageLimits.DefaultSpikeFactor;
    public long MinCount { get; set; } = StorageLimits.DefaultSpikeMinCount;
    public int Limit { get; set; }
}

// src/Vandox.Storage/StorageLimits.cs (added; SchemaVersion becomes 8)
public const int MaxSpikeRangeHours = 744;
public const int MaxBaselineHours = 2160;
public const double MaxSpikeFactor = 1000;
public const int DefaultBaselineHours = 168;
public const int DefaultMinBaselineHours = 24;
public const double DefaultSpikeFactor = 3;
public const long DefaultSpikeMinCount = 10;

// src/Vandox.Storage/StorageTime.cs (added)
internal static long HourStart(long nanoseconds);

// src/Vandox.Storage/TemplateTally.cs
internal sealed class TemplateTally : IDisposable
{
    internal TemplateTally(SqliteConnection connection, SqliteTransaction transaction);
    internal void Add(string source, string program, string message, long capturedAt);   // capturedAt in Unix ns
    internal void Write(CancellationToken cancellationToken);                            // nothing when empty
    public void Dispose();
}

// src/Vandox.Storage/TemplateQueries.cs
internal static class TemplateQueries
{
    internal static void Validate(TemplateHourQuery query);
    internal static void Validate(NewTemplateQuery query);
    internal static void Validate(TemplateSpikeQuery query);
    internal static (string Sql, Dictionary<string, object> Parameters) Build(TemplateHourQuery query);
    internal static (string Sql, Dictionary<string, object> Parameters) Build(NewTemplateQuery query);
    internal static (string Sql, Dictionary<string, object> Parameters) Build(TemplateSpikeQuery query);
    internal static (string Sql, Dictionary<string, object> Parameters) BuildFind(string source, string program, string template);
}

// src/Vandox.Storage/BatchWriter.cs (changed)
internal void WriteTemplates(CancellationToken cancellationToken);
private void WriteLogLine(long id, DataRecord record, LogLine line);

// src/Vandox.Storage/SqliteStore.cs: class declaration adds ILogTemplateReader, region "ILogTemplateReader" with the four members

// tools/Vandox.StorageBenchmark/TemplateBenchmark.cs
[ExcludeFromCodeCoverage]
internal static class TemplateBenchmark
{
    internal static Task RunAsync(string directory, int lines, int size, CancellationToken cancellationToken);
}
```

`Program.TryParse` (private) gets an `out int logLines` parameter; nothing outside the tool calls it.

## Test files

- `tests/Vandox.Core.Tests/LogTemplateNormalizerTests.cs` — AC1–AC4.
- `tests/Vandox.Storage.Tests/StorageTimeTests.cs` (existing file, new methods) — AC5.
- `tests/Vandox.Storage.Tests/SqliteStoreTemplateTests.cs` — AC6, AC7 (the store's tests are split by topic as
  `SqliteStoreOpenTests`, `SqliteStoreQueryTests`, `SqliteStoreWriteTests`; the sample builders go into the existing
  `tests/Vandox.Storage.Tests/Samples.cs` where shared).
- `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` (existing file, new method) — AC8.

Existing test code affected by the schema step (no signature change, a behavior change of the migration), adapted by the
**Tester in step 5** together with AC8, assertions kept equivalent:

- `SqliteStoreOpenTests`: add `private const string UndoStepEight = "DROP TABLE log_source_hours; DROP TABLE log_template_hours;
  DROP TABLE log_templates"` and put it first in every undo chain (the tests at about lines 162, 190, 224, 269 and 310, which
  rebuild versions 2 to 6); the literal `"7"` assertions (about lines 277 and 319) become `StorageLimits.SchemaVersion` text; the
  version-8-refused block of `SqliteStoreOpenMigratesVersionSixAndAddsLineHashes` (about lines 324-328) moves into the AC8 test as
  version 9. These tests fail on the skeleton (the step does not exist yet) and pass in step 6.
- No other test builds `BatchWriter`, asserts the table list, or calls a changed signature; `Program.TryParse` has no test.

## Areas

- `docs/areas/detection-and-reports.md` — new section *Log templates* (normalization rules, statistics, queries, limits), scope
  sentence, related decision, implementation line. **Written by the Lead with this plan.**
- `docs/areas/storage.md` — schema version 8 and its tables and indexes, the write-path rule for template statistics, the reader
  interface, retention note, related decision, implementation line. **Written by the Lead with this plan.**

No new area: templates are analysis of stored lines and sit next to the signatures.

## Documentation updates

| File | Change | Owner |
| ---- | ------ | ----- |
| `docs/areas/detection-and-reports.md`, `docs/areas/storage.md` | as above | Lead (done in step 2) |
| `docs/ARCHITECTURE.md` | intro list, components (`Vandox.Core`, `Vandox.Storage`), *Storage and retention* bullet *Log templates* and version 8, record links | Lead (done in step 2) |
| `docs/decisions/0095-…md` and its index row | new, `Proposed` | Lead (done in step 2) |
| `docs/BENCHMARKS.md` | new section *Log templates* (criterion from record 0095: one million lines with templates under 10 minutes on the reference host; procedure with `--log-lines`; results table with the development-host row and a reference-host row "not measured yet"); one development-host row of the metric measurement after this change, showing the metric criterion unaffected | Dev |
| `.squad/project.md` | security area 10: the normalizer (`Vandox.Core.Templates.LogTemplateNormalizer`: hand-written, linear, ordinal ASCII, output cut at 1,024 UTF-8 bytes, control characters removed) and the template queries (bound parameters, range and limit bounds), record 0095 in the list; area 12: templates are external text, escaped by whoever shows them | Dev |
| `.squad/stack.md` | *Layout*: `src/Vandox.Core` lists "log templates" | Dev |
| `README.md` | none: no option, command or secret | — |

## Architecture check

- *No data gaps unless explicitly recorded* — preserved and applied: hours in which a source has no line are unknown in the
  usual-level query and never counted as quiet, a source without earlier lines makes nothing "new", and lines stored before
  schema step 8 make the hours before the upgrade unknown rather than zero.
- *Backfilled data never raises an alert by itself* — untouched: nothing alerts on templates; imported and backfilled lines are
  counted like live ones, as analysis.
- *A hanging collector never blocks the agent* — not touched (backend only).
- Storage rules: one writer, `synchronous=FULL`, migrations as ordered steps, released steps never edited (step 8 is new) — kept.
  The metric write criterion (record 0083) is unaffected because a batch without log lines runs no template statement; writing log
  lines gets slower, which is measured (AC9).
- `docs/ARCHITECTURE.md` updated (components, *Storage and retention*, record links); no guarantee in `.squad/project.md` changes.

## Security considerations

- [x] **Limits and decoding.** The template limit of 1,024 bytes applies **after** normalization, counted on the UTF-8 encoding of
  the finished template, after lone surrogates became U+FFFD (three bytes each). The normalizer adds no input limit: messages are
  already bounded by the parsers (16 KiB lines and entries, `LogLineReader`, `ModelLimits.MaxTextBytes`), and the pass is linear
  (AC4). Query bounds: limit 1–10,000, usual-level range at most 744 hours, baseline at most 2,160 hours, so a usual-level query
  reads at most 2,160 hourly rows per candidate from two primary-key ranges.
- [x] **Exceptions that reach a user.** None today: no command or view calls the queries. `InvalidQueryException` texts are the
  fixed rule texts above and contain no input; `StoreException` carries SQLite's message as the existing queries do.
- Every value (source, program, template, times, numbers) is bound as a parameter, never concatenated into SQL.
- Memory: the tally of one batch holds at most one entry per stored line (≤ 20,000), each template at most 1,024 bytes, plus the
  hours it touches — less than the batch's own messages.
- Growth: hostile text without digits makes one template per line; storage grows by at most one template row, one template hour
  and nothing more per stored line (record 0095, *Consequences*).
- Output: templates hold no control character and no line feed but may hold `<`, `&` and any other text: security area 12 applies
  to whoever shows them (documented in the area and in `.squad/project.md`).
- The normalizer is a classification for grouping, not a guard: a token that looks like an address need not be one, and a local
  process can write lines that land in any template. Nothing trusts a template.

## Decision records

- `docs/decisions/0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md` (Proposed,
  indexed).

## Out of scope / follow-ups

- Follow-up issue (orchestrator, before the PR): **"[Storage] Rebuild the log template statistics from stored lines"** — body:
  after an upgrade from schema version 7, or a change of the normalization rules (record 0095), the template statistics cover
  only lines stored afterwards; a rebuild reads the stored log lines in batches, normalizes them and replaces the three template
  tables in bounded transactions; decide whether it runs as a sub-command or a migration step.
- Reference-host measurement: the Product Manager runs `dotnet Vandox.StorageBenchmark.dll <folder on the SSD volume>
  --log-lines 1000000` on the DS918+ (procedure in `docs/BENCHMARKS.md`) and the result row replaces "not measured yet". If it is
  not available before the PR, the orchestrator opens the issue **"[Docs] Measure log template throughput on the DS918+"** with
  that command and the criterion, linked under Next Steps.
- Showing templates and spikes in the web UI or a command; alerting; seasonal baselines; templates per host.
