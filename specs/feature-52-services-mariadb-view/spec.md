# Spec: Services and MariaDB view

Source: Issue #52
Status: Approved by Lead

## Problem

The administrator of the monitored server wants to see at a glance how available each tracked service and MariaDB were over a month
or longer, how often each service restarted, whether MariaDB's memory can exceed the RAM, and which queries ran long. Today
the overview shows only latest values and the history view marks restarts and charts single metrics, with no count per service,
availability, memory budget or thread snapshot. Stored are `service_state` records
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
  `Up 99.3 %, down 0.6 %, not answering 0.0 %, unknown 0.0 % of this range.` and the chart *Availability* drops in those steps.
- S5: Given the theoretical maximum (`mariadb_memory_max_bytes`) is 6.0 GiB and the RAM (`mem_total_bytes`) 4.0 GiB, when the page shows
  the range, then *Memory budget* reads `In use 1.2 GiB of a theoretical maximum of 6.0 GiB; RAM 4.0 GiB.`, adds `The theoretical maximum
  exceeds the RAM by 2.0 GiB.`, and its chart draws the lines `Theoretical maximum`, `RAM` and `In use` on one scale in GiB.
- S6: Given a year of data, when the user picks *1 y*, then the services and the charts of the metrics cover the year, and the lines
  read from status records (availability, `In use`, *Connections*) begin at the oldest stored status record, with the note of E7.
- S7: Given the agent recorded threads at 03:12:40, when the user chooses that time in the list *Snapshots* or clicks a chart within 10
  minutes of it, then the panel `Threads of <time> UTC` lists the threads that were not sleeping, longest running first, with their
  statements.

## Errors and edge cases

- E1: An invalid address shows the heading, the controls and the history view's error text, and reads nothing.
- E2: A range beginning at or after now shows `This range lies in the future.` and reads nothing.
- E3: The agent was offline or the `systemd` collector failed: time inside a recorded gap whose collector is `systemd`, none or one the time-series query leaves unnamed, and time
  more than 1 hour 5 minutes after a unit's record without a next one, counts as *Unknown*, never as available (record 0028). The same
  holds for MariaDB with the collector `mariadb` and 2 minutes. A gap of the `systemd` collector concerns every unit, also the gap the
  collector writes on every run while it refuses one unit's state ([Agent services](../../docs/areas/agent-services.md)), so whenever
  such a gap overlaps the range the table is preceded by `The systemd collector recorded gaps in this range (it failed, or it refused a
  unit's state); that time counts as unknown for every unit.`
- E4: A unit without a record before the range counts as *Unknown* until its first record in the range; a unit without any known time
  reads *Available* `unknown`.
