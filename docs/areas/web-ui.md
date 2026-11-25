# Web UI

## Scope

The pages `vandoxd` serves on its web listener as a whole: render mode and live connection, layout and navigation, theme, brand files,
static assets, charts, the display of external text, the shared bound on the views' store reads, and the security headers of the web
listener together with what the UI framework needs from them. The user relies on it to look at the monitored server in a browser in the
home network; the views ([Overview page](web-ui-overview.md), [Logs view](web-ui-logs-view.md),
[Incidents view](web-ui-incidents-view.md), [Report page](web-ui-report-page.md), [History view](web-ui-history-view.md),
[Processes view](web-ui-processes-view.md), [Network view](web-ui-network-view.md), [Services view](web-ui-services-view.md)) build on it.

## Behavior

### Pages and render mode

- Every page is prerendered on the server and then runs interactively on the server: while it is open it holds one live connection
  (WebSocket, HTTP as fallback) to the same origin, a **circuit** on the server.
- The page sets `<base href="/">`, a `viewport` meta element for device width, and loads only the files listed under *Static assets*.
- The sign-in page (`/login`) is the one page without a live connection: it is rendered on the server as a plain HTML form and does not
  load the framework script `_framework/blazor.web.js`; every other page loads it ([Web login](web-login.md), *Access*).
- When the live connection is lost, the page shows its own reconnect dialog (the element `components-reconnect-modal`) with the states
  reconnecting, failed and rejected, and a way to retry or reload; on an unhandled error it shows its own error banner
  (`blazor-error-ui`). Both are styled by the style sheet, not by the framework.
- The framework's defaults apply to circuits: a message from the browser is at most 32 KB; at most 100 disconnected circuits are kept for
  reconnecting, each for 3 minutes; the number of connected circuits is not limited.
- A view that reads the store queries only once the page is interactive: prerendered (the first response) it shows its frame and a
  waiting text and queries nothing, so its read runs once per page load; the overview page then refreshes every 30 seconds ([Overview page](web-ui-overview.md)). The waiting texts are stated per view.

### Layout and navigation

- Every page has a header with the horizontal logo and the theme switch, and a main area with the page content. A signed-in user also
  sees the main navigation and a *Sign out* button in the header ([Web login](web-login.md), *Sessions*); the login page shows neither,
  since every entry would only lead back to it.
- The login page shows the form as a card centered below the header: the heading *Sign in*, one line that asks for the password of the
  web UI, the message of a failed attempt, the password field (focused when the page loads) and the *Sign in* button in the brand color.
- The navigation lists the existing pages; the entry of the current page is marked. Today it has eight entries, *Overview* (`/`, [Overview page](web-ui-overview.md)),
  *History* (`history`, [History view](web-ui-history-view.md)), *Processes* (`processes`, [Processes view](web-ui-processes-view.md)), *Network* (`network`, [Network view](web-ui-network-view.md)), *Services* (`services`, [Services view](web-ui-services-view.md)), *Logs* (`logs`), *Incidents* (`incidents`) and *Report* (`report`);
  each view adds its own. *History* is marked on `/history` and *Processes* on `/processes`, *Network* on `/network` and *Services* on `/services` (each with or without a query), *Logs* on `/logs` and on
  `/logs/templates`, *Incidents* on `/incidents` and on `/incidents/<number>`, *Report* on `/report` (with or without a query),
  *Overview* only on `/`.
- The layout works from desktop width down to phone width (360 CSS pixels) without horizontal scrolling of the page: on narrow screens
  the navigation moves below the logo and the theme switch. It needs no script.
- A view builds a link only from a fixed local path plus validated values (the links of each view are stated with it).

### Theme

- The theme follows the system (`prefers-color-scheme`) until the user chooses one. The switch in the header cycles through *System*,
  *Light* and *Dark*; the choice is kept in the browser's local storage and applied before the page is first drawn, so there is no flash
  of the other theme. Without local storage the choice lasts until the page is reloaded.
- Colors come from the design tokens of [BRANDING.md](../BRANDING.md): `--vandox-color-brand` and `--vandox-color-ink` with their light
  and dark values; a manual choice sets the same values as the system setting.
- The choice is the `data-theme` attribute on the root element. Without it (no choice made, or the attribute lost), the style sheet and
  the charts follow the system setting.
