# Network and security model

## Scope

Who may reach whom: the transport between the agent and the backend, the exposure of the web UI, the agent's access to MariaDB, and the
resource budget of the monitored server that limits what Vandox may cost there. The container, ports and compose file are in the backend
host area; the agent's own rights are in the agent area; secrets are in [Configuration and secrets](configuration-and-secrets.md).

## Agent to backend

Planned with the agent and the ingest API (v0.2.0); v0.1.0 has no agent-to-backend traffic.

- Agent and backend communicate **only over Tailscale**. The tailnet ACL allows the monitored server to reach **only the ingest port on
  the backend host** and nothing else, so a compromised server cannot reach the rest of the home network. The ACL is part of the
  deployment documentation and must be kept strict.
- There is no inbound port on the home router. Transport encryption comes from Tailscale; the ingest endpoint still authenticates every
  request with the agent token, because the network binding is not a security boundary.
- The backend does not embed Tailscale; the host's Tailscale carries the traffic (see the backend host area).

## Web UI

- The web UI is reachable **in the home LAN only** and requires a **login**. It is not exposed to the internet and is not offered on the
  tailnet to the monitored server.
- The login is one administrator password, stored only as an argon2id hash, with server-side sessions, protection against cross-site
  requests and limits on failed sign-ins; the service trusts `X-Forwarded-For` and `X-Forwarded-Proto` only from the proxy addresses
  the operator lists, and never takes the network position of a request as proof of having passed the proxy. The rules are in the
  [Web login](web-login.md) area, the setup of the proxy in [REVERSE_PROXY.md](../REVERSE_PROXY.md).
- TLS is terminated by a reverse proxy (backend host area). Access from outside the home needs a separate, deliberate decision.

## MariaDB access

The agent reads the database server's state (status variables, process list) over the **local socket as the MariaDB user
`vandox-agent`, identified by `unix_socket` and granted only `PROCESS`**. No database password exists on disk. The agent sees the server
status and the process list, including the query text of running statements, but cannot read tables. Creating the user is part of the
agent's installation instructions.

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
- [0016](../decisions/0016-web-ui-in-home-lan-with-login.md) — why the web UI is LAN-only with a login.
- [0013](../decisions/0013-mariadb-access-via-unix-socket-process-privilege.md) — why a `unix_socket` user with only `PROCESS`.
- [0118](../decisions/0118-mariadb-collector-hand-written-client-captured-statements-refusals-as-not-answering-with-error-code.md) — why the
  agent's own minimal client, fixed captured statements and a refusal recorded with its error number.
- [0025](../decisions/0025-server-ram-stays-at-2-gb.md) — why the server's RAM stays at 2 GB.

## Not here

- Container, ports, compose file and TLS termination: the backend host area.
- The agent's user, capabilities and confinement: the agent area.
- Secret handling: [Configuration and secrets](configuration-and-secrets.md).
