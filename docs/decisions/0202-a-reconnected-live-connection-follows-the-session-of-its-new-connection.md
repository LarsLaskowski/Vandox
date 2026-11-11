# 0202: A reconnected live connection follows the session of its new connection

- **Status:** Accepted
- **Date:** 2026-11-11
- **Area:** Web login
- **Source:** Issue #416
- **Supersedes:** 0103 — amends it: the session a live connection is bound to is the one of the connection it runs on, which
  a reconnect can change.

## Context

[0103](0103-web-login-server-side-sessions-static-login-page-circuits-bound-to-session.md) binds every live connection (circuit)
to its session, and the area said a live connection belongs to the session that opened it. ASP.NET Core 10.0.0 and 11.0.0 do not
work that way: when the page reconnects a circuit over a new SignalR connection, `ComponentHub.ConnectCircuit` sets the circuit's
user to the user of the new connection (`SetCircuitUser(Context.User)`), and from 11.0.0 on `OnAuthenticationRefreshedAsync` does
the same after a refresh of the connection's authentication (which SignalR accepts, for a principal without `sub`,
`NameIdentifier` or `Upn` claim like Vandox's, only with unchanged claims, so a refresh keeps the session). `SessionCircuitHandler` follows that state
([0199](0199-session-circuit-handler-follows-authentication-state-changes.md)), so the circuit is checked against, and keeps
alive, the session of the new connection's cookie. The browser keeps one `vandox_session` cookie for all its tabs: a tab whose
connection dropped while the user signed in again (in another tab, or after the old session ended) reconnects with the new
session. A reconnect needs the circuit's protected secret, and the new connection needs an active session of its own, since
`/_blazor` falls under the fallback policy. There is one administrator, so every session grants the same access.

## Options considered

1. **Keep the framework's behavior and correct the area** (chosen) — a circuit never runs without an active session, and whoever
   can take a circuit over holds an active session and could open the same pages directly; no code in the security control
   changes.
2. **Pin each circuit to the session that opened it**: `SessionCircuitHandler` remembers the session of its first state and ends
   the circuit when an announced state names another session — closes the tabs that reconnect after a new sign-in, with no access
   kept from anyone (the holder of the new session already has it), and adds a branch to the security control.

## Decision

A live connection is bound to the session of the connection it currently runs on: the one that opened it, and after a reconnect
the one of the new connection's cookie. The rules are in [web-login](../areas/web-login.md) (*Sessions*).

## Consequences

- Signing out ends the session, not every circuit that ever ran on it: a circuit that reconnected under a newer session keeps
  running until that session ends.
- The session check still runs on every event, interop call, navigation and update of root components, against whichever
  session the circuit currently has.
- Revisit when Vandox gets more than one user: then a reconnect under another user's session would hand over a circuit, and
  option 2 (or a check of the user, not only the session) becomes necessary.
