# Spec: Web UI foundation

Status: Approved by Lead

Source: issue #24 (and its comment from #12)

## Problem / motivation

v0.1.0 is the forensics release; its views (logs #26, incidents #27) and the later ones (#48 overview, #49 history and more) need a
common web UI shell. The backend has a Blazor shell (`Components/`, a home page, a copy of the favicon), but nothing in it is interactive,
its static files are not found when the service runs from the build output, it sends no security headers of its own, there is no
navigation, no theme switch and no charting. The UI is reachable in the home LAN behind a TLS-terminating reverse proxy and must load
nothing from the internet.

## Behavior

- Every page is a Blazor page in the Interactive Server render mode, prerendered, with one live connection to its own origin.
- A base layout: header with the horizontal logo (light or dark file per theme), the main navigation (today: *Overview*) and a theme
  switch; the page content below. It works from desktop width down to a phone (360 CSS pixels) without page-wide horizontal scrolling.
- The theme follows the system until the user picks *Light* or *Dark* (the switch cycles System, Light, Dark); the choice survives a
  reload and is applied before the first paint.
- Favicon `vandox-favicon.svg`; header logos `vandox-logo-horizontal.svg` and `vandox-logo-horizontal-dark.svg`; colors from the
  `--vandox-color-*` tokens of `docs/BRANDING.md`. The served files are byte-identical to `docs/assets/`.
- Every file the browser loads comes from `vandoxd` itself: the framework script, the style sheets, two own scripts, uPlot and the brand
  files. No CDN, no web font, no Node build.
- A reusable time-series chart component draws with uPlot through JS interop; gaps (missing values) stay visible as gaps.
- Every response of the web listener carries one strict Content-Security-Policy (no inline script or style, only the own origin,
  `frame-ancestors 'none'`), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and `X-Frame-Options: DENY`; what the
  framework script and the SignalR connection need is documented in the Web UI area.
- The web port keeps answering `/healthz`; nothing of the UI (pages, static files, the live connection) is served on the ingest port.
- A lost live connection shows the page's own reconnect dialog (the framework's built-in one would need an inline style).

## Acceptance criteria

- [ ] AC1 (issue: "No external resources at runtime"): the policy allows only the own origin, the page references only local files and
  has no inline code, components, scripts and style sheets reference no other origin — `plan.md` AC1 to AC4.
- [ ] AC2 (issue: "Layout works on desktop and phone"): the layout markup and assets are in place (`plan.md` AC5, AC6) and the layout is
  checked in a browser at desktop and 360 px width (`plan.md`, *Verification without tests*).
- [ ] AC3 (issue: "Security headers set and covered by a test"): `plan.md` AC7 and AC8.
- [ ] AC4: static assets are served on the web port from the build output and the image, not on the ingest port; `/healthz` unchanged
  — `plan.md` AC9.
- [ ] AC5: charts — `plan.md` AC10 to AC12.

## Out of scope

- Login, sessions, CSRF on forms, forwarded headers, HSTS and the proxy documentation (#25).
- The views themselves (#26, #27, #48 to #54, #62) and their navigation entries.
- A raster favicon, a web app manifest, `Permissions-Policy` and `Cross-Origin-*` headers.

## Open questions

None. The comment's advice on `go:embed` and the Go Dockerfile predates the move of the backend to .NET (0073) and does not apply; the
choice it leaves open is made in record 0098.
