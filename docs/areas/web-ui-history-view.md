# History view

## Scope

The page `/history` of the [web UI](web-ui.md): the address that holds the chosen metrics, the range and the moment, the controls, the
chart of up to eight metrics with its notes, the list of events, and the panel of the moment that shows the process and the connection
snapshot closest to a point in time. It covers the texts, the calls and the deadline, and the caps with the memory per circuit. The user
relies on it to look at chosen metrics over a period side by side and to see what the server was doing at a moment of the chart. The
query behind the chart is [Time-series queries](time-series-query.md); the three reads the page adds are described in
[Storage reading](storage-reading.md), *History reads*.

## Behavior

### Page

- The page is behind the login ([Web login](web-login.md)) and is served on the web listener only. The navigation entry *History*
  (`history`) lies between *Overview* and *Logs* and is marked on `/history` with or without a query.
- Prerendered (the first response) the page shows the heading, the range controls, the metric choice and `Loading…` and reads nothing; it
  reads once its live connection is up ([Web UI](web-ui.md), *Pages and render mode*). An address with an error is shown the same way.
- Without a chosen metric the page shows only the controls and `Choose one or more metrics to draw.`: no chart, no marker, no event
  list, and no query runs.
- A link of the view is `history` or `logs` plus the query of a validated address; the page builds no other link.

### Address

Everything the page shows is in the address, so it can be bookmarked, shared and used with the browser's back and forward buttons.

| Parameter | Value |
| --------- | ----- |
| `metric` | `<source>,<name>`, repeatable; each part is 1 to 128 bytes and matches the name pattern of the wire format (starts with an ASCII letter or digit, then letters, digits and `. _ : / @ + -`; a trailing line feed is refused); at most 8 distinct metrics, a repeat is used once, an empty value is skipped |
| `range` | `1h`, `24h`, `7d`, `30d` or `1y` (365 days), up to now |
| `from`, `to` | `yyyy-MM-ddTHH:mm` or `yyyy-MM-ddTHH:mm:ss` in ASCII digits within the storable range, read as **UTC** (the logs view's formats) |
| `at` | the moment, in the same formats; the formats have no fraction, so a moment is a whole second |

- A key is matched without regard to ASCII case and may be percent-encoded; any other key is ignored; the part after `#` is never read.
  A raw `+` is read as a space, as the logs view reads it; a metric with a plus is written as `%2B` and reads back as written.
- Without `range`, `from` and `to` the range is the last 24 hours, and the address is not extended: the *24 h* button is marked, and
  choosing it writes `range=24h`. A preset stays relative: the address holds `1h` and not the time it was chosen, so a copied address
  shows another signed-in user the same metrics and moment over the preset up to their own now. A zoom, *From* and *To* are fixed.
- The parameters are read in the order `metric`, `range`, `from`, `to`, `at`, and the first error wins; then come the combination
  rules. The errors name the parameter, never its value:
  - `The address gives the parameter <name> more than once.` (not for `metric`);
  - `The address has an invalid value for the parameter <name>.`;
  - `The address names more than 8 metrics.`;
  - `The address gives both a preset range and a start or end time.`;
  - `The address needs both a start and an end time.`;
  - `The address has a start time that is not before its end time.`
- An address with an error shows the heading, the presets, *From*, *To* and the text, and nothing else; it calls no store method. The
  text of a control (below) is no address error: it shows with the working page.
- An address is written only from values checked again (pattern and byte length of every metric, at most 8 distinct, storable times, the
  combination rules), the parameters in the order `metric`, `range` (only for a preset), `from` and `to` (only as a pair), `at`, each
  percent-encoded, a time without seconds when its second is 0. A value the checks refuse is shown as the address text without
  navigating.
- The page follows every change of its address. A change of the metrics, the preset, `from` or `to` (the order of the metrics counts)
  reads the chart again; a change of `at` alone reads only the snapshots.

### Controls

- Presets *1 h*, *24 h*, *7 d*, *30 d*, *1 y*; the current one is marked. *From (UTC)* and *To (UTC)* with *Apply*: both fields are
  checked with the address rules and show their texts without navigating; empty fields give the default range. The text of a control
  shows after the metric controls and before the chart, with the metric choice, the chosen metrics, the chart, the events and the
  moment panel still shown; the next navigation clears it.
- The metric choice lists the stored metrics as `<source> <name>`; *Add* adds the chosen entry (an entry already chosen is ignored),
  *Remove* beside a chosen metric removes it. A ninth metric shows `At most 8 metrics can be drawn at once.`
- Adding or removing a metric, a preset, *Apply* and a drag selection each write a **new history entry**, so back returns to the
  previous state. A click in the chart, the time of an event and *Snapshot before this moment* **replace** the current entry.
- Metric sources and names are shown as plain text: the address pattern admits only ASCII characters that need no escape.

### Metric list

- The list holds every source and name that has a stored raw value or a kept hourly rollup, by source, then name (ordinal), at most 256
  entries. When more exist the page adds `The list shows the first 256 metrics; others can be named in the address.`
- The list is read once per open page, at its first interactive load of a valid address, whatever the address holds, and not for every
  change; entries a valid address would refuse are left out. A metric of the address that is not in the list stays chosen and drawn.

### Chart

- The query asks for the chosen metrics in the order of the address, the range (the preset up to the time of the read, or `from` to
  `to`) and a **target number of points** equal to the chart's width in CSS pixels, at most 1,000 (a width below 1 or not finite gives
  1). The width is reported when the chart is first shown; a later resize redraws the same points and reads nothing.
