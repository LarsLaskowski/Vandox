# Storage

## Scope

The backend's database: the file and how it is opened, the connections and how a running statement is interrupted, the schema with
its migrations, retention in outline and the write-throughput criterion. A database file written by one version of the backend must stay readable
by the next, whichever language implements it, so the schema below is part of the contract. How batches are written is in
[Storage writing](storage-writing.md), how stored data is read and searched in
[Storage reading and log exploration](storage-reading.md); what a record means is in the [wire format](wire-format.md), how the log
import tracks files on top of this store in [Log import](log-import.md).

## Database file and connections

- The backend keeps all data in one SQLite database, `vandox.db` in `storage.directory`, with FTS5 for log search. There is no external
  database. A backup is a copy of the file together with its `-wal` and `-shm` files.
- `storage.directory` must exist; the backend never creates it. The file is created exclusively with mode `0600` inside it. A symbolic
  link or any other entry that is not a regular file in place of `vandox.db`, `vandox.db-wal` or `vandox.db-shm` is refused, because
  SQLite would write next to the link target, outside `storage.directory`.
- The database runs in WAL mode with `foreign_keys` on and **`synchronous=FULL`**: a committed batch survives a power cut. The
  ingest endpoint acknowledges a batch only after its commit ([Ingest endpoint](ingest-endpoint.md)), and the agent then deletes it from
  its spool, so an acknowledged batch must not become a silent gap. The endpoint supplies the batch's receive time (one instant for every
  record of the batch), and its write is cancelled only by the service's shutdown deadline, never by the request.
- **One writer connection**, whose transactions start with `BEGIN IMMEDIATE` (the lock is taken at the start, so the busy timeout
  applies instead of a failure in the middle of a transaction), and up to four **query-only reader connections**. Readers never wait
  for the writer. The writer has a page cache of up to 16 MiB; readers keep the default.
- The busy timeout is **5 seconds** and bounds the **whole** wait for a lock: every connection sets `PRAGMA busy_timeout=5000` and the
  client library's own command timeout to the same 5 seconds, so the library does not retry `SQLITE_BUSY` on its own beyond it (about
  31 s were measured without it). It matters only against another process that opens the same file, such as `vandoxd import`,
  `vandoxd templates` or a backup; an ingest write that hits it fails and is answered `503`.
- Two processes may open the database at once.
- Every SQL statement is a constant or composed of constant fragments; every value from a request, batch, file or address is bound as a
  parameter, never written into the text. A helper that takes the statement text suppresses CA2100 at its one `CommandText`
  assignment with that reason.
