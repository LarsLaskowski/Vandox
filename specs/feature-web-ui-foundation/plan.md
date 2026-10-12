# Plan: Web UI foundation

Source: Issue #24 | [spec.md](spec.md)
Status: Approved by Security
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
  its 12 URLs (`http://`/`https://`) all stand in comments (the banner and source references), none is loaded; the CSS has no
  `url(`, `@import`, `image-set`, `@font-face` and no backslash; the module has none of the HTML sinks of AC4 (`setHTML`,
  `setHTMLUnsafe`, `parseHTMLUnsafe`, `createContextualFragment`, `DOMParser`, `srcdoc`, `setAttribute`, `document.write`), and no
  `setTimeout`/`setInterval` (re-checked in revise on a scratch clone at the same commit). All files LF without CR.
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
- [ ] AC2: `GET /` on the web port of the running service returns HTML on which `WebSourceScanner.FindInMarkup` (section *Source
  scanner*) finds nothing — so no URL attribute of the scanner's list with a scheme, a protocol-relative form (`//`, `\\`, `/\`, `\/`,
  with tabs or newlines in between, after leading whitespace) or a character reference, no `<script>` without `src` or with a body,
  no `<style>` element, no `style`, event-handler, `srcdoc` or `http-equiv` attribute, in any letter case; it contains `<base href="/">`, a `viewport` meta element, `<link rel="icon"` with
  `href="brand/vandox-favicon.svg"`, `<script src="js/app.js">`, `<script src="_framework/blazor.web.js">`, the interactive server marker
  `<!--Blazor:{"type":"server"`, an element with `id="components-reconnect-modal"` and one with `id="blazor-error-ui"`.
  Test: `ServeTests.ServeServesPageWithLocalResourcesAndNoInlineCode`.
- [ ] AC3: `WebSourceScanner.FindInMarkup` finds nothing in any `.razor` file under `src/Vandox.Backend/Components`, and
  `WebSourceScanner.FindInComponentCode` finds nothing in any `.razor` or `.cs` file there (so no `MarkupString`, no
  `AddMarkupContent`, no `AddAttribute` with a literal `style`, `on…`, `srcdoc` or `http-equiv` name).
  Test: `WebUiContentTests.ComponentsContainNoInlineCodeOrExternalUrls`. The scanner's behavior on every form is pinned by
  `WebSourceScannerTests` (section *Source scanner*).
- [ ] AC4: `WebSourceScanner.FindInScript` finds nothing in any `.js` file under `src/Vandox.Backend/wwwroot` outside `lib/` (the own
  scripts `js/app.js`, `js/charts.js`); `WebSourceScanner.FindInStyleSheet` finds nothing in any `.css` file under `wwwroot`, with
  `ownStyleSheet: true` outside `lib/` and `false` inside it. Tests: `WebUiContentTests.OwnScriptsContainNoExternalUrlOrDynamicCode`,
  `WebUiContentTests.StyleSheetsReferenceNoExternalResources`. The vendored uPlot module is not scanned (its 12 URLs stand in
  comments); it is read and pinned by SHA-256 (AC9, record 0098).

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
  Test: `ServeTests.ServeSetsSecurityHeadersOnWebResponses`. The `/healthz` 503 path: `HealthEndpoint` assigns `nosniff` through the
  typed property before it branches (`HealthEndpoint.cs:40`), so both answers share it; the existing
  `ServeTests.ServeUsesListenHookAndReportsUnavailableDatabase` (whose second answer is 200 or 503) additionally asserts that the
  second answer carries exactly one `X-Content-Type-Options` value, `nosniff`, and exactly one `Content-Security-Policy` value equal
  to the policy (Tester adapts it).

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

