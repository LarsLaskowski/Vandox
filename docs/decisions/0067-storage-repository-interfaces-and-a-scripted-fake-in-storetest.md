# 0067: The store package defines small repository interfaces; storetest.Fake is a scripted fake, the store itself is tested against real files

- **Status:** Superseded by [0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md)
- **Date:** 2026-10-06
- **Source:** Issue #14
- **Supersedes:** —

## Context

Issue #14 asks for repository interfaces "so higher layers can be tested with fakes", and for fakes of the
storage interfaces. The higher layers are the ingest API (#40), the log importer (#15), the backfill
classification (#41), the query API (#47) and the web views. `docs/UNIT_TESTS.md` allows hand-written fakes
behind small interfaces and no mocking library; `.squad/project.md` (*Test doubles*) already lists the fake
`Pinger` of `/healthz` and says the store itself is tested against a real database in `t.TempDir()`.

## Options considered

1. **Interfaces declared by each consumer, no shared fake** — idiomatic Go; but every consumer writes its own
   fake of the same three methods, and the issue asks for fakes of the storage interfaces.
2. **Interfaces in `store`, an in-memory fake that reimplements the store's semantics** (deduplication,
   filtering, ordering, FTS matching) — consumers could test against "almost the store", but the fake's FTS
   tokenization and ordering would drift from SQLite's, and tests would trust behavior that only the fake has.
3. **Interfaces in `store`, a scripted fake in `storetest`** — the fake records every call and returns what
   the test scripts (results, errors, blocking); behavior of the real store is tested once, in `store`.

## Decision

Option 3.

- `store` declares `Writer` (`WriteBatch`), `RecordReader` (`Records`) and `LogSearcher` (`SearchLogs`);
  `*store.Store` implements all three (compile-time assertions).
- `cmd/vandoxd/internal/store/storetest` provides `Fake`: optional hooks `OnWrite`, `OnRecords`, `OnSearch`
  script results and errors (default: every record stored, no records, no hits); `Block` makes every call
  wait until it is closed or the context is done; `Batches`, `RecordQueries` and `LogSearches` return copies
  of the recorded calls. It is safe for concurrent use.
- Consumers whose behavior depends on the store's semantics (e.g. #40's "a resent batch is stored once")
  test that against the real store in `t.TempDir()`, not against the fake.

## Consequences

- Consumers depend on the interface they need, and a test can script a failing or hanging database.
- The fake does not deduplicate or filter; a test that needs that uses the real store.
- `storetest` is production code for the coverage gate and has its own tests.
