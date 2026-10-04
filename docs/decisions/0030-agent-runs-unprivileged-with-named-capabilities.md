# 0030: The agent runs as a dedicated user with only named rights, not as root

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #7
- **Supersedes:** —

## Context

The agent runs on an internet-facing server that hosts customer services. It reads data that ordinary
users cannot see (other users' processes and their open files and sockets in `/proc`, the system journal
and the log files under `/var/log`) and must authenticate to MariaDB as OS user `vandox-agent` (0013). It
also parses untrusted input (log lines, process names), so a flaw in it must not hand out root. Reading the
state of systemd units over D-Bus is allowed to every local user and needs no extra right. The only planned
write-side right is restarting configured units for local self-healing (issues #69, #70, v0.6.0).

## Options considered

1. **Run as root** — every read works; a compromised agent is a compromised server.
2. **Dedicated user without extra rights** — minimal, but the agent cannot see other users' processes and
   file descriptors or the full journal, so the forensics are incomplete.
3. **Dedicated system user `vandox-agent` with only the rights it needs** — for reading: membership in the
   groups `adm` (log files) and `systemd-journal` (journal) and the minimal `AmbientCapabilities` that
   `/proc/<pid>/fd` and `smaps_rollup` of other users' processes require, granted by the systemd unit
   (issue #42); for restarting units, once self-healing exists: a polkit rule that lets this user restart
   only the units listed in the configuration, with no sudo and no shell (issue #70).

## Decision

Option 3: the agent runs as the dedicated system user `vandox-agent` without a login shell, never as root.
Each group membership and each capability is listed and justified in the deployment files under
`deploy/agent/` when the collector that needs it is introduced; the polkit rule is added only with the
restart feature and covers only the configured units. Anything not listed is not granted.

## Consequences

- A compromised agent gains only the listed rights, not root.
- Adding a collector or feature that needs a new right changes `deploy/agent/` and is a `security`-tier
  change.
- The installation instructions create the user, its groups and the systemd unit; the polkit rule is
  installed and removed by the install and uninstall scripts from v0.6.0 on.
