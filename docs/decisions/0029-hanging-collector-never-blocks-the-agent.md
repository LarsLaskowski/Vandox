# 0029: A hanging collector or database never blocks the agent

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #7
- **Supersedes:** —

## Context

The failure Vandox is built to explain is memory exhaustion followed by the OOM killer and MariaDB, Plesk and
mail going down (`docs/ARCHITECTURE.md`, *Monitored server*). In exactly that situation a read from `/proc`,
a systemd D-Bus call, a journal read or a query on the MariaDB socket can hang for a long time. If one
collector or the database stalls the agent, the agent stops recording at the moment its data matters most.

## Options considered

1. **Collect sequentially without limits** — simple; one hung source stops all collection, spooling and
   sending.
2. **Sequential collection with per-call timeouts** — bounded, but a slow source still delays every other
   collector of the same cycle.
3. **Each collector isolated with its own deadline** — every collector runs independently under a
   `context.Context` deadline; a collector that misses it loses only its own sample, which is recorded as a
   gap (0028), while the other collectors, the spool and the sender keep running.

## Decision

Option 3 is a guarantee: a hanging collector or a hanging database never blocks the agent. Every call to an
external source (`/proc`, `/sys`, systemd D-Bus, journald, the MariaDB socket) takes a context with a
deadline and is honored when it expires; a collector that is still hung when its next cycle is due is not
started a second time.

## Consequences

- Every collector needs a deadline and a test with a fake source that hangs until its context is cancelled.
- A hung source costs at most one goroutine per collector, not one per cycle.
- Missed samples become recorded gaps (0028) instead of silent holes or a stalled agent.
