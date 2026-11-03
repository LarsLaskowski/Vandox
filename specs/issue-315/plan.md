# Plan: Replace real-time waits in the ingest drain tests

Source: Issue #315
Status: Draft
Tier: standard — a test-only hook adds one call to the drain path of `ServeCommand` (control flow, so not `trivial` or `patch`) and creates, changes or removes no security control.
Security areas touched: Ingest authentication (the drain in `Hosting/ServeCommand`, `RunStartedAsync`: the drain is unchanged in order and bound, it only gets a test hook)

## Problem / root cause

Record 0126 accepted three tests that use the real clock against `docs/UNIT_TESTS.md` (line 69: "Never `Thread.Sleep` or a real
delay"), with this issue as the follow-up:

- `tests/Vandox.Backend.Tests/ServeTests.cs:1348` and `:1485` race `service.Run` against `Task.Delay(500 ms)` to show that the
  service has not ended while a held write runs. The service gives the test no signal for "the drain began", so the test can only
  wait; a slow host can make the test miss a regression.
- `tests/Vandox.Storage.Tests/SqliteStoreTests.cs:37-67` measures the busy timeout with a `Stopwatch` and bounds the ratio to the
  configured 5 s (0.9 to 2). The timer runs inside SQLite (`PRAGMA busy_timeout=5000`) and in Microsoft.Data.Sqlite's own retry of
  `SQLITE_BUSY` (`DefaultTimeout`, `src/Vandox.Storage/SqliteStore.cs:52-54`, `:299`); neither takes a `TimeProvider`, and
  `docs/UNIT_TESTS.md` has no exception for it (the only one, lines 99-106, is the ratio of two durations).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The two serve tests wait a real 500 ms | `ServeTests.cs:1348`, `:1485` (`Task.WhenAny(service.Run, Task.Delay(TimeSpan.FromMilliseconds(500), …))`) | confirmed |
| 2 | The drain runs in a `finally` of `RunStartedAsync`, inside `ServeAsync`, before `RunAsync`'s `await using` closes the store | `src/Vandox.Backend/Hosting/ServeCommand.cs:197-210`, `:94-105` | confirmed |
| 3 | `IngestEndpoint.DrainAsync` returns a pending task while a write is in flight (it waits on `_gate.CloseAsync`), so with the recording writer holding the write the drain task cannot be complete | `src/Vandox.Backend/Ingest/IngestEndpoint.cs:209-221`; `tests/Vandox.Backend.Tests/RecordingRecordWriter.cs` (`Hold`, `ReleaseAll`); the deadline uses the injected `FakeTimeProvider`, which these tests never advance | confirmed |
| 4 | `ServeHooks` already carries internal test-only hooks (`IngestWriter`, `FreeSpaceProbe`) visible to `Vandox.Backend.Tests` | `src/Vandox.Backend/Hosting/ServeHooks.cs:46-54`; `Vandox.Backend.csproj:44` (`InternalsVisibleTo`) | confirmed |
| 5 | No document enumerates the members of `ServeHooks` | `grep -rn ServeHooks docs .squad/project.md`: no hit | confirmed |
| 6 | The busy timeout test is "inherently real time": no clock can be injected into SQLite's busy handler or into Microsoft.Data.Sqlite's retry | `SqliteStore.cs:50-54`, `:299`; `docs/areas/storage.md:22-27` (both settings, measured 31 s without `DefaultTimeout`) | confirmed |
| 7 | The busy timeout test already meets the bounds the issue proposes (wait at most twice the configured value, ratio 0.9 to 2) | `SqliteStoreTests.cs:52`, `:65-66` | confirmed |
| 8 | Record 0126 is unreleased, so it is edited in place | `git cat-file -e v0.1.0:docs/decisions/0126-…`: not in `v0.1.0`, the only tag (`git ls-remote --tags origin`) | confirmed |
| 9 | Related: `IngestEndpointStoreLockHeldByAnotherConnectionAnswers503` (`tests/Vandox.Backend.Tests/IngestEndpointTests.cs:906`) also lets the 5 s library timer fire on the real clock, without asserting a duration | `IngestEndpointTests.cs:906-940` | confirmed |
| 10 | Related, out of scope: `ReportServiceTests.cs:58` waits a real 200 ms (`Task.Delay(200, …)`) to show the second run waits | `tests/Vandox.Backend.Tests/ReportServiceTests.cs:41-72` | confirmed |

Issue claims: the 500 ms waits (fact 1), the native timer (fact 6) and the acceptance in record 0126 (record lines 103-107 before
this change) are confirmed. The timer is not only native: Microsoft.Data.Sqlite's managed retry runs on the real clock too (fact 6).

## Acceptance criteria

- [ ] AC1: `ServeShutdownDrainsIngestWritesBeforeClosingStore` has no real delay. After the stop it waits (bounded, as the other
  waits of the file) for the drain signal or the end of the run, and asserts that the signal came, that the drain task and
  `service.Run` were both not complete when it came (the write still held), and, after `ReleaseAll`, that the drain task completed,
  the write committed (`Writer.Completed == 1`) and the 3 records are stored.
- [ ] AC2: `ServeFailureAfterStartStillDrains` has no real delay and asserts the same as AC1 on the failure path, plus exit code 1.
- [ ] AC3: `ServeHooks.Draining` is `null` by default and the service then behaves as before: the drain is started in the `finally`
  of `RunStartedAsync`, the hook (when set) gets the drain task before the service awaits it, and the store closes only after it.
- [ ] AC4: `docs/UNIT_TESTS.md` names a narrow exception to "no real clock" for a timeout that a library times itself on the real
  clock without a clock to inject (the example: SQLite's busy timeout and Microsoft.Data.Sqlite's retry): a test may let such a timer
  fire, behind a bounded wait, and at most one test per timer may measure it, waiting at most twice the configured value and bounding
  the ratio of the measured to the configured duration to 0.9 to 2. The *Before committing* checklist line (line 142) names it next to
  the ratio exception. `SqliteStoreWriteGivesUpAfterBusyTimeout` and `IngestEndpointStoreLockHeldByAnotherConnectionAnswers503`
  comply unchanged.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `ServeShutdownDrainsIngestWritesBeforeClosingStore` (rewritten) | `tests/Vandox.Backend.Tests/ServeTests.cs` |
| AC2 | `ServeFailureAfterStartStillDrains` (rewritten) | `tests/Vandox.Backend.Tests/ServeTests.cs` |
| AC3 | `ServeShutdownDrainsIngestWritesBeforeClosingStore`, `ServeFailureAfterStartStillDrains` (hook set); every other serve test, unchanged (hook `null`) | `tests/Vandox.Backend.Tests/ServeTests.cs` |
| AC4 | `SqliteStoreWriteGivesUpAfterBusyTimeout` (unchanged, now under the documented exception); the Reviewer reads the paragraph against the test | `tests/Vandox.Storage.Tests/SqliteStoreTests.cs` |

What each rewritten test proves, deterministically for correct code: the write is held until the test has seen the signal, so the
drain cannot be complete when it fires (fact 3). A service that no longer drains never fires the signal and ends the run: the test
fails at once on "the signal came". A service that drains after it closed the store fires the signal with the store closed: the
released write fails and `Completed` stays 0. A service that starts the drain without awaiting it is caught by `Completed` only when
the store closes before the released write commits — practically always, not by construction; the plan accepts that (the 500 ms wait
had the same blind spot on a slow host).

## Approach

1. **Production (Dev).** `ServeHooks` gets the internal property `Draining` (signature below). In `ServeCommand.RunStartedAsync`'s
   `finally` the Dev replaces the one `await …DrainAsync()` line by: start the drain into a local, `hooks.Draining?.Invoke(drain)`,
   then `await drain.ConfigureAwait(false)`. The `<param name="hooks">` text of `RunStartedAsync` says it also tells the test when the
   drain begins. Nothing else changes.
2. **Test helper (Tester).** `IngestService` (`tests/Vandox.Backend.Tests/IngestService.cs`) sets `Draining` in `BeginAsync`'s hooks
   to complete a new `TaskCompletionSource<Task>` (`RunContinuationsAsynchronously`) and exposes it as `internal Task<Task> Draining`,
   documented like `Stopping` and `Stopped`. Setting it for every `IngestService` test is harmless: it only records.
3. **Tests (Tester).** Rewrite the two tests to AC1/AC2: after `Stop()` (AC1, followed by the existing `Stopped` wait) or `fail.Set()`
   (AC2), `await Task.WhenAny(service.Draining, service.Run).WaitAsync(TimeSpan.FromSeconds(30), …)`; read the drain task and record
   `drain.IsCompleted` and `service.Run.IsCompleted` before `ReleaseAll()`; then await the run, the post and the rows as today. Assert
   messages name the property (for example "the drain waits for the write that is still running after the host stopped"). The
   summaries of both tests stay accurate; adjust them if they mention the wait.
4. **Documentation (Tester, test documentation).** The paragraph of AC4 next to the ratio exception in `docs/UNIT_TESTS.md`
   (*Structure*), linking record 0126, and the checklist line 142. Wording free within AC4.

## Options considered

1. **A `ServeHooks` callback that receives the drain task when the drain begins** (chosen) — the test can check that the drain is
   pending while the write is held, with no wait; one internal property and one call.
2. **A parameterless "drain began" callback** — rejected: at that moment the run is never complete, even when the drain does not
   wait, so the check proves nothing; the task is what shows the drain waits.
3. **A second callback right before the store is disposed (in `RunAsync`)** — rejected: with the held write released only after the
   drain signal, `Writer.Completed == 1` against the real store already shows the commit ran on an open store; the extra hook adds a
   second production call without closing the remaining blind spot (it races the same way).
4. **Busy timeout: a configuration check instead of the measurement** — rejected: the store's connections are private (a check needs a
   production accessor), and only the measurement shows that the library's retry ends at the configured value (record 0126).
