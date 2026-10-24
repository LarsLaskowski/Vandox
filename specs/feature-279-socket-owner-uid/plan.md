# Plan: Record the socket's UID from the kernel table in connection snapshots

Source: Issue #279 | [spec.md](spec.md)
Status: Draft
Tier: standard — a behavior change (new recorded fields, one more parsed table field, one more bounded file read) that creates, changes or removes no security control: the UID is recorded, not enforced, and no right, guard or exposure changes.
Security areas touched: 10 Parsing of external input (`internal/connstats` table reader `parseLine`/`parseUID`, the `/etc/passwd` read through `procstats.ReadUsers` in a second collector, the wire models `internal/model` `checkSocketUser` and `Vandox.Core.Model` `Listener`, `Connection`, `ConnectionSnapshot.CheckSocketUser` behind both decoders); 12 Logging and display of external data (the connection collector's new log lines; user names in the records as external text); 6 Agent privileges (no new right; `deploy/agent/README.md` lists the new read)

## Problem / root cause

See [spec.md](spec.md), *Problem*. The kernel facts behind E1 and E2, read at v5.15 and v6.18, are recorded in record 0115 (*Context*,
options 13 to 17) and the spec's *Open questions*; they are not repeated here.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The table reader splits 10 fields and never reads field 8 (`uid`); a bad inode is `errFormat` | `internal/connstats/table.go:20`, `:60-85`, `:191-201` | confirmed |
| 2 | Entries are filed per table line in `addListener`/`addConnection`; `track` drops sockets beyond `MaxSockets` and repeated inodes, `assignOwners` reaches only tracked ones | `internal/connstats/snapshot.go:312-322`, `:343-372`, `:387-397`, `:400-415` | confirmed |
| 3 | `logChanges` returns early while owners are unknown, so a line placed after that block would never be logged then | `internal/connstats/connstats.go:271-277` | confirmed |
| 4 | The passwd reader and the name rule are unexported in `procstats`; its log lines are `user names unavailable` (`err`) / `user names available` | `internal/procstats/passwd.go:28-72`, `procstats.go:101`, `:361-367`, `:507-511` | confirmed |
| 5 | v6.18 prints the field as `%5u` of `from_kuid_munged(…, sk_uid(sk))` for TCP, UDP and both IPv6 tables; time-wait `%5d` literal 0, pending the listener's UID, both inode 0 | upstream v6.18 `net/ipv4/tcp_ipv4.c:2875-2897`, `:2901-2963`, `:2965-2982`; `net/ipv4/udp.c:3407-3414`; `net/ipv6/datagram.c:1056-1069`; `net/ipv6/tcp_ipv6.c:2167-2275` | confirmed |
| 6 | `sk_uid` is copied from the socket inode (`sk_set_socket`), unchanged by `sock_orphan` (whose inode becomes 0), written by `sockfs_setattr`; the inode's UID is `current_fsuid()`; giving it to another user needs `CAP_CHOWN` | v6.18 `include/net/sock.h:2068-2077`, `:2092-2099`; `net/socket.c:600-612`, `:645`; `fs/attr.c:94-107` | confirmed |
| 7 | Sockets bound to different devices skip the UID comparison of the bind-conflict checks (TCP and UDP); an unprivileged user may set the device once | v6.18 `net/ipv4/inet_connection_sock.c:170-196`, `net/ipv4/udp.c:132-160`, `net/core/sock.c:640-657` | confirmed |
| 8 | Captured tables hold UIDs 0, 30, 102, 114, 115, 116, 118, 998; their only inode-0 line is the `TIME_WAIT` line `192.0.2.10:56518` → `198.51.100.20:443` of `tcp` (also one in the sandbox `tcp`); `testdata/etc/passwd` names only UID 0 of them | `awk` over `testdata/proc/net/*`, `testdata/connections/sandbox/*`, `cut -d: -f1,3 testdata/etc/passwd` | confirmed |
| 9 | `table_test.go` builds `socket` values as positional literals, which stop compiling when a field is added | `internal/connstats/table_test.go:86-102`, `:170-175` | confirmed |
| 10 | `pid`/`command` have no pairing rule in either model; C# texts are left out when empty through `[OmitWhenEmpty]` | `internal/model/connection.go:79-95`, `:314-319`; `src/Vandox.Core/Model/Listener.cs`, `Connection.cs`, `PayloadRegistry.cs:86-91` | confirmed |
| 11 | The storage keeps whatever the model holds: stored listener lists are `JsonSerializer.Serialize` of the decoded `Listener` objects, and `ListenerComparer` passes the snapshot's objects on | `src/Vandox.Storage/ListenerQueries.cs:81-84`, `ListenerMarkWriter.cs:111-121`, `:255-267`; `src/Vandox.Core/Reports/ListenerComparer.cs:86-93` | confirmed |
| 12 | Wire format 1.1 is unreleased; record 0115 is unreleased and may be edited in place | `git ls-remote --tags origin`: only `v0.1.0` (51c9c90, 2026-10-17); 0115 added in e2f5099 (2026-10-18) | confirmed |
| 13 | `uid` values `-1`, `-0`, `4294967296`, `1.5`, `"0"`, `1e400` are `ErrMalformed` in Go and C# alike | to verify: Tester runs the shared cases S46–S51 through both decoders in step 5; a decoder that disagrees goes to the Lead | unverified |
| 14 | Reading a table line still leaves 0 B of garbage, and the run's live peak grows only by the passwd map | to verify: Tester, step 6: `TestConnections_ManySocketsStayWithinCaps` with the fixture passwd and with a 1 MiB passwd of short lines, `BenchmarkConnections_Collect`; figures reported for the agent area and `docs/BENCHMARKS.md` | unverified |
| 15 | `connstats.TCPListeners` (MariaDB's `mariadb_exposed_listeners`) reads through `parseLine`; the MariaDB text already says a line "not in its format" leaves the metric out | `internal/connstats/table.go:276-299`, `internal/mariadb/derived.go:179`, `docs/areas/agent.md:785` | confirmed |
| 16 | The benchmark "reads no file of the host"; a collector built without `Etc` would read the host's `/etc/passwd` | `docs/BENCHMARKS.md:338`, `internal/connstats/world_test.go:66`, `:396` | confirmed |
| 17 | The shared decoder cases give `field`/`reason` for field errors; both tests require a count of `S` cases (40 today, 41 present) | `testdata/wire/decoder-cases.json` (S35, S41), `internal/wire/decode_test.go:1324`, `tests/Vandox.Core.Tests/WireContractTests.cs:434` | confirmed |

## Acceptance criteria

The feature's criteria are AC1–AC9 of [spec.md](spec.md).

## Tests per criterion

Go test names follow `docs/UNIT_TESTS.md`; the harness passes `Etc` = `testdata/etc` (or a `fstest.MapFS` passwd) so no test reads the
host's file. "Generated" lines come from `world.addSocket` with a UID argument the Tester adds.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestConnections_ListenersCarryTheSocketUID` (captured server tables: `0.0.0.0:110` owned, uid 0 → `"user":"root"`, written as 0; a uid-116 listener → `"116"`; S1, S5, S6: the entry's UID is the table's whatever the owner process's user); `TestConnections_UIDWithoutOwners` (table: probe failed, mount not learned, `fdinfo` unreadable, holder not listed (hidepid), holder beyond the PID cap, socket beyond `MaxSockets`, repeated inode: each entry has no `pid`/`command` and its line's `uid`/`user`); `TestConnections_NewListenerCarriesTheInventoryUID` (S4: uid 1001 `alice`, command `sshd`, `new_listeners[0]` equals the `listeners` entry including `mixed_uids`) | `internal/connstats/connstats_test.go` |
| AC2 | `TestConnections_ConnectionsCarryTheSocketUID` (full snapshot: `tcp6` `ESTABLISHED` uid 33 → `www-data`, owned and ownerless; a regular snapshot has no `connections`) | `internal/connstats/connstats_test.go` |
| AC3 | `TestConnections_InodeZeroEntriesCarryNoUID` (the captured `TIME_WAIT` line; generated `SYN_RECV` with the listener's uid, `FIN_WAIT2` uid 0, orphaned `FIN_WAIT1` uid 0 and `LAST_ACK` uid 1001, all inode 0; a UDP endpoint of one inode-0 and one uid-33 socket carries 33 and no `mixed_uids`; a UDP endpoint of only an inode-0 socket carries no `uid`) | `internal/connstats/connstats_test.go` |
| AC4 | `TestConnections_MixedUIDsOnOneEndpoint` (table: TCP `LISTEN` pair `0.0.0.0:8080` uids 1001 and 33, UDP pair `0.0.0.0:5353` uids 0 and 1001, each in both line orders → lowest, its user, `mixed_uids` true; two uid-116 sockets on one endpoint → no `mixed_uids`) | `internal/connstats/connstats_test.go` |
| AC5 | `TestParseLine_UID` (accepted `0`, `65534`, `4294967295`, `0000000001`; refused `4294967296`, `12345678901`, `-1`, `+1`, `1a`, `0x10` with `errFormat`); the existing `TestParseLine_*` sample tables gain the captured `uid` in every want (S5's UIDs); `TestConnections_BadUIDLineIsUnreadable` (uid `4294967296` in `tcp`: warning `socket lines not read` with `err` `connstats: net/tcp: line <n>: not in the expected format`, `aggregates_incomplete`, next run `listener_check` `none`, `inventory` `skipped`); `TestTCPListeners_RefusesABadUID` | `internal/connstats/table_test.go`, `internal/connstats/connstats_test.go` |
| AC6 | `TestConnections_UserNamesFollowTheProcessRules` (passwd variants in a `fstest.MapFS`: first line of a UID wins, invalid byte → U+FFFD, 300-byte name cut to 256 at a character boundary, control characters kept and escaped in the JSON); `TestConnections_PasswdUnavailableLogsOnce` (table: missing, open error, over 1 MiB, a line over 4 KiB → every `user` decimal, sample succeeds, one warning `user names unavailable` with `err` exactly `procstats: passwd: …` as `procstats.ReadUsers` returns it, `collector=connections`; passwd back → one info `user names available`; also logged while `socket owners unknown` holds; a late run logs nothing); `TestConnections_ReadsPasswdOncePerRun` (recording FS: one open of `passwd` per run); `TestConnections_BlockingPasswdReadIsAbandonedAsGap`; the existing `TestConnections_LogsHoldNoProcessTextOrAddress` extended: no line holds a user name or a UID value; `TestReadUsers_GivesTheProcessSnapshotsNames`; `TestUserName_DecimalForAnUnknownUID` | `internal/connstats/connstats_test.go`, `internal/procstats/passwd_test.go`, `internal/procstats/procstats_test.go` |
| AC7 | `TestConnections_LongUserNamesStillFit` (4,096 listeners and 4,096 connections, every UID named by 256 bytes of control characters: one record, `wire.CheckRecord` accepts it, `complete` false and the cut marked by the existing rules, `inventory` as `inventoryState` gives) | `internal/connstats/connstats_test.go` |
| AC8 | `TestConnectionSnapshot_Validate_SocketUser` (accepted: uid 0 with user, uid 4294967295, uid without user, `user` "" and `mixed_uids` false without uid, connection uid and user; refused, in this order: `listeners[0].user: requires uid`, `listeners[0].mixed_uids: requires uid`, same for `new_listeners[0]` and `connections[0].user`, user of 1,025 bytes `too long`); `TestEncodeBatchGolden` with `all-kinds.jsonl` regenerated (a listener with `"uid":0,"user":"root"`, a `new_listeners` entry with `"mixed_uids":true`, a connection with `uid`/`user`); `TestDecoder_NullSocketUserReadsAsAbsent`; the shared cases S42–S57 through the existing shared-case tests of both decoders; `ConnectionSnapshotValidateChecksTheSocketUser`, `ConnectionSnapshotSerializeOmitsAbsentSocketUser` (absent stays absent, uid 0 is written); `WireContractConnectionSnapshotCarriesTheSocketUid` (golden: `Uid` 0 not null, `User`, `MixedUids`) | `internal/model/connection_test.go`, `internal/wire/encode_test.go`, `internal/wire/decode_test.go`, `testdata/wire/decoder-cases.json`, `tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs`, `tests/Vandox.Core.Tests/WireContractTests.cs` |
| AC9 | `BatchWriterKeepsTheSocketUidInTheStoredPayload`; `ListenerMarkWriterKeepsTheSocketUidInStoredNewListeners` (`connection_marks` and `listener_comparisons` JSON); `SqliteStoreListenerHistoryReadsListenersWithoutUidAsUnknown` (a stored list without the fields reads `Uid` null) | `tests/Vandox.Storage.Tests/BatchWriterTests.cs`, `tests/Vandox.Storage.Tests/ListenerMarkWriterTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |

Shared cases (one listener or connection entry each, `want` as the existing S cases): S42 `user` without `uid` (Invalid
`data.listeners[0].user`, `requires uid`); S43 `mixed_uids` true without `uid` (`data.listeners[0].mixed_uids`, `requires uid`); S44 the
same as S42 in `new_listeners`; S45 the same in `connections`; S46–S51 `uid` `-1`, `4294967296`, `1.5`, `"0"`, `1e400`, `-0` (Malformed);
S52 `uid` 4294967295 with `user` (accepted); S53 `uid`, `user`, `mixed_uids` all `null` (accepted); S54 `"user":""` and
`"mixed_uids":false` without `uid` (accepted); S55 `uid` without `user` (accepted); S56 `mixed_uids` true on a connection without `uid`
(accepted, unknown key); S57 a `user` of 1,025 bytes with `uid` (`data.listeners[0].user`, `too long`). The required `S` count becomes 57
in both tests.

AC9's tests may pass as soon as the C# skeleton's properties exist: the storage has no code to change (fact 11). The Tester writes them
in step 5 and reports them as passing on the skeleton; they guard the stored copies, not new logic.

## Approach

**Model (Go and C#).** `Listener` gains `uid`, `user`, `mixed_uids`; `Connection` gains `uid`, `user`. Go: `UID *uint32` (`omitempty`, so
0 is written and absence means unknown), `User string` and `MixedUIDs bool` (`omitempty`); C#: `uint? Uid` (`WhenWritingNull`), `string User`
(`[OmitWhenEmpty]`), `bool MixedUids` (`WhenWritingDefault`). JSON order: `proto, local, pid, command, uid, user, mixed_uids` and
`proto, local, remote, state, pid, command, uid, user`. `checkSocketUser` / `CheckSocketUser` run after `checkProcess` for every entry of
`listeners`, `new_listeners` (with `mixed_uids`) and `connections` (with `false`): a non-empty `user` without `uid` → `<path>.user`
`requires uid`; `mixed_uids` true without `uid` → `<path>.mixed_uids` `requires uid`; then the short-text rule on `user`. The skeleton
does not call the new checks yet, so today's tests keep passing.

**Table reader.** `parseLine` reads field 8 with `parseUID`: 1 to 10 ASCII digits, then `strconv.ParseUint(…, 10, 32)` and `uint32(v)`
(the accepted G115 form), anything else `errFormat`, parsed as `parseInode` is so the per-line garbage stays 0 B (fact 14).

**Collector.** The UID is taken where a line is filed, never through the inode index (challenge note 3): `addConnection` stores the
line's UID with `hasUID = inode != 0`; `addListener` calls `addUID` on the new or the existing entry only when the inode is not 0. `addUID`
keeps the first UID, and on a different one keeps the lower and sets `mixedUIDs`. `listenerOf` and `connectionList` write `UID` (a copy),
`User` = `procstats.UserName(res.users, uid)` and `MixedUIDs` for entries with `hasUID`; `new_listeners` are built by `listenerOf` from
the same entry, so they equal the inventory entry. `Collect` reads `/etc/passwd` once per run after the owner scan,
`res.users, res.usersErr = procstats.ReadUsers(c.src.Etc)`; an error leaves `users` nil, so every `user` is the decimal UID and the sample
succeeds. `Sources.Etc` nil means `os.DirFS("/etc")`.

**Log lines** (the only new user-visible text besides the fields): in `logChanges`, **before** the `ownersUnknown` early return (fact 3),
per change and only for a run that finishes in time: warning `user names unavailable` with `err` = the `procstats.ReadUsers` error (`procstats:
passwd: <fixed reason or OS error>`), info `user names available`; both carry `collector=connections` from the collector's logger and no
name or UID.

**Docs.** See *Documentation updates*; the agent area's figures (*Bounded memory*, *Speed*) come from the Tester's measurement (fact 14).

## Options considered

The alternatives are recorded with their reasons in record 0115, options 13 to 19 (holder's UID from `status`; UID through the inode
index; a UID list per entry or the UID in the inventory key; the printed number for inode-0 lines; a passwd reader for the run's UIDs only
or a cache across runs; the name without the number; format 1.2). One choice of this plan alone: Go `UID *uint32` against a value with a
separate "known" flag and a custom encoder — the pointer is the model's existing form for optional numbers (`PSSBytes`, `ErrorCode`)
and needs no encoder; its cost is that `==` on a `Listener` compares the pointer, which the Tester's helpers handle.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go | `internal/model/connection.go` | fields on `Listener`, `Connection`; `checkSocketUser` |
| Go | `internal/procstats/passwd.go`, `procstats.go` | exported `ReadUsers`, `UserName` over the existing helpers |
| Go | `internal/connstats/table.go` | `socket.uid`, `parseUID`, `parseLine` |
| Go | `internal/connstats/snapshot.go` | entry fields, `addUID`, filing, `listenerOf`, `listenerList`, `connectionList`, `buildSnapshot` call sites |
| Go | `internal/connstats/connstats.go` | `Sources.Etc`, `New` default, passwd read in `Collect`, `runResult`, `logState`, `logChanges` |
| .NET | `src/Vandox.Core/Model/Listener.cs`, `Connection.cs`, `ConnectionSnapshot.cs` | properties; `CheckSocketUser`; `Validate` calls |
| shared | `testdata/wire/all-kinds.jsonl`, `testdata/wire/decoder-cases.json` | regenerated golden, cases S42–S57 (Tester) |
| docs | `docs/areas/agent.md`, `docs/areas/wire-format.md`, `deploy/agent/README.md`, `docs/BENCHMARKS.md`, `.squad/project.md` | *Documentation updates* |

## Signatures (for the Dev's skeleton)

Go, internal/model/connection.go (JSON tags as in *Approach*):

- `Listener.UID *uint32`, `Listener.User string`, `Listener.MixedUIDs bool`; `Connection.UID` and `Connection.User` alike
- `func checkSocketUser(path string, uid *uint32, user string, mixed bool) error`

Go, internal/procstats:

- `func ReadUsers(etc fs.FS) (map[uint32]string, error)` — passwd.go, the rules of readPasswd
- `func UserName(users map[uint32]string, uid uint32) string` — procstats.go, the rule of userName

Go, internal/connstats:

- `Sources.Etc fs.FS`
- `socket.uid uint32`
- `func parseUID(f []byte) (uint32, error)`
- `listenerEntry.uid uint32`, `listenerEntry.hasUID bool`, `listenerEntry.mixedUIDs bool`; `connEntry.uid uint32`, `connEntry.hasUID bool`
- `listenerEntry.addUID(uid uint32)` (method on a pointer receiver)
- `func listenerOf(e *listenerEntry, res *runResult) model.Listener` (changed: takes the run result instead of the command map)
- `collection.listenerList(res *runResult) []model.Listener` (method, changed)
- `collection.connectionList(res *runResult) []model.Connection` (method, changed)
- `runResult.users map[uint32]string`, `runResult.usersErr error`
- `logState.usersUnavailable bool`

C#, src/Vandox.Core/Model:

- `Listener.Uid` (uint?, JSON uid), `Listener.User` (string, JSON user), `Listener.MixedUids` (bool, JSON mixed_uids); `Connection.Uid` and `Connection.User` alike
- `internal static FieldError? CheckSocketUser(string path, uint? uid, string user, bool mixedUids)` — ConnectionSnapshot.cs

Existing files the skeleton rewrites: the six Go files and three C# files above (with `internal/connstats/table_test.go` for the
keyed literals); each was read for this plan, `procstats.go` and `ConnectionSnapshot.cs` in the parts the change touches.

## Test files

Named by *Layout*: `internal/connstats/connstats_test.go`, `internal/connstats/table_test.go`, `internal/connstats/world_test.go`
(harness), `internal/procstats/passwd_test.go`, `internal/procstats/procstats_test.go`, `internal/model/connection_test.go`,
`internal/wire/encode_test.go`, `internal/wire/decode_test.go`, `testdata/wire/all-kinds.jsonl`, `testdata/wire/decoder-cases.json`,
`tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs`, `tests/Vandox.Core.Tests/WireContractTests.cs`,
`tests/Vandox.Storage.Tests/BatchWriterTests.cs`, `tests/Vandox.Storage.Tests/ListenerMarkWriterTests.cs`,
`tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs`.

Existing test code affected:

- `internal/connstats/table_test.go:86-102`, `:170-175` (positional `socket{…}`): the **Dev** turns them into keyed literals in step 4,
  nothing else, so *Build* passes; the **Tester** adds the `uid` to each want in step 5 (AC5).
- `internal/connstats/connstats_test.go` helpers and wants that compare `model.Listener`/`model.Connection` with `==` or `slices.Equal`
  (`wantListener` :86-93, `wantNewListeners` :121-128, `ownedConnections` :63-68, and the wants at :255, :349, :385, :411, :449, :463, :510,
  :944, :1186): the **Tester**, step 5 — compare the UID by value and expect the fixture's UID and user.
- `newCollector` and `newRecordingCollector` (`world_test.go:65-67`, `:394-398`), `TestNew_TakesDefaults` and `BenchmarkConnections_Collect`
  (`connstats_test.go:1365`, `:1474-1483`), `internal/agent/agent_test.go:262`, `:936`: the **Tester** passes `Etc` = `testdata/etc`.
- `internal/wire/encode_test.go` `allKindRecords` (:100) and `requiredCaseIDs` (`decode_test.go:1324`), `WireContractTests.cs:434`: the
  **Tester**.

Files no unit test can cover: none.

## Areas

- [Agent](../../docs/areas/agent.md), *Connections*: *Terms* (the socket's UID: the creator's file-system UID, not the holder's; systemd's
  socket activation and root-created sockets read 0; only `CAP_CHOWN` gives a socket to another user); *Tables* (field 8, 1 to 10 digits
  within 32 bits, else unreadable); *Owners* (the sentence "The kernel's `uid` column of the tables is not recorded (a follow-up)" replaced
  by the rules of `uid`, `user`, `mixed_uids` and inode 0, independent of the owner scan, mixed UIDs by device binding, `SO_REUSEADDR` and
  `fchown`); the opening list of files read (`/etc/passwd`, once per run, the process collector's bounds); *Logging*; *Bounded memory* and
  *Speed* with the Tester's figures.
- [Wire format](../../docs/areas/wire-format.md), *`connection_snapshot`*: the three fields, their rules and field errors, absent means
  unknown, `null`/`""`/`false` read as absent; *Versioning*: the fields in the 1.1 list.
- Storage, Detection and reports, Web UI: no change (the storage area names the stored lists without their members; nothing shows the
  fields yet).

## Documentation updates

Sweep: `grep` over `docs/areas/*.md`, `docs/ARCHITECTURE.md`, `docs/BENCHMARKS.md`, `deploy/agent/README.md`, `.squad/project.md` and
`README.md` for `uid`, `passwd`, `connection collector`, `listeners` and the log-line list. Hits and owners:

| File | Edit | Owner |
| ---- | ---- | ----- |
| `docs/areas/agent.md` | *Areas* above | Lead |
| `docs/areas/wire-format.md` | *Areas* above | Lead |
| `deploy/agent/README.md` | *What the connection collector reads and what it needs*: a bullet for `/etc/passwd` (world-readable, no right, once per run) | Dev |
| `.squad/project.md` | security area 10 (the connection collector's table bounds gain the `uid` field of 1 to 10 digits within 32 bits and the `/etc/passwd` read with the process collector's bounds); area 12 (its fixed log texts gain `user names unavailable` / `user names available`, and the stored `user` is text to be escaped); area 6 (the connection collector reads `/etc/passwd`, no right) | Dev |
| `docs/BENCHMARKS.md` | *Connection snapshot*: the new figures; "reads no file of the host" stays true with `Etc` | Tester |

`docs/ARCHITECTURE.md` (line 42 stays true), `README.md` (no option), `docs/areas/storage.md`: no change.

## Architecture check

No gaps unless recorded: a bad `uid` field is a skipped line (aggregates incomplete, `listener_check` `none`), an unreadable passwd
changes only names, and `uid` absent means unknown, never root. A hanging collector never blocks the agent: the passwd read runs inside
the run under its deadline and a blocked read is abandoned as a gap (`TestConnections_BlockingPasswdReadIsAbandonedAsGap`). Backfill
is untouched. No right is added (0122 stands); the collector still opens no socket and starts no program.

## Security considerations

- [x] Limits: the `uid` field is checked as 1 to 10 raw ASCII digits before conversion; `/etc/passwd` is bounded on raw bytes (1 MiB,
  lines 4 KiB) before decoding, and a name is cut to 256 bytes **after** the U+FFFD replacement (`cleanText` bounds its output); the
  decoders measure `user` against 1,024 bytes after decoding, the Go model as counted after the encoder's replacement (0114); a name of
  256 bytes of control characters encodes to at most 1,536 bytes, inside the record's fill budget (AC7).
- [x] Exceptions reaching a user-visible text: the agent's `err` attribute holds the `procstats: passwd:` error (a fixed reason or the OS
  error, never file content); the decoders give `requires uid` / `too long` field errors and `Malformed` for a bad number (the
  `JsonException` text never reaches a user). None other.

## Decision records

- `docs/decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md`
  (Accepted, unreleased, extended in place: *Context* for #279, options 13 to 19, the decision for #279, two consequences; index row
  unchanged).

## Out of scope / follow-ups

As in the spec. Follow-up issue for the orchestrator: title "[Backend] Show the socket's UID and user in the new-listener findings",
body: "The agent's connection snapshots carry `uid`, `user` and `mixed_uids` on listener entries since #279 (record 0115). The forensics
report's *New listeners* section and the web UI show only the owner's `pid` and `command`, which the process chooses. Show the UID and
user next to them, escaped like the command, and say when the endpoint has several UIDs."

## Challenge notes

Minor objections of the plan challenge, for Dev and Tester to follow.

1. Test data for AC2 and S4 has to be generated. The captured `testdata/proc/net/tcp6` has only `LISTEN` (`0A`) lines, and `testdata/etc/passwd` names UID 33 (`www-data`) and 65534 but no UID 1001. So the AC2 `tcp6 ESTABLISHED uid 33 -> www-data` connection is a generated line (`world.addSocket` with a UID). The S4 test (`TestConnections_NewListenerCarriesTheInventoryUID`, uid 1001 -> `alice`) needs an `fstest.MapFS` passwd that names `alice`. With the default `Etc` = `testdata/etc`, the user would be `"1001"`.
2. `docs/areas/agent.md:1912` is missing from the doc edits. That line lists the `internal/procstats` exports the connection collector shares (`ListPIDs`, `CommandReader`, `Ended`). The Lead adds `ReadUsers` and `UserName` there in task 10.
3. Rename the procstats helpers instead of wrapping them. Make `ReadUsers` and `UserName` the renamed `readPasswd` and `userName`, and point the process collector's call sites (`internal/procstats/procstats.go:101`, `:348`) and `passwd_test.go` at the new names. Do not leave the unexported functions behind as one-line wrappers or as dead code; golangci-lint's `unused` would flag them and they would duplicate the rule.

## Security notes

Non-blocking findings of Security's plan review (APPROVED).

1. Memory bound. The bound is claimed but not pinned yet. A 1 MiB passwd of minimal lines (`a::0`) gives about 200,000 map entries, roughly 10 MB of live data while a run lasts. The process collector may hold its own copy at the same time. The file is root-controlled, so this is not an attack vector. The Tester's step-6 measurement (fact 14, `TestConnections_ManySocketsStayWithinCaps` with the 1 MiB short-line passwd) reports this worst case, and the agent area's *Bounded memory* figure states it.
2. Possible follow-up issue (hardening, not a defect). A local user can bind a device-bound socket (`SO_BINDTODEVICE`) on a known endpoint such as `0.0.0.0:22`. That endpoint then shows `uid` 0 with `mixed_uids` true, and it is still not reported as a new listener, as today with `pid`. A known endpoint whose `mixed_uids` turns from absent to true is a useful signal: "[Agent] Report a known listener endpoint that gains a second UID".
