# Plan: Move the backend to .NET 11

Source: [spec.md](spec.md)
Tier: security — the change rewrites the live connection's session check (`SessionCircuitHandler`, a session control of
security area 3) and moves the runtime base image of the release.
Security areas touched: 2 and 13 (`deploy/backend/Dockerfile`: base images, tags and digests), 3 (`SessionCircuitHandler`;
`HEAD /login`; the framework facts of records 0099, 0103 and 0105), 9 and 10 (`Microsoft.Data.Sqlite` 11 with SQLitePCLRaw 3
under the storage and the log search; `FtsQuery`), 12 (the Content-Security-Policy against the 11 framework script), build
configuration (`global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`), dependencies
(`Microsoft.Data.Sqlite`, `Microsoft.Extensions.TimeProvider.Testing`, `SonarAnalyzer.CSharp`).

## Problem / root cause

See [spec.md](spec.md), *Problem*. The Lead's trial build of `main` (scratch worktree: `net11.0`, SDK 11.0.100, both packages
at 11.0.0, then `SonarAnalyzer.CSharp` 10.36.0.15337) gives the complete list of diagnostics this plan fixes (facts 1–4).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | On 11.0.100 the build reports: IDE0028 ×11 (Vandox.Core); CA1830 in `PathText.cs:68`, `FtsQuery.cs:162`, `ImportRepository.cs:309` (`Append(rune.ToString())`); CA2016 + S8949 in `HashPasswordCommand.cs:47,52`, `HealthCheckCommand.cs:55,61`, `TemplatesCommand.cs:59`, `BackendApp.cs:32,39,40,47`, S8949 alone in `IncidentsCommand.cs:55`; BL0013 in `SessionCircuitHandler.cs:42`; BL0016 in `ChartInterop.cs:81,86,90` | trial build, `--no-incremental -p:TreatWarningsAsErrors=false` with IDE0028 muted to see past Vandox.Core | confirmed |
| 2 | In tests: CA2026 in `JsonLineLoggerTests.cs:34,64,97`, `BackupRunTests.cs:149,150`, `LogLineTests.cs:194,217`, `PayloadRegistryTests.cs:34,98`, `PayloadValidationTests.cs:40,251`; MSTEST0049 + S8949 in `LockedTextWriterTests.cs:77,79`; BL0016 in `RecordingJsRuntime.cs:89,95,99`, `RecordingJsObjectReference.cs:45,51,57,62,81,94`, `ChartInteropTests.cs:217,244` (calls on the doubles themselves) | same trial build | confirmed |
| 3 | `SonarAnalyzer.CSharp` 10.36.0.15337 (newest on nuget.org) adds S9422 in `JsonLineLoggerProvider.cs:17` and `ActionRun.cs:10` (`object` lock fields; `.editorconfig:481` already sets S9422 to `error` in the sonar block); neither field is used with `Monitor` | trial build with 10.36; `grep` of both files (only `lock (…)`) | confirmed |
| 4 | All six new rules (S8949, CA2016, CA1830, CA2026, BL0013, BL0016) report at warning level, so `TreatWarningsAsErrors` fails the build on an offending line; no `.editorconfig` line is needed but S8949's | trial build (facts 1–2) | confirmed |
| 5 | Catching `JSDisconnectedException` around the three calls of `RenderAsync` clears BL0016; subscribing to `AuthenticationStateChanged` in `SessionCircuitHandler` clears BL0013 | scratch edit of both files, Backend build: no BL diagnostic | confirmed |
| 6 | .NET 11 adds `TextWriter.WriteAsync(string?, CancellationToken)` and `WriteLineAsync(string?, CancellationToken)`; they are not virtual and forward to `Write(Line)Async(ReadOnlyMemory<char>, CancellationToken)`; the base implementation of those returns a cancelled task for a cancelled token and otherwise writes through `Write(ReadOnlySpan<char>)`/`WriteLine(ReadOnlySpan<char>)`, whose base `WriteLine(char[],int,int)` makes two writes (text, then line end) | dotnet/runtime v11.0.0 `TextWriter.cs:630,724,672–680,767–775,408–427` | confirmed |
| 7 | `StreamWriter` (the `stdout` of `Program.cs:31`), `StringWriter` and `Console.Error`'s synchronized writer override the `ReadOnlyMemory` overloads, so with `CancellationToken.None` they write as the string overloads did; `LockedTextWriter` (test double) overrides neither, so a line would become two lock acquisitions | runtime v11.0.0 `StreamWriter.cs:776,916`, `StringWriter.cs:214,290`, `TextWriter.cs:1108,1120`; `tests/Vandox.Backend.Tests/LockedTextWriter.cs` | confirmed |
| 8 | `HealthCheckCommand.cs:61` writes inside `catch (… OperationCanceledException …)`: given the command's cancelled token the write would throw out of the `catch` | `HealthCheckCommand.cs:59–63` | confirmed |
| 9 | `ChartInterop.RenderAsync` catches nothing; `TimeSeriesChart.OnAfterRenderAsync` sets `_rendered` after it (`TimeSeriesChart.razor.cs:61,63`); only `DisposeAsync` catches `JSDisconnectedException` (`ChartInterop.cs:134`) | code | confirmed |
| 10 | The circuit's handlers are resolved from the circuit scope and the scope is disposed when the circuit closes, so a scoped `IDisposable` handler is disposed then | aspnetcore v11.0.0 `CircuitHost.cs:826,240`; registration `WebAuthentication.cs:87` (scoped) | confirmed |
| 11 | 0097: Microsoft.Data.Sqlite 11.0.0 calls neither `sqlite3_interrupt` nor `sqlite3_progress_handler` | no such member reference in the metadata of `microsoft.data.sqlite.core/11.0.0/lib/net10.0/Microsoft.Data.Sqlite.dll` (same for 10.0.14; positive control `sqlite3_step` found) | confirmed |
| 12 | Microsoft.Data.Sqlite 11.0.0 depends on SQLitePCLRaw 3.0.5 (10.0.14: 2.1.13); `raw.sqlite3_progress_handler` used by `ReaderQuery.cs:33,120,132` and `LineIndex.cs:168` still compiles | nuspec of both versions; trial build of Vandox.Storage | confirmed |
| 13 | 0098: the 11 framework script still uses an existing `components-reconnect-modal` element; `MapStaticAssets` with `UseStaticWebAssets` serves it from the build output | to verify: the build-output part by `ServeTests.ServeServesWebAssetsOnlyOnWebPort` on the 11 run (Dev, step 4); the script part is read in `blazor.web.js` of `microsoft.aspnetcore.app.internal.assets/11.0.0` | unverified |
| 14 | 0099: `blazor.web.js` 11.0.0 has no `eval(` or `new Function(`, creates one `<style>` element (the default reconnect dialog, as in 10.0.15) and names no foreign origin but documentation links; `ContentSecurityFrameAncestorsPolicy = null` (`ServeCommand.cs:286`) still suppresses the framework's `frame-ancestors` header | `grep` of both script versions; aspnetcore v11.0.0 `ServerRazorComponentsEndpointConventionBuilderExtensions.cs:45–59`; the browser's verdict is AC4 | confirmed |
| 15 | 0103: an exception from a circuit handler's inbound-activity handler is reported as unhandled and the registry terminates the circuit; the render acknowledgement is not awaited | aspnetcore v11.0.0 `CircuitHost.cs:389,416–423,655–680`, `CircuitRegistry.cs:407–414` | confirmed |
| 16 | 0103: the cookie handler uses `ITicketStore` when set; component endpoints require antiforgery; the authorization middleware combines policies from empty metadata for a request without endpoint | aspnetcore v11.0.0 `CookieAuthenticationHandler.cs:165–188`, `RazorComponentEndpointFactory.cs:39`, `AuthorizationMiddleware.cs:118–120` | confirmed |
| 17 | 0103: Razor component endpoints accept only GET and POST | aspnetcore v10.0.0 vs. v11.0.0 `RazorComponentEndpointFactory.cs:18`: 11 adds HEAD (record 0198) | refuted |
| 17a | On 11, `HEAD /login` without a session reaches the anonymous login page (`200`, no body) instead of the fallback `302` of `web-login.md:49` | to verify: `LoginHeadWithoutSessionAnswersLikeGet`, Tester, step 5; read from `Login.razor:1–2` (`@page`, `[AllowAnonymous]`) with fact 17 | unverified |
| 18 | 0105: defaults `KnownProxies` `::1`, `KnownIPNetworks` `127.0.0.0/8`; with both empty no peer is checked; IPv4-mapped peers compared as IPv4; `ForwardLimit` 1; `KnownNetworks` obsolete (ASPDEPR005) | aspnetcore v11.0.0 `ForwardedHeadersOptions.cs:83,88,94`, `DualIPNetworkList.cs:27`, `ForwardedHeadersMiddleware.cs:216,224,388–393` | confirmed |
| 19 | `aspnet:11.0-noble-chiseled` does not exist; `sdk:11.0` and `aspnet:11.0-resolute-chiseled` do | MCR registry API, `HEAD /v2/dotnet/…/manifests/<tag>` (2026-11-11: 404 for noble; index digests `sha256:65f3c7c6…6ef8bea` and `sha256:fc0ee12f…e9cc1654ac`, which move: the Dev reads them again in step 4) | confirmed |
| 20 | `check-builder-dotnet-version.sh` and `dotnet_major.py` accept `11.0-resolute-chiseled` (both read the leading `<major>.<minor>`) | `.github/scripts/check-builder-dotnet-version.sh:19–20`, `.squad/tools/dotnet_major.py:84–87` | confirmed |
| 21 | The image starts on Linux 4.4 (DS918+) | to verify: AC4, Product Manager, before step 9 (no session or CI runner has Linux 4.4) | unverified |
| 22 | CI does not publish the image it builds (`docker save` only feeds the SBOM step, `ci.yml:262`) | `.github/workflows/ci.yml` | confirmed |
| 23 | `aspnet:11.0-resolute-chiseled` amd64 (index `sha256:fc0ee12f…`) has no shell, apt or dpkg; `usr/bin` holds only `dotnet`; no entry is owned by 65532; `etc/passwd` is the same as in the noble image. The CI smoke test does not check for a shell (`ci.yml:245–249` checks `Config.User`; `smoke-test-backend.sh` checks user, `/data` ownership, health, ports and ingest) | registry layer listing of the amd64 manifest `sha256:6207c495…a58cce4` (5 layers, 467 entries), Security's plan review; `ci.yml`, `.github/scripts/smoke-test-backend.sh:194` | confirmed |

