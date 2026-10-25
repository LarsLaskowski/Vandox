# Agent

## Scope

What `vandox-agent`, the program on the monitored server, must do and what it must never do: how it connects, what it reads and how,
how it survives a stalled data source and a missing backend, how gaps are recorded and which rights it runs with. The format of what it
sends is in the [wire format](wire-format.md); its configuration file is in [Configuration and secrets](configuration-and-secrets.md).

**Status.** The command line, the start-up with the configuration file, logging, the scheduler with its gaps and the memory pressure
switch, the self-metrics, the system metrics, the process collector with the memory pressure mode, the connection collector (with its start-up probe of the socket mount), the systemd service collector, the MariaDB collector, the kernel event collector with the boot check and the extra snapshots it requests, the log shipping collector (the journal and configured `syslog` files), the backup run check, the agent's state file, the on-disk spool and the notification of systemd exist, as do
the shared data model and the wire encoder, and the sender (*Spool and sending*, *Sending*). The other collectors and the systemd unit are **not implemented
yet**. The backend's ingest endpoint accepts the sender's batches ([Backend host](backend-host.md), *Ingest endpoint*); while the
backend is unreachable or not accepting, the sender waits and tries again (*Sending*), and the spool fills up to `spool.max_size` and
then drops its oldest records with recorded gaps. The
rules below for the parts not implemented are the target the implementation is held to and the tests must check.

## Command line and start-up

- `vandox-agent` without arguments runs the agent with the configuration file `/etc/vandox/agent.yaml`; `-config <path>` names another
  file. `-version` prints the version line and exits with 0; `-h` and `-help` print the usage and exit with 0. An unknown flag, a
  positional argument or an empty `-config` is a usage error: the usage on standard error, exit code 2.
- At start-up the agent reads the configuration file and the agent token as [Configuration and secrets](configuration-and-secrets.md)
  describes. When that fails it logs one error line with the loader's error text and exits with 1, before it notifies systemd of
  anything. It then opens its state file (*Agent state*) and, after it, its spool (*Spool and sending*); a spool that cannot be
  opened ends the start the same way, with the error line `agent cannot start`.
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
- The sender (*Sending*) is the one part that dials the backend: one `POST` of one batch to `<backend.url>/v1/batches` with the header
  `Authorization: Bearer <token>`. The agent token goes to `backend.url` only and nowhere else. The client uses **no proxy** (the
  environment's `HTTP_PROXY` and the like are not read), follows **no redirect** (a `3xx` is an answer that is not accepted, and the
  request, with the token, is never repeated at the `Location`), verifies the certificate of an `https` URL against the system roots
  (never skipped, TLS 1.2 at least), ends a request after 30 seconds, reads the header of an answer up to 16 KiB and at most 4,096 bytes
  of its body, which it discards.
- Plain `http` is meant only for the backend host's tailnet address or its MagicDNS name, because the tailnet encrypts and
  authenticates the transport; every other address is `https`. The scheme must match the listener: an `https` URL at a plain-http
  listener fails as `connection failed`, and an `http` URL at a TLS port gets a `400` without the refusal header (*Sending*), which
  counts as a backend that does not accept batches, so the agent only waits and gives no record up.
- Commands for the server (a later feature) are fetched by the agent from the backend, never pushed to it, so they reach the server
  only at the agent's next poll.
- The agent never contacts Telegram or any other service; all notification goes through the backend.
- Four local channels are not network connections and need no rule: the agent's log on standard error, which systemd passes to the
  journal, the notification datagrams it sends to the local socket systemd names (*Service manager*), the connection to the system
  bus socket `/run/dbus/system_bus_socket` that the `systemd` collector opens for each run (*Services*) and the connection to the
  MariaDB Unix socket (`mariadb.socket`, default `/run/mysqld/mysqld.sock`) that the `mariadb` collector opens for each run
  (*MariaDB*). The agent binds no socket for them.

## Collecting

- Process, memory and connection data come from **reading `/proc` and `/sys` directly**, never from running `top`, `lsof` or other
  programs: starting programs is slow and can fail under memory pressure, which is when the data matters. Collectors read the kernel
  files through a replaceable file-system interface, never through hard-coded paths, so tests can substitute fixtures and hanging
  files. Kernel messages and the log lines of the journal come from reading the journal's files directly, never from `journalctl`
  (*Kernel events*, *Log shipping*).
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
- A run learns whether the previous sample of its collector was lost (`Run.PreviousLost`): its result was not kept, because the run
  timed out, was skipped, returned an error or panicked, had a record refused, or the sink refused its records. A run that starts
  late does not set it. A run whose records were all kept and accepted clears it. A collector that records only changes uses it to
  record its full state again after a lost sample; the `kernel` collector reads the lost run's entries again and the `backups`
  collector decides the lost check again (*Kernel events*, *Backup runs*); the `logs` collector counts a lost run it was not told
  about (below) as dropped (*Log shipping*); the other collectors ignore it.
- A collector may ask to be **told whether a run was kept** (an observer): after the delivery of a run that finished in time the
  scheduler calls it with the run and whether its records were kept — true when the run returned no error and did not panic, no
  record was refused and the sink accepted the records, false otherwise. An abandoned or skipped run calls nothing; its loss shows
  only in the next run's `Run.PreviousLost`. The `logs` collector is the observer (*Log shipping*).
- An **extra run** of a collector is requested through the scheduler (the `kernel` collector does so for `processes` and
  `connections`, *Kernel events*). A runner waiting for its next due time starts it at once; a runner in a run starts it when that
  run ends. Requests made before the extra run starts merge into one, which names at most 64 kills and counts the rest. The extra run
  starts at the moment it begins and knows its request (`Run.Trigger`); in every other respect it is a run like any other: it is the
  next run's predecessor, it sets and clears `Run.PreviousLost`, and a timeout, a panic or an error records a gap over one interval
  of the current mode from its start (`collector_timeout`, `unknown`), after which an abandoned extra run makes the due runs skips as
  above. While an abandoned run of the collector is still out, no extra run starts: the request records a gap with cause
  `collector_timeout` from that moment over one interval of the current mode. Without memory pressure mode, or for a collector without
  a pressure interval, the grid is unchanged; under pressure mode a collector with a pressure interval counts its next due time from
  the extra run's start, so the extra run takes the place of the next regular one.
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
- The records that passed these checks go to the spool, which assigns the sequence numbers (*Spool and sending*). A hand-over is
  never refused because the agent's context ended, so a stop does not lose the records of a run that finished.

## Self-metrics

The collector `self` (interval 60 seconds, timeout 10 seconds) records the agent's own resource use as ordinary `metric` records with
source `agent`, through the scheduler like every collector: its resident memory, its CPU time, its number of goroutines, the size of
the spool's files (`agent_spool_bytes`), the age of the oldest record the spool holds unacknowledged (`agent_spool_age_seconds`), and —
how far sending lags behind capture (`agent_send_lag_seconds`, from the sender). The names, units and values are in the
[wire format](wire-format.md) (*Metric names*, the `agent_*` rows).

The resident memory is read from at most 64 KiB of the agent's own `/proc` status file, and its line must be `VmRSS:`, spaces or tabs,
1 to 12 ASCII digits, one space and `kB`; anything else, a failed read, a negative spool size, an error of the spool's size or age and
a negative send lag fails the sample, which then becomes a gap. The spool's age is the sample's start minus the capture time of the
oldest unacknowledged record; it is 0 when the spool holds none and when that capture time lies after the sample's start (a clock set
back), never negative.

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
  first). Under pressure mode (the run knows it, *Scheduling*) and in an extra run requested for an OOM kill (*Kernel events*) it lists
  **every process read**; the extra run's record names the kills in `trigger`. `total` is the number of processes seen
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
`/proc/<pid>/fdinfo/<n>` (the first lines only), what the process collector reads to derive a `command` (`stat`, `cmdline`) and, once per
run, `/etc/passwd` for user names (with the process collector's bounds, *Processes*), through the replaceable file-system interface. It starts no program, opens no socket and makes no raw system call; `TestConnstats_ImportsOnlyAllowedPackages`
pins the imports (`os` only for `DirFS`, no `syscall`). The one socket the agent opens for it is the start-up probe below, outside the collector.

**Terms.** A **socket** is one line of the four tables of the agent's network namespace (`/proc/net` is the reader's own namespace);
two lines are two sockets, also when both have inode 0 (`TIME_WAIT`, `SYN_RECV`), and such a line never matches a descriptor. A
**listener** is a TCP socket in `LISTEN` or a UDP socket whose remote address is all zeros with port 0; a **connection** is a socket with a
non-zero remote address or port; any other TCP socket counts in `states` only. The **owner** of a socket is the lowest PID among the
processes that hold it. The **socket's UID** is the `uid` field of its table line: the UID of the socket, not of a process that holds it.
The kernel gives a socket the file-system UID of the process that creates it (`sock_alloc`, `current_fsuid()`), and only an `fchown` of the
socket changes it, which needs `CAP_CHOWN` to give the socket to another user. So a daemon that creates its listener as root and then
switches its effective user to `www-data` (`nginx`, `apache2`), a socket that systemd creates for a socket-activated service and any socket a
root process hands on read UID 0, while the process snapshot shows the holder's own user.

