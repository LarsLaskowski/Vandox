# 0172: The spool keeps a bounded number of acknowledged ranges

- **Status:** Accepted
- **Date:** 2026-10-23
- **Area:** Agent spool
- **Source:** Issue #38
- **Supersedes:** —

## Context

The sender (#39) needs current data first and the backlog in order, so the backend acknowledges records out of order. The spool
(record 0123) deletes a segment only when all its records are acknowledged, and its memory and persisted state must stay bounded
(record 0025).

## Options considered

1. **Only a contiguous prefix** — the sender could not acknowledge current data before the backlog.
2. **A bitmap per segment** — memory and persisted state grow with the records.
3. **A fixed number of disjoint ranges above the oldest unacknowledged record** (chosen) — enough for a current and a backfill
   stream; one more is refused and the records are sent again (idempotent).

## Decision

Option 3: the spool keeps at most a fixed number of disjoint acknowledged ranges and refuses an acknowledgement beyond them. The bound
is in [Agent spool](../areas/agent-spool.md).

## Consequences

- A refused acknowledgement costs a resend, never a record.
- The sender must keep the number of open ranges low (record 0125) and must not leave refused records unacknowledged in the middle
  of acknowledged ones (record 0176).
