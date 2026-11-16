# Spec: History view

Source: Issue #49
Status: Draft

## Problem

The operator reconstructing an outage has no page that draws chosen metrics over a chosen period side by side or leads from a moment
in a chart to what the server was doing then; the time-series query of #47
([Time-series queries](../../docs/areas/time-series-query.md)) can answer it, but no view calls it.

## Scenarios

- S1: Given a signed-in user, when they choose *History* in the navigation, then `/history` shows the last 24 hours, the presets *1 h*,
  *24 h*, *7 d*, *30 d* and *1 y*, the *From* and *To* fields (UTC), the metric list and `Choose one or more metrics to draw.` With no
  metric chosen nothing else is read: no chart, no markers, no event list.
- S1a: The metric list holds every source and name with a stored raw value or a kept hourly rollup, by source, then name (ordinal), at
  most 256; when more exist it ends with `The list shows the first 256 metrics; others can be named in the address.` A metric of the
  address that is not in the list stays chosen and drawn.
- S2: Given the page, when the user adds metrics (for example `system` `mem_available_bytes` and `system` `cpu_percent`), then one chart
  draws them over the range. Every metric has its own vertical scale; the first metric's axis is drawn on the left, the second's on the
  right (below 480 CSS pixels of chart width only the left one), each labelled `<source> <name>`; further metrics have no drawn axis.
  Every stored label set is a line (`cpu_percent` per `mode`), named `<source> <name> <labels>` in the legend with its value under the
  pointer; lines take the six chart colors in order, repeated dashed from the seventh. Events are drawn as markers. Adding or removing a
  metric writes the address as a new history entry.
- S2a: At a step (any resolution but raw) each point is the step's average; the legend shows the step's minimum, average and maximum
  under the pointer, and the chart says `Each point is the average of <step>; the legend shows the minimum and maximum.` The query's
  target number of points is the chart width in CSS pixels, at most 1,000.
- S3: A preset gives that period up to now and stays in the address, relative to the time it is opened.
- S4: Applied *From* and *To* give that period, fixed in the address.
- S5: When the user drags across the chart with a mouse or pen by at least 5 CSS pixels, the view shows the selected period, widened to
  whole seconds, as a fixed range; back returns to the previous range.
- S6: When the user clicks in the chart (a press moved by less than 5 CSS pixels), the **moment** is the time under the pointer, or the
  time of the nearest marker within 8 CSS pixels (on a tie the earlier), floored to the whole second. The chart marks it, the address
  holds it (replacing the current history entry), and a panel below the chart shows the moment (`yyyy-MM-dd HH:mm:ss` UTC), the link
  *Logs of this moment* and the snapshots of S7 and S8. A change of the moment alone reads only the snapshots: the chart is not read
  again and its running read is not cancelled.
- S7: The process snapshot of a moment is the stored one closest to it within 10 minutes either side, of any source; on a tie the
  earlier, then the lower record ID. The panel shows `Process snapshot of <time> UTC (<source>)`, the 20 programs with the most memory
  (program, processes, CPU %, memory) with `and <n> more programs` beyond 20, and the 20 listed processes with the most memory (PID,
  user, command, CPU %, memory) with `<listed> of <total> processes are listed.` when the snapshot's `total` is higher. Memory is PSS
  where the snapshot has it, else RSS, and the column says which. When the snapshot shown is later than the moment (the extra snapshot
  after an OOM kill is, and no longer lists the victim), the panel offers *Snapshot before this moment* with the time of the latest one
  within 10 minutes before, which sets the moment to that time.
- S8: The connection snapshot is chosen by the same rule: `Connection snapshot of <time> UTC (<source>)`, the count of every protocol
  and state, the 20 processes and the 20 remote addresses with the most connections, each with `and <n> more` beyond 20. For a
  counts-only snapshot (`aggregates_only`: every regular agent snapshot) the remotes are headed `Top remote addresses (the snapshot
  keeps only the 25 most frequent)`.
- S9: Below the chart the events of the range are listed in time order: time, kind (`OOM kill`, `Boot`, `Service restart`, `Backup run`)
  and text (victim command and PID, unit, backup command with its end, `running` or `end unknown`); a time sets that moment.
- S10: A copied address shows another signed-in user the same metrics, range (a preset up to their now) and moment.

## Errors and edge cases

