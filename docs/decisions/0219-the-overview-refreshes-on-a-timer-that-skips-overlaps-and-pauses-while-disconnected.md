# 0219: The overview refreshes on a 30-second timer that skips overlaps and pauses while the connection is lost

- **Status:** Proposed
- **Date:** 2026-11-16
- **Area:** Web UI
- **Source:** Issue #48
- **Supersedes:** —

## Context

The overview must refresh over its live connection without a page load (#48). Every open page holds a circuit; a lost connection
keeps its circuit for 3 minutes ([Web UI](../areas/web-ui.md), *Pages and render mode*), and the views share a read gate of two
concurrent store calls. The other views read once per user action and cancel a superseded read (`PageRunner`).

## Options considered

1. **Push on ingest** — the ingest endpoint notifies open pages after each commit; current to the second, but it couples the write
   path to the circuits, refreshes every 4 seconds per live batch, and needs a fan-out with its own bounds.
2. **Timer that cancels the running refresh** (the views' `PageRunner` behavior) — a slow store would cancel every refresh before it
   ends and the page would never update.
3. **Timer that skips a due refresh while one runs, and pauses while the connection is lost** (chosen) — one refresh per page at a
   time, none while nobody sees the page, at once when the connection is back.

## Decision

Each overview page runs its own timer on the injected clock, every 30 seconds, with a 10-second deadline per refresh; a refresh due
while one runs is skipped, none starts while the circuit's connection is down, and one starts when it comes back
([Overview page](../areas/web-ui-overview.md)).

## Consequences

- Load grows with open overview pages: at most five gate slots per page every 30 seconds; many open pages can delay the other views'
  calls within their own deadlines.
- Data shown is up to 30 seconds old plus the agent's send period; the page states `Updated <time>`.
- A connection state handler per circuit is added beside the session check.
