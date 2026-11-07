# Agent

## Scope

What `vandox-agent`, the program on the monitored server, must do and must never do as a whole: its command line and start-up, its log,
the outline of its outbound-only connection, how it collects and schedules its collectors so that a stalled data source never blocks it,
its self-metrics, the memory pressure mode, its notifications to systemd, its recorded gaps and its state file. Each collector, the log
shipping, the spool, the sender, the rights and the installation have documents of their own (*Not here*). The agent is implemented;
fetching commands from the backend and restarting units (remote actions, self-healing) are planned.

## Command line and start-up

- `vandox-agent` without arguments runs the agent with the configuration file `/etc/vandox/agent.yaml`; `-config <path>` names another
  file (any non-empty path, which the loader checks). `-version` prints the version line and exits with 0; `-h` and `-help` print the
  usage and exit with 0. An unknown flag, a positional argument or an empty `-config` is a usage error: the usage on standard error,
  exit code 2.
- At start-up the agent reads the configuration file and the agent token as
  [Configuration and secrets](configuration-and-secrets.md) describes. When that fails it logs one error line with the loader's error
  text and exits with 1, before it notifies systemd of anything. It then opens its state file (*Agent state*) and, after it, its spool
  ([Agent spool](agent-spool.md)); a spool that cannot be opened ends the start the same way, with the error line `agent cannot start`.
- It runs until it receives SIGTERM or SIGINT, then stops its collectors (a collector stuck in a run is not waited for), lets the
  collectors that hold lines put them ([Agent log shipping](agent-log-shipping.md), *Classification*, **The stop flush**), saves its state (*Saving at the
  stop*), tells systemd that it is stopping and exits with 0.

## Logging

- The agent writes its log to standard error, one line per entry, as `key=value` pairs at the configured `log.level`. A value that
  contains a space, a quote, a control character or any other character that is not printable is quoted with its control characters
  escaped, so no value can start or forge a line: line breaks, `<N>` inside a value, U+2028, U+202E, invalid UTF-8 and control characters
  in keys all stay inside one line.
- The message and every text value are cut to 4 KiB, and every line, priority prefix and newline included, to 32 KiB, at a character
  boundary — below the journal's default line limit of 48 KiB (`LineMax=`), so the journal never splits an entry and no continuation can
  start with a forged priority. A value whose text form fails is logged as `<nil>` (a nil pointer) or `!PANIC: <value>`.
- When the journal stream is standard error — `JOURNAL_STREAM` is set exactly once as `<device>:<inode>` in decimal and names the device
  and inode of the agent's standard error — every line starts with its syslog priority (`<7>` debug, `<6>` info, `<4>` warn, `<3>` error),
  which the journal takes as the entry's priority, and carries no time stamp of its own. Otherwise (unset, malformed, set twice, or naming
  another stream) lines carry their time and no prefix, without a warning.
- The configuration and the agent token are never passed to the log; the start-up line names only the agent ID and the version. Every
  collector logs through a logger that adds `collector=<name>`, one line per change of state rather than per run; each collector's
  document lists its lines and what their attributes may hold.

## Connection

- The agent only opens **outbound** connections to the backend's ingest port and **never listens on a port**; the monitored server needs
  no inbound firewall rule for Vandox.
- The sender is the one part that dials the backend, with the agent token sent to `backend.url` only; the client, its transport rules
  (no proxy, no redirect, TLS, binding to `tailscale0`, timeouts) and the allowed schemes are in [Agent sending](agent-sending.md), the
  unit's address allow-list in [Agent installation](agent-installation.md).
- Commands for the server (planned) are fetched by the agent from the backend, never pushed to it, so they reach the server only at the
  agent's next poll.
- The agent never contacts Telegram or any other service; all notification goes through the backend.
- Four local channels are not network connections and need no rule: the log on standard error, which systemd passes to the journal; the
  notification datagrams to the socket systemd names (*Service manager*); the connection to the system bus socket
  `/run/dbus/system_bus_socket` that the `systemd` collector opens for each run ([Agent services and backup runs](agent-services.md)); and
  the connection to the MariaDB Unix socket (`mariadb.socket`, default `/run/mysqld/mysqld.sock`) that the `mariadb` collector opens for
  each run ([Agent MariaDB collector](agent-mariadb.md)). The agent binds no socket for them.

