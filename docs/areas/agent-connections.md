# Agent connections

## Scope

The agent's `connections` collector, which replaces the `lsof -ni` half of the legacy script: the socket tables it reads, how it finds
the processes that hold a socket, the regular and full `connection_snapshot`, the socket's user, and the new- and changed-listener check
the backend's listener findings build on. Scheduling and gaps are in [Agent](agent.md).

## Collector

The collector `connections` (interval 60 seconds, pressure interval 15 seconds, timeout 10 seconds, after `processes` in the job list)
records one `connection_snapshot` record per run with source `connections` and the capture time of the run's start
([wire format records](wire-format-records.md)). It reads `/proc/net/tcp`, `tcp6`, `udp` and `udp6`, lists `/proc`, reads the first lines
of `/proc/<pid>/fdinfo/<n>`, what the process collector reads to derive a `command` (`stat`, `cmdline`) and, once per run, `/etc/passwd`
(both with the process collector's bounds, [Agent system metrics](agent-system-metrics.md), *Processes*). It starts no program, opens no
socket and makes no raw system call; the one socket the agent opens for it is the start-up probe (*The socket mount*).

## Terms

- A **socket** is one line of the four tables of the agent's network namespace; two lines are two sockets, also when both have inode 0
  (`TIME_WAIT`, `SYN_RECV`), and a line with inode 0 never matches a descriptor.
- A **listener** is a TCP socket in `LISTEN` or a UDP socket whose remote address is all zeros with port 0; a **connection** is a socket
  with a non-zero remote address or port; any other TCP socket counts in `states` only.
- The **owner** of a socket is the lowest PID among the processes that hold it.
- The **socket's UID** is the `uid` field of its table line — the UID of the socket, not of a process that holds it. The kernel gives a
  socket the file-system UID of its creator, and only an `fchown` (which needs `CAP_CHOWN` to give it to another user) changes it, so a
  listener created as root by a daemon that then switches to `www-data`, a socket systemd creates for socket activation and any socket a
  root process hands on read UID 0.

## Tables

`tcp`, `tcp6`, `udp`, `udp6` are read in that order, line by line; the first line is the header. A line longer than 512 bytes is skipped
to its end and counted unreadable. A line needs at least 10 fields: `sl` (digits and `:`), the local and remote endpoint (8 upper-case hex
digits for IPv4 or 32 for IPv6, `:`, 4 upper-case hex digits), the state (2 upper-case hex digits), the `uid` (field 8, 1 to 10 digits
within 32 bits, read as the kernel prints it unsigned, so `65534` and `4294967295` are read as printed) and the inode (field 10, 1 to 20
digits within 64 bits); the other fields are not interpreted, and neither a repeated `sl`, a trailing `-1` nor trailing spaces matter. A
`uid` or inode with a sign, a letter or too many bits makes the line unreadable (`not in the expected format`, never a guess); the same
rules apply where the MariaDB collector reads the TCP tables ([Agent MariaDB collector](agent-mariadb.md), *Metrics*). Each address word
is a 32-bit number in the kernel's host byte order (`0100007F` is `127.0.0.1`). IPv4-mapped and link-local addresses in `tcp6` are kept as
reported (`::ffff:a.b.c.d`, no unmapping, no zone; [wire format records](wire-format-records.md), *Producer mapping for connection
endpoints*). TCP states `01` to `0C` map to the model's twelve names; any other code makes the line unreadable; for UDP the state is
checked and dropped. A table that does not exist (`tcp6` and `udp6` without IPv6) leaves its protocol out; all four missing fail the sample
(`connstats: no socket table found`), and a table that exists but cannot be read fails it too.

## Caps

Every socket of the tables is counted in `states`; the rest is bounded per run: the inode index holds the first 65,536 sockets with an
inode other than 0 (later ones get no owner and make the aggregates incomplete); the listener inventory holds the 4,096 lowest
**distinct** endpoints, so `SO_REUSEPORT` sockets share one entry; the connections list holds the 4,096 lowest (protocol, local, remote);
`remotes` counts at most 4,096 remote addresses; at most 4,096 processes are scanned for owners (the lowest PIDs, as the process collector
keeps them); at most 64 new and, separately, 64 changed listeners are reported.

## Regular and full snapshots

- A **regular** snapshot (`aggregates_only` true, no `connections`) holds `states` (per protocol and TCP state, by protocol then state,
  UDP without state), `processes` (one entry per process that owns at least one socket: PID, `command`, the distinct sockets it holds, by
  count descending then PID), `remotes` (the top 25 by count descending then address, address only) and `listeners` (the inventory, ports
  below 1024 first, then protocol, address, port).
- A **full** snapshot adds every connection (by protocol, local, remote) and every counted remote address. Full are: the first run after
  the agent starts, every run under memory pressure (the mode at the run's start), every extra run for an OOM kill (its record names the
  kills in `trigger`; [Agent kernel events and boots](agent-kernel-events.md), *Extra snapshots*) and every run that finds at least one
  new or changed listener, reported or omitted. Every other run is regular.
- `complete` is true only for a full snapshot in which nothing was cut, skipped, left without an owner beyond a cap, unread or uncounted;
  a regular snapshot is never complete. `aggregates_incomplete` is set when `states`, `listeners` or `processes` may not cover every socket
  the run saw: a list cut for size or at its cap, a skipped table line, a socket beyond the inode index, more processes than the PID cap,
  a process whose descriptors could not be read, or an owner scan that did not run.
- Imported legacy snapshots and an older agent's carry none of these fields and read as full with complete aggregates.
- A regular snapshot's top 25 `remotes` are over the counted addresses only. `remotes` does not unmap IPv4-mapped addresses, so one IPv4
  peer connected to a `0.0.0.0` and a `::` listener is two entries.

## Owners

The agent does not read the `/proc/<pid>/fd` links (listing them needs `CAP_DAC_READ_SEARCH`, which it does not hold). For every scanned
process it lists `<pid>/fdinfo` in chunks of 256 and per descriptor opens the file, reads at most 256 bytes and closes it. The first four
lines must be `pos:`, `flags:`, `mnt_id:` (1 to 10 digits, 0 to 2147483647) and `ino:` (1 to 20 digits), each a tab and digits (`pos:` may
carry a leading `-`); later lines are not read. A descriptor counts only when its `ino:` is the inode of a tracked socket **and** its
`mnt_id:` is the socket mount.

- Several descriptors of one socket in one process count once for that process; per PID the distinct sockets are counted, and no
  process's descriptors can crowd out another's owners.
- A socket held by several processes counts once in `states`, `remotes`, `listeners` and `connections`, is owned by the lowest PID and
  counts in the `processes` entry of every holder; several listening sockets on one endpoint are one listener owned by the lowest owning
  PID.
- A process that ends during the run (`ENOENT`/`ESRCH` on its directory) and a descriptor closed meanwhile are skipped; an ended process
  owns nothing. A descriptor of a socket outside the four tables (another network namespace, a Unix socket) counts nowhere.
- Sockets without an owner (`TIME_WAIT`, a closed or unreadable one) count in `states` and, as connections, in `remotes` and
  `connections`, with no PID and no `command`.
- The `command` is the process collector's ([Agent system metrics](agent-system-metrics.md), *Names*): text the process chooses, which
  whoever displays it escapes; the PID is the kernel's.

## Socket UIDs

`listeners`, `new_listeners`, `changed_listeners` and `connections` entries carry the socket's UID (`uid`) and user name (`user`), taken
from the table line when it is filed, not from descriptors or the inode index, so an entry without an owner still has them. A line with
inode 0 adds no UID (the kernel prints 0, the listener's UID or the former UID for such sockets, none of them held by a process); an entry
built only from such lines has no `uid`, `user` or `mixed_uids`.

- An inventory entry, one endpoint of possibly several sockets, carries the **lowest** UID of its sockets with an inode, and `mixed_uids`
  true when they have two or more distinct UIDs (device-bound sockets with `SO_BINDTODEVICE`, UDP sockets with `SO_REUSEADDR`, a
  `SO_REUSEPORT` group after an `fchown`). Its `pid` stays the lowest owning PID, possibly of another socket. `new_listeners` and
  `changed_listeners` entries are built from the same inventory entry; a changed one adds the previous run's values.
- The `processes` aggregate carries no UID.
- The `user` is the name of the UID from `/etc/passwd`, read once per run after the owner scan, with the process collector's rules (first
  line of a UID wins, valid UTF-8, cut to 256 bytes, control characters kept for the encoder to escape), else the UID in decimal; one UID
  has one name in both snapshots of a run that read the same file. A `passwd` that cannot be read within its bounds makes every `user` the
  decimal UID; `uid` is unchanged and the sample succeeds. The `user` is external text that whoever displays it escapes. Names come only from `/etc/passwd` (no NSS, LDAP or dynamic users).

## The socket mount

An inode number alone does not tell a socket from a file with the same number on a mount a user controls. Every socket, in any namespace,
lives on one internal kernel mount that holds nothing else and that no user can mount (mount IDs are global). At start-up the agent opens
one Unix datagram socket, never bound, connected or sent on, reads the first lines of its own `/proc/self/fdinfo/<fd>`, checks that `ino:`
equals the inode `fstat` reports (else `sockmount: self/fdinfo: inode mismatch`), closes both and keeps the `mnt_id:` for its lifetime. The
probe needs no right. Failure texts: `sockmount: socket: <errno>`, `sockmount: self/fdinfo: <os error>` and `sockmount: <fixed reason>`; a
mount ID 0 is refused with `sockmount: not in the expected format`.

When the probe failed (or no probed mount is known), the collector opens no `fdinfo`, no socket has an owner, `processes` is empty, the
aggregates are incomplete, the sample succeeds and one warning `socket owners unknown` (`err`) is logged at the first run, until the agent
restarts. A kernel before 5.14 has no `ino:` line in `fdinfo`, so the probe fails this way there (the server runs 5.15 or newer).

## The listener check

The collector keeps the previous successful run's inventory in memory: its keys (at most 4,096, as the inventory holds them, not as the
size budget may cut the list), each with its lowest socket UID, whether the UID is known and whether the endpoint was mixed, plus whether
it was cut, which tables were read and whether a line was skipped. Each snapshot states its comparison in `listener_check`:

- `start`: the first run after the agent starts; nothing is reported by the agent (the backend compares across the start,
  [Listener changes](listener-changes.md));
- `none`: a skipped table line in either run, a different set of tables, or a cut inside the privileged part; nothing is reported and no
  full snapshot is taken for it. A failed run is not compared; the next successful run is compared with the last successful one;
- `privileged`: either inventory was cut at 4,096, so only keys on ports below 1024 are compared (they come first, so a flood of
  unprivileged listeners never cuts them);
- `all`: otherwise.

**New listeners.** A key of the current inventory that the previous lacks, except an unconnected UDP socket on a port from 32768 to 60999
(the kernel's default local port range, fixed, not read from the server); a TCP listener counts on any port. At most 64, in inventory
order, in `new_listeners`; the rest is the count `new_listeners_omitted`. A change of owner on the same protocol, address and port is not
new; a listener that disappears and returns is new again.

**Changed listeners.** A key both inventories hold that lies in the compared part (all ports under `all`, 1 to 1023 under `privileged`,
none under `none` and `start`), is not an unconnected UDP socket on a port from 32768 to 60999, has a `uid` in both runs, and whose lowest
UID differs or whose `mixed_uids` turned from absent to true. The test uses UID numbers only, so it holds without `/etc/passwd`. A mixed
state that persists with the same lowest UID is reported once, when it begins; `mixed_uids` turning false reports nothing; an endpoint
without a `uid` on either side reports nothing; a new key is a new listener only. At most 64, in inventory order, in `changed_listeners`
(the current entry plus `previous_uid` and `previous_mixed_uids`); the rest, with entries the size budget leaves out, is the count
`changed_listeners_omitted`. An endpoint that qualifies again in a later run is reported again, once per run. A run with a new or changed
listener is full.

**The base.** The current inventory becomes the base, also after a `none` check, **only when the run's deadline has not passed**
immediately before the run returns its record; otherwise the run returns the deadline error and keeps no inventory and no log state, so a
run that becomes a gap cannot make a listener known without a report. A size cut of `listeners` makes the aggregates incomplete but leaves
the comparison alone.

## What a snapshot's own list holds

Every snapshot says which tables its run read (`tables`: `tcp`, `tcp6`, `udp`, `udp6` in table order) and how much of the inventory its
own `listeners` list holds (`inventory`): `skipped` when a table line was skipped; `cut` when the inventory was cut inside its privileged
part or the list holds fewer entries than the inventory has below port 1024; `all` when the inventory was not cut and the list holds every
key; `privileged` otherwise (every listener on ports 1 to 1023). The state describes the list as sent, after the size budget and the
safety net. Neither field changes the agent's comparison, the full snapshots or the caps.

## The record always fits

The record is filled entry by entry within a payload budget of 1 MiB less 8 KiB of JSON (each entry encoded alone to measure it), in the
order `states`, `new_listeners`, `changed_listeners`, `listeners`, `processes`, `remotes`, `connections`; at the first entry that does not
fit, that list and every later one stop (a cut in `states`, `listeners` or `processes` sets `aggregates_incomplete`). The record is
checked against the producer contract of the [wire format](wire-format.md); as a safety net a record still too long is shortened by
halving `connections`, then `remotes`, `processes` and `listeners` (never `changed_listeners`) and marked not complete. Neither size nor a
text a user chooses (`command`, `user`) can fail the sample: long texts mean fewer entries before the cut.

## Logging

One line per change, with `collector=connections`, only for a run that finishes in time; no per-run line, no line per listener, no
process text, address, user name, UID value or table content:

- info `socket tables unavailable` (`tables`: the missing ones, comma-separated, in table order) and `socket tables available`;
- warning `socket lines not read` (`lines`: the number, `err`: `connstats: net/<table>: line <n>: <reason>`, reason `not in the expected format`, `unknown state` or `line too long`) and info `socket lines read again`;
- warning `socket owners partly unknown` (`processes`: the number of scanned processes whose descriptors could not be read, `err`:
  `connstats: <pid>/fdinfo: <fixed reason or os error>`) and info `socket owners known again`;
- warning `socket owners unknown` (*The socket mount*);
- warning `user names unavailable` (`err`: `procstats: passwd: <fixed reason or os error>`, never file content) and info `user names available`, also while `socket owners unknown` holds.

## Failures and rights

The sample fails (a gap) when no table exists, a table cannot be read, `/proc` cannot be listed or the run's deadline passes. It succeeds
when `/etc/passwd` cannot be read and when a process or descriptor cannot be read: the first `EACCES`, `EPERM` or other error on a
process, or an `fdinfo` without the four-line shape, counts that process unreadable once, stops its scan, and its sockets have no owner.
Without `CAP_SYS_PTRACE` that is every process of another user (the warning is logged), with `hidepid=1` alike. With `hidepid=2` and
without the capability other users' processes are not listed at all, so their sockets have no owner **without a warning** (residual).
With the capability `hidepid=2` hides nothing ([Agent rights](agent-rights.md)).

## Memory and speed

The live data of a run is bounded by the caps — the inode index of 65,536 sockets (two 32-bit numbers each), two inventories of 4,096
entries, 4,096 remote counts and the user names of a `passwd` within 1 MiB (at most 115,968 distinct users) — and does not grow with the
input beyond them: measured 4.7 to 5.6 MB at 100,000 and 400,000 table lines with a small `passwd`, 9.4 to 10.3 MB with a 1 MiB `passwd`,
0.75 MB at 50,000 and 200,000 descriptors of one process. Garbage grows with descriptors (about 170 B each) and is reclaimed; a run at the
caps allocates about 24.7 MB more, a 1 MiB `passwd` about 20 MB per run. A run over the captured server tables with 300 processes of 30
descriptors takes 57.8 ms ([Benchmarks](../BENCHMARKS.md), *Connection snapshot*; not measured on the server).

## Residuals

- A listener that opens while the agent is stopped is not reported by the agent (the first run is the baseline, `start`); the backend
  compares across the start ([Listener changes](listener-changes.md), record 0116). One that opened and closed meanwhile is never seen.
- A local user who keeps more than 4,096 listeners open (about 4,100 TCP listeners on unprivileged ports) forces `privileged`, hiding new
  listeners and takeovers on ports from 1024 up while it lasts; the flood alone causes no report and no full snapshot. A `none` run still
  becomes the base, so a takeover before it is never reported.
- A user who holds hundreds of thousands of descriptors lengthens the run in proportion; a run past its timeout is a gap and no inventory
  is compared while runs are gaps, so a listener that opens and closes during such a stall is never reported (visible as the gaps). A
  deadline that passes between the deadline check and the scheduler's receipt still keeps the inventory of a run that becomes a gap.
- Socket inode numbers come from a 32-bit counter shared with pipes and other kernel files; a user who allocates some four billion can
  hold a socket outside the tables with another socket's number and become one more holder (its owner only with the lowest PID). A user
  who runs more processes than the PID cap leaves later PIDs' sockets ownerless, visible as incomplete aggregates.
- Short-lived TCP listeners (an FTP passive data connection, ProFTPD without `PassivePorts`) are reported when a run sees one; a server
  whose local port range differs reports UDP sockets outside the fixed range.
- The agent's totals include sockets without an owner, which `lsof -ni` never listed, and `processes` counts only descriptors the agent can
  read, so the numbers differ from the legacy source (#45); the incident's connection peak, taken over every source, is won by the agent's
  higher totals ([Incidents](incidents.md)).
- Two UIDs that replace each other on an already mixed endpoint while the lowest UID stays the same (0 and 1001, then 0 and 1002) are not
  detectable; no set of UIDs per endpoint is kept.
- A local user who toggles the mixed state of a shared `SO_REUSEADDR` UDP endpoint (avahi on 5353) causes a change-only finding per run and
  can push an earlier takeover out of the report's 20 latest change-only findings ([Listener changes](listener-changes.md), *Caps*); the
  rows stay stored.
- The `uid` says whose socket it is, not whose process. A user who binds a second socket on a known endpoint (bound to a device) leaves the
  entry's lowest UID with `mixed_uids` true, reported as a changed listener when the mixed state begins, not as a new listener. A socket
  cannot be added to a known endpoint held by a socket not bound to a device (the bind conflicts, a reuse-port pair needs one UID, ports
  below 1024 need `CAP_NET_BIND_SERVICE`; measured on kernel 6.18.44: `EADDRINUSE`), while two device-bound sockets, or two UDP sockets
  with `SO_REUSEADDR`, can share one.

## Related decisions

- [0019](../decisions/0019-agent-reads-proc-instead-of-top-lsof.md) — why `/proc` is read instead of running `lsof`.
- [0115](../decisions/0115-connection-collector-proc-net-tables-regular-and-full-snapshots-in-one-record-kind.md) — the `/proc/net` tables,
  regular and full snapshots in one record kind.
- [0144](../decisions/0144-socket-owners-from-fdinfo-on-the-socket-mount-the-agent-learns-at-its-start.md) — owners from `fdinfo` on the
  probed socket mount (`CAP_SYS_PTRACE` alone).
- [0145](../decisions/0145-fdinfo-lines-accepted-in-the-range-the-kernel-prints-them.md) — `fdinfo` lines accepted in the range the kernel
  prints them.
- [0143](../decisions/0143-new-listener-check-against-the-last-inventory-bounded-privileged-ports-first.md) — the bounded listener check
  favoring privileged ports.
- [0146](../decisions/0146-socket-uid-from-the-tables-uid-column-lowest-uid-and-mixed-uids-per-endpoint.md) — the socket `uid` and
  `mixed_uids`.
- [0147](../decisions/0147-a-known-endpoint-whose-socket-uid-changes-is-reported-as-a-changed-listener.md) — a known endpoint whose socket
  UID changes reported as a changed listener.
- [0148](../decisions/0148-agent-marks-its-first-snapshot-after-a-start-and-what-its-listener-list-holds.md) — the `start` check, the
  `inventory` and `tables` fields.
- [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored.md) — the comparison across agent starts in the backend.

## Not here

- Scheduling, the memory pressure mode and the extra runs: [Agent](agent.md); the kills that request them:
  [Agent kernel events and boots](agent-kernel-events.md).
- Process names and the `/proc` and `/etc/passwd` readers: [Agent system metrics](agent-system-metrics.md), *Processes*.
- The listener findings across agent starts and in the report: [Listener changes](listener-changes.md).
- `CAP_SYS_PTRACE`, `hidepid` and the unit's confinement: [Agent rights](agent-rights.md).
- The `connection_snapshot` fields and endpoint mapping: [Wire format records](wire-format-records.md).

## Implementation

`internal/connstats` (collector; fixtures `testdata/proc/net/`, `testdata/connections/sandbox/`, `testdata/etc/passwd`; it shares
`ListPIDs`, `CommandReader`, `Ended`, `ReadUsers` and `UserName` of `internal/procstats`; `TestConnstats_ImportsOnlyAllowedPackages` pins
its imports, `TestConnections_ManySocketsStayWithinCaps` the memory figures, `BenchmarkConnections_Collect` the speed,
`TestConnections_LogsHoldNoProcessTextOrAddress` the log texts), `internal/sockmount` (the start-up probe;
`TestSockmount_ImportsOnlyAllowedPackages`).
