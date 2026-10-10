# 0069: The log import is idempotent per file content: SHA-256 of the decompressed content, two passes, batches that resume by count

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Log import
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
  `docker stop` or a database error. One transaction per file would hold SQLite's single write lock (0077)
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
  the file again and drops the first *n* records. Requires deterministic parsers (0079). The compare-and-set
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

Options 2, 5, c, γ and B (α was rejected because it mixes parses within a file; β because it can leave a content
that can never be completed). A file is identified by the SHA-256 of its decompressed content; the import reads
every recognized file twice, hashing first and importing second; a file is written in bounded batches, and the store
counts the records per file in the same transaction as each batch, as a compare-and-set, so an interrupted import
resumes and two concurrent imports cannot store a file twice. A resumed content is parsed with the file name and
modification time of its first import, and the second pass reads only up to the size hashed in the first. The
rules and their limits are in [Log import](../areas/log-import.md), *Repeatable import*; the table that tracks
the files is part of schema version 3 (0077).

## Consequences

- Re-importing a directory or archive stores nothing new, and an interrupted import continues where it stopped.
- Every recognized file is read and decompressed twice. For a forensic, one-off import this is accepted.
- **A file that grew since its import is imported again as a whole** (another hash), storing the overlapping
  lines twice. Recognizing a known prefix is split into a follow-up issue.
- A file that changes while it is imported is detected only when the second pass has read the whole hashed size,
  so the records written before that stay counted under the original hash and a later import of the original
  content resumes after that count. Detecting the change earlier would need a third pass or a temporary copy
  (options 5 and 6). Inputs are copies, so this is rare; the summary says so.
- Resuming relies on parsers being deterministic for the same content and file (0079); the stored source type
  catches a changed detection, not a changed parser version.
- Records do not reference their import file; deleting or re-doing one import would need a migration that adds the
  link.
- A database at version 3 is refused by older builds (0077).
