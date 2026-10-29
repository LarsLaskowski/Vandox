# Network and security model

## Scope

Who may reach whom: the transport between the agent and the backend, the exposure of the web UI, the agent's access to MariaDB, and the
resource budget of the monitored server that limits what Vandox may cost there. The container, ports and compose file are in the backend
host area; the agent's own rights are in the agent area; secrets are in [Configuration and secrets](configuration-and-secrets.md).

## Agent to backend

Part of v0.2.0; v0.1.0 has no agent-to-backend traffic. The agent's sender posts the batches to `backend.url`
([Agent](agent.md), *Connection*, *Sending*), and the backend's ingest endpoint stores them; the compose file publishes the ingest port
on the address in `INGEST_BIND_ADDRESS`, the backend host's tailnet address. The request, the answers and the endpoint's limits are in
the backend host area (*Ingest endpoint*, *Compose file*).

- **The backend host runs Tailscale in TUN mode.** Only there is the tailnet address a host interface Docker can bind. In
  userspace-networking mode (the default of the Synology package) it is not, and Tailscale forwards inbound tailnet connections to
  `127.0.0.1`, where the web port is published, which would leave only the ACL between the tailnet and the web UI; that mode is not
  supported. The userspace behavior is as reported in issue #40 and record 0060, not verified on a host (backend host area,
  *Exposure and trust*).
- **The binding is defense in depth.** Docker matches a published port by its destination address only, so a host on the LAN that
  routes packets to the tailnet address may reach the ingest port as it may the web port. The token, bound to the one agent ID in
  `ingest.agent_id`, is the boundary of ingest.

- Agent and backend communicate **only over Tailscale**. The tailnet ACL allows the monitored server to reach **only the ingest port on
  the backend host** and nothing else, so a compromised server cannot reach the rest of the home network. The ACL is part of the
  deployment documentation and must be kept strict.
- The monitored server joins the tailnet as a tagged node (`tag:vandox-agent`, requested with `tailscale up --advertise-tags` after
  the policy names the tag under `tagOwners`). An untagged node acts as the user who logged it in, and rules only add permissions,
  so the rule below could not be its only one. The policy's only rule with a source matching the server allows TCP to the ingest port
  on the backend host (in the `grants` form `"ip": ["tcp:<port>"]`; a port without a protocol or `tcp:*` allows more). No other
  rule's source may match it: the tailnet's default rule with `*` as source matches tagged nodes too and is replaced before the server
  joins, and neither `autogroup:tagged` nor `tag:vandox-agent` may be the source of another rule. It needs no Tailscale SSH, no
  accepted subnet routes and no rule with it as destination, because the agent listens on no port. The setup steps and the checks
  (the tag, a `404` from the ingest port, a probe of another port that the policy blocks, with `No route to host` or a timeout) are in `deploy/agent/README.md` (*Tailscale on the
  monitored server*).
