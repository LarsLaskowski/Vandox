# Tasks: Web UI login and operation behind a reverse proxy

Plan: [plan.md](plan.md) — Status: Draft

Every documentation edit appears in exactly one row; the Lead's approval only touches status, index rows and bookkeeping.

| # | Task | Files | Tests (AC) | Owner | Done |
| - | ---- | ----- | ---------- | ----- | ---- |
| 1 | Area document, index rows, records 0103–0105 (`Proposed`); revised after the plan challenge and security review | `docs/areas/web-login.md`, `docs/areas/README.md`, `docs/decisions/0103…`, `0104…`, `0105…`, `docs/decisions/README.md` | — | Lead | [x] |
| 2 | Skeleton: package pin and reference; `WebConfig`, `TrustedProxyList`; `Security/*` types; `HashPasswordCommand`; `CommandOptions.HashPassword`; `ServeHooks.Input`; `BackendLog` methods; `Login.razor`, `RedirectToLogin.razor`, `Pages/_Imports.razor` as compiling stubs; `BackendConfig.Web` type change. *Build* passes | `Directory.Packages.props`, `src/Vandox.Backend/Vandox.Backend.csproj`, `src/Vandox.Core/Configuration/*`, `src/Vandox.Backend/Security/*`, `src/Vandox.Backend/Cli/*`, `src/Vandox.Backend/Hosting/*`, `src/Vandox.Backend/Components/*` | — | Dev | [x] |
| 3 | Reference vectors fixture | `testdata/argon2/reference-argon2id.txt` (content verbatim from plan) | AC2 | Tester | [x] |
| 4 | Test helpers and fixture: `LoginTestClient`, `ScriptedPasswordVerifier`, `FixedAuthenticationStateProvider`; `BackendFixture` default hash environment | `tests/Vandox.Backend.Tests/*` | AC4–AC9 | Tester | [x] |
| 5 | Failing tests for the hash and `hash-password` | `PasswordHashTests.cs`, `HashPasswordCommandTests.cs`, `CommandLineTests.cs` | AC2, AC3 | Tester | [x] |
| 6 | Failing tests for the option | `Vandox.Core.Tests/TrustedProxyListTests.cs`, `WebConfigTests.cs`, `BackendConfigLoaderTests.cs` (incl. adapted key sequence) | AC5.4, AC5.5 | Tester | [x] |
| 7 | Failing tests for throttle, login service, sessions, circuits, revalidation, forwarded headers, redirect event, redirect component, routes | `LoginThrottleTests.cs`, `LoginServiceTests.cs`, `SessionStoreTests.cs`, `SessionCircuitHandlerTests.cs`, `SessionRevalidatingStateProviderTests.cs`, `ForwardedHeadersSetupTests.cs`, `WebAuthenticationTests.cs`, `RedirectToLoginTests.cs`, `RoutesTests.cs` | AC4.3–AC4.5, AC5.3, AC6.2, AC6.3, AC7.1–AC7.7a, AC7.9, AC8.1, AC8.4, AC8.5 | Tester | [x] |
| 8 | Failing service tests and adapted existing ones | `ServeTests.cs` (new: AC1.1, AC1.2, AC4.1/4.2, AC5.1, AC5.2, AC6.1, AC7.8, AC8.2, AC8.3; adapted: four existing tests), `LoginTests.cs`, `MainLayoutTests.cs` | AC1, AC4, AC5, AC6, AC7.8, AC8.2, AC8.3, AC9 | Tester | [x] |
| 9 | Implement `PasswordHash` (strict parser, Konscious, fixed-time compare) and `hash-password` | `src/Vandox.Backend/Security/PasswordHash*.cs`, `src/Vandox.Backend/Cli/HashPasswordCommand.cs`, `CommandLine.cs`, `BackendApp.cs` | AC2, AC3 | Dev | [x] |
| 10 | Implement `TrustedProxyList`, `WebConfig`, loader validation | `src/Vandox.Core/Configuration/*` | AC5.4, AC5.5 | Dev | [x] |
| 11 | Implement throttle, login service, session store, circuit handler, revalidating provider, forwarded headers setup, `WebAuthentication` | `src/Vandox.Backend/Security/*` | AC4.4, AC5.3, AC6.2, AC6.3, AC7, AC8 | Dev | [x] |
| 12 | Wire `ServeCommand` (hash check before the database, pipeline order, services), `HealthEndpoint` anonymous; components (`App`, `Routes`, `MainLayout`, `Login`, `RedirectToLogin`, `Pages/_Imports`), `app.css` | `src/Vandox.Backend/Hosting/*`, `src/Vandox.Backend/Components/*`, `src/Vandox.Backend/wwwroot/app.css` | AC1, AC4, AC5.1, AC5.2, AC6, AC7.8, AC8.2, AC8.3, AC9 | Dev | [x] |
| 13 | Deployment: compose file hash secret; example `trusted_proxies`; smoke script (hash via `hash-password`, login checks; `bash -n`, *Analyzer gate* `--lint-only`) | `deploy/backend/docker-compose.yml`, `deploy/backend/vandoxd.yaml`, `.github/scripts/smoke-test-backend.sh` | AC5.5 (example), AC10.2 | Dev | [x] |
| 14 | *Image build check* (restore-order reproduction), result in `log.md` | — | AC10.1 | Dev | [x] |
| 15 | Area documents of other areas | `docs/areas/web-ui.md`, `backend-host.md`, `configuration-and-secrets.md`, `network-and-security-model.md` | — | Dev | [x] |
| 16 | Operator guide | `docs/REVERSE_PROXY.md` (new) | — | Dev | [x] |
| 17 | README, SECURITY.md, ARCHITECTURE.md | `README.md`, `SECURITY.md`, `docs/ARCHITECTURE.md` | — | Dev | [x] |
| 18 | Project knowledge: security areas 3 and 8 — area 3 implemented; goal restated: cookies `HttpOnly`, `SameSite`, and `Secure` whenever the request reached the proxy over HTTPS; the UI port published for the proxy only, as defense in depth (0060) | `.squad/project.md` (*Security areas*) | — | Dev | [x] |
| 19 | Project knowledge: test doubles row | `.squad/project.md` (*Test doubles*) | — | Tester | [x] |
| 20 | Coverage pass until the *Coverage gate* passes | tests | all | Tester | [x] |
