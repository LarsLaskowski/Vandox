# Backend host

## Scope

How the backend `vandoxd` runs as a service: its command line and exit codes, the two listeners and what each may serve, the health
probe, logging, shutdown, the container image and compose file, the ingest endpoint (its request, answers, limits and log lines), and
how the web UI and the ingest endpoint are exposed.
The options of the configuration file are in [Configuration and secrets](configuration-and-secrets.md), the import sub-command in
[Log import](log-import.md).

## Command line and exit codes

- `vandoxd` without a sub-command runs the service. Flags: `-config <file>` (default `/etc/vandox/vandoxd.yaml`), `-healthcheck`,
  `-version` and `-h`. `-version` wins over the other flags and prints the version line to standard output. `-h` prints the usage to
  standard error and exits 0.
- The first positional argument `import` starts the import sub-command, `incidents` the incident reconstruction, `templates` the template rebuild,
  `report` the forensics report, `hash-password` the password hash; any other positional argument is a usage error.
- `vandoxd hash-password` reads a password from standard input and prints its argon2id hash for the secret
  `VANDOX_WEB_PASSWORD_HASH` ([Web login](web-login.md), *Creating the hash*). It takes no flag but `-h`/`-help`, reads no configuration
  and is not combined with `-healthcheck`.
- `vandoxd [flags] incidents [-config file]` rebuilds the incidents from the stored data (*Incidents* in [Detection, alerts and
  reports](detection-and-reports.md)). Its flags are `-config` and `-h`/`-help` (usage to standard error, exit 0); any positional
  argument after `incidents` is a usage error (`vandoxd incidents: no argument is expected`, exit 2), and it is not combined with
  `-healthcheck`. It loads the configuration (invalid: exit 1), opens the database (cannot: exit 1) and writes one line per saved
  incident and then the counts to standard output; the log goes to standard error as JSON lines. Exit **0** when the run completed
  (also when it skipped an incident the store refused, which the counts show); **1** when the run was interrupted or the store
  failed, without the counts. Run again, it completes the work.
- `vandoxd [flags] templates [-config file]` rebuilds the log template statistics from the stored log lines (*Template rebuild* in
  [Storage](storage.md), *Statistics* in [Detection, alerts and reports](detection-and-reports.md)). Its flags are `-config` and
  `-h`/`-help` (usage to standard error, exit 0); any positional argument after `templates` is a usage error (`vandoxd templates: no
  argument is expected`, exit 2), and it is not combined with `-healthcheck`. It starts like `incidents` (invalid configuration or
  database that cannot be opened: exit 1), logs `template rebuild started` and `template rebuild progress` after every batch to
  standard error as JSON lines, and writes the three summary lines (`Log lines:`, `Batches:`, `Templates:`) to standard output. Exit
  **0** when the run completed; **1** when it was interrupted (`template rebuild interrupted`: the statistics are incomplete until a
  run completes; run again, it starts over) or the store failed (`template rebuild failed`, including `superseded by another
  rebuild` when a second run started meanwhile; the other run continues), without the summary.
