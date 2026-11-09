# Agent system metrics

## Scope

The agent's collectors for the state of the monitored server — `system`, `filesystem` and `clock`, which record `metric` records, and
`processes`, which records the process snapshot that replaces the legacy `top` script — with what they read, how they parse it, what they
record and how they fail. The analysis and the backend rely on them; the scheduling, gaps and the memory pressure mode they run under are in
[Agent](agent.md).

## System metrics

Three collectors record the server's state as `metric` records with source `system`, through the scheduler like every collector. The
names, units and labels are in the [wire format records](wire-format-records.md) (*Metric names*). They start no program, use no D-Bus
and open no socket: they read `/proc` and `/sys` and make two read-only system calls, `statfs` for a file system's use and `adjtimex`
for the kernel's clock discipline. Every number they parse is checked as 1 to 20 ASCII digits before it is converted.

| Collector | Interval | Under memory pressure | Timeout | Reads |
| --------- | -------- | --------------------- | ------- | ----- |
| `system` | 30 s | 10 s | 5 s | `/proc/stat`, `/proc/loadavg`, `/proc/meminfo`, `/proc/vmstat`, `/proc/pressure/{memory,cpu,io}`, `/sys/block` |
| `filesystem` | 60 s | — | 10 s | `/proc/self/mountinfo`, `statfs` of each configured mount point |
| `clock` | 60 s | — | 5 s | `adjtimex` without changing anything |

`filesystem` runs only when at least one mount point is configured (`filesystem.mount_points`, default `/`, at most 16;
[Configuration options](configuration-options.md)). Each collector's failure is its own gap, so a `statfs` that hangs on a stale mount
loses only the file system sample. The `system` sample also decides the memory pressure mode ([Agent](agent.md), *Memory pressure mode*).

### The `system` sample

Records in this order: `cpu_percent` per mode (from the second sample on), `load1`, `load5`, `load15`, the memory and swap values as
`top` prints them, one `meminfo_bytes` or `meminfo` record per line of `/proc/meminfo` in file order, the pressure values per resource
(`memory`, `cpu`, `io`) and kind (`some`, then `full`), the four `vmstat_total` counters (`pswpin`, `pswpout`, `pgmajfault`,
`oom_kill`), their four `vmstat_per_second` rates (from the second sample on), then per disk in name order its five counters and, from
the second sample on, its five rates.

- **CPU.** Only the first 4 KiB of `/proc/stat` are read; a first line longer fails the sample. The first line is `cpu`, one or more
  spaces and at least 8 counters (user, nice, system, idle, iowait, irq, softirq, steal; later fields are ignored). `cpu_percent` of a
  mode is its increase since the previous sample divided by the increase of the sum of the 8 counters, times 100.
