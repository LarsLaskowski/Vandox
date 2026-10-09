# Backend host

## Scope

How the backend `vandoxd` runs as a service: its command line and exit codes, the two listeners and what each may serve, the health
probe, logging, shutdown, the container image and compose file, and how the web UI and the ingest endpoint are exposed. The ingest
endpoint itself (authentication, decoding, storage calls) is **not implemented yet**; this document fixes the host it will run in.
The options of the configuration file are in [Configuration and secrets](configuration-and-secrets.md), the import sub-command in
[Log import](log-import.md).

## Command line and exit codes

- `vandoxd` without a sub-command runs the service. Flags: `-config <file>` (default `/etc/vandox/vandoxd.yaml`), `-healthcheck`,
  `-version` and `-h`. `-version` wins over the other flags and prints the version line to standard output. `-h` prints the usage to
  standard error and exits 0.
- The first positional argument `import` starts the import sub-command; any other positional argument is a usage error.
- Exit codes: **0** after a clean shutdown, a printed version or help; **1** when the configuration or the database cannot be loaded or
  opened, or serving fails; **2** for a usage error (unknown flag, flag without value, unexpected argument), with the message and the
  usage on standard error.
- A service start logs one line with the version, the configuration path, both listen addresses and the storage directory, then
  opens (and, if needed, creates and migrates) the database, then starts both listeners.

## Listeners and routing

- The service opens two listeners from `web.listen` and `ingest.listen`. Every connection carries the label of the listener that
  accepted it, and **a request reaches only the endpoints of its own listener**. A request on a connection with no label or on the
  wrong listener is answered `404` (fail closed).
- The web listener serves the web UI and `/healthz`. The ingest listener serves only the ingest endpoint; **until that exists it answers
  `404` to everything, `/healthz` included**.
- The server sends no `Server` header and limits a request's header read to 5 seconds, its header size to 16 KiB and the idle keep-alive
  to 120 seconds.
- The web UI is a Blazor Web App in the *Interactive Server* render mode: every open page holds one live connection to the server.
  HTML output is escaped by default. The number of concurrent pages is bounded only by the single-user home-network use.

## Health

- `GET` and `HEAD /healthz` on the web listener, without authentication: `200` with body `ok` when the schema version can be read from
  the database within 2 seconds, otherwise `503` with body `unavailable`. Responses carry `Cache-Control: no-store` and
  `X-Content-Type-Options: nosniff`. A failure is logged at warning level with the exception type name; the response never contains
  an error text. At most one database check is in flight at a time: requests wait for it or for their own 2-second timeout, so a hung
  volume leaves no pile of stuck work.
- The web UI login must leave `/healthz` outside the login. Reading the schema version, not merely holding a connection, is the
  check, because it makes SQLite read the file.
- `vandoxd -healthcheck` is the container's health probe: it loads the configuration file **without the environment** (no secret is
  read and no `VANDOX_` variable is checked, so an unreadable secret file does not turn a running container unhealthy), derives
  `http://<host>:<port>/healthz` from `web.listen` (an empty, any-address or IPv4-mapped host becomes loopback), sends one `GET` with a
  4-second timeout, no proxy and no redirect, and exits 0 on `200`, otherwise 1 with one line on standard error. It opens no database.
- A deployment that starts the service with another `-config` path must give the probe the same path.

## Logging

- Log lines are JSON objects, one per line, on standard error: `time` (UTC, RFC 3339), `level`, `msg` and the attributes. Control and
  format characters in every value are escaped. An exception is logged as its type name in `exception`, never with its text.
- The level is `log.level`; until the configuration is loaded, start-up errors are logged at `info`. The framework's own categories log
  from warning level unless the level is `debug`, so the health probe does not fill the log.

## Shutdown

`SIGTERM` or `SIGINT` starts a graceful shutdown with a **10-second** deadline: the listeners stop accepting, running requests
finish, the database is closed, the service logs `vandoxd stopped` and exits 0. A second signal ends the process at once. The
deadline and the health timeout are constants, not options.

## Container image

- The image is built from a pinned .NET build image and runs on the pinned *chiseled* ASP.NET runtime image (no shell, no package
  manager); both base images are referenced by digest through build arguments. The service runs as the non-root user `65532:65532`;
  the entry point is the service itself, so `docker run networlddev/vandox` starts it and `--version` works.
- `/data` is an empty directory owned by `65532:65532` with mode `0700`, so a new named volume mounted there is writable by the
  non-root user. Nothing else in the image is owned by that user.
- The image declares **no `EXPOSE`** (tools that pre-fill port mappings would publish every exposed port on all interfaces) and
  **contains no configuration file** (a missing mount must not go unnoticed). Ports are documented in the compose file and the
  example configuration.
