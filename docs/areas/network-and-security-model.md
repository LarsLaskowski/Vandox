# Network and security model

## Scope

Who may reach whom: the transport between the agent and the backend, the exposure of the web UI, the agent's access to MariaDB, and the
resource budget of the monitored server that limits what Vandox may cost there. The container, ports and compose file are in the backend
host area; the agent's own rights are in the agent area; secrets are in [Configuration and secrets](configuration-and-secrets.md).

## Agent to backend

- Agent and backend communicate **only over Tailscale**. The tailnet ACL allows the monitored server to reach **only the ingest port on
  the backend host** and nothing else, so a compromised server cannot reach the rest of the home network. The ACL is part of the
  deployment documentation and must be kept strict.
- There is no inbound port on the home router. Transport encryption comes from Tailscale; the ingest endpoint still authenticates every
  request with the agent token, because the network binding is not a security boundary.
- The backend does not embed Tailscale; the host's Tailscale carries the traffic (see the backend host area).

## Web UI

- The web UI is reachable **in the home LAN only** and requires a **login**. It is not exposed to the internet and is not offered on the
  tailnet to the monitored server.
- Authentication and sessions are a security area. TLS is terminated by a reverse proxy (backend host area). Access from outside the
  home needs a separate, deliberate decision.

## MariaDB access

The agent reads the database server's state (status variables, process list) over the **local socket as the MariaDB user
`vandox-agent`, identified by `unix_socket` and granted only `PROCESS`**. No database password exists on disk. The agent sees the server
status and the process list, including the query text of running statements, but cannot read tables. Creating the user is part of the
agent's installation instructions.

## The monitored server

- The server keeps its **2 GB of RAM**. Relief for memory pressure comes from swap, tuning the backups and an inventory of running
  services (disabled only reversibly, see the detection area), guided by what Vandox shows.
- Vandox must therefore be frugal on the server: the agent's memory and CPU use is a design constraint, and the analysis must show
  memory consumers over time.

## Related decisions

- [0010](../decisions/0010-tailscale-with-strict-acl.md) — why Tailscale with a strict ACL.
- [0016](../decisions/0016-web-ui-in-home-lan-with-login.md) — why the web UI is LAN-only with a login.
- [0013](../decisions/0013-mariadb-access-via-unix-socket-process-privilege.md) — why a `unix_socket` user with only `PROCESS`.
- [0025](../decisions/0025-server-ram-stays-at-2-gb.md) — why the server's RAM stays at 2 GB.

## Not here

- Container, ports, compose file and TLS termination: the backend host area.
- The agent's user, capabilities and confinement: the agent area.
- Secret handling: [Configuration and secrets](configuration-and-secrets.md).
