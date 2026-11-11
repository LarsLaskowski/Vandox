# Plan: Web login: root-component updates do pass the live connection's session check

Source: Issue #416
Tier: trivial — a correction of the area document of a security control plus a decision record, with no change to behavior, code or tests.
Security areas touched: Web UI login (`.squad/project.md`, area 3) — only `docs/areas/web-login.md` and a record; no security control is created, changed or removed.

## Problem / root cause

`docs/areas/web-login.md` (*Sessions*) is wrong twice: `:91–93` says updates of root components skip the session check and the
pages send none; `:89` says a live connection belongs to the session that opened it, while a reconnect gives it the session of the
new connection's cookie. The code is right in both cases (record 0202); only the document changes.

## Verified facts

Shallow clones of `dotnet/aspnetcore` `v10.0.0` (7387de9) and `v11.0.0` (1def4cd) outside the working tree; paths under
`src/Components/Server/src/`, lines of v11.0.0 [v10.0.0].

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Root-component operations pass the circuit handlers' inbound pipeline (issue claim) | `Circuits/CircuitHost.cs:880–894` [877–891] runs them inside `HandleInboundActivityAsync`, `:655`, dispatcher `:666–683` | confirmed |
| 2 | A failed check ends the circuit, the operations do not run | `CircuitHost.cs:850–856` catch → `UnhandledException` → `Circuits/CircuitRegistry.cs:407–414` `TerminateAsync` | confirmed |
| 3 | The pages send no root-component update (document claim) | `Web.JS/src/Services/WebRootComponentManager.ts:72` (no initial components), `Boot.Server.Common.ts:185–197`, `CircuitManager.ts:97–104`: each page's start sends one; `CircuitHost.cs:823–830` takes the handlers at that first update | refuted |
| 4 | The component state such an update carries is restored before or regardless of the check; Vandox keeps no such state | `CircuitHost.cs:790–830`, `:808–811`, `:842`; `grep -rn "PersistentComponentState\|PersistentState" src/` empty | confirmed |
| 5 | `ConnectCircuit` gives the circuit the new connection's user (issue claim) | `ComponentHub.cs:259` [230], `CircuitHost.cs:630–640`; followed by `Security/SessionCircuitHandler.cs:32,73–76` | confirmed |
| 6 | `OnAuthenticationRefreshedAsync` calls `SetCircuitUser`, also in 10.0.0 | `ComponentHub.cs:98–104`; absent in v10.0.0 | refuted |
| 7 | A refresh can switch the circuit to another session | `Builder/ComponentEndpointRouteBuilderExtensions.cs:78`; `SignalR/common/Http.Connections/src/Internal/HttpConnectionContext.cs:41,493–517` accept only unchanged claims without `sub`/`NameIdentifier`/`Upn`; Vandox's claims are `Name` and `vandox:session` (`Security/LoginService.cs:123`). The document does not rest on this | refuted |
| 8 | A reconnect needs an active session and the circuit's secret | `Security/WebAuthentication.cs:84` fallback policy, `web-login.md:56`; `ComponentHub.cs:240–247` | confirmed |
| 9 | The render acknowledgement's check result is discarded | `CircuitHost.cs:389` | confirmed |

The issue's line numbers (90–91, 88) are `:91–93` and `:89` today.

## Acceptance criteria

- AC1: *Sessions* in `docs/areas/web-login.md` lists updates of root components among the calls checked against the session (ended
  session: the connection ends, the call is not processed), says each page sends one when its live connection starts, and no
  longer says they skip the check; *Not checked* adds the restore of component state such a call carries (before the check; no
  page keeps such state) beside the render acknowledgement and started transfers.
- AC2: *Sessions* says a live connection is checked against the session of the connection it runs on — the opening one, after a
  reconnect the one of the new connection's cookie, which must be active — and no longer that it belongs to the session that
  opened it; *Related decisions* links 0202.

## Verification without tests

No production or test code: steps 4, 5 and the *Coverage gate* are not applicable.

- AC1: check: the Reviewer (step 8) reads the new text against rows 1–4 and 9.
- AC2: check: the Reviewer (step 8) reads it against rows 5, 7 and 8 and the link to 0202.

## Approach

The Dev rewrites the bullet `web-login.md:89–95`, in this order: the binding (AC2; the browser's one cookie for all tabs is why a
reconnect after a new sign-in carries the new session), the checked calls (AC1), *Not checked*, the 30-second sentence unchanged.
Wording is the Dev's within rows 1–9. No new output of the product.

## Options considered

1. **Correct the document, keep the behavior** (chosen) — record 0202.
2. **Pin each live connection to its opening session** (the issue's "or the behavior") — closes reconnecting tabs after a new
   sign-in and keeps no access from anyone, since every session of the one administrator grants the same (0202).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| — | `docs/areas/web-login.md` | *Sessions* bullet, *Related decisions* row |

## Signatures (for the Dev's skeleton)

None: no code changes.

## Test files

None. Call sites of changed signatures: none. Files no unit test can cover: none.

## Areas

Web login: *Sessions* corrected, no change in behavior.

## Documentation updates

- `docs/areas/web-login.md` (Dev): *Sessions* as in *Approach*; under *Related decisions*, after 0199:
  `[0202](../decisions/0202-a-reconnected-live-connection-follows-the-session-of-its-new-connection.md) — why a reconnected live connection follows the session of its new connection.`
- Sweep, no edit: `docs/ARCHITECTURE.md:335–345` (login, not live connections), `docs/areas/web-ui.md` (reconnect dialog only),
  records 0103 (released; amended by 0202) and 0199 (no root-component claim); `CHANGELOG.md` changes only at a release.

## Architecture check

No guarantee of `.squad/project.md` is affected; security area 3 (Web UI login) holds: a live connection never runs without an active session (rows 2, 8).

## Security considerations

- [x] No limits.
- [x] None: no code path changes.

## Decision records

- `docs/decisions/0202-a-reconnected-live-connection-follows-the-session-of-its-new-connection.md` (Proposed), amends 0103.

## Out of scope / follow-ups

None.

## Security notes

- Non-blocking (fix now, a one-line edit in plan.md): *Architecture check* says "Guarantee 3 of `.squad/project.md` holds". But `.squad/project.md` *Guarantees* (lines 94–110) has three guarantees, and the third is "Backfilled data never raises an alert by itself". No guarantee is about sessions; the plan means security area 3. Suggested text: `No guarantee of .squad/project.md is affected; security area 3 (Web UI login) holds: a live connection never runs without an active session (rows 2, 8).`
