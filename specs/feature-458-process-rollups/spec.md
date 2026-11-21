# Spec: Rollups of process snapshots per program

Source: Issue #458
Status: Draft

## Problem

The administrator wants the memory and CPU of each program over months. Today they exist only inside `process_snapshot`
records ([Agent system metrics](../../docs/areas/agent-system-metrics.md), *Processes*), which retention removes after
`retention.snapshots`, while only metric values leave rollups ([Retention and rollups](../../docs/areas/retention.md)). The
processes view (#50, parked on this feature) needs them for any period through a read whose cost does not grow with the stored
snapshots.

## Scenarios

- S1: Given process snapshots stored by the agent or an import, when the retention job runs its fold (at start-up and every 5 minutes),
  then each snapshot is folded exactly once into the 5-minute and the hourly bucket of its source, per program: RSS, PSS (from PSS
  snapshots only, E1), CPU % and the number of processes, as minimum, sum, maximum and last value, and the bucket counts the snapshots
  of the source folded into it, its PSS snapshots and those that do not list every program.
- S2: Given a bucket, then a program's value in a snapshot that does not name it is 0: its minimum, sum and last value count that 0, so
  the averages of all programs of a bucket add up to the average total of the source. An average weighs each snapshot equally, not by
  the time it stands for (regular, pressure and OOM snapshots alike), as the metric rollups do.
- S3: Given a process snapshot past `retention.snapshots`, when the removal pass reaches it, then it is removed only after it was
  folded; connection snapshots and `mariadb_status` keep their removal unchanged. The per-program buckets are removed after
  `retention.rollups_5m` and `retention.rollups_1h`, beside the metric rollups in the walk's order, and counted in its log line.
- S4: Given a late snapshot (an agent backfill, an import) whose bucket exists, whether the bucket's snapshots are still stored or not,
  then it is merged into the bucket, exactly as for the metric rollups.
- S5: Given per-program rollups, when the web side asks for a source, a range, a step (from the time-series query's steps) and one
  measure (memory or CPU) with a number *n* of programs, then it gets the *n* programs with the highest average of that measure over
  the range, in that order, each with one value per step (the average, minimum, maximum), the sum of all other programs per step as
  *other programs*, and per step how many folded snapshots did not list every program; a step without a folded snapshot is a gap.
  Memory is PSS when every folded snapshot of the range is a PSS snapshot, else RSS for the whole range; the read ranks and returns
  in that key and names it.
- S6: Given the same read, then it also says up to when the buckets are folded and whether the range starts before the oldest stored
  bucket of the resolution the step reads (5-minute for steps below one hour, hourly otherwise); a second read lists the sources with
  per-program buckets in a range, by name, with the time of each source's latest bucket within that range.

## Errors and edge cases

- E1: A snapshot is a PSS snapshot when it lists at least one program and every listed program carries `pss_bytes` (the agent drops a
  program's PSS when one of its processes has none: `internal/procstats/snapshot.go`). A program's PSS average is its PSS sum over the
  bucket's PSS snapshots; an RSS snapshot (a legacy import, an agent without `CAP_SYS_PTRACE`) counts toward RSS only, and a range
  holding one reads as RSS (S5).
- E2: A snapshot that does not list every program: the sum of its programs' `count` differs from `total`, `total` is absent and
  `complete` is false, or it lists no program (the agent's size fallback can drop them all). The agent's regular snapshots list only
  the top 25 processes and so have `complete` false while their programs cover every process read; they count as listing every program
  when the counts add up to `total`.
- E3: A snapshot with more than 50 programs: the 50 with the highest memory in the snapshot's own key (PSS for a PSS snapshot, else
  RSS) and the 50 with the highest CPU % are folded by name, ties by program name in ordinal order, every other program into *other
  programs* of that snapshot, so the bucket's total stays exact; a program outside the cut reads lower in that bucket, without a flag.
- E4: Hostile or very long program names (up to 1,024 bytes, any text) are stored and returned as received, never interpreted; a
  bucket holds at most 500 named programs per source: the first 500 names it meets in fold order stay named, any further name folds into
  *other programs*.
- E5: Sums that would overflow (forged values near 2^64 bytes or a huge CPU %) are clamped to the range of a double, as in the
  metric rollups, so a fold step never fails on them.
- E6: A read with an unknown source, an empty range or no bucket: empty result, no error; a range beyond the storable range, an *n*
  below 1 or above 50, a step that is not one of the query's steps or a range of more than 2,000 steps: the store's invalid-query
  error. A step below one hour over a range that starts before the 5-minute period returns what the 5-minute buckets still hold, gaps
  before, with the starts-before flag set.
- E7: The database busy or a step failing: the fold step rolls back with the retention job's warning or error line and is retried at
  the next tick; no snapshot is removed while it is not folded.
- E8: A database of an earlier version: its stored snapshots are folded first, in bounded steps, before any snapshot is removed.
- E9: A stored snapshot whose data does not decode is skipped by the fold, counted in its log line, passed by the fold's position and
  removed with its period, so it never stops folding or removal.

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
- N5: The service log adds counts only (snapshots folded and skipped, buckets folded and removed), never a program name.

## Acceptance criteria

- AC1: The fold folds every decodable process snapshot of every origin exactly once into the buckets and values of S1; an
  undecodable snapshot is skipped and counted (S1, E1, E2, E8, E9, N1).
- AC2: A program missing from a snapshot of its source counts as 0 in its minimum, sum and last value, so the averages of a bucket's
  programs add up to the source's average total; programs beyond E3's cut and E4's cap fold into *other programs* without loss of the
  total (S2, E3, E4).
- AC3: A process snapshot is removed only once folded, an undecodable one too; connection snapshots keep their removal; the buckets
  are removed with the rollup periods and counted, and a late snapshot merges into its existing bucket (S3, S4, E7, E9).
- AC4: The per-program read returns the top *n* programs by the chosen measure with their step values in the memory key of S5 and
  that key, *other programs*, the incomplete count per step, gaps, the folded-until time, the starts-before flag and E6's results
  (S5, S6, E1, E6).
- AC5: The source list returns the sources with per-program buckets in a range with their latest bucket time within the range (S6).
- AC6: Overflowing sums are clamped, hostile names are stored as received, and the log lines carry counts only (E4, E5, N5).
- AC7: The fold step, the read and the disk growth meet the figures of N2, N3 and N4 on the reference host (N2, N3, N4).

## Out of scope

- Rollups of connection snapshots (Q1, the Product Manager's answer): a `connection_snapshot` names a `command` per PID, not a
  program; the network view (#51) decides what history of connections it needs.
- Per-process history, the swap of processes, and charts or pages: the processes view is #50.
- A median or percentile per program (record 0208, *Consequences*).
- Averages weighted by the time a snapshot stands for (S2), and a flag for a program cut by E3 or E4 in a bucket.

## Open questions

- Q1: Does #458 also roll up connection snapshots, although they carry no program? Answered: by the Product Manager, (a) process
  snapshots only; connection history is left to #51, which decides what it needs (*Out of scope*).
- Q2: When is memory PSS? Answered: from `internal/procstats/snapshot.go` (E1, S5).
- Q3: What is an incomplete snapshot? Answered: from `internal/procstats/procstats.go`, counts against `total` (E2).
- Q4: How long are buckets kept? Answered: as the metric rollups, record 0208 (N1).

## Area documents

- `docs/areas/program-rollups.md` (new, linked from `retention.md`): the buckets, the fold, E3's cut and E4's cap with what they
  cost a reader, the read, the measured disk growth.
- `docs/areas/retention.md`: process snapshots are removed only once folded; the fold now covers process snapshots; the per-program
  buckets in the removal walk's order; the log line's counts.
- `docs/areas/storage.md`: the new tables, the schema step and its watermark; `docs/areas/storage-reading.md`: the per-program read
  and the source list; `docs/BENCHMARKS.md`: the fold step's figures.

## Challenge

- M1 (memory key of fold and read) — accepted: E1 defines a PSS snapshot (every listed program carries `pss_bytes`, confirmed at
  `internal/procstats/snapshot.go:71-79`) and the PSS average's denominator (the bucket's PSS snapshots); S5 and AC4 make the read rank
  and return in PSS only when every folded snapshot of the range is a PSS snapshot, else RSS, and name the key. The per-step PSS flag is
  dropped, since the key is one per range as #50 S1 shows it. The test on a range mixing PSS and RSS is the plan's, under AC4.
- M2 (an undecodable snapshot stops fold and removal) — accepted: E9 skips it, counts it in the log line, moves the fold's position past
  it and lets removal take it; AC1 and AC3 name it, and N5 the count. The test storing such a row is the plan's.
- Minor 1 (step cap) — accepted: E6 makes a range of more than 2,000 steps the invalid-query error (a year at a 5-minute step is one),
  and states what a step below one hour returns for a range older than the 5-minute period (confirmed at
  `docs/areas/time-series-query.md:27-28`); S6 ties the starts-before flag to the resolution the step reads.
- Minor 2 (E3 key, ties, E4 order) — accepted: E3 cuts by the snapshot's own memory key and CPU %, ties by ordinal program name, and
  states that a cut program reads lower without a flag (listed under *Out of scope*, stated in the area document); E4 keeps the first
  500 names in fold order, the order itself fixed in the plan.
- Minor 3 (snapshot without programs) — accepted: confirmed that `ProcessSnapshot.Validate` accepts processes without programs and that
  the size fallback halves `programs` (`internal/procstats/procstats.go:236-241`); E2 counts it as not listing every program, and S2
  makes it add zeros. Its test is the plan's.
- Minor 4 (removal) — accepted: S3 says process snapshot, leaves connection snapshots and `mariadb_status` unchanged, and puts the
  per-program buckets into the walk's order and the log line; AC3 and *Area documents* (`retention.md`) carry it.
- Minor 5 (minimum, last value, weighting) — accepted: S2 counts a missing program's 0 in its minimum and last value as in its sum, and
  states that each snapshot weighs equally, as in the metric rollups; time weighting is out of scope.
- Minor 6 (source list time) — accepted: S6 and AC5 return each source's latest bucket within the range, so #50 S6 can pick the default
  source.
