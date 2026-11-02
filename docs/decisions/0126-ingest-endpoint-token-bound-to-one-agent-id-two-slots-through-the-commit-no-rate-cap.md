# 0126: Ingest endpoint: the token bound to one configured agent ID, two slots held through the commit, a free-space floor instead of a rate cap

- **Status:** Accepted
- **Date:** 2026-10-25
- **Area:** Backend host
- **Source:** Issue #40, Issue #314
- **Supersedes:** —

## Context

The ingest endpoint (#40) implements the request of record 0124 for the agent's sender (record 0125). The backend has one agent
token, `VANDOX_AGENT_TOKEN` (record 0050 keeps it single until several agents, #89), but the consumer duties of the
[Wire format](../areas/wire-format.md) area require the header's `agent_id` to be checked against the agent the token belongs to.
Security area 1 asks that a valid agent cannot exhaust the backend's memory or disk; the container has 512 MiB (record 0060). The
backend must answer `2xx` only after the commit (record 0045), and the store takes a fully decoded batch and one writer at a time.
The agent keeps two requests in flight and treats any non-`2xx` that is not a refusal as a backend not accepting batches, which
starts one shared backoff for both of its streams (record 0125).

## Options considered

Agent ID:

1. **Any agent ID with the token** — no new option, but a token copied to a second server mixes two servers' data into the one
   the web UI shows, and the consumer duty is not met.
2. **The first agent ID stored binds the token** — no option, but the binding is invisible, survives a reinstall with a new ID and
   needs a reset path.
3. **An option `ingest.agent_id` naming the agent; ingest disabled while it or the token is unset** (chosen) — one more setting,
   explicit and fail-closed; #89 replaces it with a list of agents and tokens.

Memory:

1. **A slot for decoding only** — decoded batches could wait for the single writer outside any bound, one per open connection.
2. **A slot from the body read through the commit or rollback, two of them** (chosen) — at most two decoded batches exist; the agent's
   two streams never wait on each other for longer than one write.

Rate:

1. **A request-per-second cap answered `429`** — rejected: the answer starts the agent's shared backoff and holds current data back,
   and it does not bound disk growth, which retention (#46) does.
2. **No cap; the two slots and the one writer bound the rate** (chosen).

Connections (#314): the listener keeps at most 16 connections, counted from accept; Kestrel's keep-alive and header timeouts are
server-wide, and an idle connection never reaches the token check.

1. **An idle timeout in the connection middleware, before the first request and between requests** — a peer that reconnects fills
   the 16 places again every timeout, so the agent is still kept out; the middleware sees bytes, not request boundaries; and a
   timeout below the agent's idle reuse (30 seconds) closes the agent's own kept connections.
2. **`Connection: close` on `401` and `404`** — does not touch a peer that never sends a request.
3. **A second Kestrel server for ingest with shorter limits** — duplicates the host, its services and the shutdown drain, and is a
   timeout too, with the weakness of option 1.
4. **A limit per peer address** — the port is reachable from the monitored server, where every local process shares the agent's
   address, and from LAN hosts that route to the tailnet address; the address does not tell the agent apart.
5. **When the cap is full, the oldest held connection that has not passed the token check makes room** (chosen) — decides on the
   one thing only the agent has; a connection the agent has used keeps its place for its lifetime, and the newest connection is the
   one most likely to be the agent's.

Disk:

1. **Accept the residual until retention (#46)** — rejected: a compromised monitored server holds the token and could fill the data
   volume, often a NAS volume shared with other services, against the goal of security area 1; #46 bounds by age, not size.
2. **A free-space floor `ingest.min_free` checked before every write, answered `503`** (chosen) — the rule of the agent's
   `spool.min_free`; the agent keeps its records while the floor holds; one `statvfs` per batch.

Shutdown:

1. **The write under the request's token** — an aborted connection would roll back a batch the agent may already count as sent
   (record 0045 needs the commit).
2. **An uncancellable write and an unbounded drain** — a write waiting for another process's lock could outlast the container's
   stop grace period.
3. **The write under a token that only the shutdown deadline (stop signal plus 10 s) cancels, a gate closed at the stop signal and a
   drain bounded by the same deadline** (chosen) — no write starts after the stop signal, a started one ends by the deadline, and
   the store is closed only after it.

Token comparison: a plain `FixedTimeEquals` of the two strings returns early on a different length; comparing the SHA-256 digests
of both (fixed length) does not, at the cost of one hash per request.

## Decision

The endpoint accepts a batch only when the bearer token's SHA-256 digest equals the configured token's (`FixedTimeEquals`) and the
header's `agent_id` equals `ingest.agent_id` ordinally; it holds one of two slots from the body read through the commit or rollback,
writes nothing while less than `ingest.min_free` is free, has no request-rate cap, and writes under a token that only the shutdown
deadline cancels. When the listener's 16 connections are held, a new connection evicts the oldest held one on which no request passed
the token check, and is closed itself only when every held connection passed it. The statuses, limits, timeouts and log lines are in the [Backend host](../areas/backend-host.md) area
(*Ingest endpoint*).

## Consequences

- An operator sets `ingest.agent_id` to the agent's `agent_id`; until then the service runs for the web UI and the import, and
  ingest answers `503` (logged at start as `ingest disabled`).
- A wrong `ingest.agent_id` is answered `403`, which the agent logs as `token refused`; the backend's `ingest agent refused` line
  names the presented ID.
- The service closes the ingest write gate at the stop signal and drains the running writes, bounded by the shutdown deadline, in a
  `finally` before it closes the store; a write cancelled by the deadline rolls back and the agent resends it.
- The store's connections set `DefaultTimeout` to 5 seconds, so a write waits for another process's lock as long as the documented
  busy timeout and not the library's default of 30 seconds.
- A token holder can still grow the database up to the floor; the web UI and the import keep working there, and ingest resumes when
  space is freed or retention (#46) removes data.
- Accepted residuals, availability only (the agent keeps its spool and retries): a peer without the token that opens 16 connections
  between the agent's connect and its token check (one round trip and the header read) evicts the agent's new connection, which
  retries, while a connection the agent has used keeps its place; a token holder can hold all 16 connections; no log line reports a
  connection the cap evicts or closes, since its volume would be the peer's to choose; the ingest warnings other than the token and agent
  refusals are not rate-limited, since only a token holder reaches them and the container's log cap bounds their volume.
- Three tests rest on the real clock against `docs/UNIT_TESTS.md`, accepted for this change: `SqliteStoreWriteGivesUpAfterBusyTimeout`
  measures SQLite's busy handler, which runs on the real clock inside the native library and no `TimeProvider` drives, against the
  configured 5 seconds; `ServeShutdownDrainsIngestWritesBeforeClosingStore` and `ServeFailureAfterStartStillDrains` wait 500 ms to
  show that the service has not ended while a write runs, which can only miss a regression on a slow host, never fail correct code.
  A follow-up issue replaces the wait by a drain signal and settles the convention for timers of native libraries.
- Revisit the slot count when the measured memory of one request (Wire format area, *Duties of the ingest API*) or several agents
  (#89) change the budget.
