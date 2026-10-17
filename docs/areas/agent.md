# Agent

## Scope

What `vandox-agent`, the program on the monitored server, must do and what it must never do: how it connects, what it reads and how,
how it survives a stalled data source and a missing backend, how gaps are recorded and which rights it runs with. The format of what it
sends is in the [wire format](wire-format.md); its configuration file is in [Configuration and secrets](configuration-and-secrets.md).

**Status.** The command line, the start-up with the configuration file, logging, the scheduler with its gaps and the memory pressure
switch, the self-metrics, the system metrics, the process collector with the memory pressure mode and the notification of systemd exist, as do
the shared data model and the wire encoder. The other collectors, the spool, the sender and the systemd unit are **not implemented yet**: until the spool exists, the agent
drops the records it produces after checking them (it is not released in that state; v0.2.0 waits for the spool and the sender). The
rules below for the parts not implemented are the target the implementation is held to and the tests must check.

## Command line and start-up

- `vandox-agent` without arguments runs the agent with the configuration file `/etc/vandox/agent.yaml`; `-config <path>` names another
  file. `-version` prints the version line and exits with 0; `-h` and `-help` print the usage and exit with 0. An unknown flag, a
  positional argument or an empty `-config` is a usage error: the usage on standard error, exit code 2.
- At start-up the agent reads the configuration file and the agent token as [Configuration and secrets](configuration-and-secrets.md)
  describes. When that fails it logs one error line with the loader's error text and exits with 1, before it notifies systemd of
  anything.
- It runs until it receives SIGTERM or SIGINT, then stops its collectors (a collector stuck in a run is not waited for), tells systemd
  that it is stopping and exits with 0.

## Logging

- The agent writes its log to standard error, one line per entry, as `key=value` pairs at the configured `log.level`. A value that
  contains a space, a quote, a control character or any other character that is not printable is quoted with its control characters
  escaped, so no value can start or forge a line (verified for Go's `slog` text format: line breaks, `<N>` inside a value, U+2028,
  U+202E, invalid UTF-8 and control characters in keys all stay inside one line; a test pins it).
- The message and every text value are cut to 4 KiB, and every line, priority prefix and newline included, to 32 KiB, at a character
  boundary — below the journal's default line limit of 48 KiB (`LineMax=`), so the journal never splits an entry into a second one. A value whose Error or String method panics is logged as `<nil>` (a nil pointer) or `!PANIC: <value>`, as `log/slog` does.
- When the journal stream is standard error — `JOURNAL_STREAM` is set exactly once as `<device>:<inode>` in decimal and names the
  device and inode of the agent's standard error, as systemd documents — every line starts with its syslog priority — `<7>` debug,
  `<6>` info, `<4>` warn, `<3>` error — which the journal takes as the entry's priority, and carries no time stamp of its own (the
  journal stamps every entry). Otherwise (unset, malformed, set twice, or naming another stream) lines carry their time and no prefix,
  without a warning.
- The configuration and the agent token are never logged; the start-up line names only the agent ID and the version.

## Connection

- The agent only opens **outbound** connections to the backend's ingest port and **never listens on a port**. The monitored server
  needs no inbound firewall rule for Vandox.
- Commands for the server (a later feature) are fetched by the agent from the backend, never pushed to it, so they reach the server
  only at the agent's next poll.
- The agent never contacts Telegram or any other service; all notification goes through the backend.
- Two local channels to the service manager that started it are not network connections and need no rule: its log on standard error,
  which systemd passes to the journal, and the notification datagrams it sends to the local socket systemd names (*Service manager*).
  The agent binds no socket for them.

## Collecting

- Process, memory and connection data come from **reading `/proc` and `/sys` directly**, never from running `top`, `lsof` or other
  programs: starting programs is slow and can fail under memory pressure, which is when the data matters. Collectors read the kernel
  files through a replaceable file-system interface, never through hard-coded paths, so tests can substitute fixtures and hanging
  files.
- Linux only, matching the monitored server.
- **A hanging collector or database never blocks the agent.** Every collector runs isolated under its own deadline. When the
  deadline passes the agent stops waiting for it, records its missed sample as a gap, and keeps running the other collectors, the
  spool and the sender. A collector that is still stuck in a previous run is not started again; each skipped cycle is a gap as well.
  Sources whose API accepts a cancellation signal also receive the deadline so they can stop early. A hung source costs at most one
  stuck worker per collector.
- Every collector needs a deadline and a test with a double of its source that hangs: the cycle completes on time, a gap is recorded and
  the stuck collector is not started a second time.

## Scheduling

