# 0111: System metrics: three collectors read /proc, /sys, statfs and adjtimex; missing pressure data is logged once and never recorded as zero

- **Status:** Proposed
- **Date:** 2026-10-17
- **Area:** Agent
- **Source:** Issue #31
- **Supersedes:** —

## Context

Issue #31 asks for the system metrics of the monitored server: CPU per state, load, memory and swap, pressure stall
information (PSI, the earliest sign of the memory exhaustion Vandox is built to explain), swap and fault rates, disk use and
inodes per configured mount point, I/O throughput and the clock synchronization, "all read from `/proc` and `/sys`", with no
external program, and missing PSI "handled gracefully and reported once". The runtime frame of #30 (0110) runs each collector
under its own deadline and turns every failed or abandoned run into a gap (0028, 0029). The backend already reads these
values under fixed names (the legacy monitoring log parser writes them, the incident charts read `mem_available_bytes`,
`swap_used_bytes`, `pressure_avg10` and `load1`), and #45 retires the legacy script only when the agent shows at least the
same information.

Two of the requested values are not in `/proc` or `/sys` at all: the use of a file system comes from `statfs(2)`, and the
kernel's view of the clock synchronization from `adjtimex(2)`. Both are read-only system calls, not programs.

## Options considered

1. **Collector split.** One collector for everything — a `statfs` that hangs on a stale network mount would turn every sample
   into a gap, PSI included; one collector per file — a dozen runners for values that come from the same procfs and fail
   together; **three collectors by source: `system` (`/proc` and `/sys`), `filesystem` (mount table and `statfs`), `clock`
   (`adjtimex`)** (chosen), so a hung `statfs` loses only the file system sample.
2. **Disk use and clock sources.** `df`, `timedatectl`, `chronyc` — external programs, against 0019 and the issue; the D-Bus
   services `timedate1`/`timesync1` — a D-Bus client and one daemon only; chrony's command port on `127.0.0.1:323` — a new
   destination of the agent, against the outbound rule of 0006 and security area 11; **the read-only system calls `statfs`
   and `adjtimex`** (chosen), which work for every time daemon and need no right.
3. **"Synchronized".** The kernel's `STA_UNSYNC` flag — chrony 4.2 on Linux keeps it set unless `rtcsync` is configured (read
   in chrony's `sys_timex.c`), so a synchronized chrony host would read as unsynchronized; **the maximum error below 16
   seconds** (chosen), the rule `timedatectl` itself uses (systemd v249 `timedated.c`, `ntp_synced`), which both
   systemd-timesyncd and chrony keep up to date.
4. **Clock offset.** The daemon's measured offset — only through D-Bus or chrony's port (option 2); the kernel's PLL offset
   always — chrony switches the PLL off and resets its offset to 0 (chrony 4.2 `initialise_timex`), so the value would be a
   false zero; **the kernel's PLL offset only while the PLL is on** (chosen): systemd-timesyncd, Ubuntu's default, hands every
   slewed offset to the PLL (v249 `manager_adjust_clock`); under chrony the offset is omitted and the error estimates remain.
5. **`/proc/meminfo`.** A fixed subset of fields — less than the legacy script recorded, against #45; **every field, bounded
   to 128 lines** (chosen), as the legacy parser stores them.
6. **Whether a configured path is mounted.** `statfs` on the path alone — an unmounted mount point silently reports the file
   system below it; comparing the device of the path with its parent's — wrong for bind mounts; **the mount table of the
   agent's own mount namespace** (chosen): a path that is not a mount point there is reported once and its values are omitted.
   The host's namespace through `/proc/1/root` would also see paths the unit hides, but needs `CAP_SYS_PTRACE` for a
   collector that has no other use for it.
7. **Disks for I/O throughput.** A configured device list — one more option for a value the kernel already knows; every
   line of `/proc/diskstats` — loop, RAM, zram and device-mapper devices double-count or add noise; **the entries of
   `/sys/block` that have a `device` link, at most 16** (chosen): the disks with a driver behind them.
8. **Rates.** Left to the backend — every consumer would recompute them and the first sample after a restart would still lack
   one; **computed by the agent from two consecutive samples, like the legacy parser's `vmstat_per_second`** (chosen), with
   the raw counters kept beside them so a rate can be rebuilt.
