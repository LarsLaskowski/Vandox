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

## Options considered

1. **Remove by the periods, freeze old incidents against re-runs** — bounded, but charts empty after 30 days and an outage not yet
   reconstructed is lost.
2. **Run the incident reconstruction inside the retention job** — every outage gets its incident first, but a second writer of
   incidents next to the command, and the whole rebuild's cost in the service.
3. **Keep every record captured in a stored incident's window or in the window of a detected kill** (chosen) — the job runs the
   signature detector over the log lines and reads the agent's `kernel_event` OOM kills before it removes data, and stores each kill's
   window as a hold.

## Decision

Before removing data up to a time, the job scans the log lines and kernel events up to that time plus `incidents.window_before` for OOM
kills and stores a hold for each kill's window; no record captured inside a stored incident's window or a hold is removed while the
incident (or, for a hold, its window end under `retention.incidents`) is kept. The rules are in [Retention and
rollups](../areas/retention.md).

## Consequences

- A re-run of `vandoxd incidents` after removal builds the same incidents; incident charts keep their data.
- Residual: forged kill lines spaced by the window length can keep all data of their span; the free-space floor `ingest.min_free`
  bounds the growth, as for any token holder (record 0177).
- A late line (a record whose capture time is below the scan's position) is scanned on its own; an event split across a late and an
  earlier line can be missed.
- The forensics report counts no agent hour past `retention.logs` as observed, even inside a kept window, so kept windows do not
  weigh the base rates (spec S10).