- Every collector has a name (the wire format's name pattern, unique in the agent), an **interval**, a **timeout** of at most the
  interval and optionally a **pressure interval** between the timeout and the interval. A schedule that breaks these rules stops the
  agent at start-up.
- Runs follow a fixed rate: the first run starts when the agent starts, each further one an interval after the previous run's due
  time, however long the previous run took. Each collector has its own runner, so no collector waits for another.
- A run that has not finished at its timeout is **abandoned**: the agent stops waiting, records a gap with cause `collector_timeout`
  and logs one warning for the stuck episode. While the abandoned run has not returned, every due run of that collector is
  **skipped** and recorded as a gap with cause `collector_timeout` as well; the collector is not started a second time. When the
  abandoned run returns, its result is dropped, one line names how many runs were skipped, and the collector runs again at its next
  due time.
- A run that **panics** or returns an **error** loses its sample: the panic is recovered, the sample is recorded as a gap with cause
  `unknown`, and the collector runs again at its next due time. A panic is logged as an error with the collector's name, the panic
  value (at most 1,024 bytes) and the stack (at most 4 KiB). Errors are logged at the first failure after a success, and the next
  success logs how many runs failed, so a collector failing every run does not flood the log. The records of a failed run are
  dropped, also those it returned together with its error.
- Every record of a successful run is stamped as an agent record (origin `agent`, the capture time of the run's start where the
  collector set none) and checked against the [wire format](wire-format.md), size included, as the producer's duty of its *Producer
  size contract*. A record that fails is dropped, the sample gets one gap with cause `unknown`, and the run's other records are kept.
- A run that starts a whole interval or more after its due time (the agent was frozen or not scheduled, or its previous cycle was held
  up) records the missed span, from the due time to the actual start, as a gap with cause `unknown` and runs at once; the next run is
  an interval after that. A run that starts late by less than an interval runs at once without a gap and keeps the grid.
- The gap of a missed sample covers the slot the sample stands for, from its due time to one interval later, so the gaps of
  consecutive missed samples join; its `collector` names the collector and its source is `agent`. A late start's gap covers the span
  from the missed due time to the actual start. Each gap's capture time is the moment it was recorded, so the gap of an abandoned or
  skipped run ends after its capture time.
- **Memory pressure mode** is switched on and off at run time by the agent (the thresholds belong to the collectors that measure
  pressure); switching to the mode already in force changes nothing. When the mode changes, a collector with a pressure interval
  takes its next due time from its last due time plus the interval of the new mode, but never earlier than the moment of the switch:
  a switch long after the last run starts a run at once, and from then on the collector keeps the new interval's grid. A switch
  never makes a run late and never records a gap; a collector held up past the new due time by a whole interval records the span
  from that due time as a late start. Collectors without a pressure interval are unchanged. Every run knows whether the mode was on
  when it started, so a collector can take a fuller sample under pressure.
- A runner counts as **on time** while the current moment is not later than the due time of the run it is in or waiting for, plus
  its timeout plus 30 seconds; a runner that is waiting for an abandoned run is on time (it moves on at each due time), and a pressure
  switch moves the due time with it. The service manager's watchdog is fed only while every runner is on time (*Service manager*).
- Records go to the sink with the agent's own context, not one bounded by the run's timeout. When the sink refuses records, the
  agent logs the error (at the first failure, and the recovery once) and records no gap itself: the sink that refuses records owns
  recording their loss as a gap (the spool, *Spool and sending*).
- Until the spool exists, the records that passed these checks are dropped. The spool assigns the sequence numbers (*Spool and
  sending*).

## Self-metrics

The collector `self` (interval 60 seconds, timeout 10 seconds) records the agent's own resource use as ordinary `metric` records with
source `agent`, through the scheduler like every collector: its resident memory, its CPU time, its number of goroutines, and — once
the spool and the sender exist — the size of the spool and how far sending lags behind capture. The names, units and values are in the
[wire format](wire-format.md) (*Metric names*, the `agent_*` rows).

The resident memory is read from at most 64 KiB of the agent's own `/proc` status file, and its line must be `VmRSS:`, spaces or tabs,
1 to 12 ASCII digits, one space and `kB`; anything else, a failed read, a negative spool size or a negative send lag fails the
sample, which then becomes a gap.

## System metrics

Three collectors record the state of the server as `metric` records with source `system`, through the scheduler like every
collector. The names, units and labels are in the [wire format](wire-format.md) (*Metric names*). No collector starts a program,
uses D-Bus or opens a socket: they read `/proc` and `/sys`, and two read-only system calls, `statfs` for the use of a file system and
`adjtimex` for the kernel's clock discipline.

| Collector | Interval | Under memory pressure | Timeout | Reads |
| --------- | -------- | --------------------- | ------- | ----- |
| `system` | 30 s | 10 s | 5 s | `/proc/stat`, `/proc/loadavg`, `/proc/meminfo`, `/proc/vmstat`, `/proc/pressure/{memory,cpu,io}`, `/sys/block` |
| `filesystem` | 60 s | — | 10 s | `/proc/self/mountinfo`, `statfs` of each configured mount point |
| `clock` | 60 s | — | 5 s | `adjtimex` without changing anything |

The `filesystem` collector runs only when at least one mount point is configured (`filesystem.mount_points`, default `/`, see
[Configuration and secrets](configuration-and-secrets.md)). Each collector's failure is its own gap, so a `statfs` that hangs on a
stale mount loses only the file system sample.

**Records of one `system` sample**, in this order: `cpu_percent` per mode (from the second sample on), `load1`, `load5`, `load15`,
the memory and swap values as `top` prints them, one `meminfo_bytes` or `meminfo` record per line of `/proc/meminfo` in file order,
the pressure values per resource (`memory`, `cpu`, `io`) and kind (`some`, then `full`), the four `vmstat_total` counters
(`pswpin`, `pswpout`, `pgmajfault`, `oom_kill`), their four `vmstat_per_second` rates (from the second sample on), then per disk in
name order its five counters and, from the second sample on, its five rates.

- **CPU.** The first line of `/proc/stat` is `cpu`, one or more spaces and at least 8 counters (user, nice, system, idle, iowait,
  irq, softirq, steal; later fields are ignored). `cpu_percent` of a mode is its increase since the previous sample divided by the
  increase of the sum of the 8 counters, times 100. Only the first 4 KiB of the file are read; a first line longer than that fails
  the sample.
- **Load.** The first three fields of `/proc/loadavg`, each 1 to 10 ASCII digits, a point and 2 digits.
- **Memory.** Every line of `/proc/meminfo` is a name of 1 to 64 characters of `A-Z a-z 0-9 _ ( )`, a colon, one or more spaces and
  1 to 20 ASCII digits within 64 bits, optionally followed by ` kB` (the value times 1,024 must fit in 64 bits). A `kB` line is
  `meminfo_bytes` in bytes, a line without unit `meminfo`, label `field` the name. The derived values follow procps 3.3.17, which
  Ubuntu 22.04's `top` uses: `mem_total_bytes` = MemTotal, `mem_free_bytes` = MemFree, `mem_buff_cache_bytes` = Buffers + Cached
  + SReclaimable, `mem_used_bytes` = MemTotal − MemFree − Buffers − Cached − SReclaimable (MemTotal − MemFree when that is
  negative; left out when MemFree is the larger), `mem_available_bytes` = MemAvailable; `swap_total_bytes` = SwapTotal, `swap_free_bytes` = SwapFree, `swap_used_bytes`
  = SwapTotal − SwapFree (left out when SwapFree is the larger). MemTotal, MemFree, MemAvailable, Buffers, Cached, SReclaimable,
  SwapTotal and SwapFree must each be present as a `kB` line. At most 16 KiB and 128 lines are read; a field named twice fails the
  sample.
- **Fault and swap counters.** In `/proc/vmstat` the lines of `pswpin`, `pswpout`, `pgmajfault` and `oom_kill` are the name, one
  space and 1 to 20 ASCII digits; each must be present once. Other lines are not read. At most 64 KiB are read.
- **Pressure.** A file of `/proc/pressure` holds a `some` line and optionally a `full` line, each `some` or `full`, then
  ` avg10=A avg60=A avg300=A total=T` with single spaces, where A is 1 to 3 ASCII digits, a point and 2 digits, at most 100.00,
  and T 1 to 20 ASCII digits within 64 bits; anything else fails the sample. A file without `full` line (the `cpu` file before kernel
  5.13) gives only the `some` values. At most 256 bytes per file are read.
- **Pressure not offered.** When a pressure file does not exist (a kernel without PSI, or booted with `psi=0`) or its read answers
  "operation not supported", that resource's pressure values are left out and the sample succeeds. The agent logs one warning
  `pressure stall information unavailable` with the attribute `resources` (the unavailable resources, comma-separated, in the order
  `memory`, `cpu`, `io`) when that set changes and is not empty, and one info line `pressure stall information available` when it
  becomes empty again; a sample with an unchanged set logs nothing.
- **Disks.** A disk is an entry of `/sys/block` that has a `device` entry (so loop, RAM, zram, device-mapper and RAID devices,
  which have none, are left out), at most 16, the first in name order. Its `stat` file holds at least 11 fields of 1 to 20 ASCII
  digits separated by spaces; of them, reads and writes completed (fields 1 and 5), sectors read and written (fields 3 and 7, in
  units of 512 bytes) and the milliseconds with I/O in flight (field 10) become `disk_reads_total`, `disk_read_bytes_total`,
  `disk_writes_total`, `disk_written_bytes_total` and `disk_io_time_seconds_total`, label `device` the entry's name. At most 1 KiB per
  file is read. The I/O of a device-mapper, RAID or loop volume (`dm-*`, `md*`, `loop*`) is therefore recorded only as the I/O of its
  member disks; a comparison with the legacy script (#45) compares per disk and does not expect values for those volumes.
- **Rates.** A rate (`cpu_percent`, `vmstat_per_second`, the five disk rates) compares a sample with the previous successful sample
  of the same collector: the increase divided by the seconds between the two runs' start times (`disk_busy_percent`: the increase
  of the I/O milliseconds divided by those milliseconds, times 100). There is no rate in the first sample after the agent started,
  for a counter that fell, for a disk or counter the previous sample did not have, and when the start times did not move forward
  (the wall clock was set back). A failed sample is not a predecessor.

**Records of one `filesystem` sample**, per configured mount point in configuration order: `filesystem_size_bytes`,
`filesystem_used_bytes`, `filesystem_free_bytes`, `filesystem_available_bytes`, then `filesystem_inodes`, `filesystem_inodes_used`
and `filesystem_inodes_free`, label `mount` the configured path.

- A configured path counts as mounted when it is the mount point (field 5) of a line of the agent's own `/proc/self/mountinfo`, after
  decoding the kernel's escapes: a backslash and exactly three octal digits is one byte (`\040` a space, `\011` a tab, `\012` a line
  feed, `\134` a backslash). A line with fewer than 5 fields, a backslash in its mount point that does not start such an escape, or
  a line longer than 64 KiB fails the sample; the other fields are not decoded. The table is read line by line, so its length does not bound memory. A line longer than 64 KiB thus fails
  every `filesystem` sample for as long as that mount exists: each sample is a recorded gap, never a silent omission.
