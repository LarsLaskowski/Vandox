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
  with its ID; with a known SHA-256 it returns the stored file unchanged (name, type, records, completion
  of the first import), whatever the other fields say.
- [ ] AC-S3: `BeginImport` refuses, with an error wrapping `ErrInvalidImport` and writing nothing: a zero or
  non-UTC `StartedAt`, a negative `Size`, a `SourceType` that `logparse.CheckType` refuses. It stores
  `Name` with invalid UTF-8 replaced by U+FFFD and cut to `MaxImportNameBytes` bytes (not inside a UTF-8
  sequence).
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
  (`ResumedAfter` = records stored before), and the store then holds every record of the file once.
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
  another source type → `OutcomeFailed`; content that changed between the two passes → `OutcomeFailed`
  ("changed while it was imported") and the file not marked complete; `ErrImportConflict` →
  `OutcomeFailed`.
- [ ] AC-I7 (run-level errors): a missing root, a root that is neither a directory nor a regular file, and
  more than `MaxFiles` files (nothing written, `ErrTooManyFiles`) return an error; any other store error
  stops the run with an error and a summary of what was done; invalid `Options` (nil registry, store or
  clock; `BatchRecords` above `store.MaxBatchRecords`; a negative batch bound) return an error before
  anything is read.
- [ ] AC-I8 (batching): batches are flushed after `BatchRecords` records and after `BatchBytes` bytes of
  input consumed since the last flush, whichever comes first; every batch carries `Import` with `Done`
  equal to the records stored before it, the last one `Complete`; a recognized file without records is
  completed with an empty batch; every record has origin `import`; `ReceivedAt` is `Options.Now()` in UTC.
- [ ] AC-I9 (flat memory): importing a generated gzip file of at least 64 MiB of decompressed lines through
  `Run` (test parser built on `logparsetest.Lines`, a test store that discards batches) keeps the live heap,
  sampled after `runtime.GC()` at least ten times during the import, below the baseline before `Run` plus
  16 MiB, and reports every line.
- [ ] AC-I10 (progress): `Options.Progress` receives `EventFileFinished` for every file listed in the scan,
  then `EventScanned` with `Files` and `Pending`, then per imported file `EventFileStarted`, at least one
  `EventFileProgress` per `ProgressLines` lines, and `EventFileFinished` with the file's `Result`.
- [ ] AC-I11 (parser stops early, context): a parser that returns before the end of its input still gets
  the whole content hashed and its lines counted; a parser that ignores the context is stopped by the
  importer's reader returning `ctx.Err()`.
- [ ] AC-I12 (format sniffing, `sniffFormat`): table of heads — gzip (`1f 8b 08`), tar (USTAR/PAX and GNU
  magic at offset 257), empty, bzip2, xz, zstd, lz4, zip, 7z, a head shorter than 262 bytes, plain text,
  `1f 8b` with another method byte (plain).
- [ ] AC-I13 (`openRegular`): opens a regular file; refuses a symbolic link (to a file and to a directory),
  a directory and a FIFO with an error and without blocking.

Command (`cmd/vandoxd`)

- [ ] AC-C1: `run(ctx, ["import", path], …)` and `run(ctx, ["-config", f, "import", path], …)` and
  `run(ctx, ["import", "-config", f, path], …)` import `path` with the configuration `f` and exit 0 when no
  file failed; the summary on stdout states the counts per outcome, lines, records, skipped, the time range
  (RFC 3339 UTC, or that no records were stored), and lists every not-recognized and failed file with its
  reason and every file with skipped lines with its first problems.
- [ ] AC-C2: every path and reason in the summary is printed quoted (Go `%q`): a file name containing a
  newline and an ANSI escape produces one summary line with the characters escaped.
- [ ] AC-C3: exit code 1 with the summary printed when a file failed or the import was interrupted; exit
  code 1 with a JSON error line on stderr for an invalid configuration, a database that cannot be opened
  and a missing root.
