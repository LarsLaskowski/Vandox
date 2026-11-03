# Plan: Count the body bytes Send requests in the 4 KiB body-cut test

Source: Issue #329
Status: Draft
Tier: trivial — the diff changes test code only (one test and its helpers in `internal/sender/transport_test.go`), with no change to production behavior or control flow, and needs more than the `patch` budget of 30 lines for two small wrapper types.
Security areas touched: none

## Problem / root cause

`TestHTTPTransport_ReadsAtMostFourKiBOfBody` (`internal/sender/transport_test.go:554-594`) checks the 4 KiB body
cut indirectly: it counts every byte the HTTP client reads from the socket (`countConn`, lines 540-552) and
bounds the total by `MaxAnswerHeader` (16 KiB, line 589). That count holds three things besides what `Send`
asks for: the answer's header, the 4 KiB read-ahead of net/http's `bufio.Reader`, and whatever net/http's
post-close drain reads.

The failures on PR #324 (2026-10-29: 20,611 and 213,238 bytes) came from the drain. Before #351 (5908eab,
2026-11-01), `Send` only cancelled the request's context and then closed the body. Go 1.27's
`persistConn.readLoop` (`$GOROOT/src/net/http/transport.go:2603-2608`) then drains a chunked body
(`ContentLength` -1 is at most `maxPostCloseReadBytes`, 256 KiB) in a goroutine for up to 50 ms
(`maybeDrainBody`, 2427-2441), and that goroutine runs after `Body.Close` has returned. So the count read at
line 589 depended on how far the drain got before the assertion ran. #351 made `Send` close the connection
itself before `Body.Close` (`internal/sender/transport.go:154-160`), so the drain now fails at its first read.

