# Time-series queries

## Scope

The in-process query of metric lines for a range: which resolution it answers at, the time axis, the state of every point, the list of
gaps and their mapping to lines, the markers (kills, boots, service restarts, backup runs), up to when the points are complete, the caps,
the errors and the performance criterion. The history views of the web UI (planned) call it in process;
no HTTP route, page, option or listener belongs to it.

## Behavior

### Query

- A query names one to 16 series, each a source and a metric name, a range `[from, to)` and a target number of points (1 to 2,000). A
  series listed twice is used once. The target is the caller's choice, from its chart width.
- The query time is the clock's now. The range ends at the earlier of `to` and now, a range that does not begin before now is empty,
  and nothing after now is returned. Every bound the query derives (an hour or a day before `from`, a day or seven days after the end,
  the aligned step bounds) is clamped to the storable range, so no read refuses a window after the query was accepted.
- The result holds one line per stored label set of each series, with its labels, on one time axis at one resolution it names; a
  series without values has no line.

### Resolution

- **Raw** is tried when the raw period is not limited or the range starts within it. It reads at most the target plus one distinct time
  per series; the union of the lines' capture times is the axis. Raw is abandoned for a step as soon as one series has more times than the
  target or the union exceeds it, so lines of one collector, which share their times, count once.
- Otherwise the step is the smallest of 5, 10, 15 and 30 minutes, 1, 2, 3, 6 and 12 hours, 1 day and then whole days for which at most
  target steps overlap the range. The steps below one hour are tried only while the range starts within the 5-minute period (or it has no
  limit) and read the 5-minute rollups; the others read the hourly rollups.
- A range that starts before the hourly period sets the flag *starts before rollups*; the lines hold what the rollups still have, no
  raw values replace them ([Retention and rollups](retention.md)).

### Points

- **At a step**, the times are the multiples of the step since the Unix epoch (UTC) whose step overlaps the range, the first the step
  that holds `from`; a point covers its whole step, also beyond the range. It carries the minimum, the average (the sum over the count of
  every merged bucket), the maximum, the last value with its time and the count. A step without a folded value is a gap.
- **At raw resolution**, the axis is the union of the capture times in the range. A line has a value at its own times. At another line's
  time it is *between samples*: the chart draws through it. It is a gap instead where a gap that concerns the line overlaps the
  interval between two of its values, or a whole UTC-aligned 5-minute span without a value of the line lies in it; a gap time is added
  to the axis there where the axis has none, at most one per interval between two neighbouring times of the original axis, so the axis
  holds at most 2 x target - 1 times. A time inside a concerning gap is a gap also before the first and after the last value of the line.
- Rows of one line at one time merge as buckets do. A sum is clamped to the range of a double.

### Gaps

- The gap list holds the gaps that overlap the range: the agent's sequence and time gaps that are `open` or `final` (a `closed` gap is
  not returned), of all agents, and the agent's gap records, reported as `final`. A gap record is not listed again when it names the
  numbers of a returned sequence gap of its agent and cause; a record whose span does not parse is skipped. Gaps are sorted by start,
  rows before records, then by ID.
- A gap concerns lines by its collector: `self` the source `agent`, `mariadb` the source `mariadb`, `filesystem` the names beginning
  `filesystem_`, `clock` the names beginning `clock_`, `system` the other names of the source `system`. A collector that writes no metric
  (`processes`, `connections`, `systemd`, `kernel`, `logs`, `backups`) concerns no line. A gap without a collector, or of a collector not
  named here, concerns every line, so that a gap is never hidden.
- Gap records are read from 1 hour before `from` to 7 days after the end ([0216](../decisions/0216-time-series-context-is-read-in-fixed-windows-around-the-range.md)).

### Markers

- **Kills and boots** come from the kernel events of the range: each `oom_kill` with its victim command and PID once per boot ID, kernel
  time and PID (a kill that lacks one of them counts per record), and each boot once per boot ID.
- **Service restarts** ([0215](../decisions/0215-a-service-restart-is-a-rise-of-nrestarts-or-a-new-active-enter-time-of-a-running-or-failed-unit.md)):
  a `service_state` record whose `restarts` is above the unit's previous record's, or whose `active_enter_at` is later (or newly present)
  while the previous record was `active`, `activating`, `reloading`, `deactivating` or `failed`, and no boot lies between the two (the
  records have different boot IDs, or a boot is stored in `[previous capture time, capture time)`). A re-record with the same state, a start from
  `inactive`, a start by a boot and a falling counter are no restarts. A restart is timed at the new `active_enter_at` when it lies in the
  range and not after the record's capture time, else at the capture time. The previous record of a unit is searched among the 10,000
  most recent `service_state` records before `from`; a unit not found there has no previous record and no restart.
- **Backup runs** that overlap the range, once per boot ID and start, as a span to `gone_by` with `last_seen_at` as the last sighting. A
  run without an end is *running* while no boot is stored in `(start, now]`, else its end is *unknown*; an end and a last sighting are clamped to
  now. Starts are read from 1 day before `from` to 1 day after the end.
