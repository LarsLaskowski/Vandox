# 0069: The log import is idempotent per file content: SHA-256 of the decompressed content, two passes, batches that resume by count

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Issue #15
- **Supersedes:** —

## Context

Issue #15 asks that importing a log archive be "safe and repeatable": "file content hashes are stored;
re-importing the same file does nothing", and "re-importing the same archive creates no duplicates". It also
asks for streaming processing, never loading a whole file into memory.

The store (0063) deduplicates only agent records, by (agent ID, sequence number); records of origin `import`
are deliberately not deduplicated, because log lines have no identity of their own — the same message can
legitimately appear twice in the same second. So idempotency has to come from tracking which files were
imported. Further forces:

- A rotated log appears under several names over time with the same content: `syslog.1` today,
  `syslog.2.gz` after the next rotation; a `.gz` file and its copy inside a `.tar.gz` have different bytes
  on disk but the same lines.
- A forensic import can take long (a journal export of months, gigabytes); it may be interrupted by Ctrl-C,
  `docker stop` or a database error. One transaction per file would hold SQLite's single write lock (0065)
  for minutes and starve the service, which may run at the same time; so a file is written in many batches,
  and an interruption leaves a partly imported file behind.
- The hash of a file is known only after reading it completely, but records must not be written before it
  is known whether the file was imported already. Input from a `.tar.gz` cannot be re-read without reading
  the archive again.
- Two imports may run at the same time (an operator starting the command twice).

## Options considered

1. **Hash over the bytes on disk** — simplest; but `syslog.1` and `syslog.2.gz` (and an entry of a `.tar.gz`)
   differ, so a rotated file is imported twice.
2. **Hash over the decompressed content** — the same lines give the same hash however they are packed.
3. **Deduplicate records** (a unique index over source, time and message) — no file tracking; but genuine
   repeated lines would be lost, the index costs on every write, and it contradicts 0063.
4. **One pass: parse and write while hashing, decide at the end** — a duplicate would be detected only after
   its records were written and would have to be deleted again, including their FTS5 entries (0063).
5. **Two passes: hash first, then import** — every recognized file is read twice (decompressed twice); a
   tar archive is read once to hash all its entries and once more to import them.
6. **Temporary copy of each entry** — read once from the archive, then twice from disk; writes untrusted
   content to disk (*Security areas* 9) and needs space for the largest file.

For an interrupted file:

- a. **One transaction per file** — no partial state; but the write lock is held for the whole file.
- b. **Delete the partial records and start again** — needs a link from every record to its file and deletion
  with FTS5 maintenance.
- c. **Resume by count** — the store keeps, per file, the number of records stored; each batch advances it in
  the same transaction as its records, as a compare-and-set on the expected count. A resumed import parses
  the file again and drops the first *n* records. Requires deterministic parsers (0070). The compare-and-set
  also makes two concurrent runs safe.

A parser is deterministic only for the same content **and** the same `logparse.File` (name and modification
time): #16 infers the year of syslog time stamps from them. The same content may come back under another name
or modification time (a renamed or recompressed rotation, a newer copy of `/var/log`). For a resumed content:

- α. **Resume with the current `File`** — simplest; but the resumed part may be parsed with another year than
  the stored part, mixing years within one file.
- β. **Resume only when name and modification time match, else fail** — safe; but a content interrupted
  before its first batch, or whose first input is gone, can never be completed (there is no way to delete an
  import state).
- γ. **Store the first import's `File` and always parse with it** — the resumed part is parsed exactly as the
  interrupted run parsed it; no dead end.

For a file that changes between the passes (a log still being written):

- A. **Read pass 2 to the end and fail on any hash difference** — a log that is merely appended to fails.
- B. **Read pass 2 only up to the size hashed in pass 1** — appended bytes are ignored and left to a later
  import; only a change or shrinking of the hashed part fails.

## Decision

Options 2, 5, c, γ and B. (α was rejected because it mixes parses within a file; β because it can leave a
content that can never be completed.)

- Schema version 3 (0064, step 3) adds
  `import_files(id INTEGER PRIMARY KEY, sha256 BLOB NOT NULL UNIQUE CHECK (length(sha256) = 32),
  size INTEGER NOT NULL CHECK (size >= 0), name TEXT NOT NULL,
  file_name BLOB NOT NULL CHECK (length(file_name) <= 1024), mod_time INTEGER, source_type TEXT NOT NULL,
  records INTEGER NOT NULL DEFAULT 0 CHECK (records >= 0), complete INTEGER NOT NULL DEFAULT 0 CHECK (complete IN (0, 1)),
  started_at INTEGER NOT NULL, completed_at INTEGER) STRICT`. `sha256` is the SHA-256 of the decompressed
  content; `name` (display path of the first import, invalid UTF-8 replaced, at most 1024 bytes) is
  informational; `file_name` (the `logparse.File.Name` of the first import, byte for byte) and `mod_time`
  (its `ModTime`; NULL when zero or outside the range storable as Unix nanoseconds) are the `File` every
  parse of this content receives; `source_type` is the parser's type; times are Unix nanoseconds as in 0063.
