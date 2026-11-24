# Spec: Network view

Source: Issue #51
Status: Approved by Lead

## Problem

The administrator of the monitored server wants to see its connection patterns: how many sockets are in which state and which
programs hold them over a period, which remote addresses it talks to most, and which listeners it has, a new one standing out (a port
taken over or opened by an intruder). Today the web UI shows one connection snapshot at a moment, in the moment panel of the
[history view](../../docs/areas/web-ui-history-view.md); the `connections` collector writes no metric line
([Time-series queries](../../docs/areas/time-series-query.md), *Gaps*), so nothing shows connections over time, and new listeners
appear only in the forensics report ([Listener changes](../../docs/areas/listener-changes.md)). Connection snapshots are removed
after `retention.snapshots` (30 days by default) and have no rollups: #458 rolled up process snapshots only and left connection
snapshots to this issue ([Retention and rollups](../../docs/areas/retention.md)). The charts and the remote-address table are drawn
from the rollups of connection snapshots that #486 built first (Q1; [Connection rollups](../../docs/areas/connection-rollups.md),
record 0225), which no page reads yet.

## Scenarios

- S1: Given connection rollups of a source, when the signed-in user chooses *Network* in the navigation (between *Processes* and
  *Logs*), then `/network` shows for the last 24 hours the stacked chart *Connections per state*: one band per TCP state (`tcp` and
  `tcp6` together) and one band `UDP` (`udp` and `udp6`), each state the range's rollups name, in the ordinal order of its name from
  the bottom, so the top edge is the average of every socket the snapshots counted; the vertical axis reads `Sockets`.
