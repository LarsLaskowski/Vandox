# 0016: Web UI reachable in the home LAN with a login

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** —
- **Source:** Issue #6
- **Supersedes:** —

## Context

The web UI shows logs and process data of the server. It is used from devices at home; there is no need
to open it from the internet.

## Options considered

1. **Reachable from the internet** — usable everywhere; a public attack surface for sensitive data.
2. **Home LAN only, without login** — simple; every device in the LAN sees everything.
3. **Home LAN only, with a login** — no public exposure, and access needs credentials.

## Decision

Option 3: the web UI is reachable in the home LAN only and requires a login. It is not exposed to the
internet, and it is not offered on the tailnet to the monitored server (0010).

## Consequences

- Authentication and sessions of the web UI are a security area.
- Access from outside the home needs a separate, deliberate decision.
- TLS for the UI is provided by a reverse proxy (0023).
