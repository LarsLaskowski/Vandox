# 0226: The network view stacks connection rollups and marks listeners new against an inventory a day older

- **Status:** Proposed
- **Date:** 2026-11-24
- **Area:** Web UI
- **Source:** Issue #51
- **Supersedes:** —

## Context

Issue #51 asks for connections per state and per program over time, the top remote endpoints of a range and a listener inventory
with the listeners that are new since the previous day highlighted. The connection rollups of #486 (record 0225) hold the states,
programs and remote addresses per bucket but no listeners; listeners exist only in the connection snapshots, which follow
`retention.snapshots`, and in the derived listener findings of the forensics report (records 0116, 0149). The snapshot stamp read of
the history view (record 0222) takes a window of at most one day, and its *at or before* side adds a temporary tree for the ID term of its
order ([Storage reading](../areas/storage-reading.md), *History reads*).

## Options considered

1. **New listeners from the stored findings** (`connection_marks`, `listener_comparisons`) of the last day — no snapshot decoded, and a
   listener that opened and closed within the day is listed; but a finding exists only after an agent start or when the agent compared
   two consecutive runs, so the inventory shown would mark nothing the agent did not report itself, and the marks would not belong to the
   inventory on screen.
2. **The shown inventory against the source's latest inventory 24 to 48 hours older**, with the agent's key and port rules
   (`ListenerComparer`) — what the page shows is what it compares, also across agent restarts; a listener that opened and closed in
   between is left to the report.
3. **Against the inventory at the start of the previous UTC calendar day** — "the previous day" read literally, but the distance
   between the two inventories then varies from 24 to 48 hours with the time of day.
4. **The latest snapshot of a range by widening the stamp read's window** — no new read, but the sorted window would grow with the
   range (a year of minute snapshots).
5. **The latest snapshot of a range by its own read**: the latest capture time of the kind and source in the range by one descending
   index seek, then the lowest record ID at that time — two seeks whatever the range.
6. **Charts and remote-address table in one action** — one deadline for the page's rollup reads; but over all 1,095 days of hourly
   buckets the two reads took 4,251 and 8,022 ms on the reference host ([Connection rollups](../areas/connection-rollups.md), *Disk and
   time*), past one 10-second deadline, and a timeout of the table would also lose the charts.
7. **The remote-address table in its own action** after the charts, at their step, under its own deadline — each read fits on its own;
   the table appears after the charts.

## Decision

The charts and the remote-address table read only the connection rollups (record 0225) on the history chart's stacked mode, as the
processes view does (record 0224); the table reads in its own action after the charts (option 7). The panel *Listeners* shows the source's latest connection snapshot in the range (option 5) or the
one closest to the moment, and marks a listener new when the source's latest snapshot captured 24 to 48 hours before it lacks the key,
by `ListenerComparer` with no cap on the new listeners (option 2). The rules are in the [Network view](../areas/web-ui-network-view.md).

## Consequences

- Over the longest range the table appears seconds after the charts, and a table timeout leaves the charts shown.
- A panel read decodes at most two snapshots; its store work does not grow with the range.
- A listener that opened and closed between the two inventories, and a changed socket UID, are not marked; the report keeps them.
- Without a snapshot 24 to 48 hours older (agent stopped then, retention removed it) nothing is marked, and the page says so.
- Picking by capture time follows a stepped clock; the summary line names the time compared with.
