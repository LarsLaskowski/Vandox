# 0098: The web UI serves only its own static web assets: brand copies checked against docs/assets, uPlot vendored, theme and reconnect without inline code

- **Status:** Proposed
- **Date:** 2026-10-12
- **Area:** Web UI
- **Source:** Issue #24
- **Supersedes:** —

## Context

Issue #24 asks for a lean web UI foundation without a Node build and without external resources: a Blazor Web App in the Interactive
Server render mode (0081), uPlot for charts through JS interop, every asset served by the application itself, a base layout with
navigation, a light/dark theme that follows the system and can be switched, the logo and favicon from `docs/assets/` (0051), and a
strict Content-Security-Policy (0099). When the plan was written, the shell had a layout, a home page and a copy of the favicon in
`wwwroot` without a check against `docs/assets/`; no component had a render mode, so nothing was interactive; and the static files were
served by `UseStaticFiles`, which in .NET 10 finds the framework script `_framework/blazor.web.js` and the project's `wwwroot` only in a
published build: run from the build output, as every test runs it, the page answered 200 and every asset 404 (checked by running the
build output and a publish). The image build context is an allow-list that contains `src/` but not `docs/`. The comment on #24 about
`go:embed` and the Go Dockerfile describes the backend before 0073 and no longer applies.

## Options considered

How the brand files reach the application:

1. **Byte-identical copies under `wwwroot/brand/`, checked by a test against `docs/assets/`** (chosen) — no build or image change;
   `docs/assets/` stays the single source and a drifted copy fails the build.
2. **Link the files from `docs/assets/` into the static web assets through MSBuild, and add `docs/assets/` to `.dockerignore` and the
   Dockerfile** — no copy, but it changes the image build (a security area) and relies on static web asset discovery of linked items
   outside the project.
3. **Move the single source into `wwwroot`** — contradicts 0051; the README and `docs/BRANDING.md` point into `docs/assets/`.

How the assets are served:

1. **`MapStaticAssets` with the static web assets manifest loaded explicitly (`UseStaticWebAssets`)** (chosen) — the same endpoints
   from the build output and from a publish, precompressed files and ETags; checked in both by running them.
2. **Keep `UseStaticFiles`** — assets missing from the build output, so no test sees what the browser loads.

How uPlot gets into the application (no Node build, no CDN):

1. **The ES module build `dist/uPlot.esm.js`, `dist/uPlot.min.css` and `LICENSE` of the uPlot release tag 1.6.32, vendored under
   `wwwroot/lib/uplot/` and pinned by SHA-256 in a test** (chosen) — loaded only by the chart module when a page shows a chart; its
   1.6.32 source sets styles only through the CSSOM and writes no HTML, so it runs under the policy of 0099.
2. **The minified IIFE build as a global script on every page** — smaller file, but loaded on pages without charts and a global.
3. **LibMan or a package restore at build time** — fetches from a CDN during the build; no official NuGet package exists.

How the theme is applied:

1. **A small static script in the page head that sets the stored choice on the root element before the first paint, CSS tokens for
   light and dark** (chosen) — no flash of the wrong theme and no server round trip.
2. **A Blazor component with JS interop** — the theme would apply only after the circuit starts.
3. **A cookie read on the server** — request-dependent rendering and a cookie before the login exists.

Charts in the foundation: **a chart component and its interop now** (chosen, the issue requires uPlot through JS interop and #48/#49
build on it) against **only vendoring the library** (the views would each invent the interop).

## Decision

The web UI serves only files of its own static web assets, through `MapStaticAssets`, with every page in the Interactive Server render
mode; brand files are byte-identical copies of `docs/assets/`, uPlot 1.6.32 is vendored as an ES module pinned by hash, and the theme
switch, the reconnect dialog and the charts use static scripts, never inline code. The rules are in the [Web UI](../areas/web-ui.md)
area.

## Consequences

- A new brand file is added in `docs/assets/` first and copied byte for byte; a changed logo changes both, or the test fails.
- uPlot is updated by hand: a new release's three files and their hashes in the test change together, after reading the new source for
  inline styles, HTML writes and external URLs. Dependabot does not track it.
- The vendored files are excluded from the CI code analysis; the own scripts and style sheets are analyzed there (no local analyzer).
- The chart module is first used in a browser by the views of #48 and #49; the foundation tests its C# side with test doubles.
- The favicon is the SVG of 0051 only; a browser without SVG favicon support shows none, as no raster favicon exists.
