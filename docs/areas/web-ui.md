# Web UI

## Scope

The pages `vandoxd` serves on its web listener: render mode, layout and navigation, theme, brand files, static assets, charts, the logs
view, the incidents view, the report page and the security headers of the web listener together with what the UI framework needs from them. The user relies on it to look at the
monitored server in a browser in the home network; the views (overview, history, logs, incidents, report) build on it. The listeners, port
routing and `/healthz` are in [Backend host](backend-host.md); the login, sessions and the reverse proxy trust are in
[Web login](web-login.md), the setup of a proxy in [REVERSE_PROXY.md](../REVERSE_PROXY.md).

## Behavior

### Pages and render mode

- The web UI is a Blazor Web App. Every page is prerendered on the server and then runs in the **Interactive Server** render mode: it
  holds one live connection (SignalR: WebSocket, HTTP as fallback) to the same origin while it is open.
- The page sets `<base href="/">`, a `viewport` meta element for device width, and loads only the files listed under *Static assets*.
- The sign-in page (`/login`) is the one page without a live connection: it is rendered on the server as a plain HTML form and does not
  load the framework script `_framework/blazor.web.js`; every other page loads it ([Web login](web-login.md), *Access*).
- When the live connection is lost, the page shows its own reconnect dialog (the element `components-reconnect-modal`) with the states
  reconnecting, failed and rejected, and a way to retry or reload; it shows its own error banner (`blazor-error-ui`) on an unhandled
  error. Both are styled by the style sheet, not by the framework.
- Each open page holds one live connection (a circuit) on the server. The framework's defaults apply: a message from the browser is
  at most 32 KB; at most 100 disconnected circuits are kept for reconnecting, each for 3 minutes; the number of connected circuits is
  not limited.

### Layout and navigation

- Every page has a header with the horizontal logo, the main navigation and the theme switch, and a main area with the page content.
  A signed-in user also sees a *Sign out* button in the header ([Web login](web-login.md), *Sessions*).
- The navigation lists the existing pages; the entry of the current page is marked. Today it has four entries, *Overview* (`/`),
  *Logs* (`logs`), *Incidents* (`incidents`) and *Report* (`report`); each view adds its own. *Logs* is marked on `/logs` and on
  `/logs/templates`, *Incidents* on `/incidents` and on `/incidents/<number>`, *Report* on `/report` (with or without a query),
  *Overview* only on `/`.
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
  Labels and titles are written as text, never as HTML: uPlot sets a title and a label through `textContent` (read in the vendored
  file), so a label that holds external text, such as a program name, carries no markup; the page escapes it first as described under
  *Incidents view*.
- A **stacked chart** (`createStacked` in `js/charts.js`, chosen by `ChartData` with `stacked: true`) draws the cumulative sums of its
  series as filled areas, each band between a series and the one below, and shows the value of each series itself in the legend. Every
  series of a stacked chart has a value at every time (a gap is refused as a programming error: `The stacked series '<label>' has no
  value at point <i>.`). The series colors are six custom properties, `--vandox-chart-1` to `--vandox-chart-6`, in the light and the
  dark values; the series after the sixth reuse them in order.

### Logs view

Two pages read the stored log lines: the search (`/logs`) and the template list (`/logs/templates`). Both show the links *Search* and
*Templates*, work on the same address parameters, and are served on the web listener only (the ingest listener answers `404`). Like every
page they need a session ([Web login](web-login.md)); a signed-in user can read every stored log line (*Limits and errors*). The store behind them is described
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
  says; the results always come from the address. Submitting the form searches again even when the address is unchanged, which refreshes the results.

**Search page.**

- The page lists the matching lines **oldest first**, 100 per page: time (UTC, `yyyy-MM-dd HH:mm:ss.fff`), source, host, program,
  process ID, severity name (`-` when the line has no priority) and message. There is no total count of the matches.
- It queries only once the page is interactive. Prerendered (the first response) it shows the filters and `Searching…` and queries
  nothing, so a search runs once per page load.
