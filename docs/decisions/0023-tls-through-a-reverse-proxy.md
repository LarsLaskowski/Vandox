# 0023: TLS through a reverse proxy

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** —
- **Source:** Issue #6
- **Supersedes:** —

## Context

The web UI needs TLS so the login and the data are not sent in clear text in the LAN. Many NAS devices and Docker hosts already have a reverse proxy with certificate management.

## Options considered

1. **TLS in `vandoxd`** — self-contained; certificate handling and renewal in the backend.
2. **TLS terminated by a reverse proxy** — certificates managed by the proxy (e.g. the one built into a NAS, or Caddy/nginx); `vandoxd` serves plain
   HTTP to the proxy only.

## Decision

Option 2: TLS for the web UI is terminated by a reverse proxy. The ingest path is not routed
through the proxy; it is encrypted by Tailscale (0010, 0017).

## Consequences

- No certificate code in the backend.
- The UI port of the container must only be reachable by the reverse proxy, not directly from the LAN.
- The backend must handle running behind a proxy (forwarded headers, secure cookies).
