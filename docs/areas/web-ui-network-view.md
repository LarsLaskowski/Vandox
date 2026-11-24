# Network view

## Scope

The page `/network` of the [web UI](web-ui.md): the address that holds the source, the range and the moment, the controls, the two stacked
charts *Connections per state* and *Sockets held per program*, the table *Top remote addresses* and the panel *Listeners* with the
listeners that are new since the previous day marked. It covers the texts, the calls and the deadline, and the caps with the memory per
circuit. The administrator relies on it to see how many sockets are in which state and which programs hold them over any period, which
remote addresses the server talks to most, and which listeners it has, a new one standing out (a port taken over or opened by an
intruder). The charts and the table are drawn from the connection rollups ([Connection rollups](connection-rollups.md)), so they reach
back as far as those are kept (`retention.rollups_1h`); the panel needs the connection snapshots themselves, which follow
`retention.snapshots` ([Retention and rollups](retention.md)). The page only reads: it raises no alert and shows a gap as a gap.

## Behavior

### Page

- The page is behind the login ([Web login](web-login.md)) and is served on the web listener only. The navigation entry *Network*
  (`network`) lies between *Processes* and *Logs* and is marked on `/network` with or without a query. The heading is `Network`.
- Prerendered (the first response) the page shows the heading, the range controls and `Loading…` and reads nothing; it reads once its
  live connection is up ([Web UI](web-ui.md), *Pages and render mode*). An address with an error is shown the same way.
- Top to bottom: the presets, *From (UTC)*, *To (UTC)* and *Apply*; the choice *Source*; the text of a control; the notes; *Connections per
  state* with its chart; *Sockets held per program* with its note and chart; *Top remote addresses*; *Listeners*.
- A link of the view is `network` plus the query of a validated address; the page builds no other link.

### Address

Everything the page shows is in the address, so it can be bookmarked, shared and used with the browser's back and forward buttons.

| Parameter | Value |
| --------- | ----- |
| `source` | a name of the wire format's name pattern, 1 to 128 bytes (starts with an ASCII letter or digit, then letters, digits and `. _ : / @ + -`; a trailing line feed is refused); an empty value is skipped |
| `range`, `from`, `to`, `at` | as in the [History view](web-ui-history-view.md), *Address*: `1h`, `24h`, `7d`, `30d` or `1y`; times as `yyyy-MM-ddTHH:mm[:ss]` in UTC; `at` is the moment |

- The rules are those of the [Processes view](web-ui-processes-view.md), *Address*, without `compare`, which is an unknown key here and
  ignored like any other. Without `source` the page shows the default source (*Source*) and the address is not extended; without `range`,
  `from` and `to` the range is the last 24 hours; a preset stays relative.
