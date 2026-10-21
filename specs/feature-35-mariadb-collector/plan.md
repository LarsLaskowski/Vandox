# Plan: MariaDB collector

Source: Issue #35 | [spec.md](spec.md)
Status: Revised (1)
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
| 6 | The captured client sent capabilities `0x00bfa284` (no `CLIENT_DEPRECATE_EOF`), extended capabilities `0x1d` = progress `0x01`, bulk `0x04`, `MARIADB_CLIENT_EXTENDED_METADATA` `0x08` and `MARIADB_CLIENT_CACHE_METADATA` `0x10`, max packet bytes `00 00 10 00` = `0x00100000` (not `0x10000000`), charset 45. Result sets: a column-count packet of the count plus a "metadata follows" byte `01` (payloads `01 01`, `02 01`, `09 01`), column definitions with one extra empty field before the `0x0c` block (from `0x08`), 5-byte EOF packets (`fe 00 00 22 00`) | decoded captures (`account-check.txt` lines 11-13, 46-51; processlist capture lines 10-13, 2660-2662); bit names from the Devil's Advocate's reading of MariaDB `include/mysql_com.h`, consistent with the bytes | confirmed |
| 7 | Status: 571 rows, every status name of the selection present (e.g. `Memory_used` 483689568, `Threads_running` 1), 41 non-numeric values; variables: 696 rows, longest value 879 bytes, negative values exist (`performance_schema_*_size` = -1), every variable of the selection present plus `extra_port` 0, `socket` `/run/mysqld/mysqld.sock`, `bind_address` 127.0.0.1, `local_infile` OFF, `max_allowed_packet` 16777216; no name repeats ignoring case | decoded capture | confirmed |
| 8 | `SHOW FULL PROCESSLIST` has 9 columns `Id User Host db Command Time State Info Progress`; `db` and `Info` are NULL (`0xfb`) for the sleeping Plesk connection; the agent's own row has `Id` 22128 = the greeting's connection id | decoded capture | confirmed |
| 9 | Account check: `SELECT CURRENT_USER()` returns `vandox-agent@localhost`; the `USER_PRIVILEGES` query returns one row `PROCESS` | decoded `account-check.txt` | confirmed |
| 10 | The captures are anonymized | the real host-name label `144` in three log file values and the real `server_uid` were replaced in commit 0093b4f (`192`, `AAAAAAAAAAAAAAAAAAAAAAAAAAA=`); a decode of every field finds no `144` prefix and no other real value; ff1c0e2 still holds them on the branch (squash merge keeps them off `main`) | confirmed |
| 11 | Theoretical maximum of the captured server: 437,256,192 global + 151 × 3,051,520 = 898,035,712 bytes (`log_bin` OFF) | arithmetic on fact 7 | confirmed |
| 12 | The server sends an error packet without SQL-state marker instead of the greeting at `max_connections` (1040) and during shutdown (1053) | to verify: no capture exists (Product Manager); a capture on the server whenever it is possible, at the latest by the first run after installation; until then residual risk in record 0118 | unverified |
| 13 | A refused `unix_socket` login answers 1698 (or 1045 for a password mismatch) with SQL-state marker; a missing user may first get an authentication switch to a stand-in account's plugin (MariaDB `sql_acl.cc` `find_mpvio_user`, as the Devil's Advocate read it) | to verify: as 12; the design answers a switch with an empty payload, so both forms end in S4 | unverified |
| 14 | `COM_PING` is answered by an OK packet of the login OK's layout | to verify: as 12 | unverified |
| 15 | The replies to the agent's capability set (fact 6 minus `CLIENT_LOCAL_FILES`, `CLIENT_MULTI_STATEMENTS`, `CLIENT_CONNECT_ATTRS`, extended progress `0x01` and bulk `0x04`; both metadata bits kept) have the captured layout for these statements | to verify: a `socat -x -v` capture of the built agent against the server (Product Manager, before v0.2.0 ships); the fake refuses a handshake without the layout bits, so a drift in the agent fails the tests | unverified |
| 16 | On Linux, connecting to a missing path gives `ENOENT`, to a socket file without listener and to a regular file `ECONNREFUSED` | to verify: the Tester's real-socket tests in `t.TempDir()` (step 5) | unverified |
| 17 | Go's unix `Dial` returns `EAGAIN` at once when the listener's backlog is full | to verify: the Dev in a scratch worktree, a listener made with `syscall.Listen(fd, 1)` and no accept, dialing until it fails (step 6); if it blocks instead, the 2-second greeting budget makes it `not_answering` anyway | unverified |
| 18 | `net.Pipe` deadlines and blocking reads work inside `testing/synctest` | `internal/sdbus/sdbus_test.go:897-925` | confirmed |
| 19 | An integer out of range of its field is `ErrMalformed` in both decoders | `docs/areas/wire-format.md:360` | confirmed |
| 20 | The C# contract test pins the list of record kinds of the golden batch; the shared decoder cases are counted per prefix | `tests/Vandox.Core.Tests/WireContractTests.cs:83,335-358` | confirmed |
| 21 | `/proc/net/tcp*` and `/proc/meminfo` are readable by any user | `docs/areas/agent.md`, *Rights* | confirmed |
| 22 | `deploy/agent/mariadb-user.sql` runs twice without an error on 12.3, also through `plesk db < mariadb-user.sql` | to verify: the Product Manager runs it twice at installation (step 11); the unit test pins its text only | unverified |

