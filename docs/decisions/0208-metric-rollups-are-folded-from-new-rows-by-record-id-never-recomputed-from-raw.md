# 0208: Metric rollups are folded from new rows by record ID, never recomputed from raw values

- **Status:** Proposed
- **Date:** 2026-11-13
- **Area:** Storage
- **Source:** Issue #46
- **Supersedes:** —

## Context

Issue #46 asks for 5-minute and hourly values (minimum, average, maximum, last) per metric series, kept far longer than the raw values
(365 and 1,095 days against 30), and for backfilled data to correct buckets that were already computed. Backfill reaches buckets
whose raw values are still stored and, after a long outage or an import, buckets whose raw values are gone. The write path carries
the write-throughput criterion (median of five 10,000-record batches under one second on the reference host, record 0083), and
`records.id` is a plain `INTEGER PRIMARY KEY`, so SQLite gives the highest deleted ID again to the next insert.

## Options considered

1. **Update the rollups in the batch's transaction** — always current, but every metric costs two upserts on the hot path, against
   the criterion of record 0083, and an import pays it too.
2. **Mark dirty buckets in the write path, recompute them from the raw values** — exact while the raw values exist, wrong once they
   are removed (a recompute from the rest would drop the removed values), and a lookup per metric on the hot path.
3. **Fold the rows added since a watermark (record ID) into stored sums** (chosen) — minimum, maximum, sum, count and the last value
   with its time are all mergeable, so a late value is folded in the same way whether the raw values still exist or not; the write
   path is untouched.

## Decision

A background step folds the metric rows whose record ID lies above a stored watermark into the 5-minute and hourly buckets of their
series (source, name, labels) and advances the watermark in the same transaction; the average is the sum over the count. Raw values
are removed only at or below the watermark, and the record with the highest ID is never removed, so IDs stay increasing. The rules are
in [Retention and rollups](../areas/retention.md).

## Consequences

- A value is folded exactly once: a resend after its stored copy was removed must not be stored again (record 0211).
- The average is a floating-point sum in arrival order, so it equals a recomputation within rounding, not bit for bit.
- Buckets are current only up to the last step (at most 5 minutes behind); a reader of #47 sees the newest bucket grow.
- Revisit if a rollup needs a statistic that does not merge (a median or a percentile): that would need the raw values or a sketch.
