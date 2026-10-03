# Security Policy

## Supported Versions

Only the latest release receives security fixes.

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
container on a Synology NAS in the home network, behind the Synology reverse proxy. The two communicate only
over a private Tailscale network. An operator must not expose `vandoxd` to the internet, must protect the
web UI with its login, must keep secrets (tokens, keys) in environment variables or files readable only by the
service user, and must run the agent with minimal privileges.

## Scope

The following are considered in scope for vulnerability reports:

- The attack surface listed under *Security areas* in `.squad/project.md`: secrets and tokens, web UI
  authentication, the agent-to-backend ingest API, parsing of log files, file writes, outbound calls and
  (planned) remote actions
- Dependency vulnerabilities in packages consumed by the project

The following are **out of scope**:

- Attacks that require local system access or physical access to the host
- Issues arising from misconfiguration of the deployment environment
