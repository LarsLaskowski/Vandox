# Plan: Agent hardening and installation

Source: Issue #42 | [spec.md](spec.md)
Status: Draft
Tier: security — the change creates security controls: the unit's capability and bounding set, system-call filter and address allow-list, and the handling of the agent token, all run by root-owned scripts.
Security areas touched: 6 Agent privileges (`deploy/agent/vandox-agent.service`, the user and groups `install.sh` creates); 7 MariaDB monitoring user (`install.sh` runs `mariadb-user.sql`, new `mariadb-user-remove.sql`); 8 Secrets handling (`/etc/vandox/agent-token`, `LoadCredential=`); 9 File writes and paths derived from external input (`install.sh` and `uninstall.sh` write and delete as root, `spool.directory` read from `agent.yaml`); 11 Outbound calls (`IPAddressDeny=`/`IPAddressAllow=`; the script's download); CI configuration (new job in `.github/workflows/ci.yml`, new `.github/scripts/check-agent-install.sh`)

## Problem / root cause

See [spec.md](spec.md): nothing installs the agent, and the unit the documents require does not exist.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The issue's "capabilities for `/proc/<pid>/fd`, `smaps_rollup` and the journal": only `CAP_SYS_PTRACE` is needed | `/proc/<pid>/fd` is never read (record 0115; `docs/areas/agent.md` 1818-1820), the journal needs the group `systemd-journal` (1943-1946) | refuted |
| 2 | A release publishes `vandox-agent-linux-amd64` and a `SHA256SUMS` of exactly one line `^[0-9a-f]{64}  vandox-agent-linux-amd64$` | `.github/workflows/release.yml` 119-133, 364 | confirmed |
| 3 | `vandox-agent --version` prints `vandox-agent <tag> (commit <sha>, built <date>)`; the tag is set by `-ldflags -X .../internal/version.Version=` | `.github/workflows/ci.yml` 205-227 | confirmed |
| 4 | The agent never creates `spool.directory`; without it the start ends with exit 1 before `READY=1` | `docs/areas/agent.md` 1576-1578, 1957-1961 | confirmed |
| 5 | The example `deploy/agent/agent.yaml` holds the lines `agent_id: web-1` (line 20) and `  url: http://100.64.0.1:8081` (line 27), `spool:` with `  directory: /var/lib/vandox/spool` (34-37) | read | confirmed |
| 6 | The agent refuses only unknown `VANDOX_` variables, so `GOMEMLIMIT` in the unit is accepted | `internal/config/secret.go` 85-95 | confirmed |
| 7 | A draft unit with every setting of *Approach* scores 2.2 `OK` | `systemd-analyze security --offline=true` (systemd 255, scratch) | confirmed |
| 8 | `systemd-analyze verify` reports an unknown key as `Unknown key name '<key>' in section '<section>', ignoring` | scratch run, systemd 255 (`StartLimitIntervalSec` placed in `[Service]`) | confirmed |
| 9 | A directory with only `_test.go` files builds, vets and tests under `./...` | scratch module (`go build`, `go vet`, `go test ./...`) | confirmed |
| 10 | Watchdog: a runner is on time until due + timeout + 30 s; the longest timeout is 10 s | `docs/areas/agent.md` 137-139; `internal/agent/agent.go` 36-92 | confirmed |
| 11 | Documented memory bounds: processes run ≤ 48 MiB allocated, MariaDB ≤ 32 MiB, logs ≤ 24 MiB live, kernel ≤ 4 MiB, sender one 1 MiB batch per stream | `.squad/project.md` area 10; `docs/areas/agent.md` 780, 1050, 1331, 1789 | confirmed |
| 12 | The agent's real peak RSS on the server stays below `GOMEMLIMIT` | to verify: the maximum of the agent's own RSS self-metric during the #45 parallel run; Product Manager, after the release | unverified |
| 13 | Every key of the unit is known to systemd 249 | to verify: the Tester reads `src/core/load-fragment-gperf.gperf.in` of systemd tag `v249` (shallow clone, scratch) for each key before writing `TestService_UsesOnlyKeysKnownToSystemd249` (step 5); then the CI job's `systemd-analyze verify` on 249 | unverified |
| 14 | GitHub's `ubuntu-22.04` runner boots systemd 249 as PID 1, has `python3`, `setpriv`, passwordless `sudo` and no `mariadb` or `plesk` | to verify: the CI script's first step prints `systemctl --version` and fails unless it starts with `systemd 249`; Dev, first CI run (step 7 push / step 11) | unverified |
| 15 | `systemctl start` of a `Type=notify` unit with `Restart=on-failure` returns non-zero when the process exits before `READY=1` | to verify: CI case "E8" (an invalid kept `agent.yaml`); Dev, first CI run | unverified |
| 16 | systemd enforces `IPAddressDeny=` on the 22.04 kernel (cgroup BPF) | to verify: CI case "address allow-list" (a listener on the runner's non-tailnet address gets no request, one on 127.0.0.1 gets one); Dev, first CI run | unverified |
| 17 | Ubuntu 22.04 servers have `python3` for the README's `ptrace` check | to verify: the Product Manager's server run (step 9) | unverified |

## Tests per criterion

Go tests in the test-only package `deploy/agent` run `bash` on the scripts, source them and call one function at a time, with stub
commands on `PATH`; none needs root, network or systemd. The CI script runs the whole flow on systemd 249.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestRenderConfig_SetsAgentIDAndURL`, `TestAgentYAML_HoldsTheLinesRenderConfigReplaces`, `TestEnsureUser_ReportsEachChange` (stubbed `getent`, `id`, `useradd`, `usermod`, `groupadd`, `passwd`); CI case "first install" | `deploy/agent/install_test.go`; `.github/scripts/check-agent-install.sh` |
| AC2 | `TestCheckoutWarning_OnlyForAnotherTag`, `TestNeedsToken_OnlyWithoutTokenFile`; CI case "repeat install" (no token asked, config and token unchanged, binary inode changed, spool files kept) | `deploy/agent/install_test.go`; `.github/scripts/check-agent-install.sh` |
| AC3 | `TestVerifyChecksum_AcceptsOnlyTheSingleMatchingLine` (table: match, other hash, two lines, other name, upper-case hex, one space, trailing text, empty, missing file), `TestFetchRelease_NeverRunsABinaryThatFailsTheChecksum`, `TestFetchRelease_ReportsAMissingFile`, `TestDownload_UsesHTTPSOnly` (stub `curl` records its arguments), `TestCheckBinaryVersion_RefusesAnotherVersion`; CI case "tampered checksum" (exit 1, nothing created) | `deploy/agent/install_test.go`; `.github/scripts/check-agent-install.sh` |
| AC4 | `TestInstall_UsageErrorsExitTwo` (table: no `--version`, `v1.2`, `1.2.3`, `v1.2.3-beta`, `--token x`, a positional argument, agent IDs `-a`, 65 characters, `a b`, `a:b`, `a#b`, newline; URLs `ftp://h`, `http://h/x`, `http://u@h`, `http://h:0`, `http://h:65536`, `"http://h"`, `http://h #`, a space, a newline), `TestValidateToken_EnforcesTheSecretRules` (31, 32, 4096, 4097 bytes; space; `0x7F`; a UTF-8 letter; empty), `TestCheckPlatform_RefusesNonRootNonSystemdAndOtherMachines`, `TestReadSpoolDirectory_AcceptsOnlyTheBlockFormBelowVarLibVandox` (table: no file section, no key, plain, single- and double-quoted, trailing comment, `/var/lib/vandox`, `/var/lib/vandox2/x`, `/tmp/x`, `/var/lib/vandox/../x`, `/var/lib/vandox/x/`, `//`, a flow mapping, two `directory` keys, a tab), `TestEnsureSpoolDirectory_RefusesALinkOrAnotherOwner`, `TestTailnetWarning_OnlyForLiteralsOutsideTheRanges` (100.64.0.0, 100.127.255.255, 100.128.0.1, 10.0.0.1, `[fd7a:115c:a1e0::1]`, `[fd7a:115c:a1e1::1]`, a name) | `deploy/agent/install_test.go`, `deploy/agent/common_test.go` |
| AC5 | `TestMariadbAccount_UsesPleskWhenPresent`, `TestMariadbAccount_WarnsAndContinuesWhenItFails`, `TestMariadbAccount_SkippedWithNoMariadb`; `TestUserSQL_CreatesOnlyTheProcessUser` stays unchanged | `deploy/agent/common_test.go`, `deploy/agent/install_test.go` |
| AC6 | `TestService_RestartsWithoutStartLimit`, `TestService_NotifiesAndHasAWatchdog`; CI cases "E8" and "restart after a removed spool directory" | `deploy/agent/service_test.go`; `.github/scripts/check-agent-install.sh` |
| AC7 | `TestUninstall_UsageErrorsExitTwo`, `TestMariadbRemoveSQL_DropsOnlyTheAgentAccount`; CI cases "uninstall before install", "uninstall", "reinstall", "purge" | `deploy/agent/uninstall_test.go`; `.github/scripts/check-agent-install.sh` |
| AC8 | `TestService_HoldsTheRequiredSettings`, `TestService_HoldsNoForbiddenSetting`, `TestService_UsesOnlyKeysKnownToSystemd249`, `TestService_HasNoSpecifier`, `TestService_TokenOnlyAsCredential`, `TestService_SystemdAnalyzeVerifyReportsNoUnknownKey` (skipped without `systemd-analyze`); CI cases "verify on 249", "address allow-list" | `deploy/agent/service_test.go`; `.github/scripts/check-agent-install.sh` |
| AC9 | CI case "security score" (`systemd-analyze security vandox-agent` ends in `OK`, `SAFE` or `PERFECT`); the server score per *Approach*, *Server run* | `.github/scripts/check-agent-install.sh` |
| AC10 | `TestCheckJournalGroup_ReportsOkAndWarning`, `TestCheckJournalClock_WarnsFromOneSecond` (999,999 µs ok, 1,000,000 warns, either sign), `TestCheckIPCFilter_ReportsOkAndWarning`, `TestCheckCapabilities_ReportsOkAndWarning`; CI case "server checks and ptrace" | `deploy/agent/install_test.go`; `.github/scripts/check-agent-install.sh` |
| AC11 | `TestReadme_NamesNoMissingUnitOrScripts` (no "follow with the collectors", "nothing to install", "will list" left in the files of *Documentation updates* that mention them); the Reviewer reads the rest | `deploy/agent/service_test.go` |

## Approach

**Files.** `deploy/agent/vandox-agent.service`, `deploy/agent/common.sh` (sourced by both scripts), `deploy/agent/install.sh`,
`deploy/agent/uninstall.sh`, `deploy/agent/mariadb-user-remove.sql` (exactly `DROP USER IF EXISTS 'vandox-agent'@'localhost';` after
comments). Each script starts with `set -euo pipefail` and calls `main "$@"` only when `[[ "${BASH_SOURCE[0]}" == "$0" ]]`, so tests
can source it; `main` sets `umask 077`, `LC_ALL=C` and `PATH=/usr/sbin:/usr/bin:/sbin:/bin` first, the functions do not (tests stub
on `PATH`). `common.sh` is sourced from the script's own directory.

**Unit** (`[Unit]`): `Description=Vandox monitoring agent`, `Documentation=` the README on GitHub, `After=systemd-journal-flush.service`,
`StartLimitIntervalSec=0`. (`[Service]`): `Type=notify`, `NotifyAccess=main`, `ExecStart=/usr/local/bin/vandox-agent`,
`User=vandox-agent`, `Group=vandox-agent`, `SupplementaryGroups=adm systemd-journal`, `AmbientCapabilities=CAP_SYS_PTRACE`,
`CapabilityBoundingSet=CAP_SYS_PTRACE`, `NoNewPrivileges=yes`, `LoadCredential=agent-token:/etc/vandox/agent-token`,
`Environment=VANDOX_AGENT_TOKEN_FILE=/run/credentials/vandox-agent.service/agent-token`, `Environment=GOMEMLIMIT=192MiB`,
`ProtectSystem=strict`, `ReadWritePaths=/var/lib/vandox`, `ProtectHome=yes`, `PrivateTmp=yes`, `PrivateDevices=yes`,
`ProtectKernelTunables=yes`, `ProtectKernelModules=yes`, `ProtectKernelLogs=yes`, `ProtectControlGroups=yes`, `ProtectHostname=yes`,
`RestrictNamespaces=yes`, `RestrictRealtime=yes`, `RestrictSUIDSGID=yes`, `LockPersonality=yes`, `MemoryDenyWriteExecute=yes`,
`RemoveIPC=yes`, `UMask=0077`, `KeyringMode=private`, `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6`,
`SystemCallArchitectures=native`, `SystemCallFilter=@system-service adjtimex`, `SystemCallFilter=~ptrace process_vm_readv
process_vm_writev pidfd_getfd open_by_handle_at process_madvise`, `SystemCallErrorNumber=EPERM`,
`InaccessiblePaths=/etc/shadow /etc/gshadow`, `IPAddressDeny=any`, `IPAddressAllow=localhost 100.64.0.0/10 fd7a:115c:a1e0::/48`,
`MemoryMax=256M`, `CPUQuota=50%`, `Nice=5`, `IOWeight=50`, `WatchdogSec=2min`, `Restart=on-failure`, `RestartSec=30s`.
(`[Install]`): `WantedBy=multi-user.target`. Forbidden (test): `ProtectClock`, `PrivateNetwork`, `NetworkNamespacePath`, `ProcSubset`,
`ProtectProc` other than `default`, `PrivateUsers`, `TemporaryFileSystem`, `DynamicUser`, any `%`, `VANDOX_AGENT_TOKEN=`,
`InaccessiblePaths=` naming `/run/dbus`, `/run/mysqld`, `/var/log/journal` or `/run/log/journal`.

**Resource values** (each reason goes into `deploy/agent/README.md`): `MemoryMax=256M` — the documented worst cases (fact 11) sum to
about 112 MiB if every collector peaks at once under pressure, plus runtime and stacks; `GOMEMLIMIT=192MiB` makes the GC work before
the hard limit, leaving 64 MiB for page cache and runtime overhead the soft limit does not count. `CPUQuota=50%` bounds a runaway
loop to half a core; the collectors' timeouts (≤ 10 s) leave room. `Nice=5` and `IOWeight=50` yield to the services under
contention without starving the agent while an outage builds (a starved agent records gaps, not silence). `WatchdogSec=2min`: pings
every minute while on time (fact 10); a short stall under memory pressure must not restart the agent. `RestartSec=30s` with
`StartLimitIntervalSec=0`: retried forever, at most twice a minute.

**install.sh order** (no change before step 6): 1 `parse_args` (exit 2); 2 `check_platform "$EUID" "$(cat /proc/1/comm)"
"$(uname -m)"` (exit 1); 3 config: an existing `/etc/vandox/agent.yaml` is kept (agent ID and URL then only warned as ignored) and
`read_spool_directory` reads it, else `--agent-id`/`--backend-url` are required and the spool is the default; `tailnet_warning`;
4 the token: `needs_token` (file missing) → `read_token` (`read -r -s` on a terminal, else one line of standard input) →
`validate_token`; 5 `fetch_release` into `mktemp -d` (mode 0711, removed by an `EXIT` trap): `curl --proto =https --proto-redir =https
--fail --location --silent --show-error --max-time 300 -o <file> https://github.com/LarsLaskowski/Vandox/releases/download/<version>/<file>`,
or a copy from `--from`; `verify_checksum`; then `check_binary_version` runs the binary as `setpriv --reuid=nobody --regid=nogroup
--clear-groups --no-new-privs <bin> -version`; `checkout_warning` (`git describe --tags --exact-match` in the script's directory, only
when Git and a work tree exist); 6 apply: `ensure_user`; `/etc/vandox` (root 0755, refused when a link), `agent.yaml` (via a temporary
file in `/etc/vandox` and `mv -T`, root:vandox-agent 0640) when new, the token (temporary file, 0600 root, `mv -T`) when needed;
`/var/lib/vandox` (root 0755) and `ensure_spool_directory`; the binary (temporary file in `/usr/local/bin`, `install -m 0755`,
`mv -T`); the unit (0644 root) and `systemctl daemon-reload`; on a first install (unit file was absent) `run_mariadb_sql` unless
`--no-mariadb`; `systemctl enable vandox-agent` and `systemctl restart vandox-agent`; on failure the E8 line, exit 1; 7 the server
checks; 8 the success line. A link in place of any file the script writes is refused (`[[ -L ]]` before the write, `mv -T` onto a
plain name), never followed.

**read_spool_directory**: no `spool:` line at column 0 or no `directory:` key in its block → the default; a block line `^  directory:
<value>` with a plain, single- or double-quoted value (no escapes) and an optional ` # comment` → the value; any other form of
`spool` (flow, anchor, tag, two keys, a tab) → `install.sh: cannot read spool.directory from /etc/vandox/agent.yaml`, exit 2. The
value must be absolute and clean and be `/var/lib/vandox` or below it, else the E6 line, exit 2.

**Output** (exact; on standard error with exit 1 or 2 for errors, standard output otherwise): the lines of spec E1–E8 and E13, and
`usage: install.sh --version vX.Y.Z [--agent-id ID --backend-url URL] [--from DIR] [--no-mariadb]`; `install.sh: the token is read from
standard input, never from an option` (before the usage line, for `--token` or `--token=`); `install.sh: --agent-id is not a valid
agent ID`, `install.sh: --backend-url is not http(s)://host[:port]` (values never echoed); token rules `fewer than 32 characters`, `more
than 4096 characters`, `a character outside 0x21-0x7E`; user changes `install.sh: user vandox-agent: <shell set to /usr/sbin/nologin |
password locked | primary group set to vandox-agent | groups set to adm,systemd-journal | created>` and `install.sh: group
vandox-agent created`; checks `check journal-group: ok` / `check journal-group: WARNING journal files not readable by group
systemd-journal; see deploy/agent/README.md`, `check journal-clock: ok` / `check journal-clock: WARNING /proc/uptime and the journal
differ by <n> ms; live kills get no extra snapshots`, `check ipc-filter: ok` / `check ipc-filter: WARNING systemd does not list
process_madvise in @ipc`, `check capabilities: ok` / `check capabilities: WARNING CapEff is <hex>, not CAP_SYS_PTRACE alone`.
uninstall.sh: `usage: uninstall.sh [--purge]`; `uninstall.sh: removed <unit | binary | ...>`, `uninstall.sh: <item> not present`;
`uninstall.sh: kept user vandox-agent, /etc/vandox/agent.yaml, /etc/vandox/agent-token, <spool> and the MariaDB account; remove them
with --purge`; `uninstall.sh: deleting /var/lib/vandox (records not yet sent are lost)`; `uninstall.sh: WARNING MariaDB account not
removed; run: <plesk db | mariadb> < deploy/agent/mariadb-user-remove.sql`. Purge: `userdel vandox-agent` (never `-r`), `groupdel`
when the group remains, `/etc/vandox` removed only when empty.

**CI job** `agent-install` in `ci.yml`: `runs-on: ubuntu-22.04`, job permissions `contents: read`, no secrets, checkout (no persisted
token) and `setup-go` pinned as the other jobs, then `sudo .github/scripts/check-agent-install.sh`. The script builds the agent with
version `v0.0.0-rc.0`, writes `SHA256SUMS`, and runs these cases in order, failing on the first mismatch: verify on 249 (fact 14,
then `systemd-analyze verify` of the repository's unit file, failing on `Unknown` or `ignoring`); uninstall before install; tampered
checksum; E8 (a kept invalid `agent.yaml`, then removed); first install (files, owners, modes, user, groups, `is-enabled`,
`is-active`, the MariaDB warning, the four check lines, `CapEff`/`CapBnd` `0000000000080000`, `NoNewPrivs: 1`, `Seccomp: 2`);
security score; ptrace (`systemd-run --wait --pipe` with the unit's `User`, capability, `NoNewPrivileges` and filter properties runs
`python3` calling `ptrace(PTRACE_ATTACH)` on a root `sleep`, expecting `EPERM`); repeat install; restart after a removed spool
directory (back to `active` within 45 s); address allow-list (fact 16); uninstall; reinstall; purge (nothing of S1 left).

**Server run** (spec AC10): before step 9 the Product Manager installs the branch's build on the server with `--from` and runs the
README's *Checks on the server* (the script's checks, `systemd-analyze security`, the `ptrace` command, a reboot); the Dev enters the
results in `deploy/agent/README.md`.

## Options considered

Options 1–18 of record 0131 (token delivery, network, memory, filter groups, binary verification, uninstall, the systemd 249 check,
reading `spool.directory`). Beyond them: **bats** for the scripts — a new test tool and dependency, rejected for Go tests that source
the scripts; **a `systemd` container in CI** (`ubuntu:22.04` with systemd as PID 1, `--privileged`) — more moving parts than the
runner, which already boots 249.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| deploy | `deploy/agent/vandox-agent.service`, `common.sh`, `install.sh`, `uninstall.sh`, `mariadb-user-remove.sql` | new |
| deploy | `deploy/agent/agent.yaml`, `mariadb-user.sql` | unchanged (tests pin the replaced lines) |
| CI | `.github/workflows/ci.yml` (job `agent-install`), `.github/scripts/check-agent-install.sh` | new |
| Go | `deploy/agent/*_test.go` | new test-only package |

## Signatures (for the Dev's skeleton)

Bash functions; each skeleton body is return 99, so the scripts pass bash -n and the tests fail.

- common.sh: `read_spool_directory()` (args: config file; prints the path; status 0, or 2 with the message);
  `check_platform()` (args: euid, PID 1 command name, machine; 0, or 1 with the message); `run_mariadb_sql()` (args: SQL file,
  script name; runs plesk db when plesk is on the search path, else mariadb; prints the warning on failure; always 0).
- install.sh: `parse_args()` (sets the globals VERSION, AGENT_ID, BACKEND_URL, FROM_DIR, NO_MARIADB; exit 2); `validate_version()`,
  `validate_agent_id()`, `validate_backend_url()` (arg: value; 0 or 2); `tailnet_warning()` (arg: URL; prints the warning or nothing);
  `needs_token()` (arg: token file; 0 when missing); `read_token()` (sets the global TOKEN); `validate_token()` (arg: token; 0, or 1
  with the rule); `fetch_release()` (args: version, from-dir or empty, work dir; 0 or 1); `verify_checksum()` (arg: work dir; 0 or
  1); `check_binary_version()` (args: binary, version; 0 or 1); `checkout_warning()` (args: checkout tag or empty, version);
  `render_config()` (args: example, agent ID, URL, output file); `ensure_user()`; `ensure_spool_directory()` (arg: path; 0 or 1);
  `check_journal_group()` (args: journal directory, group); `check_journal_clock()` (args: first field of /proc/uptime, monotonic
  microseconds); `check_ipc_filter()` (reads the output of systemd-analyze syscall-filter @ipc on standard input);
  `check_capabilities()` (arg: status file); `main()`.
- uninstall.sh: `parse_args()` (sets the global PURGE; exit 2); `main()`.

## Test files

`deploy/agent/common_test.go`, `deploy/agent/install_test.go`, `deploy/agent/uninstall_test.go`, `deploy/agent/service_test.go`
(package `agent`, test-only; each beside the file it tests), and `.github/scripts/check-agent-install.sh` (Tester; runs only in CI).
Existing test call sites affected: none (`internal/mariadb` `TestUserSQL_CreatesOnlyTheProcessUser` reads `mariadb-user.sql`, which is
unchanged).

Files no unit test can cover: the shell scripts and the unit count for no coverage figure (`COVERAGE_PATHSPECS` is `*.go`, `*.cs`,
`*.razor`), so no exclusion is needed; their system-changing parts (`main`, `ensure_user`'s real commands, `systemctl`) run only in the
CI job, because they need root and systemd as PID 1.

## Areas

- Agent: a new *Installation* section (paths, owners, modes, install, upgrade, uninstall, purge, checksum and version rule, the server
  checks); *Status*; *Rights* and *Service manager* (the unit as built, the address allow-list, restart, watchdog and resource values);
  *Connection* (http only to tailnet addresses, now enforced); the "(#42)" notes (1062, 1359, 1420, 1578, 1842, 1865, 1947) point to
  *Checks on the server* or say what the unit does.
- Configuration and secrets: the agent token installed in `/etc/vandox/agent-token` and passed as a systemd credential.
- Network and security model: *Agent to backend* — the unit lets the agent reach only tailnet addresses and localhost.

## Documentation updates

All by the Dev once the code is green, except `docs/UNIT_TESTS.md` (Tester).

- `docs/areas/agent.md`, `docs/areas/configuration-and-secrets.md`, `docs/areas/network-and-security-model.md`: as *Areas*; the
  *Implementation* list (2067) names the unit and scripts; record 0131 under *Related decisions* of the agent area.
- `deploy/agent/README.md`: line 6 (scripts exist); *Unit settings that must stay as they are* (121: the allow-list is set); a new
  *Unit* section with every setting and its reason, the resource values (*Approach*), the `systemd-analyze security` result (CI on
  249 and the server) with each flagged item and its reason; a new *Install, upgrade, uninstall* section; *Checks on the server*
  with the commands and the Product Manager's results; the provenance check (`gh attestation verify`).
- `README.md`: *Install* — the *Agent* paragraph (155) replaced by the install, upgrade and uninstall commands and a link to
  `deploy/agent/README.md`; the `spool.directory` row (68) says the install script creates the directory below `/var/lib/vandox`.
- `docs/INSTALL.md`: a step 11 "Install the agent on the monitored server" after step 10; the introduction (5) no longer says there
  is no agent to install.
- `docs/ARCHITECTURE.md`: *Security model* (457) — "lists" instead of "will list (v0.2.0)", and the address allow-list with link to
  record 0131.
- `.squad/project.md`: area 6 names `install.sh`, `uninstall.sh`, `common.sh` and their guards (checksum before run, `setpriv` for the
  version check, no link followed, the token never in an argument or output) and the unit's address allow-list; area 7
  `mariadb-user-remove.sql`; area 8 the credential; area 11 the allow-list as the enforcement of "only over the tailnet".
- `.squad/stack.md`: *Layout* — `deploy/agent` is a test-only Go package; *Not checkable in a cloud session* — a row for
  `deploy/agent/*.sh` and the unit: local substitute the Go tests, `bash -n`, `systemd-analyze verify` (255); authoritative the CI job
  `agent-install`.
- `docs/UNIT_TESTS.md` (Tester): how the deploy scripts are tested (sourced functions, stubs on `PATH`, no root, network or systemd).

## Architecture check

The guarantees of `.squad/project.md` (never root, `CAP_SYS_PTRACE` alone, the six denied calls, `adjtimex` allowed, AF_UNIX, the bus,
MariaDB and journal paths reachable; secrets never in files, arguments or logs; outbound only over the tailnet) are implemented by the
unit, not weakened; the allow-list adds enforcement to the last one. `docs/ARCHITECTURE.md` changes as listed, owner Dev.

## Security considerations

- [ ] Limits: the token is checked in the C locale, so its 32 to 4096 limits are bytes, before anything is written; `SHA256SUMS` must be
  one line; `agent.yaml` is read only by the line rules of `read_spool_directory`.
- [ ] Messages that reach the user: only the fixed lines of *Approach*; no token, no configuration value but the validated host of the
  tailnet warning and the path in E6, no `curl` error body (`--silent --show-error` prints curl's own message, which names the URL of a
  public release only).

## Decision records

- `docs/decisions/0131-agent-unit-and-install-scripts-credential-tailnet-allow-list-checksum-remove-and-purge.md` (Proposed)

## Out of scope / follow-ups

As the spec. No follow-up issue.

## Challenge notes

Devil's Advocate, plan challenge: 0 major, 8 minor objections, appended verbatim.

1. **minor — the version check cannot execute the downloaded binary.** `main` sets `umask 077`, so `curl -o` (and the copy from `--from`) creates the binary as 0600 root without an execute bit. The 0711 work directory does not help: `setpriv --reuid=nobody … <bin> -version` then fails with `EACCES`, and every real install would end with `binary is not <version>` (plan *Approach*, install.sh step 5).
   - Instruction: after `verify_checksum` passes, `chmod 0755` the binary in the work directory before `check_binary_version`.
   - A unit test should pin that the function is called on a file that `nobody` can execute.
2. **minor — `spool.directory: /var/lib/vandox` (accepted as "at or below") breaks step 6.** Step 6 first creates `/var/lib/vandox` as root 0755. `ensure_spool_directory` then refuses it as "not the agent's directory", after user, token and binary have already been written.
   - The plan also leaves open who creates and owns the directories in between for a deeper path (`/var/lib/vandox/a/b`).
   - Instruction: `read_spool_directory` accepts only paths strictly below `/var/lib/vandox` (E6 message unchanged). Either create missing intermediate directories as root 0755, or allow exactly one level below. Name the chosen rule in the `TestReadSpoolDirectory_…` table, where `/var/lib/vandox` currently has no stated outcome.
3. **minor — documents that still recommend a backend outside the tailnet are not on the update list.** `deploy/agent/agent.yaml` 23-26 ("use https for any other network") and `docs/areas/agent.md` 54-55 ("every other address is `https`") describe a configuration that `IPAddressDeny=any` now blocks. Yet the plan lists `agent.yaml` as "unchanged" (*Affected projects*).
   - The same applies to `docs/areas/agent.md` 1843 and `deploy/agent/README.md` 121: both say an `IPAddressAllow=` "must include" the `/etc/resolv.conf` name server, and the plan does not include it.
   - Instruction: change the `backend.url` comment in `deploy/agent/agent.yaml`. Keep the lines that `render_config` replaces (20, 27) byte-identical so `TestAgentYAML_HoldsTheLinesRenderConfigReplaces` holds. Rewrite agent.md 54-55 and 1843 and README 121 to: tailnet addresses and localhost only; a name must resolve through a localhost or tailnet name server (E12).
4. **minor — a host name in `backend.url` can install "successfully" while the agent can never send.** Readiness does not depend on the connection, so S1's success line prints even when `/etc/resolv.conf` names a provider DNS outside the allow-list (E12).
   - Instruction: extend `tailnet_warning` (or add a S7-style check `check name-server`). When the host is a name and a `nameserver` line of `/etc/resolv.conf` is neither loopback nor in the tailnet ranges, print `install.sh: WARNING name server <ip> is blocked by the unit; use the tailnet address or MagicDNS`, warning only.
   - Add test rows for `127.0.0.53`, `100.100.100.100` and `1.1.1.1`.
5. **minor — the E8 CI case leaves the "first install" case not a first install.** In the CI case order, "E8 (a kept invalid `agent.yaml`, then removed)" runs right before "first install". If only `agent.yaml` is removed, the unit file, token and user remain. The next case then asks for no token and skips the MariaDB step (it runs only when the unit file was absent), so its assertions ("the MariaDB warning", token prompt) fail or test the wrong path.
   - Instruction: end the E8 case with `uninstall.sh --purge` and assert that nothing of S1 is left before "first install".
6. **minor — `systemd-analyze verify` of the repository's unit fails before the binary exists.** It reports `/usr/local/bin/vandox-agent` as not executable and exits non-zero. That happens in the CI "verify on 249" case, which runs before any install, and in `TestService_SystemdAnalyzeVerifyReportsNoUnknownKey` locally.
   - Instruction: in both, fail only on lines that contain `Unknown` or `ignoring` (or the specifier messages), not on the exit status. Or run the CI case after "first install". Also name the unit's install path (`/etc/systemd/system/vandox-agent.service`) in *Approach*, and have uninstall run `systemctl daemon-reload` and `reset-failed` after removing it.
7. **minor — under `sudo`, the CI script probably cannot find the Go from `setup-go`, and its build cache would land in `/root`.** The plan has `sudo .github/scripts/check-agent-install.sh` build the agent itself; `sudo`'s `secure_path` likely lacks the `setup-go` toolchain.
   - Instruction: build `vandox-agent` and write `SHA256SUMS` in an unprivileged workflow step (or a non-sudo part of the script). Pass the directory to the sudo part, which installs with `--from`.
8. **minor — `checkout_warning` silently does nothing in the common case.** It runs `git describe` as root. When the operator clones as a normal user and runs `sudo install.sh`, Ubuntu 22.04's Git (with the safe.directory fix) refuses the repository as "dubious ownership", so the warning of S3 is skipped.
   - Instruction: treat a failing `git describe` as "no tag", without output. Say in `deploy/agent/README.md` that the check needs a checkout owned by root (or `git config --global --add safe.directory`). Add a test row where the stub `git` exits 128.
