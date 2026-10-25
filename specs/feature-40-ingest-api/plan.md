# Plan: Ingest API

Source: Issue #40 | [spec.md](spec.md)
Status: Draft
Tier: security — the change creates the ingest authentication (token check, agent binding), new network exposure (the ingest endpoint and its published port) and request bounds, all security controls.
Security areas touched: 1 Ingest authentication (`Vandox.Backend.Ingest`, `BatchWriter` use); 2 Tailscale ACL and port binding (`deploy/backend/docker-compose.yml`, TUN prerequisite); 8 Secrets handling (`VANDOX_AGENT_TOKEN` now used by the backend, `IngestToken`); 10 Parsing of external input (`BatchDecoder`, new `GzipMemberReader`, `IngestLimits`); 12 Logging of external data (ingest log lines with `agent_id` and peer); 13 Release pipeline (`.github/scripts/smoke-test-backend.sh`, compose file); Docker/CI configuration (compose file, smoke test); no dependency change

## Problem / root cause

See [spec.md](spec.md), *Problem*.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The ingest listener exists and every ingest request is answered `404` by the port routing | `src/Vandox.Backend/Hosting/ServeCommand.cs` `Listen` (line 290), `Hosting/PortRoutingMiddleware.cs` `InvokeAsync` (lines 38-55) | confirmed |
| 2 | Deduplication by (agent ID, seq) with `ON CONFLICT DO NOTHING`; `received_at` is written from `RecordBatch.ReceivedAt` for every record | `src/Vandox.Storage/BatchWriter.cs` lines 48-49, 104; Storage area *Writing* | confirmed |
| 3 | `WriteBatchAsync` takes a materialized `RecordBatch`, waits on `_writeLock` and passes its token to the lock wait, the transaction, every record and the commit | `src/Vandox.Storage/SqliteStore.cs` lines 455-485, `BatchWriter.Write` | confirmed |
| 4 | `SqliteStore.DisposeAsync` disposes the writer without taking `_writeLock`, so a write still running at disposal races it | `SqliteStore.cs` `DisposeAsync` (end of file) | confirmed |
| 5 | The agent sends `POST /v1/batches`, at most 20,000 records and 1 MiB of record lines, reads `401`/`403` as `token refused`, a refusal only as `400`/`413`/`422` with `Vandox-Refused: batch` | `internal/sender/sender.go` lines 45-67, 431; `internal/sender/transport.go` `classifyAnswer` | confirmed |
| 6 | A spool read never exceeds 1 MiB of lines, also for one record of 1 MiB | `internal/spool/read.go` line 192 (`job.bytes + len > maxBytes` with zero records only for a line over 1 MiB, which the spool never holds) | confirmed |
| 7 | `GZipStream` with strict validation accepts bytes after a member that do not start a header | DA measurement on .NET 10.0.402; repeated in a scratch program: one member plus 20 zero bytes decodes to its content | confirmed |
| 8 | A raw `DeflateStream` fed one byte per `Read` consumes exactly the deflate data and leaves trailing bytes unread | scratch program (2,000,000 random bytes: 2,000,615 deflated, 2,000,615 consumed, 3 trailing bytes left, 542 ms; text: 10,692 of 10,692, 2 ms) | confirmed |
| 9 | Go's `compress/gzip` rules: magic `1f 8b 08`; flags not otherwise checked; extra field read; name and comment NUL-terminated within 512 bytes (at most 511); `FHCRC` compared with the low 16 bits of the header CRC-32; trailer CRC-32 and ISIZE checked; after a member: exact end is EOF, 1-9 bytes is unexpected EOF, a non-header is `ErrHeader` | `$(go env GOROOT)/src/compress/gzip/gunzip.go` `readHeader`, `readString`, `Read` | confirmed |
| 10 | The golden batch's header has `agent_id` `agent-1` | `testdata/wire/all-kinds.jsonl` line 1 | confirmed |
| 11 | Kestrel removes spaces and tabs around a header value and refuses a control character in a header with `400` before the pipeline runs | to verify: a `ServeTests` case against real Kestrel (`Authorization: Bearer <token>` plus a trailing space gives `204`); owner Tester, step 5 | unverified |
| 12 | Kestrel collects two `Authorization` headers into one `StringValues` of two entries | to verify: the same `ServeTests` case with two headers gives `401`; owner Tester, step 5 | unverified |
| 13 | Peak memory of one request at the limits (hostile 1 MiB line; 20,000 records from 2 MiB of JSON through the write) times two stays within 128 MiB | to verify: `IngestEndpointTests` heap and allocation bounds (*Tests per criterion*, AC12); owner Tester, step 6; a breach goes to the Lead (fewer slots or lower limits) | unverified |
| 14 | In userspace mode the tailnet address cannot be bound by Docker, and Tailscale forwards inbound tailnet connections to `127.0.0.1` | to verify: on the Synology host by the PM before release (#45); source so far the issue #40 comment and record 0060; the Lead's docs step states it as reported, not measured | unverified |
| 15 | Synology's package switches to TUN mode with `tailscale configure-host` and a package restart, repeated at boot by a triggered task | to verify: Tailscale's Synology documentation read by the Lead in the docs step and quoted with its link; the PM confirms on the host (#45) | unverified |
| 16 | Docker's userland proxy binds the host address (fails while it is missing), the iptables-only path does not; a container whose bind fails at daemon start is not restarted by `unless-stopped` | to verify: CI smoke test cannot check it (no tailnet); the Lead states it as a possible symptom with the remedy `docker compose up -d` after Tailscale is up, not as a fact | unverified |
| 17 | `docker compose config` fails for `${INGEST_BIND_ADDRESS:?message}` when the variable is unset; Synology Container Manager and QNAP Container Station run Compose v2 and honor it | to verify: the smoke test (CI job *Release build check*) for Compose v2; NAS managers by the PM before release (#45) | unverified |

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `IngestEndpointValidBatchAnswers204AfterCommit`, `IngestEndpointStoresReceiveTimeAndContextForEveryRecord` (real `SqliteStore`, `FakeTimeProvider`) | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC2 | `IngestEndpointResentBatchStoresNothingNew`, `IngestEndpointOverlappingBatchStoresOnlyNewRecords`, `IngestEndpointConcurrentOverlappingBatchesStoreEachRecordOnce`, `IngestEndpointStoredRecordWithOtherContentIsKept` | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC3 | `IngestTokenRefusesEveryOtherForm` (data rows: no header, empty, `Basic`, wrong token, prefix, longer, shorter, two spaces, tab, two values, non-ASCII), `IngestTokenAcceptsSchemeInAnyCase`; `IngestEndpointRefusedTokenReadsNoBody` (counting body stream), `IngestEndpointRefusedTokenLogsPeerAndReasonOnly`; `IngestRefusalLogAllowsOneLinePerMinuteWithSuppressedCount`; `ServeIngestHeaderWhitespaceAndDuplicates` (facts 11, 12) | `tests/Vandox.Backend.Tests/IngestTokenTests.cs`, `IngestEndpointTests.cs`, `IngestRefusalLogTests.cs`, `ServeTests.cs` |
| AC4 | `IngestTokenComparesFixedLengthDigests` (a probe of the comparison input: both sides 32 bytes for presented tokens of 1, 64 and 16,000 characters) | `tests/Vandox.Backend.Tests/IngestTokenTests.cs` |
| AC5 | `IngestEndpointOtherAgentIdAnswers403`, `IngestEndpointAgentIdIsComparedOrdinally` | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC6 | `IngestSettingsMissingTokenOrAgentIdDisablesIngest`; `IngestEndpointDisabledAnswers503`; `ServeIngestDisabledLogsOnceAndServesWeb`; `BackendConfigLoaderReadsIngestAgentId`, `BackendConfigLoaderRefusesInvalidIngestAgentIdWithoutValue` (rows from the wire agent ID rule), existing `BackendConfigLoaderLoadsRepositoryExample` and key-list tests adapted | `tests/Vandox.Backend.Tests/IngestSettingsTests.cs`, `IngestEndpointTests.cs`, `ServeTests.cs`, `tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs` |
| AC7 | `IngestEndpointContentLengthOverLimitAnswers413BeforeReading`, `IngestEndpointChunkedBodyOverLimitAnswers413`, `IngestEndpointOversizedBatchAnswers413` (rows: decompressed over 2 MiB, 20,001 records, line over 1 MiB, 1 KB body to a hostile line) | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC8 | `IngestEndpointUnreadableBodyAnswers400Refused` (rows: not gzip, empty, truncated, bad CRC, trailing bytes, bad JSON, key rule), `IngestEndpointInvalidBatchAnswers422Refused` (rows: field error, unknown kind, no records, sequence, capture time outside the storable range), `IngestEndpointErrorAfterGoodRecordsStoresNothing`, `IngestEndpointNewerMinorStoresWithUnknownKeysDropped`, `IngestEndpointRefusedBatchLogsFixedReasonOnly` | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC9 | `IngestEndpointUnsupportedMajorAnswers415`, `IngestEndpointContentTypeForms` (rows: `Application/GZIP` accepted; parameter, `application/x-gzip`, empty, missing, two headers refused), `IngestEndpointContentEncodingAnswers415WithoutReading` | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC10 | `IngestEndpointStoreFailureAnswers503AndStoresNothing` (scripted writer failure; and a real store whose writer lock another connection holds beyond the busy timeout), `IngestEndpointResendAfterStoreFailureIsStored` | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC11 | `IngestEndpointAtMostTwoRequestsHoldASlot` (gated writer counts concurrency), `IngestEndpointSlotIsHeldUntilTheCommit`, `IngestEndpointAbortedRequestStillCommits`, `IngestEndpointRequestOverBudgetGetsNo2xx` (rows: slot wait, trickling body; `FakeTimeProvider`), `ServeIngestSeventeenthConnectionIsClosed` | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs`, `ServeTests.cs` |
| AC12 | `IngestEndpointHostileLineStaysWithinMemoryBound`, `IngestEndpointMaximalBatchStaysWithinMemoryBound` (heap bound with `GC.GetTotalMemory(true)` sampled while the gated write waits, allocation bound beyond a baseline) | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC13 | `PortRoutingMiddlewareSendsOnlyPostV1BatchesToIngest` (rows: other path, `/v1/batches/`, `/V1/batches`, `GET`, `/healthz`), existing ingest-`404` cases of `ServeTests` kept, `ServeWebPortPostV1BatchesStoresNothing`, `ServeIngestAnswerCarriesNoSecurityHeaders` | `tests/Vandox.Backend.Tests/PortRoutingMiddlewareTests.cs`, `ServeTests.cs` |
| AC14 | `ServeShutdownDuringIngestWriteCommitsOrStoresNothing` (gated writer released after the stop signal), `ServeShutdownDrainsIngestWritesBeforeClosingStore` | `tests/Vandox.Backend.Tests/ServeTests.cs` |
| AC15 | `IngestEndpointLogsFirstBatchOnceAndEveryBatchAtDebug`, `IngestEndpointLogsHoldNoTokenOrContent` (a sentinel token and sentinel record text, every ingest log line of the other tests' scenarios checked) | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC19 | shared decoder cases `G1`-`G13` run by `DecodeSharedCaseGivesExpectedResult` and `TestDecoder_SharedCases`; `SharedCasesHaveUniqueNamesAndHoldEveryRequiredCase` and `requiredCaseIDs` extended by `G`; `GzipMemberReaderReadsConcatenatedMembers`, `Crc32MatchesReferenceVectors` | `testdata/wire/decoder-cases.json`, `tests/Vandox.Core.Tests/WireContractTests.cs`, `internal/wire/decode_test.go`, `tests/Vandox.Core.Tests/GzipMemberReaderTests.cs`, `tests/Vandox.Core.Tests/Crc32Tests.cs` |

## Verification without tests

Only the deployment criteria; the rest of the change has tests.

- AC16: the Dev's edit of `deploy/backend/docker-compose.yml`, checked by the smoke test of AC17 in CI (`docker compose config` without the variable fails; the binding) and by the Reviewer: `grep -n EXPOSE deploy/backend/Dockerfile` finds nothing.
- AC17: `.github/scripts/smoke-test-backend.sh`, run by the CI job *Release build check* on the PR head; locally `bash -n` and the *Analyzer gate* `--lint-only` (shellcheck) by the Dev (no Docker in the session, `.squad/stack.md`, *Not checkable in a cloud session*).
- AC18: the documentation edits, read by the Reviewer against facts 14-17 (each stated as the plan marks it) and record 0127.

## Approach

**Routing.** `PortRoutingMiddleware` takes the `IngestEndpoint` from DI. On an ingest connection it calls the endpoint only for method `POST` and path exactly `/v1/batches` (ordinal, no trailing slash, query ignored); every other ingest request stays `404`. The web pipeline (forwarded headers, security headers, login, antiforgery) never runs on ingest. `ListenerRoutes.Ingest` adds a `ConnectionCap` of 16 to its listener (one instance per listener; a 17th connection is aborted before Kestrel reads a request).

**Endpoint, in this order** (each answer without a body; `R` = `Vandox-Refused: batch`):

| Step | Condition | Answer, log |
| ---- | --------- | ----------- |
| 1 | receive time = `TimeProvider.GetUtcNow()` | — |
| 2 | no `IngestSettings` (token or `ingest.agent_id` unset) | `503` |
| 3 | `IngestToken.Matches` false | `401`, `WWW-Authenticate: Bearer`; `ingest token refused` (`peer`, `reason` `missing`/`wrong`), rate-limited |
| 4 | any `Content-Encoding`; `Content-Type` not one value equal to `application/gzip` ignoring ASCII case | `415`; `ingest request not accepted` (`status`, `reason` `content encoding`/`content type`) |
| 5 | `Content-Length` > 2 MiB | `413` R; `ingest batch refused` (`reason` `body too large`) |
| 6 | 30-second budget (from step 1, `TimeProvider`, linked with `RequestAborted`) starts; wait for one of 2 slots | budget passed: `503`; `ingest request timed out` |
| 7 | read the body into a pooled buffer of at most 2 MiB (`MaxRequestBodySize` feature set as backstop) | over: `413` R; budget passed or `BadHttpRequestException` (data rate): `408` where possible, `ingest request timed out`; aborted: nothing |
| 8 | `BatchDecoder.OpenAsync` with `IngestLimits.Wire`; header `agent_id` ≠ `ingest.agent_id` (ordinal) | `403`; `ingest agent refused` (`agent_id`, `peer`), rate-limited |
| 9 | `NextAsync` to the end into a list | `WireException`: `Malformed` → `400` R, `LimitExceeded` → `413` R, `UnknownKind`/`Sequence`/`EmptyBatch`/`Invalid` → `422` R, `UnsupportedVersion` → `415`; `ingest batch refused` / `ingest request not accepted` with `reason` from `IngestStatus.Reason(kind)`, `line`, `field` (`FieldError.Field` and `Reason` only) |
| 10 | `WriteBatchAsync(batch, CancellationToken.None)`, registered with the endpoint's write tracker | `InvalidBatchException` → `422` R (`reason` `refused by the store`); other `StoreException`, `SqliteException`, `IOException` → `503`, `ingest store failed` (`agent_id`, `exception` type name) |
| 11 | release the slot (finally, after step 10) | `204`; debug `ingest batch stored`; info `ingest first batch stored` once per process |

The refusal header is set only in steps 5, 7, 9 and 10. The rate limit (`IngestRefusalLog`, one per message) lets one line through when a minute has passed since the last one, with `suppressed` the count of refusals in between. `ServeCommand` calls `IngestEndpoint.DrainAsync` after `app.StopAsync` and before the store is disposed (fact 4). `IngestSettings.Create` logs `ingest disabled` with `missing` once at start.

**Decoder.** `BatchDecoder.OpenAsync` reads through `GzipMemberReader` instead of `GZipStream` (record 0128): the header rules of fact 9, a raw `DeflateStream` per member fed one byte per read from the reader's own buffer, CRC-32 (`Crc32`, table in code) and ISIZE of the output against the trailer; after a trailer, end of input ends the stream, else the next bytes must be a header. Every framing error is `WireException` `Malformed`, raised at the read where Go raises it (records before it are returned). `StrictGzip.Require()` stays in `OpenAsync`.

**Shared cases.** Each case of `testdata/wire/decoder-cases.json` may carry `"gzip": {"members": n, "trailing_hex": "…", "name_bytes": n, "comment_bytes": n, "extra_bytes": n, "header_crc": "right" | "wrong"}`; both loaders build the stream from it (Go with `DisallowUnknownFields`, so the struct gains the field). Cases: `G1` 20 garbage bytes, `G2` one zero byte, `G3` 16 zero bytes, `G4` `1f 8b` alone, `G6` two members then garbage → records then `Malformed`; `G5` two members → ok; `G7` name 511 ok, `G8` name 512 `Malformed`, `G9`/`G10` the same for the comment, `G11` extra field of 100 bytes ok, `G12` right and `G13` wrong header CRC.

**Compose and smoke test.** Ports entry `"${INGEST_BIND_ADDRESS:?set INGEST_BIND_ADDRESS to this host's tailnet address (tailscale ip -4), or 127.0.0.1 without agents}:8081:8081"`. The smoke test keeps the token in a variable before `chown`, adds `ingest:\n  agent_id: agent-1` to its copy of `vandoxd.yaml`, exports `INGEST_BIND_ADDRESS=127.0.0.1`, replaces the "no ingest binding" check by "8081 bound on exactly `127.0.0.1`", posts the gzip of `all-kinds.jsonl` twice (`204`, `204`), a wrong token (`401`), `GET /healthz` on 8081 (`404`), and runs `docker compose config` once with the variable unset (fails, message names it).

Log lines are fixed texts as above; layout of the documentation free for the Lead, approved in step 9.

## Options considered

- Token to agent binding, slot scope, rate cap, digest comparison: record 0126.
- Tailscale mode and the bind variable (default `127.0.0.1`, userspace support, all interfaces): record 0127.
- Gzip framing (keep `GZipStream`, inflate twice, own reader, `System.IO.Hashing`): record 0128.
- Endpoint inside the web pipeline with `MapPost` and an endpoint filter for the listener: rejected; the web middlewares (login fallback policy, antiforgery, security headers) would run on ingest requests, and the fail-closed routing of record 0081 is simpler to keep with one branch in `PortRoutingMiddleware`.
- Writing while decoding (one open transaction per request): rejected; it holds the single writer lock for the whole upload, so a slow agent blocks every other write, and record 0044's "commit only after `EOF`" is as easy with the list.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `Configuration/IngestConfig.cs` (new), `BackendConfig.cs`, `BackendConfigLoader.cs` | `ingest.agent_id` option, rule, key list |
| Vandox.Core | `Wire/GzipMemberReader.cs`, `Wire/Crc32.cs` (new), `Wire/BatchDecoder.cs` | own gzip framing |
| Vandox.Backend | `Ingest/IngestEndpoint.cs`, `IngestSettings.cs`, `IngestToken.cs`, `IngestLimits.cs`, `IngestStatus.cs`, `IngestRefusalLog.cs`, `ConnectionCap.cs` (new) | the endpoint |
| Vandox.Backend | `Hosting/PortRoutingMiddleware.cs`, `ListenerRoutes.cs`, `ServeCommand.cs`, `BackendLog.cs` | routing, cap, DI, drain, log methods |
| deploy | `backend/docker-compose.yml`, `backend/vandoxd.yaml` | ports entry and comments; commented `agent_id` |
| CI | `.github/scripts/smoke-test-backend.sh` | ingest checks |
| testdata | `wire/decoder-cases.json` | cases `G1`-`G13` |

## Signatures (for the Dev's skeleton)

- `public sealed class IngestConfig` with `[ConfigKey("listen")] public string Listen { get; set; }` and `[ConfigKey("agent_id")] public string? AgentId { get; set; }`; `BackendConfig.Ingest` becomes `public IngestConfig Ingest { get; }`
- `internal sealed class GzipMemberReader : Stream` with `internal GzipMemberReader(Stream input)` and the `ReadAsync(Memory<byte>, CancellationToken)` and `Read(Span<byte>)` overrides
- `internal static class Crc32` with `internal static uint Update(uint crc, ReadOnlySpan<byte> data)`
- `internal sealed class IngestEndpoint` with `internal IngestEndpoint(IngestSettings? settings, IRecordWriter writer, TimeProvider clock, ILoggerFactory loggerFactory)`, `internal Task HandleAsync(HttpContext context)`, `internal Task DrainAsync()`
- `internal sealed class IngestSettings` with `internal static IngestSettings? Create(BackendConfig config, ILogger logger)`, `internal string AgentId { get; }`, `internal IngestToken Token { get; }`
- `internal sealed class IngestToken` with `internal IngestToken(Secret token)`, `internal bool Matches(StringValues authorization)`, `internal static byte[] Digest(ReadOnlySpan<char> token)`
- `internal static class IngestLimits` with `MaxBodyBytes`, `MaxBatchBytes` (both `2 << 20`), `MaxLineBytes` (`1 << 20`), `MaxRecords` (20,000), `Slots` (2), `MaxConnections` (16), `RequestBudget` (30 s) and `internal static WireLimits Wire()`
- `internal static class IngestStatus` with `internal static int ForWireError(WireErrorKind kind)`, `internal static string Reason(WireErrorKind kind)`, `internal static bool IsRefusal(int status)`
- `internal sealed class IngestRefusalLog` with `internal IngestRefusalLog(TimeProvider clock)`, `internal bool TryTake(out int suppressed)`
- `internal sealed class ConnectionCap` with `internal ConnectionCap(int max)`, `internal ConnectionDelegate Wrap(ConnectionDelegate next)`
- `PortRoutingMiddleware(RequestDelegate next, IngestEndpoint ingest)` replaces `PortRoutingMiddleware(RequestDelegate next)`
- `BackendLog`: `IngestDisabled(this ILogger logger, string missing)`, `IngestTokenRefused(this ILogger logger, string peer, string reason, int suppressed)`, `IngestAgentRefused(this ILogger logger, string agentId, string peer, int suppressed)`, `IngestBatchRefused(this ILogger logger, string? agentId, int status, string reason, long? line, string? field)`, `IngestRequestNotAccepted(this ILogger logger, int status, string reason)`, `IngestRequestTimedOut(this ILogger logger, string peer)`, `IngestStoreFailed(this ILogger logger, string agentId, string exception)`, `IngestFirstBatchStored(this ILogger logger, string agentId)`, `IngestBatchStored(this ILogger logger, string agentId, string mode, int records, int stored, int duplicates)`

## Test files

`tests/Vandox.Backend.Tests/IngestEndpointTests.cs`, `IngestTokenTests.cs`, `IngestSettingsTests.cs`, `IngestRefusalLogTests.cs`, `ServeTests.cs`, `PortRoutingMiddlewareTests.cs`; `tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs`, `WireContractTests.cs`, `GzipMemberReaderTests.cs`, `Crc32Tests.cs`; `internal/wire/decode_test.go`; fixture `testdata/wire/decoder-cases.json`. New doubles (Tester): `RecordingRecordWriter` (an `IRecordWriter` that keeps every batch and token, counts writes in flight, can hold a write until released and fail with a scripted exception) and `CountingBodyStream` (a request body that counts reads), each in its own file of `tests/Vandox.Backend.Tests`.

Existing call sites: `PortRoutingMiddlewareTests` lines 31 and 64 (`new PortRoutingMiddleware(...)`) — Dev adapts in step 4 (old signature removed); `BackendConfigLoaderTests` key-list assertion (line 672) and the example-file test — Tester in step 5; `ServeTests` line 194 (`ListenerRoutes.Ingest` method group, unchanged signature) — none.

Files no unit test can cover: none (the compose file and smoke script are under *Verification without tests*).

## Areas

- `docs/areas/backend-host.md`: *Ingest endpoint* implemented (the step table above as behavior, limits, timeouts, slots, log lines); *Listeners and routing*; *Compose file* (ports entry, variable, smoke checks); *Exposure and trust* (TUN mode); *Implementation* (`Ingest/`).
- `docs/areas/wire-format.md`: *Duties of the ingest API* rewritten with the measured C# figures (fact 13); *Accepted forms* rows for trailing bytes and header rules naming the shared cases; *Implementation* (`GzipMemberReader`); *Not here*.
- `docs/areas/configuration-and-secrets.md`: `ingest.agent_id` row and rule; backend uses `VANDOX_AGENT_TOKEN`.
- `docs/areas/storage.md`: ingest path implemented (lines 17-19).
- `docs/areas/network-and-security-model.md`: *Agent to backend* implemented, TUN prerequisite.

## Documentation updates

Sweep of enumerations: `grep -n -i -E 'ingest|8081|not published|planned \(#40\)|#40' README.md SECURITY.md docs/*.md docs/areas/*.md .squad/project.md deploy/backend/*` — hits: README lines 16, 89, 151, 253; SECURITY.md line 46; `docs/ARCHITECTURE.md` lines 14, 15, 268; `docs/REVERSE_PROXY.md` line 8 (still true, unchanged); backend-host lines 6-8, 67-69, 77, 159-160, 171-173, 185-189; wire-format line 475, 531; configuration-and-secrets lines 283-284; network line 11-14; storage 18; `.squad/project.md` areas 1 and 2 and the test-double table; compose comments; `vandoxd.yaml` header.

| File | Owner |
| ---- | ----- |
| `docs/areas/*.md` (all five above), `docs/ARCHITECTURE.md` (status lines 14-15, 268; the ingest component) | Lead (mode `docs`) |
| `README.md` (status line 16, options table `ingest.agent_id`, line 151, lines 253-254: binding, TUN, `INGEST_BIND_ADDRESS` in `.env`), `docs/INSTALL.md` (Synology TUN steps, the variable, symptoms), `SECURITY.md` line 46 | Lead (mode `docs`) |
| `deploy/backend/docker-compose.yml` comments, `deploy/backend/vandoxd.yaml` (`agent_id` commented with an example, header lines 12-13) | Dev |
| `.squad/project.md` security areas 1 and 2 (implemented, the types, the binding) | Dev |
| `.squad/project.md` *Test doubles* row for `RecordingRecordWriter`, `CountingBodyStream` | Tester |

## Architecture check

- *No data gaps unless explicitly recorded*: `204` only after the commit (step 11 after step 10); a refused record becomes the agent's recorded gap (record 0125); a version mismatch, wrong token, full disk or timeout is never a refusal, so nothing is given up.
- *Backfilled data never alerts*: untouched; the header `mode` is logged only (#41).
- Two labelled listeners, fail closed (record 0081): kept; ingest gets one more branch, the web pipeline never runs on it.
- Ingest via the tailnet address (record 0017): the binding is published there; record 0127 amends 0060.

## Security considerations

- [ ] Limits: compressed body 2 MiB before decoding (on the raw request bytes); decompressed batch 2 MiB and line 1 MiB after gzip, before JSON (decoder); records 20,000 after decoding; the `Authorization` value is bounded by Kestrel's 16 KiB header limit before the token check.
- [ ] User-visible failure texts: none; every answer has no body, log lines carry fixed reasons, `FieldError.Field`/`Reason`, a line number and exception type names only; `WireException.Message` (which can wrap parser text) and `StoreException.Message` are never logged by the endpoint.
- [ ] Guard of the token, the forms the parser accepts (Kestrel header parsing, fact 11 and 12): one value only; scheme `Bearer` in any ASCII case; one space; the rest hashed as is (non-ASCII and controls cannot match the printable-ASCII token); OWS trimmed by Kestrel; query string, cookies and other headers never read.
- [ ] Content type guard: one value, exactly `application/gzip` ignoring ASCII case; `Content-Encoding` present in any form refused, so no server decompression can run (record 0124).

## Decision records

- `docs/decisions/0126-ingest-endpoint-token-bound-to-one-agent-id-two-slots-through-the-commit-no-rate-cap.md` (Proposed)
- `docs/decisions/0127-backend-host-needs-tailscale-in-tun-mode-ingest-port-on-a-required-bind-address.md` (Proposed, amends 0060)
- `docs/decisions/0128-csharp-decoder-reads-the-gzip-framing-itself-to-match-the-go-decoder.md` (Proposed, amends 0076)

## Out of scope / follow-ups

- Spec *Out of scope*. Related defect found: none beyond fact 4, which this plan fixes (`DrainAsync`). The import's `GZipStream` keeps accepting trailing bytes after an archive (record 0128, *Consequences*), deliberately.
