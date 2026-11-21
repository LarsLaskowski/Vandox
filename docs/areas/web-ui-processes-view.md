# Processes view

## Scope

The page `/processes` of the [web UI](web-ui.md): the address that holds the source, the range, the moment and a second point, the
controls, the two stacked charts *Memory per program* and *CPU per program* with their step and notes, and the panels *Top processes*
and *Comparison* for the process snapshot of one source at a moment and for two moments. It covers the texts, the calls and the
deadline, and the caps with the memory per circuit. The administrator relies on it to see which programs use the memory and the CPU over
any period and what changed between two moments. The charts are drawn from the per-program rollups ([Program rollups](program-rollups.md)),
so they reach back as far as those are kept; the panels need the snapshots themselves ([Retention and rollups](retention.md)). The
reads are described in [Program rollups](program-rollups.md), *Reading*, and [Storage reading](storage-reading.md), *History reads*.

## Behavior

### Page

- The page is behind the login ([Web login](web-login.md)) and is served on the web listener only. The navigation entry *Processes*
  (`processes`) lies between *History* and *Logs* and is marked on `/processes` with or without a query. The heading is `Processes`.
- Prerendered (the first response) the page shows the heading, the range controls and `Loading…` and reads nothing; it reads once its
  live connection is up ([Web UI](web-ui.md), *Pages and render mode*). An address with an error is shown the same way.
- Top to bottom: the presets, *From (UTC)*, *To (UTC)* and *Apply*; the choice *Source*; the text of a control; the notes; *Memory per
  program* with its chart; *CPU per program* with its chart; then the panel of the moment or of the comparison.
- A link of the view is `processes` plus the query of a validated address; the link *History of this moment* is `history` plus the
  moment alone. The page builds no other link.

### Address

Everything the page shows is in the address, so it can be bookmarked, shared and used with the browser's back and forward buttons.

| Parameter | Value |
| --------- | ----- |
| `source` | a name of the wire format's name pattern, 1 to 128 bytes (starts with an ASCII letter or digit, then letters, digits and `. _ : / @ + -`; a trailing line feed is refused); an empty value is skipped |
| `range`, `from`, `to`, `at` | as in the [History view](web-ui-history-view.md), *Address*: `1h`, `24h`, `7d`, `30d` or `1y`; times as `yyyy-MM-ddTHH:mm[:ss]` in UTC; `at` is the moment |
| `compare` | the second point of a comparison, in the format of `at`; an empty value is skipped |

- Keys are matched, percent-encoded and ignored as in the history view; a raw `+` is read as a space, so a source with a plus is written
  as `%2B`. There is no `metric`.
- Without `source` the charts show the default source (below) and the address is not extended. Without `range`, `from` and `to` the range
  is the last 24 hours, as in the history view; a preset stays relative.