- The parameters are read in the order `source`, `range`, `from`, `to`, `at`, and the first error wins; then come the range rules of the
  history view. The errors are the history view's (`The address gives the parameter <name> more than once.`, `The address has an invalid
  value for the parameter <name>.`, and the three about the range), naming the parameter, never its value. An address with an error
  shows the heading, the controls and the text, and nothing else, and reads nothing (E11).
- An address is written only from values checked again, the parameters in the order `source`, `range` (only for a preset), `from` and `to`
  (only as a pair), `at`, each percent-encoded. A value the checks refuse is shown as the text of the refusal without navigating.
- A range that begins at or after now shows `This range lies in the future.` and reads nothing; no panel is shown.
- The page follows every change of its address. A change of the source, the preset, `from` or `to` reads the charts again; a change of
  the shown source, of the range or of `at` reads the panel again (*Panel*).

### Controls

- Presets *1 h*, *24 h*, *7 d*, *30 d*, *1 y*; the current one is marked. *From (UTC)* and *To (UTC)* with *Apply*, checked with the
  history view's rules.
- *Source* is a choice of the sources that have connection data in the range; a source of the address that is not among them is added as
  chosen. A handler takes only a listed source; any other value is ignored, and so is the choice of the address's own source. A choice
  writes `source` and drops `at`, which belongs to the former source.
- A preset, *Apply*, a drag selection and a source each write a **new history entry**; a drag selection and a preset keep `source` and
  `at`. A click in a chart **replaces** the current entry and sets `at` to the time under the pointer, floored to the whole second.

### Source

- The list holds the sources that have a connection bucket in the range (the agent's `connections`, an import's `legacy-monitor`), by
  source in the byte order of SQLite, at most 256. When more exist the page says `The list shows the first 256 sources.`
- Without `source` the default is the listed source with the latest bucket in the range, the first listed on a tie. The page shows one
  source at a time, in the charts, the table and the panel.

### Charts

- Both charts are the history chart in its stacked mode ([Web UI](web-ui.md), *Charts*), with the step, the step text, the legend, the
  colors, the drag selection and the click of the processes view ([Processes view](web-ui-processes-view.md), *Charts*): one scale from 0,
  the vertical axis `Sockets`, a label escaped visibly and capped at 64 characters.
- **Connections per state** stacks one band per TCP state (`tcp` and `tcp6` together) and one band `UDP` (`udp` and `udp6`), each state
  the range's rollups name, in the ordinal order of its name from the bottom, so the top edge is the average of every socket the
  snapshots counted.
- **Sockets held per program** stacks the 10 programs (the processes' `command`) with the highest average over the range, the highest at
  the bottom, and `Other programs` on top: every other program, those the fold cut or capped included. A program's value is the sum over
  its processes of the sockets each process holds, as the snapshot's `processes` list counts them, so a socket counts once **per holding
  process**, also within one program. The page notes below the heading `Each process counts every socket it holds, so a socket shared
  by several processes (a server's master and its workers) counts once for each, and the stack can exceed the sockets per state;
  sockets without an owner (such as TIME_WAIT) are not in this chart.`
- **Legend.** For the band under the pointer the legend shows `<minimum> / <average> / <maximum>` of its step (the minimum is 0 in a step
  in which a snapshot does not name the state or program) and for `Other programs` its average; `--` at a gap.
- **Gap.** A step without a folded snapshot of the source (the agent offline, the collector failing) is a gap, and every band of both
  charts breaks there and is never drawn across.
- The accessible names of the chart hosts are the headings of the charts.

### Notes

The notes are paragraphs above the charts, each exactly when the rollup read calls for it, in this order:

- E1: `No connection data is stored in this range.` when the source has no bucket in the range, or no source has one; the charts, the
  table and the other notes are left out then.
- `The list shows the first 256 sources.`
- E3: `This range starts before the oldest stored connection data of this source.`
- E4: `Some snapshots in this range may not cover every socket.` when a folded step holds a snapshot whose aggregates were incomplete
  (`aggregates_incomplete`).
- E5: `Values after <time> UTC are not complete yet and may still change.` when snapshots of the source from before the end of the range
  are not folded yet (the fold runs every 5 minutes); times are `yyyy-MM-dd HH:mm:ss`.
- The step text: `Each point is the average of <step>; the legend shows the minimum and maximum.`

### Top remote addresses

- The table lists for the range the 20 remote addresses with the highest average, by average descending, then address (ordinal), with the
  columns *Address*, *Average connections* (one decimal) and *Highest in one snapshot*. When the rollups hold further addresses it ends
  with `and <n> more addresses`. It is shown only with the charts, in its own action after them (*Calls and deadline*), under `Loading…`
  until it is read.
- Below it, the note `Each snapshot counts only its 25 most frequent remote addresses and the rollups keep only the most frequent ones; an
  address outside them counts 0 there.` and, when a step's snapshots lost an address to a bucket's cap (E14), `Some periods in this range
  held more remote addresses than the rollups keep, so an address can read lower there and the count of further addresses is a lower
  bound.`
- The table holds addresses only: regular snapshots carry no remote ports ([Agent connections](agent-connections.md)). One IPv4 peer
  reached over an IPv4 and an IPv6 socket is two addresses (the `::ffff:` form is kept). An address is external text.

### Panel

The panel *Listeners* shows the listener inventory of one connection snapshot of the source the charts show: the address's `source`,
else the default of the charts. It is read for the combination of that source, the range and `at` and again when any of them changes; a
newer read supersedes the running one.

- **Which snapshot.** Without `at` the source's latest connection snapshot captured in the range (from `from` up to its end; an older one
  is never shown). With `at` the source's snapshot closest to the moment within **10 minutes**, with the history view's choice of snapshot
  (the nearer side, the earlier on equal distance, the lower record ID among one capture time). The heading is `Listeners of <time> UTC
  (<source>)`.
- **Table.** The columns are *State* (`new` or empty), *Protocol*, *Endpoint* (local address and port), *Owner* (`<PID> <command>`, or
  `no owner`) and *Socket UID* (`<uid> <user>`, `<uid>` without a user name, `unknown` without a UID, ending ` (lowest of several)` when
  the sockets of the listener have several). One row per listener in the snapshot's order (ports below 1024 first); at most 200 rows,
  then `and <n> more listeners`.
- **New listeners.** A listener is `new` when the **comparison snapshot**, the source's latest connection snapshot captured from 48 up to
  24 hours before the shown one (an older one is never used), lacks its key, by the agent's rules ([Listener changes](listener-changes.md),
  *Comparison across agent starts*): protocol, local address and port; an unconnected UDP socket on a port from 32768 to 60999 is never
  new; only the ports 1 to 1023 are compared when either inventory holds only those. New listeners stand first, in snapshot order,
  then the rest. Above the table the line reads `<n> new listeners since <time> UTC.` (`1 new listener since <time> UTC.` for one) or `No
  new listener since <time> UTC.`, with the capture time of the comparison snapshot. Both snapshots are picked by capture time, so after the
  agent's clock stepped another snapshot can be picked, and the line names its time. A listener that opened and closed between the two
  inventories, and a changed socket UID, are not marked; the forensics report keeps them.
- **Texts of the panel**, each exactly when it applies, the notes in this order:
  - E6: `No connection snapshot was stored within 10 minutes of this moment.` (with `at`) or `No connection snapshot of this source is
    stored in this range.`; this also holds where a range reaches beyond `retention.snapshots` and the snapshot is removed.
  - E7: `No connection snapshot of this source is stored from 24 to 48 hours before, so no listener is marked new.` in place of the line.
  - E9: the shown or the comparison snapshot is one of an import or of a 1.0 agent (no `inventory`): the listeners are shown, nothing is
    marked and the panel says `Listeners of imported or older snapshots are not compared.` A shown snapshot without `inventory` reads
    no comparison snapshot.
  - E8: inventories that cannot be compared, by the outcomes of [Listener changes](listener-changes.md): `No listener is marked new: the
    inventories are not comparable.` (a skipped line, different tables) or `No listener is marked new: ports 1 to 1023 are not
    complete.`; for a comparison of the privileged ports only, the line ends `(ports 1 to 1023 only)` without a period.
  - E10: a shown snapshot whose list holds only the privileged ports (`inventory` `privileged`): `This snapshot lists only the listeners on
    ports 1 to 1023.`
- **Source of the panel.** With `source` in the address the read starts at once. Without it the read starts after a chart result, and
  again when a change of the range moves the default source. Without `source` and before the first chart result the panel says
  `Loading…`; when no source has connection data in the range it says `No source has connection data in this range, so no snapshot is
  shown.`, and when the chart action failed `The source could not be read, so no snapshot is shown.` (E15); in both the read is
  cancelled. A future range or an address error shows no panel and reads nothing.
- Commands, user names, protocols, addresses and sources are external text ([Web UI](web-ui.md), *External text*): in the table at most
  256 characters in at most 16 runs.

### Calls and deadline

- The page runs three kinds of actions, each cancelling the running action of its own kind: a **chart action**, an **address action** and
  a **panel action**. A chart action is at most two gated calls (the source list and the rollup read of the states and programs). The
  address action is one gated call (the remote addresses at the range and the step of the charts); it starts when a chart action ended with
  times, and a new chart action cancels it. A panel action is at most five calls (the latest snapshot stamp or the closest one, its
  record, and when it has an `inventory` the comparison stamp and its record), with two decoded snapshots. Leaving the page cancels all.
- Every store call takes one slot of the views' shared read gate ([Web UI](web-ui.md), *Store reads of the views*); the rollup reads go
  through a gated reader on the same gate. Every action runs under a deadline of **10 seconds**, which includes the wait for a slot. The
  address action and the panel action run beside each other on the gate's two slots; the chart action has ended by then.
- A read past the deadline shows `Reading the connections took longer than 10 seconds and was stopped. Narrow the range.`; a store error
  and a read the store refuses show `The connections could not be read. The service log has the details.` (E12). Each action fails only its own section: a
  failed table or panel read shows the text in its place, the charts kept. A failure is logged as `network view: reading failed` (event
  81, error level) with the exception only, never an address value, a source, a command, a user or a remote address. A superseded action
  ends silently.

## Limits and errors

- **Caps.** At most 13 state lines and 11 program lines (10 programs and `Other programs`) of at most 1,000 steps; 256 sources; 20
  remote addresses; 200 listener rows; two decoded snapshots for the panel. The work of an action does not grow with the number of
  stored snapshots or buckets in the range: the charts and the table never read a snapshot, and the panel reads at most five calls.
- **Memory per circuit** is derived from the fields of the types, **not measured**. The page keeps the result of one chart read (24
  lines of at most 1,000 points, about 0.6 MiB) and two chart views (about 1 MiB), the list of 256 sources, 20 table rows and 200 panel
  rows. A panel read holds at most two decoded snapshots (each up to the 16 MiB payload limit as bytes plus its parsed document, whose size
  is not measured); the page keeps only the rows built from them. The number of circuits is not bounded ([Web UI](web-ui.md), *Limits
  and errors*).
- **Time.** The chart action and the address action of a 30-day, a 1-year and a 1,095-day range each finish within the deadline: see
  [Benchmarks](../BENCHMARKS.md), *Network view*, for the figures and the host they were measured on. The two reads of 1,095 days took
  4,251 and 8,022 ms on the reference host ([Connection rollups](connection-rollups.md), *Disk and time*), which is why the table has an
  action of its own.
- A browser script cannot call anything but the two instance methods of the chart's reference (selection and click); both drop a value
  that is not finite or outside the storable range. A drag, a click and the stacking cannot be tested without a browser; the script is
  read in review and tried by hand.

## Related decisions

- [0226](../decisions/0226-the-network-view-stacks-connection-rollups-and-marks-listeners-new-against-an-inventory-a-day-older.md) — why the charts and the table read the rollups, the table has its own action, and a listener is marked new against the inventory 24 to 48 hours older.
- [0225](../decisions/0225-connection-snapshots-are-folded-into-per-state-program-and-address-buckets-ended-buckets-trimmed.md) — why the rollups have the states, programs and addresses the view shows.
- [0224](../decisions/0224-the-processes-view-stacks-program-rollups-on-the-history-chart-and-compares-programs-of-one-source.md) — the stacked mode of the history chart.

## Not here

- The rollups and their reads: [Connection rollups](connection-rollups.md); the snapshot reads: [Storage reading](storage-reading.md); the step
  ladder: [Time-series queries](time-series-query.md).
- The chart in general, the gate, external text and circuits: [Web UI](web-ui.md); the login: [Web login](web-login.md).
- The single connection snapshot at a moment: [History view](web-ui-history-view.md); the program charts: [Processes view](web-ui-processes-view.md).
- The agent's own listener findings and the forensics report: [Listener changes](listener-changes.md).
- Remote ports and per-connection detail over time, a remote address's name (no DNS lookup), connections per process ID over time, alerts
  on connections or new listeners, automatic refresh and selection by touch: not built.

## Implementation

`Vandox.Backend`: `Components/Pages/NetworkPage` (the address, the controls and the three actions) with `HistoryRangeControls`,
`HistorySnapshotTable` and `PageLocation`; `Components/Charts/HistoryChart`, drawn by `createHistory` of `wwwroot/js/charts.js`;
`Network/` (`NetworkAddress`, `NetworkView` with the texts, `NetworkChartReader`, `NetworkCharts`, `NetworkAddressReader`,
`NetworkAddresses`, `NetworkChartView`, `NetworkListenerReader`, `NetworkListenerView`, `GatedConnectionRollupReader`); `History/`
(`AddressTimes`, `StackedLine`, `SnapshotCells`, `SourceChoice`, `HistoryMomentReader.ClosestAsync`). `Vandox.Storage`:
`IConnectionRollupReader`, `IHistoryReader.LatestSnapshotStampAsync` with `LatestSnapshotQuery`; `Vandox.Core`:
`ListenerComparer.Compare` with its limit. The measurement is `--network-view` of `tools/Vandox.StorageBenchmark`.
