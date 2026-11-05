# 0132: Tailnet policy documented as console settings, without a default grant for maintainer devices, with the token's exposure while Tailscale is down split off

- **Status:** Accepted
- **Date:** 2026-11-05
- **Area:** Network and security model
- **Source:** Issue #43
- **Supersedes:** —

## Context

Record 0010 makes the tailnet policy the boundary that lets a compromised monitored server reach only the ingest port. Until issue #43
the policy existed only as a fragment in `deploy/agent/README.md`, which also granted the maintainer's devices every port of the backend
host, at odds with record 0016 (web UI in the home LAN only). A first version of this record shipped a complete policy file
(`deploy/agent/tailnet-policy.hujson`) with policy `tests` entries, pinned by Go tests. On the production tailnet the file could not be
applied: the admin console's visual editor no longer takes a pasted policy, only shows a read-only JSON preview of a rule, so neither
the file nor its `tests` could be saved as they are. The maintainer set up the rules in the console instead, and the probes from the
server show their effect. Besides, while Tailscale is down its routes are gone and a `100.x` destination follows the server's default
route, so the agent's plain-`http` request would carry the token and the batch onto the provider's network.

## Options considered

1. **Fragment in the README (as before)** — the maintainer assembles the policy by hand from a JSON fragment that nothing checks and
   that the console no longer takes as it is.
2. **A complete policy file in `deploy/agent/`, pinned by Go tests, with a policy `tests` entry** — one source to copy whole, but the
   console cannot apply it; it would need GitOps or the policy API, which this single-maintainer setup does not use, and the file would
   drift from the live policy unnoticed.
3. **The rules as console settings in the README, pinned by Go tests** — the steps match what the maintainer does in the console; the
   tests fail when the README shows a policy file or a wide grant. The console does not refuse a later widening edit; the probes of the
   README's step 4 show one.
4. **Maintainer devices: every port, the web UI port only, or nothing by default** — the Product Manager chose nothing by default,
   with SSH to the backend host as the one opt-in, so record 0016 stays as it is.
5. **Keeping sends off the default route in this change, or in a follow-up issue** — the candidates (routes in the main table, which
   systemd-networkd removes; `ManageForeignRoutes=no`; nftables; `RestrictNetworkInterfaces=` from systemd 250) all needed a measurement
   on a real host that this change could not make.

## Decision

Option 3 with option 4's "nothing by default": the README names the console settings, and there is no
policy file in the repository. The token's exposure while Tailscale is down (option 5) is split into issue #337. The rules are in the
[Network and security model](../areas/network-and-security-model.md) area.

## Consequences

- The maintainer's devices lose the web UI, SSH and file services of the backend host over the tailnet unless the opt-in is set;
  in the home LAN nothing changes.
- Nothing in the console refuses a later edit that widens the server; the probes of the README's step 4 are the check.
- Tailscale's own reboot behavior is not checked separately: the agent unit's reboot check and the binding to `tailscale0` cover the
  agent's side.
- The `401` of the ingest endpoint needs a backend with the ingest API and is checked when the backend is upgraded to v0.2.0.
- The token's exposure while Tailscale is down, which predates this change, is closed by issue #337
  ([record 0134](0134-sender-binds-non-loopback-connections-to-the-tailnet-device.md)).
