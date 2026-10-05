# 0059: /healthz on the web listener checks the database; vandoxd -healthcheck is the image's health probe

- **Status:** Accepted
- **Date:** 2026-10-05
- **Source:** Issue #13
- **Supersedes:** —

## Context

Issue #13 asks for `GET /healthz` without authentication, returning 200 when the database is reachable,
"used by the home-network monitoring", and for a Dockerfile `HEALTHCHECK` that uses it. The runtime image
is distroless static (0041): no shell, no `curl`, no `wget`. `vandoxd` has two listeners: the web listener,
which only the reverse proxy should reach (0023), and the ingest listener on the tailnet address (0017),
which the monitored server may reach (0010) and which the compose file does not publish yet (0060). The
database is SQLite in the data volume (0007, 0057); a
read on a hung volume can block in a system call that no context cancels.

## Options considered

1. **Where `/healthz` lives**
   - *On the web listener* — reachable by the proxy, and by home monitoring through the proxy; not offered
     to the monitored server.
   - *On the ingest listener* — would offer an unauthenticated route on the tailnet to the monitored server.
   - *On a third listener* — a third port to publish and document for one route.

   Chosen: the web listener. The ingest listener has no routes yet (404).
2. **What "reachable" means** — `database/sql` `Ping` only checks a connection; reading the schema
   version from the `meta` table makes SQLite start a read transaction on the file. Chosen: the read.
3. **A database that hangs** — a plain per-request ping with a timeout answers 503 in time but leaves one
   stuck goroutine per request (every 30 s from Docker alone). Chosen: at most one ping in flight; requests
   wait for it or their own 2 s timeout, and a stuck ping is abandoned, not repeated.
4. **The image's probe**
   - *A `curl`/`wget` binary copied into the image* — a second program with its own dependencies in a
     distroless image.
   - *A separate tiny probe binary* — a second build target and release artifact.
   - *`vandoxd -healthcheck`* — reads the same configuration file to find `web.listen`, sends one request to
     loopback, exits 0 or 1.

   Chosen: `vandoxd -healthcheck`. It loads the configuration without the environment: with the process
   environment it would read every configured secret file on each probe (every 30 s) and fail the health
   status when a secret becomes unreadable, which the running service — having read it at start — does not
   care about.
5. **Making `/data` writable for UID 65532 in a distroless image** — `RUN chown` in the runtime stage
   needs a shell the image does not have; relying on `COPY --from` keeping the build stage's ownership
   contradicts Docker's documentation (copied files are owned by 0:0 unless `--chown` is given); a
   root-owned `/data` makes a fresh named volume unwritable. Chosen: `COPY --chown=65532:65532` of one empty
   directory, accepting SonarQube's security hotspot `docker:S6504` (non-root user may modify a copied
   resource) — that writability is the purpose, and nothing else in the image is owned by 65532.
6. **`EXPOSE` in the image**
   - *`EXPOSE 8080 8081`* — documents the ports in the image metadata, but `docker run -P` and NAS
     container managers that pre-fill port mappings from it publish every exposed port on all interfaces:
     the ingest port with nothing behind it before #40, and the web port against 0023. Rejected (plan
     security review of #13).
   - *`EXPOSE 8080` only* — the same objection for the web port.
   - *No `EXPOSE`* — the ports are documented where they are bound: the compose file (0060) and
     `vandoxd.yaml`; `-p` mappings work without `EXPOSE`.

   Chosen: no `EXPOSE`.
7. **A configuration file baked into the image** — `docker run` would work without a mount, but a missing
   mount would then go unnoticed and the image would carry a copy that can drift from the example. Rejected:
   the file is mounted (compose, 0060).

## Decision

- `GET /healthz` (and `HEAD`) on the web listener, without authentication: `200` with body `ok\n` when the
  `meta.schema_version` read succeeds within 2 s, otherwise `503` with body `unavailable\n`. Responses carry
  `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`; other methods get 405. The error is logged
  at `WARN` as an attribute and never put into the response. At most one database ping runs at a time.
- The ingest listener answers 404 to everything, `/healthz` included, until the ingest API exists.
- `vandoxd -healthcheck` loads the configuration file like the service but with no environment
  (`config.LoadBackend(path, nil)`: no secret is read, no `VANDOX_` variable checked), derives
  `http://<host>:<port>/healthz` from `web.listen` (empty, `0.0.0.0`, `::` or IPv4-mapped unspecified hosts
  become `127.0.0.1` or `::1`), sends one GET with a 4 s timeout, no proxy and no redirects, and exits 0 on
  200, 1 otherwise with one line on stderr. It opens no database and no listener.
- `deploy/backend/Dockerfile`:
  `HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --start-interval=2s --retries=3 CMD ["/vandoxd", "-healthcheck"]`,
  no `EXPOSE` instruction, `STOPSIGNAL SIGTERM`, and `/data` created in the build stage and copied with
  `--chown=65532:65532`, mode `0700`, so a new named volume mounted there is writable by the non-root user;
  a comment above the line states why. If SonarQube Cloud raises `docker:S6504` on it, the hotspot is
  reviewed as *Safe* with this record as the reason. No configuration file in the image.
- The web UI login (#25) must leave `/healthz` outside the login.

## Consequences

- Docker, Container Manager and home monitoring (through the proxy) see the database state, not just a
  running process.
- A secret file that becomes unreadable after start does not turn the container unhealthy; the service
  fails on it at its next start instead.
- `-healthcheck` uses the default configuration path. A deployment that passes another `-config` must also
  override the compose `healthcheck.test`.
- `--start-interval` needs Docker Engine 25 or later; older engines ignore it and run the first check after
  `--interval`.
- If the web port in `vandoxd.yaml` changes, the probe follows it automatically; the compose port mapping
  does not (0060).
