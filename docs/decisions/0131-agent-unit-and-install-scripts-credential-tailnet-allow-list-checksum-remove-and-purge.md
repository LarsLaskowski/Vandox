# 0131: Agent unit and install scripts: token as a systemd credential, tailnet-only addresses, fixed resource limits with GOMEMLIMIT, checksum-verified binary, remove and purge

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent
- **Source:** Issue #42
- **Supersedes:** —

## Context

v0.2.0 is the first release whose agent collects and sends, and nothing installs it yet. Records 0030 and 0122 fix the rights
(`CAP_SYS_PTRACE` alone, the groups `adm` and `systemd-journal`) and the confinement the unit must apply; records 0110, 0111, 0117
and 0120 add settings the collectors need. The issue asks for a hardened unit with resource limits, install and uninstall scripts,
and a checksum check of the downloaded binary. The target is Ubuntu 22.04 with systemd 249 on a 2 GB server (0025), which ignores
a unit key it does not know with only a log line, so a hardening setting can silently vanish. The cloud sessions have no systemd
as PID 1, and the release workflow (0037) publishes `vandox-agent-linux-amd64` with a one-line `SHA256SUMS`.

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
12. **The SHA-256 from `SHA256SUMS` of the same release, the scripts from the tagged source** (chosen, as the issue asks) — proves
    integrity, not authorship; the README shows the provenance check for operators who want it.

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

## Decision

The unit and the scripts are built as in options 3, 5, 8, 10, 12, 14, 16 and 18; the settings, values, paths and messages are
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
- `SHA256SUMS` does not prove who built the binary.
- The CI job depends on GitHub keeping an `ubuntu-22.04` runner; it fails when the runner's systemd is not 249.
- A kept configuration whose `spool` block is written in another YAML form is refused by the scripts, though the agent reads it.
