# 0220: A circuit showing the overview does not extend its session

- **Status:** Accepted
- **Date:** 2026-11-16
- **Area:** Web login
- **Source:** Issue #48
- **Supersedes:** —

## Context

A session ends 1 hour after its last activity, which includes any event, call or navigation of its live connections
([Web login](../areas/web-login.md), *Sessions*; record 0103). The session check runs in the circuit handler's inbound-activity
handler, which every inbound message passes, a render acknowledgement included, and it extends the session on each pass. The overview
page (record 0219) renders a new `Updated` time every 30 seconds, so its acknowledgements would keep an unattended tab's session alive
until the 12-hour lifetime. The handler's context does not say what kind of activity it is.

## Options considered

1. **Let refreshes extend the session** — the rule would read "an open overview keeps its session"; an unattended tab on a shared
   screen stays signed in for 12 hours.
2. **Stop refreshing after a quiet period** — the page sees no user input, and the last refresh still extends the session by that
   period.
3. **Pause extension while a circuit shows the overview** (chosen) — the check still runs and still ends an ended session, but it does
   not extend it; the pause ends with the page.

## Decision

While a circuit shows the overview page, the session check of its inbound activity verifies the session without extending it; every
other activity extends it as before ([Web login](../areas/web-login.md), *Sessions*).

## Consequences

- An open overview ends its session 1 hour after the last activity elsewhere; within 30 seconds the page's session check goes to `/login`, and the user signs in again.
- A click on a navigation link of the overview does not extend the session itself; the next page's render does.
- A future self-refreshing view (history, alerts) takes the same pause.
