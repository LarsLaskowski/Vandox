# Spec: Network connection collector (replaces `lsof -ni`)

Source: Issue #33
Status: Draft

## Problem

The operator of the monitored server (Ubuntu 22.04, Plesk 18, about 2 GB RAM) reconstructs outages from what the server did before
them. Until now the connection side of that picture came from the legacy shell script, which ran `lsof -ni` every few minutes and
appended its output to a log that `vandoxd import` turns into connection snapshots (Log import, *Legacy monitoring log*). Running
`lsof` forks a program that walks every open file of every process: slow, and liable to fail exactly when memory runs out, which is
when the data matters (record 0019). `vandox-agent` (#30, closed) already replaces the `top` half of the script with the process
collector (#32); the `lsof` half has no replacement, so v0.2.0 cannot retire the script (#45) and the operator has no record of how
many connections were open per state, which process held them, which remote addresses dominated, and which ports the server listened
on. A port that starts listening without the operator's doing (a forgotten service, a compromised account) goes unnoticed today, and
when memory runs short the operator has no full list of who was connected at that moment.

## Scenarios

Terms used below: a **socket** is a line of the kernel's tables `/proc/net/tcp`, `tcp6`, `udp` and `udp6` of the agent's network
namespace. A **listener** is a TCP socket in state `LISTEN` or a UDP socket without a remote endpoint (remote address all zeros and
port 0); a **connection** is a socket with a remote endpoint. The **owner** of a socket is a process that holds it among its file
descriptors (`/proc/<pid>/fd`). A **regular** snapshot carries the aggregates and the listener inventory; a **full** snapshot carries,
in addition, every connection and every remote address. Records are those of the [wire format](../../docs/areas/wire-format.md)
(`connection_snapshot`); the collector is named `connections` and its records carry the source `connections`.

- S1: (regular snapshot) Given the agent runs and the memory pressure mode is off, when the collector `connections` runs (every 60
  seconds), then it records one `connection_snapshot` with the capture time of the run's start that holds:
  `states` — the number of sockets per protocol and TCP state over every socket of the four tables (UDP sockets counted per protocol
  without a state, as the legacy parser counts them), by protocol then state;
  `processes` — one entry per process that owns at least one socket: its PID, its `command` (the same name the process snapshot
  gives that PID, Agent area *Names*) and the number of distinct sockets it owns, by count descending then PID;
  `remotes` — the 25 remote addresses with the most connections (address only, as the model counts them), by count descending then
  address;
  `listeners` — the listener inventory: one entry per protocol, local address and port, with the PID and `command` of its owner when
  one is known, by protocol, address, port;
  no `connections` list. A reader of the record can tell that it is a regular snapshot and whether its aggregates (`states`,
  `processes`, `listeners`) cover every socket the run saw.
- S2: (baseline at start) Given the agent has just started, when the collector runs for the first time, then it records a full
  snapshot (S4) and reports no listener as new: the inventory of that run is the baseline later runs are compared with.
- S3: (new listener) Given the last successful run's inventory had no `tcp` listener on `0.0.0.0:4444`, when a run finds one, then
  that run reports it as a **new listener** — protocol `tcp`, local address `0.0.0.0`, port 4444, the PID and `command` of its owner
  when known, and the capture time of the run's start — in a record the backend stores, and the run records a full snapshot (S4)
  instead of a regular one, so the connections at the moment the port opened are kept.
- S4: (full snapshot under memory pressure) Given the `system` collector has switched the memory pressure mode on (Agent area,
  *Memory pressure mode*), when the collector runs (every 15 seconds while the mode is on), then every run records a full snapshot:
  everything of S1, `remotes` with every remote address, and `connections` with every connection (protocol, local endpoint, remote
  endpoint, state for TCP, PID and `command` of its owner when known), by protocol, local endpoint, remote endpoint. A reader can tell
  that it is a full snapshot and whether it holds every socket the run saw (`complete`, wire format).
- S5: (owners of other users' sockets) Given the agent runs as the user `vandox-agent` with exactly the two capabilities the Agent
  area names (`CAP_SYS_PTRACE`, `CAP_DAC_READ_SEARCH`) and the unit's confinement, when the collector runs, then the sockets of
  processes of other users (MariaDB as `mysql`, nginx, `sshd` as root) carry their owner's PID and `command`, and
  `deploy/agent/README.md` lists, for the collector `connections`, each capability with the read it is needed for.
- S6: (sockets without an owner) Given sockets that no process holds (TCP `TIME_WAIT`, a socket whose process ended during the run),
  when the collector runs, then they count in `states` and, as connections, in `remotes` and `connections`; their entries carry no
  PID and no `command`; they are not counted in `processes`.

## Errors and edge cases

- E1: (no capability, `hidepid`) Given the agent lacks the capabilities, or `/proc` is mounted with `hidepid`, when the collector runs,
  then the sockets of processes whose descriptors it cannot read carry no owner, the sample still succeeds, and the agent logs one
  warning `socket owners partly unknown` with `collector=connections` and `processes` (the number of processes whose descriptors could
  not be read) when that number becomes positive, and one info line `socket owners known again` when it returns to 0.
- E2: (IPv6 tables missing) Given a kernel without IPv6 or booted with `ipv6.disable=1`, so `/proc/net/tcp6` and `/proc/net/udp6` do
  not exist (observed on the development host), when the collector runs, then the snapshot holds the IPv4 sockets only, nothing of
  `tcp6`/`udp6` is written as 0, and the sample succeeds; the agent logs one info line `socket tables unavailable` with `tables` (the
  missing ones, comma-separated, in the order `tcp`, `tcp6`, `udp`, `udp6`) when that set changes and is not empty, and `socket tables
  available` when it becomes empty. When all four tables are missing, the sample fails and becomes a gap.
- E3: (unreadable table line) Given a line of a table that does not have the expected shape, or a TCP state code outside the model's
  states, when the collector runs, then that socket is left out of every list and count, the snapshot is not complete, the sample goes
  on, and the agent logs one warning `socket lines not read` (`lines`: the number, `err`: the first error, naming the table and the line
  number and a fixed reason, never the line's content) when that number becomes positive, and `socket lines read again` when it
  returns to 0.
- E4: (table read error) Given a table that exists but cannot be read (an I/O or permission error), when the collector runs, then the
  sample fails and becomes a gap with cause `unknown`, logged by the scheduler (Agent area, *Scheduling*).
- E5: (very many sockets) Given tens of thousands of sockets (a SYN flood, a local user who opens 100,000 sockets), when the collector
  runs, then every socket of the tables is counted in `states`; owners are looked up and entries listed only up to fixed caps (each
  list at most 4,096 entries, the wire format's limit), and a snapshot that left anything out is not complete; the memory a run uses
  does not grow with the number of sockets beyond those caps. A run that does not finish within its timeout is abandoned and becomes a
  gap with cause `collector_timeout` (Agent area, *Scheduling*).
- E6: (very many descriptors) Given a process that holds hundreds of thousands of file descriptors (a hostile local user can open as
  many as the hard limit allows), when the collector runs, then the run takes longer in proportion; a run past its timeout becomes a
  gap, as in E5, and the collector is not started again while the abandoned run lasts (accepted residual, as for the process
  collector's hanging reads).
- E7: (hostile process names) Given a process whose command line or name holds control characters, invalid UTF-8 or more than 1,024
  bytes, when it owns a socket, then its `command` follows the process collector's rules (valid UTF-8, at most 1,024 bytes, never empty,
  `(unnamed)` as the last resort) and the record is valid; no log line of the collector holds a process name, a command line or an
  address.
- E8: (changes during the run) Given a process that ends, or a socket that closes or opens, between the read of the tables and the
  read of the descriptors, when the collector runs, then the socket is reported without an owner or not at all, the vanished process is
  left out, and nothing of this is an error; a snapshot is a sequence of reads, not an atomic picture.
- E9: (shared socket) Given one socket held by several processes (a listening socket inherited by Apache or PHP-FPM workers), when the
  collector runs, then the socket counts once in `states`, `remotes`, `listeners` and `connections`, with the lowest PID among its
  holders as its owner, and once in the `processes` count of every holder.
- E10: (several sockets on one endpoint) Given several listening sockets on the same protocol, address and port (`SO_REUSEPORT`, as
  nginx workers use it), when the collector runs, then each counts in `states`, and the inventory holds one listener for that protocol,
  address and port, owned by the lowest owning PID.
- E11: (listener that disappears and returns) Given a listener that is missing in one successful run and present in the next, when the
  next run finds it, then it is reported as new again. A failed run (a gap) is not compared: the next successful run is compared with
  the last successful one. A change of owner on the same protocol, address and port is not a new listener.
- E12: (flood of new listeners) Given a local user who opens hundreds of new listeners between two runs, when the collector runs, then
  at most 64 of them are reported individually (in inventory order), and the run's records state how many more were new; the
  inventory holds them up to its cap (E5).
- E13: (ephemeral UDP ports) Given an unconnected UDP socket on a port in the kernel's local port range (`net.ipv4.ip_local_port_range`,
  32768–60999 by default; a resolver's or client's temporary socket), when it first appears, then it is in the inventory but is not
  reported as a new listener. A TCP listener is reported whatever its port.
- E14: (agent restart) Given a listener that opened while the agent was stopped, when the agent starts again, then its first run is the
  baseline (S2) and does not report that listener as new; the inventories before and after the restart are both stored.
- E15: (mode switch during a run) Given the memory pressure mode switches while a run is in progress, when the run records its
  snapshot, then the run is regular or full according to the mode at its start (Agent area, *Scheduling*).
- E16: (record too large) Given a snapshot whose encoded record would exceed the wire format's line limit (1 MiB), when the collector
  records it, then the record is reduced (the connections first) until it fits, is marked not complete and is recorded; size never
  fails the sample.
- E17: (IPv6 forms) Given a dual-stack listener on `::` and IPv4 peers connected to it, when the collector runs, then the listener is one
  `tcp6` listener on `::` and those peers appear as IPv4-mapped IPv6 addresses (`::ffff:a.b.c.d`) in `tcp6` connections and in
  `remotes`, as the kernel reports them and as `lsof` printed them; link-local addresses are kept without a zone (wire format,
  *Producer mapping for connection endpoints*).
- E18: (other network namespaces) Given containers with network namespaces of their own on the server, when the collector runs, then
  their sockets are not in the tables the agent reads and are not reported; descriptors of host processes that point to such sockets
  are not counted.

## Non-functional requirements

- **Schedule:** collector `connections`, interval 60 seconds, pressure interval 15 seconds, timeout 10 seconds, placed after
  `processes` in the job list.
- **What it reads:** `/proc/net/tcp`, `tcp6`, `udp`, `udp6`, the `/proc` directory, `/proc/<pid>/fd` and what the process collector
  reads to derive a `command` (`stat`, `cmdline`), through the replaceable file-system interface (Agent area, *Collecting*). It starts no
  program, opens no socket (no netlink socket diagnostics) and makes no raw system call.
- **Rights:** no right beyond the two capabilities the Agent area already names; the collector works without them as E1 describes. The
  rights the collector needs are listed and justified in `deploy/agent/README.md`; the unit must not give the agent a network namespace
  of its own (`PrivateNetwork=` stays off), which the Agent area states.
- **Memory and time:** "never make the memory situation worse" (issue): the memory one run allocates has a fixed upper bound that does
  not depend on the number of sockets or descriptors beyond the caps of E5, stated in the Agent area and asserted by a test; tables and
  descriptor directories are read in bounded pieces, never whole. The time of a run over a stated number of sockets and processes is
  measured and stated in `docs/BENCHMARKS.md`; the cost on the server itself is measured by #44.
- **Size:** every record passes the wire format's producer check (*Producer size contract*), size included, before it leaves the
  collector.
- **Compatibility:** the records are wire format 1.x, decoded by both decoders; a field or kind this feature adds is additive (a minor
  version, wire format *Versioning*), and the backend reads the new minor before agents send it. The backend's existing readers of
  connection snapshots — the incident's connection peak and the *Connections* chart of the incident page — read the agent's snapshots as
  they read imported ones. The agent counts sockets without an owner (`TIME_WAIT`, S6) that `lsof -ni` never listed, so its
  totals are higher than the legacy log's at the same moment; the Agent area states this for the comparison of #45.
- **Configuration:** none new.
- **Logs:** only the lines named in E1, E2 and E3, each once per change, with `collector=connections`; no per-run line, no line per new
  listener, no process text and no address in any line.
- **Security:** the command texts are text a local user chooses (security area 12); the tables are kernel output read with bounds
  (security area 10); the capabilities are the agent's existing rights (security area 6), now granted for a second collector.

## Acceptance criteria

- [ ] AC1: a run with the memory pressure mode off, after the first run and without a new listener, records exactly one
  `connection_snapshot` with source `connections` and the run's start as capture time, holding `states`, `processes`, the top 25
  `remotes` and `listeners` as S1 defines them, in the stated orders, and no `connections`; a reader can tell from the record that it
  is regular and whether its aggregates cover every socket the run saw (S1, E9, E10, E17).
- [ ] AC2: every run with the memory pressure mode on, the first run after the agent starts and every run that reports a new listener
  records a full snapshot with every remote address and every connection; a reader can tell from the record that it is full; the mode
  at the run's start decides (S2, S3, S4, E15).
- [ ] AC3: a listener absent from the last successful run's inventory and present in the current one is reported as new with protocol,
  local address, port, owner PID and `command` when known and the run's start time, in a record the backend's decoder accepts; the
  first run after the start reports none; a failed run is not compared; an unconnected UDP socket on a port of the local port range is
  not reported; at most 64 are reported per run, with the number of the rest (S2, S3, E11, E12, E13, E14).
- [ ] AC4: sockets without an owner count in `states`, `remotes` and `connections` without PID and `command` and are not in
  `processes`; a socket held by several processes counts once, owned by the lowest PID, and once per holder in `processes` (S6, E8,
  E9).
- [ ] AC5: running as a user other than the sockets' owners with exactly `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH`, the collector
  finds the owner of another user's socket, measured on a host, and the measurement with what each capability is needed for is
  recorded in the Agent area and `deploy/agent/README.md`; without them the sockets of unreadable processes carry no owner, the sample
  succeeds and the warning and info lines of E1 are logged once per change (S5, E1).
- [ ] AC6: a missing table leaves its protocol out and logs E2's lines once per change, all four missing fail the sample; an unreadable
  line is left out with the snapshot not complete and E3's lines; a table that exists but cannot be read fails the sample as a gap
  (E2, E3, E4).
- [ ] AC7: with more sockets or listed entries than the caps, every socket is still counted in `states`, no list holds more than 4,096
  entries, the snapshot is not complete, and one run's allocations stay within the bound the Agent area states, asserted by a test; a
  record that would exceed 1 MiB is reduced, connections first, recorded and marked not complete (E5, E16).
- [ ] AC8: a run whose read blocks past the timeout becomes a gap with cause `collector_timeout` and the collector is not started again
  while it is stuck, tested with a double of the file system that hangs (E5, E6).
- [ ] AC9: a process with a hostile name yields a valid record with the process collector's `command`; no log line of the collector
  contains process text or an address (E7).
- [ ] AC10: the collector's records are accepted by the Go wire check and, through a golden fixture produced by the Go encoder, by the
  backend's decoder and validation, and the backend's record writer stores them, so the incident's connection peak reads an agent
  snapshot as it reads an imported one (S1, S3, S4).
- [ ] AC11: the collector reads only the files named in *Non-functional requirements* through the replaceable file-system interface,
  starts no program and opens no socket; a test pins the packages it imports (S1).

## Out of scope

- **On-demand snapshots by the user.** Commands reach the agent only through the command channel of the remote actions (#77, #74;
  `docs/ARCHITECTURE.md`, *Remote actions (later)*), which does not exist in v0.2.0.
- **The full snapshot on an OOM kill and its link to the event:** issue #36 requires it for the process and the connection snapshot
  together and builds the trigger for both collectors.
- **Events for listeners that disappear or change owner.** Both are visible by comparing the stored inventories; a rule for them belongs
  to the alerting milestones (#56, #57).
- **Using the new data in the backend** (a view of connections or listeners, the new listeners in an incident's timeline, alerts on
  them): the backend reads agent events from #53 on and has no ingest before #40; this feature only makes the records storable.
- **Unix domain sockets, raw sockets and sockets of other network namespaces** (E18): `lsof -ni` listed none of them either.
- **Other connection anomalies as triggers** (a sudden rise in connections, a state count above a threshold): no threshold is known
  before the parallel run of #45; the regular snapshot's `states` already show such a rise.
- **Per-port remote statistics:** `remotes` counts per address, as the model and the legacy parser do.

## Open questions

- Q1: Which anomalies trigger a full snapshot? Answered: the agent's documented anomaly signal is the memory pressure mode, under which
  a collector takes a fuller sample (Agent area, *Scheduling* and *Memory pressure mode*; record 0113 does the same for the process
  collector); the issue itself names new listeners; the first run gives the baseline. The OOM kill is #36's trigger (Out of scope).
- Q2: What is "on demand"? Answered: user commands exist only as the pulled, signed commands of the remote actions milestone (#76,
  #77) and the read commands of #74 (`docs/ARCHITECTURE.md`, *Remote actions (later)*), and the agent-internal demand is #36's OOM
  trigger; both are out of scope here.
- Q3: What are "the minimal documented capabilities"? Answered: `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH`, the only ones the Agent
  area (*Rights*) and record 0030 allow; record 0030 already names both for this purpose (listing `/proc/<pid>/fd` needs
  `CAP_DAC_READ_SEARCH`, reading its links passes the ptrace check that `CAP_SYS_PTRACE` satisfies). That claim has not been measured
  yet; AC5 requires the measurement. `deploy/agent/README.md` lists only `CAP_SYS_PTRACE` today, so this feature is the first to need
  `CAP_DAC_READ_SEARCH`.
- Q4: What does "stored" mean while the agent has no spool and the backend no ingest? Answered: until the spool (#38) exists the agent
  drops the records after checking them (Agent area, *Scheduling*), and the ingest API is #40. Here "stored" means the records pass the
  agent's wire check and the backend's decoder, and the backend's record writer stores them as it stores imported connection snapshots
  (`records.data`, Storage); the path from agent to database is completed by #38 to #40 (AC10).
- Q5: In which record is a new listener reported? Answered: the wire format has no record for it today (`connection_snapshot` has no
  marker, `kernel_event` only `oom_kill` and `boot`). Whether it becomes a new kind or an additive field is a design choice of the plan,
  within the wire format's rule that both are minor, additive changes (*Versioning*, record 0043); the spec fixes what a reader finds
  (AC3).
- Q6: Is a "remote endpoint" an address or an address and port? Answered: the model's `remotes` counts per address
  (`internal/model/connection.go`, `RemoteCount`; wire format *`connection_snapshot`*), and the legacy parser does the same; the full
  snapshot's `connections` keep the port.
- Q7: Is the dependency in place? Answered: #30 is closed; the scheduler with its per-collector runners, gaps, pressure intervals and the
  run's knowledge of the mode exists (`internal/scheduler`, Agent area *Scheduling*), as do the process collector's name derivation and
  the model `ConnectionSnapshot` with its validation in Go and C#.
- Q8: Do the issue's four tables always exist? Answered: no — on the development host (kernel 6.18) `/proc/net/tcp6` and `udp6` are
  missing, so a missing table is a case of its own (E2).

## Area documents

- [Agent](../../docs/areas/agent.md): a section *Connections* — the collector, its schedule, what it reads, the regular and the full
  snapshot and when each is taken, the listener inventory and the new-listener rule (identity, baseline, ephemeral UDP ports, the cap of
  64), owners and shared sockets, the bounds and the memory figure, the log lines, the difference from `lsof -ni` for #45; *Rights*:
  what each capability is for in this collector and the measurement; *Status* and *Implementation* updated.
- [Wire format](../../docs/areas/wire-format.md): *`connection_snapshot`* — how a reader tells a regular from a full snapshot and
  whether the aggregates are complete, and how a new listener is reported (a new kind or field, with the minor version);
  *Producer mapping for connection endpoints* — the mapping from the kernel's tables.
- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md): only if the plan finds that the connection peak or the
  chart must treat regular agent snapshots differently from imported ones.
- `deploy/agent/README.md` (not an area document): the capabilities of the collector `connections`, each with its read.
