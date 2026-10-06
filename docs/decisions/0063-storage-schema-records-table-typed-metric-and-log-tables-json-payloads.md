# 0063: Storage schema: one records table holds every record's identity, metrics and log lines get own tables, other payloads are stored as JSON

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Issue #14
- **Supersedes:** —

## Context

Issue #14 asks for a schema for all record types of the shared data model (`internal/model`: metric,
process snapshot, connection snapshot, service state, MariaDB status, kernel event, log line, gap), indexes
for time-range queries per source and metric, and FTS5 full-text search for logs (0007). Until now the
database holds only the `meta` table of #13.

Forces:

- 0045: the backend stores each (agent ID, sequence number) once, and a resent record never overwrites stored
  data (*Security areas* 1 in `.squad/project.md`). The sequence counter is per agent across all record kinds,
  so uniqueness must be enforced over all kinds at once.
- #40 stores the receive time of every record; 0046 makes the batch's boot ID and clock offset the capture
  context of every record in it; #41 later classifies records as live or backfilled from these values.
- Metrics are the bulk of the agent's data and are read as time series per source and name (#47). Log lines
  are the bulk of the forensics import (#15) and are searched in full text (#26).
- Snapshots (processes, connections, MariaDB threads) are nested lists of up to 4096 entries; their views
  (#50–#52) are not designed yet.
- SQLite's `INTEGER` is a signed 64-bit integer; the model's sequence numbers are `uint64` and its times have
  nanosecond precision.
- #14's performance criterion: 10,000 records in one transaction in under one second on the DS918+ (0068).
  For log lines the FTS5 index is most of the cost, so how the index is filled decides whether the criterion
  can be met.

## Options considered

1. **One table per kind, fully normalized** (a row per process sample, per connection, per MariaDB thread) —
   every field queryable by SQL; but uniqueness of (agent ID, seq) across kinds needs a shared table anyway,
   snapshots multiply into hundreds of rows per sample, and the column design of views that do not exist yet
   is guessed now.
2. **One generic table with the payload as JSON for every kind** — simplest; but a metric time series and the
   FTS5 index would have to read through JSON, and the log message would be stored twice (JSON and FTS
   content).
3. **Hybrid** — a `records` table with the identity and capture context of every record (and the unique
   index), typed tables for the two kinds that are queried by column today (`metrics`, `log_lines` with an
   FTS5 index), and the payload of every other kind as the model's JSON in `records.data`.

How the external-content FTS5 index of the log lines is filled (measured on 2026-10-06 on a 2.1 GHz Xeon,
4 vCPU, `modernc.org/sqlite` v1.60.1, schema below, writer settings of 0065, 10,000 kernel OOM-kill lines of
about 150 bytes per transaction, 5 to 20 successive batches into the same database):

- a. **`AFTER INSERT` trigger on `log_lines`** — the usual pattern from the FTS5 documentation; 0.50 to 0.79 s
  per batch (the Devil's Advocate measured 0.53 to 0.64 s), growing with the index. Without FTS5 the same
  batch takes 0.10 to 0.12 s. `detail=column`, `detail=none`, the `ascii` tokenizer, `automerge=0` and a
  64 MiB page cache did not change this noticeably. At the J3455's lower speed per core this probably
  exceeds 1 s.
- b. **Explicit `INSERT INTO log_fts(rowid, message)` by the store's write path, per log line, in the same
  transaction** — 0.18 to 0.33 s per batch over 20 batches (200,000 lines); FTS5 `integrity-check` passes and
  every line is found. Same atomicity as the trigger; the store must keep the invariant itself.
- c. **Index after the commit, in a separate transaction** (a background indexer with a watermark) — the
  write transaction drops to about 0.11 s, but a committed line is not searchable until the indexer has run,
  the store needs a persisted backlog and recovery at start-up, and retention (#46) must not delete lines that
  are not indexed yet (an external-content `delete` of an unindexed row corrupts the index). It changes the
  behavior "searchable when the batch is committed".

Invalid UTF-8 in strings that the store writes as JSON (payloads, metric labels):

- i. **Replace it with U+FFFD on write** (what `encoding/json` does) — consistent with the wire format, which
  accepts such strings and replaces the bytes on decode (0042), so it never happens to a record from an agent.
- ii. **Reject the record in the store's validation** — would refuse in Go what the wire format accepts, add a
  rule for a case no agent batch can reach, and still leave JSON payload strings to be handled differently.

## Decision

Option 3, schema version 2 (0064):

- `records(id INTEGER PRIMARY KEY, kind, origin, source, agent_id, seq, captured_at, received_at, boot_id,
  clock_offset_ns, data) STRICT`. `agent_id`, `seq`, `boot_id`, `clock_offset_ns` and `data` are `NULL` when
  absent (`seq` for origins other than `agent`; `data` for metrics and log lines).
- Unique index `records_agent_seq ON records(agent_id, seq) WHERE origin = 'agent'`; records are inserted with
  `ON CONFLICT DO NOTHING` and counted as duplicates, never replaced or updated (no `REPLACE`, no upsert).
- Indexes `records_kind_source_time(kind, source, captured_at)` and `records_kind_time(kind, captured_at)`.
- `metrics(record_id INTEGER PRIMARY KEY REFERENCES records(id), source, name, captured_at, value REAL, unit,
  labels) STRICT` with index `metrics_source_name_time(source, name, captured_at)`; `source` and `captured_at`
  are copied from `records` so one index serves a series' time range in order. `labels` is the JSON object,
  `NULL` when there are no labels.
- `log_lines(record_id INTEGER PRIMARY KEY REFERENCES records(id), log, program, pid, priority, message,
  truncated) STRICT`, and the FTS5 table `log_fts(message)` with external content `log_lines`
  (`content_rowid='record_id'`), tokenizer `unicode61 remove_diacritics 2`. There is **no trigger**: the
  store's write path inserts `(rowid, message)` into `log_fts` right after each `log_lines` row, in the same
  transaction (option b). Invariant: every `log_lines` row has exactly one index entry with its message,
  written in the transaction that wrote the row; `store.WriteBatch` is the only code that inserts into
  `log_lines`. A log line is searchable as soon as its batch is committed. Option a was rejected because it
  costs about three times as much for the same result, option c because it gives up search at commit and
  needs a backlog with recovery, which is not justified while option b may meet the criterion; c remains the
  next step if the DS918+ measurement (0068) still exceeds one second.
- Times are stored as Unix nanoseconds (`INTEGER`). `captured_at` and `received_at` outside the range of a
  signed 64-bit nanosecond count (1677-09-21 to 2262-04-11) and sequence numbers above 2^63 − 1 are rejected
  by the store; times inside JSON payloads keep RFC 3339 and have no such limit.
- JSON payloads use the model's own JSON encoding (the wire format's field names, `docs/WIRE_FORMAT.md`).
  Invalid UTF-8 in their strings and in metric labels is replaced on write, not rejected (option i).

## Consequences

- A resend of a record, also one inside another kind's batch, is stored once (0045), enforced by the
  database, not by the caller.
- A series query reads one index range; a kind-wide time-range query (e.g. OOM kills for #23) uses
  `records_kind_time` or `records_kind_source_time`.
- Snapshot, service, MariaDB, kernel-event and gap payloads can be filtered only through SQLite's JSON
  functions; a view that needs a column or index adds it with a migration (#41 for gap objects, #50–#52).
- The log message is stored once; deleting log lines (retention, #46) must also remove their entries from the
  external-content FTS5 index (the FTS5 `delete` command with the old message, or a `delete` trigger),
  otherwise the index keeps entries of deleted rows.
- Because no trigger keeps the index in step, any new code that writes `log_lines` (an importer that bypasses
  `WriteBatch`, a repair tool, retention) must maintain `log_fts` in the same transaction; the store's tests
  check the index with FTS5 `integrity-check`. A row written without its index entry is not found by search
  and makes `integrity-check` fail.
- Invalid UTF-8 in a JSON payload's strings and in metric labels (both stored as JSON) is replaced by U+FFFD
  on write, and `WriteBatch` reports the record as stored; log fields and the other metric fields are stored
  byte for byte. Records that arrive over the wire are already valid UTF-8, because decoding replaces invalid
  bytes (0042, `docs/WIRE_FORMAT.md`), so the replacement on write only affects callers that build records
  in Go.
- `WriteBatch` checks the agent ID's format but stores `Batch.BootID` as given (only `""` becomes `NULL`); the
  caller validates it first. The ingest API (#40) passes the boot ID of a header that `wire.Header.Validate`
  accepted, a lower-case UUID.
- An agent counter above 2^63 − 1 or a capture time outside 1677–2262 cannot be stored; both are documented in
  `docs/WIRE_FORMAT.md` as backend limits.
