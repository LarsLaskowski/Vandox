# Program rollups

## Scope

What remains of the process snapshots after `retention.snapshots`: for each source, the memory and CPU of each program in 5-minute and
hourly buckets, folded by the retention job, and the store reads that return the top programs of a source per step and the sources that
have them. The [Processes view](web-ui-processes-view.md) and anyone reading a program's history over months rely on it. The snapshots themselves, with their
programs, `total` and `complete`, are in [Agent system metrics](agent-system-metrics.md), *Processes*. Rollups of the metric values are in
[Retention and rollups](retention.md); the tables are in [Storage](storage.md); the read's place among the others is
[Storage reading](storage-reading.md). There is no option: the buckets follow `retention.rollups_5m` and `retention.rollups_1h`.

## The buckets

- The job folds every `process_snapshot` record of every origin (agent, import, backend) exactly once, in the order of its record ID,
  into the 5-minute bucket and the hourly bucket that hold its capture time, of the snapshot's source. A bucket is addressed by the source, the start of the bucket and, for a
  program, the program name.
- **A program row** holds, for one program in one bucket: `seen` (the snapshots of the bucket that name the program) and the minimum,
  maximum and sum of its RSS (bytes), of its PSS (bytes; minimum and maximum absent while no snapshot of the bucket was a PSS snapshot,
  the sum 0) and of its CPU %. **A total row** holds, for the source and the bucket: `snapshots` (the snapshots folded into it),
  `pss_snapshots`, `incomplete`, `named` (the programs the bucket names) and the sums of RSS, PSS and CPU % over all programs, named or
  not. No last value, process count or percentile is stored (record 0223).
- **A PSS snapshot** lists at least one program and every listed program carries `pss_bytes`. Its PSS counts toward the PSS columns and
  the bucket's `pss_snapshots`; any other snapshot (an import of the legacy monitoring log, an agent without `CAP_SYS_PTRACE`) counts
  toward RSS only. A bucket that holds both kinds merges in either order and keeps correct RSS columns and totals.
- **An incomplete snapshot** does not list every program (the rule is `ProcessSnapshot.ListsEveryProgram`, which the fold and the processes view share): the sum of the programs' `count` differs from `total`, or `total` is absent
  and `complete` is not true, or the snapshot lists no program. A regular agent snapshot (top 25 processes) lists every program when the
  counts add up to `total`. The total row counts these snapshots in `incomplete`.
- **A program missing from a snapshot of its source is 0 in it.** The average of a program over a bucket is its sum over the bucket's
  `snapshots`, so the averages of all programs of a bucket add up to the source's average total. A bucket's minimum for a program is 0
  when `seen` is below `snapshots`, else the stored minimum. Each snapshot weighs equally, whatever time it stands for (regular,
  pressure and OOM snapshots alike), as in the metric rollups.
- **The cut per snapshot.** A snapshot that lists more than 50 programs is folded by name only for the 50 with the highest memory in the
  snapshot's own key (PSS for a PSS snapshot, else RSS) and the 50 with the highest CPU %, ties by the program name in ordinal order;
  every other program is in the total only. A program outside the cut reads lower in that bucket, without a flag.
- **The cap per bucket.** A bucket names at most 500 programs: the first 500 names it meets in fold order stay named, and a further
  name is in the total only. The fold order is the record ID, then within a snapshot the ordinal order of the names. Once a bucket has
  500 names, a later step folds only those programs into it.
- **Names** (up to 1,024 bytes, any text) are stored and returned as received, bound as parameters, never interpreted; a NUL, a
  control character or a right-to-left override stays in the name. A caller that shows one must escape it.
- **Sums are clamped** to the range of a double (plus or minus `double.MaxValue`) at every addition, in the fold, in the merge into a
  stored bucket and in every sum a read computes, so forged values near 2^64 bytes or a huge CPU % never make a fold step fail and a
  read never returns Infinity or NaN.
- **A late snapshot** (an agent backfill, an import) whose bucket exists is merged into it, whether the bucket's snapshots are still
  stored or removed. A bucket past its tier's period is not created.
- **The fold step** reads at most 500 process snapshots above the watermark `program_rollup_after_id` by record ID and stops after the
  row that takes the stored data bytes read (UTF-8, before decoding) past 2 MiB; the first row is always taken. It runs in the
  transaction of the metric fold step ([Retention and rollups](retention.md), *The job*) and holds at most one step's data in memory;
  a database of an earlier version folds its stored snapshots first, in steps, before any snapshot is removed.
- **A snapshot that does not decode** is skipped: NULL data, data that is not valid JSON or not valid UTF-8, a payload that is not a
  process snapshot, or one the wire validation refuses. The skipped snapshot is counted in the log line (event 76), the watermark passes
  it, and the removal takes it with its period, so it never stops folding or removal. No program name of it is logged.
- **Failure and busy** are those of the metric fold: the step rolls back as a whole, the job logs its warning or error line
  and the next tick retries it.

## Reading

Both reads run on a reader connection under the caller's cancellation ([Storage](storage.md), *Database file and connections*).

- **Top programs** of one source for a range, a step, one measure and a number *n* of programs. The step is one of the time-series
  query's steps (5, 10, 15, 30 minutes, 1, 2, 3, 6, 12 hours, then whole days up to 36,500). A step below one hour reads the 5-minute
  buckets, every other step the hourly ones. Steps lie on the grid of multiples of the step since the Unix epoch (UTC); the first step
  is the one that holds `from`, so a `from` off a boundary starts at the boundary before it. A range with `from` at or after `to` returns
  the empty result.
