# Plan: Recognize an older copy of a log whose content is the start of an imported content

Source: Issue #187
Status: Draft
Tier: security — the change decides which input lines `vandoxd import` stores and which it leaves out as already
stored (`Scanner`/`ImportPass`/`ImportRepository`, *Security areas* 9 and 10 in `.squad/project.md`). A wrong match
would quietly drop lines from the forensic record, and the change also adds a schema step and a new write path.

## Problem / root cause

The import recognizes only one order. A content that **starts with** a complete stored content is continued (#131).
A content that **is the start of** a complete stored content is not:

- `ImportRepository.BeginAsync` (`src/Vandox.Storage/ImportRepository.cs:113-141`) knows only two cases: the exact
  SHA-256 already exists (`SelectExisting`, line 23, which ends as "already imported"), or a stored complete content
  is a prefix of the new one (`ChooseBaseAsync`, lines 207-243). Both rely on prefixes that pass 1 found.
- Pass 1 compares a content only with **smaller** bases. `HashingStream.Observe`/`CompareBases`
  (`src/Vandox.Import/HashingStream.cs:122-169`) snapshots the hash only at a base's size, and only once the read
  reaches that size. `Scanner.RecognizeAsync` (`src/Vandox.Import/Scanner.cs:583`) keeps only matches with
  `Size < size`. A stored content that is longer than the file is never reached.
- So an older copy O, imported after the newer copy N that starts with O's bytes, has its own hash and no base. It is
  imported as a whole, and every line of O is stored a second time.

Claims of the issue, checked against the code:

- "continues a file that grew since its import (#131) only when the older copy was imported first": **confirmed**,
  see above. Record 0069 (*Consequences*) and `docs/areas/log-import.md` line 111 state the same limit.
- "Imported the other way round — the newer copy first, the older one later, or later in the same input — the older
  content … is imported as a whole, so its lines are stored twice": **confirmed** for both orders. Within one run,
  pass 2 works in input order (`ImportPass.RunAsync`, lines 48-72). If N comes earlier in the input, it is complete
  before O begins, but `BeginAsync` still never looks at larger contents.
- "for example prefix hashes kept with every content (record 0069, options P1–P3)": **partly refuted**. P1–P3 in
  0069 solve the other direction: finding a *smaller* stored content while the larger one is hashed. P3 (fixed
  intervals) would miss almost every older copy. 0069's context says it already: the reverse order "would need prefix
  hashes of every stored content at arbitrary sizes". An older copy can end at any line end. So the hashes have to be
  kept at **every line end**, and that is the approach below.

Related defect found on the way (not fixed here, follow-up below). An interruption in pass 2 outside the parser
leaves `Importer.RunAsync` as an exception, and then no summary is printed. Two places where it happens:
`ReadArchiveAsync` calls `GetNextEntryAsync(false, cancellationToken)` (`ImportPass.cs:277`), and the verification
read `RecordEmitter.VerifyAsync` runs inside `SettleAsync`, which catches only I/O exceptions (`ImportPass.cs:151`).
Neither is caught by `ImportPass` or `Importer` (`Importer.cs:69`). `ImportCommand` catches the
`OperationCanceledException` (`ImportCommand.cs:75-80`), logs "interrupted" and returns 1, but prints no summary.
`docs/areas/log-import.md` (*Result*, exit code) says "the summary is still printed when there is one". Found by
reading; not reproduced.

## Acceptance criteria

Terms: *container* is the complete stored content that the older copy is the start of; *line end* is the offset just
after a `\n` (1 to the content size).

- [ ] AC1 (issue AC 1, later run): N is imported completely, and in a later run O = the first bytes of N up to one of
  N's line ends, with the same source type. O gets outcome `Contained`, stores 0 records, writes no batch and creates
  no `import_files` row. Its `FileResult.ContainedIn` is N's stored display name. The store holds each line of N
  exactly once (real store, `vandoxd import` end to end; also with the older copy gzip-compressed).
- [ ] AC2 (issue AC 1, same run): one input holds N earlier and O later in input order (also as two entries of one
  tar archive). N is imported, O is `Contained`, and each line is stored once.
- [ ] AC3: an older copy is recognized regardless of the parser's `LineFramed`. A journal export (`LineFramed` false)
  whose content is the start of an imported export is `Contained`.
- [ ] AC4: nothing is recognized, and O is imported as a whole as before, when:
  (a) O ends inside a line (no `\n` at its end);
  (b) O has the same first line but differs in one byte before its end;
  (c) the container has another source type;
  (d) the container is not complete (interrupted, or failed in pass 2 of the same run);
  (e) the container was completed without line hashes (`hashes_attempt` `NULL`, for example a row from schema 6);
  (f) the only rows that would match belong to an attempt other than the container's `hashes_attempt`.
- [ ] AC5: O with the same SHA-256 as a stored content keeps today's behavior: "already imported" when that content
  is complete, resumed when it is not. The containment lookup runs only for a content without a stored state.
  Containment wins over a forward base: if O both continues a smaller base and is the start of a container, it is
  `Contained`.
- [ ] AC6: importing a contained O again in a later run gives `Contained` again, with no row and no records.
- [ ] AC7 (store, line hashes written): every content imported in pass 2 (outcomes `Imported` and `Continued`, any
  parser) stores, for every line end *e*, the first 16 bytes of the SHA-256 of bytes `[0, e)` of the decompressed
  content. Each row holds at most 4,096 hashes in ascending order. The first row starts at offset 0, each further row
  starts at the previous row's `LastOffset`, and `LastOffset` is the row's last line end. The part before a
  continuation point is included. The rows do not depend on how the input is cut into reads (`ChunkedStream`, chunk
  sizes 1, 7 and 4,096). A content without any line feed writes no row.
- [ ] AC8 (attempts): `BeginImportAsync` sets `attempt` to 1 for a new row. It adds 1 to an existing incomplete row
  and returns the new value, and it returns a complete row unchanged. The completing batch sets `hashes_attempt` to
  its step's `Attempt` and, in the same transaction, deletes the line-hash rows of the file with any other attempt.
  A resumed import (interrupted, then completed) therefore keeps only the rows of the completing attempt, and a
  later O is still recognized.
- [ ] AC9 (`AddLineHashesAsync` rules): each broken rule is refused with `InvalidImportException` and the exact text
  `store: invalid import: <rule>`. The rules are `file id must be positive`, `attempt must be positive`,
  `first offset must not be negative`, `last offset must be greater than the first offset`,
  `line hashes must be 1 to 4096 hashes of 16 bytes` (length 0, not a multiple of 16, or above 65,536) and
  `line hashes must not outnumber the bytes they cover`. A file that is complete or unknown gives
  `ImportConflictException` with `store: import state changed: file <id> is complete or unknown`. A second row with
  the same (file, attempt, last offset) is a `StoreException`.
- [ ] AC10 (batch step): a batch whose `ImportStep.Attempt` is negative is refused with
  `store: invalid batch: import attempt must not be negative`. `Attempt` 0 is allowed and means "no line hashes".
- [ ] AC11 (lookup bound): only the 64 largest complete contents with the same head key and source type, larger than
  O, are compared (`MaxImportContainers`), largest first. A container that ranks 65th by size is not found. Every
  candidate reads exactly one row (the row with `first_offset < size <= last_offset`).
- [ ] AC12 (`ContentStream`): with `LineHashWriter` set, every row that is complete is written before the next read
  of the inner stream starts. `FlushLineHashesAsync` writes the rest, in order. After every `ReadAsync`,
  `PendingLineHashes` is at most 4,095 plus the number of bytes that read returned. Without a writer, no hash is
  computed and nothing is written, and today's behavior and tests stay unchanged. With a writer set, the synchronous
  `Read` throws `NotSupportedException`.
- [ ] AC13 (failure of a line-hash write): the write fails during the skip, the parse or the verification read.
  A `ImportConflictException` fails the file with "the import state changed (another import of the same content is
  running?)". Any other store error fails it with "database error" and ends the run (`Importer` returns an
  `ImportException`). A cancellation gives "interrupted". The completing batch is never written after such a failure.
  `ImportPass` never lets the exception escape.
