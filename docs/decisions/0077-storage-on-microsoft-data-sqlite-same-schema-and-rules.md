# 0077: Storage on Microsoft.Data.Sqlite with the unchanged schema, migrations and connection rules

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Storage
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

The storage layer ([0007](0007-sqlite-with-fts5-no-external-database.md), [0063](0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md),
[0066](0066-log-search-takes-literal-terms-only.md)) first existed as a Go package (issues #13, #14). The backend moves to .NET, and
databases written by the Go version must stay readable. The rules that package settled stay in force. Their reasons:

- **Migrations:** versioned and idempotent at start-up; `meta.schema_version` is the single source of the version, and `vandoxd` and `vandoxd import` may open the same file at once.
- **Connections:** concurrent writes on one pool contend for SQLite's write lock, and a deferred transaction that reads first and then writes can fail with `SQLITE_BUSY` without waiting. The ingest API acknowledges a batch only after its commit and the agent then deletes it from its spool, so a committed batch must survive a power cut (no data gaps unless recorded, 0028).
- **Fakes:** callers depend on small interfaces so higher layers can be tested with hand-written fakes (`docs/UNIT_TESTS.md`, no mocking library).

## Options considered

1. **A new schema** — free design, but no continuity for existing databases.
2. **The same schema and rules on `Microsoft.Data.Sqlite`** (chosen) — the bundled SQLite offers FTS5 and STRICT tables, and the on-disk format is the same.

Rules kept from the Go version:

- *Migrations:* an ordered list of steps in code with the version in `meta.schema_version`, rather than a migration library (new dependency, own version table) or `PRAGMA user_version` (cannot change in the same statement as the data).
- *Single writer:* one writer connection with `BEGIN IMMEDIATE` plus a query-only reader pool, rather than a mutex on a shared pool, which still lets a deferred transaction hit `SQLITE_BUSY` against another process.
- *`synchronous`:* `FULL`, because `NORMAL` in WAL mode can lose the last commits on power loss and turn an acknowledged batch into a silent gap.
- *Fakes:* small interfaces plus a scripted fake, with the real store tested once against real files, rather than interfaces declared by each consumer or an in-memory fake that reimplements deduplication and FTS matching and would drift from SQLite.
- *Throughput:* a benchmark run by hand with results in `docs/BENCHMARKS.md` and the requirement never weakened silently, rather than a unit test asserting a duration (flaky, wrong host, forbidden by 0062) or closing the issue without a measurement.

## Decision

Option 2: `Vandox.Storage` keeps schema, migrations, connection rules and the interfaces as described in the [Storage](../areas/storage.md) area.

## Consequences

- A database created by the Go version opens unchanged; an older build refuses a newer schema rather than risking its being written (going back needs a backup, #55).
- Start-up is idempotent: two processes opening the database at once apply each step once.
- Concurrent writes queue instead of failing, reads proceed while a batch is written, and a second writing process fails with `SQLITE_BUSY` only after waiting for the lock.
- Native SQLite comes with the NuGet package; the container needs no extra system library.
- Later performance criteria on the reference host add their benchmarks to `docs/BENCHMARKS.md` the same way.
