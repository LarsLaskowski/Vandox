# 0177: Ingest is bounded by two slots through the commit and a free-space floor, not a rate cap

- **Status:** Accepted
- **Date:** 2026-10-25
- **Area:** Ingest endpoint
- **Source:** Issue #40
- **Supersedes:** —

## Context

Security area 1 asks that a valid agent cannot exhaust the backend's memory or disk; the container has 512 MiB (record 0060). The
backend must answer `2xx` only after the commit (record 0045), and the store takes a fully decoded batch and one writer at a time. The
agent keeps two requests in flight and treats any non-`2xx` that is not a refusal as a backend not accepting batches, which starts one
shared backoff for both of its streams (record 0125). The data volume is often a NAS volume shared with other services, and retention
(#46) bounds the database by age, not by size.

## Options considered

1. **A slot for decoding only** — decoded batches could wait for the single writer outside any bound, one per open connection.
2. **A slot from the body read through the commit or rollback, two of them** (chosen) — at most two decoded batches exist; the agent's
   two streams never wait on each other for longer than one write.
3. **A request-per-second cap answered `429`** — the answer starts the agent's shared backoff and holds current data back, and it does
   not bound disk growth.
4. **No cap; the two slots and the one writer bound the rate** (chosen).
5. **Accept the disk residual until retention (#46)** — a compromised monitored server holds the token and could fill the data volume,
   against the goal of security area 1.
6. **A free-space floor `ingest.min_free` checked before every write** (chosen) — the rule of the agent's `spool.min_free`; the agent
   keeps its records while the floor holds; one `statvfs` per batch.

## Decision

The endpoint holds one of two slots from the body read through the commit or rollback, writes nothing while less than
`ingest.min_free` is free, and has no request-rate cap. The statuses and limits are in [Ingest endpoint](../areas/ingest-endpoint.md),
the option in [Configuration options](../areas/configuration-options.md).

## Consequences

- A token holder can still grow the database up to the floor; the web UI and the import keep working there, and ingest resumes when
  space is freed or retention (#46) removes data.
- Revisit the slot count when the measured memory of one request (Ingest endpoint area, *Limits, slots and memory*) or several agents
  (#89) change the budget.
