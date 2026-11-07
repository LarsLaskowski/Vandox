# Agent installation

## Scope

How `vandox-agent` is installed, upgraded and removed on the monitored server: the release's install bundle, the root-run scripts
`install.sh` and `uninstall.sh` with their arguments, checks, messages and exit codes, what they create, the checks on the server and the
systemd unit in short (security areas 2, 6 and 9). The operator relies on it; every setting of the unit with its reason is in
`deploy/agent/README.md`, the reasons for the choices in records 0131 and 0182 to 0191.

## The bundle and the tailnet

- `install.sh` installs or upgrades the agent, `uninstall.sh` removes it. Both come from the **install bundle** of a release,
  `vandox-agent-install-<version>.tar.gz`, and run as root from its extracted directory on Ubuntu 22.04 with systemd 249 on `x86_64`
  (`bash`, `curl`, `sha256sum`, `tar`, `gzip` and the shadow utilities; no `git`).
- The bundle has one directory and ten files (eleven archive entries, in a fixed order): `install.sh`, `uninstall.sh`, `common.sh`,
  `vandox-agent.service`, `mariadb-user.sql`, `mariadb-user-remove.sql`, `agent.yaml` (the example), `VERSION` (the tag and one line
  feed), `vandox-agent-linux-amd64.sha256` (the checksum line of the binary) and `MANIFEST.sha256` (seven `sha256sum` lines for the
  members the scripts read). It is built reproducibly from the tagged source, is listed with the binary in the release's two-line
  `SHA256SUMS` (the bundle first), and carries a build-provenance attestation. Only tags `vX.Y.Z` and `vX.Y.Z-rc.N` are packaged.
- The operator downloads the bundle and `SHA256SUMS` over HTTPS into a new private directory (`mktemp -d`) and unpacks the bundle only
  after the check, with single commands, the check line being the only one that joins the check and `tar` with `&&`
  (`deploy/agent/README.md`, *Install the agent*): `awk` selects the bundle's line of `SHA256SUMS` by exact comparison of the name field
  and `sha256sum --check --strict -` checks that file against it. The script runs by its full path in that directory, and no document
  pipes a download into a shell.
- The unit is `vandox-agent.service`.
- The monitored server must be a node of the backend host's tailnet (Tailscale installed and logged in; the steps are in
  `deploy/agent/README.md`, *Tailscale on the monitored server*). The unit's allow-list admits only localhost, `100.64.0.0/10` and
  `fd7a:115c:a1e0::/48`, by address, not by interface; the interface is the sender's binding to `tailscale0`; what the allow-list
  lets through and what fails is in [Agent sending](agent-sending.md) (*The request*, the second layer). While Tailscale is not up, a send fails at once as `connection failed` (no
  `tailscale0`) or ends as a timeout (the interface without routes), never through the provider's network, and the agent keeps spooling
  and retrying. The guarantee holds with the tailnet address in `backend.url` (a host name is refused, record 0192;
  [Network and security model](network-and-security-model.md); record 0134). The unit has no ordering on `tailscaled`,
  because the sender retries until the tailnet is up.

## Command

- `install.sh [--agent-id ID --backend-url URL] [--from DIR] [--no-mariadb]`; the version is the one in `VERSION`, there is no
  `--version`. `--agent-id` and `--backend-url` are required while no `/etc/vandox/agent.yaml` exists and are ignored when it does.
  `--from DIR` takes the binary from a directory instead of downloading it; `--no-mariadb` skips the MariaDB account silently.
- The token is never an option: `--token` and `--token=...` are refused with `install.sh: the token is read from standard input, never
  from an option` and the usage line, exit 2.
- A wrong argument (`--version` included) prints `usage: install.sh [--agent-id ID --backend-url URL] [--from DIR] [--no-mariadb]`,
  exit 2.
- `--agent-id` must be an agent ID ([Agent](agent.md), *Command line and start-up*): else `install.sh: --agent-id is not a valid agent
  ID`, exit 2.
- `--backend-url` must be `http` or `https`, `://`, a host, an optional port of 1 to 65535 without a leading zero and an optional `/`;
  the host is an IPv4 literal (four parts of 0 to 255 without a leading zero, `0` alone stays valid; a host of digits and dots that is no IPv4
  literal is refused) or a bracketed IPv6 literal without a zone; a host name is refused: else
  `install.sh: --backend-url is not http(s)://ip-address[:port]`, exit 2. The values are never echoed.

