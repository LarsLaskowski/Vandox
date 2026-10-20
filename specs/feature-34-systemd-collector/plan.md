# Plan: systemd service collector

Source: Issue #34 | [spec.md](spec.md)
Status: Revised (1)
Tier: security — the agent gets a new input channel that every local user can write to (the system bus delivers their signals to the agent's connection), and the bounds that keep such input from growing memory or crashing the agent are a guard whose purpose is to stop an attacker.
Security areas touched: 6 Agent privileges (no new right; unit settings the bus needs, `deploy/agent/README.md`); 10 Parsing of external input (the new D-Bus reader `internal/sdbus`, the option `services.units` in `internal/config`); 11 Outbound calls (the system bus socket as a new local destination besides the ingest port and the notification socket); 12 Logging of external data (unit names from systemd in the agent's log). No dependency is added.

## Problem / root cause

See [spec.md](spec.md): no producer writes the existing `service_state` record yet.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The scheduler, job list and gap rules #34 plugs into exist; #30 is closed | `internal/scheduler/scheduler.go` 23-35 (`Run`, `Collector`), `internal/agent/agent.go` 112-144 (`jobs`) | confirmed |
| 2 | `service_state` exists in Go and C# with `unit`, `load_state`, `active_state`, `sub_state`, `active_enter_at`, `restarts`; `restarts` has no `omitempty` | `internal/model/service.go` 9-48; `src/Vandox.Core/Model/ServiceState.cs`; golden `testdata/wire/all-kinds.jsonl` line 5 (source `systemd`) | confirmed |
| 3 | `Run` carries only `Start` and `Pressure`; a timed-out result is dropped, a failed run is a gap, a refused record logs `record refused` every run, a sink error is only logged | `internal/scheduler/runner.go` 185-187, 204-214, 233-246, 299-307 | confirmed |
| 4 | No D-Bus code or library is in the module | `go.mod` (only `go.yaml.in/yaml/v3`); `grep -rn dbus --include=*.go` empty | confirmed |
| 5 | systemd 249's bus policy allows every user `Properties.Get`, `GetAll`, `GetUnit`, `LoadUnit`, `ListUnits`, `ListUnitsByPatterns`; `method_load_unit` and `list_units_filtered` say "Anyone can call this method" | `src/core/org.freedesktop.systemd1.conf` lines 42-97 and `src/core/dbus-manager.c` 567-587, 1079-1127 at tag v249 (read in scratch) | confirmed |
| 6 | An unprivileged user makes every call the collector needs | `testdata/systemd/capture-systemd249.pcap`: every call sent by `nobody` on the server got a method return | confirmed |
| 7 | Property types: `Id`, `LoadState`, `ActiveState`, `SubState` are `s`; `ActiveEnterTimestamp` is `t` (µs, realtime); `NRestarts` is `u` on `org.freedesktop.systemd1.Service` | `dbus-unit.c` v249 850-893, `dbus-service.c` v249 228; capture messages 6-16 (variants `s`, `t`, `u`) | confirmed |
| 8 | The capture (pcap, link type 231, 74 little-endian version-1 messages): `ListUnitsByPatterns` reply `a(ssssssouso)` (`plesk-php85-fpm.service`), `LoadUnit` replies `o`, `Get` replies `v`; mariadb `loaded/active/running`, `ActiveEnterTimestamp` 1792240371069328 (2026-10-17T12:32:51.069328Z), `NRestarts` 0; `vandox-does-not-exist.service` `not-found/inactive/dead`, 0, 0; `cloud-config.service` `masked/inactive/dead`; signals `PropertiesChanged` (`sa{sv}as`, 1083 and 1092 bytes), `UnitNew`/`UnitRemoved` (`so`) | decoded with a scratch script (`python3 -I`) | confirmed |
| 9 | A not-found unit is loaded for each call on its path and unloaded at once (broadcast `UnitNew`/`UnitRemoved` around every `Get`), and `Get` still answers; so the collector reads a not-found, inactive unit with one `ListUnitsByNames` entry and no `Get` | capture messages 17-44 | confirmed |
| 10 | An alias resolves to its unit: the path and the name systemd returns for it are the primary name's (`ListUnitsByNames` writes `u->id`, row 22) | `reply_unit_path` → `unit_dbus_path(u)` → `u->id` (`dbus-manager.c` v249 410-426, `unit.c` v249 3215-3222) | confirmed |
| 11 | `ListUnitsByPatterns` matches primary names only, with `fnmatch(FNM_NOESCAPE)`, over loaded units (not-found stubs included) | `list_units_filtered`, `dbus-manager.c` v249 1103-1115 | confirmed |
| 12 | The default system-bus policy lets every user connect and send signals to any connection; method calls are denied unless a service allows them; replies only when requested | this host's `/usr/share/dbus-1/system.conf` lines 46-57 (dbus 1.14; the stock file has carried these lines since dbus 1.10, Ubuntu 22.04 ships 1.12); measured on the scratch bus of row 16: a signal from a second connection addressed to the first's unique name was delivered to it | confirmed |
| 13 | The default maximum message size is 32 MiB, at most 16 file descriptors per message; the protocol's absolute maximum is 2^27 bytes | `system.conf` 117-118 (defaults hard-coded in dbus-daemon); D-Bus specification, *Message Format* | confirmed |
| 14 | The bus delivers broadcast signals only to connections whose match rules select them; a client without match rules receives only unicast messages addressed to it | D-Bus specification, *Message Bus Specification*, *Match Rules* / routing | confirmed |
| 15 | The agent never receives a file descriptor: a plain `read(2)` on a unix stream socket without a control buffer makes the kernel close any `SCM_RIGHTS` descriptors of that data, whatever the bus does, and `sdbus` reads only through `net.Conn.Read` | Security's measurement in the plan review: 100 pipe descriptors sent over a socketpair, read with plain `os.read`, process descriptor count 6 before and 6 after; `net.UnixConn.Read` is `read(2)` without control data (Go `net/fd_posix.go` `Read`); pinned by `TestSdbus_ImportsOnlyAllowedPackages` (no `ReadMsgUnix`, `ReadFrom*`, `SyscallConn`, `File`, `syscall`) | confirmed |
| 16 | `EXTERNAL` authentication and `Hello` on a bus with the stock system policy: the client sends a NUL byte, `AUTH EXTERNAL <hex of the ASCII decimal uid>` and CR LF; the daemon answers `OK ` plus 32 lower-case hex digits and CR LF (`OK 379ab99b4e3b5df5bce269566ad7ae12`); after `BEGIN` and `Hello` (serial 1) it sends in one write the method return (flags `NO_REPLY_EXPECTED`, header fields destination `:1.N`, `reply_serial` 1, signature `s`, sender `org.freedesktop.DBus`, body the unique name) and then a **unicast `NameAcquired` signal** to the new connection (bytes below *Approach*) | measured by the Lead: `dbus-daemon` 1.14.10 of this host with `/usr/share/dbus-1/system.conf` (only `<listen>` moved to a scratch path, `<user>`, `<fork>`, `<pidfile>`, `<syslog>` and includes removed), a Python client run as uid 0 and as uid 65534 (`setpriv`), same exchange; the server was stopped afterwards | confirmed |
| 22 | systemd 249 lets every user call `ListUnitsByNames(as)`; it loads each named unit (`bus_load_unit_by_name`, aliases included), skips a name that is not a valid unit name and answers one `(ssssssouso)` entry per name in order, with the primary name (`u->id`), load, active and sub state and the object path | `src/core/org.freedesktop.systemd1.conf` v249 lines 98-100; `method_list_units_by_names` and `reply_unit_info`, `dbus-manager.c` v249 757-829 | confirmed |
| 23 | The capture holds no `ListUnitsByNames` reply; its signature and layout are those of the captured `ListUnitsByPatterns` reply (both written by `reply_unit_info`) | capture message 2; row 22 | confirmed |
| 17 | The system bus listens on `unix:path=/run/dbus/system_bus_socket` | this host's `system.conf` line 42; `/var/run` → `/run` | confirmed |
| 18 | Connecting to the bus socket works under the unit settings #42 plans (`ProtectSystem=strict`: the kernel's read-only check does not apply to sockets) | to verify: in #42 on the server (`systemd-run -p ProtectSystem=strict -p User=vandox-agent ...`), owner of #42 | unverified |
| 19 | A `t` value above `math.MaxInt64/1000` µs or past 2262-04-11 cannot be stored by the backend and makes Go's JSON encoder fail for years past 9999 | wire area *Records* (`captured_at` range), `time.Time.MarshalJSON` (years 0-9999) | confirmed |
| 20 | String lists in the configuration record only the key's line; a null element is refused as `<key>: has no value` with the element's line | `internal/config/decode.go` 248-281 (`setOther`, `nullElement`) | confirmed |
| 21 | Agent tests run `Run` with the default configuration; with a default unit list they would dial the real bus | `internal/agent/agent_test.go` (`TestRun_*` use `config.DefaultAgent` via fixture files) and `TestJobs_Schedules` 470-503 | confirmed |

Claims of the issue: "read via D-Bus: `ActiveState`, `SubState`, active-enter timestamp, `NRestarts`" — confirmed (rows 5-7); "#30" — confirmed (row 1). The default unit names are the issue's; `plesk-php85-fpm.service` in the capture confirms the Plesk PHP-FPM naming, the others are not checked against the server (the missing-unit warning reports any that is absent).

Related defect found on the way: none.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestLoadAgent_ServiceUnitsDefault`, `TestLoadAgent_ServiceUnitsOverrideAndSuffix`, `TestLoadAgent_ServiceUnitsEmptyTurnsCollectorOff`; `TestJobs_Schedules` (case "no service unit leaves out the systemd job") | `internal/config/agent_test.go`; `internal/agent/agent_test.go` |
| AC2 | `TestLoadAgent_ServiceUnitsRefused` (table: every E1 case with the exact error text, and that the text holds no value) | `internal/config/agent_test.go` |
| AC3 | `TestCollect_FirstRunRecordsEveryUnit`, `TestCollect_AliasRecordedOnceUnderPrimaryName`, `TestCollect_ActiveEnterLeftOutWhenZeroOrNotStorable`, `TestCollect_NotFoundUnitNeedsNoGet`; `TestSession_ReadsCapturedUnits` (the captured `ListUnitsByPatterns` reply decoded byte for byte, and the captured `Get` replies of `ActiveEnterTimestamp` and `NRestarts`), `TestSession_ListUnitsByNamesInChunks` | `internal/svcstats/svcstats_test.go`; `internal/sdbus/sdbus_test.go` |
| AC4 | `TestCollect_RecordsOnlyChanges`, `TestCollect_RepeatsAfterAnHour` | `internal/svcstats/svcstats_test.go` |
| AC5 | `TestCollect_WarnsOnceAboutMissingUnits` | `internal/svcstats/svcstats_test.go` |
| AC6 | `TestCollect_PatternsTrackLoadedMatches` (with a not-found match that is not tracked), `TestCollect_CapsTrackedUnits`, `TestCollect_SkipsMatchesTheWireRefuses` | `internal/svcstats/svcstats_test.go` |
| AC7 | `TestCollect_BusFailureFailsTheRun`, `TestSession_RefusesUnknownHeaderFieldType`, `TestCollect_HungBusEndsAtTheDeadline`, `TestSession_ErrorReplyFailsTheCall`, `TestSession_RefusesOversizedReply`, `TestDial_ConnectsAndAuthenticates` | `internal/svcstats/svcstats_test.go`; `internal/sdbus/sdbus_test.go` |
| AC8 | `TestRunner_PreviousLostAfterEveryKindOfLoss`; `TestCollect_PreviousLostRecordsEveryUnit` | `internal/scheduler/runner_test.go`; `internal/svcstats/svcstats_test.go` |
| AC9 | `TestCollect_RefusedUnitBecomesItsOwnGap` | `internal/svcstats/svcstats_test.go` |
| AC10 | `TestJobs_Schedules` (case with the systemd job: 30 s, 10 s, 5 s) | `internal/agent/agent_test.go` |
| AC11 | `TestSession_SkipsUnsolicitedMessages` (the captured signals, the measured `NameAcquired` and a message of an unknown type before a reply), `TestSession_DiscardsUnsolicitedSignalsWithoutKeepingThem` (bound test), `TestSdbus_ImportsOnlyAllowedPackages`, `TestSession_CallsOnlyReadMethods`, `TestSvcstats_ImportsOnlyAllowedPackages` | `internal/sdbus/sdbus_test.go`; `internal/svcstats/svcstats_test.go` |

Cases the tests must hold:

- AC2 texts (`config: <file>:<line>: <key>: <reason>`, line of the element): more than 64 → `services.units: must be a list of at most 64 unit names`; an element empty, over 255 bytes with the implied `.service`, with a character outside `[A-Za-z0-9:_.@*?-]`, starting with `.` or `-`, or with more than one `@` → `services.units[<i>]: must be a service unit name or pattern of 1 to 255 bytes of [A-Za-z0-9:_.@*?-], not starting with . or -`; another type suffix → `services.units[<i>]: must name a service unit`; `foo@.service`, `@foo` → `services.units[<i>]: must name an instance, not a template`; `nginx` after `nginx.service` → `services.units[<i>]: listed twice`; a null element → `services.units: has no value`; a mapping element or a scalar for the list → the decoder's existing type reason.
- AC3/E10: `mysql` and `mariadb` both configured, `ListUnitsByNames` answers `mariadb.service` for both → one record.
- AC3: timestamp 0 and `math.MaxUint64` → no `active_enter_at`; 1792240371069328 → `2026-10-17T12:32:51.069328Z`.
- AC4: each of the five fields changed alone gives one record; no change gives none; `run.Start` 59:59 after the last record gives none, 60:00 gives one.
- AC5: the line `systemd units not found` with `collector=systemd`, `count`, `units` in file order; not logged on later runs, not logged for patterns, masked or bad-setting units; with a first failed run it comes at the first successful one.
- AC6: 300 matches → 256 tracked (exact first, matches by name), `systemd units cut` once with `left_out`; a later match sorting first is left out and counted; a match whose name holds `\` is not tracked and `systemd units not tracked` (`count`) is logged once without the name; a match no longer listed is still read.
- AC7: the fake reader returns an error at open, at `List` and at `Read` → `Collect` returns an error and no record; a reader blocking until its context ends returns `context.DeadlineExceeded` (synctest bubble, 0062).
- AC8: scripted collector under `synctest`: a timeout, a skipped run, an error, a panic, a refused record and a sink error each make the next `Run.PreviousLost` true; a clean run makes it false; a late start alone does not set it.
- AC9: a unit whose `active_state` is `frozen` or `sub_state` `Running` → no record for it, one gap (source `agent`, collector `systemd`, cause `unknown`, `from` = `run.Start`, `to` = `run.Start` plus the interval of the run's mode), the other units' records kept; warning `systemd unit state not recorded` with `unit` (the tracked name) and `field` once, `systemd unit state recorded again` at recovery; the scheduler's handler sees no `record refused`; no log line holds the refused value.
- AC11 bound: the fake bus sends 8 signals with a declared 32 MiB body each (generated lazily, never held) before the reply; the call succeeds and `runtime.MemStats.TotalAlloc` grows by less than 1 MiB during it.
- AC11 method list: a recording fake bus over `net.Pipe` sees only `Hello` (destination `org.freedesktop.DBus`), `ListUnitsByPatterns`, `ListUnitsByNames` and `Properties.Get` of `ActiveEnterTimestamp` and `NRestarts` (destination `org.freedesktop.systemd1`, flag `NO_AUTO_START`), and never `NEGOTIATE_UNIX_FD` in the auth exchange.
- AC11 imports: `TestSdbus_ImportsOnlyAllowedPackages` allows only the packages and selectors the client needs and forbids `ReadMsgUnix`, `ReadFrom*`, `SyscallConn`, `File`, the `syscall` package, `os.Getenv` and `os.LookupEnv` (so the socket stays the constant `SystemBusPath`, never `DBUS_SYSTEM_BUS_ADDRESS`), as `TestConnstats_ImportsOnlyAllowedPackages` does.
- AC3 (not-found): a unit listed `not-found`/`inactive` gets `restarts` 0, no `active_enter_at` and no `Get`; a `not-found` unit that is still `active` (its file removed while it runs) gets both `Get`s.
- Fake bus data: the auth line and the `Hello` return plus `NameAcquired` are the measured bytes of row 16; `ListUnitsByPatterns` and the `Get` replies are the captured messages; a `ListUnitsByNames` reply is built by the test in the layout of the captured `ListUnitsByPatterns` body (row 23) from the captured unit values (mariadb, the not-found and the masked unit).

## Approach

1. **Configuration** (`internal/config`). `Agent.Services` with `Units []string` (`yaml:"services"` / `"units"`); default the eleven entries of the spec, stored as full names. `validate` calls `checkServiceUnits`, which applies the E1 rules in list order and replaces each entry with its full name (`.service` appended unless the suffix after the last dot is `service`; a suffix of another unit type is refused). The decoder records the line of every element of a string list as `<key>[<i>]` so the error can name it. `AgentKeys` gains `services.units`.
2. **Scheduler** (`internal/scheduler`, record 0110 option 13). `Run.PreviousLost`; the runner keeps `lost`, set by a timeout, a skipped run, an error or panic, a refused record and a sink error, cleared by a run whose records were all kept and put without error, and passes it into the next `Run`.
3. **D-Bus client** (`internal/sdbus`, new). `Dial` connects a unix stream socket to the constant `SystemBusPath` with `net.Dialer.DialContext`; `Open` runs over any `net.Conn` (tests). It uses only `Read`, `Write`, `SetDeadline` and `Close` of the connection, so the kernel drops any descriptor sent to it (row 15). Per session: the ctx deadline as the connection deadline and `context.AfterFunc` to end it on cancellation; the auth reply line read up to 512 bytes, only `OK` plus 32 hex digits accepted; never `NEGOTIATE_UNIX_FD`; `Hello` first, its `NameAcquired` signal skipped like any unsolicited message. Writer: little-endian method calls with path, interface, member, destination and signature, flag `NO_AUTO_START` for `org.freedesktop.systemd1`. Reader: a 16-byte fixed header (byte order `l` or `B`, version 1), `16 + fields + padding + body` at most 2^27 or the session fails; a message of any type other than method return or error (signals, method calls, unknown types) is skipped by length (`io.CopyN(io.Discard, …)`, never decoded or kept). A method return or error is read only with header fields of at most 64 KiB; only the known field codes 1-9 are decoded, each with its fixed basic type (`o`, `s`, `g`, `u`; no variant walk, no recursion), any other code or type fails the call; it is accepted only for the awaited serial, other serials are skipped. The awaited reply's body must be at most 1 MiB and its signature exactly the expected one (`s`, `a(ssssssouso)`, `v` holding `t` or `u`); strings must be NUL-terminated valid UTF-8 within the body; object paths are checked against the D-Bus path grammar before they are sent back. An error reply ends the call with a fixed text. Calls: `Hello`, `ListUnitsByPatterns([], patterns)`, `ListUnitsByNames(names)` in calls of at most 64 names (the reply must hold exactly one entry per name), `Get` of `ActiveEnterTimestamp` and `NRestarts`.
4. **Collector** (`internal/svcstats`, new; name and source `systemd`). Exact entries are tracked from the start; each run opens a reader, lists the pattern matches (when there are patterns), adds new matches whose name the wire format accepts and whose `load_state` is not `not-found` up to 256, reads every tracked unit with `ListUnitsByNames` (one consistent load, active and sub state per unit and its primary name) and, except for a `not-found` unit that is `inactive`, the two `Get`s; dedupes by primary name, builds `model.ServiceState` (`active_enter_at` from µs, left out when 0 or not storable), validates it with `Validate` and records it when `run.PreviousLost`, on the first read, on a field change or when its last record is an hour old. A refused unit is left out and the run returns one gap of its own. Logs as the spec fixes them. Any error of the reader fails the run. A run makes `Hello`, one call per 64 tracked units, one list call and two `Get`s per loaded unit: about 25 round trips for the default list, about 520 at the cap of 256.
5. **Agent** (`internal/agent`). `Options.Services svcstats.Sources` (zero: the system bus); the job after `connections` when `services.units` is not empty, schedule 30 s / 10 s / 5 s.

Measured bytes of row 16 (one read after `BEGIN` and `Hello`, uid 65534, unique name `:1.1`, 258 bytes: the method return, then
the `NameAcquired` signal); the Tester stores them as `testdata/systemd/hello-dbus-1.14.hex` with this provenance:

```text
6c02010109000000010000003d00000006017300040000003a312e310000000005017500010000000801670001730000
07017300140000006f72672e667265656465736b746f702e4442757300000000040000003a312e31006c040101090000
00020000008d00000001016f00150000002f6f72672f667265656465736b746f702f4442757300000002017300140000
006f72672e667265656465736b746f702e4442757300000000030173000c0000004e616d654163717569726564000000
0006017300040000003a312e3100000000080167000173000007017300140000006f72672e667265656465736b746f70
2e4442757300000000040000003a312e3100
```

## Options considered

Weighed in record 0117 (D-Bus against `systemctl` or `/run/systemd`; godbus or go-systemd against a hand-written client; one connection per run against a persistent one; `GetAll`, `LoadUnit` plus six `Get`s, or `ListUnitsByNames` plus two `Get`s; polling against `PropertiesChanged`; what to record; patterns; services only) and in record 0110 option 13 (context check, confirmation callback or a `Run` flag for a lost sample). One more: the scheduler refusing an invalid unit's record (logs `record refused` with the wire error every run) against the collector validating and returning the gap itself (chosen, spec E5).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `internal/config` | `agent.go` (`Agent`, `Services`, `DefaultAgent`, `AgentKeys`, `validate`, `checkServiceUnits`, `serviceUnitName`), `decode.go` (`setOther`: element lines) | option `services.units` |
| `internal/scheduler` | `scheduler.go` (`Run`), `runner.go` (`runner.lost`) | `PreviousLost` |
| `internal/sdbus` (new) | `sdbus.go` (`Dial`, `Open`, `Session`, `UnitInfo` and its calls), `message.go` (header encode/decode, body decoder) | minimal D-Bus client |
| `internal/svcstats` (new) | `svcstats.go` (`Collector`, `Sources`, `Systemd`, `Reader`, `Unit`, `BusSystemd`) | the collector |
| `internal/agent` | `agent.go` (`Options.Services`, constants, `jobs`) | wiring |
| `testdata/systemd` | `capture-systemd249.pcap` (exists, from the Product Manager) | read by the tests |

## Signatures (for the Dev's skeleton)

```go
// internal/config/agent.go
type Services struct {
	Units []string `yaml:"units"` // full service unit names or patterns, in file order
}
// Agent gains: Services Services `yaml:"services"` (after Processes)
const (
	MaxServiceUnits     = 64
	MaxServiceUnitBytes = 255
)
func DefaultServiceUnits() []string
func checkServiceUnits(file string, lines map[string]int, values []string) ([]string, error)
func serviceUnitName(entry string) (name, reason string)

// internal/scheduler/scheduler.go
type Run struct {
	Start        time.Time
	Pressure     bool
	PreviousLost bool // the collector's previous due sample became a gap, had a record refused or was refused by the sink
}

// internal/sdbus/sdbus.go
const (
	SystemBusPath   = "/run/dbus/system_bus_socket"
	MaxReplyBody    = 1 << 20
	MaxHeaderFields = 64 << 10
	MaxMessage      = 1 << 27
)
type Session struct{ /* conn, serial, uid */ }
func Dial(ctx context.Context, path string) (*Session, error)
func Open(ctx context.Context, conn net.Conn, uid int) (*Session, error)
func (s *Session) Close() error
func (s *Session) ListUnitsByPatterns(ctx context.Context, patterns []string) ([]UnitInfo, error)
type UnitInfo struct {
	ID, LoadState, ActiveState, SubState string
	Path                                 string // object path, checked against the D-Bus path grammar
}
func (s *Session) ListUnitsByNames(ctx context.Context, names []string) ([]UnitInfo, error) // at most 64 names per call
func (s *Session) GetUint64(ctx context.Context, path, iface, property string) (uint64, error)
func (s *Session) GetUint32(ctx context.Context, path, iface, property string) (uint32, error)

// internal/svcstats/svcstats.go
const (
	Name       = "systemd"
	MaxTracked = 256
	Repeat     = time.Hour
)
type Unit struct {
	ID          string
	LoadState   string
	ActiveState string
	SubState    string
	ActiveEnter uint64 // µs since the epoch, 0 = never
	Restarts    uint32
}
type Reader interface {
	List(ctx context.Context, patterns []string) ([]Unit, error) // matches with their states; ActiveEnter and Restarts not read
	Read(ctx context.Context, names []string) ([]Unit, error)    // one Unit per name, in order
	Close() error
}
type Systemd interface {
	Open(ctx context.Context) (Reader, error)
}
type Sources struct {
	Systemd Systemd // nil: BusSystemd(sdbus.SystemBusPath)
}
func BusSystemd(path string) Systemd
func New(src Sources, units []string, schedule scheduler.Schedule, log *slog.Logger) *Collector
func (c *Collector) Name() string
func (c *Collector) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)

// internal/agent/agent.go
const (
	SystemdInterval         = 30 * time.Second
	SystemdPressureInterval = 10 * time.Second
	SystemdTimeout          = 5 * time.Second
)
// Options gains: Services svcstats.Sources
```

Existing files the skeleton rewrites: none whole; `internal/agent/agent.go`, `internal/config/agent.go`, `internal/scheduler/scheduler.go` gain the members above.

## Test files

- `internal/config/agent_test.go`, `internal/scheduler/runner_test.go`, `internal/sdbus/sdbus_test.go`, `internal/svcstats/svcstats_test.go`, `internal/agent/agent_test.go`.
- Helpers (Tester): `internal/sdbus/capture_test.go` (reads the classic pcap of `testdata/systemd/capture-systemd249.pcap`, link type 231, returns the messages in order with their bytes) and `internal/sdbus/fakebus_test.go` (a server on one end of `net.Pipe`, or a unix stream socket in `t.TempDir()` for `Dial`: the auth line and the `Hello` return plus `NameAcquired` with the measured bytes of row 16 (unique name patched in place), then for each request the captured reply of the same member and arguments with `reply_serial` patched in place, or a `ListUnitsByNames` reply built as *Tests per criterion* says, optionally sending captured or generated messages first; it records every request and the auth lines). `svcstats` tests use a hand-written fake `Systemd` (scripted units per run and errors, a reader that blocks until its context ends) — the *systemd D-Bus* double of `.squad/project.md`.
- Existing call sites: `TestJobs_Schedules` and every `TestRun_*` in `internal/agent/agent_test.go` pass `Options` and must set `Services` to the fake (or a configuration with `services.units: []`) so no test dials the real bus — Tester. The `scheduler.Run{…}` literals in `sysstats`, `connstats`, `procstats` and `selfstats` tests are keyed and need no change. `TestLoadAgent_Example` needs the new key in `deploy/agent/agent.yaml` (Dev, task 7).
- Code a unit test cannot cover: none (the `Dial` path is covered with a unix socket in `t.TempDir()`).

## Areas

- [Agent](../../docs/areas/agent.md): new section *Services*; *Status* paragraph, *Connection* (third local channel), *Rights* (no right; unit settings), *Related decisions* (0117), *Implementation*.
- [Configuration and secrets](../../docs/areas/configuration-and-secrets.md): `services.units` row and rules with the error texts.
- [Wire format](../../docs/areas/wire-format.md): `service_state` producer (source `systemd`, when a record is written).

## Documentation updates

Sweep for enumerations the change extends (agent's local channels, packages, options, collectors, test doubles), with each hit and its owner:

| File | Edit | Owner |
| ---- | ---- | ----- |
| `docs/areas/agent.md` | as *Areas*; line 48 "Two local channels" becomes three; *Scheduling*: a run learns that the previous sample was lost — its result was not kept (timeout, skip, error or panic, refused record, sink error); a late start does not set it | Dev |
| `docs/areas/configuration-and-secrets.md` | options table row and rules with every E1 text, including more than one `@` | Dev |
| `docs/areas/wire-format.md` | `service_state` producer paragraph | Dev |
| `docs/ARCHITECTURE.md` | line 14 status list of collectors, *Components* (agent bullet and the `internal/` package list), *Records* list (0117) | Dev |
| `.squad/project.md` | area 6 (bus socket reachable, no right), area 10 (`internal/sdbus` bounds, `services.units`, and the residual of record 0117: a local user who learns the agent's unique name during a run can flood that connection and make the run a gap), area 11 (line 224: the system bus as the third destination), area 12 (unit names in logs), *Test doubles* rows *systemd D-Bus* (implemented) | Dev |
| `.squad/stack.md` | *Layout* package list (line 35): `sdbus`, `svcstats` | Dev |
| `README.md` | *Agent options* row `services.units` | Dev |
| `deploy/agent/agent.yaml` | commented `services:` section, `units` at the default | Dev |
| `deploy/agent/README.md` | the `systemd` collector needs no group and no capability; *Unit settings*: `AF_UNIX` also for the system bus, `/run/dbus/system_bus_socket` must stay reachable (no `InaccessiblePaths=`/`TemporaryFileSystem=` over `/run/dbus`) | Dev |
| `docs/UNIT_TESTS.md` | line 63: a `net.Pipe` or a unix stream socket in `t.TempDir()` stands in for the system bus, local IPC | Dev |

## Architecture check

- *A hanging collector never blocks the agent* (0029): the collector runs under the scheduler with a 5-second timeout; the session takes the run's context as its socket deadline, so a stuck bus ends the run at the deadline and the worker returns.
- *No data gaps unless recorded* (0028): every failed or late run is the scheduler's gap; a unit left out is the collector's own gap; a lost sample makes the next run record every unit.
- *Outbound only, never listens* (0006): the agent connects to a local socket and binds nothing.
- Runs unprivileged (0030): no new right (rows 5-6).

## Security considerations

- [ ] No descriptor reaches the agent (row 15): only `net.Conn` `Read`/`Write`/`SetDeadline`/`Close`, pinned by the import and selector test; the socket path is the constant, no environment variable is read.
- [ ] Limits apply to raw bytes before any decoding: the fixed header's lengths (fields at most 64 KiB, body at most 1 MiB for the awaited reply, the whole message at most 2^27) are checked before any byte of the fields or body is read; skipped messages are drained in a fixed buffer; auth lines are cut at 512 bytes before they are compared; no text is replaced, invalid UTF-8 in a string fails the call. The configuration limits (64 entries, 255 bytes) apply to the decoded YAML string.
- [ ] Texts that reach a log line (`collector failed err=`): `sdbus: connect: <os error>` (the fixed socket path), `sdbus: authentication refused`, `sdbus: message: <fixed reason>`, `sdbus: <member>: error reply <error name>` (the name only when it matches the D-Bus error-name grammar and is at most 255 bytes, else without it), `sdbus: <member>: reply too large`, `sdbus: <member>: unexpected signature`, `sdbus: <member>: malformed body`, a deadline's `os` error; never a string of a reply body. The collector's warnings carry a tracked name (configuration or validated match) and a field name, never a value systemd sent.

## Decision records

- `docs/decisions/0117-systemd-collector-hand-written-d-bus-client-per-run-states-recorded-on-change.md` (Proposed, indexed).
- `docs/decisions/0110-…` extended in place (unreleased, `git ls-remote --tags origin`: only `v0.1.0`, which does not contain it): option 13 and the decision sentence.

## Challenge

- M1 (seven calls per unit; `ActiveState` and `SubState` read at different moments): accepted. `ListUnitsByNames` (rows 22-23) gives primary name and the three states of each unit in one entry; the read path is now `ListUnitsByNames` in calls of 64 names plus two `Get`s per loaded unit (none for a not-found inactive one). The capture has no `ListUnitsByNames` reply; the fake builds it in the captured `ListUnitsByPatterns` layout. *Approach* 3-4, signatures, AC11 cases and record 0117 option 4 changed.
- m1 (not-found stubs among pattern matches): accepted; spec E14 and AC6, test case added.
- m2 (0110 wording): accepted; 0110 option 13 and its decision sentence say "a run whose result was not kept (timeout, skip, error or panic, refused record, sink error); a late start does not set it", as will the Agent area (Dev).
- m3 (rows 15/16 in the wrong step): resolved: both rows are confirmed now, no measurement task remains.
- m4 (more than one `@`): accepted; spec E1, and the configuration area states every E1 text (Dev).

## Security review

- B1: accepted. Row 15 rests on the kernel; `internal/sdbus` uses only `net.Conn` `Read`, `Write`, `SetDeadline`, `Close`, pinned with the forbidden selectors and `os.Getenv`/`os.LookupEnv` by `TestSdbus_ImportsOnlyAllowedPackages`; *Approach* 3, *Security considerations* and 0117 option 2 changed.
- B2: accepted. The Lead measured the auth exchange and `Hello` on the stock system policy before any test is written (row 16, bytes after *Approach*), which also showed the unicast `NameAcquired` signal after `Hello`; task 3 uses these bytes.
- N1, N2, N3: taken (*Approach* 3; `TestSession_RefusesUnknownHeaderFieldType`; `NO_AUTO_START` in the AC11 method list). N4: as m1. N5: taken (`.squad/project.md` area 10, Dev; 0117 *Consequences*). N6: kept.

## Out of scope / follow-ups

As the spec's *Out of scope*. Row 18 (the bus under `ProtectSystem=strict`) is checked in #42.
