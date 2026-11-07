# 0176: The sender gives up refused records one by one with a recorded gap, at a bounded rate

- **Status:** Accepted
- **Date:** 2026-10-24
- **Area:** Agent sending
- **Source:** Issue #39
- **Supersedes:** —

## Context

A backend can refuse a single batch as invalid as a whole (record 0124). The agent must never lose a record without a recorded gap
(record 0028). The spool keeps a bounded number of acknowledged ranges (record 0172); the spec's challenge showed that leaving refused
records unacknowledged between acknowledged ones can exhaust them, after which the spool stops deleting acknowledged files and drops
stored data for its size bound.

## Options considered

1. **Retry refused batches in later passes** — keeps the records but leaves one unacknowledged hole per refused batch; rejected by the
   spec challenge (exhausts the ranges).
2. **Give the whole refused batch up with a gap** — no hole, but one bad record costs up to about 2,700 good ones.
3. **Split the refused batch until only the refused records remain, give each of them up with a recorded gap, a bounded number an
   hour** (chosen) — no hole, the loss is one record per bad record and is recorded; a backend that refuses everything is caught by the
   hourly bound: the stream that meets it waits until a give-up slot frees, while the other stream goes on.

## Decision

Option 3: the sender splits a refused batch and gives each refused record up with a `gap` record, at most a fixed number an hour;
beyond that the stream waits for a free slot. The bound, the gap's fields and the log lines are in
[Agent sending](../areas/agent-sending.md).

## Consequences

- A record the backend refuses on its own is lost, but recorded as a gap the backend receives; the guarantee holds.
- A version mismatch must not be answered with a refusal status (record 0124), or the hourly bound would still give records up every
  hour.
- The hourly bound is kept in memory: each start of the agent allows a new hour's give-ups. Restarts are rare and each give-up is a
  recorded gap, so a persisted bound was not worth a second state file.
- Revisit the hourly bound if the server measurement (#44) or real outages show other figures.
