# Plan: Web UI login and operation behind a reverse proxy

Source: Issue #25 | [spec.md](spec.md)
Status: Draft
Tier: security — the change implements security area 3 (*Web UI login*) and touches area 8 (*Secrets handling*: the web password
hash), area 13 (the CI smoke script) and the deployment files of area 2, and adds a NuGet dependency.

## Problem / root cause

Feature (summary of `spec.md`): the web listener serves every page and the live connection to anyone who reaches it. The login, its
sessions, CSRF protection, failed-sign-in limits and the trust of a reverse proxy's forwarded headers are added; the normative rules are
in the new area document [`docs/areas/web-login.md`](../../docs/areas/web-login.md), written with this plan.

Claims of the issue and the owner's comment, checked against the code (base `main` 4d5dc41) and the framework sources (ASP.NET Core
10.0.12, the runtime of this SDK, read from `dotnet/aspnetcore` tag `v10.0.12`):

- *Backend plain HTTP internally, TLS at the proxy* — confirmed: `ServeCommand.Listen` opens two plain Kestrel listeners
  (`src/Vandox.Backend/Hosting/ServeCommand.cs`, `Open`); record 0023.
- *Use the framework's cookie authentication, antiforgery and forwarded-headers middleware* — confirmed available in the shared
  framework `Microsoft.AspNetCore.App` (no package). `app.UseAntiforgery()` is already in the pipeline (`ServeCommand.Build`); nothing
  else of the three is.
- *Password hash (argon2id) supplied as a secret* — confirmed in part: `VANDOX_WEB_PASSWORD_HASH`(`_FILE`) is already read
  (`BackendConfigLoader.Load`, `BackendSecrets.WebPasswordHash`) with the generic secret rules, and nothing uses it. .NET 10 has **no
  Argon2**: a dependency is needed (record 0104).
- *The UI refuses to start without it* — not implemented; `docs/areas/configuration-and-secrets.md` already promises it ("the web UI
  refuses to start without the password hash").
- *#24 is done* — confirmed (issue closed; `Components/`, `SecurityHeadersMiddleware`, CSP with `form-action 'self'`, record 0099,
  whose consequences already reserve the login form under `form-action 'self'`).
- Owner: *#13 publishes the web port on 127.0.0.1 by default (0023, 0060)* — confirmed, `deploy/backend/docker-compose.yml`
  `${WEB_BIND_ADDRESS:-127.0.0.1}:8080:8080`.
- Owner: *LAN hosts may reach a published port depending on the engine* — confirmed from Docker's release notes (Engine 28.0.0 fixed
  neighbor access to loopback-mapped ports; 28.3.3 fixed CVE-2025-54388, the same after a firewalld reload). *Verify on the supported
  NAS Docker versions* — **not verifiable here** (no NAS, no Docker daemon in the session): recorded as unverified in record 0105 with a
  check procedure in `docs/REVERSE_PROXY.md`; follow-up issue proposed (see *Out of scope / follow-ups*).
- Owner: *protect every route except `/healthz` (0059)* — `/healthz` is mapped by `HealthEndpoint.Map` and must get `AllowAnonymous`,
  otherwise the fallback policy redirects the image's own health probe (`HealthCheckCommand` follows no redirect) and the container
  turns unhealthy.

Framework facts the plan relies on, read from the 10.0.12 sources:

- `ForwardedHeadersOptions` trusts `::1` and `127.0.0.0/8` by default, and **`ForwardedHeadersMiddleware.ApplyForwarders` checks no
  peer at all when `KnownProxies` and `KnownIPNetworks` are both empty** (`checkKnownIps = KnownIPNetworks.Count > 0 ||
  KnownProxies.Count > 0`): clearing both lists to "trust nobody" would trust everybody. Hence: no middleware when the option is empty.
  `CheckKnownAddress` also compares an IPv4-mapped peer as IPv4. `KnownNetworks` is obsolete (`ASPDEPR005`); use `KnownIPNetworks`
  (`System.Net.IPNetwork`). `ForwardLimit` defaults to 1 and the values are taken right to left.
- `RazorComponentEndpointFactory` maps every page for GET and POST and adds `RequireAntiforgeryTokenAttribute`; the antiforgery
  middleware only records the result (`IAntiforgeryValidationFeature`) and the component endpoint answers 400. A minimal API endpoint
  must check it itself. `DefaultAntiforgeryTokenStore.GetRequestTokensAsync` rethrows a malformed or oversized form
  (`InvalidDataException`) and a broken body (`IOException`) as `AntiforgeryValidationException`; **only `AntiforgeryMiddleware`
  catches it** (and only for an endpoint with `IAntiforgeryMetadata { RequiresValidation: true }`), while
  `DefaultAntiforgery.IsRequestValidAsync` lets it escape (line 104). An endpoint that calls `IsRequestValidAsync` itself therefore
  answers `500` to an oversized form; the logout endpoint carries antiforgery metadata instead (Security B1).
- `FormFeature` buffers each multipart **file** section with `EnableRewind(MemoryBufferThreshold, MultipartBodyLengthLimit)` and
  caps the whole multipart body at `MultipartBodyLengthLimit` (default 128 MiB; beyond `MemoryBufferThreshold`, 64 KiB, it spills to a
  temporary file under `/tmp`, a tmpfs counted against the 512 MiB container limit); `ValueLengthLimit` does not apply to file
  sections. `MultipartBodyLengthLimit` is therefore set to 65,536, equal to the default `MemoryBufferThreshold` (Security B2).
- The token is also read from the header `RequestVerificationToken`; when that header is present the form is not read at all, so the
  form limits never run before the check fails or succeeds on the header.
- `AuthorizationMiddleware` applies the fallback policy also when no endpoint matched (`endpoint?.Metadata … ?? Array.Empty`), so an
  unknown path is challenged, not 404.
- `CircuitHost`: every event, interop call, navigation and the initialization go through `HandleInboundActivityAsync`; an exception
  there is caught and raised as `UnhandledException`, and `CircuitRegistry.CircuitHost_UnhandledException` terminates the circuit.
  The render acknowledgement passes the handler without being awaited (an exception there is dropped, and the acknowledgement with
  it). `CircuitInboundActivityContext` has only an internal constructor.
- `RevalidatingServerAuthenticationStateProvider` revalidates with `Task.Delay` on the real clock (not a `TimeProvider`).
- `AuthenticationSchemeOptions.TimeProvider` exists, so the cookie handler's expiry check can use the injected clock.
- `SemaphoreSlim.WaitAsync` has no `TimeProvider` overload; a wait abandoned with `Task.WaitAsync(timeout, timeProvider)` stays
  queued, later takes the semaphore and is never released. A wait cancelled through its `CancellationToken` is removed from the queue.
  Hence the gate wait uses `new CancellationTokenSource(VerificationWait, timeProvider)` linked with the request token (Security B3).
- `ForwardedHeadersMiddleware`, when it applies `X-Forwarded-For`, writes the original peer to `X-Original-For` (line 310) and removes
  the consumed value; when it does not apply it (untrusted peer, no middleware) the client's `X-Forwarded-For` stays and the middleware
  writes no `X-Original-For` (one the client sent itself stays, which can only suppress the hint). This is how the hint of Devil's
  Advocate objection 1 detects an ignored header.

Related defects and inconsistencies found on the way:

- `.squad/project.md`, security area 3, states as a goal that "the UI port is reachable only by that proxy" — the application cannot
  enforce that, and record 0060 accepts that the loopback binding is not a boundary. The Dev rewrites the sentence (see
  *Documentation updates*).
- `docs/areas/web-ui.md` (*Limits and errors*) states that the live connection is open to whoever reaches the port until #25; this
  change closes it (the hub requires a session).

## Acceptance criteria

Test classes live in `tests/Vandox.Backend.Tests` unless the name says `Vandox.Core.Tests`. "Service test" means a test that starts the
real service through `BackendFixture` (as `ServeTests` does). The rules and exact texts are in `docs/areas/web-login.md`.

**AC1 — The service refuses to start without a valid hash; nothing else needs it.**
- [ ] AC1.1: Without `VANDOX_WEB_PASSWORD_HASH`(`_FILE`) the service logs `"msg":"web password hash missing"` with
  `"variable":"VANDOX_WEB_PASSWORD_HASH"`, exits 1, and creates no `vandox.db`. — `ServeTests.ServeRefusesMissingPasswordHash`
- [ ] AC1.2: With a hash refused by AC2 it logs `"msg":"web password hash invalid"` with the exact reason, exits 1, creates no
  `vandox.db`, and the log does not contain the hash text. — `ServeTests.ServeRefusesInvalidPasswordHash` (DataRow: a weak reference
  vector → `memory must be 19456 to 262144 KiB`; an `argon2i` string → the form reason)
- [ ] AC1.3: `-healthcheck`, `import`, `incidents`, `templates` and `report` still run without the secret (existing tests stay green;
  no new test).

**AC2 — Hash form, bounds and verification (real sample: `testdata/argon2/reference-argon2id.txt`, see *Test files*).**
- [ ] AC2.1: The five in-bounds reference vectors parse and verify their password; each rejects a wrong password and the empty
  password. — `PasswordHashTests.PasswordHashVerifiesReferenceVectors` (DynamicData read from the fixture file)
- [ ] AC2.2: The three out-of-bounds reference vectors are refused with their reason (two `memory …`, one `iterations …`). —
  `PasswordHashTests.PasswordHashRefusesWeakReferenceVectors`