- **Load.** At most 256 bytes of `/proc/loadavg`; its first three fields, each 1 to 10 ASCII digits, a point and 2 digits.
- **Memory.** At most 16 KiB and 128 lines of `/proc/meminfo`. Every line is a name of 1 to 64 characters of `A-Z a-z 0-9 _ ( )`, a
  colon, one or more spaces and 1 to 20 ASCII digits within 64 bits, optionally followed by ` kB` (the value times 1,024 must fit in 64
  bits). A `kB` line is `meminfo_bytes` in bytes, a line without unit `meminfo`, label `field` the name. A field named twice fails the
  sample. MemTotal, MemFree, MemAvailable, Buffers, Cached, SReclaimable, SwapTotal and SwapFree must each be present as a `kB` line. The
  derived values follow procps 3.3.17 (Ubuntu 22.04's `top`): `mem_total_bytes` = MemTotal, `mem_free_bytes` = MemFree,
  `mem_buff_cache_bytes` = Buffers + Cached + SReclaimable, `mem_used_bytes` = MemTotal − MemFree − Buffers − Cached − SReclaimable
  (MemTotal − MemFree when that is negative; left out when MemFree is the larger), `mem_available_bytes` = MemAvailable;
  `swap_total_bytes` = SwapTotal, `swap_free_bytes` = SwapFree, `swap_used_bytes` = SwapTotal − SwapFree (left out when SwapFree is the
  larger).
- **Fault and swap counters.** At most 64 KiB of `/proc/vmstat`. The lines of `pswpin`, `pswpout`, `pgmajfault` and `oom_kill` are the
  name, one space and 1 to 20 ASCII digits; each must be present once. Other lines are not read.
- **Pressure.** At most 256 bytes per file of `/proc/pressure`. A file holds a `some` line and optionally a `full` line, each `some` or
  `full`, then ` avg10=A avg60=A avg300=A total=T` with single spaces, where A is 1 to 3 ASCII digits, a point and 2 digits, at most
  100.00, and T 1 to 20 ASCII digits within 64 bits; anything else fails the sample. A file without `full` line (the `cpu` file before
  kernel 5.13) gives only the `some` values.
- **Pressure not offered.** When a pressure file does not exist (a kernel without PSI, or booted with `psi=0`) or its read answers
  "operation not supported", that resource's values are left out and the sample succeeds. One warning `pressure stall information unavailable` with `resources` (the unavailable resources, comma-separated, in the order `memory`, `cpu`, `io`) is logged when that set
  changes and is not empty, one info line `pressure stall information available` when it becomes empty again; an unchanged set logs
  nothing.
- **Disks.** A disk is an entry of `/sys/block` that has a `device` entry (so loop, RAM, zram, device-mapper and RAID devices are left
  out), at most 16, the first in name order. Its `stat` file (at most 1 KiB read) holds at least 11 fields of 1 to 20 ASCII digits
  separated by spaces; reads and writes completed (fields 1 and 5), sectors read and written (fields 3 and 7, units of 512 bytes) and the
  milliseconds with I/O in flight (field 10) become `disk_reads_total`, `disk_read_bytes_total`, `disk_writes_total`,
  `disk_written_bytes_total` and `disk_io_time_seconds_total`, label `device` the entry's name. The I/O of a `dm-*`, `md*` or `loop*`
  volume is recorded only as the I/O of its member disks; a comparison with the legacy script (#45) compares per disk.
- **Rates.** A rate (`cpu_percent`, `vmstat_per_second`, the five disk rates) is the increase since the previous successful sample of the
  same collector divided by the seconds between the two runs' start times (`disk_busy_percent`: the increase of the I/O milliseconds
  divided by those milliseconds, times 100). There is no rate in the first sample after the agent started, for a counter that fell, for a
  disk or counter the previous sample lacked, and when the start times did not move forward (the wall clock was set back). A failed sample
  is no predecessor.

### The `filesystem` sample

Per configured mount point in configuration order: `filesystem_size_bytes`, `filesystem_used_bytes`, `filesystem_free_bytes`,
`filesystem_available_bytes`, then `filesystem_inodes`, `filesystem_inodes_used` and `filesystem_inodes_free`, label `mount` the
configured path.

- A path counts as mounted when it is the mount point (field 5) of a line of the agent's own `/proc/self/mountinfo` after the kernel's
  escapes are decoded: a backslash and exactly three octal digits is one byte (`\040` a space, `\011` a tab, `\012` a line feed, `\134` a
  backslash); the other fields are not decoded. The table is read line by line, so its length does not bound memory. A line with fewer
  than 5 fields, a backslash in its mount point that does not start such an escape, or a line longer than 64 KiB fails the sample — for
  as long as such a mount exists, each sample is a recorded gap, never a silent omission.
- A path that is not mounted is left out and `statfs` is not called for it. One warning `mount point not mounted` with `mount` is logged
  when the path stops being mounted (or is not mounted at the first sample), one info line `mount point mounted` when it is mounted again.
- Size, free and available are the block counts times the fragment size (the block size when the fragment size is 0); used is size minus
  free. The inode values are left out when the file system reports 0 inodes (btrfs). A `statfs` error, free or available blocks above the
  total, free inodes above the total, or a size above 64 bits fail the sample.
- The values are those of the agent's mount namespace: a path the unit hides or replaces (`ProtectHome=` for `/home`,
  `InaccessiblePaths=`) reports what the agent sees, and a mount point unmounted between the read of the table and `statfs` reports the
  file system below it for that one sample.

### The `clock` sample

`clock_synchronized`, `clock_max_error_seconds`, `clock_estimated_error_seconds` and, only while the kernel's phase-locked loop is on,
`clock_pll_offset_seconds`.

- `clock_synchronized` is 1 when the kernel's maximum error is below 16 seconds, else 0 (the rule `timedatectl` uses, valid for
  systemd-timesyncd and chrony; the kernel's "unsynchronized" flag is not used).