## Collecting

- Process, memory and connection data come from **reading `/proc` and `/sys` directly**, never from running `top`, `lsof` or other
  programs. Collectors read the kernel files through a replaceable file-system interface, never through hard-coded paths, so tests can
  substitute fixtures and hanging files. Kernel messages and journal log lines come from reading the journal's files directly, never from
  `journalctl`.
- Linux only, matching the monitored server.
- **A hanging collector or database never blocks the agent.** Every collector runs isolated under its own deadline. When the deadline
  passes the agent stops waiting for it, records its missed sample as a gap, and keeps running the other collectors, the spool and the
  sender. A collector still stuck in a previous run is not started again; each skipped cycle is a gap as well. Sources that accept a
  cancellation signal also receive the deadline. A hung source costs at most one stuck worker per collector.
- Every collector needs a deadline and a test with a double of its source that hangs: the cycle completes on time, a gap is recorded and
  the stuck collector is not started a second time.

## Scheduling

### Schedules and runs

- Every collector has a name (the wire format's name pattern, unique in the agent), an **interval**, a **timeout** of at most the
  interval and optionally a **pressure interval** between the timeout and the interval. A schedule that breaks these rules stops the agent
  at start-up. The jobs run in a fixed order; each collector's document says where it is placed.
- Runs follow a fixed rate: the first run starts when the agent starts, each further one an interval after the previous run's due time,
  however long the previous run took. Each collector has its own runner, so no collector waits for another.
- A run that has not finished at its timeout is **abandoned**: the agent stops waiting, records a gap with cause `collector_timeout` and
  logs one warning for the stuck episode. While it has not returned, every due run of that collector is **skipped** and recorded as a gap
  with cause `collector_timeout`; the collector is not started a second time. When the abandoned run returns, its result is dropped, one
  line names how many runs were skipped, and the collector runs again at its next due time.
- A run that **panics** or returns an **error** loses its sample: the panic is recovered, the sample is a gap with cause `unknown`, and
  the collector runs again at its next due time. A panic is logged as an error with the collector's name, the panic value (at most 1,024
  bytes) and the stack (at most 4 KiB) as attributes. An error is logged at the first failure after a success (`collector failed`), and
  the next success logs how many runs failed (`collector recovered`), so a collector failing every run does not flood the log. The records
  of a failed run are dropped, also those returned together with its error.
- A run that starts a whole interval or more after its due time (the agent was frozen or not scheduled, or its previous cycle was held up)
  records the missed span, from the due time to the actual start, as a gap with cause `unknown` and runs at once; the next run is an
  interval after that. A run late by less than an interval runs at once without a gap and keeps the grid.

### Lost samples and kept runs

- A run is told whether the previous sample of its collector was **lost**: its result was not kept, because the run timed out, was
  skipped, returned an error or panicked, had a record refused, or the sink refused its records. A run that starts late does not set it; a
  run whose records were all kept and accepted clears it. A collector that records only changes uses it to record its full state again;
  the `kernel` collector reads the lost run's entries again ([Agent kernel events and boots](agent-kernel-events.md)); the `logs`
  collector counts a lost run it was not told about as dropped ([Agent log shipping](agent-log-shipping.md)); the other collectors ignore
  it.
- A collector may ask to be **told whether a run was kept** (an observer): after the delivery of a run that finished in time the
  scheduler tells it the run and whether its records were kept — true when the run returned no error and did not panic, no record was
  refused and the sink accepted the records. An abandoned or skipped run tells nothing; its loss shows only in the next run's lost flag.
  The `logs`, `kernel` (the boot) and `backups` collectors are observers.

### Extra runs

An **extra run** of a collector is requested through the scheduler (the `kernel` collector does so for `processes` and `connections`). A
runner waiting for its next due time starts it at once; a runner in a run starts it when that run ends. Requests made before the extra run
starts merge into one, which names at most 64 kills and counts the rest. The extra run starts when it begins and knows its request (the
kills its record names in `trigger`); otherwise it is a run like any other: it is the next run's predecessor, it sets and clears the lost
flag, and a timeout, panic or error records a gap over one interval of the current mode from its start (`collector_timeout`, `unknown`),
after which an abandoned extra run makes the due runs skips as above. While an abandoned run of the collector is out, no extra run starts:
the request records a gap with cause `collector_timeout` from that moment over one interval of the current mode. Without memory pressure
mode, or for a collector without a pressure interval, the grid is unchanged; under pressure mode a collector with a pressure interval counts
its next due time from the extra run's start, so the extra run takes the place of the next regular one.