- [ ] AC14 (order before completion): in pass 2, the last partial row of line hashes is written after the
  verification read succeeded and before the completing batch. If the content changed between the passes, no
  completing batch is written and the file fails as today with "the content changed while it was imported".
- [ ] AC15 (issue AC 2, summary): the count block has the line `  contained:         <n>` directly after
  `  already imported:  <n>`. When at least one file is contained, a section follows *Files continued*:
  `\nFiles contained in an imported file:\n`, and one line per file in input order:
  `  <Terminal.Quote(path)>: start of <Terminal.Quote(ContainedIn)>\n`. A path or name with control characters
  appears escaped. Without contained files the section is absent and the count reads 0.
- [ ] AC16: the progress log line of a contained file has outcome `contained` (`ImportProgressLogger`).
  `FileResult.Snapshot` copies `ContainedIn`.
- [ ] AC17: the exit code is 0 when every file was imported, continued, contained, already imported or not recognized.
- [ ] AC18 (migration): a database at schema version 6 opens and migrates to 7. Existing rows get `attempt` 0 and
  `hashes_attempt` `NULL`, the table `import_line_hashes` exists, `meta.schema_version` is `7`, and a build with
  version 7 refuses version 8.
- [ ] AC19 (contained result): for a contained O, `BeginImportAsync` returns an `ImportFile` whose `ContainedIn` is
  the container's display name and whose other members keep their defaults (`Id` 0, `Complete` false, `Attempt` 0);
  containment is signalled by `ContainedIn` alone. `ImportPass` checks `ContainedIn` before `Complete`, before the
  source-type check and before any use of `state.Id`. Test (against `FakeImportStore` scripted to return exactly that
  object): O ends as `Contained`, not `AlreadyImported` and not failed, and the fake records no batch, no
  `AddLineHashesAsync` call and no other call carrying a file ID. An implementation that checked `Complete` first
  would import O with file ID 0 and fail this test. A state with `Complete` true and empty `ContainedIn` stays
  `AlreadyImported`.