- [ ] AC2.3: Every refused form of the table *Accepted forms of the hash* below throws `PasswordHashException` whose `Message` is
  exactly the listed reason. — `PasswordHashTests.PasswordHashParseRefusesInvalidForms` (DataRow per row)
- [ ] AC2.4: `Create(password)` writes `$argon2id$v=19$m=65536,t=3,p=4$<22 base64 chars>$<43 base64 chars>`, two calls give different
  salts, and the result parses and verifies the password; `Create` with explicit salt and parameters (which checks no bounds) reproduces
  byte for byte the reference vectors `m=65536,t=2,p=1` and `m=256,t=2,p=2` (the second pins Konscious's multi-lane path, which the
  default parallelism 4 uses, against the reference). — `PasswordHashTests.PasswordHashCreateRoundTrips`,
  `PasswordHashTests.PasswordHashCreateReproducesReferenceVector` (DataRow per vector)
- [ ] AC2.5: `Verify` returns `false` for a password of 1,025 characters (the right one with one character appended to a 1,024-character
  password whose hash the test creates) and `true` for that 1,024-character password. That no hash is computed for the long one is
  pinned at the `LoginService` level (AC7.6), where the verifier's calls are counted. — `PasswordHashTests.PasswordHashVerifyRejectsOverlongPassword`

**AC3 — `vandoxd hash-password`.**
- [ ] AC3.1: A first line of 12 to 1,024 characters prints one hash line (form of AC2.4) to stdout that verifies that password; exit 0;
  stderr empty; a `\r\n` line end and a missing final line feed work alike. — `HashPasswordCommandTests.HashPasswordPrintsVerifiableHash`
  (DataRow: 12 characters, 1,024 characters, with `\r\n`, non-ASCII `pässwörd-ünïcode`)
- [ ] AC3.2: 11 characters, 1,025 characters, empty input, a tab (U+0009) or another Cc character inside → exactly the line
  `vandoxd hash-password: the password must have 12 to 1024 characters and no control characters` on stderr, nothing on stdout,
  exit 1; the password never appears in either stream; an over-long first line is not read past 1,026 characters. —
  `HashPasswordCommandTests.HashPasswordRefusesInvalidPasswords`
- [ ] AC3.3: `hash-password` parses as its sub-command; `-h` prints its usage (exit 0); a positional argument after it →
  `vandoxd hash-password: no argument is expected` (exit 2); `-config x` after it → `flag provided but not defined: -config` (exit 2);
  combined with `-healthcheck` → usage error as for the other sub-commands. — `CommandLineTests.CommandLineParsesHashPassword`
  (DataRow per case); `BackendApp.RunAsync(["hash-password"], …)` reads `ServeHooks.Input` and prints the hash —
  `HashPasswordCommandTests.HashPasswordRunsThroughBackendApp`

**AC4 — Every route needs a session except the login page, `/healthz`, the static files and the sign-out post (issue AC 1).**
- [ ] AC4.1: Anonymous: `GET /` and `GET /nothing-here` → `302`, `Location: /login`; `HEAD /` → `302`; `POST /_blazor/negotiate?negotiateVersion=1`
  → `401`; `GET /login` → `200`; `GET /healthz` → `200` `ok`; every file of `ServeServesWebAssetsOnlyOnWebPort` → `200`; ingest port
  unchanged (`404` for all). — Service test `ServeTests.ServeRequiresSessionForEveryRouteExceptLoginHealthAndAssets`
- [ ] AC4.2: Signed in: `GET /` → `200` with the page, `GET /nothing-here` → `404`, negotiate → `200`, `GET /login` → `302` to `/`. —
  same test, second half (signs in through `LoginTestClient`)
- [ ] AC4.3: Every routable component of the assembly except `Login` carries `[Authorize]`, `Login` carries `[AllowAnonymous]` and
  `[ExcludeFromInteractiveRouting]`. — `RoutesTests.RoutesEveryPageExceptLoginRequiresAuthorization`
- [ ] AC4.4: The redirect event: anonymous `GET`/`HEAD` of a path outside `/_blazor` → `302` `/login` without a query; `POST`, and
  `GET /_blazor` (the WebSocket request) → `401` without `Location`. — `WebAuthenticationTests.WebAuthenticationRedirectsOnlyPageLoadsToLogin`
  (DataRow; `DefaultHttpContext` and a `RedirectContext`)
- [ ] AC4.5: When the session has ended, a page's `AuthorizeRouteView` renders `RedirectToLogin`, which navigates to `login` with a
  full page load. — `RedirectToLoginTests.RedirectToLoginNavigatesWithForceLoad` (`ComponentTestRenderer`, `TestNavigationManager`)

**AC5 — Cookie flags and forwarded headers (issue AC 2).**
- [ ] AC5.1: `Set-Cookie` of `vandox_session` after a sign-in has `httponly`, `samesite=lax`, `path=/`, no `expires`/`max-age`; and
  `secure` exactly in the case *trusted peer + `X-Forwarded-Proto: https`*; not for a direct request, not for `X-Forwarded-Proto: https`
  from an untrusted peer, not when the option is empty. `vandox_csrf` (from `GET /login`) has `httponly`, `samesite=strict` and the same
  `secure` rule. — Service test `ServeTests.ServeSetsCookieFlagsFromTrustedProxyOnly` (DataRow: option empty; `127.0.0.2` (peer
  untrusted); `127.0.0.1` (peer trusted))
- [ ] AC5.2: With `web.trusted_proxies: 127.0.0.1`, five wrong passwords sent with `X-Forwarded-For: 198.51.100.7` lock that client
  (the right password with the same header → `429`) but not `X-Forwarded-For: 198.51.100.8` (right password → `302` `/`), and the log
  carries `"client":"198.51.100.7"` and no `forwarded header ignored`. With `web.trusted_proxies: 127.0.0.2`, and with the option
  empty, the same sequence locks `127.0.0.1`, so `198.51.100.8` gets `429` too, and the log carries `forwarded header ignored` with
  `"client":"127.0.0.1"` exactly once. — Service test `ServeTests.ServeTakesClientAddressFromTrustedProxyOnly` (DataRow)
- [ ] AC5.3: `ForwardedHeadersSetup.CreateOptions` returns `null` for an empty list; otherwise options with exactly the configured
  networks, no `KnownProxies`, no loopback, `ForwardLimit` 1 and `XForwardedFor | XForwardedProto`. Running
  `ForwardedHeadersMiddleware` with them on a `DefaultHttpContext`: peer `10.0.0.1` in `10.0.0.1/32` → `RemoteIpAddress` and scheme
  taken from the rightmost values of `X-Forwarded-For: 192.0.2.1, 198.51.100.7` / `X-Forwarded-Proto: http, https`; peer
  `::ffff:10.0.0.1` → taken; peer `10.0.0.2` → unchanged; peer `127.0.0.1` with a list without loopback → unchanged;
  `X-Forwarded-Host: evil.example` → `Host` unchanged; unparsable rightmost `X-Forwarded-For: unknown` → address unchanged. —
  `ForwardedHeadersSetupTests.ForwardedHeadersSetupReturnsNoOptionsWithoutProxies`,
  `ForwardedHeadersSetupTests.ForwardedHeadersSetupAppliesHeadersOnlyFromConfiguredProxies` (DataRow)
- [ ] AC5.4: `web.trusted_proxies` accepts and refuses exactly the forms of the table *Accepted forms of `web.trusted_proxies`*. —
  `Vandox.Core.Tests/TrustedProxyListTests.TrustedProxyListParsesAcceptedForms`, `…TrustedProxyListRefusesOtherForms` (DataRow per row)
- [ ] AC5.5: The loader reads `web.trusted_proxies` (default empty), refuses an invalid value with
  `config: <file>:<line>: web.trusted_proxies: must be a comma-separated list of at most 16 IP addresses or networks, such as 172.18.0.1 or 172.18.0.0/16`
  (the message does not contain the value), and `WebConfig.GetTrustedProxies()` returns the parsed networks. —
  `Vandox.Core.Tests/BackendConfigLoaderTests.BackendConfigLoaderReadsTrustedProxies`,
  `…BackendConfigLoaderRefusesInvalidTrustedProxiesWithoutValue`, `Vandox.Core.Tests/WebConfigTests.WebConfigReturnsTrustedProxies`;
  the example file sets `trusted_proxies: ""` (existing `BackendConfigLoaderLoadsRepositoryExample` checks it).

**AC6 — CSRF token on every form post (issue AC 3).**
- [ ] AC6.1: `POST /login` without a token, and with a token that belongs to another antiforgery cookie → `400`, no session cookie, and
  the attempt is not counted (six such posts followed by the right password → `302`). `POST /logout` without a token while signed in →
  `400` and the session still works (`GET /` → `200`). `POST /` (a page) without a token while signed in → `400`. A login post with a
  20 KiB password value → `400`, not counted. A signed-in `POST /logout` with a valid token and a 20 KiB value, and one with 65
  values → `400` (not `500`), and the session still works. A multipart (`multipart/form-data`) login post with the valid token field
  and a 1 MiB file part → `400`, not counted; the same post to `/logout` while signed in → `400`, session still works. No response of
  this test is `500`, and the service log has no `error`-level line. — Service test
  `ServeTests.ServeRejectsFormPostsWithoutValidAntiforgeryToken`
- [ ] AC6.2: `WebAuthentication.AddServices` configures `FormOptions` with `ValueLengthLimit` 16,384, `ValueCountLimit` 64,
  `MultipartBodyLengthLimit` 65,536 and `MemoryBufferThreshold` 65,536 (so no form part is ever buffered to a file). —
  `WebAuthenticationTests.WebAuthenticationConfiguresFormLimits` (resolves `IOptions<FormOptions>` from a `ServiceCollection`)
