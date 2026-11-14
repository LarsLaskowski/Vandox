# Spec: Time-series query API

Source: Issue #47
Status: Draft

## Problem

The views of release v0.3.0 (overview #48, history #49, processes #50, network #51, services and MariaDB #52) chart metric series
over ranges from an hour to years. Today each view would have to read for itself: the raw values (kept 30 days by default) through
the record reader, the 5-minute and hourly rollups of #46 through the rollup reader (at most 10,000 buckets per call; a year of
5-minute buckets is 105,120), and the gap rows through a reader that pages one agent's gaps in ID order with no time range. No code
picks a resolution, aligns series, turns empty spans into gaps a chart can draw (a missing value must never read as normal, record
0028) or finds the events of a range. Charts would come out slow, inconsistent, or would draw a line across lost data.

Issue claims checked against the code: #46 is merged (rollups, [Retention and rollups](../../docs/areas/retention.md)); the readers
named above exist in `src/Vandox.Storage` (`IRecordReader`, `IRollupReader`, `IAgentTimelineReader`); the events exist as agent
records (`kernel_event` `oom_kill` and `boot`, `service_state`, `backup_run`), self-healing does not (planned for v0.6.0, #69). The
backend is .NET 11 now (record 0197), not .NET 10; the chart foundation (uPlot through JS interop, own static assets) is as stated.

## Scenarios

- S1: Given the overview page charts the last 24 hours at 1,000 points, when it queries available memory and swap, then it gets one
  line each at a 5-minute step (288 points, a 30-second series has too many values for raw), each point with minimum, average,
  maximum, last value and count, plus the markers and gaps of the day.
- S2: Given the history view shows one year at 1,000 points, when it queries eight series, then every line has a 12-hour step built
  from the hourly rollups (730 points) and the year's markers and gaps, in under one second on the reference host.
- S3: Given the history view zooms into the last hour, when it queries `mem_available_bytes`, then the result has raw resolution:
  every stored value of the hour is a point.
- S4: Given a series with labels (`cpu_percent` with `mode`), when it is queried, then the result holds one line per label set,
  each with its labels.
- S5: Given the agent was stopped for 20 minutes and a collector missed one sample, when a range over both is queried, then the
  lines carry points without value there and the gap list holds both gaps with cause, state and the collector where one is named.
- S6: Given an OOM kill, a reboot, a restart of `mariadb.service` and a nightly backup in the range, then the markers list them in
  time order, the backup as a span from its start to its end.

## Errors and edge cases

- E1: A series with no stored value in the range, or a name never stored, yields the series with no line; it is not an error.
- E2: A range that reaches past the query time: no point, marker or gap after the query time is returned.
- E3: A range that ends near the query time at a rollup step: rollups lag by up to 5 minutes (record 0208), so the result states
  the time up to which its rollup points are complete; a point after it may still grow and its missing value is pending, not lost.
- E4: A range whose start lies before the raw period (`retention.raw`): raw resolution is not chosen even where imported values
  or values in the window of an OOM kill are still stored; likewise a step under one hour is not chosen before the 5-minute period.
- E5: More than 64 lines in total (`meminfo_bytes` has one per `/proc/meminfo` field): the first 64 are returned, in the order of
  the requested series and then of their labels, and the result names each series that was cut.
- E6: More than 1,000 markers, or more than 1,000 gaps: the earliest 1,000 are returned and the result states the time up to which
  that list is complete.
- E7: A backfill arrives after a query: a sequence gap reported as `open` may shrink or close, and the rollup points of its span
  change once they are folded; a repeated query shows the new state. A gap whose data arrived in full (`closed`) is not returned.
- E8: Invalid input — no series, more than 16 series, an empty source or name, a target outside 1 to 2,000, a start not before the
  end, a time outside the storable range — is refused with an error that names the rule, before anything is read.
- E9: The caller cancels, or its deadline passes: the query stops, its store reads are interrupted, it returns no partial result
  and holds no read slot.
- E10: A unit re-recorded after an agent start or hourly with the same state is no restart; a restart counter that falls (systemd
  reset it) is no restart by itself.
- E11: A backup run that started before the range and ends in or after it, or that has no stored end yet, is one marker for the part
  that overlaps the range; one without an end is marked as still running.

## Non-functional requirements

- N1: One query of the reference workload returns in under one second, median of five runs, on the reference host of
  [`docs/BENCHMARKS.md`](../../docs/BENCHMARKS.md) (Synology DS918+, SSD volume). Reference workload: a database with one year of
  the agent's data at its default intervals (the rollups of every series of the `system`, `mariadb` and `self` collectors for a year
  and their raw values for the last 30 days), one backup run a day, 10 tracked units, 50 OOM kills and 1,000 recorded gaps; the query
  covers one year, eight series of which one is `cpu_percent` with its eight modes, at a target of 1,000 points, with markers and gaps.
- N2: The memory and the store work of one query are bounded by its caps (16 series, 64 lines, 2,000 points per line, 1,000
  markers, 1,000 gaps), not by the number of stored values in the range.
- N3: Every store read of a query takes the shared read gate of the views ([Web UI](../../docs/areas/web-ui.md), *Store reads of
  the views*) per store call and honours the caller's cancellation token and deadline.
- N4: The service is called by Blazor components inside the backend; it adds no HTTP route, page or listener.
- N5: Marker texts (a victim command, a unit name, a backup command) are external text and returned as stored; a view that shows
  them escapes them ([Web UI](../../docs/areas/web-ui.md), *External text*).
