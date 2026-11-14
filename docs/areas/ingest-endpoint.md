# Ingest endpoint

## Scope

How `vandoxd` receives the agent's batches on its ingest listener: the request, the checks and their answers, the token check, the
binding of the token to one agent ID, the limits, slots, connections, time budget and free-space floor, the acknowledgement and
repeats, the log lines and what happens at the stop signal. The agent's sender relies on it
([0124](../decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md)); the batch format is the [wire
format](wire-format.md)'s. How the agent reacts to each answer is in [Agent sending](agent-sending.md).

## Request

- `POST /v1/batches` on the ingest listener (the path is exact: the agent's `backend.url` carries no path), one request per batch.
  Method and path are compared ordinally and exactly, so `/v1/batches/`, `/V1/batches` and `GET /v1/batches` do not match; the query
  string is ignored. The ingest listener answers `404` to everything else ([Backend host](backend-host.md), *Listeners and routing*).
- Headers: `Authorization: Bearer <agent token>` (the secret `VANDOX_AGENT_TOKEN`, [Configuration and
  secrets](configuration-and-secrets.md)), `Content-Type: application/gzip`, `User-Agent: vandox-agent/<version>` and
  `Content-Length`; no `Content-Encoding`, so the server's request decompression never unpacks the body before the endpoint's own
  checks.
- The body is one gzip stream of one batch of the [wire format](wire-format.md), format version 1.1 (1.0 is accepted too), with the
  agent ID, the boot ID and the clock offset of its records' capture context and the header `mode` (`live` or `backfill`, a hint only,
  logged and not stored). The agent's batch holds at most 20,000 records and 1 MiB (1,048,576 bytes) of record lines before
  compression, counted without their line feeds, so it always fits the endpoint's limits below.

## Enabled

Ingest needs both the token and the option `ingest.agent_id` (the agent the token belongs to). Without either, the service starts as
usual (web UI, `vandoxd import`), logs the warning `ingest disabled` once at start with `missing` naming `VANDOX_AGENT_TOKEN`,
`ingest.agent_id` or both (comma-separated), and answers every `POST /v1/batches` with `503` before it looks at the request.

## Answers

No answer has a body. `R` marks an answer with the header `Vandox-Refused: batch`. The checks run in this order, and the first that
fails answers:

| # | Check | Answer | Log (level) |
| - | ----- | ------ | ----------- |
| 1 | ingest disabled | `503` | — |
| 2 | the `Authorization` header is exactly one value, `Bearer` (any ASCII case), one space and the token | `401` with `WWW-Authenticate: Bearer` | `ingest token refused` (warning): `peer`, `reason` (`missing` for no or an empty header, otherwise `wrong`), `suppressed` |
| 3 | no `Content-Encoding` header of any value | `415` | `ingest request not accepted` (warning): `status`, `reason` `content encoding` |
| 4 | exactly one `Content-Type` value, equal to `application/gzip` ignoring ASCII case (no parameter, no space) | `415` | the same, `reason` `content type` |
| 5 | `Content-Length`, when sent, at most 2 MiB (2,097,152 bytes) | `413` R | `ingest batch refused` (warning): `status`, `reason` `body too large` |
| 6 | one of the two slots free within the request's time budget | `503` | `ingest request timed out` (warning): `peer` |
| 7 | the body, read into the slot's buffer, at most 2 MiB; read within the budget | `413` R; `408` when the budget ran out while the body was arriving | `ingest batch refused` (`body too large`); `ingest request timed out` |
| 8 | the header line decodes and its `agent_id` equals `ingest.agent_id` ordinally | decoder error as in step 9; `403` for another agent ID | `ingest agent refused` (warning): `agent_id` (it passed the wire format's rule), `peer`, `suppressed` |
| 9 | every record decodes to the end, within the budget | `400` R for malformed, `413` R for limit exceeded, `422` R for unknown kind, sequence, empty batch and a field error, `415` for unsupported version ([Wire format](wire-format.md), *Errors*); `503` when the budget ran out | `ingest batch refused`: `agent_id` (once the header was read), `status`, a fixed `reason`, `line`, `field`; `415`: `ingest request not accepted` with `reason` `format version`; `ingest request timed out` |
| 10 | the free space of `storage.directory`'s file system at least `ingest.min_free` (`0MiB` switches the check off; a free space that cannot be read counts as low) | `503` | `ingest paused: free space low` (warning): `available_bytes` (`null` when unreadable), once until the next stored batch |
| 11 | the service has not received the stop signal | `503` | — |
| 12 | the store writes the batch in one transaction | `422` R when the store refuses a record the decoder accepted (a capture time outside 1677-09-21 to 2262-04-11); `503` for a store failure (I/O error, full disk, a lock held longer than the 5-second busy timeout, the shutdown deadline) | `ingest batch refused` (`reason` `refused by the store`); `ingest store failed` (error): `agent_id`, `exception` (type name) |
| 13 | committed | `204` | `ingest batch stored` (debug): `agent_id`, `mode`, `records`, `stored`, `duplicates`; `ingest first batch stored` (info): `agent_id`, once per process; `ingest resumed` (info) after a pause; then, for a batch with an agent ID whose write changed the agent's timeline: `ingest gaps changed` (info): `agent_id`, `opened` (gaps newly detected: a jump's sequence gap, a new time-gap span), `closed` (gap rows moved to closed), `final` (sequence gaps made final plus time gaps detected final) and `open` (open sequence gaps after the batch), once per batch that opened, closed or made final a gap (a batch that only shrinks or splits a gap writes none); a row split off an existing gap counts in none; `agent clock off` (warning): `agent_id`, `offset_seconds`, when the measured offset first exceeds 90 seconds; `agent clock in step` (info): `agent_id`, when it is back within 90 seconds ([Detection, alerts and reports](detection-and-reports.md), *Live data and backfill*) |

