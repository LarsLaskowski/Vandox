# Plan: Web UI foundation

Source: Issue #24 | [spec.md](spec.md)
Status: Draft
Tier: security — the change adds a third-party browser library (uPlot, vendored), sets the security headers that back up security
area 12 (display of external data in the web UI), and edits CI configuration (`.github/workflows/ci.yml`) and the image smoke script
(security area 13).

## Problem / root cause

Summary of `spec.md`: a Blazor shell exists in `src/Vandox.Backend/Components` (`App.razor`, `Routes.razor`,
`Layout/MainLayout.razor`, `Pages/Home.razor`) with `wwwroot/app.css` and `wwwroot/favicon.svg`. The foundation makes it interactive,
gives it a layout with navigation and a theme switch, the brand files, uPlot charts through JS interop, served static assets and strict
security headers.

Claims of the issue and its comment, checked against the code (and, where reading could not settle it, by running a scratch copy of
`main` from its build output and from a `dotnet publish`):

- "Blazor Web App with the Interactive Server render mode in `src/Vandox.Backend` (`Components/`)" — **partly refuted.**
  `ServeCommand.Build` (`src/Vandox.Backend/Hosting/ServeCommand.cs:155` and `:165`) registers `AddInteractiveServerComponents` and
  `AddInteractiveServerRenderMode`, but no component has a render mode (`Components/App.razor:14` renders `<Routes />` without one),
  so every page is static server rendering and no circuit is ever opened.
- "all assets served as static web assets from the application" — **refuted for the build output.** `ServeCommand.Build` serves
  files with `app.UseStaticFiles()` (`ServeCommand.cs:162`) from `ContentRootPath = AppContext.BaseDirectory`. In .NET 10 the
  framework script `_framework/blazor.web.js` is a static web asset of the package `Microsoft.AspNetCore.App.Internal.Assets`, and the
  build output has no `wwwroot`: run from `bin/Debug/net10.0`, `/` answered 200 and `/app.css`, `/favicon.svg` and
  `/_framework/blazor.web.js` answered **404**; from the publish output all four answered 200. Every test runs the build output, so
  no test sees what the browser loads (`ServeTests` checks only the HTML of `/`). With `builder.WebHost.UseStaticWebAssets()` and
  `app.MapStaticAssets()` instead of `UseStaticFiles` all four answered 200 from the build output and from the publish output, and
  404 on the ingest port (scratch experiment).
- "Security headers: CSP without inline scripts …, `frame-ancestors 'none'`" — **not present today**, and two framework defaults
  interfere (observed on `/`): the Razor components endpoint adds `Content-Security-Policy: frame-ancestors 'self'` and antiforgery
  adds `X-Frame-Options: SAMEORIGIN`. An application header set before the endpoint ran produced **two** `Content-Security-Policy`
  headers; with `AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null)` only the
  application's remained, and antiforgery keeps an `X-Frame-Options` already set (`DENY` stayed). `/healthz` already sets
  `X-Content-Type-Options: nosniff` itself (`Hosting/HealthEndpoint.cs:40`).
- "check what the Blazor framework scripts and the SignalR connection need" — checked in `blazor.web.js` 10.0.12: no `eval` or
  `new Function`; the default reconnect display creates a `<style>` element (blocked by `style-src 'self'`), but uses a page element
  with the id `components-reconnect-modal` instead when one exists (`onConnectionDown`: `document.getElementById(dialogId)`); the
  error UI and the reconnect display set styles only through the CSSOM (`element.style.display`), which the policy does not govern;
  the prerendered interactive page carries its state in HTML comments (`<!--Blazor:{"type":"server",…}-->`), no inline script.
  The SignalR connection goes to the own origin (`connect-src 'self'`). Documented in record 0099 and the Web UI area.
- "uPlot for charts through JS interop … no CDN, no Node build" — uPlot release tag `1.6.32` (commit
  `e995b061e9fc5476a6d862cd2fb2ebc7452ca012`, the newest 1.6.x tag) checked: `dist/uPlot.esm.js` and `dist/uPlot.min.css` contain no
  `innerHTML`, `eval`, `new Function`, `<style>` creation or `setAttribute("style", …)`; styles are set only through `element.style`;
  the only URL is the banner comment; the CSS has no `url(`. All files LF without CR.
- "Logo and favicon from `docs/assets/` (the image build context must include them)" and the comment's `go:embed` / Go Dockerfile
  advice — **outdated:** the backend is .NET (0073); `.dockerignore` allows `src/` and the Dockerfile copies `src/`, so files under
  `src/Vandox.Backend/wwwroot` reach the image (the publish output contains `wwwroot/`, checked). `wwwroot/favicon.svg` is already a
  byte-identical copy of `docs/assets/vandox-favicon.svg` (checked with `cmp`), but no test compares it. The choice the comment leaves
  open is made in record 0098 (copies plus a byte-for-byte test).