## Approach

1. **Line hashes in pass 2.** `ContentStream` already hashes every byte that pass 2 reads, the skipped part
   included. With `LineHashWriter` set, `Observe` splits each read at its line feeds. After the byte `\n` at offset
   `e - 1` it appends `IncrementalHash.GetCurrentHash(Span<byte>)[..16]` to the current row. A row that reaches 4,096
   hashes is queued. The async `ReadAsync` first awaits the writer for every queued row, then reads. Memory therefore
   stays at one row plus one read. `FlushLineHashesAsync` writes the queue and the partial row. `ImportPass` calls it
   after `VerifyAsync` succeeded and before the completing batch.
2. **Attempts.** `import_files.attempt` counts the `BeginImportAsync` calls that return an incomplete state. Every
   line-hash row carries the attempt that wrote it. The completing batch sets `hashes_attempt` and deletes the rows of
   other attempts, so the rows of an attempt that read changed bytes or lost a race are never used. A line-hash write
   runs in its own transaction, guarded by `complete = 0`, so nothing can be added after completion.
3. **Containment check in `BeginImportAsync`.** The check runs in the same transaction, after "existing row" and
   before the forward base choice. Candidates are the complete rows with the same head key and source type and a
   size larger than O, joined to the one line-hash row of their `hashes_attempt` that spans O's size. They are taken
   largest first, at most 64. The first candidate whose row holds `sha256(O)[..16]` at a 16-byte aligned position is
   the container. Then no row is created. The store returns an `ImportFile` with only `ContainedIn` set, to the
   container's display name; `Id`, `Complete` and `Attempt` keep their defaults and carry no meaning. Containment is
   signalled by `ContainedIn` alone, never by an `Id` of 0 or by `Complete`. `ImportPass.BeginAsync` checks
   `ContainedIn` first, before `state.Complete` (`ImportPass.cs:355`), before the source-type check and before
   `state.Id` is used anywhere, and turns it into `Contained` (AC19).
4. **Nothing is parsed for a contained file**, whatever its parser. Every line of O lies in N's bytes, and N's
   records (or those of its base chain) hold them. That is why `LineFramed` (F1) does not apply.
5. **Pass 1 does not change.** Containment needs only the hash, size and head key that pass 1 already computes.
6. **Summary and log** get the new outcome (AC15–AC17). README, the area documents and record 0069 describe the new
   rules and limits.

