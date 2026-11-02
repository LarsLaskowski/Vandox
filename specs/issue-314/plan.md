# Plan: Bound idle connections on the ingest listener

Source: Issue #314
Status: Draft
Tier: security — the change rewrites the guard that decides which peer may hold the ingest listener's 16 connections (network exposure of a listener, security areas 1 and 2).
Security areas touched: 1 Ingest authentication (`Ingest/ConnectionCap`, new `Ingest/CappedConnection`, `Ingest/IngestEndpoint`, the listener in `Hosting/ListenerRoutes` unchanged in code); 2 Tailscale ACL and port binding (the ingest listener's "at most 16 connections" in `.squad/project.md`, text only); no Docker, CI, build file or dependency

## Problem / root cause

`ConnectionCap.Wrap` (`src/Vandox.Backend/Ingest/ConnectionCap.cs:47-54`) counts every accepted connection from the moment it is
accepted, whether or not it ever sent a request or presented the token, and when 16 are held it aborts the **new** connection. A
held connection stays as long as Kestrel keeps it: the keep-alive (120 s) and header timeout (5 s) are server-wide
(`Hosting/ServeCommand.cs:320-321`, `KestrelServerOptions.Limits`) and an idle connection never reaches the token check
(`Ingest/IngestEndpoint.cs:384-402`). So any peer that reaches the port can take all 16 places without the token, and the agent's
connection is the one that is closed. Record 0126 accepted this as a residual (*Consequences*).

The fix decides on the token, the one thing only the agent has: when the cap is full, the **oldest held connection on which no
request passed the token check** is aborted to make room for the new one; a connection whose request passed the token check keeps
its place; only when all 16 held connections passed it is the new connection aborted (today's behavior).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The ingest listener keeps at most 16 connections through `ConnectionCap` (issue) | `Ingest/IngestLimits.cs` `MaxConnections = 16`; `Hosting/ListenerRoutes.cs:52` | confirmed |
| 2 | Keep-alive (120 s) and header timeout are server-wide (issue) | `Hosting/ServeCommand.cs:45-46,320-321` set `options.Limits` on `KestrelServerOptions`; `ListenOptions` has no limits of its own | confirmed |
| 3 | A peer can hold all 16 connections without the token (issue) | `ConnectionCap.cs:47-63` counts from accept, independent of any request; `ServeIngestSeventeenthConnectionIsClosed` holds 16 with `GET /healthz` answered `404` and sees the 17th closed | confirmed |
| 4 | How long Kestrel keeps a connection that has sent no byte yet (keep-alive 120 s or header timeout 5 s) | to verify: not needed by any criterion, no owner — Kestrel internals, not read; the fix does not depend on the value, since an idle untrusted connection is evicted on demand whatever its timer | unverified |
| 5 | The agent's connections are closed until one is free (issue) | `ConnectionCap.cs:49-54` aborts the newcomer | confirmed |
| 6 | Only availability is affected: the agent keeps its spool and retries (issue) | `internal/sender/transport.go:61-72` (a failure is `NotAccepting`); `docs/areas/backend-host.md` *Ingest endpoint*, "Every other answer" | confirmed |
| 7 | Record 0126 accepts this as a residual (issue) | 0126 *Consequences*, "Accepted residuals" | confirmed |
| 8 | The issue's option "`Connection: close` on `401`/`404`" does not help against a client that never sends a request (issue) | a connection without a request gets no answer to carry the header | confirmed |
| 9 | The agent opens at most 2 connections to the backend and closes an idle one after 30 s | `internal/sender/transport.go:27-28,107-108` (`MaxConnsPerHost` 2, `IdleConnTimeout` 30 s) | confirmed |
| 10 | An item a connection middleware puts into `ConnectionContext.Items` is the item the request pipeline reads from `IConnectionItemsFeature` | `ListenerRoutes.cs:63-68` writes the label, `PortRoutingMiddleware.cs:63` reads it; `ServeIngestAnswerCarriesNoSecurityHeaders` gets `204` through real Kestrel only with that label | confirmed |
| 11 | `ConnectionContext.Abort()` called from another connection's middleware closes a Kestrel connection promptly while it waits idle (before its first request, or in keep-alive after a `404`) | to verify: `ServeIngestFullListenerEvictsTheOldestUntrustedConnection` (connection 1 closed within `IsClosedAsync`'s 10 s), written by the Tester in step 5, green in step 6 — not measured; the existing cap only aborts a connection before the HTTP layer starts | unverified |
| 12 | Address does not tell the agent apart: the port is reachable from the monitored server, where every local process shares the agent's address, and from LAN hosts that route to the tailnet address | `docs/areas/network-and-security-model.md` (*The binding is defense in depth*; the ACL lets the server reach the ingest port) | confirmed |
| 13 | Record 0126 is unreleased and is edited in place | `git ls-remote --tags origin`: only `v0.1.0`; the record's adding commit `7228815` is no ancestor of `v0.1.0` (`git merge-base --is-ancestor`) | confirmed |
| 14 | No existing test calls a changed signature: the constructor and `Wrap` keep theirs | `grep ConnectionCap tests/`: `ConnectionCapTests` only; `ListenerRoutes.Ingest` used by `ServeTests` | confirmed |

## Acceptance criteria

- [ ] AC1: When `ConnectionCap` holds its maximum of connections and none or some of them are trusted, a new connection evicts the
  **oldest untrusted** held connection: that one is aborted exactly once, the new connection reaches the next delegate and is not
  aborted, and no other held connection is aborted.
- [ ] AC2: A trusted connection is never evicted. When every held connection is trusted, the new connection is aborted without
  reaching the next delegate, and a slot freed by a finished connection serves the next one (today's behavior, kept).
- [ ] AC3: An evicted connection stops counting at its eviction and its later end frees nothing more: with a cap of 1, after B
  evicted A and A's handler ended, a C arriving while B is trusted is aborted. A handler that throws still frees its slot.
- [ ] AC4: `IngestEndpoint` trusts the request's connection (`ConnectionCap.Trust`) exactly when the token matches, right after the
  token check (step 2 of the answers table) and before every later step: not for a missing or a wrong token (`401`), not while ingest
  is disabled (`503`, step 1); for a matching token also when a later step refuses (`415` for a wrong content type) and for a stored
  batch (`204`). A request without the connection item (the web listener, a test context) is served as today.
- [ ] AC5: Over real Kestrel: with 16 connections held that never presented the token, a 17th connection that posts a valid batch
  with the token gets `204` and the first held connection is closed; with 16 connections that each stored a batch with the token
  and stay open, a 17th connection is closed without an answer. The web listener answers `/healthz` `200` throughout.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `ConnectionCapEvictsTheOldestUntrustedConnectionWhenFull` (cap 2, A and B untrusted, C arrives: A aborted once, B and C not, C reached the delegate) | `tests/Vandox.Backend.Tests/ConnectionCapTests.cs` |
| AC2 | `ConnectionCapSkipsTrustedConnectionsWhenEvicting` (cap 2, A trusted, B untrusted, C arrives: B aborted, A and C not); the existing `ConnectionCapAbortsTheConnectionBeyondTheCapAndReusesFreedSlots` adapted so first and second are trusted before the third arrives, renamed `ConnectionCapAbortsTheConnectionBeyondACapOfTrustedConnectionsAndReusesFreedSlots`, assertions unchanged | `tests/Vandox.Backend.Tests/ConnectionCapTests.cs` |
| AC3 | `ConnectionCapCountsAnEvictedConnectionOnce` (cap 1, as AC3 states); `ConnectionCapFreesTheSlotWhenTheHandlerThrows` kept unchanged (a single connection, no eviction: no input the change handles differently) | `tests/Vandox.Backend.Tests/ConnectionCapTests.cs` |
| AC4 | `IngestEndpointTrustsTheConnectionOnlyWhenTheTokenMatches` (data rows: no token, wrong token, ingest disabled → untrusted; matching token with `Content-Type: text/plain` → `415` and trusted; matching token with a valid batch → `204` and trusted) | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC5 | `ServeIngestFullListenerEvictsTheOldestUntrustedConnection` (new: 16 raw connections, the first sends nothing, 2–16 get `404` for `GET /healthz`; a 17th posts a valid batch with the token on a kept-open connection: `204`; `IsClosedAsync` on the first is true; web `/healthz` `200`); the existing `ServeIngestSeventeenthConnectionIsClosed` adapted and renamed `ServeIngestSeventeenthConnectionIsClosedWhenAllHoldTheToken`: each of the 16 posts a valid batch with the token on a kept-open connection (`204`; a repeat batch is fine, the store keeps it once) instead of `GET /healthz`, the rest unchanged | `tests/Vandox.Backend.Tests/ServeTests.cs` |

Unit tests trust a connection through the real path: a `DefaultHttpContext` whose `IConnectionItemsFeature` is
`ConnectionItems(connection.Items)` of the `AbortRecordingConnection` the cap was handed, passed to `ConnectionCap.Trust`, after the
connection reached the next delegate (the item is set before that).

## Approach

**`CappedConnection`** (new, `src/Vandox.Backend/Ingest/CappedConnection.cs`): one held connection of the cap — the
`ConnectionContext` and a trusted flag written with `Volatile.Write` and read with `Volatile.Read`. `Trust()` only sets the flag.

**`ConnectionCap`** (rewritten body, same constructor and `Wrap`):

- fields: `_max`, a `Lock _lock`, a `List<CappedConnection> _held` in arrival order (at most `_max` entries); `_open` goes;
- `Wrap`'s delegate: create the entry and set `context.Items[ItemName] = entry` first; then under the lock: below `_max`, append;
  otherwise find the first (oldest) entry that is not trusted — remove it and append the new entry, remembering it as the victim; no
  untrusted entry: the new connection is refused. Outside the lock: `victim.Connection.Abort()`, or for a refused connection
  `context.Abort()` and return without calling `next`. Then `await next(context)` in `try`, and in `finally` under the lock remove the
  entry **if it is still in `_held`** (an evicted entry was removed at its eviction, so its end frees nothing — AC3);
- `Trust(HttpContext context)`: reads `context.Features.Get<IConnectionItemsFeature>()?.Items`, and when it holds `ItemName` with a
  `CappedConnection`, calls `Trust()`; otherwise does nothing (the web listener has no cap; endpoint tests have no item).

A trust and an eviction of the same entry may race (the flag is read under the lock, set without it): the victim was then chosen
while still untrusted, its request sees `RequestAborted`, and a write that already started runs to its commit or rollback as today;
the agent resends and the store keeps the batch once. No lock-order or deadlock: `Abort()` runs outside the lock.

**`IngestEndpoint.ServeAsync`** (`Ingest/IngestEndpoint.cs:360`): split the condition so that after `RefusesToken` returned
`false` it calls `ConnectionCap.Trust(context)`, then `RefusesFormat` and `RefusesLength` as today. Nothing else in the endpoint
changes; no status, header or log line changes.

**No new user-visible output**: no log line for an eviction or a refusal by the cap (none exists today; a line per connection
would be a log flood a peer controls), no status, no configuration option. `IngestLimits.MaxConnections` stays 16; Kestrel's
server-wide timeouts stay.

## Options considered

1. **An idle timeout in the connection middleware, before the first request (and between requests)** — lost: a peer that
   reconnects fills the 16 places again every timeout, so the agent is still kept out; a connection middleware sees bytes, not
   request boundaries, so the time between requests needs the HTTP layer's help anyway; and a timeout below the agent's idle reuse
   (30 s) closes the agent's own kept connections.
2. **`Connection: close` on `401` and `404`** — lost: a peer that never sends a request is not touched (fact 8); with the chosen
   option it adds nothing, since a refused connection stays untrusted and evictable.
3. **A second Kestrel server for ingest with shorter limits** — lost: a second host, its services and the shutdown drain
   (`ServeCommand.RunStartedAsync`) duplicated, and still a timeout, with option 1's weakness.
4. **A limit per peer address** — lost: the address does not tell the agent apart (fact 12).
5. **Evict the oldest untrusted connection when the cap is full** (chosen) — decides on the token; a connection the agent has used
   keeps its place for its lifetime; the newest connection is the one most likely to be the agent's, so the oldest makes room.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `Vandox.Backend` | `Ingest/CappedConnection.cs` | new type |
| `Vandox.Backend` | `Ingest/ConnectionCap.cs` | rewritten body: held list, eviction, `ItemName`, `Trust`; class summary updated |
| `Vandox.Backend` | `Ingest/IngestEndpoint.cs` | `ServeAsync` trusts the connection after the token check |
| `Vandox.Backend` | `Hosting/ListenerRoutes.cs` | XML summary of `Ingest` only ("closes a connection beyond" becomes the eviction rule); no code change |

## Signatures (for the Dev's skeleton)

In `src/Vandox.Backend/Ingest/CappedConnection.cs` (namespace `Vandox.Backend.Ingest`):

```csharp
internal sealed class CappedConnection
{
    internal CappedConnection(ConnectionContext connection);
    internal ConnectionContext Connection { get; }
    internal bool Trusted { get; }
    internal void Trust();
}
```

In `src/Vandox.Backend/Ingest/ConnectionCap.cs`, added beside the unchanged `internal ConnectionCap(int max)` and
`internal ConnectionDelegate Wrap(ConnectionDelegate next)`:

```csharp
internal const string ItemName = "vandox.ingest.connection";
internal static void Trust(HttpContext context);
```

The skeleton rewrites `ConnectionCap.cs` (adds the constant and `Trust` with a skeleton body; `Wrap` stays as it is until step 6)
and adds `CappedConnection.cs`. `IngestEndpoint.cs` is not touched by the skeleton.

## Test files

- `tests/Vandox.Backend.Tests/ConnectionCapTests.cs` (AC1–AC3; one existing test adapted by the Tester in step 5, one kept)
- `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` (AC4)
- `tests/Vandox.Backend.Tests/ServeTests.cs` (AC5; `ServeIngestSeventeenthConnectionIsClosed` adapted by the Tester in step 5:
  its 16 unauthenticated connections are now evicted, so its expectation changes)

`CappedConnection` has no test file of its own: its two members are exercised by every `ConnectionCapTests` case.

Existing test code that calls a changed signature: none (fact 14). Existing tests whose input the change handles differently: the
two named above, adapted by the Tester in step 5 (*Tests per criterion*).

Files of the change no unit test can cover: none.

## Areas

- `docs/areas/backend-host.md` (*Listeners and routing*): the cap's rule changes — eviction of the oldest untrusted connection,
  trusted connections keep their place, the new connection is closed only when all 16 are trusted, and the residual of record 0126.

## Documentation updates

Owned by the Dev, written once the code is green, against that code (no criterion fixes the wording). Sweep: `16 connections`,
`ConnectionCap`, `keep-alive`, `idle` over `docs/areas/`, `docs/ARCHITECTURE.md`, `README.md`, `SECURITY.md`, `deploy/` and
`.squad/project.md`.

- `docs/areas/backend-host.md` lines 72-75: replace "a further connection is closed at once … until one is free" and the residual
  sentence with the eviction rule (a connection is trusted once one of its requests passed the token check, step 2 of the answers
  table) and the new residual (below); line 322 (code list): add `CappedConnection` beside `ConnectionCap`. Lines 76-77 and 166
  (server timeouts) stay true.
- `docs/areas/network-and-security-model.md` (the bullet "The ingest endpoint checks the token before it reads …", around line 73):
  add that a peer without the token cannot keep the agent out of the listener's connections by holding them, linking the backend
  host area.
- `.squad/project.md` area 1: add `CappedConnection` to the type list (line 17); in the bounds (lines 27-28) "16 connections on the
  listener (`ConnectionCap`, HTTP/1.1 only)" gains "a further connection evicts the oldest one that has not passed the token check,
  and is closed only when all 16 have"; area 2 (line 45) "with at most 16 connections" stays true, unchanged. *Test doubles*
  (`ConnectionCap` tested with `AbortRecordingConnection`) stays true.
- Sweep hits that stay true and need no edit: `docs/areas/wire-format.md:494` (server timeouts), `docs/ARCHITECTURE.md` (does not
  describe the cap).

## Architecture check

`docs/ARCHITECTURE.md` does not describe the connection cap; no flow or guarantee of `.squad/project.md` (*Guarantees*) changes. Security
area 1's bounds hold: at most 16 connections count at any time; an evicted connection is aborted at once and no longer counts, as a
refused 17th connection is accepted and aborted today, so a flood costs the same per connection as before. The token check stays the
boundary (area 2's network model): trust is granted only after it, and the trust only protects a place in the cap — it grants no
request anything the token check did not already grant.

## Security considerations

- [ ] No limits on parsed input change (no new parsing; the 16-connection bound is a count, not a byte length).
- [ ] Exceptions reaching a user-visible message: none new. `Abort()` of a victim surfaces in that connection's request as
  `RequestAborted`, which the endpoint already handles without an answer (`IngestEndpoint.cs:193-196`).
- [ ] The trust cannot be gained without the token: `ConnectionCap.Trust` has one production call site, after `RefusesToken` returned
  `false`; the connection item is server-side state no request can write (AC4 pins the negative rows).
- [ ] Residual, availability only: a peer that opens 16 connections between the agent's connect and its token check (one round trip
  and the header read) evicts the agent's new connection, which retries; a connection the agent has used keeps its place; a token
  holder can hold all 16 (record 0126).

## Decision records

- `docs/decisions/0126-ingest-endpoint-token-bound-to-one-agent-id-two-slots-through-the-commit-no-rate-cap.md` (Accepted,
  unreleased, edited in place in this plan): source `#314` added, a *Connections* group under *Options considered*, one sentence in
  *Decision*, and the idle-connection residual in *Consequences* replaced by the narrower one above.

## Out of scope / follow-ups

- Kestrel's server-wide keep-alive and header timeout stay as they are; a per-listener timeout is not needed by the chosen option.
- A log line or metric for connections refused or evicted by the cap: none today, none added (a peer-controlled volume); no
  follow-up issue.

## Challenge notes

Devil's Advocate, plan round: no major objections; minor objections, verbatim:

1. The new item key must not reuse the listener-label key. `src/Vandox.Backend/Hosting/ListenerRoutes.cs:18` already defines `ItemName = "vandox.listener"`, and `PortRoutingMiddleware.cs:63` routes on it. Give `ConnectionCap.ItemName` its own string; never write the `CappedConnection` under `ListenerRoutes.ItemName`. Tester: assert in the AC5 test, or a unit test, that both items are present on an ingest connection.
2. The residual is worded too narrowly. A peer that keeps opening connections faster than 16 per agent round trip keeps an agent that has no trusted connection out until it stops. A connection the agent already holds is not affected. The cost to the peer is a constant connect rate instead of 16 idle connections. State the residual this way in record 0126 and the area text.
3. Do not add any claim about how long an idle, never-requesting connection lives to the area text or record 0126 unless the AC5 test measures it.

## Security notes

Security, plan round: APPROVED, no blocking findings. Non-blocking, verbatim in substance:

1. Fact 11 is confirmed by measurement (`ConnectionContext.Abort()` from another connection's middleware closed idle, partial-header and keep-alive connections within 0-16 ms; 2000 back-to-back connections left 16 live handlers). Keep the Tester's AC5 test as the regression pin.
2. The residual undersells the loss rate (about 58% of fresh connections evicted before their token check under a local flood of about 11.8k conn/s). Suggested wording for record 0126 and `docs/areas/backend-host.md`: "a sustained connection flood can delay the agent's first connection (after a start or a lost connection) until one attempt wins the race; it cannot evict a connection the agent has used".
3. Fact 4 is not load-bearing and may stay unverified.