- The parameters are read in the order `source`, `range`, `from`, `to`, `at`, `compare`, and the first error wins; then come the range
  rules of the history view and `The address needs both a moment and a second point for a comparison.` for a `compare` without `at`. The
  errors are the history view's (`The address gives the parameter <name> more than once.`, `The address has an invalid value for the
  parameter <name>.`, and the three about the range), naming the parameter, never its value. An address with an error shows the heading,
  the controls and the text, and nothing else, and reads nothing.
- An address is written only from values checked again (the source against the name pattern, the times against the storable range, the
  rule about `compare`), the parameters in the order `source`, `range` (only for a preset), `from` and `to` (only as a pair), `at`,
  `compare`, each percent-encoded. A value the checks refuse is shown as the text of the refusal without navigating.
- A range that begins at or after now shows `This range lies in the future.` and reads nothing; no panel is shown.
- The page follows every change of its address. A change of the source, the preset, `from` or `to` reads the charts again; a change of
  the shown source, of `at` or of `compare` reads the panel again (*Panel source*).

### Controls

- Presets *1 h*, *24 h*, *7 d*, *30 d*, *1 y*; the current one is marked. *From (UTC)* and *To (UTC)* with *Apply*, checked with the
  history view's rules; their texts show with the working page.
- *Source* is a choice of the sources that have a bucket in the range, in the order of the store; a source of the address that is not
  among them is added as chosen. A handler takes only a listed source; any other value is ignored, and so is the choice of the address's
  own source. A choice writes `source` and drops `at` and `compare`, which belong to the former source.
- A preset, *Apply*, a drag selection and a source each write a **new history entry**; a drag selection and a preset keep `source`, `at`
  and `compare`. A click in a chart, and *Snapshot before this moment* **replace** the current entry.
- A click sets `at` and drops `compare`; the time is the one under the pointer, floored to the whole second (the charts report no
  markers). *Compare from here* (shown with a moment and a known source) arms the page: it says `Click a second point in a chart to
  compare.` with a button *Cancel*, and the next click writes `compare`, keeps `at` and leaves the mode. The mode is not in the address;
  an address without `at`, or one with `compare`, ends it.

### Source

- The list holds the sources that have a bucket in the range, by source in the byte order of SQLite, at most 256. When more exist the page
  says `The list shows the first 256 sources.`
- Without `source` the default is the listed source with the latest bucket in the range, the first listed on a tie. The page shows one
  source at a time.

### Charts

- Both charts are the history chart in its stacked mode ([Web UI](web-ui.md), *Charts*): one scale from 0, the lines as filled bands
  stacked in line order. *Memory per program* stacks the 10 programs with the highest average memory over the range, the highest at the
  bottom, and `Other programs` on top, so the top edge is the source's average total; *CPU per program* stacks its own 10 programs by
  average CPU % (100 per fully used core) and `Other programs`. `Other programs` holds every other program, those the fold cut or
  capped included. A program that a snapshot does not name counts 0 in it.
- The colors are the six chart colors in order; from the seventh line they repeat dashed. A label is escaped visibly and capped at 64
  characters.
- Memory is in MiB. The axis reads `Memory (PSS), MiB` when every folded snapshot of the range is a PSS snapshot, else `Memory (RSS), MiB`
  for the whole range, with the note below the heading *Memory per program*: `RSS counts shared memory in every program that maps it, so
  the stack can exceed the server's memory.` The CPU axis reads `CPU %`.
- **Step.** Each point is the average of a step: the smallest step of the time-series query from 5 minutes up for which at most the
  chart's width in CSS pixels, at most 1,000, steps overlap the range; steps below one hour only while the range starts within
  `retention.rollups_5m`, then whole days ([Time-series queries](time-series-query.md), *Resolution*). The width is reported when the
  charts are first shown; a later resize redraws the same points and reads nothing.
- **Legend.** For the program under the pointer the legend shows `<minimum> / <average> / <maximum>` of its step (the minimum is 0 in a
  step in which a snapshot does not name the program) and for `Other programs` its average; `--` at a gap.
- **Gap.** A step without a folded snapshot of the source (the agent offline, the collector failing) is a gap, and every band of both
  charts breaks there and is never drawn across; a recorded gap shorter than a step whose snapshots exist breaks nothing.
- **Selection and click** are reported as in the history chart: a drag of 5 CSS pixels or more selects a period, which becomes the fixed
  range; a click picks a moment. There is no selection by touch. The moment is a line in the brand color in both charts when it lies in
  the range. The incidents chart is not stacked this way and keeps refusing a gap.
- The accessible names of the chart hosts are `Memory per program` and `CPU per program`.

### Notes

The notes are paragraphs above the charts, each exactly when the memory read calls for it, in this order:

- E1: `No process data is stored in this range.` when the source has no bucket in the range, or no source has one; the charts and the
  other notes are left out then.
- `The list shows the first 256 sources.`
- E3: `This range starts before the oldest stored process data of this source.`
- E4: `Some snapshots in this range do not list every program.` when a folded step holds a snapshot that does not list every program
  (`ProcessSnapshot.ListsEveryProgram`: no program, or the programs' counts differ from `total`, or without `total` the snapshot is not
  `complete`; [Program rollups](program-rollups.md), *The buckets*).
- E5: `Values after <time> UTC are not complete yet and may still change.` when snapshots of the source from before the end of the range
  are not folded yet (the fold runs every 5 minutes); times are `yyyy-MM-dd HH:mm:ss`.