Rejected alternatives and the reasons are in record 0069 (*Options considered*, H1–H4, W1–W2, S1–S2).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `ImportLineHashes.cs` (new) | one row of line hashes |
| Vandox.Storage | `IImportTracker.cs` | new `AddLineHashesAsync`; doc of `BeginImportAsync` (attempt, containment) |
| Vandox.Storage | `ImportFile.cs` | `Attempt`, `ContainedIn` |
| Vandox.Storage | `ImportStep.cs` | `Attempt` |
| Vandox.Storage | `StorageLimits.cs` | `SchemaVersion` 7, `MaxLineHashes`, `LineHashBytes`, `MaxImportContainers` |
| Vandox.Storage | `SchemaMigrator.cs` | step 7 |
| Vandox.Storage | `ImportRepository.cs` | `ValidateLineHashes`, `AddLineHashesAsync`, `FindContainerAsync`, `HoldsLineHash`; `BeginAsync` (attempt, containment), `AdvanceAsync` (`hashes_attempt`, deletion), `SelectAsync` (reads `attempt`) |
| Vandox.Storage | `BatchValidator.cs` | rule `import attempt must not be negative` |
| Vandox.Storage | `SqliteStore.cs` | `AddLineHashesAsync` (write lock, `StoreException` wrapping as `BeginImportAsync`) |
| Vandox.Import | `ImportOutcome.cs` | `Contained` (appended last) |
| Vandox.Import | `FileResult.cs` | `ContainedIn`; `Snapshot` copies it |
| Vandox.Import | `ContentStream.cs` | line hashes, `LineHashWriter`, `PendingLineHashes`, `FlushLineHashesAsync`, async `ReadAsync`, `Read` guard |
| Vandox.Import | `RecordEmitter.cs` | constructor without `lineOffset`, settable `LineOffset`, `Attempt` in every step, `WriteLineHashesAsync` |
| Vandox.Import | `ImportPass.cs` | `BeginAsync` (Contained), `ImportContentAsync` (emitter before skip, writer wired), `SettleAsync`/`JudgeAsync` (flush before completion, failures through `emitter.Error`) |
| Vandox.Backend | `Cli/ImportStoreAdapter.cs` | forwards `AddLineHashesAsync` |
| Vandox.Backend | `Cli/ImportSummaryWriter.cs` | count line, section *Files contained in an imported file* |
| Vandox.Backend | `Cli/ImportProgressLogger.cs` | `Contained => "contained"` |

Files the skeleton rewrites: all rows above. None of them is final as it stands; each needs the members listed below.
`Scanner.cs`, `HashingStream.cs`, `ContentHead.cs` and `ImportFileStart.cs` stay unchanged.

## Signatures (for the Dev's skeleton)

`src/Vandox.Storage/ImportLineHashes.cs` (new):

```csharp
/// One stored row of line hashes of an imported content.
public sealed class ImportLineHashes
{
    /// Gets or sets the ID of the import file.
    public long FileId { get; set; }

    /// Gets or sets the attempt that computed the hashes.
    public long Attempt { get; set; }

    /// Gets or sets the offset after the line end before the row's first one; 0 for the first row.
    public long FirstOffset { get; set; }

    /// Gets or sets the offset of the row's last line end.
    public long LastOffset { get; set; }

    /// Gets or sets the hashes, LineHashBytes bytes per line end, in ascending order of their line ends.
    public byte[] Hashes { get; set; } = [];
}
```

`IImportTracker` (new member):

```csharp
Task AddLineHashesAsync(ImportLineHashes hashes, CancellationToken cancellationToken);
// <exception cref="InvalidImportException">The row breaks a rule</exception>
// <exception cref="ImportConflictException">The file is complete or unknown</exception>
```

The doc of `BeginImportAsync` changes to: creates the state with attempt 1 when it is unknown, unless the content is
the start of a complete stored content, in which case no state is created and the returned `ImportFile` has
`ContainedIn` set (not empty) and no other member set; a caller checks `ContainedIn` before any other member. An
existing incomplete state is returned with its attempt advanced by one, and a complete state is returned unchanged;
both have an empty `ContainedIn`.

`ImportFile` (new properties):

```csharp
public long Attempt { get; set; }
/// Gets or sets the display name of the complete content this content is the start of; empty unless the content is
/// contained, in which case no state exists and no other member is set.
public string ContainedIn { get; set; } = string.Empty;
```

`ImportStep` (new property):

```csharp
public long Attempt { get; set; }
```

`StorageLimits`:

```csharp
public const int SchemaVersion = 7;         // was 6
public const int MaxLineHashes = 4096;
public const int LineHashBytes = 16;
public const int MaxImportContainers = 64;
```

`SchemaMigrator` step 7 (statements exactly):

```sql
ALTER TABLE import_files ADD COLUMN attempt INTEGER NOT NULL DEFAULT 0 CHECK (attempt >= 0)
ALTER TABLE import_files ADD COLUMN hashes_attempt INTEGER
CREATE TABLE import_line_hashes (
  file_id INTEGER NOT NULL,
  attempt INTEGER NOT NULL CHECK (attempt > 0),
  first_offset INTEGER NOT NULL CHECK (first_offset >= 0),
  last_offset INTEGER NOT NULL CHECK (last_offset > first_offset),
  hashes BLOB NOT NULL CHECK (length(hashes) BETWEEN 16 AND 65536 AND length(hashes) % 16 = 0),
  PRIMARY KEY (file_id, attempt, last_offset)
) STRICT
```

