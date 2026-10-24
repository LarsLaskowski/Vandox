# 0115: Connection collector: the /proc/net tables with owners from /proc/<pid>/fdinfo, regular and full snapshots in one record kind, a bounded new-listener check that favors privileged ports

- **Status:** Accepted
- **Date:** 2026-10-18
- **Area:** Agent
- **Source:** Issue #33, Issue #279 (the socket's UID)
- **Supersedes:** —

## Context

Issue #33 replaces the `lsof -ni` half of the legacy script: count sockets per state, per process and per remote address, keep an
inventory of listening sockets, report new listeners, and keep a full connection list on anomalies, without running `lsof` (0019) and
without making the memory situation of the 2 GB server worse. The wire format already has `connection_snapshot`, written by the legacy
`lsof` parser, read by the incident's connection peak and chart; its `complete` means "no entry the producer saw was left out", so an
aggregate-only snapshot cannot be told from a full one by it. The agent may hold only `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH` (0030),
and a local user can open as many sockets and listeners as the limits allow. Wire format 1.0 was released with v0.1.0. The behavior is
in [Agent](../areas/agent.md) (*Connections*) and [Wire format](../areas/wire-format.md) (*`connection_snapshot`*); the spec is in the
squad record of #33.

Issue #279 adds who holds a socket in a form no process chooses: the `command` is the process's own text and is missing
whenever the owner scan cannot see the holder (no `CAP_SYS_PTRACE`, `hidepid=2`, a failed probe, a cap), while the tables carry the
kernel's `uid` column (field 8), which the reader counted and skipped. That number is the socket's UID, the file-system UID of the
process that created it, changed only by an `fchown` (which needs `CAP_CHOWN` to give it to another user); for a line with inode 0 it
is 0 (time-wait), root or the former UID (orphaned, 5.15 and 6.x differ) or the listener's (pending), so it names no holder there
(upstream `net/ipv4/tcp_ipv4.c`, `net/ipv4/udp.c`, `net/ipv6/*`, `include/net/sock.h`, `net/socket.c`, read at v5.15 and v6.18).

## Options considered

1. **Netlink socket diagnostics (`sock_diag`)** instead of the text tables — faster on very many sockets and filterable in the kernel,
   but the collector would open and query a netlink socket every run, which the agent's collectors do not (0111, 0113), and it still
   needs the descriptors for the owners. Rejected. (The one socket of option 11 is opened once by the agent at its start, outside any
   collector, and is never bound, connected or used.)
2. **The four `/proc/net` tables of the agent's network namespace** (chosen) — what the issue names; read through `io/fs`, streamed
   line by line, so tests run on real captured tables.
3. **A full connection list every run** — simplest, but about 4,096 entries of up to 1 KiB each every minute for data that matters
   only around an anomaly. Rejected for a **regular** snapshot (aggregates and inventory) every run and a **full** one on the first run,
   under memory pressure and on a new listener.
4. **New listeners as a record kind of their own (`listener_event`)** or as a new `kernel_event` type — a separate record per listener,
   but a new kind in both languages, the storage and every reader, and the event would lose the full snapshot of the same moment;
   `kernel_event` is about the kernel. Rejected for **additive fields of `connection_snapshot`** (`aggregates_only`,
   `aggregates_incomplete`, `listener_check`, `new_listeners`, `new_listeners_omitted`), whose absence describes imported and older
   snapshots correctly (full, aggregates complete, no check made).
5. **Reusing `complete` for "regular or full"** — no wire change, but it would change what `complete` means for the readers that rely
   on it. Rejected (spec challenge).
6. **Comparing new listeners against an unbounded set of every listener seen** — no misses, but memory grows with what a local user
   opens. Rejected for a comparison against the last recorded inventory (at most 4,096 entries) with the privileged ports (1–1023)
   listed first, compared alone when an inventory was cut, and no comparison after a skipped line or a changed set of tables; each
   snapshot says which comparison was made.
7. **Reading `ip_local_port_range`** for the UDP sockets that are not reported — exact per server, but one more file and its failure
   modes. Rejected for the kernel's default range 32768–60999, fixed.
