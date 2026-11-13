# Logs view

## Scope

The two pages of the [web UI](web-ui.md) that read the stored log lines: the search (`/logs`) and the template list
(`/logs/templates`). It covers their address, what they show, their calls and deadline, and their limits. The user relies on it to
find the log lines around a moment and the templates a source logs. The store behind them is the log exploration of
[Storage reading and log exploration](storage-reading.md), the templates are those of [Log templates](log-templates.md).

## Behavior

### Pages

- Both pages show the links *Search* and *Templates*, work on the same address parameters, and are served on the web listener only (the
  ingest listener answers `404`). Like every page they need a session ([Web login](web-login.md)); a signed-in user can read every
  stored log line.
- Every link of the view is one of the fixed paths `logs` and `logs/templates` plus the query of a validated address.

### Address

Every filter is in the address, so a search can be bookmarked, shared and used with the browser's back and forward buttons. An address
is written only from validated values: the parameters in this order, each percent-encoded, with empty ones left out:

| Parameter | Value |
| --------- | ----- |
| `q` | the search text (the literal-term rules of [Storage reading and log exploration](storage-reading.md), *Log search*) |
| `source` | one source, exact |
| `program` | one program, exact |
| `severity` | `emerg`, `alert`, `crit`, `err`, `warning`, `notice`, `info` or `debug` (lower case); shows lines with that priority or a more severe one, and no line without a priority |
| `template` | a template number (1 to 18 ASCII digits): on `/logs` the lines of that template, on `/logs/templates` the template whose chart is shown |
| `from`, `to` | `yyyy-MM-ddTHH:mm` or `yyyy-MM-ddTHH:mm:ss` in ASCII digits within the storable range, read as **UTC**; `from` is inclusive, `to` exclusive |

- `q`, `source` and `program` are valid UTF-16 of at most 1,024 UTF-8 bytes after decoding and hold no character of category Cc, Cf, Zl
  or Zp (white space other than U+0020 included).
- A key is matched without regard to ASCII case and may be percent-encoded; any other key is ignored. The part of the address after `#`
  is never read.
- A parameter given more than once, even with an empty value, is refused with `The address gives the parameter <name> more than once.`
  A value that breaks its rule is refused with `The address has an invalid value for the parameter <name>.`, and `from` not before `to`
  with `The address has a start time that is not before its end time.` An error text names the parameter, never the value. An address
  with an error calls no store method.
- The validated values reach only the literal-term search, bound SQL parameters, or integer and date parsing with fixed formats.
- Without `from` and `to` the whole stored range is searched. A time field the user leaves empty is left out of the address. The filter
  form is a GET form to the same page, so it also works without script; with script it validates the entered values first and shows the
  same error texts instead of navigating.
- The page follows every change of its address (submitting the form, a link, back and forward) and then searches what the new address
  says; the results always come from the address. Submitting the form searches again even when the address is unchanged, which
  refreshes the results.

### Search page

- The page lists the matching lines **oldest first**, 100 per page: time (UTC, `yyyy-MM-dd HH:mm:ss.fff`), source, host, program,
  process ID, severity name (`-` when the line has no priority) and message. There is no total count of the matches.
- Prerendered it shows the filters and `Searching…` and queries nothing ([Web UI](web-ui.md), *Pages and render mode*).
- A page that is full offers *Next page* (the next 100 lines) and *First page*.
- A search that stops at the examined-lines bound before it has a full page (a common word or a filter that few lines pass, in a large
  range) shows `Searched up to <time> UTC.` with *Continue*, which searches on from there. The bound is **8,000** examined lines per
  request, fewer for a text of several word parts (8,000 divided by the parts, so an IP address of four parts examines 2,000). The page
  may then hold fewer than 100 lines, or none.
- When the store could not finish its index check within 0.7 s (a text of many common words, or of a term of several common parts such
  as an IP address, over a large range), the search goes through the time range step by step the same way and the page says so above
  *Searched up to* with `These words are too common to look up at once, so the search goes through the time range step by step. Narrow the
  time range to get a complete answer.` Such a search may give no final "no lines" answer until the time range is narrowed, and every
  *Continue* pays the 0.7 s again. The text is shown only for a page that stopped at the bound after a stopped index check, not for an
  ordinary bound page.
- A scan page, with or without the stepwise text, also ends when **1.5 seconds** have passed since the request began (the 0.7 s of the
  index check included) with `Searched up to … Continue`, so on a slow or loaded host a search of a very common word may end sooner than
  its line bound; at least one line is always examined, so *Continue* advances. The deadline of 10 seconds stays the outer limit.
- **The page position is not in the address**: *Next page*, *First page* and *Continue* add no history entry, so the browser's back
  button leaves the search, not the page, and a reloaded or shared address opens the first page.
- *Context* on a line shows the 5 lines before and the 5 lines after it from the same log of the same source and host (time, program and
  message of each), at most 10 lines around a marker for the hit; opening another replaces it. Lines of other logs of the source are
  skipped, and each side looks at no more than 2,000 lines of the source.