- [ ] AC6.3: `MapLogout` maps `POST /logout` with `IAntiforgeryMetadata` whose `RequiresValidation` is `true` and with
  `IAllowAnonymous`; the handler answers `400` when the `IAntiforgeryValidationFeature` is missing or not valid and does not sign out.
  — `WebAuthenticationTests.WebAuthenticationMapsLogoutWithAntiforgeryMetadata`,
  `WebAuthenticationTests.WebAuthenticationLogoutRefusesMissingOrInvalidAntiforgeryFeature` (DataRow: no feature, invalid feature;
  `DefaultHttpContext`, a `LoginService` over `ScriptedPasswordVerifier`)

**AC7 — Failed sign-ins (issue AC 4).**
- [ ] AC7.1: Per client: failures 1–4 leave it unlocked, the 5th within 15 minutes locks it; it stays locked until 15 minutes after the
  5th failure and is unlocked after that; another client is not affected. — `LoginThrottleTests.LoginThrottleLocksClientAfterFiveFailures`,
  `LoginThrottleTests.LoginThrottleUnlocksAfterLockoutDuration` (`FakeTimeProvider`)
- [ ] AC7.2: Failures older than 15 minutes do not count (4 failures, 16 minutes, 4 failures → not locked); a success clears the
  client's count. — `LoginThrottleTests.LoginThrottleForgetsFailuresOutsideWindowAndAfterSuccess`
- [ ] AC7.3: 100 failures from 100 different clients within one hour lock every client, a new one included, until the oldest failure
  is one hour old. — `LoginThrottleTests.LoginThrottleLocksEveryoneAfterHundredFailures`
- [ ] AC7.4: Failures of 4,097 clients keep at most 4,096 tracked (`TrackedClients`), and the one with the oldest last failure is the
  one forgotten. — `LoginThrottleTests.LoginThrottleBoundsTrackedClients`
- [ ] AC7.5: `ClientKey`: `null` → `unknown`; `192.0.2.10` → `192.0.2.10`; `::ffff:192.0.2.10` → `192.0.2.10`;
  `2001:db8:1:2:3:4:5:6` and `2001:db8:1:2:ffff::1` → `2001:db8:1:2::/64`; `fe80::1%3` → `fe80::/64`. —
  `LoginThrottleTests.LoginThrottleClientKeyGroupsIpv6ByNetwork` (DataRow)
- [ ] AC7.6: `LoginService`: a locked client gets `Throttled` without the verifier being called (also with the right password); a wrong
  password → `WrongPassword` and one failure recorded; the right password → `SignedIn`, the client's failures cleared, a cookie
  sign-in with a `vandox:session` claim of 43 base64url characters; a password of 1,025 characters → `WrongPassword` without a call of
  the verifier. — `LoginServiceTests.LoginServiceRefusesLockedClientWithoutVerifying`,
  `LoginServiceTests.LoginServiceSignsInWithNewSessionClaim`, `LoginServiceTests.LoginServiceCountsWrongAndOverlongPasswords`
- [ ] AC7.7: Two concurrent attempts never run the verifier at the same time; an attempt that waits more than 5 seconds for the running
  check returns `Throttled` and records no failure. — `LoginServiceTests.LoginServiceVerifiesOneAttemptAtATime`
  (`ScriptedPasswordVerifier` that blocks until released, `FakeTimeProvider`)
- [ ] AC7.7a: The gate stays usable after abandoned waits: while a check blocks, one waiter times out (clock advanced by 5 seconds →
  `Throttled`) and another waiter's token is cancelled (→ `OperationCanceledException`, nothing recorded); after the blocked check is
  released, a new attempt runs the verifier (call count rises by one) and its outcome is not `Throttled`; the same holds for a second
  new attempt after it. — `LoginServiceTests.LoginServiceGateStaysUsableAfterTimedOutAndCancelledWaits`
- [ ] AC7.9: When a failure locks the client and the request carries `X-Forwarded-For` but no `X-Original-For` (the header was not
  applied), `forwarded header ignored` (`client`) is logged once, together with `login locked`; not when `X-Original-For` is present,
  not without `X-Forwarded-For`, and not for failures 1 to 4. — `LoginServiceTests.LoginServiceHintsAtIgnoredForwardedForWhenLocking`
  (DataRow; `ListLogger`)
- [ ] AC7.8: Through the login page: five wrong passwords → each `401` with *The password is not correct.*; the sixth attempt with the
  right password → `429` with *Too many sign-in attempts. Try again later.*; with `ServeHooks.Clock` a `FakeTimeProvider` advanced by
  15 minutes, the right password → `302` `/`; the log has `login failed` five times, `login locked` once, `login succeeded` once, and
  never the password. — Service test `ServeTests.ServeLocksOutClientAfterFiveFailedSignIns`

**AC8 — Sessions, logout and live connections (issue AC 5).**
- [ ] AC8.1: `SessionStore`: a stored ticket is retrieved by its key (the `vandox:session` claim); a ticket without the claim throws
  `InvalidOperationException`; idle 1 hour → `null`; activity every 50 minutes keeps it until 12 hours after storing, then `null`
  despite activity; `RemoveAsync` → `null` at once; a 17th session removes the oldest (`Count` stays 16); `Touch`/`IsActive` follow the
  same rules, `IsActive` does not extend the idle time. — `SessionStoreTests.SessionStoreExpiresIdleSessions`,
  `…SessionStoreEndsSessionAfterAbsoluteLifetime`, `…SessionStoreRemoveEndsSession`, `…SessionStoreBoundsSessions`,
  `…SessionStoreRefusesTicketWithoutSessionClaim`, `…SessionStoreIsActiveDoesNotExtendSession`
- [ ] AC8.2: Service, `FakeTimeProvider` clock: signed in, `GET /` → `200`; after 61 minutes `GET /` → `302` `/login`. A second session
  used every 50 minutes is still valid at 11 h 40 min and redirected at 12 h 10 min. — Service test `ServeTests.ServeEndsSessionAfterIdleAndAbsoluteTimeout`
- [ ] AC8.3: Service: signed in, the session cookie copied into a second client; `POST /logout` with a valid token → `303`
  `Location: /login` and a deleting `Set-Cookie`; the copied cookie then gets `302` `/login` for `GET /` and `401` for the negotiate
  post; log `logged out`. — Service test `ServeTests.ServeLogoutEndsSessionOnServer`
- [ ] AC8.4: `SessionCircuitHandler`: with an active session the inbound handler calls `next` once and extends the idle time; after
  `RemoveAsync`, or with an anonymous user, it throws `SessionEndedException` and does not call `next`. —
  `SessionCircuitHandlerTests.SessionCircuitHandlerPassesActivityOfActiveSession`,
  `SessionCircuitHandlerTests.SessionCircuitHandlerEndsCircuitAfterSessionEnded` (`FixedAuthenticationStateProvider`; the handler is
  invoked with a `null!` context, which `next` ignores)
- [ ] AC8.5: `SessionRevalidatingStateProvider.IsValid` is `true` for an active session and `false` after logout, after expiry and for
  a user without the claim; `RevalidationInterval` is 30 seconds. — `SessionRevalidatingStateProviderTests.SessionRevalidatingStateProviderValidatesSession`

**AC9 — Login page.**
- [ ] AC9.1: `GET /login` → `200`, a `<form method="post"` with the hidden `__RequestVerificationToken` and `_handler` value `login`, a
  `password` input with `type="password"` and `autocomplete="current-password"`, no `_framework/blazor.web.js`, no
  `<!--Blazor:` marker, and `WebSourceScanner.FindInMarkup` finds nothing; it carries the security headers of 0099. —
  `LoginTests.LoginPageRendersStaticFormWithoutLiveConnection`
- [ ] AC9.2: A wrong password re-renders the page with status `401` and the message, and the page source does not contain the sent
  password. — `LoginTests.LoginShowsWrongPasswordWithoutEchoingIt`
- [ ] AC9.3: The signed-in layout shows the *Sign out* form (`method="post"`, `action="logout"`, antiforgery input); the anonymous
  layout does not. — `MainLayoutTests.MainLayoutShowsSignOutOnlyWhenSignedIn`

**AC10 — Image and deployment (verified by the Dev and CI, not unit tests).**
- [ ] AC10.1: *Image build check* (`.squad/stack.md`) after the package and `.razor` changes: the `--no-restore` publish succeeds and
  `publish/` holds `Konscious.Security.Cryptography.Argon2.dll`, `Konscious.Security.Cryptography.Blake2.dll` and
  `wwwroot/_framework/blazor.web.js`. Owner: Dev, before the review; result in `log.md`.
- [ ] AC10.2: The smoke test (`.github/scripts/smoke-test-backend.sh`) creates the hash with the image's own `hash-password`, starts the
  compose file with the hash secret, and checks: healthy; `/healthz` `ok`; `GET /` → `302` to `/login`; `GET /login` → `200` with the
  policy; the static files `200`; a sign-in with the password (curl cookie jar, token taken from the form) → `302` `/`, then `GET /` →
  `200`. Owner: Dev (`bash -n`, *Analyzer gate* `--lint-only`); authoritative: CI job *Release build check*.

## Approach

### Accepted forms of the hash (guard: `PasswordHash.Parse`)