- `vandoxd [flags] report [-config file] [-lang en|de]` writes the forensics report of the stored data as Markdown to standard
  output (*Forensics report* in [Detection, alerts and reports](detection-and-reports.md)). Its flags are `-config`, `-lang` and
  `-h`/`-help` (usage to standard error, exit 0), each also after `report`, in the forms `-lang de`, `-lang=de`, `--lang de` and
  `--lang=de`; a repeated flag takes its last value. `-lang` accepts exactly `en` (the default) or `de`, compared ordinally: any
  other value (`EN`, `de-DE`, an empty value) is a usage error, `vandoxd report: unknown language "<value>" (expected en or de)` with
  the value quoted like every value in a usage error, exit 2; `-lang` without a value is `flag needs an argument: -lang`, exit 2.
  `-lang` belongs to `report` only: before the sub-command or with another one it is `flag provided but not defined: -lang`. Any
  positional argument after `report` is a usage error (`vandoxd report: no argument is expected`, exit 2), and it is not combined with
  `-healthcheck`. It starts like `incidents` (invalid configuration or database that cannot be opened: exit 1), logs `report started`
  to standard error as a JSON line, and writes nothing but the report to standard output. Exit **0** when the report is complete;
  **1** when the run was interrupted (`report interrupted`) or the store failed (`report failed` with the store's text), with an
  incomplete report on standard output that is to be discarded. Apart from the migration every sub-command runs when it opens the
  database, it writes nothing to the store.
- Exit codes: **0** after a clean shutdown, a printed version or help; **1** when the configuration or the database cannot be loaded or
  opened, or serving fails; **2** for a usage error (unknown flag, flag without value, unexpected argument), with the message and the
  usage on standard error.
- A service start logs one line with the version, the configuration path, both listen addresses and the storage directory, then
  checks the web password hash (a missing or refused hash is logged and exits 1, [Web login](web-login.md), *Password*), then
  opens (and, if needed, creates and migrates) the database, then starts both listeners.

## Listeners and routing

- The service opens two listeners from `web.listen` and `ingest.listen`. Every connection carries the label of the listener that
  accepted it, and **a request reaches only the endpoints of its own listener**. A request on a connection with no label or on the
  wrong listener is answered `404` (fail closed).
- The port routing runs first. On the web listener the forwarded headers of trusted proxies (only when `web.trusted_proxies` is set),
  the security headers and then the login follow, so the ingest listener answers `404` before any authentication is looked at
  ([Web login](web-login.md), *Access*).
- The web listener serves the web UI, its static files and `/healthz`, and every web response carries the security headers of the
  [Web UI](web-ui.md) area (`Hosting/SecurityHeadersMiddleware`, after the port routing). The ingest listener serves only the ingest endpoint: it hands
  `POST /v1/batches` (method and path compared ordinally and exactly, so `/v1/batches/`, `/V1/batches` and `GET /v1/batches` do not
  match; the query string is ignored) to the endpoint and **answers `404` to everything else, `/healthz` included**: static files and
  the endpoints of the web UI's live connection are web endpoints too, so they answer `404` there. The web pipeline (forwarded
  headers, security headers, login, antiforgery) never runs on an ingest connection, so no ingest answer carries the security headers.
  `POST /v1/batches` on the web listener goes down the web pipeline and never reaches the ingest endpoint.
- The ingest listener speaks HTTP/1.1 only (one request at a time per connection) and keeps at most **16 connections** open; a further
  connection is closed at once, before a request is read. The web listener has no such cap. The cap counts idle connections too: a host
  that reaches the port can hold all 16 for the keep-alive time without sending the token, and the agent's connection is then closed
  until one is free; the agent keeps its records and retries (record 0126).
- The server sends no `Server` header and limits a request's header read to 5 seconds, its header size to 16 KiB and the idle keep-alive
  to 120 seconds.
- The web UI is a Blazor Web App in the *Interactive Server* render mode: every open page holds one live connection to the server.
  HTML output is escaped by default. The number of concurrent pages is bounded only by the single-user home-network use.

## Ingest endpoint

The endpoint implements the request the agent's sender (#39) relies on
([0124](../decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md)), with the token bound to one agent ID,
two slots and a free-space floor ([0126](../decisions/0126-ingest-endpoint-token-bound-to-one-agent-id-two-slots-through-the-commit-no-rate-cap.md)).
How the agent reacts to each answer is in the [Agent](agent.md) area (*Sending*).

- **Request.** `POST /v1/batches` on the ingest listener (the path is exact: the agent's `backend.url` carries no path), one request
  per batch. Headers: `Authorization: Bearer <agent token>` (the secret `VANDOX_AGENT_TOKEN`, [Configuration and
  secrets](configuration-and-secrets.md)), `Content-Type: application/gzip`, `User-Agent: vandox-agent/<version>` and
  `Content-Length`; no `Content-Encoding`, so the server's request decompression never unpacks the body before the endpoint's own
  checks. The body is one gzip stream of one batch of the [wire format](wire-format.md), format version 1.1 (1.0 is accepted too),
  with the agent ID, the boot ID and the clock offset of its records' capture context and the header `mode` (`live` or `backfill`, a
  hint only, logged and not stored). The agent's batch holds at most 20,000 records and 1 MiB (1,048,576 bytes) of record lines before
  compression, counted without their line feeds, so it always fits the endpoint's limits below.
