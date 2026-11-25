# Services view

## Scope

The page `/services` of the [web UI](web-ui.md): the address with the range and the moment, the table of the tracked services with their
availability and restarts, MariaDB's availability, memory budget and charts, and the thread snapshots that the agent records, with their
notes, calls, the 10-second deadline, and the caps and memory per circuit. The administrator relies on it to see how available each service
and MariaDB were over a month or longer, how often each service restarted, whether MariaDB's memory can exceed the RAM, and which queries
ran long. The restarts come from the time-series query's rule ([Time-series queries](time-series-query.md), *Markers*); the reads are in
[Storage reading](storage-reading.md), *Services view reads*. The overview shows only the latest states ([Overview page](web-ui-overview.md)).

## Behavior

### Page

- The page is behind the login ([Web login](web-login.md)) and is served on the web listener only; it only reads. The navigation entry
  *Services* (`services`) lies between *Network* and *Logs* and is marked on `/services` with or without a query. The heading is
  `Services and MariaDB`.
- Prerendered (the first response) the page shows the heading, the range controls and `Loading…` and reads nothing; it reads once its
  live connection is up ([Web UI](web-ui.md), *Pages and render mode*). An address with an error is shown the same way.
- Top to bottom: the range controls; the sections *Services* (the notes, the table *Services*, the line `<n> more units are not
  evaluated.`, and `Service restarts` with the table *Time*, *Unit*), *MariaDB* (the notes, the summary line, then the headings
  *Availability*, *Memory budget* with its line or lines, *Connections*, *Queries per second* and *InnoDB buffer pool*, each with its chart)
  and *Long-running queries* (the agent's note, the list *Snapshots*, the panel `Threads of <time> UTC`). Each section shows `Loading…`
  until its own read ends.
- A link of the view is `services` plus the query of a validated address. The page builds no other link.

### Address

Everything the page shows is in the address, so it can be bookmarked, shared and used with the browser's back and forward buttons.

| Parameter | Value |
| --------- | ----- |
| `range`, `from`, `to`, `at` | as in the [History view](web-ui-history-view.md), *Address*: `1h`, `24h`, `7d`, `30d` or `1y`; times as `yyyy-MM-ddTHH:mm[:ss]` in UTC; `at` is the moment |

- Any other key is ignored. Keys are matched, percent-encoded and refused when repeated or invalid as in the history view, with its texts
  and its range rules, naming the parameter, never its value; an address with an error shows the heading, the controls and the text, and
  nothing else, and reads nothing. A range that begins at or after now shows `This range lies in the future.` and reads nothing.
- Without `range`, `from` and `to` the range is **the last 30 days** and *30 d* is marked; a preset stays relative. (The history view's default
  is 24 hours; the controls take the default of the page.)
- An address is written only from values checked again (the times against the storable range); a value the checks refuse is shown as the
  text of the refusal without navigating.
- A change of the range (the preset, `from` or `to`) reads all three sections again; a change of `at` reads only the thread section.

### Controls

- The presets *1 h*, *24 h*, *7 d*, *30 d*, *1 y* and *From (UTC)*, *To (UTC)* with *Apply*, checked with the history view's rules.
- A preset, *Apply* and a drag selection in a chart write a **new history entry**; a click in a chart, and the time of a snapshot in the list *Snapshots*,
  **replace** the current entry. The click sets `at`, the time under the pointer floored to the whole second; the button of a snapshot sets `at` to its
  capture time floored to the second. The charts draw the moment as a line. A selection keeps `at`.

### Services

- **The table *Services*** has one row per unit that has a `service_state` record in the range or a state carried into it, with the columns
  *Unit*, *Available*, *Not available*, *Unknown*, *Restarts*, *Last restart* and *Latest state*, which reads `<active_state> (<sub_state>)`
  and `<time> UTC` of the record.
- **Order:** by *Restarts* descending, then *Not available* descending, then by unit in ordinal order. At most **256 units** are evaluated (below);
  the order applies to those.
- **Availability** (record [0227](../decisions/0227-availability-is-the-time-a-recorded-state-holds-up-to-a-cap-and-gaps-are-unknown.md)): a unit's state
  holds from its record to its next record of the unit, but at most **1 hour 5 minutes** (the overview's staleness limit; the agent writes a record
  at least hourly). *Available* is the share of the known time in `active`, `reloading` or `refreshing`, rounded **down** to one decimal, so that
  `100.0 %` appears only when no time was not available; *Not available* is the known time in any other state; *Unknown* is the rest of the range up to now.
  The three durations add up to the range up to now. A state before the range holds into it for at most the cap.
- **What is unknown, never available:** time inside a recorded gap whose collector is `systemd`, none, or one the time-series query leaves
  unnamed (and the sequence and time gaps of the agent's stream, which concern every collector); time more than 1 hour 5 minutes after a
  unit's last record without a next one; time before a unit's first record. A unit without any known time reads *Available* `unknown`. A gap of
  the `systemd` collector concerns every unit, also the one the collector writes on every run while it refuses one unit's state
  ([Agent services](agent-services.md)); so whenever such a gap overlaps the range the section starts with `The systemd collector recorded gaps in this range
  (it failed, or it refused a unit's state); that time counts as unknown for every unit.`
- **Gaps are merged, then capped.** The read takes the gaps of its collector alone, as spans clipped to the range, merging overlapping and touching ones
  (a month of per-run gaps is one span). More than **1,000 spans** concern the collector: the first 1,000 by start are read, all time after the last one read
  counts as unknown, and the section says `Gaps after <time> UTC are not read; the time after it counts as unknown.`
- **Restarts** are the time-series query's service restarts of the range ([Time-series queries](time-series-query.md), *Markers*; record
  [0215](../decisions/0215-a-service-restart-is-a-rise-of-nrestarts-or-a-new-active-enter-time-of-a-running-or-failed-unit.md)), the same as the history view's
  markers. *Restarts* counts them per unit, *Last restart* is the latest or `none`; the list *Service restarts* holds the time and the unit, in time
  order, at most **1,000**; it is shown only with a restart. When the read reached its cap or could not decide a restart, the section ends its notes with `Restarts after <time> UTC are not counted.`
- **More than 256 units.** The first 256 distinct unit names in ordinal order are evaluated: a new name above the largest kept is skipped, one below
  it evicts the largest, which never returns. The line `<n> more units are not evaluated.` gives the number of distinct units of the window minus 256.
  The count is a second read that runs only when a name was skipped; see *Limits and errors*.
- Without any unit: `No service state is stored in this range.` in place of the table.

### MariaDB

- **Source of the figures.** The metric lines (`mariadb_memory_max_bytes`, `mem_total_bytes` of the source `system`, `mariadb_questions_per_second`,
  `mariadb_innodb_buffer_pool_used_percent`, `mariadb_innodb_buffer_pool_read_hit_percent`) come from the time-series query at steps only, without markers, so that all
  MariaDB charts share one step axis ([Time-series queries](time-series-query.md)); availability, `Memory_used` and `Threads_connected` come from the
  `mariadb_status` records, folded onto the same steps (record
  [0228](../decisions/0228-mariadb-status-members-are-indexed-and-folded-on-the-time-series-step-axis.md)). The shortest step is 5 minutes.
- **Summary** above the chart: `Up <s>, down <s>, not answering <s>, unknown <s> of this range.`, the shares of the range up to now, each rounded down to one decimal.
  A status record holds from its capture time to the next, at most **2 minutes**; time in a recorded gap of the collector `mariadb` (or of none or an unnamed
  collector), beyond the cap, or with an availability other than `up`, `down` and `not_answering` is unknown.
- ***Availability*** draws the line `Up`: per step the up time as a percent of the step's known time; a step without known time is a gap.
- ***Memory budget*** reads `In use <size> of a theoretical maximum of <size>; RAM <size>.` The first size is the latest `Memory_used` of an `up`
  record of the range (the figure of the overview's *MariaDB memory*), the second the latest value of `mariadb_memory_max_bytes`, the third that of `mem_total_bytes`;
  a figure the range lacks reads `unknown`, never 0. Exactly when the maximum is above the RAM, the line `The theoretical maximum exceeds the RAM by <size>.` follows; with a
  figure unknown the comparison is left out. The chart draws `Theoretical maximum`, `RAM` and `In use` on one scale in GiB. Sizes are as in the overview.
- ***Connections*** (`Threads_connected`, axis `Connections`), ***Queries per second*** (axis `Queries/s`) and ***InnoDB buffer pool*** (`Used` and `Read hit` on one scale, axis `%`) share the
  range and axis, and have the history chart's legend (minimum, average and maximum of a step), step text and gaps. A step without a value (MariaDB not `up`, a gap, no record) is
  a gap in the line, never drawn across. The history chart fits each scale to its lines outside its stacked mode ([Web UI](web-ui.md), *Charts*), so the scales do not start at 0.
- **Retention of the status.** The status records are kept for `retention.snapshots` (30 days by default) and have no rollups, so the availability, `In use` and *Connections* lines
  begin at the oldest status record. Only when `retention.snapshots` is not `unlimited` and now minus that period lies after the range's start the section says `MariaDB's status
  is kept for <period>; availability, memory in use and connections begin at <time> UTC.` with the time of the oldest status record. The metric lines reach as far as their rollups
  ([Retention and rollups](retention.md)). A range of a year therefore says `unknown` for the part before the oldest status record in the summary.
- **Notes**, in this order: the retention text; the gap text of the section (`Gaps after <time> UTC are not read; …`); the time-series query's notes for the metric lines (the range starts before
  the hourly rollups, values not complete up to a time), as in the history view; the step text above the charts.
- Without status records in the range and without a value of a metric of the source `mariadb` (`mem_total_bytes` does not count): `No MariaDB data is stored in this range.` in place of
  the section's content (the collector may be disabled).

### Long-running queries

- The section says `The agent records MariaDB's threads only in a run in which more than 10 threads are running or the server is under memory pressure.` The agent keeps at most 4,096
  threads and may drop sleepers to fit its record ([Agent MariaDB collector](agent-mariadb.md)), so the counts are of **recorded** threads.
- **The list *Snapshots*** holds the status records of the range that carry threads, newest first, at most **50**, with *Time*, *Threads recorded*, *Running* (threads whose command is not `Sleep`) and
  *Longest running* (the greatest `time_seconds` of those; `none` without one). More than 50 in the range: `Older snapshots are not listed; narrow the range.` The *Time* is a button that
  sets `at`.
- **The panel** `Threads of <time> UTC` shows the latest snapshot of the range, or with `at` the one closest to it within 10 minutes (the history view's rule: the latest at or before and the earliest after,
  the nearer, the earlier on a tie). It lists the threads whose command is not `Sleep` in the agent's order, with *Id*, *User*, *Host*, *Database*, *Command*, *State*, *Time* and *Statement*, at most
  **100** rows, and `<n> sleeping connections recorded, not listed.` for the sleeping threads. More than 100 running threads: `and <n> more threads`, where `n` is the snapshot's number of running threads minus 100,
  at least 1 (a **lower bound**: it counts recorded threads). A statement the agent shortened is followed by `[statement shortened by the agent]`.
- A *Time* or *Longest running* is written as a duration (`2 h 0 min`); a value from `3,153,600,000` seconds (100 years) on, which a stored record can only carry when it is hostile or broken, is written
  `36500 d 0 h or more`, so no stored number can fail the formatting.
- No snapshot in the range: `No thread snapshot is stored in this range.`; with `at` and none within 10 minutes: `No thread snapshot was stored within 10 minutes of this moment.`
- **Statements can carry credentials.** They are shown only behind the login, as `deploy/agent/README.md` says of the agent, and never logged.

### External text

Unit names, thread users, hosts, databases, commands, states and statements are external text ([Web UI](web-ui.md), *Display of external text*): control and direction characters are written as visible escapes,
markup is shown as text, a unit name or a thread cell is cut at 256 characters and 16 runs, a statement at 2,048 characters and 256 runs.

### Calls and deadline

- The page runs three actions, each cancelling the running action of its own kind: the **services action** (the restarts of the range up to now, then the availability of the units), the **MariaDB action** (the
  time-series query of the five metrics, then the status steps at the query's step; it starts once the chart width is known) and the **thread action** (the list of snapshots; then the closest snapshot when `at` is
  set; then the snapshot itself: at most three calls). They run beside each other. Leaving the page cancels all.
- Every store call takes one slot of the views' shared read gate ([Web UI](web-ui.md), *Store reads of the views*; `GatedServiceHistoryReader`, `GatedMariaDbHistoryReader`, and the time-series query's gated reader).
  Every action runs under a deadline of **10 seconds**, which includes the wait for a slot.
- A read past the deadline shows `Reading the services took longer than 10 seconds and was stopped. Narrow the range.`, `Reading MariaDB's data took longer than 10 seconds and was stopped. Narrow the range.`
  or `Reading the thread snapshot took longer than 10 seconds and was stopped.`; a store error and a read the store refuses show `The services could not be read. The service log has the details.`,
  `MariaDB's data could not be read. The service log has the details.` or `The thread snapshot could not be read. The service log has the details.` Each action fails only its own section. A failure is logged as
  `services view: reading failed` (event 82, error level) with the exception only, never a unit, user, host, database or statement. A superseded action ends silently.

## Limits and errors

- **Caps.** At most 256 evaluated units; 1,000 restarts; 1,000 merged gap spans per read (services and MariaDB each read their own); 2,001 steps of the status read (the page's chart width allows at most 1,000);
  50 listed snapshots (51 read); 100 thread rows (101 read). The memory of an action does not grow with the number of stored records in the range: the folds hold their per-unit and per-step
  accumulators only, and no stored record is decoded as a whole in C#: the status members come from the index, the thread counts and rows from SQLite's JSON functions.
- **Memory per circuit** is derived from the fields of the types, **not measured**: the page keeps the result of one MariaDB read (at most 1,000 steps of the eight chart lines), 256 table rows
  with at most 1,000 restart rows, 50 snapshot rows and 100 thread rows of statements of at most 2,048 displayed characters (the store holds at most 101 thread texts of at most 16 KiB while it reads them).
  The number of circuits is not bounded ([Web UI](web-ui.md), *Limits and errors*).
- **A temporary b-tree.** Counting the distinct units past 256 keeps a temporary b-tree of the window's unit names in SQLite's temporary storage (`/tmp`, a tmpfs in the container
  ([Backend host](backend-host.md))), bounded by the 10-second interrupt of the read and not by the 256 cap. It runs only when a name was skipped.
- **Errors of the reads.** A read that breaks its own rule is refused before any store read with an invalid-query error, which the page shows as the store-error text and logs
  (`from must be before to`, `from and to must be within the storable range`, `at must be within the storable range`, `step must be positive`, `at most 2001 steps`, `limit must be 1 to 51`,
  `window must be positive and at most one day`; [Storage reading](storage-reading.md)). A stored thread that does not parse is a store error.
- **Time.** The three actions of a 30-day and of a 1-year range on the benchmark workload (256 units hourly over a year, 30 days of status records at 30 seconds with a snapshot of about 1 MiB in every 100th, and a month of
  per-run `systemd` gaps) finished within the deadline on the development host, the services action of a year in about 3.4 s and every other action in under 0.5 s. On the reference host (median of five) the 30-day services action took 2,798 ms,
  the MariaDB action 1,557 ms and the thread snapshot action 380 ms (latest) and 418 ms (at a moment); for a year the MariaDB action took 1,598 ms and the thread snapshot action 377 and 414 ms, and
  **the services action 13,944 ms, past the deadline**: [Benchmarks](../BENCHMARKS.md), *Services view*, has the figures. **E12 (`Reading the services took longer than 10 seconds …`) is therefore expected for a 1-year range of a server
  whose agent reports its full 256 units** and not for 30 days; the action takes about 1.8 s plus 5.4 µs per `service_state` record on the reference host, so a year fits within 10 s with about 20 % margin only up to about 130 units
  (N3 and AC10 amended, [0227](../decisions/0227-availability-is-the-time-a-recorded-state-holds-up-to-a-cap-and-gaps-are-unknown.md), option 6; exact service availability over long ranges: issue #503). The index of schema step 18 took about 1 s to build on 30 days of status records on the development host and 3,277 ms on the reference host
  ([Storage](storage.md), *Migration steps*).
- A browser script cannot call anything but the two instance methods of the chart's reference (selection and click); both drop a value that is not finite or outside the storable range.
  A drag and a click cannot be tested without a browser; the script is read in review and tried by hand.

## Related decisions

- [0227](../decisions/0227-availability-is-the-time-a-recorded-state-holds-up-to-a-cap-and-gaps-are-unknown.md) — why availability is the time a state holds up to a cap, gap time is unknown and the gaps of a collector are merged before their cap.
- [0228](../decisions/0228-mariadb-status-members-are-indexed-and-folded-on-the-time-series-step-axis.md) — why the status members are in a covering index, folded on the time-series steps, and the thread rows are the data model's type.
- [0215](../decisions/0215-a-service-restart-is-a-rise-of-nrestarts-or-a-new-active-enter-time-of-a-running-or-failed-unit.md) — what counts as a service restart.
- [0028](../decisions/0028-data-gaps-are-always-recorded.md) — why time without data reads as unknown, never as normal.

## Not here

- The reads, their statements and errors: [Storage reading](storage-reading.md); the index and its migration step: [Storage](storage.md); the restart rule and the steps: [Time-series queries](time-series-query.md).
- The chart in general, the gate, external text and circuits: [Web UI](web-ui.md); the login: [Web login](web-login.md).
- The agent's service and MariaDB collectors and when it records threads: [Agent services](agent-services.md), [Agent MariaDB collector](agent-mariadb.md).
- Recording threads when one query runs long on a quiet server, status values beyond `retention.snapshots`, other status values and metrics, event markers on the charts, a chart per service, alerts, automatic refresh,
  selection by touch and ending a query: not built.

## Implementation

`Vandox.Backend`: `Components/Pages/ServicesPage` (the address, the controls and the three actions) with `HistoryRangeControls` (parameter `DefaultPreset`), `HistorySnapshotTable`, `HistoryChart` and `PageLocation`;
`ServiceHealth/` (`ServicesAddress`, `ServicesView` with the texts, `ServicesReader`, `ServicesTable`, `ServicesTableView`, `MariaDbReader`, `MariaDbCharts`, `MariaDbChartView`, `ThreadSnapshotReader`, `ThreadPanel`,
`ThreadStampRow`, `ThreadSnapshotView`, `GatedServiceHistoryReader`, `GatedMariaDbHistoryReader`); `History/` (`AddressTimes.Range` with a fallback preset, `HistoryChartView.Line`, `SnapshotCells`); `Hosting/BackendLog.ServicesReadFailed`.
`Vandox.Storage`: `IServiceHistoryReader`, `IMariaDbHistoryReader`, `TimeSeriesService.RestartsAsync`. The measurement is `--services-view` of `tools/Vandox.StorageBenchmark`.