- S2: Below it the stacked chart *Sockets held per program* shows the 10 programs (the processes' `command`) with the highest average
  over the range, the highest at the bottom, and `Other programs` on top (every other program, those the fold cut or capped included). A program's value is the sum over its processes of the
  sockets each process holds, as the snapshot's `processes` list counts them: a socket counts once **per holding process**, also
  within one program. The page notes `Each process counts every socket it holds, so a socket shared by several processes (a server's
  master and its workers) counts once for each, and the stack can exceed the sockets per state; sockets without an owner (such as
  TIME_WAIT) are not in this chart.`
- S3: Both charts follow the processes view's step, step text, legend, colors, drag selection and click ([Processes view](../../docs/areas/web-ui-processes-view.md), *Charts*).
- S4: Below the charts the table *Top remote addresses* lists for the range the 20 remote addresses with the highest average
  (columns *Address*, *Average connections* with one decimal, *Highest in one snapshot*), by average descending, then address; when
  the rollups hold further addresses it ends with `and <n> more addresses`. It notes `Each snapshot counts only its 25 most frequent
  remote addresses and the rollups keep only the most frequent ones; an address outside them counts 0 there.`
  Addresses only: regular snapshots carry no remote ports ([Agent connections](../../docs/areas/agent-connections.md)).
- S5: Below it the panel *Listeners* shows the listener inventory of the source's latest connection snapshot captured in the range
  (`from` up to its end; an older one is never shown, E6), headed `Listeners of <time> UTC (<source>)`; with a moment `at` (a click in a chart), the source's snapshot closest to the
  moment within 10 minutes, with the history view's choice of snapshot. A table with the columns *State* (`new` or empty),
  *Protocol*, *Endpoint* (local address and port), *Owner* (`<PID> <command>` or `no owner`) and *Socket UID* (`<uid> <user>`, `<uid>`
  without a user name, `unknown` without a UID, ending ` (lowest of several)` when mixed), one row per listener in the snapshot's order
  (ports below 1024 first). At most 200 rows, then `and <n> more listeners`.
- S6: The panel marks a listener `new` when the **comparison snapshot**, the source's latest connection snapshot captured from 48 up to 24
  hours before the shown one (an older one is never used, E7), lacks its key, with the agent's rules: protocol, local address and port; an unconnected UDP socket on a port
  from 32768 to 60999 is never new; only ports 1 to 1023 are compared when either inventory holds only those
  ([Listener changes](../../docs/areas/listener-changes.md), *Comparison across agent starts*). New listeners stand first, in snapshot
  order; above the table: `<n> new listeners since <time> UTC.` (`1 new listener since …` for one) or `No new listener since <time> UTC.`, the comparison's time. Both
  snapshots are picked by capture time, so after the agent's clock stepped another snapshot may be picked; the line names its time.
- S7: The address holds `source`, `range`, `from`, `to` and `at` with the processes view's rules, order and errors (no `compare`), so
  the page can be bookmarked and used with back and forward; presets, *From (UTC)* and *To (UTC)* with *Apply* and a drag write a new
  entry, a click replaces it. The source choice lists the sources with connection data in the range (the agent's `connections`, an
  import's `legacy-monitor`), at most 256; the default is the source with the latest data, the first listed on a tie.
- S8: Given a range that reaches back beyond `retention.snapshots` (*1 y*), then the charts and the table show the whole range as far
  as the connection rollups are kept (`retention.rollups_1h`); the panel *Listeners* needs the snapshots themselves and shows E6 where
  none is kept.

## Errors and edge cases

- E1: No connection data of the source in the range: the charts, the table and their notes are left out and the page says `No
  connection data is stored in this range.`
- E2: A step without a folded snapshot of the source (agent offline, collector failing) is a gap: every band breaks there.
- E3: A range starting before the oldest stored connection data of the source: `This range starts before the oldest stored connection
  data of this source.`
- E4: A snapshot in the range with `aggregates_incomplete`: `Some snapshots in this range may not cover every socket.`
- E5: Snapshots of the source from before the end of the range not folded yet: the processes view's `Values after <time> UTC are not
  complete yet and may still change.`
- E6: No connection snapshot for the panel: `No connection snapshot was stored within 10 minutes of this moment.` (with `at`) or `No
  connection snapshot of this source is stored in this range.`
- E7: No comparison snapshot in its window: `No connection snapshot of this source is stored from 24 to 48 hours before, so no
  listener is marked new.`
- E8: Inventories that cannot be compared, by the outcomes of [Listener changes](../../docs/areas/listener-changes.md): `No listener is
  marked new: the inventories are not comparable.` (a skipped line, different tables) or `No listener is marked new: ports 1 to 1023
  are not complete.`; for a comparison of the privileged ports only, the line of S6 ends `(ports 1 to 1023 only)`.
- E9: The shown or the comparison snapshot is one of an import or of a 1.0 agent (no `inventory`): the shown snapshot's listeners are shown and nothing is marked, with `Listeners of
  imported or older snapshots are not compared.`
- E10: A shown snapshot whose list holds only the privileged ports (`inventory` `privileged`): `This snapshot lists only the listeners
  on ports 1 to 1023.`
- E11: An invalid, repeated or contradicting address parameter, or a range in the future: the processes view's texts, naming the
  parameter, never its value; only the controls and the text are shown and nothing is read.
- E12: A read past 10 seconds: `Reading the connections took longer than 10 seconds and was stopped. Narrow the range.`; a store error
  or refused read: `The connections could not be read. The service log has the details.`, logged without an address value, source,
  command, user or remote address. A failed table or panel read shows them in its place, the charts kept.
- E14: A step whose snapshots lost an address to a bucket's cap: `Some periods in this range held more remote addresses than the
  rollups keep, so an address can read lower there and the count of further addresses is a lower bound.`
- E15: The panel shows the snapshots of the source the charts show (the address's `source`, else the default of S7) and is read again
  when the source, the range or `at` changes. Without `source` it says `Loading…` until the charts chose one, `No source has connection
  data in this range, so no snapshot is shown.` when none is listed (E1), `The source could not be read, so no snapshot is shown.`
  when the chart read failed (E12); a range in the future or an address error shows no panel (E11).
- E13: Hostile commands and user names (up to 1,024 bytes, control characters included) are external text; a chart label is escaped
  visibly and capped at 64 characters. One IPv4 peer reached over an IPv4 and an IPv6 socket is two addresses
  (`::ffff:` form kept).

## Non-functional requirements

- N1: The page is behind the login and served on the web listener only ([Web login](../../docs/areas/web-login.md)).
- N2: Every store call takes a slot of the views' shared read gate; every action runs under a 10-second deadline, the wait included;
  leaving the page cancels its reads ([Web UI](../../docs/areas/web-ui.md), *Store reads of the views*).
- N3: Memory per circuit and store work per action are bounded by stated caps (at most 13 state lines and 11 program lines of at most 1,000 steps,
  20 remote addresses shown, 256 sources, 200 listener rows, two decoded snapshots for the panel), not by the number of snapshots in the range;
  the area document states the caps and the memory derived from them.
- N4: The chart action (sources, states and programs) and the separate address action (*Top remote addresses*) of a 30-day, a 1-year
  and a 1,095-day range, over one source with 1,095 days of hourly buckets at the trimmed size and 30 days of 5-minute buckets, each
  finish within the 10-second deadline on the reference host (measured by the plan; #486: 4,251 and 8,022 ms for 1,095 days).
- N5: No new configuration option; the charts and the table never read a snapshot, only the connection rollups.

## Acceptance criteria

- AC1: `/network` shows *Connections per state* with one band per TCP state and `UDP` (S1, S8).
- AC2: The page shows *Sockets held per program* of the top 10 programs plus `Other programs`, by the measure of S2, with its note
  (S2, S8).
- AC3: Step, step text, legend, colors, drag and click follow the processes view (S3).
- AC4: *Top remote addresses* lists the 20 addresses with the highest average count in the range, with the highest count, the order, the
  left-out line and the notes (S4, E13, E14).
- AC5: The panel *Listeners* shows the inventory of the latest snapshot in the range, or closest to `at` within 10
  minutes, with its rows, cap and texts, of the charts' source, or a text of E6 or E15 (S5, E6, E10, E15).
- AC6: New listeners since the previous day are highlighted: a listener the comparison snapshot lacks is marked `new` and listed first,
  with the line of S6, by the agent's key and port rules (S6, E7, E8, E9).
- AC7: The address, controls and source choice follow S7 with the processes view's rules and errors (S7, E11).
- AC8: A step without a folded snapshot breaks every band, and the notes of E1, E3, E4 and E5 appear exactly when the read calls for them (E1,
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

- Q1: How far back do the charts and *Top remote addresses* reach? Answered: (Product Manager, 2026-11-23) option A, rollups first:
  #486 (PR #490, record 0225) built them, and this view reads them back to `retention.rollups_1h`. S1 to S4, N3 and N4 were checked
  against [Connection rollups](../../docs/areas/connection-rollups.md) and aligned (state order, address cut, cap and trim, E14).
- Q2: What does "new since the previous day" compare? Answered: (Lead) two inventories 24 to 48 hours apart (S6); a listener that
  opened and closed in between is the report's (*Out of scope*).
- Q3: What is a "remote endpoint"? Answered: (Lead) a remote address, the unit regular snapshots count (S4).
- Q4: Does the issue's .NET 10 matter? Answered: no; the backend is on .NET 11 (record 0197).

## Area documents

- `docs/areas/web-ui-network-view.md` (new): the page `/network`, its address, controls, the charts *Connections per state* and *Sockets held per program*, the remote-address table, the
  panel *Listeners* with the new-listener marking, notes, calls, deadline, caps and memory per circuit.
- `docs/areas/web-ui.md`: the navigation entry, the list of views, the gate's list of views.
- `docs/areas/listener-changes.md`: the network view as a second place that shows new listeners, by inventory comparison, in *Scope* and
  *Not here*; `docs/areas/storage-reading.md`: the read of the latest snapshot of a source in a range;
  `docs/areas/connection-rollups.md`: the network view as the reader of the rollups, in *Reading* and *Not here*.

## Challenge

- M1 (per-program measure counts a socket once per holding process): accepted. Checked in `internal/connstats/owners.go`
  (`perPID[pid]++` for every holder) and `processList`: a regular snapshot has no per-socket owner set, so distinct sockets per program
  cannot be derived. S2 keeps the measure the snapshot supports and names it exactly (sum over the program's processes, once per holding
  process, also within a program), the chart is renamed *Sockets held per program*, the note says the stack can exceed the sockets per
  state; AC2 checks the measure; the rollup issue states it.
- 2 (panel snapshot without lower bound): accepted. S5 shows only a snapshot captured in the range, else E6; AC5 follows.
- 3 (comparison window unbounded, by capture time): accepted. S6 takes the latest snapshot from 48 up to 24 hours before the shown one,
  else E7, and says capture time decides and a stepped clock can pick another snapshot. Planning note: that window is one day, which the
  snapshot stamp read allows (*at or before* 24 hours before, window one day); the panel's latest snapshot in a range longer than a day
  needs a read beyond that bound, which the plan names.
- 4 (E9 for either snapshot): accepted. E9 applies when the shown or the comparison snapshot lacks `inventory`.
- 5 (rollup issue must carry S1 to S4's needs): accepted. The rollup issue lists min, average and max per step per state and per
  program, per address the average and the highest count with an address outside a snapshot's top 25 counted 0, and the incomplete
  count; before this plan is written S1 to S4 are checked against the rollups' area document once that feature is merged (Q1).
- Plan challenge 1 (1,095 days past one deadline): accepted; the table reads in its own action (N4, E12).