Enumerated from the reference decoder (`phc-winner-argon2/src/encoding.c`, `decode_string`, `decode_decimal`, `from_base64`): it
accepts an optional `$v=`, decimals without leading zero (a lone `0` allowed), standard base64 without padding with length ≠ 1 mod 4
and zero trailing bits, nothing after the hash. The guard accepts a strict subset. `B` stands for the valid
`$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc`; "form" is the reason
`must be an argon2id hash in the form $argon2id$v=19$m=<memory>,t=<iterations>,p=<parallelism>$<salt>$<hash>`.

| Input | Result |
| ----- | ------ |
| `B`, and the other in-bounds reference vectors | accepted |
| `$argon2i$…`, `$argon2d$…`, `$Argon2id$…` (rest as `B`) | form |
| `$argon2id$m=65536,t=2,p=1$…` (no `v=`, reference: version 16) | `version must be 19` |
| `$argon2id$v=16$…` | `version must be 19` |
| `v=019`, `m=065536`, `m=+65536`, `m= 65536`, fullwidth digits U+FF10–U+FF19 in `m` | form |
| parameters in another order (`m=…,p=1,t=2`), a fourth parameter (`,data=AAAA`, `,keyid=AAAA`) | form |
| a decimal of more than 10 digits | form |
| `m=19455`, `m=262145`, `m=4294967296` | `memory must be 19456 to 262144 KiB` |
| `t=1`, `t=11` | `iterations must be 2 to 10` |
| `p=0`, `p=9` | `parallelism must be 1 to 8` |
| salt `c29tZXNhbHQ=` (padding), salt `c29tZXNhbHR` (non-zero trailing bits), salt of 13 characters (length 1 mod 4) | form |
| hash with `-` or `_` (base64url: hash of the `differentpassword` vector with `+` replaced by `-`) | form |
| salt `c29tZXNhbA` (7 bytes), salt of 65 bytes, empty salt (`$$`) | `salt must be 8 to 64 bytes` |
| hash of 15 bytes, hash of 65 bytes | `hash must be 16 to 64 bytes` |
| `B` without the hash part, `B` + `$`, `B` + one space, one space + `B`, the empty text | form |

Checks run in this order: the form (prefix, structure, digits, base64 alphabet and canonical form), then version, memory, iterations,
parallelism, salt length, hash length; the first failing check's reason is thrown. The secret rules of 0050 already refuse
non-printable-ASCII text before the parser sees it; the parser does not rely on that.

### Accepted forms of `web.trusted_proxies` (guard: `TrustedProxyList.TryParse`)

The value is split at `,`; each entry is trimmed of ASCII spaces (U+0020 only) and is `address` or `address/prefix`. An IPv4 address is
dotted decimal with four parts 0–255 without leading zeros (the pattern of `IpJson.TryParseAddress`); an IPv6 address is any text with
a `:` that `IPAddress.TryParse` reads as IPv6, without `%` zone and without brackets. The prefix is 1 to 3 ASCII digits without leading
zero; without a prefix the entry is /32 or /128.

| Input | Result |
| ----- | ------ |
| empty text | accepted, no entries |
| `172.18.0.1`, `172.18.0.0/16`, `10.0.0.0/8`, `192.168.1.10/32` | accepted |
| ` 172.18.0.1 , 192.168.1.10 ` | accepted, two entries |
| `2001:db8::1`, `2001:db8::/32`, `fd00::/16`, `2001:DB8::1` | accepted |
| 16 entries | accepted |
| 17 entries | refused |
| `127.1`, `0x7f.0.0.1`, `0177.0.0.1`, `010.0.0.1`, `2130706433`, `1.2.3`, `1.2.3.4.`, `256.1.1.1` | refused |
| `172.18.0.0/33`, `10.0.0.0/7`, `0.0.0.0/0`, `172.18.0.0/016`, `172.18.0.0/+16`, `172.18.0.0/`, `172.18.0.0/ 16` | refused |
| `2001:db8::/15`, `2001:db8::/129` | refused |
| `172.18.0.1/16`, `2001:db8::1/32` (host bits set) | refused |
| `0.0.0.0`, `0.0.0.0/8`, `::`, `::/16` (unspecified address) | refused |
| `::ffff:172.18.0.1` (IPv4-mapped: write the IPv4 form) | refused |
| `fe80::1%eth0`, `fe80::1%2`, `[::1]`, `172.18.0.1:443`, `proxy.lan` | refused |
| `172.18.0.1,,10.0.0.1`, `172.18.0.1,`, `,` | refused (empty entry) |
| entries separated or surrounded by a tab (U+0009) or a no-break space (U+00A0); fullwidth digits | refused |

### Forwarded header values from a trusted peer (framework, not ours)

From `ForwardedHeadersMiddleware` 10.0.12: values of all header lines are comma-split; the rightmost one is used (`ForwardLimit` 1).
`X-Forwarded-For` goes through `IPEndPoint.TryParse` (inet_aton forms such as `127.1`, IPv6 with or without brackets and port, an
`IPv4:port`); an unparsable rightmost value (`unknown`, an obfuscated identifier) leaves the client as the peer. `X-Forwarded-Proto` is
applied when it consists of scheme characters; only `https` in any letter case makes the request HTTPS. `Forwarded` (RFC 7239),
`X-Forwarded-Host`, `X-Forwarded-Prefix` and `X-Real-IP` are never read. The lockout keys on the resulting `IPAddress`, so the textual
form of a value does not matter. A proxy that passes the client's own `X-Forwarded-For` through instead of appending its peer lets the
client choose its address; `docs/REVERSE_PROXY.md` requires appending or overwriting.

### Pipeline and services (`ServeCommand.Build` through `WebAuthentication`)

Order: `PortRoutingMiddleware` → `UseForwardedHeaders(options)` only when `ForwardedHeadersSetup.CreateOptions` returned options →
`SecurityHeadersMiddleware` → `UseAuthentication` → `UseAuthorization` → `UseAntiforgery` → `MapStaticAssets().AllowAnonymous()` →
`HealthEndpoint.Map` (now `.AllowAnonymous()` inside) → `WebAuthentication.MapLogout` (`POST /logout`, `AllowAnonymous`, metadata
`new RequireAntiforgeryTokenAttribute(true)` so `UseAntiforgery` validates the post and catches every form-reading failure; the handler
`LogoutAsync` answers `400` unless `IAntiforgeryValidationFeature` is present and valid — fail closed when the middleware did not run;
it never calls `IAntiforgery` itself) → `MapRazorComponents<App>().AddInteractiveServerRenderMode(…)` as today.

Services (`WebAuthentication.AddServices`): `AddDataProtection().UseEphemeralDataProtectionProvider()`; cookie authentication (cookie
`vandox_session`, `HttpOnly`, `SameSite=Lax`, `SecurePolicy=SameAsRequest`, `Path=/`, `IsEssential`; `ExpireTimeSpan` 12 h,
`SlidingExpiration` false, `SessionStore` the singleton `SessionStore`, `TimeProvider` the service clock, `Events.OnRedirectToLogin =
RedirectToLoginAsync`, `OnRedirectToAccessDenied` → 403); `AddAuthorization` with `FallbackPolicy` = authenticated user;
`AddCascadingAuthenticationState`; `AuthenticationStateProvider` → `SessionRevalidatingStateProvider` (scoped); `CircuitHandler` →
`SessionCircuitHandler` (scoped, `TryAddEnumerable`); antiforgery cookie `vandox_csrf` (`HttpOnly`, `SameSite=Strict`,
`SameAsRequest`, `Path=/`); `FormOptions.ValueLengthLimit` 16,384, `ValueCountLimit` 64, `MultipartBodyLengthLimit` 65,536 and
`MemoryBufferThreshold` 65,536 (set explicitly, so a later change of the framework default cannot let a part spill to `/tmp`); singletons `SessionStore`, `LoginThrottle`,
`LoginService`, `IPasswordVerifier` (the parsed `PasswordHash`). The clock is `hooks.Clock ?? TimeProvider.System`, already available in
`ServeCommand` through `ServeHooks`.

Start: `ServeCommand.RunAsync` checks the secret after the start-up log line and before `SqliteStore.OpenAsync`: not set →
`WebPasswordHashMissing`, exit 1; `PasswordHash.Parse` throws → `WebPasswordHashInvalid(reason)`, exit 1.

Sign-in (`LoginService.SignInAsync`): client key from `Connection.RemoteIpAddress`; wait for the one-check gate (`SemaphoreSlim(1,1)`).
The wait is prescribed exactly (Security B3): `using var timeout = new CancellationTokenSource(VerificationWait, timeProvider)`,
`using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken)`, `acquired = false`, then
`await gate.WaitAsync(linked.Token)`, `acquired = true`; `catch (OperationCanceledException) when` the caller's token is not cancelled →
`Throttled`, nothing recorded; when the caller's token is cancelled the exception propagates, nothing recorded; the release happens in
a `finally` only when `acquired`. No `Task.WaitAsync(timeout)` on the semaphore's task and no `WaitAsync(TimeSpan)` (real clock). Inside
the gate: locked (client or all) → `Throttled`, no verification; password `null`, longer than 1,024 characters → failure without
verification; else `Verify` (run synchronously inside the gate); failure → `RecordFailure`, log `login failed` and, when it started a
lock, `login locked` / `login locked for all clients`; when it started the client's lock and the request carries `X-Forwarded-For`
without `X-Original-For`, also `forwarded header ignored` (Devil's Advocate objection 1: the operator learns that a proxy's header was
ignored, so all clients behind it share one lock); success → `RecordSuccess`, principal with `ClaimTypes.Name` `admin` and
`vandox:session` = `SessionStore.NewSessionId()`, `SignInAsync` with `IsPersistent = false`, log `login succeeded`. The check of the lock
**inside** the gate makes "check, verify, record" atomic, so concurrent attempts of one client cannot exceed five checks.

