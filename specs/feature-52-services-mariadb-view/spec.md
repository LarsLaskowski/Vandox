# Spec: Services and MariaDB view

Source: Issue #52
Status: Draft

## Problem

The administrator of the monitored server wants to see at a glance how available each tracked service and MariaDB were over a month
or longer, how often each service restarted, whether MariaDB's configured memory can exceed the RAM, and which queries ran long. Today
the overview shows only the latest state, restart counter and memory figure ([Overview page](../../docs/areas/web-ui-overview.md)); the
history view marks service restarts and charts single `mariadb_*` metrics, but gives no count per service, no availability, no memory
budget and no thread snapshot ([History view](../../docs/areas/web-ui-history-view.md)). The data is stored: `service_state` records
for `retention.events` (1,095 days by default), the `mariadb_*` metrics with their rollups, and `mariadb_status` records (availability,
status values, thread snapshots) for `retention.snapshots` (30 days by default; [Retention and rollups](../../docs/areas/retention.md)).

## Scenarios

- S1: Given a signed-in user, when they choose *Services* in the navigation, then the page `/services` with the heading `Services and
  MariaDB` shows the last 30 days: the section *Services* with one row per unit and the section *MariaDB* with its availability, the
  memory budget, three charts and the long-running queries.
- S2: Given `nginx.service` was `failed` for 2 hours of the range and recorded throughout, when the page shows the range, then its row
  reads *Available* `99.7 %`, *Not available* `2 h 0 min` and *Unknown* `0 s`.
