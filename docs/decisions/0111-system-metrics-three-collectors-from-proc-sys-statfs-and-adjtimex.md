# 0111: System metrics: three collectors read /proc, /sys, statfs and adjtimex, with no program, D-Bus or socket

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Agent system metrics
- **Source:** Issue #31
- **Supersedes:** —

## Context

Issue #31 asks for the system metrics of the monitored server: CPU per state, load, memory and swap, pressure stall
information (PSI, the earliest sign of the memory exhaustion Vandox is built to explain), swap and fault rates, disk use and
inodes per configured mount point, I/O throughput and the clock synchronization, "all read from `/proc` and `/sys`", with no
external program. The runtime frame (0110) runs each collector under its own deadline and turns every failed or abandoned run
into a gap (0028, 0029). The backend already reads these values under fixed names (the legacy monitoring log parser writes
them, the incident charts read `mem_available_bytes`, `swap_used_bytes`, `pressure_avg10` and `load1`), and #45 retires the
legacy script only when the agent shows at least the same information.

Two of the requested values are not in `/proc` or `/sys` at all: the use of a file system comes from `statfs(2)`, and the
kernel's view of the clock synchronization from `adjtimex(2)`. Both are read-only system calls, not programs. How the clock
values are interpreted is decided in [0138](0138-clock-synchronized-below-16-seconds-of-maximum-error-pll-offset-only-while-the-pll-is-on.md),
how failed and absent measurements are recorded in
[0139](0139-a-broken-system-file-fails-the-sample-a-measurement-the-host-does-not-offer-is-omitted.md).

## Options considered

1. **Collector split.** One collector for everything — a `statfs` that hangs on a stale network mount would turn every sample
   into a gap, PSI included; one collector per file — a dozen runners for values that come from the same procfs and fail
   together; **three collectors by source: `system` (`/proc` and `/sys`), `filesystem` (mount table and `statfs`), `clock`
   (`adjtimex`)** (chosen), so a hung `statfs` loses only the file system sample.
2. **Disk use and clock sources.** `df`, `timedatectl`, `chronyc` — external programs, against 0019 and the issue; the D-Bus
   services `timedate1`/`timesync1` — a D-Bus client and one daemon only; chrony's command port on `127.0.0.1:323` — a new
   destination of the agent, against the outbound rule of 0006 and security area 11; **the read-only system calls `statfs`
   and `adjtimex`** (chosen), which work for every time daemon and need no right.
3. **`/proc/meminfo`.** A fixed subset of fields — less than the legacy script recorded, against #45; **every field, bounded**
   (chosen), as the legacy parser stores them.
4. **Disks for I/O throughput.** A configured device list — one more option for a value the kernel already knows; every
   line of `/proc/diskstats` — loop, RAM, zram and device-mapper devices double-count or add noise; **the entries of
   `/sys/block` that have a driver behind them** (chosen).
5. **Rates.** Left to the backend — every consumer would recompute them and the first sample after a restart would still lack
   one; **computed by the agent from two consecutive samples, like the legacy parser's `vmstat_per_second`** (chosen), with
   the raw counters kept beside them so a rate can be rebuilt.
6. **Proof of "no program, D-Bus or socket".** A test that forbids known program and network packages — it misses raw
   system calls, cgo, `unsafe`, `//go:linkname` and modules such as `golang.org/x/sys/unix`; **a test that allows only a
   fixed set of packages and of `syscall` and `os` functions** (chosen), so a new way out of the package fails the test.

## Decision

The agent collects the system metrics with three collectors — `system` (`/proc`, `/sys`), `filesystem` (the mount table and
`statfs`) and `clock` (`adjtimex`) — under the names the backend already uses, with no external program, D-Bus or socket. The
rules are in [Agent system metrics](../areas/agent-system-metrics.md) (*System metrics*), the names in
[Wire format records](../areas/wire-format-records.md) (*Metric names*) and the option in
[Configuration options](../areas/configuration-options.md).

## Consequences

- The issue's "all values from `/proc` and `/sys`" holds for every value except the file system use (`statfs`) and the clock
  (`adjtimex`); the requirement that matters, no external program, holds for all of them.
- The systemd unit must allow `adjtimex`: `ProtectClock=yes` denies the whole `@clock` group with `EPERM`, also for a read, and
  `@system-service` does not contain it (systemd v249 `seccomp-util.c`, `execute.c`). Without it the `clock` collector fails
  every run and records a gap. Allowing the call grants nothing: a setting change needs `CAP_SYS_TIME`, which the agent's
  bounding set excludes (measured in the plan review).
- The file system values are those of the agent's mount namespace, so a path the unit hides or replaces reports what the
  agent sees.
- About 120 metric records every 30 seconds (every 10 seconds in memory pressure mode) plus 7 per mount point a minute: the
  spool is sized for this.
- The fixtures are a capture of kernel 6.18 (the development host); the server runs Ubuntu 22.04's 5.15. The grammars the
  parsers rely on are the same in both, but no 5.15 capture was compared; the parallel run of #45 is the check.
- Revisit if the record volume strains the spool (then a slower interval for `/proc/meminfo`).
