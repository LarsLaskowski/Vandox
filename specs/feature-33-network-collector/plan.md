# Plan: Network connection collector (replaces `lsof -ni`)

Source: [spec.md](spec.md)
Status: Revised (2)
Tier: security — the collector adds a guard against a hostile local user (a descriptor counts as a socket only on the socket mount the
agent learns from a socket of its own, E20) and new confinement rules for the agent's unit (no own network namespace, no `ProcSubset=pid`, `ProtectProc=default`); it adds
no right (revision: `CAP_SYS_PTRACE` only, not `CAP_DAC_READ_SEARCH`).
Security areas touched: 6 Agent privileges (`deploy/agent/README.md`, the unit rules in the Agent area *Rights*), 10 Parsing of
external input (`/proc/net/{tcp,tcp6,udp,udp6}`, `/proc/<pid>/fdinfo/<n>`, the start-up socket's own `/proc/self/fdinfo/<n>`, process
names; `internal/connstats`, `internal/sockmount`), 12 Logging and display of external data (process names in records, the collector's log lines)

## Problem / root cause

Feature: see [spec.md](spec.md) (Approved by Lead).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The four tables: a header line, then per socket `sl:`, local and remote `HEX:PORT`, state (2 hex), …, inode (field 10), a varying tail; IPv4 one 8-hex word, IPv6 four, each word in host byte order, port 4 hex digits | captures of the server (all four) and the sandbox (`tcp`, `udp`), `samples/README.md`; `::1` = `00000000000000000000000001000000`, `127.0.0.1` = `0100007F` | confirmed |
| 2 | `sl` is not unique, `tcp`/`udp` lines carry trailing spaces, a TIME_WAIT line has inode 0 and fewer tail fields, a line can end in `-1` | `samples/server/{tcp,udp,udp6}` | confirmed |
| 3 | Two sockets on one endpoint occur (BIND on `:53`, `:953`) | `samples/server/tcp` lines 7–8, 17–18, 20–21; `tcp6` 7–8, 10–11, 15–16 | confirmed |
| 4 | `tcp6` and `udp6` can be missing | sandbox kernel 6.18.44: `ls /proc/net` has no `tcp6`/`udp6`; an `AF_INET6` socket fails with errno 97 | confirmed |
| 5 | An IPv4-mapped address in `tcp6` is four words like any address (`0000000000000000FFFF0000` + the IPv4 word) | to verify: residual accepted; tests derive the line from a real `tcp6` line by replacing the address field (Tester, step 5); #45 reads real ones; basis: no captured line has one, the format is per word (fact 1) | unverified |
| 6 | `/proc/net` is the reader's own network namespace | `ls -ld /proc/net` → `/proc/net -> self/net` | confirmed |
| 7 | `fdinfo/<n>` starts with `pos:`, `flags:`, `mnt_id:`, `ino:` (tab, decimal) for every descriptor type; a socket's `ino:` is its table inode; pipes share the inode counter on their own mount; a regular file is on a listed mount; an epoll repeats `ino:` (hex) in later `tfd:` lines | `samples/sandbox/fdinfo/`: sockets `mnt_id: 10`, TCP `ino: 6087` = table inode; pipe `mnt_id: 16`; file `mnt_id: 28` | confirmed |
| 8 | A socket descriptor can be in none of the four tables (Unix sockets, other namespaces) | `samples/sandbox/fd-of-capturing-process.txt` (`socket:[16078]`), `fdinfo/unix` | confirmed |
| 9 | With `CAP_SYS_PTRACE` alone, another user's `fdinfo/` lists and its files read (`mnt_id`, `ino` = socket inode); `fd/` needs `CAP_DAC_READ_SEARCH` to list and both to read a link | Security's measurement (kernel 6.18.44, no Yama, UID 65534 against a root process), quoted in *Security review* B2 | confirmed |
| 10 | With `CAP_SYS_PTRACE`, `hidepid=2` hides no process; without it other users' processes are invisible | Security's measurement (N1): scratch mount namespace, `proc` with `hidepid=2` | confirmed |
| 20 | The `ino:` line exists in `fdinfo` from Linux 5.14 on; the server runs Ubuntu 22.04 (5.15 or a newer HWE kernel) | Dev's reading of upstream `fs/proc/fd.c` at v5.13 (no `ino:`) and v5.14/v5.15 (`ino:` after `mnt_id:`), step 6 (G2, e36088d); the design fails closed (E1) | confirmed |
| 21 | Through `io/fs` over `os.DirFS` each open, read, close and `ReadDir` allocates a fixed small amount | DA's measurement, Go 1.27 | confirmed |
| 22 | A run that returns after its deadline is turned into a gap and its records are dropped | `internal/scheduler/runner.go:203-206` (result received with `wctx.Err() == DeadlineExceeded`), `:207-213` (abandoned) | confirmed |
| 11 | Kernel default local port range is 32768–60999; unprivileged ports start at 1024 | sandbox `/proc/sys/net/ipv4/ip_local_port_range` `32768 60999`, `ip_unprivileged_port_start` `1024` | confirmed |
| 12 | The scheduler tells a run whether pressure mode was on at its start; a collector is `Name()` + `Collect(ctx, Run)` | `internal/scheduler/scheduler.go` (`Run.Pressure`, `Collector`) | confirmed |
| 13 | The process command derivation and PID listing exist and are reusable | `internal/procstats/names.go` (`commandFor`), `procstats.go` `listPIDs`, `reader.stat`/`cmdline` | confirmed |
| 23 | Every `Options` of the agent tests names fixture sources, so no agent test reads the host's `/proc` | `internal/agent/agent_test.go:242` (comment), `Options{` at 281, 342, 371, 417, 478, 492, `lowMemoryOptions` 576–589 | confirmed |
| 14 | The incident's connection peak takes every source; the chart only the earliest source | `IncidentReconstructor.cs` ~281–292; `IncidentWindowReader.cs` `FindSourceAsync` | confirmed |
| 15 | Wire 1.0 is released (v0.1.0); both decoders accept a newer minor and ignore unknown keys | `git ls-remote --tags origin` → `v0.1.0`; wire format *Versioning*; `decode_test.go:186` (minor 7 accepted) | confirmed |
| 16 | No released backend receives agent batches (no ingest before #40) | ARCHITECTURE (*Network*: ingest port not published until #40) | confirmed |
| 17 | A run's live data is bounded by the caps (largest structures: the inode index of 65,536 sockets with two `int32` each for owner and last counting PID, two heaps of 4,096 entries, 4,096 remote counts; target under 16 MiB); a table line adds no garbage that grows (target under 16 bytes); a descriptor adds a fixed amount (target under 1 KiB) | Dev's measurement, step 6 (G3, 26492de; Go 1.27, Xeon 2.1 GHz, in-memory `fstest`), `TestConnections_ManySocketsStayWithinCaps`: live data 3.04 → 3.18 MB from 100,000 to 400,000 lines and 0.71 MB at 50,000 and 200,000 descriptors, peak with the inode index alive about 5.5 MB; garbage 0 B per extra line, about 170 B per extra descriptor (the test's generated FS included); a run that reaches the caps allocates a fixed about 24.7 MB more, reclaimed by the GC (about 9 MB of it the growth of the 65,536-entry index map). Lead `decide`: accepted as is, stated in the Agent area (peak live figure and the fixed per-capped-run garbage) | confirmed |
| 18 | A run over a server-sized `/proc` takes milliseconds | Dev's measurement, step 6 (G3, 26492de): `BenchmarkConnections_Collect` on the captured server tables with 300 processes × 30 descriptors, 50.7 ms/op, 1.99 MB/op, 26,307 allocs/op (Go 1.27, Xeon 2.1 GHz, in-memory `fstest`); in `docs/BENCHMARKS.md` | confirmed |
| 24 | Every socket of any family (Unix datagram, TCP, UDP, netlink), in any mount, user or network namespace, has the same `mnt_id:` in `fdinfo`; a pipe's differs | Lead's measurement 2026-10-18, kernel 6.18.44: `mnt_id: 10` for all four families in the initial namespaces, after `unshare -m`, and as UID 65534 after `unshare -Urm` and `unshare -Urmn`; pipe `mnt_id: 16` | confirmed |
| 25 | Mount IDs in `fdinfo` are global: a file on another namespace's mount shows that mount's own ID, not the socket mount's | Security's measurement (B3): a user-namespace tmpfs file showed `mnt_id: 76`, the host's `mountinfo` lists 23–48 | confirmed |
| 26 | On the server's kernel (Ubuntu 22.04, 5.15 or HWE) every socket file is allocated on the one internal mount `sock_mnt` (`net/socket.c`, `sock_alloc_file`), which holds only sockets and no user can mount; socket inode numbers come from `get_next_ino()` (32 bits, shared with pipes and other pseudo files) | Dev's reading of upstream v5.15 `net/socket.c` and `fs/inode.c`, step 6 (G2, e36088d); Ubuntu's 5.15 is built on it; facts 24–25 on 6.18 | confirmed |
| 19 | Adding `testdata/proc/net/` does not disturb the existing `/proc` fixture tests (procstats lists `testdata/proc`; `net` is no canonical PID) | to verify: Tester, step 5, full `go test ./...` after copying the fixtures; basis: `procstats.go` `pidOfEntry` | unverified |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestConnections_RegularSnapshotHoldsAggregatesAndInventory`, `TestConnections_ListOrders`, `TestConnections_ReusePortEndpointIsOneListener`, `TestConnections_MappedAddressesKept`, `TestConnections_RegularIsNeverComplete` | `internal/connstats/connstats_test.go` |
| AC1 | `TestParseLine_ServerSamples`, `TestParseLine_SandboxSamples`, `TestParseLine_Rejects` | `internal/connstats/table_test.go` |
| AC2 | `TestConnections_FullSnapshotOnFirstRun`, `TestConnections_FullSnapshotUnderPressure`, `TestConnections_FullSnapshotOnNewListener` | `internal/connstats/connstats_test.go` |
| AC2 | `TestJobs_Schedules` (adapted: `connections` 60 s / 15 s / 10 s after `processes`; every `Options` gets `fixtureConnections()`) | `internal/agent/agent_test.go` |
| AC3 | `TestCompare_NewListenerReported`, `TestCompare_BaselineReportsNone`, `TestCompare_EphemeralUDPNotReported`, `TestCompare_TCPOnEphemeralPortReported`, `TestCompare_ReappearingListenerIsNewAgain`, `TestCompare_OwnerChangeIsNotNew` | `internal/connstats/inventory_test.go` |
| AC3 | `TestConnections_FailedRunIsNotCompared`, `TestConnections_LateRunKeepsNoInventory` (the context ends during the last PID's `fdinfo` read, the read is then released; `Collect` returns the context's error, and the next run still reports the listener), `TestConnections_ReportsAtMost64NewListeners`, `TestConnections_SizeCutOfListenersKeepsTheComparison` | `internal/connstats/connstats_test.go` |
| AC4 | `TestParseFdinfo_Samples` (every file of `testdata/connections/sandbox/fdinfo/`, the epoll's `tfd:` lines ignored), `TestParseFdinfo_Rejects`, `TestScanOwners_LowestPIDOwnsSharedSocket`, `TestScanOwners_CountsDistinctSocketsPerProcess`, `TestScanOwners_IgnoresSocketsOutsideTables`, `TestScanOwners_EndedProcessIsSkipped`, `TestScanOwners_ClosedDescriptorKeepsTheOtherSockets`, `TestScanOwners_UnreadableProcessCountedOnce`, `TestScanOwners_DescriptorOffTheSocketMountIsNotAnOwner` (an `ino:` equal to a listener's on mount 76, held by the lowest PID through 10,000 descriptors: no owner, not in `processes`; the real holder owns it), `TestScanOwners_DuplicatedDescriptorsCountOnce`, `TestScanOwners_ManyDescriptorsOfALowPIDStarveNoOne` (PID 1 holds 300,000 descriptors of one socket; a higher PID's socket keeps its owner) | `internal/connstats/owners_test.go` |
| AC4 | `TestProbe_ReturnsTheSocketMount` (equal to the `mnt_id:` of a `syscall.Socketpair` descriptor read through `os.DirFS("/proc")`, unequal to a pipe's) | `internal/sockmount/sockmount_test.go` |
| AC4 | `TestConnections_OwnerlessSockets`, `TestConnections_InodeZeroLinesAreSeparateSockets` | `internal/connstats/connstats_test.go` |
| AC5 | `TestConnections_UnreadableDescriptorsLogOnce`, `TestConnections_FdinfoWithoutInoIsUnreadable` (measurement: facts 9 and 10, Security's, recorded by the Dev), `TestConnections_UnknownSocketMountLeavesNoOwners` (no `fdinfo` opened, no owner, `aggregates_incomplete`, one warning over two runs) | `internal/connstats/connstats_test.go` |
| AC5 | `TestProbe_UnreadableFdinfoFails`, `TestProbe_MalformedFdinfoFails`, `TestProbe_InodeMismatchFails` (a well-formed `fdinfo` whose `ino:` is not the socket's: error, socket closed), `TestProbe_ClosesTheSocket` (entries of `/proc/self/fd` equal before and after, also on failure), `TestSockmount_ImportsOnlyAllowedPackages` | `internal/sockmount/sockmount_test.go` |
| AC5 | `TestConnectionSources_ProbesOnlyWhenUnset` | `internal/agent/agent_test.go` |
| AC6 | `TestConnections_MissingIPv6TablesLeaveProtocolsOut`, `TestConnections_AllTablesMissingFails`, `TestConnections_UnreadableLineLeftOut`, `TestConnections_TableReadErrorFails` | `internal/connstats/connstats_test.go` |
| AC7 | `TestConnections_ManySocketsStayWithinCaps` (two sizes per axis, fact 17), `TestConnections_LargeRecordIsReduced`, `TestConnections_SocketsBeyondInodeCapUnattributed`, `TestConnections_ReusePortDuplicatesDoNotCutTheInventory`, `TestConnections_RemotesBeyondCapNotCounted` | `internal/connstats/connstats_test.go` |
| AC8 | `TestConnections_BlockingReadIsAbandonedAsGap` (blocks `net/tcp` and, separately, one `fdinfo` file) | `internal/connstats/connstats_test.go` |
| AC9 | `TestConnections_HostileNameYieldsValidRecord`, `TestConnections_LogsHoldNoProcessTextOrAddress` | `internal/connstats/connstats_test.go` |
| AC9 | `TestCommandReader_MatchesProcessSnapshotNames`, `TestListPIDs_MatchesTheProcessCollector` | `internal/procstats/command_test.go` |
| AC10 | `TestConnectionSnapshot_Validate_NewFields`, `TestConnectionSnapshot_Validate_NewFieldRules` | `internal/model/connection_test.go` |
| AC10 | `TestEncodeBatchGolden` (adapted: the connection record carries every new field, a mapped remote address) | `internal/wire/golden_test.go`, `internal/wire/encode_test.go`, `testdata/wire/all-kinds.jsonl` |
| AC10 | `TestNewHeader…`/version test adapted to 1.1 | `internal/wire/wire_test.go` |
| AC10 | `TestConnections_RecordPassesWireCheck` | `internal/connstats/connstats_test.go` |
| AC10 | `WireContractConnectionSnapshotCarriesTheNewFields` | `tests/Vandox.Core.Tests/WireContractTests.cs` |
| AC10 | `ConnectionSnapshotValidateAcceptsTheNewFields`, `ConnectionSnapshotValidateRefusesBrokenFieldRules`, `ConnectionSnapshotSerializeOmitsAbsentNewFields` | `tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs` |
| AC10 | `SqliteStoreWriteAgentConnectionSnapshotKeepsTheNewFields` | `tests/Vandox.Storage.Tests/SqliteStoreWriteTests.cs` |
| AC10 | `IncidentBuilderConnectionPeakCountsAnAggregatesOnlySnapshot` | `tests/Vandox.Core.Tests/IncidentBuilderTests.cs` |
| AC11 | `TestConnstats_ImportsOnlyAllowedPackages` (imports incl. `os` and `math`; selectors pinned as in procstats: `os` only `DirFS`, no `syscall` selector beyond what the production files use — Lead `decide`, step 6, G3), `TestConnections_ReadsOnlyNamedFiles` | `internal/connstats/connstats_test.go` |
| AC12 | `TestCompare_CutInventoryComparesPrivilegedOnly`, `TestCompare_SkippedLineOrChangedTablesComparesNothing`, `TestCompare_CutPrivilegedPartComparesNothing` | `internal/connstats/inventory_test.go` |
| AC12 | `TestConnections_ListenerFloodCausesNoFullSnapshot` (4,100 unprivileged listeners held across runs: no report, regular snapshot, `listener_check` `privileged`; a new listener on port 2222 is not reported, one on port 23 is) | `internal/connstats/connstats_test.go` |

Fixtures the tests read: `testdata/proc/net/{tcp,tcp6,udp,udp6}` (the anonymized server capture) and `testdata/connections/sandbox/{tcp,udp}`,
copied byte for byte from `samples/` (check with `cmp`). Collector tests build an `fstest.MapFS` with those bytes under `net/…`, PID
directories with `stat`, `cmdline` and `fdinfo/<n>` files made from the captured `fdinfo` samples with only the `ino:` and `mnt_id:`
values changed, and `Sources.SocketMount` set to the samples' socket mount (`ID: 10`); generated tables and descriptor sets for the cap tests are
written by a helper from a real line by replacing fields, never typed by hand. A `faultFS`-style wrapper (error at open, read or
`ReadDir` of a name) and a blocking wrapper (blocks the open or read of one name until released in `t.Cleanup`) serve E1, E4 and AC8.
`testdata/connections/sandbox/fdinfo/` is copied from `samples/sandbox/fdinfo/`.

## Approach

New package `internal/connstats`, collector `connections`, source `connections`. One run:

1. **Tables** (`table.go`): for `tcp`, `tcp6`, `udp`, `udp6` in that order, open `net/<name>`; `fs.ErrNotExist` marks the table missing
   (E2); any other open or read error fails the sample (E4); all four missing fails it with `connstats: no socket table found`. Read
   with a `bufio.Reader` line by line, a line longer than 512 bytes is skipped to its end and counted unreadable; the first line is the
   header and is not parsed. A line is split at runs of spaces; it needs at least 10 fields: `sl` (digits and `:`), local and remote
   (exactly 8 or 32 upper-case hex digits by table, `:`, exactly 4 upper-case hex digits), state (2 upper-case hex digits), and the
   inode (field 10, 1 to 20 decimal digits within 64 bits); fields 5–9 and the tail are not interpreted. Each address word is parsed as
   a `uint32` and written with `binary.NativeEndian` (the kernel's host order), giving `netip.AddrFrom4`/`AddrFrom16`; mapped and
   link-local addresses are kept as they are (no `Unmap`, no zone). TCP states `01`–`0C` map to the model's twelve names; any other
   code is an unreadable line; for UDP the state is checked as hex and dropped. Unreadable lines are counted with the first error
   (`connstats: net/<table>: line <n>: <reason>`, reasons `not in the expected format`, `unknown state`, `line too long`).
2. **Classification per socket:** TCP `LISTEN` and UDP with remote `0.0.0.0:0`/`[::]:0` are listeners; a socket with a non-zero remote
   address or port is a connection; others (TCP without remote, not listening) count in `states` only. `states` counts every socket
   (UDP without state). Per run the collector keeps, each bounded: the inode index of at most `MaxSockets` (65,536) sockets with an inode
   other than 0 (further ones: no owner, `aggregates_incomplete`), each entry naming its listener slot or connection slot; a bounded
   max-heap of the 4,096 lowest **distinct** listener endpoints in inventory order (port 1–1023 first, then protocol, address, port),
   deduplicated through a map from endpoint to slot, so `SO_REUSEPORT` sockets share one slot and never count toward the cut (an evicted
   slot's sockets lose their slot reference in the index); a bounded max-heap of the 4,096
   lowest connections (protocol, local, remote); counts per remote address for at most `MaxRemotes` (4,096) addresses (further ones
   are not counted and `complete` is false; a regular snapshot's top 25 can then miss a heavy address first seen after the cap, which
   the Agent and wire-format areas state and `TestConnections_RemotesBeyondCapNotCounted` pins).
3. **Owners** (`owners.go`, revised for B2 and B3): the socket mount comes with the sources (`Sources.SocketMount`, learned once at the
   agent's start, item 9); when its `Err` is set the collector opens no `fdinfo`, every socket stays ownerless, `aggregates_incomplete`
   is set and E21's warning `socket owners unknown` (`err`) is logged once. Otherwise `procstats.ListPIDs` (lowest 4,096 PIDs ascending;
   more: `aggregates_incomplete`); a failed listing fails the sample. Per PID, `ReadDir` of `<pid>/fdinfo` in chunks of 256, and per
   entry open, read at most 256 bytes and close `<pid>/fdinfo/<n>`; `ParseFdinfo` reads only the first four lines, which must be `pos:`,
   `flags:`, `mnt_id:` (1–10 digits within `int32`, not 0) and `ino:` (1–20 digits within 64 bits), each a tab and digits (an epoll's
   later `tfd:` lines are not read). A descriptor counts only when its `ino:` is in the inode index **and** its `mnt_id:` equals the
   socket mount (E20). Per tracked socket two `int32`: its owner, the first PID that holds it (PIDs ascend, so the lowest, E9), and the
   last PID that counted it, so a PID's duplicated descriptors of one socket count once; `perPID` (at most 4,096 PIDs) counts each PID's
   distinct sockets. No list of matches is kept, so no PID's descriptor count can crowd out another's owners (B3). `ENOENT`/`ESRCH` when
   opening `<pid>/fdinfo` skip the PID silently; `ENOENT` on one `fdinfo/<n>` skips that descriptor (closed meanwhile); the first
   `EACCES`/`EPERM` or any other error, or a file not in the four-line shape (no `ino:` before Linux 5.14), counts the PID unreadable once
   and stops its scan (E1, `aggregates_incomplete`). `ctx` is checked between PIDs and between chunks.
4. **Commands:** `procstats.CommandReader` for every PID with a socket; an ended process is dropped from `processes` and owns nothing;
   another error also drops it and counts it unreadable.
5. **Inventory check** (`inventory.go`, E11–E14, E19): the collector keeps the previous successful run's inventory in memory (the at most
   4,096 keys of the heap, whether it was cut, the set of tables read, whether a line was skipped) — not the list as the record's size
   budget may cut it; a size cut sets `aggregates_incomplete` but leaves the comparison alone. No previous (first run) → `none`, nothing new; a skipped line in either run
   or a different set of tables → `none`; either inventory cut at 4,096 → `privileged`, comparing only port 1–1023 keys, or `none` when
   the privileged part itself was cut (the kept list holds only privileged keys and was cut); else `all`. New keys are reported in
   inventory order except UDP keys with a port in 32768–60999; at most 64 go into `new_listeners`, the rest into
   `new_listeners_omitted`. The current inventory then becomes the previous one (also after a `none` check), and the log state is
   updated, **only when `ctx.Err()` is nil immediately before `Collect` returns its record**; otherwise `Collect` returns `ctx.Err()`
   and keeps nothing (M1). A failed run changes nothing. Residual, stated in the Agent area: a deadline that passes between that check
   and the scheduler's receipt still keeps the inventory of a run that becomes a gap.
6. **Snapshot** (`snapshot.go`): full when `run.Pressure`, the first run, or at least one new listener (reported or omitted); regular
   otherwise (`aggregates_only` true, no `connections`, `remotes` the top 25 by count descending then address). Lists are ordered as S1
   and S4. Filled entry by entry within the payload budget of 1 MiB less 8 KiB of JSON (each entry encoded alone to measure it) in this
   order: `states`, `new_listeners`, `listeners`, `processes`, `remotes`, `connections`; at the first entry that does not fit, that list and
   every later one stop (a cut in `states`, `listeners` or `processes` sets `aggregates_incomplete`). `complete` is true only for a full
   snapshot with nothing cut, skipped, unattributed beyond a cap, unread or uncounted. The record is checked with `wire.CheckRecord`; as a safety
   net a record still too long is shortened by halving `connections`, then `remotes`, `processes`, `listeners`.
7. **Logs** (`collector=connections`, once per change, no process text, no address): the lines of spec E1, E2, E3 and E21 with the
   attributes named there, plus the info `socket tables available` when E2's tables return.
8. **Wire** (`internal/model`, `internal/wire`, `Vandox.Core`): `connection_snapshot` gains `aggregates_only`, `aggregates_incomplete`,
   `listener_check` (`all`, `privileged`, `none`), `new_listeners` (listener entries, at most `MaxItems`), `new_listeners_omitted`, all
   omitted when zero; rules in both languages: `aggregates_only` requires `complete` false and no `connections`; `aggregates_incomplete`
   requires `complete` false; `new_listeners` or `new_listeners_omitted` require `listener_check` `all` or `privileged`. Field errors:
   `data.complete`: `must be false for an aggregates-only snapshot` / `must be false when aggregates are incomplete`;
   `data.connections`: `must be empty in an aggregates-only snapshot`; `data.listener_check`: one-of error, and `must be all or
   privileged when new listeners are reported`. Format version 1.1 (`wire.MinorVersion`, `WireFormat.MinorVersion`).
9. **Agent and probe:** job `connections` after `processes` (60 s, pressure 15 s, timeout 10 s), `Options.Connections`;
   `connectionSources` fills `SocketMount` once at start, only when it is zero (tests pass the fixtures' `mnt_id: 10`), from
   `sockmount.Probe(os.DirFS("/proc"))`: `syscall.Socket(AF_UNIX, SOCK_DGRAM|SOCK_CLOEXEC, 0)` — never bound, connected or sent on —,
   open `self/fdinfo/<fd>`, read at most 256 bytes, check that the parsed `ino:` equals `syscall.Fstat(fd).Ino` (else the error
   `sockmount: self/fdinfo: inode mismatch`, failing closed; Security's note, Lead `decide` in step 6), close file and socket on every
   path, return `connstats.ParseFdinfo(…).MountID`.
   Errors: `sockmount: socket: <errno>`, `sockmount: self/fdinfo: <os error>`, `sockmount: <fixed parse reason>`.
   `TestSockmount_ImportsOnlyAllowedPackages` pins the `syscall` selectors (`Socket`, `Close`, `Fstat`, `Stat_t`, `AF_UNIX`, `SOCK_DGRAM`,
   `SOCK_CLOEXEC`).

## Options considered

See record 0115 (*Options considered*), options 1–12. Weighed in this plan's revisions: owners from `/proc/<pid>/fd` links (rejected in
revision 1: needs `CAP_DAC_READ_SEARCH`, the right to read every file; offered again after B3 and declined by the Product Manager); the
sockets' mount as the majority among mounts off the agent's mount table (revision 1, rejected after B3: outvoted by duplicated
descriptors, by filling the match cap from a low PID, or from any foreign mount); the mount learned from a socket of the agent's own
(chosen, Product Manager option A); and **sharing one `/proc` pass with the process collector** — fewer listings, but it couples two
collectors' deadlines and gaps (0029); rejected.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/connstats/{connstats,table,owners,inventory,snapshot}.go` | new collector |
| Go agent | `internal/sockmount/sockmount.go` | new: the start-up probe of the socket mount |
| Go agent | `internal/procstats/command.go` | new: `ListPIDs`, `CommandReader`, `Ended` over the existing helpers |
| Go agent | `internal/model/connection.go` | new fields, `ListenerCheck`, rules |
| Go agent | `internal/wire/wire.go` | `MinorVersion = 1` |
| Go agent | `internal/agent/agent.go` | schedule constants, `Options.Connections`, job, `connectionSources` |
| .NET | `src/Vandox.Core/Model/ConnectionSnapshot.cs` | new properties and rules |
| .NET | `src/Vandox.Core/Wire/WireFormat.cs` | `MinorVersion = 1` |
| test data | `testdata/proc/net/*`, `testdata/connections/sandbox/*`, `testdata/wire/all-kinds.jsonl` | new fixtures; golden regenerated |

## Signatures (for the Dev's skeleton)

```go
// internal/procstats/command.go
func ListPIDs(proc fs.FS) (pids []int32, seen int, err error) // = listPIDs
type CommandReader struct{ /* *reader */ }
func NewCommandReader(proc fs.FS) *CommandReader
func (c *CommandReader) Command(pid int32) (string, error) // commandFor(cmdline, comm); not safe for concurrent use
func Ended(err error) bool                                    // = hasEnded (ENOENT, ESRCH)

// internal/model/connection.go
type ListenerCheck string
const (
	ListenerCheckAll        ListenerCheck = "all"
	ListenerCheckPrivileged ListenerCheck = "privileged"
	ListenerCheckNone       ListenerCheck = "none"
)
type ConnectionSnapshot struct {
	Complete             bool                 `json:"complete"`
	AggregatesOnly       bool                 `json:"aggregates_only,omitempty"`
	AggregatesIncomplete bool                 `json:"aggregates_incomplete,omitempty"`
	ListenerCheck        ListenerCheck        `json:"listener_check,omitempty"`
	States               []StateCount         `json:"states,omitempty"`
	Processes            []ProcessConnections `json:"processes,omitempty"`
	Remotes              []RemoteCount        `json:"remotes,omitempty"`
	Listeners            []Listener           `json:"listeners,omitempty"`
	Connections          []Connection         `json:"connections,omitempty"`
	NewListeners         []Listener           `json:"new_listeners,omitempty"`
	NewListenersOmitted  uint32               `json:"new_listeners_omitted,omitempty"`
}

// internal/connstats/connstats.go
const (
	Name, Source    = "connections", "connections"
	TopRemotes      = 25
	MaxSockets      = 65536  // sockets with an inode tracked for owners per run
	MaxRemotes      = 4096  // remote addresses counted per run
	MaxNewListeners = 64
	PrivilegedPorts = 1024 // ports below are privileged
	EphemeralFirst  = 32768
	EphemeralLast   = 60999
)
type SocketMount struct{ ID int32; Err error } // learned at the agent's start; zero value: not probed
type Sources struct{ Proc fs.FS; SocketMount SocketMount } // Proc default os.DirFS("/proc")
type Connections struct{ /* src, log, mu, prev *inventory, state warnings */ }
func New(src Sources, log *slog.Logger) *Connections
func (c *Connections) Name() string
func (c *Connections) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)

// table.go
type table struct{ name string; proto model.Proto; words int } // words: 1 (IPv4) or 4 (IPv6)
type socket struct{ proto model.Proto; local, remote netip.AddrPort; state string; inode uint64 }
func parseLine(line []byte, t table) (socket, error)
func readTable(proc fs.FS, t table, visit func(socket), bad func(line int, err error)) (missing bool, err error)

// owners.go
type Fdinfo struct{ MountID int32; Inode uint64 }
func ParseFdinfo(data []byte) (Fdinfo, error) // the first four lines only; exported for internal/sockmount
type owners struct{ owner, last []int32; perPID map[int32]uint32 } // per tracked socket: owner PID (0: none), last PID that counted it
func scanOwners(ctx context.Context, proc fs.FS, pids []int32, index map[uint64]int32, mount int32) (o owners, unreadable int, firstErr error)

// internal/sockmount/sockmount.go
func Probe(proc fs.FS) (int32, error) // opens, reads self/fdinfo/<fd> of, and closes one unbound AF_UNIX datagram socket

// inventory.go
type listenerKey struct{ proto model.Proto; addr netip.Addr; port uint16 }
type inventory struct{ keys []listenerKey; cut, skipped bool; tables uint8 }
func lessListener(a, b listenerKey) bool
func compare(prev, cur *inventory) (check model.ListenerCheck, added []listenerKey)
func reportable(k listenerKey) bool

// snapshot.go
func fill(snap *model.ConnectionSnapshot, lists *lists, budget int) (cutAggregates, cut bool)
func fitRecord(start time.Time, snap *model.ConnectionSnapshot) (model.Record, error)

// internal/agent/agent.go
const (
	ConnectionsInterval         = 60 * time.Second
	ConnectionsPressureInterval = 15 * time.Second
	ConnectionsTimeout          = 10 * time.Second
)
// Options gains: Connections connstats.Sources
func connectionSources(src connstats.Sources, probe func() (int32, error)) connstats.Sources // probes only when src.SocketMount is zero
```

```csharp
// ConnectionSnapshot.cs, new properties (#region Properties), each with XML docs:
[JsonPropertyName("aggregates_only")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AggregatesOnly { get; set; }
[JsonPropertyName("aggregates_incomplete")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AggregatesIncomplete { get; set; }
[JsonPropertyName("listener_check")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ListenerCheck { get => _listenerCheck; set => _listenerCheck = string.IsNullOrEmpty(value) ? null : value; }
// (backing field _listenerCheck in #region Fields; a JSON null reads as "" through NullAsAbsent and an empty value as absent, as Go's
// omitempty writes it, so null is the only absent state and nothing writes "listener_check":"")
[JsonPropertyName("new_listeners")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<Listener>? NewListeners { get; set; }
[JsonPropertyName("new_listeners_omitted")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public uint NewListenersOmitted { get; set; }
// WireFormat.MinorVersion = 1
```

Decided after the skeleton (Lead, `decide`): `ListenerCheck` stays `string?` — a `string` with `string.Empty` would write
`"listener_check":""` into every serialized snapshot (`NullAsAbsentConverterFactoryKeepsWrittenJson`) — with the setter above (Dev).
The test helper `FindNull` in `tests/Vandox.Core.Tests/BatchDecoderTests.cs` checks only strings declared non-nullable
(`NullabilityInfoContext`, read state `Nullable` skipped), as it already lets nullable lists and values be null; the Tester adapts it and
adds a `BatchDecoderAcceptsNullOfOptionalPayloadField` row `{"complete":true,"listener_check":null}` → `{"complete":true}` (AC10).

Files the skeleton rewrites: `internal/model/connection.go`, `internal/wire/wire.go`, `internal/agent/agent.go`,
`src/Vandox.Core/Model/ConnectionSnapshot.cs`, `src/Vandox.Core/Wire/WireFormat.cs` (read; final apart from these members).

## Test files

`internal/connstats/{connstats,table,owners,inventory}_test.go`, `internal/sockmount/sockmount_test.go`, `internal/procstats/command_test.go`, `internal/model/connection_test.go`,
`internal/wire/{golden,encode,wire}_test.go`, `internal/agent/agent_test.go`, `tests/Vandox.Core.Tests/{ConnectionSnapshotTests,
WireContractTests,IncidentBuilderTests}.cs`, `tests/Vandox.Storage.Tests/SqliteStoreWriteTests.cs`; the benchmark
`BenchmarkConnections_Collect` in `internal/connstats/connstats_test.go` (Tester writes, Dev runs).

Existing call sites a change affects (Tester adapts): `internal/wire/wire_test.go:104-105, 116-117` (version 1.0 → 1.1),
`allKindRecords` in `internal/wire/encode_test.go` and the regenerated `testdata/wire/all-kinds.jsonl` (`VANDOX_UPDATE_GOLDEN=1`), the
golden assertions of `WireContractTests.cs`, `TestJobs_Schedules` in `internal/agent/agent_test.go`, and every `Options` of
`internal/agent/agent_test.go` (lines 281, 342, 371, 417, 478, 492, `lowMemoryOptions` at 589): a new helper `fixtureConnections()`
reading `../../testdata/proc`, and the in-memory `/proc` of `lowMemoryOptions` for that test, so no agent test reads the host's `/proc`.

Code a unit test cannot cover: the error return of `syscall.Socket` in `internal/sockmount/sockmount.go` (a test cannot make an unbound
`AF_UNIX` socket fail without a system-call filter); the rest of the file is covered.
Also `internal/agent/agent.go` 199–200, `probeSocketMount` (`sockmount.Probe(os.DirFS("/proc"))`): a test would open a real socket
against the host's `/proc`, which agent tests must not read; `Probe` itself is covered with real sockets in `sockmount_test.go`
(Lead `decide`, step 6, G3).

## Areas

- [Agent](../../docs/areas/agent.md) (Dev): new section *Connections* after *Processes* with everything of Approach 1–7 (reads, caps,
  regular/full, inventory order and check, the 64 cap, ephemeral UDP, logs, errors, bounded memory figure, speed), the residuals
  (E1 `hidepid=2`, E6 a stalled collector suppresses the check, E14 with #278, E19 flood, E20 inode counter wrap and PID cap, E21 probe failure and its warning, passive FTP noise,
  mapped addresses split in `remotes`, a regular snapshot's top 25 without the addresses beyond the remote cap, the late-return window
  of Approach 5, the owner's `command` is process-chosen text while its PID is the kernel's, ownerless sockets vs `lsof -ni` for #45),
  the live-data figure and the per-descriptor garbage figure (fact 17), the kernel version of `ino:` (fact 20); *Rights*: the connection
  collector uses `CAP_SYS_PTRACE` only (`fdinfo`), with Security's measurement, and why not `CAP_DAC_READ_SEARCH` (it would let the agent
  read every file); the start-up socket (Unix datagram, never bound or connected, no right) and its measurement (facts 24–26); the unit
  rules (no own network
  namespace — no `PrivateNetwork=`, no `NetworkNamespacePath=` —, no `ProcSubset=pid`, `ProtectProc=default`, `AF_UNIX` sockets allowed); *Status*, *Related
  decisions* (0115), *Implementation*.
- [Wire format](../../docs/areas/wire-format.md) (Dev): *`connection_snapshot`* (the five fields, their rules and absent meaning; the top
  `remotes` of a regular snapshot are over the counted addresses only),
  *Producer mapping for connection endpoints* (from the kernel tables: host-order words, no zone, mapped kept), *Versioning* (current
  1.1, what 1.1 added).
- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md) (Lead, done in this step): connection peak over every source.

## Documentation updates

- `docs/areas/agent.md`, `docs/areas/wire-format.md` — Dev (above).
- `deploy/agent/README.md` — Dev: `CAP_SYS_PTRACE` also for `connections` (reading another user's `/proc/<pid>/fdinfo`), the
  measurement table of *Security review* B2, that `CAP_DAC_READ_SEARCH` is not needed by any collector so far, and the unit settings
  that must stay off.
- `docs/BENCHMARKS.md` — Dev: *Connection snapshot* (figure, host, command).
- `.squad/project.md` — Dev: area 6 (the connection collector uses `CAP_SYS_PTRACE` only; the start-up socket needs no right and the
  unit's filters must allow `AF_UNIX`), area 10 (the table and `fdinfo` readers' bounds, the start-up probe and the mount rule), *Test doubles* `/proc` row (connstats fixtures and wrappers, implemented).
- `.squad/stack.md` *Layout* — Dev: `connstats` and `sockmount` in the package list.
- `docs/ARCHITECTURE.md` and `docs/areas/detection-and-reports.md` — Lead, done (status, components, `internal/` list with
  `internal/sockmount`, records; peak over every source).
- Sweep: `grep -rn "procstats\|connection snapshot\|connection_snapshot" docs .squad/project.md .squad/stack.md` — hits are the rows
  above; `docs/areas/log-import.md` (legacy parser) and `web-ui.md` (chart) need no change.

## Architecture check

Outbound-only and "never contacts Telegram": the collector opens no socket (reads files only; AC11 pins imports); the agent's start-up
probe opens one Unix datagram socket that is never bound, connected or sent on (no network; its imports pinned). Hanging collector
(0029): runs under the scheduler's deadline, tested with a blocking `fdinfo` read and table read (AC8). Gaps (0028): failures are gaps by the
scheduler. Rights (0030): no new capability; `CAP_SYS_PTRACE` gets a second user. Memory on the 2 GB server: every structure has a fixed
cap and the live data is measured; garbage per descriptor is stated (fact 17). Wire contract (0075): the golden batch pins the new
fields on both sides. No guarantee is weakened.

## Security considerations

- [x] Limits on parsed input, all on raw bytes before any decoding: table line 512 bytes; address fields exactly 8/32 hex digits, port 4,
  state 2, inode 1–20 digits (checked before `strconv`); `fdinfo`: at most 256 bytes read, four lines of fixed keys, `mnt_id` 1–10 and
  `ino` 1–20 digits, the same parser for the probe's own `fdinfo`; command
  texts by procstats' rules (4 KiB read, 1,024 bytes after U+FFFD replacement). Counts are capped as in Approach 2–6.
- [x] Failure texts reaching a user: the gap's error is logged by the scheduler: `connstats: no socket table found`,
  `connstats: net/<table>: <os error>`, `procstats`'s listing error, `ctx.Err()` of a late run; the line error
  `connstats: net/<table>: line <n>: <fixed reason>`; the `err` of `socket owners partly unknown` is
  `connstats: <pid>/fdinfo: <fixed reason or os error>`; the `err` of `socket owners unknown` is the probe's (Approach 9);
  none contains table content, an address or process text.

## Decision records

- `docs/decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md` (Proposed, indexed).

## Challenge

Devil's Advocate, plan challenge: 2 major, 5 minor. All accepted.

- **M1 (a late run saves its inventory)** — accepted: the scheduler drops a late result (`runner.go:203-213`); Approach 5 keeps the
  inventory and log state only when `ctx.Err()` is nil just before `Collect` returns, else returns `ctx.Err()`
  (`TestConnections_LateRunKeepsNoInventory`); the remaining window is an Agent-area residual; spec E6 says so.
- **M2 (the memory measure cannot hold for descriptors)** — accepted, first option: the bound is on the live data (two sizes per axis,
  fact 17), the garbage is stated per line and per descriptor (fact 21); spec *Memory and time* and AC7 reworded (also Security N2).
- **minor 1 (agent tests would read the host's `/proc`)** — accepted: `fixtureConnections()` and the in-memory `/proc` of
  `lowMemoryOptions` (fact 23).
- **minor 2 (one closed descriptor drops the process)** — accepted: `ENOENT` on one `fdinfo/<n>` skips that descriptor only
  (Approach 3; two `TestScanOwners_` tests).
- **minor 3 (listener heap without dedup)** — accepted: one slot per endpoint (Approach 2,
  `TestConnections_ReusePortDuplicatesDoNotCutTheInventory`).
- **minor 4 (top 25 remotes beyond the remote cap)** — accepted: stated in the areas, `TestConnections_RemotesBeyondCapNotCounted`.
- **minor 5 (comparison basis vs the size cut)** — accepted: the basis is the in-memory key list (Approach 5, spec E19,
  `TestConnections_SizeCutOfListenersKeepsTheComparison`).

## Security review

Security, plan review: CHANGES_REQUIRED, B1 and B2 blocking, N1–N5 non-blocking; delta review: B1, B2 fixed, B3 blocking (2nd
rejection), settled by the Product Manager.

- **B1 (the samples leak part of the server's IPv6 address)** — fixed. Every address field is replaced as a whole by a documentation
  value that takes nothing from the real one (`2001:db8::10`, `B80D0120000000000000000010000000`; link-local identifier the
  documentation MAC's, `fe80::200:5eff:fe00:5301`); every line keeps its byte length; `samples/README.md` names no part of the real
  address. The pushed history is a question for the user (orchestrator).
- **B2 (`CAP_DAC_READ_SEARCH` not weighed against `fdinfo`)** — accepted: the collector reads `/proc/<pid>/fdinfo` with
  `CAP_SYS_PTRACE` alone and adds no right. Security's measurement (kernel 6.18.44, no Yama, UID 65534 against a root process):

  | Capabilities | list `fd/` | readlink `fd/3` | list `fdinfo/` | read `fdinfo/3` | read `/etc/shadow` |
  | ------------ | ---------- | --------------- | -------------- | --------------- | ------------------ |
  | none | EACCES | EACCES | EACCES | EACCES | EACCES |
  | `CAP_DAC_READ_SEARCH` | ok | EACCES | EACCES | EACCES | ok |
  | `CAP_SYS_PTRACE` | EACCES | EACCES | ok | ok (`mnt_id: 10`, `ino: 5705`) | EACCES |
  | both | ok | `socket:[5705]` | ok | ok | ok |

  Costs in record 0115 (option 11). The `fd/` link route and `fs.ReadLinkFS` are gone.
- **B3 (the mount vote can be outvoted cheaply)** — accepted; the Product Manager chose option A (record 0115, options 11–12). The
  vote is gone: the agent learns the socket mount once at its start from a Unix datagram socket of its own (Approach 9) and a descriptor
  counts only on that mount (Approach 3). **Duplicated descriptors**: off that mount they count nowhere, on it once per PID and socket.
  **PID-order cap**: `MaxMatches` and the match list are gone; owners and counts live per tracked socket and per PID
  (`TestScanOwners_ManyDescriptorsOfALowPIDStarveNoOne`). **Foreign mounts**: tmpfs, FUSE, container and private mounts have other IDs
  (facts 24–25); `mountinfo` is no longer read. A failed probe fails closed (spec E21). Residuals (inode counter wrap, PID cap) in spec
  E20; fact 26 is the Dev's to verify.
- **N1 (facts 9 and 10)** — taken: confirmed by Security's measurement; the Dev records them in the Agent area and the README.
- **N2 (one size cannot catch growth per line)** — taken with M2: two sizes per axis.
- **N3 (pointer replacements)** — decided: random values (`os.urandom`), not derived from the real pointer; the README says so.
- **N4 (a stuck run stops the check)** — taken: spec E6 and the Agent area residual say so.
- **N5 (the owner's `command` can be faked)** — taken: the Agent area says the PID is the kernel's and the `command` process-chosen
  text; the kernel's `uid` column is the follow-up below.

## Out of scope / follow-ups

As in the spec; #278 (inventories across agent restarts). New follow-up (N5), for the orchestrator to open: **"[Agent] Record the
socket owner's UID from the kernel table in connection snapshots"** — body: "The connection collector (#33) names a listener's owner by
PID and `command`; the command is text a local user chooses, while the `uid` column of `/proc/net/{tcp,tcp6,udp,udp6}` is the kernel's.
Add the socket's UID (and a user name as the process snapshot resolves it) to listener and connection entries, an additive wire field."
Labels `area: agent`, `type: feature`.

## Security notes

Non-blocking findings of Security's delta confirmation (APPROVED) of revision 2, verbatim:

- The probe could also check that the parsed `ino:` equals `syscall.Fstat(fd).Ino`. That costs one call and would catch reading the wrong file. If they differ, return an error, which fails closed.
- `AF_UNIX` must stay allowed in the unit's address-family filter. This is already planned for the Agent area *Rights* and `.squad/project.md`; I will check it in the diff review.
