# Spec: Processes view

Source: Issue #50
Status: Draft

## Problem

The administrator of the monitored server wants to know which programs use the memory and the CPU, over any period and at a chosen
moment, and what changed between two moments ("who is eating the RAM"). Today the web UI shows the programs and processes of one
snapshot only, in the moment panel of the [history view](../../docs/areas/web-ui-history-view.md); there is no view of programs over
time and no comparison. The memory and CPU per program are stored only inside process snapshots
([Agent system metrics](../../docs/areas/agent-system-metrics.md), *Processes*), which the time-series query of #47 does not read
([Time-series queries](../../docs/areas/time-series-query.md)), and which retention removes after `retention.snapshots` (30 days by
default; imported snapshots are kept, [Retention and rollups](../../docs/areas/retention.md)). Open issue #458 plans rollups of the
snapshots per program for the history beyond that period. How far back the view reaches is open question Q1.

## Scenarios

- S1: Given stored process snapshots, when the signed-in user chooses *Processes* in the navigation (between *History* and *Logs*), then
  `/processes` shows, for the last 24 hours, the chart *Memory per program*: one stacked area per program, the 10 programs with the highest
  average memory over the range in descending order from the bottom, the others summed as `Other programs`, so that the top edge is the
  memory of all programs. The heading says `Memory (PSS)` when every value of the range has a PSS, else `Memory (RSS)` for all values,
  with the note `RSS counts shared memory in every program that maps it, so the stack can exceed the server's memory.`
- S2: Given the same page, then the chart *CPU per program* below it stacks the CPU % of the 10 programs with the highest average CPU
  over the range and `Other programs`, in the unit the agent records (100 per fully used core).
- S3: Given the page, when the user chooses a preset (*1 h*, *24 h*, *7 d*, *30 d*, *1 y*), enters *From (UTC)* and *To (UTC)* with
  *Apply*, or drags across a chart, then both charts show that range, and the address holds it under the history view's rules, so the
  page can be bookmarked and used with back and forward.
- S4: Given the page, when the user clicks a chart, then the moment is set and the panel *Top processes* shows the process snapshot
  closest to it within 10 minutes with the tables *Programs* and *Processes* of the history view's moment panel, and the link
  *History of this moment* to `/history` with that moment.
- S5: Given a moment, when the user presses *Compare from here* and then clicks another time in a chart, then the panel *Comparison*
  shows, for the snapshots closest to the two moments (the earlier is the base), one row per program: memory at each point, its change,
  CPU % at each point, and the state `new` (only at the later point), `gone` (only at the earlier point) or none; rows by memory change
  descending, then program name; at most 20 rows, then `and <n> more programs`.
- S6: Given snapshots of more than one source (the agent and an import), then the page shows one source at a time: the choice lists the
  sources with snapshots in the range, by name, and the default is the source of the latest snapshot in the range.

## Errors and edge cases

- E1: No process snapshot of the source in the range: the charts are left out and the page says `No process snapshots are stored in
  this range.`
- E2: A span without snapshots (a recorded gap, the agent offline, the collector failing) breaks both stacks; it is never drawn across
  ([0028](../../docs/decisions/0028-data-gaps-are-always-recorded.md)).
- E3: The range starts before the oldest per-program values still stored: the note `This range starts before the oldest stored process
  data; older values are no longer stored.` (what "stored" covers depends on Q1).
- E4: A snapshot in the range that is not complete (a legacy snapshot cut short): the note `Some snapshots in this range do not list
  every program.`
- E5: An address with an invalid, repeated or contradicting parameter: the history view's address errors, naming the parameter, never
  its value; only the controls and the text are shown and nothing is read.
- E6: No snapshot within 10 minutes of a moment: the panel says `No process snapshot was stored within 10 minutes of this moment.` (for
  the comparison: `... of the first point` or `... of the second point`); both moments closest to the same snapshot:
  `Both points show the same snapshot; choose points further apart.`
- E7: A read past 10 seconds: `Reading the processes took longer than 10 seconds and was stopped. Narrow the range.`; a store error:
  `The processes could not be read. The service log has the details.`, logged without an address value or a program name.
- E8: Hostile program, user or command names: shown as external text, never as markup; in a chart label escaped visibly and capped.
- E9: A range that begins at or after now: `This range lies in the future.` and nothing is read.

## Non-functional requirements

- N1: The page is behind the login and served on the web listener only ([Web login](../../docs/areas/web-login.md)).
- N2: Every store call takes a slot of the views' shared read gate; every action runs under a 10-second deadline, the wait included;
  leaving the page cancels its reads ([Web UI](../../docs/areas/web-ui.md), *Store reads of the views*).
- N3: Memory per circuit and store work per action are bounded by stated caps, not by the number of stored snapshots; the area document
  states them.
- N4: A chart of a 30-day range of the reference database draws within the 10-second deadline on the reference host (the measurement
  and its figure are the plan's).
- N5: No new configuration option; snapshots of the agent and of the legacy import are shown, the latter with RSS.

## Acceptance criteria

- AC1: `/processes` shows the stacked chart *Memory per program* of the 10 programs with the highest average memory in the range plus
  `Other programs`, PSS when every value has one, else RSS with its note (S1, N5).
- AC2: The page shows the stacked chart *CPU per program* of its own top 10 plus `Other programs` (S2).
- AC3: Range presets, *From*/*To* and a drag selection set the range in the address with the history view's address rules and errors
  (S3, E5, E9).
- AC4: A click sets the moment; the panel *Top processes* shows the closest process snapshot within 10 minutes, its texts and the link
  to the history view (S4, E6).
- AC5: Two points give the comparison per program with memory and CPU at each, the change, `new` and `gone`, its order and cap (S5,
  E6).
- AC6: The page shows one source at a time, by the stated default and choice (S6).
- AC7: Gaps break the stacks, and the notes of E1, E3 and E4 appear exactly in their cases (E1, E2, E3, E4).
- AC8: Reads go through the gate under the 10-second deadline with the texts and log line of E7, and every external text is displayed
  as the web UI's external text (N1, N2, N3, E7, E8).
- AC9: The navigation has the entry *Processes* between *History* and *Logs*, marked on `/processes` with or without a query (S1).
- AC10: The 30-day measurement of N4 meets its figure (N4).

## Out of scope

- A comparison of single processes: a snapshot lists only the top 25 processes by memory and by CPU, so a process missing from one is
  not known to be gone; programs are summed over every process read.
- Connections per program and the network view (#51); alerts on program memory; automatic refresh; selection by touch.

## Open questions

- Q1: How far back does the view reach — only as far as process snapshots are kept (30 days by default), or for any period through
  per-program rollups (#458)? Escalated to the Product Manager.
- Q2: Which programs are stacked? Answered: the 10 with the highest average over the range per chart, the rest summed, so a chart stays
  readable with the six chart colors and a host's hundreds of programs.
- Q3: What does "what grew, what appeared" compare? Answered: programs, not processes (*Out of scope*).

## Area documents

- `docs/areas/web-ui-processes-view.md` (new): the page `/processes`, its address, controls, the two charts and their notes, the panels
  *Top processes* and *Comparison*, calls, deadline, caps and memory per circuit.
- `docs/areas/web-ui.md`: the navigation entry and the list of views.
- `docs/areas/storage-reading.md`: the reads the view adds; `docs/areas/web-ui-history-view.md`: the processes view leaves *Not here*.
- By Q1: `docs/areas/retention.md` and `docs/areas/storage.md` for per-program rollups.
