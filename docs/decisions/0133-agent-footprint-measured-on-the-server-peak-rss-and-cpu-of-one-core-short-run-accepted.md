# 0133: Agent footprint measured on the server as peak RSS and CPU of one core; a run short of 24 hours without sending accepted

- **Status:** Proposed
- **Date:** 2026-10-30
- **Area:** Agent
- **Source:** Issue #44
- **Supersedes:** —

## Context

Issue #44 makes the agent's footprint a release criterion of v0.2.0: under 30 MB of memory and under 1 % CPU on average,
measured over 24 hours on a test system with realistic load (several hundred processes). The issue does not say which
memory figure counts or what 1 % is a share of. The Product Manager measured the agent on the production server
instead (Ubuntu 22.04, kernel 5.15, one core, 2 GB): one instance ran 22 h 42 min, the server had 163 processes, and the
backend (`vandoxd` v0.1.0, without the ingest API) answered every batch with `404`, so the agent never sent. The
results and their sources are in [Benchmarks](../BENCHMARKS.md) (*Agent footprint*).

## Options considered

1. **Accept the server run as meeting the criterion and document its deviations** (chosen) — the server's own load is the
   load the criterion protects, and the margins (peak RSS about 20.4 MB of 30 MB, CPU 0.42 % of 1 %) are far wider than the
   missing 78 minutes could change: peak RSS is a high-water mark over the whole instance, the CPU figure an average rate.
   The sending path and a memory pressure episode stay unmeasured.
2. **Require a new full 24-hour run while the agent sends** — measures the sender and the backfill as well, but needs
   #43 and a backend with the ingest API, and holds up #45 for figures with this much headroom; the sending path gets a
   follow-up issue instead.
3. **A test system with a synthetic load of several hundred processes** — reproducible, but a generated load is not the
   server's, and the Product Manager chose the server.

## Decision

The memory criterion is met when the agent's peak RSS (`VmHWM`) stays under 30 MB, and the CPU criterion when its
cgroup CPU time over the run's elapsed time is under 1 % of one core. The 22 h 42 min run on the production server without
sending meets both, and its deviations from the issue (duration, 163 instead of several hundred processes, no sending) are
accepted by the Product Manager.

## Consequences

- The cgroup's `MemoryCurrent` (about 52.5 MiB) is not the criterion: it includes the page cache of the spool files.
- The footprint while sending and backfilling, and under memory pressure, is not measured; it belongs to the follow-up
  issue on the sending path and to the parallel run of #45.
- Revisit when a later version adds a collector or changes the sender: a new run on the server under the same
  definitions replaces the results.
