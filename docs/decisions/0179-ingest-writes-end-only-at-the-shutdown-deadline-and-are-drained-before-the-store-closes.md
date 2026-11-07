# 0179: Ingest writes end only at the shutdown deadline and are drained before the store closes

- **Status:** Accepted
- **Date:** 2026-10-25
- **Area:** Ingest endpoint
- **Source:** Issue #40, Issue #315
- **Supersedes:** —

## Context

The backend answers `2xx` only after the commit (record 0045), and the agent counts an answered batch as sent. A write can wait for
another process's lock up to the store's busy timeout ([Storage](../areas/storage.md)); the container's stop grace period bounds how long
the service may take to stop. The project's tests use no real clock (`docs/UNIT_TESTS.md`), yet the drain and the busy timeout are
timing behavior (#315).

## Options considered

1. **The write under the request's token** — an aborted connection would roll back a batch the agent may already count as sent.
2. **An uncancellable write and an unbounded drain** — a write waiting for another process's lock could outlast the container's stop
   grace period.
3. **The write under a token that only the shutdown deadline cancels, a gate closed at the stop signal and a drain bounded by the same
   deadline** (chosen) — no write starts after the stop signal, a started one ends by the deadline, and the store is closed only after
   it.
4. **Test the drain with a real wait** — it can only miss a regression on a slow host, and it breaks the rule of no real clock.
5. **A test-only callback with the drain task when the drain begins** (chosen) — the test sees the drain pending while the write is
   held, with no wait; one internal property and one call in the serve command.
6. **Test the busy timeout by checking the configured values instead of measuring** — the connections are private to the store, and
   only a measurement shows that the library's own retry of a busy database ends with the configured value.
7. **A narrow exception in `docs/UNIT_TESTS.md` for timers a library runs on the real clock without a clock to inject, bounded to a
   ratio of the configured value** (chosen).

## Decision

Option 3 for the service, options 5 and 7 for its tests. The order at the stop is in [Backend host](../areas/backend-host.md)
(*Shutdown*), the busy timeout in [Storage](../areas/storage.md).

## Consequences

- A write cancelled by the deadline rolls back and the agent resends it.
- The store's connections set the library's command timeout to the documented busy timeout, so a write waits for another process's
  lock as long as documented and not the library's longer default.
- The busy timeout test measures on the real clock under the exception and costs about 5 seconds of test time.