### Records and gaps

- Every record of a successful run is stamped as an agent record (origin `agent`, the capture time of the run's start where the collector
  set none) and checked against the [wire format](wire-format.md), size included (*Producer size contract*). The size is checked as if the record
  carried the largest storable sequence number, so the check is an upper bound for any number the spool assigns. A record that fails is
  dropped, the sample gets one gap with cause `unknown`, and the run's other records are kept.
- The gap of a missed sample covers the slot the sample stands for, from its due time to one interval later, so consecutive missed samples'
  gaps join; its `collector` names the collector and its source is `agent`. A late start's gap covers the span from the missed due time to
  the actual start. Each gap's capture time is the moment it was recorded, so the gap of an abandoned or skipped run ends after its
  capture time.
- Records go to the sink with the agent's own context, not one bounded by the run's timeout. When the sink refuses records, the agent logs
  the error (at the first failure, and the recovery once) and records no gap itself: the spool that refuses records owns recording their
  loss ([Agent spool](agent-spool.md)). The spool assigns the sequence numbers. A hand-over is never refused because the agent's context
  ended, so a stop does not lose the records of a run that finished.

### Pressure switch and timeliness

- **Memory pressure mode** is switched at run time (*Memory pressure mode*); switching to the mode in force changes nothing. When the mode
  changes, a collector with a pressure interval takes its next due time from its last due time plus the interval of the new mode, but never
  earlier than the moment of the switch: a switch long after the last run starts a run at once, and from then on the collector keeps the
  new interval's grid. A switch never makes a run late and never records a gap; a collector held up past the new due time by a whole
  interval records the span from that due time as a late start. Collectors without a pressure interval are unchanged. Every run knows
  whether the mode was on when it started.
- A runner is **on time** while the current moment is not later than the due time of the run it is in or waiting for, plus its timeout
  plus 30 seconds; a runner waiting for an abandoned run is on time (it moves on at each due time), and a pressure switch moves the due time
  with it. The watchdog is fed only while every runner is on time (*Service manager*).

## Self-metrics

The collector `self` (interval 60 seconds, timeout 10 seconds) records the agent's own resource use as `metric` records with source
`agent`: its resident memory, its CPU time, its number of goroutines, the size of the spool's files (`agent_spool_bytes`), the age of the
oldest unacknowledged record in the spool (`agent_spool_age_seconds`) and how far sending lags behind capture (`agent_send_lag_seconds`,
from the sender). Names, units and values are in the [wire format records](wire-format-records.md) (*Metric names*, the `agent_*` rows).

The resident memory is read from at most 64 KiB of the agent's own `/proc` status file, and its line must be `VmRSS:`, spaces or tabs, 1
to 12 ASCII digits, one space and `kB`; anything else, a failed read, a negative spool size, an error of the spool's size or age and a
negative send lag fail the sample, which becomes a gap. The spool's age is the sample's start minus the capture time of the oldest
unacknowledged record; it is 0 when the spool holds none and when that capture time lies after the sample's start, never negative.

## Memory pressure mode

The `system` collector decides the mode after each successful `system` sample, from the values it has just read
([Agent system metrics](agent-system-metrics.md)):

- the mode turns **on** when `MemAvailable` is below 10 % of `MemTotal` (10 × `MemAvailable` < `MemTotal`) or the memory `some avg10` is
  at least 10.00;
- it turns **off** only when `MemAvailable` is at least 15 % of `MemTotal` (20 × `MemAvailable` ≥ 3 × `MemTotal`) and `some avg10` is
  below 5.00 or the memory pressure file is not available;
- otherwise it keeps its mode. The products are exact (128 bits, no floating point). The thresholds are fixed, not options, until the parallel run of #45 shows a reason.