**The `VERSION` file.** `VERSION` next to `install.sh` must be a regular file, not a link, of at most 64 bytes (checked by size before
it is read), with exactly one line that ends in a line feed and is `vX.Y.Z` or `vX.Y.Z-rc.N`; else `install.sh: no valid VERSION file
next to install.sh; use the install bundle of a release`, exit 2. A source checkout has no `VERSION` and is refused.

## The entry check (E15)

- Before anything is sourced or read, `install.sh` and `uninstall.sh` check, with `stat` and the physical path of the script (`cd -P`),
  every ancestor directory up to `/`, then the bundle directory, the running script and `common.sh`. Each must be owned by uid 0 and no
  link; an ancestor must not be group- or other-writable unless its sticky bit is set (`/tmp`; set-user-ID and set-group-ID bits of
  ancestors are ignored); the bundle directory, the script and `common.sh` must have no group- or other-write bit and no set-user-ID,
  set-group-ID or sticky bit (the mode is compared as an octal number, `mode & 07022 == 0`).
- A failure prints `<script>: <path> is not owned by root or is writable by others; extract the bundle as root in a private directory`,
  exit 1, with nothing sourced or changed; a bundle member that does not exist at all (the script, `common.sh` or the manifest) gets the
  message of E16 instead.
- The script then checks, with the same per-entry rule, the other files it reads: `VERSION`, `vandox-agent.service`,
  `mariadb-user.sql`, `agent.yaml` and the pin file (`uninstall.sh`: `mariadb-user-remove.sql`); a missing file is left to the later
  checks.
- Everything after the check, `common.sh` and the bundle files included, is read through the physical directory the check looked at,
  never through the path as typed (which may run through a link). What remains is the time between a check and the read.

## The manifest (E16)

- Still before `common.sh` is sourced, the entry check reads `MANIFEST.sha256` with a bounded reader: not a link, a regular file of at
  most 4096 bytes (its size taken with `wc -c`, which for a regular file seeks to the end and reads at most the last block, before any
  read), exactly seven lines that each end in a line feed, a further read finding nothing (not even a partial line), line *i* matching
  `<64 lower-case hex digits>  <name i>` with the names compared as fixed strings in the order `VERSION`, `agent.yaml`, `common.sh`,
  `mariadb-user-remove.sql`, `mariadb-user.sql`, `vandox-agent-linux-amd64.sha256`, `vandox-agent.service` (no other name, no path,
  none twice). Then `sha256sum --check --strict --quiet MANIFEST.sha256` must pass in the physical bundle directory. The manifest is
  also covered by the owner and mode rule.
- Any failure prints `<script>: the bundle is incomplete or changed; extract it again in a new directory`, exit 1, with nothing sourced
  or changed.
- The scripts are not listed (a script cannot hash itself); a truncated script never runs, because bash parses its closing `if` whole.
  The manifest proves the unpacking is complete and unchanged, not authenticity, which stays with the operator's check of the bundle.
  `stat`, `wc` and `sha256sum` come from the caller's `PATH`, because the script has not set its own yet.

## Order, and what stops it

Nothing changes before the files are verified.

1. The entry check (E15, exit 1, before the arguments are read); arguments (exit 2); the other bundle files (E15, exit 1); `VERSION`
   (exit 2); then root, systemd as PID 1 and `x86_64`, else `install.sh: must run as root`, `install.sh: needs systemd` or
   `install.sh: only linux-amd64 is supported`, exit 1.
2. Configuration and the early checks: an existing `/etc/vandox/agent.yaml` is kept (`install.sh: keeping existing
   /etc/vandox/agent.yaml`) and its `spool.directory` is read (*`spool.directory` of a kept configuration*); without one the spool is
   `/var/lib/vandox/spool`, and an IP literal outside localhost and the tailnet ranges gets `install.sh: WARNING <host> is not a tailnet
   address; the unit blocks it`.
   Then, read-only and before the token prompt and any change, the spool directory is
   checked (`/var/lib/vandox` and the spool, when they exist, must be real directories, the first owned by root and not group- or
   other-writable, the second owned by `vandox-agent`, which an existing spool cannot be while the user does not exist; missing entries
   pass) and an existing `/etc/vandox` that is a link or not a directory is refused, so every check of the steps above precedes the
   first change.
