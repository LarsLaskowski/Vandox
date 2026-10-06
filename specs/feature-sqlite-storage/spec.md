# Spec: SQLite storage layer

Status: Draft

Source: issue #14 "[Backend] SQLite storage layer".

## Problem / motivation

v0.1.0 is the forensics release: Vandox imports the production server's existing logs (#15) and later the
agent's batches (#40), and reconstructs past outages from them. Until now `vandoxd` opens `vandox.db`, puts it
in WAL mode and keeps only a `meta` table with the schema version (#13). Nothing can store or read records
of the shared data model (`internal/model`), and the log search that the forensics views rely on does not
exist.

## Behavior

- At start-up `vandoxd` brings the database to the schema this build uses by applying the missing versioned
  migrations in order, each atomically. Starting again (or a second process starting at the same time)
  changes nothing. A database written by a newer build is refused with an error and left untouched, as
  before.
- The backend can store batches of records of every kind of the shared data model in one transaction each,
  together with the capture context and the receive time. A batch is stored completely or not at all. A record
  that an agent sends again (same agent ID and sequence number) is stored once and never overwrites the stored
  one; the caller learns how many records were new and how many were duplicates.
- Writes go through a single writer; concurrent callers queue instead of failing. Reads (including the
  `/healthz` check) proceed while a batch is being written. A committed batch survives a power cut.
- The backend can read stored records of one kind (and optionally one source, and for metrics one metric
  name) in a time range, in capture order, with a bounded result size, served by indexes.
- The backend can search log lines in full text: the search text is a list of words that must all occur,
  case-insensitively and ignoring diacritics; no search text can break or slow down the query engine.
- Higher layers program against small storage interfaces and test with a shared fake.
- How fast 10,000 records are written in one transaction is measurable with a benchmark, documented with the
  procedure for the reference host, the DS918+.

## Acceptance criteria

- [ ] AC1: Migrations run idempotently at start-up (new database, database of #13, second start, concurrent
  start, failing step, newer or invalid version).
- [ ] AC2: Every record kind of the shared data model can be written and read back unchanged, with its capture
  context and receive time.
- [ ] AC3: A batch is one transaction: all or nothing; resent agent records are stored once and never
  overwritten; invalid batches are rejected before anything is written.
- [ ] AC4: Single writer with WAL, `busy_timeout`, `synchronous=FULL`; reads are not blocked by a write.
- [ ] AC5: Time-range queries per kind, source and metric are served by indexes.
- [ ] AC6: FTS5 is available and covered by a test: log lines are found by literal terms; FTS5 syntax in the
  search text is treated literally; invalid search text is an error that does not echo it.
- [ ] AC7: Storage interfaces have a fake for tests.
- [ ] AC8: Writing 10,000 records in one transaction is measured by a benchmark; the procedure and the
  development host's result are noted, the DS918+ result is marked pending (follow-up issue).

## Out of scope

- Ingest API, authentication, the receive-time source (#40); log import and file hashes (#15).
- Live/backfill classification and gap objects with state (#41); rollups and retention, including deleting
  rows and the FTS5 delete trigger (#46); time-series query API with resolution (#47); backup and vacuum (#55).
- A search syntax with operators, prefixes or column filters (#26, record 0066).
- The DS918+ measurement itself (follow-up issue, record 0068).

## Open questions

None. The DS918+ measurement cannot be made by the squad; it is split into a follow-up issue (0068).