- The header shows `vandox-logo-horizontal.svg` in the light theme and `vandox-logo-horizontal-dark.svg` in the dark theme, never both;
  the favicon is `vandox-favicon.svg`, which switches its colors itself.

### Static assets

- Every file the browser loads comes from the application itself; the UI references no other origin, no CDN and no web font. There is
  no Node build: style sheets and scripts are written as served.
- Files: the framework script `_framework/blazor.web.js`, `app.css`, `js/app.js` (theme and reconnect controls), `js/charts.js` (chart
  module, loaded only by a page that shows a chart; its exports include `width` and `createHistory` of the history view, the processes view, the network view and the services view), `lib/uplot/` (uPlot 1.6.32: `uPlot.esm.js`, `uPlot.min.css` and its `LICENSE`) and
  `brand/` (byte-identical copies of `vandox-favicon.svg`, `vandox-logo-horizontal.svg` and `vandox-logo-horizontal-dark.svg` from
  `docs/assets/`, the single source).
- The same files are served from a build and from the published image. Static files are served only on the web listener.
- The page links every file by its plain name (no fingerprinted URL, no import map). The server answers plain names with an ETag and the
  browser revalidates them, so a new version is loaded on the next page load without a long-lived cache.

### Charts

- A time-series chart takes a list of times in ascending order and one or more labelled series with one value per time. A missing value
  is drawn as a gap, never as zero or as a line across the gap.
- Data with a series whose length differs from the times, times that are not strictly ascending, a value that is not a finite number, or
  no series is refused as a programming error before anything reaches the browser.
- A chart takes its width from its container and follows its resizing; its colors come from the theme tokens and follow a theme change
  (the chart is redrawn with the same data; it keeps one resize observer for its lifetime).
- Labels and titles are written as text, never as HTML: the chart library sets a title and a label as text content (read in the
  vendored file), so a label that holds external text, such as a program name, carries no markup; a view escapes such a label first
  (*External text*).
- A draw on a lost connection (the circuit is gone) ends without an error and does not count as drawn: the chart is drawn again on the next render of the component.
- A **stacked chart** draws the cumulative sums of its series as filled areas, each band between a series and the one below, and shows the
  value of each series itself in the legend. Every series of a stacked chart has a value at every time; a gap is refused as a programming
  error: `The stacked series '<label>' has no value at point <i>.` The series colors are six custom properties, `--vandox-chart-1` to
  `--vandox-chart-6`, in the light and the dark values; the series after the sixth reuse them in order.
- A series can mark points as **between samples**: a point that has no value of its own because only another series was sampled there.
  The line is drawn through such a point (the chart gives the library an undefined cell), while a gap (a missing value that is not
  bridged) still breaks it. A bridged point has no value, so that a value is not hidden; a stacked series bridges none. The bridge
  flags are empty (none) or one per time. Refused as programming errors, with the texts
  `The series '<label>' has <n> bridge flags for <m> points in time.`, `The series '<label>' has a value at bridged point <i>.` and
  `The stacked series '<label>' cannot bridge points.`
- The **history chart** draws up to 8 scales, each metric its own, with the first two as drawn axes (the second one only from 480 CSS
  pixels of chart width), lines dashed from the seventh, marks for events and a moment, and the legend shows the minimum, average and
  maximum of a step. It reports the drag selection (from 5 CSS pixels) and the click of the user to the page through the two instance
  methods `Select` and `Pick` of a reference that it disposes with the chart; no static method is invoked from the browser. The page
  validates what arrives; the chart writes no HTML and rebuilds itself on a theme change like the other charts ([History view](web-ui-history-view.md)).
- The [Services view](web-ui-services-view.md) draws its five MariaDB charts as history charts in the unstacked mode, every chart with its lines on one scale, which the chart fits to them (it does not start at 0).
- The history chart also has a **stacked mode** (the [Processes view](web-ui-processes-view.md) and the [Network view](web-ui-network-view.md)): every line is on one scale from 0, the lines are
  drawn as filled bands, each between the cumulative sum of a line and that of the one below, in line order, and the legend shows each
  line's own value, with its minimum and maximum where the line has them. Unlike the stacked chart above, it accepts a gap: at a step where
  any line has no value every band breaks and is never drawn across, and the chart still reports the drag selection and the click like
  the history chart. A stacked view whose line is not on the first scale is refused as a programming error. The incidents chart keeps
  its refusal of a gap, which it relies on.

