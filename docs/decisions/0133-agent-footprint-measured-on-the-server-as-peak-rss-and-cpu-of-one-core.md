# 0133: Agent footprint measured on the server as peak RSS and CPU time of one core

- **Status:** Proposed
- **Date:** 2026-10-30
- **Area:** Agent
- **Source:** Issue #44
- **Supersedes:** —

## Context

Issue #44 makes the agent's footprint a release criterion of v0.2.0: under 30 MB of memory and under 1 % CPU on average,
measured over 24 hours on a test system with realistic load (several hundred processes). The issue does not say which
memory figure counts or what 1 % is a share of. The Product Manager measured one agent instance on the production
server instead (Ubuntu 22.04, kernel 5.15, one core, 2 GB) after 24 h 16 min of running. The server had 161 processes.
The backend (`vandoxd` v0.1.0, without the ingest API) answered every batch with `404`, so the agent never sent. The results
and their sources are in [Benchmarks](../BENCHMARKS.md) (*Agent footprint*).

## Options considered

1. **Measure on the production server and have the Product Manager accept the remaining deviations** (chosen) —
   the server's own load is the load the criterion protects, and the margins (peak RSS about 20.4 MB of 30 MB, CPU 0.42 %
   of 1 %) are wide. The sending path and a memory pressure episode stay unmeasured.
2. **Measure again while the agent sends** — covers the sender and the backfill, but needs #43 and a backend with the
   ingest API; the sending path gets a follow-up issue instead.
3. **A test system with a synthetic load of several hundred processes** — reproducible, but a generated load is not the
   server's, and the Product Manager measured on the server.

## Decision

The memory criterion is the agent's peak RSS (`VmHWM`) under 30 MB. The CPU criterion is its cgroup CPU time over the run's
elapsed time under 1 % of one core. The 24 h 16 min run on the production server meets both, and the Product Manager
accepted its remaining deviations from the issue on 2026-10-30.

## Consequences

- **Deviations accepted.** Three deviations from the issue remain, and the Product Manager accepted them on 2026-10-30:
  - the server had 161 processes, not several hundred;
  - the agent never sent anything;
  - the stats of the previous agent instance are lost.

  With that acceptance the issue's second criterion is met.
- The cgroup's `MemoryCurrent` (about 47.3 MiB) is not the criterion: it includes the page cache of the spool files.
- The footprint while sending and backfilling, under memory pressure, and the spool's open time on the server are not
  measured. They belong to #342 (and the pressure cost also to the parallel run of #45).
- Revisit when a later version adds a collector or changes the sender: a new run on the server under the same
  definitions replaces the results.