Login page (`Components/Account/Login.razor`): `@page "/login"`, `[AllowAnonymous]`, `[ExcludeFromInteractiveRouting]`,
`[CascadingParameter] HttpContext`, `[SupplyParameterFromForm(Name = "password")]`, `<form method="post" @formname="login"
@onsubmit=…>` with `<AntiforgeryToken />`; signed-in GET → redirect `/`; outcomes → `302` `/` (`NavigationManager.NavigateTo("/")` in
static rendering), `401` / `429` set on `HttpContext.Response.StatusCode` with the fixed message. `App.razor` renders
`<Routes @rendermode="…">` and `<HeadOutlet>` with `InteractiveServer` only when `HttpContext.AcceptsInteractiveRouting()`, and the
framework script only then. `Routes.razor` uses `AuthorizeRouteView` with `<NotAuthorized><RedirectToLogin /></NotAuthorized>`.
`Components/Pages/_Imports.razor` adds `@attribute [Authorize]` for every page. `MainLayout.razor` gets the sign-out form inside
`<AuthorizeView>`. New markup obeys the rules of 0099 (no `style` attribute, no inline script; styles go to `app.css`).

## Options considered

The weighed options and the reasons are in records 0103 (session store, keys, login page form handling, route protection, circuits,
`Origin` check, return address), 0104 (Argon2 library, accepted hashes, how the hash is created) and 0105 (trusted proxies, option form,
failed-sign-in limits). Plan-level choices besides:

1. **Missing hash: the whole service refuses to start** (chosen, the issue says so and the configuration area already promises it)
   against **serving only `/healthz` without a hash** — a container that looks healthy but has no usable UI hides the mistake.
2. **The password check runs synchronously inside the one-check gate** (chosen) against **`Task.Run`** — one thread blocked at most,
   no unbounded queue of hashing work.
3. **Integration tests through the real service (`BackendFixture`, `HttpClient` with a cookie container)** (chosen, as `ServeTests`
   already does) against **`Microsoft.AspNetCore.TestHost`** — a new test dependency, and it bypasses Kestrel's connection labels, so
   the port routing would not be exercised.
4. **The logout endpoint carries antiforgery metadata and reads the middleware's result** (chosen, Security B1) against **calling
   `IAntiforgery.IsRequestValidAsync` in the handler and catching `AntiforgeryValidationException`** — the second works too, but
   keeps a second validation path next to the one the pages use, and the next failure type the framework adds would surface as `500`.
5. **The gate wait through a `CancellationTokenSource` on the injected clock** (chosen, Security B3) against
   **`SemaphoreSlim.WaitAsync(TimeSpan)`** (real clock, untestable without waiting) and **`Task.WaitAsync(timeout, timeProvider)`**
   (abandons a queued wait that later takes the semaphore for good).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `Directory.Packages.props` | — | `Konscious.Security.Cryptography.Argon2` `1.3.1` |
| `Vandox.Backend` | `Vandox.Backend.csproj` | `PackageReference` `Konscious.Security.Cryptography.Argon2` (no version) |
| `Vandox.Core` | `Configuration/WebConfig.cs` (new) | `web` section: `listen`, `trusted_proxies` |
| `Vandox.Core` | `Configuration/TrustedProxyList.cs` (new) | guard of the option |
| `Vandox.Core` | `Configuration/BackendConfig.cs` | `Web` becomes `WebConfig`; `Keys()` lists `web.trusted_proxies` second |
| `Vandox.Core` | `Configuration/BackendConfigLoader.cs` | validates `web.trusted_proxies` after `web.listen` |
| `Vandox.Backend` | `Security/IPasswordVerifier.cs`, `PasswordHash.cs`, `PasswordHashException.cs` (new) | hash parsing and checking |
| `Vandox.Backend` | `Security/LoginThrottle.cs`, `LoginFailure.cs`, `LoginService.cs`, `LoginOutcome.cs` (new) | sign-in and limits |
| `Vandox.Backend` | `Security/SessionStore.cs`, `SessionCircuitHandler.cs`, `SessionEndedException.cs`, `SessionRevalidatingStateProvider.cs` (new) | sessions |
| `Vandox.Backend` | `Security/ForwardedHeadersSetup.cs`, `WebAuthentication.cs` (new) | proxy trust, wiring, logout, redirect event |
| `Vandox.Backend` | `Cli/HashPasswordCommand.cs` (new), `Cli/CommandLine.cs`, `Cli/CommandOptions.cs` | sub-command `hash-password` |
| `Vandox.Backend` | `Hosting/BackendApp.cs`, `Hosting/ServeHooks.cs` | route `hash-password`; `Input` hook |
| `Vandox.Backend` | `Hosting/ServeCommand.cs` | hash check before the database; `Build` wires `WebAuthentication` and forwarded headers |
| `Vandox.Backend` | `Hosting/HealthEndpoint.cs` | `AllowAnonymous` |
| `Vandox.Backend` | `Hosting/BackendLog.cs` | eight log methods (below) |
| `Vandox.Backend` | `Components/App.razor`, `Routes.razor`, `_Imports.razor`, `Layout/MainLayout.razor` | render mode per page, `AuthorizeRouteView`, sign-out form |
| `Vandox.Backend` | `Components/Pages/_Imports.razor` (new), `Components/Account/Login.razor` (+ `.razor.cs` if code-behind), `Components/Account/RedirectToLogin.razor` (new) | login and redirect |
| `Vandox.Backend` | `wwwroot/app.css` | login form and sign-out styles |
| deploy | `deploy/backend/docker-compose.yml`, `deploy/backend/vandoxd.yaml` | hash secret; `trusted_proxies` |
| CI | `.github/scripts/smoke-test-backend.sh` | hash via `hash-password`, login checks |

Existing files the skeleton rewrites and that were read and are final apart from the listed change: `ServeCommand.cs`, `BackendApp.cs`,
`ServeHooks.cs`, `HealthEndpoint.cs`, `BackendConfig.cs`, `BackendConfigLoader.cs`, `CommandLine.cs`, `CommandOptions.cs`.

## Signatures (for the Dev's skeleton)

C# skeleton as `.squad/stack.md` describes (`#pragma warning disable RH2003, S2325`, `throw new NotImplementedException()`), every member
with XML documentation, `#region` blocks.