- E1: **Address**, read as the logs view reads its own ([Logs view](../../docs/areas/web-ui-logs-view.md), *Address*: keys without
  regard to ASCII case, percent-encoding, other keys ignored, nothing after `#`): `metric` (repeatable, `<source>,<name>`, each part
  1 to 128 bytes of the wire format's name pattern, at most 8 distinct, a repeat used once), `range` (`1h`, `24h`, `7d`, `30d`, `1y` of
  365 days), `from` and `to` (the logs view's formats and range, UTC) and `at` (the same formats, floored to the second). Without
  `range`, `from` and `to` the range is `24h`. Errors, naming the parameter, never the value: `The address gives the parameter <name>
  more than once.` (not for `metric`), `The address has an invalid value for the parameter <name>.`, `The address has a start time that
  is not before its end time.`, `The address names more than 8 metrics.`, `The address gives both a preset range and a start or end
  time.` and `The address needs both a start and an end time.` An address with an error shows the text and the controls and calls no
  store method.
- E2: A ninth metric cannot be added: `At most 8 metrics can be drawn at once.`
- E3: A chosen metric without a value in the range: `<source> <name> has no values in this range.`, never for a metric the query cut.
- E4: More than 64 lines: the first 64 are drawn and `Only 64 lines can be drawn; lines of <metrics> are left out.` names the metrics
  the query cut (some of their lines may be drawn).
- E5: A range that starts before the kept hourly rollups: `This range starts before the oldest kept rollups; older values are no longer
  stored.` A range that begins after now draws nothing: `This range lies in the future.`
- E6: The query's completeness time before the end of the range: `Values after <time> UTC are not complete yet and may still change.`
- E7: An event list the query cut: `Events after <time> UTC are not shown.`
- E8: No snapshot within 10 minutes: `No process snapshot was stored within 10 minutes of this moment.` (`No connection snapshot …`).
  Snapshots are kept 30 days by default, longer around an OOM kill, for listener findings and when imported
  ([Retention and rollups](../../docs/areas/retention.md)).
- E9: A process snapshot with `complete` false and no `total`: `This snapshot does not list every process.`; a connection snapshot with
  `aggregates_incomplete`: `These counts may not cover every socket.` No other note for a scheduled snapshot partial by design.
- E10: A moment outside the range keeps its panel, unmarked in the chart.
- E11: Line labels, marker texts, programs, users, commands and addresses are external text, shown only through the web UI's display
  ([Web UI](../../docs/areas/web-ui.md), *External text*): in the chart escaped and capped at 64 characters, in the event list and the
  panel at 256 characters in at most 16 runs.
- E12: A gap breaks a line, never drawn across ([Web UI](../../docs/areas/web-ui.md), *Charts*); between samples it is drawn through.
- E13: Prerendered: the frame and `Loading…`, no read.
- E14: Past 10 seconds: `Reading the history took longer than 10 seconds and was stopped. Narrow the range or draw fewer metrics.`
  (panel: `Reading the snapshots of this moment took longer than 10 seconds and was stopped.`); a store error: `The history could not be
  read. The service log has the details.` (panel: `The snapshots of this moment could not be read. The service log has the details.`),
  logged as `history view: reading failed` without any address value.
- E15: A range or metric change cancels the running chart read, a new moment the running snapshot read; leaving the page cancels both.

## Non-functional requirements

- N1: Every store call (metric list, query, snapshot reads) takes the views' shared read gate ([Web UI](../../docs/areas/web-ui.md),
  *Store reads of the views*) and runs under a 10-second deadline per action.
- N2: A page's memory is bounded by caps, its figure per circuit stated in the area document: 256 metrics, the query's caps at a target
  of at most 1,000 (64 lines, 1,999 times, 1,000 markers), the texts of E11, one decoded snapshot at a time and the panel's lists of 20.
- N3: The page is behind the login; validated address values reach only the query, bound store parameters and fixed-format parsing;
  every link is `history` or `logs` plus a validated query; the security headers stay as they are.
- N4: The page works down to 360 CSS pixels without horizontal scrolling of the page.

## Acceptance criteria

- AC1: The address is read and written as E1 states (parameters, default, error texts, `at` floored before any read); an address with
  an error calls no store method; a written address reads back to the same metrics, range and moment (S10, E1, E2).
- AC2: Presets, *From* and *To* and a drag selection set the range as S3 to S5 state, each as a new history entry; a press moved by less
  than 5 CSS pixels is a click (S3, S4, S5).
- AC3: The chart draws up to 8 metrics with scales, axes, legend names, colors and dashes as S2 states, labels escaped as E11; the
  query's target is as S2a states; a removed metric leaves chart and address (S2, E11, E12).
