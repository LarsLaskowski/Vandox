# 0199: The live connection's session check follows authentication state changes instead of reading the state per event

- **Status:** Proposed
- **Date:** 2026-11-11
- **Area:** Web login
- **Source:** Product Manager request: move the backend to .NET 11 (feature dotnet-11-upgrade)
- **Supersedes:** —

## Context

0103 binds every live connection to its session: `SessionCircuitHandler` checks the session before each inbound event and
ends the connection when it has ended. It reads `GetAuthenticationStateAsync` on every event. The 11 SDK's rule BL0013 reports
a component or service that reads the authentication state without subscribing to `AuthenticationStateChanged`, because a
copy of the state can go stale. Reading on every event never goes stale, but the rule cannot tell. The handler and the
authentication state provider are both scoped to the circuit, and the circuit's scope is disposed when it closes. The event
does not come from the circuit's dispatcher: SignalR hub threads raise it when a connection attaches to the circuit or
refreshes its authentication (`ComponentHub` → `SetCircuitUser`), and the revalidation loop of
`SessionRevalidatingStateProvider` raises it on a thread-pool thread when it signs a session out; the inbound-activity
handler runs before the dispatcher too. A lazily read initial state could therefore overwrite a state announced during that
read.

## Options considered

1. **Subscribe to `AuthenticationStateChanged`, keep the latest state, read the provider once for the initial state, and
   unsubscribe when the handler is disposed** (chosen) — the form the rule asks for; the state checked is the one the
   provider last announced, as before.
2. **Suppress BL0013 on the handler with a justification** — no code change in a security control, but the rule is silenced
   for every later edit of the class, the one place where a stale state matters most.
3. **Subscribe only to satisfy the rule and keep reading per event** — an unused subscription, code that says one thing and
   does another.

## Decision

`SessionCircuitHandler` follows the provider's `AuthenticationStateChanged` and checks each event against the latest announced
state; before any change it reads the initial state from the provider at the first event and keeps it only if no change was
announced meanwhile (compare-exchange, volatile read); when no state can be read, the event
is not processed; it stops following the provider when the circuit's scope disposes it. The rules are in
[web-login](../areas/web-login.md).

## Consequences

- The handler implements `IDisposable`; tests change the state of their provider double and check the next event.
- A provider that announced no change and cannot give a state ends the connection, as an ended session does.
