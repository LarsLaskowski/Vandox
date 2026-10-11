# Plan: Import only the new part of a log file that grew since its import

Source: Issue #131
Status: Draft
Tier: security — the change reworks the log import's pass 1 and pass 2 (`Scanner`, `Importer`, `ImportPass` in
`src/Vandox.Import`), decides which bytes of untrusted input reach the parsers (*Security areas* 9 and 10 in
`.squad/project.md`), and adds a schema step and a lookup to the import state in `src/Vandox.Storage`.

## Problem / root cause

The import is idempotent per decompressed content (record 0069): pass 1 (`Scanner.RecognizeAsync`,
`src/Vandox.Import/Scanner.cs:411-453`) hashes the whole content through `HashingStream`, pass 2
(`ImportPass.BeginAsync`, `src/Vandox.Import/ImportPass.cs:304-347`) asks the store for the state of exactly that
hash (`ImportRepository.BeginAsync`, `src/Vandox.Storage/ImportRepository.cs:51-74`, lookup by `sha256` only) and
parses the content from byte 0 (`ImportPass.ImportContentAsync`, `ImportPass.cs:387-434`). Records of origin
`import` are never deduplicated (0063, 0069). A content that is a known content plus appended lines has a new hash,
gets a new `import_files` row and is parsed as a whole.

Claims of the issue, checked against the code:

- "`vandoxd import` skips a file whose content hash was imported before (decision 0069)" — **confirmed**:
  `ImportPass.cs:330-336` reports `AlreadyImported` when the state of the hash is complete.
- "A log file that grew since then ... has another hash and is imported as a whole, so the lines of the earlier
  import are stored twice" — **confirmed**: the hash covers the whole decompressed content (`Scanner.cs:432`,
  `HashingStream.Observe`), the store looks up only that hash, `RecordEmitter` drops only the records of the same
  content's earlier, interrupted run (`RecordEmitter.cs:45`, `_drop = state.Records`), and the area document states
  this limit (`docs/areas/log-import.md`, *Repeatable import*, "Limits of the guarantee").
- "Recognize that a file's content starts with a completely imported content (same length prefix and hash)" —
  feasible, but the issue's implied method (skip as many records as the earlier import stored) is **refuted** as a
  safe implementation: `MariaDbLineGrouper.Add` (`src/Vandox.Core/LogParsing/MariaDbLineGrouper.cs:59-69, 86-109`)
  holds the open MariaDB entry while it emits lines of other programs at once, and `KernelReportGrouper.Add`
  (`KernelReportGrouper.cs:79-129`) emits lines of another host while a report is open; at the end of input both
  flush the open entry last. So the first *n* records of the longer content are not the *n* records of the shorter
  one, and dropping by count would drop new lines and store an old entry again. The plan parses only the new part,
  on its own (record 0069, option G3).

Related defect found on the way: the area document (*Repeatable import*) and `README.md` (*Import logs*) promise
that lines appended to a log while it is imported "come with a later import", but that later import re-imported the
whole file, duplicating the part already stored. With this change that promise becomes true (the later import is a
continuation); no separate fix.

## Acceptance criteria

Test inputs in the import tests use `LineParser` (`tests/Vandox.Import.Tests`, `tests/Vandox.Backend.Tests`) unless a
criterion names a built-in parser. "C" is a content that ends with a line feed, "C+D" the same bytes followed by
more lines.

