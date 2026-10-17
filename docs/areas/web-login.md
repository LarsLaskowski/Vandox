# Web login

## Scope

Who may use the web UI of `vandoxd`: the administrator password and its hash, sign-in and sign-out, sessions and when they end,
protection against cross-site requests, the limits on failed sign-ins, and what the service trusts from a reverse proxy in front of it.
The operator relies on it to keep the UI to themselves on a home network where the container's web port may be reachable more widely
than intended. The pages are in [Web UI](web-ui.md), the listeners and the compose file in [Backend host](backend-host.md), the secret
and the option in [Configuration and secrets](configuration-and-secrets.md), the setup of a proxy in
[REVERSE_PROXY.md](../REVERSE_PROXY.md).

## Behavior

### Password

- There is one administrator and no user name. The service knows the password only as an argon2id hash, read from the secret
  `VANDOX_WEB_PASSWORD_HASH` (or the file named by `VANDOX_WEB_PASSWORD_HASH_FILE`).
- The hash is accepted only in exactly this form:
  `$argon2id$v=19$m=<memory>,t=<iterations>,p=<parallelism>$<salt>$<hash>`, with the identifier in lower case, version 19, the three
  parameters in this order as ASCII decimal digits without sign or leading zero, salt and hash in standard base64 (`A`–`Z`, `a`–`z`,
  `0`–`9`, `+`, `/`) without padding, whose length is not 1 modulo 4 and whose unused trailing bits are zero, and nothing before or
  after. Bounds: memory 19,456 to 262,144 KiB, iterations 2 to 10, parallelism 1 to 8, salt 8 to 64 bytes, hash 16 to 64 bytes.
  Refused among others: `argon2i` and `argon2d`, a missing `v=` or another version, further parameters (`keyid=`, `data=`), base64url
  (`-`, `_`) and `=` padding.
- The service (`vandoxd` without a sub-command) checks the hash after loading the configuration and before opening the database.
  Without the secret it logs `web password hash missing` with the attribute `variable` (`VANDOX_WEB_PASSWORD_HASH`) and exits 1; with a
  refused hash it logs `web password hash invalid` with one of the reasons under *Limits and errors* and exits 1. The sub-commands and
  `-healthcheck` do not need the secret.
- A password is checked as the UTF-8 bytes of exactly the text that was sent, without normalization, and the computed hash is compared
  in constant time. At most one check runs at a time; an attempt that cannot start its check within 5 seconds is refused as throttled.
  A password longer than 1,024 characters is a failed attempt without a check.

### Creating the hash

- `vandoxd hash-password` reads the password from the first line of standard input (up to the first line feed; a carriage return
  before it is removed) and prints the hash and a line feed to standard output: memory 65,536 KiB, 3 iterations, parallelism 4, a
  random 16-byte salt and a 32-byte hash. Exit 0.
- A password with fewer than 12 or more than 1,024 characters, or with a control character, is refused with the single line
  `vandoxd hash-password: the password must have 12 to 1024 characters and no control characters` on standard error, exit 1; nothing
  is printed to standard output.
- It takes no argument and only `-h`/`-help` (usage to standard error, exit 0); any other argument is a usage error (exit 2). It
  reads no configuration file and no environment variable, and writes nothing but the hash: the password never appears on standard
  output or standard error. It does not switch off a terminal's echo, so the password is read with `read -rs` and piped into
  `docker run --rm -i` without `-t`, as the README shows; with `-t` the terminal shows what is typed.

### Access

- Every request on the web listener needs a session, except:
  - `GET /login` and the post of the login form (a `HEAD /login` without a session is answered like an unknown path, `302` to `/login`);
  - `GET` and `HEAD /healthz` ([Backend host](backend-host.md), *Health*);
  - the static files of the web UI (style sheets, scripts, brand images and the framework script; the same files for every
    installation, without data);
  - `POST /logout`.
- Without a session, a `GET` or `HEAD` request is answered `302` with `Location: /login`, also for a path that does not exist, so an
  anonymous visitor cannot tell which paths exist; the login does not carry a return address, and after signing in the user lands on
  `/`. Every other request without a session, including those of the live connection (`/_blazor`), is answered `401`.
