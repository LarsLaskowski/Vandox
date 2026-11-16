# 0222: The history view reads the moment's snapshots and the metric list through its own bounded reads

- **Status:** Proposed
- **Date:** 2026-11-16
- **Area:** History view
- **Source:** Issue #49
- **Supersedes:** —

## Context

Issue #49 asks that a click in the chart open the processes and connections of that moment, and that any stored metric can be
chosen. The processes view (#50) and the network view (#51) do not exist yet. No store read lists the stored metrics, and metric
names come from agents and imports and are kept for years as hourly rollups. The existing record read
([Storage reading](../areas/storage-reading.md), *Records*) reads a kind forward in time and decodes every
record it returns, so finding the snapshot closest to a moment with it would decode every snapshot of the window (up to 40 per kind
in 10 minutes under memory pressure, more with several sources), each up to the payload limit.

## Options considered

1. **A panel in the history view, fed by bounded reads per kind** (the ID and time of the latest snapshot at or before the moment and
   of the earliest after it, within 10 minutes, one row each over `records(kind, captured_at)`, then the closest one's record by ID),
   **and a skip scan for the metric list** (one index seek per listed metric over `metrics(source, name, …)` and the hourly rollups'
   primary key, capped at 256).
2. **Both sides read as full records** and the closer kept — holds two decoded payloads of up to the payload limit per kind, and the
   `AtOrBefore` sort holds whole rows.
3. **Links to the processes and network views** — those views do not exist; the history view would ship without the jump the issue
   asks for.
4. **The existing record read for the snapshots** — decodes every snapshot of the window to keep one.
5. **The metric list by `SELECT DISTINCT`** — reads every row of the metric tables when the planner does not skip, unbounded in the
   data kept.
6. **A fixed catalog of the shared metric names** — misses imported sources and new agent metrics.

## Decision

Option 1. The panel shows the snapshot of each kind closest to the moment within 10 minutes, chosen from the ID and time of one row
per side and then read alone, one kind after the other, so one decoded snapshot is held at a time; the list of metrics is a skip scan of the raw values and the hourly rollups, at most 256 entries; both run through the views' read gate. The
rules are in [History view](../areas/web-ui-history-view.md) and
[Storage reading](../areas/storage-reading.md).

## Consequences

- A moment costs up to six store calls (per kind two ID-and-time reads and one record read), and one decode per kind.
- A snapshot removed by retention between the reads shows as none within 10 minutes.
- The list's cost grows with the number of listed metrics (at most 257 seeks per table), not with the stored rows.
- A host with more than 256 metrics lists the first 256 in order; the others can be named in the address.
- The processes and network views may later link to a moment of the history view instead of reading snapshots themselves.
