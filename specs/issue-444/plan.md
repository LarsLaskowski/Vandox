# Plan: Replace IngestRig.SettleAsync with signals

Source: Issue #444
Tier: tests — the diff changes only test code under `tests/Vandox.Backend.Tests/` and `docs/UNIT_TESTS.md`.
Security areas touched: none

## Problem / root cause

`IngestRig.SettleAsync` (`tests/Vandox.Backend.Tests/IngestRig.cs:194`) yields 2000 times and is called at eight
sites (`IngestEndpointTests.cs:97`; `IngestEndpointConcurrencyTests.cs:53, 105, 141, 245, 300, 345, 385`). At every
site a signal already exists or the state is established synchronously, so the yields are not needed.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Each request arms a budget timer of exactly `IngestLimits.RequestBudget` on the clock, with no `await` between it and `_slots.WaitAsync` | `IngestEndpoint.cs:349-378, 485`; the rig's clock moves only when a test calls `Advance`; `AdvanceUntilDoneAsync` already waits for these timers | confirmed |
| 2 | No other timer on the rig's clock has that due time | `_shutdown` is created with `Timeout.InfiniteTimeSpan` (`IngestEndpoint.cs:174`), and its deadline (`CancelAfter`, line 340) changes that timer instead of creating one; `IngestRefusalLog` and `FreeSpace` create none | confirmed |
| 3 | `Lifetime.Stop()` runs `OnStopping` (gate closed, deadline armed) before it returns | `ControlledApplicationLifetime.cs:21-23` calls `CancellationTokenSource.Cancel`, which runs registrations synchronously | confirmed |
| 4 | Sites 141, 345, 385 and `IngestEndpointTests:97` check a state that nothing pending can change in correct code. Writes are held, the writer token is `_shutdown.Token` (line 787), `DrainAsync` waits on a gate with one write in flight, and `Advance` and `CancelAsync` finish their callbacks before they return | code read | confirmed |
| 5 | At site 300, whether the request already waits or reaches `_slots.WaitAsync` with a token that is already cancelled, it takes the same catch (`WaitForSlotAsync`, `GiveUp`) and gets the same answer | `IngestEndpoint.cs:481-520` | confirmed |
| 6 | The other `Task.Yield` uses in `tests/` are single yields in fakes or polling loops, not counted waits | `IngestWriteGateTests.cs:200`, `GateProbeIncidentStore.cs:70`, `ContentStreamTests.cs:322`, the page tests | confirmed |

## Acceptance criteria

- AC1: `IngestRig.SettleAsync` is deleted, and no test under `tests/` waits through a counted loop of `Task.Yield`.
- AC2: Each of the eight former call sites waits as *Approach* states, and the adapted tests pass.
- AC3: `docs/UNIT_TESTS.md` (*Structure*, the rule on real delays) forbids a fixed number of `Task.Yield` as a
  wait. It states that a check that something has not happened is made right after the signal (or synchronous call)
  that establishes the state ruling it out.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | check: `grep -rn "SettleAsync" tests/` finds no hit in a test project, and `grep -rn -A2 "for (" tests/ \| grep "Task.Yield"` finds none (Reviewer, step 8) | — |
| AC2 | check: the Tester runs `IngestEndpointConcurrencyTests` and `IngestEndpointTests` 20 times in a row with `--filter`, and all runs are green; the Reviewer reads each site against *Approach* | `tests/Vandox.Backend.Tests/IngestEndpointConcurrencyTests.cs`, `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC3 | check: the Reviewer reads `docs/UNIT_TESTS.md` | — |

## Approach

The Tester (step 6, mode `coverage`) adds a private helper `WaitForBudgetsAsync(IngestRig rig, int requests)` to
`IngestEndpointConcurrencyTests`. It returns
`rig.Clock.WaitForTimersAsync(IngestLimits.RequestBudget, requests, TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken)`.
`AdvanceUntilDoneAsync` calls this helper instead of its own copy.

| Site | Replacement |
| ---- | ----------- |
| `AtMostTwoRequestsHoldASlot` (53) | `WaitForBudgetsAsync(rig, 5)` |
| `SlotIsHeldUntilTheCommit` (105) | `WaitForBudgetsAsync(rig, 3)` |
| `RequestOverBudgetGetsNo2xx`, slot wait (245) | `WaitForBudgetsAsync(rig, 3)`; the comment says the late request armed its budget |
| `WaitingRequestEndsWithAbortOrStop` (300) | `WaitForBudgetsAsync(rig, 3)` |
| `AbortedRequestStillCommits` (141), `DrainWaitsForWritesInFlight…` (345), `CancelsAWriteTenSeconds…` (385), `IngestEndpointTests.ValidBatchAnswers204AfterCommit` (97) | line removed (fact 4) |

Then `SettleAsync` is deleted from `IngestRig`.
At site 300, the budget timer is the last observable point before the slot wait. Fact 5 makes the outcome
independent of the remaining synchronous statements, so the test needs no seam in production code.

## Options considered

- Signals that already exist, as above: chosen.
- A hook on the slot wait in `IngestEndpoint` (named by the issue). It lost: it would add a test seam to production
  code and raise the tier, and fact 5 shows that it would not change any outcome.
- Keep `SettleAsync` and record why it is acceptable (named by the issue). It lost: signals exist at every site.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Backend.Tests | `IngestRig.cs` | `SettleAsync` deleted |
| Vandox.Backend.Tests | `IngestEndpointConcurrencyTests.cs` | helper added, seven sites |
| Vandox.Backend.Tests | `IngestEndpointTests.cs` | one line removed |

## Signatures (for the Dev's skeleton)

None: no new API. IngestRig.SettleAsync is deleted. The helper is private and described in *Approach*.

## Test files

`tests/Vandox.Backend.Tests/IngestEndpointConcurrencyTests.cs`, `tests/Vandox.Backend.Tests/IngestEndpointTests.cs`,
`tests/Vandox.Backend.Tests/IngestRig.cs`.

Existing call sites of the deleted member: the eight above, adapted by the Tester.

Files no unit test can cover: none.

## Areas

None (no change in behavior).

## Documentation updates

- `docs/UNIT_TESTS.md`, *Structure*: the sentence of AC3, written by the Tester. A sweep of `docs/` and
  `.squad/` for `SettleAsync` found no other mention.

## Architecture check

Test code only; no guarantee is touched.

## Security considerations

- [x] No limits.
- [x] None.

## Decision records

- None: this is a routine test change with no trade-off of lasting weight.

## Out of scope / follow-ups

- #445 (`TimerSignalingClock` in the *Test doubles* table of `.squad/project.md`) runs separately.
