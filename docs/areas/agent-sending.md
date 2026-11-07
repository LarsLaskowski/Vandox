# Agent sending

## Scope

How `vandox-agent` delivers the spooled records to the backend: the one request it makes, the client's network rules, the live and
the backfill stream, acknowledgement, the shared backoff while the backend does not accept batches, refused batches and give-ups, the
send lag, memory and rate, and its log lines. The backend's ingest endpoint and the operator rely on it; the agent's connection rules
in outline are in [Agent](agent.md) (*Connection*).

## The sender

- The sender runs in the agent's process beside the scheduler, only with a spool (not with the test sink), and reads the spool, hands
  it gap records and acknowledges into it ([Agent spool](agent-spool.md)). The agent stops it with the scheduler, before the state
  file's last save and the close of the spool.
- The agent starts without a reachable backend. A transport that cannot be built (a `backend.url` the transport refuses; the loader has
  checked it, so this cannot happen with a loaded configuration) is the one start error: `agent cannot start` with
  `sender: backend url: not usable`, never the URL, exit code 1.

## The request

- The sender is the one part of the agent that dials the backend: one `POST` of one batch to `<backend.url>/v1/batches` with the header
  `Authorization: Bearer <token>`. The agent token goes to `backend.url` only and nowhere else.
- The client uses **no proxy** (the environment's `HTTP_PROXY` and the like are not read), follows **no redirect** (a `3xx` is an
  answer that is not accepted, and the request, with the token, is never repeated at the `Location`), verifies the certificate of an
  `https` URL against the system roots (never skipped, TLS 1.2 at least), ends a request after 30 seconds, reads the header of an answer
  up to 16 KiB and at most 4,096 bytes of its body, which it discards; once 4,096 bytes of a body were read the agent closes the
  connection itself and cancels the request, so the transport does not read on. The transport's results hold a fixed reason, never an
  error text.
- Plain `http` is meant only for the backend host's tailnet address, because the tailnet encrypts and authenticates the transport;
  every other address is `https`. With `https` the backend's certificate must name the IP address of `backend.url` as a subject
  alternative name, so a `*.ts.net` certificate fails as `tls failed`.
- Every connection to an address that is not a loopback address (`127.0.0.0/8`, `::1`, an IPv4-mapped loopback) is bound to the
  interface `tailscale0` (`SO_BINDTODEVICE`) before it connects; an address that cannot be parsed is bound, and a connection to a
  loopback address is not, which a backend on the same host needs. While Tailscale is down a send therefore never leaves through another
  interface: without `tailscale0` it fails at once as `connection failed`, with the interface but no routes it ends as a `timeout`, and
  the records stay in the spool.
- **The second layer.** The installed unit lets the agent reach only localhost and the tailnet ranges (`IPAddressAllow=`,
  [Agent installation](agent-installation.md), *The unit in short*), so any other address fails as `connection failed`.
  `backend.url` holds an IP address; no name is looked up ([Network and security model](network-and-security-model.md), *Agent to
  backend*; the unit settings the sender needs are in [Agent rights](agent-rights.md)).
- A host name in `backend.url` is refused by the loader and by the transport (`sender: backend url: not usable`), as are the short and
  numeric IPv4 forms and an IPv6 zone, because a name server, not the configuration, would then choose the destination of the token
  (record 0192).
- The scheme must match the listener: an `https` URL at a plain-http listener fails as `connection failed`, and an `http` URL at a TLS
  port gets a `400` without the refusal header, which counts as a backend that does not accept batches (*Not accepting*), so the agent
  only waits and gives no record up.

## Two streams

- A **live** stream sends every 4 seconds the records the spool took since its previous send (`mode: live`, batches of at most 20,000
  records and 1 MiB of lines, one batch per request, batch after batch until nothing is left).
- A **backfill** stream sends the older, unacknowledged records oldest first (`mode: backfill`, the same limits). Only the backfill is
  throttled, to `backend.backfill_bandwidth` of compressed request body bytes a second (the bodies of any span sum to at most the
  bandwidth times the span plus one batch); the live stream is never throttled.
