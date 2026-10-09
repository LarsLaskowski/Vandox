# 0006: The agent connects outbound only; commands are pulled

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** —
- **Source:** Issue #6
- **Supersedes:** —

## Context

The monitored server is reachable from the internet and hosts customer services. Every listening port on
it is attack surface. The backend sits on a Docker host in the home network that is not always on.

## Options considered

1. **Backend pulls from the agent** (scrape model) — the agent must listen on a port; the backend has to
   reach the server and cannot collect while it is off.
2. **Agent pushes, outbound only** — the agent opens no port; it sends when the backend is reachable and
   spools otherwise.

## Decision

Option 2: the agent only opens outbound connections to the backend's ingest port and never listens on a
port. Commands for the server (a later feature, see 0009) are fetched by the agent from the backend, never
pushed to it.

## Consequences

- No inbound firewall rule and no listening service on the monitored server.
- The agent needs a local spool and backfill for times the backend is unreachable (0045).
- Commands reach the server only at the agent's next poll, so they have a delay.
