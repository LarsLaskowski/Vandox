# 0130: Live or backfilled is decided at write time

- **Status:** Accepted
- **Date:** 2026-10-26
- **Area:** Detection, alerts and reports
- **Source:** Issue #41
- **Supersedes:** — (amends 0022: fixes how the classification is made and what threshold it uses; 0022 stays unedited)

## Context

Record 0022 decided that the backend classifies every agent record as live or backfilled from the data, so that backfilled data never
alerts, but left open when the classification is made, its threshold and how gaps that may still fill are taken into account. A stored
record is never updated (Storage, *Writing*), and the rule engine and the gap views come later (#54, #56, #61). The agent sends a live
and a backfill stream side by side (record 0125), so batches arrive out of order.

## Options considered

1. **At write time, stored per record** (chosen) — one column set in the insert, so readers need no rule; the record row is still
   never updated, because the decision is final when the record is stored.
2. **At read time, from stored receive and capture times and the gap state** — no schema column, but every reader repeats the rule
   and a record's classification would change as gaps close, so the same record could alert later.
3. **The gap state taken after the whole batch is applied** (chosen) — a batch that brings the last missing number of the gaps below
   its records is classified by lateness alone; its records then lie within the live allowance, like any live record.
4. **The gap state taken before the batch** — a live-stream batch that arrives after a later one and fills the gap it left would be
   backfilled in normal operation, while the records it would hold back are no older than live data may be.

## Decision

The write transaction of a batch with an agent ID stores each record as live exactly when its corrected lateness lies within its
kind's allowance and no open sequence gap of the agent lies below its number once its whole batch is applied. The allowances and the
rule are in [Detection, alerts and reports](../areas/detection-and-reports.md) (*Live data and backfill*), the column in
[Storage](../areas/storage.md); how gaps are kept is record 0180, how the offset is measured record 0181.

## Consequences

- No alert on backfill by construction: a record's classification never changes after it is stored, and current data stays
  backfilled until an outage's gaps close or become final.
- A flood of log lines read later than their allowance, and a backup start seen late, are backfilled.
- All time arithmetic is done in 128-bit integers and stored bounds are saturated, so no capture time in the storable range can make a
  write fail.
