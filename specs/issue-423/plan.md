# Plan: ServeTests wait for Started without watching the run

Source: Issue #423
Tier: tests — the diff changes test code only (`tests/Vandox.Backend.Tests`), more than the `patch` line budget allows.
Security areas touched: none

## Problem / root cause

`ServeRunsServiceOnConfiguredPortsAndStopsCleanly` (`ServeTests.cs:78`), `ServeGatesTheLogExplorer` (`:954`) and
`ServeSharesOneReaderGateBetweenLogsAndIncidents` (`:1003`) start the service with `fixture.RunAsync` and then
`await started.Task.WaitAsync(TimeSpan.FromSeconds(20), …)` only. A run that ends before `Started` (a port taken
between `FreePort()` and Kestrel's bind) leaves the test waiting 20 s for a `TimeoutException` without exit code or
`fixture.Error`. Related defect found on the way: `ServeUsesListenHookAndReportsUnavailableDatabase` (`:201`) waits the
same way; it binds port 0, so the port race cannot hit it, but any other early exit loses the output alike.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The three named tests wait 20 s on `Started` only | `ServeTests.cs:78`, `:954`, `:1003` | confirmed |
| 2 | A failed bind ends the run with exit code 1 without `Started` | `ServeCommand.cs:168-184`: `app.StartAsync` throws an `IOException`, caught, `return 1`; `Started` is invoked only in `Announce` (`:376`) | confirmed |
| 3 | `BackendFixture.StartServiceAsync` watches the run, retries on new ports up to three times and reports exit code and stderr | `BackendFixture.cs:155-190`; `BackendFixtureTests.cs:69`, `:103` | confirmed |
| 4 | `StartServiceAsync` sets only `Clock` and `Started`, so the tests needing `Built` (lines 950, 999) cannot use it as it stands | `BackendFixture.cs:163-167` | confirmed |
| 5 | `Built` runs before `app.StartAsync`, so a failed attempt calls it too | `ServeCommand.cs:165-168`, `:263` | confirmed |
| 6 | `IngestService.cs:220-227` uses the watch pattern | `IngestService.cs:221` (`Task.WhenAny(_started.Task, Run)`) | confirmed |
| 7 | No other test waits on a `Started` hook alone (lines 364, 398 await the run itself) | `grep "Started = " tests/`; `ServeTests.cs:368`, `:402` | confirmed |

## Acceptance criteria

- AC1: `ServeRunsServiceOnConfiguredPortsAndStopsCleanly`, `ServeGatesTheLogExplorer` and
  `ServeSharesOneReaderGateBetweenLogsAndIncidents` start the service through `StartServiceAsync` (via the private
  helper `ServeTests.StartAsync`), keep every assertion and pass; none waits on a `Started` hook of its own.
- AC2: `StartServiceAsync` with a `built` action calls it with the service provider of every attempt it builds; the
  last call gets the provider of the returned run, which resolves services while that run listens.
- AC3: `BackendFixture.WaitForStartAsync` returns once the start task completes; when the run ends first it throws an
  `InvalidOperationException` with the message
  `the service stopped before it started, exit code {code}, standard error:\n{Error}`.
  `ServeUsesListenHookAndReportsUnavailableDatabase` waits through it.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | check: the three adapted tests pass under *Test (project)*; the Reviewer confirms none holds a `started` completion source | `tests/Vandox.Backend.Tests/ServeTests.cs` |
| AC2 | `BackendFixtureStartServiceAsyncPassesTheStartedRunsProviderToBuilt` | `tests/Vandox.Backend.Tests/BackendFixtureTests.cs` |
| AC3 | `BackendFixtureWaitForStartAsyncThrowsWithTheOutputWhenTheRunEnds`, `BackendFixtureWaitForStartAsyncReturnsWhenTheServiceStarts` | `tests/Vandox.Backend.Tests/BackendFixtureTests.cs` |

## Approach

1. `StartServiceAsync` gets an optional last parameter `built`, set as `Built = built` on every attempt's hooks; its
   XML doc says it runs once per attempt, so a caller keeps the last provider.
2. New `WaitForStartAsync`: `Task.WhenAny(started, run)` bounded by `StartTimeoutSeconds` and `wait`; return if
   `started` completed, else throw the AC3 exception (`await run` for the code). No retry: the caller owns its hooks.
3. `ServeTests.StartAsync` (private, line 1709) gets the same optional `built` parameter and passes it on.
4. Line 66 test: replace completion source, hooks, `RunAsync` and wait with `var run = await StartAsync(fixture,
   stop.Token);` (ports are already read after the start, lines 82-83).
5. Lines 950 and 999 tests: the same, with `built: provider => services = provider`.
6. Line 198 test: keep its hooks and `RunAsync`; replace the wait with
   `await fixture.WaitForStartAsync(started.Task, run, TestContext.CancellationToken);` then
   `var addresses = await started.Task;`.
7. AC2 takes the first web port with a `TcpListener` (as `BackendFixtureTests.cs:69`), collects the providers,
   asserts two calls and that the last resolves `ILogSearcher` before the stop. AC3's throw case runs
   `UseTrustedProxies("not-an-address")` (as `:103`) against a never-completing start task and asserts `exit code 1`
   and `web.trusted_proxies` in the message; the return case passes a completed task and an unfinished
   `TaskCompletionSource<int>` as the run — no service.

## Signatures (for the Dev's skeleton)

- `internal async Task<Task<int>> StartServiceAsync(CancellationToken stop, CancellationToken wait, TimeProvider? clock = null, Action<IServiceProvider>? built = null)` (changed, `BackendFixture`)
- `internal async Task WaitForStartAsync(Task started, Task<int> run, CancellationToken wait)` (new, `BackendFixture`)

No skeleton step on this tier: the Tester writes both with the tests.

## Test files

- `tests/Vandox.Backend.Tests/BackendFixtureTests.cs` (new tests), `BackendFixture.cs` and `ServeTests.cs` (adapted) —
  all by the Tester.

Existing call sites of `StartServiceAsync` and `ServeTests.StartAsync`: unaffected (the new parameter is optional
and last).

Files no unit test can cover: none.

## Areas

none (no change in behavior).

## Documentation updates

none: `docs/areas/`, `docs/ARCHITECTURE.md` and `docs/UNIT_TESTS.md` do not mention `BackendFixture` or the
`Started` hook; the PR template asks for no changelog entry for test code.

## Architecture check

No guarantee touched: test code only.

## Security considerations

- [x] no limits.
- [x] none.

## Decision records

- none: no decision beyond the obvious fix.

## Out of scope / follow-ups

- Folding `StartServiceAsync`'s own wait into `WaitForStartAsync`: not needed for this fix.