- A path that is not mounted is left out of the sample, and the agent logs one warning `mount point not mounted` with the attribute
  `mount` when the path stops being mounted (or is not mounted at the first sample), and one info line `mount point mounted` when it
  is mounted again. `statfs` is called only for mounted paths.
- Size, free and available are the block counts times the fragment size (the block size when the fragment size is 0); used is size
  minus free. The inode values are left out when the file system reports 0 inodes (btrfs has no fixed count). A `statfs` error,
  free or available blocks above the total, free inodes above the total, or a size (blocks times fragment size) above 64 bits fail
  the sample; the error names the mount point by its index
  (`filesystem.mount_points[1]`), never by its path.
- The values are those of the agent's mount namespace: a path the unit hides or replaces (`ProtectHome=` for `/home`,
  `InaccessiblePaths=`) reports what the agent sees, and a mount point unmounted between the read of the table and `statfs` reports
  the file system below it for that one sample.

**Records of one `clock` sample:** `clock_synchronized`, `clock_max_error_seconds`, `clock_estimated_error_seconds` and, only while
the kernel's phase-locked loop is on, `clock_pll_offset_seconds`.

- `clock_synchronized` is 1 when the kernel's maximum error is below 16 seconds and 0 otherwise — the rule `timedatectl` uses, which
  holds for systemd-timesyncd and chrony alike (chrony keeps the kernel's "unsynchronized" flag set unless `rtcsync` is configured,
  so the flag is not used).
