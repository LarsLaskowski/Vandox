# 0178: A full ingest listener evicts the oldest connection that has not passed the token check

- **Status:** Accepted
- **Date:** 2026-10-25
- **Area:** Ingest endpoint
- **Source:** Issue #314
- **Supersedes:** —

## Context

The ingest listener keeps a fixed number of connections, counted from accept (#314). Kestrel's keep-alive and header timeouts are
server-wide, and an idle connection never reaches the token check, so a peer without the token could hold every place and keep the
agent out. The port is reachable from the monitored server, where every local process shares the agent's address, and from LAN hosts
that route to the tailnet address. The agent reuses an idle connection for 30 seconds.

## Options considered

1. **An idle timeout in the connection middleware, before the first request and between requests** — a peer that reconnects fills the
   places again every timeout, so the agent is still kept out; the middleware sees bytes, not request boundaries; and a timeout below
   the agent's idle reuse closes the agent's own kept connections.
2. **`Connection: close` on `401` and `404`** — does not touch a peer that never sends a request.
3. **A second Kestrel server for ingest with shorter limits** — duplicates the host, its services and the shutdown drain, and is a
   timeout too, with the weakness of option 1.
4. **A limit per peer address** — the address does not tell the agent apart from other processes on its server or hosts that route to
   the tailnet address.
5. **When the cap is full, the oldest held connection that has not passed the token check makes room** (chosen) — decides on the one
   thing only the agent has; a connection the agent has used keeps its place for its lifetime, and the newest connection is the one
   most likely to be the agent's.

## Decision

Option 5: when every place is held, a new connection evicts the oldest held one on which no request passed the token check, and is
closed itself only when every held connection passed it. The cap and the rules are in [Ingest endpoint](../areas/ingest-endpoint.md).

## Consequences

- Accepted residuals, availability only (the agent keeps its spool and retries): a sustained connection flood from a peer without the
  token can delay the agent's first connection (after a start or a lost connection) until one attempt wins the race, but cannot evict a
  connection the agent has used; a token holder can hold every place.
- No log line reports a connection the cap evicts or closes, since its volume would be the peer's to choose; the ingest warnings other
  than the token and agent refusals are not rate-limited, since only a token holder reaches them and the container's log cap bounds
  their volume.
