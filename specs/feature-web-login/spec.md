# Spec: Web UI login and operation behind a reverse proxy

Status: Draft

Source: Issue #25 (labels area: web, area: security, type: security; depends on #24, closed) and the owner's comment on it.

## Problem / motivation

The web UI (#24) is served to whoever reaches the web port: no login, no session, no CSRF protection beyond the framework's default
on component forms, and the forwarded headers of a reverse proxy are not read. The compose file publishes the port on `127.0.0.1`,
but that is defense in depth only (record 0060): depending on the Docker Engine version and the host firewall, LAN hosts may reach the
published port or the container address directly. Only the maintainer may use the UI, and running behind the TLS-terminating reverse
proxy (record 0023) must be secure without trusting the network position of a request.

## Behavior

The normative rules, with every value and message, are in [`docs/areas/web-login.md`](../../docs/areas/web-login.md); this is the
summary from the user's side.

- **One administrator password.** The operator creates an argon2id hash with `vandoxd hash-password` (password on standard input) or
  any tool that writes the reference PHC form, and supplies it as the secret `VANDOX_WEB_PASSWORD_HASH` (or `_FILE`). Without it, or
  with a hash outside the accepted form and bounds, the service refuses to start with a log line that names the problem but never the
  value. The sub-commands and the health probe do not need it.
- **Everything needs a session** except the login page, `/healthz`, the UI's static files and the sign-out post. An anonymous browser
  is redirected to `/login`; requests that are not page loads get `401`. The login page is plain server-rendered HTML; the live
  connection of the interactive pages exists only for a signed-in user.
- **Sessions** live on the server, end after 1 hour without activity, 12 hours after sign-in, on sign-out and on a restart, and the
  cookie is `HttpOnly`, `SameSite=Lax`, a browser-session cookie, and `Secure` when the request came over HTTPS through a trusted
  proxy. Sign-out ends the session on the server, so a copied cookie stops working too. A live connection is closed as soon as its
  session has ended and processes nothing after that; an idle page goes to the login within 30 seconds.
- **CSRF:** every form post carries an antiforgery token; without a valid one it is refused with `400` and has no effect.
- **Failed sign-ins:** 5 failures of one client within 15 minutes lock that client out for 15 minutes; 100 failures of all clients
  within an hour lock everyone out until they age out. The client is the real client address: the peer address, or the address a
  trusted proxy reports.
- **Reverse proxy:** `X-Forwarded-For` and `X-Forwarded-Proto` are used only from the addresses in the new option
  `web.trusted_proxies` (empty by default: ignored from everyone). They never skip the login.
- **Documentation:** a reverse proxy guide (`docs/REVERSE_PROXY.md`) with a generic part (nginx, Caddy), Synology's and QNAP's built-in
  proxies, the certificate, WebSocket support for the live connection, how to find the proxy's address for `web.trusted_proxies`, and
  how an operator checks whether the Docker Engine exposes the loopback-published port to the LAN. README, compose file, example
  configuration and `SECURITY.md` say how to create and mount the hash.

## Acceptance criteria

The issue's five criteria, made testable (plan.md maps each to its tests):

- [ ] AC-S1: Every page except the login page and `/healthz` requires a session (static files and the sign-out post are public by
  design, record 0103); anonymous page loads go to `/login`, other anonymous requests get `401`.
- [ ] AC-S2: The cookies are `HttpOnly` and `SameSite`, and `Secure` exactly when the request came over TLS through a configured proxy;
  the client address behind the proxy is the one the proxy reports; forwarded headers from any other peer, loopback included, are
  ignored (tested).
- [ ] AC-S3: The CSRF token is checked on every form post (login, sign-out, page forms).
- [ ] AC-S4: Five failed attempts lead to a temporary lockout of that client.
- [ ] AC-S5: Session expiry (idle and absolute) and logout are tested, logout invalidates the session on the server, and live
  connections do not outlive the session.
- [ ] AC-S6: The service refuses to start without a valid password hash; `vandoxd hash-password` creates one.
- [ ] AC-S7: The reverse proxy documentation exists (generic, Synology, QNAP, certificate, WebSocket).

## Out of scope

- More than one user, roles, password change in the UI, two-factor sign-in, "remember me".
- Access from outside the home network (record 0016).
- A `Host` header allow-list and an `Origin` check of the live connection (record 0103, *Options considered*).
- TLS in `vandoxd` (record 0023).
- Persisting sessions across restarts.

## Open questions

- The owner asked to verify on the supported NAS Docker versions whether a port published on `127.0.0.1` is reachable from the LAN and
  to record the result. The squad cannot run that check (no NAS, no Docker daemon in the session). Record 0105 states Docker's own
  release notes (fixed in Engine 28.0.0, regressed with firewalld reloads until 28.3.3) and marks the NAS versions as unverified;
  `docs/REVERSE_PROXY.md` gives the check procedure. Proposed follow-up issue for the owner: run the procedure on Synology Container
  Manager and QNAP Container Station and amend record 0105 with the measured result. The design does not depend on the answer: the
  login is the boundary either way.