- `HEALTHCHECK` runs `-healthcheck` every 30 seconds (timeout 5 s, start period 30 s, start interval 2 s, 3 retries; the start
  interval needs Docker Engine 25 or later and is ignored by older engines). `STOPSIGNAL` is `SIGTERM`.

## Compose file

`deploy/backend/docker-compose.yml` is the supported deployment:

- Image `networlddev/vandox:latest` (the operator pins a version or digest), `restart: unless-stopped`, `mem_limit: 512m`,
  `stop_grace_period: 30s` (always above the 10-second shutdown deadline).
- **Web port** published as `${WEB_BIND_ADDRESS:-127.0.0.1}:8080:8080`: on loopback by default, for a reverse proxy on the same host;
  a proxy on another host sets the variable. A changed `web.listen` port needs the matching change in the file.
- **No ingest port is published** until the ingest endpoint exists, so nothing of the ingest listener is reachable from outside the
  container and the file starts on every Docker host regardless of Tailscale.
- Volumes: the named volume `vandox-data` at `/data` (Docker copies the image's ownership into a new volume), `./import` read-only at
  `/import`, the configuration file read-only at `/etc/vandox/vandoxd.yaml`.
- The agent token is a **file secret** (`VANDOX_AGENT_TOKEN_FILE=/run/secrets/vandox_agent_token`). The operator creates
  `secrets/vandox_agent_token` owned by `65532:65532` with mode `0400` or `0600`, never world-readable; Compose does not apply owner
  or mode to file secrets, and the service does not enforce them.
- Hardening: read-only root file system with a `tmpfs` on `/tmp`, `cap_drop: ALL`, `no-new-privileges`, `json-file` logs limited to
  3 files of 10 MB.
- A CI smoke test starts this very file with the freshly built image and checks health, the web port and the absence of an ingest
  binding, memory limit, restart policy, read-only root, user, ownership of `/data` and of everything else in the image, a clean stop
  (exit 0, `vandoxd stopped`) and that the database survives re-creating the container.

## Exposure and trust

- **TLS for the web UI is terminated by a reverse proxy**; the service speaks plain HTTP to the proxy and contains no certificate code.
  The UI port must be reachable only by the proxy. Running behind a proxy (forwarded headers, secure cookies) is part of the login
  work and is **not implemented yet**.
- **The loopback binding is not a security boundary.** Depending on the Docker Engine version and the host's firewall, a host on the
  same network may reach a published port or the container's address directly. The access control of the web UI is its login, which
  must protect every route except `/healthz` and must not treat the peer address, loopback or `X-Forwarded-*` headers as proof of
  having passed the proxy. Likewise the ingest token, not the network binding, is the boundary of the ingest endpoint.
- **The ingest path is not routed through the proxy.** Agents reach it over Tailscale at the backend host's tailnet address and the
  published ingest port; the backend does not embed Tailscale and holds no Tailscale key. The port binding must stay on the tailnet
  address once it is published, and the Tailscale ACL limits who may reach it (see the network area). Where Tailscale runs in
  userspace-networking mode the tailnet address is not a host interface, so the supported mode has to be decided when the ingest port
  is published.

## Related decisions

- [0017](../decisions/0017-ingest-via-tailnet-address-and-published-port.md) — why the host's tailnet address and a published port, not `tsnet`.
- [0023](../decisions/0023-tls-through-a-reverse-proxy.md) — why TLS is terminated by a reverse proxy.
- [0059](../decisions/0059-healthz-checks-the-database-and-the-binary-is-the-health-probe.md) — why `/healthz` reads the database and the binary is the probe.
- [0060](../decisions/0060-compose-file-port-bindings-volumes-and-memory-limit.md) — why these port bindings, volumes and limits.
- [0072](../decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md) — why no arguments run the service and a JSON log handler.
- [0081](../decisions/0081-backend-host-two-listeners-json-logs-blazor-interactive-server.md) — why two labelled listeners, JSON logs and Blazor Interactive Server.

## Not here

- The configuration file and the secrets: [Configuration and secrets](configuration-and-secrets.md).
- The import sub-command: [Log import](log-import.md).
- The database: [Storage](storage.md). The batch format: [Wire format](wire-format.md).
- The Tailscale ACL and the security model of the monitored server: the network area.

## Implementation

`Vandox.Backend` (`Cli/CommandLine`, `Hosting/BackendApp`, `ServeCommand`, `ListenerRoutes`, `PortRoutingMiddleware`, `HealthEndpoint`,
`PingChecker`, `Cli/HealthCheckCommand`, `Logging/JsonLineLogger`), `deploy/backend/` (Dockerfile, compose file, example
configuration), `.github/scripts/smoke-test-backend.sh`.
