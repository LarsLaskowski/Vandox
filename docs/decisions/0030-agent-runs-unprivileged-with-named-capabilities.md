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

Reading other users' processes (issue #42) needs two capabilities, and both reach far beyond that read:

- **`CAP_SYS_PTRACE`** passes the kernel's ptrace access check for every process. procfs applies that
  check when `/proc/<pid>/smaps_rollup` is opened and when a `/proc/<pid>/fd/*` link is read with
  `readlink`, so the agent needs it for both. The check runs inside `open`/`readlink`; reading procfs does
  not call `ptrace(2)`. The same capability also lets the holder attach to any process, root's included:
  `ptrace(2)` (e.g. `PTRACE_SEIZE`, then code injection), `process_vm_readv(2)`/`process_vm_writev(2)` and
  `pidfd_getfd(2)` (taking over another process's open file descriptors). Unconfined, it is root-equivalent.
- **`CAP_DAC_READ_SEARCH`** bypasses read and directory-search permission checks on every file. The agent
  needs it to list `/proc/<pid>/fd`, a directory with mode `0500` owned by the process's user; without it,
  `CAP_SYS_PTRACE` alone makes `smaps_rollup` readable but not the `fd` links. It also makes every file on
  the server readable (e.g. `/etc/shadow`, private keys, Plesk's and MariaDB's credentials), together with
  `CAP_SYS_PTRACE` the `environ` and `mem` files of every process, and it enables `open_by_handle_at(2)`, which opens files by handle and so bypasses path-based
  restrictions such as `InaccessiblePaths=`.

`NoNewPrivileges=` does not limit either capability: it only stops gaining privileges through `execve`
(setuid binaries, file capabilities), not using ones already held.

## Options considered

1. **Run as root** — every read works; a compromised agent is a compromised server.
2. **Dedicated user without extra rights** — minimal, but the agent cannot see other users' processes and
   file descriptors or the full journal, so the forensics are incomplete (0019, #42). Giving up those reads
   is a product decision that #42 does not ask for.
3. **Dedicated user with the two capabilities, unconfined** — the reads work, but `CAP_SYS_PTRACE` lets a
   compromised agent attach to a root process and run code as root, so "not root" would be untrue.
4. **Dedicated user with the two capabilities, confined by the systemd unit** — the process-attach system
   calls are denied, no write-side capability is granted; reads work, writing as or running code as another
   user does not. The read-everything residual of `CAP_DAC_READ_SEARCH` remains and is stated openly.
5. **A separate small helper holding the capabilities**, the main agent (which parses untrusted input)
   holding none — a smaller attack surface for the capabilities, but a second binary, an IPC protocol and a
   second process within the 2 GB budget (0025) before the first release. Not chosen now; a later record
   may supersede this one with it.

## Decision

Option 4. The agent runs as the dedicated system user `vandox-agent` without a login shell, never as root.
Its rights are:

- for reading: membership in the groups `adm` (log files) and `systemd-journal` (journal), and — from
  #42 on — `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH` as `AmbientCapabilities=`, with
  `CapabilityBoundingSet=` limited to exactly these two;
- for restarting units, once self-healing exists (#70, v0.6.0): a polkit rule that lets this user restart
  only the units listed in the configuration, with no sudo and no shell.

As soon as the unit grants any capability, it must also be confined as follows; this confinement is part of
the decision and is checked in the review of #42 and of every later change to the unit:

- `NoNewPrivileges=yes`.
- `SystemCallFilter=` denies at least `ptrace`, `process_vm_readv`, `process_vm_writev`, `pidfd_getfd` and
  `open_by_handle_at` (with `SystemCallErrorNumber=EPERM`), and `SystemCallArchitectures=native` keeps
  the filter from being bypassed through another system-call ABI. This does not affect the reads above:
  procfs applies the ptrace access check inside `open`/`readlink`/`read`, which stay allowed. It must not
  deny `pidfd_open` or `pidfd_send_signal`, which Go's `os/exec` uses.
- No write-side capability (`CAP_DAC_OVERRIDE`, `CAP_FOWNER`, `CAP_SYS_ADMIN` or any other) is granted, so
  another user's `/proc/<pid>/mem` and files cannot be opened for writing.
- `ProtectHome=yes` and `InaccessiblePaths=` for at least `/etc/shadow` and `/etc/gshadow`, as defence in
  depth against **accidental** reads only (a bug or a misconfigured path in the agent itself); further
  credential stores the agent does not need are added in #42 once their paths on the target system are
  known. They are **not** a limit on a compromised agent: they only hide paths inside the unit's private
  mount namespace, and with `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH` the agent can open the same files
  through another process's root, e.g. `/proc/1/root/etc/shadow` — a plain `open`/`read` that no
  system-call filter can block, and that cannot be closed by hiding other processes in `/proc` without
  breaking the reads #42 needs.

Each group membership, each capability and each confinement setting is listed and justified in the
deployment files under `deploy/agent/` when the collector that needs it is introduced. Anything not listed
is not granted.

## Consequences

- A compromised agent does not run as root and, with the confinement in place, cannot write as or run code
  as another user through its capabilities.
- **Residual, accepted:** its read access equals root's. It can read every file on the server, and the
  memory and environment of every process (`/proc/<pid>/mem`, `/proc/<pid>/environ`), all through plain
  `open`/`read`, which no system-call filter can block; `ProtectHome=` and `InaccessiblePaths=` do not change
  this, since a compromised agent bypasses them through `/proc/<pid>/root`. Credentials read that way may
  lead to root through other services (password reuse, Plesk or MariaDB administration). Option 5 is the
  way to reduce it.
- The review of #42 verifies on the target system that the reads work under the filter, that
  `ptrace(PTRACE_SEIZE)` on another process fails with `EPERM`, and that the documentation of the unit
  matches the actual reach: a path listed in `InaccessiblePaths=` stays readable through
  `/proc/1/root/<path>`, so no deployment file may present that list as protection against a compromised
  agent.
- Removing a confinement setting, adding a capability or a group, or adding a collector that needs a new
  right changes `deploy/agent/` and is a `security`-tier change; removing a confinement setting while a
  capability is granted needs a superseding record.
- The installation instructions create the user, its groups and the systemd unit; the polkit rule is
  installed and removed by the install and uninstall scripts from v0.6.0 on.
