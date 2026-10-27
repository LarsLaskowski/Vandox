# Spec: Agent hardening and installation

Source: Issue #42 (and its two comments)
Status: Approved by Lead

## Problem

The agent of v0.2.0 collects and sends, but nothing installs it: `deploy/agent/` has no unit and no scripts, although the Agent area
(*Rights*, *Service manager*), security area 6 and records 0030, 0110, 0122 require many unit settings. Assembled by hand, every mistake
breaks a collector silently or widens what a compromised agent reaches; several facts are still "to be checked on the server (#42)".

## Scenarios

- S1: (install) Given a fresh Ubuntu 22.04 server with systemd and the source of release `vX.Y.Z`, when root runs
  `deploy/agent/install.sh --version vX.Y.Z --agent-id web-1 --backend-url http://100.101.102.103:8081` and types the agent token at the
  prompt (no echo; piped standard input works too), then the script downloads `vandox-agent-linux-amd64` and `SHA256SUMS` of that release
  (`--from <dir>` takes both from a local directory instead), checks the checksum and the binary's `-version`, and creates: the system user `vandox-agent` (no login shell, no home, supplementary groups exactly `adm` and
  `systemd-journal`), `/usr/local/bin/vandox-agent` (root-owned, 0755), `/etc/vandox/agent.yaml` from the example with the two values
  (root:vandox-agent, 0640), `/etc/vandox/agent-token` (root:root, 0600), `/var/lib/vandox/spool` (vandox-agent, 0700), the unit
  `vandox-agent.service` and the MariaDB account (`plesk db` on Plesk, else `mariadb`); it starts the enabled unit and ends with `vandox-agent vX.Y.Z installed and running`.
- S2: (reboot) Given an installed agent, when the server reboots, then `vandox-agent.service` starts by itself after the journal flush and
  `systemctl is-active vandox-agent` says `active`.
- S3: (upgrade, repeat) Given an installed agent, when root runs the install script again with another (or the same) version, then only
  the binary and the unit are replaced, configuration, token, spool and state are kept, the agent restarts and continues from its spool
  and state file. The token is asked for only when `/etc/vandox/agent-token` is missing, whether or not `agent.yaml` exists. The new
  binary is written to a temporary file in `/usr/local/bin` and renamed over the old one, never written into the running file. When the
  script's checkout is a Git tag other than `--version`, it prints `install.sh: WARNING checkout is <tag>, not <version>` (the unit
  comes from the checkout).
- S4: (uninstall) When root runs `deploy/agent/uninstall.sh`, then the unit is stopped (the agent saves its state), disabled and removed
  and the binary is removed; user, configuration, token, spool and MariaDB account are kept so a later install continues, and the script
  says so and names `--purge`.
- S5: (purge) When root runs `deploy/agent/uninstall.sh --purge`, then additionally `/etc/vandox/agent.yaml`, `/etc/vandox/agent-token`,
  `/var/lib/vandox` (unsent records included, which the script states before it deletes), the MariaDB account and the user
  `vandox-agent` are removed; nothing of the installation remains.
- S6: (hardening report) When the operator runs `systemd-analyze security vandox-agent` on the server, then the overall exposure level
  is `OK` or better, and `deploy/agent/README.md` notes the score and each item still flagged with the collector that needs it.
- S7: (server checks) After S1 the script prints the result of each check the documents leave to the server (AC10), each as one line
  `check <name>: ok` or `check <name>: WARNING <what to do>`; a warning does not fail the installation.

## Errors and edge cases

- E1: Not root, no systemd as PID 1, or a machine other than `x86_64`: the script stops before any change with
  `install.sh: must run as root` / `install.sh: needs systemd` / `install.sh: only linux-amd64 is supported`, exit 1.
- E2: The binary does not match its `SHA256SUMS` line, or `SHA256SUMS` lacks exactly one well-formed line for
  `vandox-agent-linux-amd64`: `install.sh: checksum mismatch, nothing installed`, exit 1; an installed binary stays untouched and nothing
  downloaded is ever run.