- **Enabled.** Ingest needs both the token and the option `ingest.agent_id` (the agent the token belongs to). Without either, the
  service starts as usual (web UI, `vandoxd import`), logs the warning `ingest disabled` once at start with `missing` naming
  `VANDOX_AGENT_TOKEN`, `ingest.agent_id` or both (comma-separated), and answers every `POST /v1/batches` with `503` before it looks at
  the request.
- **Answers.** No answer has a body. `R` marks an answer with the header `Vandox-Refused: batch`. The checks run in this order, and the
  first that fails answers:

  | # | Check | Answer | Log (level) |
  | - | ----- | ------ | ----------- |
  | 1 | ingest disabled | `503` | — |
  | 2 | the `Authorization` header is exactly one value, `Bearer` (any ASCII case), one space and the token | `401` with `WWW-Authenticate: Bearer` | `ingest token refused` (warning): `peer`, `reason` (`missing` for no or an empty header, otherwise `wrong`), `suppressed` |
  | 3 | no `Content-Encoding` header of any value | `415` | `ingest request not accepted` (warning): `status`, `reason` `content encoding` |
  | 4 | exactly one `Content-Type` value, equal to `application/gzip` ignoring ASCII case (no parameter, no space) | `415` | the same, `reason` `content type` |
  | 5 | `Content-Length`, when sent, at most 2 MiB (2,097,152 bytes) | `413` R | `ingest batch refused` (warning): `status`, `reason` `body too large` |
  | 6 | one of the two slots free within the request's time budget | `503` | `ingest request timed out` (warning): `peer` |
  | 7 | the body, read into the slot's buffer, at most 2 MiB; read within the budget | `413` R; `408` when the budget ran out while the body was arriving | `ingest batch refused` (`body too large`); `ingest request timed out` |
  | 8 | the header line decodes and its `agent_id` equals `ingest.agent_id` ordinally | decoder error as in step 9; `403` for another agent ID | `ingest agent refused` (warning): `agent_id` (it passed the wire format's rule), `peer`, `suppressed` |
  | 9 | every record decodes to the end, within the budget | `400` R for `Malformed`, `413` R for `LimitExceeded`, `422` R for `UnknownKind`, `Sequence`, `EmptyBatch` and a field error, `415` for `UnsupportedVersion`; `503` when the budget ran out | `ingest batch refused`: `agent_id` (once the header was read), `status`, a fixed `reason`, `line`, `field`; `415`: `ingest request not accepted` with `reason` `format version`; `ingest request timed out` |
  | 10 | the free space of `storage.directory`'s file system at least `ingest.min_free` (`0MiB` switches the check off; a free space that cannot be read counts as low) | `503` | `ingest paused: free space low` (warning): `available_bytes` (`null` when unreadable), once until the next stored batch |
  | 11 | the service has not received the stop signal | `503` | — |
  | 12 | the store writes the batch in one transaction | `422` R when the store refuses a record the decoder accepted (a capture time outside 1677-09-21 to 2262-04-11); `503` for a store failure (I/O error, full disk, a lock held longer than the 5-second busy timeout, the shutdown deadline) | `ingest batch refused` (`reason` `refused by the store`); `ingest store failed` (error): `agent_id`, `exception` (type name) |
  | 13 | committed | `204` | `ingest batch stored` (debug): `agent_id`, `mode`, `records`, `stored`, `duplicates`; `ingest first batch stored` (info): `agent_id`, once per process; `ingest resumed` (info) after a pause; then, for a batch with an agent ID whose write changed the agent's timeline: `ingest gaps changed` (info): `agent_id`, `opened` (gaps newly detected: a jump's sequence gap, a new time-gap span), `closed` (gap rows moved to closed), `final` (sequence gaps made final plus time gaps detected final) and `open` (open sequence gaps after the batch), once per batch that changed a gap; a row split off an existing gap counts in none; `agent clock off` (warning): `agent_id`, `offset_seconds`, when the measured offset first exceeds 90 seconds; `agent clock in step` (info): `agent_id`, when it is back within 90 seconds ([Detection, alerts and reports](detection-and-reports.md), *Live data and backfill*) |

  The fixed reasons of step 9 are `malformed`, `limit exceeded`, `unknown kind`, `sequence`, `empty batch` and `invalid`; `line` is
  the line number of the batch and `field` the wire format's field path with every quoted map key reduced to `[]` (`data.labels[]`,
  list indexes kept), or `null` when there is none or the path does not parse. Nothing of the batch is stored in any answer but `204`.
