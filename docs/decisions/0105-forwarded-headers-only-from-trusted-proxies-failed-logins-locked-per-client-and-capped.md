# 0105: Forwarded headers only from `web.trusted_proxies`; failed sign-ins locked per client and capped overall; the loopback binding stays defense in depth

- **Status:** Accepted
- **Date:** 2026-10-13
- **Area:** Web login
- **Source:** Issue #25
- **Supersedes:** —

## Context

Issue #25 asks to trust `X-Forwarded-Proto` and `X-Forwarded-For` only from configured proxy addresses with the framework's forwarded
headers middleware, a `Secure` cookie when the request came over TLS through the proxy, and rate limiting with a temporary lockout
after failed sign-ins based on the real client address; five failed attempts lead to a lockout. The owner's comment on the issue adds
that neither the peer address, loopback nor `X-Forwarded-*` headers may be taken as proof that a request passed the proxy, and asks to
verify how the Docker Engine of the supported NAS devices exposes a port published on `127.0.0.1` and to record the result.

Read from the sources of ASP.NET Core 10.0.12 (`ForwardedHeadersMiddleware`, `ForwardedHeadersOptions`): the options trust `::1` and
`127.0.0.0/8` by default; **when both the known proxies and the known networks are empty, the middleware checks no peer at all and
applies the headers from everyone**; an IPv4-mapped peer address is compared as its IPv4 address too; with the default limit of one
hop it takes the rightmost value of each header. `KnownNetworks` is obsolete in .NET 10 (`ASPDEPR005`) in favor of `KnownIPNetworks`.

Docker's release notes (docs.docker.com, Engine 28): 28.0.0 fixed "a security issue that was allowing neighbor hosts to connect to
ports mapped on a loopback address" and "remote hosts to connect directly to a container on its published ports"; 28.3.3 fixed
CVE-2025-54388, where a firewalld reload made ports published on loopback reachable from the local network again. Such requests
arrive at the container with the neighbor's own address. A connection that goes through Docker's userland proxy (the default for a
published port, also for the NAS's own reverse proxy connecting to `127.0.0.1:8080`) arrives from the gateway address of the container
network. Which engine version a Docker host runs, and whether its vendor's network settings expose a published port, depends on the
device and its updates and cannot be known for the hosts Vandox will run on. **Vandox does not rely on the loopback binding, and
checking it is the operator's job**, with the procedure in [REVERSE_PROXY.md](../REVERSE_PROXY.md); no result per device is recorded.

## Options considered

Trusted proxies:

1. **The framework middleware with the configured addresses and networks only, its defaults removed, and not added at all when none
   are configured** (chosen) — an empty list would otherwise trust everyone.
2. **Trust loopback or private networks by default** — the peer address is not proof of having passed the proxy (owner's comment).
3. **An own parser of `X-Forwarded-*`** — the issue asks for the framework middleware.

The option's form: a comma-separated string `web.trusted_proxies` (chosen; the strict decoder reads scalars only), a YAML sequence
(extends the strict decoder, security area 10, for one option), or an environment variable (not a secret, so 0049 forbids it).
Entries are dotted-decimal IPv4 or IPv6 addresses with an optional prefix; inet_aton forms such as `127.1` or `0x7f.0.0.1`, zones,
host bits below the prefix, the unspecified address and prefixes shorter than /8 (IPv4) or /16 (IPv6) are refused, so an entry means
exactly what it reads.

Failed sign-ins:

1. **The framework rate limiter** — counts requests, not failures, and has no lockout; a lockout needs failure counting anyway.
2. **A lockout of the single account** — anyone in the LAN could lock the owner out at will.
3. **A lockout per client (IPv4 address, IPv6 /64) after 5 failures in 15 minutes for 15 minutes, plus an overall cap of 100 failures
   per hour, with at most one password check at a time** (chosen) — the per-client lockout answers the issue; the cap is the backstop
   for a client that can change its address (an IPv6 host, or anyone reaching the port through a trusted gateway address, who can
   choose `X-Forwarded-For`), and one check at a time bounds memory and CPU.

## Decision

Option 1 for trusted proxies with the option `web.trusted_proxies`, and option 3 for failed sign-ins. Neither the peer address,
loopback nor a forwarded header ever skips the login; the forwarded headers only decide the client address for the lockout and the
`Secure` flag of the cookies. The rules and values are in the [Web login](../areas/web-login.md) area, the option in
[Configuration and secrets](../areas/configuration-and-secrets.md).

## Consequences

- Without `web.trusted_proxies` (the default), every client behind the proxy shares the proxy's address: one client's failures lock
  all of them out for 15 minutes, repeatably, and the cookies are never `Secure`. The operator guide and `SECURITY.md` say so and tell
  how to find the proxy's address as the container sees it; a lock started by a request whose `X-Forwarded-For` was ignored logs a
  warning that points to the missing entry.
- Trusting a gateway address trusts every connection that reaches the published port through the userland proxy; such a client can
  dodge the per-client lockout, and only the overall cap limits its guesses (at most 100 per hour).
- The overall cap lets someone who can reach the login lock everyone out by failing 100 times an hour, for up to an hour after the
  oldest failure, and a stream of attempts makes each one wait up to 5 seconds for the single check; that is accepted for a single
  user on a home network, where such a client can do worse. The recovery is a restart of the container: the counts live only in
  memory.
- Whether the published port is reachable from other hosts is the operator's responsibility; the operator guide and `SECURITY.md`
  say so and give the check.