- A page that is full offers *Next page* (the next 100 lines) and *First page*. A search that stops at the examined-lines bound before
  it has a full page (a common word or a filter that few lines pass, in a large range) shows `Searched up to <time> UTC.` with
  *Continue*, which searches on from there; the bound is 8,000 examined lines per request, fewer for a text of several word parts (8,000 divided by the parts, so an IP
  address of four parts examines 2,000). The page may then hold fewer than 100 lines, or none.
- When the store could not finish its index check within 0.7 s (a text of many common words, or of a term of several common parts such
  as an IP address, over a large range), the search goes through the time range step by step the same way and the page says so above
  *Searched up to* with `These words are too common to look up at once, so the search goes through the time range step by step. Narrow the
  time range to get a complete answer.` Such a search may give no final "no lines" answer until the time range is narrowed, and every
  *Continue* pays the 0.7 s again. The text is shown only for a page that stopped at the bound after a stopped index check, not for an
  ordinary bound page. A scan page, with or without the stepwise text, also ends when **1.5 seconds** have passed since the request began (the 0.7 s of the index check included) with `Searched up to … Continue`, so on a slow or loaded host a search of a very common word may end sooner than its line bound; at least one line is always examined, so *Continue* advances. The deadline of 10 seconds stays the outer limit. **The page position is not in the address**: *Next page*, *First page* and *Continue* add no history entry, so the browser's
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
  slot within its own deadline. The store has four reader connections, so two stay free for `/healthz` and every other reader. The
  incidents view reads through the same two slots (*Incidents view*), so the bound covers both views together.
- A search the store refuses shows `The search cannot run: <rule>.` (the store's rule, which never contains the search text, for
  example that the text has more than 16 terms). Any other error shows `The logs could not be read. The service log has the
  details.` (templates: `The templates could not be read. The service log has the details.`); the details are only in the service log.

### Incidents view

**List page (`/incidents`).**