What is left is a test that still measures what the transport buffered, not what `Send` requested. It checks
the 4 KiB cut too loosely: a `Send` that read 8 KiB of body passes, because the socket count is then about 8.3
KiB, under the 16 KiB bound. The fix measures the cut where `Send` reads, at the response body, and checks
the #351 behavior directly. The socket-level bound stays as a coarse memory ceiling.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The failing runs (20,611 and 213,238 bytes against 16,384) bound bytes the transport happened to read, not bytes `Send` requested | `countConn` counts every `Conn.Read` (transport_test.go:546-552); the drain goroutine reads after `Body.Close` returns (`transport.go` of Go 1.27, 2427-2441, 2603-2608); #324 merged 2026-10-29, before #351 (2026-11-01) | confirmed |
| 2 | On current main the test no longer depends on scheduling | 200 runs (`-race -count=200`) with 4 busy-loop processes on 4 CPUs: `ok`. In a scratch worktree with a `t.Logf` of the count: all 400 subtests read exactly 8,192 bytes | confirmed |
| 3 | Before #351 the drain overlapped the assertion | Code path in fact 1 (confirmed by reading). Not reproduced locally: with the pre-#351 `transport.go`, 400 subtests under the same load read 8,192 bytes each | confirmed |
| 4 | The current bound does not check the 4 KiB cut | Scratch worktree with `Send` limited to `2*MaxAnswerBody`: the test passes, reading 8,314 to 8,342 bytes from the socket | confirmed |
| 5 | A socket count cannot pin the cut robustly | Correct code reads 8,192 bytes from the socket; a 2x regression reads about 8,330 (fact 4): a margin of about 140 bytes, set by net/http's 4 KiB `readBufferSize` (`transport.go:329-334`) | confirmed |
| 6 | `GotConnInfo.Conn` is the dialed `countConn`, so `Send`'s `conn.Close()` reaches `countConn` | `dialConn` stores the dialed conn as `pconn.conn` for a plain-HTTP server; `newServer` starts plain HTTP (transport_test.go:28-37) | confirmed |
| 7 | Wrapping `tr.client.Transport` in a RoundTripper keeps `Send`'s behavior: the trace context, dialing and `Result` classification all go through the wrapped `*http.Transport` | transport.go:98-114, 134-163; `useDial` (transport_test.go:89-97) type-asserts `*http.Transport`, so it must run before the wrap | confirmed |
| 8 | The connstats part of the issue is settled | 9faf697 (#336) on main; the issue comment of 2026-11-01 | confirmed |
| 9 | 20 consecutive runs pass under CPU load with the new assertions | to verify: Tester, step 6. Run `go test ./internal/sender -race -run TestHTTPTransport_ReadsAtMostFourKiBOfBody -count=20` while one busy loop per CPU runs, and quote the output in the result | unverified |

## Acceptance criteria

- [ ] AC1: In each of the four cases, `Send` reads exactly `MaxAnswerBody` (4,096) bytes from the answer's body. The count is taken at the `resp.Body` that `Send` reads, not at the socket. A `Send` limited to `2*MaxAnswerBody` fails the test.
- [ ] AC2: In each case, `Send` closes the connection before it closes the cut body. A `Send` without the `conn.Close()` at transport.go:155-157 fails the test.
- [ ] AC3: In each case, the client reads at most `MaxAnswerHeader` (16 KiB) from the connection in all. This bound is kept as the memory ceiling.
- [ ] AC4: `go test ./internal/sender -race -run TestHTTPTransport_ReadsAtMostFourKiBOfBody -count=20` passes 20 consecutive times with one busy loop per CPU running in parallel. The existing results and outcomes (`Result{Status, Reason}`, `Outcome`) are still checked.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestHTTPTransport_ReadsAtMostFourKiBOfBody`, all four cases: body count `!= MaxAnswerBody` fails ("Send read %d bytes of the body, want %d") | `internal/sender/transport_test.go` |
| AC2 | same test: body closed while the connection was still open fails | `internal/sender/transport_test.go` |
| AC3 | same test: the existing socket-count check, kept | `internal/sender/transport_test.go` |
| AC4 | same test, run with `-race -count=20` under load (fact 9) | `internal/sender/transport_test.go` |

The Tester mutates `transport.go` once to show AC1 and AC2 fail: first `2*MaxAnswerBody` in the `LimitReader`
and the `read >=` condition, then the `conn.Close()` removed. The mutations go in a scratch copy, never into
the diff, and the failing output is quoted in the result.

## Approach

All changes are in `internal/sender/transport_test.go`.

1. `countConn` gets a `closed *atomic.Bool` field and a `Close() error` that stores `true` and then calls the
   embedded `Close`.
2. A new `countBody` (an embedded `io.ReadCloser`, `n *atomic.Int64`, `conn *atomic.Bool`,
   `connClosedFirst *atomic.Bool`):
   - `Read` adds the bytes it returns to `n`.
   - `Close` stores `conn.Load()` into `connClosedFirst` and then closes the embedded body.
3. A new `countBodyTransport` (`base http.RoundTripper` and the three counters):
   - `RoundTrip` calls `base`;
   - on an error it returns `nil, err`;
   - otherwise it replaces `resp.Body` with a `countBody` and returns the response.
4. In the test, call `useDial` first (as today, with the `closed` flag), then set
   `tr.client.Transport = &countBodyTransport{base: tr.client.Transport, ...}`. After `send`, keep the two
   existing `require*` checks, then:
   - AC1: the body count equals `MaxAnswerBody`;
   - AC2: `connClosedFirst` is true;
   - AC3: the socket count is at most `MaxAnswerHeader`. Reword its message: it bounds everything read from the
     connection, header and the transport's read buffer included.

No time bound and no ratio is needed: AC1 and AC2 are exact for correct code. AC3 is deterministic because
the connection is closed before any drain (facts 2 and 6). No user-visible output.

## Options considered

- **Count at the response body through a RoundTripper wrapper (chosen):** exact, independent of buffering
  and scheduling, and needs no production change.
- **Tighten the socket bound** (to about `MaxAnswerBody` + read buffer + slack): rejected. The margin to a 2x
  regression is about 140 bytes (fact 5), and it would tie the test to net/http's buffer size.
- **A test seam in `HTTPTransport`** (an unexported body hook like `dial`): rejected. It adds production code
  that the wrapper makes unnecessary.
- **No change** (the flake is already gone since #351): rejected. The issue's goal, that the tests keep checking
  the 4 KiB cut, is not met: fact 4 shows a 2x regression passing.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| internal/sender (Go) | `internal/sender/transport_test.go`: `countConn`, new `countBody`, new `countBodyTransport`, `TestHTTPTransport_ReadsAtMostFourKiBOfBody` | test helpers and assertions as in *Approach* |

## Signatures (for the Dev's skeleton)

None in production code. Test helpers, written by the Tester:

- `type countConn struct { net.Conn; n *atomic.Int64; closed *atomic.Bool }` and `func (c *countConn) Close() error`
- `type countBody struct { io.ReadCloser; n *atomic.Int64; conn, connClosedFirst *atomic.Bool }`, `func (b *countBody) Read(p []byte) (int, error)`, `func (b *countBody) Close() error`
- `type countBodyTransport struct { base http.RoundTripper; n *atomic.Int64; conn, connClosedFirst *atomic.Bool }`, `func (rt *countBodyTransport) RoundTrip(req *http.Request) (*http.Response, error)`

## Test files

- `internal/sender/transport_test.go`

Existing test code that calls a changed signature: `countConn` is constructed only in this test
(transport_test.go:582), and the Tester adapts it. No other call sites.

Files of the change no unit test can cover: none.

## Areas

None: no change in behavior. `docs/areas/agent.md` already describes the cut and the closed connection, in the
transmission section from #351.

## Documentation updates

None. The sweep of `docs/areas/agent.md`, `docs/ARCHITECTURE.md` and `docs/UNIT_TESTS.md` found nothing this
change extends: no enumeration of tests or bounds, and the ratio rule for time bounds is not used.

## Architecture check

No guarantee in `docs/ARCHITECTURE.md` or `.squad/project.md` is touched: production code does not change.
The documented body cut (`docs/areas/agent.md`, transmission) is now checked exactly.

## Security considerations

- [x] No limits: no parsed input changes. The test pins the existing 4,096-byte body limit more tightly.
- [x] None: no new path to a user-visible failure reason.

## Decision records

- none: no decision beyond the obvious fix.

## Out of scope / follow-ups

- The connstats test (`TestConnections_ManySocketsStayWithinCaps`), settled by #336 (fact 8).
- A CI stress job for load-dependent tests: the issue's acceptance run is the local load run of AC4. Wiring a
  job into CI is a workflow change for a separate issue, if the orchestrator wants one.