Related defect found on the way (not this change): `web-login.md:90–91` says calls that update root components do not pass the
session check; aspnetcore v10.0.0 and v11.0.0 `CircuitHost.PerformRootComponentOperations` run them through
`HandleInboundActivityAsync` (follow-up issue in the result).

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | the whole existing suite, unchanged in number (`CI: Backend (.NET)` job; check: `git diff origin/main...HEAD -- tests/` removes no `[TestMethod]`, Reviewer) | all of `tests/` |
| AC2 | check: *Analyzer gate* on the whole solution (Code Officer, step 7) and the *Sonar gate* on the draft PR; check: `.editorconfig` has `dotnet_diagnostic.S8949.severity = error` in the hand-maintained `[*.cs]` part before the sonar block, `dotnet_diagnostic.BL0016.severity = none` in `[tests/**.cs]`, and S8969 only in the block (Reviewer) | — |
| AC3 | CI: *Release build check* ("Check base image pinning", "Check builder .NET version", "Build and verify image", "Smoke test backend container"), and "Analyze (csharp)" (CodeQL, `codeql.yml`) and "SonarQube" (`ci.yml`), which build with SDK 11, all green on the early draft PR (orchestrator or Dev confirms); no shell or package manager in the runtime image: fact 23; before it the *Image build check* (restore-order reproduction, `.squad/stack.md`) by the Dev | — |
| AC4 | manual: the Product Manager, on the image built from the draft PR's head after CI is green (*Approach*, *Manual check on the reference host*) | — |
| AC5 | `SessionCircuitHandlerChecksEventAgainstChangedState`, `SessionCircuitHandlerChecksFirstEventAgainstInitialState`, `SessionCircuitHandlerWithoutKnownStateProcessesNoEvent`, `SessionCircuitHandlerStopsFollowingStateWhenDisposed`; existing `SessionCircuitHandlerPassesActivityOfActiveSession`, `SessionCircuitHandlerEndsCircuitAfterSessionEnded` unchanged | `tests/Vandox.Backend.Tests/SessionCircuitHandlerTests.cs` |
| AC6 | `ChartInteropRenderToleratesDisconnectedCircuit`, `ChartInteropRenderLetsOtherExceptionsThrough`, `ChartInteropRenderReportsDrawn` | `tests/Vandox.Backend.Tests/ChartInteropTests.cs` |
| AC6 | `TimeSeriesChartDrawsAgainAfterDisconnectedRender` | `tests/Vandox.Backend.Tests/TimeSeriesChartTests.cs` |
| AC7 | `HashPasswordCommandPrintsResultWhenCancelledAfterRead` | `tests/Vandox.Backend.Tests/HashPasswordCommandTests.cs` |
| AC7 | `HealthCheckCancelledPrintsFailureAndExitsOne` | `tests/Vandox.Backend.Tests/HealthCheckTests.cs` |
| AC7 | `BackendAppWithCancelledTokenPrintsSameOutput` | `tests/Vandox.Backend.Tests/BackendAppTests.cs` |
| AC8 | check: `grep -rn -E "NET 10\b\|net10\.0\|10\.0-noble\|sdk:10\.0\|SDK 10\.0\.403" --exclude-dir=specs .` finds only measurements, history and released records (Reviewer) | — |
| AC9 | check: *Verified facts* 11–18 (Lead, done); `LoginHeadWithoutSessionAnswersLikeGet`; existing `ServeTests.ServeServesWebAssetsOnlyOnWebPort`, `ServeTests` anonymous `HEAD /` → `302`, `LoginTests` single-policy test, `SessionCircuitHandlerEndsCircuitAfterSessionEnded` on the 11 run | `tests/Vandox.Backend.Tests/LoginTests.cs` |

