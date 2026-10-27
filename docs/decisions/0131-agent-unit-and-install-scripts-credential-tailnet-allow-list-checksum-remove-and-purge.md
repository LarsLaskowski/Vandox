# 0131: Agent unit and install scripts: token as a systemd credential, tailnet-only addresses, fixed resource limits with GOMEMLIMIT, checksum-verified binary, install bundle as a release asset, remove and purge

- **Status:** Proposed
- **Date:** 2026-10-27
- **Area:** Agent
- **Source:** Issue #42
- **Supersedes:** 0037 (amends it: a release also publishes the agent install bundle `vandox-agent-install-vX.Y.Z.tar.gz`, listed in `SHA256SUMS` beside the binary and attested for build provenance; 0037 is not edited)

## Context

v0.2.0 is the first release whose agent collects and sends, and nothing installs it yet. Records 0030 and 0122 fix the rights
(`CAP_SYS_PTRACE` alone, the groups `adm` and `systemd-journal`) and the confinement the unit must apply; records 0110, 0111, 0117
and 0120 add settings the collectors need. The issue asks for a hardened unit with resource limits, install and uninstall scripts,
and a checksum check of the downloaded binary. The target is Ubuntu 22.04 with systemd 249 on a 2 GB server (0025), which ignores
a unit key it does not know with only a log line, so a hardening setting can silently vanish. The cloud sessions have no systemd
as PID 1, and the release workflow (0037) published `vandox-agent-linux-amd64` with a one-line `SHA256SUMS`. On the pull request the
Product Manager refused the first delivery of the scripts (a Git checkout of the tag): a production server should not need `git` and a
whole repository to install one agent.

## Options considered

Token delivery:
1. **`VANDOX_AGENT_TOKEN_FILE` naming a file the agent's group reads** — the token sits readable for the agent's user at rest.
2. **`EnvironmentFile=`** — the token lands in the process environment, where `VANDOX_AGENT_TOKEN` would also be shown by tools.
3. **`LoadCredential=` from a root-only file, the path written out without `%d`** (chosen) — systemd hands the service a private
   copy for its lifetime; `%d` is avoided so the unit does not depend on when systemd learned the specifier.

Network:
4. **No address restriction** — the outbound-only, tailnet-only guarantee (0006, 0010) would rest on the configuration alone.
5. **`IPAddressDeny=any` with the tailnet ranges and localhost allowed** (chosen) — enforces the guarantee; a backend name resolved
   by an external name server fails and is documented.

Memory:
6. **`MemoryMax=` alone** — the Go runtime does not know the cgroup limit; with the default GC a heap can grow to about twice its
   live size and the kernel kills the agent for garbage.
7. **`MemoryMax=` with `GOGC` lowered** — a permanent CPU cost on every collection.
8. **`MemoryMax=256M` with `GOMEMLIMIT=192MiB`** (chosen) — the runtime collects harder only near the soft limit; the cgroup limit
   stays the hard stop.

System-call filter:
9. **`@system-service` minus `@privileged` and `@resources`** — `@privileged` holds `@clock` and so `adjtimex`, which the `clock`
   collector needs (0111), and `@resources` holds calls the Go runtime may make at start; re-adding them by name is fragile.
10. **`@system-service` plus `adjtimex`, minus the six calls of 0122** (chosen) — what the Agent area requires; a draft scored
    2.2 `OK` in `systemd-analyze security`.

Binary and scripts:
11. **Verifying the build provenance with `gh attestation verify` on the server** — needs `gh` and a GitHub login on the server.
12. **The SHA-256 from `SHA256SUMS` of the same release** (chosen, as the issue asks) — proves integrity, not authorship; the README
    shows the provenance check for operators who want it.

Uninstall:
13. **Uninstall removes everything** — destroys unsent records and forces a new token and MariaDB account on reinstall.
14. **Remove and purge, as Debian packages do** (chosen).

Verification against systemd 249:
15. **The extracted Ubuntu 22.04 systemd package in the session** — its library path and the proxy make it fragile; unmeasured.
16. **A CI job on GitHub's `ubuntu-22.04` runner** (chosen), which boots systemd 249, installs a locally built agent, and runs
    `systemd-analyze verify` and `security` there; Go tests in `deploy/agent` pin the unit and the scripts' functions locally.

Reading `spool.directory` from a kept configuration file:
17. **A new agent flag that prints the configuration** — changes the binary, which the feature does not.
18. **A small reader of the block form `spool:` / `directory:`, failing closed on every other form** (chosen), accepting only one
    directory directly below a root-owned `/var/lib/vandox`: root never creates or changes ownership along a path an
    agent-owned directory could redirect with a link (a deeper path would need a checked walk of every element).

Clear-text URLs in the CI check script (SonarQube Cloud `shell:S5332`, a vulnerability that fails the quality gate's security
rating; it follows a URL through variables and command output and exempts loopback):
19. **Suppressing the rule for the script** with `sonar.issue.ignore.multicriteria` in the scanner arguments, or `# NOSONAR` — the
    first hides every later clear-text finding in the file and changes the CI workflow; whether the shell analyzer honours the
    second is unmeasured.
20. **Building the URL from pieces so the rule no longer sees it** — evasion: the traffic would stay what it was.
21. **No clear-text URL in the script** (chosen) — the backend is unreachable in CI and only rendered and validated, so an
    `https://` URL, which the scripts and the agent accept alike, tests the same; the address allow-list check concerns the
    connection, not the protocol, so it takes address and port and opens a socket.

How the scripts, the unit and their files reach the server:
22. **A Git checkout of the tag** (the first build of this record) — needs `git` and the whole repository on a production server; a
    checkout on another tag could only be warned about.
