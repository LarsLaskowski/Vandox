# Agent

## Scope

What `vandox-agent`, the program on the monitored server, must do and what it must never do: how it connects, what it reads and how,
how it survives a stalled data source and a missing backend, how gaps are recorded and which rights it runs with. The format of what it
sends is in the [wire format](../WIRE_FORMAT.md); its configuration file is in [Configuration and secrets](configuration-and-secrets.md).

**Status.** The command line (`-version`), the configuration loader, the shared data model and the wire encoder exist. The collectors,
the spool, the sender, gap recording and the systemd unit are **not implemented yet**; the rules below are the target the
implementation is held to and the tests must check.

## Connection

- The agent only opens **outbound** connections to the backend's ingest port and **never listens on a port**. The monitored server
  needs no inbound firewall rule for Vandox.
- Commands for the server (a later feature) are fetched by the agent from the backend, never pushed to it, so they reach the server
  only at the agent's next poll.
- The agent never contacts Telegram or any other service; all notification goes through the backend.

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
  recorded).

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

## Not here

- The format of batches and records: [wire format](../WIRE_FORMAT.md).
- Which options the agent reads and how secrets are supplied: [Configuration and secrets](configuration-and-secrets.md).
- What the backend does with the data, including gap detection: the storage and detection areas.

## Implementation

`cmd/vandox-agent` and `internal/cli` (Go), `internal/config`, `internal/model`, `internal/wire`; `deploy/agent/` (example
configuration; the unit and install scripts follow with the collectors).
