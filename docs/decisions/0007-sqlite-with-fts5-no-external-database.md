# 0007: SQLite with FTS5, no external database

- **Status:** Accepted
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The backend stores metrics, rollups, snapshots, incidents and logs from one server and has to search the
logs in full text. It runs as a single container on a Docker host (typically a NAS); deployment and backups should stay
simple.

## Options considered

1. **External database** (PostgreSQL/TimescaleDB, InfluxDB, Elasticsearch/Loki for logs) — scales far
   beyond one server; one or more extra containers to run, upgrade and back up.
2. **SQLite with FTS5 in the backend process** — one file, no extra service, full-text search built in;
   one writer at a time and limits on very large volumes.

## Decision

Option 2: `vandoxd` stores everything in SQLite with the FTS5 extension for log search, in WAL mode, with
retention tiers (raw data 30 days, 5-minute rollups 1 year, hourly rollups 3 years, incidents unlimited,
logs 90 days, process and connection snapshots 30 days).

## Consequences

- Deployment is one container plus one data volume; a backup is a copy of the database file.
- Writes go through one writer; the ingest path must batch.
- Retention and rollups are the backend's own job; changing a tier means changing the backend.
- The SQLite driver must support FTS5.
