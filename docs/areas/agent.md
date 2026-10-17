# Agent

## Scope

What `vandox-agent`, the program on the monitored server, must do and what it must never do: how it connects, what it reads and how,
how it survives a stalled data source and a missing backend, how gaps are recorded and which rights it runs with. The format of what it
sends is in the [wire format](wire-format.md); its configuration file is in [Configuration and secrets](configuration-and-secrets.md).

**Status.** The command line, the start-up with the configuration file, logging, the scheduler with its gaps and the memory pressure
switch, the self-metrics and the notification of systemd exist, as do the shared data model and the wire encoder. The collectors
other than the self-metrics, the spool, the sender and the systemd unit are **not implemented yet**: until the spool exists, the agent
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
  boundary — below the journal's default line limit of 48 KiB (`LineMax=`), so the journal never splits an entry into a second one.
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
- As soon as the unit grants any capability, the unit is confined: `NoNewPrivileges=yes`; a system-call filter that denies `ptrace`,
  `process_vm_readv`, `process_vm_writev`, `pidfd_getfd` and `open_by_handle_at` with `EPERM` (and not `pidfd_open` or
  `pidfd_send_signal`) under `SystemCallArchitectures=native`; no write-side capability; `ProtectHome=yes` and `InaccessiblePaths=` for at
  least `/etc/shadow` and `/etc/gshadow`. The last two protect only against accidental reads; they are not a limit on a compromised
  agent, which can reach the same files through another process's root in `/proc`.
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

## Not here

- The format of batches and records: [wire format](wire-format.md).
- Which options the agent reads and how secrets are supplied: [Configuration and secrets](configuration-and-secrets.md).
- What the backend does with the data, including gap detection: the storage and detection areas.

## Implementation

`cmd/vandox-agent` and `internal/cli` (Go), `internal/agent` (start-up, logging, watchdog), `internal/scheduler`, `internal/selfstats`,
`internal/sdnotify`, `internal/config`, `internal/model`, `internal/wire`; `deploy/agent/` (example configuration; the unit and install
scripts follow with the collectors).