- The step text: `Each point is the average of <step>; the legend shows the minimum and maximum.`

A program outside a snapshot's 50 by memory and 50 by CPU, or beyond a bucket's 500 names, is in `Other programs` there and reads lower
in that step without a note: the rollups keep no flag for it.

### Panels

- An address with `at` shows a panel below the charts (also for a moment outside the range) under the heading *Top processes*, or
  *Comparison* when `compare` is there.
- **Top processes.** `Moment <time> UTC`, the link *History of this moment*, *Compare from here* and the process snapshot of the shown
  source closest to the moment within **10 minutes**, with the history view's choice of snapshot (the nearer side, the earlier on equal
  distance, the lower record ID among one capture time), its heading, tables *Programs* and *Processes*, its notes and *Snapshot before this
  moment* ([History view](web-ui-history-view.md), *Moment panel*, the same component), but of **this source only**. The link opens the
  history view, whose panel shows the closest snapshot of **any** source, which can be another source's. Without a snapshot the panel
  says `No process snapshot was stored within 10 minutes of this moment.`
- **Comparison.** `<earlier> UTC to <later> UTC (<source>)` over a table with one row per program the two snapshots name: Program, Memory at each point,
  Change, CPU % at each point and State. The earlier of the two points is the base, whichever of `at` and `compare` it is; each point has
  the source's closest snapshot within 10 minutes. Memory is PSS when every program of both snapshots has it, else RSS for both, and the
  column says which (`Memory (PSS) earlier`, `Memory (RSS) later`). Change is signed in binary units; an absent side shows `--`. The
  State is `new` (only at the later point), `gone` (only at the earlier point) or empty. Rows are ordered by memory change descending, then
  by program name (ordinal); at most 20 rows follow, then `and <n> more programs (<k> new, <g> gone).` with the counts of the rows left out.
- **E6.** No snapshot within 10 minutes of a point: `No process snapshot was stored within 10 minutes of the first point.` or `... of
  the second point.` (`at` is the first point, `compare` the second); both points closest to one snapshot: `Both points show the same
  snapshot; choose points further apart.`
