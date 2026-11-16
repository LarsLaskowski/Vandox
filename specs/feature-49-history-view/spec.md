# Spec: History view

Source: Issue #49
Status: Draft

## Problem

The operator reconstructs outages and slow degradations on the monitored server. Today the web UI shows only the latest values
([Overview page](../../docs/areas/web-ui-overview.md)) and the charts of an incident's window
([Incidents view](../../docs/areas/web-ui-incidents-view.md)); no page draws a chosen metric over a chosen period, puts two metrics side
by side, or leads from a moment in a chart to what the server was doing then. The time-series query of #47
([Time-series queries](../../docs/areas/time-series-query.md)) answers such questions in process, but no view calls it.

## Scenarios

- S1: Given a signed-in user, when they choose *History* in the navigation, then `/history` shows the last 24 hours, the preset buttons
  *1 h*, *24 h*, *7 d*, *30 d* and *1 y*, the *From* and *To* fields (UTC), the list of stored metrics to choose from, and
  `Choose one or more metrics to draw.`
- S2: Given the page, when the user adds metrics from the list (each a source and a name, for example `system` `mem_available_bytes`
  and `system` `cpu_percent`), then one chart draws them over the range: every metric has its own vertical scale and axis labelled
  with its source and name, every stored label set of a metric is a line of its own (`cpu_percent` with each `mode`), and the events
  of the range are drawn as markers. Each change writes the address (a new browser history entry).
- S3: Given a chart, when the user presses a preset, then the range is that period up to now; the address keeps the preset, so the
  same address opened a week later shows the period up to that later time.
- S4: Given a chart, when the user enters *From* and *To* and applies them, then the range is exactly that period, fixed in the address.
- S5: Given a chart, when the user drags across it with a mouse or pen, then the view shows the selected period, widened to whole
  seconds, as a fixed range; the browser's back button returns to the previous range.
- S6: Given a chart, when the user clicks in it, then the **moment** is the time under the pointer, or the time of a marker when the
  click lies within 8 CSS pixels of it; the chart marks the moment, the address holds it, and a panel below the chart shows the moment
  (`yyyy-MM-dd HH:mm:ss` UTC), the link *Logs of this moment*, the process snapshot and the connection snapshot of that moment (S7, S8).
- S7: The process snapshot of a moment is the stored one closest to it within 10 minutes before or after, of any source, earlier on a
  tie. The panel shows `Process snapshot of <time> UTC (<source>)`, the 20 programs with the most memory (program, processes, CPU %,
  memory) and the 20 processes with the most memory (PID, user, command, CPU %, memory), each list followed by `and <n> more` when
  the snapshot holds more. Memory is PSS where the snapshot has it, else RSS, and the column says which.
- S8: The connection snapshot of a moment is chosen by the same rule. The panel shows `Connection snapshot of <time> UTC (<source>)`,
  the count of every protocol and state, the 20 processes with the most connections and the 20 remote addresses with the most
  connections, with `and <n> more` as in S7.
