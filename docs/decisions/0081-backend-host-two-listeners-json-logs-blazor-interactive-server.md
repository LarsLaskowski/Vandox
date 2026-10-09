# 0081: The backend host: two labelled Kestrel listeners, JSON logs, Blazor Interactive Server

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** —
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

[0072](0072-vandoxd-import-sub-command-output-and-exit-codes.md) (superseded by
[0072](0072-vandoxd-import-sub-command-output-and-exit-codes.md)) and [0059](0059-healthz-checks-the-database-and-the-binary-is-the-health-probe.md)
describe the service: web and ingest on separate ports, JSON log lines, a 10 s shutdown deadline, `/healthz`,
`-healthcheck`. ASP.NET Core serves both ports from one process by default as one application.

## Options considered

1. **Two hosts in one process** — isolation, but doubled configuration and lifecycle.
2. **One host, two Kestrel listeners, a middleware that routes by listener** — each connection carries a label
   and a request only reaches the endpoints of its port.

## Decision

Option 2. `ListenerRoutes` labels each listener (`web`, `ingest`); `PortRoutingMiddleware` answers 404 for a
request whose connection has no label or whose label does not match the endpoint (fail closed). `/healthz`
exists only on the web port and never returns an error text. Logs are JSON lines through a custom
`ILoggerProvider` with the keys of the former `slog` output (`time`, `level`, `msg`, attributes; control
characters escaped, the exception type name in `exception`, never its text); application messages use `[LoggerMessage]` methods, and the framework's own categories (`Microsoft.*`) log from warning on unless the level is debug, so the health probe does not fill the log. The
UI is a Blazor Web App with the Interactive Server render mode; HTML escaping is the Razor default. Shutdown keeps
the 10 s deadline and logs `vandoxd stopped`.

## Consequences

- The ingest endpoints and the login (issues #40 and #25) are added to this host; the port routing is where
  their reachability is enforced next to the network binding (0017).
- Interactive Server keeps a SignalR connection per open page; the number of concurrent circuits is bounded
  by the single-user home-LAN use stated in 0016.
