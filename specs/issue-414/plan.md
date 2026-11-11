# Plan: Two timing-dependent tests failed once in CI on PR #413

Source: Issue #414
Tier: tests — only test code changes (`internal/scheduler/trigger_test.go`, `tests/Vandox.Backend.Tests/`), past the `patch` budget since the fixture's start wait gains control flow.
Security areas touched: none

## Problem / root cause

1. `internal/scheduler/trigger_test.go:161-162`: `start(...)` runs `s.Run` in a goroutine (`scheduler_test.go:268-271`) before `SetPressure(true)`. The runner reads the mode per cycle (`runner.go:116`, `:219`), so the run at 0 s can see `false`.
2. `tests/Vandox.Backend.Tests/BackendFixture.cs:147` waits for `Started` alone, never for the run. A run that ends before it starts — such as a port taken between `FreePort()` (`:174-181`, released at once) and Kestrel's bind, which `ServeCommand.cs:165-183` turns into exit code 1 without `Started` — surfaces as a `TimeoutException` after exactly 20 s, its standard error lost.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Issue: pressure is set after the scheduler starts | root cause 1 | confirmed |
| 2 | `startIn(..., true)` sets the mode before `Run` begins | `scheduler_test.go:255-271`; used at `trigger_test.go:436` | confirmed |
| 3 | Issue: the wait is a fixed real-time timeout | `BackendFixture.cs:147`, 20 s | confirmed |
| 4 | Issue: the wait "ran out on a loaded runner" | to verify: not measurable — the CI log shows only `[20 s]`; a run ending before `Started` fits equally (stderr dropped). AC3 makes the next occurrence name its cause; the fix covers both | unverified |
| 5 | A bind failure ends the run before `Started` | `ServeCommand.cs:165-183`; `Announce` (`:375`) only after `StartAsync` | confirmed |
| 6 | The service cannot pick its own port | `ListenAddress.cs:27` refuses port 0 | confirmed |
| 7 | A `TcpListener` on `127.0.0.1:<WebPort>` makes Kestrel's bind fail on Linux | to verify: Tester, step 6 — the AC2 test fails on today's fixture with `TimeoutException` | unverified |
| 8 | Nothing else bounds the wait | no MSTest timeout; `ci.yml` has no `timeout-minutes` | confirmed |
| 9 | The sender's `SetPressure(true)` after `r.start()` (`sender_test.go:1338`) races alike | pressure is read only after a later live probe | refuted |

## Acceptance criteria

- AC1: The scheduler test sets the pressure mode before `Run` starts (`startIn(t, ..., true)`, the later `SetPressure` call removed). Its expectations stay: starts `[0 10 13 23 33]` s, all with `Pressure` true.
- AC2: When the run ends before it reports its start, `StartServiceAsync` starts it again on two newly chosen ports, up to 3 attempts in total. Before each new attempt it clears `Out` and `Error` and rewrites the configuration file with the new ports and the same `web.trusted_proxies` option. It returns the run of the attempt that started, and `WebPort`/`IngestPort` are that attempt's ports.
- AC3: When all 3 attempts end before their start, `StartServiceAsync` throws `InvalidOperationException` (not `TimeoutException`), with the last exit code and every attempt's standard error in its message.
- AC4: Each attempt still waits at most 60 s for a start that neither reports nor ends, then throws `TimeoutException`. This is a hang guard, not a verdict.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestScheduler_TriggerReplacesNextRunUnderPressure_AC1` (renamed) | `internal/scheduler/trigger_test.go` |
| AC2 | `BackendFixtureStartServiceAsyncStartsAgainOnNewPortsWhenThePortIsTaken`: hold `WebPort` with a listener and set a valid trusted-proxies value; the start succeeds, `WebPort` changed, the configuration file holds the new ports and the option, `Error` holds no `ServingFailed` entry | `tests/Vandox.Backend.Tests/BackendFixtureTests.cs` |
| AC3 | `BackendFixtureStartServiceAsyncThrowsWithTheOutputWhenEveryAttemptEnds`: an invalid `UseTrustedProxies` value; `Assert.ThrowsExactlyAsync<InvalidOperationException>`, message holds the exit code and the configuration error | `tests/Vandox.Backend.Tests/BackendFixtureTests.cs` |
| AC4 | manual: the Reviewer reads the bound in the diff (no injectable clock) | — |

The Tester also runs `go test ./internal/scheduler -race -run '_AC1$' -count=500 -cpu 1,2,4,8` and reports the result.

## Approach

Fixture: the trusted-proxies option in a field; `WebPort`/`IngestPort` get private setters. Per attempt, with a fresh `TaskCompletionSource` and hooks: `await Task.WhenAny(started.Task, run).WaitAsync(60 s, wait)`. If the start was reported, return the run. Otherwise await the exit code, record it with `Error.ToString()`, choose new ports, rewrite the configuration and clear the output. `IngestService.cs:220-227` uses the same pattern.

## Signatures (for the Dev's skeleton)

No new API. BackendFixture.WebPort and IngestPort gain private setters; StartServiceAsync keeps its signature.

## Test files

`internal/scheduler/trigger_test.go`, `tests/Vandox.Backend.Tests/BackendFixtureTests.cs`; the 13 `StartServiceAsync` callers stay unchanged. Code a unit test cannot cover: none.

## Areas

none.

## Documentation updates

none: a sweep of `docs/` found no mention of `BackendFixture`/`StartServiceAsync`.

## Architecture check

None touched.

## Security considerations

- [x] no limits.
- [x] none (test code).

## Decision records

- none: no decision beyond the obvious fix.

## Out of scope / follow-ups

- Issue "[CI] Stress-run the scheduler pressure trigger test" (`patch/security`, `.github/scripts/` is area 13): `docs/UNIT_TESTS.md` (*Load-dependent tests (Go)*) lists tests that failed intermittently in CI. Add `"internal/scheduler TestScheduler_TriggerReplacesNextRunUnderPressure_AC1"` to `.github/scripts/stress-go-tests.sh` under `# Failed intermittently in CI (#329, #336, #414).`. Split off: that file takes this change out of tier `tests`.
- Issue "[Tests] ServeTests wait for Started without watching the run": `ServeTests.cs` around lines 66, 950 and 999 wait 20 s on `Started` alone, the defect of root cause 2. They should use `StartServiceAsync` or its pattern.
