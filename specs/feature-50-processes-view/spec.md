# Spec: Processes view

Source: Issue #50
Status: Approved by Lead

## Problem

The administrator of the monitored server wants to know which programs use the memory and the CPU, over any period and at a chosen
moment, and what changed between two moments ("who is eating the RAM"). Today the web UI shows the programs and processes of one
snapshot only, in the moment panel of the [history view](../../docs/areas/web-ui-history-view.md); there is no view of programs over
time and no comparison. Process snapshots are removed after `retention.snapshots` (30 days by default,
[Retention and rollups](../../docs/areas/retention.md)); what remains of them are the per-program rollups of #458
([Program rollups](../../docs/areas/program-rollups.md)), which no page reads yet. This feature draws its charts from them (Q1).

## Scenarios

- S1: Given per-program rollups of a source, when the signed-in user chooses *Processes* in the navigation (between *History* and
  *Logs*), then `/processes` shows, for the last 24 hours, the chart *Memory per program*: one stacked area per program, the 10 programs
  with the highest average memory over the range, the highest at the bottom, and `Other programs` on top (every other program, cut
  or capped ones included), so that the top edge is the source's average total. A program a snapshot does not
  name counts 0 in it. The vertical axis says `Memory (PSS), MiB` when every folded snapshot of the range is a PSS snapshot, else `Memory (RSS), MiB`
  for the whole range with the note `RSS counts shared memory in every program that maps it, so the stack can exceed the server's
  memory.`
- S2: Given the same page, then the chart *CPU per program* below it stacks the CPU % of its own 10 programs with the highest average CPU
  over the range and `Other programs`, in the unit the agent records (100 per fully used core).
- S3: Given either chart, then each point is the average of its step, the page says `Each point is the average of <step>; the legend
  shows the minimum and maximum.`, and the legend shows for each program under the pointer its own `<minimum> / <average> / <maximum>`
  (a step in which a snapshot does not name the program has the minimum 0) and for `Other programs` its average, `--` at a gap. Unlike
  the stacked chart of the incidents view, which keeps refusing a gap, these stacked charts accept a gap step and report a drag and a
  click as the history chart does ([Web UI](../../docs/areas/web-ui.md), *Charts*). The step is the
  smallest of the time-series query's steps from 5 minutes up ([Time-series queries](../../docs/areas/time-series-query.md),
  *Resolution*) for which at most the chart's width in CSS pixels, at most 1,000, steps overlap the range; steps below one hour only
  while the range starts within `retention.rollups_5m`.
- S4: Given the page, when the user chooses a preset (*1 h*, *24 h*, *7 d*, *30 d*, *1 y*), enters *From (UTC)* and *To (UTC)* with
  *Apply*, or drags across a chart, then both charts show that range, so the page can be bookmarked and used with back and forward.
  The address has the history view's `range`, `from`, `to` and `at`, with its rules and errors, and instead of `metric`:
  - `source`: a name of the wire format's name pattern, 1 to 128 bytes ([Wire format](../../docs/areas/wire-format.md)); without it
    the default of S7 is shown and the address is not extended; a source without buckets in the range gives E1;
  - `compare`: the second point of a comparison, in the format of `at`; refused without `at` as `The address needs both a moment and a
    second point for a comparison.`
  A change of the source drops `at` and `compare`; a change of the range keeps them.
- S5: Given the page, when the user clicks a chart, then `at` is set, `compare` dropped and the history entry replaced, as in the
  history view, and the panel *Top processes* shows the process snapshot of the shown source closest to the moment within 10 minutes,
  with the history view's choice of snapshot, its tables *Programs* and *Processes*, its notes and *Snapshot before this moment* (of
  the same source), and the link *History of this moment* to `/history` with that moment, whose panel shows the closest snapshot of any
  source.
- S6: Given a moment, when the user presses *Compare from here* and then clicks another time in a chart, then that click writes
  `compare`, keeps `at` and replaces the history entry, and the panel *Comparison* shows in place of *Top processes*, for the source's snapshots closest to the two
  points (the earlier is the base), one row per program: memory at each point, its change, CPU % at each point, and the state `new`
  (only at the later point), `gone` (only at the earlier point) or none; memory is PSS when every program of both snapshots has one,
  else RSS for both, and the column says which; rows by memory change descending, then program name; at most 20 rows, then `and <n>
  more programs (<k> new, <g> gone).` with the counts of the rows left out.
