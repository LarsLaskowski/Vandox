# Spec: Backend skeleton and container

Status: Draft (revised after the plan challenge)

Source: GitHub issue #13 (`[Backend] Backend skeleton and container`). Depends on #10 (PR #110) and #11
(PR #114), both merged.

## Problem / motivation

`vandoxd` today only prints its version or its usage (`cmd/vandoxd/main.go` delegates to `internal/cli.Run`).
There is no service process, no database, no HTTP listener, no container health check and no compose file.
Every later backend feature of the forensics release v0.1.0 (log import, ingest, storage, analysis, web UI)
needs a running process with configuration, logging, a database and listeners to plug into, and the
operator needs a container that can be deployed on a NAS (Synology Container Manager, QNAP, any Docker host)
and monitored from the home network.

## Behavior

- `vandoxd` without arguments runs the backend service. It reads its configuration from
  `/etc/vandox/vandoxd.yaml` (or the file given with `-config`) through `internal/config.LoadBackend`, logs
  structured JSON lines (`log/slog`) to stderr at the configured `log.level`, opens (or creates) the SQLite
  database `vandox.db` in `storage.directory`, and listens on two separate listeners: the web listener
  (`web.listen`) and the ingest listener (`ingest.listen`).
- `GET /healthz` (and `HEAD`) on the web listener needs no authentication. It answers `200 ok` when the
  database answers a read query within a short timeout and `503` otherwise. It never shows an error text.
  The ingest listener has no routes yet and answers `404` to everything, including `/healthz`.
- On `SIGTERM` (or `SIGINT`) the service stops accepting connections, lets in-flight requests finish
  within a fixed deadline (10 s), closes the database and exits 0. If requests outlast the deadline their
  connections are closed and the exit code is 1. A second signal during shutdown terminates at once.
- `vandoxd -healthcheck` reads the same configuration file (without the environment, so it reads no
  secret), sends one `GET /healthz` to the web listener over loopback and exits 0 on `200`, 1 otherwise. The image's `HEALTHCHECK` uses it, because the distroless
  image has no shell or `curl`.
- `vandoxd -version` keeps its output. `-h`/`-help` print the usage and exit 0; an unknown flag or a
  positional argument prints the usage and exits 2.
- Data lives in the database file in `storage.directory` (`/data` in the container), which is a Docker
  volume, so it survives container restarts and re-creation. The image ships `/data` owned by UID 65532
  so a fresh named volume is writable by the non-root user.
- `deploy/backend/docker-compose.yml` runs the image on a Docker host: a named data volume, a read-only
  import directory, the configuration file mounted read-only, the agent token as a Docker secret,
  `mem_limit`, a restart policy, a stop grace period longer than the shutdown deadline, a read-only root
  file system, and a port binding that publishes the web port on loopback by default (for the reverse
  proxy). The ingest port is not published: the ingest listener has no routes until the ingest API (#40),
  which adds the binding on the backend host's tailnet address. The file therefore starts on any Docker
  host without a Tailscale prerequisite.

## Acceptance criteria

- [ ] AC1: The container starts on a Docker host from `deploy/backend/docker-compose.yml` and `/healthz`
  returns 200; the container's health status becomes `healthy`.
- [ ] AC2: Data survives container restarts and re-creation: the database file lives in the `/data` named
  volume and is reopened, not recreated, after `docker compose down` (without `-v`) and `up -d`.
- [ ] AC3: `mem_limit` is set in the compose file.
- [ ] AC4: Graceful shutdown on SIGTERM with a deadline is tested (unit tests and the container smoke test).
- [ ] AC5: Structured logging with `log/slog`; configuration from `internal/config`; separate web and ingest
  listeners whose addresses come from the configuration.
- [ ] AC6: Dockerfile: multi-stage, distroless non-root runtime, base images pinned by digest (unchanged
  pattern of record 0041), `HEALTHCHECK` using `/healthz`.

The detailed, testable criteria are in [plan.md](plan.md).

## Out of scope

- Database schema beyond a `meta` table with the schema version; ingest API, log import, web UI pages,
  login (#25), ingest authentication (#40), Telegram (#60).
- A configurable shutdown deadline or health timeout (constants for now).
- Running the image on architectures other than linux/amd64.
- Publishing the ingest port and the Tailscale setup it needs on the backend host: #40 (record 0060, plan
  *Out of scope / follow-ups*).

## Open questions

None that need the Product Manager; the choices are recorded as `Proposed` decision records 0057–0062.
