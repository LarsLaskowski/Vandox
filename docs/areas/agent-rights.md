# Agent rights

## Scope

The rights `vandox-agent` runs with on the monitored server (security area 6): its user and groups, its one capability, the unit's
confinement, what each collector and the sender need from the unit, why a compromised agent cannot write into another user's process
or file, and the residual that remains. The operator installing the agent and the Security reviewer rely on it; the unit's full
settings and how they are installed are in [Agent installation](agent-installation.md).

## User, groups and capability

- The agent runs as the dedicated system user `vandox-agent` without a login shell, **never as root**; the installed unit applies the
  rights and confinement below. It authenticates to MariaDB as that OS user through `unix_socket` with only the `PROCESS` privilege.
- For reading it is a member of the groups `adm` (log files) and `systemd-journal` (journal). To read other users' processes it holds
  `CAP_SYS_PTRACE` alone, as an ambient capability with a bounding set limited to it (record 0122).
- `CAP_DAC_READ_SEARCH` is **not** granted: together with `CAP_SYS_PTRACE` it opens other processes' open files through
  `/proc/<pid>/fd/<n>` for writing, memfds included (*No write into another user's process or file*). The one collector that would use
  it, log shipping, does without it: a configured file its user and groups cannot read is logged and not shipped.
- For restarting units (self-healing, a later feature, from v0.6.0) a polkit rule lets this user restart only the units named in the
  configuration; there is no sudo and no shell.
- Each group, capability and confinement setting is listed and justified in `deploy/agent/` (`deploy/agent/README.md`, *Rights per
  collector*) when the collector that needs it is introduced; anything not listed is not granted. Removing a confinement setting, adding
  a capability or a group, or adding a collector that needs a new right is a `security`-tier change; removing a confinement while a
  capability is granted needs a superseding record.

## Confinement

As soon as the unit grants any capability, the unit is confined:

- `NoNewPrivileges=yes`, which keeps the agent from gaining a capability outside the set;
- a system-call filter that denies `ptrace`, `process_vm_readv`, `process_vm_writev`, `pidfd_getfd`, `open_by_handle_at` and
  `process_madvise` with `EPERM` (`SystemCallErrorNumber=EPERM`; `pidfd_open` and `pidfd_send_signal` stay allowed), under
  `SystemCallArchitectures=native`. `process_madvise` is denied by name: systemd puts it in the group `@ipc`, which `@system-service`
  includes, so an allow-list built on `@system-service` admits it. The agent never calls it (it uses `madvise` on its own memory only).
  That the server's systemd (249, Ubuntu 22.04) knows the name, so the entry takes effect instead of being skipped, is checked on the
  server by the install script (`check ipc-filter`: `systemd-analyze syscall-filter @ipc` lists `process_madvise`); if it did not,
  barrier (c) below would still stop the write;
- no write-side capability;
- `ProtectHome=yes` and `InaccessiblePaths=` for at least `/etc/shadow` and `/etc/gshadow`. These protect only against accidental
  reads; they are no limit on a compromised agent, which reaches the same files through another process's root in `/proc` where their
  own mode allows it.

**The bounding set holds `CAP_SYS_PTRACE` only; adding any capability to it is a `security` change that supersedes record 0122**, and
so is removing one of the six denied calls. For example, `CAP_DAC_READ_SEARCH` reopens the write through `/proc/<pid>/fd/<n>` into other
processes' open files, memfds included, and below closed directories; `CAP_DAC_OVERRIDE` and the other write-side DAC capabilities the
write open of `mem`; `CAP_SYS_NICE` `process_madvise` on another process; `CAP_SYS_ADMIN` or `CAP_CHECKPOINT_RESTORE`, together with
directory search, the links in `/proc/<pid>/map_files/`. These are examples, not a closed list. `proc_mem.force_override=never` (kernel
6.12+) is not required and not set: it limits `FOLL_FORCE` only once a write-capable descriptor exists, which the agent cannot obtain,
and the production kernel (5.15) predates it.

## What each part needs

- **Process collector.** `stat`, `status` and `cmdline` of every process are readable by any user (unless `/proc` is mounted with
  `hidepid`); `smaps_rollup` of **another user's** process needs `CAP_SYS_PTRACE`, and `CAP_DAC_READ_SEARCH` does not replace it.
  Measured on the development host as UID 65534 against a root process in the same namespace, without Yama or `hidepid`:
  `smaps_rollup` answers `EACCES` without capabilities and with `CAP_DAC_READ_SEARCH` alone, and is readable with `CAP_SYS_PTRACE`. A
  Yama or other LSM policy, and another user namespace, were not measured and can refuse the read even with the capability. Without it
  the collector records the RSS from `status` and leaves the PSS out ([Agent system metrics](agent-system-metrics.md), *Processes*).
- **Connection collector.** It reads another user's `/proc/<pid>/fdinfo/<n>` (`mnt_id:`, `ino:`, the socket's table inode) with
  `CAP_SYS_PTRACE` alone, and the world-readable `/etc/passwd` once per run for the names of the sockets' UIDs, which needs no right.
  The `fd` links stay unread, because reading them needs `CAP_DAC_READ_SEARCH`, which lets the whole agent read every file
  (`/etc/shadow`, Plesk's credentials) and, with `CAP_SYS_PTRACE`, also enables the memfd write. Measured by Security on the development
  host (kernel 6.18.44, no Yama, no `hidepid`), as UID 65534 against a process of root:

  | Capabilities | list `fd/` | readlink `fd/3` | list `fdinfo/` | read `fdinfo/3` | read `/etc/shadow` |
  | ------------ | ---------- | --------------- | -------------- | --------------- | ------------------ |
  | none | `EACCES` | `EACCES` | `EACCES` | `EACCES` | `EACCES` |
  | `CAP_DAC_READ_SEARCH` | ok | `EACCES` | `EACCES` | `EACCES` | ok |
  | `CAP_SYS_PTRACE` | `EACCES` | `EACCES` | ok | ok (`mnt_id`, `ino` = the socket inode) | `EACCES` |
  | both | ok | `socket:[N]` | ok | ok | ok |

  `hidepid=2` hides no process from a reader that holds `CAP_SYS_PTRACE` (measured in a scratch mount namespace); without the
  capability other users' processes are invisible and their sockets carry no owner ([Agent connections](agent-connections.md)). A Yama
  or other LSM policy and another user namespace were not measured. The start-up probe's Unix datagram socket needs no right, but the
  unit must allow it: `RestrictAddressFamilies=` must keep `AF_UNIX` (the systemd notification socket needs it as well), and a
  system-call filter must not deny `socket` for it; when the probe is refused the collector logs `socket owners unknown` and records no
  owners. The unit gives the agent **no network namespace of its own** (no `PrivateNetwork=`, no `NetworkNamespacePath=`, or
  `/proc/net` would show another namespace's sockets), does not set `ProcSubset=pid` (it hides `/proc/net`) and leaves `ProtectProc=`
  at `default` (a stricter value hides other users' processes whenever the agent runs without `CAP_SYS_PTRACE`).
- **Sender.** It needs no group and no capability: it dials the backend's ingest port as the agent's user
  ([Agent sending](agent-sending.md)). The unit must allow it: `RestrictAddressFamilies=` keeps **`AF_INET` and `AF_INET6`** beside
  `AF_UNIX`, and a system-call filter must not deny `socket` and `connect` for them, or every request fails as `connection failed`.
  Binding the socket to `tailscale0` needs no capability on Linux 5.7 and later. The unit restricts the addresses the agent may reach to
  localhost and the tailnet ranges ([Agent installation](agent-installation.md)); with a host name in `backend.url` the agent also
  queries the name server of `/etc/resolv.conf` (port 53), which an `IPAddressAllow=` must include.
- **Systemd collector.** Reading unit states over the system bus needs no group and no capability: systemd's bus policy lets every
  user call `Properties.Get`, `ListUnitsByPatterns`, `ListUnitsByNames` and `LoadUnit` (the server capture was taken with every call
  made as `nobody`). The unit must keep the bus `/run/dbus/system_bus_socket` reachable: `RestrictAddressFamilies=` keeps `AF_UNIX` and
  nothing hides `/run/dbus` (no `InaccessiblePaths=` or `TemporaryFileSystem=` over it), or the collector fails every run as a gap.
- **MariaDB collector.** It needs no group, no capability and no password: it connects to the MariaDB socket as the agent's OS user and
  logs in as the MariaDB user `vandox-agent`, which MariaDB's `unix_socket` plugin accepts for that OS user (the account and its
  statement: [Agent MariaDB collector](agent-mariadb.md), [Network and security model](network-and-security-model.md), *MariaDB
  access*). The unit must keep `AF_UNIX` in `RestrictAddressFamilies=` and the socket's directory `/run/mysqld` reachable (no
  `InaccessiblePaths=` or `TemporaryFileSystem=` over it), or every run fails (`mariadb: connect: permission denied`) or reads as
  `down`.
- **System metrics.** No right of their own: `/proc/stat`, `/proc/loadavg`, `/proc/meminfo`, `/proc/vmstat`, `/proc/pressure`, the
  agent's own mount table and `/sys/block` are readable by any user, `statfs` needs only search permission on the directories above the
  mount point, and `adjtimex` without a change (mode 0, read-only) needs no capability (measured: unprivileged, `adjtimex` with modes 0
  and `statfs` succeed, while an `adjtimex` that sets the maximum error fails with `EPERM`). The unit's system-call filter must allow
  `adjtimex`: `ProtectClock=yes` denies it with `EPERM` even for a read, and the group `@system-service` does not contain it, so an
  allow-list names it. A change of the clock still needs `CAP_SYS_TIME`, which the bounding set excludes.
- **Kernel event collector.** It reads the journal files below `/var/log/journal` and `/run/log/journal` as a member of
  `systemd-journal`, its first user, and `/etc/machine-id`, `/proc/sys/kernel/random/boot_id`, `/proc/uptime` and `/proc/stat`, which
  any user can read; it needs no capability (not `CAP_SYSLOG`: it does not read `/dev/kmsg`). The unit must keep both journal
  directories readable (no `InaccessiblePaths=` or `TemporaryFileSystem=` over them) and start after `systemd-journal-flush.service`.
  That the server's journal files are readable by the group is checked by the install script (`check journal-group`).
- **Log shipping collector.** It reads the system and user journal files with `systemd-journal`, as the kernel collector does,
  `/etc/machine-id` and `/etc/localtime`, which any user can read, and the files `log_shipping.files` names with `adm`, or because
  their mode lets any user read them. It uses no capability. It opens the configured paths through the link-free walk of
  [Agent log shipping from files](agent-log-shipping-files.md) (*Opening a file*), so a service user who replaces its log file or a
  directory with a link to another file (`/etc/shadow`) never gets that file shipped. A file the agent's user and groups cannot read,
  or that lies below a directory they cannot search, is logged `log file not readable` with the reason `permission denied` and not
  shipped. The error logs of Plesk and the web servers (`/var/log/nginx/error.log`, `/var/log/apache2/error.log`,
  `/var/log/plesk/panel.log`, `/var/log/sw-cp-server/error_log`) are such files unless the operator has granted the agent read by hand
  with the commands of `deploy/agent/README.md` (*Error logs of Plesk and the web servers*; record 0165): no new right of the agent in
  the unit or the installer, but the grant widens what a compromised agent can read, and a default ACL on a log directory widens what
  every file later created there grants, which the README states; its statements about the server's own file rights, rotation and
  `plesk repair fs` are marked unverified. The unit must keep the journal directories and the configured files reachable (no
  `InaccessiblePaths=` or `TemporaryFileSystem=` over them).
- **Backup check.** It reads `/proc` as the process collector does and needs no right of its own.
- **State file and spool.** The agent writes only in `spool.directory`: `agent-state.json` ([Agent](agent.md), *Agent state*) and the
  spool's own files ([Agent spool](agent-spool.md)), all with mode 0600. The installation creates that directory, writable by the
  agent's user and not a symbolic link, and the agent never creates it; without it, or when the agent cannot write it, the agent does
  not start. The spool needs no right beyond the directory.

## No write into another user's process or file beyond its own mode

A compromised agent cannot use its capability to write into another user's process or file, or to run code as another user, except
through a file whose own mode grants the agent's user, its groups (`adm`, `systemd-journal`) or others write (the accepted residual,
*Accepted residual*; record 0122). No single mechanism stops every vector examined; three distinct barriers do:

| Write vector | Barrier |
| ------------ | ------- |
| Opening another user's `/proc/<pid>/mem` for writing (`O_RDWR`, `O_WRONLY`), then `pwrite` | (a) DAC and the bounding set: the file is mode `0600`, owned by the target's user, so the open needs `CAP_DAC_OVERRIDE` |
| Opening another owner-only writable `/proc/<pid>/` file of another user for writing (for example `clear_refs`) | (a), the same DAC check as for `mem` |
| Opening `/proc/<pid>/fd/<n>` of another user's process (an open file or memfd it holds), then `pwrite` | (a) DAC and the bounding set: the directory is mode `0500`, owned by the target's user, so the lookup needs `CAP_DAC_READ_SEARCH` |
| Following a link in `/proc/<pid>/map_files/<range>` of another user's process | (a) the bounding set: the link needs `CAP_SYS_ADMIN` or (since kernel 5.9) `CAP_CHECKPOINT_RESTORE`, besides search of the `0500` directory |
| `ptrace` (POKE), `process_vm_writev`, `pidfd_getfd` (taking a writable descriptor), `open_by_handle_at` | (b) the system-call filter |
| `pidfd_open` on the target, then `process_madvise` | (b) the filter denies `process_madvise`; behind it (c) the kernel: the call needs `CAP_SYS_NICE` (since kernel 5.12), and the kernel refuses destructive advice on another process |
| A file whose own mode grants the agent write, reached through another process's `/proc/<pid>/cwd` or `/proc/<pid>/root` link, or that process's executable through its `/proc/<pid>/exe` link (each also below `/proc/<pid>/task/<tid>/`) | none: the accepted residual |