- [ ] AC1 (issue AC1): run 1 imports `a/syslog` = C; run 2 imports `b/syslog` = C+D. Run 2 stores exactly the
  records of the lines of D, in order; the records of both runs together are the lines of C+D, each once. Run 2's
  result: outcome `Continued`, `Records` = number of records of D, `ContinuedFrom` = `a/syslog` (the earlier
  content's stored display name), `ContinuedAfter` = number of lines of C. A third run of C+D reports
  `AlreadyImported` and stores nothing. Checked with `FakeImportStore` (`ImporterStoreTests`) and end to end
  against SQLite with the built-in syslog parser (`ImportCommandTests`).
- [ ] AC2: the continuation is per decompressed content: C as plain `syslog` in run 1, then C+D as `syslog.1.gz` in
  a directory, and C+D as an entry of a `.tar.gz` — each stores only D.
- [ ] AC3 (same run): with C at `2026-09/syslog` and C+D at `2026-10/syslog` in one input (C first in input order),
  each line is stored once; `2026-09/syslog` is `Imported`, `2026-10/syslog` is `Continued` from `2026-09/syslog`.
  With the order reversed (C+D first), both are `Imported` as a whole (the documented limit, pinned by the test).
  When the earlier file of the run fails in pass 2 (scripted write failure on its batch), the later one is not
  continued from it: it is `Imported` as a whole, or continued from a smaller complete base if one exists.
- [ ] AC4 (chain, longest base): run 1 C, run 2 C+D (continued after C), run 3 C+D+E is continued after C+D
  (not after C): only E is stored in run 3, `ContinuedFrom` names the C+D file.
- [ ] AC5 (cut line): C = `"LOG l1\nLOG l2\nLOG l3-part"` (no final line feed), C+D =
  `"LOG l1\nLOG l2\nLOG l3-partrest\nLOG l4\n"`. Run 2 starts at the beginning of the line in which C ends: it stores
  `LOG l3-partrest` and `LOG l4`, `ContinuedAfter` = 2. A base without any line feed (`"LOG only-part"`, then
  `"LOG only-part more\n"`) gives no continuation: outcome `Imported`, all lines stored.
- [ ] AC6 (no false continuation): the file is `Imported` as a whole when (a) its first |C| bytes differ from C in
  one byte after the first line (same head key, different prefix hash); (b) C was imported with another source type
  (two `LineParser`s of different type, each claiming its file); (c) C's import is incomplete (interrupted after one
  batch, `complete = 0`) and C+D is imported alone. When instead one run holds C (resumed) before C+D in input
  order, C is completed first and C+D is continued from it.
- [ ] AC7 (summary, issue AC2): `ImportSummaryWriter` writes the count row `  continued:         <n>` directly
  after `  imported:          <n>`, and, when a file was continued, a section `\nFiles continued:\n` before
  *Files not recognized* with one line per file, exactly
  `  <Terminal.Quote(path)>: continues <Terminal.Quote(ContinuedFrom)> after line <ContinuedAfter>\n`
  (e.g. `  "b/syslog": continues "a/syslog" after line 3`). A path or name with a control character is printed
  quoted and escaped like every other path. `ImportProgressLogger` logs the outcome of a continued file as
  `continued`. The exit code is 0 for a run whose files are imported, continued, already imported or not
  recognized.