**Tables.** `tcp`, `tcp6`, `udp`, `udp6` are read in that order, line by line; the first line is the header. A line longer than 512 bytes is
skipped to its end and counted unreadable. A line needs at least 10 fields: `sl` (digits and `:`), the local and remote endpoint (8 upper-case
hex digits for IPv4 and 32 for IPv6, `:`, 4 upper-case hex digits), the state (2 upper-case hex digits), the `uid` (field 8, 1 to 10
digits within 32 bits; the kernel prints it as an unsigned decimal, so `0`, `65534`, the overflow UID of a UID without a mapping in the
agent's user namespace, and `4294967295` are read as printed) and the inode (field 10, 1 to 20 digits within 64 bits); the fields between
and after are not interpreted. A `uid` field with a sign, a letter or more than 32 bits makes the line unreadable (`not in the expected
format`), like a bad inode; this also makes `connstats.TCPListeners` refuse the table (*MariaDB*). Each address word is read as a 32-bit number and written in the
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
every counted remote address. The first run after the agent starts, every run under memory pressure (the mode at the run's start, *Scheduling*),
every extra run requested for an OOM kill (*Kernel events*; its record names the kills in `trigger`) and every run that finds at least one
new listener, reported or omitted, is full; every other run is regular. `complete` keeps its meaning of
the wire format and is true only for a full snapshot in which nothing was cut, skipped, left without an owner beyond a cap, unread or
uncounted, so a regular snapshot is never complete. `aggregates_incomplete` is set when `states`, `listeners` or `processes` may not cover
every socket the run saw: a list cut for size or at its cap, a skipped table line, a socket beyond the inode index, more processes than the
PID cap, a process whose descriptors could not be read, or an owner scan that did not run (below). Imported legacy snapshots and an older agent's
carry none of the new fields and read as full with complete aggregates. A regular snapshot's top 25 `remotes` are over the counted addresses
only: an address first seen after the 4,096 are counted, however heavy, is missing from it (a full snapshot says so through `complete`).
`remotes` does not unmap IPv4-mapped addresses, so one IPv4 peer connected to a `0.0.0.0` and to a `::` listener is two entries.

**Owners.** The agent does not read the `/proc/<pid>/fd` links, because listing them needs `CAP_DAC_READ_SEARCH`, which the agent does not
hold (*Rights*). For every
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
PID is the kernel's.

**Socket UIDs.** `listeners`, `new_listeners` and `connections` entries carry the socket's UID (`uid`) and its user name (`user`), taken
from the table line when the line is filed as a listener or a connection, not from the descriptors or the inode index. So an entry
without an owner still has them: when the start-up probe failed, `fdinfo` cannot be read, `hidepid=2` hides the holder, the holder lies
beyond the PID cap, or the socket lies beyond the inode index or repeats an inode it already holds. A line with inode 0 (`TIME_WAIT`, a
`FIN_WAIT2` or other socket whose file is closed, a pending `SYN_RECV`) adds no UID: the kernel prints 0 for a time-wait socket, the
listener's UID for a pending one and, for an orphaned one, root (5.15) or the UID the socket had (6.x); none of it is a socket a process
holds, and 0 would read as root. An entry built only from such lines has no `uid`, no `user` and no `mixed_uids`. An inventory entry is
one endpoint of possibly several sockets: it carries the **lowest** UID of its sockets with an inode, and `mixed_uids` true when they have
two or more distinct UIDs (sockets bound to different devices with `SO_BINDTODEVICE`, which any user may set once and which lets sockets of
different users share an endpoint, TCP and UDP alike; UDP sockets bound with `SO_REUSEADDR`; a `SO_REUSEPORT` group of which one socket was
given to another user by an `fchown`); its `pid` stays the lowest owning PID and may belong to another of the sockets. A `new_listeners`
entry is built from the same inventory entry, so it carries the same `uid`, `user` and `mixed_uids`. The `processes` aggregate carries no
UID (the sockets of one process can have several; its user is in the process snapshot). The `user` is `procstats.UserName` over the names
`procstats.ReadUsers` reads from `/etc/passwd` once per run, after the owner scan: the process collector's rules (*Processes*, *Readers*:
the first line of a UID wins, a name is made valid UTF-8 and cut to 256 bytes, control characters are kept and escaped by the encoder),
else the UID in decimal, so one UID has one name in both snapshots of a run that read the same file. A `passwd` that cannot be read within
its bounds (missing, an open or read error, over 1 MiB, a line over 4 KiB) makes every `user` the decimal UID; `uid` is unchanged and the
sample succeeds. A change of the UID on a known protocol, address and port is not a new listener (*The listener check*).

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
user chooses can: the `command` and the `user` follow the process collector's rules; a 256-byte name of control characters encodes to at
most 1,536 bytes, so long names mean fewer entries before the cut, marked as above, never a failed sample.

**Logging.** One line per change, through the collector's logger with `collector=connections`, no per-run line, no line per new listener, no
process text, no address, no user name and no UID value: info `socket tables unavailable` (`tables`: the missing ones, comma-separated, in table order) and `socket tables
available`; warning `socket lines not read` (`lines`: the number, `err`: `connstats: net/<table>: line <n>: <reason>` with a fixed reason
`not in the expected format`, `unknown state` or `line too long`) and info `socket lines read again`; warning `socket owners partly unknown`
(`processes`: the number of listed processes whose descriptors could not be read, `err`: `connstats: <pid>/fdinfo: <fixed reason or os error>`)
and info `socket owners known again`; warning `socket owners unknown` (above); warning `user names unavailable` (`err`: `procstats: passwd:
<fixed reason or os error>`, as `procstats.ReadUsers` returns it, never file content) and info `user names available` (*Socket UIDs*), both
also while `socket owners unknown` holds. The lines are written only for a run that finishes in time.

**Failures and rights.** The sample fails (a gap) when no table exists, a table cannot be read, `/proc` cannot be listed or the run's context ends.
It succeeds when `/etc/passwd` cannot be read (*Socket UIDs*) and when a process or a descriptor cannot be read: the first `EACCES`, `EPERM` or other error on a process, or an `fdinfo` that does not
have the four-line shape (a file changed or damaged after the start-up probe succeeded), counts that process unreadable once, stops its scan,
and the sockets it held have no owner. Without `CAP_SYS_PTRACE` that is every process of another user, so the warning is logged. With `hidepid=1` and without the
capability the same. With `hidepid=2` and without the capability other users' processes are **not listed at all**, so their sockets have no owner
**without a warning** (stated residual). With the capability `hidepid=2` hides nothing (measured, *Rights*).

**Kernel before 5.14.** Its `fdinfo` has no `ino:` line (upstream `fs/proc/fd.c` at v5.13 has none, at v5.14 and v5.15 it follows `mnt_id:`), so
the start-up probe already fails (`sockmount: not in the expected format`), no `fdinfo` of any process is scanned and the collector logs
`socket owners unknown` (above), not `socket owners partly unknown`. The server runs Ubuntu 22.04 with 5.15 or a newer kernel, so this does not apply
there.

**Bounded memory.** The live data of a run is bounded by the caps: the inode index of 65,536 sockets (two 32-bit numbers each for owner and
last counting PID), two heaps of 4,096 entries (each entry with one 32-bit UID more), 4,096 remote counts and the user names of
`/etc/passwd`, which its 1 MiB bound limits: a file of the shortest lines a name and a UID allow (`a::N`) holds at most 115,968 distinct
users. Measured by `TestConnections_ManySocketsStayWithinCaps` on Go 1.27 (an in-memory file system) at two sizes of each axis: the live
data at the run's peak (the inode index, the owner map and the user map alive) is 4.7 to 5.6 MB at 100,000 and 400,000 table lines with
the fixture `passwd` (18 lines) and 9.4 to 10.3 MB at 100,000 and 200,000 table lines with a 1 MiB `passwd` of 115,968 short lines, so
the user map adds at most about 4.6 MB; 0.75 MB at 50,000 and 200,000 descriptors of one process. It does not grow with the input beyond
the caps. A process collector run at the same time holds its own map of the same file. The **garbage** does grow: reading a table line
leaves none (0 B per extra line, also with the `uid` field read), reading a descriptor through `io/fs` allocates a fixed amount (about
170 B per extra descriptor, the test's generated file system included), a run that reaches the caps allocates a fixed about 24.7 MB more
(about 9 MB of it the growth of the 65,536-entry index map), and reading a 1 MiB `passwd` allocates about 20 MB per run, constant, not
per table line, all reclaimed by the garbage collector. The garbage is accepted; the live figure, not the garbage, is the bound.

**Speed.** `BenchmarkConnections_Collect` over the captured server tables with 300 processes of 30 descriptors, the fixture `passwd`
included, takes 57.8 ms per run and allocates 2.02 MB in 26,484 allocations; the tree before the socket UIDs, on the same host and day,
took 57.3 ms with 1.99 MB in 26,310 allocations, so the `passwd` read and the UIDs cost no measurable time ([Benchmarks](../BENCHMARKS.md),
*Connection snapshot*); not measured on the server (#44).

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
- The `uid` says whose socket it is, not whose process: a root-created socket held by another user's process reads root. A user who binds
  a second socket on a known endpoint (for example bound to a device) leaves the entry's `uid` at the lower UID with `mixed_uids` true, and
  the endpoint is not reported as a new listener, as a change of owner is not; a listener whose UID changes between two runs is not
  reported either. Names come only from `/etc/passwd` (no NSS, LDAP or `systemd-userdbd` dynamic users), so such a UID reads as a number.

## Services

The collector `systemd` (interval 30 seconds, pressure interval 10 seconds, timeout 5 seconds, placed after `connections` in the job list)
records the state of the tracked systemd service units as `service_state` records with source `systemd` and the capture time of the
run's start ([wire format](wire-format.md)). It runs only when `services.units` holds at least one entry (default: the Plesk services,
[Configuration and secrets](configuration-and-secrets.md)); with `services.units: []` it does not run, records nothing, logs nothing and
the agent never connects to the system bus. It starts no program (record 0019): it asks systemd over D-Bus, as `systemctl` does.

**What it reads.** Each run opens its own connection to the system bus socket `/run/dbus/system_bus_socket` (a constant; no environment
variable names it), authenticates as the agent's user, says `Hello` and closes the connection at its end. It calls read methods only,
all with `NO_AUTO_START`: `ListUnitsByPatterns` (only when a pattern is configured), `ListUnitsByNames` for the tracked names in calls of
at most 64 (systemd loads each named unit, resolves an alias to its primary name and answers primary name, load, active and sub state in
one consistent entry), and `Properties.Get` of `ActiveEnterTimestamp` and `NRestarts` on each unit, except for a unit that is `not-found`
and `inactive`, which has neither (they count as 0). About 25 round trips for the default list, about 520 at the cap. Reading needs no
group and no capability: systemd's bus policy lets every user make these calls.

**Which units are tracked.** An entry without a pattern character is tracked from the start; an entry with `*` or `?` is matched by
systemd against the units it has loaded, and each match whose `load_state` is not `not-found` is tracked from the first run that lists it.
A unit once tracked stays tracked until the agent restarts, even when systemd unloads it, so its stop is recorded. At most 256 units are
tracked: the exact entries first in file order, then the matches in name order; a unit once tracked is never dropped for a later match,
and a new match beyond the cap is left out. A name the wire format refuses (systemd's `\x2d` escapes) is not tracked. A pattern that
matches nothing is no error. Two entries or a pattern that name one unit (an alias, `mysql` and `mariadb`) give one unit, recorded once
under systemd's primary name.

**What is recorded and when.** One record per unit with the primary name, `load_state`, `active_state`, `sub_state`, `active_enter_at`
and `restarts` (systemd's `NRestarts` as reported, 0 for a `not-found` or masked unit; masked, `bad-setting` and `error` units are
recorded like any other). `active_enter_at` is left out when systemd reports 0 (never active) or a time the backend cannot store
(after 2262-04-11), never clamped. The first successful run after a start records every tracked unit. A later run records a unit when
its `load_state`, `active_state`, `sub_state`, `active_enter_at` or `restarts` differs from the unit's last recorded state, or when its
last record is at least one hour old, so a unit's records are at most one hour plus one interval apart while the collector succeeds (an
incident window shorter than that may hold no record of a quiet unit). After a lost sample (*Scheduling*, `Run.PreviousLost`) the next
successful run records every tracked unit again, as after a start, so a change seen only by the lost sample is not missing. A state that
came and went between two runs without a new `active_enter_at` or a higher `restarts` (a short `activating` or `reloading`) is not seen,
and a change is recorded at most one interval after it happened; the second of a failure is in the journal.

**A unit the wire format refuses.** The collector checks every unit's record against the model itself before it returns the run's
records. A unit whose state is refused (an `active_state` or `load_state` a newer systemd adds, a `sub_state` with other characters than
`[a-z0-9-]` or longer than 64 bytes) is left out and its last record forgotten; the run's other records are kept, and the run returns
one gap with cause `unknown`, collector `systemd`, from the run's start to the run's start plus the interval of the run's mode. Each such run is a gap,
because the unit counts as not recorded. The scheduler therefore never sees a refused record. The warning `systemd unit state not
recorded` (`unit`: the tracked name, `field`: the name of the field) is logged at the first such run and the info line `systemd unit
state recorded again` when the unit is recorded again; the refused value is never logged.

**Logging.** Through the collector's logger with `collector=systemd`, no line per run and none per state change, and only
fixed texts, counts and names that passed the configuration or wire rules; no text a reply carries is logged beyond a unit name:

| Line | Attributes | When |
| ---- | ---------- | ---- |
| warning `systemd units not found` | `count`, `units` (the exact entries whose `load_state` is `not-found`, comma-separated in file order) | at the first successful run after a start, when there is one; not repeated after a lost sample; not for patterns, masked or broken units; a unit installed or removed later is recorded without a line |
| warning `systemd units cut` | `left_out` | once per agent run, at the first run that leaves a match out because of the cap |
| warning `systemd units not tracked` | `count` | once per agent run, when a match has a name the wire format refuses |
| warning `systemd unit state not recorded`, info `systemd unit state recorded again` | `unit`, `field` | as above |

**Failures.** A bus that cannot be reached (socket missing, connection refused), an authentication that is refused, an error reply to one
of the agent's calls, a reply or message the client refuses and a reply larger than 1 MiB fail the run: no record and no missing-unit
warning is written, the sample is a gap with cause `unknown` and the scheduler logs `collector failed` once and `collector recovered`
(*Scheduling*). The run's context is the connection's deadline, so a systemd or bus daemon that does not answer ends the run at its
5-second timeout as a gap with cause `collector_timeout`, the collector is not started again while it is stuck, and no other collector
waits. After `daemon-reexec`, `daemon-reload` or a restart of the bus daemon the next run connects anew; at most the runs while the bus
is away are gaps. The failure texts are `sdbus: connect: <os error>`, `sdbus: authentication refused`, `sdbus: message: <fixed reason>`,
`sdbus: <member>: error reply <error name>` (the name only when it follows the D-Bus error-name grammar and is at most 255 bytes),
`sdbus: <member>: reply too large`, `sdbus: <member>: unexpected signature` and `sdbus: <member>: malformed body`; none contains a
string of a reply body.

**The client (security area 10).** The bus delivers messages to the agent that it did not ask for: any local user may send a signal to
the agent's connection, up to 32 MiB and with file descriptors, and the replies are text systemd and the bus daemon choose. The client in
`internal/sdbus` is therefore written to bound what it reads before it decodes anything:

- It reads and writes only through `Read`, `Write`, `SetDeadline` and `Close` of the connection and never negotiates file-descriptor
  passing, so the kernel closes any descriptor sent to it. The imports and selectors are pinned by `TestSdbus_ImportsOnlyAllowedPackages`
  (no `ReadMsgUnix`, `ReadFrom*`, `SyscallConn`, `File`, `syscall`, `os.Getenv`, `os.LookupEnv`).
- Authentication is `EXTERNAL`; the answer line is cut at 512 bytes before it is compared and only `OK` and 32 hexadecimal digits are
  accepted.
- A message is refused when the byte order is unknown, the version is not 1, or the message is larger than 2^27 bytes. Every message
  that is not a method return or error (signals, method calls, unknown types) is skipped by its length without a look at its header
  fields, in a fixed buffer, so a flood of them keeps nothing in memory. A return or error is read only with header fields of at most
  64 KiB, of which only the codes 1 to 9 are decoded, each with its one fixed basic type; any other code or type fails the call. A
  return or error for another call is skipped after its header fields.
- The awaited reply's body is read only up to 1 MiB and its signature must be exactly the expected one (`s`, `a(ssssssouso)`, `v`
  holding `t` or `u`); strings must be NUL-terminated valid UTF-8 within the body, object paths are checked against the D-Bus path
  grammar before they are used in a call, and the answer of `ListUnitsByNames` must hold exactly one entry per name. The body of an error
  reply is never read into a text.
- Memory is bounded independently of the server: at most 256 tracked units with their last recorded state, one connection, one reply of at
  most 1 MiB.

**Residuals.**

- A local user who learns the agent's unique bus name during a run can flood that connection with signals; the run discards them until
  its deadline and becomes a gap, visible as such, and no memory grows (record 0117).
- Reading a unit systemd has not loaded makes systemd load and unload it for the call and so broadcast `UnitNew` and `UnitRemoved` on the
  bus; that is one pair per missing unit and run (`ListUnitsByNames` needs no further `Get` for it).
- A local user can make systemd load a stub unit that a pattern matches; a match listed as `not-found` is not tracked.
- The unit must keep `AF_UNIX` and the bus socket reachable (*Rights*). The captured replies of systemd 249 are the test data; other
  systemd versions were not captured.

## MariaDB

The collector `mariadb` (interval 30 seconds, pressure interval 10 seconds, timeout 5 seconds, placed after `systemd` in the job list)
records the state of the local MariaDB server as one `mariadb_status` record per run and, for an `up` run, its `mariadb_*` metrics, all
with source `mariadb` and the capture time of the run's start ([wire format](wire-format.md)). It runs while `mariadb.enabled` is true
(the default, [Configuration and secrets](configuration-and-secrets.md)); with `mariadb.enabled: false` it is not scheduled: no record,
no log line, no connection. It starts no program and uses no client library: `internal/mariadb` speaks MariaDB's client protocol
itself (record 0118).

**What a run does.** Each run opens one connection to the Unix socket `mariadb.socket` (default `/run/mysqld/mysqld.sock`), reads the
greeting, logs in as `vandox-agent` (*The login*) and closes the connection at its end with `COM_QUIT`, sent with a write deadline of
250 ms and not waited for. In between it sends, in this order: one `COM_PING`, whose round trip on the monotonic clock is
`ping_latency_ns`; `SHOW GLOBAL STATUS`; `SHOW GLOBAL VARIABLES`; the two statements of the account check, `SELECT CURRENT_USER()` and
`SELECT PRIVILEGE_TYPE FROM information_schema.USER_PRIVILEGES WHERE GRANTEE LIKE '%vandox-agent%'`; and `SHOW FULL PROCESSLIST` only when
the status value `Threads_running` is above 10 (the agent's own statement counts) or the run started in memory pressure mode
(*Scheduling*). No other statement is ever sent, none takes input, and the agent holds at most one connection at a time: a run's
connection is closed before it returns, and the scheduler never starts the collector while a run is stuck. After the connection the run
reads `MemTotal` of `/proc/meminfo` and the TCP tables `/proc/net/tcp` and `tcp6`, as far as the metrics need them (*Metrics*).

**Time budget.** The greeting must arrive by 2 seconds after the run's start, and everything else by 4 seconds after it: every read
and write on the connection carries that deadline, and the end of the run's context ends every wait at once. The scheduler's 5-second
timeout is only the backstop for the reads of `/proc`, which no deadline can interrupt: a read that blocks there is abandoned as a
`collector_timeout` gap for `mariadb`, no second connection is opened while it is stuck, and no other collector waits.

**Availability.** Every run that reaches a verdict writes one `mariadb_status` record with `complete` false:

- `down`, and nothing else: connecting fails with `ENOENT` (no socket file: MariaDB is stopped or not installed) or `ECONNREFUSED` (a
  socket file nobody accepts on, as a killed server leaves it, or a path that is no socket).
- `not_answering` without `error_code` and without values: connecting fails with `EAGAIN` (the server's listen queue is full) or does
  not finish by 4 seconds, no greeting arrives by 2 seconds, the login, the ping or any statement gets no answer by 4 seconds, or the
  server closes or resets the connection at any point, also in the middle of a result. A run never writes part of the values.
- `not_answering` with `error_code` and without values: the server sends an error packet in place of its greeting (1040 when it has no
  free connection, 1053 while it shuts down) or as the answer to the login (1698 or 1045 when the user `vandox-agent` is missing or not
  identified via `unix_socket`, 1226 at the user's connection limit), any number from 1 to 65535. An error packet with number 0 is
  recorded without `error_code`.
- `up`: the login, the ping, both selection statements and the account check were answered (an error answer to the check counts,
  *Account check*), and the process list too when it was due. The record holds `ping_latency_ns`, `status`, `variables` and, when the
  process list was read, `threads`.

Any other outcome fails the run (*Failures*).

**The login.** The greeting is at most 1 KiB and must be protocol 10 with a NUL-terminated version text (never interpreted, so the
`5.5.5-` prefix of older servers does not matter), the capabilities `CLIENT_PROTOCOL_41`, `CLIENT_SECURE_CONNECTION`,
`CLIENT_PLUGIN_AUTH` and `CLIENT_SESSION_TRACK`, capability bit 0 clear (a MariaDB server, not MySQL), and MariaDB's extended
capabilities extended metadata (`0x08`) and cache metadata (`0x10`); anything else fails the run with `unexpected reply`. The agent
answers with the capabilities `0x00aea204` (the captured client's without `CLIENT_LOCAL_FILES`, `CLIENT_MULTI_STATEMENTS` and
`CLIENT_CONNECT_ATTRS`; no `CLIENT_DEPRECATE_EOF`, no TLS), the extended capabilities `0x18`, a maximum packet of 1 MiB, character set
45, the user `vandox-agent`, **empty authentication data** and the plugin name `mysql_native_password`. MariaDB's `unix_socket` plugin
compares the user name with the OS user of the connecting process, so no password exists and none is ever sent. The server's answer:

- OK: logged in.
- An error packet: `not_answering` with its number (*Availability*).
- An authentication switch request (`0xfe` with a plugin name, at most 1 KiB, else `packet too large`; neither the plugin name nor its
  data is kept or logged): answered once with an empty packet, never a credential, and the next packet counts as the login's answer.
  A second switch request, or any request for more authentication data (`0x01`) after the first, fails the run with `authentication
  switch refused`.
- A request for more authentication data (`0x01`) of more than 2 bytes before any switch also fails the run with `authentication switch
  refused`; one of 1 or 2 bytes, an empty packet, a `0xfe` packet without a plugin name and any other first byte fail it with
  `unexpected reply`.

**The selection.** Of `SHOW GLOBAL STATUS` the record keeps those of these 14 status values the server reports: `Uptime`,
`Threads_connected`, `Threads_running`, `Max_used_connections`, `Aborted_connects`, `Aborted_clients`, `Questions`, `Slow_queries`,
`Created_tmp_disk_tables`, `Innodb_buffer_pool_pages_total`, `Innodb_buffer_pool_pages_free`, `Innodb_buffer_pool_read_requests`,
`Innodb_buffer_pool_reads`, `Memory_used`; of `SHOW GLOBAL VARIABLES` those of these 20 variables, as text: `version`,
`max_connections`, `innodb_buffer_pool_size`, `innodb_log_buffer_size`, `key_buffer_size`, `aria_pagecache_buffer_size`,
`query_cache_size`, `tmp_table_size`, `max_heap_table_size`, `sort_buffer_size`, `read_buffer_size`, `read_rnd_buffer_size`,
`join_buffer_size`, `thread_stack`, `binlog_cache_size`, `log_bin`, `bind_address`, `skip_networking`, `port`, `extra_port`.

- A row's name is read up to 64 bytes and its value up to 1,025 bytes; the rest of a longer cell is discarded by length. A name is
  compared exactly (case-sensitive) with the 34 names of the selection; every other name is neither kept nor remembered, so a reply of
  any length keeps at most 34 values. A selected name that appears twice in one reply fails the run (`name sent twice`).
- A status value is kept when it is 1 to 20 ASCII digits within an unsigned 64-bit number; a variable when its value was not cut and
  is at most 1,024 bytes once cleaned (every byte that does not start a valid UTF-8 sequence becomes U+FFFD). Any other value is left
  out of its map and the rest of the record is written.

**Thread snapshot.** When the process list is due, `threads` holds every row of `SHOW FULL PROCESSLIST` except the agent's own
connection (the connection id of the greeting), each with `id`, `user`, `host`, `db`, `command`, `state`, `time_seconds`, `info` (the
statement text) and `truncated`. Order: threads whose `command` is not `Sleep` first, then the sleeping ones, each group by
`time_seconds` descending, then `id` ascending.

- The cells of a row are bounded on their raw bytes before anything is kept: `Id` and `Time` 20 bytes and `Progress` 64 bytes, strict
  (a longer cell fails the run with `cell too long`); `User` and `Host` 512 bytes, `db` and `State` 256, `Command` 32 and `Info` 16,384,
  cut (the rest is discarded by length). `Id` must be 1 to 20 ASCII digits within 64 bits, anything else, NULL included, fails the run
  with `unexpected reply`, so the agent's own row is always recognized; a `Time` that is not 1 to 20 digits reads 0; a NULL text is
  empty.
- At most 4,096 threads are kept, the first in the order (a later row that ranks before the last kept thread takes its place), and
  only the first 64 in the order keep their statement; a kept thread that had a statement but is not among them gets an empty `info`
  with `truncated` set.
- When the record is built, every text is cleaned as the wire format's text rules want it: every byte that does not start a valid
  UTF-8 sequence becomes U+FFFD, the text is cut at a character boundary to 1,024 bytes (`user`, `host`, `db`, `command`, `state`) or
  16,384 bytes (`info`, with `truncated` set when it or its raw cell was cut), and control and format characters are kept as the
  server sent them. These texts are chosen by whoever sends SQL to the server (any web-space user); they reach the record only, never a
  log line or an error text, and whoever displays them escapes them like log text. They can hold customer data and credentials
  (`IDENTIFIED BY '...'`, the values of an `INSERT`), which are sent to the backend and shown there; `deploy/agent/README.md` says so.

**The record always fits.** The collector bounds the record's encoded line by an estimate made before anything is encoded: 6 bytes
per byte of every kept text (the longest JSON escape, `\u00XX`), 21 bytes per number, 8 per map entry, 256 per thread for its keys and
punctuation and 1 KiB for the record, which is never less than the encoded size. Threads that cannot fit within 1 MiB even without a
statement (by the estimate of their raw short texts) are not built at all. Then, while the estimate is above 1 MiB, every `info` is cut
to 4,096, then 1,024, then 0 bytes (each cut sets `truncated`), and then the thread list is halved from its end, so idle sleepers go
first. The scheduler's check of the record (*Scheduling*) therefore passes; `complete` is always false, because the record holds a
selection.

**Bounded memory.** A run keeps of the server's replies only one scratch buffer per cell position, the 34 selected values and, for
the process list, at most 4,096 thread slots of 1,568 bytes for the short texts and 64 statement buffers of 16 KiB (about 7.2 MiB),
which are reused once full, so no row allocates beyond them. Every packet that is not a row is read whole only up to its limit (the
greeting 1 KiB, an OK, error, EOF, column count or column definition packet 64 KiB) and is refused above it with `packet too large`; a
row is read cell by cell. The tests pin the bound: a run over 4,200 threads whose every kept field is at its cap and filled with `<`,
control characters and invalid bytes, with statements of 20 KiB, allocates at most 32 MiB in total and gives a record line of at most
1 MiB (`TestCollect_WorstCaseProcessListIsBounded`); a status or variables reply of 1,000,000 distinct names allocates at most 4 MiB
(`TestCollect_LongStatusAndVariablesRepliesAreBounded`); a cell of 64 MiB is discarded with at most 4 MiB allocated
(`TestPacketReader_DiscardsBeyondTheLimitWithBoundedMemory`).

**The client's protocol rules (security area 10).** A packet has a 3-byte length and a sequence number that must increase by one modulo
256 (else `packet out of sequence`); a payload of `0xffffff` bytes is continued in the next packet. Length-encoded integers take the
prefixes `0xfc`, `0xfd` and `0xfe`, `0xfb` is NULL in a row, and `0xff` or a length beyond its packet is `malformed packet`. A statement
must be answered by an error packet or by a column count packet of exactly two bytes: the number of columns the statement has and the
"metadata follows" byte 1 (else `unexpected reply`; another number is `unexpected columns`). Each column definition's name (its fifth
length-encoded string) must be exactly the expected one (else `unexpected columns`): `Variable_name`, `Value`; `Id`, `User`, `Host`,
`db`, `Command`, `Time`, `State`, `Info`, `Progress` (so MySQL, whose process list lacks `Progress`, fails the run); `CURRENT_USER()`;
`PRIVILEGE_TYPE`. Then an EOF packet, the rows and an EOF packet; an EOF packet with `SERVER_MORE_RESULTS_EXISTS` is `more results
refused`. An answer `0xfb` (a request to send a local file) is `local file request refused`, and nothing is read from or sent for it; an
OK packet or any other first byte is `unexpected reply`. The server's error message is skipped by its length and never read into a text.

**Account check.** A login alone does not prove that the agent got the account `deploy/agent/mariadb-user.sql` creates: an anonymous
account `''@'localhost'` can accept it, and without `PROCESS` the process list shows only the agent's own connection, so an empty
`threads` would look normal. Every `up` run therefore asks the two questions of *What a run does*; both need no privilege, and an account
sees only its own rows of `USER_PRIVILEGES`. The check passes when `CURRENT_USER()` answers exactly one row `vandox-agent@localhost` (the
cell read up to 1,024 bytes; a cut one does not match) and one row of the privileges is `PROCESS` (read up to 64 bytes). When it fails,
the run's record is written as usual, and the warning `mariadb user not as installed` is logged at the first such run after the agent
started or after a run whose check passed. When the server answers either question with an error, the check has no result: the record
is written, nothing is logged and the state does not change; the agent closes the connection after that error (what is left of the
reply could not be told apart from the next one), so a process list due in that run is not read and the record has no `threads`.
No answer in time is `not_answering`, as for every statement.

**Metrics.** An `up` run writes after its `mariadb_status` record these `metric` records (names and units in the
[wire format](wire-format.md), *Metric names*), each only when its inputs are there:

- `mariadb_memory_max_bytes`: the theoretical maximum, *global buffers* + `max_connections` × *per-connection buffers*. The global
  buffers are `innodb_buffer_pool_size` + `innodb_log_buffer_size` + `key_buffer_size` + `aria_pagecache_buffer_size` +
  `query_cache_size` + the smaller of `tmp_table_size` and `max_heap_table_size`; the per-connection buffers are `sort_buffer_size` +
  `read_buffer_size` + `read_rnd_buffer_size` + `join_buffer_size` + `thread_stack`, plus `binlog_cache_size` when `log_bin` is `ON`. A
  variable is a number when it is 1 to 20 ASCII digits within 64 bits. A missing summand counts as 0 (a missing `tmp_table_size` or
  `max_heap_table_size` therefore makes the smaller of the two 0); a missing or non-numeric `innodb_buffer_pool_size` or
  `max_connections`, or any present summand that is not a number (`binlog_cache_size` only while `log_bin` is `ON`), leaves both memory
  metrics out without a log line. The sum and product are computed exactly in 128 bits. On the captured server the maximum is
  898,035,712 bytes.
- `mariadb_memory_max_ram_percent`: 100 × that maximum / `MemTotal` of `/proc/meminfo` (kB times 1,024, read by the system collector's
  reader with its bounds); left out alone when `/proc/meminfo` cannot be read or `MemTotal` is 0.
- `mariadb_innodb_buffer_pool_used_percent`: 100 × (`Innodb_buffer_pool_pages_total` − `Innodb_buffer_pool_pages_free`) /
  `Innodb_buffer_pool_pages_total`, from the first sample on; left out when either value is missing, the total is 0 or the free pages
  exceed it.
- `mariadb_questions_per_second`: the increase of `Questions` over the seconds between the two runs' starts, and
  `mariadb_innodb_buffer_pool_read_hit_percent`: 100 × (1 − increase of `Innodb_buffer_pool_reads` / increase of
  `Innodb_buffer_pool_read_requests`). Both compare with the previous run and are left out on the first `up` run, when the previous run
  was not `up` or failed, when its sample was lost (`Run.PreviousLost`, *Scheduling*), after a restart (below), when `Uptime` is
  missing, when the time between the runs' starts is not positive, and, each for itself, when its counter is missing in one sample or
  fell; the hit ratio also when the increase of the read requests is 0 or smaller than that of the reads.
- `mariadb_exposed_listeners` (no unit): the number of listening sockets in `/proc/net/tcp` and `tcp6` on MariaDB's `port` and, when it
  is not 0, `extra_port`, whose local address is not loopback once an IPv4-mapped address is unmapped. `127.0.0.0/8`, `::1` and
  `::ffff:127.0.0.0/104` are loopback; the wildcards `0.0.0.0` and `::`, private, Tailscale, public and link-local addresses and the
  IPv4-compatible `::127.0.0.1` count. A listener of another program on the port counts as well. A kernel without IPv6 has no `tcp6`,
  which is skipped. The metric is left out, and the log state below kept, when `port` is missing or not a number from 1 to 65535,
  `extra_port` is present and not a number from 0 to 65535, `tcp` is missing, or a table cannot be read or holds a line the connection
  collector's reader does not accept (over 512 bytes or not in its format). The value of `bind_address` is not interpreted.

**Restarts.** The server's start time is the run's start minus `Uptime` seconds. When it lies more than 5 seconds after the start time
of the previous `up` run of this agent run that had an `Uptime`, whether or not runs that were not `up` lie between them, the server
restarted (also when systemd restarted it without the service state showing it): the warning `mariadb restarted` with `uptime_seconds`
is logged and the rates are left out for that run. A start time that moves by 5 seconds or less, or backwards, is the jitter of whole
seconds and run starts, not a restart.

**Logging.** Through the collector's logger with `collector=mariadb`, no line per run while nothing changes, and only fixed texts with
counts, error numbers and byte values (in decimal) as attributes: never a statement, user, host, database name, server message, the
value of `bind_address` or the socket path.

| Line | Attributes | When |
| ---- | ---------- | ---- |
| warning `mariadb down` | — | the first `down` run after the agent started or after a run that was not `down` |
| warning `mariadb not answering` | — | the first `not_answering` run without `error_code` after a run in another state |
| warning `mariadb user missing or not allowed` | `error_code`, `fix` = `run deploy/agent/mariadb-user.sql as the database administrator` | the first run refused with 1045 or 1698 after a run in another state |
| warning `mariadb login refused` | `error_code` | the first run refused with any other number after a run in another state |
| info `mariadb answering again` | — | the first `up` run after a run that was `down` or `not_answering` |
| warning `mariadb user not as installed` | `fix` (the same text) | *Account check* |
| warning `mariadb restarted` | `uptime_seconds` | *Restarts* |
| warning `mariadb memory maximum above RAM` | `max_bytes`, `ram_bytes` | the first `up` run whose maximum exceeds `MemTotal` after the agent started or after a run below it; a run without both values changes nothing |
| warning `mariadb listens on a non-loopback address` | `count` | the first run with a count above 0 after the agent started or after a run with 0 |
| info `mariadb listens on loopback only` | — | the first run with a count of 0 after a run above 0 |

The state of the first five lines is the availability with the error number: the agent starts out assuming `up`, so a first `up` run
logs nothing, and a refusal with another number (1040, then 1053) is a new episode. A failed run changes none of these states. A
missing user is reported at the first run after the start that reaches the server, and again when the user disappears while the agent
runs.

**Failures.** These fail the run: no record is written, the sample is a gap with cause `unknown`, and the scheduler logs `collector
failed` once and `collector recovered` (*Scheduling*):

- connecting fails with any other error, for example `EACCES` when the unit hides the socket (`mariadb: connect: permission denied`),
  or with no OS error number (`mariadb: connect: failed`);
- a reply the client refuses (*The login*, *The selection*, *Thread snapshot*, *The client's protocol rules*);
- an error packet in answer to the ping or to the status, variables or process list statement (`mariadb: <step>: server error <n>`).

The error texts are `mariadb: connect: <OS error text>` (the text of the error number, never the path) or `mariadb: connect: failed`,
and `mariadb: <step>: <reason>` with the step `greeting`, `login`, `ping`, `status`, `variables`, `processlist` or `account` and the
reason `malformed packet`, `packet out of sequence`, `packet too large`, `unexpected reply`, `unexpected columns`, `cell too long`,
`local file request refused`, `more results refused`, `authentication switch refused`, `name sent twice` or `server error <n>`. The
reasons `no answer in time` and `connection closed` end a run as `not_answering`, not as a failure. No error text holds a byte the server
sent.

**Residuals (record 0118).**

- The replies that were not captured are built from the protocol documentation: the error packet in place of the greeting, the error
  numbers of a refused `unix_socket` login, the answer to `COM_PING`, the authentication switch and the replies to the agent's own
  capability set. A server that answers otherwise fails the run as a gap with a fixed reason, never with a wrong value; the first runs on
  the server, or a capture of the agent itself, settle it.
- Only MariaDB 12.3.3 (`12.3.3-MariaDB-ubu2204`) is captured (`testdata/mariadb/`); MariaDB from 10.6 on is served by the same rules
  but not captured, and MySQL is not supported.
- The full status and variable lists, about 40 KB, are read every run, and the account check costs two small statements per `up` run.
- A listener of another program on MariaDB's port counts as exposed; telling owners apart is the connection collector's job.
- The process list shows what `PROCESS` lets the account see: the statements of every connection, with the customer data and
  credentials they may hold (*Thread snapshot*).

## Kernel events

The collector `kernel` (interval 2 seconds, timeout 2 seconds, placed after `clock` in the job list) records the kernel's OOM kills
as `kernel_event` records of type `oom_kill` with source `kernel` ([wire format](wire-format.md)), the `boot` event of *Boots*, and
a `gap` (source and collector `kernel`, cause `unknown`) for every span of kernel messages it cannot read. It reads the binary
journal files of systemd-journald itself, through reads at offsets into fixed buffers (no `mmap`); it starts no program (no
`journalctl`), links no C library and decompresses nothing.

**Where it reads.** The machine ID comes from at most 64 bytes of `/etc/machine-id`: 32 lower-case hex digits and an optional line
feed. The journal directory is `/var/log/journal/<machine-id>`, else `/run/log/journal/<machine-id>`; the first that can be listed is
read. Of its regular files only `system.journal` (the **online** file, which journald writes), `system@*.journal` (archived) and
`system@*.journal~` (set aside by journald after an unclean close, read like an archived file) are read; the directory is listed in
chunks of 256 entries and at most 1,024 files are taken. The header of every listed file is read at every run.

**Layout (security area 10).** The two layouts journald writes are read: the signature `LPKSHHRH`, a header of 256 to 4,096 bytes (a
multiple of 8, within the file; systemd 249 writes 256, 252 and 253 write 264, 254 and later 272), the incompatible flag `KEYED-HASH`
set and no incompatible flag other than `COMPRESSED-XZ`, `COMPRESSED-LZ4`, `KEYED-HASH`, `COMPRESSED-ZSTD` and `COMPACT`. The
`COMPACT` flag of the header switches the layout per file: without it (systemd 249 on the server) a data object's payload starts at
byte 64, an entry item is 16 bytes (a 64-bit offset and a hash) and an entry-array item 8 bytes; with it (systemd 252 and later by
default) the payload starts at byte 72, so a data object is at least 72 bytes, and entry items and entry-array items are 32-bit
offsets of 4 bytes. The tail fields the compact layout adds to the header and to data objects are not read. Any other file — a
journal without keyed hashes, one with an unknown incompatible flag, a file shorter than 256 bytes — is refused as `unsupported
layout`; a file whose `COMPACT` flag differs when its header is read again is `malformed file`. Before anything is decoded, every
offset must be 8-aligned and inside the header's arena and the file, and every object at least its fixed size and end there; hash
chains and entry-array chains are followed only to strictly larger offsets, a hash chain for at most 65,536 objects and an
entry-array chain for at most 64 arrays; an entry has at most 512 items. A violation is `malformed file`. An object that lies beyond
the end of an archived or set-aside file is a **torn tail**: the file ends there. The same in the online file is `malformed file`,
retried once in the run after the file's size is read again, and at the next runs. Per entry only the items `_TRANSPORT`,
`SYSLOG_FACILITY`, `MESSAGE` and `_SOURCE_MONOTONIC_TIMESTAMP` are read in full, up to 4,096 bytes of payload each; of every other
item only the first 64 bytes, to see its name. At most 16 of these items and compressed items are kept per entry; every further one
is counted. A compressed item is never decompressed, so its name is unknown.

**Finding the entries.** The data hash table (SipHash-2-4 keyed with the file ID) finds the data objects `_TRANSPORT=kernel` and
`MESSAGE_ID=e9bf28e6e834481bb6f48f548ad13606` (journald's entry about missed kernel messages); their entry lists, merged by sequence
number, are what a file holds for the collector. The place after the position is found by bisection over the entry arrays, and the
cursors at the end of the lists of the file the position lies in are kept between runs, so the work of a run is a fixed number of
reads plus the new entries, whatever the number of entries before the position. Differences of sequence numbers are never used as
counts or gaps.

**Which entry counts.** An entry of the `_TRANSPORT=kernel` list is **readable** when it has exactly one `_TRANSPORT` item and
that is `kernel`, no compressed item, no item beyond the 16 kept, a `__REALTIME_TIMESTAMP` above 0 and below 253,402,300,800,000,000
µs (1 January 10000 UTC), a `__MONOTONIC_TIMESTAMP` of at most 9,223,372,036,854,775 µs, and at most one
`_SOURCE_MONOTONIC_TIMESTAMP`, which must be 1 to 20 ASCII digits (no sign, space or line feed) within the same bound; a readable
entry is **counted** when it also has exactly one `SYSLOG_FACILITY` item whose value is exactly `0`. Only a counted entry with exactly
one `MESSAGE` item can be a kill. Any local user can write entries of the other transports, and a write to `/dev/kmsg` reaches the
kernel transport with a facility other than 0 (the kernel forces it), so neither is ever a kill. An entry that is not readable is a
gap (*Gaps of the kernel collector*), never a kill.

**The kill line.** The `MESSAGE` starts with exactly one of `Out of memory: Killed process `, `Memory cgroup out of memory: Killed
process ` and `Out of memory (oom_kill_allocating_task): Killed process `, followed by the PID (1 to 10 digits, 1 to 2,147,483,647)
and ` (`; the victim's name runs to the **last** `) total-vm:` of the line, and a line without one is no kill. The rest is split at
`, ` and at spaces; only the tokens `anon-rss:<1 to 20 digits>kB` (times 1,024, within 64 bits) and `oom_score_adj:<an optional
minus and 1 to 4 digits>` (−1000 to 1000) are used, and one that is missing, malformed, out of range or present twice leaves its
field out while the event stays. The report lines before a kill, `Out of memory and no killable processes` and the `oom_reaper` line
are no kill. The record holds `victim_pid`, `victim_command` (the name made valid UTF-8, each invalid byte U+FFFD, cut to 1,024 bytes;
`(unnamed)` when empty), `anon_rss_bytes`, `oom_score_adj`, `boot_id` (the entry's boot ID), `kernel_time_ns` (the
`_SOURCE_MONOTONIC_TIMESTAMP` in nanoseconds, else the `__MONOTONIC_TIMESTAMP`) and the line as `message` (made valid UTF-8 the same
way, at most 16,384 bytes).
`boot_id`, `kernel_time_ns` and `victim_pid` are the kill's **identity**: the same however often the kill is read.

**Capture time.** The kernel's clock drifts against the journal's clocks, so a kill is timed by its receipt. The *lag* of a counted
entry is its `__MONOTONIC_TIMESTAMP` minus its `_SOURCE_MONOTONIC_TIMESTAMP` (0 when the source stamp is the later one). The *drift*
is the smallest lag among the entry and the counted entries with a source stamp of the same boot that were received at most 600
seconds before it, among the last 64 such entries read (kept across runs, emptied by an entry of another boot, empty after a
restart); the *delay* is lag minus drift. The capture time is the `__REALTIME_TIMESTAMP` minus the delay, so it is never after the
receipt and the delay is 0 without an earlier line; an entry without a source stamp has delay 0. A kill of an earlier boot is timed
the same way.

**Position.** The position is the sequence-number ID, the sequence number and the real time of the last entry read; it is saved in
the agent's state (*Agent state*). Without a usable saved position (a first start, no usable state, a saved position that does not
decode) the collector starts at the end of the newest file (the online file, else the file with the latest tail time) and reads no
kill before it. Each run reads the files in this order:

1. the files of the position's sequence-number ID in the order of their first sequence number, after the position (a file that ends
   at or before it is skipped);
2. the archived and set-aside files of other IDs whose header's first time is not before the committed position's time, by that time
   (ties by file name), from their start;
3. the archived and set-aside files of other IDs without a readable header time, by file name, from their start;
4. the online file, when its ID is another, from its start, whatever its header time (so a clock set back cannot hide it).

A file of steps 2 and 3 that was read to its end is not read again while the agent runs; one cut by a limit is continued where the
limit cut it. The files of steps 1 and 4 move the position, and so does a file of step 2 whose first time is not before the time the
position has reached when the file is read; the others are read without moving it. The position's time never moves back (the later
of the two times is kept). A saved ID of which no file is left records a gap from the position's time to the next readable kernel
entry (or to the run's start), once, and reading goes on there. When files of the position's ID are left but none holds the position
and one starts after it (the files that held it were vacuumed), the entries between are gone: one gap from the position's time to
the header's first time of the first such file (without a readable one, to the next readable kernel entry, else the run's start),
once for that position, and reading goes on in that file. Only when that file's first sequence number directly follows the
position's is nothing lost and no gap recorded; since sequence numbers are not dense, a gap is otherwise recorded even when the
vacuumed part held no kernel entry. A run that records the gap of a refused file (*Failures*) from the committed position's time
records no second gap for the vacuumed position, since that gap already covers the span (unless the clock was set back, see
*Residuals*). A file that disappears between the listing and its read is skipped.

**Limits of a run.** A run stops after one second of reading (checked between entries), after 256 kills, or when its deadline passes,
and returns what it read; the next run continues there.

**Commit and re-read.** What a run reaches — the position, the drift window, the files of other IDs read to their end or cut by
a limit, and the gaps already recorded for refused files and a lost ID — is committed as one unit at the start of the next run, and
only when the scheduler reports that the previous result was kept (`Run.PreviousLost` false); the position is then saved. Otherwise
the next run reads again from the committed state, so a kill whose record was dropped (a timeout, a skip, a sink error) returns with
the same identity. A dropped run that read nothing does not count. At the fourth dropped run in a row from the same committed
position, the span from that position to where the last dropped run got is **given up**: the run records one gap from the committed
position's time to the time of the last readable kernel entry that run read (else the run's start), reads on after the span, and the
span's kills are not returned; while that run is dropped as well, the next one gives up the same span again. Trade-off: a sink error
that lasts more than four runs (8 seconds: a spool write that keeps failing, or a hand-over the spool cannot place, *Spool and
sending*) gives up the span's kills as a gap although the journal still holds them and the sink may recover soon after. A full spool
is no such error: it drops its oldest records with a gap and takes the run's records. A run that is abandoned never moves the saved position.

**Gaps of the kernel collector.** Each gap has source and collector `kernel` and cause `unknown`, and its times come only from
readable entries, the committed position (decoded strictly from the state file) and the run's start; a gap whose end would not be
after its start ends 1 µs after it. Gaps are recorded for:

- an unreadable entry of the kernel list: from the last readable kernel entry read (any facility; else the committed position's time)
  to the next readable kernel entry (else the run's start); several in a row give one gap;
- journald's entry about missed kernel messages on the `driver` transport (or with an item whose name is unknown): over the same
  span;
- a saved ID without files, a vacuumed position, a given-up span, and a file the reader refuses, finds torn or cannot read (*Failures*);
- a record that would fail the scheduler's check (*Scheduling*): the collector runs the same check on every record before it returns
  them and puts a gap over that record's entry in its place (from the previous kernel entry, or the committed position, to the
  entry's time); a gap that would still fail becomes the gap of the last microsecond before the run's start. So a refused record never
  makes a span be read again, and the scheduler never refuses a record of this collector.

**Failures.** A run never fails: the collector returns its records and gaps, and an unreadable journal is logged.

- No machine ID, no journal directory, a directory that cannot be listed: the run reads nothing and records no gap; reading resumes
  at the committed position once the journal can be read, so nothing is lost while the journal still holds it.
- A file whose header the reader refuses (`unsupported layout`, `malformed file`): one gap from the committed position's time (else
  the run's start) to the run's start the first time the agent meets the file, and the other files are read on; while the online
  file is refused, a further gap from the end of the last one to the run's start at the first run at least 600 seconds after it.
- A torn tail, or `malformed file` while reading an archived or set-aside file: that file ends there, with a gap from the last readable
  kernel entry read in it (else the committed position's time, else the run's start) to the header's last time when that is later;
  when the header has no readable last time, to the next readable kernel entry read in this run (else the run's start); when its
  last time is not later, without a gap. The file is not read again: a file that moves the position moves it past the file's last
  entry.
- A file that cannot be opened, a read that fails, and `malformed file` in the online file: the run stops at that file, without a gap,
  and reads it again at the next run. At the third run in a row that fails at the same file and position the file is given up: a
  file whose header could not be read gets the gap of a refused file, one being read the gap and the move past the file of a torn
  tail (the online file's header read again first), and the other files are read on. The online file's `malformed file` is not
  logged before it is given up.

**Extra snapshots.** A kill is **live** when its entry's boot ID is the current boot's and the time since the journal
received it, plus its delay, is at most 60 seconds on the monotonic clock (the first field of `/proc/uptime` minus the
`__MONOTONIC_TIMESTAMP`, a negative age counting as 0); without a readable `/proc/uptime` or boot ID no kill is live. Kills read
from a backlog or of another boot therefore get no snapshot. A run queues its live kills whose identity is not among the last 64
kills it queued before; a run whose deadline has passed when it
finishes queues and requests nothing, so a run the scheduler abandoned never requests snapshots late, and its kills are judged live
again when the next run reads them again. Before a run returns, when no request was made at a run start less than 15 seconds before, the
collector asks the scheduler for one extra run of `processes` and one of `connections` (*Scheduling*), naming the queued kills (oldest
first, at most 64, the rest counted); otherwise they wait for the first run at least 15 seconds after the last request. A run that
reads again kills that an earlier run queued does not request them a second time.

**Logging.** Through the collector's logger, one line per change: warning `kernel messages not readable` with `err` one of
`journal: machine ID not readable`, `journal: no journal directory`, `journal: directory not readable` and `journal: <online|archived>:
<unsupported layout|malformed file|read failed>`, and info `kernel messages readable again`. No line per event and none with a path,
a field value or a message.

**Memory.** The reader and the collector hold at most 4 MiB live, independent of the size of the journal, of the backlog and
of an entry: fixed buffers, at most 1,024 file headers, the drift window and trigger lists of 64 entries each and at most 256 kill
records per run. Two tests assert it at journals of 1,000 and 100,000 kernel entries.

**Residuals (records 0119, 0120).**

- The facility rule proves that the kernel wrote the line, not that the OOM killer did: kernel code (a module or a driver) that
  prints a line of the same form at facility 0 is recorded as a kill.
- A journal without keyed hashes or one that stores the kill lines compressed is not read; its kernel events are lost to recorded
  gaps while it is refused. The compact layout is read as checked against systemd-journal-remote 255 output; files journald itself
  writes in that layout on the server are not yet checked.
- Whether a kill is live compares `/proc/uptime`, which counts time in suspend, with the journal's monotonic clock, which does not.
  They agree on a server that never suspends (to be checked on the server, #42); after a suspend every kill reads as late and gets no
  snapshots, never a snapshot too many.
- After a restart the files of another ID without a readable header time are read again from their start (their kills return with
  the same identities), and a refused file gives its gap again once per agent start.
- An archived or set-aside file of another ID whose header's first time lies before the committed position's time is not read.
- journald's entry about missed kernel messages is built from the systemd 249 source; the real journals hold none.
- The cgroup and the allocating-task kill lines (`Memory cgroup out of memory: Killed process …`, `Out of memory
  (oom_kill_allocating_task): Killed process …`) are built from the kernel source, not from real lines (the server's journals hold
  only the global form); a kernel whose line differs in the prefix or before `) total-vm:` records no event and no gap for such a
  kill.
- After the clock was set back, entries of a vacuumed position can carry times after the run's start. A run that also meets a
  refused file for the first time records only the refused file's gap, which ends at the run's start (or 1 µs after the
  position's time when that is later): the loss is recorded at the position, but not every lost entry's time lies inside the gap.
- The listing skips symbolic links, but a journal file is then opened by its name in the journal directory, so a file replaced by a
  symbolic link between the listing and the read is followed. The directory belongs to root, so only root or journald can place one,
  and they can change the journal anyway.
- The boot change of *Boots* is written to the state inside the run, before the run's records reach the spool (record 0123). A state
  save that falls between the two, followed by a power loss or kernel crash within the spool's next sync (one second), can lose the
  `boot` event while the saved state already holds the new boot, so it is never recorded again and no gap says so; the backend still
  sees the new boot in the batch headers' `boot_id`. Accepted for #38 as a stated residual; #300 moves the update behind the
  scheduler's report that the run was kept and must be merged before the agent's first release.

## Boots

At its first run the `kernel` collector reads the boot ID from `/proc/sys/kernel/random/boot_id` (at most 64 bytes: a lower-case
UUID and at most one line feed) and compares it with the boot in the agent's state (*Agent state*):

- the same boot (a restart of the agent): no record, and the state's boot is left alone;
- another boot: one `kernel_event` of type `boot` with `boot_id`, `booted_at` (the `btime` of `/proc/stat`), `previous_boot_id`
  and `previous_uptime_ns` (the uptime the state holds for the previous boot, a lower bound at most a minute short when the agent ran
  until that boot ended; boots the agent did not run in are not recorded);
- no saved boot (a first start, or no usable state): the same event without `previous_boot_id` and `previous_uptime_ns`, at every
  start for as long as no state is usable (each with the same `boot_id`).

The capture time is `booted_at`; when the boot time cannot be read, `booted_at` is left out and the capture time is the run's start.
With the event the state's boot becomes the current one, with the first field of `/proc/uptime` as its uptime (0 when unreadable)
and its boot time; the `state` job keeps that uptime current. The boot event is returned again by every run until a run's result is kept; the boot is
in the state from the first run on, so a restart after that state was saved and before a run was kept records no boot event. A boot ID that
cannot be read or is not a lower-case UUID gives no boot event, leaves the state's boot alone, makes no kill live and logs one
warning `boot ID not readable`; the other features run. The agent does not start without a readable boot ID (the spool needs it,
*Spool and sending*), so this warning comes only from a read that fails after a successful start.

## Log shipping

The collector `logs` (interval 2 seconds, timeout 2 seconds, placed after `kernel` in the job list) ships the lines of the systemd
journal and of the files `log_shipping.files` names as `log_line` records ([wire format](wire-format.md)), and records a `gap`
(source and collector `logs`, cause `unknown`) for every span of lines it cannot ship. It reads the journal files itself with the
reader of *Kernel events*, decodes zstd-compressed items with a bounded decoder of its own, and starts no program. Its options are in
[Configuration and secrets](configuration-and-secrets.md) (`log_shipping`): with `log_shipping.journal: false` it reads no journal
and drops its journal positions; without configured files it reads no file and no time zone. The collector always runs: when it
cannot be built from the checked options, the agent does not start (`agent cannot start` with the error, exit code 1) rather than run
without it.

**The journal.** The machine ID and the directory are found as for the kernel collector (*Kernel events*, *Where it reads*):
`/var/log/journal/<machine-id>`, else `/run/log/journal/<machine-id>`, the first that can be listed. Of its regular files the system
files (`system.journal`, `system@*.journal`, `system@*.journal~`) **and** the user files (`user-<UID>.journal`, `user-<UID>@*.journal`,
`user-<UID>@*.journal~`, the UID 1 to 10 digits) are read, at most 1,024 listed in chunks of 256; journald's default `SplitMode=uid`
puts the entries of users with a UID of 1000 or more (on Plesk the web-space users) into the user files. `system.journal` and
`user-<UID>.journal` are **online** files, the others archived; a set-aside file is read like an archived one. The header of every
listed file is read at every run under the layout rules of *Kernel events* (*Layout*). A file is read through its main entry array,
so every entry of every transport is seen.

**Journal entries.** Of each entry the items `MESSAGE`, `PRIORITY`, `_HOSTNAME`, `SYSLOG_IDENTIFIER`, `_COMM`, `_PID` and `SYSLOG_PID`
are read, each value up to 16,384 bytes (a longer value is cut without reading the rest); of every other item only the first 64
bytes, to see its name. Of each read name only the first item is kept (a later item of that name is passed over, a plain one
without reading its value), so an entry keeps at most seven items however often its writer repeats a name. The mapping is the import's journal mapping
([Log import](log-import.md), *Journal export*) before its mail, MariaDB and kernel report stages: the capture time from
`__REALTIME_TIMESTAMP`, `host` from `_HOSTNAME`, `program` from `SYSLOG_IDENTIFIER` (else `_COMM` when it is absent or empty), `pid`
from `_PID`, else `SYSLOG_PID` (1 to 10 digits, 1 to 2,147,483,647, else 0), `priority` from `PRIORITY` when it is one digit 0 to 7,
`message` from `MESSAGE`, the first value of a repeated item, `log` and `source` `journal`, no `event`. Every text is made valid
UTF-8 (each invalid byte becomes U+FFFD) and cut at a character boundary — `message` to 16,384 bytes, `host` and `program` to 1,024 —
with `truncated` set when one of them was cut, here or at the read; control and format characters are kept as written, for whoever
displays the line to escape. An entry without `MESSAGE`, or with a `__REALTIME_TIMESTAMP` of 0 or above 9,223,372,036,854,775 µs,
is not shipped and is no gap.

**Compressed items.** journald compresses every item whose payload is 512 bytes or more (on the server with zstd). The name of a
compressed item lies inside its frame, so every compressed item of an entry counts, wanted or not: at most 4 are decoded per entry,
and a fifth makes the entry unreadable without the rest being read, so an entry with more (a coredump's) is a gap. An item is decoded
only when it is zstd, its stored object is at most 4 MiB (a larger one is not read) and holds exactly one frame, which is
single-segment with a stated content size of at most 4 MiB and no dictionary — the form journald writes. Before anything is decoded the
frame header is checked and the block headers are walked to the last block: a reserved block type, a block over 128 KiB or past the
object, or bytes left after the last block (and the checksum, when the frame has one) refuse the frame, so no second frame follows it. The decoder (`github.com/klauspost/compress/zstd`, one goroutine, low-memory mode, memory and window bounded to 4 MiB) writes
into one output buffer of 4 MiB + 128 KiB + 16 bytes, allocated once and never grown; a result outside that buffer is refused. Of a
decoded item at most its 16,384 bytes of value are kept, as for a plain item. An item compressed with XZ or LZ4, a frame refused or
failing to decode, a larger object and a damaged data object of an item make the entry **unreadable**: a gap over it (*Gaps of the
log collector*).

**Journal positions.** The collector keeps a position per sequence-number ID: the sequence number of the last entry read and the
time of the last readable entry (which never moves back). Each run groups the files by ID, orders the groups by the head time of
their first file and the files of a group by their head sequence number, and reads each ID from its position: a file that ends at
or before the position is skipped, the first entry after the position is found by bisection over the main entry array, and the
entries are shipped in their order, one ID after the other. An ID without a position starts this way:

- when no journal position is held at all (a first start, the journal turned on or readable only now), at the tail of its latest
  file, without a gap;
- otherwise at its first entry when the head time of its first file is after the newest journal position held when the run started
  (a new ID, a new user journal);
- otherwise at the tail of its latest file, with one gap from the newest position's time to that file's tail time when that is
  later, because the skipped part may hold lines newer than every position (the clock stepped back, a user journal whose first
  entries are older, an ID whose position was not saved).

So no file is shipped a second time. An ID none of whose files is online and whose position is at its latest file's tail is
forgotten, unless it is the newest position, which is never forgotten (forgetting it would lower the newest time and let the rule
above read the forgotten ID's files again). An ID of which no file is left is forgotten with one gap from its position's time to the
head time of the earliest ID that starts after it (else the run's start). When files of an ID are left but none holds the position
and the first later file does not start at the position's next sequence number, the entries between are gone (vacuumed): one gap
from the position's time to that file's head time (when the head time is not later, to the next readable entry), and reading goes on
in that file. Sequence numbers of one ID have holes where journald numbered entries of another ID's file, so a hole between files is
not checked, and the rule may record a gap where only such entries lay between. At most 256 journal positions are saved: those of IDs
with an online file first, then the others, newest first; a further ID keeps its position in memory only and after a restart starts
at its tail by the rule above, with its one gap.

**Configured files.** Each file is opened through a walk from `/` that follows no link: every directory element with
`openat(O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC)` (so a directory that may only be searched works), the file with
`openat(O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC)`, and `fstat` of the opened descriptor must show a regular file with exactly one
link, so a symbolic link in any element and a hard link to another file are refused alike. Against an attacker who races the check, the refusal of a hard link relies on the distribution default `fs.protected_hardlinks=1` (Ubuntu's default), which stops a user from linking a file he neither owns nor can write. `O_PATH` is the constant `0x200000` of
every Linux port of Go but sparc64; only `linux/amd64` is built. The reasons a file is not read are `missing` (no such file, an element
that is no directory, or a path that is not absolute and clean), `link refused`, `not a regular file` (a FIFO or device is opened
without blocking and closed at once), `permission denied` and `read failed` (any other error of the open, of `fstat` or of a read).
The open descriptor of the file is kept across runs.

**Lines.** A file is read from its position through one 64 KiB buffer. A line ends at a line feed; its first 16,385 bytes are kept and the rest is
read past without being kept. A `\r` directly before the line feed of a line that fit is removed. A line still longer than 16,384 bytes
is cut as the import's line reader cuts it: to its first 16,384 bytes, less up to three bytes so that no UTF-8 sequence is split, with
`truncated` set. The cut comes before the syslog header is parsed, so the header counts against the 16,384 bytes and the `message` equals
the import's for the same line. A UTF-8 byte order mark at offset 0 is removed after the cut and an empty line is not shipped. A last
line without a line feed is **held**, at most 16,385 bytes in the
collector's memory, until its line feed arrives; the saved offset is the start of that line, so after a restart it is read again
from its start. A held line is shipped as it is when its file is truncated or when its renamed file is finished (below). A run that
stops while it hands over the lines of a chunk (at its record or text budget, or on its time or deadline found at a shipped line)
drops what it read after the last line it handed over, and the next run reads that again; a run that stops between two chunks (its
time, looked at after every full 64 KiB, or its deadline) keeps the offset it read up to and the held start of the line, and the next
run reads on from there. So a line of any length is read to its end over as many runs as it takes, at least one 64 KiB chunk per
run that reaches the file, and never holds back the lines after it; after a restart such a line is read again from its start.

**Syslog lines.** The line rules are the import's syslog rules ([Log import](log-import.md), *Syslog*), ported to Go: an optional
`<PRI>` (priority PRI modulo 8), a traditional or RFC 3339 time, `HOST` (1 to 255 bytes), a tag `PROGRAM[PID]:` or `PROGRAM:` (the
program 1 to 128 bytes; a process ID above 2,147,483,647 gives pid 0) and the message after the colon and at most one space. `log` is
the configured path, `source` `syslog`, no `event`; the texts are cleaned and cut as for the journal. Unlike the import, a line without
a syslog header (an RFC 5424 line, a continuation, other text) and a line whose header time does not exist (30 February, 31 November,
29 February of a common year; hour 24 or minute or second 60 is no header time at all) or lies outside the storable range are
**shipped raw**: the whole line as the message, empty `host` and `program`, pid 0, no priority, and the capture time of the previous
line read from the file. The import skips such lines; live, a skipped line would be lost without a gap.

**Time of a year-less line.** It is local time in the zone of `/etc/localtime` (at most 64 KiB, read at every run that reads a file;
an error never falls back to UTC). The year is the year of the run's start in that zone, or the year before when the line would lie
more than a day after the run's start; a time in the hour skipped at the start of daylight saving time is shifted forward by the gap,
and a time in the repeated hour takes the earlier offset unless that lies more than ten minutes before the previous year-less line of
the file. That previous instant is kept per file across runs (not across a restart) as part of the reading state, so a run whose
records were dropped never moves it and the re-read resolves the repeated hour as the dropped run did. RFC 3339 times ignore the zone.
While `/etc/localtime` cannot be read or is no valid zone file, no configured file is read: their positions stay, no gap is
recorded, and every run tries again.

**Rotation.** At every run the configured path is opened again. When it names another file (device and inode) than the kept one,
the file was rotated: the kept descriptor becomes the **renamed** file and the new file is read from its start. Every run reads the
renamed file from its offset to its end first and ships those lines before the new file's. The renamed file is finished — its rest
read, its held line shipped as it is, its descriptor closed — in the run in which the new file has data and the renamed file's size
did not change since the previous run: the writer has moved (rsyslog reopens its files only at the `HUP` after all renames). An append
to it after that is never read. A rotation while a renamed file is still read finishes that file first: the run reads the renamed file
to its end and ships its held line; when the run stops before that (its budget, its time), the new file at the path is not taken in
that run, and the next runs go on with the renamed file until it is finished, then with the newer ones. While the renamed file cannot be
read (a read error), the run reads nothing else of that configured file, so its lines are never shipped after the newer file's. The
renamed file's device,
inode, offset and last seen size are saved with the file's position, so it survives a restart.

**Rotation while the agent was stopped.** After a restart the collector has no descriptor. A saved renamed file, and the saved file
when another file is now at the path, is looked for in the directory of the configured path, reached by the same link-free walk, among
`<name>.1` and the names `<name>-` plus eight digits (at most 65,536 directory names read, in chunks of 256; a directory that cannot be
listed gives `<name>.1` only). Each candidate is opened relative to the directory with `O_RDONLY|O_NOFOLLOW|O_NONBLOCK` and accepted
only when the **opened descriptor** is a regular file with one link and the saved device and inode, so no window lies between a check
and the open; the others are closed. A file found is read on as the renamed file. When the saved file is not found (deleted,
compressed, renamed otherwise), one gap runs from the file's last time to the capture time of the first line read from the new file
(else the run's start); when a saved renamed file is not found, one gap from the file's last time to the run's start. When the
directory cannot be reached (a link on its path, a missing or unreadable directory) the position stays and the file is not read.

**Truncation.** The kept file was truncated or replaced in place when it is shorter than what was read of it or when the SHA-256 of
its first bytes — as many as were read, at most 256, kept with the position — changed, so a file that grew past the old position by
the next run is noticed as well. Its held line is then shipped as it is, one gap runs from the file's last time to the run's start,
and reading restarts at offset 0.

**A file's last time** (`last_time` of its position) is the capture time of the last line read from it, shipped or filtered (a raw
line takes the previous line's); when the collector takes a position without reading a line, it is the start of that run. It starts
the gaps of a truncation and of a lost rotation.

**First start and changed files.** Without a saved position — a first start, no usable state, a file added to `log_shipping.files` —
a file that exists starts at its end, so nothing older is shipped (history is imported with `vandoxd import`); a file that is missing
when it is first looked for is read from its first line once it appears. Positions are matched to the configured files by the SHA-256
of the path; a file removed from the configuration loses its position.

**Filter.** After mapping, a line is not shipped when it has a priority less severe than `log_shipping.min_priority` (a line without a
priority always passes) or when it meets every condition a drop rule sets: `log` equal to `journal` or the configured path, `program`
equal to the mapped program, `priority` that priority or less severe (a line without a priority never meets it), and `message` an RE2
search in the mapped message (cleaned and cut). A filtered line moves the position like a shipped one.

**Budgets of a run.** A run ends when it has produced 1,000 records (gaps included), at the record that brings the text of its log lines
(UTF-8 bytes of `log`, `host`, `program` and `message`) to 4 MiB, after one second of reading, or at its deadline; the rest is read by
the next runs. Filtered lines count only against the second, so a flood of filtered lines delays wanted ones but never stops them.
The clock is looked at after every shipped record, after every 1,000 entries or lines examined, after every full 64 KiB read from a
file and after every journal entry whose reading called the decoder (a decoded 4 MiB item costs about 4 ms, so 1,000 filtered entries
with compressed items would otherwise read far past the second). The journal and each configured file are sources; the source read
first changes from run to run (the journal first in one run, file 0 first in the next, and so on in turn), so a backlog in one source never keeps the others
waiting. Within a source the order is kept.

**Record check.** Every record is checked as the scheduler stamps and checks it (*Scheduling*) before it is returned; a refused record
is replaced by one gap from the previous line of the same source (the previous entry of the ID or line of the file read, else the
position's time) to the record's time, and a gap that would still fail becomes the last microsecond before the run's start. So the
scheduler never refuses a record of this collector and a hostile line never makes a span be read again.

**Commit, save and give-up.** A run works on a copy of the committed reading state (positions, held lines, renamed files, the per-file
previous instants, the refused journal files). When the scheduler reports the run kept (*Scheduling*, the observer), that copy
becomes the committed state at once, and changed positions are handed to the state file as an urgent change (*Agent state*), so they
are saved within one state interval. A run that is reported not kept (an error, a refused record, a sink refusal) is a dropped run; a
run that timed out or was skipped is not reported and counts as one dropped run at the next run, through `Run.PreviousLost`. Its
copy, which an abandoned worker may still change, is never committed. The next run reads again from the committed state, so the
lines return as long as the journal or the file still holds them. At the fourth dropped run in a row from one committed state the
span is **given up**: when a dropped run's result came back, the next run starts from the reading state the last such run reached
(that copy is not committed) and records one gap from the committed time (the newest of the journal positions' times and the files'
last times, else the run's start) to the latest time of an entry or line that run read (else the run's start); the span's lines are
not shipped and reading goes on after them. Every following run starts from that same state and carries the same gap until a run is
kept and its copy is committed, so the gap is never lost with a dropped run; a run that started from the span and is dropped too does
not replace the span. When none came back
(all timed out or were skipped), one gap runs from the committed time to the run's start, once per committed state, and the
positions stay. Trade-off, as for the kernel collector: a sink refusal that lasts more than four runs (8 seconds: a spool write that
keeps failing, or a hand-over the spool cannot place; a full spool drops its oldest records instead of refusing) gives up lines the
journal or the file still holds. A run that is abandoned never moves a committed or saved position.

**Gaps of the log collector.** Each gap has source and collector `logs`, cause `unknown` and the run's start as its capture time; a
gap whose end would not be after its start ends 1 µs after it. Besides the gaps of the journal positions, a lost rotation, a
truncation, a refused record and a given-up span above, gaps are recorded for:

- an unreadable entry: from the previous entry read in the ID (shipped or filtered; else the position's time, else the run's start) to
  the next readable entry (else the run's start, or the last unreadable entry's time when the run stopped before the end of the ID);
  several in a row give one gap;
- a journal file the reader refuses (`unsupported layout`, a header that is `malformed file`): one gap from the newest journal
  position's time (else the run's start) to the run's start the first time the agent process meets the file, and while the online
  file is refused a further gap from the end of the last one to the run's start at the first run at least 600 seconds after it. The
  refused files (at most 1,024) are part of the reading state, so the gap of a dropped run is recorded again by the run that reads
  again; the other files are read on;
- a journal file damaged in its structure (an entry array or entry object that violates the layout): reading of the file stops at the
  damage, the lines before it are shipped and the position stays before it. An archived or set-aside file is given up at once. In an
  online file the read is repeated once after its size is read again; the run then stops reading that ID, and the third run in a row
  that stops at the same file, ID and sequence number gives the file up. Giving up records one gap from the last entry read from the
  file (else the position's time, else the run's start) to the file's tail time (the online file's header read again first; without a
  tail time, to the run's start), moves the position to the file's tail and goes on with the next file of the ID or the entries
  journald appends later. A cut-off end of an archived or set-aside file ends it the same way, also when its tail time is that of the
  last entry read (the gap then covers 1 µs and marks the cut-off end); a damaged file given up at its tail entry gets the same 1 µs gap.

No gap is recorded while a source cannot be read and its position stays, because its lines are still there: no machine ID, no journal
directory, a directory that cannot be listed, a journal file that cannot be opened or read for another reason (the run stops reading
the journal or that ID), a configured file that is missing, not readable, not a regular file or behind a link, and an unknown time zone.
Reading resumes at the position; a file that returns with another inode is a rotation.

**Logging.** One line per change, fixed texts only, never a path, a field value or a message, and no line per shipped line, so the
agent's own entries in the journal, which it ships like any other, cause no loop:

| Line | Attributes | When |
| ---- | ---------- | ---- |
| warning `journal not readable` | `err`: `journal: machine ID not readable`, `journal: no journal directory`, `journal: directory not readable` or `journal: <online\|archived>: <unsupported layout\|malformed file\|read failed>` | the first run with a problem after the start or after the journal was read; a different problem meanwhile logs nothing |
| info `journal readable again` | — | the first run that lists and reads the journal without a problem after that |
| warning `log file not readable` | `file` (the index in `log_shipping.files`), `err`: `missing`, `not a regular file`, `link refused`, `permission denied` or `read failed` | the first run with that reason for the file; a different reason is logged again |
| info `log file readable again` | `file` | the first run that opens and reads the file without a problem after that |
| warning `time zone not readable` | `err`: `localtime not readable` | the first run that cannot load the zone |
| info `time zone readable again` | — | the first run that loads it after that |

A `read failed` is also logged for a read error on an open file, the kept current or renamed one; the lines before the error are
shipped and the position stays at the last whole line. A read error of the renamed file stops the reading of that configured file for
the run, the current file included, so the order of its lines is kept; the next run tries again (after a restart the renamed file is
opened anew by its identity).

**Memory.** The collector holds at most 24 MiB live, independent of the size of the journal, of the files, of the backlog, of an entry
and of a line: the records of one run (at most 4 MiB of text, the crossing record and the fixed part of at most 1,000 records), the
journal reader's buffers and at most 1,024 file headers, the zstd path (the output buffer above and one input buffer grown once to at
most 4 MiB, both reused), the 64 KiB read buffer, and the held lines of at most 16,385 bytes per current and renamed file in the
committed state and up to two copies. `TestLogs_HeapStaysBounded` asserts the bound with records at the maximum of every field, a
journal of 100,000 entries and lines over 16 KiB; `TestDecoder_HeapStaysBoundedForTheLargestFrame` pins the decoder's buffer.

**Checked against the server.** The reader with the decoder read every entry of the two real system files of the server (72,909 and
73,265 entries, 340 compressed objects, all zstd frames of descriptor `0x60`) with no unreadable item
(`TestReal_NextWithDecodesEveryEntry`, run on a developer's machine with `VANDOX_REAL_JOURNAL_DIR`; nothing of the files is
committed). CI reads generated files: `testdata/journal/live.export` (21 generated entries covering the server's transports, field
forms and a 20,000-byte message) written as binary journal files with zstd frames of the server's form
(`TestWrite_CompressedFramesMatchTheServerForm` compares them with `testdata/journal/zstd-frames.txt`, the frame headers seen on the
server), and `testdata/journal/live-records.jsonl`, the records the import's journal mapping gives for them, which the Go collector's
test and the C# import's test both read.

**Residuals (record 0121).**

- After an unclean end (SIGKILL, a crash, a power loss) the lines read after the last saved position are shipped again: at most one
  state interval plus one collector interval (10 + 2 seconds); nothing marks them as duplicates. A stop with SIGTERM is exact
  (*Agent state*, *Saving*).
- Any local user can write journal entries with any `SYSLOG_IDENTIFIER`, `PRIORITY` and message (`logger`, the native socket), and a
  service user anything into its own log file; such lines are shipped as written. The trusted journal fields (`_PID`, `_COMM`,
  `_HOSTNAME`) are journald's.
- A renamed file whose reads keep failing holds back the newer lines of its configured file, with the warning `log file not readable`
  (`read failed`) standing, until it reads again or the agent restarts.
- A journal without keyed hashes, XZ or LZ4 items and entries with more than four compressed items are lost to recorded gaps.
- The user files of the server's sample were empty: user-journal entries are read like system entries without a real one checked. That
  rsyslog's logrotate configuration on the server renames before its single `HUP` is not checked on the server (#42); the renamed file
  is read until the writer has moved whatever that window is.
- A refused journal file gives its gap again after every agent start; a file replaced by a link between the listing and the read of the
  journal directory is followed, as for the kernel collector.
- Lines are shipped per source and, in the journal, per ID, not merged by time across sources.
- Live lines carry no classification (`event`, the mail markers) until #290; the error log formats follow in #289.

## Backup runs

The collector `backups` (interval 10 seconds, timeout 5 seconds, placed after `logs` in the job list) records the start and the end
of each backup run as `backup_run` records with source `backups` ([wire format](wire-format.md)). It runs only when
`backups.processes` holds at least one rule (default: one rule, `command: pzstd`; [Configuration and
secrets](configuration-and-secrets.md)); with `backups.processes: []` it does not run and writes no record and no log line.

**Matching.** Each run lists `/proc` as the process collector does (its 4,096 lowest PIDs) and reads `stat` and `cmdline` of each
process with the process collector's readers (*Processes*, **Readers**). A process matches when a rule's `command` equals its
`command` as the process collector derives it (*Processes*, **Names**), or when its cleaned command line starts with a rule's
`cmdline_prefix`; both are literal comparisons, so `grep pzstd` (command `grep`) does not match `command: pzstd`. A process that
cannot be read, ended or not, is skipped like an ended one, without a log line or a gap.

**A run.** A run starts at the first check that sees a match: the **start** record holds `started_at` (the earliest start time of the
matching processes, also its capture time), `boot_id`, the `pid` and `command` of that earliest process and `processes`, the number of
matches. Each further check with a match keeps the time of the check as the last sighting and the largest number of matches; only
the earliest process is ever kept, never a list. The first check without a match is the **gone-by** time; a match before 60
seconds have passed since the last sighting keeps the run. At the first check at least 60 seconds after the last sighting the
**end** record is written with `started_at`, `boot_id`, `last_seen_at` (the last sighting, also its capture time), `gone_by` and
`max_processes`; the real end lies between `last_seen_at` and `gone_by`. A process seen at one check only gives a run with its start
and its end; one that lives shorter than the time between two checks may be missed.

**Restarts and reboots.** The open run is kept in the agent's state. At the first check after the agent starts, a stored
run of the current boot with a match continues (no second start); a stored run of another boot, or one without a match, ends at once
with the stored `last_seen_at` (at most a minute before the stop) and `gone_by` that first check; a backup that runs in the new boot
is a new run with its own start. The gone-by time is not stored: after a restart the first check without a match is the gone-by
time, so it is never before the real end.

**Lost runs.** After a check whose result was dropped (`Run.PreviousLost`), the collector first restores the open run, the gone-by
time and the first-check flag as they were before that check, so the dropped start or end is decided again: the start is written
again while a match runs, the end again with the gone-by time of the last kept check. The scheduler's gap covers the lost check.

**Errors.** A boot ID that cannot be read or is not a lower-case UUID (the rule of *Boots*), a boot time that cannot be read, a `/proc` that cannot be listed or the run's deadline fail the run (a gap,
*Scheduling*); the error texts name the file and a fixed reason, never a command or command line.

**Residuals (record 0120).**

- With the default rule a backup is recognized only while `pzstd` runs: Plesk's own backup processes have not been captured (#45), so
  a phase without `pzstd` (the database dump before the compression) is not part of the run unless the operator adds a rule.
- Any local user can start a process named `pzstd`; it counts as a backup run.
- A backup process the agent cannot read is not recognized; one that becomes unreadable during a run can end the run early.
- A state the `state` job saved between a dropped check and the next one survives a restart before that next check: the dropped start
  or end is then not written again.
- A backup start, sighting or end is written to the state inside the check, before the check's records reach the spool (record
  0123). A state save that falls between the two, followed by a power loss or kernel crash within the spool's next sync (one second),
  can lose the `backup_run` record while the saved state already holds the change, so it is never written again and no gap says so.
  Accepted for #38 as a stated residual; #300 moves the update behind the scheduler's report that the check was kept and must be
  merged before the agent's first release.

## Agent state

The agent keeps one small state file, `agent-state.json` in `spool.directory` (security area 9): the last boot it ran in with that
boot's uptime and boot time, the position of the `kernel` collector in the journal, the open backup run and the positions of the
`logs` collector. Besides the spool's own files (*Spool and sending*) it is the agent's only file write. The agent never creates the
directory; the installation does (#42).

**Format.** One JSON object `{"version":1,"boot":{"id","uptime_ns","booted_at"},"journal":{"seqnum_id","seqnum","realtime"},
"backup":{"boot_id","started_at","last_seen","max_processes"},"logs":{"journal":[{"seqnum_id","seqnum","realtime"}],"files":[{"path_sha256",
"device","inode","offset","head_sha256","head_len","last_time","renamed":{"device","inode","offset","size"}}]}}`, the four sections,
`logs.journal`, `logs.files` and `renamed` optional, IDs as lower-case UUIDs, times in RFC 3339 UTC. A file position names its
configured file by the SHA-256 of the path (64 lower-case hex digits), never by the path, so every position has the same size;
`offset` is the start of the first line not yet shipped, `head_sha256` the SHA-256 of the file's first `head_len` bytes (that of no
bytes when `head_len` is 0) and `renamed` the rotated file still being read (*Log shipping*). At most 256 journal and 16 file
positions are written, which fit the 64 KiB bound with room.

**Reading (at start-up).** The file is opened without following a link and without blocking, and is trusted only when `fstat` of
that descriptor shows a regular file of the agent's effective user without any group or other permission bit and of at most 64 KiB.
Its content is decoded strictly: one object and nothing after it, `version` 1, no unknown key, no wrong type, lower-case UUIDs, a
non-negative uptime, every time before 1 January 10000 UTC, an open run with a start and a last sighting, the last sighting not
before the start, and at least one process, and log positions with at most 256 journal and 16 file positions, both digests of 64
lower-case hex digits, `head_len` 0 to 256 and offsets and sizes that are not negative. A file without `logs` (written before the log
positions existed) stays usable: the `logs` collector starts at the current ends as on a first start. An absent file in an existing, empty directory is a first start, without a warning. Anything else that is not
usable — a missing directory, an absent file beside other entries, a link, a file of another user or with group or other permission,
an oversized or corrupt file — is treated as no state and logged once as warning `agent state not available`; the first save replaces
the file. The state file is read before the spool is opened, so on a first start the directory is still empty and neither logs a
warning; a missing directory logs this warning and then ends the start, because the spool refuses it (*Spool and sending*), so the
agent never runs without its directory. Stated residual: a start that ends after the spool created its files and before the first
state save (a `kill -9` in the first seconds, a later start error) leaves spool files without a state file, and the next start logs
`agent state not available` once.

**Saving.** The `state` job (interval 10 seconds, timeout 5 seconds, last in the job list; it records nothing) saves when a change
waits: when an urgent change waits — a kill, a boot or a backup change (a new run, its end, a higher process count, a position moved
by a run that read kills) or a log position the `logs` collector committed — and at least 5 seconds have passed since the last save,
else once 50 seconds have passed, the uptime heartbeat (a kernel position that moved over other kernel lines waits for this). The job
runs on a 10-second grid and a run starts a little after its tick, so the 5 seconds (half the interval) let an urgent change be saved
at the next run, within one state interval, however the start times jitter. Every save first refreshes the stored uptime from
`/proc/uptime`, so the saved uptime is never more than a minute old. A save removes a left-over `agent-state.json.tmp`, creates it anew
(exclusively, without following a link, mode 0600), writes and syncs it, renames it over `agent-state.json` and syncs the directory.
One write lock of the store covers the encoding, the write and the bookkeeping of a save, so a save of the job and the save at the
stop never run at once. **Every save first makes the spool durable** (the spool's `Sync`, within the save's deadline: the job's
timeout, or 5 seconds at the stop), so no saved position or state covers a record a crash can still lose (the exception is the boot
and backup change of record 0123, *Kernel events* and *Backup runs*). When that sync fails or does not finish in time, the save is
not written. A failure, also a missing directory, is logged once as warning `agent state not saved` with `err`
`agentstate: save: <system error text>` (never a path), or `agentstate: save: spool not durable` when the spool's sync failed, until
a save succeeds, whether the job or the stop saved, and the agent goes on;
a save that hangs past the job's timeout is a gap of collector `state`, as for any collector.

**Saving at the stop.** When the agent stops, after the scheduler has stopped (a collector stuck in a run is not waited for), it saves
the state once more when anything changed since the last save, so the positions of the last kept runs survive a restart (a stop with
SIGTERM neither repeats nor loses a log line). It waits at most 5 seconds for a save of the `state` job that is still running; when
that save does not end, it saves nothing and logs the warning `agent state not saved` with `err` `agentstate: save still running`
(every time it gives up), and the agent exits as usual. The order at the stop is: the scheduler stops, this last save (which makes
the spool durable first), then the spool is closed (*Spool and sending*).

**The spool leaves it alone.** The state file is not part of the spool: the spool never reads, counts, overwrites or deletes it
(*Spool and sending*).

## Memory pressure mode

The `system` collector decides the memory pressure mode ([Scheduling](#scheduling)) after each successful `system` sample, from the values
it has just read:

- the mode turns **on** when `MemAvailable` is below 10 % of `MemTotal` (10 times `MemAvailable` is less than `MemTotal`) or the memory
  `some avg10` is at least 10.00;
- it turns **off** only when `MemAvailable` is at least 15 % of `MemTotal` (20 times `MemAvailable` is at least 3 times `MemTotal`) and
  `some avg10` is below 5.00 or the memory pressure file is not available (a kernel without pressure stall information);
- otherwise it keeps its mode. The products are computed exactly (128 bits, no overflow, no floating point). The thresholds are fixed, not
  options, until the parallel run of #45 shows a reason (record 0113, option 4).

The collector calls the switch (`Scheduler.SetPressure` and `Sender.SetPressure`, which the agent connects when it builds the scheduler) only when the mode changes;
it starts off. A change logs one info line `memory pressure mode on` or `memory pressure mode off` with `collector=system`,
`mem_available_bytes` and, when the pressure file was read, `memory_some_avg10`. A failed sample changes nothing. The effect: the `system`
(10 seconds), `processes` (15 seconds), `connections` (15 seconds), `systemd` (10 seconds) and `mariadb` (10 seconds) collectors run at their pressure intervals, `processes` lists every process, `connections` records a full snapshot and `mariadb` records the thread snapshot of the server's process list (*MariaDB*). An extra run of `processes` or `connections` for an OOM kill then takes the place of the collector's next regular run (*Scheduling*), so a storm of kills costs no more snapshots than the mode already takes.

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
skipped, a span of kernel messages it cannot read (*Kernel events*), a span of log lines it cannot ship (*Log shipping*), spool
data dropped for the size bound, the free-space reserve or the file limit or lost to damage (`spool_dropped`), and the records the
spool refused while its writes failed (`unknown`; *Spool and sending*). The backend records the gaps it detects (missing sequence numbers,
intervals with no batch). Analysis, reports and the web UI treat a recorded gap as *unknown*, never as *normal*. Spool eviction,
collector timeouts and backend gap detection each emit a gap record, and each path has a test.

## Spool and sending

- The agent keeps collecting while the backend is unreachable and keeps **at least 7 days** of records in an on-disk spool. When the
  backend is reachable again it sends current data first, then backfills the spool chronologically and throttled (*Sending*).
- Every record carries a sequence number from one counter per agent that increases strictly, survives restarts and reboots and is never
  reused. A batch has no identity of its own (see the wire format). Losing the counter would make new records collide with stored
  ones; the clock bound below keeps a fresh spool's numbers above them.
- The boot ID and the clock offset are kept with each spooled record, and a batch holds only records with the same boot ID and offset.
- The spool is a file-write area of the agent: it has a size bound and defined behavior when it is full (oldest data dropped, a gap
  recorded). Records the spool refuses to take from the scheduler are its loss to record as a gap, too.
- The spool counts and deletes only its own files: the agent's state file in the same directory (*Agent state*) is never part of its
  size or of what it drops.

**What is kept.** The scheduler hands every run's checked records to the spool (*Scheduling*); a hand-over is accepted whole, when it
returns without an error, or refused whole. Each kept record gets its sequence number, in hand-over order, and is kept as its wire
line with the agent ID, the boot ID the agent read from `/proc/sys/kernel/random/boot_id` at its start and the clock offset estimate.
No part of the agent measures an offset yet, so every record is kept without one (the header's "unknown"); the format holds the field.
A record reads back unchanged, also one of the largest size the wire format allows. The agent token is never written to the spool.

**Files.** The spool lives in `spool.directory` beside `agent-state.json`. Its own names are the data files
`segment-<the first record's number in 20 decimal digits>.spool`, the meta file `spool-meta.json` and its temporary file
`spool-meta.json.tmp`, all created with mode 0600. Every other entry (`agent-state.json`, `agent-state.json.tmp`, any other file or
directory, up to 100,000 of them within the memory bound) is foreign: never opened, read, counted, overwritten or deleted.

- A data file (format version 1, every integer little-endian) starts with a header of at most 128 bytes: `VXSPOOL1`, the version, the
  agent ID (1 to 64 bytes), the boot ID, the clock offset (flag and value) and a CRC-32C. Each record is one frame: the payload's length
  (at most the wire format's line limit plus 8 bytes), the payload's CRC-32C (Castagnoli) and the payload, the sequence number and the
  wire line. A sealed file ends with a summary frame (marker `0xFFFFFFFF`, the last number, the record count, the earliest and the
  latest capture time, for a gap record its `from` and `to`, and a CRC-32C).
- A data file holds the records of one capture context (agent ID, boot ID, clock offset). It is sealed — summary written, file synced —
  before the next one is created: when the next hand-over would take it beyond `spool.max_size` / 128, bounded to 1 to 64 MiB (16 MiB
  at the default), or when its context ends; a file grows beyond that size only by a single hand-over that is larger. A start in the
  same boot appends to the newest unsealed file of the context, so restarts alone add no file; a start in a new boot seals it.
- The meta file is one JSON object, `{"version":1,"agent_id":…,"boot_id":…,"clean":…,"next":…,"acked":[{"first":…,"last":…}]}`, every
  key required, at most 16 KiB: the agent ID and the boot ID of the last start, whether the spool was closed cleanly, the next sequence
  number at the time of the write and at most 65 acknowledged ranges (64 above the oldest unacknowledged record and one that reaches down to it). It is replaced through its temporary file and a rename: at the
  start (synced, `clean` false), at every acknowledgement (not synced: a lost acknowledgement only makes records be sent again) and at
  the close (synced, `clean` true).

**File access (security area 9).** `spool.directory` must be absolute and clean and its last element must not be a symbolic link (links
above it are followed as configured); the spool opens it with `O_DIRECTORY|O_NOFOLLOW` and keeps the descriptor. Every later open,
create, remove and rename is relative to that descriptor on a plain name (`openat`, `unlinkat`, `renameat`), never by path and never
through Go's `os.Root`, which follows a symbolic link whose target stays inside the directory. A file is opened with `O_NOFOLLOW` and
`O_NONBLOCK` (a link fails, a FIFO does not block) and trusted only when `fstat` on that descriptor shows a regular file of the
agent's effective user without any group or other permission bit and with exactly one link; it is created exclusively. Because the
check runs on the opened descriptor, an entry that is no regular file is opened before it is refused and closed at once, never read or
written: a device node placed under a spool name (which needs `CAP_MKNOD`) reaches its driver's open. A name is
removed or renamed only after the same check, a data file only when its device and inode are those of the file the spool holds,
so a link or an entry swapped in between never makes the spool write or delete anything but its own files. An entry the check refuses
is never followed, written, renamed or deleted: in place of a data file it is damage (below); in place of the meta file or its
temporary file the meta file cannot be written, so the start fails and at run time an acknowledgement, a sync or the close returns
`spool: meta: refused entry`.

**Start.** The agent opens the state file first, then reads the boot ID and opens the spool. A spool that cannot be opened makes the
agent log the error line `agent cannot start` with one of these errors and exit with 1 before it notifies systemd (`READY=1`), so
`systemctl status` shows the failure; a failed start is retried only by the unit's `Restart=` (#42). No error names a path.

| Error | Cause |
| ----- | ----- |
| `spool: boot id: not readable`, `spool: boot id: not a lower-case UUID` | `/proc/sys/kernel/random/boot_id` missing or unreadable, or not a lower-case UUID |
| `spool: directory: not an absolute, clean path` | the path is relative or not clean (the configuration already refuses such a path) |
| `spool: directory: symbolic link` | the last element of `spool.directory` is a symbolic link |
| `spool: directory: not a directory` | `spool.directory` is a file or another entry that is not a directory |
| `spool: directory: <system error text>` | the directory cannot be examined or opened, such as `no such file or directory` |
| `spool: meta: refused entry` | a refused entry in place of `spool-meta.json` or `spool-meta.json.tmp` |
| `spool: meta: <system error text>` | the meta file cannot be read or written, such as `permission denied` (a directory the agent cannot write) or `read-only file system` |
| `spool: read: <system error text>`, `spool: write: <system error text>` | listing the directory, reading a data file, or cutting and sealing one at the start failed |

The directory is listed in chunks of 256 names and at most the 1,026 data files with the highest numbers are kept; a sealed file is
checked by its header, its summary and its first record, while the newest file and every file without a valid summary are read frame
by frame. A file without a summary is cut at its last good frame and sealed, unless it is the newest file of the current context,
below the sealing size and ends cleanly or in a torn frame: that one is appended to. A start that finds no meta file and no data file logs the info `spool created`; a directory that holds only foreign
entries, `agent-state.json` among them, counts as empty, so a first start logs no warning from the spool or the state file.

**Sequence numbers.** Numbers increase strictly and are never issued twice under one agent ID. The next number at the start is the
largest of the meta file's `next`, the newest data file's name number and the last good number plus 1. On a fresh spool (no meta file,
a damaged one, or one of another agent ID), after an unclean stop in an earlier boot (the meta file is not `clean` and names another
boot, so a power loss may have rolled the counter back) and when the newest data file ends in damage whose numbers cannot be known,
the **clock bound** is taken as well: the wall clock's microseconds since the Unix epoch plus 1,000,000. The agent issues far fewer
than 1,000,000 numbers a second, so a number is never more than 1,000,000 ahead of the clock's microseconds when it is issued, and a
later fresh start lies above it. After a clean stop and after a `kill -9` in the same boot the numbers go on directly after the last
one issued; an agent's first number is large, and a fresh start or a crash leaves a jump (wire format, *Batch identity*). The gaps the
spool writes take numbers too, before the records of the hand-over that carries them. A hand-over whose numbers would reach
2^63 - 1, the largest the backend stores, is refused with `spool: sequence numbers exhausted` as a write failure below. Stated
residual: a clock set back by more than the time between a lost spool and a fresh one can make new numbers collide with stored ones.

**Reading and acknowledging.** `Read(after, maxRecords, maxBytes)` returns the kept, unacknowledged records with numbers above `after`
(0 reads from the oldest) in sequence order, at most `maxRecords` records and `maxBytes` bytes of their wire lines (the line feed not
counted), together with their agent ID, boot ID and clock offset: one result never mixes two capture contexts and never holds a
record whose write has not completed. A result may end at a file boundary or go on into the next file of the same context. It holds
at least one record when the first fits; it is empty when the first record alone exceeds `maxBytes` (and when a limit is 0 or less),
so a caller passes a `maxBytes` of at least the wire format's line limit (1 MiB) to always make progress. A record is handed out by
every read that reaches it until it is acknowledged. `Ack(first, last)` acknowledges every issued, kept, unacknowledged number of the
range, whether a read handed it out or not (the spool does not track reads); numbers never issued (below the oldest, above the newest,
inside a jump), already acknowledged or dropped change nothing, and so does a range with `first` 0 or above `last`. The spool keeps
at most 64 disjoint acknowledged ranges above its oldest unacknowledged record: an acknowledgement that would make a 65th is refused
with `spool: more than 64 acknowledged ranges` and changes nothing; its records stay readable and are acknowledged again later (a
resend is idempotent at the backend). Acknowledged records are never handed out again, also after a restart. A sealed file whose
numbers are all acknowledged is deleted; the file being written to is never deleted, so once every record is acknowledged the spool
holds at most that file and the meta file, at most one sixteenth of `spool.max_size`.

**Size bound, free space and file limit.** `agent_spool_bytes` is the size of the spool's own files on disk (the data files, headers
and summaries included, and the meta file). Before every write the spool checks its room: the data files plus 32 KiB for the meta
file and its temporary file plus the write (its frames, a header and a summary when a file is created or sealed, and 1 KiB for each
gap record it owes) must stay within `spool.max_size`; unless `spool.min_free` is 0, the space available to unprivileged users on the
file system (`statfs` `f_bavail` times `f_bsize`) must be at least the write and what remains after it at least `spool.min_free`,
compared without overflow; and a new data file may not make more than 1,024. While there is no room the spool drops its oldest data
file whole and checks again; the file being written to is never dropped, and when only it is left the hand-over is refused
(`spool: no room for the records`, a write failure below). A hand-over that alone, with the gaps it owes and the 32 KiB, exceeds
`spool.max_size` is refused before anything is dropped. A full spool therefore never refuses records: it drops its oldest.

- Every drop owes one `gap` with cause `spool_dropped` per run of unacknowledged numbers of the dropped file (acknowledged numbers
  split runs; runs that join across the files one hand-over drops give one gap): origin and source `agent`, `first_seq` and
  `last_seq` the run's lowest and highest number, `from` and `to` the earliest and latest capture time of the run's records, the spans
  of dropped gap records included (from the summary when the run is the whole file, else read from the file), `to` at least one second
  after `from`, and the spool's clock at the write as its capture time. The gaps are written in the same write as the hand-over, before
  its records. Stated residual: a gap's sequence range is exact, while its time span is the union of the dropped times and may include
  moments of which other records were kept.
- The warning `spool data dropped` (`reason`: `size_bound`, `disk_space` or `file_limit`, the reason of the first drop; `records`: the
  records that hand-over dropped) is logged at the first drop of an episode, the info `spool data no longer dropped` (`records`: the
  episode's total) at the first hand-over that fits without a drop.

**Write failures.** A write that fails (no space left, an I/O error, the file system remounted read-only) is cut back to the last good
frame and the hand-over is refused at once with `spool: write: <system error text>`, so the scheduler logs `sink failed` and the run is
not kept; the refusal never blocks the watchdog. Unlike at the start the agent keeps running, because the failure may pass. The
spool logs the warning `spool write failed` (`err`) once per episode; a hand-over refused for want of room or numbers belongs to the
same episode. The first hand-over that is written again first writes one `gap` with cause `unknown`, source `agent` and no sequence
range, from the earliest to the latest capture time of every record refused in the episode (at least one second), and logs the info
`spool writes again`. A gap still owed when the agent stops is lost with it; the backend's time-gap detection (#41) covers that span.

**Damaged data (security area 10).** Values read back from the spool's files are held to consistency rules before they are used:
the header's, each frame's and the summary's checksum; the format version; a frame length within the bound above; a header boot ID
that is a lower-case UUID; a name number that is the number of the file's first record, above the previous file's last number and
below 2^63 - 1; record numbers that increase strictly within and across files; a frame number equal to the `seq` of its line; a line
that passes the wire decoder's line rules (`wire.UnmarshalRecord`); a summary that agrees with the last record and the count; times
within 1677-09-21 to 2262-04-11. A break is damage, never a value:

- The damaged record and every later record of its file are never handed out; the records before it and all other files stay
  readable. The warning `spool data damaged` (`file`: the file's name; `err`: `checksum mismatch`, `file cut short`, `not the spool
  format`, `unknown format version`, `inconsistent value`, `refused entry` or `too many files`) is logged once per file and start.
- A `spool_dropped` gap is written per run of unacknowledged dropped numbers where they can be known: from the last good number plus 1
  (the name number when no record is good) to the summary's last number when the summary is intact and in range, else to the next
  file's first number minus 1; `from` the latest capture time of the good records before the damage (else the previous file's latest),
  `to` the summary's latest (else the next file's earliest), a missing side one second from the other, both missing the spool's clock,
  `to` at least one second after `from`. A gap is never built from a damaged value. The newest file without an intact summary gets no
  gap for its tail, and a file whose name breaks a rule gets none and does not raise the next number: the backend's sequence detection
  finds those numbers.
- At the start a file with good records is cut at the last good frame and sealed; a file damaged as a whole (a header that is missing
  or broken, such as a file a crash left right after its creation, or no good record) is deleted once its gap is written. A torn tail —
  an incomplete last frame of the newest file, from a stop during a write — is cut with the warning (`file cut short`) and no gap: the
  hand-over it belonged to was never accepted. Refused entries and the data files beyond the 1,026 are never touched; the latter are
  logged once per start (`file`: the lowest such name, `err` `too many files`).
- Damage found by a read (`Read`, or the age of the self-metrics) ends that file's records in memory; its gap is written by that read
  when it fits without a drop, else by the next hand-over. In the file being written to, the run ends at the last number and the latest
  time the spool holds in memory, and the next hand-over starts a new file. The file is not cut on disk and is deleted once its good
  records are acknowledged or dropped. Stated residual: a restart before that finds the damage again, logs it again and writes a second
  gap for the same numbers.
- A meta file that is not JSON (`not the spool format`), of another version (`unknown format version`), with a missing, unknown or
  repeated key, more than 65 ranges, a range that is not sorted, overlaps another, starts at 0, has its first number above its last or
  reaches `next`, or a `next` at or above 2^63 - 1 (`inconsistent value`) is damaged (`file` `spool-meta.json`): its acknowledgements
  are discarded, so those records are handed out again, and the next number is taken as on a fresh spool.

**Another agent ID.** When `agent_id` changed, the start deletes every data file of another agent ID, writes no gap for it (a gap sent
under the new ID would describe another agent's data) and logs the warning `spool records of another agent dropped` (`records`: their
count); the new ID's numbers start as on a fresh spool.

**Durability and the stop.** A hand-over is written with one `write` and no `fsync`, so an accepted record survives a `kill -9` (the
page cache keeps it). A hand-over is written even when the caller's context is already cancelled, so the records of a run that ends
while the agent stops are kept; the context only accompanies the spool's log lines. Once a second, when something was written, the spool syncs the file being written to, the meta file and the
directory; at most one sync is in flight, outside the spool's lock, so a slow sync never holds up a hand-over, and a caller that finds
one running waits for its result. An OS crash or power loss therefore loses at most the records of about the last second, never a
damaged record handed out and never a number issued twice; the backend sees the loss as a sequence gap (#41), and the `kernel` and
`logs` collectors read their entries again because every state save first makes the spool durable (*Agent state*). The exception is
the boot and backup change written before the hand-over (*Kernel events* and *Backup runs*, residuals; #300). At the stop, after the
scheduler has stopped and the state file's last save, the spool is closed: synced, the meta file written with `clean` true and
synced. A close that fails or does not end within 5 seconds is given up with the warning `spool not closed`, and the agent exits as
usual. A disk whose writes hang is not covered: a hand-over that blocks holds up its collector, the watchdog is withheld and systemd
restarts the agent.

**Memory.** The spool's live memory stays below 4 MiB, apart from the records of a read or hand-over in progress, at every
`spool.max_size` up to 64 GiB, with 1,024 data files, 64 acknowledged ranges and 100,000 foreign entries: under 256 bytes per data file
(numbers, size, context, summary times), a sparse index (a number every file size / 64 bytes) for at most the two files the two read
positions are in, and one frame buffer; it does not grow with the number of records (`TestSpool_MemoryBound`). The two read positions are one for each
sender stream; the age read of the self-metrics (once a minute) takes a position as well, so it can replace one, and the stream whose
position was replaced then finds its place again through the sparse index, which costs reading time and never correctness. Opening a spool filled
to the default cap takes 26.2 ms and one of 1,024 files 15.9 ms on the development host, against a bound of 5 seconds
([BENCHMARKS](../BENCHMARKS.md), *Spool open*); the server is not measured (#44).

**Capacity.** The default `spool.max_size` holds 7 days of the **reference stream**, an estimate of the server that the tests generate
from a fixed seed, every record with varying content: per day 2,880 `system` samples of 110 metrics; 1,440 `filesystem` samples of 7
metrics, `clock` samples of 3 and `self` samples of 5; 1,440 process snapshots of 50 processes with command lines of 60 to 200 bytes
and 100 programs; 1,440 connection snapshots of 30 listeners, 40 owning processes and 25 remotes; 2,880 MariaDB status records of 60
status counters, 15 variables and 8 threads; 400 service states; 200,000 log lines of 40 to 400 bytes shaped like the lines of
`testdata/logs/` and `testdata/journal/`. That is 544,560 records and **209.0 MB per day** as uncompressed JSON lines
(`TestReferenceStream_FullScaleDay`), so 7 days take about 1.49 GB with the spool's framing, about 70 % of the 2 GiB default. The
full-scale 7-day run (`TestSpool_SevenDayOutageFullScale` with `VANDOX_LONG_TESTS=1`, without the race detector; 109.6 s on the
development host, 2026-10-23) kept all 3,811,920 records, held 1,576,818,758 of 2,147,483,648 bytes after the 7 days (73 %, the final
16-digit sequence numbers, headers and summaries included), dropped nothing, wrote no `spool_dropped` gap and read every record back
once in order. Every test run checks the same at one eightieth of every daily count (`TestSpool_SevenDayOutage`). The real volume is
measured on the server in #44; under sustained memory pressure the agent records much more, so less than 7 days may fit and the cap
drops the oldest with gaps (*Memory pressure mode*).

**Logging.** Through the agent's logger with `component=spool`, one line per change and never a path, a record's content or a system
error text other than an `errno` text: info `spool created`; warning `spool data dropped` and info `spool data no longer dropped`;
warning `spool write failed` and info `spool writes again`; warning `spool data damaged`; warning `spool records of another agent
dropped`; warning `spool not closed`.

## Sending

The sender (`internal/sender`) runs in the agent's process beside the scheduler, only with a spool (not with the test sink), and
reads the spool through `Read`, `Ack`, `Put`, `LastSeq` and `AckRanges`. Its transport is `sender.HTTPTransport`; the transport is a
seam, `agent.Options.Transport`, so tests send to a fake. The agent stops it with the scheduler, before the state file's last save and
the close of the spool. The agent starts without a reachable backend: a transport that cannot be built (a `backend.url` the transport
refuses; the loader has checked it, so this cannot happen with a loaded configuration) is the one start error, `agent cannot start`
with `sender: backend url: not usable`, never the URL.

**Two streams.** A **live** stream sends every 4 seconds the records the spool took since its previous send (`mode: live`, batches of
at most 20,000 records and 1 MiB of lines, one batch per request, batch after batch until nothing is left), and a **backfill** stream
sends the older, unacknowledged records oldest first (`mode: backfill`, the same limits). Only the backfill is throttled, to
`backend.backfill_bandwidth` of compressed request body bytes a second (the bodies of any span sum to at most the bandwidth times the
span plus one batch), and it pauses (info `backfill paused`, `reason=memory_pressure`, and `backfill resumed`) while the memory
pressure mode is on (*Memory pressure mode*) — the lines are logged only when records wait to be backfilled, also before a pass's
first batch, never when there is nothing to backfill; the live stream is never throttled. At the start the live stream begins after the newest
record, unless the spool already holds 32 acknowledged ranges (below), when it begins at the oldest unacknowledged record; the backfill
waits for the first acknowledgement of the live stream and then sends everything below that stream's first record (info `backfill
started` before its first batch and `backfill finished` with `records`, the number of records acknowledged, after the pass, both only when the pass found records to send). A request carries one
batch with the header `mode` as a hint (the backend classifies by lateness, [wire format](wire-format.md)). The batch is the capture
context of its records (agent ID, boot ID, clock offset), so a batch never mixes contexts.

**Acknowledgement.** A record is acknowledged in the spool only after a `2xx` answer, and a stream's position moves only after the
spool kept the acknowledgement (`Ack` returned). When the spool did not keep it (more than 64 ranges, the warning `acknowledgement not
kept` with `err`, once until an `Ack` succeeds, or a gap it could not take), the stream waits one wait of its own schedule and sends
the same records again; the backend stores a resent record once ([wire format](wire-format.md), *Batch identity*). A read the spool
refuses logs the warning `spool not readable` (`err`) once for the failure and retries after the same wait.

**Not accepting, one shared backoff.** An answer that is not `2xx` and not a refusal, and every failure without an answer (`timeout`,
`connection failed`, `tls failed`), is a backend that does not accept batches. The first one starts an **episode** that both streams
share: warning `backend not accepting batches` with `reason` (`timeout`, `connection failed`, `tls failed`, `token refused` for `401`
and `403`, `status` for any other status) once, and again only when the reason or the status changes; the backfill
sends nothing during the episode; the live stream makes one attempt after each wait, so an unreachable backend costs one request per
wait. The wait is drawn uniformly from half to the whole of 1 second, doubled after each failed attempt to at most 60 seconds. An
attempt sends the records the spool took since the previous attempt began, so current data is not held up by the old data; when the spool
holds 32 acknowledged ranges the attempt reads from the live position instead (no jump), which leaves no new hole. The first
acknowledged batch ends the episode (info `backend accepting batches again`, `seconds` the length) and releases the backfill. The
records between the live position and the jump stay unacknowledged until the backfill covers them: the **holes** are bounded at 32 so
the spool, which keeps at most 64 acknowledged ranges, never refuses an acknowledgement and deletes acknowledged files. A request that
hangs is ended after 30 seconds; once 4,096 bytes of an answer's body were read the request's context is cancelled so the transport closes the
connection instead of reading on. A failed delivery inside a backfill pass (an acknowledgement the spool did not keep, a wait for a free
give-up slot) does not end the pass: after its wait the pass reads again from its position, so it never moves past an unacknowledged
record.

**Refused batches and give-ups.** A `400`, `413` or `422` that carries the header `Vandox-Refused: batch` (exactly this value, compared
case-sensitively) refuses the batch itself ([0124](../decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md)):
it is not an episode. The sender halves the batch recursively and sends the halves in order; a record that is refused alone is
**given up**: the sender hands the spool a `gap` record (cause `unknown`, source `agent`, `first_seq` and `last_seq` the record's number,
`from` its capture time, `to` one second later, captured now), acknowledges the record, and logs the warning `record refused` (`status`,
`seq`, `kind`). A batch the wire encoder refuses is split the same way and gives its single record up with `status` 0. A `400` or `413`
without the header (a TLS listener reached with `http`, a proxy's size limit) is an ordinary `status` answer and gives nothing up. At
most **10 records are given up in an hour**, counted in the agent's memory (after a restart 10 more are allowed, a stated residual): at
the bound the stream waits until the oldest give-up is an hour old, logs the warning `backend not accepting batches` with
`reason=refused` and `status` once for the wait, and then sends the refused records again; the other stream goes on and no
acknowledgement of it ends that wait. A backend that refuses everything thus costs at most 10 records and a bounded number of requests an hour.

**Send lag.** `agent_send_lag_seconds` is the time since the capture of the newest record acknowledged since the start (before the first
acknowledgement, since the start), never negative.

**Memory and rate.** The sender holds one encoded batch for each stream, at most 1 MiB of lines compressed, independent of the backlog.
`TestSender_MemoryBound` measured, with a full batch in flight on both streams, 4.4 to 4.6 MB of heap held and a sampled peak of 8.3 to
8.6 MB under the race detector (7.5 to 7.8 MB without), the same at 600 and at 2,400 records of backlog, against the limit of 16 MiB.
`TestReferenceStream_BackfillRate` took 6,807 records of the reference stream (*Capacity*), 2,630,490 bytes of lines, to 556,335 bytes
compressed (gzip ratio 4.73), which at the default `256KiB` a second is 3,207 records a second, inside the band of 1,000 to 8,500
the test pins (on the development host; the real rate is measured in #44). At that rate the backlog of a day (544,560 records) takes about 3 minutes
of sending beside the live stream.

**Logging.** Through the agent's logger with `component=sender`, never the URL, the token, a host or an answer's text, only the fixed
lines above: warning `backend not accepting batches`, info `backend accepting batches again`, info `backfill started`, `backfill
finished`, `backfill paused`, `backfill resumed`, warning `record refused`, `acknowledgement not kept` and `spool not readable`. A stop
by the agent logs nothing.

## Rights

- The agent runs as the dedicated system user `vandox-agent` without a login shell, **never as root**. It authenticates to MariaDB as
  that OS user through `unix_socket` with only the `PROCESS` privilege.
- For reading it is a member of the groups `adm` (log files) and `systemd-journal` (journal). To read other users' processes it holds
  `CAP_SYS_PTRACE` alone, as an ambient capability with a bounding set limited to it (rule below, record 0122). `CAP_DAC_READ_SEARCH` is
  **not** granted: together with `CAP_SYS_PTRACE` it opens other processes' open files through `/proc/<pid>/fd/<n>` for writing, memfds
  included (measured below). The one collector that would use it, log shipping, does without it: a configured file its user and groups
  cannot read is logged and not shipped (below).
- **Process collector.** `stat`, `status` and `cmdline` of every process are readable by any user (unless `/proc` is mounted with
  `hidepid`); `smaps_rollup` of **another user's** process needs `CAP_SYS_PTRACE`, and `CAP_DAC_READ_SEARCH` does not replace it. Measured
  on the development host as UID 65534 against a root process in the same namespace, without Yama or `hidepid`: `smaps_rollup` answers
  `EACCES` without capabilities and with `CAP_DAC_READ_SEARCH` alone, and is readable with `CAP_SYS_PTRACE`. A Yama or other LSM policy,
  and another user namespace, were not measured and can refuse the read even with the capability. Without it the collector records the
  RSS from `status` and leaves the PSS out. `CAP_SYS_PTRACE` is the process collector's use of the capability; the list of rights per
  collector is in `deploy/agent/README.md`.
- **Connection collector.** It reads another user's `/proc/<pid>/fdinfo/<n>` (`mnt_id:`, `ino:`, the socket's table inode) and uses
  `CAP_SYS_PTRACE` alone. The `fd` links stay unread, because reading them needs `CAP_DAC_READ_SEARCH`, which lets the whole agent read
  every file (`/etc/shadow`, Plesk's credentials) and, with `CAP_SYS_PTRACE`, also enables the memfd write (*No write into another user's
  process or file beyond its own mode*); the agent does not hold it. Measured by Security on the development host (kernel 6.18.44, no Yama, no
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
- **Sender.** The sender needs no group and no capability: it dials the backend's ingest port as the agent's user. The unit must allow
  it: `RestrictAddressFamilies=` keeps **`AF_INET` and `AF_INET6`** beside `AF_UNIX`, and a system-call filter must not deny `socket` and
  `connect` for them, or every request fails as `connection failed`. (A restriction of the addresses the agent may reach, such as
  `IPAddressAllow=` for the backend, is left to the unit, #42.)
  With a host name in `backend.url` the agent also queries the name server of `/etc/resolv.conf` (port 53), which an `IPAddressAllow=` must include.
- **Systemd collector.** Reading unit states over the system bus needs no group and no capability: systemd's bus policy lets every user
  call `Properties.Get`, `ListUnitsByPatterns`, `ListUnitsByNames` and `LoadUnit` (the server capture was taken with every call made
  as `nobody`). The unit must keep the bus reachable: `RestrictAddressFamilies=` keeps `AF_UNIX` (also for this connection) and
  nothing hides `/run/dbus` (no `InaccessiblePaths=` or `TemporaryFileSystem=` over it), or the collector fails every run as a gap.
- **MariaDB collector.** It needs no group, no capability and no password: it connects to the MariaDB socket as the agent's OS user and
  logs in as the MariaDB user `vandox-agent`, which MariaDB's `unix_socket` plugin accepts for that OS user. `deploy/agent/mariadb-user.sql`
  creates the account and is its one source: `CREATE OR REPLACE USER 'vandox-agent'@'localhost' IDENTIFIED VIA unix_socket WITH
  MAX_USER_CONNECTIONS 2` and `GRANT PROCESS ON *.* TO 'vandox-agent'@'localhost'`, nothing else. It is run once as the database
  administrator (`mariadb < mariadb-user.sql` as root, or `plesk db < mariadb-user.sql` on Plesk) and may be run again, for example after a
  Plesk update dropped the user: `OR REPLACE` resets the account to exactly this state. `PROCESS` shows the status counters and the
  process list of every connection with its statement text; it gives no access to tables or their data. With `MAX_USER_CONNECTIONS 2`
  one connection stays free beside the agent's one. The unit must keep `AF_UNIX` in `RestrictAddressFamilies=` and the socket's directory
  `/run/mysqld` reachable (no `InaccessiblePaths=` or `TemporaryFileSystem=` over it), or every run fails (`mariadb: connect: permission
  denied`) or reads as `down`.
- As soon as the unit grants any capability, the unit is confined: `NoNewPrivileges=yes`; a system-call filter that denies `ptrace`,
  `process_vm_readv`, `process_vm_writev`, `pidfd_getfd`, `open_by_handle_at` and `process_madvise` with `EPERM` (and not `pidfd_open` or
  `pidfd_send_signal`) under `SystemCallArchitectures=native`; no write-side capability; `ProtectHome=yes` and `InaccessiblePaths=` for at
  least `/etc/shadow` and `/etc/gshadow`. The last two protect only against accidental reads; they are not a limit on a compromised
  agent, which can reach the same files through another process's root in `/proc` where their own mode allows it. `process_madvise` is denied by name: systemd puts it
  in the group `@ipc`, which `@system-service` includes, so an allow-list built on `@system-service` admits it. The agent never calls it
  (the Go runtime uses `madvise` on its own memory only). That the server's systemd (249, Ubuntu 22.04) knows the name, so the entry takes
  effect instead of being skipped, is checked on the server (#42): `systemd-analyze syscall-filter @ipc` lists `process_madvise`. If it
  did not, barrier (c) below would still stop the write.
- **No write into another user's process or file beyond its own mode** (record 0122). A compromised agent cannot use its capability to
  write into another user's process or file, or to run code as another user, except through a file whose own mode grants the agent's
  user, its groups (`adm`, `systemd-journal`) or others write (accepted residual, below). No single mechanism stops every vector
  examined; three distinct barriers do, and one vector is the accepted residual:

  | Write vector | Barrier |
  | ------------ | ------- |
  | Opening another user's `/proc/<pid>/mem` for writing (`O_RDWR`, `O_WRONLY`), then `pwrite` | (a) DAC and the bounding set: the file is mode `0600`, owned by the target's user, so the open needs `CAP_DAC_OVERRIDE` |
  | Opening another owner-only writable `/proc/<pid>/` file of another user for writing (for example `clear_refs`) | (a), the same DAC check as for `mem` |
  | Opening `/proc/<pid>/fd/<n>` of another user's process (an open file or memfd it holds), then `pwrite` | (a) DAC and the bounding set: the directory is mode `0500`, owned by the target's user, so the lookup needs `CAP_DAC_READ_SEARCH` |
  | Following a link in `/proc/<pid>/map_files/<range>` of another user's process | (a) the bounding set: the link needs `CAP_SYS_ADMIN` or (since kernel 5.9) `CAP_CHECKPOINT_RESTORE`, besides search of the `0500` directory |
  | `ptrace` (POKE), `process_vm_writev`, `pidfd_getfd` (taking a writable descriptor), `open_by_handle_at` | (b) the system-call filter |
  | `pidfd_open` on the target, then `process_madvise` | (b) the filter denies `process_madvise`; behind it (c) the kernel: the call needs `CAP_SYS_NICE` (since kernel 5.12), and the kernel refuses destructive advice on another process |
  | A file whose own mode grants the agent write, reached through another process's `/proc/<pid>/cwd` or `/proc/<pid>/root` link, or that process's executable through its `/proc/<pid>/exe` link (each also below `/proc/<pid>/task/<tid>/`) | none: the accepted residual (last bullet of *Rights*) |

  The filter denies neither `openat` nor `pwrite`, so it is not what stops the `mem` and `fd` writes; barrier (a) is. Barrier (c) is
  defence in depth only: the `CAP_SYS_NICE` requirement is a capability rule, but which advice the kernel accepts for another process is
  memory-management policy that has grown between versions. **The bounding set holds `CAP_SYS_PTRACE` only; adding any capability to it
  is a `security` change that supersedes record 0122**, and so is removing one of the six denied calls. For example,
  `CAP_DAC_READ_SEARCH` reopens the write through `/proc/<pid>/fd/<n>` into other processes' open files, memfds included, and below
  closed directories; `CAP_DAC_OVERRIDE` and the other write-side DAC capabilities the write open of `mem`; `CAP_SYS_NICE`
  `process_madvise` on another process; `CAP_SYS_ADMIN` or `CAP_CHECKPOINT_RESTORE`, together with directory search, the links in
  `/proc/<pid>/map_files/`. These are examples, not a closed list. `NoNewPrivileges=yes` keeps the agent from gaining a capability
  outside the set. `proc_mem.force_override=never` (kernel 6.12+) is not required and not set: it limits `FOLL_FORCE` only once a
  write-capable descriptor exists, which the agent cannot obtain, and the production kernel (5.15) predates it.

  Measured on the development host (kernel 6.18.44, same user namespace, no Yama or other LSM restriction), as UID 65534 against a
  `sleep` owned by root. Each capability set is given with `capsh --user=nobody --inh=<caps> --addamb=<caps> -- -c <probe>` and checked as
  `CapEff` in the probe's `/proc/self/status` (`0x80000` for `CAP_SYS_PTRACE`, the agent's set; `0x80004` with `CAP_DAC_READ_SEARCH`
  added). The `mem` probe opens and closes the target's `/proc/<pid>/mem` and never writes:

  | Capabilities | `O_RDONLY` | `O_RDWR` | `O_WRONLY` |
  | ------------ | ---------- | -------- | ---------- |
  | `CAP_SYS_PTRACE` (the agent's set) | `EACCES` | `EACCES` | `EACCES` |
  | `CAP_SYS_PTRACE` + `CAP_DAC_READ_SEARCH` (not granted) | ok | `EACCES` | `EACCES` |
  | `CAP_SYS_PTRACE` + `CAP_DAC_OVERRIDE` (control) | ok | ok | ok |

  The `fd` probe (measured by the Product Manager on four kernels): a root `target` maps a memfd `MAP_SHARED` and prints its first bytes;
  the probe runs as `nobody` under `capsh` with ambient capabilities, opens the target's `/proc/<pid>/fd/<n>` of that memfd `O_RDWR` and
  writes four bytes with `pwrite`:

  | Host | Kernel | none | `CAP_SYS_PTRACE` (`0x80000`, the agent's set) | `CAP_SYS_PTRACE` + `CAP_DAC_READ_SEARCH` (`0x80004`, not granted) |
  | ---- | ------ | ---- | --------------------------------------------- | ----------------------------------------------------------------- |
  | NAS | 4.4.302+ | open `EACCES` | open `EACCES` | open ok, `pwrite` of 4 bytes ok, the target shows them |
  | Ubuntu 22.04.5 | 5.15.0-198-generic | open `EACCES` | open `EACCES` | open ok, `pwrite` of 4 bytes ok, the target shows them |
  | Ubuntu 24.04.5 | 6.8.0-147-generic | open `EACCES` | open `EACCES` | open ok, `pwrite` of 4 bytes ok, the target shows them |
  | Ubuntu 26.04.1 | 7.0.0-38-generic | open `EACCES` | open `EACCES` | open ok, `pwrite` of 4 bytes ok, the target shows them |

  The open needs both capabilities: `CAP_DAC_READ_SEARCH` for the lookup in the `0500` directory and `CAP_SYS_PTRACE` for following the
  link; the opened file is then checked against its own mode only, which for a memfd is `0777`. On the development host (6.18.44) the
  open alone was repeated without a write: `/proc/<pid>/fd/<n>` and `/proc/<pid>/task/<tid>/fd/<n>` answer `EACCES` with
  `CAP_SYS_PTRACE` and open `O_RDWR` with both. `CAP_DAC_READ_SEARCH` alone also opens for writing a `0666` file inside a `0700`
  directory of root (open and close only); with `CAP_SYS_PTRACE` alone or without a capability that open fails `EACCES`.

  The `process_madvise` probe calls `pidfd_open` on the target, which succeeds without a capability, and then `process_madvise` on the
  returned descriptor with each advice on the target's first read-only mapping:

  | Capabilities | `MADV_COLD`, `MADV_PAGEOUT`, `MADV_WILLNEED` | `MADV_DONTNEED`, `MADV_FREE` |
  | ------------ | -------------------------------------------- | ---------------------------- |
  | none | `EACCES` | `EACCES` |
  | `CAP_SYS_PTRACE` (the agent's set) | `EPERM` | `EINVAL` |
  | `CAP_SYS_PTRACE` + `CAP_DAC_READ_SEARCH` (not granted) | `EPERM` | `EINVAL` |
  | `CAP_SYS_NICE` | `EACCES` | `EACCES` |
  | `CAP_SYS_PTRACE` + `CAP_SYS_NICE` (control) | accepted | `EINVAL` |

  The `CAP_SYS_NICE` requirement applies to every process other than the caller, not only another user's: as UID 65534 without
  capabilities against its own `sleep` the call gives the same `EPERM` and `EINVAL`. Under the accepted advice the target's page content
  stayed unchanged (Security's probe, which also tried `MADV_COLLAPSE`: `EINVAL`). On the production kernel (5.15) the `CAP_SYS_NICE`
  requirement holds by the kernel's history (since 5.12); the DAC result of the `mem` table rests on file-permission semantics that do
  not depend on the version; the advice set there was not measured.
- The system metrics need no right of their own: `/proc/stat`, `/proc/loadavg`, `/proc/meminfo`, `/proc/vmstat`, `/proc/pressure`,
  the agent's own mount table and `/sys/block` are readable by any user, `statfs` needs only search permission on the directories
  above the mount point, and `adjtimex` without a change needs no capability (measured: unprivileged and without capabilities,
  `adjtimex` with modes 0 and `statfs` succeed, while an `adjtimex` that sets the maximum error fails with `EPERM`). The unit's system-call filter must allow `adjtimex`: `ProtectClock=yes` denies it with `EPERM` even for a
  read, and the group `@system-service` does not contain it, so an allow-list names it. A change of the clock still needs
  `CAP_SYS_TIME`, which the bounding set excludes.
- **Kernel event collector.** It reads the journal files below `/var/log/journal` and `/run/log/journal` as a member of
  `systemd-journal`, its first user, and `/etc/machine-id`, `/proc/sys/kernel/random/boot_id`, `/proc/uptime` and `/proc/stat`, which
  any user can read; it needs no capability (not `CAP_SYSLOG`: it does not read `/dev/kmsg`). The unit must keep both journal
  directories readable (no `InaccessiblePaths=` or `TemporaryFileSystem=` over them) and start after `systemd-journal-flush.service`.
  That the server's journal files are readable by the group is checked on the server (#42).
- **Log shipping collector.** It reads the system and user journal files with `systemd-journal`, as the kernel collector does,
  `/etc/machine-id` and `/etc/localtime`, which any user can read, and the files `log_shipping.files` names with `adm`, or because their
  mode lets any user read them. It uses no capability: `CAP_DAC_READ_SEARCH` is not granted (record 0122). It opens the configured paths through the
  walk of *Log shipping* that refuses a symbolic link in any element and a file with more than one link, so a service user who replaces
  its log file or a directory with a link to another file (`/etc/shadow`) never gets that file shipped. A file that the agent's user and
  groups cannot read, or that lies below a directory they cannot search, is logged `log file not readable` with the reason
  `permission denied` and not shipped. The unit must keep the journal directories and the configured files reachable (no
  `InaccessiblePaths=` or `TemporaryFileSystem=` over them).
- **Backup check.** It reads `/proc` as the process collector does and needs no right of its own.
- **State file and spool.** The agent writes only in `spool.directory`: `agent-state.json` (*Agent state*) and the spool's own files
  (`segment-*.spool`, `spool-meta.json` and its temporary file; *Spool and sending*), all with mode 0600. The installation creates that
  directory, writable by the agent's user and not a symbolic link, and the agent never creates it; without it, or when the agent cannot
  write it, the agent does not start. The spool needs no right beyond the directory: it reads its boot ID from
  `/proc/sys/kernel/random/boot_id`, which any user can read.
- For restarting units (self-healing, a later feature) a polkit rule lets this user restart only the units named in the configuration;
  there is no sudo and no shell.
- Each group, capability and confinement setting is listed and justified in `deploy/agent/` when the collector that needs it is
  introduced; anything not listed is not granted. Removing a confinement setting, adding a capability or a group, or adding a collector
  that needs a new right is a `security`-tier change; removing a confinement while a capability is granted needs a superseding record.
- **Accepted residual** (record 0122): with `CAP_SYS_PTRACE` the agent follows another process's `/proc/<pid>/cwd`,
  `/proc/<pid>/root` and `/proc/<pid>/exe` links and their `/proc/<pid>/task/<tid>/cwd`, `…/root` and `…/exe` variants, which skip the
  search check of the directories above the file they lead to. So a file whose own mode grants the agent's user, its groups or others
  read or write is reachable even below a directory closed to the agent, but only along paths from some process's working or root
  directory, or as that process's executable through `exe`; the path components after a `cwd` or `root` link are still checked against
  their own modes, so a closed directory below that point still blocks, and the file `exe` leads to is checked against its own mode
  only. The `root` link of a process in another mount namespace (a container's) resolves in that namespace, so the same holds for its
  tree. Whether opening a running executable for writing fails with `ETXTBSY` depends on the kernel version and was not measured; it is
  not counted as a barrier. A write into such a file reaches
  another user's process memory where that process maps the file `MAP_SHARED`, and runs code as another user where that user executes
  the file or reads it as a script or configuration. Readable besides are the ptrace-checked `/proc` files whose mode allows reading
  (`maps`, `smaps_rollup`, `fdinfo`, …), not another user's `mem` or `environ` and not `/etc/shadow` (measured on kernel 6.18.44 with
  `CAP_SYS_PTRACE` alone: `EACCES`, directly and through `/proc/<pid>/root`; `maps` and `smaps_rollup` of a root process open, and a
  `0666` file in a `0755` directory below a `0700` directory of root opens for writing through `/proc/<pid>/cwd/` of a root process
  whose working directory is the `0755` directory); credentials read that way may still lead to root through other services (password reuse, Plesk or MariaDB
  administration). A separate small helper that holds the capability is the way to reduce it.

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
  alone), records a regular or a full snapshot in the one record kind, bounds the new-listener check, favoring privileged ports, and
  records the socket's `uid` from the tables with a name from `/etc/passwd` (the lowest UID and `mixed_uids` per endpoint, none for inode 0).
- [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md) — why the first snapshot after
  a start is marked `start`, every snapshot says what its listener list holds, and the backend compares across the start.
- [0117](../decisions/0117-systemd-collector-hand-written-d-bus-client-per-run-states-recorded-on-change.md) — why the systemd collector
  reads unit states over the system bus with a minimal client of its own and one connection per run, polls, records on change, after a
  start or a lost sample and hourly, and tracks patterns up to 256 services.
- [0118](../decisions/0118-mariadb-collector-hand-written-client-captured-statements-refusals-as-not-answering-with-error-code.md) — why the
  MariaDB collector speaks the protocol with a minimal client of its own over one connection per run, sends the captured statements with
  the captured layout bits, records a refused login as `not_answering` with its error number, keeps the statement text of only the first
  64 threads, and counts exposure from the kernel's TCP tables.
- [0119](../decisions/0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md) — why the kernel
  messages are read from the binary journal files in pure Go, set-aside files included, without decompression and only in the two
  layouts journald writes (the server's and the compact one), why only the kernel transport with facility 0 counts, and why a kill is
  timed by its receipt corrected for the kernel clock's drift.
- [0120](../decisions/0120-oom-kill-events-triggered-snapshots-boots-from-saved-state-and-backup-runs.md) — why a live kill requests
  extra snapshots through the scheduler and names them by its identity, why boots come from a saved state file, why the journal
  position is committed only after a kept run with at most three re-reads, and why backup runs are a record kind of their own with
  `pzstd` as the default rule.
- [0121](../decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md) — why the log
  collector reads every system and user journal file with a position per sequence-number ID, decodes zstd items with a library under
  fixed bounds, opens configured files only through a link-free walk, stores their positions by the hash of the path, commits when the
  scheduler reports a run kept and saves once more at the stop, and bounds drop patterns by their compiled size.
- [0122](../decisions/0122-cap-sys-ptrace-alone-cannot-write-another-users-process-memory-through-proc-pid-mem-or-fd.md) — why the
  agent holds `CAP_SYS_PTRACE` alone and not `CAP_DAC_READ_SEARCH`, which barrier stops each write vector examined, which residual
  remains (files whose own mode grants the agent write, reached through other processes' `cwd`, `root` and `exe` links), why the bounding set
  holds `CAP_SYS_PTRACE` only, and why the filter also denies `process_madvise`.
- [0123](../decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md) — why the spool keeps
  uncompressed segment files of checksummed frames, takes a fresh spool's numbers from the clock bound, keeps at most 64 acknowledged
  ranges, bounds its file size and count, reaches its files only relative to the directory's descriptor, and makes the state file wait
  for the spool's sync.
- [0124](../decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md) — why a request carries one batch,
  is acknowledged by its status alone and marks a refusal of the batch itself with a header.
- [0125](../decisions/0125-sender-two-streams-shared-backoff-refused-records-given-up-with-a-gap.md) — why the sender has a live and a
  backfill stream with one shared backoff, gives refused records up with a recorded gap at most 10 an hour, bounds the holes at 32 and
  pauses the backfill under memory pressure.

## Not here

- The format of batches and records: [wire format](wire-format.md).
- Which options the agent reads and how secrets are supplied: [Configuration and secrets](configuration-and-secrets.md).
- What the backend does with the data, including gap detection: the storage and detection areas.

## Implementation

`cmd/vandox-agent` and `internal/cli` (Go), `internal/agent` (start-up, logging, watchdog), `internal/scheduler`, `internal/selfstats`,
`internal/sysstats` (system metrics and the memory pressure mode; fixtures `testdata/proc/`, `testdata/sys/`),
`internal/procstats` (process collector; fixtures `testdata/proc/<pid>/`, `testdata/etc/passwd`, `testdata/processes/names.json`; `ListPIDs`, `CommandReader`, `Ended`, `ReadUsers` and `UserName` are shared with the connection collector),
`internal/connstats` (connection collector; fixtures `testdata/proc/net/`, `testdata/connections/sandbox/`, `testdata/etc/passwd`), `internal/sockmount` (the start-up probe of the socket mount),
`internal/svcstats` (systemd collector) and `internal/sdbus` (its D-Bus client; fixture `testdata/systemd/`, a capture of systemd 249's replies),
`internal/mariadb` (MariaDB collector and its protocol client; fixture `testdata/mariadb/`, two captures of MariaDB 12.3.3's replies to a `unix_socket` login; it uses `sysstats.MemTotal`, `connstats.TCPListeners` and `procstats.CleanText`),
`internal/journal` (the journal file reader) and `internal/journal/journaltest` (a writer of journal files for tests; fixtures
`testdata/journal/`: an online, an archived and a set-aside file generated from `kernel.export`, the same three in the compact layout
under `testdata/journal/compact/`, and under `testdata/journal/systemd-255/` two compact files systemd-journal-remote 255 wrote from
`kernel.export` and `live.export` (header machine ID replaced); `live.export`, `live-records.jsonl` and
`zstd-frames.txt` for the log collector, whose binary files the writer generates at test time with zstd frames), `internal/kernevents` (kernel event
collector and boot check), `internal/logship` (log shipping collector: journal, configured files with the link-free open, filter,
positions; fixtures `testdata/logs/mail.log`, `kern.log-oom`), `internal/syslogline` (the syslog line rules, the year-less time and
`/etc/localtime`), `internal/zstdfield` (the bounded zstd decoder over `github.com/klauspost/compress`), `internal/backups` (backup run
check), `internal/agentstate` (the state file, the `state` job and the save at the stop), `internal/sender` (the sender, its HTTP transport and the backfill throttle; tests use a fake transport and `net/http/httptest`; the
measurements `TestSender_MemoryBound` and, in `internal/spool`, `TestReferenceStream_BackfillRate`), `internal/spool` (the spool: data and meta
files, sequence numbers, reading, acknowledgements, drops and damage; its tests generate the reference stream and run the hard-kill and
7-day tests; it uses `wire.MarshalRecord` and `wire.UnmarshalRecord`),
`internal/sdnotify`, `internal/config`, `internal/model`, `internal/wire`; `deploy/agent/` (example configuration, `mariadb-user.sql`; the unit and install
scripts follow with the collectors).