- The PLL offset is the correction the kernel still has to apply (nanoseconds when the kernel reports nanoseconds, else microseconds,
  converted to seconds). Under chrony, which switches the PLL off, it is left out, never written as 0. A negative error estimate or a
  failed call fails the sample.

### Absent values and errors

**Absent is not zero.** A measurement the server does not offer — pressure values, a path that is not mounted, inode values of a file
system without them, the PLL offset while the PLL is off, a rate without predecessor — is left out. It is no missed sample and records no
gap; the backend reads it as unknown, never as 0.

A failed `system`, `filesystem` or `clock` sample becomes a gap with cause `unknown` and is logged by the scheduler ([Agent](agent.md),
*Scheduling*). Error texts name the file (`meminfo`, `pressure/io`, `block/sda/stat`, `self/mountinfo`), the line number where there is
one and a fixed reason, and a mount point by its index (`filesystem.mount_points[1]`); they never contain file content or a configured
path.

## Processes

The collector `processes` (interval 60 seconds, pressure interval 15 seconds, timeout 10 seconds, after `system` in the job list) records
one `process_snapshot` record per run with source `processes` and the capture time of the run's start, in the model the legacy log parser
writes ([wire format records](wire-format-records.md); the legacy naming in
[Legacy monitoring log parser](log-parsers-legacy-monitoring.md)). It reads `/proc/<pid>/stat`, `status`, `cmdline` and `smaps_rollup` of
every process, `/proc/stat` for the boot time and `/etc/passwd` for user names; it starts no program, opens no socket and makes no raw
system call.

### What a snapshot holds

- **Programs** are summed over **every process read**, not only the listed ones, so their `count`s add up to `total` (less the processes
  left out below): `count`, `cpu_percent` (sum), `rss_bytes` (sum, saturating at the largest 64-bit value), `pss_bytes` (sum, saturating,
  present only when **every** member has a PSS) and `rss_overcounted` (exactly when `count` is above 1). RSS counts a shared page in every
  process that maps it, PSS divides it; kernel memory and unmapped page cache are in neither.
- **Listed processes.** Without pressure mode the union of the top 25 by memory and the top 25 by `cpu_percent` (ties by PID, lowest
  first). Under pressure mode (the mode at the run's start) and in an extra run requested for an OOM kill ([Agent kernel events and
  boots](agent-kernel-events.md), *Extra snapshots*) **every process read**; the extra run's record names the kills in `trigger`.
  `total` is the number of processes seen (the PID directories, less those that ended during the read); `complete` is true only when
  every process was read and listed and no entry was left out for size or validity.
- **One memory key per run:** the PSS when every process read has one, else the RSS for all. Processes and programs are ordered by that
  key descending, then processes by PID and programs by name (ordinal). Without `CAP_SYS_PTRACE` the key is the RSS, as `top` ranks.
- **Per process:** `pid`, `ppid`, `user` (the name of the **effective** UID from `/etc/passwd`, else the UID in decimal), `command`,
  `cmdline` (at most 1,024 bytes; `truncated` when more was there), `state` (the `stat` state when it is one ASCII letter, else left
  out), `started_at` (boot time plus `starttime` in clock ticks of 10 ms, UTC), `cpu_percent`, `rss_bytes`, `pss_bytes` and `swap_bytes`
  (`VmSwap`, left out when absent). `oom_score_adj` is not read. A process without address space (a kernel thread, a zombie) has
  `rss_bytes` 0 and `pss_bytes` 0; a PSS that cannot be read is left out, never 0.
- **CPU.** With a predecessor (the same PID with the same `starttime` in the previous successful run, runs moving forward, counters not
  fallen) `cpu_percent` is the CPU time used between the two runs' starts over the time between them; without one (first run, new or
  reused PID, fallen counters, the clock set back) it is the average since the process started, 0 when it has no age. Clock ticks are
  the fixed 100 per second (`USER_HZ` on x86-64 and arm64). A failed run is no predecessor.

### Names

