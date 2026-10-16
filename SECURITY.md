# Security Policy

## Supported Versions

No release has been published yet, so fixes go to `main`. From the first release (v0.1.0) on, only the
latest release receives security fixes.

| Version | Supported |
| ------- | --------- |
| latest  | Yes       |
| older   | No        |

## Reporting a Vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Report vulnerabilities by e-mail to:

**Development@e-networld.de**

Include in your report:

- A clear description of the vulnerability
- Steps to reproduce
- Potential impact
- Any suggested fix (optional)

You will receive an acknowledgement within **5 business days**. We aim to release a fix or mitigation
within **30 days** for confirmed vulnerabilities. We will keep you informed of progress throughout the
process.

## Deployment Security Considerations

Vandox is meant for a private setup: `vandox-agent` runs on the monitored server and `vandoxd` runs as a Docker
container on a Docker host in the home network (for example a NAS), behind a TLS-terminating reverse proxy. The two communicate only
over a private Tailscale network. An operator must not expose `vandoxd` to the internet, must protect the
web UI with its login, and must keep secrets (tokens, keys) only in environment variables or Docker
secrets, never in the configuration file or on the command line. The agent must run as the dedicated user
`vandox-agent`, never as root, with only the rights listed in `deploy/agent/`, and the unit's confinement
(system-call filter, no further capabilities) must be kept; its capabilities give it read access to
everything on the server, so treat the server's credentials as readable by the agent. The Tailscale ACL must
allow the monitored server only the ingest port on the backend host, and the Telegram user allowlist must be
set.

Keep the web port as `deploy/backend/docker-compose.yml` publishes it, on `127.0.0.1` (or on the address of the
host that runs the reverse proxy). That binding is defense in depth, not an access boundary: depending on the
Docker Engine version and the host firewall, hosts on the LAN may reach a published container port directly,
so use a current Docker Engine and a host firewall where that matters. The ingest port is not published until
the ingest API exists. The agent token file (`secrets/vandox_agent_token`) must be owned by UID 65532 with mode
`0400`, never world-readable, because Compose sets no owner or mode on file secrets outside Swarm. The same holds for the
web password hash file (`secrets/vandox_web_password_hash`, created with `vandoxd hash-password` as the README shows, without `-t`
so the terminal does not echo the password); the hash is argon2id, but a copied file still allows offline guessing.

The web UI login needs `web.trusted_proxies` to know who is who behind the reverse proxy. Set it to the proxy's address only (as the
container sees it), because every connection from a listed address may choose its own client address with `X-Forwarded-For`; use a
current Docker Engine (28.3.3 or later) so that no neighbor host reaches the published port through that address. Without the
option, all clients behind the proxy share the proxy's address: five wrong passwords from any of them lock all of them out for 15
minutes, and the cookies are never `Secure`. Independent of that option, 100 failed sign-ins within an hour lock the sign-in for
everyone, and anyone who can reach the login can provoke it; restarting the container clears the lock (and ends all sessions). Give the
UI a host name of its own (`docs/REVERSE_PROXY.md`). Whether a port published on `127.0.0.1` is reachable from the LAN depends on
the Docker host and is the operator's to check (`docs/REVERSE_PROXY.md`, *Check the Docker Engine*); the login does not depend on it.

## Scope

The following are considered in scope for vulnerability reports:

- The attack surface listed under *Security areas* in `.squad/project.md`: ingest authentication, the
  Tailscale ACL and port binding as documented, web UI login, command signing for remote actions once
  released, the Telegram allowlist, agent privileges, the MariaDB monitoring user, secrets handling, file
  writes, parsing of external input, outbound calls including their transport security, and logging and
  display of external data in log output, the web UI and Telegram messages, and the release pipeline and
  published artifacts (agent binary, checksums, Docker image)
- Attacks by an unprivileged local user of the monitored server against the agent or its secrets (for
  example reading a secret from `/proc/<pid>/cmdline` or the environment, abusing the agent's rights, or
  escaping the agent unit's confinement to write or run code as another user)
- Dependency vulnerabilities in packages consumed by the project

The following are **out of scope**:

- Attacks that require root or physical access to the monitored server or to the backend host
- Issues arising from misconfiguration of the deployment environment
