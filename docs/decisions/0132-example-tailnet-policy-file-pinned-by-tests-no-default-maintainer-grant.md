# 0132: Example tailnet policy as a file pinned by tests, without a default grant for maintainer devices, with the token's exposure while Tailscale is down split off

- **Status:** Proposed
- **Date:** 2026-10-29
- **Area:** Network and security model
- **Source:** Issue #43
- **Supersedes:** —

## Context

Record 0010 makes the tailnet policy the boundary that lets a compromised monitored server reach only the ingest port. Until now the
policy existed only as a fragment in `deploy/agent/README.md`, which also granted the maintainer's devices every port of the backend
host, at odds with record 0016 (web UI in the home LAN only). Nothing pinned the fragment, and nothing stops a later edit of the live
policy from widening what the server reaches. Besides, while Tailscale is down its routes are gone and a `100.x` destination follows
the server's default route, so the agent's plain-`http` request would carry the token and the batch onto the provider's network; the
unit's `IPAddressAllow=` filters by address, not interface.

## Options considered

1. **Fragment in the README (as before)** — no new file; the maintainer assembles the policy by hand, and nothing checks it.
2. **A complete policy file in `deploy/agent/`, pinned by Go tests, with a policy `tests` entry** — one source the maintainer copies
   whole; the repository's tests fail on a widening edit of the file, and the admin console refuses to save a live policy whose
   `tests` fail. Whether the console evaluates that entry against `grants` is not yet verified.
3. **The same file in a new `deploy/tailscale/`** — a separate place, but its test would live in another package's folder.
4. **Reading the file with `github.com/tailscale/hujson`** — exact parsing, at the cost of a new dependency for one test.
5. **Maintainer devices: every port, the web UI port only, or nothing by default** — the Product Manager chose nothing by default,
   with a commented opt-in for SSH, so record 0016 stays as it is.
6. **Keeping sends off the default route in this change, or in a follow-up issue** — a oneshot unit with `unreachable` routes in
   the main table looked simplest, but systemd-networkd deletes such foreign routes on every link configure, reconfigure and carrier
   loss (Security's reading of systemd 249.11), so it would fail silently. The other candidates (`ManageForeignRoutes=no`, a
   `[Route] Type=unreachable` drop-in for the provider's uplink, nftables, `RestrictNetworkInterfaces=` from systemd 250) all touch
   the provider's network configuration or the agent's unit and need a measurement on a real host that this change cannot make.

## Decision

Option 2, in `deploy/agent/`, read in the test by a strip helper and a strict token walk that refuses duplicate keys, instead of a
dependency: the file's grants are the agent's `tcp:<ingest port>` and the maintainer's own devices among themselves, nothing for
maintainer devices on the backend host except a commented SSH opt-in. The token's exposure while Tailscale is down (option 6) is
split into a follow-up issue after Security's second review; this change states it as a residual. The rules are in the [Network and security model](../areas/network-and-security-model.md) area.

## Consequences

- The maintainer's devices lose the web UI, SSH and file services of the backend host over the tailnet unless the opt-in is enabled;
  in the home LAN nothing changes.
- The file is not part of the install bundle; the bundle's file lists name their files explicitly.
- Until the follow-up issue adds a control, the token and the batch go in clear to whoever answers at the backend's tailnet address
  on the provider's path while Tailscale is down; the exposure predates this change, and the area document names it.
- Until the maintainer's run on the production tailnet, the console's acceptance of the file and of its `tests` entries, are unverified;
  a wrong syntax makes the save fail visibly.
