# Storage

## Scope

The backend's database: the file and how it is opened, the schema with its migrations, how batches of records are written, how
they are read and searched, and the write-throughput criterion. A database file written by one version of the backend must stay
readable by the next, whichever language implements it, so the schema below is part of the contract. What a record means is in the
[wire format](wire-format.md); how the log import tracks files on top of this store is in [Log import](log-import.md).

## Database file and connections

- The backend keeps all data in one SQLite database, `vandox.db` in `storage.directory`, with FTS5 for log search. There is no external
  database. A backup is a copy of the file (together with its `-wal` and `-shm` files).
- The file is created exclusively with mode `0600`. A symbolic link or any other entry that is not a regular file in place of
  `vandox.db`, `vandox.db-wal` or `vandox.db-shm` is refused, because SQLite would write next to the link target, outside
  `storage.directory`.
- The database runs in WAL mode with `foreign_keys` on and **`synchronous=FULL`**: a committed batch survives a power cut. The
  ingest path acknowledges a batch only after its commit, and the agent then deletes it from its spool, so an acknowledged batch must
  not become a silent gap.
- **One writer connection**, whose transactions start with `BEGIN IMMEDIATE` (the lock is taken at the start, so the busy timeout
  applies instead of a failure in the middle of a transaction), and up to four **query-only reader connections**. Readers never wait
  for the writer. The busy timeout is 5 seconds; it matters only against another process that opens the same file, such as
  `vandoxd import` or a backup. The writer has a page cache of up to 16 MiB; readers keep the default.
- Two processes may open the database at once.

## Schema

The schema version is stored in `meta.schema_version`; the current version is 4 (step 4 adds `log_lines.host`, `TEXT NOT NULL DEFAULT ''`, so lines stored before it get an empty host). Times are Unix nanoseconds in `INTEGER` columns.
All tables are `STRICT`.

