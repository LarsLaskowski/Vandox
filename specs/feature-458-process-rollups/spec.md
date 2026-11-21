# Spec: Rollups of process snapshots per program

Source: Issue #458
Status: Draft

## Problem

The administrator wants the memory and CPU of each program over months, not only over the last 30 days. Today the
per-program values exist only inside the `process_snapshot` records ([Agent system metrics](../../docs/areas/agent-system-metrics.md),
*Processes*; [wire format](../../docs/areas/wire-format-records.md)), which retention removes after `retention.snapshots` (30 days by
default), while only metric values leave rollups behind ([Retention and rollups](../../docs/areas/retention.md)). The processes view
(#50, parked on this feature) needs, per source and program, memory with whether it is PSS or RSS, CPU %, and whether a folded
snapshot did not list every program, for any period and as faithful step averages, through a read whose cost does not grow with the
number of stored snapshots.

## Scenarios

- S1: Given process snapshots stored by the agent or an import, when the retention job runs its fold (at start-up and every 5 minutes),
  then each snapshot is folded exactly once into the 5-minute and the hourly bucket of its source, per program: memory (RSS, and PSS
  when the snapshot has one), CPU % and the number of processes, as minimum, average, maximum and last value, and the bucket counts
  the snapshots of the source folded into it, those whose memory key is PSS, and those that do not list every program.
- S2: Given a bucket, then a program's average is its sum over the bucket's snapshots of the source, a snapshot that does not name
  the program counting as 0, so the averages of all programs of a bucket add up to the average total of the source.
- S3: Given a snapshot past `retention.snapshots`, when the removal pass reaches it, then it is removed only after it was folded; the
  rollups stay for `retention.rollups_5m` and `retention.rollups_1h` as the metric rollups do.
- S4: Given a late snapshot (an agent backfill, an import) whose bucket exists, whether the bucket's snapshots are still stored or not,
  then it is merged into the bucket, exactly as for the metric rollups.
- S5: Given per-program rollups, when the web side asks for a source, a range, a step (from the time-series query's steps) and one
  measure (memory or CPU) with a number *n* of programs, then it gets the *n* programs with the highest average of that measure over
  the range, in that order, each with one value per step (the average, minimum, maximum), the sum of all other programs per step as
  *other programs*, and per step whether every folded snapshot had a PSS (memory only) and how many did not list every program; a
  step without a folded snapshot is a gap.
- S6: Given the same read, then it also says up to when the buckets are folded and whether the range starts before the oldest bucket
  of the source still stored; a second read lists the sources with per-program buckets in a range, by name, with the time of each
  source's latest bucket.

## Errors and edge cases

- E1: A snapshot whose memory key is RSS (a legacy import, an agent without `CAP_SYS_PTRACE`, one process without PSS): its programs
  count only toward RSS, and a step mixing PSS and RSS snapshots reads as not all PSS, so a reader shows RSS for it.
- E2: A snapshot that does not list every program: the sum of its programs' `count` differs from `total`, or `total` is absent and
  `complete` is false. The agent's regular snapshots list only the top 25 processes and so have `complete` false while their programs
  cover every process read; they count as listing every program when the counts add up to `total`.
- E3: A snapshot with more than 50 programs: the 50 with the highest memory and the 50 with the highest CPU % are folded by name,
  every other program into *other programs* of that snapshot, so the bucket's total stays exact.
- E4: Hostile or very long program names (up to 1,024 bytes, any text) are stored and returned as received, never interpreted; a
  bucket holds at most 500 named programs per source and folds any further name into *other programs*.
- E5: Sums that would overflow (forged values near 2^64 bytes or a huge CPU %) are clamped to the range of a double, as in the
  metric rollups, so a fold step never fails on them.
- E6: A read with an unknown source, an empty range or no bucket: empty result, no error; a range beyond the storable range, an *n*
  below 1 or above 50 or a step that is not one of the query's steps: the store's invalid-query error.
- E7: The database busy or a step failing: the fold step rolls back with the retention job's warning or error line and is retried at
  the next tick; no snapshot is removed while it is not folded.
- E8: A database of an earlier version: its stored snapshots are folded first, in bounded steps, before any snapshot is removed.

## Non-functional requirements

- N1: No new configuration option: the buckets follow `retention.rollups_5m` and `retention.rollups_1h`; imported snapshots are
  folded too and their buckets follow the same periods.
- N2: A fold step reads at most a bounded number of snapshots and bytes, holds at most one step's data in memory, and stays within
  the work of one ingest batch on the reference host, measured with the storage benchmark (the figures are the plan's).
- N3: A read holds in memory at most *n* + 1 lines of at most 2,000 steps, whatever the number of stored buckets or programs, and
  runs on a reader connection under the caller's cancellation; a 1-year range of the reference database returns within the
  10-second deadline of the views (#50 N4, the measurement is the plan's).
- N4: The disk growth of the rollups per day of a reference host is measured and stated in the area document; the free-space floor
  `ingest.min_free` stays the bound of last resort against forged program names.
- N5: The service log adds counts only (snapshots and buckets folded), never a program name.

## Acceptance criteria

- AC1: The fold folds every process snapshot of every origin exactly once into a 5-minute and an hourly bucket per source and
  program with memory (RSS, PSS), CPU % and process count as minimum, sum, maximum and last value, and per source the counts of
  snapshots, PSS snapshots and snapshots not listing every program (S1, E1, E2, E8, N1).
- AC2: A program missing from a snapshot of its source counts as 0, so the averages of a bucket's programs add up to the source's
  average total; programs beyond E3's cut and E4's cap fold into *other programs* without loss of the total (S2, E3, E4).
- AC3: A process snapshot is removed only once folded; the buckets follow the rollup periods, and a late snapshot merges into its
  existing bucket (S3, S4, E7).
- AC4: The per-program read returns the top *n* programs by the chosen measure over the range with their step values, *other
  programs*, the PSS flag and the incomplete count per step, gaps where no snapshot was folded, the folded-until time and the
  starts-before flag (S5, S6, E1, E6).
- AC5: The source list returns the sources with per-program buckets in a range with their latest bucket time (S6).
- AC6: Overflowing sums are clamped, hostile names are stored as received, and the log lines carry counts only (E4, E5, N5).
- AC7: The fold step, the read and the disk growth meet the figures of N2, N3 and N4 on the reference host (N2, N3, N4).

## Out of scope

- Connection snapshots (Q1, pending): a `connection_snapshot` names a `command` per PID, not a program, so it cannot be folded per
  program; the network view (#51) decides what history of connections it needs.
- Per-process history, the swap of processes, and charts or pages: the processes view is #50.
- A median or percentile per program (record 0208, *Consequences*).

## Open questions

- Q1: Does #458 also roll up connection snapshots, although they carry no program? Open — escalated to the Product Manager. The
  issue says #50 needs connection history; #50's draft spec puts connections per program out of scope (#51). The options: (a) process
  snapshots only, connections left to #51; (b) also fold the socket count per `command` of the connection snapshots' `processes`;
  (c) also fold the socket counts per protocol and state. Recommendation: (a).
- Q2: What tells a reader a step is PSS? Answered: from the code (`internal/procstats`), one memory key per run, PSS only when every process
  has one; a step is PSS when all its folded snapshots are (E1).
- Q3: What is an incomplete snapshot for per-program values? Answered: from the code (`internal/procstats/procstats.go`: `complete` is false
  for every regular agent snapshot), the program counts against `total` (E2).
- Q4: How long are the buckets kept? Answered: from record 0208 and [Retention and rollups](../../docs/areas/retention.md), as the metric
  rollups, no new option (N1).

## Area documents

- `docs/areas/program-rollups.md` (new: a reader of #50 needs the bucket rules and the read in one place; `retention.md` links it): what a bucket holds, the fold, the cut of E3 and the
  cap of E4, the read and its caps, the measured disk growth.
- `docs/areas/retention.md`: snapshots are removed only once folded; the fold now covers process snapshots; the log line's counts.
- `docs/areas/storage.md`: the new tables, the schema step and its watermark; `docs/areas/storage-reading.md`: the per-program read
  and the source list; `docs/BENCHMARKS.md`: the fold step's figures.
