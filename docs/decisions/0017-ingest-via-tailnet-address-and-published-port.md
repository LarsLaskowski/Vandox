# 0017: Ingest via the NAS's tailnet address and a published port, not tsnet

- **Status:** Accepted
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The agent reaches the backend over Tailscale (0010). The backend runs as a Docker container on a Synology
NAS on which Tailscale already runs.

## Options considered

1. **`tsnet` inside `vandoxd`** — the backend joins the tailnet as its own node; a Tailscale auth key and
   state inside the container, and a Tailscale library in the backend's dependencies.
2. **NAS's tailnet address plus a published container port** — the existing Tailscale on the NAS carries
   the traffic; the container publishes the ingest port on the NAS's tailnet address.

## Decision

Option 2: the agent sends to the ingest port published on the NAS's tailnet address. `vandoxd` does not
embed Tailscale.

## Consequences

- No Tailscale key or state in the container, no Tailscale dependency in the code.
- The ACL targets the NAS node and the ingest port; the port binding must stay on the tailnet address so
  the ingest port is not offered elsewhere.
- Moving the backend to another host requires Tailscale on that host.
