# Spec: Network view

Source: Issue #51
Status: Draft

## Problem

The administrator of the monitored server wants to see its connection patterns: how many sockets are in which state and which
programs hold them over a period, which remote addresses it talks to most, and which listeners it has, a new one standing out (a port
taken over or opened by an intruder). Today the web UI shows one connection snapshot at a moment, in the moment panel of the
[history view](../../docs/areas/web-ui-history-view.md); the `connections` collector writes no metric line
([Time-series queries](../../docs/areas/time-series-query.md), *Gaps*), so nothing shows connections over time, and new listeners
appear only in the forensics report ([Listener changes](../../docs/areas/listener-changes.md)). Connection snapshots are removed
after `retention.snapshots` (30 days by default) and have no rollups: #458 rolled up process snapshots only and left connection
snapshots to this issue ([Retention and rollups](../../docs/areas/retention.md)). How far back the view reaches is Q1.

## Scenarios

- S1: Given connection snapshots of a source, when the signed-in user chooses *Network* in the navigation (between *Processes* and
  *Logs*), then `/network` shows for the last 24 hours the stacked chart *Connections per state*: one band per TCP state (`tcp` and
  `tcp6` together) and one band `UDP` (`udp` and `udp6`), so the top edge is every socket the snapshots counted.
- S2: Below it the stacked chart *Connections per program* shows the 10 programs (the owner's `command`, summed over its processes)
  holding the most sockets on average over the range, the highest at the bottom, and `Other programs` on top. The page notes `A
  socket held by several programs counts for each; sockets without an owner (such as TIME_WAIT) are not in this chart.`
- S3: Both charts follow the processes view's step, step text, legend (`<minimum> / <average> / <maximum>` of the step, `--` at a gap),
  colors, drag selection and click ([Processes view](../../docs/areas/web-ui-processes-view.md), *Charts*).
- S4: Below the charts the table *Top remote addresses* lists for the range the 20 remote addresses with the most connections on average
  (address, average connections, highest count in one snapshot), by average descending, then address; more rows end with `and <n> more
  addresses`. It notes `Most snapshots keep only their 25 most frequent remote addresses; an address outside them counts 0 there.`
  Addresses only: regular snapshots carry no remote ports ([Agent connections](../../docs/areas/agent-connections.md)).
- S5: Below it the panel *Listeners* shows the listener inventory of the source's latest connection snapshot at or before the end of
  the range, headed `Listeners of <time> UTC (<source>)`; with a moment `at` (a click in a chart), the source's snapshot closest to the
  moment within 10 minutes, with the history view's choice of snapshot. One row per listener in the snapshot's order (ports below
  1024 first): protocol, address and port, PID and command (or `no owner`), socket UID and user, with the listener texts of
  [Listener changes](../../docs/areas/listener-changes.md), *Report section*. At most 200 rows, then `and <n> more listeners`.
- S6: The panel marks a listener `new` when the **comparison snapshot**, the source's latest connection snapshot at or before 24 hours
  before the shown one, lacks its key, with the agent's rules: protocol, local address and port; an unconnected UDP socket on a port
  from 32768 to 60999 is never new; only ports 1 to 1023 are compared when either inventory holds only those
  ([Listener changes](../../docs/areas/listener-changes.md), *Comparison across agent starts*). New listeners stand first, in snapshot
  order; above the table: `<n> new listeners since <time> UTC.` or `No new listener since <time> UTC.`, the comparison's time.
- S7: The address holds `source`, `range`, `from`, `to` and `at` with the processes view's rules, order and errors (no `compare`), so
  the page can be bookmarked and used with back and forward; presets, *From (UTC)* and *To (UTC)* with *Apply* and a drag write a new
  entry, a click replaces it. The source choice lists the sources with connection data in the range (the agent's `connections`, an
  import's `legacy-monitor`), at most 256; the default is the source with the latest data, the first listed on a tie.
- S8: Given a range that reaches back beyond `retention.snapshots` (*1 y*), then the charts and the table show what Q1 decides is kept;
  the panel *Listeners* needs the snapshots themselves and shows E6 where none is kept.

## Errors and edge cases

- E1: No connection data of the source in the range: the charts, the table and their notes are left out and the page says `No
  connection data is stored in this range.`
- E2: A step without a snapshot of the source (agent offline, collector failing) is a gap: every band breaks there.
- E3: A range starting before the oldest stored connection data of the source: `This range starts before the oldest stored connection
  data of this source.`
- E4: A snapshot in the range with `aggregates_incomplete`: `Some snapshots in this range may not cover every socket.`
- E5: Data of the source from before the end of the range not yet in the charts (if Q1 brings rollups): the processes view's `Values
  after <time> UTC are not complete yet and may still change.`
- E6: No connection snapshot for the panel: `No connection snapshot was stored within 10 minutes of this moment.` (with `at`) or `No
  connection snapshot of this source is stored in this range.`
- E7: No comparison snapshot: `No connection snapshot of this source is stored from 24 hours before, so no listener is marked new.`
- E8: Inventories that cannot be compared, by the outcomes of [Listener changes](../../docs/areas/listener-changes.md): `No listener is
  marked new: the inventories are not comparable.` (a skipped line, different tables) or `No listener is marked new: ports 1 to 1023
  are not complete.`; for a comparison of the privileged ports only, the line of S6 ends `(ports 1 to 1023 only)`.
- E9: A snapshot of an import or of a 1.0 agent (no `inventory`): its listeners are shown and nothing is marked, with `Listeners of
  imported or older snapshots are not compared.`
- E10: A shown snapshot whose list holds only the privileged ports (`inventory` `privileged`): `This snapshot lists only the listeners
  on ports 1 to 1023.`
- E11: An invalid, repeated or contradicting address parameter, or a range in the future: the processes view's texts, naming the
  parameter, never its value; only the controls and the text are shown and nothing is read.
- E12: A read past 10 seconds: `Reading the connections took longer than 10 seconds and was stopped. Narrow the range.`; a store error
  or refused read: `The connections could not be read. The service log has the details.`, logged without an address value, source,
  command, user or remote address. A failed panel read shows the same texts in the panel.
- E13: Hostile commands and user names (any text up to 1,024 bytes, control characters included) are external text, never markup; a
  chart label is escaped visibly and capped at 64 characters. One IPv4 peer reached over an IPv4 and an IPv6 socket is two addresses
  (`::ffff:` form kept).

## Non-functional requirements

- N1: The page is behind the login and served on the web listener only ([Web login](../../docs/areas/web-login.md)).
- N2: Every store call takes a slot of the views' shared read gate; every action runs under a 10-second deadline, the wait included;
  leaving the page cancels its reads ([Web UI](../../docs/areas/web-ui.md), *Store reads of the views*).
- N3: Memory per circuit and store work per action are bounded by stated caps (11 lines of at most 1,000 steps per chart, 20 remote
  addresses shown, 256 sources, 200 listener rows, two decoded snapshots for the panel), not by the number of snapshots in the range;
  the area document states the caps and the memory derived from them.
- N4: The charts and the table of a 30-day range, and of a 1-year range as far as Q1 keeps data, over one source with a year of data
  at the agent's 60-second interval, finish within the 10-second deadline on the reference host (the measurement is the plan's).
- N5: No new configuration option.

## Acceptance criteria

- AC1: `/network` shows *Connections per state* with one band per TCP state and `UDP` (S1, S8).
- AC2: The page shows *Connections per program* of the top 10 programs plus `Other programs`, with its note (S2, S8).
- AC3: Step, step text, legend, colors, drag and click follow the processes view (S3).
- AC4: *Top remote addresses* lists the 20 addresses with the highest average count in the range, with the highest count, the order, the
  left-out line and the note (S4, E13).
- AC5: The panel *Listeners* shows the inventory of the latest snapshot at or before the end of the range, or closest to `at` within 10
  minutes, with its rows, cap and texts, or E6 (S5, E6, E10).
- AC6: New listeners since the previous day are highlighted: a listener the comparison snapshot lacks is marked `new` and listed first,
  with the line of S6, by the agent's key and port rules (S6, E7, E8, E9).
- AC7: The address, controls and source choice follow S7 with the processes view's rules and errors (S7, E11).
- AC8: A step without a snapshot breaks every band, and the notes of E1, E3, E4 and E5 appear exactly when the read calls for them (E1,
  E2, E3, E4, E5).
- AC9: Reads go through the gate under the 10-second deadline with the texts and log line of E12; every external text is displayed as
  the web UI's external text (N1, N2, N3, E12, E13).
- AC10: The navigation has *Network* between *Processes* and *Logs*, marked on `/network` with or without a query, and the measurements
  of N4 meet their figure (S1, N4, N5).

## Out of scope

- Remote ports and per-connection detail over time (only full snapshots list connections); a remote address's name (no DNS lookup).
- Listeners whose socket UID changed, and listeners that opened and closed within the day: the forensics report lists the agent's
  findings ([Listener changes](../../docs/areas/listener-changes.md)); this view compares two inventories only.
- Connections per PID over time (PIDs change; programs are summed); alerts on connections or new listeners; automatic refresh;
  selection by touch.

## Open questions

- Q1: How far back do the charts and *Top remote addresses* reach? Open — for the Product Manager. (A) A backend feature first, as #458
  was for #50: rollups of connection snapshots (states, programs, remote addresses per 5-minute and hourly bucket), then this view over
  them for any period they keep (`retention.rollups_1h`, 1,095 days); (B) this view alone, reading the snapshots themselves, so only
  `retention.snapshots` (30 days) back, and at most one snapshot per step (a sample at each step, not its average) to stay within the
  deadline; (C) the rollups built inside this feature. Recommendation: A.
- Q2: What does "new since the previous day" compare? Answered: (Lead) the shown inventory with the source's latest inventory at least
  24 hours older (S6), so a listener opened while the agent was stopped is new too; a listener that opened and closed in between is the
  report's (*Out of scope*).
- Q3: What is a "remote endpoint"? Answered: (Lead) a remote address, the unit regular snapshots count (S4).
- Q4: Does the issue's .NET 10 matter? Answered: no, the backend is on .NET 11 (record 0197); the view is unaffected.

## Area documents

- `docs/areas/web-ui-network-view.md` (new): the page `/network`, its address, controls, the two charts, the remote-address table, the
  panel *Listeners* with the new-listener marking, notes, calls, deadline, caps and memory per circuit.
- `docs/areas/web-ui.md`: the navigation entry, the list of views, the gate's list of views.
- `docs/areas/listener-changes.md`: the network view as a second place that shows new listeners, by inventory comparison, in *Scope* and
  *Not here*; `docs/areas/storage-reading.md`: the reads of the panel (latest snapshot of a source at or before a time); with Q1 (A or
  C) the area document of the connection rollups.
