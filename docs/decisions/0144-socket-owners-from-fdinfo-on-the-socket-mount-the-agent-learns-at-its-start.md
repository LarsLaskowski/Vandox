# 0144: Socket owners come from /proc/<pid>/fdinfo on the socket mount the agent learns at its start

- **Status:** Accepted
- **Date:** 2026-10-18
- **Area:** Agent connections
- **Source:** Issue #33
- **Supersedes:** —

## Context

The connection collector ([0115](0115-connection-collector-proc-net-tables-regular-and-full-snapshots-in-one-record-kind.md))
counts sockets per process, so it must find which processes hold each socket of the tables. The agent may hold only
`CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH` (0030), and the process collector already uses `CAP_SYS_PTRACE` (0113). The tables give a
socket's inode number; a process's descriptors show which inodes it holds. Any local user can create files with chosen inode
numbers on a mount of their own (a tmpfs or FUSE mount in a user namespace).

## Options considered

1. **Owners from the `/proc/<pid>/fd` links** (`socket:[N]`), as the issue names — exact and one `readlink` per descriptor, but
   measured (Security, kernel 6.18, UID 65534 against a root process) to need `CAP_DAC_READ_SEARCH` to list the directory and both
   capabilities to read a link; `CAP_DAC_READ_SEARCH` lets the whole agent read every file (`/etc/shadow`, Plesk's credentials), a
   right no collector holds yet. Rejected.
2. **Owners from `/proc/<pid>/fdinfo/<n>` on the socket mount the agent learns from a socket of its own** (chosen) — measured
   readable with `CAP_SYS_PTRACE` alone. Costs an open, a read and a close per descriptor instead of one `readlink`, and needs the
   `ino:` line (Linux 5.14 and later; without it the owners are unknown, as without the right). An inode number alone does not tell a
   socket from a file with the same number; the mount does, since every socket of every family and namespace is on one internal
   kernel mount that holds nothing else, and mount IDs are global (measured on 6.18). The agent learns that mount's ID once at its
   start from an unbound, unconnected Unix datagram socket and its own `fdinfo`, which needs no right; a failed probe leaves every
   socket without owner (fail closed).
3. **The sockets' mount as the majority** of the mounts matching descriptors are on, among those the agent's mount table does not
   list — no socket of the agent's own, but Security measured that a user outvotes it cheaply: one colliding file on a tmpfs or FUSE
   mount in a user namespace, duplicated up to the open-file limit, or a low PID filling a cap on matches before the real holders are
   read, makes the owners of every socket theirs to choose or erase. Rejected; a restated residual and re-adding
   `CAP_DAC_READ_SEARCH` for the `fd/` links (option 1) were declined by the Product Manager.
4. **Netlink socket diagnostics** for the owners — they do not name the holders either; the descriptors are needed regardless
   (0115, option 1).

## Decision

The collector finds a socket's holders in the `fdinfo` files of the processes, counting a descriptor only when its inode is a tracked
socket's and its mount is the socket mount the agent learns once at its start from a probe socket of its own, so no right beyond
`CAP_SYS_PTRACE` is needed. The rules are in [Agent connections](../areas/agent-connections.md) (*Owners*, *The socket mount*) and
[Agent rights](../areas/agent-rights.md).

## Consequences

- The agent opens one Unix datagram socket at its start, never bound, connected or sent on, so the unit's filters must allow
  `AF_UNIX` (as the systemd notification needs anyway); the collector itself opens none.
- The collector adds no right: it uses `CAP_SYS_PTRACE` like the process collector. `CAP_DAC_READ_SEARCH` stays unused by any
  collector (clarified in place: [0122](0122-cap-sys-ptrace-alone-cannot-write-another-users-process-memory-through-proc-pid-mem-or-fd.md)
  drops the capability and excludes granting it again without a record that supersedes 0122).
- Residuals: the 32-bit inode counter wraps after some four billion allocations, so a user can make a socket of their own with
  another socket's number and become one more holder of it, its owner only with a lower PID than every real holder; with `hidepid=2`
  and without `CAP_SYS_PTRACE` owners are lost without a warning; a user with more processes than the PID cap leaves later sockets
  without owner, visible as incomplete aggregates.
