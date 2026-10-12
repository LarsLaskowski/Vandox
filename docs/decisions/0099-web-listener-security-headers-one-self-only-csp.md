# 0099: The web listener sends one strict Content-Security-Policy with self-only sources and frame-ancestors 'none', set by the application

- **Status:** Proposed
- **Date:** 2026-10-12
- **Area:** Web UI
- **Source:** Issue #24
- **Supersedes:** —

## Context

Issue #24 asks for a Content-Security-Policy without inline scripts, `X-Content-Type-Options`, `Referrer-Policy` and
`frame-ancestors 'none'`, and for a check of what the Blazor framework script and the SignalR connection need. The web UI will show
external text (log lines, process names, incident texts; security area 12 in `.squad/project.md`); Razor escapes it, and a policy that
allows no inline code and no foreign origin is the second line if an escape is ever missed. Checked against ASP.NET Core 10.0.12 and
`blazor.web.js` of that version:

- With `AddInteractiveServerRenderMode`, the framework adds its own header `Content-Security-Policy: frame-ancestors 'self'` to page
  responses; next to an application policy the browser receives two policies. Antiforgery adds `X-Frame-Options: SAMEORIGIN` unless
  the header is already set.
- In the Interactive Server render mode the script needs no `eval` (no `'unsafe-eval'`, no `'wasm-unsafe-eval'`); it loads from the
  own origin and opens the SignalR connection (WebSocket, or HTTP as fallback) to the own origin, which `connect-src 'self'` allows in
  browsers that implement CSP Level 3 (`'self'` there also matches `ws:`/`wss:` of the same host).
- The script's default reconnect dialog creates a `<style>` element, which `style-src 'self'` blocks; it uses an element with the id
  `components-reconnect-modal` from the page instead when one exists. Styles it sets through the CSSOM (`element.style.x = …`) are not
  subject to the policy; `style` attributes in component markup are, because Blazor writes them with `setAttribute`.
- The prerendered page carries the component state in HTML comments and no inline script.

## Options considered

1. **One middleware on the web listener sets the whole policy and the other headers on every response; the framework's own
   frame-ancestors header is switched off** (chosen) — one policy to read and test, also on static files and `/healthz`.
2. **Keep the framework's header, set to `'none'`, and add a second header for the rest** — two policies whose intersection applies;
   harder to reason about and to test.
3. **Nonces or hashes to allow inline code** — per-request plumbing for code nobody needs; every script is a static file.
4. **A `<meta http-equiv>` policy in the page** — cannot carry `frame-ancestors` and does not cover static files.
5. **Leave the headers to the reverse proxy** — depends on each operator's proxy; the application knows what it needs.

How the sources are kept within the policy:

1. **Text rules over the whole source, with URLs normalized as the browser's URL parser reads them, in a test; the policy as
   backstop** (chosen) — no dependency; covers markup, component code, scripts and style sheets alike.
2. **An HTML parser package in the tests** — a new dependency; a `.razor` file is not HTML, and scripts and style sheets would
   still need text rules.
3. **The policy alone** — it does not stop markup injected through a DOM sink, a meta refresh or a navigation to another origin.

## Decision

Option 1 of each list: every response of the web listener carries exactly one `Content-Security-Policy` whose sources are only `'self'` or `'none'`
(no inline code, no `eval`, no foreign origin, `frame-ancestors 'none'`), plus `X-Content-Type-Options: nosniff`,
`Referrer-Policy: no-referrer` and `X-Frame-Options: DENY`, and a test scans the sources for the forms the policy is meant to
make unnecessary; the exact values and rules are in the [Web UI](../areas/web-ui.md) area.

## Consequences

- No component may use a `style` attribute, a `<style>` or inline `<script>` element, an HTML event-handler attribute, a URL of
  another origin or raw markup; no own script may use dynamic code or an HTML sink; styles go into the style sheets, behavior into
  static scripts. A test scanner keeps these forms out of the sources, in their case, whitespace and encoding variants, so the
  policy never has to be loosened; the policy remains the run-time backstop for what a text scanner cannot see (values computed
  at run time). The two are complementary because the policy does not stop markup injected through a DOM sink, a meta refresh or a
  navigation, and the scanner does not see run time. The rules and the split are in the Web UI area.
- A browser without CSP Level 3 matching of `ws:`/`wss:` for `'self'` falls back to the HTTP transport or cannot connect; current
  browsers are not affected.
- The reverse proxy must pass WebSocket upgrades (#25 documents the proxy setup); it may add headers, but must not replace the policy
  with a weaker one.
- The login (#25) adds its form under `form-action 'self'`; a feature that needs another source changes this record and the area.
