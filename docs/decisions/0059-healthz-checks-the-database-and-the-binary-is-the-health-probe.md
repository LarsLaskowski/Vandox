# 0059: /healthz on the web listener checks the database; vandoxd -healthcheck is the image's health probe

- **Status:** Accepted
- **Date:** 2026-10-05
- **Area:** Backend host
- **Source:** Issue #13
- **Supersedes:** —

## Context

`GET /healthz` without authentication must return 200 when the database is reachable, for home monitoring, and the container image needs a health probe. The runtime image has no shell, `curl` or `wget` (0041). The service has two listeners: the web listener, which only the reverse proxy should reach (0023), and the ingest listener, which the monitored server may reach (0010). A read on a hung volume can block in a call that nothing cancels.

## Options considered

1. **Where `/healthz` lives:** on the web listener (chosen: reachable by the proxy and home monitoring, not offered to the monitored server); on the ingest listener (an unauthenticated route on the tailnet); on a third listener (a third port for one route).
2. **What "reachable" means:** a connection ping, or a read of the schema version from the `meta` table (chosen: it makes SQLite read the file).
3. **A database that hangs:** a per-request ping with a timeout leaves one stuck task per request (every 30 s from Docker alone); chosen is at most one ping in flight, requests wait for it or for their own timeout.
4. **The image's probe:** a `curl`/`wget` binary (a second program in the image), a separate probe binary (a second build target), or `vandoxd -healthcheck` (chosen). It loads the configuration without the environment, because with it every probe would read every secret file and an unreadable one would fail the health status although the running service does not care.
5. **Making `/data` writable for the non-root user:** `chown` in the runtime stage needs a shell; relying on build-stage ownership contradicts Docker's documentation; a root-owned `/data` makes a fresh named volume unwritable. Chosen: copy one empty directory with `--chown`, accepting SonarQube's hotspot `docker:S6504` (the writability is the purpose).
6. **`EXPOSE`:** `EXPOSE 8080 8081`, `EXPOSE 8080` only, or none. Tools that pre-fill port mappings from it publish every exposed port on all interfaces, against 0023 and for a port with nothing behind it; chosen is none, the ports are documented in the compose file and the example configuration.
7. **A configuration file in the image:** rejected, because a missing mount would go unnoticed and the copy can drift from the example.

## Decision

The choices above; the behavior of `/healthz`, `-healthcheck` and the image is in the [Backend host](../areas/backend-host.md) area (*Health*, *Container image*). If SonarQube Cloud raises `docker:S6504` on the `/data` copy, the hotspot is reviewed as *Safe* with this record as the reason.

## Consequences

- Docker, the NAS container manager and home monitoring (through the proxy) see the database state, not just a running process.
- A secret file that becomes unreadable after start does not turn the container unhealthy; the service fails on it at its next start.
- A deployment that passes another `-config` must also override the probe's command.
- If the web port in the configuration changes, the probe follows it; the compose port mapping does not (0060).