- The page lists the stored incidents **newest first** (by window start, then number), **20 per page**: the number (a link to its
  page), the time of the first kill (a link to the logs of that moment, below), the duration, the kills, the victim, the cause and
  the backup state, and a marker for an incident that has a note. The duration runs from the first to the last kill (`H:MM:SS`, hours
  not bounded, `0:00:00` for one kill; the window's length is mostly the configured padding and is shown on the incident page).
  The victim is the command of the first kill, followed by `and further kills` when there are more. The cause is the programs that
  grew most in the window (`+<n> MiB` each, rounded half away from zero), `none` when the growth is known and no program grew, and
  `unknown` when it could not be told; the backup is `running`, `not running` or `unknown`.
- It queries only once the page is interactive. Prerendered it shows its title and `Loading…` and queries nothing.
- A full page offers *Older incidents* (the next 20, after the last row), a later page offers *Newest incidents* (the first page
  again). An older page without a row shows `No older incidents.`; an empty store shows `No incidents are stored. Run vandoxd incidents
  after an import.` The position of the page is not in the address.
- The page reads summaries, not whole incidents: the store returns the columns of an incident and a few fields of its document
  (first victim, last kill, hypothesis), decoded row by row, so a page of 20 holds one document's bytes at a time
  ([Storage](storage.md), *Reading*).

**Incident page (`/incidents/<number>`).**

- Header: the window, the first and the last kill (each a link to the logs of that moment), the duration from the first to the last kill, the number of kills, and when the incident was built and last changed (UTC). Then the kills
  kept in the document (time, host, victim, process ID, anonymous memory, OOM score adjustment; at most 16, the page says how many
  more exist), the hypothesis (programs that grew, largest consumers, backup state and programs, the last boot, the number of process
  snapshots and service events), the timeline in stored order with the number of events the document left out, the connection peak
  (time, sockets, the number of connection snapshots in the window, the commands with the most sockets), the metric extremes (value, source, time) and the new templates and template
  spikes. A growth the incident could not tell is shown as `unknown`.
- A timeline row of the type `oom_kill`, `boot`, `backup_first_seen` or `backup_last_seen` carries the class `mark-oom`, `mark-boot`
  or `mark-backup` from this fixed map; no other type gets a class, whatever its text, and it is shown as escaped text.
- **Every time links to the logs of that moment**: the link is `logs` with `from` and `to` of the logs view's address ([Logs view](#logs-view)):
  `from` is the time floored to the whole second minus 30 seconds, `to` that second plus 5 minutes, both clipped to the first and last
  whole second of the storable range, so the moment is at the top of the first page of the logs view. It is not a click inside a chart.
  Times link on the kills, the first and last kill, the timeline entries, the last boot, the metric extremes, the connection peak and
  the templates, and are shown as `yyyy-MM-dd HH:mm:ss` UTC.
- A number that no incident has (also one that is not positive, which reads nothing) shows `This incident does not exist. It may have been
  merged into another one by vandoxd incidents.` and no note form.
- **Charts of the window**, read from the records of `[start, end)` after the incident is shown, each from its own source: *Memory per
  program (MiB, stacked)* (one point per process snapshot of the source of the earliest process snapshot in the window; the five programs
  with the highest peak, ties by name, and `Other programs`; a program absent from a snapshot counts 0; at most 4,096 programs are told
  apart, the rest are summed into `Other programs`), *Available memory and swap (MiB)* (`mem_available_bytes` and `swap_used_bytes`
  read from the source of the incident's stored extreme of that metric, at most 1,000 points each, at the union of their times with a gap
  where a series has no point or a value that is not finite; a metric without a stored extreme has no series) and *Connections* (the sum
  of the state counts of each connection snapshot of the source of the earliest one in the window). A chart is cut after 300 snapshots
  and the page says `The chart shows the first 300 snapshots, up to <time> UTC.` A window without any of them shows `No snapshot or
  metric was stored in this window.` A chart takes its source per query, so live agent data, whose sources are collector names, charts as
  well as a legacy import with one source for all kinds.
- Chart labels are program names through `LogText.Escape` capped at 64 characters; titles and the other series labels are fixed texts.
- **Notes.** The page shows one form for the incident's own note and one for each note that was moved to it from an incident merged into
  it (`Written for incident #<n>`), at most 64 (`Further notes of merged incidents exist and are not shown.` beyond). A form is a plain
  `POST` to `incidents/<number>/notes` with the antiforgery token, the number the note was written for as a hidden field and a text area of
  at most 1,800 characters; `Save note` stores it, an empty text deletes the note.

**The note endpoint (`POST /incidents/<number>/notes`).**

- It needs a session (anonymous: `401`) and a valid antiforgery token ([Web login](web-login.md), *Cross-site requests*). The antiforgery
  validation result is checked first (missing or invalid: `400`, the form is not read); then the number of the route (not positive: `404`);
  then the form: exactly one `note` and one `written_for` **from the form only**, never from the query string or the route (a field missing
  or given twice, also in another letter case, is refused with `400`). The text has its line ends converted to line feeds, **leading line
  feeds removed** (a text area drops the first line feed of its content, so a re-saved text stays equal), and a text of only white space is
  empty. More than 1,800 UTF-16 characters, or any U+0000, is refused with `400`; `written_for` is 1 to 18 ASCII digits above 0.
- `Saved` and `Deleted` are answered with `303` and `Location: /incidents/<number>` built from the parsed number, `NotFound` (an unknown
  incident, or a note that does not belong to it) with `404`, a note the store itself refuses with `400`, a store error with `500`, all
  without a body. **The log never holds a note text**: a failed save is logged with the exception only (`incidents view: saving a note
  failed`), never the text, a form value or the number from the request. A refused post stores nothing.
- The 1,800 characters count after decoding and line-end conversion; the worst case a browser sends under `maxlength` is 1,800 characters of
  three bytes written as `%XX`, 16,200 bytes, which stays under the 16 KiB form value limit of the service.

**Calls and deadline.** The incident and its notes are read in one action, the data of the window in a second one after it; each action
runs under the 10-second deadline of the logs view and a new action cancels the running one. A list or incident read that runs into it shows
`Reading the incidents took longer than 10 seconds and was stopped.`, a window read `Reading the data of the window took longer than 10
seconds and was stopped.` (the rest of the page stays); a store error shows `The incidents could not be read. The service log has the
details.` or `The data of the window could not be read. The service log has the details.` and is logged (`incidents view: reading failed`).
The incident and window reads take the **same gate as the logs view**: at most two calls of the logs and incidents views run at once across the
service, the gate is held per store call (a window read of many snapshots takes it once per snapshot), and saving a note does not use it.

### Report page

**Page (`/report`).**

- The page shows the forensics report of [Detection, alerts and reports](detection-and-reports.md) (*Forensics report*) behind the login:
  the head, the summary, the recurring patterns, the memory consumers, one section per outage and the notes, as headings, paragraphs,
  lists and tables. The page and `vandoxd report` compose the report from the same blocks, so for the same data, clock, lookback and
  language they show the same texts, numbers and times in the same order. **The same content per page of outages:** the page lists at most
  **50 outages** (below), while the command and the download list all of them; the summary, the patterns, the consumers and the notes are those of
  the whole run.
- **The language comes from the address.** `report` is English; `report?lang=en` and `report?lang=de` choose a language, the key in any letter
  case and percent-encoded, the value compared after decoding with exactly `en` or `de` (so `lang=%64%65` is German). Any other value (`DE`,
  `de-DE`, an empty value, `lang` without `=`, a value with a space, a control or format character) shows `The address has an invalid value for the
  parameter lang.`; a key given more than once, in any letter case or with an empty value, shows `The address gives the parameter lang more than
  once.`; other keys are ignored and the fragment is never read. A refused address runs nothing and shows no report. A navigation to another
  language runs the report again from the first outage.
- The page offers two links to the languages (*English*, *Deutsch*, the shown one marked) and the download link *Download Markdown* for the
  language shown (`report/markdown?lang=<language>` with the `download` attribute). All links are built from fixed paths and the validated language.
- It runs only once the page is interactive. Prerendered it shows its title and `Making the report… It reads every stored log line, which can take a
  few minutes on a large database.` and reads nothing. Leaving the page, or a new address, cancels the run in the store.
- **Outages in pages of 50.** A run keeps only the outages of its page, counted in the order of the stored windows. With more than 50 outages the
  page says `Outages <first> to <last> of <total>.` and offers *Next outages* (a new run that shows the following 50) and, on a later page,
  *First outages* (a new run from the first). A later page whose run finds fewer outages than its start (the stored outages shrank since the
  previous page) shows no range and no outages but still offers *First outages*. The position is not in the address. A new run reads the store again, so the outages of a
  page are those `vandoxd incidents` stored at that time.
- **External text** (victim commands and program names) is shown only as the text of an `ExternalText` inside a `code` element, capped at
  2,048 displayed characters per text with the marker of the logs view, with invisible characters as visible escapes; an empty text is the dash `—` as
  in the Markdown. No external text is in a heading or a table cell, and the page uses no `MarkupString`.
- **Failures.** A run that reaches its deadline shows `Making the report took longer than 2 minutes and was stopped.`; a store failure shows `The report could
  not be made. The service log has the details.` and is logged with the exception only (`report page: making the report failed`). No report is shown in
  either case.

**The download (`GET /report/markdown?lang=<language>`).**

- It needs a session (anonymous: `302` to the login page) and exists on the web listener only (`404` on the ingest listener). The language is read
  from the query string by the same rules as the page; a refused one is answered with `400` and no body before anything is read.
- The body is, byte for byte, standard output of `vandoxd report -lang <language>` for the same data and clock: the whole report (all outages), UTF-8
  without a byte order mark. The response carries `Content-Type: text/markdown; charset=utf-8`, `Content-Disposition: attachment;
  filename="vandox-report-<language>.md"` and `Cache-Control: no-store`, besides the security headers of every response (`X-Content-Type-Options:
  nosniff` keeps a browser from reading the Markdown as HTML). The report is written to the response as it is made; it is not held in memory.
- A run that reaches its deadline before anything is written (also while it waits for the run slot) is answered with `503`, a store failure before it with `500` (logged with the exception
  only: `report download: making the report failed`), both without a body and without the headers of the file (`Cache-Control: no-store` and the security headers stay). Nothing is flushed to the response before the first part of the report, since a flush
  starts the response on the server. Once the first byte is written the status
  cannot change: a failure then aborts the connection, so that no truncated file looks complete (a deadline is not logged, as in the views). A client
  that closes the connection ends the run silently.

**The run.** One report runs at a time across the service: the page and the download share one run slot, and a second request waits for it
within its own deadline of **2 minutes**, which counts the wait for the slot and the run, and stops waiting when its page is left or its
connection closes. The store calls of a run take the gate of the logs and incidents views (*Limits and errors*); the incidents are read
one at a time, as by the command, and the web cannot write an incident (the incident store of the run refuses to save). The deadline cancels the store
call in flight; the statements that take no token run to their end first: after the last page of lines the analysis seals the series and
summarizes (about 30 ms on the database below, at most about 0.5 s with a million snapshots over a million observed hours, measured with `Seal`
and `Summarize`), and the writer finishes the outage it is writing, so a run ends within about a second after the deadline. A run takes
56 to 60 seconds on the development host (4 vCPU Intel Xeon 2.10 GHz, .NET 10, Release) over a database of 5,000,000 log lines (1.6 GB), 43,200 process
snapshots and 64 incidents, and 67 seconds as `vandoxd report` (Debug build). A slower host or a larger database can exceed 2 minutes: the reference host
wrote the 5,000,000 lines about four times slower than the development host ([BENCHMARKS.md](../BENCHMARKS.md)), so there a run of this size may take
several minutes and is then stopped; `vandoxd report` has no deadline and remains for it.

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
- A live connection needs a session ([Web login](web-login.md), *Access*): without one, the `/_blazor` endpoints answer `401`. The
  live connection does not check the `Origin` header, and the service does not filter the `Host` header; the session and the
  protected component descriptors of the signed-in page decide who may use it.
- **The logs view is behind the login** ([Web login](web-login.md)): `/logs` and `/logs/templates` carry the same `[Authorize]` as every page
  below `Components/Pages`, and an anonymous request is redirected to the login page. The host still has no `Host`-header allow-list. A page reached through a **DNS-rebound** name does not get the session: the session cookie has no `Domain` attribute, so the browser sends it only to the host name the user signed in at, never to another name that resolves to the same address (this holds whatever `SameSite` says). Such a page reaches only what needs no session — `/login`, where the failed-sign-in limits apply, the static files and `/healthz` — and cannot read the logs without the password.
- **Memory per circuit and the number of circuits.** The search page holds the capped text of its lines, not the stored records: a
  line holds up to 16,384 characters of message and up to 2,048 characters in each of source, host, program, about 45 KiB, so a page
  of 100 lines with an expanded context of 11 lines is about **5 MiB** in the worst case (a typical line is a few hundred bytes), and the
  template page, with up to 200 rows of four capped short fields and a chart of at most 2,160 points, about **3.1 MiB**. The render batch
  that sends the page to the browser holds the same text once more, for a moment. The **number of circuits is not bounded** (the framework keeps up to 100 disconnected circuits for 3 minutes, *Pages and render mode*), and the container has 512 MB, so
  many open log pages can exhaust the memory of the service. Only a signed-in client can open a circuit ([Web login](web-login.md), *Access*),
  so this needs the password or a session.
- **The incidents view is behind the login** like the logs view (`/incidents` and `/incidents/<number>` carry the same `[Authorize]`, and the note
  endpoint has no `AllowAnonymous`, so the fallback policy demands a session). It adds the first write of the web UI, a note, under the
  token rule of [Web login](web-login.md).
- **Memory per circuit (incidents view).** The list holds 20 summaries. The incident page decodes one incident (up to 8 MiB of JSON, for the
  moment of the read) and keeps only capped text: up to 1,000 timeline entries with texts of at most 512 characters and every other field at most
  2,048, at most 64 notes of 1,800 characters, and charts of at most 300 points of six series, two series of 1,000 points and 300 points; while the
  window is read it holds one snapshot at a time and at most 4,096 programs times 300 snapshots of values (about 10 MB). These amounts are
  **per circuit**, not across the service: the gate is held per store call, so many open incident pages can each build at once, as with the
  5 MiB of a logs page. The number of circuits is not bounded ([Pages and render mode](#pages-and-render-mode)) and the container has 512 MB; only a signed-in client can
  open a circuit.
- **The report page is behind the login** like the other pages (`/report` carries the same `[Authorize]`, and the download endpoint has no
  `AllowAnonymous`, so the fallback policy demands a session). It reads only: no gap, no alert and no write; the incident store of the run
  refuses `SaveIncidentAsync`.
- **Memory per circuit (report page).** The page keeps the document of one run: the summary, at most 50 outages and the notes, as text. External text
  is at most 22 texts per outage (16 victims, 3 growth candidates, 3 consumers), each a stored text of at most 1,024 bytes (at most 2 KiB as
  UTF-16) plus its runs for the page (at most 2,048 displayed characters, 4 KiB), so up to about 6 KiB each, about 130 KiB per outage and
  **about 6.6 MB** for 50 outages, 60 KB more for the 10 consumers of the summary, plus the markup and the render tree (a typical outage holds a few hundred bytes of
  external text); the render batch holds the text once more for a moment. This is **per circuit**. The run itself keeps up to about 60 MB once across the whole
  service, because only one run goes at a time ([Detection, alerts and reports](detection-and-reports.md), *Limits of the report*). The number of
  circuits is not bounded ([Pages and render mode](#pages-and-render-mode)) and the container has 512 MB; only a signed-in client can open a circuit.
- **Cross-site opening.** The session cookie is `SameSite=Lax`, so another site can make a signed-in browser open `/report` or `/report/markdown`
  and so start a run. It changes nothing, the other site cannot read the response of a top-level navigation, the page cannot be framed, and the
  cost is bounded by the one run slot and the deadline of 2 minutes. A site that keeps sending such navigations keeps the run slot taken, so the report of
  the signed-in user waits, up to 2 minutes each time; this is an availability limit only. A client that reads the download slowly is cut off only by the
  default minimum response data rate of the server, which the service leaves unchanged.
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
- [0106](../decisions/0106-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md) — why the logs view takes its filters from the address, searches in bounded pages and caps external text.
- [0107](../decisions/0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md) — why the incidents view reads summaries and the window's own records, saves notes by a form post and links every time to the logs.
- [0108](../decisions/0108-report-page-one-block-model-outage-pages-by-rerun-language-in-the-address.md) — why the report page renders the blocks the Markdown is written from, lists the outages in pages of 50 by a new run, takes the language from the address and runs one report at a time under 2 minutes.

## Not here

- Listeners, port routing, `/healthz`, the container and exposure: [Backend host](backend-host.md).
- Who may reach the web UI: [Network and security model](network-and-security-model.md).
- Logo files and color tokens: [BRANDING.md](../BRANDING.md).
- Login, sessions, CSRF, the failed-sign-in limits and the trust in a reverse proxy: [Web login](web-login.md). The setup of a proxy:
  [REVERSE_PROXY.md](../REVERSE_PROXY.md).

## Implementation

`Vandox.Backend` (`Components/App.razor`, `Components/Layout/MainLayout.razor`, `Components/Charts`, `Components/Pages/LogsPage` and
`LogTemplatesPage`, `Components/Pages/IncidentsPage` and `IncidentPage`, `Components/Incidents/IncidentNoteForm`, `Components/Pages/ReportPage`, `Components/Reports/ReportContent`, `Components/Logs/ExternalText`, `Logs/` (`LogsAddress`, `LogText`, `LogLineView`, `GatedLogExplorer`, `WebReaderGate`, `TemplateHourSeries`, `LogsView`),
`Reports/` (`ReportService`, `ReportViewBuilder`, `ReportDocument`, `ReportAddress`, `ReportsView`, `ReportDownloadEndpoint`, `GatedIncidentStore`, and the blocks `ReportComposer`, `ReportBlock`, `ReportPiece`), `Logs/ActionRun`, `Logs/ActionSequence` and `Logs/PageRunner` (the deadline of an action),
`Incidents/` (`IncidentsView`, `IncidentRowView`, `IncidentDetailView`, `IncidentWindowReader`, `ProgramMemorySeries`, `GatedIncidentBrowser`, `GatedRecordReader`, `IncidentNoteText`, `IncidentNoteEndpoint`), `Hosting/SecurityHeadersMiddleware`,
`Hosting/ServeCommand`, `wwwroot/`, and `Vandox.Backend.csproj`, whose `RequiresAspNetWebAssets` makes the restore add the framework
script independently of the sources, as the image build restores before copying them; record 0098).