Cases:
- AC5 changed state: provider starts with an active session's user; after `ChangeUser` to a user whose session ended (or
  anonymous) the next event throws `SessionEndedException` and `next` is not called; the reverse change (anonymous → active)
  lets the next event pass. Initial state: no change announced, the first event is checked against the provider's state.
  No state: a provider whose `GetAuthenticationStateAsync` throws `InvalidOperationException` (the framework's provider before
  a state is set) — the exception propagates, `next` is not called. Disposed: after `Dispose`, `ChangeUser` to an ended
  session no longer changes what the handler checks (the next event still passes for the earlier active user).
- AC6 rows: `runtime`/`import`, `module`/`create`, `chart`/`update` (the last after a first successful render). Disconnected:
  `RenderAsync` returns `false` without exception; other (`InvalidOperationException`): it propagates. Drawn: a create and an
  update return `true`. Component: the first render fails with `JSDisconnectedException`, the renderer records no exception, and
  a later render with the same `ChartData` instance calls `create` again.
- AC7: `HashPassword`: a reader double cancels the token's source when it returns the line end; an accepted password still
  prints the hash with exit 0, a refused one the refusal with exit 1. Health check: an already cancelled token prints a line
  starting `vandoxd: health check failed: ` and returns 1, without exception. `BackendApp`: rows `-help` (0), `-version` (0),
  an unknown flag (2) with an already cancelled token print exactly what the same call with `CancellationToken.None` prints.
  These tests pass from step 4 on (the skeleton writes with `CancellationToken.None`) and fail when a write gets the command's
  token. `TemplatesCommand.cs:59` and `IncidentsCommand.cs:55` have no hook between the work and the write; the existing
  interruption tests of both commands stay and the Reviewer checks the two lines by reading.
