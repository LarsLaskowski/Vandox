# Spec: Rollups of connection snapshots

Source: Issue #486
Status: Draft

## Problem

Retention removes connection snapshots after `retention.snapshots` and nothing of them remains (issue #486, *Context*). The network
view (#51) has to show sockets per state, per program and the top remote addresses over months.

## Scenarios

- S1: Given an agent that sends a connection snapshot a minute, when the retention job's fold runs, then every snapshot is folded once
  into the 5-minute and the hourly bucket of its source that hold its capture time, and a snapshot past `retention.snapshots` is removed
  only after it was folded; one that follows `retention.events` (the newest mark of its agent, a listener finding) is kept as today.
- S2: Given a folded bucket, then per TCP state (`tcp` and `tcp6` added together) and for `UDP` (`udp` and `udp6`, any state) it holds the
  minimum, average and maximum socket count over the bucket's snapshots, a state a snapshot does not list counting 0, so the averages of
  all states add up to the average of every counted socket.
- S3: Given a snapshot whose `processes` name a program (the `command`) for several processes, then the program counts the sum of their
  `count`s in that snapshot — a socket held by several processes counts once per holder, also within one program — and the bucket holds
  its minimum, average and maximum, a snapshot that does not name it counting 0.
- S4: Given a snapshot's `remotes`, then each address counts its `count` in that snapshot and 0 in a snapshot that does not list it; the
  bucket holds its average over every folded snapshot of the bucket and the highest count in one snapshot. An IPv4-mapped `::ffff:`
  address stays apart from its IPv4 address.
- S5: Given a bucket, then it holds how many of its folded snapshots carried `aggregates_incomplete`.
- S6: Given a source, a range and a step of the time-series query, when the states-and-programs read runs, then it returns per step the
  minimum, average and maximum of every state and of the 10 programs with the highest average over the range, an *other programs* line
  per step and the incomplete counts; a step below one hour reads the 5-minute buckets, every other step the hourly ones.
- S7: Given a source, a range and a step, when the remote-address read runs, then it returns the 20 addresses with the highest average over
  the range, each with its highest count in one snapshot, and the number of further addresses the range's buckets hold.
- S8: Given either read, then it also returns the gaps (steps without a folded snapshot), the earliest capture time of the source's
  connection snapshots not yet folded, whether the range starts before the oldest bucket of the tier read, and the incomplete counts.
- S9: Given a range, when the source list is read, then it returns the sources with a connection bucket of either tier in the range, at
  most 256, with a flag when more exist.
- S10: Given an import of the legacy monitoring log (source `legacy-monitor`), then its connection snapshots are folded like an agent's.

## Errors and edge cases

- E1: A snapshot lists more than 50 programs: only the 50 with the highest count in it (ties by program name, ordinal) are folded by
  name; the others count in the bucket's total and read as *other programs*.
- E2: A bucket meets more than 500 program names: the first 500 in fold order (record ID, then name, ordinal) stay named, later names
  count in the total only.
- E3: A snapshot lists more than 25 remote addresses (a full snapshot lists up to 4,096, an imported one every address): only its 25
  highest by count (ties by address text, ordinal) are folded, as a regular snapshot holds them. A 5-minute bucket keeps at most 500
  addresses and an hourly bucket at most 1,500 (60 regular snapshots of 25 new addresses each), the first met in fold order: record
  ID, then within a snapshot by count descending, ties by address text, ordinal. A bucket counts its **capped snapshots** (folded
  snapshots that lost at least one address to the cap). Remaining bias: an address cut from a snapshot reads lower there, unflagged
  and not in *further addresses*; above one snapshot a minute (pressure, extra runs) the hourly cap can be reached, an address met
  after it reads lower there, and sub-hour and 6-hour reads can differ, flagged by the capped count (*further addresses* a lower bound).
- E4: A hostile `command` (up to 1,024 bytes, control characters, right-to-left override) is stored and returned as received, never
  interpreted; a caller escapes it. An address is stored in the text form of the decoded snapshot.
- E5: Counts near 2^32 in thousands of entries: every sum (fold, merge, read) is clamped to plus or minus `double.MaxValue`, so no fold
  step fails and no read returns Infinity or NaN.
- E6: A snapshot lists one state, program or address twice: the entries are added up within that snapshot.
- E7: A snapshot whose data does not decode (NULL, invalid JSON or UTF-8, refused by the wire validation) is skipped and counted in the
  fold's log line; the watermark passes it and removal takes it with its period.
- E8: A late snapshot (backfill, import) whose bucket exists is merged into it, whether or not the bucket's snapshots are still stored; a
  bucket past its tier's period is not created.
- E9: An empty snapshot, or one without `processes`, is folded with 0 for what it lacks.
- E10: An imported snapshot has `complete` false but no `aggregates_incomplete` when an `lsof` line was left out (unread, unmapped, too
  long), when its section reached the cap of 4,096 `lsof` lines, or when the input ended inside its section (so the last snapshot of
  every imported file); it does not count as incomplete, so an imported low count can be a cut snapshot without a flag. Its `processes` count socket lines per process, as the legacy parser defines them.
- E11: A database of an earlier version folds its stored connection snapshots first, in steps, before any connection snapshot is removed.
- E12: A failing or busy fold step rolls back whole with the metric and program fold; the job logs it and the next tick retries.
- E13: A read with an unknown source, an empty range or no bucket returns the empty result; a step not among the query's steps, a range
  of more than 2,000 steps or a bound outside the storable range is the store's invalid-query error before any read.

## Non-functional requirements

- N1: A fold step (metric, program and connection parts in one transaction) stays within the work of one ingest batch of 10,000 records
  on the reference host; the connection part is bounded by a number of snapshots and a budget of stored data bytes per step.
- N2: A one-year read of states and programs, and of remote addresses, at a 6-hour step returns within the views' 10-second deadline on
  the reference host.
- N3: The disk growth of a day of one snapshot a minute is measured and stated.
- N7: The benchmark of N2 and N3 measures at the address cap: every snapshot's 25 remotes are addresses no earlier snapshot named, so
  each hourly bucket reaches 1,500 addresses; a run with stable peers would show the best case only.
- N4: No new option: the buckets follow `retention.rollups_5m` and `retention.rollups_1h`; every origin is folded.
- N5: The fold and removal log lines carry counts only, never a command, an address or a user.
- N6: A read holds at most its lines (states, 10 programs, *other programs*, 20 addresses) of at most 2,000 steps in memory; a fold step
  holds at most one step's data.

## Acceptance criteria

- AC1: Every connection snapshot of every origin is folded exactly once into the 5-minute and hourly bucket of its source, and no
  connection snapshot is removed above the fold's watermark; the existing `retention.events` exceptions still apply (S1, S10, E7, E8, E11, N4).
- AC2: A bucket holds per state (TCP states with `tcp`/`tcp6` merged, `UDP` for `udp`/`udp6`) the minimum, sum with the snapshot count
  and maximum, a missing state counting 0 (S2, E6).
- AC3: A bucket holds per program the minimum, sum with the snapshot count and maximum of the summed per-process `count`, a missing
  program counting 0, with the cut of 50 per snapshot and the cap of 500 per bucket, and a total from which *other programs* follows
  (S3, E1, E2, E6, E9).
- AC4: A bucket holds per remote address the sum with the snapshot count and the highest count, an unlisted address counting 0, mapped
  addresses kept apart, with the cut of 25 per snapshot, the cap of 500 (5-minute) and 1,500 (hourly) per bucket in the stated order, the
  count of capped snapshots, and the count of incomplete snapshots (S4, S5, E3, E6, E10).
- AC5: The states-and-programs read returns per step the states and the top 10 programs by average over the range with *other programs*,
  each with minimum, average and maximum, plus gaps, the earliest unfolded time, the before-oldest-bucket flag and the incomplete counts
  (S6, S8, E13, N6).
- AC6: The remote-address read returns the top 20 addresses of the range by average with their highest count and the number of further
  addresses, plus the same gaps, completeness and incomplete counts and per step the capped-snapshot count; the source list returns at most 256 sources with a flag (S7, S8, S9, E13).
- AC7: Hostile commands are stored and returned as received, sums are clamped in fold, merge and read, and the fold and removal log lines
  hold counts only (E4, E5, N5).
- AC8: A failing or busy fold step rolls back whole and is retried; buckets past `retention.rollups_5m` and `retention.rollups_1h` are
  removed with their tier (E12, N4).
- AC9: The storage benchmark measures on the reference host a fold step against an ingest batch, the disk growth of a day and a one-year
  read of both reads with new addresses in every snapshot, and the results are stated in `docs/BENCHMARKS.md` (N1, N2, N3, N7).

## Out of scope

- The network view itself, its charts and listener inventory (#51).
- Counting distinct sockets per program; rollups of the `connections` list, local addresses or ports, listeners or users.
- Unmapping IPv4-mapped addresses; a minimum per address; last values, percentiles or a process count per program.
- Counting an imported snapshot with `complete` false as incomplete (E10 states what the rollup does).

## Open questions

- What does "remove a snapshot only once folded, except one that follows `retention.events`" mean? Answered: from the code
  (`RetentionRemover`, `IsRemovable`): the newest mark and a snapshot with a listener finding are kept by their own rule as today; the
  watermark rule is added for every connection snapshot.
- How many remote addresses per bucket? Answered: the Lead decides that each snapshot is cut to its top 25 by count, so a full snapshot (up to 4,096
  addresses, which in ordinal fold order would fill any cap with the lowest addresses) weighs like a regular one; a 5-minute bucket keeps
  500 and an hourly one 1,500, which the regular cadence cannot exceed, with a capped-snapshot count (Challenge 1).
- What is the program cut with one measure? Answered: the Lead takes record 0223's 50 per measure, here the count alone; the cap of 500 per bucket stays.
- Is a state missing from a snapshot 0? Answered: yes, from the issue's requirement that the stack's top edge is every counted socket.
- Do the issue's claims about today hold? Answered: yes ([Agent connections](../../docs/areas/agent-connections.md), *Owners* and
  *Regular and full snapshots*; `internal/connstats` writes no metric line).

## Area documents

- `docs/areas/connection-rollups.md` (new, beside *Program rollups*): the buckets per state, program and address, the zero rule, the cuts
  and caps, the capped-snapshot count, the incomplete count with the three causes of an imported snapshot not counted (E10), the fold step and its skip rule, both reads and the source list with their limits and errors, the
  measured disk and time; linked from the index in `docs/areas/README.md`.
- `docs/areas/retention.md`: the third watermark, removal of connection snapshots only below it, the removal of the connection buckets
  per tier in the walk, the fold step's bound and the new log lines with their counts.
- `docs/areas/storage.md` (the tables and the migration step) and `docs/areas/storage-reading.md` (the reads' place among the others).
- `docs/areas/agent-connections.md`, *Not here*: a pointer to the rollups.

## Challenge

1. MAJOR, the 500-address cap skews the hourly tier. Accepted. The hourly cap is 1,500 (60 x 25), which one regular snapshot a
   minute cannot exceed; the 5-minute cap stays 500 (5 x 25 = 125 at the regular cadence, 500 under memory pressure). Each bucket also
   counts its capped snapshots, and the address read returns that count per step, so a capped bucket is flagged and *further
   addresses* reads as a lower bound there. The remaining bias is stated in E3: since every snapshot is cut to 25, only a cadence above
   one snapshot a minute (pressure, extra runs, a backfill or import at a denser cadence) reaches the hourly cap. Changed: E3, AC4, AC6, Open questions.
2. minor, no order within a snapshot for the address cap. Accepted: record ID, then count descending, ties by address text ordinal
   (E3), so the busiest addresses of a snapshot are met first.
3. minor, E10 names one cause only. Accepted: E10 lists the left-out line, the 4,096-line cap and the end of input inside the section;
   the area document states the same (*Area documents*, `connection-rollups.md`, the incomplete count).
4. minor, the benchmark's address mix. Accepted: N7 has every snapshot name 25 addresses no earlier snapshot named, so the day's growth
   and the one-year read are measured with hourly buckets at the cap; AC9 names it.
