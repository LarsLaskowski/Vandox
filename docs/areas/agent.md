# Agent

## Scope

What `vandox-agent`, the program on the monitored server, must do and what it must never do: how it connects, what it reads and how,
how it survives a stalled data source and a missing backend, how gaps are recorded and which rights it runs with. The format of what it
sends is in the [wire format](wire-format.md); its configuration file is in [Configuration and secrets](configuration-and-secrets.md).

**Status.** The command line, the start-up with the configuration file, logging, the scheduler with its gaps and the memory pressure
switch, the self-metrics, the system metrics, the process collector with the memory pressure mode, the connection collector (with its start-up probe of the socket mount) and the notification of systemd exist, as do
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

## Connections

The collector `connections` (interval 60 seconds, pressure interval 15 seconds, timeout 10 seconds, placed after `processes` in the job
list) records one `connection_snapshot` record per run with source `connections` and the capture time of the run's start. It replaces the
`lsof -ni` half of the legacy script (record 0019). It reads `/proc/net/tcp`, `tcp6`, `udp` and `udp6`, lists `/proc`, reads
`/proc/<pid>/fdinfo/<n>` (the first lines only) and what the process collector reads to derive a `command` (`stat`, `cmdline`), through the
replaceable file-system interface. It starts no program, opens no socket and makes no raw system call; `TestConnstats_ImportsOnlyAllowedPackages`
pins the imports (`os` only for `DirFS`, no `syscall`). The one socket the agent opens for it is the start-up probe below, outside the collector.

**Terms.** A **socket** is one line of the four tables of the agent's network namespace (`/proc/net` is the reader's own namespace);
two lines are two sockets, also when both have inode 0 (`TIME_WAIT`, `SYN_RECV`), and such a line never matches a descriptor. A
**listener** is a TCP socket in `LISTEN` or a UDP socket whose remote address is all zeros with port 0; a **connection** is a socket with a
non-zero remote address or port; any other TCP socket counts in `states` only. The **owner** of a socket is the lowest PID among the
processes that hold it.

