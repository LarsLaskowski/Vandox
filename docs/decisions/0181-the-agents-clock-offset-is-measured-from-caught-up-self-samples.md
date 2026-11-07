# 0181: The agent's clock offset is measured from caught-up self samples

- **Status:** Accepted
- **Date:** 2026-10-26
- **Area:** Detection, alerts and reports
- **Source:** Issue #41
- **Supersedes:** —

## Context

The live classification (record 0130) corrects a record's lateness by the agent's clock offset. The agent sends no clock offset (its
header field stays empty). One-way timing cannot tell an agent clock that is behind from a delayed record, so any offset measured from
arrival times can be fooled by old data unless the samples it uses are known to be fresh. Log lines and kernel events carry a time
written by another program, possibly another host or zone. After an outage an agent whose acknowledged ranges are full sends the
backlog with consecutive numbers, so a late sample can arrive as the newest record.

## Options considered

1. **The tightest lower bound from records that read the agent's clock, over the agent's counted self samples** (chosen) — a record
   whose capture time is the agent's run time proves that the agent clock is at least `captured − received` ahead of the backend (the
   delay is never negative); a self sample counts only when its send lag shows it fresh, it is the newest number and no sequence gap
   is open below it, and before the first counted sample the bounds of earlier batches' run-timed records serve, so a batch's own
   records cannot prove themselves current.
2. **The largest bound over every record of the batch as well** — one future-stamped log line would raise the estimate and backfill
   every honest record of its batch.
3. **The minimum lateness over a time window of all records** — a window that holds only backfill (an agent restarted with holes)
   measures the backlog's age as an offset and makes old records live (the spec challenge's M1).
4. **The header's `clock_offset_ns`** — empty today, and relative to the agent's time source, not to the backend.
5. **A counted sample far below the estimate measures only when the agent's previous counted sample, spaced 30 seconds to 5 minutes
   and agreeing with it, confirms it** (chosen, review of #41) — the lower spacing lies well below the self interval, so timer jitter
   never decides whether a clock that is behind is measured; a burst replay delivers its late samples seconds apart, so it finds no
   partner.
6. **Confirmation by any counted sample of the last 5 minutes** — more partners for a late sample, and the non-measuring readings
   would need a store of their own.
7. **Counting a sample only when the agent's previous batch arrived shortly before** — the first sample of an agent started during
   the outage counts its lag from the start and arrives in the middle of the replay, right after other batches.
8. **Never letting a counted sample lower the estimate** — a clock that is behind or stepped back would never be learned.

## Decision

Options 1 and 5: the offset is the largest bound of the measuring counted self samples in a window before the newest of them, and a
sample far below the offset in effect measures only when the previous counted sample confirms it. The constants, windows and
buckets are in [Detection, alerts and reports](../areas/detection-and-reports.md) (*Live data and backfill*).

## Consequences

- A clock that is behind is measured from its second counted sample, about a minute after the first; before the first counted sample,
  a clock behind by more than the allowance yields only backfilled records.
- A clock stepped back makes fresh records backfilled for up to about 6 minutes, until the bounds from before the step have left the
  window; a clock stepped forward does so before the first counted sample for up to about 5 minutes, later until the next counted
  sample. A too-large estimate only makes fresh records backfilled, never old ones live.
- Residual: two late counted samples (an agent started twice during an outage) spaced and agreeing as a confirmation can still
  measure.
- Residual: the measured offset can be short by the delivery delay of the freshest counted sample (up to the agent's request
  timeout), so a record can be up to that much older than its allowance and still be live.
