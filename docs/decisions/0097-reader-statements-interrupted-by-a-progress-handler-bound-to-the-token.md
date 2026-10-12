# 0097: A cancelled token interrupts a running reader statement through a progress handler; the interruption is an OperationCanceledException

- **Status:** Accepted
- **Date:** 2026-10-12
- **Area:** Storage
- **Source:** Issue #210
- **Supersedes:** —

## Context

The log search ([0066](0066-log-search-takes-literal-terms-only.md)) and the usual-level template query
([0095](0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md), option 15) are not
bounded by their range or limit; both records name a deadline as the bound for a caller that serves a user. Microsoft.Data.Sqlite
10.0.12 calls neither `sqlite3_interrupt` nor `sqlite3_progress_handler`: `ExecuteReaderAsync`, `ExecuteScalarAsync` and
`DbDataReader.ReadAsync` only check the token before they call into SQLite, and `SqliteCommand.Cancel` does nothing. The
expensive part of both queries runs inside the first `sqlite3_step` (the `MATCH` with its join and the `ORDER BY` before the
`LIMIT`; the aggregates of the usual-level query), so a deadline did not stop them at all. Hostile log text reaches these queries
(security area 10: no hang from external input). Every reader call leases its own connection (no pooling), and the writer
connection runs batches, incident saves, import steps and migrations that must commit or roll back as a whole.

## Options considered

1. **A progress handler on each reader connection that checks the call's token** (chosen) — SQLite calls it on the thread
   that runs the statement, between virtual-machine instructions, so it cannot race with the connection being closed; it lives
   and dies with the leased connection. Cost: one managed callback per batch of instructions, and only for a token that can be
   cancelled.
2. **`sqlite3_interrupt` from a registration on the token** — no cost while nothing is cancelled; but the callback runs on
   whichever thread cancels, so it can reach the connection while it is being disposed (undefined behavior in SQLite) unless
   every registration is disposed before the connection, and an interrupt that arrives between two statements of one call is
   lost (SQLite documents it as a no-op when no statement runs), so the next statement of the same call runs unbounded.
3. **Keep the library's behavior and correct only the documents** — no code; but the two unbounded queries stay unbounded,
   and the bound both records rely on stays false.
4. **Interrupt the writer connection too** — would bound a long batch; but a batch is already bounded (20,000 records), an
   interrupted batch only rolls back work the caller must repeat, and an interrupted migration step leaves start-up failing.
   Not asked for; the writer stays uninterrupted.

How the interruption reaches the caller:

5. **`OperationCanceledException` carrying the call's token, when SQLite reports `SQLITE_INTERRUPT` and the token is cancelled**
   (chosen) — every caller already treats a cancelled token this way (the health check turns it into its timeout, the
   sub-commands into "interrupted"); the `SqliteException` stays the inner exception.
6. **`StoreException` with the SQLite message** — what the store did for every SQLite error; but callers would report a
   cancellation as a database failure instead of a timeout or an interruption.

## Decision

Options 1 and 5, without option 4: every reader connection the store leases gets a progress handler
bound to the token of its call, and an interrupted reader statement ends the call with `OperationCanceledException`. The rules —
interval, scope, granularity and the exception — are in [Storage](../areas/storage.md) (*Database file and connections*).

## Consequences

- A deadline is now a real bound for the log search, the template queries (the usual-level one included) and every other
  reader query; a caller that serves a user bounds them with a deadline and gets no partial result when it passes.
- Work inside one virtual-machine instruction (for example one call into the FTS5 module) finishes before the handler runs;
  the bound is the time between two checks, not zero.
- Reader calls with a token that can be cancelled pay one callback per 1,000 instructions; the query times in
  `docs/BENCHMARKS.md` were measured before this change.
- The store calls `SQLitePCL.raw` (the native binding Microsoft.Data.Sqlite brings) directly; a change of that binding's
  progress-handler API breaks the build, not the behavior.