**Tables.** `tcp`, `tcp6`, `udp`, `udp6` are read in that order, line by line; the first line is the header. A line longer than 512 bytes is
skipped to its end and counted unreadable. A line needs at least 10 fields: `sl` (digits and `:`), the local and remote endpoint (8 upper-case
hex digits for IPv4 and 32 for IPv6, `:`, 4 upper-case hex digits), the state (2 upper-case hex digits) and the inode (field 10, 1 to 20
digits within 64 bits); the fields between and after are not interpreted. Each address word is read as a 32-bit number and written in the
kernel's host byte order, so `0100007F` is `127.0.0.1`. An IPv4-mapped address in `tcp6` is kept as the kernel reports it (`::ffff:a.b.c.d`,
no unmapping) and so is a link-local one, without a zone ([wire format](wire-format.md), *Producer mapping for connection endpoints*). The
TCP states `01` to `0C` map to the model's twelve names; any other code makes the line unreadable; for UDP the state is checked and dropped.
`sl` is not unique and a line can end in `-1` or in trailing spaces; none of it matters. A table that does not exist (`tcp6` and `udp6` on a
kernel without IPv6, observed on the development host) leaves its protocol out; all four missing fail the sample (`connstats: no socket table
found`), and a table that exists but cannot be read fails it too (a gap, [Scheduling](#scheduling)).

**Caps.** Every socket of the tables is counted in `states`; the rest is bounded. Per run: the inode index holds the first 65,536 sockets with an inode
other than 0 (more sockets have no owner and make the aggregates incomplete); the listener inventory holds the 4,096 lowest **distinct**
endpoints (the wire format's list limit), so `SO_REUSEPORT` sockets share one entry and never count toward the cut; the connections list the 4,096 lowest
(protocol, local, remote); `remotes` counts at most 4,096 remote addresses; at most 4,096 processes are scanned for owners (the lowest PIDs,
as the process collector keeps them); at most 64 new listeners are reported.

**Regular and full snapshots.** A **regular** snapshot (`aggregates_only` true, no `connections`) holds `states` (per protocol and TCP state,
by protocol then state, UDP without state), `processes` (one entry per process that owns at least one socket: PID, `command`, distinct
sockets it holds, by count descending then PID), `remotes` (the top 25 by count descending then address, address only) and `listeners` (the
inventory, ports below 1024 first, then protocol, address, port). A **full** snapshot adds every connection (by protocol, local, remote) and
every counted remote address. The first run after the agent starts, every run under memory pressure (the mode at the run's start, *Scheduling*)
and every run that finds at least one new listener, reported or omitted, is full; every other run is regular. `complete` keeps its meaning of
the wire format and is true only for a full snapshot in which nothing was cut, skipped, left without an owner beyond a cap, unread or
uncounted, so a regular snapshot is never complete. `aggregates_incomplete` is set when `states`, `listeners` or `processes` may not cover
every socket the run saw: a list cut for size or at its cap, a skipped table line, a socket beyond the inode index, more processes than the
PID cap, a process whose descriptors could not be read, or an owner scan that did not run (below). Imported legacy snapshots and an older agent's
carry none of the new fields and read as full with complete aggregates. A regular snapshot's top 25 `remotes` are over the counted addresses
only: an address first seen after the 4,096 are counted, however heavy, is missing from it (a full snapshot says so through `complete`).
`remotes` does not unmap IPv4-mapped addresses, so one IPv4 peer connected to a `0.0.0.0` and to a `::` listener is two entries.

**Owners.** The agent does not read the `/proc/<pid>/fd` links, because listing them needs `CAP_DAC_READ_SEARCH` (*Rights*). For every
listed process it lists `<pid>/fdinfo` in chunks of 256 and, per descriptor, opens the file, reads at most 256 bytes and closes it. The
first four lines must be `pos:`, `flags:`, `mnt_id:` (1 to 10 digits, not 0) and `ino:` (1 to 20 digits), each a tab and digits; later lines
(an epoll's `tfd:`) are not read. A descriptor counts only when its `ino:` is the inode of a tracked socket **and** its `mnt_id:` is the
**socket mount** (below). Per tracked socket the collector keeps two 32-bit numbers, its owner and the last PID that counted it, so several
descriptors of one socket in one process count once for that process; per PID it counts the distinct sockets. No list of matches is kept,
so no process's descriptors can crowd out another's owners. A socket held by several processes counts once in `states`, `remotes`,
`listeners` and `connections`, is owned by the lowest PID and counts once in the `processes` entry of every holder; several listening
sockets on one endpoint are one listener owned by the lowest owning PID. A process that ends during the run is skipped (`ENOENT`/`ESRCH` on its
directory); a descriptor closed meanwhile is skipped; an ended process owns nothing and is not in `processes`. A descriptor of a socket that
is not in the four tables (another network namespace, a Unix socket) counts nowhere. Sockets without an owner (`TIME_WAIT`, a closed or
unreadable one) count in `states` and, as connections, in `remotes` and `connections`; their entries carry no PID and no `command`. The
`command` is the process collector's (*Processes*, **Names**, through `procstats.CommandReader`). It is text the process chooses; the
PID is the kernel's. The kernel's `uid` column of the tables is not recorded (a follow-up).

**The socket mount.** An inode number alone does not tell a socket from a file with the same number, which any user can create on a mount
of their own (a tmpfs in a user namespace, FUSE, a container). Every socket of every family, in any namespace, is allocated on one
internal kernel mount that holds nothing else and that no user can mount (upstream `net/socket.c`, `sock_alloc_file`, read at v5.15; the
Ubuntu 22.04 kernel is built on it). Measured on kernel 6.18.44: `mnt_id: 10` for a Unix datagram, a TCP, a UDP and a netlink socket in
the initial namespaces, after `unshare -m` and as UID 65534 after `unshare -Urm` and `unshare -Urmn`, while a pipe has `mnt_id: 16`; mount IDs are
global, a file on a user-namespace tmpfs showed `mnt_id: 76`. At start-up the agent therefore opens one Unix datagram socket, never bound,
connected or sent on, reads the first lines of its own `/proc/self/fdinfo/<fd>`, checks that the `ino:` equals the inode `fstat` reports for the
descriptor (else the probe fails: `sockmount: self/fdinfo: inode mismatch`), closes both and keeps the `mnt_id:` for its lifetime
(`internal/sockmount`). The probe needs no right. Its failure texts are `sockmount: socket: <errno>`, `sockmount: self/fdinfo: <os error>` and
`sockmount: <fixed reason>`. When it fails, or when a collector is built without a probed mount, the collector opens no `fdinfo`, no
socket has an owner, `processes` is empty, the aggregates are incomplete, the sample succeeds and one warning `socket owners unknown` (`err`) is
logged at the first run; the state lasts until the agent restarts.

**The listener check.** The collector keeps the previous successful run's inventory in memory: its keys (at most 4,096, as the heap
holds them, not as the record's size budget may cut the list), whether it was cut, which tables were read and whether a line was skipped.
Each snapshot states the comparison its run made in `listener_check`:

- `start`: the first run after the agent starts, which has no previous inventory in memory (nothing is reported by the agent; the backend
  compares this snapshot with the agent's last one before the start, [Detection, alerts and reports](detection-and-reports.md), *New
  listeners*);
- `none`: a skipped table line in either run, a different set of tables read, or a cut inside the privileged part (nothing is reported and no
  full snapshot is taken for it); a failed run is not compared, the next successful run is compared with the last successful one;
- `privileged`: either inventory was cut at 4,096, so only the keys on ports below 1024 are compared; they come first in the inventory,
  so a flood of unprivileged listeners never cuts them;
- `all`: otherwise.

A key in the current inventory that the previous lacks is a **new listener**, except an unconnected UDP socket on a port from 32768 to 60999 (the kernel's
default local port range, fixed, not read from the server; a resolver's or client's temporary socket). A TCP listener is reported on any
port. At most 64 are reported, in inventory order, in `new_listeners`; the rest is the count `new_listeners_omitted`. A change of the owner on
the same protocol, address and port is not new; a listener that disappears and returns is new again. The current inventory becomes the
base, also after a `none` check, **only when the run's context has not ended**: the collector tests `ctx.Err()` immediately before it returns
its record and, when the deadline has passed, returns that error and keeps no inventory and no log state, so a run that becomes a gap
(the scheduler drops a result that arrives after the deadline) cannot make a listener known without a report. A size cut of `listeners` in the
record makes the aggregates incomplete but leaves the comparison alone.

**What a snapshot's own list holds.** Every snapshot also says which tables its run read (`tables`: the protocols `tcp`, `tcp6`, `udp`, `udp6`
in table order) and how much of the run's inventory its own `listeners` list holds (`inventory`), which the backend needs to compare two
snapshots of one agent: `skipped` when a table line was skipped; `cut` when the inventory was cut inside its privileged part or the list holds
fewer entries than the inventory has on ports below 1024; `all` when the inventory was not cut and the list holds every key; `privileged`
otherwise (every listener on ports 1 to 1023, not necessarily the rest). The state is set from the list as it finally stands, so a list the
size budget or the safety net halves is described as it is sent. Neither field changes the agent's own comparison, the full snapshots or the
caps.

**The record always fits and is valid.** The record is filled entry by entry within a payload budget of 1 MiB less 8 KiB of JSON (each entry
encoded alone to measure it), in the order `states`, `new_listeners`, `listeners`, `processes`, `remotes`, `connections`; at the first entry
that does not fit, that list and every later one stop (a cut in `states`, `listeners` or `processes` sets `aggregates_incomplete`). The
record is checked with the producer contract of the [wire format](wire-format.md); as a safety net a record still too long is shortened by
halving `connections`, then `remotes`, `processes` and `listeners`, and is marked not complete. Size never fails the sample. No text a
user chooses can: the `command` follows the process collector's rules.

**Logging.** One line per change, through the collector's logger with `collector=connections`, no per-run line, no line per new listener, no
process text and no address: info `socket tables unavailable` (`tables`: the missing ones, comma-separated, in table order) and `socket tables
available`; warning `socket lines not read` (`lines`: the number, `err`: `connstats: net/<table>: line <n>: <reason>` with a fixed reason
`not in the expected format`, `unknown state` or `line too long`) and info `socket lines read again`; warning `socket owners partly unknown`
(`processes`: the number of listed processes whose descriptors could not be read, `err`: `connstats: <pid>/fdinfo: <fixed reason or os error>`)
and info `socket owners known again`; warning `socket owners unknown` (above). The lines are written only for a run that finishes in time.

**Failures and rights.** The sample fails (a gap) when no table exists, a table cannot be read, `/proc` cannot be listed or the run's context ends.
It succeeds when a process or a descriptor cannot be read: the first `EACCES`, `EPERM` or other error on a process, or an `fdinfo` that does not
have the four-line shape (a file changed or damaged after the start-up probe succeeded), counts that process unreadable once, stops its scan,
and the sockets it held have no owner. Without `CAP_SYS_PTRACE` that is every process of another user, so the warning is logged. With `hidepid=1` and without the
capability the same. With `hidepid=2` and without the capability other users' processes are **not listed at all**, so their sockets have no owner
**without a warning** (stated residual). With the capability `hidepid=2` hides nothing (measured, *Rights*).

**Kernel before 5.14.** Its `fdinfo` has no `ino:` line (upstream `fs/proc/fd.c` at v5.13 has none, at v5.14 and v5.15 it follows `mnt_id:`), so
the start-up probe already fails (`sockmount: not in the expected format`), no `fdinfo` of any process is scanned and the collector logs
`socket owners unknown` (above), not `socket owners partly unknown`. The server runs Ubuntu 22.04 with 5.15 or a newer kernel, so this does not apply
there.

**Bounded memory.** The live data of a run is bounded by the caps: the inode index of 65,536 sockets (two 32-bit numbers each for owner and
last counting PID), two heaps of 4,096 entries and 4,096 remote counts. Measured by `TestConnections_ManySocketsStayWithinCaps` on Go 1.27
(Xeon 2.1 GHz, an in-memory file system) at two sizes of each axis: the live data at the run's peak (the inode index and the owner map alive) is 5.5 MB at
100,000 table lines and 4.8 MB at 400,000, and 0.75 MB at 50,000 and 200,000 descriptors of one process, so it does not grow with the input
beyond the caps. The **garbage** does grow: reading a table line leaves none (0 B per extra line), reading a descriptor through `io/fs` allocates a fixed
amount (about 170 B per extra descriptor, the test's generated file system included), and a run that reaches the caps allocates a fixed
about 24.7 MB more (about 9 MB of it the growth of the 65,536-entry index map), all reclaimed by the garbage collector. The 24.7 MB is
accepted; the live figure, not the garbage, is the bound.

**Speed.** `BenchmarkConnections_Collect` over the captured server tables with 300 processes of 30 descriptors takes 50.7 ms per run and
allocates 1.99 MB in 26,307 allocations ([Benchmarks](../BENCHMARKS.md), *Connection snapshot*); not measured on the server (#44).

**Residuals.**

- A listener that opens while the agent is stopped (an update, a crash, a reboot with a service that starts at boot) is not reported by the agent: the
  first run after a start is the baseline (`listener_check` `start`). The backend compares the stored snapshot with the agent's last one before
  the start and shows the result in the forensics report ([Detection, alerts and reports](detection-and-reports.md), *New listeners*, record
  0116); a listener that opened and closed while the agent was stopped is still never seen.
- A local user who keeps more than 4,096 listeners open (about 4,100 TCP listeners on unprivileged ports suffice) hides new listeners on ports
  from 1024 up for as long; the snapshots show `listener_check` `privileged`, and the flood alone causes no report and no full snapshot.
- A user who holds hundreds of thousands of descriptors lengthens the run in proportion; a run past its timeout is a gap and the collector is
  not started again while it lasts (*Collecting*). While runs are gaps no inventory is compared, so a listener that opens and closes during the stall
  is never reported: a user who keeps the collector stalled suppresses the check, visible as the gaps. The next run compares with the last that finished
  in time. A deadline that passes between the context check and the scheduler's receipt still keeps the inventory of a run that becomes a gap.
- Socket inode numbers come from a 32-bit kernel counter that pipes and other kernel files share. A user who allocates some four billion of them can
  hold a socket of their own outside the tables with the number of another socket and become one more holder of it, its owner only with a
  lower PID than every real holder. A user who runs more processes than the PID cap leaves the sockets of later PIDs without an owner, visible
  as incomplete aggregates.
- Short-lived TCP listeners such as an FTP data connection in passive mode (ProFTPD on Plesk without `PassivePorts`) are reported when a run happens to see
  one; how often is measured in the parallel run of #45. A server whose local port range differs reports UDP sockets outside the fixed range.
- The agent's totals include sockets without an owner (`TIME_WAIT`), which `lsof -ni` never listed, and `processes` counts the sockets a process
  owns on the descriptors the agent can read, so the numbers of the two sources differ for #45. Mapped addresses split a peer in `remotes`.
- The incident's connection peak is taken over every source in its window, so the agent's higher totals win it against imported snapshots
  ([Detection, alerts and reports](detection-and-reports.md)).
- A skipped table line (it is counted, not parsed) makes the snapshot's aggregates incomplete and the run's comparison `none`.

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
(10 seconds), `processes` (15 seconds) and `connections` (15 seconds) collectors run at their pressure intervals, `processes` lists every process and `connections` records a full snapshot.

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
- **Connection collector.** It reads another user's `/proc/<pid>/fdinfo/<n>` (`mnt_id:`, `ino:`, the socket's table inode) and uses
  `CAP_SYS_PTRACE` alone; it does **not** use `CAP_DAC_READ_SEARCH`, which would only be needed for the `/proc/<pid>/fd` links and lets the whole
  agent read every file (`/etc/shadow`, Plesk's credentials). Measured by Security on the development host (kernel 6.18.44, no Yama, no
  `hidepid`), as UID 65534 against a process of root:

  | Capabilities | list `fd/` | readlink `fd/3` | list `fdinfo/` | read `fdinfo/3` | read `/etc/shadow` |
  | ------------ | ---------- | --------------- | -------------- | --------------- | ------------------ |
  | none | `EACCES` | `EACCES` | `EACCES` | `EACCES` | `EACCES` |
  | `CAP_DAC_READ_SEARCH` | ok | `EACCES` | `EACCES` | `EACCES` | ok |
  | `CAP_SYS_PTRACE` | `EACCES` | `EACCES` | ok | ok (`mnt_id`, `ino` = the socket inode) | `EACCES` |
  | both | ok | `socket:[N]` | ok | ok | ok |

  `hidepid=2` hides no process from a reader that holds `CAP_SYS_PTRACE` (measured in a scratch mount namespace with `proc` mounted with
  `hidepid=2`); without the capability other users' processes are invisible. Without the capability the sockets of other users' processes carry
  no owner (*Connections*). A Yama or other LSM policy and another user namespace were not measured. The start-up probe's Unix datagram socket needs
  no right, but the unit must allow it: `RestrictAddressFamilies=` must keep `AF_UNIX` (the notification socket of *Service manager* needs it
  as well), and a system-call filter must not deny `socket` for it; when the probe is refused the collector logs `socket owners unknown` and
  records no owners. The unit gives the agent **no network namespace of its own** (no `PrivateNetwork=`, no `NetworkNamespacePath=`, or
  `/proc/net` would show another namespace's sockets), does not set `ProcSubset=pid` (it hides `/proc/net`) and leaves `ProtectProc=` at
  `default` (a stricter value hides other users' processes whenever the agent runs without `CAP_SYS_PTRACE`).
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
- [0115](../decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md) — why the
  connection collector reads the `/proc/net` tables with owners from `fdinfo` on a socket mount the agent learns at its start (`CAP_SYS_PTRACE`
  alone), records a regular or a full snapshot in the one record kind, and bounds the new-listener check, favoring privileged ports.
- [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md) — why the first snapshot after
  a start is marked `start`, every snapshot says what its listener list holds, and the backend compares across the start.

## Not here

- The format of batches and records: [wire format](wire-format.md).
- Which options the agent reads and how secrets are supplied: [Configuration and secrets](configuration-and-secrets.md).
- What the backend does with the data, including gap detection: the storage and detection areas.

## Implementation

`cmd/vandox-agent` and `internal/cli` (Go), `internal/agent` (start-up, logging, watchdog), `internal/scheduler`, `internal/selfstats`,
`internal/sysstats` (system metrics and the memory pressure mode; fixtures `testdata/proc/`, `testdata/sys/`),
`internal/procstats` (process collector; fixtures `testdata/proc/<pid>/`, `testdata/etc/passwd`, `testdata/processes/names.json`; `ListPIDs`, `CommandReader` and `Ended` are shared with the connection collector),
`internal/connstats` (connection collector; fixtures `testdata/proc/net/`, `testdata/connections/sandbox/`), `internal/sockmount` (the start-up probe of the socket mount),
`internal/sdnotify`, `internal/config`, `internal/model`, `internal/wire`; `deploy/agent/` (example configuration; the unit and install
scripts follow with the collectors).
