# 0028: Data gaps are always recorded, never silent

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #7
- **Supersedes:** —

## Context

Vandox exists to reconstruct outages, which often happen exactly when data is hardest to collect (memory
exhaustion, a backend host switched off at night). 0018 makes collection gapless across backend downtime
through the on-disk spool and backfill, and gives every batch an identity and sequence number. Some loss
cannot be prevented, though: the agent itself is stopped or killed by the OOM killer, the spool hits its
size bound (0018, *Consequences*), or a single collector times out (0029). An analysis that reads a
missing interval as "nothing happened" draws the wrong conclusion.

## Options considered

1. **Best effort, gaps implicit** — a missing interval is simply absent; simple, but a reader cannot tell
   "no data" from "nothing happened".
2. **Gapless only, loss treated as a bug** — promises something the agent cannot keep when it is killed or
   its spool is full.
3. **No gap unless it is explicitly recorded** — every interval without data carries a recorded gap with
   its cause where known (agent not running, spool full and oldest data dropped, collector timed out,
   sequence numbers missing at the backend), stored and shown like data.

## Decision

Option 3 is a guarantee: Vandox has no data gaps unless they are explicitly recorded. The agent records the
gaps it knows about (a collector sample missed, spool data dropped when the size bound is hit) and the
backend records the gaps it detects (missing sequence numbers, intervals with no batch at all). Analysis,
reports and the web UI treat a recorded gap as "unknown", never as "normal".

## Consequences

- The wire format and the storage need a representation of a gap from v0.1.0 on.
- Spool eviction, collector timeouts and backend gap detection each have to emit a gap record; a test of
  each of these paths checks that the gap is recorded.
- Weakening this guarantee needs the Product Manager and a superseding record.