`ImportRepository`:

```csharp
internal static void ValidateLineHashes(ImportLineHashes hashes);
internal static async Task AddLineHashesAsync(SqliteConnection connection, ImportLineHashes hashes, CancellationToken cancellationToken);
internal static bool HoldsLineHash(ReadOnlySpan<byte> hashes, ReadOnlySpan<byte> sha256);   // compares sha256[..16] at offsets 0, 16, 32, …; a trailing partial entry is ignored
private static async Task<(long Id, string Name)?> FindContainerAsync(SqliteConnection connection, SqliteTransaction transaction, ImportFileStart start, CancellationToken cancellationToken);
// changed bodies: BeginAsync, AdvanceAsync, SelectAsync
```

`AddLineHashesAsync` (`ImportRepository`): one transaction that runs
`INSERT INTO import_line_hashes(file_id, attempt, first_offset, last_offset, hashes) SELECT $id, $attempt, $first, $last, $hashes WHERE EXISTS (SELECT 1 FROM import_files WHERE id = $id AND complete = 0)`.
If no row was inserted, it throws `ImportConflictException("store: import state changed: file <id> is complete or unknown")`.

`FindContainerAsync` query:

```sql
SELECT f.id, f.name, h.hashes FROM import_files f
JOIN import_line_hashes h ON h.file_id = f.id AND h.attempt = f.hashes_attempt
WHERE f.head_sha256 = $head AND f.source_type = $type AND f.complete = 1 AND f.size > $size
  AND h.first_offset < $size AND h.last_offset >= $size
ORDER BY f.size DESC, f.id LIMIT $limit
```

`AdvanceAsync`: the CAS update also sets `hashes_attempt = $hashes`, where `$hashes` is `step.Attempt` when
`step.Complete` and `NULL` otherwise. When `step.Complete`, it then runs
`DELETE FROM import_line_hashes WHERE file_id = $id AND attempt <> $attempt` in the same transaction.

`SqliteStore`:

```csharp
public async Task AddLineHashesAsync(ImportLineHashes hashes, CancellationToken cancellationToken);   // StoreException "store: adding line hashes: <sqlite message>"
```

`ImportOutcome`: `Contained` appended after `Continued`, with the doc "The content of the file is the start of a
content imported completely before; nothing was stored."

`FileResult`:

```csharp
/// Gets or sets the display name of the first import of the content this file is the start of; empty when it is not contained.
public string ContainedIn { get; set; } = string.Empty;
```

`ContentStream`:

```csharp
internal Func<ImportLineHashes, CancellationToken, Task>? LineHashWriter { get; set; }   // rows are passed with FileId and Attempt 0
internal int PendingLineHashes { get; }                                                   // hashes computed and not yet passed to the writer
internal async Task FlushLineHashesAsync(CancellationToken cancellationToken);
public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);   // writes queued rows first
public override int Read(Span<byte> buffer);   // NotSupportedException("the content is read asynchronously while line hashes are kept") when LineHashWriter is set
```

`RecordEmitter`:

```csharp
internal RecordEmitter(ImportOptions options, FoundFile item, ContentStream content, ImportFile state);   // lineOffset parameter removed
internal long LineOffset { get; set; }
internal async Task WriteLineHashesAsync(ImportLineHashes hashes, CancellationToken cancellationToken);   // sets FileId and Attempt from the state; a failure is kept in Error (sticky, as for batches) and rethrown; throws Error when it is already set
```

`ImportPass` (private, bodies only): `BeginAsync`, right after the store call and before the `state.Complete` check,
sets `Outcome = Contained` and `Result.ContainedIn` and returns `null` when `state.ContainedIn` is not empty; the
`Complete` check (`AlreadyImported`) and everything after it stay as they are. `ImportContentAsync` creates
the emitter before `SkipAsync`, sets `counted.LineHashWriter = emitter.WriteLineHashesAsync` and, after the skip,
`emitter.LineOffset = skipped`. `SettleAsync(RecordEmitter emitter, ContentStream counted, Exception? parseError, CancellationToken cancellationToken)`
calls `counted.FlushLineHashesAsync` after `VerifyAsync` returned true and before `TryFlushAsync(emitter, true, …)`,
and returns `emitter.Error` from `catch (Exception) when (emitter.Error is not null)`. `JudgeAsync` passes `counted`
through.

