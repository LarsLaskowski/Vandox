# 0062: Timeout tests use testing/synctest without network, and injected short durations over loopback

- **Status:** Accepted
- **Date:** 2026-10-05
- **Area:** —
- **Source:** Issue #13
- **Supersedes:** —

## Context

`docs/UNIT_TESTS.md` requires "no real clock (inject a clock or a fixture path)". Timeouts in Go code are enforced by
`context.WithTimeout` and `http.Server.Shutdown`, which read the Go runtime's timers; no clock value of the program is involved, so an
injectable clock cannot drive them. Go 1.27 has `testing/synctest`: inside its bubble the `time` package runs on a fake clock that
advances only when every goroutine is durably blocked, but blocking network I/O is not durable and its documentation says to avoid the
network. The first code with timeout behavior was the former Go backend (the `/healthz` ping limit and the graceful shutdown); the
rule applies to every Go timeout test, for example of the agent.

## Options considered

- **An own injectable clock** — rejected: it cannot drive `context.WithTimeout` or `http.Server.Shutdown`, and reimplementing their
  deadlines would change the production path for the sake of tests.
- **`testing/synctest` everywhere** — rejected: real `net.Listener`s are not durably blocking, so time would not advance reliably;
  an in-memory listener would test a transport the product never uses.
- **Real timers with long waits or `time.Sleep`** — rejected: slow and flaky under `-race` and on a busy runner.
- **`synctest` where no network is involved, real timers under strict rules over loopback** — chosen.

## Decision

Timeout behavior of code that does no network I/O is tested inside `synctest.Test` with `httptest.NewRecorder` and fakes that block on
channels created in the bubble; no real timer runs. Timeout behavior that needs real loopback listeners uses real timers, and such a
test injects every duration under test (a timeout that must fire is about 50 ms, one that must not fire at least 1 min), never uses
`time.Sleep` to wait for a state (it waits on channels, recorded calls or the result of the call under test), never compares elapsed
time with a bound, and holds for any scheduling delay (a fake that must time out stays blocked until `t.Cleanup` releases it). Code
that reads the current time still uses the injectable clock of `docs/UNIT_TESTS.md`; the .NET tests use `FakeTimeProvider`.

## Consequences

- `docs/UNIT_TESTS.md` names `testing/synctest` and this exception next to "no real clock" and in its checklist.
- A real-timer test takes at most its short duration plus scheduling time; a test that waits for a 1 min timeout is a bug in the test.
- Reviewers check the rules on every test that exercises a timeout.
- Revisit if a transport appears that `synctest` can drive durably; that supersedes this record.
