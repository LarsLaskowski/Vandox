# Spec: Record the socket's UID from the kernel table in connection snapshots

Source: Issue #279
Status: Approved by Lead

## Problem

Whoever reads a `connection_snapshot` of the agent (today the stored record and the backend's listener findings, later the views
built on them) learns who holds a listener or a connection only from the owner's `pid` and `command`. The `command` is text the
process chooses (`internal/connstats/snapshot.go`, `listenerOf`; [Agent](../../docs/areas/agent.md), *Connections*, *Owners*: "It is
text the process chooses"), so a local user's program can call itself `sshd` and a new listener on port 2222 reads like the SSH
daemon. The owner is also missing whenever the descriptor scan cannot see a process (no `CAP_SYS_PTRACE`, `hidepid=2`, a failed
start-up probe of the socket mount, a process beyond the PID cap), and then the entry says nothing about who holds the socket.

The kernel's socket tables already carry a number no process can choose freely: the `uid` column of `/proc/net/{tcp,tcp6,udp,udp6}`
(field 8). The table reader counts that field and does not read it (`internal/connstats/table.go`, `parseLine`), and the area document
says so: "The kernel's `uid` column of the tables is not recorded (a follow-up)". The process snapshot already turns a UID into a
user name (`internal/procstats/passwd.go`, `readPasswd`; `procstats.go`, `userName`), so a reader that compares the two snapshots
has no common name to compare today.

Claims of the issue checked against the code and the kernel:

- the collector names a listener's owner by PID and `command` only: **confirmed** (`model.Listener`, `model.Connection`);
- the `command` is text a local user chooses: **confirmed** (agent area, *Connections*, *Owners*);
- the `uid` column is the kernel's: **confirmed, with a nuance the scenarios build on**: it is the UID of the **socket**, not of the
  process that holds it — the file-system UID of the process that created the socket (`net/socket.c`, `sock_alloc`,
  `inode->i_uid = current_fsuid()`), changed only by an `fchown` of the socket, which needs `CAP_CHOWN` to give it another user — and
  for a socket that no longer has a file (inode 0) the number is not an owner at all (*Open questions*);
- the process snapshot resolves a user name: **confirmed** (the effective UID's name from `/etc/passwd`, else the UID in decimal);
- "an additive wire field": **confirmed**; wire format 1.1 is unreleased (the only tag on `origin` is `v0.1.0`, format 1.0), so the
  fields extend 1.1 in place (*Open questions*).

## Scenarios

- S1: Given `/proc/net/tcp` holds a `LISTEN` line on `0.0.0.0:22` with `uid` `0` and `/etc/passwd` has `root:x:0:…`, when the
  collector `connections` runs, then the `listeners` entry reads
  `{"proto":"tcp","local":"0.0.0.0:22","pid":<owner>,"command":"sshd","uid":0,"user":"root"}`: `uid` 0 is written, never left out
  as a zero value, and `user` is the name the process snapshot would give UID 0.
- S2: Given a full snapshot (the first run, memory pressure, an OOM-kill run or a run with a new listener) and an `ESTABLISHED` line
  of `tcp6` with `uid` `33`, when the collector runs, then that socket's `connections` entry carries `"uid":33,"user":"www-data"`
  beside its `pid` and `command`.
- S3: Given the owner scan cannot name the holder of a socket — the start-up probe of the socket mount failed, the agent lacks
  `CAP_SYS_PTRACE`, `hidepid=2` hides the process, the process lies beyond the PID cap, or the socket lies beyond the inode index
  (65,536 sockets) or repeats an inode the index already holds — when the collector runs, then the entry has no `pid` and no
  `command` as today but still carries the `uid` and `user` of its table line, because they are taken when the table line is filed
  as a listener or a connection, not from the descriptors or the inode index.
- S4: Given a local user with UID 1001 (`alice` in `/etc/passwd`) starts a program that names itself `sshd` and listens on
  `0.0.0.0:2222`, when the next run compares its inventory with the previous one, then the `new_listeners` entry reads
  `{"proto":"tcp","local":"0.0.0.0:2222","pid":<pid>,"command":"sshd","uid":1001,"user":"alice"}`, the same `uid` and `user` as that
  endpoint's entry in `listeners`, so the reader sees the kernel's UID next to the name the program chose.
- S5: Given a socket whose UID has no line in `/etc/passwd` (the captured tables `testdata/proc/net/{tcp,tcp6,udp,udp6}` hold the
  UIDs 0, 30, 102, 114, 115, 116, 118 and 998, and the fixture `testdata/etc/passwd` names only 0 of them), when the collector runs,
  then its entry carries `"uid":116,"user":"116"`, the decimal UID, as the process snapshot writes a user without a name.
- S6: Given a daemon that created its listener as root and then switched its effective user to `www-data` (the usual way of
  `nginx` or `apache2` on a Plesk server), when the collector and the process collector run, then the `listeners` entry reads
  `"uid":0,"user":"root"` while the process snapshot shows the process with `"user":"www-data"`: the connection snapshot records whose
  socket it is, the process snapshot whose process it is, and the area document says so (the same holds for a socket that systemd
  creates for a socket-activated service, or any socket a root process hands to another user's process: it reads `"uid":0`).
- S7: Given an agent batch whose connection snapshots carry `uid`, `user` and `mixed_uids`, when the backend decodes and stores it,
  then the C# decoder accepts it with the same rules as the Go model, the stored payload (`records.data`) and the stored copies of
  `new_listeners` (`connection_marks`, `listener_comparisons`) keep the three fields, and a snapshot without them (a 1.0 agent, an
  imported legacy `lsof` snapshot) reads as "UID unknown", never as UID 0.

## Errors and edge cases

- E1: A table line with inode 0 — a `TIME_WAIT` socket, a `FIN_WAIT2` socket that its process already closed, any other orphaned
  socket, or a pending connection (`SYN_RECV`) — gets neither `uid` nor `user`: the kernel writes 0 for a time-wait socket, root
  (5.15) or the UID the socket had before its file was closed (6.x) for an orphaned one, and the UID of the listener for a pending
  connection; none of it is a socket some process holds, it differs between kernels, and 0 would read as root. Such an entry already carries no `pid` and no `command`; it now carries no UID either.
- E2: Several sockets on one endpoint of the inventory whose UIDs differ — TCP or UDP sockets bound to different devices
  (`SO_BINDTODEVICE`, which any user may set once on a socket and which lets sockets share protocol, address and port whatever their
  UIDs), UDP sockets bound with `SO_REUSEADDR` (allowed across users), or a `SO_REUSEPORT` group (one UID at bind time) of which one
  socket was later given to another user by an `fchown` (which needs `CAP_CHOWN`): the entry carries the lowest of their UIDs and its
  `user`, and `"mixed_uids":true`; its `pid` stays the lowest owning PID and may belong to another of the sockets. An entry whose
  sockets share one UID carries no `mixed_uids`. Sockets with inode 0 (E1) take no part.
- E3: A `uid` field that is not 1 to 10 decimal digits within 32 bits (a letter, a sign, `4294967296`, eleven digits): the line is
  unreadable with the existing reason `not in the expected format`, like a bad inode today: it is counted, the warning `socket lines not
  read` names it, the aggregates are incomplete, the run's `listener_check` is `none` and `inventory` is `skipped`. The kernel prints
  the field as an unsigned decimal (`%5u`), so a real table never does this.
- E4: The extremes a real table can hold are recorded as printed: `0`, `4294967295`, and `65534` (the kernel's overflow UID, which a
  socket shows when its UID has no mapping in the agent's user namespace).
- E5: `/etc/passwd` is missing, unreadable, larger than 1 MiB or has a line over 4 KiB: every entry's `user` is its decimal UID, `uid`
  is unchanged, the sample succeeds, and one warning `user names unavailable` (`err`: the file named `passwd` and a fixed reason or
  the operating system's error) is logged with `collector=connections`; when the file can be read again, one info line `user names
  available`. Lines are written per change and only for a run that finishes in time, like the collector's other lines.
- E6: `/etc/passwd` with several lines for one UID, a name with bytes that are not valid UTF-8, a name over 256 bytes or with control
  characters: the first line of the UID wins, an invalid byte becomes U+FFFD, the name is cut to 256 bytes at a character boundary,
  control characters are kept and escaped by the JSON encoder — the process collector's rules, so one UID has one name in both
  snapshots of a run that read the same file.
- E7: A run near the caps (4,096 listeners and 4,096 connections) with long user names: the record still fits and the sample never
  fails for size; the fill order (`states`, `new_listeners`, `listeners`, `processes`, `remotes`, `connections`) and its cut rules are
  unchanged, so larger entries mean fewer entries before the cut, marked as today (`complete` false, `aggregates_incomplete`,
  `inventory`).
- E8: Hostile or broken input to a decoder, in Go and C# alike: `uid` negative, a fraction, above 4294967295, a string or `1e400` is
  `ErrMalformed` (the existing rule for integers out of range); `null` of `uid`, `user` or `mixed_uids` reads as absent (0089);
  `user` over 1,024 bytes fails the short-text rule (`too long`); a non-empty `user` without `uid` is the field error
  `data.<list>[<i>].user: requires uid`, and `mixed_uids` true without `uid` is `data.<list>[<i>].mixed_uids: requires uid` (checked in
  that order, before the length of `user`); `"mixed_uids":false` and `"user":""` read as absent and are accepted with or without
  `uid`; `uid` without `user` is accepted (a reader shows the decimal UID); `mixed_uids` on a connection entry is an unknown key and
  ignored.
- E9: A 1.0 backend reading a 1.1 snapshot with the fields ignores them as unknown keys; this backend reading a snapshot without them
  (a 1.0 agent, an import, a record stored before this change) treats the UID as unknown.
- E10: The listener on a known endpoint closes and a process of another user binds the same protocol, address and port between two
  runs: the entry's `uid` changes, but the endpoint is not reported as a new listener.

## Non-functional requirements

- **Rights and sockets:** none added. The tables are already read; `/etc/passwd` is world-readable and already read by the process
  collector; the collector still starts no program, opens no socket and makes no raw system call.
- **Readers (security area of the agent's readers):** the table reader parses one more field with the existing strictness (E3);
  `/etc/passwd` is read with the process collector's bounds (at most 1 MiB, lines of at most 4 KiB) at most once per run of the
  collector.
- **Time and memory:** one bounded read of `/etc/passwd` per run; the live data of a run grows by at most the names of that bounded
  file and a UID per inventory and connection entry. The run's figures in the agent area (*Bounded memory*, *Speed*) are measured again
  with the change.
- **Size:** each listener or connection entry grows by `"uid":<n>` and `"user":"<name>"` (about 30 bytes for a typical name, at most
  some 1.5 KiB for a 256-byte name of escaped control characters) and a listener entry by `"mixed_uids":true` where it applies; the
  record stays within the existing payload budget (E7).
- **Privacy:** the user names of the server's accounts enter the record, as they already do in the process snapshot; no log line
  holds a user name or a UID value.
- **Compatibility:** wire format stays 1.1 (unreleased), the fields are optional and additive; the backend must still be upgraded
  before the agents (wire format, *Versioning*). The golden fixture `testdata/wire/all-kinds.jsonl` and `decoder-cases.json` pin the
  fields between the Go encoder and the C# decoder, including a written `uid` of 0.
- **Configuration:** none new.
- **Logs:** `user names unavailable` / `user names available` with `collector=connections` (E5); no per-run line, no line per entry.

## Acceptance criteria

- [ ] AC1: every `listeners` and `new_listeners` entry built from table lines with an inode other than 0 carries `uid` (the lowest
  `uid` field of its sockets, written also when it is 0) and `user`, whether or not the entry has an owner `pid` and `command`; a
  `new_listeners` entry carries the same `uid`, `user` and `mixed_uids` as its endpoint's inventory entry; this holds also for a
  socket beyond the inode index or with a repeated inode (S1, S3, S4, S6).
- [ ] AC2: every `connections` entry of a full snapshot built from a table line with an inode other than 0 carries that line's `uid`
  and `user`, whether or not it has an owner (S2, S3).
- [ ] AC3: an entry built only from table lines with inode 0 carries no `uid`, no `user` and no `mixed_uids`: the captured `TIME_WAIT`
  line of `testdata/proc/net/tcp` (state `06`, the only inode-0 line of the captured tables) and generated lines for a `SYN_RECV`
  socket (the listener's UID), a `FIN_WAIT2` socket without a file (time-wait form, UID 0) and an orphaned `FIN_WAIT1` and `LAST_ACK`
  socket (UID 0 and a non-zero UID); a socket with inode 0 on an endpoint whose other socket has an inode adds no UID to it (E1).
- [ ] AC4: an inventory entry whose sockets (inode other than 0) have two or more distinct UIDs carries the lowest of them, its
  `user` and `mixed_uids` true, for a generated TCP `LISTEN` pair and a generated UDP pair on one endpoint, in either order of the
  lines; one whose sockets share a UID carries no `mixed_uids` (E2).
- [ ] AC5: the table reader accepts a `uid` field of 1 to 10 decimal digits within 32 bits, including `0`, `65534` and `4294967295`,
  and makes any other `uid` field an unreadable line with the reason `not in the expected format`, with the existing consequences for
  the log line, `aggregates_incomplete`, `listener_check` and `inventory` (E3, E4).
- [ ] AC6: `user` is resolved from `/etc/passwd` by the process collector's rules (first line of a UID, valid UTF-8, at most 256
  bytes, else the decimal UID); a `passwd` that cannot be read within its bounds makes every `user` the decimal UID, leaves the sample
  successful and logs `user names unavailable` once and `user names available` when it is read again, with `collector=connections`
  and without any user name or UID in the line (S5, E5, E6).
- [ ] AC7: a run with 4,096 listeners and 4,096 connections whose UIDs have 256-byte names still returns one record that
  `wire.CheckRecord` accepts, cut and marked by the existing fill rules (E7).
- [ ] AC8: the Go model and the C# model accept `uid` (0 to 4294967295), `user` (short text) and `mixed_uids` on listener entries
  (`listeners`, `new_listeners`) and `uid` and `user` on `connections` entries, refuse a non-empty `user` or `mixed_uids` true
  without `uid` with the field error `requires uid`, accept `uid` without `user`, `"user":""` and `"mixed_uids":false` (both read as
  absent), read `null` as absent and an out-of-range, negative, fractional or string `uid` as `ErrMalformed`; the golden batch
  `testdata/wire/all-kinds.jsonl` holds entries with `uid` 0, a `user` and `mixed_uids`, which the Go encoder reproduces byte for byte
  and the C# decoder reads back, and `decoder-cases.json` holds the refused and the accepted edge forms for both decoders (S7, E8).
- [ ] AC9: a stored agent snapshot keeps `uid`, `user` and `mixed_uids` in `records.data` and in the stored `new_listeners` copies of
  `connection_marks` and `listener_comparisons`, and a snapshot without them decodes with the UID absent, not 0 (S7, E9).

## Out of scope

- Showing `uid` or `user` in the forensics report's *New listeners* section or in the web UI: the issue asks for the recorded field;
  the presentation is a follow-up issue (title proposal: "[Backend] Show the socket's UID and user in the new-listener findings").
- Reporting a change of UID on a known endpoint as a new listener, or comparing inventories by UID (E10): the comparison stays by
  protocol, address and port in the agent and the backend (records 0115 and 0116: "A change of the owner on the same protocol, address
  and port is not new").
- A user or UID on the per-process `processes` aggregate: it counts the sockets a process holds, whose UIDs can differ (S6); the
  process's own user is in the process snapshot.
- The legacy `lsof -ni` import: its `USER` column is a name, not the kernel's UID, and the importer keeps writing no `uid` or `user`.
- Group IDs, name services other than `/etc/passwd` (NSS, LDAP, `systemd-userdbd` dynamic users), and any alert on who listens where.

## Open questions

- Which number does the kernel print in the `uid` column, and when is it not the socket's owner?
  Answered: from the upstream source at v5.15 (the Ubuntu 22.04 kernel's base), read for this spec: real sockets print
  `from_kuid_munged(seq_user_ns(…), sock_i_uid(sk))` with `%5u` (`net/ipv4/tcp_ipv4.c` `get_tcp4_sock`, `net/ipv6/tcp_ipv6.c`,
  `net/ipv4/udp.c` `udp4_format_sock`, `net/ipv6/datagram.c`), so the field is unsigned decimal and an unmapped UID shows as the
  overflow UID; `sock_i_uid` (`net/core/sock.c`) is the socket inode's UID, which `sock_alloc` (`net/socket.c`) sets to the creator's
  file-system UID and `sockfs_setattr` changes on an `fchown`, and is root when the socket has no file any more; a time-wait socket
  prints a literal `0` (`get_timewait4_sock`, `%5d`, state `TIME_WAIT` or `FIN_WAIT2`), a pending connection the listener's UID
  (`get_openreq4`), and all three print inode 0 (`sock_i_ino` is 0 without a file). Hence E1's rule by inode 0. The plan keeps these
  as verified facts and checked the same lines at v6.18 (the development host runs 6.18.44): the field is `sk_uid(sk)`, the socket's
  own copy of the inode's UID that `sk_set_socket` sets and `sockfs_setattr` changes, and `sock_orphan` leaves it unchanged, so an
  orphaned socket there shows the UID it had, not root, still with inode 0.
- Is wire format 1.1 released, so that the fields would need 1.2?
  Answered: no. `git ls-remote --tags origin` shows only `v0.1.0`, released with format 1.0 (wire format area, *Versioning*), and
  record 0115, which introduced 1.1, was added after that tag; the fields extend 1.1 in place, as records 0116, 0118 and 0120 did.
- What `user` does a UID without a name get, and when is `/etc/passwd` unreadable?
  Answered: the process snapshot's rule (agent area, *Processes*, *Readers* and *Outcomes*): the decimal UID; the issue asks for "a
  user name as the process snapshot resolves it".
- Which UID does an inventory entry carry when its sockets have different UIDs?
  Answered: the lowest, with `mixed_uids` true (E2). The entry already merges sockets by endpoint and names the lowest owning PID
  (agent area, *Owners*); the kernel lets sockets of different users share an endpoint, TCP and UDP alike, when they are bound to
  different devices (`SO_BINDTODEVICE`: the bind-conflict checks `inet_bind_conflict` and `udp_lib_lport_inuse` at v6.18 skip a
  socket bound to another device before any UID comparison, and `sock_bindtoindex_locked` asks for `CAP_NET_RAW` only to change a
  binding already set), for UDP sockets that both set `SO_REUSEADDR`, and after an `fchown` of one socket of a `SO_REUSEPORT` group
  (`SO_REUSEPORT` itself requires one UID at bind time); a second user on a service's endpoint is the case that matters most, so the
  flag keeps it visible without changing the inventory's key or caps.
- Does the backend display or compare the UID?
  Answered: neither, in this change (*Out of scope*); it decodes, validates and stores the fields, and the stored copies keep them
  because the storage serializes the decoded model (storage area, `records.data`; `ListenerQueries.SerializeListeners`).

## Area documents

- [Agent](../../docs/areas/agent.md), *Connections*: *Terms* (the socket's UID and what it is: the creator's file-system UID, not the
  holder's effective user), *Tables* (the `uid` field, 1 to 10 digits within 32 bits, else the line is unreadable), *Owners* (the
  sentence "The kernel's `uid` column of the tables is not recorded (a follow-up)" replaced by the rules for `uid`, `user`,
  `mixed_uids` and inode 0, independent of the owner scan), the read of `/etc/passwd` in the collector's list of files, *Logging*
  (`user names unavailable` / `available`), *Bounded memory* and *Speed* figures measured again.
- [Wire format](../../docs/areas/wire-format.md), *`connection_snapshot`*: the fields `uid`, `user` and `mixed_uids` of listener and
  connection entries, their rules and field errors, "absent means unknown"; *Versioning*: the fields added to the 1.1 list.
- [Storage](../../docs/areas/storage.md): no change; the plan's sweep found that it names the stored `new_listeners` as "listed
  entries" and enumerates no member of a listener.
- Decision record 0115 (unreleased) is extended in place for the choices above; the plan names it.

## Challenge

The spec challenge raised no major objection and four minor ones (the *Challenge notes*), each answered here.

1. *Mixed UIDs are not UDP-only* (`SO_BINDTODEVICE` lets TCP and UDP sockets of different users share an endpoint; an `fchown` can
   split a `SO_REUSEPORT` group). **Accepted.** Checked at v6.18: `inet_bind_conflict` (`net/ipv4/inet_connection_sock.c`) and
   `udp_lib_lport_inuse` (`net/ipv4/udp.c`) skip a socket bound to another device before they compare UIDs, and
   `sock_bindtoindex_locked` (`net/core/sock.c`) asks for `CAP_NET_RAW` only to change a binding already set, so an unprivileged user
   can do it; `sockfs_setattr` (`net/socket.c`) writes the new UID into the socket, and `chown_ok` (`fs/attr.c`) needs `CAP_CHOWN` to give
   it to another user. E2 and the open question now name all three ways, and AC4 covers a generated TCP `LISTEN` pair as well as a UDP
   pair, in either order. The plan and the area document do not call mixed UIDs UDP-only.
2. *Two decoder forms undefined* (`"mixed_uids":false`, `"user":""`, `uid` without `user`). **Accepted.** E8 and AC8 now say: a
   non-empty `user` without `uid` and `mixed_uids` true without `uid` are `requires uid` (in that order, before the length of
   `user`); `"mixed_uids":false` and `"user":""` read as absent and are accepted with or without `uid`; `uid` without `user` is
   accepted (the producer always writes both; a reader that gets only the UID shows the number). The forms are pinned in
   `decoder-cases.json` for both decoders (plan, *Tests per criterion*). `requires uid` is a new kind of pairing rule; `pid` and
   `command` keep none, because a `command` without `pid` is no claim about the kernel, while a `user` without `uid` would name an
   account the kernel never stated.
3. *Where the UID is taken* (`track`/`assignOwners` drop sockets beyond the inode index or with a repeated inode). **Accepted.** S3
   and AC1 now name a socket beyond the inode index and one with a repeated inode; the plan records the UID when the table line is
   filed (`addListener`, `addConnection` in `internal/connstats/snapshot.go`), never through the inode index, and names a test for
   both cases.
4. *Fixture claims off* (no UID 117 in the captured tables; the only inode-0 line is one `TIME_WAIT`). **Accepted.** S5 lists the
   UIDs exactly (0, 30, 102, 114, 115, 116, 118, 998); AC3 names the captured `TIME_WAIT` line and requires generated lines for
   `SYN_RECV`, a `FIN_WAIT2` without a file and an orphaned `FIN_WAIT1` and `LAST_ACK`.

Spec defects found while planning, fixed above: E1 said an orphaned socket shows root; that is 5.15 (`sock_i_uid` without a socket),
while 6.x keeps the socket's own UID (`sock_orphan` leaves `sk_uid` unchanged), so E1 now names both (the inode-0 rule covers either);
S6 now names systemd's socket activation, whose sockets read UID 0; *Area documents* settles the storage area (no change).