- **A cancelled token or a passed deadline interrupts a running reader statement.** Every reader connection checks the cancellation of
  its call every 1,000 SQLite virtual-machine instructions (a progress handler), so it also stops the work SQLite does before the first
  row (a sort, an aggregate, the `MATCH` of a search, including the FTS5 module's reads of its index pages). The call then ends as
  cancelled, carrying that cancellation, with no partial result; every other SQLite error stays a store error with the message of its
  operation. A call that cannot be cancelled installs no check. The gap between two checks was at most 31 ms on the development host
  for every statement shape measured (a measurement, not a proof). The bound applies while the statement runs, not while it waits for a
  lock. The writer connection is never interrupted: a batch, an incident save, an import step and a migration run to their commit or
  rollback.

## Schema

The schema version is stored in `meta.schema_version`; the current version is **14**. Times are Unix nanoseconds in `INTEGER` columns.
All tables are `STRICT`.

| Table | Content |
| ----- | ------- |
| `meta` | key and value; the schema version, the run marker `template_rebuild` from the start of a template rebuild until one completes, and the retention state: `rollup_after_id` (the record ID up to which metric values are folded), `kill_scan_after_id` (the record ID up to which kernel events are scanned for OOM kills; both absent read as 0) and `sample_time_floor` (the capture time below which agent sample times were removed; absent for none) |
| `records` | one row per record: `id`, `kind`, `origin` (`agent`, `backend` or `import`), `source`, `agent_id`, `seq`, `captured_at`, `received_at`, `boot_id`, `clock_offset_ns`, `data` (the record's payload as the `data` object of the wire format, without the kind, which is the `kind` column, for every kind except metrics and log lines; `NULL` there), `live` (`CHECK (live IN (0, 1))`: 1 when the record was classified as live data when it was stored, 0 for backfilled data and for every record of origin `import` or `backend`; set in the insert, never changed) |
| `metrics` | typed rows for metric records: `record_id`, `source`, `name`, `captured_at`, `value`, `unit`, `labels` (JSON object or `NULL`) |
| `log_lines` | typed rows for log lines: `record_id`, `log`, `program`, `pid`, `priority`, `message`, `truncated`, `host`, `event` |
| `log_fts` | FTS5 index over `log_lines.message` (external content, tokenizer `unicode61 remove_diacritics 2`) |
| `import_files` | one row per imported file content: SHA-256 of the decompressed content (unique), size, display name, file name and modification time of the first import, source type, number of records stored, complete flag, start and completion times, the head key (`head_sha256`, SHA-256 of the first line; `NULL` for rows of versions before 6), the id of the content it continues (`continues_id`, no foreign key) and the continuation point in bytes (`start_offset`, 0 when it is not continued), the attempt (`attempt`, the number of times an incomplete content was begun; 0 for rows of versions before 7) and the attempt whose line hashes are kept (`hashes_attempt`, `NULL` when the content was completed without line hashes) |
| `import_line_hashes` | rows of line hashes of an imported content: `file_id`, `attempt`, `first_offset`, `last_offset` and `hashes` (a blob of 16-byte entries, 1 to 4,096, in ascending order of their line ends); the primary key `(file_id, attempt, last_offset)` is its index |
| `log_templates` | one row per log template: `id`, `source` (the record's source), `program`, `template` (the normalized message, at most 1,024 UTF-8 bytes), `first_seen` and `last_seen` (the earliest and latest capture time of its lines), `count` (its stored lines, at least 1); unique on `(source, program, template)` |
| `log_template_hours` | `template_id`, `hour` (the start of a UTC hour), `count` (the template's stored lines in that hour, at least 1); primary key `(template_id, hour)`, without row ID |
| `log_source_hours` | `source`, `hour`, `count` (the stored log lines of the source in that hour, at least 1); primary key `(source, hour)`, without row ID |
| `incidents` | one row per incident: `id` (`AUTOINCREMENT`, so a number that was merged away is never given again), `started_at` and `ended_at` (the window, `ended_at > started_at`), `first_kill_at`, `kill_count` (at least 1), `created_at`, `updated_at` and `data` (the incident as JSON, at most 8,388,608 UTF-8 bytes) |
| `incident_notes` | the notes of the incidents: `written_for` (the incident number the note was written for, the primary key; numbers are never reused), `incident_id` (the incident the note belongs to now, a foreign key to `incidents(id)` with no `ON DELETE` action, so a delete of an incident that still has notes fails), `text` (`CHECK (length(CAST(text AS BLOB)) BETWEEN 1 AND 5400)`: 1 to 5,400 UTF-8 bytes, the size of 1,800 UTF-16 characters at most, counted in bytes because SQLite's `length()` of a text stops at the first U+0000) and `updated_at` |
| `connection_marks` | derived rows, one per usable agent connection snapshot: `record_id` (the primary key, a foreign key to `records(id)`), `agent_id`, `seq`, `captured_at`, `listener_check` (`all`, `privileged`, `none` or `start`), `new_count` (`CHECK (new_count >= 0)`: the new listeners the agent reported, listed and omitted together; 0 unless the check is `all` or `privileged`) and `new_listeners` (the first 64 listed entries as a JSON array, `NULL` when `new_count` is 0), `changed_count` (`CHECK (changed_count >= 0)`: the changed listeners the agent reported, listed and omitted together; 0 unless the check is `all` or `privileged`) and `changed_listeners` (the first 64 listed entries as a JSON array with the owner's `command` emptied, `NULL` when `changed_count` is 0) |
| `listener_comparisons` | derived rows, one per usable agent snapshot with `listener_check` `start`: `record_id` (the start's record, the primary key, a foreign key to `records(id)`), `agent_id`, `seq`, `captured_at`, `base_record_id`, `base_seq` and `base_captured_at` (the agent's previous mark by `seq`, `NULL` when there is none), `outcome` (text: `no_earlier`, `not_comparable`, `privileged_incomplete`, `all_ports` or `privileged_only`), `new_count` (the listeners found new, listed and omitted together), `new_listeners` (the first 64 as a JSON array, `NULL` when `new_count` is 0), `changed_count` and `changed_listeners` (the changed endpoints of the comparison, as in `connection_marks`; a start that is not compared has none) and `finding` (`CHECK (finding IN (0, 1))`: 1 when `new_count` or `changed_count` is above 0 or the outcome says the start was not compared) |
| `agent_streams` | one row per agent: `agent_id` (primary key, without row ID), `floor_seq` (the lowest number the backend ever received from the agent; numbers below it are never missing), `max_seq` (the highest number stored), `max_captured_at` (its capture time), `fill_at` (the instant the last record arrived into an open sequence gap, a gap opened or the wait was restarted), `open_gaps` (the number of open sequence gaps, kept as a counter), `offset_ns` and `measured_at` (the measured clock offset and the receive time of the newest counted sample, `NULL` until measured), `clock_off` (1 while the offset exceeds 90 seconds), and `last_bound_ns`, `last_captured_at` and `last_received_at` (the previous counted reading: its bound, raw capture time and receive time, `NULL` until the agent has one and after the migration) |
| `agent_clock_samples` | the clock bounds of an agent: `agent_id`, `sort` (`counted` or `run`), `bucket` (the receive time floored to 10 seconds) and `bound_ns` (the largest bound of the bucket); primary key `(agent_id, sort, bucket)`, without row ID; at most 64 rows per agent and sort, the oldest dropped |
| `agent_sample_times` | one row per distinct capture time of an agent's stored `metric` records: `agent_id`, `captured_at`, `min_seq` and `max_seq` (the lowest and highest number of a metric at that time); primary key `(agent_id, captured_at)`, without row ID; the rows below the raw horizon are removed by retention except each agent's newest ([Retention and rollups](retention.md)) |
| `metric_rollups_5m` and `metric_rollups_1h` | the 5-minute and hourly buckets of a metric series: `source`, `name`, `labels` (the labels as stored, `''` for none), `bucket` (the start), `min_value`, `max_value`, `sum_value` (REAL), `count` (`CHECK (count >= 1)`), `last_value`, `last_at` and `last_record_id` (the latest value by capture time, ties by record ID); primary key `(source, name, labels, bucket)`, without row ID |
| `retention_holds` | the windows retention keeps for the agent's OOM kills: `id`, `started_at` and `ended_at` (`CHECK (ended_at > started_at)`); stored holds never overlap |
| `gaps` | the gaps of an agent: `id` (`AUTOINCREMENT`), `agent_id`, `kind` (`sequence` or `time`), `cause`, `state` (`open`, `closed` or `final`), `first_seq` and `last_seq` (a sequence gap's missing numbers, `CHECK (first_seq <= last_seq)`, `NULL` for a time gap), `from_seq` and `to_seq` (a time gap's bounding samples' highest and lowest metric numbers, `NULL` for a sequence gap), `from_at` and `to_at` (the span in the agent's raw capture times; for a sequence gap the capture time of the highest record before it and of the record after it), `detected_at` and `changed_at` |

Indexes: a unique index on `records(agent_id, seq)` for origin `agent`; `records(kind, source, captured_at)`;
`records(kind, captured_at)`; `metrics(source, name, captured_at)`; `import_files(head_sha256, source_type, size)` for complete rows
with a head key (`import_files_head`); `log_templates(source, first_seen)` (`log_templates_source_first_seen`); `log_template_hours(hour)`
(`log_template_hours_hour`); `incidents(started_at)` (`incidents_started_at`); `incident_notes(incident_id, written_for)`
(`incident_notes_incident`); `connection_marks(agent_id, seq)`, unique (`connection_marks_agent_seq`, which serves the lookup of an
agent's previous and next mark by `seq`); `connection_marks(captured_at DESC, agent_id, seq DESC)` where `new_count > 0`
(`connection_marks_findings`); `connection_marks(captured_at DESC, agent_id, seq DESC)` where `new_count = 0 AND changed_count > 0`
(`connection_marks_changes`, the findings with changes only); `listener_comparisons(captured_at DESC, agent_id, seq DESC)` where
`finding = 1` (`listener_comparisons_findings`); `gaps(agent_id, first_seq)` where `kind = 'sequence'` and `state <> 'closed'`
(`gaps_seq`) and, apart, where `state = 'open'` (`gaps_seq_open`); `gaps(agent_id, from_at)`, unique, where `kind = 'time'` and
`state <> 'closed'` (`gaps_time`); `gaps(agent_id, id)` (`gaps_agent`, the pages of the gap reader); `metric_rollups_5m(bucket)` and `metric_rollups_1h(bucket)`; `retention_holds(started_at)`; `records(kind, captured_at)` where `origin <> 'import'` (`records_removable`, the walk of a removal step); `gaps(to_at)` (`gaps_to_at`).

- `agent_id`, `seq`, `boot_id` and `clock_offset_ns` are `NULL` when absent (`seq` for any origin other than `agent`). `source` and
  `captured_at` of a metric are copied from `records`, so one index serves a time series in order.
- The log exploration relies on `records(kind, source, captured_at)` and `records(kind, captured_at)` serving the order of its scan; a
  statement plan that stopped using them would turn a page into a sort of every log line of the range
  ([Storage reading and log exploration](storage-reading.md), *Log exploration*).

### Migration steps

What each step after the first adds, and how data stored before it reads:

- **4** adds `log_lines.host`, `TEXT NOT NULL DEFAULT ''`: lines stored before it have an empty host.
- **5** adds `log_lines.event`, `TEXT NOT NULL DEFAULT ''`: lines stored before it have an empty event.
- **6** adds `import_files.head_sha256`, `continues_id` and `start_offset` and the index `import_files_head`.
- **7** adds `import_files.attempt`, `INTEGER NOT NULL DEFAULT 0`, `import_files.hashes_attempt`, `INTEGER`, and the table
  `import_line_hashes`: existing rows get attempt 0 and no line hashes.
- **8** adds `log_templates`, `log_template_hours` and `log_source_hours` with their indexes, empty: log lines stored before it have no
  template statistics until `vandoxd templates` rebuilds them.
- **9** adds `incidents` with its index, empty; **10** adds `incident_notes` with its index, empty.
- **11** adds `connection_marks` and `listener_comparisons` with their indexes, empty: agent connection snapshots stored before it have
  no rows there.
- **12** adds `records.live`, `INTEGER NOT NULL DEFAULT 0 CHECK (live IN (0, 1))`, so every record stored before it reads as backfilled,
  and the tables `agent_streams`, `agent_clock_samples`, `agent_sample_times` and `gaps` with their indexes. `agent_streams` is seeded
  with one row per agent that has stored records, holding the highest stored number as `floor_seq` and `max_seq` and that record's
  capture and receive time, so no number below it is ever reported missing; the other three tables start empty.
- **13** adds to `connection_marks` and `listener_comparisons` the columns `changed_count` (`INTEGER NOT NULL DEFAULT 0 CHECK
  (changed_count >= 0)`) and `changed_listeners` (`TEXT`, `NULL` when none) and the index `connection_marks_changes`. Rows stored before
  it read as no changes (0 and `NULL`), and a start compared before it keeps its stored outcome without changes until it is compared
  again; stored comparisons are not recomputed when the comparison rules change.

- **14** adds `metric_rollups_5m`, `metric_rollups_1h` and `retention_holds` with their indexes and the indexes `records_removable` and
  `gaps_to_at`, all empty. The metric values stored before it are folded into the rollups by the retention job before it removes
  anything, because `rollup_after_id` is absent (0) ([Retention and rollups](retention.md)).

### Migrations

- Migrations are an ordered list of steps. A step runs in its own write transaction that first re-reads the version, skips itself if
  the database is already at or past it, and otherwise executes and writes the new version in the same transaction; a failing step
  rolls back completely and earlier steps stay committed. Two processes that start at once apply each step once.
- A missing `meta` table or version row is version 0. A version above the last known step, or one that is not a decimal integer, is
  refused before anything is written: an older build refuses a newer schema.
- A released step is never edited: every schema change is a new step, tested by migrating from the previous version.

## Retention

The backend removes expired data and keeps rollups of the raw metric values in a background job. The periods, what outlives them, the
steps and the log lines are in [Retention and rollups](retention.md). In the file: retention deletes rows in bounded steps and never
vacuums, so the file does not shrink and freed pages are reused; the stream rows of agents and the clock samples are never removed.

## Write throughput

The reference host is a Synology DS918+ (Intel Celeron J3455). The criterion: the **median of five batches** of 10,000 records, one
transaction each, takes under one second, measured with the storage benchmark on the volume that holds the database; the first batch
(fresh file) is reported next to it. The criterion is a stress value, since an agent sends hundreds of records per minute. The agent
timeline and the listener marks of [Storage writing](storage-writing.md) add their work to the same transaction. The criterion is met
on the SSD volume (median 906 to 936 ms in three runs with the agent timeline in the write path, 2026-11-08); on the HDD volume it is not (1,171 ms), because every commit waits for the disk, so the database
should be on the SSD, though any volume works. Results and the procedure are in `docs/BENCHMARKS.md` (*Storage write throughput*). The
criterion is not weakened silently, and `synchronous=FULL` stays unless the Product Manager decides otherwise in a new record.

## Tests

The real store is tested against real files. A consumer whose behavior depends on the store's semantics (such as "a resent batch is
stored once") is tested against the real store; fakes for other consumers only record calls and return what the test scripts.

## Related decisions

- [0007](../decisions/0007-sqlite-with-fts5-no-external-database.md) — why one SQLite file with FTS5 and no external database.
- [0063](../decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md) — why a `records` table plus typed metric and log tables with JSON payloads.
- [0077](../decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) — why the same schema on Microsoft.Data.Sqlite, one writer, `synchronous=FULL` and migrations in code.
- [0083](../decisions/0083-storage-write-criterion-is-the-median-of-five-batches-on-the-data-volume.md) — why the criterion is the median of five batches on the data volume.
- [0097](../decisions/0097-reader-statements-interrupted-by-a-progress-handler-bound-to-the-token.md) — why a progress handler interrupts reader statements and why the interruption is a cancellation.
- [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored.md) — why the tables of listener marks and comparisons exist.
- [0179](../decisions/0179-ingest-writes-end-only-at-the-shutdown-deadline-and-are-drained-before-the-store-closes.md) — why the connections
  set the library's command timeout to the busy timeout and why the ingest write is cancelled only by the shutdown deadline.
- [0130](../decisions/0130-live-or-backfilled-is-decided-at-write-time.md) — why the `live` column exists.
- [0180](../decisions/0180-sequence-and-time-gaps-are-stateful-rows-that-become-final-after-a-quiet-wait.md) — why the gap rows and the
  stream rows exist.
- [0181](../decisions/0181-the-agents-clock-offset-is-measured-from-caught-up-self-samples.md) — why the stream row and the clock samples
  hold the measured clock offset.
- [0208](../decisions/0208-metric-rollups-are-folded-from-new-rows-by-record-id-never-recomputed-from-raw.md) to
  [0213](../decisions/0213-agent-sample-times-follow-the-raw-period-below-a-floor-that-opens-no-time-gap.md) — why the rollup tables, the
  removal by period, the holds, the resend rule, the unshrunk file and the sample-time floor exist.
- [0204](../decisions/0204-sql-command-text-meets-ca2100-through-justified-suppressions-at-the-command-helpers.md) — why a helper that takes
  the statement text suppresses CA2100 with a reason instead of the callers assigning it.

## Not here

- How batches, incidents, notes and import state are written, deduplicated and counted: [Storage writing](storage-writing.md).
- How records, incidents, listener findings and gaps are read, and log search and exploration:
  [Storage reading and log exploration](storage-reading.md).
- The meaning of the records and the batch format: [Wire format](wire-format.md), [Wire format records](wire-format-records.md).
- How import files are found, hashed and resumed: [Log import](log-import.md).
- The container's volumes and memory limit: [Backend host](backend-host.md).

## Implementation

`Vandox.Storage`: `SqliteStore` (opening, connections), `DatabaseFile` (file creation and the refusal of links), `SchemaMigrator`
(steps), `RollupFolder` (the fold step), `RetentionRemover` (the removal step), `ReaderQuery` (the progress handler on reader connections), `StorageLimits`; the connections set Microsoft.Data.Sqlite's
`DefaultTimeout` next to the busy timeout. The throughput benchmark is `tools/Vandox.StorageBenchmark`.
