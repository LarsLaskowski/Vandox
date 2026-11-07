# Report page

## Scope

The page of the [web UI](web-ui.md) that shows the forensics report (`/report`), its Markdown download (`GET /report/markdown`) and the
run behind both: language from the address, outages in pages of 50, one run at a time under a deadline of 2 minutes. The user relies on
it to read the report in a browser without a shell. What the report says is in [Forensics report](forensics-report.md) and
[Listener changes](listener-changes.md).

## Behavior

### Page (`/report`)

- The page shows the forensics report behind the login: the head, the summary, the recurring patterns, the memory consumers, the new
  listeners, one section per outage and the notes, as headings, paragraphs, lists and tables. The page and `vandoxd report` compose the
  report from the same blocks, so for the same data, clock, lookback and language they show the same texts, numbers and times in the same
  order.
- **The same content per page of outages:** the page lists at most **50 outages** (below), while the command and the download list all of
  them; the summary, the patterns, the consumers, the new listeners and the notes are those of the whole run.
- The page offers two links to the languages (*English*, *Deutsch*, the shown one marked) and the download link *Download Markdown* for the
  language shown (`report/markdown?lang=<language>` with the `download` attribute). Its only links are the fixed paths
  `report?lang=<en|de>` and `report/markdown?lang=<en|de>`, built from the validated language.
- It runs only once the page is interactive. Prerendered it shows its title and `Making the report… It reads every stored log line, which
  can take a few minutes on a large database.` and reads nothing. Leaving the page, or a new address, cancels the run in the store.
- **Outages in pages of 50.** A run keeps only the outages of its page, counted in the order of the stored windows. With more than 50
  outages the page says `Outages <first> to <last> of <total>.` and offers *Next outages* (a new run that shows the following 50) and, on a
  later page, *First outages* (a new run from the first). A later page whose run finds fewer outages than its start (the stored outages
  shrank since the previous page) shows no range and no outages but still offers *First outages*. The position is not in the address. A
  new run reads the store again, so the outages of a page are those `vandoxd incidents` stored at that time.
- **Failures.** A run that reaches its deadline shows `Making the report took longer than 2 minutes and was stopped.`; a store failure
  shows `The report could not be made. The service log has the details.` and is logged with the exception only (`report page: making the
  report failed`). No report is shown in either case.

### Language

- `report` is English; `report?lang=en` and `report?lang=de` choose a language. The key matches in any letter case and percent-encoded;
  the value is compared after decoding with exactly `en` or `de` (so `lang=%64%65` is German).
- Any other value (`DE`, `de-DE`, an empty value, `lang` without `=`, a value with a space, a control or format character, a
  percent-encoded look-alike such as `%E2%80%AEde` or `en%00`) shows `The address has an invalid value for the parameter lang.`; a key
  given more than once, in any letter case or with any value, also an empty one, shows `The address gives the parameter lang more than
  once.` Other keys are ignored and the fragment is never read.
- A refused address runs nothing and shows no report. A navigation to another language runs the report again from the first outage.

### External text

- The victim commands, program names, the owner commands and the socket user names of the new listeners and the agent IDs of that section
  are shown only through the external-text display of the [web UI](web-ui.md) (*External text*) inside a `code` element, capped at 2,048
  displayed characters per text; the user name is first cut as [Forensics report](forensics-report.md) (*External text in the report*)
  says. An empty text is the dash `—`, as in the Markdown.
- External text is only ever a piece of a paragraph or list item, never in a heading or a table cell. The labels, numbers and times are
  plain text of the report. The page writes no raw HTML and no `style` attribute.

### The download (`GET /report/markdown?lang=<language>`)

- It needs a session (anonymous: `302` to the login page) and exists on the web listener only (`404` on the ingest listener). The
  language is read from the query string by the rules of *Language*; a refused one is answered with `400` and no body before any store
  call.
- The body is, byte for byte, the standard output of `vandoxd report -lang <language>` for the same data and clock: the whole report (all
  outages), UTF-8 without a byte order mark. The response carries `Content-Type: text/markdown; charset=utf-8`, `Content-Disposition:
  attachment; filename="vandox-report-<language>.md"` (only this fixed name, built from the validated language) and `Cache-Control:
  no-store`, besides the security headers of every response (`X-Content-Type-Options: nosniff` keeps a browser from reading the Markdown
  as HTML). The report is written to the response as it is made; it is not held in memory.
- A run that reaches its deadline before anything is written (also while it waits for the run slot) is answered with `503`, a store
  failure before it with `500` (logged with the exception only: `report download: making the report failed`), both without a body and
  without the headers of the file (`Cache-Control: no-store` and the security headers stay). Nothing is flushed to the response before the
  first part of the report, since a flush starts the response on the server.
- Once the first byte is written the status cannot change: a failure then aborts the connection, so that no truncated file looks complete
  (a deadline is not logged, as in the views). A client that closes the connection ends the run silently.

### The run

- **One report runs at a time across the service**: the page and the download share one run slot, and a second request waits for it
  within its own deadline of **2 minutes**, which counts the wait for the slot and the run, and stops waiting when its page is left or its
  connection closes.
- The store calls of a run take the shared read gate ([Web UI](web-ui.md), *Store reads of the views*); the listener reads take one slot
  per read. The incidents are read one at a time, as by the command.
