# 0069: The log import is idempotent per file content: SHA-256 of the decompressed content, two passes, batches that resume by count, a grown file continued after its imported start

- **Status:** Proposed
- **Date:** 2026-10-11
- **Area:** Log import
- **Source:** Issue #15, issue #131
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
- A log grows between two copies of `/var/log`: `syslog` imported once comes back later with the same bytes at its
  start and more lines after them, as `syslog` or rotated as `syslog.1` (issue #131). Its hash differs, so without a
  further rule it is imported as a whole and the earlier lines are stored twice. The copy may end in the middle of a
  line (a write caught by the copy).
- The parsers group lines (MariaDB lines of the journal and syslog per host, program and pid; kernel reports; MariaDB
  error log entries with their continuation lines) and emit a grouped entry only when it ends, while lines of other
  programs are emitted at once (0086, 0088). So the first *n* records of a longer content are not the *n* records of
  its start parsed alone.

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

For a file that grew since its import (issue #131):

- G1. **Import it as a whole** (the state before #131) — simplest; stores the earlier lines twice.
- G2. **Parse the whole content and drop as many records as the earlier import stored**, like a resume — no new
  parser rule; but wrong with grouping parsers: an entry open at the end of the shorter content is emitted after
  later lines of other programs in the longer one, so dropping by count drops new lines and stores an old entry
  again.
- G3. **Parse only the part after the earlier content, on its own, starting at the beginning of the line in which
  the earlier content ends** — no record is stored twice except a line the earlier copy cut, which is stored again
  complete; an entry that spans the boundary is split: the part after it becomes lines of their own or, where a
  parser cannot place a line without its entry, a skipped line that the summary reports.

How the earlier content is found while the longer one is hashed in pass 1 (the prefix hash is known only at the
earlier content's size, and that size must be known before the read passes it):

- P1. **Take a snapshot of the hash at the size of every completely imported content** — exact; but the number of
  sizes grows with every import, for every file.
- P2. **Look up candidates by a key from the head of the content** (the hash of the first line, or of the first
  4,096 bytes when they hold no line feed), stored with every content, and snapshot the hash only at their sizes,
  at most 64 per file — bounded; a content imported before the key existed is never found.
- P3. **Store prefix hashes at fixed intervals** — finds a prefix only at an interval boundary, so most growths are
  missed.

The earlier content can also be a file of the same run, earlier in input order (two copies of `/var/log` in one
input). And the older copy may come after the newer one (in a later run or later in the input); recognizing that a
content is the start of one imported before would need prefix hashes of every stored content at arbitrary sizes.

## Decision

Options 2, 5, c, γ and B (α was rejected because it mixes parses within a file; β because it can leave a content
that can never be completed). A file is identified by the SHA-256 of its decompressed content; the import reads
every recognized file twice, hashing first and importing second; a file is written in bounded batches, and the store
counts the records per file in the same transaction as each batch, as a compare-and-set, so an interrupted import
resumes and two concurrent imports cannot store a file twice. A resumed content is parsed with the file name and
modification time of its first import, and the second pass reads only up to the size hashed in the first. A file
whose content starts with a completely imported content of the same source type (an earlier run, or an earlier file
of the same run) is continued (G3, P2): only the part from the beginning of the line in which the longest such
content ends is parsed and stored, the whole content is still hashed in both passes, and the continuation point is
stored with the content's state, so a resume uses the same one. The new part is parsed with the file name and
modification time of the continued content's own first import (γ applied to that content), not those of the earlier
content: the year of its year-less times is anchored on the file it comes from. The rules and their limits are in
[Log import](../areas/log-import.md), *Repeatable import*; the table that tracks the files is part of schema
version 3 (0077), the head key and the continuation point of version 6 ([Storage](../areas/storage.md)).

## Consequences

- Re-importing a directory or archive stores nothing new, and an interrupted import continues where it stopped.
- Every recognized file is read and decompressed twice. For a forensic, one-off import this is accepted.
- A file that grew since its import stores only its new lines; the summary lists it as continued, naming the
  earlier import and the line after which it continued. Every recognized file costs one more indexed query in pass 1
  and up to 64 hash snapshots.
- Only the newer-after-older order is recognized. An older copy imported after a newer one (in a later run, or later
  in the same input) is imported as a whole, storing its lines twice; so are contents imported before schema
  version 6 (they have no head key) and an earlier content that ends without any line feed. A follow-up issue covers
  the older-after-newer order.
- At the continuation point a grouped entry is split: a MariaDB entry of the error log loses the continuation lines
  after the point (reported as skipped lines "line before the first entry"), and a MariaDB entry or kernel report of
  the journal or syslog is split into the stored entry and lines of their own; the summary counts the lines skipped
  before the first record of the new part with the continued file. A line the earlier copy cut is stored again,
  complete — the one case in which a line has two records (the cut one and the complete one); skipping it would lose
  its rest, and the cut record cannot be replaced because records do not reference their import file.
- A content whose records are removed later (retention, once it exists) keeps its complete state: re-importing it
  stores nothing, and a longer content is continued after it, so the removed part is never restored. This is the
  same as for re-importing the unchanged content.
- A file that changes while it is imported is detected only when the second pass has read the whole hashed size,
  so the records written before that stay counted under the original hash and a later import of the original
  content resumes after that count. Detecting the change earlier would need a third pass or a temporary copy
  (options 5 and 6). Inputs are copies, so this is rare; the summary says so.
- Resuming relies on parsers being deterministic for the same content and file (0079); the stored source type
  catches a changed detection, not a changed parser version.
- Records do not reference their import file; deleting or re-doing one import would need a migration that adds the
  link.
- A database at version 3 is refused by older builds (0077).