- S7: Given rollups of more than one source (the agent and an import), then the page shows one source at a time: the choice lists the
  sources that have a bucket in the range, in the store's order, and the default is the source with the latest bucket in the range (the
  first listed on a tie). Beyond 256 sources the page says `The list shows the first 256 sources.`
- S8: Given a range that reaches back beyond `retention.snapshots` (for example *1 y*), then both charts show the programs over the whole
  range as far as `retention.rollups_1h` keeps them; the panels *Top processes* and *Comparison* need the snapshots themselves and reach
  only as far back as those are kept (E6).

## Errors and edge cases

- E1: No bucket of the source in the range: the charts are left out and the page says `No process data is stored in this range.`
- E2: A step without a folded snapshot of the source (the agent offline, the collector failing) is a gap: every band of both stacks
  breaks there and is never drawn across. A recorded gap shorter than a step whose snapshots exist breaks nothing.
- E3: The read reports that the range starts before the oldest bucket of the source in the tier it read: the note `This range starts
  before the oldest stored process data of this source.`
- E4: A step folded from a snapshot that does not list every program (#458's definition, *An incomplete snapshot* in
  [Program rollups](../../docs/areas/program-rollups.md), not the snapshot's `complete` flag): the note `Some snapshots in this range do
  not list every program.`
- E5: Snapshots of the source not folded yet (the fold runs every 5 minutes) from a time before the end of the range: the note `Values
  after <time> UTC are not complete yet and may still change.`
- E6: No snapshot of the source within 10 minutes of a moment: the panel says `No process snapshot was stored within 10 minutes of this
  moment.` (for the comparison: `... of the first point` or `... of the second point`); both moments closest to the same snapshot:
  `Both points show the same snapshot; choose points further apart.`
- E7: A program outside a snapshot's 50 by memory and 50 by CPU, or beyond a bucket's 500 names, is in `Other programs` there; its area
  reads lower in that step without a note, as the rollups keep no flag for it.
- E8: An address with an invalid, repeated or contradicting parameter: the history view's address errors, naming the parameter, never
  its value; only the controls and the text are shown and nothing is read. A range that begins at or after now: `This range lies in the
  future.` and nothing is read.
- E9: A read past 10 seconds: `Reading the processes took longer than 10 seconds and was stopped. Narrow the range.`; a store error or
  a refused read: `The processes could not be read. The service log has the details.`, logged without an address value or a program name.
- E10: Hostile program, user or command names (any text up to 1,024 bytes, control characters included): shown as external text,
  never as markup; in a chart label escaped visibly and capped at 64 characters. Source names follow the name pattern and are shown as
  plain text.
- E11: Either compared snapshot does not list every program (#458's definition, as in E4; a legacy import's snapshot cut short): no row
  is marked `new` or `gone`, the left-out counts read `and <n> more programs.`, and the panel says `One of the two snapshots does not
  list every program, so no program is marked new or gone.` A program the two snapshots name differently (the two legacy import
  formats) shows as two programs.

## Non-functional requirements

- N1: The page is behind the login and served on the web listener only ([Web login](../../docs/areas/web-login.md)).
- N2: Every store call takes a slot of the views' shared read gate; every action runs under a 10-second deadline, the wait included;
  leaving the page cancels its reads ([Web UI](../../docs/areas/web-ui.md), *Store reads of the views*).
- N3: Memory per circuit and store work per action are bounded by stated caps (11 lines of at most 1,000 steps per chart, 256 sources,
  20 rows per panel list, one decoded snapshot at a time, per point two index seeks and one record read), not by the number of stored snapshots or buckets in the range; the charts never read a
  snapshot; the area document states the caps and the memory derived from them.
- N4: The charts of a 30-day and of a 1-year range, over a database of one source with a year of hourly and 30 days of one-minute
  snapshots of 80 programs, folded, draw within the 10-second deadline on the reference host (the measurement is the plan's).
- N5: No new configuration option; the agent's snapshots and those of the legacy import are shown, the latter only with RSS.

## Acceptance criteria

- AC1: `/processes` shows the stacked chart *Memory per program* of the 10 programs with the highest average memory in the range plus
  `Other programs`, with the axis label and note of the memory key the read names (S1, S8, N5).
- AC2: The page shows the stacked chart *CPU per program* of its own top 10 plus `Other programs` (S2, S8).
- AC3: The step, the step text and the legend follow S3, a missing program reading 0 and its minimum 0 (S3).
- AC4: Range presets, *From*/*To* and a drag selection set the range in the address; `source`, `at` and `compare` follow S4, with the
  history view's address rules and errors (S4, E8).
- AC5: A click sets the moment; the panel *Top processes* shows the source's closest process snapshot within 10 minutes, its texts,
  *Snapshot before this moment* and the link to the history view (S5, E6).
- AC6: Two points give the comparison per program with memory and CPU at each, the change, `new` and `gone` (none, with the note, when
  a snapshot does not list every program), the memory key, the order, the cap and the left-out counts, and `compare` in the address
  (S4, S6, E6, E11).
- AC7: The page shows one source at a time, by the stated list, default and cap (S7).
- AC8: A step without a folded snapshot breaks every band, and the notes of E1, E3, E4 and E5 appear exactly when the read calls for
  them, also for a range beyond `retention.snapshots` (E1, E2, E3, E4, E5, E7, S8).
- AC9: Reads go through the gate under the 10-second deadline with the texts and log line of E9, and every external text is displayed
  as the web UI's external text (N1, N2, N3, E9, E10).
- AC10: The navigation has the entry *Processes* between *History* and *Logs*, marked on `/processes` with or without a query, and the
  30-day and 1-year measurements of N4 meet their figure (S1, N4).

## Out of scope

- A comparison of single processes: a snapshot lists only the top 25 processes by memory and by CPU, so a process missing from one is
  not known to be gone; programs are summed over every process read.
- A program's last value or process count over time, which the rollups do not store (record 0223); a minimum-to-maximum band.
- Connections per program and the network view (#51); alerts on program memory; automatic refresh; selection by touch.

## Open questions

- Q1: How far back does the view reach — only as far as process snapshots are kept (30 days by default), or for any period through
  per-program rollups (#458)? Answered: (Product Manager, 2026-11-21) option B: #458 first, then this view on its per-program rollups,
  for any period they keep; #458 is built (record 0223).
- Q2: Which programs are stacked? Answered: the 10 with the highest average over the range per chart, the rest summed, so a chart stays
  readable with six chart colors.
- Q3: What does "what grew, what appeared" compare? Answered: programs, not processes (*Out of scope*).

## Area documents

- `docs/areas/web-ui-processes-view.md` (new): the page `/processes`, its address, controls, the two charts, their step and notes, the
  panels *Top processes* and *Comparison*, calls, deadline, caps and memory per circuit.
- `docs/areas/web-ui.md`: the navigation entry, the list of views, the gate's list of views, and in *Charts* the stacked chart that
  accepts a gap step and reports selection and click, with min/avg/max in the legend, beside the incidents chart's refusal.
- `docs/areas/program-rollups.md`: *Not here* names the processes view as the reader; `docs/areas/storage-reading.md`: the read of
  the closest process snapshot of one source; `docs/areas/web-ui-history-view.md`: the processes view leaves *Not here*.

## Challenge

- M1 (stacked chart refuses gaps): accepted. S3 states that these stacked charts accept a gap step, show each program's own
  minimum, average and maximum, and report drag and click like the history chart, while the incidents chart keeps its refusal; E2 says
  every band breaks at a gap step; *Area documents* lists web-ui.md *Charts*.
- M2 (`new`/`gone` on incomplete snapshots): accepted. E11 drops both states with a note when either snapshot does not list every
  program by #458's definition, and states that the two legacy formats' names show as two programs; AC6 checks it.
- 3 (per-source snapshot read): accepted. S5 shows the source's snapshot, offers *Snapshot before this moment* of the same source and
  says the history link shows the closest snapshot of any source; N3 bounds the read; storage-reading.md is listed.
- 4 (address): accepted. S4 defines `source` (name pattern, 1 to 128 bytes, default when absent, E1 when unknown) and `compare` with its
  error; S5 and S6 say which click writes what and that a click replaces the entry; a change of source drops both points.
- 5 (source names, label cap): accepted. E10 limits free text to program, user and command names and caps labels at 64 characters.
- 6 (recorded gaps): accepted. E2 and AC8 speak of a step without a folded snapshot; a shorter recorded gap breaks nothing.
- 7 (cap hides new programs): accepted. The closing line counts the `new` and `gone` rows left out.
