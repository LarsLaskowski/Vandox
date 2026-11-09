# 0195: A backfill's CPU above 1 % of one core is an accepted deviation; the criterion stays the mean over the run

- **Status:** Proposed
- **Date:** 2026-11-09
- **Area:** Agent
- **Source:** Issue #342
- **Supersedes:** 0133 (amends it: the footprint criterion measured again while the agent sends, and the deviation a backfill
  adds)

## Context

Record [0133](0133-agent-footprint-measured-on-the-server-as-peak-rss-and-cpu-of-one-core.md) defines the agent's footprint
criterion as peak RSS (`VmHWM`) under 30 MB and cgroup CPU time over the run's elapsed time under 1 % of one core, and left the
footprint while sending and backfilling to #342. The Product Manager measured it on the production server with `v0.2.0-rc.1`
sending to the backend: over 35 h 50 min the mean CPU is under 1 % and the peak RSS under 30 MB, but while a backlog is sent the
agent uses about 12 to 14 % of one core, for the minutes the backfill lasts. The values are in [Benchmarks](../BENCHMARKS.md)
(*Agent footprint*), the sender's rate in [Agent sending](../areas/agent-sending.md) (*Memory and rate*).

## Options considered

1. **Keep the mean over the run as the criterion and accept the backfill's CPU as a justified deviation** (chosen) — the
   backlog is sent within minutes, the mean over the run stays well under 1 %, and the server's load is what the criterion
   protects; the cost is a short CPU burst after every outage of the connection.
2. **Hold every backfill under 1 % of one core by pacing it** — keeps the bound in every window, but stretches the sending of
   a backlog many times over while the data the backend needs to reconstruct the outage waits in the spool; the bandwidth
   limit (`backend.backfill_bandwidth`) was not what bounded the measured rate either.
3. **A criterion per time window (for example per minute)** — states a burst bound, but a backfill does not meet it, and the
   issue's criterion is an average.

## Decision

The CPU criterion stays the mean of the agent's cgroup CPU time over the run, as 0133 defines it. The CPU of a backfill, about
12 to 14 % of one core while it lasts, is a justified deviation, which the Product Manager accepted on 2026-11-09; the peak RSS
while sending meets the 30 MB target and is no deviation.

## Consequences

- A backfill after an outage of the connection shows on the server as a short CPU burst of the agent; its length grows with the
  backlog.
- The peak RSS while sending is close to the target (1.2 MB margin): a change that adds memory to the sender or the backfill
  needs a new run on the server under the same definitions.
- Revisit when the sender or the backfill changes, or when a backfill measurably slows the monitored services.