- [ ] AC-C4: exit code 2 with the usage on stderr for `import` without a path, with two paths, with an
  undefined flag, and for `-healthcheck import x`; `import -h` exits 0 and prints the import usage; the
  existing usage errors (`extra`, `-config f extra`, `-healthcheck extra`) are unchanged; the main usage
  lists the `import` sub-command.
- [ ] AC-C5: progress is logged as JSON lines on stderr through the `log.level` of the configuration
  (one `file finished` line per file at `info`); `-version` still wins over everything.
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
partly imported. Unrecognized, failed and non-regular entries are listed. More than `MaxFiles` files stop
the run before anything is written. *Import* (pass 2) handles the recognized files in input order: it calls
`BeginImport` with the hash; a complete file is already imported; an incomplete file of the same source type
is resumed after its stored record count; otherwise the content is opened again (a directory file by path,
archive entries by reading the archive once more, sequentially, matching entries by ordinal), passed through
a reader that hashes, counts lines and bytes and checks the context, and parsed. Records are validated with
`store.CheckImportRecord` (refused ones are skipped with a problem), the first `ResumedAfter` valid records
are dropped, the rest are buffered and written with `WriteBatch` and an `ImportStep` whose `Done`
compare-and-set makes concurrent or repeated runs safe. After `Parse` returns, the rest of the content is
drained; if the hash differs from the scan's, the file fails and is not completed; otherwise the last batch
(possibly empty) completes it.

**Store.** Migration step 3 creates `import_files` (0069). `BeginImport` runs on the writer pool:
`INSERT … ON CONFLICT(sha256) DO NOTHING`, then `SELECT` in the same transaction. `WriteBatch` with `Import`
first runs `UPDATE import_files SET records = records + ?, complete = ?, completed_at = ? WHERE id = ? AND
records = ? AND complete = 0` and refuses with `ErrImportConflict` unless exactly one row changed; then the
records go through the existing write path, so `log_fts` stays in step (0063).

**Input handling** follows the table below and 0071: nothing below the root is followed, every file is
opened with `O_NOFOLLOW|O_NONBLOCK` and checked to be regular after opening, nothing is extracted, names
from the input are labels only.

**Command** (0072): `run` dispatches the first positional argument `import` to `importCommand`, which has
its own flag set (`-config`, default the global value), loads the configuration, opens the store, builds the
registry from `importParsers()`, runs `importer.Run` under `signal.NotifyContext` (SIGINT, SIGTERM), logs
progress with the JSON `slog` logger to stderr and writes the summary to stdout.

### Accepted input forms

From the real consumers: `os`/`io/fs` (`filepath.WalkDir` reports entries with `Lstat` semantics and does not
follow symbolic links), `compress/gzip` (multistream by default; header, data, checksum and trailing-data
errors), `archive/tar` (`Reader.Next` resolves PAX `x` and GNU `L`/`K` headers itself, returns PAX global
headers `g` as headers, converts `TypeRegA` to `TypeReg` or `TypeDir`, expands PAX sparse files and keeps
`TypeGNUSparse`; it bounds special headers to 1 MiB).