The filter denies neither `openat` nor `pwrite`, so it is not what stops the `mem` and `fd` writes; barrier (a) is. Barrier (c) is
defence in depth only: the `CAP_SYS_NICE` requirement is a capability rule, but which advice the kernel accepts for another process is
memory-management policy that has grown between versions.

**Measurements.** On the development host (kernel 6.18.44, same user namespace, no Yama or other LSM restriction), as UID 65534 against
a `sleep` owned by root; each capability set given with `capsh --user=nobody --inh=<caps> --addamb=<caps> -- -c <probe>` and checked as
`CapEff` in the probe's `/proc/self/status` (`0x80000` for `CAP_SYS_PTRACE`, the agent's set; `0x80004` with `CAP_DAC_READ_SEARCH`
added). The `mem` probe opens and closes the target's `/proc/<pid>/mem` and never writes:

| Capabilities | `O_RDONLY` | `O_RDWR` | `O_WRONLY` |
| ------------ | ---------- | -------- | ---------- |
| `CAP_SYS_PTRACE` (the agent's set) | `EACCES` | `EACCES` | `EACCES` |
| `CAP_SYS_PTRACE` + `CAP_DAC_READ_SEARCH` (not granted) | ok | `EACCES` | `EACCES` |
| `CAP_SYS_PTRACE` + `CAP_DAC_OVERRIDE` (control) | ok | ok | ok |

The `fd` probe (measured by the Product Manager on four kernels): a root `target` maps a memfd `MAP_SHARED` and prints its first bytes;
the probe runs as `nobody` under `capsh` with ambient capabilities, opens the target's `/proc/<pid>/fd/<n>` of that memfd `O_RDWR` and
writes four bytes with `pwrite`:

| Host | Kernel | none | `CAP_SYS_PTRACE` (`0x80000`, the agent's set) | `CAP_SYS_PTRACE` + `CAP_DAC_READ_SEARCH` (`0x80004`, not granted) |
| ---- | ------ | ---- | --------------------------------------------- | ----------------------------------------------------------------- |
| NAS | 4.4.302+ | open `EACCES` | open `EACCES` | open ok, `pwrite` of 4 bytes ok, the target shows them |
| Ubuntu 22.04.5 | 5.15.0-198-generic | open `EACCES` | open `EACCES` | open ok, `pwrite` of 4 bytes ok, the target shows them |
| Ubuntu 24.04.5 | 6.8.0-147-generic | open `EACCES` | open `EACCES` | open ok, `pwrite` of 4 bytes ok, the target shows them |
| Ubuntu 26.04.1 | 7.0.0-38-generic | open `EACCES` | open `EACCES` | open ok, `pwrite` of 4 bytes ok, the target shows them |

The open needs both capabilities: `CAP_DAC_READ_SEARCH` for the lookup in the `0500` directory and `CAP_SYS_PTRACE` for following the
link; the opened file is then checked against its own mode only, which for a memfd is `0777`. On the development host (6.18.44)
`/proc/<pid>/fd/<n>` and `/proc/<pid>/task/<tid>/fd/<n>` answer `EACCES` with `CAP_SYS_PTRACE` and open `O_RDWR` with both (open only).
`CAP_DAC_READ_SEARCH` alone also opens for writing a `0666` file inside a `0700` directory of root (open and close only); with
`CAP_SYS_PTRACE` alone or without a capability that open fails `EACCES`.

The `process_madvise` probe calls `pidfd_open` on the target, which succeeds without a capability, and then `process_madvise` on the
returned descriptor with each advice on the target's first read-only mapping:

| Capabilities | `MADV_COLD`, `MADV_PAGEOUT`, `MADV_WILLNEED` | `MADV_DONTNEED`, `MADV_FREE` |
| ------------ | -------------------------------------------- | ---------------------------- |
| none | `EACCES` | `EACCES` |
| `CAP_SYS_PTRACE` (the agent's set) | `EPERM` | `EINVAL` |
| `CAP_SYS_PTRACE` + `CAP_DAC_READ_SEARCH` (not granted) | `EPERM` | `EINVAL` |
| `CAP_SYS_NICE` | `EACCES` | `EACCES` |
| `CAP_SYS_PTRACE` + `CAP_SYS_NICE` (control) | accepted | `EINVAL` |

The `CAP_SYS_NICE` requirement applies to every process other than the caller, not only another user's: as UID 65534 without
capabilities against its own `sleep` the call gives the same `EPERM` and `EINVAL`. Under the accepted advice the target's page content
stayed unchanged (Security's probe, which also tried `MADV_COLLAPSE`: `EINVAL`). On the production kernel (5.15) the `CAP_SYS_NICE`
requirement holds by the kernel's history (since 5.12); the DAC result of the `mem` table rests on file-permission semantics that do
not depend on the version; the advice set there was not measured.

## Accepted residual

With `CAP_SYS_PTRACE` the agent follows another process's `/proc/<pid>/cwd`, `/proc/<pid>/root` and `/proc/<pid>/exe` links and their
`/proc/<pid>/task/<tid>/cwd`, `…/root` and `…/exe` variants, which skip the search check of the directories above the file they lead
to (record 0122):

- A file whose own mode grants the agent's user, its groups or others read or write is reachable even below a directory closed to the
  agent, but only along paths from some process's working or root directory, or as that process's executable through `exe`; the path
  components after a `cwd` or `root` link are still checked against their own modes, so a closed directory below that point still
  blocks, and the file `exe` leads to is checked against its own mode only. The `root` link of a process in another mount namespace (a
  container's) resolves in that namespace, so the same holds for its tree. Whether opening a running executable for writing fails with
  `ETXTBSY` depends on the kernel version and was not measured; it is not counted as a barrier.
- A write into such a file reaches another user's process memory where that process maps the file `MAP_SHARED`, and runs code as
  another user where that user executes the file or reads it as a script or configuration.
- Readable besides are the ptrace-checked `/proc` files whose mode allows reading (`maps`, `smaps_rollup`, `fdinfo`, …), not another
  user's `mem` or `environ` and not `/etc/shadow` (measured on kernel 6.18.44 with `CAP_SYS_PTRACE` alone: `EACCES`, directly and
  through `/proc/<pid>/root`; `maps` and `smaps_rollup` of a root process open, and a `0666` file in a `0755` directory below a `0700`
  directory of root opens for writing through `/proc/<pid>/cwd/` of a root process whose working directory is the `0755` directory).
  Credentials read that way may still lead to root through other services (password reuse, Plesk or MariaDB administration).
- A separate small helper that holds the capability is the way to reduce it.

## Related decisions

- [0030](../decisions/0030-agent-runs-unprivileged-with-named-capabilities.md) — why a dedicated user with named, confined
  capabilities.
- [0122](../decisions/0122-cap-sys-ptrace-alone-cannot-write-another-users-process-memory-through-proc-pid-mem-or-fd.md) — why
  `CAP_SYS_PTRACE` alone and not `CAP_DAC_READ_SEARCH`, which barrier stops each write vector, which residual remains, why the bounding
  set holds `CAP_SYS_PTRACE` only, and why the filter also denies `process_madvise`.
- [0113](../decisions/0113-process-collector-pss-from-smaps-rollup-top-processes-full-snapshot-under-memory-pressure.md) — why the
  process collector reads `smaps_rollup` with the capability.
- [0144](../decisions/0144-socket-owners-from-fdinfo-on-the-socket-mount-the-agent-learns-at-its-start.md) — why the connection collector
  reads `fdinfo` with `CAP_SYS_PTRACE` alone.
- [0165](../decisions/0165-error-logs-the-agent-cannot-read-get-a-documented-grant-by-hand-not-a-new-right.md) — why the error log read
  grant is made by hand.
- [0013](../decisions/0013-mariadb-access-via-unix-socket-process-privilege.md) — why a `unix_socket` MariaDB user with only `PROCESS`.

## Not here

- The unit's full settings, the install and uninstall scripts and the user they create: [Agent installation](agent-installation.md).
- The tailnet, its ACL, the address allow-list and the MariaDB account: [Network and security model](network-and-security-model.md).
- What each collector reads and records: [Agent](agent.md), [Agent system metrics](agent-system-metrics.md),
  [Agent connections](agent-connections.md), [Agent services and backup runs](agent-services.md),
  [Agent MariaDB collector](agent-mariadb.md), [Agent kernel events and boots](agent-kernel-events.md),
  [Agent log shipping](agent-log-shipping.md).
- The spool's file access rules: [Agent spool](agent-spool.md), *File access (security area 9)*.

## Implementation

`deploy/agent/vandox-agent.service` (the unit; `TestService_*` pin every setting), `deploy/agent/README.md` (*Rights per collector*,
*Error logs of Plesk and the web servers*), `deploy/agent/mariadb-user.sql`. The users of the capability are `internal/procstats` and
`internal/connstats`; `internal/sockmount` is the start-up probe; `internal/logship` (`OpenNoLinks`) the link-free open.