The fixed reasons of step 9 are `malformed`, `limit exceeded`, `unknown kind`, `sequence`, `empty batch` and `invalid`; `line` is the
line number of the batch and `field` the wire format's field path with every quoted map key reduced to `[]` (`data.labels[]`, list
indexes kept), or `null` when there is none or the path does not parse. The endpoint decodes the whole batch before it writes
anything: when any read fails, nothing is written, and nothing of the batch is stored in any answer but `204`.

## Acknowledgement and repeats

- **Acknowledgement.** A `2xx` answer acknowledges every record of the batch; the endpoint sends `204` only after the batch is
  committed ([Storage writing](storage-writing.md): one batch, one transaction). The agent then acknowledges the records in its spool,
  which may then delete them, so a `2xx` before the commit would lose data. Every record is stored with origin `agent`, the batch's
  agent ID, boot ID and clock offset, its own sequence number and capture time, and one **receive time** per batch: the service clock
  in UTC when the request reached the endpoint. The store detects the agent's sequence and time gaps and classifies each record live
  or backfilled in the batch's transaction, ignoring the header's `mode` and `clock_offset_ns` ([Storage writing](storage-writing.md),
  *Agent timeline*). The agent reads at most 4,096 bytes of an answer's body, discards them and never parses them, and reads at most
  16 KiB of an answer's header; it looks at nothing but the status and the exact value of `Vandox-Refused`.