- The backfill pauses while the memory pressure mode is on ([Agent](agent.md), *Memory pressure mode*): info `backfill paused` with
  `reason=memory_pressure`, and `backfill resumed`, logged only when records wait to be backfilled (also before a pass's first batch),
  never when there is nothing to backfill.
- At the start the live stream begins after the newest record, unless the spool already holds 32 acknowledged ranges (*Holes*), when
  it begins at the oldest unacknowledged record. The backfill waits for the first acknowledgement of the live stream and then sends
  everything below that stream's first record (info `backfill started` before its first batch and `backfill finished` with `records`,
  the number of records acknowledged, after the pass, both only when the pass found records to send).
- A request carries one batch with the header `mode` as a hint (the backend classifies by lateness, [Wire format](wire-format.md)). A
  batch is the capture context of its records (agent ID, boot ID, clock offset), so it never mixes contexts.

## Acknowledgement

A record is acknowledged in the spool only after a `2xx` answer, and a stream's position moves only after the spool kept the
acknowledgement. When the spool did not keep it (more than 64 ranges, logged as the warning `acknowledgement not kept` with `err`, once
until an acknowledgement succeeds, or a gap it could not take), the stream waits one wait of its own schedule and sends the same records
again; the backend stores a resent record once ([Wire format](wire-format.md), *Batch identity (0045)*). A read the spool refuses logs the
warning `spool not readable` (`err`) once for the failure and retries after the same wait. A failed delivery inside a backfill pass (an
acknowledgement the spool did not keep, a wait for a free give-up slot) does not end the pass: after its wait the pass reads again from
its position, so it never moves past an unacknowledged record.

## Not accepting: one shared backoff

- An answer that is not `2xx` and not a refusal (*Refused batches*), and every failure without an answer (`timeout`,
  `connection failed`, `tls failed`), is a backend that does not accept batches. The first one starts an **episode** that both streams
  share: warning `backend not accepting batches` with `reason` (`timeout`, `connection failed`, `tls failed`, `token refused` for `401`
  and `403`, `status` with the status for any other status) once, and again only when the reason or the status changes.
- During the episode the backfill sends nothing, and the live stream makes one attempt after each wait, so an unreachable backend costs
  one request per wait. The wait is drawn uniformly from half to the whole of 1 second, doubled after each failed attempt to at most 60
  seconds. An attempt sends the records the spool took since the previous attempt began, so current data is not held up by the old.
- The first acknowledged batch ends the episode (info `backend accepting batches again`, `seconds` the length) and releases the
  backfill.
- **Holes.** The records between the live position and the jump stay unacknowledged until the backfill covers them. The holes are
  bounded at 32: when the spool holds 32 acknowledged ranges, an attempt reads from the live position instead of jumping, which leaves
  no new hole, so the spool, which keeps at most 64 acknowledged ranges, never refuses an acknowledgement and deletes acknowledged files.

## Refused batches and give-ups