### External text

Text that comes from log files, agents or stored incidents (sources, hosts, programs, messages, templates, commands, user names, agent
IDs) is shown only through one display that writes it as text, never as markup, in an element that isolates the text direction
(`unicode-bidi: isolate`) and keeps line feeds and tabs.

- Every character of category Cc other than line feed and tab, every format character (Cf, among them the direction controls such as
  U+202E and U+200B), the line and paragraph separators (Zl, Zp), private-use characters (Co), unassigned code points (Cn) and U+FFFD is
  written as a visible escape (`\u202e`, `\U000e0001` above U+FFFF, lower-case hex), styled apart from the text; consecutive escapes
  share one element.
- A field holds at most 256 runs, unless a view states fewer (the report page and the incidents view: 16); the rest of the field is written as one text in the last run, in the escape style whenever it differs from the field's own text.
- A field is capped at a number of characters set by the view, counted in UTF-16 characters of the displayed text, an escape counting as
  written. A longer field ends at the last whole character or escape that fits, followed by the marker `[cut: <n> more characters]`, `<n>`
  being the characters of the field not shown. The caps are 16,384 characters for a log message and 2,048 for every other field unless a
  view states another.
- Forged lines appear like real ones: the display escapes, it does not judge.

### Store reads of the views

- **At most two store calls of the overview page, the logs view, the incidents view, the report page, the history view, the processes view, the network view and the services view run at once across the whole service**, whatever the
  number of pages and users open; a further call waits for a slot within its own deadline. The store has four reader connections, so two
  stay free for `/healthz` and every other reader.
- The time-series query ([Time-series queries](time-series-query.md)) shares this gate: each of its store reads takes one slot for that
  call, as the views' calls do.
- The slot is held per store call (a read of many snapshots takes it once per snapshot). Saving a note does not use it. The overview page
  takes one slot for each of the five calls of a refresh, in turn ([Overview page](web-ui-overview.md)); the history view takes one for
  each call of its metric list, its chart and its moment ([History view](web-ui-history-view.md), *Calls and deadline*), the processes view for each call of its charts and its panel ([Processes view](web-ui-processes-view.md), *Calls and deadline*), the network view for each call of its charts, its table and its panel ([Network view](web-ui-network-view.md), *Calls and deadline*), and the services view for each call of its three actions ([Services view](web-ui-services-view.md), *Calls and deadline*).
- A store call that a view's deadline or the leaving of its page cancels is interrupted in the store
  ([Storage](storage.md), *Database file and connections*).

### Security headers

