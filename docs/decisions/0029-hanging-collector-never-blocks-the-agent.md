# 0029: A hanging collector or database never blocks the agent

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Agent
- **Source:** Issue #7
- **Supersedes:** —

## Context

The failure Vandox is built to explain is memory exhaustion followed by the OOM killer and MariaDB, Plesk and mail going down. In exactly that situation a read from `/proc`, a D-Bus call, a journal read or a query on the MariaDB socket can hang for a long time. If one collector or the database stalls the agent, it stops recording when its data matters most. Not every source can be cancelled: a file read from `/proc` or `/sys` takes no cancellation signal, so when the kernel blocks it a deadline can only stop *waiting* for the stuck worker.

## Options considered

1. **Collect sequentially without limits** — one hung source stops all collection, spooling and sending.
2. **Sequential collection with per-call timeouts** — bounded, but a slow source still delays every other collector of the cycle.
3. **Cancel every call to a source at its deadline** — rejected: it promises something that is impossible for file reads that cannot be interrupted while the kernel blocks them.
4. **Each collector isolated and abandoned at its deadline** (chosen) — the agent waits at most until the deadline, records the sample as a gap (0028), and the other collectors, the spool and the sender keep running; sources that accept a cancellation signal also receive it.

## Decision

Option 4 is a guarantee: a hanging collector or database never blocks the agent. See the [Agent](../areas/agent.md) area (*Collecting*).

## Consequences

- Every collector needs a deadline and a test with a hanging double of its source, and collectors read `/proc` and `/sys` through a replaceable file-system interface.
- A hung source costs at most one stuck worker per collector, not one per cycle; an abandoned worker ends only when the kernel returns from the read.
- Missed samples become recorded gaps (0028) instead of silent holes or a stalled agent.