- With a session, a path that does not exist shows the *Not found* page with status `404`; a signed-in user who opens `/login` is sent
  to `/` (`302`). A post of the login form that carries a session is sent to `/` (`302`) as well, before its antiforgery token is
  looked at: that token was issued before the sign-in and no longer matches, so a second click on the button or a login page left
  open in another tab would otherwise be refused with `400`. It signs nobody in and is not counted as an attempt.
- Every redirect names the path alone (`Location: /`, never `http://host/`): a `Location` that the framework builds as an absolute
  address of the request's own scheme and host is cut to its path, query and fragment before the response starts. The browser so
  stays on the scheme and host it used; behind a reverse proxy that terminates TLS the service sees `http` (unless the proxy is
  trusted), and an absolute `http://` redirect after the login post would leave the page's origin, which `form-action 'self'`
  blocks (the sign-in would happen, but the browser would stay on the login page). A `Location` of another scheme, host or port
  and one whose path starts with `//` or `/\` are left as they are.
- The login page is rendered on the server without a live connection and without the framework script: a plain HTML form that posts
  to `/login`. A live connection (Interactive Server) is only ever opened by a signed-in user.
- The ingest listener answers `404` to everything before any of this is looked at ([Backend host](backend-host.md), *Listeners and
  routing*).

### Sessions

- A successful sign-in creates a new session whose identifier is 32 random bytes from a cryptographic generator and is kept on the
  server. The browser receives only a protected reference to it in the cookie `vandox_session`.
- The cookie is `HttpOnly`, `SameSite=Lax`, `Path=/`, without `Expires` or `Max-Age` (it ends with the browser), and `Secure` exactly
  when the request is HTTPS as the service sees it, which behind a proxy means: `X-Forwarded-Proto: https` from a trusted proxy (see
  *Reverse proxy trust*).
- A session ends
  - 1 hour after its last activity (an authenticated request, or any event, call or navigation of one of its live connections);
  - 12 hours after the sign-in, whatever the activity;
  - on sign-out;
  - when the service stops: sessions and the keys that protect cookies and forms exist only in memory.
- At most 16 sessions exist; a further sign-in ends the oldest one. An ended session is forgotten by the server, so its cookie, also a
  copy of it, no longer signs anyone in.
- Sign-out: every page shows a *Sign out* button in its header. `POST /logout` with a valid antiforgery token ends the session on the
  server, deletes the cookie and answers `303` with `Location: /login`; without a session it only redirects.
- A live connection belongs to the session that opened it. Every event, interop call and navigation it sends is checked against the
  session first; once the session has ended, an event, interop call or navigation it sends ends the connection and is not processed.
  Not checked: the acknowledgement of a render (the framework discards the handler's result, so it is only dropped), calls that
  update root components (they bypass the inbound-activity handlers; the pages do not send them with global interactive routing)
  and the data of a transfer from the server to the browser that had already started (no page uses such transfers). A page also
  checks its session every 30 seconds and goes to `/login` when it has ended, which bounds that window.

### Cross-site requests

- Every form post (the login form, sign-out, any form of a page) must carry an antiforgery token that matches the cookie
  `vandox_csrf` (`HttpOnly`, `SameSite=Strict`, `Path=/`, `Secure` like the session cookie). A post without a valid token is answered
  `400` and has no effect; a login post without one is not counted as an attempt. The token is read only from the form field
  `__RequestVerificationToken`, never from a request header, so the body of every post is read as a form and checked first.
- A form post whose body cannot be read as a form is refused with `400` like a post without a token, before the password is looked at
  and without signing out: a value of more than 16 KiB, more than 64 values, a multipart body of more than 64 KiB (file parts
  included), a malformed body, or a body the client breaks off. No part of a form is buffered in a file.
- **The note form of the incidents view** ([Web UI](web-ui.md), *Incidents view*) is a form post under this rule: `POST /incidents/<number>/notes`
  carries the token, needs a session like every endpoint that is not listed under *Access* (so there is no new exception there: anonymous is
  `401`) and is answered `400` without a valid token. The endpoint checks the result of the token validation first, and only then reads the
  form, taking its two fields from the form and never from the query string or the route. The post of a note is the only form post besides
  sign-in and sign-out; it writes a note to the store and nothing else.
- The live connection's endpoints take no form posts and need a session. A page of another site (another registrable domain) does not
  get the session cookie sent along (`SameSite=Lax`). A page of the same site does: another service on the same host name or IP
  address, on any port, is the same site. What keeps such a page out is that a live connection renders only the components named in
  protected descriptors that the signed-in page carries, and another origin cannot read that page, just as it cannot read the
  antiforgery token of a form. A host name of its own for the UI ([REVERSE_PROXY.md](../REVERSE_PROXY.md)) keeps other web
  applications off the same site.

### Failed sign-ins

- Attempts are counted per client. The client is the address the request came from as the service sees it (after the forwarded
  headers of a trusted proxy): an IPv4 address, an IPv4-mapped IPv6 address as its IPv4 address, an IPv6 address by its /64 network.
  A request without an address counts as one shared client. Without `web.trusted_proxies`, every client behind a reverse proxy has
  the proxy's address, so five wrong passwords from any of them lock all of them out for 15 minutes.
- 5 failed attempts of a client within 15 minutes lock that client for 15 minutes from the fifth failure. While a client is locked,
  every attempt is refused without checking the password, also the right one. A successful sign-in clears the client's failures.
- 100 failed attempts of all clients together within one hour lock the sign-in for everyone until the oldest of them is one hour old.
  Anyone who can reach the login can provoke this lock, and while attempts keep arriving each one may wait up to 5 seconds for the
  check; this is the price of bounding the guesses of a client that can change its address. The counts exist only in memory: restarting
  the container clears every lock at once (and signs the user out).
- At most 4,096 clients are tracked; when the table is full, the client with the oldest last failure is forgotten.
- Answers to the login post: success `302` to `/` (*Access*); a wrong password `401` with the page and the message *The password is not correct.*;
  a locked client, the overall lock or a check that could not start in time `429` with the message *Too many sign-in attempts. Try
  again later.* The message never repeats the password.
- Log lines (JSON, [Backend host](backend-host.md), *Logging*): `login succeeded` (`client`), `login failed` (`client`, `failures`
  within the window), `login locked` (`client`, `until`), `login locked for all clients` (`until`), `logged out` (`client`), and
  `forwarded header ignored` (`client`, a warning) together with `login locked` when the locking request carried `X-Forwarded-For` that
  was not used because its sender is not in `web.trusted_proxies`: a sign that the proxy's address is missing from the option. The
  password and the hash are never logged.

### Reverse proxy trust

- The option `web.trusted_proxies` lists the addresses or networks of the reverse proxies as the container sees them
  ([Configuration and secrets](configuration-and-secrets.md)). By default it is empty: `X-Forwarded-For` and `X-Forwarded-Proto` are
  ignored from everyone, the client is the peer address, and requests are plain HTTP.
- When it is set, and the peer address is in the list (an IPv4-mapped peer address is compared as its IPv4 address), the service takes
  the last (rightmost) value of `X-Forwarded-For` as the client address and the last value of `X-Forwarded-Proto` as the scheme; it
  follows one hop only. From any other peer both headers are ignored. Loopback is trusted only when it is listed. `X-Forwarded-Host`,
  `X-Forwarded-Prefix` and `Forwarded` are never read.
- Neither the peer address, loopback nor any forwarded header ever lets a request skip the login. The forwarded headers decide only the
  client address of the failed-sign-in limits and the `Secure` flag of the cookies.
- Trusting an address means trusting everyone who connects from it. Connections through a port that Docker publishes with its userland
  proxy arrive from the gateway address of the container network, the reverse proxy's as well as any other; when that address is
  trusted, such a client can choose its `X-Forwarded-For` and so its client address, and only the overall limit of 100 failures per
  hour applies to it.

### Exposure of the web port

- The compose file publishes the web port on `127.0.0.1` ([Backend host](backend-host.md), *Compose file*); this is defense in depth,
  not the access control. Docker Engine before 28.0.0 lets neighbor hosts reach ports published on loopback, and 28.0.0 to 28.3.2 do so
  again after a firewalld reload (CVE-2025-54388); such requests arrive with the neighbor's own address. The login is the boundary
  in every case. Vandox does not rely on the binding; whether a host exposes the port is the operator's to check with the procedure
  in `docs/REVERSE_PROXY.md` (record 0105).

## Limits and errors

- Reasons of `web password hash invalid` (exact texts, never the value):
  - `must be an argon2id hash in the form $argon2id$v=19$m=<memory>,t=<iterations>,p=<parallelism>$<salt>$<hash>`
  - `version must be 19`
  - `memory must be 19456 to 262144 KiB`
  - `iterations must be 2 to 10`
  - `parallelism must be 1 to 8`
  - `salt must be 8 to 64 bytes`
  - `hash must be 16 to 64 bytes`
- The secret itself is printable ASCII of at most 4,096 bytes ([Configuration and secrets](configuration-and-secrets.md), *Secrets*);
  the form check above applies after the secret rules.
- A password check needs as much memory as the hash's memory parameter (at most 256 MiB); one check at a time keeps it within the
  container's limit.
- **The note text is never logged.** A failed save of a note is logged with its exception only (`incidents view: saving a note failed`), never the
  text of the note, a value of the form or the number the note was written for; the same holds for a refused post, which is answered with a bare
  status and no log line of its content.
- **Memory per circuit.** Every page the signed-in user opens holds one live connection (a circuit) with the data it shows, as the incident page
  does for one incident (at most about 10 MB while its charts are built, [Web UI](web-ui.md), *Limits and errors*). These amounts are per circuit,
  not across the service, and the number of circuits is not bounded; only a client with a session can open one, so exhausting the memory needs the
  password or a session.
- Limits that are constants, not options: the timeouts and counts above, the 5-second wait for a check, the 30-second check of a live
  connection, 16 sessions, 4,096 tracked clients, 16 entries in `web.trusted_proxies`, and the form limits (16 KiB per value, 64
  values, 64 KiB per multipart body).

## Related decisions

- [0103](../decisions/0103-web-login-server-side-sessions-static-login-page-circuits-bound-to-session.md) — why server-side sessions in memory, a static login page and live connections bound to the session.
- [0104](../decisions/0104-argon2id-password-hash-konscious-strict-phc-form-hash-password-command.md) — why argon2id in strict PHC form, Konscious and `hash-password`.
- [0105](../decisions/0105-forwarded-headers-only-from-trusted-proxies-failed-logins-locked-per-client-and-capped.md) — why forwarded headers only from configured proxies, the per-client lockout and the overall cap.
- [0107](../decisions/0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md) — why a note is saved by a plain form post with the token to an endpoint behind the session.
- [0016](../decisions/0016-web-ui-in-home-lan-with-login.md) — why the UI is LAN-only with a login.
- [0023](../decisions/0023-tls-through-a-reverse-proxy.md) — why TLS is terminated by a reverse proxy.
- [0060](../decisions/0060-compose-file-port-bindings-volumes-and-memory-limit.md) — why the loopback binding is not a security boundary.

## Not here

- The pages, layout, static files and security headers: [Web UI](web-ui.md).
- Listeners, port routing, `/healthz`, the compose file and the container: [Backend host](backend-host.md).
- The secret rules and the option format: [Configuration and secrets](configuration-and-secrets.md).
- How to set up nginx, Caddy, Synology's or QNAP's reverse proxy and the certificate: [REVERSE_PROXY.md](../REVERSE_PROXY.md).

## Implementation

`Vandox.Backend` (`Security/PasswordHash`, `Security/LoginService`, `Security/LoginThrottle`, `Security/SessionStore`,
`Security/SessionCircuitHandler`, `Security/SessionRevalidatingStateProvider`, `Security/ForwardedHeadersSetup`,
`Security/WebAuthentication`, `Cli/HashPasswordCommand`, `Components/Account/Login.razor`, `Components/Account/RedirectToLogin.razor`,
`Hosting/ServeCommand`, `Incidents/IncidentNoteEndpoint`), `Vandox.Core` (`Configuration/TrustedProxyList`, `Configuration/WebConfig`).
