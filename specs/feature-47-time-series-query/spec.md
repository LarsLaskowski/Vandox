# Spec: Time-series query API

Source: Issue #47
Status: Approved by Lead

## Problem

The views of v0.3.0 (#48 to #52) chart metric series over ranges from an hour to years. Today each would read for itself: raw values
(30 days by default) through `IRecordReader`, the rollups of #46 through `IRollupReader` (at most 10,000 buckets per call; a year of
5-minute buckets is 105,120) and gaps through `IAgentTimelineReader`, which pages one agent's gaps in ID order with no time range. No
code picks a resolution, aligns series, marks spans without data so a chart never draws them as normal (record 0028) or finds the
events of a range.

Issue claims checked: #46 is merged; the readers exist; the events exist as agent records, self-healing does not (#69); the backend
is .NET 11 (record 0197), not .NET 10.

## Scenarios

- S1: Given the overview charts the last 24 hours at 1,000 points, when it queries available memory and swap, then each is one line
  at a 5-minute step (a 30-second series has too many values for raw), plus the day's markers and gaps.
- S2: Given the history view shows one year at 1,000 points with eight series, then every line has a 12-hour step from the hourly
  rollups, with the year's markers and gaps, in under one second on the reference host.
- S3: Given the history view overlays `mem_available_bytes` (collector `system`, 30 s) and `agent_rss_bytes` (collector `self`, 60 s)
  over the last 30 minutes, then the result is raw on one axis, the union of both lines' times, and each line is drawn connected
  through the times only the other was sampled at.
- S4: Given `cpu_percent` is queried, then the result holds one line per `mode`, each with its labels.
- S5: Given the agent was stopped for 20 minutes and the `filesystem` collector missed one sample, then every line breaks over the
  stop, only the `filesystem_*` lines break over the missed sample, and the gap list holds both.
- S6: Given an OOM kill, a reboot, a crash and automatic restart of `mariadb.service` and a nightly backup, then the markers list each
  once in time order, the backup as a span; the units started by the reboot are no restarts.

## Errors and edge cases

- E1: A series without values in the range has no line; no error.
- E2: Nothing after the query time is returned.
- E3: Rollups lag behind the stored values (record 0208), a stalled fold further: the result says up to when its rollup points are
  complete; a missing value after that is pending, not lost.
- E4: Raw is not chosen for a range that starts before `retention.raw`, nor a step under one hour before `retention.rollups_5m`, even
  where imported values or the window of an OOM kill are still stored. Before `retention.rollups_1h` no bucket exists: the lines are
  empty and the result says so.
- E5: More than 64 lines (`meminfo_bytes` alone has one per `/proc/meminfo` field) are cut, and the result names the cut series.
- E6: More than 1,000 markers or gaps are cut, and the result says up to when each list is complete.
- E7: A backfill after a query may shrink or close an `open` sequence gap and change rollup points once folded; a repeated query shows
  it. A `closed` gap is not returned.
- E9: Cancellation or a passed deadline stops the query (AC9).
- E10: A re-record with the same state (after an agent start, hourly), a start from stopped, a start by a boot, and a falling restart
  counter are no restarts.
- E11: A record stored twice (a kill from two journal files, a start written again after a lost check) gives one marker; a lost range
  the agent reported and the sequence gap it made final are one gap.
- E12: Facts before `from` count: an earlier-starting gap, a unit's previous record of any age, a backup that started before the
  range. An agent gap record is found when captured from one hour before the range to seven days after it, a backup record from one
  day before to one day after (record 0216). A start without a stored end is still running while no later boot is stored, else its
  end is unknown.

## Non-functional requirements

- N1: The reference workload runs in under one second, median of five runs, on the reference host of
  [`docs/BENCHMARKS.md`](../../docs/BENCHMARKS.md) (DS918+, SSD volume): a database with one year of the agent's data at default
  intervals (a year of rollups of the `system`, `filesystem`, `clock`, `mariadb` and `self` metrics, their raw values for the last 30
  days), a backup run a day, 10 tracked units, 50 OOM kills, 1,000 recorded gaps; a one-year query of eight series, one of them
  `cpu_percent` with its eight modes, at 1,000 points, with markers and gaps.
- N2: The memory of a query is bounded by its caps (16 series, 64 lines, 2,000 times plus the gap times AC3 adds, 1,000 markers,
  1,000 gaps); its store work by the rollup buckets of its lines in the range and one page per event kind, never by the raw values
  in the range: the raw check reads at most one distinct time more than the target.