- S3: Given `php-fpm.service` restarted three times in the range (record 0215's rule), when the page shows the range, then its row reads
  *Restarts* `3` with the time of the latest, and the list *Service restarts* names the three with time and unit.
- S4: Given MariaDB was `down` for 10 minutes of the last 24 hours, when the user picks *24 h*, then the line above the chart reads
  `Up 99.3 %, down 0.7 %, not answering 0.0 %, unknown 0.0 % of this range.` and the chart *Availability* drops in those steps.
- S5: Given the theoretical maximum (`mariadb_memory_max_bytes`) is 6.0 GiB and the RAM (`mem_total_bytes`) 4.0 GiB, when the page shows
  the range, then *Memory budget* reads `In use 1.2 GiB of a theoretical maximum of 6.0 GiB; RAM 4.0 GiB.`, adds `The theoretical maximum
  exceeds the RAM by 2.0 GiB.`, and its chart draws the lines `Theoretical maximum`, `RAM` and `In use` on one scale from 0.
- S6: Given a year of data, when the user picks *1 y*, then the services and the charts of the metrics cover the year, and the lines
  read from status records (availability, `In use`, *Connections*) begin at the oldest stored status record, with the note of E7.
- S7: Given the agent recorded threads at 03:12:40, when the user chooses that time in the list *Snapshots* or clicks a chart within 10
  minutes of it, then the panel `Threads of <time> UTC` lists the threads that were not sleeping, longest running first, with their
  statements.

## Errors and edge cases

- E1: An invalid address shows the heading, the range controls and the history view's error text, and reads nothing.
- E2: A range that begins at or after now shows `This range lies in the future.` and reads nothing.
- E3: The agent was offline or the `systemd` collector failed: time inside a recorded gap whose collector is `systemd` or none, and time
  more than 1 hour 5 minutes after a unit's record without a next one, counts as *Unknown*, never as available (record 0028). The same
  holds for MariaDB with the collector `mariadb` and 2 minutes.
- E4: A unit without a record before the range counts as *Unknown* until its first record in the range; a unit without any known time
  reads *Available* `unknown`.
- E5: The restart read reached its cap or could not decide a restart (the time-series query's boot rule): the list and the counts end
  with `Restarts after <time> UTC are not counted.`
- E6: No `service_state` record of the range: `No service state is stored in this range.`; no MariaDB status or metric in the range (the
  collector disabled): `No MariaDB data is stored in this range.`, in place of the section's content.
- E7: The range starts before the oldest stored status record: `MariaDB's status is kept for a shorter period; availability, memory in
  use and connections begin at <time> UTC.`
- E8: A figure of the memory budget the range lacks reads `unknown`, never 0, and the comparison with the RAM is left out.
- E9: Steps without a value (MariaDB not `up`, a gap, no record) are gaps in the charts, never drawn across; the time-series query's
  notes (rollups, values not complete) appear above the charts as in the history view.
- E10: No thread snapshot in the range: `No thread snapshot is stored in this range.`; with `at`, none within 10 minutes: `No thread
  snapshot was stored within 10 minutes of this moment.`
- E11: Thread texts and unit names are external text: control and direction characters are written as visible escapes, markup is shown
  as text, a statement is capped at 2,048 characters.
- E12: A section's read passes its 10-second deadline: `Reading the services took longer than 10 seconds and was stopped. Narrow the
  range.`, `Reading MariaDB's data took longer than 10 seconds and was stopped. Narrow the range.` or `Reading the thread snapshot took
  longer than 10 seconds and was stopped.`
- E13: A store error: `The services could not be read. The service log has the details.`, `MariaDB's data could not be read. The
  service log has the details.` or `The thread snapshot could not be read. The service log has the details.`; each fails only its own
  section.
- E14: More than 256 units: the first 256 in the table's order, then `and <n> more units`; more than 100 running threads: `and <n> more
  threads`; more than 50 snapshots in the range: `Older snapshots are not listed; narrow the range.`

## Non-functional requirements

- N1: The page is behind the login and on the web listener only; it only reads. No statement, user, host, database or unit name reaches a
  log line: a failure is logged as `services view: reading failed` (error level) with the exception only.
- N2: Every store call takes one slot of the views' shared read gate; each section's action runs under a 10-second deadline that
  includes the wait for a slot, and leaving the page cancels all actions.
- N3: On the reference host and the benchmark database ([Benchmarks](../../docs/BENCHMARKS.md)), the services and the MariaDB actions of
  a 30-day and of a 1-year range finish within the deadline, measured with the storage benchmark tool.
- N4: The memory per circuit is bounded by the caps (256 units, 1,000 restarts, at most 1,000 steps per chart line, 50 snapshots, 100
  thread rows, one decoded status record) and never grows with the number of stored records in the range.
- N5: No agent, wire format, configuration or schema change beyond indexes; existing databases are read as they are.

## Acceptance criteria

- AC1: `/services` is behind the login, the navigation entry *Services* lies between *Network* and *Logs*, prerendered it shows the
  heading, the range controls and `Loading…` and reads nothing; the address takes `range`, `from`, `to` and `at` with the history view's
  rules and texts, defaults to the last 30 days with *30 d* marked, and a drag in a chart sets the range, a click sets `at` (S1, E1, E2, N1).
- AC2: The table *Services* has one row per unit with a record in the range or a state carried into it, with the columns *Unit*,
  *Available*, *Not available*, *Unknown*, *Restarts*, *Last restart* and *Latest state* (`<active_state> (<sub_state>)` with its time);
  rows with restarts or not-available time come first, by restarts and then not-available time descending, then by unit in ordinal order;
  at most 256 (S1, E6, E14).
- AC3: A unit's state holds from its record to its next record; *Available* is the share of the known time in `active`, `reloading` or
  `refreshing` with one decimal, *Not available* the known time in any other state, and *Unknown* the rest of the range by E3 and E4;
  the three durations add up to the range up to now (S2, E3, E4).
- AC4: *Restarts* counts each unit's restarts in the range by record 0215's rule, the same as the history view's markers; *Last
  restart* is the latest or `none`; the list *Service restarts* holds time and unit, in time order, at most 1,000, with E5's text when
  incomplete (S3, E5).
- AC5: *MariaDB* shows the summary line of S4 and the chart *Availability* (`Up` in percent of each step's known time) from the status
  records, with E3's unknown rule, E6 and E7 (S4, S6, E6, E7).
- AC6: *Memory budget* shows the line of S5 from the latest values of the range, `The theoretical maximum exceeds the RAM by <size>.`
  exactly when the maximum is above the RAM, and a chart of `Theoretical maximum` (`mariadb_memory_max_bytes`), `RAM` (`mem_total_bytes`
  of source `system`) and `In use` (the status value `Memory_used`, as on the overview) on one scale from 0 (S5, E8, E9).
- AC7: The charts *Connections* (`Threads_connected`), *Queries per second* (`mariadb_questions_per_second`) and *InnoDB buffer pool*
  (`Used` and `Read hit`, percent from 0 to 100) share the range and time axis of the other MariaDB charts, with the history chart's
  legend, step text, gaps and notes (S6, E9).
- AC8: *Long-running queries* states `The agent records MariaDB's threads only in a run in which more than 10 threads are running or the
  server is under memory pressure.`, lists the range's snapshots newest first (*Time*, *Threads*, *Running*, *Longest running*; at most
  50), and the panel shows the latest snapshot of the range, or with `at` the closest within 10 minutes by the history view's rule: the
  threads whose command is not `Sleep` in the agent's order (*Id*, *User*, *Host*, *Database*, *Command*, *State*, *Time*, *Statement*;
  at most 100), `[statement shortened by the agent]` where `truncated` is set, and `<n> sleeping connections are not listed.` (S7, E10,
  E11, E14).
- AC9: The three sections read in separate actions under the shared gate and the 10-second deadline, show E12 and E13 in their own
  place, log only as N1 says, and keep their memory within N4's caps (E12, E13, N1, N2, N4).
- AC10: The benchmark of N3 is run and recorded, each action within the deadline (N3, N5).

## Out of scope

- Recording threads when one query runs long on a quiet server: the agent's trigger stays as it is (agent and security area 7; a
  follow-up issue).
- Status values (availability, `Memory_used`, `Threads_connected`) beyond `retention.snapshots`: they are no metrics and have no rollups
  (a follow-up issue).
- Exposed listeners, slow queries, aborted connections and the other selected status values; event markers on the charts (the history
  view charts every metric with its markers); a chart per service; alerts; automatic refresh; selection by touch; ending a query.

## Open questions

- Which memory is "actual"? Answered: MariaDB's status value `Memory_used`, which the overview already shows as *MariaDB memory*
  (`OverviewQueries.MariaDbStatusRecord`); the server process's RSS or PSS is not used.
- How far back do the MariaDB figures reach? Answered: the metrics as far as their rollups (`retention.rollups_1h`), the status values
  as far as `retention.snapshots` ([Retention and rollups](../../docs/areas/retention.md)); E7 says so.
- What is a restart? Answered: record 0215, as computed for the history view's markers.
- When does a thread snapshot exist? Answered: only for runs with `Threads_running` above 10 or in memory pressure mode
  ([Agent MariaDB collector](../../docs/areas/agent-mariadb.md), *What a run does*); AC8 says so on the page.
- One page or two? Answered: the Lead's choice, one page, as the issue names one view; MariaDB is one of the tracked services.

## Area documents

- `docs/areas/web-ui-services-view.md` (new): the page, address, controls, the three sections with their texts and rules (availability,
  unknown time, restarts, memory budget, charts, thread snapshots), calls and deadline, caps and memory per circuit, measured time.
- `docs/areas/web-ui.md`: the navigation entry, the view in the list of the shared read gate, the chart uses.
- `docs/areas/storage-reading.md`: the new reads (service states over a range, status steps, snapshot list and record) with their limits.
- `docs/areas/time-series-query.md`: only a pointer, if the restart rule becomes a helper both use.
- `docs/areas/README.md`: the index row of the new document.