The `command` is the legacy derivation, pinned by the fixture `testdata/processes/names.json`, which the agent and the backend's legacy
parser both read: the text inside `[...]` when the whole command line is bracketed, else the part after the last `/` of the first
space-separated word without one trailing `:`; when that is empty the command line; then the cleaned `comm`; then the fixed text
`(unnamed)`, so a `command` is never empty. The default program is the first space-separated word of the `command`. The configured
rules (`processes.programs`, [Configuration options](configuration-options.md)) are tried first, in order; the first match gives the
rule's `program`, with `append_next_word` followed by `/` and the word after the prefix in the cleaned command line (up to the next
space, cut so the program has at most 1,024 bytes at a character boundary; an empty word leaves the program). The default rule list is
empty, so the names match the legacy log. The titles `php-fpm: pool ...`, `spamd` and `amavisd` are the forms those programs document,
not captured from a Plesk server; the parallel run of #45 checks them.

### Readers (security area 10)

Every read is bounded before it is parsed; every number is 1 to 20 ASCII digits parsed into its target width.

- `stat`: at most 4 KiB, read whole. The PID, a space, `(`, `comm` up to the **last** `)` of the file, a space and at least 20
  single-space-separated fields; state is field 3, `ppid` 4, `flags` 9, `utime` 14, `stime` 15, `starttime` 22. The PID must equal the
  directory name; `comm` may be empty and hold any byte but NUL.
- `status`: at most 16 KiB; only lines starting with `Uid:`, `VmRSS:` and `VmSwap:` are read, each at most once. `Uid:` is four
  tab-separated decimal values within 32 bits, the second the effective UID; sizes are digits and ` kB`, the value times 1,024 within 64
  bits. `VmRSS` may be absent only for a process without address space.
- `smaps_rollup`: at most 4 KiB; the first line is not read, of the rest only `Rss:` and `Pss:`, each exactly once; other lines are
  ignored. RSS comes from here when readable, so RSS and PSS are comparable, else from `VmRSS`.
- `cmdline`: at most 4 KiB per process. Trailing NUL bytes are removed, every other NUL becomes a space, every byte that does not start a
  valid UTF-8 sequence becomes U+FFFD, and the text is cut to 1,024 bytes at a character boundary while it is built; other control
  characters are kept. `comm` is made valid UTF-8 the same way.
- `/proc/stat`: line by line, at most 1 MiB in total; a line over 64 KiB is skipped to its end; `btime`, one space and digits must occur
  exactly once.
- `/etc/passwd`: at most 1 MiB, lines of at most 4 KiB, read once per run; a line with fewer than 3 fields, an empty name or a UID that
  is not 1 to 10 digits within 32 bits is skipped; the first line of a UID wins; a name is made valid UTF-8 and cut to 256 bytes.
- `started_at` must lie before the year 10000; the arithmetic is checked before any conversion to a signed type.
- `/proc` is listed in chunks of 256 entries. A PID directory's name is the canonical decimal of a 32-bit PID above 0 (1 to 10 digits, no
  leading zero); other names are no process and are not counted. The 4,096 numerically lowest PIDs are kept while the listing streams;
  every other PID directory is counted in `total` and makes `complete` false.

### Outcomes and logging

A process whose files disappear (`ENOENT`) or whose read answers `ESRCH` has ended: it is left out, not counted, and `complete` is
unchanged. `ESRCH` on `smaps_rollup` of a kernel thread (`PF_KTHREAD` in the `stat` flags) or a zombie means no address space. `EACCES`
or `EPERM` on `smaps_rollup` leaves `pss_bytes` out and takes `rss_bytes` from `VmRSS`. Any other read or parse error of one process
leaves it out and makes `complete` false; the sample goes on. The sample fails (a gap) only when `/proc` cannot be listed, the boot time
cannot be read or no process was read. An unreadable `passwd` makes every user the decimal UID. Error texts name the file relative to
`/proc` or `/etc` (`1234/stat`, `stat`, `passwd`) and a fixed reason.

One line per change, with `collector=processes`: warning `process memory partly unknown` (`processes`: the number without PSS) when it
becomes positive, info `process memory known again` when it returns to 0; warning `processes not read` (`processes`: the count, `err`: the
first error) and info `processes read again`; warning `user names unavailable` (`err`) and info `user names available`. No log line or
error text holds file content, a `comm`, a command line, a user name or a rule value. The stored `command`, `cmdline`, `user` and
`program` are external text that whoever displays them escapes.

### The record always fits