The mode starts off. Only a change switches the scheduler (*Scheduling*) and the sender ([Agent sending](agent-sending.md)) and logs one
info line `memory pressure mode on` or `memory pressure mode off` with `collector=system`, `mem_available_bytes` and, when the pressure
file was read, `memory_some_avg10`. A failed sample changes nothing. Under the mode `system` (10 s), `processes` (15 s), `connections`
(15 s), `systemd` (10 s) and `mariadb` (10 s) run at their pressure intervals, `processes` lists every process, `connections` records full
snapshots and `mariadb` records the thread snapshot; an extra run of `processes` or `connections` for an OOM kill takes the place of the
next regular run, so a storm of kills costs no more snapshots than the mode already takes.

The switch can lag the pressure by up to one `system` interval (30 seconds). Any local user can switch the mode on by allocating memory;
the result is a full process snapshot every 15 seconds and a spool that grows faster until its size bound evicts older data with a
recorded gap, bounded by the record budget (residual).

## Service manager

- When systemd started the agent with a notification socket, the agent sends `READY=1` once its collectors run, `WATCHDOG=1` while the
  watchdog is enabled, and `STOPPING=1` when it stops. Each notification is one datagram; dial and write have a deadline of one second, so
  a socket whose queue is full (measured: about 11 queued datagrams) makes the notification fail at that deadline instead of blocking
  the agent.
- The socket is the one `NOTIFY_SOCKET` names: an absolute path of at most 107 bytes, or `@` and an abstract name of 1 to 107 bytes. Any
  other value (a relative path, `@` alone, a longer name, the `vsock:` forms) disables the notification with a warning; the agent runs the
  same. Unset or empty means no notification and no warning.
- The watchdog is enabled when `WATCHDOG_USEC` holds 100,000 to 9,223,372,036,854,775 microseconds in ASCII digits (a period below
  100 ms is refused, so the agent never pings in a busy loop) and `WATCHDOG_PID` is unset or the agent's process ID; the agent then sends
  `WATCHDOG=1` every half of that period while all runners are on time (*Scheduling*). When a runner is not on time, the notification is
  withheld and one warning is logged until it is sent again, so systemd restarts an agent whose runners stopped moving. A malformed value
  disables the watchdog with a warning.
- `NOTIFY_SOCKET`, `WATCHDOG_USEC` or `WATCHDOG_PID` set twice in the environment is malformed.
- The unit must use `Type=notify`, set `WatchdogSec=` and allow notifications from the main process (`NotifyAccess=main`); the installed
  unit does, with `WatchdogSec=2min`, `Restart=on-failure`, `RestartSec=30s` and `StartLimitIntervalSec=0`, so a failed, watchdog-stopped
  or memory-limit-killed agent is restarted twice a minute at most, for ever. It limits the agent to `MemoryMax=256M` with
  `GOMEMLIMIT=192MiB`, `CPUQuota=50%`, `Nice=5` and `IOWeight=50` (the reasons are in `deploy/agent/README.md`, *Unit*; [Agent installation](agent-installation.md)).

## Gaps

**There is no data gap unless it is explicitly recorded.** The agent records the gaps it knows about: a collector sample missed or
skipped (*Scheduling*), a span of kernel messages it cannot read ([Agent kernel events and boots](agent-kernel-events.md)), a span of log
lines it cannot ship ([Agent log shipping](agent-log-shipping.md)), spool data dropped for the size bound, the free-space reserve or the
file limit or lost to damage (`spool_dropped`), and the records the spool refused while its writes failed (`unknown`;
[Agent spool](agent-spool.md)). The backend records the gaps it detects as rows of its own, not as records: a sequence gap for numbers it
has not stored (`sequence_missing`) and a time gap for more than 2 minutes without a `metric` record (`no_data`), each open, closed or
final ([Detection, alerts and reports](detection-and-reports.md), *Live data and backfill*; [Storage writing](storage-writing.md)). A
`gap` record that names `first_seq` and `last_seq` makes the numbers below its own final at once, with its cause. Analysis, reports and
the web UI treat a recorded gap as *unknown*, never as *normal*. Spool eviction and collector timeouts each emit a gap record, and each
path has a test.

## Agent state

The agent keeps one small state file, `agent-state.json` in `spool.directory` (security area 9): the last boot it ran in with that boot's
uptime and boot time, the `kernel` collector's journal position, the open backup run and the positions of the `logs` collector. Besides
the spool's files it is the agent's only file write. The agent never creates the directory; the installation does.

