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
| `CAP_DAC_READ_SEARCH` | `logs` | Reading a file named in `log_shipping.files` that the group `adm` cannot read, and walking to it through a directory the agent has no search right on. Needed only for such a file; see *What the log collector reads and what it needs*. |

The `logs` collector is the first user of `CAP_DAC_READ_SEARCH`, and only for the files `log_shipping.files` names: it opens each one through a
walk from `/` that refuses a symbolic link in any element and a file with more than one link (against an attacker who races the check, the hard-link refusal relies on the distribution default `fs.protected_hardlinks=1`, Ubuntu's default), so a service user who replaces its log file with
a link to another file (for example `/etc/shadow`) never gets that file shipped. The connection collector deliberately does not use the
capability (see below). Without the capability the agent reads the files `adm` can read and logs `log file not readable` with the reason
`permission denied` for the others.

`CAP_SYS_PTRACE` is granted as an ambient capability, with a bounding set limited to the capabilities the Agent area names, and the unit is
confined as soon as it grants any (`NoNewPrivileges=yes`, a system-call filter that denies `ptrace`, `process_vm_readv`,
`process_vm_writev`, `pidfd_getfd` and `open_by_handle_at`, no write-side capability).

### What the log collector reads and what it needs

- **The journal.** The system files (`system.journal`, `system@*.journal`, `system@*.journal~`) **and** the user files (`user-<UID>.journal`,
  `user-<UID>@*.journal`, `user-<UID>@*.journal~`) below `/var/log/journal/<machine-id>`, else `/run/log/journal/<machine-id>`, are read with the
  group `systemd-journal`; journald's default `SplitMode=uid` puts the entries of users with a UID of 1000 or more (on Plesk the web-space
  users) into the user files. The agent reads the machine ID from `/etc/machine-id` and decodes zstd-compressed items itself (no `journalctl`).
- **Configured files.** Each file of `log_shipping.files` is read with the group `adm`, or with `CAP_DAC_READ_SEARCH` where the file is
  readable by neither. The path is walked from `/` without following a link; the directory of a rotated file is reached the same way when the
  agent looks for it after a rotation that happened while it was stopped (`<name>.1` and `<name>-YYYYMMDD`).
- **The time zone.** `/etc/localtime` (readable by anyone) gives the zone of the year-less times of `syslog` files. When it cannot be read the
  agent logs `time zone not readable` once, reads no `syslog` file until it can, keeps their positions and records no gap; the journal is
  not affected.
- **What it logs.** Fixed texts only, one per change: `journal not readable` (with a reason such as `journal: no journal directory`),
  `log file not readable` (with the index of the file in `log_shipping.files` and a reason: `missing`, `not a regular file`, `link refused`,
  `permission denied`, `read failed`), `time zone not readable` and their `... readable again`. A path, a field value or a message never
  reaches the agent's own log.
