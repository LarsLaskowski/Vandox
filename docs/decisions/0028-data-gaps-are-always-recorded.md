# 0028: Data gaps are always recorded, never silent

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Agent
- **Source:** Issue #7
- **Supersedes:** —

## Context

Vandox exists to reconstruct outages, which often happen when data is hardest to collect (memory exhaustion, a backend host switched off at night). The spool and backfill (0045) make collection gapless across backend downtime. Some loss cannot be prevented: the agent is killed by the OOM killer, the spool hits its size bound, or a single collector times out (0029). An analysis that reads a missing interval as "nothing happened" draws the wrong conclusion.

## Options considered

1. **Best effort, gaps implicit** — a reader cannot tell "no data" from "nothing happened".
2. **Gapless only, loss treated as a bug** — promises something the agent cannot keep when it is killed or its spool is full.
3. **No gap unless it is explicitly recorded** (chosen) — every interval without data carries a recorded gap with its cause where known, stored and shown like data.

## Decision

Option 3 is a guarantee: Vandox has no data gaps unless they are explicitly recorded. The agent records the gaps it knows about and the backend those it detects; analysis, reports and the UI treat a recorded gap as "unknown", never as "normal". See the [Agent](../areas/agent.md) area (*Gaps*).

## Consequences

- The wire format and the storage need a representation of a gap from v0.1.0 on.
- Spool eviction, collector timeouts and backend gap detection each emit a gap record, and a test of each path checks it.
- Weakening this guarantee needs the Product Manager and a superseding record.
