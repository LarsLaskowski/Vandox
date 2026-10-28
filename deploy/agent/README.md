# Installing the agent

This folder holds what installs the Vandox agent on the monitored server: the install scripts, the systemd unit and the example configuration. The
steps to install, upgrade and remove the agent come first; the reference after them explains the options, the checks and what the script does; the rights
the agent holds, each with the collector that needs it, follow under *Rights per collector*.

## Tailscale on the monitored server

The agent sends its data only over Tailscale: its unit lets it reach nothing but localhost and the tailnet ranges `100.64.0.0/10` and
`fd7a:115c:a1e0::/48`. Without Tailscale on this server, every send fails as `connection failed`; the agent keeps its data in its spool
and tries again. Do these steps once, before you install the agent. They follow Tailscale's documentation; where Tailscale's pages
differ from this summary, follow Tailscale.

1. **Install Tailscale** with Tailscale's guide for Linux, which covers Ubuntu 22.04: <https://tailscale.com/kb/1031/install-linux>.
   Use the package repository method that the guide describes. Check what a script does before you run it, as you would with any
   download.
2. **Log in to the tailnet of the backend host.** Run as root:

   ```
   tailscale up --advertise-tags=tag:vandox-agent
   ```

   It prints a URL; open it in a browser and sign in to the same tailnet as the backend host. The tag only works when the tailnet
   policy names it under `tagOwners` (step 3). Without a tag, run plain `tailscale up`. In that case, turn off key expiry for this
   machine in the admin console, or it loses the tailnet when its key expires
   (<https://tailscale.com/kb/1028/key-expiry>). Tailscale documents that tagged devices have key expiry turned off by default
   (<https://tailscale.com/kb/1068/acl-tags>). The agent needs neither Tailscale SSH (`--ssh`) nor any other option.
3. **Limit what the server may reach.** The tailnet policy must allow this server only the ingest port on the backend host
   ([SECURITY.md](../../SECURITY.md)). Tailscale's default policy lets every device reach every other, so replace that rule as
   Tailscale's policy guide describes (<https://tailscale.com/kb/1018/acls>). The rule for the agent, with the backend host tagged
   `tag:vandox-backend` and the ingest port 8081, is:

   ```
   "tagOwners": {
     "tag:vandox-agent":   ["autogroup:admin"],
     "tag:vandox-backend": ["autogroup:admin"],
   },
   "acls": [
     {"action": "accept", "src": ["tag:vandox-agent"], "dst": ["tag:vandox-backend:8081"]},
   ],
   ```

   Keep the rules your own devices need, for example for the web UI. The agent never listens on a port, so this server needs no
   rule that lets others reach it.
4. **Check the connection.** On this server:

   ```
   tailscale ip -4
   tailscale ping 100.64.0.1
   curl -s -o /dev/null -w '%{http_code}\n' http://100.64.0.1:8081/
   ```

   The first prints this server's tailnet address (a `100.x.y.z` address). The second must get a pong from the backend host. It
   checks the tailnet path, not the policy. The third must print `404`: the ingest port answers every request except the agent's
   own batches with `404`, so a `404` shows that the policy lets this server reach the port. `000` means no connection: check the
   policy, and that the backend binds the ingest port to its tailnet address (step 10 of [`docs/INSTALL.md`](../../docs/INSTALL.md)).
   Use the backend's MagicDNS name instead of `100.64.0.1` if you want to give the agent a name
   (<https://tailscale.com/kb/1081/magicdns>). The install script warns about a name server outside localhost and the tailnet,
   because the unit would block it.

## Install the agent

**What you need**

- The monitored server: Ubuntu 22.04 with systemd on `x86_64`, `root` access (`sudo`), and `curl`, `sha256sum`, `tar` and `gzip` (no `git`).
- Tailscale on this server, logged in to the same tailnet as the backend host (see *Tailscale on the monitored server* below).
- The backend, already running with its ingest port on its tailnet address (step 10 of [`docs/INSTALL.md`](../../docs/INSTALL.md)): you
  need that address and port, for example `http://100.64.0.1:8081`. A host name only works if it is a MagicDNS name; the agent can reach
  nothing but localhost and the tailnet.
- The agent token. You create it on the backend host (step 3 of [`docs/INSTALL.md`](../../docs/INSTALL.md): `openssl rand -hex 32 > secrets/vandox_agent_token`)
  and the backend accepts it only for the `agent_id` set under `ingest` (step 10). Have the same token value ready; the installer asks for it and never takes it as an option.
- The release version, for example `v0.2.0`: find it on <https://github.com/LarsLaskowski/Vandox/releases> (use the same version as the backend where you can).

**Step 1 - choose the version and the names.** Replace `vX.Y.Z` in the first block with the release version, `web-1` with the name of this server (the
`agent_id`, the one the backend expects) and `http://100.64.0.1:8081` with the backend's tailnet address and ingest port.

**Step 2 - run the lines as root, one at a time.** They download the install bundle and its checksum list over HTTPS, check the bundle, unpack it only if the
check passes, and start the installer. The installer asks for the token (typed without echo; or pipe one line to it), downloads the binary and checks it before it
runs it, then creates the user, the files, the unit and the MariaDB account, and starts the agent:

1. Become root (run this line on its own: it may ask for your password).
2. Set the version to install (replace `vX.Y.Z` by the release, for example `v0.2.0`).
3. Set the bundle's file name.
4. Set the release's download address.
5. Create a new private directory and change into it (start every attempt here, with a new directory).
6. Download the bundle and the checksum file of the release.
7. Check the bundle against its line in `SHA256SUMS`, and only if it matches, unpack it; if this prints anything but `...: OK`, stop and start again at step 5.
8. Run the install script from the unpacked bundle; it asks for the agent token. Replace `web-1` by the name of this server (its `agent_id`) and the address by the backend's tailnet address and ingest port.

```
sudo -i
v=vX.Y.Z
f=vandox-agent-install-$v.tar.gz
u=https://github.com/LarsLaskowski/Vandox/releases/download/$v
d=$(mktemp -d) && cd "$d"
curl --proto '=https' --proto-redir '=https' -fsSL -O "$u/$f" -O "$u/SHA256SUMS"
awk -v f="$f" '$2 == f' SHA256SUMS | sha256sum -c --strict - && tar -xzf "$f"
"$d/vandox-agent-install-$v/install.sh" --agent-id web-1 --backend-url http://100.64.0.1:8081
```

The variables live only in this shell; in a new shell, start again at step 1. The script first checks the other files of the bundle against `MANIFEST.sha256` and stops with `the bundle is incomplete or changed; extract it again in a new directory` if one is different or missing, for example after an interrupted unpacking; a missing `common.sh` or `MANIFEST.sha256` gets the same message.

Without internet access on the server, download the release files on a machine that has it:

```
v=vX.Y.Z
f=vandox-agent-install-$v.tar.gz
u=https://github.com/LarsLaskowski/Vandox/releases/download/$v
curl --proto '=https' --proto-redir '=https' -fsSL -O "$u/$f" -O "$u/SHA256SUMS"
curl --proto '=https' --proto-redir '=https' -fsSLO "$u/vandox-agent-linux-amd64"
```

Copy the three files to the server into `/root/vandox-files`. On the server run lines 1, 2, 3 and 5 of the block above (`sudo -i`, `v=`, `f=` and the `mktemp` line),
then (`web` stands for the `agent_id` of this server):

```
cp /root/vandox-files/* .
awk -v f="$f" '$2 == f' SHA256SUMS | sha256sum -c --strict - && tar -xzf "$f"
"$d/vandox-agent-install-$v/install.sh" --from . --agent-id web --backend-url http://100.64.0.1:8081
```

The binary is checked against the SHA-256 inside the checked bundle.

**Step 3 - check that it runs.** The installer ends with `vandox-agent vX.Y.Z installed and running` and prints four lines
`check journal-group`, `check journal-clock`, `check ipc-filter` and `check capabilities`, each `ok` or `WARNING <what to do>`. A warning does not fail the
installation; it names something to look at (see *Checks on the server*). Then:

```
systemctl status vandox-agent
journalctl -u vandox-agent -n 50
```

If the installer printed `vandox-agent did not start`, the journal shows why (a wrong `agent.yaml` value, an unreadable token, the spool directory); fix it and
run `systemctl restart vandox-agent`, the unit keeps retrying by itself. The backend shows the server once the first batch arrives.

**Step 4 - upgrade.** Run the lines of step 2 again with the new `v`, in a new shell and a new directory; the last line is just
`"$d/vandox-agent-install-$v/install.sh"` (the kept `agent.yaml` holds the ID and URL). Only the binary and the unit are replaced; the configuration, token, spool
and state stay, and the token is not asked for again.

**Step 5 - uninstall.** Run lines 1 to 7 of step 2 with the installed version (they unpack the same bundle into a new directory), then one of:

```
"$d/vandox-agent-install-$v/uninstall.sh"
"$d/vandox-agent-install-$v/uninstall.sh" --purge
```

The first removes the unit and the binary and keeps the user, `agent.yaml`, the token, the spool and the MariaDB account; `--purge` deletes them too, unsent
records included.

The options, what the script checks and in which order, the messages and the provenance check are in *Install reference* below.

## Install reference: options, checks and what the script does

The release publishes the install bundle `vandox-agent-install-vX.Y.Z.tar.gz` and a `SHA256SUMS` with two lines, the bundle's and the binary's. The
bundle has one directory and ten files (eleven archive entries): `install.sh`, `uninstall.sh`, `common.sh`, `vandox-agent.service`, `mariadb-user.sql`,
`mariadb-user-remove.sql`, `agent.yaml`, `VERSION` (the tag and one line feed), `vandox-agent-linux-amd64.sha256` (the binary's checksum line) and
`MANIFEST.sha256` (the checksums of the seven files the scripts read before they run anything).
It is byte-reproducible from the tagged source. The commands are in *Install the agent*: single lines, of which only the check line unpacks, and only after a passed check, so after a failed step the script's full path does not exist and nothing runs.

- **The check.** `awk` selects the bundle's line of `SHA256SUMS` by exact comparison of the name field, and `sha256sum --check --strict -` checks that
  file against it; with no such line it fails on empty input and `tar` never runs. The check proves the bundle is the file the release published; for the
  provenance too, see *Provenance* below. A new directory per attempt (`d=$(mktemp -d) && cd "$d"`) means a failed check leaves nothing to run: the script's full path does not exist.
- **Extraction as root.** `tar` run by root restores the owner and modes of the archive, which the release check pins to owner 0/0, 0755 for the scripts and 0644 for the rest,
  without special bits; the entry check verifies them. A tar made by someone else or a mode changed afterwards makes the entry check refuse.
- **The entry check.** Before anything is sourced or read, `install.sh` and `uninstall.sh` check the physical directory of the script, each of its
  ancestors, the script and `common.sh`: owned by uid 0, no link, and not writable by group or others (an ancestor with the sticky bit, like `/tmp`, may be;
  the directory, the script and `common.sh` may carry no set-user-ID, set-group-ID or sticky bit). A failure prints
  `<script>: <path> is not owned by root or is writable by others; extract the bundle as root in a private directory`, exit 1, nothing changed. The
  scripts also check `MANIFEST.sha256` and the other bundle files they read the same way before reading them. Running a script from a source checkout, owned by a user,
  therefore fails; use the bundle. Then the manifest: a regular file of at most 4096 bytes with exactly seven `sha256sum` lines for `VERSION`, `agent.yaml`, `common.sh`, `mariadb-user-remove.sql`, `mariadb-user.sql`, `vandox-agent-linux-amd64.sha256` and `vandox-agent.service`, in that order, and `sha256sum --check --strict` of them must pass; else `<script>: the bundle is incomplete or changed; extract it again in a new directory`, exit 1, before `common.sh` is sourced. The manifest proves the unpacking is complete and unchanged, not who made the bundle (that is the operator's check line). What is left is the time between a check and the read of the file.
- **Options.** `--agent-id` and `--backend-url` (required while `/etc/vandox/agent.yaml` does not exist, ignored when it does), `--from DIR` (take
  `vandox-agent-linux-amd64` from a directory instead of downloading it), `--no-mariadb` (skip the MariaDB account without a warning). There is no
  `--version`: the version is the first line of the bundle's `VERSION` (a file that is missing, a link, over 64 bytes or not one `vX.Y.Z` or `vX.Y.Z-rc.N` line
  gives `install.sh: no valid VERSION file next to install.sh; use the install bundle of a release`, exit 2). The token is read from the terminal without echo, or
  from one line of standard input; `--token` is refused. It is asked for only when `/etc/vandox/agent-token` is missing.
- **What it does, in order,** and every message and exit code: the Agent area, *Installation* (`docs/areas/agent.md`). In short: it runs the entry
  check, reads `VERSION`, checks root, systemd and `x86_64`, validates the configuration values and the spool directory, downloads only the binary
  over HTTPS (`curl --proto =https --proto-redir =https --fail`) into a private temporary directory, requires the bundle's `vandox-agent-linux-amd64.sha256` (a regular
  file, no link, one line, at most 4096 bytes) to match the binary (a link, a non-regular file or more than 64 MiB is refused), runs the verified binary once as
  `nobody` with `-version` and requires it to equal `VERSION`, and only then changes the server: user and groups, `/etc/vandox`, `agent.yaml`, the token (root:root 0600), the
  spool directory, the binary (written beside the old one and renamed over it), the unit, the MariaDB account, enable and restart.
- **Upgrade.** Run the command again with the new version. Only the binary and the unit are replaced; configuration, token, spool and state stay.
- **Offline.** The variant of *Install the agent* with `--from .` (the new directory the lines changed into).
- **Uninstall.** The commands are in *Install the agent*, step 5.
  The plain run stops and disables the agent (it saves its state), removes the unit and the binary, and keeps the user, `agent.yaml`, the token, the spool and the
  MariaDB account so a later install continues. `--purge` also removes them (it says that unsent records are lost before it deletes `/var/lib/vandox`) and drops
  the MariaDB account with `mariadb-user-remove.sql`; when that fails it prints the command to run with the bundle's path, for example
  `uninstall.sh: WARNING MariaDB account not removed; run: mariadb < /path/to/vandox-agent-install-vX.Y.Z/mariadb-user-remove.sql`. A home directory of an existing
  `vandox-agent` user is named and left in place. Both succeed on an absent or partial installation.
- **MariaDB.** When the account cannot be created, `install.sh` continues with `install.sh: WARNING MariaDB account not created; run: <plesk db | mariadb> < <path>`,
  where `<path>` is `mariadb-user.sql` in the extracted bundle.
- **Server checks.** After the start it prints `check journal-group`, `check journal-clock`, `check ipc-filter` and `check capabilities`, each `ok` or
  `WARNING <what to do>`; see *Checks on the server*.
- **Drop-ins.** A drop-in directory `/etc/systemd/system/vandox-agent.service.d/` or `/run/systemd/system/vandox-agent.service.d/` can change the unit
  after the script has installed it; the script names each one it finds and does not read it. Check it against *Unit*.
- **Provenance.** The two checks above prove integrity, not who built the files. At a release the bundle carries a build-provenance attestation, and
  because the bundle holds the binary's checksum, verifying the bundle also covers the binary:
  `gh attestation verify vandox-agent-install-vX.Y.Z.tar.gz --repo LarsLaskowski/Vandox` (and the same for `vandox-agent-linux-amd64`).
- **The name server.** A `backend.url` with a host name is resolved by the name server of `/etc/resolv.conf`; the unit blocks every name server
  outside localhost and the tailnet (`install.sh: WARNING name server <ip> is blocked by the unit; use the tailnet address or MagicDNS`). Use the
  tailnet address or the MagicDNS name (whose resolver is the tailnet's `100.100.100.100`).
- **The server run from the CI artifact.** For the Product Manager's run of a pull request's head: download the artifact `agent-install-files` of the `agent-install` run of
  the approved head SHA (the run ID and the SHA are given with the request) into `/root/vandox-files` and run `sha256sum --check --strict SHA256SUMS` there (it checks both lines);
  then follow the offline install of step 2 with `v=v0.0.0-rc.0`.

## Rights per collector

`vandox-agent` runs as the dedicated system user `vandox-agent`, **never as root**, without a login shell. The rights it holds are
listed here, each with the collector that needs it, as those collectors are implemented; the complete set is the one the Agent area names (*Rights*: `CAP_SYS_PTRACE` and the groups `adm` and `systemd-journal`), and nothing beyond it is granted. The rules behind them are in the Agent area
(`docs/areas/agent.md`, *Rights*) and the security area 6 of `.squad/project.md`. Adding a right, a capability or a group is a
`security`-tier change. The example configuration is `agent.yaml` in this folder; the unit is `vandox-agent.service`, installed by
`install.sh` and removed by `uninstall.sh` (both in this folder, with `common.sh`); see *Install the agent*, *Install reference* and *Unit*.

## Capabilities

| Capability | Collector | What it is for |
| ---------- | --------- | -------------- |
| `CAP_SYS_PTRACE` | `processes` | Reading `/proc/<pid>/smaps_rollup` of **another user's** process, which gives the proportional set size (PSS). |
| `CAP_SYS_PTRACE` | `connections` | Listing and reading `/proc/<pid>/fdinfo/<n>` of **another user's** process, which names the socket (`ino:`) a descriptor holds and its mount (`mnt_id:`), so a socket gets its owner. |

`CAP_DAC_READ_SEARCH` is **not granted**: with `CAP_SYS_PTRACE` it opens other processes' open files, memfds included, for writing
(record 0122). Only the `logs` collector would use it, for configured files the agent's user and groups cannot read, and it does without it; the connection collector deliberately does not use it (see below). The `logs` collector adds no
capability: it opens each file of `log_shipping.files` through a walk from `/` that refuses a symbolic link in any element and a file with
more than one link (against an attacker who races the check, the hard-link refusal relies on the distribution default
`fs.protected_hardlinks=1`, Ubuntu's default), so a service user who replaces its log file with a link to another file (for example
`/etc/shadow`) never gets that file shipped. It reads the files the agent's user and groups (`adm`, `systemd-journal`) can read and logs
`log file not readable` with the reason `permission denied` for the others.

`CAP_SYS_PTRACE` is granted as an ambient capability, with a bounding set that holds `CAP_SYS_PTRACE` only; adding any capability to it is a
`security` change that supersedes record 0122. For example, `CAP_DAC_READ_SEARCH` reopens the write through `/proc/<pid>/fd/<n>` into other
processes' open files, memfds included, and below closed directories; `CAP_DAC_OVERRIDE` and the other write-side DAC capabilities the write
open of `mem`; `CAP_SYS_NICE` `process_madvise` on another process; `CAP_SYS_ADMIN` or `CAP_CHECKPOINT_RESTORE`, together with directory
search, the links in `/proc/<pid>/map_files/`. The unit is
confined as soon as it grants any (`NoNewPrivileges=yes`, a system-call filter that denies `ptrace`, `process_vm_readv`,
`process_vm_writev`, `pidfd_getfd`, `open_by_handle_at` and `process_madvise`, no write-side capability). `process_madvise` is named
explicitly because `@system-service` includes it; `pidfd_open` stays allowed.

Three barriers keep the agent from writing into another user's process or file beyond what the file's own mode grants it, and none of
them alone covers every vector examined. (a) DAC plus the bounding set: opening another user's `/proc/<pid>/mem` for writing needs
`CAP_DAC_OVERRIDE` (the file is mode `0600`, owned by the target's user), which `CAP_SYS_PTRACE` does not grant, and looking up an entry in
`/proc/<pid>/fd` (mode `0500`, owned by the target's user) needs `CAP_DAC_READ_SEARCH`, so the bounding set must keep both out, and every
write-side DAC capability too. (b) The system-call filter, for `ptrace`, `process_vm_writev`, `pidfd_getfd`, `open_by_handle_at` and
`process_madvise`; it does not deny `openat` or `pwrite`, so it is not what stops a write open. (c) The kernel, behind the filter, for
`pidfd_open` plus `process_madvise`: the call needs `CAP_SYS_NICE` (since kernel 5.12), which the bounding set must also keep out, and the
kernel refuses destructive advice on another process; the accepted advice set is version-dependent, so this is defence in depth only.
A compromised agent cannot use its capability to write into another user's process or file, or to run code as another user, except through a file
whose own mode grants the agent's user, its groups (`adm`, `systemd-journal`) or others write (accepted residual, record 0122).
Measurement and reasoning: record 0122 and the Agent area.

**Accepted residual.** With `CAP_SYS_PTRACE` the agent follows another process's `/proc/<pid>/cwd` and `/proc/<pid>/root` links and their
`/proc/<pid>/task/<tid>/cwd` and `…/root` variants, and the `/proc/<pid>/exe` and `/proc/<pid>/task/<tid>/exe` links, so a file whose own mode grants the agent's user, its groups or others read or write is
reachable even below a closed directory that contains another process's working or root directory, and the `exe` link reaches that process's executable file, readable or writable as its own mode allows (a file is reachable only along paths from
some process's `cwd` or `root`, or that process's executable through `exe`; the path components after the link are still checked against their own modes), and the `root` link reaches the
file tree of another mount namespace (a container's). A write into such a file reaches another user's process memory where that process maps the
file `MAP_SHARED`, and runs code as another user where that user executes the file or reads it as a script or configuration. Readable besides are
the ptrace-checked `/proc` files whose mode allows reading (`maps`, `smaps_rollup`, `fdinfo`, …), not another user's `mem` or `environ` and not
`/etc/shadow`; credentials read that way may still lead to root through other services (password reuse, Plesk or MariaDB administration).

### What the log collector reads and what it needs

- **The journal.** The system files (`system.journal`, `system@*.journal`, `system@*.journal~`) **and** the user files (`user-<UID>.journal`,
  `user-<UID>@*.journal`, `user-<UID>@*.journal~`) below `/var/log/journal/<machine-id>`, else `/run/log/journal/<machine-id>`, are read with the
  group `systemd-journal`; journald's default `SplitMode=uid` puts the entries of users with a UID of 1000 or more (on Plesk the web-space
  users) into the user files. The agent reads the machine ID from `/etc/machine-id` and decodes zstd-compressed items itself (no `journalctl`).
- **Configured files.** Each file of `log_shipping.files` is read with the group `adm`, or because its mode lets any user read it; a file that is
  readable by neither, or below a directory the agent has no search right on, is logged `log file not readable` with the reason
  `permission denied` and not shipped, because `CAP_DAC_READ_SEARCH` is not granted (record 0122). The path is walked from `/` without following a link; the directory of a rotated file is reached the same way when the
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
- `/etc/passwd` is world-readable and needs no right. The collector reads it once per run, with the process collector's bounds (1 MiB, lines of
  4 KiB), to name the UID the kernel prints for each socket. Without it the sample still succeeds and every `user` is the decimal UID; the agent
  logs `user names unavailable` once and `user names available` when the file reads again.
- `/proc/<pid>/fdinfo/<n>` of **another user's** process needs `CAP_SYS_PTRACE`: the kernel checks ptrace access when the directory is listed
  and the file opened. The links `/proc/<pid>/fd/<n>` (`socket:[N]`) would need `CAP_DAC_READ_SEARCH` to list and both capabilities to read,
  and `CAP_DAC_READ_SEARCH` lets the agent read every file on the server (`/etc/shadow`, Plesk's credentials) and, with `CAP_SYS_PTRACE`, open
  other processes' open files, memfds included, for writing (record 0122), so the collector does not read them (record 0115) and the agent does not hold it
  (record 0122). Measured on the development host (kernel 6.18.44, no Yama restriction, no `hidepid`), as user 65534 against a process of root:

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

- `RestrictAddressFamilies=` must keep **`AF_UNIX`** (the probe above, the notification socket of systemd and the system bus of the systemd collector) and **`AF_INET`** and **`AF_INET6`** (the sender's connection to the backend; the unit's `IPAddressDeny=any` with `IPAddressAllow=localhost 100.64.0.0/10 fd7a:115c:a1e0::/48` lets the agent reach only localhost and the tailnet, so `backend.url` names a tailnet address or a MagicDNS name; a host name that is resolved by a name server outside localhost and the tailnet fails as `connection failed`, and `install.sh` warns about such a name server).
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
- **The spool** lives in `spool.directory` too and holds the records the backend has not yet acknowledged. The agent writes segment
  files `segment-<number>.spool` (mode 0600) and the bookkeeping file `spool-meta.json` (mode 0600, written through `spool-meta.json.tmp`:
  the agent ID, the boot ID, the next sequence number, whether the spool was closed cleanly and the acknowledged ranges), and deletes a segment file once the backend has acknowledged all its records,
  when it is damaged, or when the oldest data has to give way to new records (`spool.max_size`, `spool.min_free`). Other entries in the directory,
  `agent-state.json` among them, are never read, changed or deleted by the spool. No spool file holds the agent token.
- **The state file** `agent-state.json` lives in `spool.directory` and holds the boot, the position in the journal, the open backup run and the
  positions of the `logs` collector (a position per journal sequence number ID, at most 256, and per configured file, at most 16, with the
  SHA-256 of its path instead of the path; up to 64 KiB in all), so a restart neither repeats nor loses a kill or a log line, and a backup
  that ended while the agent was down is closed. The agent saves it within 10 seconds after a position changed (the next run of its
  10-second state job) and once more when it stops; after a crash it ships the lines of the last seconds again. The agent writes this file and the spool's files
  (mode 0600) and **never creates the directory**: the installation creates `spool.directory` and makes it writable for `vandox-agent`
  (for example `install -d -o vandox-agent -g vandox-agent -m 0700 /var/lib/vandox/spool`). When the file cannot be read or written the agent
  logs `agent state not available` or `agent state not saved` once and runs on without it.
- **The `backups` collector** reads `/proc` like the process collector and needs no further right; its rules are `backups.processes` in
  `agent.yaml`.

## Unit

`vandox-agent.service` is installed as `/etc/systemd/system/vandox-agent.service` (root, 0644). Every setting and why:

| Setting | Why |
| ------- | --- |
| `Description=`, `Documentation=` | Names the unit and links this file. |
| `After=systemd-journal-flush.service` | The persistent journal is in place when the `kernel` collector first reads it. |
| `StartLimitIntervalSec=0`, `Restart=on-failure`, `RestartSec=30s` | A failed or watchdog-stopped agent is restarted at most twice a minute for ever, never giving up (`start-limit-hit`), so it comes back by itself once a cause such as a missing spool directory is fixed. |
| `Type=notify`, `NotifyAccess=main`, `WatchdogSec=2min` | The agent sends `READY=1` and `WATCHDOG=1`; it pings every minute while its runners are on time (the longest runner timeout is 10 s), so a short stall under pressure does not restart it, a stuck runner does. |
| `ExecStart=/usr/local/bin/vandox-agent`, `User=`, `Group=vandox-agent` | The dedicated user, never root. |
| `SupplementaryGroups=adm systemd-journal` | Log files and the journal (*Groups*). |
| `AmbientCapabilities=CAP_SYS_PTRACE`, `CapabilityBoundingSet=CAP_SYS_PTRACE`, `NoNewPrivileges=yes` | The one capability the process and connection collectors need, and nothing else (records 0030, 0122). |
| `LoadCredential=agent-token:/etc/vandox/agent-token`, `Environment=VANDOX_AGENT_TOKEN_FILE=/run/credentials/vandox-agent.service/agent-token` | The token reaches the agent as a credential file; the path is written out because the `%d` specifier is not known to systemd 249. It is never an argument or an environment value (record 0032). |
| `Environment=GOMEMLIMIT=192MiB` | The Go runtime does not know the cgroup limit; with the default garbage collector a heap can reach about twice its live size and the kernel would kill the agent for garbage. A soft limit below `MemoryMax=` makes the runtime collect harder only near it. A unit setting, not an agent option. |
| `ProtectSystem=strict`, `ReadWritePaths=/var/lib/vandox`, `ProtectHome=yes`, `PrivateTmp=yes`, `PrivateDevices=yes`, `ProtectKernelTunables=yes`, `ProtectKernelModules=yes`, `ProtectKernelLogs=yes`, `ProtectControlGroups=yes`, `ProtectHostname=yes`, `InaccessiblePaths=/etc/shadow /etc/gshadow` | The agent writes only below `/var/lib/vandox` (spool and state) and reads little else. `ProtectKernelLogs=yes` hides `/dev/kmsg` and `syslog(2)`; the kernel collector reads the journal instead. |
| `RestrictNamespaces=yes`, `RestrictRealtime=yes`, `RestrictSUIDSGID=yes`, `LockPersonality=yes`, `MemoryDenyWriteExecute=yes`, `RemoveIPC=yes`, `UMask=0077`, `KeyringMode=private` | Further confinement; none is used by the collectors. |
| `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6` | The socket probe, the bus and the MariaDB socket need `AF_UNIX`; the sender needs the two internet families. |
| `SystemCallArchitectures=native`, `SystemCallFilter=@system-service adjtimex`, `SystemCallFilter=~ptrace process_vm_readv process_vm_writev pidfd_getfd open_by_handle_at process_madvise`, `SystemCallErrorNumber=EPERM` | `adjtimex` is read by the `clock` collector; the six denied calls are barrier (b) of record 0122. `process_madvise` is named because `@system-service` includes it. |
| `IPAddressDeny=any`, `IPAddressAllow=localhost 100.64.0.0/10 fd7a:115c:a1e0::/48` | The agent connects only over the tailnet (records 0006, 0010); the unit enforces it. `localhost` allows loopback TCP and UDP (a backend on the same host, which is also the target of the CI check); Unix sockets (MariaDB, the system bus, the journal) are not subject to `IPAddressAllow=` at all. |
| `MemoryMax=256M` | The documented worst cases (a process run 48 MiB, MariaDB 32 MiB, logs 24 MiB, kernel 4 MiB, the sender one 1 MiB batch per stream) sum to about 112 MiB if all peak at once under memory pressure; with `GOMEMLIMIT=192MiB` this leaves headroom for the runtime and page cache. The memory pressure mode reads the system's `MemAvailable`, not the cgroup's. The agent's real peak RSS is **to be measured** (the parallel run of #45); until then the values rest on the documented bounds. |
| `CPUQuota=50%` | Bounds a runaway loop to half a core; collector timeouts are at most 10 s, so an idle server never reaches it. |
| `Nice=5`, `IOWeight=50` | The agent yields to the monitored services without starving while an outage builds. |
| `[Install] WantedBy=multi-user.target` | Starts at boot. |

Not set, on purpose (the *Unit settings that must stay as they are* section): `ProtectClock`, `PrivateNetwork`, `NetworkNamespacePath`, `ProcSubset`,
a `ProtectProc=` other than `default`, `PrivateUsers`, `TemporaryFileSystem`, `DynamicUser`, any specifier, `VANDOX_AGENT_TOKEN=`, and any
`InaccessiblePaths=` over `/run/dbus`, `/run/mysqld`, `/var/log/journal` or `/run/log/journal`.

**`systemd-analyze security vandox-agent`.** Required: the overall exposure level `OK` or better (record 0131). A draft with these settings scored 2.2
`OK` offline on systemd 255 in a development session; the shipped unit was measured on 249 as below.

| Where | systemd | Overall exposure level |
| ----- | ------- | ---------------------- |
| CI job `agent-install`, Ubuntu 22.04 runner (PR #324, head `a0d92a6`, run 40480331990) | 249 (`249.11-0ubuntu3.22`) | `Overall exposure level for vandox-agent.service: 2.2 OK` |
| the monitored server | 249 | to be measured (Product Manager's server run) |

Items the CI run still flags (the exposure each adds), with the reason the unit keeps them open:

| Item | Exposure | Reason |
| ---- | -------- | ------ |
| `PrivateNetwork=` | 0.5 | The sender needs the network, and the connection collector reads the host's `/proc/net` (see *Unit settings that must stay as they are*). |
| `CapabilityBoundingSet=~CAP_SYS_PTRACE` | 0.3 | `CAP_SYS_PTRACE` is the one capability the process and connection collectors need (record 0122); the bounding set holds nothing else. |
| `RestrictAddressFamilies=~AF_(INET\|INET6)` | 0.3 | The sender's connection to the backend needs Internet sockets; `IPAddressAllow=` limits where they may go. |
| `PrivateUsers=` | 0.2 | Not set on purpose: the collectors read other users' processes, which a private user namespace would hide or change (see the forbidden settings above). |
| `ProtectClock=` | 0.2 | The `clock` collector calls `adjtimex` (record 0111), which `ProtectClock=` would deny. |
| `ProtectProc=` | 0.2 | Must stay `default`: a stricter value hides other users' processes whenever the agent runs without `CAP_SYS_PTRACE`. |
| `SystemCallFilter=~@clock` | 0.2 | The `clock` collector calls `adjtimex`, which `@clock` holds. |
| `SystemCallFilter=~@privileged` | 0.2 | `@privileged` holds `@clock`, so `adjtimex` (record 0131, option 9). |
| `SystemCallFilter=~@resources` | 0.2 | `@resources` holds calls the Go runtime may make at start (record 0131, option 9). |
| `IPAddressDeny=` | 0.1 | Flagged because the allow-list has entries other than localhost: the tailnet ranges are the point of the setting. |
| `AmbientCapabilities=` | 0.1 | The unprivileged process holds `CAP_SYS_PTRACE` through it. |
| `RootDirectory=` / `RootImage=` | 0.1 | Not applicable to this unit: the agent reads the host's `/proc`, journal and sockets. |
| `SupplementaryGroups=` | 0.1 | The groups `adm` and `systemd-journal` give the log and journal reads. |
| `RestrictAddressFamilies=~AF_UNIX` | 0.1 | `AF_UNIX` is needed by the socket probe, the notification socket, the system bus and the MariaDB socket. |
| `ProcSubset=` | 0.1 | `ProcSubset=pid` hides `/proc/net`, which the connection collector reads. |

**What the CI run measured on that runner** (not the server: the runner has no real journal or rsyslog setup, so its check results say nothing about
the server): `ptrace(PTRACE_ATTACH)` under the unit's confinement printed `EPERM`, the running agent's `CapEff` and `CapBnd` were `0000000000080000`;
the address allow-list blocked the runner's non-tailnet address and reached loopback; the four install-time checks printed `ok`; and install,
repeat install, restart after a removed spool directory, uninstall, reinstall and purge all passed.

## Checks on the server

The install script prints these four checks (a warning never fails the installation); the rest are run by hand. The Product Manager's run on
the server, with its results, is entered below.

| Check | Command or line | Result to expect | Result of the server run |
| ----- | --------------- | ---------------- | ------------------------ |
| Journal files readable by the group | `check journal-group: ok` | `ok`; the `WARNING` means a file below `/var/log/journal` is not group-readable by `systemd-journal` (or there is none: a volatile journal only under `/run/log/journal`; then the `kernel` and `logs` collectors read that one, check it by hand with `ls -l /run/log/journal/*/`) | to be measured |
| Clock of the journal | `check journal-clock: ok` | `/proc/uptime` and the journal's monotonic clock differ by less than 1 s; `WARNING ... differ by <n> ms` means every live kill reads as late and gets no extra snapshots; `WARNING could not read the journal timestamp` means `logger` or `journalctl` failed | to be measured |
| `process_madvise` in `@ipc` | `check ipc-filter: ok` | systemd lists the name, so the system-call filter entry takes effect | to be measured |
| Capabilities of the running agent | `check capabilities: ok` | `CapEff` is `0000000000080000` (`CAP_SYS_PTRACE` alone) | to be measured |
| Security score | `systemd-analyze security vandox-agent` | `OK` or better (see *Unit*) | to be measured |
| `ptrace` attach is refused | run `sleep 300 &` as root, then `sudo systemd-run --wait --pipe -p User=vandox-agent -p AmbientCapabilities=CAP_SYS_PTRACE -p CapabilityBoundingSet=CAP_SYS_PTRACE -p NoNewPrivileges=yes -p 'SystemCallFilter=@system-service adjtimex' -p 'SystemCallFilter=~ptrace process_vm_readv process_vm_writev pidfd_getfd open_by_handle_at process_madvise' -p SystemCallErrorNumber=EPERM -p SystemCallArchitectures=native python3 -c 'import ctypes,sys; l=ctypes.CDLL(None,use_errno=True); r=l.ptrace(16,int(sys.argv[1]),0,0); print("EPERM" if r==-1 and ctypes.get_errno()==1 else "NOT REFUSED")' <PID of the sleep>` | prints `EPERM` | to be measured |
| Reboot | `sudo reboot`, then `systemctl is-active vandox-agent` | `active`, started by itself after the journal flush | to be measured |
| rsyslog's logrotate | manual: the configuration renames the log before its single `HUP` | the `logs` collector reads the renamed file until the writer has moved on (Agent area, *Log shipping*) | to be measured |
| Peak memory | the agent's own RSS self-metric during the #45 parallel run | below `GOMEMLIMIT=192MiB` | to be measured |

The same cases, on a throw-away runner with systemd 249, run in the CI job `agent-install` (`.github/scripts/check-agent-install.sh`); its results
are in *Unit*. They are the runner's, not the server's: the table above stays "to be measured" until the server run.
