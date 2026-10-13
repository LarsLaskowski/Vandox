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
- Each open page holds one live connection (a circuit) on the server. The framework's defaults apply: a message from the browser is
  at most 32 KB; at most 100 disconnected circuits are kept for reconnecting, each for 3 minutes; the number of connected circuits is
  not limited.

### Layout and navigation

- Every page has a header with the horizontal logo, the main navigation and the theme switch, and a main area with the page content.
- The navigation lists the existing pages; the entry of the current page is marked. Today it has two entries, *Overview* (`/`) and
  *Logs* (`logs`); each view adds its own. *Logs* is marked on `/logs` and on `/logs/templates`, *Overview* only on `/`.
- The layout works from desktop width down to phone width (360 CSS pixels) without horizontal scrolling of the page: on narrow screens
  the navigation moves below the logo and the theme switch. It needs no script.

### Theme

- The theme follows the system (`prefers-color-scheme`) until the user chooses one. The switch in the header cycles through
  *System*, *Light* and *Dark*; the choice is kept in the browser's local storage and applied before the page is first drawn, so there
  is no flash of the other theme. Without local storage the choice lasts until the page is reloaded.
- Colors come from the design tokens of [BRANDING.md](../BRANDING.md): `--vandox-color-brand` and `--vandox-color-ink` with their light
  and dark values; a manual choice sets the same values as the system setting.
- The choice is the `data-theme` attribute on the root element. Without it (no choice made, or the attribute lost), the style sheet
  and the charts follow the system setting.
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
- The page links every file by its plain name (no fingerprinted URL, no import map). The server answers plain names with an ETag
  and the browser revalidates them, so a new version is loaded on the next page load without a long-lived cache.

### Charts

- A time-series chart takes a list of times in ascending order and one or more labelled series with one value per time. A missing value
  is drawn as a gap, never as zero or as a line across the gap.
- Data with a series whose length differs from the times, times that are not strictly ascending, a value that is not a finite number,
  or no series is refused as a programming error before anything reaches the browser.
- A chart takes its width from its container and follows its resizing; its colors come from the theme tokens and follow a theme change
  (the chart is redrawn with the same data; it keeps one resize observer for its lifetime).
  Labels and titles are written as text, never as HTML.

### Logs view