3. The token is asked for only when `/etc/vandox/agent-token` is missing: from the terminal without echo, else one line of standard
   input. It must be 32 to 4096 bytes of `0x21` to `0x7E`, else `install.sh: token rejected: fewer than 32 characters`, `...: more than
   4096 characters` or `...: a character outside 0x21-0x7E`, exit 1; the token is never printed. Only bash built-ins handle it: it is no
   argument, environment variable or here-document of any child process.
4. The binary: `vandox-agent-linux-amd64` of the release `VERSION`, downloaded from this repository's release page with
   `curl --proto =https --proto-redir =https --fail --max-time 300 --max-filesize` (nothing else is downloaded), or taken from `--from`,
   into a private temporary directory removed on every exit; a failed download or an unusable `--from` source (a link, a directory, a
   FIFO, missing, over 64 MiB) gives `install.sh: download failed: vandox-agent-linux-amd64`, exit 1. The bundle's
   `vandox-agent-linux-amd64.sha256` (the pin) must be a regular file, not a link, of at most 4096 bytes, checked by size before it is
   read, and hold exactly one line `<64 lower-case hex digits>  vandox-agent-linux-amd64` that matches the binary's SHA-256, else
   `install.sh: checksum mismatch, nothing installed`, exit 1. Only then is the binary run, once, as `nobody` without privileges
   (`setpriv`) with `-version`; a version other than `VERSION` gives `install.sh: binary is not <version>`, exit 1. The pin lies inside
   the verified bundle, so checking the bundle (or its attestation) covers the binary; the checksum proves integrity, not who built the
   binary.
5. Apply: the user and group `vandox-agent`, `/etc/vandox`, `agent.yaml`, the token, the spool directory, the binary, the unit, the
   MariaDB account (first install only), enable and restart. A link in place of `/etc/vandox`, `agent.yaml`, the token file, the binary
   or the unit is refused (`install.sh: <path> is a link; not following it`, exit 1), never followed (checked first in this step, before
   the user is touched); every file the script writes (token, configuration, binary, unit) goes through a temporary file and `mv -T`.
   `systemctl enable` must succeed: a failure prints `install.sh: vandox-agent could not be enabled` after systemctl's own message,
   exit 1, and the installed files stay. A start that does not reach `READY=1` ends with `install.sh: vandox-agent did not start; see:
   journalctl -u vandox-agent`, exit 1; the installed files stay and the unit keeps retrying.
6. The server checks (*Checks on the server*), then `vandox-agent vX.Y.Z installed and running`.

## What it creates

| Path | Owner and mode | Notes |
| ---- | -------------- | ----- |
| user and group `vandox-agent` | system user, shell `/usr/sbin/nologin`, password locked, no home, supplementary groups exactly `adm` and `systemd-journal` | An existing user is brought to this state and each change is printed (`install.sh: user vandox-agent: shell set to /usr/sbin/nologin`, `password locked`, `primary group set to vandox-agent`, `groups set to adm,systemd-journal`, `created`; `install.sh: group vandox-agent created`). A home directory it has is named (`home directory <path> is kept in place`) and never removed. |
| `/usr/local/bin/vandox-agent` | root, 0755 | Written to a temporary file in the same directory and renamed over the old one; never written into a running file. |
| `/etc/vandox` | root, 0755 | |
| `/etc/vandox/agent.yaml` | root:vandox-agent, 0640 | The example with the two values; never overwritten. |
| `/etc/vandox/agent-token` | root:root, 0600 | Written through a temporary file and renamed. |
| `/var/lib/vandox` | root, 0755 | Must be a real directory owned by root and not group- or other-writable. |
| `spool.directory` | vandox-agent, 0700 | Exactly one directory below `/var/lib/vandox` (default `/var/lib/vandox/spool`), created by the script with `mkdir -m 0700` and `chown -h` of that one entry, never recursed into (no `chown -R`). |
| `/etc/systemd/system/vandox-agent.service` | root, 0644 | A drop-in directory `/etc/systemd/system/vandox-agent.service.d/` or `/run/systemd/system/vandox-agent.service.d/` is named: `install.sh: WARNING drop-in directory <path> changes the unit; check it`. |