```csharp
// src/Vandox.Core/Configuration/WebConfig.cs
public sealed class WebConfig
{
    [ConfigKey("listen")] public string Listen { get; set; } = ":8080";
    [ConfigKey("trusted_proxies")] public string TrustedProxies { get; set; } = string.Empty;
    /// <exception cref="InvalidOperationException">The option does not parse</exception>
    public IReadOnlyList<System.Net.IPNetwork> GetTrustedProxies();
}

// src/Vandox.Core/Configuration/TrustedProxyList.cs
public static class TrustedProxyList
{
    public const int MaxEntries = 16;
    public static bool TryParse(string text, out IReadOnlyList<System.Net.IPNetwork> networks);
}

// BackendConfig: public WebConfig Web { get; } = new();   (ListenerConfig stays for Ingest)

// src/Vandox.Backend/Security/IPasswordVerifier.cs
internal interface IPasswordVerifier
{
    bool Verify(string password);
}

// src/Vandox.Backend/Security/PasswordHash.cs
internal sealed class PasswordHash : IPasswordVerifier
{
    internal const int MinMemoryKiB = 19456, MaxMemoryKiB = 262144, MinIterations = 2, MaxIterations = 10, MinParallelism = 1, MaxParallelism = 8;
    internal const int MinSaltBytes = 8, MaxSaltBytes = 64, MinHashBytes = 16, MaxHashBytes = 64, MaxPasswordLength = 1024;
    internal const int DefaultMemoryKiB = 65536, DefaultIterations = 3, DefaultParallelism = 4, DefaultSaltBytes = 16, DefaultHashBytes = 32;
    internal const string ReasonForm = "must be an argon2id hash in the form $argon2id$v=19$m=<memory>,t=<iterations>,p=<parallelism>$<salt>$<hash>";
    internal const string ReasonVersion = "version must be 19";
    internal const string ReasonMemory = "memory must be 19456 to 262144 KiB";
    internal const string ReasonIterations = "iterations must be 2 to 10";
    internal const string ReasonParallelism = "parallelism must be 1 to 8";
    internal const string ReasonSalt = "salt must be 8 to 64 bytes";
    internal const string ReasonHash = "hash must be 16 to 64 bytes";
    internal int MemoryKiB { get; }
    internal int Iterations { get; }
    internal int Parallelism { get; }
    internal int SaltLength { get; }
    internal int HashLength { get; }
    /// <exception cref="PasswordHashException">The text is not an accepted hash; the message is one of the Reason texts</exception>
    internal static PasswordHash Parse(string text);
    internal static string Create(string password);
    internal static string Create(string password, ReadOnlySpan<byte> salt, int memoryKiB, int iterations, int parallelism, int hashBytes); // no bounds check (tests, hash-password defaults)
    public bool Verify(string password);
}

// src/Vandox.Backend/Security/PasswordHashException.cs  (public: Sonar S3871)
public sealed class PasswordHashException : Exception
{
    public PasswordHashException();
    public PasswordHashException(string message);
    public PasswordHashException(string message, Exception innerException);
}

// src/Vandox.Backend/Security/LoginFailure.cs
internal readonly record struct LoginFailure(int Failures, DateTimeOffset? ClientLockedUntil, DateTimeOffset? AllLockedUntil);

// src/Vandox.Backend/Security/LoginThrottle.cs
internal sealed class LoginThrottle
{
    internal const int MaxFailures = 5, MaxGlobalFailures = 100, MaxClients = 4096;
    internal const string UnknownClient = "unknown";
    internal static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan GlobalWindow = TimeSpan.FromHours(1);
    internal LoginThrottle(TimeProvider timeProvider);
    internal int TrackedClients { get; }
    internal static string ClientKey(System.Net.IPAddress? address);
    internal bool IsLockedOut(string clientKey);
    internal LoginFailure RecordFailure(string clientKey);
    internal void RecordSuccess(string clientKey);
}

// src/Vandox.Backend/Security/LoginOutcome.cs
internal enum LoginOutcome { SignedIn, WrongPassword, Throttled }

// src/Vandox.Backend/Security/LoginService.cs
internal sealed class LoginService
{
    internal static readonly TimeSpan VerificationWait = TimeSpan.FromSeconds(5);
    public LoginService(IPasswordVerifier verifier, LoginThrottle throttle, TimeProvider timeProvider, ILogger logger);
    internal Task<LoginOutcome> SignInAsync(HttpContext context, string? password, CancellationToken cancellationToken); // OperationCanceledException only when cancellationToken is cancelled
    internal Task SignOutAsync(HttpContext context);
}

// src/Vandox.Backend/Security/SessionStore.cs
internal sealed class SessionStore : ITicketStore
{
    internal const string SessionClaimType = "vandox:session";
    internal const int MaxSessions = 16;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);
    internal static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);
    public SessionStore(TimeProvider timeProvider);
    internal int Count { get; }
    internal static string NewSessionId();                       // 32 random bytes, base64url without padding (43 characters)
    public Task<string> StoreAsync(AuthenticationTicket ticket); // key = the ticket's vandox:session claim
    public Task RenewAsync(string key, AuthenticationTicket ticket);
    public Task<AuthenticationTicket?> RetrieveAsync(string key); // null when unknown or ended; extends the idle time
    public Task RemoveAsync(string key);
    internal bool Touch(ClaimsPrincipal user);                    // true when active; extends the idle time
    internal bool IsActive(ClaimsPrincipal user);                 // true when active; does not extend
}

// src/Vandox.Backend/Security/SessionEndedException.cs (public: Sonar S3871; the three standard constructors)
public sealed class SessionEndedException : Exception { … }

// src/Vandox.Backend/Security/SessionCircuitHandler.cs
internal sealed class SessionCircuitHandler : CircuitHandler
{
    public SessionCircuitHandler(AuthenticationStateProvider authenticationStateProvider, SessionStore sessions);
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next);
    internal Task CheckSessionAsync();                            // throws SessionEndedException
}

// src/Vandox.Backend/Security/SessionRevalidatingStateProvider.cs
internal sealed class SessionRevalidatingStateProvider : RevalidatingServerAuthenticationStateProvider
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public SessionRevalidatingStateProvider(ILoggerFactory loggerFactory, SessionStore sessions);
    protected override TimeSpan RevalidationInterval { get; }
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken);
    internal bool IsValid(AuthenticationState authenticationState);
}

// src/Vandox.Backend/Security/ForwardedHeadersSetup.cs
internal static class ForwardedHeadersSetup
{
    internal static ForwardedHeadersOptions? CreateOptions(IReadOnlyList<System.Net.IPNetwork> trustedProxies);
}

// src/Vandox.Backend/Security/WebAuthentication.cs
internal static class WebAuthentication
{
    internal const string CookieName = "vandox_session";
    internal const string AntiforgeryCookieName = "vandox_csrf";
    internal const string LoginPath = "/login";
    internal const string LogoutPath = "/logout";
    internal const int MaxFormValueLength = 16384;
    internal const int MaxFormValueCount = 64;
    internal const int MaxMultipartBodyLength = 65536;          // also the MemoryBufferThreshold
    internal static void AddServices(IServiceCollection services, IPasswordVerifier verifier, TimeProvider timeProvider, ILoggerFactory loggerFactory);
    internal static void MapLogout(IEndpointRouteBuilder endpoints);
    internal static Task LogoutAsync(HttpContext context, LoginService loginService); // 400 unless the antiforgery feature is valid; else sign out, 303 /login
    internal static Task RedirectToLoginAsync(RedirectContext<CookieAuthenticationOptions> context);
}

// src/Vandox.Backend/Cli/HashPasswordCommand.cs
internal static class HashPasswordCommand
{
    internal const int MinPasswordLength = 12;
    internal const string RefusedText = "vandoxd hash-password: the password must have 12 to 1024 characters and no control characters";
    internal static Task<int> RunAsync(TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken);
}

// CommandOptions: public bool HashPassword { get; set; }
// CommandLine: private const string HashPasswordName = "hash-password"; Usage("hash-password") returns
//   "Usage of vandoxd hash-password:\n  reads the password from the first line of standard input and prints its argon2id hash\n"
// ServeHooks: public TextReader? Input { get; set; }   // standard input of hash-password; Console.In when null

// BackendLog (EventIds: the next free numbers):
//   [Error]       "web password hash missing {Variable}"      WebPasswordHashMissing(this ILogger logger, string variable)
//   [Error]       "web password hash invalid {Reason}"        WebPasswordHashInvalid(this ILogger logger, string reason)
//   [Information] "login succeeded {Client}"                  LoginSucceeded(this ILogger logger, string client)
//   [Warning]     "login failed {Client} {Failures}"          LoginFailed(this ILogger logger, string client, int failures)
//   [Warning]     "login locked {Client} {Until}"             LoginLocked(this ILogger logger, string client, string until)
//   [Warning]     "login locked for all clients {Until}"      LoginLockedForAll(this ILogger logger, string until)
//   [Information] "logged out {Client}"                       LoggedOut(this ILogger logger, string client)
//   [Warning]     "forwarded header ignored {Client}"         ForwardedHeaderIgnored(this ILogger logger, string client)
// `client` is the client address text (IPAddress.ToString(), "unknown" without address); `until` is UTC RFC 3339.
```

Razor: `Components/Account/Login.razor` (route `/login`), `Components/Account/RedirectToLogin.razor` (no parameters; navigates in
`OnInitialized`), `Components/Pages/_Imports.razor` (`@using Microsoft.AspNetCore.Authorization`, `@attribute [Authorize]`).

## Test files

New (each with its own test class; namespace `<Project>.Tests`, flat in the project like the existing ones):

- `tests/Vandox.Backend.Tests/PasswordHashTests.cs`
- `tests/Vandox.Backend.Tests/LoginThrottleTests.cs`
- `tests/Vandox.Backend.Tests/LoginServiceTests.cs`
- `tests/Vandox.Backend.Tests/SessionStoreTests.cs`
- `tests/Vandox.Backend.Tests/SessionCircuitHandlerTests.cs`
- `tests/Vandox.Backend.Tests/SessionRevalidatingStateProviderTests.cs`
- `tests/Vandox.Backend.Tests/ForwardedHeadersSetupTests.cs`
- `tests/Vandox.Backend.Tests/WebAuthenticationTests.cs`
- `tests/Vandox.Backend.Tests/HashPasswordCommandTests.cs`
- `tests/Vandox.Backend.Tests/LoginTests.cs` (the `Login` component, through the real service)
- `tests/Vandox.Backend.Tests/RedirectToLoginTests.cs`
- `tests/Vandox.Backend.Tests/RoutesTests.cs`
- `tests/Vandox.Core.Tests/TrustedProxyListTests.cs`
- `tests/Vandox.Core.Tests/WebConfigTests.cs`

Extended: `tests/Vandox.Backend.Tests/ServeTests.cs`, `CommandLineTests.cs`, `MainLayoutTests.cs`;
`tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs`.

New test helpers (internal, `tests/Vandox.Backend.Tests`): `LoginTestClient.cs` (an `HttpClient` with a `CookieContainer` and no
automatic redirects; reads the antiforgery token and `_handler` from `GET /login`, posts the login form with optional extra headers,
posts `/logout` with the token of the signed-in page), `ScriptedPasswordVerifier.cs` (`IPasswordVerifier`: scripted result, counts
calls, can block until released, records overlapping calls), `FixedAuthenticationStateProvider.cs` (`AuthenticationStateProvider`
returning a given principal).

Real sample of the external format — `testdata/argon2/reference-argon2id.txt`, created by the Tester with exactly these lines (the eight
argon2id vectors of the reference implementation's test suite, `P-H-C/phc-winner-argon2` commit
`f57e61e19229e23c4445b85494dbf7c07de721cb`, `src/test.c` lines 233–264, version 19; read in this plan from that commit). Variants:
memory 256 KiB to 256 MiB, iterations 1 to 4, parallelism 1 and 2, two passwords, two salts. Fields separated by one space:

```
# argon2id vectors of the Argon2 reference implementation: P-H-C/phc-winner-argon2 commit f57e61e19229e23c4445b85494dbf7c07de721cb, src/test.c (version 19).
# Fields: password, salt (also encoded in the hash, listed for reading), encoded hash. Lines 3 to 5 are outside the accepted bounds.
password somesalt $argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc
password somesalt $argon2id$v=19$m=262144,t=2,p=1$c29tZXNhbHQ$eP4eyR+zqlZX1y5xCFTkw9m5GYx0L5YWwvCFvtlbLow
password somesalt $argon2id$v=19$m=256,t=2,p=1$c29tZXNhbHQ$nf65EOgLrQMR/uIPnA4rEsF5h7TKyQwu9U1bMCHGi/4
password somesalt $argon2id$v=19$m=256,t=2,p=2$c29tZXNhbHQ$bQk8UB/VmZZF4Oo79iDXuL5/0ttZwg2f/5U52iv1cDc
password somesalt $argon2id$v=19$m=65536,t=1,p=1$c29tZXNhbHQ$9qWtwbpyPd3vm1rB1GThgPzZ3/ydHL92zKL+15XZypg
password somesalt $argon2id$v=19$m=65536,t=4,p=1$c29tZXNhbHQ$kCXUjmjvc5XMqQedpMTsOv+zyJEf5PhtGiUghW9jFyw
differentpassword somesalt $argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$C4TWUs9rDEvq7w3+J4umqA32aWKB1+DSiRuBfYxFj94
password diffsalt $argon2id$v=19$m=65536,t=2,p=1$ZGlmZnNhbHQ$vfMrBczELrFdWP0ZsfhWsRPaHppYdP3MVEMIVlqoFBw
```

("Lines 3 to 5" counts the vector lines: the `m=256` pair and `t=1` are refused.) The 256 MiB vector makes one test allocate 256 MiB;
it stays, because it pins the upper bound against the reference.

Existing test code affected and who adapts it (step 5, Tester — behavior changes, not signatures, except where noted):

- `BackendFixture.RunAsync` callers that start the service (`ServeTests`, 6 call sites): the service now needs the hash. The Tester
  gives `BackendFixture` a test password and its hash (computed once with `PasswordHash.Create` at the lowest accepted cost, memory
  19,456 KiB, 2 iterations, parallelism 1) and passes `VANDOX_WEB_PASSWORD_HASH` when the caller gives no environment. `ImportCommandTests`,
  `IncidentsCommandTests`, `ReportCommandTests`, `TemplatesCommandTests` then receive that variable too, which they ignore (known variable).
- `ServeTests.ServeRunsServiceOnConfiguredPortsAndStopsCleanly` (anonymous `/` now `302`, `/nothing-here` now `302`),
  `ServeServesPageWithLocalResourcesAndNoInlineCode` (sign in first), `ServeServesWebAssetsOnlyOnWebPort` (anonymous negotiate `401`,
  signed in `200`), `ServeSetsSecurityHeadersOnWebResponses` (add `/login` to the paths; the client must not follow redirects so the
  `302` answers are checked too).
- `MainLayoutTests.MainLayoutRendersHeaderNavigationAndThemeSwitch`: `AuthorizeView` and `AntiforgeryToken` need a cascading
  authentication state and the antiforgery state provider in the renderer's services.
- `BackendConfigLoaderTests.BackendConfigKeysListErrorLogPathsBeforeTimeZone`: the expected sequence gains `web.trusted_proxies` after
  `web.listen`.
- `BackendConfig.Web` changes type (`ListenerConfig` → `WebConfig`); no test names the type, `config.Web.Listen` still compiles.

Files of the change no unit test can cover: none. (The smoke script is CI, not production code; `Program.cs` is unchanged.)

## Areas

- **New area `Web login`** (`docs/areas/web-login.md`, Lead decision; index row added): scope and rules written with this plan.
- **Web UI** (`web-ui.md`): the login page is the one page without a live connection; the framework script only on interactive pages;
  the sign-out button in the header; *Limits and errors* — the sentence "Until the login exists (#25) …" replaced by a link to Web login
  (the live connection now needs a session); *Not here* and the scope sentence point to Web login.
- **Backend host** (`backend-host.md`): *Command line* — the sub-command `hash-password` and that the service refuses to start without
  the hash (exit 1); *Listeners and routing* — the login applies after the port routing; *Health* — `/healthz` stays outside the login
  (now implemented); *Compose file* — the hash secret; *Exposure and trust* — forwarded headers and secure cookies implemented, link to
  Web login.
- **Configuration and secrets** (`configuration-and-secrets.md`): option row `web.trusted_proxies` (default empty) with its format
  rule and error text; the hash's form is checked by the service (link to Web login).
- **Network and security model** (`network-and-security-model.md`): *Web UI* links Web login for the login and the proxy trust.

## Documentation updates

