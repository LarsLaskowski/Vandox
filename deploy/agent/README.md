# Installing the agent: rights per collector

`vandox-agent` runs as the dedicated system user `vandox-agent`, **never as root**, without a login shell. The rights it holds are
listed here, each with the collector that needs it; anything not listed is not granted. The rules behind them are in the Agent area
(`docs/areas/agent.md`, *Rights*) and the security area 6 of `.squad/project.md`. Adding a right, a capability or a group is a
`security`-tier change. The example configuration is `agent.yaml` in this folder; the unit and the install scripts follow with the
collectors that need them.

## Capabilities

| Capability | Collector | What it is for |
| ---------- | --------- | -------------- |
| `CAP_SYS_PTRACE` | `processes` | Reading `/proc/<pid>/smaps_rollup` of **another user's** process, which gives the proportional set size (PSS). |

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

## Groups

The groups `adm` (log files) and `systemd-journal` (journal) are for the log collectors that follow; the process collector needs
neither.