**`spool.directory` of a kept configuration** is read by fixed line rules and fails closed: the file must hold only printable ASCII and
line feeds (no tab, carriage return, byte order mark or byte at or above `0x80`), no line starting with `%`, `---` or `...`, every
other line at column 0 a comment or `<key>:` of lower-case letters and `_`, and the `spool:` block only comments and `  key: value`
lines whose value is plain, single-quoted without `''` or double-quoted without a backslash; anything else (a flow mapping, an anchor,
a tag, a block scalar, a value on the next line, a second `directory` key, another indent) gives `install.sh: cannot read
spool.directory from <file>`, exit 2, though the agent itself would read the file. A value other than exactly `/var/lib/vandox/<name>`
(`<name>` of letters, digits, `.`, `_`, `-`, not `.` or `..`) gives `install.sh: spool.directory must be one directory below
/var/lib/vandox`, exit 2, because the unit allows writes only below `/var/lib/vandox`; so no element on the way is the agent's. An
existing `/var/lib/vandox` or spool that is a link or not a directory, owned by another user (the spool: not by `vandox-agent`), or a
`/var/lib/vandox` that is group- or other-writable gives `install.sh: <path> is not the agent's directory`, exit 1. `uninstall.sh`
prints the two `spool.directory` lines above with `uninstall.sh:` in front; it does not check the directory's owner.

**MariaDB.** On the first install the account is created from `mariadb-user.sql` unchanged
([Network and security model](network-and-security-model.md), *MariaDB access*), with `plesk db` when `plesk` is on the search path,
else `mariadb`. When that fails or neither exists, the install continues with `install.sh: WARNING MariaDB account not created; run:
<plesk db | mariadb> < <bundle>/mariadb-user.sql` (the path of the file the script ran).

## Checks on the server

After the start the script prints one line per check, each `check <name>: ok` or `check <name>: WARNING <what to do>`; a warning never
fails the installation:

| Line | What it checks |
| ---- | -------------- |
| `check journal-group` | Every journal file below `/var/log/journal` is readable by the group `systemd-journal` (warns when there is none). |
| `check journal-clock` | `/proc/uptime` and the monotonic timestamp of a fresh journal entry differ by less than 1 s; else `WARNING /proc/uptime and the journal differ by <n> ms; live kills get no extra snapshots`, and `WARNING could not read the journal timestamp; see deploy/agent/README.md` when either value cannot be read. |
| `check ipc-filter` | `systemd-analyze syscall-filter @ipc` lists `process_madvise`. |
| `check capabilities` | The running agent's `CapEff` is `CAP_SYS_PTRACE` (`0000000000080000`) alone (`CapEff is unknown` when it cannot be read). |

## Uninstall and purge

- `uninstall.sh [--purge]` (another argument: `usage: uninstall.sh [--purge]`, exit 2) runs `systemctl disable --now vandox-agent`
  (the agent saves its state), removes the unit and the binary, runs `systemctl daemon-reload` and `systemctl reset-failed
  vandox-agent`, and prints `uninstall.sh: removed <item>` or `uninstall.sh: <item> not present` for each, so an absent or partial
  installation succeeds with exit 0.
- A plain run keeps the user, `agent.yaml`, the token, the spool and the MariaDB account and says so: `uninstall.sh: kept user
  vandox-agent, /etc/vandox/agent.yaml, /etc/vandox/agent-token, <spool> and the MariaDB account; remove them with --purge`.
- `--purge` additionally removes `agent.yaml` and the token, prints `uninstall.sh: deleting /var/lib/vandox (records not yet sent are
  lost)` and deletes `/var/lib/vandox` with `rm -rf --one-file-system` (which follows no link), drops the MariaDB account with
  `mariadb-user-remove.sql` (the one statement `DROP USER IF EXISTS 'vandox-agent'@'localhost'`; when that fails: `uninstall.sh: WARNING
  MariaDB account not removed; run: <plesk db | mariadb> < <bundle>/mariadb-user-remove.sql`), deletes the user (never with `-r`, so a
  home directory stays and is named) and the group, and removes `/etc/vandox` only when it is empty.
- The spool of a kept configuration is read as above; an unreadable one stops the script with exit 2 before anything is removed.

## The unit in short

