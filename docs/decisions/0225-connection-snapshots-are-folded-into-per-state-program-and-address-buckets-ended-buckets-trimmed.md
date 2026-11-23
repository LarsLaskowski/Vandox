# 0225: Connection snapshots are folded into per-state, per-program and per-address buckets, and ended buckets keep their top addresses

- **Status:** Proposed
- **Date:** 2026-11-23
- **Area:** Storage
- **Source:** Issue #486
- **Supersedes:** —

## Context

Retention removes connection snapshots after `retention.snapshots` (30 days by default), and only metric values
([0208](0208-metric-rollups-are-folded-from-new-rows-by-record-id-never-recomputed-from-raw.md)) and process snapshots
([0223](0223-process-snapshots-are-folded-into-per-program-buckets-beside-a-source-total.md)) leave rollups behind. The network view (#51)
needs sockets per state and per program over any period, stacked, and the top remote addresses of a range. States are a fixed set,
program names change rarely, and remote addresses change from minute to minute on a public web server: a regular snapshot holds its top 25,
so an hourly bucket can meet 1,500 addresses. A probe of a year of hourly buckets with 1,500 addresses each (13.1 million rows) took 5.4
to 6.2 seconds for the top 20 alone in SQLite on the development host, about three times that on the reference host, beyond the views'
10-second deadline.

## Options considered

1. **One rows table per tier keyed by source, bucket, kind (state, program, address) and name, beside a total per source and bucket,
   folded by record ID in the metric fold step** (chosen) — one upsert, one tier class and two removal stages per tier; the total holds
   the snapshot count, so a missing state, program or address reads as 0, and the sum of all processes' counts, so *other programs*
   follows as for record 0223.
2. **Three rows tables per tier (states, programs, addresses)** — typed columns per kind (no unused minimum for addresses), but six tables,
   six upserts and six removal stages for the same rules.
3. **Address buckets kept at their first-met cap** — exact only up to the cap; a one-year read scans up to 1,500 rows per hour and misses
   the deadline (the probe above).
4. **A lower hourly cap (500) with a capped-snapshot flag** — about 4.4 million rows a year, still near the deadline on the reference host,
   and an address first met after the cap reads lower in every hour of heavy churn.
5. **A daily address tier** — a year is 365 buckets, but with churn its first-met cap is reached within hours, or it needs a recomputation
   from the hourly rows whenever a late snapshot changes one.
6. **Trim an ended bucket to its top addresses by sum** (chosen with 1) — an address among the top keeps its exact sum and highest count; a
   year is at most 876,000 hourly address rows; a late merge adds rows that the next trim removes again. The trim waits for a fold step
   that read below its bound, so a bucket spanning steps of a backlog is whole before it is cut; trimming per source only the buckets
   before its earliest unfolded snapshot was rejected, as it scans every unfolded snapshot in each step.
7. **A separate fold step or store call** — rejected for the reason of record 0223 (every test double of `IRetentionStore` changes).

## Decision

The metric fold step also folds the connection snapshots above a watermark of their own (`connection_rollup_after_id`) into one rows table
and one totals table per tier, in the same transaction, and, in a step that has folded every stored connection snapshot, trims every
bucket that ended at least 10 minutes earlier to its top 50 (5-minute) or 100 (hourly) addresses; a connection snapshot is removed only at or below the watermark. The rules (cuts, caps, trim, reads)
are in [Connection rollups](../areas/connection-rollups.md).

## Consequences

- An address outside a snapshot's top 25, beyond an open bucket's cap or below an ended bucket's top is 0 there; *further addresses* counts
  only the addresses the buckets kept.
- The fold step does a third part; its bound is measured against an ingest batch (`docs/BENCHMARKS.md`, *Connection rollups*).
- During a catch-up (an upgrade, a pass after downtime) ended buckets stay untrimmed until the backlog is folded.
- Under full address churn a source keeps about 8 million 5-minute and 3.4 million hourly rows at the default periods; forged sources or
  backdated snapshots keep rows for the rollup periods, bounded by `ingest.min_free` as for program rollups.
- If a read of N2 misses 10 seconds on the reference host, an hourly bucket keeps 50 addresses instead of 100.
- Revisit if a view needs a minimum per address, distinct sockets per program or local endpoints: none is stored.