- `store.BeginImport` returns the state of a content hash, creating it (no records, not complete) when it is
  unknown; an existing state is returned unchanged. It refuses a `FileName` longer than 1024 bytes (the
  importer lists longer paths as failed before, 0071).
- `store.Batch` gets an optional `Import *ImportStep{FileID, Done, Complete}`. `WriteBatch` then requires
  origin `import` for every record and no agent ID, and in its transaction first runs
  `UPDATE import_files SET records = records + n, complete = …, completed_at = … WHERE id = ? AND records = Done
  AND complete = 0`; unless exactly one row changed, it returns `ErrImportConflict` and writes nothing. A batch
  without records is valid only to complete a file. The records go through the existing write path, so
  `WriteBatch` stays the only writer of `log_lines` and `log_fts` (0063).
- The importer (`cmd/vandoxd/internal/importer`) runs two passes. Pass 1 lists every file (stopping at the
  entry limit of 0071 as soon as it is exceeded), detects its parser
  and, for recognized files, reads the whole decompressed content to compute SHA-256 and size; a file that
  cannot be read completely (truncated or corrupt gzip, I/O error) fails here and is not imported at all.
  While a file is hashed, pass 1 reports progress every 64 MiB (0072).
  Pass 2 calls `BeginImport` per recognized file in input order: complete → "already imported"; stored with
  another source type → failed; otherwise it parses the content again **with the stored `File`**
  (`file_name`, `mod_time`), drops the first `Records` valid records, and writes the rest in batches with
  `ImportStep`, the last one `Complete`. It reads the decompressed content only up to the `size` hashed in
  pass 1 and hashes it again while parsing (draining what the parser did not read); if fewer bytes can be
  read or the hash differs from pass 1, the file fails and is not completed.
- Batches hold at most 2,000 records and the records from at most 4 MiB of input (0071), counted from the
  batch's first buffered record; the byte bound is checked only when a record is added, so dropped (resumed)
  records and skipped input never flush, and the only batch without records is the one that completes a file.

## Consequences

- Re-importing a directory or archive stores nothing new; a rotated file seen again under another name or
  compression is "already imported"; two identical files in one run are stored once; an interrupted import
  continues where it stopped, and two concurrent runs never store a file twice (the loser fails with
  `ErrImportConflict`).
- Every recognized file is read and decompressed twice. For a forensic, one-off import this is accepted.
- **A file that grew since its import is imported again as a whole** (another hash), storing the
  overlapping lines twice — e.g. `syslog` imported once and later, with more lines, as `syslog.1` in a newer
  copy of `/var/log`. Recognizing a known prefix is split into a follow-up issue.
- A file whose hashed part changes or shrinks while it is imported fails; the records written before the
  change stay stored, and the file is not completed, so it is imported again under its new hash later. Inputs
  are copies, so this is rare; the summary says so. The change is detected only when pass 2 has read the
  whole hashed size, so **the records written before that come from the changed content but stay counted
  under the original hash** (`import_files.records` of that content). Should the original content be
  imported later, it resumes after that count, dropping as many of its own records as the changed content
  produced — the stored records of that file then mix both contents. Detecting the change before writing
  would need the hash before parsing, i.e. a third pass or a temporary copy (options 5 and 6). Removing such
  records needs the record-to-file link below and falls under deleting or re-doing an import, which is out
  of scope. A file that is only appended to while it is imported is
  imported up to the size hashed in pass 1; the appended lines come with a later import of the grown file
  (and fall under the grown-file limit above). A line half-written at that size is imported as a last line
  without newline.
- An interrupted content resumed under another name or modification time is parsed with the first run's
  name and modification time; the records then carry what the first run would have produced (e.g. its year
  inference), not what the new name would suggest.
- A truncated or corrupt compressed file is not imported in part; the operator can decompress the readable
  part and import it as a plain file.
- Resuming relies on parsers being deterministic for the same content and `File` (0070). A parser change between an
  interrupted run and its resumption may shift the count; the stored `source_type` catches a changed
  detection, not a changed parser version.
- Records do not reference their import file; deleting or re-doing one import would need a migration that
  adds the link.
- A database at version 3 is refused by older builds (0064).
- `store.ImportTracker` (`BeginImport`) joins the repository interfaces of 0067; `storetest.Fake` scripts it
  like the others.