## Tests per criterion

`internal/mariadb` tests (package `mariadb`) use the fake server of `fakeserver_test.go` on a `net.Pipe` (`Sources.Dial`), replaying
`testdata/mariadb/` via `capture_test.go`; uncaptured replies are built in functions named `...FromDocs`; timing in `synctest` (0062).

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestCollect_UpRecordHoldsTheSelection` (capture: the exact maps of fact 7 restricted to the selection, ping latency, `complete` false, no threads, no log line); `TestSelection_LeavesOutUnusableValues` (non-numeric, over 20 digits, over 2^64 − 1, a variable of 1,025 bytes and one of 342 invalid bytes that clean to 1,026) | `internal/mariadb/collector_test.go`, `internal/mariadb/sample_test.go` |
| AC2 | `TestCollect_DownForMissingRefusedOrNonSocketPath` (real unix paths in `t.TempDir()`: none, a closed listener's file, a regular file; injected `ENOENT`/`ECONNREFUSED`); `TestCollect_LogsAvailabilityChangesOnce` (down, down, up: `mariadb down` once, `mariadb answering again` once) | `internal/mariadb/collector_test.go` |
| AC3 | `TestCollect_NotAnsweringWithinTheBudget` (synctest: no greeting → at start + 2 s; injected `EAGAIN`; silent after login at ping and at the variables statement; connection closed mid-result → at most start + 4 s, no values, no `error_code`, no error returned) | `internal/mariadb/collector_test.go` |
| AC4 | `TestCollect_BlockedProcReadBecomesATimeoutGap` (scheduler with this collector, a blocking `/proc` wrapper on `meminfo`, recording sink, synctest: one `collector_timeout` gap for `mariadb` at 5 s, no second dial while stuck, a scripted second collector keeps its schedule) | `internal/mariadb/collector_test.go` |
| AC5 | `TestCollect_ServerErrorsBecomeNotAnsweringWithCode` (FromDocs: 1040 and 1053 instead of the greeting, 1698, 1045 and 1226 at the login, a switch followed by 1045; the log lines and `fix` text of S4; a refusal after `up` runs; no gap) | `internal/mariadb/collector_test.go` |
| AC6 | `TestMariaDBStatus_ErrorCode` (1, 65535 with `not_answering` valid; 0, with `up`, with `down` refused with `data.error_code`); `TestEncodeBatchGolden` with the new record in `allKindRecords`; `TestDecoder_SharedCases` with the new cases; `MariaDbStatusErrorCodeIsCheckedLikeTheAgent`, `DecodeGoldenBatchAcceptsEveryKind`, `DecodeSharedCaseGivesExpectedResult` | `internal/model/mariadb_test.go`, `internal/wire/golden_test.go`, `internal/wire/encode_test.go`, `internal/wire/decode_test.go`, `tests/Vandox.Core.Tests/PayloadValidationTests.cs`, `tests/Vandox.Core.Tests/WireContractTests.cs` |
| AC7 | `TestCollect_ThreadSnapshotWhenRunningOrUnderPressure` (generated process lists in the captured column layout: `Threads_running` 11, 10, missing; 1 with `Pressure`; own id left out; order); `TestThreads_OrderAndInfoCut` | `internal/mariadb/collector_test.go`, `internal/mariadb/sample_test.go` |
| AC8 | `TestThreads_KeepsTheFirst4096AndInfoOfTheFirst64`; `TestThreads_ReducesUntilTheRecordFits` (each reduction step of *Approach*, `wire.CheckRecord` passes); `TestThreads_TextIsCleanedAndNeverLogged` (Cc, Cf, invalid UTF-8; recording `slog` handler holds none of it); `TestCollect_WorstCaseProcessListIsBounded` (synctest, 4,200 rows, every kept field at its cap filled with `<`, control characters and invalid bytes, statements of 20 KiB: `TotalAlloc` of the run at most 32 MiB, the record's encoded line at most 1 MiB, and the estimate of *Approach* at least the encoded size) | `internal/mariadb/sample_test.go`, `internal/mariadb/collector_test.go` |
| AC9 | `TestMemoryMax_Formula` (table: the captured values give 898,035,712; `log_bin` ON adds 151 × 32,768; missing optional summands; values near 2^64; non-numeric summand; missing required); `TestCollect_MemoryAboveRAMLogsOnTransitions` (meminfo fixture `MemTotal` below and above; meminfo error leaves only the percentage out); `TestMemTotal_ReturnsBytes` | `internal/mariadb/derived_test.go`, `internal/mariadb/collector_test.go`, `internal/sysstats/memory_test.go` |
| AC10 | `TestRates_Sequence` (exact values; first sample, after `not_answering`, `PreviousLost`, start moved +6 s with `mariadb restarted`, +5 s without, counter fell, non-positive time, zero and inconsistent read requests, pages total 0) | `internal/mariadb/derived_test.go` |
| AC11 | `TestExposedListeners_Forms` (table fixtures of `net/tcp` and `net/tcp6` with every form of AC11 and of *Approach*); `TestCollect_ExposureLogsOnChanges`; `TestTCPListeners_VisitsListeningSockets` (missing `tcp6`, unreadable line is an error, missing `tcp` is an error) | `internal/mariadb/derived_test.go`, `internal/mariadb/collector_test.go`, `internal/connstats/table_test.go` |
| AC12 | `TestQuery_RefusesMalformedReplies` (each reason of *Approach* with its exact error text: bad length, sequence, oversized control packet, column count and names, strict cell too long, `0xfb` local-file request, more-results flag, error after login); `TestCollect_StatusNameTwiceFailsTheRun`; `TestCollect_PermissionDeniedFailsTheRun` (`mariadb: connect: permission denied`); `TestPacketReader_DiscardsBeyondTheLimitWithBoundedMemory` (a 64 MiB cell: at most 4 MiB allocated); `TestCollect_LongStatusAndVariablesRepliesAreBounded` (synctest, 1,000,000 distinct names in each reply: at most 4 MiB allocated each, the record holds only the selection) | `internal/mariadb/client_test.go`, `internal/mariadb/collector_test.go`, `internal/mariadb/packet_test.go` |
| AC13 | `TestOpen_SendsOnlyTheUserAndNoCredential` (handshake response fields: capabilities `0x00aea204`, extended `0x18`, user, empty auth data, plugin; a switch FromDocs gets one empty payload and no credential, a second switch ends the run with `authentication switch refused`); `TestCollect_SendsOnlyFixedStatementsOverOneConnection`; `TestMariadb_ImportsOnlyAllowedPackages` (the allow-lists and the dial pin of *Approach*) | `internal/mariadb/client_test.go`, `internal/mariadb/collector_test.go` |
| AC14 | `TestLoadAgent_MariaDBDefaultsAndRules`; `TestLoadAgent_Example`; `TestJobs_MariaDBAfterSystemdOnlyWhenEnabled` | `internal/config/agent_test.go`, `internal/agent/agent_test.go` |
| AC15 | `TestUserSQL_CreatesOnlyTheProcessUser` (the exact statements of *Approach* in `deploy/agent/mariadb-user.sql`) | `internal/mariadb/client_test.go` |
| AC16 | `TestCollect_AccountCheck` (capture: no line; generated: anonymous account, no `PROCESS`, server error to the check, repeat after an `error_code` run) | `internal/mariadb/collector_test.go` |
| AC17 | `TestCapture_ReplaysByteForByte` (the helper's packets of both captures re-encode to the file's bytes); `TestCapture_FakeRequiresTheCapturedLayoutBits` (a handshake without `PROTOCOL_41`, `SESSION_TRACK`, `PLUGIN_AUTH_LENENC_CLIENT_DATA`, extended `0x08` or `0x10`, or with `DEPRECATE_EOF`, is refused by the fake before it replays); `TestOpen_CapturedLogin`; `TestQuery_CapturedResultSets` (row counts 571, 696, 2, 1, 1 and the cells of facts 7-9) | `internal/mariadb/client_test.go` |

## Approach

**Package `internal/mariadb`** (client and collector in one package so that one fake serves both). The import test pins `client.go`,
`packet.go` and `statement.go` to `bufio context encoding/binary errors io net strconv syscall time`, and `collector.go`, `sample.go`,
`derived.go` additionally to `container/heap fmt io/fs log/slog math math/bits net/netip os slices unicode/utf8` and the module's
`connstats model procstats scheduler sysstats wire`; `os` only `os.DirFS`; no `os/exec`, `unsafe`, `os.Open*`, `os.Getenv`,
`os.LookupEnv`, `crypto/tls`. As `TestSdbus_ImportsOnlyAllowedPackages` does, it pins the only dial: `(*net.Dialer).DialContext` with
the constant network `"unix"` in `UnixDialer`, and no `net.Listen*`, `net.Dial` with another network, `SyscallConn`, `File`,
`ReadMsgUnix` or `ReadFrom*`.

Client (`client.go`, `packet.go`, `statement.go`):
- Packets: 3-byte length, 1-byte sequence checked to increase by one (mod 256), payloads of `0xffffff` continued. Greeting payload at
  most 1 KiB; every OK, ERR, EOF, column count and column definition packet at most 64 KiB; row packets of any length, read cell by
  cell. Length-encoded integers `0xfc/0xfd/0xfe`, `0xfb` NULL in rows, `0xff` or a length beyond the packet is `malformed packet`.
- Greeting: protocol 10, NUL-terminated version (not interpreted), connection id, both scramble parts, capabilities; it must have
  `PROTOCOL_41`, `SECURE_CONNECTION`, `PLUGIN_AUTH`, `SESSION_TRACK`, bit 0 clear and extended capabilities `0x08` and `0x10`, else
  `unexpected reply`. `0xff` first is a server error at the greeting.
- Handshake response (sequence 1): capabilities `0x00aea204` (the captured `0x00bfa284` without `LOCAL_FILES`, `MULTI_STATEMENTS`,
  `CONNECT_ATTRS`), max packet `0x00100000`, charset 45, 19 zero bytes, extended capabilities `0x00000018` (the captured `0x1d` without
  progress and bulk), `vandox-agent` NUL, auth data length 0, `mysql_native_password` NUL. Then OK = logged in; ERR = server error at
  the login; `0xfe` (switch, at most 1 KiB, plugin name and data never kept or logged) = one empty response packet, then the next
  packet is read as the login's answer (OK, ERR as above); a second `0xfe` or a `0x01` request = `authentication switch refused`;
  anything else `unexpected reply`. No credential is ever sent.
- `COM_PING` (`0x0e`), `COM_QUERY` (`0x03`) of the fixed statements, `COM_QUIT` (`0x01`, best effort) and close. A statement answers
  ERR (server error, step's name), `0xfb` (`local file request refused`, nothing is read), `0x00` (`unexpected reply`) or a column
  count packet: the count, equal to the statement's, and a "metadata follows" byte that must be `1` (else `unexpected reply`); column
  definitions with the extended-metadata field; column names compared exactly; EOF; rows until EOF; an EOF or OK with `SERVER_MORE_RESULTS_EXISTS`
  (`0x0008`) is `more results refused`.
- Cell limits (raw bytes, before any cleaning; c = cut and flagged, s = strict, longer is `cell too long`):

  | Statement | Columns and limits |
  | --------- | ------------------ |
  | `SHOW GLOBAL STATUS`, `SHOW GLOBAL VARIABLES` | `Variable_name` 64 c, `Value` 1,025 c |
  | `SHOW FULL PROCESSLIST` | `Id` 20 s, `User` 512 c, `Host` 512 c, `db` 256 c, `Command` 32 c, `Time` 20 s, `State` 256 c, `Info` 16,384 c, `Progress` 64 s |
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
- Selection: the cells are read into two fixed scratch buffers of the reader (64 and 1,025 bytes), never allocated per row; a name is
  compared with the fixed selection (14 status, 20 variable slots) and only a match is kept, so a reply of any length keeps at most 34
  values; a selected name met twice is `name sent twice`, every other name is never kept or remembered. A status value counts when it
  is 1 to 20 ASCII digits within `uint64`; a variable when it is not cut and its cleaned text is at most 1,024 bytes.
- Threads: `Id` must be 1 to 20 ASCII digits within `uint64` (anything else, NULL included, is `unexpected reply`, so the own row is
  always recognized); own id dropped; order working (`Command` ≠ `Sleep`) first, then `time_seconds` descending, then `id` ascending.
  The fields before `Info` are read into scratch buffers; the rank is known when `Time` is read, and the row is copied only when it
  ranks among the first 4,096, into a pool of 4,096 preallocated slots (an evicted slot is reused); `Info` is kept only when the row
  ranks among the first 64, into a pool of 64 buffers of 16 KiB; otherwise it is discarded by length (an evicted info becomes empty
  with `truncated`). Heaps by `container/heap`. No allocation per row after the pools: at most 4,096 × 1,568 + 64 × 16,384 bytes,
  about 7.2 MiB. When the record is built, text is cleaned by `procstats.CleanText` (exported wrapper of `cleanText`; one guard, not
  a copy): short fields to 1,024 bytes, `Info` to 16,384 with `truncated`; `Time` not 1 to 20 digits reads 0.
- Size contract without oversized encoding: `estimate` bounds the record's encoded line before encoding — 6 bytes per byte of every
  kept text (the longest JSON escape, `\u00XX`, for `<`, `>`, `&` and control characters; U+FFFD and U+2028/2029 are smaller per
  byte), 21 bytes per number, 256 bytes per thread for keys and punctuation, plus the status and variable maps by the same rule.
  `fitThreads` applies the steps in order — every `info` to 4,096, 1,024, then 0 bytes, then the thread list halved from its end —
  until the estimate is at most 1 MiB, by estimate only; then `wire.CheckRecord` encodes the record once. The estimate is an upper
  bound, so that one encoding is at most 1 MiB and passes.
- Derived: as S7, S8, E10, E11 with 128-bit arithmetic (`math/bits`); server start = `run.Start − Uptime`; rates only when the previous
  run was `up`, not `PreviousLost`, the start moved by at most 5 s and the time is positive. Exposure: local address `Unmap()`, then
  `IsLoopback()`; every other address (wildcard, private, link-local, `::127.0.0.1`) counts; ports `port` and non-zero `extra_port`.
- State and log lines as the spec fixes them (texts, attributes, transitions); initial availability `up`, RAM state below, exposure 0,
  account check due. Metric records: source `mariadb`, the run's start, names and units of S7-S9. The account check reads only the
  current account's rows (`GRANTEE LIKE '%vandox-agent%'`, and MariaDB shows an account without privileges on `mysql` only its own).

`deploy/agent/mariadb-user.sql` (exact statements; comments above them say what it does, that it is run as the database
administrator — `mariadb < mariadb-user.sql` as root, or `plesk db < mariadb-user.sql` on Plesk — and may be run again):

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
| shared | `testdata/wire/all-kinds.jsonl`, `testdata/wire/decoder-cases.json` | regenerated / new cases |
| Go agent | `internal/procstats/cmdline.go` | exported `CleanText` |
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

// internal/procstats/cmdline.go
func CleanText(s string, limit int) (text string, cut bool) // exported wrapper of cleanText; callers unchanged

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
func (c *conn) query(ctx context.Context, s statement, row func(p *packetReader) error) error // row reads the cells of one row
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
type threadList struct{ /* slot pools and heaps */ }
func newThreadList(ownID uint64) *threadList
func (l *threadList) visit(p *packetReader, step string) error // reads one row cell by cell into the pools
func (l *threadList) threads() []model.MariaDBThread
func estimate(s *model.MariaDBStatus) int                    // upper bound of the encoded line
func fitThreads(s *model.MariaDBStatus, limit int)            // the reduction steps, by estimate only

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
  client's bounds (security area 10), that the account check reads the current account only, that statement texts can hold customer
  data and credentials, residuals of record 0118; *Connection*: the MariaDB socket as a fourth local channel; *Memory
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
| `deploy/agent/README.md` | section on the MariaDB collector and `mariadb-user.sql` (S10: both ways to run it, that recorded statement texts can hold customer data and credentials and reach the backend), the unit settings list (`/run/mysqld` reachable) | Dev |
| `deploy/agent/mariadb-user.sql` | new, as *Approach* | Dev |
| `.squad/project.md` | areas 6, 7, 10, 11, 12 (what the client bounds, the socket destination, the log lines, the unit setting, the SQL file), record lists | Dev |
| `.squad/project.md` | *Test doubles* row "MariaDB socket": the replaying fake server and the captures, implemented | Tester |

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

## Challenge

- M1 (major, extended metadata bits) — accepted. Fact 6 was misread: `0x08` is extended metadata and `0x10` cache metadata, which adds
  the "metadata follows" byte to every column count (captured `01 01`, `09 01`). The client now sends extended `0x18`, requires
  `0x08` and `0x10` in the greeting, reads the column count with that byte (must be `1`); the fake refuses a handshake without the
  layout bits before replaying (`TestCapture_FakeRequiresTheCapturedLayoutBits`); facts 6 and 15 and record 0118 option 4 corrected.
- M2 (major, a missing user as an authentication switch) — accepted, first fix: one empty response to a switch, then the next packet
  is the login's answer (ERR → S4 with its number, OK → S12's check); a second switch fails the run. Spec E6, AC5 and AC13 amended; fact
  13 notes the switch. No credential is ever sent.
- m1 (stale anonymization task) — accepted: fact 10 confirmed by 0093b4f; task 1 is a check only; the testdata row left the
  documentation table.
- m2 (max packet value) — rejected: the bytes are `00 00 10 00` (capture line 11), little-endian `0x00100000`; fact 6 now quotes them.
- m3 (cheap captures) — accepted as a request, not as a dependency: the ping, a 1698 login, a missing user and a 1226 login can be
  captured harmlessly; the orchestrator asks the Product Manager for them (commands in the Lead's result). If they arrive before step 5,
  the Tester replays them instead of the matching `...FromDocs` builders and facts 13 and 14 close; otherwise the plan stands, because
  every variant (switch or not, either code) ends in S4 or a gap with a fixed reason.
- m4 (allow-list, cleaning) — accepted: `unicode/utf8` and `container/heap` allowed; `procstats.CleanText` is exported, not copied.
- m5 (`plesk db`) — accepted: the SQL comment and the deploy README name `plesk db < mariadb-user.sql`; fact 22 includes it.

## Security review

- B1 (memory bound for any reply) — resolved. (1) Duplicates are checked only among the 34 fixed selection slots; other names are
  never kept (*Approach*, Selection). (2) The thread list reads into fixed scratch buffers and preallocated pools (about 7.2 MiB at
  most); short fields are capped at 512/512/256/32/256 bytes (spec E8); `fitThreads` reduces by an upper-bound estimate (6 bytes per
  kept byte) before the single encoding, so no line far above 1 MiB is ever encoded. (3) AC8 and AC12 gain the allocation tests:
  `TestCollect_WorstCaseProcessListIsBounded` (4,200 rows, every field at its cap with `<`, control characters and invalid bytes: at
  most 32 MiB allocated, line at most 1 MiB, estimate at least the encoded size), `TestCollect_LongStatusAndVariablesRepliesAreBounded`
  (1,000,000 names each: at most 4 MiB), `TestPacketReader_DiscardsBeyondTheLimitWithBoundedMemory` (64 MiB cell: at most 4 MiB).
- Note 1 (account check scope) — adopted in *Approach* and the agent area.
- Note 2 (dial pin) — adopted in the import test.
- Note 3 (sensitive statement text) — adopted: spec S10, the deploy README, the agent area and record 0118 *Consequences*.
- Note 4 (non-digit `Id`) — adopted: `unexpected reply`, the run fails.
- Note 5 (history of ff1c0e2) — noted in fact 10; deleting the branch after the merge needs the user's approval.
- Note 6 — nothing to do.