- **The boot read.** The two checks above that ask whether a boot lies in an interval (a restart by a moved enter time, a backup run
  without an end) are answered from one read of the boots per query, not one read per check. The read starts when the first check with a
  non-empty interval needs it: boots newest first from the query time, in pages of 1,001 keyed on capture time and ID, at most ten pages,
  and it stops below the earliest start of the intervals asked about. A restart check is *undecided* only when no boot read lies in its
  interval and the interval reaches below the oldest boot read, which happens when ten full pages did not reach the start of the
  interval. An undecided restart check gives no marker and bounds the time up to which the list of markers is complete at the time its
  marker would have had: the restart time of the record, never before `from`. A backup run without an end is never undecided: its interval
  ends at the query time, where the read starts, so whenever the read does not reach the start of the interval its oldest boot lies in it
  and the run is *unknown*. The boots are read from the query time, not from the end of the range, so that boots stored after an interval
  count against the ten pages; a host that stores more than 10,010 boot records after the start of an interval leaves those restart checks undecided, and the list says
  so by its completeness time (residual risk: boot records come from the agent, so a flood of them can hide restarts
  that lie before them, never invent a marker).
- Markers are sorted by time, kind and record ID; a record stored twice gives one marker.

### Completeness

- At a step, the result states up to when its points are complete: the earliest capture time among the stored values not yet folded, or
  now when every value is folded, never after now. At raw resolution it states none.
- The list of gaps and of markers is complete up to a time of its own when it is cut or an event read ran out of pages
  (below). A later backfill may shrink or close an `open` gap and change folded points.

### External text

Marker texts (SeriesMarker.Text: victim command, unit, backup command), line labels (SeriesLine.Labels), boot IDs and gap
collectors are external text from the agent, returned as stored; a view shows them, and a chart label built from them, only as
[Web UI](web-ui.md) *External text* writes external text.

## Limits and errors

- **Caps.** At most 64 lines, the first 64 in series order and then in the UTF-8 byte order of the stored label text; the cut series are
  named. At most 1,000 markers and 1,000 gaps, the earliest ones, with the time up to which the list is complete. At most `2 x target - 1`
  times.
- **Bounds of the work.** The memory of a query is bounded by these caps; its store work by the rollup buckets of its lines in the
  range and the `service_state` records of the range (each read once, the service rows streamed in capture order and only the last record of
  each unit and the changes kept), never by the raw values in it. Event reads (gap records, kernel events, backup runs) project the named members of their rows,
  never the payload or the message, in pages of at most 1,001 rows, at most ten pages per kind and query; service rows are read at most
  2,001 plus one backward scan of at most 10,000 rows; the boot checks of a query read at most ten pages of 1,001 boots.
- **Errors.** A query is refused before any store read with an invalid-query error whose text names the rule:
  - `store: invalid query: at least one series is required`
  - `store: invalid query: at most 16 series`
  - `store: invalid query: source and name are required`
  - `store: invalid query: target must be 1 to 2000`
  - `store: invalid query: from must be before to`
  - `store: invalid query: from and to must be within the storable range`
- A failing read ends the query with the store's error; a cancelled token or a passed deadline ends it with a cancellation, with no
  partial result and no gate slot held. Each store read takes one slot of the views' shared read gate for that call only
  ([Web UI](web-ui.md), *Store reads of the views*).
- **Performance.** The reference workload (a year of rollups of eight series, one of them `cpu_percent` with eight modes, at 1,000
  points, with markers and gaps, over the database described in [Benchmarks](../BENCHMARKS.md), *Time-series query*) must run in under one
  second, median of five runs, on the reference host. It took a median of 795 ms there (2026-11-15) and meets the criterion; the three
  reads of service records are answered from the covering index `records_service_state` ([Storage](storage.md), *Migration steps*) without
  a payload, and the service changes remain the largest part (Benchmarks has the numbers and the breakdown).

## Related decisions

- [0214](../decisions/0214-time-series-queries-share-one-resolution-and-one-time-axis-with-bridged-samples.md) — why one resolution and one axis, and why raw samples are bridged between other lines' times.
- [0215](../decisions/0215-a-service-restart-is-a-rise-of-nrestarts-or-a-new-active-enter-time-of-a-running-or-failed-unit.md) — what counts as a service restart.
- [0216](../decisions/0216-time-series-context-is-read-in-fixed-windows-around-the-range.md) — why gap records and backup runs are read in fixed windows around the range.
- [0217](../decisions/0217-service-state-members-are-indexed-for-the-restart-markers.md) — why the members of service records that tell a restart are in a covering index.
- [0028](../decisions/0028-data-gaps-are-always-recorded.md) — why a span without data is a gap and never drawn as normal.

## Not here

- The store reads and their limits: [Storage reading and log exploration](storage-reading.md), *Time-series reads*.
- The fold into rollups, the periods and the watermark: [Retention and rollups](retention.md).
- The chart, its between-samples state and the shared gate: [Web UI](web-ui.md).
- The views that call the query: planned.

## Implementation

`Vandox.Storage`: `TimeSeriesService` (`QueryAsync`; `TimeSeriesQuery`, `SeriesKey`, `TimeSeriesResult` with `SeriesLine`, `SeriesPoint`,
`SeriesGap`, `SeriesMarker`) over `ITimeSeriesReader`, which `SqliteStore` implements with `TimeSeriesQueries`. `Vandox.Backend`:
`GatedTimeSeriesReader`, registered once with the service in `Hosting/ServeCommand`. The measurement is `--query` of
`tools/Vandox.StorageBenchmark`.
