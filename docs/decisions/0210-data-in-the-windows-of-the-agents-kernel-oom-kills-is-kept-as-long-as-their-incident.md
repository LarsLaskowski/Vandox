# 0210: Data in the windows of the agent's kernel OOM kills is kept as long as their incident

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
4. **Keep the windows of all stored incidents and of agent `kernel_event` OOM kills** — the Product Manager's first answer; rejected
   once the actor was corrected (Security B3): incidents built from forged lines would keep all data.
5. **Keep only the windows of agent `kernel_event` OOM kills; leave older unconfirmed incidents unchanged on a re-run** (chosen, the
   Product Manager, 2026-11-13, option B) — real kills on a monitored server always have a kernel event, so their evidence stays
   whole; a forged line keeps nothing.

## Decision

Before removing data, the job reads the `kernel_event` records received since its last scan and stores a hold for the window of each
OOM kill, merged with the holds it overlaps; no record captured inside a hold is removed while the hold's window end is within
`retention.incidents` (or a stored incident with a note overlaps it). A stored incident's window keeps nothing by itself, and a re-run
of `vandoxd incidents` changes no incident whose window starts before the log period unless one hold contains its window. The rules are in [Retention and
rollups](../areas/retention.md).

## Consequences

- A re-run of `vandoxd incidents` after removal builds the same incidents; the charts of incidents with a kernel kill keep their data.
- An incident built from log lines only (imported data excepted, which is kept anyway) keeps its stored content, but its charts empty
  once its window data passes the periods.
- Residual of holds: only a holder of the agent token can forge a `kernel_event`; the free-space floor `ingest.min_free` bounds the
  growth (record 0177).
- A kill without a `kernel_event` (an agent that did not run its kernel collector) gets no hold; its window follows the periods.
- The forensics report counts no agent hour past `retention.logs` as observed, even inside a kept window, so kept windows do not
  weigh the base rates (spec S10).
