# Plan: Import framework for log archives

Source: Issue #15 | [spec.md](spec.md)
Status: Draft
Tier: security — the change adds the reading of external files and archives (security areas 9 *File writes
and paths derived from external input*, which names the log import, and 10 *Parsing of external input*,
which names log files) and a new database table written from that input.

## Problem / root cause

Summary of [spec.md](spec.md): `vandoxd import <path>` reads a directory, a tar or tar.gz archive, a gzip
file or a plain file as streams, detects a parser per file through a registry, stores the parsers'
records in batches, records each file's content hash so that a re-import (also after an interruption)
stores nothing twice, lists every file it does not import with the reason, and prints a summary. The
parser interface, the registry and a bounded line reader live in `internal/logparse` (shared, because #16
requires its parsers in `internal/` for reuse by the agent); the import itself is backend-only
(`cmd/vandoxd/internal/importer`, 0061).

Claims of the issue, checked against the code:

- "Depends on #14 (merged)" — **confirmed**: `cmd/vandoxd/internal/store` exists (PR #130) with
  `WriteBatch`, schema version 2 (`store.go` l. 21, `migrate.go`). #14 itself stays open only for the
  DS918+ measurement (0068).
- "Trigger via CLI sub-command (`vandoxd import <path>`)" — **confirmed as missing and conflicting**:
  `run` in `cmd/vandoxd/main.go` l. 46–50 rejects every positional argument with exit code 2, as record
  0058 decided. The sub-command changes that decision, so 0072 supersedes 0058.
- "file content hashes are stored; re-importing does nothing" — **confirmed as missing**: the schema
  (`migrate.go`) has no table for imported files, and records of origin `import` are deliberately not
  deduplicated (partial unique index `records_agent_seq` only for origin `agent`, `migrate.go` l. 37; 0063).
  Idempotency therefore has to come from file-level tracking.
- "Input: a directory or archive (.tar, .tar.gz, single .gz rotations)" — nothing reads such input yet.
  The compose file already mounts `./import:/import:ro` (`deploy/backend/docker-compose.yml`, 0060), so the
  container can reach the input without a deployment change.
- "hand it to the matching parser" — **confirmed: no parser exists** (`internal/` has no log parsing
  package). #16–#20 add them; this change defines the interface and registers none in production.
- "Streaming – never load whole files into memory" — a design requirement; note that `docker exec` runs
  the import in the service's container, which shares its 512 MiB `mem_limit` (0060) with the service.
  Batches are therefore bounded in records **and** bytes (0071).
- Related defects found on the way: none. (0063's statement that `store.WriteBatch` is the only code that
  inserts into `log_lines` stays true: the import writes through `WriteBatch`, see *Approach*.)

## Acceptance criteria

Store (`cmd/vandoxd/internal/store`)

- [ ] AC-S1: `Open` migrates to schema version 3, which adds the table `import_files` (columns and checks as
  in 0069); `SchemaVersion` is 3; a version-2 database with records is migrated and keeps its records;
  steps 1 and 2 are unchanged.
- [ ] AC-S2: `BeginImport` with an unknown SHA-256 inserts a file (records 0, not complete) and returns it
  with its ID; with a known SHA-256 it returns the stored file unchanged (name, file name, modification
  time, type, records, completion of the first import), whatever the other fields say. `FileName` is
  stored and returned byte for byte (also invalid UTF-8 and control characters); `ModTime` is returned in
  UTC with nanoseconds, and as the zero time when it was zero or outside the storable range (stored as
  unknown).
- [ ] AC-S3: `BeginImport` refuses, with an error wrapping `ErrInvalidImport` and writing nothing: a zero or
  non-UTC `StartedAt`, a negative `Size`, a `SourceType` that `logparse.CheckType` refuses, a `FileName`
  longer than `MaxImportNameBytes` bytes. It stores `Name` with invalid UTF-8 replaced by U+FFFD and cut to
  `MaxImportNameBytes` bytes (not inside a UTF-8 sequence).
- [ ] AC-S4: `WriteBatch` with `Batch.Import` stores the records and advances the file's `records` by their
  number in the same transaction; with `Complete` it also sets `complete = 1` and `completed_at` to
  `ReceivedAt`. A batch without records is valid only with `Import.Complete`. After import batches of log
  lines, FTS5 `integrity-check` passes and every line is found by `SearchLogs` (0063).
- [ ] AC-S5: `WriteBatch` with `Batch.Import` whose `Done` differs from the stored count, or for a complete
  or unknown file, returns an error wrapping `ErrImportConflict` and stores nothing (no record row, count
  unchanged).
- [ ] AC-S6: `WriteBatch` refuses with `ErrInvalidBatch`, writing nothing: `Import` with a record whose
  origin is not `import`, with a non-empty `AgentID`, with `FileID` ≤ 0 or `Done` < 0.
- [ ] AC-S7: `CheckImportRecord` accepts a valid record of origin `import` and refuses one of origin `agent`
  or `backend`, an invalid record (model rule) and one with `CapturedAt` outside the storable range.
- [ ] AC-S8: `storetest.Fake.BeginImport` returns by default a new, incomplete file with IDs 1, 2, … in call
  order and the given fields; `OnBeginImport` scripts the result; calls are recorded (`ImportStarts`); it
  honors `Block`; `Batches` returns batches whose `Import` is a copy, not shared with the caller.

Parser interface (`internal/logparse`, `internal/logparse/logparsetest`)

- [ ] AC-P1: `NewRegistry` refuses (error wrapping `ErrInvalidParser`) a nil parser, a type that
  `CheckType` refuses, and two parsers with the same type; `CheckType` accepts `^[a-z][a-z0-9._-]{0,63}$`
  and nothing else (table: empty, upper case, leading digit, 65 bytes, space, `/`, non-ASCII).
- [ ] AC-P2: `Registry.Detect` returns the parser with the highest confidence; on a tie the one registered
  first; `(nil, NoMatch)` when every parser returns `NoMatch` or the registry is empty; it passes the file
  and the head unchanged. `Types` returns the types in registration order.
- [ ] AC-P3: `LineReader` returns lines without `\n` or `\r\n` (a lone `\r` stays), the last line without a
  newline, empty lines, then `io.EOF`; `Line` counts from 1; a read error other than `io.EOF` is returned.
- [ ] AC-P4: a line longer than `MaxLineBytes` is returned cut to at most `MaxLineBytes` bytes, not inside a
  UTF-8 sequence, with `truncated` true; the rest of that line is discarded and the next line is returned
  intact; reading one 64 MiB line allocates less than 1 MiB in total (`runtime.MemStats.TotalAlloc` delta).
- [ ] AC-P5: `logparsetest.Parser` returns `TypeName`, calls `DetectFunc` (nil: `NoMatch`) and `ParseFunc`
  (nil: reads nothing, returns nil), and records the files of every `Parse` call (`Parsed`); `HeadPrefix`
  and `Lines` behave as documented in *Signatures*; `Lines` returns `ctx.Err()` once the context is done
  and the emitter's error as soon as it returns one.

Importer (`cmd/vandoxd/internal/importer`)

- [ ] AC-I1 (input forms): with a test parser that claims every file, `Run` imports the same lines from:
  a directory tree (recursively, in lexical order), a single plain file, a single gzip file, a
  multi-member gzip file (all members), a `.tar`, a `.tar.gz` (USTAR, PAX and GNU format), a tar.gz inside
  the root directory, and gzip-compressed entries inside a tar; the parser receives `logparse.File.Name`
  as specified in 0071 (relative slash path or cleaned entry name, trailing `.gz` removed after
  decompression) and the file's or entry's `ModTime` in UTC.
- [ ] AC-I2 (listed, not imported): each form in the *Accepted input forms* table whose behavior is
  "listed" appears in `Summary.Files` with `OutcomeUnrecognized` (or `OutcomeFailed` where the table says
  so) and the table's reason; a FIFO in the directory and a FIFO as root never block `Run`; tar directory
  entries and PAX global headers are not listed.
- [ ] AC-I3 (idempotent): a second `Run` of the same input against the same real store reports every
  previously imported file as `OutcomeAlreadyImported` and stores no record (record count unchanged);
  a file with the same content under another name or gzip-compressed is `OutcomeAlreadyImported`; two
  files with the same content in one run are stored once.
- [ ] AC-I4 (resume): when the context is cancelled after the first committed batch of a file, `Run`
  returns `context.Canceled` with `Summary.Interrupted`; a second `Run` stores exactly the remaining records
  (`ResumedAfter` = records stored before), and the store then holds every record of the file once. When
  the second `Run` reads the same content under another name and with another modification time (e.g. the
  file renamed, or gzip-compressed with a newer mtime), the parser's `Parse` receives the `logparse.File`
  (name and `ModTime`) of the interrupted first run — pinned with `logparsetest.Parser.Parsed` — and the
  records stored by both runs are those of one parse under that `File`. A first import (unknown content)
  passes the current `File`, with a `ModTime` outside the storable range passed as the zero time.
- [ ] AC-I5 (summary): `Summary` holds, per file, path, source type, outcome, reason, lines, records,
  skipped, first and last capture time and at most `MaxProblems` problems; the totals `Lines`, `Records`,
  `Skipped`, `First`, `Last` over the run; `Started`/`Finished` from `Options.Now`; `len(Files)` equals the
  sum of `Count` over the four outcomes. Lines are the number of `\n` plus one for a non-empty last line
  without `\n`.
- [ ] AC-I6 (per-file failures, the run continues): a truncated or corrupt gzip file and a gzip file with
  trailing garbage → `OutcomeFailed`, nothing stored for it; a corrupt tar header after two good entries →
  the two entries imported and the archive listed `OutcomeFailed`; a parser returning an error →
  `OutcomeFailed` with the records emitted before it stored; a record that `store.CheckImportRecord`
  refuses → counted in `Skipped` with a problem, the other records stored; a file whose stored import has
  another source type → `OutcomeFailed`; content whose first `size` bytes (the size pass 1 hashed) changed
  between the two passes, or that became shorter → `OutcomeFailed` ("changed while it was imported") and
  the file not marked complete; content that only **grew** between the passes (bytes appended after pass 1,
  — the test changes, truncates or appends to a plain file in the root directory from its `Options.Progress`
  callback on `EventScanned`, which runs between the passes; no test hook in production code) →
  `OutcomeImported` with exactly the records
  of the first `size` bytes, the appended bytes neither parsed nor counted in `Lines`; `ErrImportConflict`
  → `OutcomeFailed`.
- [ ] AC-I7 (run-level errors): a missing root, a root that is neither a directory nor a regular file, and
  more than `MaxFiles` entries (`ErrTooManyFiles`, nothing written) return an error; any other store error
  stops the run with an error and a summary of what was done; invalid `Options` (nil registry, store or
  clock; `BatchRecords` above `store.MaxBatchRecords`; a negative `BatchRecords`, `BatchBytes` or
  `ProgressBytes`) return an error before
  anything is read. The entry limit stops the scan **at the entry `MaxFiles`+1**, without reading, detecting
  or hashing anything after it, pinned with: (a) a `.tar.gz` root of `MaxFiles`+3 regular entries of one
  byte each, all claimed by a counting `DetectFunc` — `Run` returns an error wrapping `ErrTooManyFiles`,
  `DetectFunc` was called exactly `MaxFiles` times, `storetest.Fake.ImportStarts` and `Batches` are empty;
  (b) a root directory holding `MaxFiles`+1 one-byte files — `ErrTooManyFiles` and `DetectFunc` never called
  (the directory's entries are counted while it is read, before any file is opened); (c) AC-I14's
  stop-without-reading-further test of `eachEntry`.
- [ ] AC-I8 (batching): a batch is flushed when it holds `BatchRecords` records, or when a record is added
  and the input consumed since the batch's first record was buffered has reached `BatchBytes` bytes,
  whichever comes first; the byte bound never flushes an empty batch. Every batch carries `Import` with
  `Done` equal to the records stored before it, the last one `Complete`; only that last batch may be empty
  (a recognized file without stored records — none parsed, all skipped, or all dropped on resume — is
  completed with one empty batch); every record has origin `import`; `ReceivedAt` is `Options.Now()` in
  UTC. Pinned with: (a) a resume that drops more than `BatchBytes` of input before the first new record
  (small `BatchBytes`) — no empty batch before the last, every batch accepted by the real store; (b) a
  parser that only calls `Skip` over more than `BatchBytes` of input — exactly one batch, empty and
  `Complete`.
- [ ] AC-I9 (flat memory): importing a generated gzip file of at least 64 MiB of decompressed lines through
  `Run` (test parser built on `logparsetest.Lines`, a test store that discards batches) keeps the live heap,
  sampled after `runtime.GC()` at least ten times during the import, below the baseline before `Run` plus
  16 MiB, and reports every line.
- [ ] AC-I10 (progress): during the scan, `Options.Progress` receives `EventFileFinished` for every file
  listed in the scan and, while a recognized file is hashed, one `EventScanProgress` (with `Path` and
  `Bytes`, the decompressed bytes of that file read so far) each time another `ProgressBytes` bytes of it
  have been read — a file of `n` bytes gives exactly `n / ProgressBytes` such events (pinned with a small
  `Options.ProgressBytes`); then `EventScanned` with `Files` and `Pending`; then per imported file
  `EventFileStarted`, one `EventFileProgress` per `ProgressLines` lines, and `EventFileFinished` with the
  file's `Result`.
- [ ] AC-I11 (parser stops early, context): a parser that returns before the end of its input still gets
  the whole content hashed and its lines counted; a parser that ignores the context is stopped by the
  importer's reader returning `ctx.Err()`.
- [ ] AC-I12 (format sniffing, `sniffFormat`): table of heads — gzip (`1f 8b 08`), tar (USTAR/PAX and GNU
  magic at offset 257), empty, bzip2, xz, zstd, lz4, zip, 7z, a head shorter than 262 bytes, plain text,
  `1f 8b` with another method byte (plain).
- [ ] AC-I13 (`openSource`, `openRegular`, `openDir`): `openSource` opens a directory root and a regular-file
  root (through the directory holding it), resolves a symbolic link as root once, and refuses a FIFO, a
  socket and a missing path without opening it. `openRegular(root, name)` opens a regular file below the
  root; it refuses with an error and without blocking: a directory, a FIFO, a symbolic link whose target is
  outside the root (`../`), a symbolic link with an absolute target, a symbolic link to a directory, a name
  with `..` leaving the root. A symbolic link to a regular file **inside** the root is opened (`os.Root`
  semantics; documented, since the scan lists links before and never passes one to `openRegular`).
  `openDir(root, name)` opens a directory below the root and refuses a FIFO (without blocking) and a regular
  file.
- [ ] AC-I14 (`eachEntry`): calls `fn` for every header with its ordinal and content; with
  `t.Setenv("GODEBUG", "tarinsecurepath=0")` an archive with entries named `../x`, `/abs` and `ok` gives the
  same three calls (names unchanged, `tar.ErrInsecurePath` with a header is not an error) as without it;
  when `fn` returns an error for entry *k*, `eachEntry` returns that error, calls `fn` no more, and never
  reads the stream past the end of header *k* (a test reader that fails every read beyond that offset,
  measured while the test archive is written).

Command (`cmd/vandoxd`)

- [ ] AC-C1: `run(ctx, ["import", path], …)` and `run(ctx, ["-config", f, "import", path], …)` and
  `run(ctx, ["import", "-config", f, path], …)` import `path` with the configuration `f` and exit 0 when no
  file failed; the summary on stdout states the counts per outcome, lines, records, skipped, the time range
  (RFC 3339 UTC, or that no records were stored), and lists every not-recognized and failed file with its
  reason and every file with skipped lines with its first problems.
- [ ] AC-C2: every path and reason in the summary is printed quoted (Go `%q`): a file name containing a
  newline and an ANSI escape produces one summary line with the characters escaped; a file name containing
  U+009B (C1 CSI) and U+202E (right-to-left override) appears as `\u009b` and `‮`, and stdout holds
  neither rune raw (no bytes `C2 9B` or `E2 80 AE`).
- [ ] AC-C3: exit code 1 with the summary printed when a file failed or the import was interrupted; exit
  code 1 with a JSON error line on stderr for an invalid configuration, a database that cannot be opened
  and a missing root.
- [ ] AC-C4: exit code 2 with the usage on stderr for `import` without a path, with two paths, with an
  undefined flag, and for `-healthcheck import x`; `import -h` exits 0 and prints the import usage; the
  existing usage errors (`extra`, `-config f extra`, `-healthcheck extra`) are unchanged; the main usage
  lists the `import` sub-command.
- [ ] AC-C5: progress is logged as JSON lines on stderr through the `log.level` of the configuration
  (one `file finished` line per file at `info`, one `hashing` line per `EventScanProgress` at `info` with the
  path and the bytes read as attributes); `-version` still wins over everything. Every attribute whose value
  derives from the input or the command line — `path`, `reason`, and `error` of a run-level error — is
  logged as `strconv.Quote(value)`: for a file named with U+009B, U+202E, ESC, DEL and a newline, each stderr
  line decodes as JSON, its `path` equals `strconv.Quote(name)`, and stderr holds none of these runes raw
  (in particular no bytes `C2 9B`, `E2 80 AE` or `7F`).
- [ ] AC-C6: `importParsers()` returns no parser (pinned until #16 adds the first).

## Approach

**Packages.** `internal/logparse` (shared): `Parser`, `File`, `Confidence`, `Emitter`, `Registry`,
`CheckType`, `LineReader`. `internal/logparse/logparsetest`: a scripted parser for the tests of the
importer and the command (production code for the coverage gate, own test, like `storetest`).
`cmd/vandoxd/internal/importer`: scan, read, detect, hash, batch, summary. `cmd/vandoxd/import.go`: the
sub-command. `cmd/vandoxd/internal/store`: migration step 3, `BeginImport`, `Batch.Import`,
`CheckImportRecord`.

**Two passes** (0069). *Scan* (pass 1) walks the input, opens archives, decompresses gzip, reads the first
`logparse.SniffBytes` bytes of every file for sniffing and detection, and — only for recognized files —
reads the rest to compute SHA-256 and size; files that cannot be read completely fail here and are never
partly imported. Unrecognized, failed and non-regular entries are listed. **The entry limit is enforced
while scanning, not after it**: every directory entry read and every tar header (except PAX global headers)
counts; a directory is read in chunks (`(*os.File).ReadDir(256)`) and its entries are counted before they are
sorted, and a tar header is counted before its content is touched; the entry that brings the count to
`MaxFiles`+1 ends the scan at once with `ErrTooManyFiles` — it is neither read nor detected nor hashed, no
further header or directory chunk is read, and nothing has been written (pass 2 has not started). Memory
held by the scan is thus bounded by `MaxFiles` entries whatever the input holds. While a recognized file is hashed, the scan reports `EventScanProgress`
every `ProgressBytes` bytes, so a multi-GB file shows progress before pass 2 starts. *Import* (pass 2)
handles the recognized files in input order: it calls `BeginImport` with the hash, the size and the file's
`logparse.File` (name and modification time); a complete file is already imported; a file stored with another
source type fails; otherwise the file is (re)imported — resumed after its stored record count when an earlier
run was interrupted. **`Parse` always receives the `File` stored with the content** (`ImportFile.FileName`,
`ImportFile.ModTime`), which is the current one for a new content and the first run's one for a resumed
content, so a parser that derives data from the name or the modification time (#16's year inference) gives
the same records on resume (0069, 0070). The content is opened again (a directory file by path, archive
entries by reading the archive once more, sequentially, matching entries by ordinal), **limited to the
`size` bytes pass 1 hashed** (`io.LimitReader` on the decompressed stream), passed through a reader that
hashes, counts lines and bytes and checks the context, and parsed. Records are validated with
`store.CheckImportRecord` (refused ones are skipped with a problem), the first `ResumedAfter` valid records
are dropped, the rest are buffered and written with `WriteBatch` and an `ImportStep` whose `Done`
compare-and-set makes concurrent or repeated runs safe. A batch is flushed at `BatchRecords` records, or when
a record is added and the input consumed since the batch's first record reaches `BatchBytes`; dropped
records and skips never flush. After `Parse` returns, the rest of the limited content is drained; if fewer
than `size` bytes could be read or the hash of the `size` bytes differs from the scan's, the file fails and is
not completed; otherwise the last batch (possibly empty) completes it. Bytes appended to a log after pass 1
are thus ignored, not a failure: they belong to a later content with another hash (the grown-file limit
below).

**Store.** Migration step 3 creates `import_files` (0069). `BeginImport` runs on the writer pool:
`INSERT … ON CONFLICT(sha256) DO NOTHING`, then `SELECT` in the same transaction. `WriteBatch` with `Import`
first runs `UPDATE import_files SET records = records + ?, complete = ?, completed_at = ? WHERE id = ? AND
records = ? AND complete = 0` and refuses with `ErrImportConflict` unless exactly one row changed; then the
records go through the existing write path, so `log_fts` stays in step (0063).

**Input handling** follows the table below and 0071: the root is resolved once and opened as an `os.Root`
(a directory root itself, a file root through the directory holding it); every directory and file below it
is opened through that `os.Root` (`openDir`, `openRegular`: `O_NONBLOCK`, `O_DIRECTORY` for directories, and
`Fstat` on the opened descriptor), so no path component — not only the last — can lead outside the root,
even when the input is swapped while it is read. Listing never follows a link; nothing is extracted; names
from the input are labels only.

**Logging input-derived text** (0072): `slog`'s JSON handler escapes only `"`, `\`, characters below U+0020
and U+2028/U+2029; it writes C1 controls (U+0080–U+009F, e.g. U+009B CSI), DEL and format characters (e.g.
U+202E) raw — verified in the security review and again here. So `importCommand` logs every attribute whose
value derives from the input or the command line (`path`, `reason`, `error` of a run-level error) as
`strconv.Quote(value)`, which escapes every rune that `strconv.IsPrint` rejects — all of categories Cc, Cf,
Zl and Zp (checked over the whole Unicode range with Go 1.27), and invalid UTF-8. Message texts and the
other attributes (outcome, source type — `CheckType`-validated —, counts) are fixed or numeric.

**Command** (0072): `run` dispatches the first positional argument `import` to `importCommand`, which has
its own flag set (`-config`, default the global value), loads the configuration, opens the store, builds the
registry from `importParsers()`, runs `importer.Run` under `signal.NotifyContext` (SIGINT, SIGTERM), logs
progress with the JSON `slog` logger to stderr and writes the summary to stdout.

### Accepted input forms

From the real consumers: `os` (`(*os.File).ReadDir` reports entry types with `Lstat` semantics and does not
follow symbolic links; `os.Root` opens names component by component with `openat(…, O_NOFOLLOW)`, resolves a
symbolic link only when its target stays inside the root and is relative, and refuses `..` leaving it;
`filepath.WalkDir` is **not** used, because it reads each directory completely before the limit could stop
it), `compress/gzip` (multistream by default; header, data, checksum and trailing-data
errors), `archive/tar` (`Reader.Next` resolves PAX `x` and GNU `L`/`K` headers itself, returns PAX global
headers `g` as headers, converts `TypeRegA` to `TypeReg` or `TypeDir`, expands PAX sparse files and keeps
`TypeGNUSparse`; it bounds special headers to 1 MiB; with `GODEBUG=tarinsecurepath=0` it returns a valid
header **together with** `tar.ErrInsecurePath` for a non-local name and continues with the next call —
`eachEntry` treats that pair as a normal header, since names are labels only).

| Input form | Behavior |
| ---------- | -------- |
| Root: directory | walked recursively, entries in lexical order |
| Root: regular file | read as one file (gzip and tar detection apply) |
| Root: symbolic link | resolved once with `filepath.EvalSymlinks` (the operator named it); nothing below is followed |
| Root: FIFO, socket, device; missing; unreadable (also: the directory holding a root file is not readable, since a file root is opened through it) | `Run` error; a FIFO is never opened |
| Directory entry: regular file | read |
| Directory entry: subdirectory | descended |
| Directory entry: symbolic link (to file or directory) | listed unrecognized, "symbolic link (not followed)" |
| Directory entry: FIFO, socket, device | listed unrecognized, "not a regular file"; never opened |
| Directory entry: unreadable subdirectory or file | listed failed with the OS error (without repeating the path); walk continues |
| Regular file or directory replaced between listing and opening by a FIFO, device, directory or regular file of the other kind | `openRegular`/`openDir` fail without blocking (`O_NONBLOCK`, `O_DIRECTORY`, `Fstat` on the descriptor) → failed |
| Any path component replaced between listing and opening by a symbolic link | resolved by `os.Root` only when relative and inside the root, else the open fails → failed; never a read outside the root (a link to another file of the input reads that file, which is input anyway) |
| Relative path or entry name longer than `MaxPathBytes` (1024) | listed failed, "path too long", shown cut |
| Name with control characters, invalid UTF-8, `..`, absolute, `./`, `//` | read normally; a label only (never a file system path); cleaned with `path.Clean`, leading `/` removed; printed with `%q`; stored with U+FFFD, cut to 1024 bytes |
| Content starting `1f 8b 08` (gzip, any file name) | decompressed, all members; one layer only; `.gz` (case-insensitive) removed from the parser name |
| Gzip with corrupt header, data or checksum, truncated, or trailing non-gzip data | listed failed with the gzip error; nothing stored (pass 1 reads it completely) |
| Content after gzip starting `1f 8b 08` again | listed unrecognized, "compressed twice" |
| Content (after optional gzip) with `ustar\x0000` or `ustar  \x00` at offset 257 | a tar archive: read when it is the root or in the root's directory tree |
| Tar without magic (V7) | not an archive; goes to detection, normally "no parser recognized the file" |
| Tar entry `TypeReg` (incl. converted `TypeRegA`, PAX sparse) | read |
| Tar entry `TypeDir`, PAX global header | not listed (a `TypeDir` header counts toward `MaxFiles`, a PAX global header does not) |
| Tar entry with a non-local name while `GODEBUG=tarinsecurepath=0` (`Next` returns the header and `tar.ErrInsecurePath`) | handled like any header of its type; the name is a label |
| Tar entry `TypeSymlink` | listed unrecognized, "symbolic link (not followed)" |
| Tar entry `TypeLink` | listed unrecognized, "hard link (the content is in the linked entry)" |
| Tar entry `TypeChar`, `TypeBlock`, `TypeFifo`, `TypeCont`, `TypeGNUSparse`, unknown type | listed unrecognized, "not a regular file" |
| Tar entry whose content (after optional gzip) is a tar | listed unrecognized, "archive inside an archive (not opened)" |
| Corrupt tar header, truncated archive | entries before it are handled; the archive listed failed with the tar error |
| Duplicate entry names | each entry handled on its own content hash |
| Content `BZh`, `FD 37 7A 58 5A 00`, `28 B5 2F FD`, `04 22 4D 18`, `50 4B 03 04`, `37 7A BC AF 27 1C` | listed unrecognized, "unsupported format: bzip2/xz/zstd/lz4/zip/7z" |
| Empty content (0 bytes, after decompression) | listed unrecognized, "empty" |
| Anything else (text, BOM, CR LF, NUL bytes, invalid UTF-8, binary) | head and stream passed to the parsers unchanged; no parser → listed unrecognized, "no parser recognized the file" |
| More than `MaxFiles` (20,000) entries in the input (directory entries of any kind, tar headers except PAX global headers; e.g. a tar.gz of millions of tiny entries, a directory of millions of files) | `Run` error `ErrTooManyFiles` **as soon as entry `MaxFiles`+1 is counted** — before its content is read, without reading further headers or directory chunks; nothing written |
| Very large content (GNU sparse expansion, gzip bomb) | streamed; bounded in memory, not in time — the context (SIGINT/SIGTERM: Ctrl-C under `docker exec -it`) stops it cleanly; stopping the container kills it, and the per-batch transactions keep the database consistent. A bomb of **valid lines** (once parsers exist) is also stored, and so fills `storage.directory` until stopped — the operator watches the progress lines |
| Content appended to a file after pass 1 hashed it | pass 2 reads only the hashed `size` bytes; the appended bytes are ignored |
| Modification time outside the storable range (tar `mtime` before 1678 or after 2262) | detection sees it; stored as unknown, so `Parse` receives the zero time |

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `internal/logparse` | `logparse.go` (new) | `SniffBytes`, `File`, `Confidence` and its constants, `Emitter`, `Parser` |
| `internal/logparse` | `registry.go` (new) | `ErrInvalidParser`, `CheckType`, `Registry`, `NewRegistry`, `Detect`, `Types` |
| `internal/logparse` | `lines.go` (new) | `MaxLineBytes`, `LineReader`, `NewLineReader`, `Next`, `Line` |
| `internal/logparse/logparsetest` | `parser.go` (new) | `Parser`, `HeadPrefix`, `Lines` |
| `cmd/vandoxd/internal/store` | `migrate.go` | step 3 (`import_files`) |
| `cmd/vandoxd/internal/store` | `store.go` | `SchemaVersion = 3` |
| `cmd/vandoxd/internal/store` | `import.go` (new) | `MaxImportNameBytes`, `ErrInvalidImport`, `ErrImportConflict`, `ImportFileStart`, `ImportFile`, `ImportStep`, `BeginImport`, `CheckImportRecord` |
| `cmd/vandoxd/internal/store` | `write.go` | `Batch.Import`; `validateBatch` import rules; `WriteBatch` advances the import step in its transaction |
| `cmd/vandoxd/internal/store` | `repository.go` | `ImportTracker`; compile-time assertion |
| `cmd/vandoxd/internal/store/storetest` | `fake.go` | `OnBeginImport`, `BeginImport`, `ImportStarts`; `cloneBatch` copies `Import` |
| `cmd/vandoxd/internal/importer` | `importer.go` (new) | constants, `ErrTooManyFiles`, `Store`, `Options`, `Outcome`, `FileResult`, `Problem`, `Summary`, `Count`, `Event`, `Progress`, `Run`, pass 2 |
| `cmd/vandoxd/internal/importer` | `scan.go` (new) | pass 1: `source`, `openSource`, `scan`, `found`, `location`; entry limit while scanning |
| `cmd/vandoxd/internal/importer` | `archive.go` (new) | `eachEntry` |
| `cmd/vandoxd/internal/importer` | `sniff.go` (new) | `format`, `sniffFormat` |
| `cmd/vandoxd/internal/importer` | `open_unix.go` (new, `//go:build unix`), `open_other.go` (new, `//go:build !unix`) | `openRegular`, `openDir` |
| `cmd/vandoxd` | `main.go` | dispatch of `import`, usage text listing it |
| `cmd/vandoxd` | `import.go` (new) | `importEnv`, `importParsers`, `importCommand`, `writeSummary` |

## Signatures (for the Dev's skeleton)

The skeleton does **not** add migration step 3 and leaves `SchemaVersion` at 2 (the Tester's updated
`TestOpen_CreatesSchema` must fail until step 6). Unexported helpers not listed here are free for the Dev.

```go
// internal/logparse/logparse.go
// Package logparse defines the interface of the log parsers and the registry that picks one per file.
package logparse

// SniffBytes is the most content Detect is given: the first SniffBytes bytes of the decompressed file.
const SniffBytes = 4096

// File describes the file a parser reads.
type File struct {
	// Name is the slash-separated path relative to the import root or inside the archive, cleaned, without a
	// leading "/", and without a trailing ".gz" when the importer decompressed the file. A label only.
	Name string
	// ModTime is the modification time of the file or archive entry in UTC; zero when unknown.
	ModTime time.Time
}

// Confidence tells how well a parser matches a file; the registry picks the highest.
type Confidence int

const (
	NoMatch      Confidence = iota // the parser cannot read the file
	MatchName                      // only the name fits, or the content is not specific
	MatchContent                   // the content carries the format's signature
)

// Emitter receives what a parser reads from one file.
type Emitter interface {
	// Record takes the next record in file order. A non-nil error stops the import; Parse must return it.
	Record(r model.Record) error
	// Skip reports input that was not turned into a record: line is the 1-based line number, 0 when
	// unknown; reason is a fixed description that never contains input text.
	Skip(line int64, reason string)
}

// Parser reads one log format. Parse must be deterministic — the same content and File give the same records
// in the same order (an interrupted import is resumed by count) — must bound its memory independently of the
// input size, and must honor ctx.
type Parser interface {
	// Type returns the source type, unique in a registry and accepted by CheckType, e.g. "syslog".
	Type() string
	// Detect rates the file from its name and head, the first up to SniffBytes bytes of its content.
	Detect(f File, head []byte) Confidence
	// Parse reads r, the file's whole decompressed content, and passes every record to out.Record in order.
	// Records have origin import and UTC capture times.
	Parse(ctx context.Context, f File, r io.Reader, out Emitter) error
}

// internal/logparse/registry.go
// ErrInvalidParser is wrapped by the errors of NewRegistry and CheckType.
var ErrInvalidParser = errors.New("logparse: invalid parser")
// CheckType returns nil when t matches ^[a-z][a-z0-9._-]{0,63}$, else an error wrapping ErrInvalidParser.
func CheckType(t string) error
// Registry picks the parser for a file. The zero value is not usable; use NewRegistry.
type Registry struct{ /* unexported */ }
// NewRegistry returns a registry of parsers in priority order (on equal confidence the earlier one wins).
func NewRegistry(parsers ...Parser) (*Registry, error)
// Detect returns the parser with the highest confidence for f and head, or nil and NoMatch.
func (r *Registry) Detect(f File, head []byte) (Parser, Confidence)
// Types returns the parser types in registration order.
func (r *Registry) Types() []string

// internal/logparse/lines.go
// MaxLineBytes is the longest line LineReader returns; longer lines are cut.
const MaxLineBytes = model.MaxTextBytes
// LineReader reads lines of bounded length from a stream.
type LineReader struct{ /* unexported */ }
// NewLineReader returns a LineReader that reads from r.
func NewLineReader(r io.Reader) *LineReader
// Next returns the next line without "\n" or "\r\n", whether it was cut to MaxLineBytes (at a UTF-8
// boundary; the rest of the line is discarded), and io.EOF after the last line. The slice is valid until
// the next call.
func (l *LineReader) Next() (line []byte, truncated bool, err error)
// Line returns the 1-based number of the line Next returned last, 0 before the first.
func (l *LineReader) Line() int64

// internal/logparse/logparsetest/parser.go
// Package logparsetest provides a scripted logparse.Parser for tests.
package logparsetest
// Parser is a scripted logparse.Parser; safe for concurrent use. Set the fields before the first call.
type Parser struct {
	TypeName   string
	DetectFunc func(f logparse.File, head []byte) logparse.Confidence                             // nil: NoMatch
	ParseFunc  func(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error // nil: reads nothing
	// unexported: mutex, recorded files
}
func (p *Parser) Type() string
func (p *Parser) Detect(f logparse.File, head []byte) logparse.Confidence
func (p *Parser) Parse(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error
// Parsed returns the files Parse was called with, in call order.
func (p *Parser) Parsed() []logparse.File
// HeadPrefix returns a DetectFunc that reports c when head starts with prefix, else NoMatch.
func HeadPrefix(prefix string, c logparse.Confidence) func(f logparse.File, head []byte) logparse.Confidence
// Lines returns a ParseFunc that reads r with logparse.LineReader and emits per line a record of origin
// import, Source source, CapturedAt base plus the line number in seconds, and a *model.LogLine with Log
// source, Message the line and Truncated as reported; a line starting with "#" is reported with
// out.Skip(line, "comment line") instead. It returns ctx.Err() once ctx is done.
func Lines(source string, base time.Time) func(ctx context.Context, f logparse.File, r io.Reader, out logparse.Emitter) error

// cmd/vandoxd/internal/store/import.go
// MaxImportNameBytes is the longest file name BeginImport stores; longer names are cut.
const MaxImportNameBytes = 1024
// ErrInvalidImport is wrapped by the error BeginImport returns for a refused ImportFileStart.
var ErrInvalidImport = errors.New("store: invalid import")
// ErrImportConflict is wrapped by the error WriteBatch returns when Batch.Import does not match the stored
// import state (another run advanced it, or the file is complete or unknown); nothing is written.
var ErrImportConflict = errors.New("store: import state changed")
// ImportFileStart describes a file content the importer is about to import.
type ImportFileStart struct {
	SHA256     [32]byte  // of the decompressed content
	Size       int64     // bytes of the decompressed content, >= 0
	Name       string    // display path of the first import
	FileName   string    // logparse.File.Name the parser gets; stored byte for byte, at most MaxImportNameBytes
	ModTime    time.Time // logparse.File.ModTime; converted to UTC; zero or outside the storable range: unknown
	SourceType string    // parser type, logparse.CheckType
	StartedAt  time.Time // UTC
}
// ImportFile is the stored import state of a file content.
type ImportFile struct {
	ID          int64
	SHA256      [32]byte
	Size        int64
	Name        string
	FileName    string    // logparse.File.Name of the first import, exact
	ModTime     time.Time // logparse.File.ModTime of the first import, UTC; zero when unknown
	SourceType  string
	Records     int64 // records stored for the file so far
	Complete    bool
	StartedAt   time.Time
	CompletedAt time.Time // zero while not complete
}
// ImportStep ties a batch to the import of one file.
type ImportStep struct {
	FileID   int64
	Done     int64 // records of the file stored before this batch; must equal the stored count
	Complete bool  // the file is completely imported after this batch
}
// BeginImport returns the import state of the content f.SHA256, creating it (no records, not complete)
// when it is unknown. An existing state is returned unchanged.
func (s *Store) BeginImport(ctx context.Context, f ImportFileStart) (ImportFile, error)
// CheckImportRecord returns nil when WriteBatch accepts r in a batch with Import: the model rules, origin
// import, and the storable time range.
func CheckImportRecord(r *model.Record) error

// cmd/vandoxd/internal/store/write.go (changed)
type Batch struct {
	AgentID     string
	BootID      string
	ClockOffset *time.Duration
	ReceivedAt  time.Time
	Records     []model.Record
	Import      *ImportStep // optional: every record has origin import, AgentID is ""; the step is applied in the same transaction
}

// cmd/vandoxd/internal/store/repository.go (added)
// ImportTracker records which file contents have been imported.
type ImportTracker interface {
	BeginImport(ctx context.Context, f ImportFileStart) (ImportFile, error)
}

// cmd/vandoxd/internal/store/storetest/fake.go (added)
// in Fake: OnBeginImport func(f store.ImportFileStart) (store.ImportFile, error) // nil: a new file, IDs 1, 2, …
func (f *Fake) BeginImport(ctx context.Context, s store.ImportFileStart) (store.ImportFile, error)
// ImportStarts returns the BeginImport arguments so far, in call order.
func (f *Fake) ImportStarts() []store.ImportFileStart

// cmd/vandoxd/internal/importer/importer.go
// Package importer imports log files, directories and archives into the store.
package importer
const (
	MaxFiles            = 20000   // entries one run handles: directory entries of any kind and tar headers except PAX global headers; enforced while scanning
	MaxPathBytes        = 1024    // longest relative path or entry name
	DefaultBatchRecords = 2000    // records per batch
	DefaultBatchBytes   = 4 << 20 // input bytes per batch
	MaxProblems         = 10      // problems kept per file
	ProgressLines       = 100000  // lines between EventFileProgress
	DefaultProgressBytes = 64 << 20 // decompressed bytes between EventScanProgress while a file is hashed
)
var ErrTooManyFiles = errors.New("importer: too many files")
// Store is the part of the database the importer writes to.
type Store interface {
	store.Writer
	store.ImportTracker
}
// Options configure Run.
type Options struct {
	Parsers      *logparse.Registry // required
	Store        Store              // required
	Now          func() time.Time   // required; times are converted to UTC
	Progress     func(Progress)     // optional; called synchronously
	BatchRecords int                // 0: DefaultBatchRecords; at most store.MaxBatchRecords
	BatchBytes   int64              // 0: DefaultBatchBytes
	ProgressBytes int64             // 0: DefaultProgressBytes
}
// Outcome is what happened to a file.
type Outcome string
const (
	OutcomeImported        Outcome = "imported"
	OutcomeAlreadyImported Outcome = "already_imported"
	OutcomeUnrecognized    Outcome = "unrecognized"
	OutcomeFailed          Outcome = "failed"
)
// Problem is input a parser skipped or a record the store would refuse.
type Problem struct {
	Line   int64 // 1-based, 0 when unknown
	Reason string
}
// FileResult is the result of one file.
type FileResult struct {
	Path         string // display path: relative to the root; archive entries as "<archive path>:<entry name>"
	SourceType   string // "" when not recognized
	Outcome      Outcome
	Reason       string // why unrecognized or failed
	Lines        int64
	Records      int64 // stored in this run
	Skipped      int64
	ResumedAfter int64 // records an earlier, interrupted run had stored
	First, Last  time.Time
	Problems     []Problem // at most MaxProblems
}
// Summary is the result of a run.
type Summary struct {
	Root                    string
	Started, Finished       time.Time
	Files                   []FileResult // every file found, in input order
	Lines, Records, Skipped int64
	First, Last             time.Time // capture-time range of the records stored; zero when none
	Interrupted             bool
}
// Count returns the number of files with outcome o.
func (s *Summary) Count(o Outcome) int
// Event names a progress event.
type Event string
const (
	EventScanProgress Event = "scan_progress" // pass 1: Path, Bytes of a file being hashed
	EventScanned      Event = "scanned"
	EventFileStarted  Event = "file_started"
	EventFileProgress Event = "file_progress"
	EventFileFinished Event = "file_finished"
)
// Progress reports the state of a run.
type Progress struct {
	Event          Event
	Files, Pending int         // EventScanned: files found, files to import
	Path           string
	SourceType     string
	Lines, Records int64
	Bytes          int64       // EventScanProgress: decompressed bytes of the file read so far
	Result         *FileResult // EventFileFinished
}
// Run imports root and returns the summary; the error reports what stopped the run (the summary covers what
// was done until then).
func Run(ctx context.Context, root string, opts Options) (Summary, error)

// cmd/vandoxd/internal/importer/scan.go
// source is the opened import root.
type source struct {
	root *os.Root // the root directory, or the directory holding a root file
	file string   // the root file's name in root; "" when the root is a directory
}
// openSource resolves path once with filepath.EvalSymlinks, checks it with os.Lstat and opens it: a directory as
// the root, a regular file through the directory holding it. Anything else is an error, and nothing is opened
// before Lstat reported a directory or a regular file. The caller closes source.root.
func openSource(path string) (source, error)
// location tells where the content of a found file is read again.
type location struct {
	fsPath      string // the file, or the archive containing the entry; a name in source.root
	entry       int    // ordinal of the entry among the archive's headers; -1 for a plain file
	archiveGzip bool   // the archive is gzip-compressed
	gzip        bool   // the file or entry content is gzip-compressed
}
// found is one file of pass 1.
type found struct {
	result FileResult      // Path; Outcome and Reason when it is not imported
	file   logparse.File
	loc    location
	parser logparse.Parser // nil when not to be imported
	sum    [32]byte
	size   int64
}
// scan walks src (pass 1), lists every file and hashes the recognized ones, detecting with opts.Parsers and
// reporting EventFileFinished and EventScanProgress (every opts.ProgressBytes) to opts.Progress. It returns an
// error wrapping ErrTooManyFiles as soon as entry MaxFiles+1 is counted, without reading further.
func scan(ctx context.Context, src source, opts Options) ([]found, error)

// cmd/vandoxd/internal/importer/archive.go
// eachEntry reads the tar stream r and calls fn for every header Next returns, with its ordinal and content;
// a header returned together with tar.ErrInsecurePath is a normal header. It checks ctx between entries and
// returns the first error of the reader or of fn, without reading r any further after fn returned an error.
// It adds no read-ahead buffer of its own (the caller's gzip reader is the only buffering layer).
func eachEntry(ctx context.Context, r io.Reader, fn func(index int, h *tar.Header, content io.Reader) error) error

// cmd/vandoxd/internal/importer/sniff.go
type format int
const (
	formatPlain format = iota
	formatEmpty
	formatGzip
	formatTar
	formatUnsupported
)
// sniffFormat classifies head; for formatUnsupported it also returns the format's name.
func sniffFormat(head []byte) (format, string)

// cmd/vandoxd/internal/importer/open_unix.go and open_other.go
// openRegular opens name in root read-only with O_NONBLOCK and returns an error unless the opened file is
// regular (Fstat on the descriptor). os.Root resolves a symbolic link only inside root. On non-unix systems
// it returns errors.ErrUnsupported.
func openRegular(root *os.Root, name string) (*os.File, error)
// openDir opens the directory name in root read-only with O_DIRECTORY|O_NONBLOCK, so a FIFO or a file swapped
// in fails without blocking. On non-unix systems it returns errors.ErrUnsupported.
func openDir(root *os.Root, name string) (*os.File, error)

// cmd/vandoxd/import.go
// importEnv is what importCommand takes from the process: the parsers and the clock.
type importEnv struct {
	parsers []logparse.Parser
	now     func() time.Time
}
// importParsers returns the parsers of vandoxd import, in priority order; none until #16.
func importParsers() []logparse.Parser
// importCommand runs "vandoxd import" with the arguments after "import" and returns the exit code.
func importCommand(ctx context.Context, args []string, configPath string, environ []string, stdout, stderr io.Writer, env importEnv) int
// writeSummary writes s as text to w; every path and reason is quoted with %q.
// (Logging: importCommand passes path, reason and a run error's text to slog as strconv.Quote(value); the helper
// doing so is unexported and free for the Dev.)
func writeSummary(w io.Writer, s importer.Summary) error
```

`run` in `cmd/vandoxd/main.go` keeps its signature; it calls
`importCommand(ctx, fs.Args()[1:], *configPath, environ, stdout, stderr, importEnv{parsers: importParsers(), now: time.Now})`
when `fs.Arg(0) == "import"` and `-healthcheck` is not set.

## Test files

- `internal/logparse/registry_test.go` (AC-P1, AC-P2), `internal/logparse/lines_test.go` (AC-P3, AC-P4).
  `logparse.go` holds declarations only and gets no test file.
- `internal/logparse/logparsetest/parser_test.go` (AC-P5).
- `cmd/vandoxd/internal/store/import_test.go` (AC-S2, AC-S3, AC-S7), `cmd/vandoxd/internal/store/write_test.go`
  (AC-S4, AC-S5, AC-S6), `cmd/vandoxd/internal/store/migrate_test.go` (AC-S1),
  `cmd/vandoxd/internal/store/storetest/fake_test.go` (AC-S8).
- `cmd/vandoxd/internal/importer/importer_test.go` (AC-I1–AC-I11, end to end through `Run`, against a real
  store in `t.TempDir()` where store semantics matter — AC-I3, AC-I4, AC-I6 — per 0067, and against
  `storetest.Fake` or a test-local discarding store otherwise; AC-I9 must not use `storetest.Fake`, which
  keeps every batch; AC-I7 (a) and (b) through `Run`), `cmd/vandoxd/internal/importer/scan_test.go` (`scan`
  on the listed forms of AC-I2, and `openSource` of AC-I13), `cmd/vandoxd/internal/importer/archive_test.go`
  (AC-I14, `eachEntry`; the `GODEBUG` test uses `t.Setenv` and so is not parallel),
  `cmd/vandoxd/internal/importer/sniff_test.go` (AC-I12), `cmd/vandoxd/internal/importer/open_unix_test.go`
  (AC-I13: `openRegular`, `openDir`). Test files that create FIFOs (`syscall.Mkfifo`) carry `//go:build unix`. `open_other.go` is not
  compiled on Linux and has no test. Archives are built in the test with `archive/tar` and `compress/gzip`
  in `t.TempDir()`; no binary fixture is committed. Tests that need an unreadable file skip when running as
  root (`os.Geteuid() == 0`).
- `cmd/vandoxd/import_test.go` (AC-C1–AC-C3, AC-C5, AC-C6), `cmd/vandoxd/main_test.go` (AC-C4: dispatch and
  usage).

Existing test code affected: no existing call site breaks (`Batch` gains a field; keyed literals stay valid;
`run` keeps its signature). The assertion `SchemaVersion != 2` and the object list in
`TestOpen_CreatesSchema` (`cmd/vandoxd/internal/store/migrate_test.go` l. 68–74) are updated by the
**Tester** in step 5 to version 3 and `import_files`. `TestRun_UsageErrors` and `TestRun_Help`
(`cmd/vandoxd/main_test.go`) stay valid unchanged; the Tester adds the `import` cases.

## Documentation updates

Made by the Dev:

- `README.md`: *Binaries* — the `import` sub-command; *Layout* — `internal/logparse/` and the importer under
  `cmd/vandoxd/internal/`; a new subsection *Import logs* under *Run the backend with Docker Compose*: put the
  directory, archive or file into `import/`, grant read access **to the container's user 65532 only** —
  `sudo chown -R 65532:65532 import/<name> && sudo chmod -R u+rX import/<name>`, or, keeping the owner,
  `sudo setfacl -R -m u:65532:rX import/<name>` — never world-readable (a copied `/var/log` holds
  `0640 root:adm` files such as `auth.log` and `mail.log`, which are otherwise listed as failed with
  "permission denied", and which must not become readable for every local user); `import/` itself only
  needs to stay readable and searchable (as created, `0755`). Run `docker exec -it vandoxd /vandoxd import
  /import/<name>` (`-it` so that Ctrl-C reaches the import; without a terminal the import keeps running
  when the client is closed), what is read (input forms, nothing followed or extracted), the summary and
  the exit codes, re-import and resume (Ctrl-C stops after the current batch; stopping or restarting the
  container kills the import, which loses nothing stored and continues on the next run), the limitation for
  files that grew since their import (and that lines appended while the import runs are left for a later
  import), that a run handles at most 20,000 entries (split larger inputs), that a huge decompressed input
  of valid lines (a gzip bomb, once parsers exist) is stored and fills `storage.directory` until it is stopped
  — watch the progress lines and press Ctrl-C —, and that the parsers arrive with #16–#20.
- `docs/ARCHITECTURE.md`: the status paragraph (the import framework exists, no parser yet); *Components* —
  `importer` under `cmd/vandoxd/internal/`, `logparse` under `internal/`, and `vandoxd import`; *Data flow* —
  one paragraph on the import path (origin `import`, never live, so never alerting, 0022); *Storage and
  retention* — schema version 3 with `import_files`, and that imports are made idempotent per file content,
  not per record; links to 0069–0072, and 0072 instead of 0058 where the command line is described.
- `.squad/project.md`: *Security areas* 9 and 10 name `cmd/vandoxd/internal/importer` (`scan`, `openSource`,
  `openRegular`, `openDir`, `eachEntry`, `sniffFormat`, the limits) and `internal/logparse` (`LineReader`) with records 0069–0071;
  *Test doubles* — a row for log parsers (`logparsetest.Parser`, implemented) and `storetest.Fake`'s
  `BeginImport`; *Integration surface* — a new entry **A new log parser** touches: its type in `internal/`,
  registration in `importParsers` (`cmd/vandoxd/import.go`), detection tests against the other registered
  parsers, the README list of supported sources.

## Architecture check

- *No data gaps unless explicitly recorded* (0028): concerns collection and backfill; an import cannot know
  what is missing from an archive. It is not weakened: files that are not imported are listed with their
  reason, nothing is dropped silently. Imported data creates no gap records.
- *Backfilled data never raises an alert* / 0014, 0022: imported records carry origin `import`; they are
  never classified as live.
- Deduplication of agent records (*Security areas* 1, 0045, 0063) is untouched: import batches carry no
  agent ID, and `records_agent_seq` still applies only to origin `agent`.
- 0063's invariant (the store's write path keeps `log_fts` in step, `WriteBatch` is the only writer of
  `log_lines`) holds: the import writes through `WriteBatch`.
- 0064: a new step 3; databases at version 3 are refused by older builds (downgrade needs a backup).
- 0065: the import is a second process with its own writer pool; batches of at most 2,000 records or 4 MiB
  keep its lock short, so the service's 5 s `busy_timeout` is not reached; an import batch that waits more
  than 5 s fails, stops the run, and is resumed by the next run.
- 0058 is superseded by 0072 (a positional argument is no longer always a usage error).
- 0060: the read-only `/import` mount is used as planned; no deployment file changes. `docker exec` runs the
  import inside the service's memory limit, which is why batches are bounded in bytes.

## Security considerations

- **No writes from input** (area 9): nothing is extracted; entry names and paths are labels in the summary,
  in logs and in `import_files.name`, never file system paths. The only file the import writes is the
  database through the store (existing checks of 0065).
- **No following, no blocking** (areas 9, 10): see *Accepted input forms*. Listing never follows a link.
  Every open below the root goes through one `os.Root`, which opens each path component with
  `openat(…, O_NOFOLLOW)` and resolves a link only when it is relative and stays inside the root — so a swap
  of **any** component between listing and opening cannot redirect the read outside the root (a plain
  `O_NOFOLLOW` would protect only the last component). The narrower, accurate claim: such a swap can at
  most make another file **of the input** be read under the listed name. `openRegular` and `openDir` add
  `O_NONBLOCK` (and `O_DIRECTORY`) and check the opened descriptor with `Fstat`, so a swap cannot block on a
  FIFO either.
- **Bounded memory** (area 10): streams only; one decompression layer; head of 4 KiB; line length 16 KiB
  (`LineReader`); batches of at most 2,000 records and 4 MiB of input; at most 20,000 entries of at most
  1024-byte paths per run, **enforced while scanning** — the entry `MaxFiles`+1 ends the scan before it is
  read, and directories are read in chunks and counted before they are sorted — so the scan's list and a
  directory's entries stay below ~50 MiB in the worst case (typical inputs far less);
  `archive/tar` bounds its special headers to 1 MiB. **Time** is not bounded (a gzip bomb or a huge sparse
  entry is read to its end); every read checks the context, so SIGINT or SIGTERM to the import process
  (Ctrl-C under `docker exec -it`) ends it cleanly, and the run resumes later. The plan does **not** rely on
  `docker stop` for a clean stop: it signals only the container's PID 1 (the service), and the import is
  killed when the container stops. Correctness after a kill rests on the per-batch transactions (0065:
  `synchronous = FULL`, an uncommitted batch is rolled back at the next open) and the `Done`
  compare-and-set: a killed run leaves at most committed batches with a matching count, which the next run
  resumes.
- **Output injection** (area 12): the summary prints every path and reason with `%q`. Progress goes through
  the JSON `slog` handler, which alone is **not** enough: it writes C1 controls such as U+009B (CSI, which
  terminals interpret like `ESC [`), DEL and format characters such as U+202E raw (verified). Every
  attribute derived from the input or the command line (`path`, `reason`, a run error's text) is therefore
  logged as `strconv.Quote(value)`, which escapes every Cc, Cf, Zl and Zp rune and invalid UTF-8 (AC-C2,
  AC-C5). Parser `Skip` reasons are fixed texts by contract and are quoted anyway. OS errors are reported
  without repeating the path. Volume: a gzip bomb of valid lines fills `storage.directory` until it is
  stopped (no size limit, 0071); the progress lines make that visible.
- **Idempotency under concurrency**: the `Done` compare-and-set of `ImportStep` in the batch transaction makes
  two concurrent runs of the same content, or a run racing a resumed one, fail with `ErrImportConflict`
  instead of storing twice.
- **Integrity**: SHA-256 over the decompressed content; pass 2 reads exactly the `size` bytes pass 1 hashed,
  so a content whose hashed part changes or shrinks between the passes fails and is not completed, while
  bytes appended afterwards are not read. The change is only detected when pass 2 ends: records written from
  the changed content before that stay stored and counted under the original hash (0069, *Consequences*). A resumed content is parsed with the `File` stored at its first
  import, so the records of both runs come from one deterministic parse (0069, 0070).
- **Permissions**: the import runs as the container's user 65532 and reads only what that user may read; it
  gains no privilege. An unreadable file is listed as failed with the OS error; the README tells the
  operator to grant read access to user 65532 only (`chown` to 65532 or a `setfacl` entry for it) rather
  than to run the import as root or to make the copy world-readable — a copied `/var/log` holds
  `auth.log` and `mail.log`, which must stay unreadable for other local users.
- No new dependency (`archive/tar`, `compress/gzip`, `crypto/sha256`, `syscall`).

## Decision records

- `docs/decisions/0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md` (Proposed)
- `docs/decisions/0070-log-parser-interface-and-explicit-registry-in-internal-logparse.md` (Proposed)
- `docs/decisions/0071-log-import-reads-input-without-following-links-or-extracting.md` (Proposed)
- `docs/decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md` (Proposed, supersedes 0058; the
  Lead sets 0058 to `Superseded by 0072` at approval)

The index in `docs/decisions/README.md` lists only decided records (every row is `Accepted` or
`Superseded by …`). In step `approve-pr` the Lead (owner: Lead, file `docs/decisions/README.md`) adds rows
for 0069–0072 with status `Accepted`, changes the status of the 0058 row to
`Superseded by [0072](0072-vandoxd-import-sub-command-output-and-exit-codes.md)`, and sets the status line of
`docs/decisions/0058-vandoxd-runs-the-service-by-default-with-a-shutdown-deadline.md` to the same — the only
edit 0058 gets. Until then the Proposed records are linked from this plan only.

## Out of scope / follow-ups

- Parsers: #16–#20 (existing issues). Web UI trigger: a later issue (named in #15).
- **Follow-up issue to create** — title: `[Logs] Import only the new part of a log file that grew since its
  import`; body: "`vandoxd import` (#15) skips a file whose content hash was imported before (record 0069).
  A log file that grew since then — `syslog` imported once, later the same lines plus more as `syslog` or
  `syslog.1` in a newer copy of `/var/log` — has another hash and is imported as a whole, so the lines of
  the earlier import are stored twice. Goal: recognize that a file's content starts with a completely
  imported content (same length prefix and hash) and import only the rest, or otherwise avoid the
  duplicates. Acceptance: importing a newer copy of a grown log stores each line once; the summary says
  which files were continued."

## Challenge

Devil's Advocate, one round: 0 major, 6 minor objections. All six accepted; scope and tier unchanged
(`security`).

1. **Resume matched on the content hash alone, while `Parse` is deterministic only for the same content and
   `File`** — accepted. Instead of failing a resume whose name or modification time differ (which would
   leave a content that can never be completed once the first input is gone), the store keeps the `File`
   of the first import — `import_files.file_name` (exact bytes) and `mod_time` (Unix nanoseconds, NULL when
   unknown or outside the storable range) — and pass 2 always passes that stored `File` to `Parse`. A
   resumed content is thus parsed exactly as in the interrupted run, and the records of both runs come
   from one parse; no year can be mixed within a file. Revised: AC-S2, AC-S3, AC-I4, *Approach*, the input
   table, `ImportFileStart`/`ImportFile`, *Integrity*; 0069 (schema, decision, consequences, option
   rejected) and 0070 (contract wording).
2. **Pass 2 reads to EOF, so a log still being appended fails** — accepted. Pass 2 reads the decompressed
   content through `io.LimitReader(size)` and compares the hash of that prefix; only a change or shrinking
   of the hashed part fails, appended bytes are ignored and left to a later import. Revised: AC-I6,
   *Approach*, input table, *Integrity*, README text, 0069.
3. **`docker exec` without a TTY does not pass Ctrl-C; `docker stop` signals PID 1 only; uid 65532 cannot
   read `0640 root:adm` files** — accepted. The README documents `docker exec -it`, that closing a
   non-terminal client leaves the import running, that stopping the container kills it without losing
   committed data, and that the input must be readable by 65532 (`chmod -R a+rX`). The security text no
   longer relies on `docker stop`; it rests correctness after a kill on the per-batch transactions and the
   `Done` compare-and-set. Revised: input table, *Security considerations* (time, new *Permissions*),
   *Documentation updates*, 0071, 0072, spec.
4. **No progress while pass 1 hashes multi-GB files** — accepted. New `EventScanProgress` (`Path`, `Bytes`)
   every `ProgressBytes` (default 64 MiB, `Options.ProgressBytes` for tests) of a file being hashed, logged by
   the command as `hashing` at `info`. Revised: AC-I7 (negative bound), AC-I10, AC-C5, signatures, 0072.
5. **The byte bound can flush an empty batch** — accepted. The byte bound counts input from the batch's first
   buffered record and is checked only when a record is added; dropped (resumed) records and skips never
   flush, so the only empty batch is the completing one. AC-I8 gains the two named tests (resume past more
   than `BatchBytes` of dropped input; a parser that only skips). Revised: AC-I8, *Approach*, 0069.
6. **The decision index lacks 0069–0072 and 0058's status** — accepted as scheduled, not done now: the index
   lists only decided records, so rows for Proposed records would break its convention. The Lead adds them
   (and changes 0058) in `approve-pr`, as *Decision records* now states with owner and file.

### Security plan review, round 1

Security: CHANGES_REQUIRED with 3 blocking and 4 non-blocking points. All seven accepted; scope and tier
unchanged (`security`). (The `chmod -R a+rX` named in answer 3 above is replaced by B3.)

- **B1 — `MaxFiles` was checked after the scan, so a tar.gz of millions of tiny entries grows memory
  without bound** — accepted, fixed. The limit is enforced while scanning: every directory entry and every
  tar header except PAX global headers counts, directories are read in chunks of 256 and counted before
  sorting (so `filepath.WalkDir`, which reads a whole directory first, is no longer used), and the entry that
  brings the count to `MaxFiles`+1 ends the scan with `ErrTooManyFiles` before its content is read and
  without reading further. Revised: *Approach*, the input table row, `MaxFiles`, `scan` and `eachEntry` doc
  comments, *Bounded memory*, AC-I7 (tests a–c), new AC-I14; 0071 *Limits*.
- **B2 — the JSON `slog` handler writes U+009B and U+202E raw, so names can inject terminal escapes** —
  accepted, fixed; reproduced here (also DEL raw; ESC and U+2028 are escaped). Input-derived attributes
  (`path`, `reason`, a run error's text) are logged as `strconv.Quote(value)`; checked over the whole Unicode
  range that `strconv.Quote` escapes every Cc, Cf, Zl and Zp rune. The claim in *Output injection* is
  corrected. Revised: *Approach* (new paragraph), *Output injection*, AC-C2, AC-C5; 0072.
- **B3 — the README must not make a copied `/var/log` world-readable** — accepted, fixed: `chown -R
  65532:65532` plus `chmod -R u+rX`, or `setfacl -R -m u:65532:rX`, on `import/<name>` only. Revised:
  *Documentation updates*, *Permissions*; 0071, 0072, spec.
- **N1 — `O_NOFOLLOW` covers only the final component** — accepted, and both options taken: every open below
  the root now goes through `os.Root` (`openSource`, `openRegular(root, name)`, new `openDir`), which refuses
  any component leading outside the root; the claim is narrowed to what `os.Root` guarantees (a swapped
  in-root link can make another input file be read, never a file outside the root). Revised: *Input
  handling*, consumers paragraph, input table, signatures, AC-I13, *No following, no blocking*; 0071.
- **N2 — with `GODEBUG=tarinsecurepath=0`, `Next` returns a header and `tar.ErrInsecurePath`** — accepted;
  confirmed in `archive/tar/reader.go` (Go 1.27: the error is not sticky, the next call continues).
  `eachEntry` treats the pair as a normal header; AC-I14 tests it with `t.Setenv`.
- **N3 — a gzip bomb of valid lines also fills `storage.directory`** — accepted: stated in the input table,
  *Output injection* (volume), the README text and 0071 *Consequences*.
- **N4 — records from content that changed during pass 2 stay counted under the old hash** — accepted as a
  documented limitation in 0069 *Consequences* and *Integrity*; detecting the change before writing would
  need the hash before the parse, i.e. a third pass or a temporary copy (rejected in 0069, options 5 and 6).
  Removing such records belongs to "deleting or re-doing an import", already out of scope (spec); no
  separate follow-up issue.