- AC4: The metric list holds what S1a states with its cap and text, and a metric of the address outside it stays drawn; with no metric
  chosen no query runs (S1, S1a).
- AC5: At a step the chart shows the S2a text and the legend the step's minimum, average and maximum; at raw resolution neither (S2a).
- AC6: The markers are drawn (kills, boots, restarts as lines, backup runs as spans) and listed as S9 states, with the caps of E11 and
  the E7 text when cut (S9, E7).
- AC7: The texts of E3 to E6 appear exactly when the query's result calls for them (E3, E4, E5, E6).
- AC8: A click sets the moment as S6 states and reads only the snapshots; the chart marks it inside the range; *Logs of this moment* is
  `logs` with `from` the moment minus 30 seconds and `to` plus 5 minutes, clipped to the storable range (S6, E10).
- AC9: The panel shows the process snapshot, its lists and counts and *Snapshot before this moment* as S7 states, and the texts of E8
  and E9 (S7, E8, E9).
- AC10: The panel shows the connection snapshot as S8 states, with the texts of E8 and E9 (S8, E8, E9).
- AC11: Prerendered the page reads nothing; every store call takes one gate slot; reads are cancelled as E15 states; the texts and log
  line of E14 appear; the caps of N2 hold; the navigation has *History* (`history`), marked on `/history`, behind the login, with links
  only from `history` and `logs` (E13, E14, E15, N1, N2, N3, N4).

## Out of scope

- Self-healing markers: no such data exists before #71 (v0.6.0), whose criterion adds them to this view.
- The gap list with causes: #54; here lines only break at gaps (E12).
- The processes (#50) and network (#51) views.
- A shaded minimum-to-maximum band per line: up to 64 overlaid bands hide the lines; S2a carries the extremes.
- Drag selection by touch, automatic refresh, links into the view from other views.

## Open questions

- Q1: Where do "processes and connections of that moment" open while #50 and #51 do not exist? Answered: (Lead) in this view's panel.
- Q3: Does a shared preset stay relative? Answered: (Lead) yes (S3); a zoom or *From*/*To* is fixed.
- Q4: How many overlaid metrics? Answered: (Lead) 8 (S2); the query allows 16.

## Area documents

- `web-ui-history-view.md` (new, *History view*, with its index row): everything above, with the memory per circuit.
- `web-ui.md`: the navigation entry, the gate's callers, what charts gain (scales, axes, selection, click, markers, legend extremes).
- `time-series-query.md`: the view as caller; `storage-reading.md`: the metric list and the closest-snapshot reads;
  `docs/ARCHITECTURE.md`: the view among the existing ones.

## Challenge

- M1 (metric list without read or cap) — accepted: S1a states what the list holds (raw values and kept hourly rollups), its order, the
  cap of 256 with its text, and that an address metric outside it stays drawn; N1 puts the read under the gate and deadline, N2 caps it,
  AC4 tests it. How the read stays bounded is the plan's.
- M2 (eight axes cannot fit 360 px) — accepted: S2 draws two axes (one below 480 CSS pixels), the others are scale-only with legend
  values; the legend names every line `<source> <name> <labels>`; colors repeat dashed after six; AC3 and AC11 test it. One axis per
  unit was rejected: the query returns no unit, and two byte metrics of different size would share a scale.
- M3 (averages hide spikes) — accepted as the text plus legend extremes (S2a, AC5); the band was rejected (Out of scope).
- 1 (partial snapshots on every panel) — accepted: counts from `total` (S7), remotes heading for counts-only snapshots (S8), notes only
  where E9 states.
- 2 (floor `at`) — accepted: E1, S6, AC1 (reload shows the same snapshots).
- 3 (moment change, history entry, drag versus click) — accepted: S6 (reads only the snapshots, replaces the entry), 5 CSS pixels in
  S5, S6 and AC2.
- 4 (query target) — accepted: chart width, at most 1,000 (S2a, N2).
- 5 (retention exceptions) — accepted: E8.
- 6 (cut series and E3) — accepted: E3 and E4 wording.
- 7 (event text cap) — accepted: E11, 256 characters in 16 runs; N2 asks for the per-circuit figure in the area document.
- 8 (no metric, no query) — accepted: S1, AC4.
- 9 (marker ties, snapshot after a kill) — accepted: nearest then earlier marker (S6), record ID on a snapshot tie and *Snapshot before
  this moment* (S7).
