# Installing the agent: rights per collector

`vandox-agent` runs as the dedicated system user `vandox-agent`, **never as root**, without a login shell. The rights it holds are
listed here, each with the collector that needs it, as those collectors are implemented; the complete set is the one the Agent area names (*Rights*: `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH`, the groups `adm` and `systemd-journal`), and nothing beyond it is granted. The rules behind them are in the Agent area
(`docs/areas/agent.md`, *Rights*) and the security area 6 of `.squad/project.md`. Adding a right, a capability or a group is a
`security`-tier change. The example configuration is `agent.yaml` in this folder; the unit and the install scripts follow with the
collectors that need them.

## Capabilities

| Capability | Collector | What it is for |
| ---------- | --------- | -------------- |
| `CAP_SYS_PTRACE` | `processes` | Reading `/proc/<pid>/smaps_rollup` of **another user's** process, which gives the proportional set size (PSS). |
| `CAP_SYS_PTRACE` | `connections` | Listing and reading `/proc/<pid>/fdinfo/<n>` of **another user's** process, which names the socket (`ino:`) a descriptor holds and its mount (`mnt_id:`), so a socket gets its owner. |

`CAP_DAC_READ_SEARCH` is **not needed by any collector so far**. The connection collector deliberately does not use it (see below).

`CAP_SYS_PTRACE` is granted as an ambient capability, with a bounding set limited to the capabilities the Agent area names, and the unit is
confined as soon as it grants any (`NoNewPrivileges=yes`, a system-call filter that denies `ptrace`, `process_vm_readv`,
`process_vm_writev`, `pidfd_getfd` and `open_by_handle_at`, no write-side capability).

### What the process collector reads and what it needs

- `/proc/<pid>/stat`, `status` and `cmdline` of every process are readable by any user, so the collector needs **no right** for them,
  unless `/proc` is mounted with `hidepid` (then it sees only its own processes, and the snapshot is incomplete for that reason).
- `/proc/<pid>/smaps_rollup` of **another user's** process needs `CAP_SYS_PTRACE`: the kernel checks ptrace access when the file is
  opened. `CAP_DAC_READ_SEARCH` does **not** replace it. Measured on the development host (kernel 6.18, no Yama restriction, no
  `hidepid`), as user 65534 against a process of root in the same namespace: `EACCES` without any capability and with
  `CAP_DAC_READ_SEARCH` alone, readable with `CAP_SYS_PTRACE`.
- Without the capability the collector still runs: it records the resident set size (RSS) from `status` and leaves the PSS of those
  processes (and the PSS sum of their programs) out, never 0. The agent logs the number of processes without PSS once.
- The read can be refused even with the capability: a process in another user namespace, a Yama or other security-module policy.
  Those cases were not measured; the effect is the same as without the capability, for those processes only.
- A kernel thread or a zombie has no address space; the kernel answers `ESRCH` for its `smaps_rollup`, and the collector records 0.
- `/proc/stat`, `/etc/passwd` and the other files the system collectors read are readable by any user and need nothing.

### What the connection collector reads and what it needs

- `/proc/net/tcp`, `tcp6`, `udp` and `udp6` are readable by any user and need no right. `tcp6` and `udp6` are missing on a kernel without IPv6;
  the collector then leaves those protocols out.
- `/proc/<pid>/fdinfo/<n>` of **another user's** process needs `CAP_SYS_PTRACE`: the kernel checks ptrace access when the directory is listed
  and the file opened. The links `/proc/<pid>/fd/<n>` (`socket:[N]`) would need `CAP_DAC_READ_SEARCH` to list and both capabilities to read,
  and `CAP_DAC_READ_SEARCH` lets the agent read every file on the server (`/etc/shadow`, Plesk's credentials), so the collector does not read
  them (record 0115). Measured on the development host (kernel 6.18.44, no Yama restriction, no `hidepid`), as user 65534 against a process of root:

  | Capabilities | list `fd/` | readlink `fd/3` | list `fdinfo/` | read `fdinfo/3` | read `/etc/shadow` |
  | ------------ | ---------- | --------------- | -------------- | --------------- | ------------------ |
  | none | `EACCES` | `EACCES` | `EACCES` | `EACCES` | `EACCES` |
  | `CAP_DAC_READ_SEARCH` | ok | `EACCES` | `EACCES` | `EACCES` | ok |
  | `CAP_SYS_PTRACE` | `EACCES` | `EACCES` | ok | ok (`mnt_id: 10`, `ino:` the socket's inode) | `EACCES` |
  | both | ok | `socket:[N]` | ok | ok | ok |

- With `CAP_SYS_PTRACE`, `hidepid=2` hides no process (measured in a scratch mount namespace with `proc` mounted with `hidepid=2`); without
  it other users' processes are invisible, and their sockets then carry no owner **without a warning**.
- The `ino:` line of `fdinfo` exists from Linux 5.14 on (the server's Ubuntu 22.04 has 5.15 or a newer kernel). On an older kernel the start-up probe of the socket mount fails, no socket
  gets an owner and the agent logs `socket owners unknown`.
- Without the capability the collector still runs: the sockets of other users' processes carry no owner, the sample succeeds and the agent logs
  `socket owners partly unknown` once with the number of processes it could not read.
- **The socket probe needs no right but a unit setting.** At its start the agent opens one Unix datagram socket (never bound, connected or
  sent on) and reads its own `/proc/self/fdinfo/<fd>` to learn the mount that holds the kernel's sockets; only descriptors on that mount count as
  sockets. If the unit refuses the socket, no socket gets an owner and the agent logs `socket owners unknown`.

### Unit settings that must stay as they are

- `RestrictAddressFamilies=` must keep **`AF_UNIX`** (the probe above and the notification socket of systemd).
- No own network namespace: no `PrivateNetwork=` and no `NetworkNamespacePath=`, or `/proc/net` shows another namespace's sockets.
- No `ProcSubset=pid`: it hides `/proc/net`.
- `ProtectProc=` stays at `default`: a stricter value hides other users' processes whenever the agent runs without `CAP_SYS_PTRACE`.

## Groups

The groups `adm` (log files) and `systemd-journal` (journal) are for the log collectors that follow; the process collector needs
neither, and neither does the connection collector.
