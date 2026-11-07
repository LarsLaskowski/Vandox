# 0175: The state file syncs the spool before every save

- **Status:** Accepted
- **Date:** 2026-10-23
- **Area:** Agent spool
- **Source:** Issue #38
- **Supersedes:** —

## Context

The agent's state file commits log and kernel positions, the boot and the open backup run. None of them may become durable before the
records they cover, or a crash would lose those records without a gap. Collectors hand records to the spool every few seconds.

## Options considered

1. **An fsync per hand-over** — the I/O cost every 2 seconds per collector.
2. **Positions stored inside the spool**, with the records they cover — exact delivery across a crash, but a larger change to three
   collectors.
3. **The state file syncs the spool before every save** (chosen) — with a periodic sync of the spool, the barrier makes the records
   durable before any position that covers them.

## Decision

Option 3: every save of the state file first syncs the spool. The boot and the open backup run reach the state only once the
scheduler reports the run's records kept (record 0159), so the barrier covers them as well. The order is in
[Agent spool](../areas/agent-spool.md) and [Agent](../areas/agent.md) (*Agent state*).

## Consequences

- No saved position covers a record a crash can still lose; a crash repeats records instead (record 0161).
- A failing or slow spool sync delays the state save; what happens then is in the Agent area (*Agent state*).