- AC9 `HEAD /login` without a session answers `200` with an empty body (`HEAD /` stays `302`, existing); a second case beside
  it: a signed-in `HEAD /` is answered like its `GET`, without a body (the Tester asserts against the `GET` it runs, not a
  status fixed here).

## Approach

**Platform (Dev, step 4).** `global.json` `11.0.100`; `Directory.Build.props` `net11.0`; `Directory.Packages.props`:
`Microsoft.Data.Sqlite` `11.0.0`, `Microsoft.Extensions.TimeProvider.Testing` `11.0.0`, `SonarAnalyzer.CSharp`
`10.36.0.15337` (or newer if released by then). Dockerfile: `BASE_BUILD_TAG="11.0"`, `BASE_RUNTIME_TAG="11.0-resolute-chiseled"`,
both digests read from MCR at that time (`docker buildx imagetools inspect …` or the registry API, *Base image digests* in
`docs/CONTRIBUTING.md`), header comment `.NET 11`. `.editorconfig`: `dotnet_diagnostic.S8949.severity = error` in the
hand-maintained `[*.cs]` part (before `# squad:sonar-rules begin`), `dotnet_diagnostic.BL0016.severity = none` in
`[tests/**.cs]` after the block (beside S107). No line for S8969.

**Mechanical fixes (no behavior change).** IDE0028: collection expressions. CA1830: `Append(rune)`. S9422: the two lock fields
become `System.Threading.Lock` (`private readonly Lock _gate = new();`). CA2016/S8949 (record 0201): every flagged write passes
`CancellationToken.None`, including the lambda of `IncidentsCommand.cs:55`. Test code (Tester): CA2026 `JsonElement.Parse(…)`;
MSTEST0049 `TestContext.CancellationToken` in `LockedTextWriterTests.cs:77,79`; `LockedTextWriter` overrides
`Write(ReadOnlySpan<char>)` and `WriteLine(ReadOnlySpan<char>)` under its lock, one append each (fact 7), so a line written
through the new overloads stays one write.

