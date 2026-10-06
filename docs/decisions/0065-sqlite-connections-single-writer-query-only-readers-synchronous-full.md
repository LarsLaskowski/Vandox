# 0065: SQLite connections: modernc.org/sqlite kept; one writer connection with BEGIN IMMEDIATE, a query-only reader pool, synchronous FULL

- **Status:** Superseded by [0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md)
- **Date:** 2026-10-06
- **Source:** Issue #14
- **Supersedes:** 0057

## Context

0057 chose the pure-Go driver `modernc.org/sqlite` (no cgo, FTS5 compiled in) and fixed one connection pool
with the DSN `_pragma=journal_mode(WAL)&_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)`, plus the
`os.Lstat` checks that refuse links in place of `vandox.db`, `-wal` and `-shm`. Issue #14 again asks for "a
pure-Go SQLite driver with FTS5 support" documented in a decision record, and for WAL mode, a sensible
`busy_timeout` and a single writer with batched transactions.

With one unbounded `database/sql` pool, concurrent writes get separate connections and contend for SQLite's
write lock; a deferred transaction that reads first and then writes can fail with `SQLITE_BUSY` without
waiting. The ingest API acknowledges a batch only after its commit (#40), and the agent then deletes it from
its spool, so a committed batch must survive a power cut of the backend host (no data gaps unless recorded,
0028). SQLite's page cache is allocated outside the Go heap (0057, 0060).

## Options considered

1. **Driver** — `mattn/go-sqlite3` (cgo) and `ncruces/go-sqlite3` (WebAssembly) were rejected in 0057 for the
   reasons given there; nothing has changed. Measured on 2026-10-06 with v1.60.1 on a 2.1 GHz Xeon: 10,000
   metric records in one transaction take 0.12 to 0.15 s, 10,000 log lines 0.18 to 0.33 s with the FTS5
   index filled by the write path (0.50 to 0.79 s with an `AFTER INSERT` trigger, 0063).
2. **Single writer**
   - *A mutex around writes on the shared pool* — readers and writers still share connections, and a
     deferred transaction can still hit `SQLITE_BUSY` against another process.
   - *A writer pool limited to one connection, transactions started with `BEGIN IMMEDIATE`, and a separate
     reader pool* — writers queue in `database/sql` (honoring the context), the write lock is taken at
     `BEGIN` so `busy_timeout` applies, and WAL readers never wait for the writer.
3. **`synchronous`** — `NORMAL` is faster in WAL mode but can lose the last commits on power loss, which
   would turn an acknowledged batch into a silent gap; `FULL` (SQLite's and the driver's default) syncs the
   WAL on every commit.

## Decision

- Driver: `modernc.org/sqlite` through `database/sql`, driver name `sqlite`, unchanged from 0057.
- File checks: unchanged from 0057 (`vandox.db` created with `O_CREATE|O_EXCL`, mode `0600`, in the existing
  `storage.directory`; `os.Lstat` refuses a symbolic link or other non-regular file in place of `vandox.db`,
  `vandox.db-wal` or `vandox.db-shm`); both pools are opened only after these checks, with the DSN path built
  by `url.URL{Scheme: "file", Path: <path>}`.
- Writer pool: DSN query
  `_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)&_pragma=journal_mode(WAL)&_pragma=synchronous(FULL)&_txlock=immediate`,
  `SetMaxOpenConns(1)`; `Open` fails when `PRAGMA journal_mode` does not report `wal`. Migrations (0064) and
  every `WriteBatch` run on it; a batch is one transaction of at most `MaxBatchRecords` (20,000, the wire
  format's `MaxRecords`) records.
- Reader pool: DSN query
  `_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)&_pragma=journal_mode(WAL)&_pragma=query_only(1)&_pragma=synchronous(FULL)`,
  `SetMaxOpenConns(4)`, `SetMaxIdleConns(4)`; `Ping`, `Records` and `SearchLogs` use it. `Open` pings it
  once so a broken reader setup fails at start-up.
- `busy_timeout` stays 5 s: within the process the writer pool serializes writes, so it only covers another
  process (e.g. `vandoxd import`, #15) or an external backup holding the lock.

## Consequences

- Concurrent `WriteBatch` calls queue instead of failing with `SQLITE_BUSY`; a caller's context bounds the wait.
- Reads (health check, queries) proceed while a batch is being written.
- At most five connections, each with SQLite's default page cache of about 2 MiB, so about 10 MiB outside the
  Go heap; the margin between `GOMEMLIMIT` and `mem_limit` (0060) covers it.
- A connection that `database/sql` opens later (after a broken one is discarded) opens the path without
  re-running the file checks, as in 0057; replacing `vandox.db` with a link needs write access to the data
  volume, i.e. the container's own user.
- A second writing process waits at most 5 s for the lock; a long batch in the other process can make it fail
  with `SQLITE_BUSY`, which it reports as an error.