Two pages read the stored log lines: the search (`/logs`) and the template list (`/logs/templates`). Both show the links *Search* and
*Templates*, work on the same address parameters, and are served on the web listener only (the ingest listener answers `404`). Until the
login exists (#25) whoever reaches the web port can read every stored log line (*Limits and errors*). The store behind them is described
in [Storage](storage.md) (*Log exploration*), the templates in [Detection, alerts and reports](detection-and-reports.md) (*Log templates*).

**Address.** Every filter is in the address, so a search can be bookmarked, shared and used with the browser's back and forward buttons.
The parameters, written in this order and each percent-encoded, with empty ones left out:

| Parameter | Value |
| --------- | ----- |
| `q` | the search text (the literal-term rules of [Storage](storage.md), *Log search*) |
| `source` | one source, exact |
| `program` | one program, exact |
| `severity` | `emerg`, `alert`, `crit`, `err`, `warning`, `notice`, `info` or `debug` (lower case); shows lines with that priority or a more severe one, and no line without a priority |
| `template` | a template number (1 to 18 digits): on `/logs` the lines of that template, on `/logs/templates` the template whose chart is shown |
| `from`, `to` | `yyyy-MM-ddTHH:mm` or `yyyy-MM-ddTHH:mm:ss` in ASCII digits, read as **UTC**; `from` is inclusive, `to` exclusive |

- `q`, `source` and `program` are valid UTF-16 of at most 1,024 UTF-8 bytes after decoding and hold no character of category Cc, Cf, Zl or
  Zp (white space other than U+0020 included). A key is matched without regard to ASCII case and may be percent-encoded; any other key is
  ignored. A parameter given more than once, even with an empty value, is refused with `The address gives the parameter <name> more
  than once.` A value that breaks its rule is refused with `The address has an invalid value for the parameter <name>.`, and `from` not
  before `to` with `The address has a start time that is not before its end time.` An error text names the parameter, never the value.
  An address with an error calls no store method. The part of the address after `#` is never read.
- Without `from` and `to` the whole stored range is searched. A time field the user leaves empty is left out of the address. The
  filter form is a GET form to the same page, so it also works without script; with script it validates the entered values first and
  shows the same error texts instead of navigating.
- The page follows every change of its address (submitting the form, a link, back and forward) and then searches what the new address
  says; the results always come from the address.

**Search page.**

- The page lists the matching lines **oldest first**, 100 per page: time (UTC, `yyyy-MM-dd HH:mm:ss.fff`), source, host, program,
  process ID, severity name (`-` when the line has no priority) and message. There is no total count of the matches.
- It queries only once the page is interactive. Prerendered (the first response) it shows the filters and `Searching…` and queries
  nothing, so a search runs once per page load.
- A page that is full offers *Next page* (the next 100 lines) and *First page*. A search that stops at the examined-lines bound before
  it has a full page (a common word or a filter that few lines pass, in a large range) shows `Searched up to <time> UTC.` with
  *Continue*, which searches on from there; the bound is 12,000 examined lines per request. The page may then hold fewer than 100 lines,
  or none. **The page position is not in the address**: *Next page*, *First page* and *Continue* add no history entry, so the browser's
  back button leaves the search, not the page, and a reloaded or shared address opens the first page.
- *Context* on a line shows the 5 lines before and the 5 lines after it from the same log of the same source and host (time, program and message of each), at most 10 lines around a marker for the hit; opening another replaces it. Lines of other logs of the source are skipped, and each side looks
  at no more than 2,000 lines of the source.
- With `template` the page shows the template (text, source, program, first and last seen, its number of lines) above the lines, which are
  the lines of that template, and a link to remove the filter. A template number the store does not know (templates are renumbered when
  `vandoxd templates` rebuilds the statistics) shows `This template does not exist. Template numbers change when the statistics are
  rebuilt.` and searches nothing.
- The source list of the form holds the stored sources (at most 64); a source of the address that is not in it stays selectable.

**Template page.**

- It lists the templates with lines in the range, optionally of one source and one program, most lines first, at most 200: template
  text, source, program, first and last seen (UTC), lines in total and **lines in range**. The range count is counted in **whole
  hours**: a `from` or `to` inside an hour counts that hour completely, and the column is labelled `Lines in range (whole hours)`.
  Template numbers and counts change when the statistics are rebuilt.
- *Chart* selects a template (`template` on this page): it is shown above the list with a link *Show lines* to `logs?template=<id>` with
  the same `from` and `to`, and a chart of its lines per hour for the last 2,160 hours of the range (`to` minus 2,160 hours at the
  earliest). The chart has one point per hour from the first to the last hour of the range that holds a line of the template or is
  covered by the template's source: the count, **0** for an hour in which the source logged but the template did not, and a **gap** for
  an hour in which the source logged nothing (unknown, never zero). Its title (`Lines per hour`) and series label (`Lines`) are fixed
  texts; no log text goes into the chart.

**External text.** Source, host, program, message and template text come from log files and agents and are shown through one component
that writes them as text, never as markup, in an element that isolates the text direction and keeps line feeds and tabs.

- Every character of category Cc other than line feed and tab, every format character (Cf, among them the direction controls such as
  U+202E and U+200B), the line and paragraph separators, private-use characters, unassigned code points and U+FFFD is written as a visible
  escape (`\u202e`, `\U000e0001` above U+FFFF, lower-case hex), styled apart from the text; consecutive escapes share one element.
- A field holds at most 256 runs; the rest of the field is written as one escaped text in the last run.
- **Per-field caps:** a message is shown up to 16,384 characters and every other field (source, host, program, template text) up to 2,048,
  counted in UTF-16 characters of the displayed text, an escape counting as written. A longer field ends at the last whole character or
  escape that fits, followed by the marker `[cut: <n> more characters]`, `<n>` being the characters of the field not shown. The caps leave
  every field within the stored limits (source 128 bytes, host 1,024, program 1,024, message 16,384) whole unless it holds characters that
  are escaped, which are longer when written out.

**Calls and deadline.**

- Each page has at most one store call running: the calls of one action (opening an address, *Next page*, *Continue*, *Context*) run one
  after another, and a new action cancels the running call and waits for its end before the next one starts. Leaving the page cancels it.
- Every call runs under a deadline of **10 seconds**, which includes the wait for a free slot below. The search page then shows `The
  search took longer than 10 seconds and was stopped. Narrow the time range or add filters.`, the template page `The query took longer
  than 10 seconds and was stopped. Narrow the time range or choose a source.`
- **At most two log-view calls run at once across the whole service**, whatever the number of pages and users open; a third waits for a
  slot within its own deadline. The store has four reader connections, so two stay free for `/healthz` and every other reader.
- A search the store refuses shows `The search cannot run: <rule>.` (the store's rule, which never contains the search text, for
  example that the text has more than 16 terms). Any other error shows `The logs could not be read. The service log has the
  details.` (templates: `The templates could not be read. The service log has the details.`); the details are only in the service log.

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

- The sources stay within the policy, checked by a test (`WebSourceScanner` in `WebUiContentTests`) in every letter case and with
  whitespace around `=` and `(`:
  - Component markup (`.razor`), and the page as served: no `style` attribute, `<style>` element, `<script>` element without `src` or
    with a body, HTML event-handler attribute (`on…`; Blazor's `@on…` is allowed), `srcdoc` or `http-equiv` attribute. The URL
    attributes `src`, `href`, `xlink:href`, `srcset`, `poster`, `action`, `formaction`, `data` and `ping` hold no character
    reference and, read as the browser reads a URL (tabs and newlines removed, whitespace trimmed, `\` as `/`), no scheme
    (`javascript:`, `data:`, `https:` …) and no protocol-relative form (`//`, `\\`, `/\`, `\/`).
  - Component code (`.razor`, `.cs` under `Components/`): no `MarkupString`, no `AddMarkupContent`, no `AddAttribute` with a
    `style`, `on…`, `srcdoc` or `http-equiv` name, so text never reaches the page as raw HTML.
  - Own scripts (`js/`): no backslash, no `//` outside a whole-line comment, no `eval`, `Function`, `setTimeout`/`setInterval` with
    a string, no `setAttribute*`, and none of the HTML sinks `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `write`, `writeln`,
    `setHTML`, `setHTMLUnsafe`, `parseHTMLUnsafe`, `createContextualFragment`, `DOMParser`, `parseFromString`, `srcdoc`.
  - Style sheets: no `@import`, `@font-face`, `image-set`, no `url()` with a scheme or protocol-relative value; the own style sheet
    has no backslash (no CSS escapes).
  - The vendored uPlot files are not scanned; they are read before an update and pinned by SHA-256.
- Only the policy covers what the scanner cannot see: attribute values computed at run time (Razor expressions, `@attributes`
  splatting, attributes the framework sets), strings computed in scripts, other URL-bearing attributes and the elements `<object>`,
  `<embed>`, `<frame>` and `<base>`, CSS escapes in the vendored sheet and CSS loads through other functions. Neither covers a
  navigation to another origin through a link whose address is computed at run time, so a view builds a link only from a fixed local
  path.
- A browser without CSP Level 3 matching of `ws:`/`wss:` falls back to HTTP for the live connection or cannot connect.
- The reverse proxy must pass WebSocket upgrades for the live connection.
- Until the login exists (#25), whoever reaches the web port can load the page and open a live connection; the live connection
  does not check the `Origin` header, and the service does not filter the `Host` header. The login decides whether a live
  connection may start before authentication.
- **The logs view is readable by whoever reaches the web port until #25.** The compose file publishes the web port on `127.0.0.1` only.
  The host has no `Host`-header allow-list, so through **DNS rebinding** a website that the operator's browser visits can read the logs
  through a rebound name when the reverse proxy forwards unknown `Host` headers or the web port is reachable from the LAN. The login
  and its reverse-proxy trust (#25) have to include a `Host` allow-list; v0.1.0 is not released without them.
- **Memory per circuit and the number of circuits.** The search page holds the capped text of its lines, not the stored records: a
  line holds up to 16,384 characters of message and up to 2,048 characters in each of source, host, program, about 45 KiB, so a page
  of 100 lines with an expanded context of 11 lines is about **5 MiB** in the worst case (a typical line is a few hundred bytes), and the
  template page, with up to 200 rows of four capped short fields and a chart of at most 2,160 points, about **3.1 MiB**. The render batch
  that sends the page to the browser holds the same text once more, for a moment. The **number of circuits is not bounded** before
  #25 (the framework keeps up to 100 disconnected circuits for 3 minutes, *Pages and render mode*), and the container has 512 MB, so
  many open log pages can exhaust the memory of the service.
- Template numbers change when `vandoxd templates` rebuilds the statistics, so a stored template link can point to another template or
  to none; the page reports an unknown number. Every stored source can be chosen in the form, because the data model allows a source only
  letters, digits and `. _ : / @ + -` and at most 128 bytes, which the address rules accept; a source in the address that the store
  does not know finds no lines.
- Opened directly as a document, the favicon's own `<style>` element is blocked by the policy and the icon shows without its colors; as
  an icon or image it is not affected.

## Related decisions

- [0081](../decisions/0081-backend-host-two-listeners-json-logs-blazor-interactive-server.md) — why Blazor in the Interactive Server render mode on labelled listeners.
- [0051](../decisions/0051-brand-assets-in-docs-assets-web-ui-and-telegram-with-their-features.md) — why `docs/assets/` is the single source of the logo.
- [0098](../decisions/0098-web-ui-serves-only-its-own-static-web-assets.md) — why own static web assets only, brand copies, vendored uPlot and a script-set theme.
- [0099](../decisions/0099-web-listener-security-headers-one-self-only-csp.md) — why one self-only policy set by the application.
- [0103](../decisions/0103-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md) — why the logs view takes its filters from the address, searches in bounded pages and caps external text.

## Not here

- Listeners, port routing, `/healthz`, the container and exposure: [Backend host](backend-host.md).
- Who may reach the web UI: [Network and security model](network-and-security-model.md).
- Logo files and color tokens: [BRANDING.md](../BRANDING.md).
- Login, sessions, CSRF and the reverse proxy setup: not implemented yet (#25).

## Implementation

`Vandox.Backend` (`Components/App.razor`, `Components/Layout/MainLayout.razor`, `Components/Charts`, `Components/Pages/LogsPage` and
`LogTemplatesPage`, `Components/Logs/ExternalText`, `Logs/` (`LogsAddress`, `LogText`, `LogLineView`, `GatedLogExplorer`, `TemplateHourSeries`, `LogsView`), `Hosting/SecurityHeadersMiddleware`,
`Hosting/ServeCommand`, `wwwroot/`, and `Vandox.Backend.csproj`, whose `RequiresAspNetWebAssets` makes the restore add the framework
script independently of the sources, as the image build restores before copying them; record 0098).