- A range that begins at or after now shows `This range lies in the future.` and reads nothing.
- **Lines.** Every stored label set of a metric is a line (`cpu_percent` once per `mode`), named `<source> <name> <labels>` in the legend,
  or `<source> <name>` without labels; at most 64 lines. The colors are the six chart colors in order (`--vandox-chart-1` to
  `--vandox-chart-6`); from the seventh line they repeat dashed. A gap breaks a line and is never drawn across; between samples a line
  is drawn through ([Web UI](web-ui.md), *Charts*).
- **Scales and axes.** Every metric has its own vertical scale, fitted to its lines. The axis of the first metric is drawn on the left,
  the axis of the second on the right, each labelled `<source> <name>`; below 480 CSS pixels of chart width only the left one is drawn
  (the width is checked again on every resize). The other metrics have a scale but no drawn axis: read their values in the legend.
- **Legend.** The legend shows the value of each line under the pointer with one decimal, `--` without a value. At a step (every
  resolution but raw) each point is the average of the step, the legend shows `<minimum> / <average> / <maximum>`, and the page says
  `Each point is the average of <step>; the legend shows the minimum and maximum.` (`<step>` is `<n> minutes`, `1 hour`, `<n> hours`,
  `1 day` or `<n> days`). At raw resolution the legend shows the value and the page has no such text.
- **Markers.** The chart marks the events of the range: an OOM kill as a solid line, a boot dashed, a service restart dotted, and a backup
  run as a shaded span from its start to its end (to the end of the range while it runs or its end is unknown). The moment is a
  line in the brand color, drawn only when it lies inside the range. The marks follow a change of theme.
- **Selection.** Dragging across the chart with the mouse by 5 CSS pixels or more selects a period: the browser reports its start and
  end as Unix seconds, the page drops a value that is not finite or lies outside the storable range, and widens the period outward to
  whole seconds. It becomes the fixed range of the address (a preset is dropped, the metrics and the moment stay). There is no
  selection by touch.
- **Click.** A press that moved by less than 5 CSS pixels is a click. The browser reports the time under the pointer and the time span of
  8 CSS pixels as the tolerance (the page accepts 0 to one day). The moment is the time of the nearest marker within the tolerance (the
  earlier one on a tie), else the time under the pointer, floored to the whole second. It is written to `at`, replacing the current
  entry. Only the chart's own query result is used to find markers, so no further read is made.
- The chart writes no HTML: labels reach the chart library as text.

### Notes

The notes are paragraphs above the chart, in this order, each exactly when the query's result calls for it:

1. `<source> <name> has no values in this range.` for a chosen metric without a line, never for a metric the query cut.
2. `Only 64 lines can be drawn; lines of <metrics> are left out.` naming the metrics the query cut (some of their lines may be drawn).
3. `This range starts before the oldest kept rollups; older values are no longer stored.`
4. `Values after <time> UTC are not complete yet and may still change.` when the lines are complete only up to a time before the end of
   the range.
5. `Events after <time> UTC are not shown.` when the event list was cut.
6. The step text above, at a step.

Times are `yyyy-MM-dd HH:mm:ss` in UTC.

### Events

- Below the chart the table *Events* lists the markers of the range in time order: the time (a button that sets the moment, floored to
  the second), the kind and the text. It is left out when there is none; at most 1,000 events.
- Kinds and texts: `OOM kill` with `<victim command> (PID <pid>)`, `Boot` with the boot ID, `Service restart` with the unit, `Backup run`
  with `<command> until <time> UTC`, `<command>, running` or `<command>, end unknown`.

### Moment panel

