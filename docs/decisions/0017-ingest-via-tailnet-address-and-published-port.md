# 0017: Ingest via the backend host's tailnet address and a published port, not tsnet

- **Status:** Accepted
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The agent reaches the backend over Tailscale (0010). The backend runs as a Docker container on a Docker host
(typically a NAS) on which Tailscale already runs.

## Options considered

1. **`tsnet` inside `vandoxd`** — the backend joins the tailnet as its own node; a Tailscale auth key and
   state inside the container, and a Tailscale library in the backend's dependencies.
2. **Backend host's tailnet address plus a published container port** — the existing Tailscale on the backend host carries
   the traffic; the container publishes the ingest port on the backend host's tailnet address.

## Decision

Option 2: the agent sends to the ingest port published on the backend host's tailnet address. `vandoxd` does not
embed Tailscale.

## Consequences

- No Tailscale key or state in the container, no Tailscale dependency in the code.
- The ACL targets the backend host node and the ingest port; the port binding must stay on the tailnet address so
  the ingest port is not offered elsewhere.
- Moving the backend to another host requires Tailscale on that host.