Step 5 writes the tests of AC2 to AC12 and `WebSourceScannerTests` (they fail: files, types and the served assets are missing or the
skeleton throws; `WebSourceScannerTests` passes once the Tester's scanner is written, it tests test code); AC1 pins a constant the
skeleton already declares and is written in step 6.

### Source scanner

`WebSourceScanner` (test project, `internal static`, written by the Tester) returns the rule ids it finds; AC2 to AC4 use it. It is
a text scanner, not a parser: it keeps forms out of the sources so the policy never has to be loosened, and the policy is the
run-time backstop for every form it cannot see (list under *Security considerations*).

```csharp
// tests/Vandox.Backend.Tests/WebSourceScanner.cs
internal static class WebSourceScanner
{
    internal static IReadOnlyList<string> FindInMarkup(string markup);              // .razor files and served HTML
    internal static IReadOnlyList<string> FindInComponentCode(string code);         // .razor and .cs files under Components/
    internal static IReadOnlyList<string> FindInScript(string script);              // own .js files
    internal static IReadOnlyList<string> FindInStyleSheet(string styleSheet, bool ownStyleSheet);
}
```

Rules:

- **Markup** (matched case-insensitively, over the whole text, not per parsed tag, so C# inside a `.razor` file cannot hide a later
  tag; HTML comments are removed first, ended as the HTML parser ends them: `-->`, `--!>`, and the abrupt `<!-->` / `<!--->`):
  `style-attribute` — an attribute named `style` (with `\s*` before `=`, or without a value); `style-element` — `<style` followed by
  whitespace, `/` or `>`; `inline-script` — a `<script` start tag without a `src` attribute, or whose element body up to
  `</script` is not whitespace only; `event-handler` — an attribute name `on` + letters followed by `\s*=`, not preceded by `@`, a
  letter, digit, `_`, `:`, `.` or `-`; `srcdoc` and `http-equiv` — an attribute of that name (no `<iframe srcdoc>`, no
  `<meta http-equiv>` refresh); for the **URL attributes** `src`, `href`, `xlink:href`, `srcset`, `poster`, `action`, `formaction`,
  `data` (exact name, so not `data-*`) and `ping`, the value (double-quoted, single-quoted or unquoted, `\s*` around `=`):
  `character-reference` — the value contains `&` (so `jav&#x61;script:`, `javascript&colon;`, `java&Tab;script:` and numeric
  references without `;`); otherwise the value is normalized as the browser's URL parser does — every TAB, LF and CR removed,
  leading and trailing whitespace and C0 controls trimmed, every `\` read as `/` — and for `srcset` and `ping` split on `,` and
  spaces into tokens, each token checked: `scheme-url` — it starts with a scheme (`[a-z][a-z0-9+.-]*:`, e.g. `javascript:`, `data:`,
  `https:`); `protocol-relative-url` — it starts with `//`. A value that starts with `@` (a Razor expression) is computed at run time
  and out of reach.
- **Component code** (`.razor` and `.cs` under `Components/`, case-insensitive for names in string literals): `raw-markup` — the word
  `MarkupString` or `AddMarkupContent`; `attribute-name` — `AddAttribute(` with a string literal second argument (`\s*` around the
  commas) equal to `style`, `srcdoc`, `http-equiv` or `on` + letters.
- **Script** (case-sensitive, as JavaScript identifiers are; comments are scanned too): `backslash` — any `\` (closes every string
  escape form: `\/\/host`, `\\host`, `\u002f`, `\x2f`, octal escapes; the own scripts need none); `double-slash` — any `//` that is
  not the start of a whole-line comment (only indentation before it), so `http://`, `HTTPS://`, a string starting with `//` in any
  quote and a trailing comment; `dynamic-code` — the word `eval` or `Function` (so `eval (x)`, `(0,eval)(x)`, `new  Function`,
  `new` + newline + `Function`, `Function(` without `new`), or `setTimeout`/`setInterval` followed by `\s*(\s*` and a quote or
  backtick; `html-sink` — the words `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `write`, `writeln`, `setHTML`, `setHTMLUnsafe`,
  `parseHTMLUnsafe`, `createContextualFragment`, `DOMParser`, `parseFromString`, `srcdoc`; `set-attribute` — an identifier starting
  with `setAttribute` (also `setAttributeNS`, `setAttributeNode`; the own scripts use `dataset` and `removeAttribute`).
- **Style sheet** (case-insensitive): `import` — `@import`; `font-face` — `@font-face`; `image-set` — `image-set` followed by `\s*(`
  (so also `-webkit-image-set`); for every `url` followed by `\s*(`, the argument up to the closing `)` with quotes and whitespace
  removed, normalized as for markup: `scheme-url`, `protocol-relative-url`; `backslash` — any `\`, only when `ownStyleSheet` is true
  (closes CSS escapes in the own sheet; the vendored sheet has none, is read and hash-pinned).

Tests (`WebSourceScannerTests`; each found row asserts that the **distinct** rule ids found are exactly the expected one, so an input
that lost a backslash yields another or no rule and fails; each not-found row asserts an empty result). Inputs are given as C#
string literals: `\\` is one backslash in the string, `\t` and `\n` a tab and a newline.

`WebSourceScannerTests.WebSourceScannerMarkupFindsForbiddenForms` (input → rule):

| Input (C# literal) | Rule |
| ------------------ | ---- |
| `"<p style=\"color:red\">"` | `style-attribute` |
| `"<p STYLE = \"color:red\">"` | `style-attribute` |
| `"<style>p{}</style>"` | `style-element` |
| `"<Style>p{}</Style>"` | `style-element` |
| `"<script>alert(1)</script>"` | `inline-script` |
| `"<SCRIPT src=\"js/app.js\">alert(1)</SCRIPT>"` | `inline-script` |
| `"<button onclick=\"x()\">"` | `event-handler` |
| `"<button onClick = \"x()\">"` | `event-handler` |
| `"<body ONLOAD=x()>"` | `event-handler` |
| `"<a href=\"javascript:x()\">"` | `scheme-url` |
| `"<a href=\" JavaScript:x()\">"` | `scheme-url` |
| `"<a href=\"java\tscript:x()\">"` | `scheme-url` |
| `"<a href=javascript:x()>"` | `scheme-url` |
| `"<a href=\"jav&#x61;script:x()\">"` | `character-reference` |
| `"<a href=\"javascript&colon;x()\">"` | `character-reference` |
| `"<script src=\"HTTPS://cdn.example/x.js\"></script>"` | `scheme-url` |
| `"<img src=\"Data:image/png;base64,AA\">"` | `scheme-url` |
| `"<img src=\"//cdn.example/x.png\">"` | `protocol-relative-url` |
| `"<img src=\"\\\\cdn.example/x.png\">"` | `protocol-relative-url` |
| `"<img src=\"/\\cdn.example/x.png\">"` | `protocol-relative-url` |
| `"<img src=\"\\/cdn.example/x.png\">"` | `protocol-relative-url` |
| `"<img src=\"/\t/cdn.example/x.png\">"` | `protocol-relative-url` |
| `"<img srcset=\"brand/a.svg 1x, //cdn.example/x.png 2x\">"` | `protocol-relative-url` |
| `"<video poster=\"https://cdn.example/x.png\">"` | `scheme-url` |
| `"<form action=\"https://x.example/\">"` | `scheme-url` |
| `"<button formaction=\"//x.example/\">"` | `protocol-relative-url` |
| `"<object data=\"https://x.example/x.svg\">"` | `scheme-url` |
| `"<use xlink:href=\"https://x.example/x.svg#a\">"` | `scheme-url` |
| `"<a href=\"a\" ping=\"https://x.example/p\">"` | `scheme-url` |
| `"<iframe srcdoc=\"x\">"` | `srcdoc` |
| `"<meta http-equiv=\"refresh\" content=\"0;url=https://x.example/\">"` | `http-equiv` |
| `"<!-- x --!><p style=\"x\">"` | `style-attribute` |

`WebSourceScannerTests.WebSourceScannerMarkupAcceptsAllowedForms` (nothing found): `"<script src=\"js/app.js\"></script>"`,
`"<script src=\"_framework/blazor.web.js\"> </script>"`, `"<button @onclick=\"Save\">"`,
`"<button @onclick:preventDefault=\"true\">"`, `"<img src=\"brand/x.svg\">"`, `"<a href=\"\">"`, `"<base href=\"/\">"`,
`"<div data-theme-toggle class=\"station\">"`, `"<!--Blazor:{\"type\":\"server\",\"descriptor\":\"a/onb=\"}-->"`,
`"<div @attributes=\"Extra\">"` (splatting is out of the scanner's reach and left to the policy).

`WebSourceScannerTests.WebSourceScannerComponentCodeFindsRawMarkup`: `"@((MarkupString)text)"` → `raw-markup`;
`"builder.AddMarkupContent(0, text);"` → `raw-markup`; `"builder.AddAttribute(1, \"style\", value);"` → `attribute-name`;
`"builder.AddAttribute(1, \"ONCLICK\", value);"` → `attribute-name`; `"builder.AddAttribute( 1 , \"srcdoc\" , value);"` →
`attribute-name`. `WebSourceScannerTests.WebSourceScannerComponentCodeAcceptsAllowedForms`: `"builder.AddContent(0, text);"`,
`"builder.AddAttribute(1, \"class\", value);"`.

`WebSourceScannerTests.WebSourceScannerScriptFindsForbiddenForms`:

| Input (C# literal) | Rule |
| ------------------ | ---- |
| `"fetch(\"https://x.example/\");"` | `double-slash` |
| `"fetch(\"HTTPS://x.example/\");"` | `double-slash` |
| `"fetch(\"//x.example/\");"` | `double-slash` |
| `"fetch('//x.example/');"` | `double-slash` |
| ``"fetch(`//x.example/`);"`` | `double-slash` |
| `"let a = 1; // note"` | `double-slash` |
| `"fetch(\"\\/\\/x.example/\");"` | `backslash` |
| `"fetch(\"\\u002f\\u002fx.example/\");"` | `backslash` |
| `"eval(x);"` | `dynamic-code` |
| `"eval (x);"` | `dynamic-code` |
| `"(0,eval)(x);"` | `dynamic-code` |
| `"new Function(\"x\");"` | `dynamic-code` |
| `"new  Function(\"x\");"` | `dynamic-code` |
| `"new\nFunction(\"x\");"` | `dynamic-code` |
| `"Function(\"x\")();"` | `dynamic-code` |
| `"setTimeout(\"x()\", 1);"` | `dynamic-code` |
| `"setTimeout( 'x()' , 1);"` | `dynamic-code` |
| ``"setInterval(`x()`, 1);"`` | `dynamic-code` |
| `"el.innerHTML = s;"` | `html-sink` |
| `"el.outerHTML = s;"` | `html-sink` |
| `"el.insertAdjacentHTML(\"beforeend\", s);"` | `html-sink` |
| `"document.write(s);"` | `html-sink` |
| `"document.writeln(s);"` | `html-sink` |
| `"el.setHTML(s);"` | `html-sink` |
| `"el.setHTMLUnsafe(s);"` | `html-sink` |
| `"Document.parseHTMLUnsafe(s);"` | `html-sink` |
| `"range.createContextualFragment(s);"` | `html-sink` |
| `"new DOMParser().parseFromString(s, \"text/html\");"` | `html-sink` |
| `"frame.srcdoc = s;"` | `html-sink` |
| `"el.setAttribute(\"style\", s);"` | `set-attribute` |
| `"el.setAttribute( \"STYLE\" , s);"` | `set-attribute` |
| ``"el.setAttribute(`onclick`, s);"`` | `set-attribute` |
| `"el.setAttributeNS(null, \"href\", s);"` | `set-attribute` |

`WebSourceScannerTests.WebSourceScannerScriptAcceptsAllowedForms` (nothing found): `"// a whole-line comment"`,
`"    // an indented comment"`, `"import uPlot from \"../lib/uplot/uPlot.esm.js\";"`, `"setTimeout(tick, 1000);"`,
`"el.style.display = \"none\";"`, `"delete document.documentElement.dataset.theme;"`, `"el.removeAttribute(\"data-theme\");"`,
`"function evaluate() { return 1; }"`, `"el.textContent = s;"`.

`WebSourceScannerTests.WebSourceScannerStyleSheetFindsForbiddenForms` (input, `ownStyleSheet` → rule):

| Input (C# literal) | Own | Rule |
| ------------------ | --- | ---- |
| `"@import \"x.css\";"` | false | `import` |
| `"@IMPORT url(x.css);"` | false | `import` |
| `"p{background:url(https://x.example/a.png)}"` | false | `scheme-url` |
| `"p{background:URL( \"//x.example/a.png\" )}"` | false | `protocol-relative-url` |
| `"p{background:url('//x.example/a.png')}"` | false | `protocol-relative-url` |
| `"p{background:url(data:image/png;base64,AA)}"` | false | `scheme-url` |
| `"p{background:url(\"\\\\x.example/a.png\")}"` | false | `protocol-relative-url` |
| `"p{background:image-set(\"//x.example/a.png\" 1x)}"` | false | `image-set` |
| `"p{background:-webkit-image-set(\"a.png\" 1x)}"` | false | `image-set` |
| `"@font-face{font-family:x;src:local(x)}"` | false | `font-face` |
| `"p::before{content:\"\\2014\"}"` | true | `backslash` |

`WebSourceScannerTests.WebSourceScannerStyleSheetAcceptsAllowedForms` (nothing found, `ownStyleSheet` true unless named):
`"p{background:url(brand/a.svg)}"`, `"p{color:var(--vandox-color-ink)}"`, and `"p::before{content:\"\\2014\"}"` with `false`
(a CSS escape in the vendored sheet is out of the scanner's reach).

## Verification without tests

The change has production and test code; steps 4 to 6 and the *Coverage gate* apply. Parts no unit test can see are verified here:

| What | Where | Who |
| ---- | ---- | --- |
| Layout at desktop width and at 360 CSS pixels (no page-wide horizontal scrolling, navigation below logo and switch), theme follows the system, the switch cycles System/Light/Dark and survives a reload without a flash, logos switch with the theme | a browser on the branch build (`dotnet run` or the CI image) | Product Manager, before the PR is marked ready (step 11); the orchestrator asks for it in the PR description's test notes |
| No CSP violation in the browser console and only the own origin in the network panel on `/`, including the live connection (WebSocket) | same browser session | Product Manager |
| Reconnect dialog appears when the service stops and offers retry/reload | same browser session, stopping the service | Product Manager |
| A chosen theme (`data-theme` on `<html>`) survives a click on a navigation link and a reconnect after the service restarts; without a choice the page follows the system setting | same browser session (the root element is outside the interactive components, so no render should touch it; a cloud session has no browser, so the Dev cannot check it) | Product Manager |
| The image serves the assets and the policy (publish output, not the build output) | CI job *Release build check*, step "Smoke test backend container" (no Docker locally: *Not checkable in a cloud session*) | orchestrator reads the CI result in step 11 |
| Own scripts and style sheets pass the JavaScript/CSS rules | SonarQube Cloud (step 11), no local analyzer | Code Officer / Dev in step 11 |
| `charts.js` draws, resizes and recolors a chart | read by the Reviewer in step 8 against the uPlot 1.6.32 API of the vendored `uPlot.esm.js` (constructor `new uPlot(opts, data, element)`, the third parameter `then` taking the host element, `setSize({ width, height })`, `destroy()`, `spanGaps` default `false`), checking that the theme-change rebuild destroys the old plot and reuses the one `ResizeObserver` (no leaked observer or listener) and that `destroy` releases all three; first used in a browser by #48/#49 (record 0098) | Reviewer |

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
   and `[data-reload]` (`location.reload()`). It changes no element inside the Blazor-managed tree. It uses `dataset` and
   `delete …dataset.theme` (never `setAttribute`), no backslash and no `//` outside whole-line comments (AC4).
   `wwwroot/js/charts.js` (new, ES module): `import uPlot from "../lib/uplot/uPlot.esm.js";` and
   `export function create(element, title, labels, columns)` returning `{ update(columns), destroy() }`. Width from the element's
   client width, height 240 px; a `ResizeObserver` calls `setSize`; series colors from `getComputedStyle(document.documentElement)`
   (`--vandox-chart-1` … `--vandox-chart-4`, cycled), axis and grid from `--vandox-color-ink`; on `vandox-themechange` and on a
   `prefers-color-scheme` change the plot is rebuilt with the current data and the new colors; gaps stay gaps (uPlot's default
   `spanGaps: false`, not set); the x axis is time (Unix seconds). One `ResizeObserver` per chart, created in `create` and kept
   across rebuilds (a rebuild destroys the old plot with `destroy()` and creates the new one into the same element; it creates no
   second observer and adds no second listener); the observer calls `setSize({ width, height })` on the current plot.
   `destroy` disconnects the observer, removes both listeners and destroys the plot. When the root element has no `data-theme`
   (no stored choice, or the attribute lost), the colors come from the custom properties, which the style sheet sets from
   `prefers-color-scheme` — the script reads no theme attribute itself.
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
   variables per *Shell pitfalls* in `.squad/stack.md`. The script keeps no copy of the policy: it reads the value from the line
   `public const string ContentSecurityPolicy = "…";` of `$repo/src/Vandox.Backend/Hosting/SecurityHeadersMiddleware.cs` (a bash
   regex match on the double-quoted value; the policy has no double quote) and fails when that line is not found exactly once, so
   the constant, pinned by AC1, is the one source.

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
- Source checks: **text rules over the whole source with URL normalization, the policy as backstop** (chosen, *Source scanner*)
  vs. an HTML parser package in the test project (a new dependency, and a `.razor` file is not HTML: C# blocks, directive
  attributes and Razor expressions would need their own handling; scripts and style sheets would still need text rules) vs. relying
  on the policy alone (it does not block markup injected through a DOM sink, a meta refresh or a foreign navigation).
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
files, like `WireContractTests`; reads them through `RepositoryFiles.Path`), `WebSourceScanner.cs` (test helper, *Source scanner*)
and `WebSourceScannerTests.cs`. Extended: `ServeTests.cs` (three new methods, and two header assertions added to
`ServeUsesListenHookAndReportsUnavailableDatabase`, AC8).

New test doubles (Tester), `internal` in the test project: `RecordingJsRuntime` (`IJSRuntime`: records identifier, arguments and token
of every call, returns `RecordingJsObjectReference` instances, can throw a scripted exception), `RecordingJsObjectReference`
(`IJSObjectReference`: the same, records `DisposeAsync`; `create` returns a further reference), `TestNavigationManager`
(`NavigationManager` initialized with `http://localhost/`, for `NavLink` under `HtmlRenderer`), `ComponentTestRenderer` (a
`Renderer` subclass that renders a root component and runs `OnAfterRenderAsync`, for `TimeSeriesChart`; no package, no bUnit).

Existing test code that calls a changed signature: none (no existing member changes its signature). `HomePageTests` and
`ServeTests.ServeRunsServiceOnConfiguredPortsAndStopsCleanly` stay as they are; `ServeUsesListenHookAndReportsUnavailableDatabase`
gains assertions only (Tester, step 5).

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
| `.squad/project.md` | *Security areas* 12: the web UI's policy (one self-only CSP on every web response, no inline code, `SecurityHeadersMiddleware`); the sources are checked by `WebUiContentTests` with `WebSourceScanner`: own scripts contain none of its HTML sinks, `eval`/`Function`/string timers or `setAttribute`; component markup and code contain no `MarkupString` or `AddMarkupContent`, so external text never reaches the page as raw HTML; what only the policy covers is listed in the Web UI area (*Limits and errors*); records 0098, 0099. 13: vendored browser code `src/Vandox.Backend/wwwroot/lib/uplot/` (uPlot 1.6.32) pinned by SHA-256 in `WebUiContentTests`, updated by hand (0098). *Integration surface*: new item "A new web UI page, component or static asset" — the navigation in `MainLayout` (a page), the *Static assets* list in `docs/areas/web-ui.md` (a file), the rules of 0099 checked by `WebUiContentTests`, `ServeTests.ServeServesWebAssetsOnlyOnWebPort` (a file the page loads), and for a brand file the byte copy of `docs/assets/` | Dev |
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
- Memory: every open page holds one circuit on the server. The change sets no circuit or hub option, so the framework defaults
  apply (`ServeCommand` configures none): a SignalR message from the browser is at most 32 KB (`HubOptions.MaximumReceiveMessageSize`);
  at most 100 disconnected circuits are retained, each for 3 minutes (`CircuitOptions.DisconnectedCircuitMaxRetained`,
  `DisconnectedCircuitRetentionPeriod`); there is **no limit on connected circuits** (nor on Kestrel connections). Circuits open
  without authentication until the login (#25) exists — whoever reaches the web port can open them, as they can load the page today.
  Accepted for this change on the exposure of 0016/0023 (home network, behind the proxy); #25 decides whether a circuit may start
  before authentication (Web UI area, *Limits and errors*; *Out of scope / follow-ups*).

## Security considerations

- [x] Limits on parsed input: no external input is parsed. Chart data comes from the server's own code; no size limit is added
  (each view bounds its own query; the interop is not a trust boundary).
- [x] Exceptions that can reach a user: `ArgumentException`/`ArgumentNullException` from `ChartData` and `ObjectDisposedException`
  from `ChartInterop` are programming errors; in an interactive page an unhandled exception shows only the error banner's fixed text
  (detailed circuit errors stay off, the default), never the exception message. `JSDisconnectedException` is caught only in
  `DisposeAsync`. No exception text reaches a response.
- [x] External text: chart titles and labels go to uPlot, which writes them as text (no `innerHTML` in 1.6.32, checked); the
  `aria-label` is a Razor attribute (escaped). Own scripts contain none of the HTML sinks of the *Source scanner* (AC4), and no
  component passes text as raw markup (`MarkupString`, `AddMarkupContent`; AC3) — both checked by `WebSourceScanner`, whose reach
  is listed below.
- [x] Supply chain: uPlot is copied from the release tag at a verified commit and pinned by SHA-256 (AC9); it is MIT-licensed, the
  `LICENSE` ships with it; it is excluded from the code analysis, not from review.
- [x] Forms a browser or Razor accepts, and who covers them. **The scanner covers** (each form with a found row in *Source
  scanner*): in markup, in any letter case and with whitespace around `=` — `style` attributes, `<style>`, inline `<script>`,
  event-handler attributes, `srcdoc`, `http-equiv` (meta refresh, which the policy does not govern); URL attributes `src`, `href`,
  `xlink:href`, `srcset`, `poster`, `action`, `formaction`, `data`, `ping` with a scheme (`javascript:`, `data:`, `https:` …), a
  protocol-relative form (`//`, `\\`, `/\`, `\/`, with TAB/LF/CR inside or whitespace before) or any character reference
  (`&#x61;`, `&colon;`); in component code — `MarkupString`, `AddMarkupContent`, `AddAttribute` with a literal `style`/`on…`/
  `srcdoc`/`http-equiv` name; in own scripts — every backslash (all string escapes, `\/\/host`, `/`), every `//` outside a
  whole-line comment (URLs of any case, protocol-relative strings in any quote), `eval`, `Function` (with or without `new`, any
  spacing), string `setTimeout`/`setInterval`, the HTML sinks `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `write`, `writeln`,
  `setHTML`, `setHTMLUnsafe`, `parseHTMLUnsafe`, `createContextualFragment`, `DOMParser`/`parseFromString`, `srcdoc`, and every
  `setAttribute*`; in style sheets, in any letter case — `@import`, `@font-face`, `image-set`/`-webkit-image-set`, `url()` with a
  scheme or protocol-relative value (quoted or not, with `\`), and in the own sheet every backslash (CSS escapes).
  **Only the policy covers** (out of the scanner's reach, blocked at run time by `default-src 'none'` and the self-only
  directives): values computed at run time — Razor expressions in attribute values, `@attributes` splatting, attributes set by
  the framework, strings concatenated or computed in scripts (`window["ev" + "al"]`, `setTimeout(variable)`, `import(variable)`);
  URL-bearing attributes not in the list (`background`, `manifest`, `icon`, `codebase`, `archive`) and the elements `<object>`,
  `<embed>`, `<frame>` (`default-src 'none'`), `<base>` (`base-uri 'self'`); CSS escapes in the vendored sheet and loads through
  CSS functions other than `url()`/`image-set()` (`src()`, `image()`, `cross-fade()`); the vendored uPlot module (not scanned:
  read, hash-pinned, its 12 URLs in comments). **Neither covers** a navigation (not a load) to another origin through a link whose
  `href` is computed at run time; the Web UI area requires views to build links from a fixed local path, and
  `Referrer-Policy: no-referrer` sends no referrer. HTML injected through a DOM sink would not run script under the policy but
  could still add markup, which is why the sinks are the scanner's main job.
- [x] Residual: browsers without CSP Level 3 `'self'` matching of `ws:`/`wss:` (record 0099).

## Decision records

- `docs/decisions/0098-web-ui-serves-only-its-own-static-web-assets.md` (Proposed, Area: Web UI)
- `docs/decisions/0099-web-listener-security-headers-one-self-only-csp.md` (Proposed, Area: Web UI)

0081 and 0051 (accepted, unreleased) stay valid as they are: 0081's Interactive Server choice is implemented here, and 0051 left the
delivery of the brand files to this issue, which 0098 records.

## Out of scope / follow-ups

- Login, cookies, CSRF on forms, forwarded headers, HSTS and the proxy documentation including WebSocket pass-through (#25).
  Notes for #25 (existing exposure, not changed here; the orchestrator adds them to #25 as a comment): (1) the login decides whether
  a circuit may start before authentication (today every page opens one without it; connected circuits are unlimited);
  (2) the SignalR WebSocket of `/_blazor` checks no `Origin` header (cross-site WebSocket hijacking once a cookie authenticates the
  connection); (3) the service configures no `Host` header filtering (`CreateSlimBuilder`, no `AllowedHosts` anywhere in
  `src/Vandox.Backend`), which leaves DNS rebinding open against
  the web port.
- The views and their navigation entries (#26, #27, #48 to #54, #62); the first browser use of the chart module is in #48/#49.
- A raster favicon, a web app manifest, `Permissions-Policy`, `Cross-Origin-Opener-Policy`.
- The duplicate `<title>` (harmless, above).

## Challenge

Devil's Advocate, 0 major and 5 minor objections; all accepted.

1. *Theme attribute vs. Blazor DOM sync* — accepted. `<html>` is rendered by the static `App` component, outside the interactive
   `Routes`/`HeadOutlet`, and navigation between interactive pages is done by the circuit's router, so no render is expected to touch
   it; this is checked, not assumed: a new row under *Verification without tests* (Product Manager; a cloud session has no browser,
   so the Dev cannot do it) checks that `data-theme` survives a navigation link and a reconnect. The fallback is in the design:
   without the attribute the style sheet follows `prefers-color-scheme` (`:root:not([data-theme="light"])` under the media query),
   and `charts.js` reads only the custom properties (*Approach* 6), so a lost attribute shows the system theme, never a broken one.
2. *AC8 and the `/healthz` 503 path* — accepted. The 503 answer exists (`HealthEndpoint.cs:49`); it shares the `nosniff` assignment
   with the 200 answer. AC8 now adds header-count assertions to the existing `ServeUsesListenHookAndReportsUnavailableDatabase`,
   whose second answer is the one that can be 503 (Tester).
3. *Smoke script CSP source* — accepted. The script reads the policy from the `ContentSecurityPolicy` constant and fails when it
   cannot find it exactly once (*Approach* 9); AC1 pins the constant.
4. *Fingerprinting unused* — accepted. The page links the plain file names (no `@Assets[…]`, no import map) on purpose: the
   foundation's scanner and AC2 check fixed local paths. The Web UI area (*Static assets*) now states it and how a new version
   reaches the browser (`MapStaticAssets` answers plain names with an ETag and revalidation; the Dev confirms the served
   `Cache-Control` value in step 6 and corrects the area if it differs).
5. *`charts.js` untested in a browser* — accepted. The Reviewer row under *Verification without tests* names the uPlot 1.6.32 API
   to read it against (`new uPlot(opts, data, element)`, `setSize({ width, height })`, `destroy()`, `spanGaps` default `false`,
   checked in the vendored module) and the rebuild path; *Approach* 6 now fixes one `ResizeObserver` per chart for its lifetime.

## Security review

Verdict CHANGES_REQUIRED, 1 blocking and 3 non-blocking findings; all accepted.

1. *Blocking: the source scanners of AC3/AC4 miss sibling forms* — accepted; the whole list of accepted forms was re-checked, not only
   the reported ones. AC2 to AC4 now use one `WebSourceScanner` (section *Source scanner*) with these answers per class:
   (a) case — markup and style sheets are matched case-insensitively (found rows `<p STYLE = …>`, `<Style>`, `<SCRIPT …>`,
   `onClick`, `ONLOAD`, ` JavaScript:`, `HTTPS://`, `Data:`, `URL(`, `@IMPORT`); scripts are matched case-sensitively because
   JavaScript identifiers are, and their URL rule (`//`) has no letters. (b) whitespace and encoding — `\s*` around `=` and `(`,
   URL values normalized as the URL parser does (TAB/LF/CR removed, trimmed, `\` read as `/`), any `&` in a URL attribute value
   found (all character references, also numeric ones without `;`), `new  Function`/`new\nFunction`/`Function(`/`eval (x)`/
   `(0,eval)(x)` through the words `eval` and `Function`, every `setAttribute*` found (so the quote, backtick and spacing forms),
   CSS `url( "//x")`/`url('//x')`. (c) raw HTML from components — `FindInComponentCode` scans `.razor` and `.cs` under
   `Components/` for `MarkupString`, `AddMarkupContent` and `AddAttribute` with a literal `style`/`on…`/`srcdoc`/`http-equiv`
   name; `@attributes` splatting is named as the policy's job, with a not-found row documenting it. (d) protocol-relative forms
   `\\host`, `/\host`, `\/host` — found in markup and CSS through the `\` normalization, in AC2 at run time through the same
   scanner; in scripts every backslash is found (`"\/\/host"`). (e) URL attributes — `srcset`, `poster`, `action`, `formaction`,
   `data`, `xlink:href`, `ping` are scanned; `srcdoc` and `http-equiv` are forbidden outright; the remaining URL-bearing attributes
   and elements are left to the policy by name. (f) JS sinks — `setHTMLUnsafe`, `setHTML`, `parseHTMLUnsafe`,
   `createContextualFragment`, `DOMParser`/`parseFromString`, `srcdoc`, `Function(` without `new`, string `setTimeout`/
   `setInterval` added; `writeln` stays found (a word of its own). (g) CSS loads outside `url()` — `image-set`/`-webkit-image-set`
   and `@font-face` found; CSS escapes are found in the own sheet (any backslash) and named out of reach for the vendored one,
   which is read and hash-pinned. Every class has found rows, and each found row asserts the exact rule, so a test input that lost
   its backslash fails. The *Security considerations* bullet now lists what the scanner covers, what only the policy covers and
   what neither covers.
2. *uPlot URL claim* — accepted: the module has 12 URLs, all in comments; corrected here and in record 0098.
3. *Circuit memory is not a bound* — accepted: *Architecture check* states the framework defaults (32 KB receive size, 100
   disconnected circuits for 3 minutes, no limit on connected circuits), that circuits open without authentication until #25, and
   the note for #25; the Web UI area states the limits.
4. *No `Origin` check on the WebSocket, no `Host` filtering* — accepted as existing exposure outside this change; noted for #25
   under *Out of scope / follow-ups* and in the Web UI area.

## Security delta notes (round 2, non-blocking)

1. Removing HTML comments first hides a sibling form: in `<p title="<!--" style="x" data-x="-->">` the browser parses `style` as a real attribute, but the scanner deletes everything from `<!--` to `-->`; the same trick can hide `<meta http-equiv="refresh" …>`. Fix: treat `<!--` as a comment start only outside a quoted attribute value, or find any `<!--` / `-->` inside a quoted value; add the found row `"<p title=\"<!--\" style=\"x\" class=\"-->\">"` → `style-attribute`. If not fixed, name it among the forms neither the scanner nor the policy covers.
2. The `src` check in `inline-script` must match the exact attribute name `src` (same name-boundary rule as the URL attributes), so `<script data-src="x">alert(1)</script>` is not treated as "has src".
3. Smoke script: compare with the right-hand side quoted, `[[ "$got" == "$want" ]]`, so the value is never read as a glob pattern.
