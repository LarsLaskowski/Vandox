# 0063: Storage schema: one records table holds every record's identity, metrics and log lines get own tables, other payloads are stored as JSON

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Storage
- **Source:** Issue #14
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

Invalid UTF-8 in strings stored as JSON: **replace with U+FFFD** (chosen; consistent with the wire format, which replaces it on decode) rather than rejecting a record the wire format accepts.

## Decision

Option 3 with the write path filling the FTS5 index (b), times as Unix nanoseconds, and invalid UTF-8 in JSON strings replaced on write. The tables, indexes, invariants and limits are in the [Storage](../areas/storage.md) area (*Schema*, *Writing*).

## Consequences

- A resend of a record, also one inside another kind's batch, is stored once, enforced by the database and not by the caller.
- A series query reads one index range; a kind-wide time-range query uses the kind indexes.
- Snapshot, service, MariaDB, kernel-event and gap payloads can be filtered only through SQLite's JSON functions; a view that needs a column or index adds it with a migration.
- Because no trigger keeps the index in step, every code path that writes or deletes log lines (a repair tool, retention) must maintain the FTS5 index itself.
- An agent counter above 2^63 − 1 or a capture time outside 1677–2262 cannot be stored (a backend limit of the wire format).