| Table | Content |
| ----- | ------- |
| `meta` | key and value; the schema version |
| `records` | one row per record: `id`, `kind`, `origin` (`agent`, `backend` or `import`), `source`, `agent_id`, `seq`, `captured_at`, `received_at`, `boot_id`, `clock_offset_ns`, `data` (the record's JSON for every kind except metrics and log lines; `NULL` there) |
| `metrics` | typed rows for metric records: `record_id`, `source`, `name`, `captured_at`, `value`, `unit`, `labels` (JSON object or `NULL`) |
| `log_lines` | typed rows for log lines: `record_id`, `log`, `program`, `pid`, `priority`, `message`, `truncated`, `host` |
| `log_fts` | FTS5 index over `log_lines.message` (external content, tokenizer `unicode61 remove_diacritics 2`) |
| `import_files` | one row per imported file content: SHA-256 of the decompressed content (unique), size, display name, file name and modification time of the first import, source type, number of records stored, complete flag, start and completion times |

Indexes: a unique index on `records(agent_id, seq)` for origin `agent`; `records(kind, source, captured_at)`;
`records(kind, captured_at)`; `metrics(source, name, captured_at)`.

- `agent_id`, `seq`, `boot_id` and `clock_offset_ns` are `NULL` when absent (`seq` for any origin other than `agent`). `source` and
  `captured_at` of a metric are copied from `records`, so one index serves a time series in order.
- **Migrations** are an ordered list of steps in code. A step runs in its own write transaction that first re-reads the version,
  skips itself if the database is already at or past it, and otherwise executes and writes the new version in the same transaction;
  a failing step rolls back completely and earlier steps stay committed. A missing `meta` table or version row is version 0. A
  version above the last known step, or one that is not a decimal integer, is refused before anything is written. A released step is
  never edited: every schema change is a new step, tested by migrating from the previous version. Two processes that start at once
  apply each step once. An older build refuses a newer schema.

## Writing

- A **batch** is written in one transaction. A batch holds at most 20,000 records and at least one, except the batch that completes an
  import file. The batch has a required receive time in UTC and, for records of origin `agent`, an agent ID that follows the agent ID
  rule of the wire format; the boot ID is stored as given.
- Every record is validated before anything is written. A record is refused with a message that names the record's index and the rule
  when its capture time is outside 1677-09-21 to 2262-04-11 (the range of a signed 64-bit nanosecond count), its sequence number is
  above 2^63 − 1, or it is of origin `agent` in a batch without an agent ID. An import batch has no agent ID and every record has
  origin `import`. Invalid UTF-8 in strings that are stored as JSON (payloads, metric labels) is replaced by U+FFFD; log fields
  and the other metric fields are stored byte for byte. A record from an agent never contains invalid UTF-8, because the wire format
  replaces it on decode.
- **Deduplication:** a record of origin `agent` is identified by (agent ID, sequence number) across all kinds. It is inserted with
  `ON CONFLICT DO NOTHING` and counted as a duplicate; a stored record is never replaced or updated. Records of origin `import` are not
  deduplicated, because log lines have no identity of their own; the import is made idempotent per file instead (*Log import*).
- **Log lines:** the write path inserts the message into `log_fts` right after each `log_lines` row, in the same transaction, with no
  trigger. Invariant: every `log_lines` row has exactly one index entry with its message, written in the transaction that wrote the row.
  The write path of a batch is the only code that inserts into `log_lines`, so a log line is searchable as soon as its batch is
  committed. Any code that writes or deletes `log_lines` (a repair tool, retention) must maintain `log_fts` in the same transaction.
- **Import batches** carry the import state: in the same transaction as its records, a batch advances the file's `records` counter with
  a compare-and-set on the expected count (and sets the complete flag with the last batch). If the counter does not match, nothing is
  written and the caller gets a conflict.

## Reading

- A series is read by kind, source and name for a time range, in time order; a kind-wide time range uses the kind indexes. A query
  returns at most 10,000 records.
- Callers depend on small interfaces (record writer, record reader, log searcher, import tracker). The real store is tested against
  real files; a consumer whose behavior depends on the store's semantics (such as "a resent batch is stored once") is tested against
  the real store. Fakes for other consumers only record calls and return what the test scripts.

### Log search

A search takes literal terms only; the text is never passed to FTS5 as a query.

- The text must be valid UTF-8 of at most 1,024 bytes, without a control or format character other than white space. It is split at
  white space into 1 to 16 terms, and every term needs at least one letter or decimal digit.
- Each term is quoted as an FTS5 string (a `"` inside doubles) and the terms are joined with a space, so all terms must match
  and no FTS5 operator, prefix (`*`), column filter or `NEAR` has any effect. The expression is bound as a parameter, never
  concatenated into SQL.
- Matching is case-insensitive and ignores diacritics; a term with punctuation (`anon-rss`) matches the tokens in order as a phrase.
  `ß` is not folded to `ss`. A term that consists only of numbers that are not decimal digits (`½`, `Ⅻ`) or of private-use characters is
  refused although the tokenizer would index it.
- A violated rule is an error that names the rule and never contains the search text.
- The cost of a search is not bounded by the time range or the limit: SQLite runs the `MATCH` over the whole retention first and filters
  by time and source afterwards. The bound is time: the search honors its cancellation token, so every caller that serves a user must
  pass a deadline.

## Retention (planned)

Raw data is kept 30 days, 5-minute rollups 1 year, hourly rollups 3 years, incidents unlimited, logs 90 days, process and connection
snapshots 30 days. Retention and rollups are the backend's own job and **are not implemented yet**; deleting log lines must also remove
their `log_fts` entries (the FTS5 `delete` command with the old message).

## Write throughput

The reference host is a Synology DS918+ (Intel Celeron J3455). The criterion: the **median of five batches** of 10,000 records, one
transaction each, takes under one second, measured with `tools/Vandox.StorageBenchmark` on the volume that holds the database; the
first batch (fresh file) is reported next to it. The criterion is a stress value, since an agent sends hundreds of records per minute.
It is met on the SSD volume (783 ms median); on the HDD volume it is not (1,171 ms), because every commit waits for the disk, so the
database should be on the SSD, though any volume works. Results and the procedure are in `docs/BENCHMARKS.md`. The criterion is not
weakened silently, and `synchronous=FULL` stays unless the Product Manager decides otherwise in a new record.

## Related decisions

- [0007](../decisions/0007-sqlite-with-fts5-no-external-database.md) — why one SQLite file with FTS5 and no external database.
- [0063](../decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md) — why a `records` table plus typed metric and log tables, and why the write path fills the FTS5 index.
- [0066](../decisions/0066-log-search-takes-literal-terms-only.md) — why log search takes literal terms only.
- [0077](../decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) — why the same schema on Microsoft.Data.Sqlite, one writer, `synchronous=FULL` and migrations in code.
- [0083](../decisions/0083-storage-write-criterion-is-the-median-of-five-batches-on-the-data-volume.md) — why the writer was made cheaper and why the criterion is the median of five batches.

## Not here

- The meaning of the records and the batch format: [Wire format](wire-format.md).
- How import files are found, hashed and resumed: [Log import](log-import.md).
- The container's volumes and memory limit: the ingest and backend host area.

## Implementation

`Vandox.Storage` (`SqliteStore`, `SchemaMigrator`, `BatchWriter`, `BatchValidator`, `FtsQuery`, `StorageLimits`).