- E5: The restart read reached its cap or could not decide a restart (the time-series query's boot rule): the list and the counts end
  with `Restarts after <time> UTC are not counted.`
- E6: No `service_state` record of the range: `No service state is stored in this range.`; no MariaDB status or metric in the range (the
  collector disabled): `No MariaDB data is stored in this range.`, in place of the section's content.
- E7: Only when `retention.snapshots` is not `unlimited` and now minus that period lies after the range's start: `MariaDB's status is
  kept for <period>; availability, memory in use and connections begin at <time> UTC.` with the oldest status record's time.
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
- E14: More than 256 distinct units in the range: only the first 256 in ordinal order of
  their names are evaluated and ordered, then `<n> more units are not evaluated.`; more than 100 running threads: `and <n> more
  threads`; more than 50 snapshots in the range: `Older snapshots are not listed; narrow the range.`
- E15: More than 1,000 gap spans concern a section's collector in the range (touching or overlapping gaps form one span): the first
  1,000 by start are read, all time after the last one read counts as unknown, and the section says `Gaps after <time> UTC are not read; the time after it counts as unknown.`

## Non-functional requirements

- N1: The page is behind the login and on the web listener only; it only reads. No statement, user, host, database or unit name reaches a
  log line: a failure is logged as `services view: reading failed` (error level) with the exception only.
- N2: Every store call takes one slot of the views' shared read gate; each section's action runs under a 10-second deadline that
  includes the wait for a slot, and leaving the page cancels all actions.
- N3: On the reference host, the services, MariaDB and thread snapshot actions of a 30-day and of a 1-year range finish within the
  deadline, measured with `tools/Vandox.StorageBenchmark` on the benchmark database ([Benchmarks](../../docs/BENCHMARKS.md)) extended by a
  new MariaDB workload: 30 days of `mariadb_status` records at 30 seconds (86,400 records), of which one in 100 carries a thread
  snapshot close to the 1 MiB record bound, beside `service_state` records of 256 units (the agent's cap) over a year and the metric workloads.
- N4: The memory per circuit is bounded by the caps (256 evaluated units, 1,000 restarts, 1,000 gap spans per section, at most 1,000 steps
  per chart line, 50 snapshots, 100 thread rows, one decoded status record) and never grows with the number of stored records in the range.
- N5: No agent, wire format or configuration change; a new index (with its migration step) is allowed where the measurement of N3 needs
  one, otherwise existing databases are read as they are.

## Acceptance criteria

- AC1: `/services` is behind the login, the navigation entry *Services* lies between *Network* and *Logs*, prerendered it shows the
  heading, the range controls and `Loading…` and reads nothing; the address takes `range`, `from`, `to` and `at` with the history view's
  rules and texts, defaults to the last 30 days with *30 d* marked, and a drag in a chart sets the range, a click sets `at` (S1, E1, E2, N1).
- AC2: The table *Services* has one row per unit with a record in the range or a state carried into it, with the columns *Unit*,
  *Available*, *Not available*, *Unknown*, *Restarts*, *Last restart* and *Latest state* (`<active_state> (<sub_state>)` with its time);
  rows with restarts or not-available time come first, by restarts and then not-available time descending, then by unit in ordinal order;
  at most 256 evaluated units by E14 (S1, E6, E14).
- AC3: A unit's state holds from its record to its next record; *Available* is the share of the known time in `active`, `reloading` or
  `refreshing`, rounded down to one decimal (so `100.0 %` only without time not available), *Not available* the known time in any other state, and *Unknown* the rest of the range by E3 and E4;
  the three durations add up to the range up to now; a unit's gaps follow E15 (S2, E3, E4, E15).
- AC4: *Restarts* counts each unit's restarts in the range by record 0215's rule, the same as the history view's markers; *Last
  restart* is the latest or `none`; the list *Service restarts* holds time and unit, in time order, at most 1,000, with E5's text when
  incomplete (S3, E5).
- AC5: *MariaDB* shows the summary line of S4 and the chart *Availability* (`Up` in percent of each step's known time) from the status
  records, the shares rounded down to one decimal, with E3's unknown rule, E6, E7 and E15 (S4, S6, E6, E7, E15).
- AC6: *Memory budget* shows the line of S5 from the latest values of the range, `The theoretical maximum exceeds the RAM by <size>.`
  exactly when the maximum is above the RAM, and a chart of `Theoretical maximum` (`mariadb_memory_max_bytes`), `RAM` (`mem_total_bytes`
  of source `system`) and `In use` (the status value `Memory_used`, as on the overview) on one scale in GiB (S5, E8, E9).
- AC7: The charts *Connections* (`Threads_connected`), *Queries per second* (`mariadb_questions_per_second`) and *InnoDB buffer pool*
  (`Used` and `Read hit`, in percent) share the range and time axis of the other MariaDB charts, with the history chart's
  legend, step text, gaps and notes (S6, E9).
- AC8: *Long-running queries* states `The agent records MariaDB's threads only in a run in which more than 10 threads are running or the
  server is under memory pressure.`, lists the range's snapshots newest first (*Time*, *Threads recorded*, *Running*, *Longest running*;
  at most 50; the agent keeps at most 4,096 threads and may drop sleepers to fit its record, so the counts are of recorded threads), and the panel shows the latest snapshot of the range, or with `at` the closest within 10 minutes by the history view's rule: the
  threads whose command is not `Sleep` in the agent's order (*Id*, *User*, *Host*, *Database*, *Command*, *State*, *Time*, *Statement*;
  at most 100), `[statement shortened by the agent]` where `truncated` is set, and `<n> sleeping connections recorded, not listed.` (S7, E10,
  E11, E14).
- AC9: The three sections read in separate actions under the shared gate and the 10-second deadline, show E12 and E13 in their own
  place, log only as N1 says, and keep their memory within N4's caps (E12, E13, N1, N2, N4).
- AC10: The MariaDB workload of N3 is added to the benchmark tool and `docs/BENCHMARKS.md`, and the services, MariaDB and thread
  snapshot actions of a 30-day and a 1-year range are timed on it, each within the deadline; an index added for it is named there (N3,
  N5).

## Out of scope

- Recording threads when one query runs long on a quiet server: the agent's trigger stays as it is (agent and security area 7; a
  follow-up issue).
- Status values (availability, `Memory_used`, `Threads_connected`) beyond `retention.snapshots`: they are no metrics and have no rollups
  (a follow-up issue).
- Other status values and metrics, event markers on the charts (the history view has both), a chart per service, alerts, automatic
  refresh, selection by touch, ending a query.

## Open questions

- Which memory is "actual"? Answered: MariaDB's status value `Memory_used`, which the overview already shows as *MariaDB memory*
  (`OverviewQueries.MariaDbStatusRecord`); the server process's RSS or PSS is not used.
- How far back do the MariaDB figures reach? Answered: the metrics as far as their rollups (`retention.rollups_1h`), the status values
  as far as `retention.snapshots` ([Retention and rollups](../../docs/areas/retention.md)); E7 says so.
- What is a restart? Answered: record 0215, as computed for the history view's markers.
- When does a thread snapshot exist? Answered: only for runs with `Threads_running` above 10 or in memory pressure mode
  ([Agent MariaDB collector](../../docs/areas/agent-mariadb.md), *What a run does*); AC8 says so on the page.

## Area documents

- `docs/areas/web-ui-services-view.md` (new): the page, address, controls, the three sections with their texts and rules (availability,
  unknown time, restarts, memory budget, charts, thread snapshots), calls and deadline, caps and memory per circuit, measured time.
- `docs/areas/web-ui.md`: the navigation entry, the view in the list of the shared read gate, the chart uses.
- `docs/areas/storage-reading.md`: the new reads (service states over a range, status steps, snapshot list and record) with their limits.
- `docs/areas/time-series-query.md`: only a pointer, if the restart rule becomes a helper both use.
- `docs/areas/README.md`: the index row of the new document.

## Challenge

- MAJOR 1 (N3/AC10 can pass without timing the status reads) — accepted: N3 names the new MariaDB workload (86,400 status records of 30
  days at 30 seconds, one in 100 with a thread snapshot near 1 MiB), AC10 adds it to the benchmark tool and times the MariaDB and thread
  snapshot actions on it, and N5 allows an index with its migration step when the measurement needs one.
- MAJOR 2 (the 256 cap cannot bound memory) — accepted: E14 evaluates only the first 256 distinct units in ordinal order of their names
  and says `<n> more units are not evaluated.`; the ordering of AC2 applies to those; N4 names the cap.
- MINOR 3 (gaps uncapped) — accepted: E15 reads at most 1,000 gaps per section, counts all time after the last one read as unknown and
  says so; N4 names the cap.
- MINOR 4 (one refused unit blanks the table) — accepted: E3 states that a `systemd` gap concerns every unit, also the one written while
  a unit's state is refused, and the page shows a note whenever such a gap overlaps the range. The gap does not tell a refusal from a
  failure, so the note names both.
- MINOR 5 (E7 on a fresh install) — accepted: E7 appears only when `retention.snapshots` is limited and its cut lies after the range's
  start, and names the period.
- MINOR 6 (rounding, recorded threads) — accepted: shares are rounded down to one decimal in AC3 and AC5; AC8 names the column
  *Threads recorded*, explains the agent's caps and reads `<n> sleeping connections recorded, not listed.`

Spec fixes while planning (Lead): S4's down share follows AC5's rounding down (0.6 %, not 0.7 %); S5, AC6 and AC7 no longer promise a
scale from 0, because the history chart fits a scale to its lines outside its stacked mode (`wwwroot/js/charts.js`, `historyScales`);
E3 names the collectors the time-series query leaves unnamed, so that no gap is hidden.
