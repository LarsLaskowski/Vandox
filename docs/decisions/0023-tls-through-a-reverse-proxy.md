# 0023: TLS through a reverse proxy

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** Backend host
- **Source:** Issue #6
- **Supersedes:** —

## Context

The web UI needs TLS so the login and the data are not sent in clear text in the LAN. Many NAS devices and Docker hosts already have a reverse proxy with certificate management.

## Options considered

1. **TLS in `vandoxd`** — self-contained, but certificate handling and renewal in the backend.
2. **TLS terminated by a reverse proxy** (chosen) — certificates managed by the proxy (the NAS's own, Caddy, nginx); `vandoxd` serves plain HTTP to the proxy only.

## Decision

Option 2. The ingest path is not routed through the proxy; Tailscale encrypts it (0010, 0017). See the [Backend host](../areas/backend-host.md) area (*Exposure and trust*).

## Consequences

- No certificate code in the backend.
- The UI port of the container must only be reachable by the reverse proxy, not directly from the LAN.
- The backend must handle running behind a proxy (forwarded headers, secure cookies).