- N3: Every store read takes the views' shared read gate ([Web UI](../../docs/areas/web-ui.md), *Store reads of the views*) per call
  and honours the caller's token and deadline.
- N4: Blazor components call the service in process; no HTTP route, page or listener is added.
- N5: Marker texts are external text, returned as stored; views escape them.
- N6: No new option; the periods are the `retention.*` options.

## Acceptance criteria

- AC1: A query names its series (source, metric name), a range `[from, to)` and a target; the result holds one line per stored label
  set of each series, with its labels, on one time axis at one resolution it names: raw when the range starts within the raw period
  and the lines' values in the range fall on at most target distinct times; else the smallest step of 5, 10, 15, 30 minutes, 1, 2,
  3, 6, 12 hours, 1 day and whole days for which at most target steps overlap the range, from the 5-minute rollups when under one hour
  and the range starts within their period, else from the hourly rollups with a step of at least one hour (S1 to S4, E1, E4).
- AC2: At a step, the times are the multiples of the step since the Unix epoch (UTC) whose step overlaps the range, the first the step
  holding `from`; each point covers its whole step, also beyond the range, with minimum, average (sum over count of every merged
  bucket), maximum, last value with its time, and count. At raw resolution the times are the union of the lines' capture times in
  the range (S1, S2, S3).
- AC3: At each time a line holds a value, a gap (no value; the chart breaks the line) or, at raw resolution, *between samples* (only
  another line was sampled; the chart draws through it). A gap is a step without a folded value; at raw resolution a time inside a
  returned gap that concerns the line, and a time between two consecutive values of the line when such a gap or a whole UTC-aligned
  5-minute span without value lies between them — a gap time is added there where the axis has none (S3, S5).
- AC4: The result lists the gaps overlapping the range: agent gap records (as `final`) and agent sequence and time gaps that are
  `open` or `final`, with span, cause, state and collector if named; an agent gap record inside a returned sequence gap of the same
  cause is not listed again. A gap's collector concerns its lines — `self` the source `agent`, `mariadb` the source `mariadb`,
  `filesystem` the `filesystem_*` and `clock` the `clock_*` names, `system` the other names of source `system`; a collector that writes
  no metric (`processes`, `connections`, `systemd`, `kernel`, `logs`, `backups`) concerns no line; a gap without a collector, or of a
  collector not named here, concerns every line (S5, E7, E11, E12).
- AC5: The markers in the range, in time order: each `oom_kill` with victim command and PID, once per boot ID, kernel time and PID;
  each boot, once per boot ID; each service restart with its unit — a `service_state` record whose `restarts` is above the unit's
  previous record's, or whose `active_enter_at` is later while the previous record was `active`, `activating`, `reloading`,
  `deactivating` or `failed` with no boot between, timed at the new `active_enter_at` when it moved, else at its capture time; each
  backup run overlapping the range, once per boot ID and start, as a span to `gone_by` (with `last_seen_at`), still running, or with
  an unknown end (S6, E10, E11, E12).
- AC6: Nothing after the query time is returned; at a rollup step the result states up to when its points are complete: the earliest
  capture time among the stored values not yet folded, or the query time when every value is folded, never after it (E2, E3).
- AC7: Over 64 lines, the first 64 in series order, then ordinal label text, with the cut series named; over 1,000 markers or gaps,
  the earliest 1,000 with the time up to which the list is complete (E5, E6, N2).
- AC8: Refused before any store read, with an error naming the rule: no series, more than 16, an empty source or name, a target
  outside 1 to 2,000, `from` not before `to`, a time outside the storable range.
- AC9: Each store read holds the shared read gate for that call only; a cancelled token or passed deadline ends the query with a
  cancellation, no partial result and no slot held (E9, N3).
- AC10: The reference workload of N1 runs in under one second, median of five runs, on the reference host, measured by a benchmark
  `docs/BENCHMARKS.md` describes, with the result recorded there (S2, N1).
- AC11: The web UI's time-series chart accepts *between samples*: it draws a line through such a time and still breaks it at a gap
  (S3).

## Out of scope

