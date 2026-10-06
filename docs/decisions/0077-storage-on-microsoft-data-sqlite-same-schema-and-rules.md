# 0077: Storage on Microsoft.Data.Sqlite with the unchanged schema, migrations and connection rules

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** [0064](0064-versioned-schema-migrations-in-go-one-transaction-per-step.md), [0065](0065-sqlite-connections-single-writer-query-only-readers-synchronous-full.md), [0067](0067-storage-repository-interfaces-and-a-scripted-fake-in-storetest.md), [0068](0068-write-throughput-measured-by-a-benchmark-ds918-measurement-in-a-follow-up.md)

## Context

The storage layer ([0007](0007-sqlite-with-fts5-no-external-database.md), [0063](0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md),
[0066](0066-log-search-takes-literal-terms-only.md)) existed as a Go package on `modernc.org/sqlite`. The backend
moves to .NET, and databases written by the Go version must stay readable.

## Options considered

1. **A new schema** — free design, but no continuity for existing databases.
2. **The same schema and rules on `Microsoft.Data.Sqlite`** — the bundled SQLite offers FTS5 and STRICT tables,
   and the on-disk format is the same SQLite file.

## Decision

Option 2. `Vandox.Storage` implements the schema version 3 migrations step by step (one transaction per
step, a newer schema is refused), the single writer connection with `BEGIN IMMEDIATE`, the query-only reader
connections, WAL and `synchronous=FULL` (the pragmas of 0065), the idempotent `records_agent_seq` deduplication
and the literal-term FTS5 search of 0066. Small interfaces (`IRecordWriter`, `IRecordReader`, `ILogSearcher`,
`IImportTracker`) are what callers depend on; the store itself is tested against real database files in a
temporary directory, and the importer tests use a hand-written `FakeImportStore`. The throughput measurement of
0068 is an opt-in test (`WriteThroughputTests`, runs only with `VANDOX_BENCHMARK=1`, prints the time of each
10,000-record batch, never asserts a duration); the DS918+ run stays the follow-up issue.

## Consequences

- A database created by the Go version opens unchanged; both versions write the same schema.
- Behavior documented in 0063, 0065 (rules, not the driver) and 0066 stays in force; this record replaces only
  their Go-specific parts (migrations in Go, the Go driver, `storetest.Fake`, the Go benchmark).
- Native SQLite comes with the NuGet package; the container needs no extra system library.