Every response of the web listener (pages, static files, `/healthz`, the live connection's HTTP responses) carries exactly these headers,
and the page carries no other `Content-Security-Policy`:

| Header | Value |
| ------ | ----- |
| `Content-Security-Policy` | `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'` |
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `no-referrer` |
| `X-Frame-Options` | `DENY` |

What the framework needs, and why the policy is enough:

- `script-src 'self'`: the framework script, `js/app.js` and the chart module with uPlot are files of the own origin; no inline script,
  no `eval` (the interactive server mode needs none).
- `connect-src 'self'`: the live connection goes to the own origin; in browsers with CSP Level 3, `'self'` also matches `ws:`/`wss:` of
  the same host, so the WebSocket needs no scheme source.
- `style-src 'self'`: the style sheets are files. The framework's built-in reconnect dialog would insert a `<style>` element, which the
  policy blocks, so the page brings its own dialog. Styles that scripts set through the CSSOM are not affected; `style` attributes in
  markup are, so components never use them.
- `img-src 'self'`: the logos and the favicon. Charts draw on a canvas.
- `base-uri 'self'` allows the page's `<base href="/">`; `form-action 'self'` allows forms that post to the own origin; it also applies
  to the redirect after a post, which is why redirects carry the path alone ([Web login](web-login.md), *Access*).
- `frame-ancestors 'none'` and `X-Frame-Options: DENY`: no page may be framed.

The ingest listener serves none of this: every UI path, static file and the live connection's endpoints answer `404` there
([Backend host](backend-host.md), *Listeners and routing*).

## Limits and errors

- **The sources stay within the policy**, checked by a test of the sources in every letter case and with whitespace around `=` and `(`:
  - Component markup, and the page as served: no `style` attribute, `<style>` element, `<script>` element without `src` or with a body,
    HTML event-handler attribute (`on…`; the framework's `@on…` directives are allowed), `srcdoc` or `http-equiv` attribute. The URL
    attributes `src`, `href`, `xlink:href`, `srcset`, `poster`, `action`, `formaction`, `data` and `ping` hold no character reference
    and, read as the browser reads a URL (tabs and newlines removed, whitespace trimmed, `\` as `/`), no scheme (`javascript:`, `data:`,
    `https:` …) and no protocol-relative form (`//`, `\\`, `/\`, `\/`).
  - Component code: no way of writing raw HTML and no attribute set at run time with a `style`, `on…`, `srcdoc` or `http-equiv` name,
    so text never reaches the page as raw HTML (the forbidden framework calls are listed under *Implementation*).
  - Own scripts (`js/`): no backslash, no `//` outside a whole-line comment, no `eval`, `Function`, `setTimeout`/`setInterval` with a
    string, no `setAttribute*`, and none of the HTML sinks `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `write`, `writeln`,
    `setHTML`, `setHTMLUnsafe`, `parseHTMLUnsafe`, `createContextualFragment`, `DOMParser`, `parseFromString`, `srcdoc`.
  - Style sheets: no `@import`, `@font-face`, `image-set`, no `url()` with a scheme or protocol-relative value; the own style sheet has
    no backslash (no CSS escapes).
  - The vendored uPlot files are not scanned; they are read before an update and pinned by SHA-256.
- Only the policy covers what the scan cannot see: attribute values computed at run time (expressions, attribute splatting, attributes
  the framework sets), strings computed in scripts, other URL-bearing attributes and the elements `<object>`, `<embed>`, `<frame>` and
  `<base>`, CSS escapes in the vendored sheet and CSS loads through other functions. Neither covers a navigation to another origin through
  a link whose address is computed at run time, so a view builds a link only from a fixed local path.
- A browser without CSP Level 3 matching of `ws:`/`wss:` falls back to HTTP for the live connection or cannot connect.
- The reverse proxy must pass WebSocket upgrades for the live connection.
- Every page and endpoint of the views is behind the login ([Web login](web-login.md), *Access*). A live connection needs a session:
  without one, the `/_blazor` endpoints answer `401`. The live connection does not check the `Origin` header, and the service does not
  filter the `Host` header (no `Host`-header allow-list); the session and the protected component descriptors of the signed-in page decide
  who may use it.
- A page reached through a **DNS-rebound** name does not get the session: the session cookie has no `Domain` attribute, so the browser
  sends it only to the host name the user signed in at, never to another name that resolves to the same address (whatever `SameSite`
  says). Such a page reaches only what needs no session — `/login`, where the failed-sign-in limits apply, the static files and
  `/healthz` — and cannot read any view without the password.
- **Circuits.** Each open page holds its data in its circuit, and the render batch that sends a page to the browser holds the same text
  once more, for a moment. The amounts per circuit are stated per view. The **number of circuits is not bounded** and the container has
  512 MB, so many open pages can exhaust the memory of the service. Only a signed-in client can open a circuit, so this needs the password
  or a session.
- Opened directly as a document, the favicon's own `<style>` element is blocked by the policy and the icon shows without its colors; as an
  icon or image it is not affected.

## Related decisions

- [0200](../decisions/0200-chart-calls-tolerate-a-lost-connection-bl0016-off-in-tests.md) — why a draw on a lost connection ends quietly and is repeated on the next render.
- [0081](../decisions/0081-backend-host-two-listeners-json-logs-blazor-interactive-server.md) — why Blazor in the Interactive Server render mode on labelled listeners.
- [0051](../decisions/0051-brand-assets-in-docs-assets-web-ui-and-telegram-with-their-features.md) — why `docs/assets/` is the single source of the logo.
- [0098](../decisions/0098-web-ui-serves-only-its-own-static-web-assets.md) — why own static web assets only, brand copies, vendored uPlot and a script-set theme.
- [0099](../decisions/0099-web-listener-security-headers-one-self-only-csp.md) — why one self-only policy set by the application.
- [0106](../decisions/0106-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md) — why external text is capped and escaped visibly, and why two reads run at once.
- [0218](../decisions/0218-the-overview-reads-latest-values-through-its-own-bounded-store-reads.md) — why the overview shares the gate with five calls per refresh.
- [0219](../decisions/0219-the-overview-refreshes-on-a-timer-that-skips-overlaps-and-pauses-while-disconnected.md) — why the overview refreshes on a timer and pauses while the connection is lost.
- [0107](../decisions/0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md) — why the incidents view shares the gate of the logs view.
- [0221](../decisions/0221-the-history-view-draws-every-metric-on-its-own-scale-with-two-axes-and-step-extremes-in-the-legend.md) — why the history chart has a scale per metric, two drawn axes and the step's extremes in its legend.
- [0224](../decisions/0224-the-processes-view-stacks-program-rollups-on-the-history-chart-and-compares-programs-of-one-source.md) — why the history chart has a stacked mode that accepts a gap, beside the incidents chart that refuses one.

## Not here

- The views: [Overview page](web-ui-overview.md), [Logs view](web-ui-logs-view.md), [Incidents view](web-ui-incidents-view.md), [Report page](web-ui-report-page.md), [History view](web-ui-history-view.md), [Processes view](web-ui-processes-view.md), [Network view](web-ui-network-view.md), [Services view](web-ui-services-view.md).
- Listeners, port routing, `/healthz`, the container and exposure: [Backend host](backend-host.md).
- Who may reach the web UI: [Network and security model](network-and-security-model.md).
- Logo files and color tokens: [BRANDING.md](../BRANDING.md).
- Login, sessions, CSRF, the failed-sign-in limits and the trust in a reverse proxy: [Web login](web-login.md). The setup of a proxy:
  [REVERSE_PROXY.md](../REVERSE_PROXY.md).

## Implementation

`Vandox.Backend`: a Blazor Web App in the Interactive Server render mode over SignalR (`Components/App.razor`,
`Components/Layout/MainLayout.razor`); every page below `Components/Pages` carries `[Authorize]` (`Pages/_Imports.razor`).
Charts: `Components/Charts` (`TimeSeriesChart`, `ChartData`, and for the history view, the processes view, the network view and the services view `HistoryChart`, `HistoryChartInterop`, `HistoryChartEvents` (the stacked mode is `setup.stacked` of `createHistory`); a stacked chart is `ChartData` with `stacked: true`, drawn by `createStacked`
in `js/charts.js`, which sets the bridged cells to `undefined`; `ChartSeries.Bridged`, `ChartData.ToBridges`). External text: `Components/Logs/ExternalText` with the runs of `Logs/LogText` (`LogText.Runs`, `LogText.Escape`,
`LogText.MaxShortChars`) and `Logs/TextRun`. The read gate: `Logs/WebReaderGate`, taken by `GatedLogExplorer`, `GatedIncidentBrowser`,
`GatedRecordReader`, `GatedIncidentStore`, `GatedListenerHistoryReader`, `GatedTimeSeriesReader` (`TimeSeries/`), `GatedHistoryReader` (`History/`), `GatedProgramRollupReader` (`Processes/`), `GatedConnectionRollupReader` (`Network/`), `GatedServiceHistoryReader` and `GatedMariaDbHistoryReader` (`ServiceHealth/`) and `GatedOverviewReader` (`Overview/`; with `CircuitConnection`, the connection state of a circuit, which the overview page uses to pause its refresh). Headers: `Hosting/SecurityHeadersMiddleware`;
`Hosting/ServeCommand`; `wwwroot/`; `Vandox.Backend.csproj`, whose `RequiresAspNetWebAssets` makes the restore add the framework script
independently of the sources, as the image build restores before copying them (record 0098). The source scan is `WebSourceScanner` in
`WebUiContentTests` (tests); in component code (`.razor`, `.cs` under `Components/`) it forbids `MarkupString`, `AddMarkupContent` and
`AddAttribute` with a `style`, `on…`, `srcdoc` or `http-equiv` name.
