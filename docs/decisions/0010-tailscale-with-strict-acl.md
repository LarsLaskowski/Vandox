# 0010: Connection over Tailscale with a strict ACL

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** Network and security model
- **Source:** Issue #6
- **Supersedes:** —

## Context

The agent on an internet-facing server has to reach the backend on a Docker host in the home network. The home
network should not open a port to the internet, and a compromised server must not be able to reach the
rest of the home network.

## Options considered

1. **Port forwarding on the home router with TLS and tokens** — no extra software; exposes the backend to
   the internet.
2. **Self-managed VPN (WireGuard, OpenVPN)** — encrypted, no public backend; keys, routing and firewall
   rules to maintain by hand.
3. **Tailscale with a strict ACL** — WireGuard-based, no open router port, central ACL.

## Decision

Option 3: agent and backend communicate over Tailscale. The tailnet ACL allows the monitored server to
reach only the ingest port on the backend host and nothing else.

The resulting rules are in the [Network and security model](../areas/network-and-security-model.md) area.

## Consequences

- No inbound port on the home router; transport encryption comes from Tailscale.
- A compromised server can reach only the ingest port.
- Depends on Tailscale running on both ends and on the ACL being kept strict; the ACL is part of the
  deployment documentation.