- The PLL offset is the correction the kernel still has to apply (nanoseconds when the kernel reports nanoseconds, microseconds
  otherwise, converted to seconds). systemd-timesyncd hands its measured offset to the PLL, so there it is the remaining offset;
  chrony switches the PLL off, so under chrony the value is left out, never written as 0. A negative error estimate or a failed call
  fails the sample.

**Absent is not zero.** A measurement the server does not offer — pressure values, a configured path that is not mounted, inode
values of a file system without them, the PLL offset while the PLL is off, a rate without predecessor — is left out of the sample.
It is not a missed sample and records no gap; the backend reads it as unknown, never as 0.

**Errors.** A failed `system`, `filesystem` or `clock` sample becomes a gap with cause `unknown` and is logged by the scheduler
(*Scheduling*). The error texts name the file (`meminfo`, `pressure/io`, `block/sda/stat`,
`self/mountinfo`), the line number where there is one and a fixed reason; they never contain file content or a configured path.

## Processes

The collector `processes` (interval 60 seconds, pressure interval 15 seconds, timeout 10 seconds, placed after `system` in the job list)
records one `process_snapshot` record per run with source `processes` and the capture time of the run's start, in the model the legacy
log parser writes ([wire format](wire-format.md); the legacy naming is in [Log import](log-import.md)). It is the replacement for the
`top` script. It reads `/proc/<pid>/stat`, `status`, `cmdline` and `smaps_rollup` of every process through the replaceable file-system
interface (and `/proc/stat` for the boot time and `/etc/passwd` for user names), starts no program, opens no socket and makes no raw
system call; the package imports none that would.

**What a snapshot holds.**

- Every program is summed over **every process read**, not only the listed ones, so the `count` of the programs adds up to `total` (less
  the processes left out below): `count`, `cpu_percent` (sum), `rss_bytes` (sum, saturating at the largest 64-bit value),
  `pss_bytes` (sum, saturating, present only when **every** member has a PSS) and `rss_overcounted` (exactly when `count` is above 1).
  RSS counts a shared page in every process that maps it, PSS divides it among them, so the PSS sum is the memory the program really
  holds in the memory processes map; kernel memory and unmapped page cache are in neither sum.
