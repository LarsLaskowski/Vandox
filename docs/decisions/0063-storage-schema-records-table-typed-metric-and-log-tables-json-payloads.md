# 0063: Storage schema: one records table holds every record's identity, metrics and log lines get own tables, other payloads are stored as JSON

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Storage
- **Source:** Issue #14; the size limit of a stored payload: issue #212
- **Supersedes:** —

## Context

The schema must hold every record kind of the shared data model, serve time-range queries per source and metric, and support
full-text search for logs (0007). Forces:

- The backend stores each (agent ID, sequence number) once (0045); the sequence counter is per agent across all kinds, so uniqueness
  must be enforced over all kinds at once, and a resent record never overwrites stored data.
- Metrics are the bulk of the data and are read as time series; log lines are the bulk of the forensics import and are searched in full text.
- Snapshots are nested lists of up to 4096 entries whose views are not designed yet.
- The performance criterion (10,000 records in one transaction, 0083) depends largely on how the FTS5 index of the log lines is filled.

## Options considered

1. **One table per kind, fully normalized** — every field queryable, but uniqueness across kinds needs a shared table anyway, snapshots multiply into hundreds of rows, and the columns of views that do not exist yet would be guessed.
2. **One generic table with a JSON payload for every kind** — simplest, but time series and the FTS5 index would read through JSON and the log message would be stored twice.
3. **Hybrid: a `records` table with identity and capture context, typed tables for metrics and log lines, JSON for the rest** (chosen).

Filling the external-content FTS5 index of the log lines (10,000 lines of about 150 bytes per transaction, measured on a 2.1 GHz Xeon):

- a. **`AFTER INSERT` trigger** — the pattern from the FTS5 documentation; 0.50 to 0.79 s per batch against 0.10 to 0.12 s without FTS5; tokenizer and cache options did not change this. Rejected: about three times the cost for the same result.
- b. **Explicit insert by the write path in the same transaction** (chosen) — 0.18 to 0.33 s per batch; same atomicity, the store keeps the invariant itself.
- c. **Index after the commit by a background indexer** — cheapest transaction, but a committed line is not searchable at once, a persisted backlog with recovery is needed, and retention must not delete unindexed lines. Next step only if option b misses the criterion.

Invalid UTF-8 in strings stored as JSON: **replace with U+FFFD** (chosen; consistent with the wire encoder, which replaces it on encode, while the decoder refuses such a line, [0090](0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md)) rather than rejecting a record at the store.

How large a stored JSON payload may be, and how it reaches SQLite (#212). The default encoder writes `<`, `>`, `&`, `'`, `+`
and every non-ASCII character as a six-byte escape, so a forged legacy monitoring snapshot at the parser's caps
([0093](0093-legacy-monitoring-log-snapshots-by-header-zone-and-whole-snapshot-validity.md)) serializes to about 105 MB, and
the model alone allows far more (4,096 processes with 16 KiB command lines). `vandoxd import` runs in the service's container
(512 MiB, [0060](0060-compose-file-port-bindings-volumes-and-memory-limit.md)), where .NET caps its managed heap at 75 % of
the limit by default:

- d. **Serialize to UTF-8 bytes and bind them, without a limit** — removes the UTF-16 string, but in a scratch run under a
  384 MiB heap limit the serializer's pooled buffers alone ran out of memory for a 100 MB payload, as the string did; the
  model allows payloads several times larger still.
- e. **A relaxed encoder** — fewer escapes, but control and supplementary characters still expand, so it gives no bound by
  itself, and the stored text would differ from what the incident builder measures with
  ([0096](0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md), option 24).
- f. **Lower caps in the legacy parser** — changes what 0093 keeps from real logs and bounds one parser, not the store.
- g. **A store-wide limit on the UTF-8 bytes of a payload's JSON, measured while it is built in a bounded buffer and bound
  as UTF-8 bytes cast to text** (chosen) — memory is bounded by the limit for any payload; the import skips such a record
  like any other refused one, so a forged snapshot costs one record, not the file.
- h. **The same limit checked by the batch validator** — one more serialization of every JSON payload of every batch; the
  writer checks it while it serializes anyway, and a refused batch is rolled back whole.
- i. **A larger container** — moves the limit instead of bounding the allocation.

## Decision

Option 3 with the write path filling the FTS5 index (b), times as Unix nanoseconds, and invalid UTF-8 in JSON strings replaced on write. A JSON payload is limited
by option g: built in a bounded buffer, refused above the limit, and bound as UTF-8 bytes cast to text. The tables, indexes, invariants and limits are in the [Storage](../areas/storage.md) area (*Schema*, *Writing*).

## Consequences

- A resend of a record, also one inside another kind's batch, is stored once, enforced by the database and not by the caller.
- A series query reads one index range; a kind-wide time-range query uses the kind indexes.
- Snapshot, service, MariaDB, kernel-event and gap payloads can be filtered only through SQLite's JSON functions; a view that needs a column or index adds it with a migration.
- Because no trigger keeps the index in step, every code path that writes or deletes log lines (a repair tool, retention) must maintain the FTS5 index itself.
- An agent counter above 2^63 − 1 or a capture time outside 1677–2262 cannot be stored (a backend limit of the wire format).
- A payload whose JSON exceeds the limit cannot be stored: the import skips it with a reason, and any other batch holding one
  is refused whole. An agent record cannot reach the limit, because the wire format bounds its line; a legitimate legacy
  snapshot stays far below it. The value is in the [Storage](../areas/storage.md) area (*Writing*).
- The writer binds the payload as bytes cast to text, never as a blob: the column is a `STRICT` text column, and SQLite's
  JSON functions would read a blob as JSONB.
