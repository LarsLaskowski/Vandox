# 0180: Sequence and time gaps are stateful rows that become final after a quiet wait

- **Status:** Accepted
- **Date:** 2026-10-26
- **Area:** Detection, alerts and reports
- **Source:** Issue #41
- **Supersedes:** —

## Context

A record is live only while no open sequence gap of its agent lies below it (record 0130). A gap may still fill as the backfill
arrives, and a stored record is never updated (Storage, *Writing*). Some gaps never fill: an unclean stop leaves a numbering jump
(record 0171). The agent's backfill sends oldest first and never skips an unacknowledged record (record 0125).

## Options considered

1. **Gap rows with a state in a table of their own** (chosen) — open, closed or final, narrowed or split as records arrive.
2. **Backend `gap` records in `records`** — immutable, so a gap that fills later cannot change its state.
3. **A wait without any record arriving into an open sequence gap** (chosen) — a backfill pass that fills nothing for that long has
   nothing left for the gap.
4. **The agent's spool-age self-metric reaching 0** — an agent state reported once a minute, which a record handed over between the
   sample and its own number would contradict.
5. **Never** — one crash would keep every later record backfilled.

## Decision

Options 1 and 3: the backend keeps an agent's sequence and time gaps as rows with a state, and makes its open sequence gaps final after
a wait in which no record arrived into any of them. The states, the wait and the time-gap rule are in
[Detection, alerts and reports](../areas/detection-and-reports.md) (*Live data and backfill*), the tables in
[Storage](../areas/storage.md).

## Consequences

- After an unclean stop, current data stays backfilled for the backfill's duration plus the wait.
- Time gaps keep only `open` or `closed`; whether a time gap is final is decided when it is read, so no batch walks other gaps. A
  stored record adds at most three gap rows, each found by an indexed seek, so a hostile agent cannot make the work per record grow.
- Retention (#46) must delete the per-sample time rows and gap rows of the data it removes.
