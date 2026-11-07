# 0184: The agent unit sets a hard memory limit with GOMEMLIMIT below it

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** —

## Context

The monitored server has 2 GB of RAM, and the agent must bound its memory (record 0025). The issue asks for a unit with resource
limits. The Go runtime does not know a cgroup's memory limit; with the default garbage collector a heap can grow to about twice its
live size.

## Options considered

1. **`MemoryMax=` alone** — the runtime does not know the limit, and the kernel kills the agent for garbage.
2. **`MemoryMax=` with `GOGC` lowered** — a permanent CPU cost on every collection.
3. **`MemoryMax=` with `GOMEMLIMIT` below it** (chosen) — the runtime collects harder only near the soft limit; the cgroup limit stays
   the hard stop.

## Decision

Option 3, with fixed CPU, priority, watchdog and restart settings beside it. The values are in
[Agent installation](../areas/agent-installation.md) (*The unit in short*), each with its reason in `deploy/agent/README.md`.

## Consequences

- The memory values rest on the documented per-collector worst cases (process run 48 MiB, MariaDB 32 MiB, logs 24 MiB, kernel 4 MiB,
  the sender one 1 MiB batch per stream: about 112 MiB if all peak at once under pressure), with the gap between the soft and the hard
  limit left for page cache and runtime overhead, not on a measurement on the server; the parallel run (#45) checks the agent's peak
  RSS against them.
- The CPU quota bounds a runaway loop; the priority settings yield to the server's services without starving the agent while an
  outage builds; the watchdog tolerates a short stall under pressure; restarts are retried forever at a bounded rate.