- **Acknowledgement.** A `2xx` answer acknowledges every record of the batch; the endpoint sends `204` only after the batch is committed
  ([Storage](storage.md), *Writing*: one batch, one transaction). The agent then acknowledges the records in its spool, which may then
  delete them, so a `2xx` before the commit would lose data. Every record is stored with origin `agent`, the batch's agent ID, boot
  ID and clock offset, its own sequence number and capture time, and one **receive time** per batch: the service clock in UTC when the
  request reached the endpoint. The agent reads at most 4,096 bytes of an answer's body, discards them and never parses them, and reads
  at most 16 KiB of an answer's header; it looks at nothing but the status and the exact value of `Vandox-Refused`.
- **Refused batch.** `400`, `413` or `422` **with the header `Vandox-Refused: batch`** (exactly this value, compared case-sensitively)
  means the batch itself is invalid and sending it again cannot succeed. The agent then splits the batch and gives up each record refused alone, replacing it
  with a recorded `gap`. The endpoint sends the header only on a request that passed the token check and only for such a batch
  (steps 5, 7, 8, 9 and 12). The same statuses without the header (a TLS listener reached with `http`, a proxy's size limit, the
  server's own `400`) are not a refusal.
- **Every other answer** means "not accepting": the agent keeps the batch and sends it again after a wait (1 to 60 seconds). The
  endpoint answers with a status outside `400`, `413` and `422`, and never with the refusal header, for everything that is not the
  batch's fault: a missing or wrong token (`401`), another agent ID (`403`, which the agent logs as `token refused`), a format version
  or content type it does not support (`415`), a full disk, a busy or failing store, a timeout or the stop of the service (`503`,
  `408`). A redirect is not followed. So a version mismatch or a temporary fault never makes the agent give up records.
- **Repeats.** A batch whose answer was lost is sent again; the store keeps each agent ID and sequence number once
  ([Wire format](wire-format.md), *Batch identity*), so a repeat is answered `204`, stores only the records it did not have and changes
  no stored row, its receive time included. A record whose sequence number is stored with other content counts as a duplicate and
  never replaces the stored one. Two requests with overlapping records at once both get `204`.
- **Token check.** The token is checked **before the body is read**: a request without the right token never has a byte of its body
  read, decompressed or parsed. The presented token and the configured one are compared as SHA-256 digests with
  `CryptographicOperations.FixedTimeEquals`, so the time depends neither on the presented length nor on how much of it is right.
  The query string, cookies and other headers are never read. The server removes spaces and tabs around a header value before the
  endpoint sees it, so a token with a trailing space or tab is accepted. Control characters (U+0001), DEL and valid UTF-8 characters
  in the header reach the endpoint and are answered `401`; a header byte that is not valid UTF-8 (such as `E9` alone) and obsolete
  line folding get the server's own `400` without the refusal header before the endpoint runs; a bare line feed ends the header line,
  so the rest becomes a header of its own (measured against the server, `ServeIngestHeaderWhitespaceAndDuplicates`). The agent
  sends only printable ASCII, so none of the server's answers concerns it.
- **Log limits.** `ingest token refused` and `ingest agent refused` are each logged at most once a minute; the next line after a
  quiet minute carries in `suppressed` the number of refusals not logged since the last line. The other ingest warnings
  (`ingest request not accepted`, `ingest batch refused`, `ingest request timed out` and the rest) have no such limit; only a request
  that passed the token check produces them. No ingest log line holds the token, the
  `Authorization` header, body bytes, a map key, other record content or a parser's or the store's own text; an exception is logged
  by its type name only. An unexpected exception of the endpoint is answered `503` (when the answer has not started) and logged as
  the error `ingest request failed` with `exception`.
- **Limits.** Compressed body at most 2 MiB; decompressed batch at most 2 MiB (the wire format's 16 MiB default is lowered for
  ingest), a line at most 1 MiB, at most 20,000 records. The server's request body limit is set to 2 MiB as a backstop.
- **Slots and memory.** At most **two** requests hold a slot at once across the service (the agent's two streams); further requests
  wait. A slot is held from the first byte of the body read through decoding, the free-space check, the wait for the store's writer
  and the commit or rollback, so at most two decoded batches exist at any time. Each slot owns a body buffer of 2 MiB, allocated on
  first use and kept. The measured peak of one request and the budget of the two slots are in the [Wire format](wire-format.md) area
  (*Error text and consumer duties*). There is no request-rate cap: the slots and the one writer bound the rate.
- **Time.** Waiting for a slot, reading the body and decoding take at most **30 seconds** from the request's arrival (the agent's own
  request timeout); the server's header timeout (5 s), header size (16 KiB) and keep-alive (120 s) stay. A request past the budget
  gets `503` (`408` while its body was still arriving) wherever an answer can still be sent, also a body that trickles in; at the
  stop signal a request still waiting, reading or decoding gets `503` without a log line. A body
  read that fails with an I/O error is answered `503`, unless the connection is gone; a request whose connection is gone gets no
  answer. Once its write has started, a request runs to its commit or rollback whatever happens to its connection: neither the
  request's abort nor the 30-second budget cancels it, only the shutdown deadline (*Shutdown*); the write waits for another process's
  lock at most the busy timeout of 5 seconds ([Storage](storage.md)).
- **Free-space floor.** While less than `ingest.min_free` (default 1 GiB) is free on the file system of `storage.directory`, no batch
  is written and the agent keeps its records. The overshoot below the floor is at most what the two slots write at once. The web UI
  and the import do not check the floor; the database still grows up to it, and retention (#46) is to bound it by age.

## Health

- `GET` and `HEAD /healthz` on the web listener, without authentication: `200` with body `ok` when the schema version can be read from
  the database within 2 seconds, otherwise `503` with body `unavailable`. Responses carry `Cache-Control: no-store` and
  `X-Content-Type-Options: nosniff`. A failure is logged at warning level with the exception type name; the response never contains
  an error text. At most one database check is in flight at a time: requests wait for it or for their own 2-second timeout, so a hung
  volume leaves no pile of stuck work.
- `/healthz` is outside the login ([Web login](web-login.md), *Access*). Reading the schema version, not merely holding a connection, is the
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
finish, the database is closed, the service logs `vandoxd stopped` and exits 0.

- **Ingest at the stop signal.** From the stop signal on no ingest write starts: the endpoint closes its write gate in its
  `ApplicationStopping` callback, so a request that has not started its write gets `503` (or no answer) and stores nothing. A write
  that has started commits or rolls back; the shutdown deadline, 10 seconds after the stop signal, cancels it otherwise, and the
  transaction rolls back. A batch that was committed but not answered is sent again after the restart and answered `204` as a repeat.
- **Drain before the store closes.** On every way out after the service started (a normal stop, a failure of a listener, an
  exception while stopping) the service waits in a `finally` for the ingest writes in flight, at most until the deadline and until
  the cancelled writes have rolled back, and only then closes the database. A second signal ends the process at once. The
deadline and the health timeout are constants, not options.

## Container image

- The image is built from a pinned .NET build image and runs on the pinned *chiseled* ASP.NET runtime image (no shell, no package
  manager); both base images are referenced by digest through build arguments. The service runs as the non-root user `65532:65532` by
  default; the operator may run it as any other non-root user ID (`user:` in the compose file, `--user`), which then has to own the
  data directory and the secret files. The entry point is the service itself, so `docker run networlddev/vandox` starts it and `--version` works.
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
- **Ingest port** published as `${INGEST_BIND_ADDRESS:?…}:8081:8081`
  ([0127](../decisions/0127-backend-host-needs-tailscale-in-tun-mode-ingest-port-on-a-required-bind-address.md)). The variable has no
  default: `docker compose` refuses to start without it, with the message `set INGEST_BIND_ADDRESS to this host's tailnet address
  (tailscale ip -4), or 127.0.0.1 without agents`. The operator puts it into `.env` next to the file: the backend host's tailnet
  address, or `127.0.0.1` for an import-only setup without agents. An address the host does not have (Tailscale down, or in
  userspace mode) makes Docker report a port binding error, and the container does not start (*Exposure and trust*). A changed
  `ingest.listen` port needs the matching change in the file.
- Volumes: the named volume `vandox-data` at `/data` (Docker copies the image's ownership into a new volume), `./import` read-only at
  `/import`, the configuration file read-only at `/etc/vandox/vandoxd.yaml`.
- The web password hash is a **file secret** the same way (`VANDOX_WEB_PASSWORD_HASH_FILE=/run/secrets/vandox_web_password_hash`),
  created with `vandoxd hash-password` ([Web login](web-login.md), *Creating the hash*); the file has the owner and mode of the token
  file, and the service refuses to start without a valid hash.
- The agent token is a **file secret** (`VANDOX_AGENT_TOKEN_FILE=/run/secrets/vandox_agent_token`). The operator creates
  `secrets/vandox_agent_token` owned by the container's user (`65532:65532` by default) with mode `0400` or `0600`, never world-readable; Compose does not apply owner
  or mode to file secrets, and the service does not enforce them.
- Hardening: read-only root file system with a `tmpfs` on `/tmp`, `cap_drop: ALL`, `no-new-privileges`, `json-file` logs limited to
  3 files of 10 MB.
- A CI smoke test starts this very file with the freshly built image (with `INGEST_BIND_ADDRESS=127.0.0.1` and `ingest.agent_id:
  agent-1` in its copy of `vandoxd.yaml`) and checks health, the web port, that 8081 is bound on exactly `127.0.0.1`, that the gzip of
  the golden batch posted with the token is answered `204` and a repeat too, that a wrong token gets `401`, that `/healthz` on the
  ingest port gets `404`, that `docker compose config` without the variable fails and names it, memory limit, restart policy, read-only root, user, ownership of `/data` and of everything else in the image, a clean stop
  (exit 0, `vandoxd stopped`) and that the database survives re-creating the container.

## Exposure and trust

- **TLS for the web UI is terminated by a reverse proxy**; the service speaks plain HTTP to the proxy and contains no certificate code.
  The UI port is published for that proxy only, as defense in depth. Behind a proxy, `X-Forwarded-For` and `X-Forwarded-Proto` are
  used only from the addresses in `web.trusted_proxies` (default: none), and the session cookies are `Secure` when the request reached
  the proxy over HTTPS ([Web login](web-login.md), *Reverse proxy trust*; setup in [REVERSE_PROXY.md](../REVERSE_PROXY.md)).
- **The loopback binding is not a security boundary.** Depending on the Docker Engine version and the host's firewall, a host on the
  same network may reach a published port or the container's address directly. The access control of the web UI is its login, which
  protects every route except `/healthz` and does not treat the peer address, loopback or `X-Forwarded-*` headers as proof of
  having passed the proxy. Likewise the ingest token, not the network binding, is the boundary of the ingest endpoint.
- **The ingest path is not routed through the proxy.** Agents reach it over Tailscale at the backend host's tailnet address and the
  published ingest port; the backend does not embed Tailscale and holds no Tailscale key. The port binding must stay on the tailnet
  address, and the Tailscale ACL limits who may reach it (see the network area).
- **The backend host runs Tailscale in TUN mode** (supported); userspace-networking mode is not supported. In TUN mode the tailnet
  address is a host interface, so Docker can publish the ingest port on it; read it with `tailscale ip -4`. In userspace mode, the
  default of the Synology package, the address is no host interface, and Tailscale forwards inbound tailnet connections to
  `127.0.0.1`, where the web port is published: publishing ingest there would leave only the ACL between the tailnet and the web UI.
  On Synology the package is switched to TUN mode as [INSTALL.md](../INSTALL.md) shows.
- **Not verified on a host** (open for the Product Manager on the Synology host, #45), stated as reported or expected: that Docker
  cannot bind the tailnet address in userspace mode and Tailscale forwards to `127.0.0.1` there (issue #40, record 0060); the
  Synology steps to TUN mode (`tailscale configure-host`, a package restart, repeated at boot by a triggered task, from Tailscale's
  Synology documentation); that after a reboot Docker may start the container before Tailscale has its address — Docker's userland
  proxy binds the host address and fails while it is missing, the iptables-only path may not, and a container whose binding failed
  when the daemon started may not be restarted by `unless-stopped`; remedy: `docker compose up -d` once Tailscale is up; and that
  Synology Container Manager and QNAP Container Station honor `${VAR:?message}` (Compose v2 does, checked by the smoke test).
- **LAN hosts may reach the ingest port.** Docker matches a published port by its destination address only, so a host on the LAN
  that routes packets to the tailnet address may reach the ingest port there, as it may the web port. The token, bound to the one
  agent ID, is the boundary.

## Related decisions

- [0017](../decisions/0017-ingest-via-tailnet-address-and-published-port.md) — why the host's tailnet address and a published port, not `tsnet`.
- [0023](../decisions/0023-tls-through-a-reverse-proxy.md) — why TLS is terminated by a reverse proxy.
- [0059](../decisions/0059-healthz-checks-the-database-and-the-binary-is-the-health-probe.md) — why `/healthz` reads the database and the binary is the probe.
- [0060](../decisions/0060-compose-file-port-bindings-volumes-and-memory-limit.md) — why these port bindings, volumes and limits.
- [0072](../decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md) — why no arguments run the service and a JSON log handler.
- [0081](../decisions/0081-backend-host-two-listeners-json-logs-blazor-interactive-server.md) — why two labelled listeners, JSON logs and Blazor Interactive Server.
- [0124](../decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md) — why the ingest request carries one
  batch with a bearer token and is acknowledged by its status, and why a refused batch is marked with a header.
- [0126](../decisions/0126-ingest-endpoint-token-bound-to-one-agent-id-two-slots-through-the-commit-no-rate-cap.md) — why the token
  is bound to one configured agent ID, two slots are held through the commit, a free-space floor stands instead of a rate cap, and
  how the shutdown bounds a write.
- [0127](../decisions/0127-backend-host-needs-tailscale-in-tun-mode-ingest-port-on-a-required-bind-address.md) — why TUN mode and a
  required `INGEST_BIND_ADDRESS`.
- [0128](../decisions/0128-csharp-decoder-reads-the-gzip-framing-itself-to-match-the-go-decoder.md) — why the decoder the endpoint
  uses reads the gzip framing itself.
- [0103](../decisions/0103-web-login-server-side-sessions-static-login-page-circuits-bound-to-session.md), [0104](../decisions/0104-argon2id-password-hash-konscious-strict-phc-form-hash-password-command.md) and [0105](../decisions/0105-forwarded-headers-only-from-trusted-proxies-failed-logins-locked-per-client-and-capped.md) — the login, the password hash and the proxy trust.

## Not here

- The configuration file and the secrets: [Configuration and secrets](configuration-and-secrets.md).
- The import sub-command: [Log import](log-import.md). The incident reconstruction itself: [Detection, alerts and reports](detection-and-reports.md).
- The database: [Storage](storage.md). The batch format, its accepted forms and the measured memory of one ingest request:
  [Wire format](wire-format.md). The agent's sending, its waits and its
  reaction to each answer: [Agent](agent.md), *Sending*.
- The pages, layout, theme, static files, charts and security headers of the web UI: [Web UI](web-ui.md).
- Passwords, sessions, the failed-sign-in limits and the proxy trust: [Web login](web-login.md).
- The Tailscale ACL and the security model of the monitored server: the network area.

## Implementation

`Vandox.Backend` (`Cli/CommandLine`, `Hosting/BackendApp`, `ServeCommand`, `ListenerRoutes`, `PortRoutingMiddleware`, `SecurityHeadersMiddleware`, `HealthEndpoint`,
`PingChecker`, `Cli/HealthCheckCommand`, `Cli/StoreCommand`, `Cli/TemplatesCommand`, `Logging/JsonLineLogger`; `Ingest/IngestEndpoint` with
`IngestSettings`, `IngestToken`, `IngestLimits`, `IngestStatus`, `IngestRequest`, `IngestRefusalLog`, `IngestFieldPath`, `IngestWriteGate`,
`FreeSpace` and `ConnectionCap`; the log lines in `Hosting/BackendLog`), `deploy/backend/` (Dockerfile, compose file, example
configuration), `.github/scripts/smoke-test-backend.sh`.
