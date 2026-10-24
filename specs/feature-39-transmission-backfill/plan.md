# Plan: Transmission and backfill

Source: Issue #39 | [spec.md](spec.md)
Status: Draft
Tier: security — the change creates the agent's outbound client that carries the agent token (security areas 8 and 11: who receives the credential, no proxy, no redirect, certificate verification, timeouts), which are security controls.
Security areas touched: 1 Ingest authentication (only the request contract #40 implements: bearer token, status classes; `docs/areas/backend-host.md`), 8 Secrets handling (`VANDOX_AGENT_TOKEN` sent in the `Authorization` header by `internal/sender/transport.go`), 9 File writes (the sender's acknowledgements make `internal/spool` delete data files; new read-only `Spool.AckRanges`), 10 Parsing of external input (the HTTP answer: status and headers only, body bounded and discarded; spooled lines re-encoded by `wire.EncodeBatch`), 11 Outbound calls (`internal/sender.HTTPTransport`), 12 Logging (the sender's fixed log lines). No Docker/CI or build file, no dependency (standard library only).

## Problem / root cause

See [spec.md](spec.md), *Problem*: nothing reads the spool, so no record reaches the backend.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `Spool.Read(after, maxRecords, maxBytes)` returns only kept, unacknowledged records above `after`, in order, of one capture context, and returns empty when the first record exceeds `maxBytes`; `maxBytes` counts lines without the line feed | `internal/spool/read.go:80-101`, `docs/areas/agent.md:1598-1603` | confirmed |
| 2 | The spool keeps two read cursors, so a live and a backfill reader do not evict each other's position | `internal/spool/spool.go:140` (`cursors [2]cursor`), `read.go:282-298` | confirmed |
| 3 | `Spool.Ack` refuses with `ErrTooManyRanges` when more than 64 ranges would lie above the oldest kept number (`countAbove(merged, oldestKept())`), and with `errClosed` once closing | `internal/spool/ack.go:129-157`, `spool.go:19,38` | confirmed |
| 4 | `Ack(first, last)` acknowledges every issued, kept, unacknowledged number of the range; for a range spanning one `Read` result (or a contiguous slice of it) those are exactly the result's records | `ack.go:102-116` (`issued`), fact 1 | confirmed |
| 5 | Records read back carry origin `agent` and pass `wire.Batch.Validate`, so `wire.EncodeBatch` accepts a `Read` result; the header's `boot_id`/`clock_offset_ns` come from `spool.Batch` | `internal/wire/decode.go:525`, `wire.go:128-153`, `spool.go:69-78`; a record the model rules refuse after all is handled as a refused record (*Approach*) | confirmed |
| 6 | A `gap` with cause `unknown` and `first_seq` = `last_seq` validates | `internal/model/gap.go:33-63` | confirmed |
| 7 | `selfstats.SendLagReporter.SendLag(ctx) (time.Duration, error)` exists and a negative lag fails the sample | `internal/selfstats/selfstats.go:56-58,137-144` | confirmed |
| 8 | The memory pressure switch is one callback `System.SetPressureSwitch(func(bool))`, today connected to `Scheduler.SetPressure` only | `internal/agent/agent.go:401`, `internal/sysstats/system.go:44` | confirmed |
| 9 | The agent stops the scheduler (`wg.Wait`) before `store.Flush` and `closeSpool`, so a goroutine added to `wg` stops before the spool closes | `internal/agent/agent.go:150-166` | confirmed |
| 10 | Agent tests that run with a real spool build their options in `spoolRunOptions` (`internal/agent/agent_test.go:1704`) and run in `synctest` bubbles; a sender with the real HTTP transport there would dial `100.64.0.1:8081` | `agent_test.go:45,1704-1730` | confirmed |
| 11 | `parseSize` knows only `MiB`/`GiB`; no KiB parser exists | `internal/config/config.go:153-181` | confirmed |
| 12 | The `logs` collector hands over up to 1,000 records and 4 MiB of text per 2-second run | `internal/logship/logship.go:28-31`, `agent.go` `LogsInterval` | confirmed |
| 13 | Today's backend publishes no ingest port and its ingest listener answers `404` | `docs/areas/backend-host.md:66-68,124` | confirmed |
| 14 | Go's `http.Transport` with `Proxy: nil` ignores `HTTP_PROXY`/`HTTPS_PROXY`; `CheckRedirect` returning `http.ErrUseLastResponse` returns the 3xx answer unfollowed; `MaxResponseHeaderBytes` bounds the header read; `DisableCompression` stops the transparent `Accept-Encoding: gzip` | to verify: (source: Go `net/http` documentation) Tester, step 5, by `TestHTTPTransport_IgnoresProxyEnvironment`, `_FollowsNoRedirect`, `_RefusesOversizedHeaders` over loopback | unverified |
| 15 | The sender's peak heap growth with a full batch in flight on both streams stays below 16 MiB and does not grow with the backlog | to verify: (estimate: decoded records about 3x their 1 MiB of lines, the 1 MiB plain buffer of `EncodeBatch`, its gzip output and writer, per stream) Tester writes `TestSender_MemoryBound` (step 6, resource bound); Dev records the measured figure in `docs/areas/agent.md` (*Memory*); above 16 MiB the Dev reports it and the Lead decides | unverified |
| 16 | At the default `256KiB` per second of compressed bodies the backfill delivers between 1,000 and 8,500 records a second of the reference stream (below the backend's measured HDD throughput, and a 13-hour backlog within 30 minutes) | to verify: (the gzip ratio of the stream is not measured) Tester writes `TestReferenceStream_BackfillRate` in `internal/spool/refstream_test.go` (step 6), which encodes one scaled day of the reference stream in 1 MiB batches with `wire.EncodeBatch` and logs the ratio and the rate; Dev records both in `docs/areas/agent.md` (*Capacity*); outside the band the Lead revisits the default | unverified |
| 17 | Spec claim "the header's boot ID is the capture's, not the sending boot's" | record 0046, `spool.Batch.BootID` | confirmed |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestSender_LiveRecordsSentWithinTenSeconds`, `TestSender_BatchCarriesCaptureContext` | `internal/sender/sender_test.go` |
| AC2 | `TestSender_WaitsDoubleWithJitterUpToSixtySeconds`, `TestSender_OneWarningPerEpisodeAndReason`, `TestSender_ReasonPerAnswer`; `TestHTTPTransport_ReasonPerFailure`; `TestRun_StartsWithUnreachableBackend` | `internal/sender/sender_test.go`; `internal/sender/transport_test.go`; `internal/agent/agent_test.go` |
| AC3 | `TestSender_ThirteenHourOutageDeliversEverySequenceNumber`, `TestSender_OutageDuringBackfillStillDeliversEverything` | `internal/sender/sender_test.go` |
| AC4 | `TestSender_LiveRateDuringBackfill`, `TestSender_AboveLiveRateStaysInOrder` | `internal/sender/sender_test.go` |
| AC5 | `TestThrottle_BytesPerWindow`; `TestSender_BackfillThrottledLiveNot` | `internal/sender/throttle_test.go`; `internal/sender/sender_test.go` |
| AC6 | `TestLoadAgent_BackfillBandwidth`, `TestLoadAgent_Example` (existing; fails until the example sets the key); `TestParseBandwidth` | `internal/config/agent_test.go`; `internal/config/config_test.go` |
| AC7 | `TestSender_StartWithBacklogSendsLiveFirst`, `TestSender_LostAnswerSentAgain` | `internal/sender/sender_test.go` |
| AC8 | `TestSender_RefusedRecordIsolatedAndGivenUp`, `TestSender_EleventhRefusalInAnHourStartsEpisode` | `internal/sender/sender_test.go` |
| AC9 | `TestSender_HangingRequestEndsAfterThirtySeconds`; `TestHTTPTransport_DeadlineIsTimeout` | `internal/sender/sender_test.go`; `internal/sender/transport_test.go` |
| AC10 | `TestHTTPTransport_FollowsNoRedirect`, `TestHTTPTransport_IgnoresProxyEnvironment`, `TestHTTPTransport_RefusesUntrustedCertificate`, `TestHTTPTransport_SendsTokenOnlyToBackend`, `TestHTTPTransport_ReadsAtMostFourKiBOfBody`, `TestHTTPTransport_RefusesOversizedHeaders`; `TestSender_LogsHoldNoURLTokenOrBody` | `internal/sender/transport_test.go`; `internal/sender/sender_test.go` |
| AC11 | `TestSender_AcknowledgementRefusedLoggedOncePerEpisode`, `TestSender_SpoolReadFailureRetried` | `internal/sender/sender_test.go` |
| AC12 | `TestSender_BackfillPausedUnderMemoryPressure`; `TestNewScheduler_PressureReachesSender` | `internal/sender/sender_test.go`; `internal/agent/agent_test.go` |
| AC13 | `TestSender_LargestRecordSentAlone`, `TestSender_BatchBounds` | `internal/sender/sender_test.go` |
| AC14 | `TestSender_StopEndsWithinTwoSeconds`; `TestRun_SenderStopsBeforeSpoolCloses` | `internal/sender/sender_test.go`; `internal/agent/agent_test.go` |
| AC15 | `TestSender_SendLag` | `internal/sender/sender_test.go` |
| AC16 | `TestSender_FlapsAndRefusalsKeepRangesBounded`; `TestAckRanges_CountsRangesAboveOldestKept` | `internal/sender/sender_test.go`; `internal/spool/ack_test.go` |

Sender tests run in `testing/synctest` bubbles with a real spool in `t.TempDir()` and the fake transport (record 0062: no network,
fake time; the 30-second and 60-second waits elapse in fake time). Transport tests run over `httptest` loopback servers with
injected short deadlines (about 50 ms) per record 0062, never `time.Sleep`. AC3's stream is generated in the sender's tests
(varied metrics, log lines and snapshots), because the reference stream generator is test code of `internal/spool`.

## Approach

**Package `internal/sender`** (new), three files:

- `transport.go` — `HTTPTransport.Send(ctx, body)`: `POST <scheme>://<host>[:port]/v1/batches` (the base URL's path is empty or
  `/`, so the path is always exactly `IngestPath`), headers `Authorization: Bearer <token>`, `Content-Type: application/gzip`,
  `User-Agent: vandox-agent/<version.Version>`, `Content-Length`; no `Content-Encoding`. `http.Client` with `CheckRedirect`
  returning `http.ErrUseLastResponse` and an own `http.Transport`: `Proxy: nil`, `MaxResponseHeaderBytes: 16 KiB`,
  `DisableCompression: true`, `MaxConnsPerHost: 2`, `ForceAttemptHTTP2: false`, default TLS verification with system roots
  (`TLSClientConfig` only sets `MinVersion: tls.VersionTLS12`; never `InsecureSkipVerify`). The answer body is read through
  `io.LimitReader(body, 4096)` and closed; nothing of it is kept. The deadline comes from the caller's context. Classification
  into `Result{Status, Reason}`: a status → `Status` (and `Reason` `token refused` for 401/403, `status` for other non-2xx except
  400/413/422); no status → `timeout` (`context.DeadlineExceeded` or a `net.Error` with `Timeout()`), `tls failed`
  (`*tls.CertificateVerificationError`, `x509` errors, `tls.RecordHeaderError`, `tls.AlertError`), else `connection failed`;
  `context.Canceled` of the parent → `Result{Reason: ""}` with `Outcome` `Stopped`. No error text, URL or host leaves `Send`.
- `throttle.go` — `throttle` with `next time.Time`: `wait(ctx, n)` blocks until `next`, then sets `next = max(next, now) +
  n/bytesPerSecond`; so the bodies of any span T sum to at most B·T plus one batch.
- `sender.go` — `Sender` with two goroutines started by `Run`, a shared `episode` (mutex-guarded: active flag, reason, status,
  start time, current wait), the live position, the backfill position, the give-up window and the pressure flag.
  - **Live stream**: a 5-second ticker (`SendInterval`; 5 s plus up to ten 0.5-second requests at the live rate stays within the
    spec's 10 s); at each tick it reads `Read(liveAfter, 20000, 1 MiB)` and sends batch after batch until a read returns
    nothing. After an acknowledgement `liveAfter` = the batch's last number.
  - **Episode** (any `NotAccepting` result of either stream, or `Refused` beyond the give-up bound): warning logged on start and
    on a change of reason or status; the backfill stream parks; the live stream waits `d` drawn uniformly from [d/2, d] with
    `Rand` (d = 1 s, doubling per failed attempt, at most 60 s), then makes one attempt: a `live` batch read after `mark`, where
    `mark` is `LastSeq()` taken at the start of the previous attempt (the first attempt reads after `liveAfter`), unless
    `AckRanges() >= MaxHoles` (32), in which case it reads after `liveAfter` (no jump). No record since `mark` → no attempt, wait
    again. The first acknowledgement ends the episode: info `backend accepting batches again` with `seconds` (monotonic,
    rounded down), `liveAfter` moves to the batch's last number, `liveStart` = the batch's first number, and the backfill stream
    is released.
  - **Agent start** counts as an episode end that has not happened yet: `liveAfter` = `LastSeq()` at `Run`, the backfill stays
    parked until the first live acknowledgement (S6).
  - **Backfill stream**: released, it reads `Read(bfAfter, 20000, 1 MiB)` from `bfAfter` = 0 (acknowledged records are skipped by
    the spool) and cuts the result before the first record at or above `liveStart`; an empty cut ends the pass (info `backfill
    finished` with `records`, only when the pass sent any; info `backfill started` before its first batch). Each batch waits for
    the throttle and for the pressure flag to be off (info `backfill paused` / `backfill resumed` on the change, only while a pass
    runs). After an acknowledgement `bfAfter` = the batch's last number. An episode resets `bfAfter` to 0 for the next pass.
  - **Request**: each `Send` gets `context.WithTimeout(ctx, RequestTimeout)` (30 s). Body: `wire.EncodeBatch` into a per-stream
    reused buffer, header `format` 1.1, the batch's agent ID, boot ID and offset, `mode` `live` or `backfill`.
  - **Acknowledgement**: `Ack(first, last)` of the batch's (or piece's) first and last number; `ErrTooManyRanges` → warning
    `acknowledgement not kept` (`err`) once until an `Ack` succeeds; `spool.ErrClosed` or a cancelled context → nothing logged.
  - **Refused** (400/413/422): the records are split into halves sent in order, recursively; a refused single record is given up
    if fewer than 10 were given up in the last hour (monotonic timestamps): `Put` of `gap{cause unknown, source agent,
    first_seq = last_seq = seq, from = captured_at, to = from + 1 s}` captured now, then `Ack(seq, seq)` (a failed `Put` leaves the
    record unacknowledged), warning `record refused` (`status`, `seq`, `kind`). The eleventh refusal starts an episode with
    reason `refused` and `status`. A batch `EncodeBatch` refuses is split the same way and a single record it refuses is given up
    with `status` 0.
  - **Read failure**: warning `spool not readable` (`err`, the spool's error text) once per failure episode of that stream; the
    stream waits as in the backoff (own schedule) and retries; the other stream goes on.
  - **SendLag**: `time.Now()` minus the capture time of the newest record acknowledged since `Run` (before the first: minus the
    time `Run` began), never negative (0 when the capture time lies ahead).
  - **SetPressure(high)**: stores the flag (atomic); wired in `newScheduler` beside `Scheduler.SetPressure`.
  - **Stop**: `Run` returns once both goroutines ended; every request's context derives from `ctx`, so cancellation ends them at
    once; no waits beyond that (well within 2 s).
- `internal/spool`: `AckRanges() int` returns `countAbove(s.acked, s.oldestKept())` under the lock (the measure the cap uses);
  `errClosed` becomes exported `ErrClosed` (same text).
- `internal/config`: `backend.backfill_bandwidth` (default `256KiB`), `parseBandwidth` (one to five digits without a leading zero,
  then `KiB` or `MiB`), range 16 KiB to 1024 MiB, reason text exactly `must be a whole number of KiB or MiB from 16KiB to 1024MiB,
  such as 256KiB or 4MiB`, checked right after `backend.url`; `AgentKeys` lists it after `backend.url`.
- `internal/agent`: `Options.Transport` (nil → `sender.NewHTTPTransport(cfg.Backend.URL, cfg.Secrets.AgentToken.Value())`);
  with a real spool `Run` builds the sender, sets `opts.Self.Sender`, passes `snd.SetPressure` to `newScheduler`, starts
  `snd.Run(ctx)` in `wg`; a transport that cannot be built is `agent cannot start` with `sender: backend url: not usable`.

**Log lines** (layout fixed here; `component=sender`, keys in this order): warning `backend not accepting batches` `reason=<reason>`
[`status=<code>`]; info `backend accepting batches again` `seconds=<n>`; info `backfill started`; info `backfill finished`
`records=<n>`; info `backfill paused` `reason=memory_pressure`; info `backfill resumed`; warning `record refused` `status=<code>`
`seq=<n>` `kind=<kind>`; warning `acknowledgement not kept` `err=<spool error>`; warning `spool not readable` `err=<spool error>`.

## Options considered

- Streams, refused records, the reconnect bound and the throttle: record 0125 (one stream; alternating stream; retry refused
  batches; give up whole batches; raise the spool's cap; never jump).
- Request and acknowledgement: record 0124 (acknowledgement body; streaming upload; `Content-Encoding`; token in URL or custom
  header).
- `SendLag` through the existing `SendLagReporter` rather than a new interface returning a capture time: the existing one needs no
  change in `internal/selfstats` and is equal to the spec's definition in the self-metrics run (no time passes between the run's
  start and the call in practice; in tests both read the bubble's clock).
- Send interval 10 s (the spec's bound) was rejected for 5 s: at the live rate ten back-to-back requests follow a tick, so a 10 s
  tick could exceed the bound.
- A hole count kept by the sender itself rather than `Spool.AckRanges`: rejected, the spool's own count is exactly what its cap
  refuses on, including ranges after a restart.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| agent | `internal/sender/sender.go`, `throttle.go`, `transport.go` | new package |
| agent | `internal/spool/ack.go`, `spool.go` | `AckRanges`; `ErrClosed` exported |
| agent | `internal/config/agent.go`, `config.go` | `backend.backfill_bandwidth`, `parseBandwidth` |
| agent | `internal/agent/agent.go` | `Options.Transport`, sender wiring, `newScheduler` pressure callbacks |
| agent | `deploy/agent/agent.yaml` | the new key with its comment |

## Signatures (for the Dev's skeleton)

```go
package sender

const (
	IngestPath        = "/v1/batches"
	SendInterval      = 5 * time.Second
	RequestTimeout    = 30 * time.Second
	FirstWait         = time.Second
	MaxWait           = 60 * time.Second
	MaxBatchBytes     = 1 << 20
	MaxBatchRecords   = 20000
	MaxHoles          = 32
	MaxGivenUpPerHour = 10
	MaxAnswerBody     = 4096
	MaxAnswerHeader   = 16 << 10
)

// Outcome is how an answer is treated.
type Outcome int

const (
	Acknowledged Outcome = iota // 2xx
	Refused                     // 400, 413, 422
	NotAccepting                // every other answer and every failure
	Stopped                     // the caller's context was cancelled
)

// Result is what one request came back with.
type Result struct {
	Status int    // HTTP status; 0 without an answer
	Reason string // "", "timeout", "connection failed", "tls failed", "token refused", "status"
}

func (r Result) Outcome() Outcome

// Transport sends one encoded batch.
type Transport interface {
	Send(ctx context.Context, body []byte) Result
}

type HTTPTransport struct{ /* unexported */ }

func NewHTTPTransport(baseURL, token string) (*HTTPTransport, error)
func (t *HTTPTransport) Send(ctx context.Context, body []byte) Result

// Spool is what the sender needs of the spool.
type Spool interface {
	Read(ctx context.Context, after uint64, maxRecords int, maxBytes int64) (spool.Batch, error)
	Ack(ctx context.Context, first, last uint64) error
	Put(ctx context.Context, records []model.Record) error
	LastSeq() uint64
	AckRanges() int
}

type Options struct {
	Spool     Spool
	Transport Transport
	Bandwidth int64          // bytes per second of backfill request bodies
	Rand      func() float64 // in [0, 1); nil: math/rand/v2.Float64
}

type Sender struct{ /* unexported */ }

func New(opts Options, log *slog.Logger) *Sender
func (s *Sender) Run(ctx context.Context)
func (s *Sender) SetPressure(high bool)
func (s *Sender) SendLag(ctx context.Context) (time.Duration, error)

// throttle.go (unexported)
type throttle struct{ /* next time.Time; rate int64 */ }

func newThrottle(bytesPerSecond int64) *throttle
func (t *throttle) wait(ctx context.Context, n int) error
```

```go
// internal/spool
var ErrClosed = errors.New("spool: closed") // was errClosed
func (s *Spool) AckRanges() int

// internal/config
type BackendTarget struct {
	URL               string `yaml:"url"`
	BackfillBandwidth string `yaml:"backfill_bandwidth"`
	BackfillBytes     int64  `yaml:"-"` // BackfillBandwidth in bytes per second; set by validation
}
func parseBandwidth(text string) (int64, bool)

// internal/agent
type Options struct { /* … */ Transport sender.Transport } // nil: the HTTP transport to backend.url
func newScheduler(opts Options, cfg *config.Agent, store *agentstate.Store, sink scheduler.Putter, log *slog.Logger, pressure ...func(high bool)) (*scheduler.Scheduler, error)
```

Existing files the skeleton rewrites: `internal/spool/spool.go`, `ack.go`, `read.go`, `put.go` (rename of `errClosed`);
`internal/config/agent.go`, `config.go`; `internal/agent/agent.go`.

## Test files

`internal/sender/sender_test.go`, `internal/sender/throttle_test.go`, `internal/sender/transport_test.go`,
`internal/sender/helpers_test.go` (fake transport: scripted results per call, records bodies and times, optional delay or block
until its context ends; a recorder spool wrapper with injected `Ack`/`Read` errors), `internal/spool/ack_test.go`,
`internal/config/agent_test.go`, `internal/config/config_test.go`, `internal/agent/agent_test.go`; measurements
`internal/spool/refstream_test.go` (`TestReferenceStream_BackfillRate`) and `TestSender_MemoryBound` in `sender_test.go`.

Existing test call sites: `internal/spool/cover_test.go:170-174` uses `errClosed` — the Dev renames them in step 4.
`spoolRunOptions` in `internal/agent/agent_test.go:1704` must set `Options.Transport` to a fake that answers `NotAccepting`, so no
agent test dials a real address — the Tester in step 5 (old and new coexist). `newScheduler` stays call-compatible (variadic).

Files no unit test can cover: none.

## Areas

- [Agent](../../docs/areas/agent.md): *Status*, *Connection* (no proxy, no redirect, the ingest request), *Self-metrics* (send lag
  exists), *Spool and sending* (all sending rules and log lines of *Approach*, the measured memory and backfill rate),
  *Related decisions* (0124, 0125), *Implementation* (`internal/sender`).
- [Configuration and secrets](../../docs/areas/configuration-and-secrets.md): row and rules of `backend.backfill_bandwidth`.
- [Wire format](../../docs/areas/wire-format.md): `agent_send_lag_seconds` definition; `mode` as the sender sets it.
- [Backend host](../../docs/areas/backend-host.md): new *Ingest endpoint* section — the request and status contract #40
  implements (planned).
- [Network and security model](../../docs/areas/network-and-security-model.md): *Agent to backend* states that the agent side
  exists and the endpoint is planned.

## Documentation updates

| File | Edit | Owner |
| ---- | ---- | ----- |
| `docs/areas/agent.md` | as *Areas* | Dev |
| `docs/areas/configuration-and-secrets.md` | option row, rules, error text | Dev |
| `docs/areas/wire-format.md` | `agent_send_lag_seconds` row; `mode` | Dev |
| `README.md` | line 16 (the sender exists), line 31 (package list), *Agent options* row | Dev |
| `deploy/agent/agent.yaml` | the key with comment and default | Dev |
| `.squad/stack.md` | *Layout*: `sender` in the Go package list | Dev |
| `.squad/project.md` | area 8 (token only in `Authorization` to `backend.url`), area 11 (the agent's HTTP client: no proxy, no redirect, 30 s, TLS verified, body bound), area 12 (the sender's fixed log lines), *Test doubles* row "Tailscale network" implemented (fake transport, `httptest`) | Dev |
| `docs/areas/backend-host.md` | *Ingest endpoint* contract | Lead |
| `docs/areas/network-and-security-model.md` | *Agent to backend* status | Lead |
| `docs/ARCHITECTURE.md` | status paragraph (lines 14-15), *Components* (sender sentence with 0124, 0125), *Offline behavior and backfill* records | Lead |

Sweep: `grep -n -i 'sender\|nothing sends\|#39'` over `docs/`, `README.md`, `.squad/project.md` found exactly the rows above;
the option enumerations are the configuration table, `AgentKeys`' doc comment, the README table and the example file.

## Architecture check

*No data gaps unless explicitly recorded*: every record is acknowledged only after a `2xx`, or given up with a `gap` the backend
receives; holes are bounded so the spool keeps deleting acknowledged files. *A hanging collector never blocks the agent*: the
sender runs in its own goroutines, requests end after 30 s, a `Read` holds the spool lock only for one bounded read. *Backfilled
data never alerts*: `mode` stays a hint. Outbound only (0006): the sender only dials `backend.url`. No guarantee is weakened.

## Security considerations

- [ ] Limits: the answer body 4 KiB of raw bytes before anything (never decoded), headers 16 KiB raw (Go's limit, before parsing);
  batches 1 MiB of record lines before compression, at most 20,000 records.
- [ ] User-visible failure texts: only the fixed log texts above; `err` carries the spool's own fixed error texts; no `net/http`,
  `tls` or `url` error text reaches a log line; `sender: backend url: not usable` at start-up.

## Decision records

- `docs/decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md` (Proposed)
- `docs/decisions/0125-sender-two-streams-shared-backoff-refused-records-given-up-with-a-gap.md` (Proposed)

## Out of scope / follow-ups

The ingest endpoint (#40) implements the contract in `backend-host.md`; #41 must allow the 5 s send interval plus a request's
time as lateness tolerance (stated in 0125). No new follow-up issue.
