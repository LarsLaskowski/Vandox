# 0030: The agent runs as a dedicated user with only named rights, not as root

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Agent
- **Source:** Issue #7
- **Supersedes:** —

## Context

The agent runs on an internet-facing server that hosts customer services. It reads data that ordinary users cannot see (other users' processes and their open files and sockets in `/proc`, the system journal, the log files) and authenticates to MariaDB as OS user `vandox-agent` (0013). It parses untrusted input (log lines, process names), so a flaw in it must not hand out root. The only planned write-side right is restarting configured units for local self-healing.

Reading other users' processes needs two capabilities, and both reach far beyond that read:

- **`CAP_SYS_PTRACE`** passes the kernel's ptrace access check for every process, which procfs applies when `smaps_rollup` is opened and when `fd` links are read. It also lets the holder attach to any process, root's included, and take over its file descriptors. Unconfined, it is root-equivalent.
- **`CAP_DAC_READ_SEARCH`** bypasses read and directory-search checks on every file; it is needed to list `/proc/<pid>/fd`. It makes every file readable (`/etc/shadow`, private keys, Plesk and MariaDB credentials) and enables `open_by_handle_at`, which bypasses path-based restrictions.

`NoNewPrivileges=` does not limit either capability; it only stops gaining privileges through `execve`.

## Options considered

1. **Run as root** — every read works; a compromised agent is a compromised server.
2. **Dedicated user without extra rights** — the agent cannot see other users' processes and descriptors or the full journal, so the forensics are incomplete (0019). Giving up those reads is a product decision nobody made.
3. **Dedicated user with the two capabilities, unconfined** — `CAP_SYS_PTRACE` lets a compromised agent run code as root, so "not root" would be untrue.
4. **Dedicated user with the two capabilities, confined by the systemd unit** (chosen) — the process-attach system calls are denied and no write-side capability is granted; reads work, writing as or running code as another user does not. The read-everything residual of `CAP_DAC_READ_SEARCH` remains and is stated openly.
5. **A separate small helper holding the capabilities**, the main agent holding none — a smaller attack surface, but a second binary, an IPC protocol and a second process within the 2 GB budget (0025) before the first release. A later record may supersede this one with it.

## Decision

Option 4. The rights, the confinement and the rules for changing them are in the [Agent](../areas/agent.md) area (*Rights*).

## Consequences

- A compromised agent does not run as root and, with the confinement in place, cannot write as or run code as another user through its capabilities.
- **Residual, accepted:** its read access equals root's, through plain `open`/`read` that no system-call filter can block; `ProtectHome=` and `InaccessiblePaths=` do not change this, since a compromised agent bypasses them through `/proc/<pid>/root`. Credentials read that way may lead to root through other services. Option 5 is the way to reduce it.
- The review of the first capability-granting change verifies on the target system that the reads work under the filter and that attaching to another process fails with `EPERM`; no deployment file may present `InaccessiblePaths=` as protection against a compromised agent.
- The installation instructions create the user, its groups and the unit; the polkit rule is installed and removed by the install scripts once self-healing exists.
