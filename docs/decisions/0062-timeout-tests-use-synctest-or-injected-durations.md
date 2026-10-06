# 0062: Timeout tests use testing/synctest without network, and injected short durations over loopback

- **Status:** Accepted
- **Date:** 2026-10-05
- **Source:** Issue #13
- **Supersedes:** —

## Context

`docs/UNIT_TESTS.md` requires "no real clock (inject a clock or a fixture path)", and `.squad/project.md`
lists an injectable clock as the planned test double for time. Issue #13 brings the first code whose
behavior is a timeout: the `/healthz` database ping limit and its single-flight wait (0059) and the
graceful shutdown deadline of both HTTP servers (0058). These timeouts are enforced by
`context.WithTimeout` and `http.Server.Shutdown`, which read the Go runtime's timers; no clock value of the
program is involved. The project's toolchain is Go 1.27, which has `testing/synctest`: inside its bubble
the `time` package runs on a fake clock that advances only when every goroutine is durably blocked — but
blocking network I/O is not durable, and its documentation says to avoid the network.

## Options considered

1. **An own injectable clock** — a clock interface whose fake the test advances. It cannot drive
   `context.WithTimeout` or `http.Server.Shutdown`; the code would have to reimplement their deadlines on
   top of it, an abstraction used only by tests that changes the production path.
2. **`testing/synctest` everywhere** — fake time with the real `context` and `net/http` code; works for a
   handler called with `httptest.NewRecorder` and a fake that blocks on a channel, but `server.Run` serves
   real `net.Listener`s, whose I/O is not durably blocking (time would not advance reliably); replacing them
   with an in-memory `net.Pipe` listener would test a transport the product never uses.
3. **Real timers with long waits or `time.Sleep`** — simple, but slow and flaky under `-race` and on a busy
   CI runner.
4. **`testing/synctest` where no network is involved, real timers under strict rules over loopback** — the
   handler-level timeout logic runs on fake time; the few `Run`-level tests that need real listeners use
   injected short durations and stay deterministic.

## Decision

Option 4.

- Timeout behavior of a handler or function that does no network I/O (in #13: the `/healthz` ping timeout
  and single-flight wait, AC-S2) is tested inside `synctest.Test` with `httptest.NewRecorder` and fakes
  that block on channels created in the bubble; no real timer runs.
- Timeout behavior that needs real loopback listeners (in #13: `server.Run`'s graceful shutdown and its
  deadline, AC-S6 and AC-S7) uses real timers, and such a test
  - injects every duration under test (`server.Options.PingTimeout`, `server.Options.ShutdownTimeout`): a
    timeout that must fire is about 50 ms, one that must not fire is at least 1 min;
  - never uses `time.Sleep` to wait for a state; it waits on channels, recorded calls (e.g. a listener's
    `Close`) or the result of the call under test;
  - never compares elapsed time with a bound;
  - is written so that each outcome holds for any scheduling delay (a fake that must time out stays blocked
    until `t.Cleanup` releases it).

Code that reads the current time (timestamps, schedules, retention) still uses the injectable clock of
`docs/UNIT_TESTS.md`. A test that sends a real signal to its own process (AC-R7 of #13) involves no clock
and is not covered by this record.

## Consequences

- `docs/UNIT_TESTS.md` names `testing/synctest` and this exception next to "no real clock" and in its
  checklist.
- A real-timer test takes at most its short duration plus scheduling time; a test that waits for a 1 min
  timeout is a bug in the test.
- Reviewers check the rules on every test that exercises a timeout, and that a test without network uses
  `synctest` rather than real timers.
- Revisit if `server.Run` gets a transport that `synctest` can drive durably; that supersedes this record.
