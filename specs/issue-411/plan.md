# Plan: Remove the real delays from the polling loops in LoginServiceTests and IngestEndpointConcurrencyTests

Source: Issue #411
Tier: tests — the diff changes test code in `tests/Vandox.Backend.Tests` and one sentence of `docs/UNIT_TESTS.md` only.
Security areas touched: none

## Problem / root cause

Both helpers advance the fake clock in a loop because the test cannot tell when the code under test armed its timer,
backing off with a real delay: `LoginServiceTests.cs:352` (`Task.Delay(1)`) and `IngestEndpointConcurrencyTests.cs:474`
(`Task.Delay(10)`). `docs/UNIT_TESTS.md:97` forbids that; neither exception applies (it is a wait for a result). Fix:
a clock that signals the timers armed on it.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The two delays are at the lines the issue names; the only other real delay in `tests/` is the documented busy-timeout exception | `grep -rn "Task.Delay(" tests/`; `SqliteStoreTests.cs:52` | confirmed |
| 2 | `SignInAsync` arms a 5 s timer on the injected clock before it waits for the gate | `LoginService.cs:73-78` | confirmed |
| 3 | `ServeAsync` arms the 30 s budget timer on the clock before the slot wait and the body read; the clock has not moved, so its due time is exactly `IngestLimits.RequestBudget` | `IngestEndpoint.cs:351-374` | confirmed |
| 4 | The endpoint also arms an infinite timer for `_shutdown` | `IngestEndpoint.cs:174`; scratch `dotnet fsi`: `new CancellationTokenSource(Timeout.InfiniteTimeSpan, fake)` calls `CreateTimer` with due -1 ms | confirmed |
| 5 | `FakeTimeProvider` 11.0.0 is unsealed and `CreateTimer` overridable; `CancellationTokenSource(TimeSpan, TimeProvider)` calls it in the constructor with the delay as due time, and `Advance` by that delay cancels it | reflection and scratch run on the package (`IsSealed=false`, `CreateTimer virtual, not final`) | confirmed |
| 6 | A timer firing before the linked token exists gives the same outcome (`Throttled`, `GiveUp`) | `LoginService.cs:76-85`, `IngestEndpoint.cs:481-520` | confirmed |
| 7 | Timers armed: login one-at-a-time 2, gate-usable 3 (running, timed-out, cancelled); ingest slot wait 3 (two holders, late), trickling body 1 | the arrange blocks; one per attempt or request | confirmed |

## Acceptance criteria

- AC1: `LoginServiceTests` waits for the timed-out attempt without a real delay or loop: it waits until the clock
  reports the 5-second timers armed, advances once by `LoginService.VerificationWait` and awaits the attempt (bounded
  30 s); assertions unchanged and passing.
- AC2: `IngestEndpointConcurrencyTests` does the same with the budget timers and one advance past
  `IngestLimits.RequestBudget`; both data rows pass with unchanged assertions.
- AC3: `TimerSignalingClock.WaitForTimersAsync` completes once the given number of timers with the given due time were
  created on the clock, not before, and timers of another due time do not count.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `LoginServiceVerifiesOneAttemptAtATime`, `LoginServiceGateStaysUsableAfterTimedOutAndCancelledWaits` (adapted, `[TestCategory("AC1")]`) | `tests/Vandox.Backend.Tests/LoginServiceTests.cs` |
| AC2 | `IngestEndpointRequestOverBudgetGetsNo2xx` (adapted, `[TestCategory("AC2")]`) | `tests/Vandox.Backend.Tests/IngestEndpointConcurrencyTests.cs` |
| AC3 | `TimerSignalingClockWaitCompletesAtCountOfMatchingTimers`, `TimerSignalingClockWaitIgnoresTimersOfOtherDueTime` | `tests/Vandox.Backend.Tests/TimerSignalingClockTests.cs` |

Tester, step 6: the AC1 and AC2 tests pass 20 runs in a row (`--filter`); no `Task.Delay` or loop is left in the helpers.

## Approach

New test double `TimerSignalingClock : FakeTimeProvider` (Backend tests). `CreateTimer` calls the base, then under a lock
records the due time and completes every pending waiter whose count of matching due times is reached. A waiter is a
`TaskCompletionSource` with `RunContinuationsAsynchronously`, so the test never runs inside the code under test's
`CreateTimer`; `WaitForTimersAsync` returns at once when the count is already reached and honours its token.

`LoginServiceFixture.Clock` and `IngestRig.Clock` become `TimerSignalingClock` (same start time).

The private helpers `AdvanceUntilCompletedAsync` and `AdvanceUntilDoneAsync` gain an `int timers` parameter and do:
wait for that many timers (due 5 s, or `IngestLimits.RequestBudget`), one `Advance`, await the task — both waits bounded
by 30 s. Call sites pass 2 and 3 (login) and `situation == "slot wait" ? 3 : 1` (ingest), fact 7. Doc comments say
what they wait for. The slot-wait `SettleAsync` and its assert stay.

## Affected projects and types

All in `tests/Vandox.Backend.Tests`: `TimerSignalingClock.cs` (new), `LoginServiceFixture.cs` and `IngestRig.cs` (clock
type), the two test classes (helpers, call sites, categories).

## Signatures (for the Dev's skeleton)

No skeleton (tier `tests`); for the Tester, in `TimerSignalingClock`:

- `internal sealed class TimerSignalingClock : FakeTimeProvider`
- `internal TimerSignalingClock()`
- `internal TimerSignalingClock(DateTimeOffset startDateTime)`
- `public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)`
- `internal Task WaitForTimersAsync(TimeSpan dueTime, int count, CancellationToken cancellationToken)`

## Test files

`tests/Vandox.Backend.Tests/TimerSignalingClockTests.cs` (new), `LoginServiceTests.cs`,
`IngestEndpointConcurrencyTests.cs`. Call sites of the changed helpers: the three above, adapted by the Tester.

Files no unit test can cover: none.

## Areas

none (no change in behavior).

## Documentation updates

- `docs/UNIT_TESTS.md`, *Structure*, after "await the task or signal the fake": a sentence naming `TimerSignalingClock`
  (Backend tests) as the way to advance a fake clock only once the code under test has armed its timer — Tester.
- `.squad/project.md` *Test doubles* stays: tier `tests` may not touch it, and `CountingClock` is not listed either.

## Architecture check

No production code.

## Security considerations

- [x] no limits.
- [x] none.

## Decision records

none: no decision beyond the obvious fix.

## Out of scope / follow-ups

- Related: `IngestRig.SettleAsync` (2000 yields) is no signal either.
- Candidate issue: add `TimerSignalingClock` to the *time* row of `.squad/project.md` *Test doubles*.