No text a user chooses can fail the sample. Every rule the model checks per entry (`pid` above 0 and distinct, `ppid` not negative,
`user`, `command`, `cmdline` and `program` at most 1,024 bytes of valid UTF-8, `command` and `program` not empty, a `state` of one letter
or none, `started_at` in UTC, `cpu_percent` finite and not negative, `count` at least 1, at most 4,096 entries per list) holds by
construction. As a second guard each entry is validated and encoded alone; one that fails is left out with `complete` false (counted with
the processes not read, fixed text `processes: entry refused`). The record is filled entry by entry within a payload budget of 1 MiB less
8 KiB of JSON: programs first, at most half the budget, in their order, stopping at the first that does not fit; then the processes in
theirs, likewise. The finished record is checked once against the producer contract of the [wire format](wire-format.md); as a safety
net a record still too long is shortened by halving its processes, then its programs, and any other error fails the sample.

### Memory, hanging reads and cost

A run reads one file at a time into reused buffers, holds per process only its numbers and three texts of at most 1,024 bytes, and lists
`/proc` in chunks. One run over 4,096 processes with maximal texts allocates at most **48 MiB in total**, garbage included.

A read that blocks is abandoned at the timeout with a gap, and the collector is not started again while it hangs. A local user who holds
a process's memory map lock can make its `cmdline` or `smaps_rollup` read block; that costs process samples, as gaps, until the process
moves (accepted residual, record 0113, option 7).

A snapshot of 250 processes takes under 100 ms on the development host (48.9 ms). On the production server, with memory pressure mode
on and every process read including `smaps_rollup`, a snapshot of 174 processes (not the 250 of the criterion) took a median of 55.5 ms
(53.3 to 67.3 ms of ten runs); one such snapshot every 15 seconds under pressure is 0.37 % of one core, in wall time (derived)
([Benchmarks](../BENCHMARKS.md), *Process snapshot*). The agent's mean CPU on the server is 0.42 % of one core over 24 hours
without sending and 0.49 % over 35 h 50 min while sending (*Agent footprint*).

## Related decisions

- [0019](../decisions/0019-agent-reads-proc-instead-of-top-lsof.md) — why `/proc` is read directly instead of running `top`.
- [0111](../decisions/0111-system-metrics-three-collectors-from-proc-sys-statfs-and-adjtimex.md) — three system collectors, `statfs` and
  `adjtimex`.
- [0138](../decisions/0138-clock-synchronized-below-16-seconds-of-maximum-error-pll-offset-only-while-the-pll-is-on.md) — the
  synchronization rule and the PLL offset only while the PLL is on.
- [0139](../decisions/0139-a-broken-system-file-fails-the-sample-a-measurement-the-host-does-not-offer-is-omitted.md) — a broken file fails
  the sample; absent values left out instead of zero.
- [0113](../decisions/0113-process-collector-pss-from-smaps-rollup-top-processes-full-snapshot-under-memory-pressure.md) — PSS from
  `smaps_rollup`, top processes and every program (every process under pressure), an entry that fails its check left out.
- [0142](../decisions/0142-program-names-follow-the-legacy-derivation-after-literal-grouping-rules.md) — legacy names plus literal grouping
  rules.

## Not here

- The agent's scheduling, gaps and the memory pressure mode the `system` sample decides: [Agent](agent.md).
- The OOM kills that request extra process snapshots: [Agent kernel events and boots](agent-kernel-events.md).
- Socket owners, which use the same `/proc` listing and names: [Agent connections](agent-connections.md); backup runs, which match
  processes: [Agent services and backup runs](agent-services.md).
- The rights that make `smaps_rollup` of other users readable (`CAP_SYS_PTRACE`): [Agent rights](agent-rights.md).
- Metric names, units and the `process_snapshot` fields: [Wire format records](wire-format-records.md); the options:
  [Configuration options](configuration-options.md).

## Implementation

`internal/sysstats` (system metrics and the memory pressure decision; fixtures `testdata/proc/`, `testdata/sys/`;
`TestSysstats_ImportsOnlyAllowedPackages` pins its imports and selectors), `internal/procstats` (process collector; fixtures
`testdata/proc/<pid>/`, `testdata/etc/passwd`, `testdata/processes/names.json`; `TestProcstats_ImportsOnlyAllowedPackages`; the 48 MiB
bound is asserted by `TestProcesses_LargeSnapshotFitsTheLineLimit`, the log and error texts by
`TestProcesses_ErrorsAndLogsHoldNoProcessText`).