- The run only reads: no gap, no alert and no write; its incident store refuses to save, so the web cannot write an incident.
- The deadline cancels the store call in flight; the steps that cannot be cancelled run to their end first: after the last page of lines
  the analysis seals the series and summarizes (about 30 ms on the database below, at most about 0.5 s with a million snapshots over a
  million observed hours, measured), and the writer finishes the outage it is writing, so a run ends within about a second after the
  deadline.
- A run takes 56 to 60 seconds on the development host (4 vCPU Intel Xeon 2.10 GHz, .NET 10, Release) over a database of 5,000,000 log
  lines (1.6 GB), 43,200 process snapshots and 64 incidents, and 67 seconds as `vandoxd report` (Debug build). A slower host or a larger
  database can exceed 2 minutes: the reference host wrote the 5,000,000 lines about four times slower than the development host
  ([BENCHMARKS.md](../BENCHMARKS.md)), so there a run of this size may take several minutes and is then stopped; `vandoxd report` has no
  deadline and remains for it.

## Limits and errors

- **Memory per circuit.** The page keeps the document of one run: the summary, at most 50 outages and the notes, as text.
  - Outages: at most 22 external texts per outage (16 victims, 3 growth candidates, 3 consumers), each a stored text of at most 1,024
    bytes (at most 2 KiB as UTF-16) plus its runs for the page (at most 2,048 displayed characters, 4 KiB), so up to about 6 KiB each,
    about 130 KiB per outage and **about 6.6 MB** for 50 outages. This 6 KiB counts characters; it is not the worst case of the runs,
    which is measured below for a command of 1,024 bytes (about 22.9 KiB) and has not been measured for the outage texts: the victims and
    programs take the same runs path, so their share is to be restated with the commands' in
    [#356](https://github.com/LarsLaskowski/Vandox/issues/356). The 10 consumers of the summary add 60 KB.
  - The new listeners section: **up to about 82 MB** — at most 20 findings after an agent start, 20 found by the agent and 20 found by the
    agent with changes only, each with at most 64 listeners and up to 64 changed endpoints, and the head of at most 64 agent IDs of at most
    64 bytes each; the agent ID, each command and each user name are external text in code. A text's cost is set by its runs and is worst
    when plain and escaped characters alternate: a command of 1,024 bytes takes up to about 22.9 KiB with its runs (measured), about
    1.5 MB a finding and **about 60 MB** for the commands; each user name, cut to 32 bytes, takes up to about 3 KiB (a bound pinned by a
    test), about 7.9 MB. The changed endpoints add no command: a changed line is three to four plain texts and one user code span, up to
    about 3.6 KiB (the 3 KiB user bound plus about 0.6 KiB computed for the endpoint, PID, UID and before texts, not measured as a whole),
    so 60 findings of 64 lines (3,840 lines) take up to about 14 MB, which brings the section from about 68 MB to about 82 MB. The earlier
    figure of about 16 MB for this section understated the commands; bounding their share is the follow-up
    [#356](https://github.com/LarsLaskowski/Vandox/issues/356).
  - Plus the markup and the render tree (a typical outage holds a few hundred bytes of external text). This is **per circuit**; the number
    of circuits is not bounded ([Web UI](web-ui.md), *Limits and errors*).
- **Memory of the run.** The run itself keeps up to about 60 MB plus about 20 MB for the listener section once across the whole service,
  because only one run goes at a time ([Forensics report](forensics-report.md), *Limits of the report*).
- **Cross-site opening.** The session cookie is `SameSite=Lax`, so another site can make a signed-in browser open `/report` or
  `/report/markdown` and so start a run. It changes nothing and reveals nothing: the other site cannot read the response of a top-level
  navigation, the page cannot be framed, and the cost is bounded by the one run slot and the deadline of 2 minutes. A site that keeps
  sending such navigations keeps the run slot taken, so the report of the signed-in user waits, up to 2 minutes each time; this is an
  availability limit only.
- A client that reads the download slowly is cut off only by the default minimum response data rate of the server, which the service
  leaves unchanged.

## Related decisions

- [0108](../decisions/0108-report-page-one-block-model-outage-pages-by-rerun-language-in-the-address.md) — why the report page renders the blocks the Markdown is written from, lists the outages in pages of 50 by a new run, takes the language from the address and runs one report at a time under 2 minutes.
- [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md) — why the report shows the new listeners section.

## Not here

- What the report contains, its Markdown escaping and its limits: [Forensics report](forensics-report.md), [Listener changes](listener-changes.md).
- The general web UI (render mode, gate, external-text display, headers, circuits): [Web UI](web-ui.md); the other views:
  [Logs view](web-ui-logs-view.md), [Incidents view](web-ui-incidents-view.md).
- The login and the session cookie: [Web login](web-login.md).

## Implementation

`Vandox.Backend`: `Components/Pages/ReportPage`, `Components/Reports/ReportContent` (external text as `<code><ExternalText Runs="…" /></code>`
with `LogText.Runs(text, LogText.MaxShortChars)`, pieces as `ReportPiece.Code`); `Reports/` (`ReportService` with the run slot,
`ReportsView` with `Deadline`, `PageHref`, `DownloadHref` and `FileName`, `ReportViewBuilder`, `ReportDocument`, `ReportAddress` with
`Parse` and `ToQuery`, `ReportDownloadEndpoint`, `GatedIncidentStore` whose `SaveIncidentAsync` refuses, `GatedListenerHistoryReader`, and
the blocks `ReportComposer`, `ReportBlock`, `ReportPiece`); the uncancellable steps are `ForensicsAnalysis.Seal` and `Summarize`; the user
name bound is pinned by `LogTextRunsOfAlternatingShortNameRetainBoundedHeap`.