**Format.** One JSON object `{"version":1,"boot":{"id","uptime_ns","booted_at"},"journal":{"seqnum_id","seqnum","realtime"},"backup":{"boot_id","started_at","last_seen","max_processes"},"logs":{"journal":[{"seqnum_id","seqnum","realtime"}],"files":[{"path_sha256","device","inode","offset","head_sha256","head_len","last_time","renamed":{"device","inode","offset","size"}}]}}`, the four sections,
`logs.journal`, `logs.files` and `renamed` optional, IDs as lower-case UUIDs, times in RFC 3339 UTC. A file position names its configured
file by the SHA-256 of the path (64 lower-case hex digits), never by the path; `offset` is the start of the first line not yet shipped
(or, while the source holds an open group, of the oldest line the group holds), `head_sha256` the SHA-256 of the file's first `head_len`
bytes (that of no bytes when `head_len` is 0) and `renamed` the rotated file still being read ([Agent log shipping](agent-log-shipping.md)).
At most 256 journal and 16 file positions are written, which fit the 64 KiB bound with room.

**Reading (at start-up).** The file is opened without following a link and without blocking, and is trusted only when `fstat` of that
descriptor shows a regular file of the agent's effective user without any group or other permission bit and of at most 64 KiB. Its content
is decoded strictly: one object and nothing after it, `version` 1, no unknown key, no wrong type, lower-case UUIDs, a non-negative uptime,
every time before 1 January 10000 UTC, an open run with a start and a last sighting, the last sighting not before the start, and at least
one process, and log positions with at most 256 journal and 16 file positions, both digests of 64 lower-case hex digits, `head_len` 0 to
256 and offsets and sizes that are not negative. A file without `logs` stays usable: the `logs` collector starts at the current ends as on
a first start. An absent file in an existing, empty directory is a first start, without a warning. Anything else that is not usable — a
missing directory, an absent file beside other entries, a link, a file of another user or with group or other permission, an oversized or
corrupt file — is treated as no state and logged once as warning `agent state not available`; the first save replaces the file. The state
file is read before the spool is opened, so on a first start the directory is still empty and neither warns; a missing directory logs this
warning and then ends the start, because the spool refuses it, so the agent never runs without its directory. Residual: a start that ends
after the spool created its files and before the first state save (a `kill -9` in the first seconds, a later start error) leaves spool files without a state file, and the next start logs `agent state not available` once.

**Saving.** The `state` job (interval 10 seconds, timeout 5 seconds, last in the job list; it records nothing) saves when a change waits:

- when an urgent change waits — a kill, a boot or a backup change (a new run, its end, a higher process count, a position moved by a run
  that read kills) or a log position the `logs` collector committed — and at least 5 seconds have passed since the last save, so an urgent
  change is saved within one state interval however the start times jitter;
- else once 50 seconds have passed (the uptime heartbeat; a kernel position that moved over other kernel lines waits for it).