`Type=notify`, `User=vandox-agent`, the groups `adm` and `systemd-journal`, `CAP_SYS_PTRACE` as the only ambient capability and the
whole bounding set, `NoNewPrivileges=yes`, the filesystem and kernel protections of [Agent rights](agent-rights.md), a system-call
filter that denies the six calls of record 0122, `LoadCredential=agent-token:/etc/vandox/agent-token` with `VANDOX_AGENT_TOKEN_FILE`
naming `/run/credentials/vandox-agent.service/agent-token` (the token is never an argument or environment value), `IPAddressDeny=any`
with `IPAddressAllow=localhost 100.64.0.0/10 fd7a:115c:a1e0::/48` (the enforcement of "only over the tailnet"), `MemoryMax=256M`,
`GOMEMLIMIT=192MiB`, `CPUQuota=50%`, `Nice=5`, `IOWeight=50`, `WatchdogSec=2min`, `Restart=on-failure`, `RestartSec=30s`,
`StartLimitIntervalSec=0`. What the notification, watchdog and restart settings do is in [Agent](agent.md), *Service manager*.

## Related decisions

- [0131](../decisions/0131-the-agents-install-scripts-unit-and-files-reach-the-server-as-an-install-bundle-released-with-the-binary.md) —
  why the scripts, unit and files reach the server as an install bundle released with the binary.
- [0182](../decisions/0182-the-agent-token-reaches-the-agent-as-a-systemd-credential.md) — why the token is a systemd credential.
- [0183](../decisions/0183-the-agent-unit-allows-only-localhost-and-the-tailnet-ranges-as-addresses.md) — why the unit allows only localhost
  and the tailnet addresses.
- [0184](../decisions/0184-the-agent-unit-sets-a-hard-memory-limit-with-gomemlimit-below-it.md) — why the resource limits and `GOMEMLIMIT=`.
- [0185](../decisions/0185-the-agent-units-system-call-filter-is-built-from-system-service-plus-adjtimex.md) — why the system-call filter is
  `@system-service` plus `adjtimex`.
- [0186](../decisions/0186-install-sh-checks-the-binary-against-the-sha-256-the-bundle-carries.md) — why the binary is checked against the
  SHA-256 the bundle carries.
- [0187](../decisions/0187-uninstalling-the-agent-removes-it-and-purging-also-deletes-its-data.md) — why remove and purge.
- [0188](../decisions/0188-the-agent-unit-and-scripts-are-verified-by-a-ci-job-on-systemd-249.md) — why the unit and scripts are verified by
  a CI job on systemd 249.
- [0189](../decisions/0189-the-install-scripts-read-spool-directory-of-a-kept-configuration-with-a-fail-closed-line-reader.md) — why
  `spool.directory` of a kept configuration is read by a fail-closed line reader.
- [0190](../decisions/0190-the-install-scripts-refuse-a-bundle-another-user-could-change-and-check-its-manifest-before-sourcing.md) — why
  the entry check and the manifest check before sourcing.
- [0191](../decisions/0191-the-documented-install-steps-are-single-commands-with-check-and-extraction-in-one-line.md) — why the documented
  install steps are single commands.
- [0134](../decisions/0134-sender-binds-non-loopback-connections-to-the-tailnet-device.md) — why the sender binds to `tailscale0` and
  the unit needs no ordering on `tailscaled`.
- [0030](../decisions/0030-agent-runs-unprivileged-with-named-capabilities.md) — why a dedicated unprivileged user.

## Not here

- The rights and confinement the unit applies, and why: [Agent rights](agent-rights.md).
- The tailnet policy and the MariaDB account: [Network and security model](network-and-security-model.md); the setup steps:
  `deploy/agent/README.md`.
- The configuration file and the token's format: [Configuration and secrets](configuration-and-secrets.md); the agent's own start-up,
  logging and service manager rules: [Agent](agent.md).
- How releases and the bundle are built and published: `docs/CONTRIBUTING.md`.

## Implementation

`deploy/agent/` (`install.sh`, `uninstall.sh`, `common.sh` with `check_bundle_dir` and `read_spool_directory`, `vandox-agent.service`,
`mariadb-user.sql`, `mariadb-user-remove.sql`, the example `agent.yaml`); a test-only Go package pins the unit and the scripts
(`TestService_*`, `TestEntryCheck_*`, `TestCheckBundleDir_*`, `TestInstallDocuments_UseTheReleaseBundle`).
