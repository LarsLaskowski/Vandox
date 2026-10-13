# 0103: Web login: one password, server-side sessions in memory, a static login page, live connections bound to the session

- **Status:** Proposed
- **Date:** 2026-10-13
- **Area:** Web login
- **Source:** Issue #25
- **Supersedes:** —

## Context

The web UI is reachable in the home LAN and needs a login (0016); TLS is terminated by a reverse proxy (0023). The loopback binding of
the web port is defense in depth only (0060): depending on the Docker Engine version and the host firewall, other hosts may reach the
port, so the login has to protect every route except `/healthz` (0059) on its own. Issue #25 asks for a single admin user whose
password hash is a secret, the framework's cookie authentication and antiforgery, server-side session expiry with invalidation on
logout, and Interactive Server circuits (0081) that do not outlive the session. The service runs in a container with a read-only root
file system; the only writable place is the database volume.

ASP.NET Core 10.0.12, read from its sources: the cookie handler keeps the whole ticket in the cookie unless a session store
(`ITicketStore`) is set; Razor component endpoints accept GET and POST and require an antiforgery token for a form post; the
authorization middleware applies the fallback policy also to a request that matched no endpoint; an exception thrown by a circuit
handler's inbound-activity handler is reported as an unhandled circuit exception, and the circuit registry then terminates the circuit
(every event, interop call and navigation passes that handler; a render acknowledgement does too, but is not awaited).

## Options considered

Where a session lives:

1. **Only in the cookie (the framework's default)** — no server state, but sign-out only deletes the cookie: a copied cookie stays
   valid until it expires, which the issue rules out.
2. **A server-side session store in memory; the cookie holds a protected reference** (chosen) — sign-out and expiry end the session
   on the server; sessions end with a restart, which for one user is a sign-in, not a loss.
3. **Sessions in SQLite** — survive a restart, but a write per request for the idle timer, a schema migration and session data next to
   the monitoring data, for a convenience nobody asked for.

Data protection keys (they protect the cookie reference and the antiforgery tokens):

1. **Ephemeral, in memory** (chosen) — consistent with in-memory sessions; nothing secret is written to the volume; a restart
   invalidates open forms and sessions.
2. **Persisted under `/data`** — key files next to the database, useful only if sessions survived a restart.

The login page:

1. **A statically rendered component that handles its own form post** (chosen) — no live connection before authentication, the
   framework's antiforgery check on the post, the same layout and style sheet as the other pages.
2. **A minimal API endpoint for the post** — the component endpoint already owns GET and POST of `/login`, so the routes would be
   ambiguous, and a second path adds a redirect round trip.
3. **An interactive login page** — opens a live connection for an anonymous visitor, which is what the login is meant to prevent.

Protecting the routes:

1. **A fallback authorization policy that requires a signed-in user, with explicit exceptions** (chosen) — a new endpoint is protected
   unless it opts out; also unknown paths redirect to the login, so an anonymous visitor cannot probe which paths exist.
2. **`[Authorize]` on each page** — a forgotten attribute is an open page; kept in addition for the pages, because a live connection
   routes between pages without a new request.

Live connections and the session:

1. **Check the session on every inbound activity of the circuit and end the circuit when it ended; revalidate the authentication state
   every 30 seconds so an idle page leaves to the login** (chosen) — the inbound check is exact and needs no timer; the revalidation
   covers a page that shows data without the user doing anything.
2. **Revalidation alone** — up to one interval of events processed after the session ended.
3. **A per-circuit timer that kills the circuit at the expiry time** — there is no public API to terminate a circuit from outside.

Also considered and not done: an `Origin` check on the live connection's endpoints (a proxy that rewrites the `Host` header would
break the UI, and SameSite cookies, the session requirement and component descriptors that only the signed-in page can read already
keep other sites out), and a return address after the login redirect (an open-redirect check for little gain with one user).

## Decision

The web UI uses ASP.NET Core cookie authentication with an in-memory session store, ephemeral data protection keys, a fallback policy
that requires a signed-in user with exceptions for the login page, `/healthz`, the static files and the sign-out post, a statically
rendered login page, and a circuit handler plus a revalidating authentication state provider that bind every live connection to its
session. Timeouts, cookie flags, limits and messages are in the [Web login](../areas/web-login.md) area.

## Consequences

- A restart of the service signs the user out and invalidates open forms; the session limit and timeouts are constants, not options.
- Every new endpoint is protected by default; one that must be public opts out explicitly and is listed in the area.
- The login page loads no framework script and opens no live connection, so `/login` works without WebSockets.
- The static files of the UI are public: they are the same files as in the published image and carry no data.
- A cookie copied while a session is active is usable from another client until the session ends (it is not bound to the client
  address, which changes legitimately behind proxies and on mobile devices).
