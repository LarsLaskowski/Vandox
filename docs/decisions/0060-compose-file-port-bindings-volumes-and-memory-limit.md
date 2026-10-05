# 0060: Compose file publishes the web port on loopback and ingest only on the tailnet address; named data volume, 512 MiB limit

- **Status:** Proposed
- **Date:** 2026-10-05
- **Source:** Issue #13
- **Supersedes:** —

## Context

Issue #13 asks for `deploy/backend/docker-compose.yml` for a NAS's Container Manager with a data volume,
a read-only import volume, the configuration file, secrets, `mem_limit` and a restart policy. The
maintainer's reference host is an x86_64 NAS with 16 GB of RAM shared by more than 20 containers. Related
records: the web UI is reachable only through a TLS reverse proxy (0016, 0023: "the UI port of the
container must only be reachable by the reverse proxy"); the ingest port is published on the backend host's
tailnet address (0017) and is all the monitored server may reach (0010); secrets come only from the
environment or Docker secrets (0032, 0050); 0036 left a Dependabot `docker-compose` entry to this issue.

## Options considered

1. **Web port binding**
   - *All interfaces (`8080:8080`)* — simplest, but the UI port is then reachable from the whole LAN and
     from the tailnet, against 0023.
   - *Loopback by default, overridable (`${WEB_BIND_ADDRESS:-127.0.0.1}:8080:8080`)* — the NAS's own
     reverse proxy reaches it; a proxy on another host sets the variable.

   Chosen: loopback by default.
2. **Ingest port binding**
   - *A literal placeholder address* to edit — an unedited file fails to bind, but the error is obscure.
   - *A required variable (`${TAILNET_ADDRESS:?…}:8081:8081`)* — `docker compose` stops with a clear message
     when it is unset or empty, so the port is never published on all interfaces by accident.

   Chosen: the required variable.
3. **Data volume**
   - *Bind mount of a host directory* — visible in the NAS file browser, but the operator must `chown 65532`
     it first.
   - *Named volume* — Docker copies the ownership of the image's `/data` (0059) into a new volume, so it
     works without preparation.

   Chosen: the named volume `vandox-data`; the bind-mount alternative is documented in a comment.
4. **Memory limit** — the Go runtime does not read the cgroup memory limit, so without `GOMEMLIMIT` it may
   grow to the limit before collecting. A skeleton needs a few tens of MiB; later analysis over months of
   logs needs more. Chosen: `mem_limit: 512m` and `GOMEMLIMIT=400MiB` (the margin covers the SQLite page
   cache outside the Go heap, 0057). Raise both together when a feature needs it.
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
`stop_grace_period: 30s` (above the 10 s shutdown deadline of 0058), `environment` `GOMEMLIMIT: 400MiB` and
`VANDOX_AGENT_TOKEN_FILE: /run/secrets/vandox_agent_token`, ports
`${WEB_BIND_ADDRESS:-127.0.0.1}:8080:8080` and
`${TAILNET_ADDRESS:?…}:8081:8081`, volumes `vandox-data:/data`, `./import:/import:ro` and
`./vandoxd.yaml:/etc/vandox/vandoxd.yaml:ro`, the file secret `vandox_agent_token` from
`./secrets/vandox_agent_token` (the web password hash and Telegram token as commented entries for #25 and
#60), `read_only: true`, `tmpfs: [/tmp]`, `cap_drop: [ALL]`, `security_opt: ["no-new-privileges:true"]` and
`json-file` logging with `max-size: 10m`, `max-file: "3"`. `.gitignore` ignores `deploy/backend/secrets/`
and `deploy/backend/.env`.

`.github/scripts/smoke-test-backend.sh`, run by the `Release build check` job in `ci.yml` after the image
build, starts this compose file with the built image (`pull_policy: never`, both addresses on
`127.0.0.1`), and checks: health `healthy`, `/healthz` 200 on the web port and 404 on the ingest port,
memory limit, restart policy, read-only root file system and user, exit code 0 and `vandoxd stopped` after
`docker compose stop`, and `"created":false` after `docker compose start`. It reads no secret and pushes
nothing.

## Consequences

- An operator must provide `TAILNET_ADDRESS` (e.g. in `.env`), `vandoxd.yaml`, `secrets/vandox_agent_token`
  readable by UID 65532, and `import/`.
- Home monitoring reaches `/healthz` through the reverse proxy unless `WEB_BIND_ADDRESS` is changed.
- The container ports are fixed at 8080 and 8081; a changed `web.listen`/`ingest.listen` port needs the
  matching change in the compose file.
- On a backend host where Tailscale runs in userspace-networking mode (the default of the Synology package),
  the tailnet address is not a host interface and the ingest binding fails; the supported setup is left to
  a follow-up issue before ingest (#40) ships.
- A bind-mounted secret's ownership and mode come from the host file; Compose does not apply `uid`/`mode` to
  file secrets outside Swarm.