5. **Busy timeout: a documented exception for library timers** (chosen) — the test stays as it is and becomes compliant.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Backend | `src/Vandox.Backend/Hosting/ServeHooks.cs` | new internal property `Draining` |
| Vandox.Backend | `src/Vandox.Backend/Hosting/ServeCommand.cs` | `RunStartedAsync`: drain into a local, invoke the hook, await |
| Vandox.Backend.Tests | `tests/Vandox.Backend.Tests/IngestService.cs` | `Draining` task, hook set in `BeginAsync` |
| Vandox.Backend.Tests | `tests/Vandox.Backend.Tests/ServeTests.cs` | the two tests rewritten |
| docs | `docs/UNIT_TESTS.md` | exception for library timers, checklist line |
| docs | `docs/decisions/0126-…md` | already edited by the Lead (options and consequence for #315) |

## Signatures (for the Dev's skeleton)

In `ServeHooks`, region *Properties*, after `FreeSpaceProbe`:

```csharp
/// <summary>
/// Gets or sets an action that is called with the drain of the ingest writes as soon as it begins, before the service waits for it; tests only.
/// </summary>
internal Action<Task>? Draining { get; set; }
```

`ServeCommand.RunStartedAsync` keeps its signature. Existing files the skeleton rewrites: none beyond these two; both were read and
need only this edit.

## Test files

- `tests/Vandox.Backend.Tests/ServeTests.cs`, `tests/Vandox.Backend.Tests/IngestService.cs` (Tester).
- `tests/Vandox.Storage.Tests/SqliteStoreTests.cs`: unchanged.

Existing test code that calls a changed signature: none (a new optional property only).

Files of the change no unit test can cover: none.

## Areas

none — no change in behavior (`docs/areas/backend-host.md:211-213`, *Drain before the store closes*, stays true).

## Documentation updates

Sweep: `ServeHooks`, "real delay", "real clock" and "exception" in `docs/`, `docs/areas/`, `docs/ARCHITECTURE.md` and
`.squad/project.md`. Hits:

- `docs/UNIT_TESTS.md:68-69` ("Never `Thread.Sleep` or a real delay to wait for a result") — unchanged; the new exception is about a
  timer the code under test needs to fire, not a wait for a result.
- `docs/UNIT_TESTS.md:99-106` (the one narrow exception) — the second exception is added next to it (Tester, AC4).
- `docs/UNIT_TESTS.md:142` (checklist: "no real network, clock or sleep, again except such a ratio") — names the new exception
  (Tester, AC4).
- `.squad/project.md:18`, `:30-32` (the drain in `Hosting/ServeCommand`) — unchanged, still true.
- `docs/ARCHITECTURE.md`: no hit; no change.

## Architecture check

The guarantee that the store closes only after the ingest writes in flight (security area 1, `docs/areas/backend-host.md` *Drain
before the store closes*) is untouched: the drain is still started and awaited in the same `finally`, bounded by the same deadline;
only an optional callback sits between start and await. A throwing callback is a test fault (the property is internal and set only
by tests).

## Security considerations

- [x] No limits on parsed input change — no limits.
- [x] No exception reaches a user-visible message — none.

## Decision records

- `docs/decisions/0126-ingest-endpoint-token-bound-to-one-agent-id-two-slots-through-the-commit-no-rate-cap.md` (Accepted,
  unreleased, edited in place by the Lead): source #315, the options for the drain signal and the busy timeout test, and the
  consequence line that replaced the accepted real-clock deviation. The index row needs no change.

## Out of scope / follow-ups

- Follow-up issue (proposed): "[Tests] Replace the 200 ms wait in ReportServiceRunsOneReportAtATime" — body:
  "`ReportServiceRunsOneReportAtATime` (`tests/Vandox.Backend.Tests/ReportServiceTests.cs:58`) waits a real 200 ms to show that the
  second run does not read while the first one holds the slot, against `docs/UNIT_TESTS.md` (no real delay). Replace it with a
  deterministic signal, for example a hook or observable state when the second run starts to wait for the slot. Found while planning
  #315."

## Challenge notes

Devil's Advocate, minor (verbatim):

1. **minor**: The rewritten AC1 test still waits on the real clock, and the plan says nothing about it. Reason: the issue asks to remove the real-time waits, and this one stays without being named or covered by the docs.
   - Evidence: `ServeShutdownDrainsIngestWritesBeforeClosingStore` starts the service with `shutdownTimeout: TimeSpan.FromMilliseconds(300)` (ServeTests.cs:1338). `IngestService.Prepare` writes that into `HostOptions.ShutdownTimeout` (tests/Vandox.Backend.Tests/IngestService.cs:255-257).
   - Plan step 3 keeps the existing `Stopped` wait, and `Stopped` only fires after the host gives up on the held request. The host times that 300 ms on the real clock and takes no `TimeProvider`.
   - The test needs this timer ("the host stopped before the write ended"), so it cannot just be removed.
   - Instruction for the Tester: word the AC4 paragraph in `docs/UNIT_TESTS.md` so it also covers a host's shutdown timeout (`HostOptions.ShutdownTimeout`), set short and injected, that a test lets fire behind a bounded wait without asserting how long it took. Otherwise name this test explicitly as complying under that exception. Either way the test keeps the 300 ms setting and does not measure it.

## Security notes

Security, plan review APPROVED, non-blocking (verbatim):

1. **A throwing hook would skip the drain wait.** If `hooks.Draining?.Invoke(drain)` throws, `await drain` is skipped. The exception leaves `RunStartedAsync`, and the store can close while a write is still running. The plan accepts this as a test fault (*Architecture check*), and since only tests can set the hook it cannot happen in production. If the Dev wants the guarantee to hold by construction anyway, wrap the call: `try { hooks.Draining?.Invoke(drain); } finally { await drain.ConfigureAwait(false); }`. This is optional.
2. **Write the test helper's completion as `TrySetResult`.** The hook runs inside the `finally`, so a second invocation (not possible today) or an exception in the callback would land on the shutdown path. The plan's `TaskCompletionSource` with `RunContinuationsAsynchronously` already keeps the test's continuations off that path, which is fine.