- A `400`, `413` or `422` that carries the header `Vandox-Refused: batch` (exactly this value, compared case-sensitively) refuses the
  batch itself ([Ingest endpoint](ingest-endpoint.md)); it is not an episode. The sender halves the batch recursively and sends the
  halves in order; a record that is refused alone is **given up**: the sender hands the spool a `gap` record (cause `unknown`, source
  `agent`, `first_seq` and `last_seq` the record's number, `from` its capture time, `to` one second later, captured now), acknowledges
  the record and logs the warning `record refused` (`status`, `seq`, `kind`). A batch the wire encoder refuses is split the same way and
  gives its single record up with `status` 0.
- A `400` or `413` without the header (a TLS listener reached with `http`, a proxy's size limit) is an ordinary `status` answer and
  gives nothing up.
- At most **10 records are given up in an hour**, counted in the agent's memory (after a restart 10 more are allowed, a stated
  residual): at the bound the stream waits until the oldest give-up is an hour old, logs the warning `backend not accepting batches`
  with `reason=refused` and `status` once for the wait, and then sends the refused records again; the other stream goes on and no
  acknowledgement of it ends that wait. A backend that refuses everything thus costs at most 10 records and a bounded number of requests
  an hour.

## Send lag

`agent_send_lag_seconds` is the time since the capture of the newest record acknowledged since the start (before the first
acknowledgement, since the start), never negative. The `self` collector records it ([Agent](agent.md), *Self-metrics*).

## Memory and rate

- The sender holds one encoded batch for each stream, at most 1 MiB of lines compressed, independent of the backlog. Measured with a
  full batch in flight on both streams: 4.4 to 4.6 MB of heap held and a sampled peak of 8.3 to 8.6 MB under the race detector (7.5 to
  7.8 MB without), the same at 600 and at 2,400 records of backlog, against the limit of 16 MiB.
- 6,807 records of the reference stream ([Agent spool](agent-spool.md), *Capacity*), 2,630,490 bytes of lines, compress to 556,335
  bytes (gzip ratio 4.73), which at the default `256KiB` a second is 3,207 records a second, inside the band of 1,000 to 8,500 a test
  pins (on the development host; the real rate is not measured yet, #342). At that rate the backlog of a day (544,560 records) takes
  about 3 minutes of sending beside the live stream.

## Logging

Through the agent's logger with `component=sender`, never the URL, the token, a host, a record's content or an answer's text, only
the fixed lines above: warning `backend not accepting batches` (a fixed `reason` and the status), info `backend accepting batches
again`, info `backfill started`, `backfill finished`, `backfill paused`, `backfill resumed`, warning `record refused` (the status, the
sequence number and the record kind), `acknowledgement not kept` and `spool not readable` (the spool's error text). A stop by the agent
logs nothing.

## Related decisions

- [0006](../decisions/0006-agent-connects-outbound-only.md) — why the agent only connects outbound.
- [0124](../decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md) — why a request carries one batch, is
  acknowledged by its status alone and marks a refusal of the batch itself with a header.
- [0125](../decisions/0125-sender-with-a-live-and-a-backfill-stream-over-one-shared-backoff.md) — why a live and a backfill stream with one
  shared backoff, holes bounded at 32 and the backfill paused under memory pressure.
- [0176](../decisions/0176-the-sender-gives-up-refused-records-one-by-one-with-a-recorded-gap-at-a-bounded-rate.md) — why refused records
  are given up with a recorded gap, at most 10 an hour.
- [0134](../decisions/0134-sender-binds-non-loopback-connections-to-the-tailnet-device.md) — why every non-loopback connection is
  bound to `tailscale0`.
- [0192](../decisions/0192-backend-url-names-the-backend-by-ip-address-host-names-are-refused.md) — why `backend.url` holds an IP address and
  a host name is refused.

## Not here

- The outline of the agent's connections and its local channels: [Agent](agent.md), *Connection*.
- The spool, its reads, acknowledgements and capacity: [Agent spool](agent-spool.md).
- The ingest endpoint's statuses, limits and the refusal header: [Ingest endpoint](ingest-endpoint.md); the batch format:
  [Wire format](wire-format.md).
- The tailnet, its ACL and the unit's address allow-list: [Network and security model](network-and-security-model.md); the unit's
  settings the sender needs: [Agent rights](agent-rights.md), [Agent installation](agent-installation.md).
- `backend.url`, `backend.backfill_bandwidth` and the token file: [Configuration options](configuration-options.md),
  [Configuration and secrets](configuration-and-secrets.md).

## Implementation

`internal/sender` (the sender, its HTTP transport `HTTPTransport` and the backfill throttle; it uses the spool's `Read`, `Ack`, `Put`,
`LastSeq` and `AckRanges`; the transport is the seam `agent.Options.Transport`, so tests send to a fake transport and
`net/http/httptest`). Tests: `TestSender_MemoryBound`, `TestSender_LogsHoldNoURLTokenOrBody`, `TestReferenceStream_BackfillRate` (in
`internal/spool`), `TestHTTPTransport_IgnoresProxyEnvironment`, `TestHTTPTransport_FollowsNoRedirect`,
`TestHTTPTransport_RefusesUntrustedCertificate`, `TestHTTPTransport_RefusesOversizedHeaders`,
`TestHTTPTransport_ReadsAtMostFourKiBOfBody`, `TestHTTPTransport_RefusedOnlyWithHeader`, `TestNeedsDevice_FalseOnlyForLoopback`,
`TestHTTPTransport_FailsAtOnceWithoutTheDevice`, `TestHTTPTransport_LeavesLoopbackUnbound`, `TestNewHTTPTransport_RefusesHostNames`,
`TestLoadAgent_BackendURLHostMustBeAnIPAddress` (in `internal/config`), `TestValidateBackendURL_RefusesHostNames` (in `deploy/agent`).
