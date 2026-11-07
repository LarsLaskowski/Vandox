# 0146: The socket's UID comes from the tables' uid column, with the lowest UID and a mixed flag per endpoint

- **Status:** Accepted
- **Date:** 2026-10-18
- **Area:** Agent connections
- **Source:** Issue #279
- **Supersedes:** —

## Context

Issue #279 adds who holds a socket in a form no process chooses: the `command` of a connection entry is the process's own text and
is missing whenever the owner scan ([0144](0144-socket-owners-from-fdinfo-on-the-socket-mount-the-agent-learns-at-its-start.md))
cannot see the holder (no `CAP_SYS_PTRACE`, `hidepid=2`, a failed probe, a cap), while the tables carry the kernel's `uid` column
(field 8), which the reader counted and skipped. That number is the socket's UID, the file-system UID of the process that created
it, changed only by an `fchown` (which needs `CAP_CHOWN` to give it to another user); for a line with inode 0 it is 0 (time-wait),
root or the former UID (orphaned, 5.15 and 6.x differ) or the listener's (pending), so it names no holder there (upstream
`net/ipv4/tcp_ipv4.c`, `net/ipv4/udp.c`, `net/ipv6/*`, `include/net/sock.h`, `net/socket.c`, read at v5.15 and v6.18). Wire format
1.1 was unreleased.

## Options considered

1. **The socket's `uid` column of the tables, with a name from `/etc/passwd`** (chosen) — the kernel's number, present for every
   socket with a file whether or not its holder is known, and named by the process collector's rules, so one UID has one name in
   both snapshots. It is the creator's UID, not the holder's: a daemon that binds as root and then switches to `www-data`, or a
   socket systemd creates for a socket-activated service, reads root.
2. **The holder's effective UID from `/proc/<pid>/status`** — the process's user, but only where the owner scan already named the
   holder, so it is missing exactly where the `command` is, and the process snapshot already records it. Rejected.
3. **The UID attached through the inode index when owners are assigned** — one place for owner and UID, but the index drops sockets
   beyond its cap and repeated inodes, which would lose their UID. Rejected for the UID taken when the table line is filed as a
   listener or a connection (spec challenge).
4. **Several UIDs on one endpoint** (sockets bound to different devices, UDP `SO_REUSEADDR`, an `fchown` inside a `SO_REUSEPORT`
   group): a list of UIDs per entry — complete, but an entry's size would grow with what a user binds; or the UID as part of the
   inventory key — a second user on a service's endpoint would become a new listener, the comparison of 0143 and 0116 changes and a
   user could split the inventory. Both rejected for the **lowest UID and a flag `mixed_uids`**, which keeps the entry bounded and
   the case visible.
5. **The printed number for lines with inode 0** — no rule needed, but time-wait lines print 0, which reads as root, and orphaned
   lines differ between kernels. Rejected: such entries carry no UID.
6. **Names for only the run's UIDs, or cached across runs** — less memory or fewer reads, but a second passwd reader or a cache
   that can be stale; the file is root's, bounded and read by the process collector every run already. Rejected for the process
   collector's reader, once per run.
7. **The name alone, without the number** — shorter, but names are neither unique nor always present (UIDs without a line).
   Rejected for `uid` as an optional number, absent meaning unknown, with `user` beside it; wire format 1.1 is unreleased, so the
   fields extend it in place.

## Decision

Listener and connection entries carry the table line's `uid` and its `user` from `/etc/passwd`, taken when the line is filed and
left out for lines with inode 0, and an inventory entry with several UIDs carries the lowest and `mixed_uids`. The rules are in
[Agent connections](../areas/agent-connections.md) (*Socket UIDs*) and the fields in
[Wire format records](../areas/wire-format-records.md) (*`connection_snapshot`*).

## Consequences

- The `uid` says whose socket it is, not whose process: a root-created socket held by a `www-data` worker reads root, and a reader
  that wants the process's user takes the process snapshot. Only a holder of `CAP_CHOWN` (or of `CAP_SETUID` before it creates the
  socket) can make a socket show another user's UID. A snapshot without `uid` (a 1.0 agent, an imported `lsof` snapshot) reads as
  "UID unknown", never as root.
- The connection collector reads `/etc/passwd` once per run, so the run's live data grows by the names of that bounded file; a
  table line whose `uid` field is malformed is unreadable like a bad inode, which also makes the MariaDB collector's exposed-listener
  count refuse that table.
- The forensics report and its page show the UID and user of each new listener as "socket UID" (#304,
  [0149](0149-new-listeners-section-capped-per-finding-kind-changed-endpoints-without-command.md)).
