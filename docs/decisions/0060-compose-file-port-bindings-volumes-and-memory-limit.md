# 0060: Compose file publishes the web port on loopback and the ingest port not yet; named data volume, 512 MiB limit

- **Status:** Accepted
- **Date:** 2026-10-05
- **Area:** —
- **Source:** Issue #13
- **Supersedes:** —

## Context

Issue #13 asks for `deploy/backend/docker-compose.yml` for a NAS's Container Manager with a data volume,
a read-only import volume, the configuration file, secrets, `mem_limit` and a restart policy. The
maintainer's reference host is an x86_64 NAS with 16 GB of RAM shared by more than 20 containers. Related
records: the web UI is reachable only through a TLS reverse proxy (0016, 0023: "the UI port of the
container must only be reachable by the reverse proxy"); the ingest port is published on the backend host's
tailnet address (0017) and is all the monitored server may reach (0010); secrets come only from the
environment or Docker secrets (0032, 0050); 0041 left a Dependabot `docker-compose` entry to this issue.

## Options considered

1. **Web port binding**
   - *All interfaces (`8080:8080`)* — simplest, but the UI port is then reachable from the whole LAN and
     from the tailnet, against 0023.
   - *Loopback by default, overridable (`${WEB_BIND_ADDRESS:-127.0.0.1}:8080:8080`)* — the NAS's own
     reverse proxy reaches it; a proxy on another host sets the variable.

   Chosen: loopback by default.
2. **Ingest port binding** — the ingest listener has no routes in this feature; the ingest API (#40) and
   the agent that would use it belong to release v0.2.0.
   - *A literal placeholder address* to edit — an unedited file fails to bind, but the error is obscure.
   - *A required variable (`${TAILNET_ADDRESS:?…}:8081:8081`)* — `docker compose` stops with a clear message
     when it is unset or empty, so the port is never published on all interfaces by accident. Rejected:
     on a backend host where Tailscale runs in userspace-networking mode (the default of the Synology DSM 7
     package) the tailnet address is not a host interface, so the bind fails and the container does not
     start at all — for a port that serves nothing yet.
   - *Required variable plus a documented prerequisite (Tailscale with a TUN interface)* — the container
     starts where the prerequisite holds, but every operator must set up the tailnet binding now for no
     function, and the supported Tailscale setup would be decided before the feature that needs it.
   - *No ingest port published until #40* — nothing of the ingest listener is reachable from outside the
     container (fail-closed), the file starts on every Docker host regardless of Tailscale, and #40 adds the
     binding together with the API it exposes and the prerequisite it needs.

   Chosen: no ingest port published. This does not change 0017, which says where the port goes once it is
   published.
3. **Data volume**
   - *Bind mount of a host directory* — visible in the NAS file browser, but the operator must `chown 65532`
     it first.
   - *Named volume* — Docker copies the ownership of the image's `/data` (0059) into a new volume, so it
     works without preparation.

   Chosen: the named volume `vandox-data`; the bind-mount alternative is documented in a comment.
4. **Memory limit** — the Go runtime does not read the cgroup memory limit, so without `GOMEMLIMIT` it may
   grow to the limit before collecting. A skeleton needs a few tens of MiB; later analysis over months of
   logs needs more. Chosen: `mem_limit: 512m` and `GOMEMLIMIT=400MiB` (the margin covers the SQLite page
   cache outside the Go heap, 0077). Raise both together when a feature needs it.
5. **Image reference** — a literal version would have to be bumped in this repository for every release
   (and a Dependabot `docker-compose` entry would open those pull requests); `latest` is the highest stable
   release (0041). Chosen: `networlddev/vandox:latest` with a comment to pin `X.Y.Z` or a digest. No
   Dependabot `docker-compose` entry: the only image is Vandox's own, chosen by the operator.
6. **Hardening beyond the issue** — read-only root file system with a `tmpfs` on `/tmp`, `cap_drop: ALL`,
   `no-new-privileges`, bounded `json-file` logs. They cost nothing for a static Go binary that writes only
   to `/data`. Chosen.
7. **How the file is verified** — by review only, or by starting it in CI. Chosen: a CI smoke test with the
   real compose file and the freshly built image.

## Decision

`deploy/backend/docker-compose.yml` defines the project `vandox` with the service `vandoxd`:
`image: networlddev/vandox:latest`, `restart: unless-stopped`, `mem_limit: 512m`,
`stop_grace_period: 30s` (above the 10 s shutdown deadline of 0072), `environment` `GOMEMLIMIT: 400MiB` and
`VANDOX_AGENT_TOKEN_FILE: /run/secrets/vandox_agent_token`, the single port
`${WEB_BIND_ADDRESS:-127.0.0.1}:8080:8080` (no ingest port; a comment names #40 and 0017), volumes
`vandox-data:/data`, `./import:/import:ro` and
`./vandoxd.yaml:/etc/vandox/vandoxd.yaml:ro`, the file secret `vandox_agent_token` from
`./secrets/vandox_agent_token` (the web password hash and Telegram token as commented entries for #25 and
#60), `read_only: true`, `tmpfs: [/tmp]`, `cap_drop: [ALL]`, `security_opt: ["no-new-privileges:true"]` and
`json-file` logging with `max-size: 10m`, `max-file: "3"`. `.gitignore` ignores `deploy/backend/secrets/`
and `deploy/backend/.env`.

`.github/scripts/smoke-test-backend.sh`, run by the `Release build check` job in `ci.yml` after the image
build, starts this compose file with the built image (`pull_policy: never`, the web port on
`127.0.0.1`), and checks: health `healthy`, `/healthz` 200 on the web port, no host binding for the ingest
port, memory limit, restart policy, read-only root file system and user, exit code 0 and `vandoxd stopped`
after `docker compose stop`, and — after `docker compose down` without `-v` and `docker compose up -d` — a
new container that becomes `healthy` and logs `"created":false` (the database survived re-creation in the
named volume). It reads no repository secret and pushes nothing. It generates its agent token file and
gives it the ownership and mode the operator is told to use (`chown 65532:65532`, `chmod 0400`, through
`sudo` on the runner), so the documented setup is the tested one and no world-readable example exists.

The compose header comment and the README tell the operator to create `secrets/vandox_agent_token` owned
by `65532:65532` with mode `0400` (or `0600`), never world-readable.

## Consequences

- An operator must provide `vandoxd.yaml`, `secrets/vandox_agent_token` owned by UID 65532 with mode
  `0400`/`0600`, and
  `import/`; `.env` with `WEB_BIND_ADDRESS` is optional. No Tailscale setup is needed for v0.1.0.
- Home monitoring reaches `/healthz` through the reverse proxy unless `WEB_BIND_ADDRESS` is changed.
- The published container port is fixed at 8080; a changed `web.listen` port needs the matching change in
  the compose file (the same will hold for `ingest.listen` once #40 publishes it).
- #40 must add the ingest `ports` entry on the tailnet address (0017), decide the supported Tailscale mode
  on the backend host (userspace networking: the tailnet address is not a host interface, and inbound
  tailnet connections are forwarded to the host's loopback, where the web port is published), extend the
  smoke test, and record it. Until then the Tailscale ACL (0010) is what keeps a userspace-mode host's
  loopback web port from the monitored server.
- A bind-mounted secret's ownership and mode come from the host file; Compose does not apply `uid`/`mode` to
  file secrets outside Swarm. Hence the documented `chown`/`chmod`; `vandoxd` does not enforce them.
- **Accepted residual: the loopback binding is not a security boundary.** It implements 0023's "the UI
  port must only be reachable by the reverse proxy" as far as a compose file can, but depending on the
  Docker Engine version and the host's firewall, a host on the same LAN may reach a published container
  port, or the container's bridge address, directly (reported for older engines; not verified in #13). In
  #13 the web listener serves only the unauthenticated `/healthz`, so nothing more is exposed. The access
  control for the UI is the login of #25, which must protect every route except `/healthz` on its own and
  must not treat the peer address, loopback or `X-Forwarded-*` headers as proof of having passed the
  proxy; likewise ingest authentication, not the tailnet binding, is the boundary for #40. README and
  `SECURITY.md` say so.