- E3: The download fails: `install.sh: download failed: <file>`, exit 1, nothing changed. A verified binary whose `-version` names
  another version: `install.sh: binary is not <version>`, exit 1, nothing changed.
- E4: `--version` missing or not of the form `vX.Y.Z` / `vX.Y.Z-rc.N`: usage text, exit 2. The token given as an option or argument:
  refused with usage text (it would show in `ps` and the shell history).
- E5: The token read on a first install breaks the secret rules (fewer than 32 or more than 4096 characters, a character outside
  `0x21`–`0x7E`): `install.sh: token rejected: <rule>`, the token never echoed, exit 1, nothing changed.
- E6: First install without `--agent-id` / `--backend-url` and no `/etc/vandox/agent.yaml`: usage text, exit 2. An `--agent-id` outside
  the wire rule (`[A-Za-z0-9][A-Za-z0-9._-]{0,63}`) or a `--backend-url` that is not `http(s)://host[:port][/]` without whitespace,
  quotes, `#` or control characters: usage text, exit 2. A host that is an IP literal outside the tailnet ranges (NFR *Network*) gets
  `install.sh: WARNING <host> is not a tailnet address; the unit blocks it`. An existing `agent.yaml` is never overwritten; given values
  are then ignored with the line `install.sh: keeping existing /etc/vandox/agent.yaml`. The script, uninstall and purge take
  `spool.directory` from the kept file (default `/var/lib/vandox/spool`); a value other than one directory directly below `/var/lib/vandox` stops the script with
  `install.sh: spool.directory must be one directory below /var/lib/vandox`, exit 2, because the unit allows writes only there; an existing spool
  directory that is a link or not owned by `vandox-agent` stops it with `install.sh: <path> is not the agent's directory`, exit 1.
- E7: MariaDB not installed or the administrator login fails: the account is not created, the line
  `install.sh: WARNING MariaDB account not created; run: plesk db < deploy/agent/mariadb-user.sql` (or `mariadb <`), install continues.
  `--no-mariadb` skips the step without a warning.
- E8: The agent does not reach `READY=1` (invalid configuration, unreadable token, spool problem): the script ends with
  `install.sh: vandox-agent did not start; see: journalctl -u vandox-agent`, exit 1; the installed files stay so the operator can fix the
  cause, and the unit keeps retrying (E9).