**Session check (record 0199).** `SessionCircuitHandler` implements `IDisposable`. The constructor subscribes a private
handler to `AuthenticationStateChanged` that stores the announced `Task<AuthenticationState>` in a private field. Each inbound
event reads that field with `Volatile.Read`; while no change was announced it reads `GetAuthenticationStateAsync()` and
stores it only if the field is still empty, `Interlocked.CompareExchange(ref _state, initial, null)`, then uses the field's
value (an exception from the read propagates and nothing is stored); then `_sessions.Touch(state.User)` as today. `Dispose`
unsubscribes. The event does not come from the circuit's dispatcher: SignalR hub threads raise it (`SetCircuitUser` from
`ComponentHub` on connect and on authentication refresh) and so does the revalidation loop of `SessionRevalidatingStateProvider`
(`ForceSignOut` on a thread-pool thread), and the inbound-activity handler runs before `Renderer.Dispatcher.InvokeAsync`
(aspnetcore v11.0.0 `CircuitHost.cs:410`). The compare-exchange keeps a state announced during the first read from being
overwritten by it; no further lock is needed, since `SessionStore.Touch` looks the session up live.

**Charts (record 0200).** `ChartInterop.RenderAsync` returns `Task<bool>`: `true` after a create or an update, `false` when
the interop was disposed while waiting for the gate or a call threw `JSDisconnectedException` (caught in the existing `try`,
beside `finally`, with a comment as in `DisposeAsync`); any other exception propagates. `_module` and `_chart` stay unset when
their call failed, so the next render retries. `TimeSeriesChart.OnAfterRenderAsync` sets `_rendered = data` only when it
returns `true`.

**Skeleton (step 4).** The platform move and all mechanical fixes above are made in step 4, since nothing builds on 11
otherwise. `RenderAsync` gets its new return type with today's body plus `return true;`, and `SessionCircuitHandler` gets
`IDisposable` with an empty `Dispose`; the two members the analyzers still report (`RenderAsync` for BL0016,
`CheckSessionAsync` for BL0013) are wrapped in `#pragma warning disable BL0016` / `BL0013` with the comment `// Skeleton:
removed with the implementation` (`.squad/stack.md`, *Skeleton*). Every existing test stays green on the skeleton; the AC5 and
AC6 tests fail on it.

**HEAD /login (record 0198).** No code: `web-login.md` *Access* states the framework's answer, pinned by the AC9 test.

**Manual check on the reference host (AC4).** *Early draft PR*: the orchestrator opens the PR as a draft after step 7 and
waits for a green CI. CI publishes no image (fact 22), so the build AC4 runs on is the branch head CI built green, built with
the documented command `docker build -f deploy/backend/Dockerfile -t vandox:pr .` (`.squad/stack.md`, *Image build check*) on
an amd64 Docker host (the DS918+ itself, or another host and `docker save`/`docker load`). The Product Manager then, on the
DS918+: (1) starts it with the compose file of [backend-host](../../docs/areas/backend-host.md) on a copy of the production
data volume (N1: no migration step); (2) checks the container is `healthy` and `vandoxd --version` shows the branch;
(3) checks the ingest endpoint: the agent's batches are accepted (no growing spool, a new data point in the UI);
(4) in a browser: signs in, opens a page with a chart, changes its range or navigates, confirms the page reacts and the
developer console shows no Content-Security-Policy violation, then signs out. The answer, with the head commit, is logged as a
Product Manager row; the PR stays a draft until then (spec, Q1).

User-visible output: none new.

## Options considered

- Runtime base: decided by the Product Manager (spec Q1; record 0197 lists the alternatives).
- BL0013: subscribe (chosen) vs. suppress vs. subscribe without using it — record 0199.
- BL0016: catch only `JSDisconnectedException` in `ChartInterop` with a drawn flag (chosen) vs. catch in the component vs.
  catch everything; in tests `.editorconfig` off vs. pragmas vs. `try`/`catch` — record 0200.
