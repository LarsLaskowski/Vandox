# 0028: TLS through a reverse proxy; the backend runs on any Docker host

- **Status:** Accepted
- **Date:** 2026-10-04
- **Source:** Maintainer request
- **Supersedes:** 0023

## Context

Vandox is meant to be used by other operators, not only by its maintainer. The maintainer happens to run
the backend on a Synology NAS, and the early decision records and issues were written around that setup
(Synology Container Manager, DSM certificates, the DS918+). Other operators run QNAP, a mini PC or a
server; where the Docker container runs is not relevant to the design.

## Options considered

1. **Keep Synology as the documented target** — simple, but excludes or confuses everyone else.
2. **Treat the Synology setup as one example of a generic "backend host"** — the design only requires
   Docker, a TLS-terminating reverse proxy in front of the web UI port, and a Tailscale address for ingest.

## Decision

Option 2. The *backend host* is any Docker host in the home network; a NAS (Synology, QNAP, …) is the typical
case. TLS for the web UI is terminated by a reverse proxy in front of `vandoxd` (the NAS's built-in one,
Caddy, nginx, Traefik, …); `vandoxd` serves plain HTTP to the proxy only. The ingest path is not routed
through the proxy and is encrypted by Tailscale (0010, 0017). Installation guides may describe Synology
Container Manager as one worked example next to a generic `docker compose` guide. Sizing and performance
targets are stated for a low-end x86_64 host (the maintainer's DS918+ is a reference measurement), not as
a requirement.

## Consequences

- Older records (0004–0024, 0027) use "NAS" and "Synology" for the maintainer's setup; read them as
  "backend host" and "reverse proxy". Their reasoning is unchanged.
- The UI port of the container must only be reachable by the reverse proxy, as before; the backend must
  handle forwarded headers and secure cookies.
- Documentation and issues use the neutral terms; vendor specifics belong into clearly marked examples.