`ImportStoreAdapter`:

```csharp
public Task AddLineHashesAsync(ImportLineHashes hashes, CancellationToken cancellationToken);
```

`ImportSummaryWriter`:

```csharp
private static void WriteContained(StringBuilder text, List<FileResult> files);   // called right after WriteContinued
```

## Test files

- `tests/Vandox.Storage.Tests/ImportTrackingTests.cs`: AC4 (c–f) on the store, AC5, AC8, AC9, AC11, the store side
  of AC19 (a contained result has `ContainedIn` set and no row exists afterwards; an existing state has an empty
  `ContainedIn`), and containment
  with the real store (exact prefix at a line end, not at a line end, one byte off, row spanning the size at a row
  boundary: size equal to a row's `LastOffset` and one line end after it).
- `tests/Vandox.Storage.Tests/SqliteStoreWriteTests.cs`: AC10. `BatchValidator` is internal and has no test class
  of its own; its import-step rules are tested through `SqliteStore.WriteBatchAsync` here (existing case at line 372).
- `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`: AC18.
- `tests/Vandox.Import.Tests/ContentStreamTests.cs`: AC7 (hash values, rows, offsets, independence of read size, skip
  included, no row without a line feed) and AC12.
- `tests/Vandox.Import.Tests/ImporterStoreTests.cs`: AC1, AC2, AC3, AC4 (a, d), AC5, AC6, AC13, AC14 and AC19 against
  `FakeImportStore`. `ImportPass` and `RecordEmitter` are covered through `Importer` here, as for #131.
- `tests/Vandox.Import.Tests/ImporterArchiveTests.cs`: AC2 with both copies as entries of one tar archive.
- `tests/Vandox.Import.Tests/FileResultTests.cs`: AC16 (`Snapshot` copies `ContainedIn`).
- `tests/Vandox.Backend.Tests/ImportSummaryWriterTests.cs`: AC15.
- `tests/Vandox.Backend.Tests/ImportProgressLoggerTests.cs`: AC16 (outcome `contained`).
- `tests/Vandox.Backend.Tests/ImportCommandTests.cs`: AC1 end to end with the real store (later run, also gzip; the
  store's record count proves no line twice), AC2 in one run, AC3 (journal export), AC17.

Test doubles: `FakeImportStore` (`tests/Vandox.Import.Tests/FakeImportStore.cs`) needs the following, extended by
the Tester in step 5: `AddLineHashesAsync` that records every row (`LineHashes`) with a scripted failure (exception
and call number); `BeginImportAsync` counting attempts as the store does; a containment emulation that applies the
store's rule to the recorded rows of complete states and returns an `ImportFile` with only `ContainedIn` set, as the
store does (AC19); and batches that keep `Attempt` and apply the deletion on
completion.

Existing test code affected by the changes:

- `FakeImportStore` implements `IImportStore` and stops compiling when `IImportTracker` gets `AddLineHashesAsync`.
  The **Dev** adds the member mechanically in step 4 (`return Task.CompletedTask;`, no other change). The
  **Tester** then extends it in step 5 as described above.
- `RecordEmitter`'s constructor: its only call site is `ImportPass` (production, Dev). No test calls it.
- `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`: the migration tests at lines 160, 188, 222 and 267 undo
  later steps by SQL and set an older version. With step 7 they fail at `ALTER TABLE … ADD COLUMN attempt` (duplicate
  column) or `CREATE TABLE import_line_hashes` (exists). Line 275 expects `"6"`. The **Tester** adapts them in step 5:
  add an `UndoStepSeven` (drop table `import_line_hashes`, drop columns `hashes_attempt` and `attempt`) that runs
  before `UndoStepSix`, and keep each test's intent. This is not a signature change, so it is test code for the
  Tester. No assertion is weakened.
- `tests/Vandox.Backend.Tests/ImportSummaryWriterTests.cs:45` and `ImportCommandTests.cs:457/577` assert neighbouring
  count lines. The new line goes *after* `already imported`, so they stay valid unchanged.

## Areas

- `docs/areas/log-import.md` (owner **Dev**):
  - *Repeatable import*: a new rule list "**An older copy is recognized.**" covering the line hashes (16 bytes of
    SHA-256 per line end, rows of 4,096, written in pass 2 for every imported content, any parser), attempts, the
    containment conditions (complete container, same source type and head key, ends at a line end, at most 64
    largest candidates, checked only for a content without a state, before the forward continuation), and that
    nothing is parsed or stored and no state is kept.
  - *Limits of the guarantee*: replace "an older copy imported after a newer one … is not recognized" with the new
    limits. Not recognized are an older copy that ends inside a line, a container imported before schema version 7,
    and a container outside the 64 largest. The older copy's lines are present as the container's parse stored them
    (its file name and modification time, grouped entries). Lines are left out on a 128-bit match of truncated
    SHA-256.
  - The same limits text says explicitly that the grouping is the container's: the older copy is cut at a line end,
    which for a parser that is not framed by lines only (`LineFramed` false, the journal export) or that groups
    lines into entries (MariaDB error log, kernel reports) can lie inside an entry of the container, or inside a
    binary field of a journal entry. Nothing of the older copy is lost, because the container's parse stored that
    entry whole; the older copy's lines are found only inside the container's records, framed and grouped as the
    container's parse made them, and an entry that the older copy holds only in part is not stored as such a part.
  - *Result*: the outcome `contained`, the count line, the section, and the exit code.
- `docs/areas/storage.md` (owner **Dev**): schema version 7 (step 7), the table `import_line_hashes` in the table
  list, the columns `attempt` and `hashes_attempt` of `import_files`, the primary key as index, and the rules under
  *Import batches* / *Import bases*: attempts, completion with deletion, line-hash writes guarded by `complete = 0`,
  and the containment lookup.

## Documentation updates

- `README.md` (owner **Dev**), *Import logs* lines 270-284: the summary also lists contained files; exit code 0
  includes them. The limits sentence changes: an older copy imported after a newer one stores nothing, except one
  that ends inside a line.
- `.squad/project.md` (owner **Tester**), *Test doubles*, row "database (C#)": `FakeImportStore` also records line
  hashes and emulates attempts and containment.
- `docs/ARCHITECTURE.md` (owner **Lead**, done with this plan): *Migrations* names version 7, and *Imports* gets the
  sentence on recognizing older copies.
- `docs/decisions/0069-…md` and its index row (owner **Lead**, done with this plan).

## Architecture check

- **No data gaps unless explicitly recorded** concerns agent data and is not touched. For imports the equivalent risk
  is that lines get left out. A contained file is left out only after an exact match of 128 bits of SHA-256 over
  the whole older content, against a container that is complete and verified in pass 2, using rows of the attempt
  that completed it. The summary lists every contained file by name with its container. Nothing is silent.
- **A hanging collector never blocks the agent** and **backfill never alerts**: not touched (backend import only;
  imported records stay origin `import`).
- Storage rules (0063, 0077): a new table and columns through a migration step. Records are still never updated.
  The import state is still the only link between files and records.
- Memory: pass 1 is unchanged. Pass 2 holds at most one row of 4,096 hashes plus the line ends of one read
  (AC12). The lookup reads at most 64 rows of at most 64 KiB.

## Security considerations

- [x] Byte and length limits: line ends and offsets count **raw decompressed bytes**. Nothing is decoded, and the
  limits sit before any text decoding. Bounds: `MaxLineHashes` 4,096 per row (64 KiB blob, also a `CHECK` in the
  schema), `MaxImportContainers` 64 candidate rows per lookup, and one row queued plus one read's line ends in pass 2.
- [x] User-visible failure texts: "the import state changed (another import of the same content is running?)" for
  `ImportConflictException`, "database error" (and the run ends) for any other `StoreException`, "interrupted" for a
  cancellation. All already exist in `ImportPass.StoreReason`. No store or SQLite message reaches the summary.
  `ContainedIn` is input-derived (the container's display path) and is printed only through `Terminal.Quote`.
- **Forms the recognition accepts.** This is a guard that decides that input lines are not stored. The "parser" is
  byte equality of decompressed content, checked through truncated SHA-256. Behavior on each form:
  1. O is exactly N's bytes up to a line end of N, same source type, N complete with hashes of its final attempt:
     contained.
  2. O ends inside a line: imported whole. No hash exists at a non-line-end offset.
  3. Same first line, different bytes anywhere before the end: imported whole. A false match needs a 128-bit
     collision of truncated SHA-256: about 2^64 work for someone who controls both contents completely, 2^128 for a
     given one.
  4. O equal to a stored content (same SHA-256): existing state ("already imported" or resume). The containment
     lookup is not reached.
  5. O packed differently (`.gz`, inside a tar, either order): compared on the decompressed content, contained.
  6. Container of another source type, incomplete, failed, interrupted, from schema 6, or with only rows of a stale
     attempt (changed bytes in pass 2, a lost concurrent run): imported whole.
  7. O longer than every candidate: not contained; the forward continuation rules apply as today.
  8. More than 64 candidates with the same head key: only the 64 largest are compared, a smaller container is missed
     (imported whole).
  9. Concurrent import of N while O begins: the lookup sees only committed complete rows. Either outcome is correct
     (contained, or whole as before).
  10. A web-space user's file that starts with the exact bytes of a real log (head included) and is imported before
      that log: it becomes the real log's container, and the real log's lines are found only under the planted
      file's `log` name. This is the same exposure as the forward continuation in 0069 (the attacker must know the
      real log's bytes exactly, which mode 0640 `root:adm` withholds). It is stated as a limitation in the area.
- Who checks: Security in steps 3 and 8 (tier `security`). The Tester covers forms 1–8 (AC1–AC6, AC11), and the
  Reviewer checks form 9 by reading the transaction scope.

## Decision records

- `docs/decisions/0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md`: extended in place
  (unreleased: there are no tags), status set back to `Proposed`, index row `Proposed`. New options H1–H4 (how
  containment is verified), W1–W2 (hashes of a failed or concurrent attempt), S1–S2 (whether the older copy keeps a
  state). Decision: H3, W2, S2. Consequences replace the "only the newer-after-older order" bullet.

## Challenge

Devil's Advocate: 0 major, 2 minor objections.

1. *`ImportFile.Id` of 0 as a "contained" sentinel overloads `Complete`/`Id`; rely on a `ContainedIn` check alone,
   state that `ImportPass` checks it before using `state.Id`, and test it.* **Accepted.** The contained result is now
   signalled by a non-empty `ContainedIn` alone: the store sets nothing else (`Id`, `Complete` and `Attempt` keep
   their defaults and carry no meaning), and `Complete` is no longer set to true for it. `ImportPass.BeginAsync`
   checks `ContainedIn` right after the store call, before `state.Complete`, the source-type check and any use of
   `state.Id`. Changed: Approach step 3, the doc of `BeginImportAsync`, the doc of `ImportFile.ContainedIn`, the
   `ImportPass` body description, the `FakeImportStore` emulation, and the new AC19 with its tests in
   `ImporterStoreTests.cs` (the fake returns exactly the store's shape, `Complete` false, so a check of `Complete`
   first would import O with file ID 0 and fail the test) and `ImportTrackingTests.cs` (store side).
2. *For a journal export the older copy is cut at a line end that may fall inside a multi-line entry of the
   container; the limits text should say the grouping is the container's.* **Accepted.** Nothing is lost, as the
   objection notes, but the area document must say it. *Areas* now gives the Dev the exact content of that sentence
   for `docs/areas/log-import.md` (*Limits of the guarantee*), covering `LineFramed` false parsers and grouping
   parsers alike. Record 0069 (*Consequences*) already states that the lines are present as the container's parse
   stored them, with an open entry stored with its later lines; it is unchanged.

## Out of scope / follow-ups

- An older copy that ends inside a line (a write caught by the copy) is still imported as a whole. Recognizing it
  would need the hash at its last line end from pass 1 plus a one-line continuation (the cut line stored again, as in
  the forward case). This is a candidate follow-up if it shows up in practice. Not opened now.
- Follow-up issue for the orchestrator (the related defect above). Title: "[Logs] Print the import summary when
  pass 2 is interrupted outside the parser". Body: "`ImportPass` lets an `OperationCanceledException` escape when
  Ctrl-C arrives between archive entries (`ReadArchiveAsync` → `GetNextEntryAsync`, `ImportPass.cs:277`) or during
  the verification read (`SettleAsync` catches only I/O exceptions, `ImportPass.cs:151`). `Importer.RunAsync` does not
  catch it after the scan (`Importer.cs:69`), so `ImportCommand` logs 'interrupted' and returns 1 without printing the
  summary of the files already handled, although `docs/areas/log-import.md` (*Result*) says the summary is still
  printed. Found by reading during #187, not reproduced. Acceptance: an interruption at either point gives
  `Summary.Interrupted` and a printed summary."
- Containers imported before schema version 7 never get line hashes. Re-importing them stores nothing, so they cannot
  get any. Backfilling would need the original files. Not planned.