- [ ] AC8 (counts and line numbers): `FileResult.Lines` of a continued file counts only the lines from the
  continuation point on (the summary's *Lines read* sums them); a line the parser skips in the new part is
  reported with its line number in the whole file (a bad line that is line 5 of C+D, with C of 3 lines, is reported
  as `line 5`, not `line 2`). The progress events of the file count lines from the continuation point.
- [ ] AC9 (pass 2 verifies everything): when the prefix part of C+D is changed between pass 1 and pass 2, the file
  fails with the existing reason `the content changed while it was imported`; when the file is shorter than the
  continuation point in pass 2, it fails the same way.
- [ ] AC10 (resume of a continuation): a continuation interrupted after its first batch (scripted write failure on
  the second batch) resumes in the next run with the continuation point stored in its state — also when a larger
  complete base has appeared in between — drops the records already stored and completes; every line of D is
  stored once; the result is `Continued`, `ResumedAfter` = the records of the first run.
- [ ] AC11 (store, begin): `SqliteStore.BeginImportAsync` with `Prefixes` creates the state with `ContinuesId`,
  `Offset` and `ContinuedFrom` of the **largest** prefix whose content is stored with the same size, the same source
  type and `complete = 1`; a prefix whose content is unknown, incomplete or of another source type is passed over;
  without such a prefix `ContinuesId` is null, `Offset` 0 and `ContinuedFrom` empty. An existing state is returned
  unchanged, with its stored continuation, whatever the prefixes are. `HeadSha256` is stored.
- [ ] AC12 (store, lookup): `SqliteStore.FindImportBasesAsync(head, type)` returns the `Sha256` and `Size` of the
  complete contents with that head key and source type, largest first, at most 64 (65 complete rows with one head
  give the 64 largest); incomplete rows, rows of another source type or head, and rows migrated from version 5
  (no head key) are never returned.
- [ ] AC13 (store, validation): `BeginImportAsync` and `FindImportBasesAsync` refuse with `InvalidImportException`
  and these exact messages, nothing written:
  `store: invalid import: head_sha256 must be 32 bytes` (head of 31 and 33 bytes),
  `store: invalid import: at most 64 prefixes` (65 prefixes),
  `store: invalid import: prefix sha256 must be 32 bytes`,
  `store: invalid import: prefix size must be positive and smaller than size` (0, equal to `Size`, larger),
  `store: invalid import: prefix offset must be positive and at most the prefix size` (0, prefix size + 1);
  `FindImportBasesAsync` with an invalid source type gives the existing
  `store: invalid import: source type must match ^[a-z][a-z0-9._-]{0,63}$`.
- [ ] AC14 (migration): the schema version is 6; a version-5 database is migrated (step 6 adds
  `import_files.head_sha256`, `continues_id`, `start_offset` and the index `import_files_head`), its rows keep
  their state (a complete content is still reported `AlreadyImported`), get no head key and are never found as a
  base; the existing migration tests from versions 2, 3 and 4 still reach version 6.
- [ ] AC15 (pass 1 store failure): when `FindImportBasesAsync` throws a `StoreException`, the run ends with an
  `ImportException` whose message starts with `importer: reading the import state failed`, the summary holds the
  files found so far, and `vandoxd import` exits 1; a cancellation during the lookup ends the run as interrupted.
- [ ] AC16 (bounds): pass 1 passes at most 64 bases to the hash snapshots of one file, and `BeginImportAsync` never
  gets more than 64 prefixes: 70 files of one run with the same first line and growing contents import without an
  `InvalidImportException` and without a failure.
- [ ] AC17 (head key, `ContentHead.Key`): the SHA-256 of the head up to and including its first line feed; of the
  whole head (at most 4,096 bytes) when it holds no line feed. Two heads with the same first line and different
  later bytes give equal keys; a first line of 5,000 bytes gives the hash of the first 4,096 bytes; a head of
  `"\n..."` gives the hash of the single byte `0x0A`.
- [ ] AC18 (`HashingStream` snapshots): with bases at given sizes, `Matches` lists exactly the bases whose SHA-256
  equals the hash of the first `Size` bytes read, each with `Offset` = position after the last line feed within
  those bytes; a base whose prefix has no line feed is not listed. Holds for a size inside one read, at a read's
  first and last byte, for two bases of equal size with different hashes, and for a size never reached. The total
  hash and `BytesRead` are unchanged by the snapshots.
- [ ] AC19 (`ContentStream.SkipAsync`): reads and hashes `count` bytes (the final hash equals the hash of the whole
  content), returns the number of line feeds in them, counts no line in `Lines` and reports no progress for them,
  never reads beyond the limit, and returns early at the end of input.
- [ ] AC20 (built-in parsers at the boundary, end to end): a MariaDB error log C+D whose D begins with two
  continuation lines of C's last entry is continued; the two lines are reported as skipped
  (`line before the first entry`, with their line numbers in the whole file) and the entries after them are stored.

## Approach

**Pass 1** (`Scanner.RecognizeAsync`): after detection, compute the head key `ContentHead.Key(head)` from the head
buffer pass 1 already holds (at most `ILogParser.SniffBytes` = 4,096 bytes of the decompressed content). Collect the
bases: `Store.FindImportBasesAsync(key, parser.Type)` (complete contents, at most 64) plus the files of this run
already hashed with the same key and source type (kept per key and type in a dictionary in the `Scanner`, at most
the 64 largest per key; dedupe by `Sha256`; keep the 64 largest of the union). Hash the content through
`HashingStream` with those bases: at each base size it appends exactly up to that size, takes
`IncrementalHash.GetCurrentHash()`, compares, and for a match records the position after the last line feed seen so
far (no match when it is 0). After the read, keep the matches with `Size` < the content's size in
`FoundFile.Prefixes`, store the key in `FoundFile.Head`, and add the file to the run's bases. A `StoreException`
from the lookup becomes `ImportException("importer: reading the import state failed", inner)`, which ends the scan
(`Importer.RunAsync` treats it like `TooManyFilesException`); an `OperationCanceledException` is passed on as today.

**Begin** (`ImportPass.BeginAsync` → `IImportTracker.BeginImportAsync`): `ImportFileStart` carries `HeadSha256` and
`Prefixes`. In `ImportRepository.BeginAsync`, inside the existing serializable transaction and writer lock: when no
row for `Sha256` exists, take the largest prefix whose row exists with the same `size`, the same `source_type` and
`complete = 1`, and insert the new row with `head_sha256`, `continues_id` = that row's id and `start_offset` = the
prefix's `Offset` (none: `NULL` and 0). The returned `ImportFile` carries `ContinuesId`, `Offset` and
`ContinuedFrom` (the base row's `name`, through a `LEFT JOIN` on `continues_id`). An existing row is returned
unchanged — so a resume uses the stored continuation point.

**Pass 2** (`ImportPass.ImportContentAsync`): when `state.Offset > 0`, call `counted.SkipAsync(state.Offset)` before
the parser starts (reads and hashes, no line counting, no progress), set `result.ContinuedFrom`,
`result.ContinuedAfter` (the line feeds skipped) and later the outcome `Continued` instead of `Imported`. The
emitter gets the skipped line count as `lineOffset` and adds it to every `Skip` line number above 0. The parser gets
`new LogFile(state.FileName, state.ModTime)` as today (γ of 0069) and the stream positioned at the continuation
point; `VerifyAsync` still checks the whole content against pass 1.

**Store lookup** (`SqliteStore.FindImportBasesAsync`): validate (head 32 bytes, `ParserTypes.IsValid`), then on a
reader connection: `SELECT sha256, size FROM import_files WHERE head_sha256 = $head AND source_type = $type AND
complete = 1 ORDER BY size DESC LIMIT 64`; `SqliteException` → `StoreException("store: finding import bases: ...")`.

**Schema step 6** (`SchemaMigrator`, `StorageLimits.SchemaVersion = 6`), statements in this order:

```sql
ALTER TABLE import_files ADD COLUMN head_sha256 BLOB CHECK (head_sha256 IS NULL OR length(head_sha256) = 32)
ALTER TABLE import_files ADD COLUMN continues_id INTEGER
ALTER TABLE import_files ADD COLUMN start_offset INTEGER NOT NULL DEFAULT 0 CHECK (start_offset >= 0)
CREATE INDEX import_files_head ON import_files(head_sha256, source_type, size) WHERE complete = 1 AND head_sha256 IS NOT NULL
```

No `REFERENCES` on `continues_id` (the tests drop the columns again to simulate older versions, which SQLite refuses
for a column with a foreign key; the id is written only from a row read in the same transaction). Checked once in
a scratch in-memory database (SQLite 3.45): the four statements run on a `STRICT` table with rows, and the columns
can be dropped again after `DROP INDEX import_files_head`.

**Summary and log**: `ImportOutcome.Continued`, the count row and the *Files continued* section (AC7),
`ImportProgressLogger` outcome name `continued`. No new log attribute.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `ImportBase.cs` (new) | a complete content that may start a longer one |
| Vandox.Storage | `ImportPrefix.cs` (new) | a base the content starts with, with its continuation point |
| Vandox.Storage | `ImportFileStart.cs` | `HeadSha256`, `Prefixes` |
| Vandox.Storage | `ImportFile.cs` | `ContinuesId`, `Offset`, `ContinuedFrom` |
| Vandox.Storage | `IImportTracker.cs` | `FindImportBasesAsync` |
| Vandox.Storage | `SqliteStore.cs` | `FindImportBasesAsync` (reader connection) |
| Vandox.Storage | `ImportRepository.cs` | validation of head and prefixes, base choice in `BeginAsync`, `FindBasesAsync`, select with the continuation |
| Vandox.Storage | `SchemaMigrator.cs`, `StorageLimits.cs` | step 6; `SchemaVersion = 6`, `MaxImportBases = 64` |
| Vandox.Import | `ContentHead.cs` (new) | head key |
| Vandox.Import | `HashingStream.cs` | base snapshots, `Matches` |
| Vandox.Import | `ContentStream.cs` | `SkipAsync` |
| Vandox.Import | `RecordEmitter.cs` | `lineOffset` for skip line numbers |
| Vandox.Import | `FoundFile.cs` | `Head`, `Prefixes` |
| Vandox.Import | `FileResult.cs` | `ContinuedFrom`, `ContinuedAfter`, copied by `Snapshot` |
| Vandox.Import | `ImportOutcome.cs` | `Continued` (appended after `Failed`) |
| Vandox.Import | `Scanner.cs` | lookup, run bases, matches into `FoundFile` |
| Vandox.Import | `ImportPass.cs` | start with head and prefixes, skip, outcome |
| Vandox.Import | `Importer.cs` | `ImportException` from the scan ends the run |
| Vandox.Backend | `Cli/ImportStoreAdapter.cs` | forwards `FindImportBasesAsync` |
| Vandox.Backend | `Cli/ImportSummaryWriter.cs` | count row and *Files continued* |
| Vandox.Backend | `Cli/ImportProgressLogger.cs` | `continued` |

Every existing file in this table was read for this plan; none is final as it stands.

## Signatures (for the Dev's skeleton)

`Vandox.Storage` (public):

```csharp
public sealed class ImportBase
{
    public byte[] Sha256 { get; set; } = new byte[32];   // SHA-256 of the complete content
    public long Size { get; set; }                        // its size in bytes
}

public sealed class ImportPrefix
{
    public byte[] Sha256 { get; set; } = new byte[32];   // SHA-256 of the base the content starts with
    public long Size { get; set; }                        // the base's size
    public long Offset { get; set; }                      // where the new part starts: after the last line feed of the base
}

// ImportFileStart, added:
public byte[] HeadSha256 { get; set; } = new byte[32];
public IReadOnlyList<ImportPrefix> Prefixes { get; set; } = [];

// ImportFile, added:
public long? ContinuesId { get; set; }
public long Offset { get; set; }
public string ContinuedFrom { get; set; } = string.Empty;

// IImportTracker, added (also SqliteStore, public, region IImportTracker):
Task<IReadOnlyList<ImportBase>> FindImportBasesAsync(byte[] headSha256, string sourceType, CancellationToken cancellationToken);

// StorageLimits:
public const int SchemaVersion = 6;
public const int MaxImportBases = 64;
```

`Vandox.Storage` (internal):

```csharp
// ImportRepository
internal static void Validate(ImportFileStart start);                       // extended (AC13)
internal static void ValidateLookup(byte[] headSha256, string sourceType);  // new
internal static Task<IReadOnlyList<ImportBase>> FindBasesAsync(SqliteConnection connection, byte[] headSha256, string sourceType, CancellationToken cancellationToken);
```

`Vandox.Import`:

```csharp
// ImportOutcome (public): Continued, appended after Failed — "The part of the file after a content imported completely before was stored in this run."
// FileResult (public), added:
public string ContinuedFrom { get; set; } = string.Empty;   // display name of the first import of the continued content; empty when not continued
public long ContinuedAfter { get; set; }                     // the lines of the content before the continuation point

// internal
internal static class ContentHead
{
    internal static byte[] Key(ReadOnlySpan<byte> head);
}

// HashingStream: constructor replaced
internal HashingStream(Stream inner, long step, Action<long> report, IReadOnlyList<ImportBase> bases);
internal List<ImportPrefix> Matches { get; }

// ContentStream, added:
internal Task<long> SkipAsync(long count, CancellationToken cancellationToken);

// RecordEmitter: constructor replaced
internal RecordEmitter(ImportOptions options, FoundFile item, ContentStream content, ImportFile state, long lineOffset);

// FoundFile, added:
internal byte[] Head { get; set; } = new byte[32];
internal List<ImportPrefix> Prefixes { get; set; } = [];
```

`Scanner.ScanAsync` keeps its signature and documents `<exception cref="ImportException">` for the failed lookup.
`Vandox.Backend`: `ImportStoreAdapter.FindImportBasesAsync` with the interface signature; no other signature change.

## Test files

- `tests/Vandox.Storage.Tests/ImportTrackingTests.cs` (existing file of the import state of `SqliteStore`): AC11,
  AC12, AC13.
- `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`: AC14.
- `tests/Vandox.Import.Tests/ContentHeadTests.cs` (new): AC17.
- `tests/Vandox.Import.Tests/HashingStreamTests.cs` (new): AC18.
- `tests/Vandox.Import.Tests/ContentStreamTests.cs` (new): AC19.
- `tests/Vandox.Import.Tests/ImporterStoreTests.cs`: AC1, AC4, AC5, AC6, AC8, AC9, AC10, AC15.
- `tests/Vandox.Import.Tests/ImporterArchiveTests.cs`: AC2.
- `tests/Vandox.Import.Tests/ImporterDirectoryTests.cs`: AC3, AC16.
- `tests/Vandox.Backend.Tests/ImportSummaryWriterTests.cs` (new): AC7 (summary text).
- `tests/Vandox.Backend.Tests/ImportProgressLoggerTests.cs` (new): AC7 (outcome name).
- `tests/Vandox.Backend.Tests/ImportCommandTests.cs`: AC1 end to end against SQLite with the built-in syslog parser,
  AC7 exit code, AC20.
- Test double: `tests/Vandox.Import.Tests/FakeImportStore.cs` — a lookup over its in-memory states (head key,
  source type, complete, largest first, at most 64), the base choice of the real store in `BeginImportAsync`,
  and a scripted `FindFailure`.

Existing test code affected by a changed signature:

- `FakeImportStore` implements `IImportTracker` and stops compiling when `FindImportBasesAsync` is added: the
  **Dev** adds the member mechanically in step 4 (returns an empty list); the **Tester** gives it its behavior in
  step 5.
- `HashingStream`, `ContentStream` and `RecordEmitter` have no test call sites.
- Not a signature change but a behavior the migration tests depend on: `SqliteStoreOpenMigratesVersionThreeAndAddsHost`
  and `SqliteStoreOpenMigratesVersionFourAndAddsEvent` set the version back on a database created at the latest
  version; with step 6 they must also undo step 6 in their arrange SQL (`DROP INDEX import_files_head` and
  `ALTER TABLE import_files DROP COLUMN` for `start_offset`, `continues_id`, `head_sha256`), and the latter compares
  the literal `"5"`, which becomes `StorageLimits.SchemaVersion`. The **Tester** adapts both in step 5.

## Areas

- `docs/areas/log-import.md` (owner: Dev):
  - *Repeatable import*: replace the "grew" limit with the continuation rules — head key (first line, or the first
    4,096 bytes without a line feed); bases are complete contents of the same source type from earlier runs or
    earlier files of the run in input order; at most 64 bases per file; the longest base whose bytes the content
    starts with wins; the new part starts at the beginning of the line in which the base ends, and a base without a
    line feed is never continued; the new part is parsed on its own with the file name and modification time of the
    content's first import; the whole content is still hashed in both passes; the continuation point is stored, so a
    resume uses it. Limits: the older-after-newer order is not recognized (imported as a whole); contents imported
    before schema version 6 are never a base; at the continuation point a grouped entry is split (MariaDB error log:
    continuation lines after the point are skipped as "line before the first entry"; journal and syslog: MariaDB
    entries and kernel reports become the stored entry plus lines of their own) and a line cut by the earlier copy is
    stored again, complete. Fix the sentence on appended lines ("come with a later import" — now as a continuation).
  - *Result*: outcome "continued"; the summary's count row and *Files continued* list (path, earlier name, line);
    *Lines read* counts the new part of a continued file, skipped-line numbers count in the whole file; exit code 0
    also for continued files; progress outcome `continued`.
  - *Related decisions*: the 0069 line becomes "why content hashes, two passes, resume by count and the continuation
    of a grown file".
- `docs/areas/storage.md` (owner: Dev): *Schema* — current version 6, step 6 and its columns; the `import_files`
  row of the table gains head key, continued content and continuation point; the index `import_files_head` in
  *Indexes*; where the import state is described, the lookup of complete contents by head key and source type
  (largest first, at most 64).

## Documentation updates

- `README.md`, *Import logs* (owner: Dev): the summary lists continued files; exit code 0 includes continued files;
  the "Limits" sentence becomes: a file that grew since its import is continued after the part imported before (only
  when the older copy was imported first), and lines appended while a file is imported come with a later import as
  a continuation.
- `docs/ARCHITECTURE.md` (owner: Lead, done in this plan step): *Storage* — schema version 6 and the continuation in
  the *Imports* bullet; it links 0069 already.
- `.squad/project.md`, *Test doubles*, row `database (C#)` (owner: Tester): `FakeImportStore` gains "base lookup"
  and "scripted begin, find and write failures".
- `docs/decisions/0069-...md` and its index row (owner: Lead, done in this plan step).

## Architecture check

The guarantees in `.squad/project.md` are untouched: gap recording and the collectors concern the agent; imported
records stay origin `import` and never alert (0022). The import's own guarantee — a content imported completely is
never stored again — is extended, not weakened: a byte range is passed over only when its SHA-256 in pass 1 equals a
content that the store marks complete with the same source type, and pass 2 re-hashes the whole content, so a change
between the passes still fails the file. The flow change (lookup in pass 1, continuation in pass 2, schema 6) is
described in `docs/ARCHITECTURE.md` (*Storage*), linking 0069.

## Security considerations

- [ ] Limits on parsed input, all on decompressed bytes before any decoding: the head key covers at most 4,096 bytes
  (`ILogParser.SniffBytes`); a file's snapshots are bounded by 64 bases (store `LIMIT 64` and the cap of the merged
  list); the run keeps at most 64 bases per head key and source type and at most one entry per recognized file
  (bounded by `ImportLimits.MaxFiles` = 20,000); `BeginImportAsync` refuses more than 64 prefixes. Memory per file
  does not depend on the input size; the run's base table is bounded by the entry limit.
- [ ] Skipped bytes are never unverified input: the continuation point lies inside a prefix whose SHA-256 equals a
  complete content (pass 1), and pass 2 reads and hashes the skipped bytes too (`VerifyAsync` over the whole
  content). A crafted file can at most make the importer skip bytes that are byte-identical to stored content.
- [ ] No new file access: every open stays on `SecureRoot` as today; nothing is written to disk.
- [ ] SQL takes the head key, source type and prefixes only as parameters; `continues_id` is taken from a row read in
  the same transaction.
- [ ] User-visible texts: the summary prints `ContinuedFrom` (the earlier content's stored display name, derived from
  input, at most 1,024 bytes) and the path only through `Terminal.Quote`; the progress log adds no attribute. Errors:
  a pass-1 store failure ends the run with `importer: reading the import state failed` (logged with the inner
  `StoreException` message through the JSON logger, which escapes control characters; never in the summary);
  `InvalidImportException` texts are those of AC13 (fixed texts, no input); pass-2 reasons are unchanged
  (`database error`, `interrupted`, the conflict text, `the content changed while it was imported`).

## Decision records

- `docs/decisions/0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md` — unreleased (no `v*`
  tag exists), extended in place on its own topic and set back to `Proposed` (index row too, title extended):
  options G1–G3 for the grown file, P1–P3 for finding the base, the decision (G3, P2, bases from earlier runs and
  earlier files of the run), and the consequences (limits: order, pre-version-6 contents, split entries; the
  follow-up). `decision-check.py` passes.

## Out of scope / follow-ups

- Follow-up issue for the orchestrator to open after the PR (record 0069 names it):
  - Title: `[Logs] Recognize an older copy of a log whose content is the start of an imported content`
  - Body: "`vandoxd import` continues a file that grew since its import (#131) only when the older copy was imported
    first. Imported the other way round — the newer copy of `/var/log` first, the older one later, or later in the
    same input — the older content has its own hash and is imported as a whole, so its lines are stored twice.
    Recognizing it needs a way to tell that a content is the start of a stored content, for example prefix hashes
    kept with every content (record 0069, options P1–P3). Acceptance criteria: importing an older copy after a newer
    one stores no line twice; the summary says which files were recognized this way."
- Contents imported before schema version 6 get no head key (their content is not available to the migration).
- Keeping a grouped entry whole across the continuation point (would need parser state from the earlier import).
