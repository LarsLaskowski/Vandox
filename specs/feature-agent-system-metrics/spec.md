# Spec: System metrics collector including PSI

Status: Approved by Lead

Source: Issue #31 (depends on #30, merged).

## Problem / motivation

`vandox-agent` replaces the legacy `top`/`lsof` script on the monitored server (Ubuntu 22.04, Plesk 18, 2 GB RAM). The frame of
#30 runs collectors under deadlines, but the only collector today records the agent's own resource use. The analysis needs the
server's system metrics: CPU per state, load, memory and swap, pressure stall information (PSI, the earliest sign of the memory
exhaustion that precedes the OOM killer), swap and fault rates, disk use and inodes per configured mount point, I/O throughput and
the clock synchronization — without starting any program, and with the same metric names the backend already reads from the legacy
log (`mem_available_bytes`, `swap_used_bytes`, `pressure_avg10`, `load1`, ...), so #45 can compare agent and script.

## Behavior

- B1: The agent runs three more collectors next to the self-metrics: `system` every 30 s (10 s in memory pressure mode, timeout 5 s),
  `filesystem` every 60 s (timeout 10 s, only when a mount point is configured) and `clock` every 60 s (timeout 5 s).
- B2: Every `system` sample after the first records `cpu_percent` per mode (user, system, nice, idle, iowait, irq, softirq, steal)
  from the increase of the `/proc/stat` counters since the previous sample.
- B3: Every `system` sample records `load1`, `load5` and `load15` from `/proc/loadavg`.
- B4: Every `system` sample records memory and swap as Ubuntu 22.04's `top` prints them (`mem_*_bytes`, `swap_*_bytes`) and every line
  of `/proc/meminfo` (`meminfo_bytes` / `meminfo`, label `field`).
- B5: Every `system` sample records the PSI values of `/proc/pressure/{memory,cpu,io}` (`pressure_avg10/60/300`,
  `pressure_stall_microseconds_total`, labels `resource` and `kind`), including a `cpu` file without `full` line.
- B6: When the kernel offers no PSI for a resource (file missing, or "operation not supported"), the sample succeeds without that
  resource's values; the agent logs one warning naming the unavailable resources when that set changes, and one info line when PSI
  is available again.
- B7: Every `system` sample records `vmstat_total` for `pswpin`, `pswpout`, `pgmajfault` and `oom_kill`, and from the second sample on
  `vmstat_per_second`.
- B8: Every `system` sample records the I/O counters of each disk (an entry of `/sys/block` with a `device` entry, at most 16) and, from
  the second sample on, their rates and the busy share.
- B9: Every `filesystem` sample records size, used, free and available bytes and the inode counts of each configured mount point that is
  mounted in the agent's mount table; a path that is not mounted is left out and logged once (warning), and logged again (info) when it
  is mounted again.
- B10: Every `clock` sample records whether the clock is synchronized (maximum error below 16 s), the kernel's maximum and estimated
  error, and the kernel PLL's remaining offset only while the PLL is on.
- B11: A source file that cannot be read, exceeds its size bound or breaks its grammar fails the sample of its collector, which becomes a
  gap; the error names the file, the line and a fixed reason, never file content or a configured path.
- B12: A source that hangs costs only its own collector's sample: the run is abandoned, a gap is recorded, the collector is not started
  again while stuck, and the other collectors keep recording.
- B13: No collector starts a program, uses D-Bus or opens a socket.
- B14: The option `filesystem.mount_points` (a list of at most 16 absolute, clean, distinct paths of at most 1,024 bytes; default `/`)
  configures the mount points; any other value, a list element with no value included, stops the agent at start-up with a fixed error
  that never contains the value.
- B15: A measurement the server does not offer (PSI, an unmounted path, inode counts of a file system without them, the PLL offset while
  the PLL is off, a rate without predecessor) is left out of the sample — never written as 0 and never a gap.
- B16: Every record is a `metric` with source `system`, the names, units and labels of the wire format's *Metric names*, and passes the
  wire check.

## Acceptance criteria

- [ ] The values come from `/proc`, `/sys`, `statfs` and `adjtimex` only; no external program (issue: "all values read from `/proc`
  and `/sys`" — refuted for disk use and the clock, see plan).
- [ ] Tests run against frozen sample trees under `testdata/proc/` and `testdata/sys/` (real captures) plus in-memory variants.
- [ ] Missing PSI is handled gracefully (no failed sample) and reported once.
- [ ] Every behavior above is covered by the criteria of [plan.md](plan.md).

## Out of scope

- Switching memory pressure mode (the thresholds on PSI and `MemAvailable` belong to #32, whose requirement names them; the `system`
  collector already has a pressure interval).
- The batch header's `clock_offset_ns` (the spool and sender, #38/#39).
- The clock offset under chrony (would need chrony's command protocol, a new destination; record 0111).
- `/proc/pressure/irq`, per-CPU values, network interface counters.
- The systemd unit (#42), which must allow `adjtimex` (record 0111).

## Open questions

None for the Product Manager; the reading of the issue's "all from `/proc` and `/sys`" is decided in record 0111.
