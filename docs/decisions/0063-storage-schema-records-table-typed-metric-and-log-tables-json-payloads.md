# 0063: Storage schema: one records table holds every record's identity, metrics and log lines get own tables, other payloads are stored as JSON

- **Status:** Proposed
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
  (`content_rowid='record_id'`), tokenizer `unicode61 remove_diacritics 2`, filled by an `AFTER INSERT`
  trigger on `log_lines`.
- Times are stored as Unix nanoseconds (`INTEGER`). `captured_at` and `received_at` outside the range of a
  signed 64-bit nanosecond count (1677-09-21 to 2262-04-11) and sequence numbers above 2^63 − 1 are rejected
  by the store; times inside JSON payloads keep RFC 3339 and have no such limit.
- JSON payloads use the model's own JSON encoding (the wire format's field names, `docs/WIRE_FORMAT.md`).

## Consequences

- A resend of a record, also one inside another kind's batch, is stored once (0045), enforced by the
  database, not by the caller.
- A series query reads one index range; a kind-wide time-range query (e.g. OOM kills for #23) uses
  `records_kind_time` or `records_kind_source_time`.
- Snapshot, service, MariaDB, kernel-event and gap payloads can be filtered only through SQLite's JSON
  functions; a view that needs a column or index adds it with a migration (#41 for gap objects, #50–#52).
- The log message is stored once; deleting log lines (retention, #46) must also add a `delete` trigger for
  the external-content FTS5 index, otherwise the index keeps entries of deleted rows.
- Invalid UTF-8 in a JSON payload's strings is replaced by U+FFFD on write; log messages and metric fields are
  stored byte for byte.
- An agent counter above 2^63 − 1 or a capture time outside 1677–2262 cannot be stored; both are documented in
  `docs/WIRE_FORMAT.md` as backend limits.