- An address with `at` shows the panel below the events (also when the moment lies outside the range, where the chart does not mark it):
  `Moment <time> UTC` (`yyyy-MM-dd HH:mm:ss`), the link *Logs of this moment* and the two snapshots. The link is `logs` with `from` 30
  seconds before the moment and `to` 5 minutes after it, clipped to the storable range. While the snapshots are read the panel says
  `Loading…`.
- **Which snapshot.** Of each kind (process, connection), of any source (the link *History of this moment* of the processes view opens this panel, which can therefore show another source's snapshot than the processes view does), the stored snapshot closest to the moment within **10 minutes**
  either side: the latest at or before it and the earliest after it are compared; the nearer wins, the one at or before on equal
  distance, and among snapshots of one capture time the lower record ID. A change of the moment alone reads only these snapshots; the
  chart is not read again and its running read is not cancelled.
- **Process snapshot.** The panel is the component that the [Processes view](web-ui-processes-view.md) shows too, there for the snapshot of one source. The heading is `Process snapshot of <time> UTC (<source>)`. Two tables follow, each with at most 20 rows:
  *Programs* (program, processes, CPU %, memory), by memory descending and then by program name, and *Processes* (PID, user, command, CPU %,
  memory), by memory descending and then by PID; beyond 20 each ends with `and <n> more programs` or `and <n> more processes`. Memory is the
  PSS when every entry of that list has one, else the RSS, and the column says which (`Memory (PSS)`, `Memory (RSS)`); sizes are in binary
  units with one decimal, CPU with one decimal. When the snapshot's `total` is higher than the processes it holds, `<listed> of <total>
  processes are listed.` follows the tables. A snapshot that is not complete and has no `total` adds `This snapshot does not list every
  process.`; a scheduled snapshot that is partial by design adds no note.
- **Snapshot before this moment.** When the snapshot shown is later than the moment (the extra snapshot after an OOM kill is, and no
  longer lists the victim) and an earlier one lies within the window, the panel offers *Snapshot before this moment (<time> UTC)*. It
  sets the moment to that snapshot's time rounded **up** to the whole second (at most the last whole second of the storable range), so that
  the snapshot lies at or before the new moment, and replaces the history entry.
- **Connection snapshot.** The heading is `Connection snapshot of <time> UTC (<source>)`. The tables are *Connection states* (protocol,
  state, connections, every row, by protocol and state; the wire format names four protocols and twelve states), *Processes with the most connections* (PID, command, connections, at most 20,
  by count descending and then PID) and the remote addresses (address, connections, at most 20, by count descending and then address);
  beyond 20 a table ends with `and <n> more`. The remotes are headed `Remote addresses with the most connections`, and
  `Top remote addresses (the snapshot keeps only the 25 most frequent)` for a counts-only snapshot (`aggregates_only`: every regular agent
  snapshot, [Agent connections](agent-connections.md)). A snapshot with `aggregates_incomplete` adds `These counts may not cover every
  socket.` A table without rows is left out.
- **No snapshot.** Without one of a kind within 10 minutes the panel says `No process snapshot was stored within 10 minutes of this
  moment.` or `No connection snapshot was stored within 10 minutes of this moment.` A snapshot that retention removed between the two
  reads shows the same text. Snapshots are kept 30 days by default (`retention.snapshots`); a record inside the hold of an OOM kill's
  window is kept longer, a connection snapshot that is the newest mark of its agent or carries a listener finding follows
  `retention.events`, and imported records are never removed ([Retention and rollups](retention.md)); the page checks none of this.

### Calls and deadline

- The page runs three kinds of actions, each cancelling the running action of its own kind: the metric list read, a chart read (the time-series query,
  which makes several store reads) and a moment read (per kind two stamp reads and, when one was found, one record read: at most six
  calls). The chart and the moment actions run independently, and both start when an address changes both. Leaving the page cancels all.
- Every store call takes one slot of the views' shared read gate ([Web UI](web-ui.md), *Store reads of the views*). Every action runs under a
  deadline of **10 seconds**, which includes the wait for a slot.
- A chart or list read past the deadline shows `Reading the history took longer than 10 seconds and was stopped. Narrow the range or draw
  fewer metrics.`; a store error, and a read the store refuses, show `The history could not be read. The service log has the details.`
  (both also for the metric list). The list failure has its own text slot beside the chart failure: a chart read does not clear it, only
  the start of a list read does. A moment read shows `Reading the snapshots of this moment took longer than 10 seconds and was
  stopped.` and `The snapshots of this moment could not be read. The service log has the details.` in the panel. A failure is logged as
  `history view: reading failed` (event 75, error level) with the exception only, never an address value or a label. A superseded action
  ends silently.

### External text

Line labels, event texts, programs, users, commands, sources and addresses are external text ([Web UI](web-ui.md), *External text*): in the
chart a label is escaped visibly and capped at 64 characters; in the event list and the panel every text is shown through the web UI's
display, at most 256 characters in at most 16 runs.

## Limits and errors

- **Caps.** 8 metrics per address and 16 for the query; 256 entries in the metric list; a target of at most 1,000 points and at most
  1,999 times (`2 x target - 1`); 64 lines; 1,000 markers and events; 20 rows per list of the panel; the texts above.
- **Memory per circuit** is bounded by these caps; the figures are **derived from the fields of the types, not measured**. The page keeps
  the query result (at most 64 lines of 1,999 points; a point holds a state, four doubles, a time and a count, about 80 bytes: about
  11 MiB) and the view of the chart (per point three nullable doubles and a flag, 49 bytes at a step: about 6 MiB; 17 bytes at raw
  resolution: about 2 MiB), the 1,000 events (about 1.5 MiB of runs) and the lists of the panel (20 rows, a few hundred KiB at most). A
  draw builds one more copy of the columns (about 2 MiB) and the message to the browser while it runs. The decoded snapshot is the
  larger part of a moment read: the stamps are read first and only the chosen record is decoded, and `HistoryMomentReader` builds each
  view inside the per-kind read and drops the record before the next kind, so one decoded snapshot (up to the 16 MiB payload limit as
  bytes plus its parsed document, whose size is not measured) is held at a time; the two views stay until the panel is built. The number of circuits is not bounded ([Web UI](web-ui.md), *Limits and errors*).
- The page makes no call that scans: the stamp and record reads seek the indexes, and the metric list costs one to three index seeks per listed entry ([Storage reading](storage-reading.md), *History reads*).
- A browser script cannot call anything but the two instance methods of the chart's reference (selection and click); both drop a value
  that is not finite, outside the storable range or, for the tolerance, outside 0 to one day, and do nothing after the chart was
  disposed. The framework's limit for a message from the browser, 32 KB, stays.
- A drag or click cannot be tested without a browser; the script is read in review and tried by hand.

## Related decisions

- [0221](../decisions/0221-the-history-view-draws-every-metric-on-its-own-scale-with-two-axes-and-step-extremes-in-the-legend.md) — why every metric has its own scale, only two axes are drawn, and the legend shows the step's extremes.
- [0222](../decisions/0222-the-history-view-reads-the-moments-snapshots-and-the-metric-list-through-its-own-bounded-reads.md) — why the moment's snapshots and the metric list have their own bounded reads.

## Not here

- The query, its resolutions, caps and markers: [Time-series queries](time-series-query.md); the store reads: [Storage reading](storage-reading.md).
- The chart in general, the gate, external text and circuits: [Web UI](web-ui.md); the login: [Web login](web-login.md).
- The logs the link opens: [Logs view](web-ui-logs-view.md); how snapshots are recorded: [Agent system metrics](agent-system-metrics.md),
  [Agent connections](agent-connections.md); how long they are kept: [Retention and rollups](retention.md).
- The programs over time and the comparison of two moments: [Processes view](web-ui-processes-view.md).
- Self-healing markers, the list of gaps with their causes, the network view, a minimum-to-maximum band, drag selection
  by touch and automatic refresh: not built.

## Implementation

`Vandox.Backend`: `Components/Pages/HistoryPage` (the address, the controls and the reads) with `HistorySnapshotTable`;
`Components/Pages/ProcessSnapshotPanel` (the process part of the moment panel, shared with the processes view; its callback is `BeforeChosen`), `HistoryRangeControls` (the presets and the range fields, shared with the processes view) and `PageLocation`;
`Components/Charts/HistoryChart` (draws, reports the width; its `Label` is the accessible name of the host, `History chart` by default), `HistoryChartInterop` (the calls into `js/charts.js`: `width`,
`createHistory`, and the `mark` of the chart it returns) and `HistoryChartEvents` (the two `[JSInvokable]` instance methods `Select` and
`Pick`); `History/` (`HistoryAddress` with `Parse`, `ToQuery` and `SameChart`, `HistoryView` with the texts and helpers,
`HistoryChartView`, `HistoryMomentReader` (with `ClosestAsync`, the read of one kind, of one source or of any), `AddressTimes` (the range and the moment of the address, shared with the processes view), `ProcessSnapshotView`, `ConnectionSnapshotView`, `GatedHistoryReader`); `PageRunner` of the
logs view runs the actions. The reads are `IHistoryReader` of `Vandox.Storage`.
