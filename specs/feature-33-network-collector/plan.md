# Plan: Network connection collector (replaces `lsof -ni`)

Source: [spec.md](spec.md)
Status: Draft
Tier: security — the collector is the first to need `CAP_DAC_READ_SEARCH`, so it adds a granted right to the agent, a permission
that the Agent area makes a `security`-tier change (*Rights*: "adding a collector that needs a new right").
Security areas touched: 6 Agent privileges (`deploy/agent/README.md`, the unit rules in the Agent area *Rights*), 10 Parsing of
external input (`/proc/net/{tcp,tcp6,udp,udp6}`, `/proc/<pid>/fd` links, process names; `internal/connstats`), 12 Logging and display
of external data (process names in records, the collector's log lines)

## Problem / root cause

Feature: see [spec.md](spec.md) (Approved by Lead).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The four tables have the shape the parser expects: header line, then per socket `sl:` local `HEX:PORT` remote `HEX:PORT` state (2 hex) tx:rx tr:when retrnsmt uid timeout inode and a varying tail; IPv4 one 8-hex word, IPv6 four 8-hex words, each word in host byte order, port as 4 hex digits of its value | real captures of the monitored server (all four tables) and the sandbox (`tcp`, `udp`), `samples/README.md`; `::1` reads `00000000000000000000000001000000`, `127.0.0.1` reads `0100007F` | confirmed |
| 2 | `sl` is not unique (udp: four lines `721:`), lines are padded with trailing spaces in `tcp` and `udp` but not in `tcp6`/`udp6`, a TIME_WAIT line has inode 0 and fewer tail fields, an ESTABLISHED line can end in `-1` | `samples/server/{tcp,udp,udp6}` | confirmed |
| 3 | Two sockets on one endpoint occur on the server (BIND: `127.0.0.1:53`, `127.0.0.1:953`, public `:53`, the same in `tcp6`) | `samples/server/tcp` lines 7–8, 17–18, 20–21; `tcp6` lines 7–8, 10–11, 15–16 | confirmed |
| 4 | `tcp6` and `udp6` can be missing | sandbox kernel 6.18.44: `ls /proc/net` has no `tcp6`/`udp6`; an `AF_INET6` socket fails with errno 97 | confirmed |
| 5 | An IPv4-mapped address in `tcp6` is four words like any address (`0000000000000000FFFF0000` + the IPv4 word) | to verify: residual accepted, tests derive the line from a real `tcp6` line by replacing only the address field (Tester, step 5); the #45 parallel run reads real ones; basis: no captured line has one (no IPv6 connection on the server at capture time); the format is per word (fact 1) | unverified |
| 6 | `/proc/net` is the reader's own network namespace | `ls -ld /proc/net` → `/proc/net -> self/net` | confirmed |
| 7 | An fd link of a socket reads `socket:[<inode>]`, the inode matches the table's inode column, and a socket can be in none of the four tables | sandbox capture: fds 3–7 → `socket:[16080]`…`[16084]` = the table inodes; fd 0 → `socket:[16078]`, in no table (`samples/sandbox/`) | confirmed |
| 8 | `os.DirFS` and `fstest.MapFS` implement `fs.ReadLinkFS` (Go 1.25+); the module is on Go 1.27 | `go doc io/fs.ReadLinkFS`, `go.mod` (`go 1.27`), `.squad/project.md` *Test doubles* `/proc` row | confirmed |
| 9 | Listing another user's `/proc/<pid>/fd` needs `CAP_DAC_READ_SEARCH`, reading its links needs `CAP_SYS_PTRACE` (ptrace read check) | to verify: Dev, step 6, as UID 65534 with `setpriv` (tools present in the sandbox) against a root process: list `<pid>/fd` and readlink one entry with no capability, each one alone and both; results into the Agent area *Rights* and `deploy/agent/README.md`; basis: record 0030 states it from kernel source; `ls -ld /proc/1/fd` → `dr-x------ root root` | unverified |
| 10 | With `CAP_SYS_PTRACE`, `hidepid=2` hides no process from the agent | to verify: Dev, step 6, a `proc` mount with `hidepid=2` in a scratch mount namespace (`unshare -m`), listed as UID 65534 with and without `CAP_SYS_PTRACE`; when it cannot be measured, the Agent area says "not measured"; basis: kernel source (`has_pid_permissions` uses the ptrace read check) | unverified |
| 11 | Kernel default local port range is 32768–60999; unprivileged ports start at 1024 | sandbox `/proc/sys/net/ipv4/ip_local_port_range` `32768 60999`, `ip_unprivileged_port_start` `1024` | confirmed |
| 12 | The scheduler tells a run whether pressure mode was on at its start; a collector is `Name()` + `Collect(ctx, Run)` | `internal/scheduler/scheduler.go` (`Run.Pressure`, `Collector`) | confirmed |
| 13 | The process command derivation and PID listing exist and are reusable | `internal/procstats/names.go` (`commandFor`), `procstats.go` `listPIDs`, `reader.stat`/`cmdline` | confirmed |
| 14 | The incident's connection peak takes every source; the chart only the earliest source | `IncidentReconstructor.cs` ~281–292; `IncidentWindowReader.cs` `FindSourceAsync` | confirmed |
| 15 | Wire 1.0 is released (v0.1.0); both decoders accept a newer minor and ignore unknown keys | `git ls-remote --tags origin` → `v0.1.0`; wire format *Versioning*; `decode_test.go:186` (minor 7 accepted) | confirmed |
| 16 | No released backend receives agent batches (no ingest before #40) | ARCHITECTURE (*Network*: ingest port not published until #40) | confirmed |
| 17 | One run stays within 32 MiB of total allocations over 100,000 table lines, 4,096 processes and maximal command texts | to verify: Dev, step 6, the test `TestConnections_ManySocketsStayWithinCaps` (`runtime.MemStats.TotalAlloc` delta); a higher figure goes to the Lead before it is written into the Agent area; basis: design caps (below) | unverified |
| 18 | A run over the sandbox's `/proc` takes milliseconds | to verify: Dev, step 6, `BenchmarkConnections_Collect`, figure and host into `docs/BENCHMARKS.md`; basis: not measured | unverified |
| 19 | Adding `testdata/proc/net/` does not disturb the existing `/proc` fixture tests (procstats lists `testdata/proc`; `net` is no canonical PID) | to verify: Tester, step 5, full `go test ./...` after copying the fixtures; basis: `procstats.go` `pidOfEntry` | unverified |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestConnections_RegularSnapshotHoldsAggregatesAndInventory`, `TestConnections_ListOrders`, `TestConnections_ReusePortEndpointIsOneListener`, `TestConnections_MappedAddressesKept`, `TestConnections_RegularIsNeverComplete` | `internal/connstats/connstats_test.go` |
| AC1 | `TestParseLine_ServerSamples`, `TestParseLine_SandboxSamples`, `TestParseLine_Rejects` | `internal/connstats/table_test.go` |
| AC2 | `TestConnections_FullSnapshotOnFirstRun`, `TestConnections_FullSnapshotUnderPressure`, `TestConnections_FullSnapshotOnNewListener` | `internal/connstats/connstats_test.go` |
| AC2 | `TestJobs_Schedules` (adapted: `connections` 60 s / 15 s / 10 s after `processes`) | `internal/agent/agent_test.go` |
| AC3 | `TestCompare_NewListenerReported`, `TestCompare_BaselineReportsNone`, `TestCompare_EphemeralUDPNotReported`, `TestCompare_TCPOnEphemeralPortReported`, `TestCompare_ReappearingListenerIsNewAgain`, `TestCompare_OwnerChangeIsNotNew` | `internal/connstats/inventory_test.go` |
| AC3 | `TestConnections_FailedRunIsNotCompared`, `TestConnections_ReportsAtMost64NewListeners` | `internal/connstats/connstats_test.go` |
| AC4 | `TestScanOwners_LowestPIDOwnsSharedSocket`, `TestScanOwners_CountsDistinctSocketsPerProcess`, `TestScanOwners_IgnoresSocketsOutsideTables`, `TestScanOwners_EndedProcessIsSkipped`, `TestSocketInode` | `internal/connstats/owners_test.go` |
| AC4 | `TestConnections_OwnerlessSockets`, `TestConnections_InodeZeroLinesAreSeparateSockets` | `internal/connstats/connstats_test.go` |
| AC5 | `TestConnections_UnreadableDescriptorsLogOnce` (plus the measurement of facts 9 and 10) | `internal/connstats/connstats_test.go` |
| AC6 | `TestConnections_MissingIPv6TablesLeaveProtocolsOut`, `TestConnections_AllTablesMissingFails`, `TestConnections_UnreadableLineLeftOut`, `TestConnections_TableReadErrorFails` | `internal/connstats/connstats_test.go` |
| AC7 | `TestConnections_ManySocketsStayWithinCaps`, `TestConnections_LargeRecordIsReduced`, `TestConnections_SocketsBeyondInodeCapUnattributed` | `internal/connstats/connstats_test.go` |
| AC8 | `TestConnections_BlockingReadIsAbandonedAsGap` (blocks `net/tcp` and, separately, a `ReadLink`) | `internal/connstats/connstats_test.go` |
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
| AC11 | `TestConnstats_ImportsOnlyAllowedPackages`, `TestConnections_ReadsOnlyNamedFiles` | `internal/connstats/connstats_test.go` |
| AC12 | `TestCompare_CutInventoryComparesPrivilegedOnly`, `TestCompare_SkippedLineOrChangedTablesComparesNothing`, `TestCompare_CutPrivilegedPartComparesNothing` | `internal/connstats/inventory_test.go` |
| AC12 | `TestConnections_ListenerFloodCausesNoFullSnapshot` (4,100 unprivileged listeners held across runs: no report, regular snapshot, `listener_check` `privileged`; a new listener on port 2222 is not reported, one on port 23 is) | `internal/connstats/connstats_test.go` |

Fixtures the tests read: `testdata/proc/net/{tcp,tcp6,udp,udp6}` (the anonymized server capture) and `testdata/connections/sandbox/{tcp,udp}`,
copied byte for byte from `samples/` (check with `cmp`). Collector tests build an `fstest.MapFS` with those bytes under `net/…`, PID
directories with `stat`, `cmdline` and `fd/<n>` symbolic links (`Mode: fs.ModeSymlink`, `Data: "socket:[16363918]"`); generated tables
for the cap tests are written by a helper from a real line by replacing fields, never typed by hand. A `faultFS`-style wrapper (error
at open, read, `ReadDir` or `ReadLink` of a name) and a blocking wrapper that implements `fs.ReadLinkFS` serve E1, E4 and AC8.

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
   other than 0 (further ones: no owner, `aggregates_incomplete`); a bounded max-heap of the 4,096 lowest listener endpoints in
   inventory order (port 1–1023 first, then protocol, address, port) with the inodes of their sockets; a bounded max-heap of the 4,096
   lowest connections (protocol, local, remote); counts per remote address for at most `MaxRemotes` (4,096) addresses (further ones
   are not counted, `complete` false).
3. **Owners** (`owners.go`): `procstats.ListPIDs` (lowest 4,096 PIDs ascending; more: `aggregates_incomplete`); a failed listing fails
   the sample. Per PID, `ReadDir` of `<pid>/fd` in chunks of 256 and `fs.ReadLink` of each entry; a link `socket:[N]` whose `N` is in the
   index makes the PID the socket's owner if it has none (ascending order → lowest PID, E9) and counts the socket once for the PID (a
   per-PID set, cleared per PID). `ENOENT`/`ESRCH` (process ended) skip the PID silently; `EACCES`/`EPERM` and other errors count it as
   unreadable (E1, `aggregates_incomplete`). `ctx` is checked between PIDs.
4. **Commands:** `procstats.CommandReader` for every PID with a socket; an ended process is dropped from `processes` and owns nothing;
   another error also drops it and counts it unreadable.
5. **Inventory check** (`inventory.go`, E11–E14, E19): the collector keeps the previous successful run's inventory (keys, whether it was
   cut, the set of tables read, whether a line was skipped). No previous (first run) → `none`, nothing new; a skipped line in either run
   or a different set of tables → `none`; either inventory cut at 4,096 → `privileged`, comparing only port 1–1023 keys, or `none` when
   the privileged part itself was cut (the kept list holds only privileged keys and was cut); else `all`. New keys are reported in
   inventory order except UDP keys with a port in 32768–60999; at most 64 go into `new_listeners`, the rest into
   `new_listeners_omitted`. The current inventory then becomes the previous one (also after a `none` check); a failed run changes
   nothing.
6. **Snapshot** (`snapshot.go`): full when `run.Pressure`, the first run, or at least one new listener (reported or omitted); regular
   otherwise (`aggregates_only` true, no `connections`, `remotes` the top 25 by count descending then address). Lists are ordered as S1
   and S4. Filled entry by entry within the payload budget of 1 MiB less 8 KiB of JSON (each entry encoded alone to measure it) in this
   order: `states`, `new_listeners`, `listeners`, `processes`, `remotes`, `connections`; at the first entry that does not fit, that list and
   every later one stop (a cut in `states`, `listeners` or `processes` sets `aggregates_incomplete`). `complete` is true only for a full
   snapshot with nothing cut, skipped, unattributed beyond a cap or uncounted. The record is checked with `wire.CheckRecord`; as a safety
   net a record still too long is shortened by halving `connections`, then `remotes`, `processes`, `listeners`.
7. **Logs** (`collector=connections`, once per change, no process text, no address): warning `socket owners partly unknown` (`processes`)
   / info `socket owners known again`; info `socket tables unavailable` (`tables`, comma-separated in table order) / `socket tables
   available`; warning `socket lines not read` (`lines`, `err`) / info `socket lines read again`.
8. **Wire** (`internal/model`, `internal/wire`, `Vandox.Core`): `connection_snapshot` gains `aggregates_only`, `aggregates_incomplete`,
   `listener_check` (`all`, `privileged`, `none`), `new_listeners` (listener entries, at most `MaxItems`), `new_listeners_omitted`, all
   omitted when zero; rules in both languages: `aggregates_only` requires `complete` false and no `connections`; `aggregates_incomplete`
   requires `complete` false; `new_listeners` or `new_listeners_omitted` require `listener_check` `all` or `privileged`. Field errors:
   `data.complete`: `must be false for an aggregates-only snapshot` / `must be false when aggregates are incomplete`;
   `data.connections`: `must be empty in an aggregates-only snapshot`; `data.listener_check`: one-of error, and `must be all or
   privileged when new listeners are reported`. Format version 1.1 (`wire.MinorVersion`, `WireFormat.MinorVersion`).
9. **Agent:** job `connections` after `processes` (60 s, pressure 15 s, timeout 10 s), `Options.Connections`.

## Options considered

See record 0115 (*Options considered*): netlink `sock_diag` (rejected: a socket); a full list every run (rejected: volume); a
`listener_event` kind or a `kernel_event` type (rejected for additive fields); reusing `complete` (rejected in the spec challenge); an
unbounded comparison set (rejected: memory); reading `ip_local_port_range` (rejected: fixed default); keeping 1.0 (rejected: 1.0 is
released); a second name derivation (rejected for `procstats` exports). Also weighed here: **sharing one `/proc` pass with the process
collector** — fewer directory listings, but it couples two collectors' deadlines and gaps (0029); rejected.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/connstats/{connstats,table,owners,inventory,snapshot}.go` | new collector |
| Go agent | `internal/procstats/command.go` | new: `ListPIDs`, `CommandReader`, `Ended` over the existing helpers |
| Go agent | `internal/model/connection.go` | new fields, `ListenerCheck`, rules |
| Go agent | `internal/wire/wire.go` | `MinorVersion = 1` |
| Go agent | `internal/agent/agent.go` | schedule constants, `Options.Connections`, job |
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
	MaxSockets      = 65536 // sockets with an inode tracked for owners per run
	MaxRemotes      = 4096  // remote addresses counted per run
	MaxNewListeners = 64
	PrivilegedPorts = 1024 // ports below are privileged
	EphemeralFirst  = 32768
	EphemeralLast   = 60999
)
type Sources struct{ Proc fs.FS } // default os.DirFS("/proc"); must implement fs.ReadLinkFS
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
func socketInode(link string) (uint64, bool)
func scanOwners(ctx context.Context, proc fs.FS, pids []int32, owner map[uint64]int32) (perPID map[int32]uint32, unreadable int, firstErr error)

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
```

```csharp
// ConnectionSnapshot.cs, new properties (#region Properties), each with XML docs:
[JsonPropertyName("aggregates_only")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AggregatesOnly { get; set; }
[JsonPropertyName("aggregates_incomplete")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AggregatesIncomplete { get; set; }
[JsonPropertyName("listener_check")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ListenerCheck { get; set; }
[JsonPropertyName("new_listeners")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<Listener>? NewListeners { get; set; }
[JsonPropertyName("new_listeners_omitted")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public uint NewListenersOmitted { get; set; }
// WireFormat.MinorVersion = 1
```

Files the skeleton rewrites: `internal/model/connection.go`, `internal/wire/wire.go`, `internal/agent/agent.go`,
`src/Vandox.Core/Model/ConnectionSnapshot.cs`, `src/Vandox.Core/Wire/WireFormat.cs` (read; final apart from these members).

## Test files

`internal/connstats/{connstats,table,owners,inventory}_test.go`, `internal/procstats/command_test.go`, `internal/model/connection_test.go`,
`internal/wire/{golden,encode,wire}_test.go`, `internal/agent/agent_test.go`, `tests/Vandox.Core.Tests/{ConnectionSnapshotTests,
WireContractTests,IncidentBuilderTests}.cs`, `tests/Vandox.Storage.Tests/SqliteStoreWriteTests.cs`; the benchmark
`BenchmarkConnections_Collect` in `internal/connstats/connstats_test.go` (Tester writes, Dev runs).

Existing call sites a change affects (Tester adapts): `internal/wire/wire_test.go:104-105, 116-117` (version 1.0 → 1.1),
`allKindRecords` in `internal/wire/encode_test.go` and the regenerated `testdata/wire/all-kinds.jsonl` (`VANDOX_UPDATE_GOLDEN=1`), the
golden assertions of `WireContractTests.cs`, `TestJobs_Schedules` in `internal/agent/agent_test.go`.

Code a unit test cannot cover: none.

## Areas

- [Agent](../../docs/areas/agent.md) (Dev): new section *Connections* after *Processes* with everything of Approach 1–7 (reads, caps,
  regular/full, inventory order and check, the 64 cap, ephemeral UDP, logs, errors, bounded memory figure, speed), the residuals
  (E1 `hidepid=2`, E14 with #278, E19 flood, passive FTP noise, mapped addresses split in `remotes`, ownerless sockets vs `lsof -ni`
  for #45); *Rights*: the connection collector's use of both capabilities with the measurement, and the unit rules (no own network
  namespace — no `PrivateNetwork=`, no `NetworkNamespacePath=` —, no `ProcSubset=pid`, `ProtectProc=default`); *Status*, *Related
  decisions* (0115), *Implementation*.
- [Wire format](../../docs/areas/wire-format.md) (Dev): *`connection_snapshot`* (the five fields, their rules and absent meaning),
  *Producer mapping for connection endpoints* (from the kernel tables: host-order words, no zone, mapped kept), *Versioning* (current
  1.1, what 1.1 added).
- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md) (Lead, done in this step): connection peak over every source.

## Documentation updates

- `docs/areas/agent.md`, `docs/areas/wire-format.md` — Dev (above).
- `deploy/agent/README.md` — Dev: rows for `connections` (`CAP_DAC_READ_SEARCH`: list `/proc/<pid>/fd`; `CAP_SYS_PTRACE`: read the
  links), the measurement, the unit settings that must stay off.
- `docs/BENCHMARKS.md` — Dev: *Connection snapshot* (figure, host, command).
- `.squad/project.md` — Dev: area 6 (connection collector uses both capabilities), area 10 (the table reader's bounds), *Test doubles*
  `/proc` row (connstats fixtures and wrappers, implemented).
- `.squad/stack.md` *Layout* — Dev: `connstats` in the package list.
- `docs/ARCHITECTURE.md` and `docs/areas/detection-and-reports.md` — Lead, done in this step (status, components, `internal/` list,
  records; peak over every source).
- Sweep: `grep -rn "procstats\|connection snapshot\|connection_snapshot" docs .squad/project.md .squad/stack.md` — hits are the rows
  above; `docs/areas/log-import.md` (legacy parser) and `web-ui.md` (chart) need no change.

## Architecture check

Outbound-only and "never contacts Telegram": the collector opens no socket (reads files only; AC11 pins imports). Hanging collector
(0029): runs under the scheduler's deadline, tested with a blocking `ReadLink` and table read (AC8). Gaps (0028): failures are gaps by the
scheduler. Rights (0030): no new capability beyond the two named; the first use of `CAP_DAC_READ_SEARCH` is documented. Memory on the
2 GB server: every structure has a fixed cap; the total is measured (fact 17). Wire contract (0075): the golden batch pins the new
fields on both sides. No guarantee is weakened.

## Security considerations

- [x] Limits on parsed input, all on raw bytes before any decoding: table line 512 bytes; address fields exactly 8/32 hex digits, port 4,
  state 2, inode 1–20 digits (checked before `strconv`); fd link: `socket:[` + 1–20 digits + `]` exactly, anything else ignored; command
  texts by procstats' rules (4 KiB read, 1,024 bytes after U+FFFD replacement). Counts are capped as in Approach 2–6.
- [x] Failure texts reaching a user: the gap's error is logged by the scheduler: `connstats: no socket table found`,
  `connstats: net/<table>: <os error>`, `procstats`'s listing error; the line error `connstats: net/<table>: line <n>: <fixed reason>`;
  none contains table content, an address or process text.

## Decision records

- `docs/decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md` (Proposed, indexed).

## Out of scope / follow-ups

As in the spec; #278 (inventories across agent restarts). No new follow-up.