- N6: No new configuration option; the periods are the existing `retention.*` options.

## Acceptance criteria

- AC1: A query names its series (source and metric name), a range `[from, to)` and a target point count; the result holds one
  line per stored label set of each series, with its labels, at one resolution shared by all lines, which the result names: raw when
  the range starts within the raw period and no line has more stored values in the range than the target; otherwise the smallest step
  of the ladder 5, 10, 15, 30 minutes, 1, 2, 3, 6, 12 hours, 1 day and whole days beyond that is at least `(to − from) / target`,
  taken from the 5-minute rollups when under one hour and the range starts within the 5-minute period, else from the hourly rollups
  with a step of at least one hour; no line has more points than the target (S1, S2, S3, S4, E1, E4).
- AC2: At a step, the points lie at whole multiples of the step since the Unix epoch in UTC, one per step that starts in the range,
  each with the minimum, the average (summed values over the count of every bucket merged into it), the maximum, the last value with
  its time, and the count; at raw resolution every stored value in the range is one point (S1, S2, S3).
- AC3: A span without data is a point without value, never zero and never bridged: at a step, a step with no folded value; at raw
  resolution, a point without value is placed between two consecutive values when a returned gap that concerns the line lies between
  them, or when a whole 5-minute span aligned in UTC between them holds no value (S5).
- AC4: The result lists the gaps that overlap the range: the gap records of the agents and the agents' sequence and time gaps that
  are `open` or `final`, each with its span, cause, state and, where it names one, the collector — a gap with a collector concerns the
  lines that collector writes, one without concerns every line of the agent; `closed` gaps are left out (S5, E7).
- AC5: The result lists the markers in the range in time order: each `oom_kill` with its victim command and PID, each boot, each
  service restart with its unit — a `service_state` record whose `active_enter_at` is later, or whose `restarts` is higher, than in the
  unit's previous record, timed at the new `active_enter_at` when it moved, else at its capture time — and each backup run that
  overlaps the range as a span from its start to its end, or still running (S6, E10, E11).
- AC6: Nothing after the query time is returned, and at a rollup step the result states the time up to which its points are complete,
  5 minutes before the query time (E2, E3).
- AC7: Over 64 lines, the first 64 are returned in series then label order with the cut series named; over 1,000 markers or gaps,
  the earliest 1,000 with the time up to which the list is complete (E5, E6, N2).
- AC8: An invalid query is refused before any store read with an error naming its rule: no series, more than 16, an empty source or
  name, a target outside 1 to 2,000, `from` not before `to`, a time outside the storable range (E8).
- AC9: Each store read of a query holds the shared read gate of the views for that call only; a cancelled token or a passed deadline
  ends the query with a cancellation, no partial result and no slot held (E9, N3).
- AC10: A query of the reference workload of N1 returns in under one second, median of five runs, on the reference host, measured by
  a benchmark that docs/BENCHMARKS.md describes, with its result recorded there (S2, N1).

## Out of scope

- Self-healing markers: self-healing does not exist before v0.6.0 (#69); its marker kind is added with it.
- Events found only in log lines (imported OOM kills, boots and service events, which the signature detector finds when `vandoxd
  incidents` runs) and stored incidents as markers: the import writes no event records, and running the detector per query cannot
  meet N1.
- Series of process and connection snapshots per program (#458, needed by #50 and #51), MariaDB status and service availability
  series (#52), log template counts.
- The views that use the service (#48 to #52), an HTTP API (N4), and caching of results.
- N4, N5 and N6 are constraints the plan keeps; they need no criterion of their own.

## Open questions

- What "returns in under one second on the backend host" measures. Answered: (Lead) the reference host of `docs/BENCHMARKS.md`, as
  for the write criterion (record 0083), with the workload of N1, sized from the largest chart the v0.3.0 views plan (#49's overlays,
  #48's key figures); the run on the reference host is the Product Manager's, as before.
- What counts as a restart. Answered: (Lead) systemd's `NRestarts` counts only automatic restarts, so a later `active_enter_at` also
  counts, which covers a manual restart; an unchanged re-record is none (`docs/areas/agent-services.md`).
- Who sets the target point count. Answered: (Lead) the caller, from its chart width, 1 to 2,000.
- Whether imported data has gaps. Answered: (code) the import records none; a span without values shows as a point without value
  (AC3), which keeps it from reading as normal.

## Area documents

- New area `docs/areas/time-series-query.md` (*Time-series queries*): the query and its rules, resolution choice and ladder,
  alignment and point contents, points without value, the gap list and which lines a gap concerns, the marker kinds and their
  definitions, completeness and the caps, errors, the read gate and cancellation, the performance criterion with a link to
  `docs/BENCHMARKS.md`; its index row in `docs/areas/README.md`.
- [Storage reading and log exploration](../../docs/areas/storage-reading.md): any new store read the plan adds (gaps by time range,
  rollups merged per step) with its limits.
- [Web UI](../../docs/areas/web-ui.md), *Store reads of the views*: the query service among the callers of the shared gate.
- [Retention and rollups](../../docs/areas/retention.md), *Rollups*: the readers line names the time-series query.
- `docs/BENCHMARKS.md`: a section for the one-year query with workload, procedure and results; `docs/ARCHITECTURE.md`: the service
  in the backend component and the storage interfaces list if the plan adds one.