23. **An install bundle as a release asset** (chosen by the Product Manager on PR #324) — one `tar.gz` with the two scripts,
    `common.sh`, the unit, both SQL files, the example `agent.yaml`, a `VERSION` file and the binary's SHA-256, built from the tagged
    tree by the release workflow and listed in the same `SHA256SUMS`; the server needs `curl`, `tar` and `sha256sum` only, and the version of the scripts and
    the binary is one value (`VERSION`) instead of an option that can disagree with the files.
24. **Each file as its own release asset**, fetched and checked by `install.sh` — more downloads and more code in the one script that
    runs as root, and the first download (`install.sh` itself) is still checked by hand.
25. **The files embedded in the agent binary** (`vandox-agent install`) — one download, but it changes the binary and adds a root
    code path to it, which this feature does not.
26. **The binary inside the bundle** — a second copy of a 64 MiB-bounded file per release, and the bundle would carry an artifact that
    is attested on its own; the script keeps downloading the binary of the bundle's version.
27. **A `curl … | bash` one-liner** — runs bytes from the network before any check; never documented.
28. **`install.sh` checks its own checksum** — a script cannot vouch for the files it is part of; the check belongs before extraction.
29. **`install.sh` refuses a directory or file of the bundle that is a link, not owned by root or group- or other-writable** (chosen,
    Security review) — a cheap backstop when a bundle is extracted somewhere shared; it cannot help once `common.sh`, sourced before
    the check, or the script itself was changed, so the documented extraction stays the control.

Which checksum `install.sh` checks the binary against:
30. **The release's `SHA256SUMS`, downloaded at install time** — an operator who verifies the bundle's attestation still installs a
    binary pinned only by a second, unattested download, and `install.sh` must parse a file of several lines.
31. **The binary's SHA-256 inside the bundle** (`vandox-agent-linux-amd64.sha256`, chosen, Security review) — verifying the bundle,
    by `SHA256SUMS` or by its attestation, covers the binary too; `install.sh` keeps its one-line rule and downloads only the binary.

Documented commands:
32. **Download, check, extract and run as separate lines** — pasted line by line, a failed check does not stop `tar` and `install.sh`.
33. **One command chained with `&&`, as root in a new `mktemp -d` directory** (chosen, Security review) — a failed check stops it, and
    no other local user can have created the extraction directory; the bundle's line is selected by an exact string comparison, not a
    pattern, and `tar` extracts with root as owner and root's umask (`--no-same-owner --no-same-permissions`).

## Decision

The unit and the scripts are built as in options 3, 5, 8, 10, 12, 14, 16, 18, 21, 23, 29, 31 and 33; the settings, values, paths and messages are
in the [Agent](../areas/agent.md) area (*Installation*, *Rights*, *Service manager*) and in `deploy/agent/README.md`.

## Consequences

- The agent can reach only tailnet addresses and localhost; a `backend.url` elsewhere fails as `connection failed`.
- `MemoryMax=256M`: the documented worst cases (process run 48 MiB, MariaDB 32 MiB, logs 24 MiB, kernel 4 MiB, sender one 1 MiB
  batch per stream) sum to about 112 MiB if all peak at once under pressure; `GOMEMLIMIT=192MiB` leaves 64 MiB for page cache and
  runtime overhead. `CPUQuota=50%` bounds a runaway loop to half a core; the collectors' timeouts are at most 10 s. `Nice=5` and
  `IOWeight=50` yield to the services without starving the agent while an outage builds. `WatchdogSec=2min` pings every minute while
  the runners are on time, so a short stall under pressure does not restart the agent. `RestartSec=30s` with
  `StartLimitIntervalSec=0` retries forever, at most twice a minute.
- The resource values rest on the documented per-collector bounds, not on a measurement on the server; the #45 parallel run
  checks the agent's peak RSS against them.
- `SHA256SUMS` does not prove who built the binary or the bundle; `gh attestation verify` of the bundle covers the binary too, through
  the SHA-256 the bundle carries.
- `SHA256SUMS` holds one line per asset (bundle and binary) and serves the operator's check of the bundle and anyone who downloads the
  binary by hand; `install.sh` no longer reads it.
- The operator's check of the bundle before extraction is the trust boundary for the scripts, the unit and the binary: every document
  shows it in one `&&` chain with `tar` and `install.sh`, as root in a new private directory, and never a pipe into a shell.
- `install.sh` takes its version only from the bundle's `VERSION`, so the scripts, the unit and the binary come from one release; the
  `--version` option and the checkout warning are gone, and a run from a source checkout (no `VERSION`) is refused. A developer builds
  a bundle with the release's packaging script.
- The release packages only tags `install.sh` accepts (`vX.Y.Z`, `vX.Y.Z-rc.N`): a release with another pre-release form fails instead of
  publishing a bundle that cannot install.
- The bundle is byte-for-byte reproducible from the tagged tree with the same `tar` and `gzip` (fixed order, owner, modes and time), which
  the release checks by packaging twice; it is not reproducible across `gzip` versions.
- `--from DIR` takes only the binary, for offline installs and CI; it is checked against the bundle's SHA-256 like a download, and the
  bundle is checked and extracted the same way as online.
- The CI job depends on GitHub keeping an `ubuntu-22.04` runner; it fails when the runner's systemd is not 249.
- A kept configuration whose `spool` block is written in another YAML form is refused by the scripts, though the agent reads it.
- The CI job installs from the bundle it packages exactly as the release does, so the documented path is the tested one; it does not
  exercise an `http://` backend URL; the Go tests of the scripts' functions cover both schemes.