- CA2016/S8949: `CancellationToken.None` (chosen) vs. forwarding the token vs. suppression — record 0201.
- `HEAD /login`: accept (chosen) vs. restricting component endpoints to GET/POST vs. a middleware — record 0198.
- `RenderAsync` reporting the draw: `Task<bool>` (chosen) vs. a public `IsDrawn` property (state that can be read out of step
  with the call that set it).
- AC4 build: the branch head built on the Product Manager's host (chosen) vs. a CI change that uploads the image (a change of a
  release-pipeline workflow, security area 13, outside the spec).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| (root) | `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig` | platform, packages, two rule lines |
| deploy | `deploy/backend/Dockerfile` | tags, digests, comment |
| Vandox.Core | 11 files of fact 1 | IDE0028 |
| Vandox.Storage | `FtsQuery`, `ImportRepository` | CA1830 |
| Vandox.Import | `PathText` | CA1830 |
| Vandox.Backend | `Security/SessionCircuitHandler` | state subscription, `IDisposable` |
| Vandox.Backend | `Components/Charts/ChartInterop`, `TimeSeriesChart.razor.cs` | disconnect handling, drawn flag |
| Vandox.Backend | `Cli/HashPasswordCommand`, `HealthCheckCommand`, `IncidentsCommand`, `TemplatesCommand`, `Hosting/BackendApp` | `CancellationToken.None` |
| Vandox.Backend | `Logging/JsonLineLoggerProvider`, `Logs/ActionRun` | `Lock` fields |

## Signatures (for the Dev's skeleton)

- ChartInterop: `public async Task<bool> RenderAsync(ElementReference host, string title, ChartData data, CancellationToken cancellationToken)` (return type changed; XML `<returns>`: whether the chart was created or updated).
- SessionCircuitHandler (now also implementing IDisposable): `public void Dispose()` in a region `IDisposable`.

The private event handler and state field are described in *Approach*. Test-double members are the Tester's (*Test files*).

## Test files

`tests/Vandox.Backend.Tests/`: `SessionCircuitHandlerTests.cs`, `ChartInteropTests.cs`, `TimeSeriesChartTests.cs`,
`HashPasswordCommandTests.cs`, `HealthCheckTests.cs` (the existing file of `HealthCheckCommand`), `BackendAppTests.cs` (new),
`LoginTests.cs`. Doubles (Tester): `FixedAuthenticationStateProvider.cs` gains `internal void ChangeUser(ClaimsPrincipal user)`
(announces the new state); new `UnsetAuthenticationStateProvider.cs` (throws `InvalidOperationException` as the framework's
provider does before a state is set); new `CancellingTextReader.cs` (cancels a given source when it returns the line end);
`LockedTextWriter.cs` gains the two span overrides.

Existing test code a changed signature affects: the calls of `RenderAsync` in `ChartInteropTests.cs` compile unchanged
(`await` of `Task<bool>`); none to adapt. Test-code diagnostics of fact 2: Tester, step 5.

Files no unit test can cover: none (`Dockerfile` and the build files are verified by AC3).

## Areas

- [web-login](../../docs/areas/web-login.md): *Access* line 49 — `HEAD /login` without a session is answered like
  `GET /login` without a body; and, verbatim (Security note 3), "With a session, a `HEAD` request to a page is answered like
  its `GET`, without a body (from .NET 11 on)." (Dev; the Lead writes no area document); *Related decisions*: 0198, 0199.
- [web-ui](../../docs/areas/web-ui.md): *Charts* — a draw on a lost connection ends without an error and is drawn again on
  the next render; *Related decisions*: 0200.
- [backend-host](../../docs/areas/backend-host.md): *Related decisions*: 0197, 0201; no rule changes.

## Documentation updates

Swept: `grep` for `.NET 10`, `net10`, `10.0-noble`, `sdk:10.0`, `10.0.403` over the repository and `docs/areas/`
enumerations of the login's anonymous routes. All Dev, after the code is green, unless named:

- `README.md:10`: ".NET 11".
- `AGENTS.md:21` and `.github/copilot-instructions.md:20` (project block, identical): ".NET 11 with a Blazor web UI".
- `docs/ARCHITECTURE.md:111`: ".NET 11", link [0197](../../docs/decisions/0197-backend-moves-to-dotnet-11-on-the-resolute-chiseled-runtime.md).
- `docs/CONTRIBUTING.md:195`: example `sdk:11.0`.
- `.github/dependabot.yml:32–33,39–40`: the comments name `net11.0`, 12.0.0 and "(11 to 12)".
- `deploy/backend/Dockerfile:1`: ".NET 11".
- `.squad/stack.md:4,15–16` (Dev reports the exact text, orchestrator writes it): `.NET 11`, SDK `11.0.100`, `net11.0`,
  record 0197 beside 0073.