- Without pressure mode the list of processes is the union of the top 25 by memory and the top 25 by `cpu_percent` (ties by PID, lowest
  first). Under pressure mode (the run knows it, *Scheduling*) it lists **every process read**. `total` is the number of processes seen
  (the PID directories, less those that ended during the read), `complete` is true only when every process was read and listed and no
  entry was left out for size or validity.
- **One memory key for the whole run:** the PSS when every process read has one, else the RSS for all, so a process whose PSS cannot be
  read does not put PSS beside RSS in one ranking. Processes and programs are ordered by that key descending, then processes by PID
  and programs by name (ordinal). Without `CAP_SYS_PTRACE` the key is the RSS, as `top` ranks.
- Per process: `pid`, `ppid`, `user` (the name of the **effective** UID from `/etc/passwd`, else the UID in decimal), `command`, `cmdline`
  (at most 1,024 bytes; `truncated` when more was there), `state` (the `stat` state when it is one ASCII letter, else left out),
  `started_at` (boot time plus `starttime` in clock ticks of 10 ms, UTC), `cpu_percent`, `rss_bytes`, `pss_bytes` and `swap_bytes` (`VmSwap`,
  left out when the line is absent). `oom_score_adj` is not read. A process without address space (a kernel thread, a zombie) has
  `rss_bytes` 0 and `pss_bytes` 0, which is the measurement; a PSS that cannot be read is left out, never 0.
- **CPU.** With a predecessor (the same PID with the same `starttime` in the previous successful run, runs moving forward, counters not
  fallen) `cpu_percent` is the CPU time used between the two runs' starts over the time between them; without one (first run, new or
  reused PID, counters that fell, the clock set back) it is the average since the process started, and 0 when it has no age. The clock
  ticks are the fixed 100 per second (`USER_HZ` of the Linux ABI on x86-64 and arm64). A failed run is no predecessor.

**Names.** The `command` is the legacy derivation, pinned by the fixture `testdata/processes/names.json` that the agent and the backend's
legacy parser both read: the text inside `[...]` when the whole command line is bracketed, else the part after the last `/` of the first
space-separated word without one trailing `:`; when that is empty the command line; when that is empty too the cleaned `comm`; when that
is empty too the fixed text `(unnamed)`, which holds no process text, so a `command` is never empty (a process that blanks its name
stays in the program sums instead of hiding there). The default program is the first space-separated word of the `command`. The
configured rules (`processes.programs`, [Configuration and secrets](configuration-and-secrets.md)) are tried first, in order; the first
match gives the program, which is the rule's `program`, with `append_next_word` followed by `/` and the word after the prefix in the
cleaned command line (up to the next space, cut so that the program has at most 1,024 bytes at a character boundary; an empty word
leaves the program). The default rule list is empty, so the names match the legacy log. The titles `php-fpm: pool ...`, `spamd` and
`amavisd` used as examples are the forms those programs document, captured through a test process, **not captured from a Plesk server**;
the parallel run of #45 checks them.

**Readers (security area 10).** Every read is bounded before it is parsed and every number is 1 to 20 ASCII digits parsed into its target
width; nothing else is accepted:

- `stat`: at most 4 KiB, read whole. The PID, a space, `(`, `comm` up to the **last** `)` of the file, a space and at least 20
  single-space-separated fields; the state is field 3, `ppid` 4, `flags` 9, `utime` 14, `stime` 15, `starttime` 22. The PID must equal
  the directory name; `comm` may be empty and may hold any byte but NUL (a name can hold `)`, spaces and a line feed).
- `status`: at most 16 KiB; only lines that start with `Uid:`, `VmRSS:` and `VmSwap:` are read, each at most once. The kernel escapes a line
  feed of the name, so no name can start such a line. `Uid:` is four tab-separated decimal values within 32 bits, the second the
  effective UID; the sizes are digits and ` kB`, and the value times 1,024 must fit 64 bits. `VmRSS` may be absent only for a process
  without address space.
- `smaps_rollup`: at most 4 KiB; the first line is not read, of the rest only `Rss:` and `Pss:` are, each exactly once; `Pss_Dirty` and other
  lines (they differ between kernel versions) are ignored. RSS comes from this block when it is readable, so RSS and PSS are comparable,
  else from `VmRSS`.
- `cmdline`: at most 4 KiB read per process (a larger file is not read further). Trailing NUL bytes are removed, every other NUL becomes a
  space, every byte that does not start a valid UTF-8 sequence becomes U+FFFD, and the text is cut to 1,024 bytes at a character boundary
  while it is built; other control characters are kept as written. `comm` is made valid UTF-8 the same way.
- `/proc/stat`: line by line, at most 1 MiB in total; a line longer than 64 KiB (the `intr` line of a large host) is skipped to its end;
  the line `btime`, one space and digits must occur exactly once.