8. **Keeping wire format 1.0** as 0084 did for an unreleased format — no version change, but 1.0 is released now and the format's rule
   calls an added field a minor change. Rejected: the version becomes **1.1**; both decoders already accept a newer minor and ignore
   unknown keys.
9. **Deriving the process `command` again in the new package** — independent, but a second copy of the legacy name derivation that
   `testdata/processes/names.json` pins. Rejected for exported helpers of `internal/procstats` (`ListPIDs`, `CommandReader`).
10. **Owners from the `/proc/<pid>/fd` links** (`socket:[N]`), as the issue names — exact and one `readlink` per descriptor, but
    measured (Security, kernel 6.18, UID 65534 against a root process) to need `CAP_DAC_READ_SEARCH` to list the directory and both
    capabilities to read a link; `CAP_DAC_READ_SEARCH` lets the whole agent read every file (`/etc/shadow`, Plesk's credentials), a right
    no collector holds yet. Rejected.
11. **Owners from `/proc/<pid>/fdinfo/<n>` on the socket mount the agent learns from a socket of its own** (chosen) — measured
    readable with `CAP_SYS_PTRACE` alone, the right the process collector already uses; its first lines are `pos:`, `flags:`, `mnt_id:`,
    `ino:` (captured samples). Costs an open, a read and a close per descriptor instead of one `readlink`, and needs the `ino:` line
    (Linux 5.14 and later; without it the owners are unknown, as without the right). An inode number alone does not tell a socket from a
    file with the same number, which any user can make on a mount of their own; the mount does, since every socket of every family and
    namespace is on one internal kernel mount that holds nothing else, and mount IDs are global (measured on 6.18). The agent learns
    that mount's ID once at its start from an unbound, unconnected Unix datagram socket and its own `fdinfo`, which needs no right; a
    failed probe leaves every socket without owner (fail closed). Residual: the 32-bit inode counter wraps after some four billion
    allocations, so a user can make a socket of their own with another socket's number and become one more holder of it, its owner only
    with a lower PID than every real holder.
12. **The sockets' mount as the majority** of the mounts matching descriptors are on, among those the agent's mount table does not list
    — no socket of the agent's own, but Security measured that a user outvotes it cheaply: one colliding file on a tmpfs or FUSE mount in
    a user namespace, duplicated up to the open-file limit, or a low PID filling a cap on matches before the real holders are read, makes
    the owners of every socket theirs to choose or erase. Rejected; a restated residual and re-adding `CAP_DAC_READ_SEARCH` for the
    `fd/` links (option 10) were declined by the Product Manager.
13. **The socket's `uid` column of the tables, with a name from `/etc/passwd`** (chosen, #279) — the kernel's number, present for every
    socket with a file whether or not its holder is known, and named by the process collector's rules, so one UID has one name in both
    snapshots. It is the creator's UID, not the holder's: a daemon that binds as root and then switches to `www-data`, or a socket
    systemd creates for a socket-activated service, reads root.
14. **The holder's effective UID from `/proc/<pid>/status`** — the process's user, but only where the owner scan already named the
    holder, so it is missing exactly where the `command` is, and the process snapshot already records it. Rejected.
15. **The UID attached through the inode index when owners are assigned** — one place for owner and UID, but the index drops sockets
    beyond 65,536 and repeated inodes, which would lose their UID. Rejected for the UID taken when the table line is filed as a
    listener or a connection (spec challenge).
16. **Several UIDs on one endpoint** (sockets bound to different devices, UDP `SO_REUSEADDR`, an `fchown` inside a `SO_REUSEPORT`
    group): a list of UIDs per entry — complete, but an entry's size would grow with what a user binds; or the UID as part of the
    inventory key — a second user on a service's endpoint would become a new listener, but the comparison of this record and of 0116
    changes and a user could split the inventory. Both rejected for the **lowest UID and a flag `mixed_uids`**, which keeps the entry
    bounded and the case visible.
17. **The printed number for lines with inode 0** — no rule needed, but time-wait lines print 0, which reads as root, and orphaned
    lines differ between kernels. Rejected: such entries carry no UID.
18. **Names for only the run's UIDs, or cached across runs** — less memory or fewer reads, but a second passwd reader or a cache
    that can be stale; the file is root's, bounded at 1 MiB and read by the process collector every run already. Rejected for the
    process collector's reader (`procstats.ReadUsers`), once per run.
19. **The name alone, without the number** — shorter, but names are neither unique nor always present (UIDs without a line). Rejected
    for `uid` as an optional number, absent meaning unknown, with `user` beside it; wire format 1.1 is unreleased, so the fields
    extend it in place.

## Decision

Options 2, 3, 4, 6, 7, 8, 9 and 11: a collector `connections` in `internal/connstats` reads the four tables and the `fdinfo` files of at
most 4,096 processes within fixed caps, needing no right beyond `CAP_SYS_PTRACE`, and records one `connection_snapshot` per run, regular or full, with the new-listener check and
its outcome in the same record; wire format 1.1. The rules, caps and log lines are in [Agent](../areas/agent.md) (*Connections*), the
fields in [Wire format](../areas/wire-format.md).

For #279, options 13, 15, 16, 17, 18 and 19: listener and connection entries carry the table line's `uid` and its `user` from
`/etc/passwd`, taken when the line is filed and left out for lines with inode 0, an inventory entry with several UIDs carries the
lowest and `mixed_uids`; the rules are in [Agent](../areas/agent.md) (*Connections*) and [Wire format](../areas/wire-format.md).

## Consequences

- The agent opens one Unix datagram socket at its start, never bound, connected or sent on, so the unit's filters must allow `AF_UNIX`
  (as the systemd notification needs anyway); the collector itself opens none.
- The collector adds no right: it uses `CAP_SYS_PTRACE` like the process collector, and `deploy/agent/README.md` lists that use with
  the measurement. `CAP_DAC_READ_SEARCH` stays unused by any collector; a later collector that wants it decides on its own. (Clarified in place:
  [0122](0122-cap-sys-ptrace-alone-cannot-write-another-users-process-memory-through-proc-pid-mem-or-fd.md) drops the capability and
  excludes granting it again without a record that supersedes 0122.)
- A run that passes its deadline keeps no state, so a late return cannot make a listener known without a report; the window between
  the last check and the return is a stated residual.
- The live memory of a run is bounded by fixed caps; the garbage grows with the descriptors read (a fixed amount per descriptor through
  `io/fs`), reclaimed by the runtime.
- **Residuals:** a listener that opens while the agent is stopped is not reported by the agent (the backend compares across a start, [0116](0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md)); a local
  user who keeps more than 4,096 listeners open hides new listeners on ports from 1024 up for as long, visible as `listener_check`
  `privileged`; a user who keeps the collector stalled (a descriptor hog) suppresses the check while runs are gaps; with `hidepid=2` and without `CAP_SYS_PTRACE` owners are lost without a warning; a user who wraps the inode counter becomes one more
  holder of another socket (option 11), and one with more processes than the PID cap leaves later sockets without owner, visible as
  incomplete aggregates; short-lived TCP listeners such as
  passive FTP are reported when a run sees them.
- The agent's totals include sockets without an owner (`TIME_WAIT`), which `lsof -ni` never listed, and the incident's connection peak
  is taken over every source in its window, so agent snapshots win it against imported ones (accepted, [Detection, alerts and
  reports](../areas/detection-and-reports.md)).
- IPv4-mapped addresses stay as the kernel reports them, so one peer can be two `remotes` entries.
- The backend must be upgraded to read 1.1 before agents send it (wire format, *Versioning*); a 1.0 backend reads the new fields as
  absent.
- The `uid` says whose socket it is, not whose process: a root-created socket held by a `www-data` worker reads root, and a reader
  that wants the process's user takes the process snapshot. Only a holder of `CAP_CHOWN` (or of `CAP_SETUID` before it creates the
  socket) can make a socket show another user's UID. A snapshot without `uid` (a 1.0 agent, an imported `lsof` snapshot) reads as
  "UID unknown", never as root.
- The connection collector reads `/etc/passwd` once per run, so the run's live data grows by the names of that bounded file; a table
  line whose `uid` field is not 1 to 10 digits within 32 bits is unreadable like a bad inode, which also makes `connstats.TCPListeners`
  (the MariaDB collector's exposed-listener count) refuse that table. Showing the UID in the report and the web UI is a follow-up.