- `.squad/project.md:4` (Dev reports, orchestrator writes): `.NET 11 with Blazor (… records 0073, 0197)`; *Test doubles*,
  row web login (Tester reports, orchestrator writes): `Fixed*`, `Unset*` providers.
- The three area documents of *Areas*.
- `CHANGELOG.md`: none (written in the release PR, `docs/CONTRIBUTING.md`, *Cutting a release*); `docs/BENCHMARKS.md`: none
  (spec, *Out of scope*).

## Architecture check

No guarantee of `.squad/project.md` (data gaps, hanging collector, backfill) is touched: the change is the backend's platform
and analyzer fixes; the wire format, schema and configuration stay (N1). The session binding of live connections
(`docs/ARCHITECTURE.md`, web login) keeps its rule; only how the handler learns the state changes (record 0199). The
`ARCHITECTURE.md:111` platform line is updated and links 0197.

## Security considerations

- [x] No limits: no parsed input changes.
- [x] Exceptions reaching a user: none new. A `JSDisconnectedException` in a chart no longer surfaces (no user is left to see
  it); an `InvalidOperationException` from an unset authentication state ends the live connection like `SessionEndedException`
  and is logged by the framework as a circuit error, never shown.
- The runtime base moves to Ubuntu 26.04 chiseled: no shell, no package manager (fact 23), `65532:65532`, pinned by digest;
  the smoke test checks user, health, ports and ingest (AC3).
- `HEAD /login` returns the public sign-in page's headers only; every other anonymous `HEAD` stays `302`; a signed-in `HEAD`
  of a page is rendered like its `GET` without a body, under the same authorization (record 0198).
- SQLitePCLRaw 3 ships its own native SQLite build; the storage and log-search tests (security areas 9, 10) run on it.

## Decision records

- `docs/decisions/0197-backend-moves-to-dotnet-11-on-the-resolute-chiseled-runtime.md` (Proposed) — supersedes 0073, amends
  0041. 0073's status becomes `Superseded by 0197` when 0197 is accepted (Lead, the only edit of a released record).
- `docs/decisions/0198-razor-component-endpoints-answer-head-from-dotnet-11.md` (Proposed) — amends 0103 (context fact);
  0103 stays unedited.
- `docs/decisions/0199-session-circuit-handler-follows-authentication-state-changes.md` (Proposed).
- `docs/decisions/0200-chart-calls-tolerate-a-lost-connection-bl0016-off-in-tests.md` (Proposed).
- `docs/decisions/0201-command-output-writes-opt-out-of-cancellation.md` (Proposed).
- 0097, 0098, 0099, 0105: every fact holds on 11 (facts 11–16, 18); no record.

## Out of scope / follow-ups

- Follow-up issue: `web-login.md` *Access* says root-component updates bypass the session check; the framework (10.0.0 and
  11.0.0) passes them through it (related defect above). Also: `ComponentHub.ConnectCircuit` and `OnAuthenticationRefreshedAsync`
  call `SetCircuitUser(Context.User)`, so a reconnecting circuit takes on the session of the new connection's cookie, while
  `web-login.md:88` says "A live connection belongs to the session that opened it" (not caused by this change).
- The Go agent; re-measuring on .NET 11.

## Challenge notes

Devil's Advocate, mode `plan`, verbatim (0 major, 3 minor):