- `/etc/passwd`: at most 1 MiB, lines of at most 4 KiB; a line with fewer than 3 fields, an empty name or a UID that is not 1 to 10 digits
  within 32 bits is skipped; the first line of a UID wins; a name is made valid UTF-8 and cut to 256 bytes. It is read once per run.
- `started_at` must be a time before the year 10000, which the JSON encoding of a time needs; the arithmetic is checked before any
  conversion to a signed type.
- `/proc` is listed in chunks of 256 entries (never all names at once). A PID directory's name is the canonical decimal of a 32-bit PID
  above 0 (1 to 10 digits, no leading zero); other names are no process and are not counted. A bounded max-heap keeps the 4,096 numerically
  lowest PIDs while the listing streams; every other PID directory is counted in `total` and makes `complete` false.

**Outcomes.** A process whose files disappear (`ENOENT`) or whose read answers `ESRCH` has ended: it is left out, not counted, and
`complete` is unchanged. `ESRCH` on `smaps_rollup` of a kernel thread (`PF_KTHREAD` in the `stat` flags) or a zombie means no address
space. `EACCES` or `EPERM` on `smaps_rollup` leaves `pss_bytes` out and takes `rss_bytes` from `VmRSS`. Any other read or parse error of
one process leaves that process out and makes `complete` false; the sample goes on. The sample fails (a gap) only when `/proc` cannot be
listed, the boot time cannot be read or no process was read. A `passwd` that cannot be read makes every user the decimal UID. Error texts
name the file relative to `/proc` or `/etc` (`1234/stat`, `stat`, `passwd`) and a fixed reason, never file content, a `comm`, a command
line or a user name.

