# Plan: SQLite storage layer

Source: Issue #14 | [spec.md](spec.md)
Status: Draft
Tier: security — the store becomes the place that enforces "a resent record is stored once and never
overwrites stored data" (*Security areas* 1), it changes how `vandox.db` is opened (area 9), and it adds a
guard on search text that FTS5's query parser consumes (area 10).

## Problem / root cause

Summary of [spec.md](spec.md): `vandoxd` needs a storage layer for every record kind of `internal/model`,
with versioned migrations at start-up, a single writer with batched transactions, indexes for time-range
queries, FTS5 log search, repository interfaces with a fake, and a measured write throughput.

Current code (`cmd/vandoxd/internal/store/store.go`, from #13):

- `Open` (l. 39) checks the directory and the file (`prepareFile`, `requireRegularIfPresent`, 0057), opens
  **one** `database/sql` pool with `dsnQuery` (l. 29: WAL, `busy_timeout(5000)`, `foreign_keys(1)`) and calls
  `initialize` (l. 116), which creates `meta`, inserts `schema_version = '1'` with `INSERT OR IGNORE` and
  refuses every other value (l. 134) — an older version would be refused just like a newer one.
- The pool has no connection limit: there is no single writer, and transactions are deferred.
- No record table, no FTS5 table, no read or write API exists.

Claims of the issue, checked:

| Claim | Result |
| ----- | ------ |
| "Pure-Go SQLite driver with FTS5 support; document the choice in a decision record" | **Already done by #13**: `modernc.org/sqlite v1.60.1` is in `go.mod`, record 0057 documents it, `TestStore_SupportsFTS5` creates an FTS5 table. No dependency changes. 0057 is superseded by 0065 only because the connection settings change; the driver choice is restated unchanged. |
| "WAL mode, sensible busy_timeout" | **Confirmed present** (l. 29, checked l. 117–123). `synchronous` is not set; the driver's default is `FULL` (verified: `PRAGMA synchronous` = 2) — made explicit (0065). |
| "single writer with batched transactions" | **Refuted for the current code**: one unbounded pool, deferred transactions. |
| "Schema for all record types, versioned migrations" | **Refuted**: only `meta`; versioning is a single fixed value. |
| "/healthz checks database reachability" (#13) | **Confirmed**: `server.pingChecker.ping` (`health.go` l. 80) calls `Store.Ping` (`store.go` l. 155), which reads `meta.schema_version` (0059). Kept; `Ping` moves to the reader pool. |
| "10,000 records in one transaction under one second on a DS918+" | **Cannot be measured here, so this change does not fulfil it.** Measured on this session's 2.1 GHz Xeon (4 vCPU) with prototypes of the planned schema and writer DSN, several successive 10,000-record batches into one database: metric records 0.12–0.15 s; 9,000 metric + 1,000 log lines 0.13–0.20 s; kernel OOM-kill lines (~150 bytes) 0.18–0.33 s with the FTS5 index filled by the write path (revised design, 0063), 0.50–0.79 s with the `AFTER INSERT` trigger of the first draft (the Devil's Advocate measured 0.53–0.64 s; the first draft's "~0.45 s" was too low). The J3455 is slower per core by an unmeasured factor. #14 stays open; the DS918+ measurement is a follow-up issue (0068). |

Facts verified with the pinned driver (v1.60.1, SQLite 3.53.4) that the plan relies on:

- `_txlock=immediate` is a supported DSN parameter; `_pragma` values are applied sorted (busy_timeout first).
- A `query_only(1)` connection reports `journal_mode` `wal` and refuses writes ("attempt to write a readonly
  database").
- `INSERT ... ON CONFLICT DO NOTHING` on the partial unique index: a duplicate gives `RowsAffected() == 0`
  and a **stale** `LastInsertId()` (the previous row's id) — the Dev must use `RowsAffected` (or
  `RETURNING id`) to detect duplicates, never `LastInsertId` alone.
- Import records with `agent_id`/`seq` `NULL` and origin `import` are not constrained by the partial index.
- FTS5 with quoted terms: `"OR"`, `"NEAR(a b)"`, `"kill*"`, `"a""b"` are literal (no syntax error, no
  operator); `""` and `"--"` match nothing; a NUL inside the expression gives "unterminated string";
  `"anon-rss"` matches as a phrase; `"MARIADBD"` matches `mariadbd`; `"grosse"` does not match `Größe`.
- `EXPLAIN QUERY PLAN` of the planned queries: `SEARCH m USING INDEX metrics_source_name_time (source=? AND
  name=? AND captured_at>? AND captured_at<?)` and `SEARCH records USING COVERING INDEX
  records_kind_source_time (...)`, without `USE TEMP B-TREE` for `ORDER BY captured_at, id`.
- The log search, in contrast, runs `SCAN log_fts VIRTUAL TABLE INDEX 0:M1`, primary-key lookups of every
  hit, the time and source filter afterwards, and `USE TEMP B-TREE FOR ORDER BY` (Devil's Advocate:
  300,000 lines containing `mariadbd`, 60-second window, 77 ms). Its cost grows with the term's hits over the
  whole retention; the bound is the caller's context deadline (0066).
- Filling `log_fts` by an explicit `INSERT INTO log_fts(rowid, message) VALUES(?, ?)` per log line in the
  write transaction (interleaved or at the end) gives the same index as the trigger (FTS5
  `INSERT INTO log_fts(log_fts, rank) VALUES('integrity-check', 1)` passes, 200,000 of 200,000 lines found)
  at about a third of the cost. A `log_lines` row without its index entry makes that `integrity-check` fail
  ("database disk image is malformed").

## Acceptance criteria

All tests use a real database in `t.TempDir()`, no real clock (times are fixed values), no network.

### Migrations (`migrate.go`, 0064)

- [ ] AC-M1: `Open` on an empty directory creates a database at `SchemaVersion` (= 2): `meta.schema_version`
  is `"2"`, and `sqlite_master` contains the tables `meta`, `records`, `metrics`, `log_lines`, `log_fts`,
  the indexes `records_agent_seq`, `records_kind_source_time`, `records_kind_time`,
  `metrics_source_name_time`, and no trigger (0063: the index is filled by the write path).
- [ ] AC-M2: Opening the same database a second time applies no step: the `sqlite_master` rows (type, name,
  sql) and all data written before are unchanged.
- [ ] AC-M3: A database in the #13 layout (only `meta` with `schema_version = '1'` and an extra row
  `('probe','kept')`) is migrated to version 2 by `Open`; the extra row is kept.
- [ ] AC-M4: A database with `schema_version` `SchemaVersion+1` is refused by `Open` with an error, and
  afterwards still has that version and unchanged `sqlite_master` rows (existing
  `TestOpen_RefusesNewerSchema`, adapted).
- [ ] AC-M5: A database whose `schema_version` is not a decimal integer (`"x"`, `""`, `"1.5"`, `"-1"`) is
  refused with an error, unchanged.
- [ ] AC-M6: `migrate` with a step list whose step 2 contains a failing statement (after a valid one that
  creates a table): returns an error; the version is 1; the table created by the failing step's first
  statement does not exist (whole step rolled back); step 1's table exists.
- [ ] AC-M7: Two `Open` calls on the same new directory from two goroutines at the same time both succeed;
  the version is 2 and each table exists once (no "already exists" error).
- [ ] AC-M8: `migrations` is ordered with versions 1, 2, … without gaps, and the last version equals
  `SchemaVersion`.

### Connections (`store.go`, 0065)

- [ ] AC-C1: On the writer pool `PRAGMA journal_mode` is `wal`, `PRAGMA synchronous` is `2` (FULL),
  `PRAGMA busy_timeout` is `5000`, `PRAGMA foreign_keys` is `1`; on the reader pool the same plus
  `PRAGMA query_only` is `1`, and an `INSERT` through the reader pool fails.
- [ ] AC-C2: While a write transaction is held open on the writer pool (test begins it through `s.db`),
  `Records` and `Ping` return successfully (not blocked by the writer).
- [ ] AC-C3: 8 goroutines calling `WriteBatch` concurrently (each with distinct sequence numbers) all
  succeed (no `SQLITE_BUSY`), and the stored count is the sum.
- [ ] AC-C4: `Close` closes both pools: afterwards `Ping`, `Records` and `WriteBatch` return errors; `Close`
  returns nil on an open store. The existing tests of `store_test.go` (file mode 0600, link refusal, special
  characters, non-database content, restart) keep passing.

### Writing (`write.go`, 0063)

- [ ] AC-W1: Round trip, one table-driven case per kind (metric with and without labels and unit, process
  snapshot, connection snapshot, service state, MariaDB status, kernel event `oom_kill` and `boot`, log line
  with and without priority, gap): `WriteBatch` then `Records` returns a `StoredRecord` whose `Record` equals
  the written one (`reflect.DeepEqual`; fixture times built with `time.Date(..., time.UTC)`; maps and slices
  either nil or non-empty — an empty non-nil `Labels` map reads back as nil, tested separately), and whose
  `AgentID`, `BootID`, `ClockOffset` (nil and non-nil) and `ReceivedAt` equal the batch's; `ID > 0`.
- [ ] AC-W2: `WriteResult` counts: a batch of 3 new agent records → `{Stored: 3, Duplicates: 0}`; the same
  batch again → `{0, 3}`; a batch with seq 3 (stored before, with different payload) and 4 (new) →
  `{1, 1}` and the stored record for seq 3 still has its **original** payload and receive time (never
  overwritten); the same seq twice within one batch → stored once, counted once as duplicate; the same seq
  under another agent ID → stored.
- [ ] AC-W3: Records of origin `import` and `backend` (seq 0) are never deduplicated: the same record written
  twice is stored twice.
- [ ] AC-W4: Atomicity: with a test trigger `BEFORE INSERT ON records WHEN new.seq = 5000 BEGIN SELECT
  RAISE(ABORT, 'injected'); END` created through `s.db`, a 10,000-record batch returns an error and no record
  of it is stored.
- [ ] AC-W5: 10,000 records in one `WriteBatch` call (5,000 metric, 5,000 log lines) → `{Stored: 10000}`,
  and `Records`/`SearchLogs` find all of them (functional part of the issue's performance criterion; no
  timing assertion, 0068).
- [ ] AC-W6: Rejected before anything is written (error wraps `ErrInvalidBatch`, table-driven, and the
  record count is unchanged afterwards): no records; more than `MaxBatchRecords` records; zero or non-UTC
  `ReceivedAt`; `ReceivedAt` or a `CapturedAt` before `time.Unix(0, math.MinInt64)` or after
  `time.Unix(0, math.MaxInt64)`; a record failing `model.Record.Validate` (error also wraps
  `model.ErrInvalid` and names `records[<i>]`); an origin-`agent` record with empty `AgentID`; an invalid
  non-empty `AgentID` (`wire.ValidateAgentID`); `Seq > math.MaxInt64`. The boundary values
  `time.Unix(0, math.MinInt64)` and `time.Unix(0, math.MaxInt64)` themselves are accepted.
- [ ] AC-W7: A cancelled context → error wrapping `context.Canceled`, nothing stored. A closed store → error.
- [ ] AC-W8: `MaxBatchRecords == wire.DefaultLimits().MaxRecords`.
- [ ] AC-W9: An error from `WriteBatch` does not contain a log message, metric label value or other payload
  text of the batch (sentinel in a log message of an otherwise invalid record).
- [ ] AC-W10: FTS5 index in step with `log_lines` (0063): after writing, in one test, a batch of log lines
  and metrics, the same batch again (all duplicates), a batch that contains one log line's seq twice, and the
  failing batch of AC-W4, `INSERT INTO log_fts(log_fts, rank) VALUES('integrity-check', 1)` through `s.db`
  returns nil and `SELECT count(*) FROM log_fts_docsize` equals `SELECT count(*) FROM log_lines`. Negative
  control: after inserting a `records` row and a `log_lines` row directly through `s.db` (no index entry),
  the same `integrity-check` returns an error.

### Reading (`read.go`, 0063)

- [ ] AC-R1: `Records` returns only records of the queried kind, of `Source` when set, of `Name` when set
  (metrics), with `From <= CapturedAt < To`, ordered by `CapturedAt` then `ID`, at most `Limit`.
- [ ] AC-R2: Rejected with `ErrInvalidQuery` (table-driven): unknown or empty `Kind`; zero `From` or `To`;
  `From` not before `To`; `From`/`To` outside the storable range; `Limit < 1` or `> MaxQueryLimit`; `Name`
  with a kind other than `metric`; `Name` without `Source`.
- [ ] AC-R3: `EXPLAIN QUERY PLAN` of `recordsQuery` uses `metrics_source_name_time` for a metric query with
  source and name, `records_kind_source_time` for kind and source, `records_kind_time` for kind only; none of
  the three plans contains `USE TEMP B-TREE`.
- [ ] AC-R4: A cancelled context → error; a closed store → error.

### Log search (`read.go`, 0066)

- [ ] AC-S1: FTS5 covered by a test: after writing log lines through `WriteBatch`, `SearchLogs` finds a line by
  one word, by two words that both occur (and not when only one occurs), case-insensitively (`MARIADBD`
  finds `mariadbd`), ignoring diacritics (`fehlerubersicht` finds `Fehlerübersicht`), by a term with
  punctuation as a phrase (`anon-rss`); filtered by `Source` and `[From, To)`, ordered by `CapturedAt` then
  `ID`, at most `Limit`. The existing `TestStore_SupportsFTS5` stays.
- [ ] AC-S2: `ftsQuery`, table-driven over the accepted forms (see *Guard: search text* below): each input
  maps to the exact expected expression or to an error wrapping `ErrInvalidQuery`.
- [ ] AC-S3: `SearchLogs` with each FTS5 operator form as text (`OR`, `NOT`, `AND`, `NEAR(a b)`, `kill*`,
  `^start`, `message:x`, `{message}:x`, `-message:x`, `a+b`, `(a)`, `"quoted"`, `a"b`) returns no SQL error;
  where the literal words occur in a stored line it finds that line, and `kill*` does not find `killed`.
- [ ] AC-S4: Rejected with `ErrInvalidQuery`: the `RecordQuery` rules of AC-R2 that apply (time range,
  limit), and the text rules; the error text never contains the search text (sentinel test).
- [ ] AC-S5: A cancelled context → error wrapping `context.Canceled`; a closed store → error. (The context
  is the only bound on a search's duration, 0066.)

### Interfaces and fake (`repository.go`, `storetest/fake.go`, 0067)

- [ ] AC-F1: `*store.Store` implements `store.Writer`, `store.RecordReader`, `store.LogSearcher`, and
  `*storetest.Fake` implements all three (compile-time assertions in the production files).
- [ ] AC-F2: `Fake` without hooks: `WriteBatch` returns `{Stored: len(b.Records)}`, `Records` and
  `SearchLogs` return no records and nil; every call is recorded and returned by `Batches`,
  `RecordQueries`, `LogSearches` in call order, as copies (changing the caller's `Records` slice after the
  call, or the returned slice, does not change the recorded batch).
- [ ] AC-F3: Hooks `OnWrite`, `OnRecords`, `OnSearch` receive the argument and their result and error are
  returned unchanged.
- [ ] AC-F4: With `Block` set, a call is recorded, then waits until `Block` is closed (then proceeds
  normally) or its context is done (then returns `ctx.Err()`); synchronized on channels only, no sleep.
- [ ] AC-F5: Concurrent calls from several goroutines are safe under `-race`.

### Benchmark (`write_test.go`, `docs/BENCHMARKS.md`, 0068)

- [ ] AC-B1: `BenchmarkStore_WriteBatch` with sub-benchmarks `metric`, `log_line`, `mixed` (9,000 metric +
  1,000 log lines) writes 10,000 new records per iteration in one `WriteBatch` call (fresh sequence numbers
  per iteration, store created outside the timed section; the database keeps the earlier iterations' records,
  so the index grows as in production) and reports `records/s` via `b.ReportMetric`. Log lines are the
  kernel OOM-kill line with numbers varying per record (`Out of memory: Killed process <pid> (mariadbd)
  total-vm:<n>kB, anon-rss:<n>kB, file-rss:0kB, shmem-rss:0kB UID:27 pgtables:<n>kB oom_score_adj:0`).
  `go test -run '^$' -bench BenchmarkStore_WriteBatch -benchtime 3x ./cmd/vandoxd/internal/store` passes.
- [ ] AC-B2: `docs/BENCHMARKS.md` exists with the procedure (below) and the development host's measured
  results (CPU model, date, the three ns/op values, labelled as the development host, not the reference
  host), and a DS918+ row marked *pending* with the follow-up issue's number and the note that #14's
  criterion stays open until this row is filled.

## Approach

1. **Store and connections** (`store.go`): keep the file checks unchanged. After them open the writer pool
   (`writerQuery`, `SetMaxOpenConns(1)`), check WAL, run `migrate(ctx, s.db, migrations)`, then open the
   reader pool (`readerQuery`, `SetMaxOpenConns(maxReaders)`, `SetMaxIdleConns(maxReaders)`) and ping it with
   `schemaVersion`. On any failure close what was opened. `Ping` reads through the reader pool. `Close`
   closes the reader then the writer and returns `errors.Join` of both, wrapped as today
   (`store: closing database: ...`).
2. **Migrations** (`migrate.go`): as 0064. For each step: `BeginTx` (writer DSN makes it `BEGIN IMMEDIATE`),
   `currentVersion` inside the transaction, return an error if it exceeds the last step's version, skip if
   `>= step.version`, else exec each statement and upsert `meta.schema_version` (`INSERT ... ON CONFLICT(key)
   DO UPDATE SET value = excluded.value` — on `meta` only, never on record tables), commit. Step 1:
   `CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT` (the #13 statement).
   Step 2: the schema of 0063 exactly:

   ```sql
   CREATE TABLE records (
     id INTEGER PRIMARY KEY,
     kind TEXT NOT NULL,
     origin TEXT NOT NULL,
     source TEXT NOT NULL,
     agent_id TEXT,
     seq INTEGER,
     captured_at INTEGER NOT NULL,
     received_at INTEGER NOT NULL,
     boot_id TEXT,
     clock_offset_ns INTEGER,
     data TEXT
   ) STRICT;
   CREATE UNIQUE INDEX records_agent_seq ON records(agent_id, seq) WHERE origin = 'agent';
   CREATE INDEX records_kind_source_time ON records(kind, source, captured_at);
   CREATE INDEX records_kind_time ON records(kind, captured_at);
   CREATE TABLE metrics (
     record_id INTEGER PRIMARY KEY REFERENCES records(id),
     source TEXT NOT NULL,
     name TEXT NOT NULL,
     captured_at INTEGER NOT NULL,
     value REAL NOT NULL,
     unit TEXT NOT NULL,
     labels TEXT
   ) STRICT;
   CREATE INDEX metrics_source_name_time ON metrics(source, name, captured_at);
   CREATE TABLE log_lines (
     record_id INTEGER PRIMARY KEY REFERENCES records(id),
     log TEXT NOT NULL,
     program TEXT NOT NULL,
     pid INTEGER NOT NULL,
     priority INTEGER,
     message TEXT NOT NULL,
     truncated INTEGER NOT NULL
   ) STRICT;
   CREATE VIRTUAL TABLE log_fts USING fts5(message, content='log_lines', content_rowid='record_id',
     tokenize='unicode61 remove_diacritics 2');
   ```

   (`unit` and `program` store `""` as `''`; `priority` `NULL` for nil; `truncated` 0/1.) No trigger: the
   index is filled by `WriteBatch` (step 3, 0063).
3. **Writing** (`write.go`): validate the whole batch first (AC-W6; nothing is written on a validation
   error). Then one transaction on the writer pool: prepared statements for `records` (`INSERT ... ON
   CONFLICT DO NOTHING`), `metrics`, `log_lines` and `log_fts` (`INSERT INTO log_fts(rowid, message)
   VALUES(?, ?)`); per record insert into `records` (`seq` `NULL` unless origin agent; `data` =
   `json.Marshal(r.Data)` for kinds other than metric and log line, else `NULL`); if `RowsAffected() == 0`
   count a duplicate and skip the payload row (and the index entry), else insert the payload row with the new
   id, and for a log line its index entry with the same id and the same message, right after it.
   `WriteBatch` is the only code that inserts into `log_lines`. Commit; on any error roll back and return it wrapped (`store: writing batch: %w`). The error never
   includes payload values (model errors carry field paths only).
4. **Reading** (`read.go`): validate the query (AC-R2), build SQL with `recordsQuery` (metric with name →
   `metrics` joined to `records`; otherwise `records` filtered by kind/source, left-joined to `metrics` or
   `log_lines` as needed), scan into `StoredRecord` (times via `time.Unix(0, ns).UTC()`, `seq` `NULL` → 0,
   JSON payloads unmarshalled into a new value of the kind's type, labels JSON → map or nil). `SearchLogs`
   validates time range and limit like `Records`, builds the `MATCH` expression with `ftsQuery`, and selects
   from `log_fts` joined to `log_lines` and `records`, `WHERE log_fts MATCH ?`, time range, optional source,
   `ORDER BY records.captured_at, records.id LIMIT ?`. Kind-to-payload mapping is a local `switch` in the
   store (wire's `kinds` map stays unexported and unchanged).
5. **Interfaces and fake**: `repository.go`, `storetest/fake.go` as below.
6. **Benchmark and docs**: `BenchmarkStore_WriteBatch`; `docs/BENCHMARKS.md`; documentation updates below.

`cmd/vandoxd/serve.go` and `cmd/vandoxd/internal/server` do not change: `store.Open`, `Created`, `Ping` and
`Close` keep their signatures, and `*store.Store` still satisfies `server.Pinger`.

### Guard: search text (`ftsQuery`, 0066)

The consumer is SQLite FTS5's query parser (sqlite.org/fts5.html §3, "Full-text Query Syntax"), fed through
a bound parameter (so no SQL injection is possible; the guard is about the FTS5 language). Rules, in order:
invalid UTF-8 → error; more than `MaxSearchBytes` (1024) bytes → error; any rune that is not white space
(`unicode.IsSpace`) and is of category Cc or Cf → error; split with `strings.Fields`; 0 terms or more than
`MaxSearchTerms` (16) → error; a term with no rune for which `unicode.IsLetter || unicode.IsDigit` → error;
else each term → `"` + `strings.ReplaceAll(term, `"`, `""`)` + `"`, joined by one space. Errors are
`fmt.Errorf("%w: <rule>", ErrInvalidQuery)` with a fixed rule text (e.g. `search text has more than 16
terms`), never the text or a term. Behavior on every form FTS5 accepts:

| Form FTS5 accepts | Example text | Guard result | Effect |
| ----------------- | ------------ | ------------ | ------ |
| bareword | `oom` | `"oom"` | token match |
| several barewords (implicit AND) | `out memory` | `"out" "memory"` | both must occur |
| string with `""` escape | `a"b`, `"quoted"` | `"a""b"`, `"""quoted"""` | literal, quote is a separator for the tokenizer |
| prefix `*` | `kill*` | `"kill*"` | literal; `*` dropped by tokenizer, exact token `kill` only |
| initial token `^` | `^start` | `"^start"` | literal |
| concatenation `+` | `a+b` / `a + b` | `"a+b"` / **error** (the term `+` has no letter or digit) | phrase `a b` / refused |
| `NEAR(...)`, `NEAR(a b, 5)` | `NEAR(a` `b)` | `"NEAR(a" "b)"` | literal tokens `near`, `a`, `b` |
| `AND`, `OR`, `NOT` (upper case only are operators) | `OR` | `"OR"` | literal token `or` |
| parentheses | `(a)` | `"(a)"` | literal |
| column filter `col:`, `{a b}:`, `-col:` | `message:x`, `{message}:x`, `-message:x` | `"message:x"` etc. | literal tokens |
| punctuation-only term | `--`, `*`, `"` | **error** | would match nothing |
| empty or white space only | ``, `  ` | **error** | — |
| NUL and other C0/C1 controls (Cc) that are not white space | `a\x00b`, `a\x1bb` | **error** | NUL would end the expression ("unterminated string") |
| format characters (Cf): BOM U+FEFF, ZWSP U+200B, bidi controls | `﻿oom` | **error** | invisible characters |
| white space, also Cc white space and Unicode spaces (`\t`, `\n`, `\r`, U+0085, U+00A0, U+2028, U+3000) | `a\tb`, `a b` | `"a" "b"` | separator (`unicode.IsSpace`, `strings.Fields`) |
| letters with diacritics, non-Latin scripts | `Größe`, `ошибка` | `"Größe"`, `"ошибка"` | tokenizer folds case and diacritics |
| invalid UTF-8 | `"\xff"` | **error** | — |
| over 1024 bytes / over 16 terms | — | **error** | bounds parser work |

`\t`, `\n`, `\r` and U+0085 are Cc **and** white space: they separate terms (a pasted multi-line text works),
which the Tester asserts explicitly next to `\x00` and `\x1b` being refused.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `cmd/vandoxd/internal/store` | `store.go` | **rewrite parts**: `SchemaVersion` 1 → 2; `dsnQuery` and `dsn(path)` replaced by `writerQuery`, `readerQuery`, `dsn(path, query)`; `Store` gains `read`; `Open` opens two pools and calls `migrate`; `initialize` keeps only the WAL check and calls `migrate` (its `INSERT OR IGNORE`/equality check at l. 124–136 is removed); `Ping` on the reader; `Close` closes both |
| | `migrate.go` (new) | `migration`, `migrations`, `migrate`, `currentVersion` |
| | `write.go` (new) | `Batch`, `WriteResult`, `MaxBatchRecords`, `ErrInvalidBatch`, `WriteBatch` |
| | `read.go` (new) | `RecordQuery`, `LogSearch`, `StoredRecord`, `MaxQueryLimit`, `MaxSearchTerms`, `MaxSearchBytes`, `ErrInvalidQuery`, `Records`, `SearchLogs`, `recordsQuery`, `ftsQuery` |
| | `repository.go` (new) | `Writer`, `RecordReader`, `LogSearcher`, compile-time assertions |
| `cmd/vandoxd/internal/store/storetest` (new package) | `fake.go` | `Fake` |
| `cmd/vandoxd` | `serve.go` | none |
| `internal/model`, `internal/wire` | — | none; `write.go` imports `wire.ValidateAgentID`, `write_test.go` uses `wire.DefaultLimits` (AC-W8) |

## Signatures (for the Dev's skeleton)

Package `store` (`cmd/vandoxd/internal/store`):

```go
// store.go
const SchemaVersion = 2

const (
	writerQuery = "_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)&_pragma=journal_mode(WAL)&_pragma=synchronous(FULL)&_txlock=immediate"
	readerQuery = "_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)&_pragma=journal_mode(WAL)&_pragma=query_only(1)&_pragma=synchronous(FULL)"
	maxReaders  = 4
)

type Store struct {
	db      *sql.DB // writer pool, one connection
	read    *sql.DB // query-only reader pool
	created bool
}

func Open(ctx context.Context, dir string) (*Store, error)     // unchanged signature
func dsn(path, query string) string                           // changed: was dsn(path string)
func initialize(ctx context.Context, db *sql.DB) error         // unchanged signature, new body
func schemaVersion(ctx context.Context, db *sql.DB) (string, error) // unchanged
func (s *Store) Created() bool                                 // unchanged
func (s *Store) Ping(ctx context.Context) error                // unchanged signature, reader pool
func (s *Store) Close() error                                  // unchanged signature, both pools

// migrate.go
type migration struct {
	version int      // the schema version after this step
	stmts   []string // executed in order in one transaction
}

var migrations = []migration{ /* {1, ...meta}, {2, ...0063 schema} */ }

func migrate(ctx context.Context, db *sql.DB, steps []migration) error
func currentVersion(ctx context.Context, tx *sql.Tx) (int, error) // 0 when meta or its row is missing

// write.go
const MaxBatchRecords = 20000

var ErrInvalidBatch = errors.New("store: invalid batch")

// Batch is a set of records written in one transaction, with the context shared by all of them.
type Batch struct {
	AgentID     string         // required when a record has origin agent; "" otherwise allowed
	BootID      string         // optional; stored as NULL when ""
	ClockOffset *time.Duration // optional
	ReceivedAt  time.Time      // required, UTC
	Records     []model.Record
}

// WriteResult counts the records of a batch that were stored and those already present.
type WriteResult struct {
	Stored     int
	Duplicates int
}

func (s *Store) WriteBatch(ctx context.Context, b Batch) (WriteResult, error)

// read.go
const (
	MaxQueryLimit  = 10000
	MaxSearchTerms = 16
	MaxSearchBytes = model.MaxShortTextBytes
)

var ErrInvalidQuery = errors.New("store: invalid query")

// RecordQuery selects records of one kind in the half-open time range [From, To).
type RecordQuery struct {
	Kind   model.Kind // required
	Source string     // optional
	Name   string     // optional, metric name; only with Kind metric and a Source
	From   time.Time  // required
	To     time.Time  // required, after From
	Limit  int        // 1 to MaxQueryLimit
}

// LogSearch selects log lines that contain every term of Text in the half-open time range [From, To).
type LogSearch struct {
	Text   string    // literal terms, see ftsQuery
	Source string    // optional
	From   time.Time // required
	To     time.Time // required, after From
	Limit  int       // 1 to MaxQueryLimit
}

// StoredRecord is a record as stored, with its storage context.
type StoredRecord struct {
	ID          int64
	Record      model.Record
	AgentID     string
	BootID      string
	ClockOffset *time.Duration
	ReceivedAt  time.Time
}

func (s *Store) Records(ctx context.Context, q RecordQuery) ([]StoredRecord, error)
func (s *Store) SearchLogs(ctx context.Context, q LogSearch) ([]StoredRecord, error)
func recordsQuery(q RecordQuery) (query string, args []any) // q already validated; used by AC-R3
func ftsQuery(text string) (string, error)                  // errors wrap ErrInvalidQuery

// repository.go
type Writer interface {
	WriteBatch(ctx context.Context, b Batch) (WriteResult, error)
}
type RecordReader interface {
	Records(ctx context.Context, q RecordQuery) ([]StoredRecord, error)
}
type LogSearcher interface {
	SearchLogs(ctx context.Context, q LogSearch) ([]StoredRecord, error)
}
var (
	_ Writer       = (*Store)(nil)
	_ RecordReader = (*Store)(nil)
	_ LogSearcher  = (*Store)(nil)
)
```

Package `storetest` (`cmd/vandoxd/internal/store/storetest/fake.go`):

```go
// Fake is a scripted stand-in for store.Writer, store.RecordReader and store.LogSearcher. It is safe for
// concurrent use; set the hook and Block fields before the first call.
type Fake struct {
	OnWrite   func(b store.Batch) (store.WriteResult, error)          // nil: every record stored
	OnRecords func(q store.RecordQuery) ([]store.StoredRecord, error) // nil: no records
	OnSearch  func(q store.LogSearch) ([]store.StoredRecord, error)   // nil: no hits
	Block     chan struct{} // non-nil: every call waits until closed or ctx is done

	mu       sync.Mutex
	batches  []store.Batch
	queries  []store.RecordQuery
	searches []store.LogSearch
}

func (f *Fake) WriteBatch(ctx context.Context, b store.Batch) (store.WriteResult, error)
func (f *Fake) Records(ctx context.Context, q store.RecordQuery) ([]store.StoredRecord, error)
func (f *Fake) SearchLogs(ctx context.Context, q store.LogSearch) ([]store.StoredRecord, error)
func (f *Fake) Batches() []store.Batch
func (f *Fake) RecordQueries() []store.RecordQuery
func (f *Fake) LogSearches() []store.LogSearch

var (
	_ store.Writer       = (*Fake)(nil)
	_ store.RecordReader = (*Fake)(nil)
	_ store.LogSearcher  = (*Fake)(nil)
)
```

Other unexported helpers (insert statements, scanning, the kind switch, range checks) are the Dev's choice;
tests call only the members listed above.

## Test files

- `cmd/vandoxd/internal/store/store_test.go` (existing): AC-C1, AC-C2, AC-C4; adapt AC-M4.
- `cmd/vandoxd/internal/store/migrate_test.go` (new): AC-M1–AC-M3, AC-M5–AC-M8.
- `cmd/vandoxd/internal/store/write_test.go` (new): AC-W1–AC-W10, AC-C3, `BenchmarkStore_WriteBatch` (AC-B1).
- `cmd/vandoxd/internal/store/read_test.go` (new): AC-R1–AC-R4, AC-S1–AC-S5.
- `cmd/vandoxd/internal/store/storetest/fake_test.go` (new): AC-F2–AC-F5 (AC-F1 is compile-time, in the
  production files).
- `repository.go` holds only declarations and has no test file.

Existing test code affected: no signature that a test calls changes (`Open`, `Created`, `Ping`, `Close`
unchanged; the writer pool keeps the field name `db`, which `store_test.go` uses). Assertions that change
with the new schema version are the **Tester's** in step 5, in `store_test.go`:
`TestOpen_CreatesDatabase` (expects `"1"` → `strconv.Itoa(SchemaVersion)`) and
`TestOpen_RefusesNewerSchema` (writes `'2'`, which becomes the current version → write
`SchemaVersion+1`, AC-M4). `cmd/vandoxd/serve_test.go` and `main_test.go` open real stores and need no change.

## Documentation updates

Made by the Dev:

- `docs/ARCHITECTURE.md` (*project block*): the introduction's "as of now" sentence adds the storage layer;
  *Components*: `store` described as "the SQLite database: schema, migrations, writing batches, queries and
  log search"; *Storage and retention*: schema overview (records table, metrics and log lines, JSON
  payloads), migrations at start-up, single writer and query-only readers with `synchronous=FULL`, literal-term
  log search (FTS5 index filled by the write path in the same transaction, a line searchable at commit;
  search cost grows with the term's hits, bounded by the caller's context deadline); the 0057 link is
  replaced by 0065, and 0063–0068 are linked.
- `docs/WIRE_FORMAT.md`: in *Records*, the `seq` and `captured_at` rows note the backend storage limits
  (seq at most 2^63 − 1; `captured_at` between 1677-09-21 and 2262-04-11, 0063); the sentence "Nothing in
  `cmd/` uses the package yet" is corrected: the store validates agent IDs with `wire.ValidateAgentID`, and
  the codec's first consumers are still #39 and #40.
- `docs/BENCHMARKS.md` (new): purpose; reference host DS918+ (Celeron J3455, DSM, Docker); procedure:
  `CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go test -c -o store.test ./cmd/vandoxd/internal/store`, copy to the
  NAS, `TMPDIR=<dir on the data volume> ./store.test -test.run '^$' -test.bench BenchmarkStore_WriteBatch
  -test.benchtime 5x`; results table (benchmark, host, CPU, date, ns/op, records/s) with the development host's
  numbers (labelled as the development host) and the DS918+ row *pending — issue #<follow-up>*; the 1 s
  target of #14, which stays open until the DS918+ row is filled.
- `docs/UNIT_TESTS.md`: a bullet under *Structure*: benchmarks live in the `_test.go` file of the code they
  measure, never assert a duration, and their reference-host results go into `docs/BENCHMARKS.md` (0068).
- `.squad/project.md` (product facts made untrue by this change): *Security areas* 1 — deduplication is
  implemented by `store.WriteBatch` (partial unique index `records_agent_seq`, `ON CONFLICT DO NOTHING`;
  records 0063); 9 — records `0045, 0065` instead of `0045, 0057`; 10 — add the log search text
  (`store.SearchLogs`, `ftsQuery`, record 0066); *Test doubles*, row *database*: add
  "`storetest.Fake` for `store.Writer`, `RecordReader`, `LogSearcher` (scripted hooks, recorded calls,
  `Block`; `cmd/vandoxd/internal/store/storetest`) (implemented)".
- `README.md`: none (the layout line already names the SQLite store).

## Architecture check

- **No data gaps unless explicitly recorded** (0028, 0045): strengthened — a committed batch is durable
  (`synchronous=FULL`, explicit), and a resent record is never overwritten (`ON CONFLICT DO NOTHING`).
  Classification and gap objects remain #41.
- **Backfilled data never alerts** (0022): untouched; `received_at`, `boot_id` and `clock_offset_ns` are
  stored so #41 can classify.
- **A hanging database never blocks the agent** (0029): agent-side, untouched. On the backend, `/healthz`'s
  single-flight ping (0059) is unchanged and now reads through the reader pool, so a long write batch does
  not make the backend look unhealthy.
- 0007 (SQLite, WAL, FTS5, single writer, batching): implemented as stated. 0057: superseded by 0065, which
  keeps the driver and the file checks. 0059 (`/healthz` reads `meta.schema_version`): kept. 0061: all code
  under `cmd/vandoxd/internal/store`. 0060 (memory limit): at most five connections, ~10 MiB of page cache.

## Security considerations

- **Area 1 (deduplication)**: uniqueness is enforced by the database for origin `agent`; there is no code path
  that updates or replaces a record row (only `INSERT`; the only upsert is on `meta.schema_version` inside
  migrations). The batch size is bounded (`MaxBatchRecords` = wire `MaxRecords`); the bytes of a batch are
  bounded by the caller (ingest: `wire.Limits.MaxBatchBytes`; importer: its chunking, #15).
- **Area 9 (database file)**: the `Lstat`/`O_EXCL` checks run before either pool is opened, unchanged. Residual
  as in 0057: a connection opened later re-opens the path without re-checking (0065 *Consequences*).
- **Area 10 (FTS5 query)**: the guard above; the expression is bound, never concatenated; errors never echo
  the text. The guard bounds the expression, not the work: a search's cost grows with the stored lines that
  contain its terms. Context cancellation interrupts a running query (driver behavior, AC-S5); callers that
  serve a user (#26, #47) must pass a deadline (0066).
- **Area 12**: store errors carry field paths and rule names only, never payload values (AC-W9, AC-S4).
  Log text is returned raw; escaping for the UI or Telegram is the display layer's job.
- No new dependency; no change to Docker, CI or configuration.

## Decision records

- `docs/decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md` (Proposed)
- `docs/decisions/0064-versioned-schema-migrations-in-go-one-transaction-per-step.md` (Proposed)
- `docs/decisions/0065-sqlite-connections-single-writer-query-only-readers-synchronous-full.md` (Proposed;
  supersedes 0057 — at approval the Lead sets 0057 to `Superseded by 0065` and updates the index)
- `docs/decisions/0066-log-search-takes-literal-terms-only.md` (Proposed)
- `docs/decisions/0067-storage-repository-interfaces-and-a-scripted-fake-in-storetest.md` (Proposed)
- `docs/decisions/0068-write-throughput-measured-by-a-benchmark-ds918-measurement-in-a-follow-up.md` (Proposed)

## Challenge

Devil's Advocate, 2026-10-06 (1 major, 1 minor):

1. **Major — the write path will probably miss the 1 s criterion on the DS918+, and the likely remedy
   (changing the schema created here) was left to a follow-up.** **Accepted**, with both of the asked
   decisions taken:
   - *(b) Indexing strategy settled now.* The Lead re-measured the Devil's Advocate's case and alternatives
     (*Facts verified*, 0063 *Options considered*): the per-row `AFTER INSERT` trigger costs 0.50–0.79 s per
     10,000 OOM-kill lines on this host, an explicit `INSERT INTO log_fts` per line from the write path in
     the same transaction 0.18–0.33 s (same index, `integrity-check` clean), indexing after the commit about
     0.11 s plus a separate index pass. The plan now drops the trigger from migration step 2 and fills the
     index in `WriteBatch` (Approach steps 2 and 3, AC-M1, new AC-W10). Atomicity and "searchable at commit"
     are kept; deferred indexing is recorded as the next candidate (0063 option c) and needs the Product
     Manager because it changes when a line becomes searchable.
   - *(a) #14 is not closed.* Even at 0.18–0.33 s here, the J3455 result is unknown, and the criterion says
     "measured and noted". The PR refers to #14 without a closing keyword; #14 stays open with that criterion
     until the follow-up notes the DS918+ result (0068, spec AC8). "Pending" now means "open", not "done".
   - The "~0.45 s" figure is corrected in this plan, 0065 and 0068 and the follow-up body; the benchmark's
     log-line fixture is the kernel OOM-kill line (AC-B1).
2. **Minor — the claimed bound on search cost is false.** **Accepted.** The guard bounds the expression,
   not the work. spec.md now says a search's cost grows with the number of stored lines containing its words
   and that the caller limits it with a context deadline; 0066 *Consequences* states the query plan, the
   measurement and the deadline requirement for #26/#47; *Security considerations* area 10 and AC-S5 say the
   same. A plan that narrows by time first is not possible with the current IDs (imports and backfills are
   not in capture order) and is left to #26 if measurements require it.

## Out of scope / follow-ups

- **Pull request and #14:** the PR refers to #14 **without a closing keyword** (`Part of #14`, not
  `Closes #14`); #14 stays open with the DS918+ criterion unmet (0068). The PR body states that the
  DS918+ measurement has not been made and gives the development host's numbers as such.
- Follow-up issue (created by the orchestrator, number goes into `docs/BENCHMARKS.md` and the PR):
  **Title:** `[Tests] Measure the storage write benchmark on the DS918+`
  **Body:** "Issue #14 requires that writing 10,000 records in one transaction takes under one second on the
  reference host, a DS918+ (measured and noted). The storage layer was merged without that measurement, so
  #14 stays open for this criterion. The benchmark `BenchmarkStore_WriteBatch` and the procedure are in
  `docs/BENCHMARKS.md`. On the development host (2.1 GHz Xeon, 4 vCPU) 10,000 records per transaction took
  0.12–0.15 s for metrics, 0.13–0.20 s for the mixed batch and 0.18–0.33 s for kernel OOM-kill log lines; the
  J3455 is slower per core by an unknown factor. Run the procedure on the DS918+ with the temporary
  directory on the data volume and note the results in `docs/BENCHMARKS.md`; the PR that does so closes this
  issue and #14. If a sub-benchmark exceeds one second, decide on a remedy in a decision record first: the
  next candidate is indexing FTS5 after the commit (record 0063, option c), which changes when a log line
  becomes searchable and needs the Product Manager. Records 0063, 0068."
- Not in this change: ingest (#40), import (#15), classification and gap objects (#41), rollups, retention
  and removing FTS5 entries of deleted lines (#46), query API (#47), backup (#55), search operators and
  search deadlines (#26).