- **The example policy file is the source of the rules.** `deploy/agent/tailnet-policy.hujson` is the complete policy the maintainer
  saves: both tags owned by `autogroup:admin`, the agent's one `tcp:<ingest port>` grant, `autogroup:member` to `autogroup:self`, and a
  commented SSH opt-in. The README holds no copy. It is not in the install bundle. Go tests in `deploy/agent` read it (a strip helper and a
  strict token walk that refuses duplicate keys) and fail when a source other than the two known ones appears, when the agent has a second
  rule, when a grant reaches the backend host but for the agent's ingest port, or when the commented opt-in is anything but SSH
  ([record 0132](../decisions/0132-example-tailnet-policy-file-pinned-by-tests-no-default-maintainer-grant.md)). The file also carries two policy
  `tests` entries (the agent reaches the ingest port and not ports 22, 8080 and 443, and not the ingest port over UDP) meant to make the
  admin console refuse a later edit that widens the server; that the console evaluates them on save is **not yet verified** (issue #338).
- **A compromised root cannot widen the policy.** With no `ssh` rule, no `autoApprovers` entry and no grant to a subnet or
  `autogroup:internet` that matches `tag:vandox-agent`, a root that turns on Tailscale SSH, advertises a route or an exit node, accepts
  routes or selects an exit node still reaches only the ingest port. The README's settings line turns these off anyway and a settings check
  (`tailscale debug prefs`) shows them; neither has been run on the production server yet (issue #338).
- **Maintainer devices get nothing on the backend host by default.** The policy grants them no access to the backend host's tailnet address
  but what the maintainer enables on purpose (a commented SSH line); there is none for the web port.
- **After a reboot** Tailscale should come up by itself with its tag and settings, and the agent sends again from its spool; the README
  has the check, not yet run on the production server (issue #338).
- **Residual while Tailscale is not up.** Tailscale's routes exist only while it runs, and the unit's `IPAddressAllow=` filters by
  address, not interface. While Tailscale is down (before its network map after a boot, stopped, logged out, the node removed), a send to the
  backend's tailnet address follows the server's default route and fails as `connection failed` or a timeout, and whoever answers at that
  address on the provider's path gets the token and the batch in clear. The exposure predates the policy file, is stated in the README, and
  is not closed; issue #337 adds a control (record 0132). The records stay in the spool and go out once the tailnet is up.
- There is no inbound port on the home router. Transport encryption comes from Tailscale; the ingest endpoint still authenticates every
  request with the agent token, because the network binding is not a security boundary.
- The backend does not embed Tailscale; the host's Tailscale carries the traffic (see the backend host area).
- **The agent's unit enforces this on the server.** `IPAddressDeny=any` with `IPAddressAllow=localhost 100.64.0.0/10 fd7a:115c:a1e0::/48` lets the
  agent's process reach only localhost and the tailnet ranges, so a `backend.url` elsewhere, and a host name resolved by a name server outside
  localhost and the tailnet, fails as `connection failed`; the install script warns about both. Use the tailnet address: the documented setup turns Tailscale's DNS off on the monitored server (`tailscale set --accept-dns=false`),
  because its policy lets the server reach only the ingest port, so Tailscale's name servers can be out of reach and lookups can fail; MagicDNS names then do not resolve there
  ([Agent](agent.md), *Installation*, [record 0131](../decisions/0131-agent-unit-and-install-scripts-credential-tailnet-allow-list-checksum-remove-and-purge.md)).
- The agent sends its token only in the `Authorization` header of its requests to `backend.url`, connects only to that URL's host and
  port (no proxy from the environment, no redirect followed), verifies the certificate of an `https` URL and never switches the check
  off. Plain `http` is meant only for the backend host's tailnet address or MagicDNS name, where Tailscale encrypts and authenticates
  the transport. The agent treats every answer as untrusted: it reads at most 4,096 bytes of the body, discards them and looks only
  at the status and the exact value of the header `Vandox-Refused`.
- The ingest endpoint checks the token before it reads or decodes a request's body, so a peer without the token costs the backend no
  decoding, and it marks a refused batch (`Vandox-Refused: batch`) only on an authenticated request. A token holder (the monitored
  server, or whoever has taken it over) can write batches of its own agent ID only, bounded per request in memory and time, and
  only until the free space of the data volume falls to `ingest.min_free`.

## Web UI

- The web UI is reachable **in the home LAN only** and requires a **login**. It is not exposed to the internet and is not offered on the
  tailnet to the monitored server.
- The example tailnet policy grants no source access to the backend host's tailnet address except the agent's `tcp:<ingest port>`; the
  maintainer's devices reach the web UI in the home LAN. A grant the maintainer adds to a subnet route of the home LAN gives the devices behind
  it the web UI at its LAN address, as if they were in the home LAN (record 0016 already allows it there, with the login).
- The login is one administrator password, stored only as an argon2id hash, with server-side sessions, protection against cross-site
  requests and limits on failed sign-ins; the service trusts `X-Forwarded-For` and `X-Forwarded-Proto` only from the proxy addresses
  the operator lists, and never takes the network position of a request as proof of having passed the proxy. The rules are in the
  [Web login](web-login.md) area, the setup of the proxy in [REVERSE_PROXY.md](../REVERSE_PROXY.md).
- TLS is terminated by a reverse proxy (backend host area). Access from outside the home needs a separate, deliberate decision.

## MariaDB access

The agent reads the database server's state (status variables, process list) over the **local socket as the MariaDB user
`vandox-agent`, identified by `unix_socket` and granted only `PROCESS`**. No database password exists on disk. The agent sees the server
status and the process list, including the query text of running statements, but cannot read tables. Creating the user is part of the
agent's installation: `install.sh` creates it on the first install, `uninstall.sh --purge` drops it with
`deploy/agent/mariadb-user-remove.sql`.

- **The account.** `deploy/agent/mariadb-user.sql` is its one source: `CREATE OR REPLACE USER 'vandox-agent'@'localhost' IDENTIFIED VIA
  unix_socket WITH MAX_USER_CONNECTIONS 2` and `GRANT PROCESS ON *.* TO 'vandox-agent'@'localhost'`, nothing else. The operator runs it as
  the database administrator (`mariadb < mariadb-user.sql` as root, or `plesk db < mariadb-user.sql` on Plesk), and may run it again, for
  example after a Plesk update dropped the user; it resets the account to exactly this state. Plesk's administrator account is never
  used. With at most 2 connections for the account, one stays free beside the agent's one.
- **The login.** The agent sends the user name and empty authentication data; `unix_socket` accepts it only for the OS user of the same
  name. No password or other credential is read, stored or sent: a server that asks for another authentication method gets one empty
  answer and nothing more ([Agent](agent.md), *MariaDB*).
- **Fixed, read-only statements.** Over one connection per run the agent sends a ping, `SHOW GLOBAL STATUS`, `SHOW GLOBAL VARIABLES`, the
  two questions of the account check and, under load or memory pressure, `SHOW FULL PROCESSLIST`; no statement takes input, and the
  client neither offers nor answers a local-file request.
- **The account check.** Every run that logs in and reads the status and variables asks which account it got (`CURRENT_USER()`) and whether that account holds `PROCESS`; an
  account that is not `vandox-agent`@`localhost` (an anonymous account that accepted the login) or lacks `PROCESS` is reported in the
  agent's log as `mariadb user not as installed` with how to fix it, and the agent keeps collecting what it can.
- **A refusal is recorded.** A server that refuses the agent with an error packet, before its greeting (no free connection) or at the
  login (the user missing or not identified via `unix_socket`), is recorded as `not_answering` with the error number (`error_code`), so a
  missing user shows in the data, and the agent logs `mariadb user missing or not allowed` for 1045 and 1698.
- **The statement texts are sensitive.** `PROCESS` shows the statements of every connection, which can hold customer data and
  credentials (`IDENTIFIED BY '...'`, values of an `INSERT`). The agent records them only in its thread snapshot (more than 10 running
  threads or memory pressure), never in a log line, and sends them to the backend, which stores and shows them escaped; the backend and
  its web UI are therefore as sensitive as the database's process list.

## The monitored server

- The server keeps its **2 GB of RAM**. Relief for memory pressure comes from swap, tuning the backups and an inventory of running
  services (disabled only reversibly, see the detection area), guided by what Vandox shows.
- Vandox must therefore be frugal on the server: the agent's memory and CPU use is a design constraint, and the analysis must show
  memory consumers over time.

## Related decisions

- [0010](../decisions/0010-tailscale-with-strict-acl.md) — why Tailscale with a strict ACL.
- [0131](../decisions/0131-agent-unit-and-install-scripts-credential-tailnet-allow-list-checksum-remove-and-purge.md) — why the agent's unit
  allows only tailnet addresses and localhost.
- [0124](../decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md) — why the agent sends one batch per
  request with a bearer token and judges the answer by its status and one fixed header value.
- [0126](../decisions/0126-ingest-endpoint-token-bound-to-one-agent-id-two-slots-through-the-commit-no-rate-cap.md) — why the
  token is bound to one agent ID and a free-space floor bounds what a token holder can write.
- [0127](../decisions/0127-backend-host-needs-tailscale-in-tun-mode-ingest-port-on-a-required-bind-address.md) — why TUN mode and a
  required bind address.
- [0016](../decisions/0016-web-ui-in-home-lan-with-login.md) — why the web UI is LAN-only with a login.
- [0132](../decisions/0132-example-tailnet-policy-file-pinned-by-tests-no-default-maintainer-grant.md) — why the example policy is a file
  pinned by tests, gives maintainer devices nothing by default, and leaves the token's exposure while Tailscale is down to a follow-up.
- [0013](../decisions/0013-mariadb-access-via-unix-socket-process-privilege.md) — why a `unix_socket` user with only `PROCESS`.
- [0118](../decisions/0118-mariadb-collector-hand-written-client-captured-statements-refusals-as-not-answering-with-error-code.md) — why the
  agent's own minimal client, fixed captured statements and a refusal recorded with its error number.
- [0025](../decisions/0025-server-ram-stays-at-2-gb.md) — why the server's RAM stays at 2 GB.

## Not here

- Container, ports, compose file and TLS termination: the backend host area.
- The agent's user, capabilities and confinement: the agent area.
- Secret handling: [Configuration and secrets](configuration-and-secrets.md).