9. **Failure inside the `system` sample.** Partial records with the failed part left out — the frame cannot record a partial
   loss, so the missing part would look normal (0028); **a file that cannot be read or breaks its grammar fails the whole
   sample, which becomes a gap** (chosen); the only exceptions are the absences the kernel defines: PSI that the kernel does
   not offer, and a pressure file without a `full` line.
10. **Missing PSI.** Fail the sample — memory data lost on every PSI-less kernel; a gap per sample — a gap means a missed
    sample, and the log would repeat; **omit the pressure metrics and log one warning when the set of unavailable resources
    changes** (chosen); the backend reads the absence as unknown, as it does for every omitted measurement.
11. **A null element in `filesystem.mount_points`.** Dropped, as the YAML library does on its own — `[~]` would become an
    empty list and silently switch the file system metrics off; a check after decoding that the list kept every element —
    unreachable with the pinned library, whose only silent drop is a null element; **refused before decoding as "has no
    value"** (chosen), for every list option of the agent's configuration.
12. **Proof of "no program, D-Bus or socket".** A test that forbids known program and network packages — it misses raw
    system calls, cgo, `unsafe`, `//go:linkname` and modules such as `golang.org/x/sys/unix`; **a test that allows only a
    fixed set of packages and of `syscall` and `os` functions** (chosen), so a new way out of the package fails the test.

## Decision

The agent collects the system metrics with three collectors — `system` (`/proc`, `/sys`), `filesystem` (the mount table and
`statfs`) and `clock` (`adjtimex`) — under the names the backend already uses, with no external program, D-Bus or socket.
Synchronized means a maximum error below 16 seconds, the offset is the kernel PLL's and only while the PLL is on, and a
measurement the host does not offer (PSI, a configured path that is not mounted, the PLL offset under chrony) is omitted,
logged once and never recorded as zero or as a gap. The rules are in the [Agent](../areas/agent.md) area (*System metrics*),
the names in the [wire format](../areas/wire-format.md) (*Metric names*) and the option in
[Configuration and secrets](../areas/configuration-and-secrets.md).

## Consequences

- The issue's "all values from `/proc` and `/sys`" holds for every value except the file system use (`statfs`) and the clock
  (`adjtimex`); the requirement that matters, no external program, holds for all of them.
- The systemd unit (#42) must allow `adjtimex`: `ProtectClock=yes` denies the whole `@clock` group with `EPERM`, also for a
  read, and `@system-service` does not contain it (systemd v249 `seccomp-util.c`, `execute.c`), so an allow-list needs
  `adjtimex` named. Without it the `clock` collector fails every run and records a gap. Allowing the call grants nothing: a
  setting change needs `CAP_SYS_TIME`, which the agent's bounding set excludes (measured in the plan review: unprivileged and
  without capabilities, `adjtimex` with modes 0 succeeds and changes nothing, `ADJ_MAXERROR` fails with `EPERM`, and `statfs`
  works).
- The file system values are those of the agent's mount namespace: a path the unit hides or replaces (`ProtectHome=` for
  `/home`, `InaccessiblePaths=`) reports what the agent sees. A mount point unmounted between the mount table read and
  `statfs` reports the file system below it for one sample.
- About 120 metric records every 30 seconds (every 10 seconds in memory pressure mode, which #32 switches) plus 7 per mount
  point a minute: the spool (#38) is sized for this.
- The fixtures are a capture of kernel 6.18 (the development host); the server runs Ubuntu 22.04's 5.15. The grammars the
  parsers rely on are the same in both, but no 5.15 capture was compared; the parallel run of #45 is the check.
- The PLL offset is recorded with the sign the kernel reports for the correction still to be applied; what that sign means
  relative to the reference clock is not measured (no time daemon on the development host) and is not interpreted here. The batch header's `clock_offset_ns` (0046) stays the spool's and the sender's (#38, #39).
- Revisit if the clock offset under chrony is needed (then a reader of chrony's protocol needs its own record, against 0006),
  or if the record volume strains the spool (then a slower interval for `/proc/meminfo`).
