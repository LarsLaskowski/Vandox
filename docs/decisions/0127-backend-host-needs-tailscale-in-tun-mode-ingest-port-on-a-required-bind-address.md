# 0127: The backend host needs Tailscale in TUN mode; the ingest port is published on a required bind address

- **Status:** Accepted
- **Date:** 2026-10-25
- **Area:** Backend host
- **Source:** Issue #40
- **Supersedes:** 0060 (amends its ingest-port choice: the compose file now publishes the ingest port, on `INGEST_BIND_ADDRESS`)

## Context

Record 0017 publishes the ingest port on the backend host's tailnet address, and security area 2 requires the port to be bound only
there. Record 0060 published no ingest port while the endpoint served nothing, and rejected a required variable and a documented TUN
prerequisite only for that reason. The Synology Tailscale package runs in userspace-networking mode by default: the tailnet address is
no host interface, so a binding to it fails, and Tailscale forwards inbound tailnet connections to `127.0.0.1`, where the web port is
published. A port binding on a host address is not a security boundary (record 0060); the token is.

## Options considered

1. **Support userspace mode by publishing the ingest port on `127.0.0.1`** — works on a default Synology package, but the tailnet then
   reaches every loopback-published port, the web UI included, with only the ACL in between, against record 0016 and security area 2.
2. **TUN mode, the variable with a default of `127.0.0.1`** — the file starts everywhere, but an operator who forgets the variable
   gets an endpoint no agent reaches and nothing that says why, and a userspace host would silently work in the unsupported mode.
3. **TUN mode, a required variable `INGEST_BIND_ADDRESS`** (chosen) — the file refuses to start until the operator names the address,
   so the port is never published on an address no one chose; an import-only setup without Tailscale sets `127.0.0.1`.
4. **Publish on all interfaces and rely on the token** — rejected by record 0017.

## Decision

Option 3: the supported backend host runs Tailscale in TUN mode, and the compose file publishes the ingest port on
`${INGEST_BIND_ADDRESS:?…}`. The binding, the prerequisite and the symptoms are in the [Backend host](../areas/backend-host.md) area
(*Compose file*, *Exposure and trust*).

## Consequences

- Every operator sets `INGEST_BIND_ADDRESS` (in `.env`) after updating the compose file, an import-only one too.
- On Synology the package has to be switched to TUN mode; the README and `docs/INSTALL.md` show how.
- A host that starts Docker before Tailscale has its address may fail to start the container; the documentation names the symptom
  and the remedy.
- The CI smoke test binds to `127.0.0.1` (no tailnet in CI) and checks the binding and the refusal without the variable.