- S9: Below the chart the view lists the events of the range in time order: time, kind (`OOM kill`, `Boot`, `Service restart`,
  `Backup run`) and text (the victim's command and PID, the unit, the backup command with its end, `running` or `end unknown`); a
  time in the list selects that moment as a click on the marker does.
- S10: Given an address copied from the view, when another signed-in user opens it, then they see the same metrics in the same order,
  the same range (fixed, or the preset up to their now) and the same moment with its panel.
- S11: Given a chart, when the user removes a metric, then the chart is drawn without it and the address loses it.

## Errors and edge cases

- E1: **Address.** The parameters, read as the logs view reads its address ([Logs view](../../docs/areas/web-ui-logs-view.md),
  *Address*: keys without regard to ASCII case, percent-encoding, other keys ignored, nothing after `#` read): `metric` (repeatable,
  `<source>,<name>`, each part 1 to 128 bytes of the wire format's name pattern, at most 8 distinct, a repeat used once), `range` (`1h`,
  `24h`, `7d`, `30d`, `1y`; `1y` is 365 days), `from` and `to` (the logs view's time formats and range, UTC, `from` before `to`) and
  `at` (the same time formats). Without `range`, `from` and `to` the range is `24h`. Errors, each naming the parameter and never the
  value: `The address gives the parameter <name> more than once.` (not for `metric`), `The address has an invalid value for the
  parameter <name>.`, `The address has a start time that is not before its end time.`, `The address names more than 8 metrics.`,
  `The address gives both a preset range and a start or end time.` and `The address needs both a start and an end time.` An address
  with an error shows the text and the controls, and calls no store method.
- E2: A ninth metric cannot be added; the list says `At most 8 metrics can be drawn at once.`
- E3: A chosen metric without a value in the range draws no line, and the view says `<source> <name> has no values in this range.`
- E4: More than 64 lines: the first 64 are drawn and the view says `Only 64 lines can be drawn; left out: <metrics>.`
- E5: A range that starts before the kept hourly rollups: `This range starts before the oldest kept rollups; older values are no longer
  stored.` A range that begins after now draws nothing and says `This range lies in the future.`
- E6: Points not yet complete (the query's completeness time before the end of the range): `Values after <time> UTC are not complete
  yet and may still change.`
- E7: More events than the query returns (its cap, or an event read that ran out of pages): the view says `Events after <time> UTC are
  not shown.`
- E8: No snapshot within 10 minutes of the moment: `No process snapshot was stored within 10 minutes of this moment.` (connections:
  `No connection snapshot …`). Snapshots are kept for `retention.snapshots` (30 days by default,
  [Retention and rollups](../../docs/areas/retention.md)), so an older moment usually shows this text.
- E9: A process snapshot whose `complete` is false says `This snapshot does not list every process.`; a connection snapshot with
  counts only (`aggregates_only`) or incomplete counts says `This snapshot holds counts only.` or `These counts may not cover every
  socket.` ([Wire format records](../../docs/areas/wire-format-records.md)).
- E10: A moment outside the range keeps its panel; the chart marks it only inside the range. A new click replaces the moment.
- E11: Line labels (label sets), marker texts, program and process names, users, commands and addresses are external text: shown only
  through the web UI's display of external text ([Web UI](../../docs/areas/web-ui.md), *External text*); a line label in the chart is
  escaped first and capped at 64 characters, as the incidents view's program labels.
- E12: A gap of a line breaks it, never drawn across ([Web UI](../../docs/areas/web-ui.md), *Charts*); between samples the line is
  drawn through.
- E13: Prerendered, the page shows its frame and `Loading…` and reads nothing.
- E14: A read that runs past 10 seconds is stopped with `Reading the history took longer than 10 seconds and was stopped. Narrow the
  range or draw fewer metrics.` (the panel: `Reading the snapshots of this moment took longer than 10 seconds and was stopped.`); a
  store error shows `The history could not be read. The service log has the details.` (the panel: `The snapshots of this moment could
  not be read. The service log has the details.`) and is logged as `history view: reading failed`, without any value of the address.
- E15: A new action (an address change, a click) cancels the running read; leaving the page cancels it.

## Non-functional requirements

- N1: Every store call of the view takes the views' shared read gate ([Web UI](../../docs/areas/web-ui.md), *Store reads of the
  views*) and runs under the 10-second deadline of an action.
- N2: The memory a page holds is bounded by caps the History view's area document states per circuit: the query's caps (64 lines,
  `2 x target - 1` times, 1,000 markers), at most one decoded snapshot at a time, and the capped lists of the panel.
- N3: The page is behind the login like every page; validated address values reach only the time-series query, bound store
  parameters and fixed-format parsing; every link is the fixed path `history` or `logs` plus the query of validated values; the
  security headers and the self-only policy stay unchanged (no inline script or style, no other origin).
- N4: The page works from desktop width down to 360 CSS pixels without horizontal scrolling of the page.

## Acceptance criteria

- AC1: The address of E1 is read and written as stated: every parameter and default, each error text, at most 8 metrics, an address
  with an error calls no store method, and the address the view writes reads back to the same metrics, range and moment (S10, E1, E2).
- AC2: A preset gives the period up to now and keeps the preset in the address; *From* and *To* and a drag selection (widened to whole
  seconds) give a fixed range; each writes a new history entry (S3, S4, S5).
- AC3: One chart draws up to 8 metrics, each with its own scale and an axis labelled `<source> <name>`, one line per label set with an
  escaped label of at most 64 characters; the metric list offers the stored metrics, and a removed metric leaves chart and address
  (S1, S2, S11, E11, E12).
- AC4: The markers of the range are drawn (kills, boots and restarts as lines, backup runs as spans) and listed below the chart with
  time, kind and text as S9 states; a cut list shows the E7 text (S9, E7).
- AC5: The texts of E3, E4, E5 and E6 appear exactly when the query's result calls for them (E3, E4, E5, E6).
- AC6: A click sets the moment to the time under the pointer, or to a marker's time within 8 CSS pixels; the moment is in the address,
  marked in the chart while inside the range, and its link *Logs of this moment* is `logs` with `from` the moment floored to the
  second minus 30 seconds and `to` that second plus 5 minutes, clipped to the storable range (S6, E10).
- AC7: The panel shows the process snapshot closest to the moment within 10 minutes, its two lists of 20 with `and <n> more`, the PSS
  or RSS column, and the texts of E8 and E9 (S7, E8, E9).
- AC8: The panel shows the connection snapshot chosen the same way with its states, top processes and top remote addresses, and the
  texts of E8 and E9 (S8, E8, E9).
- AC9: Prerendered the page reads nothing; every store call takes one gate slot; a new action cancels the running read; the deadline and
  error texts of E14 appear and a failure is logged without address values; a page holds no more than the caps N2 names
  (E13, E14, E15, N1, N2).
- AC10: The navigation has the entry *History* (`history`), marked on `/history`; the page needs a session and builds links only from
  `history` and `logs` (S1, N3, N4).

## Out of scope

- Self-healing markers: the agent records no self-healing actions before #71 (v0.6.0), whose criterion adds them to this view.
- The gap list with causes and its marking in the chart: #54; here lines only break at gaps (E12).
- The processes view (#50) and the network view (#51); the panel shows the moment's snapshots itself, and those views may link to it.
- Drawing the minimum and maximum of a step; a step's line is its average, and zooming in shows finer steps or raw values.
- A drag selection by touch gesture (N4 phones use the presets and the *From* and *To* fields), an automatic refresh, and links into
  the view from the other views.

## Open questions

- Q1: Where do "processes and connections of that moment" open, when the processes and network views do not exist yet?
  Answered: (Lead) in a panel of this view (S6 to S8); the address holds the moment, so the panel is shareable.
- Q2: Self-healing markers? Answered: no such data exists before #71 (Out of scope).
- Q3: Does a shared preset stay relative? Answered: (Lead) yes (S3); a zoom or *From*/*To* is fixed.
- Q4: How many overlaid metrics? Answered: (Lead) 8, since each needs its own axis on one chart; the query allows 16.
- Q5: Does the issue's .NET 10 (record 0073) still hold? Answered: no, record 0197 moved the backend to .NET 11; no effect here.

## Area documents

- `docs/areas/web-ui-history-view.md` (new, *History view*): address, controls, chart, markers and event list, moment panel, texts,
  calls and deadline, memory per circuit; its row in `docs/areas/README.md`.
- `docs/areas/web-ui.md`: the fifth navigation entry, the history view among the views and the gate's callers, and what charts gain
  (several scales and axes, selection, click, markers).
- `docs/areas/time-series-query.md`: the history view as its caller instead of "planned".
- `docs/areas/storage-reading.md`: any new read the view needs (the list of stored metrics, the snapshot closest to a moment).
- `docs/ARCHITECTURE.md`: the history view among the existing views.
