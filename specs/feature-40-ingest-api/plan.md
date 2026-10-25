# Plan: Ingest API

Source: Issue #40 | [spec.md](spec.md)
Status: Revised (1)
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
| 11 | Kestrel removes spaces and tabs around a header value; it lets U+0001 through to the application; it answers obsolete line folding with its own `400`; a bare LF ends the header line and the rest becomes a header of its own | Tester's `ServeIngestHeaderWhitespaceAndDuplicates` against real Kestrel, step 5: `204` for trailing space and tab, `401` for U+0001, `400` for folding | confirmed |
| 12 | Kestrel collects two `Authorization` headers into one `StringValues` of two entries | the same test, step 5: two headers give `401` | confirmed |
| 13 | Peak memory of one request at the limits (hostile 1 MiB line; 20,000 distinct log lines filling 2 MiB and the largest connection snapshot through a real `SqliteStore` write, its `TemplateTally`, `_labelBuffer`, `ListenerMarkWriter` state and SQLite's native memory included) times two stays within 128 MiB | `IngestEndpointMaximalBatchStaysWithinMemoryBound`, `IngestEndpointHostileLineStaysWithinMemoryBound` (Tester, step 6): worst request 18.8 MB, about 42 MB for two slots; figures in the Wire format area | confirmed |
| 14 | In userspace mode the tailnet address cannot be bound by Docker, and Tailscale forwards inbound tailnet connections to `127.0.0.1` | to verify: on the Synology host by the PM before release (#45); source so far the issue #40 comment and record 0060; the Lead's docs step states it as reported, not measured | unverified |
| 15 | Synology's package switches to TUN mode with `tailscale configure-host` and a package restart, repeated at boot by a triggered task | to verify: Tailscale's Synology documentation read by the Lead in the docs step and quoted with its link; the PM confirms on the host (#45) | unverified |
| 16 | Docker's userland proxy binds the host address (fails while it is missing), the iptables-only path does not; a container whose bind fails at daemon start is not restarted by `unless-stopped` | to verify: CI smoke test cannot check it (no tailnet); the Lead states it as a possible symptom with the remedy `docker compose up -d` after Tailscale is up, not as a fact | unverified |
| 18 | Microsoft.Data.Sqlite retries `SQLITE_BUSY` until the connection's `DefaultTimeout` (30 s by default) on top of `busy_timeout`, so the store's writer waits about 30-35 s, not the documented 5 s | DA scratch program `/tmp/claude-0/-home-user-Vandox/12e5a606-84aa-5097-9899-979fbf03d825/scratchpad/busy/Program.cs`: `DefaultTimeout=4` error 5 after 4,477 ms, default after 31,097 ms; `SqliteStore.cs` `ConnectionString` (lines 281-289) sets none | confirmed |
| 19 | `DefaultTimeout` bounds only the busy retry, not a statement that runs long without a lock conflict | measured by the Dev (8.9 s) and the Reviewer (9.25 s): a statement without a lock conflict completed with `DefaultTimeout` 1; the `SqliteStore` tests stay green | confirmed |
| 20 | `DriveInfo` on Linux answers `AvailableFreeSpace` for the file system of any directory path (`statvfs` `f_bavail * f_frsize`) | `FreeSpaceReadsTheVolumeOfADirectory` (a `TempDirectory`: positive, at most the total size) | confirmed |
| 21 | DEL and valid UTF-8 characters (`ö`) in a header reach the application; a single invalid byte (`E9`) gets Kestrel's own `400` without `Vandox-Refused` | the same test, step 5: `401` for DEL and `ö`, `400` for `E9` | confirmed |
| 22 | `ApplicationStopping` fires at the stop signal before Kestrel stops accepting; `StopAsync` waits for running requests at most `ShutdownTimeout` (10 s) and does not wait for application code beyond it | Security plan review B3; `ServeCommand.cs` lines 166-170, `HostOptions.ShutdownTimeout`; covered by `ServeShutdown*` tests | confirmed |
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
| AC7 | `IngestEndpointContentLengthOverLimitAnswers413BeforeReading`, `IngestEndpointChunkedBodyOverLimitAnswers413`, `IngestEndpointOversizedBatchAnswers413` (rows: decompressed over 2 MiB, 20,001 records, line over 1 MiB, header line over 1 MiB, 1 KB body to a hostile line) | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC8 | `IngestEndpointUnreadableBodyAnswers400Refused` (rows: not gzip, empty, truncated, bad CRC, trailing bytes, bad JSON, key rule, unreadable header line), `IngestEndpointInvalidBatchAnswers422Refused` (rows: field error, unknown kind, no records, sequence, capture time outside the storable range), `IngestEndpointErrorAfterGoodRecordsStoresNothing`, `IngestEndpointNewerMinorStoresWithUnknownKeysDropped`, `IngestEndpointRefusedBatchLogsFixedReasonOnly` (a refused label key holds a sentinel; the logged `field` is `data.labels[]`), `IngestFieldPathReducesMapKeys` | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC9 | `IngestEndpointUnsupportedMajorAnswers415` (header line), `IngestEndpointContentTypeForms` (rows: `Application/GZIP` accepted; parameter, `application/x-gzip`, empty, missing, two headers refused), `IngestEndpointContentEncodingAnswers415WithoutReading` | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC10 | `IngestEndpointStoreFailureAnswers503AndStoresNothing` (scripted writer failure; and a real store whose writer lock a second connection holds, answered after about 5 s), `IngestEndpointResendAfterStoreFailureIsStored`; `SqliteStoreWriteGivesUpAfterBusyTimeout` (the writer waits at least 5 s and less than 10 s, measured as a ratio to the configured timeout) | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreTests.cs` |
| AC11 | `IngestEndpointAtMostTwoRequestsHoldASlot` (gated writer counts concurrency), `IngestEndpointSlotIsHeldUntilTheCommit`, `IngestEndpointAbortedRequestStillCommits`, `IngestEndpointBodyBuffersBelongToTheSlots`, `IngestEndpointRequestOverBudgetGetsNo2xx` (rows: slot wait, trickling body; `FakeTimeProvider`), `ServeIngestSeventeenthConnectionIsClosed` | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs`, `ServeTests.cs` |
| AC12 | `IngestEndpointHostileLineStaysWithinMemoryBound` (decoder), `IngestEndpointMaximalBatchStaysWithinMemoryBound` (a real `SqliteStore` in a `TempDirectory`: 20,000 distinct log lines filling 2 MiB, and a batch with the largest connection snapshot; the heap sampled with `GC.GetTotalMemory(true)` by a second thread while the real write runs, its maximum kept, plus `sqlite3_memory_highwater(1)` reset before and read after the write) | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC13 | `PortRoutingMiddlewareSendsOnlyPostV1BatchesToIngest` (rows: other path, `/v1/batches/`, `/V1/batches`, `GET`, `/healthz`), existing ingest-`404` cases of `ServeTests` kept, `ServeWebPortPostV1BatchesStoresNothing`, `ServeIngestAnswerCarriesNoSecurityHeaders` | `tests/Vandox.Backend.Tests/PortRoutingMiddlewareTests.cs`, `ServeTests.cs` |
| AC14 | `ServeShutdownDuringIngestWriteCommitsOrStoresNothing` (gated writer released after the stop signal), `ServeShutdownDrainsIngestWritesBeforeClosingStore`, `ServeShutdownRefusesAWriteThatStartsAfterTheDrain` (a request that finished decoding when the drain began gets no `2xx` and the writer sees no call), `ServeShutdownCancelsAWriteAfterTheDeadline` (`FakeTimeProvider`: the write's token is cancelled 10 s after the stop), `ServeFailureAfterStartStillDrains` (a listener failure path), `IngestWriteGateClosesAtomically` | `tests/Vandox.Backend.Tests/ServeTests.cs`, `IngestWriteGateTests.cs` |
| AC15 | `IngestEndpointLogsFirstBatchOnceAndEveryBatchAtDebug`, `IngestEndpointLogsHoldNoTokenOrContent` (a sentinel token, sentinel record text and a sentinel map key, every ingest log line of the other tests' scenarios checked) | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC19 | shared decoder cases `G1`-`G13` run by `DecodeSharedCaseGivesExpectedResult` and `TestDecoder_SharedCases`; `SharedCasesHaveUniqueNamesAndHoldEveryRequiredCase` and `requiredCaseIDs` extended by `G`; `GzipMemberReaderReadsConcatenatedMembers`, `Crc32MatchesReferenceVectors`; `WireContractGoGzipDecodesToGoldenRecords` (rows: `all-kinds.jsonl.gz` and `all-kinds-two-members.jsonl.gz` written by Go, `all-kinds-gzip1.jsonl.gz` written by `gzip -9 -c` with `FNAME`; records equal the plaintext golden file, every input byte consumed); `TestGolden_GzipSamples` (Go regenerates or checks the two Go samples under `VANDOX_UPDATE_GOLDEN=1`) | `testdata/wire/decoder-cases.json`, `tests/Vandox.Core.Tests/WireContractTests.cs`, `internal/wire/decode_test.go`, `internal/wire/golden_test.go`, `tests/Vandox.Core.Tests/GzipMemberReaderTests.cs`, `tests/Vandox.Core.Tests/Crc32Tests.cs` |
| AC20 | `IngestEndpointLowFreeSpaceAnswers503AndWritesNothing` (rows: below floor, unreadable, floor 0 off), `IngestEndpointLogsPauseOnceAndResume`, `IngestEndpointUnexpectedExceptionAnswers503WithTypeOnly`; `BackendConfigLoaderReadsIngestMinFree`, `BackendConfigLoaderRefusesInvalidIngestMinFreeWithoutValue` (the `spool.min_free` rows); `FreeSpaceReadsTheVolumeOfADirectory` | `tests/Vandox.Backend.Tests/IngestEndpointTests.cs`, `tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs`, `tests/Vandox.Backend.Tests/FreeSpaceTests.cs` |

## Verification without tests

Only the deployment criteria; the rest of the change has tests.

- AC16: the Dev's edit of `deploy/backend/docker-compose.yml`, checked by the smoke test of AC17 in CI (`docker compose config` without the variable fails; the binding) and by the Reviewer: `grep -n EXPOSE deploy/backend/Dockerfile` finds nothing.
- AC17: `.github/scripts/smoke-test-backend.sh`, run by the CI job *Release build check* on the PR head; locally `bash -n` and the *Analyzer gate* `--lint-only` (shellcheck) by the Dev (no Docker in the session, `.squad/stack.md`, *Not checkable in a cloud session*).
- AC18: the documentation edits, read by the Reviewer against facts 14-17 (each stated as the plan marks it) and record 0127.

## Approach

**Routing.** `PortRoutingMiddleware` takes the `IngestEndpoint` from DI. On an ingest connection it calls the endpoint only for method `POST` and path exactly `/v1/batches` (ordinal, no trailing slash, query ignored); every other ingest request stays `404`. The web pipeline (forwarded headers, security headers, login, antiforgery) never runs on ingest. `ListenerRoutes.Ingest` adds a `ConnectionCap` of 16 to its listener (one instance per listener; a 17th connection is aborted before Kestrel reads a request).

**Endpoint, in this order** (each answer without a body; `R` = `Vandox-Refused: batch`; `HandleAsync` has a catch-all after the steps: `503`, `ingest request failed` with the exception's type name):

| Step | Condition | Answer, log |
| ---- | --------- | ----------- |
| 1 | receive time = `TimeProvider.GetUtcNow()` | — |
| 2 | no `IngestSettings` (token or `ingest.agent_id` unset) | `503` |
| 3 | `IngestToken.Matches` false | `401`, `WWW-Authenticate: Bearer`; `ingest token refused` (`peer`, `reason` `missing`/`wrong`), rate-limited |
| 4 | any `Content-Encoding`; `Content-Type` not one value equal to `application/gzip` ignoring ASCII case | `415`; `ingest request not accepted` (`status`, `reason` `content encoding`/`content type`) |
| 5 | `Content-Length` > 2 MiB | `413` R; `ingest batch refused` (`reason` `body too large`) |
| 6 | 30-second budget (from step 1, `TimeProvider`) linked with `RequestAborted` and `ApplicationStopping`; wait for one of 2 slots | budget passed or stopping: `503`; `ingest request timed out` (not on stopping) |
| 7 | read the body into the slot's own buffer of 2 MiB (allocated once per slot on first use, never pooled; `MaxRequestBodySize` feature set as backstop) | over: `413` R; budget passed or `BadHttpRequestException` (data rate): `408` where possible, `ingest request timed out`; aborted: nothing |
| 8 | `BatchDecoder.OpenAsync` with `IngestLimits.Wire`; its `WireException` mapped as in step 9; header `agent_id` ≠ `ingest.agent_id` (ordinal) | as step 9; `403`, `ingest agent refused` (`agent_id`, `peer`), rate-limited |
| 9 | `NextAsync` to the end into a list | `WireException`: `Malformed` → `400` R, `LimitExceeded` → `413` R, `UnknownKind`/`Sequence`/`EmptyBatch`/`Invalid` → `422` R, `UnsupportedVersion` → `415`; `ingest batch refused` / `ingest request not accepted` with `reason` from `IngestStatus.Reason(kind)`, `line`, `field` (`IngestFieldPath.Reduce(FieldError.Field)`: every `["…"]` segment becomes `[]`) |
| 10 | free space of `storage.directory` (`FreeSpace.Available`) below `ingest.min_free` (0 = off) or unreadable | `503`; warning `ingest paused: free space low` (`available_bytes`, or none when unreadable) once per episode |
| 11 | `IngestWriteGate.TryEnter` (false once the drain began) | false: `503`, nothing written |
| 12 | `WriteBatchAsync(batch, shutdownToken)`: `shutdownToken` is cancelled 10 s after `ApplicationStopping` (`IngestEndpoint` owns the source, `TimeProvider`), never by the request | `InvalidBatchException` → `422` R (`reason` `refused by the store`); other `StoreException`, `SqliteException`, `IOException`, and `OperationCanceledException` of the shutdown token → `503`, `ingest store failed` (`agent_id`, `exception` type name) |
| 13 | leave the gate, release the slot (finally) | `204`; debug `ingest batch stored`; info `ingest first batch stored` once per process; info `ingest resumed` after a pause |

The refusal header is set only in steps 5, 7, 8, 9 and 12. The rate limit (`IngestRefusalLog`, one per message) lets one line through when a minute has passed since the last one, with `suppressed` the count of refusals in between. `IngestSettings.Create` logs `ingest disabled` with `missing` once at start.

**Shutdown.** `IngestWriteGate` counts writes in flight under one lock: `TryEnter` fails once `Close` was called, so no write can register after the drain began. `IngestEndpoint.DrainAsync` closes the gate and waits for the writes in flight, at most until the shutdown token (stop signal + 10 s) is cancelled and the cancelled writes have rolled back. `ServeAsync` calls it in a `finally` that covers every exit after `app.StartAsync` succeeded (normal stop, the catch paths, an exception of `StopAsync`), so the store is disposed only after it (facts 4, 22). A cancelled write is a rollback the agent resends after the restart.

Rules for the Dev (Lead decisions in step 5): the endpoint reads the floor from `IngestSettings.MinFreeBytes`, which `Create` takes from `config.Ingest`; with no settings there is no floor check (step 2 answers first). Tests reach the writer and the probe only through `ServeHooks.IngestWriter` and `ServeHooks.FreeSpaceProbe`, which `ServeCommand.Build` applies when it registers the endpoint, never by reflection on private fields (the Tester moves the `ServeShutdown*` tests to them). The endpoint registers its `ApplicationStopping` callback (close the gate, start the 10-second deadline on the `TimeProvider`) in its constructor, and `Build` resolves the endpoint right after `app.Build()`, so the callback exists before the service starts; its order among other callbacks does not matter, because no other callback writes. `IngestWriteGate.CloseAsync(token)` closes the gate synchronously first, then completes normally as soon as no write is in flight (at once when none is, whatever the token) and throws `OperationCanceledException` when the token is cancelled while writes remain; `DrainAsync` catches it and then waits with `CloseAsync(CancellationToken.None)` for the cancelled writes to roll back, so the store is never disposed under a running write.

**Store.** `SqliteStore.ConnectionString` sets `DefaultTimeout = 5`, so a write waits at most the documented busy timeout of 5 s for another process's lock (fact 18), not about 30 s; the Storage area keeps "5 seconds" and now says it bounds the whole wait.

**Listener.** `ListenerRoutes.Ingest` sets `HttpProtocols.Http1` (one request at a time per connection, so 16 connections bound 16 requests).

**Decoder.** `BatchDecoder.OpenAsync` reads through `GzipMemberReader` instead of `GZipStream` (record 0128): the header rules of fact 9, a raw `DeflateStream` per member fed one byte per read from the reader's own buffer, CRC-32 (`Crc32`, table in code) and ISIZE of the output against the trailer; after a trailer, end of input ends the stream, else the next bytes must be a header. Every framing error is `WireException` `Malformed`, raised at the read where Go raises it (records before it are returned). `StrictGzip.Require()` stays in `OpenAsync`.

**Shared cases.** Each case of `testdata/wire/decoder-cases.json` may carry `"gzip": {"members": n, "trailing_hex": "…", "name_bytes": n, "comment_bytes": n, "extra_bytes": n, "header_crc": "right" | "wrong"}`; both loaders build the stream from it (Go with `DisallowUnknownFields`, so the struct gains the field). Cases: `G1` 20 garbage bytes, `G2` one zero byte, `G3` 16 zero bytes, `G4` `1f 8b` alone, `G6` two members then garbage → records then `Malformed`; `G5` two members → ok; `G7` name 511 ok, `G8` name 512 `Malformed`, `G9`/`G10` the same for the comment, `G11` extra field of 100 bytes ok, `G12` right and `G13` wrong header CRC.

**Gzip samples.** `internal/wire/golden_test.go` writes or checks `testdata/wire/all-kinds.jsonl.gz` (the bytes `EncodeBatch` writes for the golden records) and `all-kinds-two-members.jsonl.gz` (the golden lines split into two members by Go's `gzip.Writer`) under `VANDOX_UPDATE_GOLDEN=1`; the Tester commits `all-kinds-gzip1.jsonl.gz` made once with `gzip -9 -c all-kinds.jsonl` (`FNAME` and `MTIME` set by gzip(1)), which only the C# test reads.

**Compose and smoke test.** Ports entry `"${INGEST_BIND_ADDRESS:?set INGEST_BIND_ADDRESS to this host's tailnet address (tailscale ip -4), or 127.0.0.1 without agents}:8081:8081"`. The smoke test keeps the token in a variable before `chown`, replaces the commented line `# agent_id: web-1` under the existing `ingest:` block of its copy of `vandoxd.yaml` by `  agent_id: agent-1` (and fails when the file did not change, so no second `ingest:` key is ever written), exports `INGEST_BIND_ADDRESS=127.0.0.1`, replaces the "no ingest binding" check by "8081 bound on exactly `127.0.0.1`", posts the gzip of `all-kinds.jsonl` twice (`204`, `204`), a wrong token (`401`), `GET /healthz` on 8081 (`404`), and runs `docker compose config` once with the variable unset (fails, message names it).

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
| Vandox.Core | `Configuration/IngestConfig.cs` (new), `BackendConfig.cs`, `BackendConfigLoader.cs` | `ingest.agent_id` and `ingest.min_free` options, rules, key list |
| Vandox.Storage | `SqliteStore.cs` `ConnectionString` | `DefaultTimeout = 5` |
| Vandox.Core | `Wire/GzipMemberReader.cs`, `Wire/Crc32.cs` (new), `Wire/BatchDecoder.cs` | own gzip framing |
| Vandox.Backend | `Ingest/IngestEndpoint.cs`, `IngestSettings.cs`, `IngestToken.cs`, `IngestLimits.cs`, `IngestStatus.cs`, `IngestRefusalLog.cs`, `ConnectionCap.cs`, `IngestWriteGate.cs`, `FreeSpace.cs`, `IngestFieldPath.cs` (new) | the endpoint |
| Vandox.Backend | `Hosting/PortRoutingMiddleware.cs`, `ListenerRoutes.cs`, `ServeCommand.cs`, `BackendLog.cs` | routing, cap, DI, drain, log methods |
| deploy | `backend/docker-compose.yml`, `backend/vandoxd.yaml` | ports entry and comments; commented `agent_id` |
| CI | `.github/scripts/smoke-test-backend.sh` | ingest checks |
| testdata | `wire/decoder-cases.json`, `wire/all-kinds.jsonl.gz`, `wire/all-kinds-two-members.jsonl.gz`, `wire/all-kinds-gzip1.jsonl.gz` | cases `G1`-`G13`; gzip samples |

## Signatures (for the Dev's skeleton)

- `public sealed class IngestConfig` with `[ConfigKey("listen")] public string Listen { get; set; }` `[ConfigKey("agent_id")] public string? AgentId { get; set; }` and `[ConfigKey("min_free")] public string MinFree { get; set; }` (default 1GiB) with `public long GetMinFreeBytes()`; `BackendConfig.Ingest` becomes `public IngestConfig Ingest { get; }`
- `internal sealed class GzipMemberReader : Stream` with `internal GzipMemberReader(Stream input)` and the `ReadAsync(Memory<byte>, CancellationToken)` and `Read(Span<byte>)` overrides
- `internal static class Crc32` with `internal static uint Update(uint crc, ReadOnlySpan<byte> data)`
- `internal sealed class IngestEndpoint : IDisposable` with `internal IngestEndpoint(IngestSettings? settings, IRecordWriter writer, FreeSpace freeSpace, IHostApplicationLifetime lifetime, TimeProvider clock, ILoggerFactory loggerFactory)`, `internal Task HandleAsync(HttpContext context)`, `internal Task DrainAsync()`
- `internal sealed class IngestWriteGate` with `internal bool TryEnter()`, `internal void Exit()`, `internal Task CloseAsync(CancellationToken cancellationToken)`
- `internal sealed class FreeSpace` with `internal FreeSpace(string directory, Func<string, long?> probe)`, `internal long? Available()` and `internal static long? Probe(string directory)` (through DriveInfo, no value on any failure)
- `internal static class IngestFieldPath` with `internal static string Reduce(string field)`
- `internal sealed class IngestSettings` with `internal static IngestSettings? Create(BackendConfig config, ILogger logger)`, `internal string AgentId { get; }`, `internal IngestToken Token { get; }`, `internal long MinFreeBytes { get; }` (from `config.Ingest.GetMinFreeBytes()`)
- `ServeHooks`: `internal Func<IRecordWriter, IRecordWriter>? IngestWriter { get; set; }` (wraps the store the endpoint writes to) and `internal Func<string, long?>? FreeSpaceProbe { get; set; }` (replaces `FreeSpace.Probe`)
- `internal sealed class IngestToken` with `internal IngestToken(Secret token)`, `internal bool Matches(StringValues authorization)`, `internal static byte[] Digest(ReadOnlySpan<char> token)`
- `internal static class IngestLimits` with `MaxBodyBytes`, `MaxBatchBytes` (both `2 << 20`), `MaxLineBytes` (`1 << 20`), `MaxRecords` (20,000), `Slots` (2), `MaxConnections` (16), `RequestBudget` (30 s) and `internal static WireLimits Wire()`
- `internal static class IngestStatus` with `internal static int ForWireError(WireErrorKind kind)`, `internal static string Reason(WireErrorKind kind)`, `internal static bool IsRefusal(int status)`
- `internal sealed class IngestRefusalLog` with `internal IngestRefusalLog(TimeProvider clock)`, `internal bool TryTake(out int suppressed)`
- `internal sealed class ConnectionCap` with `internal ConnectionCap(int max)`, `internal ConnectionDelegate Wrap(ConnectionDelegate next)`
- `PortRoutingMiddleware(RequestDelegate next, IngestEndpoint ingest)` replaces `PortRoutingMiddleware(RequestDelegate next)`
- `BackendLog`: `IngestDisabled(this ILogger logger, string missing)`, `IngestTokenRefused(this ILogger logger, string peer, string reason, int suppressed)`, `IngestAgentRefused(this ILogger logger, string agentId, string peer, int suppressed)`, `IngestBatchRefused(this ILogger logger, string? agentId, int status, string reason, long? line, string? field)`, `IngestRequestNotAccepted(this ILogger logger, int status, string reason)`, `IngestRequestTimedOut(this ILogger logger, string peer)`, `IngestStoreFailed(this ILogger logger, string agentId, string exception)`, `IngestRequestFailed(this ILogger logger, string exception)`, `IngestPaused(this ILogger logger, long? availableBytes)`, `IngestResumed(this ILogger logger)`, `IngestFirstBatchStored(this ILogger logger, string agentId)`, `IngestBatchStored(this ILogger logger, string agentId, string mode, int records, int stored, int duplicates)`

## Test files

`tests/Vandox.Backend.Tests/IngestEndpointTests.cs`, `IngestTokenTests.cs`, `IngestSettingsTests.cs`, `IngestRefusalLogTests.cs`, `IngestWriteGateTests.cs`, `FreeSpaceTests.cs`, `IngestFieldPathTests.cs`, `ServeTests.cs`, `PortRoutingMiddlewareTests.cs`; `tests/Vandox.Storage.Tests/SqliteStoreTests.cs`; `tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs`, `WireContractTests.cs`, `GzipMemberReaderTests.cs`, `Crc32Tests.cs`; `internal/wire/decode_test.go`, `internal/wire/golden_test.go`; fixtures `testdata/wire/decoder-cases.json` and the three gzip samples. New doubles (Tester): `RecordingRecordWriter` (an `IRecordWriter` that keeps every batch and token, counts writes in flight, can hold a write until released and fail with a scripted exception) and `CountingBodyStream` (a request body that counts reads), each in its own file of `tests/Vandox.Backend.Tests`.

Existing call sites: `PortRoutingMiddlewareTests` lines 31 and 64 (`new PortRoutingMiddleware(...)`) — Dev adapts in step 4 (old signature removed); `BackendConfigLoaderTests` key-list assertion (line 672) and the example-file test — Tester in step 5; `ServeTests` line 194 (`ListenerRoutes.Ingest` method group, unchanged signature) — none.

Files no unit test can cover: none (the compose file and smoke script are under *Verification without tests*).

## Areas

- `docs/areas/backend-host.md`: *Ingest endpoint* implemented (the step table above as behavior, limits, timeouts, slots, log lines); *Listeners and routing*; *Compose file* (ports entry, variable, smoke checks); *Exposure and trust* (TUN mode); *Implementation* (`Ingest/`).
- `docs/areas/wire-format.md`: *Duties of the ingest API* rewritten with the measured C# figures (fact 13); *Accepted forms* rows for trailing bytes and header rules naming the shared cases; *Implementation* (`GzipMemberReader`); *Not here*.
- `docs/areas/configuration-and-secrets.md`: `ingest.agent_id` and `ingest.min_free` rows and rules; backend uses `VANDOX_AGENT_TOKEN`.
- `docs/areas/storage.md`: ingest path implemented (lines 17-19); the busy timeout of 5 s bounds the whole wait for the lock (`DefaultTimeout`, lines 20-23).
- `docs/areas/network-and-security-model.md`: *Agent to backend* implemented, TUN prerequisite.

## Documentation updates

Sweep of enumerations: `grep -n -i -E 'ingest|8081|not published|planned \(#40\)|#40' README.md SECURITY.md docs/*.md docs/areas/*.md .squad/project.md deploy/backend/*` — hits: README lines 16, 89, 151, 253; SECURITY.md line 46; `docs/ARCHITECTURE.md` lines 14, 15, 268; `docs/REVERSE_PROXY.md` line 8 (still true, unchanged); backend-host lines 6-8, 67-69, 77, 159-160, 171-173, 185-189; wire-format line 475, 531; configuration-and-secrets lines 283-284; network line 11-14; storage 18; `.squad/project.md` areas 1 and 2 and the test-double table; compose comments; `vandoxd.yaml` header.

| File | Owner |
| ---- | ----- |
| `docs/areas/*.md` (all five above), `docs/ARCHITECTURE.md` (status lines 14-15, 268; the ingest component) | Lead (mode `docs`) |
| `README.md` (status line 16, options table `ingest.agent_id` and `ingest.min_free`, line 151, lines 253-254: binding, TUN, `INGEST_BIND_ADDRESS` in `.env`), `docs/INSTALL.md` (Synology TUN steps, the variable, symptoms), `SECURITY.md` line 46 (LAN hosts may reach the ingest port on the tailnet address; the token is the boundary; the disk floor) | Lead (mode `docs`) |
| `deploy/backend/docker-compose.yml` comments, `deploy/backend/vandoxd.yaml` (`agent_id` commented with an example, `min_free: 1GiB`, header lines 5-6 name `ingest.agent_id` beside `import.time_zone` as without default, lines 12-13) | Dev |
| `.squad/project.md` security areas 1 and 2 (implemented, the types, the binding, the disk floor `ingest.min_free` as how the goal's disk bound is met) | Dev |
| `.squad/project.md` *Test doubles* row for `RecordingRecordWriter`, `CountingBodyStream` | Tester |

## Architecture check

- *No data gaps unless explicitly recorded*: `204` only after the commit (step 11 after step 10); a refused record becomes the agent's recorded gap (record 0125); a version mismatch, wrong token, full disk or timeout is never a refusal, so nothing is given up.
- *Backfilled data never alerts*: untouched; the header `mode` is logged only (#41).
- Two labelled listeners, fail closed (record 0081): kept; ingest gets one more branch, the web pipeline never runs on it.
- Ingest via the tailnet address (record 0017): the binding is published there; record 0127 amends 0060.

## Security considerations

- [ ] Disk: writes stop below `ingest.min_free` (default 1 GiB); the overshoot is at most two batches being written.
- [ ] Limits: compressed body 2 MiB before decoding (on the raw request bytes); decompressed batch 2 MiB and line 1 MiB after gzip, before JSON (decoder); records 20,000 after decoding; the `Authorization` value is bounded by Kestrel's 16 KiB header limit before the token check.
- [ ] User-visible failure texts: none; every answer has no body, log lines carry fixed reasons, `FieldError.Field`/`Reason`, a line number and exception type names only; `WireException.Message` (which can wrap parser text) and `StoreException.Message` are never logged by the endpoint; map keys of a field path are reduced to `[]` before logging.
- [ ] Guard of the token, the forms the parser accepts (Kestrel header parsing, fact 11 and 12): one value only; scheme `Bearer` in any ASCII case; one space; the rest hashed as is (non-ASCII and controls cannot match the printable-ASCII token); OWS trimmed by Kestrel; query string, cookies and other headers never read.
- [ ] Content type guard: one value, exactly `application/gzip` ignoring ASCII case; `Content-Encoding` present in any form refused, so no server decompression can run (record 0124).

## Decision records

- `docs/decisions/0126-ingest-endpoint-token-bound-to-one-agent-id-two-slots-through-the-commit-no-rate-cap.md` (Proposed)
- `docs/decisions/0127-backend-host-needs-tailscale-in-tun-mode-ingest-port-on-a-required-bind-address.md` (Proposed, amends 0060)
- 0126 extended: the disk floor (option chosen over the accepted residual), the uncancellable-by-the-request but deadline-bounded write, the closed gate and the drain.
- `docs/decisions/0128-csharp-decoder-reads-the-gzip-framing-itself-to-match-the-go-decoder.md` (Proposed, amends 0076)

## Challenge

- M1 (major, uncancellable write and unbounded drain; the lock wait is about 30 s): accepted. The write runs under a shutdown token cancelled 10 s after `ApplicationStopping`; `DrainAsync` waits at most until then; the step-6 budget is linked with `ApplicationStopping` and the gate refuses every write after the drain began, so no write starts after the stop signal (E16, AC14). `DefaultTimeout = 5` on the store's connections makes the documented busy timeout true (fact 18; fact 19 to verify); the AC10 real-store row waits about 5 s.
- M2 (major, no real gzip sample): accepted. Two samples written by Go (`all-kinds.jsonl.gz` from `EncodeBatch`, a two-member variant) and one by `gzip -9 -c` with `FNAME`; `WireContractGoGzipDecodesToGoldenRecords` compares the records with the plaintext golden file and checks that every input byte was consumed (*Gzip samples*).
- m1 (duplicate `ingest:` key in the smoke config): accepted. The script replaces the commented `agent_id` line inside the existing block and fails if nothing changed; the example's header names `ingest.agent_id` as without default.
- m2 (a map key in the logged field): accepted. `IngestFieldPath.Reduce` turns every bracketed key into `[]`; the sentinel test holds a map key (spec E7, AC8, AC15).
- m3 (header-line errors unmapped): accepted. Step 8 maps `OpenAsync`'s `WireException` as step 9; header-line rows added to AC7, AC8, AC9.
- m4 (drain on every exit): accepted, with B3 (*Shutdown*).

## Security review

- B1 (disk exhaustion by a token holder): option (a) taken, no escalation: the free-space floor `ingest.min_free` (default `1GiB`, the agent's `spool.min_free` rule) is checked before every write and answered `503`, which the agent treats as not accepting and keeps its records (step 10, spec E20, AC20, record 0126). Option (b) would leave area 1's goal untrue for the NAS volume the data often shares; the floor costs one `statvfs` per batch. `.squad/project.md` area 1 states the floor as the disk bound.
- B2 (memory of the real write): accepted. AC12 measures through a real `SqliteStore` with 20,000 distinct log lines filling 2 MiB and the largest connection snapshot, heap sampled during the write and SQLite's native high-water mark read; the figures go into the Wire format area. Body buffers are owned by the two slots, never `ArrayPool<byte>.Shared`.
- B3 (drain race): accepted (*Shutdown*): `IngestWriteGate` closes atomically, a later `TryEnter` gets `503` without starting, the drain runs in a `finally` over every exit after `StartAsync`, and `ServeShutdownRefusesAWriteThatStartsAfterTheDrain` and `ServeFailureAfterStartStillDrains` cover it.
- Non-blocking 1 (DEL and non-ASCII reach the endpoint): spec E1 corrected; fact 21; the fact-11 case expects `401` for DEL.
- Non-blocking 2: `HttpProtocols.Http1` on the ingest listener (*Listener*).
- Non-blocking 3: AC18 and `SECURITY.md` state that LAN hosts may reach the ingest port on the tailnet address.
- Non-blocking 4: catch-all `503` with the type name only (spec E21, AC20).
- Non-blocking 5: map keys in field paths reduced (m2).

## Out of scope / follow-ups

- Spec *Out of scope*. Related defect found: none beyond fact 4, which this plan fixes (`DrainAsync`). The import's `GZipStream` keeps accepting trailing bytes after an archive (record 0128, *Consequences*), deliberately.

## Security notes

Squad-security, round 2 (APPROVED), non-blocking, appended verbatim:

1. **When the gate closes.** Spec E16, AC14 and record 0126 say "from the stop signal on no write starts", and 0126 says the gate is closed at the stop signal. The plan's *Shutdown* section closes it only in `DrainAsync`, which runs after `app.StopAsync`, up to 10 s later. In that window, a request that had already finished decoding can still pass `TryEnter`; the budget linked to `ApplicationStopping` stops only requests that are still waiting, reading or decoding. This is safe: the drain still waits for such a write, and the deadline bounds it. The fix is to call `Close` from an `ApplicationStopping` registration, so the plan matches the spec, and to let `ServeShutdownRefusesAWriteThatStartsAfterTheDrain` probe at the stop signal itself.
2. **`IngestFieldPath.Reduce` must parse every form `FieldError` produces.** "Every `["…"]` segment becomes `[]`" is not enough. `Check.Keyed` builds `field[QuoteName(key)]`, and `Quote` escapes `"` as `\"` and `\` as `\\`. A cut key is followed by `...` after its closing quote, giving `["abc"...]`. A reducer that looks for the next `"]` lets key text through in four cases: a key containing `\"` or `"]`; a key ending in a backslash (`\\"]`); a key containing `]` or `[`; a cut key, where `...` stands between the closing quote and `]`. Fix: walk the quoted string honouring the escapes, allow the optional `...`, and fail closed by logging no `field` when the path does not parse. Add these rows to `IngestFieldPathReducesMapKeys` and the AC15 sentinel test.
