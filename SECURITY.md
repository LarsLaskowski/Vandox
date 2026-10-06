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
`0400`, never world-readable, because Compose sets no owner or mode on file secrets outside Swarm.

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
