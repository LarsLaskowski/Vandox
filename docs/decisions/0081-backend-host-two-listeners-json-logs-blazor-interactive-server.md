# 0081: The backend host: two labelled Kestrel listeners, JSON logs, Blazor Interactive Server

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Backend host
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

Web and ingest are served on separate ports from one process, with JSON log lines, a shutdown deadline, `/healthz` and `-healthcheck` (0059, 0072). ASP.NET Core serves several ports as one application by default.

## Options considered

1. **Two hosts in one process** — isolation, but doubled configuration and lifecycle.
2. **One host, two Kestrel listeners, a middleware that routes by listener** (chosen) — each connection carries a label and a request only reaches the endpoints of its port.

## Decision

Option 2 and fail closed: an unlabelled connection or a request for an endpoint of the other port gets `404`. Logs are JSON lines through a custom logger provider with the keys of the former Go output. The UI is a Blazor Web App with the Interactive Server render mode. Details are in the [Backend host](../areas/backend-host.md) area (*Listeners and routing*, *Logging*).

## Consequences

- The ingest endpoint and the login are added to this host; the port routing is where their reachability is enforced next to the network binding (0017).
- Interactive Server keeps one live connection per open page; the number of concurrent pages is bounded by the single-user home-LAN use stated in 0016.
