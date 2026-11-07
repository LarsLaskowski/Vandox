# 0192: backend.url names the backend by IP address; host names are refused

- **Status:** Proposed
- **Date:** 2026-11-07
- **Area:** Network and security model
- **Source:** Issue #373
- **Supersedes:** —

## Context

The sender binds every connection whose address is not a loopback address to `tailscale0` (record 0134), and the unit lets the agent
reach only localhost and the tailnet ranges (record 0183). Both act on the address a connection goes to, not on the name it was
looked up by. `backend.url` also took a host name, which the agent resolves through the server's `systemd-resolved` stub; the unit
allows the stub as localhost, and the stub asks the hosting provider's name servers unbound, because the documented setup turns
Tailscale's DNS off. Whoever forges that answer picks the destination: `127.0.0.1` or `::1` meets the loopback exemption, and other
answers that are still local to the server (its own tailnet address while Tailscale is up) pass the binding as well. Any local user
of the Plesk server who listens on the backend port then receives the bearer token and the batch.

The documented setup already asks for the backend's tailnet address, and MagicDNS names do not resolve there. No released agent
sends: v0.1.0 has no sender, so no deployed configuration relies on a name reaching a backend.

## Options considered

1. **Refuse host names in `backend.url`** (chosen) — the host must be an IP address; the dialer then looks nothing up, so no name
   answer exists to forge and the address the binding and the unit check is the configured one. A setup that names the backend by a
   name (MagicDNS with Tailscale's DNS on, an `/etc/hosts` entry, `localhost`) must use the address instead.
2. **Exempt loopback from the binding only for a literal loopback host** (the issue's second option) — keeps names, but the
   destination is still the name server's choice: an answer of the server's own tailnet address while Tailscale is up is bound to
   `tailscale0`, whose local route delivers it to a listener on the server; an unspecified answer (`0.0.0.0`, `::`) is connected to
   the host itself. Closing those needs more guards at every dial (refuse unspecified answers, compare against the server's own
   addresses at that moment), each a new place to be wrong, to keep a form the documented setup cannot use.
3. **Keep the residual documented** — the agent and a hosting customer's process share the server, so a listener on the backend
   port is within reach of the threat the binding exists for.

## Decision

Option 1. The configuration loader refuses a `backend.url` whose host is not an IP address without a zone, the sender's transport
refuses to be built from one, and `install.sh` refuses one in `--backend-url` (accepting only forms the loader accepts); a loopback address stays exempt from the binding only as such a
literal. The rule and its error texts are in [Configuration options](../areas/configuration-options.md) and
[Agent installation](../areas/agent-installation.md), what it guarantees in [Network and security model](../areas/network-and-security-model.md).

## Consequences

- The token goes to the configured address only: no name server is asked, so the agent no longer needs one (port 53), and the
  install script's name-server warning goes away.
- A configuration with a host name no longer loads: the agent exits at start with a configuration error naming `backend.url`.
- An `https` backend needs a certificate that names its IP address; a certificate for a `*.ts.net` name fails verification.
- A zoned IPv6 address is refused too: the transport cannot rebuild a request URL from it, and a link-local backend is no tailnet
  address.
- The guard is the same parser the Go dialer uses to decide that a host is a literal (`netip.ParseAddr`), so every form the
  configuration accepts is dialed without a lookup.
- Revisiting this means trusting a name server for the destination of the token: a tailnet-only resolver would have to be reachable
  while Tailscale is down, which it is not.
