# 0060: Compose file publishes the web port on loopback and the ingest port not yet; named data volume, 512 MiB limit

- **Status:** Accepted
- **Date:** 2026-10-05
- **Area:** Backend host
- **Source:** Issue #13
- **Supersedes:** —

## Context

The compose file serves a NAS container manager: a data volume, a read-only import volume, the configuration file, secrets, a memory limit and a restart policy. The reference host is an x86_64 NAS with 16 GB of RAM shared by more than 20 containers. The web UI is reachable only through a TLS reverse proxy (0016, 0023); the ingest port is published on the backend host's tailnet address (0017) and is all the monitored server may reach (0010); secrets come only from the environment or Docker secrets (0032, 0050).

## Options considered

1. **Web port:** all interfaces (the UI reachable from the whole LAN, against 0023) or loopback by default, overridable (chosen: the NAS's own proxy reaches it, a proxy on another host sets the variable).
2. **Ingest port:** a literal placeholder address (an obscure bind error), a required variable (on a host where Tailscale runs in userspace-networking mode, the default of the Synology package, the tailnet address is not a host interface and the container would not start, for a port that serves nothing yet), the variable plus a documented TUN prerequisite (every operator sets up the binding now for no function), or **no ingest port until the ingest endpoint exists** (chosen: fail closed, the file starts on every host, and the ingest work adds the binding with the endpoint it exposes).
3. **Data volume:** a bind mount (the operator must `chown` first) or a named volume (chosen: Docker copies the image's ownership into it).
4. **Memory limit:** 512 MiB; a skeleton needs a few tens of MiB and later analysis over months of logs needs more. Raise it when a feature needs it.
5. **Image reference:** `latest` with a comment to pin a version or digest (chosen), rather than a literal version bumped in this repository for every release; no Dependabot entry, because the only image is Vandox's own.
6. **Hardening beyond the issue:** read-only root file system, `cap_drop: ALL`, `no-new-privileges`, bounded logs. They cost nothing for a service that writes only to `/data`. Chosen.
7. **Verification:** review only, or a CI smoke test with the real compose file and the freshly built image (chosen).

## Decision

The choices above. The file's contents and the smoke test are described in the [Backend host](../areas/backend-host.md) area (*Compose file*).

## Consequences

- An operator provides `vandoxd.yaml`, the token secret file owned by UID 65532 with mode `0400`/`0600`, and an `import/` directory; no Tailscale setup is needed before the ingest endpoint exists.
- Home monitoring reaches `/healthz` through the reverse proxy unless `WEB_BIND_ADDRESS` is changed.
- The published container port is fixed at 8080; a changed `web.listen` port needs the matching change in the file, and the same will hold for the ingest port.
- The ingest work must add the ingest `ports` entry on the tailnet address (0017), decide the supported Tailscale mode on the backend host, extend the smoke test and record it. Until then the Tailscale ACL (0010) is what keeps the monitored server from a userspace-mode host's loopback web port.
- **Accepted residual: the loopback binding is not a security boundary** (an engine or firewall may let a LAN host reach a published port directly). The access control is the login, and the ingest token is the boundary of ingest; README and `SECURITY.md` say so.