**Logging.** One line per change, through the collector's logger with the attribute `collector=processes`: a warning `process memory partly
unknown` (`processes`: the number without PSS) when that number becomes positive and an info line `process memory known again` when it
returns to 0; a warning `processes not read` (`processes`: the count, `err`: the first error) and `processes read again`; a warning `user
names unavailable` (`err`) and `user names available`. No log line holds process text.

**The record always fits and is valid on both sides.** No text a user chooses can fail the sample. Every rule the model checks per entry
(`pid` above 0 and distinct, `ppid` not negative, `user`, `command`, `cmdline` and `program` at most 1,024 bytes of valid UTF-8, `command`
and `program` not empty, a `state` of one letter or none, `started_at` in UTC, `cpu_percent` finite and not negative, `count` at least 1,
at most 4,096 entries per list) holds by construction. As a second guard each entry is also validated and encoded alone, and an entry that
fails either is left out with `complete` false (counted with the processes not read, with the fixed text `processes: entry refused`).
The collector fills the record entry by entry within a payload budget of 1 MiB less 8 KiB of JSON: programs first, at most half of the
budget, in their order, stopping at the first that does not fit; then the processes in theirs, stopping at the first that does not fit. The
finished record is checked once as the producer contract of the [wire format](wire-format.md); as a safety net that the budget makes
unused, a record that is still too long is shortened by halving its processes and then its programs, and any other error fails the sample.

**Bounded memory.** A run reads one file at a time into buffers that are reused for every process, at most 4 KiB of a command line per
process, holds per process only its numbers and three texts of at most 1,024 bytes, and lists `/proc` in chunks. One run over 4,096
processes with maximal texts allocates at most **48 MiB in total** (garbage included, so the memory it holds at any moment is less); this
asserted figure, checked by `TestProcesses_LargeSnapshotFitsTheLineLimit`, is the only memory figure stated here.

**Hanging reads (accepted residual).** A read that blocks is abandoned by the scheduler at the timeout with a gap, and the collector is not
started again while it hangs. A local user who holds the memory map lock of a process can make its `cmdline` or `smaps_rollup` read block; that costs process samples, as gaps, until the process moves. This
is deliberate: a goroutine and deadline per process would cost more than the collector itself (record 0113, option 10).

**Speed and cost.** A snapshot of 250 processes takes under 100 ms; the figure and the host it was measured on are in
[Benchmarks](../BENCHMARKS.md) (*Process snapshot*), not measured on the server (kernel 5.15, 2 GB), which #44 does. Reading
`smaps_rollup` of every process every 15 seconds under pressure is a cost nobody has measured on the server; it is an **open point for #44
and #45**.

## Memory pressure mode

The `system` collector decides the memory pressure mode ([Scheduling](#scheduling)) after each successful `system` sample, from the values
it has just read:

- the mode turns **on** when `MemAvailable` is below 10 % of `MemTotal` (10 times `MemAvailable` is less than `MemTotal`) or the memory
  `some avg10` is at least 10.00;
- it turns **off** only when `MemAvailable` is at least 15 % of `MemTotal` (20 times `MemAvailable` is at least 3 times `MemTotal`) and
  `some avg10` is below 5.00 or the memory pressure file is not available (a kernel without pressure stall information);
- otherwise it keeps its mode. The products are computed exactly (128 bits, no overflow, no floating point). The thresholds are fixed, not
  options, until the parallel run of #45 shows a reason (record 0113, option 4).

The collector calls the switch (`Scheduler.SetPressure`, which the agent connects when it builds the scheduler) only when the mode changes;
it starts off. A change logs one info line `memory pressure mode on` or `memory pressure mode off` with `collector=system`,
`mem_available_bytes` and, when the pressure file was read, `memory_some_avg10`. A failed sample changes nothing. The effect: the `system`
(10 seconds) and `processes` (15 seconds) collectors run at their pressure intervals and `processes` lists every process.

Because the decision follows a `system` sample, the switch can lag the pressure by up to one `system` interval (30 seconds). Any local
user can switch the mode on by allocating memory; the result is a process snapshot with every process every 15 seconds and a spool that
grows faster until its size bound evicts older data with a recorded gap, bounded by the record budget (a stated residual).

## Service manager

- When systemd started the agent with a notification socket, the agent sends `READY=1` once its collectors run, `WATCHDOG=1` while
  the watchdog is enabled, and `STOPPING=1` when it stops. Each notification is one datagram; the dial and the write of it have a
  deadline of one second, so a socket whose queue is full makes the notification fail at that deadline instead of blocking the agent
  (measured: the write returns a deadline error once about 11 datagrams are queued).
- The socket is the one `NOTIFY_SOCKET` names: an absolute path of at most 107 bytes, or `@` and an abstract name of 1 to 107 bytes.
  Any other value (a relative path, `@` alone, a longer name, the `vsock:` forms of newer systemd versions) disables the notification
  with a warning; the agent runs the same. Unset or empty means no notification and no warning.
- The watchdog is enabled when `WATCHDOG_USEC` holds 100,000 to 9,223,372,036,854,775 microseconds in ASCII digits (a period below
  100 ms is refused, so the agent never pings in a busy loop) and `WATCHDOG_PID` is
  unset or the agent's process ID; the agent then sends `WATCHDOG=1` every half of that period while all collector runners are on
  time (*Scheduling*). When a runner is not on time, the notification is withheld and one warning is logged until it is sent again,
  so systemd restarts an agent whose runners stopped moving. A malformed value disables the watchdog with a warning.
- For this to work the unit must use `Type=notify`, set `WatchdogSec=` and allow notifications from the main process
  (`NotifyAccess=main`); the unit is part of the installation files.
- `NOTIFY_SOCKET`, `WATCHDOG_USEC` or `WATCHDOG_PID` named twice in the environment is treated as malformed (*Logging* for
  `JOURNAL_STREAM`).

## Gaps

**There is no data gap unless it is explicitly recorded.** The agent records the gaps it knows about: a collector sample missed or
skipped, and spool data dropped when the spool's size bound is hit. The backend records the gaps it detects (missing sequence numbers,
intervals with no batch). Analysis, reports and the web UI treat a recorded gap as *unknown*, never as *normal*. Spool eviction,
collector timeouts and backend gap detection each emit a gap record, and each path has a test.

## Spool and sending

- The agent keeps collecting while the backend is unreachable and keeps **at least 7 days** of records in an on-disk spool. When the
  backend is reachable again it sends current data first, then backfills the spool chronologically and throttled.
- Every record carries a sequence number from one counter per agent that increases strictly, survives restarts and reboots and is never
  reused. A batch has no identity of its own (see the wire format). Losing the counter makes new records collide with stored ones, so it
  is persistent state of the agent.
- The boot ID and the clock offset are kept with each spooled record, and a batch holds only records with the same boot ID and offset.
- The spool is a file-write area of the agent: it has a size bound and defined behavior when it is full (oldest data dropped, a gap
  recorded). Records the spool refuses to take from the scheduler are its loss to record as a gap, too.

## Rights

- The agent runs as the dedicated system user `vandox-agent` without a login shell, **never as root**. It authenticates to MariaDB as
  that OS user through `unix_socket` with only the `PROCESS` privilege.
- For reading it is a member of the groups `adm` (log files) and `systemd-journal` (journal). To read other users' processes and their
  open files it holds exactly `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH`, as ambient capabilities with a bounding set limited to these two.
- **Process collector.** `stat`, `status` and `cmdline` of every process are readable by any user (unless `/proc` is mounted with
  `hidepid`); `smaps_rollup` of **another user's** process needs `CAP_SYS_PTRACE`, and `CAP_DAC_READ_SEARCH` does not replace it. Measured
  on the development host as UID 65534 against a root process in the same namespace, without Yama or `hidepid`: `smaps_rollup` answers
  `EACCES` without capabilities and with `CAP_DAC_READ_SEARCH` alone, and is readable with `CAP_SYS_PTRACE`. A Yama or other LSM policy,
  and another user namespace, were not measured and can refuse the read even with the capability. Without it the collector records the
  RSS from `status` and leaves the PSS out. `CAP_SYS_PTRACE` is the process collector's use of the capability; the list of rights per
  collector is in `deploy/agent/README.md`.
- As soon as the unit grants any capability, the unit is confined: `NoNewPrivileges=yes`; a system-call filter that denies `ptrace`,
  `process_vm_readv`, `process_vm_writev`, `pidfd_getfd` and `open_by_handle_at` with `EPERM` (and not `pidfd_open` or
  `pidfd_send_signal`) under `SystemCallArchitectures=native`; no write-side capability; `ProtectHome=yes` and `InaccessiblePaths=` for at
  least `/etc/shadow` and `/etc/gshadow`. The last two protect only against accidental reads; they are not a limit on a compromised
  agent, which can reach the same files through another process's root in `/proc`.
- The system metrics need no right of their own: `/proc/stat`, `/proc/loadavg`, `/proc/meminfo`, `/proc/vmstat`, `/proc/pressure`,
  the agent's own mount table and `/sys/block` are readable by any user, `statfs` needs only search permission on the directories
  above the mount point, and `adjtimex` without a change needs no capability (measured: unprivileged and without capabilities,
  `adjtimex` with modes 0 and `statfs` succeed, while an `adjtimex` that sets the maximum error fails with `EPERM`). The unit's system-call filter must allow `adjtimex`: `ProtectClock=yes` denies it with `EPERM` even for a
  read, and the group `@system-service` does not contain it, so an allow-list names it. A change of the clock still needs
  `CAP_SYS_TIME`, which the bounding set excludes.
- For restarting units (self-healing, a later feature) a polkit rule lets this user restart only the units named in the configuration;
  there is no sudo and no shell.
- Each group, capability and confinement setting is listed and justified in `deploy/agent/` when the collector that needs it is
  introduced; anything not listed is not granted. Removing a confinement setting, adding a capability or a group, or adding a collector
  that needs a new right is a `security`-tier change; removing a confinement while a capability is granted needs a superseding record.
- **Accepted residual:** the agent's read access equals root's (every file, the memory and environment of every process). A separate
  small helper that holds the capabilities is the way to reduce it.

## Related decisions

- [0006](../decisions/0006-agent-connects-outbound-only.md) — why outbound only.
- [0019](../decisions/0019-agent-reads-proc-instead-of-top-lsof.md) — why `/proc` is read directly.
- [0028](../decisions/0028-data-gaps-are-always-recorded.md) — why gaps are always recorded.
- [0029](../decisions/0029-hanging-collector-never-blocks-the-agent.md) — why collectors are isolated and abandoned at their deadline.
- [0030](../decisions/0030-agent-runs-unprivileged-with-named-capabilities.md) — why a dedicated user with two confined capabilities.
- [0045](../decisions/0045-batch-identified-by-agent-id-and-record-sequence-numbers.md) — why a spool of at least 7 days and per-record sequence numbers.
- [0110](../decisions/0110-agent-runtime-frame-runner-per-collector-gaps-for-every-missed-sample-hand-written-sd-notify.md) — why one runner per
  collector at a fixed rate, a gap for every missed sample, the notification without a library, the watchdog gated on the runners and the
  log format.
- [0111](../decisions/0111-system-metrics-three-collectors-from-proc-sys-statfs-and-adjtimex.md) — why three system collectors,
  `statfs` and `adjtimex` beside `/proc` and `/sys`, the synchronization rule, the PLL offset only while the PLL is on, and absent
  values left out instead of zero.
- [0113](../decisions/0113-process-collector-pss-from-smaps-rollup-top-processes-full-snapshot-under-memory-pressure.md) — why the process
  collector reads `smaps_rollup` for PSS, lists the top processes and every program (every process under pressure), the `system` collector
  switches the memory pressure mode with fixed hysteretic thresholds, names follow the legacy derivation plus literal rules, and an entry
  that fails its check is left out.

## Not here

- The format of batches and records: [wire format](wire-format.md).
- Which options the agent reads and how secrets are supplied: [Configuration and secrets](configuration-and-secrets.md).
- What the backend does with the data, including gap detection: the storage and detection areas.

## Implementation

`cmd/vandox-agent` and `internal/cli` (Go), `internal/agent` (start-up, logging, watchdog), `internal/scheduler`, `internal/selfstats`,
`internal/sysstats` (system metrics and the memory pressure mode; fixtures `testdata/proc/`, `testdata/sys/`),
`internal/procstats` (process collector; fixtures `testdata/proc/<pid>/`, `testdata/etc/passwd`, `testdata/processes/names.json`),
`internal/sdnotify`, `internal/config`, `internal/model`, `internal/wire`; `deploy/agent/` (example configuration; the unit and install
scripts follow with the collectors).