| Input form | Behavior |
| ---------- | -------- |
| Root: directory | walked recursively, entries in lexical order |
| Root: regular file | read as one file (gzip and tar detection apply) |
| Root: symbolic link | resolved once with `filepath.EvalSymlinks` (the operator named it); nothing below is followed |
| Root: FIFO, socket, device; missing; unreadable | `Run` error; a FIFO is never opened |
| Directory entry: regular file | read |
| Directory entry: subdirectory | descended |
| Directory entry: symbolic link (to file or directory) | listed unrecognized, "symbolic link (not followed)" |
| Directory entry: FIFO, socket, device | listed unrecognized, "not a regular file"; never opened |
| Directory entry: unreadable subdirectory or file | listed failed with the OS error (without repeating the path); walk continues |
| Regular file replaced by a link or FIFO between listing and opening | `openRegular` fails (`O_NOFOLLOW`, `O_NONBLOCK`, `Fstat` not regular) → failed |
| Relative path or entry name longer than `MaxPathBytes` (1024) | listed failed, "path too long", shown cut |
| Name with control characters, invalid UTF-8, `..`, absolute, `./`, `//` | read normally; a label only (never a file system path); cleaned with `path.Clean`, leading `/` removed; printed with `%q`; stored with U+FFFD, cut to 1024 bytes |
| Content starting `1f 8b 08` (gzip, any file name) | decompressed, all members; one layer only; `.gz` (case-insensitive) removed from the parser name |
| Gzip with corrupt header, data or checksum, truncated, or trailing non-gzip data | listed failed with the gzip error; nothing stored (pass 1 reads it completely) |
| Content after gzip starting `1f 8b 08` again | listed unrecognized, "compressed twice" |
| Content (after optional gzip) with `ustar\x0000` or `ustar  \x00` at offset 257 | a tar archive: read when it is the root or in the root's directory tree |
| Tar without magic (V7) | not an archive; goes to detection, normally "no parser recognized the file" |
| Tar entry `TypeReg` (incl. converted `TypeRegA`, PAX sparse) | read |
| Tar entry `TypeDir`, PAX global header | not listed |
| Tar entry `TypeSymlink` | listed unrecognized, "symbolic link (not followed)" |
| Tar entry `TypeLink` | listed unrecognized, "hard link (the content is in the linked entry)" |
| Tar entry `TypeChar`, `TypeBlock`, `TypeFifo`, `TypeCont`, `TypeGNUSparse`, unknown type | listed unrecognized, "not a regular file" |
| Tar entry whose content (after optional gzip) is a tar | listed unrecognized, "archive inside an archive (not opened)" |
| Corrupt tar header, truncated archive | entries before it are handled; the archive listed failed with the tar error |
| Duplicate entry names | each entry handled on its own content hash |
| Content `BZh`, `FD 37 7A 58 5A 00`, `28 B5 2F FD`, `04 22 4D 18`, `50 4B 03 04`, `37 7A BC AF 27 1C` | listed unrecognized, "unsupported format: bzip2/xz/zstd/lz4/zip/7z" |
| Empty content (0 bytes, after decompression) | listed unrecognized, "empty" |
| Anything else (text, BOM, CR LF, NUL bytes, invalid UTF-8, binary) | head and stream passed to the parsers unchanged; no parser → listed unrecognized, "no parser recognized the file" |
| More than `MaxFiles` (20,000) files in the input | `Run` error `ErrTooManyFiles` after the scan, nothing written |
| Very large content (GNU sparse expansion, gzip bomb) | streamed; bounded in memory, not in time — the context (Ctrl-C, `docker stop`) stops it |

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
| `cmd/vandoxd/internal/importer` | `scan.go` (new) | pass 1: `scan`, `found`, `location` |
| `cmd/vandoxd/internal/importer` | `archive.go` (new) | `eachEntry` |
| `cmd/vandoxd/internal/importer` | `sniff.go` (new) | `format`, `sniffFormat` |
| `cmd/vandoxd/internal/importer` | `open_unix.go` (new, `//go:build unix`), `open_other.go` (new, `//go:build !unix`) | `openRegular` |
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
	SourceType string    // parser type, logparse.CheckType
	StartedAt  time.Time // UTC
}
// ImportFile is the stored import state of a file content.
type ImportFile struct {
	ID          int64
	SHA256      [32]byte
	Size        int64
	Name        string
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
	MaxFiles            = 20000   // files (incl. listed ones) one run handles
	MaxPathBytes        = 1024    // longest relative path or entry name
	DefaultBatchRecords = 2000    // records per batch
	DefaultBatchBytes   = 4 << 20 // input bytes per batch
	MaxProblems         = 10      // problems kept per file
	ProgressLines       = 100000  // lines between EventFileProgress
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
	Result         *FileResult // EventFileFinished
}
// Run imports root and returns the summary; the error reports what stopped the run (the summary covers what
// was done until then).
func Run(ctx context.Context, root string, opts Options) (Summary, error)

