# 0228: MariaDB status members are kept in a covering partial index and folded on the time-series step axis

- **Status:** Proposed
- **Date:** 2026-11-25
- **Area:** Web UI
- **Source:** Issue #52
- **Supersedes:** —

## Context

The services view (#52) charts MariaDB's availability, memory in use (`Memory_used`) and connections (`Threads_connected`). They are
members of the `mariadb_status` records. These records are no metrics, have no rollups and keep their payload as JSON in `records.data`
([Storage](../areas/storage.md)). Thirty days at 30 seconds are 86,400 records. A record with a thread snapshot may reach the 1 MiB record
bound, and the view also lists those snapshots. The view draws these lines beside `mariadb_*` metrics and `mem_total_bytes`, which come
from the time-series query, on one time axis.

## Options considered

1. **Read the members with `json_extract` from the payloads.** No schema change, but every read parses every payload of the range,
   among them the snapshots near 1 MiB. Rejected.
2. **A covering partial index over the members, as `records_service_state` does for services (record
   [0217](0217-service-state-members-are-indexed-for-the-restart-markers.md))** (chosen). The index covers capture time, source,
   availability, the two status values, the number of threads and the kind. The stream reads no payload, and the snapshot list finds
   the records with threads without one. Cost: one index entry computed per stored status record, and a migration step that reads every
   stored status record once.
3. **Roll the status values up in the retention job, or have the agent write them as metrics.** This would reach beyond
   `retention.snapshots`, but needs a new fold or an agent and wire change. Rejected for this issue; it is a follow-up issue.
4. **Draw the status lines at raw resolution when the metric lines are raw.** That needs a second axis rule for the status lines.
   Rejected: the view asks the time-series query for steps only (`TimeSeriesQuery.StepsOnly`) and folds the status records on the same
   steps, so every MariaDB chart shares one axis.

## Decision

Schema step 18 adds the covering partial index `records_mariadb_status`, and the status reads of the services view name exactly its
expressions. The status records are folded in C# while they stream, onto the step axis of the view's time-series query, which skips raw
resolution and markers for it. The rules are in [Services view](../areas/web-ui-services-view.md) and [Storage](../areas/storage.md).

## Consequences

- The shortest step is 5 minutes, so the *1 h* preset shows 12 points.
- Every `mariadb_status` insert computes the index entry; any other record only checks its `kind`.
- The time of the migration step on a database with 30 days of status records is measured and recorded in [Storage](../areas/storage.md).
