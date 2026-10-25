# Spec: Ingest API

Source: Issue #40
Status: Draft

## Problem

The agent's sender (#39) already posts its spool to `<backend.url>/v1/batches` by the contract of record 0124 (Backend host area,
*Ingest endpoint*), but `vandoxd` has no ingest endpoint: its ingest listener answers `404` to everything and the compose file
publishes no ingest port (record 0060). So no agent data reaches the backend; the agent keeps retrying, its spool fills to its size
bound and then drops its oldest records with recorded gaps. Release v0.2.0 needs the backend side: an endpoint on the ingest listener
that only the holder of the agent token can write to, that bounds what one request can cost in memory and time, that stores each
(agent ID, sequence number) once with the time it was received, and that acknowledges a batch only after its commit, because the
agent deletes acknowledged records. The operator also needs a supported way to reach that endpoint over Tailscale: the ingest port
published on the backend host's tailnet address (record 0017), with the Tailscale mode that makes this address bindable (the Synology
package defaults to userspace networking, where it is not a host interface) stated in the compose file, the README and a decision
record, and checked by the CI smoke test.

## Scenarios

Terms: *the token* is the backend's secret `VANDOX_AGENT_TOKEN`; *the configured agent* is the agent ID the backend binds the token to
(new option `ingest.agent_id`, see *Non-functional requirements*); *a refusal* is an answer that carries the header
`Vandox-Refused: batch`.

- S1: Given a backend with the token and `ingest.agent_id: web-1` set, when the agent `web-1` posts a valid gzip batch to
  `POST /v1/batches` on the ingest listener with `Authorization: Bearer <token>` and `Content-Type: application/gzip`, then the
  backend stores every record of the batch in one transaction and, only after the commit, answers `204 No Content` without a body;
  the agent acknowledges the records in its spool.
- S2: Given S1, when the operator reads the stored records, then each carries origin `agent`, the batch's agent ID, boot ID and clock
  offset, its own sequence number and capture time, and the **receive time**: the moment the request reached the endpoint, in UTC,
  the same for every record of the batch.
- S3: Given a batch was stored but its `204` was lost (agent timeout, cut connection), when the agent posts the same batch again, then
  the backend answers `204` and stores nothing new; the stored rows, their receive time included, stay as they were.
- S4: Given the backend has stored records 1 to 100 of `web-1`, when a batch with records 90 to 120 arrives, then records 101 to 120 are
  stored, 90 to 100 are counted as duplicates and left untouched, and the answer is `204`.
- S5: Given the live and the backfill stream of the agent each have a request in flight, when both arrive at once, then both are
  decoded and stored (a record that both batches hold is stored once) and each gets its `204` after its own commit.
- S6: Given the operator runs the compose file with `INGEST_BIND_ADDRESS` set to the backend host's tailnet address on a host whose
  Tailscale runs in TUN mode, when the container starts, then the ingest port 8081 is published on that address only, the web port
  stays as it is, and the agent reaches the endpoint at `http://<tailnet address>:8081`.
- S7: Given the service starts with the token and `ingest.agent_id` set, then it logs its usual start lines and, at the first stored
  batch after the start, one info line `ingest first batch stored` with `agent_id`; every stored batch logs a debug line
  `ingest batch stored` with `agent_id`, `mode`, `records`, `stored` and `duplicates`.

## Errors and edge cases

Every answer below other than `204` has no body. A refusal is sent only to a request that passed the token check and only for a batch
that is invalid as a whole (record 0124); every other non-`2xx` answer leaves the agent keeping and resending the batch. In no case
below is anything of the batch stored.

- E1: A request without an `Authorization` header, with another scheme (`Basic …`), with a bearer token that is not the token (also
  one that only shares a prefix with it, or has a trailing space or line break), with two `Authorization` headers, or with the token in
  the query string only: `401` with `WWW-Authenticate: Bearer`, no refusal header. The body is never read, decompressed or parsed.
  The service logs the warning `ingest token refused` with `peer` (the peer address as the service sees it) and `reason`
  (`missing` or `wrong`), never the presented value or the header.
- E2: Many refused requests in a row (a scan, a misconfigured agent that retries every second): at most one `ingest token refused`
  line per minute; the next line after a quiet minute carries `suppressed`, the number of refusals not logged since the last line.