- "The web port keeps answering `/healthz` and nothing of the UI is served on the ingest port" — **confirmed** for today:
  `PortRoutingMiddleware` (`Hosting/PortRoutingMiddleware.cs:47`) answers 404 for every non-web connection before any endpoint runs;
  it also covered `POST /_blazor/negotiate` (web 200, ingest 404) and the static assets in the experiment.

Related defects found on the way:

- The static assets 404 from the build output (above) — fixed by this plan (AC9).
- `App.razor` has no `<base href="/">`; relative asset URLs break on any page below `/` (none exists yet) — fixed (AC2).
- `App.razor` has a static `<title>` and `Home.razor` a `<PageTitle>`, so the page carries two `<title>` elements, both "Vandox".
  Harmless (browsers take the first); left as is.

## Acceptance criteria

Test classes are in `tests/Vandox.Backend.Tests`. "The policy" is the value of `SecurityHeadersMiddleware.ContentSecurityPolicy`:
`default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`.

No external resources (issue AC "No external resources at runtime"):

- [ ] AC1: The policy equals the value above, every source token of every directive is `'self'` or `'none'` (so no
  `'unsafe-inline'`, `'unsafe-eval'`, `*`, scheme, host or `data:` source), `default-src` and `frame-ancestors` are `'none'`.
  Test: `SecurityHeadersMiddlewareTests.SecurityHeadersMiddlewarePolicyAllowsOnlyOwnOrigin` (a pin of the constant: written in step 6).
- [ ] AC2: `GET /` on the web port of the running service returns HTML in which every `src` and `href` attribute value is relative
  or root-relative (no scheme, not starting with `//`), every `<script>` element has a `src` and an empty body, there is no `<style>`
  element and no `style` attribute; it contains `<base href="/">`, a `viewport` meta element, `<link rel="icon"` with
  `href="brand/vandox-favicon.svg"`, `<script src="js/app.js">`, `<script src="_framework/blazor.web.js">`, the interactive server marker
  `<!--Blazor:{"type":"server"`, an element with `id="components-reconnect-modal"` and one with `id="blazor-error-ui"`.
  Test: `ServeTests.ServeServesPageWithLocalResourcesAndNoInlineCode`.
- [ ] AC3: No `.razor` file under `src/Vandox.Backend/Components` contains a `style` attribute, a `<style` element, a `<script`
  element without `src`, an HTML event-handler attribute (`on` + letters + `=`, not preceded by `@`), a `javascript:` URL, or a
  `src`/`href` value with a scheme or starting with `//`. The scanner the test uses finds each of these forms and accepts the allowed
  ones. Tests: `WebUiContentTests.ComponentsContainNoInlineCodeOrExternalUrls` and
  `WebUiContentTests.MarkupScannerFindsInlineCode` (`[DataRow]` cases, expected found / not found):
  `<p style="color:red">` found; `<style>p{}</style>` found; `<script>alert(1)</script>` found; `<script src="js/app.js"></script>` not
  found; `<button onclick="x()">` found; `<button @onclick="Save">` not found; `<a href="javascript:x()">` found;
  `<script src="https://cdn.example/x.js"></script>` found; `<img src="//cdn.example/x.png">` found; `<img src="brand/x.svg">` not found;
  `<a href="">` not found.
