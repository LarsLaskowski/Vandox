# Network and security model

## Scope

Who may reach whom: the transport between the agent and the backend, the exposure of the web UI, the agent's access to MariaDB, and the
resource budget of the monitored server that limits what Vandox may cost there. The container, ports and compose file are in the backend
host area; the agent's own rights are in [Agent rights](agent-rights.md); secrets are in [Configuration and secrets](configuration-and-secrets.md).

## Agent to backend

Part of v0.2.0; v0.1.0 has no agent-to-backend traffic. The agent's sender posts the batches to `backend.url`
([Agent sending](agent-sending.md)), and the backend's ingest endpoint stores them; the compose file publishes the ingest port
on the address in `INGEST_BIND_ADDRESS`, the backend host's tailnet address. The request, the answers and the endpoint's limits are in
[Ingest endpoint](ingest-endpoint.md), the compose file in [Backend host](backend-host.md) (*Compose file*).

- **The backend host runs Tailscale in TUN mode.** Only there is the tailnet address a host interface Docker can bind. In
  userspace-networking mode (the default of the Synology package) it is not, and Tailscale forwards inbound tailnet connections to
  `127.0.0.1`, where the web port is published, which would leave only the ACL between the tailnet and the web UI; that mode is not
  supported. The userspace behavior is as reported in issue #40 and record 0060, not verified on a host ([Backend host](backend-host.md),
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
- **The README documents the policy as console settings.** `deploy/agent/README.md` (*Tailscale on the monitored server*, step 2) names
  what the maintainer sets up in the admin console: both tags owned by `autogroup:admin`, the agent's one rule (TCP on the ingest port to
  `tag:vandox-backend`), `autogroup:member` to `autogroup:self`, no default rule, and SSH from the admins to the backend host as the only
  opt-in. There is no policy file in the repository: the admin console no longer takes a pasted policy, so a file could not be applied
  as it is ([record 0132](../decisions/0132-tailnet-policy-documented-as-console-settings-no-default-maintainer-grant.md)). The settings
  are in place on the production tailnet, and the probes of step 4 show their effect (issue #338). Go tests in `deploy/agent` pin the
  README's list of settings word for word and the key items of its list of rules that must never be added, so a widening edit of the
  README fails them. Nothing in the console refuses a later edit that widens what the server reaches; the probes of step 4 are the check.
- **A compromised root cannot widen the policy.** With no `ssh` rule, no `autoApprovers` entry and no grant to a subnet or
  `autogroup:internet` that matches `tag:vandox-agent`, a root that turns on Tailscale SSH, advertises a route or an exit node, accepts
  routes or selects an exit node still reaches only the ingest port. The README's settings line turns these off anyway and a settings check
  (`tailscale debug prefs`) shows them; both have run on the production server with the expected values (issue #338).
- **Maintainer devices get nothing on the backend host by default.** The policy grants them no access to the backend host's tailnet address
  but what the maintainer enables on purpose (the one opt-in: a rule from `autogroup:admin` to `tag:vandox-backend` on TCP port 22); there
  is none for the web port.
- **While Tailscale is not up, the agent's sender binds to `tailscale0`.** Tailscale's routes exist only while it runs, and the unit's
  `IPAddressAllow=` filters by address, not interface, so without a binding a send to the backend's tailnet address would follow the
  server's default route onto the provider's network. The sender therefore binds every connection whose address is not a loopback
  address (`127.0.0.0/8`, `::1`, an IPv4-mapped loopback) to the interface `tailscale0` (`SO_BINDTODEVICE`) before it connects; an
  address it cannot parse is bound, and a connection to a loopback address (a backend on the same host) is not. The kernel then routes
  the connection through that interface only, whatever the routing tables and the network manager hold. Without the device (`tailscaled`
  stopped, a boot before it started) the connect fails at once, before a packet is sent, as `connection failed`; with the device but
  without routes (before its network map, logged out) the packets go into Tailscale and the send ends as a `timeout`. Neither reaches
  the provider's network, and the records stay in the spool until the tailnet is up. The server must run Tailscale in TUN mode with the
  default interface name; otherwise every send fails. Binding a socket to a device needs no capability on Linux 5.7 and later. The
  server's checks (the route while up, the egress while `tailscaled` is stopped) have not yet run (issue #372). Record 0134.
- **Residual: a host name in `backend.url`.** The guarantee holds only with the backend's tailnet address. A host name is resolved
  through the local `systemd-resolved` stub, which the unit's `localhost` allows and which asks the provider's name servers (Tailscale's
  DNS is off); the lookup is unbound, and a forged answer of `127.0.0.1` or `::1` meets the loopback exemption and sends the token
  unbound to whatever listens on that local port. This predates the binding; issue #373 proposes closing it. A root user can create an
  interface named `tailscale0`, which changes nothing, as root already reads the token.
- There is no inbound port on the home router. Transport encryption comes from Tailscale; the ingest endpoint still authenticates every
  request with the agent token, because the network binding is not a security boundary.
- The backend does not embed Tailscale; the host's Tailscale carries the traffic ([Backend host](backend-host.md)).
- **The agent's unit enforces this on the server, as the second layer.** The first is the binding to `tailscale0` above.
  `IPAddressDeny=any` with `IPAddressAllow=localhost 100.64.0.0/10 fd7a:115c:a1e0::/48` lets the agent's process reach only localhost
  and the tailnet ranges, so a `backend.url` elsewhere fails as `connection failed`; the install script warns about it. The allow-list
  does not stop a host name: the local name-server stub is localhost, so a lookup through it is not blocked (the residual above), and
  only a name server outside localhost and the tailnet fails. Use the tailnet address: the documented setup turns Tailscale's DNS off on the monitored server (`tailscale set --accept-dns=false`),
  because its policy lets the server reach only the ingest port, so Tailscale's name servers can be out of reach and lookups can fail; MagicDNS names then do not resolve there
  ([Agent installation](agent-installation.md), [record 0131](../decisions/0131-agent-unit-and-install-scripts-credential-tailnet-allow-list-checksum-remove-and-purge.md), [record 0134](../decisions/0134-sender-binds-non-loopback-connections-to-the-tailnet-device.md)).
- The agent sends its token only to `backend.url`, uses no proxy and follows no redirect, never switches certificate verification
  off, uses plain `http` only to the backend host's tailnet address, where Tailscale encrypts and authenticates the transport, and
  treats every answer as untrusted (at most 4,096 bytes of body read and discarded, only the status and the exact value of the header
  `Vandox-Refused` looked at); the client's rules are in [Agent sending](agent-sending.md) (*The request*).
- The ingest endpoint checks the token before it reads or decodes a request's body, so a peer without the token costs the backend no
  decoding, and it marks a refused batch (`Vandox-Refused: batch`) only on an authenticated request. A token holder (the monitored
  server, or whoever has taken it over) can write batches of its own agent ID only, bounded per request in memory and time, and
  only until the free space of the data volume falls to `ingest.min_free`. A peer without the token cannot keep the agent out of the
  listener's 16 connections by holding them: a connection that has not passed the token check is evicted for a new one
  ([Ingest endpoint](ingest-endpoint.md)).

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
- TLS is terminated by a reverse proxy ([Backend host](backend-host.md)). Access from outside the home needs a separate, deliberate decision.

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
  answer and nothing more ([Agent MariaDB collector](agent-mariadb.md)).
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
- [0017](../decisions/0017-ingest-via-tailnet-address-and-published-port.md) — why ingest is reached on the backend host's tailnet
  address through a published port.
- [0134](../decisions/0134-sender-binds-non-loopback-connections-to-the-tailnet-device.md) — why the sender binds every non-loopback
  connection to `tailscale0`.
- [0131](../decisions/0131-agent-unit-and-install-scripts-credential-tailnet-allow-list-checksum-remove-and-purge.md) — why the agent's unit
  allows only tailnet addresses and localhost.
- [0124](../decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md) — why the agent sends one batch per
  request with a bearer token and judges the answer by its status and one fixed header value.
- [0126](../decisions/0126-ingest-endpoint-token-bound-to-one-agent-id-two-slots-through-the-commit-no-rate-cap.md) — why the
  token is bound to one agent ID and a free-space floor bounds what a token holder can write.
- [0127](../decisions/0127-backend-host-needs-tailscale-in-tun-mode-ingest-port-on-a-required-bind-address.md) — why TUN mode and a
  required bind address.
- [0016](../decisions/0016-web-ui-in-home-lan-with-login.md) — why the web UI is LAN-only with a login.
- [0132](../decisions/0132-tailnet-policy-documented-as-console-settings-no-default-maintainer-grant.md) — why the policy is documented in the README as console
  settings, the README pinned by tests, gives maintainer devices nothing by default, and leaves the token's exposure while Tailscale is down to a follow-up.
- [0013](../decisions/0013-mariadb-access-via-unix-socket-process-privilege.md) — why a `unix_socket` user with only `PROCESS`.
- [0118](../decisions/0118-mariadb-collector-hand-written-client-captured-statements-refusals-as-not-answering-with-error-code.md) — why the
  agent's own minimal client, fixed captured statements and a refusal recorded with its error number.
- [0025](../decisions/0025-server-ram-stays-at-2-gb.md) — why the server's RAM stays at 2 GB.

## Not here

- Container, ports, compose file and TLS termination: [Backend host](backend-host.md); the ingest endpoint:
  [Ingest endpoint](ingest-endpoint.md).
- The agent's user, capabilities and confinement: [Agent rights](agent-rights.md); its unit and install scripts:
  [Agent installation](agent-installation.md); the sender's client rules: [Agent sending](agent-sending.md).
- Secret handling: [Configuration and secrets](configuration-and-secrets.md).

## Implementation

The sender's binding to `tailscale0` is `HTTPTransport` in `internal/sender`; the unit's address allow-list is in
`deploy/agent/vandox-agent.service`; Go tests in `deploy/agent` pin the README's tailnet settings and rules.