- E3: The token is set, but the batch header's `agent_id` is not the configured agent (another ID, or the same letters in another
  case): `403`, no refusal header, read no further than the header line. Warning `ingest agent refused` with the presented
  `agent_id` (it has passed the wire format's agent ID rule) and `peer`, rate-limited as in E2. The agent logs this as `token refused`
  (its reason for `401` and `403`), so the README names this log line as the first thing to check.
- E4: The backend has no token, or no `ingest.agent_id`: the service starts as before (web UI and `vandoxd import` work), logs once
  at start the warning `ingest disabled` with `missing` naming `VANDOX_AGENT_TOKEN`, `ingest.agent_id` or both, and answers every
  `POST /v1/batches` with `503` before looking at the request.
- E5: `Content-Length` above the compressed limit (2 MiB): `413` with the refusal header, answered after the token check and before
  the body is read. A body without `Content-Length` (chunked) that grows past the limit: reading stops at the limit, `413` with the
  refusal header. The agent then halves the batch (Agent area, *Sending*).
- E6: A body within the compressed limit that decompresses to more than 2 MiB, holds more than 20,000 records, or has a line longer
  than 1 MiB (also a body of about 1 KB that unpacks to a hostile line): `413` with the refusal header (the decoder's
  `LimitExceeded`).
- E7: A body that is not one readable batch: not gzip, empty, a truncated gzip stream, a wrong CRC or size, trailing bytes after the
  gzip data, a line that is not JSON or breaks the line rules (keys, nesting, UTF-8, surrogate escapes; Wire format area,
  *Accepted forms*): `400` with the refusal header. A batch that reads but breaks a rule: a field error, an unknown or missing
  `kind`, a header without records, sequence numbers that do not increase, a record the store refuses although the decoder accepted
  it (a capture time outside 1677-09-21 to 2262-04-11): `422` with the refusal header. This holds also when the damage comes after
  records that decoded well, such as a bad CRC at the end of the stream: nothing is committed before the decoder reached the end.
  Trailing bytes after the last gzip member (garbage, one byte, zero padding) are refused by the C# decoder too: today it accepts
  them, unlike the Go decoder and the Wire format area (*Accepted forms*), so this change fixes `BatchDecoder` and pins the case
  in the shared fixture `testdata/wire/decoder-cases.json` for both decoders; concatenated members stay accepted.
  The service logs the warning `ingest batch refused` with `agent_id` (once the header was read), `status`, a fixed `reason` and,
  where known, `line` and `field` (the field path of the wire format); never a value, a record's content or a parser's own text.
- E8: A newer minor of format 1 with fields the backend does not know: the records are stored, the unknown keys are dropped (the
  store writes the payload of the backend's own model, so they are not kept). A newer minor
  with a record kind the backend does not know: refused as in E7 (`422`), as the wire format prescribes (upgrade the backend first,
  #85); the agent then gives those records up with recorded gaps, at most 10 an hour.
- E9: A header with a major other than 1 (the decoder's `UnsupportedVersion`), a `Content-Type` other than `application/gzip`, or any
  `Content-Encoding` header (the content type is accepted when it is exactly `application/gzip` compared ignoring ASCII case, with no
  parameter and no surrounding space: `Application/GZIP` is accepted; `application/gzip; charset=x`, `application/x-gzip`, an empty
  or missing value and two `Content-Type` headers are not): `415`, no refusal header, logged as the warning `ingest request not accepted` with `status` and a fixed
  `reason` (`format version`, `content type`, `content encoding`). For the content type and encoding the body is not read.
- E10: The store fails while writing (disk full, an I/O error, a lock held by another process beyond the busy timeout): the
  transaction rolls back, the answer is `503` without the refusal header, and the error line `ingest store failed` names `agent_id`
  and the exception type. A later resend of the same batch is stored normally.
- E11: More requests than the endpoint handles at once (two): the further requests wait for a free slot. A slot is held from the
  first byte of the body read through decoding, the wait for the writer and the commit or rollback, so no decoded batch ever waits
  outside a slot. A request whose waiting, reading and decoding together take more than 30 seconds from its arrival gets no `2xx`
  (`503`, or `408` when the body was still arriving, wherever an answer can still be sent) and logs the warning `ingest request
  timed out`. Once its write has started, a request runs to its commit or rollback, whatever happens to its connection: neither the
  request's abort nor the 30-second bound cancels it, only the 10-second shutdown deadline governs it (E16); when the agent's own
  30-second timeout ends the request first, the agent resends and S3 applies.
- E12: A body that trickles in (a few bytes a second): ended by the 30-second bound of E11 at the latest, not held open for hours.
- E13: More than 16 connections open on the ingest listener at once: a further connection is closed at once, without a request
  being read; the web listener is not affected.
- E14: Any request on the ingest listener that is not `POST /v1/batches` (another path, `/healthz`, a static file, the login,
  `GET /v1/batches`): `404`, as today. `POST /v1/batches` on the web listener never reaches the ingest endpoint: nothing is stored
  and the answer is never `2xx`. No ingest answer carries the web UI's security headers.
- E15: Two requests carry overlapping records at the same time (a backfill batch and a resend of a live batch): every record is stored
  once and both answers are `204`; neither request fails for the other's records.
- E16: The service is stopped (`SIGTERM`) while a request is in flight: a request whose write has started commits or rolls back and
  answers if the 10-second shutdown deadline allows; any other request gets no `2xx` and stores nothing. A batch that was committed but
  not answered is resent after the restart and handled as in S3.
- E17: A record with a sequence number that is already stored but different content (a hostile or broken agent): counted as a
  duplicate; the stored record is never replaced.
- E18: `INGEST_BIND_ADDRESS` is not set: `docker compose up` refuses to start and prints a message that names the variable and says to
  set it to this host's tailnet address. The variable set to an address the host does not have (Tailscale down, or in userspace mode,
  where the tailnet address is no host interface): the container does not start and Docker reports the port binding error; the
  README and `docs/INSTALL.md` name this symptom, the TUN prerequisite and how to switch the Synology package to TUN mode.
- E19: The backend host reboots and Docker starts the container before Tailscale has brought up its address: the documentation names
  what the operator sees and the remedy (see *Non-functional requirements*, runtime facts to verify).

## Non-functional requirements

- **Limits:** compressed body at most 2 MiB (2,097,152 bytes); decompressed batch at most 2 MiB, line at most 1 MiB, at most 20,000
  records (the wire format's own line and record limits; its 16 MiB batch default is lowered for ingest). Every batch the agent's
  sender builds fits: at most 20,000 records and 1 MiB of record lines, plus one line feed per line and the header line.
- **Time:** waiting, reading and decoding of one request take at most 30 seconds (the agent's request timeout); the existing header
  timeout (5 s), header size (16 KiB) and keep-alive (120 s) stay. Once started, the write runs under a cancellation that neither the
  request's abort nor the 30-second bound triggers; only the shutdown deadline ends it (the store's writer is never interrupted in
  the middle, Storage area).
- **Concurrency and memory:** at most two requests hold a slot at once across the service (the agent's two streams); a slot covers
  reading, decoding, the materialized batch, the wait for the writer and the write; at most 16 connections are open on the ingest
  listener. The peak memory of one request at the limits — the decoder with the hostile line shapes of #175 **and** the materialized
  batch of 20,000 records from 2 MiB of JSON through the write — is measured on the backend (the area document's figures were
  measured on the Go decoder alone), and the two slots together stay within 128 MiB of the container's 512 MiB limit; the measured
  figures go into the Wire format area (*Duties of the ingest API*).
- **Rate:** the request rate is bounded by the two decode slots and the one writer connection; there is no request-per-second cap
  (see *Open questions*).
- **Security:** the token is checked before the body is read, in time that depends neither on how much of the presented token is
  right nor on its length (the issue names `CryptographicOperations.FixedTimeEquals`); the token, the `Authorization` header, the
  body, record contents and parser texts never appear in a log line or an answer; the token is held as the existing `Secret` type.
  The tailnet binding is defense in depth: the token is the boundary of ingest (records 0060, 0017; Backend host area, *Exposure
  and trust*). Security areas 1, 2, 8, 10 and 12 of `.squad/project.md` apply.
- **Compatibility:** the request and answers are those of record 0124, which the released agent code already relies on; wire format
  1.0 and 1.1 batches are accepted. The backend still starts without the token or `ingest.agent_id` (E4), so an import-only setup
  keeps working. The Dockerfile keeps no `EXPOSE`.
- **New configuration:** backend option `ingest.agent_id`, no default (optional: while unset ingest is disabled, E4), the agent ID
  rule of the wire format (1 to 64 characters of `A-Z a-z 0-9 . _ -`, starting with a letter or digit), compared exactly; an invalid
  value stops the service at start with an error that names the key and never the value. Compose variable `INGEST_BIND_ADDRESS`,
  required, no default (E18).
- **Deployment:** supported is Tailscale in TUN mode on the backend host (the tailnet address is a host interface); userspace
  networking is not supported, because the address cannot be bound there and Tailscale forwards inbound tailnet connections to
  `127.0.0.1`, where the web port is published, which would leave only the ACL between the tailnet and the web UI. Runtime facts that
  no one can check in a cloud session, named here for the plan to verify or mark: the bind failure in userspace mode, the Synology
  steps to switch to TUN mode, the boot-order behavior of E19 (Docker's userland proxy binds the address, the iptables-only path may
  not), and that Synology Container Manager and QNAP Container Station honor `${VAR:?message}`.
- **Logs:** the lines of S7 and E1 to E11 above, JSON through `BackendLog` `[LoggerMessage]` methods; no info line per batch.

## Acceptance criteria

- [ ] AC1: A valid batch posted with the token by the configured agent is answered `204` without a body only after its transaction
  committed, and every record is stored with origin `agent`, the batch's agent ID, boot ID and clock offset, its sequence number, and
  one receive time per batch taken from the service clock when the request reached the endpoint (S1, S2).
- [ ] AC2: A batch whose records are all or partly stored already is answered `204`; only the new records are stored and no stored row
  changes, its receive time included; two concurrent requests with overlapping records both get `204` and store each record once; a
  stored sequence number with different content is never replaced (S3, S4, S5, E15, E17).
- [ ] AC3: Every request form of E1 is answered `401` with `WWW-Authenticate: Bearer` and no refusal header, without a byte of the
  body read; the warning `ingest token refused` carries `peer` and `reason` and never the presented value, and repeated refusals log
  at most one line a minute with the `suppressed` count (E1, E2).
- [ ] AC4: The token comparison takes the same steps for a presented token of any length and any number of matching leading
  characters (fixed-length comparison through `CryptographicOperations.FixedTimeEquals`), confirmed by a test of the comparison and by
  the reviews (E1).
- [ ] AC5: A batch whose header `agent_id` differs from `ingest.agent_id`, ordinally, is answered `403` without a refusal header, stores
  nothing, and logs `ingest agent refused` with the presented `agent_id` and `peer`, rate-limited as in AC3 (E3).
- [ ] AC6: Without the token or without `ingest.agent_id` the service starts, logs `ingest disabled` once with the missing names, keeps
  serving the web UI, and answers every `POST /v1/batches` with `503`; an invalid `ingest.agent_id` stops the start with an error
  that names the key and not the value; the option is in `vandoxd.yaml` (commented out with an example), the README options table and
  the configuration tests (E4).
- [ ] AC7: A body over 2 MiB compressed (announced by `Content-Length`, or chunked), over 2 MiB decompressed, with more than 20,000
  records or a line over 1 MiB is answered `413` with `Vandox-Refused: batch` and stores nothing; a `Content-Length` over the limit is
  answered before the body is read (E5, E6).
- [ ] AC8: Each body of E7 is answered `400` (unreadable) or `422` (rule broken) with `Vandox-Refused: batch` and stores nothing, also
  when the error comes after well-decoded records; a newer minor with unknown keys is stored with those keys dropped and one with an
  unknown kind is answered `422`; the warning `ingest batch refused` carries only `agent_id`, `status`, a fixed `reason`, `line` and
  `field` (E7, E8).
- [ ] AC19: `BatchDecoder` refuses any byte after the last complete gzip member as `Malformed` (garbage, a single byte, zero padding)
  while still reading concatenated members, and `testdata/wire/decoder-cases.json` holds these cases so the Go and the C# decoder
  are both pinned to them (E7).
- [ ] AC9: An unsupported major, a content type other than `application/gzip` and any `Content-Encoding` are answered `415` without the
  refusal header and store nothing; `Application/GZIP` is accepted and every other content type form of E9 is answered `415`; for
  the content type and encoding no byte of the body is read; the warning `ingest request not accepted` names `status` and the fixed
  `reason` (E9).
- [ ] AC10: A store failure during the write rolls the batch back and is answered `503` without the refusal header, logged as
  `ingest store failed` with `agent_id` and the exception type; the same batch sent again later is stored and answered `204` (E10).
- [ ] AC11: At most two requests hold a slot at once, from the body read through the commit or rollback, and further ones wait; a
  request whose connection is aborted after its write started still commits; a request that has not finished waiting, reading
  and decoding 30 seconds after its arrival gets no `2xx`, stores nothing and logs `ingest request timed out`, also for a body that
  trickles in; a 17th concurrent connection to the ingest listener is closed without affecting the web listener (E11, E12, E13).
- [ ] AC12: The peak memory of one request at the limits — the C# decoder with the hostile shapes of #175 and the materialized batch of
  20,000 records from 2 MiB of JSON through its write — is measured, a test bounds it,
  the two slots together stay within 128 MiB, and the figures are written in the Wire format area (*Duties of the ingest API*) in
  place of the Go-only statement (E6, E11).
- [ ] AC13: On the ingest listener every request other than `POST /v1/batches` is answered `404`; `POST /v1/batches` on the web
  listener stores nothing and is never answered `2xx`; ingest answers carry no web security headers (E14).
- [ ] AC14: A request in flight at shutdown either commits and is answered `204`, or stores nothing and gets no `2xx` (E16).
- [ ] AC15: The first stored batch after a start logs the info line `ingest first batch stored` with `agent_id`, every stored batch
  the debug line `ingest batch stored` with `agent_id`, `mode`, `records`, `stored` and `duplicates`, and no ingest log line of any
  scenario or edge case contains the token, the `Authorization` header, body bytes or record content (S7, E1, E7).
- [ ] AC16: `deploy/backend/docker-compose.yml` publishes `${INGEST_BIND_ADDRESS:?<message>}:8081:8081` beside the unchanged web
  entry, `docker compose config` without the variable fails with a message naming it, and the Dockerfile still declares no `EXPOSE`
  (S6, E18).
- [ ] AC17: The CI smoke test sets `ingest.agent_id: agent-1` (the golden batch's agent ID) in its copy of `deploy/backend/vandoxd.yaml`,
  starts the compose file with `INGEST_BIND_ADDRESS=127.0.0.1` and checks that 8081 is bound on exactly that
  address, that a gzip of the golden batch `testdata/wire/all-kinds.jsonl` posted with the token is answered `204` and a repeat too,
  that a wrong token gets `401`, that `/healthz` on the ingest port gets `404`, and that the compose file refuses to start without the
  variable (S1, S3, S6, E1, E14, E18).
- [ ] AC18: The compose file's comments, `README.md`, `docs/INSTALL.md` and `SECURITY.md` state the TUN prerequisite, how to read the
  tailnet address (`tailscale ip -4`), how to switch the Synology package to TUN mode, the symptoms of E18 and E19 with their remedies,
  why userspace mode is not supported, and that the token, not the binding, is the boundary; a decision record holds the choice of
  the Tailscale mode and the binding (S6, E18, E19).

## Out of scope

- Classifying records as live or backfilled from the data (#41); the header `mode` is logged, not stored or trusted.
- Several agents with one token each (#89): this change binds the one token to one agent ID; the variable scheme for several tokens
  supersedes it there (record 0050).
- The example Tailscale ACL policy and its test from the monitored server (#43).
- A request-per-second cap and disk quotas: disk growth is retention's job (#46); a full disk is E10.
- TLS on the ingest listener: the tailnet encrypts and authenticates the transport (Agent area, *Connection*; record 0010).
- Upgrade rules between agent and backend versions (#85); E8 follows the current rule "upgrade the backend first".
- Gap detection from missing sequence numbers (`sequence_missing`, #41).

## Open questions

- Which agent does "the per-agent token" belong to, when the backend has the single secret `VANDOX_AGENT_TOKEN`?
  Answered: record 0050 keeps one token until #89; the Wire format area (*Duties of the ingest API*) requires checking the header's
  `agent_id` against the agent the token belongs to. The backend gets the option `ingest.agent_id` that names that agent, with no
  default and ingest disabled while it is unset (E4), so a token copied to a second server cannot mix two servers' data into the one
  the web UI shows. The plan records the choice and its alternatives (accepting any agent ID with the token; binding to the first
  agent ID stored).
- What do "size and rate limits" mean for the ingest of one agent?
  Answered: the issue names the Kestrel body limit and request timeouts; security area 1 asks that a valid agent cannot exhaust
  memory or disk. Memory is bounded by the body, batch and line limits, the two decode slots and the connection cap; time by the
  30-second bound and Kestrel's existing header and keep-alive limits. A request-per-second cap would answer the agent's backfill with a
  non-`2xx`, which starts the shared backoff of both streams and holds current data back (record 0125), and it would not stop disk
  growth, which retention (#46) bounds; so there is none.
- Which statuses answer which failure?
  Answered: record 0124 and the Backend host area (*Ingest endpoint*) fix the classes: `2xx` only after the commit; `400`, `413` and
  `422` with `Vandox-Refused: batch` only for a batch invalid as a whole, on an authenticated request; everything else outside those
  three without the header (`401` or `403` for the token, a status outside the three for an unsupported version). The spec picks
  `204`, `401`, `403`, `415` and `503` within these classes.
- Which Tailscale mode is supported on the backend host, and is the bind address variable required?
  Answered: the issue's comment asks for a documented prerequisite such as TUN mode, and record 0060 rejected a required variable and
  a documented TUN prerequisite only because the port served nothing yet. TUN mode is supported, userspace mode is not (see
  *Non-functional requirements*, *Deployment*), and `INGEST_BIND_ADDRESS` is required, so the port is never published on an address
  the operator did not choose (security area 2: the ingest port is bound only to the tailnet address). An import-only setup without
  Tailscale sets it to `127.0.0.1`, which the documentation shows.
- Does storage need a change for the receive time and the deduplication?
  Answered: no. `records.received_at` exists and `BatchWriter` writes `RecordBatch.ReceivedAt` for every record; deduplication is the
  partial unique index on `(agent_id, seq)` with `ON CONFLICT DO NOTHING` (Storage area, *Writing*; `src/Vandox.Storage/BatchWriter.cs`).

## Area documents

- `docs/areas/backend-host.md`: *Ingest endpoint* from planned to implemented, with the statuses, the refusal header, the order of the
  checks, the limits, the concurrency, the timeouts and the log lines of this spec; *Listeners and routing* (the ingest listener
  serves `POST /v1/batches` and `404` otherwise, the connection cap); *Compose file* (the ingest `ports` entry, the required variable,
  the smoke test's new checks); *Exposure and trust* (TUN mode supported, userspace not, and why).
- `docs/areas/wire-format.md`: *Duties of the ingest API* rewritten for the .NET endpoint: the compressed and decompressed limits it
  uses, the measured C# memory of one request and the two slots, the mapping of the decoder's errors to statuses; *Not here* points to
  the implemented endpoint; *Accepted forms* and *Implementation* name the trailing-bytes cases now pinned in the shared fixture for
  both decoders.
- `docs/areas/configuration-and-secrets.md`: the option `ingest.agent_id`; the backend now uses `VANDOX_AGENT_TOKEN`, and ingest is
  disabled without it or the option.
- `docs/areas/storage.md`: the ingest path is no longer planned: it acknowledges after the commit and supplies the receive time.
- `docs/areas/network-and-security-model.md`: *Agent to backend* is implemented; the backend host needs Tailscale in TUN mode.

## Challenge

- M1 (major, trailing bytes after the gzip data): accepted. Checked: `BatchDecoder.OpenAsync` wraps the input in a plain `GZipStream`
  and nothing reads the input after it ends. The decoder fix is now in scope (E7, new AC19): any byte after the last complete member
  is `Malformed`, concatenated members stay accepted, and the cases go into `testdata/wire/decoder-cases.json` so both decoders are
  pinned. Dropping the case would have left the Wire format area's *Accepted forms* untrue for the backend on the one path that
  takes agent input.
- M2 (major, decoded batches waiting for the writer outside the bound): accepted. Checked: `SqliteStore.WriteBatchAsync` takes a
  materialized `RecordBatch` and then waits on `_writeLock`. A slot is now held from the body read through the commit or rollback
  (E11, *Concurrency and memory*, AC11), so at most two decoded batches exist at once, and AC12 measures one request's peak including
  the materialized batch of 20,000 records from 2 MiB of JSON through the write, not the decoder alone.
- m1 (minor, a write must not be cancellable by the request): accepted. E11 and *Time* now say the write runs under a cancellation
  that neither the request's abort nor the 30-second bound triggers, only the shutdown deadline; AC11 adds that a request aborted
  after its write started still commits.
- m2 (minor, unknown keys are not stored): accepted. E8 and AC8 now say the records are stored and unknown keys are dropped, because
  the store writes the payload of the backend's own model.
- m3 (minor, smoke test agent ID): accepted. Checked: the golden header has `agent_id` `agent-1`. AC17 now has the smoke test set
  `ingest.agent_id: agent-1` in its copy of the example configuration.
- m4 (minor, content type comparison): accepted. E9 fixes the rule (exactly `application/gzip` ignoring ASCII case, no parameter, no
  surrounding space, one header) with accepted and refused forms, and AC9 names the accepted form and the refusal of the others.
