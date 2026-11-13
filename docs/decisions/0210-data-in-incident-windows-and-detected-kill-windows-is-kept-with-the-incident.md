# 0210: Data in incident windows and in the windows of detected kills is kept as long as the incident

- **Status:** Proposed
- **Date:** 2026-11-13
- **Area:** Storage
- **Source:** Issue #46
- **Supersedes:** —

## Context

Incidents are kept without limit by default, but they are built only by `vandoxd incidents`, which rebuilds from everything stored
([Incidents](../areas/incidents.md)). Removing the lines and snapshots of a window would let a re-run replace a stored incident by a
poorer one, would empty its charts after 30 days, and would lose for good every outage nobody ran the command for yet. The Product
Manager chose to keep the data of a kept incident's window (spec, Q1 c); the spec challenge extended it to kills no incident covers.
Any local process on the monitored server can write a journal line that the signature catalog reads as an OOM kill; the agent's
`kernel_event` OOM kills come only from kernel-transport entries of facility 0, which no process writes ([Agent kernel events and
boots](../areas/agent-kernel-events.md)).

## Options considered

1. **Remove by the periods, freeze old incidents against re-runs** — bounded, but charts empty after 30 days and an outage not yet
   reconstructed is lost.
2. **Run the incident reconstruction inside the retention job** — every outage gets its incident first, but a second writer of
   incidents next to the command, and the whole rebuild's cost in the service.
3. **Keep every record captured in a stored incident's window or in the window of any detected kill** — also of catalog kill lines;
   rejected: one forged line every `window_before + window_after` would keep all data.
4. **Keep the windows of stored incidents and of agent `kernel_event` OOM kills** (chosen) — the job reads the agent's kernel events
   before it removes data and stores each kill's window as a hold.

## Decision

Before removing data, the job reads the `kernel_event` records received since its last scan and stores a hold for the window of each
OOM kill, merged with the holds it overlaps; no record captured inside a stored incident's window or a hold is removed while the
incident (or, for a hold, its window end under `retention.incidents`) is kept. The rules are in [Retention and
rollups](../areas/retention.md).

## Consequences

- A re-run of `vandoxd incidents` after removal builds the same incidents; incident charts keep their data.
- Residual of holds: only a holder of the agent token can forge a `kernel_event`; the free-space floor `ingest.min_free` bounds the
  growth (record 0177).
- Residual of stored incidents (pending the Product Manager, spec Q1 c given with the wrong actor): any local process can write kill
  lines; once `vandoxd incidents` runs, each becomes an incident whose window (`window_before + window_after`, 1 h 30 min by default) is
  kept as long as the incident (default unlimited), so one line every 90 minutes keeps all agent data until `ingest.min_free` stops
  ingest.
- A kill without a `kernel_event` (an agent that did not run its kernel collector) gets no hold; its window follows the periods.
- The forensics report counts no agent hour past `retention.logs` as observed, even inside a kept window, so kept windows do not
  weigh the base rates (spec S10).