- E9: A start fails at any time (for example the spool directory was removed, comment 2 of #42): systemd restarts the agent at a fixed
  interval without ever giving up (no `start-limit-hit`), so it comes back by itself once the cause is fixed.
- E10: A collector runner stops moving: the watchdog is withheld and systemd restarts the agent.
- E11: The agent exceeds its memory limit: the kernel kills it inside its own cgroup, never another process, and systemd restarts it.
- E12: A `backend.url` host resolved by a name server outside the allowed addresses fails as `connection failed`; the README says to
  use the tailnet address or MagicDNS.
- E13: A user `vandox-agent` already exists: it is reused and set to the S1 state — shell `nologin`, password locked, primary group
  `vandox-agent` (created when missing), supplementary groups exactly `adm` and `systemd-journal` — and the script prints each change.
  When it has a home directory, the script names it and leaves it in place (also on purge).
- E14: Uninstall of an absent or partial installation removes what exists, reports the rest absent, exit 0.

## Non-functional requirements

- Security (area 6, records 0030, 0122): the unit runs the agent as `vandox-agent`, never root, with `CAP_SYS_PTRACE` as its only ambient
  capability and the only one in the bounding set, every confinement the Agent area *Rights* and `deploy/agent/README.md` (*Unit settings that
  must stay as they are*) require, none they forbid, and `ProtectSystem=strict`, `ReadWritePaths=/var/lib/vandox`, `PrivateTmp=yes`. `Type=notify`, `NotifyAccess=main`, `WatchdogSec=`, `After=systemd-journal-flush.service`.
- Network (records 0006, 0010): the unit allows IP traffic only to the tailnet ranges (`100.64.0.0/10`, `fd7a:115c:a1e0::/48`) and
  localhost; the agent listens on nothing.
- Resources (record 0025, ARCHITECTURE "frugal on the server"): `MemoryMax=`, `CPUQuota=`, `Nice=` and `IOWeight=` are set; each value and
  its reason is in `deploy/agent/README.md`, `MemoryMax=` derived from the documented bounds of the collectors in memory pressure mode
  (which reads the system's `MemAvailable`, not the cgroup's) plus the Go runtime's GC headroom; the README also states whether the
  unit sets `GOMEMLIMIT=` below it and why (a unit setting, not an agent option). A limit must not
  make the agent miss its schedule on an idle server.
- Secrets (area 8, record 0032): the token reaches the agent only as a systemd credential behind `VANDOX_AGENT_TOKEN_FILE`, whose
  value is written out as `/run/credentials/vandox-agent.service/agent-token`, never with the `%d` specifier; the token is never in the
  unit, an environment line, a command line, the script's output or a log.
- Install-script safety: it runs as root, downloads only over HTTPS from this repository's release (`curl --proto =https
  --proto-redir =https --fail`, since the release URL redirects to another GitHub host), works in a private temporary
  directory removed on every exit, never follows a link it did not create, and changes nothing before every check of E1–E6 passed.
- Compatibility: Ubuntu 22.04 with systemd 249 and kernel 5.15 is the target; every unit setting and specifier must be known to systemd
  249, because systemd ignores an unknown key with a log line only.
  `bash`, `curl`, `sha256sum` and the shadow utilities are the only tools assumed.
- Verification: the scripts pass the shell analyzers of the *Analyzer gate*; S2, S6, S7 and the reboot can only be run on a systemd host
  by the Product Manager, who notes the results (AC10).
- No new agent option; no change to the binary's behavior.

## Acceptance criteria

- [ ] AC1: On a first install the script creates exactly the files, user, groups, owners and modes of S1, enables and starts the unit, and
  prints the success line; an existing `vandox-agent` user is brought to that state with each change printed (S1, E13).
- [ ] AC2: A repeated install replaces only binary and unit, keeps configuration, token, spool and state, asks for no token, and an
  existing `agent.yaml` is never overwritten; the token is asked only when its file is missing, the binary is replaced by rename,
  and a checkout tag other than `--version` is warned about (S3, E6).
- [ ] AC3: The binary is installed only when its SHA-256 equals the single well-formed `SHA256SUMS` line for it; any mismatch, malformed
  file or failed download stops the script before any change, and nothing downloaded is run before the match (E2, E3).
- [ ] AC4: Every precondition and input error of E1, E4, E5 and E6 (agent ID, URL and spool directory included) stops the script with its message and exit code before any change; the
  token is accepted only from the prompt or standard input and never printed (E1, E4, E5, E6).
- [ ] AC5: The MariaDB account is created from `deploy/agent/mariadb-user.sql` unchanged; when that fails the script warns and continues,
  and `--no-mariadb` skips it (S1, E7).
- [ ] AC6: A start that does not reach readiness makes the script fail with the `journalctl` hint and leaves the installation in place;
  the unit restarts a failed or watchdog-stopped agent at a fixed interval without a start limit (E8, E9, E10, E11).
- [ ] AC7: `uninstall.sh` removes unit and binary and keeps the rest; `--purge` removes everything of S1, states before deleting that
  unsent records are lost, and both succeed on a partial or absent installation (S4, S5, E14).
- [ ] AC8: The unit file holds every setting the NFR *Security*, *Network*, *Resources* and *Secrets* bullets require and none they forbid,
  and `systemd-analyze verify` of systemd 249 (Ubuntu 22.04's package) reports no unknown section, key or specifier (S6, E12).
- [ ] AC9: `systemd-analyze security` rates the unit `OK` or better, and `deploy/agent/README.md` notes the score, the systemd version it
  was taken with and each remaining flagged item with its reason (S6).
- [ ] AC10: The script runs and reports, warning only, the server checks the documents leave to #42: journal files readable by group
  `systemd-journal`; `/proc/uptime` against the `__MONOTONIC_TIMESTAMP` of a fresh `logger` entry below 1 s; `systemd-analyze
  syscall-filter @ipc` lists `process_madvise`; the agent's `CapEff` is `CAP_SYS_PTRACE` alone. The Product Manager's run on the server —
  these checks, reboot autostart, `ptrace` attach refused with `EPERM`, the score of AC9 — is noted in `deploy/agent/README.md` (S2, S7).
- [ ] AC11: The README *Install* section, `docs/INSTALL.md` and `deploy/agent/README.md` describe installing, upgrading, uninstalling and
  verifying the agent, and no document still says the unit or the install scripts do not exist (S1–S5).

## Out of scope

- Publishing the scripts or the unit as release assets (the release workflow is unchanged; the scripts come with the tagged source).
- Provenance on the server (`gh attestation verify`, shown in the README): the script checks the checksum the issue asks for, which
  proves integrity, not authorship.
- The polkit rule for self-healing (v0.6.0), other distributions and architectures, a package (`.deb`).
- Two install runs at once; the rsyslog logrotate check (Agent area, *Log shipping*), a manual step in `deploy/agent/README.md`.

## Open questions

- Which capabilities does the issue's "`/proc/<pid>/fd`, `smaps_rollup` and the journal" need? Answered: `CAP_SYS_PTRACE` alone; the
  claim is refuted in part: `/proc/<pid>/fd` is not read (records 0115, 0122), the journal needs the group `systemd-journal`.
- Can a unit with every required setting reach `OK`? Answered: a draft unit with the NFR's settings scored 2.2 `OK`
  offline on systemd 255 (this session); 249 has no offline mode, so AC9's score is taken on the server (AC10).
- What does uninstall remove? Answered: a Lead decision, remove and purge as Debian packages do (S4, S5), so a plain
  uninstall never destroys unsent records.
- Where does the binary come from? Answered: the assets of the given release (record 0037);
  `--version` is required so binary and checksum come from one release.
- Should `IPAddressAllow=` be set (left to #42 by the Agent area)? Answered: yes, tailnet ranges and localhost, from the
  only-over-Tailscale guarantee (ARCHITECTURE, *Security model*; records 0006, 0010).

## Area documents

- `docs/areas/agent.md`: *Connection* (the "every other address is `https`" bullet) says the unit limits the agent to tailnet
  addresses; *Status* names the unit and scripts; *Rights* and *Service manager* name the unit as built (address
  allow-list, restart and watchdog values, resource limits); the "(#42)" notes point to the server checks; a short *Installation*
  section: paths, owners, modes, install/upgrade/uninstall/purge behavior, the checksum rule.
- `docs/areas/configuration-and-secrets.md`: the agent token as a systemd credential in `/etc/vandox/agent-token`.
- `docs/areas/network-and-security-model.md`: the agent's outbound address allow-list.
- `deploy/agent/README.md`: every unit setting with its reason, the resource values, the security score and the server check results.

## Challenge

Devil's Advocate, spec challenge (answers folded into the sections named):

1. Major — AC8 checked only on systemd 255. Accepted: AC8 verifies with systemd 249 (how: `plan.md`), the Secrets NFR forbids `%d`.
2. Minor — kept `spool.directory` outside `/var/lib/vandox`. Accepted: E6, AC4; uninstall and purge use the same path.
3. Minor — unchecked `--agent-id` / `--backend-url`. Accepted: E6, AC4, the *Connection* entry under *Area documents*.
4. Minor — an existing user. Accepted: E13; a home directory the script did not create is kept, also on purge.
5. Minor — upgrade details. Accepted in part: S3, AC2; the checkout check only warns, for a Git checkout on a tag, because a source
   archive carries no reliable version.
6. Minor — redirects. Accepted: Install-script safety NFR.
7. Minor — `MemoryMax=` basis and `GOMEMLIMIT=`. Accepted: Resources NFR; `GOMEMLIMIT=` is decided in `plan.md`.

Lead, planning: added `--from <dir>` (S1; for the end-to-end check), the `-version` check (E3) and the
existing-spool check (E6).