// cmd/vandoxd/internal/importer/scan.go
// location tells where the content of a found file is read again.
type location struct {
	fsPath      string // the file, or the archive containing the entry
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
// scan walks root (pass 1), lists every file and hashes the recognized ones.
func scan(ctx context.Context, root string, reg *logparse.Registry) ([]found, error)

// cmd/vandoxd/internal/importer/archive.go
// eachEntry reads the tar stream r and calls fn for every header Next returns, with its ordinal and content;
// it checks ctx between entries and returns the first error of the reader or of fn.
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
// openRegular opens path read-only without following a symbolic link and without blocking, and returns an
// error unless it is a regular file (checked on the open file). On non-unix systems it returns
// errors.ErrUnsupported.
func openRegular(path string) (*os.File, error)

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
  keeps every batch), `cmd/vandoxd/internal/importer/scan_test.go` (`scan` on the listed forms of AC-I2 and
  AC-I7's limits), `cmd/vandoxd/internal/importer/archive_test.go` (`eachEntry`),
  `cmd/vandoxd/internal/importer/sniff_test.go` (AC-I12), `cmd/vandoxd/internal/importer/open_unix_test.go`
  (AC-I13). Test files that create FIFOs (`syscall.Mkfifo`) carry `//go:build unix`. `open_other.go` is not
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
  directory, archive or file into `import/`, run `docker exec vandoxd /vandoxd import /import/<name>`, what
  is read (input forms, nothing followed or extracted), the summary and the exit codes, re-import and
  resume, the limitation for files that grew since their import, and that the parsers arrive with #16–#20.
- `docs/ARCHITECTURE.md`: the status paragraph (the import framework exists, no parser yet); *Components* —
  `importer` under `cmd/vandoxd/internal/`, `logparse` under `internal/`, and `vandoxd import`; *Data flow* —
  one paragraph on the import path (origin `import`, never live, so never alerting, 0022); *Storage and
  retention* — schema version 3 with `import_files`, and that imports are made idempotent per file content,
  not per record; links to 0069–0072, and 0072 instead of 0058 where the command line is described.
- `.squad/project.md`: *Security areas* 9 and 10 name `cmd/vandoxd/internal/importer` (`scan`, `openRegular`,
  `eachEntry`, `sniffFormat`, the limits) and `internal/logparse` (`LineReader`) with records 0069–0071;
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
- **No following, no blocking** (areas 9, 10): see *Accepted input forms*. `openRegular` uses
  `O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC` and `Fstat` on the opened descriptor, so a swap between listing and
  opening cannot redirect the read or block on a FIFO.
- **Bounded memory** (area 10): streams only; one decompression layer; head of 4 KiB; line length 16 KiB
  (`LineReader`); batches of at most 2,000 records and 4 MiB of input; at most 20,000 files of at most
  1024-byte paths per run (the scan's list stays below ~50 MiB in the worst case, typical inputs far less);
  `archive/tar` bounds its special headers to 1 MiB. **Time** is not bounded (a gzip bomb or a huge sparse
  entry is read to its end); every read checks the context, so `Ctrl-C` or `docker stop` ends it, and the
  run resumes later.
- **Output injection** (area 12): the summary prints every path and reason with `%q`; progress goes through
  the JSON `slog` handler as attributes. Parser `Skip` reasons are fixed texts by contract and are quoted
  anyway. OS errors are reported without repeating the path.
- **Idempotency under concurrency**: the `Done` compare-and-set of `ImportStep` in the batch transaction makes
  two concurrent runs of the same content, or a run racing a resumed one, fail with `ErrImportConflict`
  instead of storing twice.
- **Integrity**: SHA-256 over the decompressed content; a content that changes between the passes fails and
  is not completed.
- No new dependency (`archive/tar`, `compress/gzip`, `crypto/sha256`, `syscall`).

## Decision records

- `docs/decisions/0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md` (Proposed)
- `docs/decisions/0070-log-parser-interface-and-explicit-registry-in-internal-logparse.md` (Proposed)
- `docs/decisions/0071-log-import-reads-input-without-following-links-or-extracting.md` (Proposed)
- `docs/decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md` (Proposed, supersedes 0058; the
  Lead sets 0058 to `Superseded by 0072` at approval)

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
