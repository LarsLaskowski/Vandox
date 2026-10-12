# Web UI

## Scope

The pages `vandoxd` serves on its web listener: render mode, layout and navigation, theme, brand files, static assets, charts, and the
security headers of the web listener together with what the UI framework needs from them. The user relies on it to look at the
monitored server in a browser in the home network; the views (overview, history, logs, incidents) build on it. The listeners, port
routing and `/healthz` are in [Backend host](backend-host.md); the login and the reverse proxy setup are not implemented yet (#25).

## Behavior

### Pages and render mode

- The web UI is a Blazor Web App. Every page is prerendered on the server and then runs in the **Interactive Server** render mode: it
  holds one live connection (SignalR: WebSocket, HTTP as fallback) to the same origin while it is open.
- The page sets `<base href="/">`, a `viewport` meta element for device width, and loads only the files listed under *Static assets*.
- When the live connection is lost, the page shows its own reconnect dialog (the element `components-reconnect-modal`) with the states
  reconnecting, failed and rejected, and a way to retry or reload; it shows its own error banner (`blazor-error-ui`) on an unhandled
  error. Both are styled by the style sheet, not by the framework.

### Layout and navigation

- Every page has a header with the horizontal logo, the main navigation and the theme switch, and a main area with the page content.
- The navigation lists the existing pages; the entry of the current page is marked. Today it has one entry, *Overview* (`/`); each view
  adds its own.
- The layout works from desktop width down to phone width (360 CSS pixels) without horizontal scrolling of the page: on narrow screens
  the navigation moves below the logo and the theme switch. It needs no script.

### Theme

- The theme follows the system (`prefers-color-scheme`) until the user chooses one. The switch in the header cycles through
  *System*, *Light* and *Dark*; the choice is kept in the browser's local storage and applied before the page is first drawn, so there
  is no flash of the other theme. Without local storage the choice lasts until the page is reloaded.
- Colors come from the design tokens of [BRANDING.md](../BRANDING.md): `--vandox-color-brand` and `--vandox-color-ink` with their light
  and dark values; a manual choice sets the same values as the system setting.
- The header shows `vandox-logo-horizontal.svg` in the light theme and `vandox-logo-horizontal-dark.svg` in the dark theme; the favicon
  is `vandox-favicon.svg`, which switches its colors itself.

### Static assets

- Every file the browser loads comes from the application itself; the UI references no other origin, no CDN and no web font. There is
  no Node build: style sheets and scripts are written as served.
- Files: the framework script `_framework/blazor.web.js`, `app.css`, `js/app.js` (theme and reconnect controls), `js/charts.js` (chart
  module, loaded only by a page that shows a chart), `lib/uplot/` (uPlot 1.6.32: `uPlot.esm.js`, `uPlot.min.css` and its `LICENSE`)
  and `brand/` (byte-identical copies of `vandox-favicon.svg`, `vandox-logo-horizontal.svg` and `vandox-logo-horizontal-dark.svg` from
  `docs/assets/`, the single source).
- The same files are served from a build and from the published image. Static files are served only on the web listener.

### Charts

- A time-series chart takes a list of times in ascending order and one or more labelled series with one value per time. A missing value
  is drawn as a gap, never as zero or as a line across the gap.
- Data with a series whose length differs from the times, times that are not strictly ascending, a value that is not a finite number,
  or no series is refused as a programming error before anything reaches the browser.
- A chart takes its width from its container and follows its resizing; its colors come from the theme tokens and follow a theme change.
  Labels and titles are written as text, never as HTML.

### Security headers

Every response of the web listener (pages, static files, `/healthz`, the live connection's HTTP responses) carries exactly these
headers, and the page carries no other `Content-Security-Policy`:

| Header | Value |
| ------ | ----- |
| `Content-Security-Policy` | `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'` |
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `no-referrer` |
| `X-Frame-Options` | `DENY` |

What the framework needs, and why the policy is enough:

- `script-src 'self'`: the framework script, `js/app.js` and the chart module with uPlot are files of the own origin; no inline script,
  no `eval` (the Interactive Server mode needs none).
- `connect-src 'self'`: the live connection goes to the own origin; in browsers with CSP Level 3, `'self'` also matches `ws:`/`wss:` of
  the same host, so the WebSocket needs no scheme source.
- `style-src 'self'`: the style sheets are files. The framework's built-in reconnect dialog would insert a `<style>` element, which the
  policy blocks, so the page brings its own dialog. Styles that scripts set through the CSSOM are not affected; `style` attributes in
  markup are, so components never use them.
- `img-src 'self'`: the logos and the favicon. Charts draw on a canvas.
- `base-uri 'self'` allows the page's `<base href="/">`; `form-action 'self'` allows forms that post to the own origin.
- `frame-ancestors 'none'` and `X-Frame-Options: DENY`: no page may be framed.

The ingest listener serves none of this: every UI path, static file and the live connection's endpoints answer `404` there
([Backend host](backend-host.md), *Listeners and routing*).

## Limits and errors

- Component markup contains no `style` attribute, no `<style>` element, no `<script>` element without `src`, no HTML event-handler
  attribute and no `javascript:` URL; the own scripts contain no URL of another origin, no `eval`, `new Function` or `innerHTML`; no
  style sheet uses `@import` or a `url()` of another origin. Tests check the sources.
- A browser without CSP Level 3 matching of `ws:`/`wss:` falls back to HTTP for the live connection or cannot connect.
- The reverse proxy must pass WebSocket upgrades for the live connection.
- Opened directly as a document, the favicon's own `<style>` element is blocked by the policy and the icon shows without its colors; as
  an icon or image it is not affected.

## Related decisions

- [0081](../decisions/0081-backend-host-two-listeners-json-logs-blazor-interactive-server.md) — why Blazor in the Interactive Server render mode on labelled listeners.
- [0051](../decisions/0051-brand-assets-in-docs-assets-web-ui-and-telegram-with-their-features.md) — why `docs/assets/` is the single source of the logo.
- [0098](../decisions/0098-web-ui-serves-only-its-own-static-web-assets.md) — why own static web assets only, brand copies, vendored uPlot and a script-set theme.
- [0099](../decisions/0099-web-listener-security-headers-one-self-only-csp.md) — why one self-only policy set by the application.

## Not here

- Listeners, port routing, `/healthz`, the container and exposure: [Backend host](backend-host.md).
- Who may reach the web UI: [Network and security model](network-and-security-model.md).
- Logo files and color tokens: [BRANDING.md](../BRANDING.md).
- Login, sessions, CSRF and the reverse proxy setup: not implemented yet (#25).

## Implementation

`Vandox.Backend` (`Components/App.razor`, `Components/Layout/MainLayout.razor`, `Components/Charts`, `Hosting/SecurityHeadersMiddleware`,
`Hosting/ServeCommand`, `wwwroot/`).
