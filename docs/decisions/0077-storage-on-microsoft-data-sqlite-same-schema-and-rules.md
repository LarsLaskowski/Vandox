# 0077: Storage on Microsoft.Data.Sqlite with the unchanged schema, migrations and connection rules

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** —
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

The storage layer ([0007](0007-sqlite-with-fts5-no-external-database.md), [0063](0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md),
[0066](0066-log-search-takes-literal-terms-only.md)) first existed as a Go package on `modernc.org/sqlite`
(issues #13, #14). The backend moves to .NET, and databases written by the Go version must stay readable.
The rules that package settled stay in force and are recorded here with their reasons:

- **Driver.** 0007 requires FTS5. The image ran with `CGO_ENABLED=0` on distroless static, so a cgo driver
  (`mattn/go-sqlite3`: needs a C toolchain and a libc) and a WebAssembly one (`ncruces/go-sqlite3`: a runtime
  in the process, more memory per connection) were rejected in favor of `modernc.org/sqlite`. That choice is
  moot with .NET; the opened file is the same SQLite file.
- **Migrations.** Issue #14 asked for versioned migrations applied idempotently at start-up; `meta.schema_version`
  (created by #13, read by `/healthz`, 0059) is the single source of the version, and `vandoxd` and the
  `vandoxd import` process may open the same file at once.
- **Connections.** One unbounded pool lets concurrent writes contend for SQLite's write lock, and a deferred
  transaction that reads first and then writes can fail with `SQLITE_BUSY` without waiting. The ingest API
  acknowledges a batch only after its commit (#40) and the agent then deletes it from its spool, so a committed
  batch must survive a power cut (no data gaps unless recorded, 0028).
- **Interfaces and fakes.** Issue #14 asked for repository interfaces so higher layers can be tested with
  fakes; `docs/UNIT_TESTS.md` allows hand-written fakes behind small interfaces and no mocking library.
- **Throughput.** Issue #14's criterion: 10,000 records in one transaction in under one second on the
  reference host, a DS918+ (Intel Celeron J3455, the maintainer's NAS). `docs/UNIT_TESTS.md` forbids a real
  clock in tests and 0062 forbids comparing elapsed time with a bound. Measured on a 2.1 GHz Xeon with the
  schema of 0063: 10,000 metric records 0.12 to 0.15 s, 10,000 log lines (kernel OOM-kill lines of about 150
  bytes) 0.18 to 0.33 s with the FTS5 index filled by the write path against 0.50 to 0.79 s with an
  `AFTER INSERT` trigger; the J3455 is slower per core, so the log-line case may come close to 1 s there.

## Options considered

1. **A new schema** — free design, but no continuity for existing databases.
2. **The same schema and rules on `Microsoft.Data.Sqlite`** — the bundled SQLite offers FTS5 and STRICT tables,
   and the on-disk format is the same SQLite file.

Rules the Go version decided between alternatives (kept):

- *Migrations:* a migration library (new dependency, its own version table, SQL files to embed) and
  `PRAGMA user_version` (a pragma cannot change in the same statement as the data) were rejected for an
  ordered list of steps in code, with the version in `meta.schema_version`.
- *Single writer:* a mutex around writes on a shared pool still lets a deferred transaction hit `SQLITE_BUSY`
  against another process; chosen: one writer connection whose transactions start with `BEGIN IMMEDIATE`
  (the lock is taken at `BEGIN`, so `busy_timeout` applies) and a separate query-only reader pool (WAL readers
  never wait for the writer).
- *`synchronous`:* `NORMAL` is faster in WAL mode but can lose the last commits on power loss, turning an
  acknowledged batch into a silent gap; `FULL` syncs the WAL on every commit. Chosen: `FULL`.
- *Fakes:* interfaces declared by each consumer (every consumer writes its own fake of the same methods) and
  an in-memory fake that reimplements deduplication, filtering, ordering and FTS matching (it would drift from
  SQLite) were rejected for small interfaces plus a scripted fake; the behavior of the real store is tested
  once, against real files.
- *Throughput:* a unit test asserting the duration is flaky on shared runners and measures the wrong host;
  blocking the change until the DS918+ result exists is impossible for the squad; closing #14 with the
  criterion moved to a follow-up would mark it done without a measurement. Chosen: a benchmark run by hand, the
  results noted in `docs/BENCHMARKS.md`, the DS918+ measurement kept as its own open item, and the requirement
  never weakened silently (if the criterion fails, the next candidate is indexing FTS5 after the commit, which
  changes "a log line is searchable when its batch is committed" and needs the Product Manager).

## Decision

Option 2. `Vandox.Storage` implements the schema version 3 migrations step by step: each step runs in its own
write transaction that first re-reads the version, skips the step when the database is already at or past it
and otherwise executes the step and writes the new version in the same transaction (a failing step rolls back
completely, earlier steps stay committed); a missing `meta` table or version row is version 0; a version above
the last step or one that is not a decimal integer is refused before anything is written; a database at the
current version executes no step. Steps are never edited once released; every schema change is a new step with
a test that migrates from the previous version.

Connections: `vandox.db` is created with mode `0600` and a symbolic link or other non-regular file in place of
`vandox.db`, `-wal` or `-shm` is refused (SQLite would write database and `-wal`/`-shm` next to the resolved
target, outside `storage.directory`); a single writer connection with `BEGIN IMMEDIATE`, WAL, `foreign_keys`,
`synchronous=FULL` and a `busy_timeout` of 5 s (it only covers another process such as `vandoxd import` or an
external backup, since the process serializes its own writes), and query-only reader connections (at most four,
with their own page cache outside the managed heap, so the memory limit of 0060 keeps a margin). A batch is one
transaction of at most 20,000 records (the wire format's `MaxRecords`). Besides that: the idempotent
`records_agent_seq` deduplication and the literal-term FTS5 search of 0066.

Small interfaces (`IRecordWriter`, `IRecordReader`, `ILogSearcher`, `IImportTracker`) are what callers depend
on; the store itself is tested against real database files in a temporary directory, consumers whose behavior
depends on the store's semantics (such as "a resent batch is stored once") test against the real store, and the
importer tests use a hand-written `FakeImportStore` that records calls and returns what the test scripts (it does
not deduplicate or filter). The throughput measurement is an opt-in test (`WriteThroughputTests`, runs only with
`VANDOX_BENCHMARK=1`, prints the time of each 10,000-record batch, never asserts a duration); the DS918+ run
stays the follow-up issue (see also 0083).

## Consequences

- A database created by the Go version opens unchanged; both versions write the same schema, and an older build
  refuses a newer schema rather than risking its being written (going back needs a backup, #55).
- Start-up is idempotent: two processes opening the database at once apply each step once, because the second
  waits on the write lock and then sees the new version.
- Concurrent writes queue instead of failing with `SQLITE_BUSY`, reads proceed while a batch is written, and
  a second writing process fails with `SQLITE_BUSY` only after waiting 5 s for the lock.
- Behavior documented in 0063 and 0066 stays in force. Native SQLite comes with the NuGet package; the
  container needs no extra system library.
- Later performance criteria on the reference host add their benchmarks to `docs/BENCHMARKS.md` the same way.