Every save first refreshes the stored uptime from `/proc/uptime` when the stored boot is the current boot (its `id` equals the boot ID of
`/proc`; another boot keeps its saved uptime, so a start-up save never overwrites the previous boot's uptime), so the current boot's saved
uptime is never more than a minute old. A save removes a left-over `agent-state.json.tmp`, creates it anew (exclusively, without following
a link, mode 0600), writes and syncs it, renames it over `agent-state.json` and syncs the directory. One write lock covers the encoding,
the write and the bookkeeping of a save, so the job's save and the stop's never run at once. **Every save first makes the spool durable**
(within the save's deadline: the job's timeout, or 5 seconds at the stop), so no saved position or state covers a record a crash can still
lose; when that sync fails or does not finish in time, the save is not written. A failure, also a missing directory, is logged once as
warning `agent state not saved` with `err` `agentstate: save: <system error text>` (never a path), or `agentstate: save: spool not durable` when the spool's sync failed, until a save succeeds, and the agent goes on; a save that hangs past the job's timeout is a gap of
collector `state`.

**Saving at the stop.** After the scheduler has stopped, the agent saves the state once more when anything changed since the last save, so
the positions of the last kept runs survive a restart (a stop with SIGTERM neither repeats nor loses a log line). It waits at most 5
seconds for a `state` job save that is still running; when that save does not end, it saves nothing and logs `agent state not saved` with
`err` `agentstate: save still running` (every time it gives up), and the agent exits as usual. The order at the stop is: the scheduler
stops, the collectors' flush (at most 5 seconds), this last save (which makes the spool durable first), then the spool is closed.

**The spool leaves it alone.** The spool never reads, counts, overwrites or deletes the state file ([Agent spool](agent-spool.md)).

## Related decisions

- [0006](../decisions/0006-agent-connects-outbound-only.md) — why outbound only.
- [0019](../decisions/0019-agent-reads-proc-instead-of-top-lsof.md) — why `/proc` is read directly.
- [0028](../decisions/0028-data-gaps-are-always-recorded.md) — why gaps are always recorded.
- [0029](../decisions/0029-hanging-collector-never-blocks-the-agent.md) — why collectors are isolated and abandoned at their deadline.
- [0110](../decisions/0110-agent-runtime-frame-runner-per-collector-gaps-for-every-missed-sample.md) — one runner per collector at a fixed
  rate, a gap for every missed sample.
- [0136](../decisions/0136-systemd-notification-by-hand-and-a-watchdog-gated-on-the-collector-runners.md) — the systemd notification without
  a library, the watchdog gated on the runners.
- [0137](../decisions/0137-agent-log-as-text-lines-on-standard-error-with-the-journals-priority-prefix.md) — the log format: text lines on
  standard error with the journal's priority prefix.
- [0141](../decisions/0141-memory-pressure-mode-switched-by-the-system-collector-with-fixed-hysteretic-thresholds.md) — the memory pressure
  mode switched by the `system` collector with fixed hysteretic thresholds.
- [0156](../decisions/0156-one-agent-state-file-in-the-spool-directory-written-by-a-state-job.md) — one state file in `spool.directory`,
  written by a `state` job.
- [0159](../decisions/0159-boot-and-backup-run-changes-reach-the-state-only-once-their-run-is-kept.md) — boots and backup runs reaching the
  state only once their run is kept.
- [0163](../decisions/0163-the-state-file-keys-a-configured-log-file-by-the-sha-256-of-its-path.md) — log file positions in the state file
  keyed by the hash of the path.
- [0161](../decisions/0161-log-shipping-commits-its-positions-when-the-scheduler-keeps-a-run.md) — log positions committed when the
  scheduler keeps a run.
- [0168](../decisions/0168-open-log-groups-are-sent-at-a-clean-stop-and-a-holding-source-is-saved-at-its-oldest-held-line.md) — log
  positions saved once more at the stop after the flush.
- [0175](../decisions/0175-the-state-file-syncs-the-spool-before-every-save.md) — the state file waits for the spool's sync.
- [0184](../decisions/0184-the-agent-unit-sets-a-hard-memory-limit-with-gomemlimit-below-it.md) — the unit's restart policy, resource limits
  and `GOMEMLIMIT=`.

## Not here

- The collectors: [Agent system metrics](agent-system-metrics.md) (`system`, `filesystem`, `clock`, `processes`),
  [Agent connections](agent-connections.md), [Agent services and backup runs](agent-services.md) (`systemd`, `backups`),
  [Agent MariaDB collector](agent-mariadb.md), [Agent kernel events and boots](agent-kernel-events.md) (`kernel`) and
  [Agent log shipping](agent-log-shipping.md) (`logs`).
- The on-disk spool: [Agent spool](agent-spool.md); sending batches to the backend: [Agent sending](agent-sending.md).
- The user, capabilities and confinement the agent runs with: [Agent rights](agent-rights.md); the unit and the install and uninstall
  scripts: [Agent installation](agent-installation.md).
- The format of batches and records: [Wire format](wire-format.md) and [Wire format records](wire-format-records.md).
- Which options the agent reads and how secrets are supplied: [Configuration and secrets](configuration-and-secrets.md) and
  [Configuration options](configuration-options.md).
- What the backend does with the data, including gap detection: [Ingest endpoint](ingest-endpoint.md),
  [Storage writing](storage-writing.md) and [Detection, alerts and reports](detection-and-reports.md).

## Implementation

`cmd/vandox-agent` and `internal/cli` (command line), `internal/agent` (start-up, logging, watchdog), `internal/scheduler`,
`internal/selfstats`, `internal/sysstats` (the memory pressure decision), `internal/sdnotify` (notifications), `internal/agentstate` (the
state file, the `state` job and the save at the stop), `internal/config`, `internal/model`, `internal/wire`; each collector's document
names its package.
