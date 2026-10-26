# 0130: Live or backfilled is decided at write time; gaps are stateful objects; the offset comes from caught-up self samples

- **Status:** Proposed
- **Date:** 2026-10-26
- **Area:** Detection, alerts and reports
- **Source:** Issue #41
- **Supersedes:** — (amends 0022: fixes how the classification is made and what threshold it uses; 0022 stays unedited)

## Context

Record 0022 decided that the backend classifies every agent record as live or backfilled from the data, but left open the
threshold, how gaps that may still fill are tracked, and how the agent's clock is taken into account. The agent sends no
clock offset (its header field stays empty), a stored record is never updated (Storage, *Writing*), and the rule engine and
the gap views come later (#54, #56, #61). One-way timing cannot tell an agent clock that is behind from a delayed record, so
any offset measured from arrival times can be fooled by old data unless the samples it uses are known to be fresh.

## Options considered

Where the classification is decided:

1. **At write time, stored per record** (chosen): one column set in the insert, so readers need no rule; the record row is
   still never updated, because the decision is final when the record is stored.
2. **At read time, from stored receive and capture times and the gap state** — no schema column, but every reader repeats the
   rule and a record's classification would change as gaps close, so the same record could alert later.

How gaps are kept:

1. **Gap rows with a state in a table of their own** (chosen): open, closed or final, narrowed or split as records arrive.
2. **Backend `gap` records in `records`** — immutable, so a gap that fills later cannot change its state.

How a gap that never fills (a numbering jump after an unclean stop) stops holding back live data:

1. **A wait of 5 minutes without any record arriving into an open sequence gap** (chosen): the agent's backfill sends oldest
   first and never skips an unacknowledged record, so a pass that fills nothing for that long has nothing left for them.
2. **The agent's spool-age self-metric reaching 0** — an agent state reported once a minute, which a record handed over between
   the sample and its own number would contradict.
3. **Never** — one crash would keep every later record backfilled.

When the gap state is taken for a batch's records:

1. **After the whole batch is applied** (chosen): a batch that brings the last missing number of the gaps below its records
   is classified by lateness alone. Its records then lie within the live allowance, like any live record.
2. **Before the batch** — a live-stream batch that arrives after a later one and fills the gap it left would be backfilled in
   normal operation, where the live and the backfill stream run side by side, while the closing batch's records it would
   hold back are no older than live data may be.

How the clock offset is measured:

1. **The tightest lower bound from records that read the agent's clock** (chosen): a record whose capture time is the
   agent's run time proves that the agent clock is at least `captured − received` ahead of the backend (the delay is never
   negative). Log lines and kernel events carry a time written by another program, possibly another host or zone, and prove
   nothing. The estimate is the largest such bound over the agent's *counted* self samples (a send lag of at most 60 seconds on
   the agent's own clock, which a quiet agent reaches through its 30-second `system` samples; a new highest number; no open
   sequence gap below it) received within 5 minutes before the newest of them. Before the first counted sample it is the
   largest bound of the run-timed records of earlier batches of the last 5 minutes, never below 0: a batch's own records
   would let a record stamped at the end of the storable range prove itself current. A counted sample counts for its own
   batch, because its send lag already shows it fresh. Both windows are kept as bounded rows of
   10-second buckets, so a window's maximum survives the expiry of its largest value. A late sample only lowers its own bound, which
   the maximum ignores; a clock stepped back leaves a too-large estimate for up to 5 minutes, which only makes fresh records
   backfilled.
2. **The largest bound over every record of the batch as well** — one future-stamped log line would raise the estimate and
   backfill every honest record of its batch.
3. **The minimum lateness over a time window of all records** — a window that holds only backfill (an agent restarted with
   holes) measures the backlog's age as an offset and makes old records live (the spec challenge's M1).
4. **The header's `clock_offset_ns`** — empty today, and relative to the agent's time source, not to the backend.

## Decision

The write transaction of a batch with an agent ID keeps the agent's sequence and time gaps, measures the offset with option
1 of the last list, and stores each record as live exactly when its corrected lateness lies within its kind's allowance
(10 minutes for `log_line` and `kernel_event`, 90 seconds otherwise) and no open sequence gap of the agent lies below its
number once its whole batch is applied. The rules, constants and states are in the [Detection, alerts and reports](../areas/detection-and-reports.md) area
(*Live data and backfill*); the tables in [Storage](../areas/storage.md).

## Consequences

- No alert on backfill by construction: a record's classification never changes after it is stored, and current data stays
  backfilled until an outage's gaps close or become final.
- After an unclean stop, current data stays backfilled for the backfill's duration plus 5 minutes.
- Before the first counted self sample, an agent clock that is behind by more than the allowance yields only backfilled
  records; a clock stepped back, or stepped forward before the first counted sample, makes fresh records backfilled for up to
  about 5 minutes; a clock stepped forward later, until the next counted sample.
- All time arithmetic is done in 128-bit integers and stored bounds are saturated, so no capture time in the storable range
  can make a write fail.
- Time gaps keep only `open` or `closed`; whether a time gap is final is decided when it is read, so no batch walks other gaps.
  A stored record adds at most three gap rows, each found by an indexed seek.
- Residual: the measured offset can be short by the delivery delay of the freshest counted sample (up to the agent's 30-second request timeout), so a record can
  be up to that much older than its allowance and still be live.
- A flood of log lines read more than 10 minutes late, and a backup start seen more than 90 seconds late, are backfilled.
- Retention (#46) must delete the per-sample time rows and gap rows of the data it removes.