- [ ] AC4: The own scripts `wwwroot/js/app.js` and `wwwroot/js/charts.js` contain no `http://`, `https://`, no string literal starting
  with `//` (`"//`, `'//`, `` `// ``), no `eval(`, `new Function`, `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`,
  `setAttribute("style"` or `setAttribute('style'`; every `.css` file under `wwwroot` (own and vendored) contains no `@import` and no
  `url(` whose value has a scheme or starts with `//`. Tests: `WebUiContentTests.OwnScriptsContainNoExternalUrlOrDynamicCode`,
  `WebUiContentTests.StyleSheetsReferenceNoExternalResources`.

Layout (issue AC "Layout works on desktop and phone"; the visual check is under *Verification without tests*):

- [ ] AC5: `MainLayout`, rendered with a body, has a header with `<img src="brand/vandox-logo-horizontal.svg"` (class `logo-light`)
  and `<img src="brand/vandox-logo-horizontal-dark.svg"` (class `logo-dark`), both `alt="Vandox"`; a `<nav aria-label="Main">` with a
  link `Overview` to `""` that carries the class `active` when the current URI is the base URI; a `<button type="button"` with the
  attribute `data-theme-toggle` and the three texts `System`, `Light`, `Dark`; and the body inside `<main>`.
  Test: `MainLayoutTests.MainLayoutRendersHeaderNavigationAndThemeSwitch`.
- [ ] AC6: Every `.svg` file under `src/Vandox.Backend/wwwroot` lies in `wwwroot/brand/` and is byte-identical to the file of the same
  name in `docs/assets/`, and the three files `vandox-favicon.svg`, `vandox-logo-horizontal.svg`, `vandox-logo-horizontal-dark.svg`
  exist there (so `wwwroot/favicon.svg` is gone). `wwwroot/app.css` contains, for each token row of the *Color tokens* table in
  `docs/BRANDING.md`, the custom property name and its light and dark value (read from that table, compared ignoring ASCII case), and
  the selectors `[data-theme="dark"]` and `[data-theme="light"]` and the media query `prefers-color-scheme: dark`.
  Tests: `WebUiContentTests.BrandFilesMatchDocsAssets`, `WebUiContentTests.StyleSheetUsesBrandingTokens`.

Security headers (issue AC "Security headers set and covered by a test"):

- [ ] AC7: `SecurityHeadersMiddleware.InvokeAsync` sets `Content-Security-Policy` to the policy, `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: no-referrer` and `X-Frame-Options: DENY` before it calls the next delegate, calls it exactly once, and replaces a
  value already present (a preset `X-Frame-Options: SAMEORIGIN` and a preset policy end as one value each).
  Tests: `SecurityHeadersMiddlewareTests.SecurityHeadersMiddlewareSetsHeadersAndCallsNext`,
  `SecurityHeadersMiddlewareTests.SecurityHeadersMiddlewareReplacesExistingHeaders`.
- [ ] AC8: On the running service's web port, the responses to `GET /`, `/app.css`, `/_framework/blazor.web.js`, `/healthz` and
  `/nothing-here` (404) each carry exactly one `Content-Security-Policy` value equal to the policy, and exactly one value each of
  `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and `X-Frame-Options: DENY`.
  Test: `ServeTests.ServeSetsSecurityHeadersOnWebResponses`.

Static assets and port routing:

- [ ] AC9: The running service (build output, as every test runs it) answers 200 on the web port and 404 on the ingest port for
  `/_framework/blazor.web.js`, `/app.css`, `/js/app.js`, `/js/charts.js`, `/lib/uplot/uPlot.esm.js`, `/lib/uplot/uPlot.min.css`,
  `/brand/vandox-favicon.svg`, `/brand/vandox-logo-horizontal.svg` and `/brand/vandox-logo-horizontal-dark.svg`; and to
  `POST /_blazor/negotiate?negotiateVersion=1` 200 on the web port and 404 on the ingest port. The vendored files
  `wwwroot/lib/uplot/uPlot.esm.js`, `uPlot.min.css` and `LICENSE` have the SHA-256 values
  `5dd9b3281aa64b461b42d9945f6adb2649d346502b12281a9ae0d46599a80eba`,
  `df630c6a8d6f8eeaff264b50f73ce5b114f646ffd9a0bb74f049b0a00135fa04` and
  `8f989229699b4fe2f1a0432d0e9edc338a8a911e250e2d1b01ecd770a5f5b1bd` (uPlot tag 1.6.32).
  Tests: `ServeTests.ServeServesWebAssetsOnlyOnWebPort`, `WebUiContentTests.VendoredUPlotMatchesRelease`.
  The existing `ServeTests.ServeRunsServiceOnConfiguredPortsAndStopsCleanly` keeps passing unchanged (`/healthz` on the web port,
  404 on the ingest port).

Charts:

- [ ] AC10: `new ChartData(times, series)` keeps its inputs; `ToColumns()` returns the times as Unix seconds (milliseconds / 1000.0,
  e.g. `2026-10-12T00:00:00Z` → `1791763200`, `+00:00:00.500` → `1791763200.5`) followed by one column per series, a `null` value
  staying `null`. The constructor throws `ArgumentException` (exactly that type) when a series has a different length than the
  times, the times are not strictly ascending (a descending pair, an equal pair), a value is `NaN`, positive or negative infinity, or
  the series list is empty; and `ArgumentNullException` for a null argument or label.
  Tests: `ChartDataTests.ChartDataConvertsTimesToUnixSecondsAndKeepsGaps`, `ChartDataTests.ChartDataRefusesInvalidData` (`[DataRow]`
  per case above), `ChartDataTests.ChartDataRefusesNullArguments`.
- [ ] AC11: `ChartInterop.RenderAsync` imports `./js/charts.js` once (`import`), calls `create` on the module with the host element,
  the title, the labels and `ToColumns()` on the first call, and `update` with the new columns on the chart object on later calls
  (no second import, no second `create`); every call passes the caller's token. `DisposeAsync` calls `destroy` on the chart and
  disposes the chart and the module reference; it does nothing before the first render; it completes without an exception when the
  circuit is gone (`JSDisconnectedException` from any of these calls) and lets any other exception through; `RenderAsync` after
  `DisposeAsync` throws `ObjectDisposedException`.
  Tests: `ChartInteropTests.ChartInteropCreatesChartOnFirstRender`, `ChartInteropTests.ChartInteropUpdatesChartOnLaterRender`,
  `ChartInteropTests.ChartInteropDisposeDestroysChartAndModule`, `ChartInteropTests.ChartInteropDisposeBeforeRenderDoesNothing`,
  `ChartInteropTests.ChartInteropDisposeToleratesDisconnectedCircuit`, `ChartInteropTests.ChartInteropRenderAfterDisposeThrows`.
- [ ] AC12: `TimeSeriesChart` renders `<div class="chart" role="img" aria-label="{Title}">` (the title escaped like any Razor text);
  after its first render with data it creates the chart through the interop; a re-render with the same `ChartData` instance calls no
  further interop; a new `ChartData` instance calls `update`; disposing the component destroys the chart; without data nothing is
  created. Tests: `TimeSeriesChartTests.TimeSeriesChartRendersLabelledHost`,
  `TimeSeriesChartTests.TimeSeriesChartCreatesChartAndUpdatesOnNewData`, `TimeSeriesChartTests.TimeSeriesChartWithoutDataCreatesNothing`.

Step 5 writes the tests of AC2 to AC12 (they fail: files, types and the served assets are missing or the skeleton throws); AC1 pins
a constant the skeleton already declares and is written in step 6.

## Verification without tests

The change has production and test code; steps 4 to 6 and the *Coverage gate* apply. Parts no unit test can see are verified here:

| What | Where | Who |
| ---- | ---- | --- |
| Layout at desktop width and at 360 CSS pixels (no page-wide horizontal scrolling, navigation below logo and switch), theme follows the system, the switch cycles System/Light/Dark and survives a reload without a flash, logos switch with the theme | a browser on the branch build (`dotnet run` or the CI image) | Product Manager, before the PR is marked ready (step 11); the orchestrator asks for it in the PR description's test notes |
| No CSP violation in the browser console and only the own origin in the network panel on `/`, including the live connection (WebSocket) | same browser session | Product Manager |
| Reconnect dialog appears when the service stops and offers retry/reload | same browser session, stopping the service | Product Manager |
| The image serves the assets and the policy (publish output, not the build output) | CI job *Release build check*, step "Smoke test backend container" (no Docker locally: *Not checkable in a cloud session*) | orchestrator reads the CI result in step 11 |
| Own scripts and style sheets pass the JavaScript/CSS rules | SonarQube Cloud (step 11), no local analyzer | Code Officer / Dev in step 11 |
| `charts.js` draws, resizes and recolors a chart | read by the Reviewer in step 8; first used in a browser by #48/#49 (record 0098) | Reviewer |

## Approach

1. **Serving** (`Hosting/ServeCommand.cs`, `Build`): add `builder.WebHost.UseStaticWebAssets();` (loads
   `vandoxd.staticwebassets.runtime.json` from the base directory when present, i.e. in build and test output; absent from a publish,
   then a no-op); replace `app.UseStaticFiles();` by `app.MapStaticAssets();` placed before `MapRazorComponents`; register
   `app.UseMiddleware<SecurityHeadersMiddleware>();` directly after `PortRoutingMiddleware` (so the ingest listener's 404 stays
   untouched and every web response gets the headers); change `.AddInteractiveServerRenderMode()` to
   `.AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null)`. Nothing else in the file changes.
2. **Headers** (`Hosting/SecurityHeadersMiddleware.cs`, new): sets the four headers (assignment through the typed `IHeaderDictionary`
   properties where they exist, so a preset value is replaced) and calls the next delegate. `HealthEndpoint` keeps its own `nosniff`
   assignment (same value, one header).
3. **Page** (`Components/App.razor`, rewritten): `<html lang="en">`; head: charset, viewport, `<base href="/" />`, the existing
   `<title>Vandox</title>`, `<script src="js/app.js"></script>` (classic, synchronous, before the style sheets so the theme is set
   before the first paint), `<link rel="icon" type="image/svg+xml" href="brand/vandox-favicon.svg" />`,
   `<link rel="stylesheet" href="lib/uplot/uPlot.min.css" />`, `<link rel="stylesheet" href="app.css" />`,
   `<HeadOutlet @rendermode="InteractiveServer" />`; body: `<Routes @rendermode="InteractiveServer" />`, the reconnect element
   `<div id="components-reconnect-modal">` (texts for reconnecting, failed/rejected, buttons with `data-reconnect-retry` and
   `data-reload`, shown and hidden by the `components-reconnect-*` classes in `app.css`), the error banner `<div id="blazor-error-ui">`
   (text, a `data-reload` button, a `.dismiss` element the framework wires), then `<script src="_framework/blazor.web.js"></script>`.
   No inline code, no `style` attribute.
4. **Layout** (`Components/Layout/MainLayout.razor`, rewritten): markup of AC5 (logo images 110×32 inside a link to `""`,
   `<nav aria-label="Main">` with `<NavLink href="" Match="NavLinkMatch.All">Overview</NavLink>`, the theme button with the text
   `Theme` and three spans `theme-system`/`theme-light`/`theme-dark` of which the style sheet shows the one matching the root
   element's `data-theme`, no `aria-label`), `<main>@Body</main>`. No code block.
5. **Style sheet** (`wwwroot/app.css`, rewritten): the tokens of `docs/BRANDING.md` on `:root` (light), the dark values under
   `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { … } }` and under `:root[data-theme="dark"]`, the light
   values again under `:root[data-theme="light"]`; `color-scheme` per theme; background and surface colors derived from the tokens;
   the logo, theme-text, reconnect and error-banner visibility rules; chart series colors as custom properties `--vandox-chart-1` to
   `--vandox-chart-4` per theme (the first is `--vandox-color-brand`); a mobile-first layout: the header wraps at a breakpoint of
   40rem so the navigation takes its own row and scrolls horizontally inside itself; `main` with a max width; system font stack, no
   `@import`, no `url()`.
6. **Scripts** (`wwwroot/js/app.js`, new, classic script): on load, reads `localStorage["vandox-theme"]` (`light` or `dark`, anything
   else ignored) inside `try`/`catch` and sets `document.documentElement.dataset.theme`; one `click` listener on `document` handles
   `[data-theme-toggle]` (cycle System → Light → Dark → System, store or remove the key, set or remove `data-theme`, then dispatch
   `vandox-themechange` on `window`), `[data-reconnect-retry]` (`Blazor.reconnect()`, and `location.reload()` when it resolves `false`)
   and `[data-reload]` (`location.reload()`). It changes no element inside the Blazor-managed tree.
   `wwwroot/js/charts.js` (new, ES module): `import uPlot from "../lib/uplot/uPlot.esm.js";` and
   `export function create(element, title, labels, columns)` returning `{ update(columns), destroy() }`. Width from the element's
   client width, height 240 px; a `ResizeObserver` calls `setSize`; series colors from `getComputedStyle(document.documentElement)`
   (`--vandox-chart-1` … `--vandox-chart-4`, cycled), axis and grid from `--vandox-color-ink`; on `vandox-themechange` and on a
   `prefers-color-scheme` change the plot is rebuilt with the current data and the new colors; gaps stay gaps (uPlot's default
   `spanGaps: false`); the x axis is time (Unix seconds). `destroy` disconnects the observer, removes both listeners and destroys the plot.
7. **Charts, C# side** (`Components/Charts/`, new): `ChartSeries`, `ChartData` (validation of AC10 in the constructor, copying the
   lists so a caller cannot change them later), `ChartInterop` (AC11), `TimeSeriesChart.razor` with the markup
   `<div @ref="_host" class="chart" role="img" aria-label="@Title"></div>` and its code in `TimeSeriesChart.razor.cs` (AC12).
   `_Imports.razor` adds `@using Microsoft.JSInterop` and `@using Vandox.Backend.Components.Charts`.
8. **Files**: copy `docs/assets/vandox-favicon.svg`, `vandox-logo-horizontal.svg`, `vandox-logo-horizontal-dark.svg` byte for byte to
   `wwwroot/brand/`; delete `wwwroot/favicon.svg`; copy `dist/uPlot.esm.js`, `dist/uPlot.min.css` and `LICENSE` of uPlot tag 1.6.32
   (`git clone --depth 1 --branch 1.6.32 https://github.com/leeoniya/uPlot` into a scratch directory, `git rev-parse HEAD` must be
   `e995b061e9fc5476a6d862cd2fb2ebc7452ca012`; `.squad/routing.md`, *Reading third-party repositories*) to `wwwroot/lib/uplot/` and
   check the SHA-256 values of AC9 before committing.
9. **CI** (`.github/workflows/ci.yml`): add `src/Vandox.Backend/wwwroot/lib/**` to `sonar.exclusions` (vendored code is not analyzed;
   0098). **Smoke script** (`.github/scripts/smoke-test-backend.sh`): after the `/healthz` check, check that
   `/_framework/blazor.web.js`, `/app.css` and `/lib/uplot/uPlot.esm.js` answer 200 from the image and that `/` carries exactly one
   `Content-Security-Policy` header with the documented value (the publish path, which the unit tests do not run); `[[ … ]]` and local
   variables per *Shell pitfalls* in `.squad/stack.md`.

## Options considered

Each with the reason it lost; the full reasoning is in records 0098 and 0099.

- Brand files: **copies plus byte test** (chosen) vs. linking `docs/assets/` into the static web assets and extending `.dockerignore`
  and the Dockerfile (image build change in a security area, fragile discovery of linked items) vs. moving the source into `wwwroot`
  (contradicts 0051).
- Serving: **`MapStaticAssets` + `UseStaticWebAssets`** (chosen, verified from build and publish output) vs. keeping
  `UseStaticFiles` (404 from the build output; tests blind to the assets).
- Headers: **one application middleware with the whole policy, framework frame-ancestors header off** (chosen) vs. framework header
  `'none'` plus a second policy header vs. nonces/hashes vs. a `<meta>` policy (no `frame-ancestors`) vs. leaving it to the proxy.
- uPlot: **vendored ES module of tag 1.6.32, hash-pinned, loaded only with a chart** (chosen) vs. the minified IIFE on every page vs.
  LibMan/restore from a CDN (no official NuGet package).
- Theme: **static head script + CSS tokens** (chosen) vs. a Blazor component with interop (applies after the circuit starts: flash)
  vs. a server-read cookie.
- Charts now: **component and interop in the foundation** (chosen; the issue requires it and #48/#49 build on it) vs. only vendoring
  the library.
- Render mode: **every page interactive (`Routes` and `HeadOutlet` with `InteractiveServer`)**, as the Backend host area already
  states, vs. per-page interactivity (the login of #25 may opt its page out with `[ExcludeFromInteractiveRouting]` when it needs a
  static form; not built here, nothing uses it yet).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Backend | `Hosting/SecurityHeadersMiddleware.cs` | new |
| Vandox.Backend | `Hosting/ServeCommand.cs` | `Build` only, as in *Approach* 1 |
| Vandox.Backend | `Components/App.razor`, `Components/Layout/MainLayout.razor` | rewritten |
| Vandox.Backend | `Components/_Imports.razor` | two `@using` lines |
| Vandox.Backend | `Components/Charts/ChartSeries.cs`, `ChartData.cs`, `ChartInterop.cs`, `TimeSeriesChart.razor`, `TimeSeriesChart.razor.cs` | new |
| Vandox.Backend | `wwwroot/app.css` | rewritten |
| Vandox.Backend | `wwwroot/js/app.js`, `wwwroot/js/charts.js` | new |
| Vandox.Backend | `wwwroot/brand/*.svg` (3), `wwwroot/lib/uplot/*` (3) | new copies |
| Vandox.Backend | `wwwroot/favicon.svg` | deleted |
| CI | `.github/workflows/ci.yml` (`sonar.exclusions`), `.github/scripts/smoke-test-backend.sh` | as in *Approach* 9 |

Files read and final (no change): `Components/Routes.razor`, `Components/Pages/Home.razor`, `Hosting/PortRoutingMiddleware.cs`,
`Hosting/ListenerRoutes.cs`, `Hosting/HealthEndpoint.cs`, `Vandox.Backend.csproj` (no package: everything used is in the ASP.NET Core
shared framework), `Directory.Packages.props`, `deploy/backend/Dockerfile`, `.dockerignore`.

## Signatures (for the Dev's skeleton)

```csharp
// src/Vandox.Backend/Hosting/SecurityHeadersMiddleware.cs
namespace Vandox.Backend.Hosting;

public sealed class SecurityHeadersMiddleware
{
    public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    public const string ContentTypeOptions = "nosniff";
    public const string ReferrerPolicy = "no-referrer";
    public const string FrameOptions = "DENY";

    public SecurityHeadersMiddleware(RequestDelegate next);
    public Task InvokeAsync(HttpContext context);
}

// src/Vandox.Backend/Components/Charts/ChartSeries.cs
namespace Vandox.Backend.Components.Charts;

public sealed class ChartSeries
{
    public ChartSeries(string label, IReadOnlyList<double?> values);   // ArgumentNullException for null
    public string Label { get; }
    public IReadOnlyList<double?> Values { get; }
}

// src/Vandox.Backend/Components/Charts/ChartData.cs
public sealed class ChartData
{
    public ChartData(IReadOnlyList<DateTimeOffset> times, IReadOnlyList<ChartSeries> series);   // AC10
    public IReadOnlyList<DateTimeOffset> Times { get; }
    public IReadOnlyList<ChartSeries> Series { get; }
    internal double?[][] ToColumns();   // [0] Unix seconds, then one column per series
}

// src/Vandox.Backend/Components/Charts/ChartInterop.cs
public sealed class ChartInterop : IAsyncDisposable
{
    public const string ModulePath = "./js/charts.js";

    public ChartInterop(IJSRuntime jsRuntime);
    public Task RenderAsync(ElementReference host, string title, ChartData data, CancellationToken cancellationToken);
    public ValueTask DisposeAsync();
}

// src/Vandox.Backend/Components/Charts/TimeSeriesChart.razor.cs (the .razor file holds only the markup of Approach 7)
public sealed partial class TimeSeriesChart : IAsyncDisposable
{
    [Parameter, EditorRequired] public string Title { get; set; } = string.Empty;
    [Parameter, EditorRequired] public ChartData? Data { get; set; }
    [Inject] private IJSRuntime JSRuntime { get; set; } = null!;

    protected override Task OnAfterRenderAsync(bool firstRender);   // renders when Data is set and not the instance last rendered
    public ValueTask DisposeAsync();
}
```

JS contract the interop calls (`IJSRuntime`/`IJSObjectReference` with the token overloads):
`import(ModulePath)` → module; `module.create(host, title, labels: string[], columns: (number|null)[][])` → chart;
`chart.update(columns)`; `chart.destroy()`.

The skeleton keeps the four constants with their final values; bodies throw (`#pragma warning disable RH2003, S2325`, *Skeleton* in
`.squad/stack.md`). `TimeSeriesChart.razor` gets its final markup in the skeleton.

## Test files

New, in `tests/Vandox.Backend.Tests` (flat, as the existing files): `SecurityHeadersMiddlewareTests.cs`, `ChartDataTests.cs`,
`ChartInteropTests.cs`, `TimeSeriesChartTests.cs`, `MainLayoutTests.cs`, `WebUiContentTests.cs` (a contract test over repository
files, like `WireContractTests`; reads them through `RepositoryFiles.Path`). Extended: `ServeTests.cs` (three new methods).

New test doubles (Tester), `internal` in the test project: `RecordingJsRuntime` (`IJSRuntime`: records identifier, arguments and token
of every call, returns `RecordingJsObjectReference` instances, can throw a scripted exception), `RecordingJsObjectReference`
(`IJSObjectReference`: the same, records `DisposeAsync`; `create` returns a further reference), `TestNavigationManager`
(`NavigationManager` initialized with `http://localhost/`, for `NavLink` under `HtmlRenderer`), `ComponentTestRenderer` (a
`Renderer` subclass that renders a root component and runs `OnAfterRenderAsync`, for `TimeSeriesChart`; no package, no bUnit).

Existing test code that calls a changed signature: none (no existing member changes its signature). `HomePageTests` and
`ServeTests.ServeRunsServiceOnConfiguredPortsAndStopsCleanly` stay as they are.

## Areas

- **New area *Web UI*** (`docs/areas/web-ui.md`, Lead decision): scope and index row are written with this plan (Lead). The Dev
  updates it in step 6 only where the implementation deviates from it (and reports the deviation).
- **Backend host** (`docs/areas/backend-host.md`, Dev): *Listeners and routing* — the web listener serves the web UI, its static files
  and `/healthz`, every web response carries the security headers of the Web UI area (link), static files and the live connection's
  endpoints are not served on the ingest listener; *Not here* — link to the Web UI area; *Implementation* — add
  `Hosting/SecurityHeadersMiddleware`.
- Network and security model: none (exposure unchanged).

## Documentation updates

| File | Edit | Owner |
| ---- | ---- | ----- |
| `docs/areas/web-ui.md`, `docs/areas/README.md` (index row) | new area | Lead (done with the plan) |
| `docs/ARCHITECTURE.md` | Web UI area link in the `src/Vandox.Backend` entry, one sentence with records 0098/0099 | Lead (done with the plan) |
| `docs/areas/backend-host.md` | as under *Areas* | Dev |
| `docs/BRANDING.md` | *Where the logo is used*, Web UI bullet: the copies live in `src/Vandox.Backend/wwwroot/brand/`, checked byte for byte by a test (record 0098); remove "currently … #24 decides …" | Dev |
| `.squad/project.md` | *Security areas* 12: the web UI's policy (one self-only CSP on every web response, no inline code, `SecurityHeadersMiddleware`, `WebUiContentTests`; own scripts write no HTML; external text never through `MarkupString`; records 0098, 0099). 13: vendored browser code `src/Vandox.Backend/wwwroot/lib/uplot/` (uPlot 1.6.32) pinned by SHA-256 in `WebUiContentTests`, updated by hand (0098). *Integration surface*: new item "A new web UI page, component or static asset" — the navigation in `MainLayout` (a page), the *Static assets* list in `docs/areas/web-ui.md` (a file), the rules of 0099 checked by `WebUiContentTests`, `ServeTests.ServeServesWebAssetsOnlyOnWebPort` (a file the page loads), and for a brand file the byte copy of `docs/assets/` | Dev |
| `.squad/project.md` | *Test doubles*: row "Blazor components (C#)" with the four new doubles (implemented) | Tester |
| `.squad/stack.md` | *Not checkable in a cloud session*: row for own browser scripts and style sheets (`src/Vandox.Backend/wwwroot/js/*.js`, `*.css`): no JavaScript/CSS analyzer locally; substitute `WebUiContentTests`; authoritative SonarQube Cloud (step 11) | Dev |
| `docs/decisions/0098-…`, `0099-…`, index rows | Proposed | Lead (done) |

`README.md`: none (no option, secret or command changes).

## Architecture check

- Guarantees of `.squad/project.md`: none touched. Charts draw a missing value as a gap, in line with "data that is missing is
  unknown, never normal".
- Port routing (0081): unchanged; the new middleware runs after it, static files and the SignalR endpoints are endpoints behind it
  (404 on the ingest listener, AC9).
- Exposure (0016, 0023): unchanged; TLS stays at the proxy. The policy does not use `upgrade-insecure-requests`, because the service
  speaks plain HTTP to the proxy and may be reached on loopback directly.
- Image (0041, 0059): unchanged Dockerfile; the publish output already contains `wwwroot/` with precompressed files, served
  read-only (the root file system stays read-only).
- Memory: one circuit per open page, bounded by the single-user home-network use (0081).

## Security considerations

- [x] Limits on parsed input: no external input is parsed. Chart data comes from the server's own code; no size limit is added
  (each view bounds its own query; the interop is not a trust boundary).
- [x] Exceptions that can reach a user: `ArgumentException`/`ArgumentNullException` from `ChartData` and `ObjectDisposedException`
  from `ChartInterop` are programming errors; in an interactive page an unhandled exception shows only the error banner's fixed text
  (detailed circuit errors stay off, the default), never the exception message. `JSDisconnectedException` is caught only in
  `DisposeAsync`. No exception text reaches a response.
- [x] External text: chart titles and labels go to uPlot, which writes them as text (no `innerHTML` in 1.6.32, checked); the
  `aria-label` is a Razor attribute (escaped). Own scripts write no HTML (AC4).
- [x] Supply chain: uPlot is copied from the release tag at a verified commit and pinned by SHA-256 (AC9); it is MIT-licensed, the
  `LICENSE` ships with it; it is excluded from the code analysis, not from review.
- [x] Policy bypass forms considered for AC3/AC4 (what a browser executes or loads without a `<script src>`): inline `<script>`,
  event-handler attributes, `javascript:` URLs, `style` attributes and `<style>` elements, `@import` and `url()` in CSS, protocol-
  relative `//host` URLs, `eval`/`new Function`, HTML writes (`innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`). The
  policy blocks all of them at run time; the tests keep them out of the sources so the policy never has to be loosened.
- [x] Residual: browsers without CSP Level 3 `'self'` matching of `ws:`/`wss:` (record 0099).

## Decision records

- `docs/decisions/0098-web-ui-serves-only-its-own-static-web-assets.md` (Proposed, Area: Web UI)
- `docs/decisions/0099-web-listener-security-headers-one-self-only-csp.md` (Proposed, Area: Web UI)

0081 and 0051 (accepted, unreleased) stay valid as they are: 0081's Interactive Server choice is implemented here, and 0051 left the
delivery of the brand files to this issue, which 0098 records.

## Out of scope / follow-ups

- Login, cookies, CSRF on forms, forwarded headers, HSTS and the proxy documentation including WebSocket pass-through (#25).
- The views and their navigation entries (#26, #27, #48 to #54, #62); the first browser use of the chart module is in #48/#49.
- A raster favicon, a web app manifest, `Permissions-Policy`, `Cross-Origin-Opener-Policy`.
- The duplicate `<title>` (harmless, above).
