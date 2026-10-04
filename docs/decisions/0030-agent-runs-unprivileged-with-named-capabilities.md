# 0030: The agent runs as a dedicated user with named capabilities and a polkit rule, not as root

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #7
- **Supersedes:** —

## Context

The agent runs on an internet-facing server that hosts customer services. It reads data that ordinary
users cannot see (other users' processes and their open files and sockets in `/proc`, the system journal,
unit state over systemd D-Bus) and must authenticate to MariaDB as OS user `vandox-agent` (0013). It also
parses untrusted input (log lines, process names), so a flaw in it must not hand out root.

## Options considered

1. **Run as root** — every read works; a compromised agent is a compromised server.
2. **Dedicated user without extra rights** — minimal, but the agent cannot see other users' processes and
   file descriptors or the full journal, so the forensics are incomplete.
3. **Dedicated system user `vandox-agent` with only the rights it needs** — the Linux capabilities the
   collectors need, granted by the systemd unit; journal access through group membership; a polkit rule
   that allows exactly the D-Bus actions the agent needs, for that user only.

## Decision

Option 3: the agent runs as the dedicated system user `vandox-agent`, never as root. Each capability, each
group membership and each polkit action is listed and justified in the deployment files under
`deploy/agent/` when the collector that needs it is introduced; anything not listed is not granted.

## Consequences

- A compromised agent gains only the listed rights, not root.
- Adding a collector that needs a new right changes `deploy/agent/` and is a `security`-tier change.
- The installation instructions must create the user, the systemd unit and the polkit rule.