1. **MINOR 1: the session check's "no lock" reasoning is wrong.** The plan says "no lock (the framework raises the event on the circuit's dispatcher; a reference write is atomic)" (*Approach*, *Session check*). The first half is wrong. In the 10 and 11 sources `AuthenticationStateChanged` is raised from SignalR hub threads, via `SetCircuitUser` in `ComponentHub.cs:101` (ConnectCircuit) and `:259` (OnAuthenticationRefreshedAsync), into `CircuitHost.cs:630-639`. It is also raised from the revalidation loop (`RevalidatingServerAuthenticationStateProvider.cs:73,90,103-107`: `ForceSignOut` runs after `Task.Delay`, on a thread-pool thread, for this project's `SessionRevalidatingStateProvider`). The inbound-activity handler runs before `Renderer.Dispatcher.InvokeAsync` (`CircuitHost.cs:410,436,470,570`), so it is not on the dispatcher either. The real risk is the lazy initial read: "read once and stored" can overwrite a state the event announced in the meantime. Not a security hole (`SessionStore.Touch` decides on its own), but a newer valid state could be lost. Instruction to the Dev: store the initial state only if no change was announced, with `Interlocked.CompareExchange(ref _state, initial, null)`; read the field with `Volatile.Read`; correct the threading sentence in the plan and in record 0199 (its *Context* says where the event comes from: hub threads and the revalidation loop).
2. **MINOR 2: HEAD is now accepted on every page, not just `/login`.** `RazorComponentEndpointFactory.cs:18` applies to every Razor component page. With a session, a `HEAD` on a page now prerenders the whole page without a body (including its data loading: `App.razor:17`, InteractiveServer with default prerender); on 10 such a request matched no endpoint. A `HEAD /login` with a session goes through the login page's redirect to `/`. Instruction: record 0198 and web-login *Access* state that a signed-in `HEAD` on a page is answered like `GET` without a body. The Tester adds one case beside `LoginHeadWithoutSessionAnswersLikeGet` for a signed-in `HEAD /`; state the intent ("answered like GET, no body"), do not prescribe a status the Tester has not run.
3. **MINOR 3: AC3's list of CI checks leaves out two jobs the SDK change affects.** `.github/workflows/codeql.yml:25-26,49-51` runs a manual C# build under the CodeQL tracer pinned at v4.39.3 (its extractor has to handle SDK 11 and C# 15, `LangVersion latest`), and the `sonarqube` job (`ci.yml:79-135`) builds and scans with SDK 11. Instruction: on the early draft PR, the Dev or orchestrator confirms "Analyze (csharp)" and "SonarQube" are green alongside the AC3 jobs.

## Security notes

Security, mode `plan`, `APPROVED`; non-blocking findings, verbatim:

1. **The plan and AC3 say the CI smoke test checks "no shell"; it does not.** Plan *Security considerations*: "the smoke test checks user, shell and health (AC3)"; spec AC3: "(non-root user, no shell, health check, ingest port)". `ci.yml:245-249` checks only `Config.User`; `smoke-test-backend.sh` checks the user (line 194), `/data` ownership, health, ports and ingest; nothing checks for a shell or package manager. Smallest fix: add a *Verified facts* row: "`aspnet:11.0-resolute-chiseled` amd64 (index sha256:fc0ee12f…) has no shell, apt or dpkg; usr/bin holds only dotnet; no entry is owned by 65532; etc/passwd is the same as the noble image", citing the registry layer listing (amd64 manifest sha256:6207c495…a58cce4, 5 layers, 467 entries); drop "shell" from what the plan and AC3 say the smoke test checks. A real no-shell check in the smoke script would change a CI script (area 13): a linked issue if wanted.
2. **The reasoning behind the lock-free session field is wrong.** In v11.0.0 `ForceSignOut` raises `AuthenticationStateChanged` from its revalidation loop on a thread-pool thread; `ConnectCircuit` and `OnAuthenticationRefreshedAsync` raise it on hub threads; the inbound activity handler runs before `Renderer.Dispatcher.InvokeAsync` (`CircuitHost.cs:410`). The lazy first read can overwrite an announcement made at the same moment; the guarantee holds because `SessionStore.Touch` looks the session up live. Fix: correct the sentence in the plan and 0199; the implementation stores the initial state with `Interlocked.CompareExchange(ref _state, provider.GetAuthenticationStateAsync(), null)` and reads with `Volatile.Read`; if `GetAuthenticationStateAsync` throws, nothing is stored. No new test needed.
3. **HEAD with a session is also a behavior change.** A signed-in `HEAD` to any component page changes from 405 to a full server render with the body dropped; protection is unchanged (same authorization as GET, page renders write nothing, `Login.razor.cs:94-96` still sends a signed-in user to `/`). Add verbatim to record 0198 and `docs/areas/web-login.md` *Access*: "With a session, a `HEAD` request to a page is answered like its `GET`, without a body (from .NET 11 on)."
4. **Observation, not caused by this change, for the follow-up issue.** `ComponentHub.ConnectCircuit` (`:259`) and `OnAuthenticationRefreshedAsync` call `SetCircuitUser(Context.User)`, so a reconnecting circuit takes on the session of the new connection's cookie, while `web-login.md:88` says "A live connection belongs to the session that opened it". Add one line about it to the follow-up issue.
