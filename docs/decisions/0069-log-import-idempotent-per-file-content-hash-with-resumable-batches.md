# 0069: The log import is idempotent per file content: SHA-256 of the decompressed content, two passes, batches that resume by count

- **Status:** Proposed
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

## Decision

Options 2, 5 and c.

- Schema version 3 (0064, step 3) adds
  `import_files(id INTEGER PRIMARY KEY, sha256 BLOB NOT NULL UNIQUE CHECK (length(sha256) = 32),
  size INTEGER NOT NULL CHECK (size >= 0), name TEXT NOT NULL, source_type TEXT NOT NULL,
  records INTEGER NOT NULL DEFAULT 0 CHECK (records >= 0), complete INTEGER NOT NULL DEFAULT 0 CHECK (complete IN (0, 1)),
  started_at INTEGER NOT NULL, completed_at INTEGER) STRICT`. `sha256` is the SHA-256 of the decompressed
  content; `name` (display path of the first import, invalid UTF-8 replaced, at most 1024 bytes) and
  `source_type` (the parser's type) are informational; times are Unix nanoseconds as in 0063.
- `store.BeginImport` returns the state of a content hash, creating it (no records, not complete) when it is
  unknown; an existing state is returned unchanged.
- `store.Batch` gets an optional `Import *ImportStep{FileID, Done, Complete}`. `WriteBatch` then requires
  origin `import` for every record and no agent ID, and in its transaction first runs
  `UPDATE import_files SET records = records + n, complete = …, completed_at = … WHERE id = ? AND records = Done
  AND complete = 0`; unless exactly one row changed, it returns `ErrImportConflict` and writes nothing. A batch
  without records is valid only to complete a file. The records go through the existing write path, so
  `WriteBatch` stays the only writer of `log_lines` and `log_fts` (0063).
- The importer (`cmd/vandoxd/internal/importer`) runs two passes. Pass 1 lists every file, detects its parser
  and, for recognized files, reads the whole decompressed content to compute SHA-256 and size; a file that
  cannot be read completely (truncated or corrupt gzip, I/O error) fails here and is not imported at all.
  Pass 2 calls `BeginImport` per recognized file in input order: complete → "already imported"; stored with
  another source type → failed; otherwise it parses the content again, drops the first `Records` valid
  records, and writes the rest in batches with `ImportStep`, the last one `Complete`. It hashes the content
  again while parsing (draining what the parser did not read); if the hash differs from pass 1, the file
  fails and is not completed.
- Batches hold at most 2,000 records and the records from at most 4 MiB of input (0071).

## Consequences

- Re-importing a directory or archive stores nothing new; a rotated file seen again under another name or
  compression is "already imported"; two identical files in one run are stored once; an interrupted import
  continues where it stopped, and two concurrent runs never store a file twice (the loser fails with
  `ErrImportConflict`).
- Every recognized file is read and decompressed twice. For a forensic, one-off import this is accepted.
- **A file that grew since its import is imported again as a whole** (another hash), storing the
  overlapping lines twice — e.g. `syslog` imported once and later, with more lines, as `syslog.1` in a newer
  copy of `/var/log`. Recognizing a known prefix is split into a follow-up issue.
- A file that changes while it is imported fails; the records written before the change stay stored, and the
  file is not completed, so it is imported again under its new hash later. Inputs are copies, so this is
  rare; the summary says so.
- A truncated or corrupt compressed file is not imported in part; the operator can decompress the readable
  part and import it as a plain file.
- Resuming relies on parsers being deterministic for the same content (0070). A parser change between an
  interrupted run and its resumption may shift the count; the stored `source_type` catches a changed
  detection, not a changed parser version.
- Records do not reference their import file; deleting or re-doing one import would need a migration that
  adds the link.
- A database at version 3 is refused by older builds (0064).
- `store.ImportTracker` (`BeginImport`) joins the repository interfaces of 0067; `storetest.Fake` scripts it
  like the others.
