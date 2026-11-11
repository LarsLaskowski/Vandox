# 0198: Razor component endpoints answer HEAD from .NET 11 on; the login page answers HEAD without a session

- **Status:** Proposed
- **Date:** 2026-11-11
- **Area:** Web login
- **Source:** Product Manager request: move the backend to .NET 11 (feature dotnet-11-upgrade)
- **Supersedes:** [0103](0103-web-login-server-side-sessions-static-login-page-circuits-bound-to-session.md) — amends its
  context only: "Razor component endpoints accept GET and POST" holds for ASP.NET Core 10; from 11.0.0 on they accept GET,
  HEAD and POST. Every decision of 0103 stands.

## Context

0103 was built on facts read from ASP.NET Core 10.0.12. Read again in 11.0.0 (feature dotnet-11-upgrade, AC9), all of them
hold but one: `RazorComponentEndpointFactory` adds `HttpMethodMetadata` with GET, HEAD and POST (10.0.0: GET and POST). On .NET
10 a `HEAD` of a page matched no endpoint, so the fallback policy answered it: `302` to `/login` without a session, also for
`/login` itself ([web-login](../areas/web-login.md), *Access*). On .NET 11 the anonymous `/login` endpoint answers `HEAD` like
`GET`, without a body. Every page that requires a session still answers `302` without one: authorization, not the method,
decides that. The sign-in page is the same for every installation and holds no data.

## Options considered

1. **Accept the framework's HEAD and document it** (chosen) — no code; `HEAD /login` tells nothing that `GET /login` does not.
2. **Restrict component endpoints to GET and POST again** with an endpoint convention — today's answer kept, but own code in
   security area 3 that works against the framework's routing and has to follow every later framework change.
3. **Refuse HEAD in a middleware** — the same result as option 2, at a second place that has to know the page routes.

## Decision

Component endpoints keep the framework's methods; a `HEAD /login` without a session is answered like `GET /login` without a
body, and every other `HEAD` without a session stays `302` to `/login`. With a session, a `HEAD` request to a page is answered
like its `GET`, without a body (from .NET 11 on). The rules are in [web-login](../areas/web-login.md),
*Access*.

## Consequences

- A test pins both answers, so a later framework change of the methods shows up as a failing test.
- A signed-in `HEAD` of a page renders it like a `GET`, its data loading included, without a body, instead of being refused
  (`405` on .NET 10); the authorization is the same as for `GET`, and a page render writes nothing.