- **E11.** When either snapshot does not list every program (the rule of E4, as with a legacy import's snapshot cut short), no row is
  marked `new` or `gone`, the left-out line reads `and <n> more programs.` and the panel says `One of the two snapshots does not list
  every program, so no program is marked new or gone.` A program the two snapshots name differently (the two legacy import formats)
  shows as two programs.
- Names of programs are external text ([Web UI](web-ui.md), *External text*): in the table at most 256 characters in at most 16 runs,
  in a chart label escaped and capped at 64.

### Panel source

The panel shows the snapshots of the source the charts show: the address's `source`, else the default of the charts. The panel is read
for the combination of that source, `at` and `compare` and again when any of them changes; a newer read supersedes the running one.

- With `source` in the address the read starts at once. Without it the read starts after a chart result, and again when a change of the
  range moves the default source.
- Without `source` and before the first chart result the panel says `Loading…`. When no source has process data in the range it says
  `No source has process data in this range, so no snapshot is shown.`, and when the chart action failed (the next text of E9) `The source
  could not be read, so no snapshot is shown.`; in both the panel's read is cancelled. The chart failure text shows above the charts.
- A future range or an address error shows no panel and reads nothing.

### Calls and deadline

- The page runs two kinds of actions, each cancelling the running action of its own kind: a chart action and a panel action. A chart
  action is at most three gated calls (the source list, the memory read and the CPU read; the CPU read only when the memory read has
  steps). A panel action is one moment (two stamp reads and one record read: three calls) or one comparison (at most six calls); the
  earlier point is reduced to its figures before the later one is read. Leaving the page cancels all.
- Every store call takes one slot of the views' shared read gate ([Web UI](web-ui.md), *Store reads of the views*); the rollup reads go
  through a gated reader on the same gate. Every action runs under a deadline of **10 seconds**, which includes the wait for a slot.
- A read past the deadline shows `Reading the processes took longer than 10 seconds and was stopped. Narrow the range.`; a store error and
  a read the store refuses show `The processes could not be read. The service log has the details.` (E9; a failure of a panel read
  shows the same texts in the panel). A failure is logged as `processes view: reading failed` (event 78, error level) with the exception
  only, never an address value, a source or a program name. A superseded action ends silently.

## Limits and errors

- **Caps.** 11 lines (10 programs and `Other programs`) of at most 1,000 steps per chart; 256 sources; 20 rows per panel list; one decoded
  snapshot at a time; per panel two index seeks and one record read per point ([Storage reading](storage-reading.md), *History reads*).
  The work of an action does not grow with the number of stored snapshots or buckets in the range.
- **Memory per circuit** is derived from the fields of the types, **not measured**. The page keeps two results of 11 lines of at most
  1,000 points (about 0.5 MiB) and two chart views (about 1 MiB), the list of 256 sources, and 20 rows per panel. A moment read holds one
  decoded snapshot (up to the 16 MiB payload limit as bytes plus its parsed document, whose size is not measured). A comparison holds one
  decoded snapshot and the `ProgramFigures` of the earlier snapshot: at most 4,096 programs of names up to 1,024 bytes, about 8.5 MiB at
  most; while the table is built it holds at most 8,192 rows (the programs of both snapshots) before the cut to 20. The number of
  circuits is not bounded ([Web UI](web-ui.md), *Limits and errors*).
- **Time.** On the development host (4 logical CPUs) over one source with a year of hourly and 30 days of one-minute snapshots of 80
  programs, the whole chart read took 104 ms for a 30-day range and 779 ms for a 1-year range, a moment 2 ms and a comparison 4 ms, each the
  median of five; the figure of the reference host is in [Benchmarks](../BENCHMARKS.md), *Processes view*.
- A browser script cannot call anything but the two instance methods of the chart's reference (selection and click); both drop a value
  that is not finite or outside the storable range, as in the history view. A drag, a click and the stacking cannot be tested without a
  browser; the script is read in review and tried by hand.

## Related decisions

- [0224](../decisions/0224-the-processes-view-stacks-program-rollups-on-the-history-chart-and-compares-programs-of-one-source.md) — why the charts are the history chart in a stacked mode over the rollups, and the snapshots are those of one source.
- [0223](../decisions/0223-process-snapshots-are-folded-into-per-program-buckets-beside-a-source-total.md) — why the rollups have the cut, the cap and the other programs.

## Not here

- The rollups and their reads: [Program rollups](program-rollups.md); the snapshot reads: [Storage reading](storage-reading.md); the step
  ladder: [Time-series queries](time-series-query.md).
- The chart in general, the gate, external text and circuits: [Web UI](web-ui.md); the login: [Web login](web-login.md).
- The history view and its panel of any source: [History view](web-ui-history-view.md).
- Comparison of single processes, a program's last value or process count over time, a minimum-to-maximum band, connections per program,
  alerts on program memory, automatic refresh and selection by touch: not built.

## Implementation

`Vandox.Backend`: `Components/Pages/ProcessesPage` (the address, the controls and the reads) with `ProcessSnapshotPanel` (shared with the
history page; its callback is `BeforeChosen`), `HistoryRangeControls` (the presets and fields, with `PresetChosen` and `ApplyPressed`,
shared with the history page) and `PageLocation`; `Components/Charts/HistoryChart` (`Label`) and `HistoryChartInterop` (the `stacked`
flag), drawn by `createHistory` of `wwwroot/js/charts.js`; `Processes/` (`ProcessesAddress`, `ProcessesView` with the texts,
`ProcessesChartReader`, `ProcessesCharts`, `ProcessesChartView`, `ProcessesMomentReader`, `ProgramFigures`, `ProgramFigure`,
`ProgramComparisonView`, `GatedProgramRollupReader`); `History/` (`AddressTimes`, shared with `HistoryAddress`, `HistoryChartView.Stacked`,
`HistoryMomentReader.ClosestAsync`, `ClosestSnapshot`, `StoreReads`). `Vandox.Storage`: `SeriesLadder.Choose` and `SnapshotQuery.Source`;
`Vandox.Core`: `ProcessSnapshot.ListsEveryProgram`. The measurement is `--processes-view` of `tools/Vandox.StorageBenchmark`.