- With `template` the page shows the template (text, source, program, first and last seen, its number of lines) above the lines, which
  are the lines of that template, and a link to remove the filter. A template number the store does not know (templates are renumbered
  when `vandoxd templates` rebuilds the statistics) shows `This template does not exist. Template numbers change when the statistics are
  rebuilt.` and searches nothing.
- The source list of the form holds the stored sources (at most 64); a source of the address that is not in it stays selectable.

### Template page

- It lists the templates with lines in the range, optionally of one source and one program, most lines first, at most 200: template text,
  source, program, first and last seen (UTC), lines in total and **lines in range**. The range count is counted in **whole hours**: a
  `from` or `to` inside an hour counts that hour completely, and the column is labelled `Lines in range (whole hours)`. Template numbers
  and counts change when the statistics are rebuilt.
- *Chart* selects a template (`template` on this page): it is shown above the list with a link *Show lines* to `logs?template=<id>` with
  the same `from` and `to`, and a chart of its lines per hour for the last 2,160 hours of the range (`to` minus 2,160 hours at the
  earliest). The chart has one point per hour from the first to the last hour of the range that holds a line of the template or is
  covered by the template's source: the count, **0** for an hour in which the source logged but the template did not, and a **gap** for an
  hour in which the source logged nothing (unknown, never zero). Its title (`Lines per hour`) and series label (`Lines`) are fixed texts;
  no log text goes into the chart.

### External text

Source, host, program, message and template text are shown only through the external-text display of the [web UI](web-ui.md)
(*External text*): a message up to 16,384 characters and every other field (source, host, program, template text) up to 2,048. The caps
leave every field within the stored limits (source 128 bytes, host 1,024, program 1,024, message 16,384) whole unless it holds characters
that are escaped, which are longer when written out. A page keeps the capped text of its lines, not the stored records.

### Calls and deadline

- Each page has at most one store call running: the calls of one action (opening an address, *Next page*, *Continue*, *Context*) run one
  after another, and a new action cancels the running call and waits for its end before the next one starts. Leaving the page cancels it.
- Every call runs under a deadline of **10 seconds**, which includes the wait for a slot of the shared read gate
  ([Web UI](web-ui.md), *Store reads of the views*). The search page then shows `The search took longer than 10 seconds and was stopped.
  Narrow the time range or add filters.`, the template page `The query took longer than 10 seconds and was stopped. Narrow the time range
  or choose a source.`
- A search the store refuses shows `The search cannot run: <rule>.` (the store's rule, which never contains the search text, for example
  that the text has more than 16 terms). Any other error shows `The logs could not be read. The service log has the details.` (templates:
  `The templates could not be read. The service log has the details.`); the details are only in the service log.

## Limits and errors

- **Memory per circuit.** The characters are held as runs, at most 256 a field. With the runs, a message takes up to 56 KiB and a host or
  program up to 24 KiB (bounds pinned by tests); the source is at most 128 ASCII characters, never escaped, one run. A line takes about
  104 KiB, so a search page of 100 lines with an expanded context of 11 lines is about **11 MiB** in the worst case (a typical line is
  a few hundred bytes), and the template page, with up to 200 rows of a template text and a program of up to 24 KiB each and a chart of
  at most 2,160 points, about **9.5 MiB**. The number of circuits is not bounded ([Web UI](web-ui.md), *Limits and errors*).
- Template numbers change when `vandoxd templates` rebuilds the statistics, so a stored template link can point to another template or to
  none; the page reports an unknown number.
- Every stored source can be chosen in the form, because the data model allows a source only letters, digits and `. _ : / @ + -` and at
  most 128 bytes, which the address rules accept; a source in the address that the store does not know finds no lines.

## Related decisions

- [0106](../decisions/0106-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md) — why the logs view takes its filters from the address, searches in bounded pages and caps external text.
- [0207](../decisions/0207-incidents-view-shows-each-external-text-in-at-most-16-runs-logs-view-keeps-256.md) — why the logs view keeps 256 runs a field while the incidents view takes 16, and the memory figures with the run cost.

## Not here

- The general web UI (render mode, gate, external-text display, headers, circuits): [Web UI](web-ui.md); the other views:
  [Incidents view](web-ui-incidents-view.md), [Report page](web-ui-report-page.md).
- The search and its bounds in the store: [Storage reading and log exploration](storage-reading.md).
- Templates and their statistics: [Log templates](log-templates.md).
- The login: [Web login](web-login.md).

## Implementation

`Vandox.Backend`: `Components/Pages/LogsPage` and `LogTemplatesPage`; `Logs/` (`LogsAddress` with `Parse`, `Validate` and `ToQuery`,
reading the query of `new Uri(NavigationManager.Uri).Query` through `QueryHelpers.ParseQuery`; `Severities`; `LogLineView`, which builds
the capped runs; `TemplateRowView`, `TemplateHourSeries`, `LogsView` with the chart texts `ChartTitle` and `ChartSeries`;
`GatedLogExplorer`; `ActionRun`, `ActionSequence` and `PageRunner`, the deadline of an action).