| Document | Change | Owner |
| -------- | ------ | ----- |
| `docs/areas/web-login.md`, `docs/areas/README.md` (index rows) | written in this step | Lead (done) |
| `docs/decisions/0103`–`0105`, `docs/decisions/README.md` | written in this step, `Proposed` | Lead (done) |
| `docs/areas/web-ui.md`, `backend-host.md`, `configuration-and-secrets.md`, `network-and-security-model.md` | as *Areas* says | Dev |
| `docs/areas/web-login.md` | only a deviation the build forces, reported in the Dev's report | Dev (step 6) |
| `docs/REVERSE_PROXY.md` (new) | operator guide: what the proxy must do (TLS, pass `Host`, append/overwrite `X-Forwarded-For`, set `X-Forwarded-Proto`, WebSocket upgrade for `/_blazor`, no buffering of it), a generic nginx block and a Caddyfile, Synology DSM 7 (*Login Portal → Advanced → Reverse Proxy*, *Custom Header → Create → WebSocket*, certificate under *Security → Certificate*), QNAP QTS 5 (*Network & File Services → Network Access → Reverse Proxy*, certificate under *Security → SSL Certificate & Private Key*), the vendor steps marked as written from the vendors' documentation and not verified on a device; how to find the address for `web.trusted_proxies` (`docker network inspect vandox_default` gateway for a proxy on the same host; the proxy host's LAN address otherwise) and what trusting it means; that without the option all clients behind the proxy share the proxy's address, so five wrong passwords
from any of them lock everyone behind it out for 15 minutes, and that the log line `forwarded header ignored` points to that (Devil's
Advocate 1); that the overall lock of 100 failures per hour can be provoked by any client that reaches the login and is cleared by
restarting the container (Devil's Advocate 2); a recommendation to give the UI its own host name, not shared with other web
applications on the same host name or IP address (another port is the same site, and its pages receive the `SameSite` cookies,
Security N1); a check list after setup; the Docker Engine check (from a second LAN host, route the container network via the NAS and `curl` the container address and `127.0.0.1`-published port, expected: refused; Engine ≥ 28.3.3 recommended) | Dev |
| `README.md` | backend options row `web.trusted_proxies`; secrets row of `VANDOX_WEB_PASSWORD_HASH` (argon2id PHC form, link to Web login, `hash-password`); *Run the backend with Docker Compose*: create `secrets/vandox_web_password_hash` with `read -rs` + a pipe into `docker run --rm -i networlddev/vandox hash-password`, with the warning not to add `-t` (a terminal echoes what is typed; Security N4), owner 65532 and mode 0400, then the proxy guide; the sub-command `hash-password` | Dev |
| `SECURITY.md` | *Deployment Security Considerations*: the hash secret file's owner and mode, set `web.trusted_proxies` only to the proxy's address, and without it all clients behind the proxy share one lockout; the overall lock (100 failures per hour) can be provoked from the LAN and is cleared by a restart; Docker Engine ≥ 28.3.3 | Dev |
| `docs/ARCHITECTURE.md` | *Network* and *Security model*: the login with server-side sessions, forwarded headers only from `web.trusted_proxies`, records 0103–0105 linked; the introduction's list of what exists gains the login | Dev |
| `.squad/project.md` security areas 3 and 8 | area 3 implemented: name `PasswordHash`, `LoginService`, `LoginThrottle`, `SessionStore`, `SessionCircuitHandler`, `SessionRevalidatingStateProvider`, `ForwardedHeadersSetup`, `WebAuthentication`, `TrustedProxyList`, `Login.razor`, the dependency Konscious, records 0103–0105; replace "the UI port is reachable only by that proxy" by "the UI port is published for that proxy only, as defense in depth (0060)"; replace "travel only in `HttpOnly`, `Secure`, `SameSite` cookies" by "travel only in `HttpOnly`, `SameSite` cookies that are `Secure` whenever the request reached the proxy over HTTPS" (Security N2: the issue makes `Secure` depend on the proxy's TLS); area 8 names the hash check (`PasswordHash`, constant-time comparison) | Dev |
| `.squad/project.md` *Test doubles* | Blazor/web row: `LoginTestClient`, `ScriptedPasswordVerifier`, `FixedAuthenticationStateProvider` (implemented) | Tester |
| `deploy/backend/docker-compose.yml` | `VANDOX_WEB_PASSWORD_HASH_FILE: /run/secrets/vandox_web_password_hash`, the secret uncommented, header comment explains the file | Dev |
| `deploy/backend/vandoxd.yaml` | `web.trusted_proxies: ""` with a comment (format, default, what it trusts, link to the guide); the secrets comment names the argon2id form and `hash-password` | Dev |

## Architecture check

- Guarantees in `.squad/project.md` (no data gaps, a hanging collector never blocks the agent, backfill never alerts) are not touched.
- `docs/ARCHITECTURE.md` already says the web UI requires a login and runs behind a reverse proxy; this implements it. The new
  elements (server-side sessions, `web.trusted_proxies`) are described there by the Dev with links to 0103–0105.
- Record 0081 (labelled listeners, fail closed): the port routing stays first, so the ingest listener answers `404` before any
  authentication. Record 0099 (one CSP): the login form posts to the own origin (`form-action 'self'`), the login page loads no other
  source; `SecurityHeadersMiddleware` runs before authentication, so redirects and `401` answers carry the headers too. Record 0059:
  `/healthz` stays anonymous. Record 0060: unchanged ports; the compose file gains the second secret it already prepared. Record 0050:
  the hash keeps the generic secret rules; the service checks the form on top. Record 0032: the hash never reaches the configuration
  file.

## Security considerations

- [x] Limits and whether before or after decoding: the password limit of 1,024 characters applies to the decoded form value (after
  URL decoding), and `hash-password` reads at most 1,026 characters of its first line (decoded text from the reader); the form reader's
  limits (16 KiB per value, 64 values, 64 KiB for a whole multipart body, never buffered to a file) apply as the framework's form
  reader measures them, inside the antiforgery middleware, before any of our code; the hash is at most
  4,096 bytes of printable ASCII by the secret rules before `PasswordHash.Parse`; `web.trusted_proxies` is bounded by the 1 MiB file and
  16 entries; the session table (16), client table (4,096) and global failure queue (100) are counts of entries.
- [x] Exceptions that can reach a user: none. The login page shows only its two fixed messages; `PasswordHashException` reaches the
  log as one of the seven fixed reasons; `SessionEndedException` ends the circuit, and the browser receives the framework's generic
  circuit error text (`DetailedErrors` is off outside Development), never our message; a refused form post is the framework's plain
  `400` (pages) or the logout handler's `400`, also for a malformed, oversized or broken form body, because the antiforgery middleware
  catches every form-reading failure for both (no `500`, Security B1). The framework logs the ended circuit at error level as an unhandled circuit exception; the JSON logger writes only the
  exception type name.
- Secrets: the hash is held as `Secret` until parsed, never logged; the password is never logged, echoed or stored; the computed hash is
  compared with `CryptographicOperations.FixedTimeEquals`.
- New dependency: `Konscious.Security.Cryptography.Argon2` 1.3.1 (MIT, managed) and its `Konscious.Security.Cryptography.Blake2` 1.1.1;
  its `net8.0` dependency group also names `System.Memory` 4.5.4, which has no assembly for `net10.0`. Security reviews the pin
  (`Directory.Packages.props`, transitive pinning on).

## Security review

Plan review (squad-security, CHANGES_REQUIRED), each finding answered:

- **B1 (blocking) — logout answers `500` to a malformed or oversized form.** Accepted; confirmed in the 10.0.12 sources
  (`DefaultAntiforgery.IsRequestValidAsync` line 104 without a catch, `DefaultAntiforgeryTokenStore` rethrowing as
  `AntiforgeryValidationException`, caught only in `AntiforgeryMiddleware.InvokeAwaited`). Revised: the logout endpoint carries
  `RequireAntiforgeryTokenAttribute(true)`, so the middleware validates and catches, and the handler reads only
  `IAntiforgeryValidationFeature` (missing → `400`, fail closed). Chosen over a `try/catch` around `IsRequestValidAsync` because it
  is the path the page endpoints already take, so one mechanism decides every form post. *Framework facts*, *Pipeline*, signature
  `LogoutAsync`, AC6.1 (20 KiB value, 65 values on `/logout`), AC6.3 (metadata and feature check), area document *Cross-site requests*.
- **B2 (blocking) — multipart file sections escape the form limits.** Accepted; `MultipartBodyLengthLimit` 65,536 and
  `MemoryBufferThreshold` 65,536 set explicitly (constant `MaxMultipartBodyLength`), listed in *Services*, *Security considerations* and
  the area document. AC6.1 (1 MiB file part to `/login` and `/logout`), AC6.2 (the options). The compose file's `/tmp` tmpfs stays
  unsized: with the limit nothing reaches it from a form, and sizing it is SQLite's concern (0060).
- **B3 (blocking) — an abandoned gate wait leaks the semaphore.** Accepted; the wait is prescribed exactly in *Sign-in*
  (`CancellationTokenSource(VerificationWait, timeProvider)` linked with the request token, release only after an acquire; caller
  cancellation propagates as `OperationCanceledException`, nothing recorded). AC7.7a pins that the gate is usable after a timed-out and
  after a cancelled waiter.
- **N1 — `SameSite=Lax` does not keep same-site pages off the live connection.** Accepted; the area document now names the protection
  that holds (the live connection renders only components named in protected descriptors of the signed-in page, which another origin
  cannot read; the antiforgery token for posts likewise) and says that `SameSite` only keeps *other sites* out; record 0103 reworded;
  `docs/REVERSE_PROXY.md` recommends a host name of its own (Dev, *Documentation updates*).
- **N2 — `.squad/project.md` area 3 says the cookies are always `Secure`.** Accepted; the goal sentence is restated to the issue's rule
  (`Secure` whenever the request reached the proxy over HTTPS), in *Documentation updates* and tasks row 18. This aligns the goal with
  what the issue's owner asked for; no guarantee of `docs/ARCHITECTURE.md` changes.
- **N3 — multi-lane path only checked against itself.** Accepted; AC2.4 reproduces the reference vector `m=256,t=2,p=2` through the
  explicit `Create` overload, which checks no bounds (signature comment). Should Konscious not reproduce it, that is a finding that
  stops the pipeline (escalation), not a test to relax.
- **N4 — "never echoes the password".** Accepted; the area document now says that the command prints nothing but the hash and does
  not switch off terminal echo, and gives the `read -rs` plus pipe recipe without `-t`; README row carries the warning.
- **N5 — "nothing it sent is processed" too broad.** Accepted; the area document now says no event, interop call or navigation it sends
  is processed after the session ended, and names the exception (data of a server-to-client transfer that had already started, which
  no page uses).
- Dependency pin: no objection; unchanged.

## Challenge

Devil's Advocate (0 major, 2 minor), each objection answered:

1. **Shared client address by default** — accepted. Revised: `docs/REVERSE_PROXY.md` and `SECURITY.md` say plainly that without
   `web.trusted_proxies` every client behind the proxy shares the proxy's address and its lockout (Dev, *Documentation updates*); the
   area document says it under *Failed sign-ins*; `LoginService` logs `forwarded header ignored` (`client`) when a client lock starts on
   a request whose `X-Forwarded-For` was not applied (new `BackendLog.ForwardedHeaderIgnored`, AC7.9, service check in AC5.2). The log
   is tied to the start of a lock, not to every request, so a client cannot flood the log with it (one line per five failures at most).
   A start-up warning was weighed and dropped: without a request the service cannot know that a proxy sits in front of it.
2. **Global lockout can be provoked** — accepted. The trade-off and the recovery path (a restart of the container clears the in-memory
   counts; the per-client lock ends after 15 minutes, the overall lock at the latest one hour after its oldest failure) are written in
   the area document (*Failed sign-ins*), record 0105 (*Consequences*), `docs/REVERSE_PROXY.md` and `SECURITY.md`. The design is kept:
   the cap is the only bound on guesses from a client that can change its address (0105); the 5-second gate wait is part of the same
   trade-off and is named with it.

## Deviations accepted at approval

Lead, step 9 (approve-pr), on 0c52aa3:

- `LoginClient`, `StoredSession`, `RequiredAntiforgery` as top-level internal types and `App.razor.cs` code-behind (RH2101/RH2109):
  structure only, accepted.
- `RequiredAntiforgery : IAntiforgeryMetadata` (`RequiresValidation` true) on `POST /logout` instead of
  `RequireAntiforgeryTokenAttribute(true)`: the same metadata the middleware reads (Security confirmed), accepted; record 0103 names the
  mechanism (endpoint metadata), not the type.
- `TrustedProxyList` refuses prefixes shorter than /8 (IPv4) and /16 (IPv6): written in record 0105 and
  `configuration-and-secrets.md`, consistent with the guard table above (`/7` and `/15` refused), accepted.
- Antiforgery `HeaderName = null` (review round 1): the token is read only from the form field, so the middleware always reads the body
  as a form and catches its failures (no `500`). The behavior is in `web-login.md` (*Cross-site requests*); it applies the mechanism
  record 0103 already gives the reason for, so the record is not extended.
- `logged out` only when a session existed (review round 1, non-blocking): fixed now with a test and covered by the round-2 delta
  review, confirmed.
- Smoke script reads the CSP header from `/login` (an anonymous `/` is now a redirect); `.top-actions` header wrapper;
  `LoginService : IDisposable` (disposes its gate; the singleton is disposed by the container); `AddAuthorizationBuilder()
  .SetFallbackPolicy` (ASP0025, same policy): accepted.
- `web-login.md` does not repeat the prefix bounds: the option format is written once in `configuration-and-secrets.md`, which
  `web-login.md` links; no change.

## Decision records

- `docs/decisions/0103-web-login-server-side-sessions-static-login-page-circuits-bound-to-session.md` (Proposed)
- `docs/decisions/0104-argon2id-password-hash-konscious-strict-phc-form-hash-password-command.md` (Proposed)
- `docs/decisions/0105-forwarded-headers-only-from-trusted-proxies-failed-logins-locked-per-client-and-capped.md` (Proposed)

## Out of scope / follow-ups

- Out of scope as in `spec.md` (multiple users, 2FA, remember-me, `Host` allow-list, `Origin` check, sessions across restarts).
- Follow-up issue (the orchestrator opens it): **"[Security] Verify loopback-published ports on the NAS Docker engines and record the
  result"** — body: "#25 needs the Docker Engine behavior on the supported NAS devices. Run the check in `docs/REVERSE_PROXY.md`
  (*Check the Docker Engine*) on Synology Container Manager and QNAP Container Station, note the engine versions (`docker version`) and
  whether a second LAN host reaches the web port published on 127.0.0.1 or the container address, and amend record 0105 with the
  result. The login does not depend on the answer; the record and `SECURITY.md` advice do." Labels: `area: security`, `type: security`.