- **The result** holds the step starts (`Times`), at most *n* programs, each with one point per step (average, minimum, maximum) or a gap, the
  sum of all other programs per step as *other programs* (`Others`), the number of folded snapshots per step that did not list every
  program (`Incomplete`), and the memory key.
  - The *n* programs are those with the highest sum of the measure over the range, which is the highest average since every program is
    divided by the same snapshots, in that order, ties by program name in the byte order of SQLite. A program a step's snapshots do not
    name reads 0 in that step.
  - A step without a folded snapshot is a gap: `null` in a line and in *other programs*, 0 in the incomplete count.
  - *Other programs* is the average of the total of the source less the named programs, computed from the clamped sums and kept within 0 and
    `double.MaxValue`; it includes the programs cut or capped in the fold. The named programs and *other programs* add up to the source's
    average total, unless a fold step commits between the read's queries, which share one reader connection without a shared transaction: then a
    step's named programs can differ from its total by that fold step's snapshots, and *other programs* stays at 0 at the least.
  - **The memory key**: the read returns PSS and ranks by it when every folded snapshot of the range is a PSS snapshot, else RSS for the
    whole range; it names the key. The key is one per range. For CPU the key is absent.
  - `EarliestUnfolded` is the earliest capture time of the source's process snapshots not yet folded (none when all are), and
    `StartsBeforeBuckets` is true when the range starts before the oldest bucket the source has in the tier the step reads. A step below one
    hour over a range that starts before the 5-minute period returns what the 5-minute buckets still hold: gaps before, the flag set.
- **Sources**: the sources that have a bucket in a range of either tier, by source in the byte order of SQLite, with the start of the
  latest bucket within the range, at most 256, with a flag when more exist.
- **Memory:** a read holds at most *n* + 1 lines of at most 2,000 steps.
- **Limits and errors.** An unknown source, an empty range or no bucket is the empty result, no error. Every other broken rule is the
  store's invalid-query error `store: invalid query: <text>` before any read: `count must be 1 to 50`, `step must be one of the query's steps`,
  `the range holds more than 2000 steps` (steps of the epoch grid that overlap the range; a year at a 5-minute step is one such range),
  `from and to must be within the storable range` and `unknown measure`. The source list refuses only a bound outside the storable
  range. A failing statement is `store: reading program rollups` with SQLite's message; a cancelled call ends as cancelled.

## Disk and time

Measured with `--programs` of the storage benchmark on the development host and on the reference host SSD volume (`docs/BENCHMARKS.md`, *Program
rollups*); the figures below are those of the reference host, the development host measured about a quarter to two fifths of them:

- A day of one snapshot a minute (1,440 snapshots of 80 programs) added 703 pages of 4,096 bytes (2,812 KiB) and folded in 1,464 ms in
  6 steps.
- A fold step beside 10,000 metric values and 500 snapshots of 80 programs took a median of 317 ms and folded 275 snapshots, which the
  2 MiB budget limits; the median ingest batch of the same run took 1,667 ms.
- A year of 8,760 hourly snapshots folded in 50,399 ms in 32 steps; a one-year read at a 6-hour step, memory, *n* = 10, took a median
  of 1,270 ms, within the views' 10-second deadline.
- The 5-minute tier costs about as many rows per day as the named programs times 288, the hourly tier the named programs times 24. The free-space
  floor `ingest.min_free` stays the bound of last resort against names forged to fill the tables ([Retention and rollups](retention.md),
  *Limits and errors*).

## Related decisions

- [0223](../decisions/0223-process-snapshots-are-folded-into-per-program-buckets-beside-a-source-total.md) — why process snapshots are folded into per-program buckets beside a source total, by record ID, with the cut and the cap.
- [0208](../decisions/0208-metric-rollups-are-folded-from-new-rows-by-record-id-never-recomputed-from-raw.md) — the watermark rule and the merge of late values this fold follows.

## Not here

- The retention periods, the removal walk and the log lines: [Retention and rollups](retention.md).
- The tables, the index and the migration step: [Storage](storage.md).
- The meaning of a snapshot's fields: [Agent system metrics](agent-system-metrics.md), *Processes*.
- The same for connection snapshots (sockets per state, program and remote address): [Connection rollups](connection-rollups.md).
- The charts that read the result: [Processes view](web-ui-processes-view.md); they take the top 10 programs per chart and one source at a time and never read the snapshots, which only the view's panels *Top processes* and *Comparison* do.

## Implementation

`Vandox.Storage`: `ProgramRollupFolder` (the fold, with `ProgramBucketTotals`, `ProgramSourceTotals`, `ProgramRollupTier`), `ProgramRollupQueries` (the
reads, with `ProgramStepGrid`, `ProgramStepTotals`), `IProgramRollupReader` (`ProgramRollupsAsync`, `ProgramSourcesAsync`), implemented by `SqliteStore`
with the types `ProgramRollupQuery`, `ProgramRollupResult`, `ProgramRollupLine`, `ProgramRollupPoint`, `ProgramMeasure`, `ProgramMemoryKey`,
`ProgramSourceQuery`, `ProgramSourceList` and `ProgramSource`. `ProcessSnapshot.ListsEveryProgram` (`Vandox.Core`) holds the rule of an incomplete snapshot. The limits are in `StorageLimits`. The measurement is `--programs` of
`tools/Vandox.StorageBenchmark`.