- **Refused batch.** `400`, `413` or `422` **with the header `Vandox-Refused: batch`** (exactly this value, compared case-sensitively)
  means the batch itself is invalid and sending it again cannot succeed. The agent then splits the batch and gives up each record
  refused alone, replacing it with a recorded `gap`. The endpoint sends the header only on a request that passed the token check and
  only for such a batch (steps 5, 7, 8, 9 and 12). The same statuses without the header (a TLS listener reached with `http`, a proxy's
  size limit, the server's own `400`) are not a refusal.
- **Every other answer** means "not accepting": the agent keeps the batch and sends it again after a wait (1 to 60 seconds). The
  endpoint answers with a status outside `400`, `413` and `422`, and never with the refusal header, for everything that is not the
  batch's fault: a missing or wrong token (`401`), another agent ID (`403`, which the agent logs as `token refused`), a format version
  or content type it does not support (`415`), a full disk, a busy or failing store, a timeout or the stop of the service (`503`,
  `408`). A redirect is not followed. So a version mismatch or a temporary fault never makes the agent give up records.
- **Repeats.** A batch whose answer was lost is sent again; the store keeps each agent ID and sequence number once ([Wire
  format](wire-format.md), *Batch identity (0045)*), so a repeat is answered `204`, stores only the records it did not have and changes no
  stored row, its receive time included. A record whose sequence number is stored with other content counts as a duplicate and never
  replaces the stored one. Two requests with overlapping records at once both get `204`.

## Token check

- Only a request with the valid token can store data. The token is checked **before the body is read**: a request without the right
  token never has a byte of its body read, decompressed or parsed, so a peer without the token costs no decoding.
- The presented token and the configured one are compared in constant time, as SHA-256 digests, so the time depends neither on the
  presented length nor on how much of it is right. The token is read only from the one `Authorization` value with the scheme
  `Bearer`; the query string, cookies and other headers are never read.
- The header's `agent_id` must equal `ingest.agent_id` ordinally (step 8): the one token is bound to one agent, and a token holder can
  write batches of that agent ID only.
- The server removes spaces and tabs around a header value before the endpoint sees it, so a token with a trailing space or tab is
  accepted. Control characters (U+0001), DEL and valid UTF-8 characters in the header reach the endpoint and are answered `401`; a
  header byte that is not valid UTF-8 (such as `E9` alone) and obsolete line folding get the server's own `400` without the refusal
  header before the endpoint runs; a bare line feed ends the header line, so the rest becomes a header of its own (measured against the
  server). The agent sends only printable ASCII, so none of the server's answers concerns it.

## Connections

The ingest listener speaks HTTP/1.1 only (one request at a time per connection) and keeps at most **16 connections** open. A connection
is trusted once one of its requests passed the token check (step 2). When 16 are held, a further connection evicts the oldest
connection that is not trusted (it is aborted at once) and takes its place; the further connection is closed, before a request is
read, only when all 16 held connections are trusted, and a place freed by a finished connection serves the next one. The web listener
has no such cap. A host without the token therefore cannot keep the agent out by holding connections. A sustained connection flood can
delay the agent's first connection (after a start or a lost connection) until one attempt wins the race; it cannot evict a connection
the agent has used. A token holder can hold all 16; the agent keeps its records and retries (record 0178).

## Limits, slots and memory

- **Limits.** Compressed body at most 2 MiB: a `Content-Length` over it is answered `413` before the body is read, a body without a
  length is read into its slot's buffer only up to the limit, and the server's request body limit is set to the same 2 MiB as a
  backstop. The decoder runs with the decompressed batch at most 2 MiB (the wire format's 16 MiB default is lowered for ingest), a line
  at most 1 MiB and at most 20,000 records, which every batch of the agent's sender fits.
- **Slots.** At most **two** requests hold a slot at once across the service (the agent's two streams); further requests wait. A slot
  is held from the first byte of the body read through decoding, the free-space check, the wait for the store's writer and the commit
  or rollback, so at most two decoded batches exist at any time. Each slot owns a body buffer of 2 MiB, allocated on first use, kept
  and never pooled or shared. There is no request-rate cap: the slots and the one writer bound the rate.
- **Memory.** Measured peak memory of one request at these limits (the backend's decoder and a real store write, one request after a
  warm-up, the live heap sampled by a second thread during the request; Debug and Release builds gave the same figures, development
  host):

  | Input | Live heap | SQLite native high-water | Peak |
  | ----- | --------- | ------------------------ | ---- |
  | Hostile record line of 1,000,119 bytes, distinct keys (#175), body of a few KB | 9.7 MB | — | 9.7 MB |
  | Hostile line of 1.0 MB of 24-key objects | 7.2 MB | — | 7.2 MB |
  | Hostile line of 1.0 MB of small objects | 9.1 MB | — | 9.1 MB |
  | 17,936 distinct log lines (the batch limit) | 8.6 MB | 6.2 MB | 14.8 MB |
  | Two connection snapshots of 1,034,749 bytes each | 14.3 MB | 4.5 MB | 18.8 MB |

  The 17,936-line batch allocates about 123 MB in total, but that is garbage the runtime collects during the request: the bound is on
  live memory. The two slots at the worst case (2 × 18.8 MB) plus their two 2 MiB body buffers come to about 42 MB, within the 128 MiB
  budget the endpoint may use of the container's 512 MiB; tests assert that bound. The agent's Go decoder's figure for the hostile 1 MiB
  line (about 160-170 MB of heap, roughly 160 times the line limit) was measured on that decoder alone and does not describe the backend.

## Time

Waiting for a slot, reading the body and decoding take at most **30 seconds** from the request's arrival (the agent's own request
timeout); the server's header timeout (5 s), header size (16 KiB) and keep-alive (120 s) stay ([Backend host](backend-host.md),
*Listeners and routing*). A request past the budget gets `503` (`408` while its body was still arriving) wherever an answer can still
be sent, also a body that trickles in; at the stop signal a request still waiting, reading or decoding gets `503` without a log line.
A body read that fails with an I/O error is answered `503`, unless the connection is gone; a request whose connection is gone gets no
answer. Once its write has started, a request runs to its commit or rollback whatever happens to its connection: neither the
request's abort nor the 30-second budget cancels it, only the shutdown deadline (*Stop*); the write waits for another process's lock
at most the busy timeout of 5 seconds ([Storage](storage.md)).

## Free-space floor

While less than `ingest.min_free` (default 1 GiB) is free on the file system of `storage.directory`, no batch is written and the agent
keeps its records. The free space is read before every write. The overshoot below the floor is at most what the two slots write at
once. The web UI and the import do not check the floor; the database still grows up to it. Retention bounds the database by age ([Retention and rollups](retention.md)) but frees no space on the volume: it deletes rows inside `vandox.db`, which does not shrink (record 0212), so ingest stopped at the floor resumes only when space on the volume is freed (shrinking the file is #55).

## Stop

- **No new write after the stop signal.** From the stop signal on no ingest write starts: the endpoint closes its write gate, so a
  request that has not started its write gets `503` (or no answer) and stores nothing. A write that has started commits or rolls back;
  the shutdown deadline, 10 seconds after the stop signal ([Backend host](backend-host.md), *Shutdown*), cancels it otherwise, and the
  transaction rolls back. A batch that was committed but not answered is sent again after the restart and answered `204` as a repeat.
- **Drain before the store closes.** On every way out after the service started (a normal stop, a failure of a listener, an exception
  while stopping) the service waits for the ingest writes in flight, at most until the deadline and until the cancelled writes have
  rolled back, and only then closes the database.

## Log lines

- `ingest token refused` and `ingest agent refused` are each logged at most once a minute; the next line after a quiet minute carries
  in `suppressed` the number of refusals not logged since the last line. The other ingest warnings (`ingest request not accepted`,
  `ingest batch refused`, `ingest request timed out` and the rest) have no such limit; only a request that passed the token check
  produces them.
- No ingest log line holds the token, the `Authorization` header, body bytes, a map key, other record content or a parser's or the
  store's own text; an exception is logged by its type name only. The timeline lines (`ingest gaps changed`, `agent clock off`,
  `agent clock in step`) carry only the agent ID (it passed the wire format's rule), counts and the offset in seconds.
- An unexpected exception of the endpoint is answered `503` (when the answer has not started) and logged as the error
  `ingest request failed` with `exception`.

## Related decisions

- [0124](../decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md) — why the ingest request carries one
  batch with a bearer token and is acknowledged by its status, and why a refused batch is marked with a header.
- [0126](../decisions/0126-the-ingest-token-is-bound-to-one-configured-agent-id.md) — why the token is bound to one configured agent ID.
- [0177](../decisions/0177-ingest-is-bounded-by-two-slots-through-the-commit-and-a-free-space-floor-not-a-rate-cap.md) — why two slots are
  held through the commit and a free-space floor stands instead of a rate cap.
- [0178](../decisions/0178-a-full-ingest-listener-evicts-the-oldest-connection-that-has-not-passed-the-token-check.md) — why a full listener
  evicts the oldest untrusted connection.
- [0179](../decisions/0179-ingest-writes-end-only-at-the-shutdown-deadline-and-are-drained-before-the-store-closes.md) — how the shutdown
  bounds a write.
- [0128](../decisions/0128-csharp-decoder-reads-the-gzip-framing-itself-to-match-the-go-decoder.md) — why the decoder the endpoint
  uses reads the gzip framing itself.
- [0032](../decisions/0032-secrets-only-from-environment-or-docker-secrets.md) — why the token comes only from the environment.
- [0044](../decisions/0044-batch-validated-as-a-whole-agent-records-only.md) and
  [0045](../decisions/0045-batch-identified-by-agent-id-and-record-sequence-numbers.md) — why a batch is valid only as a whole and a
  record is stored once per agent ID and sequence number.

## Not here

- The listeners, the port routing, the server's header limits, the health probe, the shutdown deadline and the compose file that
  publishes the ingest port: [Backend host](backend-host.md).
- The batch format, its error classes and accepted forms: [Wire format](wire-format.md), [Wire format records](wire-format-records.md).
- The token's source and rules, `ingest.agent_id` and `ingest.min_free`: [Configuration and secrets](configuration-and-secrets.md),
  [Configuration options](configuration-options.md).
- How records are written, deduplicated and classified: [Storage writing](storage-writing.md).
- The agent's sending, its waits and its reaction to each answer: [Agent sending](agent-sending.md).
- The tailnet path to the endpoint: [Network and security model](network-and-security-model.md).

## Implementation

`src/Vandox.Backend/Ingest`: `IngestEndpoint` (with `DrainAsync`, reading `BatchDecoder.NextAsync` to the end into a list), `IngestSettings`
(`IngestSettings.Create`), `IngestToken` (SHA-256 digests compared with `CryptographicOperations.FixedTimeEquals`), `IngestLimits`
(`IngestLimits.Wire`), `IngestStatus`, `IngestRequest`, `IngestRefusalLog`, `IngestFieldPath`, `IngestWriteGate` (closed in the
`ApplicationStopping` callback), `FreeSpace`, `ConnectionCap` and `CappedConnection`; the request branch in `Hosting/PortRoutingMiddleware`,
the listener in `Hosting/ListenerRoutes`, the drain in a `finally` of `Hosting/ServeCommand`, the log lines in `Hosting/BackendLog`. The
store write is `SqliteStore` with `BatchWriter` in `src/Vandox.Storage`. Tests: `ServeIngestHeaderWhitespaceAndDuplicates` (the server's
header handling), `IngestEndpointHostileLineStaysWithinMemoryBound` and `IngestEndpointMaximalBatchStaysWithinMemoryBound` (the memory
bound), `IngestEndpointTimelineTests` (the timeline log lines).