- **Duplicates.** A file that holds what the journal ships (rsyslog's `/var/log/syslog` on a server that forwards the journal to rsyslog) is shipped
  twice, once per source. The agent does not remove them.

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

- `RestrictAddressFamilies=` must keep **`AF_UNIX`** (the probe above, the notification socket of systemd and the system bus of the systemd collector).
- No own network namespace: no `PrivateNetwork=` and no `NetworkNamespacePath=`, or `/proc/net` shows another namespace's sockets.
- `/run/dbus/system_bus_socket` stays reachable: no `InaccessiblePaths=` or `TemporaryFileSystem=` over `/run/dbus`.
- No `ProcSubset=pid`: it hides `/proc/net`.
- `ProtectProc=` stays at `default`: a stricter value hides other users' processes whenever the agent runs without `CAP_SYS_PTRACE`.
- `/run/mysqld` stays reachable (the MariaDB collector connects to `/run/mysqld/mysqld.sock`): no `InaccessiblePaths=` or `TemporaryFileSystem=` over `/run/mysqld`, and `AF_UNIX` in `RestrictAddressFamilies=`.

### What the systemd collector reads and what it needs

- It asks systemd for the state of the configured units over the system bus socket `/run/dbus/system_bus_socket`. Reading unit states is
  allowed to any user, so the collector needs **no group and no capability**.
- It needs the socket to be reachable: the unit must not hide `/run/dbus` (no `InaccessiblePaths=` or `TemporaryFileSystem=` over it), and
  `RestrictAddressFamilies=` must keep `AF_UNIX`, also for this bus connection.
- With `services.units: []` the collector is off and the bus is never opened.

### What the MariaDB collector reads and what it needs

- It connects to the local MariaDB server over its Unix socket (`mariadb.socket`, default `/run/mysqld/mysqld.sock`) and sends only fixed,
  read-only statements: the server status and variables, the process list when many threads run or the server is under memory pressure, and
  two questions about its own account. It needs **no group, no capability and no password**; with `mariadb.enabled: false` it does not run and
  never opens the socket.
- **The account.** The agent logs in as `vandox-agent`@`localhost`, authenticated by the operating system user of the connecting process
  (`unix_socket`), so no database password exists. `deploy/agent/mariadb-user.sql` creates it: `CREATE OR REPLACE USER 'vandox-agent'@'localhost'
  IDENTIFIED VIA unix_socket WITH MAX_USER_CONNECTIONS 2` and `GRANT PROCESS ON *.* TO 'vandox-agent'@'localhost'`, nothing else.
- **Run it once as the database administrator.** The file may be run again, for example after a Plesk update dropped the user; it resets the
  account to exactly this state. As root on a plain MariaDB server: `mariadb < mariadb-user.sql`. On a Plesk server: `plesk db < mariadb-user.sql`.
- **What `PROCESS` lets the agent see.** The process list of all connections: user, host, database, command, state, time and the **statement
  text** of every running or sleeping connection, and the server status counters. It does not let the agent read a table or its data, change
  anything or see a password hash.
- **The statement texts can hold customer data and credentials** (`IDENTIFIED BY '...'`, the values of an `INSERT`). While the process list is
  recorded (more than 10 running threads, or memory pressure), the agent cuts each statement to 16 KiB, keeps the first 64, and sends them to
  the backend, which stores and shows them in the web UI. Treat the backend and its web UI like the database for that reason.
- **Without the user** the collector records the server as `not_answering` with the error number the server sent (for example 1698) and logs
  how to fix it; a missing socket is recorded as `down`.

## Groups

The group `systemd-journal` is used by the `kernel` collector and the `logs` collector: they read the binary journal files below
`/var/log/journal` and `/run/log/journal` themselves (pure Go, no `journalctl`). The `kernel` collector reads the system files for the kernel
messages that report an out-of-memory kill, without decompression; the `logs` collector reads the system and the user files and decodes
zstd-compressed items. The group `adm` (log files) is for the files named in `log_shipping.files`; the process and `backups` collectors need
neither group, and neither do the connection, systemd and MariaDB collectors.

## Kernel events, log shipping, backups and the state file

- **The unit keeps the journal readable**: no `InaccessiblePaths=` or `TemporaryFileSystem=` over `/var/log/journal` or `/run/log/journal`, and
  it starts after `systemd-journal-flush.service`, so the persistent journal is in place when the `kernel` collector first reads it. A server
  without a persistent journal is read from `/run/log/journal`; if neither can be read, the collector logs
  `kernel messages not readable` once, records no gap and resumes at the saved position once the journal is readable again.
- **The state file** `agent-state.json` lives in `spool.directory` and holds the boot, the position in the journal, the open backup run and the
  positions of the `logs` collector (a position per journal sequence number ID, at most 256, and per configured file, at most 16, with the
  SHA-256 of its path instead of the path; up to 64 KiB in all), so a restart neither repeats nor loses a kill or a log line, and a backup
  that ended while the agent was down is closed. The agent saves it within 10 seconds after a position changed (the next run of its
  10-second state job) and once more when it stops; after a crash it ships the lines of the last seconds again. The agent writes this one file
  (mode 0600) and **never creates the directory**: the installation creates `spool.directory` and makes it writable for `vandox-agent`
  (for example `install -d -o vandox-agent -g vandox-agent -m 0700 /var/lib/vandox/spool`). When the file cannot be read or written the agent
  logs `agent state not available` or `agent state not saved` once and runs on without it.
- **The `backups` collector** reads `/proc` like the process collector and needs no further right; its rules are `backups.processes` in
  `agent.yaml`.
