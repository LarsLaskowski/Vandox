# Plan: MariaDB collector

Source: Issue #35 | [spec.md](spec.md)
Status: Draft
Tier: security — the change creates security controls: the bounded decoder of a protocol whose replies carry text local users choose (area 10), the MariaDB account with its privilege in `deploy/agent/mariadb-user.sql` (area 7), and a new local destination of the agent (area 11).
Security areas touched: 6 Agent privileges (the unit must keep `AF_UNIX` and `/run/mysqld` reachable; no new right), 7 MariaDB monitoring user (`deploy/agent/mariadb-user.sql`, the login and the account check), 10 Parsing of external input (`internal/mariadb` reply decoder, the process list text, the exported `/proc/meminfo` and `/proc/net/tcp*` readers, the options `mariadb.*`, the wire field `mariadb_status.error_code` in both decoders), 11 Outbound calls (the MariaDB socket as a further local destination), 12 Logging and display of external data (the collector's log lines; process list text stored for display)

## Problem / root cause

Feature: [spec.md](spec.md). Nothing produces `mariadb_status` today; the agent has no MariaDB client (`go.mod` holds only
`go.yaml.in/yaml/v3`).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `mariadb_status` exists with `up`/`down`/`not_answering`, values only with `up`; its `Validate` is excluded from gocognit by name | `internal/model/mariadb.go:13-15,65`; `src/Vandox.Core/Model/MariaDbStatus.cs:134,157`; `.squad/stack.md:93` (record 0047) | confirmed |
| 2 | A run's `Start` is `time.Now().UTC()`, wall time without a monotonic reading; `Pressure` and `PreviousLost` are passed in | `internal/scheduler/runner.go:186`, `scheduler.go:23-27` | confirmed |
| 3 | Job order self, system, processes, connections, systemd (if units), filesystem (if mount points), clock | `internal/agent/agent.go` `jobs` | confirmed |
| 4 | The server is `12.3.3-MariaDB-ubu2204` with no `5.5.5-` prefix; protocol 10; capabilities `0x81fffffe` (bit 0 clear: MariaDB), extended capabilities `0x3d`, plugin `mysql_native_password`; connection id `0x5670` = 22128 | decoded greeting, `testdata/mariadb/select-status-variables-processlist.txt` lines 1-9 (scratch decoder, read-only) | confirmed |
| 5 | **No authentication switch:** the client sent `vandox-agent`, empty length-encoded auth data and `mysql_native_password`; the next server packet (sequence 2) is an OK packet (`0x00`, status `0x4002`, 152 bytes of session state) | capture lines 10-37; same in `account-check.txt` | confirmed |
| 6 | The captured client sent capabilities `0x00bfa284` (no `CLIENT_DEPRECATE_EOF`), extended capabilities `0x1d` (with extended metadata), max packet `0x00100000`, charset 45; result sets end in 5-byte EOF packets (`fe 00 00 22 00`, `fe 00 00 02 00`), and every column definition has one extra empty length-encoded field before the `0x0c` block | decoded capture; status, variables, processlist, both account statements | confirmed |
| 7 | Status: 571 rows, every status name of the selection present (e.g. `Memory_used` 483689568, `Threads_running` 1), 41 non-numeric values; variables: 696 rows, longest value 879 bytes, negative values exist (`performance_schema_*_size` = -1), every variable of the selection present plus `extra_port` 0, `socket` `/run/mysqld/mysqld.sock`, `bind_address` 127.0.0.1, `local_infile` OFF, `max_allowed_packet` 16777216; no name repeats ignoring case | decoded capture | confirmed |
| 8 | `SHOW FULL PROCESSLIST` has 9 columns `Id User Host db Command Time State Info Progress`; `db` and `Info` are NULL (`0xfb`) for the sleeping Plesk connection; the agent's own row has `Id` 22128 = the greeting's connection id | decoded capture | confirmed |
| 9 | Account check: `SELECT CURRENT_USER()` returns `vandox-agent@localhost`; the `USER_PRIVILEGES` query returns one row `PROCESS` | decoded `account-check.txt` | confirmed |
| 10 | The captures are fully anonymized | `general_log_file` `144.log`, `log_slow_query_file` and `slow_query_log_file` `144-slow.log` keep the first label of the real host name (the placeholder replaced only `hostname`/`wsrep_node_name`); `server_uid` `7IMtIuiK3yUxSz52tzqD201rYgA=` is the installation's real identifier | refuted |
| 11 | Theoretical maximum of the captured server: 437,256,192 global + 151 × 3,051,520 = 898,035,712 bytes (`log_bin` OFF) | arithmetic on fact 7 | confirmed |
| 12 | The server sends an error packet without SQL-state marker instead of the greeting at `max_connections` (1040) and during shutdown (1053) | to verify: no capture exists (Product Manager); a capture on the server whenever it is possible, at the latest by the first run after installation; until then residual risk in record 0118 | unverified |
| 13 | A refused `unix_socket` login answers 1698 (or 1045 for a password mismatch) with SQL-state marker | to verify: as 12 | unverified |
| 14 | `COM_PING` is answered by an OK packet of the login OK's layout | to verify: as 12 | unverified |
| 15 | The replies to the agent's capability set (fact 6 minus `CLIENT_LOCAL_FILES`, `CLIENT_MULTI_STATEMENTS`, `CLIENT_CONNECT_ATTRS`, extended `0x0d`) are byte-identical in layout to the captured ones for these statements | to verify: a `socat -x -v` capture of the built agent against the server (Product Manager, before v0.2.0 ships) | unverified |
| 16 | On Linux, connecting to a missing path gives `ENOENT`, to a socket file without listener and to a regular file `ECONNREFUSED` | to verify: the Tester's real-socket tests in `t.TempDir()` (step 5) | unverified |
| 17 | Go's unix `Dial` returns `EAGAIN` at once when the listener's backlog is full | to verify: the Dev in a scratch worktree, a listener made with `syscall.Listen(fd, 1)` and no accept, dialing until it fails (step 6); if it blocks instead, the 2-second greeting budget makes it `not_answering` anyway | unverified |
| 18 | `net.Pipe` deadlines and blocking reads work inside `testing/synctest` | `internal/sdbus/sdbus_test.go:897-925` | confirmed |
| 19 | An integer out of range of its field is `ErrMalformed` in both decoders | `docs/areas/wire-format.md:360` | confirmed |
| 20 | The C# contract test pins the list of record kinds of the golden batch; the shared decoder cases are counted per prefix | `tests/Vandox.Core.Tests/WireContractTests.cs:83,335-358` | confirmed |
| 21 | `/proc/net/tcp*` and `/proc/meminfo` are readable by any user | `docs/areas/agent.md`, *Rights* | confirmed |
| 22 | `deploy/agent/mariadb-user.sql` runs twice without an error on 12.3 | to verify: the Product Manager runs it twice at installation (step 11); the unit test pins its text only | unverified |

## Tests per criterion

`internal/mariadb` tests (package `mariadb`) use the fake server of `fakeserver_test.go` on a `net.Pipe` (`Sources.Dial`), replaying
`testdata/mariadb/` via `capture_test.go`; uncaptured replies are built in functions named `...FromDocs`; timing in `synctest` (0062).

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestCollect_UpRecordHoldsTheSelection` (capture: the exact maps of fact 7 restricted to the selection, ping latency, `complete` false, no threads, no log line); `TestSelection_LeavesOutUnusableValues` (non-numeric, over 20 digits, over 2^64 − 1, a variable of 1,025 bytes and one of 342 invalid bytes that clean to 1,026) | `internal/mariadb/collector_test.go`, `internal/mariadb/sample_test.go` |
| AC2 | `TestCollect_DownForMissingRefusedOrNonSocketPath` (real unix paths in `t.TempDir()`: none, a closed listener's file, a regular file; injected `ENOENT`/`ECONNREFUSED`); `TestCollect_LogsAvailabilityChangesOnce` (down, down, up: `mariadb down` once, `mariadb answering again` once) | `internal/mariadb/collector_test.go` |
| AC3 | `TestCollect_NotAnsweringWithinTheBudget` (synctest: no greeting → at start + 2 s; injected `EAGAIN`; silent after login at ping and at the variables statement; connection closed mid-result → at most start + 4 s, no values, no `error_code`, no error returned) | `internal/mariadb/collector_test.go` |
| AC4 | `TestCollect_BlockedProcReadBecomesATimeoutGap` (scheduler with this collector, a blocking `/proc` wrapper on `meminfo`, recording sink, synctest: one `collector_timeout` gap for `mariadb` at 5 s, no second dial while stuck, a scripted second collector keeps its schedule) | `internal/mariadb/collector_test.go` |
| AC5 | `TestCollect_ServerErrorsBecomeNotAnsweringWithCode` (FromDocs: 1040 and 1053 instead of the greeting, 1698, 1045 and 1226 at the login; the log lines and `fix` text of S4; a refusal after `up` runs; no gap) | `internal/mariadb/collector_test.go` |
| AC6 | `TestMariaDBStatus_ErrorCode` (1, 65535 with `not_answering` valid; 0, with `up`, with `down` refused with `data.error_code`); `TestEncodeBatchGolden` with the new record in `allKindRecords`; `TestDecoder_SharedCases` with the new cases; `MariaDbStatusErrorCodeIsCheckedLikeTheAgent`, `DecodeGoldenBatchAcceptsEveryKind`, `DecodeSharedCaseGivesExpectedResult` | `internal/model/mariadb_test.go`, `internal/wire/golden_test.go`, `internal/wire/encode_test.go`, `internal/wire/decode_test.go`, `tests/Vandox.Core.Tests/PayloadValidationTests.cs`, `tests/Vandox.Core.Tests/WireContractTests.cs` |
| AC7 | `TestCollect_ThreadSnapshotWhenRunningOrUnderPressure` (generated process lists in the captured column layout: `Threads_running` 11, 10, missing; 1 with `Pressure`; own id left out; order); `TestThreads_OrderAndInfoCut` | `internal/mariadb/collector_test.go`, `internal/mariadb/sample_test.go` |
| AC8 | `TestThreads_KeepsTheFirst4096AndInfoOfTheFirst64`; `TestThreads_ReducesUntilTheRecordFits` (each reduction step of *Approach*, `wire.CheckRecord` passes); `TestThreads_TextIsCleanedAndNeverLogged` (Cc, Cf, invalid UTF-8; recording `slog` handler holds none of it); `TestCollect_LargeProcessListAllocationIsBounded` (4,200 rows with 20 KiB statements: `TotalAlloc` of the run at most 32 MiB) | `internal/mariadb/sample_test.go`, `internal/mariadb/collector_test.go` |
| AC9 | `TestMemoryMax_Formula` (table: the captured values give 898,035,712; `log_bin` ON adds 151 × 32,768; missing optional summands; values near 2^64; non-numeric summand; missing required); `TestCollect_MemoryAboveRAMLogsOnTransitions` (meminfo fixture `MemTotal` below and above; meminfo error leaves only the percentage out); `TestMemTotal_ReturnsBytes` | `internal/mariadb/derived_test.go`, `internal/mariadb/collector_test.go`, `internal/sysstats/memory_test.go` |
| AC10 | `TestRates_Sequence` (exact values; first sample, after `not_answering`, `PreviousLost`, start moved +6 s with `mariadb restarted`, +5 s without, counter fell, non-positive time, zero and inconsistent read requests, pages total 0) | `internal/mariadb/derived_test.go` |
| AC11 | `TestExposedListeners_Forms` (table fixtures of `net/tcp` and `net/tcp6` with every form of AC11 and of *Approach*); `TestCollect_ExposureLogsOnChanges`; `TestTCPListeners_VisitsListeningSockets` (missing `tcp6`, unreadable line is an error, missing `tcp` is an error) | `internal/mariadb/derived_test.go`, `internal/mariadb/collector_test.go`, `internal/connstats/table_test.go` |
| AC12 | `TestQuery_RefusesMalformedReplies` (each reason of *Approach* with its exact error text: bad length, sequence, oversized control packet, column count and names, strict cell too long, `0xfb` local-file request, more-results flag, error after login); `TestCollect_StatusNameTwiceFailsTheRun`; `TestCollect_PermissionDeniedFailsTheRun` (`mariadb: connect: permission denied`); `TestPacketReader_DiscardsBeyondTheLimitWithBoundedMemory` | `internal/mariadb/client_test.go`, `internal/mariadb/collector_test.go`, `internal/mariadb/packet_test.go` |
| AC13 | `TestOpen_SendsOnlyTheUserAndNoCredential` (handshake response fields: capabilities, user, empty auth data, plugin; switch request FromDocs closes); `TestCollect_SendsOnlyFixedStatementsOverOneConnection`; `TestMariadb_ImportsOnlyAllowedPackages` | `internal/mariadb/client_test.go`, `internal/mariadb/collector_test.go` |
| AC14 | `TestLoadAgent_MariaDBDefaultsAndRules`; `TestLoadAgent_Example`; `TestJobs_MariaDBAfterSystemdOnlyWhenEnabled` | `internal/config/agent_test.go`, `internal/agent/agent_test.go` |
| AC15 | `TestUserSQL_CreatesOnlyTheProcessUser` (the exact statements of *Approach* in `deploy/agent/mariadb-user.sql`) | `internal/mariadb/client_test.go` |
| AC16 | `TestCollect_AccountCheck` (capture: no line; generated: anonymous account, no `PROCESS`, server error to the check, repeat after an `error_code` run) | `internal/mariadb/collector_test.go` |
| AC17 | `TestCapture_ReplaysByteForByte` (the helper's packets of both captures re-encode to the file's bytes); `TestOpen_CapturedLogin`; `TestQuery_CapturedResultSets` (row counts 571, 696, 2, 1, 1 and the cells of facts 7-9) | `internal/mariadb/client_test.go` |

## Approach

**Package `internal/mariadb`** (client and collector, like `sdbus` + `svcstats` in one package so that one fake serves both; the import
test pins `client.go`, `packet.go` and `statement.go` to `bufio context encoding/binary errors io net strconv syscall time` and
`collector.go`, `sample.go`, `derived.go` additionally to `fmt io/fs log/slog math math/bits net/netip os slices` and the module's
`connstats model scheduler sysstats wire`; `os` only `os.DirFS`; no `os/exec`, `unsafe`, `os.Open*`, `os.Getenv`, `os.LookupEnv`,
`crypto/tls`).

Client (`client.go`, `packet.go`, `statement.go`):
- Packets: 3-byte length, 1-byte sequence checked to increase by one (mod 256), payloads of `0xffffff` continued. Greeting payload at
  most 1 KiB; every OK, ERR, EOF, column count and column definition packet at most 64 KiB; row packets of any length, read cell by
  cell. Length-encoded integers `0xfc/0xfd/0xfe`, `0xfb` NULL in rows, `0xff` or a length beyond the packet is `malformed packet`.
- Greeting: protocol 10, NUL-terminated version (not interpreted), connection id, both scramble parts, capabilities; it must have
  `PROTOCOL_41`, `SECURE_CONNECTION`, `PLUGIN_AUTH`, bit 0 clear and extended capability `0x10`, else `unexpected reply`. `0xff` first
  is a server error at the greeting.
- Handshake response (sequence 1): capabilities `0x00aea204`, max packet `0x00100000`, charset 45, 19 zero bytes, extended capabilities
  `0x00000010`, `vandox-agent` NUL, auth data length 0, `mysql_native_password` NUL. Then OK = logged in; ERR = server error at the
  login; `0xfe` (switch) = close and `authentication switch refused`; anything else `unexpected reply`.
- `COM_PING` (`0x0e`), `COM_QUERY` (`0x03`) of the fixed statements, `COM_QUIT` (`0x01`, best effort) and close. A statement answers
  ERR (server error, step's name), `0xfb` (`local file request refused`, nothing is read), `0x00` (`unexpected reply`) or a column
  count equal to the statement's; column names compared exactly; EOF; rows until EOF; an EOF or OK with `SERVER_MORE_RESULTS_EXISTS`
  (`0x0008`) is `more results refused`.
- Cell limits (raw bytes, before any cleaning; c = cut and flagged, s = strict, longer is `cell too long`):

  | Statement | Columns and limits |
  | --------- | ------------------ |
  | `SHOW GLOBAL STATUS`, `SHOW GLOBAL VARIABLES` | `Variable_name` 64 c, `Value` 1,025 c |
  | `SHOW FULL PROCESSLIST` | `Id` 20 s, `User` `Host` `db` `Command` `State` 1,024 c, `Time` 20 s, `Info` 16,384 c, `Progress` 64 s |
  | `SELECT CURRENT_USER()` | `CURRENT_USER()` 1,024 c |
  | `SELECT PRIVILEGE_TYPE FROM information_schema.USER_PRIVILEGES WHERE GRANTEE LIKE '%vandox-agent%'` | `PRIVILEGE_TYPE` 64 c |

- Deadlines: every read and write sets the connection deadline to the earlier of the step's budget and the context's deadline;
  `context.AfterFunc` closes the connection on cancellation. A deadline error is `errTimeout`, `io.EOF`/`ECONNRESET`/`EPIPE` from the
  server is `errClosed`; neither carries server text.
- Error texts (exact; AC12 compares them): `mariadb: connect: <errno text>` (`syscall.Errno.Error()`, never Go's `OpError` text, which
  holds the path) or `mariadb: connect: failed`; `mariadb: <step>: <reason>` with step `greeting`, `login`, `ping`, `status`,
  `variables`, `processlist`, `account` and reason `malformed packet`, `packet out of sequence`, `packet too large`, `unexpected
  reply`, `unexpected columns`, `cell too long`, `local file request refused`, `more results refused`, `authentication switch refused`,
  `server error <code>`, `no answer in time`, `connection closed`; the collector adds `mariadb: status: name sent twice` and
  `mariadb: variables: name sent twice`. The server's error message is skipped by length and never read into a string.

Collector (`collector.go`, `sample.go`, `derived.go`), per run: budget context of `run.Start + 4 s` (greeting by `run.Start + 2 s`);
open, ping (latency by the monotonic clock), status, variables, the account check when due, the process list when `Threads_running`
> 10 or `run.Pressure`, close; then `sysstats.MemTotal` and, when `port` is valid, `connstats.TCPListeners`.
- Classification: dial `ENOENT`/`ECONNREFUSED` → `down`; dial `EAGAIN`, `errTimeout`, `errClosed` → `not_answering`; server error at
  the greeting or login → `not_answering` + `error_code`; any other error → the run fails (the scheduler's gap and log).
- Selection: exact ordinal names; a status value is kept when it is 1 to 20 ASCII digits within `uint64`; a variable when its cleaned
  text is at most 1,024 bytes and not cut; a name cut or seen twice ignored resp. `name sent twice`.
- Threads: own id dropped; order working (`Command` ≠ `Sleep`) first, then `time_seconds` descending, then `id` ascending; a bounded
  heap keeps the first 4,096 rows and a second the `Info` of the first 64 (an evicted info becomes empty with `truncated`); text cleaned
  like `procstats.cleanText` (invalid bytes as U+FFFD, cut at a character boundary): short fields to 1,024 bytes, `Info` to 16,384 with
  `truncated`; `Time` not 1 to 20 digits reads 0. Size contract: on `wire.RecordSizeError`, cut every `info` to 4,096, then 1,024, then
  0 bytes, then halve the thread list from its end until the record fits.
- Derived: as S7, S8, E10, E11 with 128-bit arithmetic (`math/bits`); server start = `run.Start − Uptime`; rates only when the previous
  run was `up`, not `PreviousLost`, the start moved by at most 5 s and the time is positive. Exposure: local address `Unmap()`, then
  `IsLoopback()`; every other address (wildcard, private, link-local, `::127.0.0.1`) counts; ports `port` and non-zero `extra_port`.
- State and log lines as the spec fixes them (texts, attributes, transitions); initial availability `up`, RAM state below, exposure 0,
  account check due. Metric records: source `mariadb`, the run's start, names and units of S7-S9.

`deploy/agent/mariadb-user.sql` (exact statements; comments above them say what it does, that it is run as the database
administrator, for example `mariadb < mariadb-user.sql` as root, and may be run again):

```sql
CREATE OR REPLACE USER 'vandox-agent'@'localhost' IDENTIFIED VIA unix_socket WITH MAX_USER_CONNECTIONS 2;
GRANT PROCESS ON *.* TO 'vandox-agent'@'localhost';
```

`OR REPLACE` resets a changed user to exactly this state. User-visible output is only what the spec fixes; no layout is open.

## Options considered

Options 1-8 of record 0118 (client library or own client, connection lifetime, statements, capabilities, the refusal's encoding,
statement text kept, exposure source, test data for the uncaptured replies). Further: copying the `/proc/net` and meminfo parsers into
the new package — rejected, two guards to keep in step; exporting one reader from each (chosen); two packages as `sdbus`/`svcstats` —
rejected, the capture-replaying fake would exist twice.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/mariadb/client.go`, `packet.go`, `statement.go` | new: protocol client |
| Go agent | `internal/mariadb/collector.go`, `sample.go`, `derived.go` | new: collector |
| Go agent | `internal/model/mariadb.go` | `ErrorCode *uint16`, check in a helper |
| Go agent | `internal/config/agent.go` | section `mariadb` |
| Go agent | `internal/agent/agent.go` | schedule, job, `Options.MariaDB` |
| Go agent | `internal/sysstats/memory.go`, `internal/connstats/table.go` | exported readers |
| .NET | `src/Vandox.Core/Model/MariaDbStatus.cs` | `ErrorCode` and its check |
| shared | `testdata/wire/all-kinds.jsonl`, `testdata/wire/decoder-cases.json`, `testdata/mariadb/select-status-variables-processlist.txt` | regenerated / new cases / anonymization fixed |
| deploy | `deploy/agent/mariadb-user.sql`, `deploy/agent/agent.yaml`, `deploy/agent/README.md` | new / section / documentation |

## Signatures (for the Dev's skeleton)

```go
// internal/model/mariadb.go
type MariaDBStatus struct { /* existing fields */ ErrorCode *uint16 `json:"error_code,omitempty"` } // after PingLatency
func checkErrorCode(s *MariaDBStatus) error // "data.error_code": "must be 1 to 65535", "only with not_answering"

// internal/config/agent.go
type MariaDB struct {
	Enabled bool   `yaml:"enabled"`
	Socket  string `yaml:"socket"`
}
// Agent gains `MariaDB MariaDB `yaml:"mariadb"`` after Services.
const DefaultMariaDBSocket = "/run/mysqld/mysqld.sock"
const MaxSocketPathBytes = 107
func checkSocketPath(file string, lines map[string]int, key, value string) error // reason "must be an absolute, clean path of at most 107 bytes"
// AgentKeys appends "mariadb.enabled", "mariadb.socket"; DefaultAgent sets Enabled true and DefaultMariaDBSocket.

// internal/sysstats/memory.go
func MemTotal(proc fs.FS) (uint64, error) // MemTotal of meminfo in bytes

// internal/connstats/table.go
func TCPListeners(proc fs.FS, visit func(local netip.AddrPort)) error // LISTEN sockets of net/tcp and net/tcp6; missing tcp6 skipped; missing tcp or any unread line is an error

// internal/agent/agent.go
const (
	MariaDBInterval         = 30 * time.Second
	MariaDBPressureInterval = 10 * time.Second
	MariaDBTimeout          = 5 * time.Second
)
// Options gains `MariaDB mariadb.Sources`; jobs appends the job after systemd when cfg.MariaDB.Enabled.

// internal/mariadb/client.go
const User = "vandox-agent"
type Dialer func(ctx context.Context) (net.Conn, error)
func UnixDialer(path string) Dialer
type dialError struct{ errno syscall.Errno }
func (e *dialError) Error() string
type serverError struct{ step string; code uint16 }
func (e *serverError) Error() string
type protocolError struct{ step, reason string }
func (e *protocolError) Error() string
var errTimeout, errClosed error // wrapped with the step: "mariadb: <step>: no answer in time" / "connection closed"
type conn struct{ /* net.Conn, packet reader, id */ }
func open(ctx context.Context, dial Dialer, greetingBy time.Time) (*conn, error)
func (c *conn) id() uint32
func (c *conn) ping(ctx context.Context) error
func (c *conn) query(ctx context.Context, s statement, visit func(row []cell) error) error
func (c *conn) close()

// internal/mariadb/statement.go
type statement int
const (globalStatus statement = iota + 1; globalVariables; fullProcesslist; currentUser; userPrivileges)
type column struct{ name string; limit int; cut bool }
func (s statement) text() string
func (s statement) step() string
func (s statement) columns() []column
type cell struct{ value []byte; null, cut bool } // value valid only during visit

// internal/mariadb/packet.go
type packetReader struct{ /* bufio.Reader, seq, remaining */ }
func newPacketReader(r io.Reader) *packetReader
func (p *packetReader) next(step string, limit int) ([]byte, error)        // a whole control packet up to limit
func (p *packetReader) beginRow(step string) (first byte, err error)      // starts a row or end packet
func (p *packetReader) readCell(step string, col column) (cell, error)  // a length-encoded cell of the current row
func writePacket(w io.Writer, seq byte, payload []byte) error

// internal/mariadb/collector.go
const (
	Name             = "mariadb"
	GreetingBudget   = 2 * time.Second
	RunBudget        = 4 * time.Second
	ThreadsThreshold = 10
	MaxThreads       = 4096
	MaxInfoThreads   = 64
	MaxInfoBytes     = 16384
	RestartSlack     = 5 * time.Second
)
type Sources struct {
	Dial Dialer // default UnixDialer(socket)
	Proc fs.FS  // default os.DirFS("/proc")
}
type Collector struct{ /* sources, logger, state */ }
func New(src Sources, socket string, log *slog.Logger) *Collector
func (c *Collector) Name() string
func (c *Collector) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)
func classify(err error) (availability model.MariaDBAvailability, code uint16, ok bool)

// internal/mariadb/sample.go
type sample struct{ /* status, variables, threads, ping, own id, run */ }
func selectStatus(rows func(func(name, value []byte, cut bool) error) error) (map[string]uint64, error)
func keepVariable(value []byte, cut bool) (string, bool)
type threadList struct{ /* bounded heaps */ }
func newThreadList(ownID uint64) *threadList
func (l *threadList) add(row []cell) error
func (l *threadList) threads() []model.MariaDBThread
func fitRecord(r model.Record) (model.Record, error) // the size contract steps

// internal/mariadb/derived.go
func memoryMax(vars map[string]string) (hi, lo uint64, ok bool)
func rates(prev, cur *sample) []model.MetricPoint
func bufferPoolUsed(status map[string]uint64) (float64, bool)
func exposed(addr netip.Addr) bool
func countExposed(proc fs.FS, ports []uint16) (int, error)
```

```csharp
// src/Vandox.Core/Model/MariaDbStatus.cs — after PingLatencyNs, in the Properties region
[JsonPropertyName("error_code")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public ushort? ErrorCode { get; set; }
// Validate: present and Availability != NotAnswering -> "error_code" "only with not_answering"; 0 -> "must be 1 to 65535"
```

Existing files the skeleton rewrites: none whole; the edits extend files read for this plan.

## Test files

`internal/mariadb/client_test.go`, `internal/mariadb/packet_test.go`, `internal/mariadb/collector_test.go`,
`internal/mariadb/sample_test.go`, `internal/mariadb/derived_test.go`, and the helpers `internal/mariadb/capture_test.go` (reads the
`socat -x -v` dumps into the packets of each direction) and `internal/mariadb/fakeserver_test.go`; `internal/model/mariadb_test.go`,
`internal/wire/encode_test.go` (`allKindRecords`), `internal/wire/golden_test.go`, `internal/wire/decode_test.go`,
`internal/config/agent_test.go`, `internal/agent/agent_test.go`, `internal/sysstats/memory_test.go`, `internal/connstats/table_test.go`,
`tests/Vandox.Core.Tests/PayloadValidationTests.cs`, `tests/Vandox.Core.Tests/WireContractTests.cs`.

Existing test call sites of a changed signature: `WireContractTests.DecodeGoldenBatchAcceptsEveryKind` (expected kinds, line 83) and
`SharedCasesHaveUniqueNamesAndHoldEveryRequiredCase` (case counts) — the Tester, step 5. No Go signature changes; `agent.Options`
gains a field, so no call site breaks.

Files no unit test can cover: none.

## Areas

- `docs/areas/agent.md` — new section *MariaDB* (after *Services*): schedule, what is read and sent, budgets, classification, the
  selection, thread snapshot and its bounds, the derived metrics, the account check, the log lines, failures with their texts, the
  client's bounds (security area 10), residuals of record 0118; *Connection*: the MariaDB socket as a fourth local channel; *Memory
  pressure mode*: `mariadb` (10 seconds) and its thread snapshot; *Rights*: the collector needs no right, the SQL file, the unit keeps
  `/run/mysqld` and `AF_UNIX`; status line, *Related decisions* (0118) and *Implementation*.
- `docs/areas/wire-format.md` — `mariadb_status`: `error_code` and a producer paragraph; *Metric names*: the six `mariadb_*` rows and
  that they carry the source `mariadb`; *Versioning*: 1.1 also added `error_code`.
- `docs/areas/configuration-and-secrets.md` — rows `mariadb.enabled` and `mariadb.socket` and the socket rule.
- `docs/areas/network-and-security-model.md` — *MariaDB access*: the SQL file, the account check, fixed read-only statements, the
  refusal with its error number.

## Documentation updates

| Document | Edit | Owner |
| -------- | ---- | ----- |
| the four area documents above | as *Areas* | Lead |
| `docs/ARCHITECTURE.md` | *Components*: the MariaDB collector (`internal/mariadb`) in the agent bullet and the `internal/` list; record 0118 in the records list | Lead |
| `README.md` | *Agent options*: `mariadb.enabled`, `mariadb.socket` | Dev |
| `deploy/agent/agent.yaml` | section `mariadb` with both keys at their defaults and comments | Dev |
| `deploy/agent/README.md` | section on the MariaDB collector and `mariadb-user.sql` (S10), the unit settings list (`/run/mysqld` reachable) | Dev |
| `deploy/agent/mariadb-user.sql` | new, as *Approach* | Dev |
| `.squad/project.md` | areas 6, 7, 10, 11, 12 (what the client bounds, the socket destination, the log lines, the unit setting, the SQL file), record lists | Dev |
| `.squad/project.md` | *Test doubles* row "MariaDB socket": the replaying fake server and the captures, implemented | Tester |
| `testdata/mariadb/select-status-variables-processlist.txt` | anonymization of fact 10: `144` → `192` in the three log file values, `server_uid` → `AAAAAAAAAAAAAAAAAAAAAAAAAAA=` (same lengths, hex and text columns), checked by decoding every field against the real values and the prefix `144` | Tester |

Sweep hits: "three local channels" (agent.md:48), the pressure-interval list (agent.md:611), the system-metrics source sentence
(wire-format.md:148), the options table, the components list; storage and detection enumerate no metrics or collectors.

## Architecture check

*Never blocks the agent*: the collector's budgets end every network wait by 4 s, the scheduler abandons a stuck `/proc` read at 5 s
(AC3, AC4). *No unrecorded gaps*: a failed run is a gap; `down` and `not_answering` are samples. Outbound only: the socket is local IPC.
Record 0013 holds. Backfill rule untouched.

## Security considerations

- [x] Limits: cell limits are on raw bytes before cleaning; the kept texts are then measured after U+FFFD replacement (1,024 and
  16,384 bytes, record 0114); control packet limits on raw payloads; the 1 MiB statement budget is after cleaning.
- [x] Errors reaching a log: only the fixed texts of *Approach*; no server message, path, cell or `bind_address` value.

## Decision records

- `docs/decisions/0118-mariadb-collector-hand-written-client-captured-statements-refusals-as-not-answering-with-error-code.md` (Proposed)

## Out of scope / follow-ups

As in the spec. The verification rows 12-15 and 22 are for the Product Manager on the server; nothing else follows.
