# 0011: Own Go web UI with historical views, no Grafana

- **Status:** Superseded by [0073](0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md)
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The main use of Vandox is looking back: what happened in the minutes before an outage, across metrics,
processes, connections and logs. The data lives in SQLite (0007).

## Options considered

1. **Grafana on top of the data** — powerful dashboards; another container, a SQLite data source plugin,
   and incident timelines that correlate logs and snapshots are hard to build in panels.
2. **Own web UI served by `vandoxd`** — exactly the historical and incident views needed, one container;
   charts and views have to be built.

## Decision

Option 2: `vandoxd` serves its own web UI, written in Go, with historical views (time ranges, incidents,
log search) and no Grafana.

## Consequences

- One container and one login (0016).
- UI features have to be implemented and tested in this repository.
- Rendering external data (log lines, process names) in the UI is a security area (injection).