- Self-healing markers (#69, v0.6.0).
- Events found only in log lines and stored incidents as markers: the import writes no event records, and running the signature
  detector per query cannot meet N1.
- Per-program snapshot series (#458), MariaDB status and service availability series (#52), template counts; imported values older
  than the hourly period (E4).
- The views (#48 to #52), an HTTP API, caching. N4, N5, N6: constraints without criteria.

## Open questions

- What "under one second on the backend host" measures. Answered: (Lead) N1, on the reference host of `docs/BENCHMARKS.md` (as for
  record 0083), run there by the Product Manager.
- What counts as a restart. Answered: (Lead) AC5, record 0215.
- Who sets the target. Answered: (Lead) the caller, from its chart width.
- Whether imported data has gaps. Answered: (code) none are recorded; spans without values are gaps by AC3.
- Whether the chart can bridge a sample but break at a gap. Answered: (code) vendored uPlot 1.6.32 breaks only at `null` and draws
  through `undefined` (`findGaps`, `wwwroot/lib/uplot/uPlot.esm.js`).

## Area documents

- New `docs/areas/time-series-query.md` (*Time-series queries*), with its index row: every rule of AC1 to AC10.
- [Storage reading](../../docs/areas/storage-reading.md): the new store reads; [Web UI](../../docs/areas/web-ui.md): *Charts*
  gains *between samples*, the gate names the service; [Retention and rollups](../../docs/areas/retention.md): its readers line.
- `docs/BENCHMARKS.md`: the one-year query; `docs/ARCHITECTURE.md`: the service.

## Challenge

- M1 (raw lines cannot share the chart's axis). Accepted: AC1 puts all lines on one time axis; at raw resolution it is the union of
  the lines' capture times (AC2), and a line's time without its own sample is *between samples*, distinct from a gap (AC3, S3). The
  chart contract gains that state (AC11); the vendored uPlot already draws through `undefined` and breaks only at `null` (Open
  questions). The raw check now counts distinct times of the union against the target, so a raw result never has more times than the
  target.
- M2 (collectors are not sources). Accepted: AC4 maps each collector to its lines — `self` to source `agent`, `mariadb` to source
  `mariadb`, `filesystem` and `clock` by their name prefixes within source `system`, `system` the other `system` names (record of
  the names: `docs/areas/wire-format-records.md`, *Metric names*); an unmapped collector concerns every line, so a gap is never
  hidden. S5 tests `filesystem` against `system`; the plan adds a `self` → `agent` case.
- M3 (every start counted as restart). Accepted with one widening: a later `active_enter_at` counts only when the previous record was
  `active`, `activating`, `reloading`, `deactivating` or `failed` and no boot lies between (AC5, E10, S6). `failed` is kept because a
  manual restart of a failed unit is what an operator calls a restart; a start from `inactive` is not.
- m1 (completeness from the fold state). Accepted: AC6 takes the time of the last fold step that left nothing unfolded, at most the
  query time; E3 names the stalled fold, which the plan tests.
- m2 (duplicates). Accepted: AC5 deduplicates kills by boot ID, kernel time and PID, boots by boot ID and backup runs by boot ID and
  start; AC4 lists an agent gap record that lies in a returned sequence gap of the same cause once (E11).
- m3 (reads before `from`). Accepted: E12 and AC4/AC5 name what counts from before the range; a start without an end is still running
  only while no later boot is stored, else its end is unknown.
- m4 (range edges at a step). Accepted: AC2 takes every step that overlaps the range, the first one the step holding `from`, each
  point covering its whole step; AC1 chooses the step by the number of overlapping steps, so the target still holds.
- m5 (raw check cost). Accepted: N2 bounds the raw check to one value more than the target per line.
- m6 (range before the rollup periods). Accepted as an outcome, not as a raw fallback: E4 states empty lines with a flag, and Out of
  scope names it; a raw fallback over imported values is unbounded by any period and would break N1 and N2.
- m7 (wording). Accepted: agent gap records are reported as `final` (AC4); a backup span ends at `gone_by` with `last_seen_at` as the
  last sighting (AC5); label order is ordinal on the stored label text (E5, AC7).
- Lead, while planning (spec fixes before approval): AC4 let a gap of the `logs` or `kernel` collector break every metric line,
  though those collectors write no metric — such a collector now concerns no line, and only an unnamed collector every line. AC6
  took a fold time the store does not keep; the earliest unfolded capture time states the same completeness exactly from the
  watermark. E12 and N2 now name the read windows (record 0216) and that a step's work grows with the buckets in the range.
